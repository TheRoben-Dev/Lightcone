using Godot;
using Lightcone.App.Ui;
using Lightcone.Host;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// Search everything (all sources). With a controller (always in Controller mode, and in Desktop
/// mode when the last input was a pad) Lightcone's own keyboard sits left of the results, which
/// update as you type: X deletes, Y adds a space, right from the keyboard goes to the results.
/// With a mouse and keyboard it is a plain search field. Steam's keyboard is never used.
/// </summary>
public partial class SearchPage : Screen
{
    private string _query;
    private LineEdit? _field;
    private Label? _shown;
    private OnScreenKeyboard? _keys;
    private GridContainer? _grid;
    private Label? _status;

    public SearchPage(string query = "") => _query = query;

    public override IEnumerable<(string Action, string Label)> Hints =>
        _keys != null ? [(Pad.Tertiary, "Delete"), (Pad.Secondary, "Space")] : [];

    private static bool UseKeyboard => !Desktop || Pad.LastFamily != PadFamily.Keyboard;

    protected override void Build()
    {
        _field = null;
        _shown = null;
        _keys = null;
        var tileW = PosterTile.SizeFor(TileShape.Poster, Desktop).X;
        _grid = new GridContainer();
        _grid.AddThemeConstantOverride("h_separation", 22);
        _grid.AddThemeConstantOverride("v_separation", 22);
        _status = UiTheme.Label("", UiTheme.SmallSize + 1, UiTheme.Dim);
        if (UseKeyboard)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", Desktop ? 24 : 32);
            AddChild(row);
            var left = Column(Desktop ? 10 : 12);
            var kbWidth = Desktop ? 460 : 520;
            left.CustomMinimumSize = new Vector2(kbWidth, 0);
            row.AddChild(left);
            left.AddChild(UiTheme.Label("Search", Desktop ? UiTheme.TitleSize - 8 : UiTheme.TitleSize - 4, UiTheme.Text));
            _shown = TextEntryPage.Field(left);
            _keys = new OnScreenKeyboard(Desktop ? 40 : 46, paste: false);
            _keys.Typed += t => SetQuery(_query.Length < 200 ? _query + t : _query);
            _keys.Deleted += () => SetQuery(_query.Length > 0 ? _query[..^1] : "");
            _keys.Cleared += () => SetQuery("");
            left.AddChild(_keys);
            left.AddChild(_status);
            var right = Column(Desktop ? 10 : 12);
            right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(right);
            right.AddChild(Scroller(_grid));
            _grid.Columns = ColumnsFor(tileW, 22, kbWidth + (Desktop ? 24 : 32));
        }
        else
        {
            var col = Column(Desktop ? 12 : 18);
            AddChild(col);
            col.AddChild(UiTheme.Label("Search", Desktop ? UiTheme.TitleSize - 8 : UiTheme.TitleSize - 4, UiTheme.Text));
            _field = new LineEdit
            {
                Text = _query,
                PlaceholderText = "Titles, people, genres, years…",
                CustomMinimumSize = new Vector2(0, Desktop ? 40 : 56),
                TooltipText = "Search field",
                ClearButtonEnabled = true,
                VirtualKeyboardEnabled = false,
            };
            _field.AddThemeFontSizeOverride("font_size", Desktop ? UiTheme.FontSize - 2 : UiTheme.FontSize + 2);
            _field.TextChanged += t =>
            {
                _query = t;
                Fill();
            };
            col.AddChild(_field);
            col.AddChild(_status);
            col.AddChild(Scroller(_grid));
            _grid.Columns = ColumnsFor(tileW, 22);
        }
        ShowQuery();
        Fill();
    }

    private void SetQuery(string q)
    {
        _query = q;
        ShowQuery();
        Fill();
    }

    private void ShowQuery()
    {
        if (_shown != null)
        {
            _shown.Text = _query + "▏";
        }
    }

    private void Fill()
    {
        if (_grid == null)
        {
            return;
        }
        foreach (var c in _grid.GetChildren())
        {
            _grid.RemoveChild(c);
            c.QueueFree();
        }
        var results = Host.Library.Search(_query);
        _status!.Text = _query.Trim().Length == 0 ? "Type to search every source." : results.Count == 0 ? $"Nothing matches “{_query}”." : $"{results.Count} result{(results.Count == 1 ? "" : "s")}";
        var tiles = new List<Control>();
        foreach (var item in results)
        {
            var tile = new PosterTile(item, PosterTile.ShapeFor(item), PosterTile.ProgressOf(Host.Library, item), Desktop, MediaActions.Open);
            _grid.AddChild(tile);
            tiles.Add(tile);
        }
        if (_keys != null)
        {
            Focus.Grid(tiles, _grid.Columns);
            // Right from the keyboard's last key goes to the results, left from the first column back to "q".
            _keys.Wire(right: tiles.FirstOrDefault());
            for (var i = 0; i < tiles.Count; i += _grid.Columns)
            {
                tiles[i].FocusNeighborLeft = _keys.Rows[Math.Min(_keys.Rows.Count - 1, 1 + i / _grid.Columns)][^1].GetPath();
            }
        }
        else if (_field != null)
        {
            Focus.Grid(tiles, _grid.Columns, above: _field);
            _field.FocusNeighborBottom = tiles.Count > 0 ? tiles[0].GetPath() : _field.GetPath();
            _field.FocusNeighborTop = _field.GetPath();
        }
    }

    public override void FocusDefault()
    {
        if (_keys != null)
        {
            _keys.FirstKey.GrabFocus();
        }
        else
        {
            _field?.GrabFocus();
        }
    }

    public override void OnAction(string action) => _keys?.OnAction(action);

    /// <summary>A real keyboard types into the on-screen keyboard's field too.</summary>
    public override void _Input(InputEvent e)
    {
        if (_keys != null && e is InputEventKey { Pressed: true } k && App.Top == this && _keys.HandleKey(k))
        {
            GetViewport().SetInputAsHandled();
        }
    }
}
