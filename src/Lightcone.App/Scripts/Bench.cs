using Lightcone.Host;
using Lightcone.Playback;
using Godot;
using Lightcone.App.Playback;
using Lightcone.Host.Library;

namespace Lightcone.App;

/// <summary>
/// <c>--bench[=file]</c>: plays a file (or the first sample movie) full screen for 30 s and writes what
/// matters for real hardware (Steam Deck, Macs): render path, hwdec in use, codec, dropped frames,
/// Godot fps. Printed to stdout and saved as bench-*.txt next to the logs, then the app quits.
/// </summary>
public partial class Bench : Node
{
    private const double Seconds = 30;
    private readonly string? _file;
    private double _t;
    private bool _started;
    private double _fpsSum;
    private int _fpsN;
    private double _fpsMin = double.MaxValue;

    public Bench(string? file) => _file = string.IsNullOrWhiteSpace(file) ? null : file;

    public override void _Process(double delta)
    {
        var host = Main.Instance.Host;
        var svc = PlayerService.Instance;
        if (host == null)
        {
            Finish("no host");
            return;
        }
        if (!_started)
        {
            _started = true;
            var item = _file != null
                ? new MediaItem { Id = "bench", Title = System.IO.Path.GetFileName(_file), Kind = MediaKind.Movie, SourceId = "bench", SourceName = "Bench", PlayUrl = _file }
                : host.Library.Section(MediaKind.Movie, SortKey.Title, false).FirstOrDefault(i => i.Playable);
            if (item == null)
            {
                Finish("nothing to play (pass --bench=<file> or enable the sample library)");
                return;
            }
            AppLog.Info($"bench: {item.PlayUrl}");
            svc.Play(item, fromStart: true);
            Main.Instance.ShowPlayer();
            return;
        }
        _t += delta;
        if (_t > 3)
        {
            var fps = Engine.GetFramesPerSecond();
            _fpsSum += fps;
            _fpsN++;
            _fpsMin = Math.Min(_fpsMin, fps);
        }
        if (_t >= Seconds || (_t > 3 && svc.Current == null))
        {
            Finish(null);
        }
    }

    private void Finish(string? error)
    {
        SetProcess(false);
        var svc = PlayerService.Instance;
        var lines = new List<string>
        {
            $"Lightcone bench {DateTime.Now:yyyy-MM-dd HH:mm}",
            $"platform     {Lightcone.Core.Platform.Name}, {OS.GetProcessorName()}, {RenderingServer.GetVideoAdapterName()}",
            $"display      {DisplayServer.GetName()} {DisplayServer.WindowGetSize()}, driver {RenderingServer.GetCurrentRenderingDriverName()}",
        };
        if (error != null)
        {
            lines.Add("error        " + error);
        }
        else
        {
            string P(string name) => svc.Property(name) ?? "-";
            lines.Add($"file         {svc.Current?.PlayUrl ?? "-"}");
            lines.Add($"render       {svc.RenderPath}");
            lines.Add($"video        {P("video-codec")} {P("width")}x{P("height")} @ {P("container-fps")} fps");
            lines.Add($"hwdec        {P("hwdec-current")} (asked: {P("hwdec")})");
            lines.Add($"dropped      vo {P("frame-drop-count")}, decoder {P("decoder-frame-drop-count")}, mistimed {P("mistimed-frame-count")}");
            lines.Add($"audio        {P("audio-codec-name")} {P("audio-params/channel-count")} ch");
            // Subtitle tracks as the demuxer found them: Blu-ray rips should list hdmv_pgs_subtitle.
            var subs = svc.Tracks.Where(t => t.Kind == TrackKind.Subtitle).ToList();
            lines.Add($"subtitles    {(subs.Count == 0 ? "none" : string.Join(", ", subs.Select(t => $"{t.Codec ?? "?"} {t.Language ?? "und"}{(t.Forced ? " forced" : "")}")))}");
            lines.Add($"godot fps    avg {(_fpsN > 0 ? _fpsSum / _fpsN : 0):0.0}, min {(_fpsN > 0 ? _fpsMin : 0):0}");
            lines.Add($"played       {svc.Position:0.0} s");
        }
        var text = string.Join('\n', lines);
        GD.Print(text);
        AppLog.Info("bench\n" + text);
        try
        {
            var dir = Main.Instance.Host?.Paths.LogsDir ?? OS.GetUserDataDir();
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"bench-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), text + "\n");
        }
        catch (IOException)
        {
        }
        svc.Stop();
        GetTree().Quit(error == null ? 0 : 1);
    }
}
