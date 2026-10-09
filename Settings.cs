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
            Path.Combine(LocalDataDirectory(), "BgmHotkeyShare");
        public static readonly string SettingsFile = Path.Combine(SettingsDirectory, "settings.xml");
        public static readonly string LogFile = Path.Combine(SettingsDirectory, "app.log");

        private static string LocalDataDirectory()
        {
            string directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (String.IsNullOrWhiteSpace(directory)) directory = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (String.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
                throw new InvalidOperationException("无法获取当前用户的本地应用数据目录。");
            return directory;
        }

        public static string TrackPath(string fileName)
        {
            return Path.Combine(SettingsDirectory, "bgm", fileName);
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

        public AppSettings()
        {
            RandomKey = "";
            RandomTrackIds = new string[0];
            RandomFileNames = new string[0];
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
            if (Bindings != null)
            {
                foreach (TrackBinding binding in Bindings)
                {
                    if (binding != null && String.Equals(binding.TrackId, trackId, StringComparison.OrdinalIgnoreCase))
                        return binding.Key ?? "";
                }
            }
            return "";
        }

        public void SetKey(string trackId, string key)
        {
            if (Bindings == null)
                Bindings = new TrackBinding[0];
            for (int i = 0; i < Bindings.Length; i++)
            {
                if (Bindings[i] != null && String.Equals(Bindings[i].TrackId, trackId, StringComparison.OrdinalIgnoreCase))
                {
                    Bindings[i].Key = key;
                    return;
                }
            }

            TrackBinding[] expanded = new TrackBinding[Bindings.Length + 1];
            Array.Copy(Bindings, expanded, Bindings.Length);
            expanded[expanded.Length - 1] = new TrackBinding { TrackId = trackId, Key = key };
            Bindings = expanded;
        }

        public void Normalize()
        {
            HeadphoneVolume = Clamp(HeadphoneVolume, 0, 100);
            CableVolume = Clamp(CableVolume, 0, 100);
            if (HeadphoneDeviceName == null) HeadphoneDeviceName = "";
            if (CableDeviceName == null) CableDeviceName = "";
            if (MicrophoneDeviceName == null) MicrophoneDeviceName = "";
            if (Bindings == null) Bindings = new AppSettings().Bindings;

            if (Tracks == null)
            {
                Tracks = TrackCatalog.DefaultTracks();
                AppSettings defaults = new AppSettings();
                foreach (SavedTrack track in Tracks)
                {
                    bool exists = false;
                    foreach (TrackBinding binding in Bindings)
                        if (binding != null && binding.TrackId == track.Id) exists = true;
                    string key = defaults.GetKey(track.Id);
                    bool occupied = false;
                    foreach (TrackBinding binding in Bindings)
                        if (binding != null && binding.TrackId != "thirteen_216" && String.Equals(binding.Key, key, StringComparison.OrdinalIgnoreCase)) occupied = true;
                    if (!exists) SetKey(track.Id, occupied ? "" : key);
                }
            }
            List<SavedTrack> valid = new List<SavedTrack>();
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SavedTrack track in Tracks)
            {
                if (track == null || String.IsNullOrWhiteSpace(track.Id) || String.IsNullOrWhiteSpace(track.FileName) ||
                    !IsAudioFileName(track.FileName) || !ids.Add(track.Id)) continue;
                if (String.IsNullOrWhiteSpace(track.Name)) track.Name = Path.GetFileNameWithoutExtension(track.FileName);
                if (Double.IsNaN(track.StartSeconds) || Double.IsInfinity(track.StartSeconds) || track.StartSeconds < 0) track.StartSeconds = 0;
                valid.Add(track);
            }
            Tracks = valid.ToArray();
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
            TrackCatalog.Configure(this);
        }

        internal static bool IsAudioFileName(string file)
        {
            return !String.IsNullOrWhiteSpace(file) && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                Path.GetFileName(file) == file && String.Equals(Path.GetExtension(file), ".mp3", StringComparison.OrdinalIgnoreCase);
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
    }

    [Serializable]
    public sealed class TrackBinding
    {
        public string TrackId { get; set; }
        public string Key { get; set; }
    }

    internal sealed class TrackDefinition
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string FileName { get; private set; }
        public double StartSeconds { get; private set; }

        public TrackDefinition(string id, string name, string fileName, double startSeconds)
        {
            Id = id;
            Name = name;
            FileName = fileName;
            StartSeconds = startSeconds;
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
            foreach (SavedTrack track in settings.Tracks ?? DefaultTracks())
                tracks.Add(new TrackDefinition(track.Id, track.Name, track.FileName, track.StartSeconds));
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

    internal static class SettingsStore
    {
        public static AppSettings Load()
        {
            foreach (string path in new string[] { AppPaths.SettingsFile, AppPaths.SettingsFile + ".bak" })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    XmlSerializer serializer = new XmlSerializer(typeof(AppSettings));
                    using (FileStream stream = File.OpenRead(path))
                    {
                        AppSettings settings = (AppSettings)serializer.Deserialize(stream);
                        settings.Normalize();
                        return settings;
                    }
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
                    File.Replace(temporary, AppPaths.SettingsFile, AppPaths.SettingsFile + ".bak");
                else File.Move(temporary, AppPaths.SettingsFile);
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
