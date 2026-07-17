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
			const string usage = "Usage: SampleApp <analyticsApiSecret> <Segment|Mixpanel|???> " +
				"[i[nitialTrackingState]=true|false] [c[onsentToggle]=true|false]";

			if (args.Length < 2 || args.Length > 4)
			{
				Console.WriteLine(usage);
				return 1;
			}

			IClient client;
			if (args[1].Equals("Segment", StringComparison.OrdinalIgnoreCase))
				client = new SegmentClient();
			else if (args[1].Equals("Mixpanel", StringComparison.OrdinalIgnoreCase))
				client = new MixpanelClient();
			else
			{
				Console.WriteLine($"{usage}{Environment.NewLine}Unrecognized client type: {args[1]}");
				return 1;
			}

			var initialTracking = true;
			// Exercises the AllowTracking off/on demo below by default (existing behavior). A caller
			// driving this app as a durability-test harness across multiple process runs (rather than
			// as a manual consent-flow demo) needs c=false: toggling AllowTracking calls
			// PurgeQueuedEvents, which empties the ENTIRE on-disk spool immediately -- including any
			// leftover events from a prior run -- so with the toggle left on, this app can never be
			// used to prove "events spooled by a previous run survive and drain," only to demo consent.
			var exerciseConsentToggle = true;

			for (var i = 2; i < args.Length; i++)
			{
				var parts = args[i].Split('=');
				if (parts.Length != 2)
				{
					Console.WriteLine($"Unrecognized parameter: {args[i]}{Environment.NewLine}{usage}");
					return 1;
				}

				if (parts[0].Equals("i", StringComparison.OrdinalIgnoreCase) ||
				    parts[0].Equals("initialTrackingState", StringComparison.OrdinalIgnoreCase))
				{
					if (!bool.TryParse(parts[1], out initialTracking))
					{
						Console.WriteLine($"Unrecognized parameter: {args[i]}{Environment.NewLine}{usage}");
						return 1;
					}
				}
				else if (parts[0].Equals("c", StringComparison.OrdinalIgnoreCase) ||
				         parts[0].Equals("consentToggle", StringComparison.OrdinalIgnoreCase))
				{
					if (!bool.TryParse(parts[1], out exerciseConsentToggle))
					{
						Console.WriteLine($"Unrecognized parameter: {args[i]}{Environment.NewLine}{usage}");
						return 1;
					}
				}
				else
				{
					Console.WriteLine($"Unrecognized parameter: {args[i]}{Environment.NewLine}{usage}");
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
			using (new Analytics(args[0], userInfo, propsForEveryEvent, initialTracking, client: client))
			{
				Thread.Sleep(3000);
				//note that anything we set from here on didn't make it into the initial "Launch" event. Things we want to 
				//be in that event should go in the propertiesThatGoWithEveryEvent parameter of the constructor.

				Analytics.SetApplicationProperty("TimeSinceLaunch", "3 seconds");
				Analytics.Track("SomeEvent", new Dictionary<string, string> {{"SomeValue", "62"}});
				Debug.WriteLine("Sleeping for 20 seconds to give it all a chance to send an event in the background...");
				Thread.Sleep(20000);

				if (exerciseConsentToggle)
				{
					Analytics.AllowTracking = !Analytics.AllowTracking;
					Analytics.Track("Should not be tracked");
					Debug.WriteLine("Sleeping for 2 seconds just for fun");
					Thread.Sleep(2000);

					Analytics.AllowTracking = !Analytics.AllowTracking;
				}

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
