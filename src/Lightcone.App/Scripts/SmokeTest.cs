using Lightcone.App.Playback;
using Lightcone.App.Ui;
using Godot;
using Lightcone.App.Screens;

namespace Lightcone.App;

/// <summary>
/// `Lightcone -- --smoke &lt;dir&gt;`: drives the UI with scripted input, saves a screenshot after
/// each step and quits. Used by CI (under Xvfb) to catch startup and navigation regressions,
/// and to publish screenshots of each build. Exit code 1 if any step throws.
/// </summary>
public partial class SmokeTest : Node
{
    private readonly string _outDir;
    private readonly Queue<(string Name, Action Act, double Wait, string? Expect, string? Tab)> _steps = new();
    private double _wait = 1.0;
    private int _shot;
    private int _failures;

    public SmokeTest(string outDir)
    {
        _outDir = outDir;
        Directory.CreateDirectory(outDir);
        // The animated background costs a lot under software rendering (CI); it would skew the
        // hold-to-repeat timing. It is switched on for the theme screenshots further down.
        // The run script starts the app with --demo: the sample library, played through libmpv's lavfi inputs.
        Step("home", () => SetAnimatedBackground(false), wait: 2.0, tab: "Home");
        // Real joypad and keyboard events (not actions), through Pad's translation layer.
        Step("home-down", () => Tap(JoyButton.DpadDown), tab: "Home");
        Step("home-right", () => Tap(JoyButton.DpadRight), tab: "Home");
        Step("home-stick", () => { Stick(1f, 0f); Stick(0f, 0f); }, tab: "Home");
        // Back up to the hero: the page scrolls all the way to the top again.
        Step("home-down-more", () => Tap(JoyButton.DpadDown, 2), wait: 0.8, tab: "Home");
        Step("home-up-top", () => Tap(JoyButton.DpadUp, 3), wait: 0.9, expect: "Play", tab: "Home");
        // Bumpers switch sections.
        Step("movies", () => Tap(JoyButton.RightShoulder), wait: 1.2, tab: "Movies");
        // Y: sort and filter on a card. Unwatched, back to everything, then sort by title.
        Step("movies-sort", () => Tap(JoyButton.Y), wait: 1.0, expect: "Sort: Title", tab: "Movies");
        Step("movies-filter-unwatched", () =>
        {
            Tap(JoyButton.DpadRight);
            Tap(JoyButton.DpadDown);
            Tap(JoyButton.A);
        }, wait: 1.0, expect: "Show: Unwatched", tab: "Movies");
        Step("movies-filter-all", () =>
        {
            Tap(JoyButton.DpadUp);
            Tap(JoyButton.A);
        }, wait: 1.0, expect: "Show: Everything", tab: "Movies");
        Step("movies-sort-pick", () =>
        {
            Tap(JoyButton.DpadLeft);
            Tap(JoyButton.A);
        }, wait: 1.0, expect: "Sort: Title", tab: "Movies");
        Step("movies-sort-title", () => Tap(JoyButton.B), wait: 1.0, expect: "A Field of Lanterns", tab: "Movies");
        Step("movie-detail", () => Tap(JoyButton.A), wait: 1.2, expect: "Play", tab: "Movies");
        // X: on the watchlist (Home gets a Watchlist row). Collections…: start one with this film.
        Step("movie-watchlist", () => Tap(JoyButton.X), wait: 1.0, expect: "Play", tab: "Movies");
        Step("movie-collections", () =>
        {
            FindFocusable(GetTree().Root, "A Field of Lanterns: collections")?.GrabFocus();
            Tap(JoyButton.A);
        }, wait: 1.0, expect: "New collection");
        Step("collection-name", () => Tap(JoyButton.A), wait: 1.0);
        Step("collection-made", () =>
        {
            TypeText("Faves");
            Tap(JoyButton.Start);
        }, wait: 1.2, tab: "Movies");
        Step("movie-detail-again", () => FindFocusable(GetTree().Root, "A Field of Lanterns: play")?.GrabFocus(), wait: 0.6, expect: "Play", tab: "Movies");
        // A on Play: the player, full screen, playing a generated test pattern.
        Step("player", () => Tap(JoyButton.A), wait: 2.5, tab: "Movies");
        // Triggers seek (the generated sample streams can't actually jump; the OSD still shows).
        Step("player-seek-trigger", () => Trigger(JoyAxis.TriggerRight), wait: 1.2);
        Step("player-seek-back", () => Trigger(JoyAxis.TriggerLeft), wait: 1.2);
        Step("player-pause", () => Tap(JoyButton.A), wait: 1.0);
        Step("player-info", () => Tap(JoyButton.Y), wait: 1.0);
        Step("player-tracks", () => Tap(JoyButton.Start), wait: 1.0);
        // Options: speed (remembered for this movie) and the sleep timer.
        Step("player-options-speed", () =>
        {
            Tap(JoyButton.DpadDown);
            Tap(JoyButton.DpadRight);
        }, wait: 1.0, expect: "Playback speed");
        Step("player-options-sleep", () =>
        {
            Tap(JoyButton.DpadDown, 3);
            Tap(JoyButton.DpadRight);
        }, wait: 1.0, expect: "Pauses playback");
        Step("player-tracks-back", () => Tap(JoyButton.B), wait: 1.0);
        // B stops video and goes back (position saving and Resume are covered by the unit tests:
        // the sample streams play in real time, and resume starts after 30 s).
        Step("player-back", () => Tap(JoyButton.B), wait: 1.2, expect: "Play", tab: "Movies");
        Step("detail-back", () => Tap(JoyButton.B), wait: 1.0, expect: "A Field of Lanterns", tab: "Movies");
        // X in Movies: collections (yours, the source's, automatic ones).
        Step("movies-collections", () => Tap(JoyButton.X), wait: 1.2, expect: "Faves", tab: "Movies");
        Step("collection-detail", () => Tap(JoyButton.A), wait: 1.2, expect: "Play", tab: "Movies");
        Step("collection-titles", () => Tap(JoyButton.DpadDown), wait: 0.8, expect: "A Field of Lanterns", tab: "Movies");
        Step("collection-back", () => Tap(JoyButton.B), wait: 1.0, expect: "Faves", tab: "Movies");
        Step("movies-all", () => Tap(JoyButton.X), wait: 1.2, tab: "Movies");
        Step("home-watchlist", () => Main.Instance.SwitchTab("Home"), wait: 1.2, tab: "Home");
        Step("back-to-movies", () => Main.Instance.SwitchTab("Movies"), wait: 1.0, tab: "Movies");
        Step("shows", () => Tap(JoyButton.RightShoulder), wait: 1.2, tab: "Shows");
        // Deep Field has three seasons and specials: a season strip above the episodes.
        Step("show-detail", () =>
        {
            FindFocusable(GetTree().Root, "Deep Field")?.GrabFocus();
            Tap(JoyButton.A);
        }, wait: 1.2, expect: "Play S1 · E1", tab: "Shows");
        Step("show-seasons", () => Tap(JoyButton.DpadDown), wait: 0.8, expect: "Season 1", tab: "Shows");
        // Moving along the strip switches the season below.
        Step("show-season-2", () => Tap(JoyButton.DpadRight), wait: 0.8, expect: "Season 2", tab: "Shows");
        Step("show-episodes", () => Tap(JoyButton.DpadDown, 2), wait: 0.8, expect: "Episode 1: play", tab: "Shows");
        // RT / LT switch season from the episode list too.
        Step("show-season-trigger", () => Trigger(JoyAxis.TriggerRight), wait: 0.8, expect: "Episode 1: play", tab: "Shows");
        // Y marks the whole season watched.
        Step("show-season-watched", () => Tap(JoyButton.Y), wait: 1.0, tab: "Shows");
        // "Previously": the recap card before an episode after a break.
        Step("show-recap", () =>
        {
            var lib = Main.Instance.Host!.Library;
            var show = lib.Section(Lightcone.Host.Library.MediaKind.Show).First(s => s.Title == "Deep Field");
            var eps = lib.Children(show.Id).Where(e => e.Season == 1).ToList();
            lib.SetWatched(eps[0], true);
            Main.Instance.Push(new RecapPage(eps[1], eps[0]));
        }, wait: 1.2, expect: "Play S1 · E2", tab: "Shows");
        Step("show-recap-back", () => Tap(JoyButton.B), wait: 1.0, tab: "Shows");
        // Up next: the card counts down (A plays now), and after a run with no press asks "Still watching?" (B stops).
        Step("episode-play", () =>
        {
            FindFocusable(GetTree().Root, "Episode 2: play")?.GrabFocus();
            Tap(JoyButton.A);
        }, wait: 2.5, tab: "Shows");
        Step("player-up-next", () => (Main.Instance.Top as PlayerScreen)?.ForceUpNext(false), wait: 1.6, tab: "Shows");
        Step("player-up-next-play", () => Tap(JoyButton.A), wait: 2.0, tab: "Shows");
        Step("player-still-watching", () => (Main.Instance.Top as PlayerScreen)?.ForceUpNext(true), wait: 1.2, tab: "Shows");
        Step("player-still-stop", () => Tap(JoyButton.B), wait: 1.5, tab: "Shows");
        Step("show-back", () => Tap(JoyButton.B), wait: 1.0, tab: "Shows");
        // Music keeps playing in the background with a now-playing chip in the header.
        Step("music", () => Tap(JoyButton.RightShoulder), wait: 1.2, tab: "Music");
        Step("album", () => Tap(JoyButton.A), wait: 1.2, tab: "Music");
        Step("album-play", () => Tap(JoyButton.A), wait: 2.0, tab: "Music");
        Step("album-player-back", () => Tap(JoyButton.B), wait: 1.2, tab: "Music");
        Step("album-back", () => Tap(JoyButton.B), wait: 1.0, tab: "Music");
        Step("home-continue", () => Tap(JoyButton.LeftShoulder, 3), wait: 1.5, tab: "Home");
        // Y on Home: Surprise me (Y again: another pick). X: your stats.
        Step("home-surprise", () => Tap(JoyButton.Y), wait: 1.2, tab: "Home");
        Step("home-surprise-again", () => Tap(JoyButton.Y), wait: 1.0, tab: "Home");
        Step("home-surprise-back", () => Tap(JoyButton.B), wait: 1.0, tab: "Home");
        Step("home-stats", () => Tap(JoyButton.X), wait: 1.5, tab: "Home");
        Step("home-stats-back", () => Tap(JoyButton.B), wait: 1.0, tab: "Home");
        // Search: the View button (or /) opens it; typing filters across the library.
        // The built-in keyboard: A types the focused key, X deletes, Y adds a space; a real keyboard types too.
        Step("search", () => Tap(JoyButton.Back), wait: 1.0);
        Step("search-osk", () =>
        {
            Tap(JoyButton.A);  // "q"
            Tap(JoyButton.Y);  // space
            Tap(JoyButton.X, 2);  // delete both
        }, wait: 0.6);
        Step("search-type", () => TypeText("orbit"), wait: 1.2);
        Step("search-results", () =>
        {
            Tap(JoyButton.DpadLeft);  // wraps to the row's last key
            Tap(JoyButton.DpadRight);  // and on to the results
        }, wait: 0.8, expect: "Glass Orbit");
        Step("search-back", () => Tap(JoyButton.B), wait: 1.0, tab: "Home");
        Step("sources", () => Tap(JoyButton.LeftShoulder, 2), wait: 1.2, tab: "Sources");
        // Plex sign-in (no network in the smoke test: the page shows a stand-in code).
        Step("plex-sign-in", () =>
        {
            PlexSignInPage.Offline = true;
            FindFocusable(GetTree().Root, "Plex. Sign in")?.GrabFocus();
            Tap(JoyButton.A);
        }, wait: 1.2, expect: "Open plex.tv/link", tab: "Sources");
        Step("plex-sign-in-back", () => Tap(JoyButton.B), wait: 1.0, tab: "Sources");
        // Jellyfin: servers on this network (none in the smoke test) or an address, then Quick Connect.
        Step("jellyfin-add", () =>
        {
            JellyfinAddPage.Offline = true;
            FindFocusable(GetTree().Root, "Jellyfin. Find a server")?.GrabFocus();
            Tap(JoyButton.A);
        }, wait: 1.2, expect: "Enter the server's address", tab: "Sources");
        Step("jellyfin-sign-in", () =>
        {
            JellyfinSignInPage.Offline = true;
            Main.Instance.Push(new JellyfinSignInPage(new Lightcone.Host.Jellyfin.JellyfinServerInfo("http://192.168.1.30:8096", "demo", "Basement", "10.10.3")));
        }, wait: 1.2, expect: "Sign in with user name and password", tab: "Sources");
        Step("jellyfin-back", () => Tap(JoyButton.B, 2), wait: 1.2, tab: "Sources");
        Step("settings", () => Tap(JoyButton.RightShoulder), wait: 1.2, expect: "Playback", tab: "Settings");
        Step("playback-pane", () => Press(Pad.Accept), expect: "Decode video on the GPU");
        Step("playback-hwdec", () => Press("ui_right"), expect: "Decode video on the GPU");
        Step("playback-hwdec-back", () => Press("ui_left"), expect: "Decode video on the GPU");
        Step("playback-back", () => Press(Pad.Back), expect: "Playback");
        Step("settings-library-row", () => Press("ui_down"), expect: "Library");
        Step("settings-appearance", () => Press("ui_down"), expect: "Appearance");
        Step("settings-sound", () => Press("ui_down"), expect: "Sound & feel");
        Step("sound-pane", () => Press(Pad.Accept), expect: "Short sounds when you move");
        Step("sound-back", () => Press(Pad.Back), expect: "Sound & feel");
        // Music is optional: off takes the tab away (and music out of Home and search).
        Step("settings-library", () => FindFocusable(GetTree().Root, "Music, collections")?.GrabFocus(), expect: "Library");
        Step("library-pane", () => Press(Pad.Accept), expect: "movies and TV first");
        Step("music-off", () => Press("ui_right"), wait: 1.0, expect: "movies and TV first", tab: "Settings");
        Step("music-off-home", () => Main.Instance.SwitchTab("Home"), wait: 1.2, tab: "Home");
        Step("music-on", () =>
        {
            Main.Instance.SwitchTab("Settings");
            FindFocusable(GetTree().Root, "Music, collections")?.GrabFocus();
            Press(Pad.Accept);
            Press("ui_left");
        }, wait: 1.2, expect: "movies and TV first", tab: "Settings");
        Step("library-back", () => Press(Pad.Back), expect: "Library");
        Step("about", () => FindFocusable(GetTree().Root, "Where Lightcone keeps its files")?.GrabFocus(), expect: "About");
        Step("about-pane", () => Press(Pad.Accept), expect: "Open the app log");
        Step("log-page", () => Press(Pad.Accept), expect: "Log lines");
        Step("log-back", () => Press(Pad.Back), expect: "Open the app log");
        Step("about-back", () => Press(Pad.Back), expect: "About");
        Step("changelog", () =>
        {
            FindFocusable(GetTree().Root, "Version, changelog")?.GrabFocus();
            Press(Pad.Accept);
            FindFocusable(GetTree().Root, "What changed in each version")?.GrabFocus();
            Press(Pad.Accept);
        }, wait: 1.0);
        Step("changelog-back", () =>
        {
            Press(Pad.Back);
            Press(Pad.Back);
        }, wait: 1.0, tab: "Settings");
        // Every theme, live: Home and Settings.
        foreach (var t in UiTheme.Themes)
        {
            var id = t.Id;
            Step($"theme-{id}-home", () =>
            {
                SetTheme(id);
                SetAnimatedBackground(true);
                Main.Instance.SwitchTab("Home");
            }, wait: 1.4, tab: "Home");
            Step($"theme-{id}-movies", () => Tap(JoyButton.RightShoulder), wait: 1.0, tab: "Movies");
        }
        Step("theme-restore", () =>
        {
            SetTheme("dark");
            SetAnimatedBackground(false);
        }, wait: 1.0);
        // Desktop mode: sidebar, toolbar, sortable table, mini player, keyboard shortcuts.
        Step("desktop-home", () =>
        {
            Main.Instance.ApplyLayout("desktop");
            Main.Instance.SwitchTab("Home");
        }, wait: 1.5, tab: "Home");
        Step("desktop-movies", () => Key(Godot.Key.Key2, ctrl: true), wait: 1.2, tab: "Movies");
        Step("desktop-list-view", () => Click(FindFocusable(GetTree().Root, "List")), wait: 1.2, tab: "Movies");
        Step("desktop-sort-year", () => SortColumn(1), wait: 1.0, tab: "Movies");
        Step("desktop-grid-view", () => Click(FindFocusable(GetTree().Root, "Grid")), wait: 1.2, tab: "Movies");
        Step("desktop-filter-watched", () => PickOption("Filter: which titles to show", 3), wait: 1.2, tab: "Movies");
        Step("desktop-filter-all", () => PickOption("Filter: which titles to show", 0), wait: 1.2, tab: "Movies");
        Step("desktop-collections", () => Click(FindFocusable(GetTree().Root, "Show collections")), wait: 1.2, tab: "Movies");
        Step("desktop-all-titles", () => Click(FindFocusable(GetTree().Root, "Show collections")), wait: 1.2, tab: "Movies");
        Step("desktop-shows", () => Key(Godot.Key.Key3, ctrl: true), wait: 1.2, tab: "Shows");
        Step("desktop-show-detail", () =>
        {
            FindFocusable(GetTree().Root, "Deep Field")?.GrabFocus();
            Key(Godot.Key.Enter);
        }, wait: 1.2, tab: "Shows");
        Step("desktop-show-season", () => Click(FindFocusable(GetTree().Root, "Specials")), wait: 1.0, expect: "Special 1: play", tab: "Shows");
        Step("desktop-show-back", () => Key(Godot.Key.Escape), wait: 1.0, tab: "Shows");
        Step("desktop-movies-again", () => Key(Godot.Key.Key2, ctrl: true), wait: 1.2, tab: "Movies");
        Step("desktop-detail", () =>
        {
            FindFocusable(GetTree().Root, "Glass Orbit")?.GrabFocus();
            Key(Godot.Key.Enter);
        }, wait: 1.2, tab: "Movies");
        Step("desktop-play", () => Key(Godot.Key.Enter), wait: 2.0, tab: "Movies");
        Step("desktop-player-mini", () => Click(FindAny(GetTree().Root, "Keep playing in the mini player")), wait: 1.5, tab: "Movies");
        Step("desktop-music-mini", () => Key(Godot.Key.Key4, ctrl: true), wait: 1.2, tab: "Music");
        Step("desktop-search", () =>
        {
            Key(Godot.Key.Slash);
            TypeText("lights");
            Key(Godot.Key.Enter);
        }, wait: 1.5);
        Step("desktop-settings", () => Key(Godot.Key.Key6, ctrl: true), wait: 1.2, tab: "Settings");
        Step("desktop-settings-playback", () => Press(Pad.Accept), expect: "Decode video on the GPU");
        Step("desktop-stop", () => PlayerService.Instance.Stop(), wait: 1.0);
        Step("controller-layout", () => Main.Instance.ApplyLayout("auto"), wait: 1.5, tab: "Settings");
    }

    private static void TypeText(string text)
    {
        foreach (var ch in text)
        {
            var k = (Key)char.ToUpperInvariant(ch);
            Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Unicode = ch, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, Unicode = ch, Pressed = false });
        }
        Input.FlushBufferedEvents();
    }

    private static void SetTheme(string id)
    {
        if (Main.Instance.Host is { } host)
        {
            host.SaveSettings(host.Settings with { Theme = id });
            Main.Instance.ApplyTheme(id);
        }
    }

    private static void SetAnimatedBackground(bool on)
    {
        if (Main.Instance.Host is { } host)
        {
            host.SaveSettings(host.Settings with { AnimatedBackground = on });
            Main.Instance.RefreshBackground();
        }
    }

    private void Step(string name, Action act, double wait = 1.0, string? expect = null, string? tab = null) => _steps.Enqueue((name, act, wait, expect, tab));

    private static Control? FindFocusable(Node root, string tooltip)
    {
        foreach (var c in root.GetChildren())
        {
            if (c is Control { FocusMode: Control.FocusModeEnum.All } ctl && ctl.TooltipText.StartsWith(tooltip, StringComparison.Ordinal) && ctl.IsVisibleInTree())
            {
                return ctl;
            }
            if (FindFocusable(c, tooltip) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static void Joy(JoyButton b, bool pressed) =>
        Input.ParseInputEvent(new InputEventJoypadButton { Device = 0, ButtonIndex = b, Pressed = pressed });

    private static void Mouse(Vector2 at, bool pressed) =>
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = at, GlobalPosition = at });

    private static void RightClick(Control? c)
    {
        if (c == null)
        {
            return;
        }
        var at = c.GetViewport().GetFinalTransform() * c.GetGlobalRect().GetCenter();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        Input.FlushBufferedEvents();
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = at, GlobalPosition = at });
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = at, GlobalPosition = at });
        Input.FlushBufferedEvents();
    }

    private static void TouchTap(Control? c)
    {
        if (c == null)
        {
            return;
        }
        var at = c.GetGlobalRect().GetCenter();
        Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        Mouse(at, true);
        Mouse(at, false);
    }

    private static void Tap(JoyButton b, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            Joy(b, true);
            Joy(b, false);
            Input.FlushBufferedEvents();
        }
    }

    private static void Trigger(JoyAxis axis)
    {
        Input.ParseInputEvent(new InputEventJoypadMotion { Device = 0, Axis = axis, AxisValue = 1f });
        Input.FlushBufferedEvents();
        Input.ParseInputEvent(new InputEventJoypadMotion { Device = 0, Axis = axis, AxisValue = 0f });
        Input.FlushBufferedEvents();
    }

    private static void Click(Control? c)
    {
        if (c == null)
        {
            throw new InvalidOperationException("nothing to click");
        }
        if (c is BaseButton b)
        {
            b.EmitSignal(BaseButton.SignalName.Pressed);
            return;
        }
        TouchTap(c);
    }

    /// <summary>Any visible control by tooltip, focusable or not (the player's buttons take no focus).</summary>
    /// <summary>Picks an item of a drop-down (as choosing it from its popup would).</summary>
    private void PickOption(string tooltip, int index)
    {
        if (FindAny(GetTree().Root, tooltip) is not OptionButton ob)
        {
            throw new InvalidOperationException($"no drop-down '{tooltip}'");
        }
        ob.Select(index);
        ob.EmitSignal(OptionButton.SignalName.ItemSelected, index);
    }

    private static Control? FindAny(Node root, string tooltip)
    {
        foreach (var c in root.GetChildren())
        {
            if (c is Control ctl && ctl.TooltipText.StartsWith(tooltip, StringComparison.Ordinal) && ctl.IsVisibleInTree())
            {
                return ctl;
            }
            if (FindAny(c, tooltip) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>Clicks a column title of the desktop table (sorting by it).</summary>
    private void SortColumn(int column)
    {
        var tree = FindTree(GetTree().Root) ?? throw new InvalidOperationException("no table");
        tree.EmitSignal(Tree.SignalName.ColumnTitleClicked, column, (int)MouseButton.Left);
    }

    private static Tree? FindTree(Node root)
    {
        foreach (var c in root.GetChildren())
        {
            if (c is Tree { Visible: true } t)
            {
                return t;
            }
            if (FindTree(c) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static void Stick(float x, float y)
    {
        Input.ParseInputEvent(new InputEventJoypadMotion { Device = 0, Axis = JoyAxis.LeftX, AxisValue = x });
        Input.ParseInputEvent(new InputEventJoypadMotion { Device = 0, Axis = JoyAxis.LeftY, AxisValue = y });
    }

    private static void Key(Key k, bool shift = false, bool ctrl = false)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, ShiftPressed = shift, CtrlPressed = ctrl, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = k, PhysicalKeycode = k, ShiftPressed = shift, CtrlPressed = ctrl, Pressed = false });
        Input.FlushBufferedEvents();
    }

    private static void Press(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
        Input.FlushBufferedEvents();
    }

    public override void _Process(double delta)
    {
        _wait -= delta;
        if (_wait > 0)
        {
            return;
        }
        if (_steps.Count == 0)
        {
            GD.Print($"smoke: done, {_shot} screenshots, {_failures} failures");
            GetTree().Quit(_failures == 0 ? 0 : 1);
            return;
        }
        var (name, act, wait, expect, tab) = _steps.Dequeue();
        try
        {
            act();
        }
        catch (Exception e)
        {
            _failures++;
            GD.PushError($"smoke: step {name} failed: {e}");
        }
        // Give layout and focus a few frames to settle, then capture.
        _wait = wait;
        GetTree().CreateTimer(wait - 0.15).Timeout += () => Capture(name, expect, tab);
    }

    private void Capture(string name, string? expect, string? tab)
    {
        if (tab != null && Main.Instance.CurrentTab != tab)
        {
            _failures++;
            GD.PushError($"smoke: step {name} failed: expected the {tab} tab, got {Main.Instance.CurrentTab}");
        }
        var focus = GetViewport().GuiGetFocusOwner();
        // Tiles draw their title with child labels, so fall back to the tooltip (which has the title).
        var text = focus is Button { Text: { Length: > 0 } t } ? t : focus?.TooltipText ?? "";
        if (expect != null && !text.Contains(expect))
        {
            _failures++;
            GD.PushError($"smoke: step {name} failed: expected focus on '{expect}', got '{text.Split('\n')[0]}'");
        }
        GD.Print($"smoke: {name}: focus={(focus == null ? "none" : focus.GetType().Name + " '" + text.Split('\n')[0] + "'")}");
        var img = GetViewport().GetTexture().GetImage();
        img?.SavePng(Path.Combine(_outDir, $"{++_shot:00}-{name}.png"));
    }
}
