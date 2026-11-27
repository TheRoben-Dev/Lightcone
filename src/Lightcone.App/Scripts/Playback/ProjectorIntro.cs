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

    public ProjectorIntro()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Visible = false;
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
                _fade = 1;
                _running = true;
                _tip = NextTip(_tip);
                Visible = true;
                Feedback.PlayProjector();
            }
        }
        if (!_running)
        {
            return;
        }
        _t += delta;
        var speed = _t < Spin ? 0 : Math.Min(1, (_t - Spin) / 0.5) * 9.0;  // rad/s, winding up
        _angle += (float)(speed * delta);
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
        QueueRedraw();
    }

    private static float EaseOutBack(float x)
    {
        const float c1 = 1.4f;
        const float c3 = c1 + 1;
        return 1 + c3 * MathF.Pow(x - 1, 3) + c1 * MathF.Pow(x - 1, 2);
    }

    public override void _Draw()
    {
        var t = (float)_t;
        var a = _fade;
        var c = Size / 2;
        var unit = Math.Min(Size.X / 1280f, Size.Y / 800f);
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, a));
        var body = UiTheme.PanelHi with { A = a };
        var edge = UiTheme.Line.Lightened(0.25f) with { A = a };
        var metal = UiTheme.Dim with { A = a };
        var warm = UiTheme.Info;

        // Projector body, lens on the right.
        var bw = 230 * unit;
        var bh = 120 * unit;
        var bodyPos = c + new Vector2(-bw * 0.65f, 10 * unit);
        DrawRect(new Rect2(bodyPos, new Vector2(bw, bh)), body);
        DrawRect(new Rect2(bodyPos, new Vector2(bw, bh)), edge, false, 2 * unit);
        DrawRect(new Rect2(bodyPos + new Vector2(18, bh - 6) * new Vector2(1, 1), new Vector2(bw - 36, 6 * unit)), edge);
        var lens = bodyPos + new Vector2(bw + 18 * unit, bh * 0.45f);
        DrawRect(new Rect2(bodyPos + new Vector2(bw, bh * 0.45f - 16 * unit), new Vector2(22 * unit, 32 * unit)), body);
        DrawCircle(lens, 20 * unit, edge);
        DrawCircle(lens, 14 * unit, new Color(0.05f, 0.05f, 0.06f, a));

        // Beam: opens at the gate, flickers a little, reaches to the right edge.
        if (t > Gate)
        {
            var open = Math.Min(1, (t - Gate) / 0.35f);
            var flicker = 0.85f + 0.15f * MathF.Sin(t * 47f) * MathF.Sin(t * 13f);
            var far = Size.X + 40;
            var spread = (far - lens.X) * 0.28f * open;
            var beam = new[] { lens + new Vector2(0, -10 * unit), new Vector2(far, lens.Y - spread), new Vector2(far, lens.Y + spread), lens + new Vector2(0, 10 * unit) };
            var col = warm with { A = 0.22f * open * flicker * a };
            var edgeCol = warm with { A = 0 };
            DrawPolygon(beam, [col, edgeCol, edgeCol, col]);
            DrawCircle(lens, 9 * unit, warm with { A = 0.9f * open * a });
        }

        // Take-up reel at the back (always there), feed reel dropping in at the front.
        var spindleBack = bodyPos + new Vector2(bw * 0.16f, -58 * unit);
        var spindleFront = bodyPos + new Vector2(bw * 0.8f, -80 * unit);
        DrawLine(bodyPos + new Vector2(bw * 0.16f, 0), spindleBack, metal, 4 * unit);
        DrawLine(bodyPos + new Vector2(bw * 0.8f, 0), spindleFront, metal, 4 * unit);
        Reel(spindleBack, 46 * unit, -_angle * 1.3f, a, unit);
        var drop = Math.Clamp(t / Drop, 0, 1);
        var y = Mathf.Lerp(-260 * unit, 0, EaseOutBack(drop));
        var frontA = a * Math.Clamp(t / 0.15f, 0, 1);
        Reel(spindleFront + new Vector2(30 * unit * (1 - drop), y), 70 * unit, _angle, frontA, unit);
        // Film strip from the feed reel through the gate.
        if (drop >= 1)
        {
            var film = UiTheme.Accent.Darkened(0.35f) with { A = a * 0.9f };
            DrawLine(spindleFront + new Vector2(70 * unit, 20 * unit), bodyPos + new Vector2(bw - 6 * unit, 8 * unit), film, 3 * unit);
            DrawLine(spindleBack + new Vector2(-44 * unit, 24 * unit), bodyPos + new Vector2(10 * unit, 8 * unit), film, 3 * unit);
        }

        // A label that names what's coming, under the projector.
        if (_for != null)
        {
            var font = UiTheme.CurrentFont;
            var text = _for.Title;
            var size = (int)(22 * unit);
            var w = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
            DrawString(font, new Vector2(c.X - w / 2, bodyPos.Y + bh + 64 * unit), text, HorizontalAlignment.Left, -1, size, UiTheme.Text with { A = a * Math.Clamp((t - 0.2f) / 0.4f, 0, 1) * 0.85f });
            if (_tip.Length > 0)
            {
                // The tip, wrapped to at most two centred lines under the title.
                var tipSize = (int)(17 * unit);
                var maxW = Math.Min(Size.X - 120 * unit, 900 * unit);
                var tipA = a * Math.Clamp((t - 0.4f) / 0.5f, 0, 1) * 0.7f;
                var lines = Wrap(font, Tr(_tip), tipSize, maxW);
                for (var i = 0; i < lines.Count && i < 2; i++)
                {
                    var lw = font.GetStringSize(lines[i], HorizontalAlignment.Left, -1, tipSize).X;
                    DrawString(font, new Vector2(c.X - lw / 2, bodyPos.Y + bh + (104 + i * 26) * unit), lines[i], HorizontalAlignment.Left, -1, tipSize, UiTheme.Dim with { A = tipA });
                }
            }
        }
    }

    private static List<string> Wrap(Font font, string text, int size, float maxW)
    {
        var lines = new List<string>();
        var cur = "";
        foreach (var word in text.Split(' '))
        {
            var next = cur.Length == 0 ? word : cur + " " + word;
            if (cur.Length > 0 && font.GetStringSize(next, HorizontalAlignment.Left, -1, size).X > maxW)
            {
                lines.Add(cur);
                cur = word;
            }
            else
            {
                cur = next;
            }
        }
        if (cur.Length > 0)
        {
            lines.Add(cur);
        }
        return lines;
    }

    private void Reel(Vector2 at, float r, float angle, float a, float unit)
    {
        var metal = UiTheme.Dim with { A = a };
        var dark = new Color(0.03f, 0.03f, 0.04f, a);
        var red = UiTheme.Accent with { A = a };
        DrawCircle(at, r, metal);
        DrawCircle(at, r * 0.93f, UiTheme.Panel with { A = a });
        // Wound film.
        DrawCircle(at, r * 0.78f, UiTheme.Accent.Darkened(0.55f) with { A = a });
        // Five windows.
        for (var i = 0; i < 5; i++)
        {
            var ang = angle + i * Mathf.Tau / 5;
            var p = at + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * r * 0.52f;
            DrawCircle(p, r * 0.2f, dark);
        }
        DrawCircle(at, r * 0.16f, metal);
        DrawCircle(at, r * 0.07f, red);
        DrawArc(at, r, 0, Mathf.Tau, 48, UiTheme.Line.Lightened(0.3f) with { A = a }, 1.5f * unit, true);
    }
}
