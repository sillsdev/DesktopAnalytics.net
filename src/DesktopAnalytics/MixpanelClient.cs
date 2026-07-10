using System;
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
	/// <see cref="EventSpool"/> immediately (never lost to being offline), and a background flush
	/// loop drains the spool through a Polly-wrapped <see cref="IEventSender"/> whenever it can. See
	/// offline-analytics.md for the design this implements.
	/// </summary>
	/// <remarks>
	/// Every public member here swallows exceptions and returns/no-ops rather than throwing --
	/// analytics must never crash the host application (see offline-analytics.md, "Never crash the
	/// host"). The one intentionally-not-swallowed failure is a bad <see cref="EventSpool"/>
	/// constructor call inside <see cref="Initialize"/>, which is itself caught here so it cannot
	/// escape to the caller either; it just leaves this client running with no spool (Track/Drain
	/// become no-ops) rather than durable.
	/// </remarks>
	internal class MixpanelClient : IClient
	{
		// Open questions in offline-analytics.md: exact cap/batch/cadence values are not locked yet.
		// These are reasonable starting defaults, not requirements baked in elsewhere.
		private const int kDefaultMaxSpoolItems = 5000;
		private const int kDefaultBatchSize = 20;
		private const int kDefaultFlushIntervalSeconds = 30;

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

		private EventSpool _spool;
		private IEventSender _sender;
		private TimeProvider _timeProvider = TimeProvider.System;
		private ResiliencePipeline<SendResult> _pipeline = ResiliencePipeline<SendResult>.Empty;
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

		private int _submitted;
		private int _succeeded;
		private int _failed;

		// Guards only against overlapping TIMER ticks: if a previous tick's drain is still running
		// when the next tick fires, the new tick is skipped. It does NOT serialize ticks against
		// Flush/ShutDown/DrainOnceAsync -- those may run concurrently with a tick, which is safe
		// because all spool access is serialized inside EventSpool (its semaphore); this flag just
		// keeps a slow drain from stacking up redundant timer callbacks behind it.
		private int _timerDraining;

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
				_spool = new EventSpool(EventSpool.GetDefaultSpoolPath(apiSecret), kDefaultMaxSpoolItems);
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
		/// to exactly one <see cref="IEventSender.SendAsync"/> call per spooled event -- the simplest,
		/// most deterministic shape for most tests. Tests that specifically want to exercise Polly
		/// retry/circuit-breaker behavior pass their own pipeline (see
		/// <see cref="BuildDefaultPipeline"/>).</param>
		internal void InitializeForTest(
			EventSpool spool,
			IEventSender sender,
			TimeProvider timeProvider = null,
			ResiliencePipeline<SendResult> pipeline = null,
			int batchSize = kDefaultBatchSize)
		{
			_initialized = true;
			_spool = spool;
			_sender = sender;
			_timeProvider = timeProvider ?? TimeProvider.System;
			_pipeline = pipeline ?? ResiliencePipeline<SendResult>.Empty;
			_batchSize = batchSize;
		}

		/// <summary>
		/// Builds the production Polly pipeline: a small bounded retry (exponential backoff +
		/// jitter) for quick transient blips, wrapped in a circuit breaker so a sustained outage backs
		/// off instead of hammering the network on every flush tick. Exposed (internal, static) so
		/// tests that specifically want to exercise real Polly retry/circuit-breaker behavior --
		/// rather than the zero-strategy default used by <see cref="InitializeForTest"/> -- can build
		/// one with a deterministic <see cref="TimeProvider"/> (e.g. a zero-delay variant; see
		/// MixpanelClientTests for the rationale).
		/// </summary>
		internal static ResiliencePipeline<SendResult> BuildDefaultPipeline(
			TimeProvider timeProvider,
			int maxRetryAttempts = 2,
			TimeSpan? retryDelay = null)
		{
			bool ShouldHandleOutcome(Outcome<SendResult> outcome) =>
				outcome.Exception != null || outcome.Result == SendResult.RetryableFailure;

			var retryOptions = new RetryStrategyOptions<SendResult>
			{
				ShouldHandle = args => new ValueTask<bool>(ShouldHandleOutcome(args.Outcome)),
				MaxRetryAttempts = maxRetryAttempts,
				Delay = retryDelay ?? TimeSpan.FromMilliseconds(250),
				BackoffType = DelayBackoffType.Exponential,
				UseJitter = true
			};

			var breakerOptions = new CircuitBreakerStrategyOptions<SendResult>
			{
				ShouldHandle = args => new ValueTask<bool>(ShouldHandleOutcome(args.Outcome)),
				FailureRatio = 0.5,
				// Matches attempts-per-tick: a fully failing drain tick produces exactly 3
				// outcomes through this pipeline (1 initial attempt + MaxRetryAttempts=2 retries),
				// all within the same instant, so all 3 always land in one SamplingDuration window.
				// With this at 4 (the previous value), a single tick could never reach the minimum
				// throughput -- and successive ticks are kDefaultFlushIntervalSeconds (30s) apart,
				// far outside the 10s window -- so the breaker could never open at all during a
				// sustained outage. 3 makes one bad tick enough to trip it.
				MinimumThroughput = 3,
				SamplingDuration = TimeSpan.FromSeconds(10),
				BreakDuration = TimeSpan.FromSeconds(5)
			};

			var builder = new ResiliencePipelineBuilder<SendResult>
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
				await DrainOnceAsync().ConfigureAwait(false);
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
			return DrainOnceCoreAsync(CancellationToken.None, usePipeline: true);
		}

		// Shared implementation behind DrainOnceAsync() and BoundedDrainAsync(). usePipeline is false
		// only for BoundedDrainAsync's bounded attempts (see its comment for why retries are skipped
		// there).
		private async Task DrainOnceCoreAsync(CancellationToken cancellationToken, bool usePipeline)
		{
			try
			{
				if (_spool == null)
					return;
				await _spool.ProcessBatchAsync(_batchSize,
						evt => SendOneEventAsync(evt, cancellationToken, usePipeline))
					.ConfigureAwait(false);
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.DrainOnce failed: " + e);
			}
		}

		// The callback EventSpool.ProcessBatchAsync awaits per dequeued event. Must never throw or
		// fault (see EventSpool.ProcessBatchAsync's contract: a thrown exception is treated as a
		// RetryableFailure anyway, but returning it directly avoids relying on that fallback).
		private async Task<SendResult> SendOneEventAsync(AnalyticsEvent evt, CancellationToken cancellationToken,
			bool usePipeline)
		{
			try
			{
				SendResult result;
				try
				{
					result = usePipeline
						? await _pipeline.ExecuteAsync(
								async ct => await _sender.SendAsync(evt, ct).ConfigureAwait(false),
								cancellationToken)
							.ConfigureAwait(false)
						: await _sender.SendAsync(evt, cancellationToken).ConfigureAwait(false);
				}
				catch (BrokenCircuitException e)
				{
					// Circuit is open from a sustained outage -- back off without even trying the
					// network this round. Leave the event for a later retry.
					Debug.WriteLine("MixpanelClient: circuit breaker open, leaving event for retry: " + e);
					return SendResult.RetryableFailure;
				}
				catch (OperationCanceledException e)
				{
					// BoundedDrain's per-attempt deadline (see its comment) fired before the sender
					// finished -- treat exactly like any other transient failure so the event stays
					// spooled for the next drain, rather than letting this hang or throw into the host.
					Debug.WriteLine(
						"MixpanelClient: send canceled (bounded drain deadline), leaving event for retry: " + e);
					return SendResult.RetryableFailure;
				}

				switch (result)
				{
					case SendResult.Delivered:
						Interlocked.Increment(ref _succeeded);
						break;
					case SendResult.PoisonDrop:
						Interlocked.Increment(ref _failed);
						break;
				}

				return result;
			}
			catch (Exception e)
			{
				// The sender (or the pipeline itself) threw and retries -- if any -- were exhausted.
				// Never let that propagate: leave the event in the spool for the next drain.
				Debug.WriteLine("MixpanelClient: send pipeline threw unexpectedly, leaving event for retry: " +
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
		/// enqueues into the spool. Never throws once initialized; calling it on a client that was
		/// never initialized at all is a programming error in the host and throws (matching the
		/// original MixpanelClient, which dereferenced a null field in that case).
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
				if (_spool == null)
					return;

				var scrubbedName = PathScrubber.Scrub(eventName);
				var scrubbedProperties = ScrubProperties(properties);

				var evt = AnalyticsEvent.Create(
					analyticsId,
					scrubbedName,
					scrubbedProperties,
					time: _timeProvider.GetUtcNow());

				_spool.Enqueue(evt);
				Interlocked.Increment(ref _submitted);
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.Track failed: " + e);
			}
		}

		private static JsonObject ScrubProperties(JsonObject properties)
		{
			var result = new JsonObject();
			if (properties == null)
				return result;

			foreach (var kv in properties)
			{
				if (kv.Value is JsonPrimitive primitive && primitive.IsString)
					result[kv.Key] = PathScrubber.Scrub(primitive.Content);
				else
					result[kv.Key] = kv.Value;
			}

			return result;
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
		/// <see cref="System.Net.Http.HttpClient"/> via <see cref="IEventSender.SendAsync"/>. Without
		/// this, the deadline below is only checked BETWEEN attempts -- one hanging attempt could
		/// run as long as the sender's own retry-multiplied HTTP timeout (e.g. 3 attempts x a 15s
		/// HttpClient timeout ~= 45s) before the bound is ever checked. Created via the injected
		/// <see cref="TimeProvider"/> (not <c>new CancellationTokenSource(delay)</c>, which only
		/// knows the system clock) so tests keep deterministic control of the deadline.</item>
		/// <item>Retries are bypassed (<c>usePipeline: false</c>): retrying during a bounded
		/// shutdown/flush has little delivery value (we are about to give up on this attempt
		/// anyway) and would multiply -- rather than bound -- the time spent waiting on a hanging
		/// server.</item>
		/// </list>
		/// </remarks>
		private async Task BoundedDrainAsync()
		{
			if (_spool == null)
				return;

			try
			{
				using (var cts = _timeProvider.CreateCancellationTokenSource(s_boundedDrainDuration))
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
		public async Task FlushAsync()
		{
			try
			{
				await BoundedDrainAsync().ConfigureAwait(false);
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
				FlushAsync().GetAwaiter().GetResult();
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.Flush failed: " + e);
			}
		}

		/// <summary>
		/// Bounded drain, then release the spool's cross-process lock. Never hangs, even fully
		/// offline: undelivered events simply remain on disk for the next launch to pick up.
		/// Never faults, and is idempotent -- a redundant <see cref="ShutDown"/> (e.g. Dispose on
		/// the <see cref="Analytics"/> facade after an explicit ShutDownAsync) finds a stopped
		/// timer, an empty-reporting spool, and a no-op re-Dispose.
		/// </summary>
		public async Task ShutDownAsync()
		{
			try
			{
				StopTimerPermanently();
				await BoundedDrainAsync().ConfigureAwait(false);
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
			}
		}

		/// <summary>Synchronous <see cref="ShutDownAsync"/>. See <see cref="Flush"/> for why
		/// blocking here is safe.</summary>
		public void ShutDown()
		{
			try
			{
				ShutDownAsync().GetAwaiter().GetResult();
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
				_spool?.Purge();
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.PurgeQueuedEvents failed: " + e);
			}
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

		public Statistics Statistics => new Statistics(_submitted, _succeeded, _failed);

		// Test seam: exposes whether the flush loop is currently paused (consent revoked). See
		// MixpanelClientTests. Not part of IClient.
		internal bool SendingPaused => _paused;
	}
}
