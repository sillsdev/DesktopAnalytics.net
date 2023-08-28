using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Segment.Serialization;

namespace DesktopAnalytics
{
	internal class MixpanelClient : IClient
	{
		public event Action<Exception> Failed;
		public event Action<string> Succeeded;

		private Mixpanel.MixpanelClient _client;

		private readonly List<Task<bool>> _tasks = new List<Task<bool>>();

		public void Initialize(string apiSecret, string host = null, int _flushAt = -1, int _flushInterval = -1)
		{
			// currently only the SegmentClient uses the host parameter
			if (host != null)
			{
				throw new ArgumentException("MixpanelClient does not currently support a host parameter");
			}
			_client = new Mixpanel.MixpanelClient(apiSecret);
		}

		public void ShutDown()
		{
			var totalWait = 0;
			while (_tasks.Any(t => !t.IsCompleted))
			{
				if (totalWait > 7500)
					break;
				// REVIEW: I modeled this waiting off the segment client, there might be
				// better metrics for Mixpanel, but I'm in a rush
				totalWait += 500;
				Thread.Sleep(500);
			}
		}

		public void Identify(string analyticsId, JsonObject traits, JsonObject options)
		{
			_tasks.Add(_client.PeopleSetAsync(analyticsId, traits));
		}

		public void Track(string analyticsId, string eventName, JsonObject properties)
		{
			_tasks.Add(_client.TrackAsync(eventName, analyticsId, properties));
		}

		// Flush is really a no-op in our current Mixpanel client
		public void Flush()
		{
		}

		public Statistics Statistics => new Statistics(_tasks.Count,
			_tasks.Count(t => t.IsCompleted && t.Result), _tasks.Count(t => t.IsCompleted && !t.Result));
	}
}