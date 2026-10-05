using System;
using System.Runtime.InteropServices;
using System.Text;

namespace BgmHotkey
{
    // Windows streams the original MP3 to the default playback device.
    // The separate decoder is used only for the VB-CABLE mix.
    internal sealed class DirectMp3Player : IDisposable
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(string command, StringBuilder returnValue, int returnLength, IntPtr callback);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool mciGetErrorString(int errorCode, StringBuilder errorText, int errorTextSize);

        private readonly object _gate = new object();
        private readonly string _alias;
        private bool _opened;

        public DirectMp3Player(string path, double startSeconds, int sliderVolume)
        {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentException("MP3 文件路径为空。", "path");

            _alias = "Bgm" + Guid.NewGuid().ToString("N");
            Send("open \"" + path + "\" type mpegvideo alias " + _alias);
            _opened = true;
            try
            {
                Send("set " + _alias + " time format milliseconds");
                SetSliderVolume(sliderVolume);
                long startMilliseconds = (long)Math.Round(Math.Max(0, startSeconds) * 1000.0);
                Send("play " + _alias + " from " + startMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch
            {
                Close();
                throw;
            }
        }

        public void SetSliderVolume(int sliderVolume)
        {
            int clamped = Math.Max(0, Math.Min(100, sliderVolume));
            int mciVolume = (int)Math.Round(clamped * 3 * 0.90); // Same peak trim and 0–30% slider range as the cable route.
            lock (_gate)
            {
                if (_opened)
                    Send("setaudio " + _alias + " volume to " + mciVolume.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
                if (!_opened)
                    return;
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
