using Godot;
using Lightcone.App.Playback;
using Lightcone.App.Screens;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Desktop;

/// <summary>
/// Desktop mode's bottom bar while something plays: a live thumbnail (or the cover), title, transport,
/// a seek bar and a button back to the full player. Controller mode reaches the same controls through
/// the header chip and the player screen.
/// </summary>
public partial class MiniPlayer : PanelContainer
{
    private readonly Control _thumb;
    private readonly VideoView _video;
    private readonly Label _title;
    private readonly Label _sub;
    private readonly Label _time;
    private readonly Button _play;
    private readonly SeekBar _bar;
    private PosterArt? _cover;
    private string? _coverFor;

    public MiniPlayer()
    {
        Visible = false;
        CustomMinimumSize = new Vector2(0, 84);
        AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel.Darkened(0.2f), 0, 0));
        var margin = new MarginContainer();
        foreach (var (side, v) in new[] { ("left", 14), ("right", 18), ("top", 8), ("bottom", 8) })
        {
            margin.AddThemeConstantOverride($"margin_{side}", v);
        }
        AddChild(margin);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 14);
        margin.AddChild(row);

        _thumb = new Control { CustomMinimumSize = new Vector2(120, 68), ClipContents = true, MouseFilter = MouseFilterEnum.Stop, TooltipText = "Open the player" };
        _thumb.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
            {
                Main.Instance.ShowPlayer();
            }
        };
        _video = new VideoView();
        _video.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _thumb.AddChild(_video);
        row.AddChild(_thumb);

        var text = new VBoxContainer { CustomMinimumSize = new Vector2(220, 0), Alignment = BoxContainer.AlignmentMode.Center };
        _title = UiTheme.Label("", UiTheme.FontSize, UiTheme.Text);
        _title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _sub = UiTheme.Label("", UiTheme.SmallSize, UiTheme.Dim);
        _sub.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        text.AddChild(_title);
        text.AddChild(_sub);
        row.AddChild(text);

        var transport = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        transport.AddThemeConstantOverride("separation", 4);
        transport.AddChild(Small("⟲", $"Back {Main.Instance.Host?.Settings.SeekBack ?? 10} s", () => PlayerService.Instance.Step(-1)));
        _play = Small("❚❚", "Play / pause (Space)", () => PlayerService.Instance.TogglePause());
        _play.CustomMinimumSize = new Vector2(52, 44);
        transport.AddChild(_play);
        transport.AddChild(Small("⟳", $"Forward {Main.Instance.Host?.Settings.SeekForward ?? 30} s", () => PlayerService.Instance.Step(1)));
        transport.AddChild(Small("■", "Stop", () => PlayerService.Instance.Stop()));
        row.AddChild(transport);

        var seek = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
        _bar = new SeekBar { CustomMinimumSize = new Vector2(0, 14) };
        _bar.Seek += f =>
        {
            if (PlayerService.Instance.Duration is > 0 and var d)
            {
                PlayerService.Instance.SeekTo(f * d);
            }
        };
        seek.AddChild(_bar);
        _time = UiTheme.Label("", UiTheme.SmallSize, UiTheme.Dim);
        seek.AddChild(_time);
        row.AddChild(seek);

        row.AddChild(Small("⤢", "Open the player", () => Main.Instance.ShowPlayer()));
    }

    private static Button Small(string glyph, string tip, Action onPress)
    {
        var b = new Button { Text = glyph, TooltipText = tip, Flat = true, CustomMinimumSize = new Vector2(44, 44), FocusMode = FocusModeEnum.All };
        b.AddThemeFontSizeOverride("font_size", 22);
        b.Pressed += onPress;
        return b;
    }

    /// <summary>Re-reads what's playing (title, cover vs. live video).</summary>
    public void Refresh()
    {
        var svc = PlayerService.Instance;
        var cur = svc.Current;
        if (cur == null)
        {
            return;
        }
        _title.Text = cur.Title;
        _sub.Text = cur.Subtitle ?? cur.SourceName;
        _play.Text = svc.Paused ? "▶" : "❚❚";
        _video.Visible = svc.HasVideo;
        if (!svc.HasVideo && _coverFor != cur.Id)
        {
            _cover?.QueueFree();
            var art = cur.ParentId != null && Main.Instance.Host?.Library.Find(cur.ParentId) is { } parent ? parent : cur;
            _cover = new PosterArt(art, new Vector2(68, 68), null, 6, false) { MouseFilter = MouseFilterEnum.Ignore };
            _thumb.AddChild(_cover);
            _coverFor = cur.Id;
        }
        if (_cover != null)
        {
            _cover.Visible = !svc.HasVideo;
        }
    }

    public override void _Process(double delta)
    {
        if (!Visible)
        {
            return;
        }
        var svc = PlayerService.Instance;
        var d = svc.Duration ?? 0;
        _bar.Fraction = d > 0 ? (float)(svc.DisplayPosition / d) : 0;
        _time.Text = d > 0
            ? $"{MediaItem.FormatDuration(svc.DisplayPosition, true)} / {MediaItem.FormatDuration(d, true)}"
            : MediaItem.FormatDuration(svc.DisplayPosition, true);
        var glyph = svc.Paused ? "▶" : "❚❚";
        if (_play.Text != glyph)
        {
            _play.Text = glyph;
        }
    }
}
