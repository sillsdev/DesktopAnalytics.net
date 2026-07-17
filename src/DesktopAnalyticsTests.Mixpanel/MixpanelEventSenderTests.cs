using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DesktopAnalytics;
using NUnit.Framework;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DesktopAnalyticsTests
{
	// Layer 4 of the fidelity ladder in offline-analytics.md: a local HTTP stub (WireMock.Net)
	// standing in for Mixpanel's real /import endpoint, exercising MixpanelEventSender's real HTTP
	// posting/serialization and its response->BatchSendResult classification.
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

		private static AnalyticsEvent MakeEvent(string name = "Save", string insertId = "insert-id-1")
		{
			return AnalyticsEvent.Create("user-1", name, null, insertId,
				DateTimeOffset.UtcNow);
		}

		private static IReadOnlyList<AnalyticsEvent> OneEvent(string name = "Save")
		{
			return new[] { MakeEvent(name) };
		}

		private IRequestBuilder ImportRequest()
		{
			return Request.Create().WithPath("/import").UsingPost();
		}

		[Test]
		public async Task Send_200Response_ReturnsDeliveredWithNoFailedRecords()
		{
			_server.Given(ImportRequest())
				.RespondWith(Response.Create().WithStatusCode(200)
					.WithBody("{\"code\":200,\"num_records_imported\":1,\"status\":\"OK\"}"));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			var result = await sender.SendBatchAsync(OneEvent());
			Assert.AreEqual(SendResult.Delivered, result.Outcome);
			Assert.IsEmpty(result.FailedIndices);
		}

		[Test]
		public async Task Send_500Response_ReturnsRetryableRejection()
		{
			_server.Given(ImportRequest())
				.RespondWith(Response.Create().WithStatusCode(500));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			// A 5xx means the request DID reach Mixpanel -- distinct from a connectivity failure
			// (see SendResult.RetryableRejection), so it counts against the spool's retry-attempt
			// ceiling where RetryableFailure does not.
			Assert.AreEqual(SendResult.RetryableRejection, (await sender.SendBatchAsync(OneEvent())).Outcome);
		}

		[Test]
		public async Task Send_429Response_ReturnsRetryableRejection()
		{
			_server.Given(ImportRequest())
				.RespondWith(Response.Create().WithStatusCode(429));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			Assert.AreEqual(SendResult.RetryableRejection, (await sender.SendBatchAsync(OneEvent())).Outcome);
		}

		[Test]
		public async Task Send_408Response_ReturnsRetryableRejection()
		{
			_server.Given(ImportRequest())
				.RespondWith(Response.Create().WithStatusCode(408));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			Assert.AreEqual(SendResult.RetryableRejection, (await sender.SendBatchAsync(OneEvent())).Outcome);
		}

		[Test]
		public async Task Send_400ResponseWithoutParseableBody_ReturnsPoisonDrop()
		{
			// A 400 with no failed_records means the request as a whole was rejected -- retrying
			// the same request can never succeed.
			_server.Given(ImportRequest())
				.RespondWith(Response.Create().WithStatusCode(400).WithBody("Bad Request"));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			Assert.AreEqual(SendResult.PoisonDrop, (await sender.SendBatchAsync(OneEvent())).Outcome);
		}

		[Test]
		public async Task Send_401Response_ReturnsPoisonDrop()
		{
			_server.Given(ImportRequest())
				.RespondWith(Response.Create().WithStatusCode(401)
					.WithBody("{\"code\":401,\"error\":\"Unauthorized\",\"status\":\"error\"}"));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			Assert.AreEqual(SendResult.PoisonDrop, (await sender.SendBatchAsync(OneEvent())).Outcome);
		}

		// ---- STRICT-MODE PARTIAL FAILURE (failed_records) ----------------------------------------

		[Test]
		public async Task Send_400WithFailedRecords_ReturnsDeliveredWithOnlyThoseIndicesFailed()
		{
			// Strict-mode /import: the invalid records are reported per-index; the REST WERE
			// INGESTED. The whole batch is therefore finished with (Delivered), and only the
			// reported indices are poison.
			_server.Given(ImportRequest())
				.RespondWith(Response.Create().WithStatusCode(400).WithBody(
					"{\"code\":400,\"num_records_imported\":2,\"status\":\"Bad Request\"," +
					"\"failed_records\":[{\"index\":1,\"insert_id\":\"insert-id-b\"," +
					"\"field\":\"properties.time\",\"message\":\"'properties.time' is invalid\"}]}"));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);
			var batch = new[] { MakeEvent("A", "insert-id-a"), MakeEvent("B", "insert-id-b"), MakeEvent("C", "insert-id-c") };

			var result = await sender.SendBatchAsync(batch);

			Assert.AreEqual(SendResult.Delivered, result.Outcome,
				"a strict-mode 400 with failed_records means the request WAS processed -- the batch is finished with");
			CollectionAssert.AreEquivalent(new[] { 1 }, result.FailedIndices);
		}

		[Test]
		public async Task Send_400WithUnmappableFailedRecordIndex_ReturnsPoisonDrop()
		{
			// An index outside the batch we sent means we can't trust the body -- fall back to
			// whole-batch poison rather than misattributing the failure.
			_server.Given(ImportRequest())
				.RespondWith(Response.Create().WithStatusCode(400).WithBody(
					"{\"code\":400,\"failed_records\":[{\"index\":7}]}"));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			Assert.AreEqual(SendResult.PoisonDrop, (await sender.SendBatchAsync(OneEvent())).Outcome);
		}

		// ---- OFFLINE SHAPES ----------------------------------------------------------------------

		[Test]
		public async Task Send_ConnectionRefused_ReturnsRetryableFailure()
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

			Assert.AreEqual(SendResult.RetryableFailure, (await sender.SendBatchAsync(OneEvent())).Outcome);
		}

		[Test]
		public async Task Send_UnreachableHost_ReturnsRetryableFailure()
		{
			// A bad port on a live host is another common "offline" shape distinct from a
			// stopped-server connection refusal.
			var sender = new MixpanelEventSender("secret", "http://127.0.0.1:1",
				new HttpClient { Timeout = TimeSpan.FromSeconds(5) });

			Assert.AreEqual(SendResult.RetryableFailure, (await sender.SendBatchAsync(OneEvent())).Outcome);
		}

		// ---- CAPTIVE PORTAL (redirect handling) --------------------------------------------------

		[Test]
		public async Task Send_302Response_ReturnsRetryableFailureAndDoesNotFollowRedirect()
		{
			// Simulates a captive portal intercepting the POST and redirecting to its login page.
			_server.Given(ImportRequest())
				.RespondWith(Response.Create()
					.WithStatusCode(302)
					.WithHeader("Location", "/portal-login"));
			_server.Given(Request.Create().WithPath("/portal-login"))
				.RespondWith(Response.Create().WithStatusCode(200));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			Assert.AreEqual(SendResult.RetryableFailure, (await sender.SendBatchAsync(OneEvent())).Outcome);

			// The key assertion: AllowAutoRedirect must be disabled, so the portal's login page is
			// never actually requested. If it were followed, this sender would see the portal's 200
			// and wrongly classify the batch as Delivered even though Mixpanel never received it.
			Assert.IsFalse(_server.LogEntries.Any(e => e.RequestMessage.Path == "/portal-login"),
				"the redirect target must never have been requested (AllowAutoRedirect must be false)");
		}

		// ---- CANCELLATION (bounded drain deadline) ------------------------------------------------

		[Test]
		public void Send_PreCancelledToken_ReturnsRetryableFailureAndDoesNotThrow()
		{
			_server.Given(ImportRequest())
				.RespondWith(Response.Create().WithStatusCode(200));

			var sender = new MixpanelEventSender("secret", _server.Urls[0]);

			var result = BatchSendResult.Delivered;
			using (var cts = new CancellationTokenSource())
			{
				cts.Cancel();
				Assert.DoesNotThrowAsync(async () => result = await sender.SendBatchAsync(OneEvent(), cts.Token));
			}

			Assert.AreEqual(SendResult.RetryableFailure, result.Outcome);
		}

		[Test]
		public void Send_ShortDeadlineAgainstDelayedResponse_ReturnsRetryableFailureAndDoesNotThrow()
		{
			// Simulates a "black hole" server: it eventually responds, but not before our deadline
			// (BoundedDrain's per-attempt CancellationTokenSource, in production) fires.
			_server.Given(ImportRequest())
				.RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(5)));

			var sender = new MixpanelEventSender("secret", _server.Urls[0],
				new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

			var result = BatchSendResult.Delivered;
			using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
			{
				Assert.DoesNotThrowAsync(async () => result = await sender.SendBatchAsync(OneEvent(), cts.Token));
			}

			Assert.AreEqual(SendResult.RetryableFailure, result.Outcome);
		}

		// ---- WIRE FORMAT ---------------------------------------------------------------------------

		[Test]
		public async Task Send_TwoEvents_PostsOneJsonArrayRequestWithBasicAuthAndStrictMode()
		{
			const string token = "my-token";
			var expectedAuth = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(token + ":"));

			// The stub only matches (and returns 200) if this is a single strict-mode /import POST
			// whose JSON-array body carries both events with their insert ids, authenticated via
			// the project token as the basic-auth username -- exercising MixpanelEventSender's real
			// payload serialization end-to-end without coupling the test to
			// Segment.Serialization's exact JSON formatting (key order, whitespace, etc). Notably
			// there must be NO token in the payload itself: /import authenticates via the header.
			_server
				.Given(Request.Create()
					.WithPath("/import")
					.WithParam("strict", "1")
					.UsingPost()
					.WithHeader("Authorization", expectedAuth)
					.WithHeader("Content-Type", "application/json*")
					.WithBody(body =>
						body != null &&
						body.TrimStart().StartsWith("[") &&
						body.TrimEnd().EndsWith("]") &&
						body.Contains("\"event\"") &&
						body.Contains("\"Save PDF\"") &&
						body.Contains("\"Print\"") &&
						body.Contains("\"$insert_id\"") &&
						body.Contains("\"insert-id-a\"") &&
						body.Contains("\"insert-id-b\"") &&
						body.Contains("\"distinct_id\"") &&
						!body.Contains("\"token\"")))
				.RespondWith(Response.Create().WithStatusCode(200));

			var sender = new MixpanelEventSender(token, _server.Urls[0]);
			var batch = new[] { MakeEvent("Save PDF", "insert-id-a"), MakeEvent("Print", "insert-id-b") };

			var result = await sender.SendBatchAsync(batch);

			Assert.AreEqual(SendResult.Delivered, result.Outcome,
				"expected the WireMock stub -- which only matches the expected /import request -- to have matched");
			Assert.AreEqual(1, _server.LogEntries.Count(),
				"both events must go out in ONE request -- that is the point of batching");
		}
	}
}
