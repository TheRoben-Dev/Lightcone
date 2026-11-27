using Lightcone.App.Playback;
using Lightcone.Host.Library;
using Lightcone.Playback;

namespace Lightcone.App.Screens;

/// <summary>Download buttons on detail pages: pick a size, then queue the movie or episodes.</summary>
public static class DownloadActions
{
    private static MediaLibrary Lib => Main.Instance.Host!.Library;

    private static Downloads Store => Main.Instance.Host!.Downloads;

    /// <summary>Whether the item's server can hand over its file (sample streams and opened files can't).</summary>
    public static bool CanDownload(MediaItem item) =>
        item.Playable && item.Kind is MediaKind.Movie or MediaKind.Episode
        && Lib.DownloadUrl(item, QualityPolicy.Original) is { } url && url.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks which size, then queues <paramref name="items"/> (already downloaded ones are skipped).</summary>
    public static void Offer(IReadOnlyList<MediaItem> items, string what)
    {
        var todo = items.Where(i => CanDownload(i) && Store.Of(i.Id) is not { State: not DownloadState.Failed }).ToList();
        if (todo.Count == 0)
        {
            Main.Instance.Toast(items.Count == 1 ? "Already downloaded" : "Those are downloaded already");
            return;
        }
        var size = todo.Sum(i => i.Duration ?? 0) / 3600.0;  // hours
        string Gb(int kbps) => $"about {size * kbps * 0.45 / 1000:0.#} GB";  // kbps × 3600 s / 8 / 1e6
        var convert = Lib.DownloadUrl(todo[0], QualityPolicy.Find("4000")) != null;
        var options = new List<(string, Action)> { ("Original quality (the file as is)", () => Queue(todo, QualityPolicy.Original)) };
        if (convert)
        {
            options.Add(($"1080p, 8 Mbps ({Gb(8000)})", () => Queue(todo, QualityPolicy.Find("8000"))));
            options.Add(($"720p, 4 Mbps ({Gb(4000)})", () => Queue(todo, QualityPolicy.Find("4000"))));
            options.Add(($"480p, 1 Mbps ({Gb(1000)})", () => Queue(todo, QualityPolicy.Find("1000"))));
        }
        options.Add(("Cancel", () => { }));
        var count = todo.Count == 1 ? "" : $" ({todo.Count} episodes)";
        Main.Instance.Push(new ChoicePage($"Download {what}{count} to watch offline? {(convert ? "Smaller sizes are converted by your server as they download." : "")}".Trim(), [.. options]));
    }

    private static void Queue(IReadOnlyList<MediaItem> items, Quality q)
    {
        var added = 0;
        foreach (var item in items)
        {
            if (Lib.DownloadUrl(item, q) is { } url && Store.Add(item, url, q))
            {
                added++;
            }
        }
        Main.Instance.Toast(added == 0 ? "Nothing new to download" : added == 1 ? $"Downloading {items[0].Title}" : $"Downloading {added} episodes");
    }

    /// <summary>The next <paramref name="count"/> episodes to watch of a show.</summary>
    public static IReadOnlyList<MediaItem> NextEpisodes(MediaItem show, int count)
    {
        var eps = Lib.Children(show.Id).Where(e => e.Playable && e.Season != 0).ToList();
        var start = Lib.NextToPlay(show) is { } next ? Math.Max(0, eps.FindIndex(e => e.Id == next.Id)) : 0;
        return eps.Skip(start).Where(e => !Lib.IsWatched(e)).Take(count).ToList();
    }

    /// <summary>Label for a Download button on an item's page: what it would do, or its state.</summary>
    public static string Label(MediaItem item) => Store.Of(item.Id) switch
    {
        { State: DownloadState.Done } => "✓  Downloaded",
        { State: DownloadState.Downloading } e => e.Fraction is { } f ? $"Downloading {f * 100:0}%" : "Downloading…",
        { State: DownloadState.Queued } => "Download queued",
        { State: DownloadState.Failed } => "Download failed: retry",
        _ => "⤓  Download",
    };

    /// <summary>The Download button's press: offer it, or manage the one there.</summary>
    public static void Press(MediaItem item)
    {
        switch (Store.Of(item.Id))
        {
            case null:
                Offer([item], item.Title);
                break;
            case { State: DownloadState.Failed }:
                Store.Retry();
                break;
            default:
                Main.Instance.Push(new DownloadsPage());
                break;
        }
    }
}
