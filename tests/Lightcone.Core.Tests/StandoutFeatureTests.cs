using Lightcone.Host.Library;
using Xunit;

namespace Lightcone.Core.Tests;

/// <summary>0.7.0: watched filters, Surprise me, the recap's previous episode, per-show speed and delays, history and stats.</summary>
public class StandoutFeatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lightcone-standout-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private MediaLibrary Make()
    {
        var lib = new MediaLibrary(new ProgressStore(Path.Combine(_dir, "progress.json")), new UserLists(Path.Combine(_dir, "lists.json")),
            new WatchHistory(Path.Combine(_dir, "history.json")));
        lib.Add(new DemoSource());
        return lib;
    }

    [Fact]
    public void FiltersByWatchedState()
    {
        var lib = Make();
        var movies = lib.Section(MediaKind.Movie);
        lib.SetWatched(movies[0], true);
        lib.Report(movies[1], 600, movies[1].Duration);
        Assert.DoesNotContain(lib.Section(MediaKind.Movie, filter: WatchFilter.Unwatched), m => m.Id == movies[0].Id);
        Assert.Equal([movies[0].Id], lib.Section(MediaKind.Movie, filter: WatchFilter.Watched).Select(m => m.Id));
        Assert.Equal([movies[1].Id], lib.Section(MediaKind.Movie, filter: WatchFilter.InProgress).Select(m => m.Id));
        Assert.Equal(movies.Count, lib.Section(MediaKind.Movie).Count);

        var show = lib.Section(MediaKind.Show)[0];
        Assert.Empty(lib.Section(MediaKind.Show, filter: WatchFilter.InProgress));
        lib.SetWatched(lib.Children(show.Id)[0], true);
        Assert.Contains(lib.Section(MediaKind.Show, filter: WatchFilter.InProgress), s => s.Id == show.Id);
        Assert.Contains(lib.Section(MediaKind.Show, filter: WatchFilter.Unwatched), s => s.Id == show.Id);
        foreach (var e in lib.Children(show.Id))
        {
            lib.SetWatched(e, true);
        }
        Assert.Equal([show.Id], lib.Section(MediaKind.Show, filter: WatchFilter.Watched).Select(s => s.Id));
        Assert.DoesNotContain(lib.Section(MediaKind.Show, filter: WatchFilter.Unwatched), s => s.Id == show.Id);
    }

    [Fact]
    public void SurprisePicksUnwatchedThingsThatFit()
    {
        var lib = Make();
        var movies = lib.Section(MediaKind.Movie);
        foreach (var m in movies.Skip(1))
        {
            lib.SetWatched(m, true);
        }
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(movies[0].Id, lib.Surprise(new Random(i), shows: false)?.Id);
            var any = lib.Surprise(new Random(i));
            Assert.NotNull(any);
            Assert.True(any!.Kind == MediaKind.Show || any.Id == movies[0].Id);
        }
        Assert.Null(lib.Surprise(new Random(1), shows: false, skip: [movies[0].Id]));
        // Sample episodes run 25 minutes: none fit in 10.
        Assert.Null(lib.Surprise(new Random(1), movies: false, maxMinutes: 10));
        Assert.NotNull(lib.Surprise(new Random(1), movies: false, maxMinutes: 30));
    }

    [Fact]
    public void PreviousEpisodeAndLastViewed()
    {
        var lib = Make();
        var show = lib.Section(MediaKind.Show).First(s => s.Title == "Deep Field");
        var eps = lib.Children(show.Id).Where(e => e.Season != 0).ToList();
        Assert.Null(lib.PreviousEpisode(eps[0]));
        Assert.Equal(eps[0].Id, lib.PreviousEpisode(eps[1])?.Id);
        // The last of season 1 comes before the first of season 2.
        var s2 = eps.First(e => e.Season == 2);
        Assert.Equal(eps.Last(e => e.Season == 1).Id, lib.PreviousEpisode(s2)?.Id);

        Assert.Null(lib.ShowLastViewed(show));
        lib.SetWatched(eps[0], true);
        Assert.NotNull(lib.LastViewed(eps[0]));
        Assert.True(DateTime.UtcNow - lib.ShowLastViewed(show)!.Value < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void SpeedAndDelaysAreRememberedPerShow()
    {
        var file = Path.Combine(_dir, "lists.json");
        var lists = new UserLists(file);
        lists.RememberTracks("show:1", audio: "ja");
        lists.RememberPlayback("show:1", speed: 1.25, subDelay: -0.4);
        lists.RememberPlayback("show:1", audioDelay: 0.2);
        var again = new UserLists(file).TrackChoice("show:1");
        Assert.Equal(new TrackChoice("ja", null, false, 1.25, -0.4, 0.2), again);
        // Back to normal is stored as "nothing remembered".
        lists.RememberPlayback("show:1", speed: 1, subDelay: 0);
        Assert.Equal(new TrackChoice("ja", null, false, null, null, 0.2), new UserLists(file).TrackChoice("show:1"));
    }

    [Fact]
    public void OldListsFilesStillLoad()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "lists.json");
        File.WriteAllText(file, """{"Watchlist":{},"Collections":[],"Tracks":{"s":{"Audio":"en","Subtitle":null,"SubtitlesOff":true}}}""");
        Assert.Equal(new TrackChoice("en", null, true), new UserLists(file).TrackChoice("s"));
    }

    [Fact]
    public void HistoryAddsUpPerDayAndFeedsTheStats()
    {
        var lib = Make();
        var show = lib.Section(MediaKind.Show).First(s => s.Title == "Deep Field");
        var eps = lib.Children(show.Id).Where(e => e.Season == 1).ToList();
        var now = DateTime.Now;
        lib.History.Add(eps[0], show, 600, when: now);
        lib.History.Add(eps[0], show, 900, finished: true, when: now);
        lib.History.Add(eps[1], show, 1500, finished: true, when: now.AddDays(-1));
        lib.History.Add(eps[2], show, 1500, when: now.AddDays(-3));
        lib.History.Flush();
        var entries = new WatchHistory(Path.Combine(_dir, "history.json")).Entries;
        Assert.Equal(3, entries.Count);
        var today = entries.Single(e => e.ItemId == eps[0].Id);
        Assert.Equal(1500, today.Seconds);
        Assert.Equal(1, today.Finished);
        Assert.Equal("Deep Field", today.ShowTitle);

        lib.SetWatched(eps[0], true);
        lib.SetWatched(eps[1], true);
        lib.SetWatched(lib.Section(MediaKind.Movie)[0], true);
        var stats = WatchStats.Compute(lib, now);
        Assert.Equal(1, stats.MoviesWatched);
        Assert.Equal(2, stats.EpisodesWatched);
        Assert.Equal(1, stats.ShowsStarted);
        Assert.Equal(0, stats.ShowsFinished);
        Assert.Equal(2, stats.Streak);  // today and yesterday; three days ago is a gap
        Assert.Equal(2, stats.LongestStreak);
        Assert.Equal(4500 / 3600.0, stats.HoursInLightcone, 3);
        Assert.Equal("Deep Field", stats.TopShows[0].Title);
        Assert.Equal(14, stats.LastTwoWeeks.Count);
        Assert.True(stats.LastTwoWeeks[^1].Minutes >= 25);
        Assert.NotEmpty(stats.TopGenres);
        Assert.Equal(3, stats.Recent.Count);
        Assert.False(stats.Empty);
    }
}
