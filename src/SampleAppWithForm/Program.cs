using System;
using System.Collections.Generic;
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
		static void Main(string[] args)
		{
			if (args.Length == 0)
				Console.WriteLine("Usage: SampleApp <segmentioApiSecret>");

			var userInfo = new UserInfo
			{
				FirstName = "John",
				LastName = "Smith",
				Email="john@example.com",
				UILanguageCode= "es"
			};
			userInfo.OtherProperties.Add("HowIUseIt",
				"This is a really long explanation of how I use this product to see how much you would be able to extract from Mixpanel.\r\n" +
				"And a second line of it.");

			var propertiesThatGoWithEveryEvent = new Dictionary<string, string> {{"channel", "beta"}};
			s_analyticsSingleton = new Analytics(args[0], userInfo, propertiesThatGoWithEveryEvent);

			var mainWindow = new Form1();
			Application.Run(mainWindow);
		}
	}
}