using Godot;
using System;
using System.Diagnostics;
using System.Threading;

// Spike: libmpv renders straight into a Godot texture (OpenGL render API, Compatibility renderer),
// with Godot UI composited on top. Scripted run: screenshots + timings, then quit.
public unsafe partial class Main : Control
{
    IntPtr _mpv, _render;
    static int _dirty;
    static readonly bool Shared = OS.GetEnvironment("SPIKE_SHARED") != "0";
    static readonly bool Isolate = OS.GetEnvironment("SPIKE_ISOLATE") != "0";
    ImageTexture _tex = null!, _tex2 = null!; TextureRect _view = null!;
    int _fbo2, _texId2, _back; volatile int _front = -1;
    int _fbo, _w = 1920, _h = 1080;
    Label _osd = null!;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _renderMsTotal; int _frames; double _renderMsMax;
    string _out = OS.GetEnvironment("SPIKE_OUT") is { Length: > 0 } o ? o : "/tmp/shots";
    int _step;

    public override void _Ready()
    {
        Instance = this;
        DirAccess.MakeDirRecursiveAbsolute(_out);
        var bg = new ColorRect { Color = new Color(0.05f, 0.06f, 0.08f) }; bg.SetAnchorsPreset(LayoutPreset.FullRect); AddChild(bg);

        var img = Image.CreateEmpty(_w, _h, false, Image.Format.Rgba8);
        _tex = ImageTexture.CreateFromImage(img); _tex2 = ImageTexture.CreateFromImage(img);
        var view = new TextureRect { Texture = _tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
        view.SetAnchorsPreset(LayoutPreset.FullRect); AddChild(view); _view = view;

        // Godot UI drawn over the video: proves the 10-foot OSD can live in Godot, not in mpv.
        var panel = new PanelContainer(); panel.SetAnchorsPreset(LayoutPreset.BottomWide); panel.OffsetTop = -120; panel.OffsetBottom = -24; panel.OffsetLeft = 48; panel.OffsetRight = -48;
        var sb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.6f), CornerRadiusBottomLeft = 16, CornerRadiusBottomRight = 16, CornerRadiusTopLeft = 16, CornerRadiusTopRight = 16, ContentMarginLeft = 24, ContentMarginTop = 12 };
        panel.AddThemeStyleboxOverride("panel", sb);
        _osd = new Label(); _osd.AddThemeFontSizeOverride("font_size", 22); panel.AddChild(_osd); AddChild(panel);

        RenderingServer.CallOnRenderThread(Callable.From(InitRender));
    }

    void InitRender()
    {
        Gl.DetectLoader(); Gl.Load();
        GD.Print($"GL loader: {Gl.Loader}, GL renderer: {RenderingServer.GetVideoAdapterName()} / {RenderingServer.GetVideoAdapterApiVersion()}");
        GD.Print($"libmpv client API: {Mpv.mpv_client_api_version() >> 16}.{Mpv.mpv_client_api_version() & 0xffff}");

        _mpv = Mpv.mpv_create();
        Set("vo", "libmpv");
        Set("hwdec", OS.GetEnvironment("SPIKE_HWDEC") is { Length: > 0 } hw ? hw : "auto-safe");
        Set("ao", OS.GetEnvironment("SPIKE_AO") is { Length: > 0 } ao ? ao : "null");
        Set("keep-open", "yes");
        Set("sub-auto", "fuzzy");
        Set("terminal", "yes"); Set("msg-level", OS.GetEnvironment("SPIKE_MSG") is { Length: > 0 } m ? m : "all=warn");
        foreach (var kv in OS.GetEnvironment("SPIKE_OPTS").Split(',', StringSplitOptions.RemoveEmptyEntries)) { var i = kv.IndexOf('='); Set(kv[..i], kv[(i + 1)..]); }
        Check(Mpv.mpv_initialize(_mpv), "initialize");

        var texId = (int)RenderingServer.TextureGetNativeHandle(_tex.GetRid());
        _texId2 = (int)RenderingServer.TextureGetNativeHandle(_tex2.GetRid());
        if (Shared && Gl.Loader == "EGL") { StartMpvThread(texId); return; }
        CreateRenderContext(texId);
        var video = OS.GetEnvironment("SPIKE_VIDEO");
        Check(Mpv.mpv_command_string(_mpv, $"loadfile \"{video}\""), "loadfile");
    }

    void CreateRenderContext(int texId)
    {
        var api = System.Text.Encoding.ASCII.GetBytes("opengl\0");
        fixed (byte* apiP = api)
        {
            var init = new Mpv.GlInitParams { GetProcAddress = &Gl.GetProc, Ctx = IntPtr.Zero };
            var ps = stackalloc Mpv.RenderParam[4];
            ps[0] = new() { Type = Mpv.PARAM_API_TYPE, Data = apiP };
            ps[1] = new() { Type = Mpv.PARAM_OPENGL_INIT_PARAMS, Data = &init };
            // Hand mpv the native display so VA-API/EGL interop (zero-copy hwdec) can be set up.
            IntPtr dpy = Gl.Loader == "EGL" ? IntPtr.Zero : Gl.glXGetCurrentDisplay();
            ps[2] = dpy != IntPtr.Zero ? new() { Type = Mpv.PARAM_X11_DISPLAY, Data = (void*)dpy } : new() { Type = Mpv.PARAM_INVALID };
            ps[3] = new() { Type = Mpv.PARAM_INVALID };
            Check(Mpv.mpv_render_context_create(out _render, _mpv, ps), "render_context_create");
        }
        Mpv.mpv_render_context_set_update_callback(_render, &OnMpvUpdate, IntPtr.Zero);

        int fbo; Gl.GenFramebuffers(1, &fbo); _fbo = fbo;
        int prev; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &prev);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, texId, 0);
        GD.Print($"FBO status: 0x{Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER):X} (complete=0x8CD5), godot tex id {texId}");
        { int f2; Gl.GenFramebuffers(1, &f2); _fbo2 = f2; Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo2); Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _texId2, 0); }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, prev);

    }

    // Shared-context mode: mpv gets its own EGL context in Godot's share group, on its own thread.
    // Textures are shared, GL state is not, so neither renderer can disturb the other.
    readonly SemaphoreSlim _wake = new(0);
    volatile bool _quit;
    void StartMpvThread(int texId)
    {
        var dpy = Egl.eglGetCurrentDisplay(); var godotCtx = Egl.eglGetCurrentContext();
        int cfgIdTmp; Egl.eglQueryContext(dpy, godotCtx, Egl.CONFIG_ID, &cfgIdTmp); int cfgId = cfgIdTmp;
        int api = Egl.eglQueryAPI();
        var t = new Thread(() =>
        {
            var attr = stackalloc int[] { Egl.CONFIG_ID, cfgId, Egl.NONE };
            IntPtr cfg; int n; Egl.eglChooseConfig(dpy, attr, &cfg, 1, &n);
            Egl.eglBindAPI(api);
            var cattr = stackalloc int[] { Egl.CONTEXT_MAJOR, 3, Egl.NONE }; if (api != Egl.OPENGL_ES_API) cattr[0] = Egl.NONE;
            var ctx = Egl.eglCreateContext(dpy, cfg, godotCtx, cattr);
            var ok = Egl.eglMakeCurrent(dpy, IntPtr.Zero, IntPtr.Zero, ctx); // surfaceless (EGL_KHR_surfaceless_context)
            GD.Print($"shared EGL context: {ctx != IntPtr.Zero}, current: {ok == 1}, err 0x{Egl.eglGetError():X}");
            CreateRenderContext(texId);
            Check(Mpv.mpv_command_string(_mpv, $"loadfile \"{OS.GetEnvironment("SPIKE_VIDEO")}\""), "loadfile");
            while (!_quit)
            {
                _wake.Wait(100);
                // Double buffer: render into the back texture, finish, then publish it. Godot binds a
                // different texture each swap, which is what makes the other context's writes visible.
                if (DoRender(isolate: false)) { Gl.Finish(); _front = _back; _back ^= 1; }
            }
            Mpv.mpv_render_context_free(_render); _render = IntPtr.Zero;
            Egl.eglMakeCurrent(dpy, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }) { IsBackground = true, Name = "mpv-render" };
        _thread = t; t.Start();
    }
    Thread? _thread;

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    static void OnMpvUpdate(IntPtr _) { Volatile.Write(ref _dirty, 1); Instance?._wake.Release(); }
    static Main? Instance;

    void Set(string k, string v) => Check(Mpv.mpv_set_option_string(_mpv, k, v), $"set {k}={v}");
    static void Check(int r, string what) { if (r < 0) GD.PrintErr($"mpv {what}: {System.Runtime.InteropServices.Marshal.PtrToStringUTF8(Mpv.mpv_error_string(r))}"); }

    void RenderFrame() { if (_thread == null) DoRender(Isolate); }

    bool DoRender(bool isolate)
    {
        if (_render == IntPtr.Zero || Interlocked.Exchange(ref _dirty, 0) == 0) return false;
        if ((Mpv.mpv_render_context_update(_render) & Mpv.UPDATE_FRAME) == 0) return false;
        int prevFbo; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &prevFbo);
        var vp = stackalloc int[4]; Gl.GetIntegerv(Gl.VIEWPORT, vp);
        var fbo = new Mpv.GlFbo { Fbo = (_thread != null && _back == 1) ? _fbo2 : _fbo, W = _w, H = _h };
        int flip = 0; // Godot samples GL textures with v=0 at the top, same as mpv's unflipped FBO output.
        var ps = stackalloc Mpv.RenderParam[3];
        ps[0] = new() { Type = Mpv.PARAM_OPENGL_FBO, Data = &fbo };
        ps[1] = new() { Type = Mpv.PARAM_FLIP_Y, Data = &flip };
        ps[2] = new() { Type = Mpv.PARAM_INVALID };
        var t0 = _clock.Elapsed.TotalMilliseconds;
        // Godot's Compatibility renderer binds sampler objects to texture units; they override the
        // filtering on mpv's plane textures (chroma reads as 0). Unbind them around mpv's render.
        int active; Gl.GetIntegerv(0x84E0, &active);
        var samplers = stackalloc int[16];
        if (isolate) for (int u = 0; u < 16; u++) { Gl.ActiveTexture(0x84C0 + u); Gl.GetIntegerv(0x8919, &samplers[u]); Gl.BindSampler(u, 0); }
        Gl.ActiveTexture(active);
        Mpv.mpv_render_context_render(_render, ps);
        if (isolate) for (int u = 0; u < 16; u++) Gl.BindSampler(u, samplers[u]);
        Gl.ActiveTexture(active);
        var dt = _clock.Elapsed.TotalMilliseconds - t0;
        _renderMsTotal += dt; _frames++; _renderMsMax = Math.Max(_renderMsMax, dt);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, prevFbo); Gl.Viewport(vp[0], vp[1], vp[2], vp[3]);
        return true;
    }

    public override void _Process(double delta)
    {
        if (_mpv == IntPtr.Zero) return;
        while (true)
        {
            var ev = Mpv.mpv_wait_event(_mpv, 0);
            if (ev->EventId == 0) break;
            if (ev->EventId == Mpv.EVENT_FILE_LOADED) GD.Print("event: file-loaded");
            if (ev->EventId == Mpv.EVENT_END_FILE) GD.Print("event: end-file");
        }
        RenderingServer.CallOnRenderThread(Callable.From(RenderFrame));
        if (_thread != null && _front >= 0) _view.Texture = _front == 0 ? _tex : _tex2;

        var t = _clock.Elapsed.TotalSeconds;
        _osd.Text = $"{Mpv.Get(_mpv, "time-pos/full") ?? "-"} / {Mpv.Get(_mpv, "duration") ?? "-"}   " +
                    $"hwdec: {Mpv.Get(_mpv, "hwdec-current") ?? "-"}   audio: {Mpv.Get(_mpv, "current-tracks/audio/title") ?? "-"}   " +
                    $"sub: {Mpv.Get(_mpv, "current-tracks/sub/codec") ?? "off"}   chapter: {Mpv.Get(_mpv, "chapter-metadata/title") ?? "-"}";
        Script(t);
    }

    // Scripted checks: subtitles (ASS, SRT), audio switch, seek (trigger), chapter skip (skip intro), resume via start=.
    void Script(double t)
    {
        void Shot(string name) { RenderingServer.FramePostDraw += Grab; void Grab() { RenderingServer.FramePostDraw -= Grab; GetViewport().GetTexture().GetImage().SavePng($"{_out}/{name}.png"); GD.Print($"shot {name}: t={Mpv.Get(_mpv, "time-pos")} aid={Mpv.Get(_mpv, "aid")} sid={Mpv.Get(_mpv, "sid")} chapter={Mpv.Get(_mpv, "chapter")}"); } }
        (double at, Action act)[] steps =
        {
            (3.0, () => Shot("01-playing-ass-sub")),
            (3.2, () => { Mpv.mpv_set_property_string(_mpv, "aid", "2"); Mpv.mpv_set_property_string(_mpv, "sid", "2"); }),
            (4.5, () => Shot("02-audio2-srt-sub")),
            (4.7, () => { Mpv.mpv_command_string(_mpv, "seek 5 relative"); Mpv.mpv_set_property_string(_mpv, "sid", "no"); }),
            (6.0, () => Shot("03-seek-plus5-subs-off")),
            (6.2, () => { Mpv.mpv_set_property_string(_mpv, "chapter", "1"); }),
            (7.5, () => Shot("04-chapter-skip")),
            (7.7, () => { Mpv.mpv_set_property_string(_mpv, "start", "15"); Mpv.mpv_command_string(_mpv, $"loadfile \"{OS.GetEnvironment("SPIKE_VIDEO")}\""); }),
            (9.5, () => Shot("05-resume-at-15s")),
            (10.0, () => { GD.Print($"render: {_frames} frames, avg {_renderMsTotal / Math.Max(1, _frames):F2} ms, max {_renderMsMax:F2} ms; dropped {Mpv.Get(_mpv, "frame-drop-count")} vo-drop {Mpv.Get(_mpv, "vo-delayed-frame-count")}"); GetTree().Quit(); }),
        };
        while (_step < steps.Length && t >= steps[_step].at) steps[_step++].act();
    }

    public override void _ExitTree()
    {
        _quit = true; _thread?.Join();
        if (_render != IntPtr.Zero) Mpv.mpv_render_context_free(_render);
        if (_mpv != IntPtr.Zero) Mpv.mpv_terminate_destroy(_mpv);
    }
}
