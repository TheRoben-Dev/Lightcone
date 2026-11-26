namespace Lightcone.Core;

/// <summary>
/// Lightcone's own directories. Linux: XDG (~/.config/lightcone, ~/.local/share/lightcone, ~/.cache/lightcone).
/// Windows: %APPDATA%\Lightcone (settings) and %LOCALAPPDATA%\Lightcone (data, cache).
/// macOS: ~/Library/Application Support/Lightcone and ~/Library/Caches/Lightcone.
/// Media files are only ever read where they are; nothing is written next to them.
/// </summary>
public sealed class AppPaths
{
    public const string Name = "lightcone";
    public const string DisplayName = "Lightcone";

    public string Data { get; }
    public string Config { get; }
    public string Cache { get; }
    public string Runtime { get; }
    public string Home { get; }

    /// <param name="env">Environment variables (tests pass their own).</param>
    /// <param name="os">"linux", "windows" or "macos"; null = the OS Lightcone runs on.</param>
    public AppPaths(Func<string, string?>? env = null, string? os = null)
    {
        env ??= System.Environment.GetEnvironmentVariable;
        os ??= Platform.IsWindows ? "windows" : Platform.IsMacOS ? "macos" : "linux";
        if (os == "windows")
        {
            Home = NonEmpty(env("USERPROFILE")) ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            var local = Path.Combine(NonEmpty(env("LOCALAPPDATA")) ?? Path.Combine(Home, "AppData", "Local"), DisplayName);
            Data = local;
            Config = Path.Combine(NonEmpty(env("APPDATA")) ?? Path.Combine(Home, "AppData", "Roaming"), DisplayName);
            Cache = Path.Combine(local, "cache");
            Runtime = Path.Combine(local, "run");
            return;
        }
        Home = NonEmpty(env("HOME")) ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (os == "macos")
        {
            var support = Path.Combine(Home, "Library", "Application Support", DisplayName);
            Data = support;
            Config = support;
            Cache = Path.Combine(Home, "Library", "Caches", DisplayName);
            Runtime = Path.Combine(support, "run");
            return;
        }
        Data = Path.Combine(Xdg(env, "XDG_DATA_HOME", Home, ".local/share"), Name);
        Config = Path.Combine(Xdg(env, "XDG_CONFIG_HOME", Home, ".config"), Name);
        Cache = Path.Combine(Xdg(env, "XDG_CACHE_HOME", Home, ".cache"), Name);
        var runtime = env("XDG_RUNTIME_DIR");
        Runtime = string.IsNullOrEmpty(runtime) ? Path.Combine(Path.GetTempPath(), Name + "-" + System.Environment.UserName) : Path.Combine(runtime, Name);
    }

    public string SettingsFile => Path.Combine(Config, "settings.json");

    /// <summary>Media sources (folders, Jellyfin and Plex servers) the user added.</summary>
    public string SourcesFile => Path.Combine(Config, "sources.json");

    /// <summary>Local library index and watch state (SQLite, from the library phase on).</summary>
    public string LibraryDb => Path.Combine(Data, "library.db");

    /// <summary>Resume positions for items without a server to sync to.</summary>
    public string ProgressFile => Path.Combine(Data, "progress.json");

    /// <summary>Posters, backdrops and album art fetched for the UI.</summary>
    public string ArtCacheDir => Path.Combine(Cache, "art");

    public string LogsDir => Path.Combine(Data, "logs");

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static string Xdg(Func<string, string?> env, string name, string home, string fallback)
    {
        var v = env(name);
        return string.IsNullOrEmpty(v) ? Path.Combine(home, fallback) : v;
    }
}
