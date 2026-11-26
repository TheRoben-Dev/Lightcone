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

    /// <summary>Meta on controls whose tooltip only identifies them (tests): no help line for it.</summary>
    public const string NoHelp = "lc_no_help";

    private readonly PosterArt _art;
    private readonly Label _title;

    public PosterTile(MediaItem item, TileShape shape, double? progress, bool desktop, Action<MediaItem> open)
    {
        Item = item;
        var size = SizeFor(shape, desktop);
        Flat = true;
        FocusMode = FocusModeEnum.All;
        TooltipText = item.Title + (item.Subtitle != null ? "\n" + item.Subtitle : "");
        SetMeta(NoHelp, true);
        // The art draws its own focus ring; the shared ring would also frame the caption.
        SetMeta(FocusHighlight.OwnFocus, true);
        foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus", "disabled" })
        {
            AddThemeStyleboxOverride(state, new StyleBoxEmpty());
        }
        MouseDefaultCursorShape = CursorShape.PointingHand;
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 6);
        AddChild(col);
        // The caption below names the item, so placeholder art shows only its kind.
        _art = new PosterArt(item, size, progress, showTitle: false);
        if (Main.Instance?.Host?.Library is { } lib)
        {
            _art.Badge = BadgeFor(lib, item);
            _art.OnWatchlist = item.Kind is MediaKind.Movie or MediaKind.Show && lib.OnWatchlist(item);
        }
        col.AddChild(_art);
        _title = UiTheme.Label(item.Title, desktop ? UiTheme.SmallSize + 1 : UiTheme.SmallSize + 3, UiTheme.Text);
        _title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _title.CustomMinimumSize = new Vector2(size.X, 0);
        _title.MouseFilter = MouseFilterEnum.Ignore;
        col.AddChild(_title);
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
        // The button is as tall as art plus caption, whatever the fonts measure on this screen.
        CustomMinimumSize = size;
        col.MinimumSizeChanged += () => CustomMinimumSize = new Vector2(size.X, col.GetCombinedMinimumSize().Y);
        Pressed += () => open(item);
        FocusEntered += () => SetFocused(true);
        FocusExited += () => SetFocused(false);
        Anim.FocusPop(this, 1.03f, press: false);
    }

    private void SetFocused(bool on)
    {
        _art.Focused = on;
        _title.AddThemeColorOverride("font_color", on ? UiTheme.AccentText : UiTheme.Text);
    }

    /// <summary>"✓" for a watched movie or episode, the unwatched count for a show being watched, else null.</summary>
    public static string? BadgeFor(MediaLibrary lib, MediaItem item)
    {
        if (item.Kind is MediaKind.Movie or MediaKind.Episode)
        {
            return lib.IsWatched(item) ? "✓" : null;
        }
        if (lib.UnwatchedCount(item) is { } n && lib.Children(item.Id).Count is var total and > 0)
        {
            return n == 0 ? "✓" : n < total ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        return null;
    }

    /// <summary>Progress fraction for an item from the library's resume data.</summary>
    public static double? ProgressOf(MediaLibrary lib, MediaItem item) => ResumePolicy.Progress(lib.PositionOf(item), item.Duration);
}
