using Godot;

namespace Lightcone.App.Ui;

/// <summary>
/// The projector intro's sound and rumble: the reel clicking onto its spindle, the latch clack,
/// the sprockets whirring up and the gate opening. Synthesised once (no audio files), played at
/// the menu-sound volume, with matching pulses on the pad when rumble is on.
/// </summary>
public partial class Feedback
{
    private AudioStreamWav? _projector;
    private AudioStreamPlayer? _projectorPlayer;
    private readonly List<SceneTreeTimer> _pulses = [];

    /// <summary>Starts the intro's sound and rumble (no-op when both are off).</summary>
    public static void PlayProjector() => Instance?.StartProjector();

    /// <summary>Cuts the intro's sound short (the picture is there).</summary>
    public static void StopProjector()
    {
        if (Instance?._projectorPlayer is { } p && IsInstanceValid(p) && p.Playing)
        {
            var tween = p.CreateTween();
            tween.TweenProperty(p, "volume_db", -40f, 0.25f);
            tween.TweenCallback(Callable.From(p.Stop));
        }
    }

    private void StartProjector()
    {
        if (Volume > 0)
        {
            _projector ??= Wav(RenderProjector());
            if (_projectorPlayer == null)
            {
                _projectorPlayer = new AudioStreamPlayer();
                AddChild(_projectorPlayer);
            }
            _projectorPlayer.Stream = _projector;
            _projectorPlayer.VolumeDb = Mathf.LinearToDb(Volume / 100f * 0.8f);
            _projectorPlayer.Play();
        }
        if (Haptics && Pad.LastDevice is { } device && Pad.LastFamily != PadFamily.Keyboard)
        {
            // (time, weak, strong, seconds): click, clack, whirr, gate.
            foreach (var (at, weak, strong, secs) in new[] { (0.35, 0.35f, 0f, 0.04f), (0.46, 0f, 0.55f, 0.06f), (0.62, 0.12f, 0f, 0.5f), (1.1, 0f, 0.4f, 0.08f) })
            {
                var timer = GetTree().CreateTimer(at);
                timer.Timeout += () => Input.StartJoyVibration(device, weak, strong, secs);
                _pulses.Add(timer);
            }
        }
    }

    /// <summary>About 1.5 s of mechanical projector sounds, mono.</summary>
    internal static float[] RenderProjector()
    {
        const float length = 1.55f;
        var buf = new float[(int)(length * Rate)];
        var rng = new Random(24);
        float Noise() => (float)(rng.NextDouble() * 2 - 1);

        // A sharp click: a burst of bright noise and a short metallic ping.
        void Click(float at, float level, float ping)
        {
            var start = (int)(at * Rate);
            var n = (int)(0.035f * Rate);
            var prev = 0f;
            for (var i = 0; i < n && start + i < buf.Length; i++)
            {
                var t = i / (float)Rate;
                var raw = Noise();
                var hp = raw - prev;  // crude high-pass: keeps it crisp
                prev = raw;
                var s = hp * MathF.Exp(-t * 260) * 0.8f + MathF.Sin(MathF.Tau * ping * t) * MathF.Exp(-t * 90) * 0.35f;
                buf[start + i] += s * level;
            }
        }

        // A low clack/thunk: a pitched-down knock with some body.
        void Thunk(float at, float level, float pitch)
        {
            var start = (int)(at * Rate);
            var n = (int)(0.12f * Rate);
            var lp = 0f;
            for (var i = 0; i < n && start + i < buf.Length; i++)
            {
                var t = i / (float)Rate;
                lp += (Noise() - lp) * 0.08f;
                var f = pitch * (1 + 0.6f * MathF.Exp(-t * 40));
                var s = MathF.Sin(MathF.Tau * f * t) * MathF.Exp(-t * 32) + lp * 2.2f * MathF.Exp(-t * 55);
                buf[start + i] += s * level;
            }
        }

        Click(0.33f, 0.55f, 2600);  // the reel meets the spindle
        Click(0.37f, 0.35f, 3100);
        Thunk(0.46f, 0.55f, 140);   // the latch
        Click(0.47f, 0.4f, 1900);

        // Sprocket whirr: a motor hum rising in pitch with fast ticks as the film is pulled through.
        var whirrFrom = 0.6f;
        var next = whirrFrom;
        var hp2 = 0f;
        for (var i = (int)(whirrFrom * Rate); i < buf.Length; i++)
        {
            var t = i / (float)Rate;
            var run = Math.Min(1f, (t - whirrFrom) / 0.45f);           // spin-up
            var fade = Math.Clamp((length - t) / 0.35f, 0, 1);       // tail
            var hum = MathF.Sin(MathF.Tau * (55 + 45 * run) * t) * 0.18f + MathF.Sin(MathF.Tau * (110 + 90 * run) * t) * 0.08f;
            var raw = Noise();
            hp2 += (raw - hp2) * 0.25f;
            buf[i] += (hum + hp2 * 0.06f) * run * fade * 0.7f;
            if (t >= next)
            {
                Click(t, 0.12f * run * fade + 0.03f, 4200);
                next = t + 1f / (10 + 14 * run);  // up to 24 ticks a second
            }
        }

        Thunk(1.08f, 0.45f, 95);  // the gate opens
        Click(1.09f, 0.3f, 1500);

        for (var i = 0; i < buf.Length; i++)
        {
            buf[i] = Math.Clamp(buf[i] * 0.7f, -1f, 1f);
        }
        return buf;
    }
}
