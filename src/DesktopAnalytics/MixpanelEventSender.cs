using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using Segment.Serialization;

namespace DesktopAnalytics
{
	/// <summary>
	/// The real (network-hitting) <see cref="IEventSender"/>: posts one event at a time to Mixpanel's
	/// HTTP "track" endpoint (https://developer.mixpanel.com/reference/track-event) and classifies the
	/// response into a <see cref="SendResult"/> for the flush loop.
	/// </summary>
	/// <remarks>
	/// Deliberately does not go through the <c>mixpanel-csharp</c> package for this path: that package's
	/// <c>TrackAsync</c> reports success/failure as a single <c>bool</c>, which is not enough information
	/// to distinguish "retry me" from "poison, drop me" (see offline-analytics.md, "Failure
	/// classification"). Posting the well-documented raw HTTP form directly keeps that distinction and
	/// makes the base URL trivially swappable for tests (WireMock.Net). <c>mixpanel-csharp</c> is still
	/// used, elsewhere, for the best-effort (non-spooled) Identify call.
	/// </remarks>
	internal class MixpanelEventSender : IEventSender, IDisposable
	{
		internal const string kDefaultBaseUrl = "https://api.mixpanel.com";

		private readonly string _apiSecret;
		private readonly string _baseUrl;
		private readonly HttpClient _httpClient;
		private readonly bool _ownsHttpClient;

		/// <param name="apiSecret">The Mixpanel project token.</param>
		/// <param name="baseUrl">Base URL of the Mixpanel HTTP API. Defaults to the real Mixpanel
		/// endpoint; tests override this with a WireMock.Net base URL.</param>
		/// <param name="httpClient">Injectable so tests can supply an <see cref="HttpClient"/> already
		/// configured (e.g. short timeout) or point it at a local stub. If omitted, this instance owns
		/// and disposes its own <see cref="HttpClient"/>.</param>
		public MixpanelEventSender(string apiSecret, string baseUrl = kDefaultBaseUrl, HttpClient httpClient = null)
		{
			_apiSecret = apiSecret;
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
				// losing the event even though Mixpanel never received it. With redirects disabled we
				// see the raw 3xx instead and can classify it correctly (see the status-code check
				// below).
				_httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
					{ Timeout = TimeSpan.FromSeconds(15) };
				_ownsHttpClient = true;
			}
		}

		/// <summary>
		/// Posts <paramref name="evt"/> to Mixpanel's /track endpoint and classifies the result. Never
		/// throws: any serialization, network, or cancellation failure is caught and reported as a
		/// <see cref="SendResult"/> (see class remarks for the classification rules).
		/// </summary>
		/// <param name="cancellationToken">See <see cref="IEventSender.Send"/>. A bounded caller
		/// (<see cref="MixpanelClient"/>'s BoundedDrain) uses this to abandon a request against a
		/// server that accepts the connection but never responds; that is reported as
		/// <see cref="SendResult.RetryableFailure"/>, exactly like any other transient failure.</param>
		public SendResult Send(AnalyticsEvent evt, CancellationToken cancellationToken = default)
		{
			try
			{
				if (evt == null)
					return SendResult.PoisonDrop;

				string json;
				try
				{
					json = BuildPayloadJson(evt);
				}
				catch (Exception e)
				{
					// A payload we cannot even serialize will never succeed -- drop it rather than
					// retry forever.
					Debug.WriteLine("MixpanelEventSender.Send: failed to build payload, dropping: " + e);
					return SendResult.PoisonDrop;
				}

				var formBody = "data=" + Uri.EscapeDataString(json);

				HttpResponseMessage response;
				try
				{
					using (var content = new StringContent(formBody, Encoding.UTF8,
						       "application/x-www-form-urlencoded"))
					{
						response = _httpClient
							.PostAsync(_baseUrl + "/track", content, cancellationToken)
							.ConfigureAwait(false)
							.GetAwaiter()
							.GetResult();
					}
				}
				catch (OperationCanceledException e)
				{
					// The caller's deadline (see MixpanelClient.BoundedDrain) fired before the server
					// responded -- most commonly a "black hole" server that accepts the TCP connection
					// but never replies (captive portals and some firewalls do this). Treat exactly
					// like any other transient failure: leave the event in the spool for a later
					// retry rather than throwing into (and hanging) the caller.
					Debug.WriteLine("MixpanelEventSender.Send: canceled (deadline), will retry: " + e);
					return SendResult.RetryableFailure;
				}
				catch (Exception e)
				{
					// Connection refused, DNS failure, timeout, etc. -- transient from our point of
					// view, so leave the event in the spool for a later retry.
					Debug.WriteLine("MixpanelEventSender.Send: network failure, will retry: " + e);
					return SendResult.RetryableFailure;
				}

				using (response)
				{
					var status = (int)response.StatusCode;
					if (status >= 200 && status < 300)
					{
						// Mixpanel's /track returns HTTP 200 with a plain-text body of "0" when it
						// rejects the payload (e.g. malformed data) -- as opposed to "1" for a genuine
						// accept. A "0" will never succeed on retry, so treat it as poison rather than
						// Delivered so it cannot silently vanish while never actually landing.
						try
						{
							var body = response.Content.ReadAsStringAsync()
								.ConfigureAwait(false)
								.GetAwaiter()
								.GetResult();
							if (body != null && body.Trim() == "0")
								return SendResult.PoisonDrop;
						}
						catch (Exception e)
						{
							// A body-read failure on an otherwise-successful response shouldn't turn a
							// delivered event into a retry loop -- fall through and count it Delivered.
							// At-least-once semantics + $insert_id dedup make an occasional
							// false-positive here harmless.
							Debug.WriteLine(
								"MixpanelEventSender.Send: failed to read 2xx response body, assuming delivered: " +
								e);
						}

						return SendResult.Delivered;
					}

					if (status >= 300 && status < 400)
					{
						// With AllowAutoRedirect disabled (see the constructor), a 3xx here means
						// something intercepted the POST before it reached Mixpanel -- most commonly a
						// captive portal redirecting to its login page. Mixpanel's /track endpoint
						// never legitimately redirects, so treat this as an intercepting middlebox:
						// leave the event spooled for retry instead of risking a misclassification.
						return SendResult.RetryableFailure;
					}

					if (status == 408 || status == 429 || status >= 500)
						return SendResult.RetryableFailure;

					// Any other 4xx (or any other unexpected status) will never succeed by retrying --
					// treat it as a poison message so it cannot wedge the spool.
					return SendResult.PoisonDrop;
				}
			}
			catch (OperationCanceledException e)
			{
				Debug.WriteLine("MixpanelEventSender.Send: canceled (deadline), will retry: " + e);
				return SendResult.RetryableFailure;
			}
			catch (Exception e)
			{
				// Belt-and-suspenders: analytics must never throw into the host. Treat anything
				// unexpected as retryable rather than silently dropping the event.
				Debug.WriteLine("MixpanelEventSender.Send failed unexpectedly, will retry: " + e);
				return SendResult.RetryableFailure;
			}
		}

		private string BuildPayloadJson(AnalyticsEvent evt)
		{
			var properties = new JsonObject
			{
				{ "token", _apiSecret },
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
