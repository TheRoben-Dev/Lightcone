using Godot;
using Lightcone.Host;
using Lightcone.Host.Library;

namespace Lightcone.App.Ui;

/// <summary>
/// Ambient mode: after a few idle minutes on the menus, the screen turns into a slow slideshow of
/// the library's artwork with a clock (and moves a little each time, so nothing burns in on an
/// OLED). Any button, key, click or touch brings the menus back without doing anything else.
/// </summary>
public partial class AmbientMode : ColorRect
{
    private const double Slide = 14;

    private readonly Random _rng = new();
    private Control? _card;
    private Label _clock = null!;
    private Label _date = null!;
    private double _sinceSlide;
    private int _shown;
    private List<MediaItem> _pool = [];

    public AmbientMode()
    {
        Color = new Color(0.02f, 0.02f, 0.03f);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
    }

    public bool Showing => Visible;

    public void Begin(IEnumerable<MediaItem> items)
    {
        _pool = items.Where(i => i.Kind is MediaKind.Movie or MediaKind.Show).DistinctBy(i => i.Id).OrderBy(_ => _rng.Next()).ToList();
        foreach (var c in GetChildren())
        {
            c.QueueFree();
        }
        _clock = UiTheme.Label("", 96, UiTheme.Text);
        _date = UiTheme.Label("", UiTheme.FontSize + 2, UiTheme.Dim);
        var corner = new VBoxContainer();
        corner.AddThemeConstantOverride("separation", 0);
        corner.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight);
        corner.GrowHorizontal = GrowDirection.Begin;
        corner.GrowVertical = GrowDirection.Begin;
        corner.OffsetRight = -56;
        corner.OffsetBottom = -44;
        _clock.HorizontalAlignment = HorizontalAlignment.Right;
        _date.HorizontalAlignment = HorizontalAlignment.Right;
        corner.AddChild(_clock);
        corner.AddChild(_date);
        AddChild(corner);
        Visible = true;
        Modulate = Colors.Transparent;
        CreateTween().TweenProperty(this, "modulate", Colors.White, 1.2);
        _shown = 0;
        NextSlide();
        Tick();
        AppLog.Info($"ambient mode: {_pool.Count} titles");
    }

    public void End()
    {
        if (!Visible)
        {
            return;
        }
        Visible = false;
        foreach (var c in GetChildren())
        {
            c.QueueFree();
        }
        _card = null;
    }

    public override void _Process(double delta)
    {
        if (!Visible)
        {
            return;
        }
        _sinceSlide += delta;
        if (_sinceSlide >= Slide)
        {
            NextSlide();
        }
        Tick();
    }

    private void Tick()
    {
        var now = DateTime.Now;
        _clock.Text = now.ToString("HH:mm");
        _date.Text = now.ToString("dddd d MMMM");
    }

    private void NextSlide()
    {
        _sinceSlide = 0;
        var old = _card;
        if (old != null)
        {
            var t = old.CreateTween();
            t.TweenProperty(old, "modulate", Colors.Transparent, 1.2);
            t.TweenCallback(Callable.From(old.QueueFree));
        }
        if (_pool.Count == 0)
        {
            return;
        }
        var item = _pool[_shown++ % _pool.Count];
        var card = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        card.AddThemeConstantOverride("separation", 40);
        var size = GetViewportRect().Size;
        var art = new Vector2(300, 450) * Math.Clamp(size.Y / 1080f, 0.6f, 1.4f);
        card.AddChild(new PosterArt(item, art, null, 16, showTitle: false) { SizeFlagsVertical = SizeFlags.ShrinkCenter, CustomMinimumSize = art });
        var text = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, CustomMinimumSize = new Vector2(Math.Min(560, size.X * 0.4f), 0) };
        text.AddThemeConstantOverride("separation", 8);
        card.AddChild(text);
        text.AddChild(UiTheme.Label(UiTheme.Mono ? Loc.Upper("From your library") : "From your library", UiTheme.SmallSize, UiTheme.AccentText));
        var textW = text.CustomMinimumSize.X;
        var title = UiTheme.Label(item.Title, UiTheme.TitleSize + 6, UiTheme.Text, wrap: true);
        title.CustomMinimumSize = new Vector2(textW, 0);
        text.AddChild(title);
        var meta = new List<string>();
        if (item.Year is { } y)
        {
            meta.Add(y.ToString());
        }
        meta.AddRange(item.Genres.Take(3));
        if (meta.Count > 0)
        {
            text.AddChild(UiTheme.Label(string.Join("  ·  ", meta), UiTheme.SmallSize + 2, UiTheme.Dim));
        }
        if (Spoilers.Overview(item) is { } o)
        {
            var ov = UiTheme.Label(o, UiTheme.SmallSize + 2, UiTheme.Text with { A = 0.8f }, wrap: true);
            ov.MaxLinesVisible = 4;
            ov.CustomMinimumSize = new Vector2(textW, 0);
            text.AddChild(ov);
        }
        AddChild(card);
        MoveChild(card, 0);
        // A different spot each time (OLED-friendly), drifting slowly while it shows.
        var w = art.X + 40 + text.CustomMinimumSize.X;
        var x = (float)(_rng.NextDouble() * Math.Max(40, size.X - w - 120)) + 60;
        var yPos = (float)(_rng.NextDouble() * Math.Max(40, size.Y - art.Y - 220)) + 50;
        card.Position = new Vector2(x, yPos);
        card.Modulate = Colors.Transparent;
        var tw = card.CreateTween().SetParallel();
        tw.TweenProperty(card, "modulate", Colors.White, 1.4);
        tw.TweenProperty(card, "position", card.Position + new Vector2(_rng.Next(-30, 30), _rng.Next(-16, 16)), Slide + 1.5);
        _card = card;
    }
}
