namespace DesktopAnalytics
{
	/// <summary>
	/// The one seam between the durable <see cref="MixpanelClient"/> and the network. Implementations
	/// deliver a single <see cref="AnalyticsEvent"/> and classify the outcome via <see cref="SendResult"/>
	/// (defined alongside <see cref="EventSpool"/>) so the flush loop knows whether to remove the event
	/// from the spool, leave it for retry, or drop it as a poison message.
	/// </summary>
	/// <remarks>
	/// Implementations must never throw: the flush loop (via the Polly-wrapped call in
	/// <see cref="MixpanelClient"/>) treats an exception the same as <see cref="SendResult.RetryableFailure"/>,
	/// but a well-behaved sender should catch its own network/timeout exceptions and return
	/// <see cref="SendResult.RetryableFailure"/> directly.
	/// </remarks>
	internal interface IEventSender
	{
		/// <summary>
		/// Synchronous by design -- the flush loop that calls this always runs on a background
		/// thread/timer, never on the UI thread. Implementations that need to talk to the network
		/// asynchronously (e.g. via <see cref="System.Net.Http.HttpClient"/>) may block internally.
		/// </summary>
		SendResult Send(AnalyticsEvent evt);
	}
}
