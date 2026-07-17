//License: MIT

#if NET462
namespace DesktopAnalytics
{
	/// <summary>
	/// Thin wrapper delegating every member straight to <see cref="AnalyticsSettings.Default"/>
	/// (ApplicationSettingsBase/user.config). Preserves today's net462 behavior exactly.
	/// </summary>
	internal sealed class NetFrameworkAnalyticsSettingsStore : IAnalyticsSettingsStore
	{
		public string IdForAnalytics
		{
			get => AnalyticsSettings.Default.IdForAnalytics;
			set => AnalyticsSettings.Default.IdForAnalytics = value;
		}

		public string LastVersionLaunched
		{
			get => AnalyticsSettings.Default.LastVersionLaunched;
			set => AnalyticsSettings.Default.LastVersionLaunched = value;
		}

		public bool NeedUpgrade
		{
			get => AnalyticsSettings.Default.NeedUpgrade;
			set => AnalyticsSettings.Default.NeedUpgrade = value;
		}

		public string FirstName
		{
			get => AnalyticsSettings.Default.FirstName;
			set => AnalyticsSettings.Default.FirstName = value;
		}

		public string LastName
		{
			get => AnalyticsSettings.Default.LastName;
			set => AnalyticsSettings.Default.LastName = value;
		}

		public string Email
		{
			get => AnalyticsSettings.Default.Email;
			set => AnalyticsSettings.Default.Email = value;
		}

		public void Save() => AnalyticsSettings.Default.Save();

		public void Upgrade() => AnalyticsSettings.Default.Upgrade();
	}
}
#endif
