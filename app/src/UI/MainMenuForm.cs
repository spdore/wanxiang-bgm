using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BgmHotkey
{
    internal sealed partial class MainMenuForm : Form
    {
        private readonly AppSettings _settings;
        private readonly AudioDeviceCatalog _devices;
        private readonly AudioEngine _engine;
        private readonly Label _status;
        private readonly ComboBox _microphone;
        private readonly ComboBox _headphone;
        private readonly ComboBox _cable;
        private readonly TrackBar _headphoneVolume;
        private readonly TrackBar _cableVolume;
        private readonly Label _headphoneValue;
        private readonly Label _cableValue;
        private bool _initializing;
        private volatile bool _capturingKey;
        private readonly object _keyCaptureGate = new object();
        private readonly HashSet<string> _keyCaptureReasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly ProgressBar _microphoneMeter;
        private readonly ProgressBar _musicMeter;
        private readonly ProgressBar _outputMeter;
        private readonly Label _routeResult;
        private readonly Button _routeCheck;
        private readonly Timer _meterTimer;
        private SongImportOperation _activeSongImport;
        private SongImportProgressState _songImportState;
        private readonly List<SongImportResultItem> _songImportResults = new List<SongImportResultItem>();
        private volatile bool _shuttingDown;

        private void RebuildBindings()
        {
            RefreshStatus();
        }

        private void SetKeyCapture(string reason, bool active)
        {
            if (String.IsNullOrWhiteSpace(reason)) reason = "unspecified";
            lock (_keyCaptureGate)
            {
                if (active) _keyCaptureReasons.Add(reason);
                else _keyCaptureReasons.Remove(reason);
                _capturingKey = _keyCaptureReasons.Count > 0;
            }
        }

        private void AddSongs(object sender, EventArgs e)
        {
            if (_activeSongImport != null)
            {
                NotifyWeb("歌曲导入正在进行，请先完成或取消当前任务。");
                return;
            }

            string initialLibraryFolder = AppPaths.TrackPath("");
            try { Directory.CreateDirectory(initialLibraryFolder); }
            catch (Exception ex) { ShowLibraryNotice("无法打开曲库文件夹：" + ex.Message, true); return; }

            using (OpenFileDialog picker = new OpenFileDialog
            {
                Title = "添加音频",
                Filter = AudioFormatCatalog.FileDialogFilter,
                InitialDirectory = initialLibraryFolder,
                RestoreDirectory = true,
                Multiselect = true,
                CheckFileExists = true
            })
            {
                DialogResult picked;
                SetKeyCapture("file-picker", true);
                try { picked = picker.ShowDialog(this); }
                finally { SetKeyCapture("file-picker", false); }
                if (picked != DialogResult.OK) return;

                string[] paths = (string[])picker.FileNames.Clone();
                SongImportOperation operation = new SongImportOperation();
                _activeSongImport = operation;
                SetKeyCapture("song-import", true);
                _songImportResults.Clear();
                _songImportState = new SongImportProgressState
                {
                    OperationId = operation.Id.ToString("N"),
                    Active = true,
                    Total = paths.Length,
                    Current = 0,
                    Stage = "正在准备任务…",
                    Summary = "源文件只读检查；转换结果将复制到曲库。"
                };
                for (int i = 0; i < paths.Length; i++)
                    _songImportResults.Add(new SongImportResultItem
                    {
                        Index = i + 1,
                        FileName = Path.GetFileName(paths[i]),
                        Status = "等待处理",
                        Detail = "等待导入任务处理此文件。",
                        Success = true,
                        Pending = true,
                        Outcome = "pending"
                    });
                _songImportState.Results = _songImportResults.ToArray();
                operation.StageChanged += delegate(int fileIndex, string stage)
                {
                    string activeFile = fileIndex > 0 && fileIndex <= paths.Length ? Path.GetFileName(paths[fileIndex - 1]) : "";
                    PostSongImportUi(operation, delegate
                    {
                        if (_songImportState.Complete) return;
                        _songImportState.Current = fileIndex;
                        _songImportState.Total = paths.Length;
                        _songImportState.CurrentFile = activeFile;
                        _songImportState.Stage = stage;
                        PublishWebState();
                    });
                };
                PublishWebState();

                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    string taskDirectory = AppPaths.ImportJobPath(operation.Id);
                    List<ImportedAudioCandidate> playable = new List<ImportedAudioCandidate>();
                    try
                    {
                        Directory.CreateDirectory(taskDirectory);
                        for (int i = 0; i < paths.Length; i++)
                        {
                            string path = paths[i];
                            if (operation.IsCancelled) break;
                            string fileName = Path.GetFileName(path);
                            ReportSongImportProgress(operation, i + 1, "检查格式");
                            try
                            {
                                AudioFileInspectionResult inspection = AudioImportValidator.Inspect(path, taskDirectory, i + 1, operation);
                                if (inspection.Status == AudioFileInspectionStatus.Cancelled) break;
                                if (inspection.Status != AudioFileInspectionStatus.Inspected)
                                {
                                    AddSongImportResult(operation, new SongImportResultItem
                                    {
                                        Index = i + 1,
                                        FileName = fileName,
                                        Status = inspection.Status == AudioFileInspectionStatus.TimedOut ? "检查超时" : "未导入",
                                        Detail = inspection.Status == AudioFileInspectionStatus.TimedOut
                                            ? "文件读取或格式识别超过 30 秒，已停止本次检查；源文件未修改。"
                                            : "文件不存在、无法读取或格式无效；源文件未修改。",
                                        TechnicalDetail = inspection.Status + ": " + (inspection.Message ?? ""),
                                        Success = false
                                    });
                                    if (!inspection.CleanupSafe || !operation.CanCleanup) break;
                                    continue;
                                }
                                string importPath = path;
                                string outputContainer = "";
                                bool converted = false;
                                double duration;
                                if (inspection.IsEncrypted)
                                {
                                    ReportSongImportProgress(operation, i + 1, "本地转换");
                                    EncryptedConversionResult conversion = EncryptedAudioConverter.ConvertDetected(path, taskDirectory, i + 1, inspection.Length, operation);
                                    if (conversion.Status == EncryptedConversionStatus.Cancelled) break;
                                    if (conversion.Status == EncryptedConversionStatus.KeyUnavailable ||
                                        conversion.Status == EncryptedConversionStatus.UnsupportedVariant ||
                                        conversion.Status == EncryptedConversionStatus.InvalidFile ||
                                        conversion.Status == EncryptedConversionStatus.TimedOut ||
                                        conversion.Status == EncryptedConversionStatus.FileTooLarge ||
                                        conversion.Status == EncryptedConversionStatus.Failed)
                                    {
                                        string status = conversion.Status == EncryptedConversionStatus.TimedOut ? "转换超时" :
                                            conversion.Status == EncryptedConversionStatus.FileTooLarge ? "文件过大" : "未导入";
                                        AddSongImportResult(operation, new SongImportResultItem
                                        {
                                            Index = i + 1,
                                            FileName = fileName,
                                            Status = status,
                                            Detail = DescribeEncryptedImportFailure(conversion.Status),
                                            TechnicalDetail = conversion.Status + ": " + (conversion.Message ?? ""),
                                            Success = false
                                        });
                                        if (!operation.CanCleanup) break;
                                        continue;
                                    }
                                    if (conversion.Status == EncryptedConversionStatus.Converted)
                                    {
                                        importPath = conversion.OutputPath;
                                        outputContainer = conversion.Container;
                                        converted = true;
                                        ReportSongImportProgress(operation, i + 1, "完整解码验证");
                                        long convertedLength = new FileInfo(importPath).Length;
                                        AudioImportValidationResult validation = AudioImportValidator.Validate(importPath, convertedLength, true, taskDirectory, i + 1, operation);
                                        if (validation.Status == AudioImportValidationStatus.Cancelled) break;
                                        if (validation.Status != AudioImportValidationStatus.Valid)
                                        {
                                            AddSongImportResult(operation, new SongImportResultItem
                                            {
                                                Index = i + 1,
                                                FileName = fileName,
                                                Status = validation.Status == AudioImportValidationStatus.TimedOut ? "验证超时" : "未导入",
                                                Detail = DescribeAudioValidationFailure(validation.Status, true, conversion.Container),
                                                TechnicalDetail = validation.Status + ": " + (validation.Message ?? ""),
                                                Success = false
                                            });
                                            if (!operation.CanCleanup) break;
                                            continue;
                                        }
                                        duration = validation.DurationSeconds;
                                        if (operation.IsCancelled) break;
                                        AudioReuseResult reuse = AudioImportValidator.FindReusableAudioPath(importPath, convertedLength, taskDirectory, i + 1, operation);
                                        if (reuse.Status == AudioReuseStatus.Cancelled) break;
                                        if (reuse.Status != AudioReuseStatus.Found)
                                        {
                                            AddSongImportResult(operation, new SongImportResultItem
                                            {
                                                Index = i + 1,
                                                FileName = fileName,
                                                Status = reuse.Status == AudioReuseStatus.TimedOut ? "查重超时" : "未导入",
                                                Detail = reuse.Status == AudioReuseStatus.TimedOut
                                                    ? "曲库查重超过时间上限，未导入；源文件未修改。"
                                                    : "曲库查重未能完成，未导入；源文件未修改。",
                                                TechnicalDetail = reuse.Status + ": " + (reuse.Message ?? ""),
                                                Success = false
                                            });
                                            if (!reuse.CleanupSafe || !operation.CanCleanup) break;
                                            continue;
                                        }
                                        importPath = reuse.Path;
                                    }
                                    else
                                    {
                                        ReportSongImportProgress(operation, i + 1, "检查系统解码器");
                                        AudioImportValidationResult validation = AudioImportValidator.Validate(importPath, inspection.Length, false, taskDirectory, i + 1, operation);
                                        if (validation.Status == AudioImportValidationStatus.Cancelled) break;
                                        if (validation.Status != AudioImportValidationStatus.Valid)
                                        {
                                            AddSongImportResult(operation, new SongImportResultItem
                                            {
                                                Index = i + 1,
                                                FileName = fileName,
                                                Status = validation.Status == AudioImportValidationStatus.TimedOut ? "验证超时" : "未导入",
                                                Detail = DescribeAudioValidationFailure(validation.Status, false, ""),
                                                TechnicalDetail = validation.Status + ": " + (validation.Message ?? ""),
                                                Success = false
                                            });
                                            if (!operation.CanCleanup) break;
                                            continue;
                                        }
                                        duration = validation.DurationSeconds;
                                    }
                                }
                                else
                                {
                                    ReportSongImportProgress(operation, i + 1, "检查系统解码器");
                                    AudioImportValidationResult validation = AudioImportValidator.Validate(importPath, inspection.Length, false, taskDirectory, i + 1, operation);
                                    if (validation.Status == AudioImportValidationStatus.Cancelled) break;
                                    if (validation.Status != AudioImportValidationStatus.Valid)
                                    {
                                        AddSongImportResult(operation, new SongImportResultItem
                                        {
                                            Index = i + 1,
                                            FileName = fileName,
                                            Status = validation.Status == AudioImportValidationStatus.TimedOut ? "验证超时" : "未导入",
                                            Detail = DescribeAudioValidationFailure(validation.Status, false, ""),
                                            TechnicalDetail = validation.Status + ": " + (validation.Message ?? ""),
                                            Success = false
                                        });
                                        if (!operation.CanCleanup) break;
                                        continue;
                                    }
                                    duration = validation.DurationSeconds;
                                }

                                if (operation.IsCancelled) break;
                                ReportSongImportProgress(operation, i + 1, "准备曲库文件");
                                ImportFilePreparationResult preparation = AudioImportValidator.PrepareLibraryCopy(importPath,
                                    converted ? new FileInfo(importPath).Length : inspection.Length, i + 1, operation);
                                if (preparation.Status == ImportFilePreparationStatus.Cancelled) break;
                                if (preparation.Status != ImportFilePreparationStatus.Prepared)
                                {
                                    AddSongImportResult(operation, new SongImportResultItem
                                    {
                                        Index = i + 1,
                                        FileName = fileName,
                                        Status = preparation.Status == ImportFilePreparationStatus.TimedOut ? "复制超时" : "未导入",
                                        Detail = preparation.Status == ImportFilePreparationStatus.TimedOut
                                            ? "准备曲库文件超过时间上限；源文件未修改。"
                                            : "无法安全准备曲库文件；源文件未修改。",
                                        TechnicalDetail = preparation.Status + ": " + (preparation.Message ?? ""),
                                        Success = false
                                    });
                                    if (!operation.CanCleanup) break;
                                    continue;
                                }

                                playable.Add(new ImportedAudioCandidate
                                {
                                    SourcePath = path,
                                    LibraryPath = importPath,
                                    LibraryFileName = Path.GetFileName(importPath),
                                    PreparedPath = preparation.PreparedPath,
                                    FileName = fileName,
                                    ImportIndex = i + 1,
                                    DurationSeconds = duration,
                                    WasConverted = converted
                                });
                                AddSongImportResult(operation, new SongImportResultItem
                                {
                                    Index = i + 1,
                                    FileName = fileName,
                                    Status = "等待写入曲库",
                                    Detail = converted ? "已转换为 " + outputContainer.ToUpperInvariant() + " 并通过完整解码验证。" : "音频格式已识别。",
                                    Success = true,
                                    Pending = true
                                });
                                if (!operation.CanCleanup) break;
                            }
                            catch (OperationCanceledException) { break; }
                            catch (Exception ex)
                            {
                                AddSongImportResult(operation, new SongImportResultItem
                                {
                                    Index = i + 1,
                                    FileName = fileName,
                                    Status = "未导入",
                                    Detail = "文件无法读取或格式无效；源文件未修改。",
                                    TechnicalDetail = ex.Message,
                                    Success = false
                                });
                                if (!operation.CanCleanup) break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AddSongImportResult(operation, new SongImportResultItem
                        {
                            Index = 0,
                            FileName = "导入任务",
                            Status = "任务失败",
                            Detail = "导入任务无法继续；源文件未修改。",
                            TechnicalDetail = ex.Message,
                            Success = false
                        });
                    }

                    if (IsDisposed || !IsHandleCreated)
                    {
                        QueueSongImportCleanup(operation, taskDirectory, playable);
                        return;
                    }
                    try
                    {
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            if (_shuttingDown || IsDisposed || !IsCurrentSongImport(operation))
                            {
                                if (Object.ReferenceEquals(_activeSongImport, operation)) _activeSongImport = null;
                                QueueSongImportCleanup(operation, taskDirectory, playable);
                                return;
                            }
                            if (operation.IsCancelled)
                            {
                                _activeSongImport = null;
                                FinalizeSongImport(operation, true, 0, 0);
                                PublishWebState();
                                QueueSongImportCleanup(operation, taskDirectory, playable);
                                return;
                            }

                            List<string> newTrackFiles = new List<string>();
                            List<ImportedAudioCandidate> newlyAddedCandidates = new List<ImportedAudioCandidate>();
                            int addedTrackCount = 0;
                            bool settingsChanged = false;
                            SavedTrack[] previousAssets = (SavedTrack[])_settings.Tracks.Clone();
                            SavedTrack[] previousTracks = _settings.ActiveTracks();
                            ScenePreset previousScene = _settings.CloneActiveScene();

                            foreach (ImportedAudioCandidate candidate in playable)
                            {
                                try
                                {
                                    SavedTrack track = String.IsNullOrEmpty(candidate.PreparedPath)
                                        ? SongLibrary.Add(_settings, candidate.LibraryPath)
                                        : SongLibrary.AddPrepared(_settings, candidate.PreparedPath, candidate.LibraryFileName);
                                    if (track == null)
                                    {
                                        SetSongImportResult(operation, candidate.ImportIndex, false, false, "未导入", "音频未能加入当前场景；源文件未修改。", "SongLibrary.Add returned no track.");
                                        continue;
                                    }
                                    bool wasPresent = Array.Find(previousTracks, delegate(SavedTrack item)
                                    {
                                        return item != null && String.Equals(item.Id, track.Id, StringComparison.OrdinalIgnoreCase);
                                    }) != null;
                                    if (!wasPresent)
                                    {
                                        settingsChanged = true;
                                        addedTrackCount++;
                                        newlyAddedCandidates.Add(candidate);
                                        candidate.AddedFileName = track.FileName;
                                        if (!newTrackFiles.Contains(track.FileName)) newTrackFiles.Add(track.FileName);
                                    }
                                    SetSongImportResult(operation, candidate.ImportIndex, true, false,
                                        wasPresent ? "已在当前场景中" : "已加入当前场景",
                                        wasPresent ? "相同音频已存在，本次复用了曲库项目。" : "音频已写入曲库并保存到当前场景。", "");
                                }
                                catch (Exception ex)
                                {
                                    SetSongImportResult(operation, candidate.ImportIndex, false, false, "未导入",
                                        "无法写入曲库；源文件未修改。", ex.Message);
                                }
                            }

                            if (settingsChanged)
                            {
                                string error;
                                if (!SettingsStore.TrySave(_settings, out error))
                                {
                                    _settings.Tracks = previousAssets;
                                    _settings.RestoreScene(previousScene);
                                    _settings.Normalize();
                                    foreach (ImportedAudioCandidate candidate in newlyAddedCandidates)
                                    {
                                        if (String.IsNullOrEmpty(candidate.PreparedPath) || String.IsNullOrEmpty(candidate.AddedFileName)) continue;
                                        try
                                        {
                                            string copied = AppPaths.TrackPath(candidate.AddedFileName);
                                            if (File.Exists(copied) && !File.Exists(candidate.PreparedPath)) File.Move(copied, candidate.PreparedPath);
                                        }
                                        catch (Exception rollbackError)
                                        {
                                            AppPaths.Log("回滚歌曲导入文件失败 operation=" + operation.Id.ToString("N") + " type=" + rollbackError.GetType().Name);
                                        }
                                    }
                                    foreach (ImportedAudioCandidate candidate in newlyAddedCandidates)
                                        SetSongImportResult(operation, candidate.ImportIndex, false, false, "保存失败",
                                            "曲库保存失败，新增项目已回滚；源文件未修改。", error);
                                    newTrackFiles.Clear();
                                    addedTrackCount = 0;
                                }
                            }

                            _activeSongImport = null;
                            RefreshWebFiles();
                            if (newTrackFiles.Count > 0) QueueLoudnessAnalysis(newTrackFiles);
                            RebuildBindings();
                            FinalizeSongImport(operation, operation.IsCancelled, addedTrackCount, 0);
                            PublishWebState();
                            QueueSongImportCleanup(operation, taskDirectory, playable);
                        }));
                    }
                    catch (InvalidOperationException)
                    {
                        QueueSongImportCleanup(operation, taskDirectory, playable);
                    }
                });
            }
        }

        private static void QueueSongImportCleanup(SongImportOperation operation, string taskDirectory, List<ImportedAudioCandidate> candidates)
        {
            if (operation == null) return;
            if (!operation.CanCleanup)
            {
                AppPaths.Log("导入工作进程退出未确认，保留本次临时文件以避免与进程竞争 operation=" + operation.Id.ToString("N"));
                return;
            }
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                if (!operation.CanCleanup) return;
                if (candidates != null)
                {
                    foreach (ImportedAudioCandidate candidate in candidates)
                    {
                        if (candidate == null || String.IsNullOrEmpty(candidate.PreparedPath)) continue;
                        try { if (File.Exists(candidate.PreparedPath)) File.Delete(candidate.PreparedPath); }
                        catch (Exception ex) { AppPaths.Log("清理导入曲库临时文件失败 operation=" + operation.Id.ToString("N") + " type=" + ex.GetType().Name); }
                    }
                }
                if (!operation.CanCleanup) return;
                try { EncryptedAudioConverter.CleanupTaskDirectory(operation.Id); }
                catch (Exception ex) { AppPaths.Log("清理本次歌曲导入临时目录失败 operation=" + operation.Id.ToString("N") + " type=" + ex.GetType().Name); }
            });
        }

        private bool IsCurrentSongImport(SongImportOperation operation)
        {
            return operation != null && _songImportState != null &&
                String.Equals(_songImportState.OperationId, operation.Id.ToString("N"), StringComparison.OrdinalIgnoreCase);
        }

        private void PostSongImportUi(SongImportOperation operation, MethodInvoker callback)
        {
            if (IsDisposed || !IsHandleCreated) return;
            MethodInvoker guarded = delegate
            {
                if (!_shuttingDown && !IsDisposed && IsCurrentSongImport(operation)) callback();
            };
            if (InvokeRequired)
            {
                try { BeginInvoke(guarded); } catch (InvalidOperationException) { }
            }
            else guarded();
        }

        private void ReportSongImportProgress(SongImportOperation operation, int index, string stage)
        {
            operation.SetStage(index, stage);
        }

        private void AddSongImportResult(SongImportOperation operation, SongImportResultItem item)
        {
            PostSongImportUi(operation, delegate
            {
                item.Outcome = ImportOutcome(item);
                int existing = _songImportResults.FindIndex(delegate(SongImportResultItem result) { return result.Index == item.Index; });
                if (existing >= 0) _songImportResults[existing] = item;
                else _songImportResults.Add(item);
                _songImportResults.Sort(delegate(SongImportResultItem a, SongImportResultItem b) { return a.Index.CompareTo(b.Index); });
                _songImportState.Results = _songImportResults.ToArray();
                PublishWebState();
            });
        }

        private void SetSongImportResult(SongImportOperation operation, int index, bool success, bool pending, string status, string detail, string technicalDetail)
        {
            if (!IsCurrentSongImport(operation)) return;
            SongImportResultItem item = _songImportResults.Find(delegate(SongImportResultItem result) { return result.Index == index; });
            if (item == null)
            {
                item = new SongImportResultItem { Index = index, FileName = "导入任务" };
                _songImportResults.Add(item);
            }
            item.Success = success;
            item.Pending = pending;
            item.Status = status ?? "";
            item.Detail = detail ?? "";
            item.TechnicalDetail = technicalDetail ?? "";
            item.Outcome = ImportOutcome(item);
            _songImportState.Results = _songImportResults.ToArray();
        }

        private void FinalizeSongImport(SongImportOperation operation, bool cancelled, int addedCount, int extraFailures)
        {
            if (!IsCurrentSongImport(operation)) return;
            _songImportState.Active = false;
            _songImportState.Complete = true;
            _songImportState.Cancelled = cancelled;
            _songImportState.CancelRequested = operation.IsCancelled;
            _songImportState.Added = addedCount;
            int failed = 0;
            int succeeded = 0;
            int timedOut = 0;
            int cancelledCount = 0;
            foreach (SongImportResultItem item in _songImportResults)
            {
                if (item.Pending)
                {
                    item.Pending = false;
                    if (cancelled)
                    {
                        item.Success = false;
                        item.Status = "已取消";
                        item.Detail = "批次取消，此文件未完成处理；源文件未修改。";
                    }
                    else
                    {
                        item.Success = false;
                        item.Status = "未导入";
                        item.Detail = "任务结束前未能写入曲库；源文件未修改。";
                    }
                }
                item.Outcome = ImportOutcome(item);
                if (item.Outcome == "cancelled") cancelledCount++;
                else if (item.Outcome == "timedOut") { timedOut++; failed++; }
                else if (!item.Success) failed++;
                else succeeded++;
            }
            _songImportState.Succeeded = succeeded;
            _songImportState.Failed = failed + Math.Max(0, extraFailures - failed);
            _songImportState.TimedOut = timedOut;
            _songImportState.CancelledCount = cancelledCount;
            _songImportState.Results = _songImportResults.ToArray();
            _songImportState.Stage = cancelled ? "已取消" : "已完成";
            _songImportState.ProgressMode = "complete";
            _songImportState.ElapsedMilliseconds = operation.ElapsedMilliseconds;
            _songImportState.Current = Math.Min(_songImportState.Total, Math.Max(_songImportState.Current, _songImportResults.Count));
            _songImportState.Summary = cancelled
                ? "导入已取消。成功 " + succeeded + " 项（新增 " + addedCount + " 首），取消 " + cancelledCount + " 项，失败或超时 " + _songImportState.Failed + " 项。源文件未修改。"
                : operation.CanCleanup
                    ? "处理完成。成功 " + succeeded + " 项（新增 " + addedCount + " 首），失败 " + _songImportState.Failed + " 项，超时 " + timedOut + " 项。源文件未修改。"
                    : "处理已停止。成功 " + succeeded + " 项，另有工作进程退出未确认；临时文件已保留，未继续处理。";
            PublishWebState();
        }

        private static string ImportOutcome(SongImportResultItem item)
        {
            if (item == null) return "failed";
            if (item.Pending) return "pending";
            if (item.Status != null && item.Status.IndexOf("取消", StringComparison.Ordinal) >= 0) return "cancelled";
            if (item.Status != null && item.Status.IndexOf("超时", StringComparison.Ordinal) >= 0) return "timedOut";
            return item.Success ? "success" : "failed";
        }

        private static string DescribeEncryptedImportFailure(EncryptedConversionStatus status)
        {
            if (status == EncryptedConversionStatus.KeyUnavailable)
                return "此文件需要当前转换器没有的专用密钥；未生成音频，源文件未修改。";
            if (status == EncryptedConversionStatus.UnsupportedVariant)
                return "此加密版本目前不支持离线转换；源文件未修改。";
            if (status == EncryptedConversionStatus.InvalidFile)
                return "文件格式无效或内容不完整；源文件未修改。";
            if (status == EncryptedConversionStatus.TimedOut)
                return "转换超时，工作进程已停止；源文件未修改。";
            if (status == EncryptedConversionStatus.FileTooLarge)
                return "该加密文件超过 256 MiB 的安全转换上限；源文件未修改。";
            return "转换失败，未加入曲库；源文件未修改。";
        }

        private static string DescribeAudioValidationFailure(AudioImportValidationStatus status, bool converted, string container)
        {
            if (status == AudioImportValidationStatus.TimedOut)
                return converted
                    ? "已转换为 " + (container ?? "音频").ToUpperInvariant() + "，但完整音频验证超时；源文件未修改。"
                    : "音频解码检查超时，未导入曲库；源文件未修改。";
            if (status == AudioImportValidationStatus.DecoderUnavailable)
                return converted
                    ? "容器转换成功，但当前 Windows 没有可用解码器播放 " + (container ?? "音频").ToUpperInvariant() + "；未导入曲库，源文件未修改。"
                    : "当前 Windows 没有可用的音频解码器；未导入曲库，源文件未修改。";
            if (status == AudioImportValidationStatus.InvalidFile)
                return converted
                    ? "转换结果损坏或不完整，未导入曲库；源文件未修改。"
                    : "音频文件损坏或不完整，未导入曲库；源文件未修改。";
            return "音频验证进程未能返回有效结果；未导入曲库，源文件未修改。";
        }

        private void CancelSongImport(string operationId)
        {
            if (_songImportState == null || !String.Equals(_songImportState.OperationId, operationId, StringComparison.OrdinalIgnoreCase)) return;
            if (_songImportState.Complete) return;
            if (_activeSongImport != null && String.Equals(_activeSongImport.Id.ToString("N"), operationId, StringComparison.OrdinalIgnoreCase))
            {
                _songImportState.CancelRequested = true;
                _songImportState.Stage = "已请求取消，正在停止本次工作进程…";
                PublishWebState();
                _activeSongImport.Cancel();
            }
        }

        private void CloseSongImport(string operationId)
        {
            if (_songImportState == null || !String.Equals(_songImportState.OperationId, operationId, StringComparison.OrdinalIgnoreCase)) return;
            if (!_songImportState.Complete)
            {
                CancelSongImport(operationId);
                return;
            }
            _songImportState = null;
            _songImportResults.Clear();
            SetKeyCapture("song-import", false);
            PublishWebState();
        }
        private void DeleteSong(TrackDefinition track)
        {
            ScenePreset previousScene = _settings.CloneActiveScene();
            SongLibrary.Remove(_settings, track.Id);
            string error;
            if (!SettingsStore.TrySave(_settings, out error))
            {
                _settings.RestoreScene(previousScene);
                _settings.Normalize();
                ShowLibraryNotice("删除失败：" + error, true);
                return;
            }
            _engine.StopTrack(track.Id);
            RebuildBindings();
            ShowLibraryNotice("已移除“" + track.Name + "”，音频文件保留。", false);
        }

        private void ShowLibraryNotice(string message, bool error)
        {
            _libraryNotice.Text = message;
            NotifyWeb(message);
            _libraryNotice.ForeColor = error ? Color.FromArgb(170, 50, 35) : UiTheme.Muted;
        }

        private static ProgressBar AddMeter(TableLayoutPanel grid, string label, int row)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
            grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            ProgressBar meter = new UiMeter { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Margin = new Padding(4, 3, 4, 3) };
            grid.Controls.Add(meter, 1, row);
            return meter;
        }

        private void CheckVirtualMicrophone(object sender, EventArgs e)
        {
            AudioDevice recording = _devices.FindCableRecording(_devices.FindCable(_settings.CableDeviceName));
            _routeCheck.Enabled = false;
            _routeResult.Text = "正在采集虚拟录音端，请播放 BGM 或讲话……";
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string result;
                bool success = false;
                try
                {
                    int peak = AudioRouteProbe.MeasurePeak(recording);
                    success = peak > AudioRouteProbe.SignalFloor;
                    result = success ? "CABLE Output 已收到信号（" + AudioRouteProbe.FormatPeak(peak) + "）。聊天软件麦克风请选择该设备。"
                        : "CABLE Output 未收到明显信号。请确认 BGM 电平、混合输出电平和音量后重试。";
                }
                catch (Exception ex) { result = "虚拟录音端检查失败：" + ex.Message; }
                AppPaths.Log("虚拟通道自检：" + result);
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        _routeCheck.Enabled = true;
                        _routeResult.ForeColor = success ? Color.FromArgb(20, 110, 50) : Color.FromArgb(170, 50, 35);
                        _routeResult.Text = result;
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        public void QueueHotkey(string keyName)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new MethodInvoker(delegate { if (!IsDisposed && !_capturingKey) PlayHotkey(keyName); })); }
            catch (InvalidOperationException) { }
        }

        public void PlayHotkey(string keyName)
        {
            if (InvokeRequired)
            {
                try { BeginInvoke(new MethodInvoker(delegate { PlayHotkey(keyName); })); }
                catch { }
                return;
            }

            if (!String.IsNullOrWhiteSpace(_settings.ActiveRandomKey) && String.Equals(_settings.ActiveRandomKey, keyName, StringComparison.OrdinalIgnoreCase))
            {
                PlayRandom();
                return;
            }
            foreach (TrackDefinition track in TrackCatalog.All)
            {
                if (String.Equals(_settings.GetKey(track.Id), keyName, StringComparison.OrdinalIgnoreCase))
                {
                    CancelContinuousRandom();
                    _engine.ToggleTrack(track);
                    return;
                }
            }
        }

        public bool IsCapturingKey
        {
            get { return _capturingKey; }
        }

        private static ComboBox CreateCombo()
        {
            return new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                IntegralHeight = false,
                Margin = new Padding(2, 5, 2, 3)
            };
        }

        private static TrackBar CreateTrackBar(int value)
        {
            TrackBar trackBar = new TrackBar();
            trackBar.Minimum = 0;
            trackBar.Maximum = 100;
            trackBar.TickFrequency = 10;
            trackBar.TickStyle = TickStyle.None;
            trackBar.BackColor = UiTheme.Surface;
            trackBar.SmallChange = 1;
            trackBar.LargeChange = 5;
            trackBar.Value = Math.Max(0, Math.Min(100, value));
            trackBar.Dock = DockStyle.Fill;
            trackBar.Margin = new Padding(0, 0, 2, 0);
            return trackBar;
        }

        private static Label CreateValueLabel(int value)
        {
            return new Label
            {
                Text = FormatMappedVolume(value),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight
            };
        }

        private static void AddDeviceRow(TableLayoutPanel panel, string label, ComboBox combo, int row)
        {
            Label name = new Label();
            name.Text = label;
            name.Dock = DockStyle.Fill;
            name.TextAlign = ContentAlignment.MiddleLeft;
            panel.Controls.Add(name, 0, row);
            panel.Controls.Add(combo, 1, row);
        }

        private static void AddBindingHeader(TableLayoutPanel panel)
        {
            panel.Controls.Add(new Label { Text = "歌曲", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiTheme.Muted, Padding = new Padding(10, 0, 0, 0) }, 0, 0);
            panel.Controls.Add(new Label { Text = "快捷键", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = UiTheme.Muted }, 1, 0);
        }

        private void LoadDevices()
        {
            _initializing = true;
            _microphone.Items.Clear();
            foreach (AudioDevice device in _devices.Microphones)
                _microphone.Items.Add(device);
            SelectAvailableOrMissing(_microphone, _settings.MicrophoneDeviceName, _devices.FindMicrophone(_settings.MicrophoneDeviceName), "所选麦克风不可用，请重新选择");

            _headphone.Items.Clear();
            _headphone.Items.Add(new AudioDevice(0xFFFFFFFF, "Windows 默认播放设备（原生 MP3）"));
            _headphone.SelectedIndex = 0;
            _headphone.Enabled = false;

            _cable.Items.Clear();
            foreach (AudioDevice device in _devices.Cables)
                _cable.Items.Add(device);
            SelectAvailableOrMissing(_cable, _settings.CableDeviceName, _devices.FindCable(_settings.CableDeviceName), "所选 VB-CABLE 不可用，请重新选择");
            _initializing = false;
        }

        private static void SelectAvailableOrMissing(ComboBox combo, string savedName, AudioDevice resolved, string missing)
        {
            if (resolved != null) { SelectByName(combo, resolved.Name, resolved.Name); return; }
            AudioDevice unavailable = new AudioDevice(0xFFFFFFFE, missing + (String.IsNullOrWhiteSpace(savedName) ? "" : "：" + savedName));
            combo.Items.Insert(0, unavailable);
            combo.SelectedIndex = 0;
        }

        private static void SelectByName(ComboBox combo, string name, string fallback)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                AudioDevice device = combo.Items[i] as AudioDevice;
                if (device != null && String.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            for (int i = 0; i < combo.Items.Count; i++)
            {
                AudioDevice device = combo.Items[i] as AudioDevice;
                if (device != null && String.Equals(device.Name, fallback, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private void VolumeChanged(object sender, EventArgs e)
        {
            if (_initializing)
                return;
            _settings.HeadphoneVolume = _headphoneVolume.Value;
            _settings.CableVolume = _cableVolume.Value;
            _headphoneValue.Text = FormatMappedVolume(_headphoneVolume.Value);
            _cableValue.Text = FormatMappedVolume(_cableVolume.Value);
            _engine.SetVolumes(_settings.HeadphoneVolume, _settings.CableVolume);
            string volumeError;
            if (!SettingsStore.TrySave(_settings, out volumeError)) ShowLibraryNotice("音量保存失败：" + volumeError, true);
        }

        private void DeviceChanged(object sender, EventArgs e)
        {
            if (_initializing)
                return;
            AudioDevice microphone = _microphone.SelectedItem as AudioDevice;
            AudioDevice cable = _cable.SelectedItem as AudioDevice;
            if (microphone != null && microphone.Id != 0xFFFFFFFE) _settings.MicrophoneDeviceName = microphone.Name;
            if (cable != null && cable.Id != 0xFFFFFFFE)
            {
                _settings.CableDeviceName = cable.Name.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cable.Name.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0 ? cable.Name : "";
            }
            string deviceError;
            if (!SettingsStore.TrySave(_settings, out deviceError)) ShowLibraryNotice("设备设置保存失败：" + deviceError, true);
            _engine.ReconfigureDevices();
            RefreshStatus();
        }

        private static string KeyName(Keys key)
        {
            return key.ToString();
        }

        private static string FormatKey(string key)
        {
            if (String.IsNullOrWhiteSpace(key))
                return "未设置";
            if (key.Length == 2 && key[0] == 'D' && Char.IsDigit(key[1]))
                return key.Substring(1);
            if (key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase))
                return "Num " + key.Substring(6);
            return key;
        }

        private static string FormatMappedVolume(int sliderValue)
        {
            int outputPercent = (int)Math.Round(sliderValue * 0.30);
            return sliderValue + "%";
        }

        public void SetHotkeyError(string message)
        {
            _hotkeyError = message ?? "";
            RefreshStatus();
        }

        private string _hotkeyError = "";

        private void EngineStatusChanged(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    if (IsDisposed) return;
                    MarkFailedRandomPlayback();
                    RefreshStatus();
                }));
            }
            catch { }
        }

        private void RefreshStatus()
        {
            if (!IsDisposed)
            {
                _status.Text = String.IsNullOrWhiteSpace(_hotkeyError)
                    ? _engine.StatusText
                    : _hotkeyError + Environment.NewLine + _engine.StatusText;
                PublishWebState();

            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _shuttingDown = true;
            CancelContinuousRandom();
            CancelAnchorInspection();
            _engine.StopActiveTrack();
            if (_activeSongImport != null) _activeSongImport.Cancel();
            SoundPackService.Cancel();
            if (!String.IsNullOrWhiteSpace(_pendingImportToken))
            {
                SoundPackService.CancelPrepared(_pendingImportToken);
                _pendingImportToken = "";
            }
            SettingsStore.Save(_settings);
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _engine.StatusChanged -= EngineStatusChanged;
            _engine.PlaybackCompleted -= EnginePlaybackCompleted;
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_activeSongImport != null) _activeSongImport.Cancel();
                _engine.StatusChanged -= EngineStatusChanged;
                _engine.PlaybackCompleted -= EnginePlaybackCompleted;
                if (_meterTimer != null) _meterTimer.Dispose();
                if (_stateAdapters != null) _stateAdapters.Dispose();
                if (_startupTimeout != null) _startupTimeout.Dispose();
                if (_web != null) _web.Dispose();
            }
            base.Dispose(disposing);
        }

        public void ExitApplication()
        {
            Close();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_status != null)
                RefreshStatus();
        }
    }
}

