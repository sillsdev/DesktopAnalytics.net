// License: MIT

// JsonAnalyticsSettingsStore only exists on non-net462 TFMs (see
// src/DesktopAnalytics/JsonAnalyticsSettingsStore.cs), so this whole file compiles to nothing on
// net462.
#if !NET462
using System;
using System.IO;
using DesktopAnalytics;
using NUnit.Framework;

namespace DesktopAnalyticsTests
{
	[TestFixture]
	public class JsonAnalyticsSettingsStoreTests
	{
		private string _tempRoot;

		[SetUp]
		public void SetUp()
		{
			_tempRoot = Path.Combine(Path.GetTempPath(), "DesktopAnalyticsTests_" + Guid.NewGuid());
		}

		[TearDown]
		public void TearDown()
		{
			if (Directory.Exists(_tempRoot))
				Directory.Delete(_tempRoot, recursive: true);
		}

		[Test]
		public void DefaultsWhenAbsent_FreshDirectory_MatchesDesignerDefaults()
		{
			var store = JsonAnalyticsSettingsStore.ForTesting(_tempRoot);

			Assert.That(store.NeedUpgrade, Is.True);
			Assert.That(store.IdForAnalytics, Is.EqualTo(""));
			Assert.That(store.LastVersionLaunched, Is.EqualTo(""));
			Assert.That(store.FirstName, Is.EqualTo(""));
			Assert.That(store.LastName, Is.EqualTo(""));
			Assert.That(store.Email, Is.EqualTo(""));
		}

		[Test]
		public void RoundTrip_SetPropertiesAndSave_FreshInstanceReadsSameValues()
		{
			var first = JsonAnalyticsSettingsStore.ForTesting(_tempRoot);
			first.IdForAnalytics = "abc-123";
			first.LastVersionLaunched = "1.2.3.4";
			first.NeedUpgrade = false;
			first.FirstName = "Bob";
			first.LastName = "Smith";
			first.Email = "test@example.com";
			first.Save();

			var second = JsonAnalyticsSettingsStore.ForTesting(_tempRoot);

			Assert.That(second.IdForAnalytics, Is.EqualTo("abc-123"));
			Assert.That(second.LastVersionLaunched, Is.EqualTo("1.2.3.4"));
			Assert.That(second.NeedUpgrade, Is.False);
			Assert.That(second.FirstName, Is.EqualTo("Bob"));
			Assert.That(second.LastName, Is.EqualTo("Smith"));
			Assert.That(second.Email, Is.EqualTo("test@example.com"));
		}

		[Test]
		public void Save_DirectoryDoesNotExist_CreatesItAndWritesFile()
		{
			Assert.That(Directory.Exists(_tempRoot), Is.False);

			var store = JsonAnalyticsSettingsStore.ForTesting(_tempRoot);
			store.IdForAnalytics = "new-id";
			store.Save();

			Assert.That(File.Exists(Path.Combine(_tempRoot, "settings.json")), Is.True);
		}

		[Test]
		public void RestartStability_MultipleSequentialInstancesLikeProcessRestarts_ValuesPersist()
		{
			// Simulates: launch 1 assigns an ID and saves; launch 2 (a fresh process/instance)
			// reads it back and updates LastVersionLaunched; launch 3 sees both.
			var launch1 = JsonAnalyticsSettingsStore.ForTesting(_tempRoot);
			Assert.That(launch1.IdForAnalytics, Is.EqualTo(""));
			launch1.IdForAnalytics = "stable-id";
			launch1.LastVersionLaunched = "1.0.0.0";
			launch1.Save();

			var launch2 = JsonAnalyticsSettingsStore.ForTesting(_tempRoot);
			Assert.That(launch2.IdForAnalytics, Is.EqualTo("stable-id"));
			Assert.That(launch2.LastVersionLaunched, Is.EqualTo("1.0.0.0"));
			launch2.LastVersionLaunched = "2.0.0.0";
			launch2.Save();

			var launch3 = JsonAnalyticsSettingsStore.ForTesting(_tempRoot);
			Assert.That(launch3.IdForAnalytics, Is.EqualTo("stable-id"));
			Assert.That(launch3.LastVersionLaunched, Is.EqualTo("2.0.0.0"));
		}

		[Test]
		public void Load_CorruptFile_FallsBackToDefaultsInsteadOfThrowing()
		{
			Directory.CreateDirectory(_tempRoot);
			File.WriteAllText(Path.Combine(_tempRoot, "settings.json"), "{ not valid json");

			JsonAnalyticsSettingsStore store = null;
			Assert.DoesNotThrow(() => store = JsonAnalyticsSettingsStore.ForTesting(_tempRoot));
			Assert.That(store.NeedUpgrade, Is.True);
			Assert.That(store.IdForAnalytics, Is.EqualTo(""));
		}
	}
}
#endif
