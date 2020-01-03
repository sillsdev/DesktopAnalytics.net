using System;
using System.Collections.Generic;

namespace DesktopAnalytics
{
	/// <summary>
	/// Used to send id information to analytics; the most natural way to use this is to load
	/// it from your Settings.Default each time you run, even if you don't know these things
	/// yet because the user hasn't registered yet. Then even if they register while offline,
	/// eventually this informatino will be sent when they *are* online.
	/// </summary>
	public class UserInfo
	{
		public string FirstName = "";
		public string LastName = "";
		public string Email = "";
		public string UILanguageCode = "";
		public Dictionary<string, string> OtherProperties = new Dictionary<string, string>();

		internal UserInfo CreateSanitized()
		{
			var firstName = FirstName;
			if (!String.IsNullOrWhiteSpace(firstName))
				firstName = firstName[0].ToString() + firstName.GetHashCode();
			var lastName = LastName;
			if (!String.IsNullOrWhiteSpace(lastName))
				lastName = lastName[0].ToString() + lastName.GetHashCode();
			var emailDomain = Email;
			if (emailDomain != null)
			{
				var charactersToStrip = emailDomain.IndexOf("@", StringComparison.Ordinal);
				if (charactersToStrip > 0)
					emailDomain = emailDomain.Substring(charactersToStrip);
			}
			return new UserInfo { FirstName = firstName, LastName = lastName, Email = emailDomain, OtherProperties = OtherProperties };
		}
	}
}
