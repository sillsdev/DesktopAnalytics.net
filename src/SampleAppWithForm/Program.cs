using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using DesktopAnalytics;

namespace SampleAppWithForm
{
	internal static class Program
	{
		internal static Analytics s_analyticsSingleton;
		/// <summary>
		///  The main entry point for the application.
		/// </summary>
		[STAThread]
		static int Main(string[] args)
		{
			if (args.Length != 2)
			{
				Console.WriteLine("Usage: SampleApp <analyticsApiSecret> <clientType e.g.(Segment|Mixpanel|???)> {-q:maxQueuedEvents} {-f:flushInterval}");
				return 1;
			}


			var userInfo = new UserInfo
			{
				FirstName = "John",
				LastName = "Smith",
				Email="john@example.com",
				UILanguageCode= "es"
			};
			userInfo.OtherProperties.Add("HowIUseIt",
				"This is a really long explanation of how I use this product to see how much you would be able to extract from MixPanel.\r\n" +
				"And a second line of it.");

			var propertiesThatGoWithEveryEvent = new Dictionary<string, string> {{"channel", "beta"}};

			if (!int.TryParse(args.Skip(1).SingleOrDefault(a => a.StartsWith("-q:"))?.Substring(2), out var flushAt))
				flushAt = -1;
			if (!int.TryParse(args.Skip(1).SingleOrDefault(a => a.StartsWith("-f:"))?.Substring(2), out var flushInterval))
				flushInterval = -1;

			s_analyticsSingleton = new Analytics(args[0], userInfo, propertiesThatGoWithEveryEvent, flushAt: flushAt,
				flushInterval: flushInterval);

			var mainWindow = new Form1();
			Application.Run(mainWindow);
			return 0;
		}
	}
}