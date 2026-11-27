using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// One section of the library (Movies, Shows, Music). Controller mode: a poster grid, Y changes
/// the sort. Desktop mode: a toolbar with sort and a grid / list switch; the list is a table
/// whose column headers sort it. Both read the same <see cref="MediaLibrary.Section"/>.
/// </summary>
public partial class LibraryScreen : Screen
{
    private readonly MediaKind _kind;
    private readonly string _title;
    private SortKey _sort = SortKey.Title;
    private bool _descending;
    private bool _collections;
    private Control? _first;

    // The filter sticks per section while the app runs (switching tabs rebuilds the page).
    private static readonly Dictionary<MediaKind, WatchFilter> FilterOf = new();

    private WatchFilter Filter => HasCollections ? FilterOf.GetValueOrDefault(_kind) : WatchFilter.All;

    private void SetFilter(WatchFilter f)
    {
        FilterOf[_kind] = f;
        Rebuild();
        App.UpdateChrome();
    }

    private void SetSort(SortKey key)
    {
        _descending = key == _sort ? !_descending : key is SortKey.Added or SortKey.Year;
        _sort = key;
        Rebuild();
        App.UpdateChrome();
    }

    private void OpenViewOptions() => App.Push(new ViewOptionsPage(Sorts, () => (_sort, _descending, Filter), SetSort, HasCollections ? SetFilter : null));

    /// <summary>Movies and Shows can list their collections instead of every title.</summary>
    private bool HasCollections => _kind is MediaKind.Movie or MediaKind.Show;

    private static readonly (SortKey Key, string Label)[] Sorts =
    [
        (SortKey.Title, "Title"), (SortKey.Year, "Year"), (SortKey.Added, "Date added"), (SortKey.Duration, "Length"),
    ];

    public LibraryScreen(MediaKind kind, string title)
    {
        _kind = kind;
        _title = title;
        if (kind != MediaKind.Album)
        {
            (_sort, _descending) = (SortKey.Added, true);
        }
    }

    public override IEnumerable<(string Action, string Label)> Hints =>
        _collections
            ? [(Pad.Tertiary, "All " + _title.ToLowerInvariant()), (Pad.Switch, "Search")]
            : HasCollections
                ? [(Pad.Secondary, "Sort & filter"), (Pad.Tertiary, "Collections"), (Pad.Switch, "Search")]
                : [(Pad.Secondary, "Sort"), (Pad.Switch, "Search")];

    private bool ListView => Desktop && Host.Settings.DesktopView == "list";

    protected override void Build()
    {
        _first = null;
        var items = _collections ? Host.Library.Collections(_kind) : Host.Library.Section(_kind, _sort, _descending, Filter);
        var filterLabel = Filter == WatchFilter.All ? null : ViewOptionsPage.Filters.First(f => f.Key == Filter).Label;
        var col = Column(Desktop ? 12 : 16);
        AddChild(col);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 14);
        col.AddChild(head);
        var heading = _collections ? $"{_title} · Collections" : filterLabel != null ? $"{_title} · {filterLabel}" : _title;
        head.AddChild(UiTheme.Label(UiTheme.Mono ? heading.ToUpperInvariant() : heading, Desktop ? UiTheme.TitleSize - 8 : UiTheme.TitleSize - 4, UiTheme.Text));
        var noun = _collections ? (items.Count == 1 ? "collection" : "collections") : items.Count == 1 ? "item" : "items";
        var count = UiTheme.Label($"{items.Count} {noun}", UiTheme.SmallSize + 1, UiTheme.Dim);
        count.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(count);
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        if (Desktop)
        {
            BuildToolbar(head);
        }
        else if (!_collections)
        {
            var sortLabel = UiTheme.Label($"Sorted by {Sorts.First(s => s.Key == _sort).Label.ToLowerInvariant()}{(_descending ? ", newest first" : "")}", UiTheme.SmallSize + 1, UiTheme.Dim);
            sortLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            head.AddChild(sortLabel);
        }
        if (items.Count == 0 && _collections)
        {
            col.AddChild(UiTheme.Label("No collections yet. Open a movie or show and pick Collections… to start one, or turn on automatic collections in Settings → Library.",
                UiTheme.FontSize - 1, UiTheme.Dim, wrap: true));
            var back = new ActionButton("All " + _title.ToLowerInvariant(), ToggleCollections, "Show every title");
            col.AddChild(back);
            _first = back;
            return;
        }
        if (items.Count == 0 && filterLabel != null)
        {
            col.AddChild(UiTheme.Label($"No {_title.ToLowerInvariant()} here: none are {filterLabel.ToLowerInvariant()}.", UiTheme.FontSize - 1, UiTheme.Dim, wrap: true));
            var all = new ActionButton("Show everything", () => SetFilter(WatchFilter.All), "Show every title");
            col.AddChild(all);
            _first = all;
            return;
        }
        if (items.Count == 0)
        {
            col.AddChild(UiTheme.Label($"No {_title.ToLowerInvariant()} yet. Add a source in Sources, or turn on the sample library there.", UiTheme.FontSize - 1, UiTheme.Dim, wrap: true));
            var go = new ActionButton("Go to Sources", () => App.SwitchTab("Sources"), "Go to Sources");
            col.AddChild(go);
            _first = go;
            return;
        }
        if (ListView && !_collections)
        {
            col.AddChild(BuildTable(items));
            return;
        }
        var shape = items.Count > 0 ? PosterTile.ShapeFor(items[0]) : TileShape.Poster;
        var tileW = PosterTile.SizeFor(shape, Desktop).X;
        var gap = Desktop ? 18 : 24;
        var columns = ColumnsFor(tileW, gap);
        var grid = new GridContainer { Columns = columns };
        grid.AddThemeConstantOverride("h_separation", gap);
        grid.AddThemeConstantOverride("v_separation", gap);
        col.AddChild(Scroller(grid));
        var tiles = new List<Control>();
        foreach (var item in items)
        {
            var tile = new PosterTile(item, shape, PosterTile.ProgressOf(Host.Library, item), Desktop, MediaActions.Open);
            grid.AddChild(tile);
            tiles.Add(tile);
        }
        Focus.Grid(tiles, columns);
        _first = tiles.FirstOrDefault();
    }

    private void BuildToolbar(HBoxContainer head)
    {
        if (HasCollections)
        {
            var coll = new Button { Text = "Collections", TooltipText = "Show collections (X)", ToggleMode = true, ButtonPressed = _collections };
            coll.Pressed += ToggleCollections;
            head.AddChild(coll);
            if (_collections)
            {
                return;
            }
        }
        if (HasCollections)
        {
            var filter = new OptionButton { TooltipText = "Filter: which titles to show" };
            foreach (var (key, label, _) in ViewOptionsPage.Filters)
            {
                filter.AddItem(label, (int)key);
            }
            filter.Select(Array.FindIndex(ViewOptionsPage.Filters, f => f.Key == Filter));
            filter.ItemSelected += i => SetFilter(ViewOptionsPage.Filters[i].Key);
            head.AddChild(filter);
        }
        var sort = new OptionButton { TooltipText = "Sort by" };
        foreach (var (key, label) in Sorts)
        {
            sort.AddItem(label, (int)key);
        }
        sort.Select(Array.FindIndex(Sorts, s => s.Key == _sort));
        sort.ItemSelected += i =>
        {
            _sort = Sorts[i].Key;
            _descending = _sort is SortKey.Added or SortKey.Year;
            Rebuild();
        };
        head.AddChild(sort);
        var dir = new Button { Text = _descending ? "↓" : "↑", TooltipText = _descending ? "Descending" : "Ascending", CustomMinimumSize = new Vector2(40, 0) };
        dir.Pressed += () =>
        {
            _descending = !_descending;
            Rebuild();
        };
        head.AddChild(dir);
        foreach (var (id, glyph, tip) in new[] { ("grid", "▦", "Grid view"), ("list", "☰", "List view") })
        {
            var b = new Button { Text = glyph, TooltipText = tip, ToggleMode = true, ButtonPressed = Host.Settings.DesktopView == id, CustomMinimumSize = new Vector2(40, 0) };
            b.Pressed += () =>
            {
                Host.SaveSettings(Host.Settings with { DesktopView = id });
                Rebuild();
            };
            head.AddChild(b);
        }
    }

    private Control BuildTable(IReadOnlyList<MediaItem> items)
    {
        var tree = new Tree
        {
            Columns = 5,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = $"{_title} table",
        };
        var cols = new[] { ("Title", SortKey.Title), ("Year", SortKey.Year), ("Length", SortKey.Duration), ("Added", SortKey.Added), ("Source", (SortKey)(-1)) };
        for (var i = 0; i < cols.Length; i++)
        {
            var arrow = cols[i].Item2 == _sort ? (_descending ? "  ↓" : "  ↑") : "";
            tree.SetColumnTitle(i, cols[i].Item1 + arrow);
            tree.SetColumnTitleAlignment(i, HorizontalAlignment.Left);
            tree.SetColumnExpand(i, i == 0);
            if (i > 0)
            {
                tree.SetColumnCustomMinimumWidth(i, i == 4 ? 180 : 110);
            }
        }
        tree.ColumnTitleClicked += (column, button) =>
        {
            var key = cols[column].Item2;
            if ((int)key < 0)
            {
                return;
            }
            _descending = key == _sort ? !_descending : key is SortKey.Added or SortKey.Year;
            _sort = key;
            Rebuild();
        };
        var root = tree.CreateItem();
        foreach (var item in items)
        {
            var row = tree.CreateItem(root);
            row.SetText(0, item.Title);
            row.SetText(1, item.Year?.ToString() ?? "");
            row.SetText(2, item.DurationText ?? (item.Kind is MediaKind.Show or MediaKind.Album ? $"{Host.Library.Children(item.Id).Count} {(item.Kind == MediaKind.Show ? "episodes" : "tracks")}" : ""));
            row.SetText(3, item.Added.ToLocalTime().ToString("yyyy-MM-dd"));
            row.SetText(4, item.SourceName);
            row.SetMetadata(0, item.Id);
        }
        tree.ItemActivated += () =>
        {
            if (tree.GetSelected()?.GetMetadata(0).AsString() is { } id && Host.Library.Find(id) is { } item)
            {
                MediaActions.Open(item);
            }
        };
        root.GetFirstChild()?.Select(0);
        _first = tree;
        return tree;
    }

    public override void FocusDefault() => _first?.GrabFocus();

    private void ToggleCollections()
    {
        _collections = !_collections;
        Rebuild();
        App.UpdateChrome();
    }

    public override void OnAction(string action)
    {
        if (action == Pad.Tertiary && HasCollections)
        {
            ToggleCollections();
            return;
        }
        if (action != Pad.Secondary || _collections)
        {
            return;
        }
        // Y: sort and filter, on a card over the page.
        OpenViewOptions();
    }
}
