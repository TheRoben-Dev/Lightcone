namespace Lightcone.Host.Library;

/// <summary>
/// A place media comes from: a local folder (SD card and network shares included), a Jellyfin
/// server or a Plex server. Implementations arrive per phase; the UI only talks to this.
/// </summary>
public interface IMediaSource
{
    string Id { get; }
    string Name { get; }

    /// <summary>"Folder", "Jellyfin", "Plex", "Demo".</summary>
    string Type { get; }

    /// <summary>Shown on the Sources screen: "Connected", "Offline, showing what was saved"…</summary>
    string Status => "Connected";

    /// <summary>False while the source can't be reached (its items are what was saved).</summary>
    bool Online => true;

    /// <summary>Every item the source has (cached; Refresh fetches again).</summary>
    IReadOnlyList<MediaItem> Items { get; }

    /// <summary>Resume position the server knows (progress from any device), or null.</summary>
    Progress? ServerProgress(string itemId) => null;

    /// <summary>Watched state the server knows, or null when it doesn't track one.</summary>
    bool? ServerWatched(string itemId) => null;

    /// <summary>When the server last saw the item played (any device), or null.</summary>
    DateTime? ServerLastViewed(string itemId) => null;

    /// <summary>Playback progress, for the server (called every few seconds while playing, and on pause and stop).</summary>
    void OnProgress(MediaItem item, double position, double? duration, PlaybackState state)
    {
    }

    /// <summary>Marked watched or unwatched by the user.</summary>
    void OnWatched(MediaItem item, bool watched)
    {
    }

    /// <summary>
    /// Whether the account's online watchlist has the item: true / false when the server keeps a
    /// watchlist (it wins over this device's), null when it doesn't.
    /// </summary>
    bool? ServerWatchlist(MediaItem item) => null;

    /// <summary>Added to or removed from the watchlist by the user.</summary>
    void OnWatchlist(MediaItem item, bool on)
    {
    }

    /// <summary>The server's collections with their own art, summary and order (titles also name them in <see cref="MediaItem.Collections"/>).</summary>
    IReadOnlyList<ServerCollection> ServerCollections => [];

    /// <summary>Called when an item starts playing: fetch what only matters then (intro / credits markers).</summary>
    Task PrepareAsync(MediaItem item, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Raised when the source's items or state changed (after a refresh).</summary>
    event Action? Changed
    {
        add { }
        remove { }
    }

    /// <summary>Intro / credits markers when the server provides them.</summary>
    IReadOnlyList<Marker> MarkersOf(string itemId) => [];

    /// <summary>Cast, crew and extras (trailers, featurettes) of a movie or show, fetched on demand; null when the source has none.</summary>
    Task<ItemDetails?> DetailsAsync(MediaItem item, CancellationToken ct = default) => Task.FromResult<ItemDetails?>(null);

    /// <summary>Ids of this source's movies and shows a person is in (as cast or crew).</summary>
    Task<IReadOnlyList<string>> TitlesWithAsync(Person person, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Whether the server is reached away from home (remote address or relay): picks the away quality.</summary>
    bool Remote => false;

    /// <summary>
    /// A URL for <paramref name="item"/> converted by the server to <paramref name="quality"/>, or null
    /// when the source can't convert (then the file plays as is).
    /// </summary>
    string? StreamUrl(MediaItem item, Lightcone.Playback.Quality quality) => null;

    /// <summary>
    /// A URL to download <paramref name="item"/> from: the original file, or converted to
    /// <paramref name="quality"/> as one progressive file. Null when the source can't.
    /// </summary>
    string? DownloadUrl(MediaItem item, Lightcone.Playback.Quality quality) => quality.Original ? item.PlayUrl : null;

    /// <summary>Whether the server can search subtitles online for <paramref name="item"/>.</summary>
    bool CanSearchSubtitles(MediaItem item) => false;

    /// <summary>Subtitles the server finds online for <paramref name="item"/> in a two-letter <paramref name="language"/>.</summary>
    Task<IReadOnlyList<SubtitleResult>> SearchSubtitlesAsync(MediaItem item, string language, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SubtitleResult>>([]);

    /// <summary>Has the server fetch <paramref name="result"/> and returns a URL (or path) mpv can load, or null.</summary>
    Task<string?> FetchSubtitleAsync(MediaItem item, SubtitleResult result, CancellationToken ct = default) => Task.FromResult<string?>(null);

    /// <summary>Ids of titles the server thinks are like <paramref name="item"/> ("More like this"), best first.</summary>
    Task<IReadOnlyList<string>> SimilarAsync(MediaItem item, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Seek preview thumbnails for an item, once <see cref="PrepareAsync"/> found them; null when the server has none.</summary>
    SeekPreview? PreviewOf(string itemId) => null;

    Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Whether this server can send its titles to another player.</summary>
    bool CanCast => false;

    /// <summary>Players reachable through this server (not Lightcone itself).</summary>
    Task<IReadOnlyList<CastTarget>> CastTargetsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CastTarget>>([]);

    Task CastAsync(CastTarget target, MediaItem item, double position, CancellationToken ct = default) => Task.CompletedTask;

    Task CastControlAsync(CastTarget target, CastCommand command, CancellationToken ct = default) => Task.CompletedTask;
}

public enum PlaybackState
{
    Playing,
    Paused,
    Stopped,
}

/// <summary>A skippable section of a video (seconds).</summary>
public sealed record Marker(string Kind, double Start, double End)
{
    public const string Intro = "intro", Credits = "credits";

    public string Label => Kind == Credits ? "Skip credits" : "Skip intro";
}
