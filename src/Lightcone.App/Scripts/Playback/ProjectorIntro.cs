using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Playback;

/// <summary>
/// While a movie or episode loads: darkness, then the projector lamp strikes and a soft cone of
/// light opens across the room, with haze and dust drifting through it (see ProjectorShader). It never holds playback back:
/// the moment the first frame is there it fades away, and a long load keeps the reel turning.
/// Sounds and rumble follow the app's sound and rumble settings; Settings → Sound and feel →
/// Projector intro turns it off.
/// </summary>
public partial class ProjectorIntro : Control
{
    private const float Strike = 0.42f;   // the lamp strikes (with the latch sound)
    private MediaItem? _for;
    private double _t;
    private float _fade;
    private bool _running;
    private string _tip = "";
    private static List<string>? _tips;
    private static readonly Random Rng = new();

    /// <summary>One line from Data/tips.txt (viewing tips and cinema trivia), never the same twice running.</summary>
    private static string NextTip(string last)
    {
        if (_tips == null)
        {
            _tips = [];
            using var f = Godot.FileAccess.Open("res://Data/tips.txt", Godot.FileAccess.ModeFlags.Read);
            while (f != null && !f.EofReached())
            {
                var line = f.GetLine().Trim();
                if (line.Length > 0 && !line.StartsWith('#'))
                {
                    _tips.Add(line);
                }
            }
        }
        if (_tips.Count == 0)
        {
            return "";
        }
        string pick;
        do
        {
            pick = _tips[Rng.Next(_tips.Count)];
        }
        while (pick == last && _tips.Count > 1);
        return pick;
    }

    /// <summary>Tests: keep the intro on screen even after the first frame (for a screenshot).</summary>
    public static bool Hold { get; set; }

    private static PlayerService Svc => PlayerService.Instance;

    private readonly ShaderMaterial _mat;
    private readonly Label _title;
    private readonly Label _tipLabel;

    public ProjectorIntro()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Visible = false;
        ProcessPriority = -100;  // up before the player draws its controls, so they never flash first
        _mat = new ShaderMaterial { Shader = new Shader { Code = ProjectorShader.Code } };
        var scene = new ColorRect { Material = _mat, MouseFilter = MouseFilterEnum.Ignore };
        scene.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(scene);
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End };
        col.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        col.OffsetBottom = -110;
        col.OffsetLeft = 80;
        col.OffsetRight = -80;
        col.AddThemeConstantOverride("separation", 10);
        AddChild(col);
        _title = UiTheme.Label("", UiTheme.FontSize + 4, UiTheme.Text);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        col.AddChild(_title);
        _tipLabel = UiTheme.Label("", UiTheme.SmallSize + 2, UiTheme.Dim, wrap: true);
        _tipLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _tipLabel.MaxLinesVisible = 2;
        col.AddChild(_tipLabel);
        Resized += () => _mat.SetShaderParameter("size", Size);
        var toScreen = Homography.Solve(ScreenCorners, Frame);  // resting scene → the screen's 0..1
        _mat.SetShaderParameter("g0", toScreen[0]);
        _mat.SetShaderParameter("g1", toScreen[1]);
        _mat.SetShaderParameter("g2", toScreen[2]);
    }

    /// <summary>Turned on in Settings (on by default).</summary>
    public static bool Enabled => Main.Instance.Host?.Settings.ProjectorIntro != false;

    public override void _Process(double delta)
    {
        var cur = Svc.Current;
        if (cur != _for)
        {
            _for = cur;
            var video = cur != null && cur.Kind is MediaKind.Movie or MediaKind.Episode;
            if (video && Enabled && (!Svc.FrameShown || Hold))
            {
                _t = 0;
                _fade = 1;
                _readyAt = -1;
                _running = true;
                _tip = NextTip(_tip);
                _title.Text = cur!.Title;
                _tipLabel.Text = _tip;
                _mat.SetShaderParameter("size", Size);
                _mat.SetShaderParameter("warm", new Color(1f, 0.85f, 0.64f));  // tungsten-warm in every theme
                Visible = true;
                Feedback.PlayProjector();
            }
        }
        if (!_running)
        {
            return;
        }
        _t += delta;
        var t = (float)_t;
        if (cur == null || !Anim.Enabled)
        {
            // Stopped while loading, or animations off: just fade away.
            _fade = Mathf.MoveToward(_fade, 0, (float)delta * 4.5f);
            if (_fade <= 0)
            {
                Finish();
                return;
            }
        }
        else if (_readyAt < 0 && !Hold && Svc.FrameShown && Svc.Texture != null)
        {
            _readyAt = t;  // the film is running: it goes up on the screen and the camera moves in
        }
        Modulate = new Color(1, 1, 1, _fade);
        var since = _readyAt < 0 ? -1f : t - _readyAt;
        // Lamp: dark, then it strikes (a stutter as the arc catches) and settles with a faint
        // shimmer; the beam opens up from a sliver as it does. A film that's ready early brings
        // the light up at once.
        var lamp = 0f;
        if (t > Strike)
        {
            var k = t - Strike;
            lamp = k switch
            {
                < 0.05f => 0.55f,
                < 0.11f => 0.08f,
                < 0.18f => 0.8f,
                < 0.23f => 0.3f,
                _ => Math.Min(1f, 0.55f + (k - 0.23f) * 1.4f),
            };
        }
        var open = Math.Clamp((t - Strike - 0.15f) / 1.1f, 0, 1);
        if (since >= 0)
        {
            lamp = Math.Max(lamp, Math.Min(1f, since * 4f));
            open = Math.Max(open, Math.Min(1f, since * 2.5f));
        }
        lamp *= 0.985f + 0.015f * MathF.Sin(t * 9.1f) * MathF.Sin(t * 5.3f);
        var spread = 1 - MathF.Pow(1 - open, 3);
        var show = since < 0 ? 0f : Math.Clamp(since / 0.35f, 0, 1);
        var push = since < 0 ? 0f : EaseInOut(Math.Clamp((since - PushDelay) / PushTime, 0, 1));
        SetCamera(push);
        _mat.SetShaderParameter("t", t);
        _mat.SetShaderParameter("lamp", lamp);
        _mat.SetShaderParameter("spread", spread);
        _mat.SetShaderParameter("show", show);
        _mat.SetShaderParameter("push", push);
        if (Svc.Texture is { } tex)
        {
            _mat.SetShaderParameter("video", tex);
            var ts = tex.GetSize();
            _mat.SetShaderParameter("vid_aspect", ts.Y > 0 ? ts.X / ts.Y : 16f / 9f);
        }
        var labels = since < 0 ? 1f : Math.Clamp(1 - since / 0.3f, 0, 1);
        _title.Modulate = new Color(1, 1, 1, Math.Clamp((t - 0.75f) / 0.6f, 0, 1) * 0.9f * labels);
        _tipLabel.Modulate = new Color(1, 1, 1, Math.Clamp((t - 1.0f) / 0.6f, 0, 1) * 0.8f * labels);
        if (since >= PushDelay + PushTime)
        {
            Finish();  // the screen now is the player's picture: hand over
        }
    }

    private const float PushDelay = 0.3f;
    private const float PushTime = 1.6f;
    private float _readyAt = -1;

    // The screen's corners in the resting scene (UV space, y down): off to the right, turned
    // towards the projector, so its near (right) edge is taller.
    private static readonly Vector2[] ScreenCorners = [new(0.80f, 0.31f), new(0.985f, 0.18f), new(0.985f, 0.82f), new(0.80f, 0.67f)];
    private static readonly Vector2[] Frame = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];

    private void Finish()
    {
        _running = false;
        Visible = false;
        Feedback.StopProjector();
    }

    /// <summary>
    /// The camera move: the screen's corners glide from where they rest to the corners of the
    /// frame, and the room follows the same perspective mapping, so it reads as a push in and turn
    /// towards the screen.
    /// </summary>
    private void SetCamera(float push)
    {
        var now = new Vector2[4];
        for (var i = 0; i < 4; i++)
        {
            now[i] = ScreenCorners[i].Lerp(Frame[i], push);
        }
        var back = Homography.Solve(now, ScreenCorners);   // output → resting scene
        _mat.SetShaderParameter("h0", back[0]);
        _mat.SetShaderParameter("h1", back[1]);
        _mat.SetShaderParameter("h2", back[2]);
    }

    private static float EaseInOut(float x) => x < 0.5f ? 4 * x * x * x : 1 - MathF.Pow(-2 * x + 2, 3) / 2;
}
