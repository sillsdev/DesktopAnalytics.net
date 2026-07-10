using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesktopAnalytics;
using NUnit.Framework;
using Segment.Serialization;

namespace DesktopAnalyticsTests
{
	// Layer 3 of the fidelity ladder in offline-analytics.md: the real EventSpool (on a temp
	// folder) driving a real MixpanelClient, with a scripted fake IEventSender standing in for the
	// network. Tests await DrainOnceAsync() directly rather than racing the real background timer (see
	// "Design-for-test seams" / "Flush-loop scheduling" in offline-analytics.md); InitializeForTest
	// never starts that timer.
	[TestFixture]
	public class MixpanelClientTests
	{
		private string _spoolDir;

		[SetUp]
		public void SetUp()
		{
			_spoolDir = Path.Combine(Path.GetTempPath(), "MixpanelClientTests_" + Guid.NewGuid());
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(_spoolDir))
			{
				try
				{
					Directory.Delete(_spoolDir, true);
				}
				catch (Exception e)
				{
					Console.WriteLine("MixpanelClientTests.TearDown: failed to delete " + _spoolDir + ": " + e);
				}
			}
		}

		// A scripted IEventSender: each call to Send() consumes the next step in the script (or
		// defaults to Delivered once the script is exhausted) and always records the event it was
		// given, so tests can assert both what happened and what was actually handed to the sender.
		private class ScriptedSender : IEventSender
		{
			private readonly Queue<Func<AnalyticsEvent, SendResult>> _script;
			public readonly List<AnalyticsEvent> Sent = new List<AnalyticsEvent>();

			public ScriptedSender(IEnumerable<Func<AnalyticsEvent, SendResult>> script)
			{
				_script = new Queue<Func<AnalyticsEvent, SendResult>>(script);
			}

			public Task<SendResult> SendAsync(AnalyticsEvent evt, CancellationToken cancellationToken = default)
			{
				Sent.Add(evt);
				var step = _script.Count > 0 ? _script.Dequeue() : (_ => SendResult.Delivered);
				// Deliberately NOT wrapped in Task.FromResult of a try/catch: a script step that
				// throws makes this fake violate IEventSender's never-throw contract on purpose,
				// exactly like the sync fake it replaced -- the crash-window tests rely on it.
				return Task.FromResult(step(evt));
			}
		}

		private class AlwaysResultSender : IEventSender
		{
			private readonly SendResult _result;
			public int CallCount;

			public AlwaysResultSender(SendResult result)
			{
				_result = result;
			}

			public Task<SendResult> SendAsync(AnalyticsEvent evt, CancellationToken cancellationToken = default)
			{
				CallCount++;
				return Task.FromResult(_result);
			}
		}

		private class ThrowingSender : IEventSender
		{
			public Task<SendResult> SendAsync(AnalyticsEvent evt, CancellationToken cancellationToken = default)
			{
				throw new InvalidOperationException("simulated sender failure");
			}
		}

		// Simulates a "black hole" server (accepts the connection but never responds): waits until
		// its CancellationToken is signaled -- exactly what BoundedDrain's deadline does -- then
		// reports the send as failed. Guards against ever being handed a token that can't be
		// canceled, which would otherwise stall the whole test run rather than just fail one test.
		private class BlockingUntilCanceledSender : IEventSender
		{
			public int CallCount;

			public async Task<SendResult> SendAsync(AnalyticsEvent evt, CancellationToken cancellationToken = default)
			{
				Interlocked.Increment(ref CallCount);
				if (cancellationToken.CanBeCanceled)
				{
					try
					{
						await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
					}
					catch (OperationCanceledException)
					{
						// The deadline fired -- fall through and report the failure, per the
						// IEventSender contract (treat cancellation like any transient failure).
					}
				}
				return SendResult.RetryableFailure;
			}
		}

		// ---- NO-LOSS -------------------------------------------------------------------------

		[Test]
		public async Task DrainOnce_RetryRetryThenDeliverAcrossSuccessiveCalls_DeliversExactlyOnceAndEmptiesSpool()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				var sender = new ScriptedSender(new Func<AnalyticsEvent, SendResult>[]
				{
					_ => SendResult.RetryableFailure,
					_ => SendResult.RetryableFailure,
					_ => SendResult.Delivered
				});
				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender);

				client.Track("user-1", "Save", null);
				Assert.AreEqual(1, spool.ApproximateCount);

				await client.DrainOnceAsync(); // 1st attempt: RetryableFailure -- left in spool.
				Assert.AreEqual(1, spool.ApproximateCount);

				await client.DrainOnceAsync(); // 2nd attempt: RetryableFailure -- still left in spool.
				Assert.AreEqual(1, spool.ApproximateCount);

				await client.DrainOnceAsync(); // 3rd attempt: Delivered -- removed.
				Assert.AreEqual(0, spool.ApproximateCount);

				Assert.AreEqual(3, sender.Sent.Count, "the event should have been handed to the sender exactly three times");
				Assert.AreEqual(1, client.Statistics.Succeeded);
				Assert.AreEqual(0, client.Statistics.Failed);
			}
		}

		// ---- CRASH-WINDOW / DEDUP -------------------------------------------------------------

		[Test]
		public async Task DrainOnce_SenderRecordsThenThrows_SameInsertIdSeenAgainOnLaterRetry()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				var client = new MixpanelClient();

				// A sender that behaves exactly like one that delivered the event over the network
				// and then crashed/threw before this process could observe success and flush --
				// the crash-window this whole design exists to survive.
				var crashingSender = new ScriptedSender(new Func<AnalyticsEvent, SendResult>[]
				{
					_ => throw new InvalidOperationException("simulated crash after send, before ack")
				});
				client.InitializeForTest(spool, crashingSender);
				client.Track("user-1", "Save", null);

				Assert.DoesNotThrowAsync(async () => await client.DrainOnceAsync());
				Assert.AreEqual(1, spool.ApproximateCount, "the event must not be removed -- the sender never confirmed delivery");
				Assert.AreEqual(1, crashingSender.Sent.Count);

				// "Reopen"/retry with a now-succeeding sender against the SAME spool.
				var succeedingSender = new ScriptedSender(new Func<AnalyticsEvent, SendResult>[]
				{
					_ => SendResult.Delivered
				});
				client.InitializeForTest(spool, succeedingSender);
				await client.DrainOnceAsync();

				Assert.AreEqual(0, spool.ApproximateCount);
				Assert.AreEqual(1, succeedingSender.Sent.Count);

				// The key assertion: the sender saw the SAME $insert_id both times, proving Mixpanel
				// would dedup the at-least-once replay rather than double-counting the event.
				Assert.AreEqual(crashingSender.Sent[0].InsertId, succeedingSender.Sent[0].InsertId);
			}
		}

		// ---- POISON ---------------------------------------------------------------------------

		[Test]
		public async Task DrainOnce_PoisonDrop_RemovesEventAndIncrementsFailedWithoutRetrying()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				var sender = new AlwaysResultSender(SendResult.PoisonDrop);
				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender);

				client.Track("user-1", "BadEvent", null);
				await client.DrainOnceAsync();

				Assert.AreEqual(0, spool.ApproximateCount, "a poison event must be dropped, not left to wedge the spool");
				Assert.AreEqual(1, sender.CallCount);
				Assert.AreEqual(1, client.Statistics.Failed);
				Assert.AreEqual(0, client.Statistics.Succeeded);

				// A further drain must not re-attempt it (it is already gone).
				await client.DrainOnceAsync();
				Assert.AreEqual(1, sender.CallCount);
			}
		}

		// ---- CONSENT PURGE ----------------------------------------------------------------------

		[Test]
		public void PurgeQueuedEvents_EmptiesNonEmptySpool()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				var sender = new AlwaysResultSender(SendResult.Delivered);
				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender);

				client.Track("user-1", "A", null);
				client.Track("user-1", "B", null);
				Assert.AreEqual(2, spool.ApproximateCount);

				client.PurgeQueuedEvents();

				Assert.AreEqual(0, spool.ApproximateCount);
			}
		}

		[Test]
		public void PurgeQueuedEvents_NeverThrows_EvenWithNoSpool()
		{
			var client = new MixpanelClient();
			client.InitializeForTest(null, new AlwaysResultSender(SendResult.Delivered));

			Assert.DoesNotThrow(() => client.PurgeQueuedEvents());
		}

		// ---- SCRUBBING --------------------------------------------------------------------------

		[Test]
		public async Task Track_ExceptionEventWithUserPathInStackTrace_ScrubsBeforeSpoolingAndSending()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				var sender = new AlwaysResultSender(SendResult.Delivered);
				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender);

				var props = new JsonObject
				{
					{ "Message", "Boom" },
					{ "Stack Trace", @"at Foo.Bar() in C:\Users\alice\src\Foo.cs:line 10" }
				};
				client.Track("user-1", "Exception", props);

				// Prove it was scrubbed BEFORE hitting the disk by scanning the raw spool files
				// themselves, before anything drains: no assertion on what the sender is later
				// handed can distinguish scrub-at-enqueue from scrub-at-send, but the bytes on
				// disk can. Guard against vacuously passing (e.g. if the persisted encoding ever
				// changes) by requiring the scrubbed marker to actually be FOUND on disk.
				var scrubbedMarkerFoundOnDisk = false;
				foreach (var file in EventSpoolTests.ReadableSpoolFiles(_spoolDir))
				{
					StringAssert.DoesNotContain("alice", file.Value,
						"unscrubbed user path found on disk in " + file.Key);
					if (file.Value.Contains("%USER%"))
						scrubbedMarkerFoundOnDisk = true;
				}
				Assert.IsTrue(scrubbedMarkerFoundOnDisk,
					"expected to find the scrubbed path (%USER%) in the raw spool files -- if this " +
					"stops being readable as UTF-8 text, rework this scan");

				// And the scrubbed form (not just an absent event) is what got persisted: drain
				// with a sender that records what it was actually handed.
				var scriptedSender = new ScriptedSender(new Func<AnalyticsEvent, SendResult>[]
				{
					_ => SendResult.Delivered
				});
				var reopenedClient = new MixpanelClient();
				reopenedClient.InitializeForTest(spool, scriptedSender);
				await reopenedClient.DrainOnceAsync();

				Assert.AreEqual(1, scriptedSender.Sent.Count);
				var sentStackTrace = ((JsonPrimitive)scriptedSender.Sent[0].Properties["Stack Trace"]).Content;
				StringAssert.DoesNotContain("alice", sentStackTrace);
				StringAssert.Contains("%USER%", sentStackTrace);
			}
		}

		[Test]
		public async Task Track_EventNameContainingUserPath_ScrubsEventName()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				var client = new MixpanelClient();
				client.InitializeForTest(spool, new AlwaysResultSender(SendResult.Delivered));

				client.Track("user-1", @"Opened C:\Users\alice\project.xyz", null);

				var sender = new ScriptedSender(new Func<AnalyticsEvent, SendResult>[] { _ => SendResult.Delivered });
				var drainer = new MixpanelClient();
				drainer.InitializeForTest(spool, sender);
				await drainer.DrainOnceAsync();

				StringAssert.DoesNotContain("alice", sender.Sent[0].EventName);
				StringAssert.Contains("%USER%", sender.Sent[0].EventName);
			}
		}

		// ---- NEVER CRASH ------------------------------------------------------------------------

		// Calling Track on a client that was never initialized at all is a programming error in
		// the host and must throw (the original MixpanelClient dereferenced a null field there) --
		// NOT silently drop every event. Distinct from Track_WithNoSpool_NeverThrows below, where
		// Initialize DID run but the spool could not be created.
		[Test]
		public void Track_WithoutInitialize_ThrowsInvalidOperationException()
		{
			var client = new MixpanelClient();
			Assert.Throws<InvalidOperationException>(() => client.Track("user-1", "Save", null));
		}

		[Test]
		public void Track_WithNoSpool_NeverThrows()
		{
			var client = new MixpanelClient();
			// Simulates a failed Initialize (e.g. the EventSpool constructor threw acquiring the
			// cross-process lock), which leaves the client with no spool at all.
			client.InitializeForTest(null, new AlwaysResultSender(SendResult.Delivered));

			Assert.DoesNotThrow(() => client.Track("user-1", "Save", null));
			Assert.DoesNotThrowAsync(async () => await client.DrainOnceAsync());
			Assert.DoesNotThrow(() => client.Flush());
			Assert.DoesNotThrow(() => client.ShutDown());
		}

		[Test]
		public void DrainOnceAndFlush_SenderAlwaysThrows_NeverPropagateOutOfMixpanelClient()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				var client = new MixpanelClient();
				client.InitializeForTest(spool, new ThrowingSender());

				client.Track("user-1", "Save", null);

				Assert.DoesNotThrowAsync(async () => await client.DrainOnceAsync());
				Assert.DoesNotThrow(() => client.Flush());

				// A sender that only ever throws must not be treated as delivered/poison -- the
				// event must still be sitting in the spool for a later retry.
				Assert.AreEqual(1, spool.ApproximateCount);
			}
		}

		// ---- SHUTDOWN OFFLINE -------------------------------------------------------------------

		[Test]
		public void ShutDown_SenderAlwaysFails_ReturnsPromptlyAndEventsRemainOnDisk()
		{
			var spool = new EventSpool(_spoolDir, 10);
			var sender = new AlwaysResultSender(SendResult.RetryableFailure);
			var client = new MixpanelClient();
			client.InitializeForTest(spool, sender);

			for (var i = 0; i < 5; i++)
				client.Track("user-1", "Save-" + i, null);

			var stopwatch = Stopwatch.StartNew();
			var completed = Task.Run(() => client.ShutDown()).Wait(TimeSpan.FromSeconds(15));
			stopwatch.Stop();

			Assert.IsTrue(completed, "ShutDown() did not return within the timeout while offline");
			Assert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(15));

			// ShutDown() must have released the spool's cross-process lock -- reopening it and
			// finding all 5 events proves nothing was lost, and that the lock really was released.
			using (var reopened = new EventSpool(_spoolDir, 10))
			{
				Assert.AreEqual(5, reopened.ApproximateCount);
			}
		}

		[Test]
		public void Flush_WhileOffline_ReturnsPromptlyWithoutEmptyingSpool()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				var sender = new AlwaysResultSender(SendResult.RetryableFailure);
				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender);

				client.Track("user-1", "Save", null);

				var stopwatch = Stopwatch.StartNew();
				var completed = Task.Run(() => client.Flush()).Wait(TimeSpan.FromSeconds(15));
				stopwatch.Stop();

				Assert.IsTrue(completed, "Flush() did not return within the timeout while offline");
				Assert.AreEqual(1, spool.ApproximateCount, "an offline flush must leave the undelivered event on disk");
			}
		}

		// ---- BOUNDED SHUTDOWN/FLUSH AGAINST A "BLACK HOLE" SERVER --------------------------------
		//
		// Regression coverage for the bug where BoundedDrain's 5s wall-clock bound was only checked
		// BETWEEN DrainOnce calls: a server that accepts the connection but never responds could
		// make a single DrainOnce take up to ~45s (3 attempts x a 15s HttpClient timeout) before the
		// bound was ever consulted, hanging ShutDown()/Flush(). These use a sender that blocks until
		// the CancellationToken BoundedDrain hands it is canceled, which only happens promptly if
		// the per-attempt deadline is actually wired through.

		[Test]
		public void ShutDown_SenderBlocksUntilCanceled_ReturnsWithinBoundAndEventRemainsInSpool()
		{
			var spool = new EventSpool(_spoolDir, 10);
			var sender = new BlockingUntilCanceledSender();
			var client = new MixpanelClient();
			client.InitializeForTest(spool, sender);

			client.Track("user-1", "Save", null);

			var stopwatch = Stopwatch.StartNew();
			var completed = Task.Run(() => client.ShutDown()).Wait(TimeSpan.FromSeconds(10));
			stopwatch.Stop();

			Assert.IsTrue(completed, "ShutDown() did not return within the outer test timeout");
			Assert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(10),
				"ShutDown() must be bounded even against a server that never responds");
			Assert.GreaterOrEqual(sender.CallCount, 1);

			// ShutDown() must have released the spool's cross-process lock -- reopening it and
			// finding the event proves nothing was lost (it was rolled back, not flushed) and that
			// the lock really was released.
			using (var reopened = new EventSpool(_spoolDir, 10))
			{
				Assert.AreEqual(1, reopened.ApproximateCount,
					"the undelivered event must remain in the spool after a bounded shutdown");
			}
		}

		[Test]
		public void Flush_SenderBlocksUntilCanceled_ReturnsWithinBoundAndEventRemainsInSpool()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				var sender = new BlockingUntilCanceledSender();
				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender);

				client.Track("user-1", "Save", null);

				var stopwatch = Stopwatch.StartNew();
				var completed = Task.Run(() => client.Flush()).Wait(TimeSpan.FromSeconds(10));
				stopwatch.Stop();

				Assert.IsTrue(completed, "Flush() did not return within the outer test timeout");
				Assert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(10),
					"Flush() must be bounded even against a server that never responds");
				Assert.AreEqual(1, spool.ApproximateCount,
					"the undelivered event must remain in the spool after a bounded flush");
			}
		}

		// ---- STATISTICS -------------------------------------------------------------------------

		[Test]
		public void Track_IncrementsSubmittedImmediately()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				var client = new MixpanelClient();
				client.InitializeForTest(spool, new AlwaysResultSender(SendResult.Delivered));

				client.Track("user-1", "A", null);
				client.Track("user-1", "B", null);

				Assert.AreEqual(2, client.Statistics.Submitted);
			}
		}

		// ---- POLLY --------------------------------------------------------------------------------
		//
		// NOTE on which of the two documented approaches this uses (see offline-analytics.md,
		// "Polly"): rather than driving Polly's real (non-zero) backoff delays through a
		// FakeTimeProvider -- which is documented to hit the Polly #1932
		// SynchronizationContext-vs-ConfigureAwait gotcha -- this test injects a real retry pipeline
		// (via MixpanelClient.BuildDefaultPipeline) configured with a ZERO base delay. That keeps
		// the retry *strategy* itself real (ShouldHandle, MaxRetryAttempts, backoff wiring) while
		// making the test deterministic and instantaneous, per the spec's documented fallback.

		[Test]
		public async Task DrainOnce_WithRealRetryPipelineAndZeroDelay_TransientThenSuccess_DeliversWithinOneDrainOnceCall()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				var sender = new ScriptedSender(new Func<AnalyticsEvent, SendResult>[]
				{
					_ => SendResult.RetryableFailure,
					_ => SendResult.Delivered
				});
				var pipeline = MixpanelClient.BuildDefaultPipeline(TimeProvider.System,
					maxRetryAttempts: 2, retryDelay: TimeSpan.Zero);

				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender, pipeline: pipeline);

				client.Track("user-1", "Save", null);
				await client.DrainOnceAsync();

				Assert.AreEqual(0, spool.ApproximateCount,
					"Polly should have retried in-process and delivered within this single DrainOnce call");
				Assert.AreEqual(2, sender.Sent.Count,
					"the sender should have been called twice: once for the transient failure, once for the retry");
				Assert.AreEqual(1, client.Statistics.Succeeded);
			}
		}

		[Test]
		public async Task DrainOnce_WithRealCircuitBreakerAndZeroDelay_SustainedFailures_TripsBreakerAndStopsCallingSender()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				var sender = new AlwaysResultSender(SendResult.RetryableFailure);
				// MinimumThroughput=3 by default (see BuildDefaultPipeline): force enough failing
				// attempts through quickly (small batch, zero delay) to trip the breaker, then verify
				// a further drain of a fresh event does not even reach the sender.
				// MaxRetryAttempts must be >= 1 (Polly validates this); 1 is close enough to "no
				// retry" for this test's purposes -- what matters is that sender calls stop growing
				// once the breaker trips, not the exact attempt count.
				var pipeline = MixpanelClient.BuildDefaultPipeline(TimeProvider.System,
					maxRetryAttempts: 1, retryDelay: TimeSpan.Zero);

				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender, pipeline: pipeline, batchSize: 1);

				for (var i = 0; i < 8; i++)
				{
					client.Track("user-1", "Save-" + i, null);
					await client.DrainOnceAsync();
				}

				var callsAfterWarmup = sender.CallCount;

				client.Track("user-1", "OneMore", null);
				await client.DrainOnceAsync();

				// Once the breaker is open, MixpanelClient must treat BrokenCircuitException as a
				// RetryableFailure (leaving the event spooled) WITHOUT invoking the sender again.
				Assert.AreEqual(callsAfterWarmup, sender.CallCount,
					"once the circuit breaker is open, the sender must not be called again");
				Assert.Greater(spool.ApproximateCount, 0);
			}
		}

		[Test]
		public async Task DrainOnce_WithRealCircuitBreakerAndFixedMinimumThroughput_OpensAfterOneFullyFailingTick()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				var sender = new AlwaysResultSender(SendResult.RetryableFailure);
				// Regression test for the bug where MinimumThroughput=4 made the breaker inert: a
				// fully failing DrainOnce tick produces exactly 3 outcomes (1 attempt + the default
				// MaxRetryAttempts=2 retries), and successive ticks are 30s apart -- far outside the
				// 10s sampling window -- so 4 outcomes could never land in the same window during a
				// real outage. With MinimumThroughput fixed to 3, a single fully-failing tick alone
				// must be enough to trip the breaker.
				var pipeline = MixpanelClient.BuildDefaultPipeline(TimeProvider.System, retryDelay: TimeSpan.Zero);

				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender, pipeline: pipeline, batchSize: 1);

				client.Track("user-1", "Save", null);
				await client.DrainOnceAsync();

				var callsAfterFirstTick = sender.CallCount;
				Assert.GreaterOrEqual(callsAfterFirstTick, 3,
					"one fully-failing tick should make at least 3 attempts (1 try + 2 retries)");

				client.Track("user-1", "OneMore", null);
				await client.DrainOnceAsync();

				Assert.AreEqual(callsAfterFirstTick, sender.CallCount,
					"the breaker should already be open after just one failing tick, so the sender must not be called again");
				Assert.Greater(spool.ApproximateCount, 0);
			}
		}

		// ---- CONTRACT: host parameter is rejected (Mixpanel does not support a host) -----------

		[Test]
		public void Initialize_WithNonEmptyHost_ThrowsArgumentException()
		{
			var client = new MixpanelClient();
			// Validated before any spool/timer is created, so no side effects and it propagates to the
			// caller exactly as the original MixpanelClient did.
			Assert.Throws<ArgumentException>(() => client.Initialize("secret", "https://example.com"));
		}

		// ---- CONSENT PAUSE/RESUME (fix: re-enabling consent re-arms the flush loop) -------------

		[Test]
		public void PurgeQueuedEvents_ThenResumeSending_TogglesSendingPaused()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				var client = new MixpanelClient();
				client.InitializeForTest(spool, new AlwaysResultSender(SendResult.Delivered));

				Assert.IsFalse(client.SendingPaused, "should start unpaused");

				client.PurgeQueuedEvents();
				Assert.IsTrue(client.SendingPaused, "revoking consent (purge) must pause the flush loop");

				client.ResumeSending();
				Assert.IsFalse(client.SendingPaused, "re-granting consent (resume) must un-pause the flush loop");
			}
		}
	}
}
