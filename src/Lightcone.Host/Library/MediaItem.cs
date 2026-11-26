namespace Lightcone.Host.Library;

public enum MediaKind
{
    Movie,
    Show,
    Episode,
    Album,
    Track,
    Artist,

    /// <summary>A group of movies or shows: a server collection, one made in Lightcone, or an automatic one.</summary>
    Collection,
}

/// <summary>
/// One playable or browsable thing, whatever source it comes from (a local folder, Jellyfin,
/// Plex). Both UI modes show the same items; only the layout differs.
/// </summary>
public sealed record MediaItem
{
    /// <summary>Unique across sources: "&lt;source&gt;:&lt;id in source&gt;".</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }
    public required MediaKind Kind { get; init; }

    /// <summary>Second line on a tile: "S1 · E3", an artist, a year.</summary>
    public string? Subtitle { get; init; }

    public int? Year { get; init; }
    public string? Overview { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Runtime in seconds, when known.</summary>
    public double? Duration { get; init; }

    public DateTime Added { get; init; }

    public required string SourceId { get; init; }
    public required string SourceName { get; init; }

    /// <summary>What mpv opens (a path or URL); null for containers (shows, albums, artists).</summary>
    public string? PlayUrl { get; init; }

    /// <summary>Local poster / cover image (cached), or null for generated art.</summary>
    public string? Poster { get; init; }

    /// <summary>Poster / cover image to download (server artwork), used when <see cref="Poster"/> is null.</summary>
    public string? PosterUrl { get; init; }

    /// <summary>Show for an episode, artist for a track or album (for display and search).</summary>
    public string? ParentTitle { get; init; }

    /// <summary>Parent (show for an episode, album for a track).</summary>
    public string? ParentId { get; init; }

    /// <summary>Order within the parent (episode or track number).</summary>
    public int? Index { get; init; }

    /// <summary>Season number of an episode.</summary>
    public int? Season { get; init; }

    /// <summary>The server's global id ("plex://movie/5d77…"), used for its online watchlist.</summary>
    public string? Guid { get; init; }

    /// <summary>Collections the server puts this item in ("The Lord of the Rings Collection").</summary>
    public IReadOnlyList<string> Collections { get; init; } = [];

    /// <summary>Age rating ("PG-13", "TV-MA"), when the source knows it.</summary>
    public string? ContentRating { get; init; }

    /// <summary>Audience or critic score out of 10, when the source knows it.</summary>
    public double? Rating { get; init; }

    public bool IsMusic => Kind is MediaKind.Album or MediaKind.Track or MediaKind.Artist;

    public bool Playable => PlayUrl != null;

    /// <summary>"1 h 52 min", "3:41".</summary>
    public string? DurationText => Duration is { } d and > 0 ? FormatDuration(d, Kind is MediaKind.Track) : null;

    public static string FormatDuration(double seconds, bool clock = false)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        if (clock)
        {
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{Math.Max(1, t.Minutes)} min";
    }
}

/// <summary>A horizontal row on the home screen.</summary>
/// <param name="Wide">16:9 tiles (continue watching, episodes) rather than posters.</param>
public sealed record HomeRow(string Id, string Title, IReadOnlyList<MediaItem> Items, bool Wide = false);
