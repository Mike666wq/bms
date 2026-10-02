using System;
using System.Data.SQLite;
using System.IO;
using System.Threading;

namespace BmsSerialDemo
{
    public sealed partial class MainForm
    {
        internal static int RunLocalIntegrationTests()
        {
            string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".it-"+Guid.NewGuid().ToString("N").Substring(0,6));
            string data=Path.Combine(root,"data"),settings=Path.Combine(root,"settings");Directory.CreateDirectory(settings);
            MainForm form=null;int checks=0;
            try
            {
                form=new MainForm(data,Path.Combine(settings,"recording-period.txt"));form.simulate.Checked=true;IntPtr h=form.Handle;
                form.ToggleConnection();
                if(form.activeStore!=null||File.Exists(Path.Combine(settings,"partition-cycle-simulation.txt")))throw new Exception("连接或浏览页面不应创建首次采集分期。");checks++;
                form.acquisitionRound=1;form.activeRound=1;form.pollCycleBusy=true;form.PrepareRoundPartition("simulation",DateTime.UtcNow,1);
                Frame sample=Protocol.Decode(Simulator.Respond(1,0x42,1)),alarm=Protocol.Decode(Simulator.Respond(1,0x44,1));
                form.Display(sample,0x42,1,0,1,2);string first=form.activeStore.DatabasePath;
                form.requestedPartitionSource="simulation";form.requestedPartitionDays=7;form.requestedPartitionImmediate=true;form.ApplyQueuedPartitionSettings();
                if(form.partitionManagers["simulation"].ActiveDays!=30)throw new Exception("读取轮次尚未完成时分期设置提前生效。");checks++;
                form.Display(alarm,0x44,1,0,1,2);if(form.activeStore.DatabasePath!=first)throw new Exception("同轮42/44跨库。");checks++;
                form.pollCycleBusy=false;form.ApplyQueuedPartitionSettings();
                form.acquisitionRound=2;form.activeRound=2;form.PrepareRoundPartition("simulation",DateTime.UtcNow,2);
                form.Display(sample,0x42,1,0,2,2);form.Display(alarm,0x44,1,0,2,2);string second=form.activeStore.DatabasePath;
                if(first==second||form.partitionManagers["simulation"].ActiveDays!=7)throw new Exception("下一轮未进入7天新库。");checks++;
                form.Disconnect();
                foreach(SampleStore db in form.stores.Values)db.Dispose();form.stores.Clear();form.activeStore=null;
                foreach(string db in new[]{first,second})using(SQLiteConnection c=new SQLiteConnection("Data Source="+db+";Version=3;Read Only=True;"))
                {
                    c.Open();using(SQLiteCommand q=new SQLiteCommand("SELECT (SELECT COUNT(*) FROM samples)+(SELECT COUNT(*) FROM alarm_observations)",c))
                        if(Convert.ToInt32(q.ExecuteScalar())!=2)throw new Exception("切库后样本或告警丢失。");
                    using(SQLiteCommand q=new SQLiteCommand("SELECT COUNT(*) FROM partition_info WHERE source='simulation'",c))if(Convert.ToInt32(q.ExecuteScalar())!=1)throw new Exception("分期身份未持久化到数据库。");checks++;
                }
                MonthlyCatalog catalog=new MonthlyCatalog(data,form.deviceId);
                if(catalog.Discover("simulation").Count!=2||catalog.Discover("serial").Count!=0)throw new Exception("周期库发现或来源隔离失败。");checks++;
                if(catalog.QueryPage("simulation",DateTime.UtcNow.AddHours(-1),DateTime.UtcNow.AddHours(1),0,null,200).Count!=2)throw new Exception("跨周期查询遗漏记录。");checks++;
                string exports=Path.Combine(root,"out");XlsxExportResult x=catalog.ExportXlsxAsync("simulation",DateTime.UtcNow.AddHours(-1),DateTime.UtcNow.AddHours(1),0,exports,null,CancellationToken.None).GetAwaiter().GetResult();
                if(x.SampleRows!=2||x.AlarmRows!=2)throw new Exception("跨周期Excel导出计数不正确。");checks++;
                form.Dispose();form=null;
                string faultData=Path.Combine(root,"fault-data");
                form=new MainForm(faultData,Path.Combine(root,"fault-settings","recording-period.txt"));form.simulate.Checked=true;h=form.Handle;form.ToggleConnection();
                form.PrepareRoundPartition("simulation",DateTime.UtcNow,1);
                form.Display(alarm,0x44,1,0,1,2);
                if(form.activeStore==null||form.recordingFailure!=null)throw new Exception("没有42采样时首个有效44告警被丢弃。");
                string alarmOnly=form.activeStore.DatabasePath;form.activeStore.StopSessionAsync().GetAwaiter().GetResult();
                using(SQLiteConnection c=new SQLiteConnection("Data Source="+alarmOnly+";Version=3;Read Only=True;")){c.Open();using(SQLiteCommand q=new SQLiteCommand("SELECT COUNT(*) FROM alarm_observations",c))if(Convert.ToInt32(q.ExecuteScalar())!=1)throw new Exception("独立告警未持久化。");}checks++;
                form.Disconnect();foreach(SampleStore db in form.stores.Values)db.Dispose();form.stores.Clear();form.activeStore=null;
                form.partitionManagers["simulation"].Configure(15,true,DateTime.UtcNow);form.PrepareRoundPartition("simulation",DateTime.UtcNow,2);
                PartitionDescriptor bad=form.roundPartitions[2];
                using(SampleStore poison=new SampleStore("simulation",bad.DatabasePath,true,false)){poison.Ready.GetAwaiter().GetResult();poison.SetPartitionInfoAsync("wrong-device",bad.StartUtc,bad.EndUtc,bad.Days,bad.Epoch).GetAwaiter().GetResult();}
                form.ToggleConnection();form.polling=true;form.Display(sample,0x42,1,0,2,2);
                if(form.recordingFailure==null||form.polling||form.activeStore!=null||form.stores.ContainsKey("simulation"))throw new Exception("分期身份冲突未停止采集或留下坏store。");checks++;
                if(!form.status.Text.StartsWith("本地记录故障",StringComparison.Ordinal))throw new Exception("读取成功掩盖了存储故障。");checks++;
                return checks;
            }
            finally
            {
                if(form!=null){form.previewMode=true;form.pollCycleBusy=false;form.Disconnect();foreach(SampleStore db in form.stores.Values)try{db.Dispose();}catch{}form.stores.Clear();form.Dispose();}
                string absolute=Path.GetFullPath(root),baseRoot=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                if(absolute.StartsWith(baseRoot,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(absolute))Directory.Delete(absolute,true);
            }
        }
    }
}

