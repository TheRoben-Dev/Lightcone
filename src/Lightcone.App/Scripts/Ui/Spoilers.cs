using Lightcone.Host.Library;

namespace Lightcone.App.Ui;

/// <summary>
/// Spoiler-free mode (Settings → Library): episodes not watched or started yet show no thumbnail
/// and no summary, so a still or a synopsis doesn't give the plot away.
/// </summary>
public static class Spoilers
{
    public static bool Hidden(MediaItem item)
    {
        if (item.Kind != MediaKind.Episode || Main.Instance?.Host is not { } host || !host.Settings.HideSpoilers)
        {
            return false;
        }
        var lib = host.Library;
        return !lib.IsWatched(item) && lib.PositionOf(item) == null;
    }

    /// <summary>The item, or a copy without its thumbnail and summary when they'd spoil it.</summary>
    public static MediaItem Safe(MediaItem item) => Hidden(item) ? item with { Poster = null, PosterUrl = null, Overview = null } : item;

    /// <summary>A summary to show, or null (none, or hidden).</summary>
    public static string? Overview(MediaItem item) => Hidden(item) ? null : item.Overview;
}
