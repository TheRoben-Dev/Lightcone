using Godot;
using Lightcone.App.Playback;
using Lightcone.App.Ui;
using Lightcone.Playback;
using Lightcone.Host.Library;
using System.Globalization;

namespace Lightcone.App.Screens;

/// <summary>
/// The player's options, as a card over the video: audio and subtitle tracks, then speed,
/// subtitle and audio timing (remembered per show or movie) and the sleep timer.
/// </summary>
public partial class TrackPickerPage : Screen
{
    private Control? _first;

    public override bool IsOverlay => true;

    public override void AnimateOpen() => Anim.FadeIn(this, Anim.Normal);

    protected override void Build()
    {
        var svc = PlayerService.Instance;
        var page = Column(14);
        AddChild(CardFrame(Scroller(page)));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 30);
        page.AddChild(row);
        var audio = Column(8);
        var subs = Column(8);
        row.AddChild(audio);
        row.AddChild(subs);
        audio.AddChild(Section("Audio"));
        subs.AddChild(Section("Subtitles"));
        var aRows = new List<Control>();
        foreach (var t in svc.Tracks.Where(t => t.Kind == TrackKind.Audio))
        {
            var id = t.Id;
            var b = new ListRow(t.Label, "", t.Selected ? "✓" : null, UiTheme.Accent, onPress: () =>
            {
                svc.SelectAudio(id);
                Callable.From(Rebuild).CallDeferred();
            }, help: "Audio: " + t.Label) { CustomMinimumSize = new Vector2(0, 52) };
            audio.AddChild(b);
            aRows.Add(b);
        }
        if (aRows.Count == 0)
        {
            audio.AddChild(UiTheme.Label("No audio tracks.", UiTheme.SmallSize + 1, UiTheme.Dim));
        }
        var sRows = new List<Control>();
        var subTracks = svc.Tracks.Where(t => t.Kind == TrackKind.Subtitle).ToList();
        var off = new ListRow("Off", "", subTracks.All(t => !t.Selected) ? "✓" : null, UiTheme.Accent, onPress: () =>
        {
            svc.SelectSubtitle(null);
            Callable.From(Rebuild).CallDeferred();
        }, help: "Subtitles: off") { CustomMinimumSize = new Vector2(0, 52) };
        subs.AddChild(off);
        sRows.Add(off);
        foreach (var t in subTracks)
        {
            var id = t.Id;
            var b = new ListRow(t.Label, "", t.Selected ? "✓" : null, UiTheme.Accent, onPress: () =>
            {
                svc.SelectSubtitle(id);
                Callable.From(Rebuild).CallDeferred();
            }, help: "Subtitles: " + t.Label) { CustomMinimumSize = new Vector2(0, 52) };
            subs.AddChild(b);
            sRows.Add(b);
        }
        var steppers = BuildPlayback(page, svc);
        Focus.Column(aRows, right: sRows.FirstOrDefault(), below: steppers[0]);
        Focus.Column(sRows, left: aRows.FirstOrDefault(), below: steppers[0]);
        Focus.Column(steppers, above: aRows.LastOrDefault() ?? sRows.LastOrDefault());
        _first = aRows.FirstOrDefault(r => r is ListRow) ?? sRows.FirstOrDefault();
        var info = UiTheme.Label($"Decoding: {(svc.HwdecCurrent is "" or "no" ? "software" : svc.HwdecCurrent)}  ·  Drawing: {svc.RenderPath}", UiTheme.SmallSize, UiTheme.Dim);
        subs.AddChild(info);
    }

    private static string Seconds(double v) => v == 0 ? "0.0 s" : v.ToString("+0.0 's';-0.0 's'", CultureInfo.InvariantCulture);

    private static IEnumerable<(string, string)> Delays() =>
        Enumerable.Range(-50, 101).Select(i => i / 10.0).Select(v => (v.ToString("0.0", CultureInfo.InvariantCulture), Seconds(v)));

    /// <summary>Speed, subtitle / audio delay and the sleep timer, as steppers under the tracks.</summary>
    private List<Control> BuildPlayback(VBoxContainer page, PlayerService svc)
    {
        page.AddChild(Section("Playback"));
        var cur = svc.Current;
        var which = cur?.Kind == MediaKind.Episode ? "every episode of this show" : cur?.Kind == MediaKind.Movie ? "this movie" : "this";
        var list = new List<Control>();
        void Add(Stepper st)
        {
            page.AddChild(st);
            list.Add(st);
        }
        double[] speeds = [0.5, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2];
        var speed = new Stepper("Speed", speeds.Select(v => (v.ToString("0.##", CultureInfo.InvariantCulture), v == 1 ? "Normal" : v.ToString("0.##", CultureInfo.InvariantCulture) + "×")),
            speeds.OrderBy(v => Math.Abs(v - svc.Speed)).First().ToString("0.##", CultureInfo.InvariantCulture),
            $"Playback speed, with the pitch kept natural. Remembered for {which}.") { Wrap = false };
        speed.Changed += v => svc.SetSpeed(double.Parse(v, CultureInfo.InvariantCulture));
        Add(speed);
        var sub = new Stepper("Subtitle timing", Delays(), Math.Clamp(Math.Round(svc.SubDelay, 1), -5, 5).ToString("0.0", CultureInfo.InvariantCulture),
            $"Subtitles early or late? Positive shows them later, negative earlier. Remembered for {which}.") { Wrap = false };
        sub.Changed += v => svc.SetSubDelay(double.Parse(v, CultureInfo.InvariantCulture));
        Add(sub);
        var audio = new Stepper("Audio timing", Delays(), Math.Clamp(Math.Round(svc.AudioDelay, 1), -5, 5).ToString("0.0", CultureInfo.InvariantCulture),
            $"Lips and voices out of sync (Bluetooth headphones)? Positive delays the sound. Remembered for {which}.") { Wrap = false };
        audio.Changed += v => svc.SetAudioDelay(double.Parse(v, CultureInfo.InvariantCulture));
        Add(audio);
        var end = cur?.Kind == MediaKind.Episode ? "End of this episode" : "End of this one";
        var sleep = new Stepper("Sleep timer", [("0", "Off"), ("15", "15 min"), ("30", "30 min"), ("45", "45 min"), ("60", "1 hour"), ("90", "1 h 30 min"), ("-1", end)],
            svc.SleepChoice.ToString(CultureInfo.InvariantCulture),
            "Pauses playback for you (the sound fades out first), and keeps your spot.")
        {
            Expands = true,
            DetailFor = _ => svc.SleepText is { } t ? t + "." : null,
        };
        sleep.Changed += v => svc.SetSleep(int.Parse(v, CultureInfo.InvariantCulture));
        Add(sleep);
        return list;
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
