using Godot;
using Lightcone.Host.Library;

namespace Lightcone.App.Ui;

/// <summary>
/// The player's "Up next" card: the next episode's thumbnail and name, a countdown bar, and Play
/// now / Watch credits (or, after a long autoplay run, "Still watching?" with Continue / Stop).
/// Controller mode answers with A / B (the player routes them); the buttons take clicks and taps.
/// </summary>
public partial class UpNextCard : PanelContainer
{
    private readonly bool _desktop;
    private readonly Action _play;
    private readonly Action _dismiss;
    private readonly HBoxContainer _row;
    private readonly Label _kicker;
    private readonly Label _title;
    private readonly Label _status;
    private readonly ProgressBar _bar;
    private readonly Button _playButton;
    private readonly Button _dismissButton;
    private Control? _art;

    public MediaItem? Item { get; private set; }

    public UpNextCard(bool desktop, Action play, Action dismiss)
    {
        _desktop = desktop;
        _play = play;
        _dismiss = dismiss;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(desktop ? 460 : 540, 0);
        var box = UiTheme.Box(UiTheme.Panel with { A = 0.92f }, 1, 16, UiTheme.Line);
        box.ContentMarginLeft = box.ContentMarginRight = box.ContentMarginTop = box.ContentMarginBottom = 14;
        box.ShadowColor = new Color(0, 0, 0, 0.5f);
        box.ShadowSize = 18;
        AddThemeStyleboxOverride("panel", box);
        _row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _row.AddThemeConstantOverride("separation", 16);
        AddChild(_row);
        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        text.AddThemeConstantOverride("separation", 4);
        _row.AddChild(text);
        _kicker = UiTheme.Label("UP NEXT", UiTheme.SmallSize, UiTheme.AccentText);
        _title = UiTheme.Label("", UiTheme.FontSize, UiTheme.Text);
        _title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _title.CustomMinimumSize = new Vector2(desktop ? 200 : 240, 0);
        _status = UiTheme.Label("", UiTheme.SmallSize, UiTheme.Dim);
        _bar = new ProgressBar { ShowPercentage = false, ClipContents = true, CustomMinimumSize = new Vector2(0, 6), MaxValue = 1, Step = 0, MouseFilter = MouseFilterEnum.Ignore };
        _bar.AddThemeFontSizeOverride("font_size", 1);
        _bar.AddThemeStyleboxOverride("background", UiTheme.Box(new Color(1, 1, 1, 0.15f), 0, 3));
        _bar.AddThemeStyleboxOverride("fill", UiTheme.Box(UiTheme.Accent, 0, 3));
        text.AddChild(_kicker);
        text.AddChild(_title);
        text.AddChild(_status);
        text.AddChild(_bar);
        var buttons = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        buttons.AddThemeConstantOverride("separation", 10);
        text.AddChild(buttons);
        _playButton = new Button { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 40), TooltipText = "Up next: play now" };
        _playButton.Pressed += () => _play();
        _dismissButton = new Button { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 40), TooltipText = "Up next: dismiss" };
        _dismissButton.Pressed += () => _dismiss();
        buttons.AddChild(_playButton);
        buttons.AddChild(_dismissButton);
    }

    /// <summary>Fills the card for <paramref name="next"/>; <paramref name="askStill"/> asks "Still watching?".</summary>
    public void Fill(MediaItem next, bool askStill)
    {
        Item = next;
        _art?.QueueFree();
        _art = new PosterArt(next, _desktop ? new Vector2(176, 99) : new Vector2(208, 117), null, 10, showTitle: false);
        _row.AddChild(_art);
        _row.MoveChild(_art, 0);
        var code = (next.Season, next.Index) switch
        {
            (0, { } n) => $"Special {n}",
            ({ } s, { } n) => $"S{s} · E{n}",
            (null, { } n) => $"E{n}",
            _ => null,
        };
        _kicker.Text = askStill ? "STILL WATCHING?" : code != null ? $"UP NEXT  ·  {code}" : "UP NEXT";
        _title.Text = next.Title;
        _bar.Visible = !askStill;
        _bar.Value = 0;
        var a = _desktop ? "" : Pad.Glyph(Pad.Accept) + "  ";
        var b = _desktop ? "" : Pad.Glyph(Pad.Back) + "  ";
        _playButton.Text = askStill ? $"{a}Keep watching" : $"{a}▶ Play now";
        _dismissButton.Text = askStill ? $"{b}Stop" : $"{b}Watch credits";
        _status.Text = askStill ? "Are you still watching?" : "";
    }

    /// <summary>Status line ("Plays in 7 s") and countdown fill (0–1), or no bar.</summary>
    public void SetStatus(string text, double? fill)
    {
        _status.Text = text;
        if (fill is { } f)
        {
            _bar.Value = Math.Clamp(f, 0, 1);
        }
    }
}
