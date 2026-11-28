using Godot;
using Lightcone.App.Desktop;
using Lightcone.App.Playback;
using Lightcone.App.Screens;
using Lightcone.App.Ui;
using Lightcone.Host;
using Lightcone.Host.Library;
using Lightcone.Host.Updates;

namespace Lightcone.App;

/// <summary>
/// Root node: the screen stack, input routing and the chrome around it. The chrome depends on the
/// UI mode: Controller mode is a 10-foot layout (header with section tabs, hint bar, no cursor);
/// Desktop mode is a traditional window (sidebar, mouse, mini player). Both show the same
/// screens and features: everything either mode can do is reachable in the other.
/// All non-UI work goes through <see cref="AppHost"/>, the same code the CLI uses.
/// </summary>
public partial class Main : Control
{
    /// <summary>Sections, in order: bumpers / Tab cycle them in Controller mode, the sidebar lists them in Desktop mode.</summary>
    public static string[] TabNames => Instance?.Host?.Settings.MusicTab == false ? NoMusic : AllTabs;

    private static readonly string[] AllTabs = ["Home", "Movies", "Shows", "Music", "Sources", "Settings"];
    private static readonly string[] NoMusic = ["Home", "Movies", "Shows", "Sources", "Settings"];

    private static Screen NewTab(int index) => TabNames[index] switch
    {
        "Movies" => new LibraryScreen(MediaKind.Movie, "Movies"),
        "Shows" => new LibraryScreen(MediaKind.Show, "Shows"),
        "Music" => new LibraryScreen(MediaKind.Album, "Music"),
        "Sources" => new SourcesScreen(),
        "Settings" => new SettingsScreen(),
        _ => new HomeScreen(),
    };

    private readonly List<Screen> _stack = new();
    private Control _chrome = null!;
    private Control _content = null!;
    private MarginContainer _contentMargin = null!;
    private Control _overlay = null!;
    private Backdrop _backdrop = null!;
    private Header? _header;
    private HintBar? _hints;
    private Sidebar? _sidebar;
    private MiniPlayer? _mini;
    private Control _bgLayer = null!;
    private FocusHighlight _highlight = null!;
    private Toast _toast = null!;
    private PlayerService _player = null!;
    private int _tab;
    private bool _appFocused = true;
    private AnimatedBackground? _animBg;
    private Scanlines? _crt;
    private ulong _navAtMs;
    private AmbientMode _ambient = null!;
    private double _idle;
    private Feedback? _feedback;

    public static Main Instance { get; private set; } = null!;

    /// <summary>Null only if startup failed (shown on screen).</summary>
    public AppHost? Host { get; private set; }

    /// <summary>The root section shown now ("Home", "Movies"...).</summary>
    public string CurrentTab => TabNames[_tab];

    public Screen Top => _stack[^1];

    /// <summary>Seconds since the last button, key, click or stick move.</summary>
    public double IdleSeconds => _idle;

    /// <summary>The page on top, or null before the first one.</summary>
    public Screen? TopOrNull => _stack.Count > 0 ? _stack[^1] : null;

    /// <summary>True while a full-screen page (the player) is up: background scenes stop rendering.</summary>
    public bool FullScreenActive => _stack.Any(s => s.FullScreen && s.Visible);

    private void PauseBackground()
    {
        var paused = !_appFocused || _stack.Any(s => s.FullScreen);
        if (_animBg != null && IsInstanceValid(_animBg))
        {
            _animBg.Paused = paused;
        }
        if (_crt != null && IsInstanceValid(_crt))
        {
            _crt.Paused = paused;
        }
    }

    public override void _Ready()
    {
        Instance = this;
        Pad.Setup();
        Pad.FamilyChanged += UpdateChrome;
        AppHost? host = null;
        Exception? startupError = null;
        try
        {
            host = new AppHost();
        }
        catch (Exception ex)
        {
            startupError = ex;
        }
        if (host != null)
        {
            AppLog.Open(host.Paths.LogsDir);
        }
        if (startupError != null)
        {
            AppLog.Error("startup: " + startupError);
        }
        var args = OS.GetCmdlineUserArgs();
        if (host != null && args.Contains("--demo") && !host.Settings.DemoLibrary)
        {
            host.SaveSettings(host.Settings with { DemoLibrary = true });
            host.ApplyDemo(true);
        }
        var startTheme = host?.Settings.Theme ?? "lightcone";
        AppLog.Info($"{Lightcone.Core.Platform.Name}, theme {startTheme}, window {DisplayServer.WindowGetSize()}, driver {RenderingServer.GetCurrentRenderingDriverName()} on {DisplayServer.GetName()}, pads: {Pad.DescribePads()}");
        UiTheme.Use(startTheme, host?.Settings.Accents.GetValueOrDefault(startTheme));
        Anim.Enabled = !(host?.Settings.ReduceMotion ?? false);
        Loc.Apply(host?.Settings.Language ?? "auto");
        Pad.GlyphStyle = host?.Settings.GlyphStyle ?? "auto";
        var steamPads = Lightcone.Host.SteamPads.VirtualPads();
        AppLog.Info($"language {Loc.Current}, glyphs {Pad.GlyphStyle}{(steamPads.Count > 0 ? ", Steam Input pads: " + string.Join(", ", steamPads) : "")}");
        Theme = UiTheme.Build();
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Host = host;

        _bgLayer = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _bgLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_bgLayer);
        BuildBackground();
        _player = new PlayerService();
        AddChild(_player);
        AddChild(new PhoneRemote());

        _chrome = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _chrome.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_chrome);
        _content = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _overlay = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_overlay);
        _backdrop = new Backdrop();
        _backdrop.Clicked += () =>
        {
            if (_stack.Count > 1 && _stack[^1].IsOverlay)
            {
                Pop();
            }
        };
        _overlay.AddChild(_backdrop);
        _highlight = new FocusHighlight();
        AddChild(_highlight);
        _ambient = new AmbientMode();
        var ambientLayer = new CanvasLayer { Layer = 50 };
        ambientLayer.AddChild(_ambient);
        AddChild(ambientLayer);
        _toast = new Toast();
        AddChild(_toast);
        _feedback = new Feedback(UiTheme.Name)
        {
            Volume = host?.Settings.SoundVolume ?? 40,
            Haptics = host?.Settings.Haptics ?? true,
        };
        AddChild(_feedback);

        var windowed = args.Contains("--windowed");
        if (windowed)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        }
        UiLayout.Apply(GetWindow(), host?.Settings.Layout, windowed);
        BuildChrome();
        AppLog.Info("display: " + DisplayCheck.Describe());
        if (DisplayCheck.SteamResolutionTip() is { } tip)
        {
            AppLog.Warn(tip);  // also shown in Settings → Layout; no pop-up
        }

        GetViewport().GuiFocusChanged += c => _hints?.SetHelp(c.HasMeta(Stepper.ExpandsMeta) || c.HasMeta(PosterTile.NoHelp) ? "" : c.TooltipText.Split('\n')[0]);
        // The tile you're on gets its "Did you know?" fact looked up first, so its page has it ready.
        GetViewport().GuiFocusChanged += c =>
        {
            if (c is PosterTile { Item: var item } && Host?.Settings.Trivia == true)
            {
                Host.Trivia.Prefetch([item], soon: true);
            }
        };
        // Some hints depend on what has focus (an episode row offers "Episode watched").
        GetViewport().GuiFocusChanged += _ => CallDeferred(MethodName.UpdateChrome);
        GetViewport().GuiFocusChanged += _ =>
        {
            if (Time.GetTicksMsec() - _navAtMs < 250)
            {
                Feedback.Play(Cue.Move);
            }
        };

        if (startupError is { } e)
        {
            GD.PushError(e.ToString());
            var msg = UiTheme.Label($"Lightcone could not start:\n\n{e.Message}", wrap: true);
            msg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _content.AddChild(msg);
            return;
        }
        WritePidFile();
        Host!.Library.Changed += () => Callable.From(RefreshLibraryScreens).CallDeferred();
        // Plex servers show what was saved at once; this brings them up to date in the background.
        _ = Host.Plex.RefreshAllAsync();
        _ = Host.Jellyfin.RefreshAllAsync();
        _player.Changed += () => Callable.From(UpdatePlaying).CallDeferred();
        // Downloads carry on from last time; what was watched offline goes back to the servers once they answer.
        Host.Downloads.Kick();
        var sync = new Godot.Timer { WaitTime = 60, Autostart = true };
        sync.Timeout += () => Host.Downloads.SyncBack(Host.Library);
        AddChild(sync);
        Host.Library.Changed += () => Callable.From(() => Host.Downloads.SyncBack(Host.Library)).CallDeferred();
        Host.Downloads.StateChanged += e => Callable.From(() => NoteDownload(e)).CallDeferred();
        Host.Library.Changed += () => Callable.From(PrefetchTrivia).CallDeferred();
        PrefetchTrivia();
        _player.SleepFired += () => Callable.From(() => Toast("Sleep timer: playback stopped. Your spot is saved.")).CallDeferred();

        if (args.Contains("--open=appearance"))
        {
            _tab = Array.IndexOf(TabNames, "Settings");
            var settings = new SettingsScreen { StartIn = "Appearance" };
            _stack.Add(settings);
            _content.AddChild(settings);
            UpdateChrome();
        }
        else
        {
            SwitchTab(0);
        }

        var smoke = Array.IndexOf(args, "--smoke");
        var sounds = Array.IndexOf(args, "--render-sounds");
        var play = args.FirstOrDefault(a => a.StartsWith("--play=", StringComparison.Ordinal));
        if (sounds >= 0 && sounds + 1 < args.Length)
        {
            Feedback.WriteAll(args[sounds + 1]);
            GetTree().Quit();
        }
        else if (smoke >= 0 && smoke + 1 < args.Length)
        {
            AddChild(new SmokeTest(args[smoke + 1]));
        }
        else if (args.FirstOrDefault(a => a.StartsWith("--bench", StringComparison.Ordinal)) is { } bench)
        {
            AddChild(new Bench(bench.Contains('=') ? bench[(bench.IndexOf('=') + 1)..] : null));
        }
        else if (play != null)
        {
            // `Lightcone -- --play=<file or URL>`: open a file straight in the player (also used by "Open with").
            var url = play["--play=".Length..];
            MediaActions.Play(new MediaItem { Id = "file:" + url, Title = Path.GetFileNameWithoutExtension(url), Kind = MediaKind.Movie, SourceId = "file", SourceName = "File", PlayUrl = url });
        }
        else if (!ShowWhatsNew() && Host is { Settings.CheckForUpdates: true, Updater.CanSelfUpdate: true })
        {
            CheckForAppUpdate(silent: true);
        }
    }

    private void WritePidFile()
    {
        try
        {
            Directory.CreateDirectory(Host!.Paths.Runtime);
            File.WriteAllText(Host.PidFile, OS.GetProcessId().ToString());
        }
        catch (IOException)
        {
        }
    }

    // ---- chrome ----

    /// <summary>Lays out the chrome for the current UI mode around the shared content area.</summary>
    private void BuildChrome()
    {
        _content.GetParent()?.RemoveChild(_content);
        foreach (var c in _chrome.GetChildren())
        {
            _chrome.RemoveChild(c);
            c.QueueFree();
        }
        _header = null;
        _hints = null;
        _sidebar = null;
        _mini = null;
        _contentMargin = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _contentMargin.AddChild(_content);
        if (UiLayout.Desktop)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 0);
            row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _chrome.AddChild(row);
            _sidebar = new Sidebar(TabNames);
            _sidebar.Picked += i =>
            {
                PopAll();
                SwitchTab(i);
            };
            _sidebar.Searched += q =>
            {
                PopAll();
                Push(new SearchPage(q));
            };
            _sidebar.ControllerMode += () => ApplyLayout(UiLayout.Controller);
            row.AddChild(_sidebar);
            var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            right.AddThemeConstantOverride("separation", 0);
            row.AddChild(right);
            right.AddChild(_contentMargin);
            _mini = new MiniPlayer();
            right.AddChild(_mini);
            _overlay.OffsetBottom = 0;
        }
        else
        {
            Input.MouseMode = Input.MouseModeEnum.Hidden;  // never drawn; clicks and touch still work
            var margin = new MarginContainer();
            margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            margin.AddThemeConstantOverride("margin_left", 40);
            margin.AddThemeConstantOverride("margin_right", 40);
            margin.AddThemeConstantOverride("margin_top", 20);
            margin.AddThemeConstantOverride("margin_bottom", 12);
            _chrome.AddChild(margin);
            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", 16);
            margin.AddChild(root);
            _header = new Header(TabNames);
            _header.TabClicked += i =>
            {
                if (CanSwitchTabs)
                {
                    PopAll();
                    SwitchTab(i);
                }
            };
            _header.GameClicked += OpenSearch;
            root.AddChild(_header);
            root.AddChild(_contentMargin);
            _hints = new HintBar();
            root.AddChild(_hints);
            _overlay.OffsetBottom = -56;
        }
        ApplyFullScreen();
        UpdatePlaying();
        UpdateChrome();
    }

    /// <summary>Full-screen pages (the player) hide the chrome and the margins.</summary>
    private void ApplyFullScreen()
    {
        var full = _stack.Count > 0 && _stack.Any(s => s.FullScreen && s.Visible);
        if (_header != null)
        {
            _header.Visible = !full;
        }
        if (_hints != null)
        {
            _hints.Visible = !full;
        }
        if (_sidebar != null)
        {
            _sidebar.Visible = !full;
        }
        if (_mini != null)
        {
            _mini.Visible = !full && _player.Active;
        }
        var pad = full ? 0 : UiLayout.Desktop ? 26 : 0;
        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            _contentMargin.AddThemeConstantOverride($"margin_{side}", pad);
        }
        if (full && _chrome.GetChild(0) is MarginContainer m)
        {
            foreach (var side in new[] { "left", "right", "top", "bottom" })
            {
                m.AddThemeConstantOverride($"margin_{side}", 0);
            }
        }
        else if (!UiLayout.Desktop && _chrome.GetChildCount() > 0 && _chrome.GetChild(0) is MarginContainer cm)
        {
            cm.AddThemeConstantOverride("margin_left", 40);
            cm.AddThemeConstantOverride("margin_right", 40);
            cm.AddThemeConstantOverride("margin_top", 20);
            cm.AddThemeConstantOverride("margin_bottom", 12);
        }
        _highlight.Suspended = full && !_stack[^1].IsOverlay;
        PauseBackground();
    }

    /// <summary>The header chip (Controller) and the mini player (Desktop) follow what's playing.</summary>
    private void UpdatePlaying()
    {
        var cur = _player.Current;
        _header?.SetChip(cur != null && !_stack.Any(s => s is PlayerScreen) ? $"{(_player.Paused ? "❚❚" : "▶")}  {cur.Title}" : "Search", cur != null);
        if (_mini != null)
        {
            _mini.Visible = cur != null && !_stack.Any(s => s.FullScreen);
            _mini.Refresh();
        }
    }

    private void BuildBackground()
    {
        foreach (var c in _bgLayer.GetChildren())
        {
            c.QueueFree();
        }
        var bg = new ColorRect { Color = UiTheme.Bg, MouseFilter = MouseFilterEnum.Ignore };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _bgLayer.AddChild(bg);
        if (UiTheme.Spec.Glow > 0)
        {
            // Soft accent glow so the background isn't flat.
            var glow = new TextureRect
            {
                Texture = new GradientTexture2D
                {
                    Gradient = new Gradient
                    {
                        Offsets = [0f, 1f],
                        Colors = [UiTheme.Accent with { A = UiTheme.Spec.Glow }, UiTheme.Accent with { A = 0f }],
                    },
                    Fill = GradientTexture2D.FillEnum.Radial,
                    FillFrom = new Vector2(0.1f, 0f),
                    FillTo = new Vector2(0.85f, 0.9f),
                    Width = 256,
                    Height = 256,
                },
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            glow.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            _bgLayer.AddChild(glow);
        }
        _animBg = null;
        _crt = null;
        if (Host?.Settings.AnimatedBackground != false)
        {
            _animBg = new AnimatedBackground(UiTheme.Spec);
            _bgLayer.AddChild(_animBg);
        }
        switch (UiTheme.Spec.Decor)
        {
            case Decor.DotGrid:
                _bgLayer.AddChild(new DotGrid());
                break;
            case Decor.PixelGrid:
                _bgLayer.AddChild(new PixelGrid());
                break;
        }
        // Scanlines sit above everything (ZIndex), from the theme or the CRT setting.
        if (UiTheme.Spec.Decor == Decor.Scanlines || Host?.Settings.Crt == true)
        {
            _crt = new Scanlines(Host?.Settings.Crt == true ? Host.Settings.CrtLevel : 0);
            _bgLayer.AddChild(_crt);
        }
        PauseBackground();
    }

    /// <summary>Re-applies the background (animation setting changed).</summary>
    public void RefreshBackground() => BuildBackground();

    /// <summary>Smoke test only: switches theme in place (users restart from Settings).</summary>
    public void ApplyTheme(string name)
    {
        AppLog.Info($"theme -> {name}");
        UiTheme.Use(name, Host?.Settings.Accents.GetValueOrDefault(name));
        Theme = UiTheme.Build();
        BuildBackground();
        _toast.Restyle();
        _highlight.Restyle();
        BuildChrome();
        foreach (var s in _stack)
        {
            s.Rebuild();
        }
        UpdateChrome();
    }

    /// <summary>Shows or hides the Music tab (Settings → Library); stays on Settings.</summary>
    public void ApplyMusicTab(bool on)
    {
        if (Host == null || Host.Settings.MusicTab == on)
        {
            return;
        }
        Host.SaveSettings(Host.Settings with { MusicTab = on });
        AppLog.Info($"music tab -> {(on ? "on" : "off")}");
        var focus = GetViewport().GuiGetFocusOwner();
        BuildChrome();
        if (focus != null && IsInstanceValid(focus))
        {
            Callable.From(() =>
            {
                if (IsInstanceValid(focus) && focus.IsInsideTree())
                {
                    focus.GrabFocus();
                }
            }).CallDeferred();
        }
        _tab = Array.IndexOf(TabNames, "Settings");
        UpdateChrome();
        UpdatePlaying();
    }

    /// <summary>Switches between Controller and Desktop mode (Settings, or the sidebar's button).</summary>
    public void ApplyLayout(string setting)
    {
        if (Host == null)
        {
            return;
        }
        Host.SaveSettings(Host.Settings with { Layout = setting });
        UiLayout.Apply(GetWindow(), setting, OS.GetCmdlineUserArgs().Contains("--windowed"));
        AppLog.Info($"layout -> {(UiLayout.Desktop ? "desktop" : "controller")} ({setting})");
        var tab = _tab;
        var settingsOpen = _stack.Count > 0 && _stack[0] is SettingsScreen;
        foreach (var s in _stack)
        {
            s.QueueFree();
        }
        _stack.Clear();
        BuildChrome();
        _tab = -1;
        if (settingsOpen)
        {
            _tab = Array.IndexOf(TabNames, "Settings");
            var settings = new SettingsScreen { StartIn = "Layout" };
            _stack.Add(settings);
            _content.AddChild(settings);
            UpdateChrome();
        }
        else
        {
            SwitchTab(tab);
        }
    }

    public void ApplyFeedback(int volume, bool haptics)
    {
        if (_feedback != null)
        {
            _feedback.Volume = volume;
            _feedback.Haptics = haptics;
        }
    }

    private void RefreshLibraryScreens()
    {
        foreach (var s in _stack)
        {
            if (s is HomeScreen or LibraryScreen or SourcesScreen)
            {
                s.Rebuild();
            }
        }
    }

    // ---- updates ----

    private bool ShowWhatsNew()
    {
        if (Host == null)
        {
            return false;
        }
        var current = Host.Updater.CurrentVersion;
        var display = AppUpdater.DisplayVersion(current);
        var seen = AppUpdater.ParseVersion(Host.Settings.LastSeenVersion);
        if (Host.Settings.LastSeenVersion != display)
        {
            Host.SaveSettings(Host.Settings with { LastSeenVersion = display });
        }
        if (seen == null)
        {
            return false;
        }
        var news = Changelog.Between(Changelog.Bundled, seen, current);
        if (news.Count == 0)
        {
            return false;
        }
        Push(new ChangelogPage($"What's new in Lightcone {display}", news, whatsNew: true));
        return true;
    }

    public void ShowChangelog() => Push(new ChangelogPage("Changelog", Changelog.Bundled));

    public async void CheckForAppUpdate(bool silent)
    {
        if (Host == null)
        {
            return;
        }
        var updater = Host.Updater;
        AppRelease? release;
        try
        {
            release = await Task.Run(() => updater.CheckAsync());
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (!silent)
            {
                Error("Couldn't reach GitHub to check for updates.");
            }
            return;
        }
        var current = updater.CurrentDisplay;
        if (release == null)
        {
            if (!silent)
            {
                Success($"Lightcone {current} is the latest version");
            }
            return;
        }
        // A startup check never interrupts playback or a page the user is on.
        if (silent && (_player.Active || _stack.Count != 1))
        {
            return;
        }
        var next = release.Display;
        if (!updater.CanSelfUpdate)
        {
            Toast($"Lightcone {next} is available. Re-run the installer to update this copy.");
            return;
        }
        var whatsNew = Changelog.ItemsFromReleaseNotes(release.Notes);
        var notes = whatsNew.Count > 0 ? "\n\nWhat's new:\n" + string.Join("\n", whatsNew.Take(8).Select(i => "• " + i)) : "";
        Push(new ChoicePage($"Lightcone {next} is available (you have {current}).{notes}\n\nThe update takes a few seconds. Your sources, watch progress and settings are not touched, and the current version is kept so you can roll back in Settings.",
            ("Update and restart", () => InstallAppUpdate(release)),
            ("Later", () => { })));
    }

    private void InstallAppUpdate(AppRelease release)
    {
        if (Host == null)
        {
            return;
        }
        _player.Stop();
        var updater = Host.Updater;
        Busy($"Updating to {release.Display}…", (p, ct) => updater.InstallAsync(release, p, ct), Restart);
    }

    public void RollbackApp()
    {
        if (Host != null)
        {
            Guard(() => Restart(Host.Updater.Rollback()));
        }
    }

    /// <summary>Restarts the app with the same arguments (plus <paramref name="extra"/>), e.g. to apply a theme.</summary>
    public void RestartApp(params string[] extra)
    {
        AppLog.Info($"restart ({string.Join(' ', extra)})");
        var user = OS.GetCmdlineUserArgs().Where(a => !a.StartsWith("--open=", StringComparison.Ordinal)).Concat(extra).ToList();
        var args = OS.GetCmdlineArgs().ToList();
        if (user.Count > 0)
        {
            args.Add("--");
            args.AddRange(user);
        }
        OS.SetRestartOnExit(true, args.ToArray());
        GetTree().Quit();
    }

    private void Restart(string exe)
    {
        try
        {
            AppUpdater.Relaunch(exe, OS.GetCmdlineUserArgs());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            Error("Updated. Please start Lightcone again to finish.");
            return;
        }
        GetTree().Quit();
    }

    /// <summary>Releases Godot objects that C# statics keep alive, while .NET is still running.</summary>
    public override void _ExitTree()
    {
        IdleInhibit.Release();
        try
        {
            if (Host != null && File.Exists(Host.PidFile))
            {
                File.Delete(Host.PidFile);
            }
        }
        catch (IOException)
        {
        }
        Theme = null;
        CoverArt.ReleaseShared();
        PosterArt.ReleaseShared();
        VideoView.ReleaseShared();
        Lightcone.App.Ui.Poly.Ps1.ReleaseShared();
        UiTheme.ReleaseFonts();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut)
        {
            _appFocused = false;
            Pad.ReleaseAll();
            PauseBackground();
        }
        else if (what == NotificationApplicationFocusIn)
        {
            _appFocused = true;
            if (!UiLayout.Desktop)
            {
                Input.MouseMode = Input.MouseModeEnum.Hidden;
            }
            PauseBackground();
        }
    }

    // ---- input ----

    public override void _Input(InputEvent e)
    {
        if (IsUserInput(e))
        {
            _idle = 0;
            if (_ambient.Showing)
            {
                // Waking up only wakes up: the press doesn't also act on the menus.
                _ambient.End();
                GetViewport().SetInputAsHandled();
                return;
            }
        }
        if (e is InputEventAction or InputEventKey && e.IsPressed() && !e.IsEcho())
        {
            if (e.IsAction("ui_up") || e.IsAction("ui_down") || e.IsAction("ui_left") || e.IsAction("ui_right"))
            {
                _navAtMs = Time.GetTicksMsec();
            }
            else if (e.IsAction(Pad.Accept) && GetViewport().GuiGetFocusOwner() is BaseButton and not Stepper)
            {
                Feedback.Play(Cue.Confirm);
            }
        }
        if (_stack.Count > 0 && e is InputEventKey { Pressed: true, CtrlPressed: true } key && Shortcut(_stack[^1], key))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        // Desktop: right-click a row or tile = A / Enter.
        if (UiLayout.Desktop && e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }
            && ButtonUnder(GetViewport().GuiGetHoveredControl()) is { Disabled: false } target)
        {
            target.GrabFocus();
            target.EmitSignal(BaseButton.SignalName.Pressed);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (TouchScroll.Handle(GetViewport(), e))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is InputEventJoypadButton or InputEventJoypadMotion && !_appFocused)
        {
            // Godot receives joypad input even when unfocused; never react to it.
            GetViewport().SetInputAsHandled();
            return;
        }
        if (Pad.Translate(e))
        {
            GetViewport().SetInputAsHandled();
        }
    }

    private static bool IsUserInput(InputEvent e) => e switch
    {
        InputEventKey or InputEventMouseButton or InputEventJoypadButton or InputEventScreenTouch or InputEventAction => e.IsPressed(),
        InputEventJoypadMotion m => Math.Abs(m.AxisValue) > 0.5f,
        InputEventMouseMotion mm => mm.Relative.Length() > 4,
        _ => false,
    };

    /// <summary>Starts ambient mode now (also after Settings' idle minutes on the menus).</summary>
    public void ShowAmbient()
    {
        if (!_ambient.Showing && Host != null)
        {
            _ambient.Begin(Host.Library.All);
        }
    }

    private void CheckIdle(double delta)
    {
        _idle += delta;
        var minutes = Host?.Settings.AmbientMinutes ?? 0;
        if (minutes <= 0 || _ambient.Showing || _idle < minutes * 60 || _player.Current != null || _stack.Count == 0 || _stack[^1] is PlayerScreen or TextEntryPage)
        {
            return;
        }
        ShowAmbient();
    }

    private double _watchlistClock;

    public override void _Process(double delta)
    {
        CheckIdle(delta);
        // The Plex watchlist follows changes made in other Plex apps: every 5 minutes (not mid-film).
        _watchlistClock += delta;
        if (_watchlistClock > 60)
        {
            _watchlistClock = 0;
            if (Host != null && !_player.Active)
            {
                Host.Plex.SyncWatchlistIfStale(TimeSpan.FromMinutes(5));
            }
        }
        if (_toast.Visible || _stack.Count == 0 || _stack[^1] is not PlayerScreen)
        {
            _player.Paced = false;  // anything else on screen runs at the display's rate
        }
        // No screen dimming, screensaver or sleep while a video plays (paused or stopped: normal).
        IdleInhibit.Update(_player.Active && _player.HasVideo && !_player.Paused);
        if (_appFocused)
        {
            TouchScroll.Process(delta);
            Pad.Process(delta);
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_stack.Count == 0)
        {
            return;
        }
        var top = _stack[^1];
        if (e.IsActionPressed(Pad.Back))
        {
            var handled = top.OnBack();
            if (!handled && _stack.Count > 1)
            {
                Pop();
                handled = true;
            }
            if (handled)
            {
                Feedback.Play(Cue.Back);
            }
        }
        else if (e.IsActionPressed(Pad.TabPrev) && CanSwitchTabs)
        {
            PopAll();
            SwitchTab((_tab + TabNames.Length - 1) % TabNames.Length);
        }
        else if (e.IsActionPressed(Pad.TabNext) && CanSwitchTabs)
        {
            PopAll();
            SwitchTab((_tab + 1) % TabNames.Length);
        }
        else if (e.IsActionPressed(Pad.Switch) && !top.FullScreen && top is not SearchPage)
        {
            OpenSearch();
        }
        else if (e.IsActionPressed(Pad.Menu) && _player.Active && !_stack.Any(s => s is PlayerScreen))
        {
            ShowPlayer();
        }
        else if (e.IsActionPressed(Pad.Secondary) || e.IsActionPressed(Pad.Tertiary))
        {
            top.OnAction(e.IsActionPressed(Pad.Secondary) ? Pad.Secondary : Pad.Tertiary);
        }
        else
        {
            return;
        }
        GetViewport().SetInputAsHandled();
    }

    /// <summary>Ctrl+1–6 pick a section, Ctrl+F searches, the rest go to the screen on top.</summary>
    private bool Shortcut(Screen top, InputEventKey key)
    {
        if (key.Echo)
        {
            return false;
        }
        if (key.Keycode is >= Key.Key1 and <= Key.Key9)
        {
            var i = (int)(key.Keycode - Key.Key1);
            if (i < TabNames.Length && !top.FullScreen)
            {
                PopAll();
                SwitchTab(i);
                return true;
            }
            return false;
        }
        if (key.Keycode == Key.F && !top.FullScreen)
        {
            if (_sidebar != null)
            {
                _sidebar.FocusSearch();
            }
            else
            {
                OpenSearch();
            }
            return true;
        }
        return top.OnShortcut(key);
    }

    private static BaseButton? ButtonUnder(Control? c)
    {
        for (var n = (Node?)c; n != null; n = n.GetParent())
        {
            if (n is BaseButton b)
            {
                return b;
            }
        }
        return null;
    }

    // ---- navigation ----

    private string StackNames() => string.Join(" > ", _stack.Select(x => x.GetType().Name));

    public void OpenSearch()
    {
        if (_stack.Count > 0 && Top is not SearchPage)
        {
            Push(new SearchPage());
        }
    }

    /// <summary>Opens the full-screen player for what's playing now.</summary>
    public void ShowPlayer()
    {
        if (_stack.Count > 0 && _stack[^1] is PlayerScreen)
        {
            return;
        }
        // Only one player page: drop one further down (e.g. opened again from the mini player).
        var old = _stack.OfType<PlayerScreen>().FirstOrDefault();
        if (old != null)
        {
            _stack.Remove(old);
            old.QueueFree();
        }
        Push(new PlayerScreen());
    }

    /// <summary>Leaves the player without stopping (Desktop mini player, or music in Controller mode).</summary>
    public void MinimizePlayer()
    {
        if (_stack.Count > 1 && _stack[^1] is PlayerScreen)
        {
            Pop();
        }
    }

    public void SwitchTab(string name)
    {
        var i = Array.IndexOf(TabNames, name);
        if (i >= 0)
        {
            PopAll();
            SwitchTab(i);
        }
    }

    private void SwitchTab(int index)
    {
        AppLog.Info($"tab -> {TabNames[index]}");
        if (index != _tab && _tab >= 0)
        {
            Feedback.Play(Cue.Tab);
        }
        var dir = index == _tab || _tab < 0 ? 0 : (index - _tab + TabNames.Length) % TabNames.Length == 1 ? 1 : -1;
        _tab = index;
        foreach (var s in _stack)
        {
            if (dir != 0 && s.Visible && !s.IsOverlay && !UiLayout.Desktop)
            {
                SetFocusable(s, false);
                Anim.Exit(s, -dir);
            }
            else
            {
                s.QueueFree();
            }
        }
        _stack.Clear();
        var screen = NewTab(index);
        _stack.Add(screen);
        _content.AddChild(screen);
        if (dir != 0 && !UiLayout.Desktop)
        {
            Anim.Enter(screen, dir);
        }
        else
        {
            Anim.FadeIn(screen);
        }
        UpdateChrome();
    }

    private void PopAll()
    {
        while (_stack.Count > 1)
        {
            var top = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            if (top is PlayerScreen && _player.HasVideo)
            {
                _player.Stop();
            }
            top.QueueFree();
        }
        if (_stack.Count == 1)
        {
            _stack[0].Visible = true;
            SetFocusable(_stack[0], true);
            _backdrop.FadeTo(false);
        }
        ApplyFullScreen();
    }

    private bool OverlayOpen => _stack.Any(x => x.IsOverlay);

    /// <summary>
    /// Bumpers and the header switch sections from any page (a detail page closes on the way),
    /// but not from the full-screen player or a dialog on top.
    /// </summary>
    private bool CanSwitchTabs => _stack.Count > 0 && !_stack.Any(x => x.IsOverlay || x.FullScreen);

    public void Push(Screen screen)
    {
        AppLog.Info($"push {screen.GetType().Name} over {StackNames()}");
        var top = _stack.Count > 0 ? _stack[^1] : null;
        top?.Remember();
        if (screen.IsOverlay)
        {
            if (top != null)
            {
                SetFocusable(top, false);
            }
            _stack.Add(screen);
            _overlay.AddChild(screen);
            _backdrop.FadeTo(true);
            screen.AnimateOpen();
        }
        else
        {
            if (top != null)
            {
                top.Visible = false;
            }
            _stack.Add(screen);
            (OverlayOpen ? _overlay : _content).AddChild(screen);
            Anim.Enter(screen, 1);
        }
        ApplyFullScreen();
        UpdatePlaying();
        UpdateChrome();
    }

    public void Pop()
    {
        AppLog.Info($"pop ({StackNames()})");
        if (_stack.Count <= 1)
        {
            return;
        }
        var top = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        var prev = _stack[^1];
        if (top.IsOverlay)
        {
            top.AnimateClose(top.QueueFree);
            SetFocusable(prev, true);
            if (!OverlayOpen)
            {
                _backdrop.FadeTo(false);
            }
        }
        else
        {
            SetFocusable(top, false);
            if (top.FullScreen)
            {
                top.QueueFree();
            }
            else
            {
                Anim.Exit(top, 1);
            }
            prev.Visible = true;
            Anim.Enter(prev, -1);
        }
        prev.Restore();
        if (prev is HomeScreen or LibraryScreen or DetailPage)
        {
            // Progress may have changed while playing.
            prev.Rebuild();
        }
        ApplyFullScreen();
        UpdatePlaying();
        UpdateChrome();
    }

    private static void SetFocusable(Node root, bool on)
    {
        foreach (var n in root.GetChildren())
        {
            if (n is Control c)
            {
                if (!on && c.FocusMode != FocusModeEnum.None)
                {
                    c.SetMeta("ed_focus", (int)c.FocusMode);
                    c.FocusMode = FocusModeEnum.None;
                }
                else if (on && c.HasMeta("ed_focus"))
                {
                    c.FocusMode = (FocusModeEnum)c.GetMeta("ed_focus").AsInt32();
                    c.RemoveMeta("ed_focus");
                }
            }
            SetFocusable(n, on);
        }
    }

    public void PopTo(Screen screen)
    {
        while (_stack.Count > 1 && _stack[^1] != screen && _stack.Contains(screen))
        {
            Pop();
        }
    }

    public void UpdateChrome()
    {
        _header?.SetTab(_tab, CanSwitchTabs);
        _sidebar?.SetCurrent(_tab, _stack.Count == 1);
        if (_stack.Count == 0 || _hints == null)
        {
            return;
        }
        var hints = new List<(string, string)>();
        hints.AddRange(_stack[^1].Hints);
        if (_player.Active && !_stack.Any(s => s is PlayerScreen))
        {
            hints.Add((Pad.Menu, "Now playing"));
        }
        hints.Add((Pad.Accept, "Select"));
        if (_stack.Count > 1)
        {
            hints.Add((Pad.Back, "Back"));
        }
        _hints.SetHints(hints);
    }

    // ---- feedback ----

    public void Toast(string text) => _toast.Show(text);

    public bool ToastShowing => _toast.Visible;

    private ulong _triviaAt;

    /// <summary>
    /// Looks up "Did you know?" facts in the background for what's likely to be opened: what you're
    /// watching, then the newest movies and shows. At most once a minute (the library changes often
    /// while it loads); the trivia queue itself goes one title at a time.
    /// </summary>
    private void PrefetchTrivia()
    {
        if (Host is not { Settings.Trivia: true } host || Time.GetTicksMsec() - _triviaAt < 60_000 && _triviaAt != 0)
        {
            return;
        }
        var lib = host.Library;
        var watching = lib.Section(Lightcone.Host.Library.MediaKind.Movie, filter: Lightcone.Host.Library.WatchFilter.InProgress)
            .Concat(lib.Section(Lightcone.Host.Library.MediaKind.Show, filter: Lightcone.Host.Library.WatchFilter.InProgress));
        var newest = lib.Section(Lightcone.Host.Library.MediaKind.Movie, Lightcone.Host.Library.SortKey.Added, descending: true).Take(60)
            .Concat(lib.Section(Lightcone.Host.Library.MediaKind.Show, Lightcone.Host.Library.SortKey.Added, descending: true).Take(40));
        var all = watching.Concat(newest).ToList();
        if (all.Count > 0)
        {
            _triviaAt = Time.GetTicksMsec();  // nothing loaded yet: try again on the next change
            host.Trivia.Prefetch(all);
        }
    }

    /// <summary>A download started, finished or failed: say so (not while watching something).</summary>
    private void NoteDownload(Lightcone.Host.Library.DownloadEntry e)
    {
        var watching = _stack.Count > 0 && _stack[^1] is PlayerScreen;
        switch (e.State)
        {
            case Lightcone.Host.Library.DownloadState.Downloading when !watching:
                Toast($"Downloading {e.Item.Title}");
                break;
            case Lightcone.Host.Library.DownloadState.Done when !watching:
                Toast($"Downloaded {e.Item.Title}");
                break;
            case Lightcone.Host.Library.DownloadState.Failed:
                Error($"Download failed: {e.Error}");
                break;
        }
    }

    public void Error(string text)
    {
        AppLog.Warn("shown error: " + text);
        Feedback.Play(Cue.Error);
        _toast.Show(text, UiTheme.Bad);
    }

    public void Success(string text)
    {
        Feedback.Play(Cue.Success);
        _toast.Show(text, UiTheme.Good);
    }

    public bool Guard(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception e) when (IsUserFacing(e))
        {
            Error(e.Message);
            return false;
        }
    }

    public static bool IsUserFacing(Exception e) =>
        e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or InvalidDataException or HttpRequestException or KeyNotFoundException or TaskCanceledException
            or System.Text.Json.JsonException or Lightcone.Host.Plex.PlexAuthException or Lightcone.Host.Jellyfin.JellyfinAuthException;

    public void Confirm(string message, string yes, Action onYes) => Push(new ConfirmPage(message, yes, onYes));

    public async void Busy<T>(string title, Func<IProgress<double>, CancellationToken, Task<T>> work, Action<T> done, bool cancellable = true)
    {
        using var cts = new CancellationTokenSource();
        var page = new BusyPage(title, cancellable ? cts : null);
        Push(page);
        var progress = new Progress<double>(page.SetProgress);
        try
        {
            var result = await Task.Run(() => work(progress, cts.Token), cts.Token);
            PopTo(page);
            Pop();
            done(result);
        }
        catch (Exception e) when (IsUserFacing(e) || e is OperationCanceledException)
        {
            PopTo(page);
            Pop();
            if (e is not OperationCanceledException)
            {
                Error(e.Message);
            }
        }
    }
}
