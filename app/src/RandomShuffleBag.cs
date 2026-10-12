using System;
using System.Collections.Generic;

namespace BgmHotkey
{
    internal sealed class RandomShuffleBag
    {
        private sealed class Bag
        {
            public readonly List<string> Pool = new List<string>();
            public readonly List<string> Remaining = new List<string>();
            public readonly HashSet<string> Played = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public string Last;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Bag> _scenes = new Dictionary<string, Bag>(StringComparer.OrdinalIgnoreCase);
        private readonly Random _random = new Random();

        public string Next(string sceneId, IEnumerable<string> fileNames)
        {
            lock (_gate)
            {
                Bag bag = Get(sceneId);
                Reconcile(bag, fileNames);
                List<string> eligible = new List<string>();
                foreach (string file in bag.Pool)
                    if (!bag.Failed.Contains(file)) eligible.Add(file);
                if (eligible.Count == 0) return null;
                if (bag.Remaining.Count == 0)
                {
                    bag.Played.Clear();
                    bag.Remaining.AddRange(eligible);
                    Shuffle(bag.Remaining);
                    if (eligible.Count > 1 && String.Equals(bag.Remaining[0], bag.Last, StringComparison.OrdinalIgnoreCase))
                    {
                        int swap = 1 + _random.Next(bag.Remaining.Count - 1);
                        string first = bag.Remaining[0]; bag.Remaining[0] = bag.Remaining[swap]; bag.Remaining[swap] = first;
                    }
                }
                string next = bag.Remaining[0];
                bag.Remaining.RemoveAt(0);
                bag.Played.Add(next);
                bag.Last = next;
                return next;
            }
        }

        public void Refresh(string sceneId)
        {
            lock (_gate)
            {
                Bag bag = Get(sceneId);
                bag.Failed.Clear();
            }
        }

        public void MarkFailed(string sceneId, string fileName)
        {
            if (String.IsNullOrWhiteSpace(fileName)) return;
            lock (_gate)
            {
                Bag bag = Get(sceneId);
                bag.Failed.Add(fileName);
                bag.Remaining.RemoveAll(delegate(string file) { return String.Equals(file, fileName, StringComparison.OrdinalIgnoreCase); });
            }
        }

        private Bag Get(string sceneId)
        {
            Bag bag;
            if (!_scenes.TryGetValue(sceneId ?? "", out bag))
            {
                bag = new Bag();
                _scenes.Add(sceneId ?? "", bag);
            }
            return bag;
        }

        private void Reconcile(Bag bag, IEnumerable<string> fileNames)
        {
            List<string> next = new List<string>();
            HashSet<string> incoming = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (fileNames != null)
                foreach (string file in fileNames)
                    if (!String.IsNullOrWhiteSpace(file) && incoming.Add(file)) next.Add(file);

            HashSet<string> old = new HashSet<string>(bag.Pool, StringComparer.OrdinalIgnoreCase);
            bag.Remaining.RemoveAll(delegate(string file) { return !incoming.Contains(file) || bag.Failed.Contains(file); });
            bag.Played.RemoveWhere(delegate(string file) { return !incoming.Contains(file); });
            bag.Failed.RemoveWhere(delegate(string file) { return !incoming.Contains(file); });
            foreach (string file in next)
            {
                if (!old.Contains(file) && !bag.Failed.Contains(file) && !bag.Played.Contains(file))
                {
                    int insertion = bag.Remaining.Count == 0 ? 0 : _random.Next(bag.Remaining.Count + 1);
                    bag.Remaining.Insert(insertion, file);
                }
            }
            bag.Pool.Clear();
            bag.Pool.AddRange(next);
            if (bag.Remaining.Count == 0)
            {
                foreach (string file in bag.Pool)
                    if (!bag.Played.Contains(file) && !bag.Failed.Contains(file)) bag.Remaining.Add(file);
                if (bag.Remaining.Count > 1) Shuffle(bag.Remaining);
            }
        }

        private void Shuffle(List<string> values)
        {
            for (int i = values.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                string value = values[i]; values[i] = values[j]; values[j] = value;
            }
        }
    }
}
