using System.Text.Json;
using Lightcone.Playback;

namespace Lightcone.Host.Library;

/// <summary>A remembered position.</summary>
public sealed record Progress(double Position, double? Duration, DateTime At);

/// <summary>
/// Resume positions and watched state kept on this device (progress.json). Server sources
/// also sync to their server; this is what a local file or an offline session falls back on.
/// </summary>
public sealed class ProgressStore
{
    private readonly string _file;
    private readonly object _gate = new();
    private Dictionary<string, Progress> _items;
    private HashSet<string> _watched;

    private sealed record FileModel(Dictionary<string, Progress> Items, HashSet<string> Watched);

    public ProgressStore(string file)
    {
        _file = file;
        var model = Load(file);
        _items = model?.Items ?? new();
        _watched = model?.Watched ?? new();
    }

    private static FileModel? Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(file)) : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            AppLog.Warn($"progress file unreadable, starting fresh: {e.Message}");
            return null;
        }
    }

    public Progress? Get(string id)
    {
        lock (_gate)
        {
            return _items.GetValueOrDefault(id);
        }
    }

    public bool IsWatched(string id)
    {
        lock (_gate)
        {
            return _watched.Contains(id);
        }
    }

    /// <summary>Records where playback is (applies <see cref="ResumePolicy"/>) and saves.</summary>
    public void Report(string id, double position, double? duration, DateTime? at = null)
    {
        lock (_gate)
        {
            if (ResumePolicy.IsWatched(position, duration))
            {
                _items.Remove(id);
                _watched.Add(id);
            }
            else if (ResumePolicy.PositionToSave(position, duration) is { } pos)
            {
                _items[id] = new Progress(pos, duration, at ?? DateTime.UtcNow);
            }
            else
            {
                _items.Remove(id);
            }
            Save();
        }
    }

    public void SetWatched(string id, bool watched)
    {
        lock (_gate)
        {
            _items.Remove(id);
            if (watched)
            {
                _watched.Add(id);
            }
            else
            {
                _watched.Remove(id);
            }
            Save();
        }
    }

    /// <summary>In-progress item ids, most recent first.</summary>
    public IReadOnlyList<string> InProgress()
    {
        lock (_gate)
        {
            return _items.OrderByDescending(kv => kv.Value.At).Select(kv => kv.Key).ToList();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel(_items, _watched)));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (IOException e)
        {
            AppLog.Warn($"couldn't save progress: {e.Message}");
        }
    }
}
