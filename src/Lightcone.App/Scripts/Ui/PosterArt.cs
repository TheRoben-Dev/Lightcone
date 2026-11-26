using Godot;
using Lightcone.Host;
using Lightcone.Host.Library;

namespace Lightcone.App.Ui;

/// <summary>
/// A poster, still or album cover. Uses the cached image when there is one; otherwise draws a
/// card in the item's own colour with a projector-beam sweep and the title, so every item still
/// has a recognisable face. A thin progress bar runs along the bottom for started items.
/// </summary>
public partial class PosterArt : Control
{
    private static readonly Dictionary<string, ImageTexture?> Cache = new();

    private readonly MediaItem _item;
    private readonly ImageTexture? _tex;
    private readonly float _radius;
    private double? _progress;
    private readonly Label? _title;

    public PosterArt(MediaItem item, Vector2 size, double? progress = null, float radius = 10, bool showTitle = true)
    {
        _item = item;
        _radius = radius;
        _progress = progress;
        _tex = Load(item.Poster);
        CustomMinimumSize = size;
        MouseFilter = MouseFilterEnum.Ignore;
        TextureFilter = TextureFilterEnum.LinearWithMipmaps;
        ClipContents = true;
        Resized += QueueRedraw;
        if (_tex == null && showTitle)
        {
            var pad = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
            pad.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            foreach (var side in new[] { "left", "right", "top", "bottom" })
            {
                pad.AddThemeConstantOverride($"margin_{side}", side == "bottom" ? 14 : 12);
            }
            AddChild(pad);
            var big = size.Y >= 160;
            _title = UiTheme.Label(UiTheme.Mono ? item.Title.ToUpperInvariant() : item.Title, big ? 22 : 16, Colors.White, wrap: true);
            _title.VerticalAlignment = VerticalAlignment.Bottom;
            _title.SizeFlagsVertical = SizeFlags.ShrinkEnd;
            _title.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.6f));
            _title.AddThemeConstantOverride("shadow_offset_y", 2);
            _title.MouseFilter = MouseFilterEnum.Ignore;
            pad.AddChild(_title);
        }
    }

    public void SetProgress(double? p)
    {
        _progress = p;
        QueueRedraw();
    }

    private static ImageTexture? Load(string? path)
    {
        if (path == null)
        {
            return null;
        }
        if (Cache.TryGetValue(path, out var t))
        {
            return t;
        }
        ImageTexture? tex = null;
        try
        {
            var img = Image.LoadFromFile(path);
            if (img != null && !img.IsEmpty())
            {
                img.GenerateMipmaps();
                tex = ImageTexture.CreateFromImage(img);
            }
        }
        catch (Exception e)
        {
            AppLog.Warn($"poster {path}: {e.Message}");
        }
        Cache[path] = tex;
        return tex;
    }

    /// <summary>Frees cached textures while .NET is still running (see Main._ExitTree).</summary>
    public static void ReleaseShared() => Cache.Clear();

    private static string Glyph(MediaKind kind) => kind switch
    {
        MediaKind.Movie => "▶",
        MediaKind.Show or MediaKind.Episode => "▤",
        MediaKind.Album or MediaKind.Track or MediaKind.Artist => "♪",
        _ => "•",
    };

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        var r = Math.Min(_radius, UiTheme.Spec.Radius);
        if (_tex != null)
        {
            // Cover-fit: fill the box, crop the overflow.
            var ts = _tex.GetSize();
            var scale = Mathf.Max(Size.X / ts.X, Size.Y / ts.Y);
            var src = new Vector2(Size.X / scale, Size.Y / scale);
            DrawTextureRectRegion(_tex, rect, new Rect2((ts - src) / 2, src));
        }
        else
        {
            var baseColor = UiTheme.TileColor(_item.Title);
            var top = baseColor.Lightened(0.18f);
            var bottom = baseColor.Darkened(0.45f);
            var box = new StyleBoxFlat { BgColor = bottom };
            box.SetCornerRadiusAll((int)r);
            DrawStyleBox(box, rect);
            // Vertical gradient, then a soft diagonal "beam" for depth.
            DrawPolygon([rect.Position, new Vector2(rect.End.X, 0), new Vector2(rect.End.X, Size.Y * 0.7f), new Vector2(0, Size.Y * 0.7f)],
                [top with { A = 0.85f }, top with { A = 0.85f }, bottom with { A = 0f }, bottom with { A = 0f }]);
            var beam = new Color(1, 1, 1, 0.07f);
            DrawPolygon([new Vector2(Size.X * 0.55f, 0), new Vector2(Size.X * 0.85f, 0), new Vector2(Size.X * 0.25f, Size.Y), new Vector2(-Size.X * 0.1f, Size.Y)],
                [beam, beam, beam with { A = 0 }, beam with { A = 0 }]);
            var font = UiTheme.CurrentFont;
            var gs = Mathf.Clamp((int)(Size.Y * 0.12f), 14, 40);
            DrawString(font, new Vector2(12, 12 + gs), Glyph(_item.Kind), HorizontalAlignment.Left, -1, gs, new Color(1, 1, 1, 0.55f));
        }
        if (_progress is { } p and > 0)
        {
            var h = Mathf.Max(4, Size.Y * 0.025f);
            var track = new Rect2(0, Size.Y - h, Size.X, h);
            DrawRect(track, new Color(0, 0, 0, 0.55f));
            DrawRect(new Rect2(track.Position, new Vector2((float)(Size.X * Math.Clamp(p, 0, 1)), h)), UiTheme.Accent);
        }
    }
}
