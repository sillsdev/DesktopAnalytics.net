using System.IO;
using Microsoft.Data.Sqlite;

namespace DesktopAnalyticsTests
{
	// Shared by EventSpoolContractTests and MixpanelClientTests: both simulate an
	// undeserializable/corrupt spooled row by opening a second raw connection to the same
	// spool.db and overwriting payload bytes directly, bypassing the spool's own Enqueue path
	// (which would refuse to write anything but valid serialized events).
	internal static class SpoolCorruptionTestHelper
	{
		public static void CorruptAllPayloads(string spoolDir) =>
			Execute(spoolDir, "UPDATE events SET payload = X'00', len = 1;");

		public static void CorruptOldestPayload(string spoolDir) =>
			Execute(spoolDir, "UPDATE events SET payload = X'00', len = 1 " +
				"WHERE id = (SELECT MIN(id) FROM events);");

		private static void Execute(string spoolDir, string sql)
		{
			using (var conn = new SqliteConnection("Data Source=" + Path.Combine(spoolDir, "spool.db")))
			{
				conn.Open();
				using (var cmd = conn.CreateCommand())
				{
					cmd.CommandText = sql;
					cmd.ExecuteNonQuery();
				}
			}
		}
	}
}
