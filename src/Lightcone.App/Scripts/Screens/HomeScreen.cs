using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// Home: a featured item up top (what you were watching, else the newest), then rows: continue
/// watching, recently added, and one per source. Same content in both modes; Desktop uses
/// smaller tiles and a shorter hero.
/// </summary>
public partial class HomeScreen : Screen
{
    private Control? _first;

    public override IEnumerable<(string Action, string Label)> Hints =>
        [(Pad.Secondary, "Surprise me"), (Pad.Tertiary, "Your stats"), (Pad.Switch, "Search")];

    protected override void Build()
    {
        _first = null;
        var lib = Host.Library;
        var rows = lib.HomeRows(Host.Settings.HomeOrder, Host.Settings.HomeHidden).ToList();
        // Downloads, ready to watch offline, right after what you're watching.
        var downloaded = Host.Downloads.Entries.Where(e => e.State == DownloadState.Done).OrderByDescending(e => e.Added)
            .Select(e => lib.Find(e.Item.Id) ?? e.Item).Take(20).ToList();
        if (downloaded.Count > 0)
        {
            var at = rows.FindIndex(r => r.Id == "continue") + 1;
            rows.Insert(at, new HomeRow("downloads", "Downloaded", downloaded, Wide: downloaded.All(i => i.Kind == MediaKind.Episode)));
        }
        if (rows.Count == 0)
        {
            BuildEmpty();
            return;
        }
        var col = Column(Desktop ? 22 : 30);
        AddChild(Faded(Scroller(col)));
        var lead = rows.FirstOrDefault(r => r.Id == "continue") ?? rows[0];
        var featured = lead.Items[0];
        var hero = BuildHero(featured, resume: lead.Id == "continue");
        col.AddChild(hero.Panel);
        var mediaRows = new List<MediaRow>();
        // Back on Home: pick up watchlist changes made in other Plex apps (at most once a minute).
        Host.Plex.SyncWatchlistIfStale(TimeSpan.FromMinutes(1));
        foreach (var row in rows)
        {
            MediaRow r;
            if (row.Id == "watchlist")
            {
                // The whole list (with titles your server doesn't have) is a page of its own.
                var total = lib.Watchlist(notInLibrary: true).Count;
                r = new MediaRow(row, lib, Desktop, MediaActions.Open, seeAll: () => App.Push(new WatchlistPage()), seeAllDetail: $"{total} {(total == 1 ? "title" : "titles")}");
            }
            else
            {
                r = new MediaRow(row, lib, Desktop, row.Id == "continue" ? MediaActions.Resume : MediaActions.Open);
            }
            mediaRows.Add(r);
            col.AddChild(r);
        }
        _first = hero.Play;
        Callable.From(() =>
        {
            if (!IsInstanceValid(this))
            {
                return;
            }
            MediaRow.LinkRows(mediaRows, hero.Play);
            Focus.Row(hero.Buttons, below: mediaRows.FirstOrDefault()?.Tiles.FirstOrDefault());
        }).CallDeferred();
    }

    private (Control Panel, Button Play, List<Control> Buttons) BuildHero(MediaItem item, bool resume)
    {
        var lib = Host.Library;
        var panel = new PanelContainer();
        var box = UiTheme.Box(UiTheme.Panel with { A = 0.72f }, 1, 18, UiTheme.Line);
        box.ContentMarginLeft = box.ContentMarginRight = Desktop ? 18 : 26;
        box.ContentMarginTop = box.ContentMarginBottom = Desktop ? 16 : 22;
        panel.AddThemeStyleboxOverride("panel", box);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", Desktop ? 22 : 30);
        panel.AddChild(row);
        var artSize = Desktop ? new Vector2(320, 180) : new Vector2(400, 225);
        row.AddChild(new PosterArt(item, artSize, PosterTile.ProgressOf(lib, item), 14, showTitle: false));
        var text = Column(Desktop ? 8 : 10);
        text.Alignment = BoxContainer.AlignmentMode.Center;
        row.AddChild(text);
        text.AddChild(UiTheme.Label(resume ? (lib.PositionOf(item) != null ? "CONTINUE WATCHING" : "UP NEXT") : "NEW IN YOUR LIBRARY", UiTheme.SmallSize, UiTheme.AccentText));
        var title = UiTheme.Label(item.Subtitle != null && item.Kind == MediaKind.Episode ? item.Subtitle.Split(" · ")[0] : item.Title,
            Desktop ? UiTheme.TitleSize - 6 : UiTheme.TitleSize, UiTheme.Text);
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        text.AddChild(title);
        var meta = new List<string>();
        if (item.Kind == MediaKind.Episode)
        {
            meta.Add(item.Title);
        }
        if (item.Year is { } y)
        {
            meta.Add(y.ToString());
        }
        if (item.DurationText is { } d)
        {
            meta.Add(d);
        }
        meta.AddRange(item.Genres.Take(2));
        meta.Add(item.SourceName);
        text.AddChild(UiTheme.Label(string.Join("  ·  ", meta), UiTheme.SmallSize + 1, UiTheme.Dim));
        if (Spoilers.Overview(item) is { } o)
        {
            var ov = UiTheme.Label(o, UiTheme.SmallSize + 2, UiTheme.Text, wrap: true);
            ov.MaxLinesVisible = 3;
            ov.CustomMinimumSize = new Vector2(Desktop ? 360 : 420, 0);
            text.AddChild(ov);
        }
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 14);
        text.AddChild(buttons);
        var pos = lib.PositionOf(item);
        var playLabel = pos is { } p ? $"▶  Resume from {Lightcone.Host.Library.MediaItem.FormatDuration(p, clock: true)}" : "▶  Play";
        var play = new ActionButton(playLabel, () =>
        {
            if (item.Playable)
            {
                MediaActions.Play(item);
            }
            else
            {
                MediaActions.PlayContainer(item);
            }
        }, $"{item.Title}: play") { CustomMinimumSize = new Vector2(0, Desktop ? 44 : 54) };
        var details = new ActionButton("Details", () => App.Push(new DetailPage(item)), $"{item.Title}: details") { CustomMinimumSize = new Vector2(0, Desktop ? 44 : 54) };
        var surprise = new ActionButton("Surprise me", () => App.Push(new SurprisePage()), "Surprise me: a random pick from what you haven't watched")
        {
            CustomMinimumSize = new Vector2(0, Desktop ? 44 : 54),
        };
        play.SetMeta(PosterTile.NoHelp, true);
        details.SetMeta(PosterTile.NoHelp, true);
        surprise.SetMeta(PosterTile.NoHelp, true);
        buttons.AddChild(play);
        buttons.AddChild(details);
        buttons.AddChild(surprise);
        var list = new List<Control> { play, details, surprise };
        if (Desktop)
        {
            // Controller mode opens the stats with X (see the hint bar).
            var stats = new ActionButton("Your stats", () => App.Push(new StatsPage()), "Your watch stats") { CustomMinimumSize = new Vector2(0, 44) };
            stats.SetMeta(PosterTile.NoHelp, true);
            buttons.AddChild(stats);
            list.Add(stats);
        }
        return (panel, play, list);
    }

    private void BuildEmpty()
    {
        var center = new CenterContainer();
        AddChild(center);
        var col = Column(16);
        col.CustomMinimumSize = new Vector2(620, 0);
        center.AddChild(col);
        col.AddChild(UiTheme.Label("Nothing to watch yet", UiTheme.TitleSize, UiTheme.Text));
        col.AddChild(UiTheme.Label("Add a folder (SD cards and network shares work too), or sign in to Jellyfin or Plex, and your movies, shows and music show up here.",
            UiTheme.FontSize - 1, UiTheme.Dim, wrap: true));
        var add = new ActionButton("Add a source", () => App.SwitchTab("Sources"), "Add a source: folders, Jellyfin, Plex.");
        var demo = new ActionButton("Try the sample library", () =>
        {
            Host.SaveSettings(Host.Settings with { DemoLibrary = true });
            Host.ApplyDemo(true);
            Rebuild();
        }, "Fills the app with sample movies, shows and music that play without any files.");
        col.AddChild(add);
        col.AddChild(demo);
        Focus.Column([add, demo]);
        _first = add;
    }

    public override void FocusDefault() => _first?.GrabFocus();

    public override void OnAction(string action)
    {
        if (action == Pad.Secondary)
        {
            App.Push(new SurprisePage());
        }
        else if (action == Pad.Tertiary)
        {
            App.Push(new StatsPage());
        }
    }
}
