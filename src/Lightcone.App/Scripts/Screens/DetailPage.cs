using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// An item's page: art, facts, overview and actions. Shows list their episodes, albums their
/// tracks. Used as is in both modes (Desktop only shrinks it).
/// </summary>
public partial class DetailPage : Screen
{
    private readonly MediaItem _item;
    private Control? _first;

    // Shows: the season strip, the picked season and its episode list (swapped in place).
    private IReadOnlyList<ShowSeason> _seasons = [];
    private int? _seasonKey;
    private MediaRow? _strip;
    private VBoxContainer? _episodes;
    private List<Control> _actionButtons = [];

    public DetailPage(MediaItem item) => _item = item;

    private bool Listable => _item.Kind is MediaKind.Movie or MediaKind.Show;

    private bool HasSeasons => _item.Kind == MediaKind.Show && _seasons.Count > 0;

    private ShowSeason? CurrentSeason => _seasons.FirstOrDefault(x => x.Key == _seasonKey) ?? _seasons.FirstOrDefault();

    public override IEnumerable<(string Action, string Label)> Hints
    {
        get
        {
            var hints = new List<(string, string)>();
            if (_item.Playable)
            {
                hints.Add((Pad.Secondary, Host.Library.IsWatched(_item) ? "Mark unwatched" : "Mark watched"));
            }
            else if (HasSeasons && CurrentSeason is { } season)
            {
                hints.Add((Pad.Secondary, SeasonWatched(season) ? "Season unwatched" : "Season watched"));
            }
            if (Listable)
            {
                hints.Add((Pad.Tertiary, Host.Library.OnWatchlist(_item) ? "Remove from watchlist" : "Add to watchlist"));
            }
            if (HasSeasons && _seasons.Count > 1)
            {
                hints.Add((Pad.SeekBack, "Previous season"));
                hints.Add((Pad.SeekForward, "Next season"));
            }
            return hints;
        }
    }

    protected override void Build()
    {
        _first = null;
        _strip = null;
        _episodes = null;
        var lib = Host.Library;
        var col = Column(Desktop ? 16 : 22);
        AddChild(Scroller(col));

        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", Desktop ? 26 : 34);
        col.AddChild(top);
        var shape = PosterTile.ShapeFor(_item);
        var art = shape switch
        {
            TileShape.Square => new Vector2(260, 260),
            TileShape.Wide => new Vector2(420, 236),
            _ => new Vector2(240, 360),
        };
        top.AddChild(new PosterArt(_item, Desktop ? art * 0.85f : art, PosterTile.ProgressOf(lib, _item), 14, showTitle: false));
        var info = Column(Desktop ? 8 : 12);
        top.AddChild(info);
        info.AddChild(UiTheme.Label(_item.Title, Desktop ? UiTheme.TitleSize - 4 : UiTheme.TitleSize + 2, UiTheme.Text, wrap: true));
        var meta = new List<string>();
        if (_item.Subtitle != null && _item.Kind is MediaKind.Episode or MediaKind.Album or MediaKind.Track or MediaKind.Collection)
        {
            meta.Add(_item.Subtitle);
        }
        if (_item.Year is { } y)
        {
            meta.Add(y.ToString());
        }
        if (_item.DurationText is { } d)
        {
            meta.Add(d);
        }
        if (_item.ContentRating is { } cr)
        {
            meta.Add(cr);
        }
        if (_item.Rating is { } r)
        {
            meta.Add($"★ {r:0.0}");
        }
        meta.AddRange(_item.Genres);
        info.AddChild(UiTheme.Label(string.Join("  ·  ", meta), UiTheme.SmallSize + 2, UiTheme.Dim, wrap: true));
        info.AddChild(UiTheme.Label("From " + _item.SourceName, UiTheme.SmallSize, UiTheme.Dim));
        if (_item.Overview is { } o)
        {
            var ov = UiTheme.Label(o, UiTheme.FontSize - 2, UiTheme.Text, wrap: true);
            ov.CustomMinimumSize = new Vector2(360, 0);
            info.AddChild(ov);
        }
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 14);
        info.AddChild(actions);
        var buttons = new List<Control>();
        void Act(string label, Action a, string help)
        {
            var b = new ActionButton(label, a, help) { CustomMinimumSize = new Vector2(0, Desktop ? 42 : 54) };
            b.SetMeta(PosterTile.NoHelp, true);  // the tooltip names it for tests; the label says it all
            actions.AddChild(b);
            buttons.Add(b);
        }
        var children = lib.Children(_item.Id);
        _seasons = _item.Kind == MediaKind.Show ? lib.Seasons(_item) : [];
        if (_item.Kind == MediaKind.Collection)
        {
            BuildCollection(col, children, Act, buttons);
            return;
        }
        if (_item.Playable)
        {
            if (lib.PositionOf(_item) is { } pos)
            {
                Act($"▶  Resume from {MediaItem.FormatDuration(pos, clock: true)}", () => MediaActions.Play(_item), $"{_item.Title}: resume");
                Act("Play from the start", () => MediaActions.Play(_item, fromStart: true), $"{_item.Title}: from the start");
            }
            else
            {
                Act("▶  Play", () => MediaActions.Play(_item), $"{_item.Title}: play");
            }
            var watched = lib.IsWatched(_item);
            Act(watched ? "Mark unwatched" : "Mark watched", ToggleWatched, $"{_item.Title}: mark {(watched ? "unwatched" : "watched")}");
        }
        else if (children.Count > 0)
        {
            var music = _item.Kind == MediaKind.Album;
            var label = music ? "▶  Play album" : "▶  Play next episode";
            if (!music && lib.NextToPlay(_item) is { } next)
            {
                // Name the episode: "▶  Resume S2 · E4", "▶  Play S1 · E1".
                var verb = lib.PositionOf(next) != null ? "Resume" : "Play";
                label = $"▶  {verb} {EpisodeCode(next) ?? next.Title}";
            }
            Act(label, () => MediaActions.PlayContainer(_item), $"{_item.Title}: play");
            if (music || children.Count(c => c.Playable) > 2)
            {
                Act("Shuffle", () => MediaActions.PlayContainer(_item, shuffle: true), $"{_item.Title}: shuffle");
            }
        }
        if (Listable)
        {
            var on = lib.OnWatchlist(_item);
            Act(on ? "✓  On watchlist" : "＋  Watchlist", ToggleWatchlist, $"{_item.Title}: {(on ? "remove from" : "add to")} watchlist");
            Act("Collections…", PickCollection, $"{_item.Title}: collections");
        }
        Focus.Row(buttons);
        _first = buttons.FirstOrDefault();
        _actionButtons = buttons;
        if (_item.Kind == MediaKind.Show)
        {
            AddLeftOff(info);
        }

        if (HasSeasons)
        {
            BuildSeasons(col, buttons);
        }
        else if (children.Count > 0)
        {
            col.AddChild(Section(_item.Kind == MediaKind.Album ? "Tracks" : "Episodes"));
            var rows = new List<Control>();
            foreach (var c in children)
            {
                var progress = PosterTile.ProgressOf(lib, c);
                var status = lib.IsWatched(c) ? "Watched" : progress is { } p ? $"{p * 100:0}%" : c.DurationText;
                var label = c.Index is { } n ? (c.Season is { } sn && children.Any(x => x.Season != sn) ? $"S{sn} E{n}.  {c.Title}" : $"{n}.  {c.Title}") : c.Title;
                var row = new ListRow(label, c.Kind == MediaKind.Episode ? "" : c.Subtitle ?? "", status,
                    lib.IsWatched(c) ? UiTheme.Good : UiTheme.Dim, onPress: () => MediaActions.Play(c), help: $"{c.Title}: play")
                {
                    CustomMinimumSize = new Vector2(0, Desktop ? 48 : 60),
                };
                row.SetMeta(PosterTile.NoHelp, true);
                col.AddChild(row);
                rows.Add(row);
            }
            Focus.Column(rows, above: buttons.FirstOrDefault());
            foreach (var b in buttons)
            {
                b.FocusNeighborBottom = rows[0].GetPath();
            }
        }
    }

    /// <summary>A collection: its titles as a poster grid, plus rename / delete for one made here.</summary>
    private void BuildCollection(VBoxContainer col, IReadOnlyList<MediaItem> members, Action<string, Action, string> act, List<Control> buttons)
    {
        var lib = Host.Library;
        var playable = members.Where(m => m.Playable).ToList();
        if (playable.Count > 0)
        {
            var next = playable.FirstOrDefault(m => lib.PositionOf(m) != null) ?? playable.FirstOrDefault(m => !lib.IsWatched(m)) ?? playable[0];
            act($"▶  {(lib.PositionOf(next) != null ? "Resume" : "Play")} {next.Title}", () => MediaActions.Play(next), $"{_item.Title}: play");
        }
        if (MediaLibrary.IsUserCollection(_item.Id))
        {
            var id = MediaLibrary.UserCollectionId(_item);
            act("Rename", () => App.Push(new TextEntryPage("Rename collection", _item.Title, name =>
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    lib.Lists.RenameCollection(id, name);
                    App.Pop();
                    if (lib.Find(_item.Id) is { } renamed)
                    {
                        App.Push(new DetailPage(renamed));
                    }
                }
            })), $"{_item.Title}: rename");
            act("Delete", () => App.Confirm($"Delete the collection \"{_item.Title}\"? The movies and shows in it stay.", "Delete", () =>
            {
                lib.Lists.DeleteCollection(id);
                App.Pop();
                App.Toast($"Deleted {_item.Title}");
            }), $"{_item.Title}: delete");
        }
        Focus.Row(buttons);
        _first = buttons.FirstOrDefault();
        if (members.Count == 0)
        {
            col.AddChild(UiTheme.Label("Nothing in it yet. Open a movie or show and pick Collections… to add it.", UiTheme.FontSize - 1, UiTheme.Dim, wrap: true));
            return;
        }
        col.AddChild(Section("Titles"));
        var shape = TileShape.Poster;
        var gap = Desktop ? 18 : 24;
        var columns = ColumnsFor(PosterTile.SizeFor(shape, Desktop).X, gap);
        var grid = new GridContainer { Columns = columns };
        grid.AddThemeConstantOverride("h_separation", gap);
        grid.AddThemeConstantOverride("v_separation", gap);
        col.AddChild(grid);
        var tiles = new List<Control>();
        foreach (var m in members)
        {
            var tile = new PosterTile(m, shape, PosterTile.ProgressOf(lib, m), Desktop, MediaActions.Open);
            grid.AddChild(tile);
            tiles.Add(tile);
        }
        Focus.Grid(tiles, columns);
        _first ??= tiles[0];
        foreach (var b in buttons)
        {
            b.FocusNeighborBottom = tiles[0].GetPath();
        }
        for (var i = 0; i < Math.Min(columns, tiles.Count); i++)
        {
            if (buttons.Count > 0)
            {
                tiles[i].FocusNeighborTop = buttons[0].GetPath();
            }
        }
    }

    /// <summary>"Where you left off": the last episode watched before the next one, and what happened in it.</summary>
    private void AddLeftOff(VBoxContainer info)
    {
        var lib = Host.Library;
        if (lib.NextToPlay(_item) is not { } next || lib.PositionOf(next) != null || lib.PreviousEpisode(next) is not { } prev || !lib.IsWatched(prev))
        {
            return;
        }
        var panel = new PanelContainer();
        var box = UiTheme.Box(UiTheme.PanelHi with { A = 0.6f }, 1, 12, UiTheme.Line);
        box.ContentMarginLeft = box.ContentMarginRight = 14;
        box.ContentMarginTop = box.ContentMarginBottom = 10;
        panel.AddThemeStyleboxOverride("panel", box);
        var col = Column(4);
        panel.AddChild(col);
        var when = lib.LastViewed(prev) is { } at ? "  ·  " + RecapPage.Ago(at).ToUpperInvariant() : "";
        col.AddChild(UiTheme.Label("WHERE YOU LEFT OFF" + when, UiTheme.SmallSize, UiTheme.AccentText));
        var code = RecapPage.Code(prev);
        col.AddChild(UiTheme.Label(code != null ? $"{code}  ·  {prev.Title}" : prev.Title, UiTheme.FontSize - 1, UiTheme.Text, wrap: true));
        if (prev.Overview is { Length: > 0 } o)
        {
            var ov = UiTheme.Label(o.ReplaceLineEndings(" "), UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true);
            ov.MaxLinesVisible = 3;
            ov.CustomMinimumSize = new Vector2(360, 0);
            col.AddChild(ov);
        }
        info.AddChild(panel);
    }

    // ---- shows: seasons ----

    /// <summary>"S2 · E4" for an episode (specials: "Special 3"), or null without numbers.</summary>
    private static string? EpisodeCode(MediaItem e) => (e.Season, e.Index) switch
    {
        (0, { } n) => $"Special {n}",
        ({ } s, { } n) => $"S{s} · E{n}",
        (null, { } n) => $"E{n}",
        _ => null,
    };

    private bool SeasonWatched(ShowSeason season)
    {
        var lib = Host.Library;
        var playable = season.Episodes.Where(e => e.Playable).ToList();
        return playable.Count > 0 && playable.All(lib.IsWatched);
    }

    /// <summary>
    /// A strip of season posters (only with more than one season) above the picked season's
    /// episodes. Moving along the strip switches season; LT / RT switch from anywhere on the page.
    /// </summary>
    private void BuildSeasons(VBoxContainer col, List<Control> buttons)
    {
        var lib = Host.Library;
        if (_seasonKey == null || _seasons.All(x => x.Key != _seasonKey))
        {
            // Open on the season of the episode "Play" would start.
            var next = lib.NextToPlay(_item);
            _seasonKey = (next != null ? _seasons.FirstOrDefault(x => x.Episodes.Contains(next)) : null)?.Key ?? _seasons[0].Key;
        }
        if (_seasons.Count > 1)
        {
            var items = _seasons.Select(SeasonItem).ToList();
            // A (click, tap) on a season: pick it (a click may not focus it first) and go to its next episode.
            _strip = new MediaRow(new HomeRow("seasons", $"Seasons  ·  {_seasons.Count}", items), lib, Desktop, it =>
            {
                SelectSeason(_seasons[items.IndexOf(it)].Key, keepFocus: true);
                FocusEpisodes();
            }, scale: 0.7f);
            col.AddChild(_strip);
            for (var i = 0; i < _strip.Tiles.Count; i++)
            {
                var season = _seasons[i];
                var tile = _strip.Tiles[i];
                var playable = season.Episodes.Where(e => e.Playable).ToList();
                var left = playable.Count(e => !lib.IsWatched(e));
                tile.Badge = playable.Count == 0 ? null : left == 0 ? "✓" : left < playable.Count ? left.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                tile.Selected = season.Key == _seasonKey;
                tile.SetProgress(playable.Count > 0 && left > 0 && left < playable.Count ? 1 - (double)left / playable.Count : null);
                tile.FocusEntered += () => SelectSeason(season.Key, keepFocus: true);
            }
            MediaRow.LinkRows([_strip], above: buttons.FirstOrDefault());
            foreach (var b in buttons)
            {
                b.FocusNeighborBottom = SelectedTile()!.GetPath();
            }
        }
        _episodes = Column(Desktop ? 8 : 10);
        col.AddChild(_episodes);
        FillEpisodes();
    }

    /// <summary>A stand-in item for a season tile: its poster (or the show's), name and watched count.</summary>
    private MediaItem SeasonItem(ShowSeason season)
    {
        var lib = Host.Library;
        var playable = season.Episodes.Where(e => e.Playable).ToList();
        var watched = playable.Count(lib.IsWatched);
        var count = $"{season.Episodes.Count} episode{(season.Episodes.Count == 1 ? "" : "s")}";
        return new MediaItem
        {
            Id = $"{_item.Id}#season:{season.Key}",
            Title = season.Title,
            Kind = MediaKind.Show,
            Subtitle = watched == 0 || watched == playable.Count ? count : $"{watched} of {playable.Count} watched",
            Poster = season.PosterUrl == null ? _item.Poster : null,
            PosterUrl = season.PosterUrl ?? _item.PosterUrl,
            SourceId = _item.SourceId,
            SourceName = _item.SourceName,
        };
    }

    private PosterTile? SelectedTile()
    {
        var i = _seasons.ToList().FindIndex(x => x.Key == _seasonKey);
        return _strip != null && i >= 0 && i < _strip.Tiles.Count ? _strip.Tiles[i] : null;
    }

    /// <summary>Switches the episode list to another season, in place (the strip and focus stay).</summary>
    private void SelectSeason(int key, bool keepFocus)
    {
        if (key == _seasonKey || _episodes == null)
        {
            return;
        }
        var inEpisodes = GetViewport()?.GuiGetFocusOwner() is { } f && _episodes.IsAncestorOf(f);
        _seasonKey = key;
        if (_strip != null)
        {
            for (var i = 0; i < _strip.Tiles.Count; i++)
            {
                _strip.Tiles[i].Selected = _seasons[i].Key == key;
            }
            if (SelectedTile() is { } tile)
            {
                _strip.Reveal(tile);
                foreach (var b in _actionButtons)
                {
                    b.FocusNeighborBottom = tile.GetPath();
                }
            }
        }
        FillEpisodes();
        if (!keepFocus && inEpisodes)
        {
            FocusEpisodes();
        }
        App.UpdateChrome();
    }

    private void StepSeason(int dir)
    {
        var i = _seasons.ToList().FindIndex(x => x.Key == _seasonKey);
        var j = Math.Clamp(i + dir, 0, _seasons.Count - 1);
        if (j == i)
        {
            return;
        }
        var focusOnStrip = _strip != null && GetViewport()?.GuiGetFocusOwner() is PosterTile t && _strip.Tiles.Contains(t);
        SelectSeason(_seasons[j].Key, keepFocus: false);
        if (focusOnStrip)
        {
            SelectedTile()?.GrabFocus();
        }
    }

    /// <summary>Focus the picked season's next episode to watch (else its first).</summary>
    private void FocusEpisodes()
    {
        if (_episodes == null || CurrentSeason is not { } season)
        {
            return;
        }
        var lib = Host.Library;
        var rows = _episodes.GetChildren().OfType<ListRow>().ToList();
        if (rows.Count == 0)
        {
            return;
        }
        var next = season.Episodes.FirstOrDefault(e => lib.PositionOf(e) != null) ?? season.Episodes.FirstOrDefault(e => e.Playable && !lib.IsWatched(e));
        var i = next == null ? 0 : season.Episodes.ToList().IndexOf(next);
        rows[Math.Clamp(i, 0, rows.Count - 1)].GrabFocus();
    }

    /// <summary>The picked season's header (name, counts, Mark season watched) and its episodes.</summary>
    private void FillEpisodes()
    {
        if (_episodes == null || CurrentSeason is not { } season)
        {
            return;
        }
        foreach (var c in _episodes.GetChildren())
        {
            _episodes.RemoveChild(c);
            c.QueueFree();
        }
        var lib = Host.Library;
        var playable = season.Episodes.Where(e => e.Playable).ToList();
        var left = playable.Count(e => !lib.IsWatched(e));
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 16);
        _episodes.AddChild(head);
        var titleCol = Column(2);
        titleCol.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(titleCol);
        titleCol.AddChild(UiTheme.Label(UiTheme.Mono ? season.Title.ToUpperInvariant() : season.Title, Desktop ? UiTheme.FontSize + 2 : UiTheme.FontSize + 6, UiTheme.Text));
        var counts = $"{season.Episodes.Count} episode{(season.Episodes.Count == 1 ? "" : "s")}";
        if (playable.Count > 0)
        {
            counts += left == 0 ? "  ·  all watched" : left < playable.Count ? $"  ·  {left} left" : "";
        }
        var runtime = season.Episodes.Sum(e => e.Duration ?? 0);
        if (runtime > 0)
        {
            counts += $"  ·  {MediaItem.FormatDuration(runtime)}";
        }
        titleCol.AddChild(UiTheme.Label(counts, UiTheme.SmallSize + 1, left == 0 && playable.Count > 0 ? UiTheme.Good : UiTheme.Dim));
        ActionButton? mark = null;
        if (playable.Count > 0)
        {
            var all = left == 0;
            mark = new ActionButton(all ? "Mark season unwatched" : "Mark season watched", ToggleSeasonWatched,
                $"{_item.Title}: {season.Title}: mark {(all ? "unwatched" : "watched")}")
            {
                CustomMinimumSize = new Vector2(0, Desktop ? 38 : 48),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
            };
            mark.SetMeta(PosterTile.NoHelp, true);
            head.AddChild(mark);
        }

        var rows = new List<Control>();
        var thumb = Desktop ? new Vector2(128, 72) : new Vector2(160, 90);
        foreach (var e in season.Episodes)
        {
            var progress = PosterTile.ProgressOf(lib, e);
            var watched = lib.IsWatched(e);
            var status = watched ? "✓  Watched" : progress is { } p && e.Duration is { } d ? $"{MediaItem.FormatDuration(d * (1 - p))} left" : e.DurationText;
            var label = e.Index is { } n ? $"{n}.  {e.Title}" : e.Title;
            var art = new PosterArt(e, thumb, progress, 8, showTitle: false) { Badge = watched ? "✓" : null };
            var detail = e.Overview is { } o ? o.ReplaceLineEndings(" ") : "";
            var row = new ListRow(label, detail, status, watched ? UiTheme.Good : UiTheme.Dim, art,
                onPress: () => MediaActions.Play(e), help: $"{e.Title}: play")
            {
                CustomMinimumSize = new Vector2(0, thumb.Y + 18),
            };
            row.SetMeta(PosterTile.NoHelp, true);
            _episodes.AddChild(row);
            rows.Add(row);
        }
        if (rows.Count == 0)
        {
            return;
        }
        Control above = SelectedTile() as Control ?? _actionButtons.FirstOrDefault() ?? rows[0];
        if (mark != null)
        {
            Focus.Row([mark], above: above, below: rows[0]);
            above = mark;
        }
        Focus.Column(rows, above: above);
        var down = mark as Control ?? rows[0];
        if (_strip != null)
        {
            foreach (var t in _strip.Tiles)
            {
                t.FocusNeighborBottom = down.GetPath();
            }
        }
        else
        {
            foreach (var b in _actionButtons)
            {
                b.FocusNeighborBottom = down.GetPath();
            }
        }
    }

    private void ToggleSeasonWatched()
    {
        if (CurrentSeason is not { } season)
        {
            return;
        }
        var lib = Host.Library;
        var watched = !SeasonWatched(season);
        foreach (var e in season.Episodes.Where(e => e.Playable && lib.IsWatched(e) != watched))
        {
            lib.SetWatched(e, watched);
        }
        App.Toast(watched ? $"Marked {season.Title} watched" : $"Marked {season.Title} unwatched");
        Rebuild();
        App.UpdateChrome();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!HasSeasons || _seasons.Count < 2 || App.Top != this || !e.IsPressed())
        {
            return;
        }
        if (e.IsAction(Pad.SeekBack) || e.IsAction(Pad.SeekForward))
        {
            StepSeason(e.IsAction(Pad.SeekBack) ? -1 : 1);
            GetViewport().SetInputAsHandled();
        }
    }

    private void ToggleWatchlist()
    {
        var lib = Host.Library;
        var on = !lib.OnWatchlist(_item);
        lib.SetWatchlist(_item, on);
        App.Toast(on ? $"Added {_item.Title} to your watchlist" : $"Removed {_item.Title} from your watchlist");
        Rebuild();
        App.UpdateChrome();
    }

    /// <summary>Add to or remove from collections made here, or start a new one with this title.</summary>
    private void PickCollection()
    {
        var lib = Host.Library;
        var options = new List<(string, Action)>();
        foreach (var c in lib.Lists.Collections)
        {
            var inIt = c.Items.Contains(_item.Id);
            options.Add((inIt ? $"✓  {c.Name}" : c.Name, () =>
            {
                lib.Lists.SetInCollection(c.Id, _item.Id, !inIt);
                App.Toast(inIt ? $"Removed from {c.Name}" : $"Added to {c.Name}");
            }));
        }
        options.Add(("＋  New collection…", () => App.Push(new TextEntryPage("New collection", "", name =>
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                var c = lib.Lists.CreateCollection(name, [_item.Id]);
                App.Toast($"Added to {c.Name}");
            }
        }, "Name the collection. You can add more titles from their pages."))));
        options.Add(("Cancel", () => { }));
        var server = _item.Collections.Count > 0 ? $" It's also in {string.Join(", ", _item.Collections)} on {_item.SourceName}." : "";
        App.Push(new ChoicePage($"Collections for {_item.Title}. Pick one to add or remove it.{server}", [.. options]));
    }

    private void ToggleWatched()
    {
        var lib = Host.Library;
        lib.SetWatched(_item, !lib.IsWatched(_item));
        Rebuild();
        App.UpdateChrome();
    }

    public override void OnAction(string action)
    {
        if (action == Pad.Secondary && _item.Playable)
        {
            ToggleWatched();
        }
        else if (action == Pad.Secondary && HasSeasons)
        {
            ToggleSeasonWatched();
        }
        else if (action == Pad.Tertiary && Listable)
        {
            ToggleWatchlist();
        }
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
