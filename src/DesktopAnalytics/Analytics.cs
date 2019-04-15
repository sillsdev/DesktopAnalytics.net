//License: MIT

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Xml.Linq;
using System.Xml.XPath;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Segment.Model;

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

		private static Options _options;
		private static Traits _traits;
		private static UserInfo _userInfo;
		private static Analytics _singleton;
		private Dictionary<string, string> _propertiesThatGoWithEveryEvent;
		private static int _exceptionCount = 0;
		const int MAX_EXCEPTION_REPORTS_PER_RUN = 10;

		public Analytics(string apiSecret, UserInfo userInfo, bool allowTracking = true, bool retainPii = false)
			: this(apiSecret, userInfo, new Dictionary<string, string>(), allowTracking, retainPii)
		{

		}

		/// <summary>
		/// Initialized a singleton; after calling this, use Analytics.Track() for each event.
		/// </summary>
		/// <param name="apiSecret">The segment.com apiSecret</param>
		/// <param name="userInfo">Information about the user that you have previous collected</param>
		/// <param name="propertiesThatGoWithEveryEvent">A set of key-value pairs to send with *every* event</param>
		/// <param name="allowTracking">If false, this will not do any communication with segment.io</param>
		/// <param name="retainPii">If false, userInfo will be stripped/hashed/adjusted to prevent communication of
		/// personally identifiable information to the analytics server.</param>
		public Analytics(string apiSecret, UserInfo userInfo, Dictionary<string, string> propertiesThatGoWithEveryEvent, bool allowTracking = true, bool retainPii = false)
		{
			if (_singleton != null)
			{
				throw new ApplicationException("You can only construct a single Analytics object.");
			}
			_singleton = this;
			_propertiesThatGoWithEveryEvent = propertiesThatGoWithEveryEvent;

			if (retainPii)
				_userInfo = userInfo;
			else
			{
				var firstName = userInfo.FirstName;
				if (!String.IsNullOrWhiteSpace(firstName))
					firstName = firstName[0].ToString() + firstName.GetHashCode();
				var lastName = userInfo.LastName;
				if (!String.IsNullOrWhiteSpace(lastName))
					lastName = lastName[0].ToString() + lastName.GetHashCode();
				var emailDomain = userInfo.Email;
				if (emailDomain != null)
				{
					var charactersToStrip = emailDomain.IndexOf("@", StringComparison.Ordinal);
					if (charactersToStrip > 0)
						emailDomain = emailDomain.Substring(charactersToStrip);
				}
				_userInfo = new UserInfo { FirstName = firstName, LastName = lastName, Email = emailDomain, OtherProperties = userInfo.OtherProperties };
			}

			AllowTracking = allowTracking;

			// UrlThatReturnsExternalIpAddress is a static and should really be set before this is called, so don't mess with it if the client has given us a different url to us
			if (string.IsNullOrEmpty(UrlThatReturnsExternalIpAddress))
				UrlThatReturnsGeolocationJson = "http://ip-api.com/json/";

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

			if (string.IsNullOrEmpty(AnalyticsSettings.Default.IdForAnalytics))
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

			Segment.Analytics.Initialize(apiSecret);
			Segment.Analytics.Client.Failed += Client_Failed;
			Segment.Analytics.Client.Succeeded += Client_Succeeded;

			if (string.IsNullOrEmpty(AnalyticsSettings.Default.IdForAnalytics))
			{

				AnalyticsSettings.Default.IdForAnalytics = Guid.NewGuid().ToString();
				AnalyticsSettings.Default.Save();
			}

			var context = new Context();
			context.Add("language", _userInfo.UILanguageCode);

			_options = new Options();
			_options.SetContext(context);

			UpdateSegmentIOInformationOnThisUser();
			ReportIpAddressOfThisMachineAsync(); //this will take a while and may fail, so just do it when/if we can
			string versionNumberWithBuild = "";
			try
			{
				versionNumberWithBuild = System.Reflection.Assembly.GetEntryAssembly().GetName().Version.ToString();
			}
			catch (NullReferenceException)
			{
				try
				{
					// GetEntryAssembly is null for MAF plugins
					versionNumberWithBuild = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
				}
				catch (NullReferenceException)
				{
					// This probably can't happen, but if it does, just roll with it.
				}
			}
			string versionNumber = versionNumberWithBuild.Split('.').Take(2).Aggregate((a, b) => a + "." + b);
			SetApplicationProperty("Version", versionNumber);
			SetApplicationProperty("FullVersion", versionNumberWithBuild);
			SetApplicationProperty("UserName", GetUserNameForEvent());
			SetApplicationProperty("Browser", GetOperatingSystemLabel());


			if (string.IsNullOrEmpty(AnalyticsSettings.Default.LastVersionLaunched))
			{
				//"Created" is a special property that segment.io understands and coverts to equivalents in various analytics services
				//So it's not as descriptive for us as "FirstLaunchOnSystem", but it will give the best experience on the analytics sites.
				TrackWithApplicationProperties("Created");
			}
			else if (AnalyticsSettings.Default.LastVersionLaunched != versionNumberWithBuild)
			{
				TrackWithApplicationProperties("Upgrade", new Properties
				{
					{"OldVersion", AnalyticsSettings.Default.LastVersionLaunched},
				});
			}

			// We want to record the launch event independent of whether we also recorded a special first launch
			// But that is done after we retrieve (or fail to retrieve) our external ip address.
			// See http://issues.bloomlibrary.org/youtrack/issue/BL-4011.

			AnalyticsSettings.Default.LastVersionLaunched = versionNumberWithBuild;
			AnalyticsSettings.Default.Save();

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

			string settingsLocation;
			string softwareName;

			for (int attempt = 0; attempt < 2; attempt++)
			{
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
						if (string.IsNullOrEmpty(analyticsId))
							continue;
						AnalyticsSettings.Default.IdForAnalytics = analyticsId;
						AnalyticsSettings.Default.FirstName = ExtractSetting(AnalyticsSettings.Default.FirstName, doc, "FirstName");
						AnalyticsSettings.Default.LastName = ExtractSetting(AnalyticsSettings.Default.LastName, doc, "LastName");
						AnalyticsSettings.Default.LastVersionLaunched = ExtractSetting(AnalyticsSettings.Default.LastVersionLaunched, doc, "LastVersionLaunched");
						AnalyticsSettings.Default.Email = ExtractSetting(AnalyticsSettings.Default.Email, doc, "Email");
						AnalyticsSettings.Default.Save();
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
			if (companyAttribute == null || string.IsNullOrEmpty(softwareName))
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
			if (!string.IsNullOrEmpty(current))
				return current;
			var setting =
				doc.XPathSelectElement(
					"configuration/userSettings/DesktopAnalytics.AnalyticsSettings/setting[@name='" + name + "']");
			if (setting == null)
				return "";
			return setting.Value;
		}

		private static void UpdateSegmentIOInformationOnThisUser()
		{
			_traits = new Traits()
			{
				{"lastName", _userInfo.LastName},
				{"firstName", _userInfo.FirstName},
				{"Email", _userInfo.Email},
				{"UILanguage", _userInfo.UILanguageCode},
	            //segmentio collects this in context, but doesn't seem to convey it to MixPanel
	            {"$browser", GetOperatingSystemLabel()}
			};
			foreach (var property in _userInfo.OtherProperties)
			{
				if (!string.IsNullOrWhiteSpace(property.Value))
					_traits.Add(property.Key, property.Value);
			}

			if (!AllowTracking)
				return;

			Segment.Analytics.Client.Identify(AnalyticsSettings.Default.IdForAnalytics, _traits, _options);
		}

		/// <summary>
		/// Use this after showing a registration dialog, so that this stuff is sent right away, rather than the next time you start up Analytics
		/// </summary>
		public static void IdentifyUpdate(UserInfo userInfo)
		{
			_userInfo = userInfo;
			UpdateSegmentIOInformationOnThisUser();
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
					Uri uri;
					bool json = string.IsNullOrEmpty(UrlThatReturnsExternalIpAddress);
					Uri.TryCreate(json ? UrlThatReturnsGeolocationJson : UrlThatReturnsExternalIpAddress, UriKind.Absolute, out uri);
					client.DownloadDataCompleted += (object sender, DownloadDataCompletedEventArgs e) =>
					{
						var launchProperties = new Properties {{"installedUiLangId", CultureInfo.InstalledUICulture.ThreeLetterISOLanguageName}};

						try
						{
							var result = System.Text.Encoding.UTF8.GetString(e.Result).Trim();
							if (json)
							{
								Debug.WriteLine(String.Format("DesktopAnalytics: geolocation JSON data = {0}", result));
								var j = new JTokenReader(JToken.Parse(result));
								AddGeolocationProperty(j, "city");
								AddGeolocationProperty(j, "country", "countryCode");
								AddGeolocationProperty(j, "regionName", "region");
							}
							else
							{
								Debug.WriteLine(String.Format("DesktopAnalytics: external ip = {0}", result));
								_options.Context.Add("ip", result);
								_propertiesThatGoWithEveryEvent.Add("ip", result);
							}
						}
						catch (Exception)
						{
							// we get here when the user isn't online, or anything else prevents us from
							// getting their ip. Still worth reporting the launch in the latter case.
							TrackWithApplicationProperties("Launch", launchProperties);
							return;
						}
						UpdateSegmentIOInformationOnThisUser();
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

		private bool AddGeolocationProperty(JTokenReader j, string primary, string secondary = null)
		{
			var value = j.CurrentToken.SelectToken(primary, false)?.Values<string>().FirstOrDefault();
			if (!String.IsNullOrWhiteSpace(value))
			{
				_options.Context.Add(primary, value);
				_propertiesThatGoWithEveryEvent.Add(primary, value);
				return true;
			}
			return (secondary != null) && AddGeolocationProperty(j, secondary);
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
		/// Record an event
		/// </summary>
		///	Analytics.RecordEvent("Save PDF");
		/// <param name="eventName"></param>
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

			TrackWithApplicationProperties(eventName, MakeSegmentIOProperties(properties));
		}

		/// <summary>
		/// Sends the exception's message and stacktrace
		/// </summary>
		/// <param name="e"></param>
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

			_exceptionCount++;

			// we had an incident where some problem caused a user to emit hundreds of thousands of exceptions,
			// in the background, blowing through our Analytics service limits and getting us kicked off.
			if (_exceptionCount > MAX_EXCEPTION_REPORTS_PER_RUN)
			{
				return;
			}

			var props = new Properties()
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
			Debug.WriteLine("SegmentIO succeeded: " + action.Type);
		}

		private static void Client_Failed(BaseAction action, Exception e)
		{
			Debug.WriteLine("**** Segment.IO Failed to deliver");
		}

		public void Dispose()
		{
			if (Segment.Analytics.Client != null)
			{
				// BL-5276 indicated that some events shortly before program termination were not being sent.
				// The documentation is ambiguous about whether Flush() needs to be called before Dispose(),
				// but source code at https://github.com/segmentio/Analytics.NET/blob/master/Analytics/Client.cs
				// clearly says "Note, this does not call Flush() first".
				// So to be sure of getting all our events we should call it.
				Segment.Analytics.Client.Flush();
				Segment.Analytics.Client.Dispose();
			}
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
			if (Environment.OSVersion.Platform == PlatformID.Unix)
			{
				if (UnixName == "Linux")
					return String.Format("{0} / {1}", LinuxVersion, LinuxDesktop);
				else
					return UnixName;    // Maybe "Darwin" for a Mac?
			}
			var list = new List<Version>();
			list.Add(new Version(System.PlatformID.Win32NT, 5, 0, "Windows 2000"));
			list.Add(new Version(System.PlatformID.Win32NT, 5, 1, "Windows XP"));
			list.Add(new Version(System.PlatformID.Win32NT, 6, 0, "Vista"));
			list.Add(new Version(System.PlatformID.Win32NT, 6, 1, "Windows 7"));
			list.Add(new Version(System.PlatformID.Win32NT, 6, 2, "Windows 8"));
			list.Add(new Version(System.PlatformID.Win32NT, 6, 3, "Windows 8.1"));
			list.Add(new Version(System.PlatformID.Win32NT, 10, 0, "Windows 10"));
			foreach (var version in list)
			{
				if (version.Match(System.Environment.OSVersion))
					return version.Label;// +" " + Environment.OSVersion.ServicePack;
			}
			return System.Environment.OSVersion.VersionString;
		}

		[System.Runtime.InteropServices.DllImport("libc")]
		static extern int uname(IntPtr buf);
		private static string _unixName;
		private static string UnixName
		{
			get
			{
				if (Environment.OSVersion.Platform != PlatformID.Unix)
					return String.Empty;
				if (_unixName == null)
				{
					IntPtr buf = IntPtr.Zero;
					try
					{
						// This is a hacktastic way of getting sysname from uname()
						buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(8192);
						if (uname(buf) == 0)
							_unixName = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(buf);
					}
					catch
					{
						_unixName = String.Empty;
					}
					finally
					{
						if (buf != IntPtr.Zero)
							System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
					}
				}
				return _unixName;
			}
		}

		private static string _linuxVersion;
		private static string LinuxVersion
		{
			get
			{
				if (Environment.OSVersion.Platform != PlatformID.Unix)
					return String.Empty;
				if (_linuxVersion == null)
				{
					_linuxVersion = String.Empty;
					if (File.Exists("/etc/wasta-release"))
					{
						var versionData = File.ReadAllText("/etc/wasta-release");
						var versionLines = versionData.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
						for (int i = 0; i < versionLines.Length; ++i)
						{
							if (versionLines[i].StartsWith("DESCRIPTION=\""))
							{
								_linuxVersion = versionLines[i].Substring(13).Trim(new char[] { '"' });
								break;
							}
						}
					}
					else if (File.Exists("/etc/lsb-release"))
					{
						var versionData = File.ReadAllText("/etc/lsb-release");
						var versionLines = versionData.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
						for (int i = 0; i < versionLines.Length; ++i)
						{
							if (versionLines[i].StartsWith("DISTRIB_DESCRIPTION=\""))
							{
								_linuxVersion = versionLines[i].Substring(21).Trim(new char[] { '"' });
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
				if (string.IsNullOrEmpty(currentDesktop))
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
					if (string.IsNullOrEmpty(currentDesktop))
						currentDesktop = Environment.GetEnvironmentVariable("GDMSESSION") ?? string.Empty;
				}
				return currentDesktop.ToLowerInvariant();
			}
		}

		private static string _linuxDesktop;
		/// <summary>
		/// Get the currently running desktop environment (like Unity, Gnome shell etc)
		/// </summary>
		private static string LinuxDesktop
		{
			get
			{
				if (Environment.OSVersion.Platform != PlatformID.Unix)
					return string.Empty;
				if (_linuxDesktop == null)
				{
					// see http://unix.stackexchange.com/a/116694
					// and http://askubuntu.com/a/227669
					var currentDesktop = DesktopEnvironment;
					var mirSession = Environment.GetEnvironmentVariable("MIR_SERVER_NAME");
					var additionalInfo = string.Empty;
					if (!string.IsNullOrEmpty(mirSession))
						additionalInfo = " [display server: Mir]";
					var gdmSession = Environment.GetEnvironmentVariable("GDMSESSION") ?? "not set";
					_linuxDesktop = String.Format("{0} ({1}{2})", currentDesktop, gdmSession, additionalInfo);
				}
				return _linuxDesktop;
			}
		}

		/// <summary>
		/// All calls to Segment.Analytics.Client.Track should run through here so we can provide defaults for every event
		/// </summary>
		private static void TrackWithApplicationProperties(string eventName, Properties properties = null)
		{
			if (_singleton == null)
			{
				throw new ApplicationException("The application must first construct a single Analytics object");
			}
			if (properties == null)
				properties = new Properties();
			foreach (var p in _singleton._propertiesThatGoWithEveryEvent)
			{
				if (properties.ContainsKey(p.Key))
					properties.Remove(p.Key);
				properties.Add(p.Key, p.Value ?? string.Empty);
			}
			Segment.Analytics.Client.Track(AnalyticsSettings.Default.IdForAnalytics, eventName, properties);
		}

		/// <summary>
		/// Add a property that says something about the application, which goes out with every event.
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		public static void SetApplicationProperty(string key, string value)
		{
			if (string.IsNullOrEmpty(key))
				throw new ArgumentNullException(key);
			if (value == null)
				value = string.Empty;
			if (_singleton._propertiesThatGoWithEveryEvent.ContainsKey(key))
			{
				_singleton._propertiesThatGoWithEveryEvent.Remove(key);
			}
			_singleton._propertiesThatGoWithEveryEvent.Add(key, value);
		}

		private static string GetUserNameForEvent()
		{
			return _userInfo == null ? "unknown" :
				(String.IsNullOrWhiteSpace(_userInfo.FirstName) ? "" : _userInfo.FirstName + " ") + _userInfo.LastName;
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
		public string FirstName = "";
		public string LastName = "";
		public string Email = "";
		public string UILanguageCode = "";
		public Dictionary<string, string> OtherProperties = new Dictionary<string, string>();
	}
}
