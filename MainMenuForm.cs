using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BgmHotkey
{
    internal sealed partial class MainMenuForm : Form
    {
        private readonly AppSettings _settings;
        private readonly AudioDeviceCatalog _devices;
        private readonly AudioEngine _engine;
        private readonly Label _status;
        private readonly ComboBox _microphone;
        private readonly ComboBox _headphone;
        private readonly ComboBox _cable;
        private readonly TrackBar _headphoneVolume;
        private readonly TrackBar _cableVolume;
        private readonly Label _headphoneValue;
        private readonly Label _cableValue;
        private readonly Dictionary<string, TextBox> _keyBoxes = new Dictionary<string, TextBox>();
        private bool _initializing;
        private volatile bool _capturingKey;
        private readonly ProgressBar _microphoneMeter;
        private readonly ProgressBar _musicMeter;
        private readonly ProgressBar _outputMeter;
        private readonly Label _routeResult;
        private readonly Button _routeCheck;
        private readonly Timer _meterTimer;
        private readonly TableLayoutPanel _bindingGrid;

        private void RebuildBindings()
        {
            _bindingGrid.SuspendLayout();
            while (_bindingGrid.Controls.Count > 0) _bindingGrid.Controls[0].Dispose();
            _bindingGrid.RowStyles.Clear();
            _keyBoxes.Clear();
            _playButtons.Clear();
            _capturingKey = false;
            _bindingGrid.RowCount = TrackCatalog.All.Length + 1;
            _bindingGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, UiPixels(24)));
            AddBindingHeader(_bindingGrid);
            _songCount.Text = TrackCatalog.All.Length + " 首歌曲";
            for (int i = 0; i < TrackCatalog.All.Length; i++)
            {
                TrackDefinition track = TrackCatalog.All[i];
                _bindingGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, UiPixels(80)));
                TableLayoutPanel song = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = UiPadding(12, 12, 8, 12), Margin = new Padding(0), BackColor = i % 2 == 0 ? UiTheme.Background : Color.White };
                song.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); song.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
                song.Controls.Add(UiTheme.Label(track.Name, 10, true), 0, 0);
                Label file = UiTheme.Label(track.FileName, 8, false); file.ForeColor = UiTheme.Muted;
                song.Controls.Add(file, 0, 1);
                _bindingGrid.Controls.Add(song, 0, i + 1);

                TextBox key = new TextBox();
                key.ReadOnly = true;
                key.TextAlign = HorizontalAlignment.Center;
                key.Dock = DockStyle.Fill;
                key.Margin = UiPadding(8, 26, 8, 26);
                key.BackColor = UiTheme.Tint;
                key.ForeColor = UiTheme.Accent;
                key.Font = new Font("Segoe UI", 10, FontStyle.Bold);
                key.BorderStyle = BorderStyle.FixedSingle;
                key.Cursor = Cursors.Hand;
                key.Text = FormatKey(_settings.GetKey(track.Id));
                key.Tag = track.Id;
                key.PreviewKeyDown += KeyBoxPreviewKeyDown;
                key.KeyDown += KeyBoxKeyDown;
                key.GotFocus += delegate { _capturingKey = true; key.BackColor = Color.FromArgb(233, 241, 255); };
                key.LostFocus += delegate { _capturingKey = false; key.BackColor = UiTheme.Tint; };
                _bindingGrid.Controls.Add(key, 1, i + 1);
                _keyBoxes[track.Id] = key;
                Button play = UiTheme.Button("播放", false); play.Dock = DockStyle.Fill; play.Margin = UiPadding(5, 20, 5, 20);
                play.Click += delegate { _engine.ToggleTrack(track); };
                _playButtons[track.Id] = play;
                _bindingGrid.Controls.Add(play, 2, i + 1);
                Button deleteSong = UiTheme.Button("删除", false); deleteSong.Dock = DockStyle.Fill; deleteSong.Margin = UiPadding(5, 20, 4, 20);
                deleteSong.FlatAppearance.BorderSize = 0; deleteSong.ForeColor = Color.FromArgb(165, 76, 73);
                deleteSong.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 239, 238);
                deleteSong.Click += delegate { DeleteSong(track); };
                _bindingGrid.Controls.Add(deleteSong, 3, i + 1);
            }
            if (TrackCatalog.All.Length == 0)
            {
                _bindingGrid.RowCount = 2;
                _bindingGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, UiPixels(120)));
                Label empty = UiTheme.Label("曲库还是空的\n点击“添加歌曲”，选一首喜欢的 MP3。", 10, false);
                empty.ForeColor = UiTheme.Muted; empty.TextAlign = ContentAlignment.MiddleCenter;
                _bindingGrid.Controls.Add(empty, 0, 1); _bindingGrid.SetColumnSpan(empty, 4);
            }
            _bindingGrid.ResumeLayout(true);
            if (_status != null) RefreshStatus();
        }

        private void AddSongs(object sender, EventArgs e)
        {
            using (OpenFileDialog picker = new OpenFileDialog { Title = "添加 MP3 歌曲", Filter = "MP3 音频文件 (*.mp3)|*.mp3", Multiselect = true, CheckFileExists = true })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                SavedTrack[] previousTracks = _settings.Tracks;
                TrackBinding[] previousBindings = _settings.Bindings;
                SavedTrack last = null;
                try
                {
                    foreach (string path in picker.FileNames) last = SongLibrary.Add(_settings, path);
                    string error;
                    if (!SettingsStore.TrySave(_settings, out error)) throw new InvalidOperationException("保存歌曲列表失败：" + error);
                    RebuildBindings();
                    ShowLibraryNotice("歌曲已添加，点击按键栏即可绑定快捷键。", false);
                    if (last != null && _keyBoxes.ContainsKey(last.Id)) _keyBoxes[last.Id].Focus();
                }
                catch (Exception ex)
                {
                    _settings.Tracks = previousTracks;
                    _settings.Bindings = previousBindings;
                    _settings.Normalize();
                    ShowLibraryNotice("添加失败：" + ex.Message, true);
                }
            }
        }

        private void DeleteSong(TrackDefinition track)
        {
            SavedTrack[] previousTracks = _settings.Tracks;
            TrackBinding[] previousBindings = _settings.Bindings;
            SongLibrary.Remove(_settings, track.Id);
            string error;
            if (!SettingsStore.TrySave(_settings, out error))
            {
                _settings.Tracks = previousTracks;
                _settings.Bindings = previousBindings;
                _settings.Normalize();
                ShowLibraryNotice("删除失败：" + error, true);
                return;
            }
            _engine.StopTrack(track.Id);
            RebuildBindings();
            ShowLibraryNotice("已移除“" + track.Name + "”，音频文件保留。", false);
        }

        private void ShowLibraryNotice(string message, bool error)
        {
            _libraryNotice.Text = message;
            _libraryNotice.ForeColor = error ? Color.FromArgb(170, 50, 35) : UiTheme.Muted;
        }

        private static ProgressBar AddMeter(TableLayoutPanel grid, string label, int row)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
            grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            ProgressBar meter = new UiMeter { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Margin = new Padding(4, 3, 4, 3) };
            grid.Controls.Add(meter, 1, row);
            return meter;
        }

        private void CheckVirtualMicrophone(object sender, EventArgs e)
        {
            AudioDevice recording = _devices.FindCableRecording(_devices.FindCable(_settings.CableDeviceName));
            _routeCheck.Enabled = false;
            _routeResult.Text = "正在采集虚拟录音端，请播放 BGM 或讲话……";
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string result;
                bool success = false;
                try
                {
                    int peak = AudioRouteProbe.MeasurePeak(recording);
                    success = peak > AudioRouteProbe.SignalFloor;
                    result = success ? "CABLE Output 已收到信号（" + AudioRouteProbe.FormatPeak(peak) + "）。聊天软件麦克风请选择该设备。"
                        : "CABLE Output 未收到明显信号。请确认 BGM 电平、混合输出电平和音量后重试。";
                }
                catch (Exception ex) { result = "虚拟录音端检查失败：" + ex.Message; }
                AppPaths.Log("虚拟通道自检：" + result);
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _routeCheck.Enabled = true;
                        _routeResult.ForeColor = success ? Color.FromArgb(20, 110, 50) : Color.FromArgb(170, 50, 35);
                        _routeResult.Text = result;
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        public void PlayHotkey(string keyName)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new MethodInvoker(delegate { PlayHotkey(keyName); })); }
                catch { }
                return;
            }

            foreach (TrackDefinition track in TrackCatalog.All)
            {
                if (String.Equals(_settings.GetKey(track.Id), keyName, StringComparison.OrdinalIgnoreCase))
                {
                    _engine.ToggleTrack(track);
                    return;
                }
            }
        }

        public bool IsCapturingKey
        {
            get { return _capturingKey; }
        }

        private static ComboBox CreateCombo()
        {
            return new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                IntegralHeight = false,
                Margin = new Padding(2, 5, 2, 3)
            };
        }

        private static TrackBar CreateTrackBar(int value)
        {
            TrackBar trackBar = new TrackBar();
            trackBar.Minimum = 0;
            trackBar.Maximum = 100;
            trackBar.TickFrequency = 10;
            trackBar.TickStyle = TickStyle.None;
            trackBar.BackColor = Color.White;
            trackBar.SmallChange = 1;
            trackBar.LargeChange = 5;
            trackBar.Value = Math.Max(0, Math.Min(100, value));
            trackBar.Dock = DockStyle.Fill;
            trackBar.Margin = new Padding(0, 0, 2, 0);
            return trackBar;
        }

        private static Label CreateValueLabel(int value)
        {
            return new Label
            {
                Text = FormatMappedVolume(value),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight
            };
        }

        private static void AddDeviceRow(TableLayoutPanel panel, string label, ComboBox combo, int row)
        {
            Label name = new Label();
            name.Text = label;
            name.Dock = DockStyle.Fill;
            name.TextAlign = ContentAlignment.MiddleLeft;
            panel.Controls.Add(name, 0, row);
            panel.Controls.Add(combo, 1, row);
        }

        private static void AddBindingHeader(TableLayoutPanel panel)
        {
            panel.Controls.Add(new Label { Text = "歌曲 / 文件", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiTheme.Muted, Padding = new Padding(10, 0, 0, 0) }, 0, 0);
            panel.Controls.Add(new Label { Text = "快捷键", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = UiTheme.Muted }, 1, 0);
        }

        private void LoadDevices()
        {
            _initializing = true;
            _microphone.Items.Clear();
            foreach (AudioDevice device in _devices.Microphones)
                _microphone.Items.Add(device);
            SelectAvailableOrMissing(_microphone, _settings.MicrophoneDeviceName, _devices.FindMicrophone(_settings.MicrophoneDeviceName), "所选麦克风不可用，请重新选择");

            _headphone.Items.Clear();
            _headphone.Items.Add(new AudioDevice(0xFFFFFFFF, "Windows 默认播放设备（原生 MP3）"));
            _headphone.SelectedIndex = 0;
            _headphone.Enabled = false;

            _cable.Items.Clear();
            foreach (AudioDevice device in _devices.Cables)
                _cable.Items.Add(device);
            SelectAvailableOrMissing(_cable, _settings.CableDeviceName, _devices.FindCable(_settings.CableDeviceName), "所选 VB-CABLE 不可用，请重新选择");
            _initializing = false;
        }

        private static void SelectAvailableOrMissing(ComboBox combo, string savedName, AudioDevice resolved, string missing)
        {
            if (resolved != null) { SelectByName(combo, resolved.Name, resolved.Name); return; }
            AudioDevice unavailable = new AudioDevice(0xFFFFFFFE, missing + (String.IsNullOrWhiteSpace(savedName) ? "" : "：" + savedName));
            combo.Items.Insert(0, unavailable);
            combo.SelectedIndex = 0;
        }

        private static void SelectByName(ComboBox combo, string name, string fallback)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                AudioDevice device = combo.Items[i] as AudioDevice;
                if (device != null && String.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            for (int i = 0; i < combo.Items.Count; i++)
            {
                AudioDevice device = combo.Items[i] as AudioDevice;
                if (device != null && String.Equals(device.Name, fallback, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private void VolumeChanged(object sender, EventArgs e)
        {
            if (_initializing)
                return;
            _settings.HeadphoneVolume = _headphoneVolume.Value;
            _settings.CableVolume = _cableVolume.Value;
            _headphoneValue.Text = FormatMappedVolume(_headphoneVolume.Value);
            _cableValue.Text = FormatMappedVolume(_cableVolume.Value);
            _engine.SetVolumes(_settings.HeadphoneVolume, _settings.CableVolume);
            SettingsStore.Save(_settings);
        }

        private void DeviceChanged(object sender, EventArgs e)
        {
            if (_initializing)
                return;
            AudioDevice microphone = _microphone.SelectedItem as AudioDevice;
            AudioDevice cable = _cable.SelectedItem as AudioDevice;
            if (microphone != null && microphone.Id != 0xFFFFFFFE) _settings.MicrophoneDeviceName = microphone.Name;
            if (cable != null && cable.Id != 0xFFFFFFFE)
            {
                _settings.CableDeviceName = cable.Name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cable.Name.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0 ? cable.Name : "";
            }
            SettingsStore.Save(_settings);
            _engine.ReconfigureDevices();
            RefreshStatus();
        }

        private void KeyBoxPreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            e.IsInputKey = true;
        }

        private void KeyBoxKeyDown(object sender, KeyEventArgs e)
        {
            TextBox box = sender as TextBox;
            if (box == null || box.Tag == null)
                return;
            Keys key = e.KeyCode;
            e.SuppressKeyPress = true;
            e.Handled = true;
            string keyName = KeyName(key);
            if (key == Keys.Escape || key == Keys.Back || key == Keys.Delete ||
                key == Keys.ShiftKey || key == Keys.ControlKey || key == Keys.Menu)
            {
                ShowLibraryNotice("请按一个字母、数字或功能键。", true);
                return;
            }

            string trackId = (string)box.Tag;
            foreach (TrackDefinition track in TrackCatalog.All)
            {
                if (track.Id != trackId && String.Equals(_settings.GetKey(track.Id), keyName, StringComparison.OrdinalIgnoreCase))
                {
                    ShowLibraryNotice("这个按键已绑定给“" + track.Name + "”。", true);
                    return;
                }
            }

            string previousKey = _settings.GetKey(trackId);
            _settings.SetKey(trackId, keyName);
            string saveError;
            if (!SettingsStore.TrySave(_settings, out saveError))
            {
                _settings.SetKey(trackId, previousKey);
                ShowLibraryNotice("快捷键保存失败：" + saveError, true);
                return;
            }
            box.Text = FormatKey(keyName);
            ShowLibraryNotice("快捷键已保存。点击其他区域后即可使用。", false);
            RefreshStatus();
        }

        private static string KeyName(Keys key)
        {
            return key.ToString();
        }

        private static string FormatKey(string key)
        {
            if (String.IsNullOrWhiteSpace(key))
                return "未设置";
            if (key.Length == 2 && key[0] == 'D' && Char.IsDigit(key[1]))
                return key.Substring(1);
            if (key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase))
                return "Num " + key.Substring(6);
            return key;
        }

        private static string FormatMappedVolume(int sliderValue)
        {
            int outputPercent = (int)Math.Round(sliderValue * 0.30);
            return sliderValue + "% → " + outputPercent + "%";
        }

        public void SetHotkeyError(string message)
        {
            _hotkeyError = message ?? "";
            RefreshStatus();
        }

        private string _hotkeyError = "";

        private void EngineStatusChanged(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            try
            {
                BeginInvoke(new MethodInvoker(RefreshStatus));
            }
            catch { }
        }

        private void RefreshStatus()
        {
            if (!IsDisposed)
            {
                _status.Text = String.IsNullOrWhiteSpace(_hotkeyError)
                    ? _engine.StatusText
                    : _hotkeyError + Environment.NewLine + _engine.StatusText;
                _playbackBadge.Text = "●  " + (String.IsNullOrWhiteSpace(_hotkeyError) ? _engine.PlaybackSummary : "快捷键不可用");
                _playbackBadge.ForeColor = String.IsNullOrWhiteSpace(_hotkeyError) ? UiTheme.Accent : Color.FromArgb(170, 50, 35);
                foreach (KeyValuePair<string, Button> pair in _playButtons)
                {
                    bool playing = _engine.IsTrackPlaying(pair.Key);
                    pair.Value.Text = playing ? "停止" : "播放";
                    pair.Value.BackColor = playing ? UiTheme.Tint : Color.White;
                    pair.Value.ForeColor = playing ? UiTheme.Accent : UiTheme.Text;
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SettingsStore.Save(_settings);
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _engine.StatusChanged -= EngineStatusChanged;
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _meterTimer != null) _meterTimer.Dispose();
            base.Dispose(disposing);
        }

        public void ExitApplication()
        {
            Close();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_status != null)
                RefreshStatus();
        }
    }
}
