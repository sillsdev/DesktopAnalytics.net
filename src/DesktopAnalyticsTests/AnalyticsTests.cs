using System.Globalization;
using System.Xml.Linq;
using DesktopAnalytics;
using NUnit.Framework;

namespace DesktopAnalyticsTests
{
	[TestFixture]
	public class AnalyticsTests
	{
		#region ExtractSetting tests

		private static readonly XDocument s_settingsDoc = XDocument.Parse(@"
<configuration>
  <userSettings>
    <DesktopAnalytics.AnalyticsSettings>
      <setting name=""IdForAnalytics""><value>abc-123</value></setting>
      <setting name=""FirstName""><value>Bob</value></setting>
      <setting name=""LastName""><value>Smith</value></setting>
      <setting name=""Email""><value>test@example.com</value></setting>
      <setting name=""LastVersionLaunched""><value>1.2.3.4</value></setting>
    </DesktopAnalytics.AnalyticsSettings>
  </userSettings>
</configuration>");

		[Test]
		public void ExtractSetting_CurrentNonEmpty_ReturnsCurrentWithoutConsultingDoc()
		{
			Assert.AreEqual("existing", Analytics.ExtractSetting("existing", s_settingsDoc, "IdForAnalytics"));
		}

		[Test]
		public void ExtractSetting_CurrentEmpty_SettingPresent_ReturnsSettingValue()
		{
			Assert.AreEqual("abc-123", Analytics.ExtractSetting("", s_settingsDoc, "IdForAnalytics"));
		}

		[Test]
		public void ExtractSetting_CurrentNull_SettingPresent_ReturnsSettingValue()
		{
			Assert.AreEqual("test@example.com", Analytics.ExtractSetting(null, s_settingsDoc, "Email"));
		}

		[Test]
		public void ExtractSetting_CurrentEmpty_SettingAbsent_ReturnsEmptyString()
		{
			Assert.AreEqual("", Analytics.ExtractSetting("", s_settingsDoc, "NonExistent"));
		}

		#endregion

		#region GetInstalledUICultureCode tests

		[Test]
		public void GetInstalledUICultureCode_NormalCulture_ReturnsTwoLetterCode()
		{
			var ci = new CultureInfo("en-US");
			Assert.AreEqual("en", Analytics.GetInstalledUICultureCode(ci));
		}

		[Test]
		public void GetInstalledUICultureCode_FrenchCulture_ReturnsTwoLetterCode()
		{
			var ci = new CultureInfo("fr-FR");
			Assert.AreEqual("fr", Analytics.GetInstalledUICultureCode(ci));
		}

		[Test]
		public void GetInstalledUICultureCode_InvariantCulture_FallsBackToThreeLetter()
		{
			// InvariantCulture has TwoLetterISOLanguageName == "iv"
			var ci = CultureInfo.InvariantCulture;
			Assert.AreEqual(ci.ThreeLetterISOLanguageName,
				Analytics.GetInstalledUICultureCode(ci));
		}

		#endregion

		#region GetWindowsVersionInfoFromNetworkAPI tests

		[Test]
		[Platform("Win")]
		public void GetWindowsVersionInfoFromNetworkAPI_ReturnsRecognizedWindowsString()
		{
			var result = Analytics.GetWindowsVersionInfoFromNetworkAPI();
			Assert.That(result, Does.Match(@"^Windows [\d.]+$"));
		}

		#endregion
	}
}
