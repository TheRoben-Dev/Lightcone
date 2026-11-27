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

    private readonly Dictionary<string, Task<List<string>?>> _inflight = [];
    private readonly LinkedList<MediaItem> _queue = new();
    private Task? _worker;

    /// <summary>True when <paramref name="item"/>'s fact (or "none") is already on disk: it shows at once.</summary>
    public bool IsCached(MediaItem item)
    {
        lock (_gate)
        {
            return _cache.ContainsKey(Key(item));
        }
    }

    /// <summary>
    /// Looks facts up in the background, one title at a time with a pause between (polite to
    /// Wikipedia), so detail pages show them instantly. <paramref name="soon"/> jumps the queue
    /// (the tile you're on).
    /// </summary>
    public void Prefetch(IEnumerable<MediaItem> items, bool soon = false)
    {
        lock (_gate)
        {
            foreach (var item in items)
            {
                if (!Applies(item) || _cache.ContainsKey(Key(item)) || _inflight.ContainsKey(Key(item)))
                {
                    continue;
                }
                var existing = _queue.FirstOrDefault(q => Key(q) == Key(item));
                if (existing != null)
                {
                    if (!soon)
                    {
                        continue;
                    }
                    _queue.Remove(existing);
                }
                if (soon)
                {
                    _queue.AddFirst(item);
                }
                else if (_queue.Count < 400)
                {
                    _queue.AddLast(item);
                }
            }
            if (_queue.Count > 0 && (_worker == null || _worker.IsCompleted))
            {
                _worker = Task.Run(DrainAsync);
            }
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            MediaItem next;
            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    return;
                }
                next = _queue.First!.Value;
                _queue.RemoveFirst();
            }
            await EntryAsync(next, CancellationToken.None);
            await Task.Delay(1200);
        }
    }

    /// <summary>One fact for <paramref name="item"/> (the same one each time), or null.</summary>
    public async Task<TriviaFact?> GetAsync(MediaItem item, CancellationToken ct = default)
    {
        if (!Applies(item))
        {
            return null;
        }
        var entry = await EntryAsync(item, ct);
        // [page title, fact 1, fact 2, ...]
        if (entry == null || entry.Count < 2)
        {
            return null;
        }
        var pick = entry[1 + (int)((uint)StableHash(item.Id) % (uint)(entry.Count - 1))];
        return new TriviaFact(pick, "Wikipedia", "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(entry[0].Replace(' ', '_')));
    }

    /// <summary>The cached entry, or one lookup shared by everyone asking for the same title.</summary>
    private Task<List<string>?> EntryAsync(MediaItem item, CancellationToken ct)
    {
        var key = Key(item);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                return Task.FromResult<List<string>?>(hit);
            }
            if (_inflight.TryGetValue(key, out var running))
            {
                return running;
            }
            var task = FetchAndStoreAsync(item, key, ct);
            _inflight[key] = task;
            return task;
        }
    }

    private async Task<List<string>?> FetchAndStoreAsync(MediaItem item, string key, CancellationToken ct)
    {
        try
        {
            var (ok, facts) = await LookUpAsync(item, ct);
            if (!ok)
            {
                return null;  // network trouble: try again another time, don't remember "nothing"
            }
            lock (_gate)
            {
                _cache[key] = facts;
                try
                {
                    AppHost.SaveJson(_file, _cache);
                }
                catch (IOException e)
                {
                    AppLog.Info("trivia: couldn't save the cache: " + e.Message);
                }
            }
            return facts;
        }
        finally
        {
            lock (_gate)
            {
                _inflight.Remove(key);
            }
        }
    }

    private async Task<(bool Ok, List<string> Facts)> LookUpAsync(MediaItem item, CancellationToken ct)
    {
        try
        {
            var page = await FindPageAsync(item, ct);
            if (page == null)
            {
                return (true, []);
            }
            var text = await PlainTextAsync(page, ct);
            var facts = Facts(text, item.Title);
            return (true, facts.Count == 0 ? [] : [page, .. facts]);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException or KeyNotFoundException or InvalidOperationException)
        {
            AppLog.Info($"trivia for {item.Title}: {e.Message}");
            return (false, []);
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

    /// <summary>
    /// Whole sentences that make sense on their own, from the production-type sections of a
    /// plain-text article, best first. Anything that leans on the text around it is dropped:
    /// quotes, "I" / "we" / "you", an opening pronoun or link word ("He", "This", "However"),
    /// back-references ("this", "that decision"), ellipses and citation leftovers. Better no box
    /// than a confusing one.
    /// </summary>
    internal static List<string> Facts(string article, string? title = null)
    {
        var scored = new List<(string Text, int Score, int Order)>();
        var inSection = false;
        var level = 0;
        var core = false;
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
                    if (!inSection || lv <= level)
                    {
                        level = lv;
                    }
                    inSection = true;
                    core = new[] { "Production", "Development", "Filming", "Casting", "Principal photography" }.Any(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase));
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
            foreach (var part in SentenceRegex().Split(line))
            {
                var t = part.Trim();
                if (!StandsAlone(t))
                {
                    continue;
                }
                var score = (core ? 2 : 0) + (DigitRegex().IsMatch(t) ? 1 : 0)
                    + (title != null && t.Contains(title, StringComparison.OrdinalIgnoreCase) ? 2 : 0)
                    + (t.Length is >= 100 and <= 190 ? 1 : 0);
                scored.Add((t, score, scored.Count));
            }
        }
        return scored.OrderByDescending(x => x.Score).ThenBy(x => x.Order).Take(6).Select(x => x.Text).ToList();
    }

    private static readonly string[] Openers =
    [
        "He ", "She ", "They ", "It ", "It's", "This ", "That ", "These ", "Those ", "There ", "Here ", "His ", "Her ", "Their ", "Its ",
        "However", "Also", "In addition", "Additionally", "Although", "Though", "Instead", "Later", "Then", "Meanwhile", "Despite",
        "Such ", "Both ", "Each ", "Another ", "The latter", "The former", "The same", "Nevertheless", "Furthermore", "Moreover",
        "As a result", "As such", "Because of", "Similarly", "Likewise", "Otherwise", "Still", "Yet ", "But ", "And ", "So ", "Or ",
    ];

    /// <summary>A sentence a reader understands without the one before it.</summary>
    internal static bool StandsAlone(string t)
    {
        if (t.Length is < 80 or > 220 || !char.IsUpper(t[0]) || !t.EndsWith('.') || t.EndsWith("..."))
        {
            return false;
        }
        if (t.IndexOfAny(['"', '\u201c', '\u201d', '\u2026', '[', ']', ':', ';']) >= 0 || t.Contains("citation", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (Openers.Any(o => t.StartsWith(o, StringComparison.Ordinal)))
        {
            return false;
        }
        if (PersonRegex().IsMatch(t) || BackRefRegex().IsMatch(t) || SingleQuoteRegex().IsMatch(t))
        {
            return false;
        }
        // Balanced brackets, or it was cut mid-aside.
        return t.Count(c => c == '(') == t.Count(c => c == ')');
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

    [GeneratedRegex(@"\d")]
    private static partial Regex DigitRegex();

    // First and second person: someone speaking, so a quote out of context.
    [GeneratedRegex(@"\b(I|I'm|I'll|I've|I'd|[Mm]e|[Mm]y|[Ww]e|[Ww]e're|[Oo]ur|[Uu]s|[Yy]ou|[Yy]our|[Yy]ou're)\b")]
    private static partial Regex PersonRegex();

    // Words that point back at the text before: "this", "these", "those", "than that", "the latter".
    [GeneratedRegex(@"\b([Tt]his|[Tt]hese|[Tt]hose|[Ss]uch|latter|former|aforementioned)\b|\b(than|of|like|with|from|about|after|before) that\b|\bthat (time|point|year|decision|version|idea|scene|change|role|film|series|show|season|episode)\b|\b(he|she|they|it) (also|later|then)\b")]
    private static partial Regex BackRefRegex();

    // A quoted phrase in single quotes (an apostrophe inside a word is fine).
    [GeneratedRegex(@"(^|\s)'\w|\w'(\s|[.,]|$)")]
    private static partial Regex SingleQuoteRegex();
}
