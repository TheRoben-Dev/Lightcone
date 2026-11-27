using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Lightcone.Host.Library;
using Lightcone.Host.Plex;
using Xunit;

namespace Lightcone.Core.Tests;

/// <summary>
/// Plex against a fake plex.tv and server that answer with the JSON shapes Plex sends
/// (trimmed to the fields Lightcone reads).
/// </summary>
public class PlexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lightcone-plex-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private const string Lan = "https://192-168-1-20.abc123.plex.direct:32400";
    private const string Wan = "https://84-1-2-3.abc123.plex.direct:32400";
    private const string Relay = "https://10-0-0-1.abc123.plex.direct:8443";

    private const string Resources = $$"""
        [
          {"name":"Living Room","product":"Plex Media Server","provides":"server","clientIdentifier":"srv1","accessToken":"server-token","owned":true,
           "connections":[
             {"protocol":"https","uri":"{{Relay}}","local":false,"relay":true,"IPv6":false},
             {"protocol":"https","uri":"{{Wan}}","local":false,"relay":false,"IPv6":false},
             {"protocol":"https","uri":"https://[2001:db8::1]:32400","local":false,"relay":false,"IPv6":true},
             {"protocol":"https","uri":"{{Lan}}","local":true,"relay":false,"IPv6":false}]},
          {"name":"Phone","product":"Plex for Android","provides":"player,controller","clientIdentifier":"phone1","connections":[]}
        ]
        """;

    private const string Sections = """
        {"MediaContainer":{"Directory":[
          {"key":"1","type":"movie","title":"Movies"},
          {"key":"2","type":"show","title":"TV Shows"},
          {"key":"3","type":"photo","title":"Photos"}]}}
        """;

    private const string Movies = """
        {"MediaContainer":{"Metadata":[
          {"ratingKey":"101","type":"movie","title":"Arrival","year":2016,"guid":"plex://movie/5d7768ba96b655001fdc0408",
           "contentRating":"PG-13","audienceRating":7.9,"Collection":[{"tag":"Favorites"}],"summary":"Linguist meets heptapods.","duration":6960000,
           "addedAt":1700000000,"viewOffset":1800000,"lastViewedAt":1700500000,"thumb":"/library/metadata/101/thumb/1",
           "Genre":[{"tag":"Science Fiction"},{"tag":"Drama"}],
           "Media":[{"Part":[{"key":"/library/parts/9001/1/Arrival.mkv"}]}]},
          {"ratingKey":"102","type":"movie","title":"Heat","year":1995,"guid":"plex://movie/5d776826999c64001ec2c606","Collection":[{"tag":"Favorites"}],"duration":10200000,"addedAt":1600000000,"viewCount":2,
           "Media":[{"Part":[{"key":"/library/parts/9002/1/Heat.mkv"}]}]}]}}
        """;

    private const string Shows = """
        {"MediaContainer":{"Metadata":[
          {"ratingKey":"200","type":"show","title":"Severance","year":2022,"guid":"plex://show/5d9c086c46115600200aa2fe","leafCount":3,"viewedLeafCount":1,"addedAt":1650000000,"thumb":"/library/metadata/200/thumb/1"}]}}
        """;

    private const string Episodes = """
        {"MediaContainer":{"Metadata":[
          {"ratingKey":"211","type":"episode","title":"Good News About Hell","grandparentRatingKey":"200","grandparentTitle":"Severance",
           "parentIndex":1,"index":1,"duration":3420000,"addedAt":1650000000,"viewCount":1,"Media":[{"Part":[{"key":"/library/parts/211/1/e1.mkv"}]}]},
          {"ratingKey":"212","type":"episode","title":"Half Loop","grandparentRatingKey":"200","grandparentTitle":"Severance",
           "parentIndex":1,"index":2,"duration":3300000,"addedAt":1650000000,"Media":[{"Part":[{"key":"/library/parts/212/1/e2.mkv"}]}]},
          {"ratingKey":"221","type":"episode","title":"Hello, Ms. Cobel","grandparentRatingKey":"200","grandparentTitle":"Severance",
           "parentIndex":2,"parentTitle":"Season 2","parentThumb":"/library/metadata/220/thumb/5","index":1,"duration":3500000,"addedAt":1700000000,"Media":[{"Part":[{"key":"/library/parts/221/1/s2e1.mkv"}]}]}]}}
        """;

    private const string EpisodeMarkers = """
        {"MediaContainer":{"Metadata":[{"ratingKey":"212","type":"episode","title":"Half Loop",
          "Marker":[{"type":"intro","startTimeOffset":45000,"endTimeOffset":105500},
                    {"type":"credits","startTimeOffset":3200000,"endTimeOffset":3300000},
                    {"type":"commercial","startTimeOffset":1000,"endTimeOffset":2000}]}]}}
        """;

    /// <summary>Answers by method and path (query ignored unless the key has one); records every request.</summary>
    private sealed class FakePlex : HttpMessageHandler
    {
        public readonly ConcurrentDictionary<string, string> Routes = new();
        public readonly ConcurrentQueue<HttpRequestMessage> Seen = new();
        public readonly HashSet<string> Down = new();
        public int PinChecks;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Enqueue(request);
            var u = request.RequestUri!;
            var origin = u.GetLeftPart(UriPartial.Authority);
            if (Down.Contains(origin))
            {
                throw new HttpRequestException("connection refused");
            }
            if (u.AbsolutePath == "/api/v2/pins/77")
            {
                // The code is "entered" on the second check.
                var token = Interlocked.Increment(ref PinChecks) >= 2 ? "\"account-token\"" : "null";
                return Ok($$"""{"id":77,"code":"AB12","authToken":{{token}}}""");
            }
            var keyWithQuery = $"{request.Method} {origin}{u.AbsolutePath}{u.Query}";
            var key = $"{request.Method} {origin}{u.AbsolutePath}";
            if (Routes.TryGetValue(keyWithQuery, out var body) || Routes.TryGetValue(key, out body))
            {
                return Ok(body);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Ok(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        public IEnumerable<string> Paths(string contains) =>
            Seen.Select(r => r.RequestUri!.PathAndQuery).Where(p => p.Contains(contains, StringComparison.Ordinal));
    }

    private FakePlex Fake()
    {
        var f = new FakePlex();
        f.Routes["POST https://plex.tv/api/v2/pins"] = """{"id":77,"code":"AB12","expiresIn":900}""";
        f.Routes["GET https://plex.tv/api/v2/user"] = """{"username":"roben","title":"Roben"}""";
        f.Routes["GET https://plex.tv/api/v2/resources"] = Resources;
        // The watchlist: Arrival, plus a film that isn't on any server (ignored).
        f.Routes[$"GET {PlexWatchlist.Discover}/library/sections/watchlist/all"] = """
            {"MediaContainer":{"size":2,"totalSize":2,"Metadata":[
              {"ratingKey":"5d7768ba96b655001fdc0408","guid":"plex://movie/5d7768ba96b655001fdc0408","type":"movie","title":"Arrival"},
              {"ratingKey":"5d77683f6f4521001ea9dc53","guid":"plex://movie/5d77683f6f4521001ea9dc53","type":"movie","title":"Dune"}]}}
            """;
        f.Routes[$"PUT {PlexWatchlist.Discover}/actions/addToWatchlist"] = "{}";
        f.Routes[$"PUT {PlexWatchlist.Discover}/actions/removeFromWatchlist"] = "{}";
        foreach (var origin in new[] { Lan, Wan, Relay })
        {
            f.Routes[$"GET {origin}/identity"] = """{"MediaContainer":{"machineIdentifier":"srv1"}}""";
            f.Routes[$"GET {origin}/library/sections"] = Sections;
            f.Routes[$"GET {origin}/library/sections/1/all?type=1&includeGuids=0"] = Movies;
            f.Routes[$"GET {origin}/library/sections/2/all?type=2&includeGuids=0"] = Shows;
            f.Routes[$"GET {origin}/library/sections/2/all?type=4&includeGuids=0"] = Episodes;
            f.Routes[$"GET {origin}/library/metadata/212?includeMarkers=1"] = EpisodeMarkers;
            f.Routes[$"GET {origin}/:/timeline"] = "{}";
            f.Routes[$"GET {origin}/:/scrobble"] = "{}";
            f.Routes[$"GET {origin}/:/unscrobble"] = "{}";
        }
        return f;
    }

    private (MediaLibrary Lib, PlexService Plex) Make(FakePlex fake)
    {
        var lib = new MediaLibrary(new ProgressStore(Path.Combine(_dir, "data", "progress.json")));
        return (lib, new PlexService(lib, Path.Combine(_dir, "config"), Path.Combine(_dir, "data"), new HttpClient(fake)));
    }

    [Fact]
    public void ParsesServersAndOrdersConnections()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(Resources);
        var servers = PlexClient.ParseServers(doc.RootElement, "account-token");
        var s = Assert.Single(servers);  // the phone is a player, not a server
        Assert.Equal(("srv1", "Living Room", "server-token", true), (s.MachineId, s.Name, s.AccessToken, s.Owned));
        Assert.Equal(3, s.Connections.Count);  // IPv6 skipped
        Assert.Equal([Lan, Wan, Relay], PlexClient.Preferred(s.Connections).Select(c => c.Uri));
    }

    [Fact]
    public async Task SignsInWithALinkCodeAndLoadsTheLibrary()
    {
        var fake = Fake();
        fake.Down.Add(Lan);  // away from home: the LAN address doesn't answer
        var (lib, plex) = Make(fake);
        Assert.False(plex.Account.SignedIn);

        var pin = await plex.Client.CreatePinAsync();
        Assert.Equal("AB12", pin.Code);
        var token = await plex.WaitForLinkAsync(pin, CancellationToken.None, TimeSpan.FromMilliseconds(10));
        Assert.Equal("account-token", token);
        var servers = await plex.SignInAsync(token!);
        Assert.Equal("Roben", plex.Account.Name);
        await plex.UseServersAsync(servers);

        var source = Assert.Single(plex.Sources);
        Assert.True(source.Online);
        Assert.Equal(Wan, source.Entry.LastUri);  // best address that answered; relay only as a last resort
        Assert.Equal("plex:srv1", source.Id);

        // Photos are skipped; movies, shows and episodes come in.
        Assert.Equal(2, lib.Section(MediaKind.Movie).Count);
        var arrival = lib.Find("plex:srv1:101")!;
        Assert.Equal($"{Wan}/library/parts/9001/1/Arrival.mkv?X-Plex-Token=server-token", arrival.PlayUrl);
        Assert.Equal(6960, arrival.Duration);
        Assert.Equal(["Science Fiction", "Drama"], arrival.Genres);
        Assert.StartsWith($"{Wan}/photo/:/transcode?width=400&height=600", arrival.PosterUrl);
        Assert.Contains("url=%2Flibrary%2Fmetadata%2F101%2Fthumb%2F1", arrival.PosterUrl);

        var episodes = lib.Children("plex:srv1:200");
        Assert.Equal(["Good News About Hell", "Half Loop", "Hello, Ms. Cobel"], episodes.Select(e => e.Title));
        Assert.Equal("Severance · S2 · E1", episodes[2].Subtitle);
        Assert.Equal(2, episodes[2].Season);
        Assert.Equal("Season 2", episodes[2].SeasonTitle);
        Assert.Contains("url=%2Flibrary%2Fmetadata%2F220%2Fthumb%2F5", episodes[2].SeasonPosterUrl);
        var seasons = lib.Seasons(lib.Find("plex:srv1:200")!);
        Assert.Equal(["Season 1", "Season 2"], seasons.Select(x => x.Title));
        Assert.Null(seasons[0].PosterUrl);
        Assert.Equal(episodes[2].SeasonPosterUrl, seasons[1].PosterUrl);

        // Server state: Arrival half watched (continue watching), Heat watched, episode 1 watched.
        Assert.Equal(1800, lib.PositionOf(arrival));
        Assert.True(lib.IsWatched(lib.Find("plex:srv1:102")!));
        Assert.True(lib.IsWatched(episodes[0]));
        Assert.False(lib.IsWatched(episodes[1]));
        Assert.Equal("plex:srv1:101", lib.HomeRows()[0].Items[0].Id);
        Assert.Equal("continue", lib.HomeRows()[0].Id);
    }

    [Fact]
    public async Task ReportsProgressMarkersAndWatchedStateToTheServer()
    {
        var fake = Fake();
        var (lib, plex) = Make(fake);
        await plex.SignInAsync("account-token");
        await plex.UseServersAsync(await plex.ServersAsync());
        var source = plex.Sources.Single();
        Assert.Equal(Lan, source.Entry.LastUri);  // at home: LAN first

        var ep = lib.Find("plex:srv1:212")!;
        await lib.PrepareAsync(ep);
        var markers = lib.MarkersOf(ep);
        Assert.Equal([Marker.Intro, Marker.Credits], markers.Select(m => m.Kind));  // "commercial" ignored
        Assert.Equal(45, markers[0].Start);
        Assert.Equal(105.5, markers[0].End);

        lib.Report(ep, 600, 3300, PlaybackState.Playing);
        lib.Report(ep, 605, 3300, PlaybackState.Playing);  // within 10 s of the last report: not sent again
        lib.Report(ep, 610, 3300, PlaybackState.Paused);
        await WaitFor(() => fake.Paths("/:/timeline").Count() >= 2);
        var timelines = fake.Paths("/:/timeline").ToList();
        Assert.Equal(2, timelines.Count);
        Assert.Contains("ratingKey=212", timelines[0]);
        Assert.Contains("state=playing", timelines[0]);
        Assert.Contains("time=600000", timelines[0]);
        Assert.Contains("duration=3300000", timelines[0]);
        Assert.Contains("state=paused", timelines[1]);
        Assert.Equal(610, lib.PositionOf(ep));
        var req = fake.Seen.First(r => r.RequestUri!.AbsolutePath == "/:/timeline");
        Assert.Equal("server-token", req.Headers.GetValues("X-Plex-Token").Single());
        Assert.Equal(plex.Account.ClientId, req.Headers.GetValues("X-Plex-Client-Identifier").Single());

        var heat = lib.Find("plex:srv1:102")!;
        lib.SetWatched(heat, false);
        Assert.False(lib.IsWatched(heat));
        lib.SetWatched(ep, true);
        Assert.True(lib.IsWatched(ep));
        await WaitFor(() => fake.Paths("/:/scrobble").Any() && fake.Paths("/:/unscrobble").Any());
        Assert.Contains("key=102", fake.Paths("/:/unscrobble").Single());
        Assert.Contains("key=212", fake.Paths("/:/scrobble").Single());
    }

    [Fact]
    public async Task ShowsTheSavedLibraryOfflineAndSignsOut()
    {
        var fake = Fake();
        var (_, plex) = Make(fake);
        await plex.SignInAsync("account-token");
        await plex.UseServersAsync(await plex.ServersAsync());

        // Next start, nothing reachable: the saved library shows, marked offline.
        var down = Fake();
        down.Down.UnionWith([Lan, Wan, Relay]);
        var (lib2, plex2) = Make(down);
        Assert.True(plex2.Account.SignedIn);
        Assert.Equal(plex.Account.ClientId, plex2.Account.ClientId);
        Assert.Equal(2, lib2.Section(MediaKind.Movie).Count);
        await plex2.RefreshAllAsync();
        var source = plex2.Sources.Single();
        Assert.False(source.Online);
        Assert.Equal("Offline, showing what was saved", source.Status);
        Assert.Equal(1800, lib2.PositionOf(lib2.Find("plex:srv1:101")!));

        plex2.SignOut();
        Assert.Empty(lib2.Sources);
        Assert.False(plex2.Account.SignedIn);
        var (lib3, plex3) = Make(down);
        Assert.Empty(lib3.Sources);
        Assert.Equal(plex.Account.ClientId, plex3.Account.ClientId);  // same device to Plex after signing out
        Assert.False(File.Exists(Path.Combine(_dir, "data", "plex", "srv1.json")));
    }

    [Fact]
    public async Task LocalProgressWinsOnlyWhenNewer()
    {
        var fake = Fake();
        var (lib, plex) = Make(fake);
        await plex.SignInAsync("account-token");
        await plex.UseServersAsync(await plex.ServersAsync());
        var arrival = lib.Find("plex:srv1:101")!;
        // Watched further on this device long ago (before the server's lastViewedAt): the server wins.
        lib.Progress.Report(arrival.Id, 3000, arrival.Duration, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(1800, lib.PositionOf(arrival));
        lib.Progress.Report(arrival.Id, 3000, arrival.Duration);
        Assert.Equal(3000, lib.PositionOf(arrival));
    }

    [Fact]
    public void TokensNeverReachTheLog() =>
        Assert.Equal("open https://h:32400/library/parts/1/a.mkv?X-Plex-Token=*** done",
            Lightcone.Host.AppLog.Scrub("open https://h:32400/library/parts/1/a.mkv?X-Plex-Token=abc-123_XYZ done"));

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    [Fact]
    public async Task SyncsTheWatchlistAndReadsCollections()
    {
        var fake = Fake();
        var (lib, plex) = Make(fake);
        await plex.SignInAsync("account-token");
        await plex.UseServersAsync(await plex.ServersAsync());
        await plex.Watchlist.RefreshAsync();
        var arrival = lib.Find("plex:srv1:101")!;
        var severance = lib.Find("plex:srv1:200")!;
        Assert.Equal(("PG-13", 7.9), (arrival.ContentRating, arrival.Rating));
        Assert.True(lib.OnWatchlist(arrival));
        Assert.False(lib.OnWatchlist(severance));
        Assert.Equal(["Arrival"], lib.Watchlist().Select(i => i.Title));
        Assert.Contains(lib.HomeRows(), r => r.Id == "watchlist");

        lib.SetWatchlist(severance, true);
        lib.SetWatchlist(arrival, false);
        await plex.Watchlist.Flush();
        Assert.Equal(["Severance"], lib.Watchlist().Select(i => i.Title));
        var puts = fake.Seen.Where(r => r.Method == HttpMethod.Put).Select(r => r.RequestUri!.PathAndQuery).ToList();
        Assert.Equal(["/actions/addToWatchlist?ratingKey=5d9c086c46115600200aa2fe", "/actions/removeFromWatchlist?ratingKey=5d7768ba96b655001fdc0408"], puts);
        Assert.Equal("account-token", fake.Seen.First(r => r.Method == HttpMethod.Put).Headers.GetValues("X-Plex-Token").Single());

        // Plex's collections show up as Lightcone collections.
        var favorites = Assert.Single(lib.Collections(), c => c.Title == "Favorites");
        Assert.Equal(MediaKind.Collection, favorites.Kind);
        Assert.Equal(["Heat", "Arrival"], lib.Children(favorites.Id).Select(i => i.Title));  // by year
        Assert.Equal("2 movies", favorites.Subtitle);
        Assert.Equal(favorites, lib.Find(favorites.Id));

        // Next start, offline: the watchlist is what was saved.
        fake.Down.Add(PlexWatchlist.Discover);
        var lib2 = new MediaLibrary(new ProgressStore(Path.Combine(_dir, "data", "progress2.json")));
        var plex2 = new PlexService(lib2, Path.Combine(_dir, "config"), Path.Combine(_dir, "data"), new HttpClient(fake));
        await plex2.Watchlist.RefreshAsync();
        Assert.True(lib2.OnWatchlist(lib2.Find("plex:srv1:200")!));

        // Signing out forgets it.
        plex2.SignOut();
        Assert.False(plex2.Watchlist.Known);
    }
}
