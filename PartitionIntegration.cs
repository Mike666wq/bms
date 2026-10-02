using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BmsSerialDemo
{
    public sealed partial class MainForm
    {
        readonly Dictionary<string,PartitionCycleManager> partitionManagers=new Dictionary<string,PartitionCycleManager>(StringComparer.Ordinal);
        readonly HashSet<string> partitionUnavailable=new HashSet<string>(StringComparer.Ordinal);
        string partitionStartupNotice="";
        readonly Dictionary<long,PartitionDescriptor> roundPartitions=new Dictionary<long,PartitionDescriptor>();
        PartitionSettingsControl partitionSettings;
        string requestedPartitionSource;
        int requestedPartitionDays;
        bool requestedPartitionImmediate;
        string recordingFailure;

        void StopForRecordingFailure(Exception error)
        {
            if(polling)TogglePolling();
            recordingFailure="本地记录故障，采集已停止："+error.Message;
            Notice(recordingFailure);
        }

        void InitializePartitionManagers()
        {
            string settingsRoot=Path.GetDirectoryName(periodSettingsPath);
            foreach(string source in new[]{"serial","simulation"})
            {
                try
                {
                    PartitionCycleManager manager=new PartitionCycleManager(dataRoot,deviceId,source,Path.Combine(settingsRoot,"partition-cycle-"+source+".txt"));
                    partitionManagers.Add(source,manager);
                    string notice=manager.StartupNotice;
                    if(!String.IsNullOrEmpty(notice))partitionStartupNotice=(partitionStartupNotice.Length==0?"":partitionStartupNotice+"；")+notice;
                }
                catch(Exception e)
                {
                    // 单个来源的分期初始化失败不再阻断整个应用启动；该来源在本次运行中禁止写库，历史数据仍可查看。
                    partitionUnavailable.Add(source);
                    partitionStartupNotice=(partitionStartupNotice.Length==0?"":partitionStartupNotice+"；")+"本地记录分期（"+source+"）初始化失败，该来源本次运行不可记录："+e.Message;
                    CrashLogger.Write("partition manager init failed for "+source,e);
                }
            }
            if(partitionStartupNotice.Length>0)Notice(partitionStartupNotice);
        }
        PartitionCycleManager GetPartitionManager(string source)
        {
            if(partitionUnavailable.Contains(source))throw new InvalidOperationException("本地记录分期（"+source+"）不可用：初始化失败或状态损坏，本次运行禁止写库");
            PartitionCycleManager manager;
            if(!partitionManagers.TryGetValue(source,out manager))throw new InvalidOperationException("本地记录分期（"+source+"）不存在");
            return manager;
        }
        bool IsPartitionAvailable(string source){return !partitionUnavailable.Contains(source)&&partitionManagers.ContainsKey(source);}
        void BuildRecordingPage(TabPage page)
        {
            partitionSettings=new PartitionSettingsControl(delegate{if(!IsPartitionAvailable(storagePage.SelectedSource))return "本地记录分期（"+storagePage.SelectedSource+"）不可用：本次运行禁止写库，历史数据仍可查看。";return GetPartitionManager(storagePage.SelectedSource).Summary(DateTime.UtcNow);});
            partitionSettings.Dock=DockStyle.Top;
            partitionSettings.Requested+=delegate(object sender,PartitionSettingsRequestedEventArgs args)
            {
                requestedPartitionSource=storagePage.SelectedSource;requestedPartitionDays=args.Days;requestedPartitionImmediate=args.ApplyImmediately;
                if(pollCycleBusy){Notice("分期设置已排队，将在当前实时数据和告警读取完成后处理。");return;}
                try{ApplyQueuedPartitionSettings();}catch(Exception e){Notice("分期设置未保存："+e.Message);}
            };
            storagePage.SelectedSourceChanged+=delegate{SyncPartitionSettingsDays();partitionSettings.RefreshSummary();};
            SyncPartitionSettingsDays();
            TableLayoutPanel recordLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,BackColor=Color.FromArgb(243,247,251),Padding=new Padding(4)};
            recordLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));recordLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));recordLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            recordLayout.Controls.Add(partitionSettings,0,0);recordLayout.Controls.Add(storagePage,0,1);page.Controls.Add(recordLayout);
        }
        void ApplyQueuedPartitionSettings()
        {
            if(pollCycleBusy||requestedPartitionDays==0)return;
            if(!IsPartitionAvailable(requestedPartitionSource)){Notice("分期设置未保存：本地记录分期（"+requestedPartitionSource+"）不可用，本次运行禁止写库。");return;}
            GetPartitionManager(requestedPartitionSource).Configure(requestedPartitionDays,requestedPartitionImmediate,DateTime.UtcNow);
            requestedPartitionDays=0;
            if(partitionSettings!=null){SyncPartitionSettingsDays();partitionSettings.RefreshSummary();}
            Notice(requestedPartitionImmediate?"分期设置已保存；下一轮采集写入新分期，历史数据保留。":"分期设置已保存；当前分期结束后生效，历史数据保留。");
        }
        void SyncPartitionSettingsDays()
        {
            if(partitionSettings==null||storagePage==null)return;
            string source=storagePage.SelectedSource;
            if(String.IsNullOrEmpty(source)||!IsPartitionAvailable(source))return;
            PartitionCycleManager manager=GetPartitionManager(source);
            partitionSettings.SetCurrentDays(manager.ActiveDays,manager.PendingDays);
        }
        void PrepareRoundPartition(string source,DateTime utc,long round)
        {
            PartitionDescriptor descriptor=GetPartitionManager(source).Describe(utc);
            roundPartitions[round]=descriptor;roundDatabase[round]=descriptor.DatabasePath;
            if(roundPartitions.Count>128)
            {
                long oldest=Int64.MaxValue;foreach(long key in roundPartitions.Keys)if(key<oldest)oldest=key;
                roundPartitions.Remove(oldest);roundDatabase.Remove(oldest);
            }
            if(partitionSettings!=null){SyncPartitionSettingsDays();partitionSettings.RefreshSummary();}
        }
    }
}
