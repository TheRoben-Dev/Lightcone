namespace Lightcone.Host.Library;

public enum SortKey
{
    Title,
    Year,
    Added,
    Duration,
}

/// <summary>
/// Every source's items in one place, plus what the home screen shows. Both UI modes read
/// from here, which is what keeps them at feature parity.
/// </summary>
public sealed class MediaLibrary
{
    // Copy-on-write: sources are added from background work (a Plex sign-in) while the UI reads.
    private volatile IReadOnlyList<IMediaSource> _sources = [];
    private readonly object _gate = new();

    public MediaLibrary(ProgressStore progress, UserLists? lists = null)
    {
        Progress = progress;
        Lists = lists ?? new UserLists(null);
        Lists.Changed += OnSourceChanged;
    }

    public ProgressStore Progress { get; }

    /// <summary>The watchlist and collections kept on this device.</summary>
    public UserLists Lists { get; }

    private bool _music = true;

    /// <summary>
    /// Whether music shows anywhere (its tab, Home rows, search, continue listening). Movies and
    /// TV are what Lightcone is about; music can be switched off in Settings.
    /// </summary>
    public bool Music
    {
        get => _music;
        set
        {
            if (_music != value)
            {
                _music = value;
                OnSourceChanged();
            }
        }
    }

    private bool _auto = true;

    /// <summary>Whether automatic collections (genres, decades) are made.</summary>
    public bool AutoCollections
    {
        get => _auto;
        set
        {
            if (_auto != value)
            {
                _auto = value;
                OnSourceChanged();
            }
        }
    }

    private bool Visible(MediaItem i) => _music || !i.IsMusic;

    public IReadOnlyList<IMediaSource> Sources => _sources;

    public event Action? Changed;

    public void Add(IMediaSource source)
    {
        lock (_gate)
        {
            foreach (var old in _sources.Where(s => s.Id == source.Id))
            {
                old.Changed -= OnSourceChanged;
            }
            _sources = _sources.Where(s => s.Id != source.Id).Append(source).ToList();
            source.Changed += OnSourceChanged;
        }
        OnSourceChanged();
    }

    public void Remove(string sourceId)
    {
        bool removed;
        lock (_gate)
        {
            var gone = _sources.Where(s => s.Id == sourceId).ToList();
            foreach (var old in gone)
            {
                old.Changed -= OnSourceChanged;
            }
            removed = gone.Count > 0;
            _sources = _sources.Where(s => s.Id != sourceId).ToList();
        }
        if (removed)
        {
            OnSourceChanged();
        }
    }

    private void OnSourceChanged()
    {
        _collections = null;
        Changed?.Invoke();
    }

    /// <summary>Tells the UI something changed that isn't a source refresh (a synced watchlist).</summary>
    public void NotifyChanged() => OnSourceChanged();

    private IMediaSource? SourceOf(MediaItem item) => _sources.FirstOrDefault(s => s.Id == item.SourceId);

    public IEnumerable<MediaItem> All => _sources.SelectMany(s => s.Items);

    public MediaItem? Find(string id) =>
        IsCollection(id) ? Collections().FirstOrDefault(c => c.Id == id) : All.FirstOrDefault(i => i.Id == id);

    /// <summary>Top-level items of a section: movies, shows, albums (tracks and episodes live under their parent).</summary>
    public IReadOnlyList<MediaItem> Section(MediaKind kind, SortKey sort = SortKey.Title, bool descending = false) =>
        Sort(All.Where(i => i.Kind == kind && Visible(i)), sort, descending);

    public IReadOnlyList<MediaItem> Children(string parentId) =>
        IsCollection(parentId) ? CollectionItems(parentId) : All.Where(i => i.ParentId == parentId).OrderBy(i => i.Season ?? 0).ThenBy(i => i.Index ?? int.MaxValue).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static IReadOnlyList<MediaItem> Sort(IEnumerable<MediaItem> items, SortKey sort, bool descending)
    {
        var titleCmp = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<MediaItem> ordered = sort switch
        {
            SortKey.Year => items.OrderBy(i => i.Year ?? 0),
            SortKey.Added => items.OrderBy(i => i.Added),
            SortKey.Duration => items.OrderBy(i => i.Duration ?? 0),
            _ => items.OrderBy(i => SortTitle(i.Title), titleCmp),
        };
        if (descending)
        {
            ordered = sort switch
            {
                SortKey.Year => items.OrderByDescending(i => i.Year ?? 0),
                SortKey.Added => items.OrderByDescending(i => i.Added),
                SortKey.Duration => items.OrderByDescending(i => i.Duration ?? 0),
                _ => items.OrderByDescending(i => SortTitle(i.Title), titleCmp),
            };
        }
        return ordered.ThenBy(i => SortTitle(i.Title), titleCmp).ToList();
    }

    /// <summary>"The Matrix" sorts under M.</summary>
    public static string SortTitle(string title)
    {
        foreach (var article in new[] { "The ", "A ", "An " })
        {
            if (title.StartsWith(article, StringComparison.OrdinalIgnoreCase) && title.Length > article.Length)
            {
                return title[article.Length..];
            }
        }
        return title;
    }

    /// <summary>
    /// Resume position: the more recent of this device's and the server's (so a show paused on
    /// the TV resumes there on the Deck), or null when there is nothing to resume.
    /// </summary>
    public double? PositionOf(MediaItem item)
    {
        var local = Progress.Get(item.Id);
        var server = SourceOf(item)?.ServerProgress(item.Id);
        if (local != null && (server == null || local.At >= server.At))
        {
            return local.Position;
        }
        return server?.Position;
    }

    /// <summary>Watched here, or on the server (any device).</summary>
    public bool IsWatched(MediaItem item) => Progress.IsWatched(item.Id) || SourceOf(item)?.ServerWatched(item.Id) == true;

    public bool IsWatched(string id) => Find(id) is { } item ? IsWatched(item) : Progress.IsWatched(id);

    public void SetWatched(MediaItem item, bool watched)
    {
        Progress.SetWatched(item.Id, watched);
        SourceOf(item)?.OnWatched(item, watched);
    }

    /// <summary>Records playback progress here and tells the item's server.</summary>
    public void Report(MediaItem item, double position, double? duration, PlaybackState state = PlaybackState.Playing)
    {
        Progress.Report(item.Id, position, duration);
        SourceOf(item)?.OnProgress(item, position, duration, state);
    }

    public Task PrepareAsync(MediaItem item, CancellationToken ct = default) => SourceOf(item)?.PrepareAsync(item, ct) ?? Task.CompletedTask;

    /// <summary>Items with a resume position here or on a server, most recent first.</summary>
    public IReadOnlyList<MediaItem> InProgress()
    {
        var at = new Dictionary<string, DateTime>();
        foreach (var id in Progress.InProgress())
        {
            if (Progress.Get(id) is { } p)
            {
                at[id] = p.At;
            }
        }
        foreach (var s in _sources)
        {
            foreach (var i in s.Items)
            {
                if (s.ServerProgress(i.Id) is { } p && (!at.TryGetValue(i.Id, out var t) || p.At > t))
                {
                    at[i.Id] = p.At;
                }
            }
        }
        return at.OrderByDescending(kv => kv.Value).Select(kv => Find(kv.Key)).OfType<MediaItem>()
            .Where(i => i.Playable && Visible(i) && PositionOf(i) != null && !IsWatched(i)).ToList();
    }

    public IReadOnlyList<Marker> MarkersOf(MediaItem item) =>
        _sources.FirstOrDefault(s => s.Id == item.SourceId)?.MarkersOf(item.Id) ?? [];

    /// <summary>Case- and accent-insensitive search over titles, subtitles and genres; top-level and playable items.</summary>
    public IReadOnlyList<MediaItem> Search(string query, int max = 60)
    {
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return [];
        }
        var collections = Collections().Where(c => !c.Id.StartsWith(AutoPrefix, StringComparison.Ordinal));
        return All.Where(i => (i.Kind is not MediaKind.Episode || i.ParentId == null) && Visible(i)).Concat(collections)
            .Select(i => (Item: i, Text: Fold($"{i.Title} {i.Subtitle} {i.ParentTitle} {string.Join(' ', i.Genres)} {i.Year}")))
            .Where(x => words.All(w => x.Text.Contains(w, StringComparison.Ordinal)))
            .OrderBy(x => Fold(x.Item.Title).StartsWith(words[0], StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(x => x.Item.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(max).Select(x => x.Item).ToList();
    }

    private static string Fold(string s)
    {
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }

    /// <summary>Home screen rows: continue watching, recently added, then one row per source.</summary>
    public IReadOnlyList<HomeRow> HomeRows(int perRow = 20)
    {
        var rows = new List<HomeRow>();
        var resume = InProgress().Take(perRow).ToList();
        if (resume.Count > 0)
        {
            rows.Add(new HomeRow("continue", "Continue watching", resume, Wide: true));
        }
        var watchlist = Watchlist().Take(perRow).ToList();
        if (watchlist.Count > 0)
        {
            rows.Add(new HomeRow("watchlist", "Watchlist", watchlist));
        }
        var recent = All.Where(i => i.Kind is MediaKind.Movie or MediaKind.Show or MediaKind.Album && Visible(i))
            .OrderByDescending(i => i.Added).Take(perRow).ToList();
        if (recent.Count > 0)
        {
            rows.Add(new HomeRow("recent", "Recently added", recent));
        }
        // Collections from servers and ones made here (the automatic ones live in Movies / Shows).
        var collections = Collections().Where(c => !c.Id.StartsWith(AutoPrefix, StringComparison.Ordinal)).Take(perRow).ToList();
        if (collections.Count > 0)
        {
            rows.Add(new HomeRow("collections", "Collections", collections));
        }
        var kinds = _music ? new[] { MediaKind.Movie, MediaKind.Show, MediaKind.Album } : [MediaKind.Movie, MediaKind.Show];
        foreach (var s in _sources)
        {
            foreach (var kind in kinds)
            {
                var items = s.Items.Where(i => i.Kind == kind).OrderByDescending(i => i.Added).Take(perRow).ToList();
                if (items.Count > 0)
                {
                    var what = kind switch { MediaKind.Movie => "Movies", MediaKind.Show => "Shows", _ => "Music" };
                    rows.Add(new HomeRow($"{s.Id}:{kind}", $"{what} · {s.Name}", items));
                }
            }
        }
        return rows;
    }

    // ---- watchlist ----

    /// <summary>On the watchlist: the server's (when it keeps one) or this device's.</summary>
    public bool OnWatchlist(MediaItem item) => SourceOf(item)?.ServerWatchlist(item) ?? Lists.OnWatchlist(item.Id);

    public void SetWatchlist(MediaItem item, bool on)
    {
        SourceOf(item)?.OnWatchlist(item, on);
        Lists.SetWatchlist(item.Id, on);  // raises Changed
    }

    /// <summary>Movies and shows to watch, most recently added first; watched movies drop off.</summary>
    public IReadOnlyList<MediaItem> Watchlist()
    {
        var local = Lists.Watchlist();
        var order = local.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return All.Where(i => i.Kind is MediaKind.Movie or MediaKind.Show && Visible(i) && OnWatchlist(i))
            .Where(i => i.Kind != MediaKind.Movie || !IsWatched(i))
            .OrderBy(i => order.TryGetValue(i.Id, out var n) ? n : int.MaxValue)
            .ThenByDescending(i => i.Added)
            .ToList();
    }

    // ---- collections ----

    public const string CollectionSourceId = "collections";
    private const string Prefix = "collection:", UserPrefix = "collection:user:", ServerPrefix = "collection:server:";
    private const string AutoPrefix = "collection:auto:";

    private volatile IReadOnlyList<(MediaItem Item, IReadOnlyList<MediaItem> Members)>? _collections;

    public static bool IsCollection(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>A collection made in Lightcone (it can be renamed, changed and deleted).</summary>
    public static bool IsUserCollection(string id) => id.StartsWith(UserPrefix, StringComparison.Ordinal);

    public static bool IsAutoCollection(string id) => id.StartsWith(AutoPrefix, StringComparison.Ordinal);

    public static string UserCollectionId(MediaItem collection) => collection.Id[UserPrefix.Length..];

    /// <summary>
    /// Collections of movies and shows: made here first, then the servers' (Plex collections,
    /// Jellyfin box sets), then automatic ones by genre and decade. Optionally only those holding
    /// <paramref name="of"/> items.
    /// </summary>
    public IReadOnlyList<MediaItem> Collections(MediaKind? of = null)
    {
        var all = _collections ??= BuildCollections();
        return all.Where(c => of == null || c.Members.Any(m => m.Kind == of)).Select(c => c.Item).ToList();
    }

    public IReadOnlyList<MediaItem> CollectionItems(string id) =>
        (_collections ??= BuildCollections()).FirstOrDefault(c => c.Item.Id == id).Members ?? [];

    /// <summary>The collections made here that hold the item.</summary>
    public IReadOnlyList<UserCollection> UserCollectionsWith(MediaItem item) =>
        Lists.Collections.Where(c => c.Items.Contains(item.Id)).ToList();

    private IReadOnlyList<(MediaItem, IReadOnlyList<MediaItem>)> BuildCollections()
    {
        var titles = All.Where(i => i.Kind is MediaKind.Movie or MediaKind.Show).ToList();
        var byId = titles.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        var result = new List<(MediaItem, IReadOnlyList<MediaItem>)>();
        void Add(string id, string name, IEnumerable<MediaItem> members, string from, string? overview = null, bool keepEmpty = false)
        {
            var list = members.DistinctBy(m => m.Id).ToList();
            if (list.Count == 0 && !keepEmpty)
            {
                return;
            }
            var ordered = id.StartsWith(UserPrefix, StringComparison.Ordinal)
                ? list
                : list.OrderBy(m => m.Year ?? int.MaxValue).ThenBy(m => SortTitle(m.Title), StringComparer.CurrentCultureIgnoreCase).ToList();
            var art = ordered.FirstOrDefault(m => m.Poster != null || m.PosterUrl != null);
            var movies = ordered.Count(m => m.Kind == MediaKind.Movie);
            var what = movies == ordered.Count ? (movies == 1 ? "movie" : "movies") : movies == 0 ? (ordered.Count == 1 ? "show" : "shows") : ordered.Count == 1 ? "title" : "titles";
            result.Add((new MediaItem
            {
                Id = id,
                Title = name,
                Kind = MediaKind.Collection,
                Subtitle = $"{ordered.Count} {what}",
                Overview = overview,
                Added = ordered.Count > 0 ? ordered.Max(m => m.Added) : DateTime.MinValue,
                SourceId = CollectionSourceId,
                SourceName = from,
                Poster = art?.Poster,
                PosterUrl = art?.PosterUrl,
                Genres = ordered.SelectMany(m => m.Genres).GroupBy(g => g).OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key).ToList(),
            }, ordered));
        }
        foreach (var c in Lists.Collections)
        {
            Add(UserPrefix + c.Id, c.Name, c.Items.Select(i => byId.GetValueOrDefault(i)).OfType<MediaItem>(), "Lightcone", "A collection you made in Lightcone.", keepEmpty: true);
        }
        foreach (var g in titles.SelectMany(t => t.Collections.Select(name => (name, t))).GroupBy(x => x.name, StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(g => SortTitle(g.Key), StringComparer.CurrentCultureIgnoreCase))
        {
            var sources = g.Select(x => x.t.SourceName).Distinct().ToList();
            Add(ServerPrefix + Slug(g.Key), g.Key, g.Select(x => x.t), string.Join(", ", sources));
        }
        // Automatic: genres and decades with enough in them to be worth browsing.
        const int min = 4;
        foreach (var kind in _auto ? new[] { MediaKind.Movie, MediaKind.Show } : [])
        {
            var what = kind == MediaKind.Movie ? "movies" : "shows";
            var ofKind = titles.Where(t => t.Kind == kind).ToList();
            foreach (var g in ofKind.SelectMany(t => t.Genres.Select(genre => (genre, t))).GroupBy(x => x.genre, StringComparer.CurrentCultureIgnoreCase)
                         .Where(g => g.Count() >= min).OrderByDescending(g => g.Count()))
            {
                Add($"{AutoPrefix}{what}:genre:{Slug(g.Key)}", $"{g.Key} {what}", g.Select(x => x.t).OrderByDescending(t => t.Added), "Lightcone (automatic)");
            }
            foreach (var g in ofKind.Where(t => t.Year is > 1800).GroupBy(t => t.Year!.Value / 10 * 10).Where(g => g.Count() >= min).OrderByDescending(g => g.Key))
            {
                Add($"{AutoPrefix}{what}:decade:{g.Key}", $"{g.Key}s {what}", g, "Lightcone (automatic)");
            }
        }
        return result;
    }

    private static string Slug(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}
