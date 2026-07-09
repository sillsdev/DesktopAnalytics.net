using System;
using System.Text;
using Segment.Serialization;

namespace DesktopAnalytics
{
	/// <summary>
	/// A single spoolable analytics event in a neutral, JSON-serializable form. This is the
	/// unit written to and read from the on-disk event spool (DiskQueue), independent of the
	/// eventual transport (Mixpanel, etc). Instances are round-tripped as UTF-8 JSON bytes so
	/// the spool stays portable and inspectable (see <see cref="ToBytes"/>/<see cref="FromBytes"/>).
	/// </summary>
	internal class AnalyticsEvent
	{
		public string AnalyticsId { get; set; }
		public string EventName { get; set; }

		/// <summary>Event properties, e.g. Message/Stack Trace for exception reports.</summary>
		public JsonObject Properties { get; set; }

		/// <summary>Mixpanel's $insert_id, stamped at enqueue time so at-least-once replay can
		/// be safely deduplicated by the backend.</summary>
		public string InsertId { get; set; }

		/// <summary>The original event time, stamped at enqueue time so a later replay is
		/// back-dated correctly rather than reported at delivery time.</summary>
		public DateTimeOffset Time { get; set; }

		public AnalyticsEvent()
		{
			Properties = new JsonObject();
		}

		/// <summary>
		/// Creates a new event, stamping <see cref="InsertId"/> and <see cref="Time"/>. Both are
		/// overridable so callers/tests can be deterministic; production code can omit them to get
		/// a real GUID and the real wall-clock time.
		/// </summary>
		/// <param name="analyticsId">The stable per-user analytics id.</param>
		/// <param name="eventName">The event name.</param>
		/// <param name="properties">Event properties. May be null, in which case an empty
		/// property bag is used.</param>
		/// <param name="insertId">The $insert_id. Defaults to a fresh GUID. Never call
		/// Guid.NewGuid() directly elsewhere in the stamping path -- pass it here so tests are
		/// deterministic.</param>
		/// <param name="time">The event time. Defaults to <c>DateTimeOffset.UtcNow</c>. Never
		/// call DateTime.Now/DateTimeOffset.UtcNow directly elsewhere in the stamping path --
		/// pass it here (e.g. from an injected <see cref="TimeProvider"/>) so tests are
		/// deterministic.</param>
		public static AnalyticsEvent Create(
			string analyticsId,
			string eventName,
			JsonObject properties = null,
			string insertId = null,
			DateTimeOffset? time = null
		)
		{
			return new AnalyticsEvent
			{
				AnalyticsId = analyticsId,
				EventName = eventName,
				Properties = properties ?? new JsonObject(),
				InsertId = insertId ?? Guid.NewGuid().ToString(),
				Time = time ?? DateTimeOffset.UtcNow
			};
		}

		/// <summary>Serializes this event as UTF-8 JSON bytes, suitable for enqueuing into the
		/// on-disk spool.</summary>
		public byte[] ToBytes()
		{
			var json = JsonUtility.ToJson(this, false);
			return Encoding.UTF8.GetBytes(json);
		}

		/// <summary>Deserializes an event previously produced by <see cref="ToBytes"/>.</summary>
		public static AnalyticsEvent FromBytes(byte[] bytes)
		{
			var json = Encoding.UTF8.GetString(bytes);
			return JsonUtility.FromJson<AnalyticsEvent>(json);
		}
	}
}
