using Lightcone.App.Ui;
using Lightcone.Host;
using Godot;

namespace Lightcone.App.Screens;

/// <summary>
/// A full-screen page. Root sections (Home, Movies, Settings...) and pushed pages (details, search,
/// player, confirm) are all screens; Main keeps them on a stack and B pops.
/// Screens build their UI in code in Build(), after they are in the tree, so focus neighbours
/// can be wired with real node paths.
/// </summary>
public abstract partial class Screen : MarginContainer
{
    private Control? _lastFocus;

    protected static Main App => Main.Instance;
    protected static AppHost Host => Main.Instance.Host!;

    /// <summary>Overlay screens open as a card above the page behind, which stays visible but dimmed and blurred.</summary>
    public virtual bool IsOverlay => false;

    /// <summary>Full-screen pages (the player) hide the app chrome: header, sidebar, hint bar, mini player.</summary>
    public virtual bool FullScreen => false;

    /// <summary>True in Desktop mode (denser layout, mouse first).</summary>
    protected static bool Desktop => UiLayout.Desktop;

    /// <summary>Plays the open animation (overlays). Origin is the rect the card grows from, if any.</summary>
    public virtual void AnimateOpen()
    {
    }

    /// <summary>Plays the close animation, then calls done (overlays).</summary>
    public virtual void AnimateClose(Action done) => done();

    /// <summary>Context hints shown in the bottom bar (Accept/Back are added by Main).</summary>
    public virtual IEnumerable<(string Action, string Label)> Hints => [];

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Rebuild();
        // Grids are laid out for the page's width: rebuild when it really changes (window resize,
        // or the first layout pass putting the page where it belongs).
        ItemRectChanged += () =>
        {
            // (Not while sliding in or out: the page moves then, and settles where it belongs after.)
            if (WidthChanged())
            {
                Callable.From(() =>
                {
                    if (IsInstanceValid(this) && IsInsideTree() && WidthChanged())
                    {
                        Rebuild();
                    }
                }).CallDeferred();
            }
        };
    }

    private float _builtWidth = -1;
    private (float Tile, float Gap, float Reserve, int Columns) _builtGrid;

    private bool WidthChanged() =>
        UsesWidth && Modulate.A > 0.99f && Columns(AvailableWidth(), _builtGrid.Tile, _builtGrid.Gap, _builtGrid.Reserve) != _builtGrid.Columns;

    private static int Columns(float width, float tileW, float gap, float reserve) =>
        Math.Max(1, (int)((width - reserve - 16 + gap) / (tileW + gap)));

    /// <summary>Set when Build used <see cref="ColumnsFor"/>: the page rebuilds when its width changes.</summary>
    private bool UsesWidth { get; set; }

    /// <summary>
    /// Width from the page's left edge to the window's right margin. Measured against the window,
    /// not the page's own size, which a too-wide grid would stretch.
    /// </summary>
    private float AvailableWidth()
    {
        var view = GetViewportRect().Size.X;
        var left = IsInsideTree() ? GetGlobalRect().Position.X : 0;
        if (left <= 0 && Desktop)
        {
            left = 260;  // before the first layout: about the sidebar's width
        }
        return Math.Max(200, view - Math.Max(left, Desktop ? 0 : 40) - (Desktop ? 26 : 40));
    }

    /// <summary>
    /// How many tiles of width <paramref name="tileW"/> with <paramref name="gap"/> between them fit
    /// across the page, less <paramref name="reserve"/> pixels used by other things on the same line.
    /// </summary>
    protected int ColumnsFor(float tileW, float gap, float reserve = 0)
    {
        UsesWidth = true;
        _builtWidth = AvailableWidth();
        // (16: room for the scroller's gutter and the focus ring at the edges.)
        var columns = Columns(_builtWidth, tileW, gap, reserve);
        _builtGrid = (tileW, gap, reserve, columns);
        return columns;
    }

    /// <summary>Clears and rebuilds the page (after data changes), keeping focus near where it was.</summary>
    public void Rebuild()
    {
        var focusedIndex = FocusIndex();
        var scrolls = Scrollers(this).Select(x => x.ScrollVertical).ToList();
        foreach (var c in GetChildren())
        {
            RemoveChild(c);
            c.QueueFree();
        }
        _failBack = null;
        try
        {
            Build();
        }
        catch (Exception e)
        {
            // Never leave a half-built (or invisible) page holding focus: show what went wrong and a way back.
            GD.PushError($"{GetType().Name} could not be built: {e}");
            AppLog.Error($"{GetType().Name} could not be built: {e}");
            foreach (var c in GetChildren())
            {
                RemoveChild(c);
                c.QueueFree();
            }
            BuildFailed(e);
            return;
        }
        // Keep the lists where they were (theme switch, data change) instead of jumping to the top.
        var fresh = Scrollers(this);
        for (var i = 0; i < Math.Min(scrolls.Count, fresh.Count); i++)
        {
            foreach (var f in fresh[i].GetChildren().OfType<ScrollFollower>())
            {
                f.Restore(scrolls[i]);
            }
        }
        // (A page under an open card, rebuilt by a pick on that card, leaves the card its focus.)
        if (IsVisibleInTree() && !(Main.Instance?.TopOrNull is { IsOverlay: true } top && top != this))
        {
            if (!RefocusIndex(focusedIndex))
            {
                FocusDefault();
            }
        }
    }

    protected abstract void Build();

    private ActionButton? _failBack;

    private void BuildFailed(Exception e)
    {
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(560, 0) };
        var box = UiTheme.Box(UiTheme.Panel, 1, 18, UiTheme.Line);
        box.ContentMarginLeft = box.ContentMarginRight = box.ContentMarginTop = box.ContentMarginBottom = 26;
        panel.AddThemeStyleboxOverride("panel", box);
        center.AddChild(panel);
        var col = Column(12);
        panel.AddChild(col);
        col.AddChild(UiTheme.Label("This page couldn't open.", UiTheme.FontSize + 4, UiTheme.Text, wrap: true));
        col.AddChild(UiTheme.Label(e.Message, UiTheme.SmallSize + 1, UiTheme.Bad, wrap: true));
        col.AddChild(UiTheme.Label("The details are in the app's log. Please send it along with what you did.", UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        _failBack = new ActionButton("Back", () => App.Pop(), "Goes back to the previous page.");
        col.AddChild(_failBack);
        App?.Error($"Something went wrong: {e.Message}");
        if (IsVisibleInTree())
        {
            _failBack.CallDeferred(Control.MethodName.GrabFocus);
        }
    }

    /// <summary>Focuses the screen's primary control.</summary>
    public abstract void FocusDefault();

    /// <summary>Called when B is pressed while this screen is on top. True = handled here.</summary>
    public virtual bool OnBack() => false;

    /// <summary>A key press with no other meaning (Ctrl+F, Ctrl+Up...). True = handled here.</summary>
    public virtual bool OnShortcut(InputEventKey key) => false;

    /// <summary>Called for the Secondary / Tertiary context buttons.</summary>
    public virtual void OnAction(string action)
    {
    }

    public void Remember() => _lastFocus = GetViewport()?.GuiGetFocusOwner();

    public void Restore()
    {
        if (_lastFocus != null && IsInstanceValid(_lastFocus) && _lastFocus.IsVisibleInTree() && IsAncestorOf(_lastFocus))
        {
            _lastFocus.GrabFocus();
        }
        else if (_failBack != null && IsInstanceValid(_failBack))
        {
            _failBack.GrabFocus();
        }
        else
        {
            FocusDefault();
        }
    }

    private static List<ScrollContainer> Scrollers(Node root)
    {
        var list = new List<ScrollContainer>();
        void Walk(Node n)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is ScrollContainer s && !s.IsQueuedForDeletion())
                {
                    list.Add(s);
                }
                Walk(c);
            }
        }
        Walk(root);
        return list;
    }

    private int FocusIndex()
    {
        var f = GetViewport()?.GuiGetFocusOwner();
        return f != null && IsAncestorOf(f) ? Focusables().IndexOf(f) : -1;
    }

    private bool RefocusIndex(int index)
    {
        var all = Focusables();
        if (index < 0 || all.Count == 0)
        {
            return false;
        }
        all[Math.Min(index, all.Count - 1)].GrabFocus();
        return true;
    }

    private List<Control> Focusables()
    {
        var list = new List<Control>();
        void Walk(Node n)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is Control { FocusMode: FocusModeEnum.All } ctl && ctl.IsVisibleInTree())
                {
                    list.Add(ctl);
                }
                Walk(c);
            }
        }
        Walk(this);
        return list;
    }

    // ---- small builders shared by screens ----

    /// <summary>Reads something for the page; a failure shows a note there instead of stopping the page from opening.</summary>
    protected T Try<T>(Func<T> read, T fallback, string what, Control noteParent)
    {
        try
        {
            return read();
        }
        catch (Exception e)
        {
            GD.PushError($"{GetType().Name}: reading {what} failed: {e}");
            AppLog.Error($"{GetType().Name}: reading {what} failed: {e}");
            noteParent.AddChild(UiTheme.Label($"Couldn't read {what}: {e.Message}", UiTheme.SmallSize + 1, UiTheme.Warn, wrap: true));
            return fallback;
        }
    }

    protected static VBoxContainer Column(int separation = 10)
    {
        var v = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", separation);
        return v;
    }

    /// <summary>Vertical scroller that glides to keep the focused control visible.</summary>
    protected static ScrollContainer Scroller(Control content)
    {
        var s = new ScrollContainer
        {
            FollowFocus = false,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            // No visible bar: lists scroll with focus, the stick, the wheel or a finger drag.
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        // Small inset so the focus border of edge rows is never clipped.
        var gutter = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        gutter.AddThemeConstantOverride("margin_right", 6);
        gutter.AddThemeConstantOverride("margin_left", 4);
        gutter.AddThemeConstantOverride("margin_top", 4);
        gutter.AddThemeConstantOverride("margin_bottom", 4);
        gutter.AddChild(content);
        s.AddChild(gutter);
        s.AddChild(new ScrollFollower());
        return s;
    }

    /// <summary>
    /// A card the size of the game card, centred, holding <paramref name="content"/>: for pages
    /// opened from the game card, so they sit over the dimmed library the same way.
    /// </summary>
    protected Control CardFrame(Control content)
    {
        var center = new CenterContainer();
        var view = GetViewportRect().Size;
        var card = new PanelContainer
        {
            CustomMinimumSize = new Vector2(Mathf.Min(1120, view.X * 0.88f), Mathf.Min(600, (view.Y - 56) * 0.84f)),
        };
        var box = UiTheme.Box(UiTheme.Panel, 1, 22, UiTheme.Line);
        box.ShadowColor = new Color(0, 0, 0, 0.55f);
        box.ShadowSize = 28;
        box.ShadowOffset = new Vector2(0, 10);
        box.ContentMarginLeft = box.ContentMarginRight = 30;
        box.ContentMarginTop = box.ContentMarginBottom = 26;
        card.AddThemeStyleboxOverride("panel", box);
        center.AddChild(card);
        card.AddChild(content);
        return center;
    }

    protected static Control Section(string text)
    {
        var l = UiTheme.Label(Loc.Upper(text), UiTheme.SmallSize, UiTheme.Dim);
        l.CustomMinimumSize = new Vector2(0, 34);
        l.VerticalAlignment = VerticalAlignment.Bottom;
        if (UiTheme.Nightgrid)
        {
            // "// SECTION" headers with a magenta slash pair.
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var slash = UiTheme.Label("//", UiTheme.SmallSize, UiTheme.Accent);
            slash.CustomMinimumSize = l.CustomMinimumSize;
            slash.VerticalAlignment = VerticalAlignment.Bottom;
            l.AddThemeColorOverride("font_color", UiTheme.Text);
            row.AddChild(slash);
            row.AddChild(l);
            return row;
        }
        return l;
    }
}
