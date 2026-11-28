using Lightcone.Host.Library;
using Xunit;

namespace Lightcone.Core.Tests;

/// <summary>0.11: collection shelves, franchise and director collections, server collection details, the full watchlist.</summary>
public class CollectionsWatchlistTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lightcone-cw-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private MediaLibrary Make(params IMediaSource[] sources)
    {
        var lib = new MediaLibrary(new ProgressStore(Path.Combine(_dir, "progress.json")), new UserLists(Path.Combine(_dir, "lists.json")));
        foreach (var s in sources.DefaultIfEmpty(new DemoSource()))
        {
            lib.Add(s);
        }
        return lib;
    }

    private static MediaItem Movie(string id, string title, int year, string? guid = null, string[]? collections = null, string[]? directors = null) => new()
    {
        Id = $"t:{id}",
        Title = title,
        Kind = MediaKind.Movie,
        Year = year,
        Guid = guid,
        Collections = collections ?? [],
        Directors = directors ?? [],
        SourceId = "t",
        SourceName = "Test server",
        PlayUrl = "file:///dev/null",
    };

    private sealed class ListSource(IReadOnlyList<MediaItem> items, IReadOnlyList<ServerCollection>? collections = null) : IMediaSource
    {
        public string Id => "t";
        public string Name => "Test server";
        public string Type => "Test";
        public IReadOnlyList<MediaItem> Items => items;
        public IReadOnlyList<ServerCollection> ServerCollections => collections ?? [];
    }

    private sealed class FakeWatchlist : IOnlineWatchlist
    {
        public List<WatchlistEntry> List = new();
        public bool Active => true;
        public string Name => "Plex";
        public IReadOnlyList<WatchlistEntry> Entries => List;

        public void Set(string guid, bool on)
        {
            List.RemoveAll(e => e.Guid == guid);
            if (on)
            {
                List.Insert(0, new WatchlistEntry(guid[(guid.LastIndexOf('/') + 1)..], guid, "", MediaKind.Movie));
            }
        }
    }

    [Theory]
    [InlineData("John Wick: Chapter 2", "john wick")]
    [InlineData("Toy Story 3", "toy story")]
    [InlineData("Rocky II", "rocky")]
    [InlineData("The Matrix Reloaded", "matrix reloaded")]
    [InlineData("Mission: Impossible - Fallout", "mission")]
    [InlineData("2001: A Space Odyssey", "2001")]
    [InlineData("Kill Bill: Vol. 2", "kill bill")]
    [InlineData("Harry Potter and the Goblet of Fire", "harry potter and the goblet of fire")]
    public void FranchiseStems(string title, string stem) => Assert.Equal(stem, MediaLibrary.FranchiseStem(title));

    [Fact]
    public void FranchisesAreSeriesNotRemakes()
    {
        var films = new[]
        {
            Movie("1", "John Wick", 2014), Movie("2", "John Wick: Chapter 2", 2017), Movie("3", "John Wick: Chapter 3 - Parabellum", 2019),
            Movie("4", "Mission: Impossible", 1996), Movie("5", "Mission: Impossible - Ghost Protocol", 2011),
            Movie("6", "Dune", 1984), Movie("7", "Dune", 2021),  // a remake: same title
            Movie("8", "Heat", 1995),
        };
        var found = MediaLibrary.Franchises(films).ToDictionary(f => f.Name, f => f.Members.Select(m => m.Year).ToList());
        Assert.Equal(2, found.Count);
        Assert.Equal([2014, 2017, 2019], found["John Wick"]);
        Assert.Equal([1996, 2011], found["Mission: Impossible"]);
    }

    [Fact]
    public void DemoGetsFranchiseDirectorAndShelfRows()
    {
        var lib = Make();
        var shelves = lib.CollectionShelves(MediaKind.Movie);
        Assert.Equal(new[] { "From Sample library", "Franchises", "Directors", "Decades" }, shelves.Select(r => r.Title));
        var franchises = shelves.Single(r => r.Id == "collections:franchise").Items.Select(c => c.Title).ToList();
        Assert.Equal(["Glass Orbit", "Paper Tigers"], franchises.Order());
        var orbit = lib.Collections().Single(c => c.Title == "Glass Orbit");
        Assert.Equal([2025, 2026], lib.Children(orbit.Id).Select(m => m.Year));
        var directors = shelves.Single(r => r.Id == "collections:director").Items.Select(c => c.Title).ToList();
        Assert.Equal("Directed by Tomas Reyes", directors[0]);  // the most films first
        Assert.Contains("Directed by Ada Lindqvist", directors);
        Assert.DoesNotContain("Directed by Ines Moreau", directors);  // only two

        // A film's page: the series it's in.
        var two = lib.Section(MediaKind.Movie).Single(m => m.Title == "Glass Orbit II");
        Assert.Equal("Glass Orbit", lib.SeriesWith(two).First().Title);

        // Yours come first once there is one; turning automatic collections off leaves the rest.
        lib.Lists.CreateCollection("Sunday night", [two.Id]);
        Assert.Equal("Your collections", lib.CollectionShelves(MediaKind.Movie)[0].Title);
        lib.AutoCollections = false;
        Assert.Equal(["collections:yours", "collections:server"], lib.CollectionShelves(MediaKind.Movie).Select(r => r.Id));
    }

    [Fact]
    public void ServerCollectionsUseTheServersArtSummaryOrderAndSmartTitles()
    {
        var films = new[]
        {
            Movie("1", "Alien", 1979, collections: ["Alien"]), Movie("2", "Aliens", 1986, collections: ["Alien"]), Movie("3", "Prometheus", 2012, collections: ["Alien"]),
            Movie("4", "Heat", 1995), Movie("5", "Collateral", 2004),
            Movie("6", "Toy Story", 1995), Movie("7", "Toy Story 2", 1999, collections: ["Pixar"]),
        };
        var meta = new[]
        {
            new ServerCollection("Alien", "Xenomorphs, in the order Ridley would want.", "http://srv/alien.jpg", CollectionOrder.Custom, false, ["t:3", "t:1", "t:2"]),
            new ServerCollection("Michael Mann", null, null, CollectionOrder.Release, true, ["t:5", "t:4"]),  // smart: no tags
            new ServerCollection("Pixar", null, null, CollectionOrder.Title, false, null),
        };
        var lib = Make(new ListSource(films, meta));
        var alien = lib.Collections().Single(c => c.Title == "Alien");
        Assert.Equal("http://srv/alien.jpg", alien.PosterUrl);
        Assert.Equal("Xenomorphs, in the order Ridley would want.", alien.Overview);
        Assert.Equal(["Prometheus", "Alien", "Aliens"], lib.Children(alien.Id).Select(m => m.Title));  // custom order
        var mann = lib.Collections().Single(c => c.Title == "Michael Mann");
        Assert.Equal(["Heat", "Collateral"], lib.Children(mann.Id).Select(m => m.Title));  // release order
        Assert.Equal("From Test server", lib.CollectionShelves(MediaKind.Movie).Single(r => r.Id == "collections:server").Title);
        // The automatic Toy Story franchise isn't hidden by a server collection holding only half of it…
        Assert.Contains(lib.Collections(), c => c.Title == "Toy Story");
        // …but is when the server has the whole series.
        var lib2 = Make(new ListSource(films.Select(f => f.Title.StartsWith("Toy") ? f with { Collections = ["Toy Story Collection"] } : f).ToList(), meta));
        Assert.DoesNotContain(lib2.Collections(), c => c.Title == "Toy Story");
        Assert.Contains(lib2.Collections(), c => c.Title == "Toy Story Collection");
    }

    [Fact]
    public void WatchlistShowsTitlesNoLibraryHasAndTakesThemOff()
    {
        var arrival = Movie("1", "Arrival", 2016, guid: "plex://movie/a1");
        var heat = Movie("2", "Heat", 1995, guid: "plex://movie/h2");
        var lib = Make(new ListSource([arrival, heat]));
        var online = new FakeWatchlist
        {
            List =
            [
                new WatchlistEntry("d3", "plex://movie/d3", "Dune: Part Two", MediaKind.Movie, 2024, "Paul goes south.", "https://metadata-static.plex.tv/d3.jpg"),
                new WatchlistEntry("a1", "plex://movie/a1", "Arrival", MediaKind.Movie, 2016),
                new WatchlistEntry("s4", "plex://show/s4", "Shogun", MediaKind.Show, 2024),
            ],
        };
        lib.OnlineWatchlist = online;
        Assert.Equal(["Arrival"], lib.Watchlist().Select(i => i.Title));
        var all = lib.Watchlist(notInLibrary: true);
        Assert.Equal(["Arrival", "Dune: Part Two", "Shogun"], all.Select(i => i.Title));
        var dune = all[1];
        Assert.True(dune.NotInLibrary);
        Assert.False(dune.Playable);
        Assert.Equal(("Paul goes south.", "https://metadata-static.plex.tv/d3.jpg", "Your Plex watchlist"), (dune.Overview, dune.PosterUrl, dune.SourceName));
        Assert.True(lib.OnWatchlist(dune));
        Assert.Equal(dune, lib.Find(dune.Id));

        lib.SetWatchlist(dune, false);
        Assert.Equal(["Arrival", "Shogun"], lib.Watchlist(notInLibrary: true).Select(i => i.Title));
        Assert.Null(lib.Find(dune.Id));
    }
}
