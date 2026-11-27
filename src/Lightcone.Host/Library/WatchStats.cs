namespace Lightcone.Host.Library;

/// <summary>A show or genre on the stats page, with its watch time.</summary>
public sealed record StatsLine(string Title, double Hours, int Count, string? ItemId = null);

/// <summary>
/// The stats page: what's been watched across the library (servers' watched state included, so it
/// starts full) and when (Lightcone's own history plus the servers' last-played dates).
/// </summary>
public sealed record WatchStats
{
    public int MoviesWatched { get; init; }
    public int EpisodesWatched { get; init; }
    public int ShowsFinished { get; init; }
    public int ShowsStarted { get; init; }

    /// <summary>Running time of everything watched.</summary>
    public double HoursWatched { get; init; }

    public double HoursThisWeek { get; init; }
    public double HoursThisMonth { get; init; }

    /// <summary>Time played in Lightcone itself (from its history).</summary>
    public double HoursInLightcone { get; init; }

    /// <summary>Days in a row with something watched, up to today (or yesterday).</summary>
    public int Streak { get; init; }

    public int LongestStreak { get; init; }

    /// <summary>The weekday with the most watching, or null without any.</summary>
    public DayOfWeek? BusiestDay { get; init; }

    /// <summary>Minutes watched on each of the last 14 days, oldest first.</summary>
    public IReadOnlyList<(DateOnly Day, double Minutes)> LastTwoWeeks { get; init; } = [];

    public IReadOnlyList<StatsLine> TopShows { get; init; } = [];
    public IReadOnlyList<StatsLine> TopGenres { get; init; } = [];

    /// <summary>The last few things watched, newest first.</summary>
    public IReadOnlyList<(MediaItem Item, DateTime At)> Recent { get; init; } = [];

    public bool Empty => MoviesWatched + EpisodesWatched == 0 && HoursInLightcone < 0.01;

    /// <summary>A day counts toward a streak with at least this much watched.</summary>
    public const double StreakMinutes = 5;

    public static WatchStats Compute(MediaLibrary lib, DateTime? now = null)
    {
        var today = DateOnly.FromDateTime((now ?? DateTime.Now).ToLocalTime());
        var history = lib.History.Entries;
        var perDay = new Dictionary<DateOnly, double>();
        var logged = new Dictionary<(DateOnly, string), double>();
        foreach (var e in history)
        {
            perDay[e.Day] = perDay.GetValueOrDefault(e.Day) + e.Seconds;
            logged[(e.Day, e.ItemId)] = logged.GetValueOrDefault((e.Day, e.ItemId)) + e.Seconds;
        }

        var watched = lib.All.Where(i => i.Kind is MediaKind.Movie or MediaKind.Episode && i.Playable && lib.IsWatched(i))
            .DistinctBy(i => i.Id).ToList();
        var recent = new List<(MediaItem, DateTime)>();
        foreach (var item in watched)
        {
            if (lib.LastViewed(item) is not { } at)
            {
                continue;
            }
            recent.Add((item, at));
            var day = DateOnly.FromDateTime(at.ToLocalTime());
            // Watched elsewhere (another device, before Lightcone kept history, or marked watched): its
            // running time that day, less what Lightcone logged of it.
            if (item.Duration is > 0 and var d)
            {
                perDay[day] = perDay.GetValueOrDefault(day) + Math.Max(0, d - logged.GetValueOrDefault((day, item.Id)));
            }
        }

        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));  // Monday
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        double Hours(Func<DateOnly, bool> when) => perDay.Where(kv => when(kv.Key)).Sum(kv => kv.Value) / 3600;

        var days = perDay.Where(kv => kv.Value >= StreakMinutes * 60).Select(kv => kv.Key).ToHashSet();
        var streak = 0;
        var cursor = days.Contains(today) ? today : today.AddDays(-1);
        while (days.Contains(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }
        var longest = 0;
        foreach (var d in days)
        {
            if (days.Contains(d.AddDays(-1)))
            {
                continue;  // not the start of a run
            }
            var n = 0;
            while (days.Contains(d.AddDays(n)))
            {
                n++;
            }
            longest = Math.Max(longest, n);
        }

        var showsById = lib.All.Where(i => i.Kind == MediaKind.Show).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        var episodes = watched.Where(i => i.Kind == MediaKind.Episode).ToList();
        var topShows = episodes.Where(e => e.ParentId != null).GroupBy(e => e.ParentId!)
            .Select(g => new StatsLine(showsById.GetValueOrDefault(g.Key)?.Title ?? g.First().ParentTitle ?? "Unknown show",
                g.Sum(e => e.Duration ?? 0) / 3600, g.Count(), g.Key))
            .OrderByDescending(l => l.Hours).ThenByDescending(l => l.Count).Take(5).ToList();

        var genreTime = new Dictionary<string, (double Seconds, int Count)>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var item in watched)
        {
            var genres = item.Genres.Count > 0 ? item.Genres : item.ParentId != null && showsById.TryGetValue(item.ParentId, out var show) ? show.Genres : [];
            foreach (var g in genres)
            {
                var (s, c) = genreTime.GetValueOrDefault(g);
                genreTime[g] = (s + (item.Duration ?? 0), c + 1);
            }
        }
        var topGenres = genreTime.OrderByDescending(kv => kv.Value.Seconds).ThenByDescending(kv => kv.Value.Count).Take(5)
            .Select(kv => new StatsLine(kv.Key, kv.Value.Seconds / 3600, kv.Value.Count)).ToList();

        var started = showsById.Values.Where(s => lib.Children(s.Id).Any(e => e.Playable && (lib.IsWatched(e) || lib.PositionOf(e) != null))).ToList();
        var finished = started.Count(s => lib.UnwatchedCount(s) == 0);
        var busiest = perDay.Count == 0 ? (DayOfWeek?)null
            : perDay.GroupBy(kv => kv.Key.DayOfWeek).OrderByDescending(g => g.Sum(kv => kv.Value)).First().Key;

        return new WatchStats
        {
            MoviesWatched = watched.Count(i => i.Kind == MediaKind.Movie),
            EpisodesWatched = episodes.Count,
            ShowsStarted = started.Count,
            ShowsFinished = finished,
            HoursWatched = watched.Sum(i => i.Duration ?? 0) / 3600,
            HoursThisWeek = Hours(d => d >= weekStart && d <= today),
            HoursThisMonth = Hours(d => d >= monthStart && d <= today),
            HoursInLightcone = history.Sum(e => e.Seconds) / 3600,
            Streak = streak,
            LongestStreak = Math.Max(longest, streak),
            BusiestDay = busiest,
            LastTwoWeeks = Enumerable.Range(0, 14).Select(i => today.AddDays(i - 13)).Select(d => (d, perDay.GetValueOrDefault(d) / 60)).ToList(),
            TopShows = topShows,
            TopGenres = topGenres,
            Recent = recent.OrderByDescending(r => r.Item2).Take(8).ToList(),
        };
    }
}
