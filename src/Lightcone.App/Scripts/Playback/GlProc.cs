using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;

namespace Lightcone.App.Playback;

/// <summary>
/// OpenGL function lookup for the context Godot's Compatibility renderer runs, which is what
/// mpv's render API draws with: GLX (Linux X11, opengl3), EGL (opengl3_es, or any Wayland
/// session), WGL (Windows) or CGL (macOS).
/// </summary>
public static unsafe class GlProc
{
    public static bool UseEgl { get; private set; }
    private static nint _gl;

    public static string Api => OperatingSystem.IsWindows() ? "WGL" : OperatingSystem.IsMacOS() ? "CGL" : UseEgl ? "EGL" : "GLX";

    /// <summary>Picks the lookup for Godot's current driver. Call on the main thread before <see cref="Init"/>.</summary>
    public static void Detect() =>
        UseEgl = OperatingSystem.IsLinux() &&
                 (RenderingServer.GetCurrentRenderingDriverName() == "opengl3_es" || DisplayServer.GetName() == "Wayland");

    /// <summary>Loads the GL library where lookups need it. Call once, on the render thread.</summary>
    public static void Init()
    {
        if (OperatingSystem.IsMacOS())
        {
            _gl = NativeLibrary.Load("/System/Library/Frameworks/OpenGL.framework/OpenGL");
        }
        else if (OperatingSystem.IsWindows())
        {
            _gl = NativeLibrary.Load("opengl32.dll");
        }
    }

    [DllImport("libEGL.so.1")] private static extern nint eglGetProcAddress(nint name);
    [DllImport("libGL.so.1")] private static extern nint glXGetProcAddressARB(nint name);
    [DllImport("opengl32.dll")] private static extern nint wglGetProcAddress(nint name);

    /// <summary>For mpv: (ctx, name) -> function pointer.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static nint MpvGetProc(nint ctx, nint name) => Resolve(name);

    public static nint Resolve(nint name)
    {
        if (OperatingSystem.IsWindows())
        {
            var p = wglGetProcAddress(name);
            if (p is 0 or 1 or 2 or 3 or -1)
            {
                NativeLibrary.TryGetExport(_gl, Marshal.PtrToStringAnsi(name)!, out p);
            }
            return p;
        }
        if (OperatingSystem.IsMacOS())
        {
            return NativeLibrary.TryGetExport(_gl, Marshal.PtrToStringAnsi(name)!, out var p) ? p : 0;
        }
        return UseEgl ? eglGetProcAddress(name) : glXGetProcAddressARB(name);
    }

    public static nint Resolve(string name)
    {
        var n = Marshal.StringToHGlobalAnsi(name);
        try
        {
            return Resolve(n);
        }
        finally
        {
            Marshal.FreeHGlobal(n);
        }
    }
}

/// <summary>The few GL calls needed to point an FBO at a Godot texture.</summary>
public static unsafe class Gl
{
    public const uint Framebuffer = 0x8D40, ColorAttachment0 = 0x8CE0, Texture2D = 0x0DE1, FramebufferBinding = 0x8CA6, FramebufferComplete = 0x8CD5;

    private static delegate* unmanaged<uint, int*, void> _getIntegerv;
    private static delegate* unmanaged<int, uint*, void> _genFramebuffers;
    private static delegate* unmanaged<int, uint*, void> _deleteFramebuffers;
    private static delegate* unmanaged<uint, uint, void> _bindFramebuffer;
    private static delegate* unmanaged<uint, uint, uint, uint, int, void> _framebufferTexture2D;
    private static delegate* unmanaged<uint, uint> _checkFramebufferStatus;

    public static bool Loaded { get; private set; }

    public static void Load()
    {
        if (Loaded)
        {
            return;
        }
        _getIntegerv = (delegate* unmanaged<uint, int*, void>)GlProc.Resolve("glGetIntegerv");
        _genFramebuffers = (delegate* unmanaged<int, uint*, void>)GlProc.Resolve("glGenFramebuffers");
        _deleteFramebuffers = (delegate* unmanaged<int, uint*, void>)GlProc.Resolve("glDeleteFramebuffers");
        _bindFramebuffer = (delegate* unmanaged<uint, uint, void>)GlProc.Resolve("glBindFramebuffer");
        _framebufferTexture2D = (delegate* unmanaged<uint, uint, uint, uint, int, void>)GlProc.Resolve("glFramebufferTexture2D");
        _checkFramebufferStatus = (delegate* unmanaged<uint, uint>)GlProc.Resolve("glCheckFramebufferStatus");
        Loaded = _getIntegerv != null && _genFramebuffers != null && _bindFramebuffer != null && _framebufferTexture2D != null;
    }

    public static int BoundFramebuffer()
    {
        int v;
        _getIntegerv(FramebufferBinding, &v);
        return v;
    }

    public static void Bind(int fbo) => _bindFramebuffer(Framebuffer, (uint)fbo);

    /// <summary>Creates an FBO drawing into GL texture <paramref name="texture"/>. Returns 0 if it isn't complete.</summary>
    public static int FramebufferFor(ulong texture)
    {
        var prev = BoundFramebuffer();
        uint fbo;
        _genFramebuffers(1, &fbo);
        _bindFramebuffer(Framebuffer, fbo);
        _framebufferTexture2D(Framebuffer, ColorAttachment0, Texture2D, (uint)texture, 0);
        var ok = _checkFramebufferStatus(Framebuffer) == FramebufferComplete;
        _bindFramebuffer(Framebuffer, (uint)prev);
        if (!ok)
        {
            _deleteFramebuffers(1, &fbo);
            return 0;
        }
        return (int)fbo;
    }

    public static void DeleteFramebuffer(int fbo)
    {
        if (fbo != 0 && _deleteFramebuffers != null)
        {
            var f = (uint)fbo;
            _deleteFramebuffers(1, &f);
        }
    }
}
