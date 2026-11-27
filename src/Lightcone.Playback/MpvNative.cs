using System.Runtime.InteropServices;

namespace Lightcone.Playback;

[StructLayout(LayoutKind.Sequential)]
internal struct MpvRenderParam
{
    public int Type;
    public nint Data;

    public MpvRenderParam(int type, nint data)
    {
        Type = type;
        Data = data;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvOpenGlFbo
{
    public int Fbo;
    public int W;
    public int H;
    public int InternalFormat;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvOpenGlInitParams
{
    public delegate* unmanaged[Cdecl]<nint, nint, nint> GetProcAddress;
    public nint Ctx;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventRaw
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public nint Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventPropertyRaw
{
    public nint Name;
    public int Format;
    public nint Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFileRaw
{
    public int Reason;
    public int Error;
}

/// <summary>The mpv events Lightcone reacts to (values from mpv/client.h).</summary>
public enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    Idle = 11,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
}

/// <summary>Raw libmpv client and render API (libmpv 2.x, client API 2.0 and newer).</summary>
internal static unsafe class MpvNative
{
    public const string Lib = "mpv";

    public const int FormatNone = 0, FormatString = 1, FormatFlag = 3, FormatInt64 = 4, FormatDouble = 5;

    // mpv_render_param_type
    public const int RenderApiType = 1, RenderOpenGlInitParams = 2, RenderOpenGlFbo = 3, RenderFlipY = 4, RenderBlockForTargetTime = 12,
        RenderSwSize = 17, RenderSwFormat = 18, RenderSwStride = 19, RenderSwPointer = 20;

    [DllImport(Lib)] public static extern ulong mpv_client_api_version();
    [DllImport(Lib)] public static extern nint mpv_create();
    [DllImport(Lib)] public static extern int mpv_initialize(nint ctx);
    [DllImport(Lib)] public static extern void mpv_terminate_destroy(nint ctx);
    [DllImport(Lib)] public static extern int mpv_set_option_string(nint ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);
    [DllImport(Lib)] public static extern int mpv_set_property_string(nint ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);
    [DllImport(Lib)] public static extern nint mpv_get_property_string(nint ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] public static extern int mpv_get_property(nint ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, void* data);
    [DllImport(Lib)] public static extern int mpv_observe_property(nint ctx, ulong id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);
    [DllImport(Lib)] public static extern int mpv_command(nint ctx, nint* args);
    [DllImport(Lib)] public static extern void mpv_free(nint p);
    [DllImport(Lib)] public static extern MpvEventRaw* mpv_wait_event(nint ctx, double timeout);
    [DllImport(Lib)] public static extern void mpv_wakeup(nint ctx);
    [DllImport(Lib)] public static extern nint mpv_error_string(int error);
    [DllImport(Lib)] public static extern int mpv_render_context_create(out nint res, nint mpv, MpvRenderParam* p);
    [DllImport(Lib)] public static extern int mpv_render_context_render(nint ctx, MpvRenderParam* p);
    [DllImport(Lib)] public static extern ulong mpv_render_context_update(nint ctx);
    [DllImport(Lib)] public static extern void mpv_render_context_report_swap(nint ctx);
    [DllImport(Lib)] public static extern void mpv_render_context_set_update_callback(nint ctx, delegate* unmanaged[Cdecl]<nint, void> cb, nint data);
    [DllImport(Lib)] public static extern void mpv_render_context_free(nint ctx);

    public static string ErrorText(int error) => Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? $"error {error}";

    public static void Check(int result, string what)
    {
        if (result < 0)
        {
            throw new MpvException($"{what}: {ErrorText(result)}", result);
        }
    }
}

public sealed class MpvException(string message, int code) : Exception(message)
{
    public int Code { get; } = code;
}
