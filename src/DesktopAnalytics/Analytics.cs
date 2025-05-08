﻿//License: MIT

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml.Linq;
using System.Xml.XPath;
using JetBrains.Annotations;
using Newtonsoft.Json.Linq;
using Segment.Serialization;
using static System.String;

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
		private const string kUserConfigFileName = "user.config";

		/// <summary>
		/// Collection of "location"-specific traits about the user. This information is
		/// also included with each event to facilitate queries about where in the world
		/// certain events happen. But it is kept separate because there are event-related
		/// properties that are not meaningful to attach to users.
		/// </summary>
		private static JsonObject s_locationInfo;
		private static JsonObject s_traits;
		private static UserInfo s_userInfo;
		private static Analytics s_singleton;
		private readonly Dictionary<string, string> _propertiesThatGoWithEveryEvent;
		private static int s_exceptionCount = 0;

		const int MAX_EXCEPTION_REPORTS_PER_RUN = 10;

		private readonly IClient Client;

		/// <summary>
		/// Initialized a singleton; after calling this, use Analytics.Track() for each event.
		/// </summary>
		/// <param name="apiSecret">The segment.com apiSecret</param>
		/// <param name="userInfo">Information about the user that you have previous collected</param>
		/// <param name="allowTracking">If false, this will not do any communication with segment.io</param>
		/// <param name="retainPii">If false, userInfo will be stripped/hashed/adjusted to prevent
		/// communication of personally identifiable information to the analytics server.</param>
		/// <param name="clientType"><see cref="ClientType"/></param>
		/// <param name="host">The url of the host to send analytics to. Will use the client's
		/// default if not provided. Throws an ArgumentException if the client does not support
		/// setting the host.</param>
		/// <param name="useCallingAssemblyVersion">For plugins (etc.) hosted in-process in another
		/// program, set this flag to force use of the calling assembly instead of the entry
		/// assembly to get version information. (GetEntryAssembly is null for MAF plugins.)
		/// </param>
		[PublicAPI]
		public Analytics(string apiSecret, UserInfo userInfo, bool allowTracking = true,
			bool retainPii = false, ClientType clientType = ClientType.Segment, string host = null, bool useCallingAssemblyVersion = false)
			: this(apiSecret, userInfo, new Dictionary<string, string>(), allowTracking, retainPii, clientType, host,
				assemblyToUseForVersion:useCallingAssemblyVersion ? Assembly.GetCallingAssembly() : null)
		{

		}

		private void UpdateServerInformationOnThisUser()
		{
			s_traits = new JsonObject
			{
				{"lastName", s_userInfo.LastName},
				{"firstName", s_userInfo.FirstName},
				{"Email", s_userInfo.Email},
				{"UILanguage", s_userInfo.UILanguageCode},
				//segmentio collects this in context, but doesn't seem to convey it to Mixpanel
				{"$browser", GetOperatingSystemLabel()}
			};
			foreach (var property in s_userInfo.OtherProperties)
			{
				if (!IsNullOrWhiteSpace(property.Value))
					s_traits.Add(property.Key, property.Value);
			}

			if (!AllowTracking)
				return;

			Client.Identify(AnalyticsSettings.Default.IdForAnalytics, s_traits, s_locationInfo);
		}

		/// <summary>
		/// Initialized a singleton; after calling this, use Analytics.Track() for each event.
		/// </summary>
		/// <param name="apiSecret">The segment.com apiSecret</param>
		/// <param name="userInfo">Information about the user that you have previous collected</param>
		/// <param name="propertiesThatGoWithEveryEvent">A set of key-value pairs to send with
		/// *every* event</param>
		/// <param name="allowTracking">If false, this will not do any communication with segment.io</param>
		/// <param name="retainPii">If false, userInfo will be stripped/hashed/adjusted to prevent
		/// communication of personally identifiable information to the analytics server.</param>
		/// <param name="clientType"><see cref="ClientType"/></param>
		/// <param name="host">The url of the host to send analytics to. Will use the client's
		/// default if not provided. Throws an ArgumentException if the client does not support
		/// setting the host.</param>
		/// <param name="flushAt">Count of events at which we flush events. By default, we do not
		/// batch events.</param>
		/// <param name="flushInterval">Interval in seconds at which we flush events. By default,
		/// we process every event immediately.</param>
		/// <param name="assemblyToUseForVersion">For plugins (etc.) hosted in-process in another
		/// program, pass a specific assembly to use as the basis for getting the version
		/// information. (Note: GetEntryAssembly is null for MAF plugins.)
		/// </param>
		public Analytics(string apiSecret, UserInfo userInfo, Dictionary<string,
			string> propertiesThatGoWithEveryEvent, bool allowTracking = true,
			bool retainPii = false, ClientType clientType = ClientType.Segment,
			string host = null, int flushAt = -1, int flushInterval = -1,
			Assembly assemblyToUseForVersion = null)
		{
			if (s_singleton != null)
			{
				throw new ApplicationException("You can only construct a single Analytics object.");
			}
			s_singleton = this;

			switch (clientType)
			{
				case ClientType.Segment:
				{
					var segmentClient = new SegmentClient();
					segmentClient.Failed += Client_Failed;
					Client = segmentClient;
					break;
				}
				case ClientType.Mixpanel:
				{
					Client = new MixpanelClient();
					break;
				}
				default:
				{
					throw new ArgumentException("Unknown client type", nameof(clientType));
				}
			}
			_propertiesThatGoWithEveryEvent = propertiesThatGoWithEveryEvent;

			s_userInfo = retainPii ? userInfo : userInfo.CreateSanitized();

			AllowTracking = allowTracking;

			// UrlThatReturnsExternalIpAddress is a static and should really be set before this is called, so don't mess with it if the client has given us a different url to us
			if (IsNullOrEmpty(UrlThatReturnsExternalIpAddress))
				UrlThatReturnsGeolocationJson = "http://ip-api.com/json/";

			if (!AllowTracking)
				return;

			//bring in settings from any previous version
			if (AnalyticsSettings.Default.NeedUpgrade)
			{
				//see http://stackoverflow.com/questions/3498561/net-applicationsettingsbase-should-i-call-upgrade-every-time-i-load
				try
				{
					AnalyticsSettings.Default.Upgrade();
					AnalyticsSettings.Default.NeedUpgrade = false;
					TrySaveSettings();
				}
				catch (ConfigurationErrorsException e)
				{
					try
					{
						Console.WriteLine(e);
					}
					catch
					{
						Debug.WriteLine(e);
					}
				}
			}

			if (IsNullOrEmpty(AnalyticsSettings.Default.IdForAnalytics))
			{
				// Apparently a first-time install. Any chance we can migrate settings from another channel of this app?
				// We really want to use the same ID if possible to keep our statistics valid.
				try
				{
					AttemptToGetUserIdSettingsFromDifferentChannel();
				}
				catch (Exception)
				{
					// Oh, well, we tried.
				}
			}

			Client.Initialize(apiSecret, host, flushAt, flushInterval);

			if (IsNullOrEmpty(AnalyticsSettings.Default.IdForAnalytics))
			{
				AnalyticsSettings.Default.IdForAnalytics = Guid.NewGuid().ToString();
				TrySaveSettings();
			}

			s_locationInfo = new JsonObject();

			UpdateServerInformationOnThisUser();
			ReportIpAddressOfThisMachineAsync(); //this will take a while and may fail, so just do it when/if we can
			
			var assembly = assemblyToUseForVersion ?? Assembly.GetEntryAssembly();
			var versionNumberWithBuild = assembly.GetName().Version?.ToString() ?? "";
			string versionNumber = versionNumberWithBuild.Split('.').Take(2).Aggregate((a, b) => a + "." + b);
			SetApplicationProperty("Version", versionNumber);
			SetApplicationProperty("FullVersion", versionNumberWithBuild);
			SetApplicationProperty("UserName", GetUserNameForEvent());
			SetApplicationProperty("Browser", GetOperatingSystemLabel());
			SetApplicationProperty("OS Version Number", GetOperatingSystemVersionLabel());
			SetApplicationProperty("64bit OS", Environment.Is64BitOperatingSystem.ToString());
			SetApplicationProperty("64bit App", Environment.Is64BitProcess.ToString());


			if (IsNullOrEmpty(AnalyticsSettings.Default.LastVersionLaunched))
			{
				//"Created" is a special property that segment.io understands and coverts to equivalents in various analytics services
				//So it's not as descriptive for us as "FirstLaunchOnSystem", but it will give the best experience on the analytics sites.
				TrackWithApplicationProperties("Created");
			}
			else if (AnalyticsSettings.Default.LastVersionLaunched != versionNumberWithBuild)
			{
				TrackWithApplicationProperties("Upgrade", new JsonObject
				{
					{"OldVersion", AnalyticsSettings.Default.LastVersionLaunched},
				});
			}

			// We want to record the launch event independent of whether we also recorded a special first launch
			// But that is done after we retrieve (or fail to retrieve) our external ip address.
			// See http://issues.bloomlibrary.org/youtrack/issue/BL-4011.

			AnalyticsSettings.Default.LastVersionLaunched = versionNumberWithBuild;
			TrySaveSettings();
		}

		private static void TrySaveSettings()
		{
			int retryCount = 0;
			do
			{
				try
				{
					AnalyticsSettings.Default.Save();
					return;
				}
				catch (Exception e)
				{
					try
					{
						Console.WriteLine(e);
					}
					catch
					{
					}
					Thread.Sleep(300);
				}
			} while (++retryCount < 3);
		}

		private void AttemptToGetUserIdSettingsFromDifferentChannel()
		{
			// We need to get the company name and exe name of the main application, without introducing a dependency on
			// Windows.Forms, so we can't use the Windows.Forms.Application methods. For maximum robustness, we try two
			// different approaches.
			// REVIEW: The first approach will NOT work for plugins or calls from unmanaged code, but for maximum
			// compatibility (to keep from breaking Bloom), we try it first. If it fails, we try the second approach,
			// which should work for everyone (though until there is a plugin that supports channels, it will presumably
			// never actually find a pre-existing config file from a different channel).

			for (int attempt = 0; attempt < 2; attempt++)
			{
				string settingsLocation;
				string softwareName;
				if (attempt == 0)
				{
					if (!TryGetSettingsLocationInfoFromEntryAssembly(out settingsLocation, out softwareName))
						continue;
				}
				else
				{
					if (!TryGetDefaultSettingsLocationInfo(out settingsLocation, out softwareName))
						return;
				}

				// Coincidentally, 5 is a good length for Bloom...better heuristic later?
				// For example, we could
				// - look for the last capital letter, and truncate to there. BloomAlpha->Bloom; HearThisAlpha->HearThis; *HearThis->Hear; TEX->?TE; TEXAlpha->TEX; BloomBetaOne->*BloomBeta
				// - look for the first non-initial capital letter, and truncate from there on. BloomAlpha->Bloom, HearThisAlpha->*Hear; HearThis->*Hear; TEX->*T' TEXAlpha->TEX; BloomBetaOne->Bloom
				// - look for a non-initial capital letter following at least one LC letter. Similar except TEX->TEX, TEXAlpha->*TEXAlpha.
				// In general, truncating too much is better than too little; too much just makes us slow, while too little may make us miss useful results.
				// It's true that truncating too much (like TEX->TE) may cause us to fetch an analytics ID from the wrong program. But even this is harmless, AFAIK.
				var index = Math.Min(5, softwareName.Length);
				var prefix = softwareName.Substring(0, index);
				var pattern = prefix + "*";
				var possibleParentFolders = Directory.GetDirectories(settingsLocation, pattern);
				var possibleFolders = new List<string>();
				foreach (var folder in possibleParentFolders)
				{
					possibleFolders.AddRange(Directory.GetDirectories(folder).Where(f => File.Exists(Path.Combine(f, kUserConfigFileName))));
				}

				possibleFolders.Sort((first, second) =>
				{
					if (first == second)
						return 0;
					var firstConfigPath = Path.Combine(first, kUserConfigFileName);
					var secondConfigPath = Path.Combine(second, kUserConfigFileName);
					// Reversing the arguments like this means that second comes before first if it has a LARGER mod time.
					// That is, we end up with the most recently modified user.config first.
					return new FileInfo(secondConfigPath).LastWriteTimeUtc.CompareTo(new FileInfo(firstConfigPath).LastWriteTimeUtc);
				});
				foreach (var folder in possibleFolders)
				{
					try
					{
						var doc = XDocument.Load(Path.Combine(folder, kUserConfigFileName));
						var idSetting =
							doc.XPathSelectElement(
								"configuration/userSettings/DesktopAnalytics.AnalyticsSettings/setting[@name='IdForAnalytics']");
						if (idSetting == null)
							continue;
						string analyticsId = idSetting.Value;
						if (IsNullOrEmpty(analyticsId))
							continue;
						AnalyticsSettings.Default.IdForAnalytics = analyticsId;
						AnalyticsSettings.Default.FirstName = ExtractSetting(AnalyticsSettings.Default.FirstName, doc, "FirstName");
						AnalyticsSettings.Default.LastName = ExtractSetting(AnalyticsSettings.Default.LastName, doc, "LastName");
						AnalyticsSettings.Default.LastVersionLaunched = ExtractSetting(AnalyticsSettings.Default.LastVersionLaunched, doc, "LastVersionLaunched");
						AnalyticsSettings.Default.Email = ExtractSetting(AnalyticsSettings.Default.Email, doc, "Email");
						TrySaveSettings();
						return;
					}
					catch (Exception)
					{
						// If anything goes wrong we just won't try to get our ID from this source.
					}
				}
			}
		}

		private bool TryGetSettingsLocationInfoFromEntryAssembly(out string settingsLocation, out string softwareName)
		{
			settingsLocation = null;
			softwareName = null;

			var entryAssembly = Assembly.GetEntryAssembly(); // the main exe assembly
			if (entryAssembly == null) // Called from unmanaged code?
				return false;
			softwareName = Path.GetFileNameWithoutExtension(entryAssembly.Location);
			AssemblyCompanyAttribute companyAttribute = Attribute.GetCustomAttribute(entryAssembly, typeof(AssemblyCompanyAttribute)) as AssemblyCompanyAttribute;
			if (companyAttribute == null || IsNullOrEmpty(softwareName))
				return false;
			string companyName = companyAttribute.Company;
			if (companyName == null)
				return false;
			settingsLocation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				companyName);
			return true;
		}

		private bool TryGetDefaultSettingsLocationInfo(out string settingsLocation, out string softwareName)
		{
			settingsLocation = null;
			softwareName = null;
			try
			{
				var userConfigPath = GetUserConfigPath();
				if (Path.GetFileName(userConfigPath) != kUserConfigFileName)
					return false;
				userConfigPath = Path.GetDirectoryName(Path.GetDirectoryName(userConfigPath)); // strip file name and last folder level
				softwareName = Path.GetFileName(userConfigPath); // This is actually a folder, not a file.
				if (softwareName == null)
					return false;
				int i = softwareName.IndexOf(".exe", StringComparison.Ordinal);
				if (i > 0)
					softwareName = softwareName.Substring(0, i);
				else
				{
					i = softwareName.IndexOf("_StrongName_", StringComparison.Ordinal);
					if (i > 0)
						softwareName = softwareName.Substring(0, i);
				}
				settingsLocation = Path.GetDirectoryName(userConfigPath); // strip product folder

				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private string GetUserConfigPath()
		{
			try
			{
				return ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal).FilePath;
			}
			catch (ConfigurationErrorsException ex)
			{
				// If the user.config file is corrupt then it will throw a ConfigurationErrorsException
				// Fortunately we can still gets it path from the exception.
				return ex.Filename;
			}
		}

		// If the specified setting's current value is empty, try to extract it from the document.
		private string ExtractSetting(string current, XDocument doc, string name)
		{
			if (!IsNullOrEmpty(current))
				return current;
			var setting =
				doc.XPathSelectElement(
					"configuration/userSettings/DesktopAnalytics.AnalyticsSettings/setting[@name='" + name + "']");
			if (setting == null)
				return "";
			return setting.Value;
		}

		/// <summary>
		/// Use this after showing a registration dialog, so that this stuff is sent right away, rather than the next time you start up Analytics
		/// </summary>
		[PublicAPI]
		public static void IdentifyUpdate(UserInfo userInfo)
		{
			s_userInfo = userInfo;
			s_singleton.UpdateServerInformationOnThisUser();
		}

		/// <summary>
		/// Override this if you want your analytics to report an actual IP address to the server (which could be considered PII), rather than
		/// just the general geolocation info. The service should simply return a page with a body containing the ip address alone.
		/// </summary>
		/// <remarks>This used to default to "http://icanhazip.com"; //formerly: "http://ipecho.net/plain" (that URL went down, but is now back up)</remarks>
		public static string UrlThatReturnsExternalIpAddress { get; set; }
		/// <summary>
		/// Override this for any reason you like, including if the built-in one ( http://ip-api.com/json/) stops working some day.
		/// This will be ignored if <seealso cref="UrlThatReturnsExternalIpAddress"/> is set.
		/// The service should return json that contains values for one or more of the following names: city, country, countryCode,
		/// region, regionName.
		/// </summary>
		public static string UrlThatReturnsGeolocationJson { get; set; }

		private void ReportIpAddressOfThisMachineAsync()
		{
			using (var client = new WebClient())
			{
				try
				{
					var useGeoLocation = IsNullOrEmpty(UrlThatReturnsExternalIpAddress);
					Uri.TryCreate(useGeoLocation ? UrlThatReturnsGeolocationJson : UrlThatReturnsExternalIpAddress,
						UriKind.Absolute, out var uri);
					client.DownloadDataCompleted += (sender, e) =>
					{
						var launchProperties = new JsonObject { { "installedUiLangId", CultureInfo.InstalledUICulture.ThreeLetterISOLanguageName } };

						try
						{
							var result = System.Text.Encoding.UTF8.GetString(e.Result).Trim();
							if (useGeoLocation)
							{
								Debug.WriteLine($"DesktopAnalytics: geolocation JSON data = {result}");
								var j = JObject.Parse(result);
								AddGeolocationProperty(j, "city");
								AddGeolocationProperty(j, "country", "countryCode");
								AddGeolocationProperty(j, "regionName", "region");
							}
							else
							{
								Debug.WriteLine($"DesktopAnalytics: external ip = {result}");
								s_locationInfo.Add("ip", result);
								_propertiesThatGoWithEveryEvent.Add("ip", result);
							}
						}
						catch (Exception)
						{
							// We get here when the user isn't online, or anything else prevents us from getting
							// their IP address or location. Still worth reporting the launch in the latter case.
							TrackWithApplicationProperties("Launch", launchProperties);
							return;
						}
						UpdateServerInformationOnThisUser();
						TrackWithApplicationProperties("Launch", launchProperties);
					};
					client.DownloadDataAsync(uri);

				}
				catch (Exception)
				{
					return;
				}
			}
		}

		private bool AddGeolocationProperty(JObject j, string primary, string secondary = null)
		{
			var value = j.GetValue(primary)?.ToString();
			if (!IsNullOrWhiteSpace(value))
			{
				s_locationInfo.Add(primary, value);
				_propertiesThatGoWithEveryEvent.Add(primary, value);
				return true;
			}

			return secondary != null && AddGeolocationProperty(j, secondary);
		}

		private IEnumerable<KeyValuePair<string, string>> GetLocationPropertiesOfThisMachine()
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
		/// Records an event
		/// </summary>
		/// <example>
		///	Analytics.Track("Save PDF");
		///	</example>
		/// <param name="eventName">A good event name should be meaningful to a developer or
		/// analyst, relatively short, and unique within an app. (Of course, you can issue the
		/// same event from multiple places in your application code if they should be treated as
		/// equivalent events.)</param>
		[PublicAPI]
		public static void Track(string eventName)
		{
			if (!AllowTracking)
				return;

			TrackWithApplicationProperties(eventName);
		}

		/// <summary>
		/// Record an event with extra properties
		/// </summary>
		/// <example>
		///	Analytics.Track("Save PDF", new Dictionary&lt;string, string&gt;()
		/// {
		///		{"Portion",  Enum.GetName(typeof(BookletPortions), BookletPortion)},
		///		{"Layout", PageLayout.ToString()}
		///	});
		///	</example>
		/// <param name="eventName">A good event name should be meaningful to a developer or
		/// analyst, relatively short, and unique within an app. (Of course, you can issue the
		/// same event from multiple places in your application code if they should be treated as
		/// equivalent events.)</param>
		/// <param name="properties">A dictionary of key-value pairs that are relevant in
		/// understanding more about the event being tracked.</param>
		public static void Track(string eventName, Dictionary<string, string> properties)
		{
			if (!AllowTracking)
				return;

			TrackWithApplicationProperties(eventName, MakeSegmentIOProperties(properties));
		}

		/// <summary>
		/// Sends the exception's message and stacktrace
		/// </summary>
		/// <param name="e"></param>
		[PublicAPI]
		public static void ReportException(Exception e)
		{
			ReportException(e, null);
		}

		/// <summary>
		/// Sends the exception's message and stacktrace, plus additional information the
		/// program thinks may be relevant. Limited to MAX_EXCEPTION_REPORTS_PER_RUN
		/// </summary>
		public static void ReportException(Exception e, Dictionary<string, string> moreProperties)
		{
			if (!AllowTracking)
				return;

			s_exceptionCount++;

			// we had an incident where some problem caused a user to emit hundreds of thousands of exceptions,
			// in the background, blowing through our Analytics service limits and getting us kicked off.
			if (s_exceptionCount > MAX_EXCEPTION_REPORTS_PER_RUN)
			{
				return;
			}

			var props = new JsonObject()
			{
				{ "Message", e.Message },
				{ "Stack Trace", e.StackTrace }
			};
			if (moreProperties != null)
			{
				foreach (var key in moreProperties.Keys)
				{
					props.Add(key, moreProperties[key]);
				}
			}
			TrackWithApplicationProperties("Exception", props);
		}

		private static JsonObject MakeSegmentIOProperties(Dictionary<string, string> properties)
		{
			var prop = new JsonObject();
			foreach (var key in properties.Keys)
			{
				prop.Add(key, properties[key]);
			}
			return prop;
		}

		private static void Client_Succeeded(string action)
		{
			Debug.WriteLine($"Analytics action succeeded: {action}");
		}

		private static void Client_Failed(Exception e)
		{
			Debug.WriteLine($"**** Analytics action Failed: {Environment.NewLine}{e.StackTrace}");
		}

		public void Dispose()
		{
			Client?.ShutDown();
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
			public string Label { get; }

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
			if (Environment.OSVersion.Platform == PlatformID.Unix)
			{
				return UnixName == "Linux" ? $"{LinuxVersion} / {LinuxDesktop}" : UnixName;
			}
			var list = new List<Version>
			{
				new Version(PlatformID.Win32NT, 5, 0, "Windows 2000"),
				new Version(PlatformID.Win32NT, 5, 1, "Windows XP"),
				new Version(PlatformID.Win32NT, 6, 0, "Vista"),
				new Version(PlatformID.Win32NT, 6, 1, "Windows 7"),
				// After Windows 8 the Environment.OSVersion started misreporting information unless
				// your app has a manifest which says it supports the OS it is running on. This is not
				// helpful if someone starts using an app built before the OS is released. Anything that
				// reports itself as Windows 8 is suspect, and must get the version info another way.
				new Version(PlatformID.Win32NT, 6, 3, "Windows 8.1"),
				new Version(PlatformID.Win32NT, 10, 0, "Windows 10")
			};

			foreach (var version in list)
			{
				if (version.Match(Environment.OSVersion))
					return version.Label + " " + Environment.OSVersion.ServicePack;
			}

			// Handle any as yet unrecognized (possibly unmanifested) versions, or anything that reported its self as Windows 8.
			if (Environment.OSVersion.Platform == PlatformID.Win32NT)
			{
				return GetWindowsVersionInfoFromNetworkAPI() + " " + Environment.OSVersion.ServicePack;
			}

			return Environment.OSVersion.VersionString;
		}


		private string GetOperatingSystemVersionLabel()
		{
			return Environment.OSVersion.Version.ToString();
		}

		#region Windows8PlusVersionReportingSupport
		[DllImport("netapi32.dll", CharSet = CharSet.Auto)]
		private static extern int NetWkstaGetInfo(string server,
			int level,
			out IntPtr info);

		[DllImport("netapi32.dll")]
		private static extern int NetApiBufferFree(IntPtr pBuf);

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
		private struct MachineInfo
		{
			private readonly int platform_id;
			[MarshalAs(UnmanagedType.LPWStr)]
			private readonly string _computerName;
			[MarshalAs(UnmanagedType.LPWStr)]
			private readonly string _languageGroup;
			public readonly int _majorVersion;
			public readonly int _minorVersion;
		}

		/// <summary>
		/// An application can avoid the need of this method by adding/modifying the application manifest to declare support for a
		/// particular windows version. This code is still necessary to report usefully about versions of windows released after
		/// the application has shipped.
		/// </summary>
		public static string GetWindowsVersionInfoFromNetworkAPI()
		{
			// Get the version information from the network api, passing null to get network info from this machine
			var retval = NetWkstaGetInfo(null, 100, out var pBuffer);
			if (retval != 0)
				return "Windows Unknown(unidentifiable)";

			var info = (MachineInfo)Marshal.PtrToStructure(pBuffer, typeof(MachineInfo));
			string windowsVersion = null;
			if (info._majorVersion == 6)
			{
				if (info._minorVersion == 2)
					windowsVersion = "Windows 8";
				else if (info._minorVersion == 3)
					windowsVersion = "Windows 8.1";
			}
			else if (info._majorVersion == 10 && info._minorVersion == 0)
			{
				windowsVersion = "Windows 10"; 
			}
			else
			{
				windowsVersion = $"Windows Unknown({info._majorVersion}.{info._minorVersion})";
			}
			NetApiBufferFree(pBuffer);
			return windowsVersion;
		}
		#endregion

		[DllImport("libc")]
		static extern int uname(IntPtr buf);
		private static string s_unixName;
		private static string UnixName
		{
			get
			{
				if (Environment.OSVersion.Platform != PlatformID.Unix)
					return Empty;
				if (s_unixName == null)
				{
					IntPtr buf = IntPtr.Zero;
					try
					{
						// This is a hacktastic way of getting sysname from uname()
						buf = Marshal.AllocHGlobal(8192);
						if (uname(buf) == 0)
							s_unixName = Marshal.PtrToStringAnsi(buf);
					}
					catch
					{
						s_unixName = Empty;
					}
					finally
					{
						if (buf != IntPtr.Zero)
							Marshal.FreeHGlobal(buf);
					}
				}
				return s_unixName;
			}
		}

		private static string _linuxVersion;
		private static string LinuxVersion
		{
			get
			{
				if (Environment.OSVersion.Platform != PlatformID.Unix)
					return Empty;
				if (_linuxVersion == null)
				{
					_linuxVersion = Empty;
					if (File.Exists("/etc/wasta-release"))
					{
						var versionData = File.ReadAllText("/etc/wasta-release");
						var versionLines = versionData.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
						foreach (var line in versionLines)
						{
							if (line.StartsWith("DESCRIPTION=\""))
							{
								_linuxVersion = line.Substring(13).Trim('"');
								break;
							}
						}
					}
					else if (File.Exists("/etc/lsb-release"))
					{
						var versionData = File.ReadAllText("/etc/lsb-release");
						var versionLines = versionData.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
						foreach (var t in versionLines)
						{
							if (t.StartsWith("DISTRIB_DESCRIPTION=\""))
							{
								_linuxVersion = t.Substring(21).Trim('"');
								break;
							}
						}
					}
					else
					{
						// If it's linux, it really should have /etc/lsb-release!
						_linuxVersion = Environment.OSVersion.VersionString;
					}
				}
				return _linuxVersion;
			}
		}

		/// <summary>
		/// On a Unix machine this gets the current desktop environment (gnome/xfce/...), on
		/// non-Unix machines the platform name.
		/// </summary>
		private static string DesktopEnvironment
		{
			get
			{
				if (Environment.OSVersion.Platform != PlatformID.Unix)
					return Environment.OSVersion.Platform.ToString();

				// see http://unix.stackexchange.com/a/116694
				// and http://askubuntu.com/a/227669
				var currentDesktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
				if (IsNullOrEmpty(currentDesktop))
				{
					var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
					if (dataDirs != null)
					{
						dataDirs = dataDirs.ToLowerInvariant();
						if (dataDirs.Contains("xfce"))
							currentDesktop = "XFCE";
						else if (dataDirs.Contains("kde"))
							currentDesktop = "KDE";
						else if (dataDirs.Contains("gnome"))
							currentDesktop = "Gnome";
					}
					if (IsNullOrEmpty(currentDesktop))
						currentDesktop = Environment.GetEnvironmentVariable("GDMSESSION") ?? Empty;
				}
				return currentDesktop.ToLowerInvariant();
			}
		}

		private static string s_linuxDesktop;
		/// <summary>
		/// Get the currently running desktop environment (like Unity, Gnome shell etc)
		/// </summary>
		private static string LinuxDesktop
		{
			get
			{
				if (Environment.OSVersion.Platform != PlatformID.Unix)
					return Empty;
				if (s_linuxDesktop == null)
				{
					// see http://unix.stackexchange.com/a/116694
					// and http://askubuntu.com/a/227669
					var currentDesktop = DesktopEnvironment;
					var mirSession = Environment.GetEnvironmentVariable("MIR_SERVER_NAME");
					var additionalInfo = Empty;
					if (!IsNullOrEmpty(mirSession))
						additionalInfo = " [display server: Mir]";
					var gdmSession = Environment.GetEnvironmentVariable("GDMSESSION") ?? "not set";
					s_linuxDesktop = $"{currentDesktop} ({gdmSession}{additionalInfo})";
				}
				return s_linuxDesktop;
			}
		}

		public static Statistics Statistics => s_singleton.Client.Statistics;

		/// <summary>
		/// All calls to Client.Track should run through here so we can provide defaults for every event
		/// </summary>
		private static void TrackWithApplicationProperties(string eventName, JsonObject properties = null)
		{
			if (s_singleton == null)
			{
				throw new ApplicationException("The application must first construct a single Analytics object");
			}
			if (properties == null)
				properties = new JsonObject();
			foreach (var p in s_singleton._propertiesThatGoWithEveryEvent)
			{
				if (properties.ContainsKey(p.Key))
					properties.Remove(p.Key);
				properties.Add(p.Key, p.Value ?? Empty);
			}
			s_singleton.Client.Track(AnalyticsSettings.Default.IdForAnalytics, eventName, properties);
		}

		/// <summary>
		/// Add a property that says something about the application, which goes out with every event.
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		public static void SetApplicationProperty(string key, string value)
		{
			if (IsNullOrEmpty(key))
				throw new ArgumentNullException(key);
			if (value == null)
				value = Empty;
			if (s_singleton._propertiesThatGoWithEveryEvent.ContainsKey(key))
			{
				s_singleton._propertiesThatGoWithEveryEvent.Remove(key);
			}
			s_singleton._propertiesThatGoWithEveryEvent.Add(key, value);
		}

		private static string GetUserNameForEvent()
		{
			return s_userInfo == null ? "unknown" :
				(IsNullOrWhiteSpace(s_userInfo.FirstName) ? "" : s_userInfo.FirstName + " ") + s_userInfo.LastName;
		}
		#endregion

		public static void FlushClient()
		{
			s_singleton.Client.Flush();
		}
	}
}
