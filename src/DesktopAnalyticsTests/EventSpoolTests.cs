using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DesktopAnalytics;
using NUnit.Framework;

namespace DesktopAnalyticsTests
{
	// NOTE on the "corrupt/partial spool" edge case (power loss mid-write): rather than
	// hand-crafting a truncated transaction-log file matching DiskQueue's private on-disk format
	// (which would couple this suite to internals that may change between DiskQueue versions),
	// test 10 below appends format-agnostic garbage bytes to the transaction log and asserts the
	// spool survives -- exercising DiskQueue's transactional recovery (entries are only
	// discoverable after a matching start/end transaction marker pair) without depending on what
	// the markers look like. Separately, EventSpool.ProcessBatch guards against an individual
	// entry that deserializes badly (e.g. an unreadable/incompatible schema version) by flushing
	// (dropping) it like a poison message rather than wedging the spool -- see EventSpool.cs.
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

		// Reads every file under the spool directory that can be opened for shared reading.
		// DiskQueue holds its 'lock' file open exclusively while the spool is open; that file
		// only ever contains a process id, never event data, so skipping unopenable files does
		// not weaken any event-content assertion.
		internal static IEnumerable<KeyValuePair<string, string>> ReadableSpoolFiles(string spoolDir)
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
					continue; // DiskQueue's exclusively-held lock file.
				}

				yield return new KeyValuePair<string, string>(file, content);
			}
		}

		// 1. RESTART DURABILITY: enqueue N, dispose, reopen a NEW spool on the same directory --
		// all N survive, and a subsequent ProcessBatch (all Delivered) empties it.
		[Test]
		public async Task Enqueue_ThenDisposeAndReopen_EventsSurviveAndDrainFully()
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
				await reopened.ProcessBatchAsync(10, evt =>
				{
					delivered.Add(evt.EventName);
					return Task.FromResult(SendResult.Delivered);
				});

				Assert.AreEqual(5, delivered.Count);
				Assert.AreEqual(0, reopened.ApproximateCount);
			}
		}

		// 2. ProcessBatch where send returns Delivered for all => queue empty afterward.
		[Test]
		public async Task ProcessBatch_AllDelivered_EmptiesSpool()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				spool.Enqueue(MakeEvent("A"));
				spool.Enqueue(MakeEvent("B"));

				await spool.ProcessBatchAsync(10, evt => Task.FromResult(SendResult.Delivered));

				Assert.AreEqual(0, spool.ApproximateCount);
			}
		}

		// 3. send returns RetryableFailure on the 2nd item => 1st removed, 2nd and rest remain;
		// a later ProcessBatch delivers them.
		[Test]
		public async Task ProcessBatch_RetryableFailureOnSecondItem_LeavesSecondAndRestInSpool()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				spool.Enqueue(MakeEvent("A"));
				spool.Enqueue(MakeEvent("B"));
				spool.Enqueue(MakeEvent("C"));

				var firstPassCalls = new List<string>();
				await spool.ProcessBatchAsync(10, evt =>
				{
					firstPassCalls.Add(evt.EventName);
					return Task.FromResult(evt.EventName == "B" ? SendResult.RetryableFailure : SendResult.Delivered);
				});

				// A was delivered and flushed; B failed (not flushed, so processing stopped before C.
				CollectionAssert.AreEqual(new[] { "A", "B" }, firstPassCalls);
				Assert.AreEqual(2, spool.ApproximateCount);

				var secondPassCalls = new List<string>();
				await spool.ProcessBatchAsync(10, evt =>
				{
					secondPassCalls.Add(evt.EventName);
					return Task.FromResult(SendResult.Delivered);
				});

				CollectionAssert.AreEqual(new[] { "B", "C" }, secondPassCalls);
				Assert.AreEqual(0, spool.ApproximateCount);
			}
		}

		// 4. CRASH-WINDOW: send records the event then THROWS => nothing removed; reopening the
		// spool shows the same events still present (at-least-once, no data loss).
		[Test]
		public async Task ProcessBatch_SendThrows_NothingRemovedAndEventsSurviveReopen()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				spool.Enqueue(MakeEvent("Crash-1"));
				spool.Enqueue(MakeEvent("Crash-2"));

				var recorded = new List<string>();
				Assert.DoesNotThrowAsync(async () => await spool.ProcessBatchAsync(10, evt =>
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
				await reopened.ProcessBatchAsync(10, evt =>
				{
					delivered.Add(evt.EventName);
					return Task.FromResult(SendResult.Delivered);
				});

				CollectionAssert.AreEqual(new[] { "Crash-1", "Crash-2" }, delivered);
			}
		}

		// 5. Poison: send returns PoisonDrop => item removed even though not delivered.
		[Test]
		public async Task ProcessBatch_PoisonDrop_RemovesItemEvenThoughNotDelivered()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				spool.Enqueue(MakeEvent("Poison"));
				spool.Enqueue(MakeEvent("Good"));

				var delivered = new List<string>();
				await spool.ProcessBatchAsync(10, evt =>
				{
					if (evt.EventName == "Poison")
						return Task.FromResult(SendResult.PoisonDrop);
					delivered.Add(evt.EventName);
					return Task.FromResult(SendResult.Delivered);
				});

				CollectionAssert.AreEqual(new[] { "Good" }, delivered);
				Assert.AreEqual(0, spool.ApproximateCount);
			}
		}

		// 6. Bounding: maxItems=3, enqueue 5 => count==3, the 3 NEWEST remain.
		[Test]
		public async Task Enqueue_ExceedingMaxItems_DropsOldestKeepsNewest()
		{
			using (var spool = new EventSpool(_spoolDir, 3))
			{
				for (var i = 0; i < 5; i++)
					spool.Enqueue(MakeEvent("Event-" + i));

				Assert.AreEqual(3, spool.ApproximateCount);

				var remaining = new List<string>();
				await spool.ProcessBatchAsync(10, evt =>
				{
					remaining.Add(evt.EventName);
					return Task.FromResult(SendResult.Delivered);
				});

				CollectionAssert.AreEqual(new[] { "Event-2", "Event-3", "Event-4" }, remaining);
			}
		}

		// 7. Purge empties a non-empty spool.
		[Test]
		public async Task Purge_EmptiesNonEmptySpool()
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
				await spool.ProcessBatchAsync(10, evt =>
				{
					delivered.Add(evt.EventName);
					return Task.FromResult(SendResult.Delivered);
				});
				Assert.AreEqual(0, delivered.Count);
			}
		}

		// 7b. Purge leaves the spool fully usable: new events can be enqueued in the same
		// session, only they survive a dispose/reopen, and the purged events' bytes are gone
		// from the on-disk files once the spool is closed (the privacy property of a consent
		// purge -- DiskQueue trims consumed entries rather than retaining them).
		[Test]
		public async Task Purge_ThenEnqueue_SpoolRemainsUsableAndPurgedBytesAreGoneFromDisk()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				spool.Enqueue(MakeEvent("PrePurgeSecret"));
				Assert.AreEqual(1, spool.ApproximateCount);

				spool.Purge();
				Assert.AreEqual(0, spool.ApproximateCount);

				spool.Enqueue(MakeEvent("PostPurge"));
				Assert.AreEqual(1, spool.ApproximateCount);
			}

			foreach (var file in ReadableSpoolFiles(_spoolDir))
			{
				StringAssert.DoesNotContain("PrePurgeSecret", file.Value,
					"a consent purge must remove event data from disk, not just mark it consumed: " + file.Key);
			}

			using (var reopened = new EventSpool(_spoolDir, 10))
			{
				var delivered = new List<string>();
				await reopened.ProcessBatchAsync(10, evt =>
				{
					delivered.Add(evt.EventName);
					return Task.FromResult(SendResult.Delivered);
				});
				CollectionAssert.AreEqual(new[] { "PostPurge" }, delivered);
			}
		}

		// 8. Concurrent enqueue from multiple threads (single process): 4 threads x 25 events =>
		// all 100 persisted. Dedicated Threads (not Task.Run) so the intended parallelism is
		// explicit rather than at the mercy of thread-pool scheduling.
		[Test]
		public async Task Enqueue_FromMultipleThreadsConcurrently_AllEventsPersisted()
		{
			const int threadCount = 4;
			const int perThread = 25;

			using (var spool = new EventSpool(_spoolDir, threadCount * perThread))
			{
				var threads = new Thread[threadCount];
				for (var t = 0; t < threadCount; t++)
				{
					var threadIndex = t;
					threads[t] = new Thread(() =>
					{
						for (var i = 0; i < perThread; i++)
							spool.Enqueue(MakeEvent($"T{threadIndex}-{i}"));
					});
				}

				foreach (var thread in threads)
					thread.Start();
				foreach (var thread in threads)
					thread.Join();

				Assert.AreEqual(threadCount * perThread, spool.ApproximateCount);

				// Confirm every single one is actually retrievable, not just counted.
				var seen = new HashSet<string>();
				await spool.ProcessBatchAsync(threadCount * perThread, evt =>
				{
					seen.Add(evt.EventName);
					return Task.FromResult(SendResult.Delivered);
				});
				Assert.AreEqual(threadCount * perThread, seen.Count);
			}
		}

		// 8a. Per-event byte cap: an event whose serialized form exceeds maxItemBytes is refused
		// (Enqueue returns false) and never spooled, while a normal event on the same spool is
		// accepted -- and Enqueue's return value distinguishes the two.
		[Test]
		public void Enqueue_EventLargerThanMaxItemBytes_IsRefusedWhileNormalEventIsAccepted()
		{
			using (var spool = new EventSpool(_spoolDir, 10, maxItemBytes: 500))
			{
				var oversized = AnalyticsEvent.Create("user-1", "Huge",
					new Segment.Serialization.JsonObject
					{
						{ "Stack Trace", new string('x', 2000) }
					});

				Assert.IsFalse(spool.Enqueue(oversized), "an event over the byte cap must be refused");
				Assert.AreEqual(0, spool.ApproximateCount);

				Assert.IsTrue(spool.Enqueue(MakeEvent("Normal")), "a normal event must still be accepted");
				Assert.AreEqual(1, spool.ApproximateCount);
			}
		}

		// 8b. ProcessBatch honors maxItems: with more events spooled than the batch size, exactly
		// maxItems are attempted per call and the rest stay put for the next call.
		[Test]
		public async Task ProcessBatch_MoreEventsThanMaxItems_AttemptsExactlyMaxItemsPerCall()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				for (var i = 0; i < 5; i++)
					spool.Enqueue(MakeEvent("Event-" + i));

				var firstBatch = new List<string>();
				await spool.ProcessBatchAsync(2, evt =>
				{
					firstBatch.Add(evt.EventName);
					return Task.FromResult(SendResult.Delivered);
				});

				CollectionAssert.AreEqual(new[] { "Event-0", "Event-1" }, firstBatch);
				Assert.AreEqual(3, spool.ApproximateCount);

				var secondBatch = new List<string>();
				await spool.ProcessBatchAsync(2, evt =>
				{
					secondBatch.Add(evt.EventName);
					return Task.FromResult(SendResult.Delivered);
				});

				CollectionAssert.AreEqual(new[] { "Event-2", "Event-3" }, secondBatch);
				Assert.AreEqual(1, spool.ApproximateCount);
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

		// 10. CORRUPTION, torn data-file write: garbage appended to a data file (bytes beyond the
		// extents any committed transaction references) must not prevent reopening the spool, and
		// events from committed transactions must still be readable. Appending garbage -- rather
		// than hand-crafting file contents -- keeps this test independent of DiskQueue's private
		// on-disk format (see the NOTE at the top of this fixture).
		[Test]
		public async Task Constructor_GarbageAppendedToDataFile_SpoolReopensAndCommittedEventsSurvive()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				spool.Enqueue(MakeEvent("Committed-1"));
				spool.Enqueue(MakeEvent("Committed-2"));
			} // Disposed: both enqueues are fully committed transactions on disk.

			AppendGarbageTo(FindSpoolFile("data.0"));

			using (var reopened = new EventSpool(_spoolDir, 10))
			{
				var delivered = new List<string>();
				await reopened.ProcessBatchAsync(10, evt =>
				{
					delivered.Add(evt.EventName);
					return Task.FromResult(SendResult.Delivered);
				});
				CollectionAssert.AreEqual(new[] { "Committed-1", "Committed-2" }, delivered);

				// And the recovered spool must still accept new work.
				reopened.Enqueue(MakeEvent("PostRecovery"));
				Assert.AreEqual(1, reopened.ApproximateCount);
			}
		}

		// 10b. CORRUPTION, garbage in the transaction log: DiskQueue deliberately refuses to open
		// a log with unrecognized trailing bytes (it recovers a TRUNCATED log -- the realistic
		// power-loss shape, where entries simply end mid-transaction -- but treats garbage after a
		// committed entry as a conflict). Pin that fail-fast behavior here: the constructor throws
		// rather than silently serving corrupt data. At the client level this is already handled:
		// MixpanelClient.Initialize catches the throw and degrades to a non-durable no-op client
		// rather than crashing the host (see MixpanelClient's class remarks).
		[Test]
		public void Constructor_GarbageAppendedToTransactionLog_FailsFastRatherThanServingCorruptData()
		{
			using (var spool = new EventSpool(_spoolDir, 10))
			{
				spool.Enqueue(MakeEvent("Committed-1"));
			}

			AppendGarbageTo(FindSpoolFile("transaction.log"));

			Assert.Catch<Exception>(() =>
			{
				using (new EventSpool(_spoolDir, 10))
				{
				}
			});
		}

		private string FindSpoolFile(string fileName)
		{
			var path = Path.Combine(_spoolDir, fileName);
			Assert.IsTrue(File.Exists(path),
				"expected DiskQueue file at " + path + " -- if DiskQueue renamed it, update this test");
			return path;
		}

		private static void AppendGarbageTo(string path)
		{
			var garbage = new byte[257];
			new Random(12345).NextBytes(garbage);
			using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
				stream.Write(garbage, 0, garbage.Length);
		}
	}
}
