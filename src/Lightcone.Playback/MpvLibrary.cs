using System.Reflection;
using System.Runtime.InteropServices;

namespace Lightcone.Playback;

/// <summary>
/// Finds and loads libmpv. Release builds bundle it (lib/ next to the app on Linux and Windows,
/// Contents/Frameworks in the macOS bundle), so the app never depends on what the system has;
/// development builds fall back to the system's libmpv. LIGHTCONE_LIBMPV overrides both.
/// </summary>
public static class MpvLibrary
{
    private static readonly object Gate = new();
    private static bool _resolverSet;
    private static nint _handle;
    private static string? _error;

    /// <summary>The file libmpv was loaded from, once loaded.</summary>
    public static string? LoadedFrom { get; private set; }

    /// <summary>libmpv's client API version, "2.1" etc.; null until loaded.</summary>
    public static string? ApiVersion { get; private set; }

    /// <summary>File names libmpv has on an OS, most specific first.</summary>
    internal static IReadOnlyList<string> FileNames(OSPlatform os) =>
        os == OSPlatform.Windows ? ["libmpv-2.dll", "mpv-2.dll", "mpv-1.dll"]
        : os == OSPlatform.OSX ? ["libmpv.2.dylib", "libmpv.dylib"]
        : ["libmpv.so.2", "libmpv.so"];

    /// <summary>Folders searched before the system's library path, in order.</summary>
    internal static IReadOnlyList<string> BundleDirs(string baseDir, OSPlatform os)
    {
        var dirs = new List<string> { Path.Combine(baseDir, "lib"), baseDir };
        if (os == OSPlatform.OSX)
        {
            // Contents/MacOS/<binary> -> Contents/Frameworks
            dirs.Insert(0, Path.GetFullPath(Path.Combine(baseDir, "..", "Frameworks")));
        }
        return dirs;
    }

    /// <summary>Every path tried, in order: the override, the bundle, then bare names for the system loader.</summary>
    internal static IEnumerable<string> Candidates(string baseDir, OSPlatform os, string? overridePath)
    {
        if (!string.IsNullOrEmpty(overridePath))
        {
            yield return overridePath;
        }
        foreach (var dir in BundleDirs(baseDir, os))
        {
            foreach (var name in FileNames(os))
            {
                yield return Path.Combine(dir, name);
            }
        }
        foreach (var name in FileNames(os))
        {
            yield return name;
        }
        if (os == OSPlatform.OSX)
        {
            // Homebrew, for development on a Mac.
            yield return "/opt/homebrew/lib/libmpv.2.dylib";
            yield return "/usr/local/lib/libmpv.2.dylib";
        }
    }

    private static OSPlatform CurrentOs =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;

    /// <summary>Loads libmpv once. False (with a reason) when no usable libmpv is found.</summary>
    public static bool TryLoad(out string? error)
    {
        lock (Gate)
        {
            if (!_resolverSet)
            {
                _resolverSet = true;
                NativeLibrary.SetDllImportResolver(typeof(MpvLibrary).Assembly, Resolve);
            }
            if (_handle == 0 && _error == null)
            {
                Load();
            }
            error = _error;
            return _handle != 0;
        }
    }

    public static bool Available => TryLoad(out _);

    private static nint Resolve(string name, Assembly asm, DllImportSearchPath? path) =>
        name == MpvNative.Lib && TryLoad(out _) ? _handle : 0;

    private static void Load()
    {
        var tried = new List<string>();
        foreach (var c in Candidates(AppContext.BaseDirectory, CurrentOs, Environment.GetEnvironmentVariable("LIGHTCONE_LIBMPV")))
        {
            if (Path.IsPathRooted(c) && !File.Exists(c))
            {
                continue;
            }
            tried.Add(c);
            if (NativeLibrary.TryLoad(c, out var h))
            {
                if (!NativeLibrary.TryGetExport(h, "mpv_render_context_create", out _))
                {
                    continue;  // too old: no render API
                }
                _handle = h;
                LoadedFrom = c;
                var v = MpvNative.mpv_client_api_version();
                ApiVersion = $"{v >> 16}.{v & 0xffff}";
                if (v >> 16 < 2)
                {
                    _handle = 0;
                    _error = $"libmpv at {c} is too old (client API {ApiVersion}, need 2.0 or newer)";
                }
                return;
            }
        }
        _error = $"libmpv not found (tried {string.Join(", ", tried)})";
    }
}
