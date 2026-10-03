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
                Check(control.SummaryText.Contains("下一切换"), "设置控件展示manager摘要");
                control.SetCurrentDays(7, 0); Check(control.SelectedDays == 7, "C11设置控件回读7天预设而非恒显30天");
                control.SetCurrentDays(45, 0); Check(control.SelectedDays == 45, "C11设置控件回读非预设天数切自定义并填值");
                control.SetCurrentDays(30, 15); Check(control.SelectedDays == 15, "已排期的15天显示在编辑器中，当前周期仍由摘要显示");
                var preset = (System.Windows.Forms.ComboBox)typeof(PartitionSettingsControl).GetField("preset", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(control);
                var custom = (System.Windows.Forms.NumericUpDown)typeof(PartitionSettingsControl).GetField("custom", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(control);
                preset.SelectedIndex = 0;
                for (int i = 0; i < 5; i++) control.SetCurrentDays(30, 0);
                Check(control.SelectedDays == 7, "重复采集刷新不覆盖未保存的7天选择");
                preset.SelectedIndex = 1; control.SetCurrentDays(30, 0);
                Check(control.SelectedDays == 15, "采集刷新不覆盖15天选择");
                preset.SelectedIndex = 3; custom.Value = 12; control.SetCurrentDays(30, 0);
                Check(control.SelectedDays == 12 && custom.Enabled, "采集刷新不覆盖自定义周期");
                int submitted = 0; control.Requested += delegate(object sender, PartitionSettingsRequestedEventArgs args) { submitted = args.Days; };
                typeof(PartitionSettingsControl).GetMethod("Raise", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(control, new object[] { false });
                Check(submitted == 12, "提交使用保留的自定义周期");
                control.SetCurrentDays(30, 12, true); control.SetCurrentDays(30, 12);
                Check(control.SelectedDays == 12, "保存下期生效后不跳回当前30天");
                control.SetCurrentDays(7, 0, true);
                Check(control.SelectedDays == 7, "切换数据源或立即生效后重新读取已保存周期");
                control.Dispose();
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
                string before = File.ReadAllText(corrupt);
                PartitionCycleManager recovered = new PartitionCycleManager(data, "stable-device-id", "serial", corrupt);
                Check(!String.IsNullOrEmpty(recovered.StartupNotice) && recovered.ActiveDays == 30 && recovered.AnchorUtc == DateTime.MinValue, "损坏配置隔离重置为默认周期并给出启动提示");
                string[] quarantined = Directory.GetFiles(settings, "corrupt.txt.corrupt-*");
                Check(!File.Exists(corrupt) && quarantined.Length == 1 && File.ReadAllText(quarantined[0]) == before, "损坏配置原始字节保留在.corrupt-隔离备份中且原路径已让位");
                PartitionDescriptor recoveredCycle = recovered.Describe(start);
                Check(recoveredCycle.Days == 30 && recoveredCycle.StartUtc == start, "隔离重置后可直接开始新周期写库");
                string traversal = Path.Combine(settings, "traversal.txt"); string[] tampered = File.ReadAllLines(serialState); tampered[9] = "..\\evil.db"; File.WriteAllLines(traversal, tampered, new UTF8Encoding(false));
                string traversalBefore = File.ReadAllText(traversal);
                PartitionCycleManager traversalRecovered = new PartitionCycleManager(data, "stable-device-id", "serial", traversal);
                Check(!String.IsNullOrEmpty(traversalRecovered.StartupNotice) && !File.Exists(traversal) && Directory.GetFiles(settings, "traversal.txt.corrupt-*").Length == 1 && File.ReadAllText(Directory.GetFiles(settings, "traversal.txt.corrupt-*")[0]) == traversalBefore, "路径穿越配置同样隔离重置且保留原始内容");
                PartitionCycleManager mismatch = new PartitionCycleManager(data, "another-device-id", "serial", serialState);
                Check(!String.IsNullOrEmpty(mismatch.StartupNotice) && !File.Exists(serialState) && Directory.GetFiles(settings, "serial.txt.corrupt-*").Length == 1, "设备身份不匹配时隔离重置，不静默续写也不崩溃");

                string blockedPath = Path.Combine(settings, "blocked-state"); Directory.CreateDirectory(blockedPath);
                PartitionCycleManager blocked = new PartitionCycleManager(data, "stable-device-id", "serial", blockedPath);
                PartitionDescriptor blockedFirst = blocked.Describe(start);
                Check(blockedFirst.Days == 30 && blocked.SaveDegraded && !String.IsNullOrEmpty(blocked.SaveDegradedError), "C3保存失败降级：首期仍返回可写描述符并标记降级");
                Check(blocked.Describe(start.AddDays(1)).DatabasePath == blockedFirst.DatabasePath && blocked.Summary(start.AddDays(1)).Contains("数据仍写入当前库"), "C3降级期间继续写当前库且Summary区分提示");
                PartitionDescriptor blockedRollover = blocked.Describe(start.AddDays(31));
                Check(blockedRollover.DatabasePath == blockedFirst.DatabasePath && blockedRollover.StartUtc == start, "C3降级期间周期切换暂缓，不阻断记录");
                bool configureFailed = false; try { blocked.Configure(7, false, start.AddDays(31)); } catch (InvalidOperationException e) { configureFailed = e.Message.Contains("周期设置未变更"); }
                Check(configureFailed && blocked.PendingDays == 0, "C3降级期间设置变更回滚并报错，不损坏内存态");
                Directory.Delete(blockedPath);
                PartitionDescriptor recoveredRollover = blocked.Describe(start.AddDays(31));
                Check(!blocked.SaveDegraded && recoveredRollover.DatabasePath != blockedFirst.DatabasePath && recoveredRollover.StartUtc == start.AddDays(30), "C3障碍消除后自动完成持久化与周期切换");
                return checks;
            }
            finally { string checkedRoot=Path.GetFullPath(root), baseRoot=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar; if(checkedRoot.StartsWith(baseRoot,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(checkedRoot))Directory.Delete(checkedRoot,true); }
        }
    }
}
