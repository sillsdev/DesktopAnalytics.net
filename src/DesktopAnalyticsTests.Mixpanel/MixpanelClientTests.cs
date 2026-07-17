using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopAnalytics;
using NUnit.Framework;
using Segment.Serialization;

namespace DesktopAnalyticsTests
{
	// Layer 3 of the fidelity ladder in offline-analytics.md: the real SqliteEventSpool (on a temp
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

		// A scripted IEventSender: each call to SendBatchAsync() consumes the next step in the
		// script (or defaults to Delivered once the script is exhausted) and always records the
		// events it was given (flattened, in order), so tests can assert both what happened and
		// what was actually handed to the sender.
		private class ScriptedSender : IEventSender
		{
			private readonly Queue<Func<IReadOnlyList<AnalyticsEvent>, BatchSendResult>> _script;
			public readonly List<AnalyticsEvent> Sent = new List<AnalyticsEvent>();
			public int CallCount;

			public ScriptedSender(IEnumerable<Func<IReadOnlyList<AnalyticsEvent>, BatchSendResult>> script)
			{
				_script = new Queue<Func<IReadOnlyList<AnalyticsEvent>, BatchSendResult>>(script);
			}

			public Task<BatchSendResult> SendBatchAsync(IReadOnlyList<AnalyticsEvent> events,
				CancellationToken cancellationToken = default)
			{
				Sent.AddRange(events);
				CallCount++;
				var step = _script.Count > 0 ? _script.Dequeue() : (_ => BatchSendResult.Delivered);
				// Deliberately NOT wrapped in Task.FromResult of a try/catch: a script step that
				// throws makes this fake violate IEventSender's never-throw contract on purpose,
				// exactly like the sync fake it replaced -- the crash-window tests rely on it.
				return Task.FromResult(step(events));
			}
		}

		private class AlwaysResultSender : IEventSender
		{
			private readonly SendResult _result;
			public int CallCount;
			public int EventCount;

			public AlwaysResultSender(SendResult result)
			{
				_result = result;
			}

			public Task<BatchSendResult> SendBatchAsync(IReadOnlyList<AnalyticsEvent> events,
				CancellationToken cancellationToken = default)
			{
				CallCount++;
				EventCount += events.Count;
				return Task.FromResult(new BatchSendResult(_result));
			}
		}

		private class ThrowingSender : IEventSender
		{
			public Task<BatchSendResult> SendBatchAsync(IReadOnlyList<AnalyticsEvent> events,
				CancellationToken cancellationToken = default)
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

			public async Task<BatchSendResult> SendBatchAsync(IReadOnlyList<AnalyticsEvent> events,
				CancellationToken cancellationToken = default)
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
				return BatchSendResult.Retryable;
			}
		}

		// Blocks inside SendBatchAsync until the test explicitly releases it (via a
		// TaskCompletionSource, not a real delay), so a test can precisely control when an
		// in-flight drain's network call "returns" -- used to race concurrent Track() calls against
		// an in-progress drain deterministically and quickly.
		private class BlockingUntilSignaledSender : IEventSender
		{
			private readonly TaskCompletionSource<bool> _release =
				new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			public int CallCount;

			public void Release() => _release.TrySetResult(true);

			public async Task<BatchSendResult> SendBatchAsync(IReadOnlyList<AnalyticsEvent> events,
				CancellationToken cancellationToken = default)
			{
				Interlocked.Increment(ref CallCount);
				await _release.Task.ConfigureAwait(false);
				return new BatchSendResult(SendResult.Delivered);
			}
		}

		// Reads every file under the spool directory that can be opened for shared reading.
		// SqliteEventSpool keeps its connection (and thus a share-mode lock on the database file)
		// open for the whole test, but WAL mode still allows a second, independent handle to read
		// the file's on-disk bytes concurrently; skipping any file that genuinely cannot be opened
		// this way does not weaken any event-content assertion.
		private static IEnumerable<KeyValuePair<string, string>> ReadableSpoolFiles(string spoolDir)
		{
			foreach (var file in Directory.GetFiles(spoolDir, "*", SearchOption.AllDirectories))
			{
				string content;
				try
				{
					using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
						       FileShare.ReadWrite | FileShare.Delete))
					using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
						content = reader.ReadToEnd();
				}
				catch (IOException)
				{
					continue;
				}

				yield return new KeyValuePair<string, string>(file, content);
			}
		}

		// ---- NO-LOSS -------------------------------------------------------------------------

		[Test]
		public async Task DrainOnce_RetryRetryThenDeliverAcrossSuccessiveCalls_DeliversExactlyOnceAndEmptiesSpool()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
			{
				var sender = new ScriptedSender(new Func<IReadOnlyList<AnalyticsEvent>, BatchSendResult>[]
				{
					_ => BatchSendResult.Retryable,
					_ => BatchSendResult.Retryable,
					_ => BatchSendResult.Delivered
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
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
			{
				var client = new MixpanelClient();

				// A sender that behaves exactly like one that delivered the event over the network
				// and then crashed/threw before this process could observe success and flush --
				// the crash-window this whole design exists to survive.
				var crashingSender = new ScriptedSender(new Func<IReadOnlyList<AnalyticsEvent>, BatchSendResult>[]
				{
					_ => throw new InvalidOperationException("simulated crash after send, before ack")
				});
				client.InitializeForTest(spool, crashingSender);
				client.Track("user-1", "Save", null);

				Assert.DoesNotThrowAsync(async () => await client.DrainOnceAsync());
				Assert.AreEqual(1, spool.ApproximateCount, "the event must not be removed -- the sender never confirmed delivery");
				Assert.AreEqual(1, crashingSender.Sent.Count);

				// "Reopen"/retry with a now-succeeding sender against the SAME spool.
				var succeedingSender = new ScriptedSender(new Func<IReadOnlyList<AnalyticsEvent>, BatchSendResult>[]
				{
					_ => BatchSendResult.Delivered
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
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
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
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
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

			Assert.IsFalse(client.SendingPaused, "test setup: should start unpaused");

			Assert.DoesNotThrow(() => client.PurgeQueuedEvents());

			// Statistics must be untouched -- there was never any spool to purge anything from.
			Assert.AreEqual(0, client.Statistics.Submitted);
			Assert.AreEqual(0, client.Statistics.Succeeded);
			Assert.AreEqual(0, client.Statistics.Failed);
			// The flush loop must still pause even with no spool -- PurgeQueuedEvents' PauseTimer()
			// call does not depend on there being a spool to actually purge.
			Assert.IsTrue(client.SendingPaused,
				"PurgeQueuedEvents must still pause the flush loop even with no spool");
		}

		// ---- SCRUBBING --------------------------------------------------------------------------

		[Test]
		public async Task Track_ExceptionEventWithUserPathInStackTrace_ScrubsBeforeSpoolingAndSending()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
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
				foreach (var file in ReadableSpoolFiles(_spoolDir))
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
				var scriptedSender = new ScriptedSender(new Func<IReadOnlyList<AnalyticsEvent>, BatchSendResult>[]
				{
					_ => BatchSendResult.Delivered
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
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
			{
				var client = new MixpanelClient();
				client.InitializeForTest(spool, new AlwaysResultSender(SendResult.Delivered));

				client.Track("user-1", @"Opened C:\Users\alice\project.xyz", null);

				var sender = new ScriptedSender(new Func<IReadOnlyList<AnalyticsEvent>, BatchSendResult>[]
					{ _ => BatchSendResult.Delivered });
				var drainer = new MixpanelClient();
				drainer.InitializeForTest(spool, sender);
				await drainer.DrainOnceAsync();

				StringAssert.DoesNotContain("alice", sender.Sent[0].EventName);
				StringAssert.Contains("%USER%", sender.Sent[0].EventName);
			}
		}

		// A user path nested inside a structured property (a JsonObject or JsonArray value, not a
		// top-level string) must be scrubbed too -- ScrubProperties must recurse, not just look at
		// the top level.
		[Test]
		public async Task Track_UserPathNestedInsideObjectAndArrayProperties_IsScrubbed()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
			{
				var client = new MixpanelClient();
				client.InitializeForTest(spool, new AlwaysResultSender(SendResult.Delivered));

				var props = new JsonObject
				{
					{
						"Context", new JsonObject
						{
							{ "Path", @"C:\Users\alice\file.txt" }
						}
					},
					{
						"RecentFiles", new JsonArray
						{
							@"C:\Users\alice\a.txt",
							@"C:\Users\alice\b.txt"
						}
					}
				};
				client.Track("user-1", "Exception", props);

				var sender = new ScriptedSender(new Func<IReadOnlyList<AnalyticsEvent>, BatchSendResult>[]
					{ _ => BatchSendResult.Delivered });
				var drainer = new MixpanelClient();
				drainer.InitializeForTest(spool, sender);
				await drainer.DrainOnceAsync();

				var sentProperties = sender.Sent[0].Properties;
				var context = (JsonObject)sentProperties["Context"];
				var contextPath = ((JsonPrimitive)context["Path"]).Content;
				StringAssert.DoesNotContain("alice", contextPath);
				StringAssert.Contains("%USER%", contextPath);

				var recentFiles = (JsonArray)sentProperties["RecentFiles"];
				foreach (var entry in recentFiles)
				{
					var path = ((JsonPrimitive)entry).Content;
					StringAssert.DoesNotContain("alice", path);
					StringAssert.Contains("%USER%", path);
				}
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
			// Simulates a failed Initialize (e.g. the spool's constructor threw opening its
			// database), which leaves the client with no spool at all.
			client.InitializeForTest(null, new AlwaysResultSender(SendResult.Delivered));

			Assert.DoesNotThrow(() => client.Track("user-1", "Save", null));
			// Track() must return (as a no-op) before it ever counts the event as submitted --
			// distinct from Track_WithoutInitialize_ThrowsInvalidOperationException, where a
			// never-initialized client is a programming error rather than a "no spool" no-op.
			Assert.AreEqual(0, client.Statistics.Submitted,
				"Track() with no spool must not count the event as submitted at all");

			Assert.DoesNotThrowAsync(async () => await client.DrainOnceAsync());
			Assert.DoesNotThrow(() => client.Flush());
			Assert.DoesNotThrowAsync(async () => await client.FlushAsync());
			Assert.DoesNotThrowAsync(async () => await client.ShutDownAsync());
			Assert.DoesNotThrow(() => client.ShutDown());

			// None of the above should have moved the needle: with no spool, there is nothing to
			// drain/flush/shut down, so the statistics must still read exactly as they started.
			Assert.AreEqual(0, client.Statistics.Submitted);
			Assert.AreEqual(0, client.Statistics.Succeeded);
			Assert.AreEqual(0, client.Statistics.Failed);
		}

		[Test]
		public void DrainOnceAndFlush_SenderAlwaysThrows_NeverPropagateOutOfMixpanelClient()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
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

		// ---- RETRY-ATTEMPT CEILING VS. PLAIN CONNECTIVITY FAILURE -------------------------------
		//
		// Regression coverage for the gap found wiring up the Phase 5 retry-attempt ceiling: a
		// naive "increment attempts on every RetryableFailure" implementation would also erode the
		// budget on plain offline stretches (SendResult.RetryableFailure), not just on a genuine
		// per-event server rejection (SendResult.RetryableRejection) -- e.g. dropping queued events
		// after only ~5 minutes offline at the default flush cadence, or within a single offline
		// ShutDown()/Flush() call (BoundedDrain's internal tight retry loop). These prove the two
		// SendResult values are handled distinctly all the way through MixpanelClient.

		[Test]
		public async Task DrainRepeatedly_SenderAlwaysRetryableRejection_EventDroppedAfterMaxAttempts()
		{
			const int maxAttempts = 5;
			using (var spool = new SqliteEventSpool(_spoolDir, 10, maxAttempts: maxAttempts))
			{
				var sender = new AlwaysResultSender(SendResult.RetryableRejection);
				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender);

				var exhaustedCount = 0;
				// ItemDroppedByRetryExhaustion is on IEventSpool; MixpanelClient itself only
				// exposes it via Statistics.Failed, so subscribe directly on the spool here to
				// pinpoint exactly when the drop happens.
				spool.ItemDroppedByRetryExhaustion += () => Interlocked.Increment(ref exhaustedCount);

				client.Track("user-1", "Save", null);

				// maxAttempts - 1 = 4 drains: a genuine server rejection each time, but not yet
				// enough to hit the ceiling.
				for (var i = 0; i < maxAttempts - 1; i++)
					await client.DrainOnceAsync();

				Assert.AreEqual(1, spool.ApproximateCount,
					"after " + (maxAttempts - 1) + " RetryableRejection verdicts (< maxAttempts = " +
					maxAttempts + "), the event must still be spooled");
				Assert.AreEqual(0, exhaustedCount);

				// The 5th (== maxAttempts) drain must drop it.
				await client.DrainOnceAsync();

				Assert.AreEqual(0, spool.ApproximateCount,
					"the " + maxAttempts + "th RetryableRejection must drop the event");
				Assert.AreEqual(1, exhaustedCount,
					"ItemDroppedByRetryExhaustion must fire exactly once");
				Assert.AreEqual(1, client.Statistics.Failed,
					"a retry-exhaustion drop must count as Failed, same as cap eviction");
			}
		}

		[Test]
		public async Task DrainRepeatedly_SenderAlwaysPlainRetryableFailure_NeverErodesAttemptBudgetNoMatterHowManyTimes()
		{
			const int maxAttempts = 5;
			using (var spool = new SqliteEventSpool(_spoolDir, 10, maxAttempts: maxAttempts))
			{
				// Simulates plain "offline" (or a throwing/circuit-broken sender) -- a connectivity
				// failure, not a server response -- via SendResult.RetryableFailure.
				var sender = new AlwaysResultSender(SendResult.RetryableFailure);
				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender);

				var exhaustedCount = 0;
				spool.ItemDroppedByRetryExhaustion += () => Interlocked.Increment(ref exhaustedCount);

				client.Track("user-1", "Save", null);

				// Far more than maxAttempts (5) -- if RetryableFailure incremented attempts like
				// RetryableRejection does, this would have dropped the event long ago.
				const int drains = 50;
				for (var i = 0; i < drains; i++)
					await client.DrainOnceAsync();

				Assert.AreEqual(1, spool.ApproximateCount,
					drains + " plain connectivity failures (SendResult.RetryableFailure) must NOT " +
					"erode the retry-attempt budget -- the event must still be spooled");
				Assert.AreEqual(0, exhaustedCount,
					"ItemDroppedByRetryExhaustion must never fire for connectivity-style failures");
				Assert.AreEqual(0, client.Statistics.Failed,
					"an event merely retried while offline must not count as Failed");
			}
		}

		[Test]
		public void Flush_SenderAlwaysRetryableRejection_DoesNotErodeAttemptCeilingDuringBoundedDrain()
		{
			// Regression test: BoundedDrainAsync (which Flush()/ShutDown() drive) can make up to
			// kBoundedDrainMaxAttempts (20) attempts back-to-back within its 5s bound -- far faster
			// than the once-per-30s-tick cadence the retry ceiling was sized against. If it counted
			// RetryableRejection against the ceiling the same way a normal timer tick does, a single
			// Flush() call against a server that is quickly rate-limiting/rejecting could burn
			// through this tiny ceiling in seconds and drop an otherwise-good event.
			const int maxAttempts = 3;
			using (var spool = new SqliteEventSpool(_spoolDir, 10, maxAttempts: maxAttempts))
			{
				var sender = new AlwaysResultSender(SendResult.RetryableRejection);
				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender);

				client.Track("user-1", "Save", null);

				var completed = Task.Run(() => client.Flush()).Wait(TimeSpan.FromSeconds(15));

				Assert.IsTrue(completed, "Flush() did not return within the timeout");
				Assert.Greater(sender.CallCount, maxAttempts,
					"the bounded drain loop must have made more attempts than maxAttempts, to prove " +
					"this is a real exercise of the exemption rather than a vacuously-passing test");
				Assert.AreEqual(1, spool.ApproximateCount,
					"a RetryableRejection storm during a single bounded Flush()/ShutDown() drain " +
					"must not erode the per-event retry ceiling the way a normal timer tick does");
				Assert.AreEqual(0, client.Statistics.Failed,
					"the event must not have been dropped");
			}
		}

		// ---- UNDESERIALIZABLE (CORRUPT) SPOOLED EVENTS -------------------------------------------
		//
		// Regression coverage for IEventSpool.ItemDroppedByCorruption: a claimed row whose payload
		// cannot be deserialized (on-disk corruption, or a payload from a since-upgraded
		// incompatible schema) is removed by the spool so it cannot wedge the queue -- but before
		// this fix, that removal did not increment Statistics.Failed, silently breaking the
		// invariant Statistics.Submitted == Statistics.Succeeded + Statistics.Failed (Submitted was
		// already incremented when the event was originally Track()'d).

		[Test]
		public async Task DrainOnce_EventCorruptedOnDiskBeforeDrain_RemovedAndCountedAsFailed()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
			{
				var client = new MixpanelClient();
				var sender = new AlwaysResultSender(SendResult.Delivered);
				client.InitializeForTest(spool, sender);

				client.Track("user-1", "Save", null);
				Assert.AreEqual(1, spool.ApproximateCount);

				SpoolCorruptionTestHelper.CorruptAllPayloads(_spoolDir);

				await client.DrainOnceAsync();

				Assert.AreEqual(0, spool.ApproximateCount,
					"an undeserializable row must be removed rather than retried forever");
				Assert.AreEqual(0, sender.CallCount,
					"the sender must never be called for a batch that was entirely undeserializable");
				Assert.AreEqual(1, client.Statistics.Submitted);
				Assert.AreEqual(0, client.Statistics.Succeeded);
				Assert.AreEqual(1, client.Statistics.Failed,
					"a corrupt/undeserializable row must count as Failed so Submitted == Succeeded + Failed");
			}
		}

		// ---- AGE-BASED RETENTION EXPIRY -----------------------------------------------------------
		//
		// Regression coverage for IEventSpool.ItemDroppedByExpiry: an event aged out by TrimExpired's
		// 60-day retention floor is removed so it cannot accumulate forever. Unlike a corrupt row or
		// a retry-ceiling drop, this is not a delivery failure -- it is counted separately as
		// Statistics.Expired rather than Statistics.Failed (see OnItemExpired's doc comment).

		[Test]
		public async Task DrainOnce_EventAgedOutByRetentionFloor_RemovedAndCountedAsExpired()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
			{
				var client = new MixpanelClient();
				var sender = new AlwaysResultSender(SendResult.Delivered);
				client.InitializeForTest(spool, sender);

				client.Track("user-1", "Save", null);
				Assert.AreEqual(1, spool.ApproximateCount);

				// Backdate the row directly (bypassing Track()'s own time-stamping) so
				// DrainOnceCoreAsync's automatic TrimExpired call (60-day floor) considers it expired
				// before ProcessBatchAsync ever gets to claim it.
				using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
					       "Data Source=" + Path.Combine(_spoolDir, "spool.db")))
				{
					conn.Open();
					using (var cmd = conn.CreateCommand())
					{
						cmd.CommandText = "UPDATE events SET time_unix_ms = 0;";
						cmd.ExecuteNonQuery();
					}
				}

				await client.DrainOnceAsync();

				Assert.AreEqual(0, spool.ApproximateCount,
					"an aged-out event must be removed by the retention floor");
				Assert.AreEqual(0, sender.CallCount,
					"the sender must never be called for an event trimmed before it could be claimed");
				Assert.AreEqual(1, client.Statistics.Submitted);
				Assert.AreEqual(0, client.Statistics.Succeeded);
				Assert.AreEqual(0, client.Statistics.Failed,
					"aging out is a retention decision, not a delivery failure -- must not count as Failed");
				Assert.AreEqual(1, client.Statistics.Expired,
					"an event dropped by the retention floor must count as Expired so " +
					"Submitted == Succeeded + Failed + Expired");
			}
		}

		// ---- SHUTDOWN OFFLINE -------------------------------------------------------------------

		[Test]
		public void ShutDown_SenderAlwaysFails_ReturnsPromptlyAndEventsRemainOnDisk()
		{
			var spool = new SqliteEventSpool(_spoolDir, 10);
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

			// ShutDown() must have disposed the spool's connection -- reopening it and finding all
			// 5 events proves nothing was lost, and that the connection really was released.
			using (var reopened = new SqliteEventSpool(_spoolDir, 10))
			{
				Assert.AreEqual(5, reopened.ApproximateCount);
			}
		}

		[Test]
		public void Flush_WhileOffline_ReturnsPromptlyWithoutEmptyingSpool()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
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
			var spool = new SqliteEventSpool(_spoolDir, 10);
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

			// ShutDown() must have disposed the spool's connection -- reopening it and finding the
			// event proves nothing was lost (it was rolled back, not flushed) and that the
			// connection really was released.
			using (var reopened = new SqliteEventSpool(_spoolDir, 10))
			{
				Assert.AreEqual(1, reopened.ApproximateCount,
					"the undelivered event must remain in the spool after a bounded shutdown");
			}
		}

		[Test]
		public void Flush_SenderBlocksUntilCanceled_ReturnsWithinBoundAndEventRemainsInSpool()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
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

		// ---- ASYNC PUBLIC SURFACE (FlushAsync/ShutDownAsync) -------------------------------------
		//
		// The async counterparts must honor the same bounds as their sync wrappers. Awaited
		// directly (no Task.Run + outer timeout) -- if the bound regressed, these would hang the
		// awaiting test and fail via the test runner's timeout, same signal as the sync tests.

		[Test]
		public async Task ShutDownAsync_SenderBlocksUntilCanceled_ReturnsWithinBoundAndEventRemainsInSpool()
		{
			var spool = new SqliteEventSpool(_spoolDir, 10);
			var sender = new BlockingUntilCanceledSender();
			var client = new MixpanelClient();
			client.InitializeForTest(spool, sender);

			client.Track("user-1", "Save", null);

			var stopwatch = Stopwatch.StartNew();
			await client.ShutDownAsync();
			stopwatch.Stop();

			Assert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(10),
				"ShutDownAsync() must be bounded even against a server that never responds");
			Assert.GreaterOrEqual(sender.CallCount, 1);

			// ShutDownAsync() must have disposed the spool's connection -- reopening it and finding
			// the event proves nothing was lost (it was rolled back, not flushed) and that the
			// connection really was released.
			using (var reopened = new SqliteEventSpool(_spoolDir, 10))
			{
				Assert.AreEqual(1, reopened.ApproximateCount,
					"the undelivered event must remain in the spool after a bounded shutdown");
			}
		}

		[Test]
		public async Task FlushAsync_SenderBlocksUntilCanceled_ReturnsWithinBoundAndEventRemainsInSpool()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
			{
				var sender = new BlockingUntilCanceledSender();
				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender);

				client.Track("user-1", "Save", null);

				var stopwatch = Stopwatch.StartNew();
				await client.FlushAsync();
				stopwatch.Stop();

				Assert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(10),
					"FlushAsync() must be bounded even against a server that never responds");
				Assert.AreEqual(1, spool.ApproximateCount,
					"the undelivered event must remain in the spool after a bounded flush");
			}
		}

		// FlushAsync/ShutDownAsync accept an external CancellationToken (linked into the bounded
		// drain's own deadline): an already-canceled token means no send is even attempted, the
		// events stay spooled, nothing throws -- and ShutDownAsync still releases the spool's lock.
		[Test]
		public async Task FlushAsyncAndShutDownAsync_PreCanceledToken_SkipSendingLeaveEventsAndNeverThrow()
		{
			var spool = new SqliteEventSpool(_spoolDir, 10);
			var sender = new BlockingUntilCanceledSender();
			var client = new MixpanelClient();
			client.InitializeForTest(spool, sender);

			client.Track("user-1", "Save", null);

			using (var cts = new CancellationTokenSource())
			{
				cts.Cancel();

				await client.FlushAsync(cts.Token);
				Assert.AreEqual(0, sender.CallCount,
					"a pre-canceled token must end the bounded drain before any send is attempted");
				Assert.AreEqual(1, spool.ApproximateCount);

				await client.ShutDownAsync(cts.Token);
				Assert.AreEqual(0, sender.CallCount);
			}

			// The connection must be released and the event still on disk for the next launch.
			using (var reopened = new SqliteEventSpool(_spoolDir, 10))
			{
				Assert.AreEqual(1, reopened.ApproximateCount);
			}
		}

		// ShutDownAsync followed by Dispose (the natural shape for a host that awaits ShutDownAsync
		// inside a `using` over the Analytics facade) must be a harmless no-op the second time.
		[Test]
		public async Task ShutDownAsync_ThenSyncShutDown_IsIdempotent()
		{
			var spool = new SqliteEventSpool(_spoolDir, 10);
			var client = new MixpanelClient();
			client.InitializeForTest(spool, new AlwaysResultSender(SendResult.Delivered));

			client.Track("user-1", "Save", null);

			await client.ShutDownAsync();
			Assert.DoesNotThrow(() => client.ShutDown());

			// The spool's connection must be released (and stay released) -- reopening
			// proves it.
			using (var reopened = new SqliteEventSpool(_spoolDir, 10))
			{
				Assert.AreEqual(0, reopened.ApproximateCount,
					"the event was delivered during the first shutdown's bounded drain");
			}
		}

		// ---- PER-EVENT SIZE CAP -------------------------------------------------------------------

		// An event over the spool's per-event byte cap (production: Mixpanel's documented 1MB
		// limit) is refused at Track time: never spooled, never handed to the sender, and counted
		// as Failed -- NOT left to wedge the head of the queue as an eternally-retryable timeout.
		[Test]
		public async Task Track_EventOverSizeCap_IsDroppedCountedFailedAndNeverReachesSender()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10, maxItemBytes: 500))
			{
				var sender = new AlwaysResultSender(SendResult.Delivered);
				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender);

				client.Track("user-1", "Exception",
					new JsonObject { { "Stack Trace", new string('x', 2000) } });

				Assert.AreEqual(0, spool.ApproximateCount, "the oversized event must never be spooled");
				Assert.AreEqual(1, client.Statistics.Submitted);
				Assert.AreEqual(1, client.Statistics.Failed);

				await client.DrainOnceAsync();
				Assert.AreEqual(0, sender.CallCount, "the oversized event must never reach the sender");

				// A normal event on the same client still flows end to end.
				client.Track("user-1", "Save", null);
				await client.DrainOnceAsync();
				Assert.AreEqual(1, sender.CallCount);
				Assert.AreEqual(1, client.Statistics.Succeeded);
			}
		}

		// A drain tick is byte-budgeted (bandwidth courtesy on slow/metered connections): three
		// ~200KB events against the 256KB/tick budget take two ticks, not one -- and each tick is
		// a single batched request, however many events it carries.
		[Test]
		public async Task DrainOnce_EventsExceedingPerTickByteBudget_SpreadsAcrossTicks()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
			{
				var sender = new AlwaysResultSender(SendResult.Delivered);
				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender);

				for (var i = 0; i < 3; i++)
					client.Track("user-1", "Big-" + i,
						new JsonObject { { "Payload", new string('x', 200 * 1024) } });

				await client.DrainOnceAsync();
				Assert.AreEqual(1, sender.CallCount, "one tick = one batched request");
				Assert.AreEqual(2, sender.EventCount,
					"the 256KB/tick budget is crossed after the second ~200KB event");
				Assert.AreEqual(1, spool.ApproximateCount);

				await client.DrainOnceAsync();
				Assert.AreEqual(2, sender.CallCount);
				Assert.AreEqual(3, sender.EventCount);
				Assert.AreEqual(0, spool.ApproximateCount);
			}
		}

		// ---- STATISTICS -------------------------------------------------------------------------

		// Strict-mode /import can process a batch while rejecting individual records
		// (failed_records). The batch commits (nothing left to retry), the rejected records count
		// as Failed, and the ingested ones as Succeeded.
		[Test]
		public async Task DrainOnce_BatchProcessedWithSomeRecordsRejected_CountsThemFailedAndRestSucceeded()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
			{
				var sender = new ScriptedSender(new Func<IReadOnlyList<AnalyticsEvent>, BatchSendResult>[]
				{
					batch => new BatchSendResult(SendResult.Delivered, new[] { 1 })
				});
				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender);

				client.Track("user-1", "Good-1", null);
				client.Track("user-1", "Bad", null);
				client.Track("user-1", "Good-2", null);

				await client.DrainOnceAsync();

				Assert.AreEqual(0, spool.ApproximateCount,
					"a processed batch is finished with -- rejected records must not stay spooled");
				Assert.AreEqual(2, client.Statistics.Succeeded);
				Assert.AreEqual(1, client.Statistics.Failed);

				await client.DrainOnceAsync();
				Assert.AreEqual(1, sender.CallCount, "nothing must be re-sent");
			}
		}

		[Test]
		public void Track_IncrementsSubmittedImmediately()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
			{
				var client = new MixpanelClient();
				client.InitializeForTest(spool, new AlwaysResultSender(SendResult.Delivered));

				client.Track("user-1", "A", null);
				client.Track("user-1", "B", null);

				Assert.AreEqual(2, client.Statistics.Submitted);
			}
		}

		// An event dropped later by cap enforcement (not at its own Enqueue call, but a still-older
		// one evicted by a LATER Track/Enqueue once the spool is full) must still be counted as
		// Failed -- otherwise Submitted permanently outruns Succeeded + Failed and never resolves.
		[Test]
		public void Track_EventEvictedByCapEnforcement_IsCountedFailed()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 2)) // maxItems = 2
			{
				var client = new MixpanelClient();
				client.InitializeForTest(spool, new AlwaysResultSender(SendResult.Delivered));

				client.Track("user-1", "Event-0", null); // Will be evicted by Event-2 below.
				client.Track("user-1", "Event-1", null);
				client.Track("user-1", "Event-2", null); // Cap enforcement evicts Event-0 here.

				Assert.AreEqual(2, spool.ApproximateCount, "the oldest event must have been evicted");
				Assert.AreEqual(3, client.Statistics.Submitted);
				Assert.AreEqual(1, client.Statistics.Failed,
					"the cap-evicted event must be counted as Failed even though it was dropped on " +
					"a LATER Track call than its own");
				Assert.AreEqual(client.Statistics.Submitted,
					client.Statistics.Succeeded + client.Statistics.Failed + spool.ApproximateCount,
					"every submitted event must eventually be accounted for as succeeded, failed, or " +
					"still spooled");
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
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
			{
				var sender = new ScriptedSender(new Func<IReadOnlyList<AnalyticsEvent>, BatchSendResult>[]
				{
					_ => BatchSendResult.Retryable,
					_ => BatchSendResult.Delivered
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
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
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
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
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

		[Test]
		public async Task DrainOnce_WithRealCircuitBreakerAndZeroDelay_SustainedRetryableRejection_TripsBreakerAndStopsCallingSender()
		{
			// Regression test for the bug where the Polly pipeline's ShouldHandle predicate only
			// checked SendResult.RetryableFailure, never SendResult.RetryableRejection -- so a
			// sustained run of HTTP 408/429/5xx responses (which DID reach the server, unlike a
			// connectivity failure) was entirely invisible to both in-tick retry and the circuit
			// breaker's failure tracking. The client would keep hammering an already-struggling or
			// rate-limiting server every flush tick with no backoff, exactly what the breaker
			// exists to prevent.
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
			{
				var sender = new AlwaysResultSender(SendResult.RetryableRejection);
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

				Assert.AreEqual(callsAfterWarmup, sender.CallCount,
					"once the circuit breaker is open, the sender must not be called again for a " +
					"sustained RetryableRejection outage either");
				Assert.Greater(spool.ApproximateCount, 0);
			}
		}

		// Breaker recovery: half-open -> closed. Once the circuit breaker has tripped (same
		// single-fully-failing-tick recipe as the regression test above), let its BreakDuration
		// elapse and drive a successful send, proving delivery resumes rather than the breaker
		// staying open forever.
		//
		// This uses the real TimeProvider.System (not a FakeTimeProvider) for the pipeline's clock,
		// exactly like the other real-circuit-breaker tests above, and shrinks BreakDuration itself
		// (via BuildDefaultPipeline's breakDuration parameter) so this test can wait out the break
		// for real, quickly -- rather than driving a FakeTimeProvider through Polly, which is
		// documented (Polly #1932, see offline-analytics.md's "Polly" section) to need extra
		// ceremony (nulling the SynchronizationContext / ConfigureAwait(true)) or retries silently
		// don't fire.
		[Test]
		public async Task DrainOnce_WithRealCircuitBreaker_AfterBreakDurationElapses_HalfOpenTrialSucceedsAndClosesBreaker()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
			{
				var shortBreakDuration = TimeSpan.FromMilliseconds(600);
				var failingSender = new AlwaysResultSender(SendResult.RetryableFailure);
				var pipeline = MixpanelClient.BuildDefaultPipeline(TimeProvider.System,
					retryDelay: TimeSpan.Zero, breakDuration: shortBreakDuration);

				var client = new MixpanelClient();
				// Deliberately NOT overriding batchSize here (unlike the regression test above):
				// tripping only depends on the retry pipeline producing enough outcomes, not on
				// batch size, and leaving it at the default means the half-open trial below gathers
				// BOTH events still queued at that point (the rolled-back "Save" and "StillOpen")
				// into a single batch, matching the ApproximateCount assertions below.
				client.InitializeForTest(spool, failingSender, pipeline: pipeline);

				// Trip the breaker: one fully-failing tick produces 3 outcomes (1 attempt + the
				// default MaxRetryAttempts=2 retries), enough to satisfy MinimumThroughput=3 on its
				// own (see the regression test above for why this is exactly 3, not 4).
				client.Track("user-1", "Save", null);
				await client.DrainOnceAsync();
				var callsWhileTripping = failingSender.CallCount;
				Assert.GreaterOrEqual(callsWhileTripping, 3);

				// While still within BreakDuration, the breaker must still be open: a further drain
				// must not even reach the sender.
				client.Track("user-1", "StillOpen", null);
				await client.DrainOnceAsync();
				Assert.AreEqual(callsWhileTripping, failingSender.CallCount,
					"the breaker must still be open immediately after tripping");

				// Let BreakDuration elapse for real (short on purpose, see shortBreakDuration above,
				// so this test stays fast), then swap in a succeeding sender on the SAME client /
				// spool / pipeline instance -- exactly the pattern the crash-window dedup test above
				// uses to swap senders mid-test -- so the breaker's own state carries over.
				await Task.Delay(shortBreakDuration + TimeSpan.FromMilliseconds(400));

				var succeedingSender = new AlwaysResultSender(SendResult.Delivered);
				client.InitializeForTest(spool, succeedingSender, pipeline: pipeline);

				await client.DrainOnceAsync(); // The half-open trial: gathers BOTH queued events.

				Assert.AreEqual(1, succeedingSender.CallCount,
					"once BreakDuration has elapsed, the half-open trial must reach the sender");
				Assert.AreEqual(0, spool.ApproximateCount,
					"the trial batch (both events still queued: the rolled-back Save and StillOpen) " +
					"succeeded and must be fully delivered");

				// And the breaker should now be fully closed: a further event flows through normally,
				// with no special handling.
				client.Track("user-1", "AfterRecovery", null);
				await client.DrainOnceAsync();

				Assert.AreEqual(2, succeedingSender.CallCount);
				Assert.AreEqual(0, spool.ApproximateCount);
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
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
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

		// Defense-in-depth: Track() itself must no-op while _paused (consent revoked), not just
		// rely on the caller (Analytics.TrackWithApplicationProperties) checking AllowTracking
		// first. Covers the residual race window between a consent revocation observed by
		// PurgeQueuedEvents/PauseTimer and a concurrent Track() call that a caller-side check alone
		// cannot close.
		[Test]
		public void Track_WhileSendingPaused_DoesNotEnqueue()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
			{
				var client = new MixpanelClient();
				client.InitializeForTest(spool, new AlwaysResultSender(SendResult.Delivered));

				client.PurgeQueuedEvents();
				Assert.IsTrue(client.SendingPaused, "test setup: client must be paused before Track is called");

				client.Track("user-1", "ShouldNotEnqueue", null);

				Assert.AreEqual(0, spool.ApproximateCount,
					"Track() must not enqueue while the flush loop is paused (consent revoked)");
				Assert.AreEqual(0, client.Statistics.Submitted,
					"a Track() call while paused must not even count as submitted");
			}
		}

		// fix: consent revocation must not sit blocked behind an in-flight send, and the batch that
		// send was carrying must not survive to be delivered after consent was revoked -- it must
		// roll back and then be purged, not slip through.
		[Test]
		public async Task PurgeQueuedEvents_WhileSendInFlight_CancelsSendReturnsPromptlyAndPurges()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 10))
			{
				var sender = new BlockingUntilCanceledSender();
				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender);

				client.Track("user-1", "Save", null);

				// A real timer-tick-style drain (DrainOnceAsync, usePipeline: true) that will block
				// inside the sender until PurgeQueuedEvents cancels it.
				var drainTask = client.DrainOnceAsync();

				// Wait for the send to actually start before purging, so this exercises the race
				// (purge arriving WHILE a send is in flight) rather than purging before any drain
				// has even started.
				var deadline = DateTime.UtcNow.AddSeconds(5);
				while (sender.CallCount == 0 && DateTime.UtcNow < deadline)
					await Task.Delay(10);
				Assert.AreEqual(1, sender.CallCount, "the send must have started before purging");

				var stopwatch = Stopwatch.StartNew();
				client.PurgeQueuedEvents();
				stopwatch.Stop();

				Assert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(5),
					"Purge must cancel the in-flight send rather than blocking for its full (10s) delay");

				await drainTask;

				Assert.AreEqual(0, spool.ApproximateCount,
					"purge must remove the batch that was in flight when consent was revoked, not " +
					"leave it to be delivered afterward");
			}
		}

		// ---- CONCURRENCY: Track() racing an in-progress drain ------------------------------------

		// Stress/regression coverage for SqliteEventSpool's concurrency safety: while a drain's send
		// is genuinely in flight (blocked, via BlockingUntilSignaledSender, until this test releases
		// it -- never a real Thread.Sleep-style delay), several threads call Track() concurrently.
		// This does not assert a specific interleaving -- only that nothing throws, nothing
		// deadlocks, and the bookkeeping (Submitted == Succeeded + Failed + still-spooled) stays
		// internally consistent once everything settles.
		[Test]
		public async Task ConcurrentTrack_WhileDrainInProgress_NoExceptionsAndConsistentFinalState()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 1000))
			{
				var sender = new BlockingUntilSignaledSender();
				var client = new MixpanelClient();
				client.InitializeForTest(spool, sender, batchSize: 5);

				// Seed one event so the drain below has something to gather and actually calls the
				// (blocking) sender.
				client.Track("user-1", "Seed", null);

				var drainTask = client.DrainOnceAsync(); // Blocks inside the sender until Release().

				// Wait for the send to actually start, so the concurrent Track() calls below
				// genuinely race an in-flight drain (the send is blocked, awaiting Release()) rather
				// than running before the drain even begins.
				var deadline = DateTime.UtcNow.AddSeconds(5);
				while (sender.CallCount == 0 && DateTime.UtcNow < deadline)
					await Task.Delay(10);
				Assert.AreEqual(1, sender.CallCount,
					"the drain's send must have started before the concurrent Track() calls below");

				const int threadCount = 4;
				const int perThread = 25;
				var exceptions = new ConcurrentBag<Exception>();
				var threads = new Thread[threadCount];
				for (var t = 0; t < threadCount; t++)
				{
					var threadIndex = t;
					threads[t] = new Thread(() =>
					{
						try
						{
							for (var i = 0; i < perThread; i++)
								client.Track("user-1", $"T{threadIndex}-{i}", null);
						}
						catch (Exception e)
						{
							exceptions.Add(e);
						}
					});
				}

				foreach (var thread in threads)
					thread.Start();

				// Release the blocked send BEFORE joining: DrainOnceAsync (drainTask) only completes
				// once the sender unblocks, and this test's own cleanup awaits drainTask below, so
				// releasing first (rather than after joining the Track() threads) avoids ever
				// leaving the drain permanently blocked.
				sender.Release();

				foreach (var thread in threads)
					thread.Join();
				await drainTask;

				Assert.IsEmpty(exceptions, "no Track() call racing an in-flight drain should throw");

				var expectedSubmitted = 1 + threadCount * perThread;
				Assert.AreEqual(expectedSubmitted, client.Statistics.Submitted,
					"every Track() call (the seed plus every concurrent one) must be counted submitted");
				Assert.AreEqual(expectedSubmitted,
					client.Statistics.Succeeded + client.Statistics.Failed + spool.ApproximateCount,
					"every submitted event must be accounted for as succeeded, failed, or still spooled " +
					"-- concurrent access must not corrupt that invariant");

				// Drain the rest so the final state is fully resolved.
				while (spool.ApproximateCount > 0)
					await client.DrainOnceAsync();

				Assert.AreEqual(0, spool.ApproximateCount);
				Assert.AreEqual(expectedSubmitted, client.Statistics.Succeeded);
				Assert.AreEqual(0, client.Statistics.Failed);
			}
		}
	}
}
