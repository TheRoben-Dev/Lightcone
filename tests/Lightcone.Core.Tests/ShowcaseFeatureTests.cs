using System.Buffers.Binary;
using System.Text.Json;
using Lightcone.Host.Jellyfin;
using Lightcone.Host.Library;
using Lightcone.Host.Plex;
using Lightcone.Playback;
using Xunit;

namespace Lightcone.Core.Tests;

/// <summary>0.8.0: cast and extras, seek previews, chapters, Night mode / Clear voices, smart resume.</summary>
public class ShowcaseFeatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lightcone-showcase-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>A BIF: header, index of (frame, offset) ending in 0xffffffff, then the "JPEGs".</summary>
    private static byte[] Bif(int frames, uint intervalMs)
    {
        var images = Enumerable.Range(0, frames).Select(i => Enumerable.Repeat((byte)(i + 1), 10 + i).ToArray()).ToList();
        var indexSize = (frames + 1) * 8;
        var data = new byte[64 + indexSize + images.Sum(x => x.Length)];
        new byte[] { 0x89, 0x42, 0x49, 0x46, 0x0d, 0x0a, 0x1a, 0x0a }.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)frames);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), intervalMs);
        var offset = 64 + indexSize;
        for (var i = 0; i < frames; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(64 + i * 8), (uint)i);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(64 + i * 8 + 4), (uint)offset);
            images[i].CopyTo(data, offset);
            offset += images[i].Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(64 + frames * 8), 0xffffffff);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(64 + frames * 8 + 4), (uint)offset);
        return data;
    }

    [Fact]
    public async Task BifPreviewFindsTheFrameBefore()
    {
        var data = Bif(5, 10000);
        var bif = BifPreview.Parse(data)!;
        Assert.Equal(10, bif.Interval);
        Assert.Equal(5, bif.Frames.Count);
        Assert.Equal((20.0, bif.Frames[2].Offset, 12), bif.Frames[2]);
        Assert.Null(BifPreview.Parse(new byte[100]));

        var download = new TaskCompletionSource<byte[]>();
        var preview = new BifPreview(_ => download.Task);
        Assert.Null(await preview.FrameAsync(25));  // first ask starts the download; the seek never waits
        download.SetResult(data);
        var frame = await preview.FrameAsync(25);
        Assert.NotNull(frame);
        Assert.Equal(20, frame!.At);
        Assert.All(frame.Image, b => Assert.Equal(3, b));
        Assert.Equal(40, (await preview.FrameAsync(9999))!.At);
        Assert.Equal(0, (await preview.FrameAsync(-5))!.At);
    }

    [Fact]
    public async Task TilePreviewLocatesFramesInTheGrid()
    {
        var asked = new List<int>();
        var tiles = new TilePreview((i, _) =>
        {
            asked.Add(i);
            return Task.FromResult(new byte[] { (byte)i });
        }, 320, 180, 10, 10, 250, 10);
        Assert.Equal((0, 0, 0, 0.0), tiles.Locate(5));
        Assert.Equal((0, 320 * 3, 180 * 1, 130.0), tiles.Locate(135));
        Assert.Equal((1, 320 * 2, 0, 1020.0), tiles.Locate(1025));
        Assert.Equal(2, tiles.Locate(1e9).Tile);  // clamped to the last frame (249)
        var f = await tiles.FrameAsync(1025);
        Assert.Equal(new PreviewFrame(f!.Image, 640, 0, 320, 180, 1020), f);
        await tiles.FrameAsync(1030);
        Assert.Equal([1], asked);  // the tile image is fetched once
    }

    [Fact]
    public void ChaptersParseAndStep()
    {
        var list = Chapter.ParseList("""[{"title":"Opening","time":0},{"time":312.5},{"title":"","time":901}]""");
        Assert.Equal(["Opening", "Chapter 2", "Chapter 3"], list.Select(c => c.Title));
        Assert.Equal(312.5, list[1].Start);
        Assert.Equal("Opening", Chapter.At(list, 100)?.Title);
        Assert.Equal("Chapter 2", Chapter.At(list, 312.5)?.Title);
        Assert.Equal("Chapter 3", Chapter.At(list, 5000)?.Title);
        Assert.Empty(Chapter.ParseList(null));
        Assert.Empty(Chapter.ParseList("not json"));
        Assert.Empty(Chapter.ParseList("{}"));
    }

    [Fact]
    public void AudioFiltersFitTheTrack()
    {
        Assert.Equal("", AudioFilters.Build(false, false, 6));
        var night = AudioFilters.Build(true, false, 2);
        Assert.StartsWith("lavfi=[acompressor", night);
        Assert.EndsWith("alimiter=limit=0.9:level=disabled]", night);
        Assert.Contains("FC", AudioFilters.Build(false, true, 6));  // surround: centre channel forward
        Assert.DoesNotContain("FC", AudioFilters.Build(false, true, 2));
        Assert.DoesNotContain("FC", AudioFilters.Build(false, true, null));
        var both = AudioFilters.Build(true, true, 8);
        Assert.True(both.IndexOf("pan=", StringComparison.Ordinal) < both.IndexOf("acompressor", StringComparison.Ordinal));
    }

    [Fact]
    public void SmartResumeBacksUpMoreAfterLongerBreaks()
    {
        Assert.Equal(5, ResumePolicy.Rewind(null));
        Assert.Equal(5, ResumePolicy.Rewind(TimeSpan.FromHours(3)));
        Assert.Equal(10, ResumePolicy.Rewind(TimeSpan.FromDays(1.5)));
        Assert.Equal(20, ResumePolicy.Rewind(TimeSpan.FromDays(4)));
        Assert.Equal(30, ResumePolicy.Rewind(TimeSpan.FromDays(40)));
    }

    [Fact]
    public void PlexDetailsReadCastCrewAndExtras()
    {
        Directory.CreateDirectory(_dir);
        var plex = new PlexSource(new PlexClient(new HttpClient(), "test"), new PlexServerEntry("m1", "Home", "tok", [], null), Path.Combine(_dir, "plex.json"));
        using var doc = JsonDocument.Parse("""
            {"ratingKey":"10","Role":[{"id":11,"tag":"Ada Lane","role":"Captain Rhee","thumb":"http://img/ada.jpg"},{"tag":"No Id"}],
             "Director":[{"id":21,"tag":"Bo Kim"}],"Writer":[{"id":31,"tag":"Cy Ode"}],
             "Extras":{"Metadata":[
               {"title":"Making of","subtype":"behindTheScenes","duration":120000,"Media":[{"Part":[{"key":"/library/parts/5/file.mp4"}]}]},
               {"title":"Trailer","extraType":1,"thumb":"/library/metadata/77/thumb","Media":[{"Part":[{"key":"/library/parts/6/file.mp4"}]}]},
               {"title":"No media"}]},
             "Media":[{"Part":[{"id":99,"indexes":"sd"}]}]}
            """);
        var d = plex.ParseDetails(doc.RootElement, "https://srv", "tok");
        Assert.Equal(["Ada Lane", "No Id", "Bo Kim", "Cy Ode"], d.People.Select(p => p.Name));
        Assert.Equal("Captain Rhee", d.People[0].Credit);
        Assert.Equal("actor:11", d.People[0].Key);
        Assert.Equal("actor:name:No Id", d.People[1].Key);
        Assert.Equal("Director", d.People[2].Credit);
        Assert.Contains("/photo/:/transcode", d.People[0].PhotoUrl);
        Assert.Equal(2, d.Extras.Count);
        Assert.Equal("Trailer", d.Trailer?.Title);
        Assert.Equal("Trailer", d.Extras[0].Title);  // trailers first
        Assert.Equal("https://srv/library/parts/6/file.mp4?X-Plex-Token=tok", d.Extras[0].PlayUrl);
        Assert.Equal("Behind the scenes", d.Extras[1].Kind);
        Assert.Equal(120, d.Extras[1].Duration);
        Assert.Equal("99", PlexSource.PreviewPart(doc.RootElement));
    }

    [Fact]
    public void JellyfinReadsPeopleExtrasAndTrickplay()
    {
        Directory.CreateDirectory(_dir);
        var jf = new JellyfinSource(new JellyfinClient(new HttpClient(), "dev"), new JellyfinServerEntry("s1", "Home", "http://jf", "u1", "me", "tok"), Path.Combine(_dir, "jf.json"));
        using var item = JsonDocument.Parse("""
            {"People":[{"Name":"Ada Lane","Id":"p1","Role":"Rhee","Type":"Actor","PrimaryImageTag":"t1"},
                       {"Name":"Guest","Id":"p2","Type":"GuestStar"},{"Name":"Bo Kim","Id":"p3","Type":"Director"},{"Name":"Pro","Id":"p4","Type":"Producer"}],
             "Trickplay":{"src1":{"160":{"Width":160,"Height":90,"TileWidth":10,"TileHeight":10,"ThumbnailCount":300,"Interval":10000},
                                  "320":{"Width":320,"Height":180,"TileWidth":10,"TileHeight":10,"ThumbnailCount":300,"Interval":5000}}}}
            """);
        var people = jf.ParsePeople(item.RootElement);
        Assert.Equal(["Ada Lane", "Guest", "Bo Kim"], people.Select(p => p.Name));
        Assert.Equal("http://jf/Items/p1/Images/Primary?fillWidth=300&fillHeight=300&quality=90&tag=t1", people[0].PhotoUrl);
        var tp = JellyfinSource.ParseTrickplay(item.RootElement)!;
        Assert.Equal(("src1", 320, 180, 5.0), (tp.Source, tp.Width, tp.Height, tp.Interval));

        using var extras = JsonDocument.Parse("""[{"Id":"x1","Name":"Teaser","RunTimeTicks":600000000}]""");
        var list = jf.ParseExtras(extras.RootElement, trailers: true);
        Assert.Equal("Trailer", list[0].Kind);
        Assert.Equal(60, list[0].Duration);
        Assert.Equal("http://jf/Videos/x1/stream?static=true&api_key=tok", list[0].PlayUrl);
    }

    [Fact]
    public async Task DemoCastLeadsToTheirOtherTitles()
    {
        var lib = new MediaLibrary(new ProgressStore(Path.Combine(_dir, "progress.json")));
        lib.Add(new DemoSource());
        var movie = lib.Section(MediaKind.Movie)[0];
        var details = await lib.DetailsAsync(movie);
        Assert.NotNull(details);
        Assert.Contains(details!.People, p => p.Job == Person.Director);
        Assert.NotNull(details.Trailer);
        var actor = details.People.First(p => p.Job == Person.Actor);
        var titles = await lib.TitlesWithAsync(actor);
        Assert.Contains(titles, t => t.Id == movie.Id);
        Assert.All(titles, t => Assert.True(t.Kind is MediaKind.Movie or MediaKind.Show));
    }
}
