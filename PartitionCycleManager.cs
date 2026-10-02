using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

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
        // C3：状态保存失败不再永久停库。saveDegraded 表示"持久化降级"——周期切换暂缓，当前库继续写。
        bool saveDegraded;
        string saveDegradedError = "";
        string startupNotice = "";

        public string StartupNotice { get { lock (sync) return startupNotice; } }
        public bool SaveDegraded { get { lock (sync) return saveDegraded; } }
        public string SaveDegradedError { get { lock (sync) return saveDegradedError; } }

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
                DateTime effective = utc;
                if (anchorUtc == DateTime.MinValue) { anchorUtc = effective; if (pendingDays > 0) pendingBoundaryUtc = anchorUtc.AddDays(activeDays); TrySaveWithRetry(); }
                if (lastCycleStartUtc != DateTime.MinValue && effective < lastCycleStartUtc) effective = lastCycleStartUtc;
                ApplyPendingIfDue(effective);
                DateTime start = CycleStart(effective, anchorUtc, activeDays);
                if (lastCycleStartUtc == DateTime.MinValue || start > lastCycleStartUtc)
                {
                    // C3：周期切换必须先持久化；保存失败则回滚到当前周期继续写库，下个采集周期自动重试。
                    // 首个周期没有可回滚的旧库，保留内存态继续（目录发现兜底历史，重启后重开新周期）。
                    bool hadCycle = lastCycleStartUtc != DateTime.MinValue && currentFileName.Length > 0;
                    StateSnapshot snapshot = TakeSnapshot();
                    lastCycleStartUtc = start; currentFileName = ""; currentZoneId = "";
                    if (!TrySaveWithRetry() && hadCycle) { RestoreSnapshot(snapshot); start = lastCycleStartUtc; }
                }
                DateTime end = start.AddDays(activeDays);
                if (String.IsNullOrEmpty(currentFileName))
                {
                    TimeZoneInfo localZone = timeZoneProvider();
                    DateTime local = TimeZoneInfo.ConvertTimeFromUtc(start, localZone);
                    currentFileName = source + "_cycle_" + local.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture) + "_" + activeDays.ToString(CultureInfo.InvariantCulture) + "d_" + epoch.ToString("D6", CultureInfo.InvariantCulture) + "_001.db";
                    currentZoneId = localZone.Id; ValidateCurrentFileName(currentFileName, activeDays, epoch);
                    TrySaveWithRetry();
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
                StateSnapshot snapshot = TakeSnapshot();
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
                // C3：设置无法持久化时回滚并报错（不损坏内存态、不影响数据记录），用户可重试。
                if (!TrySaveWithRetry()) { string detail = saveDegradedError; RestoreSnapshot(snapshot); throw new InvalidOperationException("分期设置未能保存（" + detail + "）；周期设置未变更，数据记录未受影响：" + statePath); }
            }
        }

        // This method is intentionally observational: opening settings cannot create the first cycle or mutate state.
        public string Summary(DateTime nowUtc)
        {
            nowUtc = AsUtc(nowUtc);
            lock (sync)
            {
                string degraded = saveDegraded ? "注意：分期状态保存持续失败（" + saveDegradedError + "），周期切换暂缓，数据仍写入当前库。" : "";
                if (anchorUtc == DateTime.MinValue)
                {
                    DateTime projectedBoundary = nowUtc.AddDays(activeDays);
                    return "当前每 " + activeDays + " 天一库；尚未开始首期，若现在开始，预计下一切换 " + projectedBoundary.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "。" + (pendingDays > 0 ? "该边界后改为 " + pendingDays + " 天。" : "") + degraded;
                }
                DateTime effective = nowUtc < lastCycleStartUtc ? lastCycleStartUtc : nowUtc;
                DateTime start = CycleStart(effective, anchorUtc, activeDays);
                DateTime nextBoundary = pendingDays > 0 ? pendingBoundaryUtc : start.AddDays(activeDays);
                string result = "当前每 " + activeDays + " 天一库（本地起始 " + start.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "）；下一切换 " + nextBoundary.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                if (pendingDays > 0) result += " 起改为 " + pendingDays + " 天。";
                else result += "。";
                return result + "采集间隔独立。" + degraded;
            }
        }

        public string Summary() { return Summary(utcClock()); }

        void ApplyPendingIfDue(DateTime utc)
        {
            if (pendingDays <= 0 || pendingBoundaryUtc == DateTime.MinValue || utc < pendingBoundaryUtc) return;
            // Persist the fixed transition before deriving the present cycle. A long shutdown must not move it.
            // C3：无法持久化时回滚，下一边界重试；期间按原周期继续写当前库。
            StateSnapshot snapshot = TakeSnapshot();
            DateTime boundary = pendingBoundaryUtc;
            activeDays = pendingDays; pendingDays = 0; pendingBoundaryUtc = DateTime.MinValue;
            anchorUtc = boundary; epoch++; lastCycleStartUtc = boundary; currentFileName = ""; currentZoneId = "";
            if (!TrySaveWithRetry()) RestoreSnapshot(snapshot);
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
            catch (Exception ex)
            {
                // 损坏/身份不符的状态不再让应用启动即崩溃：隔离原始文件保留证据，重置为默认周期继续运行。
                // 周期库文件不删除，历史数据仍可查询；重置只影响“当前周期指针”，下次写入从新周期开始。
                startupNotice = "分期配置与当前设备/来源不符或已损坏，原文件已隔离备份，分期已重置为默认 30 天。" + statePath;
                try { QuarantineStateFile(ex); } catch (Exception moveEx) { startupNotice = "分期配置损坏且隔离备份失败，将在首次保存时覆盖：" + statePath; CrashLogger.Write("分期状态文件隔离备份失败", new AggregateException(ex, moveEx)); }
                activeDays = 30; pendingDays = 0; epoch = 1; anchorUtc = DateTime.MinValue; pendingBoundaryUtc = DateTime.MinValue; lastCycleStartUtc = DateTime.MinValue; currentFileName = ""; currentZoneId = "";
            }
        }

        void QuarantineStateFile(Exception cause)
        {
            string target = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                target = statePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture) + (attempt == 0 ? "" : "-" + attempt.ToString(CultureInfo.InvariantCulture));
                if (!File.Exists(target)) break;
                target = null;
            }
            if (target == null) throw new IOException("找不到可用的隔离备份文件名");
            File.Move(statePath, target);
            CrashLogger.Write("分期状态文件已隔离备份：" + target, cause);
        }

        static DateTime FromTicks(long ticks) { return ticks == 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc); }

        struct StateSnapshot
        {
            public int ActiveDays, PendingDays, Epoch;
            public DateTime AnchorUtc, PendingBoundaryUtc, LastCycleStartUtc;
            public string CurrentFileName, CurrentZoneId;
        }
        StateSnapshot TakeSnapshot() { return new StateSnapshot { ActiveDays = activeDays, PendingDays = pendingDays, Epoch = epoch, AnchorUtc = anchorUtc, PendingBoundaryUtc = pendingBoundaryUtc, LastCycleStartUtc = lastCycleStartUtc, CurrentFileName = currentFileName, CurrentZoneId = currentZoneId }; }
        void RestoreSnapshot(StateSnapshot s) { activeDays = s.ActiveDays; pendingDays = s.PendingDays; epoch = s.Epoch; anchorUtc = s.AnchorUtc; pendingBoundaryUtc = s.PendingBoundaryUtc; lastCycleStartUtc = s.LastCycleStartUtc; currentFileName = s.CurrentFileName; currentZoneId = s.CurrentZoneId; }

        // C3：返回 true 表示已持久化；false 表示有限重试后仍失败（降级：调用方决定回滚或保留内存态）。
        // 杀软短暂锁文件等瞬态故障由 3 次尝试 + 退避吸收；持续故障只延缓周期切换，绝不停止数据记录。
        bool TrySaveWithRetry()
        {
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) Thread.Sleep(attempt == 1 ? 80 : 200);
                try { SaveOnce(); saveDegraded = false; saveDegradedError = ""; return true; }
                catch (Exception ex) { last = ex; }
            }
            if (!saveDegraded) CrashLogger.Write("分期状态保存失败（已重试3次）；周期切换暂缓，数据仍写入当前库：" + statePath, last);
            saveDegraded = true; saveDegradedError = last == null ? "未知错误" : last.Message;
            return false;
        }
        void SaveOnce()
        {
            string temp = null;
            Mutex stateMutex = new Mutex(false, @"Global\BmsSerialDemo.PartitionState." + MutexKey(statePath));
            bool owns = false;
            try
            {
                try { owns = stateMutex.WaitOne(TimeSpan.FromSeconds(2)); } catch (AbandonedMutexException) { owns = true; }
                if (!owns) throw new IOException("分期状态文件正被其他进程写入");
                string parent = Path.GetDirectoryName(statePath); if (String.IsNullOrEmpty(parent)) throw new InvalidOperationException("分期配置路径必须有父目录");
                Directory.CreateDirectory(parent);
                string body = Version + "\r\n" + deviceId + "\r\n" + source + "\r\n" + activeDays.ToString(CultureInfo.InvariantCulture) + "\r\n" + pendingDays.ToString(CultureInfo.InvariantCulture) + "\r\n" + Ticks(anchorUtc) + "\r\n" + Ticks(pendingBoundaryUtc) + "\r\n" + epoch.ToString(CultureInfo.InvariantCulture) + "\r\n" + Ticks(lastCycleStartUtc) + "\r\n" + currentFileName + "\r\n" + currentZoneId + "\r\n";
                temp = statePath + "." + Guid.NewGuid().ToString("N") + ".partial";
                using (FileStream fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (StreamWriter writer = new StreamWriter(fs, new UTF8Encoding(false))) { writer.Write(body); writer.Flush(); fs.Flush(true); }
                if (File.Exists(statePath)) File.Replace(temp, statePath, null); else File.Move(temp, statePath);
            }
            finally
            {
                if (owns) try { stateMutex.ReleaseMutex(); } catch { }
                stateMutex.Dispose();
                if (!String.IsNullOrEmpty(temp) && File.Exists(temp)) try { File.Delete(temp); } catch { }
            }
        }
        static string MutexKey(string path) { using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create()) { byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant())); return BitConverter.ToString(hash).Replace("-", ""); } }
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
