using System.Text;
using System.Text.Json;
using Lightcone.Host;
using Lightcone.Host.Jellyfin;
using Lightcone.Host.Library;
using Lightcone.Host.Plex;
using Lightcone.Host.Remote;
using Lightcone.Playback;
using Xunit;

namespace Lightcone.Core.Tests;

/// <summary>0.9.0: discovery, quality, downloads, subtitle look, casting, the phone remote, Steam Input pads.</summary>
public class DiscoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lightcone-discovery-" + Guid.NewGuid().ToString("N")[..8]);

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
    public void SimilarInLibraryPrefersSameGenreAndLeavesOutTheItem()
    {
        var lib = Demo();
        var movie = lib.All.First(i => i.Kind == MediaKind.Movie);
        var like = lib.SimilarInLibrary(movie);
        Assert.NotEmpty(like);
        Assert.DoesNotContain(like, i => i.Id == movie.Id);
        Assert.All(like, i => Assert.True(i.Kind is MediaKind.Movie or MediaKind.Show));
        var sameGenre = like.TakeWhile(i => i.Genres.Intersect(movie.Genres).Any()).Count();
        Assert.True(sameGenre >= 1, "titles sharing a genre come first");
    }

    [Fact]
    public void WatchingSomethingMakesBecauseYouWatchedAndTopPicks()
    {
        var lib = Demo();
        Assert.Empty(lib.Recommended());
        var movie = lib.All.First(i => i.Kind == MediaKind.Movie);
        lib.SetWatched(movie, true);
        var rows = lib.Recommended();
        var picks = lib.TopPicks();
        Assert.NotEmpty(picks);
        Assert.DoesNotContain(picks, i => i.Id == movie.Id);
        Assert.All(rows, r => Assert.True(r.Items.Count >= 3));
    }

    [Fact]
    public void HomeRowsFollowTheChosenOrderAndHideRows()
    {
        var lib = Demo();
        var rows = lib.HomeRows(["recent"], ["collections"]);
        Assert.Equal("recent", MediaLibrary.SectionOf(rows[0]));
        Assert.DoesNotContain(rows, r => MediaLibrary.SectionOf(r) == "collections");
        var order = MediaLibrary.SectionOrder(["libraries", "bogus", "libraries"]);
        Assert.Equal(0, order["libraries"]);
        Assert.Equal(MediaLibrary.HomeSections.Length, order.Count);
    }

    [Theory]
    [InlineData("http://192.168.1.20:32400", true)]
    [InlineData("http://10.0.0.5:8096", true)]
    [InlineData("https://192-168-1-20.0123abcd.plex.direct:32400", true)]
    [InlineData("http://jellyfin.local:8096", true)]
    [InlineData("https://84-23-1-9.0123abcd.plex.direct:32400", false)]
    [InlineData("https://media.example.com", false)]
    public void QualityKnowsHomeFromAway(string url, bool local) => Assert.Equal(local, QualityPolicy.IsLocal(url));

    [Fact]
    public void QualityChoicesResolve()
    {
        Assert.True(QualityPolicy.Find("original").Original);
        Assert.Equal(8000, QualityPolicy.Find("8000").Kbps);
        Assert.True(QualityPolicy.Find("nonsense").Original);
    }

    [Fact]
    public void SubtitleLookMapsToMpvOptions()
    {
        var box = SubtitleLook.Options(125, "yellow", "box", 90, true);
        Assert.Equal("1.25", box["sub-scale"]);
        Assert.Equal("opaque-box", box["sub-border-style"]);
        Assert.Equal("yes", box["sub-bold"]);
        Assert.Equal("90", box["sub-pos"]);
        Assert.Equal("#FFFFE14D", box["sub-color"]);
        var plain = SubtitleLook.Options(100, "bogus", "outline", 140, false);
        Assert.Equal("#FFFFFFFF", plain["sub-color"]);
        Assert.Equal("100", plain["sub-pos"]);
        Assert.Equal("outline-and-shadow", plain["sub-border-style"]);
    }

    [Fact]
    public void DownloadFileNamesAreSafeAndKeepTheExtension()
    {
        var ep = new MediaItem
        {
            Id = "x:1", Title = "What: Now?", Kind = MediaKind.Episode, ParentTitle = "Show/Name", Season = 1, Index = 2,
            SourceId = "x", SourceName = "X",
        };
        Assert.Equal("Show_Name - S01E02 - What_ Now_.mkv", Downloads.FileName(ep, "http://h/library/parts/1/file.mkv?X-Plex-Token=a"));
        var movie = ep with { Kind = MediaKind.Movie, Title = "Film", Year = 2019 };
        Assert.Equal("Film (2019).mp4", Downloads.FileName(movie, "http://h/video/:/transcode/universal/start.mp4?x=1"));
        Assert.Equal("Film (2019).mkv", Downloads.FileName(movie, "http://h/Items/abc/Download?api_key=1"));
    }

    [Fact]
    public async Task DownloadsResumeFinishAndRemove()
    {
        var body = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();
        var handler = new RangeHandler(body);
        var dl = new Downloads(Path.Combine(_dir, "downloads.json"), () => Path.Combine(_dir, "dl"), new HttpClient(handler));
        var item = new MediaItem { Id = "x:9", Title = "Film", Kind = MediaKind.Movie, SourceId = "x", SourceName = "X" };
        // A half-finished earlier attempt: the rest is asked for with a Range header.
        Directory.CreateDirectory(Path.Combine(_dir, "dl"));
        await File.WriteAllBytesAsync(Path.Combine(_dir, "dl", "Film.mp4.part"), body[..100_000]);
        var done = new TaskCompletionSource();
        dl.Changed += () =>
        {
            if (dl.Of(item.Id)?.State is DownloadState.Done or DownloadState.Failed)
            {
                done.TrySetResult();
            }
        };
        Assert.True(dl.Add(item, "http://server/file.mp4", QualityPolicy.Find("original")));
        Assert.False(dl.Add(item, "http://server/file.mp4", QualityPolicy.Find("original")));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var entry = dl.Of(item.Id)!;
        Assert.Equal(DownloadState.Done, entry.State);
        Assert.Equal(100_000, handler.RangeFrom);
        Assert.Equal(body, await File.ReadAllBytesAsync(entry.File!));
        Assert.Equal(entry.File, dl.LocalFile(item));
        dl.NoteProgress(item.Id);
        Assert.True(dl.Of(item.Id)!.Unsynced);
        dl.Remove(item.Id);
        Assert.Null(dl.Of(item.Id));
        Assert.False(File.Exists(entry.File));
    }

    private sealed class RangeHandler(byte[] body) : HttpMessageHandler
    {
        public long? RangeFrom { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RangeFrom = request.Headers.Range?.Ranges.First().From;
            var from = (int)(RangeFrom ?? 0);
            var res = new HttpResponseMessage(from > 0 ? System.Net.HttpStatusCode.PartialContent : System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body[from..]),
            };
            return Task.FromResult(res);
        }
    }

    [Fact]
    public void JellyfinSessionsLeaveOutThisDevice()
    {
        using var doc = JsonDocument.Parse("""
            [{"Id":"s1","DeviceName":"Living room","Client":"Jellyfin Android TV","DeviceId":"tv","SupportsRemoteControl":true},
             {"Id":"s2","DeviceName":"Deck","Client":"Lightcone","DeviceId":"me","SupportsRemoteControl":true},
             {"Id":"s3","DeviceName":"Web","Client":"Jellyfin Web","DeviceId":"web","SupportsRemoteControl":false}]
            """);
        var targets = JellyfinSource.ParseSessions(doc.RootElement, "jellyfin:1", "me");
        var t = Assert.Single(targets);
        Assert.Equal("s1", t.Id);
        Assert.Equal("Living room", t.Name);
    }

    [Fact]
    public void PlexPlayersComeFromResourcesAndClients()
    {
        using var res = JsonDocument.Parse("""
            [{"name":"Server","provides":"server","clientIdentifier":"srv"},
             {"name":"Shield","product":"Plex for Android (TV)","provides":"player,pubsub-player","clientIdentifier":"shield",
              "connections":[{"uri":"http://203.0.113.5:32500","local":false},{"uri":"http://192.168.1.40:32500","local":true}]},
             {"name":"Me","provides":"player","clientIdentifier":"me"}]
            """);
        var players = PlexSource.ParsePlayers(res.RootElement, "plex:srv", "me").ToList();
        var p = Assert.Single(players);
        Assert.Equal("http://192.168.1.40:32500", p.Uri);
        using var clients = JsonDocument.Parse("""
            {"MediaContainer":{"Server":[{"name":"HTPC","product":"Plex HTPC","machineIdentifier":"htpc","address":"192.168.1.50","port":32500}]}}
            """);
        var c = Assert.Single(PlexSource.ParseClients(clients.RootElement, "plex:srv", "me"));
        Assert.Equal("http://192.168.1.50:32500", c.Uri);
    }

    [Fact]
    public void PhoneRemoteNeedsThePinAndQueuesCommands()
    {
        using var server = new RemoteServer("1234", () => new RemoteState("Player", "Film", null, 60, 600, true, false));
        var none = new Dictionary<string, string>();
        Assert.StartsWith("200", server.Handle("GET", "/", none).Item1);
        Assert.StartsWith("401", server.Handle("GET", "/api/state", none).Item1);
        var pin = new Dictionary<string, string> { ["X-Lightcone-Pin"] = "1234" };
        var state = server.Handle("GET", "/api/state", pin);
        Assert.StartsWith("200", state.Item1);
        Assert.Contains("\"title\":\"Film\"", state.Item3);
        Assert.StartsWith("200", server.Handle("POST", "/api/cmd?c=playpause", pin).Item1);
        Assert.StartsWith("400", server.Handle("POST", "/api/cmd?c=rm%20-rf", pin).Item1);
        Assert.True(server.TryTake(out var c));
        Assert.Equal("playpause", c);
        Assert.False(server.TryTake(out _));
        // Five wrong PINs lock it for a while, even for the right PIN.
        for (var i = 0; i < 5; i++)
        {
            server.Handle("GET", "/api/state?pin=0000", none);
        }
        Assert.StartsWith("429", server.Handle("GET", "/api/state", pin).Item1);
    }

    [Fact]
    public void PhoneRemoteParsesRequests()
    {
        var head = RemoteServer.ParseHead("POST /api/cmd?c=up HTTP/1.1\r\nHost: x\r\nX-Lightcone-Pin: 0042");
        Assert.NotNull(head);
        Assert.Equal("POST", head.Value.Method);
        Assert.Equal("0042", head.Value.Headers["x-lightcone-pin"]);
        Assert.Equal("a b", RemoteServer.Query("/x?q=a+b&n=1")["q"]);
    }

    [Fact]
    public void SteamInputPadsAreReadFromSteamsFile()
    {
        var info = "[slot 0]\nhandle=1\ntype=PS5\nVID=0x054c\nPID=0x0ce6\n[slot 1]\ntype=unknown\nVID=0x28de\nPID=0x1205\n[slot 2]\ntype=XboxOne\n";
        Assert.Equal(["playstation", "deck", "xbox"], SteamPads.ParseInfo(info));
        Assert.Equal("nintendo", SteamPads.FamilyOf("Nintendo Switch Pro Controller"));
        Assert.Equal("playstation", SteamPads.FamilyOf("DualSense Wireless Controller"));
        Assert.Equal("xbox", SteamPads.FamilyOf("Steam Virtual Gamepad"));
    }

    [Fact]
    public async Task DemoSourceSearchesAndFetchesSubtitles()
    {
        var demo = new DemoSource();
        var movie = demo.Items.First(i => i.Kind == MediaKind.Movie);
        Assert.True(((IMediaSource)demo).CanSearchSubtitles(movie));
        var found = await ((IMediaSource)demo).SearchSubtitlesAsync(movie, "sv");
        Assert.Equal(3, found.Count);
        var file = await ((IMediaSource)demo).FetchSubtitleAsync(movie, found[0]);
        Assert.NotNull(file);
        var text = await File.ReadAllTextAsync(file!, Encoding.UTF8);
        Assert.Contains("00:00:05,000 --> 00:00:10,000", text);
        File.Delete(file!);
    }

    [Fact]
    public void SubtitleFilesUnpackAndConvertToUtf8()
    {
        const string srt = "1\n00:00:01,000 --> 00:00:02,500\nÅsa säger hej\n";
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        Assert.Equal((srt, ".srt"), SubtitleFiles.Normalize(Encoding.GetEncoding(1252).GetBytes(srt)));
        Assert.Equal((srt, ".srt"), SubtitleFiles.Normalize(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(srt)).ToArray()));
        Assert.Equal((srt, ".srt"), SubtitleFiles.Normalize(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(srt)).ToArray()));

        using var gz = new MemoryStream();
        using (var z = new System.IO.Compression.GZipStream(gz, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(Encoding.UTF8.GetBytes(srt));
        }
        Assert.Equal((srt, ".srt"), SubtitleFiles.Normalize(gz.ToArray()));

        using var zip = new MemoryStream();
        using (var a = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(a.CreateEntry("readme.nfo").Open()))
            {
                w.Write("release info, much longer than the subtitle itself ............................................");
            }
            using (var w = new StreamWriter(a.CreateEntry("Movie.ass").Open()))
            {
                w.Write("[Script Info]\nTitle: x\n[Events]\n");
            }
        }
        Assert.Equal(".ass", SubtitleFiles.Normalize(zip.ToArray()).Ext);
        Assert.Equal(".vtt", SubtitleFiles.Normalize(Encoding.UTF8.GetBytes("WEBVTT\n\n00:01.000 --> 00:02.000\nhi\n")).Ext);

        Assert.Contains("web page", Assert.Throws<SubtitleException>(() => SubtitleFiles.Normalize(Encoding.UTF8.GetBytes("<!DOCTYPE html><html>sign in</html>"))).Message);
        Assert.Throws<SubtitleException>(() => SubtitleFiles.Normalize([]));
    }
}
