using DesktopAnalytics;
using NUnit.Framework;

namespace DesktopAnalyticsTests
{
	[TestFixture]
	public class PathScrubberTests
	{
		[Test]
		public void Scrub_WindowsPath_ReplacesUserSegment()
		{
			const string input = @"C:\Users\jsmith\AppData\Local\Temp\file.txt";
			const string expected = @"%USER%\AppData\Local\Temp\file.txt";
			Assert.AreEqual(expected, PathScrubber.Scrub(input));
		}

		[Test]
		public void Scrub_UnixPath_ReplacesUserSegment()
		{
			const string input = "/home/jsmith/.config/app/settings.json";
			const string expected = "%USER%/.config/app/settings.json";
			Assert.AreEqual(expected, PathScrubber.Scrub(input));
		}

		[Test]
		public void Scrub_MultipleOccurrencesInOneString_ReplacesAll()
		{
			const string input =
				"at Foo.Bar() in C:\\Users\\jsmith\\src\\Foo.cs:line 10\r\n" +
				"   at Baz.Qux() in C:\\Users\\otherUser\\src\\Baz.cs:line 20";
			const string expected =
				"at Foo.Bar() in %USER%\\src\\Foo.cs:line 10\r\n" +
				"   at Baz.Qux() in %USER%\\src\\Baz.cs:line 20";
			Assert.AreEqual(expected, PathScrubber.Scrub(input));
		}

		[Test]
		public void Scrub_MixedWindowsAndUnixInOneString_ReplacesBoth()
		{
			const string input = @"C:\Users\jsmith\a.txt and /home/jsmith/b.txt";
			const string expected = "%USER%\\a.txt and %USER%/b.txt";
			Assert.AreEqual(expected, PathScrubber.Scrub(input));
		}

		[Test]
		public void Scrub_NoMatch_ReturnsInputUnchanged()
		{
			const string input = @"D:\Projects\MyApp\bin\Debug\App.exe";
			Assert.AreEqual(input, PathScrubber.Scrub(input));
		}

		[Test]
		public void Scrub_PlainStringWithNoPaths_ReturnsInputUnchanged()
		{
			const string input = "Just a regular exception message, nothing to scrub here.";
			Assert.AreEqual(input, PathScrubber.Scrub(input));
		}

		[Test]
		public void Scrub_Null_ReturnsNull()
		{
			Assert.IsNull(PathScrubber.Scrub(null));
		}

		[Test]
		public void Scrub_Empty_ReturnsEmpty()
		{
			Assert.AreEqual(string.Empty, PathScrubber.Scrub(string.Empty));
		}

		[Test]
		public void Scrub_LowercaseDriveLetter_StillMatches()
		{
			const string input = @"c:\Users\jsmith\file.txt";
			const string expected = @"%USER%\file.txt";
			Assert.AreEqual(expected, PathScrubber.Scrub(input));
		}

		[Test]
		public void Scrub_LowercaseUsersSegment_StillMatches()
		{
			const string input = @"C:\users\jsmith\file.txt";
			const string expected = @"%USER%\file.txt";
			Assert.AreEqual(expected, PathScrubber.Scrub(input));
		}

		[Test]
		public void Scrub_MixedCaseDriveAndUsersSegment_StillMatches()
		{
			const string input = @"D:\UseRs\JSmith\file.txt";
			const string expected = @"%USER%\file.txt";
			Assert.AreEqual(expected, PathScrubber.Scrub(input));
		}

		[Test]
		public void Scrub_UserNameWithSpacesAndDots_ReplacesWholeSegment()
		{
			const string input = @"C:\Users\John Q. Smith\Documents\file.txt";
			const string expected = @"%USER%\Documents\file.txt";
			Assert.AreEqual(expected, PathScrubber.Scrub(input));
		}

		[Test]
		public void Scrub_HugeStackTraceWithManyPaths_ReplacesAllWithoutError()
		{
			var sb = new System.Text.StringBuilder();
			for (int i = 0; i < 500; i++)
			{
				sb.AppendLine($@"   at Some.Method{i}() in C:\Users\jsmith\repo\File{i}.cs:line {i}");
			}
			var result = PathScrubber.Scrub(sb.ToString());
			StringAssert.DoesNotContain("jsmith", result);
			StringAssert.Contains("%USER%\\repo\\File0.cs", result);
			StringAssert.Contains("%USER%\\repo\\File499.cs", result);
		}

		[Test]
		public void Scrub_WindowsPath_NoTrailingSeparatorAtEndOfString_ReplacesUserSegment()
		{
			const string input = @"Access denied: C:\Users\jsmith";
			const string expected = "Access denied: %USER%";
			Assert.AreEqual(expected, PathScrubber.Scrub(input));
		}

		[Test]
		public void Scrub_WindowsPath_ForwardSlashes_ReplacesUserSegmentPreservingSlash()
		{
			const string input = @"C:/Users/jsmith/file.txt";
			const string expected = "%USER%/file.txt";
			Assert.AreEqual(expected, PathScrubber.Scrub(input));
		}

		[Test]
		public void Scrub_UncPath_ReplacesUserSegment()
		{
			const string input = @"\\fileserver\Users\jsmith\Documents\file.txt";
			const string expected = @"%USER%\Documents\file.txt";
			Assert.AreEqual(expected, PathScrubber.Scrub(input));
		}

		[Test]
		public void Scrub_UncPath_NoTrailingSeparatorAtEndOfString_ReplacesUserSegment()
		{
			const string input = @"\\fileserver\Users\jsmith";
			const string expected = "%USER%";
			Assert.AreEqual(expected, PathScrubber.Scrub(input));
		}

		[Test]
		public void Scrub_UnixPath_NoTrailingSeparatorAtEndOfString_ReplacesUserSegment()
		{
			const string input = "/home/jsmith";
			const string expected = "%USER%";
			Assert.AreEqual(expected, PathScrubber.Scrub(input));
		}

		[Test]
		public void Scrub_WindowsPathWithNoUsersSegment_ReturnsInputUnchanged()
		{
			const string input = @"D:\Projects\MyApp";
			Assert.AreEqual(input, PathScrubber.Scrub(input));
		}
	}
}
