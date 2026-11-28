namespace Lightcone.Host.Library;

/// <summary>
/// One title on an online watchlist (the Plex account's, on plex.tv), whether or not a library
/// has it. <paramref name="Key"/> is the watchlist's own id for it, <paramref name="Guid"/> the
/// global id library items carry ("plex://movie/5d77…").
/// </summary>
public sealed record WatchlistEntry(string Key, string Guid, string Title, MediaKind Kind, int? Year = null, string? Summary = null,
    string? PosterUrl = null, string? ContentRating = null, double? Duration = null);

/// <summary>
/// A watchlist kept by an online account rather than on this device (Plex's). It wins over
/// Lightcone's own list for the titles it knows about.
/// </summary>
public interface IOnlineWatchlist
{
    /// <summary>Signed in and loaded at least once (now or in an earlier session).</summary>
    bool Active { get; }

    /// <summary>"Plex": shown on titles that aren't in a library.</summary>
    string Name { get; }

    /// <summary>Its titles, most recently added first.</summary>
    IReadOnlyList<WatchlistEntry> Entries { get; }

    /// <summary>Adds or removes a title by its global id.</summary>
    void Set(string guid, bool on);
}

/// <summary>
/// A collection as the server keeps it: its own poster and summary, how its titles are ordered,
/// and (for smart collections and custom order) the titles themselves, as library item ids.
/// </summary>
/// <param name="Members">Item ids in the server's order, or null when the titles' own collection tags say who's in it.</param>
public sealed record ServerCollection(string Title, string? Summary, string? PosterUrl, CollectionOrder Order, bool Smart, IReadOnlyList<string>? Members);

public enum CollectionOrder
{
    Release,
    Title,
    Custom,
}
