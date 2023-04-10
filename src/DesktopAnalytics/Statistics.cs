namespace DesktopAnalytics
{
	public struct Statistics
	{
		public Statistics(int submitted, int succeeded, int failed)
		{
			Submitted = submitted;
			Succeeded = succeeded;
			Failed = failed;
		}

		public int Submitted { get; }
		public int Succeeded { get; }
		public int Failed { get; }
	}
}