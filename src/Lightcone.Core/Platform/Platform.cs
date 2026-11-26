using System.Diagnostics;

namespace Lightcone.Core;

/// <summary>
/// The operating system Lightcone runs on, and the few things that differ by it: binary names, how
/// paths compare, whether a process is alive. Linux (SteamOS, Bazzite...), Windows and macOS are supported.
/// Platform-specific code lives behind this and in the Windows/ folders; callers stay the same.
/// </summary>
public static class Platform
{
    public static bool IsWindows { get; } = OperatingSystem.IsWindows();

    public static bool IsMacOS { get; } = OperatingSystem.IsMacOS();

    public static bool IsLinux { get; } = OperatingSystem.IsLinux();

    /// <summary>"Windows", "macOS" or "Linux", for messages.</summary>
    public static string Name => IsWindows ? "Windows" : IsMacOS ? "macOS" : "Linux";

    /// <summary>How file paths compare here: without case on Windows, exactly on Linux.</summary>
    /// <remarks>macOS volumes (APFS, HFS+) ignore case by default, like Windows.</remarks>
    public static StringComparer PathComparer => IsWindows || IsMacOS ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison PathComparison => IsWindows || IsMacOS ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The Godot app's executable in a release folder.</summary>
    public static string AppBinary => IsWindows ? "Lightcone.exe" : IsMacOS ? "Lightcone" : "Lightcone.x86_64";

    /// <summary>The command line helper (`lightcone`) next to it; lightcone-cli.exe on Windows, where lightcone.exe would clash with Lightcone.exe.</summary>
    public static string CliBinary => IsWindows ? "lightcone-cli.exe" : "lightcone";

    /// <summary>True while a process with this id is running.</summary>
    public static bool ProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }
        if (IsLinux)
        {
            return Directory.Exists($"/proc/{pid}");
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
