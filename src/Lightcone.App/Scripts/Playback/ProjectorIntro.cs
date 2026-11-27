using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Playback;

/// <summary>
/// While a movie or episode loads: a film reel drops onto a projector, locks in (click, clack),
/// the sprockets whirr up and the gate opens, throwing the beam. It never holds playback back:
/// the moment the first frame is there it fades away, and a long load keeps the reel turning.
/// Sounds and rumble follow the app's sound and rumble settings; Settings → Sound and feel →
/// Projector intro turns it off.
/// </summary>
public partial class ProjectorIntro : Control
{
    private const float Drop = 0.35f;     // the reel seats
    private const float Spin = 0.6f;      // the motor starts
    private const float Gate = 1.1f;      // the beam comes on
    private MediaItem? _for;
    private double _t;
    private float _fade;
    private float _angle;
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
    private float _travel;

    public ProjectorIntro()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Visible = false;
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
                _angle = 0;
                _travel = 0;
                _fade = 1;
                _running = true;
                _tip = NextTip(_tip);
                _title.Text = cur!.Title;
                _tipLabel.Text = _tip;
                _mat.SetShaderParameter("size", Size);
                _mat.SetShaderParameter("warm", UiTheme.Info.Lerp(new Color(1f, 0.86f, 0.64f), 0.6f));
                _mat.SetShaderParameter("accent", UiTheme.Accent);
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
        // Motor: winds up from Spin over half a second (rad/s at full speed).
        var speed = t < Spin ? 0f : Math.Min(1f, (t - Spin) / 0.5f);
        _angle += (float)(speed * 7.5 * delta);
        _travel += (float)(speed * 0.9 * delta);
        if (!Hold && (Svc.FrameShown || cur == null || !Anim.Enabled))
        {
            _fade = Mathf.MoveToward(_fade, 0, (float)delta * 4.5f);  // ~0.2 s, over the first frames
            if (_fade <= 0)
            {
                _running = false;
                Visible = false;
                Feedback.StopProjector();
                return;
            }
        }
        Modulate = new Color(1, 1, 1, _fade);
        // The camera eases in from close and slightly right, then drifts.
        var ease = 1 - MathF.Pow(1 - Math.Min(1, t / 1.8f), 3);
        var zoom = Mathf.Lerp(1.22f, 1.0f, ease) + 0.008f * MathF.Sin(t * 0.6f);
        var cam = new Vector2(Mathf.Lerp(0.05f, 0.18f, ease), Mathf.Lerp(-0.18f, -0.1f, ease)) + new Vector2(0.006f * MathF.Sin(t * 0.45f), 0.004f * MathF.Cos(t * 0.5f));
        // Lamp: off until the gate opens, a stutter while it strikes, then steady with a faint shimmer.
        var lamp = 0f;
        if (t > Gate)
        {
            var k = t - Gate;
            lamp = k switch
            {
                < 0.06f => 0.7f,
                < 0.12f => 0.1f,
                < 0.2f => 0.85f,
                < 0.26f => 0.35f,
                _ => Math.Min(1f, 0.6f + (k - 0.26f) * 1.6f),
            };
        }
        var drop = EaseOutBack(Math.Clamp(t / Drop, 0, 1));
        _mat.SetShaderParameter("t", t);
        _mat.SetShaderParameter("ang", _angle);
        _mat.SetShaderParameter("speed", speed);
        _mat.SetShaderParameter("film_pos", _travel);
        _mat.SetShaderParameter("drop", drop);
        _mat.SetShaderParameter("lamp", lamp);
        _mat.SetShaderParameter("zoom", zoom);
        _mat.SetShaderParameter("cam", cam);
        _title.Modulate = new Color(1, 1, 1, Math.Clamp((t - 0.2f) / 0.4f, 0, 1) * 0.9f);
        _tipLabel.Modulate = new Color(1, 1, 1, Math.Clamp((t - 0.45f) / 0.5f, 0, 1) * 0.8f);
    }

    private static float EaseOutBack(float x)
    {
        const float c1 = 1.4f;
        const float c3 = c1 + 1;
        return 1 + c3 * MathF.Pow(x - 1, 3) + c1 * MathF.Pow(x - 1, 2);
    }
}
