using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BmsSerialDemo
{
    public sealed class Frame
    {
        public byte Version, Address, ReturnCode;
        public byte[] Info, Raw;
    }

    public static class Protocol
    {
        public static ushort LengthWord(int length)
        {
            if (length < 0 || length > 4094 || length % 2 != 0) throw new ArgumentException("INFO ASCII 长度非法");
            int sum = (length & 15) + ((length >> 4) & 15) + ((length >> 8) & 15);
            return (ushort)(length | (((-sum) & 15) << 12));
        }
        public static byte[] Encode(byte version, byte address, byte command, byte[] info)
        {
            string body = version.ToString("X2") + address.ToString("X2") + "46" + command.ToString("X2") + LengthWord(info.Length * 2).ToString("X4") + Hex(info);
            int sum = 0;
            foreach (byte b in Encoding.ASCII.GetBytes(body)) sum += b;
            return Encoding.ASCII.GetBytes("~" + body + ((-sum) & 65535).ToString("X4") + "\r");
        }
        public static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", ""); }
        public static byte[] Unhex(string text)
        {
            if (text.Length % 2 != 0) throw new FormatException("HEX 长度必须为偶数");
            byte[] bytes = new byte[text.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = byte.Parse(text.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            return bytes;
        }
        public static Frame Decode(byte[] raw)
        {
            if (raw.Length < 18 || raw[0] != 0x7e || raw[raw.Length - 1] != 13) throw new FormatException("帧边界错误");
            for (int i = 1; i < raw.Length - 1; i++)
                if (!((raw[i] >= '0' && raw[i] <= '9') || (raw[i] >= 'A' && raw[i] <= 'F') || (raw[i] >= 'a' && raw[i] <= 'f'))) throw new FormatException("非 HEX ASCII 字符");
            string s = Encoding.ASCII.GetString(raw);
            int lw = int.Parse(s.Substring(9, 4), NumberStyles.HexNumber);
            int len = lw & 4095;
            if (len % 2 != 0) throw new FormatException("LENID 为奇数，INFO 字节长度非法");
            if (LengthWord(len) != lw) throw new FormatException("LCHKSUM 错误");
            if (raw.Length != 18 + len) throw new FormatException("帧长度错误");
            int sum = 0;
            for (int i = 1; i < raw.Length - 5; i++) sum += raw[i];
            if (((-sum) & 65535) != int.Parse(s.Substring(raw.Length - 5, 4), NumberStyles.HexNumber)) throw new FormatException("CHKSUM 错误");
            byte[] header = Unhex(s.Substring(1, 8));
            if (header[2] != 0x46) throw new FormatException("CID1 不为 46");
            return new Frame { Version = header[0], Address = header[1], ReturnCode = header[3], Info = Unhex(s.Substring(13, len)), Raw = raw };
        }
    }

    public sealed class Framer
    {
        readonly List<byte> buffer = new List<byte>();
        public Action<byte[]> Complete;
        public Action<string, byte[]> Invalid;
        public void Reset() { buffer.Clear(); }
        public void Feed(byte[] bytes)
        {
            foreach (byte b in bytes)
            {
                if (b == 0x7e)
                {
                    if (buffer.Count > 0 && Invalid != null) Invalid("遇到新帧头，丢弃不完整帧", buffer.ToArray());
                    buffer.Clear(); buffer.Add(b); continue;
                }
                if (buffer.Count == 0) continue;
                buffer.Add(b);
                if (b == 13)
                {
                    byte[] raw = buffer.ToArray(); buffer.Clear();
                    if (Complete != null) Complete(raw);
                }
                else if (buffer.Count > 4112)
                {
                    if (Invalid != null) Invalid("帧超过最大长度", buffer.ToArray());
                    buffer.Clear();
                }
            }
        }
    }

    public sealed class Cursor
    {
        readonly byte[] bytes;
        public int Position;
        public int Remaining { get { return bytes.Length - Position; } }
        public Cursor(byte[] bytes, int start) { this.bytes = bytes; Position = start; }
        public byte U8() { if (Remaining < 1) throw new FormatException("字段数据不足"); return bytes[Position++]; }
        public int U16() { return U8() * 256 + U8(); }
        public int I16() { return (short)U16(); }
    }

    public sealed class PackData
    {
        public int Pack, Soc, Soh, Cycles, BalanceLow, BalanceHigh, Humidity;
        public double Voltage, Current, RemainingAh, TotalAh;
        public int[] Cells, Temperatures;
    }
    public static class DataParser
    {
        // 文档文字描述带 INFOFLAG，文档示例却不带；只接受完整消费载荷的结构。
        public static List<PackData> Realtime(byte[] info, byte requested, out string layout)
        {
            List<PackData> result;
            bool plain = TryRealtime(info, requested, 0, out result);
            List<PackData> flagged;
            bool flag = TryRealtime(info, requested, 1, out flagged);
            if (plain && flag) throw new FormatException("INFOFLAG 布局有歧义，请核对原始帧");
            if (!plain && !flag) throw new FormatException("实时载荷不符合已知布局，已保留原始数据");
            layout = flag ? "含 INFOFLAG" : "不含 INFOFLAG（文档示例布局）";
            return flag ? flagged : result;
        }
        static bool TryRealtime(byte[] info, byte requested, int offset, out List<PackData> result)
        {
            result = new List<PackData>();
            try
            {
                Cursor c = new Cursor(info, offset);
                int marker = c.U8();
                int count = requested == 255 ? marker : 1;
                if (count < 1 || count > 16 || (requested != 255 && marker != requested)) return false;
                for (int i = 0; i < count; i++)
                {
                    PackData p = new PackData(); p.Pack = requested == 255 ? i + 1 : requested;
                    int cells = c.U8(); if (cells < 1 || cells > 48) return false;
                    p.Cells = new int[cells]; for (int j = 0; j < cells; j++) p.Cells[j] = c.U16();
                    int temps = c.U8(); if (temps > 32) return false;
                    p.Temperatures = new int[temps]; for (int j = 0; j < temps; j++) p.Temperatures[j] = c.U16() - 40;
                    p.Current = c.I16() / 100.0; p.Voltage = c.U16() / 100.0; p.RemainingAh = c.U16() / 100.0;
                    if (c.U8() != 6) return false;
                    p.TotalAh = c.U16() / 100.0; p.Cycles = c.U16(); p.Soc = c.U8(); p.Soh = c.U8();
                    p.BalanceLow = c.U16(); p.Humidity = c.U16(); p.BalanceHigh = c.U16(); result.Add(p);
                }
                return c.Remaining == 0;
            }
            catch (FormatException) { return false; }
        }
        public static string Alarm(byte[] info, byte requested)
        {
            string a = TryAlarm(info, requested, 0), b = TryAlarm(info, requested, 1);
            if (a != null && b != null) throw new FormatException("告警 INFOFLAG 布局有歧义");
            if (a == null && b == null) throw new FormatException("告警载荷布局未知，已保留原始数据");
            return (b != null ? "含 INFOFLAG\r\n" : "不含 INFOFLAG\r\n") + (b ?? a);
        }
        static string TryAlarm(byte[] bytes, byte requested, int offset)
        {
            try
            {
                Cursor c = new Cursor(bytes, offset); int marker = c.U8(), count = requested == 255 ? marker : 1;
                if (count < 1 || count > 16 || (requested != 255 && marker != requested)) return null;
                StringBuilder s = new StringBuilder();
                for (int i = 0; i < count; i++)
                {
                    s.AppendLine("Pack " + (requested == 255 ? i + 1 : requested));
                    int m = c.U8(); if (m < 1 || m > 48) return null;
                    for (int j = 0; j < m; j++) s.Append("电芯" + (j + 1) + "=" + State(c.U8()) + "  ");
                    s.AppendLine(); int n = c.U8(); if (n > 32) return null;
                    for (int j = 0; j < n; j++) s.Append("温度" + (j + 1) + "=" + State(c.U8()) + "  ");
                    s.AppendLine(); s.AppendLine("充电电流=" + State(c.U8()) + "，总压=" + State(c.U8()) + "，短路=0x" + c.U8().ToString("X2") + "，短路次数=" + c.U8());
                    if (c.U8() != 15) return null;
                    for (int j = 0; j < 15; j++)
                    {
                        byte v = c.U8(); s.AppendLine("状态" + (j + 1) + " = 0x" + v.ToString("X2") + " / " + Convert.ToString(v, 2).PadLeft(8, '0'));
                    }
                    s.AppendLine("保留 = 0x" + c.U8().ToString("X2"));
                }
                return c.Remaining == 0 ? s.ToString() : null;
            }
            catch (FormatException) { return null; }
        }
        static string State(byte v) { return v == 0 ? "正常" : v == 1 ? "低于下限" : v == 2 ? "高于上限" : v == 240 ? "其他错误" : "0x" + v.ToString("X2"); }
    }
}
