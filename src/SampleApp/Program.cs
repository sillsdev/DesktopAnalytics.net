using System;
using System.Collections.Generic;
using System.Threading;
using DesktopAnalytics;

namespace SampleApp
{
	class Program
	{
		static void Main(string[] args)
		{
			if (args.Length == 0)
			{
				Console.WriteLine("Usage: SampleApp <segmentioApiSecret>");
			}

			var userInfo = new UserInfo()
				{
					FirstName = "John",
					LastName = "Smith",
					Email="john@example.com",
					UILanguageCode= "fr"
				};
			userInfo.OtherProperties.Add("HowIUseIt","This is a really long explanation of how I use this product to see how much you would be able to extract from Mixpanel.\r\nAnd a second line of it.");

			using (new Analytics(args[0], userInfo))
			{
				DesktopAnalytics.Analytics.Track("SomeEvent", new Dictionary<string, string>() {{"SomeValue", "62"}});
				Segment.Analytics.Client.Flush();
				Console.WriteLine("Sleeping for a 20 seconds to give it all a chance to send an event in the background..."); 
				Thread.Sleep(20000);
			}
		}
	}
}
