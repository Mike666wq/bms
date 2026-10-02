using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace BmsSerialDemo
{
    // Immutable, detached copy of one 42/44 observation. Arrays are copied at both boundaries.
    public sealed class RealtimeSnapshot
    {
        int[] cells, temperatures;
        byte[] rawFrame, payload, alarmPayload;
        public string Source { get; private set; }
        public DateTime ReceivedUtc { get; private set; }
        public long AcquisitionRound { get; private set; }
        public int? PeriodSeconds { get; private set; }
        public byte Address { get; private set; }
        public int Pack { get; private set; }
        public int Soc { get; private set; }
        public int Soh { get; private set; }
        public int Cycles { get; private set; }
        public int BalanceLow { get; private set; }
        public int BalanceHigh { get; private set; }
        public int Humidity { get; private set; }
        public double Voltage { get; private set; }
        public double Current { get; private set; }
        public double RemainingAh { get; private set; }
        public double TotalAh { get; private set; }
        public IList<int> Cells { get { return new ReadOnlyCollection<int>((int[])cells.Clone()); } }
        public IList<int> Temperatures { get { return new ReadOnlyCollection<int>((int[])temperatures.Clone()); } }
        public byte[] RawFrame { get { return (byte[])rawFrame.Clone(); } }
        public byte[] Payload { get { return (byte[])payload.Clone(); } }
        public byte[] AlarmPayload { get { return (byte[])alarmPayload.Clone(); } }
        RealtimeSnapshot() { }
        public DateTime? AlarmObservedUtc { get; private set; }
        public long? AlarmAcquisitionRound { get; private set; }
        public int? AlarmPack { get; private set; }
        internal AlarmSnapshot AlarmObservation { get; private set; }
        public static RealtimeSnapshot Capture(Frame frame, PackData parsed, string source, DateTime receivedUtc, byte[] raw44 = null, long acquisitionRound = 0, int? periodSeconds = null, AlarmSnapshot observedAlarm = null)
        {
            if (frame == null || parsed == null) throw new ArgumentNullException();
            if (source != "simulation" && source != "serial") throw new ArgumentException("source 必须为 simulation 或 serial");
            return new RealtimeSnapshot { Source = source, ReceivedUtc = receivedUtc.ToUniversalTime(), AcquisitionRound=acquisitionRound, PeriodSeconds=periodSeconds, Address = frame.Address, Pack = parsed.Pack,
                Soc = parsed.Soc, Soh = parsed.Soh, Cycles = parsed.Cycles, BalanceLow = parsed.BalanceLow, BalanceHigh = parsed.BalanceHigh, Humidity = parsed.Humidity,
                Voltage = parsed.Voltage, Current = parsed.Current, RemainingAh = parsed.RemainingAh, TotalAh = parsed.TotalAh,
                cells = parsed.Cells == null ? new int[0] : (int[])parsed.Cells.Clone(), temperatures = parsed.Temperatures == null ? new int[0] : (int[])parsed.Temperatures.Clone(),
                rawFrame = frame.Raw == null ? new byte[0] : (byte[])frame.Raw.Clone(), payload = frame.Info == null ? new byte[0] : (byte[])frame.Info.Clone(), alarmPayload = observedAlarm!=null?observedAlarm.Payload:(raw44 == null ? new byte[0] : (byte[])raw44.Clone()), AlarmObservedUtc=observedAlarm==null?(DateTime?)null:observedAlarm.ReceivedUtc, AlarmAcquisitionRound=observedAlarm==null?(long?)null:observedAlarm.AcquisitionRound,AlarmPack=observedAlarm==null?(int?)null:observedAlarm.Pack,AlarmObservation=observedAlarm };
        }
        internal int[] CellArray { get { return (int[])cells.Clone(); } }
        internal int[] TemperatureArray { get { return (int[])temperatures.Clone(); } }
        public PackData ToPackData() { return new PackData { Pack=Pack,Soc=Soc,Soh=Soh,Cycles=Cycles,BalanceLow=BalanceLow,BalanceHigh=BalanceHigh,Humidity=Humidity,Voltage=Voltage,Current=Current,RemainingAh=RemainingAh,TotalAh=TotalAh,Cells=CellArray,Temperatures=TemperatureArray }; }
    }
    public sealed class AlarmSnapshot
    {
        byte[] rawFrame, payload;
        public string Source { get; private set; }
        public DateTime ReceivedUtc { get; private set; }
        public long AcquisitionRound { get; private set; }
        public int? PeriodSeconds { get; private set; }
        public byte Address { get; private set; }
        public int Pack { get; private set; }
        public string DecodedText { get; private set; }
        public byte[] RawFrame { get { return (byte[])rawFrame.Clone(); } }
        public byte[] Payload { get { return (byte[])payload.Clone(); } }
        AlarmSnapshot() { }
        public static AlarmSnapshot Capture(Frame frame, string source, int pack, DateTime receivedUtc, string decodedText, long acquisitionRound=0, int? periodSeconds=null)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            if (source != "simulation" && source != "serial") throw new ArgumentException("source 必须为 simulation 或 serial");
            return new AlarmSnapshot { Source = source, ReceivedUtc = receivedUtc.ToUniversalTime(), AcquisitionRound=acquisitionRound, PeriodSeconds=periodSeconds, Address = frame.Address, Pack = pack, DecodedText = decodedText ?? "",
                rawFrame = frame.Raw == null ? new byte[0] : (byte[])frame.Raw.Clone(), payload = frame.Info == null ? new byte[0] : (byte[])frame.Info.Clone() };
        }
    }
    // Future publishers must enqueue into their own bounded background queue; Publish must never perform network I/O on the UI thread.
    public interface IRealtimePublisher { void Publish(RealtimeSnapshot snapshot); }
    public sealed class NullRealtimePublisher : IRealtimePublisher { public void Publish(RealtimeSnapshot snapshot) { } }
    [System.Runtime.Serialization.DataContract]
    public sealed class CloudAlarmEnvelope
    {
        [System.Runtime.Serialization.DataMember(Name="observedUtc")] public string ObservedUtc{get;private set;}
        [System.Runtime.Serialization.DataMember(Name="acquisitionRound")] public long AcquisitionRound{get;private set;}
        [System.Runtime.Serialization.DataMember(Name="pack")] public int Pack{get;private set;}
        [System.Runtime.Serialization.DataMember(Name="payloadHex")] public string PayloadHex{get;private set;}
        internal static CloudAlarmEnvelope From(AlarmSnapshot alarm){return new CloudAlarmEnvelope{ObservedUtc=alarm.ReceivedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture),AcquisitionRound=alarm.AcquisitionRound,Pack=alarm.Pack,PayloadHex=Protocol.Hex(alarm.Payload)};}
    }
    [System.Runtime.Serialization.DataContract]
    public sealed class CloudSnapshotEnvelope
    {
        [System.Runtime.Serialization.DataMember(Name="schemaVersion")]
        public int SchemaVersion { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="deviceId")]
        public string DeviceId { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="connectionSessionId")]
        public string ConnectionSessionId { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="sequence")]
        public long Sequence { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="acquisitionRound")]
        public long AcquisitionRound { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="periodSeconds",EmitDefaultValue=false)]
        public int? PeriodSeconds { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="alarmObservationAvailable")]
        public bool AlarmObservationAvailable { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="alarmObservation")]
        public CloudAlarmEnvelope AlarmObservation { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="capturedUtc")]
        public string CapturedUtc { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="source")]
        public string Source { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="address")]
        public byte Address { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="pack")]
        public int Pack { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="voltageCentivolts")]
        public int VoltageCentivolts { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="currentCentiamps")]
        public int CurrentCentiamps { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="socPercent")]
        public int SocPercent { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="sohPercent")]
        public int SohPercent { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="remainingCentiAh")]
        public int RemainingCentiAh { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="totalCentiAh")]
        public int TotalCentiAh { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="cycles")]
        public int Cycles { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="humidityPercent")]
        public int HumidityPercent { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="cellsMillivolts")]
        public IList<int> CellsMillivolts { get; private set; }
        [System.Runtime.Serialization.DataMember(Name="temperaturesCelsius")]
        public IList<int> TemperaturesCelsius { get; private set; }
        public static CloudSnapshotEnvelope From(RealtimeSnapshot s,string deviceId,string sessionId,long sequence,AlarmSnapshot alarm=null)
        {
            if(s==null)throw new ArgumentNullException("s");
            if(alarm==null)alarm=s.AlarmObservation;
            return new CloudSnapshotEnvelope { SchemaVersion=1,DeviceId=deviceId,ConnectionSessionId=sessionId,Sequence=sequence,AcquisitionRound=s.AcquisitionRound,PeriodSeconds=s.PeriodSeconds,CapturedUtc=s.ReceivedUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'",CultureInfo.InvariantCulture),Source=s.Source,Address=s.Address,Pack=s.Pack,
                VoltageCentivolts=(int)Math.Round(s.Voltage*100),CurrentCentiamps=(int)Math.Round(s.Current*100),SocPercent=s.Soc,SohPercent=s.Soh,
                RemainingCentiAh=(int)Math.Round(s.RemainingAh*100),TotalCentiAh=(int)Math.Round(s.TotalAh*100),Cycles=s.Cycles,HumidityPercent=s.Humidity,
                CellsMillivolts=s.Cells,TemperaturesCelsius=s.Temperatures,AlarmObservationAvailable=alarm!=null,AlarmObservation=alarm==null?null:CloudAlarmEnvelope.From(alarm) };
        }
    }
    // Explicit placeholder only: no HTTP client, endpoint, token, or background sender is included.
    public sealed class CloudRealtimePublisherTemplate : IRealtimePublisher
    {
        public void Publish(RealtimeSnapshot snapshot) { throw new NotSupportedException("GitHub publisher is a contract template only; network publication is disabled."); }
    }
}
