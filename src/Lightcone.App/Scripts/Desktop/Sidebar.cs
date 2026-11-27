using Godot;
using Lightcone.App.Ui;

namespace Lightcone.App.Desktop;

/// <summary>
/// Desktop mode's left column: wordmark, a search box, one entry per section and a switch back to
/// Controller mode. Mirrors the Controller header (same sections, same search), just laid out for a mouse.
/// </summary>
public partial class Sidebar : PanelContainer
{
    private readonly List<Button> _items = new();
    private readonly LineEdit _search;

    public event Action<int>? Picked;
    public event Action<string>? Searched;
    public event Action? ControllerMode;

    private static readonly Dictionary<string, string> Icons = new()
    {
        ["Home"] = "⌂", ["Movies"] = "▣", ["Shows"] = "▤", ["Music"] = "♫", ["Sources"] = "⛁", ["Settings"] = "⚙",
    };

    public Sidebar(string[] tabs)
    {
        CustomMinimumSize = new Vector2(232, 0);
        AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel.Darkened(0.25f), 0, 0));
        var margin = new MarginContainer();
        foreach (var (side, v) in new[] { ("left", 16), ("right", 16), ("top", 22), ("bottom", 16) })
        {
            margin.AddThemeConstantOverride($"margin_{side}", v);
        }
        AddChild(margin);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        margin.AddChild(col);

        var mark = UiTheme.Label(UiTheme.Spec.Wordmark, 22, UiTheme.Nightgrid ? UiTheme.Text : UiTheme.AccentText);
        mark.CustomMinimumSize = new Vector2(0, 40);
        col.AddChild(mark);

        // Mouse and keyboard only: a controller searches with View (the search page has the on-screen keyboard).
        _search = new LineEdit { PlaceholderText = "Search  ( / )", ClearButtonEnabled = true, CustomMinimumSize = new Vector2(0, 40), FocusMode = FocusModeEnum.Click, VirtualKeyboardEnabled = false };
        _search.TextSubmitted += q =>
        {
            Searched?.Invoke(q.Trim());
            _search.ReleaseFocus();
        };
        col.AddChild(_search);
        col.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) });

        for (var i = 0; i < tabs.Length; i++)
        {
            var index = i;
            var b = new Button
            {
                Text = $"{Icons.GetValueOrDefault(tabs[i], "•")}   {tabs[i]}",
                Alignment = HorizontalAlignment.Left,
                ToggleMode = true,
                CustomMinimumSize = new Vector2(0, 42),
                FocusMode = FocusModeEnum.All,
            };
            b.AddThemeStyleboxOverride("normal", UiTheme.Box(Colors.Transparent, 0, 8));
            b.AddThemeStyleboxOverride("pressed", UiTheme.Box(UiTheme.Accent with { A = 0.18f }, 0, 8));
            b.AddThemeStyleboxOverride("hover", UiTheme.Box(UiTheme.PanelHi, 0, 8));
            b.AddThemeStyleboxOverride("hover_pressed", UiTheme.Box(UiTheme.Accent with { A = 0.25f }, 0, 8));
            b.AddThemeColorOverride("font_pressed_color", UiTheme.AccentText);
            b.AddThemeColorOverride("font_hover_pressed_color", UiTheme.AccentText);
            b.Pressed += () => Picked?.Invoke(index);
            _items.Add(b);
            col.AddChild(b);
        }

        col.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        col.AddChild(new DownloadIndicator(15));
        var pad = new Button { Text = "🎮  Controller mode", Alignment = HorizontalAlignment.Left, Flat = true, TooltipText = "Switch to the 10-foot layout (also in Settings → Layout)" };
        pad.Pressed += () => ControllerMode?.Invoke();
        col.AddChild(pad);
    }

    /// <summary>Highlights the section shown now; <paramref name="atRoot"/> false while a page is open on top.</summary>
    public void SetCurrent(int index, bool atRoot)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            _items[i].SetPressedNoSignal(i == index);
            _items[i].Modulate = i == index && !atRoot ? new Color(1, 1, 1, 0.75f) : Colors.White;
        }
    }

    public void FocusSearch()
    {
        _search.GrabFocus();
        _search.SelectAll();
    }

    /// <summary>True while the search box has keyboard focus (so letters aren't treated as shortcuts).</summary>
    public bool Typing => _search.HasFocus();
}
