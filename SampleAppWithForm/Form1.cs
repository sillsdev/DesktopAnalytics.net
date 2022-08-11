using System.Diagnostics;
using DesktopAnalytics;

namespace SampleAppWithForm
{
	public partial class Form1 : Form
	{
		public Form1()
		{
			InitializeComponent();
		}

		private void textBox1_TextChanged(object sender, EventArgs e)
		{
			_btnTrack.Enabled = _txtEventName.Text.Trim().Length > 0;
		}

		private void HandleTrackButtonClick(object sender, EventArgs e)
		{
			DesktopAnalytics.Analytics.SetApplicationProperty("TimeSinceLaunch", "3 seconds");
			DesktopAnalytics.Analytics.Track("SomeEvent", new Dictionary<string, string>() {{"SomeValue", "62"}});
			if (_chkFlush.Checked) 
				Segment.Analytics.Client.Flush();
		}

		private void Form1_FormClosed(object sender, FormClosedEventArgs e)
		{
			Debug.WriteLine($"Succeeded: {Segment.Analytics.Client.Statistics.Succeeded}; " +
				$"Submitted: {Segment.Analytics.Client.Statistics.Submitted}; " +
				$"Failed:  {Segment.Analytics.Client.Statistics.Failed}");
			// This allows us to illustrate the deadlock problem.
			if (_chkFlush.Checked) 
				Segment.Analytics.Client.Flush();
			Program.s_analyticsSingleton?.Dispose();
		}
	}
}