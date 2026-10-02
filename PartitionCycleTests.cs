using System;
using System.IO;
using System.Text;

namespace BmsSerialDemo
{
    internal static class PartitionCycleTests
    {
        static int checks;
        static void Check(bool value, string label) { if (!value) throw new Exception("分期测试失败：" + label); checks++; }
        static DateTime U(DateTime value) { return DateTime.SpecifyKind(value, DateTimeKind.Utc); }

        // Isolated deterministic tests; does not open the real data/log directories or a serial port.
        internal static int Run()
        {
            checks = 0;
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".pt-" + Guid.NewGuid().ToString("N").Substring(0,6));
            string data = Path.Combine(root, "data"), settings = Path.Combine(root, "settings");
            string serialState = Path.Combine(settings, "serial.txt"), simState = Path.Combine(settings, "simulation.txt");
            DateTime start = U(new DateTime(2024, 12, 30, 12, 0, 0));
            try
            {
                PartitionCycleManager m = new PartitionCycleManager(data, "stable-device-id", "serial", serialState, delegate { return start; });
                string summary = m.Summary();
                Check(m.AnchorUtc == DateTime.MinValue && !File.Exists(serialState) && summary.Contains("尚未开始首期") && summary.Contains("预计下一切换"), "Summary不创建首期且展示预计下一切换");
                PartitionDescriptor first = m.Describe(start);
                Check(first.Days == 30 && first.StartUtc == start && first.EndUtc == start.AddDays(30), "默认30天及UTC边界");
                Check(!Path.GetFileName(first.DatabasePath).Contains("Z") && Path.GetFileName(first.DatabasePath).StartsWith("serial_cycle_", StringComparison.Ordinal), "文件名本地显示且不含UTC后缀");
                Check(m.Describe(start.AddDays(29)).DatabasePath == first.DatabasePath, "周期内路径稳定");
                Check(m.Summary(start.AddDays(1)).Contains(start.AddDays(30).ToLocalTime().ToString("yyyy-MM-dd HH:mm")), "未设置pending时Summary展示下一边界");
                PartitionSettingsControl control = new PartitionSettingsControl(delegate { return m.Summary(start.AddDays(1)); });
                Check(control.SummaryText.Contains("下一切换"), "设置控件展示manager摘要"); control.Dispose();
                PartitionCycleManager restarted = new PartitionCycleManager(data, "stable-device-id", "serial", serialState);
                Check(restarted.Describe(start.AddDays(1)).DatabasePath == first.DatabasePath, "重启恢复当前周期");

                restarted.Configure(7, false, start.AddDays(10));
                restarted.Configure(15, false, start.AddDays(11));
                Check(restarted.PendingDays == 15, "连续延期设置以最后一次值替换并保持当前周期");
                restarted.Configure(7, false, start.AddDays(12));
                DateTime fixedBoundary = start.AddDays(30);
                Check(restarted.PendingDays == 7 && restarted.Summary(start.AddDays(12)).Contains(fixedBoundary.ToLocalTime().ToString("yyyy-MM-dd HH:mm")) && restarted.Describe(fixedBoundary.AddTicks(-1)).Days == 30, "下一期变更固定边界、Summary提示且边界前不变");
                restarted = new PartitionCycleManager(data, "stable-device-id", "serial", serialState);
                PartitionDescriptor after = restarted.Describe(fixedBoundary.AddDays(20));
                Check(after.Days == 7 && after.StartUtc == fixedBoundary.AddDays(14) && after.Epoch == 2, "重启和停机跨期后按固定边界推导当前7天分期");
                Check(restarted.PendingDays == 0, "待生效设置仅应用一次");
                Check(restarted.Describe(fixedBoundary.AddDays(5)).DatabasePath == after.DatabasePath, "系统时钟回拨不会写回已封存分期");

                restarted.Configure(15, false, fixedBoundary.AddDays(20));
                DateTime secondBoundary = fixedBoundary.AddDays(21);
                PartitionDescriptor longSleep = restarted.Describe(secondBoundary.AddDays(100));
                Check(longSleep.Days == 15 && longSleep.StartUtc == secondBoundary.AddDays(90), "长停机跨多个新周期仍从固定边界计算");
                restarted.Configure(37, true, secondBoundary.AddDays(101));
                PartitionDescriptor immediate = restarted.Describe(secondBoundary.AddDays(101));
                Check(immediate.Days == 37 && immediate.Epoch == longSleep.Epoch + 1 && immediate.DatabasePath != longSleep.DatabasePath, "立即新期与自定义周期");
                restarted.Configure(30, true, secondBoundary.AddDays(102));
                Check(restarted.Describe(secondBoundary.AddDays(102)).Days == 30, "30天立即设置");
                restarted.Configure(15, true, secondBoundary.AddDays(103));
                Check(restarted.Describe(secondBoundary.AddDays(103)).Days == 15, "15天立即设置");
                restarted.Configure(7, true, secondBoundary.AddDays(104));
                Check(restarted.Describe(secondBoundary.AddDays(104)).Days == 7, "7天立即设置");

                string zoneState = Path.Combine(settings, "zone.txt");
                TimeZoneInfo plusThree = TimeZoneInfo.CreateCustomTimeZone("BmsTestPlusThree", TimeSpan.FromHours(3), "BMS Test +03", "BMS Test +03");
                PartitionCycleManager zoneManager = new PartitionCycleManager(data, "stable-device-id", "serial", zoneState, null, delegate { return plusThree; });
                string zonePath = zoneManager.Describe(start).DatabasePath;
                PartitionCycleManager changedZone = new PartitionCycleManager(data, "stable-device-id", "serial", zoneState, null, delegate { return TimeZoneInfo.Utc; });
                Check(changedZone.Describe(start.AddDays(1)).DatabasePath == zonePath, "重启或系统时区变化后恢复已持久化的当期文件名");
                changedZone.Configure(7, true, start.AddDays(1));
                PartitionDescriptor newZoneCycle = changedZone.Describe(start.AddDays(1));
                Check(newZoneCycle.Days == 7 && newZoneCycle.DatabasePath != zonePath && Path.GetFileName(newZoneCycle.DatabasePath).Contains("20241231T120000000"), "新分期使用当前时区生成本地文件名");

                PartitionCycleManager simulation = new PartitionCycleManager(data, "stable-device-id", "simulation", simState);
                PartitionDescriptor sim = simulation.Describe(start);
                Check(sim.DatabasePath != first.DatabasePath && sim.StartUtc == first.StartUtc, "serial与simulation分期隔离");
                PartitionCycleManager loaded = new PartitionCycleManager(data, "stable-device-id", "serial", serialState);
                Check(loaded.ActiveDays == 7 && loaded.Describe(secondBoundary.AddDays(104)).Days == 7, "连续切换后的周期设置跨重启保持");

                string corrupt = Path.Combine(settings, "corrupt.txt"); Directory.CreateDirectory(settings); File.WriteAllText(corrupt, "partial damaged state", new UTF8Encoding(false));
                string before = File.ReadAllText(corrupt); bool rejected = false;
                try { new PartitionCycleManager(data, "stable-device-id", "serial", corrupt); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && File.ReadAllText(corrupt) == before, "损坏配置显式拒绝且不静默覆盖");
                bool mismatch = false; try { new PartitionCycleManager(data, "another-device-id", "serial", serialState); } catch (InvalidDataException) { mismatch = true; }
                Check(mismatch, "设备配置身份不匹配时拒绝续写");
                string traversal = Path.Combine(settings, "traversal.txt"); string[] tampered = File.ReadAllLines(serialState); tampered[9] = "..\\evil.db"; File.WriteAllLines(traversal, tampered, new UTF8Encoding(false));
                bool traversalRejected = false; try { new PartitionCycleManager(data, "stable-device-id", "serial", traversal); } catch (InvalidDataException) { traversalRejected = true; }
                Check(traversalRejected, "配置文件名路径穿越校验");

                string blockedPath = Path.Combine(settings, "blocked-state"); Directory.CreateDirectory(blockedPath);
                PartitionCycleManager blocked = new PartitionCycleManager(data, "stable-device-id", "serial", blockedPath);
                bool saveFailed = false; try { blocked.Describe(start); } catch (InvalidOperationException) { saveFailed = true; }
                bool rejectedAfterFailure = false; try { blocked.Describe(start.AddDays(1)); } catch (InvalidDataException) { rejectedAfterFailure = true; }
                Check(saveFailed && rejectedAfterFailure && Directory.Exists(blockedPath), "原子保存失败后本实例持续fail-closed");
                return checks;
            }
            finally { string checkedRoot=Path.GetFullPath(root), baseRoot=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar; if(checkedRoot.StartsWith(baseRoot,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(checkedRoot))Directory.Delete(checkedRoot,true); }
        }
    }
}
