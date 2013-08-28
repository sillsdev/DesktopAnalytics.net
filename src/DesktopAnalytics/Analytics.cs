//License: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Segmentio.Model;

namespace DesktopAnalytics
{
	/// <summary>
	/// NB: if you don't dispose of this, your app might not really exit.
	/// </summary>
	/// <example>
	/// #if DEBUG
	/// using (new Analytics("mySecretSegmentIOKeyForDebugBuild"))
	/// #else
	/// using (new Analytics("mySecretSegmentIOKeyForReleaseBuild")
	/// #endif
	/// {
	///			run the app, with statements like this placed at key points:
	///			Analytics.RecordEvent("Create New Image");
	/// }
	/// </example>
	public class Analytics : IDisposable
	{
		private static string _applicationVersion;

		public Analytics(string apiSecret, UserInfo userInfo, bool allowTracking=true)
		{
			AllowTracking = allowTracking;
			UrlThatReturnsExternalIpAdress = "http://ipecho.net/plain";

			if (!AllowTracking)
				return;

			//bring in settings from any previous version
			if (AnalyticsSettings.Default.NeedUpgrade)
			{
				//see http://stackoverflow.com/questions/3498561/net-applicationsettingsbase-should-i-call-upgrade-every-time-i-load
				AnalyticsSettings.Default.Upgrade();
				AnalyticsSettings.Default.NeedUpgrade = false;
				AnalyticsSettings.Default.Save();
			}

			Segmentio.Analytics.Initialize(apiSecret);
			Segmentio.Analytics.Client.Failed += Client_Failed;
			Segmentio.Analytics.Client.Succeeded += Client_Succeeded;

			if (string.IsNullOrEmpty(AnalyticsSettings.Default.IdForAnalytics))
			{

				AnalyticsSettings.Default.IdForAnalytics = Guid.NewGuid().ToString();
				AnalyticsSettings.Default.Save();
			}

			Traits traits = new Traits()
				{
					{"lastName", userInfo.LastName}, 
					{"firstName", userInfo.FirstName}, 
					{"Email", userInfo.Email},
					{"UILanguage", userInfo.UILanguageCode},//segmentio collects this in context, but doesn't seem to convey it to MixPanel
					{"$browser", GetOperatingSystemLabel()}
				};
			foreach (var property in userInfo.OtherProperties)
			{
				if(!string.IsNullOrWhiteSpace(property.Value))
					traits.Add(property.Key, property.Value);
			}
			
			/*this was cool, but SegmentIO is sending the IP to Mixpanel, so theirs no point in looking it up ourselves
			try
			{
				foreach (var property in GetLocationPropertiesOfThisMachine())
				{
					traits.Add(property.Key, property.Value);
				}
			}
			catch (Exception)
			{
				//swallow
			}*/

			var context = new Context();
			
			context.SetIp(GetExternalIpAddressOfThisMachine());
			context.SetLanguage(userInfo.UILanguageCode);

			Segmentio.Analytics.Client.Identify(AnalyticsSettings.Default.IdForAnalytics, traits, context);
						
			_applicationVersion = System.Reflection.Assembly.GetEntryAssembly().GetName().Version.ToString();

			if (string.IsNullOrEmpty(AnalyticsSettings.Default.LastVersionLaunched))
			{
				//"Created" is a special property that segment.io understands and coverts to equivalents in various analytics services
				//So it's not as descriptive for us as "FirstLaunchOnSystem", but it will give the best experience on the analytics sites.
				Segmentio.Analytics.Client.Track(AnalyticsSettings.Default.IdForAnalytics, "Created", new Properties()
					{
						{"Version", _applicationVersion},
					});
			}
			else if (AnalyticsSettings.Default.LastVersionLaunched != _applicationVersion)
			{
				Segmentio.Analytics.Client.Track(AnalyticsSettings.Default.IdForAnalytics, "Upgrade", new Properties()
					{
						{"OldVersion", AnalyticsSettings.Default.LastVersionLaunched},
						{"Version", _applicationVersion},
					});
			}
			
			//we want to record the launch event independent of whether we also recorded a special first launch

			Segmentio.Analytics.Client.Track(AnalyticsSettings.Default.IdForAnalytics, "Launch", new Properties()
					{
						{"Version", _applicationVersion},
					});
			
			AnalyticsSettings.Default.LastVersionLaunched = _applicationVersion;
			AnalyticsSettings.Default.Save();

		}

		/// <summary>
		/// Override this for any reason you like, including if the built-in one (http://ipecho.net/plain) stops working some day.
		/// The service should simply return a page with a body containing the ip address alone. 
		/// </summary>
		public static string UrlThatReturnsExternalIpAdress { get; set; }

		private string GetExternalIpAddressOfThisMachine()
		{
			using (var client = new WebClient())
			{
				return client.DownloadString(UrlThatReturnsExternalIpAdress);
			}
		}

		private IEnumerable<KeyValuePair<string,string>> GetLocationPropertiesOfThisMachine()
		{
			using (var client = new WebClient())
			{
				var json = client.DownloadString("http://freegeoip.net/json");
				JObject results = JObject.Parse(json);
				yield return new KeyValuePair<string, string>("Country", (string)results["country_name"]);
				yield return new KeyValuePair<string, string>("City", (string)results["city"]);
			}
		}

		/// <summary>
		/// Record an event
		/// </summary>
		///	Analytics.RecordEvent("Save PDF");
		/// <param name="eventName"></param>
		public static void Track(string eventName)
		{
			if (!AllowTracking)
				return;

			Segmentio.Analytics.Client.Track(AnalyticsSettings.Default.IdForAnalytics, eventName);
		}

		/// <summary>
		/// Record an event with extra properties
		/// </summary>
		/// <example>
		///	Analytics.RecordEvent("Save PDF", new Dictionary<string, string>()
		/// {
		///		{"Portion",  Enum.GetName(typeof(BookletPortions), BookletPortion)}, 
		///		{"Layout", PageLayout.ToString()}
		///	});
		///	</example>
		/// <param name="eventName"></param>
		/// <param name="properties"></param>
		public static void Track(string eventName, Dictionary<string, string> properties)
		{
			if (!AllowTracking)
				return;

			Segmentio.Analytics.Client.Track(AnalyticsSettings.Default.IdForAnalytics, eventName, MakeSegmentIOProperties(properties));
		}

		/// <summary>
		/// Use this after showing a registration dialog, so that this stuff is sent right away, rather than the next time you start up Analytics
		/// </summary>
		public static void IdentifyUpdate(UserInfo userInfo)
		{
			if (!AllowTracking)
				return;

			Traits traits = new Traits()
				{
					{"lastName", userInfo.LastName},
					{"firstName", userInfo.FirstName},
					{"Email", userInfo.Email},
					{"UILanguage", userInfo.UILanguageCode}
				};
			foreach (var property in userInfo.OtherProperties)
			{
				if (!string.IsNullOrWhiteSpace(property.Value))
					traits.Add(property.Key, property.Value);
			}
			Segmentio.Analytics.Client.Identify(AnalyticsSettings.Default.IdForAnalytics, traits);
		}

		/// <summary>
		/// Sends the exception's message and stacktrace
		/// </summary>
		/// <param name="e"></param>
		public static void ReportException(Exception e)
		{
			if (!AllowTracking)
				return;

			Segmentio.Analytics.Client.Track(AnalyticsSettings.Default.IdForAnalytics, "Exception", new Segmentio.Model.Properties()
			{
					{ "Message", e.Message },
					{ "Stack Trace", e.StackTrace },
					{ "Version", _applicationVersion}
			});
		}

		private static Properties MakeSegmentIOProperties(Dictionary<string, string> properties)
		{
			var prop = new Properties();
			foreach (var key in properties.Keys)
			{
				prop.Add(key, properties[key]);
			}
			return prop;
		}


		private static void Client_Succeeded(BaseAction action)
		{
			Debug.WriteLine("SegmentIO succeeded: " + action.GetAction());
		}

		private static void Client_Failed(BaseAction action, Exception e)
		{
			Debug.WriteLine("**** Segment.IO Failed to deliver");
		}

		public void Dispose()
		{
			if(Segmentio.Analytics.Client !=null)
				Segmentio.Analytics.Client.Dispose();
		}

		/// <summary>
		/// Indicates whether we are tracking or not
		/// </summary>
		public static bool AllowTracking { get; private set; }

		#region OSVersion
		class Version
		{
			private readonly PlatformID _platform;
			private readonly int _major;
			private readonly int _minor;
			public string Label { get; private set; }

			public Version(PlatformID platform, int major, int minor, string label)
			{
				_platform = platform;
				_major = major;
				_minor = minor;
				Label = label;
			}
			public bool Match(OperatingSystem os)
			{
				return os.Version.Minor == _minor &&
					   os.Version.Major == _major &&
					   os.Platform == _platform;
			}
		}

		private static string GetOperatingSystemLabel()
		{
			var list = new List<Version>();
			list.Add(new Version(System.PlatformID.Win32NT, 5, 0, "Windows 2000"));
			list.Add(new Version(System.PlatformID.Win32NT, 5, 1, "Windows XP"));
			list.Add(new Version(System.PlatformID.Win32NT, 6, 0, "Vista"));
			list.Add(new Version(System.PlatformID.Win32NT, 6, 1, "Windows 7"));
			list.Add(new Version(System.PlatformID.Win32NT, 6, 2, "Windows 8"));
			foreach (var version in list)
			{
				if (version.Match(System.Environment.OSVersion))
					return version.Label;// +" " + Environment.OSVersion.ServicePack;
			}
			return System.Environment.OSVersion.VersionString;
		}
		#endregion
	}

	/// <summary>
	/// Used to send id information to analytics; the most natural way to use this is to load
	/// it from your Settings.Default each time you run, even if you don't know these things
	/// yet because the user hasn't registered yet. Then even if they register while offline,
	/// eventually this informatino will be sent when they *are* online.
	/// </summary>
	public class UserInfo
	{
		public string FirstName="";
		public string LastName = "";
		public string Email="";
		public string UILanguageCode="";
		public Dictionary<string, string> OtherProperties = new Dictionary<string, string>();
	}
}
