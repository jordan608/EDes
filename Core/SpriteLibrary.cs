using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EDes
{
    // ==========================================================================
    //  SPRITE LIBRARY
    //
    //  Discovers every sprite animation under Assets/Sprites — each folder that
    //  DIRECTLY contains PNG frames is one selectable animation (e.g.
    //  "Assets/Sprites/Explosion", "Assets/Sprites/Muzzle Flash", "Assets/Sprites/Pickup Sparkle").
    //  The folder scan is cheap (no decode); each set's frames are decoded lazily
    //  the first time it's selected, then cached, so switching sets is instant
    //  after that. Pair with SpriteBurstRenderer to actually draw one.
    // ==========================================================================
    public static class SpriteLibrary
    {
        private const string Root = "Assets/Sprites";

        private static readonly object  _lock = new();
        private static bool             _scanned;
        private static List<string>     _dirs   = new();   // full path per set
        private static List<string>     _names  = new();   // display name per set
        private static SpriteFrameSet?[] _sets   = Array.Empty<SpriteFrameSet?>();
        private static bool[]           _loaded = Array.Empty<bool>();

        /// <summary>Display names of all discovered animations (folder names), sorted.</summary>
        public static IReadOnlyList<string> Names { get { EnsureScanned(); return _names; } }
        public static int Count { get { EnsureScanned(); return _names.Count; } }

        private static void EnsureScanned()
        {
            if (_scanned) return;
            lock (_lock)
            {
                if (_scanned) return;
                try
                {
                    if (Directory.Exists(Root))
                    {
                        // Any folder (at any depth) that directly holds ≥1 PNG is an animation.
                        var found = Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories)
                            .Where(d => Directory.EnumerateFiles(d, "*.png").Any())
                            .ToList();

                        // Sort by display name; disambiguate duplicate leaf names with a suffix.
                        var ordered = found.OrderBy(LeafName, StringComparer.OrdinalIgnoreCase).ToList();
                        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (var d in ordered)
                        {
                            string name = LeafName(d);
                            if (seen.TryGetValue(name, out int k)) { seen[name] = k + 1; name = $"{name} ({k + 1})"; }
                            else seen[name] = 1;
                            _dirs.Add(d);
                            _names.Add(name);
                        }
                    }
                }
                catch (Exception ex) { App.Log($"[SpriteLibrary] scan failed: {ex.Message}"); }

                _sets   = new SpriteFrameSet?[_dirs.Count];
                _loaded = new bool[_dirs.Count];
                App.Log($"[SpriteLibrary] found {_dirs.Count} sprite animation(s) under {Root}.");
                _scanned = true;
            }
        }

        private static string LeafName(string dir)
            => new DirectoryInfo(dir).Name;

        /// <summary>Index of a discovered animation by its display (folder) name, or −1 if
        /// not found. Case-insensitive.</summary>
        public static int IndexOf(string name)
        {
            EnsureScanned();
            for (int i = 0; i < _names.Count; i++)
                if (string.Equals(_names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>Get a loaded frame set by index (lazily decodes + caches on first
        /// use). Returns null if there are no sets or decode failed.</summary>
        public static SpriteFrameSet? Get(int index)
        {
            EnsureScanned();
            if (_dirs.Count == 0) return null;
            index = Math.Clamp(index, 0, _dirs.Count - 1);
            if (!_loaded[index])
            {
                _loaded[index] = true;
                _sets[index]   = SpriteFrameSet.Load(_dirs[index]);
            }
            return _sets[index];
        }
    }
}
