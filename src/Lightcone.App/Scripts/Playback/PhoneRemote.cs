using Godot;
using Lightcone.App.Screens;
using Lightcone.App.Ui;
using Lightcone.Host;
using Lightcone.Host.Remote;

namespace Lightcone.App.Playback;

/// <summary>
/// Runs the phone remote (<see cref="RemoteServer"/>) while it's turned on in Settings and turns
/// its commands into the same input a controller makes (so every screen works with it), plus
/// play / pause, volume and next for the player.
/// </summary>
public partial class PhoneRemote : Node
{
    public static PhoneRemote? Instance { get; private set; }

    private RemoteServer? _server;
    private volatile RemoteState _state = new("Home", null, null, 0, 0, false, false);
    private double _stateAge;

    private static AppHost Host => Main.Instance.Host!;

    public bool Running => _server != null;

    public int Port => _server?.Port ?? 0;

    public IReadOnlyList<string> Urls => _server?.Urls() ?? [];

    public override void _Ready()
    {
        Instance = this;
        Apply();
    }

    public override void _ExitTree()
    {
        _server?.Dispose();
        _server = null;
    }

    /// <summary>Starts or stops the server to match Settings (makes a PIN the first time).</summary>
    public void Apply()
    {
        var s = Host.Settings;
        if (!s.RemoteEnabled)
        {
            _server?.Dispose();
            _server = null;
            return;
        }
        if (s.RemotePin is not { Length: 4 } pin)
        {
            pin = RemoteServer.NewPin();
            Host.SaveSettings(Host.Settings with { RemotePin = pin });
        }
        if (_server != null)
        {
            return;
        }
        var server = new RemoteServer(pin, () => _state);
        if (server.Start())
        {
            _server = server;
        }
        else
        {
            server.Dispose();
            Main.Instance.Error("The phone remote couldn't open a network port.");
        }
    }

    /// <summary>A new PIN (phones that used the old one ask again).</summary>
    public void NewPin()
    {
        Host.SaveSettings(Host.Settings with { RemotePin = RemoteServer.NewPin() });
        _server?.Dispose();
        _server = null;
        Apply();
    }

    public override void _Process(double delta)
    {
        if (_server == null)
        {
            return;
        }
        _stateAge += delta;
        if (_stateAge > 0.5)
        {
            _stateAge = 0;
            var svc = PlayerService.Instance;
            var main = Main.Instance;
            _state = svc.Current is { } cur
                ? new RemoteState("Player", cur.Kind == Lightcone.Host.Library.MediaKind.Episode && cur.ParentTitle != null ? cur.ParentTitle : cur.Title,
                    cur.Kind == Lightcone.Host.Library.MediaKind.Episode ? $"S{cur.Season ?? 0}E{cur.Index ?? 0} · {cur.Title}" : null,
                    svc.Position, svc.Duration ?? 0, true, svc.Paused)
                : new RemoteState(main.CurrentTab, main.CurrentTab, "Use the pad to move around", 0, 0, false, false);
        }
        while (_server.TryTake(out var c))
        {
            Run(c);
        }
    }

    private static void Emit(string action)
    {
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
    }

    private static void Run(string c)
    {
        var svc = PlayerService.Instance;
        var playing = svc.Current != null;
        switch (c)
        {
            case "up": Emit("ui_up"); break;
            case "down": Emit("ui_down"); break;
            case "left": Emit("ui_left"); break;
            case "right": Emit("ui_right"); break;
            case "accept": Emit(Pad.Accept); break;
            case "back": Emit(Pad.Back); break;
            case "options": Emit(Pad.Menu); break;
            case "menu": Emit(Pad.Secondary); break;
            case "tabprev": Emit(Pad.TabPrev); break;
            case "tabnext": Emit(Pad.TabNext); break;
            case "skipback": Emit(Pad.SeekBack); break;
            case "skipforward": Emit(Pad.SeekForward); break;
            case "home": Main.Instance.SwitchTab("Home"); break;
            case "playpause" when playing: svc.TogglePause(); break;
            case "next" when playing: svc.PlayNext(false); break;
            case "volup" when playing:
                svc.NudgeVolume(5);
                Main.Instance.Toast(svc.VolumeText ?? "Volume up");
                break;
            case "voldown" when playing:
                svc.NudgeVolume(-5);
                Main.Instance.Toast(svc.VolumeText ?? "Volume down");
                break;
            case "mute" when playing: svc.ToggleMute(); break;
        }
    }
}
