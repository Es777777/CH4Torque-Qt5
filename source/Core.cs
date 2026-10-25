using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace CH4Torque
{
    [DataContract]
    public sealed class Settings
    {
        [DataMember] public string Port = "";
        [DataMember] public int Baud = 115200;
        [DataMember] public string Parity = "None";
        [DataMember] public int StopBits = 1;
        [DataMember] public int Station = 1;
        [DataMember] public string Address = "";
        [DataMember] public string DataType = "";
        [DataMember] public string ByteOrder = "";
        [DataMember] public string Gain = "";
        [DataMember] public string Offset = "0";
        [DataMember] public string Unit = "N·m";
        [DataMember] public int Interval = 100;
        [DataMember] public int Timeout = 500;
        [DataMember] public bool RawMode = true;
        public int RegisterCount { get { return DataType == "Int16" || DataType == "UInt16" ? 1 : 2; } }

        public static int ParseAddress(string value)
        {
            value = (value ?? "").Trim();
            int n;
            bool ok = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.TryParse(value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)
                : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
            if (!ok || n < 0 || n > 65535) throw new ArgumentException("请填写 CH4 的协议地址（0~65535，或 0x 开头的十六进制）。\n请按厂家协议将 4xxxx 编号转换为报文中的地址。");
            return n;
        }

        public static double Number(string value, string label)
        {
            double n;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out n) || double.IsInfinity(n) || double.IsNaN(n))
                throw new ArgumentException(label + "必须是有限数值，小数点请使用英文句点。");
            return n;
        }

        public void Validate(bool requirePort)
        {
            if (requirePort && string.IsNullOrWhiteSpace(Port)) throw new ArgumentException("请选择 USB-RS485 转换器对应的 COM 串口。");
            if (Baud < 300 || Baud > 2000000) throw new ArgumentException("波特率超出支持范围。");
            if (Parity != "None" && Parity != "Even" && Parity != "Odd") throw new ArgumentException("校验位无效。");
            if (StopBits != 1 && StopBits != 2) throw new ArgumentException("停止位无效。");
            if (Station < 1 || Station > 247) throw new ArgumentException("本软件支持 Modbus 标准单播站号 1~247。");
            if (DataType != "Int16" && DataType != "UInt16" && DataType != "Int32" && DataType != "UInt32" && DataType != "Float32")
                throw new ArgumentException("请选择厂家协议规定的 CH4 数据类型。");
            if (RegisterCount == 1 ? ByteOrder != "AB" && ByteOrder != "BA" : ByteOrder != "ABCD" && ByteOrder != "BADC" && ByteOrder != "CDAB" && ByteOrder != "DCBA")
                throw new ArgumentException("请选择与数据类型匹配的字节序：16位为 AB/BA，32位为 ABCD/BADC/CDAB/DCBA。");
            int address = ParseAddress(Address);
            if (address + RegisterCount > 65536) throw new ArgumentException("寄存器范围超界。");
            if (Number(Gain, "换算系数") == 0) throw new ArgumentException("换算系数不能为 0。若设备直接返回 N·m，请填写 1。");
            Number(Offset, "换算偏置");
            if (Unit != "N·m" && Unit != "N·mm" && Unit != "kgf·cm" && Unit != "原始单位") throw new ArgumentException("单位无效。");
            if (Interval < 20 || Interval > 10000 || Timeout < 50 || Timeout > 10000) throw new ArgumentException("采集周期或超时时间超界。");
        }

        public static Settings Load(string path)
        {
            if (!File.Exists(path)) return new Settings();
            using (var f = File.OpenRead(path)) return (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(f);
        }
        public void Save(string path)
        {
            string temp = path + ".tmp";
            using (var f = File.Create(temp)) new DataContractJsonSerializer(typeof(Settings)).WriteObject(f, this);
            File.Copy(temp, path, true);
            File.Delete(temp);
        }
    }

    public interface IByteTransport : IDisposable
    {
        void DiscardInput();
        void Write(byte[] bytes);
        int ReadByte();
    }

    public sealed class SerialTransport : IByteTransport
    {
        private readonly SerialPort port;
        public SerialTransport(Settings s)
        {
            port = new SerialPort(s.Port, s.Baud, (Parity)Enum.Parse(typeof(Parity), s.Parity), 8, s.StopBits == 2 ? StopBits.Two : StopBits.One);
            port.ReadTimeout = 30;
            port.WriteTimeout = s.Timeout;
            port.DtrEnable = false;
            port.RtsEnable = false;
            try { port.Open(); } catch { port.Dispose(); throw; }
        }
        public void DiscardInput() { port.DiscardInBuffer(); }
        public void Write(byte[] bytes) { port.Write(bytes, 0, bytes.Length); }
        public int ReadByte() { try { return port.ReadByte(); } catch (TimeoutException) { return -1; } }
        public void Dispose() { port.Dispose(); }
    }

    public static class Modbus
    {
        public static ushort Crc(byte[] data, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < count; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++) crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }
            return crc;
        }
        public static byte[] WithCrc(byte[] body)
        {
            var result = new byte[body.Length + 2];
            Array.Copy(body, result, body.Length);
            ushort crc = Crc(body, body.Length);
            result[body.Length] = (byte)(crc & 255);
            result[body.Length + 1] = (byte)(crc >> 8);
            return result;
        }
        public static byte[] Request(Settings s)
        {
            s.Validate(false);
            int address = Settings.ParseAddress(s.Address);
            // CH4 only. The function is deliberately fixed to 03 (read holding registers).
            return WithCrc(new byte[] { (byte)s.Station, 3, (byte)(address >> 8), (byte)(address & 255), 0, (byte)s.RegisterCount });
        }
        public static byte[] ParseResponse(byte[] frame, Settings s)
        {
            if (frame.Length < 5) throw new IOException("应答长度不足。");
            ushort crc = Crc(frame, frame.Length - 2);
            if (frame[frame.Length - 2] != (byte)(crc & 255) || frame[frame.Length - 1] != (byte)(crc >> 8)) throw new IOException("CRC 校验失败，数据已丢弃。");
            if (frame[0] != s.Station) throw new IOException("应答站号不匹配。");
            if (frame[1] == 0x83)
            {
                string detail = frame[2] == 1 ? "不支持功能码" : frame[2] == 2 ? "寄存器地址无效" : frame[2] == 3 ? "数据长度无效" : "设备异常";
                throw new IOException("Modbus 异常 " + frame[2].ToString("X2") + "：" + detail);
            }
            if (frame[1] != 3 || frame[2] != s.RegisterCount * 2 || frame.Length != 5 + s.RegisterCount * 2)
                throw new IOException("应答功能码或数据长度不匹配。");
            var payload = new byte[s.RegisterCount * 2];
            Array.Copy(frame, 3, payload, 0, payload.Length);
            return payload;
        }

        public static double Decode(byte[] payload, string type, string order)
        {
            string canonical = payload.Length == 2 ? "AB" : "ABCD";
            if ((payload.Length != 2 && payload.Length != 4) || order.Length != payload.Length) throw new IOException("数据长度或字节序无效。");
            var big = new byte[payload.Length];
            for (int i = 0; i < big.Length; i++)
            {
                int at = order.IndexOf(canonical[i]);
                if (at < 0) throw new IOException("字节序无效。");
                big[i] = payload[at];
            }
            uint n = 0;
            foreach (byte b in big) n = (n << 8) | b;
            double value;
            if (type == "Int16" && big.Length == 2) value = unchecked((short)n);
            else if (type == "UInt16" && big.Length == 2) value = n;
            else if (type == "Int32" && big.Length == 4) value = unchecked((int)n);
            else if (type == "UInt32" && big.Length == 4) value = n;
            else if (type == "Float32" && big.Length == 4)
            {
                if (BitConverter.IsLittleEndian) Array.Reverse(big);
                value = BitConverter.ToSingle(big, 0);
            }
            else throw new IOException("数据类型与寄存器数量不匹配。");
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new IOException("设备返回非有限数值，已丢弃。");
            return value;
        }

        public static double Read(IByteTransport port, Settings s, CancellationToken token, Action<string> trace)
        {
            byte[] request = Request(s);
            port.DiscardInput();
            // RTU silent interval, including low baud rates and parity/stop-bit overhead.
            int gap = Math.Max(2, (int)Math.Ceiling(3.5 * 12 * 1000 / s.Baud));
            if (token.WaitHandle.WaitOne(gap)) token.ThrowIfCancellationRequested();
            trace("TX  " + Hex(request));
            port.Write(request);
            var frame = new List<byte>();
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < s.Timeout)
            {
                token.ThrowIfCancellationRequested();
                int b = port.ReadByte();
                if (b < 0) continue;
                frame.Add((byte)b);
                while (frame.Count > 0 && frame[0] != s.Station) frame.RemoveAt(0);
                if (frame.Count < 2) continue;
                if (frame[1] != 3 && frame[1] != 0x83) { frame.RemoveAt(0); continue; }
                if (frame.Count < 3) continue;
                int length = frame[1] == 0x83 ? 5 : frame[2] + 5;
                if (length > 255) throw new IOException("设备应答长度异常。");
                if (frame.Count == length)
                {
                    byte[] received = frame.ToArray();
                    trace("RX  " + Hex(received));
                    return Decode(ParseResponse(received, s), s.DataType, s.ByteOrder);
                }
            }
            if (frame.Count > 0) trace("RX（不完整）  " + Hex(frame.ToArray()));
            throw new TimeoutException("读取超时：检查 COM、波特率、站号、A/B 接线及 CH4 地址。请关闭占用串口的其他上位机。");
        }
        public static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace('-', ' '); }
        public static double ConvertValue(double raw, double gain, double offset, double tare)
        {
            double value = raw * gain + offset - tare;
            if (double.IsInfinity(value) || double.IsNaN(value)) throw new IOException("换算结果溢出，请检查系数。");
            return value;
        }
    }

    public sealed class CsvRecorder : IDisposable
    {
        private readonly StreamWriter writer;
        public static string Escape(string s) { return "\"" + (s ?? "").Replace("\"", "\"\"") + "\""; }
        public CsvRecorder(string path)
        {
            writer = new StreamWriter(path, false, new UTF8Encoding(true));
            try
            {
                writer.AutoFlush = true;
            writer.WriteLine("电脑接收时间_ISO8601,会话秒,通道_待实测核对,模式,原始值,显示值,单位,换算系数,换算偏置,软件去皮量,COM,波特率,站号,CH4协议地址,数据类型,字节序");
            }
            catch { writer.Dispose(); throw; }
        }
        public void Write(DateTimeOffset time, double elapsed, double raw, double value, double gain, double offset, double tare, Settings s, bool demo)
        {
            Func<double, string> num = n => n.ToString("R", CultureInfo.InvariantCulture);
            writer.WriteLine(string.Join(",", new string[] { Escape(time.ToString("o")), num(elapsed), "CH4", demo ? "DEMO_SIMULATED" : s.RawMode ? "DEVICE_RAW_UNCALIBRATED" : "DEVICE_USER_SCALED", num(raw), num(value), Escape(s.Unit), num(gain), num(offset), num(tare), Escape(demo ? "" : s.Port), demo ? "" : s.Baud.ToString(CultureInfo.InvariantCulture), demo ? "" : s.Station.ToString(CultureInfo.InvariantCulture), Escape(demo ? "" : s.Address), Escape(demo ? "" : s.DataType), Escape(demo ? "" : s.ByteOrder) }));
        }
        public void Dispose() { writer.Dispose(); }
    }
}
