using Godot;
using Lightcone.App.Ui;
using Lightcone.Host;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// Search everything (all sources). In Game Mode, focusing the field brings up Steam's on-screen
/// keyboard; the built-in keyboard (X) works everywhere else, and a real keyboard always works.
/// </summary>
public partial class SearchPage : Screen
{
    private string _query;
    private LineEdit? _field;
    private GridContainer? _grid;
    private Label? _status;

    public SearchPage(string query = "") => _query = query;

    public override IEnumerable<(string Action, string Label)> Hints => [(Pad.Tertiary, "Keyboard")];

    protected override void Build()
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
            VirtualKeyboardEnabled = true,
        };
        _field.AddThemeFontSizeOverride("font_size", Desktop ? UiTheme.FontSize - 2 : UiTheme.FontSize + 2);
        _field.TextChanged += t =>
        {
            _query = t;
            Fill();
        };
        _field.FocusEntered += SteamKeyboard.ShowIfGameMode;
        col.AddChild(_field);
        _status = UiTheme.Label("", UiTheme.SmallSize + 1, UiTheme.Dim);
        col.AddChild(_status);
        var tileW = PosterTile.SizeFor(TileShape.Poster, Desktop).X;
        var width = Math.Max(400, GetViewportRect().Size.X / (Desktop ? GetWindow().ContentScaleFactor : 1) - (Desktop ? 300 : 100));
        _grid = new GridContainer { Columns = Math.Max(1, (int)((width + 22) / (tileW + 22))) };
        _grid.AddThemeConstantOverride("h_separation", 22);
        _grid.AddThemeConstantOverride("v_separation", 22);
        col.AddChild(Scroller(_grid));
        Fill();
    }

    private void Fill()
    {
        if (_grid == null || _field == null)
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
        Focus.Grid(tiles, _grid.Columns, above: _field);
        _field.FocusNeighborBottom = tiles.Count > 0 ? tiles[0].GetPath() : _field.GetPath();
        _field.FocusNeighborTop = _field.GetPath();
    }

    public override void FocusDefault() => _field?.GrabFocus();

    public override void OnAction(string action)
    {
        if (action == Pad.Tertiary)
        {
            App.Push(new TextEntryPage("Search", _query, t =>
            {
                _query = t;
                if (_field != null)
                {
                    _field.Text = t;
                }
                Fill();
            }));
        }
    }
}

/// <summary>Steam's on-screen keyboard (Game Mode / Big Picture), requested through a steam:// link.</summary>
public static class SteamKeyboard
{
    public static void ShowIfGameMode()
    {
        if (!SteamSetup.InGameMode)
        {
            return;
        }
        AppLog.Info("asking Steam for its on-screen keyboard");
        OS.ShellOpen("steam://open/keyboard");
    }
}
