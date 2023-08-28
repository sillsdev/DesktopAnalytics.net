using System;
using System.Diagnostics;
using Segment.Concurrent;
using Segment.Serialization;

namespace DesktopAnalytics
{
	internal class SegmentClient : IClient, ICoroutineExceptionHandler
	{
		public event Action<Exception> Failed;
		private Segment.Analytics.Analytics _analytics;
		public static StatisticsMonitor StatMonitor { get; } = new StatisticsMonitor();
		public void Initialize(string apiSecret, string host = null, int flushAt = -1, int flushInterval = -1)
		{
			Segment.Analytics.Configuration configuration;
			if (flushAt >= 0)
			{
				if (flushInterval >= 0)
				{
					configuration = new Segment.Analytics.Configuration(apiSecret,
						Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
						flushAt, flushInterval, exceptionHandler: this, apiHost:host);
				}
				else
				{
					configuration = new Segment.Analytics.Configuration(apiSecret,
						Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
						flushAt, exceptionHandler: this, apiHost:host);
				}
			}
			else
			{
				if (flushInterval >= 0)
				{
					configuration = new Segment.Analytics.Configuration(apiSecret,
						Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
						flushInterval: flushInterval, exceptionHandler: this, apiHost:host);

				}
				else
				{
					configuration = new Segment.Analytics.Configuration(apiSecret,
						Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
						exceptionHandler: this, apiHost:host);
				}
			}

			_analytics = new Segment.Analytics.Analytics(configuration);
			_analytics.Add(StatMonitor);
		}

		public void ShutDown()
		{
			_analytics?.Flush();
		}
		public void Identify(string analyticsId, JsonObject traits, JsonObject options)
		{
			_analytics.Identify(analyticsId, traits);
		}

		public void Track(string defaultIdForAnalytics, string eventName, JsonObject properties)
		{
			lock (StatMonitor)
				StatMonitor.Submitted++;
			_analytics.Track(eventName, properties);
		}

		public void Flush()
		{
			_analytics.Flush();
		}

		public Statistics Statistics => new Statistics(StatMonitor.Submitted,
			StatMonitor.Succeeded, StatMonitor.Failed);

		public void OnExceptionThrown(Exception e)
		{
			Debug.WriteLine($"**** Segment.IO Failed to deliver. {e.Message}");

			lock (StatMonitor)
				StatMonitor.NoteFailures();
			Failed?.Invoke(e);
		}
	}
}