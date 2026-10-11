using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace BgmHotkey
{
    // The original MP3 file is played natively to the Windows default output.
    // The separate streaming reader feeds only the app's virtual-cable music mix.
    internal sealed class DirectMp3Player : IDisposable
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(string command, StringBuilder returnValue, int returnLength, IntPtr callback);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool mciGetErrorString(int errorCode, StringBuilder errorText, int errorTextSize);

        private readonly object _gate = new object();
        private readonly string _alias;
        private double _endSeconds;
        private bool _opened;

        public DirectMp3Player(string path, double startSeconds, double? endSeconds, int sliderVolume)
        {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentException("音频文件路径为空。", "path");

            _alias = "Bgm" + Guid.NewGuid().ToString("N");
            Send("open \"" + path + "\" type mpegvideo alias " + _alias);
            _opened = true;
            try
            {
                Send("set " + _alias + " time format milliseconds");
                SetSliderVolume(sliderVolume);
                PlayFrom(startSeconds, endSeconds);
            }
            catch
            {
                Close();
                throw;
            }
        }

        public double DurationSeconds
        {
            get { return ReadMillisecondsStatus("length") / 1000.0; }
        }

        public double PositionSeconds
        {
            get { return ReadMillisecondsStatus("position") / 1000.0; }
        }

        public bool IsPlaying
        {
            get
            {
                lock (_gate)
                {
                    if (!_opened) return false;
                    StringBuilder value = new StringBuilder(64);
                    int result = mciSendString("status " + _alias + " mode", value, value.Capacity, IntPtr.Zero);
                    return result == 0 && String.Equals(value.ToString(), "playing", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        public void Seek(double seconds)
        {
            lock (_gate)
            {
                if (!_opened) return;
                long milliseconds = Math.Max(0, (long)Math.Round(seconds * 1000.0));
                string target = milliseconds.ToString(CultureInfo.InvariantCulture);
                Send("seek " + _alias + " to " + target);
                string command = "play " + _alias + " from " + target;
                if (_endSeconds > 0)
                    command += " to " + Math.Max(milliseconds, (long)Math.Round(_endSeconds * 1000.0)).ToString(CultureInfo.InvariantCulture);
                Send(command);
            }
        }

        public void Pause()
        {
            lock (_gate)
            {
                if (!_opened) return;
                StringBuilder value = new StringBuilder(64);
                int result = mciSendString("status " + _alias + " mode", value, value.Capacity, IntPtr.Zero);
                if (result == 0 && String.Equals(value.ToString(), "playing", StringComparison.OrdinalIgnoreCase))
                    Send("pause " + _alias);
            }
        }

        public void SetSliderVolume(int sliderVolume)
        {
            int clamped = Math.Max(0, Math.Min(100, sliderVolume));
            int mciVolume = (int)Math.Round(clamped * 3 * 0.90);
            lock (_gate)
            {
                if (_opened)
                    Send("setaudio " + _alias + " volume to " + mciVolume.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void PlayFrom(double startSeconds, double? endSeconds)
        {
            long startMilliseconds = Math.Max(0, (long)Math.Round(startSeconds * 1000.0));
            string command = "play " + _alias + " from " + startMilliseconds.ToString(CultureInfo.InvariantCulture);
            _endSeconds = endSeconds.HasValue ? endSeconds.Value : 0;
            if (endSeconds.HasValue)
                command += " to " + Math.Max(startMilliseconds, (long)Math.Round(endSeconds.Value * 1000.0)).ToString(CultureInfo.InvariantCulture);
            Send(command);
        }

        private long ReadMillisecondsStatus(string status)
        {
            lock (_gate)
            {
                if (!_opened) return 0;
                StringBuilder value = new StringBuilder(64);
                int result = mciSendString("status " + _alias + " " + status, value, value.Capacity, IntPtr.Zero);
                long milliseconds;
                if (result != 0 || !Int64.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out milliseconds)) return 0;
                return Math.Max(0, milliseconds);
            }
        }

        private void Send(string command)
        {
            int result = mciSendString(command, null, 0, IntPtr.Zero);
            if (result != 0)
                throw CreateMciException(command, result);
        }

        private static Exception CreateMciException(string command, int result)
        {
            StringBuilder message = new StringBuilder(256);
            if (!mciGetErrorString(result, message, message.Capacity) || message.Length == 0)
                message.Append("MCI 错误 ").Append(result);
            return new InvalidOperationException("Windows MP3 播放失败：" + message + "（" + command + "）");
        }

        private void Close()
        {
            lock (_gate)
            {
                if (!_opened) return;
                mciSendString("stop " + _alias, null, 0, IntPtr.Zero);
                mciSendString("close " + _alias, null, 0, IntPtr.Zero);
                _opened = false;
            }
        }

        public void Dispose()
        {
            Close();
        }
    }
}
