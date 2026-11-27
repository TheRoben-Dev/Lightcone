namespace Lightcone.Host.Library;

/// <summary>A player that can be told what to play: a Plex app (Plex Companion) or a Jellyfin session.</summary>
/// <param name="Id">The player's id on its server (Plex client identifier, Jellyfin session id).</param>
/// <param name="Uri">Plex: a direct address of the player, if it has one; else commands go through the server.</param>
public sealed record CastTarget(string SourceId, string Id, string Name, string Product, string? Uri = null);

public enum CastCommand
{
    PlayPause,
    Pause,
    Play,
    Stop,
    SkipBack,
    SkipForward,
    Next,
    Previous,
}

public sealed partial class MediaLibrary
{
    /// <summary>Players on the item's server that can be told to play it.</summary>
    public async Task<IReadOnlyList<CastTarget>> CastTargetsAsync(MediaItem item, CancellationToken ct = default)
    {
        if (SourceOf(item) is not { } source)
        {
            return [];
        }
        try
        {
            return await source.CastTargetsAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException)
        {
            AppLog.Info($"cast targets on {source.Name}: {e.Message}");
            return [];
        }
    }

    public bool CanCast(MediaItem item) => item.Kind is MediaKind.Movie or MediaKind.Episode && SourceOf(item)?.CanCast == true;

    /// <summary>Tells <paramref name="target"/> to play <paramref name="item"/> from <paramref name="position"/> seconds.</summary>
    public async Task<string?> CastAsync(CastTarget target, MediaItem item, double position, CancellationToken ct = default)
    {
        if (SourceOf(item) is not { } source)
        {
            return "That server isn't connected.";
        }
        try
        {
            await source.CastAsync(target, item, position, ct);
            AppLog.Info($"cast {item.Id} to {target.Name} ({target.Product}) at {position:0}s");
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException)
        {
            AppLog.Info($"cast {item.Id} to {target.Name}: {e.Message}");
            return e.Message;
        }
    }

    /// <summary>Sends a remote-control command to a player that's playing something cast to it.</summary>
    public async Task<bool> CastControlAsync(CastTarget target, CastCommand command, CancellationToken ct = default)
    {
        if (Sources.FirstOrDefault(s => s.Id == target.SourceId) is not { } source)
        {
            return false;
        }
        try
        {
            await source.CastControlAsync(target, command, ct);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException)
        {
            AppLog.Info($"cast control {command} on {target.Name}: {e.Message}");
            return false;
        }
    }
}
