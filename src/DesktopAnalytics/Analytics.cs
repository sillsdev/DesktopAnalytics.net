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
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml.Linq;
using System.Xml.XPath;
using JetBrains.Annotations;
using Newtonsoft.Json.Linq;
using Segment.Serialization;
using static System.Attribute;
using static System.Configuration.ConfigurationUserLevel;
using static System.Environment;
using static System.Environment.SpecialFolder;
using static System.IO.Directory;
using static System.IO.Path;
using static System.Reflection.Assembly;
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
		private const int kMaxExceptionReportsPerRun = 10;

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

		private class InitializationParameters
		{
			public string ApiSecret;
			public string Host;
			public int FlushAt;
			public int FlushInterval;
			public Assembly Assembly;
		}

		private readonly IClient _client;
		private volatile InitializationParameters _deferredInitializationParameters;

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
		public Analytics(
			string apiSecret,
			UserInfo userInfo,
			bool allowTracking = true,
			bool retainPii = false,
			ClientType clientType = ClientType.Segment,
			string host = null,
			bool useCallingAssemblyVersion = false) : this(
				apiSecret,
				userInfo,
				new Dictionary<string, string>(),
				allowTracking,
				retainPii,
				clientType,
				host,
				assemblyToUseForVersion: useCallingAssemblyVersion ? GetCallingAssembly() : null
			)
		{
		}

		private void UpdateServerInformationOnThisUser(bool initializing = false)
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

			if (!AllowTracking && !initializing)
				return;

			_client.Identify(AnalyticsSettings.Default.IdForAnalytics, s_traits, s_locationInfo);
		}

		/// <summary>
		/// Initialized a singleton; after calling this, use Analytics.Track() for each event.
		/// </summary>
		/// <param name="apiSecret">The segment.com apiSecret</param>
		/// <param name="userInfo">Information about the user that you have previously collected</param>
		/// <param name="propertiesThatGoWithEveryEvent">A set of key-value pairs to send with
		/// *every* event</param>
		/// <param name="allowTracking">If false, prevents communication with segment.io</param>
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
		public Analytics(
			string apiSecret,
			UserInfo userInfo,
			Dictionary<string, string> propertiesThatGoWithEveryEvent,
			bool allowTracking = true,
			bool retainPii = false,
			ClientType clientType = ClientType.Segment,
			string host = null,
			int flushAt = -1,
			int flushInterval = -1,
			Assembly assemblyToUseForVersion = null
		)
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
					_client = segmentClient;
					break;
				}
				case ClientType.Mixpanel:
				{
					_client = new MixpanelClient();
					break;
				}
				default:
				{
					throw new ArgumentException("Unknown client type", nameof(clientType));
				}
			}
			_propertiesThatGoWithEveryEvent = propertiesThatGoWithEveryEvent;

			s_userInfo = retainPii ? userInfo : userInfo.CreateSanitized();

			var initializationParameters = new InitializationParameters
			{
				ApiSecret = apiSecret,
				Host = host,
				FlushAt = flushAt,
				FlushInterval = flushInterval,
				Assembly = assemblyToUseForVersion ?? GetEntryAssembly(),
			};

			// UrlThatReturnsExternalIpAddress is a static and should really be set before this is
			// called, so don't mess with it if the client has given us a different URL.
			if (IsNullOrEmpty(UrlThatReturnsExternalIpAddress))
				UrlThatReturnsGeolocationJson = "http://ip-api.com/json/";

			if (allowTracking)
				Initialize(initializationParameters);
			else
			{
				s_allowTracking = false;
				_deferredInitializationParameters = initializationParameters;
			}
		}

		private void Initialize(InitializationParameters parameters)
		{
			// Bring in settings from any previous version.
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
				// Apparently a first-time installation. If possible, we really want to use the
				// same ID (from another channel of this app) to keep our statistics valid.
				try
				{
					AttemptToGetUserIdSettingsFromDifferentChannel();
				}
				catch (Exception)
				{
					// Oh, well, we tried.
				}
			}

			_client.Initialize(
				parameters.ApiSecret,
				parameters.Host,
				parameters.FlushAt,
				parameters.FlushInterval
			);

			if (IsNullOrEmpty(AnalyticsSettings.Default.IdForAnalytics))
			{
				AnalyticsSettings.Default.IdForAnalytics = Guid.NewGuid().ToString();
				TrySaveSettings();
			}

			s_locationInfo = new JsonObject();

			UpdateServerInformationOnThisUser(true);
			ReportIpAddressOfThisMachineAsync(); //this will take a while and may fail, so just do it when/if we can

			var assembly = parameters.Assembly;
			var version = assembly?.GetName().Version;
			var versionNumberWithBuild = version?.ToString() ?? "";
			var versionNumber = version == null ? "" : $"{version.Major}.{version.Minor}";
			SetApplicationProperty("Version", versionNumber);
			SetApplicationProperty("FullVersion", versionNumberWithBuild);
			SetApplicationProperty("UserName", GetUserNameForEvent());
			SetApplicationProperty("Browser", GetOperatingSystemLabel());
			SetApplicationProperty("OS Version Number", GetOperatingSystemVersionLabel());
			SetApplicationProperty("64bit OS", Is64BitOperatingSystem.ToString());
			SetApplicationProperty("64bit App", Is64BitProcess.ToString());
			// This (and "64bit OS" above) really belong in Context, but segment.io doesn't seem
			// to convey context to Mixpanel in a reliable/predictable form.
			var ci = CultureInfo.CurrentUICulture;
			var installedUICulture = GetInstalledUICultureCode(ci);
			SetApplicationProperty("DeviceUILanguage", installedUICulture);

			// This method only ever gets called when the client has requested that we allow
			// tracking. We can set this flag to true now that the client is created and our user
			// and application properties are initialized. In practice, we hope that no other
			// tracking events come through before our Created/Upgrade event below, but in the
			// unlikely event that they do, it's not the end of the world.
			s_allowTracking = true;

			if (IsNullOrEmpty(AnalyticsSettings.Default.LastVersionLaunched))
			{
				// "Created" is a special property that segment.io understands and coverts to
				// equivalents in various analytics services. So it's not as descriptive for us as
				// "FirstLaunchOnSystem", but it will give the best experience on the analytics sites.
				TrackWithApplicationProperties("Created");
			}
			else if (AnalyticsSettings.Default.LastVersionLaunched != versionNumberWithBuild)
			{
				TrackWithApplicationProperties("Upgrade", new JsonObject
				{
					{"OldVersion", AnalyticsSettings.Default.LastVersionLaunched},
				});
			}

			// We want to record the launch event even if we also recorded a special first launch,
			// but that is done after we retrieve (or fail to retrieve) our external IP address.
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
						// Ignore and retry.
					}
					Thread.Sleep(300);
				}
			} while (++retryCount < 3);
		}

		private void AttemptToGetUserIdSettingsFromDifferentChannel()
		{
			// We need to get the company name and exe name of the main application, without
			// introducing a dependency on Windows.Forms, so we can't use the
			// Windows.Forms.Application methods. For maximum robustness, we try two different
			// approaches.
			// REVIEW: The first approach will NOT work for plugins or calls from unmanaged code,
			// but for maximum compatibility (to keep from breaking Bloom), we try it first. If it
			// fails, we try the second approach, which should work for everyone (though until
			// there is a plugin that supports channels, it will presumably never actually find a
			// pre-existing config file from a different channel).

			for (int attempt = 0; attempt < 2; attempt++)
			{
				string settingsLocation;
				string softwareName;
				if (attempt == 0)
				{
					if (!TryGetSettingsLocationFromEntryAssembly(out settingsLocation, out softwareName))
						continue;
				}
				else
				{
					if (!TryGetDefaultSettingsLocation(out settingsLocation, out softwareName))
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
				var possibleParentFolders = GetDirectories(settingsLocation, pattern);
				var possibleFolders = new List<string>();
				foreach (var folder in possibleParentFolders)
					possibleFolders.AddRange(GetDirectories(folder).Where(f => File.Exists(Combine(f, kUserConfigFileName))));

				possibleFolders.Sort((first, second) =>
					{
						if (first == second)
							return 0;
						var firstConfigPath = Combine(first, kUserConfigFileName);
						var secondConfigPath = Combine(second, kUserConfigFileName);
						// Reversing the arguments like this means that second comes before first if it has a LARGER mod time.
						// That is, we end up with the most recently modified user.config first.
						return new FileInfo(secondConfigPath).LastWriteTimeUtc.CompareTo(
							new FileInfo(firstConfigPath).LastWriteTimeUtc
						);
					});

				foreach (var folder in possibleFolders)
				{
					try
					{
						var doc = XDocument.Load(Combine(folder, kUserConfigFileName));
						var idSetting = doc.XPathSelectElement(
							"configuration/userSettings/DesktopAnalytics.AnalyticsSettings/setting[@name='IdForAnalytics']"
						);
						if (idSetting == null)
							continue;
						string analyticsId = idSetting.Value;
						if (IsNullOrEmpty(analyticsId))
							continue;
						AnalyticsSettings.Default.IdForAnalytics = analyticsId;
						AnalyticsSettings.Default.FirstName = ExtractSetting(
							AnalyticsSettings.Default.FirstName,
							doc,
							"FirstName"
						);
						AnalyticsSettings.Default.LastName = ExtractSetting(
							AnalyticsSettings.Default.LastName,
							doc,
							"LastName"
						);
						AnalyticsSettings.Default.LastVersionLaunched = ExtractSetting(
							AnalyticsSettings.Default.LastVersionLaunched,
							doc,
							"LastVersionLaunched"
						);
						AnalyticsSettings.Default.Email = ExtractSetting(
							AnalyticsSettings.Default.Email,
							doc,
							"Email"
						);
						TrySaveSettings();
						return;
					}
					catch (Exception)
					{
						// If anything goes wrong, skip trying to get our ID from this source.
					}
				}
			}
		}

		private static bool TryGetSettingsLocationFromEntryAssembly(out string settingsLocation,
			out string softwareName
		)
		{
			settingsLocation = null;
			softwareName = null;

			var entryAssembly = GetEntryAssembly(); // the main exe assembly
			if (entryAssembly == null) // Called from unmanaged code?
				return false;
			softwareName = GetFileNameWithoutExtension(entryAssembly.Location);
			if (!(GetCustomAttribute(entryAssembly, typeof(AssemblyCompanyAttribute)) is
				    AssemblyCompanyAttribute companyAttribute) || IsNullOrEmpty(softwareName))
			{
				return false;
			}

			string companyName = companyAttribute.Company;
			if (companyName == null)
				return false;
			settingsLocation = Combine(GetFolderPath(LocalApplicationData), companyName);
			return true;
		}

		private static bool TryGetDefaultSettingsLocation(out string settingsLocation, out string softwareName)
		{
			settingsLocation = null;
			softwareName = null;
			try
			{
				var userConfigPath = GetUserConfigPath();
				if (GetFileName(userConfigPath) != kUserConfigFileName)
					return false;
				userConfigPath = GetDirectoryName(GetDirectoryName(userConfigPath)); // strip file name and last folder level
				softwareName = GetFileName(userConfigPath); // This is actually a folder, not a file.
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
				settingsLocation = GetDirectoryName(userConfigPath); // strip product folder

				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static string GetUserConfigPath()
		{
			try
			{
				return ConfigurationManager.OpenExeConfiguration(PerUserRoamingAndLocal).FilePath;
			}
			catch (ConfigurationErrorsException ex)
			{
				// If the user.config file is corrupt then it will throw a ConfigurationErrorsException
				// Fortunately we can still get its path from the exception.
				return ex.Filename;
			}
		}

		internal static string GetInstalledUICultureCode(CultureInfo ci)
		{
			const string invariantCulture = "iv";
			return !IsNullOrEmpty(ci.TwoLetterISOLanguageName) &&
				ci.TwoLetterISOLanguageName != invariantCulture
				? ci.TwoLetterISOLanguageName
				: ci.ThreeLetterISOLanguageName;
		}

		// If the specified setting's current value is empty, try to extract it from the document.
		internal static string ExtractSetting(string current, XDocument doc, string name)
		{
			if (!IsNullOrEmpty(current))
				return current;
			var setting = doc.XPathSelectElement(
				$"configuration/userSettings/DesktopAnalytics.AnalyticsSettings/setting[@name='{name}']"
			);
			return setting?.Value ?? "";
		}

		/// <summary>
		/// Use this after showing a registration dialog, so that this stuff is sent right away,
		/// rather than the next time you start up Analytics
		/// </summary>
		[PublicAPI]
		public static void IdentifyUpdate(UserInfo userInfo)
		{
			s_userInfo = userInfo;
			s_singleton.UpdateServerInformationOnThisUser();
		}

		/// <summary>
		/// Override this if you want your analytics to report an actual IP address to the server
		/// (which could be considered PII), rather than just the general geolocation info. The
		/// service should simply return a page with a body containing the ip address alone.
		/// </summary>
		/// <remarks>This used to default to "http://icanhazip.com";
		/// formerly: "http://ipecho.net/plain" (that URL went down, but is now back up)</remarks>
		public static string UrlThatReturnsExternalIpAddress { get; set; }

		/// <summary>
		/// Override this for any reason you like, including if the built-in one
		/// (http://ip-api.com/json/) stops working some day. This will be ignored if
		/// <see cref="UrlThatReturnsExternalIpAddress"/> is set. The service should return json
		/// that contains values for one or more of the following names: city, country,
		/// countryCode, region, regionName.
		/// </summary>
		public static string UrlThatReturnsGeolocationJson { get; set; }

		private void ReportIpAddressOfThisMachineAsync()
		{
			using (var client = new WebClient())
			{
				try
				{
					var url = UrlThatReturnsExternalIpAddress;
					var useGeoLocation = IsNullOrEmpty(url);
					if (useGeoLocation)
						url = UrlThatReturnsGeolocationJson;
					Uri.TryCreate(url, UriKind.Absolute, out var uri);
					client.DownloadDataCompleted += (sender, e) =>
					{
						var launchProperties = new JsonObject
						{
							{
								"installedUiLangId",
								CultureInfo.InstalledUICulture.ThreeLetterISOLanguageName
							},
						};

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
							// We get here when the user isn't online, or anything else prevents us
							// from getting their IP address or location. Still worth reporting the
							// launch in the latter case.
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
			if (IsNullOrWhiteSpace(value))
				return secondary != null && AddGeolocationProperty(j, secondary);

			s_locationInfo.Add(primary, value);
			_propertiesThatGoWithEveryEvent.Add(primary, value);
			return true;
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
			if (s_exceptionCount > kMaxExceptionReportsPerRun)
			{
				return;
			}

			var props = new JsonObject
			{
				{ "Message", e.Message },
				{ "Stack Trace", e.StackTrace }
			};
			if (moreProperties != null)
			{
				foreach (var key in moreProperties.Keys)
					props.Add(key, moreProperties[key]);
			}
			TrackWithApplicationProperties("Exception", props);
		}

		private static JsonObject MakeSegmentIOProperties(Dictionary<string, string> properties)
		{
			var prop = new JsonObject();
			foreach (var key in properties.Keys)
				prop.Add(key, properties[key]);
			return prop;
		}

		private static void Client_Failed(Exception e)
		{
			Debug.WriteLine($"**** Analytics action Failed: {NewLine}{e.StackTrace}");
		}

		public void Dispose()
		{
			_client?.ShutDown();
		}

		/// <summary>
		/// Indicates whether we are tracking or not
		/// </summary>
		public static bool AllowTracking
		{
			get => s_allowTracking;
			set
			{
				if (value == s_allowTracking)
					return;

				// The following is not strictly thread safe because another thread could set this
				// after the singleton is created but before it checks the flag to decide whether
				// to complete initialization based on the value of the flag. In practice, the
				// Analytics object is normally constructed during application startup, before
				// there is any real chance of multiple threads running.
				if (value && s_singleton != null)
				{
					var initializationParameters = s_singleton._deferredInitializationParameters;
					if (initializationParameters != null)
					{
						// By clearing these parameters, we reduce the chance that we will try to
						// initialize twice if multiple threads are setting AllowTracking to true.
						s_singleton._deferredInitializationParameters = null;
						s_singleton.Initialize(initializationParameters);
						return; // Initialize sets s_allowTracking = true
					}
				}

				s_allowTracking = value;
			}
		}

		private static bool s_allowTracking;

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
			if (OSVersion.Platform == PlatformID.Unix)
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
				if (version.Match(OSVersion))
					return version.Label + " " + OSVersion.ServicePack;
			}

			// Handle any as yet unrecognized (possibly unmanifested) versions, or anything that reported its self as Windows 8.
			if (OSVersion.Platform == PlatformID.Win32NT)
			{
				return GetWindowsVersionInfoFromNetworkAPI() + " " + OSVersion.ServicePack;
			}

			return OSVersion.VersionString;
		}

		private string GetOperatingSystemVersionLabel()
		{
			return OSVersion.Version.ToString();
		}

		#region Windows8PlusVersionReportingSupport
		[DllImport("netapi32.dll", CharSet = CharSet.Auto)]
		private static extern int NetWkstaGetInfo(string server, int level, out IntPtr info);

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
				if (OSVersion.Platform != PlatformID.Unix)
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

		private static string s_linuxVersion;
		private static string LinuxVersion
		{
			get
			{
				if (OSVersion.Platform != PlatformID.Unix)
					return Empty;
				if (s_linuxVersion != null)
					return s_linuxVersion;

				s_linuxVersion = DetermineLinuxVersion();
				return s_linuxVersion;
			}
		}

		private static string DetermineLinuxVersion()
		{
			var candidates = new[]
			{
				(FilePath: "/etc/wasta-release", Prefix: "DESCRIPTION=\""),
				(FilePath: "/etc/lsb-release", Prefix: "DISTRIB_DESCRIPTION=\"")
			};

			foreach (var candidate in candidates)
			{
				if (File.Exists(candidate.FilePath))
				{
					var line = File.ReadLines(candidate.FilePath)
						.FirstOrDefault(l => l.StartsWith(candidate.Prefix));
					if (line != null)
						return line.Substring(candidate.Prefix.Length).Trim('"');
				}
			}

			// Fallback, but if it's linux, it really should have /etc/lsb-release!
			return OSVersion.VersionString;
		}

		/// <summary>
		/// On a Unix machine this gets the current desktop environment (gnome/xfce/...), on
		/// non-Unix machines the platform name.
		/// </summary>
		private static string DesktopEnvironment
		{
			get
			{
				if (OSVersion.Platform != PlatformID.Unix)
					return OSVersion.Platform.ToString();

				// see http://unix.stackexchange.com/a/116694
				// and http://askubuntu.com/a/227669
				var currentDesktop = GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
				if (IsNullOrEmpty(currentDesktop))
				{
					var dataDirs = GetEnvironmentVariable("XDG_DATA_DIRS");
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
						currentDesktop = GetEnvironmentVariable("GDMSESSION") ?? Empty;
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
				if (OSVersion.Platform != PlatformID.Unix)
					return Empty;
				if (s_linuxDesktop == null)
				{
					// see http://unix.stackexchange.com/a/116694
					// and http://askubuntu.com/a/227669
					var currentDesktop = DesktopEnvironment;
					var mirSession = GetEnvironmentVariable("MIR_SERVER_NAME");
					var additionalInfo = Empty;
					if (!IsNullOrEmpty(mirSession))
						additionalInfo = " [display server: Mir]";
					var gdmSession = GetEnvironmentVariable("GDMSESSION") ?? "not set";
					s_linuxDesktop = $"{currentDesktop} ({gdmSession}{additionalInfo})";
				}
				return s_linuxDesktop;
			}
		}

		public static Statistics Statistics => s_singleton._client?.Statistics ?? new Statistics(0, 0, 0);

		/// <summary>
		/// All calls to Client.Track should run through here so we can provide defaults for every event
		/// </summary>
		private static void TrackWithApplicationProperties(string eventName, JsonObject properties = null)
		{
			if (s_singleton == null)
				throw new ApplicationException("The application must first construct a single Analytics object");

			if (!AllowTracking)
				return;

			if (properties == null)
				properties = new JsonObject();

			foreach (var p in s_singleton._propertiesThatGoWithEveryEvent)
			{
				properties.Remove(p.Key);
				properties.Add(p.Key, p.Value ?? Empty);
			}

			s_singleton._client.Track(
				AnalyticsSettings.Default.IdForAnalytics,
				eventName,
				properties
			);
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
			s_singleton._propertiesThatGoWithEveryEvent.Remove(key);
			s_singleton._propertiesThatGoWithEveryEvent.Add(key, value);
		}

		private static string GetUserNameForEvent()
		{
			if (s_userInfo == null)
				return "unknown";

			return IsNullOrWhiteSpace(s_userInfo.FirstName)
				? s_userInfo.LastName
				: $"{s_userInfo.FirstName} {s_userInfo.LastName}";
		}
		#endregion

		public static void FlushClient()
		{
			s_singleton._client?.Flush();
		}
	}
}
