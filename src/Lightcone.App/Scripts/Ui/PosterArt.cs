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
    private bool _focused;

    /// <summary>Draws the focus ring inside the art (tiles draw their own focus).</summary>
    public bool Focused
    {
        get => _focused;
        set
        {
            _focused = value;
            QueueRedraw();
        }
    }

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

    /// <summary>A w x h rectangle whose top corners are rounded by r, as polygon points.</summary>
    private static Vector2[] RoundedTop(float w, float h, float r)
    {
        var pts = new List<Vector2>();
        const int steps = 6;
        for (var i = 0; i <= steps; i++)
        {
            var a = Mathf.Pi + Mathf.Pi / 2 * i / steps;  // left corner: 180° to 270°
            pts.Add(new Vector2(r + r * Mathf.Cos(a), r + r * Mathf.Sin(a)));
        }
        for (var i = 0; i <= steps; i++)
        {
            var a = Mathf.Pi * 1.5f + Mathf.Pi / 2 * i / steps;  // right corner: 270° to 360°
            pts.Add(new Vector2(w - r + r * Mathf.Cos(a), r + r * Mathf.Sin(a)));
        }
        pts.Add(new Vector2(w, h));
        pts.Add(new Vector2(0, h));
        return pts.ToArray();
    }

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        var r = Math.Min(_radius, UiTheme.Spec.Radius);
        if (_tex != null)
        {
            var ts = _tex.GetSize();
            var frameAspect = Size.X / Size.Y;
            var texAspect = ts.X / ts.Y;
            if (Math.Abs(frameAspect / texAspect - 1) > 0.25f && _item.Kind is MediaKind.Album or MediaKind.Track or MediaKind.Artist)
            {
                // A square cover in a poster frame: show all of it, centred on the item's colour.
                var bg = new StyleBoxFlat { BgColor = UiTheme.TileColor(_item.Title).Darkened(0.5f) };
                bg.SetCornerRadiusAll((int)r);
                DrawStyleBox(bg, rect);
                var fit = Mathf.Min(Size.X / ts.X, Size.Y / ts.Y);
                var dst = ts * fit;
                DrawTextureRect(_tex, new Rect2((Size - dst) / 2, dst), false);
            }
            else
            {
                // Cover-fit: fill the box, crop the overflow.
                var scale = Mathf.Max(Size.X / ts.X, Size.Y / ts.Y);
                var src = new Vector2(Size.X / scale, Size.Y / scale);
                DrawTextureRectRegion(_tex, rect, new Rect2((ts - src) / 2, src));
            }
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
            // (Its top corners follow the box's rounding, so nothing pokes out past the frame or the focus ring.)
            var shade = RoundedTop(Size.X, Size.Y * 0.7f, r);
            DrawPolygon(shade, shade.Select(v => (top with { A = 0.85f }).Lerp(bottom with { A = 0f }, v.Y / (Size.Y * 0.7f))).ToArray());
            var beam = new Color(1, 1, 1, 0.07f);
            DrawPolygon([new Vector2(Size.X * 0.55f, 0), new Vector2(Size.X * 0.85f, 0), new Vector2(Size.X * 0.25f, Size.Y), new Vector2(-Size.X * 0.1f, Size.Y)],
                [beam, beam, beam with { A = 0 }, beam with { A = 0 }]);
            var font = UiTheme.CurrentFont;
            if (_title != null)
            {
                var gs = Mathf.Clamp((int)(Size.Y * 0.12f), 14, 40);
                DrawString(font, new Vector2(12, 12 + gs), Glyph(_item.Kind), HorizontalAlignment.Left, -1, gs, new Color(1, 1, 1, 0.55f));
            }
            else
            {
                // No title on the card: one larger centred glyph.
                var gs = Mathf.Clamp((int)(Mathf.Min(Size.X, Size.Y) * 0.28f), 18, 72);
                var w = font.GetStringSize(Glyph(_item.Kind), HorizontalAlignment.Left, -1, gs).X;
                DrawString(font, new Vector2((Size.X - w) / 2, Size.Y / 2 + gs * 0.35f), Glyph(_item.Kind), HorizontalAlignment.Left, -1, gs,
                    new Color(1, 1, 1, 0.5f));
            }
        }
        if (_progress is { } p and > 0)
        {
            var h = Mathf.Max(4, Size.Y * 0.025f);
            var track = new Rect2(0, Size.Y - h, Size.X, h);
            DrawRect(track, new Color(0, 0, 0, 0.55f));
            DrawRect(new Rect2(track.Position, new Vector2((float)(Size.X * Math.Clamp(p, 0, 1)), h)), UiTheme.Accent);
        }
        if (_focused)
        {
            var ring = new StyleBoxFlat { DrawCenter = false, BorderColor = UiTheme.Accent, AntiAliasing = true };
            ring.SetBorderWidthAll(3);
            ring.SetCornerRadiusAll((int)r);
            DrawStyleBox(ring, rect);
        }
    }
}
