using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace BgmHotkey
{
    internal sealed class LoudnessAnalysisResult
    {
        public bool HasMeasurement;
        public double IntegratedLufs;
        public double PeakDbfs;
        public double GainDb;
    }

    internal static class LoudnessAnalysisService
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, string> Statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string _progressText = "响度分析尚未开始";
        public static string ProgressText { get { lock (Gate) return _progressText; } }

        public static string GetStatus(string fileName)
        {
            lock (Gate)
            {
                string status;
                return Statuses.TryGetValue(fileName ?? "", out status) ? status : "等待分析";
            }
        }

        public static void SetStatus(string fileName, string status)
        {
            lock (Gate) Statuses[fileName ?? ""] = status ?? "";
        }

        public static void SetProgress(string status)
        {
            lock (Gate) _progressText = status ?? "";
        }
    }

    internal static class LoudnessAnalyzer
    {
        public const string AlgorithmVersion = "BS1770-R128-v4-baseline-44100";
        public const string PreviousAlgorithmVersion = "BS1770-R128-v3-mcicap-44100";
        public const string LegacyAlgorithmVersion = "BS1770-R128-v2-peakcap-44100";
        public const double TargetLufs = -18.0;
        // MCI's documented volume-factor range tops out at 1000. Keep normalization
        // within that range so native MP3 and the virtual PCM mix use the same gain.
        public const double MaximumBoostDb = 11.0;
        public const double TrackPeakCeilingDbfs = -1.0;
        private const int Rate = WinMm.SampleRate;
        private const int Channels = WinMm.OutputChannels;
        private const int WindowFrames = Rate * 400 / 1000;
        private const int HopFrames = Rate * 100 / 1000;
        private const double AbsoluteGateLufs = -70.0;
        private const double LoudnessOffset = -0.691;

        private sealed class Biquad
        {
            public double B0, B1, B2, A1, A2, Z1, Z2;
            public double Process(double input)
            {
                double output = B0 * input + Z1;
                Z1 = B1 * input - A1 * output + Z2;
                Z2 = B2 * input - A2 * output;
                return output;
            }
        }

        private sealed class ChannelFilter
        {
            public readonly Biquad Shelf;
            public readonly Biquad Rlb;
            public ChannelFilter(Biquad shelf, Biquad rlb) { Shelf = shelf; Rlb = rlb; }
            public double Process(double value) { return Rlb.Process(Shelf.Process(value)); }
        }

        public static string Fingerprint(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            {
                byte[] hash = sha.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        public static LoudnessAnalysisResult Analyze(string path)
        {
            return Analyze(path, TargetLufs);
        }

        public static LoudnessAnalysisResult Analyze(string path, double targetLufs)
        {
            targetLufs = NormalizeTarget(targetLufs);
            ChannelFilter left = CreateFilter();
            ChannelFilter right = CreateFilter();
            double[] window = new double[WindowFrames];
            List<double> blocks = new List<double>();
            double sum = 0;
            int write = 0;
            int filled = 0;
            int peakSample = 0;
            long frame = 0;
            short[] pcm = new short[WinMm.BlockFrames * Channels];
            using (Mp3Decoder.StreamingReader reader = new Mp3Decoder.StreamingReader(path, 0, null))
            {
                while (true)
                {
                    int frames = reader.ReadFrames(pcm, WinMm.BlockFrames);
                    if (frames <= 0) break;
                    for (int i = 0; i < frames; i++)
                    {
                        short leftSample = pcm[i * Channels];
                        short rightSample = pcm[i * Channels + 1];
                        peakSample = Math.Max(peakSample, Math.Abs((int)leftSample));
                        peakSample = Math.Max(peakSample, Math.Abs((int)rightSample));
                        double l = left.Process(leftSample / 32768.0);
                        double r = right.Process(rightSample / 32768.0);
                        double energy = l * l + r * r;
                        if (filled == WindowFrames) sum -= window[write]; else filled++;
                        window[write] = energy;
                        sum += energy;
                        write++; if (write == WindowFrames) write = 0;
                        frame++;
                        if (filled == WindowFrames && (frame - WindowFrames) % HopFrames == 0)
                            blocks.Add(sum / WindowFrames);
                    }
                }
            }
            if (blocks.Count == 0)
                return new LoudnessAnalysisResult { HasMeasurement = false, IntegratedLufs = -100, PeakDbfs = peakSample == 0 ? -100 : 20.0 * Math.Log10(peakSample / 32768.0), GainDb = 0 };

            double absoluteGate = Math.Pow(10.0, (AbsoluteGateLufs - LoudnessOffset) / 10.0);
            double preliminary = MeanAboveGate(blocks, absoluteGate);
            if (preliminary <= 0 || Double.IsNaN(preliminary) || Double.IsInfinity(preliminary))
                return new LoudnessAnalysisResult { HasMeasurement = false, IntegratedLufs = -100, PeakDbfs = peakSample == 0 ? -100 : 20.0 * Math.Log10(peakSample / 32768.0), GainDb = 0 };
            double relativeGate = preliminary * 0.1;
            double finalMean = MeanAboveGate(blocks, Math.Max(absoluteGate, relativeGate));
            if (finalMean <= 0 || Double.IsNaN(finalMean) || Double.IsInfinity(finalMean))
                return new LoudnessAnalysisResult { HasMeasurement = false, IntegratedLufs = -100, PeakDbfs = peakSample == 0 ? -100 : 20.0 * Math.Log10(peakSample / 32768.0), GainDb = 0 };

            double lufs = LoudnessOffset + 10.0 * Math.Log10(finalMean);
            if (Double.IsNaN(lufs) || Double.IsInfinity(lufs))
                return new LoudnessAnalysisResult { HasMeasurement = false, IntegratedLufs = -100, PeakDbfs = peakSample == 0 ? -100 : 20.0 * Math.Log10(peakSample / 32768.0), GainDb = 0 };
            double peakDbfs = peakSample == 0 ? -100 : 20.0 * Math.Log10(peakSample / 32768.0);
            double gainDb = CalculateGainDb(lufs, peakDbfs, targetLufs);
            return new LoudnessAnalysisResult { HasMeasurement = true, IntegratedLufs = lufs, PeakDbfs = peakDbfs, GainDb = gainDb };
        }

        public static bool IsCompatibleAlgorithmVersion(string version)
        {
            return String.Equals(version, AlgorithmVersion, StringComparison.Ordinal) ||
                String.Equals(version, PreviousAlgorithmVersion, StringComparison.Ordinal) ||
                String.Equals(version, LegacyAlgorithmVersion, StringComparison.Ordinal);
        }

        public static double CalculateGainDb(double integratedLufs, double peakDbfs, double targetLufs)
        {
            if (Double.IsNaN(integratedLufs) || Double.IsInfinity(integratedLufs) || integratedLufs <= -99 ||
                Double.IsNaN(peakDbfs) || Double.IsInfinity(peakDbfs)) return 0;
            targetLufs = NormalizeTarget(targetLufs);
            return Math.Max(-60.0, Math.Min(MaximumBoostDb, Math.Min(targetLufs - integratedLufs, TrackPeakCeilingDbfs - peakDbfs)));
        }

        private static double NormalizeTarget(double targetLufs)
        {
            if (Double.IsNaN(targetLufs) || Double.IsInfinity(targetLufs)) return TargetLufs;
            return Math.Max(-70.0, Math.Min(10.0, targetLufs));
        }

        private static double MeanAboveGate(List<double> blocks, double gate)
        {
            double sum = 0;
            int count = 0;
            foreach (double energy in blocks)
                if (energy >= gate && energy > 0) { sum += energy; count++; }
            return count == 0 ? 0 : sum / count;
        }

        private static ChannelFilter CreateFilter()
        {
            const double gainDb = 3.999843853973347;
            const double shelfFrequency = 1681.974450955533;
            const double shelfQ = 0.7071752369554196;
            const double shelfExponent = 0.4996667741545416;
            double k = Math.Tan(Math.PI * shelfFrequency / Rate);
            double vh = Math.Pow(10.0, gainDb / 20.0);
            double vb = Math.Pow(vh, shelfExponent);
            double a0 = 1.0 + k / shelfQ + k * k;
            Biquad shelf = new Biquad();
            shelf.B0 = (vh + vb * k / shelfQ + k * k) / a0;
            shelf.B1 = 2.0 * (k * k - vh) / a0;
            shelf.B2 = (vh - vb * k / shelfQ + k * k) / a0;
            shelf.A1 = 2.0 * (k * k - 1.0) / a0;
            shelf.A2 = (1.0 - k / shelfQ + k * k) / a0;

            const double rlbFrequency = 38.13547087602444;
            const double rlbQ = 0.5003270373238773;
            k = Math.Tan(Math.PI * rlbFrequency / Rate);
            a0 = 1.0 + k / rlbQ + k * k;
            Biquad rlb = new Biquad();
            rlb.B0 = 1.0 / a0; rlb.B1 = -2.0 / a0; rlb.B2 = 1.0 / a0;
            rlb.A1 = 2.0 * (k * k - 1.0) / a0;
            rlb.A2 = (1.0 - k / rlbQ + k * k) / a0;
            return new ChannelFilter(shelf, rlb);
        }
    }
}
