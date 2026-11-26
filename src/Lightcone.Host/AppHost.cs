using System.Text.Json;
using System.Text.Json.Serialization;
using Lightcone.Core;
using Lightcone.Host.Library;
using Lightcone.Host.Updates;

namespace Lightcone.Host;

/// <summary>Lightcone's own app settings (settings.json).</summary>
public sealed record AppSettings
{
    /// <summary>UI theme id (see the app's theme list); unknown ids fall back to the default.</summary>
    public string Theme { get; init; } = "dark";

    /// <summary>Accent colour per theme id (a swatch id from the app's list); missing = the theme's own.</summary>
    public Dictionary<string, string> Accents { get; init; } = new();

    /// <summary>UI mode: "auto" (Controller in Game Mode / Big Picture, Desktop elsewhere), "controller" or "desktop".</summary>
    public string Layout { get; init; } = "auto";

    /// <summary>Desktop mode library view: "grid" or "list".</summary>
    public string DesktopView { get; init; } = "grid";

    /// <summary>Turns off UI animations.</summary>
    public bool ReduceMotion { get; init; }

    /// <summary>CRT scanlines over the UI in any theme.</summary>
    public bool Crt { get; init; }

    /// <summary>CRT effect strength when <see cref="Crt"/> is on: 1 subtle, 2 medium, 3 strong.</summary>
    public int CrtLevel { get; init; } = 2;

    /// <summary>The theme's slow animated background behind the menus.</summary>
    public bool AnimatedBackground { get; init; } = true;

    /// <summary>Menu sounds, 0 (off) to 100.</summary>
    public int SoundVolume { get; init; } = 40;

    /// <summary>A light rumble on the pad for menu moves, presses and errors.</summary>
    public bool Haptics { get; init; } = true;

    /// <summary>Show the built-in sample library (until a real source is added, and for trying the app out).</summary>
    public bool DemoLibrary { get; init; } = true;

    /// <summary>Hardware decoding: "auto", "copy" or "off" (see Lightcone.Playback.HwdecPolicy).</summary>
    public string Hwdec { get; init; } = "auto";

    /// <summary>Preferred audio languages, in order (ISO 639 codes).</summary>
    public List<string> AudioLanguages { get; init; } = ["en"];

    /// <summary>Preferred subtitle languages, in order; empty = subtitles off unless forced.</summary>
    public List<string> SubtitleLanguages { get; init; } = ["en"];

    /// <summary>Subtitle size relative to the default, in percent.</summary>
    public int SubtitleScale { get; init; } = 100;

    /// <summary>Seconds a back press (left trigger, Left, J) jumps. Held presses go faster.</summary>
    public int SeekBack { get; init; } = 10;

    /// <summary>Seconds a forward press (right trigger, Right, L) jumps. Held presses go faster.</summary>
    public int SeekForward { get; init; } = 30;

    /// <summary>Update channel: "stable" (releases) or "test" (test builds too); null = test for a test build, else stable.</summary>
    public string? UpdateChannel { get; init; }

    /// <summary>Check GitHub Releases for a new Lightcone version at startup.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Last app version whose "What's new" was shown; null on a fresh install.</summary>
    public string? LastSeenVersion { get; init; }
}

/// <summary>
/// Composition root shared by the Godot UI and the headless CLI (`lightcone`). Everything that
/// isn't drawing lives behind this so it can be unit tested without Godot.
/// </summary>
public sealed class AppHost
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AppPaths Paths { get; }
    public AppSettings Settings { get; private set; }
    public AppUpdater Updater { get; }
    public MediaLibrary Library { get; }

    /// <summary>The Plex account and its servers (sources in <see cref="Library"/>).</summary>
    public Plex.PlexService Plex { get; }

    /// <summary>Raised after settings are saved (the app re-applies what changed).</summary>
    public event Action<AppSettings>? SettingsChanged;

    public AppHost(AppPaths? paths = null, HttpClient? http = null)
    {
        Paths = paths ?? new AppPaths();
        Directory.CreateDirectory(Paths.Config);
        Directory.CreateDirectory(Paths.Data);
        Settings = LoadJson<AppSettings>(Paths.SettingsFile) ?? new AppSettings();
        Updater = new AppUpdater(http);
        if (Settings.UpdateChannel is { } channel)
        {
            Updater.TestChannel = channel == "test";
        }
        Library = new MediaLibrary(new ProgressStore(Paths.ProgressFile));
        ApplyDemo(Settings.DemoLibrary);
        Plex = new Plex.PlexService(Library, Paths.Config, Paths.Data, http);
    }

    /// <summary>Adds or removes the sample library.</summary>
    public void ApplyDemo(bool on)
    {
        if (on)
        {
            Library.Add(new DemoSource());
        }
        else
        {
            Library.Remove(DemoSource.SourceId);
        }
    }

    /// <summary>The app's pid file (one running instance).</summary>
    public string PidFile => Path.Combine(Paths.Runtime, "lightcone-app.pid");

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        SaveJson(Paths.SettingsFile, settings);
        SettingsChanged?.Invoke(settings);
    }

    internal static T? LoadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
