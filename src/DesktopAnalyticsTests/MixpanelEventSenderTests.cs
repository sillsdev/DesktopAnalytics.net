using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using DesktopAnalytics;
using NUnit.Framework;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DesktopAnalyticsTests
{
	// Layer 4 of the fidelity ladder in offline-analytics.md: a local HTTP stub (WireMock.Net)
	// standing in for Mixpanel's real endpoint, exercising MixpanelEventSender's real HTTP
	// posting/serialization and its response->SendResult classification.
	[TestFixture]
	public class MixpanelEventSenderTests
	{
		private WireMockServer _server;

		[SetUp]
		public void SetUp()
		{
			_server = WireMockServer.Start();
		}

		[TearDown]
		public void TearDown()
		{
			try
			{
				_server?.Stop();
				_server?.Dispose();
			}
			catch (Exception e)
			{
				Console.WriteLine("MixpanelEventSenderTests.TearDown: " + e);
			}
		}

		private static AnalyticsEvent MakeEvent(string name = "Save")
		{
			return AnalyticsEvent.Create("user-1", name, null, "insert-id-1",
				DateTimeOffset.UtcNow);
		}

		[Test]
		public void Send_200Response_ReturnsDelivered()
		{
			_server.Given(Request.Create().WithPath("/track").UsingPost())
				.RespondWith(Response.Create().WithStatusCode(200));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			Assert.AreEqual(SendResult.Delivered, sender.Send(MakeEvent()));
		}

		[Test]
		public void Send_500Response_ReturnsRetryableFailure()
		{
			_server.Given(Request.Create().WithPath("/track").UsingPost())
				.RespondWith(Response.Create().WithStatusCode(500));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			Assert.AreEqual(SendResult.RetryableFailure, sender.Send(MakeEvent()));
		}

		[Test]
		public void Send_429Response_ReturnsRetryableFailure()
		{
			_server.Given(Request.Create().WithPath("/track").UsingPost())
				.RespondWith(Response.Create().WithStatusCode(429));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			Assert.AreEqual(SendResult.RetryableFailure, sender.Send(MakeEvent()));
		}

		[Test]
		public void Send_408Response_ReturnsRetryableFailure()
		{
			_server.Given(Request.Create().WithPath("/track").UsingPost())
				.RespondWith(Response.Create().WithStatusCode(408));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			Assert.AreEqual(SendResult.RetryableFailure, sender.Send(MakeEvent()));
		}

		[Test]
		public void Send_400Response_ReturnsPoisonDrop()
		{
			_server.Given(Request.Create().WithPath("/track").UsingPost())
				.RespondWith(Response.Create().WithStatusCode(400));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			Assert.AreEqual(SendResult.PoisonDrop, sender.Send(MakeEvent()));
		}

		[Test]
		public void Send_401Response_ReturnsPoisonDrop()
		{
			_server.Given(Request.Create().WithPath("/track").UsingPost())
				.RespondWith(Response.Create().WithStatusCode(401));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			Assert.AreEqual(SendResult.PoisonDrop, sender.Send(MakeEvent()));
		}

		[Test]
		public void Send_ConnectionRefused_ReturnsRetryableFailure()
		{
			// Stop (and dispose) the server so the base URL it handed out is now unreachable --
			// simulates being offline without waiting for a real timeout (connection-refused is
			// near-instant on loopback).
			var deadUrl = _server.Urls[0];
			_server.Stop();
			_server.Dispose();
			_server = null;

			var sender = new MixpanelEventSender("secret", deadUrl,
				new HttpClient { Timeout = TimeSpan.FromSeconds(5) });

			Assert.AreEqual(SendResult.RetryableFailure, sender.Send(MakeEvent()));
		}

		[Test]
		public void Send_UnreachableHost_ReturnsRetryableFailure()
		{
			// A bad port on a live host is another common "offline" shape distinct from a
			// stopped-server connection refusal.
			var sender = new MixpanelEventSender("secret", "http://127.0.0.1:1",
				new HttpClient { Timeout = TimeSpan.FromSeconds(5) });

			Assert.AreEqual(SendResult.RetryableFailure, sender.Send(MakeEvent()));
		}

		// ---- CAPTIVE PORTAL (redirect handling) --------------------------------------------------

		[Test]
		public void Send_302Response_ReturnsRetryableFailureAndDoesNotFollowRedirect()
		{
			// Simulates a captive portal intercepting the POST and redirecting to its login page.
			_server.Given(Request.Create().WithPath("/track").UsingPost())
				.RespondWith(Response.Create()
					.WithStatusCode(302)
					.WithHeader("Location", "/portal-login"));
			_server.Given(Request.Create().WithPath("/portal-login"))
				.RespondWith(Response.Create().WithStatusCode(200));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			Assert.AreEqual(SendResult.RetryableFailure, sender.Send(MakeEvent()));

			// The key assertion: AllowAutoRedirect must be disabled, so the portal's login page is
			// never actually requested. If it were followed, this sender would see the portal's 200
			// and wrongly classify the event as Delivered even though Mixpanel never received it.
			Assert.IsFalse(_server.LogEntries.Any(e => e.RequestMessage.Path == "/portal-login"),
				"the redirect target must never have been requested (AllowAutoRedirect must be false)");
		}

		// ---- MIXPANEL'S "0" REJECTION SENTINEL ----------------------------------------------------

		[Test]
		public void Send_200ResponseWithBodyZero_ReturnsPoisonDrop()
		{
			// Mixpanel's /track returns HTTP 200 with a body of "0" for a payload it rejected.
			_server.Given(Request.Create().WithPath("/track").UsingPost())
				.RespondWith(Response.Create().WithStatusCode(200).WithBody("0"));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			Assert.AreEqual(SendResult.PoisonDrop, sender.Send(MakeEvent()));
		}

		[Test]
		public void Send_200ResponseWithBodyOne_ReturnsDelivered()
		{
			_server.Given(Request.Create().WithPath("/track").UsingPost())
				.RespondWith(Response.Create().WithStatusCode(200).WithBody("1"));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			Assert.AreEqual(SendResult.Delivered, sender.Send(MakeEvent()));
		}

		// ---- CANCELLATION (bounded drain deadline) ------------------------------------------------

		[Test]
		public void Send_PreCancelledToken_ReturnsRetryableFailureAndDoesNotThrow()
		{
			_server.Given(Request.Create().WithPath("/track").UsingPost())
				.RespondWith(Response.Create().WithStatusCode(200));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			SendResult result = SendResult.Delivered;
			using (var cts = new CancellationTokenSource())
			{
				cts.Cancel();
				Assert.DoesNotThrow(() => result = sender.Send(MakeEvent(), cts.Token));
			}

			Assert.AreEqual(SendResult.RetryableFailure, result);
		}

		[Test]
		public void Send_ShortDeadlineAgainstDelayedResponse_ReturnsRetryableFailureAndDoesNotThrow()
		{
			// Simulates a "black hole" server: it eventually responds, but not before our deadline
			// (BoundedDrain's per-attempt CancellationTokenSource, in production) fires.
			_server.Given(Request.Create().WithPath("/track").UsingPost())
				.RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)));

			var sender = new MixpanelEventSender("secret", _server.Urls[0],
				new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

			SendResult result = SendResult.Delivered;
			using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
			{
				Assert.DoesNotThrow(() => result = sender.Send(MakeEvent(), cts.Token));
			}

			Assert.AreEqual(SendResult.RetryableFailure, result);
		}

		[Test]
		public void Send_PostsFormEncodedBodyContainingTokenAndEventNameAndInsertId()
		{
			const string token = "my-token";

			// The stub only matches (and returns 200) if the POST body is the expected
			// "data=<url-encoded json>" shape and contains the token/event/insert-id we gave it --
			// this exercises MixpanelEventSender's real payload serialization end-to-end without
			// coupling the test to Segment.Serialization's exact JSON formatting (key order,
			// whitespace, etc).
			_server
				.Given(Request.Create()
					.WithPath("/track")
					.UsingPost()
					.WithBody(body =>
						body != null &&
						body.StartsWith("data=") &&
						body.Contains(Uri.EscapeDataString("\"event\"")) &&
						body.Contains(Uri.EscapeDataString("\"Save PDF\"")) &&
						body.Contains(Uri.EscapeDataString("\"token\"")) &&
						body.Contains(Uri.EscapeDataString("\"" + token + "\"")) &&
						body.Contains(Uri.EscapeDataString("\"$insert_id\"")) &&
						body.Contains(Uri.EscapeDataString("\"insert-id-1\""))))
				.RespondWith(Response.Create().WithStatusCode(200));

			var sender = new MixpanelEventSender(token, _server.Urls[0]);

			Assert.AreEqual(SendResult.Delivered, sender.Send(MakeEvent("Save PDF")),
				"expected the WireMock stub -- which only matches the expected form body -- to have matched");
		}
	}
}
