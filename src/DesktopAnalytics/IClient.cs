using System;
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
	}
}