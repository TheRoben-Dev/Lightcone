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
    private ImageTexture? _tex;
    private float _texAlpha = 1;
    private readonly float _radius;
    private double? _progress;

    /// <summary>Small corner label: "✓" (watched) or a show's unwatched episode count.</summary>
    public string? Badge { get; set; }

    /// <summary>Draws a watchlist ribbon.</summary>
    public bool OnWatchlist { get; set; }
    private Label? _title;
    private bool _focused;
    private bool _selected;

    /// <summary>A softer ring for the picked one of a set (a show's current season) while focus is elsewhere.</summary>
    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            QueueRedraw();
        }
    }

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
        _tex = Load(item.Poster) ?? (item.PosterUrl is { } url ? RemoteImage.Cached(url) : null);
        CustomMinimumSize = size;
        Material = RoundedMaterial(Math.Min(radius, UiTheme.Spec.Radius));
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

    public override void _Ready()
    {
        if (_tex == null && _item.PosterUrl is { } url)
        {
            LoadRemote(url);
        }
    }

    /// <summary>Server artwork: the generated card shows until the picture arrives, then it fades in.</summary>
    private async void LoadRemote(string url)
    {
        var max = (int)Mathf.Max(CustomMinimumSize.X, CustomMinimumSize.Y) * 2;
        var tex = await RemoteImage.TextureAsync(url, Math.Max(256, max));
        if (tex == null || !IsInstanceValid(this))
        {
            return;
        }
        _tex = tex;
        _title?.GetParent()?.QueueFree();
        _title = null;
        if (Anim.Enabled && IsVisibleInTree())
        {
            _texAlpha = 0;
            CreateTween().TweenMethod(Callable.From<float>(a =>
            {
                _texAlpha = a;
                QueueRedraw();
            }), 0f, 1f, Anim.Slow);
        }
        QueueRedraw();
    }

    private static Shader? _rounded;

    /// <summary>
    /// Clips everything the art draws (picture, generated card, focus ring) to a rounded
    /// rectangle with soft edges, so corners stay round with real artwork too.
    /// </summary>
    private ShaderMaterial RoundedMaterial(float radius)
    {
        _rounded ??= new Shader
        {
            Code = """
                shader_type canvas_item;
                uniform vec2 size = vec2(100.0);
                uniform float radius = 10.0;
                varying vec2 local;
                void vertex() { local = VERTEX; }
                void fragment() {
                    vec2 q = abs(local - size * 0.5) - (size * 0.5 - vec2(radius));
                    float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
                    COLOR.a *= clamp(0.5 - d, 0.0, 1.0);
                }
                """,
        };
        var m = new ShaderMaterial { Shader = _rounded };
        m.SetShaderParameter("radius", radius);
        Resized += () => m.SetShaderParameter("size", Size);
        return m;
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
        MediaKind.Collection => "▦",
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
        (Material as ShaderMaterial)?.SetShaderParameter("size", Size);
        var rect = new Rect2(Vector2.Zero, Size);
        var r = Math.Min(_radius, UiTheme.Spec.Radius);
        var tint = new Color(1, 1, 1, _texAlpha);
        // The generated card shows until artwork arrives (and under it while it fades in).
        if (_tex == null || _texAlpha < 1)
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
        if (_tex != null)
        {
            var ts = _tex.GetSize();
            var frameAspect = Size.X / Size.Y;
            var texAspect = ts.X / ts.Y;
            if (Math.Abs(frameAspect / texAspect - 1) > 0.25f && _item.Kind is MediaKind.Album or MediaKind.Track or MediaKind.Artist)
            {
                // A square cover in a poster frame: show all of it, centred on the item's colour.
                var bg = new StyleBoxFlat { BgColor = UiTheme.TileColor(_item.Title).Darkened(0.5f) with { A = _texAlpha } };
                bg.SetCornerRadiusAll((int)r);
                DrawStyleBox(bg, rect);
                var fit = Mathf.Min(Size.X / ts.X, Size.Y / ts.Y);
                var dst = ts * fit;
                DrawTextureRect(_tex, new Rect2((Size - dst) / 2, dst), false, tint);
            }
            else
            {
                // Cover-fit: fill the box, crop the overflow.
                var scale = Mathf.Max(Size.X / ts.X, Size.Y / ts.Y);
                var src = new Vector2(Size.X / scale, Size.Y / scale);
                DrawTextureRectRegion(_tex, rect, new Rect2((ts - src) / 2, src), tint);
            }
        }
        if (_progress is { } p and > 0)
        {
            var h = Mathf.Max(4, Size.Y * 0.025f);
            var track = new Rect2(0, Size.Y - h, Size.X, h);
            DrawRect(track, new Color(0, 0, 0, 0.55f));
            DrawRect(new Rect2(track.Position, new Vector2((float)(Size.X * Math.Clamp(p, 0, 1)), h)), UiTheme.Accent);
        }
        if (Badge is { Length: > 0 } badge)
        {
            // Top right: ✓ for watched, the number of unwatched episodes for a show.
            var font = UiTheme.CurrentFont;
            var fs = Mathf.Clamp((int)(Size.Y * 0.06f), 11, 18);
            var tw = font.GetStringSize(badge, HorizontalAlignment.Left, -1, fs).X;
            var h = fs + 10;
            var w = Mathf.Max(h, tw + 14);
            var pill = new Rect2(Size.X - w - 8, 8, w, h);
            var box = new StyleBoxFlat { BgColor = badge == "✓" ? UiTheme.Accent : new Color(0, 0, 0, 0.72f), AntiAliasing = true };
            box.SetCornerRadiusAll((int)(h / 2));
            DrawStyleBox(box, pill);
            DrawString(font, new Vector2(pill.Position.X + (w - tw) / 2, pill.Position.Y + h / 2 + fs * 0.36f), badge, HorizontalAlignment.Left, -1, fs, Colors.White);
        }
        if (OnWatchlist)
        {
            // Top left: a small ribbon.
            var rw = Mathf.Clamp(Size.X * 0.09f, 12, 20);
            var rh = rw * 1.5f;
            var c = UiTheme.Accent;
            DrawPolygon([new Vector2(10, 0), new Vector2(10 + rw, 0), new Vector2(10 + rw, rh), new Vector2(10 + rw / 2, rh - rw * 0.4f), new Vector2(10, rh)], [c, c, c, c, c]);
        }
        if (_focused || _selected)
        {
            var ring = new StyleBoxFlat { DrawCenter = false, BorderColor = _focused ? UiTheme.Accent : UiTheme.Accent with { A = 0.6f }, AntiAliasing = true };
            ring.SetBorderWidthAll(_focused ? 3 : 2);
            ring.SetCornerRadiusAll((int)r);
            DrawStyleBox(ring, rect);
        }
    }
}
