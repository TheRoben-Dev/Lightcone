using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lightcone.Host.Library;

/// <summary>A behind-the-scenes fact for a detail page, with where it came from.</summary>
public sealed record TriviaFact(string Text, string Source, string Url);

/// <summary>
/// "Did you know?" facts for movies and shows, from the English Wikipedia article's production
/// sections (Production, Development, Casting, Filming and so on), through the public MediaWiki
/// API. Text is CC BY-SA, shown with its source. Only the title, year and kind are sent. Results
/// (also "nothing found") are cached in trivia.json, so each title is looked up once.
/// </summary>
public sealed partial class Trivia
{
    private static readonly string[] Sections = ["Production", "Development", "Pre-production", "Writing", "Casting", "Filming", "Principal photography", "Conception", "Background", "Design", "Visual effects", "Music"];
    private static readonly string[] Weak = ["He ", "She ", "They ", "It ", "This ", "These ", "Those ", "However", "Also", "In addition", "Additionally", "His ", "Her ", "Their ", "Its "];

    private readonly string _file;
    private readonly HttpClient _http;
    private readonly Dictionary<string, List<string>> _cache;
    private readonly object _gate = new();

    public Trivia(string file, HttpClient? http = null)
    {
        _file = file;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd("Lightcone/1.0 (media player; trivia lookup)");
        _cache = AppHost.LoadJson<Dictionary<string, List<string>>>(file) ?? [];
    }

    /// <summary>Movies and shows from a real server (not the made-up sample library).</summary>
    public static bool Applies(MediaItem item) => item.Kind is MediaKind.Movie or MediaKind.Show && item.SourceId != DemoSource.SourceId;

    private static string Key(MediaItem item) => $"{item.Kind}:{item.Title.ToLowerInvariant()}:{item.Year}";

    /// <summary>One fact for <paramref name="item"/> (the same one each time), or null.</summary>
    public async Task<TriviaFact?> GetAsync(MediaItem item, CancellationToken ct = default)
    {
        if (!Applies(item))
        {
            return null;
        }
        var key = Key(item);
        List<string>? entry;
        lock (_gate)
        {
            _cache.TryGetValue(key, out entry);
        }
        if (entry == null)
        {
            entry = await LookUpAsync(item, ct) ?? [];
            lock (_gate)
            {
                _cache[key] = entry;
                try
                {
                    AppHost.SaveJson(_file, _cache);
                }
                catch (IOException e)
                {
                    AppLog.Info("trivia: couldn't save the cache: " + e.Message);
                }
            }
        }
        // [page title, fact 1, fact 2, ...]
        if (entry.Count < 2)
        {
            return null;
        }
        var pick = entry[1 + (int)((uint)StableHash(item.Id) % (uint)(entry.Count - 1))];
        return new TriviaFact(pick, "Wikipedia", "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(entry[0].Replace(' ', '_')));
    }

    private async Task<List<string>?> LookUpAsync(MediaItem item, CancellationToken ct)
    {
        try
        {
            var page = await FindPageAsync(item, ct);
            if (page == null)
            {
                return null;
            }
            var text = await PlainTextAsync(page, ct);
            var facts = Facts(text);
            return facts.Count == 0 ? null : [page, .. facts];
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            AppLog.Info($"trivia for {item.Title}: {e.Message}");
            return null;
        }
    }

    private async Task<string?> FindPageAsync(MediaItem item, CancellationToken ct)
    {
        var what = item.Kind == MediaKind.Show ? "television series" : "film";
        var query = $"{item.Title} {item.Year} {what}".Replace("  ", " ");
        var url = "https://en.wikipedia.org/w/api.php?action=query&list=search&format=json&srlimit=5&srsearch=" + Uri.EscapeDataString(query);
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        var want = Normalize(item.Title);
        foreach (var hit in doc.RootElement.GetProperty("query").GetProperty("search").EnumerateArray())
        {
            var title = hit.GetProperty("title").GetString() ?? "";
            if (MatchesTitle(title, want, item.Kind, item.Year))
            {
                return title;
            }
        }
        return null;
    }

    /// <summary>"Dune (2021 film)" matches Dune, 2021; "Dune (novel)" doesn't.</summary>
    internal static bool MatchesTitle(string pageTitle, string wantNormalized, MediaKind kind, int? year)
    {
        var m = ParenRegex().Match(pageTitle);
        var bare = Normalize(m.Success ? pageTitle[..m.Index] : pageTitle);
        if (bare != wantNormalized)
        {
            return false;
        }
        if (!m.Success)
        {
            return true;
        }
        var tag = m.Groups[1].Value.ToLowerInvariant();
        var kindOk = kind == MediaKind.Show ? tag.Contains("series") || tag.Contains("tv") || tag.Contains("miniseries") : tag.Contains("film") || tag.Contains("movie");
        var yearOk = year == null || !YearRegex().IsMatch(tag) || tag.Contains(year.Value.ToString());
        return kindOk && yearOk;
    }

    private async Task<string> PlainTextAsync(string page, CancellationToken ct)
    {
        var url = "https://en.wikipedia.org/w/api.php?action=query&prop=extracts&explaintext=1&exsectionformat=wiki&format=json&redirects=1&titles=" + Uri.EscapeDataString(page);
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
        foreach (var p in doc.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject())
        {
            if (p.Value.TryGetProperty("extract", out var e))
            {
                return e.GetString() ?? "";
            }
        }
        return "";
    }

    /// <summary>Stand-alone sentences from the production-type sections of a plain-text article.</summary>
    internal static List<string> Facts(string article)
    {
        var facts = new List<string>();
        var inSection = false;
        var level = 0;
        foreach (var raw in article.Split('\n'))
        {
            var line = raw.Trim();
            var h = HeadingRegex().Match(line);
            if (h.Success)
            {
                var lv = h.Groups[1].Value.Length;
                var name = h.Groups[2].Value.Trim();
                if (Sections.Any(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                {
                    inSection = true;
                    level = lv;
                }
                else if (inSection && lv <= level)
                {
                    inSection = false;
                }
                continue;
            }
            if (!inSection || line.Length == 0)
            {
                continue;
            }
            foreach (var s in SentenceRegex().Split(line))
            {
                var t = s.Trim();
                if (t.Length is >= 70 and <= 230 && char.IsUpper(t[0]) && t.EndsWith('.') && !t.Contains('[') && !t.Contains("citation")
                    && !Weak.Any(w => t.StartsWith(w, StringComparison.Ordinal)) && facts.Count < 12)
                {
                    facts.Add(t);
                }
            }
        }
        return facts;
    }

    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Replace("&", "and").Where(char.IsLetterOrDigit).ToArray());

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = (int)2166136261;
            foreach (var c in s)
            {
                h = (h ^ c) * 16777619;
            }
            return h;
        }
    }

    [GeneratedRegex(@"\s*\(([^)]*)\)\s*$")]
    private static partial Regex ParenRegex();

    [GeneratedRegex(@"\d{4}")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"^(=+)\s*(.*?)\s*=+$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z""'])")]
    private static partial Regex SentenceRegex();
}
