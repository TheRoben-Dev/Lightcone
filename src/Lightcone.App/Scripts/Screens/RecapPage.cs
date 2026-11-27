using Godot;
using Lightcone.Host;
using Lightcone.App.Playback;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// "Previously": shown before an episode when the show hasn't been watched for a while (Settings
/// → Playback → Recap after a break). It names the last episode watched and what happened in it,
/// and can replay that episode's last two minutes (before its credits), which runs straight on
/// into the new one.
/// </summary>
public partial class RecapPage : Screen
{
    private readonly MediaItem _episode;
    private readonly MediaItem _previous;
    private Control? _first;

    public RecapPage(MediaItem episode, MediaItem previous)
    {
        _episode = episode;
        _previous = previous;
    }

    public override bool IsOverlay => true;

    public override void AnimateOpen() => Anim.FadeIn(this, Anim.Normal);

    /// <summary>"S2 · E4", "Special 3", "E5", or null.</summary>
    public static string? Code(MediaItem e) => (e.Season, e.Index) switch
    {
        (0, { } n) => $"Special {n}",
        ({ } s, { } n) => $"S{s} · E{n}",
        (null, { } n) => $"E{n}",
        _ => null,
    };

    /// <summary>"today", "yesterday", "5 days ago", "3 weeks ago", "4 months ago".</summary>
    public static string Ago(DateTime at)
    {
        var days = (int)(DateTime.Now.Date - at.ToLocalTime().Date).TotalDays;
        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 14 => $"{days} days ago",
            < 60 => $"{days / 7} weeks ago",
            < 730 => $"{days / 30} months ago",
            _ => $"{days / 365} years ago",
        };
    }

    /// <summary>
    /// The episode to recap before <paramref name="episode"/> plays from the start, or null: the
    /// one before it was watched, and the show hasn't been played for the days set in Settings.
    /// </summary>
    public static MediaItem? RecapFor(MediaItem episode, bool fromStart)
    {
        var host = Main.Instance.Host!;
        var lib = host.Library;
        var after = host.Settings.RecapAfterDays;
        if (after < 0 || episode.Kind != MediaKind.Episode || episode.ParentId == null || (!fromStart && lib.PositionOf(episode) != null))
        {
            return null;
        }
        if (lib.PreviousEpisode(episode) is not { } prev || !lib.IsWatched(prev))
        {
            return null;
        }
        var show = lib.Find(episode.ParentId);
        var last = show != null ? lib.ShowLastViewed(show) : lib.LastViewed(prev);
        return last == null || after == 0 || DateTime.UtcNow - last.Value.ToUniversalTime() >= TimeSpan.FromDays(after) ? prev : null;
    }

    protected override void Build()
    {
        var lib = Host.Library;
        _ = lib.PrepareAsync(_previous);  // its credits marker, for where the replay starts
        var col = Column(14);
        AddChild(CardFrame(Scroller(col)));
        var showTitle = _episode.ParentTitle ?? lib.Find(_episode.ParentId ?? "")?.Title ?? "";
        var last = lib.Find(_episode.ParentId ?? "") is { } show ? lib.ShowLastViewed(show) : null;
        col.AddChild(UiTheme.Label(last is { } at ? $"PREVIOUSLY ON {showTitle.ToUpperInvariant()}  ·  LAST WATCHED {Ago(at).ToUpperInvariant()}" : $"PREVIOUSLY ON {showTitle.ToUpperInvariant()}",
            UiTheme.SmallSize, UiTheme.AccentText, wrap: true));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 24);
        col.AddChild(row);
        row.AddChild(new PosterArt(_previous, Desktop ? new Vector2(256, 144) : new Vector2(320, 180), null, 12, showTitle: false) { Badge = "✓" });
        var text = Column(8);
        row.AddChild(text);
        var code = Code(_previous);
        text.AddChild(UiTheme.Label(code != null ? $"{code}  ·  {_previous.Title}" : _previous.Title, UiTheme.FontSize + 4, UiTheme.Text, wrap: true));
        var overview = _previous.Overview is { Length: > 0 } o ? o.ReplaceLineEndings(" ") : "No summary for this episode on the server.";
        var ov = UiTheme.Label(overview, UiTheme.FontSize - 2, _previous.Overview != null ? UiTheme.Text : UiTheme.Dim, wrap: true);
        ov.CustomMinimumSize = new Vector2(360, 0);
        text.AddChild(ov);

        var buttons = new List<Control>();
        var next = Code(_episode) ?? _episode.Title;
        void Act(string label, Action a, string help)
        {
            var b = new ActionButton(label, a, help) { CustomMinimumSize = new Vector2(0, Desktop ? 44 : 54) };
            b.SetMeta(PosterTile.NoHelp, true);
            col.AddChild(b);
            buttons.Add(b);
        }
        Act($"▶  Play {next}", () =>
        {
            App.Pop();
            MediaActions.Play(_episode, fromStart: true, recap: false);
        }, $"{_episode.Title}: play");
        if (_previous.Duration is > 150)
        {
            Act($"Replay the last 2 minutes of {code ?? _previous.Title}, then {next}", ReplayEnding, $"{_previous.Title}: replay the ending");
        }
        Act("Back", () => App.Pop(), "Back");
        Focus.Column(buttons);
        _first = buttons[0];
    }

    private void ReplayEnding()
    {
        var lib = Host.Library;
        var d = _previous.Duration ?? 0;
        // The two minutes before the final credits (or the end), so the story bit is what replays.
        var credits = lib.MarkersOf(_previous).Where(m => m.Kind == Marker.Credits && m.End >= d - 30).Select(m => m.Start).DefaultIfEmpty(d).Min();
        var start = Math.Max(0, credits - 120);
        AppLog.Info($"recap: replay {_previous.Id} from {start:0}s");
        App.Pop();
        if (PlayerService.EngineAvailable(out _))
        {
            PlayerService.Instance.Play(_previous, startAt: start);
            App.ShowPlayer();
        }
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
