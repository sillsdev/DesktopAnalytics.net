using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Segment.Concurrent;
using Segment.Serialization;

namespace DesktopAnalytics
{
	/// <summary>
	/// <see cref="IClient"/> implementation backed by Segment.Analytics.CSharp. Constructible
	/// directly by callers who want to pass it explicitly to an <see cref="Analytics"/>
	/// constructor; also the implicit default when no <see cref="IClient"/> is supplied.
	/// </summary>
	public class SegmentClient : IClient, ICoroutineExceptionHandler
	{
		public event Action<Exception> Failed;
		private Segment.Analytics.Analytics _analytics;
		public static StatisticsMonitor StatMonitor { get; } = new StatisticsMonitor();
		public void Initialize(string apiSecret, string host = null, int flushAt = -1, int flushInterval = -1)
		{
			Segment.Analytics.Configuration configuration;
			if (flushAt >= 0)
			{
				if (flushInterval >= 0)
				{
					configuration = new Segment.Analytics.Configuration(apiSecret, this,
						flushAt, flushInterval, apiHost:host);
				}
				else
				{
					configuration = new Segment.Analytics.Configuration(apiSecret, this,
						flushAt, apiHost:host);
				}
			}
			else
			{
				if (flushInterval >= 0)
				{
					configuration = new Segment.Analytics.Configuration(apiSecret,
						flushInterval: flushInterval, exceptionHandler: this, apiHost:host);

				}
				else
				{
					configuration = new Segment.Analytics.Configuration(apiSecret,
						exceptionHandler: this, apiHost:host);
				}
			}

			_analytics = new Segment.Analytics.Analytics(configuration);
			_analytics.Add(StatMonitor);
		}

		public void ShutDown()
		{
			_analytics?.Flush();
		}

		public void Identify(string analyticsId, JsonObject traits, JsonObject options)
		{
			_analytics.Identify(analyticsId, traits);
		}

		public void Track(string defaultIdForAnalytics, string eventName, JsonObject properties)
		{
			lock (StatMonitor)
				StatMonitor.Submitted++;
			_analytics.Track(eventName, properties);
		}

		public void Flush()
		{
			_analytics.Flush();
		}

		/// <summary>
		/// Completes synchronously: <see cref="Track"/> already just hands the event to
		/// Segment.Analytics.CSharp's own fire-and-forget delivery pipeline and returns, so there is
		/// no async work to represent here.
		/// </summary>
		public Task TrackAsync(string defaultIdForAnalytics, string eventName, JsonObject properties,
			CancellationToken cancellationToken = default)
		{
			Track(defaultIdForAnalytics, eventName, properties);
			return Task.CompletedTask;
		}

		/// <summary>
		/// Completes synchronously: Segment.Analytics.CSharp's <c>Flush()</c> only signals the
		/// library's own background delivery (its coroutine system) and exposes nothing awaitable,
		/// so there is no async work to represent here.
		/// </summary>
		public Task ShutDownAsync(CancellationToken cancellationToken = default)
		{
			ShutDown();
			return Task.CompletedTask;
		}

		/// <summary>See <see cref="ShutDownAsync"/> for why this completes synchronously.</summary>
		public Task FlushAsync(CancellationToken cancellationToken = default)
		{
			Flush();
			return Task.CompletedTask;
		}

		public Statistics Statistics => new Statistics(StatMonitor.Submitted,
			StatMonitor.Succeeded, StatMonitor.Failed);

		/// <summary>
		/// The Segment path already gets offline durability from Segment.Analytics.CSharp's own
		/// on-disk storage/retry, and this class does not maintain a separate spool of its own -- so
		/// there is nothing here to purge on consent revocation.
		/// </summary>
		public void PurgeQueuedEvents()
		{
		}

		/// <summary>
		/// No-op: this client has no separate background flush loop of its own to re-arm (the
		/// Segment.Analytics.CSharp library manages its own delivery), so there is nothing to resume.
		/// </summary>
		public void ResumeSending()
		{
		}

		public void OnExceptionThrown(Exception e)
		{
			Debug.WriteLine($"**** Segment.IO Failed to deliver. {e.Message}");

			lock (StatMonitor)
				StatMonitor.NoteFailures();
			Failed?.Invoke(e);
		}
	}
}