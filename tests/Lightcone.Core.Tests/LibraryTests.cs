using Lightcone.Host.Library;
using Xunit;

namespace Lightcone.Core.Tests;

public class LibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lightcone-lib-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private MediaLibrary Demo()
    {
        var lib = new MediaLibrary(new ProgressStore(Path.Combine(_dir, "progress.json")));
        lib.Add(new DemoSource());
        return lib;
    }

    [Fact]
    public void SectionsHoldTopLevelItemsSorted()
    {
        var lib = Demo();
        var movies = lib.Section(MediaKind.Movie);
        Assert.Equal(10, movies.Count);
        Assert.Equal("A Field of Lanterns", movies[0].Title);  // "A " is ignored when sorting: Field
        Assert.True(string.Compare(MediaLibrary.SortTitle(movies[1].Title), MediaLibrary.SortTitle(movies[2].Title), StringComparison.CurrentCultureIgnoreCase) <= 0);
        var newest = lib.Section(MediaKind.Movie, SortKey.Year, descending: true);
        Assert.Equal(2026, newest[0].Year);
        Assert.Equal(3, lib.Section(MediaKind.Show).Count);
        Assert.Equal(6, lib.Children("demo:show:0").Count);
        Assert.Equal(1, lib.Children("demo:show:0")[0].Index);
    }

    [Fact]
    public void SearchIgnoresCaseAccentsAndOrder()
    {
        var lib = Demo();
        Assert.Equal("Signal & Noise", lib.Search("SIGNAL")[0].Title);
        Assert.Contains(lib.Search("sci-fi orbit"), i => i.Title == "Glass Orbit");
        Assert.Contains(lib.Search("the reels"), i => i.Title == "Afterglow");
        Assert.Empty(lib.Search("   "));
    }

    [Fact]
    public void HomeShowsContinueWatchingFirstOncePlaybackIsReported()
    {
        var lib = Demo();
        Assert.Equal("recent", lib.HomeRows()[0].Id);
        var movie = lib.Section(MediaKind.Movie)[3];
        lib.Progress.Report(movie.Id, 1200, movie.Duration);
        var rows = lib.HomeRows();
        Assert.Equal("continue", rows[0].Id);
        Assert.True(rows[0].Wide);
        Assert.Equal(movie.Id, rows[0].Items[0].Id);
        Assert.Equal(1200, lib.PositionOf(movie));

        // Survives a restart.
        var again = new MediaLibrary(new ProgressStore(Path.Combine(_dir, "progress.json")));
        again.Add(new DemoSource());
        Assert.Equal(1200, again.PositionOf(movie));

        // Finishing it clears the position and marks it watched.
        again.Progress.Report(movie.Id, movie.Duration!.Value - 60, movie.Duration);
        Assert.Null(again.PositionOf(movie));
        Assert.True(again.Progress.IsWatched(movie.Id));
        Assert.DoesNotContain(again.HomeRows(), r => r.Id == "continue");
    }

    [Theory]
    [InlineData(6720, false, "1 h 52 min")]
    [InlineData(1500, false, "25 min")]
    [InlineData(221, true, "3:41")]
    [InlineData(3725, true, "1:02:05")]
    public void FormatsDurations(double s, bool clock, string expected) =>
        Assert.Equal(expected, MediaItem.FormatDuration(s, clock));
}
