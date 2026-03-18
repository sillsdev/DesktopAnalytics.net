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
			if (args.Length < 2 || args.Length > 3)
			{
				Console.WriteLine("Usage: SampleApp <analyticsApiSecret> <Segment|Mixpanel|???> [i[nitialTrackingState]=true|false]");
				return 1;
			}

			if (!Enum.TryParse<ClientType>(args[1], true, out var clientType))
			{
				Console.WriteLine($"Usage: SampleApp <analyticsApiSecret> <Segment|Mixpanel|???>{Environment.NewLine}Unrecognized client type: {args[1]}");
				return 1;
			}

			var initialTracking = true;
			
			if (args.Length == 3)
			{
				var parts = args[2].Split('=');
				if (parts.Length != 2 ||
				    (!parts[0].Equals("i", StringComparison.OrdinalIgnoreCase) &&
				     !parts[0].Equals("initialTrackingState", StringComparison.OrdinalIgnoreCase)) ||
				    !bool.TryParse(parts[1], out initialTracking))
				{
					Console.WriteLine(
						$"Unrecognized parameter: {args[2]}{Environment.NewLine}Usage: SampleApp <analyticsApiSecret> <Segment|Mixpanel|???> [i[nitialTrackingState]=true|false]");
					return 1;
				}
			}

			var userInfo = new UserInfo
			{
				FirstName = "John",
				LastName = "Smith",
				Email="john@example.com",
				UILanguageCode= "fr"
			};
			userInfo.OtherProperties.Add("HowIUseIt",
				"This is a really long explanation of how I use this product to see how much you would be able to extract from Mixpanel.\r\nAnd a second line of it.");

			var propsForEveryEvent = new Dictionary<string, string> {{"channel", "beta"}};
			using (new Analytics(args[0], userInfo, propsForEveryEvent, initialTracking, clientType: clientType))
			{
				Thread.Sleep(3000);
				//note that anything we set from here on didn't make it into the initial "Launch" event. Things we want to 
				//be in that event should go in the propertiesThatGoWithEveryEvent parameter of the constructor.

				Analytics.SetApplicationProperty("TimeSinceLaunch", "3 seconds");
				Analytics.Track("SomeEvent", new Dictionary<string, string> {{"SomeValue", "62"}});
				Debug.WriteLine("Sleeping for 20 seconds to give it all a chance to send an event in the background...");
				Thread.Sleep(20000);

				Analytics.AllowTracking = !Analytics.AllowTracking;
				Analytics.Track("Should not be tracked");
				Debug.WriteLine("Sleeping for 2 seconds just for fun");
				Thread.Sleep(2000);

				Analytics.AllowTracking = !Analytics.AllowTracking;
				Analytics.SetApplicationProperty("TimeSinceLaunch", "25 seconds");
				Analytics.Track("SomeEvent", new Dictionary<string, string> {{"SomeValue", "42"}});
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
