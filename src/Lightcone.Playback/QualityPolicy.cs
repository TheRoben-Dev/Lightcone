using System.Globalization;
using System.Net;

namespace Lightcone.Playback;

/// <summary>A streaming quality: the file as is (<see cref="Original"/>), or converted by the server to a bitrate and size.</summary>
public sealed record Quality(string Id, string Label, int Kbps, int Width, int Height)
{
    public bool Original => Kbps <= 0;
}

/// <summary>The quality choices (Settings → Playback, the player's options, downloads) and where a server is.</summary>
public static class QualityPolicy
{
    public static readonly Quality Original = new("original", "Original (direct play)", 0, 0, 0);

    public static readonly IReadOnlyList<Quality> Choices =
    [
        Original,
        new("20000", "20 Mbps · 1080p", 20000, 1920, 1080),
        new("12000", "12 Mbps · 1080p", 12000, 1920, 1080),
        new("8000", "8 Mbps · 1080p", 8000, 1920, 1080),
        new("4000", "4 Mbps · 720p", 4000, 1280, 720),
        new("2000", "2 Mbps · 720p", 2000, 1280, 720),
        new("1000", "1 Mbps · 480p", 1000, 854, 480),
    ];

    public static Quality Find(string? id) => Choices.FirstOrDefault(q => q.Id == id) ?? Original;

    public static string Describe(string id) => Find(id) switch
    {
        { Original: true } => "Plays the file as it is: best picture, and the server does no work. Needs the full bitrate over the network.",
        var q => string.Create(CultureInfo.InvariantCulture, $"The server converts to H.264 at up to {q.Kbps / 1000} Mbps and {q.Height}p. For slow networks or busy servers."),
    };

    /// <summary>
    /// Whether a server address is on this network (a private, link-local or loopback address,
    /// or a .local / plex.direct name for one); anything else counts as away from home.
    /// </summary>
    public static bool IsLocal(string? url)
    {
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }
        var host = uri.Host;
        if (host.EndsWith(".plex.direct", StringComparison.OrdinalIgnoreCase))
        {
            // "192-168-1-20.<hash>.plex.direct" names the address it points at.
            host = host.Split('.')[0].Replace('-', '.');
        }
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            return false;
        }
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xfe) == 0xfc;
        }
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] < 128);
    }
}
