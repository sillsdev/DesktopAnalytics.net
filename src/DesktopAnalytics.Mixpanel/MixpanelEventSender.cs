using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Segment.Serialization;

namespace DesktopAnalytics
{
	/// <summary>
	/// The real (network-hitting) <see cref="IEventSender"/>: posts a batch of events in a single
	/// request to Mixpanel's HTTP "import" endpoint (https://docs.mixpanel.com/reference/import-events)
	/// and classifies the response into a <see cref="BatchSendResult"/> for the flush loop.
	/// </summary>
	/// <remarks>
	/// /import rather than /track (PR #43 review): it accepts a plain project token for auth (basic
	/// auth username with an empty password), takes up to 2,000 events per request -- one round trip
	/// per batch matters to users on poor connections -- has no /track-style 5-day age limit on
	/// replayed events (an arbitrarily old offline backlog is accepted), and with <c>strict=1</c>
	/// reports per-record rejections (<c>failed_records</c>) so a poison event can be dropped without
	/// discarding the good events sent alongside it.
	/// Deliberately does not go through the <c>mixpanel-csharp</c> package for this path: that package's
	/// send reports success/failure as a single <c>bool</c>, which is not enough information
	/// to distinguish "retry me" from "poison, drop me" (see offline-analytics.md, "Failure
	/// classification"). Posting the well-documented raw HTTP form directly keeps that distinction and
	/// makes the base URL trivially swappable for tests (WireMock.Net). <c>mixpanel-csharp</c> is still
	/// used, elsewhere, for the best-effort (non-spooled) Identify call.
	/// </remarks>
	internal class MixpanelEventSender : IEventSender, IDisposable
	{
		internal const string kDefaultBaseUrl = "https://api.mixpanel.com";

		private readonly string _baseUrl;
		private readonly HttpClient _httpClient;
		private readonly bool _ownsHttpClient;
		// /import authenticates with the project token as the basic-auth username (empty password).
		private readonly AuthenticationHeaderValue _authorization;

		/// <param name="apiSecret">The Mixpanel project token.</param>
		/// <param name="baseUrl">Base URL of the Mixpanel HTTP API. Defaults to the real Mixpanel
		/// endpoint; tests override this with a WireMock.Net base URL.</param>
		/// <param name="httpClient">Injectable so tests can supply an <see cref="HttpClient"/> already
		/// configured (e.g. short timeout) or point it at a local stub. If omitted, this instance owns
		/// and disposes its own <see cref="HttpClient"/>.</param>
		public MixpanelEventSender(string apiSecret, string baseUrl = kDefaultBaseUrl, HttpClient httpClient = null)
		{
			_authorization = new AuthenticationHeaderValue("Basic",
				Convert.ToBase64String(Encoding.ASCII.GetBytes((apiSecret ?? string.Empty) + ":")));
			_baseUrl = string.IsNullOrEmpty(baseUrl)
				? kDefaultBaseUrl
				: baseUrl.TrimEnd('/');

			if (httpClient != null)
			{
				_httpClient = httpClient;
				_ownsHttpClient = false;
			}
			else
			{
				// AllowAutoRedirect is disabled deliberately: with the default handler (which follows
				// redirects), a captive portal (hotel/coffee-shop Wi-Fi login page) that intercepts
				// this POST would 302 us to its login page, HttpClient would follow it, get back a
				// harmless 200, and the code below would classify that as Delivered -- silently
				// losing the batch even though Mixpanel never received it. With redirects disabled we
				// see the raw 3xx instead and can classify it correctly (see the status-code check
				// below).
				_httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
					{ Timeout = TimeSpan.FromSeconds(15) };
				_ownsHttpClient = true;
			}
		}

		/// <summary>
		/// Posts <paramref name="events"/> to Mixpanel's /import endpoint (strict mode) in a single
		/// request and classifies the result. Never throws (and never returns a faulted task): any
		/// serialization, network, or cancellation failure is caught and reported as a
		/// <see cref="BatchSendResult"/> (see class remarks for the classification rules).
		/// </summary>
		/// <param name="cancellationToken">See <see cref="IEventSender.SendBatchAsync"/>. A bounded
		/// caller (<see cref="MixpanelClient"/>'s BoundedDrain) uses this to abandon a request against
		/// a server that accepts the connection but never responds; that is reported as
		/// <see cref="BatchSendResult.Retryable"/>, exactly like any other transient failure.</param>
		public async Task<BatchSendResult> SendBatchAsync(IReadOnlyList<AnalyticsEvent> events,
			CancellationToken cancellationToken = default)
		{
			try
			{
				if (events == null || events.Count == 0)
					return BatchSendResult.Delivered;

				// Serialize each event individually so one unserializable event (which will never
				// succeed) is reported as poison via FailedIndices instead of retrying -- or
				// poisoning -- the whole batch. payloadToBatchIndex maps positions in the JSON array
				// actually sent back to positions in the incoming batch.
				var payloads = new List<string>(events.Count);
				var payloadToBatchIndex = new List<int>(events.Count);
				var unserializable = new List<int>();
				for (var i = 0; i < events.Count; i++)
				{
					try
					{
						payloads.Add(BuildEventJson(events[i]));
						payloadToBatchIndex.Add(i);
					}
					catch (Exception e)
					{
						Debug.WriteLine("MixpanelEventSender.SendBatch: failed to build payload, dropping: " + e);
						unserializable.Add(i);
					}
				}

				if (payloads.Count == 0)
					return new BatchSendResult(SendResult.Delivered, unserializable);

				var body = "[" + string.Join(",", payloads) + "]";

				HttpResponseMessage response;
				try
				{
					using (var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/import?strict=1"))
					{
						request.Headers.Authorization = _authorization;
						request.Content = new StringContent(body, Encoding.UTF8, "application/json");
						response = await _httpClient.SendAsync(request, cancellationToken)
							.ConfigureAwait(false);
					}
				}
				catch (OperationCanceledException e)
				{
					// The caller's deadline (see MixpanelClient.BoundedDrain) fired before the server
					// responded -- most commonly a "black hole" server that accepts the TCP connection
					// but never replies (captive portals and some firewalls do this). Treat exactly
					// like any other transient failure: leave the batch in the spool for a later
					// retry rather than throwing into (and hanging) the caller.
					Debug.WriteLine("MixpanelEventSender.SendBatch: canceled (deadline), will retry: " + e);
					return BatchSendResult.Retryable;
				}
				catch (Exception e)
				{
					// Connection refused, DNS failure, timeout, etc. -- transient from our point of
					// view, so leave the batch in the spool for a later retry.
					Debug.WriteLine("MixpanelEventSender.SendBatch: network failure, will retry: " + e);
					return BatchSendResult.Retryable;
				}

				using (response)
				{
					var status = (int)response.StatusCode;
					if (status >= 200 && status < 300)
					{
						// Strict-mode /import: 2xx means every record was validated and ingested.
						return new BatchSendResult(SendResult.Delivered, unserializable);
					}

					if (status >= 300 && status < 400)
					{
						// With AllowAutoRedirect disabled (see the constructor), a 3xx here means
						// something intercepted the POST before it reached Mixpanel -- most commonly a
						// captive portal redirecting to its login page. Mixpanel's /import endpoint
						// never legitimately redirects, so treat this as an intercepting middlebox:
						// leave the batch spooled for retry instead of risking a misclassification.
						return BatchSendResult.Retryable;
					}

					if (status == 408 || status == 429 || status >= 500)
						// Unlike the network/timeout/redirect cases above, we DID reach Mixpanel and
						// it gave a definite "try again later" -- counts against the retry-attempt
						// ceiling (see SendResult.RetryableRejection), unlike the connectivity-style
						// failures elsewhere in this method.
						return BatchSendResult.RetryableRejection;

					if (status == 400)
					{
						// Strict-mode /import returns 400 with a failed_records array when SOME records
						// fail validation -- the rest were ingested. Those records are permanently
						// invalid (poison); report their indices so only they are counted as failed.
						var failed = await TryParseFailedRecordsAsync(response, payloadToBatchIndex)
							.ConfigureAwait(false);
						if (failed != null)
						{
							foreach (var i in unserializable)
								failed.Add(i);
							return new BatchSendResult(SendResult.Delivered, failed);
						}

						// A 400 without a parseable failed_records body means the request as a whole
						// was rejected (e.g. malformed JSON) -- it will never succeed by retrying.
						return BatchSendResult.Poison;
					}

					// Any other 4xx (or any other unexpected status) will never succeed by retrying --
					// treat the batch as poison so it cannot wedge the spool.
					return BatchSendResult.Poison;
				}
			}
			catch (OperationCanceledException e)
			{
				Debug.WriteLine("MixpanelEventSender.SendBatch: canceled (deadline), will retry: " + e);
				return BatchSendResult.Retryable;
			}
			catch (Exception e)
			{
				// Belt-and-suspenders: analytics must never throw into the host. Treat anything
				// unexpected as retryable rather than silently dropping the batch.
				Debug.WriteLine("MixpanelEventSender.SendBatch failed unexpectedly, will retry: " + e);
				return BatchSendResult.Retryable;
			}
		}

		// Returns the batch indices of the records reported in the response's failed_records array,
		// or null if the body has no parseable failed_records (in which case the caller treats the
		// whole batch as poison). The response's "index" values refer to positions in the JSON array
		// we sent, which payloadToBatchIndex maps back to positions in the incoming batch (they
		// differ when an unserializable event was excluded from the payload).
		private static async Task<HashSet<int>> TryParseFailedRecordsAsync(HttpResponseMessage response,
			List<int> payloadToBatchIndex)
		{
			try
			{
				var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				if (!(JToken.Parse(body) is JObject parsed) ||
				    !(parsed["failed_records"] is JArray failedRecords))
					return null;

				var failed = new HashSet<int>();
				foreach (var record in failedRecords)
				{
					var index = record?["index"]?.Value<int?>();
					if (index == null || index < 0 || index >= payloadToBatchIndex.Count)
						return null; // An index we can't map -- treat the body as unparseable.
					failed.Add(payloadToBatchIndex[index.Value]);
				}
				return failed;
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelEventSender.SendBatch: failed to parse 400 body: " + e);
				return null;
			}
		}

		private static string BuildEventJson(AnalyticsEvent evt)
		{
			// No token property: /import authenticates via the Authorization header instead.
			var properties = new JsonObject
			{
				{ "distinct_id", evt.AnalyticsId },
				{ "$insert_id", evt.InsertId },
				{ "time", evt.Time.ToUnixTimeSeconds() }
			};

			if (evt.Properties != null)
			{
				foreach (var kv in evt.Properties)
					properties[kv.Key] = kv.Value;
			}

			var payload = new JsonObject
			{
				{ "event", evt.EventName },
				{ "properties", properties }
			};

			return JsonUtility.ToJson(payload, false);
		}

		public void Dispose()
		{
			if (_ownsHttpClient)
				_httpClient?.Dispose();
		}
	}
}
