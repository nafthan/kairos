using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Kairos
{
    public sealed class PlaylistInfo
    {
        public string Id { get; init; } = "";
        public string Uri { get; init; } = "";
        public string Name { get; init; } = "";
        public string Owner { get; init; } = "";
        public string? ImageUrl { get; init; }
        public int Total { get; init; }
        /// <summary>You own it or it's collaborative, so songs can be added and its contents can be read.</summary>
        public bool Editable { get; init; }
        /// <summary>The special "Liked Songs" collection rather than a real playlist.</summary>
        public bool IsLikedSongs { get; init; }
        /// <summary>Someone else's playlist that you can add to (you're a collaborator).</summary>
        public bool IsShared { get; init; }
        /// <summary>Added to Kairos by pasting its link.</summary>
        public bool AddedByLink { get; init; }
        public string Subtitle => IsLikedSongs
            ? (Total == 1 ? "1 song" : $"{Total} songs")
            : (Total > 0 ? $"{Total} songs  ·  {Owner}" : Owner) + (IsShared ? "  ·  shared with you" : "");
    }

    public sealed class PlaylistTrack
    {
        public int Number { get; init; }
        public string Title { get; init; } = "";
        public string Artists { get; init; } = "";
        public string Album { get; init; } = "";
        public string Duration { get; init; } = "";
        public string Uri { get; init; } = "";
        public string? ImageUrl { get; init; }
    }

    /// <summary>A song from a Spotify search.</summary>
    public sealed class SearchTrack
    {
        public string Title { get; init; } = "";
        public string Artists { get; init; } = "";
        public string Album { get; init; } = "";
        public string? AlbumUri { get; init; }
        public string Uri { get; init; } = "";
        public string? ImageUrl { get; init; }
        public string Duration { get; init; } = "";
    }

    public sealed class SpotifyException : Exception
    {
        public SpotifyException(string message, int status = 0) : base(message) => Status = status;
        /// <summary>The HTTP status from Spotify, when there was one.</summary>
        public int Status { get; }
    }

    /// <summary>
    /// Talks to Spotify's Web API for playlists. Each user registers their own (free) Spotify developer app
    /// and pastes its Client ID into settings; sign-in uses the PKCE flow, so no secret is ever stored.
    /// The sign-in token is kept encrypted with Windows' own per-user data protection.
    /// </summary>
    public sealed class SpotifyClient
    {
        public const int CallbackPort = 43821;
        public static readonly string RedirectUri = $"http://127.0.0.1:{CallbackPort}/callback";
        public const string DashboardUrl = "https://developer.spotify.com/dashboard";

        private const string Scopes =
            "playlist-read-private playlist-read-collaborative playlist-modify-private playlist-modify-public " +
            "user-read-currently-playing user-read-playback-state user-modify-playback-state " +
            "user-library-read user-library-modify";

        /// <summary>Id used for the Liked Songs collection in place of a playlist id.</summary>
        public const string LikedSongsId = "liked-songs";

        /// <summary>
        /// Liked Songs needs two permissions added in Kairos 1.4. People who connected with an earlier version
        /// are asked to reconnect once to grant them.
        /// </summary>
        public bool HasLibraryAccess => IsConnected && _s.SpotifyScopes.Contains("user-library-read") && _s.SpotifyScopes.Contains("user-library-modify");

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        private readonly Settings _s;
        private string? _accessToken;
        private DateTime _accessExpires;
        private string? _userId;
        private List<PlaylistInfo>? _playlistCache;
        private DateTime _playlistCacheTime;

        public SpotifyClient(Settings settings) => _s = settings;

        public bool IsConnected => !string.IsNullOrEmpty(_s.SpotifyClientId) && !string.IsNullOrEmpty(_s.SpotifyToken);

        // =================================================================
        //  Sign-in
        // =================================================================

        /// <summary>Opens the browser to sign in, waits for Spotify to send the user back, and stores the token.</summary>
        public async Task ConnectAsync(string clientId, CancellationToken ct)
        {
            clientId = clientId.Trim();
            if (clientId.Length < 16) throw new SpotifyException("That doesn't look like a Client ID. Copy it from your app's page in the Spotify dashboard.");

            string verifier = RandomString(64);
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string state = RandomString(16);

            // Start listening before opening the browser, so we can't miss the reply.
            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, CallbackPort);
                listener.Start();
            }
            catch (SocketException)
            {
                throw new SpotifyException($"Port {CallbackPort} is in use by another program. Close it and try again.");
            }

            try
            {
                string url = "https://accounts.spotify.com/authorize" +
                             $"?client_id={Uri.EscapeDataString(clientId)}" +
                             "&response_type=code" +
                             $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                             "&code_challenge_method=S256" +
                             $"&code_challenge={challenge}" +
                             $"&scope={Uri.EscapeDataString(Scopes)}" +
                             $"&state={state}";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                var reply = await WaitForCallbackAsync(listener, timeout.Token);

                if (reply.TryGetValue("error", out var error))
                    throw new SpotifyException(error == "access_denied" ? "Sign-in was cancelled." : $"Spotify said: {error}");
                if (!reply.TryGetValue("state", out var gotState) || gotState != state)
                    throw new SpotifyException("Sign-in reply didn't match. Please try again.");
                if (!reply.TryGetValue("code", out var code))
                    throw new SpotifyException("Spotify didn't send a sign-in code. Please try again.");

                var token = await PostTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = RedirectUri,
                    ["client_id"] = clientId,
                    ["code_verifier"] = verifier,
                }, ct);

                _s.SpotifyClientId = clientId;
                StoreTokens(token);
                _playlistCache = null; // permissions may have changed (e.g. Liked Songs now allowed)

                // Find out who we are, for the "Connected as …" line.
                using var me = await GetJsonAsync("me", ct);
                _userId = Str(me!.RootElement, "id");
                _s.SpotifyUserName = Str(me.RootElement, "display_name") ?? _userId ?? "Spotify user";
                _s.Save();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new SpotifyException("Sign-in timed out. Please try again.");
            }
            finally
            {
                listener.Stop();
            }
        }

        public void Disconnect()
        {
            _s.SpotifyToken = "";
            _s.SpotifyUserName = "";
            _s.SpotifyScopes = "";
            _accessToken = null;
            _userId = null;
            _playlistCache = null;
            _s.Save();
        }

        /// <summary>
        /// A tiny one-page web server on 127.0.0.1 for Spotify's redirect after sign-in.
        /// (A plain socket, so it works without administrator rights.)
        /// </summary>
        private static async Task<Dictionary<string, string>> WaitForCallbackAsync(TcpListener listener, CancellationToken ct)
        {
            using var reg = ct.Register(listener.Stop);
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(ct); }
                catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }

                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    int read = 0;
                    var readTimeout = Task.Delay(5000, ct);
                    while (read < buffer.Length)
                    {
                        var readTask = stream.ReadAsync(buffer, read, buffer.Length - read, ct);
                        if (await Task.WhenAny(readTask, readTimeout) != readTask) break;
                        int n = await readTask;
                        if (n == 0) break;
                        read += n;
                        if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n")) break;
                    }

                    string request = Encoding.ASCII.GetString(buffer, 0, read);
                    string firstLine = request.Split("\r\n")[0]; // GET /callback?code=…&state=… HTTP/1.1
                    var parts = firstLine.Split(' ');
                    string target = parts.Length > 1 ? parts[1] : "";

                    if (!target.StartsWith("/callback"))
                    {
                        await Respond(stream, "404 Not Found", "Not found");
                        continue; // e.g. the browser asking for /favicon.ico
                    }

                    var query = new Dictionary<string, string>();
                    int q = target.IndexOf('?');
                    if (q >= 0)
                    {
                        foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var kv = pair.Split('=', 2);
                            query[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
                        }
                    }

                    bool ok = query.ContainsKey("code");
                    await Respond(stream, "200 OK", ok
                        ? "Kairos is connected to Spotify. You can close this tab."
                        : "Sign-in didn't complete. You can close this tab and try again from Kairos.");
                    return query;
                }
            }
        }

        private static async Task Respond(NetworkStream stream, string status, string message)
        {
            string html =
                "<!doctype html><html><head><meta charset=\"utf-8\"><title>Kairos</title></head>" +
                "<body style=\"margin:0;height:100vh;display:flex;align-items:center;justify-content:center;" +
                "background:#121212;color:#f2f2f2;font:16px 'Segoe UI',sans-serif\">" +
                $"<p>{WebUtility.HtmlEncode(message)}</p></body></html>";
            byte[] body = Encoding.UTF8.GetBytes(html);
            string header = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            try
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
            catch { }
        }

        // =================================================================
        //  Tokens
        // =================================================================

        private async Task<JsonDocument> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
        {
            HttpResponseMessage resp;
            try { resp = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form), ct); }
            catch (HttpRequestException) { throw new SpotifyException("Couldn't reach Spotify. Check your internet connection."); }
            using var response = resp;
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                string reason = "";
                try { using var err = JsonDocument.Parse(body); reason = Str(err.RootElement, "error_description") ?? Str(err.RootElement, "error") ?? ""; } catch { }
                throw new SpotifyException(string.IsNullOrEmpty(reason) ? $"Spotify sign-in failed ({(int)resp.StatusCode})." : $"Spotify sign-in failed: {reason}",
                                           (int)resp.StatusCode);
            }
            return JsonDocument.Parse(body);
        }

        private void StoreTokens(JsonDocument token)
        {
            using (token)
            {
                var root = token.RootElement;
                _accessToken = Str(root, "access_token");
                var scope = Str(root, "scope");
                if (!string.IsNullOrEmpty(scope)) _s.SpotifyScopes = scope; // what the user actually granted
                int expires = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var x) ? x : 3600;
                _accessExpires = DateTime.UtcNow.AddSeconds(expires - 60);
                var refresh = Str(root, "refresh_token");
                if (!string.IsNullOrEmpty(refresh)) _s.SpotifyToken = Protect(refresh);
            }
        }

        private readonly SemaphoreSlim _refreshGate = new(1, 1);

        private async Task<string> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
        {
            if (!forceRefresh && _accessToken != null && DateTime.UtcNow < _accessExpires) return _accessToken;

            // Only one renewal at a time; anyone else waiting just uses the fresh token.
            await _refreshGate.WaitAsync(ct);
            try
            {
                if (!forceRefresh && _accessToken != null && DateTime.UtcNow < _accessExpires) return _accessToken;
                return await RenewAccessTokenAsync(ct);
            }
            finally { _refreshGate.Release(); }
        }

        private async Task<string> RenewAccessTokenAsync(CancellationToken ct)
        {
            if (!IsConnected) throw new SpotifyException("Connect Spotify in settings first.");

            var refresh = Unprotect(_s.SpotifyToken);
            if (refresh == null)
            {
                Disconnect();
                throw new SpotifyException("Your Spotify sign-in has expired. Connect again in settings.");
            }

            try
            {
                var token = await PostTokenAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refresh,
                    ["client_id"] = _s.SpotifyClientId,
                }, ct);
                StoreTokens(token);
                _s.Save();
            }
            catch (SpotifyException e) when (e.Status == 400 || e.Status == 401)
            {
                // The sign-in was revoked (e.g. access removed on spotify.com). Network trouble doesn't get here.
                Disconnect();
                throw new SpotifyException("Your Spotify sign-in has expired. Connect again in settings.");
            }
            return _accessToken ?? throw new SpotifyException("Couldn't sign in to Spotify.");
        }

        // =================================================================
        //  API
        // =================================================================

        /// <summary>The URI (spotify:track:…) of what's playing, or null if nothing / a podcast / an ad.</summary>
        public async Task<(string Uri, string Name)?> GetCurrentTrackAsync(CancellationToken ct = default)
        {
            using var doc = await GetJsonAsync("me/player/currently-playing", ct, allowEmpty: true);
            if (doc == null || !doc.RootElement.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
            var uri = Str(item, "uri");
            if (uri == null || !uri.StartsWith("spotify:track:")) return null;
            return (uri, Str(item, "name") ?? "");
        }

        /// <summary>The album of the song playing now, as a spotify:album:… link (null if nothing is playing).</summary>
        public async Task<string?> GetCurrentAlbumUriAsync(CancellationToken ct = default)
        {
            using var doc = await GetJsonAsync("me/player/currently-playing", ct, allowEmpty: true);
            if (doc == null || !doc.RootElement.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
            if (!item.TryGetProperty("album", out var album) || album.ValueKind != JsonValueKind.Object) return null;
            var uri = Str(album, "uri");
            return uri != null && uri.StartsWith("spotify:album:") ? uri : null;
        }

        /// <summary>Your Spotify profile, as a spotify:user:… link.</summary>
        public async Task<string?> GetMyProfileUriAsync(CancellationToken ct = default)
        {
            if (_userId == null)
            {
                using var me = await GetJsonAsync("me", ct);
                _userId = Str(me!.RootElement, "id");
            }
            return _userId == null ? null : $"spotify:user:{_userId}";
        }

        /// <summary>Your Spotify account's profile picture (the biggest size Spotify has), or null if you haven't set one.</summary>
        public async Task<byte[]?> GetProfilePictureAsync(CancellationToken ct = default)
        {
            using var me = await GetJsonAsync("me", ct);
            if (me == null || !me.RootElement.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
                return null;
            string? best = null;
            int bestWidth = -1;
            foreach (var image in images.EnumerateArray())
            {
                var url = Str(image, "url");
                if (string.IsNullOrEmpty(url)) continue;
                int width = image.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0;
                if (width > bestWidth) { best = url; bestWidth = width; }
            }
            if (best == null) return null;
            return await Http.GetByteArrayAsync(best, ct);
        }

        /// <summary>Your playlists (cached for a few minutes).</summary>
        public async Task<List<PlaylistInfo>> GetPlaylistsAsync(CancellationToken ct = default, bool refresh = false)
        {
            if (!refresh && _playlistCache != null && DateTime.UtcNow - _playlistCacheTime < TimeSpan.FromMinutes(5))
                return _playlistCache;

            if (_userId == null)
            {
                using var me = await GetJsonAsync("me", ct);
                _userId = Str(me!.RootElement, "id");
            }

            // Re-check which shared playlists you can edit once a week (or when you press Refresh).
            if (refresh || DateTime.UtcNow - _s.SpotifyCanEditChecked > TimeSpan.FromDays(7))
            {
                _s.SpotifyCanEdit.Clear();
                _s.SpotifyCanEditChecked = DateTime.UtcNow;
            }

            var raw = new List<RawPlaylist>();
            string? next = "me/playlists?limit=50";
            while (next != null && raw.Count < 1000)
            {
                using var page = await GetJsonAsync(next, ct);
                var root = page!.RootElement;
                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var p in items.EnumerateArray())
                        if (p.ValueKind == JsonValueKind.Object) raw.Add(ReadPlaylist(p, byLink: false));
                next = NextPage(root);
            }

            // Shared playlists added by link (Spotify leaves out ones you collaborate on but don't follow).
            foreach (var id in _s.SpotifyExtraPlaylists.ToList())
            {
                if (raw.Any(r => r.Id == id)) continue;
                try
                {
                    using var doc = await GetJsonAsync($"playlists/{id}", ct);
                    raw.Add(ReadPlaylist(doc!.RootElement, byLink: true));
                }
                catch (SpotifyException e) when (e.Status == 404)
                {
                    _s.SpotifyExtraPlaylists.Remove(id); // deleted, or no longer shared
                }
                catch (SpotifyException) { }
            }

            // Playlists you don't own and that aren't marked collaborative may still be shared with you
            // (Spotify's invite-a-collaborator feature doesn't set the flag). Check, a few at a time.
            var unknown = raw.Where(r => r.OwnerId != _userId && !r.Collaborative && !_s.SpotifyCanEdit.ContainsKey(r.Id))
                             .Take(60).ToList();
            if (unknown.Count > 0)
            {
                using var gate = new SemaphoreSlim(5);
                await Task.WhenAll(unknown.Select(async r =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var can = await CanEditAsync(r.Id, ct);
                        if (can.HasValue) lock (_s.SpotifyCanEdit) _s.SpotifyCanEdit[r.Id] = can.Value;
                    }
                    finally { gate.Release(); }
                }));
                _s.Save();
            }

            var list = new List<PlaylistInfo>();
            foreach (var r in raw)
            {
                bool owned = r.OwnerId == _userId;
                bool shared = !owned && !r.Collaborative && _s.SpotifyCanEdit.TryGetValue(r.Id, out var can) && can;
                list.Add(new PlaylistInfo
                {
                    Id = r.Id,
                    Uri = r.Uri,
                    Name = r.Name,
                    Owner = r.OwnerName,
                    ImageUrl = r.ImageUrl,
                    Total = r.Total,
                    Editable = owned || r.Collaborative || shared,
                    IsShared = shared || (!owned && r.Collaborative),
                    AddedByLink = r.ByLink,
                });
            }

            // Liked Songs isn't a playlist in Spotify's API, but it belongs at the top of the list.
            if (HasLibraryAccess)
            {
                int likedCount = 0;
                try
                {
                    using var liked = await GetJsonAsync("me/tracks?limit=1", ct);
                    if (liked!.RootElement.TryGetProperty("total", out var t) && t.TryGetInt32(out var n)) likedCount = n;
                }
                catch (SpotifyException) { /* still show it; the count is just a nicety */ }

                list.Insert(0, new PlaylistInfo
                {
                    Id = LikedSongsId,
                    Uri = $"spotify:user:{_userId}:collection",
                    Name = "Liked Songs",
                    Owner = "You",
                    Total = likedCount,
                    Editable = true,
                    IsLikedSongs = true,
                });
            }

            _playlistCache = list;
            _playlistCacheTime = DateTime.UtcNow;
            return list;
        }

        private sealed record RawPlaylist(string Id, string Uri, string Name, string OwnerId, string OwnerName,
                                          string? ImageUrl, int Total, bool Collaborative, bool ByLink);

        private static RawPlaylist ReadPlaylist(JsonElement p, bool byLink)
        {
            string ownerId = p.TryGetProperty("owner", out var o) ? Str(o, "id") ?? "" : "";
            string ownerName = p.TryGetProperty("owner", out var o2) ? Str(o2, "display_name") ?? ownerId : "";
            bool collaborative = p.TryGetProperty("collaborative", out var c) && c.ValueKind == JsonValueKind.True;
            return new RawPlaylist(Str(p, "id") ?? "", Str(p, "uri") ?? "", Str(p, "name") ?? "Untitled",
                                   ownerId, ownerName, FirstImage(p), CountOf(p), collaborative, byLink);
        }

        /// <summary>Spotify only lets apps read the songs of playlists you own or collaborate on, so that tells us.</summary>
        private async Task<bool?> CanEditAsync(string playlistId, CancellationToken ct)
        {
            try
            {
                using var doc = await GetJsonAsync($"playlists/{playlistId}/items?limit=1", ct);
                return doc!.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array;
            }
            catch (SpotifyException e) when (e.Status == 403 || e.Status == 404) { return false; }
            catch (SpotifyException) { return null; } // couldn't tell (network, busy): ask again later
        }

        /// <summary>Adds a playlist by its link (open.spotify.com/playlist/… or spotify:playlist:…). Returns its name.</summary>
        public async Task<string> AddSharedPlaylistAsync(string linkOrId, CancellationToken ct = default)
        {
            var id = ParsePlaylistId(linkOrId) ?? throw new SpotifyException("That doesn't look like a Spotify playlist link. In Spotify, use Share → Copy link to playlist.");
            using var doc = await GetJsonAsync($"playlists/{id}", ct);
            var name = Str(doc!.RootElement, "name") ?? "Playlist";
            if (!_s.SpotifyExtraPlaylists.Contains(id)) _s.SpotifyExtraPlaylists.Add(id);
            _s.SpotifyCanEdit.Remove(id); // check it fresh
            _s.Save();
            _playlistCache = null;
            return name;
        }

        public void RemoveSharedPlaylist(string id)
        {
            _s.SpotifyExtraPlaylists.Remove(id);
            _s.Save();
            _playlistCache = null;
        }

        public static string? ParsePlaylistId(string text)
        {
            text = text.Trim();
            var m = System.Text.RegularExpressions.Regex.Match(text, @"playlist[/:]([A-Za-z0-9]{16,40})");
            if (m.Success) return m.Groups[1].Value;
            return System.Text.RegularExpressions.Regex.IsMatch(text, @"^[A-Za-z0-9]{22}$") ? text : null;
        }

        public async Task<List<PlaylistTrack>> GetPlaylistTracksAsync(string playlistId, CancellationToken ct = default)
        {
            var tracks = new List<PlaylistTrack>();
            string? next = playlistId == LikedSongsId ? "me/tracks?limit=50" : $"playlists/{playlistId}/items?limit=50";
            while (next != null && tracks.Count < 5000)
            {
                using var page = await GetJsonAsync(next, ct);
                var root = page!.RootElement;
                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in items.EnumerateArray())
                    {
                        // Newer responses call the song "item"; older ones called it "track".
                        JsonElement t;
                        if (!(entry.TryGetProperty("item", out t) && t.ValueKind == JsonValueKind.Object) &&
                            !(entry.TryGetProperty("track", out t) && t.ValueKind == JsonValueKind.Object))
                            continue;

                        string artists = t.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
                            ? string.Join(", ", a.EnumerateArray().Select(x => Str(x, "name")).Where(n => !string.IsNullOrEmpty(n)))
                            : "";
                        string album = "";
                        string? image = null;
                        if (t.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object)
                        {
                            album = Str(al, "name") ?? "";
                            image = SmallestImage(al);
                        }
                        int ms = t.TryGetProperty("duration_ms", out var d) && d.TryGetInt32(out var v) ? v : 0;

                        tracks.Add(new PlaylistTrack
                        {
                            Number = tracks.Count + 1,
                            Title = Str(t, "name") ?? "",
                            Artists = artists,
                            Album = album,
                            Duration = ms > 0 ? $"{ms / 60000}:{ms / 1000 % 60:00}" : "",
                            Uri = Str(t, "uri") ?? "",
                            ImageUrl = image,
                        });
                    }
                }
                next = NextPage(root);
            }
            return tracks;
        }

        public async Task AddToPlaylistAsync(string playlistId, string trackUri, CancellationToken ct = default)
        {
            var body = JsonSerializer.Serialize(new { uris = new[] { trackUri } });
            if (playlistId == LikedSongsId)
            {
                if (!HasLibraryAccess) throw new SpotifyException("Reconnect Spotify in settings to use Liked Songs.");
                // Saving to your library: Spotify URIs, sent both ways it accepts them.
                await SendAsync(HttpMethod.Put, $"me/library?uris={Uri.EscapeDataString(trackUri)}", body, ct);
                _playlistCache = null;
                return;
            }
            await SendAsync(HttpMethod.Post, $"playlists/{playlistId}/items", body, ct);
            _playlistCache = null; // song counts changed
        }

        /// <summary>Is this song already in your Liked Songs?</summary>
        public async Task<bool> IsLikedAsync(string trackUri, CancellationToken ct = default)
        {
            if (!HasLibraryAccess) return false;
            try
            {
                using var doc = await GetJsonAsync($"me/library/contains?uris={Uri.EscapeDataString(trackUri)}", ct);
                var root = doc!.RootElement;
                return root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.True;
            }
            catch (SpotifyException)
            {
                return false; // not worth failing the add over
            }
        }

        /// <summary>Plays a list of songs (used for Liked Songs if Spotify won't play it as a collection).</summary>
        public async Task PlayTracksAsync(IEnumerable<string> trackUris, CancellationToken ct = default)
        {
            var uris = trackUris.Take(100).ToArray();
            if (uris.Length == 0) return;
            await SendAsync(HttpMethod.Put, "me/player/play", JsonSerializer.Serialize(new { uris }), ct);
        }

        /// <summary>
        /// Where the playing song is playing from: ("playlist", id) for a playlist, or ("album"/"artist"/"collection", …).
        /// Null if nothing is playing or Spotify doesn't say.
        /// </summary>
        public async Task<(string Type, string Id, string Uri)?> GetPlaybackContextAsync(CancellationToken ct = default)
        {
            using var doc = await GetJsonAsync("me/player/currently-playing", ct, allowEmpty: true);
            if (doc == null || !doc.RootElement.TryGetProperty("context", out var c) || c.ValueKind != JsonValueKind.Object) return null;
            var uri = Str(c, "uri");
            var type = Str(c, "type") ?? "";
            if (string.IsNullOrEmpty(uri)) return null;
            if (uri.EndsWith(":collection")) return ("collection", LikedSongsId, uri);
            var id = uri.Split(':').LastOrDefault() ?? "";
            return (type, id, uri);
        }

        /// <summary>The playlist's name (for showing a playlist you marked by id).</summary>
        public async Task<string?> GetPlaylistNameAsync(string id, CancellationToken ct = default)
        {
            var cached = _playlistCache?.FirstOrDefault(p => p.Id == id);
            if (cached != null) return cached.Name;
            try
            {
                using var doc = await GetJsonAsync($"playlists/{id}", ct);
                return Str(doc!.RootElement, "name");
            }
            catch (SpotifyException) { return null; }
        }

        // ----- Spotify's own volume (used when Windows can't find Spotify's sound) -----

        public async Task<int?> GetVolumeAsync(CancellationToken ct = default)
        {
            using var doc = await GetJsonAsync("me/player", ct, allowEmpty: true);
            if (doc == null || !doc.RootElement.TryGetProperty("device", out var d) || d.ValueKind != JsonValueKind.Object) return null;
            return d.TryGetProperty("volume_percent", out var v) && v.TryGetInt32(out var n) ? n : null;
        }

        public async Task SetVolumeAsync(int percent, CancellationToken ct = default) =>
            await SendAsync(HttpMethod.Put, $"me/player/volume?volume_percent={Math.Clamp(percent, 0, 100)}", null, ct);

        // ----- Search -----

        /// <summary>Searches Spotify for songs and playlists (10 of each at a time; use offset for more).</summary>
        public async Task<(List<SearchTrack> Tracks, List<PlaylistInfo> Playlists, bool MoreTracks, bool MorePlaylists)> SearchAsync(
            string query, int trackOffset = 0, int playlistOffset = 0, bool tracks = true, bool playlists = true, CancellationToken ct = default)
        {
            var foundTracks = new List<SearchTrack>();
            var foundPlaylists = new List<PlaylistInfo>();
            bool moreTracks = false, morePlaylists = false;
            if (string.IsNullOrWhiteSpace(query)) return (foundTracks, foundPlaylists, false, false);

            async Task<JsonDocument> Search(string type, int offset) =>
                (await GetJsonAsync($"search?q={Uri.EscapeDataString(query.Trim())}&type={type}&limit=10&offset={offset}", ct))!;

            if (tracks)
            {
                using var doc = await Search("track", trackOffset);
                if (doc.RootElement.TryGetProperty("tracks", out var t) && t.ValueKind == JsonValueKind.Object)
                {
                    if (t.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var item in items.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object) continue;
                            string artists = item.TryGetProperty("artists", out var a) && a.ValueKind == JsonValueKind.Array
                                ? string.Join(", ", a.EnumerateArray().Select(x => Str(x, "name")).Where(n => !string.IsNullOrEmpty(n)))
                                : "";
                            string album = "", image = "";
                            string? albumUri = null;
                            if (item.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object)
                            {
                                album = Str(al, "name") ?? "";
                                albumUri = Str(al, "uri");
                                image = SmallestImage(al) ?? "";
                            }
                            int ms = item.TryGetProperty("duration_ms", out var d) && d.TryGetInt32(out var v) ? v : 0;
                            foundTracks.Add(new SearchTrack
                            {
                                Title = Str(item, "name") ?? "",
                                Artists = artists,
                                Album = album,
                                AlbumUri = albumUri,
                                Uri = Str(item, "uri") ?? "",
                                ImageUrl = string.IsNullOrEmpty(image) ? null : image,
                                Duration = ms > 0 ? $"{ms / 60000}:{ms / 1000 % 60:00}" : "",
                            });
                        }
                    moreTracks = NextPage(t) != null;
                }
            }

            if (playlists)
            {
                using var doc = await Search("playlist", playlistOffset);
                if (doc.RootElement.TryGetProperty("playlists", out var pl) && pl.ValueKind == JsonValueKind.Object)
                {
                    if (pl.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var p in items.EnumerateArray())
                        {
                            if (p.ValueKind != JsonValueKind.Object) continue; // Spotify sometimes returns empty entries
                            var r = ReadPlaylist(p, byLink: false);
                            foundPlaylists.Add(new PlaylistInfo
                            {
                                Id = r.Id, Uri = r.Uri, Name = r.Name, Owner = r.OwnerName, ImageUrl = r.ImageUrl, Total = r.Total,
                                Editable = _userId != null && r.OwnerId == _userId,
                            });
                        }
                    morePlaylists = NextPage(pl) != null;
                }
            }
            return (foundTracks, foundPlaylists, moreTracks, morePlaylists);
        }

        /// <summary>Plays one song, continuing through its album afterwards.</summary>
        public async Task PlayTrackAsync(SearchTrack track, CancellationToken ct = default)
        {
            object payload = track.AlbumUri != null
                ? new { context_uri = track.AlbumUri, offset = new { uri = track.Uri } }
                : new { uris = new[] { track.Uri } };
            await SendAsync(HttpMethod.Put, "me/player/play", JsonSerializer.Serialize(payload), ct);
        }

        public async Task QueueAsync(string uri, CancellationToken ct = default) =>
            await SendAsync(HttpMethod.Post, $"me/player/queue?uri={Uri.EscapeDataString(uri)}", null, ct);

        /// <summary>Saves a song to Liked Songs, or a playlist to your library.</summary>
        public async Task SaveToLibraryAsync(string uri, CancellationToken ct = default)
        {
            if (uri.StartsWith("spotify:track:") && !HasLibraryAccess)
                throw new SpotifyException("Reconnect Spotify in settings to use Liked Songs.");
            var body = JsonSerializer.Serialize(new { uris = new[] { uri } });
            await SendAsync(HttpMethod.Put, $"me/library?uris={Uri.EscapeDataString(uri)}", body, ct);
            _playlistCache = null;
        }

        /// <summary>Jumps to a position in the playing song (used when Windows' own media controls can't seek).</summary>
        public async Task SeekAsync(TimeSpan position, CancellationToken ct = default)
        {
            long ms = (long)Math.Max(0, position.TotalMilliseconds);
            await SendAsync(HttpMethod.Put, $"me/player/seek?position_ms={ms}", null, ct);
        }

        /// <summary>Plays a playlist in Spotify, optionally starting at a particular song.</summary>
        public async Task PlayAsync(string contextUri, string? startTrackUri, CancellationToken ct = default)
        {
            object payload = startTrackUri == null
                ? new { context_uri = contextUri }
                : new { context_uri = contextUri, offset = new { uri = startTrackUri } };
            await SendAsync(HttpMethod.Put, "me/player/play", JsonSerializer.Serialize(payload), ct);
        }

        // ----- HTTP plumbing -----

        private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct, bool allowEmpty = false)
        {
            var body = await SendAsync(HttpMethod.Get, path, null, ct);
            if (string.IsNullOrWhiteSpace(body))
            {
                if (allowEmpty) return null;
                throw new SpotifyException("Spotify sent an empty reply.");
            }
            return JsonDocument.Parse(body);
        }

        private async Task<string> SendAsync(HttpMethod method, string path, string? json, CancellationToken ct)
        {
            string url = path.StartsWith("http") ? path : "https://api.spotify.com/v1/" + path;

            for (int attempt = 0; ; attempt++)
            {
                var token = await GetAccessTokenAsync(ct, forceRefresh: attempt == 1);
                using var req = new HttpRequestMessage(method, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                else if (method != HttpMethod.Get) req.Content = new ByteArrayContent(Array.Empty<byte>()); // explicit empty body

                HttpResponseMessage resp;
                try { resp = await Http.SendAsync(req, ct); }
                catch (HttpRequestException) { throw new SpotifyException("Couldn't reach Spotify. Check your internet connection."); }

                using (resp)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    if (resp.IsSuccessStatusCode) return body;

                    int status = (int)resp.StatusCode;
                    if (status == 401 && attempt == 0) continue; // token expired early: refresh and retry once

                    if (status == 429)
                    {
                        if (body.Contains("QUOTA_EXCEEDED"))
                            throw new SpotifyException("Your Spotify developer app has used its daily allowance. It resets later.");
                        var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                        if (attempt == 0 && wait < TimeSpan.FromSeconds(10))
                        {
                            await Task.Delay(wait, ct);
                            continue;
                        }
                        throw new SpotifyException("Spotify is busy. Try again in a moment.");
                    }

                    string message = "";
                    try { using var err = JsonDocument.Parse(body); message = err.RootElement.TryGetProperty("error", out var e) ? Str(e, "message") ?? "" : ""; } catch { }

                    var failure = status switch
                    {
                        403 when message.Contains("registered", StringComparison.OrdinalIgnoreCase) =>
                            new SpotifyException("Your Spotify account isn't on this developer app's user list. Add it under User Management in the Spotify dashboard."),
                        403 when message.Contains("premium", StringComparison.OrdinalIgnoreCase) =>
                            new SpotifyException("This needs Spotify Premium."),
                        403 => new SpotifyException(string.IsNullOrEmpty(message) ? "Spotify didn't allow that." : $"Spotify didn't allow that: {message}"),
                        404 when path.StartsWith("me/player") => new SpotifyException("Open Spotify and play something first, so there's a device to play on."),
                        404 => new SpotifyException("Spotify couldn't find that playlist."),
                        _ => new SpotifyException(string.IsNullOrEmpty(message) ? $"Spotify error ({status})." : $"Spotify error: {message}"),
                    };
                    throw new SpotifyException(failure.Message, status);
                }
            }
        }

        // ----- JSON helpers -----

        private static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string? NextPage(JsonElement root)
        {
            var next = Str(root, "next");
            return string.IsNullOrEmpty(next) ? null : next;
        }

        private static int CountOf(JsonElement playlist)
        {
            // Newer responses: "items": { "total": n }; older: "tracks": { "total": n }
            foreach (var name in new[] { "items", "tracks" })
                if (playlist.TryGetProperty(name, out var obj) && obj.ValueKind == JsonValueKind.Object &&
                    obj.TryGetProperty("total", out var t) && t.TryGetInt32(out var n))
                    return n;
            return 0;
        }

        private static string? FirstImage(JsonElement e)
        {
            if (e.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                foreach (var img in imgs.EnumerateArray())
                {
                    var url = Str(img, "url");
                    if (!string.IsNullOrEmpty(url)) return url;
                }
            return null;
        }

        private static string? SmallestImage(JsonElement e)
        {
            if (!e.TryGetProperty("images", out var imgs) || imgs.ValueKind != JsonValueKind.Array) return null;
            string? best = null;
            int bestW = int.MaxValue;
            foreach (var img in imgs.EnumerateArray())
            {
                var url = Str(img, "url");
                int w = img.TryGetProperty("width", out var wv) && wv.TryGetInt32(out var x) ? x : 1000;
                if (!string.IsNullOrEmpty(url) && w < bestW) { best = url; bestW = w; }
            }
            return best;
        }

        private static string RandomString(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
            var bytes = RandomNumberGenerator.GetBytes(length);
            var sb = new StringBuilder(length);
            foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

        private static string Base64Url(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // =================================================================
        //  Encrypting the stored sign-in token with Windows' per-user data protection (DPAPI)
        // =================================================================

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
            IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        private const int UiForbidden = 0x1;

        private static string Protect(string secret)
        {
            var result = Transform(Encoding.UTF8.GetBytes(secret), protect: true);
            return result == null ? "" : Convert.ToBase64String(result);
        }

        private static string? Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            try
            {
                var result = Transform(Convert.FromBase64String(stored), protect: false);
                return result == null ? null : Encoding.UTF8.GetString(result);
            }
            catch { return null; }
        }

        private static byte[]? Transform(byte[] data, bool protect)
        {
            var input = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
            var output = new DataBlob();
            try
            {
                Marshal.Copy(data, 0, input.Data, data.Length);
                bool ok = protect
                    ? CryptProtectData(ref input, "Kairos", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
                if (!ok || output.Data == IntPtr.Zero) return null;
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(input.Data);
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
    }
}
