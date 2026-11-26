using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Lightcone.Host.Updates;

namespace Lightcone.Host.Jellyfin;

/// <summary>What a Jellyfin server says about itself before sign-in.</summary>
public sealed record JellyfinServerInfo(string Url, string Id, string Name, string? Version);

/// <summary>A signed-in user on one server.</summary>
public sealed record JellyfinLogin(string UserId, string UserName, string Token);

/// <summary>A Quick Connect request: the code to enter on a signed-in device, and the secret to poll with.</summary>
public sealed record QuickConnectCode(string Secret, string Code);

/// <summary>
/// The Jellyfin REST API, the parts Lightcone uses. Every request carries Lightcone's client
/// headers (one device id per install) and the user's token once signed in.
/// </summary>
public sealed class JellyfinClient
{
    public const int DiscoveryPort = 7359;

    private readonly HttpClient _http;

    public JellyfinClient(HttpClient http, string deviceId)
    {
        _http = http;
        DeviceId = deviceId;
    }

    public string DeviceId { get; }

    private static string Quote(string s) => s.Replace("\"", "'", StringComparison.Ordinal);

    public HttpRequestMessage Request(HttpMethod method, string url, string? token = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var device = SteamSetup.InGameMode ? "Steam Deck" : Environment.MachineName is { Length: > 0 } m && m != "localhost" ? m : "PC";
        var auth = $"MediaBrowser Client=\"Lightcone\", Device=\"{Quote(device)}\", DeviceId=\"{DeviceId}\", " +
                   $"Version=\"{AppUpdater.DisplayVersion(AppUpdater.CurrentAppVersion())}\"" + (token != null ? $", Token=\"{token}\"" : "");
        req.Headers.TryAddWithoutValidation("Authorization", auth);
        return req;
    }

    public async Task<JsonDocument> GetJsonAsync(string url, string? token, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var req = Request(HttpMethod.Get, url, token);
        return await SendForJsonAsync(req, ct, timeout) ?? throw new JsonException("empty response");
    }

    /// <summary>A request with an optional JSON body; returns the JSON answer, or null when there is none.</summary>
    public async Task<JsonDocument?> SendAsync(HttpMethod method, string url, string? token, object? body = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        using var req = Request(method, url, token);
        if (body != null)
        {
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }
        else if (method == HttpMethod.Post)
        {
            req.Content = new StringContent("", Encoding.UTF8, "application/json");
        }
        return await SendForJsonAsync(req, ct, timeout);
    }

    private async Task<JsonDocument?> SendForJsonAsync(HttpRequestMessage req, CancellationToken ct, TimeSpan? timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t)
        {
            cts.CancelAfter(t);
        }
        using var resp = await _http.SendAsync(req, cts.Token);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new JellyfinAuthException();
        }
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(cts.Token);
        return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
    }

    // ---- finding a server ----

    /// <summary>
    /// Addresses to try for what someone typed: "192.168.1.20" → http://192.168.1.20:8096,
    /// http://192.168.1.20, https://192.168.1.20; a full URL is tried as is.
    /// </summary>
    public static IReadOnlyList<string> CandidateUrls(string input)
    {
        var s = input.Trim().TrimEnd('/');
        if (s.Length == 0)
        {
            return [];
        }
        if (s.Contains("://", StringComparison.Ordinal))
        {
            return [s];
        }
        var host = s.Split('/')[0];
        var hasPort = host.LastIndexOf(':') > host.LastIndexOf(']');
        return hasPort ? [$"http://{s}", $"https://{s}"] : [$"http://{s.Insert(host.Length, ":8096")}", $"https://{s}", $"http://{s}"];
    }

    public async Task<JellyfinServerInfo> PublicInfoAsync(string url, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"{url}/System/Info/Public", null, ct, TimeSpan.FromSeconds(6));
        var r = doc.RootElement;
        var id = Str(r, "Id") ?? throw new JsonException("not a Jellyfin server");
        return new JellyfinServerInfo(url, id, Str(r, "ServerName") ?? "Jellyfin", Str(r, "Version"));
    }

    /// <summary>The first address (of what was typed) that answers as a Jellyfin server.</summary>
    public async Task<JellyfinServerInfo> FindServerAsync(string input, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var url in CandidateUrls(input))
        {
            try
            {
                return await PublicInfoAsync(url, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                last = e;
            }
        }
        throw new HttpRequestException($"No Jellyfin server answered at {input.Trim()}." + (last != null ? $" ({last.Message})" : ""));
    }

    /// <summary>Asks the local network who runs Jellyfin (its UDP discovery on port 7359).</summary>
    public static async Task<IReadOnlyList<(string Url, string Id, string Name)>> DiscoverAsync(TimeSpan wait, CancellationToken ct = default)
    {
        var found = new List<(string, string, string)>();
        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;
            var msg = Encoding.UTF8.GetBytes("Who is JellyfinServer?");
            await udp.SendAsync(msg, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort), ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(wait);
            while (!cts.IsCancellationRequested)
            {
                var res = await udp.ReceiveAsync(cts.Token);
                if (ParseDiscovery(Encoding.UTF8.GetString(res.Buffer)) is { } s && found.All(f => f.Item2 != s.Id))
                {
                    found.Add(s);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException e)
        {
            AppLog.Warn($"jellyfin: discovery unavailable: {e.Message}");
        }
        return found;
    }

    internal static (string Url, string Id, string Name)? ParseDiscovery(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return Str(r, "Address") is { } a && Str(r, "Id") is { } id ? (a.TrimEnd('/'), id, Str(r, "Name") ?? "Jellyfin") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- sign-in ----

    public async Task<bool> QuickConnectEnabledAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync($"{url}/QuickConnect/Enabled", null, ct, TimeSpan.FromSeconds(6));
            return doc.RootElement.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return false;
        }
    }

    public async Task<QuickConnectCode> InitiateQuickConnectAsync(string url, CancellationToken ct = default)
    {
        JsonDocument? doc;
        try
        {
            doc = await SendAsync(HttpMethod.Post, $"{url}/QuickConnect/Initiate", null, ct: ct);
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound)
        {
            doc = await GetJsonAsync($"{url}/QuickConnect/Initiate", null, ct);  // servers before 10.9
        }
        using (doc)
        {
            var r = doc?.RootElement ?? throw new JsonException("empty Quick Connect answer");
            return new QuickConnectCode(Str(r, "Secret") ?? throw new JsonException("no secret"), Str(r, "Code") ?? throw new JsonException("no code"));
        }
    }

    /// <summary>True once the code was entered on a signed-in device.</summary>
    public async Task<bool> CheckQuickConnectAsync(string url, QuickConnectCode code, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"{url}/QuickConnect/Connect?secret={Uri.EscapeDataString(code.Secret)}", null, ct, TimeSpan.FromSeconds(10));
        return Bool(doc.RootElement, "Authenticated");
    }

    public async Task<JellyfinLogin> AuthenticateQuickConnectAsync(string url, QuickConnectCode code, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"{url}/Users/AuthenticateWithQuickConnect", null, new { Secret = code.Secret }, ct);
        return ParseLogin(doc);
    }

    public async Task<JellyfinLogin> AuthenticateAsync(string url, string user, string password, CancellationToken ct = default)
    {
        try
        {
            using var doc = await SendAsync(HttpMethod.Post, $"{url}/Users/AuthenticateByName", null, new { Username = user, Pw = password }, ct);
            return ParseLogin(doc);
        }
        catch (JellyfinAuthException)
        {
            throw new InvalidOperationException("Wrong user name or password.");
        }
    }

    internal static JellyfinLogin ParseLogin(JsonDocument? doc)
    {
        var r = doc?.RootElement ?? throw new JsonException("empty sign-in answer");
        var token = Str(r, "AccessToken") ?? throw new JsonException("no token");
        var user = r.TryGetProperty("User", out var u) ? u : default;
        return new JellyfinLogin(Str(user, "Id") ?? throw new JsonException("no user"), Str(user, "Name") ?? "", token);
    }

    // ---- JSON helpers ----

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    internal static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    internal static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        } : null;
}

/// <summary>The server refused the token (signed out elsewhere, or the user was removed).</summary>
public sealed class JellyfinAuthException : Exception
{
    public JellyfinAuthException()
        : base("Jellyfin signed this device out. Remove the server and sign in again.")
    {
    }
}
