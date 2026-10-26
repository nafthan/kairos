using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace Kairos
{
    /// <summary>
    /// Version and project details. The version number itself lives in Kairos.csproj (&lt;Version&gt;),
    /// so there is only one place to change it for a release.
    /// </summary>
    public static class AppInfo
    {
        /// <summary>Set this to your GitHub repository once it exists, e.g. "https://github.com/yourname/kairos".</summary>
        public const string RepoUrl = "https://github.com/nafthan/kairos";

        public static Version Version { get; } =
            typeof(AppInfo).Assembly.GetName().Version ?? new Version(1, 0, 0);

        /// <summary>e.g. "1.2.0"</summary>
        public static string VersionText => $"{Version.Major}.{Version.Minor}.{Math.Max(0, Version.Build)}";

        /// <summary>Identifies Kairos to the lyrics service, as LRCLIB asks apps to do.</summary>
        public static string UserAgent =>
            string.IsNullOrEmpty(RepoUrl) ? $"Kairos/{VersionText}" : $"Kairos/{VersionText} ({RepoUrl})";

        // ----- Version history, read from CHANGELOG.md (built into the app) -----

        public sealed record Release(string Version, string Date, List<(string Heading, List<string> Items)> Sections);

        public static List<Release> LoadChangelog()
        {
            var releases = new List<Release>();
            try
            {
                var info = Application.GetResourceStream(new Uri("pack://application:,,,/CHANGELOG.md"));
                if (info == null) return releases;
                using var reader = new StreamReader(info.Stream);

                Release? current = null;
                List<string>? items = null;
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.StartsWith("## "))
                    {
                        // "## [1.2.0] - 2026-10-25"
                        var head = line[3..].Trim();
                        var parts = head.Split(" - ", 2, StringSplitOptions.TrimEntries);
                        current = new Release(parts[0].Trim('[', ']'), parts.Length > 1 ? parts[1] : "", new());
                        releases.Add(current);
                        items = null;
                    }
                    else if (line.StartsWith("### ") && current != null)
                    {
                        items = new List<string>();
                        current.Sections.Add((line[4..].Trim(), items));
                    }
                    else if (line.StartsWith("- ") && current != null)
                    {
                        if (items == null)
                        {
                            items = new List<string>();
                            current.Sections.Add(("", items));
                        }
                        items.Add(line[2..].Trim());
                    }
                }
            }
            catch
            {
                // Missing or unreadable changelog: show nothing rather than fail.
            }
            return releases;
        }
    }
}
