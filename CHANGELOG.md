# Changelog

All notable changes to Kairos are listed here. Versions follow [Semantic Versioning](https://semver.org).

## [1.3.0] - 2026-10-25

### Added
- Spotify connection (optional): add the song that's playing to a playlist straight from the lyrics window, and browse and play your playlists. Needs Spotify Premium and a one-time developer app setup, guided step by step in settings.
- Background images: choose or drop in any picture, then adjust brightness, contrast, saturation, blur, opacity, tint, fit, zoom, position and mirroring. The editor and its preview appear once an image is chosen.
- A Background page in settings to choose between a colour, your own image or the album art.

### Changed
- The album art background moved to the Background page; your setting carries over.
- "Show album art beside the title" moved to the Window page.

### Fixed
- The song title now lines up with the centred lyrics when album art is shown beside it.

## [1.2.0] - 2026-10-25

### Added
- Your own lyrics: a folder with one subfolder per artist and one file per song, checked before the online library.
- My lyrics page in settings to add, paste, import (one file or many at once), edit and delete lyrics.
- "Add lyrics for this song" button when no lyrics are found.
- Album art beside the song title, and an optional blurred album art background with a slow drift.
- Save your own themes from the Colours page in settings; they also appear in the right-click Theme menu.
- Lock size option, so the window can't be resized by accident.
- Version history in settings (click the version number in the sidebar).
- Automatic Windows builds on GitHub, with Kairos.exe attached to each release.

## [1.1.0] - 2026-10-25

### Added
- Glow on current line option.
- Show timing info option, for checking sync.
- A note under the song title when lyrics aren't time-synced.

### Changed
- Redesigned settings: a dark panel with sections, a live preview, switches, a built-in colour picker and theme previews.
- The current line now glides into place, growing and brightening smoothly.

### Fixed
- Lines not highlighting or following the song in some cases, thanks to more reliable playback tracking.
- Unsynced lyrics sometimes chosen when time-synced lyrics were available.

## [1.0.0] - 2026-10-25

### Added
- First release: a small, always-on-top window showing time-synced Spotify lyrics, with the current line centred.
- Drag to move, resize from the corner, hover controls, right-click menu, click a line to seek, scroll to browse.
- Fully customisable look and behaviour.
