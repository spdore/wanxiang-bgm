using System;
using System.IO;
using System.Xml.Serialization;
using System.Collections.Generic;
using System.Windows.Forms;

namespace BgmHotkey
{
    internal static class AppPaths
    {
        public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string SettingsDirectory =
#if TEST_BUILD
            Path.Combine(Root, "test-data");
#elif PUBLIC_RELEASE
            Path.Combine(LocalDataDirectory(), "BgmHotkeyShare");
#else
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BgmHotkey");
#endif
        public static readonly string SettingsFile = Path.Combine(SettingsDirectory, "settings.xml");
        public static readonly string LogFile = Path.Combine(SettingsDirectory, "app.log");

#if PUBLIC_RELEASE
        private static string LocalDataDirectory()
        {
            string directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (String.IsNullOrWhiteSpace(directory)) directory = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (String.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
                throw new InvalidOperationException("无法获取当前用户的本地应用数据目录。");
            return directory;
        }
#endif

        public static string TrackPath(string fileName)
        {
#if PUBLIC_RELEASE
            return Path.Combine(SettingsDirectory, "bgm", fileName);
#else
            return Path.Combine(Root, "bgm", fileName);
#endif
        }

        public static string ImportJobPath(Guid jobId)
        {
            return Path.Combine(SettingsDirectory, "import-work", "job_" + jobId.ToString("N"));
        }

        public static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine);
            }
            catch
            {
                // Logging must never prevent the hidden player from starting.
            }
        }
    }

    [Serializable]
    public sealed class AppSettings
    {
        public int HeadphoneVolume { get; set; }
        public int CableVolume { get; set; }
        public string HeadphoneDeviceName { get; set; }
        public string CableDeviceName { get; set; }
        public string MicrophoneDeviceName { get; set; }
        public TrackBinding[] Bindings { get; set; }
        public SavedTrack[] Tracks { get; set; }
        public string RandomKey { get; set; }
        public string[] RandomTrackIds { get; set; }
        public string[] RandomFileNames { get; set; }
        public ScenePreset[] Scenes { get; set; }
        public string ActiveSceneId { get; set; }
        public LoudnessAnalysis[] LoudnessAnalyses { get; set; }
        public string LoudnessBaselineFileName { get; set; }
        public string LoudnessBaselineFingerprint { get; set; }
        public double LoudnessBaselineLufs { get; set; }
        public bool LoudnessBaselineReady { get; set; }
        public bool LoudnessNormalizationInitialized { get; set; }
        [XmlIgnore]
        public bool NeedsSceneMigration { get; private set; }
        [XmlIgnore]
        public ScenePreset ActiveScene
        {
            get
            {
                if (Scenes != null)
                    foreach (ScenePreset scene in Scenes)
                        if (scene != null && String.Equals(scene.Id, ActiveSceneId, StringComparison.OrdinalIgnoreCase)) return scene;
                return Scenes != null && Scenes.Length > 0 ? Scenes[0] : null;
            }
        }

        public AppSettings()
        {
            RandomKey = "";
            RandomTrackIds = new string[0];
            RandomFileNames = new string[0];
            LoudnessAnalyses = new LoudnessAnalysis[0];
            LoudnessBaselineFileName = "";
            LoudnessBaselineFingerprint = "";
            LoudnessBaselineLufs = LoudnessAnalyzer.TargetLufs;
            LoudnessBaselineReady = false;
            LoudnessNormalizationInitialized = false;
            ActiveSceneId = "";
            HeadphoneVolume = 80;
            CableVolume = 80;
            HeadphoneDeviceName = "";
            CableDeviceName = "";
            MicrophoneDeviceName = "";
            Tracks = new SavedTrack[0];
            Bindings = new TrackBinding[0];
        }

        public string GetKey(string trackId)
        {
            ScenePreset scene = ActiveScene;
            TrackBinding[] bindings = scene == null ? Bindings : scene.Bindings;
            if (bindings != null)
            {
                foreach (TrackBinding binding in bindings)
                {
                    if (binding != null && String.Equals(binding.TrackId, trackId, StringComparison.OrdinalIgnoreCase))
                        return binding.Key ?? "";
                }
            }
            return "";
        }

        public void SetKey(string trackId, string key)
        {
            ScenePreset scene = ActiveScene;
            TrackBinding[] bindings = scene == null ? Bindings : scene.Bindings;
            if (bindings == null) bindings = new TrackBinding[0];
            for (int i = 0; i < bindings.Length; i++)
            {
                if (bindings[i] != null && String.Equals(bindings[i].TrackId, trackId, StringComparison.OrdinalIgnoreCase))
                {
                    bindings[i].Key = key;
                    if (scene == null) Bindings = bindings; else scene.Bindings = bindings;
                    return;
                }
            }

            TrackBinding[] expanded = new TrackBinding[bindings.Length + 1];
            Array.Copy(bindings, expanded, bindings.Length);
            expanded[expanded.Length - 1] = new TrackBinding { TrackId = trackId, Key = key };
            if (scene == null) Bindings = expanded; else scene.Bindings = expanded;
        }

        public SavedTrack[] ActiveTracks()
        {
            ScenePreset scene = ActiveScene;
            if (scene == null) return new SavedTrack[0];
            Dictionary<string, SavedTrack> assets = new Dictionary<string, SavedTrack>(StringComparer.OrdinalIgnoreCase);
            foreach (SavedTrack asset in Tracks ?? new SavedTrack[0])
                if (asset != null && !String.IsNullOrWhiteSpace(asset.Id) && !assets.ContainsKey(asset.Id)) assets.Add(asset.Id, asset);
            List<SavedTrack> result = new List<SavedTrack>();
            foreach (SceneTrack entry in scene.Tracks ?? new SceneTrack[0])
            {
                SavedTrack asset;
                if (entry == null || !assets.TryGetValue(entry.TrackId ?? "", out asset)) continue;
                result.Add(new SavedTrack { Id = asset.Id, Name = String.IsNullOrWhiteSpace(entry.Name) ? asset.Name : entry.Name, FileName = asset.FileName,
                    StartSeconds = entry.StartSeconds, EndSeconds = entry.EndSeconds });
            }
            return result.ToArray();
        }

        public SavedTrack FindActiveTrack(string id)
        {
            foreach (SavedTrack track in ActiveTracks())
                if (String.Equals(track.Id, id, StringComparison.OrdinalIgnoreCase)) return track;
            return null;
        }

        public SavedTrack FindAsset(string id)
        {
            foreach (SavedTrack track in Tracks ?? new SavedTrack[0])
                if (track != null && String.Equals(track.Id, id, StringComparison.OrdinalIgnoreCase)) return track;
            return null;
        }

        public SceneTrack FindSceneTrack(string id)
        {
            ScenePreset scene = ActiveScene;
            if (scene == null) return null;
            foreach (SceneTrack track in scene.Tracks ?? new SceneTrack[0])
                if (track != null && String.Equals(track.TrackId, id, StringComparison.OrdinalIgnoreCase)) return track;
            return null;
        }

        public string ActiveRandomKey
        {
            get { ScenePreset scene = ActiveScene; return scene == null ? RandomKey : (scene.RandomKey ?? ""); }
            set { ScenePreset scene = ActiveScene; if (scene == null) RandomKey = value; else scene.RandomKey = value ?? ""; }
        }

        public string[] ActiveRandomFiles
        {
            get { ScenePreset scene = ActiveScene; return scene == null ? (RandomFileNames ?? new string[0]) : (scene.RandomFileNames ?? new string[0]); }
            set { ScenePreset scene = ActiveScene; if (scene == null) RandomFileNames = value ?? new string[0]; else scene.RandomFileNames = value ?? new string[0]; }
        }

        public LoudnessAnalysis FindAnalysis(string fileName)
        {
            FileInfo currentFile;
            try
            {
                currentFile = new FileInfo(AppPaths.TrackPath(fileName));
                if (!currentFile.Exists) return null;
            }
            catch { return null; }
            foreach (LoudnessAnalysis item in LoudnessAnalyses ?? new LoudnessAnalysis[0])
                if (item != null && String.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase) &&
                    item.IsValidated &&
                    String.Equals(item.AlgorithmVersion, LoudnessAnalyzer.AlgorithmVersion, StringComparison.Ordinal) &&
                    item.FileLength == currentFile.Length && item.LastWriteTimeUtcTicks == currentFile.LastWriteTimeUtc.Ticks &&
                    LoudnessBaselineReady && Math.Abs(item.TargetLufs - LoudnessBaselineLufs) < 0.001) return item;
            return null;
        }

        public LoudnessAnalysis FindStoredAnalysis(string fileName)
        {
            foreach (LoudnessAnalysis item in LoudnessAnalyses ?? new LoudnessAnalysis[0])
                if (item != null && String.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase) &&
                    LoudnessAnalyzer.IsCompatibleAlgorithmVersion(item.AlgorithmVersion)) return item;
            return null;
        }

        public void SetAnalysis(LoudnessAnalysis analysis)
        {
            analysis.IsValidated = true;
            List<LoudnessAnalysis> items = new List<LoudnessAnalysis>();
            foreach (LoudnessAnalysis item in LoudnessAnalyses ?? new LoudnessAnalysis[0])
                if (item != null && !String.Equals(item.FileName, analysis.FileName, StringComparison.OrdinalIgnoreCase)) items.Add(item);
            items.Add(analysis);
            LoudnessAnalyses = items.ToArray();
        }

        public ScenePreset CloneActiveScene()
        {
            ScenePreset source = ActiveScene;
            if (source == null) return null;
            ScenePreset copy = new ScenePreset { Id = source.Id, Name = source.Name, RandomKey = source.RandomKey,
                RandomFileNames = (string[])(source.RandomFileNames ?? new string[0]).Clone(),
                RandomMode = RandomPlaybackMode.Normalize(source.RandomMode) };
            List<SceneTrack> tracks = new List<SceneTrack>();
            foreach (SceneTrack track in source.Tracks ?? new SceneTrack[0])
                tracks.Add(new SceneTrack { TrackId = track.TrackId, Name = track.Name, StartSeconds = track.StartSeconds, EndSeconds = track.EndSeconds });
            List<TrackBinding> bindings = new List<TrackBinding>();
            foreach (TrackBinding binding in source.Bindings ?? new TrackBinding[0])
                bindings.Add(new TrackBinding { TrackId = binding.TrackId, Key = binding.Key });
            copy.Tracks = tracks.ToArray(); copy.Bindings = bindings.ToArray();
            return copy;
        }

        public void RestoreScene(ScenePreset replacement)
        {
            if (replacement == null) return;
            List<ScenePreset> scenes = new List<ScenePreset>();
            foreach (ScenePreset scene in Scenes ?? new ScenePreset[0])
                scenes.Add(String.Equals(scene.Id, replacement.Id, StringComparison.OrdinalIgnoreCase) ? replacement : scene);
            Scenes = scenes.ToArray();
            TrackCatalog.Configure(this);
        }

        public ScenePreset CreateScene(string name, bool copyActive)
        {
            ScenePreset current = ActiveScene;
            ScenePreset created = new ScenePreset { Id = "scene_" + Guid.NewGuid().ToString("N"),
                Name = UniqueSceneName(name) };
            if (copyActive && current != null)
            {
                List<SceneTrack> tracks = new List<SceneTrack>();
                foreach (SceneTrack track in current.Tracks ?? new SceneTrack[0])
                    tracks.Add(new SceneTrack { TrackId = track.TrackId, Name = track.Name, StartSeconds = track.StartSeconds, EndSeconds = track.EndSeconds });
                List<TrackBinding> bindings = new List<TrackBinding>();
                foreach (TrackBinding binding in current.Bindings ?? new TrackBinding[0])
                    bindings.Add(new TrackBinding { TrackId = binding.TrackId, Key = binding.Key });
                created.Tracks = tracks.ToArray();
                created.Bindings = bindings.ToArray();
                created.RandomKey = current.RandomKey;
                created.RandomFileNames = (string[])(current.RandomFileNames ?? new string[0]).Clone();
                created.RandomMode = RandomPlaybackMode.Normalize(current.RandomMode);
            }
            List<ScenePreset> scenes = new List<ScenePreset>(Scenes ?? new ScenePreset[0]);
            scenes.Add(created);
            Scenes = scenes.ToArray();
            return created;
        }

        public bool RenameScene(string sceneId, string name)
        {
            ScenePreset scene = FindScene(sceneId);
            if (scene == null || String.IsNullOrWhiteSpace(name)) return false;
            string baseName = name.Trim();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ScenePreset item in Scenes ?? new ScenePreset[0])
                if (item != null && !String.Equals(item.Id, sceneId, StringComparison.OrdinalIgnoreCase)) names.Add(item.Name ?? "");
            string candidate = baseName; int suffix = 2;
            while (names.Contains(candidate)) candidate = baseName + " (" + suffix++ + ")";
            scene.Name = candidate;
            return true;
        }

        private string UniqueSceneName(string requested)
        {
            string baseName = String.IsNullOrWhiteSpace(requested) ? "新场景" : requested.Trim();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ScenePreset item in Scenes ?? new ScenePreset[0]) if (item != null) names.Add(item.Name ?? "");
            string candidate = baseName; int suffix = 2;
            while (names.Contains(candidate)) candidate = baseName + " (" + suffix++ + ")";
            return candidate;
        }

        public ScenePreset FindScene(string sceneId)
        {
            foreach (ScenePreset scene in Scenes ?? new ScenePreset[0])
                if (scene != null && String.Equals(scene.Id, sceneId, StringComparison.OrdinalIgnoreCase)) return scene;
            return null;
        }

        public bool SetActiveScene(string sceneId)
        {
            if (FindScene(sceneId) == null) return false;
            ActiveSceneId = sceneId;
            TrackCatalog.Configure(this);
            return true;
        }

        public bool DeleteScene(string sceneId)
        {
            if ((Scenes ?? new ScenePreset[0]).Length <= 1) return false;
            List<ScenePreset> scenes = new List<ScenePreset>();
            foreach (ScenePreset scene in Scenes)
                if (!String.Equals(scene.Id, sceneId, StringComparison.OrdinalIgnoreCase)) scenes.Add(scene);
            if (scenes.Count == (Scenes ?? new ScenePreset[0]).Length) return false;
            Scenes = scenes.ToArray();
            if (String.Equals(ActiveSceneId, sceneId, StringComparison.OrdinalIgnoreCase)) ActiveSceneId = Scenes[0].Id;
            TrackCatalog.Configure(this);
            return true;
        }

        public void Normalize()
        {
            NeedsSceneMigration = Scenes == null || Scenes.Length == 0;
            HeadphoneVolume = Clamp(HeadphoneVolume, 0, 100);
            CableVolume = Clamp(CableVolume, 0, 100);
            if (HeadphoneDeviceName == null) HeadphoneDeviceName = "";
            if (CableDeviceName == null) CableDeviceName = "";
            if (MicrophoneDeviceName == null) MicrophoneDeviceName = "";
            if (LoudnessBaselineFileName == null) LoudnessBaselineFileName = "";
            if (LoudnessBaselineFingerprint == null) LoudnessBaselineFingerprint = "";
            if (Double.IsNaN(LoudnessBaselineLufs) || Double.IsInfinity(LoudnessBaselineLufs) || LoudnessBaselineLufs < -70 || LoudnessBaselineLufs > 10)
            {
                LoudnessBaselineLufs = LoudnessAnalyzer.TargetLufs;
                LoudnessBaselineReady = false;
            }
            if (!IsAudioFileName(LoudnessBaselineFileName)) LoudnessBaselineFileName = "";
            if (!LoudnessBaselineReady) LoudnessBaselineFingerprint = "";
            if (Bindings == null) Bindings = new AppSettings().Bindings;

            if (Tracks == null)
            {
                Tracks = TrackCatalog.DefaultTracks();
            }
            List<SavedTrack> valid = new List<SavedTrack>();
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SavedTrack track in Tracks)
            {
                if (track == null || String.IsNullOrWhiteSpace(track.Id) || String.IsNullOrWhiteSpace(track.FileName) ||
                    !IsAudioFileName(track.FileName) || !ids.Add(track.Id)) continue;
                if (String.IsNullOrWhiteSpace(track.Name)) track.Name = Path.GetFileNameWithoutExtension(track.FileName);
                if (Double.IsNaN(track.StartSeconds) || Double.IsInfinity(track.StartSeconds) || track.StartSeconds < 0) track.StartSeconds = 0;
                if (track.EndSeconds.HasValue &&
                    (Double.IsNaN(track.EndSeconds.Value) || Double.IsInfinity(track.EndSeconds.Value) || track.EndSeconds.Value <= track.StartSeconds))
                    track.EndSeconds = null;
                valid.Add(track);
            }
            Tracks = valid.ToArray();
            Dictionary<string, SavedTrack> assetsById = new Dictionary<string, SavedTrack>(StringComparer.OrdinalIgnoreCase);
            foreach (SavedTrack asset in Tracks) assetsById.Add(asset.Id, asset);
            if (RandomKey == null) RandomKey = "";
            List<string> randomFiles = new List<string>();
            HashSet<string> seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in RandomFileNames ?? new string[0])
                if (IsAudioFileName(file) && seenFiles.Add(file)) randomFiles.Add(file);
            // Migrate the previous library-ID selection to independent file names.
            foreach (string id in RandomTrackIds ?? new string[0])
                foreach (SavedTrack track in Tracks)
                    if (String.Equals(track.Id, id, StringComparison.OrdinalIgnoreCase) && seenFiles.Add(track.FileName)) randomFiles.Add(track.FileName);
            RandomFileNames = randomFiles.ToArray();
            RandomTrackIds = new string[0];
            List<TrackBinding> active = new List<TrackBinding>();
            HashSet<string> boundIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TrackBinding binding in Bindings)
            {
                if (binding == null || binding.TrackId == null || !ids.Contains(binding.TrackId) || !boundIds.Add(binding.TrackId)) continue;
                string key = NormalizeKey(binding.Key);
                if (key.Length > 0 && !keys.Add(key)) key = "";
                active.Add(new TrackBinding { TrackId = binding.TrackId, Key = key });
            }
            RandomKey = NormalizeKey(RandomKey);
            if (keys.Contains(RandomKey)) RandomKey = "";
            Bindings = active.ToArray();
            if (NeedsSceneMigration)
            {
                ScenePreset scene = new ScenePreset { Id = "scene_" + Guid.NewGuid().ToString("N"), Name = "默认场景" };
                List<SceneTrack> entries = new List<SceneTrack>();
                foreach (SavedTrack track in Tracks)
                    entries.Add(new SceneTrack { TrackId = track.Id, Name = track.Name, StartSeconds = track.StartSeconds, EndSeconds = track.EndSeconds });
                scene.Tracks = entries.ToArray();
                scene.Bindings = (TrackBinding[])Bindings.Clone();
                scene.RandomKey = NormalizeKey(RandomKey);
                scene.RandomFileNames = (string[])RandomFileNames.Clone();
                Scenes = new ScenePreset[] { scene };
                ActiveSceneId = scene.Id;
            }
            List<ScenePreset> validScenes = new List<ScenePreset>();
            HashSet<string> sceneIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ScenePreset scene in Scenes ?? new ScenePreset[0])
            {
                if (scene == null || String.IsNullOrWhiteSpace(scene.Id) || !sceneIds.Add(scene.Id)) continue;
                if (String.IsNullOrWhiteSpace(scene.Name)) scene.Name = "未命名场景";
                scene.RandomMode = RandomPlaybackMode.Normalize(scene.RandomMode);
                List<SceneTrack> sceneTracks = new List<SceneTrack>();
                HashSet<string> sceneTrackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (SceneTrack entry in scene.Tracks ?? new SceneTrack[0])
                {
                    SavedTrack asset;
                    if (entry == null || !assetsById.TryGetValue(entry.TrackId ?? "", out asset) || !sceneTrackIds.Add(entry.TrackId)) continue;
                    if (Double.IsNaN(entry.StartSeconds) || Double.IsInfinity(entry.StartSeconds) || entry.StartSeconds < 0) entry.StartSeconds = 0;
                    if (entry.Name != null && entry.Name.Length > 256) entry.Name = entry.Name.Substring(0, 256);
                    if (entry.EndSeconds.HasValue && (Double.IsNaN(entry.EndSeconds.Value) || Double.IsInfinity(entry.EndSeconds.Value) || entry.EndSeconds.Value <= entry.StartSeconds)) entry.EndSeconds = null;
                    sceneTracks.Add(entry);
                }
                scene.Tracks = sceneTracks.ToArray();
                List<TrackBinding> sceneBindings = new List<TrackBinding>();
                HashSet<string> sceneKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> sceneBoundIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TrackBinding binding in scene.Bindings ?? new TrackBinding[0])
                {
                    if (binding == null || !sceneTrackIds.Contains(binding.TrackId) || !sceneBoundIds.Add(binding.TrackId)) continue;
                    string key = NormalizeKey(binding.Key);
                    if (key.Length > 0 && !sceneKeys.Add(key)) key = "";
                    sceneBindings.Add(new TrackBinding { TrackId = binding.TrackId, Key = key });
                }
                scene.RandomKey = NormalizeKey(scene.RandomKey);
                if (sceneKeys.Contains(scene.RandomKey)) scene.RandomKey = "";
                List<string> sceneRandom = new List<string>();
                HashSet<string> sceneRandomSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string file in scene.RandomFileNames ?? new string[0])
                    if (IsAudioFileName(file) && sceneRandomSet.Add(file)) sceneRandom.Add(file);
                scene.Bindings = sceneBindings.ToArray();
                scene.RandomFileNames = sceneRandom.ToArray();
                validScenes.Add(scene);
            }
            if (validScenes.Count == 0)
            {
                Scenes = new ScenePreset[0];
                NeedsSceneMigration = true;
                Normalize();
                return;
            }
            Scenes = validScenes.ToArray();
            bool activeExists = false;
            foreach (ScenePreset scene in Scenes) if (String.Equals(scene.Id, ActiveSceneId, StringComparison.OrdinalIgnoreCase)) activeExists = true;
            if (!activeExists) ActiveSceneId = Scenes[0].Id;
            List<LoudnessAnalysis> analyses = new List<LoudnessAnalysis>();
            HashSet<string> analysisFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LoudnessAnalysis item in LoudnessAnalyses ?? new LoudnessAnalysis[0])
                if (item != null && IsAudioFileName(item.FileName) && analysisFiles.Add(item.FileName) &&
                    item.TargetLufs >= -70 && item.TargetLufs <= 10 && item.GainDb >= -60 && item.GainDb <= 12 &&
                    item.IntegratedLufs >= -100 && item.IntegratedLufs <= 10 && item.PeakDbfs >= -100 && item.PeakDbfs <= 0) analyses.Add(item);
            LoudnessAnalyses = analyses.ToArray();
            if (LoudnessBaselineReady)
            {
                bool baselineVerified = false;
                foreach (LoudnessAnalysis item in LoudnessAnalyses)
                    if (item.IsValidated && String.Equals(item.FileName, LoudnessBaselineFileName, StringComparison.OrdinalIgnoreCase) &&
                        String.Equals(item.Fingerprint, LoudnessBaselineFingerprint, StringComparison.OrdinalIgnoreCase) &&
                        String.Equals(item.AlgorithmVersion, LoudnessAnalyzer.AlgorithmVersion, StringComparison.Ordinal) &&
                        item.FileLength > 0 && item.LastWriteTimeUtcTicks > 0 &&
                        Math.Abs(item.IntegratedLufs - LoudnessBaselineLufs) < 0.001)
                    { baselineVerified = true; break; }
                if (!baselineVerified)
                {
                    LoudnessBaselineReady = false;
                    LoudnessBaselineFileName = "";
                    LoudnessBaselineFingerprint = "";
                    LoudnessBaselineLufs = LoudnessAnalyzer.TargetLufs;
                }
            }
            TrackCatalog.Configure(this);
        }

        internal static bool IsAudioFileName(string file)
        {
            return !String.IsNullOrWhiteSpace(file) && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                Path.GetFileName(file) == file && AudioFormatCatalog.IsSupportedExtension(file);
        }

        internal static string NormalizeKey(string value)
        {
            Keys key;
            if (String.IsNullOrWhiteSpace(value) || !Enum.TryParse<Keys>(value.Trim(), true, out key)) return "";
            return ((key >= Keys.A && key <= Keys.Z) || (key >= Keys.D0 && key <= Keys.D9) ||
                (key >= Keys.NumPad0 && key <= Keys.NumPad9) || (key >= Keys.F1 && key <= Keys.F24)) ? key.ToString() : "";
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }

    [Serializable]
    public sealed class SavedTrack
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string FileName { get; set; }
        public double StartSeconds { get; set; }
        public double? EndSeconds { get; set; }
    }

    [Serializable]
    public sealed class TrackBinding
    {
        public string TrackId { get; set; }
        public string Key { get; set; }
    }

    [Serializable]
    public sealed class ScenePreset
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public SceneTrack[] Tracks { get; set; }
        public TrackBinding[] Bindings { get; set; }
        public string RandomKey { get; set; }
        public string[] RandomFileNames { get; set; }
        public string RandomMode { get; set; }
        public ScenePreset() { Tracks = new SceneTrack[0]; Bindings = new TrackBinding[0]; RandomFileNames = new string[0]; RandomKey = ""; RandomMode = RandomPlaybackMode.Normal; }
    }

    internal static class RandomPlaybackMode
    {
        public const string Normal = "normal";
        public const string Continuous = "continuous";

        public static string Normalize(string value)
        {
            if (String.Equals(value, Continuous, StringComparison.OrdinalIgnoreCase)) return Continuous;
            return Normal;
        }
    }

    [Serializable]
    public sealed class SceneTrack
    {
        public string TrackId { get; set; }
        public string Name { get; set; }
        public double StartSeconds { get; set; }
        public double? EndSeconds { get; set; }
    }

    [Serializable]
    public sealed class LoudnessAnalysis
    {
        public string FileName { get; set; }
        public string Fingerprint { get; set; }
        public double IntegratedLufs { get; set; }
        public double PeakDbfs { get; set; }
        public double GainDb { get; set; }
        public double TargetLufs { get; set; }
        public string AlgorithmVersion { get; set; }
        public bool IsValidated { get; set; }
        public long FileLength { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }
    }

    internal sealed class TrackDefinition
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string FileName { get; private set; }
        public double StartSeconds { get; private set; }
        public double? EndSeconds { get; private set; }
        public double GainDb { get; private set; }
        public double GainLinear { get; private set; }

        public TrackDefinition(string id, string name, string fileName, double startSeconds, double? endSeconds = null, double gainDb = 0)
        {
            Id = id;
            Name = name;
            FileName = fileName;
            StartSeconds = startSeconds;
            EndSeconds = endSeconds;
            GainDb = Math.Max(-60, Math.Min(LoudnessAnalyzer.MaximumBoostDb, gainDb));
            GainLinear = Math.Pow(10.0, GainDb / 20.0);
        }
    }

    internal static class TrackCatalog
    {
        public static TrackDefinition[] All { get; private set; }

        static TrackCatalog() { Configure(new AppSettings { Tracks = DefaultTracks() }); }

        public static SavedTrack[] DefaultTracks()
        {
            return new SavedTrack[0];
        }

        public static void Configure(AppSettings settings)
        {
            List<TrackDefinition> tracks = new List<TrackDefinition>();
            foreach (SavedTrack track in settings.ActiveTracks())
            {
                LoudnessAnalysis analysis = settings.FindAnalysis(track.FileName);
                double gain = analysis == null ? 0 : analysis.GainDb;
                tracks.Add(new TrackDefinition(track.Id, track.Name, track.FileName, track.StartSeconds, track.EndSeconds, gain));
            }
            All = tracks.ToArray();
        }

        public static TrackDefinition Find(string id)
        {
            foreach (TrackDefinition track in All)
                if (String.Equals(track.Id, id, StringComparison.OrdinalIgnoreCase))
                    return track;
            return null;
        }
    }

    internal static class RandomFileCatalog
    {
        internal static string[] Scan(string folder)
        {
            List<string> files = new List<string>();
            if (Directory.Exists(folder))
            {
                foreach (string path in Directory.GetFiles(folder))
                {
                    string name = Path.GetFileName(path);
                    if (AppSettings.IsAudioFileName(name)) files.Add(name);
                }
            }
            files.Sort(StringComparer.CurrentCultureIgnoreCase);
            return files.ToArray();
        }

        internal static string[] RetainAvailable(string[] selected, string[] available)
        {
            HashSet<string> existing = new HashSet<string>(available ?? new string[0], StringComparer.OrdinalIgnoreCase);
            List<string> retained = new List<string>();
            foreach (string file in selected ?? new string[0])
                if (existing.Contains(file)) retained.Add(file);
            return retained.ToArray();
        }
    }

    internal static class SettingsStore
    {
        private static bool _loadedFromBackup;

        public static AppSettings Load()
        {
            foreach (string path in new string[] { AppPaths.SettingsFile, AppPaths.SettingsFile + ".bak" })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    XmlSerializer serializer = new XmlSerializer(typeof(AppSettings));
                    AppSettings settings;
                    using (FileStream stream = File.OpenRead(path))
                    {
                        settings = (AppSettings)serializer.Deserialize(stream);
                    }
                    settings.Normalize();
                    _loadedFromBackup = String.Equals(path, AppPaths.SettingsFile + ".bak", StringComparison.OrdinalIgnoreCase);
                    if (settings.NeedsSceneMigration)
                    {
                        string migrationError;
                        if (!TrySave(settings, out migrationError)) AppPaths.Log("保存场景配置迁移失败：" + migrationError);
                    }
                    return settings;
                }
                catch (Exception ex) { AppPaths.Log("读取设置失败，尝试备份：" + ex.Message); }
            }

            AppSettings defaults = new AppSettings();
            defaults.Normalize();
            return defaults;
        }

        public static void Save(AppSettings settings)
        {
            string error;
            TrySave(settings, out error);
        }

        public static bool TrySave(AppSettings settings, out string error)
        {
            error = "";
            try
            {
                settings.Normalize();
                Directory.CreateDirectory(AppPaths.SettingsDirectory);
                string temporary = AppPaths.SettingsFile + ".tmp";
                XmlSerializer serializer = new XmlSerializer(typeof(AppSettings));
                using (FileStream stream = File.Create(temporary))
                    serializer.Serialize(stream, settings);
                if (File.Exists(AppPaths.SettingsFile))
                {
                    if (_loadedFromBackup)
                        File.Replace(temporary, AppPaths.SettingsFile, null);
                    else
                        File.Replace(temporary, AppPaths.SettingsFile, AppPaths.SettingsFile + ".bak");
                }
                else File.Move(temporary, AppPaths.SettingsFile);
                _loadedFromBackup = false;
                return true;
            }
            catch (Exception ex)
            {
                AppPaths.Log("保存设置失败：" + ex);
                error = ex.Message;
                return false;
            }
        }
    }
}
