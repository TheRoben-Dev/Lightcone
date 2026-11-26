using System.Text.Json;

namespace Lightcone.Host.Plex;

/// <summary>
/// The Plex account's watchlist (it lives on plex.tv, not on a server, so it's the same in every
/// Plex app). Items are matched by their global guid ("plex://movie/5d77…"); the last segment is
/// the key plex.tv's watchlist uses. Kept on disk so it shows offline.
/// </summary>
public sealed class PlexWatchlist
{
    public const string Discover = "https://discover.provider.plex.tv";

    private readonly PlexClient _client;
    private readonly string _cacheFile;
    private readonly object _gate = new();
    private HashSet<string> _keys = new();
    private Task _sends = Task.CompletedTask;

    public PlexWatchlist(PlexClient client, string cacheFile)
    {
        _client = client;
        _cacheFile = cacheFile;
        try
        {
            if (File.Exists(cacheFile) && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(cacheFile)) is { } keys)
            {
                _keys = keys.ToHashSet();
                Known = true;
            }
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            AppLog.Warn($"plex watchlist cache unreadable: {e.Message}");
        }
    }

    /// <summary>The token to use (null while signed out: nothing is synced).</summary>
    public Func<string?> Token { get; set; } = () => null;

    /// <summary>True once the watchlist has been loaded (now or in an earlier session).</summary>
    public bool Known { get; private set; }

    public event Action? Changed;

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

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (Token() is not { } token)
        {
            return;
        }
        try
        {
            var keys = new HashSet<string>();
            const int page = 100;
            for (var start = 0; start < 5000; start += page)
            {
                using var doc = await _client.GetJsonAsync(
                    $"{Discover}/library/sections/watchlist/all?includeFields=guid,title,type&X-Plex-Container-Start={start}&X-Plex-Container-Size={page}",
                    token, ct, TimeSpan.FromSeconds(20));
                var count = 0;
                if (doc.RootElement.TryGetProperty("MediaContainer", out var mc) && mc.TryGetProperty("Metadata", out var md) && md.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in md.EnumerateArray())
                    {
                        count++;
                        if (KeyOf(PlexClient.Str(m, "guid")) is { } k)
                        {
                            keys.Add(k);
                        }
                        else if (PlexClient.Str(m, "ratingKey") is { } rk)
                        {
                            keys.Add(rk);
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
                _keys = keys;
                Known = true;
            }
            Save();
            AppLog.Info($"plex: watchlist has {keys.Count} titles");
            Changed?.Invoke();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or PlexAuthException)
        {
            AppLog.Warn($"plex: watchlist not loaded: {e.Message}");
        }
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
            if (on)
            {
                _keys.Add(key);
            }
            else
            {
                _keys.Remove(key);
            }
            Known = true;
            var url = $"{Discover}/actions/{(on ? "addToWatchlist" : "removeFromWatchlist")}?ratingKey={Uri.EscapeDataString(key)}";
            _sends = _sends.ContinueWith(async _ =>
            {
                try
                {
                    await _client.SendAsync(HttpMethod.Put, url, token);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    AppLog.Warn($"plex: watchlist {(on ? "add" : "remove")} not sent: {e.Message}");
                }
            }, TaskScheduler.Default).Unwrap();
        }
        Save();
        Changed?.Invoke();
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
            _keys.Clear();
            Known = false;
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
            List<string> keys;
            lock (_gate)
            {
                keys = _keys.ToList();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var tmp = _cacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(keys));
            File.Move(tmp, _cacheFile, overwrite: true);
        }
        catch (IOException e)
        {
            AppLog.Warn($"plex: watchlist cache not saved: {e.Message}");
        }
    }
}
