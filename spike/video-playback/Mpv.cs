using System;
using System.Runtime.InteropServices;

// Minimal libmpv binding: client API + OpenGL render API (render.h / render_gl.h).
public static unsafe partial class Mpv
{
    const string Lib = "mpv";
    static Mpv()
    {
        NativeLibrary.SetDllImportResolver(typeof(Mpv).Assembly, (name, asm, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            foreach (var n in new[] { "libmpv.so.2", "libmpv.so.1", "libmpv-2.dll", "mpv-2.dll", "libmpv.2.dylib" })
                if (NativeLibrary.TryLoad(n, asm, path, out var h)) return h;
            return IntPtr.Zero;
        });
    }

    [DllImport(Lib)] public static extern IntPtr mpv_create();
    [DllImport(Lib)] public static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Lib)] public static extern void mpv_terminate_destroy(IntPtr ctx);
    [DllImport(Lib)] public static extern ulong mpv_client_api_version();
    [DllImport(Lib)] public static extern int mpv_set_option_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);
    [DllImport(Lib)] public static extern int mpv_set_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);
    [DllImport(Lib)] public static extern int mpv_command_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string args);
    [DllImport(Lib)] static extern IntPtr mpv_get_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] static extern void mpv_free(IntPtr p);
    [DllImport(Lib)] public static extern IntPtr mpv_error_string(int err);
    [DllImport(Lib)] public static extern MpvEvent* mpv_wait_event(IntPtr ctx, double timeout);
    [DllImport(Lib)] public static extern int mpv_render_context_create(out IntPtr res, IntPtr mpv, RenderParam* p);
    [DllImport(Lib)] public static extern void mpv_render_context_set_update_callback(IntPtr r, delegate* unmanaged<IntPtr, void> cb, IntPtr ctx);
    [DllImport(Lib)] public static extern ulong mpv_render_context_update(IntPtr r);
    [DllImport(Lib)] public static extern int mpv_render_context_render(IntPtr r, RenderParam* p);
    [DllImport(Lib)] public static extern void mpv_render_context_free(IntPtr r);

    public static string? Get(IntPtr ctx, string name)
    {
        var p = mpv_get_property_string(ctx, name);
        if (p == IntPtr.Zero) return null;
        var s = Marshal.PtrToStringUTF8(p); mpv_free(p); return s;
    }

    [StructLayout(LayoutKind.Sequential)] public struct RenderParam { public int Type; public void* Data; }
    [StructLayout(LayoutKind.Sequential)] public struct GlInitParams { public delegate* unmanaged<IntPtr, IntPtr, IntPtr> GetProcAddress; public IntPtr Ctx; }
    [StructLayout(LayoutKind.Sequential)] public struct GlFbo { public int Fbo, W, H, InternalFormat; }
    [StructLayout(LayoutKind.Sequential)] public struct MpvEvent { public int EventId; public int Error; public ulong ReplyUserdata; public IntPtr Data; }

    public const int PARAM_INVALID = 0, PARAM_API_TYPE = 1, PARAM_OPENGL_INIT_PARAMS = 2, PARAM_OPENGL_FBO = 3, PARAM_FLIP_Y = 4,
        PARAM_X11_DISPLAY = 8, PARAM_WL_DISPLAY = 9;
    public const ulong UPDATE_FRAME = 1;
    public const int EVENT_SHUTDOWN = 1, EVENT_LOG_MESSAGE = 2, EVENT_END_FILE = 7, EVENT_FILE_LOADED = 8, EVENT_PLAYBACK_RESTART = 21;
}

// GL entry points, resolved through whichever loader owns Godot's current context (EGL or GLX).
public static unsafe class Gl
{
    [DllImport("libEGL.so.1")] static extern IntPtr eglGetProcAddress(byte* name);
    [DllImport("libEGL.so.1")] static extern IntPtr eglGetCurrentContext();
    [DllImport("libEGL.so.1")] public static extern IntPtr eglGetCurrentDisplay();
    [DllImport("libGL.so.1")] static extern IntPtr glXGetProcAddressARB(byte* name);
    [DllImport("libGL.so.1")] static extern IntPtr glXGetCurrentContext();
    [DllImport("libGL.so.1")] public static extern IntPtr glXGetCurrentDisplay();

    public static string Loader = "?";
    public static void DetectLoader()
    {
        try { if (eglGetCurrentContext() != IntPtr.Zero) { Loader = "EGL"; return; } } catch { }
        try { if (glXGetCurrentContext() != IntPtr.Zero) { Loader = "GLX"; return; } } catch { }
        Loader = "none";
    }

    [UnmanagedCallersOnly]
    public static IntPtr GetProc(IntPtr ctx, IntPtr name) =>
        Loader == "EGL" ? eglGetProcAddress((byte*)name) : glXGetProcAddressARB((byte*)name);

    static IntPtr P(string n) { var b = System.Text.Encoding.ASCII.GetBytes(n + "\0"); fixed (byte* p = b) return Loader == "EGL" ? eglGetProcAddress(p) : glXGetProcAddressARB(p); }

    public static delegate* unmanaged<int, int*, void> GenFramebuffers;
    public static delegate* unmanaged<int, int, void> BindFramebuffer;
    public static delegate* unmanaged<int, int, int, int, int, void> FramebufferTexture2D;
    public static delegate* unmanaged<int, int> CheckFramebufferStatus;
    public static delegate* unmanaged<int, int*, void> GetIntegerv;
    public static delegate* unmanaged<int, int, int, int, void> Viewport;
    public static delegate* unmanaged<byte*> GetStringRenderer;

    public static delegate* unmanaged<int, void> Enable, Disable, UseProgram, ActiveTexture, BindVertexArray;
    public static delegate* unmanaged<int, int, void> BindTexture, BindBuffer;
    public static delegate* unmanaged<int, int, void> BindSampler;
    public static delegate* unmanaged<void> Finish;
    public static void Load()
    {
        BindSampler = (delegate* unmanaged<int, int, void>)P("glBindSampler");
        Finish = (delegate* unmanaged<void>)P("glFinish");
        Enable = (delegate* unmanaged<int, void>)P("glEnable"); Disable = (delegate* unmanaged<int, void>)P("glDisable");
        UseProgram = (delegate* unmanaged<int, void>)P("glUseProgram"); ActiveTexture = (delegate* unmanaged<int, void>)P("glActiveTexture");
        BindVertexArray = (delegate* unmanaged<int, void>)P("glBindVertexArray");
        BindTexture = (delegate* unmanaged<int, int, void>)P("glBindTexture"); BindBuffer = (delegate* unmanaged<int, int, void>)P("glBindBuffer");
        GenFramebuffers = (delegate* unmanaged<int, int*, void>)P("glGenFramebuffers");
        BindFramebuffer = (delegate* unmanaged<int, int, void>)P("glBindFramebuffer");
        FramebufferTexture2D = (delegate* unmanaged<int, int, int, int, int, void>)P("glFramebufferTexture2D");
        CheckFramebufferStatus = (delegate* unmanaged<int, int>)P("glCheckFramebufferStatus");
        GetIntegerv = (delegate* unmanaged<int, int*, void>)P("glGetIntegerv");
        Viewport = (delegate* unmanaged<int, int, int, int, void>)P("glViewport");
    }
    public const int FRAMEBUFFER = 0x8D40, COLOR_ATTACHMENT0 = 0x8CE0, TEXTURE_2D = 0x0DE1, FRAMEBUFFER_COMPLETE = 0x8CD5,
        FRAMEBUFFER_BINDING = 0x8CA6, VIEWPORT = 0x0BA2;
}

public static unsafe class Egl
{
    const string L = "libEGL.so.1";
    [DllImport(L)] public static extern IntPtr eglGetCurrentDisplay();
    [DllImport(L)] public static extern IntPtr eglGetCurrentContext();
    [DllImport(L)] public static extern int eglQueryContext(IntPtr dpy, IntPtr ctx, int attr, int* val);
    [DllImport(L)] public static extern int eglChooseConfig(IntPtr dpy, int* attribs, IntPtr* configs, int size, int* num);
    [DllImport(L)] public static extern int eglBindAPI(int api);
    [DllImport(L)] public static extern int eglQueryAPI();
    [DllImport(L)] public static extern IntPtr eglCreateContext(IntPtr dpy, IntPtr cfg, IntPtr share, int* attribs);
    [DllImport(L)] public static extern int eglMakeCurrent(IntPtr dpy, IntPtr draw, IntPtr read, IntPtr ctx);
    [DllImport(L)] public static extern int eglGetError();
    public const int CONFIG_ID = 0x3028, NONE = 0x3038, OPENGL_ES_API = 0x30A0, OPENGL_API = 0x30A2, CONTEXT_CLIENT_VERSION = 0x3098, CONTEXT_MAJOR = 0x3098, CONTEXT_MINOR = 0x30FB;
}
