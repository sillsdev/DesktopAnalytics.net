using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Segment.Serialization;

namespace DesktopAnalytics
{
	/// <summary>
	/// Durable Mixpanel client: events are scrubbed, stamped, and written to an on-disk
	/// <see cref="SqliteEventSpool"/> immediately (never lost to being offline), and a background
	/// flush loop drains the spool through a Polly-wrapped <see cref="IEventSender"/> whenever it
	/// can. See offline-analytics.md for the design this implements.
	/// </summary>
	/// <remarks>
	/// Every public member here swallows exceptions and returns/no-ops rather than throwing --
	/// analytics must never crash the host application (see offline-analytics.md, "Never crash the
	/// host"). The one intentionally-not-swallowed failure is a bad <see cref="SqliteEventSpool"/>
	/// constructor call inside <see cref="Initialize"/>, which is itself caught here so it cannot
	/// escape to the caller either; it just leaves this client running with no spool (Track/Drain
	/// become no-ops) rather than durable.
	/// </remarks>
	public class MixpanelClient : IClient
	{
		// Open questions in offline-analytics.md: exact cap/batch/cadence values are not locked yet.
		// These are reasonable starting defaults, not requirements baked in elsewhere.
		//
		// Sized to comfortably OUTLAST the 60-day age floor (kDefaultMaxSpoolAgeDays below) under
		// realistic desktop-app usage-analytics volumes, rather than being the tightest bound in
		// practice: SqliteEventSpool.TrimExpired's age-based eviction is meant to be what normally
		// reclaims space from a long-offline backlog, not this item cap. At a generous ~200
		// events/day of active use, 60 days is on the order of 12,000 events; 20,000 leaves
		// headroom for bursty days (e.g. an exception storm) without the item cap kicking in well
		// before the age floor ever would.
		private const int kDefaultMaxSpoolItems = 20000;
		// Events per /import request (one request per drain tick). Mixpanel accepts up to 2,000
		// events and 10MB per request; this stays well under both (the per-tick byte budget below
		// bounds the request size long before the count does), while draining a large offline
		// backlog reasonably fast (20,000 events in ~50 min at the default cadence).
		private const int kDefaultBatchSize = 200;
		private const int kDefaultFlushIntervalSeconds = 30;

		// Mixpanel's documented per-event limit: 1MB of uncompressed JSON
		// (https://docs.mixpanel.com/reference/import-events, "Common Issues"). An event over this
		// can never be delivered, so it is refused at enqueue time (counted in Statistics.Failed)
		// instead of spooled -- if it merely got rejected we'd catch it as a PoisonDrop, but one
		// large enough to time out the HTTP request would classify as RETRYABLE (a timeout is
		// indistinguishable from being offline) and wedge the head of the queue forever.
		private const int kMaxSpooledEventBytes = 1024 * 1024;

		// Bandwidth courtesy on slow/metered connections: a soft byte budget per flush tick, and a
		// total-backlog cap (drop-oldest) bounding disk footprint and upload liability. See the
		// "Constrained bandwidth" section of the PR #43 description for the pacing rationale.
		// Like kDefaultMaxSpoolItems above, sized to comfortably outlast the 60-day age floor
		// (kDefaultMaxSpoolAgeDays below): at a generous ~2KB/event average (small usage events
		// plus occasional larger exception/stack-trace payloads), kDefaultMaxSpoolItems worth of
		// events is on the order of 40MB; 50MB leaves headroom above that estimate.
		private const long kMaxBytesPerDrainTick = 256 * 1024;
		private const long kDefaultMaxSpoolBytes = 50L * 1024 * 1024;

		// Age-based retention floor: an ADDITIONAL, independent eviction dimension alongside the
		// item/byte caps above (enforced separately by SqliteEventSpool.TrimExpired), not a replacement
		// for them. Keeps queued (undelivered) events around for roughly two months of offline time
		// before dropping them -- a generous, explicit retention floor of our own, rather than
		// whatever much shorter window Mixpanel's legacy /track endpoint implied (moot here anyway,
		// since MixpanelEventSender uses /import, which has no such limit).
		private const int kDefaultMaxSpoolAgeDays = 60;

		// Per-event retry ceiling (offline-analytics-v2-plan.md, Phase 5 / decision D6): a single
		// event that keeps coming back as RetryableFailure (e.g. a bug in serialization that never
		// quite hits the PoisonDrop path) would otherwise wedge the head of the queue behind it
		// forever -- previously bounded only by the 60-day age floor above, i.e. up to 60 days of
		// pointless retries.
		private const int kDefaultMaxRetryAttempts = 10;

		// On (re)start, attempt the first drain soon rather than waiting a full interval, so events
		// spooled during a PREVIOUS (offline) session go out shortly after launch instead of ~30s later.
		private const int kInitialFlushDelaySeconds = 3;
		// When we get an opportunistic OS signal that connectivity may have returned (a network address
		// change), reschedule the next drain this soon instead of waiting for the next poll tick.
		private const int kReconnectFlushDelaySeconds = 1;

		// Bound on Flush()/ShutDown(): an offline shutdown must return fast rather than hang the
		// host's exit. Two independent bounds (wall-clock + attempt count) so either one alone
		// failing to trip (e.g. a TimeProvider that never advances in a test) still terminates.
		private static readonly TimeSpan s_boundedDrainDuration = TimeSpan.FromSeconds(5);
		private const int kBoundedDrainMaxAttempts = 20;

		private IEventSpool _spool;
		private IEventSender _sender;
		private TimeProvider _timeProvider = TimeProvider.System;
		private ResiliencePipeline<BatchSendResult> _pipeline = ResiliencePipeline<BatchSendResult>.Empty;
		private Mixpanel.MixpanelClient _identifyClient;
		private Timer _flushTimer;
		private int _batchSize = kDefaultBatchSize;
		// Normalized once in Initialize (clamped to >= 1s there); everywhere else just reads it.
		private TimeSpan _flushInterval = TimeSpan.FromSeconds(kDefaultFlushIntervalSeconds);
		// Set by Initialize/InitializeForTest. Track() throws if this is still false -- calling
		// Track on a never-initialized client is a programming error in the host, matching the
		// pre-durability behavior (the old client dereferenced a null field there).
		private bool _initialized;
		// Paused == consent revoked (see PurgeQueuedEvents): the poll timer is stopped and opportunistic
		// network-change kicks are ignored until ResumeSending re-enables them.
		private volatile bool _paused;

		// Cancels whatever timer-driven send (DrainOnceAsync) is currently in flight, via
		// CancelInFlightSend -- used by two independent call paths with different post-conditions:
		// PurgeQueuedEvents (consent revoked; the flush loop keeps running afterward under the fresh
		// token so the batch that send was carrying rolls back into the spool, which the impending
		// Purge() then removes, rather than being resent locally after revocation) and ShutDownAsync
		// (StopTimerPermanently already stopped FUTURE ticks; this only aborts one already in flight
		// so it cannot linger and race the spool/sender dispose that follows -- the fresh token
		// swapped in there is simply discarded moments later along with everything else). This is a
		// best-effort reduction of the window, not a hard guarantee against server-side receipt: if
		// the POST body was already fully sent to and received by Mixpanel's server before this
		// cancellation is observed locally, the batch WAS still delivered once -- cancellation only
		// prevents it from being resent/kept around locally, it cannot recall bytes the server
		// already has. Guarded by Interlocked.Exchange since both callers (e.g. a UI thread via
		// Analytics.AllowTracking, or whatever thread calls ShutDown) can run concurrently with the
		// timer-driven drain being canceled -- and CancelInFlightSend deliberately never Disposes
		// the swapped-out instance (see its own comment), so a concurrent reader of this field that
		// raced the swap and is still holding the old CancellationTokenSource can safely finish
		// using it without needing a lock or risking ObjectDisposedException.
		private CancellationTokenSource _sendCts = new CancellationTokenSource();

		private int _submitted;
		private int _succeeded;
		private int _failed;
		private int _expired;

		// Guards only against overlapping TIMER ticks: if a previous tick's drain is still running
		// when the next tick fires, the new tick is skipped. It does NOT serialize ticks against
		// Flush/ShutDown/DrainOnceAsync -- those may run concurrently with a tick, which is safe
		// because SqliteEventSpool's own locking makes concurrent callers safe; this flag just
		// keeps a slow drain from stacking up redundant timer callbacks behind it.
		private int _timerDraining;

		// 0 = not yet shut down; guarded by Interlocked.Exchange in ShutDownAsync so the real
		// shutdown body (stop timer, bounded drain, dispose spool/sender) runs exactly once, even
		// if ShutDownAsync/ShutDown is called more than once concurrently.
		private int _shutDownState;

		/// <summary>
		/// Production initializer (via <see cref="IClient"/>). Builds a real on-disk spool keyed by
		/// <paramref name="apiSecret"/>, a real HTTP-posting <see cref="MixpanelEventSender"/>, and a
		/// Polly retry+circuit-breaker pipeline, then starts the background flush timer.
		/// </summary>
		/// <param name="host">Not supported by the Mixpanel client; passing a non-empty value throws
		/// <see cref="ArgumentException"/> (this is the long-standing contract — only
		/// <see cref="SegmentClient"/> honors a host).</param>
		public void Initialize(string apiSecret, string host = null, int flushAt = -1, int flushInterval = -1)
		{
			// Preserve the original contract: unlike SegmentClient, the Mixpanel client does not support
			// pointing at a different host. Validated up front, OUTSIDE the catch below, so a
			// misconfiguration surfaces to the caller exactly as it always has rather than being swallowed.
			if (!string.IsNullOrEmpty(host))
				throw new ArgumentException("MixpanelClient does not currently support a host parameter", nameof(host));

			_initialized = true;

			try
			{
				_batchSize = flushAt > 0 ? flushAt : kDefaultBatchSize;
				// Clamp to >= 1s once, here, so every later consumer can use the value as-is.
				_flushInterval = TimeSpan.FromSeconds(
					Math.Max(1, flushInterval > 0 ? flushInterval : kDefaultFlushIntervalSeconds));

				_timeProvider = TimeProvider.System;
				_spool = new SqliteEventSpool(SqliteEventSpool.GetDefaultSpoolPath(apiSecret),
					kDefaultMaxSpoolItems, kMaxSpooledEventBytes, kDefaultMaxSpoolBytes,
					timeProvider: _timeProvider, maxAttempts: kDefaultMaxRetryAttempts);
				_spool.ItemDroppedByCap += OnItemPermanentlyDropped;
				_spool.ItemDroppedByRetryExhaustion += OnItemPermanentlyDropped;
				_spool.ItemDroppedByCorruption += OnItemPermanentlyDropped;
				_spool.ItemDroppedByExpiry += OnItemExpired;
				_sender = new MixpanelEventSender(apiSecret);
				_pipeline = BuildDefaultPipeline(_timeProvider);

				try
				{
					// Best-effort only; used solely for the (unspooled) Identify call.
					_identifyClient = new Mixpanel.MixpanelClient(apiSecret);
				}
				catch (Exception e)
				{
					Debug.WriteLine("MixpanelClient.Initialize: failed to create identify client: " + e);
				}

				StartTimer();
				SubscribeToNetworkChanges();
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.Initialize failed: " + e);
			}
		}

		/// <summary>
		/// Test-only initializer: injects every non-deterministic dependency directly (the spool, the
		/// sender, the clock, and the resilience pipeline) and deliberately does NOT start the
		/// background timer -- tests pump <see cref="DrainOnceAsync"/> manually instead of racing a real
		/// timer thread. See offline-analytics.md, "Design-for-test seams".
		/// </summary>
		/// <param name="pipeline">If null, defaults to <see cref="ResiliencePipeline{TResult}.Empty"/>
		/// (no retry, no circuit breaker) so that, by default, one <see cref="DrainOnceAsync"/> call maps
		/// to exactly one <see cref="IEventSender.SendBatchAsync"/> call -- the simplest,
		/// most deterministic shape for most tests. Tests that specifically want to exercise Polly
		/// retry/circuit-breaker behavior pass their own pipeline (see
		/// <see cref="BuildDefaultPipeline"/>).</param>
		internal void InitializeForTest(
			IEventSpool spool,
			IEventSender sender,
			TimeProvider timeProvider = null,
			ResiliencePipeline<BatchSendResult> pipeline = null,
			int batchSize = kDefaultBatchSize)
		{
			_initialized = true;
			_spool = spool;
			if (_spool != null)
			{
				_spool.ItemDroppedByCap += OnItemPermanentlyDropped;
				_spool.ItemDroppedByRetryExhaustion += OnItemPermanentlyDropped;
				_spool.ItemDroppedByCorruption += OnItemPermanentlyDropped;
				_spool.ItemDroppedByExpiry += OnItemExpired;
			}
			_sender = sender;
			_timeProvider = timeProvider ?? TimeProvider.System;
			_pipeline = pipeline ?? ResiliencePipeline<BatchSendResult>.Empty;
			_batchSize = batchSize;
		}

		/// <summary>
		/// Test-only: starts the real background flush <see cref="Timer"/> against whatever
		/// dependencies <see cref="InitializeForTest"/> already injected. <see cref="InitializeForTest"/>
		/// deliberately never starts this timer (see its doc comment) because almost every test wants
		/// to pump <see cref="DrainOnceAsync"/> by hand instead of racing a real timer thread -- call
		/// this only when a test specifically needs a genuine timer-driven tick (e.g. proving a normal
		/// drain can be caught mid-send by <see cref="ShutDownAsync"/>, or proving drain-pacing
		/// acceleration across several real ticks).
		/// </summary>
		internal void StartTimerForTest(TimeSpan flushInterval)
		{
			_flushInterval = flushInterval;
			StartTimer();
		}

		// Shared by all three of the spool's "permanently dropped, never delivered" events --
		// ItemDroppedByCap (capacity eviction), ItemDroppedByRetryExhaustion (decision D6), and
		// ItemDroppedByCorruption (an undeserializable row) -- since each has exactly the same
		// statistics effect: keeping Statistics.Failed accurate for a drop that happens later than,
		// and separately from, Track()'s own enqueue-time accounting. ItemDroppedByExpiry is
		// deliberately NOT included here -- see OnItemExpired.
		private void OnItemPermanentlyDropped()
		{
			Interlocked.Increment(ref _failed);
		}

		// ItemDroppedByExpiry gets its own counter rather than folding into OnItemPermanentlyDropped:
		// an event aged out by the retention floor was never attempted and rejected, so counting it
		// as Failed would conflate "we tried and it didn't work" with "we never got to it in time".
		// Keeps Statistics.Submitted == Succeeded + Failed + Expired accurate for this drop path.
		private void OnItemExpired()
		{
			Interlocked.Increment(ref _expired);
		}

		/// <summary>
		/// Builds the production Polly pipeline: a small bounded retry (exponential backoff +
		/// jitter) for quick transient blips, wrapped in a circuit breaker so a sustained outage backs
		/// off instead of hammering the network on every flush tick. Exposed (internal, static) so
		/// tests that specifically want to exercise real Polly retry/circuit-breaker behavior --
		/// rather than the zero-strategy default used by <see cref="InitializeForTest"/> -- can build
		/// one with a deterministic <see cref="TimeProvider"/> (e.g. a zero-delay variant; see
		/// MixpanelClientTests for the rationale). <paramref name="breakDuration"/> similarly lets
		/// tests shrink the production 5s break duration so a breaker-recovery (half-open -> closed)
		/// test can wait it out for real, quickly, on the real <see cref="TimeProvider.System"/>
		/// clock -- rather than driving a <c>FakeTimeProvider</c> through it, which is documented
		/// (Polly #1932, see offline-analytics.md) to need extra ceremony to make retries fire at
		/// all.
		/// </summary>
		internal static ResiliencePipeline<BatchSendResult> BuildDefaultPipeline(
			TimeProvider timeProvider,
			int maxRetryAttempts = 2,
			TimeSpan? retryDelay = null,
			TimeSpan? breakDuration = null)
		{
			// Both retryable verdicts must be handled here, not just RetryableFailure: a
			// RetryableRejection (HTTP 408/429/5xx -- the batch DID reach the server) is exactly the
			// "server is struggling, back off" case the circuit breaker exists for. Omitting it would
			// make a sustained 429/5xx outage invisible to both in-tick retry and the breaker's
			// failure tracking, so the client would keep hammering an already-struggling endpoint
			// every flush tick.
			bool ShouldHandleOutcome(Outcome<BatchSendResult> outcome) =>
				outcome.Exception != null ||
				outcome.Result?.Outcome == SendResult.RetryableFailure ||
				outcome.Result?.Outcome == SendResult.RetryableRejection;

			var retryOptions = new RetryStrategyOptions<BatchSendResult>
			{
				ShouldHandle = args => new ValueTask<bool>(ShouldHandleOutcome(args.Outcome)),
				MaxRetryAttempts = maxRetryAttempts,
				Delay = retryDelay ?? TimeSpan.FromMilliseconds(250),
				BackoffType = DelayBackoffType.Exponential,
				UseJitter = true
			};

			var breakerOptions = new CircuitBreakerStrategyOptions<BatchSendResult>
			{
				ShouldHandle = args => new ValueTask<bool>(ShouldHandleOutcome(args.Outcome)),
				FailureRatio = 0.5,
				// Matches attempts-per-tick: a fully failing drain tick produces exactly
				// maxRetryAttempts + 1 outcomes through this pipeline (1 initial attempt + the
				// configured retries), all within the same instant, so they all land in one
				// SamplingDuration window. Derived from maxRetryAttempts -- rather than a separate
				// hardcoded literal -- so the two can never silently drift out of sync: with a value
				// too high for the actual attempts-per-tick, a single tick could never reach the
				// minimum throughput, and successive ticks are kDefaultFlushIntervalSeconds (30s)
				// apart, far outside the 10s window below, so the breaker could never open at all
				// during a sustained outage. Clamped to Polly's own minimum (2), which only avoids a
				// build-time rejection for maxRetryAttempts: 0 -- it does NOT restore the "one bad
				// tick trips the breaker" property for that value specifically, since a 0-retry tick
				// still produces only 1 outcome and reaching 2 then requires a second tick 30s later,
				// outside this window. Production always passes >= 1 (the default is 2), where the
				// property holds; 0 remains usable but degrades to the slower multi-tick behavior the
				// original (pre-fix) hardcoded-literal bug had for every value.
				MinimumThroughput = Math.Max(2, maxRetryAttempts + 1),
				SamplingDuration = TimeSpan.FromSeconds(10),
				BreakDuration = breakDuration ?? TimeSpan.FromSeconds(5)
			};

			var builder = new ResiliencePipelineBuilder<BatchSendResult>
			{
				TimeProvider = timeProvider ?? TimeProvider.System
			};
			return builder
				.AddRetry(retryOptions)
				.AddCircuitBreaker(breakerOptions)
				.Build();
		}

		private void StartTimer()
		{
			try
			{
				// Fire the first drain soon after launch (bounded by the interval) so events left in the
				// spool by a previous offline session are attempted promptly, not a full interval later.
				var initialDelay = TimeSpan.FromTicks(
					Math.Min(TimeSpan.FromSeconds(kInitialFlushDelaySeconds).Ticks, _flushInterval.Ticks));
				_paused = false;
				// Fire-and-forget by design: OnTimerTickAsync catches everything internally, so the
				// discarded task can never fault unobserved.
				_flushTimer = new Timer(_ => _ = OnTimerTickAsync(), null, initialDelay, _flushInterval);
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.StartTimer failed: " + e);
			}
		}

		private void SubscribeToNetworkChanges()
		{
			try
			{
				NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
			}
			catch (Exception e)
			{
				// Not fatal: the periodic poll timer is the RELIABLE delivery mechanism. This subscription
				// is only an opportunistic accelerator, and some platforms may not support it.
				Debug.WriteLine("MixpanelClient.SubscribeToNetworkChanges failed: " + e);
			}
		}

		private void UnsubscribeFromNetworkChanges()
		{
			try
			{
				NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.UnsubscribeFromNetworkChanges failed: " + e);
			}
		}

		// Opportunistic accelerator: when the OS reports a network address change (e.g. Wi-Fi reconnecting
		// after being offline), reschedule the next drain to happen almost immediately instead of waiting
		// out the remainder of the poll interval. The periodic timer remains the guarantee -- this only
		// improves reconnect latency. Ignored while paused (consent revoked). Never throws.
		private void OnNetworkAddressChanged(object sender, EventArgs e)
		{
			try
			{
				if (_paused)
					return;
				if (!NetworkInterface.GetIsNetworkAvailable())
					return;
				_flushTimer?.Change(
					TimeSpan.FromSeconds(kReconnectFlushDelaySeconds), _flushInterval);
			}
			catch (Exception ex)
			{
				Debug.WriteLine("MixpanelClient.OnNetworkAddressChanged failed: " + ex);
			}
		}

		// Starts on a ThreadPool thread (System.Threading.Timer) and continues on the pool after
		// awaits (ConfigureAwait(false) throughout the drain path) -- never the UI thread.
		private async Task OnTimerTickAsync()
		{
			if (Interlocked.CompareExchange(ref _timerDraining, 1, 0) != 0)
				return; // A previous tick is still draining; skip.

			try
			{
				// Captured before/after the drain (not any new instrumentation) so "this tick made
				// genuine forward progress" can be detected cheaply: a decrease means at least one
				// event was actually removed from the spool this tick (delivered, poison-dropped, or
				// retry-exhausted), as opposed to a tick where nothing happened because we're still
				// offline (every attempt RetryableFailure/RetryableRejection, count unchanged).
				var before = _spool?.ApproximateCount ?? 0;
				await DrainOnceAsync().ConfigureAwait(false);
				var after = _spool?.ApproximateCount ?? 0;

				// Backlog pacing (Phase 6): if this tick made progress but a backlog remains,
				// reschedule soon via the same reconnect-accelerator mechanism instead of waiting the
				// full interval. Skip on no-progress ticks (genuinely offline) to avoid spamming the
				// network. !_paused guards the same race OnNetworkAddressChanged guards against.
				if (!_paused && after > 0 && after < before)
					_flushTimer?.Change(TimeSpan.FromSeconds(kReconnectFlushDelaySeconds), _flushInterval);
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient: background drain failed: " + e);
			}
			finally
			{
				Interlocked.Exchange(ref _timerDraining, 0);
			}
		}

		/// <summary>
		/// One-shot attempt to deliver up to one batch of spooled events. This is exactly what the
		/// background timer awaits on each tick; tests await it directly to pump the flush loop
		/// deterministically instead of racing a real timer (see offline-analytics.md, "Flush-loop
		/// scheduling" in the design-for-test-seams table). Always goes through the full Polly
		/// pipeline (retry + circuit breaker) with no cancellation -- <see cref="BoundedDrainAsync"/>
		/// is the bounded/cancellable variant used by Flush/ShutDown.
		/// </summary>
		internal Task DrainOnceAsync()
		{
			return DrainOnceCoreAsync(_sendCts.Token, usePipeline: true);
		}

		// Shared implementation behind DrainOnceAsync() and BoundedDrainAsync(). usePipeline is false
		// only for BoundedDrainAsync's bounded attempts (see its comment for why retries are skipped,
		// and why a RetryableRejection there is remapped in SendBatchGuardedAsync so it does not
		// erode the per-event retry ceiling either).
		private async Task DrainOnceCoreAsync(CancellationToken cancellationToken, bool usePipeline)
		{
			try
			{
				if (_spool == null)
					return;

				// Age-based retention floor: cheap to run every tick since it stops at the first
				// non-expired entry (see SqliteEventSpool.TrimExpired's doc comment).
				_spool.TrimExpired(TimeSpan.FromDays(kDefaultMaxSpoolAgeDays), _timeProvider.GetUtcNow());

				await _spool.ProcessBatchAsync(_batchSize,
						(batch, ct) => SendBatchGuardedAsync(batch, ct, usePipeline),
						kMaxBytesPerDrainTick, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.DrainOnce failed: " + e);
			}
		}

		// The callback SqliteEventSpool.ProcessBatchAsync awaits with the gathered batch; the
		// SendResult returned is the whole batch's fate (commit vs roll back -- per-record rejections
		// within a processed batch are counted in the statistics here and still commit). Must never
		// throw or fault (see SqliteEventSpool.ProcessBatchAsync's contract: a thrown exception is
		// treated as a RetryableFailure anyway, but returning it directly avoids relying on that
		// fallback).
		private async Task<SendResult> SendBatchGuardedAsync(IReadOnlyList<AnalyticsEvent> batch,
			CancellationToken cancellationToken, bool usePipeline)
		{
			try
			{
				BatchSendResult result;
				try
				{
					result = usePipeline
						? await _pipeline.ExecuteAsync(
								async ct => await _sender.SendBatchAsync(batch, ct).ConfigureAwait(false),
								cancellationToken)
							.ConfigureAwait(false)
						: await _sender.SendBatchAsync(batch, cancellationToken).ConfigureAwait(false);
				}
				catch (BrokenCircuitException e)
				{
					// Circuit is open from a sustained outage -- back off without even trying the
					// network this round. Leave the batch for a later retry.
					Debug.WriteLine("MixpanelClient: circuit breaker open, leaving batch for retry: " + e);
					return SendResult.RetryableFailure;
				}
				catch (OperationCanceledException e)
				{
					// BoundedDrain's per-attempt deadline (see its comment) fired before the sender
					// finished -- treat exactly like any other transient failure so the batch stays
					// spooled for the next drain, rather than letting this hang or throw into the host.
					Debug.WriteLine(
						"MixpanelClient: send canceled (bounded drain deadline), leaving batch for retry: " + e);
					return SendResult.RetryableFailure;
				}

				// See BoundedDrainAsync's remarks for why usePipeline also gates this: its compressed,
				// many-attempts-in-seconds retry loop must not let a RetryableRejection erode the
				// per-event retry ceiling the way it does on a normal, once-per-30s-tick drain.
				// Remapping to RetryableFailure reuses that verdict's existing "never counted"
				// handling in SqliteEventSpool.ResolveClaim rather than threading a second flag
				// through IEventSpool's contract for what both verdicts already treat identically
				// otherwise (leave the batch spooled, in order, for a later retry). Normalized BEFORE
				// the switch below (rather than on the way out) so both statistics accounting and the
				// returned verdict always agree on the one true outcome for this call.
				var outcome = !usePipeline && result.Outcome == SendResult.RetryableRejection
					? SendResult.RetryableFailure
					: result.Outcome;

				switch (outcome)
				{
					case SendResult.Delivered:
						var failed = result.FailedIndices.Count;
						Interlocked.Add(ref _succeeded, Math.Max(0, batch.Count - failed));
						if (failed > 0)
							Interlocked.Add(ref _failed, failed);
						break;
					case SendResult.PoisonDrop:
						Interlocked.Add(ref _failed, batch.Count);
						break;
				}

				return outcome;
			}
			catch (Exception e)
			{
				// The sender (or the pipeline itself) threw and retries -- if any -- were exhausted.
				// Never let that propagate: leave the batch in the spool for the next drain.
				Debug.WriteLine("MixpanelClient: send pipeline threw unexpectedly, leaving batch for retry: " +
					e);
				return SendResult.RetryableFailure;
			}
		}

		/// <summary>
		/// Best-effort, non-spooled identify call. Deliberately out of scope for the durability work
		/// here (see offline-analytics.md): traits are small, low-value to replay, and bundling them
		/// into the same at-least-once spool as usage/exception events would complicate dedup for
		/// little benefit. Never throws; failures are logged and otherwise ignored.
		/// </summary>
		public void Identify(string analyticsId, JsonObject traits, JsonObject options)
		{
			try
			{
				if (_identifyClient == null)
					return;

				var task = _identifyClient.PeopleSetAsync(analyticsId, traits);
				task.ContinueWith(t =>
				{
					if (t.Exception != null)
						Debug.WriteLine("MixpanelClient.Identify: best-effort identify failed: " +
							t.Exception);
				}, TaskContinuationOptions.OnlyOnFaulted);
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.Identify failed: " + e);
			}
		}

		/// <summary>
		/// Scrubs the event name and every string property value (this is where stack traces --
		/// spooled as the "Stack Trace" property of the "Exception" event -- get their embedded user
		/// paths normalized), stamps $insert_id/time via <see cref="AnalyticsEvent.Create"/>, and
		/// enqueues into the spool. An event over Mixpanel's per-event size limit (see
		/// <see cref="kMaxSpooledEventBytes"/>) is refused at enqueue and counted in
		/// <see cref="Statistics.Failed"/>. Never throws once initialized; calling it on a client
		/// that was never initialized at all is a programming error in the host and throws
		/// (matching the original MixpanelClient, which dereferenced a null field in that case).
		/// </summary>
		public void Track(string analyticsId, string eventName, JsonObject properties)
		{
			// Deliberately OUTSIDE the catch-all below: a never-initialized client must surface the
			// programming error rather than silently dropping every event. Distinct from _spool ==
			// null, which means Initialize ran but the spool could not be created (e.g. another
			// instance holds the cross-process lock) -- that case degrades to a no-op by design.
			if (!_initialized)
				throw new InvalidOperationException(
					"MixpanelClient.Track called before Initialize");

			try
			{
				// Defense-in-depth against the same consent-revocation race described on
				// Analytics.AllowTracking's setter: PurgeQueuedEvents (via PauseTimer) sets
				// _paused = true BEFORE it cancels the in-flight send and purges the spool, so a
				// concurrent Track() that observes _paused here cannot enqueue an event that would
				// survive the purge. This closes almost all of the residual race window down to the
				// tiny gap between PauseTimer() setting _paused and this read of it -- fully
				// eliminating that last sliver would require a lock spanning both the flag check
				// and the enqueue call, which isn't justified here given how small the window is.
				if (_paused)
					return;

				if (_spool == null)
					return;

				var scrubbedName = PathScrubber.Scrub(eventName);
				var scrubbedProperties = ScrubProperties(properties);

				var evt = AnalyticsEvent.Create(
					analyticsId,
					scrubbedName,
					scrubbedProperties,
					time: _timeProvider.GetUtcNow());

				Interlocked.Increment(ref _submitted);
				if (!_spool.Enqueue(evt))
				{
					// Dropped at enqueue (most likely over the per-event size cap; see
					// kMaxSpooledEventBytes) -- surface it in the statistics like any other
					// undeliverable event.
					Interlocked.Increment(ref _failed);
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.Track failed: " + e);
			}
		}

		/// <summary>
		/// Completes synchronously: <see cref="Track"/> already returns in single-digit
		/// milliseconds (a synchronous SQLite insert -- Microsoft.Data.Sqlite does not implement
		/// true async I/O, so its async ADO.NET methods would not make this any less blocking).
		/// This exists purely for API ergonomics for callers (e.g. FW Lite) that await pervasively,
		/// not to make anything genuinely non-blocking that wasn't already fast.
		/// </summary>
		public Task TrackAsync(string analyticsId, string eventName, JsonObject properties,
			CancellationToken cancellationToken = default)
		{
			Track(analyticsId, eventName, properties);
			return Task.CompletedTask;
		}

		private static JsonObject ScrubProperties(JsonObject properties)
		{
			var result = new JsonObject();
			if (properties == null)
				return result;

			foreach (var kv in properties)
				result[kv.Key] = ScrubValue(kv.Value);

			return result;
		}

		// Recurses into nested JsonObject/JsonArray values so a path embedded anywhere in the
		// property tree gets scrubbed, not just top-level string properties -- e.g. a structured
		// "Context": { "Path": ... } property.
		private static JsonElement ScrubValue(JsonElement value)
		{
			if (value is JsonPrimitive primitive && primitive.IsString)
				return PathScrubber.Scrub(primitive.Content);

			if (value is JsonObject nested)
				return ScrubProperties(nested);

			if (value is JsonArray array)
			{
				var scrubbedArray = new JsonArray();
				foreach (var item in array)
					scrubbedArray.Add(ScrubValue(item));
				return scrubbedArray;
			}

			return value;
		}

		/// <summary>
		/// Bounded drain: repeatedly attempts a drain until the spool is empty or a bound
		/// (wall-clock duration or attempt count) is hit, then returns. Used by both
		/// <see cref="Flush"/> and <see cref="ShutDown"/> so an offline flush/shutdown returns fast
		/// instead of hanging the host.
		/// </summary>
		/// <remarks>
		/// Two things make this provably bounded even against a "black hole" server (one that
		/// accepts the TCP connection but never responds -- captive portals and some firewalls do
		/// this), which the plain <see cref="DrainOnceAsync"/> path is not:
		/// <list type="bullet">
		/// <item>A single <see cref="CancellationTokenSource"/> covering the whole wall-clock
		/// budget is shared by every attempt, passed all the way down to
		/// <see cref="System.Net.Http.HttpClient"/> via <see cref="IEventSender.SendBatchAsync"/>. Without
		/// this, the deadline below is only checked BETWEEN attempts -- one hanging attempt could
		/// run as long as the sender's own retry-multiplied HTTP timeout (e.g. 3 attempts x a 15s
		/// HttpClient timeout ~= 45s) before the bound is ever checked. Created via the injected
		/// <see cref="TimeProvider"/> (not <c>new CancellationTokenSource(delay)</c>, which only
		/// knows the system clock) so tests keep deterministic control of the deadline. The
		/// caller's token (if any) is linked into it via Register, so external cancellation and
		/// the deadline flow through the same token.</item>
		/// <item>Retries are bypassed (<c>usePipeline: false</c>): retrying during a bounded
		/// shutdown/flush has little delivery value (we are about to give up on this attempt
		/// anyway) and would multiply -- rather than bound -- the time spent waiting on a hanging
		/// server.</item>
		/// <item>The per-event retry-attempt ceiling is bypassed too: unlike a normal timer tick
		/// (kDefaultFlushIntervalSeconds = 30s apart, so reaching kDefaultMaxRetryAttempts takes
		/// roughly 5 minutes of real rejections), this loop can drive up to kBoundedDrainMaxAttempts
		/// attempts back-to-back within s_boundedDrainDuration. If the server were quickly returning
		/// RetryableRejection (e.g. rate-limiting), counting those against the ceiling here could
		/// burn through it in seconds and permanently drop otherwise-good events -- during a single
		/// Flush()/ShutDown() call -- for reasons that have nothing to do with the event itself.
		/// <see cref="SendBatchGuardedAsync"/> remaps a RetryableRejection to RetryableFailure
		/// whenever <c>usePipeline</c> is false (i.e. only on this bounded path), reusing
		/// RetryableFailure's existing "never counted" semantics rather than threading a second flag
		/// all the way through <see cref="IEventSpool.ProcessBatchAsync"/>'s contract. The ceiling
		/// still applies normally to every ordinary timer-driven tick; this only exempts the
		/// compressed, bounded-drain-specific retry loop from also counting against it.</item>
		/// </list>
		/// </remarks>
		private async Task BoundedDrainAsync(CancellationToken cancellationToken)
		{
			if (_spool == null)
				return;

			try
			{
				using (var cts = _timeProvider.CreateCancellationTokenSource(s_boundedDrainDuration))
				using (cancellationToken.Register(cts.Cancel))
				{
					for (var i = 0; i < kBoundedDrainMaxAttempts; i++)
					{
						if (cts.IsCancellationRequested)
							return;

						if (_spool.ApproximateCount <= 0)
							return;

						await DrainOnceCoreAsync(cts.Token, usePipeline: false).ConfigureAwait(false);
					}
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.BoundedDrain failed: " + e);
			}
		}

		/// <summary>Bounded drain; returns fast even while offline. See <see cref="BoundedDrainAsync"/>.
		/// Never faults.</summary>
		/// <param name="cancellationToken">Optionally ends the drain even sooner than its own
		/// wall-clock bound; undelivered events stay spooled. Cancellation never faults the task.</param>
		public async Task FlushAsync(CancellationToken cancellationToken = default)
		{
			try
			{
				await BoundedDrainAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.Flush failed: " + e);
			}
		}

		/// <summary>Synchronous <see cref="FlushAsync"/>.</summary>
		/// <remarks>
		/// Blocks on the bounded drain -- safe even from a UI thread because the entire drain path
		/// awaits with ConfigureAwait(false) (no continuation ever needs the caller's
		/// SynchronizationContext), and the drain itself is bounded to a few seconds.
		/// </remarks>
		public void Flush()
		{
			try
			{
				FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.Flush failed: " + e);
			}
		}

		/// <summary>
		/// Bounded drain, then release the spool's cross-process lock. Never hangs, even fully
		/// offline: undelivered events simply remain on disk for the next launch to pick up.
		/// Never faults, and is genuinely idempotent -- guarded by an internal
		/// <c>Interlocked.Exchange</c> so only the first call (even if concurrent) runs the
		/// shutdown body; a redundant call (e.g. Dispose on the <see cref="Analytics"/> facade
		/// after an explicit ShutDownAsync) is an immediate no-op rather than re-entering the
		/// drain against an already-disposed spool/sender.
		/// </summary>
		/// <param name="cancellationToken">Optionally ends the final drain even sooner than its own
		/// wall-clock bound; undelivered events stay on disk. The spool's lock is released either
		/// way, and cancellation never faults the task.</param>
		public async Task ShutDownAsync(CancellationToken cancellationToken = default)
		{
			// Make the "idempotent" guarantee in this method's doc comment actually true rather
			// than accidental: only the FIRST call (even under concurrent callers) runs the body
			// below. Every subsequent call is an immediate no-op instead of re-entering
			// BoundedDrainAsync / ProcessBatchAsync against a spool that may already be disposed.
			if (Interlocked.Exchange(ref _shutDownState, 1) != 0)
				return;

			try
			{
				StopTimerPermanently();
				// StopTimerPermanently only stops FUTURE ticks; a normal drain already mid-send at
				// this instant would otherwise linger and race the dispose below. Same mechanism
				// PurgeQueuedEvents uses -- see CancelInFlightSend's own comment.
				CancelInFlightSend();
				await BoundedDrainAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.ShutDown failed: " + e);
			}
			finally
			{
				try
				{
					_spool?.Dispose();
				}
				catch (Exception e)
				{
					Debug.WriteLine("MixpanelClient.ShutDown: failed to dispose spool: " + e);
				}
				finally
				{
					_spool = null;
				}

				try
				{
					(_sender as IDisposable)?.Dispose();
				}
				catch (Exception e)
				{
					Debug.WriteLine("MixpanelClient.ShutDown: failed to dispose sender: " + e);
				}
				finally
				{
					_sender = null;
				}
			}
		}

		/// <summary>Synchronous <see cref="ShutDownAsync"/>. See <see cref="Flush"/> for why
		/// blocking here is safe.</summary>
		public void ShutDown()
		{
			try
			{
				ShutDownAsync(CancellationToken.None).GetAwaiter().GetResult();
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.ShutDown failed: " + e);
			}
		}

		/// <summary>
		/// Consent revocation: empties the spool immediately and pauses the flush loop (the timer is
		/// stopped and opportunistic network-change kicks are ignored). This is a pause, not a teardown:
		/// if consent is granted again within the same process run, <c>Analytics.AllowTracking</c>'s
		/// setter calls <see cref="ResumeSending"/> to re-arm the loop.
		/// </summary>
		public void PurgeQueuedEvents()
		{
			try
			{
				PauseTimer();
				CancelInFlightSend();
				_spool?.Purge();
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.PurgeQueuedEvents failed: " + e);
			}
		}

		// Aborts whatever timer-driven send is currently in flight (if any). Two call sites, two
		// different reasons: PurgeQueuedEvents uses this so Purge() does not block behind a network
		// round trip, and so the in-flight batch rolls back into the spool -- where the impending
		// Purge() removes it -- rather than being resent/kept around locally after consent was just
		// revoked (best-effort: cannot recall a POST body Mixpanel's server already fully received
		// before the local await observed cancellation). ShutDownAsync uses the exact same swap here
		// for a different reason -- see _sendCts's field doc -- to stop a lingering send from racing
		// the spool/sender dispose that follows shutdown; nothing there depends on the fresh token
		// this swaps in, since everything is torn down moments later regardless. Swaps in a fresh,
		// non-canceled token either way (for PurgeQueuedEvents, that token goes on to back the NEXT
		// drain -- post-purge, or after ResumeSending). Deliberately does NOT Dispose the swapped-out
		// instance: _sendCts has no timer and nothing external registers against its Token beyond
		// this one drain's own await chain, which Cancel() (above) already unwinds -- so there is
		// nothing meaningful to release early, and skipping Dispose means a concurrent DrainOnceAsync
		// that already captured the old token (see DrainOnceAsync) can safely keep using it instead
		// of racing an ObjectDisposedException against this swap.
		private void CancelInFlightSend()
		{
			var previous = Interlocked.Exchange(ref _sendCts, new CancellationTokenSource());
			previous.Cancel();
		}

		/// <summary>
		/// Re-arms the flush loop after <see cref="PurgeQueuedEvents"/> paused it (consent revoked then
		/// granted again in the same run). Clears the paused flag and reschedules the timer to fire
		/// shortly so newly tracked events go out promptly. No-op on the timer if the client was never
		/// initialized (e.g. deferred-init clients) or has been shut down.
		/// </summary>
		public void ResumeSending()
		{
			try
			{
				// Clear the flag unconditionally so the paused state is correct even for a client whose
				// timer was never started (deferred init); reschedule only if there is a live timer.
				_paused = false;
				_flushTimer?.Change(
					TimeSpan.FromSeconds(kReconnectFlushDelaySeconds), _flushInterval);
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.ResumeSending failed: " + e);
			}
		}

		private void PauseTimer()
		{
			try
			{
				_paused = true;
				_flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient: failed to pause timer: " + e);
			}
		}

		private void StopTimerPermanently()
		{
			try
			{
				_paused = true;
				UnsubscribeFromNetworkChanges();
				_flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
				_flushTimer?.Dispose();
				_flushTimer = null;
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient: failed to stop timer: " + e);
			}
		}

		public Statistics Statistics => new Statistics(_submitted, _succeeded, _failed, _expired);

		// Test seam: exposes whether the flush loop is currently paused (consent revoked). See
		// MixpanelClientTests. Not part of IClient.
		internal bool SendingPaused => _paused;
	}
}
