using System;
using DesktopAnalytics;
using NUnit.Framework;
using Segment.Serialization;

namespace DesktopAnalyticsTests
{
	[TestFixture]
	public class AnalyticsEventTests
	{
		private static string ContentOf(JsonElement element)
		{
			return element.ToJsonPrimitive().Content;
		}

		[Test]
		public void ToBytes_FromBytes_RoundTrip_PreservesAllScalarFields()
		{
			var time = new DateTimeOffset(2026, 7, 8, 12, 34, 56, TimeSpan.Zero);
			var original = new AnalyticsEvent
			{
				AnalyticsId = "abc-123",
				EventName = "Save PDF",
				Properties = new JsonObject { { "Portion", "All" } },
				InsertId = "insert-guid-1",
				Time = time
			};

			var roundTripped = AnalyticsEvent.FromBytes(original.ToBytes());

			Assert.AreEqual(original.AnalyticsId, roundTripped.AnalyticsId);
			Assert.AreEqual(original.EventName, roundTripped.EventName);
			Assert.AreEqual(original.InsertId, roundTripped.InsertId);
			Assert.AreEqual(original.Time, roundTripped.Time);
		}

		[Test]
		public void ToBytes_ReturnsUtf8EncodedJson()
		{
			var evt = new AnalyticsEvent
			{
				AnalyticsId = "abc-123",
				EventName = "Save PDF",
				InsertId = "insert-guid-1",
				Time = DateTimeOffset.UtcNow
			};

			var bytes = evt.ToBytes();
			var json = System.Text.Encoding.UTF8.GetString(bytes);

			StringAssert.Contains("\"abc-123\"", json);
			StringAssert.Contains("\"Save PDF\"", json);
		}

		[Test]
		public void PropertiesBag_WithMultipleKeys_SurvivesRoundTrip()
		{
			var original = new AnalyticsEvent
			{
				AnalyticsId = "abc-123",
				EventName = "Exception",
				Properties = new JsonObject
				{
					{ "Message", "Oops" },
					{ "Stack Trace", "at Foo.Bar()" },
					{ "Count", 3 }
				},
				InsertId = "insert-guid-2",
				Time = DateTimeOffset.UtcNow
			};

			var roundTripped = AnalyticsEvent.FromBytes(original.ToBytes());

			Assert.AreEqual(3, roundTripped.Properties.Count);
			Assert.AreEqual("Oops", ContentOf(roundTripped.Properties["Message"]));
			Assert.AreEqual("at Foo.Bar()", ContentOf(roundTripped.Properties["Stack Trace"]));
			Assert.AreEqual("3", ContentOf(roundTripped.Properties["Count"]));
		}

		[Test]
		public void Create_WithNoInjectedDependencies_StampsRealGuidAndRecentTime()
		{
			var before = DateTimeOffset.UtcNow;
			var evt = AnalyticsEvent.Create("abc-123", "Launch");
			var after = DateTimeOffset.UtcNow;

			Assert.IsTrue(Guid.TryParse(evt.InsertId, out _));
			Assert.GreaterOrEqual(evt.Time, before);
			Assert.LessOrEqual(evt.Time, after);
		}

		[Test]
		public void Create_WithInjectedDependencies_StampsExactInjectedValues()
		{
			var fixedTime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
			const string fixedInsertId = "fixed-insert-id";

			var evt = AnalyticsEvent.Create(
				"abc-123",
				"Launch",
				new JsonObject { { "Version", "1.2.3" } },
				insertId: fixedInsertId,
				time: fixedTime
			);

			Assert.AreEqual("abc-123", evt.AnalyticsId);
			Assert.AreEqual("Launch", evt.EventName);
			Assert.AreEqual(fixedInsertId, evt.InsertId);
			Assert.AreEqual(fixedTime, evt.Time);
			Assert.AreEqual("1.2.3", ContentOf(evt.Properties["Version"]));
		}

		[Test]
		public void Create_CalledTwice_ProducesDifferentInsertIdsWhenNotInjected()
		{
			var first = AnalyticsEvent.Create("abc-123", "Launch");
			var second = AnalyticsEvent.Create("abc-123", "Launch");

			Assert.AreNotEqual(first.InsertId, second.InsertId);
		}

		[Test]
		public void Create_WithNullProperties_ProducesEmptyPropertyBag()
		{
			var evt = AnalyticsEvent.Create("abc-123", "Launch", null,
				"insert-id", DateTimeOffset.UtcNow);

			Assert.IsNotNull(evt.Properties);
			Assert.AreEqual(0, evt.Properties.Count);
		}
	}
}
