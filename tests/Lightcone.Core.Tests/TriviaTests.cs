using Lightcone.Host.Library;
using Xunit;

namespace Lightcone.Core.Tests;

public class TriviaTests
{
    private const string Article = """
        Dune is a 2021 American epic science fiction film.

        == Plot ==
        Paul Atreides travels to the desert planet Arrakis with his family, who have been given control of it by the emperor.

        == Production ==
        === Development ===
        The director had wanted to adapt the novel since reading it as a teenager, and called it one of his biggest dreams as a filmmaker.
        He was hired in 2017.
        However, the studio had other plans for the release date that year and beyond that point in time.
        === Filming ===
        Principal photography began in March 2019 at Origo Film Studios in Budapest, with desert scenes shot in Jordan and Abu Dhabi.
        Short line.

        == Release ==
        The film premiered at the Venice Film Festival in September 2021 and was released in cinemas the following month.
        """;

    [Fact]
    public void FactsComeOnlyFromProductionSections()
    {
        var facts = Trivia.Facts(Article);
        Assert.Equal(2, facts.Count);
        Assert.StartsWith("The director had wanted", facts[0]);
        Assert.StartsWith("Principal photography began", facts[1]);
        Assert.DoesNotContain(facts, f => f.StartsWith("However") || f.StartsWith("He ") || f.Contains("Venice"));
    }

    [Theory]
    [InlineData("Dune (2021 film)", "Dune", MediaKind.Movie, 2021, true)]
    [InlineData("Dune (1984 film)", "Dune", MediaKind.Movie, 2021, false)]
    [InlineData("Dune (novel)", "Dune", MediaKind.Movie, 2021, false)]
    [InlineData("Arcane (TV series)", "Arcane", MediaKind.Show, 2021, true)]
    [InlineData("Arcane (film)", "Arcane", MediaKind.Show, 2021, false)]
    [InlineData("Andor", "Andor", MediaKind.Show, 2022, true)]
    [InlineData("Tokyo Vice (TV series)", "Tokyo Vice", MediaKind.Show, null, true)]
    [InlineData("Star Wars Rebels", "Star Wars: Rebels", MediaKind.Show, 2014, true)]
    public void MatchesTheRightPage(string page, string title, MediaKind kind, int? year, bool expected)
    {
        var want = new string(title.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        Assert.Equal(expected, Trivia.MatchesTitle(page, want, kind, year));
    }

    [Fact]
    public void SampleLibraryIsNeverLookedUp()
    {
        Assert.False(Trivia.Applies(new MediaItem { Id = "demo:1", Title = "Made up", Kind = MediaKind.Movie, SourceId = DemoSource.SourceId, SourceName = "x" }));
        Assert.True(Trivia.Applies(new MediaItem { Id = "plex:1", Title = "Dune", Kind = MediaKind.Movie, SourceId = "plex", SourceName = "x" }));
        Assert.False(Trivia.Applies(new MediaItem { Id = "plex:2", Title = "Ep", Kind = MediaKind.Episode, SourceId = "plex", SourceName = "x" }));
    }

    [Fact]
    public void DownloadRefusalsSayWhy()
    {
        Assert.Contains("turned off", Downloads.Refusal(System.Net.HttpStatusCode.Forbidden));
        Assert.Contains("turned off", Downloads.Refusal(System.Net.HttpStatusCode.Unauthorized));
    }
}
