using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Kairos
{
    /// <summary>
    /// Your own lyrics, kept as plain files you can also edit by hand:
    ///
    ///     Kairos Lyrics\
    ///         Artist Name\
    ///             Song Title.lrc    ← time-synced (lines start with [mm:ss.xx])
    ///             Other Song.txt    ← plain lyrics
    ///
    /// These are checked before the online lyrics service, so they always win.
    /// Matching ignores case, punctuation and things like " - Remastered 2011".
    /// </summary>
    public static class LocalLyrics
    {
        public sealed record Entry(string Artist, string Title, string Path, bool Synced);

        public static string DefaultFolder =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Kairos Lyrics");

        public static string FolderFor(Settings s) =>
            string.IsNullOrWhiteSpace(s.LyricsFolder) ? DefaultFolder : s.LyricsFolder;

        private static readonly Regex TimeTag = new(@"\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\]", RegexOptions.Compiled);
        private static readonly Regex MetaTag = new(@"^\[(ar|ti|al):\s*(.*?)\s*\]\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

        public static bool HasTimestamps(string text) => TimeTag.IsMatch(text);

        // ----- Finding lyrics for a song -----

        public static string? FindFile(string folder, TrackInfo track)
        {
            try
            {
                if (!Directory.Exists(folder)) return null;

                var artistKeys = new[] { track.Artist, LyricsService.FirstArtist(track.Artist) }
                    .Select(Normalize).Where(k => k.Length > 0).Distinct().ToList();
                var titleKeys = new[] { track.Title, LyricsService.CleanTitle(track.Title) }
                    .Select(Normalize).Where(k => k.Length > 0).Distinct().ToList();

                foreach (var dir in Directory.GetDirectories(folder))
                {
                    if (!artistKeys.Contains(Normalize(System.IO.Path.GetFileName(dir)))) continue;

                    string? plain = null;
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                        if (ext != ".lrc" && ext != ".txt") continue;
                        if (!titleKeys.Contains(Normalize(System.IO.Path.GetFileNameWithoutExtension(file)))) continue;
                        if (ext == ".lrc") return file; // synced wins
                        plain ??= file;
                    }
                    if (plain != null) return plain;
                }
            }
            catch
            {
                // Folder unreadable; fall through to the online service.
            }
            return null;
        }

        public static Lyrics? Load(string file)
        {
            try
            {
                var text = File.ReadAllText(file);
                if (HasTimestamps(text))
                {
                    var lines = LyricsService.ParseLrc(text);
                    if (lines.Count > 0) return new Lyrics { Lines = lines, IsSynced = true, Source = LyricsSource.Local };
                }

                var plain = text.Replace("\r", "").Split('\n')
                    .Where(l => !MetaTag.IsMatch(l))
                    .Select(l => new LyricLine(TimeSpan.Zero, l.Trim()))
                    .ToList();
                while (plain.Count > 0 && plain[0].Text.Length == 0) plain.RemoveAt(0);
                while (plain.Count > 0 && plain[^1].Text.Length == 0) plain.RemoveAt(plain.Count - 1);
                return plain.Count == 0 ? null : new Lyrics { Lines = plain, IsSynced = false, Source = LyricsSource.Local };
            }
            catch
            {
                return null;
            }
        }

        // ----- Saving, listing, deleting -----

        /// <summary>Saves lyrics as Artist\Title.lrc (synced) or .txt (plain). Returns the file path.</summary>
        public static string Save(string folder, string artist, string title, string text)
        {
            var dir = System.IO.Path.Combine(folder, SafeName(artist));
            Directory.CreateDirectory(dir);

            bool synced = HasTimestamps(text);
            var name = SafeName(title);
            var path = System.IO.Path.Combine(dir, name + (synced ? ".lrc" : ".txt"));
            var other = System.IO.Path.Combine(dir, name + (synced ? ".txt" : ".lrc"));

            File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
            if (File.Exists(other)) File.Delete(other); // don't leave a stale copy in the other format
            return path;
        }

        public static List<Entry> List(string folder)
        {
            var result = new List<Entry>();
            try
            {
                if (!Directory.Exists(folder)) return result;
                foreach (var dir in Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var file in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                        if (ext != ".lrc" && ext != ".txt") continue;
                        result.Add(new Entry(System.IO.Path.GetFileName(dir), System.IO.Path.GetFileNameWithoutExtension(file), file, ext == ".lrc"));
                    }
                }
            }
            catch { }
            return result;
        }

        public static void Delete(string file)
        {
            try
            {
                File.Delete(file);
                var dir = System.IO.Path.GetDirectoryName(file);
                if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { }
        }

        // ----- Importing files -----

        public sealed record ImportGuess(string? Artist, string? Title, string Text);

        /// <summary>
        /// Reads a lyrics file and works out the artist and title from its [ar:] / [ti:] tags,
        /// or from a file name like "Artist - Title.lrc".
        /// </summary>
        public static ImportGuess ReadForImport(string file)
        {
            var text = File.ReadAllText(file);
            string? artist = null, title = null;

            foreach (Match m in MetaTag.Matches(text))
            {
                var key = m.Groups[1].Value.ToLowerInvariant();
                var value = m.Groups[2].Value.Trim();
                if (value.Length == 0) continue;
                if (key == "ar") artist ??= value;
                if (key == "ti") title ??= value;
            }

            if (artist == null || title == null)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                var parts = name.Split(" - ", 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    artist ??= parts[0];
                    title ??= parts[1];
                }
                else
                {
                    title ??= name;
                }
            }
            return new ImportGuess(artist, title, text);
        }

        // ----- Helpers -----

        /// <summary>Lower-case letters and digits only, so "Don't Stop (Remastered)" matches "dont stop remastered".</summary>
        public static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s.Normalize(NormalizationForm.FormKD))
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        /// <summary>Makes a name safe to use as a Windows file or folder name.</summary>
        public static string SafeName(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
            var sb = new StringBuilder();
            foreach (var ch in name.Trim())
                sb.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
            var result = sb.ToString().Trim().TrimEnd('.', ' ');
            if (result.Length > 120) result = result[..120].TrimEnd();
            return result.Length == 0 ? "Untitled" : result;
        }
    }
}
