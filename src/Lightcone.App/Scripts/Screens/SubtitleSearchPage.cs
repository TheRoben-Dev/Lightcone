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
                CustomMinimumSize = new Vector2(0, 66),
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
        catch (Exception e)
        {
            AppLog.Warn($"subtitle search for {_item.Id}: {e.GetType().Name}: {e.Message}");
            found = [];
            _status = e switch
            {
                HttpRequestException h => SubtitleException.FromHttp(h, "The server", "search for subtitles").Message,
                TaskCanceledException => "The server took too long to search. OpenSubtitles may be slow right now; try again.",
                _ => "The server couldn't search right now: " + e.Message,
            };
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

    private bool _fetching;

    private async void Fetch(SubtitleResult r)
    {
        if (_fetching)
        {
            return;
        }
        _fetching = true;
        App.Toast("Getting the subtitle… the server fetches it from OpenSubtitles first.");
        string? file = null;
        string? error = null;
        try
        {
            file = await Host.Library.FetchSubtitleAsync(_item, r);
            if (file == null)
            {
                error = "This server can't fetch subtitles.";
            }
        }
        catch (SubtitleException e)
        {
            error = e.Message;
        }
        catch (TaskCanceledException)
        {
            error = "The server took too long to answer. Try again, or pick another one.";
        }
        catch (Exception e)
        {
            error = $"Couldn't get that subtitle: {e.Message}";
        }
        _fetching = false;
        if (error != null)
        {
            AppLog.Warn($"subtitle fetch {r.Id} for {_item.Id}: {error}");
            if (IsInstanceValid(this))
            {
                App.Toast(error);
            }
            return;
        }
        if (PlayerService.Instance.Current?.Id != _item.Id)
        {
            App.Toast("Got it, but playback moved on. It's saved on the server for next time.");
            return;
        }
        if (PlayerService.Instance.AddSubtitle(file!, r.Title, r.Language) is { } why)
        {
            App.Toast(why);
            return;
        }
        App.Toast("Subtitles on: " + r.Title);
        if (IsInstanceValid(this) && Main.Instance.TopOrNull == this)
        {
            App.Pop();
            // Back on the player options: show the new track, ticked.
            if (Main.Instance.TopOrNull is TrackPickerPage picker)
            {
                picker.Rebuild();
            }
        }
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
