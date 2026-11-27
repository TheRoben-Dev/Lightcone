using System.Diagnostics;
using Godot;

namespace Lightcone.App.Playback;

/// <summary>
/// Keeps the screen on and the system awake while a video is actually playing (not paused, not
/// stopped), and lets go again otherwise. Godot's keep-screen-on covers each desktop's own idle
/// lock: org.freedesktop.ScreenSaver on Linux, SetThreadExecutionState on Windows, an IOPM
/// assertion on macOS. On Linux a systemd-inhibit idle/sleep lock is held as well, which SteamOS
/// Game Mode (where the dimming and screen-off come from Steam, not a desktop) respects.
/// </summary>
public static class IdleInhibit
{
    private static bool _on;
    private static Process? _systemd;

    /// <summary>Called every frame with whether a video is playing right now.</summary>
    public static void Update(bool playing)
    {
        if (playing == _on)
        {
            return;
        }
        _on = playing;
        DisplayServer.ScreenSetKeepOn(playing);
        if (OperatingSystem.IsLinux())
        {
            if (playing)
            {
                StartSystemd();
            }
            else
            {
                StopSystemd();
            }
        }
        Lightcone.Host.AppLog.Info(playing ? "idle inhibit on (video playing)" : "idle inhibit off");
    }

    private static void StartSystemd()
    {
        StopSystemd();
        try
        {
            var psi = new ProcessStartInfo("systemd-inhibit")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "--what=idle:sleep", "--who=Lightcone", "--why=Playing a video", "--mode=block", "sleep", "infinity" })
            {
                psi.ArgumentList.Add(a);
            }
            _systemd = Process.Start(psi);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _systemd = null;  // no systemd (Flatpak sandbox, other init): the D-Bus lock still holds
        }
    }

    private static void StopSystemd()
    {
        try
        {
            if (_systemd is { HasExited: false })
            {
                _systemd.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        _systemd?.Dispose();
        _systemd = null;
    }

    /// <summary>On quit: never leave a lock behind.</summary>
    public static void Release() => Update(false);
}
