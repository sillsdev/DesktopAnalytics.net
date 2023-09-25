using Segment.Analytics;

namespace DesktopAnalytics
{
	/// <summary>
	/// Simple class to monitor success rate of statistics reporting.
	/// </summary>
	/// <remarks>See https://github.com/segmentio/Analytics-CSharp#plugin-architecture</remarks>
	public class StatisticsMonitor : Plugin
	{
		private readonly object _lock = new object();
		/// <summary>
		/// Note: Incremented by Analytics whenever Segment.Analytics.Client.Track is called
		/// </summary>
		public int Submitted { get; internal set; }
		public int Failed { get; private set; }
		public int Succeeded { get; private set; }
		private int Outstanding => Submitted - Failed - Succeeded;

		/// <summary>
		/// Called by Analytics whenever an exception is thrown during event reporting.
		/// Any outstanding events are then understood to have failed.
		/// </summary>
		internal void NoteFailures()
		{
			lock (_lock)
				Failed += Outstanding;
		}

		/// <summary>
		/// This property tells Segment to call the <see cref="Execute"/> method
		/// after all event processing completes.
		/// </summary>
		public override PluginType Type => PluginType.After;

		public override RawEvent Execute(RawEvent incomingEvent)
		{
			// We only monitor "track" events. We ignore identify events (which are expected to
			// occur once per user). DesktopAnalytics does not initiate Screen or Group events.
			if (incomingEvent.Type == "track")
			{
				lock (_lock)
					Succeeded++;
			}

			return base.Execute(incomingEvent);
		}
	}
}
