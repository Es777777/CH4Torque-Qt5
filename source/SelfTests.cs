using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CH4Torque
{
    internal sealed class FakeTransport : IByteTransport
    {
        public byte[] Written;
        private readonly Queue<int> bytes;
        public FakeTransport(IEnumerable<int> values) { bytes = new Queue<int>(values); }
        public void DiscardInput() { }
        public void Write(byte[] b) { Written = b; }
        public int ReadByte() { if (bytes.Count > 0) return bytes.Dequeue(); Thread.Sleep(1); return -1; }
        public void Dispose() { }
    }
    internal static class SelfTests
    {
        private static int passed;
        private static readonly StringBuilder report = new StringBuilder();
        private static void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: " + name);
            passed++; report.AppendLine("PASS: " + name);
        }
        private static void Throws(Action a, string name)
        {
            bool threw = false; try { a(); } catch { threw = true; } Check(threw, name);
        }
        private static Settings Valid()
        {
            // Test fixture only. This address is not a device register map and is never installed as a default.
            return new Settings { Address = "0", DataType = "Int32", ByteOrder = "ABCD", Gain = "1", Timeout = 60 };
        }
        public static string Run()
        {
            passed = 0; report.Clear();
            var s = Valid();
            Check(Modbus.Hex(Modbus.Request(s)) == "01 03 00 00 00 02 C4 0B", "known Modbus 03 request and CRC vector");
            var candidate = Valid(); candidate.Address = "6"; candidate.ByteOrder = "CDAB"; candidate.RawMode = true; candidate.Unit = "原始单位";
            Check(Modbus.Request(candidate).Take(6).SequenceEqual(new byte[] { 1, 3, 0, 6, 0, 2 }), "candidate CH4 reads only protocol addresses 6 and 7");
            using (var fake = new FakeTransport(Modbus.WithCrc(new byte[] { 1, 3, 4, 0xFF, 0x9C, 0xFF, 0xFF }).Select(b => (int)b)))
                Check(Modbus.Read(fake, candidate, CancellationToken.None, t => { }) == -100, "reference-compatible low-word-first signed negative sample");
            using (var fake = new FakeTransport(Modbus.WithCrc(new byte[] { 1, 3, 4, 0x86, 0xA0, 0, 1 }).Select(b => (int)b)))
                Check(Modbus.Read(fake, candidate, CancellationToken.None, t => { }) == 100000, "reference-compatible low-word-first positive sample");
            Throws(() => new Settings().Validate(false), "blank factory-specific parameters block device reading");
            Throws(() => { var x = Valid(); x.Station = 0; x.Validate(false); }, "broadcast address blocked");
            Throws(() => { var x = Valid(); x.Address = "65535"; x.Validate(false); }, "two-register overflow blocked");
            Throws(() => { var x = Valid(); x.Gain = "0"; x.Validate(false); }, "zero gain blocked");
            Throws(() => Settings.Number("NaN", "gain"), "NaN coefficient blocked");
            Check(Settings.ParseAddress("0x1234") == 0x1234, "hex protocol addresses");
            var valid = Modbus.WithCrc(new byte[] { 1, 3, 4, 0xFF, 0xFF, 0xFF, 0x9C });
            Check(Modbus.Decode(Modbus.ParseResponse(valid, s), "Int32", "ABCD") == -100, "negative torque decoding");
            var damaged = (byte[])valid.Clone(); damaged[4] ^= 1;
            Throws(() => Modbus.ParseResponse(damaged, s), "bad CRC rejected");
            Throws(() => Modbus.ParseResponse(Modbus.WithCrc(new byte[] { 2, 3, 4, 0, 0, 0, 1 }), s), "wrong station rejected");
            Throws(() => Modbus.ParseResponse(Modbus.WithCrc(new byte[] { 1, 4, 4, 0, 0, 0, 1 }), s), "wrong function rejected");
            Throws(() => Modbus.ParseResponse(Modbus.WithCrc(new byte[] { 1, 3, 2, 0, 1 }), s), "wrong byte count rejected");
            Throws(() => Modbus.ParseResponse(Modbus.WithCrc(new byte[] { 1, 0x83, 2 }), s), "Modbus exception response rejected");
            foreach (string order in new string[] { "ABCD", "BADC", "CDAB", "DCBA" })
            {
                byte[] canonical = new byte[] { 0x41, 0x48, 0, 0 };
                byte[] wire = order.Select(c => canonical["ABCD".IndexOf(c)]).ToArray();
                Check(Modbus.Decode(wire, "Float32", order) == 12.5, "Float32 " + order);
                byte[] integer = new byte[] { 0xFF, 0xFF, 0xFF, 0x9C };
                Check(Modbus.Decode(order.Select(c => integer["ABCD".IndexOf(c)]).ToArray(), "Int32", order) == -100, "Int32 " + order);
            }
            Check(Modbus.Decode(new byte[] { 255, 255 }, "Int16", "AB") == -1, "signed 16-bit");
            Check(Modbus.Decode(new byte[] { 255, 255 }, "UInt16", "AB") == 65535, "unsigned 16-bit");
            Check(Modbus.Decode(new byte[] { 0x34, 0x12 }, "UInt16", "BA") == 0x1234, "16-bit byte swap");
            Check(Modbus.Decode(new byte[] { 255, 255, 255, 255 }, "UInt32", "ABCD") == 4294967295.0, "unsigned 32-bit");
            Throws(() => Modbus.Decode(new byte[] { 0x7F, 0xC0, 0, 0 }, "Float32", "ABCD"), "NaN device value rejected");
            Throws(() => Modbus.Decode(new byte[] { 0x7F, 0x80, 0, 0 }, "Float32", "ABCD"), "infinite device value rejected");
            Check(Math.Abs(Modbus.ConvertValue(-1000, .01, 2, 3) + 11) < 1e-12, "scale, offset, local tare");
            Throws(() => Modbus.ConvertValue(double.MaxValue, 2, 0, 0), "conversion overflow rejected");
            using (var fake = new FakeTransport(new int[] { 99, -1, 98 }.Concat(valid.SelectMany(b => new int[] { -1, b }))))
            {
                Check(Modbus.Read(fake, s, CancellationToken.None, t => { }) == -100, "fragmented serial response and prefix noise");
                Check(fake.Written[1] == 3 && fake.Written[5] == 2, "only 03 and selected register span sent");
            }
            using (var fake = new FakeTransport(valid.Take(4).Select(b => (int)b)))
                Throws(() => Modbus.Read(fake, s, CancellationToken.None, t => { }), "truncated response times out");
            using (var fake = new FakeTransport(valid.Select(b => (int)b)))
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel(); Throws(() => Modbus.Read(fake, s, cts.Token, t => { }), "cancellation handled");
            }
            string dir = Path.Combine(Path.GetTempPath(), "CH4Torque-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string config = Path.Combine(dir, "settings.json"); s.Save(config); s.Gain = ".01"; s.Save(config);
                Check(Settings.Load(config).Gain == ".01", "portable settings save and replace round trip");
                string csv = Path.Combine(dir, "record.csv");
                using (var recorder = new CsvRecorder(csv))
                {
                    recorder.Write(DateTimeOffset.Now, .1, -100, -1, .01, 0, 0, s, true);
                    recorder.Write(DateTimeOffset.Now, .2, -200, -2, .01, 0, 1, s, false);
                    candidate.RawMode = false;
                    recorder.Write(DateTimeOffset.Now, .3, 100, 1, .01, 0, 0, candidate, false);
                    using (var shared = new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(shared, Encoding.UTF8))
                        Check(reader.ReadToEnd().Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Length == 4, "CSV flushed while recording");
                }
                byte[] csvBytes = File.ReadAllBytes(csv); string csvText = File.ReadAllText(csv, Encoding.UTF8);
                Check(csvBytes[0] == 0xEF && csvBytes[1] == 0xBB && csvBytes[2] == 0xBF, "Excel-compatible UTF-8 BOM");
                Check(csvText.Contains("DEMO_SIMULATED") && csvText.Contains("DEVICE"), "simulation and device rows explicitly distinguished");
                Check(csvText.Contains("DEVICE_RAW_UNCALIBRATED") && csvText.Contains("DEVICE_USER_SCALED"), "uncalibrated raw samples and user-scaled samples distinguishable in CSV");
                Check(csvText.Contains("-100,-1") && csvText.Contains("-200,-2"), "raw and converted values preserved");
            }
            finally { Directory.Delete(dir, true); }
            report.AppendLine("TOTAL: " + passed + " passed. No physical device was accessed.");
            return report.ToString();
        }
    }
}
