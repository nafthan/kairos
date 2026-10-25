# Kairos

*Kairos (καιρός): the ancient Greek idea of the right moment, as opposed to chronos, clock time.*

A small, always-on-top window for Windows that shows Spotify lyrics in time with the music, so you can keep them on screen while you work instead of the full Spotify window.

## Features

- The line being sung is highlighted and kept centred, gliding smoothly from line to line.
- Album art beside the title, or blurred behind the lyrics with a slow drift.
- Your own lyrics folder for songs the online library doesn't have, with import from `.lrc` and `.txt` files.
- Fully customisable: fonts, sizes, colours, themes (including your own), opacity, shape and behaviour, all previewed live.
- Drag to move, resize from the corner, lock position and size, click a line to jump to it, scroll to browse.
- No Spotify login and no third-party packages.

## Download

Get **Kairos.exe** from the [Releases](../../releases) page and run it; there's nothing to install.
It needs the .NET 10 Desktop Runtime: if it's missing, Windows will offer to download it the first time you open Kairos.

Kairos isn't signed by a publisher, so Windows may show "Windows protected your PC" the first time. Click **More info**, then **Run anyway**.

## Using it

- Play a song in the Spotify desktop app.
- **Hover** for previous / play-pause / next, settings and close. **Right-click** for quick options.
- **Click a line** to jump to it. **Scroll** to browse; *Back to current line* (or Esc) resumes following.
- Keyboard, when the window is focused: `Ctrl +` / `Ctrl −` text size, `[` / `]` lyrics later / earlier, `Space` play/pause, `Ctrl ,` settings.

## Your own lyrics

When a song has no lyrics online, click **Add lyrics for this song**, or open settings → **My lyrics**. Paste the lyrics or import files, and Kairos saves them as:

```
Documents\Kairos Lyrics\
    Artist Name\
        Song Title.lrc    time-synced: each line starts with a tag like [01:23.45]
        Other Song.txt    plain lyrics
```

You can also add or edit files there directly. Your own lyrics are always used ahead of the online library. Matching ignores capitals, accents, punctuation and suffixes like "- Remastered 2011". When importing several files at once, each file needs `[ar:]` and `[ti:]` tags or a name like `Artist - Song.lrc`.

## Where things are saved

| What | Where |
|---|---|
| Settings and saved themes | `%APPDATA%\Kairos\settings.json` |
| Lyrics looked up online (cache) | `%APPDATA%\Kairos\cache-v2` |
| Your own lyrics | `Documents\Kairos Lyrics` (can be changed in settings) |

## How it works

- **What's playing and the album art** come from Windows' built-in media controls, the same source as the volume-key media overlay.
- **Lyrics** come from your own folder first, then [LRCLIB](https://lrclib.net), a free and open database of time-synced lyrics. Spotify doesn't make its own lyrics available to other apps.
- **Spotify Canvas** videos aren't available to other apps, so Kairos can't show them; the album art background is the closest alternative.

## Troubleshooting

- **"Play a song in Spotify" stays up:** use the Spotify desktop app (not the web player) and make sure a song is playing.
- **Lyrics slightly early or late:** press `[` or `]`, or adjust *Lyrics timing* in settings.
- **Lines aren't highlighting or following:** the note under the song title says if the lyrics aren't time-synced. Otherwise, right-click → *Show timing info* and compare the position with Spotify's.
- **Clicking a line doesn't seek:** Spotify sometimes ignores seek requests from other apps; Kairos tells you when it does.
- **Wrong lyrics:** right-click → *Reload lyrics*, or add your own.

## Building from source

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows x64).
2. Double-click `build.bat`, or run:
   ```
   dotnet publish Kairos.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
   ```
3. The app is at `publish\Kairos.exe`.

## Releasing a new version

1. Change `<Version>` in `Kairos.csproj`.
2. Add an entry at the top of `CHANGELOG.md`; it appears in the app under settings → *What's new*.
3. Commit, then tag and push: `git tag v1.2.0 && git push origin v1.2.0`.

GitHub Actions builds `Kairos.exe` and creates the release automatically. It checks that the tag matches the version in `Kairos.csproj` first.
