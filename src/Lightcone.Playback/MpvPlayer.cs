using System.Globalization;
using System.Runtime.InteropServices;

namespace Lightcone.Playback;

/// <summary>An mpv event, already copied out of mpv's memory.</summary>
/// <param name="Property">Name of the changed property (PropertyChange only).</param>
/// <param name="EndReason">mpv_end_file_reason (EndFile only): 0 eof, 2 stop, 3 quit, 4 error, 5 redirect.</param>
public sealed record MpvEvent(MpvEventId Id, string? Property = null, int EndReason = -1, int Error = 0);

/// <summary>
/// One libmpv player instance. Godot-free: the app's video view attaches a <see cref="MpvRenderContext"/>
/// to it, the music player uses one with video off. Not thread-safe except where noted; the app
/// drives it from the main thread and drains events once per frame with <see cref="Poll"/>.
/// </summary>
public sealed unsafe class MpvPlayer : IDisposable
{
    private nint _ctx;
    private readonly Dictionary<ulong, string> _observed = new();
    private ulong _nextObserveId = 1;

    /// <param name="options">mpv options set before initialisation (vo, hwdec, ao...).</param>
    public MpvPlayer(IReadOnlyDictionary<string, string>? options = null)
    {
        if (!MpvLibrary.TryLoad(out var error))
        {
            throw new MpvException(error ?? "libmpv not available", -1);
        }
        _ctx = MpvNative.mpv_create();
        if (_ctx == 0)
        {
            throw new MpvException("mpv_create failed", -1);
        }
        // Lightcone draws its own OSD and owns input; mpv must not read config files or the terminal.
        foreach (var (k, v) in new Dictionary<string, string>
        {
            ["config"] = "no",
            ["terminal"] = "no",
            ["input-default-bindings"] = "no",
            ["input-vo-keyboard"] = "no",
            ["osc"] = "no",
            ["idle"] = "yes",
            ["keep-open"] = "yes",
            ["vo"] = "libmpv",
        })
        {
            MpvNative.mpv_set_option_string(_ctx, k, v);
        }
        if (options != null)
        {
            foreach (var (k, v) in options)
            {
                MpvNative.Check(MpvNative.mpv_set_option_string(_ctx, k, v), $"option {k}={v}");
            }
        }
        MpvNative.Check(MpvNative.mpv_initialize(_ctx), "mpv_initialize");
    }

    internal nint Handle => _ctx != 0 ? _ctx : throw new ObjectDisposedException(nameof(MpvPlayer));

    public bool Disposed => _ctx == 0;

    /// <summary>Runs an mpv command ("loadfile", "seek"...). Throws on error.</summary>
    public void Command(params string[] args)
    {
        var ptrs = stackalloc nint[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            }
            ptrs[args.Length] = 0;
            MpvNative.Check(MpvNative.mpv_command(Handle, ptrs), args.Length > 0 ? args[0] : "command");
        }
        finally
        {
            for (var i = 0; i < args.Length; i++)
            {
                Marshal.FreeCoTaskMem(ptrs[i]);
            }
        }
    }

    public void Load(string url, double startSeconds = 0)
    {
        if (startSeconds > 0)
        {
            Command("loadfile", url, "replace", "-1", $"start={startSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
        }
        else
        {
            Command("loadfile", url);
        }
    }

    public void Seek(double seconds, bool relative = false) =>
        Command("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), relative ? "relative" : "absolute");

    public bool Paused
    {
        get => GetString("pause") == "yes";
        set => SetString("pause", value ? "yes" : "no");
    }

    public double? Position => GetDouble("time-pos");

    public double? Duration => GetDouble("duration");

    /// <summary>Selects an audio track by mpv track id.</summary>
    public void SelectAudio(int id) => SetString("aid", id.ToString(CultureInfo.InvariantCulture));

    /// <summary>Selects a subtitle track by mpv track id; null turns subtitles off.</summary>
    public void SelectSubtitle(int? id) => SetString("sid", id?.ToString(CultureInfo.InvariantCulture) ?? "no");

    /// <summary>The current file's audio, video and subtitle tracks.</summary>
    public IReadOnlyList<MediaTrack> Tracks => MediaTrack.ParseList(GetString("track-list"));

    public string? GetString(string name)
    {
        var p = MpvNative.mpv_get_property_string(Handle, name);
        if (p == 0)
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUTF8(p);
        }
        finally
        {
            MpvNative.mpv_free(p);
        }
    }

    public double? GetDouble(string name)
    {
        double d;
        return MpvNative.mpv_get_property(Handle, name, MpvNative.FormatDouble, &d) >= 0 ? d : null;
    }

    public void SetString(string name, string value) =>
        MpvNative.Check(MpvNative.mpv_set_property_string(Handle, name, value), $"set {name}");

    /// <summary>Reports changes of <paramref name="name"/> as <see cref="MpvEventId.PropertyChange"/> events.</summary>
    public void Observe(string name)
    {
        var id = _nextObserveId++;
        _observed[id] = name;
        MpvNative.Check(MpvNative.mpv_observe_property(Handle, id, name, MpvNative.FormatNone), $"observe {name}");
    }

    /// <summary>Drains mpv's event queue without waiting.</summary>
    public List<MpvEvent> Poll(double timeout = 0)
    {
        var list = new List<MpvEvent>();
        while (_ctx != 0)
        {
            var e = MpvNative.mpv_wait_event(_ctx, list.Count == 0 ? timeout : 0);
            if (e->EventId == MpvEventId.None)
            {
                break;
            }
            switch (e->EventId)
            {
                case MpvEventId.PropertyChange:
                    var prop = (MpvEventPropertyRaw*)e->Data;
                    list.Add(new MpvEvent(e->EventId, Property: Marshal.PtrToStringUTF8(prop->Name)));
                    break;
                case MpvEventId.EndFile:
                    var end = (MpvEventEndFileRaw*)e->Data;
                    list.Add(new MpvEvent(e->EventId, EndReason: end->Reason, Error: end->Error));
                    break;
                default:
                    list.Add(new MpvEvent(e->EventId, Error: e->Error));
                    break;
            }
            if (e->EventId == MpvEventId.Shutdown)
            {
                break;
            }
        }
        return list;
    }

    /// <summary>Waits (up to <paramref name="timeout"/>) for an event; for tests and the headless probe.</summary>
    public MpvEvent? WaitFor(MpvEventId id, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            foreach (var e in Poll(0.05))
            {
                if (e.Id == id)
                {
                    return e;
                }
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_ctx != 0)
        {
            var ctx = _ctx;
            _ctx = 0;
            MpvNative.mpv_terminate_destroy(ctx);
        }
    }
}
