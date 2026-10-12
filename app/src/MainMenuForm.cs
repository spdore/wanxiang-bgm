using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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
        private bool _initializing;
        private volatile bool _capturingKey;
        private readonly ProgressBar _microphoneMeter;
        private readonly ProgressBar _musicMeter;
        private readonly ProgressBar _outputMeter;
        private readonly Label _routeResult;
        private readonly Button _routeCheck;
        private readonly Timer _meterTimer;

        private void RebuildBindings()
        {
            _capturingKey = false;
            RefreshStatus();
        }

        private void AddSongs(object sender, EventArgs e)
        {
            string initialLibraryFolder = AppPaths.TrackPath("");
            try { Directory.CreateDirectory(initialLibraryFolder); }
            catch (Exception ex) { ShowLibraryNotice("无法打开曲库文件夹：" + ex.Message, true); return; }
            using (OpenFileDialog picker = new OpenFileDialog { Title = "添加音频", Filter = AudioFormatCatalog.FileDialogFilter, InitialDirectory = initialLibraryFolder, RestoreDirectory = true, Multiselect = true, CheckFileExists = true })
            {
                DialogResult picked;
                _capturingKey = true;
                try { picked = picker.ShowDialog(this); }
                finally { _capturingKey = false; }
                if (picked != DialogResult.OK) return;
                string[] paths = (string[])picker.FileNames.Clone();
                _capturingKey = true;
                ShowLibraryNotice("正在检查音频格式和时长…", false);
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    List<string> playable = new List<string>();
                    List<string> failures = new List<string>();
                    foreach (string path in paths)
                    {
                        try { AudioFormatCatalog.ProbeDurationSeconds(path); playable.Add(path); }
                        catch (Exception ex) { failures.Add(Path.GetFileName(path) + "：" + ex.Message); }
                    }
                    if (IsDisposed || !IsHandleCreated) { _capturingKey = false; return; }
                    try { BeginInvoke(new MethodInvoker(delegate
                    {
                        if (IsDisposed) return;
                        SavedTrack[] previousAssets = (SavedTrack[])_settings.Tracks.Clone();
                        SavedTrack[] previousTracks = _settings.ActiveTracks();
                        ScenePreset previousScene = _settings.CloneActiveScene();
                        HashSet<string> filesBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        string libraryFolder = AppPaths.TrackPath("");
                        if (Directory.Exists(libraryFolder))
                            foreach (string filePath in Directory.GetFiles(libraryFolder)) filesBefore.Add(Path.GetFileName(filePath));
                        List<string> addedFiles = new List<string>();
                        List<string> newTrackFiles = new List<string>();
                        List<string> copyFailures = new List<string>();
                        int addedTrackCount = 0;
                        bool settingsChanged = false;
                        foreach (string path in playable)
                        {
                            try
                            {
                                SavedTrack track = SongLibrary.Add(_settings, path);
                                if (track != null && Array.Find(previousTracks, delegate(SavedTrack item)
                                    { return item != null && String.Equals(item.Id, track.Id, StringComparison.OrdinalIgnoreCase); }) == null)
                                {
                                    settingsChanged = true;
                                    addedTrackCount++;
                                    if (!newTrackFiles.Contains(track.FileName)) newTrackFiles.Add(track.FileName);
                                    if (!filesBefore.Contains(track.FileName)) addedFiles.Add(track.FileName);
                                }
                            }
                            catch (Exception ex) { copyFailures.Add(Path.GetFileName(path) + "：" + ex.Message); }
                        }
                        if (settingsChanged)
                        {
                            string error;
                            if (!SettingsStore.TrySave(_settings, out error))
                            {
                                _settings.Tracks = previousAssets;
                                _settings.RestoreScene(previousScene);
                                _settings.Normalize();
                                foreach (string file in addedFiles)
                                    try { string copied = AppPaths.TrackPath(file); if (File.Exists(copied)) File.Delete(copied); } catch { }
                                failures.Add("保存曲库失败：" + error);
                                addedFiles.Clear();
                                newTrackFiles.Clear();
                                addedTrackCount = 0;
                            }
                        }
                        _capturingKey = false;
                        RefreshWebFiles();
                        if (newTrackFiles.Count > 0) QueueLoudnessAnalysis(newTrackFiles);
                        RebuildBindings();
                        string summary = addedTrackCount > 0 ? "已添加 " + addedTrackCount + " 首音频。" : "没有添加可播放的音频。";
                        if (failures.Count > 0 || copyFailures.Count > 0)
                        {
                            List<string> all = new List<string>(failures);
                            all.AddRange(copyFailures);
                            summary += "\n未添加 " + all.Count + " 个文件：" + String.Join("；", all.ToArray());
                        }
                        ShowLibraryNotice(summary, failures.Count > 0 || copyFailures.Count > 0);
                        PublishWebState();
                    })); }
                    catch (InvalidOperationException) { _capturingKey = false; }
                });
            }
        }

        private void DeleteSong(TrackDefinition track)
        {
            ScenePreset previousScene = _settings.CloneActiveScene();
            SongLibrary.Remove(_settings, track.Id);
            string error;
            if (!SettingsStore.TrySave(_settings, out error))
            {
                _settings.RestoreScene(previousScene);
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
            NotifyWeb(message);
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

        public void QueueHotkey(string keyName)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new MethodInvoker(delegate { if (!IsDisposed && !_capturingKey) PlayHotkey(keyName); })); }
            catch (InvalidOperationException) { }
        }

        public void PlayHotkey(string keyName)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new MethodInvoker(delegate { PlayHotkey(keyName); })); }
                catch { }
                return;
            }

            if (!String.IsNullOrWhiteSpace(_settings.ActiveRandomKey) && String.Equals(_settings.ActiveRandomKey, keyName, StringComparison.OrdinalIgnoreCase))
            {
                PlayRandom();
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
            trackBar.BackColor = UiTheme.Surface;
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
            panel.Controls.Add(new Label { Text = "歌曲", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiTheme.Muted, Padding = new Padding(10, 0, 0, 0) }, 0, 0);
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
            string volumeError;
            if (!SettingsStore.TrySave(_settings, out volumeError)) ShowLibraryNotice("音量保存失败：" + volumeError, true);
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
            string deviceError;
            if (!SettingsStore.TrySave(_settings, out deviceError)) ShowLibraryNotice("设备设置保存失败：" + deviceError, true);
            _engine.ReconfigureDevices();
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
            return sliderValue + "%";
        }

        public void SetHotkeyError(string message)
        {
            _hotkeyError = message ?? "";
            RefreshStatus();
        }

        private string _hotkeyError = "";

        private void EngineStatusChanged(object sender, EventArgs e)
        {
            MarkFailedRandomPlayback();
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
                PublishWebState();

            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SoundPackService.Cancel();
            if (!String.IsNullOrWhiteSpace(_pendingImportToken))
            {
                SoundPackService.CancelPrepared(_pendingImportToken);
                _pendingImportToken = "";
            }
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
            if (disposing)
            {
                _engine.StatusChanged -= EngineStatusChanged;
                if (_meterTimer != null) _meterTimer.Dispose();
                if (_stateAdapters != null) _stateAdapters.Dispose();
                if (_startupTimeout != null) _startupTimeout.Dispose();
                if (_web != null) _web.Dispose();
            }
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

