# Changelog

All notable changes to Kairos are listed here. Versions follow [Semantic Versioning](https://semver.org).

## \[1.15.0] - 2026-10-26

### Added

* Click the album cover to open the album in Spotify (without the Spotify connection, it searches Spotify for it). Click your profile picture to open your Spotify profile, or a link of your choosing. Both can be switched off in settings → Layout.
* Lyric cards: your profile picture in the corner, and up to 8 lines.
* Lyric cards can play the song through from your first chosen line, like the lyrics window: the lines scroll up and each one lights up and fills in as it's sung. Choose the clip length (5–30 seconds; always long enough for the lines you chose). In the preview and the GIF (up to 20 seconds).
* Lyric card videos (Save MP4… / Copy MP4): the song played through at 30 frames a second, in 720p or 1080p, up to 30 seconds, with the song's sound. Spotify plays the lines while Kairos records them: Spotify alone on Windows 10 version 2004 and later, otherwise everything you can hear. Uses Windows' own video encoder; no extra software.
* A Cancel button while a GIF or video is being made.
* Lyric cards show where they're saved, with Open folder and Change… to pick a different folder (pictures, GIFs and videos all go there; Pictures\\Kairos to start with).

### Changed

* Long GIFs are made frame by frame, so they no longer use a lot of memory.

## \[1.14.0] - 2026-10-23

### Added

* Layout editor (settings → Layout): a big live preview where you drag the lyrics, the song title and artist, the album cover and your profile picture anywhere in the window, and drag their edges and corners to resize them. Edges and centres snap to the window and to each other with guide lines (hold Alt to place freely); arrow keys nudge, Ctrl + arrows resize. Layouts stay in proportion when the window is resized.
* Ready-made layouts to start from: Classic, Cover left, Cover right, Cover on top and Title below.
* Window size from settings, with quick sizes (Compact, Tall, Wide, Large) and width and height sliders, so you can design for the shape you use.
* A panel behind the lyrics, with its own colour, opacity, border (colour, opacity, width), corner roundness and inner spacing.
* Title size, title alignment (custom layouts) and the option to let the cover fill its box instead of staying square.
* Profile picture: your Spotify account's picture or one of your own, shown as a circle. In the standard layout it sits on the right of the title row (the cover is on the left); in a custom layout, anywhere you like.
* Saved themes now include the layout.

## \[1.13.0] - 2026-10-20

### Added

* Click-through: the lyrics window (and mini strip) lets clicks go to whatever is underneath, so it never gets in the way. Hold Ctrl over it to drag it, use its buttons or right-click. Switch it in settings → Window, the right-click menu, the tray icon, or Ctrl+Alt+T from any app.
* Hide for full-screen apps: Kairos fades out while a game, video or presentation fills the screen it's on, and comes back afterwards. A full-screen app on another screen leaves it showing. On by default (settings → Window).
* Tray icon by the clock: click it to hide or show Kairos; right-click for play/pause, next, previous, mini mode, click-through, Spotify's window, lyric card, settings and exit. Hover over it to see the song.
* Lyric cards (right-click → Lyric card…): pick one to four lines, a shape (square, tall or wide) and a background (yours, the album art or a colour), with the song and the Kairos logo underneath. Copy it as a picture, save it as a PNG, or save or copy it as an animated GIF with the karaoke fill, glow, colour dance, album art drift and your GIF background moving on a loop.

## \[1.12.1] - 2026-10-14

### Changed

* New logo: the right moment rising over a lyric line.

### Fixed

* The logo in settings was blurry; it's now drawn sharply at any size and display scaling.

## \[1.12.0] - 2026-10-14

### Added

* Spotify's window can be hidden (settings → Window → Together with Spotify → Spotify's window: Hidden). Spotify plays in the background with no window or taskbar button, so only Kairos is in the taskbar; Kairos opens Spotify when it starts and closes it when it closes. Right-click → Show Spotify's window when you need it; minimising it hides it again.
* Colour dance opacity, and a choice of behind your background picture or GIF (it shows through see-through parts or a lowered opacity) or in front of it.

### Changed

* The colour dance is now its own layer that works with any background (colour, picture/GIF or album art), instead of replacing the background. Its settings are on the Effects and Background pages. Settings and themes from 1.10–1.11 carry over.
* "Keep Spotify minimised" is now the Spotify window choice: Normal, Minimised or Hidden.

## \[1.11.0] - 2026-10-04

### Added

* Statistics (settings → Statistics, or right-click → Statistics): listening time today, this week and all time, songs played, your day streak, when in the day you listen, your top songs and artists, and how many of your songs have synced lyrics, with a shortcut to add lyrics for your most-played songs that are missing them. Kept only on your PC; can be switched off or reset.
* Colour dance is now also a switch on the Effects page, and the right-click menu has a Background submenu.

### Fixed

* The colour dance could stay hidden after the window reloaded, and its colours could stop short of the album's real colours.

## \[1.10.0] - 2026-10-03

### Added

* Mini mode: a slim strip, like subtitles, showing just the line being sung and (optionally) the next one, with your fonts, colours, glow and karaoke fill. Switch with the new mini button, Ctrl+Alt+M (works in any app) or the right-click menu; double-click the strip to go back. It can stretch across the screen, and remembers where you put it.
* Colour dance background: glowing colours taken from the album art drift around behind the lyrics, swelling and speeding up with the beat, and blend into the next song's colours. Adjust speed, how much it moves with the music, size and darkening. Works in themes and the live preview.

## \[1.9.0] - 2026-09-22

### Added

* Live preview in settings: a miniature of your lyrics window at its real shape, playing a sample song with your fonts, colours, background, title row and album art. Karaoke fill, glow, blur, pop and beat pulse all animate. Click it to pause.
* Eyedropper: pick a colour from anywhere on screen, with a magnifying loupe, next to every colour setting and inside the colour picker.
* Recently used colours in the colour picker.
* Zoom out on image and GIF backgrounds (down to 25%), with a choice of your desktop or the window colour around the picture.

### Changed

* New logo.
* Your own lyrics now live inside Kairos's folder (%APPDATA%\\Kairos\\Lyrics). Lyrics in the old Documents\\Kairos Lyrics folder are moved across automatically.

## \[1.8.0] - 2026-09-16

### Added

* Playlists without lyrics: mark a playlist (for example an instrumental one) and its songs skip the lyrics search and show your no-lyrics visual. Mark it from the right-click menu while it plays, or with "Mark as no lyrics" in the playlist browser; manage them in settings under Behaviour. Needs the Spotify connection.
* Search Spotify: the magnifier button (or Ctrl+F) finds songs and playlists. Play, add to queue, save to Liked Songs or add to a playlist, and play or save playlists.
* If Windows can't find Spotify's sound, the volume control uses Spotify's own volume through your Spotify connection, and shows what sound Windows does report.
* Effects page in settings: glow colour, size and strength, plus karaoke fill (the current line fills in as it's sung), blur on the other lines, a bouncy pop for each new line, and a beat pulse that moves with Spotify's sound.
* Animated GIFs as the background, with the same brightness, contrast, blur and tint controls as other images.
* The settings window can be resized (from the right edge, bottom edge or corner) and remembers its size.

### Changed

* The settings window no longer stays on top like the lyrics window, and has its own taskbar button.
* Glow settings moved from the Text page to the new Effects page; saved themes keep their glow colour.

### Fixed

* The buttons in the Sync lyrics window showed boxes instead of words.
* The volume control could miss Spotify: it now looks on every output device and recognises Spotify more reliably, including the Microsoft Store version.
* Long right-click menus now scroll instead of running off the screen.

## \[1.7.0] - 2026-09-12

### Added

* Together with Spotify (settings → Window): open Spotify when Kairos opens (kept minimised), pause or close Spotify when you close Kairos, and hide or close Kairos when Spotify closes. With "Hide Kairos", Kairos waits in the background and appears whenever Spotify opens.
* Ctrl+Alt+C centres the lyrics window left-to-right on its screen, from any app. Also in the right-click menu.
* Option to hide the "No lyrics for this song" caption, leaving just the visual.
* Opening Kairos again while it's waiting in the background brings it back.

### Changed

* The volume control now adjusts Spotify only, with mute and a live level meter.
* The right-click menu, its submenus and tooltips now match the dark settings look, with a green tick for options that are on.

## \[1.6.0] - 2026-09-10

### Added

* "This song has no lyrics" button next to "Add lyrics". Kairos remembers it and stops searching; right-click to undo.
* Something to look at when a song has no lyrics: a pulse that moves with the music, the album art, a clock, the song's progress, or quotes about time. Choose in settings under Behaviour.
* Volume mixer: Spotify, all sound, and every other app playing, with mute buttons and live level meters. Open it from the speaker button or the right-click menu. Ctrl + scroll on the lyrics window changes Spotify's volume.
* Add a shared playlist by pasting its link, for collaborative playlists Spotify doesn't list.

### Fixed

* Playlists shared with you through Spotify's invite feature now show up and can be added to.

## \[1.5.0] - 2026-09-06

### Added

* Sync timings: turn plain lyrics into synced ones by tapping Space as each line starts while the song plays. Undo a tap with Backspace, click any line to re-time from there, shift everything earlier or later, and adjust for reaction time. Already-synced lyrics can be fine-tuned the same way.
* Search online in My lyrics: find lyrics by artist and song, see which results are synced, and load one with a click.
* "Sync timings for this song" in the right-click menu, which saves straight to your lyrics folder.
* If Windows' media controls can't jump to a point in the song, Kairos uses your Spotify connection instead (when connected).

## \[1.4.0] - 2026-09-05

### Added

* Liked Songs: save the playing song to Liked Songs from the + button, and browse and play them in the playlist browser. If you connected Spotify with an earlier version, reconnect once to allow it.
* Playlist covers in the + picker.
* Themes now include the background: a colour, your own image with all its adjustments, or the album art. Save several looks and switch between them in one click.

### Fixed

* The + picker showed every letter as a box.

## \[1.3.0] - 2026-08-28

### Added

* Spotify connection (optional): add the song that's playing to a playlist straight from the lyrics window, and browse and play your playlists. Needs Spotify Premium and a one-time developer app setup, guided step by step in settings.
* Background images: choose or drop in any picture, then adjust brightness, contrast, saturation, blur, opacity, tint, fit, zoom, position and mirroring. The editor and its preview appear once an image is chosen.
* A Background page in settings to choose between a colour, your own image or the album art.

### Changed

* The album art background moved to the Background page; your setting carries over.
* "Show album art beside the title" moved to the Window page.

### Fixed

* The song title now lines up with the centred lyrics when album art is shown beside it.

## \[1.2.0] - 2026-08-21

### Added

* Your own lyrics: a folder with one subfolder per artist and one file per song, checked before the online library.
* My lyrics page in settings to add, paste, import (one file or many at once), edit and delete lyrics.
* "Add lyrics for this song" button when no lyrics are found.
* Album art beside the song title, and an optional blurred album art background with a slow drift.
* Save your own themes from the Colours page in settings; they also appear in the right-click Theme menu.
* Lock size option, so the window can't be resized by accident.
* Version history in settings (click the version number in the sidebar).
* Automatic Windows builds on GitHub, with Kairos.exe attached to each release.

## \[1.1.0] - 2026-08-19

### Added

* Glow on current line option.
* Show timing info option, for checking sync.
* A note under the song title when lyrics aren't time-synced.

### Changed

* Redesigned settings: a dark panel with sections, a live preview, switches, a built-in colour picker and theme previews.
* The current line now glides into place, growing and brightening smoothly.

### Fixed

* Lines not highlighting or following the song in some cases, thanks to more reliable playback tracking.
* Unsynced lyrics sometimes chosen when time-synced lyrics were available.

## \[1.0.0] - 2026-08-18

### Added

* First release: a small, always-on-top window showing time-synced Spotify lyrics, with the current line centred.
* Drag to move, resize from the corner, hover controls, right-click menu, click a line to seek, scroll to browse.
* Fully customisable look and behaviour.

