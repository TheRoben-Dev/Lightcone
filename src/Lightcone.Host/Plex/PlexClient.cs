using System.Net.Http.Headers;
using System.Text.Json;
using Lightcone.Host.Updates;

namespace Lightcone.Host.Plex;

/// <summary>A sign-in code from plex.tv: the user enters <see cref="Code"/> at plex.tv/link.</summary>
public sealed record PlexPin(long Id, string Code, DateTime Expires);

/// <summary>One way to reach a server: its LAN address, its public address, or Plex's relay.</summary>
public sealed record PlexConnection(string Uri, bool Local, bool Relay);

/// <summary>A Plex Media Server the account can use (owned or shared).</summary>
public sealed record PlexServer(string MachineId, string Name, string AccessToken, bool Owned, IReadOnlyList<PlexConnection> Connections);

/// <summary>
/// plex.tv: PIN sign-in (the plex.tv/link flow, which works with a controller), the account's
/// servers, and choosing how to reach a server. Requests carry the X-Plex-* headers that
/// identify Lightcone as a client; the client identifier stays the same for an install.
/// </summary>
public sealed class PlexClient
{
    public const string PlexTv = "https://plex.tv";
    public const string LinkUrl = "https://plex.tv/link";

    private readonly HttpClient _http;

    public PlexClient(HttpClient http, string clientId)
    {
        _http = http;
        ClientId = clientId;
    }

    public string ClientId { get; }

    /// <summary>A request with Lightcone's client headers, plus the token when given.</summary>
    public HttpRequestMessage Request(HttpMethod method, string url, string? token = null)
    {
        var req = new HttpRequestMessage(method, url);
        var h = req.Headers;
        h.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        h.TryAddWithoutValidation("X-Plex-Client-Identifier", ClientId);
        h.TryAddWithoutValidation("X-Plex-Product", "Lightcone");
        h.TryAddWithoutValidation("X-Plex-Version", AppUpdater.DisplayVersion(AppUpdater.CurrentAppVersion()));
        h.TryAddWithoutValidation("X-Plex-Platform", Core.Platform.IsWindows ? "Windows" : Core.Platform.IsMacOS ? "macOS" : "Linux");
        h.TryAddWithoutValidation("X-Plex-Device", SteamSetup.InGameMode ? "Steam Deck" : "PC");
        h.TryAddWithoutValidation("X-Plex-Device-Name", DeviceName());
        h.TryAddWithoutValidation("X-Plex-Provides", "player");
        if (token != null)
        {
            h.TryAddWithoutValidation("X-Plex-Token", token);
        }
        return req;
    }

    private static string DeviceName()
    {
        var name = Environment.MachineName;
        return string.IsNullOrWhiteSpace(name) || name == "localhost" ? "Lightcone" : $"Lightcone ({name})";
    }

    public async Task<JsonDocument> GetJsonAsync(string url, string? token, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var req = Request(HttpMethod.Get, url, token);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t)
        {
            cts.CancelAfter(t);
        }
        using var resp = await _http.SendAsync(req, cts.Token);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new PlexAuthException();
        }
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
    }

    /// <summary>Downloads a file (seek preview index) with the token.</summary>
    public async Task<byte[]> GetBytesAsync(string url, string token, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, url, token);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>A plain GET/PUT for reporting (timeline, scrobble); errors are the caller's to ignore.</summary>
    public async Task SendAsync(HttpMethod method, string url, string token, CancellationToken ct = default)
    {
        using var req = Request(method, url, token);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>A Plex Companion command to a player (directly, or through the server), aimed with X-Plex-Target-Client-Identifier.</summary>
    public async Task SendToPlayerAsync(string url, string token, string target, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, url, token);
        req.Headers.TryAddWithoutValidation("X-Plex-Target-Client-Identifier", target);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));
        using var resp = await _http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
    }

    // ---- sign-in ----

    /// <summary>Asks plex.tv for a 4-character code to enter at plex.tv/link.</summary>
    public async Task<PlexPin> CreatePinAsync(CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, $"{PlexTv}/api/v2/pins");
        req.Content = new FormUrlEncodedContent([new("strong", "false")]);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return ParsePin(doc.RootElement);
    }

    internal static PlexPin ParsePin(JsonElement e)
    {
        var expiresIn = e.TryGetProperty("expiresIn", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 900;
        return new PlexPin(e.GetProperty("id").GetInt64(), e.GetProperty("code").GetString() ?? "", DateTime.UtcNow.AddSeconds(expiresIn));
    }

    /// <summary>The account token once the code was entered, else null (keep polling every couple of seconds).</summary>
    public async Task<string?> CheckPinAsync(PlexPin pin, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"{PlexTv}/api/v2/pins/{pin.Id}", null, ct);
        return doc.RootElement.TryGetProperty("authToken", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } token
            ? token
            : null;
    }

    /// <summary>The account's display name (also checks the token still works).</summary>
    public async Task<string> AccountNameAsync(string token, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"{PlexTv}/api/v2/user", token, ct);
        var u = doc.RootElement;
        return Str(u, "title") ?? Str(u, "username") ?? Str(u, "email") ?? "Plex";
    }

    // ---- servers ----

    public async Task<IReadOnlyList<PlexServer>> ServersAsync(string token, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"{PlexTv}/api/v2/resources?includeHttps=1&includeRelay=1&includeIPv6=1", token, ct);
        return ParseServers(doc.RootElement, token);
    }

    internal static IReadOnlyList<PlexServer> ParseServers(JsonElement root, string accountToken)
    {
        var servers = new List<PlexServer>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return servers;
        }
        foreach (var r in root.EnumerateArray())
        {
            if (!(Str(r, "provides") ?? "").Split(',').Contains("server") || Str(r, "clientIdentifier") is not { } id)
            {
                continue;
            }
            var conns = new List<PlexConnection>();
            if (r.TryGetProperty("connections", out var cs) && cs.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cs.EnumerateArray())
                {
                    if (Str(c, "uri") is { } uri && !Bool(c, "IPv6"))
                    {
                        conns.Add(new PlexConnection(uri.TrimEnd('/'), Bool(c, "local"), Bool(c, "relay")));
                    }
                }
            }
            servers.Add(new PlexServer(id, Str(r, "name") ?? "Plex", Str(r, "accessToken") ?? accountToken, Bool(r, "owned"), conns));
        }
        return servers;
    }

    /// <summary>
    /// Connections in the order worth trying: on the LAN first, then direct over the internet,
    /// then Plex's relay (slow, capped bitrate) as a last resort.
    /// </summary>
    public static IEnumerable<PlexConnection> Preferred(IEnumerable<PlexConnection> conns) =>
        conns.OrderBy(c => c.Relay ? 2 : c.Local ? 0 : 1);

    /// <summary>
    /// Tries the server's connections (all at once, a few seconds each) and returns the best one
    /// that answers, or null when none does.
    /// </summary>
    public async Task<string?> ReachAsync(PlexServer server, CancellationToken ct = default)
    {
        var ordered = Preferred(server.Connections).ToList();
        var tries = ordered.Select(c => Probe(c.Uri, server.AccessToken, ct)).ToList();
        for (var i = 0; i < tries.Count; i++)
        {
            if (await tries[i])
            {
                return ordered[i].Uri;
            }
        }
        return null;
    }

    private async Task<bool> Probe(string uri, string token, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJsonAsync($"{uri}/identity", token, ct, TimeSpan.FromSeconds(5));
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or PlexAuthException)
        {
            return false;
        }
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    internal static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.Number && v.GetRawText() != "0" || v.ValueKind == JsonValueKind.String && v.GetString() is "1" or "true");

    internal static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        } : null;
}

/// <summary>plex.tv or the server said the token is no longer valid (signed out elsewhere).</summary>
public sealed class PlexAuthException : Exception
{
    public PlexAuthException()
        : base("Plex sign-in expired. Sign in again in Sources.")
    {
    }
}
