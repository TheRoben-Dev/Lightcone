using Lightcone.Host;
using Godot;
using Lightcone.App.Playback;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// The full-screen player, shared by both modes. Controller: A pause, B stop, triggers and
/// left/right seek, X (or Menu) audio and subtitles, Y skip intro/credits or info. Desktop: a
/// mouse control bar (play, seek bar, tracks, mini player, full screen) and Space / arrows / F.
/// Touch: tap shows the controls, the seek bar drags.
/// </summary>
public partial class PlayerScreen : Screen
{
    private const double OsdSeconds = 3.5;

    private Control _osd = null!;
    private SeekBar _bar = null!;
    private Label _title = null!;
    private Label _subtitle = null!;
    private Label _time = null!;
    private Label _info = null!;
    private Label _pausedIcon = null!;
    private Button _skip = null!;
    private Button? _playButton;
    private Control? _audioArt;
    private double _osdLeft = OsdSeconds;
    private bool _showInfo;
    private MediaItem? _shownItem;

    public override bool FullScreen => true;

    public override IEnumerable<(string Action, string Label)> Hints => [];

    private static PlayerService Svc => PlayerService.Instance;

    protected override void Build()
    {
        // No margins: the picture goes edge to edge.
        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            AddThemeConstantOverride($"margin_{side}", 0);
        }
        var root = new Control { MouseFilter = MouseFilterEnum.Stop, FocusMode = FocusModeEnum.All, TooltipText = "Player" };
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.GuiInput += OnPointer;
        AddChild(root);
        var video = new VideoView();
        video.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddChild(video);
        _audioArt = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        _audioArt.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddChild(_audioArt);

        _pausedIcon = UiTheme.Label("❚❚", 72, new Color(1, 1, 1, 0.85f));
        _pausedIcon.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        _pausedIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _pausedIcon.GrowHorizontal = GrowDirection.Both;
        _pausedIcon.GrowVertical = GrowDirection.Both;
        root.AddChild(_pausedIcon);

        _osd = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _osd.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddChild(_osd);

        // Top: title, episode line, clock.
        var top = Gradient(true);
        top.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
        top.OffsetBottom = 120;
        _osd.AddChild(top);
        var topBox = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        topBox.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
        topBox.OffsetLeft = 40;
        topBox.OffsetTop = 24;
        topBox.OffsetRight = -40;
        _osd.AddChild(topBox);
        _title = UiTheme.Label("", UiTheme.TitleSize - 6, Colors.White);
        _subtitle = UiTheme.Label("", UiTheme.SmallSize + 2, new Color(1, 1, 1, 0.75f));
        topBox.AddChild(_title);
        topBox.AddChild(_subtitle);
        _info = UiTheme.Label("", UiTheme.SmallSize, new Color(1, 1, 1, 0.75f), wrap: true);
        topBox.AddChild(_info);

        // Bottom: seek bar, times, then hints (controller) or buttons (desktop).
        var bottom = Gradient(false);
        bottom.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        bottom.OffsetTop = -170;
        _osd.AddChild(bottom);
        var bottomBox = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        bottomBox.AddThemeConstantOverride("separation", 10);
        bottomBox.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        bottomBox.OffsetLeft = 40;
        bottomBox.OffsetRight = -40;
        bottomBox.OffsetBottom = -24;
        bottomBox.GrowVertical = GrowDirection.Begin;
        _osd.AddChild(bottomBox);
        var timeRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        bottomBox.AddChild(timeRow);
        _time = UiTheme.Label("", UiTheme.SmallSize + 2, Colors.White);
        timeRow.AddChild(_time);
        timeRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        _skip = new Button { Text = "Skip intro", Visible = false, FocusMode = FocusModeEnum.None, TooltipText = "Skip" };
        _skip.Pressed += Svc.SkipMarker;
        timeRow.AddChild(_skip);
        _bar = new SeekBar { CustomMinimumSize = new Vector2(0, 26) };
        _bar.Seek += f =>
        {
            if (Svc.Duration is { } d)
            {
                Svc.SeekTo(d * f);
                Poke();
            }
        };
        bottomBox.AddChild(_bar);
        if (Desktop)
        {
            bottomBox.AddChild(DesktopControls());
        }
        else
        {
            var hints = UiTheme.Label("", UiTheme.SmallSize + 1, new Color(1, 1, 1, 0.8f));
            hints.Text = $"{Pad.Glyph(Pad.Accept)} Pause    {Pad.Glyph(Pad.SeekBack)} / {Pad.Glyph(Pad.SeekForward)} Seek    {Pad.Glyph(Pad.Tertiary)} Audio & subtitles    {Pad.Glyph(Pad.Secondary)} Skip / info    {Pad.Glyph(Pad.Back)} {(Svc.HasVideo ? "Stop" : "Back (keeps playing)")}";
            bottomBox.AddChild(hints);
        }
        Svc.Changed += OnChanged;
        Svc.Finished += OnFinished;
        TreeExiting += () =>
        {
            Svc.Changed -= OnChanged;
            Svc.Finished -= OnFinished;
        };
        OnChanged();
    }

    private HBoxContainer DesktopControls()
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 10);
        Button B(string text, string tip, Action a)
        {
            var b = new Button { Text = text, TooltipText = tip, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(44, 40) };
            b.Pressed += () =>
            {
                a();
                Poke();
            };
            row.AddChild(b);
            return b;
        }
        B($"⟲ {Host.Settings.SeekBack}", $"Back {Host.Settings.SeekBack} seconds (Left, hold to go faster)", () => Svc.Step(-1));
        _playButton = B("❚❚", "Play / pause (Space)", Svc.TogglePause);
        B($"{Host.Settings.SeekForward} ⟳", $"Forward {Host.Settings.SeekForward} seconds (Right, hold to go faster)", () => Svc.Step(1));
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        B("Audio & subtitles", "Audio and subtitle tracks (A)", () => App.Push(new TrackPickerPage()));
        B("Mini player", "Keep playing in the mini player while you browse", App.MinimizePlayer);
        B("Full screen", "Full screen (F)", ToggleFullscreen);
        B("✕", "Stop (Esc)", () =>
        {
            Svc.Stop();
            App.Pop();
        });
        return row;
    }

    private static void ToggleFullscreen()
    {
        var w = Main.Instance.GetWindow();
        w.Mode = w.Mode is Window.ModeEnum.Fullscreen or Window.ModeEnum.ExclusiveFullscreen ? Window.ModeEnum.Windowed : Window.ModeEnum.Fullscreen;
    }

    private static Control Gradient(bool top)
    {
        var tex = new GradientTexture2D
        {
            Gradient = new Gradient { Offsets = [0f, 1f], Colors = [new Color(0, 0, 0, 0.75f), new Color(0, 0, 0, 0)] },
            FillFrom = top ? new Vector2(0, 0) : new Vector2(0, 1),
            FillTo = top ? new Vector2(0, 1) : new Vector2(0, 0),
            Width = 4,
            Height = 64,
        };
        return new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
    }

    private void OnChanged()
    {
        if (!IsInstanceValid(this))
        {
            return;
        }
        var item = Svc.Current;
        if (item != _shownItem && _audioArt != null)
        {
            _shownItem = item;
            foreach (var c in _audioArt.GetChildren())
            {
                c.QueueFree();
            }
            if (item != null && !Svc.HasVideo)
            {
                var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
                col.AddThemeConstantOverride("separation", 16);
                var parent = item.ParentId != null ? Host.Library.Find(item.ParentId) : null;
                col.AddChild(new PosterArt(parent ?? item, new Vector2(320, 320), null, 16));
                var l = UiTheme.Label(item.Title, UiTheme.FontSize + 4, UiTheme.Text);
                l.HorizontalAlignment = HorizontalAlignment.Center;
                col.AddChild(l);
                _audioArt.AddChild(col);
            }
        }
        _audioArt!.Visible = !Svc.HasVideo;
        if (item != null)
        {
            var isEpisode = item.Kind is MediaKind.Episode or MediaKind.Track;
            _title.Text = isEpisode && item.Subtitle != null ? item.Subtitle.Split(" · ")[0] : item.Title;
            _subtitle.Text = isEpisode ? item.Title + (item.Subtitle?.Contains(" · ") == true ? "  ·  " + string.Join(" · ", item.Subtitle.Split(" · ").Skip(1)) : "") : item.Subtitle ?? "";
        }
        Poke();
    }

    private void OnFinished(MediaItem done)
    {
        // Episodes and tracks continue with the next one; anything else closes the player.
        var next = done.ParentId == null ? null :
            Host.Library.Children(done.ParentId).SkipWhile(c => c.Id != done.Id).Skip(1).FirstOrDefault(c => c.Playable);
        if (next != null)
        {
            AppLog.Info($"up next: {next.Id}");
            Svc.Play(next, fromStart: true);
            App.Toast("Up next: " + next.Title);
            return;
        }
        Svc.Stop();
        if (App.Top == this)
        {
            App.Pop();
        }
    }

    /// <summary>Shows the controls for a few seconds.</summary>
    private void Poke() => _osdLeft = OsdSeconds;

    public override void _Process(double delta)
    {
        var paused = Svc.Paused;
        if (!paused)
        {
            _osdLeft -= delta;
        }
        var show = _osdLeft > 0 || paused || !Svc.HasVideo || _showInfo;
        var target = show ? 1f : 0f;
        _osd.Modulate = _osd.Modulate with { A = Mathf.MoveToward(_osd.Modulate.A, target, (float)delta * 5f) };
        _pausedIcon.Visible = paused && Svc.Current != null;
        if (Desktop)
        {
            Input.MouseMode = show ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Hidden;
        }
        var d = Svc.Duration;
        var pos = Svc.DisplayPosition;
        _bar.Fraction = d is > 0 ? (float)(pos / d.Value) : 0;
        var clock = d is > 0 ? _bar.Shown * d.Value : pos;  // the clock rides along with the bar's glide
        _time.Text = d is { } dd ? $"{MediaItem.FormatDuration(clock, true)} / {MediaItem.FormatDuration(dd, true)}" : MediaItem.FormatDuration(pos, true);
        _bar.Markers = Svc.Current is { } cur && d is > 0 ? Host.Library.MarkersOf(cur).Select(m => ((float)(m.Start / d.Value), (float)(m.End / d.Value))).ToList() : [];
        var marker = Svc.ActiveMarker;
        _skip.Visible = marker != null;
        if (marker != null)
        {
            _skip.Text = Desktop ? marker.Label : $"{Pad.Glyph(Pad.Secondary)}  {marker.Label}";
        }
        if (_playButton != null)
        {
            _playButton.Text = paused ? "▶" : "❚❚";
        }
        _info.Visible = _showInfo;
        if (_showInfo)
        {
            _info.Text = $"Decoding: {(Svc.HwdecCurrent is "" or "no" ? "software" : Svc.HwdecCurrent)}  ·  Drawing: {Svc.RenderPath}  ·  " +
                         string.Join("  ·  ", Svc.Tracks.Where(t => t.Selected && t.Kind != Lightcone.Playback.TrackKind.Video).Select(t => t.Label));
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (App.Top != this || !e.IsPressed())
        {
            return;
        }
        if (e.IsAction(Pad.Accept) && !e.IsEcho())
        {
            Svc.TogglePause();
        }
        else if (e.IsAction("ui_left") || e.IsAction(Pad.SeekBack))
        {
            Svc.Step(-1);
        }
        else if (e.IsAction("ui_right") || e.IsAction(Pad.SeekForward))
        {
            Svc.Step(1);
        }
        else if (e.IsAction("ui_up") || e.IsAction("ui_down"))
        {
            // Just show the controls.
        }
        else if (e.IsAction(Pad.Menu) || e.IsAction(Pad.Tertiary) || e is InputEventKey { Keycode: Key.A })
        {
            App.Push(new TrackPickerPage());
        }
        else if (e is InputEventKey { Keycode: Key.F, Pressed: true, Echo: false })
        {
            ToggleFullscreen();
        }
        else if (e.IsAction(Pad.Secondary))
        {
            if (Svc.ActiveMarker != null)
            {
                Svc.SkipMarker();
            }
            else
            {
                _showInfo = !_showInfo;
            }
        }
        else
        {
            return;
        }
        Poke();
        GetViewport().SetInputAsHandled();
    }

    private void OnPointer(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion:
                Poke();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true } when Desktop:
                ToggleFullscreen();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }:
                // A click on the picture: pause on desktop; on touch the first tap just shows the controls.
                if (Desktop || _osd.Modulate.A > 0.5f)
                {
                    Svc.TogglePause();
                }
                Poke();
                break;
        }
    }

    /// <summary>B: video stops (position saved); music keeps playing in the background.</summary>
    public override bool OnBack()
    {
        if (Svc.HasVideo)
        {
            Svc.Stop();
        }
        return false;
    }

    public override void FocusDefault() => GetChild<Control>(0).GrabFocus();
}

/// <summary>
/// Progress bar with intro/credit markers; click, tap or drag to seek. Jumps (resume, skips) glide
/// to the new spot with an ease-out that never takes longer than <see cref="SweepSeconds"/>,
/// however far the jump, which also covers the moment the player needs to get there.
/// </summary>
public partial class SeekBar : Control
{
    /// <summary>Longest a jump glides, whatever the distance.</summary>
    public const double SweepSeconds = 0.7;

    private float _fraction;
    private float _shown;
    private float _from;
    private double _t = -1;  // seconds into the current glide; -1 = none

    public event Action<float>? Seek;

    public IReadOnlyList<(float Start, float End)> Markers { get; set; } = [];

    /// <summary>Where the bar is drawn right now (part way through a glide).</summary>
    public float Shown => _shown;

    public float Fraction
    {
        get => _fraction;
        set
        {
            if (Math.Abs(value - _fraction) <= 0.0005f)
            {
                return;
            }
            // Playback creeps forward in tiny steps; anything bigger is a jump worth a glide.
            if (Math.Abs(value - _shown) > 0.004f && Anim.Enabled)
            {
                if (_t < 0)
                {
                    _from = _shown;
                    _t = 0;
                }
                // A new target mid-glide keeps the glide's clock, so it still ends on time.
            }
            else if (_t < 0)
            {
                _shown = value;
            }
            _fraction = value;
            QueueRedraw();
        }
    }

    public SeekBar()
    {
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
    }

    public override void _Process(double delta)
    {
        if (_t < 0)
        {
            return;
        }
        _t += delta;
        var k = (float)Math.Min(1, _t / SweepSeconds);
        var ease = 1 - (1 - k) * (1 - k) * (1 - k);  // ease-out cubic
        _shown = Mathf.Lerp(_from, _fraction, ease);
        if (k >= 1)
        {
            _shown = _fraction;
            _t = -1;
        }
        QueueRedraw();
    }

    private void Jump(float f)
    {
        _t = -1;
        _shown = f;
        Seek?.Invoke(f);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb)
        {
            Jump(Math.Clamp(mb.Position.X / Size.X, 0, 1));
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion { ButtonMask: MouseButtonMask.Left } mm)
        {
            Jump(Math.Clamp(mm.Position.X / Size.X, 0, 1));
            AcceptEvent();
        }
    }

    public override void _Draw()
    {
        var h = 6f;
        var y = (Size.Y - h) / 2;
        DrawRect(new Rect2(0, y, Size.X, h), new Color(1, 1, 1, 0.25f));
        foreach (var (s, e) in Markers)
        {
            DrawRect(new Rect2(Size.X * s, y, Size.X * (e - s), h), new Color(1, 1, 1, 0.45f));
        }
        DrawRect(new Rect2(0, y, Size.X * _shown, h), UiTheme.Accent);
        DrawCircle(new Vector2(Size.X * _shown, Size.Y / 2), 9, UiTheme.Accent);
    }
}
