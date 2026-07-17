namespace DesktopAnalytics
{
	public struct Statistics
	{
		public Statistics(int submitted, int succeeded, int failed) : this(submitted, succeeded, failed, 0)
		{
		}

		/// <param name="expired">Events dropped by an age-based retention floor rather than a
		/// delivery failure -- distinct from <paramref name="failed"/>, which is events that were
		/// actually attempted (or attempted and refused at enqueue) and did not succeed. A client
		/// with no such retention floor always reports 0 here.</param>
		public Statistics(int submitted, int succeeded, int failed, int expired)
		{
			Submitted = submitted;
			Succeeded = succeeded;
			Failed = failed;
			Expired = expired;
		}

		public int Submitted { get; }
		public int Succeeded { get; }
		public int Failed { get; }
		public int Expired { get; }
	}
}