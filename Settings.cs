using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace Kairos
{
    /// <summary>
    /// Everything the user can customise. Saved as JSON in %APPDATA%\Kairos\settings.json.
    /// </summary>
    public sealed class Settings
    {
        // ----- Text -----
        public string FontFamily { get; set; } = "Segoe UI";
        public double FontSize { get; set; } = 18;
        /// <summary>How much bigger the current line is than the others (1.0 = same size).</summary>
        public double CurrentLineScale { get; set; } = 1.25;
        public bool CurrentLineBold { get; set; } = true;
        public bool OtherLinesBold { get; set; } = false;
        public string CurrentLineColor { get; set; } = "#FFFFFF";
        public string OtherLinesColor { get; set; } = "#FFFFFF";
        /// <summary>Opacity of lines that aren't the current one (0–1).</summary>
        public double OtherLinesOpacity { get; set; } = 0.45;
        /// <summary>Lines further from the current one get progressively fainter.</summary>
        public bool FadeDistantLines { get; set; } = true;
        /// <summary>Left, Center or Right.</summary>
        public string TextAlignment { get; set; } = "Center";
        public double LineSpacing { get; set; } = 10;
        public bool TextShadow { get; set; } = false;
        /// <summary>A soft glow in the current line's colour.</summary>
        public bool CurrentLineGlow { get; set; } = false;

        // ----- Window -----
        public string BackgroundColor { get; set; } = "#121212";
        public double BackgroundOpacity { get; set; } = 0.85;
        public double CornerRadius { get; set; } = 12;
        public double Padding { get; set; } = 16;
        public bool AlwaysOnTop { get; set; } = true;
        public bool ShowInTaskbar { get; set; } = true;
        public bool ShowTrackInfo { get; set; } = true;
        public bool LockPosition { get; set; } = false;
        /// <summary>Hides the resize handle so the window can't be resized by accident.</summary>
        public bool LockSize { get; set; } = false;
        public bool StartWithWindows { get; set; } = false;

        // ----- Behaviour -----
        public bool SmoothScrolling { get; set; } = true;
        public bool ClickToSeek { get; set; } = true;
        /// <summary>After scrolling away, return to the current line automatically after this many seconds (0 = never).</summary>
        public int AutoResumeSeconds { get; set; } = 6;
        /// <summary>Positive = lyrics appear earlier, negative = later.</summary>
        public int SyncOffsetMs { get; set; } = 0;
        /// <summary>Shows playback position details, useful when lyrics seem out of sync.</summary>
        public bool ShowTimingInfo { get; set; } = false;

        // ----- Your own lyrics folder (empty = Documents\Kairos Lyrics) -----
        public string LyricsFolder { get; set; } = "";

        // ----- Album art -----
        /// <summary>Small cover picture beside the song title.</summary>
        public bool ShowAlbumArt { get; set; } = true;
        /// <summary>Blurred cover filling the window behind the lyrics.</summary>
        public bool AlbumArtBackground { get; set; } = false;
        public double AlbumArtBlur { get; set; } = 40;
        /// <summary>How much the background colour darkens the artwork (0 = full artwork, 1 = hidden).</summary>
        public double AlbumArtDim { get; set; } = 0.55;
        /// <summary>A slow drift and zoom on the background artwork.</summary>
        public bool AnimateAlbumArt { get; set; } = true;

        // ----- Themes the user has saved -----
        public List<ThemeData> CustomThemes { get; set; } = new();

        // ----- Remembered window placement -----
        public double Left { get; set; } = double.NaN;
        public double Top { get; set; } = double.NaN;
        public double Width { get; set; } = 380;
        public double Height { get; set; } = 260;

        // =================================================================

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public static string AppDataFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kairos");

        private static string SettingsPath => Path.Combine(AppDataFolder, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), JsonOptions);
                    if (loaded != null) return loaded;
                }
            }
            catch
            {
                // A corrupt settings file shouldn't stop the app from starting; fall back to defaults.
            }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppDataFolder);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch
            {
                // Ignore (e.g. disk full / folder locked). Settings stay in memory.
            }
        }

        public Settings Clone() =>
            JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

        /// <summary>Copies every value from another Settings object into this one (a deep copy, so lists aren't shared).</summary>
        public void CopyFrom(Settings other)
        {
            var copy = other.Clone();
            foreach (var p in typeof(Settings).GetProperties())
                if (p.CanRead && p.CanWrite) p.SetValue(this, p.GetValue(copy));
        }

        /// <summary>Resets appearance/behaviour to defaults but keeps the window where it is.</summary>
        public void ResetToDefaults()
        {
            var fresh = new Settings
            {
                Left = Left, Top = Top, Width = Width, Height = Height,
                StartWithWindows = StartWithWindows,
                CustomThemes = CustomThemes, // saved themes are kept
            };
            CopyFrom(fresh);
        }

        // ----- Themes -----

        public static readonly IReadOnlyList<ThemeData> BuiltInThemes = new[]
        {
            new ThemeData { Name = "Dark",        BackgroundColor = "#121212", BackgroundOpacity = 0.85, CurrentLineColor = "#FFFFFF", OtherLinesColor = "#FFFFFF", OtherLinesOpacity = 0.45 },
            new ThemeData { Name = "Spotify",     BackgroundColor = "#0B0B0B", BackgroundOpacity = 0.90, CurrentLineColor = "#1ED760", OtherLinesColor = "#FFFFFF", OtherLinesOpacity = 0.50 },
            new ThemeData { Name = "Sunset",      BackgroundColor = "#2B1331", BackgroundOpacity = 0.88, CurrentLineColor = "#FFB86B", OtherLinesColor = "#F5D0E8", OtherLinesOpacity = 0.50 },
            new ThemeData { Name = "Light",       BackgroundColor = "#F4F4F4", BackgroundOpacity = 0.92, CurrentLineColor = "#111111", OtherLinesColor = "#333333", OtherLinesOpacity = 0.45 },
            // 0.01 rather than 0 keeps the window clickable
            new ThemeData { Name = "Transparent", BackgroundColor = "#000000", BackgroundOpacity = 0.01, CurrentLineColor = "#FFFFFF", OtherLinesColor = "#FFFFFF", OtherLinesOpacity = 0.55, TextShadow = true },
        };

        /// <summary>Applies a built-in or saved theme by name.</summary>
        public void ApplyPreset(string name)
        {
            var theme = CustomThemes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? BuiltInThemes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (theme != null) ApplyTheme(theme);
        }

        public void ApplyTheme(ThemeData t)
        {
            BackgroundColor = t.BackgroundColor;
            BackgroundOpacity = t.BackgroundOpacity;
            CurrentLineColor = t.CurrentLineColor;
            OtherLinesColor = t.OtherLinesColor;
            OtherLinesOpacity = t.OtherLinesOpacity;
            TextShadow = t.TextShadow;
            CurrentLineGlow = t.CurrentLineGlow;
        }

        /// <summary>Saves the current look as a theme. A theme with the same name is replaced.</summary>
        public ThemeData SaveCurrentAsTheme(string name)
        {
            var theme = new ThemeData
            {
                Name = name.Trim(),
                BackgroundColor = BackgroundColor,
                BackgroundOpacity = BackgroundOpacity,
                CurrentLineColor = CurrentLineColor,
                OtherLinesColor = OtherLinesColor,
                OtherLinesOpacity = OtherLinesOpacity,
                TextShadow = TextShadow,
                CurrentLineGlow = CurrentLineGlow,
            };
            int existing = CustomThemes.FindIndex(t => string.Equals(t.Name, theme.Name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) CustomThemes[existing] = theme;
            else CustomThemes.Add(theme);
            return theme;
        }

        public void DeleteTheme(string name) =>
            CustomThemes.RemoveAll(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        // ----- Start with Windows (per-user Run key, no admin needed) -----
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "Kairos";

        public static void SetStartWithWindows(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (enabled)
                {
                    var exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe)) key.SetValue(RunValueName, $"\"{exe}\"");
                }
                else
                {
                    key.DeleteValue(RunValueName, throwOnMissingValue: false);
                }
            }
            catch
            {
                // Not fatal; the setting simply won't take effect.
            }
        }
    }

    /// <summary>A named set of colours. Built-in themes and the user's saved themes share this shape.</summary>
    public sealed class ThemeData
    {
        public string Name { get; set; } = "Theme";
        public string BackgroundColor { get; set; } = "#121212";
        public double BackgroundOpacity { get; set; } = 0.85;
        public string CurrentLineColor { get; set; } = "#FFFFFF";
        public string OtherLinesColor { get; set; } = "#FFFFFF";
        public double OtherLinesOpacity { get; set; } = 0.45;
        public bool TextShadow { get; set; }
        public bool CurrentLineGlow { get; set; }
    }
}
