using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiskQueue;

namespace DesktopAnalytics
{
	/// <summary>
	/// The outcome of attempting to deliver one spooled event, as reported by the caller of
	/// <see cref="EventSpool.ProcessBatchAsync"/>. This drives whether the event is removed from the
	/// spool (<see cref="Delivered"/>/<see cref="PoisonDrop"/>) or left in place for a later
	/// retry (<see cref="RetryableFailure"/>).
	/// </summary>
	internal enum SendResult
	{
		/// <summary>The event was confirmed delivered (e.g. HTTP 2xx). Remove it from the spool.</summary>
		Delivered,

		/// <summary>A transient failure (connection error, timeout, 5xx, 429). Leave the event in
		/// the spool for a later retry.</summary>
		RetryableFailure,

		/// <summary>A non-retryable failure (e.g. a 4xx that will never succeed). Remove the event
		/// from the spool anyway so a single bad event cannot wedge the spool forever.</summary>
		PoisonDrop
	}

	/// <summary>
	/// Durable, bounded, on-disk store of pending <see cref="AnalyticsEvent"/>s, backed by
	/// DiskQueue. Events are enqueued as opaque UTF-8 JSON bytes (see
	/// <see cref="AnalyticsEvent.ToBytes"/>) so the spool stays portable and inspectable.
	/// </summary>
	/// <remarks>
	/// Every public method here swallows disk/IO/serialization exceptions internally (logging via
	/// <see cref="Debug.WriteLine(string)"/>) and returns gracefully -- analytics code must never
	/// throw into the host application. The one exception is the constructor: failing to acquire
	/// the underlying DiskQueue's cross-process exclusive lock is a startup-time condition the
	/// caller needs to know about, so it is allowed to propagate.
	/// </remarks>
	internal class EventSpool : IDisposable
	{
		// How long we wait to acquire DiskQueue's cross-process exclusive lock when opening the
		// spool. Kept short: if another process is holding the lock for this long, something is
		// wrong, and we'd rather fail fast than hang application startup/shutdown.
		private static readonly TimeSpan s_lockWaitTimeout = TimeSpan.FromSeconds(2);

		// Number of leading bytes of the SHA-256 hash of the API key used to build the spool
		// directory name. Just needs to be stable and distinguish keys from each other -- it is
		// not a security boundary -- so a short prefix is plenty.
		private const int kApiKeyHashBytes = 8;

		private readonly int _maxItems;
		private readonly int _maxItemBytes;
		private readonly string _spoolDirectory;
		// Not readonly: Purge replaces the queue wholesale (see its remarks). Always accessed
		// under _sync. Null only if a Purge-time reopen failed; every public method already
		// catches and logs whatever follows from that.
		private IPersistentQueue _queue;
		// Serializes ALL access to _queue, exactly like the lock(_syncRoot) it replaced -- a
		// SemaphoreSlim because ProcessBatchAsync must hold it across awaits of the send callback,
		// which a monitor lock cannot. Deliberately never disposed: it holds no unmanaged
		// resources unless AvailableWaitHandle is touched (it isn't), and disposing it would turn
		// a benign use-after-Dispose race into ObjectDisposedException noise.
		private readonly SemaphoreSlim _sync = new SemaphoreSlim(1, 1);
		private bool _disposed;

		/// <summary>
		/// Opens (creating if necessary) the on-disk event spool rooted at
		/// <paramref name="spoolDirectory"/>, taking DiskQueue's cross-process exclusive lock for
		/// the lifetime of this object.
		/// </summary>
		/// <param name="spoolDirectory">Directory to hold the spool's files. Callers normally get
		/// this from <see cref="GetDefaultSpoolPath"/>; tests typically inject a temp directory.</param>
		/// <param name="maxItems">The maximum number of events retained. Enqueuing beyond this
		/// drops the oldest event(s) first.</param>
		/// <param name="maxItemBytes">The maximum serialized size of a single event; anything
		/// larger is refused at enqueue time (<see cref="Enqueue"/> returns false) rather than
		/// spooled. Defaults to unbounded; <see cref="MixpanelClient"/> passes the transport's
		/// real per-event limit.</param>
		/// <exception cref="Exception">Propagates whatever DiskQueue throws if the lock cannot be
		/// acquired within the internal wait timeout (e.g. another process/instance already has
		/// this spool open) or if the directory cannot be created/opened. This is deliberately not
		/// swallowed: it is a startup-time failure the caller needs to know about.</exception>
		public EventSpool(string spoolDirectory, int maxItems, int maxItemBytes = int.MaxValue)
		{
			if (maxItems < 0)
				throw new ArgumentOutOfRangeException(nameof(maxItems));
			if (maxItemBytes <= 0)
				throw new ArgumentOutOfRangeException(nameof(maxItemBytes));

			_maxItems = maxItems;
			_maxItemBytes = maxItemBytes;
			_spoolDirectory = spoolDirectory;
			_queue = PersistentQueue.WaitFor(spoolDirectory, s_lockWaitTimeout);
		}

		/// <summary>
		/// Computes the per-user, per-API-key spool directory:
		/// %LocalAppData%\SIL\DesktopAnalytics\spool\&lt;short hash of apiKey&gt;. The API key is
		/// hashed (never embedded raw) so that, e.g., DEBUG and RELEASE builds of an app -- which
		/// use different keys -- get separate spools, without exposing the key via the file system.
		/// </summary>
		public static string GetDefaultSpoolPath(string apiKey)
		{
			var root = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"SIL", "DesktopAnalytics", "spool");
			return Path.Combine(root, HashApiKey(apiKey));
		}

		private static string HashApiKey(string apiKey)
		{
			using (var sha = SHA256.Create())
			{
				var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(apiKey ?? string.Empty));
				var builder = new StringBuilder(kApiKeyHashBytes * 2);
				for (var i = 0; i < kApiKeyHashBytes; i++)
					builder.Append(hash[i].ToString("x2"));
				return builder.ToString();
			}
		}

		/// <summary>
		/// An approximate count of events currently in the spool. "Approximate" because DiskQueue
		/// tracks this as an estimate that is cheap to read; it is accurate enough for bounding and
		/// diagnostics but should not be treated as a hard guarantee under concurrent access.
		/// </summary>
		public int ApproximateCount
		{
			get
			{
				try
				{
					_sync.Wait();
					try
					{
						return _queue.EstimatedCountOfItemsInQueue;
					}
					finally
					{
						_sync.Release();
					}
				}
				catch (Exception e)
				{
					Debug.WriteLine("EventSpool.ApproximateCount: failed to read count: " + e);
					return 0;
				}
			}
		}

		/// <summary>
		/// Serializes and enqueues <paramref name="evt"/>, then enforces the configured item cap by
		/// dropping the oldest event(s) if necessary. Never throws: any disk/IO/serialization
		/// failure is logged and swallowed, and the event is simply lost rather than crashing the
		/// host.
		/// </summary>
		/// <returns>True if the event was durably enqueued; false if it was dropped (null,
		/// unserializable, larger than the per-event byte cap, or a disk failure). Callers use
		/// this to count the drop -- see <see cref="MixpanelClient.Track"/>.</returns>
		public bool Enqueue(AnalyticsEvent evt)
		{
			if (evt == null)
				return false;

			try
			{
				byte[] bytes;
				try
				{
					bytes = evt.ToBytes();
				}
				catch (Exception e)
				{
					Debug.WriteLine("EventSpool.Enqueue: failed to serialize event, dropping: " + e);
					return false;
				}

				if (bytes.Length > _maxItemBytes)
				{
					// Refused up front rather than spooled: an event over the transport's per-event
					// limit can never be delivered, and if it is large enough to time out the HTTP
					// request it would be classified as a RETRYABLE failure (a timeout is
					// indistinguishable from being offline) and wedge the head of the queue forever.
					Debug.WriteLine("EventSpool.Enqueue: event of " + bytes.Length +
						" bytes exceeds the " + _maxItemBytes + "-byte cap, dropping: " + evt.EventName);
					return false;
				}

				_sync.Wait();
				try
				{
					using (var session = _queue.OpenSession())
					{
						session.Enqueue(bytes);
						session.Flush();
					}

					EnforceCapLocked();
				}
				finally
				{
					_sync.Release();
				}

				return true;
			}
			catch (Exception e)
			{
				Debug.WriteLine("EventSpool.Enqueue failed: " + e);
				return false;
			}
		}

		// Caller must hold _sync. Drops the oldest item(s) one at a time until the queue is at
		// or under _maxItems. Each iteration removes exactly one item (or gives up on a read/dequeue
		// failure), so this always terminates -- including the degenerate case of _maxItems == 0,
		// where even the item just enqueued is dropped.
		private void EnforceCapLocked()
		{
			while (true)
			{
				int count;
				try
				{
					count = _queue.EstimatedCountOfItemsInQueue;
				}
				catch (Exception e)
				{
					Debug.WriteLine("EventSpool.EnforceCap: failed to read count, giving up: " + e);
					return;
				}

				if (count <= _maxItems)
					return;

				try
				{
					using (var session = _queue.OpenSession())
					{
						var oldest = session.Dequeue();
						if (oldest == null)
							return; // Queue is unexpectedly empty; nothing left to drop.
						session.Flush();
					}
				}
				catch (Exception e)
				{
					Debug.WriteLine("EventSpool.EnforceCap: failed to drop oldest item, giving up: " + e);
					return;
				}
			}
		}

		/// <summary>
		/// Attempts to deliver up to <paramref name="maxItems"/> spooled events, oldest first, in
		/// a single DiskQueue session/transaction. For each dequeued event, <paramref name="send"/>
		/// decides its fate:
		/// <list type="bullet">
		/// <item><see cref="SendResult.Delivered"/> or <see cref="SendResult.PoisonDrop"/>: the
		/// dequeue is committed (<c>session.Flush()</c>) and processing continues with the next
		/// event.</item>
		/// <item><see cref="SendResult.RetryableFailure"/>: the dequeue is <em>not</em> committed,
		/// and processing stops -- disposing the session without flushing rolls that event (and
		/// only that event) back into the spool for a later retry.</item>
		/// </list>
		/// If <paramref name="send"/> throws, it is treated exactly like
		/// <see cref="SendResult.RetryableFailure"/>: the exception is swallowed, nothing is
		/// flushed, and processing stops. This is the crash-window path that guarantees
		/// at-least-once delivery -- a sender that delivered the event but crashed/threw before
		/// this method could flush will see the same event again on the next call.
		/// </summary>
		public async Task ProcessBatchAsync(int maxItems, Func<AnalyticsEvent, Task<SendResult>> send)
		{
			if (send == null || maxItems <= 0)
				return;

			try
			{
				await _sync.WaitAsync().ConfigureAwait(false);
				try
				{
					using (var session = _queue.OpenSession())
					{
						for (var i = 0; i < maxItems; i++)
						{
							byte[] bytes;
							try
							{
								bytes = session.Dequeue();
							}
							catch (Exception e)
							{
								Debug.WriteLine("EventSpool.ProcessBatch: dequeue failed: " + e);
								return;
							}

							if (bytes == null)
								return; // Spool is empty.

							AnalyticsEvent evt;
							try
							{
								evt = AnalyticsEvent.FromBytes(bytes);
							}
							catch (Exception e)
							{
								// Unreadable entry (corrupt, or from an incompatible schema version).
								// Treat it like a poison message rather than wedging the spool.
								Debug.WriteLine(
									"EventSpool.ProcessBatch: failed to deserialize event, dropping: " + e);
								session.Flush();
								continue;
							}

							SendResult result;
							try
							{
								result = await send(evt).ConfigureAwait(false);
							}
							catch (Exception e)
							{
								Debug.WriteLine(
									"EventSpool.ProcessBatch: send threw, leaving event for retry: " + e);
								return; // Do not flush -- this dequeue rolls back on session dispose.
							}

							if (result == SendResult.Delivered || result == SendResult.PoisonDrop)
							{
								session.Flush();
								continue;
							}

							// RetryableFailure: stop without flushing so this event rolls back.
							return;
						}
					}
				}
				finally
				{
					_sync.Release();
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine("EventSpool.ProcessBatch failed: " + e);
			}
		}

		/// <summary>
		/// Empties the spool entirely (used on consent revocation), removing the event data from
		/// disk -- not merely marking it consumed. Never throws.
		/// </summary>
		/// <remarks>
		/// Implemented as dispose + delete-the-directory + reopen rather than either alternative,
		/// both tried empirically (2026-07, DiskQueue 1.7.2): a dequeue-and-flush loop leaves the
		/// purged events' bytes sitting in the data files (DiskQueue does not trim consumed
		/// entries), which is not acceptable for a CONSENT purge; and DiskQueue's own
		/// <c>HardDelete(reset: true)</c> leaves the live instance's
		/// <c>EstimatedCountOfItemsInQueue</c> stale, so the spool keeps reporting the purged
		/// events as present -- consistent with its "not thread safe ... or safe in any other
		/// way" warning. Between the dispose and the reopen the cross-process exclusive lock is
		/// briefly released; if another process steals it in that window the reopen fails, this
		/// spool degrades to a no-op (every method here already tolerates that), and the purge
		/// itself has still succeeded -- the data is gone, which is the property that matters.
		/// </remarks>
		public void Purge()
		{
			try
			{
				_sync.Wait();
				try
				{
					if (_disposed)
						return;

					try
					{
						_queue?.Dispose();
					}
					finally
					{
						_queue = null;
					}

					if (Directory.Exists(_spoolDirectory))
						Directory.Delete(_spoolDirectory, true);

					_queue = PersistentQueue.WaitFor(_spoolDirectory, s_lockWaitTimeout);
				}
				finally
				{
					_sync.Release();
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine("EventSpool.Purge failed: " + e);
			}
		}

		/// <summary>
		/// Releases the underlying DiskQueue's cross-process exclusive lock. Safe to call more
		/// than once.
		/// </summary>
		public void Dispose()
		{
			if (_disposed)
				return;

			try
			{
				_sync.Wait();
				try
				{
					_disposed = true;
					_queue?.Dispose();
				}
				finally
				{
					_sync.Release();
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine("EventSpool.Dispose failed: " + e);
			}
		}
	}
}
