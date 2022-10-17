using Segment.Analytics;

namespace DesktopAnalytics
{
	public class StatisticsMonitor : Plugin
	{
		public int Submitted { get; internal set; }
		public int Failed { get; internal set; }
		public int Succeeded { get; private set; }
		private int Outstanding => Submitted - Failed - Succeeded;

		internal void NoteFailures()
		{
			Failed += Outstanding;
		}

		public override PluginType type => PluginType.After;

		public override RawEvent Execute(RawEvent incomingEvent)
		{
			if (incomingEvent.type != "identify")
			{
				lock (this)
					Succeeded++;
			}

			return base.Execute(incomingEvent);
		}
	}
}
