using Lightcone.App.Ui;
using Godot;

namespace Lightcone.App.Screens;

/// <summary>
/// Text entry that works with a controller alone: Lightcone's on-screen keyboard (Y deletes, X adds
/// a space, Start is Done), plus Paste for API keys copied in a browser. A real keyboard types
/// straight into the field. Secret text (keys) is masked apart from its last four characters.
/// </summary>
public partial class TextEntryPage : Screen
{
    private readonly string _title;
    private readonly string _help;
    private readonly bool _secret;
    private readonly Action<string> _done;
    private string _text;
    private Label? _field;
    private OnScreenKeyboard? _keys;

    public TextEntryPage(string title, string current, Action<string> done, string help = "", bool secret = false)
    {
        _title = title;
        _text = current;
        _done = done;
        _help = help;
        _secret = secret;
    }

    public override IEnumerable<(string Action, string Label)> Hints =>
        [(Pad.Secondary, "Delete"), (Pad.Tertiary, "Space"), (Pad.Menu, "Done")];

    protected override void Build()
    {
        var center = new CenterContainer();
        AddChild(center);
        var col = Column(Desktop ? 10 : 14);
        col.CustomMinimumSize = new Vector2(Mathf.Min(980, GetViewportRect().Size.X - 80), 0);
        center.AddChild(col);
        col.AddChild(UiTheme.Label(_title, Desktop ? UiTheme.TitleSize - 6 : UiTheme.TitleSize));
        if (_help.Length > 0)
        {
            col.AddChild(UiTheme.Label(_help, UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        }
        _field = Field(col);
        ShowText();

        _keys = new OnScreenKeyboard(Desktop ? 42 : 52);
        _keys.Typed += t => SetText(_text.Length < 400 ? _text + t : _text);
        _keys.Deleted += Delete;
        _keys.Cleared += () => SetText("");
        col.AddChild(_keys);
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 12);
        col.AddChild(actions);
        var done = new ActionButton("Done", Finish, "Use this text.") { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var cancel = new ActionButton("Cancel", () => App.Pop(), "Go back without changing anything.") { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        actions.AddChild(done);
        actions.AddChild(cancel);
        _keys.Wire(below: [[done, cancel]]);
    }

    /// <summary>The text box shown above an on-screen keyboard (a label with a caret).</summary>
    internal static Label Field(Control parent, int height = 0)
    {
        var fieldBox = new PanelContainer();
        var fb = UiTheme.Box(UiTheme.PanelHi, 2, 12, UiTheme.Accent);
        fb.ContentMarginLeft = fb.ContentMarginRight = 18;
        fb.ContentMarginTop = fb.ContentMarginBottom = UiLayout.Desktop ? 8 : 12;
        fieldBox.AddThemeStyleboxOverride("panel", fb);
        fieldBox.CustomMinimumSize = new Vector2(0, height);
        parent.AddChild(fieldBox);
        var field = UiTheme.Label("", UiLayout.Desktop ? UiTheme.FontSize : UiTheme.FontSize + 4);
        field.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        field.ClipText = true;
        field.VerticalAlignment = VerticalAlignment.Center;
        fieldBox.AddChild(field);
        return field;
    }

    private void Delete()
    {
        if (_text.Length > 0)
        {
            SetText(_text[..^1]);
        }
    }

    private void SetText(string s)
    {
        _text = s;
        ShowText();
    }

    private void ShowText()
    {
        if (_field == null)
        {
            return;
        }
        var shown = _secret && _text.Length > 4 ? new string('•', Math.Min(24, _text.Length - 4)) + _text[^4..] : _text;
        _field.Text = shown + "▏";
    }

    private void Finish()
    {
        var text = _text.Trim();
        App.Pop();
        _done(text);
    }

    public override void OnAction(string action) => _keys?.OnAction(action);

    /// <summary>A real keyboard types into the field (Enter is Done); arrows and Escape keep navigating.</summary>
    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } k || App.Top != this || _keys == null)
        {
            return;
        }
        if (k.Keycode is Godot.Key.Enter or Godot.Key.KpEnter)
        {
            Finish();
        }
        else if (!_keys.HandleKey(k))
        {
            return;
        }
        GetViewport().SetInputAsHandled();
    }

    /// <summary>Start is Done.</summary>
    public override void _UnhandledInput(InputEvent e)
    {
        if (App.Top == this && e.IsActionPressed(Pad.Menu))
        {
            Finish();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void FocusDefault() => _keys?.FirstKey.GrabFocus();
}
