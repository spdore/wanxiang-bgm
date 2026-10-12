using System;
using System.Collections.Generic;
using System.IO;

namespace BgmHotkey
{
    internal static class SongLibrary
    {
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
