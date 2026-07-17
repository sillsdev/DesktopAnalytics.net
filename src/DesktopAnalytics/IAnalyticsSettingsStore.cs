//License: MIT

namespace DesktopAnalytics
{
	/// <summary>
	/// Abstraction over the small set of persisted user settings Analytics needs. On net462 this
	/// is backed by <see cref="AnalyticsSettings"/> (ApplicationSettingsBase/user.config) so
	/// existing consumers see byte-for-byte identical behavior. On netstandard2.0/net8.0 it is
	/// backed by a JSON file at a stable path, since ApplicationSettingsBase's per-install settings
	/// path is not stable across publish shapes on those TFMs.
	/// </summary>
	internal interface IAnalyticsSettingsStore
	{
		string IdForAnalytics { get; set; }
		string LastVersionLaunched { get; set; }
		bool NeedUpgrade { get; set; }
		string FirstName { get; set; }
		string LastName { get; set; }
		string Email { get; set; }

		void Save();
		void Upgrade();
	}
}
