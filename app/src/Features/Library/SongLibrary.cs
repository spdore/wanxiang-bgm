using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace BgmHotkey
{
    internal static class SongLibrary
    {
        public static bool IsLibraryAudioPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            try
            {
                return IsLibraryAudioLocation(path) && File.Exists(Path.GetFullPath(path));
            }
            catch { return false; }
        }

        public static bool IsLibraryAudioLocation(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string root = Path.GetFullPath(AppPaths.TrackPath("")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(path);
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static string FindReusableAudioPath(string candidatePath, Func<bool> cancelRequested)
        {
            string candidate = Path.GetFullPath(candidatePath);
            FileInfo candidateInfo = new FileInfo(candidate);
            if (!candidateInfo.Exists) throw new FileNotFoundException("找不到待导入音频。", candidate);

            string candidateFingerprint = null;
            string candidateExtension = Path.GetExtension(candidate);
            foreach (string fileName in RandomFileCatalog.Scan(AppPaths.TrackPath("")))
            {
                if (cancelRequested != null && cancelRequested()) throw new OperationCanceledException("音频去重已取消。");
                string existingPath = AppPaths.TrackPath(fileName);
                if (String.Equals(candidate, Path.GetFullPath(existingPath), StringComparison.OrdinalIgnoreCase)) return existingPath;
                if (!String.Equals(candidateExtension, Path.GetExtension(existingPath), StringComparison.OrdinalIgnoreCase)) continue;

                FileInfo existingInfo = new FileInfo(existingPath);
                if (!existingInfo.Exists || existingInfo.Length != candidateInfo.Length) continue;
                if (candidateFingerprint == null) candidateFingerprint = Fingerprint(candidate, cancelRequested);
                string existingFingerprint = Fingerprint(existingPath, cancelRequested);
                if (String.Equals(candidateFingerprint, existingFingerprint, StringComparison.OrdinalIgnoreCase)) return existingPath;
            }
            return candidate;
        }

        public static SavedTrack Add(AppSettings settings, string sourcePath)
        {
            settings.Normalize();
            string source = Path.GetFullPath(sourcePath);
            if (!File.Exists(source)) throw new FileNotFoundException("歌曲文件不存在。", source);
            if (!AudioFormatCatalog.IsSupportedExtension(source))
                throw new InvalidOperationException("请选择支持的音频格式：" + AudioFormatCatalog.SupportedExtensionsText + "。");
            SavedTrack asset = null;
            foreach (SavedTrack existing in settings.Tracks)
                if (String.Equals(Path.GetFullPath(AppPaths.TrackPath(existing.FileName)), source, StringComparison.OrdinalIgnoreCase))
                { asset = existing; break; }
            Directory.CreateDirectory(AppPaths.TrackPath(""));
            string fileName = asset == null ? Path.GetFileName(source) : asset.FileName;
            string destination = AppPaths.TrackPath(fileName);
            bool copy = asset == null && !String.Equals(source, Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase);
            if (asset == null && copy)
            {
                string stem = Path.GetFileNameWithoutExtension(fileName);
                int suffix = 1;
                HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (SavedTrack existing in settings.Tracks) usedNames.Add(existing.FileName);
                while (File.Exists(destination) || usedNames.Contains(fileName))
                {
                    fileName = stem + "_" + suffix++ + Path.GetExtension(source);
                    destination = AppPaths.TrackPath(fileName);
                }
                File.Copy(source, destination, false);
            }
            SavedTrack track = asset;
            if (track == null)
            {
                track = new SavedTrack { Id = "song_" + Guid.NewGuid().ToString("N"), Name = Path.GetFileNameWithoutExtension(source), FileName = fileName };
                List<SavedTrack> tracks = new List<SavedTrack>(settings.Tracks);
                tracks.Add(track);
                settings.Tracks = tracks.ToArray();
            }
            ScenePreset scene = settings.ActiveScene;
            bool alreadyInScene = false;
            foreach (SceneTrack item in scene.Tracks)
                if (String.Equals(item.TrackId, track.Id, StringComparison.OrdinalIgnoreCase)) alreadyInScene = true;
            if (!alreadyInScene)
            {
                List<SceneTrack> sceneTracks = new List<SceneTrack>(scene.Tracks ?? new SceneTrack[0]);
                sceneTracks.Add(new SceneTrack { TrackId = track.Id, StartSeconds = 0 });
                scene.Tracks = sceneTracks.ToArray();
                settings.SetKey(track.Id, "");
            }
            settings.Normalize();
            return settings.FindActiveTrack(track.Id);
        }

        public static SavedTrack AddPrepared(AppSettings settings, string preparedPath, string sourceFileName)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (String.IsNullOrWhiteSpace(preparedPath)) throw new ArgumentException("Prepared file path is required.", "preparedPath");
            if (String.IsNullOrWhiteSpace(sourceFileName)) throw new ArgumentException("Source file name is required.", "sourceFileName");
            settings.Normalize();
            string prepared = Path.GetFullPath(preparedPath);
            if (!File.Exists(prepared)) throw new FileNotFoundException("曲库临时音频文件不存在。", prepared);
            string extension = Path.GetExtension(sourceFileName);
            if (!AudioFormatCatalog.IsSupportedExtension(sourceFileName))
                throw new InvalidOperationException("准备导入的音频格式不受支持。");

            string fileName = Path.GetFileName(sourceFileName);
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string destination = AppPaths.TrackPath(fileName);
            int suffix = 1;
            HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SavedTrack existing in settings.Tracks)
                if (existing != null) usedNames.Add(existing.FileName);
            while (File.Exists(destination) || usedNames.Contains(fileName))
            {
                fileName = stem + "_" + suffix++ + extension;
                destination = AppPaths.TrackPath(fileName);
            }

            bool moved = false;
            try
            {
                File.Move(prepared, destination);
                moved = true;
                SavedTrack track = Add(settings, destination);
                if (track == null) throw new InvalidOperationException("曲库没有返回新歌曲。");
                return track;
            }
            catch
            {
                if (moved)
                {
                    try { if (File.Exists(destination) && !File.Exists(prepared)) File.Move(destination, prepared); }
                    catch { }
                }
                throw;
            }
        }

        private static string Fingerprint(string path, Func<bool> cancelRequested)
        {
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[64 * 1024];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (cancelRequested != null && cancelRequested()) throw new OperationCanceledException("音频去重已取消。");
                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                }
                if (cancelRequested != null && cancelRequested()) throw new OperationCanceledException("音频去重已取消。");
                hash.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        public static void Remove(AppSettings settings, string id)
        {
            settings.Normalize();
            ScenePreset scene = settings.ActiveScene;
            List<SceneTrack> tracks = new List<SceneTrack>();
            foreach (SceneTrack track in scene.Tracks ?? new SceneTrack[0])
                if (!String.Equals(track.TrackId, id, StringComparison.OrdinalIgnoreCase)) tracks.Add(track);
            scene.Tracks = tracks.ToArray();
            List<TrackBinding> bindings = new List<TrackBinding>();
            foreach (TrackBinding binding in scene.Bindings ?? new TrackBinding[0])
                if (!String.Equals(binding.TrackId, id, StringComparison.OrdinalIgnoreCase)) bindings.Add(binding);
            scene.Bindings = bindings.ToArray();
            settings.Normalize();
        }
    }
}
