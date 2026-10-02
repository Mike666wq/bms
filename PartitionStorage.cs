using System;
using System.Data.SQLite;
using System.Globalization;
using System.Threading.Tasks;

namespace BmsSerialDemo
{
    public sealed partial class SampleStore
    {
        // Written through the existing single writer; never inferred back from a display filename.
        internal Task SetPartitionInfoAsync(string deviceId, DateTime startUtc, DateTime endUtc, int days, int epoch)
        {
            return Submit(delegate(SQLiteConnection c, SQLiteTransaction tx)
            {
                using (SQLiteCommand ddl = new SQLiteCommand("CREATE TABLE IF NOT EXISTS partition_info(singleton INTEGER PRIMARY KEY CHECK(singleton=1),device_id TEXT NOT NULL,source TEXT NOT NULL,start_utc_ms INTEGER NOT NULL,end_utc_ms INTEGER NOT NULL,days INTEGER NOT NULL,epoch INTEGER NOT NULL,time_zone TEXT NOT NULL)", c, tx)) ddl.ExecuteNonQuery();
                using (SQLiteCommand read = new SQLiteCommand("SELECT device_id,source,start_utc_ms,end_utc_ms,days,epoch FROM partition_info WHERE singleton=1", c, tx))
                using (SQLiteDataReader r = read.ExecuteReader())
                {
                    if (r.Read())
                    {
                        if (r.GetString(0) != deviceId || r.GetString(1) != source || r.GetInt64(2) != ToMs(startUtc) || r.GetInt64(3) != ToMs(endUtc) || r.GetInt32(4) != days || r.GetInt32(5) != epoch)
                            throw new InvalidOperationException("数据库的分期身份与当前设置不一致，已拒绝续写。");
                        return;
                    }
                }
                using (SQLiteCommand insert = new SQLiteCommand("INSERT INTO partition_info VALUES(1,@device,@source,@start,@end,@days,@epoch,@zone)", c, tx))
                {
                    insert.Parameters.AddWithValue("@device", deviceId); insert.Parameters.AddWithValue("@source", source);
                    insert.Parameters.AddWithValue("@start", ToMs(startUtc)); insert.Parameters.AddWithValue("@end", ToMs(endUtc));
                    insert.Parameters.AddWithValue("@days", days); insert.Parameters.AddWithValue("@epoch", epoch);
                    insert.Parameters.AddWithValue("@zone", TimeZoneInfo.Local.Id); insert.ExecuteNonQuery();
                }
            }, false);
        }
    }
}
