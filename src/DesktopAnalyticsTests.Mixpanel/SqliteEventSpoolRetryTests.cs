using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesktopAnalytics;
using NUnit.Framework;

namespace DesktopAnalyticsTests
{
	/// <summary>
	/// The per-event retry-attempt ceiling (offline-analytics-v2-plan.md, Phase 5 / decision D6):
	/// a <see cref="SqliteEventSpool"/> constructor parameter, not part of the generic
	/// <see cref="IEventSpool"/> contract, so it gets its own fixture rather than living in
	/// <see cref="EventSpoolContractTests{TFactory}"/>.
	/// </summary>
	/// <remarks>
	/// Only <see cref="SendResult.RetryableRejection"/> (a genuine "the server told us to try
	/// again later") counts against the ceiling. <see cref="SendResult.RetryableFailure"/> (a
	/// connectivity-level failure -- offline, circuit open, canceled, or send() throwing) must
	/// NEVER erode it, however many times it happens: that distinction is what
	/// <see cref="PlainRetryableFailure_RepeatedManyTimes_NeverIncrementsAttemptsOrExhausts"/>
	/// guards. See the class remarks on <see cref="SendResult"/> for why.
	/// </remarks>
	[TestFixture]
	internal class SqliteEventSpoolRetryTests
	{
		private string _spoolDir;

		[SetUp]
		public void SetUp()
		{
			_spoolDir = Path.Combine(Path.GetTempPath(), "SpoolRetry_" + Guid.NewGuid());
		}

		[TearDown]
		public void TearDown()
		{
			if (!Directory.Exists(_spoolDir))
				return;
			try
			{
				Directory.Delete(_spoolDir, true);
			}
			catch (Exception e)
			{
				Console.WriteLine("TearDown: failed to delete " + _spoolDir + ": " + e);
			}
		}

		private static AnalyticsEvent MakeEvent(string name) => AnalyticsEvent.Create("user-1", name);

		// A genuine per-batch server rejection (HTTP 408/429/5xx in production) -- the ONLY verdict
		// that counts against the retry-attempt ceiling.
		private static Task RejectOnce(IEventSpool spool) =>
			spool.ProcessBatchAsync(10, (batch, ct) => Task.FromResult(SendResult.RetryableRejection));

		// A connectivity-level failure (offline, circuit open, canceled) -- must NEVER count.
		private static Task FailOnce(IEventSpool spool) =>
			spool.ProcessBatchAsync(10, (batch, ct) => Task.FromResult(SendResult.RetryableFailure));

		[Test]
		public async Task RetryableRejection_MaxAttemptsMinusOneTimes_EventStillPresentAndStillOffered()
		{
			const int maxAttempts = 3;
			using (var spool = new SqliteEventSpool(_spoolDir, 10, maxAttempts: maxAttempts))
			{
				spool.Enqueue(MakeEvent("A"));

				// maxAttempts - 1 == 2 rejections: attempts goes 0 -> 1 -> 2, still < maxAttempts (3).
				for (var i = 0; i < maxAttempts - 1; i++)
					await RejectOnce(spool);

				Assert.AreEqual(1, spool.ApproximateCount,
					"after " + (maxAttempts - 1) + " rejections (< maxAttempts = " + maxAttempts +
					"), the event must still be in the spool");

				// And it must still be OFFERED to the next ProcessBatchAsync call (not silently
				// stuck behind a lease or otherwise invisible).
				var offered = false;
				await spool.ProcessBatchAsync(10, (batch, ct) =>
				{
					offered = batch.Count == 1 && batch[0].EventName == "A";
					return Task.FromResult(SendResult.RetryableRejection);
				});
				Assert.IsTrue(offered, "the event must still be offered on the next drain");

				// That drain was itself the maxAttempts-th rejection (3rd), so now it must be gone.
				Assert.AreEqual(0, spool.ApproximateCount,
					"the drain just performed was the Nth (maxAttempts-th) rejection, so the event " +
					"must now be dropped");
			}
		}

		[Test]
		public async Task RetryableRejection_NthTime_EventDroppedAndEventFiresExactlyOnce()
		{
			const int maxAttempts = 3;
			using (var spool = new SqliteEventSpool(_spoolDir, 10, maxAttempts: maxAttempts))
			{
				spool.Enqueue(MakeEvent("A"));

				var exhaustedCount = 0;
				spool.ItemDroppedByRetryExhaustion += () => Interlocked.Increment(ref exhaustedCount);

				// Rejection 1: attempts 0 -> 1
				await RejectOnce(spool);
				Assert.AreEqual(1, spool.ApproximateCount, "rejection 1/3: event must still be present");
				Assert.AreEqual(0, exhaustedCount, "rejection 1/3: must not yet be exhausted");

				// Rejection 2: attempts 1 -> 2
				await RejectOnce(spool);
				Assert.AreEqual(1, spool.ApproximateCount, "rejection 2/3: event must still be present");
				Assert.AreEqual(0, exhaustedCount, "rejection 2/3: must not yet be exhausted");

				// Rejection 3 (== maxAttempts): attempts 2 -> 3 >= maxAttempts (3) -> dropped NOW.
				await RejectOnce(spool);
				Assert.AreEqual(0, spool.ApproximateCount,
					"rejection 3/3 (== maxAttempts): event must be dropped from the spool");
				Assert.AreEqual(1, exhaustedCount,
					"ItemDroppedByRetryExhaustion must fire exactly once for the dropped event");
			}
		}

		[Test]
		public async Task AttemptCounter_SurvivesRestart_DropsAfterReopenOnTheNthRejection()
		{
			const int maxAttempts = 3;

			using (var spool = new SqliteEventSpool(_spoolDir, 10, maxAttempts: maxAttempts))
			{
				spool.Enqueue(MakeEvent("A"));

				// N - 1 == 2 rejections against the first instance.
				for (var i = 0; i < maxAttempts - 1; i++)
					await RejectOnce(spool);

				Assert.AreEqual(1, spool.ApproximateCount,
					"after " + (maxAttempts - 1) + " rejections, still present before restart");
			}

			// Reopen a NEW instance pointed at the same directory -- the counter must not reset.
			using (var reopened = new SqliteEventSpool(_spoolDir, 10, maxAttempts: maxAttempts))
			{
				var exhaustedCount = 0;
				reopened.ItemDroppedByRetryExhaustion += () => Interlocked.Increment(ref exhaustedCount);

				Assert.AreEqual(1, reopened.ApproximateCount,
					"the event itself must survive the restart");

				// One more rejection is the Nth (3rd) overall -- must drop now, proving the counter
				// (2 attempts already recorded) was NOT reset by reopening the database.
				await RejectOnce(reopened);

				Assert.AreEqual(0, reopened.ApproximateCount,
					"the counter must have survived reopening: this 3rd rejection (1st since restart) " +
					"must be enough to drop the event");
				Assert.AreEqual(1, exhaustedCount,
					"ItemDroppedByRetryExhaustion must fire once after the restart-preserved count " +
					"reaches maxAttempts");
			}
		}

		[Test]
		public async Task DeliveredAfterPriorRejections_NeverIncrementsIntoExhaustionAndNeverFiresEvent()
		{
			const int maxAttempts = 3;
			using (var spool = new SqliteEventSpool(_spoolDir, 10, maxAttempts: maxAttempts))
			{
				spool.Enqueue(MakeEvent("A"));

				var exhaustedCount = 0;
				spool.ItemDroppedByRetryExhaustion += () => Interlocked.Increment(ref exhaustedCount);

				// maxAttempts - 2 == 1 rejection: attempts 0 -> 1. One more rejection (attempt 2)
				// would still be short of maxAttempts (3), but the point of this test is that a
				// SUCCESS never increments attempts at all, regardless of how many prior rejections
				// there were.
				for (var i = 0; i < maxAttempts - 2; i++)
					await RejectOnce(spool);
				Assert.AreEqual(1, spool.ApproximateCount, "still present after the prior rejection(s)");

				// Now deliver successfully.
				var delivered = false;
				await spool.ProcessBatchAsync(10, (batch, ct) =>
				{
					delivered = true;
					return Task.FromResult(SendResult.Delivered);
				});

				Assert.IsTrue(delivered, "sanity: the send callback must have been invoked");
				Assert.AreEqual(0, spool.ApproximateCount,
					"a Delivered batch must remove the event (delivered, not retry-exhausted)");
				Assert.AreEqual(0, exhaustedCount,
					"ItemDroppedByRetryExhaustion must NOT fire for a Delivered event, no matter " +
					"how many RetryableRejection verdicts preceded it");
			}
		}

		[Test]
		public async Task PoisonDropAfterPriorRejections_NeverIncrementsIntoExhaustionAndNeverFiresEvent()
		{
			const int maxAttempts = 3;
			using (var spool = new SqliteEventSpool(_spoolDir, 10, maxAttempts: maxAttempts))
			{
				spool.Enqueue(MakeEvent("A"));

				var exhaustedCount = 0;
				spool.ItemDroppedByRetryExhaustion += () => Interlocked.Increment(ref exhaustedCount);

				await RejectOnce(spool); // attempts 0 -> 1

				await spool.ProcessBatchAsync(10, (batch, ct) => Task.FromResult(SendResult.PoisonDrop));

				Assert.AreEqual(0, spool.ApproximateCount, "PoisonDrop must remove the event");
				Assert.AreEqual(0, exhaustedCount,
					"a PoisonDrop removal must never be mistaken for retry exhaustion");
			}
		}

		[Test]
		public async Task MultipleEventsInOneBatch_EachOwnAttemptCounterTrackedIndependently()
		{
			const int maxAttempts = 3;
			using (var spool = new SqliteEventSpool(_spoolDir, 10, maxAttempts: maxAttempts))
			{
				// A gets 2 solo rejections first (attempts: A=2, B=0), then A and B are rejected
				// together once more (attempts: A=3 -> dropped, B=1 -> kept), proving each event's
				// counter is independent rather than one shared counter for the whole batch.
				spool.Enqueue(MakeEvent("A"));
				await spool.ProcessBatchAsync(10, (batch, ct) =>
				{
					Assert.AreEqual(1, batch.Count);
					Assert.AreEqual("A", batch[0].EventName);
					return Task.FromResult(SendResult.RetryableRejection);
				});
				await spool.ProcessBatchAsync(10, (batch, ct) =>
				{
					Assert.AreEqual(1, batch.Count);
					Assert.AreEqual("A", batch[0].EventName);
					return Task.FromResult(SendResult.RetryableRejection);
				});

				spool.Enqueue(MakeEvent("B"));
				Assert.AreEqual(2, spool.ApproximateCount, "A (attempts=2) and B (attempts=0) present");

				var exhaustedCount = 0;
				spool.ItemDroppedByRetryExhaustion += () => Interlocked.Increment(ref exhaustedCount);

				// One shared batch containing BOTH A and B, both rejected together.
				var namesSeen = new System.Collections.Generic.List<string>();
				await spool.ProcessBatchAsync(10, (batch, ct) =>
				{
					foreach (var e in batch)
						namesSeen.Add(e.EventName);
					return Task.FromResult(SendResult.RetryableRejection);
				});
				CollectionAssert.AreEquivalent(new[] { "A", "B" }, namesSeen,
					"sanity: both events were claimed together in the same batch");

				// A: attempts 2 -> 3 (== maxAttempts) -> dropped, fires the event once.
				// B: attempts 0 -> 1 (< maxAttempts) -> kept, no event.
				Assert.AreEqual(1, spool.ApproximateCount,
					"only A should be dropped; B (attempts=1) must remain");
				Assert.AreEqual(1, exhaustedCount,
					"exactly one drop (A) despite both events sharing the same rejected batch");

				var remaining = new System.Collections.Generic.List<string>();
				await spool.ProcessBatchAsync(10, (batch, ct) =>
				{
					foreach (var e in batch)
						remaining.Add(e.EventName);
					return Task.FromResult(SendResult.Delivered);
				});
				CollectionAssert.AreEqual(new[] { "B" }, remaining,
					"the surviving event must be B, not A");
			}
		}

		// ---- CONNECTIVITY-STYLE FAILURE MUST NEVER ERODE THE BUDGET ------------------------------
		//
		// Regression coverage for the gap found wiring this feature into MixpanelClient: a naive
		// "increment attempts on every retryable verdict" implementation would also drop events
		// during plain offline stretches (SendResult.RetryableFailure), not just on a genuine
		// per-event server rejection (SendResult.RetryableRejection). This is the pure-spool-level
		// counterpart to MixpanelClientTests
		// .DrainRepeatedly_SenderAlwaysPlainRetryableFailure_NeverErodesAttemptBudgetNoMatterHowManyTimes.

		[Test]
		public async Task PlainRetryableFailure_RepeatedManyTimes_NeverIncrementsAttemptsOrExhausts()
		{
			const int maxAttempts = 3;
			using (var spool = new SqliteEventSpool(_spoolDir, 10, maxAttempts: maxAttempts))
			{
				spool.Enqueue(MakeEvent("A"));

				var exhaustedCount = 0;
				spool.ItemDroppedByRetryExhaustion += () => Interlocked.Increment(ref exhaustedCount);

				// Far more than maxAttempts (3) -- if RetryableFailure incremented attempts the way
				// RetryableRejection does, this would have dropped the event long ago.
				const int failures = 50;
				for (var i = 0; i < failures; i++)
					await FailOnce(spool);

				Assert.AreEqual(1, spool.ApproximateCount,
					failures + " plain connectivity failures must NOT erode the retry-attempt " +
					"budget -- the event must still be spooled");
				Assert.AreEqual(0, exhaustedCount,
					"ItemDroppedByRetryExhaustion must never fire for connectivity-style failures");

				// And it must still be fully usable afterwards -- e.g. still deliverable.
				var delivered = false;
				await spool.ProcessBatchAsync(10, (batch, ct) =>
				{
					delivered = batch.Count == 1 && batch[0].EventName == "A";
					return Task.FromResult(SendResult.Delivered);
				});
				Assert.IsTrue(delivered, "the event must still be present and deliverable afterwards");
				Assert.AreEqual(0, spool.ApproximateCount);
			}
		}
	}
}
