using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BgmHotkey
{
    internal sealed partial class MainMenuForm
    {
        private readonly RandomShuffleBag _shuffleBag = new RandomShuffleBag();
        private string _lastRandomSceneId = "";
        private string _lastRandomFileName = "";
        private bool _continuousRandomActive;
        private int _continuousRandomToken;
        private int _continuousExpectedSessionId;
        private int _continuousExpectedGeneration;
        private string _continuousSceneId = "";
        private string _continuousCurrentFile = "";
        private int _continuousSkippedCount;

        private void PlayRandom()
        {
            if (_continuousRandomActive)
            {
                CancelContinuousRandom();
                _engine.StopActiveTrack();
                NotifyWeb("已停止随机连播。");
                PublishWebState();
                return;
            }
            if (_engine.StopActiveTrack()) return;

            ScenePreset scene = _settings.ActiveScene;
            if (scene != null && RandomPlaybackMode.Normalize(scene.RandomMode) == RandomPlaybackMode.Continuous)
            {
                StartContinuousRandom();
                return;
            }
            PlaySingleRandom();
        }

        private List<string> AvailableRandomFiles()
        {
            List<string> available = new List<string>();
            foreach (string file in _settings.ActiveRandomFiles ?? new string[0])
                if (AppSettings.IsAudioFileName(file) && File.Exists(AppPaths.TrackPath(file))) available.Add(file);
            return available;
        }

        private void PlaySingleRandom()
        {
            string sceneId = _settings.ActiveSceneId;
            List<string> available = AvailableRandomFiles();
            string selected = _shuffleBag.Next(sceneId, available);
            if (selected == null)
            {
                NotifyWeb("当前场景没有可播放的随机候选，请在随机播放页选择音频文件。");
                return;
            }
            LoudnessAnalysis analysis = _settings.FindAnalysis(selected);
            double gain = analysis == null ? 0 : analysis.GainDb;
            _lastRandomSceneId = sceneId;
            _lastRandomFileName = selected;
            if (!_engine.TryStartFullTrack(new TrackDefinition("random_file:" + selected, Path.GetFileNameWithoutExtension(selected), selected, 0, null, gain)))
            {
                _shuffleBag.MarkFailed(sceneId, selected);
                _lastRandomFileName = "";
                NotifyWeb("无法启动这首随机候选，请检查音频文件或解码器。");
            }
        }

        private void StartContinuousRandom()
        {
            List<string> available = AvailableRandomFiles();
            if (available.Count == 0)
            {
                NotifyWeb("当前场景没有可播放的随机候选，请在随机播放页选择音频文件。");
                return;
            }
            _continuousRandomToken++;
            _continuousRandomActive = true;
            _continuousSceneId = _settings.ActiveSceneId;
            _continuousCurrentFile = "";
            _continuousSkippedCount = 0;
            _continuousExpectedSessionId = 0;
            _continuousExpectedGeneration = 0;
            _shuffleBag.Refresh(_continuousSceneId);
            StartNextContinuousTrack(_continuousRandomToken);
            PublishWebState();
        }

        private void StartNextContinuousTrack(int runToken)
        {
            if (!_continuousRandomActive || runToken != _continuousRandomToken ||
                !String.Equals(_settings.ActiveSceneId, _continuousSceneId, StringComparison.OrdinalIgnoreCase)) return;

            List<string> available = AvailableRandomFiles();
            int attempts = available.Count;
            while (attempts-- > 0)
            {
                string selected = _shuffleBag.Next(_continuousSceneId, available);
                if (String.IsNullOrWhiteSpace(selected)) break;
                if (!File.Exists(AppPaths.TrackPath(selected)))
                {
                    _shuffleBag.MarkFailed(_continuousSceneId, selected);
                    _continuousSkippedCount++;
                    continue;
                }
                LoudnessAnalysis analysis = _settings.FindAnalysis(selected);
                double gain = analysis == null ? 0 : analysis.GainDb;
                TrackDefinition candidate = new TrackDefinition("random_file:" + selected, Path.GetFileNameWithoutExtension(selected), selected, 0, null, gain);
                _lastRandomSceneId = _continuousSceneId;
                _lastRandomFileName = selected;
                if (!_engine.TryStartFullTrack(candidate))
                {
                    _shuffleBag.MarkFailed(_continuousSceneId, selected);
                    _continuousSkippedCount++;
                    _lastRandomFileName = "";
                    continue;
                }
                PlaybackSnapshot playback = _engine.GetPlaybackSnapshot();
                _continuousCurrentFile = selected;
                _continuousExpectedSessionId = playback.SessionId;
                _continuousExpectedGeneration = playback.Generation;
                PublishWebState();
                if (_continuousSkippedCount > 0)
                {
                    NotifyWeb("已跳过 " + _continuousSkippedCount + " 首无法读取或启动的随机候选，继续连播。");
                    _continuousSkippedCount = 0;
                }
                return;
            }

            bool allCandidatesFailed = available.Count > 0;
            CancelContinuousRandom();
            NotifyWeb(allCandidatesFailed
                ? "当前场景的随机候选均无法播放，已结束连播。"
                : "当前场景没有可播放的随机候选，已结束连播。");
            PublishWebState();
        }

        private void CancelContinuousRandom()
        {
            _continuousRandomToken++;
            _continuousRandomActive = false;
            _continuousExpectedSessionId = 0;
            _continuousExpectedGeneration = 0;
            _continuousSceneId = "";
            _continuousCurrentFile = "";
        }

        private void EnginePlaybackCompleted(object sender, PlaybackCompletedEventArgs e)
        {
            if (e == null || IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new MethodInvoker(delegate { HandleContinuousPlaybackCompleted(e); }));
            }
            catch (InvalidOperationException) { }
        }

        private void HandleContinuousPlaybackCompleted(PlaybackCompletedEventArgs e)
        {
            if (IsDisposed || !_continuousRandomActive || e == null || e.PlaybackMode == "preview" ||
                e.SessionId != _continuousExpectedSessionId || e.Generation != _continuousExpectedGeneration ||
                !String.Equals(e.TrackId, "random_file:" + _continuousCurrentFile, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(_settings.ActiveSceneId, _continuousSceneId, StringComparison.OrdinalIgnoreCase)) return;

            PlaybackSnapshot current = _engine.GetPlaybackSnapshot();
            if (current.SessionId != e.SessionId || current.Generation != e.Generation || current.State != "stopped") return;
            string completedFile = _continuousCurrentFile;
            if (e.Reason == PlaybackEndReason.Failed)
            {
                _shuffleBag.MarkFailed(_continuousSceneId, completedFile);
                _continuousSkippedCount++;
                AppPaths.Log("随机连播跳过无法解码的候选文件：" + completedFile);
            }
            _continuousCurrentFile = "";
            StartNextContinuousTrack(_continuousRandomToken);
        }

        private void MarkFailedRandomPlayback()
        {
            PlaybackSnapshot snapshot = _engine.GetPlaybackSnapshot();
            if (_continuousRandomActive)
            {
                if (snapshot.SessionId != _continuousExpectedSessionId && !String.IsNullOrWhiteSpace(snapshot.Error))
                {
                    CancelContinuousRandom();
                    NotifyWeb("当前连播会话已因播放或跳转错误结束。");
                    PublishWebState();
                }
                return;
            }
            if (!String.IsNullOrWhiteSpace(snapshot.Error) && !String.IsNullOrWhiteSpace(_lastRandomFileName) &&
                String.Equals(snapshot.TrackId, "random_file:" + _lastRandomFileName, StringComparison.OrdinalIgnoreCase))
            {
                string failedFile = _lastRandomFileName;
                _shuffleBag.MarkFailed(_lastRandomSceneId, _lastRandomFileName);
                _lastRandomFileName = "";
                NotifyWeb("随机候选“" + Path.GetFileNameWithoutExtension(failedFile) + "”无法播放，已停止。请检查文件或系统解码器。");
            }
        }

        private void OpenRandomSettings(object sender, EventArgs e)
        {
            SetKeyCapture("winforms-dialog", true);
            try
            {
                using (RandomPlaybackForm dialog = new RandomPlaybackForm(_settings))
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                        {
                        ShowLibraryNotice("随机播放已保存。按快捷键，或点“随机播放”。", false);
                        RefreshStatus();
                    }
            }
            finally { SetKeyCapture("winforms-dialog", false); ActiveControl = null; }
        }
    }

    internal sealed class RandomPlaybackForm : Form
    {
        private readonly AppSettings _settings;
        private readonly CheckedListBox _songs;
        private readonly TextBox _key;
        private readonly Label _notice;
        private string _keyName;

        public RandomPlaybackForm(AppSettings settings)
        {
            SuspendLayout();
            _settings = settings;
            _keyName = settings.ActiveRandomKey ?? "";
            Text = "随机播放设置";
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Font = new Font("Microsoft YaHei UI", 10F);
            BackColor = UiTheme.Background;
            ForeColor = UiTheme.Text;
            ClientSize = new Size(560, 580);
            MinimumSize = new Size(460, 480);
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new int[] { 48, 56, 48, 0, 66, 48 })
                layout.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
            Controls.Add(layout);
            layout.Controls.Add(UiTheme.Label("随机挑一首，交给快捷键", 16, true), 0, 0);
            TableLayoutPanel binding = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
            binding.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            binding.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            binding.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            binding.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            binding.Controls.Add(UiTheme.Label("随机快捷键", 10, true), 0, 0);
            _key = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, TextAlign = HorizontalAlignment.Center, BackColor = UiTheme.Tint, ForeColor = UiTheme.Accent, Margin = new Padding(8, 12, 8, 8) };
            UpdateKeyText();
            _key.PreviewKeyDown += delegate(object o, PreviewKeyDownEventArgs args) { args.IsInputKey = true; };
            _key.KeyDown += CaptureKey;
            binding.Controls.Add(_key, 1, 0);
            Button clear = UiTheme.Button("清除", false); clear.Dock = DockStyle.Fill; clear.Margin = new Padding(4, 6, 0, 6);
            clear.Click += delegate { _keyName = ""; UpdateKeyText(); _notice.Text = "保存后将禁用随机快捷键。"; };
            binding.Controls.Add(clear, 2, 0);
            layout.Controls.Add(binding, 0, 1);
            layout.Controls.Add(UiTheme.Label("勾选参与随机的歌曲（可多选）", 10, true), 0, 2);
            _songs = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle, HorizontalScrollbar = true, BackColor = UiTheme.Surface, ForeColor = UiTheme.Text, Margin = new Padding(0, 0, 0, 8) };
            HashSet<string> selected = new HashSet<string>(settings.ActiveRandomFiles ?? new string[0], StringComparer.OrdinalIgnoreCase);
            string scanError = "";
            try
            {
                string folder = AppPaths.TrackPath("");
                if (Directory.Exists(folder))
                {
                    string[] paths = Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly);
                    Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
                    foreach (string path in paths)
                    {
                        string file = Path.GetFileName(path);
                        if (!AppSettings.IsAudioFileName(file)) continue;
                        TrackDefinition track = new TrackDefinition("random_file:" + file, Path.GetFileNameWithoutExtension(file), file, 0);
                        _songs.Items.Add(new SongChoice(track), selected.Contains(file));
                    }
                }
            }
            catch (Exception ex) { scanError = "读取 bgm 文件夹失败：" + ex.Message; }
            layout.Controls.Add(_songs, 0, 3);
            _notice = UiTheme.Label(scanError.Length > 0 ? scanError : (_songs.Items.Count == 0 ? "bgm 文件夹中没有音频候选文件。" : "已扫描 bgm 文件夹中的音频候选文件；随机播放时仍会检查解码。\n无需单曲快捷键，播放中按随机键或任意曲目键即可停止。"), 9, false);
            _notice.ForeColor = UiTheme.Muted;
            layout.Controls.Add(_notice, 0, 4);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
            Button save = UiTheme.Button("保存设置", true); save.Size = new Size(120, 40); save.Click += SaveSettings;
            Button cancel = UiTheme.Button("取消", false); cancel.Size = new Size(90, 40); cancel.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(save); actions.Controls.Add(cancel); layout.Controls.Add(actions, 0, 5);
            CancelButton = cancel;
            AutoScaleDimensions = new SizeF(96, 96);
            ResumeLayout(true);
            Shown += delegate
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width - 16), Math.Min(MinimumSize.Height, area.Height - 16));
                Size = new Size(Math.Min(Width, area.Width - 16), Math.Min(Height, area.Height - 16));
                Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            };
        }

        private void UpdateKeyText() { _key.Text = String.IsNullOrWhiteSpace(_keyName) ? "点击设置" : _keyName; }
        private void CaptureKey(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true; e.Handled = true;
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.Menu)
            { _notice.Text = "请按一个字母、数字或功能键；清除绑定请点“清除”。"; return; }
            string keyName = AppSettings.NormalizeKey(e.KeyCode.ToString());
            if (keyName.Length == 0) { _notice.Text = "请按字母、数字或功能键。"; return; }
            foreach (TrackDefinition track in TrackCatalog.All)
                if (String.Equals(_settings.GetKey(track.Id), keyName, StringComparison.OrdinalIgnoreCase))
                { _notice.Text = "这个按键已绑定给“" + track.Name + "”，请选择其他按键。"; return; }
            _keyName = keyName; UpdateKeyText(); _notice.Text = "快捷键已选定，点击保存设置生效。";
        }
        private void SaveSettings(object sender, EventArgs e)
        {
            List<string> ids = new List<string>();
            foreach (SongChoice choice in _songs.CheckedItems) ids.Add(choice.Track.FileName);
            if (!String.IsNullOrWhiteSpace(_keyName) && ids.Count == 0)
            { _notice.Text = "请至少勾选一首歌曲，或清除随机快捷键。"; return; }
            string oldKey = _settings.ActiveRandomKey; string[] oldIds = _settings.ActiveRandomFiles;
            _settings.ActiveRandomKey = _keyName; _settings.ActiveRandomFiles = ids.ToArray();
            string error;
            if (!SettingsStore.TrySave(_settings, out error))
            { _settings.ActiveRandomKey = oldKey; _settings.ActiveRandomFiles = oldIds; _notice.Text = "保存失败：" + error; return; }
            DialogResult = DialogResult.OK; Close();
        }
        private sealed class SongChoice
        {
            public readonly TrackDefinition Track;
            public SongChoice(TrackDefinition track) { Track = track; }
            public override string ToString() { return Track.FileName; }
        }
    }
}
