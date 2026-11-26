using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Lightcone.Host.Jellyfin;
using Lightcone.Host.Library;
using Xunit;

namespace Lightcone.Core.Tests;

/// <summary>Jellyfin against a fake server answering with the JSON shapes Jellyfin 10.10 sends (trimmed).</summary>
public class JellyfinTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lightcone-jf-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private const string Url = "http://192.168.1.30:8096";

    private sealed class FakeJellyfin : HttpMessageHandler
    {
        public readonly ConcurrentDictionary<string, string> Routes = new();
        public readonly ConcurrentQueue<(HttpRequestMessage Request, string Body)> Seen = new();
        public int Checks;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content != null ? await request.Content.ReadAsStringAsync(ct) : "";
            Seen.Enqueue((request, body));
            var u = request.RequestUri!;
            if (u.AbsolutePath == "/QuickConnect/Connect")
            {
                var ok = Interlocked.Increment(ref Checks) >= 2;  // approved on the second check
                return Ok($$"""{"Authenticated":{{(ok ? "true" : "false")}},"Secret":"s3cret","Code":"123456"}""");
            }
            var origin = u.GetLeftPart(UriPartial.Authority);
            if (Routes.TryGetValue($"{request.Method} {origin}{u.AbsolutePath}{u.Query}", out var r) || Routes.TryGetValue($"{request.Method} {origin}{u.AbsolutePath}", out r))
            {
                return Ok(r);
            }
            return new HttpResponseMessage(r == null && origin.Contains(":8096") ? HttpStatusCode.NotFound : HttpStatusCode.ServiceUnavailable);
        }

        private static HttpResponseMessage Ok(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        public IEnumerable<(HttpRequestMessage Request, string Body)> To(string path) => Seen.Where(s => s.Request.RequestUri!.AbsolutePath == path);
    }

    private static FakeJellyfin Fake()
    {
        var f = new FakeJellyfin();
        f.Routes[$"GET {Url}/System/Info/Public"] = """{"ServerName":"Basement","Id":"f00dfeed","Version":"10.10.3","LocalAddress":"http://192.168.1.30:8096"}""";
        f.Routes[$"GET {Url}/QuickConnect/Enabled"] = "true";
        f.Routes[$"POST {Url}/QuickConnect/Initiate"] = """{"Secret":"s3cret","Code":"123456","Authenticated":false}""";
        f.Routes[$"POST {Url}/Users/AuthenticateWithQuickConnect"] = """{"AccessToken":"jf-token","User":{"Id":"u1","Name":"roben"}}""";
        f.Routes[$"GET {Url}/Users/u1/Views"] = """
            {"Items":[{"Id":"v1","Name":"Movies","CollectionType":"movies"},{"Id":"v2","Name":"Shows","CollectionType":"tvshows"},
                      {"Id":"v3","Name":"Photos","CollectionType":"photos"}]}
            """;
        f.Routes[$"GET {Url}/Users/u1/Items"] = """{"Items":[]}""";
        // The fake matches on path only, so the views and box set queries answer by ParentId below.
        return f;
    }

    private const string Movies = """
        {"Items":[
          {"Id":"m1","Name":"Arrival","Type":"Movie","ProductionYear":2016,"RunTimeTicks":69600000000,"DateCreated":"2024-05-01T10:00:00.0000000Z",
           "Overview":"Heptapods.","Genres":["Science Fiction","Drama"],"OfficialRating":"PG-13","CommunityRating":7.9,"ImageTags":{"Primary":"tag1"},
           "UserData":{"PlaybackPositionTicks":18000000000,"Played":false,"LastPlayedDate":"2024-06-01T20:00:00Z"}},
          {"Id":"m2","Name":"Heat","Type":"Movie","ProductionYear":1995,"RunTimeTicks":102000000000,"DateCreated":"2023-01-01T00:00:00Z",
           "UserData":{"PlaybackPositionTicks":0,"Played":true}}]}
        """;

    private const string Shows = """
        {"Items":[
          {"Id":"s1","Name":"Severance","Type":"Series","ProductionYear":2022,"RecursiveItemCount":2,"ImageTags":{"Primary":"st"},"UserData":{"Played":false}},
          {"Id":"e1","Name":"Good News About Hell","Type":"Episode","SeriesId":"s1","SeriesName":"Severance","SeriesPrimaryImageTag":"st",
           "ParentIndexNumber":1,"IndexNumber":1,"RunTimeTicks":34200000000,"UserData":{"Played":true}},
          {"Id":"e2","Name":"Half Loop","Type":"Episode","SeriesId":"s1","SeriesName":"Severance","ParentIndexNumber":1,"IndexNumber":2,
           "RunTimeTicks":33000000000,"ImageTags":{"Primary":"e2t"},"UserData":{"Played":false}}]}
        """;

    private sealed class Routed(FakeJellyfin inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var q = request.RequestUri!.Query;
            if (request.RequestUri.AbsolutePath == "/Users/u1/Items")
            {
                var body = q.Contains("ParentId=v1") ? Movies
                    : q.Contains("ParentId=v2") ? Shows
                    : q.Contains("IncludeItemTypes=BoxSet") ? """{"Items":[{"Id":"b1","Name":"Favorites","Type":"BoxSet"}]}"""
                    : q.Contains("ParentId=b1") ? """{"Items":[{"Id":"m1"},{"Id":"m2"}]}"""
                    : null;
                if (body != null)
                {
                    inner.Seen.Enqueue((request, ""));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
                }
            }
            return base.SendAsync(request, ct);
        }
    }

    private (MediaLibrary Lib, JellyfinService Jf) Make(FakeJellyfin fake)
    {
        var lib = new MediaLibrary(new ProgressStore(Path.Combine(_dir, "data", "progress.json")));
        return (lib, new JellyfinService(lib, Path.Combine(_dir, "config"), Path.Combine(_dir, "data"), new HttpClient(new Routed(fake))));
    }

    [Fact]
    public void TriesLikelyAddresses()
    {
        Assert.Equal(["http://192.168.1.30:8096", "https://192.168.1.30", "http://192.168.1.30"], JellyfinClient.CandidateUrls(" 192.168.1.30/ "));
        Assert.Equal(["http://nas:8920", "https://nas:8920"], JellyfinClient.CandidateUrls("nas:8920"));
        Assert.Equal(["https://jf.example.com/jellyfin"], JellyfinClient.CandidateUrls("https://jf.example.com/jellyfin/"));
        Assert.Equal(("http://192.168.1.30:8096", "f00dfeed", "Basement"),
            JellyfinClient.ParseDiscovery("""{"Address":"http://192.168.1.30:8096/","Id":"f00dfeed","Name":"Basement","EndpointAddress":null}"""));
    }

    [Fact]
    public async Task SignsInWithQuickConnectAndLoadsTheLibrary()
    {
        var fake = Fake();
        var (lib, jf) = Make(fake);
        var info = await jf.Client.FindServerAsync("192.168.1.30");
        Assert.Equal((Url, "f00dfeed", "Basement"), (info.Url, info.Id, info.Name));
        Assert.True(await jf.Client.QuickConnectEnabledAsync(info.Url));
        var code = await jf.Client.InitiateQuickConnectAsync(info.Url);
        Assert.Equal("123456", code.Code);
        var login = await jf.WaitForQuickConnectAsync(info, code, CancellationToken.None, TimeSpan.FromMilliseconds(10));
        Assert.Equal(new JellyfinLogin("u1", "roben", "jf-token"), login);
        var source = await jf.AddServerAsync(info, login!);
        Assert.True(source.Online);
        Assert.Equal("jellyfin:f00dfeed", source.Id);

        var auth = fake.Seen.Last().Request.Headers.GetValues("Authorization").Single();
        Assert.Contains("Client=\"Lightcone\"", auth);
        Assert.Contains($"DeviceId=\"{jf.Account.DeviceId}\"", auth);
        Assert.Contains("Token=\"jf-token\"", auth);

        var arrival = lib.Find("jellyfin:f00dfeed:m1")!;
        Assert.Equal($"{Url}/Videos/m1/stream?static=true&api_key=jf-token", arrival.PlayUrl);
        Assert.Equal(6960, arrival.Duration);
        Assert.Equal(("PG-13", 7.9), (arrival.ContentRating, arrival.Rating));
        Assert.Equal($"{Url}/Items/m1/Images/Primary?fillWidth=400&fillHeight=600&quality=90&tag=tag1", arrival.PosterUrl);
        Assert.Equal(["Favorites"], arrival.Collections);
        Assert.Equal(1800, lib.PositionOf(arrival));
        Assert.True(lib.IsWatched(lib.Find("jellyfin:f00dfeed:m2")!));

        var eps = lib.Children("jellyfin:f00dfeed:s1");
        Assert.Equal(["Good News About Hell", "Half Loop"], eps.Select(e => e.Title));
        Assert.Equal("Severance · S1 · E1", eps[0].Subtitle);
        Assert.Contains("/Items/s1/Images/Primary", eps[0].PosterUrl);  // no own image: the show's
        Assert.Contains("/Items/e2/Images/Primary?fillWidth=640&fillHeight=360", eps[1].PosterUrl);
        Assert.True(lib.IsWatched(eps[0]));
        Assert.Equal("2 episodes", lib.Find("jellyfin:f00dfeed:s1")!.Subtitle);
        Assert.Contains(lib.Collections(), c => c.Title == "Favorites" && c.SourceName == "Basement");

        // Kept: a new start shows the saved library before refreshing.
        var lib2 = new MediaLibrary(new ProgressStore(Path.Combine(_dir, "data", "p2.json")));
        var jf2 = new JellyfinService(lib2, Path.Combine(_dir, "config"), Path.Combine(_dir, "data"), new HttpClient(new Routed(fake)));
        Assert.Equal(jf.Account.DeviceId, jf2.Account.DeviceId);
        Assert.NotNull(lib2.Find("jellyfin:f00dfeed:e2"));
    }

    [Fact]
    public async Task ReportsPlaybackWatchedStateAndSegments()
    {
        var fake = Fake();
        fake.Routes[$"GET {Url}/MediaSegments/e2"] = """
            {"Items":[{"Type":"Intro","StartTicks":450000000,"EndTicks":1055000000},{"Type":"Outro","StartTicks":32000000000,"EndTicks":33000000000},
                      {"Type":"Recap","StartTicks":0,"EndTicks":100}]}
            """;
        foreach (var p in new[] { "/Sessions/Playing", "/Sessions/Playing/Progress", "/Sessions/Playing/Stopped", "/Users/u1/PlayedItems/m2", "/Users/u1/PlayedItems/e2" })
        {
            fake.Routes[$"POST {Url}{p}"] = "";
            fake.Routes[$"DELETE {Url}{p}"] = "";
        }
        var (lib, jf) = Make(fake);
        var source = await jf.AddServerAsync(new JellyfinServerInfo(Url, "f00dfeed", "Basement", "10.10.3"), new JellyfinLogin("u1", "roben", "jf-token"));
        var ep = lib.Find("jellyfin:f00dfeed:e2")!;
        await lib.PrepareAsync(ep);
        var markers = lib.MarkersOf(ep);
        Assert.Equal([Marker.Intro, Marker.Credits], markers.Select(m => m.Kind));
        Assert.Equal((45, 105.5), (markers[0].Start, markers[0].End));

        lib.Report(ep, 600, 3300, PlaybackState.Playing);
        lib.Report(ep, 605, 3300, PlaybackState.Playing);  // throttled
        lib.Report(ep, 900, 3300, PlaybackState.Stopped);
        await source.Flush();
        Assert.Contains("\"PositionTicks\":6000000000", fake.To("/Sessions/Playing").Single().Body);
        Assert.Contains("\"ItemId\":\"e2\"", fake.To("/Sessions/Playing").Single().Body);
        Assert.Contains("\"PositionTicks\":9000000000", fake.To("/Sessions/Playing/Stopped").Single().Body);
        Assert.Empty(fake.To("/Sessions/Playing/Progress"));
        Assert.Equal(900, lib.PositionOf(ep));

        lib.SetWatched(lib.Find("jellyfin:f00dfeed:m2")!, false);
        lib.SetWatched(ep, true);
        await source.Flush();
        Assert.Equal(HttpMethod.Delete, fake.To("/Users/u1/PlayedItems/m2").Single().Request.Method);
        Assert.Equal(HttpMethod.Post, fake.To("/Users/u1/PlayedItems/e2").Single().Request.Method);

        jf.RemoveServer("f00dfeed");
        Assert.Empty(jf.Account.Servers);
        Assert.Null(lib.Find("jellyfin:f00dfeed:e2"));
    }

    [Fact]
    public async Task WrongPasswordSaysSo()
    {
        var fake = new FakeJellyfin();  // no routes: every call is refused
        var client = new JellyfinClient(new HttpClient(new Unauthorized()), "dev");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AuthenticateAsync(Url, "roben", "nope"));
        Assert.Equal("Wrong user name or password.", e.Message);
        await Assert.ThrowsAsync<HttpRequestException>(() => new JellyfinClient(new HttpClient(fake), "dev").FindServerAsync("10.0.0.9"));
    }

    private sealed class Unauthorized : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }
}
