using Godot;

namespace Lightcone.App.Ui;

public enum PadFamily
{
    Keyboard,
    Xbox,
    PlayStation,
    Nintendo,
    /// <summary>Steam Deck's own buttons (A B X Y, L1 / R1, L2 / R2, ☰ and ⧉), drawn without colours.</summary>
    Deck,
}

/// <summary>
/// App-side controller handling. The app always uses the button *labelled* A to confirm and B to
/// go back, on every controller, whatever remaps the user sets up for games (those live in the
/// player's own settings and never touch this). Joypad buttons are translated to actions
/// here instead of through the InputMap so the Nintendo A/B swap can be done per device, and so
/// held directions repeat at a controlled rate.
/// </summary>
public static class Pad
{
    public const string Accept = "ui_accept";
    public const string Back = "ui_cancel";
    public const string TabPrev = "tab_prev";
    public const string TabNext = "tab_next";
    /// <summary>North face button (Y on Xbox): context action on the current screen.</summary>
    public const string Secondary = "secondary";
    /// <summary>West face button (X on Xbox): second context action.</summary>
    public const string Tertiary = "tertiary";
    /// <summary>View / Select button: search (Steam's on-screen keyboard in Game Mode).</summary>
    public const string Switch = "search";
    /// <summary>Menu / Start button: options for what's on screen (player: tracks and settings).</summary>
    public const string Menu = "menu";
    /// <summary>Left / right trigger: seek back / forward in the player, page up / down in lists.</summary>
    public const string SeekBack = "seek_back";
    public const string SeekForward = "seek_forward";

    private const float TriggerOn = 0.6f, TriggerOff = 0.3f;
    private const float TriggerRepeat = 0.33f;
    private static readonly Dictionary<JoyAxis, float> _triggerHeld = new();

    private static readonly string[] Directions = ["ui_up", "ui_down", "ui_left", "ui_right"];
    private const float RepeatDelay = 0.38f;
    private const float RepeatRate = 0.075f;
    private const float StickDeadzone = 0.55f;

    public static PadFamily LastFamily { get; private set; } = PadFamily.Keyboard;

    /// <summary>The joypad that was used last (for rumble), or null before any pad input.</summary>
    public static int? LastDevice { get; private set; }
    public static event Action? FamilyChanged;

    private static string? _held;
    private static float _heldFor;
    private static float _sinceRepeat;

    // Direction state comes from the events themselves (not polling Input), so it works for any
    // device SDL reports and can be exercised by the smoke test with synthetic events.
    private static readonly List<string> _dpadHeld = new();
    private static readonly Dictionary<int, Vector2> _sticks = new();

    /// <summary>
    /// True on handheld PCs (Steam Deck, ROG Ally, Legion Go, MSI Claw). Used to show gamepad
    /// glyphs even when Steam's desktop layout turns the built-in controls into keyboard keys.
    /// </summary>
    public static bool IsHandheld { get; } = DetectHandheld();

    private static bool DetectHandheld()
    {
        try
        {
            string Read(string f) => File.Exists(f) ? File.ReadAllText(f).Trim() : "";
            string vendor, product;
            if (OperatingSystem.IsWindows())
            {
                // Windows handhelds (ROG Ally, Legion Go, Claw): the same SMBIOS fields, from the registry.
                using var bios = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                string Reg(string name) => bios?.GetValue(name) as string ?? "";
                vendor = Reg("BaseBoardManufacturer") + " " + Reg("SystemManufacturer");
                product = Reg("SystemProductName") + " " + Reg("BaseBoardProduct");
            }
            else
            {
                vendor = Read("/sys/class/dmi/id/board_vendor") + " " + Read("/sys/class/dmi/id/sys_vendor");
                product = Read("/sys/class/dmi/id/product_name") + " " + Read("/sys/class/dmi/id/board_name");
            }
            return vendor.Contains("Valve") || product.Contains("Jupiter") || product.Contains("Galileo")
                || product.Contains("ROG Ally") || product.Contains("RC71L") || product.Contains("RC72L")
                || product.Contains("Legion Go") || product.Contains("83E1") || product.Contains("Claw");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static void Setup()
    {
        // Keyboard keeps Godot's defaults (arrows, Enter, Esc). Drop the default joypad events so
        // they don't fire twice alongside our own translation.
        foreach (var action in Directions.Concat([Accept, Back, "ui_select", "ui_focus_next", "ui_focus_prev"]))
        {
            foreach (var e in InputMap.ActionGetEvents(action))
            {
                if (e is InputEventJoypadButton or InputEventJoypadMotion)
                {
                    InputMap.ActionEraseEvent(action, e);
                }
            }
        }
        // Hot-plug (Bluetooth idle timeout, unplugging, docking, Steam swapping its virtual pad):
        // forget everything about a pad that went away, so nothing sticks and nothing (rumble,
        // glyph detection) ever touches a device id that no longer exists. Godot reuses ids, so a
        // stale one would otherwise point at the next controller that connects.
        Input.Singleton.JoyConnectionChanged += (device, connected) =>
        {
            try
            {
                OnConnectionChanged((int)device, connected);
            }
            catch (Exception e)
            {
                Lightcone.Host.AppLog.Error($"pad {device} hot-plug: {e}");
            }
        };
        AddKeyAction(TabPrev, Key.Q, Key.Pageup);
        AddKeyAction(TabNext, Key.E, Key.Pagedown);
        AddKeyAction(Secondary, Key.R);
        AddKeyAction(Tertiary, Key.F);
        AddKeyAction(Switch, Key.Slash);
        AddKeyAction(Menu, Key.M);
        AddKeyAction(SeekBack, Key.J, Key.Bracketleft);
        AddKeyAction(SeekForward, Key.L, Key.Bracketright);
        // Steam's Desktop Mode layout and handheld "lizard mode" send keys and mouse clicks
        // instead of gamepad buttons; accept the common ones.
        AddKeyAction(Back, Key.Backspace);
        InputMap.ActionAddEvent(Back, new InputEventMouseButton { ButtonIndex = MouseButton.Xbutton1 });
        InputMap.ActionAddEvent(TabPrev, new InputEventMouseButton { ButtonIndex = MouseButton.WheelLeft });
        InputMap.ActionAddEvent(TabNext, new InputEventMouseButton { ButtonIndex = MouseButton.WheelRight });
    }

    private static void OnConnectionChanged(int device, bool connected)
    {
        Lightcone.Host.AppLog.Info($"pad {device} {(connected ? "connected" : "disconnected")}; pads: {DescribePads()}");
        // Buttons this pad was holding: release the action once no other pad holds it either.
        foreach (var (button, down) in _buttonDown)
        {
            if (down.Remove(device) && down.Count == 0 && !connected && ActionFor(button, LastFamily) is { } action && !Directions.Contains(action))
            {
                Emit(action, false);
            }
        }
        foreach (var button in _lastPress.Where(p => p.Value.Device == device).Select(p => p.Key).ToList())
        {
            _lastPress.Remove(button);
        }
        _sticks.Remove(device);
        if (_glyphDevice == device)
        {
            _glyphDevice = -1;  // re-detect the glyphs on the next press, whichever pad it is
        }
        if (connected)
        {
            return;
        }
        _dpadHeld.Clear();
        _triggerHeld.Clear();
        _held = null;
        if (LastDevice == device)
        {
            // Rumble goes to another pad that's still here, or nowhere.
            var left = Input.GetConnectedJoypads();
            LastDevice = left.Count > 0 ? left[0] : null;
        }
    }

    /// <summary>True when the joypad id belongs to a controller that is connected right now.</summary>
    public static bool IsConnected(int device) => device >= 0 && Input.GetConnectedJoypads().Contains(device);

    /// <summary>Rumble on a pad, if it's still connected (it may have timed out or been unplugged).</summary>
    public static void Rumble(int device, float weak, float strong, float seconds)
    {
        try
        {
            if (IsConnected(device))
            {
                Input.StartJoyVibration(device, weak, strong, seconds);
            }
        }
        catch (Exception e)
        {
            Lightcone.Host.AppLog.Warn($"rumble on pad {device}: {e.Message}");
        }
    }

    private static void AddKeyAction(string action, params Key[] keys)
    {
        if (!InputMap.HasAction(action))
        {
            InputMap.AddAction(action);
        }
        foreach (var k in keys)
        {
            InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = k });
        }
    }

    public static PadFamily FamilyOf(int device)
    {
        var name = Input.GetJoyName(device).ToLowerInvariant();
        if (name.Contains("nintendo") || name.Contains("pro controller") || name.Contains("joy-con") || name.Contains("switch"))
        {
            return PadFamily.Nintendo;
        }
        if (name.Contains("playstation") || name.Contains("dualsense") || name.Contains("dualshock") || name.Contains("ps4") || name.Contains("ps5") || name.Contains("sony"))
        {
            return PadFamily.PlayStation;
        }
        return PadFamily.Xbox;  // Xbox, Steam Deck, ROG Ally, Steam Virtual Gamepad, generic XInput
    }

    /// <summary>
    /// Translates a raw input event. Returns true when the event was consumed (the caller should
    /// mark it handled); the translated action is injected with Input.ParseInputEvent.
    /// </summary>
    public static bool Translate(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey { Keycode: Key.Tab } tab:
                // Tab / Shift+Tab (and Ctrl+Tab) switch the top tabs; focus moves with arrows.
                if (tab.Pressed && !tab.Echo)
                {
                    SetFamily(PadFamily.Keyboard);
                    Emit(tab.ShiftPressed ? TabPrev : TabNext, true);
                    Emit(tab.ShiftPressed ? TabPrev : TabNext, false);
                }
                return true;
            case InputEventKey { Pressed: true }:
                SetFamily(PadFamily.Keyboard);
                return false;
            case InputEventJoypadButton jb:
                if (IsDuplicate(jb))
                {
                    return true;
                }
                LastDevice = jb.Device;
                if (jb.Device != _glyphDevice)
                {
                    _glyphDevice = jb.Device;
                    _padGlyphs = GlyphFamilyOf(jb.Device);
                    if (LastFamily != PadFamily.Keyboard)
                    {
                        FamilyChanged?.Invoke();
                    }
                }
                SetFamily(FamilyOf(jb.Device));
                var action = ActionFor(jb.ButtonIndex, LastFamily);
                if (action == null)
                {
                    return true;
                }
                if (Directions.Contains(action))
                {
                    _dpadHeld.Remove(action);
                    if (jb.Pressed)
                    {
                        _dpadHeld.Add(action);  // most recent last
                        // Move on the press itself, so a tap shorter than a frame still counts
                        // (frames are slow in low-processor mode). Process() only repeats.
                        StartHold(action);
                    }
                    return true;
                }
                Emit(action, jb.Pressed);
                return true;
            case InputEventJoypadMotion { Axis: JoyAxis.TriggerLeft or JoyAxis.TriggerRight } trig:
                LastDevice = trig.Device;
                var seek = trig.Axis == JoyAxis.TriggerLeft ? SeekBack : SeekForward;
                var held = _triggerHeld.ContainsKey(trig.Axis);
                if (!held && trig.AxisValue > TriggerOn)
                {
                    SetFamily(FamilyOf(trig.Device));
                    _triggerHeld[trig.Axis] = 0;
                    Tap(seek);
                }
                else if (held && trig.AxisValue < TriggerOff)
                {
                    _triggerHeld.Remove(trig.Axis);
                }
                return true;
            case InputEventJoypadMotion jm:
                if (jm.Axis is JoyAxis.LeftX or JoyAxis.LeftY)
                {
                    var v = _sticks.GetValueOrDefault(jm.Device);
                    v = jm.Axis == JoyAxis.LeftX ? v with { X = jm.AxisValue } : v with { Y = jm.AxisValue };
                    _sticks[jm.Device] = v;
                }
                if (Mathf.Abs(jm.AxisValue) > StickDeadzone)
                {
                    LastDevice = jm.Device;
                    SetFamily(FamilyOf(jm.Device));
                }
                return true;
            default:
                return false;
        }
    }

    // The same physical controller can show up as two joypads: in Game Mode Steam Input exposes
    // a virtual pad while SDL also sees the raw device, so every button arrives twice (the stick
    // doesn't double, since it's read as state). A button counts as held while any device holds
    // it: a press only counts if no other device already holds that button and the same button
    // wasn't pressed on another device a moment ago; a release only counts once every device has
    // let go.
    private static readonly Dictionary<JoyButton, HashSet<int>> _buttonDown = new();
    private static readonly Dictionary<JoyButton, (int Device, ulong At)> _lastPress = new();
    private const ulong EchoWindowMs = 80;

    private static bool IsDuplicate(InputEventJoypadButton jb)
    {
        if (!_buttonDown.TryGetValue(jb.ButtonIndex, out var down))
        {
            _buttonDown[jb.ButtonIndex] = down = new HashSet<int>();
        }
        var now = Time.GetTicksMsec();
        if (jb.Pressed)
        {
            var othersHolding = down.Any(d => d != jb.Device);
            var already = !down.Add(jb.Device);
            // Another device echoing this press a moment later.
            var echo = _lastPress.TryGetValue(jb.ButtonIndex, out var last) && last.Device != jb.Device && now - last.At < EchoWindowMs;
            if (othersHolding || already || echo)
            {
                return true;
            }
            _lastPress[jb.ButtonIndex] = (jb.Device, now);
            return false;
        }
        if (!down.Remove(jb.Device))
        {
            // Release from a device whose press we never saw (or an echo already released).
            return down.Count > 0 || (_lastPress.TryGetValue(jb.ButtonIndex, out var l) && l.Device != jb.Device);
        }
        return down.Count > 0;
    }

    private static string? ActionFor(JoyButton b, PadFamily family)
    {
        // SDL reports buttons by position: A = south, B = east. On Nintendo pads the "A" label
        // is east, so swap to keep "the button labelled A confirms".
        var nintendo = family == PadFamily.Nintendo;
        return b switch
        {
            JoyButton.A => nintendo ? Back : Accept,
            JoyButton.B => nintendo ? Accept : Back,
            JoyButton.Y => nintendo ? Tertiary : Secondary,
            JoyButton.X => nintendo ? Secondary : Tertiary,
            JoyButton.LeftShoulder => TabPrev,
            JoyButton.RightShoulder => TabNext,
            JoyButton.Back => Switch,
            JoyButton.Start => Menu,
            JoyButton.DpadUp => "ui_up",
            JoyButton.DpadDown => "ui_down",
            JoyButton.DpadLeft => "ui_left",
            JoyButton.DpadRight => "ui_right",
            _ => null,
        };
    }

    private static void StartHold(string dir)
    {
        _held = dir;
        _heldFor = 0;
        _sinceRepeat = 0;
        Tap(dir);
    }

    /// <summary>Called every frame: d-pad and left stick with delayed auto-repeat.</summary>
    public static void Process(double delta)
    {
        // Held triggers keep seeking (scrubbing) at a steady rate.
        foreach (var axis in _triggerHeld.Keys.ToList())
        {
            _triggerHeld[axis] += (float)delta;
            if (_triggerHeld[axis] >= TriggerRepeat + RepeatDelay)
            {
                _triggerHeld[axis] = RepeatDelay;
                Tap(axis == JoyAxis.TriggerLeft ? SeekBack : SeekForward);
            }
        }
        var dir = CurrentDirection();
        if (dir != _held)
        {
            if (dir == null)
            {
                _held = null;
            }
            else
            {
                StartHold(dir);
            }
            return;
        }
        if (dir == null)
        {
            return;
        }
        var before = _heldFor;
        _heldFor += (float)delta;
        if (_heldFor < RepeatDelay)
        {
            return;
        }
        // Count only the time past the delay, and keep the remainder after each repeat, so the
        // repeat rate doesn't depend on the frame rate (low-processor mode and slow frames would
        // otherwise drop repeats). At most 3 per frame, so a hitch doesn't fling focus.
        _sinceRepeat += before < RepeatDelay ? _heldFor - RepeatDelay : (float)delta;
        for (var n = 0; n < 3 && _sinceRepeat >= RepeatRate; n++)
        {
            _sinceRepeat -= RepeatRate;
            Tap(dir);
        }
        _sinceRepeat = Math.Min(_sinceRepeat, RepeatRate);
    }

    private static string? CurrentDirection()
    {
        if (_dpadHeld.Count > 0)
        {
            return _dpadHeld[^1];
        }
        foreach (var (x, y) in _sticks.Values.Select(v => (v.X, v.Y)))
        {
            if (Mathf.Max(Mathf.Abs(x), Mathf.Abs(y)) < StickDeadzone)
            {
                continue;
            }
            // Dominant axis only: no diagonal moves.
            return Mathf.Abs(x) > Mathf.Abs(y) ? (x < 0 ? "ui_left" : "ui_right") : (y < 0 ? "ui_up" : "ui_down");
        }
        return null;
    }

    /// <summary>Forget held directions (on focus loss / game start) so nothing keeps repeating.</summary>
    public static void ReleaseAll()
    {
        _dpadHeld.Clear();
        _buttonDown.Clear();
        _sticks.Clear();
        _triggerHeld.Clear();
        _held = null;
    }

    private static void Tap(string action)
    {
        Emit(action, true);
        Emit(action, false);
    }

    private static void Emit(string action, bool pressed) =>
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = pressed });

    private static void SetFamily(PadFamily f)
    {
        if (f != LastFamily)
        {
            LastFamily = f;
            FamilyChanged?.Invoke();
        }
    }

    /// <summary>A connected controller as SDL reports it.</summary>
    public sealed record PadInfo(string Guid, string Name, int Vendor, int Product);

    public static List<PadInfo> AllPads() => Input.GetConnectedJoypads().Select(d =>
    {
        var info = Input.GetJoyInfo(d);
        int Id(string k) => info.TryGetValue(k, out var v) ? v.AsInt32() : 0;
        return new PadInfo(Input.GetJoyGuid(d), Input.GetJoyName(d), Id("vendor_id"), Id("product_id"));
    }).ToList();

    /// <summary>One line per connected pad, for diagnostics ("name 28de:11ff guid").</summary>
    public static string DescribePads()
    {
        var pads = AllPads();
        return pads.Count == 0 ? "no controller detected" :
            string.Join("; ", pads.Select(p => $"{p.Name} [{p.Vendor:x4}:{p.Product:x4}, {(p.Guid.Length > 0 ? p.Guid : "no guid")}]"));
    }

    /// <summary>Settings → Layout → Button glyphs: "auto", "xbox", "playstation", "nintendo" or "deck".</summary>
    public static string GlyphStyle
    {
        get => _glyphStyle;
        set
        {
            if (value != _glyphStyle)
            {
                _glyphStyle = value;
                FamilyChanged?.Invoke();
            }
        }
    }

    private static string _glyphStyle = "auto";
    private static PadFamily _padGlyphs = PadFamily.Xbox;
    private static int _glyphDevice = -1;

    private static PadFamily Parse(string f) => f switch
    {
        "playstation" => PadFamily.PlayStation,
        "nintendo" => PadFamily.Nintendo,
        "deck" => PadFamily.Deck,
        _ => PadFamily.Xbox,
    };

    /// <summary>
    /// The buttons printed on the pad in use: the real controller behind Steam's virtual gamepad
    /// (Steam Input), else its USB vendor or name; the Deck's own controls on a Deck.
    /// </summary>
    public static PadFamily GlyphFamilyOf(int device)
    {
        var name = Input.GetJoyName(device);
        var info = Input.GetJoyInfo(device);
        int Id(string k) => info.TryGetValue(k, out var v) ? v.AsInt32() : 0;
        if (Lightcone.Host.SteamPads.FamilyOfVendor(Id("vendor_id"), Id("product_id")) is { } byVendor)
        {
            return Parse(byVendor);
        }
        if (name.Contains("Steam Virtual", StringComparison.OrdinalIgnoreCase) || Id("vendor_id") == 0x28de)
        {
            var pads = Lightcone.Host.SteamPads.VirtualPads();
            var slot = Input.GetConnectedJoypads().IndexOf(device);
            if (pads.Count > 0)
            {
                return Parse(pads[Math.Clamp(slot, 0, pads.Count - 1)]);
            }
            return IsDeck ? PadFamily.Deck : PadFamily.Xbox;
        }
        var byName = Parse(Lightcone.Host.SteamPads.FamilyOf(name));
        return byName == PadFamily.Xbox && IsDeck && name.Contains("Deck", StringComparison.OrdinalIgnoreCase) ? PadFamily.Deck : byName;
    }

    /// <summary>The detected glyph set, for Settings.</summary>
    public static string GlyphFamilyName() => (LastFamily == PadFamily.Keyboard ? (IsDeck ? PadFamily.Deck : IsHandheld ? PadFamily.Xbox : PadFamily.Keyboard) : _padGlyphs) switch
    {
        PadFamily.PlayStation => "PlayStation",
        PadFamily.Nintendo => "Nintendo",
        PadFamily.Deck => "Steam Deck",
        PadFamily.Keyboard => "keyboard",
        _ => "Xbox",
    };

    /// <summary>True on a Steam Deck (LCD or OLED).</summary>
    public static bool IsDeck { get; } = DetectDeck();

    private static bool DetectDeck()
    {
        try
        {
            var product = File.Exists("/sys/class/dmi/id/product_name") ? File.ReadAllText("/sys/class/dmi/id/product_name") : "";
            return product.Contains("Jupiter") || product.Contains("Galileo");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Which button set to draw: the chosen style, else the detected pad; handhelds show pad glyphs even when Steam sends keys.</summary>
    public static PadFamily GlyphFamily
    {
        get
        {
            var pad = LastFamily != PadFamily.Keyboard || IsHandheld;
            if (!pad)
            {
                return PadFamily.Keyboard;
            }
            if (_glyphStyle != "auto")
            {
                return Parse(_glyphStyle);
            }
            if (LastFamily == PadFamily.Keyboard)
            {
                return IsDeck ? PadFamily.Deck : PadFamily.Xbox;
            }
            return _padGlyphs;
        }
    }

    /// <summary>The label printed on the button for an action, for the current controller.</summary>
    public static string Glyph(string action) => (GlyphFamily, action) switch
    {
        (PadFamily.Keyboard, Accept) => "Enter",
        (PadFamily.Keyboard, Back) => "Esc",
        (PadFamily.Keyboard, TabPrev) => "Q",
        (PadFamily.Keyboard, TabNext) => "E",
        (PadFamily.Keyboard, Secondary) => "R",
        (PadFamily.Keyboard, Tertiary) => "F",
        (PadFamily.Keyboard, Switch) => "/",
        (PadFamily.Keyboard, Menu) => "M",
        (PadFamily.Keyboard, SeekBack) => "J",
        (PadFamily.Keyboard, SeekForward) => "L",
        (PadFamily.PlayStation, Menu) => "Options",
        (PadFamily.Nintendo, Menu) => "+",
        (PadFamily.Deck, Menu) => "☰",
        (PadFamily.Deck, SeekBack) => "L2",
        (PadFamily.Deck, SeekForward) => "R2",
        (PadFamily.Deck, Switch) => "⧉",
        (PadFamily.Deck, TabPrev) => "L1",
        (PadFamily.Deck, TabNext) => "R1",
        (_, Menu) => "Menu",
        (PadFamily.PlayStation, SeekBack) => "L2",
        (PadFamily.PlayStation, SeekForward) => "R2",
        (PadFamily.Nintendo, SeekBack) => "ZL",
        (PadFamily.Nintendo, SeekForward) => "ZR",
        (_, SeekBack) => "LT",
        (_, SeekForward) => "RT",
        (PadFamily.PlayStation, Switch) => "Share",
        (PadFamily.Nintendo, Switch) => "−",
        (_, Switch) => "View",
        (PadFamily.PlayStation, Accept) => "✕",
        (PadFamily.PlayStation, Back) => "○",
        (PadFamily.PlayStation, Secondary) => "△",
        (PadFamily.PlayStation, Tertiary) => "□",
        (PadFamily.PlayStation, TabPrev) => "L1",
        (PadFamily.PlayStation, TabNext) => "R1",
        (PadFamily.Nintendo, TabPrev) => "L",
        (PadFamily.Nintendo, TabNext) => "R",
        (_, Accept) => "A",
        (_, Back) => "B",
        (_, Secondary) => "Y",
        (_, Tertiary) => "X",
        (_, TabPrev) => "LB",
        (_, TabNext) => "RB",
        _ => "?",
    };
}
