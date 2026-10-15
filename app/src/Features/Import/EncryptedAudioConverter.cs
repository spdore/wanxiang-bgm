using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Text;
using System.Web.Script.Serialization;

namespace BgmHotkey
{
    internal enum EncryptedConversionStatus
    {
        NotEncrypted,
        Converted,
        KeyUnavailable,
        UnsupportedVariant,
        InvalidFile,
        TimedOut,
        FileTooLarge,
        Cancelled,
        Failed
    }

    internal sealed class EncryptedConversionResult
    {
        public EncryptedConversionStatus Status;
        public string OutputPath = "";
        public string Container = "";
        public string Variant = "";
        public string Message = "";
    }

    internal sealed class ImportedAudioCandidate
    {
        public string SourcePath = "";
        public string LibraryPath = "";
        public string LibraryFileName = "";
        public string PreparedPath = "";
        public string AddedFileName = "";
        public string FileName = "";
        public int ImportIndex;
        public double DurationSeconds;
        public bool WasConverted;
    }

    internal sealed class SongImportOperation
    {
        private readonly object _gate = new object();
        private bool _cancelled;
        private IImportChildControl _process;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private bool _cleanupSafe = true;
        private bool _helperExtractionAttempted;
        private long _progressRevision;
        private long _lastProgressBytes;
        private long _stageStartedMilliseconds;
        private string _stage = "";
        private int _stageFileIndex;

        public readonly Guid Id = Guid.NewGuid();
        public event Action<int, string> StageChanged;

        public bool IsCancelled
        {
            get { lock (_gate) return _cancelled; }
        }

        public long ElapsedMilliseconds
        {
            get { return _clock.ElapsedMilliseconds; }
        }

        public long ProgressRevision
        {
            get { return Interlocked.Read(ref _progressRevision); }
        }

        public bool CanCleanup
        {
            get { lock (_gate) return _cleanupSafe; }
        }

        public void SetStage(int fileIndex, string stage)
        {
            string previous;
            long elapsed;
            int previousIndex;
            Action<int, string> handler;
            lock (_gate)
            {
                previous = _stage;
                previousIndex = _stageFileIndex;
                elapsed = _clock.ElapsedMilliseconds - _stageStartedMilliseconds;
                _stage = stage ?? "";
                _stageFileIndex = fileIndex;
                _stageStartedMilliseconds = _clock.ElapsedMilliseconds;
                _lastProgressBytes = 0;
                handler = StageChanged;
            }
            if (!String.IsNullOrEmpty(previous))
                AppPaths.Log("导入阶段 operation=" + Id.ToString("N") + " file=" + previousIndex + " stage=" + previous + " elapsedMs=" + elapsed);
            if (handler != null)
            {
                try { handler(fileIndex, stage ?? ""); }
                catch { }
            }
        }

        public bool RegisterProcess(IImportChildControl process)
        {
            if (process == null) throw new ArgumentNullException("process");
            lock (_gate)
            {
                if (_cancelled) return false;
                _process = process;
                _lastProgressBytes = 0;
                Interlocked.Exchange(ref _progressRevision, 0);
                return true;
            }
        }

        public void ClearProcess(IImportChildControl process)
        {
            lock (_gate)
                if (Object.ReferenceEquals(_process, process)) _process = null;
        }

        public void ReportProgress(long bytes)
        {
            lock (_gate)
            {
                if (bytes <= _lastProgressBytes) return;
                _lastProgressBytes = bytes;
                Interlocked.Increment(ref _progressRevision);
            }
        }

        public void MarkCleanupUnsafe()
        {
            lock (_gate) _cleanupSafe = false;
        }

        public bool TryBeginHelperExtraction()
        {
            lock (_gate)
            {
                if (_cancelled || _helperExtractionAttempted) return false;
                _helperExtractionAttempted = true;
                return true;
            }
        }

        public void Cancel()
        {
            IImportChildControl process;
            lock (_gate)
            {
                _cancelled = true;
                process = _process;
            }
            if (process != null) process.RequestTerminate();
        }
    }

    internal static class EncryptedAudioConverter
    {
        private static readonly HashSet<string> EncryptedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".ncm", ".mflac", ".mflac0", ".mgg", ".mgg0", ".mgg1", ".mggl", ".mmp4",
            ".qmcflac", ".qmcogg", ".qmc0", ".qmc2", ".qmc3", ".qmc4", ".qmc6", ".qmc8",
            ".tkm", ".bkcmp3", ".bkcm4a", ".bkcflac", ".bkcwav", ".bkcape", ".bkcogg", ".bkcwma",
            ".666c6163", ".6d7033", ".6f6767", ".6d3461", ".776176", ".kgm", ".kgma", ".vpr"
        };

        public static void CleanupTaskDirectory(Guid taskId)
        {
            string root = Path.GetFullPath(Path.Combine(AppPaths.SettingsDirectory, "import-work")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string target = Path.GetFullPath(AppPaths.ImportJobPath(taskId)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("导入临时目录越出应用数据目录。");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }

        public static bool IsEncryptedCandidate(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            string name = Path.GetFileName(path);
            if (name.EndsWith(".kgm.flac", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".vpr.flac", StringComparison.OrdinalIgnoreCase) ||
                EncryptedExtensions.Contains(Path.GetExtension(name))) return true;

            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] head = new byte[16];
                    int headRead = stream.Read(head, 0, head.Length);
                    if (headRead >= 8 && StartsWith(head, headRead, Encoding.ASCII.GetBytes("CTENFDAM"))) return true;
                    if (headRead == 16 && (StartsWith(head, headRead, new byte[] { 0x7C, 0xD5, 0x32, 0xEB, 0x86, 0x02, 0x7F, 0x4B, 0xA8, 0xAF, 0xA6, 0x8E, 0x0F, 0xFF, 0x99, 0x14 }) ||
                        StartsWith(head, headRead, new byte[] { 0x05, 0x28, 0xBC, 0x96, 0xE9, 0xE4, 0x5A, 0x43, 0x91, 0xAA, 0xBD, 0xD0, 0x7A, 0xF5, 0x36, 0x31 }))) return true;

                    long length = stream.Length;
                    if (length >= 8)
                    {
                        int readLength = (int)Math.Min(1024L, length);
                        byte[] tail = new byte[readLength];
                        stream.Seek(-readLength, SeekOrigin.End);
                        int read = 0;
                        while (read < tail.Length)
                        {
                            int count = stream.Read(tail, read, tail.Length - read);
                            if (count <= 0) break;
                            read += count;
                        }
                        if (EndsWith(tail, read, Encoding.ASCII.GetBytes("QTag")) ||
                            EndsWith(tail, read, Encoding.ASCII.GetBytes("STag")) ||
                            EndsWith(tail, read, Encoding.ASCII.GetBytes("musicex\0"))) return true;

                        // PcV1Legacy has no marker; validate its bounded
                        // base64 key length and bytes, unless this looks like
                        // an ordinary supported audio extension.
                        if (!AudioFormatCatalog.IsSupportedExtension(path) && HasPcV1Footer(tail, read)) return true;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }

        public static EncryptedConversionResult Convert(string sourcePath, string taskDirectory, int index, SongImportOperation operation)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            if (operation.IsCancelled) return Result(EncryptedConversionStatus.Cancelled, "", "", "", "已取消。");
            if (!IsEncryptedCandidate(sourcePath)) return Result(EncryptedConversionStatus.NotEncrypted, "", "", "", "");

            long sourceLength = 0L;
            try { sourceLength = new FileInfo(sourcePath).Length; }
            catch { }
            return ConvertDetected(sourcePath, taskDirectory, index, sourceLength, operation);
        }

        public static EncryptedConversionResult ConvertDetected(string sourcePath, string taskDirectory, int index, long sourceLength, SongImportOperation operation)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            if (operation.IsCancelled) return Result(EncryptedConversionStatus.Cancelled, "", "", "", "已取消。");
            if (sourceLength > ImportTimeoutPolicy.MaximumEncryptedInputBytes)
                return Result(EncryptedConversionStatus.FileTooLarge, "", "", "", "该加密文件超过 256 MiB 的安全转换上限；源文件未修改。");

            string helperDirectory = Path.Combine(taskDirectory, "helper");
            string rawOutput = Path.Combine(taskDirectory, "decoded_" + index.ToString("D4") + ".bin");
            string helper = Path.Combine(helperDirectory, "BgmCryptoHelper.exe");
            try
            {
                if (!File.Exists(helper))
                {
                    operation.SetStage(index, "准备本地转换组件");
                    if (!operation.TryBeginHelperExtraction())
                    {
                        if (operation.IsCancelled)
                            return Result(EncryptedConversionStatus.Cancelled, "", "", "", "已取消。");
                        return Result(EncryptedConversionStatus.Failed, "", "", "", "本地转换组件未能准备好，已跳过后续加密文件；源文件未修改。");
                    }
                    ExtractBundledHelper(helperDirectory, operation);
                }
                if (!File.Exists(helper)) return Result(EncryptedConversionStatus.Failed, "", "", "", "本地转换组件缺失。");
                if (operation.IsCancelled) return Result(EncryptedConversionStatus.Cancelled, "", "", "", "已取消。");

                operation.SetStage(index, "启动本地转换组件");

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = helper,
                    Arguments = "--decode " + ImportProcessRunner.QuoteArgument(sourcePath) + " " + ImportProcessRunner.QuoteArgument(rawOutput),
                    WorkingDirectory = helperDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                ImportProcessResult processResult = ImportProcessRunner.Run(startInfo, operation,
                    ImportTimeoutPolicy.ConversionTotalMilliseconds(sourceLength), 0);
                if (processResult.Status == ImportProcessStatus.Cancelled || operation.IsCancelled)
                    return Result(EncryptedConversionStatus.Cancelled, "", "", "", "已取消。");
                if (processResult.Status == ImportProcessStatus.TimedOut)
                {
                    if (operation.CanCleanup && File.Exists(rawOutput)) try { File.Delete(rawOutput); } catch { }
                    return Result(EncryptedConversionStatus.TimedOut, "", "", "", "本地转换超过时间上限，已停止本次转换；源文件未修改。");
                }
                if (processResult.Status != ImportProcessStatus.Exited || !processResult.CleanupSafe)
                    return Result(EncryptedConversionStatus.Failed, "", "", "", "本地转换进程无法安全回收；本次临时目录保留待清理。");

                if (operation.IsCancelled) return Result(EncryptedConversionStatus.Cancelled, "", "", "", "已取消。");
                Dictionary<string, object> response = null;
                string stdout = LastNonEmptyLine(processResult.StandardOutput);
                try { response = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(stdout); }
                catch { }
                if (response == null)
                    return Result(EncryptedConversionStatus.Failed, "", "", "", String.IsNullOrWhiteSpace(processResult.StandardError) ? "转换助手未返回有效结果。" : "转换助手运行失败：" + Limit(processResult.StandardError.Trim(), 180));

                string status = ReadText(response, "status");
                string message = ReadText(response, "message");
                string container = ReadText(response, "container").ToLowerInvariant();
                string variant = ReadText(response, "variant");
                if (status == "KeyUnavailable") return Result(EncryptedConversionStatus.KeyUnavailable, "", "", variant, String.IsNullOrWhiteSpace(message) ? "该版本缺少可用的内嵌密钥。" : message);
                if (status == "UnsupportedVariant") return Result(EncryptedConversionStatus.UnsupportedVariant, "", "", variant, String.IsNullOrWhiteSpace(message) ? "此加密变体暂不支持。" : message);
                if (status == "InvalidFile") return Result(EncryptedConversionStatus.InvalidFile, "", "", variant, String.IsNullOrWhiteSpace(message) ? "文件损坏或容器内容无效。" : message);
                if (status == "FileTooLarge") return Result(EncryptedConversionStatus.FileTooLarge, "", "", variant, "该加密文件超过安全转换上限；源文件未修改。");
                if (status != "converted" || processResult.ExitCode != 0 || !File.Exists(rawOutput))
                    return Result(EncryptedConversionStatus.Failed, "", "", variant, "转换失败或没有生成音频内容。");

                FileInfo outputInfo = new FileInfo(rawOutput);
                if (outputInfo.Length < 8 || outputInfo.Length > ImportTimeoutPolicy.MaximumDecodedOutputBytes || !HasContainerHeader(rawOutput, container))
                    return Result(EncryptedConversionStatus.InvalidFile, "", "", variant, "转换结果与识别出的音频容器不一致或内容不完整。");
                string extension = "." + container;
                string outputStem = Path.GetFileNameWithoutExtension(sourcePath);
                string convertedPath = Path.Combine(taskDirectory, outputStem + extension);
                if (File.Exists(convertedPath)) convertedPath = Path.Combine(taskDirectory, outputStem + "_" + index.ToString("D4") + extension);
                File.Move(rawOutput, convertedPath);
                return Result(EncryptedConversionStatus.Converted, convertedPath, container, variant, "");
            }
            catch (Exception ex)
            {
                if (operation.CanCleanup) try { if (File.Exists(rawOutput)) File.Delete(rawOutput); } catch { }
                if (operation.IsCancelled)
                    return Result(EncryptedConversionStatus.Cancelled, "", "", "", "已取消。");
                if (ex is TimeoutException)
                    return Result(EncryptedConversionStatus.TimedOut, "", "", "", "本地转换组件准备超过时间上限，源文件未修改。");
                return Result(EncryptedConversionStatus.Failed, "", "", "", "本地转换失败：" + Limit(ex.Message, 180));
            }
        }

        private static void ExtractBundledHelper(string targetDirectory, SongImportOperation operation)
        {
            string executable = Assembly.GetExecutingAssembly().Location;
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--import-extract-helper " + ImportProcessRunner.QuoteArgument(targetDirectory),
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            ImportProcessResult process = ImportProcessRunner.Run(startInfo, operation,
                ImportTimeoutPolicy.HelperExtractionMilliseconds, 0);
            if (process.Status == ImportProcessStatus.Cancelled || operation.IsCancelled)
                throw new OperationCanceledException("本地转换组件解压已取消。");
            if (process.Status == ImportProcessStatus.TimedOut)
                throw new TimeoutException("本地转换组件解压超过 60 秒，已停止本次准备。");
            if (process.Status != ImportProcessStatus.Exited || !process.CleanupSafe || process.ExitCode != 0 ||
                !String.Equals(LastNonEmptyLine(process.StandardOutput), "helper-ready", StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(targetDirectory, "BgmCryptoHelper.exe")))
                throw new InvalidOperationException("本地转换组件未能安全解压或返回有效结果。");
        }

        public static int RunHelperExtractionWorker(string[] args)
        {
            if (args == null || args.Length != 2 || args[0] != "--import-extract-helper" || !IsSafeHelperDirectory(args[1])) return 2;
            try
            {
                ExtractBundledHelperContents(args[1]);
                Console.Out.WriteLine("helper-ready");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("helper extraction failed: " + ex.GetType().Name);
                return 1;
            }
        }

        private static bool IsSafeHelperDirectory(string targetDirectory)
        {
            try
            {
                string fullTarget = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!String.Equals(Path.GetFileName(fullTarget), "helper", StringComparison.OrdinalIgnoreCase)) return false;
                DirectoryInfo jobDirectory = Directory.GetParent(fullTarget);
                if (jobDirectory == null) return false;
                string jobName = jobDirectory.Name;
                Guid jobId;
                if (jobName.Length != 36 || !jobName.StartsWith("job_", StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParseExact(jobName.Substring(4), "N", out jobId)) return false;
                string expected = Path.GetFullPath(Path.Combine(AppPaths.ImportJobPath(jobId), "helper"))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return String.Equals(fullTarget, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void ExtractBundledHelperContents(string targetDirectory)
        {
            string fullTarget = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = fullTarget + Path.DirectorySeparatorChar;
            long totalBytes = 0L;
            int entryCount = 0;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CryptoHelperArchive"))
            {
                if (stream == null) throw new InvalidOperationException("程序没有内嵌本地转换组件。");
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                {
                    Directory.CreateDirectory(fullTarget);
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (++entryCount > 512) throw new InvalidDataException("本地转换组件文件数超出限制。");
                        totalBytes = checked(totalBytes + Math.Max(0L, entry.Length));
                        if (totalBytes > 512L * 1024L * 1024L) throw new InvalidDataException("本地转换组件解压大小超出限制。");
                        string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                        string destination = Path.GetFullPath(Path.Combine(fullTarget, relative));
                        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("本地转换组件包含无效路径。");
                        if (entry.Name.Length == 0)
                        {
                            Directory.CreateDirectory(destination);
                            continue;
                        }
                        string parent = Path.GetDirectoryName(destination);
                        if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
                        using (Stream input = entry.Open())
                        using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            byte[] buffer = new byte[65536];
                            int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                output.Write(buffer, 0, count);
                            }
                        }
                    }
                }
            }
        }

        private static bool HasContainerHeader(string path, string container)
        {
            byte[] head = new byte[16];
            using (FileStream stream = File.OpenRead(path))
            {
                int read = stream.Read(head, 0, head.Length);
                if (container == "flac") return read >= 4 && StartsWith(head, read, Encoding.ASCII.GetBytes("fLaC"));
                if (container == "ogg") return read >= 4 && StartsWith(head, read, Encoding.ASCII.GetBytes("OggS"));
                if (container == "wav") return read >= 12 && StartsWith(head, read, Encoding.ASCII.GetBytes("RIFF")) && Encoding.ASCII.GetString(head, 8, 4) == "WAVE";
                if (container == "m4a") return read >= 8 && Encoding.ASCII.GetString(head, 4, 4) == "ftyp";
                if (container == "mp3") return read >= 3 && (StartsWith(head, read, Encoding.ASCII.GetBytes("ID3")) || (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0));
                if (container == "aac") return read >= 2 && head[0] == 0xFF && (head[1] & 0xF6) == 0xF0;
            }
            return false;
        }

        private static bool HasPcV1Footer(byte[] tail, int length)
        {
            if (length < 8) return false;
            int size = tail[length - 4] | (tail[length - 3] << 8) | (tail[length - 2] << 16) | (tail[length - 1] << 24);
            if (size <= 0 || size > 0x500 || size + 4 > length) return false;
            int start = length - size - 4;
            for (int i = start; i < start + size; i++)
            {
                byte c = tail[i];
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=')) return false;
            }
            return true;
        }

        private static bool StartsWith(byte[] data, int length, byte[] prefix)
        {
            if (length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++) if (data[i] != prefix[i]) return false;
            return true;
        }

        private static bool EndsWith(byte[] data, int length, byte[] suffix)
        {
            if (length < suffix.Length) return false;
            int start = length - suffix.Length;
            for (int i = 0; i < suffix.Length; i++) if (data[start + i] != suffix[i]) return false;
            return true;
        }

        private static string LastNonEmptyLine(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            string[] lines = value.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "" : lines[lines.Length - 1].Trim();
        }

        private static string ReadText(Dictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) ? System.Convert.ToString(value) ?? "" : "";
        }

        private static string Limit(string value, int length)
        {
            if (String.IsNullOrEmpty(value)) return "";
            return value.Length <= length ? value : value.Substring(0, length) + "…";
        }

        private static EncryptedConversionResult Result(EncryptedConversionStatus status, string output, string container, string variant, string message)
        {
            return new EncryptedConversionResult { Status = status, OutputPath = output, Container = container, Variant = variant, Message = message };
        }
    }
}
