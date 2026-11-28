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
        ("Playback", "Decoding, languages, subtitles, seeking, sound and resume."),
        ("Library", "Music, collections, spoilers and your stats."),
        ("Home", "Which rows Home shows, and in what order."),
        ("Phone remote", "Control Lightcone from a phone's browser on the same network."),
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
            case "Home":
                BuildHome(col, Add);
                break;
            case "Phone remote":
                BuildRemote(col, Add);
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
            "Makes franchise, director, genre and decade collections, next to your own and your servers' collections.",
            "Movies and Shows list collections like \"Sci-Fi movies\" and \"1990s movies\".",
            "Only collections you made and your servers' collections."));
        Add(Toggle.Create("Spoiler-free", Host.Settings.HideSpoilers, on => Host.SaveSettings(Host.Settings with { HideSpoilers = on }),
            "Hides thumbnails and summaries of episodes you haven't watched yet, so a still or a synopsis doesn't give the plot away.",
            "Unwatched episodes show only their number and title.", "Every episode shows its thumbnail and summary."));
        Add(new ActionButton("Your stats", () => App.Push(new StatsPage()), "Hours watched, streaks, top shows and genres (Home: X)."));
        var dl = Host.Downloads.Entries.Count(e => e.State == DownloadState.Done);
        Add(new ActionButton($"Downloads ({dl})", () => App.Push(new DownloadsPage()), "Movies and episodes saved on this device to watch offline."));
        var places = DownloadPlaces();
        if (places.Count > 1)
        {
            var current = Host.Settings.DownloadFolder ?? "";
            var where = new Stepper("Download to", places, places.Any(p => p.Item1 == current) ? current : "",
                "Where new downloads go. On a Steam Deck, pick the SD card to save the internal drive.")
            {
                Expands = true,
                Wrap = false,
            };
            where.Changed += v => Host.SaveSettings(Host.Settings with { DownloadFolder = v == "" ? null : v });
            Add(where);
        }
        var lib = Host.Library;
        var watch = lib.Watchlist().Count;
        var mine = lib.Lists.Collections.Count;
        col.AddChild(UiTheme.Label($"Watchlist: {watch} {(watch == 1 ? "title" : "titles")}. Collections you made: {mine}. " +
            (Host.Plex.Account.Token != null ? "Your Plex watchlist syncs both ways." : "Sign in to Plex in Sources to sync your Plex watchlist."),
            UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
    }

    private void BuildRemote(VBoxContainer col, Action<Control> Add)
    {
        var remote = PhoneRemote.Instance;
        Add(Toggle.Create("Phone remote", Host.Settings.RemoteEnabled, on =>
        {
            Host.SaveSettings(Host.Settings with { RemoteEnabled = on });
            remote?.Apply();
            Callable.From(Rebuild).CallDeferred();
        }, "A remote in your phone's browser: D-pad, play / pause, skip and volume. Only on this network, and only with the PIN.",
            "Scan the code with your phone's camera.", "Off: nothing listens on the network."));
        if (remote is not { Running: true })
        {
            return;
        }
        var urls = remote.Urls;
        col.AddChild(UiTheme.Label($"PIN  {Host.Settings.RemotePin}", UiTheme.TitleSize - 6, UiTheme.Accent));
        if (urls.Count == 0)
        {
            col.AddChild(UiTheme.Label($"No network found. Connect to Wi-Fi, then open http://<this device's address>:{remote.Port}/ on your phone.",
                UiTheme.SmallSize + 1, UiTheme.Warn, wrap: true));
        }
        else
        {
            col.AddChild(UiTheme.Label("On your phone (same Wi-Fi), scan this or open " + string.Join("  or  ", urls), UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
            if (QrCode.Texture(urls[0], 6) is { } tex)
            {
                col.AddChild(new TextureRect
                {
                    Texture = tex,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    CustomMinimumSize = new Vector2(220, 220),
                    SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                    TextureFilter = TextureFilterEnum.Nearest,
                });
            }
        }
        Add(new ActionButton("New PIN", () =>
        {
            remote.NewPin();
            Rebuild();
        }, "Make a new PIN; phones that used the old one ask for it again."));
    }

    private void BuildHome(VBoxContainer col, Action<Control> Add)
    {
        Add(new ActionButton("Arrange Home rows…", () => App.Push(new HomeLayoutPage()),
            "Show, hide and reorder Home's rows: Continue watching, Because you watched, Top picks, Recently added and more."));
        var hidden = Host.Settings.HomeHidden.Count;
        col.AddChild(UiTheme.Label(hidden == 0 ? "Every row is shown." : $"{hidden} {(hidden == 1 ? "row is" : "rows are")} hidden.",
            UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
    }

    /// <summary>This device's storage for downloads ("" = Lightcone's data folder), plus SD cards and other drives.</summary>
    private static List<(string, string)> DownloadPlaces()
    {
        var list = new List<(string, string)> { ("", "This device (Lightcone's folder)") };
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var user = System.Environment.UserName;
                foreach (var root in new[] { $"/run/media/{user}", "/run/media/deck", "/media", $"/media/{user}" }.Distinct())
                {
                    if (!System.IO.Directory.Exists(root))
                    {
                        continue;
                    }
                    foreach (var d in System.IO.Directory.GetDirectories(root))
                    {
                        if (d == $"/media/{user}")
                        {
                            continue;
                        }
                        list.Add((System.IO.Path.Combine(d, "Lightcone"), "SD card / drive: " + System.IO.Path.GetFileName(d)));
                    }
                }
            }
            else
            {
                foreach (var drive in System.IO.DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is System.IO.DriveType.Removable or System.IO.DriveType.Fixed))
                {
                    var path = System.IO.Path.Combine(drive.RootDirectory.FullName, "Lightcone Downloads");
                    if (OperatingSystem.IsMacOS() && !drive.RootDirectory.FullName.StartsWith("/Volumes/", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    list.Add((path, $"{drive.Name.TrimEnd('\\', '/')} ({drive.AvailableFreeSpace / (1L << 30)} GB free)"));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Info($"download places: {e.Message}");
        }
        return list.DistinctBy(p => p.Item1).ToList();
    }

    private string? _pendingTheme;
    private string? _pendingBase;
    private Dictionary<string, string>? _pendingAccents;

    internal static readonly (string Code, string Label)[] LanguageChoices =
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
        size.Changed += v =>
        {
            Host.SaveSettings(Host.Settings with { SubtitleScale = int.Parse(v) });
            PlayerService.Instance.ApplySubtitleLook();
        };
        Add(size);

        Stepper Look(string label, IEnumerable<(string, string)> choices, string value, string help, Func<string, AppSettings> apply)
        {
            var st = new Stepper(label, choices, value, help) { Expands = true };
            st.Changed += v =>
            {
                Host.SaveSettings(apply(v));
                PlayerService.Instance.ApplySubtitleLook();
            };
            return st;
        }
        Add(Look("Subtitle colour", SubtitleLook.Colors.Select(c => (c.Id, c.Label)), s.SubtitleColor,
            "Text colour for text subtitles.", v => Host.Settings with { SubtitleColor = v }));
        Add(Look("Subtitle style", SubtitleLook.Styles.Select(c => (c.Id, c.Label)), s.SubtitleStyle,
            "Outline is easiest on most films; a dark box reads best over bright scenes.", v => Host.Settings with { SubtitleStyle = v }));
        Add(Look("Subtitle height", [("100", "Bottom"), ("95", "A little higher"), ("90", "Higher"), ("80", "Much higher")],
            new[] { 100, 95, 90, 80 }.OrderBy(x => Math.Abs(x - s.SubtitlePosition)).First().ToString(),
            "Lift subtitles off the bottom edge (handy on TVs that crop, or with burnt-in captions).", v => Host.Settings with { SubtitlePosition = int.Parse(v) }));
        Add(Toggle.Create("Bold subtitles", s.SubtitleBold, on =>
        {
            Host.SaveSettings(Host.Settings with { SubtitleBold = on });
            PlayerService.Instance.ApplySubtitleLook();
        }, "Heavier text for text subtitles.", "Subtitles are bold.", "Normal weight."));

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
        Add(Toggle.Create("Skip intros automatically", Host.Settings.AutoSkipIntro, on => Host.SaveSettings(Host.Settings with { AutoSkipIntro = on }),
            "When the server marks an episode's intro, jump past it without pressing anything.",
            "Intros are skipped by themselves (a note says so).", "A Skip intro button shows; press Y to skip."));

        Stepper Choice(string label, (int Value, string Label)[] choices, int value, string help, Func<int, AppSettings> apply, Func<string, string?>? detail = null)
        {
            var st = new Stepper(label, choices.Select(c => (c.Value.ToString(), c.Label)), choices.OrderBy(c => Math.Abs(c.Value - value)).First().Value.ToString(), help)
            {
                Expands = true,
                Wrap = false,
                DetailFor = detail,
            };
            st.Changed += v => Host.SaveSettings(apply(int.Parse(v)));
            return st;
        }
        Add(Choice("Up next countdown", [(0, "Off"), (5, "5 seconds"), (10, "10 seconds"), (15, "15 seconds"), (20, "20 seconds")], s.UpNextSeconds,
            "In the end credits (or 20 seconds before the end), a card counts down to the next episode. A plays it now, B lets the credits run.",
            v => Host.Settings with { UpNextSeconds = v },
            v => v == "0" ? "No card: the player stops after each episode." : null));
        Add(Choice("Still watching?", [(0, "Never ask"), (2, "After 2 episodes"), (3, "After 3 episodes"), (5, "After 5 episodes")], s.StillWatchingAfter,
            "After this many episodes play by themselves with no button pressed, Lightcone asks before going on, so a show doesn't run all night.",
            v => Host.Settings with { StillWatchingAfter = v }));
        Add(Choice("Recap after a break", [(-1, "Off"), (0, "Every time"), (3, "After 3 days"), (7, "After a week"), (14, "After 2 weeks")], s.RecapAfterDays,
            "Back to a show after a while? Before the next episode, see what happened in the last one, and rewatch its last two minutes if you like.",
            v => Host.Settings with { RecapAfterDays = v }));

        var qualities = QualityPolicy.Choices.Select(q => (q.Id, q.Label)).ToList();
        var home = new Stepper("Quality at home", qualities, QualityPolicy.Find(s.Quality).Id,
            "For servers on this network. Original plays the file as is; a bitrate has the server convert it (smaller, any device can play it).")
        {
            Expands = true,
            Wrap = false,
            DetailFor = QualityPolicy.Describe,
        };
        home.Changed += v => Host.SaveSettings(Host.Settings with { Quality = v });
        Add(home);
        var away = new Stepper("Quality away from home", qualities, QualityPolicy.Find(s.QualityAway).Id,
            "For servers reached over the internet or Plex's relay (on the go, at a friend's). Lower keeps it smooth on slow connections.")
        {
            Expands = true,
            Wrap = false,
            DetailFor = QualityPolicy.Describe,
        };
        away.Changed += v => Host.SaveSettings(Host.Settings with { QualityAway = v });
        Add(away);

        Add(Toggle.Create("Smart resume", s.SmartResume, on => Host.SaveSettings(Host.Settings with { SmartResume = on }),
            "Picking up where you left off backs up a little so you hear the last line again: 5 seconds, more after a long break.",
            "Rewinds 5 s, 10 s after a day away, 20 s after three days, 30 s after a week.", "Resumes at the exact second you stopped."));
        Add(Toggle.Create("Night mode", s.NightMode, on => PlayerService.Instance.SetNightMode(on),
            "Evens out the volume: quiet dialogue comes up, explosions come down. For late nights and thin walls.",
            "Loud and quiet parts are brought closer together.", "The soundtrack plays as mixed."));
        Add(Toggle.Create("Clear voices", s.ClearVoices, on => PlayerService.Instance.SetClearVoices(on),
            "Lifts dialogue over music and effects. With surround sound it pulls the centre (voice) channel forward.",
            "Dialogue is boosted.", "The soundtrack plays as mixed."));

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
        var lang = new Stepper("Interface language", Loc.Choices, Host.Settings.Language,
            "Menus, the player and Settings. Rarer texts may still be in English.") { Expands = true };
        lang.Changed += v =>
        {
            Host.SaveSettings(Host.Settings with { Language = v });
            Loc.Apply(v);
            Callable.From(Rebuild).CallDeferred();
        };
        Add(lang);
        var glyphs = new Stepper("Button glyphs",
            [("auto", "Detect"), ("xbox", "Xbox (A B X Y)"), ("playstation", "PlayStation (✕ ○ □ △)"), ("nintendo", "Nintendo (B A Y X)"), ("deck", "Steam Deck")],
            Host.Settings.GlyphStyle,
            "Which buttons the hints show. Detect reads the real controller, also behind Steam Input.")
        {
            Expands = true,
            DetailFor = v => v == "auto" ? "Now: " + Pad.GlyphFamilyName() : null,
        };
        glyphs.Changed += v =>
        {
            Host.SaveSettings(Host.Settings with { GlyphStyle = v });
            Pad.GlyphStyle = v;
        };
        Add(glyphs);
        col.AddChild(Section("Display"));
        col.AddChild(UiTheme.Label("Drawing at " + DisplayCheck.Describe() + ".", UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        if (DisplayCheck.SteamResolutionTip() is { } tip)
        {
            col.AddChild(UiTheme.Label(tip, UiTheme.SmallSize + 1, UiTheme.Warn, wrap: true));
        }
        col.AddChild(Section("Keyboard and mouse"));
        foreach (var line in new[]
        {
            "Enter or double-click: open / play.  Esc or Backspace: back.  Tab: next section.",
            "Ctrl+1 to Ctrl+6: Home, Movies, Shows, Music, Sources, Settings.",
            "/ or Ctrl+F: search.  M: options for the selected item.",
            "In the player: Space pause, J / L or ← / → seek, A audio, S subtitles, F full screen, Esc leave.",
            "Controller: A play, B back, bumpers switch sections, triggers seek, Y sort & filter, Start options. Home: Y surprise me, X your stats.",
        })
        {
            col.AddChild(UiTheme.Label("•  " + line, UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        }
    }

    private void BuildFeedback(VBoxContainer col, Action<Control> Add)
    {
        var ambient = new Stepper("Ambient mode", [("0", "Off"), ("2", "After 2 minutes"), ("5", "After 5 minutes"), ("10", "After 10 minutes"), ("20", "After 20 minutes")],
            new[] { 0, 2, 5, 10, 20 }.OrderBy(x => Math.Abs(x - Host.Settings.AmbientMinutes)).First().ToString(),
            "Left alone on the menus, the screen turns into a slow slideshow of your library with a clock. Any button wakes it.")
        {
            Expands = true,
            Wrap = false,
        };
        ambient.Changed += v => Host.SaveSettings(Host.Settings with { AmbientMinutes = int.Parse(v) });
        Add(ambient);
        Add(new ActionButton("Show ambient mode now", () => App.ShowAmbient(), "Try it: any button brings the menus back."));
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
        Add(Toggle.Create("Projector intro", Host.Settings.ProjectorIntro, on => Host.SaveSettings(Host.Settings with { ProjectorIntro = on }),
            "While a movie or episode loads, a film reel drops onto a projector and the beam comes on, with a viewing tip. It never delays playback.",
            "Reel, clicks and a tip while loading.", "Straight to the picture."));
        Add(Toggle.Create("Did you know?", Host.Settings.Trivia, on => Host.SaveSettings(Host.Settings with { Trivia = on }),
            "A behind-the-scenes fact on movie and show pages, from Wikipedia. Only the title and year are looked up.",
            "Facts from Wikipedia on detail pages.", "No trivia box."));
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
