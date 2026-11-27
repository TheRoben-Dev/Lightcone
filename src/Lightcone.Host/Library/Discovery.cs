namespace Lightcone.Host.Library;

/// <summary>
/// Discovery: "More like this" (the server's similar titles, topped up from the library by genre,
/// decade and rating), "Because you watched …" and "Top picks for you" (genres you watch most),
/// and the Home row layout (order and hidden rows).
/// </summary>
public sealed partial class MediaLibrary
{
    /// <summary>Home row sections, in their default order, with names for Settings → Home.</summary>
    public static readonly (string Id, string Name)[] HomeSections =
    [
        ("continue", "Continue watching"),
        ("watchlist", "Watchlist"),
        ("because", "Because you watched …"),
        ("picks", "Top picks for you"),
        ("recent", "Recently added"),
        ("collections", "Collections"),
        ("libraries", "Your libraries (one row per server)"),
    ];

    /// <summary>Which section of <see cref="HomeSections"/> a Home row belongs to.</summary>
    public static string SectionOf(HomeRow row) =>
        row.Id.StartsWith("because:", StringComparison.Ordinal) ? "because"
        : HomeSections.Any(s => s.Id == row.Id) ? row.Id
        : "libraries";

    /// <summary>
    /// Home rows in the given section <paramref name="order"/> (sections not named keep their
    /// default place after them), leaving out <paramref name="hidden"/> sections.
    /// </summary>
    public IReadOnlyList<HomeRow> HomeRows(IReadOnlyList<string>? order, IReadOnlyCollection<string>? hidden, int perRow = 20)
    {
        var rows = HomeRows(perRow).ToList();
        var at = 0;
        foreach (var r in Recommended(perRow))
        {
            // After Continue watching and the watchlist by default.
            while (at < rows.Count && SectionOf(rows[at]) is "continue" or "watchlist")
            {
                at++;
            }
            rows.Insert(at++, r);
        }
        var rank = SectionOrder(order);
        return rows.Where(r => hidden?.Contains(SectionOf(r)) != true)
            .Select((r, i) => (r, i)).OrderBy(x => rank[SectionOf(x.r)]).ThenBy(x => x.i).Select(x => x.r).ToList();
    }

    /// <summary>Every section's position: the given order first, then the rest in default order.</summary>
    public static Dictionary<string, int> SectionOrder(IReadOnlyList<string>? order)
    {
        var ids = (order ?? []).Where(id => HomeSections.Any(s => s.Id == id)).Distinct()
            .Concat(HomeSections.Select(s => s.Id)).Distinct().ToList();
        return ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
    }

    /// <summary>Whether the item's server is reached away from home (the away quality applies).</summary>
    public bool IsRemote(MediaItem item) => SourceOf(item)?.Remote == true;

    /// <summary>What to open for <paramref name="item"/> at <paramref name="quality"/>: a server-converted stream, else the file.</summary>
    public string? StreamUrl(MediaItem item, Lightcone.Playback.Quality quality) =>
        quality.Original ? item.PlayUrl : SourceOf(item)?.StreamUrl(item, quality) ?? item.PlayUrl;

    /// <summary>Whether the item's server can convert it (else quality choices don't apply).</summary>
    public bool CanConvert(MediaItem item) => SourceOf(item)?.StreamUrl(item, Lightcone.Playback.QualityPolicy.Choices[^1]) != null;

    /// <summary>Where to download <paramref name="item"/> from, or null when its source can't.</summary>
    public string? DownloadUrl(MediaItem item, Lightcone.Playback.Quality quality) => SourceOf(item)?.DownloadUrl(item, quality);

    public bool CanSearchSubtitles(MediaItem item) => SourceOf(item)?.CanSearchSubtitles(item) == true;

    public Task<IReadOnlyList<SubtitleResult>> SearchSubtitlesAsync(MediaItem item, string language, CancellationToken ct = default) =>
        SourceOf(item)?.SearchSubtitlesAsync(item, language, ct) ?? Task.FromResult<IReadOnlyList<SubtitleResult>>([]);

    public Task<string?> FetchSubtitleAsync(MediaItem item, SubtitleResult result, CancellationToken ct = default) =>
        SourceOf(item)?.FetchSubtitleAsync(item, result, ct) ?? Task.FromResult<string?>(null);

    /// <summary>Whether the item's server answered last time (downloads sync back only then).</summary>
    public bool SourceOnline(MediaItem item) => SourceOf(item)?.Online == true;

    /// <summary>Not started: a movie with no progress, or a show with nothing watched or in progress.</summary>
    public bool Untouched(MediaItem item) => item.Kind switch
    {
        MediaKind.Movie => !IsWatched(item) && PositionOf(item) == null,
        MediaKind.Show => !Children(item.Id).Any(e => IsWatched(e) || PositionOf(e) != null),
        _ => false,
    };

    private static double Overlap(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }
        var shared = a.Count(g => b.Contains(g, StringComparer.OrdinalIgnoreCase));
        return shared / (double)(a.Count + b.Count - shared);
    }

    /// <summary>Titles like <paramref name="item"/> from the library alone: genres first, then era and rating.</summary>
    public IReadOnlyList<MediaItem> SimilarInLibrary(MediaItem item, int max = 20)
    {
        if (item.Kind is not (MediaKind.Movie or MediaKind.Show))
        {
            return [];
        }
        return All.Where(i => i.Kind is MediaKind.Movie or MediaKind.Show && i.Id != item.Id && Visible(i))
            .DistinctBy(i => i.Id)
            .Select(i => (i, score: Overlap(item.Genres, i.Genres) * 10
                + (i.Kind == item.Kind ? 1.5 : 0)
                + (item.Year is { } y && i.Year is { } y2 ? Math.Max(0, 1 - Math.Abs(y - y2) / 15.0) : 0)
                + (i.Rating ?? 5) / 10
                - (IsWatched(i) ? 3 : 0)))
            .Where(x => x.score > 2)
            .OrderByDescending(x => x.score).ThenBy(x => x.i.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(max).Select(x => x.i).ToList();
    }

    /// <summary>"More like this": the server's picks that are in the library, then the library's own.</summary>
    public async Task<IReadOnlyList<MediaItem>> SimilarAsync(MediaItem item, int max = 20, CancellationToken ct = default)
    {
        var list = new List<MediaItem>();
        if (SourceOf(item) is { } source)
        {
            try
            {
                foreach (var id in await source.SimilarAsync(item, ct))
                {
                    if (Find(id) is { } found && found.Id != item.Id && Visible(found))
                    {
                        list.Add(found);
                    }
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException)
            {
                AppLog.Info($"similar to {item.Id}: {e.Message}");
            }
        }
        list.AddRange(SimilarInLibrary(item, max));
        return list.DistinctBy(i => i.Id).Take(max).ToList();
    }

    /// <summary>The titles last watched (movies, and the shows of episodes), newest first.</summary>
    public IReadOnlyList<MediaItem> RecentlyWatchedTitles(int max)
    {
        return All.Where(i => i.Kind is MediaKind.Movie or MediaKind.Episode && i.Playable && IsWatched(i))
            .Select(i => (title: i.Kind == MediaKind.Episode && i.ParentId != null ? Find(i.ParentId) : i, at: LastViewed(i)))
            .Where(x => x.title != null && x.at != null)
            .OrderByDescending(x => x.at)
            .Select(x => x.title!).DistinctBy(t => t.Id).Take(max).ToList();
    }

    /// <summary>"Because you watched …" (up to two) and "Top picks for you", from what's been watched.</summary>
    public IReadOnlyList<HomeRow> Recommended(int perRow = 20)
    {
        var rows = new List<HomeRow>();
        var used = new HashSet<string>();
        foreach (var seen in RecentlyWatchedTitles(2))
        {
            var like = SimilarInLibrary(seen, perRow * 2).Where(i => Untouched(i) && !used.Contains(i.Id)).Take(perRow).ToList();
            if (like.Count >= 3)
            {
                rows.Add(new HomeRow($"because:{seen.Id}", $"Because you watched {seen.Title}", like));
                used.UnionWith(like.Take(4).Select(i => i.Id));
            }
        }
        var picks = TopPicks(perRow);
        if (picks.Count >= 3)
        {
            rows.Add(new HomeRow("picks", "Top picks for you", picks));
        }
        return rows;
    }

    /// <summary>Unstarted movies and shows in the genres watched most, better rated first.</summary>
    public IReadOnlyList<MediaItem> TopPicks(int max = 20)
    {
        var taste = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in All.Where(i => i.Kind is MediaKind.Movie or MediaKind.Episode && i.Playable && IsWatched(i)).DistinctBy(i => i.Id))
        {
            var genres = w.Genres.Count > 0 ? w.Genres : w.ParentId != null && Find(w.ParentId) is { } show ? show.Genres : [];
            foreach (var g in genres)
            {
                // A movie counts as much as a few episodes, so one long show doesn't drown out the rest.
                taste[g] = taste.GetValueOrDefault(g) + (w.Kind == MediaKind.Movie ? 1 : 0.3);
            }
        }
        if (taste.Count == 0)
        {
            return [];
        }
        var top = taste.Values.Max();
        return All.Where(i => i.Kind is MediaKind.Movie or MediaKind.Show && Visible(i)).DistinctBy(i => i.Id)
            .Where(Untouched)
            .Select(i => (i, score: i.Genres.Sum(g => taste.GetValueOrDefault(g) / top) / Math.Sqrt(Math.Max(1, i.Genres.Count)) + (i.Rating ?? 6) / 20))
            .Where(x => x.score > 0.4)
            .OrderByDescending(x => x.score).ThenBy(x => x.i.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(max).Select(x => x.i).ToList();
    }
}
