using Segment.Serialization;

namespace DesktopAnalytics
{
	internal interface IClient
	{
		void Initialize(string apiSecret, string host = null, int flushAt = -1, int flushInterval = -1);
		void ShutDown();
		void Identify(string analyticsId, JsonObject traits, JsonObject options);
		void Track(string defaultIdForAnalytics, string eventName, JsonObject properties);
		void Flush();
		Statistics Statistics { get; }

		/// <summary>
		/// Called when the user revokes tracking consent (<see cref="Analytics.AllowTracking"/>
		/// transitioning to false). Implementations that spool events on disk (see
		/// <see cref="MixpanelClient"/>) must purge that spool immediately; implementations without a
		/// local spool (see <see cref="SegmentClient"/>) can no-op.
		/// </summary>
		void PurgeQueuedEvents();

		/// <summary>
		/// Called when the user grants tracking consent again (<see cref="Analytics.AllowTracking"/>
		/// transitioning back to true) on an already-initialized client, after a prior
		/// <see cref="PurgeQueuedEvents"/> paused it. Implementations with a background flush loop (see
		/// <see cref="MixpanelClient"/>) must re-arm it; implementations without one (see
		/// <see cref="SegmentClient"/>) can no-op.
		/// </summary>
		void ResumeSending();
	}
}