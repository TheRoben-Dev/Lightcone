using Godot;

namespace Lightcone.App.Ui;

/// <summary>
/// Smooth replacement for ScrollContainer.FollowFocus: when focus moves inside the scroller,
/// the list glides (critically damped) until the focused control is fully visible with a
/// margin. It keeps following while the focused row grows (expanding help), and backs off as
/// soon as anything else scrolls the list (touch drag, momentum, mouse wheel) until the next
/// focus move. Added as a child of the ScrollContainer by Screen.Scroller.
/// </summary>
public partial class ScrollFollower : Node
{
    private ScrollContainer _s = null!;

    /// <summary>Follows along x (a row of tiles) instead of y (a page).</summary>
    public bool Horizontal { get; init; }

    /// <summary>
    /// How much room to keep past the focused control, as a share of the view: the list starts
    /// moving before the selector reaches the edge, so it travels with it instead of jumping.
    /// </summary>
    public float Lead { get; init; } = 0.18f;

    private int Scroll
    {
        get => Horizontal ? _s.ScrollHorizontal : _s.ScrollVertical;
        set
        {
            if (Horizontal)
            {
                _s.ScrollHorizontal = value;
            }
            else
            {
                _s.ScrollVertical = value;
            }
        }
    }
    private bool _active = true;
    private float _pos;
    private int _lastSet = -1;
    private int _frames;
    private int? _restore;

    /// <summary>Scroll offset to restore on the first frames (after a rebuild).</summary>
    public void Restore(int value) => _restore = value;

    /// <summary>Stops following until focus moves again (a finger is scrolling).</summary>
    public static void Pause(ScrollContainer s)
    {
        foreach (var n in s.GetChildren())
        {
            if (n is ScrollFollower f)
            {
                f._active = false;
            }
        }
    }

    public override void _Ready()
    {
        _s = GetParent<ScrollContainer>();
        _s.FollowFocus = false;
        _pos = Scroll;
    }

    // Enter/exit pair (not _Ready): the content area is re-parented when the layout changes.
    public override void _EnterTree() => GetViewport().GuiFocusChanged += OnFocus;

    public override void _ExitTree()
    {
        if (GetViewport() is { } vp)
        {
            vp.GuiFocusChanged -= OnFocus;
        }
    }

    private void OnFocus(Control c)
    {
        if (IsInstanceValid(_s) && _s.IsAncestorOf(c))
        {
            TouchScroll.StopMomentum();
            _active = true;
            _pos = Scroll;
            _lastSet = Scroll;
        }
    }

    public override void _Process(double delta)
    {
        _frames++;
        if (_restore is { } r)
        {
            Scroll = r;
            _pos = _lastSet = Scroll;
            if (_frames > 3)
            {
                _restore = null;
            }
            return;
        }
        if (!_active)
        {
            return;
        }
        if (_lastSet >= 0 && Scroll != _lastSet)
        {
            _active = false;  // someone else scrolled (touch, wheel): leave it there
            return;
        }
        var focus = _s.GetViewport().GuiGetFocusOwner();
        if (focus == null || !_s.IsAncestorOf(focus) || _s.GetChildCount() == 0 || _s.GetChild(0) is not Control content)
        {
            return;
        }
        // The tile that holds focus (a tile's inner button can be the focus owner).
        var rect = focus.GetGlobalRect();
        var top = Horizontal ? rect.Position.X - content.GetGlobalRect().Position.X : rect.Position.Y - content.GetGlobalRect().Position.Y;
        var bottom = top + (Horizontal ? rect.Size.X : rect.Size.Y);
        var view = Horizontal ? _s.Size.X : _s.Size.Y;
        var margin = Math.Clamp(view * Lead, 28f, 220f);
        if (bottom - top + 2 * margin > view)
        {
            margin = Math.Max(12f, (view - (bottom - top)) / 2);
        }
        var target = _pos;
        if (bottom - top + 2 * margin > view || top - margin < _pos)
        {
            target = top - margin;
        }
        else if (bottom + margin > _pos + view)
        {
            target = bottom + margin - view;
        }
        // Near the top of the page (the hero, a detail page's buttons), go all the way up so the
        // header art and titles above the focused control show again.
        if (!Horizontal && target < view * 0.4f && bottom + margin <= view)
        {
            target = 0;
        }
        var max = Math.Max(0, (Horizontal ? content.Size.X : content.Size.Y) - view);
        target = Math.Clamp(target, 0, max);
        if (Math.Abs(target - _pos) < 0.5f)
        {
            return;
        }
        if (!Anim.Enabled || _frames < 15)
        {
            _pos = target;  // fresh page (or reduced motion): no glide
        }
        else
        {
            // Exponential ease-out (~200 ms to settle, in step with the focus ring's glide), at
            // least one pixel per frame so it never stalls.
            var next = Mathf.Lerp(_pos, target, 1 - (float)Math.Exp(-delta * 16));
            _pos = Math.Abs(next - _pos) < 1 ? Mathf.MoveToward(_pos, target, 1) : next;
        }
        Scroll = (int)Math.Round(_pos);
        _lastSet = Scroll;
    }
}
