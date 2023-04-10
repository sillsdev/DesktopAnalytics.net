using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;
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
			Analytics.SetApplicationProperty("TimeSinceLaunch", "3 seconds");
			Analytics.Track("SomeEvent", new Dictionary<string, string>() {{"SomeValue", "62"}});
			if (_chkFlush.Checked) 
				Analytics.FlushClient();
		}

		private void Form1_FormClosed(object sender, FormClosedEventArgs e)
		{
			Debug.WriteLine($"Succeeded: {Analytics.Statistics.Succeeded}; " +
				$"Submitted: {Analytics.Statistics.Submitted}; " +
				$"Failed:  {Analytics.Statistics.Failed}");
			// This was added to allow us to illustrate the deadlock problem:
			// https://github.com/segmentio/Analytics.NET/issues/200
			// This has now been fixed, but keeping this code to illustrate that
			// it is not needed (since Dispose does the flush) but allowed.
			if (_chkFlush.Checked) 
				Analytics.FlushClient();
			var stopwatch = Stopwatch.StartNew();
			Program.s_analyticsSingleton?.Dispose();
			stopwatch.Stop();
			Debug.WriteLine($"Total wait = {stopwatch.Elapsed}");
		}
	}
}