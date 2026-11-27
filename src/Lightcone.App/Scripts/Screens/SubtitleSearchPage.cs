using Godot;
using Lightcone.App.Playback;
using Lightcone.App.Ui;
using Lightcone.Host;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// Player options → Find subtitles…: searches OpenSubtitles through the item's server (Plex's
/// built-in agent, Jellyfin's OpenSubtitles plugin) in a picked language; A on a result fetches it
/// and shows it right away. The server keeps it, so it's there next time too.
/// </summary>
public partial class SubtitleSearchPage : Screen
{
    private readonly MediaItem _item;
    private string _lang;
    private IReadOnlyList<SubtitleResult>? _results;
    private string? _status;
    private bool _busy;
    private Control? _first;
    private Control? _firstResult;
    private int _search;

    public SubtitleSearchPage(MediaItem item)
    {
        _item = item;
        _lang = Host.Settings.SubtitleLanguages.FirstOrDefault() ?? "en";
    }

    public override bool IsOverlay => true;

    public override void AnimateOpen() => Anim.FadeIn(this, Anim.Normal);

    public override IEnumerable<(string Action, string Label)> Hints => [(Pad.Accept, "Use"), (Pad.Back, "Back")];

    protected override void Build()
    {
        var page = Column(12);
        AddChild(CardFrame(Scroller(page)));
        page.AddChild(Section("Find subtitles"));
        page.AddChild(UiTheme.Label($"Searches OpenSubtitles through your server for {_item.Title}.", UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        var lang = new Stepper("Language", SettingsScreen.LanguageChoices.Select(l => (l.Code, l.Label)), _lang, "The language to look for.")
        {
            Expands = true,
        };
        lang.Changed += v =>
        {
            _lang = v;
            Search();
        };
        page.AddChild(lang);
        var rows = new List<Control> { lang };
        if (_busy || _status != null)
        {
            page.AddChild(UiTheme.Label(_busy ? "Searching…" : _status!, UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        }
        foreach (var r in _results ?? [])
        {
            var bits = new List<string> { r.Provider };
            if (r.Score is { } sc)
            {
                bits.Add($"score {sc:0.#}");
            }
            if (r.Downloads is { } d)
            {
                bits.Add($"{d:N0} downloads");
            }
            if (r.HearingImpaired)
            {
                bits.Add("SDH");
            }
            var result = r;
            var row = new ListRow(r.Title, string.Join("  ·  ", bits), null, UiTheme.Accent, onPress: () => Fetch(result), help: "Subtitle: " + r.Title)
            {
                CustomMinimumSize = new Vector2(0, 56),
            };
            page.AddChild(row);
            rows.Add(row);
        }
        Focus.Column(rows);
        _first = rows[0];
        _firstResult = rows.Count > 1 ? rows[1] : null;
        if (_results == null && !_busy && _status == null)
        {
            Callable.From(Search).CallDeferred();
        }
    }

    private async void Search()
    {
        var n = ++_search;
        _busy = true;
        _status = null;
        _results = null;
        Rebuild();
        IReadOnlyList<SubtitleResult> found;
        try
        {
            found = await Host.Library.SearchSubtitlesAsync(_item, _lang);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            AppLog.Info($"subtitle search for {_item.Id}: {e.Message}");
            found = [];
            _status = "The server couldn't search right now: " + e.Message;
        }
        if (!IsInstanceValid(this) || n != _search)
        {
            return;
        }
        _busy = false;
        _results = found;
        if (found.Count == 0 && _status == null)
        {
            _status = Host.Library.CanSearchSubtitles(_item)
                ? "Nothing found in this language."
                : "This server can't search for subtitles (on Jellyfin, add the OpenSubtitles plugin).";
        }
        Rebuild();
        if (found.Count > 0)
        {
            Callable.From(() => _firstResult?.GrabFocus()).CallDeferred();
        }
    }

    private async void Fetch(SubtitleResult r)
    {
        App.Toast("Getting the subtitle…");
        string? url;
        try
        {
            url = await Host.Library.FetchSubtitleAsync(_item, r);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            AppLog.Info($"subtitle fetch {r.Id}: {e.Message}");
            url = null;
        }
        if (!IsInstanceValid(this))
        {
            return;
        }
        if (url == null)
        {
            App.Toast("Couldn't get that subtitle. Try another one.");
            return;
        }
        PlayerService.Instance.AddSubtitle(url, r.Title, r.Language);
        App.Toast("Subtitles: " + r.Title);
        App.Pop();
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
