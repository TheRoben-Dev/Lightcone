using System.Globalization;
using System.Text.Json;
using Lightcone.Host.Library;
using Lightcone.Host.Plex;

namespace Lightcone.Host.Jellyfin;

/// <summary>
/// One Jellyfin server as a source: its movie, show and music libraries, resume positions and
/// played state from the server, progress reported back while playing, box sets as collections,
/// and intro / credits segments (Jellyfin 10.10+ media segments). Cached on disk like Plex, so
/// it shows at once and offline. Files direct play through mpv; nothing is transcoded.
/// </summary>
public sealed class JellyfinSource : IMediaSource
{
    private const double Ticks = 10_000_000;  // per second

    private readonly JellyfinClient _client;
    private readonly string _cacheFile;
    private readonly object _gate = new();
    private readonly JellyfinServerEntry _entry;
    private IReadOnlyList<MediaItem> _items = [];
    private Dictionary<string, PlexState> _state = new();
    private readonly Dictionary<string, IReadOnlyList<Marker>> _markers = new();
    private readonly Dictionary<string, SeekPreview> _previews = new();
    private readonly Dictionary<string, ItemDetails> _details = new();
    private readonly Dictionary<string, DateTime> _lastReport = new();
    private readonly HashSet<string> _started = new();
    private Task _reports = Task.CompletedTask;

    public JellyfinSource(JellyfinClient client, JellyfinServerEntry entry, string cacheFile)
    {
        _client = client;
        _entry = entry;
        _cacheFile = cacheFile;
        LoadCache();
    }

    public static string SourceIdFor(string serverId) => "jellyfin:" + serverId;

    public string Id => SourceIdFor(_entry.ServerId);
    public string Name => _entry.Name;
    public string Type => "Jellyfin";
    public JellyfinServerEntry Entry => _entry;

    public string Status { get; private set; } = "Connecting…";
    public bool Online { get; private set; }

    public event Action? Changed;

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

    private string Url => _entry.Url;

    private string ServerKey(string itemId) => itemId[(Id.Length + 1)..];

    private bool Mine(MediaItem item) => item.Id.StartsWith(Id + ":", StringComparison.Ordinal);

    // ---- server state ----

    public Progress? ServerProgress(string itemId)
    {
        lock (_gate)
        {
            return _state.TryGetValue(itemId, out var s) && s.Offset is > 0 && !s.Watched
                ? new Progress(s.Offset.Value, _items.FirstOrDefault(i => i.Id == itemId)?.Duration, s.LastViewed ?? DateTime.MinValue)
                : null;
        }
    }

    public DateTime? ServerLastViewed(string itemId)
    {
        lock (_gate)
        {
            return _state.TryGetValue(itemId, out var s) ? s.LastViewed : null;
        }
    }

    public bool? ServerWatched(string itemId)
    {
        lock (_gate)
        {
            return _state.TryGetValue(itemId, out var s) ? s.Watched : null;
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

    public void OnProgress(MediaItem item, double position, double? duration, PlaybackState state)
    {
        if (!Mine(item))
        {
            return;
        }
        var now = DateTime.UtcNow;
        bool first;
        lock (_gate)
        {
            if (state == PlaybackState.Playing && _lastReport.TryGetValue(item.Id, out var last) && now - last < TimeSpan.FromSeconds(10))
            {
                return;
            }
            _lastReport[item.Id] = now;
            first = state != PlaybackState.Stopped && _started.Add(item.Id);
            if (state == PlaybackState.Stopped)
            {
                _started.Remove(item.Id);
            }
            var watched = Lightcone.Playback.ResumePolicy.IsWatched(position, duration);
            var prev = _state.GetValueOrDefault(item.Id);
            _state[item.Id] = new PlexState(watched ? null : position, watched || prev?.Watched == true && position < 1, now);
        }
        var id = ServerKey(item.Id);
        var body = new Dictionary<string, object>
        {
            ["ItemId"] = id,
            ["PositionTicks"] = (long)(position * Ticks),
            ["IsPaused"] = state == PlaybackState.Paused,
            ["PlayMethod"] = "DirectPlay",
            ["CanSeek"] = true,
        };
        var path = state == PlaybackState.Stopped ? "/Sessions/Playing/Stopped" : first ? "/Sessions/Playing" : "/Sessions/Playing/Progress";
        Fire(HttpMethod.Post, path, body, state.ToString().ToLowerInvariant());
        if (first && state == PlaybackState.Paused)
        {
            Fire(HttpMethod.Post, "/Sessions/Playing/Progress", body, "paused");
        }
    }

    public void OnWatched(MediaItem item, bool watched)
    {
        if (!Mine(item))
        {
            return;
        }
        lock (_gate)
        {
            _state[item.Id] = new PlexState(null, watched, DateTime.UtcNow);
        }
        Fire(watched ? HttpMethod.Post : HttpMethod.Delete, $"/Users/{_entry.UserId}/PlayedItems/{ServerKey(item.Id)}", null, watched ? "played" : "unplayed");
        SaveCache();
    }

    /// <summary>Sends in the background, one after another so the server sees them in order.</summary>
    private void Fire(HttpMethod method, string path, object? body, string what)
    {
        lock (_gate)
        {
            _reports = _reports.ContinueWith(async _ =>
            {
                try
                {
                    using var __ = await _client.SendAsync(method, Url + path, _entry.Token, body, timeout: TimeSpan.FromSeconds(15));
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or JellyfinAuthException)
                {
                    AppLog.Warn($"jellyfin {Name}: {what} not sent: {e.Message}");
                }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>Waits for queued reports (tests).</summary>
    public Task Flush()
    {
        lock (_gate)
        {
            return _reports;
        }
    }

    // ---- markers ----

    public async Task PrepareAsync(MediaItem item, CancellationToken ct = default)
    {
        if (!Mine(item) || item.Kind is not (MediaKind.Episode or MediaKind.Movie))
        {
            return;
        }
        try
        {
            using var doc = await _client.GetJsonAsync($"{Url}/MediaSegments/{ServerKey(item.Id)}", _entry.Token, ct, TimeSpan.FromSeconds(10));
            var markers = ParseSegments(doc.RootElement);
            lock (_gate)
            {
                _markers[item.Id] = markers;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or JellyfinAuthException)
        {
            // Older servers (before 10.10) have no media segments.
            AppLog.Info($"jellyfin {Name}: no segments for {item.Id}: {e.Message}");
        }
        try
        {
            var key = ServerKey(item.Id);
            using var doc = await _client.GetJsonAsync($"{Url}/Users/{_entry.UserId}/Items/{key}?Fields=Trickplay", _entry.Token, ct, TimeSpan.FromSeconds(10));
            if (ParseTrickplay(doc.RootElement) is { } t)
            {
                var token = Uri.EscapeDataString(_entry.Token);
                var preview = new TilePreview((i, c) => _client.GetBytesAsync(
                    string.Create(CultureInfo.InvariantCulture, $"{Url}/Videos/{key}/Trickplay/{t.Width}/{i}.jpg?MediaSourceId={t.Source}&api_key={token}"), null, c),
                    t.Width, t.Height, t.Columns, t.Rows, t.Count, t.Interval);
                lock (_gate)
                {
                    _previews[item.Id] = preview;
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or JellyfinAuthException)
        {
            AppLog.Info($"jellyfin {Name}: no trickplay for {item.Id}: {e.Message}");
        }
    }

    internal sealed record Trickplay(string Source, int Width, int Height, int Columns, int Rows, int Count, double Interval);

    /// <summary>The trickplay set nearest 320 px wide (Jellyfin 10.9+), or null.</summary>
    internal static Trickplay? ParseTrickplay(JsonElement m)
    {
        if (!m.TryGetProperty("Trickplay", out var tp) || tp.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        Trickplay? best = null;
        foreach (var source in tp.EnumerateObject())
        {
            if (source.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            foreach (var size in source.Value.EnumerateObject())
            {
                var v = size.Value;
                if (JellyfinClient.Num(v, "Width") is not { } w || JellyfinClient.Num(v, "Height") is not { } h || JellyfinClient.Num(v, "ThumbnailCount") is not { } n)
                {
                    continue;
                }
                var t = new Trickplay(source.Name, (int)w, (int)h, (int)(JellyfinClient.Num(v, "TileWidth") ?? 10), (int)(JellyfinClient.Num(v, "TileHeight") ?? 10),
                    (int)n, (JellyfinClient.Num(v, "Interval") ?? 10000) / 1000);
                if (best == null || Math.Abs(t.Width - 320) < Math.Abs(best.Width - 320))
                {
                    best = t;
                }
            }
        }
        return best;
    }

    public SeekPreview? PreviewOf(string itemId)
    {
        lock (_gate)
        {
            return _previews.GetValueOrDefault(itemId);
        }
    }

    // ---- cast, crew and extras ----

    public async Task<ItemDetails?> DetailsAsync(MediaItem item, CancellationToken ct = default)
    {
        if (!Mine(item) || item.Kind is not (MediaKind.Movie or MediaKind.Show))
        {
            return null;
        }
        lock (_gate)
        {
            if (_details.TryGetValue(item.Id, out var cached))
            {
                return cached;
            }
        }
        var key = ServerKey(item.Id);
        var user = _entry.UserId;
        using var doc = await _client.GetJsonAsync($"{Url}/Users/{user}/Items/{key}", _entry.Token, ct, TimeSpan.FromSeconds(15));
        var people = ParsePeople(doc.RootElement);
        var extras = new List<Extra>();
        foreach (var path in new[] { "LocalTrailers", "SpecialFeatures" })
        {
            try
            {
                using var list = await _client.GetJsonAsync($"{Url}/Users/{user}/Items/{key}/{path}", _entry.Token, ct, TimeSpan.FromSeconds(15));
                extras.AddRange(ParseExtras(list.RootElement, path == "LocalTrailers"));
            }
            catch (Exception e) when (e is HttpRequestException or JsonException)
            {
                AppLog.Info($"jellyfin {Name}: no {path} for {item.Id}: {e.Message}");
            }
        }
        var details = new ItemDetails(people, extras.DistinctBy(e => e.PlayUrl).OrderBy(e => e.IsTrailer ? 0 : 1).ToList());
        lock (_gate)
        {
            _details[item.Id] = details;
        }
        return details;
    }

    internal IReadOnlyList<Person> ParsePeople(JsonElement m)
    {
        var people = new List<Person>();
        foreach (var p in Array(m, "People"))
        {
            if (JellyfinClient.Str(p, "Name") is not { } name || JellyfinClient.Str(p, "Id") is not { } pid)
            {
                continue;
            }
            var job = JellyfinClient.Str(p, "Type") switch
            {
                "Actor" or "GuestStar" => Person.Actor,
                "Director" => Person.Director,
                "Writer" => Person.Writer,
                _ => null,
            };
            if (job == null)
            {
                continue;
            }
            var tag = JellyfinClient.Str(p, "PrimaryImageTag");
            var photo = tag == null ? null : $"{Url}/Items/{pid}/Images/Primary?fillWidth=300&fillHeight=300&quality=90&tag={Uri.EscapeDataString(tag)}";
            people.Add(new Person(name, job == Person.Actor ? JellyfinClient.Str(p, "Role") : null, job, photo, Id, pid));
        }
        // Cast first (as the server orders them), then directors and writers.
        return people.Where(p => p.Job == Person.Actor).Take(30)
            .Concat(people.Where(p => p.Job == Person.Director).Take(4))
            .Concat(people.Where(p => p.Job == Person.Writer).Take(4)).ToList();
    }

    internal IReadOnlyList<Extra> ParseExtras(JsonElement root, bool trailers)
    {
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : Array(root, "Items");
        var token = Uri.EscapeDataString(_entry.Token);
        var list = new List<Extra>();
        foreach (var x in items)
        {
            if (JellyfinClient.Str(x, "Id") is not { } id || JellyfinClient.Str(x, "Name") is not { } name)
            {
                continue;
            }
            var kind = trailers ? "Trailer" : ItemDetails.ExtraKind(JellyfinClient.Str(x, "ExtraType"));
            var tag = PrimaryTag(x);
            var thumb = tag == null ? null : $"{Url}/Items/{id}/Images/Primary?fillWidth=640&fillHeight=360&quality=90&tag={Uri.EscapeDataString(tag)}";
            var duration = JellyfinClient.Num(x, "RunTimeTicks") is { } t ? t / Ticks : (double?)null;
            list.Add(new Extra(name, kind, duration, $"{Url}/Videos/{id}/stream?static=true&api_key={token}", thumb));
        }
        return list;
    }

    public async Task<IReadOnlyList<string>> TitlesWithAsync(Person person, CancellationToken ct = default)
    {
        if (person.SourceId != Id)
        {
            return [];
        }
        using var doc = await _client.GetJsonAsync(
            $"{Url}/Users/{_entry.UserId}/Items?PersonIds={Uri.EscapeDataString(person.Key)}&Recursive=true&IncludeItemTypes=Movie,Series", _entry.Token, ct, TimeSpan.FromSeconds(20));
        return Array(doc.RootElement, "Items").Select(x => JellyfinClient.Str(x, "Id")).OfType<string>().Select(id => $"{Id}:{id}").ToList();
    }

    internal static IReadOnlyList<Marker> ParseSegments(JsonElement root)
    {
        var list = new List<Marker>();
        if (root.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in items.EnumerateArray())
            {
                var kind = JellyfinClient.Str(x, "Type") switch { "Intro" => Marker.Intro, "Outro" => Marker.Credits, _ => null };
                if (kind == null || JellyfinClient.Num(x, "StartTicks") is not { } s || JellyfinClient.Num(x, "EndTicks") is not { } e || e <= s)
                {
                    continue;
                }
                list.Add(new Marker(kind, s / Ticks, e / Ticks));
            }
        }
        return list;
    }

    // ---- refresh ----

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var (items, state) = await FetchAsync(ct);
            lock (_gate)
            {
                _items = items;
                _state = state;
            }
            Online = true;
            Status = "Connected";
            SaveCache();
            AppLog.Info($"jellyfin {Name}: {items.Count} items");
        }
        catch (JellyfinAuthException e)
        {
            Online = false;
            Status = e.Message;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Online = false;
            Status = Items.Count > 0 ? "Offline, showing what was saved" : $"Couldn't load: {e.Message}";
            AppLog.Warn($"jellyfin {Name}: refresh failed: {e.Message}");
        }
        Changed?.Invoke();
    }

    private const string Fields = "Overview,Genres,DateCreated,OfficialRating,CommunityRating,ProviderIds,RecursiveItemCount";

    private async Task<(IReadOnlyList<MediaItem>, Dictionary<string, PlexState>)> FetchAsync(CancellationToken ct)
    {
        var user = _entry.UserId;
        var raw = new List<JsonElement>();
        var docs = new List<JsonDocument>();
        try
        {
            var views = await _client.GetJsonAsync($"{Url}/Users/{user}/Views", _entry.Token, ct, TimeSpan.FromSeconds(20));
            docs.Add(views);
            foreach (var v in Array(views.RootElement, "Items"))
            {
                var types = JellyfinClient.Str(v, "CollectionType") switch
                {
                    "movies" => "Movie",
                    "tvshows" => "Series,Episode",
                    "music" => "MusicAlbum,Audio",
                    null or "mixed" or "homevideos" => "Movie,Series,Episode",
                    _ => null,
                };
                if (types == null || JellyfinClient.Str(v, "Id") is not { } viewId)
                {
                    continue;
                }
                var list = await _client.GetJsonAsync(
                    $"{Url}/Users/{user}/Items?ParentId={viewId}&Recursive=true&IncludeItemTypes={types}&Fields={Fields}&EnableUserData=true&EnableImageTypes=Primary&ImageTypeLimit=1",
                    _entry.Token, ct, TimeSpan.FromMinutes(2));
                docs.Add(list);
                raw.AddRange(Array(list.RootElement, "Items"));
            }
            // Box sets → each title's collections.
            var collections = new Dictionary<string, List<string>>();
            var sets = await _client.GetJsonAsync($"{Url}/Users/{user}/Items?IncludeItemTypes=BoxSet&Recursive=true", _entry.Token, ct, TimeSpan.FromSeconds(30));
            docs.Add(sets);
            foreach (var set in Array(sets.RootElement, "Items").Take(300))
            {
                if (JellyfinClient.Str(set, "Id") is not { } setId || JellyfinClient.Str(set, "Name") is not { } setName)
                {
                    continue;
                }
                using var members = await _client.GetJsonAsync($"{Url}/Users/{user}/Items?ParentId={setId}", _entry.Token, ct, TimeSpan.FromSeconds(20));
                foreach (var m in Array(members.RootElement, "Items"))
                {
                    if (JellyfinClient.Str(m, "Id") is { } mid)
                    {
                        (collections.TryGetValue(mid, out var l) ? l : collections[mid] = []).Add(setName);
                    }
                }
            }
            var items = new List<MediaItem>();
            var state = new Dictionary<string, PlexState>();
            foreach (var m in raw)
            {
                if (Map(m) is not { } item)
                {
                    continue;
                }
                var key = ServerKey(item.Id);
                if (collections.TryGetValue(key, out var names))
                {
                    item = item with { Collections = names };
                }
                if (items.Any(i => i.Id == item.Id))
                {
                    continue;  // in two libraries
                }
                items.Add(item);
                state[item.Id] = StateOf(m);
            }
            return (items, state);
        }
        finally
        {
            foreach (var d in docs)
            {
                d.Dispose();
            }
        }
    }

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    internal static PlexState StateOf(JsonElement m)
    {
        if (!m.TryGetProperty("UserData", out var u))
        {
            return new PlexState(null, false, null);
        }
        var played = JellyfinClient.Bool(u, "Played");
        var pos = JellyfinClient.Num(u, "PlaybackPositionTicks") is { } t and > 0 ? t / Ticks : (double?)null;
        DateTime? last = JellyfinClient.Str(u, "LastPlayedDate") is { } d && DateTime.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
        return new PlexState(played ? null : pos, played, last);
    }

    internal MediaItem? Map(JsonElement m)
    {
        var type = JellyfinClient.Str(m, "Type");
        MediaKind? kind = type switch
        {
            "Movie" => MediaKind.Movie,
            "Series" => MediaKind.Show,
            "Episode" => MediaKind.Episode,
            "MusicAlbum" => MediaKind.Album,
            "Audio" => MediaKind.Track,
            _ => null,
        };
        if (kind == null || JellyfinClient.Str(m, "Id") is not { } id || JellyfinClient.Str(m, "Name") is not { } title)
        {
            return null;
        }
        var year = JellyfinClient.Num(m, "ProductionYear") is { } y ? (int)y : (int?)null;
        var genres = m.TryGetProperty("Genres", out var gs) && gs.ValueKind == JsonValueKind.Array
            ? gs.EnumerateArray().Select(g => g.GetString()).OfType<string>().Take(4).ToList()
            : [];
        var duration = JellyfinClient.Num(m, "RunTimeTicks") is { } rt and > 0 ? rt / Ticks : (double?)null;
        var added = JellyfinClient.Str(m, "DateCreated") is { } dc && DateTime.TryParse(dc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var a) ? a : DateTime.MinValue;
        int? index = JellyfinClient.Num(m, "IndexNumber") is { } ix ? (int)ix : null;
        int? season = null;
        string? parent = null, parentTitle = null, subtitle = null, seasonTitle = null, seasonPoster = null;
        switch (kind)
        {
            case MediaKind.Movie:
                subtitle = string.Join(" · ", new[] { year?.ToString(CultureInfo.InvariantCulture), genres.FirstOrDefault() }.OfType<string>());
                break;
            case MediaKind.Show:
                subtitle = JellyfinClient.Num(m, "RecursiveItemCount") is { } n and > 0 ? $"{n:0} episode{(n == 1 ? "" : "s")}" : year?.ToString(CultureInfo.InvariantCulture);
                break;
            case MediaKind.Episode:
                parent = JellyfinClient.Str(m, "SeriesId") is { } sid ? $"{Id}:{sid}" : null;
                parentTitle = JellyfinClient.Str(m, "SeriesName");
                season = JellyfinClient.Num(m, "ParentIndexNumber") is { } si ? (int)si : null;
                seasonTitle = JellyfinClient.Str(m, "SeasonName");
                // (No image tag on an episode for its season: a season without a poster 404s and the show's poster is used.)
                seasonPoster = JellyfinClient.Str(m, "SeasonId") is { } seasonId
                    ? $"{Url}/Items/{seasonId}/Images/Primary?fillWidth=400&fillHeight=600&quality=90"
                    : null;
                subtitle = string.Join(" · ", new[] { parentTitle, season is { } s ? $"S{s}" : null, index is { } e ? $"E{e}" : null }.OfType<string>());
                break;
            case MediaKind.Album:
                parentTitle = JellyfinClient.Str(m, "AlbumArtist");
                subtitle = parentTitle;
                break;
            case MediaKind.Track:
                parent = JellyfinClient.Str(m, "AlbumId") is { } al ? $"{Id}:{al}" : null;
                parentTitle = JellyfinClient.Str(m, "AlbumArtist");
                subtitle = parentTitle;
                break;
        }
        var token = Uri.EscapeDataString(_entry.Token);
        string? play = kind switch
        {
            MediaKind.Movie or MediaKind.Episode => $"{Url}/Videos/{id}/stream?static=true&api_key={token}",
            MediaKind.Track => $"{Url}/Audio/{id}/stream?static=true&api_key={token}",
            _ => null,
        };
        var (imageId, tag) = kind switch
        {
            MediaKind.Track => (JellyfinClient.Str(m, "AlbumId"), JellyfinClient.Str(m, "AlbumPrimaryImageTag")),
            _ => (id, PrimaryTag(m)),
        };
        if (kind == MediaKind.Episode && tag == null)
        {
            (imageId, tag) = (JellyfinClient.Str(m, "SeriesId"), JellyfinClient.Str(m, "SeriesPrimaryImageTag"));
        }
        var (w, h) = kind switch { MediaKind.Episode => (640, 360), MediaKind.Album or MediaKind.Track => (500, 500), _ => (400, 600) };
        var poster = imageId != null && tag != null
            ? string.Create(CultureInfo.InvariantCulture, $"{Url}/Items/{imageId}/Images/Primary?fillWidth={w}&fillHeight={h}&quality=90&tag={Uri.EscapeDataString(tag)}")
            : null;
        var rating = JellyfinClient.Num(m, "CommunityRating");
        return new MediaItem
        {
            Id = $"{Id}:{id}",
            Title = title,
            Kind = kind.Value,
            Subtitle = subtitle is { Length: > 0 } ? subtitle : null,
            Year = year,
            Overview = JellyfinClient.Str(m, "Overview") is { Length: > 0 } o ? o : null,
            Genres = genres,
            Duration = duration,
            Added = added,
            SourceId = Id,
            SourceName = Name,
            PlayUrl = play,
            PosterUrl = poster,
            ParentId = parent,
            ParentTitle = parentTitle,
            Index = index,
            Season = season,
            SeasonTitle = seasonTitle is { Length: > 0 } ? seasonTitle : null,
            SeasonPosterUrl = seasonPoster,
            ContentRating = JellyfinClient.Str(m, "OfficialRating") is { Length: > 0 } cr ? cr : null,
            Rating = rating is > 0 and <= 10 ? Math.Round(rating.Value, 1) : null,
        };
    }

    private static string? PrimaryTag(JsonElement m) =>
        m.TryGetProperty("ImageTags", out var tags) && tags.ValueKind == JsonValueKind.Object ? JellyfinClient.Str(tags, "Primary") : null;

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
            AppLog.Warn($"jellyfin {Name}: cache unreadable: {e.Message}");
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
            AppLog.Warn($"jellyfin {Name}: couldn't save cache: {e.Message}");
        }
    }

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
