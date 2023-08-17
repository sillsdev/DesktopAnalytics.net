using System;
using Segment.Model;

namespace DesktopAnalytics
{
	internal interface IClient
	{
		void Initialize(string apiSecret, string host=null);
		void ShutDown();
		event Action<string, Exception> Failed;
		event Action<string> Succeeded;
		void Identify(string analyticsId, Traits traits, Options options);
		void Track(string defaultIdForAnalytics, string eventName, Properties properties);
		void Flush();
		Statistics Statistics { get; }
	}
}