using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BmsSerialDemo
{
    internal sealed class PartitionDescriptor
    {
        public string DatabasePath { get; internal set; }
        public DateTime StartUtc { get; internal set; }
        public DateTime EndUtc { get; internal set; }
        public int Days { get; internal set; }
        public int Epoch { get; internal set; }
    }

    // One manager and one durable schedule per device/source pair. UTC intervals are authoritative;
    // local time is used only to make the database filename legible.
    internal sealed class PartitionCycleManager
    {
        const string Version = "partition-cycle-v2";
        readonly object sync = new object();
        readonly string dataRoot, deviceId, source, statePath;
        readonly Func<DateTime> utcClock;
        readonly Func<TimeZoneInfo> timeZoneProvider;
        int activeDays = 30, pendingDays, epoch = 1;
        DateTime anchorUtc = DateTime.MinValue, pendingBoundaryUtc = DateTime.MinValue, lastCycleStartUtc = DateTime.MinValue;
        string currentFileName = "", currentZoneId = "";
        bool invalidState;

        public int ActiveDays { get { lock (sync) return activeDays; } }
        public int PendingDays { get { lock (sync) return pendingDays; } }
        public DateTime AnchorUtc { get { lock (sync) return anchorUtc; } }

        public PartitionCycleManager(string root, string id, string sourceName, string path, Func<DateTime> clock = null, Func<TimeZoneInfo> zoneProvider = null)
        {
            if (String.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, "^[A-Za-z0-9_-]{16,64}$", RegexOptions.CultureInvariant)) throw new ArgumentException("设备标识格式无效", "id");
            if (sourceName != "serial" && sourceName != "simulation") throw new ArgumentException("source must be serial or simulation", "sourceName");
            dataRoot = Path.GetFullPath(root); deviceId = id; source = sourceName; statePath = Path.GetFullPath(path); utcClock = clock ?? delegate { return DateTime.UtcNow; }; timeZoneProvider = zoneProvider ?? delegate { return TimeZoneInfo.Local; };
            Load();
        }

        public PartitionDescriptor Describe(DateTime utc)
        {
            utc = AsUtc(utc);
            lock (sync)
            {
                ThrowIfInvalid();
                DateTime effective = utc;
                if (anchorUtc == DateTime.MinValue) { anchorUtc = effective; if (pendingDays > 0) pendingBoundaryUtc = anchorUtc.AddDays(activeDays); Save(); }
                if (lastCycleStartUtc != DateTime.MinValue && effective < lastCycleStartUtc) effective = lastCycleStartUtc;
                ApplyPendingIfDue(effective);
                DateTime start = CycleStart(effective, anchorUtc, activeDays), end = start.AddDays(activeDays);
                if (lastCycleStartUtc == DateTime.MinValue || start > lastCycleStartUtc)
                {
                    lastCycleStartUtc = start; currentFileName = ""; currentZoneId = ""; Save();
                }
                if (String.IsNullOrEmpty(currentFileName))
                {
                    TimeZoneInfo localZone = timeZoneProvider();
                    DateTime local = TimeZoneInfo.ConvertTimeFromUtc(start, localZone);
                    currentFileName = source + "_cycle_" + local.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture) + "_" + activeDays.ToString(CultureInfo.InvariantCulture) + "d_" + epoch.ToString("D6", CultureInfo.InvariantCulture) + "_001.db";
                    currentZoneId = localZone.Id; ValidateCurrentFileName(currentFileName, activeDays, epoch); Save();
                }
                string dir = Path.Combine(dataRoot, deviceId); Directory.CreateDirectory(dir);
                return new PartitionDescriptor { DatabasePath = Path.Combine(dir, currentFileName), StartUtc = start, EndUtc = end, Days = activeDays, Epoch = epoch };
            }
        }

        public string DatabasePath(DateTime utc) { return Describe(utc).DatabasePath; }

        public void Configure(int days, bool applyImmediately, DateTime nowUtc)
        {
            if (days < 1 || days > 3650) throw new ArgumentOutOfRangeException("days", "周期必须为1到3650天");
            nowUtc = AsUtc(nowUtc);
            lock (sync)
            {
                ThrowIfInvalid();
                DateTime effective = nowUtc;
                if (lastCycleStartUtc != DateTime.MinValue && effective < lastCycleStartUtc) effective = lastCycleStartUtc;
                if (anchorUtc != DateTime.MinValue) ApplyPendingIfDue(effective);
                if (anchorUtc == DateTime.MinValue)
                {
                    if (applyImmediately) { activeDays = days; pendingDays = 0; pendingBoundaryUtc = DateTime.MinValue; }
                    else { pendingDays = days == activeDays ? 0 : days; pendingBoundaryUtc = DateTime.MinValue; }
                }
                else if (applyImmediately)
                {
                    activeDays = days; pendingDays = 0; pendingBoundaryUtc = DateTime.MinValue;
                    anchorUtc = effective; lastCycleStartUtc = DateTime.MinValue; currentFileName = ""; currentZoneId = ""; epoch++;
                }
                else
                {
                    pendingDays = days == activeDays ? 0 : days;
                    pendingBoundaryUtc = pendingDays == 0 ? DateTime.MinValue : CycleStart(effective, anchorUtc, activeDays).AddDays(activeDays);
                }
                Save();
            }
        }

        // This method is intentionally observational: opening settings cannot create the first cycle or mutate state.
        public string Summary(DateTime nowUtc)
        {
            nowUtc = AsUtc(nowUtc);
            lock (sync)
            {
                ThrowIfInvalid();
                if (anchorUtc == DateTime.MinValue)
                {
                    DateTime projectedBoundary = nowUtc.AddDays(activeDays);
                    return "当前每 " + activeDays + " 天一库；尚未开始首期，若现在开始，预计下一切换 " + projectedBoundary.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "。" + (pendingDays > 0 ? "该边界后改为 " + pendingDays + " 天。" : "");
                }
                DateTime effective = nowUtc < lastCycleStartUtc ? lastCycleStartUtc : nowUtc;
                DateTime start = CycleStart(effective, anchorUtc, activeDays);
                DateTime nextBoundary = pendingDays > 0 ? pendingBoundaryUtc : start.AddDays(activeDays);
                string result = "当前每 " + activeDays + " 天一库（本地起始 " + start.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "）；下一切换 " + nextBoundary.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                if (pendingDays > 0) result += " 起改为 " + pendingDays + " 天。";
                else result += "。";
                return result + "采集间隔独立。";
            }
        }

        public string Summary() { return Summary(utcClock()); }

        void ApplyPendingIfDue(DateTime utc)
        {
            if (pendingDays <= 0 || pendingBoundaryUtc == DateTime.MinValue || utc < pendingBoundaryUtc) return;
            DateTime boundary = pendingBoundaryUtc;
            activeDays = pendingDays; pendingDays = 0; pendingBoundaryUtc = DateTime.MinValue;
            anchorUtc = boundary; epoch++; lastCycleStartUtc = boundary; currentFileName = ""; currentZoneId = "";
            // Persist the fixed transition before deriving the present cycle. A long shutdown must not move it.
            Save();
        }

        static DateTime CycleStart(DateTime utc, DateTime anchor, int days)
        {
            long span = TimeSpan.FromDays(days).Ticks;
            long delta = utc.Ticks - anchor.Ticks;
            long index = delta >= 0 ? delta / span : -(((-delta) + span - 1) / span);
            return new DateTime(anchor.Ticks + index * span, DateTimeKind.Utc);
        }

        static DateTime AsUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) return value;
            if (value.Kind == DateTimeKind.Local) return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        void Load()
        {
            if (!File.Exists(statePath)) return;
            try
            {
                string[] v = File.ReadAllLines(statePath, Encoding.UTF8);
                if (v.Length != 11 || v[0] != Version || v[1] != deviceId || v[2] != source) throw new FormatException("状态文件版本、设备或来源不匹配");
                int a = Int32.Parse(v[3], CultureInfo.InvariantCulture), p = Int32.Parse(v[4], CultureInfo.InvariantCulture), e = Int32.Parse(v[7], CultureInfo.InvariantCulture);
                long anchor = Int64.Parse(v[5], CultureInfo.InvariantCulture), boundary = Int64.Parse(v[6], CultureInfo.InvariantCulture), last = Int64.Parse(v[8], CultureInfo.InvariantCulture);
                string file = v[9], zone = v[10];
                if (a < 1 || a > 3650 || p < 0 || p > 3650 || e < 1 || anchor < 0 || boundary < 0 || last < 0 || (p == 0 && boundary != 0) || (p > 0 && anchor != 0 && boundary <= anchor) || (anchor == 0 && last != 0) || (last != 0 && (last < anchor || last > DateTime.MaxValue.Ticks)) || ((file.Length == 0) != (zone.Length == 0)) || (file.Length > 0 && (last == 0 || zone.Length > 128))) throw new FormatException("分期状态字段越界或矛盾");
                if (file.Length > 0) ValidateCurrentFileName(file, a, e);
                activeDays = a; pendingDays = p; epoch = e; anchorUtc = FromTicks(anchor); pendingBoundaryUtc = FromTicks(boundary); lastCycleStartUtc = FromTicks(last); currentFileName = file; currentZoneId = zone;
            }
            catch (Exception ex) { invalidState = true; throw new InvalidDataException("分期配置损坏或与当前设备/来源不匹配，已拒绝继续写入：" + statePath, ex); }
        }

        static DateTime FromTicks(long ticks) { return ticks == 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc); }
        void ThrowIfInvalid() { if (invalidState) throw new InvalidDataException("分期配置无效，已拒绝写入。"); }

        void Save()
        {
            string temp = null;
            try
            {
                string parent = Path.GetDirectoryName(statePath); if (String.IsNullOrEmpty(parent)) throw new InvalidOperationException("分期配置路径必须有父目录");
                Directory.CreateDirectory(parent);
                string body = Version + "\r\n" + deviceId + "\r\n" + source + "\r\n" + activeDays.ToString(CultureInfo.InvariantCulture) + "\r\n" + pendingDays.ToString(CultureInfo.InvariantCulture) + "\r\n" + Ticks(anchorUtc) + "\r\n" + Ticks(pendingBoundaryUtc) + "\r\n" + epoch.ToString(CultureInfo.InvariantCulture) + "\r\n" + Ticks(lastCycleStartUtc) + "\r\n" + currentFileName + "\r\n" + currentZoneId + "\r\n";
                temp = statePath + "." + Guid.NewGuid().ToString("N") + ".partial";
                using (FileStream fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (StreamWriter writer = new StreamWriter(fs, new UTF8Encoding(false))) { writer.Write(body); writer.Flush(); fs.Flush(true); }
                if (File.Exists(statePath)) File.Replace(temp, statePath, null); else File.Move(temp, statePath);
            }
            catch (Exception ex) { invalidState = true; throw new InvalidOperationException("分期状态保存失败；本管理器已停止写库，需重新加载确认状态：" + statePath, ex); }
            finally { if (!String.IsNullOrEmpty(temp) && File.Exists(temp)) File.Delete(temp); }
        }
        void ValidateCurrentFileName(string value, int days, int fileEpoch)
        {
            if (Path.GetFileName(value) != value) throw new FormatException("当前库文件名包含路径成分");
            Match m = Regex.Match(value, "^" + Regex.Escape(source) + "_cycle_([0-9]{8}T[0-9]{9})_([0-9]{1,4})d_([0-9]{6,})_001\\.db$", RegexOptions.CultureInvariant);
            DateTime localStart; int nameDays, nameEpoch;
            if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.None, out localStart) || !Int32.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out nameDays) || !Int32.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out nameEpoch) || nameDays != days || nameEpoch != fileEpoch)
                throw new FormatException("当前库文件名与周期状态不一致");
        }
        static string Ticks(DateTime value) { return value == DateTime.MinValue ? "0" : value.Ticks.ToString(CultureInfo.InvariantCulture); }
    }
}
