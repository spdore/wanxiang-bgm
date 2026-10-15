using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace BgmHotkey
{
    internal enum AudioImportValidationStatus
    {
        Valid,
        DecoderUnavailable,
        InvalidFile,
        TimedOut,
        Cancelled,
        Failed
    }

    internal sealed class AudioImportValidationResult
    {
        public AudioImportValidationStatus Status;
        public double DurationSeconds;
        public long ElapsedMilliseconds;
        public string Message = "";
        public bool CleanupSafe = true;
    }

    internal enum ImportFilePreparationStatus
    {
        Prepared,
        TimedOut,
        Cancelled,
        Failed
    }

    internal sealed class ImportFilePreparationResult
    {
        public ImportFilePreparationStatus Status;
        public string PreparedPath = "";
        public string Message = "";
        public long ElapsedMilliseconds;
        public bool CleanupSafe = true;
    }

    internal enum AudioReuseStatus
    {
        Found,
        TimedOut,
        Cancelled,
        Failed
    }

    internal sealed class AudioReuseResult
    {
        public AudioReuseStatus Status;
        public string Path = "";
        public string Message = "";
        public long ElapsedMilliseconds;
        public bool CleanupSafe = true;
    }

    internal enum AudioFileInspectionStatus
    {
        Inspected,
        TimedOut,
        Cancelled,
        MissingOrUnreadable,
        Failed
    }

    internal sealed class AudioFileInspectionResult
    {
        public AudioFileInspectionStatus Status;
        public bool IsEncrypted;
        public long Length;
        public long ElapsedMilliseconds;
        public string Message = "";
        public bool CleanupSafe = true;
    }

    internal sealed class AudioFileFingerprintResult
    {
        public bool Success;
        public string Sha256 = "";
        public string Message = "";
        public long ElapsedMilliseconds;
        public bool CleanupSafe = true;
    }

    internal sealed class AudioLoudnessAnalysisResult
    {
        public bool Success;
        public LoudnessAnalysisResult Measurement;
        public string Fingerprint = "";
        public string Message = "";
        public long ElapsedMilliseconds;
        public bool CleanupSafe = true;
    }

    internal static class AudioImportValidator
    {
        public static AudioLoudnessAnalysisResult AnalyzeLoudness(string path, long fileLength, double targetLufs, string taskDirectory, int index, SongImportOperation operation)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            if (operation.IsCancelled) return new AudioLoudnessAnalysisResult { Message = "已取消。" };
            int timeout = ImportTimeoutPolicy.LoudnessAnalysisTotalMilliseconds(fileLength);
            string resultPath = Path.Combine(taskDirectory, "loudness_" + index.ToString("D4") + "_" + Guid.NewGuid().ToString("N") + ".json");
            string executable = Assembly.GetExecutingAssembly().Location;
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--import-loudness " + ImportProcessRunner.QuoteArgument(path) + " " +
                    ImportProcessRunner.QuoteArgument(resultPath) + " " + fileLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " +
                    targetLufs.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " " +
                    Math.Max(1000, timeout - 2000).ToString(System.Globalization.CultureInfo.InvariantCulture),
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            operation.SetStage(index, "隔离测量响度");
            ImportProcessResult process = ImportProcessRunner.Run(startInfo, operation, timeout, 0);
            AudioLoudnessAnalysisResult result = new AudioLoudnessAnalysisResult { ElapsedMilliseconds = process.ElapsedMilliseconds, CleanupSafe = process.CleanupSafe };
            if (process.Status == ImportProcessStatus.Cancelled || operation.IsCancelled)
            {
                result.Message = "已取消。";
                SafeDelete(resultPath, operation.CanCleanup);
                return result;
            }
            if (process.Status == ImportProcessStatus.TimedOut)
            {
                result.Message = "响度测量超过时间上限。";
                SafeDelete(resultPath, process.CleanupSafe && operation.CanCleanup);
                return result;
            }
            if (process.Status != ImportProcessStatus.Exited || !process.CleanupSafe || process.ExitCode != 0)
            {
                result.Message = "响度测量工作进程未能安全完成。";
                return result;
            }
            try
            {
                Dictionary<string, object> response = ReadSmallResult(resultPath);
                string status = ReadText(response, "status");
                if (status != "measured" && status != "noMeasurement") throw new InvalidDataException("响度测量结果无效。");
                string hash = ReadText(response, "sha256");
                if (hash.Length != 64) throw new InvalidDataException("响度测量结果缺少指纹。");
                foreach (char ch in hash) if (!Uri.IsHexDigit(ch)) throw new InvalidDataException("响度测量指纹格式无效。");
                result.Fingerprint = hash;
                if (status == "measured")
                {
                    result.Measurement = new LoudnessAnalysisResult
                    {
                        HasMeasurement = true,
                        IntegratedLufs = System.Convert.ToDouble(response["integratedLufs"], System.Globalization.CultureInfo.InvariantCulture),
                        PeakDbfs = System.Convert.ToDouble(response["peakDbfs"], System.Globalization.CultureInfo.InvariantCulture),
                        GainDb = System.Convert.ToDouble(response["gainDb"], System.Globalization.CultureInfo.InvariantCulture)
                    };
                }
                else result.Measurement = new LoudnessAnalysisResult { HasMeasurement = false, IntegratedLufs = -100, PeakDbfs = -100, GainDb = 0 };
                result.Success = true;
            }
            catch (Exception ex) { result.Message = "响度测量结果无效：" + ex.GetType().Name; }
            finally { SafeDelete(resultPath, operation.CanCleanup); }
            return result;
        }

        public static AudioFileFingerprintResult Fingerprint(string path, long fileLength, string taskDirectory, int index, SongImportOperation operation)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            if (operation.IsCancelled) return new AudioFileFingerprintResult { Message = "已取消。" };
            string resultPath = Path.Combine(taskDirectory, "hash_" + index.ToString("D4") + "_" + Guid.NewGuid().ToString("N") + ".json");
            string executable = Assembly.GetExecutingAssembly().Location;
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--import-hash " + ImportProcessRunner.QuoteArgument(path) + " " +
                    ImportProcessRunner.QuoteArgument(resultPath) + " " + fileLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            operation.SetStage(index, "隔离计算音频指纹");
            ImportProcessResult process = ImportProcessRunner.Run(startInfo, operation, ImportTimeoutPolicy.FingerprintTotalMilliseconds(fileLength), 0);
            AudioFileFingerprintResult result = new AudioFileFingerprintResult { ElapsedMilliseconds = process.ElapsedMilliseconds, CleanupSafe = process.CleanupSafe };
            if (process.Status == ImportProcessStatus.Cancelled || operation.IsCancelled)
            {
                result.Message = "已取消。";
                SafeDelete(resultPath, operation.CanCleanup);
                return result;
            }
            if (process.Status == ImportProcessStatus.TimedOut)
            {
                result.Message = "计算音频指纹超过时间上限。";
                SafeDelete(resultPath, process.CleanupSafe && operation.CanCleanup);
                return result;
            }
            if (process.Status != ImportProcessStatus.Exited || !process.CleanupSafe || process.ExitCode != 0)
            {
                result.Message = "指纹工作进程未能安全完成。";
                return result;
            }
            try
            {
                Dictionary<string, object> response = ReadSmallResult(resultPath);
                string status = ReadText(response, "status");
                string hash = ReadText(response, "sha256");
                if (status != "ok" || hash.Length != 64) throw new InvalidDataException("指纹结果无效。");
                foreach (char ch in hash) if (!Uri.IsHexDigit(ch)) throw new InvalidDataException("指纹结果格式无效。");
                result.Success = true;
                result.Sha256 = hash;
            }
            catch (Exception ex) { result.Message = "指纹结果无效：" + ex.GetType().Name; }
            finally { SafeDelete(resultPath, operation.CanCleanup); }
            return result;
        }

        public static AudioFileInspectionResult Inspect(string path, string taskDirectory, int index, SongImportOperation operation)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            if (operation.IsCancelled) return new AudioFileInspectionResult { Status = AudioFileInspectionStatus.Cancelled, Message = "已取消。" };
            string resultPath = Path.Combine(taskDirectory, "inspect_" + index.ToString("D4") + "_" + Guid.NewGuid().ToString("N") + ".json");
            string executable = Assembly.GetExecutingAssembly().Location;
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--import-inspect " + ImportProcessRunner.QuoteArgument(path) + " " + ImportProcessRunner.QuoteArgument(resultPath),
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            operation.SetStage(index, "识别文件格式");
            ImportProcessResult process = ImportProcessRunner.Run(startInfo, operation, ImportTimeoutPolicy.FileInspectionMilliseconds, 0);
            AudioFileInspectionResult result = new AudioFileInspectionResult
            {
                ElapsedMilliseconds = process.ElapsedMilliseconds,
                CleanupSafe = process.CleanupSafe
            };
            if (process.Status == ImportProcessStatus.Cancelled || operation.IsCancelled)
            {
                result.Status = AudioFileInspectionStatus.Cancelled;
                result.Message = "已取消。";
                SafeDelete(resultPath, operation.CanCleanup);
                return result;
            }
            if (process.Status == ImportProcessStatus.TimedOut)
            {
                result.Status = AudioFileInspectionStatus.TimedOut;
                result.Message = "文件读取或格式识别超过 30 秒。";
                SafeDelete(resultPath, process.CleanupSafe && operation.CanCleanup);
                return result;
            }
            if (process.Status != ImportProcessStatus.Exited || !process.CleanupSafe || process.ExitCode != 0)
            {
                result.Status = AudioFileInspectionStatus.Failed;
                result.Message = "格式识别工作进程未能安全完成。";
                return result;
            }
            try
            {
                Dictionary<string, object> response = ReadSmallResult(resultPath);
                string status = ReadText(response, "status");
                if (status == "missing")
                {
                    result.Status = AudioFileInspectionStatus.MissingOrUnreadable;
                    result.Message = "文件不存在或无法读取。";
                }
                else if (status == "inspected")
                {
                    object lengthValue;
                    object encryptedValue;
                    if (!response.TryGetValue("length", out lengthValue) || !response.TryGetValue("encrypted", out encryptedValue))
                        throw new InvalidDataException("格式识别结果缺少字段。");
                    result.Length = System.Convert.ToInt64(lengthValue, System.Globalization.CultureInfo.InvariantCulture);
                    result.IsEncrypted = System.Convert.ToBoolean(encryptedValue, System.Globalization.CultureInfo.InvariantCulture);
                    if (result.Length < 0) throw new InvalidDataException("格式识别结果中的文件大小无效。");
                    result.Status = AudioFileInspectionStatus.Inspected;
                }
                else
                {
                    result.Status = AudioFileInspectionStatus.Failed;
                    result.Message = "文件无法读取或格式无效。";
                }
            }
            catch (Exception ex)
            {
                result.Status = AudioFileInspectionStatus.Failed;
                result.Message = "格式识别结果无效：" + ex.GetType().Name;
            }
            finally { SafeDelete(resultPath, operation.CanCleanup); }
            return result;
        }

        public static AudioReuseResult FindReusableAudioPath(string candidatePath, long fileLength, string taskDirectory, int index, SongImportOperation operation)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            if (operation.IsCancelled) return new AudioReuseResult { Status = AudioReuseStatus.Cancelled, Message = "已取消。" };
            string resultPath = Path.Combine(taskDirectory, "reuse_" + index.ToString("D4") + "_" + Guid.NewGuid().ToString("N") + ".json");
            string executable = Assembly.GetExecutingAssembly().Location;
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--import-reuse " + ImportProcessRunner.QuoteArgument(candidatePath) + " " + ImportProcessRunner.QuoteArgument(resultPath),
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            operation.SetStage(index, "复用曲库内相同音频");
            ImportProcessResult process = ImportProcessRunner.Run(startInfo, operation,
                ImportTimeoutPolicy.ReuseTotalMilliseconds(fileLength), 0);
            AudioReuseResult result = new AudioReuseResult
            {
                ElapsedMilliseconds = process.ElapsedMilliseconds,
                CleanupSafe = process.CleanupSafe
            };
            if (process.Status == ImportProcessStatus.Cancelled || operation.IsCancelled)
            {
                result.Status = AudioReuseStatus.Cancelled;
                result.Message = "已取消。";
                SafeDelete(resultPath, operation.CanCleanup);
                return result;
            }
            if (process.Status == ImportProcessStatus.TimedOut)
            {
                result.Status = AudioReuseStatus.TimedOut;
                result.Message = "曲库查重超过时间上限。";
                SafeDelete(resultPath, process.CleanupSafe && operation.CanCleanup);
                return result;
            }
            if (process.Status != ImportProcessStatus.Exited || !process.CleanupSafe || process.ExitCode != 0)
            {
                result.Status = AudioReuseStatus.Failed;
                result.Message = "曲库查重工作进程未能安全完成。";
                return result;
            }
            try
            {
                FileInfo responseInfo = new FileInfo(resultPath);
                if (!responseInfo.Exists || responseInfo.Length <= 0 || responseInfo.Length > 65536L)
                    throw new InvalidDataException("曲库查重结果缺失或超过大小限制。");
                Dictionary<string, object> response = ReadSmallResult(resultPath);
                string status = ReadText(response, "status");
                string selectedPath = ReadText(response, "path");
                if (status != "found" || String.IsNullOrWhiteSpace(selectedPath) || selectedPath.Length > 32768)
                    throw new InvalidDataException("曲库查重结果无效。");
                string fullCandidate = Path.GetFullPath(candidatePath);
                string fullSelected = Path.GetFullPath(selectedPath);
                string libraryRoot = Path.GetFullPath(AppPaths.TrackPath("")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!String.Equals(fullCandidate, fullSelected, StringComparison.OrdinalIgnoreCase) &&
                    !fullSelected.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("曲库查重结果越出允许目录。");
                if (!File.Exists(fullSelected) || !AudioFormatCatalog.IsSupportedExtension(fullSelected))
                    throw new InvalidDataException("曲库查重文件已不存在或格式不受支持。");
                result.Status = AudioReuseStatus.Found;
                result.Path = fullSelected;
            }
            catch (Exception ex)
            {
                result.Status = AudioReuseStatus.Failed;
                result.Message = "曲库查重结果无效：" + ex.GetType().Name;
            }
            finally { SafeDelete(resultPath, operation.CanCleanup); }
            return result;
        }

        public static ImportFilePreparationResult PrepareLibraryCopy(string sourcePath, long fileLength, int index, SongImportOperation operation)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            if (operation.IsCancelled) return new ImportFilePreparationResult { Status = ImportFilePreparationStatus.Cancelled, Message = "已取消。" };
            if (SongLibrary.IsLibraryAudioLocation(sourcePath))
                return new ImportFilePreparationResult { Status = ImportFilePreparationStatus.Prepared, PreparedPath = "" };

            string libraryFolder = AppPaths.TrackPath("");
            Directory.CreateDirectory(libraryFolder);
            string preparedPath = AppPaths.TrackPath(".import_" + operation.Id.ToString("N") + "_" + index.ToString("D4") + ".partial");
            if (File.Exists(preparedPath)) throw new IOException("本次导入临时文件已存在。");

            string executable = Assembly.GetExecutingAssembly().Location;
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--import-copy " + ImportProcessRunner.QuoteArgument(sourcePath) + " " + ImportProcessRunner.QuoteArgument(preparedPath) + " " +
                    fileLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            operation.SetStage(index, "复制到曲库临时区");
            ImportProcessResult process = ImportProcessRunner.Run(startInfo, operation,
                ImportTimeoutPolicy.CopyTotalMilliseconds(fileLength), ImportTimeoutPolicy.CopyNoProgressMilliseconds);
            ImportFilePreparationResult result = new ImportFilePreparationResult
            {
                ElapsedMilliseconds = process.ElapsedMilliseconds,
                CleanupSafe = process.CleanupSafe
            };
            if (process.Status == ImportProcessStatus.Cancelled || operation.IsCancelled)
            {
                result.Status = ImportFilePreparationStatus.Cancelled;
                result.Message = "已取消。";
            }
            else if (process.Status == ImportProcessStatus.TimedOut)
            {
                result.Status = ImportFilePreparationStatus.TimedOut;
                result.Message = ImportTimeoutPolicy.CopyNoProgressMilliseconds > 0 && process.Error == "no-progress"
                    ? "文件读取超过 120 秒没有进展，已停止本次导入。"
                    : "文件复制超过时间上限，已停止本次导入。";
            }
            else if (process.Status == ImportProcessStatus.Exited && process.CleanupSafe && process.ExitCode == 0 && File.Exists(preparedPath))
            {
                try
                {
                    FileInfo preparedInfo = new FileInfo(preparedPath);
                    if (preparedInfo.Length == fileLength) result.Status = ImportFilePreparationStatus.Prepared;
                    else
                    {
                        result.Status = ImportFilePreparationStatus.Failed;
                        result.Message = "复制结果长度与源文件不一致。";
                    }
                }
                catch (Exception ex)
                {
                    result.Status = ImportFilePreparationStatus.Failed;
                    result.Message = ex.GetType().Name;
                }
            }
            else
            {
                result.Status = ImportFilePreparationStatus.Failed;
                result.Message = "曲库临时文件未能完整生成。";
            }

            if (result.Status != ImportFilePreparationStatus.Prepared)
                SafeDelete(preparedPath, result.CleanupSafe && operation.CanCleanup);
            else result.PreparedPath = preparedPath;
            return result;
        }

        public static AudioImportValidationResult Validate(string path, long fileLength, bool completeDecode, string taskDirectory, int index, SongImportOperation operation)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            if (operation.IsCancelled) return new AudioImportValidationResult { Status = AudioImportValidationStatus.Cancelled, Message = "已取消。" };

            int timeout = completeDecode
                ? ImportTimeoutPolicy.ValidationFullTotalMilliseconds(fileLength)
                : ImportTimeoutPolicy.ValidationProbeTotalMilliseconds(fileLength);
            string mode = completeDecode ? "full" : "probe";
            string resultPath = Path.Combine(taskDirectory, "validation_" + index.ToString("D4") + "_" + Guid.NewGuid().ToString("N") + ".json");
            string executable = Assembly.GetExecutingAssembly().Location;
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--import-validate " + ImportProcessRunner.QuoteArgument(mode) + " " +
                    ImportProcessRunner.QuoteArgument(path) + " " + ImportProcessRunner.QuoteArgument(resultPath) + " " +
                    Math.Max(1000, timeout - 2000).ToString(System.Globalization.CultureInfo.InvariantCulture),
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            operation.SetStage(index, completeDecode ? "完整解码验证" : "检查系统解码器");
            ImportProcessResult process = ImportProcessRunner.Run(startInfo, operation, timeout, 0);
            AudioImportValidationResult result = new AudioImportValidationResult
            {
                ElapsedMilliseconds = process.ElapsedMilliseconds,
                CleanupSafe = process.CleanupSafe
            };
            if (process.Status == ImportProcessStatus.Cancelled || operation.IsCancelled)
            {
                result.Status = AudioImportValidationStatus.Cancelled;
                result.Message = "已取消。";
                SafeDelete(resultPath, operation.CanCleanup);
                return result;
            }
            if (process.Status == ImportProcessStatus.TimedOut)
            {
                result.Status = AudioImportValidationStatus.TimedOut;
                result.Message = "音频验证超过时间上限。";
                SafeDelete(resultPath, process.CleanupSafe && operation.CanCleanup);
                return result;
            }
            if (process.Status != ImportProcessStatus.Exited || !process.CleanupSafe)
            {
                result.Status = AudioImportValidationStatus.Failed;
                result.Message = "验证工作进程无法安全回收。";
                return result;
            }

            try
            {
                if (process.ExitCode != 0 || !File.Exists(resultPath))
                {
                    result.Status = AudioImportValidationStatus.Failed;
                    result.Message = "验证工作进程未返回有效结果。";
                    return result;
                }
                Dictionary<string, object> response = ReadSmallResult(resultPath);
                string status = ReadText(response, "status");
                if (status == "valid")
                {
                    object durationValue;
                    if (!response.TryGetValue("durationSeconds", out durationValue)) throw new InvalidDataException("验证结果缺少音频时长。");
                    double duration = System.Convert.ToDouble(durationValue, System.Globalization.CultureInfo.InvariantCulture);
                    if (duration <= 0 || Double.IsNaN(duration) || Double.IsInfinity(duration)) throw new InvalidDataException("验证结果中的音频时长无效。");
                    result.Status = AudioImportValidationStatus.Valid;
                    result.DurationSeconds = duration;
                }
                else if (status == "decoderUnavailable")
                {
                    result.Status = AudioImportValidationStatus.DecoderUnavailable;
                    result.Message = "当前 Windows 没有可用的音频解码器。";
                }
                else if (status == "invalidFile")
                {
                    result.Status = AudioImportValidationStatus.InvalidFile;
                    result.Message = "音频内容损坏或不完整。";
                }
                else if (status == "timedOut")
                {
                    result.Status = AudioImportValidationStatus.TimedOut;
                    result.Message = "音频验证超过时间上限。";
                }
                else
                {
                    result.Status = AudioImportValidationStatus.Failed;
                    result.Message = "验证工作进程返回了未知状态。";
                }
            }
            catch (Exception ex)
            {
                result.Status = AudioImportValidationStatus.Failed;
                result.Message = "验证结果无效：" + ex.GetType().Name;
            }
            finally { SafeDelete(resultPath, operation.CanCleanup); }
            return result;
        }

        public static int RunWorker(string[] args)
        {
            if (args == null || args.Length != 5 || args[0] != "--import-validate") return 2;
            string mode = args[1];
            string path = args[2];
            string resultPath = args[3];
            int timeout;
            if ((mode != "probe" && mode != "full") || !Int32.TryParse(args[4], out timeout) || timeout < 1000) return 2;
            if (!IsSafeResultPath(resultPath, "validation_", ".json")) return 2;

            ValidationWork work = new ValidationWork { Path = path, Complete = mode == "full" };
            Thread decodeThread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    work.DurationSeconds = work.Complete
                        ? AudioFormatCatalog.ValidateCompleteAudio(work.Path, null)
                        : AudioFormatCatalog.ProbeDurationSeconds(work.Path);
                    work.Status = "valid";
                }
                catch (Exception ex)
                {
                    work.Status = LooksLikeDecoderUnavailable(ex) ? "decoderUnavailable" : "invalidFile";
                    work.ErrorType = ex.GetType().Name;
                }
            }));
            decodeThread.IsBackground = true;
            decodeThread.Name = "Isolated Media Foundation validation";
            decodeThread.SetApartmentState(ApartmentState.MTA);
            decodeThread.Start();

            // This thread lives only in the disposable validator process. If a native
            // Media Foundation call blocks, process isolation supplies the hard stop.
            if (!decodeThread.Join(timeout))
            {
                WriteWorkerResult(resultPath, new Dictionary<string, object> { { "status", "timedOut" } });
                return 0;
            }
            Dictionary<string, object> response = new Dictionary<string, object> { { "status", work.Status ?? "invalidFile" } };
            if (work.Status == "valid") response["durationSeconds"] = work.DurationSeconds;
            else response["errorType"] = work.ErrorType ?? "";
            WriteWorkerResult(resultPath, response);
            return 0;
        }

        public static int RunCopyWorker(string[] args)
        {
            if (args == null || args.Length != 4 || args[0] != "--import-copy") return 2;
            string source = Path.GetFullPath(args[1]);
            string destination = Path.GetFullPath(args[2]);
            long expectedLength;
            if (!Int64.TryParse(args[3], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out expectedLength) || expectedLength < 0) return 2;
            string libraryRoot = Path.GetFullPath(AppPaths.TrackPath("")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(libraryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(destination).StartsWith(".import_", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetExtension(destination).Equals(".partial", StringComparison.OrdinalIgnoreCase)) return 2;

            long copied = 0L;
            long nextProgress = 4L * 1024L * 1024L;
            try
            {
                FileInfo sourceInfo = new FileInfo(source);
                if (!sourceInfo.Exists || sourceInfo.Length != expectedLength) return 1;
                using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan))
                using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[131072];
                    int count;
                    Stream progress = Console.OpenStandardOutput();
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, count);
                        copied += count;
                        if (copied > expectedLength) throw new InvalidDataException("源文件在复制期间变大。");
                        if (copied >= nextProgress)
                        {
                            byte[] line = Encoding.ASCII.GetBytes("IMPORT_PROGRESS:" + copied.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
                            try { progress.Write(line, 0, line.Length); progress.Flush(); } catch { }
                            nextProgress = copied + 4L * 1024L * 1024L;
                        }
                    }
                    output.Flush(true);
                }
                if (copied != expectedLength) throw new InvalidDataException("源文件在复制期间发生变化。");
                return 0;
            }
            catch
            {
                try { if (File.Exists(destination)) File.Delete(destination); } catch { }
                return 1;
            }
        }

        public static int RunHashWorker(string[] args)
        {
            if (args == null || args.Length != 4 || args[0] != "--import-hash") return 2;
            string source = Path.GetFullPath(args[1]);
            string resultPath = Path.GetFullPath(args[2]);
            long expectedLength;
            if (!Int64.TryParse(args[3], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out expectedLength) || expectedLength < 0 ||
                !IsSafeResultPath(resultPath, "hash_", ".json")) return 2;
            try
            {
                FileInfo before = new FileInfo(source);
                if (!before.Exists || before.Length != expectedLength) throw new IOException("源文件长度与预期不符。");
                DateTime write = before.LastWriteTimeUtc;
                string hash = LoudnessAnalyzer.Fingerprint(source);
                FileInfo after = new FileInfo(source);
                if (!after.Exists || after.Length != expectedLength || after.LastWriteTimeUtc != write) throw new IOException("计算指纹期间源文件发生变化。");
                WriteWorkerResult(resultPath, new Dictionary<string, object> { { "status", "ok" }, { "sha256", hash } });
                return 0;
            }
            catch (Exception ex)
            {
                try { WriteWorkerResult(resultPath, new Dictionary<string, object> { { "status", "failed" }, { "errorType", ex.GetType().Name } }); } catch { }
                return 1;
            }
        }

        public static int RunLoudnessWorker(string[] args)
        {
            if (args == null || args.Length != 6 || args[0] != "--import-loudness") return 2;
            string source = Path.GetFullPath(args[1]);
            string resultPath = Path.GetFullPath(args[2]);
            long expectedLength;
            double target;
            int timeout;
            if (!Int64.TryParse(args[3], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out expectedLength) || expectedLength < 0 ||
                !Double.TryParse(args[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out target) ||
                !Int32.TryParse(args[5], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out timeout) || timeout < 1000 ||
                !IsSafeResultPath(resultPath, "loudness_", ".json")) return 2;

            Dictionary<string, object> response = null;
            Exception workerError = null;
            Thread analyzer = new Thread(new ThreadStart(delegate
            {
                try
                {
                    FileInfo before = new FileInfo(source);
                    if (!before.Exists || before.Length != expectedLength) throw new IOException("源文件长度与预期不符。");
                    DateTime write = before.LastWriteTimeUtc;
                    string fingerprint = LoudnessAnalyzer.Fingerprint(source);
                    LoudnessAnalysisResult measured = LoudnessAnalyzer.Analyze(source, target);
                    FileInfo after = new FileInfo(source);
                    if (!after.Exists || after.Length != expectedLength || after.LastWriteTimeUtc != write) throw new IOException("响度测量期间源文件发生变化。");
                    response = new Dictionary<string, object>
                    {
                        { "status", measured.HasMeasurement ? "measured" : "noMeasurement" },
                        { "sha256", fingerprint }, { "integratedLufs", measured.IntegratedLufs },
                        { "peakDbfs", measured.PeakDbfs }, { "gainDb", measured.GainDb }
                    };
                }
                catch (Exception ex) { workerError = ex; }
            }));
            analyzer.IsBackground = true;
            analyzer.Name = "Isolated loudness analysis";
            analyzer.Start();
            if (!analyzer.Join(timeout))
            {
                WriteWorkerResult(resultPath, new Dictionary<string, object> { { "status", "timedOut" } });
                return 0;
            }
            if (workerError != null)
            {
                try { WriteWorkerResult(resultPath, new Dictionary<string, object> { { "status", "failed" }, { "errorType", workerError.GetType().Name } }); } catch { }
                return 1;
            }
            WriteWorkerResult(resultPath, response);
            return 0;
        }

        public static int RunInspectWorker(string[] args)
        {
            if (args == null || args.Length != 3 || args[0] != "--import-inspect") return 2;
            string source = Path.GetFullPath(args[1]);
            string resultPath = Path.GetFullPath(args[2]);
            if (!IsSafeResultPath(resultPath, "inspect_", ".json")) return 2;
            try
            {
                FileInfo info = new FileInfo(source);
                if (!info.Exists)
                {
                    WriteWorkerResult(resultPath, new Dictionary<string, object> { { "status", "missing" } });
                    return 0;
                }
                bool encrypted = EncryptedAudioConverter.IsEncryptedCandidate(source);
                WriteWorkerResult(resultPath, new Dictionary<string, object>
                {
                    { "status", "inspected" },
                    { "length", info.Length },
                    { "encrypted", encrypted }
                });
                return 0;
            }
            catch (Exception ex)
            {
                try { WriteWorkerResult(resultPath, new Dictionary<string, object> { { "status", "failed" }, { "errorType", ex.GetType().Name } }); } catch { }
                return 1;
            }
        }

        public static int RunReuseWorker(string[] args)
        {
            if (args == null || args.Length != 3 || args[0] != "--import-reuse") return 2;
            string source = Path.GetFullPath(args[1]);
            string resultPath = Path.GetFullPath(args[2]);
            if (!IsSafeResultPath(resultPath, "reuse_", ".json")) return 2;
            try
            {
                string reusablePath = SongLibrary.FindReusableAudioPath(source, null);
                WriteWorkerResult(resultPath, new Dictionary<string, object> { { "status", "found" }, { "path", Path.GetFullPath(reusablePath) } });
                return 0;
            }
            catch (Exception ex)
            {
                try { WriteWorkerResult(resultPath, new Dictionary<string, object> { { "status", "failed" }, { "errorType", ex.GetType().Name } }); } catch { }
                return 1;
            }
        }

        private static bool IsSafeResultPath(string resultPath, string expectedPrefix, string expectedExtension)
        {
            string root = Path.GetFullPath(Path.Combine(AppPaths.SettingsDirectory, "import-work")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(resultPath);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            string directory = Path.GetDirectoryName(resultPath) ?? "";
            string name = Path.GetFileName(directory);
            if (name.Length == 36 && name.StartsWith("job_", StringComparison.OrdinalIgnoreCase))
            {
                Guid parsed;
                if (Guid.TryParseExact(name.Substring(4), "N", out parsed))
                    return String.Equals(Path.GetExtension(full), expectedExtension, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetFileName(full).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static Dictionary<string, object> ReadSmallResult(string path)
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > 65536L)
                throw new InvalidDataException("导入工作进程结果缺失或超过大小限制。");
            return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
        }

        private static bool LooksLikeDecoderUnavailable(Exception exception)
        {
            Exception current = exception;
            while (current != null)
            {
                int code = current.HResult;
                if (code == unchecked((int)0xC00D36C4) || code == unchecked((int)0xC00D5212) || code == unchecked((int)0x80040154))
                    return true;
                string message = current.Message ?? "";
                if (message.IndexOf("no decoder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("decoder unavailable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("class not registered", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("没有此格式的解码器", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("解码器不可用", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                current = current.InnerException;
            }
            return false;
        }

        private static void WriteWorkerResult(string path, Dictionary<string, object> response)
        {
            string json = new JavaScriptSerializer().Serialize(response);
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(json);
        }

        private static string ReadText(Dictionary<string, object> data, string key)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) ? System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "" : "";
        }

        private static void SafeDelete(string path, bool safe)
        {
            if (!safe) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private sealed class ValidationWork
        {
            public string Path;
            public bool Complete;
            public string Status;
            public string ErrorType;
            public double DurationSeconds;
        }
    }
}
