using System.Text.Json;
using Lightcone.Playback;

namespace Lightcone.Host.Library;

public enum DownloadState
{
    Queued,
    Downloading,
    Done,
    Failed,
}

/// <summary>One download: the item as it was (so it shows offline too), its file and progress.</summary>
public sealed record DownloadEntry
{
    public required MediaItem Item { get; init; }
    public string Quality { get; init; } = "original";
    public string? File { get; init; }
    public long Bytes { get; init; }
    public long? Total { get; init; }
    public DownloadState State { get; init; }
    public string? Error { get; init; }
    public DateTime Added { get; init; } = DateTime.UtcNow;

    /// <summary>Watched or resume state changed while playing the download: push it to the server again when online.</summary>
    public bool Unsynced { get; init; }

    public double? Fraction => Total is > 0 and var t ? Math.Clamp(Bytes / (double)t, 0, 1) : null;
}

/// <summary>
/// Offline downloads: movies and episodes saved to this device (as the file is, or converted
/// smaller by the server), one at a time, resuming a broken download where the server allows.
/// Downloaded items play from the file even with no network; what's watched offline is pushed to
/// the server again once it's reachable (<see cref="SyncBack"/>).
/// </summary>
public sealed class Downloads
{
    private readonly string _index;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly List<DownloadEntry> _entries;
    private CancellationTokenSource? _current;
    private string? _currentId;
    private Task? _worker;

    public Func<string> Folder { get; set; }

    /// <summary>Raised (on a worker thread) when a download starts, moves, finishes or fails.</summary>
    public event Action? Changed;

    public Downloads(string indexFile, Func<string> folder, HttpClient? http = null)
    {
        _index = indexFile;
        Folder = folder;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _entries = AppHost.LoadJson<List<DownloadEntry>>(indexFile) ?? [];
        // A download cut off by quitting carries on next time.
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].State == DownloadState.Downloading)
            {
                _entries[i] = _entries[i] with { State = DownloadState.Queued };
            }
        }
    }

    public IReadOnlyList<DownloadEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToList();
            }
        }
    }

    public DownloadEntry? Of(string itemId)
    {
        lock (_gate)
        {
            return _entries.FirstOrDefault(e => e.Item.Id == itemId);
        }
    }

    /// <summary>The downloaded file to play for <paramref name="item"/>, or null.</summary>
    public string? LocalFile(MediaItem item) =>
        Of(item.Id) is { State: DownloadState.Done, File: { } f } && System.IO.File.Exists(f) ? f : null;

    public long BytesUsed => Entries.Where(e => e.File != null).Sum(e => e.Bytes);

    /// <summary>Queues <paramref name="item"/> from <paramref name="url"/> (see MediaLibrary.DownloadUrl). False if already there.</summary>
    public bool Add(MediaItem item, string url, Quality quality)
    {
        lock (_gate)
        {
            if (_entries.Any(e => e.Item.Id == item.Id && e.State != DownloadState.Failed))
            {
                return false;
            }
            _entries.RemoveAll(e => e.Item.Id == item.Id);
            _entries.Add(new DownloadEntry { Item = item with { PlayUrl = url }, Quality = quality.Id, State = DownloadState.Queued });
            Save();
        }
        Changed?.Invoke();
        Kick();
        return true;
    }

    /// <summary>Deletes a download (stopping it if it's running) and its file.</summary>
    public void Remove(string itemId)
    {
        DownloadEntry? gone;
        lock (_gate)
        {
            gone = _entries.FirstOrDefault(e => e.Item.Id == itemId);
            if (gone == null)
            {
                return;
            }
            _entries.Remove(gone);
            if (_currentId == itemId)
            {
                _current?.Cancel();
            }
            Save();
        }
        foreach (var f in new[] { gone.File, gone.File + ".part" })
        {
            try
            {
                if (f != null && System.IO.File.Exists(f))
                {
                    System.IO.File.Delete(f);
                }
            }
            catch (IOException e)
            {
                AppLog.Info($"download: couldn't delete {f}: {e.Message}");
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Tries failed downloads again.</summary>
    public void Retry()
    {
        lock (_gate)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].State == DownloadState.Failed)
                {
                    _entries[i] = _entries[i] with { State = DownloadState.Queued, Error = null };
                }
            }
            Save();
        }
        Kick();
    }

    /// <summary>Starts the worker if there's anything queued (after start-up, say).</summary>
    public void Kick()
    {
        lock (_gate)
        {
            if (_worker is { IsCompleted: false } || !_entries.Any(e => e.State == DownloadState.Queued))
            {
                return;
            }
            _worker = Task.Run(RunAsync);
        }
    }

    /// <summary>Notes that a downloaded item was watched or moved on while playing (offline, maybe).</summary>
    public void NoteProgress(string itemId)
    {
        lock (_gate)
        {
            var i = _entries.FindIndex(e => e.Item.Id == itemId);
            if (i >= 0 && !_entries[i].Unsynced)
            {
                _entries[i] = _entries[i] with { Unsynced = true };
                Save();
            }
        }
    }

    /// <summary>
    /// Pushes watched / resume state of downloads played since the last sync to their servers
    /// (call when the servers are reachable again). Returns how many were sent.
    /// </summary>
    public int SyncBack(MediaLibrary lib)
    {
        List<DownloadEntry> dirty;
        lock (_gate)
        {
            dirty = _entries.Where(e => e.Unsynced).ToList();
        }
        var sent = 0;
        foreach (var e in dirty)
        {
            if (lib.Find(e.Item.Id) is not { } item || !lib.SourceOnline(item))
            {
                continue;  // its server isn't back yet
            }
            if (lib.Progress.IsWatched(item.Id))
            {
                lib.SetWatched(item, true);
            }
            else if (lib.Progress.Get(item.Id) is { Position: > 0 } p && p.Position is var pos)
            {
                lib.Report(item, pos, item.Duration, PlaybackState.Stopped);
            }
            lock (_gate)
            {
                var i = _entries.FindIndex(x => x.Item.Id == item.Id);
                if (i >= 0)
                {
                    _entries[i] = _entries[i] with { Unsynced = false };
                }
            }
            sent++;
        }
        if (sent > 0)
        {
            lock (_gate)
            {
                Save();
            }
            AppLog.Info($"downloads: synced {sent} watched / resume states back to the servers");
        }
        return sent;
    }

    /// <summary>A file name for the item: "Show - S01E02 - Title.mkv", "Movie (2019).mp4".</summary>
    public static string FileName(MediaItem item, string url)
    {
        var ext = Path.GetExtension(new Uri(url, UriKind.Absolute).AbsolutePath);
        if (ext is not { Length: > 1 and <= 5 } || ext.Contains("m3u8", StringComparison.Ordinal) || url.Contains("/transcode/", StringComparison.Ordinal) || url.Contains("stream.mp4", StringComparison.Ordinal))
        {
            ext = url.Contains("/Download?", StringComparison.Ordinal) ? ".mkv" : ".mp4";
        }
        var name = item.Kind == MediaKind.Episode && item.ParentTitle != null
            ? $"{item.ParentTitle} - S{item.Season ?? 0:00}E{item.Index ?? 0:00} - {item.Title}"
            : item.Year is { } y ? $"{item.Title} ({y})" : item.Title;
        var bad = Path.GetInvalidFileNameChars().Concat([':', '?', '*', '"', '<', '>', '|', '/', '\\']).ToHashSet();
        var safe = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return (safe.Length > 120 ? safe[..120] : safe) + ext;
    }

    private async Task RunAsync()
    {
        while (true)
        {
            DownloadEntry? next;
            CancellationToken ct;
            lock (_gate)
            {
                next = _entries.FirstOrDefault(e => e.State == DownloadState.Queued);
                if (next == null)
                {
                    _currentId = null;
                    return;
                }
                _current = new CancellationTokenSource();
                _currentId = next.Item.Id;
                ct = _current.Token;
                Update(next.Item.Id, e => e with { State = DownloadState.Downloading });
            }
            Changed?.Invoke();
            try
            {
                await FetchAsync(next, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // removed while downloading
            }
            catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
            {
                AppLog.Info($"download of {next.Item.Id} failed: {e.Message}");
                lock (_gate)
                {
                    Update(next.Item.Id, x => x with { State = DownloadState.Failed, Error = e.Message });
                }
            }
            Changed?.Invoke();
        }
    }

    private async Task FetchAsync(DownloadEntry entry, CancellationToken ct)
    {
        var url = entry.Item.PlayUrl ?? throw new IOException("nothing to download");
        var folder = Folder();
        Directory.CreateDirectory(folder);
        var file = entry.File ?? Path.Combine(folder, FileName(entry.Item, url));
        var part = file + ".part";
        var have = System.IO.File.Exists(part) ? new FileInfo(part).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0)
        {
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        }
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var resumed = have > 0 && res.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed)
        {
            have = 0;
        }
        long? total = res.Content.Headers.ContentLength is { } len ? len + have : null;
        lock (_gate)
        {
            Update(entry.Item.Id, e => e with { File = file, Total = total, Bytes = have });
        }
        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            var lastNote = Environment.TickCount64;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                have += n;
                if (Environment.TickCount64 - lastNote > 500)
                {
                    lastNote = Environment.TickCount64;
                    lock (_gate)
                    {
                        Update(entry.Item.Id, e => e with { Bytes = have }, save: false);
                    }
                    Changed?.Invoke();
                }
            }
        }
        System.IO.File.Move(part, file, overwrite: true);
        lock (_gate)
        {
            Update(entry.Item.Id, e => e with { Bytes = have, Total = have, State = DownloadState.Done });
        }
        AppLog.Info($"downloaded {entry.Item.Id} to {file} ({have / 1048576.0:0} MB)");
    }

    private void Update(string itemId, Func<DownloadEntry, DownloadEntry> change, bool save = true)
    {
        var i = _entries.FindIndex(e => e.Item.Id == itemId);
        if (i < 0)
        {
            return;
        }
        _entries[i] = change(_entries[i]);
        if (save)
        {
            Save();
        }
    }

    private void Save()
    {
        try
        {
            AppHost.SaveJson(_index, _entries);
        }
        catch (IOException e)
        {
            AppLog.Info($"downloads: couldn't save the list: {e.Message}");
        }
    }
}
