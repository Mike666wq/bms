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
                partitionManagers.Add(source,new PartitionCycleManager(dataRoot,deviceId,source,Path.Combine(settingsRoot,"partition-cycle-"+source+".txt")));
        }
        void BuildRecordingPage(TabPage page)
        {
            partitionSettings=new PartitionSettingsControl(delegate{return partitionManagers[storagePage.SelectedSource].Summary(DateTime.UtcNow);});
            partitionSettings.Dock=DockStyle.Top;
            partitionSettings.Requested+=delegate(object sender,PartitionSettingsRequestedEventArgs args)
            {
                requestedPartitionSource=storagePage.SelectedSource;requestedPartitionDays=args.Days;requestedPartitionImmediate=args.ApplyImmediately;
                if(pollCycleBusy){Notice("分期设置已排队，将在当前实时数据和告警读取完成后处理。");return;}
                try{ApplyQueuedPartitionSettings();}catch(Exception e){Notice("分期设置未保存："+e.Message);}
            };
            storagePage.SelectedSourceChanged+=delegate{partitionSettings.RefreshSummary();};
            TableLayoutPanel recordLayout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,BackColor=Color.FromArgb(243,247,251),Padding=new Padding(4)};
            recordLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));recordLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));recordLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            recordLayout.Controls.Add(partitionSettings,0,0);recordLayout.Controls.Add(storagePage,0,1);page.Controls.Add(recordLayout);
        }
        void ApplyQueuedPartitionSettings()
        {
            if(pollCycleBusy||requestedPartitionDays==0)return;
            partitionManagers[requestedPartitionSource].Configure(requestedPartitionDays,requestedPartitionImmediate,DateTime.UtcNow);
            requestedPartitionDays=0;
            if(partitionSettings!=null)partitionSettings.RefreshSummary();
            Notice(requestedPartitionImmediate?"分期设置已保存；下一轮采集写入新分期，历史数据保留。":"分期设置已保存；当前分期结束后生效，历史数据保留。");
        }
        void PrepareRoundPartition(string source,DateTime utc,long round)
        {
            PartitionDescriptor descriptor=partitionManagers[source].Describe(utc);
            roundPartitions[round]=descriptor;roundDatabase[round]=descriptor.DatabasePath;
            if(roundPartitions.Count>128)
            {
                long oldest=Int64.MaxValue;foreach(long key in roundPartitions.Keys)if(key<oldest)oldest=key;
                roundPartitions.Remove(oldest);roundDatabase.Remove(oldest);
            }
            if(partitionSettings!=null)partitionSettings.RefreshSummary();
        }
    }
}
