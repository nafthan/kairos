using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Kairos
{
    public sealed record LyricLine(TimeSpan Time, string Text);

    public enum LyricsSource { Online, Local }

    public sealed class Lyrics
    {
        public LyricsSource Source { get; init; } = LyricsSource.Online;
        /// <summary>Lines in order. For unsynced lyrics every Time is zero.</summary>
        public List<LyricLine> Lines { get; init; } = new();
        public bool IsSynced { get; init; }
        public bool IsInstrumental { get; init; }
        public bool IsEmpty => Lines.Count == 0 && !IsInstrumental;
    }

    /// <summary>
    /// Fetches time-synced lyrics from LRCLIB (https://lrclib.net), a free and open lyrics database.
    /// Spotify's own lyrics aren't available to other apps, so an online source is needed.
    /// Results are cached on disk so a song is only looked up once.
    /// </summary>
    public sealed class LyricsService
    {
        private static readonly HttpClient Http = CreateClient();
        // "v2": earlier versions could cache unsynced lyrics when synced ones existed.
        private static string CacheFolder => Path.Combine(Settings.AppDataFolder, "cache-v2");

        private static HttpClient CreateClient()
        {
            var http = new HttpClient
            {
                BaseAddress = new Uri("https://lrclib.net/"),
                Timeout = TimeSpan.FromSeconds(15),
            };
            // LRCLIB asks apps to identify themselves.
            http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
            return http;
        }

        public async Task<Lyrics> GetAsync(TrackInfo track, CancellationToken ct, bool bypassCache = false, string? localFolder = null)
        {
            // Your own lyrics folder always comes first.
            if (localFolder != null)
            {
                var file = LocalLyrics.FindFile(localFolder, track);
                var local = file == null ? null : LocalLyrics.Load(file);
                if (local != null) return local;
            }

            string cacheFile = Path.Combine(CacheFolder, Hash(track.Key) + ".json");

            if (!bypassCache)
            {
                var cached = ReadCache(cacheFile);
                if (cached != null) return cached;
            }

            var result = await FetchAsync(track, ct);

            // Cache hits and confirmed misses (so we don't keep asking for songs that have no lyrics).
            WriteCache(cacheFile, result);
            return result;
        }

        // ----------------------------------------------------------------

        private async Task<Lyrics> FetchAsync(TrackInfo track, CancellationToken ct)
        {
            // Time-synced lyrics are what make the highlighting work, so keep looking for a synced
            // version and only settle for unsynced text if nothing better turns up.
            Lyrics? fallback = null;

            // 1) Exact match on title + artist + album + duration.
            var exact = await GetExactAsync(track.Title, track.Artist, track.Album, track.Duration, ct);
            if (exact?.IsSynced == true) return exact;
            if (exact != null && exact.IsInstrumental && exact.Lines.Count == 0) return exact;
            if (exact != null && !exact.IsEmpty) fallback = exact;

            // 2) Same, with the song title cleaned of " - Remastered 2011" style suffixes and only the first artist.
            string cleanTitle = CleanTitle(track.Title);
            string firstArtist = FirstArtist(track.Artist);
            if (cleanTitle != track.Title || firstArtist != track.Artist)
            {
                var cleaned = await GetExactAsync(cleanTitle, firstArtist, track.Album, track.Duration, ct);
                if (cleaned?.IsSynced == true) return cleaned;
                if (cleaned != null && !cleaned.IsEmpty) fallback ??= cleaned;

                // Also try without the album, which is often named differently between services.
                var noAlbum = await GetExactAsync(cleanTitle, firstArtist, "", track.Duration, ct);
                if (noAlbum?.IsSynced == true) return noAlbum;
                if (noAlbum != null && !noAlbum.IsEmpty) fallback ??= noAlbum;
            }

            // 3) Search, preferring synced results with the closest duration.
            var found = await SearchAsync(cleanTitle, firstArtist, track.Duration, ct);
            if (found?.IsSynced == true) return found;
            if (found != null && !found.IsEmpty) fallback ??= found;

            // 4) Broad search on title + artist as free text (catches differently-split artist names).
            var broad = await SearchAsync(cleanTitle, "", track.Duration, ct, freeText: $"{cleanTitle} {firstArtist}");
            if (broad?.IsSynced == true) return broad;
            if (broad != null && !broad.IsEmpty) fallback ??= broad;

            return fallback ?? exact ?? new Lyrics();
        }

        private async Task<Lyrics?> GetExactAsync(string title, string artist, string album, TimeSpan duration, CancellationToken ct)
        {
            var q = new StringBuilder("api/get?");
            q.Append("track_name=").Append(Uri.EscapeDataString(title));
            q.Append("&artist_name=").Append(Uri.EscapeDataString(artist));
            if (!string.IsNullOrWhiteSpace(album)) q.Append("&album_name=").Append(Uri.EscapeDataString(album));
            if (duration > TimeSpan.Zero) q.Append("&duration=").Append((int)Math.Round(duration.TotalSeconds));

            using var resp = await Http.GetAsync(q.ToString(), ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return FromJson(doc.RootElement);
        }

        private async Task<Lyrics?> SearchAsync(string title, string artist, TimeSpan duration, CancellationToken ct, string? freeText = null)
        {
            string url = freeText != null
                ? $"api/search?q={Uri.EscapeDataString(freeText)}"
                : $"api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            using var resp = await Http.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode(); // server errors shouldn't be cached as "no lyrics"

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            double target = duration.TotalSeconds;
            var candidates = doc.RootElement.EnumerateArray()
                .Select(e => new
                {
                    Element = e,
                    Synced = HasString(e, "syncedLyrics"),
                    Plain = HasString(e, "plainLyrics"),
                    Diff = target > 0 && e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                        ? Math.Abs(d.GetDouble() - target) : 0,
                })
                // A big duration mismatch usually means a different version (live, remix…); skip those.
                .Where(c => (c.Synced || c.Plain) && (target <= 0 || c.Diff <= 8))
                .OrderByDescending(c => c.Synced)
                .ThenBy(c => c.Diff)
                .ToList();

            return candidates.Count == 0 ? null : FromJson(candidates[0].Element);
        }

        private static Lyrics FromJson(JsonElement e)
        {
            bool instrumental = e.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;
            if (HasString(e, "syncedLyrics"))
            {
                var parsed = ParseLrc(e.GetProperty("syncedLyrics").GetString()!);
                if (parsed.Count > 0) return new Lyrics { Lines = parsed, IsSynced = true };
            }
            if (HasString(e, "plainLyrics"))
            {
                var lines = e.GetProperty("plainLyrics").GetString()!
                    .Replace("\r", "").Split('\n')
                    .Select(l => new LyricLine(TimeSpan.Zero, l.Trim()))
                    .ToList();
                return new Lyrics { Lines = lines, IsSynced = false };
            }
            return new Lyrics { IsInstrumental = instrumental };
        }

        private static bool HasString(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString());

        // ----- LRC parsing: "[01:23.45]Some line", possibly several timestamps per line -----

        private static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
        private static readonly Regex OffsetTag = new(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static List<LyricLine> ParseLrc(string lrc)
        {
            var result = new List<LyricLine>();
            int offsetMs = 0;

            foreach (var raw in lrc.Replace("\r", "").Split('\n'))
            {
                var line = raw.Trim();
                var off = OffsetTag.Match(line);
                if (off.Success) { int.TryParse(off.Groups[1].Value, out offsetMs); continue; }

                var tags = new List<TimeSpan>();
                int pos = 0;
                Match m;
                while ((m = TimeTag.Match(line, pos)).Success && m.Index == pos)
                {
                    int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    int ms = 0;
                    if (m.Groups[3].Success)
                    {
                        string frac = m.Groups[3].Value;
                        ms = frac.Length switch
                        {
                            1 => int.Parse(frac) * 100,
                            2 => int.Parse(frac) * 10,
                            _ => int.Parse(frac[..3]),
                        };
                    }
                    tags.Add(new TimeSpan(0, 0, min, sec, ms));
                    pos = m.Index + m.Length;
                }
                if (tags.Count == 0) continue;

                string text = line[pos..].Trim();
                foreach (var t in tags)
                    result.Add(new LyricLine(t - TimeSpan.FromMilliseconds(offsetMs), text));
            }

            result.Sort((a, b) => a.Time.CompareTo(b.Time));

            // Drop empty lines at the very start; keep the rest (they mark instrumental gaps).
            while (result.Count > 0 && string.IsNullOrWhiteSpace(result[0].Text)) result.RemoveAt(0);
            return result;
        }

        // ----- Title clean-up for better matching -----

        private static readonly Regex Suffix = new(
            @"\s*[-–—]\s*(\d{4}\s*)?(remaster(ed)?|re-?master(ed)?|mono|stereo|single version|radio edit|album version|bonus track|deluxe|live|edit|version|mix)\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Brackets = new(
            @"\s*[\(\[](feat\.?|ft\.?|with|remaster|remastered|\d{4} remaster|bonus|deluxe|explicit|clean)[^\)\]]*[\)\]]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static string CleanTitle(string title)
        {
            var t = Suffix.Replace(title, "");
            t = Brackets.Replace(t, "");
            return string.IsNullOrWhiteSpace(t) ? title : t.Trim();
        }

        internal static string FirstArtist(string artist)
        {
            var parts = artist.Split(new[] { ", ", " & ", " feat. ", " ft. ", "; " }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0].Trim() : artist;
        }

        // ----- Disk cache -----

        private sealed class CacheEntry
        {
            public bool Synced { get; set; }
            public bool Instrumental { get; set; }
            public List<double> Times { get; set; } = new();
            public List<string> Texts { get; set; } = new();
            public DateTime Saved { get; set; }
        }

        private static Lyrics? ReadCache(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                var e = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(file));
                if (e == null) return null;
                // Retry songs that had no lyrics after a few days, in case someone has added them.
                if (e.Texts.Count == 0 && !e.Instrumental && DateTime.UtcNow - e.Saved > TimeSpan.FromDays(3)) return null;
                var lines = e.Times.Zip(e.Texts, (t, s) => new LyricLine(TimeSpan.FromMilliseconds(t), s)).ToList();
                return new Lyrics { Lines = lines, IsSynced = e.Synced, IsInstrumental = e.Instrumental };
            }
            catch { return null; }
        }

        private static void WriteCache(string file, Lyrics l)
        {
            try
            {
                Directory.CreateDirectory(CacheFolder);
                var e = new CacheEntry
                {
                    Synced = l.IsSynced,
                    Instrumental = l.IsInstrumental,
                    Times = l.Lines.Select(x => x.Time.TotalMilliseconds).ToList(),
                    Texts = l.Lines.Select(x => x.Text).ToList(),
                    Saved = DateTime.UtcNow,
                };
                File.WriteAllText(file, JsonSerializer.Serialize(e));
            }
            catch { }
        }

        public static void ClearCache()
        {
            try { if (Directory.Exists(CacheFolder)) Directory.Delete(CacheFolder, recursive: true); } catch { }
            try
            {
                var old = Path.Combine(Settings.AppDataFolder, "cache");
                if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
            }
            catch { }
        }

        private static string Hash(string s) =>
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }
}
