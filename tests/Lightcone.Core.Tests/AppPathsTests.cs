using Xunit;

namespace Lightcone.Core.Tests;

public class AppPathsTests
{
    // Simulated Linux / macOS paths are built with the host's separator; compare them with '/'.
    private static string N(string path) => path.Replace('\\', '/');

    private static Func<string, string?> Env(params (string K, string V)[] vars) =>
        k => vars.FirstOrDefault(v => v.K == k).V;

    [Fact]
    public void LinuxUsesXdgFolders()
    {
        var p = new AppPaths(Env(("HOME", "/home/deck"), ("XDG_CONFIG_HOME", "/cfg")), "linux");
        Assert.Equal("/cfg/lightcone", N(p.Config));
        Assert.Equal("/home/deck/.local/share/lightcone", N(p.Data));
        Assert.Equal("/home/deck/.cache/lightcone", N(p.Cache));
        Assert.Equal("/cfg/lightcone/settings.json", N(p.SettingsFile));
    }

    [Fact]
    public void MacUsesLibraryFolders()
    {
        var p = new AppPaths(Env(("HOME", "/Users/roben")), "macos");
        Assert.Equal("/Users/roben/Library/Application Support/Lightcone", N(p.Config));
        Assert.Equal("/Users/roben/Library/Application Support/Lightcone", N(p.Data));
        Assert.Equal("/Users/roben/Library/Caches/Lightcone", N(p.Cache));
    }

    [WindowsFact]
    public void WindowsUsesAppData()
    {
        var p = new AppPaths(Env(("USERPROFILE", @"C:\Users\r"), ("APPDATA", @"C:\Users\r\AppData\Roaming"), ("LOCALAPPDATA", @"C:\Users\r\AppData\Local")), "windows");
        Assert.Equal(@"C:\Users\r\AppData\Roaming\Lightcone", p.Config);
        Assert.Equal(@"C:\Users\r\AppData\Local\Lightcone", p.Data);
    }
}
