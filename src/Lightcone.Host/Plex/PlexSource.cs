using System.Globalization;
using System.Text.Json;
using Lightcone.Host.Library;

namespace Lightcone.Host.Plex;

/// <summary>What the server knows about an item's playback (from any device).</summary>
public sealed record PlexState(double? Offset, bool Watched, DateTime? LastViewed);

/// <summary>
/// One Plex Media Server as a source: its movie, show and music libraries, continue-watching
/// positions and watched state from the server, progress reported back while playing, and
/// intro / credits markers. Items are cached on disk, so the library shows instantly at
/// startup (and offline) while a refresh runs in the background. Files play directly
/// (direct play through mpv); nothing is transcoded.
/// </summary>
public sealed class PlexSource : IMediaSource
{
    private readonly PlexClient _client;
    private readonly string _cacheFile;
    private readonly object _gate = new();
    private PlexServerEntry _entry;
    private IReadOnlyList<MediaItem> _items = [];
    private Dictionary<string, PlexState> _state = new();
    private readonly Dictionary<string, IReadOnlyList<Marker>> _markers = new();
    private readonly Dictionary<string, DateTime> _lastReport = new();

    public PlexSource(PlexClient client, PlexServerEntry entry, string cacheFile)
    {
        _client = client;
        _entry = entry;
        _cacheFile = cacheFile;
        LoadCache();
    }

    public string Id => SourceIdFor(_entry.MachineId);
    public string Name => _entry.Name;
    public string Type => "Plex";
    public PlexServerEntry Entry => _entry;

    public static string SourceIdFor(string machineId) => "plex:" + machineId;

    /// <summary>"Connected", "Offline (showing what was cached)", or what went wrong.</summary>
    public string Status { get; private set; } = "Connecting…";

    public bool Online { get; private set; }

    /// <summary>Raised after a refresh (with the entry, whose last working address may have changed).</summary>
    public event Action? Changed;

    public event Action<PlexServerEntry>? EntryChanged;

    public IReadOnlyList<MediaItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items;
            }
        }
    }

    private string? Base => _entry.LastUri;

    private string RatingKey(string itemId) => itemId[(Id.Length + 1)..];

    // ---- server state ----

    public Progress? ServerProgress(string itemId)
    {
        lock (_gate)
        {
            return _state.TryGetValue(itemId, out var s) && s.Offset is > 0 && !s.Watched
                ? new Progress(s.Offset.Value, Items.FirstOrDefault(i => i.Id == itemId)?.Duration, s.LastViewed ?? DateTime.MinValue)
                : null;
        }
    }

    public bool? ServerWatched(string itemId)
    {
        lock (_gate)
        {
            return _state.TryGetValue(itemId, out var s) ? s.Watched : null;
        }
    }

    /// <summary>The account's watchlist on plex.tv (shared by every server of the account).</summary>
    public PlexWatchlist? Watchlist { get; set; }

    public bool? ServerWatchlist(MediaItem item) =>
        item.Kind is MediaKind.Movie or MediaKind.Show ? Watchlist?.Has(item.Guid) : null;

    public void OnWatchlist(MediaItem item, bool on)
    {
        if (item.Guid is { } g && item.Kind is MediaKind.Movie or MediaKind.Show)
        {
            Watchlist?.Set(g, on);
        }
    }

    public IReadOnlyList<Marker> MarkersOf(string itemId)
    {
        lock (_gate)
        {
            return _markers.GetValueOrDefault(itemId) ?? [];
        }
    }

    // ---- reporting ----

    /// <summary>Tells the server where playback is (its "timeline"), at most every 10 s while playing.</summary>
    public void OnProgress(MediaItem item, double position, double? duration, PlaybackState state)
    {
        if (Base is not { } b || !item.Id.StartsWith(Id + ":", StringComparison.Ordinal))
        {
            return;
        }
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (state == PlaybackState.Playing && _lastReport.TryGetValue(item.Id, out var last) && now - last < TimeSpan.FromSeconds(10))
            {
                return;
            }
            _lastReport[item.Id] = now;
            var watched = Lightcone.Playback.ResumePolicy.IsWatched(position, duration);
            var prev = _state.GetValueOrDefault(item.Id);
            _state[item.Id] = new PlexState(watched ? null : position, watched || prev?.Watched == true && position < 1, now);
        }
        var rk = RatingKey(item.Id);
        var s = state switch { PlaybackState.Paused => "paused", PlaybackState.Stopped => "stopped", _ => "playing" };
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{b}/:/timeline?ratingKey={rk}&key=%2Flibrary%2Fmetadata%2F{rk}&state={s}&time={(long)(position * 1000)}&duration={(long)((duration ?? 0) * 1000)}");
        Fire(url, $"timeline {s}");
        if (state == PlaybackState.Stopped && Lightcone.Playback.ResumePolicy.IsWatched(position, duration))
        {
            Fire($"{b}/:/scrobble?identifier=com.plexapp.plugins.library&key={rk}", "scrobble");
        }
    }

    public void OnWatched(MediaItem item, bool watched)
    {
        if (Base is not { } b)
        {
            return;
        }
        lock (_gate)
        {
            _state[item.Id] = new PlexState(null, watched, DateTime.UtcNow);
        }
        var rk = RatingKey(item.Id);
        Fire($"{b}/:/{(watched ? "scrobble" : "unscrobble")}?identifier=com.plexapp.plugins.library&key={rk}", watched ? "scrobble" : "unscrobble");
        SaveCache();
    }

    private Task _reports = Task.CompletedTask;

    /// <summary>Sends a report in the background, one after another so the server sees them in order.</summary>
    private void Fire(string url, string what)
    {
        lock (_gate)
        {
            _reports = _reports.ContinueWith(async _ =>
            {
                try
                {
                    await _client.SendAsync(HttpMethod.Get, url, _entry.AccessToken);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or PlexAuthException)
                {
                    AppLog.Warn($"plex {Name}: {what} not sent: {e.Message}");
                }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    // ---- markers ----

    public async Task PrepareAsync(MediaItem item, CancellationToken ct = default)
    {
        if (Base is not { } b || item.Kind != MediaKind.Episode && item.Kind != MediaKind.Movie)
        {
            return;
        }
        try
        {
            using var doc = await _client.GetJsonAsync($"{b}/library/metadata/{RatingKey(item.Id)}?includeMarkers=1", _entry.AccessToken, ct, TimeSpan.FromSeconds(10));
            var markers = Metadata(doc.RootElement).Select(ParseMarkers).FirstOrDefault() ?? [];
            lock (_gate)
            {
                _markers[item.Id] = markers;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or PlexAuthException)
        {
            AppLog.Warn($"plex {Name}: no markers for {item.Id}: {e.Message}");
        }
    }

    internal static IReadOnlyList<Marker> ParseMarkers(JsonElement m)
    {
        var list = new List<Marker>();
        if (m.TryGetProperty("Marker", out var ms) && ms.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in ms.EnumerateArray())
            {
                var kind = PlexClient.Str(x, "type");
                if (kind is not (Marker.Intro or Marker.Credits) || PlexClient.Num(x, "startTimeOffset") is not { } s || PlexClient.Num(x, "endTimeOffset") is not { } e || e <= s)
                {
                    continue;
                }
                list.Add(new Marker(kind, s / 1000, e / 1000));
            }
        }
        return list;
    }

    // ---- refresh ----

    /// <summary>Reaches the server (last working address first), then reads every movie, show and music library.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var uri = await ReachAsync(ct);
            if (uri == null)
            {
                Online = false;
                Status = Items.Count > 0 ? "Offline, showing what was saved" : "Can't reach this server";
                Changed?.Invoke();
                return;
            }
            var (items, state) = await FetchAsync(uri, ct);
            lock (_gate)
            {
                _items = items;
                _state = state;
            }
            Online = true;
            Status = "Connected";
            SaveCache();
            AppLog.Info($"plex {Name}: {items.Count} items via {(Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.Host : "?")}");
        }
        catch (PlexAuthException e)
        {
            Online = false;
            Status = e.Message;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Online = false;
            Status = Items.Count > 0 ? "Offline, showing what was saved" : $"Couldn't load: {e.Message}";
            AppLog.Warn($"plex {Name}: refresh failed: {e.Message}");
        }
        Changed?.Invoke();
    }

    private async Task<string?> ReachAsync(CancellationToken ct)
    {
        if (_entry.LastUri is { } last)
        {
            try
            {
                using var _ = await _client.GetJsonAsync($"{last}/identity", _entry.AccessToken, ct, TimeSpan.FromSeconds(4));
                return last;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Moved networks: try every address again.
            }
        }
        var uri = await _client.ReachAsync(new PlexServer(_entry.MachineId, _entry.Name, _entry.AccessToken, true, _entry.Connections), ct);
        if (uri != null && uri != _entry.LastUri)
        {
            _entry = _entry with { LastUri = uri };
            EntryChanged?.Invoke(_entry);
        }
        return uri;
    }

    private async Task<(IReadOnlyList<MediaItem>, Dictionary<string, PlexState>)> FetchAsync(string uri, CancellationToken ct)
    {
        var token = _entry.AccessToken;
        var items = new List<MediaItem>();
        var state = new Dictionary<string, PlexState>();
        using var sections = await _client.GetJsonAsync($"{uri}/library/sections", token, ct);
        foreach (var d in Directories(sections.RootElement))
        {
            var key = PlexClient.Str(d, "key");
            var type = PlexClient.Str(d, "type");
            // Plex list types: 1 movie, 2 show, 4 episode, 9 album, 10 track.
            int[] kinds = type switch { "movie" => [1], "show" => [2, 4], "artist" => [9, 10], _ => [] };
            foreach (var k in kinds)
            {
                using var list = await _client.GetJsonAsync($"{uri}/library/sections/{key}/all?type={k}&includeGuids=0", token, ct, TimeSpan.FromMinutes(2));
                foreach (var m in Metadata(list.RootElement))
                {
                    if (Map(m, uri, token) is { } item)
                    {
                        items.Add(item);
                        state[item.Id] = StateOf(m);
                    }
                }
            }
        }
        return (items, state);
    }

    internal static IEnumerable<JsonElement> Directories(JsonElement root) => Children(root, "Directory");

    internal static IEnumerable<JsonElement> Metadata(JsonElement root) => Children(root, "Metadata");

    private static IEnumerable<JsonElement> Children(JsonElement root, string name) =>
        root.TryGetProperty("MediaContainer", out var mc) && mc.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray()
            : [];

    internal static PlexState StateOf(JsonElement m)
    {
        var offset = PlexClient.Num(m, "viewOffset") is { } o ? o / 1000 : (double?)null;
        var watched = PlexClient.Num(m, "viewCount") is > 0 && offset == null;
        if (PlexClient.Str(m, "type") == "show")
        {
            watched = PlexClient.Num(m, "leafCount") is { } leaves && leaves > 0 && PlexClient.Num(m, "viewedLeafCount") == leaves;
        }
        var last = PlexClient.Num(m, "lastViewedAt") is { } t ? DateTimeOffset.FromUnixTimeSeconds((long)t).UtcDateTime : (DateTime?)null;
        return new PlexState(offset, watched, last);
    }

    /// <summary>A Plex metadata entry as a Lightcone item, or null for kinds Lightcone doesn't show.</summary>
    internal MediaItem? Map(JsonElement m, string uri, string token)
    {
        var rk = PlexClient.Str(m, "ratingKey");
        var title = PlexClient.Str(m, "title");
        if (rk == null || title == null)
        {
            return null;
        }
        var type = PlexClient.Str(m, "type");
        MediaKind? kind = type switch
        {
            "movie" => MediaKind.Movie,
            "show" => MediaKind.Show,
            "episode" => MediaKind.Episode,
            "album" => MediaKind.Album,
            "track" => MediaKind.Track,
            _ => null,
        };
        if (kind == null)
        {
            return null;
        }
        var year = PlexClient.Num(m, "year") is { } y ? (int)y : (int?)null;
        var genres = new List<string>();
        if (m.TryGetProperty("Genre", out var gs) && gs.ValueKind == JsonValueKind.Array)
        {
            genres.AddRange(gs.EnumerateArray().Select(g => PlexClient.Str(g, "tag")).OfType<string>().Take(4));
        }
        var collections = new List<string>();
        if (m.TryGetProperty("Collection", out var cs) && cs.ValueKind == JsonValueKind.Array)
        {
            collections.AddRange(cs.EnumerateArray().Select(c => PlexClient.Str(c, "tag")).OfType<string>());
        }
        var rating = PlexClient.Num(m, "audienceRating") ?? PlexClient.Num(m, "rating");
        var duration = PlexClient.Num(m, "duration") is { } ms ? ms / 1000 : (double?)null;
        var added = PlexClient.Num(m, "addedAt") is { } a ? DateTimeOffset.FromUnixTimeSeconds((long)a).UtcDateTime : DateTime.MinValue;
        string? parent = null, parentTitle = null, subtitle = null;
        int? index = PlexClient.Num(m, "index") is { } ix ? (int)ix : null;
        int? season = null;
        string? seasonTitle = null, seasonThumb = null;
        switch (kind)
        {
            case MediaKind.Movie:
                subtitle = string.Join(" · ", new[] { year?.ToString(CultureInfo.InvariantCulture), genres.FirstOrDefault() }.OfType<string>());
                break;
            case MediaKind.Show:
                subtitle = PlexClient.Num(m, "leafCount") is { } n ? $"{n:0} episode{(n == 1 ? "" : "s")}" : null;
                break;
            case MediaKind.Episode:
                parent = PlexClient.Str(m, "grandparentRatingKey") is { } g ? $"{Id}:{g}" : null;
                parentTitle = PlexClient.Str(m, "grandparentTitle");
                season = PlexClient.Num(m, "parentIndex") is { } si ? (int)si : null;
                seasonTitle = PlexClient.Str(m, "parentTitle");
                seasonThumb = PlexClient.Str(m, "parentThumb");
                subtitle = string.Join(" · ", new[] { parentTitle, season is { } s ? $"S{s}" : null, index is { } e ? $"E{e}" : null }.OfType<string>());
                break;
            case MediaKind.Album:
                parentTitle = PlexClient.Str(m, "parentTitle");
                subtitle = parentTitle;
                break;
            case MediaKind.Track:
                parent = PlexClient.Str(m, "parentRatingKey") is { } p ? $"{Id}:{p}" : null;
                parentTitle = PlexClient.Str(m, "grandparentTitle");
                subtitle = PlexClient.Str(m, "originalTitle") ?? parentTitle;
                break;
        }
        string? play = null;
        if (kind is MediaKind.Movie or MediaKind.Episode or MediaKind.Track && FirstPart(m) is { } part)
        {
            play = $"{uri}{part}?X-Plex-Token={Uri.EscapeDataString(token)}";
        }
        var thumb = kind == MediaKind.Episode ? PlexClient.Str(m, "thumb") ?? PlexClient.Str(m, "grandparentThumb") : PlexClient.Str(m, "thumb");
        if (kind == MediaKind.Track)
        {
            thumb = PlexClient.Str(m, "parentThumb") ?? thumb;
        }
        var (w, h) = kind switch { MediaKind.Episode => (640, 360), MediaKind.Album or MediaKind.Track => (500, 500), _ => (400, 600) };
        return new MediaItem
        {
            Id = $"{Id}:{rk}",
            Title = title,
            Kind = kind.Value,
            Subtitle = subtitle is { Length: > 0 } ? subtitle : null,
            Year = year,
            Overview = PlexClient.Str(m, "summary") is { Length: > 0 } sum ? sum : null,
            Genres = genres,
            Duration = duration,
            Added = added,
            SourceId = Id,
            SourceName = Name,
            PlayUrl = play,
            PosterUrl = thumb == null ? null : PhotoUrl(uri, token, thumb, w, h),
            ParentId = parent,
            ParentTitle = parentTitle,
            Index = index,
            Season = season,
            SeasonTitle = seasonTitle is { Length: > 0 } ? seasonTitle : null,
            SeasonPosterUrl = seasonThumb == null ? null : PhotoUrl(uri, token, seasonThumb, 400, 600),
            Guid = PlexClient.Str(m, "guid"),
            Collections = collections,
            ContentRating = PlexClient.Str(m, "contentRating") is { Length: > 0 } cr ? cr : null,
            Rating = rating is > 0 and <= 10 ? Math.Round(rating.Value, 1) : null,
        };
    }

    private static string? FirstPart(JsonElement m) =>
        m.TryGetProperty("Media", out var media) && media.ValueKind == JsonValueKind.Array && media.GetArrayLength() > 0 &&
        media[0].TryGetProperty("Part", out var parts) && parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0
            ? PlexClient.Str(parts[0], "key")
            : null;

    /// <summary>Artwork scaled by the server (small downloads, cached by URL).</summary>
    public static string PhotoUrl(string uri, string token, string thumb, int w, int h) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{uri}/photo/:/transcode?width={w}&height={h}&minSize=1&upscale=1&url={Uri.EscapeDataString(thumb)}&X-Plex-Token={Uri.EscapeDataString(token)}");

    // ---- cache ----

    private sealed record CacheModel(List<MediaItem> Items, Dictionary<string, PlexState> State);

    private void LoadCache()
    {
        try
        {
            if (File.Exists(_cacheFile) && JsonSerializer.Deserialize<CacheModel>(File.ReadAllText(_cacheFile)) is { } c)
            {
                _items = c.Items;
                _state = c.State;
                Status = "Offline, showing what was saved";
            }
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            AppLog.Warn($"plex {Name}: cache unreadable: {e.Message}");
        }
    }

    private void SaveCache()
    {
        try
        {
            CacheModel model;
            lock (_gate)
            {
                model = new CacheModel(_items.ToList(), new Dictionary<string, PlexState>(_state));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var tmp = _cacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(model));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);  // play URLs carry the token
            }
            File.Move(tmp, _cacheFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"plex {Name}: couldn't save cache: {e.Message}");
        }
    }

    /// <summary>Deletes the cached items (on sign-out or removing the server).</summary>
    public void DeleteCache()
    {
        try
        {
            File.Delete(_cacheFile);
        }
        catch (IOException)
        {
        }
    }
}
