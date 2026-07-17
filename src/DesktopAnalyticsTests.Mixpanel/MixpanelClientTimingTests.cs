using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesktopAnalytics;
using NUnit.Framework;

namespace DesktopAnalyticsTests
{
	// Phase 6 (offline-analytics-v2-plan.md, "Outstanding review findings"): behavioral/timing
	// tests against a REAL background Timer-driven flush loop (via MixpanelClient.StartTimerForTest),
	// rather than the manual DrainOnceAsync pump every other MixpanelClientTests fixture uses. Both
	// findings here are about WHEN/HOW OFTEN a genuine timer tick runs, which manual pumping cannot
	// exercise at all.
	[TestFixture]
	public class MixpanelClientTimingTests
	{
		private string _spoolDir;

		[SetUp]
		public void SetUp()
		{
			_spoolDir = Path.Combine(Path.GetTempPath(), "MixpanelClientTimingTests_" + Guid.NewGuid());
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
					Console.WriteLine("MixpanelClientTimingTests.TearDown: failed to delete " + _spoolDir + ": " + e);
				}
			}
		}

		// Blocks inside SendBatchAsync until EITHER the test explicitly releases it OR the
		// CancellationToken it was handed is canceled -- unlike MixpanelClientTests'
		// BlockingUntilSignaledSender (which ignores cancellation entirely), this sender needs to
		// distinguish "a real fix canceled me" from "nobody has released or canceled me yet" so a
		// test can assert on WasCanceled as the one signal that is immune to the disposal-ordering
		// race between the canceled call's own continuation and a concurrent Dispose (see the
		// class remarks on the cancellation test below for why WasCanceled -- not lease state or
		// CallCount -- is the only race-proof discriminator).
		private class BlockingUntilSignaledOrCanceledSender : IEventSender
		{
			private readonly TaskCompletionSource<bool> _release =
				new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			public int CallCount;
			public volatile bool WasCanceled;

			public void Release() => _release.TrySetResult(true);

			public async Task<BatchSendResult> SendBatchAsync(IReadOnlyList<AnalyticsEvent> events,
				CancellationToken cancellationToken = default)
			{
				Interlocked.Increment(ref CallCount);

				var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				using (cancellationToken.Register(() => canceled.TrySetResult(true)))
				{
					var completed = await Task.WhenAny(_release.Task, canceled.Task).ConfigureAwait(false);
					if (completed == canceled.Task)
					{
						WasCanceled = true;
						return BatchSendResult.Retryable;
					}
				}

				return new BatchSendResult(SendResult.Delivered);
			}
		}

		// ---- FINDING 1: ShutDownAsync must cancel a genuinely wedged, timer-driven drain --------
		//
		// Regression coverage for offline-analytics-v2-plan.md Phase 6, finding 1: ShutDownAsync used
		// to call StopTimerPermanently() (which only prevents FUTURE ticks) but never
		// CancelInFlightSend() (which PurgeQueuedEvents already does) -- so a normal timer-driven
		// drain genuinely mid-send at the moment ShutDown is called was never aborted; it kept
		// running in the background, racing the spool/sender ShutDown's finally block disposes.
		//
		// IMPORTANT (measured, not assumed -- see the report): the wall-clock Stopwatch bound on
		// ShutDownAsync() below turns out to hold EVEN WITHOUT the fix, because SqliteEventSpool
		// leases a claimed batch for 2 minutes: BoundedDrainAsync's own (separate, freshly bounded)
		// claim attempt simply finds nothing unleased to claim and returns in milliseconds, never
		// blocking on the wedged send at all. So the Stopwatch assertion alone is NOT proof this
		// finding is fixed -- it passes either way. The actual discriminator is WasCanceled: only a
		// real CancelInFlightSend() call causes the WEDGED call's own CancellationToken
		// (_sendCts.Token, captured when the timer tick started) to fire. Polled with a bound
		// (never a fixed sleep) so a still-broken build fails cleanly instead of hanging.
		[Test]
		public async Task ShutDownAsync_TimerDrivenDrainGenuinelyWedgedMidSend_CancelsPromptlyAndShutDownStaysBounded()
		{
			var spool = new SqliteEventSpool(_spoolDir, 10);
			var sender = new BlockingUntilSignaledOrCanceledSender();
			var client = new MixpanelClient();
			client.InitializeForTest(spool, sender, batchSize: 5);

			client.Track("user-1", "Save", null);

			// A short real flush interval so the first real timer tick fires almost immediately
			// (StartTimer's initial delay is min(kInitialFlushDelaySeconds, flushInterval)) and
			// genuinely races ShutDown, rather than requiring this test to wait out the 30s
			// production default.
			client.StartTimerForTest(TimeSpan.FromMilliseconds(100));

			// Wait for the tick's send to actually start (and therefore be genuinely blocked)
			// before calling ShutDown -- otherwise this would race a tick that hasn't begun yet.
			var startDeadline = DateTime.UtcNow.AddSeconds(5);
			while (sender.CallCount == 0 && DateTime.UtcNow < startDeadline)
				await Task.Delay(10);
			Assert.AreEqual(1, sender.CallCount,
				"the real timer tick must have started a send (and be blocked in it) before ShutDown is called");

			try
			{
				var stopwatch = Stopwatch.StartNew();
				await client.ShutDownAsync();
				stopwatch.Stop();

				// Necessary but NOT sufficient (see the remarks above) -- kept because ShutDown must
				// never hang regardless, and to document the measured number.
				Assert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(10),
					"ShutDownAsync() must stay bounded even with a genuinely wedged timer-driven " +
					"drain in the background");
				Console.WriteLine("ShutDownAsync() elapsed while a timer-driven drain was wedged: " +
					stopwatch.Elapsed.TotalMilliseconds + " ms");

				// THE actual proof: the wedged call's own CancellationToken must have fired. Polled
				// (not a fixed delay) because the sender's continuation runs on a thread-pool thread
				// after ShutDownAsync returns; a fixed short sleep would be flaky under load, and if
				// the fix regressed, this must fail promptly rather than hang.
				var cancelDeadline = DateTime.UtcNow.AddSeconds(2);
				while (!sender.WasCanceled && DateTime.UtcNow < cancelDeadline)
					await Task.Delay(20);

				Assert.IsTrue(sender.WasCanceled,
					"ShutDownAsync must call CancelInFlightSend so a genuinely wedged, timer-driven " +
					"drain observes cancellation promptly instead of lingering in the background " +
					"after the spool/sender have been disposed");

				// A racy re-claim (BoundedDrainAsync's own attempt grabbing the row right after the
				// wedged call's lease is released) can make the sender's SECOND call the one that
				// actually reports WasCanceled, so CallCount is asserted loosely here -- what matters
				// is that cancellation was observed by SOME call, not which one.
				Assert.GreaterOrEqual(sender.CallCount, 1);
			}
			finally
			{
				// Release unconditionally so a failed/broken-fix run does not leave a permanently
				// blocked thread-pool task behind.
				sender.Release();
			}

			// No-loss invariant (true whether or not the lease itself happened to clear in time):
			// the event must still be on disk, not silently lost, for the next launch to retry.
			using (var reopened = new SqliteEventSpool(_spoolDir, 10))
			{
				Assert.AreEqual(1, reopened.ApproximateCount,
					"the undelivered event must remain in the spool -- it was never actually " +
					"acknowledged as delivered");
			}
		}

		// ---- FINDING 2: drain pacing must accelerate while a backlog is being cleared -----------
		//
		// Regression coverage for offline-analytics-v2-plan.md Phase 6, finding 2:
		// kMaxBytesPerDrainTick per kDefaultFlushIntervalSeconds is far too slow to clear a real
		// backlog within a typical reconnect window (the plan calculates 50-100 minutes for a full
		// backlog). OnTimerTickAsync must reschedule the NEXT tick after
		// kReconnectFlushDelaySeconds (not the full flushInterval) whenever a tick both leaves a
		// backlog and made genuine forward progress.
		[Test]
		public async Task RealTimerLoop_BacklogAcrossMultipleTicksWithProgress_AcceleratesInsteadOfWaitingFullInterval()
		{
			using (var spool = new SqliteEventSpool(_spoolDir, 1000))
			{
				var sender = new AlwaysDeliveredSender();
				var client = new MixpanelClient();
				const int batchSize = 5;
				const int totalEvents = 25; // Exactly 5 ticks at batchSize=5.
				client.InitializeForTest(spool, sender, batchSize: batchSize);

				for (var i = 0; i < totalEvents; i++)
					client.Track("user-1", "Backlog-" + i, null);
				Assert.AreEqual(totalEvents, spool.ApproximateCount);

				// Deliberately >> kReconnectFlushDelaySeconds (1s): if acceleration is NOT wired up,
				// clearing 5 ticks needs 4 full intervals between them (~3s initial delay + 4 x 5s =
				// ~23s). If it IS wired up, the gaps after the first tick are ~1s each (~3s + 4 x 1s
				// = ~7s). A 12s bound cleanly separates the two.
				var flushInterval = TimeSpan.FromSeconds(5);

				var stopwatch = Stopwatch.StartNew();
				client.StartTimerForTest(flushInterval);

				var deadline = DateTime.UtcNow.AddSeconds(20);
				while (spool.ApproximateCount > 0 && DateTime.UtcNow < deadline)
					await Task.Delay(50);
				stopwatch.Stop();

				Assert.AreEqual(0, spool.ApproximateCount,
					"the backlog did not fully clear within the test's outer deadline");
				Console.WriteLine("Backlog of " + totalEvents + " events (batchSize=" + batchSize +
					", flushInterval=" + flushInterval.TotalSeconds + "s) cleared in " +
					stopwatch.Elapsed.TotalMilliseconds + " ms across " + sender.CallCount + " sender calls");

				Assert.Less(stopwatch.Elapsed, TimeSpan.FromSeconds(12),
					"clearing a multi-tick backlog must be paced by kReconnectFlushDelaySeconds " +
					"(~1s) between ticks once progress is being made, not by waiting the full " +
					flushInterval.TotalSeconds + "s flushInterval between every tick");
				Assert.GreaterOrEqual(sender.CallCount, totalEvents / batchSize,
					"at least " + (totalEvents / batchSize) + " ticks (batches) must have occurred to " +
					"clear " + totalEvents + " events at batchSize=" + batchSize);

				client.ShutDown();
			}
		}

		// A fast, never-blocking, always-Delivered sender -- deliberately separate from
		// MixpanelClientTests' AlwaysResultSender (private to that fixture) and from this file's
		// cancellation-aware sender above, which exists only to test the different (cancellation)
		// behavior.
		private class AlwaysDeliveredSender : IEventSender
		{
			public int CallCount;

			public Task<BatchSendResult> SendBatchAsync(IReadOnlyList<AnalyticsEvent> events,
				CancellationToken cancellationToken = default)
			{
				Interlocked.Increment(ref CallCount);
				return Task.FromResult(new BatchSendResult(SendResult.Delivered));
			}
		}
	}
}
