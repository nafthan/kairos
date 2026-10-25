using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Kairos
{
    public sealed record TrackInfo(string Title, string Artist, string Album, TimeSpan Duration)
    {
        public string Key => $"{Artist}\u0001{Title}\u0001{Album}";
    }

    /// <summary>
    /// Follows the Spotify desktop app through Windows' built-in media controls
    /// (the same thing that powers the volume-key media overlay). No Spotify login needed.
    ///
    /// Spotify only reports its playback position now and then (on play, pause, seek and some
    /// track changes), and those reports can arrive late or belong to the previous song.
    /// So this keeps its own clock and treats Spotify's reports as corrections:
    ///   • a new song starts at 0:00 on our clock the moment the title changes;
    ///   • the clock runs while playing and stops while paused;
    ///   • a fresh position report from Spotify (e.g. after a seek) resets the clock to it;
    ///   • reports that are stale or from the previous song are ignored.
    /// </summary>
    public sealed class MediaWatcher : IDisposable
    {
        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private GlobalSystemMediaTransportControlsSession? _session;

        private readonly object _lock = new();
        private TimeSpan _basePosition;                 // position at the moment _clock was restarted
        private readonly Stopwatch _clock = new();      // time since _basePosition was set
        private bool _isPlaying;
        private TrackInfo? _track;
        private DateTimeOffset _lastReportStamp;        // LastUpdatedTime of the last report we've seen
        private DateTimeOffset _trackStartedAt;         // when we noticed the current song begin
        private bool _awaitingFirstReport;              // first read after discovering Spotify (app opened mid-song)
        private bool _firstTrackPending;                // the first song seen after discovering Spotify

        // Diagnostics
        private TimeSpan _lastReportPosition;
        private DateTimeOffset _lastReportReceived;
        private string _lastReportNote = "none yet";

        public event Action<TrackInfo?>? TrackChanged;
        public event Action<bool>? PlayStateChanged;

        public TrackInfo? CurrentTrack { get { lock (_lock) return _track; } }
        public bool IsPlaying { get { lock (_lock) return _isPlaying; } }

        public async Task StartAsync()
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => _ = RefreshSessionAsync();
            await RefreshSessionAsync();
        }

        /// <summary>Called every second by the window as a safety net; Spotify doesn't fire every event.</summary>
        public async Task PollAsync()
        {
            try
            {
                if (_session == null) { await RefreshSessionAsync(); return; }
                await ReadMediaPropertiesAsync();
                ReadPlaybackInfo();
                ReadTimeline();
            }
            catch
            {
                await RefreshSessionAsync();
            }
        }

        /// <summary>The current playback position.</summary>
        public TimeSpan GetPosition()
        {
            lock (_lock)
            {
                var pos = _basePosition + (_isPlaying ? _clock.Elapsed : TimeSpan.Zero);
                if (_track != null && _track.Duration > TimeSpan.Zero && pos > _track.Duration) pos = _track.Duration;
                return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
            }
        }

        public string DebugInfo
        {
            get
            {
                lock (_lock)
                {
                    var ago = _lastReportReceived == default ? "-" : $"{(DateTimeOffset.Now - _lastReportReceived).TotalSeconds:0}s ago";
                    return $"{(_isPlaying ? "playing" : "paused")} · last Spotify report {Fmt(_lastReportPosition)} ({ago}, {_lastReportNote})";
                }
            }
        }

        public bool CanSeek
        {
            get
            {
                try { return _session?.GetPlaybackInfo()?.Controls?.IsPlaybackPositionEnabled ?? false; }
                catch { return false; }
            }
        }

        public async Task<bool> SeekAsync(TimeSpan position)
        {
            var s = _session;
            if (s == null) return false;
            try
            {
                bool ok = await s.TryChangePlaybackPositionAsync(position.Ticks);
                if (ok) lock (_lock) SetPosition(position);
                return ok;
            }
            catch { return false; }
        }

        /// <summary>
        /// The current song's cover art as image bytes, or null if Spotify hasn't provided it (yet).
        /// Spotify often sends the picture a moment after the title, so callers may want to retry.
        /// </summary>
        public async Task<byte[]?> GetAlbumArtAsync()
        {
            var s = _session;
            if (s == null) return null;
            try
            {
                var props = await s.TryGetMediaPropertiesAsync();
                var thumb = props?.Thumbnail;
                if (thumb == null) return null;

                using var stream = await thumb.OpenReadAsync();
                if (stream == null || stream.Size == 0 || stream.Size > 20_000_000) return null;

                using var input = stream.GetInputStreamAt(0);
                using var reader = new DataReader(input);
                uint size = (uint)stream.Size;
                await reader.LoadAsync(size);
                var bytes = new byte[size];
                reader.ReadBytes(bytes);
                return bytes;
            }
            catch
            {
                return null;
            }
        }

        public async Task TogglePlayPauseAsync() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { } }
        public async Task NextAsync() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } }
        public async Task PreviousAsync() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } }

        // =================================================================

        private void SetPosition(TimeSpan pos)
        {
            _basePosition = pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
            _clock.Restart();
        }

        private async Task RefreshSessionAsync()
        {
            if (_manager == null) return;

            GlobalSystemMediaTransportControlsSession? spotify = null;
            try
            {
                spotify = _manager.GetSessions()
                    .FirstOrDefault(s => s.SourceAppUserModelId?.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { }

            if (spotify != null && _session != null && spotify.SourceAppUserModelId == _session.SourceAppUserModelId)
                return;

            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelineChanged;
            }

            _session = spotify;

            lock (_lock)
            {
                _track = null;
                _isPlaying = false;
                _lastReportStamp = default;
                SetPosition(TimeSpan.Zero);
                _awaitingFirstReport = true;
                _firstTrackPending = true;
            }

            if (_session == null)
            {
                TrackChanged?.Invoke(null);
                return;
            }

            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;

            ReadPlaybackInfo();
            await ReadMediaPropertiesAsync();
            ReadTimeline();
        }

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = ReadMediaPropertiesAsync();
        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => ReadPlaybackInfo();
        private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => ReadTimeline();

        private async Task ReadMediaPropertiesAsync()
        {
            var s = _session;
            if (s == null) return;
            try
            {
                var props = await s.TryGetMediaPropertiesAsync();
                if (props == null || string.IsNullOrWhiteSpace(props.Title)) return;

                TimeSpan duration = TimeSpan.Zero;
                try { duration = s.GetTimelineProperties().EndTime; } catch { }

                var info = new TrackInfo(props.Title ?? "", props.Artist ?? "", props.AlbumTitle ?? "", duration);

                bool changed;
                lock (_lock)
                {
                    changed = _track == null || _track.Key != info.Key;
                    if (changed)
                    {
                        bool firstSeen = _firstTrackPending;
                        _firstTrackPending = false;
                        _track = info;
                        _trackStartedAt = DateTimeOffset.Now;
                        if (!firstSeen)
                        {
                            // A new song: start our clock at zero. A fresh report from Spotify will correct it if needed.
                            SetPosition(TimeSpan.Zero);
                        }
                        else if (info.Duration > TimeSpan.Zero &&
                                 _basePosition + (_isPlaying ? _clock.Elapsed : TimeSpan.Zero) > info.Duration)
                        {
                            // The startup estimate overshot the song (Spotify's report was too old to extrapolate
                            // from); fall back to the position Spotify actually reported.
                            SetPosition(_lastReportPosition <= info.Duration ? _lastReportPosition : TimeSpan.Zero);
                        }
                    }
                    else if (info.Duration > TimeSpan.Zero && _track!.Duration != info.Duration)
                    {
                        _track = info; // Spotify sometimes reports the length a moment late
                    }
                }

                if (changed)
                {
                    ReadTimeline();
                    TrackChanged?.Invoke(info);
                }
            }
            catch { }
        }

        private void ReadPlaybackInfo()
        {
            var s = _session;
            if (s == null) return;
            try
            {
                bool playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                bool changed;
                lock (_lock)
                {
                    changed = playing != _isPlaying;
                    if (changed)
                    {
                        // Freeze or resume our clock at the exact moment of the change.
                        var now = _basePosition + (_isPlaying ? _clock.Elapsed : TimeSpan.Zero);
                        _isPlaying = playing;
                        SetPosition(now);
                    }
                }
                if (changed)
                {
                    ReadTimeline();
                    PlayStateChanged?.Invoke(playing);
                }
            }
            catch { }
        }

        private void ReadTimeline()
        {
            var s = _session;
            if (s == null) return;
            try
            {
                var t = s.GetTimelineProperties();
                var stamp = t.LastUpdatedTime;
                var position = t.Position;
                var now = DateTimeOffset.Now;

                lock (_lock)
                {
                    bool sameStamp = stamp == _lastReportStamp;
                    if (sameStamp && position == _lastReportPosition && !_awaitingFirstReport) return; // nothing new
                    // Same timestamp but a different position means Spotify changed it without restamping: treat as fresh.
                    bool freshWithoutStamp = sameStamp && !_awaitingFirstReport;
                    _lastReportStamp = stamp;
                    _lastReportPosition = position;
                    _lastReportReceived = now;

                    bool stampValid = stamp.Year > 2000 && !freshWithoutStamp;
                    var age = stampValid ? now - stamp : TimeSpan.Zero;
                    if (age < TimeSpan.Zero) age = TimeSpan.Zero;

                    if (_track != null && _track.Duration > TimeSpan.Zero && position > _track.Duration + TimeSpan.FromSeconds(2))
                    {
                        _lastReportNote = "ignored: past end of song";
                        return;
                    }

                    if (_awaitingFirstReport)
                    {
                        // Kairos just started (or Spotify just opened) mid-song: trust Spotify's position,
                        // plus however long it has been playing since that report.
                        _awaitingFirstReport = false;
                        var p = position + (_isPlaying && age < TimeSpan.FromMinutes(20) ? age : TimeSpan.Zero);
                        if (_track != null && _track.Duration > TimeSpan.Zero && p > _track.Duration)
                            p = position; // report is too old to extrapolate from
                        SetPosition(p);
                        _lastReportNote = "used (startup)";
                        return;
                    }

                    if (stampValid && stamp < _trackStartedAt - TimeSpan.FromSeconds(1.5))
                    {
                        // This report was made before the current song began, so it's about the previous song.
                        _lastReportNote = "ignored: from previous song";
                        return;
                    }

                    if (age > TimeSpan.FromSeconds(5))
                    {
                        // An old report that only just reached us; can't tell what happened since.
                        _lastReportNote = "ignored: stale";
                        return;
                    }

                    SetPosition(position + (_isPlaying ? age : TimeSpan.Zero));
                    _lastReportNote = "used";
                }
            }
            catch { }
        }

        private static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";

        public void Dispose()
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelineChanged;
            }
            _session = null;
            _manager = null;
        }
    }
}
