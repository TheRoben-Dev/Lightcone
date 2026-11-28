using Lightcone.Host.Library;

namespace Lightcone.Host.Plex;

/// <summary>
/// The Plex account in Lightcone: sign-in, which servers show up as sources, refreshing them
/// and signing out. UI-free (both UI modes and the tests drive it).
/// </summary>
public sealed class PlexService
{
    private readonly MediaLibrary _library;
    private readonly string _accountFile;
    private readonly string _cacheDir;
    private readonly Dictionary<string, PlexSource> _sources = new();

    public PlexService(MediaLibrary library, string configDir, string dataDir, HttpClient? http = null)
    {
        _library = library;
        _accountFile = Path.Combine(configDir, "plex.json");
        _cacheDir = Path.Combine(dataDir, "plex");
        Account = PlexAccount.Load(_accountFile);
        Client = new PlexClient(http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) }, Account.ClientId);
        Watchlist = new PlexWatchlist(Client, Path.Combine(_cacheDir, "watchlist.json")) { Token = () => Account.Token };
        Watchlist.Changed += () => _library.NotifyChanged();
        _library.OnlineWatchlist = Watchlist;
        foreach (var s in Account.Servers)
        {
            AddSource(s);
        }
    }

    public PlexAccount Account { get; private set; }
    public PlexClient Client { get; }

    /// <summary>The account's plex.tv watchlist, synced with Lightcone's.</summary>
    public PlexWatchlist Watchlist { get; }

    public IReadOnlyCollection<PlexSource> Sources => _sources.Values;

    /// <summary>Refreshes every server in the background (at startup and from Sources).</summary>
    public Task RefreshAllAsync(CancellationToken ct = default) =>
        Task.WhenAll(_sources.Values.Select(s => s.RefreshAsync(ct)).Append(SyncWatchlistAsync(ct)));

    private int _syncing;

    /// <summary>
    /// Reads the plex.tv watchlist, then sends what was added on this device since the last sync
    /// (before signing in, or while offline), so the two lists end up the same. Removals made in
    /// other Plex apps stay removed: only newer local additions are sent.
    /// </summary>
    public async Task SyncWatchlistAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _syncing, 1) == 1)
        {
            return;
        }
        try
        {
            var since = Watchlist.SyncedAt;
            if (!await Watchlist.RefreshAsync(ct))
            {
                return;
            }
            var sent = 0;
            foreach (var id in _library.Lists.Watchlist())
            {
                if (since is { } t && _library.Lists.AddedToWatchlist(id) is { } at && at <= t)
                {
                    continue;
                }
                if (_library.Find(id) is { Guid: { } guid, Kind: MediaKind.Movie or MediaKind.Show } item && item.SourceId.StartsWith("plex:", StringComparison.Ordinal) &&
                    Watchlist.Has(guid) == false)
                {
                    Watchlist.Set(guid, true);
                    sent++;
                }
            }
            if (sent > 0)
            {
                AppLog.Info($"plex: sent {sent} watchlist title(s) added on this device");
            }
            if (_sources.Count > 0)
            {
                Watchlist.MarkSynced();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _syncing, 0);
        }
    }

    /// <summary>Syncs the watchlist when it's older than <paramref name="age"/> (returning to Home, the 5-minute timer).</summary>
    public void SyncWatchlistIfStale(TimeSpan age)
    {
        if (Account.Token != null && DateTime.UtcNow - Watchlist.LastRefresh > age)
        {
            _ = SyncWatchlistAsync();
        }
    }

    /// <summary>
    /// Waits for the code to be entered at plex.tv/link (polling every 2 s until it expires).
    /// Returns the token, or null if the code expired.
    /// </summary>
    public async Task<string?> WaitForLinkAsync(PlexPin pin, CancellationToken ct, TimeSpan? interval = null)
    {
        while (DateTime.UtcNow < pin.Expires)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await Client.CheckPinAsync(pin, ct) is { } token)
                {
                    return token;
                }
            }
            catch (HttpRequestException e)
            {
                AppLog.Warn($"plex: pin check failed, retrying: {e.Message}");
            }
            await Task.Delay(interval ?? TimeSpan.FromSeconds(2), ct);
        }
        return null;
    }

    /// <summary>Keeps the token and lists the account's servers (the caller lets the user pick).</summary>
    public async Task<IReadOnlyList<PlexServer>> SignInAsync(string token, CancellationToken ct = default)
    {
        var name = await Client.AccountNameAsync(token, ct);
        Account = Account with { Token = token, Name = name };
        Account.Save(_accountFile);
        AppLog.Info($"plex: signed in as {name}");
        _ = SyncWatchlistAsync(ct);
        return await Client.ServersAsync(token, ct);
    }

    public Task<IReadOnlyList<PlexServer>> ServersAsync(CancellationToken ct = default) =>
        Account.Token is { } t ? Client.ServersAsync(t, ct) : Task.FromResult<IReadOnlyList<PlexServer>>([]);

    /// <summary>Shows exactly these servers as sources (new ones are added and refreshed, unchecked ones removed).</summary>
    public async Task UseServersAsync(IEnumerable<PlexServer> servers, CancellationToken ct = default)
    {
        var chosen = servers.ToList();
        var keep = chosen.Select(s => s.MachineId).ToHashSet();
        foreach (var id in _sources.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            RemoveSource(id);
        }
        var entries = chosen.Select(s =>
            new PlexServerEntry(s.MachineId, s.Name, s.AccessToken, s.Connections,
                _sources.TryGetValue(s.MachineId, out var old) ? old.Entry.LastUri : null)).ToList();
        Account = Account with { Servers = entries };
        Account.Save(_accountFile);
        var fresh = new List<PlexSource>();
        foreach (var e in entries.Where(e => !_sources.ContainsKey(e.MachineId)))
        {
            fresh.Add(AddSource(e));
        }
        await Task.WhenAll(fresh.Select(s => s.RefreshAsync(ct)));
    }

    public void RemoveServer(string machineId)
    {
        RemoveSource(machineId);
        Account = Account with { Servers = Account.Servers.Where(s => s.MachineId != machineId).ToList() };
        Account.Save(_accountFile);
    }

    /// <summary>Forgets the token and every Plex server (cached items too). Progress on this device stays.</summary>
    public void SignOut()
    {
        foreach (var id in _sources.Keys.ToList())
        {
            RemoveSource(id);
        }
        Account = new PlexAccount { ClientId = Account.ClientId };
        Account.Save(_accountFile);
        Watchlist.Clear();
        AppLog.Info("plex: signed out");
    }

    private PlexSource AddSource(PlexServerEntry entry)
    {
        var source = new PlexSource(Client, entry, Path.Combine(_cacheDir, entry.MachineId + ".json"));
        source.EntryChanged += OnEntryChanged;
        source.Watchlist = Watchlist;
        _sources[entry.MachineId] = source;
        _library.Add(source);
        return source;
    }

    private void RemoveSource(string machineId)
    {
        if (_sources.Remove(machineId, out var s))
        {
            s.EntryChanged -= OnEntryChanged;
            s.DeleteCache();
            _library.Remove(s.Id);
        }
    }

    private void OnEntryChanged(PlexServerEntry entry)
    {
        lock (_sources)
        {
            Account = Account with { Servers = Account.Servers.Select(s => s.MachineId == entry.MachineId ? entry : s).ToList() };
            Account.Save(_accountFile);
        }
    }
}
