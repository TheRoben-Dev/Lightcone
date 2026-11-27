namespace Lightcone.Host.Library;

/// <summary>A subtitle found online for an item (through its server's OpenSubtitles search).</summary>
public sealed record SubtitleResult(string Id, string Title, string Provider, string Language, double? Score = null, bool HearingImpaired = false, int? Downloads = null);

/// <summary>Language codes for subtitle searches: Plex takes two letters, Jellyfin three.</summary>
public static class SubtitleLanguages
{
    private static readonly Dictionary<string, string> ThreeLetter = new()
    {
        ["en"] = "eng", ["sv"] = "swe", ["ja"] = "jpn", ["de"] = "ger", ["fr"] = "fre", ["es"] = "spa", ["it"] = "ita", ["pt"] = "por",
        ["nl"] = "dut", ["no"] = "nor", ["da"] = "dan", ["fi"] = "fin", ["pl"] = "pol", ["ru"] = "rus", ["zh"] = "chi", ["ko"] = "kor",
    };

    public static string ToThree(string two) => ThreeLetter.GetValueOrDefault(two, two);
}
