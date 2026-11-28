using Godot;
using Lightcone.Host;

namespace Lightcone.App;

/// <summary>
/// Makes a crash show up in app.log, which is the one file people send. A native crash (signal 11
/// and the like) never reaches .NET, so it's only in Godot's own log, which Godot rotates away on
/// the next start (user://logs/godot.log becomes godot&lt;date&gt;.log). On start-up this notes when
/// the last run ended without quitting (its pid file is still there) and copies the crash lines
/// from the most recent rotated Godot log into app.log, once.
/// </summary>
public static class CrashReport
{
    private const int MaxLines = 60;

    public static void CheckPrevious(string pidFile, string stampDir)
    {
        try
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid)
                && pid != OS.GetProcessId() && !Running(pid))
            {
                AppLog.Warn("the last run ended without quitting (crash, freeze or forced close); see the lines above");
            }
            CarryOverGodotCrash(stampDir);
        }
        catch (Exception e)
        {
            AppLog.Warn("crash check: " + e.Message);
        }
    }

    private static bool Running(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.Contains("ightcone", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void CarryOverGodotCrash(string stampDir)
    {
        var dir = Path.Combine(OS.GetUserDataDir(), "logs");
        if (!Directory.Exists(dir))
        {
            return;
        }
        var stamp = Path.Combine(stampDir, "last-crash-reported");
        var reported = File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : "";
        // Rotated logs only (the current run writes godot.log), newest first; the renderer switch
        // restarts once, so the crashed run isn't always the newest one.
        var logs = new DirectoryInfo(dir).GetFiles("godot*.log")
            .Where(f => f.Name != "godot.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(3);
        foreach (var log in logs)
        {
            if (log.Name == reported)
            {
                return;
            }
            var lines = File.ReadAllLines(log.FullName);
            var at = Array.FindIndex(lines, l => l.Contains("Program crashed", StringComparison.Ordinal)
                || l.Contains("crashed with signal", StringComparison.Ordinal) || l.Contains("Unhandled exception", StringComparison.Ordinal));
            if (at < 0)
            {
                continue;
            }
            var from = Math.Max(0, at - 8);
            AppLog.Error($"previous run crashed ({log.Name}):\n    " + string.Join("\n    ", lines.Skip(from).Take(MaxLines)));
            Directory.CreateDirectory(stampDir);
            File.WriteAllText(stamp, log.Name);
            return;
        }
    }
}
