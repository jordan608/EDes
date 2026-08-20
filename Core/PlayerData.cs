// ═══════════════════════════════════════════════════════════════════════════
//  PlayerData.cs — Player profiles + high scores (persisted separately)
//
//  Stored in %AppData%/EDes/players.json — kept apart from settings.json
//  so gameplay records and app configuration evolve independently.
//
//  Typical use from a game:
//      var p = App.Players;
//      p.SelectProfile("Alice");          // create/select the active player
//      ...
//      bool madeTable = p.SubmitScore(1234);   // at game over
//      var top = p.HighScores;            // for a leaderboard
//
//  Thread-safe: the game thread submits scores while the UI thread reads/edits
//  profiles, so mutations and saves are guarded by a lock.
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EDes
{
    public sealed class PlayerProfile
    {
        public string Name        { get; set; } = "Player";
        public int    GamesPlayed { get; set; }
        public int    BestScore   { get; set; }
        public long   TotalScore  { get; set; }
        public string LastPlayed  { get; set; } = "";
    }

    public sealed class HighScoreEntry
    {
        public string Name  { get; set; } = "";
        public int    Score { get; set; }
        public string Date  { get; set; } = "";
    }

    public sealed class PlayerStore
    {
        public const int MAX_SCORES = 10;

        // Persisted state (public properties → System.Text.Json round-trips these).
        public List<PlayerProfile>  Profiles       { get; set; } = new();
        public string               CurrentProfile { get; set; } = "Player";
        public List<HighScoreEntry> HighScores     { get; set; } = new();

        private readonly object _lock = new();

        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EDes");
        private static readonly string PathName = Path.Combine(Dir, "players.json");
        private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

        // ── Load / Save ────────────────────────────────────────────────────────
        public static PlayerStore Load()
        {
            try
            {
                if (File.Exists(PathName))
                {
                    var s = JsonSerializer.Deserialize<PlayerStore>(File.ReadAllText(PathName), Opts);
                    if (s != null) { s.Normalize(); return s; }
                }
            }
            catch (Exception ex) { App.Log($"[Players.Load] {ex.Message}"); }

            var fresh = new PlayerStore();
            fresh.Normalize();
            return fresh;
        }

        public void Save()
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(Dir);
                    File.WriteAllText(PathName, JsonSerializer.Serialize(this, Opts));
                }
            }
            catch (Exception ex) { App.Log($"[Players.Save] {ex.Message}"); }
        }

        // Guard against a missing/empty file leaving no usable profile.
        private void Normalize()
        {
            Profiles   ??= new();
            HighScores ??= new();
            if (Profiles.Count == 0) Profiles.Add(new PlayerProfile { Name = "Player" });
            if (string.IsNullOrWhiteSpace(CurrentProfile) ||
                !Profiles.Any(p => p.Name == CurrentProfile))
                CurrentProfile = Profiles[0].Name;
        }

        // ── Profiles ─────────────────────────────────────────────────────────
        public PlayerProfile Current => GetOrCreate(CurrentProfile);

        public PlayerProfile GetOrCreate(string name)
        {
            lock (_lock)
            {
                name = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim();
                var p = Profiles.FirstOrDefault(x => x.Name == name);
                if (p == null) { p = new PlayerProfile { Name = name }; Profiles.Add(p); }
                return p;
            }
        }

        public void SelectProfile(string name)
        {
            CurrentProfile = GetOrCreate(name).Name;
            Save();
        }

        // ── Scores ──────────────────────────────────────────────────────────
        /// <summary>
        /// Record a score for the current profile and the high-score table.
        /// Returns true if it placed on the (top-MAX_SCORES) leaderboard.
        /// </summary>
        public bool SubmitScore(int score)
        {
            bool madeTable;
            lock (_lock)
            {
                var p = GetOrCreate(CurrentProfile);
                p.GamesPlayed++;
                p.TotalScore += score;
                if (score > p.BestScore) p.BestScore = score;
                p.LastPlayed = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

                var entry = new HighScoreEntry
                {
                    Name  = p.Name,
                    Score = score,
                    Date  = DateTime.Now.ToString("yyyy-MM-dd"),
                };
                HighScores.Add(entry);
                HighScores.Sort((a, b) => b.Score.CompareTo(a.Score));
                if (HighScores.Count > MAX_SCORES)
                    HighScores.RemoveRange(MAX_SCORES, HighScores.Count - MAX_SCORES);
                madeTable = HighScores.Contains(entry);
            }
            Save();
            return madeTable;
        }

        public void ClearScores()
        {
            lock (_lock) { HighScores.Clear(); }
            Save();
        }
    }
}
