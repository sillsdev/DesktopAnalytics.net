using System;
using System.Text.RegularExpressions;

namespace DesktopAnalytics
{
	/// <summary>
	/// Normalizes user home paths embedded in strings (e.g. exception stack traces) so that the
	/// OS account name does not leak when spooled/reported. This is not a general PII scrubber;
	/// it only targets the well-known Windows and Unix "home directory" prefixes.
	/// </summary>
	internal static class PathScrubber
	{
		// Windows: C:\Users\<name>\  (any drive letter, case-insensitive on both the drive
		// letter and the literal "Users" segment). We stop matching the user name at the next
		// path separator so we don't eat the rest of the path.
		private static readonly Regex s_windowsUserPath =
			new Regex(@"[A-Za-z]:\\Users\\[^\\/:*?""<>|\r\n]+\\",
				RegexOptions.Compiled | RegexOptions.IgnoreCase);

		// Unix: /home/<name>/
		private static readonly Regex s_unixUserPath =
			new Regex(@"/home/[^/\r\n]+/", RegexOptions.Compiled);

		/// <summary>
		/// Replaces every occurrence of a Windows or Unix user-home-directory prefix in
		/// <paramref name="input"/> with a neutral placeholder ("%USER%\" or "%USER%/"
		/// respectively). Returns the input unchanged if it is null/empty or contains no match.
		/// </summary>
		public static string Scrub(string input)
		{
			if (string.IsNullOrEmpty(input))
				return input;

			var result = s_windowsUserPath.Replace(input, @"%USER%\");
			result = s_unixUserPath.Replace(result, "%USER%/");
			return result;
		}
	}
}
