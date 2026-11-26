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

    /// <summary>Every item the source has (cached; Refresh fetches again).</summary>
    IReadOnlyList<MediaItem> Items { get; }

    /// <summary>Resume position the source knows (server-side progress), seconds.</summary>
    double? PositionOf(string itemId) => null;

    /// <summary>Intro / credits markers when the server provides them.</summary>
    IReadOnlyList<Marker> MarkersOf(string itemId) => [];

    Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>A skippable section of a video (seconds).</summary>
public sealed record Marker(string Kind, double Start, double End)
{
    public const string Intro = "intro", Credits = "credits";

    public string Label => Kind == Credits ? "Skip credits" : "Skip intro";
}
