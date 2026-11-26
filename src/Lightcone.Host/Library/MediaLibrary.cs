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
    private readonly List<IMediaSource> _sources = new();

    public MediaLibrary(ProgressStore progress)
    {
        Progress = progress;
    }

    public ProgressStore Progress { get; }

    public IReadOnlyList<IMediaSource> Sources => _sources;

    public event Action? Changed;

    public void Add(IMediaSource source)
    {
        _sources.RemoveAll(s => s.Id == source.Id);
        _sources.Add(source);
        Changed?.Invoke();
    }

    public void Remove(string sourceId)
    {
        if (_sources.RemoveAll(s => s.Id == sourceId) > 0)
        {
            Changed?.Invoke();
        }
    }

    public IEnumerable<MediaItem> All => _sources.SelectMany(s => s.Items);

    public MediaItem? Find(string id) => All.FirstOrDefault(i => i.Id == id);

    /// <summary>Top-level items of a section: movies, shows, albums (tracks and episodes live under their parent).</summary>
    public IReadOnlyList<MediaItem> Section(MediaKind kind, SortKey sort = SortKey.Title, bool descending = false) =>
        Sort(All.Where(i => i.Kind == kind), sort, descending);

    public IReadOnlyList<MediaItem> Children(string parentId) =>
        All.Where(i => i.ParentId == parentId).OrderBy(i => i.Index ?? int.MaxValue).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

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

    /// <summary>Resume position: this device's, else the source's (server sync).</summary>
    public double? PositionOf(MediaItem item) =>
        Progress.Get(item.Id)?.Position ?? _sources.FirstOrDefault(s => s.Id == item.SourceId)?.PositionOf(item.Id);

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
        return All.Where(i => i.Kind is not MediaKind.Episode || i.ParentId == null)
            .Select(i => (Item: i, Text: Fold($"{i.Title} {i.Subtitle} {string.Join(' ', i.Genres)} {i.Year}")))
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
        var resume = Progress.InProgress().Select(Find).OfType<MediaItem>().Where(i => i.Playable).Take(perRow).ToList();
        if (resume.Count > 0)
        {
            rows.Add(new HomeRow("continue", "Continue watching", resume, Wide: true));
        }
        var recent = All.Where(i => i.Kind is MediaKind.Movie or MediaKind.Show or MediaKind.Album)
            .OrderByDescending(i => i.Added).Take(perRow).ToList();
        if (recent.Count > 0)
        {
            rows.Add(new HomeRow("recent", "Recently added", recent));
        }
        foreach (var s in _sources)
        {
            foreach (var kind in new[] { MediaKind.Movie, MediaKind.Show, MediaKind.Album })
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
}
