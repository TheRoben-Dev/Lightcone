using Godot;
using Lightcone.App.Playback;
using Lightcone.App.Ui;
using Lightcone.Playback;

namespace Lightcone.App.Screens;

/// <summary>Audio and subtitle tracks for what's playing, as a card over the video.</summary>
public partial class TrackPickerPage : Screen
{
    private Control? _first;

    public override bool IsOverlay => true;

    public override void AnimateOpen() => Anim.FadeIn(this, Anim.Normal);

    protected override void Build()
    {
        var svc = PlayerService.Instance;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 30);
        AddChild(CardFrame(row));
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
        Focus.Column(aRows, right: sRows.FirstOrDefault());
        Focus.Column(sRows, left: aRows.FirstOrDefault());
        _first = aRows.FirstOrDefault(r => r is ListRow) ?? sRows.FirstOrDefault();
        var info = UiTheme.Label($"Decoding: {(svc.HwdecCurrent is "" or "no" ? "software" : svc.HwdecCurrent)}  ·  Drawing: {svc.RenderPath}", UiTheme.SmallSize, UiTheme.Dim);
        subs.AddChild(info);
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
