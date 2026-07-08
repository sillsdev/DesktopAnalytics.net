using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DesktopAnalytics;
using NUnit.Framework;

namespace DesktopAnalyticsTests
{
	// NOTE on the "corrupt/partial spool" edge case (power loss mid-write): we do not add a test
	// for this here. Simulating it deterministically would mean hand-crafting a truncated
	// transaction-log file matching DiskQueue's private on-disk format, which is not part of its
	// public contract and would make the test brittle against DiskQueue version upgrades. This
	// case is covered by DiskQueue's own transactional recovery instead: entries are only
	// discoverable after a matching start/end transaction marker pair is written, so a partial
	// write from a crash mid-transaction leaves an incomplete transaction that DiskQueue's own
	// recovery skips on next open, without our code doing anything special. Separately,
	// EventSpool.ProcessBatch guards against an individual entry that deserializes badly (e.g. an
	// unreadable/incompatible schema version) by flushing (dropping) it like a poison message
	// rather than wedging the spool -- see EventSpool.cs.
	[TestFixture]
	public class EventSpoolTests
	{
		private string _spoolDir;

		[SetUp]
		public void SetUp()
		{
			_spoolDir = Path.Combine(Path.GetTempPath(), "EventSpoolTests_" + Guid.NewGuid());
		}

		[TearDown]
		public void TearDown()
		{
			// Any EventSpool opened by a test must already be disposed (releasing DiskQueue's
			// exclusive lock) by the time we get here -- each test disposes its spool(s) via
			// `using` before returning.
			if (Directory.Exists(_spoolDir))
			{
				try
				{
					Directory.Delete(_spoolDir, true);
				}
				catch (Exception e)
				{
					// Best-effort cleanup; don't fail the test run over a leftover temp directory.
					Console.WriteLine("EventSpoolTests.TearDown: failed to delete " + _spoolDir + ": " + e);
				}
			}
		}

		private static AnalyticsEvent MakeEvent(string name)
		{
			return AnalyticsEvent.Create("user-1", name);
		}

		// 1. RESTART DURABILITY: enqueue N, dispose, reopen a NEW spool on the same directory --
		// all N survive, and a subsequent ProcessBatch (all Delivered) empties it.
		[Test]
		public void Enqueue_ThenDisposeAndReopen_EventsSurviveAndDrainFully()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				for (var i = 0; i < 5; i++)
					spool.Enqueue(MakeEvent("Event-" + i));
			} // Disposed here, releasing the exclusive lock.

			using (var reopened = new EventSpool(_spoolDir, 10))
			{
				Assert.AreEqual(5, reopened.ApproximateCount);

				var delivered = new List<string>();
				reopened.ProcessBatch(10, evt =>
				{
					delivered.Add(evt.EventName);
					return SendResult.Delivered;
				});

				Assert.AreEqual(5, delivered.Count);
				Assert.AreEqual(0, reopened.ApproximateCount);
			}
		}

		// 2. ProcessBatch where send returns Delivered for all => queue empty afterward.
		[Test]
		public void ProcessBatch_AllDelivered_EmptiesSpool()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				spool.Enqueue(MakeEvent("A"));
				spool.Enqueue(MakeEvent("B"));

				spool.ProcessBatch(10, evt => SendResult.Delivered);

				Assert.AreEqual(0, spool.ApproximateCount);
			}
		}

		// 3. send returns RetryableFailure on the 2nd item => 1st removed, 2nd and rest remain;
		// a later ProcessBatch delivers them.
		[Test]
		public void ProcessBatch_RetryableFailureOnSecondItem_LeavesSecondAndRestInSpool()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				spool.Enqueue(MakeEvent("A"));
				spool.Enqueue(MakeEvent("B"));
				spool.Enqueue(MakeEvent("C"));

				var firstPassCalls = new List<string>();
				spool.ProcessBatch(10, evt =>
				{
					firstPassCalls.Add(evt.EventName);
					return evt.EventName == "B" ? SendResult.RetryableFailure : SendResult.Delivered;
				});

				// A was delivered and flushed; B failed (not flushed, so processing stopped before C.
				CollectionAssert.AreEqual(new[] { "A", "B" }, firstPassCalls);
				Assert.AreEqual(2, spool.ApproximateCount);

				var secondPassCalls = new List<string>();
				spool.ProcessBatch(10, evt =>
				{
					secondPassCalls.Add(evt.EventName);
					return SendResult.Delivered;
				});

				CollectionAssert.AreEqual(new[] { "B", "C" }, secondPassCalls);
				Assert.AreEqual(0, spool.ApproximateCount);
			}
		}

		// 4. CRASH-WINDOW: send records the event then THROWS => nothing removed; reopening the
		// spool shows the same events still present (at-least-once, no data loss).
		[Test]
		public void ProcessBatch_SendThrows_NothingRemovedAndEventsSurviveReopen()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				spool.Enqueue(MakeEvent("Crash-1"));
				spool.Enqueue(MakeEvent("Crash-2"));

				var recorded = new List<string>();
				Assert.DoesNotThrow(() => spool.ProcessBatch(10, evt =>
				{
					// Simulate a sender that actually delivered the event over the network, then
					// crashed/threw before this method could observe success and flush.
					recorded.Add(evt.EventName);
					throw new InvalidOperationException("simulated crash after send, before ack");
				}));

				Assert.AreEqual(new[] { "Crash-1" }, recorded);
				Assert.AreEqual(2, spool.ApproximateCount);
			}

			using (var reopened = new EventSpool(_spoolDir, 10))
			{
				Assert.AreEqual(2, reopened.ApproximateCount);

				var delivered = new List<string>();
				reopened.ProcessBatch(10, evt =>
				{
					delivered.Add(evt.EventName);
					return SendResult.Delivered;
				});

				CollectionAssert.AreEqual(new[] { "Crash-1", "Crash-2" }, delivered);
			}
		}

		// 5. Poison: send returns PoisonDrop => item removed even though not delivered.
		[Test]
		public void ProcessBatch_PoisonDrop_RemovesItemEvenThoughNotDelivered()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				spool.Enqueue(MakeEvent("Poison"));
				spool.Enqueue(MakeEvent("Good"));

				var delivered = new List<string>();
				spool.ProcessBatch(10, evt =>
				{
					if (evt.EventName == "Poison")
						return SendResult.PoisonDrop;
					delivered.Add(evt.EventName);
					return SendResult.Delivered;
				});

				CollectionAssert.AreEqual(new[] { "Good" }, delivered);
				Assert.AreEqual(0, spool.ApproximateCount);
			}
		}

		// 6. Bounding: maxItems=3, enqueue 5 => count==3, the 3 NEWEST remain.
		[Test]
		public void Enqueue_ExceedingMaxItems_DropsOldestKeepsNewest()
		{
			using (var spool = new EventSpool(_spoolDir, 3))
			{
				for (var i = 0; i < 5; i++)
					spool.Enqueue(MakeEvent("Event-" + i));

				Assert.AreEqual(3, spool.ApproximateCount);

				var remaining = new List<string>();
				spool.ProcessBatch(10, evt =>
				{
					remaining.Add(evt.EventName);
					return SendResult.Delivered;
				});

				CollectionAssert.AreEqual(new[] { "Event-2", "Event-3", "Event-4" }, remaining);
			}
		}

		// 7. Purge empties a non-empty spool.
		[Test]
		public void Purge_EmptiesNonEmptySpool()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				for (var i = 0; i < 5; i++)
					spool.Enqueue(MakeEvent("Event-" + i));

				Assert.AreEqual(5, spool.ApproximateCount);

				spool.Purge();

				Assert.AreEqual(0, spool.ApproximateCount);

				// A drain after Purge should find nothing to send.
				var delivered = new List<string>();
				spool.ProcessBatch(10, evt =>
				{
					delivered.Add(evt.EventName);
					return SendResult.Delivered;
				});
				Assert.AreEqual(0, delivered.Count);
			}
		}

		// 8. Concurrent enqueue from multiple threads (single process): 4 threads x 25 events =>
		// all 100 persisted.
		[Test]
		public void Enqueue_FromMultipleThreadsConcurrently_AllEventsPersisted()
		{
			const int threadCount = 4;
			const int perThread = 25;

			using (var spool = new EventSpool(_spoolDir, threadCount * perThread))
			{
				var tasks = new Task[threadCount];
				for (var t = 0; t < threadCount; t++)
				{
					var threadIndex = t;
					tasks[t] = Task.Run(() =>
					{
						for (var i = 0; i < perThread; i++)
							spool.Enqueue(MakeEvent($"T{threadIndex}-{i}"));
					});
				}

				Task.WaitAll(tasks);

				Assert.AreEqual(threadCount * perThread, spool.ApproximateCount);

				// Confirm every single one is actually retrievable, not just counted.
				var seen = new HashSet<string>();
				spool.ProcessBatch(threadCount * perThread, evt =>
				{
					seen.Add(evt.EventName);
					return SendResult.Delivered;
				});
				Assert.AreEqual(threadCount * perThread, seen.Count);
			}
		}

		// 9. Second EventSpool opened on the SAME directory while the first is still open
		// fails/throws, proving the cross-process exclusive lock (DiskQueue.PersistentQueue.WaitFor
		// with a short internal timeout) does not silently allow shared access.
		[Test]
		public void Constructor_SecondSpoolOnSameDirectoryWhileFirstOpen_Throws()
		{
			using (new EventSpool(_spoolDir, 10))
			{
				// Assert.Catch (unlike Assert.Throws<Exception>, which requires an *exact* type
				// match) accepts any exception assignable to Exception -- we don't want this test
				// coupled to DiskQueue's specific exception type (currently TimeoutException) for
				// a lock-acquisition failure.
				Assert.Catch<Exception>(() =>
				{
					using (new EventSpool(_spoolDir, 10))
					{
					}
				});
			}
		}

	}
}
