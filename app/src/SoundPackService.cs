using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace BgmHotkey
{
    [Serializable]
    public sealed class SoundPackManifest
    {
        public int Version { get; set; }
        public string SceneName { get; set; }
        public string RandomKey { get; set; }
        public SoundPackAsset[] Assets { get; set; }
        public SoundPackTrack[] Tracks { get; set; }
        public string[] RandomAssetIds { get; set; }
        public SoundPackAnalysis[] Loudness { get; set; }
        public SoundPackManifest() { Assets = new SoundPackAsset[0]; Tracks = new SoundPackTrack[0]; RandomAssetIds = new string[0]; Loudness = new SoundPackAnalysis[0]; }
    }

    [Serializable]
    public sealed class SoundPackAsset
    {
        public string Id { get; set; }
        public string FileName { get; set; }
        public string DisplayName { get; set; }
        public string Sha256 { get; set; }
    }

    [Serializable]
    public sealed class SoundPackTrack
    {
        public string Id { get; set; }
        public string AssetId { get; set; }
        public string Name { get; set; }
        public string Key { get; set; }
        public double StartSeconds { get; set; }
        public double? EndSeconds { get; set; }
    }

    [Serializable]
    public sealed class SoundPackAnalysis
    {
        public string AssetId { get; set; }
        public double IntegratedLufs { get; set; }
        public double PeakDbfs { get; set; }
        public double GainDb { get; set; }
        public double TargetLufs { get; set; }
        public string AlgorithmVersion { get; set; }
    }

    internal sealed class SoundPackPreview
    {
        public string Token;
        public string SceneName;
        public int TrackCount;
        public int AssetCount;
        public long ExpandedBytes;
        public string[] Warnings;
    }

    internal sealed class SoundPackFileCopy
    {
        public string StagedPath;
        public string DestinationPath;
        public string Sha256;
    }

    internal sealed class SoundPackImportPlan
    {
        public ScenePreset Scene;
        public SavedTrack[] NewAssets;
        public LoudnessAnalysis[] NewAnalyses;
        public List<SoundPackFileCopy> CreatedFiles;
        public string[] Warnings;
    }

    internal static class SoundPackService
    {
        private const int ManifestLimit = 2 * 1024 * 1024;
        private const int MaximumAssets = 500;
        private const long MaximumEntryBytes = 512L * 1024 * 1024;
        private const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, PreparedPack> Prepared = new Dictionary<string, PreparedPack>(StringComparer.OrdinalIgnoreCase);
        private static CancellationTokenSource _cancellation;
        private static bool _busy;
        private static string _progress = "";

        private sealed class PreparedPack
        {
            public SoundPackManifest Manifest;
            public string Directory;
            public long ExpandedBytes;
            public string[] Warnings;
            public Dictionary<string, double> Durations;
        }

        private sealed class LocalFile
        {
            public string FileName;
        }

        public static bool IsBusy { get { lock (Gate) return _busy; } }
        public static string ProgressText { get { lock (Gate) return _progress; } }

        public static void Cancel()
        {
            lock (Gate) if (_cancellation != null) _cancellation.Cancel();
        }

        public static void Export(AppSettings settings, string destinationPath)
        {
            CancellationToken token = Begin("正在准备场景包导出…");
            string temporary = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                ScenePreset scene = settings.ActiveScene;
                if (scene == null) throw new InvalidOperationException("当前没有可导出的场景。");
                Dictionary<string, SoundPackAsset> assets = new Dictionary<string, SoundPackAsset>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                SoundPackManifest manifest = new SoundPackManifest { Version = 1, SceneName = SafeText(scene.Name, 128), RandomKey = scene.RandomKey ?? "" };
                foreach (SavedTrack track in settings.ActiveTracks()) AddExportAsset(settings, manifest, assets, paths, track.Id, track.FileName, track.Name);
                foreach (string file in scene.RandomFileNames ?? new string[0]) AddExportAsset(settings, manifest, assets, paths, "", file, Path.GetFileNameWithoutExtension(file));
                if (assets.Count == 0) throw new InvalidOperationException("当前场景没有可导出的音频文件。");
                if (assets.Count > MaximumAssets) throw new InvalidDataException("场景包最多可包含 500 个音频文件。");

                List<SoundPackTrack> tracks = new List<SoundPackTrack>();
                foreach (SavedTrack active in settings.ActiveTracks())
                {
                    string assetId = FindAssetId(assets, active.FileName);
                    if (assetId == null) continue;
                    tracks.Add(new SoundPackTrack { Id = "track_" + Guid.NewGuid().ToString("N"), AssetId = assetId, Name = SafeText(active.Name, 256), Key = settings.GetKey(active.Id),
                        StartSeconds = active.StartSeconds, EndSeconds = active.EndSeconds });
                }
                manifest.Tracks = tracks.ToArray();
                List<string> random = new List<string>();
                foreach (string file in scene.RandomFileNames ?? new string[0])
                {
                    string assetId = FindAssetId(assets, file);
                    if (assetId != null && !random.Contains(assetId)) random.Add(assetId);
                }
                manifest.RandomAssetIds = random.ToArray();
                List<SoundPackAnalysis> loudness = new List<SoundPackAnalysis>();
                foreach (SoundPackAsset asset in manifest.Assets)
                {
                    LoudnessAnalysis analysis = settings.FindAnalysis(asset.FileName);
                    if (analysis != null && String.Equals(analysis.Fingerprint, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                        loudness.Add(new SoundPackAnalysis { AssetId = asset.Id, IntegratedLufs = analysis.IntegratedLufs, GainDb = analysis.GainDb,
                            PeakDbfs = analysis.PeakDbfs, TargetLufs = analysis.TargetLufs, AlgorithmVersion = analysis.AlgorithmVersion });
                }
                manifest.Loudness = loudness.ToArray();

                JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
                byte[] manifestBytes = new UTF8Encoding(false).GetBytes(serializer.Serialize(manifest));
                long expanded = manifestBytes.LongLength;
                foreach (SoundPackAsset asset in manifest.Assets)
                {
                    token.ThrowIfCancellationRequested();
                    FileInfo info = new FileInfo(paths[asset.Id]);
                    if (!info.Exists || info.Length > MaximumEntryBytes) throw new InvalidDataException("音频文件不存在或超过单文件导出上限：" + asset.FileName);
                    expanded += info.Length;
                    if (expanded > MaximumExpandedBytes) throw new InvalidDataException("场景包超过 2 GiB 展开大小上限。");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (ZipArchive archive = new ZipArchive(output, ZipArchiveMode.Create, false, Encoding.UTF8))
                {
                    ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                    using (Stream stream = manifestEntry.Open()) stream.Write(manifestBytes, 0, manifestBytes.Length);
                    long copied = 0;
                    foreach (SoundPackAsset asset in manifest.Assets)
                    {
                        token.ThrowIfCancellationRequested();
                        ZipArchiveEntry entry = archive.CreateEntry("audio/" + asset.FileName, CompressionLevel.NoCompression);
                        using (Stream source = new FileStream(paths[asset.Id], FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                        using (Stream target = entry.Open()) CopyBounded(source, target, MaximumEntryBytes, token, delegate(long bytes)
                        {
                            copied += bytes;
                            SetProgress("正在打包音频：" + Path.GetFileName(asset.FileName) + " · " + FormatBytes(copied) + " / " + FormatBytes(expanded));
                        });
                    }
                }
                token.ThrowIfCancellationRequested();
                if (File.Exists(destinationPath)) File.Replace(temporary, destinationPath, null); else File.Move(temporary, destinationPath);
                SetProgress("场景包导出完成。");
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                throw;
            }
            finally { End(); }
        }

        public static SoundPackPreview PrepareImport(string archivePath)
        {
            CancellationToken token = Begin("正在安全检查音效包…");
            string stage = Path.Combine(Path.GetTempPath(), "WanxiangBgmPack-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(stage);
                using (FileStream file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Read, false, Encoding.UTF8))
                {
                    if (archive.Entries.Count < 1 || archive.Entries.Count > MaximumAssets + 1) throw new InvalidDataException("音效包条目数量超出限制。");
                    HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    ZipArchiveEntry manifestEntry = null;
                    Dictionary<string, ZipArchiveEntry> audioEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                    long declaredTotal = 0;
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        string entryName = entry.FullName;
                        ValidateEntryName(entryName);
                        if (!names.Add(entryName)) throw new InvalidDataException("压缩包内存在重复文件名。");
                        int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                        if (unixType == 0xA000) throw new InvalidDataException("音效包不允许包含符号链接。");
                        if (entryName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                        {
                            if (manifestEntry != null || entry.Length > ManifestLimit) throw new InvalidDataException("manifest.json 重复或超过 2 MiB。");
                            manifestEntry = entry;
                        }
                        else if (entryName.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                        {
                            string leaf = entryName.Substring(6);
                            if (!AppSettings.IsAudioFileName(leaf) || entry.Length <= 0 || entry.Length > MaximumEntryBytes)
                                throw new InvalidDataException("音效包包含无效或过大的音频文件。");
                            audioEntries.Add(leaf, entry);
                            declaredTotal += entry.Length;
                        }
                        else throw new InvalidDataException("音效包包含不支持的条目：" + entryName);
                        if (declaredTotal > MaximumExpandedBytes) throw new InvalidDataException("音效包展开大小超过 2 GiB。");
                    }
                    if (manifestEntry == null) throw new InvalidDataException("音效包缺少 manifest.json。");
                    if (declaredTotal + manifestEntry.Length > MaximumExpandedBytes) throw new InvalidDataException("音效包展开大小超过 2 GiB。");
                    SoundPackManifest manifest;
                    using (Stream stream = manifestEntry.Open())
                    using (MemoryStream memory = new MemoryStream())
                    {
                        CopyBounded(stream, memory, ManifestLimit, token, null);
                        string json = new UTF8Encoding(false, true).GetString(memory.ToArray());
                        manifest = new JavaScriptSerializer { MaxJsonLength = ManifestLimit }.Deserialize<SoundPackManifest>(json);
                    }
                    ValidateManifest(manifest, audioEntries);
                    List<string> warnings = new List<string>();
                    long actualBytes = 0;
                    Dictionary<string, double> durations = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (SoundPackAsset asset in manifest.Assets)
                    {
                        token.ThrowIfCancellationRequested();
                        ZipArchiveEntry entry = audioEntries[asset.FileName];
                        string target = Path.Combine(stage, asset.FileName);
                        long currentFileBytes = 0;
                        using (Stream source = entry.Open())
                        using (Stream destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                            actualBytes += CopyBounded(source, destination, MaximumEntryBytes, token, delegate(long count)
                            { currentFileBytes += count; SetProgress("正在展开音频：" + Path.GetFileName(asset.FileName) + " · " + FormatBytes(actualBytes + currentFileBytes)); });
                        if (actualBytes > MaximumExpandedBytes) throw new InvalidDataException("实际展开大小超过 2 GiB。");
                        string hash = LoudnessAnalyzer.Fingerprint(target);
                        if (!String.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("文件校验失败：" + asset.FileName);
                        durations[asset.Id] = AudioFormatCatalog.ProbeDurationSeconds(target);
                    }
                    CleanAnchorsAndKeys(manifest, durations, warnings);
                    string tokenId = Guid.NewGuid().ToString("N");
                    PreparedPack prepared = new PreparedPack { Manifest = manifest, Directory = stage, ExpandedBytes = actualBytes, Warnings = warnings.ToArray(), Durations = durations };
                    lock (Gate) Prepared.Add(tokenId, prepared);
                    SetProgress("音效包已检查，等待确认。");
                    return new SoundPackPreview { Token = tokenId, SceneName = SafeText(manifest.SceneName, 128), TrackCount = manifest.Tracks.Length,
                        AssetCount = manifest.Assets.Length, ExpandedBytes = actualBytes, Warnings = warnings.ToArray() };
                }
            }
            catch
            {
                TryDeleteDirectory(stage);
                throw;
            }
            finally { End(); }
        }

        public static SoundPackImportPlan BuildImportPlan(string tokenId, SavedTrack[] existingAssets, string[] existingSceneNames)
        {
            CancellationToken token = Begin("正在导入场景和音频文件…");
            PreparedPack prepared;
            List<SoundPackFileCopy> created = new List<SoundPackFileCopy>();
            try
            {
                lock (Gate) if (!Prepared.TryGetValue(tokenId, out prepared)) throw new InvalidOperationException("音效包预览已失效，请重新导入。");
                Dictionary<string, LocalFile> local = new Dictionary<string, LocalFile>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, List<SavedTrack>> tracksByFile = new Dictionary<string, List<SavedTrack>>(StringComparer.OrdinalIgnoreCase);
                foreach (SavedTrack item in existingAssets ?? new SavedTrack[0])
                {
                    List<SavedTrack> sameFile;
                    if (!tracksByFile.TryGetValue(item.FileName, out sameFile)) { sameFile = new List<SavedTrack>(); tracksByFile.Add(item.FileName, sameFile); }
                    sameFile.Add(item);
                }
                Dictionary<string, List<SavedTrack>> knownTracksByHash = new Dictionary<string, List<SavedTrack>>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in RandomFileCatalog.Scan(AppPaths.TrackPath("")))
                {
                    token.ThrowIfCancellationRequested();
                    string hash = LoudnessAnalyzer.Fingerprint(AppPaths.TrackPath(file));
                    if (!local.ContainsKey(hash)) local.Add(hash, new LocalFile { FileName = file });
                    List<SavedTrack> known;
                    if (tracksByFile.TryGetValue(file, out known))
                    {
                        List<SavedTrack> byHash;
                        if (!knownTracksByHash.TryGetValue(hash, out byHash)) { byHash = new List<SavedTrack>(); knownTracksByHash.Add(hash, byHash); }
                        byHash.AddRange(known);
                    }
                }

                Dictionary<string, string> targetNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> reservedNames = new HashSet<string>(RandomFileCatalog.Scan(AppPaths.TrackPath("")), StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> hashToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int index = 0;
                foreach (SoundPackAsset asset in prepared.Manifest.Assets)
                {
                    token.ThrowIfCancellationRequested();
                    LocalFile existing;
                    string fileName;
                    if (local.TryGetValue(asset.Sha256, out existing)) fileName = existing.FileName;
                    else
                    {
                        if (hashToName.TryGetValue(asset.Sha256, out fileName)) { }
                        else
                        {
                            fileName = ReserveFileName(asset.FileName, reservedNames);
                            string staged = Path.Combine(prepared.Directory, asset.FileName);
                            string destination = AppPaths.TrackPath(fileName);
                            int fileIndex = ++index;
                            CopyCreateNew(staged, destination, token, delegate(long copied, long total)
                            { SetProgress("正在复制音频文件 " + fileIndex + " / " + prepared.Manifest.Assets.Length + " · " + FormatBytes(copied) + " / " + FormatBytes(total)); },
                            delegate { created.Add(new SoundPackFileCopy { StagedPath = staged, DestinationPath = destination, Sha256 = asset.Sha256 }); });
                            hashToName[asset.Sha256] = fileName;
                            local[asset.Sha256] = new LocalFile { FileName = fileName };
                        }
                    }
                    targetNames[asset.Id] = fileName;
                }

                List<SavedTrack> newAssets = new List<SavedTrack>();
                Dictionary<string, string> targetTrackIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> claimedExistingTracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (SoundPackTrack track in prepared.Manifest.Tracks)
                {
                    string fileName = targetNames[track.AssetId];
                    SoundPackAsset trackAsset = FindManifestAsset(prepared.Manifest, track.AssetId);
                    SavedTrack knownAsset = null;
                    List<SavedTrack> matchingTracks;
                    if (trackAsset != null && knownTracksByHash.TryGetValue(trackAsset.Sha256, out matchingTracks))
                        foreach (SavedTrack item in matchingTracks)
                            if (item != null && !claimedExistingTracks.Contains(item.Id)) { knownAsset = item; break; }
                    if (knownAsset != null)
                    {
                        targetTrackIds[track.Id] = knownAsset.Id;
                        claimedExistingTracks.Add(knownAsset.Id);
                    }
                    else
                    {
                        string newId = "song_" + Guid.NewGuid().ToString("N");
                        targetTrackIds[track.Id] = newId;
                        string displayName = SafeText(track.Name, 256);
                        if (displayName.Length == 0) displayName = Path.GetFileNameWithoutExtension(fileName);
                        newAssets.Add(new SavedTrack { Id = newId, Name = displayName, FileName = fileName });
                    }
                }

                ScenePreset scene = new ScenePreset { Id = "scene_" + Guid.NewGuid().ToString("N"), Name = UniqueSceneName(prepared.Manifest.SceneName, existingSceneNames),
                    RandomKey = prepared.Manifest.RandomKey ?? "", RandomFileNames = new string[0] };
                List<SceneTrack> sceneTracks = new List<SceneTrack>();
                List<TrackBinding> bindings = new List<TrackBinding>();
                foreach (SoundPackTrack track in prepared.Manifest.Tracks)
                {
                    string mappedId = targetTrackIds[track.Id];
                    sceneTracks.Add(new SceneTrack { TrackId = mappedId, Name = SafeText(track.Name, 256), StartSeconds = track.StartSeconds, EndSeconds = track.EndSeconds });
                    if (!String.IsNullOrWhiteSpace(track.Key)) bindings.Add(new TrackBinding { TrackId = mappedId, Key = track.Key });
                }
                scene.Tracks = sceneTracks.ToArray(); scene.Bindings = bindings.ToArray();
                List<string> randomFiles = new List<string>();
                foreach (string assetId in prepared.Manifest.RandomAssetIds ?? new string[0])
                    if (targetNames.ContainsKey(assetId) && !randomFiles.Contains(targetNames[assetId])) randomFiles.Add(targetNames[assetId]);
                scene.RandomFileNames = randomFiles.ToArray();

                List<LoudnessAnalysis> importedAnalysis = new List<LoudnessAnalysis>();
                foreach (SoundPackAnalysis item in prepared.Manifest.Loudness ?? new SoundPackAnalysis[0])
                {
                    SoundPackAsset asset = FindManifestAsset(prepared.Manifest, item.AssetId);
                    if (asset == null || !LoudnessAnalyzer.IsCompatibleAlgorithmVersion(item.AlgorithmVersion) ||
                        Double.IsNaN(item.TargetLufs) || Double.IsInfinity(item.TargetLufs) || Double.IsNaN(item.GainDb) || Double.IsInfinity(item.GainDb) ||
                        Double.IsNaN(item.PeakDbfs) || Double.IsInfinity(item.PeakDbfs) || item.TargetLufs < -70 || item.TargetLufs > 10 ||
                        item.PeakDbfs < -100 || item.PeakDbfs > 0 || item.GainDb < -60 || item.GainDb > 12 ||
                        Double.IsNaN(item.IntegratedLufs) || Double.IsInfinity(item.IntegratedLufs) || item.IntegratedLufs < -100 || item.IntegratedLufs > 10) continue;
                    importedAnalysis.Add(new LoudnessAnalysis { FileName = targetNames[item.AssetId], Fingerprint = asset.Sha256, IntegratedLufs = item.IntegratedLufs, PeakDbfs = item.PeakDbfs,
                        GainDb = item.GainDb, TargetLufs = item.TargetLufs, AlgorithmVersion = item.AlgorithmVersion, IsValidated = true });
                }
                return new SoundPackImportPlan { Scene = scene, NewAssets = newAssets.ToArray(), NewAnalyses = importedAnalysis.ToArray(), CreatedFiles = created, Warnings = prepared.Warnings };
            }
            catch
            {
                foreach (SoundPackFileCopy copy in created) try { if (File.Exists(copy.DestinationPath)) File.Delete(copy.DestinationPath); } catch { }
                throw;
            }
            finally { End(); }
        }

        public static void CompleteImport(string tokenId)
        {
            PreparedPack pack = null;
            lock (Gate)
            {
                if (Prepared.TryGetValue(tokenId, out pack)) Prepared.Remove(tokenId);
            }
            if (pack != null) TryDeleteDirectory(pack.Directory);
        }

        public static void RollbackFiles(SoundPackImportPlan plan)
        {
            if (plan == null || plan.CreatedFiles == null) return;
            foreach (SoundPackFileCopy copy in plan.CreatedFiles)
                try
                {
                    if (File.Exists(copy.DestinationPath) && String.Equals(LoudnessAnalyzer.Fingerprint(copy.DestinationPath), copy.Sha256, StringComparison.OrdinalIgnoreCase))
                        File.Delete(copy.DestinationPath);
                }
                catch { }
        }

        public static void CancelPrepared(string tokenId)
        {
            CompleteImport(tokenId);
        }

        private static void AddExportAsset(AppSettings settings, SoundPackManifest manifest, Dictionary<string, SoundPackAsset> assets,
            Dictionary<string, string> paths, string trackId, string fileName, string displayName)
        {
            if (assets.ContainsKey(fileName)) return;
            if (!AppSettings.IsAudioFileName(fileName)) throw new InvalidDataException("曲库包含不支持的文件名：" + fileName);
            string path = AppPaths.TrackPath(fileName);
            if (!File.Exists(path)) throw new FileNotFoundException("场景引用的音频文件不存在。", fileName);
            string hash = LoudnessAnalyzer.Fingerprint(path);
            SoundPackAsset asset = new SoundPackAsset { Id = "asset_" + Guid.NewGuid().ToString("N"), FileName = fileName,
                DisplayName = SafeText(displayName, 256), Sha256 = hash };
            assets.Add(fileName, asset); paths.Add(asset.Id, path);
            List<SoundPackAsset> list = new List<SoundPackAsset>(manifest.Assets); list.Add(asset); manifest.Assets = list.ToArray();
        }

        private static string FindAssetId(Dictionary<string, SoundPackAsset> assets, string fileName)
        {
            SoundPackAsset asset;
            return assets.TryGetValue(fileName, out asset) ? asset.Id : null;
        }

        private static void ValidateManifest(SoundPackManifest manifest, Dictionary<string, ZipArchiveEntry> entries)
        {
            if (manifest == null || manifest.Version != 1) throw new InvalidDataException("不支持的音效包版本。");
            if (manifest.Assets == null || manifest.Assets.Length == 0 || manifest.Assets.Length > MaximumAssets) throw new InvalidDataException("音效包音频数量无效。");
            if (manifest.Tracks == null || manifest.Tracks.Length > MaximumAssets) throw new InvalidDataException("音效包歌曲列表无效。");
            HashSet<string> assetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> assetFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SoundPackAsset asset in manifest.Assets)
            {
                if (asset == null || String.IsNullOrWhiteSpace(asset.Id) || !assetIds.Add(asset.Id) || !AppSettings.IsAudioFileName(asset.FileName) || !assetFiles.Add(asset.FileName))
                    throw new InvalidDataException("音效包包含重复或无效的歌曲资源。");
                ZipArchiveEntry entry;
                if (!entries.TryGetValue(asset.FileName, out entry)) throw new InvalidDataException("音效包缺少歌曲文件：" + asset.FileName);
                if (String.IsNullOrWhiteSpace(asset.Sha256) || asset.Sha256.Length != 64) throw new InvalidDataException("歌曲校验值格式无效。");
                foreach (char hashChar in asset.Sha256) if (!Uri.IsHexDigit(hashChar)) throw new InvalidDataException("歌曲校验值格式无效。");
            }
            if (entries.Count != assetFiles.Count) throw new InvalidDataException("音效包含有未列入清单的歌曲文件。");
            HashSet<string> trackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SoundPackTrack track in manifest.Tracks)
            {
                if (track == null || String.IsNullOrWhiteSpace(track.AssetId) || !assetIds.Contains(track.AssetId))
                    throw new InvalidDataException("音效包歌曲引用无效或重复。");
                if (String.IsNullOrWhiteSpace(track.Id)) track.Id = "track_" + Guid.NewGuid().ToString("N");
                if (!trackIds.Add(track.Id)) throw new InvalidDataException("音效包歌曲标识重复。");
            }
            HashSet<string> randomIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in manifest.RandomAssetIds ?? new string[0])
                if (!assetIds.Contains(id) || !randomIds.Add(id)) throw new InvalidDataException("随机歌单引用无效或重复。");
            manifest.SceneName = SafeText(manifest.SceneName, 128);
            if (manifest.SceneName.Length == 0) manifest.SceneName = "导入场景";
        }

        private static void CleanAnchorsAndKeys(SoundPackManifest manifest, Dictionary<string, double> durations, List<string> warnings)
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SoundPackTrack track in manifest.Tracks)
            {
                string key = AppSettings.NormalizeKey(track.Key);
                if (key.Length > 0 && !keys.Add(key)) { track.Key = ""; warnings.Add("存在重复歌曲快捷键，重复绑定已清空。"); }
                else track.Key = key;
                double duration = durations[track.AssetId];
                if (Double.IsNaN(track.StartSeconds) || Double.IsInfinity(track.StartSeconds) || track.StartSeconds < 0 || track.StartSeconds >= duration)
                { track.StartSeconds = 0; track.EndSeconds = null; warnings.Add("有起始锚点超出歌曲时长，已重置为整首播放。"); }
                else if (track.EndSeconds.HasValue && (Double.IsNaN(track.EndSeconds.Value) || Double.IsInfinity(track.EndSeconds.Value) ||
                    track.EndSeconds.Value <= track.StartSeconds || track.EndSeconds.Value > duration))
                { track.EndSeconds = null; warnings.Add("有结束锚点无效，已改为播放到结尾。"); }
            }
            manifest.RandomKey = AppSettings.NormalizeKey(manifest.RandomKey);
            if (manifest.RandomKey.Length > 0 && keys.Contains(manifest.RandomKey)) { manifest.RandomKey = ""; warnings.Add("随机快捷键与歌曲快捷键冲突，已清空随机快捷键。"); }
        }

        private static SoundPackAsset FindManifestAsset(SoundPackManifest manifest, string id)
        {
            foreach (SoundPackAsset asset in manifest.Assets)
                if (String.Equals(asset.Id, id, StringComparison.OrdinalIgnoreCase)) return asset;
            return null;
        }

        private static string UniqueSceneName(string requested, string[] existingNames)
        {
            string baseName = SafeText(requested, 128);
            if (baseName.Length == 0) baseName = "导入场景";
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in existingNames ?? new string[0]) names.Add(name ?? "");
            string candidate = baseName;
            int suffix = 2;
            while (names.Contains(candidate)) candidate = baseName + " (" + suffix++ + ")";
            return candidate;
        }

        private static string ReserveFileName(string requested, HashSet<string> reserved)
        {
            string leaf = Path.GetFileName(requested);
            string extension = Path.GetExtension(leaf);
            string stem = Path.GetFileNameWithoutExtension(leaf);
            string candidate = leaf;
            int suffix = 1;
            while (reserved.Contains(candidate) || File.Exists(AppPaths.TrackPath(candidate)))
                candidate = stem + "_import" + suffix++ + extension;
            reserved.Add(candidate);
            return candidate;
        }

        private static void ValidateEntryName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.IndexOf('\0') >= 0 || name.IndexOf('\\') >= 0 || name.IndexOf(':') >= 0 ||
                name.StartsWith("/", StringComparison.Ordinal) || name.StartsWith("\\\\", StringComparison.Ordinal) || name.Contains("../") || name.Contains("..\\") || name.Equals("..", StringComparison.Ordinal))
                throw new InvalidDataException("音效包包含危险路径。");
            if (name.EndsWith("/", StringComparison.Ordinal)) throw new InvalidDataException("音效包不允许目录条目。");
        }

        private static string SafeText(string value, int maximum)
        {
            if (value == null) return "";
            StringBuilder builder = new StringBuilder();
            foreach (char ch in value)
                if (!Char.IsControl(ch) && ch != '\0') builder.Append(ch);
            string result = builder.ToString().Trim();
            return result.Length > maximum ? result.Substring(0, maximum) : result;
        }

        private static long CopyBounded(Stream source, Stream destination, long maximum, CancellationToken token, Action<long> copied)
        {
            byte[] buffer = new byte[1024 * 1024];
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                total += read;
                if (total > maximum) throw new InvalidDataException("音频条目实际展开内容超过大小限制。");
                destination.Write(buffer, 0, read);
                if (copied != null) copied(read);
            }
            return total;
        }

        private static void CopyCreateNew(string sourcePath, string destinationPath, CancellationToken token, Action<long, long> progress, Action destinationCreated)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
            FileInfo info = new FileInfo(sourcePath);
            using (FileStream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            using (FileStream destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            {
                if (destinationCreated != null) destinationCreated();
                long total = 0; byte[] buffer = new byte[1024 * 1024]; int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested(); total += read;
                    if (total > MaximumEntryBytes) throw new InvalidDataException("音频文件超过单文件大小上限。");
                    destination.Write(buffer, 0, read); if (progress != null) progress(total, info.Length);
                }
                destination.Flush(true);
                if (total != info.Length) throw new IOException("复制音频文件时长度发生变化。");
            }
        }

        private static CancellationToken Begin(string initialProgress)
        {
            lock (Gate)
            {
                if (_busy) throw new InvalidOperationException("已有音效包操作正在进行。");
                _busy = true; _progress = initialProgress; _cancellation = new CancellationTokenSource(); return _cancellation.Token;
            }
        }

        private static void End()
        {
            lock (Gate)
            {
                _busy = false;
                if (_cancellation != null) { _cancellation.Dispose(); _cancellation = null; }
            }
        }

        private static void SetProgress(string text) { lock (Gate) _progress = text ?? ""; }
        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GiB";
            if (bytes >= 1024L * 1024) return (bytes / (1024.0 * 1024)).ToString("0", CultureInfo.InvariantCulture) + " MiB";
            return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KiB";
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }
}
