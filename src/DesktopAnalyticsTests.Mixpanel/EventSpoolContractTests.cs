using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopAnalytics;
using NUnit.Framework;

namespace DesktopAnalyticsTests
{
	/// <summary>
	/// The <see cref="IEventSpool"/> contract, run against the SQLite engine. Kept parametrized
	/// over <typeparamref name="TFactory"/> (rather than collapsed to a concrete fixture) as a
	/// low-risk leftover of when this ran against both DiskQueue- and SQLite-backed engines side
	/// by side to prove they agreed; now there is exactly one <see cref="ISpoolFactory"/>.
	/// </summary>
	[TestFixture(typeof(SqliteSpoolFactory))]
	internal class EventSpoolContractTests<TFactory> where TFactory : ISpoolFactory, new()
	{
		private string _spoolDir;
		private TFactory _factory;

		[SetUp]
		public void SetUp()
		{
			_factory = new TFactory();
			_spoolDir = Path.Combine(Path.GetTempPath(),
				"SpoolContract_" + _factory.Name + "_" + Guid.NewGuid());
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

		private IEventSpool Create(int maxItems, int maxItemBytes = int.MaxValue,
			long maxSpoolBytes = long.MaxValue) =>
			_factory.Create(_spoolDir, maxItems, maxItemBytes, maxSpoolBytes);

		private static AnalyticsEvent MakeEvent(string name) => AnalyticsEvent.Create("user-1", name);

		private static AnalyticsEvent MakeEventAt(string name, DateTimeOffset time) =>
			AnalyticsEvent.Create("user-1", name, time: time);

		private static async Task<List<string>> DrainAllDelivered(IEventSpool spool, int maxItems = 100)
		{
			var names = new List<string>();
			await spool.ProcessBatchAsync(maxItems, (batch, ct) =>
			{
				names.AddRange(batch.Select(e => e.EventName));
				return Task.FromResult(SendResult.Delivered);
			});
			return names;
		}

		// ---- RESTART DURABILITY -----------------------------------------------------------------

		[Test]
		public async Task Enqueue_ThenDisposeAndReopen_EventsSurviveAndDrainFully()
		{
			using (var spool = Create(10))
			{
				Assert.IsTrue(spool.Enqueue(MakeEvent("A")));
				Assert.IsTrue(spool.Enqueue(MakeEvent("B")));
			}

			using (var reopened = Create(10))
			{
				Assert.AreEqual(2, reopened.ApproximateCount, "events must survive a restart");
				CollectionAssert.AreEqual(new[] { "A", "B" }, await DrainAllDelivered(reopened));
				Assert.AreEqual(0, reopened.ApproximateCount);
			}
		}

		// ---- DELIVERY / ACK ---------------------------------------------------------------------

		[Test]
		public async Task ProcessBatch_AllDelivered_EmptiesSpool()
		{
			using (var spool = Create(10))
			{
				spool.Enqueue(MakeEvent("A"));
				spool.Enqueue(MakeEvent("B"));

				CollectionAssert.AreEqual(new[] { "A", "B" }, await DrainAllDelivered(spool));
				Assert.AreEqual(0, spool.ApproximateCount);
			}
		}

		[Test]
		public async Task ProcessBatch_RetryableFailure_RollsWholeBatchBackForLaterRetry()
		{
			using (var spool = Create(10))
			{
				spool.Enqueue(MakeEvent("A"));
				spool.Enqueue(MakeEvent("B"));

				await spool.ProcessBatchAsync(10, (batch, ct) => Task.FromResult(SendResult.RetryableFailure));

				Assert.AreEqual(2, spool.ApproximateCount, "a retryable failure must keep the batch");
				CollectionAssert.AreEqual(new[] { "A", "B" }, await DrainAllDelivered(spool),
					"the rolled-back batch must still be there, in order");
			}
		}

		[Test]
		public async Task ProcessBatch_SendThrows_NothingRemovedAndEventsSurviveReopen()
		{
			using (var spool = Create(10))
			{
				spool.Enqueue(MakeEvent("A"));

				await spool.ProcessBatchAsync(10,
					(batch, ct) => throw new InvalidOperationException("boom"));

				Assert.AreEqual(1, spool.ApproximateCount, "a throwing send must not lose the batch");
			}

			using (var reopened = Create(10))
			{
				Assert.AreEqual(1, reopened.ApproximateCount, "and it must survive a restart");
				CollectionAssert.AreEqual(new[] { "A" }, await DrainAllDelivered(reopened));
			}
		}

		[Test]
		public async Task ProcessBatch_PoisonDrop_RemovesBatchEvenThoughNotDelivered()
		{
			using (var spool = Create(10))
			{
				spool.Enqueue(MakeEvent("A"));

				await spool.ProcessBatchAsync(10, (batch, ct) => Task.FromResult(SendResult.PoisonDrop));

				Assert.AreEqual(0, spool.ApproximateCount,
					"poison must be removed so it cannot wedge the spool");
			}
		}

		[Test]
		public async Task ProcessBatch_MoreEventsThanMaxItems_GathersExactlyMaxItemsPerCall()
		{
			using (var spool = Create(10))
			{
				for (var i = 0; i < 5; i++)
					spool.Enqueue(MakeEvent("Event-" + i));

				var first = await DrainAllDelivered(spool, maxItems: 2);
				CollectionAssert.AreEqual(new[] { "Event-0", "Event-1" }, first);
				Assert.AreEqual(3, spool.ApproximateCount);
			}
		}

		[Test]
		public async Task ProcessBatch_ByteBudget_BoundsBatchButNeverStarves()
		{
			var a = MakeEvent("Event-A");
			// The budget is checked AFTER each gathered event, against the running total already
			// accumulated -- not "would adding the next event exceed it". So the running total must
			// already reach the budget once A is in for B to be excluded; a.Length exactly (not +1)
			// is what bounds the batch to one event.
			var budget = a.ToBytes().Length;

			using (var spool = Create(10))
			{
				spool.Enqueue(a);
				spool.Enqueue(MakeEvent("Event-B"));

				var first = await DrainAllDelivered2(spool, 10, budget);
				CollectionAssert.AreEqual(new[] { "Event-A" }, first,
					"the byte budget must bound the batch");

				var second = await DrainAllDelivered2(spool, 10, budget);
				CollectionAssert.AreEqual(new[] { "Event-B" }, second,
					"the rest must follow on the next drain");
			}
		}

		[Test]
		public async Task ProcessBatch_SingleEventLargerThanWholeByteBudget_StillGoes()
		{
			using (var spool = Create(10))
			{
				spool.Enqueue(MakeEvent("Event-A"));

				var names = await DrainAllDelivered2(spool, 10, maxBytes: 1);
				CollectionAssert.AreEqual(new[] { "Event-A" }, names,
					"the byte budget must never starve the queue");
			}
		}

		private static async Task<List<string>> DrainAllDelivered2(IEventSpool spool, int maxItems,
			long maxBytes)
		{
			var names = new List<string>();
			await spool.ProcessBatchAsync(maxItems, (batch, ct) =>
			{
				names.AddRange(batch.Select(e => e.EventName));
				return Task.FromResult(SendResult.Delivered);
			}, maxBytes);
			return names;
		}

		[Test]
		public async Task ProcessBatch_CancellationToken_IsPassedThroughToSend()
		{
			using (var spool = Create(10))
			{
				spool.Enqueue(MakeEvent("A"));

				var sawToken = false;
				using (var cts = new CancellationTokenSource())
				{
					await spool.ProcessBatchAsync(10, (batch, ct) =>
					{
						sawToken = ct == cts.Token;
						return Task.FromResult(SendResult.Delivered);
					}, long.MaxValue, cts.Token);
				}

				Assert.IsTrue(sawToken, "the caller's token must reach the send callback");
			}
		}

		// ---- ITEM CAP ---------------------------------------------------------------------------

		[Test]
		public async Task Enqueue_ExceedingMaxItems_DropsOldestKeepsNewest()
		{
			using (var spool = Create(3))
			{
				foreach (var name in new[] { "A", "B", "C", "D" })
					spool.Enqueue(MakeEvent(name));

				Assert.AreEqual(3, spool.ApproximateCount);
				CollectionAssert.AreEqual(new[] { "B", "C", "D" }, await DrainAllDelivered(spool),
					"the cap must drop the OLDEST event");
			}
		}

		[Test]
		public void Enqueue_MaxItemsZero_ImmediatelyEvictsTheJustEnqueuedEvent()
		{
			using (var spool = Create(0))
			{
				Assert.IsTrue(spool.Enqueue(MakeEvent("A")),
					"Enqueue reports the write itself succeeded, independent of cap enforcement");
				Assert.AreEqual(0, spool.ApproximateCount,
					"maxItems == 0 must evict even the event that was just enqueued");
			}
		}

		[Test]
		public async Task Enqueue_ExactlyAtMaxItems_NoneDropped_ThenOneOver_DropsOldestStaysAtCap()
		{
			const int maxItems = 5;
			using (var spool = Create(maxItems))
			{
				for (var i = 0; i < maxItems; i++)
					spool.Enqueue(MakeEvent("Event-" + i));

				Assert.AreEqual(maxItems, spool.ApproximateCount,
					"exactly maxItems events must all be retained");

				spool.Enqueue(MakeEvent("OneMore"));
				Assert.AreEqual(maxItems, spool.ApproximateCount, "count must stay pinned at the cap");

				CollectionAssert.AreEqual(
					new[] { "Event-1", "Event-2", "Event-3", "Event-4", "OneMore" },
					await DrainAllDelivered(spool, maxItems + 5));
			}
		}

		[Test]
		public void Enqueue_ExceedingMaxItems_RaisesItemDroppedByCapPerDroppedEvent()
		{
			using (var spool = Create(2))
			{
				var dropped = 0;
				spool.ItemDroppedByCap += () => Interlocked.Increment(ref dropped);

				foreach (var name in new[] { "A", "B", "C", "D" })
					spool.Enqueue(MakeEvent(name));

				Assert.AreEqual(2, dropped,
					"two events over the cap must raise the drop notification twice");
			}
		}

		// ---- BYTE CAP ---------------------------------------------------------------------------

		[Test]
		public async Task Enqueue_ExceedingMaxSpoolBytes_DropsOldestKeepsNewest()
		{
			// A shared, fixed timestamp keeps every event's serialized length identical: Time's
			// fractional-seconds component can otherwise vary by a byte or two between events
			// created microseconds apart, which would make the cap computed below land a few bytes
			// off from the four events' real total and flip which one the byte cap evicts.
			var now = DateTimeOffset.UtcNow;
			var events = new[]
			{
				MakeEventAt("Event-A", now), MakeEventAt("Event-B", now),
				MakeEventAt("Event-C", now), MakeEventAt("Event-D", now)
			};
			long cap = events[0].ToBytes().Length + events[1].ToBytes().Length +
				events[2].ToBytes().Length;

			using (var spool = Create(100, maxSpoolBytes: cap))
			{
				foreach (var evt in events)
					spool.Enqueue(evt);

				CollectionAssert.AreEqual(new[] { "Event-B", "Event-C", "Event-D" },
					await DrainAllDelivered(spool), "the byte cap must drop the OLDEST event");
			}
		}

		[Test]
		public void Enqueue_EventLargerThanMaxItemBytes_IsRefusedWhileNormalEventIsAccepted()
		{
			using (var spool = Create(10, maxItemBytes: 500))
			{
				var huge = AnalyticsEvent.Create("user-1", "Huge", new Segment.Serialization.JsonObject
				{
					{ "blob", new string('x', 2000) }
				});

				Assert.IsFalse(spool.Enqueue(huge), "an event over the per-event cap must be refused");
				Assert.IsTrue(spool.Enqueue(MakeEvent("Normal")), "a normal event must still be accepted");
				Assert.AreEqual(1, spool.ApproximateCount);
			}
		}

		[Test]
		public void ApproximateBytes_TracksEnqueuedPayloadSizeAndSurvivesReopen()
		{
			long expected;
			using (var spool = Create(10))
			{
				var a = MakeEvent("A");
				expected = a.ToBytes().Length;
				spool.Enqueue(a);
				Assert.AreEqual(expected, spool.ApproximateBytes);
			}

			using (var reopened = Create(10))
			{
				Assert.AreEqual(expected, reopened.ApproximateBytes,
					"the byte total the cap is enforced against must survive a restart");
			}
		}

		// ---- AGE RETENTION ----------------------------------------------------------------------

		[Test]
		public void TrimExpired_EventOlderThanMaxAge_IsDropped()
		{
			using (var spool = Create(10))
			{
				var now = DateTimeOffset.UtcNow;
				spool.Enqueue(MakeEventAt("Old", now - TimeSpan.FromDays(90)));

				spool.TrimExpired(TimeSpan.FromDays(60), now);

				Assert.AreEqual(0, spool.ApproximateCount);
			}
		}

		[Test]
		public void TrimExpired_EventOlderThanMaxAge_RaisesItemDroppedByExpiry()
		{
			using (var spool = Create(10))
			{
				var now = DateTimeOffset.UtcNow;
				spool.Enqueue(MakeEventAt("Old1", now - TimeSpan.FromDays(90)));
				spool.Enqueue(MakeEventAt("Old2", now - TimeSpan.FromDays(90)));
				spool.Enqueue(MakeEventAt("Fresh", now - TimeSpan.FromDays(10)));

				var expiredCount = 0;
				spool.ItemDroppedByExpiry += () => Interlocked.Increment(ref expiredCount);

				spool.TrimExpired(TimeSpan.FromDays(60), now);

				Assert.AreEqual(1, spool.ApproximateCount, "only the fresh event should remain");
				Assert.AreEqual(2, expiredCount,
					"ItemDroppedByExpiry must fire once per expired event actually removed");
			}
		}

		[Test]
		public void TrimExpired_EventWithinMaxAge_IsRetained()
		{
			using (var spool = Create(10))
			{
				var now = DateTimeOffset.UtcNow;
				spool.Enqueue(MakeEventAt("Fresh", now - TimeSpan.FromDays(10)));

				spool.TrimExpired(TimeSpan.FromDays(60), now);

				Assert.AreEqual(1, spool.ApproximateCount);
			}
		}

		[Test]
		public async Task TrimExpired_MixOfExpiredAndFreshEvents_DropsExpiredKeepsRestInOrder()
		{
			using (var spool = Create(10))
			{
				var now = DateTimeOffset.UtcNow;
				spool.Enqueue(MakeEventAt("Expired-1", now - TimeSpan.FromDays(90)));
				spool.Enqueue(MakeEventAt("Expired-2", now - TimeSpan.FromDays(70)));
				spool.Enqueue(MakeEventAt("Fresh-1", now - TimeSpan.FromDays(10)));
				spool.Enqueue(MakeEventAt("Fresh-2", now - TimeSpan.FromDays(1)));

				spool.TrimExpired(TimeSpan.FromDays(60), now);

				Assert.AreEqual(2, spool.ApproximateCount);
				CollectionAssert.AreEqual(new[] { "Fresh-1", "Fresh-2" }, await DrainAllDelivered(spool));
			}
		}

		[Test]
		public void TrimExpired_EmptySpool_IsNoOpAndDoesNotThrow()
		{
			using (var spool = Create(10))
			{
				Assert.DoesNotThrow(() => spool.TrimExpired(TimeSpan.FromDays(60), DateTimeOffset.UtcNow));
				Assert.AreEqual(0, spool.ApproximateCount);
			}
		}

		[Test]
		public void TrimExpired_ClockMovedBackward_DropsNothing()
		{
			using (var spool = Create(10))
			{
				var now = DateTimeOffset.UtcNow;
				spool.Enqueue(MakeEventAt("A", now));

				// "now" is earlier than the event's own stamp -- nothing can be older than the cutoff.
				spool.TrimExpired(TimeSpan.FromDays(60), now - TimeSpan.FromDays(30));

				Assert.AreEqual(1, spool.ApproximateCount,
					"a backward clock jump must not evict events");
			}
		}

		// ---- CONSENT PURGE ----------------------------------------------------------------------

		[Test]
		public void Purge_EmptiesNonEmptySpool()
		{
			using (var spool = Create(10))
			{
				spool.Enqueue(MakeEvent("A"));
				spool.Enqueue(MakeEvent("B"));

				spool.Purge();

				Assert.AreEqual(0, spool.ApproximateCount);
				Assert.AreEqual(0, spool.ApproximateBytes);
			}
		}

		[Test]
		public async Task Purge_ThenEnqueue_SpoolRemainsUsableAndPurgedBytesAreGoneFromDisk()
		{
			using (var spool = Create(10))
			{
				spool.Enqueue(MakeEvent("SecretEventName"));
				spool.Purge();

				Assert.IsTrue(spool.Enqueue(MakeEvent("AfterPurge")),
					"the spool must remain usable after a purge");
				CollectionAssert.AreEqual(new[] { "AfterPurge" }, await DrainAllDelivered(spool));
			}

			// The consent guarantee: the purged event's bytes must be gone from disk, not merely
			// marked consumed.
			foreach (var file in Directory.GetFiles(_spoolDir, "*", SearchOption.AllDirectories))
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

				StringAssert.DoesNotContain("SecretEventName", content,
					"purged event data must not survive anywhere on disk in " + file);
			}
		}

		[Test]
		public void Purge_EmptySpool_DoesNotThrow()
		{
			using (var spool = Create(10))
			{
				Assert.DoesNotThrow(() => spool.Purge());
				Assert.AreEqual(0, spool.ApproximateCount);
			}
		}

		[Test]
		public void Purge_ThenImmediateDispose_PurgedDataStillNotOnDisk()
		{
			// Purge() runs its DELETE/VACUUM/checkpoint synchronously (see Purge's doc comment for
			// why an earlier backgrounded version of this was tried and reverted), so by the time it
			// returns the disk-scrub is already complete -- an immediately-following Dispose() (no
			// delay at all, as below) has nothing left to race or skip. This pins that guarantee
			// down as an explicit regression test rather than leaving it as something only true "by
			// construction" of Purge() being synchronous.
			var dir = Path.Combine(Path.GetTempPath(), "SpoolContract_ImmediateDispose_" + Guid.NewGuid());
			try
			{
				using (var spool = _factory.Create(dir, 10, int.MaxValue, long.MaxValue))
				{
					spool.Enqueue(MakeEvent("SecretEventName"));
					spool.Purge();
				}

				foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
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

					StringAssert.DoesNotContain("SecretEventName", content,
						"purged event data must not survive anywhere on disk in " + file +
						" even when Dispose() immediately follows Purge()");
				}
			}
			finally
			{
				try { Directory.Delete(dir, true); } catch { }
			}
		}

		// ---- CORRUPT / UNDESERIALIZABLE PAYLOADS -------------------------------------------------
		//
		// Regression coverage for two related fixes: (1) ProcessBatchAsync's exception-path call to
		// ResolveClaim used to omit undeserializableIds, so a corrupt row claimed alongside events
		// whose send() call threw would never be removed -- it would loop forever, re-claimed and
		// re-failing to deserialize on every subsequent drain. (2) IEventSpool.ItemDroppedByCorruption
		// (consumed by MixpanelClient to keep Statistics.Failed accurate) did not exist at all, so an
		// undeserializable row vanished from the spool with no way for a caller to ever count it.

		[Test]
		public async Task ProcessBatch_AllRowsCorrupt_RemovedWithoutCallingSendAndRaisesItemDroppedByCorruption()
		{
			using (var spool = Create(10))
			{
				spool.Enqueue(MakeEvent("A"));
				SpoolCorruptionTestHelper.CorruptAllPayloads(_spoolDir);

				var corruptedCount = 0;
				spool.ItemDroppedByCorruption += () => Interlocked.Increment(ref corruptedCount);

				var sendCalled = false;
				await spool.ProcessBatchAsync(100, (batch, ct) =>
				{
					sendCalled = true;
					return Task.FromResult(SendResult.Delivered);
				});

				Assert.IsFalse(sendCalled,
					"a batch that is entirely undeserializable must never reach send()");
				Assert.AreEqual(0, spool.ApproximateCount, "the corrupt row must be removed");
				Assert.AreEqual(1, corruptedCount,
					"ItemDroppedByCorruption must fire once for the corrupt row");
			}
		}

		[Test]
		public async Task ProcessBatch_MixedBatchAndSendThrows_CorruptRowStillRemovedAndValidRowReleased()
		{
			using (var spool = Create(10))
			{
				spool.Enqueue(MakeEvent("Good"));
				SpoolCorruptionTestHelper.CorruptOldestPayload(_spoolDir);
				spool.Enqueue(MakeEvent("AlsoGood"));

				var corruptedCount = 0;
				spool.ItemDroppedByCorruption += () => Interlocked.Increment(ref corruptedCount);

				await spool.ProcessBatchAsync(100,
					(batch, ct) => throw new InvalidOperationException("simulated send failure"));

				Assert.AreEqual(1, corruptedCount,
					"the corrupt row must still be removed even though send() threw for the batch");
				Assert.AreEqual(1, spool.ApproximateCount,
					"the corrupt row is gone; the still-valid row remains, released for a later retry");
				CollectionAssert.AreEqual(new[] { "AlsoGood" }, await DrainAllDelivered(spool));
			}
		}

		// ---- ROBUSTNESS -------------------------------------------------------------------------

		[Test]
		public void Enqueue_NullEvent_ReturnsFalseAndDoesNotThrow()
		{
			using (var spool = Create(10))
			{
				Assert.IsFalse(spool.Enqueue(null));
				Assert.AreEqual(0, spool.ApproximateCount);
			}
		}

		[Test]
		public void Enqueue_FromMultipleThreadsConcurrently_AllEventsPersisted()
		{
			const int threadCount = 4;
			const int perThread = 25;

			using (var spool = Create(threadCount * perThread))
			{
				var threads = new Thread[threadCount];
				for (var t = 0; t < threadCount; t++)
				{
					var index = t;
					threads[t] = new Thread(() =>
					{
						for (var i = 0; i < perThread; i++)
							spool.Enqueue(MakeEvent($"T{index}-{i}"));
					});
				}

				foreach (var thread in threads)
					thread.Start();
				foreach (var thread in threads)
					thread.Join();

				Assert.AreEqual(threadCount * perThread, spool.ApproximateCount,
					"concurrent enqueues must not lose events");
			}
		}

		[Test]
		public async Task ProcessBatch_EmptySpool_IsNoOpAndNeverCallsSend()
		{
			using (var spool = Create(10))
			{
				var called = false;
				await spool.ProcessBatchAsync(10, (batch, ct) =>
				{
					called = true;
					return Task.FromResult(SendResult.Delivered);
				});

				Assert.IsFalse(called, "send must not be called with an empty batch");
			}
		}

		[Test]
		public void Dispose_CalledTwice_DoesNotThrow()
		{
			var spool = Create(10);
			spool.Dispose();
			Assert.DoesNotThrow(() => spool.Dispose());
		}
	}

	// ---- ENGINE FACTORIES -----------------------------------------------------------------------

	internal interface ISpoolFactory
	{
		string Name { get; }
		IEventSpool Create(string dir, int maxItems, int maxItemBytes, long maxSpoolBytes);
	}

	internal class SqliteSpoolFactory : ISpoolFactory
	{
		public string Name => "Sqlite";

		public IEventSpool Create(string dir, int maxItems, int maxItemBytes, long maxSpoolBytes) =>
			new SqliteEventSpool(dir, maxItems, maxItemBytes, maxSpoolBytes);
	}
}
