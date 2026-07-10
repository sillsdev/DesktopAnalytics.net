using System.Threading;
using System.Threading.Tasks;

namespace DesktopAnalytics
{
	/// <summary>
	/// The one seam between the durable <see cref="MixpanelClient"/> and the network. Implementations
	/// deliver a single <see cref="AnalyticsEvent"/> and classify the outcome via <see cref="SendResult"/>
	/// (defined alongside <see cref="EventSpool"/>) so the flush loop knows whether to remove the event
	/// from the spool, leave it for retry, or drop it as a poison message.
	/// </summary>
	/// <remarks>
	/// Implementations must never throw (synchronously or as a faulted task): the flush loop (via the
	/// Polly-wrapped call in <see cref="MixpanelClient"/>) treats an exception the same as
	/// <see cref="SendResult.RetryableFailure"/>, but a well-behaved sender should catch its own
	/// network/timeout exceptions and return <see cref="SendResult.RetryableFailure"/> directly.
	/// </remarks>
	internal interface IEventSender
	{
		/// <param name="cancellationToken">Allows a bounded caller (see
		/// <see cref="MixpanelClient"/>'s BoundedDrain, used by Flush/ShutDown) to abandon a send
		/// that is taking too long -- e.g. a server that accepts the connection but never responds
		/// (a "black hole"). Implementations should treat cancellation like any other transient
		/// failure and return <see cref="SendResult.RetryableFailure"/> rather than throwing.</param>
		Task<SendResult> SendAsync(AnalyticsEvent evt, CancellationToken cancellationToken = default);
	}
}
