using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Kairos
{
    /// <summary>Listening statistics, kept only on this PC (%APPDATA%\Kairos\statistics.json).</summary>
    public sealed class Stats
    {
        public sealed class Song
        {
            public string Title { get; set; } = "";
            public string Artist { get; set; } = "";
            public int Plays { get; set; }
            public double Seconds { get; set; }
            /// <summary>"synced", "plain", "none" (not found), "nolyrics" (marked / instrumental) or "" (not known yet).</summary>
            public string Lyrics { get; set; } = "";
            public DateTime First { get; set; }
            public DateTime Last { get; set; }
        }

        public sealed class Data
        {
            public Dictionary<string, Song> Songs { get; set; } = new();
            /// <summary>Seconds listened per day ("yyyy-MM-dd", local time).</summary>
            public Dictionary<string, double> Days { get; set; } = new();
            /// <summary>Seconds listened in each hour of the day (0–23, local time).</summary>
            public double[] Hours { get; set; } = new double[24];
            public DateTime Since { get; set; } = DateTime.Now;
        }

        public Data D { get; private set; } = new();
        private readonly string? _path;

        // The song currently being listened to, for counting a "play".
        private string? _sessionKey;
        private double _sessionSeconds;
        private bool _sessionCounted;
        private double _unsaved;

        public Stats(string? path = null) => _path = path;

        public static string DefaultPath => Path.Combine(Settings.AppDataFolder, "statistics.json");

        public static Stats Load(string? path = null)
        {
            var stats = new Stats(path ?? DefaultPath);
            try
            {
                if (File.Exists(stats._path))
                {
                    var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(stats._path!));
                    if (data != null)
                    {
                        if (data.Hours == null || data.Hours.Length != 24) data.Hours = new double[24];
                        stats.D = data;
                    }
                }
            }
            catch { /* a damaged file starts fresh rather than stopping Kairos */ }
            return stats;
        }

        public void Save()
        {
            _unsaved = 0;
            if (_path == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(D));
                File.Move(tmp, _path, overwrite: true); // write then swap, so a crash can't leave half a file
            }
            catch { }
        }

        public void Reset()
        {
            D = new Data();
            _sessionKey = null;
            Save();
        }

        public static string KeyFor(string title, string artist) =>
            $"{artist.Trim().ToLowerInvariant()}\u0001{title.Trim().ToLowerInvariant()}";

        // ----- Recording -----

        /// <summary>Adds listening time to the song playing now. Call regularly (e.g. every second) while music plays.</summary>
        public void AddListening(string title, string artist, double durationSeconds, double seconds, DateTime now)
        {
            if (seconds <= 0 || string.IsNullOrWhiteSpace(title)) return;
            seconds = Math.Min(seconds, 5); // a long gap (sleep, freeze) shouldn't count as listening

            var key = KeyFor(title, artist);
            if (!D.Songs.TryGetValue(key, out var song))
                D.Songs[key] = song = new Song { Title = title.Trim(), Artist = artist.Trim(), First = now };
            song.Seconds += seconds;
            song.Last = now;

            var day = now.ToString("yyyy-MM-dd");
            D.Days[day] = D.Days.GetValueOrDefault(day) + seconds;
            D.Hours[now.Hour] += seconds;

            // A "play" counts once per listen, after 30 seconds (or half of a very short song).
            if (key != _sessionKey)
            {
                _sessionKey = key;
                _sessionSeconds = 0;
                _sessionCounted = false;
            }
            _sessionSeconds += seconds;
            double needed = durationSeconds > 0 ? Math.Min(30, durationSeconds * 0.5) : 30;
            if (!_sessionCounted && _sessionSeconds >= needed)
            {
                _sessionCounted = true;
                song.Plays++;
            }

            _unsaved += seconds;
            if (_unsaved >= 30) Save();
        }

        /// <summary>A new song started (or the same one again): the next listen counts as a new play.</summary>
        public void NewListen() => _sessionKey = null;

        public void SetLyrics(string title, string artist, string state, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(title)) return;
            var key = KeyFor(title, artist);
            if (!D.Songs.TryGetValue(key, out var song))
                D.Songs[key] = song = new Song { Title = title.Trim(), Artist = artist.Trim(), First = now, Last = now };
            song.Lyrics = state;
        }

        // ----- Reading -----

        public double TotalSeconds => D.Days.Values.Sum();
        public double SecondsOn(DateTime day) => D.Days.GetValueOrDefault(day.ToString("yyyy-MM-dd"));
        public double LastDaysSeconds(DateTime now, int days) =>
            Enumerable.Range(0, days).Sum(i => SecondsOn(now.Date.AddDays(-i)));

        public IEnumerable<Song> Played => D.Songs.Values.Where(s => s.Plays > 0);
        public int SongsPlayed => Played.Count();

        /// <summary>Days in a row with at least a minute of listening, up to today (today can still be to come).</summary>
        public int Streak(DateTime now)
        {
            var day = now.Date;
            if (SecondsOn(day) < 60) day = day.AddDays(-1);
            int streak = 0;
            while (SecondsOn(day) >= 60) { streak++; day = day.AddDays(-1); }
            return streak;
        }

        public List<Song> TopSongs(int n) =>
            Played.OrderByDescending(s => s.Plays).ThenByDescending(s => s.Seconds).Take(n).ToList();

        public List<(string Artist, int Plays, double Seconds)> TopArtists(int n) =>
            Played.GroupBy(s => s.Artist, StringComparer.OrdinalIgnoreCase)
                  .Select(g => (Artist: g.First().Artist, Plays: g.Sum(s => s.Plays), Seconds: g.Sum(s => s.Seconds)))
                  .OrderByDescending(a => a.Plays).ThenByDescending(a => a.Seconds)
                  .Take(n).ToList();

        /// <summary>Of the songs you've played: how many had synced, plain, or no lyrics found. Songs without words aren't counted.</summary>
        public (int Synced, int Plain, int None, int NoLyrics) Coverage()
        {
            int synced = 0, plain = 0, none = 0, nolyrics = 0;
            foreach (var s in Played)
            {
                switch (s.Lyrics)
                {
                    case "synced": synced++; break;
                    case "plain": plain++; break;
                    case "none": none++; break;
                    case "nolyrics": nolyrics++; break;
                }
            }
            return (synced, plain, none, nolyrics);
        }

        /// <summary>Your most-played songs that don't have synced lyrics yet: the best ones to add or sync.</summary>
        public List<Song> MissingSynced(int n) =>
            Played.Where(s => s.Lyrics == "plain" || s.Lyrics == "none")
                  .OrderByDescending(s => s.Plays).ThenByDescending(s => s.Seconds)
                  .Take(n).ToList();

        public static string Duration(double seconds)
        {
            if (seconds < 60) return seconds < 1 ? "0 m" : "under 1 m";
            var t = TimeSpan.FromSeconds(seconds);
            if (t.TotalHours >= 100) return $"{(int)t.TotalHours} h";
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} m";
            return $"{t.Minutes} m";
        }
    }
}
