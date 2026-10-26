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
    ///     %APPDATA%\Kairos\Lyrics\
    ///         Artist Name\
    ///             Song Title.lrc    ← time-synced (lines start with [mm:ss.xx])
    ///             Other Song.txt    ← plain lyrics
    ///
    /// These are checked before the online lyrics service, so they always win.
    /// Matching ignores case, punctuation and things like " - Remastered 2011".
    /// </summary>
    public static class LocalLyrics
    {
        public sealed record Entry(string Artist, string Title, string Path, bool Synced, bool NoLyrics = false);

        /// <summary>A song marked "this song has no lyrics" is an empty file with this extension.</summary>
        public const string NoLyricsExtension = ".nolyrics";

        /// <summary>Inside Kairos's own folder, next to its settings: %APPDATA%\Kairos\Lyrics.</summary>
        public static string DefaultFolder => System.IO.Path.Combine(Settings.AppDataFolder, "Lyrics");

        /// <summary>Where lyrics were kept before 1.9.</summary>
        private static string OldDefaultFolder =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Kairos Lyrics");

        /// <summary>
        /// One-time move from Documents\Kairos Lyrics (before 1.9) into Kairos's own folder.
        /// Only when the default folder is in use; a folder you chose yourself is left where it is.
        /// </summary>
        public static void MoveFromOldDefault(Settings s)
        {
            try
            {
                if (string.Equals(s.LyricsFolder, OldDefaultFolder, StringComparison.OrdinalIgnoreCase)) s.LyricsFolder = "";
                if (!string.IsNullOrEmpty(s.LyricsFolder)) return;
                if (!Directory.Exists(OldDefaultFolder)) return;

                Directory.CreateDirectory(DefaultFolder);
                foreach (var file in Directory.GetFiles(OldDefaultFolder, "*", SearchOption.AllDirectories))
                {
                    var relative = System.IO.Path.GetRelativePath(OldDefaultFolder, file);
                    var target = System.IO.Path.Combine(DefaultFolder, relative);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    if (!File.Exists(target)) File.Move(file, target);   // never overwrite anything already there
                }
                // Remove the old folder if everything moved out of it.
                if (!Directory.EnumerateFiles(OldDefaultFolder, "*", SearchOption.AllDirectories).Any())
                    Directory.Delete(OldDefaultFolder, recursive: true);
            }
            catch
            {
                // If it can't be moved (file in use, permissions), the old lyrics stay where they were.
            }
        }

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

                    string? plain = null, none = null;
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                        if (ext != ".lrc" && ext != ".txt" && ext != NoLyricsExtension) continue;
                        if (!titleKeys.Contains(Normalize(System.IO.Path.GetFileNameWithoutExtension(file)))) continue;
                        if (ext == ".lrc") return file; // synced wins
                        if (ext == ".txt") plain ??= file;
                        else none ??= file;
                    }
                    if (plain != null) return plain;
                    if (none != null) return none;
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
                if (file.EndsWith(NoLyricsExtension, StringComparison.OrdinalIgnoreCase))
                    return new Lyrics { IsInstrumental = true, Source = LyricsSource.Local };

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
            var marker = System.IO.Path.Combine(dir, name + NoLyricsExtension);
            if (File.Exists(marker)) File.Delete(marker); // it has lyrics now
            return path;
        }

        /// <summary>Remembers that a song has no lyrics, so Kairos stops looking and shows the filler instead.</summary>
        public static string MarkNoLyrics(string folder, string artist, string title)
        {
            var dir = System.IO.Path.Combine(folder, SafeName(artist));
            Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, SafeName(title) + NoLyricsExtension);
            File.WriteAllText(path, "Marked in Kairos as having no lyrics. Delete this file to undo.\r\n");
            return path;
        }

        public static void UnmarkNoLyrics(string file) => Delete(file);

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
                        if (ext != ".lrc" && ext != ".txt" && ext != NoLyricsExtension) continue;
                        result.Add(new Entry(System.IO.Path.GetFileName(dir), System.IO.Path.GetFileNameWithoutExtension(file), file,
                                             ext == ".lrc", ext == NoLyricsExtension));
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

        // ----- Syncing -----

        /// <summary>
        /// The lines to time, from pasted text: time tags and [ar:]-style tags removed, blank lines dropped
        /// (they're just gaps between verses, and there's nothing to tap for them).
        /// </summary>
        public static List<string> LinesForSync(string text)
        {
            var result = new List<string>();
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                if (MetaTag.IsMatch(raw.Trim())) continue;
                var line = TimeTag.Replace(raw, "").Trim();
                if (line.Length > 0) result.Add(line);
            }
            return result;
        }

        /// <summary>Builds .lrc text: [ar:]/[ti:] tags, then "[mm:ss.xx]line" for each line.</summary>
        public static string BuildLrc(string artist, string title, IEnumerable<(TimeSpan Time, string Text)> lines)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(artist)) sb.Append("[ar:").Append(artist.Trim()).Append("]\n");
            if (!string.IsNullOrWhiteSpace(title)) sb.Append("[ti:").Append(title.Trim()).Append("]\n");
            sb.Append("[by:Kairos]\n");
            foreach (var (time, text) in lines)
                sb.Append('[').Append(FormatTag(time)).Append(']').Append(text).Append('\n');
            return sb.ToString();
        }

        /// <summary>"mm:ss.xx", the usual .lrc time format (hundredths of a second).</summary>
        public static string FormatTag(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            long cs = (long)Math.Round(t.TotalMilliseconds / 10.0);
            return $"{cs / 6000:00}:{cs / 100 % 60:00}.{cs % 100:00}";
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
