using System;
using System.IO;

namespace BgmHotkey
{
    internal static class AudioFormatCatalog
    {
        private static readonly string[] Extensions =
        {
            ".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac",
            ".ogg", ".opus", ".aif", ".aiff"
        };

        public static string FileDialogFilter
        {
            get
            {
                return "音频候选文件（导入时检查编码）|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac;*.ogg;*.opus;*.aif;*.aiff|所有文件|*.*";
            }
        }

        public static string SupportedExtensionsText
        {
            get { return "候选扩展名：MP3、WAV、M4A/AAC、WMA、FLAC、OGG/Opus、AIFF；导入时逐首检查系统解码器是否能播放。"; }
        }

        public static bool IsSupportedExtension(string pathOrName)
        {
            if (String.IsNullOrWhiteSpace(pathOrName)) return false;
            string extension = Path.GetExtension(pathOrName);
            foreach (string candidate in Extensions)
                if (String.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string FormatLabel(string pathOrName)
        {
            string extension = Path.GetExtension(pathOrName ?? "").TrimStart('.').ToUpperInvariant();
            return extension.Length == 0 ? "音频" : extension;
        }

        public static double ProbeDurationSeconds(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("找不到音频文件。", path);
            if (!IsSupportedExtension(path)) throw new InvalidOperationException("文件扩展名不在支持列表中。");
            using (Mp3Decoder.StreamingReader reader = new Mp3Decoder.StreamingReader(path, 0, null))
            {
                if (reader.DurationSeconds <= 0 || Double.IsNaN(reader.DurationSeconds) || Double.IsInfinity(reader.DurationSeconds))
                    throw new InvalidOperationException("系统解码器无法读取有效时长。");
                short[] firstBlock = new short[WinMm.BlockFrames * WinMm.OutputChannels];
                if (reader.ReadFrames(firstBlock, WinMm.BlockFrames) <= 0)
                    throw new InvalidOperationException("音频没有可播放的内容，或当前系统没有此格式的解码器。");
                return reader.DurationSeconds;
            }
        }
    }
}
