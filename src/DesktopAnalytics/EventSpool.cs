using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DiskQueue;

namespace DesktopAnalytics
{
	/// <summary>
	/// The outcome of attempting to deliver one spooled event, as reported by the caller of
	/// <see cref="EventSpool.ProcessBatch"/>. This drives whether the event is removed from the
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
		private readonly IPersistentQueue _queue;
		private readonly object _syncRoot = new object();
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
		/// <exception cref="Exception">Propagates whatever DiskQueue throws if the lock cannot be
		/// acquired within the internal wait timeout (e.g. another process/instance already has
		/// this spool open) or if the directory cannot be created/opened. This is deliberately not
		/// swallowed: it is a startup-time failure the caller needs to know about.</exception>
		public EventSpool(string spoolDirectory, int maxItems)
		{
			if (maxItems < 0)
				throw new ArgumentOutOfRangeException(nameof(maxItems));

			_maxItems = maxItems;
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
					lock (_syncRoot)
						return _queue.EstimatedCountOfItemsInQueue;
				}
				catch (Exception e)
				{
					Debug.WriteLine("EventSpool.ApproximateCount: failed to read count: " + e);
					return 0;
				}
			}
		}

		/// <summary>
		/// Serializes and enqueues <paramref name="evt"/>, then enforces the configured cap by
		/// dropping the oldest event(s) if necessary. Never throws: any disk/IO/serialization
		/// failure is logged and swallowed, and the event is simply lost rather than crashing the
		/// host.
		/// </summary>
		public void Enqueue(AnalyticsEvent evt)
		{
			if (evt == null)
				return;

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
					return;
				}

				lock (_syncRoot)
				{
					using (var session = _queue.OpenSession())
					{
						session.Enqueue(bytes);
						session.Flush();
					}

					EnforceCapLocked();
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine("EventSpool.Enqueue failed: " + e);
			}
		}

		// Caller must hold _syncRoot. Drops the oldest item(s) one at a time until the queue is at
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
		public void ProcessBatch(int maxItems, Func<AnalyticsEvent, SendResult> send)
		{
			if (send == null || maxItems <= 0)
				return;

			try
			{
				lock (_syncRoot)
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
								result = send(evt);
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
			}
			catch (Exception e)
			{
				Debug.WriteLine("EventSpool.ProcessBatch failed: " + e);
			}
		}

		/// <summary>
		/// Empties the spool entirely (used on consent revocation). Never throws.
		/// </summary>
		public void Purge()
		{
			try
			{
				lock (_syncRoot)
				{
					using (var session = _queue.OpenSession())
					{
						while (true)
						{
							byte[] item;
							try
							{
								item = session.Dequeue();
							}
							catch (Exception e)
							{
								Debug.WriteLine("EventSpool.Purge: dequeue failed, giving up: " + e);
								return;
							}

							if (item == null)
								return;

							session.Flush();
						}
					}
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
				lock (_syncRoot)
				{
					_disposed = true;
					_queue?.Dispose();
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine("EventSpool.Dispose failed: " + e);
			}
		}
	}
}
