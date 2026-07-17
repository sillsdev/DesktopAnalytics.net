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
		// Windows: C:\Users\<name>  or  \\server\Users\<name>  (any drive letter or UNC server
		// name; case-insensitive on the drive letter and the literal "Users" segment; separators
		// may be '\' or '/'). We stop matching the user name at the next path separator so we
		// don't eat the rest of the path. The trailing separator is optional: the match may end
		// at a separator (more path follows) or at the true end of the string (the user name is
		// the last thing in the string, e.g. a truncated exception message).
		private static readonly Regex s_windowsUserPath =
			new Regex(@"(?:[A-Za-z]:|\\\\[^\\/]+)[\\/]Users[\\/][^\\/:*?""<>|\r\n]+(?:(?<sep>[\\/])|$)",
				RegexOptions.Compiled | RegexOptions.IgnoreCase);

		// Unix: /home/<name>/ or /home/<name> at end of string. As with the Windows regex, the
		// trailing separator is optional so a user name at the very end of the string is scrubbed.
		private static readonly Regex s_unixUserPath =
			new Regex(@"/home/[^/\r\n]+(?:(?<sep>/)|$)", RegexOptions.Compiled);

		/// <summary>
		/// Replaces every occurrence of a Windows or Unix user-home-directory prefix in
		/// <paramref name="input"/> with a neutral placeholder ("%USER%"). If the matched prefix
		/// was followed by a path separator ('\' or '/'), that same separator is preserved after
		/// the placeholder; if the user name ran to the end of the string with no separator, the
		/// placeholder is emitted with nothing after it. Returns the input unchanged if it is
		/// null/empty or contains no match.
		/// </summary>
		public static string Scrub(string input)
		{
			if (string.IsNullOrEmpty(input))
				return input;

			var result = s_windowsUserPath.Replace(input, ReplaceUserSegment);
			result = s_unixUserPath.Replace(result, ReplaceUserSegment);
			return result;
		}

		// Shared MatchEvaluator for both regexes: preserves whichever trailing separator (if any)
		// was actually matched, via the "sep" capture group.
		private static string ReplaceUserSegment(Match match)
		{
			var sep = match.Groups["sep"];
			return sep.Success ? "%USER%" + sep.Value : "%USER%";
		}
	}
}
