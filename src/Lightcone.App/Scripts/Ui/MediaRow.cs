using Godot;
using Lightcone.Host.Library;

namespace Lightcone.App.Ui;

/// <summary>
/// A titled horizontal row of tiles (home screen). Left/right move along the row and it scrolls
/// to keep the focused tile in view; up/down move to the nearest tile in the next row.
/// </summary>
public partial class MediaRow : VBoxContainer
{
    public List<PosterTile> Tiles { get; } = new();
    private readonly ScrollContainer _scroll;

    /// <param name="scale">Tile size against the usual (a show's season strip uses smaller posters).</param>
    public MediaRow(HomeRow row, MediaLibrary lib, bool desktop, Action<MediaItem> open, float scale = 1)
    {
        AddThemeConstantOverride("separation", 10);
        var title = UiTheme.Label(UiTheme.Mono ? row.Title.ToUpperInvariant() : row.Title, desktop ? UiTheme.FontSize : UiTheme.FontSize + 4, UiTheme.Text);
        AddChild(title);
        var scroll = _scroll = new ScrollContainer
        {
            FollowFocus = true,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            HorizontalScrollMode = desktop ? ScrollContainer.ScrollMode.Auto : ScrollContainer.ScrollMode.ShowNever,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        AddChild(scroll);
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", desktop ? 16 : 22);
        // Room for the focus ring and the focus pop.
        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_left", 6);
        pad.AddThemeConstantOverride("margin_right", 6);
        pad.AddThemeConstantOverride("margin_top", 8);
        pad.AddThemeConstantOverride("margin_bottom", 8);
        pad.AddChild(hbox);
        scroll.AddChild(pad);
        // One shape per row, so every card in it is the same height (an album among movies gets a
        // poster frame with its cover fitted inside). The row is as tall as its cards.
        var shapes = row.Items.Select(i => PosterTile.ShapeFor(i, row.Wide)).Distinct().ToList();
        var shape = shapes.Count == 1 ? shapes[0] : shapes.Contains(TileShape.Poster) ? TileShape.Poster : shapes[0];
        foreach (var item in row.Items)
        {
            var tile = new PosterTile(item, shape, PosterTile.ProgressOf(lib, item), desktop, open, scale);
            Tiles.Add(tile);
            hbox.AddChild(tile);
        }
    }

    /// <summary>Scrolls the row so a tile is in view without focusing it.</summary>
    public void Reveal(PosterTile tile) => Callable.From(() =>
    {
        if (IsInstanceValid(tile) && tile.IsInsideTree())
        {
            _scroll.EnsureControlVisible(tile);
        }
    }).CallDeferred();

    /// <summary>Wires up/down between rows to the tile nearest in x (no diagonal surprises).</summary>
    public static void LinkRows(IReadOnlyList<MediaRow> rows, Control? above = null)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var tiles = rows[i].Tiles;
            for (var t = 0; t < tiles.Count; t++)
            {
                var tile = tiles[t];
                tile.FocusNeighborLeft = t > 0 ? tiles[t - 1].GetPath() : tile.GetPath();
                tile.FocusNeighborRight = t < tiles.Count - 1 ? tiles[t + 1].GetPath() : tile.GetPath();
                if (i > 0 && rows[i - 1].Tiles.Count > 0)
                {
                    tile.FocusNeighborTop = rows[i - 1].Tiles[Math.Min(t, rows[i - 1].Tiles.Count - 1)].GetPath();
                }
                else if (above != null)
                {
                    tile.FocusNeighborTop = above.GetPath();
                }
                if (i < rows.Count - 1 && rows[i + 1].Tiles.Count > 0)
                {
                    tile.FocusNeighborBottom = rows[i + 1].Tiles[Math.Min(t, rows[i + 1].Tiles.Count - 1)].GetPath();
                }
                else
                {
                    tile.FocusNeighborBottom = tile.GetPath();
                }
            }
        }
    }
}
