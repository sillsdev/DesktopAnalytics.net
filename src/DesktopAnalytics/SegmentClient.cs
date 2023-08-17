using System;
using System.Threading;
using Segment;
using Segment.Model;

namespace DesktopAnalytics
{
	internal class SegmentClient : IClient
	{

		public event Action<string, Exception> Failed;
		public event Action<string> Succeeded;

		// uses default Segment host if host is null
		public void Initialize(string apiSecret, string host=null)
		{
			var config = new Config();
			if (host != null)
			{
                config.SetHost(host);
            }
            Segment.Analytics.Initialize(apiSecret, config);
            Segment.Analytics.Client.Succeeded += Success_Handler;
            Segment.Analytics.Client.Failed += Fail_Handler;
        }

		private void Fail_Handler(BaseAction action, Exception e)
		{
			Failed?.Invoke(action.Type, e);
		}

		private void Success_Handler(BaseAction action)
		{
			Succeeded?.Invoke(action.Type);
		}

		public void ShutDown()
		{
			// BL-5276 indicated that some events shortly before program termination were not being sent.
			// The documentation is ambiguous about whether Flush() needs to be called before Dispose(),
			// but source code at https://github.com/segmentio/Analytics.NET/blob/master/Analytics/Client.cs
			// clearly says "Note, this does not call Flush() first".
			// So to be sure of getting all our events we should call it. Unfortunately, if Flush is called
			// in response to the main application window closing, it can cause deadlock, and the app hangs.
			// https://github.com/segmentio/Analytics.NET/issues/200
			// So instead of calling Flush, if there are events in the queue, we just wait a little while.
			// The default timeout on the client is 5 seconds, so probably we should never need to wait
			// longer than that.
			var stats = Segment.Analytics.Client.Statistics;
			int totalWait = 0;
			while (stats.Submitted > stats.Failed + stats.Succeeded)
			{
				if (totalWait > 7500)
					break;
				// This might seem like a long time to wait, but
				// a) it will only have to do this is there are unsent events in the queue
				// b) trying to wait less time doesn't seem to make it go any faster. (I did
				//    try to cut the timeout time way down in hopes that that would cause
				//    Segment to process the queue immediately, but it seemed to have no
				//    effect.)
				totalWait += 500;
				Thread.Sleep(500);
			}
			//Client.Flush();
			Segment.Analytics.Client.Dispose();
		}
		public void Identify(string analyticsId, Traits traits, Options options)
		{
			Segment.Analytics.Client.Identify(analyticsId, traits, options);
		}

		public void Track(string defaultIdForAnalytics, string eventName, Properties properties)
		{
			Segment.Analytics.Client.Track(defaultIdForAnalytics, eventName, properties);
		}

		public void Flush()
		{
			Segment.Analytics.Client.Flush();
		}

		public Statistics Statistics => new Statistics(Segment.Analytics.Client.Statistics.Submitted,
			Segment.Analytics.Client.Statistics.Succeeded, Segment.Analytics.Client.Statistics.Failed);
	}
}