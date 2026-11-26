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

    public DetailPage(MediaItem item) => _item = item;

    private bool Listable => _item.Kind is MediaKind.Movie or MediaKind.Show;

    public override IEnumerable<(string Action, string Label)> Hints
    {
        get
        {
            var hints = new List<(string, string)>();
            if (_item.Playable)
            {
                hints.Add((Pad.Secondary, Host.Library.IsWatched(_item) ? "Mark unwatched" : "Mark watched"));
            }
            if (Listable)
            {
                hints.Add((Pad.Tertiary, Host.Library.OnWatchlist(_item) ? "Remove from watchlist" : "Add to watchlist"));
            }
            return hints;
        }
    }

    protected override void Build()
    {
        _first = null;
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
            Act(music ? "▶  Play album" : "▶  Play next episode", () => MediaActions.PlayContainer(_item), $"{_item.Title}: play");
            if (music)
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

        if (children.Count > 0)
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
        else if (action == Pad.Tertiary && Listable)
        {
            ToggleWatchlist();
        }
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
