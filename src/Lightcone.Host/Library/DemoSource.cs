using System.Globalization;

namespace Lightcone.Host.Library;

/// <summary>
/// A built-in sample library so every screen has something to show before a real source is
/// added (and for the CI screenshots). Its videos are mpv's generated test patterns, so they
/// play anywhere with no files or network. Turned on in Settings → Sources or with --demo.
/// </summary>
public sealed class DemoSource : IMediaSource
{
    public const string SourceId = "demo";

    public string Id => SourceId;
    public string Name => "Sample library";
    public string Type => "Demo";

    public IReadOnlyList<MediaItem> Items { get; }

    private static readonly string[] Patterns = ["testsrc2", "smptehdbars", "rgbtestsrc", "testsrc", "pal100bars", "yuvtestsrc"];

    public DemoSource(DateTime? now = null)
    {
        var t0 = now ?? new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var items = new List<MediaItem>();
        var movies = new (string Title, int Year, int Minutes, string Genre, string Overview)[]
        {
            ("Projector Dreams", 2024, 112, "Drama", "A small-town projectionist keeps the last single-screen cinema running for one more summer."),
            ("The Long Exposure", 2021, 98, "Thriller", "A night photographer catches something in a frame that shouldn't be there."),
            ("Signal & Noise", 2023, 124, "Sci-Fi", "Radio astronomers decode a pattern that rewrites itself every time they listen."),
            ("Harbor Lights", 2019, 105, "Romance", "Two ferry captains, one crossing, forty years of letters."),
            ("Glass Orbit", 2025, 131, "Sci-Fi", "A repair crew on a failing station has eleven hours and one spare part."),
            ("Paper Tigers", 2020, 94, "Comedy", "A failing origami shop enters the city's biggest float parade."),
            ("Undertow", 2022, 117, "Thriller", "A lifeguard investigates the summer the tide brought back what it took."),
            ("Northern Static", 2018, 101, "Documentary", "Life at the radio station closest to the pole."),
            ("Midnight Matinee", 2026, 89, "Horror", "The late show at the Rialto never seems to end."),
            ("A Field of Lanterns", 2017, 108, "Family", "A grandmother and a grandson build three hundred lanterns for the festival."),
        };
        for (var i = 0; i < movies.Length; i++)
        {
            var m = movies[i];
            items.Add(new MediaItem
            {
                Id = $"{SourceId}:movie:{i}",
                Title = m.Title,
                Kind = MediaKind.Movie,
                Year = m.Year,
                Subtitle = $"{m.Year} · {m.Genre}",
                Genres = [m.Genre],
                Overview = m.Overview,
                Duration = m.Minutes * 60,
                Added = t0.AddDays(-i * 3),
                SourceId = SourceId,
                SourceName = Name,
                PlayUrl = Video(i, m.Minutes * 60),
            });
        }
        var shows = new (string Title, int Year, string Genre, int Episodes)[]
        {
            ("Station Eleven-Twelve", 2022, "Drama", 6),
            ("Dial Tone", 2024, "Comedy", 8),
            ("Deep Field", 2023, "Documentary", 5),
        };
        for (var s = 0; s < shows.Length; s++)
        {
            var show = shows[s];
            var showId = $"{SourceId}:show:{s}";
            items.Add(new MediaItem
            {
                Id = showId,
                Title = show.Title,
                Kind = MediaKind.Show,
                Year = show.Year,
                Subtitle = $"{show.Episodes} episodes",
                Genres = [show.Genre],
                Overview = $"A sample {show.Genre.ToLowerInvariant()} series to try Lightcone with.",
                Added = t0.AddDays(-1 - s * 5),
                SourceId = SourceId,
                SourceName = Name,
            });
            for (var e = 1; e <= show.Episodes; e++)
            {
                items.Add(new MediaItem
                {
                    Id = $"{showId}:e{e}",
                    Title = $"Episode {e}",
                    Kind = MediaKind.Episode,
                    Subtitle = $"{show.Title} · S1 · E{e}",
                    ParentId = showId,
                    Index = e,
                    Duration = 25 * 60,
                    Added = t0.AddDays(-1 - s * 5),
                    SourceId = SourceId,
                    SourceName = Name,
                    PlayUrl = Video(s + e, 25 * 60),
                });
            }
        }
        var albums = new (string Title, string Artist, int Year, int Tracks)[]
        {
            ("Afterglow", "The Reels", 2021, 9),
            ("Analog Weather", "Mira Vale", 2024, 11),
            ("Low Orbit Lullabies", "Static Choir", 2019, 8),
            ("Night Bus", "Kasimir", 2023, 10),
            ("Sodium Lights", "The Reels", 2025, 12),
        };
        for (var a = 0; a < albums.Length; a++)
        {
            var al = albums[a];
            var albumId = $"{SourceId}:album:{a}";
            items.Add(new MediaItem
            {
                Id = albumId,
                Title = al.Title,
                Kind = MediaKind.Album,
                Year = al.Year,
                Subtitle = al.Artist,
                Genres = ["Music"],
                Added = t0.AddDays(-2 - a * 4),
                SourceId = SourceId,
                SourceName = Name,
            });
            for (var t = 1; t <= al.Tracks; t++)
            {
                var seconds = 150 + (a * 37 + t * 23) % 150;
                items.Add(new MediaItem
                {
                    Id = $"{albumId}:t{t}",
                    Title = $"Track {t}",
                    Kind = MediaKind.Track,
                    Subtitle = al.Artist,
                    ParentId = albumId,
                    Index = t,
                    Duration = seconds,
                    Added = t0.AddDays(-2 - a * 4),
                    SourceId = SourceId,
                    SourceName = Name,
                    PlayUrl = Tone(220 + (a * 7 + t * 31) % 440, seconds),
                });
            }
        }
        Items = items;
    }

    private static string Video(int i, int seconds) =>
        $"av://lavfi:{Patterns[i % Patterns.Length]}=size=1280x720:rate=30:duration={seconds}";

    private static string Tone(int hz, int seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"av://lavfi:sine=frequency={hz}:duration={seconds}");
}
