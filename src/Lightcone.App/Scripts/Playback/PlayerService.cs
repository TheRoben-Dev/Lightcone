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

    /// <summary>
    /// Where the bar and clock should show: the target of a seek that is still in flight (for up to
    /// a second and a half), else <see cref="Position"/>. Keeps rapid presses adding up visibly.
    /// </summary>
    public double DisplayPosition =>
        _seekTarget is { } t && Time.GetTicksMsec() - _seekAt < 1500 && Math.Abs(Position - t) > 1 ? t : Position;

    private double? _seekTarget;
    private ulong _seekAt;
    private ulong _lastStepAt;
    private int _stepRun;
    private int _stepDir;

    public double? Duration { get; private set; }

    /// <summary>The intro/credits marker under the playhead, if any.</summary>
    public Marker? ActiveMarker { get; private set; }

    private (string, double)? _autoSkipped;

    public IReadOnlyList<MediaTrack> Tracks { get; private set; } = [];

    /// <summary>hwdec mpv actually uses ("vaapi", "videotoolbox", "no"...), for the info panel and the log.</summary>
    public string HwdecCurrent { get; private set; } = "";

    public string RenderPath => _software ? "software" : $"OpenGL ({GlProc.Api})";

    /// <summary>Raised on load, track changes, end and stop (UI refresh).</summary>
    public event Action? Changed;

    /// <summary>Raised when the current item ends by itself.</summary>
    public event Action<MediaItem>? Finished;

    // ---- sleep timer ----

    /// <summary>Sleep timer: seconds of playback left before it pauses (null = off).</summary>
    public double? SleepLeft { get; private set; }

    /// <summary>Sleep timer set to stop at the end of what's playing (no next episode).</summary>
    public bool SleepAtEnd { get; private set; }

    /// <summary>What the sleep timer was set to (minutes, -1 end of item, 0 off).</summary>
    public int SleepChoice { get; private set; }

    /// <summary>Volume fades over this long before the sleep timer pauses.</summary>
    public const double SleepFadeSeconds = 30;

    /// <summary>Raised when the sleep timer paused or stopped playback.</summary>
    public event Action? SleepFired;

    /// <summary>Sets the sleep timer: minutes from now, 0 = off, -1 = at the end of this episode / movie.</summary>
    public void SetSleep(int minutes)
    {
        RestoreVolume();
        SleepAtEnd = minutes < 0;
        SleepLeft = minutes > 0 ? minutes * 60 : null;
        SleepChoice = minutes;
        AppLog.Info($"sleep timer: {(minutes > 0 ? $"{minutes} min" : minutes < 0 ? "end of item" : "off")}");
        Changed?.Invoke();
    }

    /// <summary>"Sleep in 24 min", "Sleep after this episode", or null when off.</summary>
    public string? SleepText => SleepAtEnd ? $"Sleep after this {(Current?.Kind == MediaKind.Episode ? "episode" : "one")}"
        : SleepLeft is { } left ? $"Sleep in {Math.Max(1, (int)Math.Ceiling(left / 60))} min" : null;

    private bool _faded;

    private void RestoreVolume()
    {
        if (_faded)
        {
            _faded = false;
            Guard(() => _mpv?.SetString("volume", "100"));
        }
    }

    /// <summary>Called by the player when it finishes an item: true when the sleep timer ends playback here.</summary>
    public bool SleepEndsHere()
    {
        if (!SleepAtEnd)
        {
            return false;
        }
        SleepAtEnd = false;
        SleepChoice = 0;
        AppLog.Info("sleep timer: stopped at the end");
        SleepFired?.Invoke();
        return true;
    }

    private void TickSleep(double delta)
    {
        if (SleepLeft is not { } left || _mpv == null || _mpv.Paused)
        {
            return;
        }
        left -= delta;
        if (left <= 0)
        {
            SleepLeft = null;
            SleepChoice = 0;
            _mpv.Paused = true;
            SaveProgress(PlaybackState.Paused);
            RestoreVolume();
            AppLog.Info("sleep timer: paused");
            SleepFired?.Invoke();
            Changed?.Invoke();
            return;
        }
        SleepLeft = left;
        if (left < SleepFadeSeconds)
        {
            _faded = true;
            var v = (int)(100 * Math.Clamp(left / SleepFadeSeconds, 0.05, 1));
            Guard(() => _mpv.SetString("volume", v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    // ---- speed and delays (remembered per show / movie) ----

    public double Speed { get; private set; } = 1;
    public double SubDelay { get; private set; }
    public double AudioDelay { get; private set; }

    private static string Num(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    public void SetSpeed(double v)
    {
        Speed = Math.Clamp(v, 0.25, 4);
        Guard(() => _mpv?.SetString("speed", Num(Speed)));
        if (TrackKey(Current) is { } key)
        {
            Host.Library.Lists.RememberPlayback(key, speed: Speed);
        }
        Changed?.Invoke();
    }

    public void SetSubDelay(double v)
    {
        SubDelay = Math.Round(v, 2);
        Guard(() => _mpv?.SetString("sub-delay", Num(SubDelay)));
        if (TrackKey(Current) is { } key)
        {
            Host.Library.Lists.RememberPlayback(key, subDelay: SubDelay);
        }
        Changed?.Invoke();
    }

    public void SetAudioDelay(double v)
    {
        AudioDelay = Math.Round(v, 2);
        Guard(() => _mpv?.SetString("audio-delay", Num(AudioDelay)));
        if (TrackKey(Current) is { } key)
        {
            Host.Library.Lists.RememberPlayback(key, audioDelay: AudioDelay);
        }
        Changed?.Invoke();
    }

    // ---- what plays next: in order, or shuffled ----

    /// <summary>The show being shuffled (random episodes in a row), or null.</summary>
    public string? ShuffleShow { get; private set; }

    private readonly HashSet<string> _shufflePlayed = new();

    /// <summary>Plays random episodes of a show, one after another.</summary>
    public void Shuffle(MediaItem show)
    {
        ShuffleShow = show.Id;
        _shufflePlayed.Clear();
        AutoRun = 0;
        if (UpNextFor(null) is { } first)
        {
            Play(first, fromStart: Host.Library.PositionOf(first) == null);
        }
    }

    /// <summary>
    /// What plays after <paramref name="done"/>: a random episode not played yet in this shuffle
    /// (unwatched ones first), else the next episode or track.
    /// </summary>
    public MediaItem? UpNextFor(MediaItem? done)
    {
        if (ShuffleShow is { } show && (done == null || done.ParentId == show))
        {
            var eps = Host.Library.Children(show).Where(e => e.Playable && e.Id != done?.Id && !_shufflePlayed.Contains(e.Id)).ToList();
            if (eps.Count == 0)
            {
                return null;
            }
            var fresh = eps.Where(e => !Host.Library.IsWatched(e)).ToList();
            var pool = fresh.Count > 0 && Random.Shared.Next(3) > 0 ? fresh : eps;
            return pool[Random.Shared.Next(pool.Count)];
        }
        return done == null ? null : Host.Library.NextAfter(done);
    }

    /// <summary>Episodes in a row that started by themselves (no button pressed since): for "Still watching?".</summary>
    public int AutoRun { get; private set; }

    /// <summary>Any press in the player: the viewer is still there.</summary>
    public void NoteInput() => AutoRun = 0;

    /// <summary>True when the next autoplay should ask "Still watching?" first.</summary>
    public bool ShouldAskStillWatching => Host.Settings.StillWatchingAfter is > 0 and var n && AutoRun >= n;

    /// <summary>Plays what comes after the current item; <paramref name="auto"/> when nobody pressed anything.</summary>
    public MediaItem? PlayNext(bool auto)
    {
        if (Current is not { } cur || UpNextFor(cur) is not { } next)
        {
            return null;
        }
        if (Position > 0 && Duration is { } d && Position >= d - Math.Max(120, d * 0.08))
        {
            Host.Library.SetWatched(cur, true);  // leaving during the credits counts as watched
        }
        AutoRun = auto ? AutoRun + 1 : 0;
        AppLog.Info($"next{(auto ? " (auto)" : "")}: {next.Id}");
        Play(next, fromStart: true);
        return next;
    }

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

    /// <summary>Plays <paramref name="item"/>; resumes where it was left unless <paramref name="fromStart"/> (or <paramref name="startAt"/> seconds).</summary>
    public void Play(MediaItem item, bool fromStart = false, double? startAt = null)
    {
        if (item.PlayUrl == null || !EnsurePlayer())
        {
            return;
        }
        SaveProgress(PlaybackState.Stopped);
        FlushHistory();
        if (ShuffleShow != null && item.ParentId != ShuffleShow)
        {
            ShuffleShow = null;
        }
        if (ShuffleShow != null)
        {
            _shufflePlayed.Add(item.Id);
        }
        RestoreVolume();
        var start = startAt ?? (fromStart ? 0 : Host.Library.PositionOf(item) ?? 0);
        Current = item;
        Position = start;
        _seekTarget = null;
        Duration = item.Duration;
        HasVideo = item.Kind is not (MediaKind.Track or MediaKind.Album);
        ActiveMarker = null;
        _ending = false;
        AppLog.Info($"play {item.Id} '{item.Title}' from {start:0}s");
        try
        {
            ApplyTrackChoice(item);
            _mpv!.Load(item.PlayUrl, start);
            _mpv.Paused = false;
        }
        catch (MpvException e)
        {
            Main.Instance.Error($"Couldn't open {item.Title}: {e.Message}");
            Current = null;
        }
        if (Current != null)
        {
            // Intro / credits markers from the server, ready by the time the intro plays.
            _ = Host.Library.PrepareAsync(item);
        }
        Changed?.Invoke();
    }

    public void TogglePause()
    {
        if (_mpv != null && Current != null)
        {
            _mpv.Paused = !_mpv.Paused;
            SaveProgress(_mpv.Paused ? PlaybackState.Paused : PlaybackState.Playing);
        }
    }

    public void SeekBy(double seconds)
    {
        if (_mpv != null && Current != null)
        {
            var from = DisplayPosition;
            Guard(() => _mpv.Seek(seconds, relative: true));
            MarkSeek(from + seconds);
        }
    }

    public void SeekTo(double seconds)
    {
        if (_mpv != null && Current != null)
        {
            Guard(() => _mpv.Seek(Math.Max(0, seconds)));
            MarkSeek(seconds);
        }
    }

    /// <summary>
    /// One skip press (<paramref name="dir"/> -1 back, +1 forward) by the step from Settings. Presses
    /// that follow each other quickly in the same direction (a held trigger or key) speed up: after
    /// three, each jumps twice as far, after six four times, after ten eight times.
    /// </summary>
    public void Step(int dir)
    {
        var now = Time.GetTicksMsec();
        var since = now - _lastStepAt;
        if (dir == _stepDir && since < 180)
        {
            return;  // d-pad and key auto-repeat come faster than seeks are useful; the multiplier speeds up instead
        }
        _stepRun = dir == _stepDir && since < 800 ? _stepRun + 1 : 0;
        _stepDir = dir;
        _lastStepAt = now;
        var s = Host.Settings;
        var step = dir < 0 ? s.SeekBack : s.SeekForward;
        var mult = _stepRun >= 10 ? 8 : _stepRun >= 6 ? 4 : _stepRun >= 3 ? 2 : 1;
        SeekBy(dir * step * mult);
    }

    private void MarkSeek(double target)
    {
        _seekTarget = Math.Clamp(target, 0, Duration ?? double.MaxValue);
        _seekAt = Time.GetTicksMsec();
    }

    /// <summary>During an episode's end credits with another episode after it: what the skip button plays instead.</summary>
    public MediaItem? NextInsteadOfCredits =>
        ActiveMarker is { Kind: Marker.Credits } && Current is { Kind: MediaKind.Episode } cur && !SleepAtEnd ? UpNextFor(cur) : null;

    public string? SkipLabel => ActiveMarker is { } m ? NextInsteadOfCredits != null ? "Next episode" : m.Label : null;

    public void SkipMarker()
    {
        if (NextInsteadOfCredits is { } next && Current is { } cur)
        {
            AppLog.Info($"credits: on to {next.Id}");
            Host.Library.SetWatched(cur, true);
            AutoRun = 0;
            Play(next, fromStart: true);
            return;
        }
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

    public void SelectAudio(int id)
    {
        Guard(() => _mpv?.SelectAudio(id));
        if (TrackKey(Current) is { } key && Tracks.FirstOrDefault(t => t.Id == id && t.Kind == TrackKind.Audio)?.Language is { } lang)
        {
            Host.Library.Lists.RememberTracks(key, audio: lang);
        }
    }

    public void SelectSubtitle(int? id)
    {
        Guard(() => _mpv?.SelectSubtitle(id));
        if (TrackKey(Current) is { } key)
        {
            var lang = id is { } i ? Tracks.FirstOrDefault(t => t.Id == i && t.Kind == TrackKind.Subtitle)?.Language : null;
            Host.Library.Lists.RememberTracks(key, subtitle: lang, subtitlesOff: id == null);
        }
    }

    /// <summary>Tracks are remembered per show (every episode) or per movie.</summary>
    private static string? TrackKey(MediaItem? item) => item switch
    {
        { Kind: MediaKind.Episode, ParentId: { } show } => show,
        { Kind: MediaKind.Movie or MediaKind.Episode } => item.Id,
        _ => null,
    };

    /// <summary>Before a file loads: the show's remembered audio / subtitle languages first, then the settings'.</summary>
    private void ApplyTrackChoice(MediaItem item)
    {
        var s = Host.Settings;
        var pref = TrackKey(item) is { } key ? Host.Library.Lists.TrackChoice(key) : null;
        var alang = (pref?.Audio is { } a ? s.AudioLanguages.Prepend(a) : s.AudioLanguages).Distinct();
        var slang = (pref?.Subtitle is { } sl ? s.SubtitleLanguages.Prepend(sl) : s.SubtitleLanguages).Distinct();
        var off = pref?.SubtitlesOff ?? s.SubtitleLanguages.Count == 0;
        if (pref != null && pref.Subtitle != null && !pref.SubtitlesOff)
        {
            off = false;
        }
        _mpv!.SetString("alang", string.Join(',', alang));
        _mpv.SetString("slang", string.Join(',', slang));
        _mpv.SetString("aid", "auto");
        _mpv.SetString("sid", off ? "no" : "auto");
        // mpv keeps these across files: set each one, remembered or default.
        Speed = pref?.Speed ?? 1;
        SubDelay = pref?.SubDelay ?? 0;
        AudioDelay = pref?.AudioDelay ?? 0;
        _mpv.SetString("speed", Num(Speed));
        _mpv.SetString("sub-delay", Num(SubDelay));
        _mpv.SetString("audio-delay", Num(AudioDelay));
        if (pref != null)
        {
            AppLog.Info($"tracks for {item.Id}: audio {pref.Audio ?? "default"}, subtitles {(off ? "off" : pref.Subtitle ?? "default")}, speed {Speed}, delays {SubDelay}/{AudioDelay}");
        }
    }

    /// <summary>Stops playback (saving where it was) and frees nothing: the next Play reuses the player.</summary>
    public void Stop()
    {
        if (Current == null)
        {
            return;
        }
        SaveProgress(PlaybackState.Stopped);
        FlushHistory();
        AppLog.Info($"stop {Current.Id} at {Position:0}s");
        Current = null;
        ShuffleShow = null;
        SleepLeft = null;
        SleepAtEnd = false;
        SleepChoice = 0;
        RestoreVolume();
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

    private double _watchedSeconds;

    /// <summary>Adds the time watched since the last call to the history (the stats page).</summary>
    private void FlushHistory(bool finished = false, bool flush = true)
    {
        if (Current is not { } item || item.SourceId == "file" || (_watchedSeconds < 1 && !finished))
        {
            _watchedSeconds = 0;
            return;
        }
        var show = item.ParentId != null ? Host.Library.Find(item.ParentId) : null;
        Host.Library.History.Add(item, show, _watchedSeconds, finished, flush: flush);
        _watchedSeconds = 0;
    }

    private void SaveProgress(PlaybackState state = PlaybackState.Playing)
    {
        if (Current is { } item && Position > 0)
        {
            Host.Library.Report(item, Position, Duration, state);
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
                    FlushHistory(finished: true);
                    Host.Library.Report(done, Duration ?? Position, Duration, PlaybackState.Stopped);
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
                FlushHistory(finished: true);
                Host.Library.Report(item, Duration ?? Position, Duration, PlaybackState.Stopped);
                Finished?.Invoke(item);
            }
            ActiveMarker = Host.Library.MarkersOf(Current).FirstOrDefault(m => Position >= m.Start && Position < m.End - 1);
            if (ActiveMarker is { Kind: Marker.Intro } intro && Host.Settings.AutoSkipIntro && _autoSkipped != (Current.Id, intro.Start))
            {
                _autoSkipped = (Current.Id, intro.Start);  // once: seeking back into it plays it
                AppLog.Info($"auto-skip intro of {Current.Id}");
                SkipMarker();
                Main.Instance.Toast("Skipped the intro");
            }
            if (!_mpv.Paused)
            {
                _watchedSeconds += delta * Speed;
            }
            TickSleep(delta);
            _saveClock += delta;
            if (_saveClock >= ResumePolicy.SaveIntervalSeconds && !_mpv.Paused)
            {
                _saveClock = 0;
                SaveProgress();
                FlushHistory(flush: false);
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
        SaveProgress(PlaybackState.Stopped);
        FlushHistory();
        Host.Library.History.Flush();
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
