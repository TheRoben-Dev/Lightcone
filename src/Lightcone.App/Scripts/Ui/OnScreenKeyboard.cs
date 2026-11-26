using Godot;

namespace Lightcone.App.Ui;

/// <summary>
/// Lightcone's own on-screen keyboard, so a controller can type on every OS (Windows, macOS and
/// desktop Linux have no controller keyboard of their own). D-pad and stick move between keys
/// (up/down land on the key nearest in x), A types, Y deletes, X adds a space, Start is Done.
/// Ported from Luma-Modman's keyboard (same layout, same buttons), so it feels the same in both
/// apps. Used by the search page and every text entry page.
/// </summary>
public partial class OnScreenKeyboard : VBoxContainer
{
    private static readonly string[] Lower = ["1234567890", "qwertyuiop", "asdfghjkl'", "zxcvbnm-_.", ":/@&?=+#!,"];

    private readonly List<Button> _letters = new();
    private readonly List<List<Button>> _rows = new();
    private bool _shift;

    /// <summary>Text to add (a key, or a paste).</summary>
    public event Action<string>? Typed;

    public event Action? Deleted;

    public event Action? Cleared;

    /// <param name="keyHeight">Key height in pixels.</param>
    /// <param name="paste">Adds a Paste key (API keys copied in a browser).</param>
    public OnScreenKeyboard(int keyHeight = 54, bool paste = true)
    {
        AddThemeConstantOverride("separation", 8);
        foreach (var line in Lower)
        {
            var row = new List<Button>();
            foreach (var ch in line)
            {
                var c = ch.ToString();
                var b = Key(c, () => Type(_shift ? c.ToUpperInvariant() : c), keyHeight);
                b.SetMeta("ch", c);
                _letters.Add(b);
                row.Add(b);
            }
            _rows.Add(row);
        }
        List<Button> bottom =
        [
            Key("Shift", ToggleShift, keyHeight, 2),
            Key("Space", () => Type(" "), keyHeight, 3),
            Key("Delete", () => Deleted?.Invoke(), keyHeight, 2),
        ];
        if (paste)
        {
            bottom.Add(Key("Paste", Paste, keyHeight, 2));
        }
        bottom.Add(Key("Clear", () => Cleared?.Invoke(), keyHeight, 2));
        _rows.Add(bottom);
        foreach (var row in _rows)
        {
            var h = new HBoxContainer();
            h.AddThemeConstantOverride("separation", 8);
            foreach (var b in row)
            {
                h.AddChild(b);
            }
            AddChild(h);
        }
        UpdateShift();
    }

    /// <summary>The key focus should start on ("q").</summary>
    public Button FirstKey => _rows[1][0];

    /// <summary>Key rows, top to bottom, for wiring focus to controls around the keyboard.</summary>
    public IReadOnlyList<IReadOnlyList<Button>> Rows => _rows;

    /// <summary>
    /// Wires focus between the keys, plus <paramref name="below"/> (rows of buttons under the
    /// keyboard, such as Done / Cancel) and the controls left and right of every row (null wraps).
    /// </summary>
    public void Wire(Control? above = null, IReadOnlyList<List<Button>>? below = null, Control? right = null)
    {
        var rows = _rows.Concat(below ?? []).ToList();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (var i = 0; i < row.Count; i++)
            {
                var b = row[i];
                var frac = (i + 0.5f) / row.Count;
                b.FocusNeighborLeft = (i > 0 ? row[i - 1] : row[^1]).GetPath();
                b.FocusNeighborRight = (i < row.Count - 1 ? row[i + 1] : (Control?)right ?? row[0]).GetPath();
                b.FocusNeighborTop = (r > 0 ? Nearest(rows[r - 1], frac) : (Control?)above ?? b).GetPath();
                b.FocusNeighborBottom = (r < rows.Count - 1 ? Nearest(rows[r + 1], frac) : b).GetPath();
                b.FocusPrevious = b.FocusNeighborLeft;
                b.FocusNext = b.FocusNeighborRight;
            }
        }
    }

    private static Button Key(string label, Action press, int height, int weight = 1)
    {
        var b = new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(0, height),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = weight,
            FocusMode = FocusModeEnum.All,
        };
        b.SetMeta(Ui.PosterTile.NoHelp, true);
        b.Pressed += press;
        Anim.PressFlash(b);
        return b;
    }

    private static Button Nearest(List<Button> row, float frac)
    {
        // Weighted positions: wide keys cover more of the row.
        var total = row.Sum(b => b.SizeFlagsStretchRatio);
        var x = 0f;
        foreach (var b in row)
        {
            x += b.SizeFlagsStretchRatio;
            if (frac <= x / total + 0.0001f)
            {
                return b;
            }
        }
        return row[^1];
    }

    private void Type(string s)
    {
        Typed?.Invoke(s);
        if (_shift)
        {
            ToggleShift();
        }
    }

    private void Paste()
    {
        var clip = DisplayServer.ClipboardGet().Trim().Replace("\r", "").Replace("\n", "");
        if (clip.Length == 0)
        {
            Main.Instance.Toast("The clipboard is empty. Copy the text in your browser first.");
            return;
        }
        Typed?.Invoke(clip);
    }

    private void ToggleShift()
    {
        _shift = !_shift;
        UpdateShift();
    }

    private void UpdateShift()
    {
        foreach (var b in _letters)
        {
            var ch = (string)b.GetMeta("ch");
            b.Text = _shift ? ch.ToUpperInvariant() : ch;
        }
    }

    /// <summary>
    /// Pad shortcuts while the keyboard is on screen: Y deletes, X adds a space. Returns true when
    /// handled. (Start = Done is up to the page.)
    /// </summary>
    public bool OnAction(string action)
    {
        if (action == Pad.Secondary)
        {
            Deleted?.Invoke();
            return true;
        }
        if (action == Pad.Tertiary)
        {
            Type(" ");
            return true;
        }
        return false;
    }

    /// <summary>A physical keyboard (or Steam's) types too: letters, Backspace, Ctrl+V. True when handled.</summary>
    public bool HandleKey(InputEventKey k)
    {
        if (k.CtrlPressed && k.Keycode == Godot.Key.V)
        {
            Paste();
        }
        else if (k.Keycode == Godot.Key.Backspace)
        {
            Deleted?.Invoke();
        }
        else if (k.Unicode >= 32 && !k.CtrlPressed && !k.AltPressed)
        {
            Typed?.Invoke(char.ConvertFromUtf32((int)k.Unicode));
        }
        else
        {
            return false;
        }
        return true;
    }
}
