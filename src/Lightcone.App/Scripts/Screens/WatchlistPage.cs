using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// The whole watchlist: what's in your libraries, plus (signed in to Plex) the titles on your Plex
/// watchlist that no server has, marked "Not on server". A row of filters on top (All, Movies,
/// Shows, On your server, Not on server), Y changes the order, X takes the focused title off.
/// Opened from Home's Watchlist row ("See all"). Same page in both modes.
/// </summary>
public partial class WatchlistPage : Screen
{
    private enum View
    {
        All,
        Movies,
        Shows,
        Available,
        Missing,
    }

    private enum Order
    {
        Added,
        Title,
        Year,
    }

    private static readonly (View Key, string Label)[] Filters =
    [
        (View.All, "All"), (View.Movies, "Movies"), (View.Shows, "Shows"), (View.Available, "On your server"), (View.Missing, "Not on server"),
    ];

    private static readonly (Order Key, string Label)[] Orders = [(Order.Added, "Date added"), (Order.Title, "Title"), (Order.Year, "Release year")];

    // Kept while the app runs, like the library's sort.
    private static View _show = View.All;
    private static Order _order = Order.Added;
    private Control? _first;
    private string? _focusId;

    public override IEnumerable<(string Action, string Label)> Hints =>
        [(Pad.Secondary, "Order: " + Orders.First(o => o.Key == _order).Label.ToLowerInvariant()), (Pad.Tertiary, "Remove from watchlist")];

    private IReadOnlyList<MediaItem> Items()
    {
        var lib = Host.Library;
        IEnumerable<MediaItem> items = lib.Watchlist(notInLibrary: true);
        items = _show switch
        {
            View.Movies => items.Where(i => i.Kind == MediaKind.Movie),
            View.Shows => items.Where(i => i.Kind == MediaKind.Show),
            View.Available => items.Where(i => !i.NotInLibrary),
            View.Missing => items.Where(i => i.NotInLibrary),
            _ => items,
        };
        return _order switch
        {
            Order.Title => items.OrderBy(i => MediaLibrary.SortTitle(i.Title), StringComparer.CurrentCultureIgnoreCase).ToList(),
            Order.Year => items.OrderByDescending(i => i.Year ?? 0).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => items.ToList(),  // as the watchlist keeps them: newest first
        };
    }

    protected override void Build()
    {
        _first = null;
        var lib = Host.Library;
        var all = lib.Watchlist(notInLibrary: true);
        var items = Items();
        var col = Column(Desktop ? 12 : 16);
        AddChild(col);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 14);
        col.AddChild(head);
        head.AddChild(UiTheme.Label(UiTheme.Mono ? Loc.Upper("Watchlist") : "Watchlist", Desktop ? UiTheme.TitleSize - 8 : UiTheme.TitleSize - 4, UiTheme.Text));
        var missing = all.Count(i => i.NotInLibrary);
        var count = UiTheme.Label(missing > 0 ? $"{all.Count} titles  ·  {missing} not on your server" : $"{all.Count} {(all.Count == 1 ? "title" : "titles")}",
            UiTheme.SmallSize + 1, UiTheme.Dim);
        count.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(count);
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var sync = lib.OnlineWatchlist is { Active: true } w
            ? $"Synced with your {w.Name} watchlist"
            : Host.Plex.Account.Token != null ? "Syncing with your Plex watchlist…" : "On this device. Sign in to Plex in Sources to sync it.";
        var syncLabel = UiTheme.Label(sync, UiTheme.SmallSize, UiTheme.Dim);
        syncLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(syncLabel);

        // Filters, and (Desktop) the order; Y changes it with a controller.
        var bar = new HFlowContainer();
        bar.AddThemeConstantOverride("h_separation", 10);
        bar.AddThemeConstantOverride("v_separation", 8);
        col.AddChild(bar);
        var chips = new List<Control>();
        foreach (var (key, label) in Filters)
        {
            if (key == View.Missing && missing == 0 && _show != View.Missing)
            {
                continue;
            }
            var on = key == _show;
            var chip = new ActionButton(on ? $"●  {label}" : label, () => SetShow(key), $"Watchlist: show {label.ToLowerInvariant()}")
            {
                CustomMinimumSize = new Vector2(0, Desktop ? 36 : 46),
            };
            chip.SetMeta(PosterTile.NoHelp, true);
            bar.AddChild(chip);
            chips.Add(chip);
        }
        if (Desktop)
        {
            var sort = new OptionButton { TooltipText = "Order" };
            foreach (var (_, label) in Orders)
            {
                sort.AddItem(label);
            }
            sort.Select(Array.FindIndex(Orders, o => o.Key == _order));
            sort.ItemSelected += i =>
            {
                _order = Orders[i].Key;
                Rebuild();
                App.UpdateChrome();
            };
            bar.AddChild(sort);
            chips.Add(sort);
        }
        Focus.Row(chips);

        if (items.Count == 0)
        {
            col.AddChild(UiTheme.Label(all.Count == 0
                    ? "Nothing on your watchlist yet. On a movie or show, press X (or pick ＋ Watchlist) to save it for later. With Plex, titles you add in any Plex app show up here too."
                    : "Nothing here with this filter.",
                UiTheme.FontSize - 1, UiTheme.Dim, wrap: true));
            _first = chips.FirstOrDefault(c => c is Button b && b.Text.StartsWith('●')) ?? chips.FirstOrDefault();
            return;
        }
        var gap = Desktop ? 18 : 24;
        var columns = ColumnsFor(PosterTile.SizeFor(TileShape.Poster, Desktop).X, gap);
        var grid = new GridContainer { Columns = columns };
        grid.AddThemeConstantOverride("h_separation", gap);
        grid.AddThemeConstantOverride("v_separation", gap);
        col.AddChild(Faded(Scroller(grid)));
        var tiles = new List<Control>();
        foreach (var item in items)
        {
            var tile = new PosterTile(item, TileShape.Poster, PosterTile.ProgressOf(lib, item), Desktop, MediaActions.Open, FillScale());
            grid.AddChild(tile);
            tiles.Add(tile);
        }
        Focus.Grid(tiles, columns);
        foreach (var c in chips)
        {
            c.FocusNeighborBottom = tiles[0].GetPath();
        }
        for (var i = 0; i < Math.Min(columns, tiles.Count); i++)
        {
            tiles[i].FocusNeighborTop = chips[Math.Min(i, chips.Count - 1)].GetPath();
        }
        _first = (_focusId != null ? tiles.OfType<PosterTile>().FirstOrDefault(t => t.Item.Id == _focusId) : null) ?? tiles[0];
        _focusId = null;
    }

    private void SetShow(View show)
    {
        _show = show;
        Rebuild();
        App.UpdateChrome();
        FocusDefault();
    }

    public override void FocusDefault() => _first?.GrabFocus();

    public override void OnAction(string action)
    {
        if (action == Pad.Secondary)
        {
            _order = Orders[(Array.FindIndex(Orders, o => o.Key == _order) + 1) % Orders.Length].Key;
            Rebuild();
            App.UpdateChrome();
            FocusDefault();
            App.Toast("Ordered by " + Orders.First(o => o.Key == _order).Label.ToLowerInvariant());
        }
        else if (action == Pad.Tertiary && GetViewport().GuiGetFocusOwner() is PosterTile { Item: var item })
        {
            // Focus stays where the removed card was (the next one slides in).
            var list = Items();
            var at = list.ToList().FindIndex(i => i.Id == item.Id);
            _focusId = at >= 0 && at + 1 < list.Count ? list[at + 1].Id : at > 0 ? list[at - 1].Id : null;
            Host.Library.SetWatchlist(item, false);
            App.Toast($"Removed {item.Title} from your watchlist");
            Rebuild();
            App.UpdateChrome();
            FocusDefault();
        }
    }
}
