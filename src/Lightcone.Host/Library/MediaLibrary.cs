namespace Lightcone.Host.Library;

public enum SortKey
{
    Title,
    Year,
    Added,
    Duration,
}

/// <summary>Which titles a Movies or Shows page lists, by watched state.</summary>
public enum WatchFilter
{
    All,

    /// <summary>Movies not watched; shows with episodes left.</summary>
    Unwatched,

    /// <summary>Movies part watched; shows started but not finished.</summary>
    InProgress,

    /// <summary>Movies watched; shows with every episode watched.</summary>
    Watched,
}

/// <summary>
/// Every source's items in one place, plus what the home screen shows. Both UI modes read
/// from here, which is what keeps them at feature parity.
/// </summary>
public sealed partial class MediaLibrary
{
    // Copy-on-write: sources are added from background work (a Plex sign-in) while the UI reads.
    private volatile IReadOnlyList<IMediaSource> _sources = [];
    private readonly object _gate = new();

    public MediaLibrary(ProgressStore progress, UserLists? lists = null, WatchHistory? history = null)
    {
        Progress = progress;
        Lists = lists ?? new UserLists(null);
        History = history ?? new WatchHistory(null);
        Lists.Changed += OnSourceChanged;
    }

    public ProgressStore Progress { get; }

    /// <summary>The watchlist and collections kept on this device.</summary>
    public UserLists Lists { get; }

    /// <summary>Time watched in Lightcone, per item per day (the stats page).</summary>
    public WatchHistory History { get; }

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

    /// <summary>Whether automatic collections (franchises, directors, genres, decades) are made.</summary>
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
        _byParent = null;
        Changed?.Invoke();
    }

    /// <summary>Tells the UI something changed that isn't a source refresh (a synced watchlist).</summary>
    public void NotifyChanged() => OnSourceChanged();

    private IMediaSource? SourceOf(MediaItem item) => _sources.FirstOrDefault(s => s.Id == item.SourceId);

    public IEnumerable<MediaItem> All => _sources.SelectMany(s => s.Items);

    public MediaItem? Find(string id) =>
        IsCollection(id) ? Collections().FirstOrDefault(c => c.Id == id)
        : id.StartsWith(WatchlistSourceId + ":", StringComparison.Ordinal) ? NotInLibrary().FirstOrDefault(i => i.Id == id)
        : All.FirstOrDefault(i => i.Id == id);

    /// <summary>Top-level items of a section: movies, shows, albums (tracks and episodes live under their parent).</summary>
    public IReadOnlyList<MediaItem> Section(MediaKind kind, SortKey sort = SortKey.Title, bool descending = false, WatchFilter filter = WatchFilter.All) =>
        SortWithShows(All.Where(i => i.Kind == kind && Visible(i) && Matches(i, filter)), sort, descending);

    /// <summary>Whether a movie or show passes a watched-state filter (anything else always does).</summary>
    public bool Matches(MediaItem item, WatchFilter filter)
    {
        if (filter == WatchFilter.All || item.Kind is not (MediaKind.Movie or MediaKind.Show))
        {
            return true;
        }
        if (item.Kind == MediaKind.Movie)
        {
            var watched = IsWatched(item);
            return filter switch
            {
                WatchFilter.Unwatched => !watched,
                WatchFilter.InProgress => !watched && PositionOf(item) != null,
                _ => watched,
            };
        }
        var eps = Children(item.Id).Where(e => e.Playable).ToList();
        var seen = eps.Count(IsWatched);
        var left = eps.Count - seen;
        return filter switch
        {
            WatchFilter.Unwatched => left > 0 || eps.Count == 0,
            WatchFilter.InProgress => left > 0 && (seen > 0 || eps.Any(e => PositionOf(e) != null)),
            _ => eps.Count > 0 && left == 0,
        };
    }

    /// <summary>
    /// When the item was last played, on this device or (per its server) anywhere: finished,
    /// paused part way, or the server's last-played date. Null when it never was, as far as we know.
    /// </summary>
    public DateTime? LastViewed(MediaItem item)
    {
        DateTime? best = null;
        void Take(DateTime? t)
        {
            if (t is { } v && v > DateTime.MinValue && (best == null || v > best))
            {
                best = v;
            }
        }
        Take(Progress.WatchedAt(item.Id));
        Take(Progress.Get(item.Id)?.At);
        var source = SourceOf(item);
        Take(source?.ServerLastViewed(item.Id));
        Take(source?.ServerProgress(item.Id)?.At);
        return best;
    }

    /// <summary>When any episode of a show was last played (see <see cref="LastViewed"/>).</summary>
    public DateTime? ShowLastViewed(MediaItem show) =>
        Children(show.Id).Select(LastViewed).Where(t => t != null).DefaultIfEmpty(null).Max();

    /// <summary>The episode before this one in its show (by season and number), or null for the first.</summary>
    public MediaItem? PreviousEpisode(MediaItem episode)
    {
        if (episode.ParentId == null)
        {
            return null;
        }
        var eps = Children(episode.ParentId).Where(e => e.Playable && (e.Season != 0 || episode.Season == 0)).ToList();
        var i = eps.FindIndex(e => e.Id == episode.Id);
        return i > 0 ? eps[i - 1] : null;
    }

    /// <summary>
    /// "What should I watch?": a random unwatched movie, or a show with episodes left (its next
    /// episode is what plays). Watchlist titles and shows already started come up more often.
    /// <paramref name="maxMinutes"/> limits a movie's length, or a show's episode length.
    /// </summary>
    public MediaItem? Surprise(Random random, bool movies = true, bool shows = true, int? maxMinutes = null, ICollection<string>? skip = null)
    {
        var pool = new List<(MediaItem Item, int Weight)>();
        foreach (var item in All.Where(i => (movies && i.Kind == MediaKind.Movie || shows && i.Kind == MediaKind.Show) && Visible(i)).DistinctBy(i => i.Id))
        {
            if (skip?.Contains(item.Id) == true)
            {
                continue;
            }
            var weight = 2;
            double? length;
            if (item.Kind == MediaKind.Movie)
            {
                if (!item.Playable || IsWatched(item))
                {
                    continue;
                }
                length = item.Duration;
                weight += PositionOf(item) != null ? 2 : 0;
            }
            else
            {
                if (UnwatchedCount(item) is not > 0 || NextToPlay(item) is not { } next)
                {
                    continue;
                }
                length = next.Duration;
                weight += Children(item.Id).Any(IsWatched) ? 2 : 0;
            }
            if (maxMinutes is { } max && length is { } len && len > max * 60)
            {
                continue;
            }
            weight += OnWatchlist(item) ? 3 : 0;
            pool.Add((item, weight));
        }
        if (pool.Count == 0)
        {
            return null;
        }
        var roll = random.Next(pool.Sum(p => p.Weight));
        foreach (var (item, w) in pool)
        {
            roll -= w;
            if (roll < 0)
            {
                return item;
            }
        }
        return pool[^1].Item;
    }

    public IReadOnlyList<MediaItem> Children(string parentId) =>
        IsCollection(parentId) ? CollectionItems(parentId) : (_byParent ??= BuildByParent()).GetValueOrDefault(parentId) ?? [];

    // Episodes by show and tracks by album, in order (rebuilt when sources change).
    private volatile Dictionary<string, IReadOnlyList<MediaItem>>? _byParent;

    private Dictionary<string, IReadOnlyList<MediaItem>> BuildByParent() =>
        All.Where(i => i.ParentId != null).GroupBy(i => i.ParentId!).ToDictionary(g => g.Key, g => (IReadOnlyList<MediaItem>)g
            .OrderBy(i => i.Season ?? 0).ThenBy(i => i.Index ?? int.MaxValue).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList());

    /// <summary>
    /// A show's episodes by season, in order: numbered seasons, then episodes without a season,
    /// then specials (season 0) last, as most apps list them.
    /// </summary>
    public IReadOnlyList<ShowSeason> Seasons(MediaItem show) =>
        Children(show.Id).GroupBy(e => e.Season)
            .OrderBy(g => g.Key switch { null => 1, 0 => 2, _ => 0 }).ThenBy(g => g.Key)
            .Select(g =>
            {
                var eps = g.OrderBy(e => e.Index ?? int.MaxValue).ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
                var title = eps.Select(e => e.SeasonTitle).FirstOrDefault(t => t != null)
                    ?? g.Key switch { 0 => "Specials", { } n => $"Season {n}", null => "Episodes" };
                return new ShowSeason(g.Key, title, eps, eps.Select(e => e.SeasonPosterUrl).FirstOrDefault(u => u != null));
            }).ToList();

    /// <summary>
    /// What "play" on a show or album starts: the one in progress, else the first not watched
    /// (specials only once the regular seasons are done), else the first (null when nothing plays).
    /// </summary>
    public MediaItem? NextToPlay(MediaItem container)
    {
        var children = Children(container.Id).Where(c => c.Playable).ToList();
        var regular = children.Where(c => c.Season != 0).ToList();
        return children.FirstOrDefault(c => PositionOf(c) != null && !IsWatched(c))
            ?? regular.FirstOrDefault(c => !IsWatched(c))
            ?? children.FirstOrDefault(c => !IsWatched(c))
            ?? regular.FirstOrDefault() ?? children.FirstOrDefault();
    }

    /// <summary>Episodes of a show not watched yet (null for anything else).</summary>
    public int? UnwatchedCount(MediaItem item) =>
        item.Kind == MediaKind.Show ? Children(item.Id).Count(e => e.Playable && !IsWatched(e)) : null;

    /// <summary>When something new last arrived: a show's newest episode, else the item itself.</summary>
    public DateTime LastAdded(MediaItem item) =>
        item.Kind == MediaKind.Show && Children(item.Id) is { Count: > 0 } eps ? eps.Max(e => e.Added) is var e2 && e2 > item.Added ? e2 : item.Added : item.Added;

    private IReadOnlyList<MediaItem> SortWithShows(IEnumerable<MediaItem> items, SortKey sort, bool descending)
    {
        if (sort != SortKey.Added)
        {
            return Sort(items, sort, descending);
        }
        var titleCmp = StringComparer.CurrentCultureIgnoreCase;
        var keyed = items.Select(i => (Item: i, At: LastAdded(i)));
        return (descending ? keyed.OrderByDescending(x => x.At) : keyed.OrderBy(x => x.At)).ThenBy(x => SortTitle(x.Item.Title), titleCmp).Select(x => x.Item).ToList();
    }

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

    /// <summary>The episode or track after this one in its show or album, if any.</summary>
    public MediaItem? NextAfter(MediaItem item) =>
        item.ParentId == null ? null : Children(item.ParentId).SkipWhile(c => c.Id != item.Id).Skip(1).FirstOrDefault(c => c.Playable);

    /// <summary>
    /// Next episodes of shows being watched: the first unwatched episode after the last watched
    /// one, for shows with nothing in progress. Most recently watched first.
    /// </summary>
    public IReadOnlyList<MediaItem> UpNext()
    {
        var result = new List<(MediaItem Next, DateTime At, DateTime Added)>();
        var shows = All.Where(i => i.Kind == MediaKind.Show && Visible(i)).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var show in shows.Values)
        {
            var eps = Children(show.Id).Where(e => e.Playable).ToList();
            if (eps.Count == 0)
            {
                continue;
            }
            var watched = eps.Select(IsWatched).ToList();
            var last = watched.LastIndexOf(true);
            if (last < 0 || eps.Any(e => !IsWatched(e) && PositionOf(e) != null))
            {
                continue;
            }
            var next = eps.Skip(last + 1).FirstOrDefault(e => !IsWatched(e));
            if (next != null)
            {
                result.Add((next, Progress.WatchedAt(eps[last].Id) ?? DateTime.MinValue, show.Added));
            }
        }
        return result.OrderByDescending(r => r.At).ThenByDescending(r => r.Added).Select(r => r.Next).ToList();
    }

    /// <summary>Cast, crew and extras of a movie or show (from its server), or null.</summary>
    public async Task<ItemDetails?> DetailsAsync(MediaItem item, CancellationToken ct = default) =>
        SourceOf(item) is { } source ? await source.DetailsAsync(item, ct) : null;

    /// <summary>Movies and shows in the library a person is in, newest first.</summary>
    public async Task<IReadOnlyList<MediaItem>> TitlesWithAsync(Person person, CancellationToken ct = default)
    {
        if (_sources.FirstOrDefault(s => s.Id == person.SourceId) is not { } source)
        {
            return [];
        }
        var ids = (await source.TitlesWithAsync(person, ct)).ToHashSet();
        return All.Where(i => ids.Contains(i.Id) && Visible(i)).DistinctBy(i => i.Id).OrderByDescending(i => i.Year ?? 0).ThenBy(i => SortTitle(i.Title), StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Seek bar thumbnails for an item, when its server has them (after <see cref="PrepareAsync"/>).</summary>
    public SeekPreview? PreviewOf(MediaItem item) => SourceOf(item)?.PreviewOf(item.Id);

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
        // In progress first, then the next episode of shows being watched.
        var resume = InProgress().Concat(UpNext()).DistinctBy(i => i.Id).Take(perRow).ToList();
        if (resume.Count > 0)
        {
            rows.Add(new HomeRow("continue", "Continue watching", resume, Wide: true));
        }
        // Titles you can play first, then ones on the Plex watchlist that no server has.
        var watchlist = Watchlist(notInLibrary: true).Take(perRow).ToList();
        if (watchlist.Count > 0)
        {
            rows.Add(new HomeRow("watchlist", "Watchlist", watchlist));
        }
        var recent = All.Where(i => i.Kind is MediaKind.Movie or MediaKind.Show or MediaKind.Album && Visible(i))
            .OrderByDescending(LastAdded).Take(perRow).ToList();
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
                var items = s.Items.Where(i => i.Kind == kind).OrderByDescending(LastAdded).Take(perRow).ToList();
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

    /// <summary>The account watchlist synced with this device's (Plex's), when signed in.</summary>
    public IOnlineWatchlist? OnlineWatchlist { get; set; }

    public const string WatchlistSourceId = "watchlist";

    /// <summary>On the watchlist: the server's (when it keeps one) or this device's.</summary>
    public bool OnWatchlist(MediaItem item)
    {
        if (item.NotInLibrary)
        {
            return OnlineWatchlist is { Active: true } w && w.Entries.Any(e => e.Guid == item.Guid);
        }
        return SourceOf(item)?.ServerWatchlist(item)
            ?? (item.Guid != null && OnlineWatchlist is { Active: true } online && online.Entries.Any(e => e.Guid == item.Guid) ? true : (bool?)null)
            ?? Lists.OnWatchlist(item.Id);
    }

    public void SetWatchlist(MediaItem item, bool on)
    {
        if (item.NotInLibrary)
        {
            if (item.Guid is { } g)
            {
                OnlineWatchlist?.Set(g, on);
            }
            return;
        }
        SourceOf(item)?.OnWatchlist(item, on);
        Lists.SetWatchlist(item.Id, on);  // raises Changed
    }

    /// <summary>
    /// Movies and shows to watch, most recently added first; watched movies drop off. With
    /// <paramref name="notInLibrary"/>, also the online watchlist's titles no library has (last).
    /// </summary>
    public IReadOnlyList<MediaItem> Watchlist(bool notInLibrary = false)
    {
        var local = Lists.Watchlist();
        var order = local.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        // The online list's own order (newest first) wins for the titles it has.
        var online = OnlineWatchlist is { Active: true } w ? w.Entries : [];
        var onlineOrder = new Dictionary<string, int>();
        for (var i = 0; i < online.Count; i++)
        {
            if (online[i].Guid is { Length: > 0 } g)
            {
                onlineOrder.TryAdd(g, i);
            }
        }
        var inLibrary = All.Where(i => i.Kind is MediaKind.Movie or MediaKind.Show && Visible(i) && OnWatchlist(i))
            .Where(i => i.Kind != MediaKind.Movie || !IsWatched(i))
            .DistinctBy(i => i.Guid ?? i.Id)
            .OrderBy(i => i.Guid != null && onlineOrder.TryGetValue(i.Guid, out var n) ? n : online.Count + (order.TryGetValue(i.Id, out var m) ? m : local.Count))
            .ThenByDescending(i => i.Added)
            .ToList();
        if (!notInLibrary)
        {
            return inLibrary;
        }
        return inLibrary.Concat(NotInLibrary()).ToList();
    }

    /// <summary>The online watchlist's titles that no library has, newest first.</summary>
    public IReadOnlyList<MediaItem> NotInLibrary()
    {
        if (OnlineWatchlist is not { Active: true } w)
        {
            return [];
        }
        var have = All.Where(i => i.Guid != null && i.Kind is MediaKind.Movie or MediaKind.Show).Select(i => i.Guid!).ToHashSet();
        return w.Entries.Where(e => e.Title.Length > 0 && e.Guid.Length > 0 && !have.Contains(e.Guid))
            .Select(e => new MediaItem
            {
                Id = $"{WatchlistSourceId}:{e.Key}",
                Title = e.Title,
                Kind = e.Kind,
                Year = e.Year,
                Subtitle = e.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Overview = e.Summary,
                Duration = e.Duration,
                ContentRating = e.ContentRating,
                PosterUrl = e.PosterUrl,
                Guid = e.Guid,
                SourceId = WatchlistSourceId,
                SourceName = $"Your {w.Name} watchlist",
                NotInLibrary = true,
            }).ToList();
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
    /// Jellyfin box sets), then automatic ones (franchises, directors, genres, decades). Optionally only those holding
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
        void Add(string id, string name, IEnumerable<MediaItem> members, string from, string? overview = null, bool keepEmpty = false,
            bool keepOrder = false, string? posterUrl = null)
        {
            var list = members.DistinctBy(m => m.Id).ToList();
            if (list.Count == 0 && !keepEmpty)
            {
                return;
            }
            var ordered = keepOrder || id.StartsWith(UserPrefix, StringComparison.Ordinal)
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
                Poster = posterUrl != null ? null : art?.Poster,
                PosterUrl = posterUrl ?? art?.PosterUrl,
                Genres = ordered.SelectMany(m => m.Genres).GroupBy(g => g).OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key).ToList(),
            }, ordered));
        }
        foreach (var c in Lists.Collections)
        {
            Add(UserPrefix + c.Id, c.Name, c.Items.Select(i => byId.GetValueOrDefault(i)).OfType<MediaItem>(), "Lightcone", "A collection you made in Lightcone.", keepEmpty: true);
        }

        // The servers': titles name the plain ones they're in; the server's own list adds its art,
        // summary and order, and the titles of smart collections.
        var meta = _sources.SelectMany(s => s.ServerCollections.Select(c => (Source: s, Meta: c))).ToList();
        var tagged = titles.SelectMany(t => t.Collections.Select(name => (Name: name, Item: t))).ToList();
        var names = tagged.Select(x => x.Name).Concat(meta.Where(m => m.Meta.Members is { Count: > 0 }).Select(m => m.Meta.Title))
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(SortTitle, StringComparer.CurrentCultureIgnoreCase).ToList();
        var serverSets = new List<HashSet<string>>();
        foreach (var name in names)
        {
            var info = meta.Where(m => string.Equals(m.Meta.Title, name, StringComparison.CurrentCultureIgnoreCase)).ToList();
            var fromInfo = info.SelectMany(m => m.Meta.Members ?? []).Select(id => byId.GetValueOrDefault(id)).OfType<MediaItem>().ToList();
            var fromTags = tagged.Where(x => string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase)).Select(x => x.Item).ToList();
            var first = info.Select(m => m.Meta).FirstOrDefault();
            IEnumerable<MediaItem> members = fromInfo.Concat(fromTags);
            var keepOrder = false;
            if (first?.Order == CollectionOrder.Custom && fromInfo.Count > 0)
            {
                keepOrder = true;  // the server's order, then any tagged title it didn't list
            }
            else if (first?.Order == CollectionOrder.Title)
            {
                members = members.OrderBy(m => SortTitle(m.Title), StringComparer.CurrentCultureIgnoreCase);
                keepOrder = true;
            }
            var sources = fromTags.Select(t => t.SourceName).Concat(info.Select(m => m.Source.Name)).Distinct().ToList();
            var list = members.ToList();
            serverSets.Add(list.Select(m => m.Id).ToHashSet());
            Add(ServerPrefix + Slug(name), name, list, string.Join(", ", sources), first?.Summary, keepOrder: keepOrder,
                posterUrl: info.Select(m => m.Meta.PosterUrl).FirstOrDefault(p => p != null));
        }

        // Automatic: franchises, directors, genres and decades with enough in them to be worth browsing.
        if (!_auto)
        {
            return result;
        }
        const string Auto = "Lightcone (automatic)";
        var films = titles.Where(t => t.Kind == MediaKind.Movie).ToList();
        foreach (var (name, members) in Franchises(films))
        {
            // Plex (with its automatic TMDB collections) usually has these already: don't repeat them.
            var ids = members.Select(m => m.Id).ToHashSet();
            if (serverSets.Any(set => ids.Count(set.Contains) is var n && (n == ids.Count || n >= 2 && n * 3 >= ids.Count * 2)))
            {
                continue;
            }
            Add($"{AutoPrefix}movies:franchise:{Slug(name)}", name, members, Auto, $"Every {name} film in your library, in release order.");
        }
        foreach (var g in films.SelectMany(t => t.Directors.Select(d => (Director: d, Item: t))).GroupBy(x => x.Director, StringComparer.CurrentCultureIgnoreCase)
                     .Where(g => g.Count() >= 3).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).Take(40))
        {
            Add($"{AutoPrefix}movies:director:{Slug(g.Key)}", $"Directed by {g.Key}", g.Select(x => x.Item), Auto, $"Films by {g.Key} in your library, in release order.");
        }
        const int min = 4;
        foreach (var kind in new[] { MediaKind.Movie, MediaKind.Show })
        {
            var what = kind == MediaKind.Movie ? "movies" : "shows";
            var ofKind = titles.Where(t => t.Kind == kind).ToList();
            foreach (var g in ofKind.SelectMany(t => t.Genres.Select(genre => (genre, t))).GroupBy(x => x.genre, StringComparer.CurrentCultureIgnoreCase)
                         .Where(g => g.Count() >= min).OrderByDescending(g => g.Count()))
            {
                Add($"{AutoPrefix}{what}:genre:{Slug(g.Key)}", $"{g.Key} {what}", g.Select(x => x.t).OrderByDescending(t => t.Added), Auto, keepOrder: true);
            }
            foreach (var g in ofKind.Where(t => t.Year is > 1800).GroupBy(t => t.Year!.Value / 10 * 10).Where(g => g.Count() >= min).OrderByDescending(g => g.Key))
            {
                Add($"{AutoPrefix}{what}:decade:{g.Key}", $"{g.Key}s {what}", g, Auto);
            }
        }
        return result;
    }

    // ---- franchises ----

    private static readonly System.Text.RegularExpressions.Regex SequelTail = new(
        @"(\s+(part|chapter|vol\.?|volume|episode)\s+([0-9]+|[ivx]+|one|two|three|four|five))$|(\s+([0-9]{1,2}|[ivx]{1,5}))$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// "John Wick: Chapter 2" → "john wick", "Toy Story 3" → "toy story", "Rocky II" → "rocky":
    /// the part of a title its sequels share.
    /// </summary>
    internal static string FranchiseStem(string title)
    {
        var t = title.Trim();
        foreach (var sep in new[] { ": ", " - ", " – ", " — " })
        {
            if (t.IndexOf(sep, StringComparison.Ordinal) is > 0 and var i)
            {
                t = t[..i];
            }
        }
        for (var n = 0; n < 2; n++)
        {
            t = SequelTail.Replace(t, "").TrimEnd();
        }
        t = Fold(t).Trim();
        return t.StartsWith("the ", StringComparison.Ordinal) ? t[4..] : t;
    }

    /// <summary>
    /// Films that look like one series: two or more different titles sharing a stem ("Toy Story",
    /// "Toy Story 2"). Named by the words their titles start with ("Mission: Impossible").
    /// </summary>
    internal static IEnumerable<(string Name, IReadOnlyList<MediaItem> Members)> Franchises(IEnumerable<MediaItem> films)
    {
        foreach (var g in films.GroupBy(f => FranchiseStem(f.Title)).Where(g => g.Key.Length >= 3))
        {
            var members = g.DistinctBy(f => f.Id).OrderBy(f => f.Year ?? int.MaxValue).ToList();
            if (members.Select(f => Fold(f.Title)).Distinct().Count() < 2)
            {
                continue;  // remakes with the same title aren't a series
            }
            var name = CommonStart(members.Select(f => f.Title).ToList());
            if (name.Length < 3)
            {
                continue;
            }
            yield return (name, members);
        }
    }

    /// <summary>The words every title starts with, without trailing punctuation.</summary>
    private static string CommonStart(IReadOnlyList<string> titles)
    {
        var split = titles.Select(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var words = new List<string>();
        for (var i = 0; split.All(w => w.Length > i); i++)
        {
            var w = split[0][i];
            if (!split.All(x => string.Equals(x[i].TrimEnd(':', '-', ',', '.'), w.TrimEnd(':', '-', ',', '.'), StringComparison.CurrentCultureIgnoreCase)))
            {
                break;
            }
            words.Add(w);
        }
        return string.Join(' ', words).TrimEnd(':', '-', ',', '.', ' ', '–', '—');
    }

    // ---- collection shelves ----

    /// <summary>Which shelf a collection sits on in Movies / Shows: yours, server, franchise, director, genre, decade.</summary>
    public static string ShelfOf(string collectionId)
    {
        if (collectionId.StartsWith(UserPrefix, StringComparison.Ordinal))
        {
            return "yours";
        }
        if (collectionId.StartsWith(ServerPrefix, StringComparison.Ordinal))
        {
            return "server";
        }
        var parts = collectionId.Split(':');
        return parts.Length > 3 ? parts[3] : "other";
    }

    /// <summary>
    /// The collections holding <paramref name="of"/> titles as rows, one per kind: yours, the
    /// servers', franchises, directors, genres, decades (empty ones left out).
    /// </summary>
    public IReadOnlyList<HomeRow> CollectionShelves(MediaKind of)
    {
        var all = Collections(of);
        var server = all.Where(c => ShelfOf(c.Id) == "server").ToList();
        var servers = server.SelectMany(c => c.SourceName.Split(", ")).Distinct().ToList();
        var shelves = new (string Id, string Title)[]
        {
            ("yours", "Your collections"),
            ("server", servers.Count == 1 ? $"From {servers[0]}" : "From your servers"),
            ("franchise", "Franchises"),
            ("director", "Directors"),
            ("genre", "Genres"),
            ("decade", "Decades"),
        };
        var rows = new List<HomeRow>();
        foreach (var (id, title) in shelves)
        {
            var items = all.Where(c => ShelfOf(c.Id) == id).ToList();
            if (items.Count > 0)
            {
                rows.Add(new HomeRow("collections:" + id, title, items));
            }
        }
        return rows;
    }

    /// <summary>Server collections and franchises a title is in (what its page shows as "In …").</summary>
    public IReadOnlyList<MediaItem> SeriesWith(MediaItem item)
    {
        if (item.Kind is not (MediaKind.Movie or MediaKind.Show))
        {
            return [];
        }
        var all = _collections ??= BuildCollections();
        return all.Where(c => ShelfOf(c.Item.Id) is "server" or "franchise" or "yours" && c.Members.Count > 1 && c.Members.Any(m => m.Id == item.Id))
            .OrderBy(c => ShelfOf(c.Item.Id) switch { "franchise" => 0, "server" => 1, _ => 2 })
            .ThenBy(c => c.Members.Count)
            .Select(c => c.Item).ToList();
    }

    private static string Slug(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}
