using System.Text.Json;
using Lightcone.Host.Library;

namespace Lightcone.Host.Plex;

/// <summary>
/// The Plex account's watchlist (it lives on plex.tv, not on a server, so it's the same in every
/// Plex app). Items are matched by their global guid ("plex://movie/5d77…"); the last segment is
/// the key plex.tv's watchlist uses. Titles your servers don't have are kept too (title, year,
/// poster, summary), so they can be shown and taken off. Kept on disk so it shows offline.
/// </summary>
public sealed class PlexWatchlist : IOnlineWatchlist
{
    public const string Discover = "https://discover.provider.plex.tv";

    private readonly PlexClient _client;
    private readonly string _cacheFile;
    private readonly object _gate = new();
    private List<WatchlistEntry> _entries = new();
    private HashSet<string> _keys = new();
    private Task _sends = Task.CompletedTask;

    private sealed record CacheModel(List<WatchlistEntry>? Entries, DateTime? SyncedAt);

    public PlexWatchlist(PlexClient client, string cacheFile)
    {
        _client = client;
        _cacheFile = cacheFile;
        try
        {
            if (File.Exists(cacheFile))
            {
                var text = File.ReadAllText(cacheFile);
                if (text.TrimStart().StartsWith('['))
                {
                    // 0.4–0.10 kept only the keys.
                    var keys = JsonSerializer.Deserialize<List<string>>(text) ?? [];
                    _entries = keys.Select(k => new WatchlistEntry(k, "", "", MediaKind.Movie)).ToList();
                }
                else if (JsonSerializer.Deserialize<CacheModel>(text) is { } model)
                {
                    _entries = model.Entries ?? [];
                    SyncedAt = model.SyncedAt;
                }
                _keys = _entries.Select(e => e.Key).ToHashSet();
                Known = true;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            AppLog.Warn($"plex watchlist cache unreadable: {e.Message}");
        }
    }

    /// <summary>The token to use (null while signed out: nothing is synced).</summary>
    public Func<string?> Token { get; set; } = () => null;

    /// <summary>True once the watchlist has been loaded (now or in an earlier session).</summary>
    public bool Known { get; private set; }

    public bool Active => Known && Token() != null;

    public string Name => "Plex";

    /// <summary>When it was last read from plex.tv and this device's additions sent (any session).</summary>
    public DateTime? SyncedAt { get; private set; }

    /// <summary>When it was last read from plex.tv in this session.</summary>
    public DateTime LastRefresh { get; private set; } = DateTime.MinValue;

    public event Action? Changed;

    public IReadOnlyList<WatchlistEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    /// <summary>"plex://movie/5d776825880197001ec967c6" → "5d776825880197001ec967c6"; null for other guids.</summary>
    public static string? KeyOf(string? guid) =>
        guid != null && guid.StartsWith("plex://", StringComparison.Ordinal) && guid.LastIndexOf('/') is var i and > 7 ? guid[(i + 1)..] : null;

    /// <summary>On the watchlist, or null when that isn't known (no Plex guid, never loaded, signed out).</summary>
    public bool? Has(string? guid)
    {
        if (KeyOf(guid) is not { } key || !Known || Token() == null)
        {
            return null;
        }
        lock (_gate)
        {
            return _keys.Contains(key);
        }
    }

    /// <summary>Reads the watchlist from plex.tv. False when it couldn't (signed out, offline).</summary>
    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        if (Token() is not { } token)
        {
            return false;
        }
        try
        {
            var entries = new List<WatchlistEntry>();
            const int page = 100;
            for (var start = 0; start < 5000; start += page)
            {
                using var doc = await _client.GetJsonAsync(
                    $"{Discover}/library/sections/watchlist/all?includeFields=guid,title,type,year,thumb,summary,contentRating,duration,ratingKey" +
                    $"&sort=watchlistedAt:desc&X-Plex-Container-Start={start}&X-Plex-Container-Size={page}",
                    token, ct, TimeSpan.FromSeconds(20));
                var count = 0;
                if (doc.RootElement.TryGetProperty("MediaContainer", out var mc) && mc.TryGetProperty("Metadata", out var md) && md.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in md.EnumerateArray())
                    {
                        count++;
                        if (ParseEntry(m) is { } e)
                        {
                            entries.Add(e);
                        }
                    }
                }
                var total = PlexClient.Num(mc, "totalSize") ?? PlexClient.Num(mc, "size") ?? count;
                if (count < page || start + count >= total)
                {
                    break;
                }
            }
            lock (_gate)
            {
                _entries = entries.DistinctBy(e => e.Key).ToList();
                _keys = _entries.Select(e => e.Key).ToHashSet();
                Known = true;
                LastRefresh = DateTime.UtcNow;
            }
            Save();
            AppLog.Info($"plex: watchlist has {entries.Count} titles");
            Changed?.Invoke();
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or PlexAuthException)
        {
            AppLog.Warn($"plex: watchlist not loaded: {e.Message}");
            return false;
        }
    }

    /// <summary>One watchlist entry from plex.tv's Discover API.</summary>
    internal static WatchlistEntry? ParseEntry(JsonElement m)
    {
        var guid = PlexClient.Str(m, "guid");
        var key = KeyOf(guid) ?? PlexClient.Str(m, "ratingKey");
        if (key == null)
        {
            return null;
        }
        var kind = PlexClient.Str(m, "type") == "show" ? MediaKind.Show : MediaKind.Movie;
        var thumb = PlexClient.Str(m, "thumb");
        return new WatchlistEntry(key, guid ?? "", PlexClient.Str(m, "title") ?? "", kind,
            PlexClient.Num(m, "year") is { } y ? (int)y : null,
            PlexClient.Str(m, "summary") is { Length: > 0 } s ? s : null,
            thumb != null && thumb.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? thumb : null,
            PlexClient.Str(m, "contentRating") is { Length: > 0 } cr ? cr : null,
            PlexClient.Num(m, "duration") is { } ms and > 0 ? ms / 1000 : null);
    }

    /// <summary>Adds or removes a title on plex.tv (sent in the background, in order).</summary>
    public void Set(string guid, bool on)
    {
        if (KeyOf(guid) is not { } key || Token() is not { } token)
        {
            return;
        }
        lock (_gate)
        {
            if (on && _keys.Add(key))
            {
                // Newest first, like plex.tv; the details come with the next refresh.
                _entries.Insert(0, new WatchlistEntry(key, guid, "", MediaKind.Movie));
            }
            else if (!on && _keys.Remove(key))
            {
                _entries.RemoveAll(e => e.Key == key);
            }
            Known = true;
            var url = $"{Discover}/actions/{(on ? "addToWatchlist" : "removeFromWatchlist")}?ratingKey={Uri.EscapeDataString(key)}";
            _sends = _sends.ContinueWith(async _ =>
            {
                try
                {
                    await _client.SendAsync(HttpMethod.Put, url, token);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or PlexAuthException)
                {
                    AppLog.Warn($"plex: watchlist {(on ? "add" : "remove")} not sent: {e.Message}");
                }
            }, TaskScheduler.Default).Unwrap();
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>Remembers that this device's watchlist additions up to now have been sent.</summary>
    public void MarkSynced()
    {
        SyncedAt = DateTime.UtcNow;
        Save();
    }

    /// <summary>Waits for queued changes (tests).</summary>
    public Task Flush()
    {
        lock (_gate)
        {
            return _sends;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _keys.Clear();
            Known = false;
            SyncedAt = null;
            LastRefresh = DateTime.MinValue;
        }
        try
        {
            File.Delete(_cacheFile);
        }
        catch (IOException)
        {
        }
        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            CacheModel model;
            lock (_gate)
            {
                model = new CacheModel(_entries.ToList(), SyncedAt);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var tmp = _cacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(model));
            File.Move(tmp, _cacheFile, overwrite: true);
        }
        catch (IOException e)
        {
            AppLog.Warn($"plex: watchlist cache not saved: {e.Message}");
        }
    }
}
