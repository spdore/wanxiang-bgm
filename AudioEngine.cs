using System;
using System.IO;
using System.Threading;

namespace BgmHotkey
{
    internal sealed class AudioEngine : IDisposable
    {
        private const double MaximumMusicGain = 0.30;
        private readonly object _stateGate = new object();
        private readonly object _deviceGate = new object();
        private readonly AppSettings _settings;
        private readonly AudioDeviceCatalog _catalog;
        private volatile bool _running;
        private Thread _audioThread;
        private WaveInCapture _microphone;
        private WaveOutSink _cable;
        private string _deviceStatus = "";
        private string _playbackStatus = "就绪";
        private string _playingName = "";
        private string _playingTrackId = "";
        private bool _playing;
        private bool _loading;
        private Mp3StreamPlayer _streamPlayer;
        private DirectMp3Player _directPlayer;
        private int _headphoneVolume;
        private int _cableVolume;
        private bool _timerResolutionActive;
        private bool _disposed;
        private volatile int _microphonePeak;
        private volatile int _musicPeak;
        private volatile int _outputPeak;

        public int MicrophonePeak { get { return _microphonePeak; } }
        public int MusicPeak { get { return _musicPeak; } }
        public int OutputPeak { get { return _outputPeak; } }

        public event EventHandler StatusChanged;
        public string PlaybackSummary
        {
            get { lock (_stateGate) return _playing ? _playingName : (_loading ? "准备播放…" : "等待播放"); }
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
                    _deviceStatus = microphoneStatus + Environment.NewLine + "耳机输出：Windows 原生 MP3 播放（系统默认播放设备）" + Environment.NewLine + cableStatus +
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
            if (track == null)
                return;

            string filePath = AppPaths.TrackPath(track.FileName);
            Mp3StreamPlayer playerToStop = null;
            Mp3StreamPlayer playerToStart = null;
            DirectMp3Player directToStop = null;
            DirectMp3Player directToStart = null;
            int headphoneVolume = 0;
            lock (_stateGate)
            {
                if (_playing || _loading)
                {
                    playerToStop = _streamPlayer;
                    _streamPlayer = null;
                    directToStop = _directPlayer;
                    _directPlayer = null;
                    _playing = false;
                    _loading = false;
                    _playingName = "";
                    _playingTrackId = "";
                    _playbackStatus = "已停止";
                }
                else
                {
                    _streamPlayer = new Mp3StreamPlayer(filePath, track.StartSeconds);
                    playerToStart = _streamPlayer;
                    headphoneVolume = _headphoneVolume;
                    _playingName = track.Name;
                    _playingTrackId = track.Id;
                    _loading = true;
                    _playbackStatus = "正在启动：" + track.Name;
                }
            }

            if (playerToStop != null)
            {
                playerToStop.Cancel();
                ThreadPool.QueueUserWorkItem(delegate { playerToStop.Dispose(); });
            }
            if (directToStop != null)
                directToStop.Dispose();
            NotifyStatus();
            if (playerToStart == null)
                return;

            try
            {
                directToStart = new DirectMp3Player(filePath, track.StartSeconds, headphoneVolume);
                playerToStart.Start();
                lock (_stateGate)
                {
                    if (_streamPlayer == playerToStart)
                    {
                        _directPlayer = directToStart;
                        _loading = false;
                        _playing = true;
                        _playbackStatus = "正在播放：" + track.Name + "（耳机原生 MP3；VB-CABLE 独立混音）";
                    }
                    else
                        directToStart.Dispose();
                }
            }
            catch (Exception ex)
            {
                if (directToStart != null)
                    directToStart.Dispose();
                playerToStart.Dispose();
                lock (_stateGate)
                {
                    if (_streamPlayer == playerToStart)
                    {
                        _streamPlayer = null;
                        _loading = false;
                        _playing = false;
                        _playingName = "";
                        _playbackStatus = "启动失败：" + ex.Message;
                    }
                }
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
                _streamPlayer = null;
                _directPlayer = null;
                _playing = false;
                _loading = false;
                _playingName = "";
                _playingTrackId = "";
                _playbackStatus = "已停止";
            }
            if (stream != null) { stream.Cancel(); ThreadPool.QueueUserWorkItem(delegate { stream.Dispose(); }); }
            if (direct != null) direct.Dispose();
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
                try { ReadMusic(music); }
                catch (Exception ex)
                {
                    Array.Clear(music, 0, music.Length);
                    AppPaths.Log("音乐混音读取失败：" + ex);
                    StopActiveTrack();
                    SetDeviceError("音乐混音读取失败：" + ex.Message);
                }
                lock (_deviceGate)
                {
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
                    lock (_stateGate)
                    {
                        cableVolume = _cableVolume;
                    }

                    if (_cable != null)
                    {
                        MixCable(music, microphone, cableSamples, cableVolume);
                        _microphonePeak = Peak(microphone);
                        _musicPeak = (int)Math.Round(Peak(music) * cableVolume / 100.0 * MaximumMusicGain);
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
                            SetDeviceError("VB-CABLE 输出发生错误，已停止虚拟麦克风混音。");
                        }
                    }
                    else
                    {
                        _microphonePeak = Peak(microphone);
                        _musicPeak = (int)Math.Round(Peak(music) * cableVolume / 100.0 * MaximumMusicGain);
                        _outputPeak = 0;
                    }
                }
                // The output device consumes one block every 20 ms. Its completed-buffer event
                // paces the mixer; adding another 20 ms sleep would progressively starve it.
                if (!outputPaced) Thread.Sleep(20);
            }
        }

        private void ReadMusic(short[] destination)
        {
            Array.Clear(destination, 0, destination.Length);
            Mp3StreamPlayer player;
            lock (_stateGate)
            {
                if (!_playing || _streamPlayer == null)
                    return;
                player = _streamPlayer;
            }

            if (player.TryReadBlock(destination))
                return;
            if (player.IsFinished)
            {
                Exception error = player.Error;
                bool ended = false;
                DirectMp3Player direct = null;
                lock (_stateGate)
                {
                    if (_streamPlayer == player)
                    {
                        _streamPlayer = null;
                        direct = _directPlayer;
                        _directPlayer = null;
                        _playing = false;
                        _loading = false;
                        _playingName = "";
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
}
