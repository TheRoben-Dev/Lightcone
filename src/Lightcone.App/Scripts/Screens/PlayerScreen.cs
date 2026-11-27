using Lightcone.Host;
using Godot;
using Lightcone.App.Playback;
using Lightcone.App.Ui;
using Lightcone.Host.Library;
using Lightcone.Playback;

namespace Lightcone.App.Screens;

/// <summary>
/// The full-screen player, shared by both modes. Controller: A pause, B stop, triggers and
/// left/right seek, X (or Menu) options (audio, subtitles, speed, delays, sleep timer), Y skip
/// intro/credits or info. Desktop: a mouse control bar (play, seek bar, options, mini player,
/// full screen) and Space / arrows / F. Touch: tap shows the controls, the seek bar drags.
/// Near an episode's end an Up next card counts down to the next one (A plays it now, B lets the
/// credits run); after a few episodes in a row with no press it asks "Still watching?" instead.
/// Chapters: ticks on the seek bar; D-pad up/down (with the controls showing), LB / RB, or
/// Page Up / Page Down move between them. Seeking or hovering the bar shows a thumbnail of that
/// moment when the server has them (Plex preview thumbnails, Jellyfin trickplay).
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
    private Label _status = null!;
    private UpNextCard _card = null!;
    private string? _cardDismissedFor;
    private double _countdown = -1;
    private bool _askStill;
    private Label _pausedIcon = null!;
    private Button _skip = null!;
    private Button? _playButton;
    private Control? _audioArt;
    private double _osdLeft = OsdSeconds;
    private bool _showInfo;
    private MediaItem? _shownItem;
    private Label? _hints;
    private PanelContainer _preview = null!;
    private TextureRect _previewPic = null!;
    private Label _previewTime = null!;
    private double _previewAt = -1;
    private int _previewAsk;
    private byte[]? _tileBytes;
    private Image? _tileImage;
    private Button? _prevChapter;
    private Button? _nextChapter;

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
        _status = UiTheme.Label("", UiTheme.SmallSize + 1, new Color(1, 1, 1, 0.75f));
        _status.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        timeRow.AddChild(_status);
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
            _hints = UiTheme.Label("", UiTheme.SmallSize + 1, new Color(1, 1, 1, 0.8f));
            bottomBox.AddChild(_hints);
        }
        _preview = BuildPreview();
        _osd.AddChild(_preview);
        _card = new UpNextCard(Desktop, PlayNextNow, DismissCard);
        _card.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight);
        _card.GrowHorizontal = GrowDirection.Begin;
        _card.GrowVertical = GrowDirection.Begin;
        _card.OffsetRight = -40;
        _card.OffsetBottom = Desktop ? -150 : -130;
        _card.Visible = false;
        root.AddChild(_card);
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
        _prevChapter = B("⏮", "Previous chapter (Page Up)", () => StepChapter(-1));
        _nextChapter = B("⏭", "Next chapter (Page Down)", () => StepChapter(1));
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });
        B("Options", "Audio, subtitles, speed, timing, sleep timer, Night mode, Clear voices and chapters (A)", () => App.Push(new TrackPickerPage()));
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
            _previewPic.Texture = null;
            _previewAt = -1;
            _tileBytes = null;
            _tileImage = null;
            _ = Svc.Preview?.FrameAsync(0);  // start fetching thumbnails before the first seek
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
        if (_hints != null)
        {
            var chapters = Svc.Chapters.Count > 1 ? "    ↑ / ↓ Chapters" : "";
            _hints.Text = $"{Pad.Glyph(Pad.Accept)} Pause    {Pad.Glyph(Pad.SeekBack)} / {Pad.Glyph(Pad.SeekForward)} Seek{chapters}    {Pad.Glyph(Pad.Tertiary)} Options    {Pad.Glyph(Pad.Secondary)} Skip / info    {Pad.Glyph(Pad.Back)} {(Svc.HasVideo ? "Stop" : "Back (keeps playing)")}";
        }
        if (_prevChapter != null && _nextChapter != null)
        {
            _prevChapter.Visible = _nextChapter.Visible = Svc.Chapters.Count > 1;
        }
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
        // The sleep timer set to "end of this episode" stops here.
        if (Svc.SleepEndsHere())
        {
            CloseAfterEnd();
            return;
        }
        var next = Svc.UpNextFor(done);
        if (next != null && done.Kind == MediaKind.Track)
        {
            Svc.PlayNext(auto: false);  // albums play through
            return;
        }
        if (next != null && done.Kind == MediaKind.Episode && Host.Settings.UpNextSeconds > 0)
        {
            if (_askStill || Svc.ShouldAskStillWatching)
            {
                // Stay on the last frame with the question up; nothing plays until someone answers.
                _askStill = true;
                ShowCard(next);
                return;
            }
            Svc.PlayNext(auto: true);
            App.Toast("Up next: " + next.Title);
            return;
        }
        CloseAfterEnd();
    }

    private void CloseAfterEnd()
    {
        Svc.Stop();
        if (App.Top == this)
        {
            App.Pop();
        }
    }

    // ---- Up next ----

    /// <summary>
    /// Where the Up next card appears: at the start of the final credits when the server marks
    /// them (a credits marker that runs to the end; one followed by more of the episode is just a
    /// Skip credits button), else 20 seconds before the end. Null when there's nothing next.
    /// </summary>
    private double? CardStart(MediaItem cur, double duration)
    {
        var final = Host.Library.MarkersOf(cur).Where(m => m.Kind == Marker.Credits && m.End >= duration - 30).OrderBy(m => m.Start).FirstOrDefault();
        return final?.Start ?? duration - 20;
    }

    private void UpdateCard(double delta)
    {
        var cur = Svc.Current;
        MediaItem? next = null;
        var show = false;
        if (cur is { Kind: MediaKind.Episode } && Svc.Duration is { } d and > 60 && !Svc.SleepAtEnd && Host.Settings.UpNextSeconds > 0
            && (next = Svc.UpNextFor(cur)) != null)
        {
            show = _forceCard || _askStill || (_cardDismissedFor != cur.Id && Svc.Position >= CardStart(cur, d) && Svc.Position < d - 0.5);
        }
        if (!show || next == null)
        {
            if (_card.Visible)
            {
                _card.Visible = false;
                _countdown = -1;
            }
            return;
        }
        if (!_card.Visible || _card.Item?.Id != next.Id)
        {
            _askStill = _askStill || Svc.ShouldAskStillWatching;
            ShowCard(next);
        }
        if (_askStill)
        {
            _card.SetStatus("Are you still watching?", null);
            return;
        }
        if (!Svc.Paused)
        {
            _countdown -= delta;
        }
        var total = Host.Settings.UpNextSeconds;
        if (_countdown <= 0)
        {
            PlayNextNow(auto: true);
            return;
        }
        _card.SetStatus(Svc.Paused ? "Paused" : $"Plays in {Math.Ceiling(_countdown):0} s", 1 - _countdown / total);
    }

    private void ShowCard(MediaItem next)
    {
        _countdown = Host.Settings.UpNextSeconds;
        _card.Fill(next, _askStill);
        _card.Visible = true;
        AppLog.Info($"up next card: {next.Id}{(_askStill ? " (still watching?)" : "")}");
    }

    private void PlayNextNow() => PlayNextNow(auto: false);

    private bool _forceCard;

    /// <summary>Smoke test: shows the card now (the sample streams can't seek to their credits).</summary>
    internal void ForceUpNext(bool askStill)
    {
        _forceCard = true;
        _askStill = askStill;
        _card.Visible = false;
    }

    private void PlayNextNow(bool auto)
    {
        _forceCard = false;
        _card.Visible = false;
        _countdown = -1;
        _askStill = false;
        if (Svc.PlayNext(auto) is { } next && auto)
        {
            App.Toast("Up next: " + next.Title);
        }
    }

    /// <summary>B or Watch credits on the card: let this one run out. "Still watching?" + B stops.</summary>
    private void DismissCard()
    {
        _forceCard = false;
        if (_askStill)
        {
            _askStill = false;
            _card.Visible = false;
            CloseAfterEnd();
            return;
        }
        _cardDismissedFor = Svc.Current?.Id;
        _card.Visible = false;
        _countdown = -1;
    }

    // ---- chapters ----

    private void StepChapter(int dir)
    {
        if (Svc.StepChapter(dir))
        {
            var c = Chapter.At(Svc.Chapters, Svc.DisplayPosition);
            if (c != null)
            {
                App.Toast($"Chapter {c.Index + 1} of {Svc.Chapters.Count}: {c.Title}");
            }
        }
    }

    // ---- seek preview ----

    private PanelContainer BuildPreview()
    {
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false, TopLevel = false };
        var box = UiTheme.Box(new Color(0, 0, 0, 0.8f), 1, 10, new Color(1, 1, 1, 0.3f));
        box.ContentMarginLeft = box.ContentMarginRight = box.ContentMarginTop = box.ContentMarginBottom = 4;
        panel.AddThemeStyleboxOverride("panel", box);
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 4);
        panel.AddChild(col);
        var w = Desktop ? 256 : 300;
        _previewPic = new TextureRect
        {
            CustomMinimumSize = new Vector2(w, w * 9 / 16f),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        col.AddChild(_previewPic);
        _previewTime = UiTheme.Label("", UiTheme.SmallSize + 1, Colors.White);
        _previewTime.HorizontalAlignment = HorizontalAlignment.Center;
        _previewTime.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _previewTime.CustomMinimumSize = new Vector2(w, 0);
        col.AddChild(_previewTime);
        return panel;
    }

    /// <summary>Thumbnail and time over the bar while seeking (or the mouse hovers the bar).</summary>
    private void UpdatePreview(double? duration)
    {
        var preview = Svc.Preview;
        var hover = _bar.Hover;
        var seeking = Math.Abs(Svc.DisplayPosition - Svc.Position) > 1;
        if (duration is not > 0 || !(hover >= 0 || (seeking && preview != null)))
        {
            _preview.Visible = false;
            return;
        }
        var at = hover >= 0 ? hover * duration.Value : Svc.DisplayPosition;
        var chapter = Chapter.At(Svc.Chapters, at);
        _previewTime.Text = MediaItem.FormatDuration(at, true) + (chapter != null && Svc.Chapters.Count > 1 ? "  ·  " + chapter.Title : "");
        _previewPic.Visible = preview != null && _previewPic.Texture != null;
        if (preview != null && Math.Abs(at - _previewAt) >= 1)
        {
            _previewAt = at;
            LoadPreview(preview, at);
        }
        _preview.Visible = true;
        _preview.ResetSize();
        var bar = _bar.GetGlobalRect();
        var size = _preview.Size;
        var x = bar.Position.X + bar.Size.X * (float)(at / duration.Value) - size.X / 2;
        _preview.GlobalPosition = new Vector2(Math.Clamp(x, bar.Position.X, bar.End.X - size.X), bar.Position.Y - size.Y - 14);
    }

    private async void LoadPreview(SeekPreview preview, double at)
    {
        var ask = ++_previewAsk;
        PreviewFrame? frame;
        try
        {
            frame = await preview.FrameAsync(at);
        }
        catch (Exception e)
        {
            AppLog.Info($"seek preview: {e.Message}");
            return;
        }
        if (!IsInstanceValid(this) || ask != _previewAsk || frame == null)
        {
            return;
        }
        if (!ReferenceEquals(frame.Image, _tileBytes))
        {
            var img = new Image();
            if (img.LoadJpgFromBuffer(frame.Image) != Error.Ok)
            {
                return;
            }
            _tileBytes = frame.Image;
            _tileImage = img;
        }
        var image = _tileImage!;
        if (frame.Width > 0)
        {
            var r = new Rect2I(frame.X, frame.Y, Math.Min(frame.Width, image.GetWidth() - frame.X), Math.Min(frame.Height, image.GetHeight() - frame.Y));
            if (r.Size.X <= 0 || r.Size.Y <= 0)
            {
                return;
            }
            image = image.GetRegion(r);
        }
        _previewPic.Texture = ImageTexture.CreateFromImage(image);
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
        var show = _osdLeft > 0 || paused || !Svc.HasVideo || _showInfo || (_card.Visible && _askStill);
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
        _bar.Chapters = d is > 0 && Svc.Chapters.Count > 1 ? Svc.Chapters.Skip(1).Select(c => (float)(c.Start / d.Value)).ToList() : [];
        UpdatePreview(d);
        UpdateCard(delta);
        var marker = Svc.ActiveMarker;
        _skip.Visible = marker != null && !(_card.Visible && Svc.NextInsteadOfCredits != null);
        var status = new List<string>();
        if (Math.Abs(Svc.Speed - 1) > 0.001)
        {
            status.Add($"{Svc.Speed:0.##}×");
        }
        if (Svc.SleepText is { } sleep)
        {
            status.Add(sleep);
        }
        if (Svc.Chapters.Count > 1 && Chapter.At(Svc.Chapters, pos) is { } chapterNow)
        {
            status.Add(chapterNow.Title);
        }
        if (Host.Settings.NightMode)
        {
            status.Add("Night mode");
        }
        if (Host.Settings.ClearVoices)
        {
            status.Add("Clear voices");
        }
        _status.Text = status.Count > 0 ? "    " + string.Join("  ·  ", status) : "";
        if (marker != null)
        {
            var label = Svc.SkipLabel ?? marker.Label;
            _skip.Text = Desktop ? label : $"{Pad.Glyph(Pad.Secondary)}  {label}";
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
        Svc.NoteInput();
        if (_card.Visible && e.IsAction(Pad.Accept) && !e.IsEcho())
        {
            PlayNextNow();
        }
        else if (e.IsAction(Pad.Accept) && !e.IsEcho())
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
            // The first press shows the controls; with them showing, up / down change chapter.
            if (_osd.Modulate.A > 0.5f && Svc.Chapters.Count > 1 && !e.IsEcho())
            {
                StepChapter(e.IsAction("ui_down") ? 1 : -1);
            }
        }
        else if ((e.IsAction(Pad.TabPrev) || e.IsAction(Pad.TabNext)) && !e.IsEcho())
        {
            StepChapter(e.IsAction(Pad.TabNext) ? 1 : -1);
        }
        else if (Desktop && e is InputEventKey { Keycode: Key.F, Pressed: true, Echo: false })
        {
            ToggleFullscreen();
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
            case InputEventMouseButton { Pressed: true } when _card.Visible && _askStill:
                break;  // answer the question with its buttons
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
        Svc.NoteInput();
        if (_card.Visible)
        {
            DismissCard();
            return true;
        }
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

    /// <summary>Where chapters start (after the first), as fractions of the bar.</summary>
    public IReadOnlyList<float> Chapters { get; set; } = [];

    /// <summary>Where the mouse is over the bar (0–1), or -1.</summary>
    public float Hover { get; private set; } = -1;

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
        MouseExited += () => Hover = -1;
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
            Hover = Math.Clamp(mm.Position.X / Size.X, 0, 1);
            AcceptEvent();
        }
        else if (e is InputEventMouseMotion hover)
        {
            // Touch screens report emulated mouse motion only while dragging (handled above).
            Hover = Math.Clamp(hover.Position.X / Size.X, 0, 1);
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
        foreach (var c in Chapters)
        {
            // A small gap in the bar where each chapter starts.
            DrawRect(new Rect2(Size.X * c - 1.5f, y - 1, 3, h + 2), new Color(0, 0, 0, 0.7f));
        }
        DrawCircle(new Vector2(Size.X * _shown, Size.Y / 2), 9, UiTheme.Accent);
    }
}
