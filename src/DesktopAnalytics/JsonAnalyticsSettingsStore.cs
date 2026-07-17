//License: MIT

#if !NET462
using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace DesktopAnalytics
{
	/// <summary>
	/// System.Text.Json-backed settings store for netstandard2.0/net8.0, used instead of
	/// ApplicationSettingsBase because that type's per-install settings path is not stable across
	/// publish shapes (self-contained vs. framework-dependent, single-file vs. not) on those TFMs
	/// -- see offline-analytics-v2-plan.md. Stored at a stable, well-known path:
	/// LocalApplicationData/SIL/DesktopAnalytics/{appId}/settings.json.
	/// </summary>
	internal sealed class JsonAnalyticsSettingsStore : IAnalyticsSettingsStore
	{
		// Mirrors AnalyticsSettings.Designer.cs's DefaultSettingValueAttribute defaults exactly.
		private class Data
		{
			public string IdForAnalytics { get; set; } = "";
			public string LastVersionLaunched { get; set; } = "";
			public bool NeedUpgrade { get; set; } = true;
			public string FirstName { get; set; } = "";
			public string LastName { get; set; } = "";
			public string Email { get; set; } = "";
		}

		private readonly string _filePath;
		private readonly Data _data;

		/// <param name="appId">Identifies this app's settings file on disk. Defaults to the entry
		/// assembly's name; falls back to a fixed name if there is no entry assembly (e.g. plugin
		/// hosting), mirroring the fallback pattern Analytics.cs already uses for
		/// GetCallingAssembly/GetEntryAssembly.</param>
		public JsonAnalyticsSettingsStore(string appId = null)
			: this(BuildDefaultRoot(appId), isRootPath: true)
		{
		}

		/// <summary>
		/// Test seam: points the store at an explicit root directory instead of computing one from
		/// LocalApplicationData, so tests can use a temp directory.
		/// </summary>
		internal static JsonAnalyticsSettingsStore ForTesting(string rootPath) =>
			new JsonAnalyticsSettingsStore(rootPath, isRootPath: true);

		// The unused bool parameter exists only to give this constructor a distinct signature from
		// the public string-appId one above (C# won't allow two ctors that both take a single
		// string).
		private JsonAnalyticsSettingsStore(string rootPath, bool isRootPath)
		{
			_filePath = Path.Combine(rootPath, "settings.json");
			_data = Load(_filePath);
		}

		private static string BuildDefaultRoot(string appId)
		{
			appId = appId ?? Assembly.GetEntryAssembly()?.GetName().Name ?? "DesktopAnalytics";
			return Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"SIL", "DesktopAnalytics", appId);
		}

		private static Data Load(string filePath)
		{
			if (!File.Exists(filePath))
				return new Data();

			try
			{
				var json = File.ReadAllText(filePath);
				return JsonSerializer.Deserialize<Data>(json) ?? new Data();
			}
			catch (Exception)
			{
				// Corrupt or unreadable settings file: start fresh rather than crash the host app.
				return new Data();
			}
		}

		public string IdForAnalytics
		{
			get => _data.IdForAnalytics;
			set => _data.IdForAnalytics = value;
		}

		public string LastVersionLaunched
		{
			get => _data.LastVersionLaunched;
			set => _data.LastVersionLaunched = value;
		}

		public bool NeedUpgrade
		{
			get => _data.NeedUpgrade;
			set => _data.NeedUpgrade = value;
		}

		public string FirstName
		{
			get => _data.FirstName;
			set => _data.FirstName = value;
		}

		public string LastName
		{
			get => _data.LastName;
			set => _data.LastName = value;
		}

		public string Email
		{
			get => _data.Email;
			set => _data.Email = value;
		}

		public void Save()
		{
			var dir = Path.GetDirectoryName(_filePath);
			if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
				Directory.CreateDirectory(dir);

			File.WriteAllText(_filePath, JsonSerializer.Serialize(_data));
		}

		// No legacy JSON schema exists to upgrade from, so this is a no-op. Only the net462
		// (ApplicationSettingsBase) store actually upgrades anything.
		public void Upgrade()
		{
		}
	}
}
#endif
