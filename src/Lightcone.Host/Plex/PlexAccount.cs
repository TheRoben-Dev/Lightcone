using System.Text.Json;

namespace Lightcone.Host.Plex;

/// <summary>A server the user chose to show in Lightcone, and how it was last reached.</summary>
public sealed record PlexServerEntry(string MachineId, string Name, string AccessToken, IReadOnlyList<PlexConnection> Connections, string? LastUri);

/// <summary>
/// The signed-in Plex account (plex.json in the config folder): this install's client id, the
/// account token and the chosen servers. The file holds secrets, so it is readable by the user
/// only, and it never goes into logs or settings.json.
/// </summary>
public sealed record PlexAccount
{
    public string ClientId { get; init; } = Guid.NewGuid().ToString("N");
    public string? Token { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<PlexServerEntry> Servers { get; init; } = [];

    public bool SignedIn => Token != null;

    public static PlexAccount Load(string file)
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<PlexAccount>(File.ReadAllText(file)) is { } a)
            {
                return a;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"plex.json unreadable, signed out: {e.Message}");
        }
        var fresh = new PlexAccount();
        fresh.Save(file);  // keep the client id stable from the first run
        return fresh;
    }

    public void Save(string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"couldn't save plex.json: {e.Message}");
        }
    }
}
