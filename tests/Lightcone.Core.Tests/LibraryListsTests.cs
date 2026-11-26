using Lightcone.Host.Library;
using Xunit;

namespace Lightcone.Core.Tests;

/// <summary>The music switch, the local watchlist and collections (made here and automatic).</summary>
public class LibraryListsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lightcone-lists-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private MediaLibrary Make()
    {
        var lib = new MediaLibrary(new ProgressStore(Path.Combine(_dir, "progress.json")), new UserLists(Path.Combine(_dir, "lists.json")));
        lib.Add(new DemoSource());
        return lib;
    }

    [Fact]
    public void MusicCanBeSwitchedOff()
    {
        var lib = Make();
        Assert.NotEmpty(lib.Section(MediaKind.Album));
        Assert.Contains(lib.HomeRows(), r => r.Items.Any(i => i.IsMusic));
        var changed = 0;
        lib.Changed += () => changed++;

        lib.Music = false;
        Assert.Equal(1, changed);
        Assert.Empty(lib.Section(MediaKind.Album));
        Assert.DoesNotContain(lib.HomeRows(), r => r.Items.Any(i => i.IsMusic));
        Assert.DoesNotContain(lib.Search("a", 500), i => i.IsMusic);
        Assert.NotEmpty(lib.Section(MediaKind.Movie));
    }

    [Fact]
    public void WatchlistIsKeptAndWatchedMoviesDropOff()
    {
        var lib = Make();
        var movies = lib.Section(MediaKind.Movie);
        var show = lib.Section(MediaKind.Show)[0];
        lib.SetWatchlist(movies[0], true);
        lib.SetWatchlist(show, true);
        Assert.True(lib.OnWatchlist(movies[0]));
        Assert.Equal([show.Id, movies[0].Id], lib.Watchlist().Select(i => i.Id));  // newest first
        Assert.Equal("watchlist", lib.HomeRows().First(r => r.Id != "continue").Id);  // right after continue watching

        lib.SetWatched(movies[0], true);
        Assert.Equal([show.Id], lib.Watchlist().Select(i => i.Id));

        // Kept on disk.
        var again = new MediaLibrary(new ProgressStore(Path.Combine(_dir, "progress.json")), new UserLists(Path.Combine(_dir, "lists.json")));
        again.Add(new DemoSource());
        Assert.True(again.OnWatchlist(show));
        lib.SetWatchlist(show, false);
        Assert.False(lib.OnWatchlist(show));
    }

    [Fact]
    public void CollectionsMadeHereFromServersAndAutomatic()
    {
        var lib = Make();
        var movies = lib.Section(MediaKind.Movie, SortKey.Title);
        var mine = lib.Lists.CreateCollection("  Rainy day  ", [movies[2].Id]);
        lib.Lists.SetInCollection(mine.Id, movies[0].Id, true);
        lib.Lists.SetInCollection(mine.Id, movies[0].Id, true);  // no duplicates

        var all = lib.Collections();
        Assert.Equal("Rainy day", all[0].Title);  // made here: first
        Assert.True(MediaLibrary.IsUserCollection(all[0].Id));
        Assert.Equal([movies[2].Id, movies[0].Id], lib.Children(all[0].Id).Select(i => i.Id));  // in the order added
        Assert.Equal("2 movies", all[0].Subtitle);
        Assert.Single(lib.UserCollectionsWith(movies[0]));

        var dark = Assert.Single(all, c => c.Title == "After Dark Collection");  // from the source's tags
        Assert.Equal(3, lib.Children(dark.Id).Count);
        Assert.Contains(all, c => c.Title == "2020s movies" && MediaLibrary.IsAutoCollection(c.Id));
        Assert.DoesNotContain(lib.HomeRows().Single(r => r.Id == "collections").Items, c => MediaLibrary.IsAutoCollection(c.Id));
        Assert.Contains(lib.Search("rainy"), c => c.Id == all[0].Id);

        lib.AutoCollections = false;
        Assert.DoesNotContain(lib.Collections(), c => MediaLibrary.IsAutoCollection(c.Id));

        lib.Lists.RenameCollection(mine.Id, "Sunday");
        lib.Lists.SetInCollection(mine.Id, movies[2].Id, false);
        Assert.Equal("Sunday", lib.Collections()[0].Title);
        Assert.Single(lib.Children(lib.Collections()[0].Id));
        lib.Lists.DeleteCollection(mine.Id);
        Assert.DoesNotContain(lib.Collections(), c => MediaLibrary.IsUserCollection(c.Id));
    }

    [Fact]
    public void UpNextIsTheEpisodeAfterTheLastWatched()
    {
        var lib = Make();
        var show = lib.Section(MediaKind.Show)[0];
        var eps = lib.Children(show.Id);
        Assert.Empty(lib.UpNext());
        lib.SetWatched(eps[0], true);
        lib.SetWatched(eps[1], true);
        Assert.Equal([eps[2].Id], lib.UpNext().Select(e => e.Id));
        Assert.Equal(eps[2].Id, lib.NextAfter(eps[1])!.Id);
        Assert.Contains(lib.HomeRows().Single(r => r.Id == "continue").Items, i => i.Id == eps[2].Id);

        lib.Report(eps[3], 120, eps[3].Duration);  // something in progress: that's what continues
        Assert.Empty(lib.UpNext());
        lib.SetWatched(eps[3], true);
        // Watched out of order: up next follows the last watched episode (like Plex's On Deck).
        Assert.Equal(eps.Count > 4 ? [eps[4].Id] : [], lib.UpNext().Select(e => e.Id));
    }

    [Fact]
    public void RemembersTrackChoicesPerShow()
    {
        var file = Path.Combine(_dir, "lists.json");
        var lists = new UserLists(file);
        lists.RememberTracks("show1", audio: "jpn");
        lists.RememberTracks("show1", subtitle: "eng", subtitlesOff: false);
        lists.RememberTracks("show2", subtitlesOff: true);
        var again = new UserLists(file);
        Assert.Equal(new TrackChoice("jpn", "eng", false), again.TrackChoice("show1"));
        Assert.Equal(new TrackChoice(null, null, true), again.TrackChoice("show2"));
        Assert.Null(again.TrackChoice("movie"));
    }
}
