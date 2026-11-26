using System.Text.Json;

namespace Lightcone.Host.Jellyfin;

/// <summary>A Jellyfin server added to Lightcone, with the user signed in on it.</summary>
public sealed record JellyfinServerEntry(string ServerId, string Name, string Url, string UserId, string UserName, string Token);

/// <summary>
/// Jellyfin servers in Lightcone (jellyfin.json in the config folder): this install's device id
/// and one signed-in user per server. The file holds tokens, so it is readable by the user only.
/// </summary>
public sealed record JellyfinAccount
{
    public string DeviceId { get; init; } = "lightcone-" + Guid.NewGuid().ToString("N");
    public IReadOnlyList<JellyfinServerEntry> Servers { get; init; } = [];

    public static JellyfinAccount Load(string file)
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<JellyfinAccount>(File.ReadAllText(file)) is { } a)
            {
                return a;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"jellyfin.json unreadable, starting fresh: {e.Message}");
        }
        var fresh = new JellyfinAccount();
        fresh.Save(file);
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
            AppLog.Warn($"couldn't save jellyfin.json: {e.Message}");
        }
    }
}
