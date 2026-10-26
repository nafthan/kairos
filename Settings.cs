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
        /// <summary>A soft glow around the current line.</summary>
        public bool CurrentLineGlow { get; set; } = false;
        /// <summary>Glow in the current line's own colour (otherwise <see cref="GlowColor"/>).</summary>
        public bool GlowMatchLine { get; set; } = true;
        public string GlowColor { get; set; } = "#1ED760";
        public double GlowSize { get; set; } = 16;        // 4 … 40
        public double GlowStrength { get; set; } = 0.9;   // 0.1 … 1

        // ----- Effects -----
        /// <summary>The current line fills in from left to right as it's sung.</summary>
        public bool KaraokeFill { get; set; } = false;
        /// <summary>Lines other than the current one are softly blurred.</summary>
        public bool FocusBlur { get; set; } = false;
        public double FocusBlurAmount { get; set; } = 2.5; // 0.5 … 8
        /// <summary>Each new line pops in with a little bounce.</summary>
        public bool PopEffect { get; set; } = false;
        /// <summary>The current line gently pulses with Spotify's sound.</summary>
        public bool BeatPulse { get; set; } = false;
        public double BeatPulseStrength { get; set; } = 1.0; // 0.3 … 2

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
        /// <summary>The lyrics window ignores the mouse, so you can click what's underneath; hold Ctrl to use it.</summary>
        public bool ClickThrough { get; set; } = false;
        /// <summary>Fade out while a full-screen app (game, video, presentation) is on the same screen as Kairos.</summary>
        public bool HideForFullScreen { get; set; } = true;
        /// <summary>An icon in the notification area (system tray) with quick controls.</summary>
        public bool ShowTrayIcon { get; set; } = true;

        // ----- Lyric card -----
        /// <summary>"Square", "Story" (tall) or "Wide".</summary>
        public string CardShape { get; set; } = "Square";
        /// <summary>"Mine" (your Kairos background), "AlbumArt" or "Colour".</summary>
        public string CardBackground { get; set; } = "Mine";
        public bool CardShowSong { get; set; } = true;
        public bool CardShowMark { get; set; } = true;
        public bool CardKaraoke { get; set; } = true;
        public bool CardShowProfile { get; set; } = false;
        /// <summary>Animate by playing the chosen lines with their real timings (instead of a short loop).</summary>
        public bool CardPlayThrough { get; set; } = false;
        /// <summary>Video size: 720 or 1080 (the short side).</summary>
        public int CardVideoSize { get; set; } = 720;
        /// <summary>Record the song's audio into the video (Spotify plays the lines while it records).</summary>
        public bool CardVideoAudio { get; set; } = true;
        /// <summary>How long a played-through GIF or video runs (seconds), from the first chosen line.</summary>
        public double CardClipSeconds { get; set; } = 15;
        /// <summary>Where lyric cards are saved (empty = Pictures\Kairos).</summary>
        public string CardFolder { get; set; } = "";
        public double CardSeconds { get; set; } = 4;     // GIF length
        /// <summary>GIF width in pixels (PNG is always full size).</summary>
        public int CardGifSize { get; set; } = 480;

        // ----- Behaviour -----
        public bool SmoothScrolling { get; set; } = true;
        public bool ClickToSeek { get; set; } = true;
        /// <summary>After scrolling away, return to the current line automatically after this many seconds (0 = never).</summary>
        public int AutoResumeSeconds { get; set; } = 6;
        /// <summary>Positive = lyrics appear earlier, negative = later.</summary>
        public int SyncOffsetMs { get; set; } = 0;
        /// <summary>Shows playback position details, useful when lyrics seem out of sync.</summary>
        public bool ShowTimingInfo { get; set; } = false;

        // ----- Together with the Spotify app -----
        /// <summary>Open Spotify when Kairos starts (if it isn't already open).</summary>
        public bool OpenSpotifyOnStart { get; set; } = false;
        /// <summary>Keep Spotify minimised when Kairos opens it, so only the lyrics window is on screen.</summary>
        public bool MinimiseSpotifyOnStart { get; set; } = true;
        /// <summary>
        /// Spotify's own window: "Normal"; "Minimised" (when Kairos opens Spotify); or "Hidden" (no window or taskbar
        /// button at all while Spotify plays in the background, so only Kairos is in the taskbar).
        /// </summary>
        public string SpotifyWindowMode { get; set; } = "Minimised";

        /// <summary>When Kairos closes: "Nothing", "Pause" or "Close" (Spotify).</summary>
        public string OnKairosClose { get; set; } = "Nothing";
        /// <summary>When Spotify closes: "Keep" (Kairos open), "Hide" (until Spotify opens again) or "Close" (Kairos).</summary>
        public string OnSpotifyClose { get; set; } = "Keep";
        /// <summary>Ctrl+Alt+C centres the window horizontally from any app.</summary>
        public bool GlobalCentreShortcut { get; set; } = true;

        // ----- Songs without lyrics -----
        /// <summary>What fills the window when a song has no lyrics: "Pulse", "AlbumArt", "Clock", "Progress", "Quotes" or "Message".</summary>
        public string NoLyricsFiller { get; set; } = "Pulse";
        /// <summary>Also show the filler when lyrics simply couldn't be found (not just for songs marked as having none).</summary>
        public bool FillerWhenNotFound { get; set; } = false;
        /// <summary>Playlists whose songs have no lyrics (id → name), e.g. an instrumental playlist. Needs the Spotify connection.</summary>
        public Dictionary<string, string> NoLyricsPlaylists { get; set; } = new();

        /// <summary>Show the "No lyrics for this song" / "Instrumental" caption under the filler.</summary>
        public bool ShowFillerCaption { get; set; } = true;

        // ----- Your own lyrics folder (empty = %APPDATA%\Kairos\Lyrics) -----
        public string LyricsFolder { get; set; } = "";

        // ----- Window background -----
        /// <summary>"Colour", "Image" or "AlbumArt". (Before 1.12 the colour dance was a fourth option, "Dance".)</summary>
        public string BackgroundMode { get; set; } = "Colour";

        // Background image (a copy is kept in the app's folder, so moving the original doesn't break it)
        public string BackgroundImagePath { get; set; } = "";
        /// <summary>"Fill", "Fit", "Stretch" or "Tile".</summary>
        public string ImageFit { get; set; } = "Fill";
        public double ImageZoom { get; set; } = 1.0;        // 0.25 … 3 (below 1 = smaller than the window)
        /// <summary>What shows around a picture that doesn't fill the window: "Clear" (your desktop) or "Colour" (the window colour).</summary>
        public string ImageSurround { get; set; } = "Clear";
        public double ImageOffsetX { get; set; } = 0;       // -100 … 100 (% of half the window)
        public double ImageOffsetY { get; set; } = 0;
        public bool ImageFlip { get; set; } = false;
        public double ImageBrightness { get; set; } = 0;    // -100 … 100
        public double ImageContrast { get; set; } = 0;      // -100 … 100
        public double ImageSaturation { get; set; } = 100;  // 0 … 200 %
        public double ImageBlur { get; set; } = 0;          // 0 … 60
        public double ImageOpacity { get; set; } = 1.0;     // 0 … 1 (lower = see-through)
        public string ImageTintColor { get; set; } = "#000000";
        public double ImageTintStrength { get; set; } = 0.25; // 0 … 1

        public void ResetImageAdjustments()
        {
            var d = new Settings();
            ImageFit = d.ImageFit; ImageZoom = d.ImageZoom; ImageSurround = d.ImageSurround; ImageOffsetX = d.ImageOffsetX; ImageOffsetY = d.ImageOffsetY;
            ImageFlip = d.ImageFlip; ImageBrightness = d.ImageBrightness; ImageContrast = d.ImageContrast;
            ImageSaturation = d.ImageSaturation; ImageBlur = d.ImageBlur; ImageOpacity = d.ImageOpacity;
            ImageTintColor = d.ImageTintColor; ImageTintStrength = d.ImageTintStrength;
        }

        public static string BackgroundsFolder => Path.Combine(AppDataFolder, "backgrounds");

        // ----- Colour dance (its own layer, with any background) -----
        public bool DanceOn { get; set; } = false;
        /// <summary>"Behind" your background picture, or "Front" (over it).</summary>
        public string DanceLayer { get; set; } = "Behind";
        public double DanceOpacity { get; set; } = 1.0; // 0.05 … 1
        public double DanceSpeed { get; set; } = 1.0;   // 0.2 … 2.5
        public double DanceEnergy { get; set; } = 1.0;  // 0 … 2: how much the music moves it
        public double DanceSize { get; set; } = 1.0;    // 0.5 … 1.6
        public double DanceDim { get; set; } = 0.3;     // 0 … 0.9: darkens it with the window colour, so lyrics stay readable
        /// <summary>The background to go back to when the colour dance is switched off from the Effects page.</summary>
        public string DancePreviousMode { get; set; } = "Colour";

        /// <summary>1.10–1.11 stored the colour dance as a background type; it's now a layer on top of any background.</summary>
        public void MigrateDance()
        {
            if (BackgroundMode != "Dance") return;
            DanceOn = true;
            BackgroundMode = DancePreviousMode is "Image" or "AlbumArt" ? DancePreviousMode : "Colour";
        }

        // ----- Layout (where the lyrics, title, cover and profile picture go) -----
        public LayoutData Layout { get; set; } = new();

        // ----- Profile picture -----
        /// <summary>"Off", "Spotify" (your Spotify account's picture) or "Custom" (a picture you choose).</summary>
        public string ProfilePicture { get; set; } = "Off";
        /// <summary>Your chosen picture (a copy kept in Kairos's folder).</summary>
        public string ProfilePicturePath { get; set; } = "";
        /// <summary>Clicking the profile picture opens: "Off", "Spotify" (your Spotify profile) or "Custom" (<see cref="ProfileLinkUrl"/>).</summary>
        public string ProfileLink { get; set; } = "Spotify";
        public string ProfileLinkUrl { get; set; } = "";
        /// <summary>Clicking the album cover opens the album in Spotify.</summary>
        public bool AlbumLink { get; set; } = true;

        // ----- Album art -----
        /// <summary>Small cover picture beside the song title.</summary>
        public bool ShowAlbumArt { get; set; } = true;
        /// <summary>Old (1.2.0) switch for the album art background; now BackgroundMode = "AlbumArt".</summary>
        public bool AlbumArtBackground { get; set; } = false;
        public double AlbumArtBlur { get; set; } = 40;
        /// <summary>How much the background colour darkens the artwork (0 = full artwork, 1 = hidden).</summary>
        public double AlbumArtDim { get; set; } = 0.55;
        /// <summary>A slow drift and zoom on the background artwork.</summary>
        public bool AnimateAlbumArt { get; set; } = true;

        // ----- Spotify connection (for playlists) -----
        /// <summary>The Client ID of the user's own Spotify developer app.</summary>
        public string SpotifyClientId { get; set; } = "";
        /// <summary>The sign-in token, encrypted with Windows' per-user data protection (only this Windows account can read it).</summary>
        public string SpotifyToken { get; set; } = "";
        public string SpotifyUserName { get; set; } = "";
        /// <summary>The permissions granted at sign-in (space-separated).</summary>
        public string SpotifyScopes { get; set; } = "";
        /// <summary>Shared playlists added by link (Spotify doesn't list ones you collaborate on but don't follow).</summary>
        public List<string> SpotifyExtraPlaylists { get; set; } = new();
        /// <summary>Remembered answers to "can you add songs to this playlist you don't own?" (playlist id → yes/no).</summary>
        public Dictionary<string, bool> SpotifyCanEdit { get; set; } = new();
        public DateTime SpotifyCanEditChecked { get; set; }

        /// <summary>Things settings "Undo" and "Reset" shouldn't touch: saved themes, the Spotify connection, the lyrics folder.</summary>
        public void KeepPersonalDataFrom(Settings source)
        {
            CustomThemes = source.CustomThemes;
            SpotifyClientId = source.SpotifyClientId;
            SpotifyToken = source.SpotifyToken;
            SpotifyUserName = source.SpotifyUserName;
            SpotifyScopes = source.SpotifyScopes;
            SpotifyExtraPlaylists = source.SpotifyExtraPlaylists;
            SpotifyCanEdit = source.SpotifyCanEdit;
            SpotifyCanEditChecked = source.SpotifyCanEditChecked;
            NoLyricsPlaylists = source.NoLyricsPlaylists;
            RecentColours = source.RecentColours;
            LyricsFolder = source.LyricsFolder;
        }

        // ----- Themes the user has saved -----
        public List<ThemeData> CustomThemes { get; set; } = new();

        /// <summary>Colours you've used recently in the colour picker (newest first).</summary>
        public List<string> RecentColours { get; set; } = new();

        // ----- Remembered size of the settings window -----
        public double SettingsWidth { get; set; } = 800;
        public double SettingsHeight { get; set; } = 620;

        /// <summary>Keep listening statistics (only on this PC).</summary>
        public bool TrackStatistics { get; set; } = true;

        // ----- Mini mode (a one-line strip, like subtitles) -----
        public bool MiniMode { get; set; } = false;
        public bool MiniShowNextLine { get; set; } = true;
        public bool MiniFullWidth { get; set; } = false;
        public double MiniFontSize { get; set; } = 22;
        public double MiniWidth { get; set; } = 640;
        public double MiniLeft { get; set; } = double.NaN;
        public double MiniTop { get; set; } = double.NaN;

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
                    if (loaded != null)
                    {
                        // 1.2.0 → 1.3.0: the album art background switch became a background mode.
                        loaded.MigrateDance();
                        loaded.Layout ??= new LayoutData();
                        loaded.Layout.Tidy();
                        // 1.12: "Keep Spotify minimised" became the Spotify window choice.
                        if (!loaded.MinimiseSpotifyOnStart && loaded.SpotifyWindowMode == "Minimised") loaded.SpotifyWindowMode = "Normal";
                        loaded.MinimiseSpotifyOnStart = true;
                        if (loaded.AlbumArtBackground)
                        {
                            loaded.BackgroundMode = "AlbumArt";
                            loaded.AlbumArtBackground = false;
                        }
                        return loaded;
                    }
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
                LyricsFolder = LyricsFolder,
                SpotifyClientId = SpotifyClientId,
                SpotifyToken = SpotifyToken,
                SpotifyUserName = SpotifyUserName,
                SpotifyScopes = SpotifyScopes,
                SpotifyExtraPlaylists = SpotifyExtraPlaylists,
                SpotifyCanEdit = SpotifyCanEdit,
                SpotifyCanEditChecked = SpotifyCanEditChecked,
                NoLyricsPlaylists = NoLyricsPlaylists,
                BackgroundImagePath = BackgroundImagePath, // keep the chosen picture, just switch back to colour
            };
            CopyFrom(fresh);
        }

        // ----- Themes -----

        public static readonly IReadOnlyList<ThemeData> BuiltInThemes = new[]
        {
            new ThemeData { BackgroundMode = "Colour", Name = "Dark",        BackgroundColor = "#121212", BackgroundOpacity = 0.85, CurrentLineColor = "#FFFFFF", OtherLinesColor = "#FFFFFF", OtherLinesOpacity = 0.45 },
            new ThemeData { BackgroundMode = "Colour", Name = "Spotify",     BackgroundColor = "#0B0B0B", BackgroundOpacity = 0.90, CurrentLineColor = "#1ED760", OtherLinesColor = "#FFFFFF", OtherLinesOpacity = 0.50 },
            new ThemeData { BackgroundMode = "Colour", Name = "Sunset",      BackgroundColor = "#2B1331", BackgroundOpacity = 0.88, CurrentLineColor = "#FFB86B", OtherLinesColor = "#F5D0E8", OtherLinesOpacity = 0.50 },
            new ThemeData { BackgroundMode = "Colour", Name = "Light",       BackgroundColor = "#F4F4F4", BackgroundOpacity = 0.92, CurrentLineColor = "#111111", OtherLinesColor = "#333333", OtherLinesOpacity = 0.45 },
            // 0.01 rather than 0 keeps the window clickable
            new ThemeData { BackgroundMode = "Colour", Name = "Transparent", BackgroundColor = "#000000", BackgroundOpacity = 0.01, CurrentLineColor = "#FFFFFF", OtherLinesColor = "#FFFFFF", OtherLinesOpacity = 0.55, TextShadow = true },
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
            if (t.GlowMatchLine.HasValue) GlowMatchLine = t.GlowMatchLine.Value;
            if (!string.IsNullOrEmpty(t.GlowColor)) GlowColor = t.GlowColor;

            // The background is part of the theme. (Themes saved before 1.4 have none, and mean "colour".)
            var mode = t.BackgroundMode ?? "Colour";
            if (mode == "Image" && t.Image != null && File.Exists(t.Image.Path))
            {
                BackgroundMode = "Image";
                BackgroundImagePath = t.Image.Path;
                ImageFit = t.Image.Fit;
                ImageZoom = t.Image.Zoom;
                ImageSurround = t.Image.Surround ?? "Clear";
                ImageOffsetX = t.Image.OffsetX;
                ImageOffsetY = t.Image.OffsetY;
                ImageFlip = t.Image.Flip;
                ImageBrightness = t.Image.Brightness;
                ImageContrast = t.Image.Contrast;
                ImageSaturation = t.Image.Saturation;
                ImageBlur = t.Image.Blur;
                ImageOpacity = t.Image.Opacity;
                ImageTintColor = t.Image.TintColor;
                ImageTintStrength = t.Image.TintStrength;
            }
            else if (mode == "Dance")
            {
                BackgroundMode = "Colour"; // a theme saved in 1.10–1.11 with the dance as its background
            }
            else if (mode == "AlbumArt")
            {
                BackgroundMode = "AlbumArt";
                if (t.AlbumArtBlur.HasValue) AlbumArtBlur = t.AlbumArtBlur.Value;
                if (t.AlbumArtDim.HasValue) AlbumArtDim = t.AlbumArtDim.Value;
                if (t.AnimateAlbumArt.HasValue) AnimateAlbumArt = t.AnimateAlbumArt.Value;
            }
            else
            {
                BackgroundMode = "Colour"; // also when a theme's image file has gone missing
            }

            // The colour dance, which works with any background.
            if (t.DanceOn.HasValue || mode == "Dance") DanceOn = t.DanceOn ?? true;
            if (t.DanceLayer != null) DanceLayer = t.DanceLayer;
            if (t.DanceOpacity.HasValue) DanceOpacity = t.DanceOpacity.Value;
            if (t.DanceSpeed.HasValue) DanceSpeed = t.DanceSpeed.Value;
            if (t.DanceEnergy.HasValue) DanceEnergy = t.DanceEnergy.Value;
            if (t.DanceSize.HasValue) DanceSize = t.DanceSize.Value;
            if (t.DanceDim.HasValue) DanceDim = t.DanceDim.Value;

            // Themes saved from 1.14 on carry their layout too.
            if (t.Layout != null) { Layout = t.Layout.Copy(); Layout.Tidy(); }
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
                GlowMatchLine = GlowMatchLine,
                GlowColor = GlowColor,
                BackgroundMode = BackgroundMode,
            };
            if (BackgroundMode == "Image" && !string.IsNullOrEmpty(BackgroundImagePath))
            {
                theme.Image = new ThemeImage
                {
                    Path = BackgroundImagePath,
                    Fit = ImageFit, Zoom = ImageZoom, OffsetX = ImageOffsetX, OffsetY = ImageOffsetY, Flip = ImageFlip,
                    Surround = ImageSurround,
                    Brightness = ImageBrightness, Contrast = ImageContrast, Saturation = ImageSaturation,
                    Blur = ImageBlur, Opacity = ImageOpacity, TintColor = ImageTintColor, TintStrength = ImageTintStrength,
                };
            }
            else if (BackgroundMode == "AlbumArt")
            {
                theme.AlbumArtBlur = AlbumArtBlur;
                theme.AlbumArtDim = AlbumArtDim;
                theme.AnimateAlbumArt = AnimateAlbumArt;
            }
            theme.DanceOn = DanceOn;
            if (DanceOn)
            {
                theme.DanceLayer = DanceLayer;
                theme.DanceOpacity = DanceOpacity;
                theme.DanceSpeed = DanceSpeed;
                theme.DanceEnergy = DanceEnergy;
                theme.DanceSize = DanceSize;
                theme.DanceDim = DanceDim;
            }
            theme.Layout = Layout.Copy();
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

    /// <summary>
    /// A named look: colours plus the background (a colour, an image with its adjustments, or the album art).
    /// Built-in themes and the user's saved themes share this shape.
    /// </summary>
    public sealed class ThemeData
    {
        /// <summary>"Colour", "Image" or "AlbumArt". Missing in themes saved before 1.4, which means "Colour".</summary>
        public string? BackgroundMode { get; set; }
        public ThemeImage? Image { get; set; }
        public double? AlbumArtBlur { get; set; }
        public bool? DanceOn { get; set; }
        public string? DanceLayer { get; set; }
        public double? DanceOpacity { get; set; }
        public double? DanceSpeed { get; set; }
        public double? DanceEnergy { get; set; }
        public double? DanceSize { get; set; }
        public double? DanceDim { get; set; }
        public double? AlbumArtDim { get; set; }
        /// <summary>Where things go in the window (themes saved before 1.14 don't have one).</summary>
        public LayoutData? Layout { get; set; }
        public bool? AnimateAlbumArt { get; set; }

        public string Name { get; set; } = "Theme";
        public string BackgroundColor { get; set; } = "#121212";
        public double BackgroundOpacity { get; set; } = 0.85;
        public string CurrentLineColor { get; set; } = "#FFFFFF";
        public string OtherLinesColor { get; set; } = "#FFFFFF";
        public double OtherLinesOpacity { get; set; } = 0.45;
        public bool TextShadow { get; set; }
        public bool CurrentLineGlow { get; set; }
        public bool? GlowMatchLine { get; set; }
        public string? GlowColor { get; set; }
    }

    /// <summary>A theme's background picture and how it's adjusted.</summary>
    public sealed class ThemeImage
    {
        public string Path { get; set; } = "";
        public string Fit { get; set; } = "Fill";
        public double Zoom { get; set; } = 1;
        public string? Surround { get; set; }
        public double OffsetX { get; set; }
        public double OffsetY { get; set; }
        public bool Flip { get; set; }
        public double Brightness { get; set; }
        public double Contrast { get; set; }
        public double Saturation { get; set; } = 100;
        public double Blur { get; set; }
        public double Opacity { get; set; } = 1;
        public string TintColor { get; set; } = "#000000";
        public double TintStrength { get; set; } = 0.25;
    }
}
