using Lightcone.Host.Library;

namespace Lightcone.Host.Jellyfin;

/// <summary>
/// Jellyfin in Lightcone: finding servers (on this network or by address), signing in (Quick
/// Connect or user name and password), the servers' libraries as sources, and removing them.
/// UI-free (both UI modes and the tests drive it).
/// </summary>
public sealed class JellyfinService
{
    private readonly MediaLibrary _library;
    private readonly string _accountFile;
    private readonly string _cacheDir;
    private readonly Dictionary<string, JellyfinSource> _sources = new();

    public JellyfinService(MediaLibrary library, string configDir, string dataDir, HttpClient? http = null)
    {
        _library = library;
        _accountFile = Path.Combine(configDir, "jellyfin.json");
        _cacheDir = Path.Combine(dataDir, "jellyfin");
        Account = JellyfinAccount.Load(_accountFile);
        Client = new JellyfinClient(http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) }, Account.DeviceId);
        foreach (var s in Account.Servers)
        {
            AddSource(s);
        }
    }

    public JellyfinAccount Account { get; private set; }
    public JellyfinClient Client { get; }

    public IReadOnlyCollection<JellyfinSource> Sources => _sources.Values;

    public Task RefreshAllAsync(CancellationToken ct = default) => Task.WhenAll(_sources.Values.Select(s => s.RefreshAsync(ct)));

    /// <summary>Waits for the Quick Connect code to be approved (polling every 2 s, up to 10 minutes). Null if it never was.</summary>
    public async Task<JellyfinLogin?> WaitForQuickConnectAsync(JellyfinServerInfo server, QuickConnectCode code, CancellationToken ct, TimeSpan? interval = null)
    {
        var until = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await Client.CheckQuickConnectAsync(server.Url, code, ct))
                {
                    return await Client.AuthenticateQuickConnectAsync(server.Url, code, ct);
                }
            }
            catch (HttpRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
            {
                return null;  // the code expired on the server
            }
            catch (HttpRequestException e)
            {
                AppLog.Warn($"jellyfin: quick connect check failed, retrying: {e.Message}");
            }
            await Task.Delay(interval ?? TimeSpan.FromSeconds(2), ct);
        }
        return null;
    }

    /// <summary>Adds (or re-signs-in) a server and loads its libraries.</summary>
    public async Task<JellyfinSource> AddServerAsync(JellyfinServerInfo server, JellyfinLogin login, CancellationToken ct = default)
    {
        var entry = new JellyfinServerEntry(server.Id, server.Name, server.Url, login.UserId, login.UserName, login.Token);
        RemoveSource(server.Id);
        Account = Account with { Servers = Account.Servers.Where(s => s.ServerId != server.Id).Append(entry).ToList() };
        Account.Save(_accountFile);
        AppLog.Info($"jellyfin: signed in to {server.Name} as {login.UserName}");
        var source = AddSource(entry);
        await source.RefreshAsync(ct);
        return source;
    }

    /// <summary>Signs out on the server (best effort) and forgets it, cached items too.</summary>
    public void RemoveServer(string serverId)
    {
        if (Account.Servers.FirstOrDefault(s => s.ServerId == serverId) is { } entry)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var _ = await Client.SendAsync(HttpMethod.Post, $"{entry.Url}/Sessions/Logout", entry.Token, timeout: TimeSpan.FromSeconds(5));
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JellyfinAuthException or System.Text.Json.JsonException)
                {
                }
            });
        }
        RemoveSource(serverId);
        Account = Account with { Servers = Account.Servers.Where(s => s.ServerId != serverId).ToList() };
        Account.Save(_accountFile);
        AppLog.Info("jellyfin: removed a server");
    }

    private JellyfinSource AddSource(JellyfinServerEntry entry)
    {
        var source = new JellyfinSource(Client, entry, Path.Combine(_cacheDir, entry.ServerId + ".json"));
        _sources[entry.ServerId] = source;
        _library.Add(source);
        return source;
    }

    private void RemoveSource(string serverId)
    {
        if (_sources.Remove(serverId, out var s))
        {
            s.DeleteCache();
            _library.Remove(s.Id);
        }
    }
}
