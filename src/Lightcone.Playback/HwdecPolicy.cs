namespace Lightcone.Playback;

/// <summary>
/// Which mpv hwdec mode to use. The user setting is "auto" (default), "copy" (GPU decode, frames
/// copied back: safest), or "off". "auto" picks the zero-copy path where our GL context can do
/// interop: VAAPI with an EGL context on Linux (Steam Deck's AMD APU), VideoToolbox on macOS
/// (CGL interop). On Windows we draw through OpenGL, which can't share D3D11 frames reliably, so
/// "auto" means the copy path there.
/// </summary>
public static class HwdecPolicy
{
    public const string Auto = "auto", Copy = "copy", Off = "off";

    public static readonly (string Id, string Label)[] Choices =
    [
        (Auto, "Automatic"),
        (Copy, "Hardware, copy-back"),
        (Off, "Software only"),
    ];

    /// <param name="setting">The user's setting (null = auto).</param>
    /// <param name="os">"linux", "windows" or "macos".</param>
    /// <param name="egl">True when the GL context is EGL (Godot's opengl3_es driver, or Wayland).</param>
    public static string Resolve(string? setting, string os, bool egl) => (setting ?? Auto) switch
    {
        Off => "no",
        Copy => os == "macos" ? "videotoolbox-copy" : "auto-copy-safe",
        _ => os switch
        {
            "linux" => egl ? "vaapi,auto-copy-safe" : "vaapi-copy,auto-copy-safe",
            "macos" => "videotoolbox,auto-copy-safe",
            _ => "auto-copy-safe",
        },
    };

    public static string Describe(string? setting) => (setting ?? Auto) switch
    {
        Off => "Decode on the CPU. Uses more power; only for files the GPU decodes wrongly.",
        Copy => "Decode on the GPU and copy frames back. Works everywhere, uses a little more power than Automatic.",
        _ => "The fastest path this device supports (VAAPI on Steam Deck and Linux, VideoToolbox on Mac). Falls back by itself.",
    };
}
