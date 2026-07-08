using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
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
				_httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
				_ownsHttpClient = true;
			}
		}

		/// <summary>
		/// Posts <paramref name="evt"/> to Mixpanel's /track endpoint and classifies the result. Never
		/// throws: any serialization or network failure is caught and reported as a
		/// <see cref="SendResult"/> (see class remarks for the classification rules).
		/// </summary>
		public SendResult Send(AnalyticsEvent evt)
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
							.PostAsync(_baseUrl + "/track", content)
							.ConfigureAwait(false)
							.GetAwaiter()
							.GetResult();
					}
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
						return SendResult.Delivered;

					if (status == 408 || status == 429 || status >= 500)
						return SendResult.RetryableFailure;

					// Any other 4xx (or any other unexpected status) will never succeed by retrying --
					// treat it as a poison message so it cannot wedge the spool.
					return SendResult.PoisonDrop;
				}
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
