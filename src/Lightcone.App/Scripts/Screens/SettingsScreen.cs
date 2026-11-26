using Lightcone.App.Playback;
using Lightcone.Host;
using Lightcone.Host.Library;
using Lightcone.Host.Updates;
using Lightcone.App.Ui;
using Lightcone.Playback;
using Godot;

namespace Lightcone.App.Screens;

/// <summary>Settings in two panes (see <see cref="TwoPaneScreen"/>).</summary>
public partial class SettingsScreen : TwoPaneScreen
{
    private static readonly (string Name, string Summary)[] CategoryList =
    [
        ("Playback", "Hardware decoding, languages, subtitles and seeking."),
        ("Library", "Music, collections and the watchlist."),
        ("Appearance", "Theme, accent colour, CRT effect and animations."),
        ("Sound & feel", "Menu sounds and controller rumble."),
        ("Layout", "Controller or desktop layout, and keyboard shortcuts."),
        ("Updates", "Version, changelog and updates."),
        ("About", "Where Lightcone keeps its files, and the app log."),
    ];

    protected override string Heading => "Settings";

    protected override IReadOnlyList<(string Name, string Summary)> Categories => CategoryList;

    protected override void BuildCategory(string name, VBoxContainer col, Action<Control> Add)
    {
        switch (name)
        {
            case "Playback":
                BuildPlayback(col, Add);
                break;
            case "Library":
                BuildLibrary(col, Add);
                break;
            case "Appearance":
                BuildAppearance(col, Add);
                break;
            case "Sound & feel":
                BuildFeedback(col, Add);
                break;
            case "Layout":
                BuildLayout(col, Add);
                break;
            case "Updates":
                BuildUpdates(col, Add);
                break;
            case "About":
                BuildAbout(col, Add);
                break;
        }
    }

    private void BuildLibrary(VBoxContainer col, Action<Control> Add)
    {
        Add(Toggle.Create("Music", Host.Settings.MusicTab, on => App.ApplyMusicTab(on),
            "Lightcone is about movies and TV first. Music adds its own tab, Home rows and search results.",
            "Albums and tracks from your sources get a Music tab.",
            "No Music tab, and music stays out of Home and search. Your music isn't removed."));
        Add(Toggle.Create("Automatic collections", Host.Settings.AutoCollections, on => Host.SaveSettings(Host.Settings with { AutoCollections = on }),
            "Groups movies and shows by genre and decade, next to your own and your servers' collections.",
            "Movies and Shows list collections like \"Sci-Fi movies\" and \"1990s movies\".",
            "Only collections you made and your servers' collections."));
        var lib = Host.Library;
        var watch = lib.Watchlist().Count;
        var mine = lib.Lists.Collections.Count;
        col.AddChild(UiTheme.Label($"Watchlist: {watch} {(watch == 1 ? "title" : "titles")}. Collections you made: {mine}. " +
            (Host.Plex.Account.Token != null ? "Your Plex watchlist syncs both ways." : "Sign in to Plex in Sources to sync your Plex watchlist."),
            UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
    }

    private string? _pendingTheme;
    private string? _pendingBase;
    private Dictionary<string, string>? _pendingAccents;

    private static readonly (string Code, string Label)[] LanguageChoices =
    [
        ("en", "English"), ("sv", "Swedish"), ("ja", "Japanese"), ("de", "German"), ("fr", "French"), ("es", "Spanish"),
        ("it", "Italian"), ("pt", "Portuguese"), ("nl", "Dutch"), ("no", "Norwegian"), ("da", "Danish"), ("fi", "Finnish"),
        ("pl", "Polish"), ("ru", "Russian"), ("zh", "Chinese"), ("ko", "Korean"),
    ];

    private void BuildPlayback(VBoxContainer col, Action<Control> Add)
    {
        var s = Host.Settings;
        var engine = PlayerService.EngineAvailable(out var why)
            ? $"Playback engine: libmpv {MpvLibrary.ApiVersion} from {MpvLibrary.LoadedFrom}"
            : "Playback engine not found: " + why;
        col.AddChild(UiTheme.Label(engine, UiTheme.SmallSize + 1, PlayerService.EngineAvailable(out _) ? UiTheme.Dim : UiTheme.Warn, wrap: true));
        if (Codecs.MissingInLoadedLibrary() is { } missing)
        {
            var formats = missing.Count == 0
                ? $"Decodes every supported format: {string.Join(", ", Codecs.Required.SelectMany(g => g.Codecs).Select(Codecs.Display).Distinct())}"
                : "This libmpv can't decode: " + string.Join(", ", missing.Select(Codecs.Display));
            col.AddChild(UiTheme.Label(formats, UiTheme.SmallSize, missing.Count == 0 ? UiTheme.Dim : UiTheme.Warn, wrap: true));
        }

        var hw = new Stepper("Hardware decoding", HwdecPolicy.Choices, s.Hwdec, "Decode video on the GPU. Applies to the next thing you play.")
        {
            Expands = true,
            DetailFor = HwdecPolicy.Describe,
        };
        hw.Changed += v => Host.SaveSettings(Host.Settings with { Hwdec = v });
        Add(hw);

        var audio = new Stepper("Audio language", LanguageChoices.Select(l => (l.Code, l.Label)), s.AudioLanguages.FirstOrDefault() ?? "en",
            "Picked first when a file or server item has several audio tracks. You can still switch while watching.")
        {
            Expands = true,
        };
        audio.Changed += v => Host.SaveSettings(Host.Settings with { AudioLanguages = [v] });
        Add(audio);

        var subs = new Stepper("Subtitles", [("", "Off (forced only)"), .. LanguageChoices.Select(l => (l.Code, l.Label))], s.SubtitleLanguages.FirstOrDefault() ?? "",
            "Shown automatically in this language. Off still shows forced subtitles (signs, foreign dialogue).")
        {
            Expands = true,
        };
        subs.Changed += v => Host.SaveSettings(Host.Settings with { SubtitleLanguages = v == "" ? [] : [v] });
        Add(subs);

        var size = new Stepper("Subtitle size", [("75", "Small"), ("100", "Normal"), ("125", "Large"), ("150", "Extra large")],
            new[] { 75, 100, 125, 150 }.OrderBy(x => Math.Abs(x - s.SubtitleScale)).First().ToString(),
            "Scales text subtitles (SRT, ASS). Picture subtitles (PGS, VobSub) keep their own size.")
        {
            Expands = true,
        };
        size.Changed += v => Host.SaveSettings(Host.Settings with { SubtitleScale = int.Parse(v) });
        Add(size);

        Stepper SeekStepper(string label, int value, int[] steps, string help, Func<int, AppSettings> apply)
        {
            var st = new Stepper(label, steps.Select(x => (x.ToString(), x < 60 ? $"{x} seconds" : $"{x / 60} minute{(x >= 120 ? "s" : "")}")).ToArray(),
                steps.OrderBy(x => Math.Abs(x - value)).First().ToString(), help)
            {
                Expands = true,
            };
            st.Changed += v => Host.SaveSettings(apply(int.Parse(v)));
            return st;
        }
        Add(SeekStepper("Skip back", s.SeekBack, [5, 10, 15, 30, 60], "How far left trigger, Left or J jumps back. Hold it to go faster.",
            v => Host.Settings with { SeekBack = v }));
        Add(SeekStepper("Skip forward", s.SeekForward, [10, 15, 30, 60, 120], "How far right trigger, Right or L jumps ahead. Hold it to go faster.",
            v => Host.Settings with { SeekForward = v }));

        Add(new ActionButton("Play a file or URL…", () => App.Push(new TextEntryPage("File or URL", "", PlayPath,
            "A path on this computer (SD card and network shares too) or a stream URL.")),
            "Opens anything mpv can play, without adding it to the library."));
    }

    private static void PlayPath(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0)
        {
            return;
        }
        var item = new MediaItem
        {
            Id = "file:" + path,
            Title = System.IO.Path.GetFileName(path.TrimEnd('/')) is { Length: > 0 } n ? n : path,
            Kind = MediaKind.Movie,
            SourceId = "file",
            SourceName = "File",
            PlayUrl = path,
        };
        PlayerService.Instance.Play(item);
        Main.Instance.ShowPlayer();
    }

    private void BuildLayout(VBoxContainer col, Action<Control> Add)
    {
        var current = Host.Settings.Layout;
        var layout = new Stepper("Layout", UiLayout.Choices.Select(c => (c.Id, c.Label)), UiLayout.Choices.Any(c => c.Id == current) ? current : UiLayout.Auto,
            "Controller: full screen, big rows. Desktop: a resizable window with more on screen.")
        {
            Expands = true,
            DetailFor = UiLayout.Describe,
        };
        layout.Changed += v =>
        {
            App.ApplyLayout(v);
            Callable.From(RefreshPane).CallDeferred();
        };
        Add(layout);
        col.AddChild(Section("Keyboard and mouse"));
        foreach (var line in new[]
        {
            "Enter or double-click: open / play.  Esc or Backspace: back.  Tab: next section.",
            "Ctrl+1 to Ctrl+6: Home, Movies, Shows, Music, Sources, Settings.",
            "/ or Ctrl+F: search.  M: options for the selected item.",
            "In the player: Space pause, J / L or ← / → seek, A audio, S subtitles, F full screen, Esc leave.",
            "Controller: A play, B back, bumpers switch sections, triggers seek, Y sort, Start options.",
        })
        {
            col.AddChild(UiTheme.Label("•  " + line, UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        }
    }

    private void BuildFeedback(VBoxContainer col, Action<Control> Add)
    {
        col.AddChild(UiTheme.Label("Each theme has its own set of sounds. They never play over a video or music.",
            UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        var levels = new List<(string, string)> { ("0", "Off"), ("25", "Quiet"), ("50", "Medium"), ("75", "Loud"), ("100", "Full") };
        var current = Host.Settings.SoundVolume;
        var nearest = levels.OrderBy(l => Math.Abs(int.Parse(l.Item1) - current)).First().Item1;
        var sound = new Stepper("Menu sounds", levels, nearest, "Short sounds when you move, press, go back or switch tabs.")
        {
            Expands = true,
            DetailFor = v => v == "0" ? "Silent menus." : "Plays through your speakers or headphones at this level.",
        };
        sound.Changed += v =>
        {
            Host.SaveSettings(Host.Settings with { SoundVolume = int.Parse(v) });
            App.ApplyFeedback(Host.Settings.SoundVolume, Host.Settings.Haptics);
            Lightcone.App.Ui.Feedback.Play(Lightcone.App.Ui.Cue.Confirm);  // a sample at the new level
        };
        Add(sound);
        Add(Toggle.Create("Controller rumble", Host.Settings.Haptics, on =>
        {
            Host.SaveSettings(Host.Settings with { Haptics = on });
            App.ApplyFeedback(Host.Settings.SoundVolume, on);
            Lightcone.App.Ui.Feedback.Play(Lightcone.App.Ui.Cue.Confirm);
        }, "A light buzz in your hands for menu moves, presses and errors. On a Steam Deck it uses the trackpad haptics when Steam Input passes rumble through.",
            "Rumbles on menu actions.", "No rumble in the menus."));
    }

    private void BuildAppearance(VBoxContainer col, Action<Control> Add)
    {
        // Theme and accent are picked here and applied by restarting the app, which rebuilds every
        // cached style and font in one go.
        var active = UiTheme.Name;
        if (_pendingBase != active)
        {
            _pendingBase = active;
            _pendingTheme = null;
            _pendingAccents = null;
        }
        _pendingTheme ??= active;
        _pendingAccents ??= new Dictionary<string, string>(Host.Settings.Accents);
        var themeId = _pendingTheme;
        var theme = new Stepper("Theme", UiTheme.Themes.Select(t => (t.Id, t.Label + (t.Id == active ? "  (current)" : ""))), themeId,
            "Pick a theme, then choose Restart to apply. The app restarts and comes back here.")
        {
            Expands = true,
            DetailFor = v => UiTheme.Themes.FirstOrDefault(t => t.Id == v)?.Blurb,
        };
        theme.Changed += v =>
        {
            _pendingTheme = v;
            RefreshPane();  // the accent row's choices depend on the theme
        };
        Add(theme);
        var pocket = themeId == "pocket";
        var accent = new Stepper(pocket ? "Screen colour" : "Accent colour",
            [("", pocket ? "Classic green" : "Theme's own"), .. UiTheme.SwatchesFor(UiTheme.Themes.First(t => t.Id == themeId))],
            _pendingAccents.GetValueOrDefault(themeId) ?? "",
            pocket ? "Pocket LCD's four shades, in another screen colour. Saved for this theme." : "The highlight colour for this theme. Each theme remembers its own.")
        {
            Expands = true,
            DetailFor = v => v == ""
                ? (pocket ? "The original pea-green screen." : "The colour this theme was designed with.")
                : (pocket ? "All four shades change together, so it still looks like one screen." : "Focus, selected values and highlights use this colour."),
        };
        accent.Changed += v =>
        {
            if (v == "")
            {
                _pendingAccents.Remove(themeId);
            }
            else
            {
                _pendingAccents[themeId] = v;
            }
            RefreshPane();
        };
        Add(accent);
        var changed = themeId != active ||
                      (_pendingAccents.GetValueOrDefault(themeId) ?? "") != (Host.Settings.Accents.GetValueOrDefault(themeId) ?? "");
        if (changed)
        {
            var label = UiTheme.Themes.First(t => t.Id == themeId).Label;
            Add(new ActionButton($"↻  Restart to apply {label}", () =>
            {
                Host.SaveSettings(Host.Settings with { Theme = themeId, Accents = new Dictionary<string, string>(_pendingAccents) });
                App.RestartApp("--open=appearance");
            }, "Saves the theme and restarts the app. You come back to this page."));
        }
        Add(Toggle.Create("Animated background", Host.Settings.AnimatedBackground, on =>
        {
            Host.SaveSettings(Host.Settings with { AnimatedBackground = on });
            App.RefreshBackground();
        }, "A slow animation behind the menus that matches the theme. It stops while something plays full screen.",
            "Each theme has its own: drifting lights, a pulsing grid, phosphor rain, pixel clouds, a scrolling floor, an LCD shimmer or spinning shapes.",
            "A plain background. Uses a little less battery."));
        var crt = new Stepper("CRT effect", [("off", "Off"), ("1", "Subtle"), ("2", "Medium"), ("3", "Strong")],
            Host.Settings.Crt ? Host.Settings.CrtLevel.ToString() : "off",
            "Makes the whole screen look like an old TV. Works with every theme.")
        {
            Expands = true,
            DetailFor = v => v switch
            {
                "off" => "No effect. Retro terminal still has its own light scanlines.",
                "1" => "Faint scanlines, a slight wobble and a gentle tube curve.",
                "2" => "Scanlines, a wobbling picture, a rolling band, colour fringes and dark edges.",
                _ => "The full old-TV look. Text gets a little softer.",
            },
        };
        crt.Changed += v =>
        {
            Host.SaveSettings(v == "off" ? Host.Settings with { Crt = false } : Host.Settings with { Crt = true, CrtLevel = int.Parse(v) });
            App.RefreshBackground();
        };
        Add(crt);
        Add(Toggle.Create("Animations", !Host.Settings.ReduceMotion, on =>
        {
            Host.SaveSettings(Host.Settings with { ReduceMotion = !on });
            Anim.Enabled = on;
        }, "Sliding focus, smooth scrolling and page transitions.", null,
            "Everything changes instantly, nothing slides or fades."));
    }

    private void BuildUpdates(VBoxContainer col, Action<Control> Add)
    {
        var updater = Host.Updater;
        col.AddChild(UiTheme.Label($"Lightcone {updater.CurrentDisplay}", UiTheme.FontSize + 6, UiTheme.Text));
        Add(new ActionButton("Changelog", App.ShowChangelog, "What changed in each version of Lightcone."));
        if (updater.CanSelfUpdate)
        {
            Add(new ActionButton("Check for updates", () => App.CheckForAppUpdate(silent: false),
                "Looks for a newer Lightcone on GitHub. Updates install in place and keep your library, progress and settings."));
            var channel = new Stepper("Update channel", [("stable", "Releases"), ("test", "Test builds")], updater.TestChannel ? "test" : "stable",
                "Releases: only finished versions. Test builds: every build published for testing too, so you get fixes first.")
            {
                Expands = true,
            };
            channel.Changed += v =>
            {
                Host.SaveSettings(Host.Settings with { UpdateChannel = v });
                updater.TestChannel = v == "test";
            };
            Add(channel);
            Add(Toggle.Create("Check at startup", Host.Settings.CheckForUpdates,
                on => Host.SaveSettings(Host.Settings with { CheckForUpdates = on }),
                "Asks when a new version is out. Never interrupts playback."));
            if (updater.CanRollback)
            {
                var prev = updater.PreviousVersion is { } pv ? $" ({pv})" : "";
                Add(new ActionButton($"Roll back to previous version{prev}", () => App.Confirm(
                    $"Go back to the previous Lightcone{prev}? The app restarts.", "Roll back", App.RollbackApp)));
            }
        }
        if (updater.CanSelfUpdate)
        {
            var dir = updater.InstallDir;
            var added = Try(() => SteamSetup.IsAdded(Host.Paths.Home, dir), false, "Steam's shortcuts", col);
            Add(new ActionButton(added ? "Add Lightcone to Steam again (refresh art)" : "Add Lightcone to Steam", () => AddToSteam(dir),
                $"A Steam shortcut with Lightcone's own art, so it opens from Game Mode and Big Picture."));
        }
        else
        {
            col.AddChild(UiTheme.Label("This copy can't update itself (development build or package). Use the installer to update.",
                UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        }
    }

    private void AddToSteam(string dir)
    {
        var home = Host.Paths.Home;
        if (!Lightcone.Core.Steam.SteamShortcuts.SteamRunning(home))
        {
            if (App.Guard(() => SteamSetup.Add(home, dir)))
            {
                App.Success("Lightcone is in your Steam library.");
            }
            return;
        }
        if (SteamSetup.InGameMode)
        {
            App.Push(new ChoicePage("Steam reads shortcuts only when it starts, and it can't be closed from Game Mode. Switch to Desktop Mode and add Lightcone from there, or run the installer again.",
                ("OK", () => { })));
            return;
        }
        App.Push(new ChoicePage("Steam reads shortcuts only when it starts. Lightcone can close Steam, add itself, and start Steam again.",
            ("Close Steam and add Lightcone", () => App.Busy("Adding Lightcone to Steam…", (_, _) => Task.Run(() =>
            {
                if (!SteamSetup.CloseSteam(home, TimeSpan.FromSeconds(30)))
                {
                    throw new InvalidOperationException("Steam didn't close. Exit it yourself (Steam → Exit), then try again.");
                }
                var n = SteamSetup.Add(home, dir);
                SteamSetup.StartSteam();
                return n;
            }), _ => App.Success("Lightcone is in your Steam library."), cancellable: false)),
            ("Cancel", () => { })));
    }

    private void BuildAbout(VBoxContainer col, Action<Control> Add)
    {
        col.AddChild(UiTheme.Label("Lightcone only reads your media. Its own data (settings, library index, watch progress, artwork cache) lives in these folders.",
            UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        foreach (var (label, path) in new[]
        {
            ("Settings", Host.Paths.Config),
            ("Watch progress", Host.Paths.ProgressFile),
            ("Artwork cache", Host.Paths.ArtCacheDir),
            ("App log", AppLog.FilePath ?? "(not open)"),
        })
        {
            col.AddChild(UiTheme.Label($"{label}:  {path}", UiTheme.SmallSize + 1, UiTheme.Text, wrap: true));
        }
        Add(new ActionButton("Open the app log", () => App.Push(new LogPage()), "The app's own log: navigation, playback (decoder, tracks) and errors."));
    }
}
