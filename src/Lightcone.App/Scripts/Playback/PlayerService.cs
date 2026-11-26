using Godot;
using Lightcone.App.Ui;
using Lightcone.Host;
using Lightcone.Host.Library;
using Lightcone.Playback;

namespace Lightcone.App.Playback;

/// <summary>
/// Owns the one libmpv player the app uses for video and music, and its render target: a Godot
/// texture mpv draws into on the render thread (GL), or a CPU buffer we upload (software path).
/// Any view (the full-screen player, the desktop mini player) shows <see cref="Texture"/>.
/// Progress is saved every few seconds and when playback stops, through the library, so both UI
/// modes and every source share it.
/// </summary>
public partial class PlayerService : Node
{
    private MpvPlayer? _mpv;
    private MpvRenderContext? _render;
    private ImageTexture? _texture;
    private Image? _image;
    private byte[] _buffer = [];
    private Vector2I _size = new(1280, 720);
    private int _fbo;
    private bool _software;
    private double _saveClock;
    private bool _ending;

    public static PlayerService Instance { get; private set; } = null!;

    /// <summary>What's loaded (playing or paused), or null.</summary>
    public MediaItem? Current { get; private set; }

    /// <summary>The picture mpv draws into. Null until something with video plays.</summary>
    public Texture2D? Texture => _texture;

    public bool HasVideo { get; private set; }

    public bool Active => Current != null && _mpv != null;

    public bool Paused => _mpv?.Paused ?? true;

    public double Position { get; private set; }

    public double? Duration { get; private set; }

    /// <summary>The intro/credits marker under the playhead, if any.</summary>
    public Marker? ActiveMarker { get; private set; }

    public IReadOnlyList<MediaTrack> Tracks { get; private set; } = [];

    /// <summary>hwdec mpv actually uses ("vaapi", "videotoolbox", "no"...), for the info panel and the log.</summary>
    public string HwdecCurrent { get; private set; } = "";

    public string RenderPath => _software ? "software" : $"OpenGL ({GlProc.Api})";

    /// <summary>Raised on load, track changes, end and stop (UI refresh).</summary>
    public event Action? Changed;

    /// <summary>Raised when the current item ends by itself.</summary>
    public event Action<MediaItem>? Finished;

    private static AppHost Host => Main.Instance.Host!;

    public override void _EnterTree() => Instance = this;

    public override void _Ready()
    {
        _software = OS.GetCmdlineUserArgs().Contains("--video-sw") || OS.GetEnvironment("LIGHTCONE_VIDEO_SW") == "1";
    }

    /// <summary>True when libmpv can be loaded; otherwise a reason for the user.</summary>
    public static bool EngineAvailable(out string? why) => MpvLibrary.TryLoad(out why);

    private Dictionary<string, string> Options()
    {
        var s = Host.Settings;
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var egl = OperatingSystem.IsLinux() && (RenderingServer.GetCurrentRenderingDriverName() == "opengl3_es" || DisplayServer.GetName() == "Wayland");
        var o = new Dictionary<string, string>
        {
            ["hwdec"] = _software ? (s.Hwdec == HwdecPolicy.Off ? "no" : "auto-copy-safe") : HwdecPolicy.Resolve(s.Hwdec, os, egl),
            ["alang"] = string.Join(',', s.AudioLanguages),
            ["slang"] = string.Join(',', s.SubtitleLanguages),
            ["sub-scale"] = (s.SubtitleScale / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["sub-auto"] = "fuzzy",
            ["sub-ass-override"] = "scale",
            ["audio-display"] = "no",
            ["gapless-audio"] = "weak",
            ["prefetch-playlist"] = "yes",
            ["volume-max"] = "150",
            ["msg-level"] = "all=warn",
        };
        if (s.SubtitleLanguages.Count == 0)
        {
            o["sid"] = "no";
            o["subs-with-matching-audio"] = "forced";
        }
        if (OS.GetCmdlineUserArgs().Contains("--smoke") || OS.GetEnvironment("LIGHTCONE_AO") is { Length: > 0 })
        {
            o["ao"] = OS.GetEnvironment("LIGHTCONE_AO") is { Length: > 0 } ao ? ao : "null";
        }
        return o;
    }

    private bool EnsurePlayer()
    {
        if (_mpv != null)
        {
            return true;
        }
        try
        {
            GlProc.Detect();
            _mpv = new MpvPlayer(Options());
            foreach (var p in new[] { "pause", "track-list", "duration", "hwdec-current", "aid", "sid" })
            {
                _mpv.Observe(p);
            }
            AppLog.Info($"mpv {_mpv.GetString("mpv-version")} from {MpvLibrary.LoadedFrom}, render {RenderPath}");
        }
        catch (Exception e) when (e is MpvException or DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Error("mpv: " + e.Message);
            Main.Instance.Error("Can't play: the playback engine (libmpv) didn't load. " + e.Message);
            _mpv = null;
            return false;
        }
        if (_software)
        {
            _render = MpvRenderContext.CreateSoftware(_mpv);
        }
        else
        {
            RenderingServer.CallOnRenderThread(Callable.From(CreateGlContext));
        }
        return true;
    }

    private unsafe void CreateGlContext()
    {
        try
        {
            GlProc.Init();
            Gl.Load();
            if (!Gl.Loaded)
            {
                throw new InvalidOperationException("GL functions not found");
            }
            _render = MpvRenderContext.CreateOpenGl(_mpv!, &GlProc.MpvGetProc);
        }
        catch (Exception e)
        {
            // Some drivers / contexts can't do it: fall back to the software path for this run.
            AppLog.Warn($"GL render path unavailable ({e.Message}); using software rendering");
            _software = true;
            _render = MpvRenderContext.CreateSoftware(_mpv!);
        }
    }

    /// <summary>Plays <paramref name="item"/>; resumes where it was left unless <paramref name="fromStart"/>.</summary>
    public void Play(MediaItem item, bool fromStart = false)
    {
        if (item.PlayUrl == null || !EnsurePlayer())
        {
            return;
        }
        SaveProgress();
        var start = fromStart ? 0 : Host.Library.PositionOf(item) ?? 0;
        Current = item;
        Position = start;
        Duration = item.Duration;
        HasVideo = item.Kind is not (MediaKind.Track or MediaKind.Album);
        ActiveMarker = null;
        _ending = false;
        AppLog.Info($"play {item.Id} '{item.Title}' from {start:0}s");
        try
        {
            _mpv!.Load(item.PlayUrl, start);
            _mpv.Paused = false;
        }
        catch (MpvException e)
        {
            Main.Instance.Error($"Couldn't open {item.Title}: {e.Message}");
            Current = null;
        }
        Changed?.Invoke();
    }

    public void TogglePause()
    {
        if (_mpv != null && Current != null)
        {
            _mpv.Paused = !_mpv.Paused;
            if (_mpv.Paused)
            {
                SaveProgress();
            }
        }
    }

    public void SeekBy(double seconds)
    {
        if (_mpv != null && Current != null)
        {
            Guard(() => _mpv.Seek(seconds, relative: true));
        }
    }

    public void SeekTo(double seconds)
    {
        if (_mpv != null && Current != null)
        {
            Guard(() => _mpv.Seek(Math.Max(0, seconds)));
        }
    }

    public void SkipMarker()
    {
        if (ActiveMarker is { } m)
        {
            SeekTo(m.End);
        }
    }

    /// <summary>Reads an mpv property as text (null when nothing is loaded or it doesn't exist).</summary>
    public string? Property(string name)
    {
        try
        {
            return _mpv?.GetString(name);
        }
        catch (MpvException)
        {
            return null;
        }
    }

    public void SelectAudio(int id) => Guard(() => _mpv?.SelectAudio(id));

    public void SelectSubtitle(int? id) => Guard(() => _mpv?.SelectSubtitle(id));

    /// <summary>Stops playback (saving where it was) and frees nothing: the next Play reuses the player.</summary>
    public void Stop()
    {
        if (Current == null)
        {
            return;
        }
        SaveProgress();
        AppLog.Info($"stop {Current.Id} at {Position:0}s");
        Current = null;
        HasVideo = false;
        Guard(() => _mpv?.Command("stop"));
        Changed?.Invoke();
    }

    private static void Guard(Action a)
    {
        try
        {
            a();
        }
        catch (MpvException e)
        {
            AppLog.Warn("mpv: " + e.Message);
        }
    }

    private void SaveProgress()
    {
        if (Current is { } item && Position > 0)
        {
            Host.Library.Progress.Report(item.Id, Position, Duration);
        }
    }

    public override void _Process(double delta)
    {
        if (_mpv == null)
        {
            return;
        }
        foreach (var e in _mpv.Poll())
        {
            switch (e.Id)
            {
                case MpvEventId.FileLoaded:
                    HwdecCurrent = _mpv.GetString("hwdec-current") ?? "";
                    Tracks = _mpv.Tracks;
                    HasVideo = Tracks.Any(t => t.Kind == TrackKind.Video) && Current?.Kind is not (MediaKind.Track or MediaKind.Album);
                    AppLog.Info($"loaded: {Tracks.Count} tracks, hwdec {HwdecCurrent}, {_mpv.GetString("video-codec")}");
                    Changed?.Invoke();
                    break;
                case MpvEventId.PropertyChange when e.Property is "track-list" or "aid" or "sid":
                    Tracks = _mpv.Tracks;
                    Changed?.Invoke();
                    break;
                case MpvEventId.PropertyChange when e.Property == "hwdec-current":
                    HwdecCurrent = _mpv.GetString("hwdec-current") ?? "";
                    break;
                case MpvEventId.PropertyChange when e.Property == "duration":
                    // Generated streams (the sample library) report only what's been read so far.
                    Duration = Current?.PlayUrl?.StartsWith("av://", StringComparison.Ordinal) == true
                        ? Current.Duration
                        : _mpv.Duration ?? Current?.Duration;
                    break;
                case MpvEventId.PropertyChange when e.Property == "pause":
                    Changed?.Invoke();
                    break;
                case MpvEventId.EndFile when e.EndReason == 0 && Current is { } done && !_ending:
                    _ending = true;
                    Host.Library.Progress.Report(done.Id, Duration ?? Position, Duration);
                    Finished?.Invoke(done);
                    break;
            }
        }
        if (Current != null)
        {
            Position = _mpv.Position ?? Position;
            if (_mpv.GetString("eof-reached") == "yes" && !_ending && Current is { } item)
            {
                // keep-open=yes: the end shows as eof-reached, not end-file.
                _ending = true;
                Host.Library.Progress.Report(item.Id, Duration ?? Position, Duration);
                Finished?.Invoke(item);
            }
            ActiveMarker = Host.Library.MarkersOf(Current).FirstOrDefault(m => Position >= m.Start && Position < m.End - 1);
            _saveClock += delta;
            if (_saveClock >= ResumePolicy.SaveIntervalSeconds && !_mpv.Paused)
            {
                _saveClock = 0;
                SaveProgress();
            }
        }
        DrawFrame();
    }

    private void EnsureTexture(Vector2I size)
    {
        if (_texture != null && _size == size)
        {
            return;
        }
        _size = size;
        _image = Image.CreateEmpty(size.X, size.Y, false, Image.Format.Rgba8);
        _image.Fill(Colors.Black);
        if (_texture == null)
        {
            _texture = ImageTexture.CreateFromImage(_image);
        }
        else
        {
            _texture.SetImage(_image);
        }
        _buffer = new byte[size.X * size.Y * 4];
        if (!_software && _fbo != 0)
        {
            var old = _fbo;
            _fbo = 0;
            RenderingServer.CallOnRenderThread(Callable.From(() => Gl.DeleteFramebuffer(old)));
        }
    }

    /// <summary>Video size to render at: the video's own, capped to the window (no point drawing 4K into 800p).</summary>
    private Vector2I TargetSize()
    {
        var w = (int)(_mpv?.GetDouble("video-params/dw") ?? 1280);
        var h = (int)(_mpv?.GetDouble("video-params/dh") ?? 720);
        if (w <= 0 || h <= 0)
        {
            (w, h) = (1280, 720);
        }
        var win = DisplayServer.WindowGetSize();
        var scale = Math.Min(1.0, Math.Min((double)Math.Max(640, win.X) / w, (double)Math.Max(360, win.Y) / h));
        return new Vector2I(Math.Max(2, (int)(w * scale)) & ~1, Math.Max(2, (int)(h * scale)) & ~1);
    }

    private void DrawFrame()
    {
        if (_render == null || !HasVideo)
        {
            return;
        }
        EnsureTexture(TargetSize());
        if (_software)
        {
            if (_render.TakeFrame())
            {
                _render.RenderSoftware(_buffer, _size.X, _size.Y);
                _image!.SetData(_size.X, _size.Y, false, Image.Format.Rgba8, _buffer);
                _texture!.Update(_image);
            }
            return;
        }
        var tex = _texture!;
        var size = _size;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_render == null || !_render.TakeFrame())
            {
                return;
            }
            if (_fbo == 0)
            {
                _fbo = Gl.FramebufferFor(RenderingServer.TextureGetNativeHandle(tex.GetRid()));
                if (_fbo == 0)
                {
                    AppLog.Warn("couldn't attach a framebuffer to the video texture");
                    return;
                }
            }
            var prev = Gl.BoundFramebuffer();
            _render.RenderGl(_fbo, size.X, size.Y);
            Gl.Bind(prev);
            _render.ReportSwap();
        }));
    }

    public override void _ExitTree()
    {
        SaveProgress();
        // The render context must go before the player, on the render thread.
        var rc = _render;
        _render = null;
        if (rc != null)
        {
            if (rc.Software)
            {
                rc.Dispose();
            }
            else
            {
                RenderingServer.CallOnRenderThread(Callable.From(rc.Dispose));
                RenderingServer.ForceSync();
            }
        }
        _mpv?.Dispose();
        _mpv = null;
        _texture = null;
    }
}
