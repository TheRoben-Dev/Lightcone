using System.Text.Json;

namespace Lightcone.Host.Library;

/// <summary>
/// Time spent watching one item on one day (history.json). Titles are kept so the stats still
/// read right after a source is removed.
/// </summary>
public sealed record WatchEntry(DateOnly Day, string ItemId, string Title, MediaKind Kind, string? ShowId, string? ShowTitle,
    IReadOnlyList<string> Genres, double Seconds, int Finished = 0);

/// <summary>
/// What was watched in Lightcone and for how long, per item per day: the source of the stats
/// page's time, streaks and busiest days. Watched state from servers comes on top of it.
/// </summary>
public sealed class WatchHistory
{
    private readonly string? _file;
    private readonly object _gate = new();
    private readonly List<WatchEntry> _entries;
    private DateTime _savedAt = DateTime.MinValue;
    private bool _dirty;

    /// <param name="file">Where to keep it; null keeps it in memory only (tests).</param>
    public WatchHistory(string? file)
    {
        _file = file;
        try
        {
            _entries = file != null && File.Exists(file) ? JsonSerializer.Deserialize<List<WatchEntry>>(File.ReadAllText(file)) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            AppLog.Warn($"history file unreadable, starting fresh: {e.Message}");
            _entries = [];
        }
    }

    public IReadOnlyList<WatchEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    /// <summary>
    /// Adds <paramref name="seconds"/> watched of <paramref name="item"/> on <paramref name="when"/>'s
    /// (local) day, and counts a finish. Saved at most every half minute unless <paramref name="flush"/>.
    /// </summary>
    public void Add(MediaItem item, MediaItem? show, double seconds, bool finished = false, DateTime? when = null, bool flush = false)
    {
        if (seconds <= 0 && !finished)
        {
            return;
        }
        var day = DateOnly.FromDateTime((when ?? DateTime.Now).ToLocalTime());
        lock (_gate)
        {
            var i = _entries.FindLastIndex(e => e.Day == day && e.ItemId == item.Id);
            if (i >= 0)
            {
                var old = _entries[i];
                _entries[i] = old with { Seconds = old.Seconds + Math.Max(0, seconds), Finished = old.Finished + (finished ? 1 : 0) };
            }
            else
            {
                _entries.Add(new WatchEntry(day, item.Id, item.Title, item.Kind, show?.Id ?? item.ParentId, show?.Title ?? item.ParentTitle,
                    (show?.Genres.Count > 0 ? show.Genres : item.Genres).ToList(), Math.Max(0, seconds), finished ? 1 : 0));
            }
            _dirty = true;
            if (flush || finished || DateTime.UtcNow - _savedAt > TimeSpan.FromSeconds(30))
            {
                Save();
            }
        }
    }

    /// <summary>Writes pending changes (on stop and at exit).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_dirty)
            {
                Save();
            }
        }
    }

    private void Save()
    {
        _savedAt = DateTime.UtcNow;
        _dirty = false;
        if (_file == null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (IOException e)
        {
            AppLog.Warn($"couldn't save history: {e.Message}");
        }
    }
}
