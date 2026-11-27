using System.Buffers.Binary;

namespace Lightcone.Host.Library;

/// <summary>A preview frame: an image (JPEG), and the part of it to show (all of it when <see cref="Width"/> is 0).</summary>
public sealed record PreviewFrame(byte[] Image, int X, int Y, int Width, int Height, double At);

/// <summary>
/// Thumbnails along a video for the seek bar: a Plex BIF index (downloaded once) or Jellyfin
/// trickplay tiles (a grid of frames per image, fetched as needed).
/// </summary>
public abstract class SeekPreview
{
    /// <summary>The frame nearest before <paramref name="seconds"/>, or null (not loaded, or none).</summary>
    public abstract Task<PreviewFrame?> FrameAsync(double seconds, CancellationToken ct = default);
}

/// <summary>Roku-style BIF: a header, an index of (frame number, offset) pairs, then the JPEGs.</summary>
public sealed class BifPreview : SeekPreview
{
    private static readonly byte[] Magic = [0x89, 0x42, 0x49, 0x46, 0x0d, 0x0a, 0x1a, 0x0a];
    private readonly Func<CancellationToken, Task<byte[]>> _download;
    private Task<Bif?>? _load;
    private readonly object _gate = new();

    public sealed record Bif(double Interval, IReadOnlyList<(double At, int Offset, int Length)> Frames, byte[] Data);

    public BifPreview(Func<CancellationToken, Task<byte[]>> download) => _download = download;

    public static Bif? Parse(byte[] data)
    {
        if (data.Length < 64 || !data.AsSpan(0, 8).SequenceEqual(Magic))
        {
            return null;
        }
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        var ms = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        var interval = (ms == 0 ? 1000 : ms) / 1000.0;
        var entries = new List<(uint Index, int Offset)>();
        for (var i = 0; i <= count && 64 + i * 8 + 8 <= data.Length; i++)
        {
            var at = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(64 + i * 8));
            var off = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(64 + i * 8 + 4));
            entries.Add((at, (int)Math.Min(off, (uint)data.Length)));
            if (at == 0xffffffff)
            {
                break;
            }
        }
        var frames = new List<(double, int, int)>();
        for (var i = 0; i + 1 < entries.Count; i++)
        {
            var len = entries[i + 1].Offset - entries[i].Offset;
            if (len > 0 && entries[i].Index != 0xffffffff)
            {
                frames.Add((entries[i].Index * interval, entries[i].Offset, len));
            }
        }
        return frames.Count == 0 ? null : new Bif(interval, frames, data);
    }

    public override async Task<PreviewFrame?> FrameAsync(double seconds, CancellationToken ct = default)
    {
        Task<Bif?> load;
        lock (_gate)
        {
            load = _load ??= Load();
        }
        if (!load.IsCompleted)
        {
            return null;  // still downloading: no preview yet (the seek itself never waits)
        }
        if (await load is not { } bif)
        {
            return null;
        }
        var frames = bif.Frames;
        var lo = 0;
        var hi = frames.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (frames[mid].At <= seconds)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }
        var (at, offset, length) = frames[lo];
        return new PreviewFrame(bif.Data.AsSpan(offset, length).ToArray(), 0, 0, 0, 0, at);
    }

    private async Task<Bif?> Load()
    {
        try
        {
            return Parse(await _download(CancellationToken.None));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            AppLog.Info($"seek previews unavailable: {e.Message}");
            return null;
        }
    }
}

/// <summary>Jellyfin trickplay: <see cref="Columns"/> × <see cref="Rows"/> frames of <see cref="Width"/> × <see cref="Height"/> per tile image.</summary>
public sealed class TilePreview : SeekPreview
{
    private readonly Func<int, CancellationToken, Task<byte[]>> _tile;
    private readonly Dictionary<int, Task<byte[]?>> _tiles = new();
    private readonly object _gate = new();

    public int Width { get; }
    public int Height { get; }
    public int Columns { get; }
    public int Rows { get; }
    public int Count { get; }
    public double Interval { get; }

    public TilePreview(Func<int, CancellationToken, Task<byte[]>> tile, int width, int height, int columns, int rows, int count, double intervalSeconds)
    {
        _tile = tile;
        Width = width;
        Height = height;
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        Count = count;
        Interval = intervalSeconds <= 0 ? 10 : intervalSeconds;
    }

    /// <summary>Which tile image and where in it the frame for <paramref name="seconds"/> is.</summary>
    public (int Tile, int X, int Y, double At) Locate(double seconds)
    {
        var n = Math.Clamp((int)(seconds / Interval), 0, Math.Max(0, Count - 1));
        var per = Columns * Rows;
        var inTile = n % per;
        return (n / per, inTile % Columns * Width, inTile / Columns * Height, n * Interval);
    }

    public override async Task<PreviewFrame?> FrameAsync(double seconds, CancellationToken ct = default)
    {
        if (Count <= 0)
        {
            return null;
        }
        var (tile, x, y, at) = Locate(seconds);
        Task<byte[]?> load;
        lock (_gate)
        {
            if (!_tiles.TryGetValue(tile, out load!))
            {
                _tiles[tile] = load = Fetch(tile);
            }
        }
        return await load is { } img ? new PreviewFrame(img, x, y, Width, Height, at) : null;
    }

    private async Task<byte[]?> Fetch(int tile)
    {
        try
        {
            return await _tile(tile, CancellationToken.None);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            AppLog.Info($"trickplay tile {tile} unavailable: {e.Message}");
            return null;
        }
    }
}
