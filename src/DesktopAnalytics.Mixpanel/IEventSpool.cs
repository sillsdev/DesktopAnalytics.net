using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopAnalytics
{
	/// <summary>
	/// The outcome of attempting to deliver one batch of spooled events, as reported by the caller
	/// of <see cref="IEventSpool.ProcessBatchAsync"/>. This drives whether the batch is removed
	/// from the spool (<see cref="Delivered"/>/<see cref="PoisonDrop"/>) or left in place for a
	/// later retry (<see cref="RetryableFailure"/> or <see cref="RetryableRejection"/>). Both
	/// retry outcomes release the batch's lease the same way; they differ ONLY in whether the
	/// retry is counted against the per-event attempt ceiling (see
	/// <c>SqliteEventSpool.ResolveClaim</c> and offline-analytics-v2-plan.md, Phase 5 / decision
	/// D6) -- see each member's doc for the line between them. (Per-record rejections within a
	/// processed batch are the sender/client's concern -- see
	/// <see cref="BatchSendResult.FailedIndices"/> -- and count as <see cref="Delivered"/> here:
	/// the batch is finished with either way.)
	/// </summary>
	internal enum SendResult
	{
		/// <summary>The batch was processed by the server (e.g. HTTP 2xx, or a strict-mode 400
		/// where the rejected records are individually reported). Remove it from the spool.</summary>
		Delivered,

		/// <summary>A connectivity-level failure: the round trip could not be completed at all, so
		/// nothing is known about this batch's actual deliverability -- a connection error, DNS
		/// failure, timeout, a captive-portal-style 3xx interception, a bounded-drain
		/// cancellation, or a circuit breaker sitting open from a prior outage. Leave the batch in
		/// the spool for a later retry, and (unlike <see cref="RetryableRejection"/>) do NOT count
		/// it against the retry-attempt ceiling: being offline, however long, must never erode
		/// that budget -- the age-based retention floor is the correct, sole backstop for an
		/// offline stretch.</summary>
		RetryableFailure,

		/// <summary>The batch DID reach the server and got back a definite "try again later" (HTTP
		/// 408, 429, or 5xx). Leave the batch in the spool for a later retry, same as
		/// <see cref="RetryableFailure"/>, but DOES count against the retry-attempt ceiling: a
		/// batch that keeps coming back this way is failing for a reason other than plain
		/// connectivity, which is exactly the "stuck retrying a single bad event forever" case the
		/// ceiling exists to bound.</summary>
		RetryableRejection,

		/// <summary>A non-retryable failure (e.g. a 4xx that will never succeed). Remove the batch
		/// from the spool anyway so bad events cannot wedge the spool forever.</summary>
		PoisonDrop
	}

	/// <summary>
	/// The durable, bounded store of pending <see cref="AnalyticsEvent"/>s behind
	/// <see cref="MixpanelClient"/>. Implemented by <see cref="SqliteEventSpool"/>; see
	/// offline-analytics.md for the design background.
	/// </summary>
	/// <remarks>
	/// Contract shared by every implementation:
	/// <list type="bullet">
	/// <item><b>Never throws.</b> Disk/IO/serialization failures are logged and swallowed --
	/// analytics must never crash the host. (Constructors may throw; that is a startup-time
	/// condition the caller needs to know about.)</item>
	/// <item><b>At-least-once delivery.</b> An event is removed only after
	/// <see cref="ProcessBatchAsync"/>'s callback confirms the batch's fate. A crash between a
	/// successful send and that removal replays the batch, which Mixpanel deduplicates via
	/// <see cref="AnalyticsEvent.InsertId"/>.</item>
	/// <item><b>FIFO.</b> Events are gathered oldest-first, and eviction drops oldest-first.</item>
	/// </list>
	/// </remarks>
	internal interface IEventSpool : IDisposable
	{
		/// <summary>
		/// Raised once per event permanently dropped by cap enforcement -- i.e. NOT a drop at
		/// enqueue time (which <see cref="Enqueue"/>'s return value already reports), but an older
		/// event evicted later to stay within the configured caps. This is the only way callers
		/// learn of that kind of drop, since it can happen on an <see cref="Enqueue"/> call for a
		/// different event than the one dropped. Handlers must be fast and must not call back into
		/// the spool.
		/// </summary>
		event Action ItemDroppedByCap;

		/// <summary>
		/// Raised once per event permanently dropped for exceeding the maximum retry attempt count
		/// (see offline-analytics-v2-plan.md, Phase 5 / decision D6) -- distinct from
		/// <see cref="ItemDroppedByCap"/>, which is capacity-based eviction, not a delivery failure.
		/// Handlers must be fast and must not call back into the spool.
		/// </summary>
		event Action ItemDroppedByRetryExhaustion;

		/// <summary>
		/// Raised once per claimed event whose stored payload could not be deserialized (on-disk
		/// corruption, or a payload written by a since-upgraded, incompatible schema version) and
		/// was therefore removed -- it can never be delivered. Distinct from both
		/// <see cref="ItemDroppedByCap"/> and <see cref="ItemDroppedByRetryExhaustion"/>, neither of
		/// which fires for this case, and without which such a drop would silently go uncounted.
		/// Handlers must be fast and must not call back into the spool.
		/// </summary>
		event Action ItemDroppedByCorruption;

		/// <summary>
		/// Raised once per event permanently dropped by <see cref="TrimExpired"/>'s age-based
		/// retention floor -- distinct from <see cref="ItemDroppedByCap"/> (capacity-based eviction)
		/// and <see cref="ItemDroppedByCorruption"/> (an undeserializable row): those are counted as
		/// <c>Statistics.Failed</c>, while this is counted separately as <c>Statistics.Expired</c>,
		/// since aging out is a retention decision rather than a delivery failure. Without this
		/// event, an event dropped this way would silently go uncounted anywhere in
		/// <c>Statistics</c>. Handlers must be fast and must not call back into the spool.
		/// </summary>
		event Action ItemDroppedByExpiry;

		/// <summary>An approximate count of events currently spooled. Accurate enough for
		/// bounding and diagnostics; not a hard guarantee under concurrent access.</summary>
		int ApproximateCount { get; }

		/// <summary>The spool's current logical byte total -- the value the byte cap is enforced
		/// against.</summary>
		long ApproximateBytes { get; }

		/// <summary>
		/// Serializes and enqueues <paramref name="evt"/>, then enforces the configured caps by
		/// dropping the oldest event(s) if necessary.
		/// </summary>
		/// <returns>True if durably enqueued; false if dropped at enqueue time (null,
		/// unserializable, over the per-event byte cap, or a disk failure).</returns>
		bool Enqueue(AnalyticsEvent evt);

		/// <summary>
		/// Async counterpart of <see cref="Enqueue"/>, added for API completeness as part of the
		/// async-first work (see offline-analytics-v2-plan.md, Phase 4). Note: <c>MixpanelClient
		/// .TrackAsync</c> calls the synchronous <see cref="Enqueue"/> path directly and does not go
		/// through this method -- it is not currently called by anything in this codebase.
		/// Implementations complete synchronously: a local SQLite insert here is already
		/// sub-millisecond, and Microsoft.Data.Sqlite does not implement true async I/O anyway, so
		/// there is no real non-blocking work to represent.
		/// </summary>
		Task<bool> EnqueueAsync(AnalyticsEvent evt);

		/// <summary>
		/// Gathers up to <paramref name="maxItems"/> events (oldest first, bounded by
		/// <paramref name="maxBytes"/>), hands them to <paramref name="send"/> as ONE batch, and
		/// commits or rolls back on its verdict: <see cref="SendResult.Delivered"/> and
		/// <see cref="SendResult.PoisonDrop"/> remove the batch; <see cref="SendResult.RetryableFailure"/>
		/// (or a throwing <paramref name="send"/>) leaves it spooled, in order, for a later retry.
		/// An entry that cannot be deserialized is dropped rather than allowed to wedge the spool.
		/// </summary>
		/// <param name="maxBytes">Soft byte budget for the batch, checked AFTER each gathered event
		/// so a single oversized event still goes -- the budget can never starve the queue.</param>
		Task ProcessBatchAsync(int maxItems,
			Func<IReadOnlyList<AnalyticsEvent>, CancellationToken, Task<SendResult>> send,
			long maxBytes = long.MaxValue, CancellationToken cancellationToken = default);

		/// <summary>
		/// Age-based retention floor: drops events whose stamped <see cref="AnalyticsEvent.Time"/>
		/// is older than <paramref name="now"/> minus <paramref name="maxAge"/>. An independent
		/// eviction dimension alongside the item/byte caps, not a replacement for them.
		/// </summary>
		/// <param name="now">From the caller's injected <see cref="TimeProvider"/> so tests stay
		/// deterministic -- never <c>DateTimeOffset.UtcNow</c> directly.</param>
		void TrimExpired(TimeSpan maxAge, DateTimeOffset now);

		/// <summary>
		/// Empties the spool entirely (consent revocation), removing the event data from disk --
		/// not merely marking it consumed -- before returning. May be called from a UI thread (e.g.
		/// from <c>Analytics.AllowTracking</c>'s setter); implementations should keep this bounded to
		/// a small, measured cost rather than an unbounded one (see <c>SqliteEventSpool.Purge</c>'s
		/// doc comment for why a backgrounded version of this was tried and reverted).
		/// </summary>
		void Purge();
	}
}
