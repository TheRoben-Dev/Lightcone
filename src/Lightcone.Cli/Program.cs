using Lightcone.Host;
using Lightcone.Host.Updates;
using Lightcone.Playback;

// Headless entry point (`lightcone`): used by the installers (Steam shortcut) and for support
// (version, settings, whether libmpv loads on this machine).

return Cli.Run(args);

internal static class Cli
{
    private const string Usage = """
        Lightcone command line

          lightcone version                 show the installed version
          lightcone settings                print the settings file path and contents
          lightcone probe                   check that the bundled libmpv loads, and print its version
          lightcone steam-add [--restart]   add Lightcone to Steam with its art (--restart closes and
                                            restarts Steam around it)
        """;

    /// <summary>The install folder: the folder of this binary, or on macOS the .app bundle around it.</summary>
    private static string InstallDir()
    {
        var dir = AppContext.BaseDirectory.TrimEnd('/', '\\');
        if (OperatingSystem.IsMacOS() && dir.EndsWith("/Contents/MacOS", StringComparison.Ordinal))
        {
            dir = dir[..^"/Contents/MacOS".Length];
        }
        return dir;
    }

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            switch (args[0])
            {
                case "version":
                    Console.WriteLine(AppUpdater.DisplayVersion(AppUpdater.CurrentAppVersion()));
                    return 0;
                case "settings":
                {
                    var host = new AppHost();
                    Console.WriteLine(host.Paths.SettingsFile);
                    Console.WriteLine(File.Exists(host.Paths.SettingsFile) ? File.ReadAllText(host.Paths.SettingsFile) : "(defaults)");
                    return 0;
                }
                case "probe":
                {
                    if (!MpvLibrary.TryLoad(out var error))
                    {
                        Console.Error.WriteLine($"libmpv: not available ({error})");
                        return 1;
                    }
                    Console.WriteLine($"libmpv: {MpvLibrary.LoadedFrom} (client API {MpvLibrary.ApiVersion})");
                    using var probe = new MpvPlayer(new Dictionary<string, string> { ["vo"] = "null", ["ao"] = "null" });
                    Console.WriteLine($"mpv:    {probe.GetString("mpv-version")}");
                    Console.WriteLine($"ffmpeg: {probe.GetString("ffmpeg-version")}");
                    Console.WriteLine($"hwdec:  {probe.GetString("hwdec-interop") ?? "(decided when a video opens)"}");
                    var missing = Codecs.Missing(probe.GetString("decoder-list"));
                    foreach (var (group, codecs) in Codecs.Required)
                    {
                        Console.WriteLine($"{group + ":",-11}{string.Join(' ', codecs.Select(c => missing.Contains(c) ? "-" + c : c))}");
                    }
                    if (missing.Count > 0)
                    {
                        Console.Error.WriteLine($"missing decoders: {string.Join(", ", missing)}");
                        return 1;
                    }
                    return 0;
                }
                case "steam-add":
                {
                    var host = new AppHost();
                    var restart = args.Contains("--restart") && Lightcone.Core.Steam.SteamShortcuts.SteamRunning(host.Paths.Home);
                    if (restart && !SteamSetup.CloseSteam(host.Paths.Home, TimeSpan.FromSeconds(30)))
                    {
                        Console.Error.WriteLine("error: Steam didn't close. Exit it yourself, then run this again.");
                        return 1;
                    }
                    var n = SteamSetup.Add(host.Paths.Home, InstallDir());
                    Console.WriteLine($"Lightcone added to Steam ({n} account{(n == 1 ? "" : "s")}).");
                    if (restart)
                    {
                        SteamSetup.StartSteam();
                    }
                    return 0;
                }
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }
}
