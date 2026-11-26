namespace Lightcone.Playback;

/// <summary>
/// When a position is worth remembering, and when an item counts as watched. Matches what
/// Jellyfin and Plex do by default, so progress synced to a server means the same thing there.
/// </summary>
public static class ResumePolicy
{
    /// <summary>Positions before this are "not started" (seconds).</summary>
    public const double MinResumeSeconds = 30;

    /// <summary>Past this share of the runtime the item counts as watched.</summary>
    public const double WatchedFraction = 0.90;

    /// <summary>How often progress is saved while playing (seconds).</summary>
    public const double SaveIntervalSeconds = 10;

    /// <summary>The position to save: null = clear (not started, or watched).</summary>
    public static double? PositionToSave(double position, double? duration)
    {
        if (position < MinResumeSeconds || IsWatched(position, duration))
        {
            return null;
        }
        return position;
    }

    public static bool IsWatched(double position, double? duration) =>
        duration is > 0 && position / duration.Value >= WatchedFraction;

    /// <summary>Progress bar fraction for a tile, 0..1; null hides the bar.</summary>
    public static double? Progress(double? position, double? duration) =>
        position is > 0 && duration is > 0 ? Math.Clamp(position.Value / duration.Value, 0, 1) : null;
}
