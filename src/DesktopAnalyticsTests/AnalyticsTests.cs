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

		#region ApplyPiiPolicy tests

		[Test]
		public void ApplyPiiPolicy_RetainPiiTrue_ReturnsSameInstanceUnchanged()
		{
			var userInfo = new UserInfo { Email = "real@example.com", FirstName = "Real", LastName = "Name" };
			var result = Analytics.ApplyPiiPolicy(userInfo, true);
			Assert.AreSame(userInfo, result);
			Assert.AreEqual("real@example.com", result.Email);
		}

		[Test]
		public void ApplyPiiPolicy_RetainPiiFalse_ReturnsSanitizedCopy()
		{
			var userInfo = new UserInfo { Email = "real@example.com", FirstName = "Real", LastName = "Name" };
			var result = Analytics.ApplyPiiPolicy(userInfo, false);
			Assert.AreNotSame(userInfo, result);
			Assert.AreEqual("@example.com", result.Email);
			Assert.AreNotEqual("Real", result.FirstName);
			Assert.AreNotEqual("Name", result.LastName);
		}

		[Test]
		public void ApplyPiiPolicy_NameUnchangedAndPreviousAlreadyHashed_CarriesOverPreviousHashInstead()
		{
			var previous = new UserInfo { Email = "real@example.com", FirstName = "Real", LastName = "Name" }.CreateSanitized();
			var incoming = new UserInfo { Email = "real@example.com", FirstName = previous.FirstName, LastName = previous.LastName };

			var result = Analytics.ApplyPiiPolicy(incoming, false, previous);

			Assert.AreEqual(previous.FirstName, result.FirstName);
			Assert.AreEqual(previous.LastName, result.LastName);
		}

		[Test]
		public void ApplyPiiPolicy_NameChangedFromPrevious_HashesTheNewNameInstead()
		{
			var previous = new UserInfo { FirstName = "Real", LastName = "Name" }.CreateSanitized();
			var incoming = new UserInfo { FirstName = "Different", LastName = "Person" };

			var result = Analytics.ApplyPiiPolicy(incoming, false, previous);

			Assert.AreNotEqual(previous.FirstName, result.FirstName);
			Assert.AreNotEqual(previous.LastName, result.LastName);
			Assert.AreNotEqual("Different", result.FirstName);
			Assert.AreNotEqual("Person", result.LastName);
			Assert.IsTrue(Analytics.LooksAlreadyHashed(result.FirstName));
			Assert.IsTrue(Analytics.LooksAlreadyHashed(result.LastName));
			Assert.IsTrue(result.FirstName.StartsWith("D"));
			Assert.IsTrue(result.LastName.StartsWith("P"));
		}

		[Test]
		public void ApplyPiiPolicy_NameUnchangedButPreviousDoesNotLookHashed_HashesAnyway()
		{
			// previous.FirstName/LastName are real, unsanitized names here (as if retainPii had
			// been true before) - even though "incoming" matches them exactly, they don't match
			// the hash shape, so they must still be sanitized rather than carried over verbatim.
			var previous = new UserInfo { FirstName = "Real", LastName = "Name" };
			var incoming = new UserInfo { FirstName = "Real", LastName = "Name" };

			var result = Analytics.ApplyPiiPolicy(incoming, false, previous);

			Assert.AreNotEqual("Real", result.FirstName);
			Assert.AreNotEqual("Name", result.LastName);
		}

		[Test]
		public void ApplyPiiPolicy_NoPreviousGiven_HashesNormally()
		{
			var incoming = new UserInfo { FirstName = "Real", LastName = "Name" };
			var result = Analytics.ApplyPiiPolicy(incoming, false);
			Assert.AreNotEqual("Real", result.FirstName);
			Assert.AreNotEqual("Name", result.LastName);
		}

		[TestCase("Z", false)]
		[TestCase("Z1", true)]
		[TestCase("Z-1", true)]
		[TestCase("Z2147483647", true)]
		[TestCase("Z-2147483648", true)]
		[TestCase("Z12345678901", false)] // 11 digits: one too many for a signed 32-bit hash
		[TestCase("ZZ1", false)] // two leading non-digit characters
		[TestCase("", false)]
		[TestCase(null, false)]
		public void LooksAlreadyHashed_VariousInputs_MatchesExpectation(string value, bool expected)
		{
			Assert.AreEqual(expected, Analytics.LooksAlreadyHashed(value));
		}

		#endregion

		#region IdentifyUpdate tests

		[Test]
		public void IdentifyUpdate_NullUserInfo_IsNoOpAndDoesNotThrow()
		{
			// No Analytics object is constructed anywhere in this test run, so s_singleton is
			// still null; if IdentifyUpdate didn't return early for a null userInfo, it would
			// reach s_singleton.UpdateServerInformationOnThisUser() and NRE there regardless of
			// userInfo. DoesNotThrow is therefore proof it never got past the guard - but that
			// alone doesn't prove nothing was mutated before any such later failure would occur,
			// so also assert CurrentUserInfo (s_userInfo) is untouched.
			var before = Analytics.CurrentUserInfo;

			Assert.DoesNotThrow(() => Analytics.IdentifyUpdate(null));

			Assert.AreSame(before, Analytics.CurrentUserInfo);
		}

		#endregion
	}
}
