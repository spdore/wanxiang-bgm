using System;
using System.IO;
using System.Threading;

namespace BgmHotkey
{
    internal struct AudioPlaybackRange
    {
        public double StartSeconds;
        public double? EndSeconds;
    }

    internal sealed class PlaybackSnapshot
    {
        public int SessionId;
        public int Generation;
        public string TrackId;
        public string TrackName;
        public string FileName;
        public string State;
        public double PositionSeconds;
        public double DurationSeconds;
        public double StartSeconds;
        public double? EndSeconds;
        public string Error;
        public bool CanSeek;
    }

    internal sealed class AudioEngine : IDisposable
    {
        private const double MaximumMusicGain = 0.30;
        private const int SpectrumFftSize = 2048;
        private const int SpectrumBandCount = 32;
        private readonly object _stateGate = new object();
        private readonly object _deviceGate = new object();
        private readonly AppSettings _settings;
        private readonly AudioDeviceCatalog _catalog;
        private volatile bool _running;
        private Thread _audioThread;
        private WaveInCapture _microphone;
        private WaveOutSink _cable;
        private WaveOutSink _headphones;
        private string _deviceStatus = "";
        private string _playbackStatus = "就绪";
        private string _playingName = "";
        private string _playingTrackId = "";
        private bool _playing;
        private bool _loading;
        private bool _seeking;
        private bool _directHeadphonePlayback;
        private double _currentTrackGain = 1.0;
        private readonly FinalMixLimiter _finalMixLimiter = new FinalMixLimiter();
        private Mp3StreamPlayer _streamPlayer;
        private DirectMp3Player _directPlayer;
        private string _playingFileName = "";
        private string _lastTrackId = "";
        private string _lastTrackName = "";
        private string _lastFileName = "";
        private int _nextSessionId;
        private int _sessionId;
        private int _seekGeneration;
        private int _resetOutputPending;
        private double _lastPositionSeconds;
        private double _lastDurationSeconds;
        private double _segmentStartSeconds;
        private double? _segmentEndSeconds;
        private string _playbackError = "";
        private int _headphoneVolume;
        private int _cableVolume;
        private bool _timerResolutionActive;
        private bool _disposed;
#if TEST_BUILD
        private bool _suppressHardwareOutput;
#endif
        private volatile int _microphonePeak;
        private volatile int _musicPeak;
        private volatile int _headphoneMusicPeak;
        private volatile int _outputPeak;
        private readonly short[] _spectrumRing = new short[SpectrumFftSize];
        private readonly double[] _spectrumReal = new double[SpectrumFftSize];
        private readonly double[] _spectrumImaginary = new double[SpectrumFftSize];
        private readonly int[] _spectrumSmoothed = new int[SpectrumBandCount];
        private volatile int[] _musicSpectrum = new int[SpectrumBandCount];
        private int _spectrumWrite;
        private int _spectrumSamples;
        private int _spectrumUpdateCounter;

        public int MicrophonePeak { get { return _microphonePeak; } }
        public int MusicPeak { get { return _musicPeak; } }
        public int HeadphoneMusicPeak { get { return _headphoneMusicPeak; } }
        public int OutputPeak { get { return _outputPeak; } }
        public int[] MusicSpectrum { get { return (int[])_musicSpectrum.Clone(); } }

        public event EventHandler StatusChanged;
        public string PlaybackSummary
        {
            get { lock (_stateGate) return _playing ? _playingName : (_loading ? (_seeking ? "正在跳转…" : "准备播放…") : "等待播放"); }
        }

        public bool IsTrackPlaying(string id)
        {
            lock (_stateGate) return _playing && String.Equals(_playingTrackId, id, StringComparison.OrdinalIgnoreCase);
        }

        public AudioEngine(AppSettings settings, AudioDeviceCatalog catalog)
        {
            _settings = settings;
            _catalog = catalog;
            _headphoneVolume = settings.HeadphoneVolume;
            _cableVolume = settings.CableVolume;
        }

#if TEST_BUILD
        internal AudioEngine(AppSettings settings, AudioDeviceCatalog catalog, bool suppressHardwareOutput)
            : this(settings, catalog)
        {
            _suppressHardwareOutput = suppressHardwareOutput;
        }
#endif

        internal static AudioPlaybackRange ResolvePlaybackRange(TrackDefinition track, bool useShortcutRange)
        {
            if (track == null) throw new ArgumentNullException("track");
            return new AudioPlaybackRange
            {
                StartSeconds = useShortcutRange ? track.StartSeconds : 0,
                EndSeconds = useShortcutRange ? track.EndSeconds : null
            };
        }

        public string StatusText
        {
            get
            {
                lock (_stateGate)
                    return _playbackStatus + Environment.NewLine + _deviceStatus;
            }
        }

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException("AudioEngine");
            if (_running) return;
            _timerResolutionActive = WinMm.timeBeginPeriod(1) == 0;
            ReconfigureDevices();
            _running = true;
            _audioThread = new Thread(AudioLoop);
            _audioThread.IsBackground = true;
            _audioThread.Name = "BGM audio mixer";
            _audioThread.Start();
        }

        public void SetVolumes(int headphoneVolume, int cableVolume)
        {
            DirectMp3Player player;
            lock (_stateGate)
            {
                _headphoneVolume = Clamp(headphoneVolume, 0, 100);
                _cableVolume = Clamp(cableVolume, 0, 100);
                player = _directPlayer;
            }
            if (player != null)
                player.SetSliderVolume(headphoneVolume);
        }

        public void ReconfigureDevices()
        {
            lock (_deviceGate)
            {
                CloseDevices();
                AudioDevice microphone = _catalog.FindMicrophone(_settings.MicrophoneDeviceName);
                AudioDevice cable = _catalog.FindCable(_settings.CableDeviceName);
                if (String.IsNullOrWhiteSpace(_settings.CableDeviceName))
                    cable = _catalog.FindCableByDefaultName();

                string microphoneStatus;
                string cableStatus;

                if (microphone != null)
                {
                    try
                    {
                        _microphone = new WaveInCapture(microphone.Id, microphone.Name);
                        microphoneStatus = "麦克风：已连接（" + microphone.Name + "）";
                    }
                    catch (Exception ex)
                    {
                        microphoneStatus = "麦克风：无法打开（" + ex.Message + "）";
                        AppPaths.Log(microphoneStatus);
                    }
                }
                else
                {
                    microphoneStatus = "麦克风：所选设备不可用，请重新选择（" + _settings.MicrophoneDeviceName + "）";
                }

                if (cable != null)
                {
                    try
                    {
                        _cable = new WaveOutSink(cable.Id, cable.Name);
                        AudioDevice recording = _catalog.FindCableRecording(cable);
                        cableStatus = "虚拟通道写入端：" + cable.Name + Environment.NewLine +
                            "聊天软件输入端：" + (recording == null ? "未找到对应录音端，请检查驱动" : recording.Name);
                    }
                    catch (Exception ex)
                    {
                        cableStatus = "虚拟麦克风：无法打开（" + ex.Message + "）";
                        AppPaths.Log(cableStatus);
                    }
                }
                else
                {
                    cableStatus = "虚拟麦克风：未检测到 VB-CABLE，请安装驱动并重启电脑";
                }

                lock (_stateGate)
                    _deviceStatus = microphoneStatus + Environment.NewLine + "耳机输出：Windows 默认播放设备（MP3 原生 MCI 可用时使用，否则走同步 PCM）" + Environment.NewLine + cableStatus +
                        Environment.NewLine + "游戏语音输入请选择：CABLE Output（VB-Audio Virtual Cable）";
                AppPaths.Log("音频路由：" + _deviceStatus.Replace(Environment.NewLine, " | "));
            }
            NotifyStatus();
        }

        public bool StopActiveTrack()
        {
            string id;
            lock (_stateGate)
            {
                if (!_playing && !_loading) return false;
                id = _playingTrackId;
            }
            StopTrack(id);
            return true;
        }

        public void ToggleTrack(TrackDefinition track)
        {
            ToggleTrack(track, true);
        }

        public void ToggleTrack(TrackDefinition track, bool useShortcutRange)
        {
            if (track == null) return;
            bool stopCurrent;
            lock (_stateGate)
            { stopCurrent = _playing || _loading; }
            if (stopCurrent) { StopActiveTrack(); return; }

            AudioPlaybackRange playbackRange = ResolvePlaybackRange(track, useShortcutRange);
            double startSeconds = playbackRange.StartSeconds;
            double? endSeconds = playbackRange.EndSeconds;
            bool isMp3 = track.FileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
            if (!isMp3 || startSeconds > 0 || endSeconds.HasValue)
            {
                try { EnsureHeadphoneSink(); }
                catch (Exception ex) { SetPlaybackError("无法打开耳机输出：" + ex.Message); return; }
            }

            string filePath = AppPaths.TrackPath(track.FileName);
            Mp3StreamPlayer playerToStart = new Mp3StreamPlayer(filePath, startSeconds, endSeconds);
            int sessionId;
            lock (_stateGate)
            {
                if (_playing || _loading) { playerToStart.Dispose(); return; }
                _streamPlayer = playerToStart;
                _directPlayer = null;
                _playingName = track.Name;
                _playingTrackId = track.Id;
                _playingFileName = track.FileName;
                _lastTrackId = track.Id;
                _lastTrackName = track.Name;
                _lastFileName = track.FileName;
                _currentTrackGain = Math.Max(0, Math.Min(Math.Pow(10.0, LoudnessAnalyzer.MaximumBoostDb / 20.0), track.GainLinear));
                _sessionId = ++_nextSessionId;
                _seekGeneration++;
                sessionId = _sessionId;
                _lastPositionSeconds = startSeconds;
                _lastDurationSeconds = 0;
                _segmentStartSeconds = startSeconds;
                _segmentEndSeconds = endSeconds;
                _playbackError = "";
                _directHeadphonePlayback = false;
                _loading = true;
                _seeking = false;
                _playing = false;
                _playbackStatus = "正在启动：" + track.Name;
            }
            NotifyStatus();
            try
            {
                playerToStart.Start();
                ThreadPool.QueueUserWorkItem(delegate { CompleteTrackStart(playerToStart, track, sessionId, isMp3, startSeconds, endSeconds); });
            }
            catch (Exception ex)
            {
                playerToStart.Dispose();
                lock (_stateGate)
                {
                    if (_streamPlayer == playerToStart)
                    {
                        _streamPlayer = null;
                        _loading = false;
                        _playing = false;
                        _sessionId = ++_nextSessionId;
                        _playbackError = "启动失败：" + ex.Message;
                        _playbackStatus = _playbackError;
                    }
                }
            }
            NotifyStatus();
        }

        private void EnsureHeadphoneSink()
        {
#if TEST_BUILD
            if (_suppressHardwareOutput) return;
#endif
            lock (_deviceGate)
                if (_headphones == null) _headphones = new WaveOutSink(WinMm.Mapper, "Windows 默认播放设备");
        }

        private void CompleteTrackStart(Mp3StreamPlayer player, TrackDefinition track, int sessionId, bool isMp3, double startSeconds, double? endSeconds)
        {
            DirectMp3Player direct = null;
            try
            {
                if (!player.WaitUntilReady(20000)) throw new TimeoutException("读取音频格式和时长超时。");
                if (player.Error != null) throw player.Error;
                double duration = player.DurationSeconds;
                if (startSeconds < 0 || startSeconds >= duration)
                    throw new InvalidOperationException("开始锚点必须位于歌曲时长以内。");
                if (endSeconds.HasValue && (endSeconds.Value <= startSeconds || endSeconds.Value > duration))
                    throw new InvalidOperationException("结束锚点必须大于开始锚点且不能超过歌曲时长。");
                int volume;
                lock (_stateGate) volume = _headphoneVolume;
                bool directHeadphonePlayback = false;
                if (isMp3 && startSeconds <= 0 && !endSeconds.HasValue)
                {
                    try
                    {
                        direct = new DirectMp3Player(AppPaths.TrackPath(track.FileName), startSeconds, endSeconds, volume, track.GainLinear);
                        directHeadphonePlayback = true;
                    }
                    catch (Exception ex)
                    {
                        // MCI's legacy MPEG driver is missing on some Windows installs. Keep
                        // MP3 playback working by sending the same streamed PCM to both routes.
                        AppPaths.Log("MCI MP3 耳机播放不可用，回退到同步 PCM 输出：" + ex.Message);
                        EnsureHeadphoneSink();
                    }
                }
                lock (_stateGate)
                {
                    if (_streamPlayer != player || _sessionId != sessionId || !_loading)
                    {
                        if (direct != null) direct.Dispose();
                        return;
                    }
                    _directPlayer = direct;
                    _directHeadphonePlayback = directHeadphonePlayback;
                    _lastDurationSeconds = duration;
                    _loading = false;
                    _seeking = false;
                    _playing = true;
                    _playbackError = "";
                    _playbackStatus = isMp3
                        ? "正在播放：" + track.Name + (directHeadphonePlayback ? "（耳机原生 MP3；CABLE 独立混音）" : "（MP3 同步流式输出到耳机与 CABLE）")
                        : "正在播放：" + track.Name + "（流式音频；耳机与 CABLE 独立音量）";
                    Interlocked.Exchange(ref _resetOutputPending, 1);
                }
            }
            catch (Exception ex)
            {
                if (direct != null) direct.Dispose();
                player.Cancel();
                lock (_stateGate)
                {
                    if (_streamPlayer == player && _sessionId == sessionId)
                    {
                        _streamPlayer = null;
                        _directPlayer = null;
                        _loading = false;
                        _seeking = false;
                        _playing = false;
                        _directHeadphonePlayback = false;
                        _playbackError = "启动失败：" + ex.Message;
                        _playbackStatus = _playbackError;
                    }
                }
                ThreadPool.QueueUserWorkItem(delegate { player.Dispose(); });
            }
            NotifyStatus();
        }

        public void StopTrack(string id)
        {
            Mp3StreamPlayer stream;
            DirectMp3Player direct;
            lock (_stateGate)
            {
                if (!String.Equals(_playingTrackId, id, StringComparison.OrdinalIgnoreCase)) return;
                stream = _streamPlayer;
                direct = _directPlayer;
                if (stream != null) _lastPositionSeconds = stream.PositionSeconds;
                else if (direct != null) _lastPositionSeconds = direct.PositionSeconds;
                _lastTrackId = _playingTrackId;
                _lastTrackName = _playingName;
                _lastFileName = _playingFileName;
                _streamPlayer = null;
                _directPlayer = null;
                _playing = false;
                _loading = false;
                _seeking = false;
                _playingTrackId = "";
                _playingFileName = "";
                _directHeadphonePlayback = false;
                _currentTrackGain = 1.0;
                _sessionId = ++_nextSessionId;
                _seekGeneration++;
                _playbackStatus = "已停止";
                Interlocked.Exchange(ref _resetOutputPending, 1);
            }
            if (stream != null) { stream.Cancel(); ThreadPool.QueueUserWorkItem(delegate { stream.Dispose(); }); }
            if (direct != null) direct.Dispose();
            NotifyStatus();
        }

        public PlaybackSnapshot GetPlaybackSnapshot()
        {
            Mp3StreamPlayer stream;
            DirectMp3Player direct;
            PlaybackSnapshot snapshot = new PlaybackSnapshot();
            lock (_stateGate)
            {
                snapshot.SessionId = _sessionId;
                snapshot.Generation = _seekGeneration;
                snapshot.TrackId = _playing ? _playingTrackId : _lastTrackId;
                snapshot.TrackName = _playing ? _playingName : _lastTrackName;
                snapshot.FileName = _playing ? _playingFileName : _lastFileName;
                snapshot.State = _loading ? (_seeking ? "seeking" : "loading") : (_playing ? "playing" : "stopped");
                snapshot.PositionSeconds = _lastPositionSeconds;
                snapshot.DurationSeconds = _lastDurationSeconds;
                snapshot.StartSeconds = _segmentStartSeconds;
                snapshot.EndSeconds = _segmentEndSeconds;
                snapshot.Error = _playbackError;
                snapshot.CanSeek = _playing && _lastDurationSeconds > 0;
                stream = _streamPlayer;
                direct = _directPlayer;
            }
            if (stream != null && snapshot.State != "stopped")
            {
                snapshot.DurationSeconds = Math.Max(snapshot.DurationSeconds, stream.DurationSeconds);
                if (direct != null) snapshot.PositionSeconds = direct.PositionSeconds;
                else snapshot.PositionSeconds = stream.PositionSeconds;
                if (snapshot.DurationSeconds > 0)
                    snapshot.PositionSeconds = Math.Max(0, Math.Min(snapshot.DurationSeconds, snapshot.PositionSeconds));
                lock (_stateGate)
                    if (_sessionId == snapshot.SessionId) _lastPositionSeconds = snapshot.PositionSeconds;
            }
            snapshot.CanSeek = snapshot.State == "playing" && snapshot.DurationSeconds > 0;
            return snapshot;
        }

        public bool Seek(int sessionId, int expectedGeneration, double positionSeconds)
        {
            if (Double.IsNaN(positionSeconds) || Double.IsInfinity(positionSeconds) || positionSeconds < 0) return false;
            Mp3StreamPlayer oldPlayer;
            Mp3StreamPlayer newPlayer;
            DirectMp3Player direct;
            double target;
            int generation;
            lock (_stateGate)
            {
                if ((!_playing && !_loading) || sessionId != _sessionId || expectedGeneration != _seekGeneration || _streamPlayer == null || _lastDurationSeconds <= 0) return false;
                double limit = _lastDurationSeconds;
                if (positionSeconds >= limit)
                {
                    target = limit;
                    oldPlayer = null;
                    newPlayer = null;
                    direct = null;
                    generation = 0;
                }
                else
                {
                    target = Math.Min(limit, positionSeconds);
                    oldPlayer = _streamPlayer;
                    direct = _directPlayer;
                    newPlayer = new Mp3StreamPlayer(AppPaths.TrackPath(_playingFileName), target, null);
                    // A manual seek is an explicit request to browse the whole song. Drop
                    // only this session's effective range; saved shortcut anchors stay intact.
                    _segmentStartSeconds = 0;
                    _segmentEndSeconds = null;
                    _streamPlayer = newPlayer;
                    _loading = true;
                    _seeking = true;
                    _playing = false;
                    _lastPositionSeconds = target;
                    generation = ++_seekGeneration;
                    _playbackError = "";
                    _playbackStatus = "正在跳转…";
                    Interlocked.Exchange(ref _resetOutputPending, 1);
                }
            }
            if (newPlayer == null)
            {
                StopActiveTrack();
                return true;
            }
            oldPlayer.Cancel();
            ThreadPool.QueueUserWorkItem(delegate { oldPlayer.Dispose(); });
            try
            {
                if (direct != null)
                {
                    lock (_stateGate)
                    {
                        if (_sessionId == sessionId && _seekGeneration == generation && _directPlayer == direct)
                        {
                            _directPlayer = null;
                            _directHeadphonePlayback = false;
                        }
                    }
                    direct.Dispose();
                    direct = null;
                    EnsureHeadphoneSink();
                }
                newPlayer.Start();
                ThreadPool.QueueUserWorkItem(delegate { CompleteSeek(newPlayer, direct, target, sessionId, generation); });
                NotifyStatus();
                return true;
            }
            catch (Exception ex)
            {
                newPlayer.Cancel();
                bool ownsSession;
                string activeId;
                lock (_stateGate)
                {
                    ownsSession = _sessionId == sessionId && _seekGeneration == generation && _streamPlayer == newPlayer;
                    activeId = ownsSession ? _playingTrackId : "";
                }
                if (ownsSession)
                {
                    StopTrack(activeId);
                    SetPlaybackError("跳转失败：" + ex.Message);
                }
                ThreadPool.QueueUserWorkItem(delegate { newPlayer.Dispose(); });
                return false;
            }
        }

        private void CompleteSeek(Mp3StreamPlayer player, DirectMp3Player direct, double target, int sessionId, int generation)
        {
            try
            {
                if (!player.WaitUntilReady(20000)) throw new TimeoutException("跳转超时。");
                if (player.Error != null) throw player.Error;
                if (target >= player.DurationSeconds) throw new InvalidOperationException("跳转位置超过音频时长。");
                bool applied;
                lock (_stateGate)
                {
                    applied = _streamPlayer == player && _sessionId == sessionId && _seekGeneration == generation && _loading;
                    if (applied)
                    {
                        if (direct != null) direct.Seek(target);
                        _lastDurationSeconds = player.DurationSeconds;
                        _lastPositionSeconds = target;
                        _loading = false;
                        _seeking = false;
                        _playing = true;
                        _playbackStatus = "正在播放：" + _playingName;
                    }
                }
                if (!applied)
                {
                    player.Cancel();
                    ThreadPool.QueueUserWorkItem(delegate { player.Dispose(); });
                }
            }
            catch (Exception ex)
            {
                bool ownsSession;
                lock (_stateGate)
                {
                    ownsSession = _streamPlayer == player && _sessionId == sessionId && _seekGeneration == generation;
                    if (ownsSession)
                    {
                        _streamPlayer = null;
                        _directPlayer = null;
                        _loading = false;
                        _seeking = false;
                        _playing = false;
                        _lastPositionSeconds = target;
                        _playbackError = "跳转失败：" + ex.Message;
                        _playbackStatus = _playbackError;
                        _sessionId = ++_nextSessionId;
                        Interlocked.Exchange(ref _resetOutputPending, 1);
                    }
                }
                if (ownsSession)
                {
                    if (direct != null) direct.Dispose();
                    player.Cancel();
                    ThreadPool.QueueUserWorkItem(delegate { player.Dispose(); });
                }
                else
                {
                    player.Cancel();
                    ThreadPool.QueueUserWorkItem(delegate { player.Dispose(); });
                }
            }
            NotifyStatus();
        }

        private void SetPlaybackError(string message)
        {
            lock (_stateGate) { _playbackError = message; _playbackStatus = message; }
            NotifyStatus();
        }

        private void AudioLoop()
        {
            short[] music = new short[WinMm.BlockFrames * 2];
            short[] microphone = new short[WinMm.BlockFrames * 2];
            short[] cableSamples = new short[WinMm.BlockFrames * 2];

            while (_running)
            {
                bool outputPaced = false;
                int musicSessionId;
                int musicGeneration;
                double activeTrackGain = 1.0;
                try { ReadMusic(music, out musicSessionId, out musicGeneration); }
                catch (Exception ex)
                {
                    Array.Clear(music, 0, music.Length);
                    musicSessionId = 0;
                    musicGeneration = 0;
                    AppPaths.Log("音乐混音读取失败：" + ex);
                    StopActiveTrack();
                    SetDeviceError("音乐混音读取失败：" + ex.Message);
                }
                if (musicSessionId != 0)
                {
                    lock (_stateGate)
                    {
                        if (_playing && _sessionId == musicSessionId && _seekGeneration == musicGeneration)
                            activeTrackGain = _currentTrackGain;
                        else
                            Array.Clear(music, 0, music.Length);
                    }
                }
                lock (_deviceGate)
                {
                    if (musicSessionId != 0)
                    {
                        lock (_stateGate)
                            if (!_playing || _sessionId != musicSessionId || _seekGeneration != musicGeneration)
                                Array.Clear(music, 0, music.Length);
                    }
                    if (Interlocked.Exchange(ref _resetOutputPending, 0) != 0)
                    {
                        try { if (_cable != null) _cable.ResetPlayback(); } catch (Exception ex) { AppPaths.Log("重置 CABLE 播放缓冲失败：" + ex.Message); }
                        try { if (_headphones != null) _headphones.ResetPlayback(); } catch (Exception ex) { AppPaths.Log("重置耳机播放缓冲失败：" + ex.Message); }
                    }
                    if (_microphone != null)
                    {
                        try { _microphone.ReadStereo(microphone, WinMm.BlockFrames); }
                        catch (Exception ex)
                        {
                            Array.Clear(microphone, 0, microphone.Length);
                            AppPaths.Log("读取麦克风失败：" + ex.Message);
                            try { _microphone.Dispose(); } catch { }
                            _microphone = null;
                            SetDeviceError("麦克风采集已停止，请刷新设备：" + ex.Message);
                        }
                    }
                    else
                    {
                        Array.Clear(microphone, 0, microphone.Length);
                    }

                    int cableVolume;
                    int headphoneVolume;
                    bool streamToHeadphones;
                    lock (_stateGate)
                    {
                        cableVolume = _cableVolume;
                        headphoneVolume = _headphoneVolume;
                        streamToHeadphones = _playing && !_directHeadphonePlayback;
                    }

                    UpdateMusicSpectrum(music, Math.Max(cableVolume, headphoneVolume) / 100.0 * MaximumMusicGain * activeTrackGain);

                    // Expose the music source peak after each route's configured gain.
                    // This observes the decoded stream without changing either output route.
                    int sourceMusicPeak = Peak(music);
                    _headphoneMusicPeak = Clamp((int)Math.Round(sourceMusicPeak * headphoneVolume / 100.0 * MaximumMusicGain * activeTrackGain), 0, Int16.MaxValue);

                    if (_cable != null)
                    {
                        int cableMusicPeak;
                        _finalMixLimiter.Mix(music, microphone, cableSamples, cableVolume, activeTrackGain, out cableMusicPeak);
                        _microphonePeak = Peak(microphone);
                        _musicPeak = cableMusicPeak;
                        try
                        {
                            _cable.Write(cableSamples);
                            _outputPeak = Peak(cableSamples);
                            outputPaced = true;
                        }
                        catch (Exception ex)
                        {
                            AppPaths.Log("VB-CABLE 输出停止：" + ex);
                            try { _cable.Dispose(); } catch { }
                            _cable = null;
                            _outputPeak = 0;
                            _musicPeak = 0;
                            SetDeviceError("VB-CABLE 输出发生错误，已停止虚拟麦克风混音。");
                        }
                    }
                    else
                    {
                        _microphonePeak = Peak(microphone);
                        _musicPeak = 0;
                        _outputPeak = 0;
                    }

                    if (_headphones != null && streamToHeadphones)
                    {
                        try
                        {
                            ScaleHeadphoneMusic(music, headphoneVolume, activeTrackGain);
                            _headphones.Write(music);
                            outputPaced = true;
                        }
                        catch (Exception ex)
                        {
                            AppPaths.Log("耳机音频输出停止：" + ex);
                            try { _headphones.Dispose(); } catch { }
                            _headphones = null;
                            StopActiveTrack();
                            Array.Clear(music, 0, music.Length);
                            SetDeviceError("耳机音频输出发生错误，已停止音乐。");
                        }
                    }
                }
                // The output device consumes one block every 20 ms. Its completed-buffer event
                // paces the mixer; adding another 20 ms sleep would progressively starve it.
                if (!outputPaced) Thread.Sleep(20);
            }
        }

        private void UpdateMusicSpectrum(short[] samples, double routeGain)
        {
            int frames = samples.Length / 2;
            for (int i = 0; i < frames; i++)
            {
                int mono = ((int)samples[i * 2] + samples[i * 2 + 1]) / 2;
                _spectrumRing[_spectrumWrite] = (short)mono;
                _spectrumWrite = (_spectrumWrite + 1) % SpectrumFftSize;
            }
            _spectrumSamples = Math.Min(SpectrumFftSize, _spectrumSamples + frames);
            if (++_spectrumUpdateCounter < 5 || _spectrumSamples < SpectrumFftSize) return;
            _spectrumUpdateCounter = 0;

            int oldest = _spectrumWrite;
            for (int i = 0; i < SpectrumFftSize; i++)
            {
                int ringIndex = oldest + i;
                if (ringIndex >= SpectrumFftSize) ringIndex -= SpectrumFftSize;
                double window = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (SpectrumFftSize - 1));
                _spectrumReal[i] = _spectrumRing[ringIndex] * routeGain * window;
                _spectrumImaginary[i] = 0.0;
            }

            int reversed = 0;
            for (int i = 1; i < SpectrumFftSize; i++)
            {
                int bit = SpectrumFftSize >> 1;
                while ((reversed & bit) != 0)
                {
                    reversed ^= bit;
                    bit >>= 1;
                }
                reversed ^= bit;
                if (i < reversed)
                {
                    double swap = _spectrumReal[i];
                    _spectrumReal[i] = _spectrumReal[reversed];
                    _spectrumReal[reversed] = swap;
                    swap = _spectrumImaginary[i];
                    _spectrumImaginary[i] = _spectrumImaginary[reversed];
                    _spectrumImaginary[reversed] = swap;
                }
            }

            for (int length = 2; length <= SpectrumFftSize; length <<= 1)
            {
                double angle = -2.0 * Math.PI / length;
                double stepReal = Math.Cos(angle);
                double stepImaginary = Math.Sin(angle);
                int half = length >> 1;
                for (int start = 0; start < SpectrumFftSize; start += length)
                {
                    double twiddleReal = 1.0;
                    double twiddleImaginary = 0.0;
                    for (int offset = 0; offset < half; offset++)
                    {
                        int even = start + offset;
                        int odd = even + half;
                        double oddReal = _spectrumReal[odd] * twiddleReal - _spectrumImaginary[odd] * twiddleImaginary;
                        double oddImaginary = _spectrumReal[odd] * twiddleImaginary + _spectrumImaginary[odd] * twiddleReal;
                        _spectrumReal[odd] = _spectrumReal[even] - oddReal;
                        _spectrumImaginary[odd] = _spectrumImaginary[even] - oddImaginary;
                        _spectrumReal[even] += oddReal;
                        _spectrumImaginary[even] += oddImaginary;
                        double nextReal = twiddleReal * stepReal - twiddleImaginary * stepImaginary;
                        twiddleImaginary = twiddleReal * stepImaginary + twiddleImaginary * stepReal;
                        twiddleReal = nextReal;
                    }
                }
            }

            double frequencyRatio = 18000.0 / 35.0;
            for (int band = 0; band < SpectrumBandCount; band++)
            {
                double lowHz = 35.0 * Math.Pow(frequencyRatio, (double)band / SpectrumBandCount);
                double highHz = 35.0 * Math.Pow(frequencyRatio, (double)(band + 1) / SpectrumBandCount);
                int firstBin = Math.Max(1, (int)Math.Floor(lowHz * SpectrumFftSize / WinMm.SampleRate));
                int lastBin = Math.Min(SpectrumFftSize / 2, Math.Max(firstBin, (int)Math.Ceiling(highHz * SpectrumFftSize / WinMm.SampleRate)));
                double maxMagnitude = 0.0;
                for (int bin = firstBin; bin <= lastBin; bin++)
                {
                    double real = _spectrumReal[bin];
                    double imaginary = _spectrumImaginary[bin];
                    double magnitude = Math.Sqrt(real * real + imaginary * imaginary) * 4.0 / SpectrumFftSize;
                    if (magnitude > maxMagnitude) maxMagnitude = magnitude;
                }

                int target = AudioRouteProbe.MeterValue((int)Math.Min(Int16.MaxValue, Math.Round(maxMagnitude)));
                double smoothing = target >= _spectrumSmoothed[band] ? 0.65 : 0.45;
                _spectrumSmoothed[band] = Math.Max(0, Math.Min(100, (int)Math.Round(_spectrumSmoothed[band] * (1.0 - smoothing) + target * smoothing)));
            }
            _musicSpectrum = (int[])_spectrumSmoothed.Clone();
        }

        private void ReadMusic(short[] destination, out int sessionId, out int generation)
        {
            Array.Clear(destination, 0, destination.Length);
            Mp3StreamPlayer player;
            sessionId = 0;
            generation = 0;
            lock (_stateGate)
            {
                if (!_playing || _streamPlayer == null)
                    return;
                player = _streamPlayer;
                sessionId = _sessionId;
                generation = _seekGeneration;
            }

            int frames;
            if (player.TryReadBlock(destination, out frames))
            {
                lock (_stateGate)
                {
                    if (_streamPlayer == player && _sessionId == sessionId && _seekGeneration == generation)
                        _lastPositionSeconds = player.PositionSeconds;
                    else
                        Array.Clear(destination, 0, destination.Length);
                }
                return;
            }
            if (player.IsFinished)
            {
                Exception error = player.Error;
                bool ended = false;
                DirectMp3Player direct = null;
                lock (_stateGate)
                {
                    if (_streamPlayer == player && _sessionId == sessionId && _seekGeneration == generation)
                    {
                        _streamPlayer = null;
                        direct = _directPlayer;
                        _directPlayer = null;
                        _lastPositionSeconds = player.PositionSeconds;
                        _lastDurationSeconds = Math.Max(_lastDurationSeconds, player.DurationSeconds);
                        _lastTrackId = _playingTrackId;
                        _lastTrackName = _playingName;
                        _lastFileName = _playingFileName;
                        _playing = false;
                        _loading = false;
                        _seeking = false;
                        _playingTrackId = "";
                        _playingFileName = "";
                        _currentTrackGain = 1.0;
                        _directHeadphonePlayback = false;
                        _playbackError = error == null ? "" : error.Message;
                        _playbackStatus = error == null ? "播放结束" : "播放失败：" + error.Message;
                        ended = true;
                    }
                }
                if (ended)
                {
                    if (direct != null)
                        direct.Dispose();
                    player.Dispose();
                    NotifyStatusLater();
                }
            }
        }

        internal static void MixCable(short[] music, short[] microphone, short[] destination, int volume)
        {
            if (music == null || microphone == null || destination == null) throw new ArgumentNullException("samples");
            if (microphone.Length != music.Length || destination.Length != music.Length) throw new ArgumentException("音频块长度必须一致。");
            double gain = Clamp(volume, 0, 100) / 100.0 * MaximumMusicGain;
            for (int i = 0; i < music.Length; i++)
            {
                int mixed = microphone[i] + (int)Math.Round(music[i] * gain);
                destination[i] = ClampSample(mixed);
            }
        }

        internal static void ScaleHeadphoneMusic(short[] samples, int volume)
        {
            ScaleHeadphoneMusic(samples, volume, 1.0);
        }

        internal static void ScaleHeadphoneMusic(short[] samples, int volume, double trackGain)
        {
            double gain = Clamp(volume, 0, 100) / 100.0 * MaximumMusicGain * trackGain;
            for (int i = 0; i < samples.Length; i++) samples[i] = (short)Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, Math.Round(samples[i] * gain)));
        }

        private static int Peak(short[] samples)
        {
            int peak = 0;
            foreach (short sample in samples) peak = Math.Max(peak, Math.Abs((int)sample));
            return peak;
        }

        private static short ClampSample(int value)
        {
            if (value > Int16.MaxValue) return Int16.MaxValue;
            if (value < Int16.MinValue) return Int16.MinValue;
            return (short)value;
        }

        private void SetDeviceError(string message)
        {
            lock (_stateGate)
                _deviceStatus = message + Environment.NewLine + _deviceStatus;
            NotifyStatus();
        }

        private void NotifyStatusLater()
        {
            ThreadPool.QueueUserWorkItem(delegate { NotifyStatus(); });
        }

        private void NotifyStatus()
        {
            EventHandler handler = StatusChanged;
            if (handler != null)
                try { handler(this, EventArgs.Empty); } catch { }
        }

        private void CloseDevices()
        {
            if (_microphone != null) { _microphone.Dispose(); _microphone = null; }
            if (_cable != null) { _cable.Dispose(); _cable = null; }
            if (_headphones != null) { _headphones.Dispose(); _headphones = null; }
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
            Mp3StreamPlayer player;
            lock (_stateGate)
            {
                player = _streamPlayer;
                _streamPlayer = null;
                _playing = false;
                _loading = false;
                _seeking = false;
            }
            if (player != null)
                player.Cancel();
            DirectMp3Player direct;
            lock (_stateGate)
            {
                direct = _directPlayer;
                _directPlayer = null;
            }
            if (direct != null)
                direct.Dispose();
            if (_audioThread != null && _audioThread.IsAlive && Thread.CurrentThread != _audioThread)
                _audioThread.Join(1500);
            if (player != null)
                player.Dispose();
            lock (_deviceGate)
                CloseDevices();
            if (_timerResolutionActive)
            {
                WinMm.timeEndPeriod(1);
                _timerResolutionActive = false;
            }
        }
    }

    internal sealed class FinalMixLimiter
    {
        internal const double TargetDbfs = -1.0;
        private const double MaximumSample = 32767.0;
        private static readonly double Ceiling = MaximumSample * Math.Pow(10.0, TargetDbfs / 20.0);
        private static readonly double ReleaseCoefficient = 1.0 - Math.Exp(-1.0 / (0.08 * WinMm.SampleRate));
        private double _gain = 1.0;

        public void Mix(short[] music, short[] microphone, short[] destination, int volume, double trackGain, out int musicOutputPeak)
        {
            if (music == null || microphone == null || destination == null) throw new ArgumentNullException("samples");
            if (microphone.Length != music.Length || destination.Length != music.Length) throw new ArgumentException("音频块长度必须一致。");
            double musicGain = Math.Max(0, Math.Min(100, volume)) / 100.0 * 0.30 * trackGain;
            musicOutputPeak = 0;
            for (int i = 0; i < music.Length; i++)
            {
                double scaledMusic = music[i] * musicGain;
                double mixed = microphone[i] + scaledMusic;
                double magnitude = Math.Abs(mixed);
                double desired = magnitude > Ceiling ? Ceiling / magnitude : 1.0;
                if (desired < _gain) _gain = desired;
                else _gain += (desired - _gain) * ReleaseCoefficient;
                int limitedMusic = (int)Math.Round(scaledMusic * _gain);
                musicOutputPeak = Math.Max(musicOutputPeak, Math.Min(Int16.MaxValue, Math.Abs(limitedMusic)));
                double limited = mixed * _gain;
                if (limited > Ceiling) limited = Ceiling;
                else if (limited < -Ceiling) limited = -Ceiling;
                destination[i] = (short)Math.Round(limited);
            }
        }
    }
}
