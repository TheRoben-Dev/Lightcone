using Godot;

namespace Lightcone.App.Ui;

/// <summary>
/// Wraps a ScrollContainer and fades its content out softly at the edges instead of cutting it
/// off: left and right for a row of cards, top and bottom for a page. An edge only fades while
/// there is more to scroll to on that side, and the fades ease in and out. Drawn as gradient
/// strips in the theme background colour laid over the content (the Compatibility renderer has no
/// clip_children masking, which rendered the content as white blocks).
/// </summary>
public partial class EdgeFade : MarginContainer
{
    private readonly bool _horizontal;
    private readonly float _near;
    private readonly float _far;
    private readonly Overlay _overlay = new();
    private ScrollContainer? _scroll;
    private float _a;
    private float _b;

    /// <param name="horizontal">Fade left/right (a row) or top/bottom (a page).</param>
    /// <param name="near">Fade length at the start (left / top), in px.</param>
    /// <param name="far">Fade length at the end (right / bottom), in px.</param>
    public EdgeFade(bool horizontal, float near, float far)
    {
        _horizontal = horizontal;
        _near = near;
        _far = far;
        MouseFilter = MouseFilterEnum.Pass;
        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            AddThemeConstantOverride($"margin_{side}", 0);
        }
        _overlay.Owner_ = this;
        _overlay.MouseFilter = MouseFilterEnum.Ignore;
        _overlay.FocusMode = FocusModeEnum.None;
        AddChild(_overlay);
        ChildEnteredTree += n =>
        {
            if (n is ScrollContainer s)
            {
                _scroll = s;
            }
            if (n != _overlay)
            {
                CallDeferred(Node.MethodName.MoveChild, _overlay, -1);
            }
        };
    }

    public override void _Process(double delta)
    {
        if (_scroll == null || !IsInstanceValid(_scroll) || _scroll.GetChildCount() == 0 || _scroll.GetChild(0) is not Control content)
        {
            return;
        }
        var pos = _horizontal ? _scroll.ScrollHorizontal : _scroll.ScrollVertical;
        var max = Math.Max(0, (_horizontal ? content.Size.X : content.Size.Y) - (_horizontal ? _scroll.Size.X : _scroll.Size.Y));
        var wantA = pos > 2 ? 1f : 0f;
        var wantB = pos < max - 2 ? 1f : 0f;
        var step = (float)delta * 6;
        var a = Mathf.MoveToward(_a, wantA, step);
        var b = Mathf.MoveToward(_b, wantB, step);
        if (a != _a || b != _b)
        {
            _a = a;
            _b = b;
            _overlay.QueueRedraw();
        }
    }

    private partial class Overlay : Control
    {
        public EdgeFade Owner_ = null!;

        public override void _Draw()
        {
            var f = Owner_;
            var bg = UiTheme.Bg;
            var clear = bg with { A = 0 };
            var len = f._horizontal ? Size.X : Size.Y;
            if (len <= 1)
            {
                return;
            }
            var near = Math.Min(f._near, len * 0.45f);
            var far = Math.Min(f._far, len * 0.45f);
            if (f._a > 0)
            {
                Strip(0, near, bg with { A = f._a }, clear);
            }
            if (f._b > 0)
            {
                Strip(len - far, len, clear, bg with { A = f._b });
            }
        }

        // Eased gradient: a few bands so the fade reads smooth rather than linear.
        private void Strip(float from, float to, Color start, Color end)
        {
            const int Bands = 6;
            for (var i = 0; i < Bands; i++)
            {
                var t0 = i / (float)Bands;
                var t1 = (i + 1) / (float)Bands;
                var c0 = start.Lerp(end, Smooth(t0));
                var c1 = start.Lerp(end, Smooth(t1));
                var p0 = Mathf.Lerp(from, to, t0);
                var p1 = Mathf.Lerp(from, to, t1);
                Vector2[] pts = _horizontalOf()
                    ? [new(p0, 0), new(p1, 0), new(p1, Size.Y), new(p0, Size.Y)]
                    : [new(0, p0), new(Size.X, p0), new(Size.X, p1), new(0, p1)];
                Color[] cols = _horizontalOf() ? [c0, c1, c1, c0] : [c0, c0, c1, c1];
                DrawPolygon(pts, cols);
            }
        }

        private bool _horizontalOf() => Owner_._horizontal;

        private static float Smooth(float t) => t * t * (3 - 2 * t);
    }
}
