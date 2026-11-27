using Godot;
using Lightcone.App.Playback;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// "Play on…": the players the item's server can reach (Plex apps through Plex Companion,
/// Jellyfin apps by session). Picking one starts the item there (from the spot reached here) and
/// opens <see cref="CastRemotePage"/> to control it.
/// </summary>
public partial class CastPage : Screen
{
    private readonly MediaItem _item;
    private readonly double _position;
    private IReadOnlyList<CastTarget>? _targets;
    private bool _sending;
    private Control? _first;

    public CastPage(MediaItem item, double position)
    {
        _item = item;
        _position = position;
    }

    public override bool IsOverlay => true;

    public override void AnimateOpen() => Anim.FadeIn(this, Anim.Normal);

    public override IEnumerable<(string Action, string Label)> Hints => [(Pad.Accept, "Play there"), (Pad.Secondary, "Look again"), (Pad.Back, "Back")];

    protected override void Build()
    {
        var page = Column(12);
        AddChild(CardFrame(Scroller(page)));
        page.AddChild(Section("Play on another screen"));
        var at = _position > 30 ? $" from {Clock(_position)}" : "";
        page.AddChild(UiTheme.Label($"{_item.Title}{at}. Players signed in to the same server show here (on Plex, turn on \"Advertise as player\" in the app).",
            UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        var rows = new List<Control>();
        if (_targets == null)
        {
            page.AddChild(UiTheme.Label("Looking for players…", UiTheme.SmallSize + 1, UiTheme.Dim));
            Callable.From(Load).CallDeferred();
        }
        else if (_targets.Count == 0)
        {
            page.AddChild(UiTheme.Label("No players found. Open Plex or Jellyfin on the TV or phone and look again.", UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        }
        foreach (var t in _targets ?? [])
        {
            var target = t;
            var row = new ListRow(t.Name, t.Product, null, UiTheme.Accent, onPress: () => Send(target), help: $"Play on {t.Name}")
            {
                CustomMinimumSize = new Vector2(0, 66),
            };
            page.AddChild(row);
            rows.Add(row);
        }
        var again = new ActionButton("Look again", Reload, "Search for players again.");
        page.AddChild(again);
        rows.Add(again);
        Focus.Column(rows);
        _first = rows[0];
    }

    internal static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void Reload()
    {
        _targets = null;
        Rebuild();
    }

    public override void OnAction(string action)
    {
        if (action == Pad.Secondary)
        {
            Reload();
            return;
        }
        base.OnAction(action);
    }

    private async void Load()
    {
        var found = await Host.Library.CastTargetsAsync(_item);
        if (!IsInstanceValid(this))
        {
            return;
        }
        _targets = found;
        Rebuild();
        FocusDefault();
    }

    private async void Send(CastTarget target)
    {
        if (_sending)
        {
            return;
        }
        _sending = true;
        App.Toast($"Starting on {target.Name}…");
        var error = await Host.Library.CastAsync(target, _item, _position);
        if (!IsInstanceValid(this))
        {
            return;
        }
        _sending = false;
        if (error != null)
        {
            App.Toast($"{target.Name} didn't start it: {error}");
            return;
        }
        // Playing here too? Hand over: stop (the spot is saved) and leave the player.
        var svc = PlayerService.Instance;
        var handOver = svc.Current?.Id == _item.Id;
        App.Pop();
        if (handOver && App.Top is PlayerScreen)
        {
            svc.Stop();
            App.Pop();
        }
        App.Push(new CastRemotePage(target, _item));
    }

    public override void FocusDefault() => _first?.GrabFocus();
}

/// <summary>
/// A remote for what's playing on another screen: play / pause, skip back and forward, previous /
/// next and stop. Works with the pad (A on a button, Y play / pause, X stop) or a mouse.
/// </summary>
public partial class CastRemotePage : Screen
{
    private readonly CastTarget _target;
    private readonly MediaItem _item;
    private Control? _first;

    public CastRemotePage(CastTarget target, MediaItem item)
    {
        _target = target;
        _item = item;
    }

    public override bool IsOverlay => true;

    public override void AnimateOpen() => Anim.FadeIn(this, Anim.Normal);

    public override IEnumerable<(string Action, string Label)> Hints =>
        [(Pad.Accept, "Press"), (Pad.Secondary, "Play / pause"), (Pad.Tertiary, "Stop"), (Pad.Back, "Close")];

    protected override void Build()
    {
        var page = Column(14);
        AddChild(CardFrame(page));
        page.AddChild(Section("Playing on " + _target.Name));
        page.AddChild(UiTheme.Label(_item.Kind == MediaKind.Episode && _item.ParentTitle != null ? $"{_item.ParentTitle}: {_item.Title}" : _item.Title,
            UiTheme.FontSize + 2, UiTheme.Text, wrap: true));
        page.AddChild(UiTheme.Label(_target.Product, UiTheme.SmallSize + 1, UiTheme.Dim));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        page.AddChild(row);
        var buttons = new List<Control>();
        void Button(string label, CastCommand cmd, string help)
        {
            var b = new ActionButton(label, () => Send(cmd), help) { CustomMinimumSize = new Vector2(0, Desktop ? 48 : 60) };
            row.AddChild(b);
            buttons.Add(b);
        }
        Button("|◀  Previous", CastCommand.Previous, "Previous");
        Button("◀◀  Back", CastCommand.SkipBack, "Skip back");
        Button("▶ ❚❚  Play / pause", CastCommand.PlayPause, "Play / pause");
        Button("Ahead  ▶▶", CastCommand.SkipForward, "Skip ahead");
        Button("Next  ▶|", CastCommand.Next, "Next");
        Button("■  Stop", CastCommand.Stop, "Stop");
        var done = new ActionButton("Done", () => App.Pop(), "Close the remote (it keeps playing there).");
        page.AddChild(done);
        Focus.Row(buttons);
        foreach (var b in buttons)
        {
            b.FocusNeighborBottom = done.GetPath();
        }
        done.FocusNeighborTop = buttons[2].GetPath();
        _first = buttons[2];
    }

    private async void Send(CastCommand cmd)
    {
        var ok = await Host.Library.CastControlAsync(_target, cmd);
        if (!IsInstanceValid(this))
        {
            return;
        }
        if (!ok)
        {
            App.Toast($"{_target.Name} didn't answer.");
        }
        else if (cmd == CastCommand.Stop)
        {
            App.Toast($"Stopped on {_target.Name}.");
            App.Pop();
        }
    }

    public override void OnAction(string action)
    {
        Send(action == Pad.Secondary ? CastCommand.PlayPause : CastCommand.Stop);
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
