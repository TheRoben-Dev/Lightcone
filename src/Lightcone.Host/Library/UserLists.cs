using System.Text.Json;

namespace Lightcone.Host.Library;

/// <summary>Audio and subtitle choices remembered for a show (or a movie).</summary>
public sealed record TrackChoice(string? Audio, string? Subtitle, bool SubtitlesOff);

/// <summary>A collection made in Lightcone: a name and the items in it, in the order added.</summary>
public sealed record UserCollection(string Id, string Name, List<string> Items);

/// <summary>
/// What the user keeps on this device (lists.json): the watchlist (item id → when it was added)
/// and collections made in Lightcone. Server watchlists sync through their source on top of this.
/// </summary>
public sealed class UserLists
{
    private readonly string? _file;
    private readonly object _gate = new();
    private Dictionary<string, DateTime> _watchlist;
    private List<UserCollection> _collections;
    private Dictionary<string, TrackChoice> _tracks;

    private sealed record FileModel(Dictionary<string, DateTime>? Watchlist, List<UserCollection>? Collections, Dictionary<string, TrackChoice>? Tracks = null);

    /// <param name="file">Where to keep them; null keeps them in memory only (tests).</param>
    public UserLists(string? file)
    {
        _file = file;
        FileModel? model = null;
        try
        {
            model = file != null && File.Exists(file) ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(file)) : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            AppLog.Warn($"lists file unreadable, starting fresh: {e.Message}");
        }
        _watchlist = model?.Watchlist ?? new();
        _collections = model?.Collections ?? new();
        _tracks = model?.Tracks ?? new();
    }

    // ---- remembered tracks ----

    public TrackChoice? TrackChoice(string key)
    {
        lock (_gate)
        {
            return _tracks.GetValueOrDefault(key);
        }
    }

    /// <summary>Updates what's remembered for a show or movie (null arguments keep the old value).</summary>
    public void RememberTracks(string key, string? audio = null, string? subtitle = null, bool? subtitlesOff = null)
    {
        lock (_gate)
        {
            var old = _tracks.GetValueOrDefault(key) ?? new TrackChoice(null, null, false);
            _tracks[key] = new TrackChoice(audio ?? old.Audio, subtitle ?? old.Subtitle, subtitlesOff ?? old.SubtitlesOff);
            Save();
        }
    }

    public event Action? Changed;

    // ---- watchlist ----

    public bool OnWatchlist(string id)
    {
        lock (_gate)
        {
            return _watchlist.ContainsKey(id);
        }
    }

    public DateTime? AddedToWatchlist(string id)
    {
        lock (_gate)
        {
            return _watchlist.TryGetValue(id, out var at) ? at : null;
        }
    }

    public void SetWatchlist(string id, bool on)
    {
        lock (_gate)
        {
            if (on == _watchlist.ContainsKey(id))
            {
                return;
            }
            if (on)
            {
                _watchlist[id] = DateTime.UtcNow;
            }
            else
            {
                _watchlist.Remove(id);
            }
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Watchlist ids, most recently added first.</summary>
    public IReadOnlyList<string> Watchlist()
    {
        lock (_gate)
        {
            return _watchlist.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        }
    }

    // ---- collections ----

    public IReadOnlyList<UserCollection> Collections
    {
        get
        {
            lock (_gate)
            {
                return _collections.Select(c => c with { Items = [.. c.Items] }).ToList();
            }
        }
    }

    public UserCollection CreateCollection(string name, IEnumerable<string>? items = null)
    {
        UserCollection c;
        lock (_gate)
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            c = new UserCollection(id, name.Trim(), items?.Distinct().ToList() ?? []);
            _collections.Add(c);
            Save();
        }
        Changed?.Invoke();
        return c;
    }

    public void RenameCollection(string id, string name) => Update(id, c => c with { Name = name.Trim() });

    public void DeleteCollection(string id)
    {
        lock (_gate)
        {
            _collections.RemoveAll(c => c.Id == id);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Adds or removes an item (no-op when it's already that way).</summary>
    public void SetInCollection(string collectionId, string itemId, bool on) => Update(collectionId, c =>
    {
        var items = c.Items.Where(i => i != itemId).ToList();
        if (on)
        {
            items.Add(itemId);
        }
        return c with { Items = items };
    });

    private void Update(string id, Func<UserCollection, UserCollection> change)
    {
        lock (_gate)
        {
            var i = _collections.FindIndex(c => c.Id == id);
            if (i < 0)
            {
                return;
            }
            _collections[i] = change(_collections[i]);
            Save();
        }
        Changed?.Invoke();
    }

    private void Save()
    {
        if (_file == null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel(_watchlist, _collections, _tracks)));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (IOException e)
        {
            AppLog.Warn($"couldn't save lists: {e.Message}");
        }
    }
}
