using System.Threading;
using System.Threading.Tasks;
using Segment.Serialization;

namespace DesktopAnalytics
{
	/// <summary>
	/// The seam between <see cref="Analytics"/> and a concrete analytics transport. Core ships
	/// <see cref="SegmentClient"/>; a Mixpanel implementation (<c>MixpanelClient</c>) is provided by
	/// the separate <c>SIL.DesktopAnalytics.Mixpanel</c> package -- core has no compile-time
	/// reference to it. Construct whichever client you want and pass it to one of the
	/// <see cref="Analytics"/> constructors.
	/// </summary>
	public interface IClient
	{
		void Initialize(string apiSecret, string host = null, int flushAt = -1, int flushInterval = -1);
		void ShutDown();
		void Identify(string analyticsId, JsonObject traits, JsonObject options);
		void Track(string defaultIdForAnalytics, string eventName, JsonObject properties);
		void Flush();

		/// <summary>
		/// Async counterpart of <see cref="Track"/>. Implementations whose <c>Track</c> already
		/// completes fast (see <c>SegmentClient</c>/<c>MixpanelClient</c>) may complete
		/// synchronously -- this exists for API ergonomics for callers (e.g. FW Lite) that await
		/// pervasively, not to change how/when the event is sent. Shares <see cref="Track"/>'s
		/// contract: must not throw once the client is initialized (implementations that call Track
		/// before Initialize may still throw, matching Track's own documented behavior).
		/// </summary>
		Task TrackAsync(string defaultIdForAnalytics, string eventName, JsonObject properties,
			CancellationToken cancellationToken = default);

		/// <summary>
		/// Async counterpart of <see cref="ShutDown"/>. Implementations whose shutdown has nothing
		/// awaitable (see <see cref="SegmentClient"/>) may complete synchronously; implementations
		/// with real async delivery work (e.g. <c>MixpanelClient</c>) must never block a
		/// thread waiting on it. Like every member here, must not throw -- including on
		/// cancellation, which just ends any in-flight delivery early (events stay queued).
		/// </summary>
		Task ShutDownAsync(CancellationToken cancellationToken = default);

		/// <summary>
		/// Async counterpart of <see cref="Flush"/>. Same contract notes as
		/// <see cref="ShutDownAsync"/>.
		/// </summary>
		Task FlushAsync(CancellationToken cancellationToken = default);

		Statistics Statistics { get; }

		/// <summary>
		/// Called when the user revokes tracking consent (<see cref="Analytics.AllowTracking"/>
		/// transitioning to false). Implementations that spool events on disk (e.g.
		/// <c>MixpanelClient</c>) must purge that spool immediately; implementations without a
		/// local spool (see <see cref="SegmentClient"/>) can no-op.
		/// </summary>
		void PurgeQueuedEvents();

		/// <summary>
		/// Called when the user grants tracking consent again (<see cref="Analytics.AllowTracking"/>
		/// transitioning back to true) on an already-initialized client, after a prior
		/// <see cref="PurgeQueuedEvents"/> paused it. Implementations with a background flush loop
		/// (e.g. <c>MixpanelClient</c>) must re-arm it; implementations without one (see
		/// <see cref="SegmentClient"/>) can no-op.
		/// </summary>
		void ResumeSending();
	}
}