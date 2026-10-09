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
            if (!String.Equals(Path.GetExtension(source), ".mp3", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择 MP3 音频文件。");
            foreach (SavedTrack existing in settings.Tracks)
                if (String.Equals(Path.GetFullPath(AppPaths.TrackPath(existing.FileName)), source, StringComparison.OrdinalIgnoreCase))
                    return existing;
            Directory.CreateDirectory(AppPaths.TrackPath(""));
            string fileName = Path.GetFileName(source);
            string destination = AppPaths.TrackPath(fileName);
            bool copy = !String.Equals(source, Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase);
            if (copy)
            {
                string stem = Path.GetFileNameWithoutExtension(fileName);
                int suffix = 2;
                HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (SavedTrack existing in settings.Tracks) usedNames.Add(existing.FileName);
                while (File.Exists(destination) || usedNames.Contains(fileName))
                {
                    fileName = stem + "_" + suffix++ + ".mp3";
                    destination = AppPaths.TrackPath(fileName);
                }
                File.Copy(source, destination, false);
            }
            SavedTrack track = new SavedTrack { Id = "song_" + Guid.NewGuid().ToString("N"), Name = Path.GetFileNameWithoutExtension(source), FileName = fileName };
            List<SavedTrack> tracks = new List<SavedTrack>(settings.Tracks);
            tracks.Add(track);
            settings.Tracks = tracks.ToArray();
            settings.SetKey(track.Id, "");
            settings.Normalize();
            return track;
        }

        public static void Remove(AppSettings settings, string id)
        {
            settings.Normalize();
            List<SavedTrack> tracks = new List<SavedTrack>();
            foreach (SavedTrack track in settings.Tracks)
                if (!String.Equals(track.Id, id, StringComparison.OrdinalIgnoreCase)) tracks.Add(track);
            settings.Tracks = tracks.ToArray();
            settings.Normalize();
        }
    }
}
