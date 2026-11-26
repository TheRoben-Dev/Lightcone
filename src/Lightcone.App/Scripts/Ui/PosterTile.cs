using Godot;
using Lightcone.Host.Library;
using Lightcone.Playback;

namespace Lightcone.App.Ui;

public enum TileShape
{
    Poster,
    Wide,
    Square,
}

/// <summary>
/// A focusable media tile: art plus title and detail lines below. A (or click / tap) opens it.
/// The tooltip carries the title so tests and the hint bar can identify it.
/// </summary>
public partial class PosterTile : Button
{
    public MediaItem Item { get; }

    public static Vector2 SizeFor(TileShape shape, bool desktop) => (shape, desktop) switch
    {
        (TileShape.Wide, false) => new Vector2(320, 180),
        (TileShape.Wide, true) => new Vector2(256, 144),
        (TileShape.Square, false) => new Vector2(200, 200),
        (TileShape.Square, true) => new Vector2(160, 160),
        (_, false) => new Vector2(180, 270),
        (_, true) => new Vector2(144, 216),
    };

    public static TileShape ShapeFor(MediaItem item, bool wide = false) =>
        wide || item.Kind == MediaKind.Episode ? TileShape.Wide
        : item.Kind is MediaKind.Album or MediaKind.Track or MediaKind.Artist ? TileShape.Square
        : TileShape.Poster;

    public PosterTile(MediaItem item, TileShape shape, double? progress, bool desktop, Action<MediaItem> open)
    {
        Item = item;
        var size = SizeFor(shape, desktop);
        Flat = true;
        FocusMode = FocusModeEnum.All;
        TooltipText = item.Title + (item.Subtitle != null ? "\n" + item.Subtitle : "");
        MouseDefaultCursorShape = CursorShape.PointingHand;
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 6);
        AddChild(col);
        col.AddChild(new PosterArt(item, size, progress, showTitle: item.Poster == null));
        var title = UiTheme.Label(item.Title, desktop ? UiTheme.SmallSize + 1 : UiTheme.SmallSize + 3, UiTheme.Text);
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        title.CustomMinimumSize = new Vector2(size.X, 0);
        title.MouseFilter = MouseFilterEnum.Ignore;
        col.AddChild(title);
        var detail = item.Subtitle ?? item.DurationText ?? "";
        if (progress is { } p && item.Duration is { } d)
        {
            detail = $"{MediaItem.FormatDuration(d * (1 - p))} left";
        }
        var sub = UiTheme.Label(detail, UiTheme.SmallSize - (desktop ? 1 : 0), UiTheme.Dim);
        sub.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        sub.CustomMinimumSize = new Vector2(size.X, 0);
        sub.MouseFilter = MouseFilterEnum.Ignore;
        col.AddChild(sub);
        CustomMinimumSize = new Vector2(size.X, size.Y + 54);
        Pressed += () => open(item);
        FocusEntered += () => Anim.FocusPop(this, 1.03f, press: false);
    }

    /// <summary>Progress fraction for an item from the library's resume data.</summary>
    public static double? ProgressOf(MediaLibrary lib, MediaItem item) => ResumePolicy.Progress(lib.PositionOf(item), item.Duration);
}
