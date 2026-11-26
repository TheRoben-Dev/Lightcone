using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Plex;

namespace Lightcone.App.Screens;

/// <summary>
/// Plex sign-in that works with a controller: Lightcone shows a 4-character code, you enter it
/// at plex.tv/link on a phone or computer, and the page moves on by itself. No password is
/// typed into Lightcone. The same page serves Desktop mode (it can open the link in a browser).
/// </summary>
public partial class PlexSignInPage : Screen
{
    private CancellationTokenSource? _cts;
    private Label? _code;
    private Label? _status;
    private Control? _first;
    private PlexPin? _pin;

    /// <summary>Smoke test: show a stand-in code instead of asking plex.tv.</summary>
    public static bool Offline { get; set; }

    public override IEnumerable<(string Action, string Label)> Hints => [(Pad.Secondary, "New code")];

    protected override void Build()
    {
        var center = new CenterContainer();
        AddChild(center);
        var col = Column(Desktop ? 12 : 16);
        col.CustomMinimumSize = new Vector2(Desktop ? 560 : 680, 0);
        center.AddChild(col);
        col.AddChild(UiTheme.Label("Sign in to Plex", Desktop ? UiTheme.TitleSize - 6 : UiTheme.TitleSize, UiTheme.Text));
        col.AddChild(UiTheme.Label("On your phone or computer, go to", UiTheme.FontSize - 1, UiTheme.Dim, wrap: true));
        col.AddChild(UiTheme.Label("plex.tv/link", UiTheme.TitleSize - 4, UiTheme.AccentText));
        col.AddChild(UiTheme.Label("sign in, and enter this code:", UiTheme.FontSize - 1, UiTheme.Dim, wrap: true));
        var box = new PanelContainer();
        var sb = UiTheme.Box(UiTheme.PanelHi, 2, 14, UiTheme.Accent);
        sb.ContentMarginTop = sb.ContentMarginBottom = 14;
        box.AddThemeStyleboxOverride("panel", sb);
        col.AddChild(box);
        _code = UiTheme.Label(_pin?.Code ?? "····", Desktop ? 52 : 64, UiTheme.Text);
        _code.HorizontalAlignment = HorizontalAlignment.Center;
        _code.AddThemeConstantOverride("letter_spacing", 18);  // (ignored by fonts without spacing support)
        box.AddChild(_code);
        _status = UiTheme.Label("Getting a code…", UiTheme.SmallSize + 2, UiTheme.Dim, wrap: true);
        col.AddChild(_status);
        var buttons = new List<Control>();
        var open = new ActionButton("Open plex.tv/link on this device", () => OS.ShellOpen(PlexClient.LinkUrl),
            "Opens the page in your browser, if you'd rather sign in here.");
        var fresh = new ActionButton("New code", NewCode, "Codes last 15 minutes.");
        var cancel = new ActionButton("Cancel", () => App.Pop(), "Back to Sources.");
        foreach (var b in new[] { open, fresh, cancel })
        {
            col.AddChild(b);
            buttons.Add(b);
        }
        Focus.Column(buttons);
        _first = open;
        if (_cts == null)
        {
            NewCode();
        }
    }

    public override void FocusDefault() => _first?.GrabFocus();

    public override void OnAction(string action)
    {
        if (action == Pad.Secondary)
        {
            NewCode();
        }
    }

    public override void _ExitTree() => _cts?.Cancel();

    private async void NewCode()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var plex = Host.Plex;
        if (Offline)
        {
            _pin = new PlexPin(0, "AB12", DateTime.UtcNow.AddMinutes(15));
            if (_code != null)
            {
                _code.Text = _pin.Code;
            }
            SetStatus("Waiting for you to enter the code…");
            return;
        }
        SetStatus("Getting a code…");
        try
        {
            _pin = await Task.Run(() => plex.Client.CreatePinAsync(ct), ct);
            if (!IsInstanceValid(this) || ct.IsCancellationRequested)
            {
                return;
            }
            if (_code != null)
            {
                _code.Text = _pin.Code;
            }
            SetStatus("Waiting for you to enter the code…");
            var token = await Task.Run(() => plex.WaitForLinkAsync(_pin, ct), ct);
            if (!IsInstanceValid(this) || ct.IsCancellationRequested)
            {
                return;
            }
            if (token == null)
            {
                SetStatus("That code expired. Press New code for another one.");
                return;
            }
            SetStatus("Signed in. Finding your servers…");
            var servers = await Task.Run(() => plex.SignInAsync(token, ct), ct);
            if (!IsInstanceValid(this) || ct.IsCancellationRequested)
            {
                return;
            }
            App.Pop();
            App.Push(new PlexServersPage(servers, firstTime: true));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (Main.IsUserFacing(e))
        {
            Lightcone.Host.AppLog.Warn($"plex sign-in: {e.Message}");
            if (IsInstanceValid(this))
            {
                SetStatus("Couldn't reach plex.tv. Check the network, then press New code.");
            }
        }
    }

    private void SetStatus(string text)
    {
        if (_status != null && IsInstanceValid(_status))
        {
            _status.Text = text;
        }
    }
}

/// <summary>Which of the account's Plex servers show up in Lightcone (owned and shared ones).</summary>
public partial class PlexServersPage : Screen
{
    private readonly IReadOnlyList<PlexServer> _servers;
    private readonly HashSet<string> _on;
    private Control? _first;

    public PlexServersPage(IReadOnlyList<PlexServer> servers, bool firstTime = false)
    {
        _servers = servers;
        var current = Main.Instance.Host!.Plex.Account.Servers.Select(s => s.MachineId).ToHashSet();
        // First sign-in: every server on. Later: what's shown now.
        _on = firstTime || current.Count == 0 ? servers.Select(s => s.MachineId).ToHashSet() : current;
    }

    protected override void Build()
    {
        var col = Column(Desktop ? 10 : 14);
        AddChild(Scroller(col));
        col.AddChild(UiTheme.Label("Plex servers", Desktop ? UiTheme.TitleSize - 8 : UiTheme.TitleSize - 4, UiTheme.Text));
        var who = Host.Plex.Account.Name is { } n ? $"Signed in as {n}. " : "";
        col.AddChild(UiTheme.Label(_servers.Count == 0
            ? who + "This account has no servers yet. Set up Plex Media Server, or ask a friend to share theirs, then come back."
            : who + "Choose which servers show up in Lightcone.", UiTheme.FontSize - 2, UiTheme.Dim, wrap: true));
        var rows = new List<Control>();
        foreach (var s in _servers)
        {
            var id = s.MachineId;
            var where = s.Connections.Any(c => c.Local) ? "on this network" : s.Connections.Any(c => !c.Relay) ? "over the internet" : "through Plex's relay";
            var t = Toggle.Create(s.Name, _on.Contains(id), on =>
            {
                if (on)
                {
                    _on.Add(id);
                }
                else
                {
                    _on.Remove(id);
                }
            }, $"{(s.Owned ? "Your server" : "Shared with you")}, reachable {where}.");
            col.AddChild(t);
            rows.Add(t);
        }
        var done = new ActionButton("Done", Apply, "Loads the chosen servers' libraries.");
        col.AddChild(done);
        rows.Add(done);
        Focus.Column(rows);
        _first = rows.FirstOrDefault();
    }

    public override void FocusDefault() => _first?.GrabFocus();

    private void Apply()
    {
        var chosen = _servers.Where(s => _on.Contains(s.MachineId)).ToList();
        App.Busy("Loading your Plex libraries…", async (_, ct) =>
        {
            await Host.Plex.UseServersAsync(chosen, ct);
            return chosen.Count;
        }, n =>
        {
            App.Pop();
            var offline = Host.Plex.Sources.Where(s => !s.Online).Select(s => s.Name).ToList();
            if (offline.Count > 0)
            {
                App.Error($"Couldn't reach {string.Join(", ", offline)}. Lightcone keeps trying when it starts.");
            }
            else if (n > 0)
            {
                App.Success($"Plex: {Host.Plex.Sources.Sum(s => s.Items.Count(i => i.Kind is Lightcone.Host.Library.MediaKind.Movie or Lightcone.Host.Library.MediaKind.Show or Lightcone.Host.Library.MediaKind.Album))} titles added");
            }
        });
    }
}
