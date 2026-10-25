using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CH4Torque
{
    public sealed class SampleEvent
    {
        public string Kind, Text;
        public double Raw, Seconds;
        public DateTimeOffset Time;
    }

    public sealed class Trend : Control
    {
        private readonly List<PointF> points = new List<PointF>();
        public string Unit = "N·m";
        public bool Demo;
        public Trend() { DoubleBuffered = true; BackColor = Color.White; }
        public void ClearData() { points.Clear(); Invalidate(); }
        public void Add(double x, double y)
        {
            // Retain 60 seconds; discard display-only outliers outside the float range.
            points.Add(new PointF((float)x, Math.Abs(y) > 1e30 ? float.NaN : (float)y));
            points.RemoveAll(p => p.X < x - 60);
            if (points.Count > 6000) points.RemoveRange(0, points.Count - 6000);
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(82, 38, Math.Max(40, Width - 108), Math.Max(40, Height - 82));
            double end = points.Count == 0 ? 60 : Math.Max(60, points[points.Count - 1].X);
            double begin = end - 60;
            double low = double.PositiveInfinity, high = double.NegativeInfinity;
            foreach (PointF p in points) if (!float.IsNaN(p.Y)) { low = Math.Min(low, p.Y); high = Math.Max(high, p.Y); }
            if (double.IsInfinity(low)) { low = -1; high = 1; }
            double pad = Math.Max((high - low) * .12, Math.Max(Math.Abs(high) * .02, .001));
            low -= pad; high += pad;
            using (var brush = new SolidBrush(Color.FromArgb(95, 110, 130)))
            using (var grid = new Pen(Color.FromArgb(232, 237, 243)))
            using (var line = new Pen(Demo ? Color.FromArgb(205, 128, 18) : Color.FromArgb(0, 135, 127), 2))
            {
                g.DrawString("CH4  /  最近 60 秒 · " + Unit + (Demo ? "  [模拟]" : ""), Font, brush, 16, 12);
                for (int i = 0; i <= 4; i++)
                {
                    float y = r.Top + r.Height * i / 4;
                    g.DrawLine(grid, r.Left, y, r.Right, y);
                    string label = (high - (high - low) * i / 4).ToString("G5", CultureInfo.InvariantCulture);
                    g.DrawString(label, Font, brush, new RectangleF(0, y - 9, 76, 24), new StringFormat { Alignment = StringAlignment.Far });
                }
                for (int i = 0; i <= 6; i++)
                {
                    float x = r.Left + r.Width * i / 6;
                    g.DrawLine(grid, x, r.Top, x, r.Bottom);
                    g.DrawString((begin + i * 10).ToString("0", CultureInfo.InvariantCulture) + "s", Font, brush, x - 12, r.Bottom + 10);
                }
                PointF? previous = null;
                foreach (PointF p in points)
                {
                    if (float.IsNaN(p.Y)) { previous = null; continue; }
                    var next = new PointF(r.Left + (float)((p.X - begin) / 60) * r.Width, r.Bottom - (float)((p.Y - low) / (high - low)) * r.Height);
                    if (previous.HasValue) g.DrawLine(line, previous.Value, next);
                    previous = next;
                }
                if (points.Count == 0) g.DrawString("等待 CH4 数据", Font, brush, r.Left + 20, r.Top + r.Height / 2);
            }
        }
    }

    public sealed class MainForm : Form
    {
        private readonly Color navy = Color.FromArgb(22, 41, 61), teal = Color.FromArgb(0, 126, 119), muted = Color.FromArgb(91, 108, 125);
        private readonly ComboBox port = new ComboBox(), baud = new ComboBox(), parity = new ComboBox(), stopBits = new ComboBox();
        private readonly ComboBox dataType = new ComboBox(), byteOrder = new ComboBox(), unit = new ComboBox();
        private readonly TextBox address = new TextBox(), gain = new TextBox(), offset = new TextBox();
        private readonly NumericUpDown station = new NumericUpDown(), interval = new NumericUpDown(), timeout = new NumericUpDown();
        private readonly CheckBox confirmed = new CheckBox(), rawMode = new CheckBox();
        private readonly TabControl tabs = new TabControl();
        private readonly Label banner = new Label(), valueLabel = new Label(), unitLabel = new Label(), detail = new Label(), stats = new Label(), footer = new Label(), requestLabel = new Label(), valueTitle = new Label();
        private readonly TextBox log = new TextBox();
        private readonly Trend trend = new Trend();
        private readonly Button start = new Button(), demo = new Button(), stop = new Button(), record = new Button(), tareButton = new Button(), resetTare = new Button(), save = new Button();
        private readonly ConcurrentQueue<SampleEvent> events = new ConcurrentQueue<SampleEvent>();
        private readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
        private readonly string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CH4-settings.json");
        private CancellationTokenSource cancellation;
        private Task worker;
        private Settings active;
        private bool running, isDemo, good, closing;
        private double scale, bias, tare, lastRaw, lastValue, min, max, lastSeconds;
        private long count, errors, recordCount;
        private DateTimeOffset lastReceived;
        private CsvRecorder recorder;
        private string recordPath;

        public MainForm()
        {
            Text = "CH4 力矩采集 v1.1 | 中诺 ZNSV6-F4-485";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1220, 820);
            MinimumSize = new Size(1060, 760);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(241, 245, 249);
            BuildUi();
            Settings s;
            try { s = Settings.Load(settingsPath); } catch (Exception ex) { s = new Settings(); AppendLog("设置读取失败，已使用空白协议配置：" + ex.Message); }
            ApplySettings(s);
            RefreshPorts();
            if (port.Items.Contains(s.Port)) port.SelectedItem = s.Port;
            uiTimer.Interval = 50;
            uiTimer.Tick += Tick;
            uiTimer.Start();
            FormClosing += OnClosing;
            FormClosed += (o, e) => { uiTimer.Dispose(); if (cancellation != null) cancellation.Dispose(); };
            SetIdle();
            AppendLog("CH4 地址 6 / Int32 / CDAB 已完成本机加载、卸载响应检查；N·m 系数仍待已知力矩标定。未执行设备清零或 PWM 控制。");
        }

        private Label LabelOf(string text, float size, Color color)
        {
            return new Label { Text = text, AutoSize = false, Dock = DockStyle.Fill, ForeColor = color, Font = new Font(Font.FontFamily, size), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(4, 2, 4, 2) };
        }
        private void ButtonOf(Button b, string text, int width, bool primary, EventHandler click)
        {
            b.Text = text; b.Width = width; b.Height = 34; b.Margin = new Padding(4, 7, 4, 4);
            b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = primary ? teal : Color.FromArgb(205, 215, 225);
            b.BackColor = primary ? teal : Color.White; b.ForeColor = primary ? Color.White : navy;
            b.Click += click;
        }
        private void Choices(ComboBox box, params string[] choices)
        {
            box.DropDownStyle = ComboBoxStyle.DropDownList; box.Items.AddRange(choices); box.Dock = DockStyle.Fill;
        }
        private TableLayoutPanel Fields(TabPage page)
        {
            page.BackColor = Color.White; page.Padding = new Padding(10);
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 4, 0, 0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(t); return t;
        }
        private void Field(TableLayoutPanel t, string label, Control c)
        {
            int r = t.RowCount++; t.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            t.Controls.Add(LabelOf(label, 9, muted), 0, r);
            c.Dock = DockStyle.Fill; c.Margin = new Padding(3, 7, 3, 4); t.Controls.Add(c, 1, r);
        }
        private void Note(TableLayoutPanel t, string text, int height, Color color)
        {
            int r = t.RowCount++; t.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            var l = LabelOf(text, 9, color); t.Controls.Add(l, 0, r); t.SetColumnSpan(l, 2);
        }
        private void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            Controls.Add(root);
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = navy, ColumnCount = 2, Margin = Padding.Empty, Padding = new Padding(16, 4, 16, 4) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            header.Controls.Add(LabelOf("CH4  力矩采集", 22, Color.White), 0, 0);
            var subtitle = LabelOf("中诺 ZNSV6-F4-485\nRS485 / Modbus RTU · 功能码 03", 10, Color.FromArgb(187, 209, 221)); subtitle.TextAlign = ContentAlignment.MiddleRight;
            header.Controls.Add(subtitle, 1, 0); root.Controls.Add(header, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 0, 0), WrapContents = false, Margin = Padding.Empty };
            ButtonOf(start, "开始读取 CH4", 128, true, (o, e) => Begin(false));
            ButtonOf(demo, "体验演示", 98, false, (o, e) => Begin(true));
            ButtonOf(stop, "停止采集", 98, false, async (o, e) => await StopAsync());
            ButtonOf(record, "开始记录 CSV", 132, false, (o, e) => ToggleRecord());
            ButtonOf(tareButton, "软件去皮", 98, false, (o, e) => SetTare(false));
            ButtonOf(resetTare, "恢复去皮", 98, false, (o, e) => SetTare(true));
            ButtonOf(save, "保存设置", 98, false, (o, e) => SaveSettings());
            actions.Controls.AddRange(new Control[] { start, demo, stop, record, tareButton, resetTare, save }); root.Controls.Add(actions, 0, 1);
            banner.Dock = DockStyle.Fill; banner.Margin = new Padding(16, 3, 16, 5); banner.Padding = new Padding(10, 0, 0, 0); banner.TextAlign = ContentAlignment.MiddleLeft;
            root.Controls.Add(banner, 0, 2);
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12, 0, 12, 0), Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 354)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); root.Controls.Add(body, 0, 3);
            tabs.Dock = DockStyle.Fill; tabs.Margin = new Padding(4, 0, 10, 0);
            var connection = new TabPage("串口连接"); var protocol = new TabPage("CH4 解析参数");
            connection.AutoScroll = protocol.AutoScroll = true; tabs.TabPages.AddRange(new TabPage[] { connection, protocol }); body.Controls.Add(tabs, 0, 0);
            Choices(port); Choices(baud, "2400", "4800", "9600", "19200", "28800", "38400", "57600", "115200", "128000", "230400", "256000", "460800", "500000", "512000", "600000");
            Choices(parity, "None", "Even", "Odd"); Choices(stopBits, "1", "2");
            Choices(dataType, "Int16", "UInt16", "Int32", "UInt32", "Float32"); Choices(byteOrder, "AB", "BA", "ABCD", "BADC", "CDAB", "DCBA"); Choices(unit, "N·m", "N·mm", "kgf·cm", "原始单位");
            station.Minimum = 1; station.Maximum = 247; interval.Minimum = 20; interval.Maximum = 10000; timeout.Minimum = 50; timeout.Maximum = 10000;
            var c = Fields(connection);
            Field(c, "串口 COM", port); Field(c, "波特率", baud); Field(c, "从站地址", station); Field(c, "校验位", parity); Field(c, "停止位", stopBits); Field(c, "周期 / ms", interval); Field(c, "超时 / ms", timeout);
            Note(c, "数据位固定为 8。上述初值仅用于配置，\n请核对设备拨码或厂家软件。", 58, muted);
            var refresh = new Button { Text = "刷新串口列表", Height = 32, FlatStyle = FlatStyle.Flat }; refresh.Click += (o, e) => RefreshPorts(); Field(c, "插拔后刷新", refresh);
            Note(c, "一次仅能由一个上位机占用同一 COM。\n采集周期是电脑轮询间隔，\n并非变送器内部 AD 采样率。", 78, muted);
            var p = Fields(protocol);
            rawMode.Text = "先读原始值（未标定）";
            rawMode.CheckedChanged += (o, e) => { UpdateRawMode(); confirmed.Checked = false; Preview(); };
            Field(p, "显示方式", rawMode);
            Field(p, "CH4 协议地址", address); Field(p, "数据类型", dataType); Field(p, "字节序", byteOrder); Field(p, "换算系数", gain); Field(p, "换算偏置", offset); Field(p, "显示单位", unit);
            Note(p, "参考软件 CH1/2/3 地址为 0/2/4。\n本机 CH4 地址 6 已完成加载/卸载响应检查。\nInt32 + CDAB：每通道 2 寄存器，低字在前。", 80, muted);
            Note(p, "显示值 = 原始值 × 系数 + 偏置 − 去皮量\n若原始值已经是 N·m，系数填 1、偏置填 0。\n单位标签不会自动换算数值。", 76, muted);
            confirmed.Text = "我了解原始值尚未标定为力矩，\n需要已知力矩才能确定 N·m 系数"; confirmed.Dock = DockStyle.Fill; confirmed.Margin = new Padding(5);
            int cr = p.RowCount++; p.RowStyles.Add(new RowStyle(SizeType.Absolute, 56)); p.Controls.Add(confirmed, 0, cr); p.SetColumnSpan(confirmed, 2);
            requestLabel.Dock = DockStyle.Fill; requestLabel.ForeColor = teal; requestLabel.Font = new Font("Consolas", 9); requestLabel.Margin = new Padding(5);
            int pr = p.RowCount++; p.RowStyles.Add(new RowStyle(SizeType.Absolute, 60)); p.Controls.Add(requestLabel, 0, pr); p.SetColumnSpan(requestLabel, 2);
            foreach (Control input in new Control[] { address, dataType, byteOrder, gain, offset, unit }) input.TextChanged += (o, e) => { confirmed.Checked = false; Preview(); };
            station.ValueChanged += (o, e) => Preview();
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 164)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 118)); body.Controls.Add(right, 1, 0);
            var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, ColumnCount = 2, RowCount = 3, Padding = new Padding(16, 8, 16, 8), Margin = new Padding(0, 0, 0, 6) };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 76)); card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); card.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); card.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            valueTitle.Text = "CH4 当前读数"; valueTitle.Dock = DockStyle.Fill; valueTitle.ForeColor = muted; valueTitle.TextAlign = ContentAlignment.MiddleLeft; card.Controls.Add(valueTitle, 0, 0); card.SetColumnSpan(valueTitle, 2);
            valueLabel.Text = "--"; valueLabel.Dock = DockStyle.Fill; valueLabel.Font = new Font("Consolas", 38, FontStyle.Bold); valueLabel.ForeColor = teal; valueLabel.TextAlign = ContentAlignment.MiddleLeft;
            card.Controls.Add(valueLabel, 0, 1); unitLabel.Dock = DockStyle.Fill; unitLabel.TextAlign = ContentAlignment.MiddleLeft; unitLabel.Font = new Font(Font.FontFamily, 19); unitLabel.ForeColor = muted; card.Controls.Add(unitLabel, 1, 1);
            detail.Dock = DockStyle.Fill; detail.ForeColor = muted; detail.TextAlign = ContentAlignment.MiddleLeft; card.Controls.Add(detail, 0, 2); card.SetColumnSpan(detail, 2); right.Controls.Add(card, 0, 0);
            stats.Dock = DockStyle.Fill; stats.ForeColor = muted; stats.TextAlign = ContentAlignment.MiddleLeft; stats.Padding = new Padding(10, 0, 0, 0); right.Controls.Add(stats, 0, 1);
            trend.Dock = DockStyle.Fill; trend.Margin = new Padding(0, 0, 0, 6); right.Controls.Add(trend, 0, 2);
            log.Dock = DockStyle.Fill; log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical; log.Font = new Font("Consolas", 9); log.BackColor = Color.FromArgb(232, 239, 245); log.BorderStyle = BorderStyle.None; right.Controls.Add(log, 0, 3);
            footer.Dock = DockStyle.Fill; footer.Padding = new Padding(18, 0, 0, 0); footer.TextAlign = ContentAlignment.MiddleLeft; footer.ForeColor = muted; root.Controls.Add(footer, 0, 4);
        }

        private void ApplySettings(Settings s)
        {
            baud.SelectedItem = s.Baud.ToString(); if (baud.SelectedIndex < 0) baud.SelectedItem = "115200";
            parity.SelectedItem = s.Parity; if (parity.SelectedIndex < 0) parity.SelectedIndex = 0;
            stopBits.SelectedItem = s.StopBits.ToString(); if (stopBits.SelectedIndex < 0) stopBits.SelectedIndex = 0;
            station.Value = Math.Max(1, Math.Min(247, s.Station)); interval.Value = Math.Max(20, Math.Min(10000, s.Interval)); timeout.Value = Math.Max(50, Math.Min(10000, s.Timeout));
            address.Text = s.Address; dataType.SelectedItem = s.DataType; byteOrder.SelectedItem = s.ByteOrder;
            gain.Text = s.Gain; offset.Text = s.Offset; unit.SelectedItem = s.Unit; if (unit.SelectedIndex < 0) unit.SelectedIndex = 0;
            rawMode.Checked = s.RawMode; UpdateRawMode();
            confirmed.Checked = false; Preview();
        }
        private void UpdateRawMode()
        {
            gain.Enabled = offset.Enabled = unit.Enabled = !rawMode.Checked;
            if (!running) unitLabel.Text = rawMode.Checked ? "原始值" : unit.Text;
            if (!running) { trend.Unit = rawMode.Checked ? "原始单位" : unit.Text; trend.Invalidate(); }
        }
        private Settings ReadSettings()
        {
            return new Settings { Port = port.Text, Baud = int.Parse(baud.Text, CultureInfo.InvariantCulture), Parity = parity.Text, StopBits = int.Parse(stopBits.Text), Station = (int)station.Value, Address = address.Text.Trim(), DataType = dataType.Text, ByteOrder = byteOrder.Text, Gain = rawMode.Checked ? "1" : gain.Text.Trim(), Offset = rawMode.Checked ? "0" : offset.Text.Trim(), Unit = rawMode.Checked ? "原始单位" : unit.Text, Interval = (int)interval.Value, Timeout = (int)timeout.Value, RawMode = rawMode.Checked };
        }
        private void Preview()
        {
            try { requestLabel.Text = "03 读取请求（HEX）\n" + Modbus.Hex(Modbus.Request(ReadSettings())); }
            catch { requestLabel.Text = "完善 CH4 解析参数后\n显示只读请求报文。"; }
        }
        private void RefreshPorts()
        {
            string old = port.Text; port.Items.Clear();
            try { var ports = SerialPort.GetPortNames(); Array.Sort(ports, StringComparer.OrdinalIgnoreCase); port.Items.AddRange(ports); }
            catch (Exception ex) { AppendLog("串口枚举失败：" + ex.Message); }
            if (port.Items.Contains(old)) port.SelectedItem = old;
            else if (port.Items.Count == 1) port.SelectedIndex = 0;
            if (port.Items.Count == 0) AppendLog("未检测到 COM 串口。请连接 USB-RS485 转换器并安装对应驱动。");
        }
        private void SaveSettings()
        {
            try { ReadSettings().Save(settingsPath); AppendLog("已保存设置。重新启动后仍需人工确认 CH4 参数。"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "设置未保存", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        private void Begin(bool simulate)
        {
            if (running) return;
            try
            {
                Settings s = ReadSettings();
                if (!simulate)
                {
                    s.Validate(true);
                    if (!confirmed.Checked) { tabs.SelectedIndex = 1; throw new ArgumentException("请先查看“CH4 解析参数”并勾选说明。\n本机地址 6 已完成 CH4 加载、卸载响应检查。修改地址或解析参数后需要重新核对通道。\n力矩系数未确认时，请勾选“先读原始值”。"); }
                }
                if (simulate) { s.Gain = "1"; s.Offset = "0"; s.Unit = "N·m"; }
                active = s; scale = simulate ? 1 : Settings.Number(s.Gain, "系数"); bias = simulate ? 0 : Settings.Number(s.Offset, "偏置");
                tare = 0; count = errors = recordCount = 0; min = double.PositiveInfinity; max = double.NegativeInfinity; good = false; lastSeconds = 0;
                valueTitle.Text = simulate ? "模拟力矩 / CH4 演示" : s.RawMode ? "CH4 当前原始值（未标定）" : "CH4 换算读数 · 系数须标定核对";
                trend.ClearData(); trend.Unit = s.Unit; trend.Demo = simulate; isDemo = simulate; unitLabel.Text = s.Unit;
                if (cancellation != null) cancellation.Dispose(); cancellation = new CancellationTokenSource();
                CancellationToken token = cancellation.Token;
                running = true; tabs.Enabled = start.Enabled = demo.Enabled = save.Enabled = false; stop.Enabled = record.Enabled = true; tareButton.Enabled = resetTare.Enabled = false;
                valueLabel.Text = "--";
                SetBanner(simulate ? "演示模式 · 所有数值均为模拟数据，不代表传感器实测" : "正在连接 · CH4 地址 " + s.Address + "，等待设备应答", true);
                AppendLog(simulate ? "开始模拟会话。系数=1，偏置=0，单位=N·m；未打开串口。" : "开始设备会话：" + s.Port + "，" + s.Baud + "，站号 " + s.Station + "，CH4 地址 " + s.Address + "，只读 03。");
                worker = Task.Run(() => Acquisition(s, simulate, token));
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "请完善采集参数", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        private void Acquisition(Settings s, bool simulate, CancellationToken token)
        {
            var timer = Stopwatch.StartNew();
            IByteTransport serial = null;
            int failures = 0;
            try
            {
                if (!simulate) serial = new SerialTransport(s);
                while (!token.IsCancellationRequested)
                {
                    long cycle = timer.ElapsedMilliseconds;
                    try
                    {
                        double raw = simulate ? 3 * Math.Sin(timer.Elapsed.TotalSeconds * .85) + .35 * Math.Sin(timer.Elapsed.TotalSeconds * 4.2) : Modbus.Read(serial, s, token, msg => events.Enqueue(new SampleEvent { Kind = "log", Text = msg }));
                        Modbus.ConvertValue(raw, scale, bias, 0);
                        events.Enqueue(new SampleEvent { Kind = "sample", Raw = raw, Seconds = timer.Elapsed.TotalSeconds, Time = DateTimeOffset.Now });
                        failures = 0;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (UnauthorizedAccessException) { throw; }
                    catch (Exception ex)
                    {
                        events.Enqueue(new SampleEvent { Kind = "error", Text = ex.Message, Seconds = timer.Elapsed.TotalSeconds });
                        if (++failures >= 5) { events.Enqueue(new SampleEvent { Kind = "log", Text = "连续 5 次读取失败，采集已自动停止，请核对参数后重试。" }); break; }
                    }
                    int wait = Math.Max(0, s.Interval - (int)(timer.ElapsedMilliseconds - cycle));
                    if (token.WaitHandle.WaitOne(wait)) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { events.Enqueue(new SampleEvent { Kind = "error", Text = "无法继续采集：" + ex.Message, Seconds = timer.Elapsed.TotalSeconds }); }
            finally
            {
                if (serial != null) { try { serial.Dispose(); } catch (Exception ex) { events.Enqueue(new SampleEvent { Kind = "log", Text = "串口关闭：" + ex.Message }); } }
                events.Enqueue(new SampleEvent { Kind = "done" });
            }
        }
        private async Task StopAsync()
        {
            if (!running) return;
            stop.Enabled = false; cancellation.Cancel();
            if (worker != null) await worker;
            Drain();
        }
        private void Tick(object sender, EventArgs e)
        {
            Drain();
            if (running && good && (DateTimeOffset.Now - lastReceived).TotalMilliseconds > Math.Max(active.Timeout, active.Interval * 3))
            {
                good = false; valueLabel.Text = "--"; tareButton.Enabled = false;
                SetBanner(isDemo ? "演示模式 · 暂无新模拟数据" : "数据已过期 · 当前值暂停显示，等待下一次有效应答", true);
            }
        }
        private void Drain()
        {
            SampleEvent ev;
            while (events.TryDequeue(out ev))
            {
                if (ev.Kind == "log") { AppendLog(ev.Text); continue; }
                if (ev.Kind == "sample")
                {
                    lastRaw = ev.Raw; lastSeconds = ev.Seconds; lastReceived = ev.Time;
                    try { lastValue = Modbus.ConvertValue(lastRaw, scale, bias, tare); }
                    catch (Exception ex) { AppendLog(ex.Message); good = false; valueLabel.Text = "--"; continue; }
                    good = true; count++; min = Math.Min(min, lastValue); max = Math.Max(max, lastValue);
                    valueLabel.Text = FormatValue(lastValue); valueLabel.ForeColor = isDemo ? Color.FromArgb(181, 109, 10) : teal;
                    detail.Text = "原始值 " + lastRaw.ToString("G9", CultureInfo.InvariantCulture) + "   |   软件去皮量 " + tare.ToString("G6", CultureInfo.InvariantCulture) + "   |   " + lastReceived.ToString("HH:mm:ss.fff");
                    stats.Text = "有效点 " + count + "    最小 " + min.ToString("G6") + "    最大 " + max.ToString("G6") + "    均速 " + (count / Math.Max(ev.Seconds, .001)).ToString("0.0") + " 点/s    失败 " + errors;
                    trend.Add(ev.Seconds, lastValue); tareButton.Enabled = resetTare.Enabled = true;
                    SetBanner(isDemo ? "演示模式 · 所有数值均为模拟数据，不代表传感器实测" : active.RawMode ? "正在读取 CH4 原始值 · 尚未标定为 N·m · 仅发送 03 读命令" : "正在读取 CH4 换算值 · 换算系数须经标定核对 · 仅发送 03 读命令", true);
                    if (recorder != null)
                    {
                        try { recorder.Write(ev.Time, ev.Seconds, ev.Raw, lastValue, scale, bias, tare, active, isDemo); recordCount++; }
                        catch (Exception ex) { StopRecord(); MessageBox.Show(this, "CSV 记录失败，采集继续：\n" + ex.Message, "记录已停止", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    }
                }
                else if (ev.Kind == "error")
                {
                    errors++; good = false; valueLabel.Text = "--"; tareButton.Enabled = false; trend.Add(ev.Seconds, double.NaN);
                    SetBanner("读取失败 · " + ev.Text, true); AppendLog(ev.Text);
                }
                else if (ev.Kind == "done")
                {
                    running = false; StopRecord(); SetIdle(); valueLabel.Text = "--"; good = false;
                    if (isDemo) SetBanner("演示已停止 · 曲线为模拟数据", true);
                    else SetBanner(errors > 0 ? "采集已停止 · 请查看通信日志并核对参数" : "采集已停止 · 当前值暂停显示", errors > 0);
                    AppendLog("会话结束，串口已释放。有效数据 " + count + " 点，读取失败 " + errors + " 次。");
                }
            }
            footer.Text = recorder == null ? "CH4 专用  |  电脑本地去皮  |  曲线保留最近 60 秒  |  CSV 未记录" : "正在记录 " + recordCount + " 行  |  " + recordPath;
        }
        private static string FormatValue(double n)
        {
            return Math.Abs(n) >= 1000000 || (n != 0 && Math.Abs(n) < .0001) ? n.ToString("0.000E+0", CultureInfo.InvariantCulture) : n.ToString("0.0000", CultureInfo.InvariantCulture);
        }
        private void SetIdle()
        {
            tabs.Enabled = start.Enabled = demo.Enabled = save.Enabled = true; stop.Enabled = record.Enabled = tareButton.Enabled = resetTare.Enabled = false;
            if (active == null) { SetBanner("已完成本机 CH4 加载响应检查 · 默认显示未标定原始值 · N·m 系数待标定", true); detail.Text = "等待有效数据"; unitLabel.Text = rawMode.Checked ? "原始值" : unit.Text; valueTitle.Text = rawMode.Checked ? "CH4 当前原始值（未标定）" : "CH4 当前读数"; stats.Text = "CH4 独立显示 · CSV 持续写盘 · 通信异常提示"; }
        }
        private void SetBanner(string text, bool warning)
        {
            banner.Text = text; banner.BackColor = warning ? Color.FromArgb(255, 242, 214) : Color.FromArgb(218, 240, 237); banner.ForeColor = warning ? Color.FromArgb(137, 83, 9) : teal;
        }
        private void AppendLog(string text)
        {
            if (log.TextLength > 24000) log.Text = log.Text.Substring(log.TextLength - 12000);
            log.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + Environment.NewLine);
        }
        private void SetTare(bool reset)
        {
            if (!running || !good || (DateTimeOffset.Now - lastReceived).TotalMilliseconds > Math.Max(active.Timeout, active.Interval * 3)) return;
            tare = reset ? 0 : Modbus.ConvertValue(lastRaw, scale, bias, 0);
            min = double.PositiveInfinity; max = double.NegativeInfinity; trend.Add(lastSeconds, double.NaN);
            AppendLog(reset ? "已恢复软件去皮；极值统计重新开始。" : "已设置软件去皮量 " + tare.ToString("R", CultureInfo.InvariantCulture) + "；极值统计重新开始。此操作不向变送器写入任何参数。");
        }
        private void ToggleRecord()
        {
            if (recorder != null) { StopRecord(); return; }
            if (!running) return;
            using (var dialog = new SaveFileDialog { Title = isDemo ? "保存模拟数据 CSV" : "保存 CH4 实测数据 CSV", Filter = "CSV 文件 (*.csv)|*.csv", FileName = "CH4_" + (isDemo ? "DEMO_" : "") + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv", OverwritePrompt = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || !running) return;
                try { recorder = new CsvRecorder(dialog.FileName); recordPath = dialog.FileName; recordCount = 0; record.Text = "停止记录 CSV"; AppendLog("开始记录 CSV：" + recordPath); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法创建 CSV", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
        private void StopRecord()
        {
            if (recorder == null) return;
            var savedRecorder = recorder; recorder = null;
            try { savedRecorder.Dispose(); AppendLog("CSV 已保存 " + recordCount + " 行：" + recordPath); }
            catch (Exception ex) { AppendLog("CSV 关闭失败，请检查磁盘：" + ex.Message); }
            record.Text = "开始记录 CSV";
        }
        private async void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (closing) return;
            if (running) { e.Cancel = true; Enabled = false; await StopAsync(); closing = true; Close(); }
            else StopRecord();
        }
    }
}
