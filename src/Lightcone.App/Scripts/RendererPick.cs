using Godot;
using Lightcone.Host;

namespace Lightcone.App;

/// <summary>
/// Picks the Linux GL driver for this GPU. The project defaults to OpenGL ES over EGL (opengl3_es)
/// so libmpv can map VA-API frames straight into GL textures on AMD and Intel (Steam Deck). On
/// NVIDIA's proprietary driver that path runs the whole UI at a crawl, and NVIDIA has no VA-API
/// anyway, so there Lightcone switches to desktop OpenGL (opengl3, GLX on X11), the same driver
/// Luma uses. The choice is written to override.cfg next to the binary (Godot reads it before the
/// renderer starts; the updater carries it over) and the app restarts itself once.
/// Opt out with LIGHTCONE_GL=es; force with LIGHTCONE_GL=gl or --rendering-driver.
/// </summary>
public static class RendererPick
{
    private const string Relaunched = "LIGHTCONE_GL_RELAUNCHED";

    public static string Describe() =>
        $"{RenderingServer.GetVideoAdapterVendor()} {RenderingServer.GetVideoAdapterName()} ({RenderingServer.GetVideoAdapterApiVersion()})";

    /// <summary>True when the app is restarting on another driver: quit right away.</summary>
    public static bool RestartIfNeeded()
    {
        try
        {
            return Check();
        }
        catch (Exception e)
        {
            AppLog.Warn("renderer pick: " + e.Message);
            return false;
        }
    }

    private static bool Check()
    {
        if (!OperatingSystem.IsLinux() || RenderingServer.GetCurrentRenderingDriverName() != "opengl3_es")
        {
            return false;
        }
        var pref = OS.GetEnvironment("LIGHTCONE_GL").ToLowerInvariant();
        var args = OS.GetCmdlineArgs();
        if (pref == "es" || args.Contains("--rendering-driver") || OS.GetEnvironment(Relaunched) == "1"
            || args.Contains("--smoke") || args.Contains("--headless"))
        {
            return false;
        }
        var vendor = RenderingServer.GetVideoAdapterVendor();
        var adapter = RenderingServer.GetVideoAdapterName();
        var nvidiaDriver = File.Exists("/proc/driver/nvidia/version");
        var onNvidia = vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || adapter.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        // EGL that ends up on the CPU (llvmpipe) next to NVIDIA's driver is the same problem, worse.
        var software = adapter.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) || adapter.Contains("softpipe", StringComparison.OrdinalIgnoreCase);
        if (pref != "gl" && !onNvidia && !(software && nvidiaDriver))
        {
            return false;
        }
        AppLog.Info($"renderer: OpenGL ES runs slowly on {Describe()}, restarting on desktop OpenGL");
        var dir = Path.GetDirectoryName(OS.GetExecutablePath())!;
        var extra = Array.Empty<string>();
        try
        {
            var cfg = Path.Combine(dir, "override.cfg");
            var text = File.Exists(cfg) ? File.ReadAllText(cfg) : "";
            if (!text.Contains("driver.linuxbsd"))
            {
                File.WriteAllText(cfg, text + (text.Length > 0 && !text.EndsWith('\n') ? "\n" : "")
                    + "[rendering]\n\n; Written by Lightcone for this GPU (NVIDIA): desktop OpenGL instead of OpenGL ES.\n"
                    + "gl_compatibility/driver.linuxbsd=\"opengl3\"\n");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Read-only install: ask for the driver on the command line instead (every start).
            extra = ["--rendering-driver", "opengl3"];
        }
        OS.SetEnvironment(Relaunched, "1");
        var user = OS.GetCmdlineUserArgs();
        string[] relaunch = user.Length > 0 ? [.. extra, .. args, "--", .. user] : [.. extra, .. args];
        var pid = OS.CreateProcess(OS.GetExecutablePath(), relaunch);
        if (pid <= 0)
        {
            AppLog.Warn("renderer: restart failed, staying on OpenGL ES");
            return false;
        }
        return true;
    }
}
