using Xunit;

namespace Lightcone.Core.Tests;

/// <summary>A test of Linux behaviour (XDG folders, Steam's Linux layout, Flatpak): skipped elsewhere.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux only";
        }
    }
}

/// <summary>A test of macOS behaviour (Library folders, Steam in Application Support): skipped elsewhere.</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "macOS only";
        }
    }
}

/// <summary>A test of Windows behaviour (registry, known folders): skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only";
        }
    }
}
