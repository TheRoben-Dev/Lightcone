using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// "Surprise me": one random pick from what you haven't watched (a movie, or a show with episodes
/// left), weighted toward the watchlist and shows you've started. Y (or Another one) picks again
/// without repeats; what to pick from and how long it may run are steppers on the card.
/// </summary>
public partial class SurprisePage : Screen
{
    private static string _what = "all";
    private static string _time = "0";
    private readonly HashSet<string> _seen = new();
    private MediaItem? _pick;
    private Control? _first;
    private bool _picked;

    public override bool IsOverlay => true;

    public override void AnimateOpen() => Anim.FadeIn(this, Anim.Normal);

    public override IEnumerable<(string Action, string Label)> Hints => [(Pad.Secondary, "Another one")];

    private void Roll()
    {
        var lib = Host.Library;
        var max = int.Parse(_time, System.Globalization.CultureInfo.InvariantCulture);
        bool movies = _what != "shows", shows = _what != "movies";
        _pick = lib.Surprise(Random.Shared, movies, shows, max > 0 ? max : null, _seen);
        if (_pick == null && _seen.Count > 0)
        {
            _seen.Clear();  // went through them all: start over
            _pick = lib.Surprise(Random.Shared, movies, shows, max > 0 ? max : null, _seen);
        }
        if (_pick != null)
        {
            _seen.Add(_pick.Id);
        }
        _picked = true;
    }

    protected override void Build()
    {
        if (!_picked)
        {
            Roll();
        }
        var lib = Host.Library;
        var col = Column(14);
        AddChild(CardFrame(Scroller(col)));
        col.AddChild(UiTheme.Label("SURPRISE ME", UiTheme.SmallSize, UiTheme.AccentText));
        var buttons = new List<Control>();
        void Act(HBoxContainer row, string label, Action a, string help)
        {
            var b = new ActionButton(label, a, help) { CustomMinimumSize = new Vector2(0, Desktop ? 44 : 54) };
            b.SetMeta(PosterTile.NoHelp, true);
            row.AddChild(b);
            buttons.Add(b);
        }
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 14);
        if (_pick is { } pick)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 24);
            col.AddChild(row);
            row.AddChild(new PosterArt(pick, Desktop ? new Vector2(150, 225) : new Vector2(180, 270), PosterTile.ProgressOf(lib, pick), 12, showTitle: false));
            var text = Column(8);
            row.AddChild(text);
            text.AddChild(UiTheme.Label(pick.Title, UiTheme.TitleSize - 6, UiTheme.Text, wrap: true));
            var meta = new List<string>();
            if (pick.Year is { } y)
            {
                meta.Add(y.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            var next = pick.Kind == MediaKind.Show ? lib.NextToPlay(pick) : null;
            if ((next ?? pick).DurationText is { } d)
            {
                meta.Add(next != null ? $"{d} episodes" : d);
            }
            if (pick.Rating is { } r)
            {
                meta.Add($"★ {r:0.0}");
            }
            meta.AddRange(pick.Genres.Take(3));
            text.AddChild(UiTheme.Label(string.Join("  ·  ", meta), UiTheme.SmallSize + 2, UiTheme.Dim, wrap: true));
            text.AddChild(UiTheme.Label(Why(pick, next), UiTheme.SmallSize + 1, UiTheme.AccentText, wrap: true));
            if (pick.Overview is { Length: > 0 } o)
            {
                var ov = UiTheme.Label(o.ReplaceLineEndings(" "), UiTheme.FontSize - 2, UiTheme.Text, wrap: true);
                ov.MaxLinesVisible = 5;
                ov.CustomMinimumSize = new Vector2(360, 0);
                text.AddChild(ov);
            }
            text.AddChild(actions);
            var play = next != null ? $"▶  {(lib.PositionOf(next) != null ? "Resume" : "Play")} {RecapPage.Code(next) ?? next.Title}"
                : lib.PositionOf(pick) is { } pos ? $"▶  Resume from {MediaItem.FormatDuration(pos, clock: true)}" : "▶  Play";
            Act(actions, play, () =>
            {
                App.Pop();
                if (next != null)
                {
                    MediaActions.Play(next);
                }
                else
                {
                    MediaActions.Play(pick);
                }
            }, $"Surprise: play {pick.Title}");
            Act(actions, "Details", () =>
            {
                App.Pop();
                App.Push(new DetailPage(pick));
            }, $"Surprise: details of {pick.Title}");
        }
        else
        {
            col.AddChild(UiTheme.Label("Nothing unwatched fits these choices. Try anything, or any length.", UiTheme.FontSize - 1, UiTheme.Dim, wrap: true));
            col.AddChild(actions);
        }
        Act(actions, Desktop ? "Another one" : $"{Pad.Glyph(Pad.Secondary)}  Another one", Another, "Surprise: another one");

        var what = new Stepper("Pick from", [("all", "Movies and shows"), ("movies", "Movies"), ("shows", "Shows")], _what,
            "What the pick comes from. Only things you haven't finished.");
        what.Changed += v =>
        {
            _what = v;
            Callable.From(Another).CallDeferred();
        };
        var time = new Stepper("Time I have", [("0", "Any length"), ("30", "Under 30 min"), ("60", "Under 1 hour"), ("100", "Under 1 h 40 min"), ("130", "Under 2 h 10 min")], _time,
            "Longest it may run: a movie, or one episode of a show.");
        time.Changed += v =>
        {
            _time = v;
            Callable.From(Another).CallDeferred();
        };
        col.AddChild(what);
        col.AddChild(time);
        Focus.Row(buttons, below: what);
        Focus.Column([what, time], above: buttons[0]);
        _first = buttons[0];
    }

    /// <summary>Why this came up: "On your watchlist", "You're 3 episodes in", ...</summary>
    private string Why(MediaItem pick, MediaItem? next)
    {
        var lib = Host.Library;
        var reasons = new List<string>();
        if (lib.OnWatchlist(pick))
        {
            reasons.Add("On your watchlist");
        }
        if (pick.Kind == MediaKind.Show)
        {
            var eps = lib.Children(pick.Id).Where(e => e.Playable).ToList();
            var seen = eps.Count(lib.IsWatched);
            reasons.Add(seen == 0 ? $"New to you: {eps.Count} episodes" : $"You're {seen} of {eps.Count} episodes in");
        }
        else
        {
            reasons.Add(lib.PositionOf(pick) != null ? "You started this one" : "Not watched yet");
        }
        return string.Join("  ·  ", reasons);
    }

    private void Another()
    {
        Roll();
        Rebuild();  // focus stays on the same control
    }

    public override void OnAction(string action)
    {
        if (action == Pad.Secondary)
        {
            Another();
        }
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
