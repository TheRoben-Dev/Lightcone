using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Lightcone.Playback;

/// <summary>
/// mpv's render API for one player: draws video frames into a target the app owns. Two backends:
/// <list type="bullet">
/// <item>OpenGL: frames go straight into an FBO (a Godot texture) on the render thread. Needs a
/// GL proc-address function for the context Godot runs (GLX, EGL, WGL or CGL).</item>
/// <item>Software: frames go into a byte buffer (RGBX) the app uploads. Used where GL interop
/// isn't available, and by the headless tests.</item>
/// </list>
/// Must be created, rendered and freed on the thread that owns the GL context (OpenGL backend).
/// </summary>
public sealed unsafe class MpvRenderContext : IDisposable
{
    private nint _ctx;
    private readonly GCHandle _self;
    private int _dirty;

    /// <summary>Raised from an mpv thread when a new frame is ready (keep it cheap).</summary>
    public event Action? FrameReady;

    public bool Software { get; }

    /// <summary>
    /// True: rendering waits for each frame's display time, so the app presents at the video's own
    /// cadence (what a VRR screen wants while nobody touches anything). False: never wait, so the
    /// UI keeps the display's full rate.
    /// </summary>
    public bool PaceToVideo { get; set; }

    /// <summary>True while mpv has a frame waiting (peek, doesn't take it).</summary>
    public bool FramePending => Volatile.Read(ref _dirty) == 1;

    private MpvRenderContext(MpvPlayer player, bool software, delegate* unmanaged[Cdecl]<nint, nint, nint> getProcAddress)
    {
        Software = software;
        var api = Marshal.StringToCoTaskMemUTF8(software ? "sw" : "opengl");
        try
        {
            var init = new MpvOpenGlInitParams { GetProcAddress = getProcAddress };
            var p = stackalloc MpvRenderParam[3];
            p[0] = new MpvRenderParam(MpvNative.RenderApiType, api);
            p[1] = software ? default : new MpvRenderParam(MpvNative.RenderOpenGlInitParams, (nint)(&init));
            p[2] = default;
            MpvNative.Check(MpvNative.mpv_render_context_create(out _ctx, player.Handle, p), "mpv_render_context_create");
        }
        finally
        {
            Marshal.FreeCoTaskMem(api);
        }
        _self = GCHandle.Alloc(this);
        MpvNative.mpv_render_context_set_update_callback(_ctx, &OnUpdate, GCHandle.ToIntPtr(_self));
    }

    public static MpvRenderContext CreateSoftware(MpvPlayer player) => new(player, true, null);

    /// <param name="getProcAddress">(ctx, const char* name) -> GL function pointer, for the current context.</param>
    public static MpvRenderContext CreateOpenGl(MpvPlayer player, delegate* unmanaged[Cdecl]<nint, nint, nint> getProcAddress) =>
        new(player, false, getProcAddress);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUpdate(nint data)
    {
        if (GCHandle.FromIntPtr(data).Target is MpvRenderContext rc)
        {
            Interlocked.Exchange(ref rc._dirty, 1);
            rc.FrameReady?.Invoke();
        }
    }

    /// <summary>True (once) when mpv has a new frame to draw. Call from the render thread.</summary>
    public bool TakeFrame() =>
        _ctx != 0 && Interlocked.Exchange(ref _dirty, 0) == 1 && (MpvNative.mpv_render_context_update(_ctx) & 1) != 0;

    /// <summary>Draws the current frame into GL framebuffer <paramref name="fbo"/> (<paramref name="w"/>×<paramref name="h"/>).</summary>
    public void RenderGl(int fbo, int w, int h, bool flipY = false)
    {
        var target = new MpvOpenGlFbo { Fbo = fbo, W = w, H = h };
        var flip = flipY ? 1 : 0;
        var block = PaceToVideo ? 1 : 0;
        var p = stackalloc MpvRenderParam[4];
        p[0] = new MpvRenderParam(MpvNative.RenderOpenGlFbo, (nint)(&target));
        p[1] = new MpvRenderParam(MpvNative.RenderFlipY, (nint)(&flip));
        // mpv's default is to block until the frame's display time. This runs on the app's render
        // thread, so blocking paces the whole UI to the video: only wanted while idle (PaceToVideo).
        p[2] = new MpvRenderParam(MpvNative.RenderBlockForTargetTime, (nint)(&block));
        p[3] = default;
        MpvNative.Check(MpvNative.mpv_render_context_render(_ctx, p), "render");
    }

    /// <summary>Draws the current frame as RGBX (4 bytes per pixel, X undefined) into <paramref name="buffer"/>.</summary>
    public void RenderSoftware(Span<byte> buffer, int w, int h)
    {
        if (buffer.Length < w * h * 4)
        {
            throw new ArgumentException("buffer too small", nameof(buffer));
        }
        var size = stackalloc int[] { w, h };
        nuint stride = (nuint)(w * 4);
        var fmt = Marshal.StringToCoTaskMemUTF8("rgb0");
        try
        {
            fixed (byte* b = buffer)
            {
                var block = PaceToVideo ? 1 : 0;
                var p = stackalloc MpvRenderParam[6];
                p[0] = new MpvRenderParam(MpvNative.RenderSwSize, (nint)size);
                p[1] = new MpvRenderParam(MpvNative.RenderSwFormat, fmt);
                p[2] = new MpvRenderParam(MpvNative.RenderSwStride, (nint)(&stride));
                p[3] = new MpvRenderParam(MpvNative.RenderSwPointer, (nint)b);
                p[4] = new MpvRenderParam(MpvNative.RenderBlockForTargetTime, (nint)(&block));
                p[5] = default;
                MpvNative.Check(MpvNative.mpv_render_context_render(_ctx, p), "render (sw)");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(fmt);
        }
    }

    /// <summary>Tells mpv the frame was shown (better frame timing).</summary>
    public void ReportSwap()
    {
        if (_ctx != 0)
        {
            MpvNative.mpv_render_context_report_swap(_ctx);
        }
    }

    /// <summary>Frees the context. Must happen before the player is disposed, on the render thread.</summary>
    public void Dispose()
    {
        if (_ctx != 0)
        {
            var ctx = _ctx;
            _ctx = 0;
            MpvNative.mpv_render_context_free(ctx);
            _self.Free();
        }
    }
}
