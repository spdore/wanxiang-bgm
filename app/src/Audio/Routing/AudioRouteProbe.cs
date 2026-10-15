using System;
using System.Diagnostics;
using System.Threading;

namespace BgmHotkey
{
    internal static class AudioRouteProbe
    {
        // Drivers can dither digital silence to +/-1. Avoid reporting that as audio.
        public const int SignalFloor = 16;
        // Reads only the selected virtual recording endpoint; no audio is saved or replayed.
        public static int MeasurePeak(AudioDevice recording)
        {
            if (recording == null)
                throw new InvalidOperationException("没有找到对应的 CABLE Output 录音设备，请刷新设备或检查驱动。");
            using (WaveInCapture capture = new WaveInCapture(recording.Id, recording.Name))
            {
                short[] samples = new short[WinMm.BlockFrames * 2];
                int peak = 0;
                Stopwatch duration = Stopwatch.StartNew();
                while (duration.ElapsedMilliseconds < 1500)
                {
                    Thread.Sleep(20);
                    capture.ReadStereo(samples, WinMm.BlockFrames);
                    foreach (short sample in samples) peak = Math.Max(peak, Math.Abs((int)sample));
                }
                return peak;
            }
        }

        public static string FormatPeak(int peak)
        {
            return peak <= SignalFloor ? "无明显信号" : (20 * Math.Log10(peak / 32768.0)).ToString("0.0") + " dBFS";
        }

        public static int MeterValue(int peak)
        {
            if (peak <= SignalFloor) return 0;
            return Math.Max(0, Math.Min(100, (int)Math.Round((20 * Math.Log10(peak / 32768.0) + 60) / 60 * 100)));
        }
    }
}
