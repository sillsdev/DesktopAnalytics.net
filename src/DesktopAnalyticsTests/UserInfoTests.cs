using System.Linq;
using DesktopAnalytics;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace DesktopAnalyticsTests
{
	[TestFixture]
    public class UserInfoTests
	{
		[Test]
		public void CreateSanitized_AllFieldsNull_NoChange()
		{
			var orig = new UserInfo { Email = null, FirstName = null, LastName = null, OtherProperties = null };
			var sanitized = orig.CreateSanitized();
			Assert.IsNull(sanitized.Email);
			Assert.IsNull(sanitized.FirstName);
			Assert.IsNull(sanitized.LastName);
			Assert.IsNull(sanitized.OtherProperties);
		}

		[Test]
		public void CreateSanitized_AllFieldsWhitespace_NoChange()
		{
			var orig = new UserInfo { Email = "  ", FirstName = "", LastName = "    ", OtherProperties = new AttributeDictionary() };
			orig.OtherProperties.Add("   ", " \r\n ");
			var sanitized = orig.CreateSanitized();
			Assert.AreEqual("  ", sanitized.Email);
			Assert.AreEqual("", sanitized.FirstName);
			Assert.AreEqual("    ", sanitized.LastName);
			Assert.AreEqual(" \r\n ", sanitized.OtherProperties["   "]);
		}

		[Test]
		public void CreateSanitized_AllFieldsSingleLetter_FirstNameAndLastNameHashed_NoChagneToOtherFields()
		{
			var orig = new UserInfo { Email = "e", FirstName = "f", LastName = "L", OtherProperties = new AttributeDictionary() };
			orig.OtherProperties.Add("a", "b");
			var sanitized = orig.CreateSanitized();
			Assert.AreEqual("e", sanitized.Email);
			Assert.IsTrue(sanitized.FirstName.StartsWith("f"));
			Assert.IsTrue(sanitized.FirstName.Length > 1);
			Assert.IsTrue(sanitized.LastName.StartsWith("L"));
			Assert.IsTrue(sanitized.LastName.Length > 1);
			Assert.AreEqual("b", sanitized.OtherProperties["a"]);
		}

		[Test]
		public void CreateSanitized_NormalData_FirstNameAndLastNameHashed_EmailDomainPreserved()
		{
			var orig = new UserInfo { Email = "Wanda_Finkelstein@gumbyland.org", FirstName = "Wanda", LastName = "Finkelstein", OtherProperties = new AttributeDictionary() };
			orig.OtherProperties.Add("Important", "stuff");
			var sanitized = orig.CreateSanitized();
			Assert.AreEqual("@gumbyland.org", sanitized.Email);
			Assert.IsTrue(sanitized.FirstName.StartsWith("W"));
			Assert.AreNotEqual("Wanda", sanitized.FirstName);
			Assert.IsTrue(sanitized.FirstName.Length > 1);
			Assert.IsTrue(sanitized.LastName.StartsWith("F"));
			Assert.IsTrue(sanitized.LastName.Length > 1);
			Assert.AreNotEqual("Finkelstein", sanitized.LastName);
			Assert.AreEqual("stuff", sanitized.OtherProperties["Important"]);
		}

		[Test]
		public void CreateSanitized_InvalidEmailAddressMissingDomain_EmailIsAtSign()
		{
			var orig = new UserInfo { Email = "Buck@", FirstName = "Buck", LastName = "", OtherProperties = new AttributeDictionary() };
			var sanitized = orig.CreateSanitized();
			Assert.AreEqual("@", sanitized.Email);
			Assert.IsTrue(sanitized.FirstName.StartsWith("B"));
			Assert.AreNotEqual("Buck", sanitized.FirstName);
			Assert.IsTrue(sanitized.FirstName.Length > 1);
			Assert.AreEqual("", sanitized.LastName);
			Assert.IsFalse(sanitized.OtherProperties.Any());
		}

		[Test]
		public void CreateSanitized_InvalidEmailAddressMultipleAtSigns_EmailIsEverythingAfterFirstAtSign()
		{
			var orig = new UserInfo { Email = "What@is@this@nonesense@.c.o.m", FirstName = "", LastName = "" };
			var sanitized = orig.CreateSanitized();
			Assert.AreEqual("@is@this@nonesense@.c.o.m", sanitized.Email);
			Assert.AreEqual("", sanitized.FirstName);
			Assert.AreEqual("", sanitized.LastName);
			Assert.IsFalse(sanitized.OtherProperties.Any());
		}
	}
}
