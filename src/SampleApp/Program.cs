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

			var propertiesThatGoWithEveryEvent = new Dictionary<string, string> {{"channel", "beta"}};
			using (new Analytics(args[0], userInfo, propertiesThatGoWithEveryEvent))
			{
				//note that anything we set from here on didn't make it into the initial "Launch" event. Things we want to 
				//be in that event should go in the propertiesThatGoWithEveryEvent parameter of the constructor.
				DesktopAnalytics.Analytics.SetApplicationProperty("TimeSinceLaunch", "3 seconds");


				DesktopAnalytics.Analytics.Track("SomeEvent", new Dictionary<string, string>() {{"SomeValue", "62"}});
				Segment.Analytics.Client.Flush();
				Console.WriteLine("Sleeping for a 20 seconds to give it all a chance to send an event in the background..."); 
				Thread.Sleep(20000);
			}
		}
	}
}
