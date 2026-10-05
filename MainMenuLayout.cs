using System;
using System.Drawing;
using System.Windows.Forms;

namespace BgmHotkey
{
    internal sealed partial class MainMenuForm
    {
        private readonly Label _songCount;
        private readonly Label _playbackBadge;
        private readonly Label _libraryNotice;
        private readonly TabControl _audioTabs;
        private readonly System.Collections.Generic.Dictionary<TableLayoutStyle, float> _designTableSizes = new System.Collections.Generic.Dictionary<TableLayoutStyle, float>();
        private readonly System.Collections.Generic.Dictionary<string, Button> _playButtons = new System.Collections.Generic.Dictionary<string, Button>();

        public MainMenuForm(AppSettings settings, AudioDeviceCatalog devices, AudioEngine engine)
        {
            SuspendLayout();
            _settings = settings;
            _devices = devices;
            _engine = engine;
            settings.Normalize();
            _initializing = true;
            Text = "万象 · BGM 播放器";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(960, 720);
            ClientSize = new Size(1280, 900);
            Font = new Font("Microsoft YaHei UI", 10F);
            ForeColor = UiTheme.Text;
            BackColor = UiTheme.Background;
            DoubleBuffered = true;

            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 12), ColumnCount = 1, RowCount = 3, BackColor = UiTheme.Background };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(root);

            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 12) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.Controls.Add(UiTheme.Label("万象 / BGM", 23, true), 0, 0);
            Label subtitle = UiTheme.Label("让音乐加入语音，让操作保持简单。", 9, false);
            subtitle.ForeColor = UiTheme.Muted;
            header.Controls.Add(subtitle, 0, 1);
            _playbackBadge = UiTheme.Label("●  等待播放", 10, true);
            _playbackBadge.ForeColor = UiTheme.Accent;
            _playbackBadge.BackColor = UiTheme.Tint;
            _playbackBadge.Padding = new Padding(12, 0, 8, 0);
            _playbackBadge.Margin = new Padding(0, 4, 0, 4);
            header.Controls.Add(_playbackBadge, 1, 0);
            Label saved = UiTheme.Label("歌曲与快捷键自动保存", 8.5F, false);
            saved.ForeColor = UiTheme.Muted;
            saved.TextAlign = ContentAlignment.MiddleRight;
            header.Controls.Add(saved, 1, 1);
            root.Controls.Add(header, 0, 0);

            TableLayoutPanel body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            root.Controls.Add(body, 0, 1);

            UiCard library = new UiCard { Margin = new Padding(0, 0, 24, 0) };
            body.Controls.Add(library, 0, 0);
            TableLayoutPanel songs = Stack(4);
            songs.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            songs.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            songs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            songs.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            library.Controls.Add(songs);
            TableLayoutPanel libraryHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            libraryHeading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            libraryHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            libraryHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
            libraryHeading.Controls.Add(UiTheme.Label("我的曲库", 15, true), 0, 0);
            _songCount = UiTheme.Label("", 9, false);
            _songCount.ForeColor = UiTheme.Muted;
            _songCount.TextAlign = ContentAlignment.MiddleRight;
            libraryHeading.Controls.Add(_songCount, 1, 0);
            songs.Controls.Add(libraryHeading, 0, 0);
            FlowLayoutPanel songActions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 12) };
            Button add = UiTheme.Button("＋ 添加歌曲", true);
            add.Width = 140; add.Height = 40;
            add.Click += AddSongs;
            songActions.Controls.Add(add);
            Label keyHint = UiTheme.Label("点击按键栏即可重新绑定", 8.5F, false);
            keyHint.Dock = DockStyle.None;
            keyHint.Size = new Size(210, 40);
            keyHint.ForeColor = UiTheme.Muted;
            songActions.Controls.Add(keyHint);
            songs.Controls.Add(songActions, 0, 1);
            Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0, 8, 0, 0) };
            _bindingGrid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Margin = new Padding(0), BackColor = Color.White };
            _bindingGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _bindingGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            _bindingGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
            _bindingGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            scroll.Controls.Add(_bindingGrid);
            songs.Controls.Add(scroll, 0, 2);
            _libraryNotice = UiTheme.Label("按任意曲目键停止播放。\n删除仅移出曲库，保留音频文件。", 8F, false);
            _libraryNotice.Padding = new Padding(0, 12, 0, 0);
            _libraryNotice.ForeColor = UiTheme.Muted;
            songs.Controls.Add(_libraryNotice, 0, 3);
            RebuildBindings();

            UiCard audio = new UiCard { Margin = new Padding(0) };
            body.Controls.Add(audio, 1, 0);
            TableLayoutPanel audioLayout = Stack(2);
            audioLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            audioLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            audio.Controls.Add(audioLayout);
            audioLayout.Controls.Add(UiTheme.Label("音频控制", 15, true), 0, 0);
            TabControl tabs = _audioTabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 10F), Padding = new Point(14, 8), Margin = new Padding(0) };
            TabPage sound = new TabPage("设备与音量") { BackColor = Color.White, Padding = new Padding(16, 20, 16, 16), AutoScroll = true };
            TabPage diagnostics = new TabPage("信号与检测") { BackColor = Color.White, Padding = new Padding(12), AutoScroll = true };
            tabs.TabPages.Add(sound);
            tabs.TabPages.Add(diagnostics);
            audioLayout.Controls.Add(tabs, 0, 1);

            TableLayoutPanel soundGrid = Stack(8); soundGrid.Dock = DockStyle.Top; soundGrid.AutoSize = true;
            float[] sizes = { 80, 80, 80, 0, 108, 108, 72, 56 };
            foreach (float size in sizes) soundGrid.RowStyles.Add(new RowStyle(size == 0 ? SizeType.AutoSize : SizeType.Absolute, size));
            sound.Controls.Add(soundGrid);
            _microphone = CreateCombo(); _headphone = CreateCombo(); _cable = CreateCombo();
            soundGrid.Controls.Add(DeviceField("麦克风输入", _microphone), 0, 0);
            soundGrid.Controls.Add(DeviceField("耳机输出 · 跟随系统默认设备", _headphone), 0, 1);
            soundGrid.Controls.Add(DeviceField("虚拟通道 · CABLE Input", _cable), 0, 2);
            FlowLayoutPanel deviceActions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 20) };
            Button refresh = UiTheme.Button("刷新设备", false); refresh.Width = 100; refresh.Height = 38;
            refresh.Click += delegate { _devices.Refresh(); LoadDevices(); _engine.ReconfigureDevices(); _routeResult.Text = "设备已刷新，请确认所选麦克风。"; };
            Button system = UiTheme.Button("系统录音设置", false); system.Width = 120; system.Height = 38;
            system.Click += delegate { try { System.Diagnostics.Process.Start("control.exe", "mmsys.cpl,,1"); } catch (Exception ex) { _routeResult.Text = ex.Message; } };
            deviceActions.Controls.Add(refresh); deviceActions.Controls.Add(system);
            soundGrid.Controls.Add(deviceActions, 0, 3);
            _headphoneVolume = CreateTrackBar(settings.HeadphoneVolume);
            _cableVolume = CreateTrackBar(settings.CableVolume);
            _headphoneValue = CreateValueLabel(settings.HeadphoneVolume);
            _cableValue = CreateValueLabel(settings.CableVolume);
            soundGrid.Controls.Add(VolumeField("耳机音乐", _headphoneVolume, _headphoneValue), 0, 4);
            soundGrid.Controls.Add(VolumeField("队友听到的音乐", _cableVolume, _cableValue), 0, 5);
            Label range = UiTheme.Label("滑块 0–100% 对应实际增益 0–30%。\n虚拟通道音乐音量不会影响麦克风。", 8.5F, false);
            range.ForeColor = UiTheme.Muted;
            soundGrid.Controls.Add(range, 0, 6);
            Label routeHint = UiTheme.Label("聊天软件麦克风请选择 CABLE Output", 8.5F, true);
            routeHint.ForeColor = UiTheme.Accent;
            soundGrid.Controls.Add(routeHint, 0, 7);

            TableLayoutPanel checkGrid = Stack(5); checkGrid.Dock = DockStyle.Top; checkGrid.AutoSize = true;
            checkGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            checkGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            checkGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            checkGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            checkGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            diagnostics.Controls.Add(checkGrid);
            TableLayoutPanel meters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = new Padding(0, 0, 0, 12) };
            meters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86)); meters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _microphoneMeter = AddMeter(meters, "麦克风", 0); _musicMeter = AddMeter(meters, "音乐", 1); _outputMeter = AddMeter(meters, "混合输出", 2);
            checkGrid.Controls.Add(meters, 0, 0);
            _status = UiTheme.Label("", 8.5F, false); _status.AutoEllipsis = false; _status.AutoSize = true; _status.MinimumSize = new Size(0, 150); _status.TextAlign = ContentAlignment.TopLeft; _status.Padding = new Padding(0, 12, 0, 10);
            checkGrid.Controls.Add(_status, 0, 1);
            _routeCheck = UiTheme.Button("检查虚拟麦克风", true); _routeCheck.Dock = DockStyle.Fill; _routeCheck.Margin = new Padding(0, 4, 0, 6); _routeCheck.Click += CheckVirtualMicrophone;
            checkGrid.Controls.Add(_routeCheck, 0, 2);
            _routeResult = UiTheme.Label("播放歌曲后点击检查，确认虚拟录音端实际收到声音。", 8.5F, false); _routeResult.AutoEllipsis = false; _routeResult.AutoSize = true; _routeResult.MinimumSize = new Size(0, 74); _routeResult.TextAlign = ContentAlignment.TopLeft; _routeResult.Padding = new Padding(0, 8, 0, 10);
            checkGrid.Controls.Add(_routeResult, 0, 3);
            Label signalHint = UiTheme.Label("电平表示实时信号；检测不会保存录音。", 8F, false); signalHint.ForeColor = UiTheme.Muted; checkGrid.Controls.Add(signalHint, 0, 4);
            diagnostics.SizeChanged += delegate
            {
                int width = Math.Max(UiPixels(180), diagnostics.ClientSize.Width - diagnostics.Padding.Horizontal);
                _status.MaximumSize = new Size(width, 0);
                _routeResult.MaximumSize = new Size(width, 0);
            };

            TableLayoutPanel footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, Margin = new Padding(0, 10, 0, 0) };
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Label footnote = UiTheme.Label("快捷键全局生效  ·  关闭窗口即退出播放器", 8.5F, false); footnote.ForeColor = UiTheme.Muted;
            footer.Controls.Add(footnote, 0, 0);
            root.Controls.Add(footer, 0, 2);

            _headphoneVolume.ValueChanged += VolumeChanged; _cableVolume.ValueChanged += VolumeChanged;
            _microphone.SelectedIndexChanged += DeviceChanged; _cable.SelectedIndexChanged += DeviceChanged;
            _engine.StatusChanged += EngineStatusChanged;
            LoadDevices(); _initializing = false; RefreshStatus();
            _meterTimer = new Timer { Interval = 100 };
            _meterTimer.Tick += delegate { _microphoneMeter.Value = AudioRouteProbe.MeterValue(_engine.MicrophonePeak); _musicMeter.Value = AudioRouteProbe.MeterValue(_engine.MusicPeak); _outputMeter.Value = AudioRouteProbe.MeterValue(_engine.OutputPeak); };
            _meterTimer.Start();
            Shown += delegate
            {
                ApplyTableDpi();
                FitToDisplay(true);
                ActiveControl = add;
                scroll.AutoScrollPosition = Point.Empty;
                sound.AutoScrollPosition = Point.Empty;
                AppPaths.Log("显示模式：" + DisplayDpi.Describe() + "；窗口 DPI：" + DeviceDpi
                    + "；布局 DPI：" + CurrentAutoScaleDimensions.Width + "；屏幕：" + Screen.FromControl(this).Bounds.Size);
            };
            DpiChanged += delegate
            {
                BeginInvoke(new MethodInvoker(delegate { ApplyTableDpi(); FitToDisplay(false); Invalidate(true); }));
            };
            // Complete the control tree before consuming its design-time DPI scale.
            AutoScaleDimensions = new SizeF(96F, 96F);
            CaptureTableSizes(this);
            ResumeLayout(true);
        }

        private void CaptureTableSizes(Control control)
        {
            TableLayoutPanel table = control as TableLayoutPanel;
            if (table != null)
            {
                foreach (RowStyle row in table.RowStyles)
                    if (row.SizeType == SizeType.Absolute) _designTableSizes[row] = row.Height;
                foreach (ColumnStyle column in table.ColumnStyles)
                    if (column.SizeType == SizeType.Absolute) _designTableSizes[column] = column.Width;
            }
            foreach (Control child in control.Controls) CaptureTableSizes(child);
        }

        private void ApplyTableDpi()
        {
            SuspendLayout();
            foreach (var item in _designTableSizes)
            {
                RowStyle row = item.Key as RowStyle;
                if (row != null) row.Height = (float)Math.Round(item.Value * CurrentAutoScaleDimensions.Height / 96F);
                ColumnStyle column = item.Key as ColumnStyle;
                if (column != null) column.Width = (float)Math.Round(item.Value * CurrentAutoScaleDimensions.Width / 96F);
            }
            ResumeLayout(true);
        }

        // Layout uses 96-DPI units; at 200% the 4K window is 2560 x 1800 pixels.
        // WinForms renders controls at the monitor's DPI instead of stretching a bitmap.
        private void FitToDisplay(bool initial)
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            float scale = CurrentAutoScaleDimensions.Width / 96F;
            int gap = Math.Max(8, (int)Math.Round(16 * scale));
            int maxWidth = Math.Max(1, area.Width - gap * 2);
            int maxHeight = Math.Max(1, area.Height - gap * 2);
            MinimumSize = new Size(Math.Min(maxWidth, (int)Math.Round(960 * scale)),
                Math.Min(maxHeight, (int)Math.Round(720 * scale)));
            if (initial)
                ClientSize = new Size((int)Math.Round(1280 * scale), (int)Math.Round(900 * scale));
            if (WindowState != FormWindowState.Normal) return;
            Size = new Size(Math.Min(Width, maxWidth), Math.Min(Height, maxHeight));
            Location = initial ? new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2)
                : new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)),
                    Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }

        private int UiPixels(int value)
        {
            return (int)Math.Round(value * (IsHandleCreated ? CurrentAutoScaleDimensions.Width / 96F : 1F));
        }

        private Padding UiPadding(int left, int top, int right, int bottom)
        {
            return new Padding(UiPixels(left), UiPixels(top), UiPixels(right), UiPixels(bottom));
        }

        private static TableLayoutPanel Stack(int rows)
        {
            return new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, Margin = new Padding(0), BackColor = Color.White };
        }

        private static TableLayoutPanel DeviceField(string caption, ComboBox combo)
        {
            TableLayoutPanel field = Stack(2);
            field.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); field.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label label = UiTheme.Label(caption, 8.5F, true);
            field.Controls.Add(label, 0, 0); combo.Margin = new Padding(0, 0, 0, 8); field.Controls.Add(combo, 0, 1);
            return field;
        }

        private static TableLayoutPanel VolumeField(string caption, TrackBar slider, Label value)
        {
            TableLayoutPanel field = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 8, 0, 12) };
            field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            field.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            field.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            field.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            field.Controls.Add(UiTheme.Label(caption, 9, true), 0, 0);
            value.ForeColor = UiTheme.Accent; value.TextAlign = ContentAlignment.MiddleLeft; field.Controls.Add(value, 0, 1);
            field.Controls.Add(slider, 0, 2); return field;
        }
    }
}
