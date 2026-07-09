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
		private int _flushIntervalSeconds = kDefaultFlushIntervalSeconds;
		// Paused == consent revoked (see PurgeQueuedEvents): the poll timer is stopped and opportunistic
		// network-change kicks are ignored until ResumeSending re-enables them.
		private volatile bool _paused;

		private int _submitted;
		private int _succeeded;
		private int _failed;

		// Guards against a background timer tick re-entering DrainOnce while a previous tick (or an
		// explicit Flush/ShutDown) is still running. DrainOnce() itself is NOT guarded by this --
		// tests call it directly and expect exactly one pass per call.
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

			try
			{
				_batchSize = flushAt > 0 ? flushAt : kDefaultBatchSize;
				_flushIntervalSeconds = flushInterval > 0 ? flushInterval : kDefaultFlushIntervalSeconds;

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
		/// background timer -- tests pump <see cref="DrainOnce"/> manually instead of racing a real
		/// timer thread. See offline-analytics.md, "Design-for-test seams".
		/// </summary>
		/// <param name="pipeline">If null, defaults to <see cref="ResiliencePipeline{TResult}.Empty"/>
		/// (no retry, no circuit breaker) so that, by default, one <see cref="DrainOnce"/> call maps
		/// to exactly one <see cref="IEventSender.Send"/> call per spooled event -- the simplest,
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
				// Matches attempts-per-tick: a fully failing DrainOnce tick produces exactly 3
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
				var interval = TimeSpan.FromSeconds(Math.Max(1, _flushIntervalSeconds));
				// Fire the first drain soon after launch (bounded by the interval) so events left in the
				// spool by a previous offline session are attempted promptly, not a full interval later.
				var initialDelay = TimeSpan.FromSeconds(Math.Min(kInitialFlushDelaySeconds, Math.Max(1, _flushIntervalSeconds)));
				_paused = false;
				_flushTimer = new Timer(_ => OnTimerTick(), null, initialDelay, interval);
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
					TimeSpan.FromSeconds(kReconnectFlushDelaySeconds),
					TimeSpan.FromSeconds(Math.Max(1, _flushIntervalSeconds)));
			}
			catch (Exception ex)
			{
				Debug.WriteLine("MixpanelClient.OnNetworkAddressChanged failed: " + ex);
			}
		}

		// Runs on a ThreadPool thread (System.Threading.Timer), never the UI thread.
		private void OnTimerTick()
		{
			if (Interlocked.CompareExchange(ref _timerDraining, 1, 0) != 0)
				return; // A previous tick (or a concurrent Flush/ShutDown) is still draining; skip.

			try
			{
				DrainOnce();
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
		/// background timer calls on each tick; tests call it directly to pump the flush loop
		/// deterministically instead of racing a real timer (see offline-analytics.md, "Flush-loop
		/// scheduling" in the design-for-test-seams table). Always goes through the full Polly
		/// pipeline (retry + circuit breaker) with no cancellation -- <see cref="BoundedDrain"/> is
		/// the bounded/cancellable variant used by Flush/ShutDown.
		/// </summary>
		internal void DrainOnce()
		{
			DrainOnceCore(CancellationToken.None, usePipeline: true);
		}

		// Shared implementation behind DrainOnce() and BoundedDrain(). usePipeline is false only for
		// BoundedDrain's bounded attempts (see its comment for why retries are skipped there).
		private void DrainOnceCore(CancellationToken cancellationToken, bool usePipeline)
		{
			try
			{
				_spool?.ProcessBatch(_batchSize, evt => SendOneEvent(evt, cancellationToken, usePipeline));
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.DrainOnce failed: " + e);
			}
		}

		// The callback EventSpool.ProcessBatch invokes per dequeued event. Must never throw (see
		// EventSpool.ProcessBatch's contract: a thrown exception is treated as a RetryableFailure
		// anyway, but returning it directly avoids relying on that fallback).
		private SendResult SendOneEvent(AnalyticsEvent evt, CancellationToken cancellationToken, bool usePipeline)
		{
			try
			{
				SendResult result;
				try
				{
					result = usePipeline
						? _pipeline.Execute(ct => _sender.Send(evt, ct), cancellationToken)
						: _sender.Send(evt, cancellationToken);
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
		/// enqueues into the spool. Never throws.
		/// </summary>
		public void Track(string analyticsId, string eventName, JsonObject properties)
		{
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
					clock: () => _timeProvider.GetUtcNow());

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
		/// this), which the plain <see cref="DrainOnce"/> path is not:
		/// <list type="bullet">
		/// <item>Each attempt gets its own <see cref="CancellationTokenSource"/> tied to the
		/// <em>remaining</em> wall-clock budget, passed all the way down to
		/// <see cref="System.Net.Http.HttpClient"/> via <see cref="IEventSender.Send"/>. Without
		/// this, the deadline below is only checked BETWEEN attempts -- one hanging attempt could
		/// run as long as the sender's own retry-multiplied HTTP timeout (e.g. 3 attempts x a 15s
		/// HttpClient timeout ~= 45s) before the bound is ever checked.</item>
		/// <item>Retries are bypassed (<c>usePipeline: false</c>): retrying during a bounded
		/// shutdown/flush has little delivery value (we are about to give up on this attempt
		/// anyway) and would multiply -- rather than bound -- the time spent waiting on a hanging
		/// server.</item>
		/// </list>
		/// </remarks>
		private void BoundedDrain()
		{
			if (_spool == null)
				return;

			try
			{
				var deadline = _timeProvider.GetUtcNow() + s_boundedDrainDuration;
				for (var i = 0; i < kBoundedDrainMaxAttempts; i++)
				{
					if (_spool.ApproximateCount <= 0)
						return;

					var remaining = deadline - _timeProvider.GetUtcNow();
					if (remaining <= TimeSpan.Zero)
						return;

					using (var cts = new CancellationTokenSource(remaining))
					{
						DrainOnceCore(cts.Token, usePipeline: false);
					}
				}
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.BoundedDrain failed: " + e);
			}
		}

		/// <summary>Bounded drain; returns fast even while offline. See <see cref="BoundedDrain"/>.</summary>
		public void Flush()
		{
			try
			{
				BoundedDrain();
			}
			catch (Exception e)
			{
				Debug.WriteLine("MixpanelClient.Flush failed: " + e);
			}
		}

		/// <summary>
		/// Bounded drain, then release the spool's cross-process lock. Never hangs, even fully
		/// offline: undelivered events simply remain on disk for the next launch to pick up.
		/// </summary>
		public void ShutDown()
		{
			try
			{
				StopTimerPermanently();
				BoundedDrain();
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
					TimeSpan.FromSeconds(kReconnectFlushDelaySeconds),
					TimeSpan.FromSeconds(Math.Max(1, _flushIntervalSeconds)));
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
