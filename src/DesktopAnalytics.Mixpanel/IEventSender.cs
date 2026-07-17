using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopAnalytics
{
	/// <summary>
	/// The outcome of attempting to deliver one batch of events. Mixpanel's /import endpoint
	/// yields exactly two shapes of outcome, and this type models both: either the whole request
	/// failed to be processed (<see cref="Outcome"/> is <see cref="SendResult.RetryableFailure"/>,
	/// <see cref="SendResult.RetryableRejection"/>, or <see cref="SendResult.PoisonDrop"/>,
	/// applying to every event in the batch), or the request WAS processed
	/// (<see cref="SendResult.Delivered"/>) with zero or more individual records rejected as
	/// permanently invalid (<see cref="FailedIndices"/> -- /import's <c>failed_records</c>; the
	/// remainder were ingested).
	/// </summary>
	internal class BatchSendResult
	{
		private static readonly IReadOnlyCollection<int> s_noFailures = new int[0];

		/// <summary>Whole-batch verdict. <see cref="SendResult.Delivered"/> means the request was
		/// processed and every event is finished with (ingested, or rejected-and-reported in
		/// <see cref="FailedIndices"/>) -- the caller should remove the whole batch from the
		/// spool. <see cref="SendResult.RetryableFailure"/> and <see cref="SendResult
		/// .RetryableRejection"/> both mean nothing was processed and the whole batch should be
		/// retried later -- they differ only in whether the round trip actually reached the server
		/// (see each value's own doc). <see cref="SendResult.PoisonDrop"/> means the whole batch
		/// was permanently rejected (e.g. an unexpected 4xx) and should be dropped.</summary>
		public SendResult Outcome { get; }

		/// <summary>When <see cref="Outcome"/> is <see cref="SendResult.Delivered"/>: the
		/// zero-based indices (into the batch that was sent) of records Mixpanel permanently
		/// rejected (its <c>failed_records</c>). Empty otherwise.</summary>
		public IReadOnlyCollection<int> FailedIndices { get; }

		public BatchSendResult(SendResult outcome, IReadOnlyCollection<int> failedIndices = null)
		{
			Outcome = outcome;
			FailedIndices = failedIndices ?? s_noFailures;
		}

		public static readonly BatchSendResult Delivered = new BatchSendResult(SendResult.Delivered);
		public static readonly BatchSendResult Retryable = new BatchSendResult(SendResult.RetryableFailure);
		public static readonly BatchSendResult RetryableRejection =
			new BatchSendResult(SendResult.RetryableRejection);
		public static readonly BatchSendResult Poison = new BatchSendResult(SendResult.PoisonDrop);
	}

	/// <summary>
	/// The one seam between the durable <see cref="MixpanelClient"/> and the network. Implementations
	/// deliver a batch of <see cref="AnalyticsEvent"/>s in a single request and classify the outcome
	/// via <see cref="BatchSendResult"/> so the flush loop knows whether to remove the batch from the
	/// spool, leave it for retry, or drop it.
	/// </summary>
	/// <remarks>
	/// Implementations must never throw (synchronously or as a faulted task): the flush loop (via the
	/// Polly-wrapped call in <see cref="MixpanelClient"/>) treats an exception the same as
	/// <see cref="SendResult.RetryableFailure"/>, but a well-behaved sender should catch its own
	/// network/timeout exceptions and return <see cref="BatchSendResult.Retryable"/> directly.
	/// </remarks>
	internal interface IEventSender
	{
		/// <param name="events">The batch to deliver, oldest first. Never null; callers do not
		/// pass empty batches.</param>
		/// <param name="cancellationToken">Allows a bounded caller (see
		/// <see cref="MixpanelClient"/>'s BoundedDrain, used by Flush/ShutDown) to abandon a send
		/// that is taking too long -- e.g. a server that accepts the connection but never responds
		/// (a "black hole"). Implementations should treat cancellation like any other transient
		/// failure and return <see cref="BatchSendResult.Retryable"/> rather than throwing.</param>
		Task<BatchSendResult> SendBatchAsync(IReadOnlyList<AnalyticsEvent> events,
			CancellationToken cancellationToken = default);
	}
}
