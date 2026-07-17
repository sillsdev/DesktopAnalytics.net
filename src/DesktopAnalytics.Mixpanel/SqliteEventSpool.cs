using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DesktopAnalytics
{
	/// <summary>
	/// SQLite-backed <see cref="IEventSpool"/>. Same contract as the (now removed) DiskQueue-backed
	/// <c>EventSpool</c>, but the storage model removes -- rather than works around -- the two
	/// structural problems that one had.
	/// </summary>
	/// <remarks>
	/// <para><b>Why this exists.</b> As originally written, <c>EventSpool</c> held its global
	/// <c>_sync</c> semaphore across the network send, so every concurrent <c>Track()</c> waited out
	/// the whole send -- measured at ~3s behind a 3s send, and up to ~45s in production against a
	/// stalled connection (15s HttpClient timeout x 3 pipeline attempts). That was a UI freeze on
	/// exactly the "bad cafe Wi-Fi" path this whole feature is for.</para>
	/// <para><b>Note:</b> that stall was not inherent to DiskQueue. Probed 2026-07-16: DiskQueue
	/// happily enqueues (~8ms) while an uncommitted dequeue session is open, and a second session
	/// dequeues a DISTINCT item rather than blocking or double-serving. So dropping
	/// <c>EventSpool</c>'s global lock would have fixed the stall without changing engines. The
	/// case for SQLite rests on the OTHER problems below, not on the stall alone.</para>
	/// <para>SQLite needs no such thing. <see cref="ProcessBatchAsync"/> claims a batch under a
	/// short lock, <b>releases the lock</b>, sends with nothing held, then removes the delivered
	/// rows under a second short lock. At-least-once falls out for free: a crash between the send
	/// and the removal replays the batch, which Mixpanel deduplicates on
	/// <see cref="AnalyticsEvent.InsertId"/> -- the semantics the design already depends on.
	/// Because the lock is never held across an await, a plain <c>lock</c> suffices here where
	/// <c>EventSpool</c> needed a <c>SemaphoreSlim</c>.</para>
	/// <para><b>What it deletes.</b> The byte-total sidecar file, its staleness heuristic, and its
	/// re-measure fallback all collapse into <c>SUM(len)</c>. Age retention collapses into one
	/// indexed <c>DELETE</c>. Cap eviction collapses into one <c>DELETE</c>. Purge becomes
	/// <c>DELETE</c> + <c>VACUUM</c> instead of dispose + delete-the-directory + reopen.</para>
	/// <para><b>Multi-process.</b> WAL gives real concurrent readers/writers, so a second
	/// FieldWorks process spools and drains normally. The old DiskQueue-backed spool could not even
	/// open (exclusive lock) and silently degraded to a no-op, losing its events entirely. A short
	/// lease on claimed rows keeps two processes from uploading the same batch; the lease expires
	/// on its own, so a process that dies mid-send does not strand its batch.</para>
	/// </remarks>
	internal class SqliteEventSpool : IEventSpool
	{
		// How long a claimed batch stays invisible to other drains. Long enough to cover a slow
		// send, short enough that a crashed process's batch is retried promptly.
		private static readonly TimeSpan s_leaseDuration = TimeSpan.FromMinutes(2);

		// How long SQLite waits on a lock held by another process before returning SQLITE_BUSY.
		private const int kBusyTimeoutMs = 2000;

		private readonly int _maxItems;
		private readonly int _maxItemBytes;
		private readonly long _maxSpoolBytes;
		private readonly int _maxAttempts;
		private readonly string _spoolDirectory;
		private readonly string _databasePath;
		private readonly TimeProvider _timeProvider;

		// Guards _connection. Only ever held for short, purely-local database work -- NEVER across
		// the network send (see the class remarks). A monitor lock is therefore sufficient.
		private readonly object _sync = new object();
		private SqliteConnection _connection;
		private bool _disposed;

		/// <inheritdoc/>
		public event Action ItemDroppedByCap;

		/// <inheritdoc/>
		public event Action ItemDroppedByRetryExhaustion;

		/// <inheritdoc/>
		public event Action ItemDroppedByCorruption;

		/// <inheritdoc/>
		public event Action ItemDroppedByExpiry;

		/// <param name="spoolDirectory">Directory to hold the spool database. Callers normally get
		/// this from <see cref="GetDefaultSpoolPath"/>; tests inject a temp directory.</param>
		/// <param name="maxItems">Maximum number of events retained; enqueuing beyond this drops
		/// the oldest first.</param>
		/// <param name="maxItemBytes">Maximum serialized size of a single event; larger events are
		/// refused at enqueue rather than spooled.</param>
		/// <param name="maxSpoolBytes">Maximum total serialized size of all retained events;
		/// enqueuing beyond this drops the oldest first.</param>
		/// <param name="timeProvider">Clock for lease expiry. Injected so tests stay deterministic.</param>
		/// <param name="maxAttempts">Maximum number of <see cref="SendResult.RetryableRejection"/>
		/// verdicts a single event may accumulate before it is permanently dropped (see
		/// offline-analytics-v2-plan.md, Phase 5 / decision D6) and
		/// <see cref="ItemDroppedByRetryExhaustion"/> is raised for it. Bounds the case where a
		/// single poison event -- one that always fails but never classifies as
		/// <see cref="SendResult.PoisonDrop"/> -- would otherwise wedge the head of the queue
		/// forever.</param>
		/// <exception cref="Exception">Propagates a failure to create/open the database. This is
		/// deliberately not swallowed: it is a startup-time condition the caller needs to know
		/// about.</exception>
		public SqliteEventSpool(string spoolDirectory, int maxItems, int maxItemBytes = int.MaxValue,
			long maxSpoolBytes = long.MaxValue, TimeProvider timeProvider = null, int maxAttempts = 10)
		{
			if (maxItems < 0)
				throw new ArgumentOutOfRangeException(nameof(maxItems));
			if (maxItemBytes <= 0)
				throw new ArgumentOutOfRangeException(nameof(maxItemBytes));
			if (maxSpoolBytes <= 0)
				throw new ArgumentOutOfRangeException(nameof(maxSpoolBytes));
			if (maxAttempts < 0)
				throw new ArgumentOutOfRangeException(nameof(maxAttempts));

			_maxItems = maxItems;
			_maxItemBytes = maxItemBytes;
			_maxSpoolBytes = maxSpoolBytes;
			_maxAttempts = maxAttempts;
			_spoolDirectory = spoolDirectory;
			_databasePath = Path.Combine(spoolDirectory, "spool.db");
			_timeProvider = timeProvider ?? TimeProvider.System;

			Directory.CreateDirectory(spoolDirectory);
			_connection = OpenConnection(_databasePath);
			InitializeSchema(_connection);
		}

		private static SqliteConnection OpenConnection(string databasePath)
		{
			// Pooling off: this class holds its single connection open for its whole lifetime, so
			// pooling buys nothing -- and it would keep the file handle alive after Dispose,
			// blocking Purge's VACUUM and the temp-directory cleanup in tests.
			var connectionString = new SqliteConnectionStringBuilder
			{
				DataSource = databasePath,
				Pooling = false
			}.ToString();

			var connection = new SqliteConnection(connectionString);
			connection.Open();

			// WAL: concurrent readers and a writer, across processes -- the property DiskQueue's
			// exclusive lock cannot give us.
			// synchronous=NORMAL: no fsync per commit. A power loss (not a process crash -- WAL
			// still recovers those) can lose the last few events. That is the right trade for
			// analytics: the alternative fsyncs on every Track(), on the UI thread.
			Execute(connection, "PRAGMA journal_mode=WAL;");
			Execute(connection, "PRAGMA synchronous=NORMAL;");
			Execute(connection, "PRAGMA busy_timeout=" + kBusyTimeoutMs + ";");
			return connection;
		}

		private static void InitializeSchema(SqliteConnection connection)
		{
			Execute(connection,
				// len is stored rather than computed so the cap queries below are index-only scans
				// and never have to read the payload blobs.
				"CREATE TABLE IF NOT EXISTS events (" +
				"  id INTEGER PRIMARY KEY AUTOINCREMENT," +
				"  time_unix_ms INTEGER NOT NULL," +
				"  lease_until_unix_ms INTEGER NOT NULL DEFAULT 0," +
				"  len INTEGER NOT NULL," +
				"  payload BLOB NOT NULL," +
				"  attempts INTEGER NOT NULL DEFAULT 0);");
			Execute(connection, "CREATE INDEX IF NOT EXISTS ix_events_time ON events(time_unix_ms);");
			Execute(connection,
				"CREATE INDEX IF NOT EXISTS ix_events_lease ON events(lease_until_unix_ms, id);");
		}

		private static void Execute(SqliteConnection connection, string sql)
		{
			using (var cmd = connection.CreateCommand())
			{
				cmd.CommandText = sql;
				cmd.ExecuteNonQuery();
			}
		}

		// Number of leading bytes of the SHA-256 hash of the API key used to build the spool
		// directory name. Just needs to be stable and distinguish keys from each other -- it is
		// not a security boundary -- so a short prefix is plenty.
		private const int kApiKeyHashBytes = 8;

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

		/// <inheritdoc/>
		public int ApproximateCount
		{
			get
			{
				try
				{
					lock (_sync)
					{
						if (_disposed || _connection == null)
							return 0;
						return (int)ScalarLocked("SELECT COUNT(*) FROM events;");
					}
				}
				catch (Exception e)
				{
					Debug.WriteLine("SqliteEventSpool.ApproximateCount failed: " + e);
					return 0;
				}
			}
		}

		/// <inheritdoc/>
		public long ApproximateBytes
		{
			get
			{
				try
				{
					lock (_sync)
					{
						if (_disposed || _connection == null)
							return 0;
						return ScalarLocked("SELECT COALESCE(SUM(len), 0) FROM events;");
					}
				}
				catch (Exception e)
				{
					Debug.WriteLine("SqliteEventSpool.ApproximateBytes failed: " + e);
					return 0;
				}
			}
		}

		// Caller must hold _sync.
		private long ScalarLocked(string sql)
		{
			using (var cmd = _connection.CreateCommand())
			{
				cmd.CommandText = sql;
				return Convert.ToInt64(cmd.ExecuteScalar());
			}
		}

		/// <inheritdoc/>
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
					Debug.WriteLine("SqliteEventSpool.Enqueue: failed to serialize event, dropping: " + e);
					return false;
				}

				if (bytes.Length > _maxItemBytes)
				{
					// Refused up front rather than spooled: an event over the transport's per-event
					// limit can never be delivered, and one large enough to time out the request
					// would classify as RETRYABLE (indistinguishable from being offline) and wedge
					// the head of the queue forever.
					Debug.WriteLine("SqliteEventSpool.Enqueue: event of " + bytes.Length +
						" bytes exceeds the " + _maxItemBytes + "-byte cap, dropping: " + evt.EventName);
					return false;
				}

				int dropped;
				lock (_sync)
				{
					if (_disposed || _connection == null)
						return false;

					using (var cmd = _connection.CreateCommand())
					{
						cmd.CommandText =
							"INSERT INTO events (time_unix_ms, lease_until_unix_ms, len, payload) " +
							"VALUES (@time, 0, @len, @payload);";
						cmd.Parameters.AddWithValue("@time", evt.Time.ToUnixTimeMilliseconds());
						cmd.Parameters.AddWithValue("@len", bytes.Length);
						cmd.Parameters.AddWithValue("@payload", bytes);
						cmd.ExecuteNonQuery();
					}

					dropped = EnforceCapsLocked();
				}

				// Raised outside the lock: handlers are the caller's code, and nothing here needs
				// the lock held while they run.
				RaiseDropped(dropped);
				return true;
			}
			catch (Exception e)
			{
				Debug.WriteLine("SqliteEventSpool.Enqueue failed: " + e);
				return false;
			}
		}

		/// <inheritdoc/>
		public Task<bool> EnqueueAsync(AnalyticsEvent evt) => Task.FromResult(Enqueue(evt));

		private void RaiseDropped(int count) => RaiseDropEvent(ItemDroppedByCap, count, nameof(ItemDroppedByCap));

		private void RaiseRetryExhausted(int count) =>
			RaiseDropEvent(ItemDroppedByRetryExhaustion, count, nameof(ItemDroppedByRetryExhaustion));

		private void RaiseCorrupted(int count) =>
			RaiseDropEvent(ItemDroppedByCorruption, count, nameof(ItemDroppedByCorruption));

		private void RaiseExpired(int count) =>
			RaiseDropEvent(ItemDroppedByExpiry, count, nameof(ItemDroppedByExpiry));

		// Shared by all four "permanently dropped" events above: from inside the declaring class,
		// an event field reads like a plain delegate field, so one handler-agnostic helper can raise
		// any of them count times, each invocation independently guarded so one misbehaving
		// subscriber can't stop the rest from being counted.
		private static void RaiseDropEvent(Action handler, int count, string eventName)
		{
			for (var i = 0; i < count; i++)
			{
				try
				{
					handler?.Invoke();
				}
				catch (Exception e)
				{
					Debug.WriteLine("SqliteEventSpool: " + eventName + " handler threw: " + e);
				}
			}
		}

		// Caller must hold _sync. Brings the spool back within BOTH caps, dropping oldest first,
		// and returns how many events were dropped. One DELETE, no loop -- contrast the old
		// DiskQueue-backed EventSpool.EnforceCapLocked, which dequeued one item at a time.
		private int EnforceCapsLocked()
		{
			try
			{
				using (var cmd = _connection.CreateCommand())
				{
					// Both caps evaluated in a single pass over the table (one window-function scan
					// instead of a separate COUNT(*) for the item cap plus a separate SUM(len) scan
					// for the byte cap -- this runs on every Enqueue, so halving the per-call scan
					// cost matters). rn > @maxItems: this row is not among the newest maxItems rows
					// (also covers the degenerate maxItems == 0 case, where the event just enqueued
					// is itself evicted). running > @maxBytes: including this row (and everything
					// newer) would push the running total, counted newest-first, past the byte cap.
					cmd.CommandText = _maxSpoolBytes < long.MaxValue
						? "DELETE FROM events WHERE id IN (" +
						  "  SELECT id FROM (" +
						  "    SELECT id, ROW_NUMBER() OVER (ORDER BY id DESC) AS rn," +
						  "           SUM(len) OVER (ORDER BY id DESC) AS running FROM events" +
						  "  ) WHERE rn > @maxItems OR running > @maxBytes);"
						: "DELETE FROM events WHERE id IN (" +
						  "  SELECT id FROM (" +
						  "    SELECT id, ROW_NUMBER() OVER (ORDER BY id DESC) AS rn FROM events" +
						  "  ) WHERE rn > @maxItems);";
					cmd.Parameters.AddWithValue("@maxItems", _maxItems);
					if (_maxSpoolBytes < long.MaxValue)
						cmd.Parameters.AddWithValue("@maxBytes", _maxSpoolBytes);
					return cmd.ExecuteNonQuery();
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine("SqliteEventSpool.EnforceCaps failed: " + e);
				return 0;
			}
		}

		/// <inheritdoc/>
		/// <remarks>
		/// The lock is taken twice -- once to claim, once to resolve -- and is NOT held across
		/// <paramref name="send"/>. See the class remarks for why that is the entire point of this
		/// implementation.
		/// </remarks>
		public async Task ProcessBatchAsync(int maxItems,
			Func<IReadOnlyList<AnalyticsEvent>, CancellationToken, Task<SendResult>> send,
			long maxBytes = long.MaxValue, CancellationToken cancellationToken = default)
		{
			if (send == null || maxItems <= 0 || maxBytes <= 0)
				return;

			try
			{
				var claim = ClaimBatch(maxItems, maxBytes);
				if (claim.Ids.Count == 0)
					return;

				if (claim.Events.Count == 0)
				{
					// Everything claimed was undeserializable (corrupt, or an incompatible schema
					// version). Nothing to send; just remove them so they cannot wedge the spool.
					// RaiseCorrupted uses ResolveClaim's own return value (how many undeserializable
					// rows THIS call actually deleted), not claim.UndeserializableIds.Count, so a row
					// a concurrent Purge() already removed (Purge's DELETE races ClaimBatch/ResolveClaim
					// the same way a send does -- see the class remarks) or a failed transaction
					// (ResolveClaim returns 0 on any exception) is never double- or over-counted.
					RaiseCorrupted(ResolveClaim(claim.Ids, ClaimOutcome.Remove,
						undeserializableIds: claim.UndeserializableIds));
					return;
				}

				SendResult result;
				try
				{
					// NO LOCK HELD HERE. Track() stays responsive for the whole round trip.
					result = await send(claim.Events, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception e)
				{
					// A throwing send is a connectivity-style failure (we don't know anything about
					// this batch's deliverability), exactly like SendResult.RetryableFailure -- see
					// ResolveOutcome. Must NOT count against the retry-attempt ceiling. Undeserializable
					// rows in the same claim are still removed regardless (see ResolveClaim) -- passed
					// through here the same as the non-throwing path below, so a corrupt row cannot
					// loop forever just because the batch alongside it happened to throw.
					Debug.WriteLine("SqliteEventSpool.ProcessBatch: send threw, leaving batch for retry: " + e);
					RaiseCorrupted(ResolveClaim(claim.Ids, ClaimOutcome.ReleaseOnly,
						undeserializableIds: claim.UndeserializableIds));
					return;
				}

				RaiseCorrupted(ResolveClaim(claim.Ids, ResolveOutcome(result),
					undeserializableIds: claim.UndeserializableIds));
			}
			catch (Exception e)
			{
				Debug.WriteLine("SqliteEventSpool.ProcessBatch failed: " + e);
			}
		}

		// What ResolveClaim should do with a resolved (non-empty-events) batch. Distinct from
		// SendResult because Delivered/PoisonDrop collapse to the same DELETE, and because the two
		// "leave it spooled" verdicts differ in whether they touch the attempts counter -- see
		// each member's doc and SendResult.RetryableFailure/RetryableRejection.
		private enum ClaimOutcome
		{
			// Delivered or PoisonDrop (or every claimed row was undeserializable): the batch is
			// finished with either way. Straight DELETE; attempts is never touched.
			Remove,

			// SendResult.RetryableFailure, or send() throwing: a connectivity-style failure -- we
			// don't know anything about this batch's actual deliverability, only that the round
			// trip didn't complete. Release the lease so the next drain retries it, in order, but
			// do NOT touch attempts: being offline, however long, must never erode the retry
			// budget (the age-based retention floor is the correct, sole backstop for that).
			ReleaseOnly,

			// SendResult.RetryableRejection: the batch reached the server and got a definite "try
			// again later". Release the lease AND bump attempts, dropping any event whose count
			// just reached the configured maximum.
			CountAttemptAndRelease
		}

		// MixpanelClient.SendBatchGuardedAsync remaps RetryableRejection to RetryableFailure before it
		// ever reaches here when running BoundedDrainAsync's compressed, many-attempts-in-seconds
		// drain loop (see its doc comment) -- so a rejection there still leaves the batch spooled for
		// retry, exactly like a normal tick, but never reaches the CountAttemptAndRelease case below
		// and so does not additionally erode the attempt ceiling. This method only ever sees the
		// post-remap result and needs no awareness of which caller produced it.
		private static ClaimOutcome ResolveOutcome(SendResult result)
		{
			switch (result)
			{
				case SendResult.Delivered:
				case SendResult.PoisonDrop:
					return ClaimOutcome.Remove;
				case SendResult.RetryableRejection:
					return ClaimOutcome.CountAttemptAndRelease;
				default: // SendResult.RetryableFailure
					return ClaimOutcome.ReleaseOnly;
			}
		}

		private class ClaimedBatch
		{
			public readonly List<long> Ids = new List<long>();
			public readonly List<AnalyticsEvent> Events = new List<AnalyticsEvent>();
			// Claimed rows whose payload could not be deserialized. Removed regardless of the
			// batch's verdict -- they can never be delivered.
			public readonly List<long> UndeserializableIds = new List<long>();
		}

		// Short, purely-local transaction: pick the oldest unleased rows within both budgets and
		// lease them so a concurrent drain (this process or another) will not send them too.
		private ClaimedBatch ClaimBatch(int maxItems, long maxBytes)
		{
			var claim = new ClaimedBatch();

			lock (_sync)
			{
				if (_disposed || _connection == null)
					return claim;

				var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
				var leaseUntilMs = nowMs + (long)s_leaseDuration.TotalMilliseconds;

				// IMMEDIATE: this transaction reads and then writes, and a deferred transaction
				// that upgrades can deadlock against another process doing the same (both hold
				// SHARED, both want RESERVED) in a way busy_timeout cannot resolve.
				using (var txn = _connection.BeginTransaction(deferred: false))
				{
					long batchBytes = 0;
					using (var cmd = _connection.CreateCommand())
					{
						cmd.Transaction = txn;
						cmd.CommandText =
							"SELECT id, len, payload FROM events WHERE lease_until_unix_ms <= @now " +
							"ORDER BY id ASC LIMIT @maxItems;";
						cmd.Parameters.AddWithValue("@now", nowMs);
						cmd.Parameters.AddWithValue("@maxItems", maxItems);

						using (var reader = cmd.ExecuteReader())
						{
							while (reader.Read())
							{
								// Budget checked BEFORE adding, against the bytes already gathered,
								// so the first event always goes even if it alone exceeds the
								// budget -- the budget can never starve the queue.
								if (batchBytes >= maxBytes)
									break;

								var id = reader.GetInt64(0);
								var len = reader.GetInt64(1);
								var payload = (byte[])reader.GetValue(2);

								claim.Ids.Add(id);
								batchBytes += len;

								try
								{
									claim.Events.Add(AnalyticsEvent.FromBytes(payload));
								}
								catch (Exception e)
								{
									Debug.WriteLine(
										"SqliteEventSpool.ClaimBatch: failed to deserialize event, dropping: " + e);
									claim.UndeserializableIds.Add(id);
								}
							}
						}
					}

					if (claim.Ids.Count > 0)
					{
						using (var cmd = _connection.CreateCommand())
						{
							cmd.Transaction = txn;
							cmd.CommandText =
								"UPDATE events SET lease_until_unix_ms = @leaseUntil WHERE id IN (" +
								IdList(claim.Ids) + ");";
							cmd.Parameters.AddWithValue("@leaseUntil", leaseUntilMs);
							cmd.ExecuteNonQuery();
						}
					}

					txn.Commit();
				}
			}

			return claim;
		}

		// Short, purely-local transaction closing out a claim. ClaimOutcome.Remove: straight
		// DELETE. Otherwise: clear the lease so the next drain retries the batch, in order -- and,
		// ONLY for CountAttemptAndRelease, also bump the attempt counter and drop any event whose
		// count just reached the configured max (dropped NOW, not on some future failure, since
		// the check runs after the increment). Returns how many of undeserializableIds THIS call
		// actually deleted (0 on any exception, including a partial failure that rolled the whole
		// transaction back) -- deliberately not just undeserializableIds.Count, since a concurrent
		// Purge() can remove the same row first (Purge's DELETE races ClaimBatch/ResolveClaim the
		// same way a send does -- see the class remarks), which must not be double-counted as a
		// corruption drop by the caller's RaiseCorrupted.
		// undeserializableIds is always claim.UndeserializableIds from ProcessBatchAsync's ClaimBatch
		// call -- never null (ClaimedBatch initializes it to an empty list) -- so callers need not
		// (and must not) pass null; DeleteByIdLocked already no-ops on an empty list, so every branch
		// below can unconditionally split it out without a separate empty-check first.
		private int ResolveClaim(List<long> ids, ClaimOutcome outcome, List<long> undeserializableIds)
		{
			// Populated inside the lock below, then used to raise ItemDroppedByRetryExhaustion
			// AFTER the lock is released -- same pattern Enqueue uses for ItemDroppedByCap.
			List<long> retryExhaustedIds = null;
			var corruptedRemoved = 0;

			try
			{
				lock (_sync)
				{
					if (_disposed || _connection == null)
						return 0;

					using (var txn = _connection.BeginTransaction(deferred: false))
					{
						if (outcome == ClaimOutcome.Remove)
						{
							// Delivered/PoisonDrop path (or "every claimed row was undeserializable").
							// Split the undeserializable subset out of the single DELETE the old code
							// used, purely so its own affected-row count is available to return below
							// (a no-op DELETE, no round trip, when it's empty -- see DeleteByIdLocked)
							// -- attempts is never touched either way, so a batch that ultimately
							// succeeds (even after some number of prior retryable verdicts of either
							// kind) never counts against the retry ceiling.
							corruptedRemoved = DeleteByIdLocked(txn, undeserializableIds);
							var remaining = new List<long>(ids);
							remaining.RemoveAll(undeserializableIds.Contains);
							DeleteByIdLocked(txn, remaining);
						}
						else
						{
							// Undeserializable rows are removed regardless of which retryable
							// verdict this is: they can never be delivered, so retrying them
							// forever would wedge the head of the queue.
							corruptedRemoved = DeleteByIdLocked(txn, undeserializableIds);

							var toRelease = new List<long>(ids);
							toRelease.RemoveAll(undeserializableIds.Contains);

							if (toRelease.Count > 0)
							{
								if (outcome == ClaimOutcome.CountAttemptAndRelease)
								{
									using (var cmd = _connection.CreateCommand())
									{
										cmd.Transaction = txn;
										cmd.CommandText =
											"UPDATE events SET attempts = attempts + 1, lease_until_unix_ms = 0 " +
											"WHERE id IN (" + IdList(toRelease) + ");";
										cmd.ExecuteNonQuery();
									}

									// Must run AFTER the increment above so an event whose attempts
									// just reached maxAttempts on THIS failure is caught
									// immediately, not on some future failure.
									retryExhaustedIds = new List<long>();
									using (var cmd = _connection.CreateCommand())
									{
										cmd.Transaction = txn;
										cmd.CommandText =
											"SELECT id FROM events WHERE id IN (" + IdList(toRelease) + ") " +
											"AND attempts >= @maxAttempts;";
										cmd.Parameters.AddWithValue("@maxAttempts", _maxAttempts);
										using (var reader = cmd.ExecuteReader())
										{
											while (reader.Read())
												retryExhaustedIds.Add(reader.GetInt64(0));
										}
									}

									if (retryExhaustedIds.Count > 0)
										DeleteByIdLocked(txn, retryExhaustedIds);
								}
								else
								{
									// ReleaseOnly: connectivity-style failure -- attempts is
									// deliberately left untouched (see ClaimOutcome.ReleaseOnly).
									using (var cmd = _connection.CreateCommand())
									{
										cmd.Transaction = txn;
										cmd.CommandText =
											"UPDATE events SET lease_until_unix_ms = 0 WHERE id IN (" +
											IdList(toRelease) + ");";
										cmd.ExecuteNonQuery();
									}
								}
							}
						}

						txn.Commit();
					}
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine("SqliteEventSpool.ResolveClaim failed: " + e);
				return 0;
			}

			// Raised outside the lock: handlers are the caller's code (see RaiseDropped).
			if (retryExhaustedIds != null && retryExhaustedIds.Count > 0)
				RaiseRetryExhausted(retryExhaustedIds.Count);

			return corruptedRemoved;
		}

		// Returns the number of rows actually deleted (per SQLite's own affected-row count), not
		// merely ids.Count -- a concurrent Purge() or an already-expired row can mean fewer rows
		// existed to delete than ids named. See ResolveClaim's corruptedRemoved for why callers
		// rely on this being the true count rather than an assumed one.
		private int DeleteByIdLocked(SqliteTransaction txn, List<long> ids)
		{
			if (ids.Count == 0)
				return 0;

			using (var cmd = _connection.CreateCommand())
			{
				cmd.Transaction = txn;
				cmd.CommandText = "DELETE FROM events WHERE id IN (" + IdList(ids) + ");";
				return cmd.ExecuteNonQuery();
			}
		}

		// Ids are database-generated integers we just read back, never caller input, so inlining
		// them cannot be an injection vector -- and it avoids building N parameters per batch.
		private static string IdList(List<long> ids) => string.Join(",", ids);

		/// <inheritdoc/>
		/// <remarks>
		/// One indexed DELETE. Unlike the old DiskQueue-backed EventSpool's TrimExpired, this does
		/// not have to stop at the first non-expired event -- it removes every expired event wherever it sits,
		/// so an out-of-order timestamp cannot shield older events from the retention floor. A
		/// corrupt payload is irrelevant here too: the age lives in a column, so nothing has to be
		/// deserialized to be dated. Raises <see cref="ItemDroppedByExpiry"/> once per row actually
		/// removed this way, so a drop via this path is never silently uncounted in <c>Statistics</c>.
		/// </remarks>
		public void TrimExpired(TimeSpan maxAge, DateTimeOffset now)
		{
			var dropped = 0;

			try
			{
				var cutoffMs = (now - maxAge).ToUnixTimeMilliseconds();

				lock (_sync)
				{
					if (_disposed || _connection == null)
						return;

					using (var cmd = _connection.CreateCommand())
					{
						cmd.CommandText = "DELETE FROM events WHERE time_unix_ms < @cutoff;";
						cmd.Parameters.AddWithValue("@cutoff", cutoffMs);
						dropped = cmd.ExecuteNonQuery();
					}
				}

				// Raised outside the lock: handlers are the caller's code (see RaiseDropped).
				if (dropped > 0)
					RaiseExpired(dropped);
			}
			catch (Exception e)
			{
				Debug.WriteLine("SqliteEventSpool.TrimExpired failed: " + e);
			}
		}

		/// <inheritdoc/>
		/// <remarks>
		/// DELETE removes the rows; VACUUM rebuilds the database file so the purged bytes are
		/// actually gone rather than lingering as free pages; the WAL checkpoint/truncate does the
		/// same for the write-ahead log. All three matter for a CONSENT purge, where "marked
		/// consumed" is not good enough -- and all three run synchronously, in that order, before
		/// this method returns: a version that backgrounded the VACUUM/checkpoint step was tried and
		/// reverted, because it traded a small, bounded, measured cost (below) for a real regression
		/// -- a crash or kill between this method returning and that background work completing
		/// would leave the "purged" bytes physically recoverable on disk, which is precisely the
		/// guarantee a consent purge exists to provide -- while not even fully solving the UI-thread
		/// concern it was meant to address, since the background work still takes <c>_sync</c>, so
		/// any other call into this spool (e.g. the next <c>Track()</c>) would simply block on that
		/// instead. Measured directly (fill a temp SQLite db with ~2KB rows to ~100MB -- twice this
		/// class's default 50MB byte cap -- then time DELETE FROM events + VACUUM + PRAGMA
		/// wal_checkpoint(TRUNCATE) back to back): the three together took on the order of 60-80ms
		/// (roughly 60ms delete, under 20ms checkpoint, VACUUM itself near-instant on a freshly
		/// emptied table) -- not the multi-second-to-45s stalls the old DiskQueue-backed design
		/// risked (see the class remarks), which is what justified paying a synchronous cost here at
		/// all. Reproduce by timing those three statements against a similarly-sized spool.db.
		/// <para>Contrast the old DiskQueue-backed EventSpool's Purge, which had to dispose the
		/// queue, delete the whole directory and reopen -- briefly dropping its cross-process lock,
		/// and degrading to a no-op spool if another process stole it in that window.</para>
		/// </remarks>
		public void Purge()
		{
			try
			{
				lock (_sync)
				{
					if (_disposed || _connection == null)
						return;

					Execute(_connection, "DELETE FROM events;");
					// Must run outside a transaction, and after the DELETE is committed.
					Execute(_connection, "VACUUM;");
					Execute(_connection, "PRAGMA wal_checkpoint(TRUNCATE);");
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine("SqliteEventSpool.Purge failed: " + e);
			}
		}

		/// <summary>Closes the database. Safe to call more than once.</summary>
		public void Dispose()
		{
			try
			{
				lock (_sync)
				{
					if (_disposed)
						return;

					_disposed = true;
					_connection?.Dispose();
					_connection = null;
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine("SqliteEventSpool.Dispose failed: " + e);
			}
		}
	}
}
