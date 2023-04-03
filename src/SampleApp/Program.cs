using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using DesktopAnalytics;

namespace SampleApp
{
	class Program
	{
		static int Main(string[] args)
		{
			if (args.Length != 2)
			{
				Console.WriteLine("Usage: SampleApp <analyticsApiSecret> <Segment|Mixpanel|???>");
				return 1;
			}

			if (!Enum.TryParse<ClientType>(args[1], true, out var clientType))
			{
				Console.WriteLine($"Usage: SampleApp <analyticsApiSecret> <Segment|Mixpanel|???>{Environment.NewLine}Unrecoginzed client type: {args[1]}");
				return 1;
			}

			var userInfo = new UserInfo
			{
				FirstName = "John",
				LastName = "Smith",
				Email="john@example.com",
				UILanguageCode= "fr"
			};
			userInfo.OtherProperties.Add("HowIUseIt","This is a really long explanation of how I use this product to see how much you would be able to extract from Mixpanel.\r\nAnd a second line of it.");

			var propsForEveryEvent = new Dictionary<string, string> {{"channel", "beta"}};
			using (new Analytics(args[0], userInfo, propertiesThatGoWithEveryEvent:propsForEveryEvent, clientType: clientType))
			{
				Thread.Sleep(3000);
				//note that anything we set from here on didn't make it into the initial "Launch" event. Things we want to 
				//be in that event should go in the propertiesThatGoWithEveryEvent parameter of the constructor.

				Analytics.SetApplicationProperty("TimeSinceLaunch", "3 seconds");
				Analytics.Track("SomeEvent", new Dictionary<string, string>() {{"SomeValue", "62"}});
				Analytics.FlushClient();
				Debug.WriteLine("Sleeping for 20 seconds to give it all a chance to send an event in the background...");
				Thread.Sleep(20000);

				Analytics.SetApplicationProperty("TimeSinceLaunch", "23 seconds");
				Analytics.Track("SomeEvent", new Dictionary<string, string>() {{"SomeValue", "42"}});
				Analytics.FlushClient();
				Console.WriteLine("Sleeping for another 20 seconds to give it all a chance to send an event in the background...");
				Thread.Sleep(20000);

				Console.WriteLine($"Succeeded: {Analytics.Statistics.Succeeded}; " +
					$"Submitted: {Analytics.Statistics.Submitted}; " +
					$"Failed:  {Analytics.Statistics.Failed}");
				return 0;
			}
		}
	}
}
