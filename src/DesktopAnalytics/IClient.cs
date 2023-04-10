using System;
using Segment.Serialization;

namespace DesktopAnalytics
{
	internal interface IClient
	{
		void Initialize(string apiSecret, int flushAt, int flushInterval);
		void ShutDown();
		event Action<Exception> Failed;
		event Action<string> Succeeded;
		void Identify(string analyticsId, JsonObject traits, JsonObject options);
		void Track(string defaultIdForAnalytics, string eventName, JsonObject properties);
		void Flush();
		Statistics Statistics { get; }
	}
}