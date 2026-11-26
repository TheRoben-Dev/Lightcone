using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Jellyfin;

namespace Lightcone.App.Screens;

/// <summary>
/// Adding a Jellyfin server: servers found on this network, or an address typed with the
/// built-in keyboard. Picking one goes on to sign-in.
/// </summary>
public partial class JellyfinAddPage : Screen
{
    private CancellationTokenSource? _cts;
    private IReadOnlyList<(string Url, string Id, string Name)>? _found;
    private Control? _first;

    /// <summary>Smoke test: skip the network search (nothing answers there).</summary>
    public static bool Offline { get; set; }

    public override IEnumerable<(string Action, string Label)> Hints => [(Pad.Secondary, "Search again")];

    protected override void Build()
    {
        var col = Column(Desktop ? 10 : 14);
        AddChild(Scroller(col));
        col.AddChild(UiTheme.Label("Add a Jellyfin server", Desktop ? UiTheme.TitleSize - 8 : UiTheme.TitleSize - 4, UiTheme.Text));
        var rows = new List<Control>();
        col.AddChild(Section("On this network"));
        if (_found == null)
        {
            col.AddChild(UiTheme.Label("Looking for Jellyfin servers…", UiTheme.FontSize - 1, UiTheme.Dim));
        }
        else if (_found.Count == 0)
        {
            col.AddChild(UiTheme.Label("None found. A server on another network (or with discovery off) can be added by its address.", UiTheme.FontSize - 1, UiTheme.Dim, wrap: true));
        }
        else
        {
            foreach (var s in _found)
            {
                var server = s;
                var known = Host.Jellyfin.Account.Servers.Any(e => e.ServerId == s.Id);
                var row = new ListRow(s.Name, s.Url, known ? "Added" : "Sign in", known ? UiTheme.Good : UiTheme.AccentText,
                    onPress: () => Open(server.Url), help: $"{s.Name} at {s.Url}");
                row.SetMeta(PosterTile.NoHelp, true);
                col.AddChild(row);
                rows.Add(row);
            }
        }
        col.AddChild(Section("Or"));
        var type = new ActionButton("Enter the server's address…", () => App.Push(new TextEntryPage("Jellyfin server address", "", address =>
        {
            if (!string.IsNullOrWhiteSpace(address))
            {
                Open(address);
            }
        }, "Like 192.168.1.20, 192.168.1.20:8096 or https://jellyfin.example.com. Port 8096 is tried when none is given.")),
            "Type the address with the built-in keyboard.");
        col.AddChild(type);
        rows.Add(type);
        var cancel = new ActionButton("Cancel", () => App.Pop(), "Back to Sources.");
        col.AddChild(cancel);
        rows.Add(cancel);
        Focus.Column(rows);
        _first = rows[0];
        if (_found == null && _cts == null)
        {
            Search();
        }
    }

    public override void FocusDefault() => _first?.GrabFocus();

    public override void OnAction(string action)
    {
        if (action == Pad.Secondary)
        {
            _found = null;
            _cts = null;
            Rebuild();
        }
    }

    public override void _ExitTree() => _cts?.Cancel();

    private async void Search()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        await Task.Yield();  // not inside Build
        var found = Offline ? [] : await Task.Run(() => JellyfinClient.DiscoverAsync(TimeSpan.FromSeconds(2.5), ct), ct);
        if (!IsInstanceValid(this) || ct.IsCancellationRequested)
        {
            return;
        }
        _found = found;
        Rebuild();
        App.UpdateChrome();
    }

    private void Open(string address) =>
        App.Busy("Connecting to Jellyfin…", (_, ct) => Host.Jellyfin.Client.FindServerAsync(address, ct), info => App.Push(new JellyfinSignInPage(info)));
}

/// <summary>
/// Signing in to one Jellyfin server with a controller: Quick Connect shows a 6-digit code to
/// approve from any device already signed in (no typing), or user name and password with the
/// built-in keyboard.
/// </summary>
public partial class JellyfinSignInPage : Screen
{
    private readonly JellyfinServerInfo _server;
    private CancellationTokenSource? _cts;
    private Label? _code;
    private Label? _status;
    private Control? _first;
    private bool? _quick;

    /// <summary>Smoke test: show a stand-in code instead of asking the server.</summary>
    public static bool Offline { get; set; }

    public JellyfinSignInPage(JellyfinServerInfo server) => _server = server;

    public override IEnumerable<(string Action, string Label)> Hints => _quick == true ? [(Pad.Secondary, "New code")] : [];

    protected override void Build()
    {
        var center = new CenterContainer();
        AddChild(center);
        var col = Column(Desktop ? 12 : 16);
        col.CustomMinimumSize = new Vector2(Desktop ? 600 : 720, 0);
        center.AddChild(col);
        col.AddChild(UiTheme.Label($"Sign in to {_server.Name}", Desktop ? UiTheme.TitleSize - 6 : UiTheme.TitleSize, UiTheme.Text, wrap: true));
        col.AddChild(UiTheme.Label($"Jellyfin {_server.Version} at {_server.Url}", UiTheme.SmallSize + 1, UiTheme.Dim));
        var buttons = new List<Control>();
        if (_quick != false)
        {
            col.AddChild(UiTheme.Label("On a phone or computer signed in to this server, open your user menu → Quick Connect and enter:",
                UiTheme.FontSize - 1, UiTheme.Dim, wrap: true));
            var box = new PanelContainer();
            var sb = UiTheme.Box(UiTheme.PanelHi, 2, 14, UiTheme.Accent);
            sb.ContentMarginTop = sb.ContentMarginBottom = 14;
            box.AddThemeStyleboxOverride("panel", sb);
            col.AddChild(box);
            _code = UiTheme.Label("······", Desktop ? 52 : 64, UiTheme.Text);
            _code.HorizontalAlignment = HorizontalAlignment.Center;
            box.AddChild(_code);
        }
        else
        {
            col.AddChild(UiTheme.Label("Quick Connect is off on this server (an admin can turn it on in the dashboard), so sign in with your user name and password.",
                UiTheme.FontSize - 1, UiTheme.Dim, wrap: true));
        }
        _status = UiTheme.Label(_quick == null ? "Checking the server…" : "", UiTheme.SmallSize + 2, UiTheme.Dim, wrap: true);
        col.AddChild(_status);
        var password = new ActionButton("Sign in with user name and password", AskUser, "Type them with the built-in keyboard.");
        buttons.Add(password);
        if (_quick != false)
        {
            buttons.Add(new ActionButton("New code", NewCode, "Get another Quick Connect code."));
        }
        buttons.Add(new ActionButton("Cancel", () => App.Pop(), "Back."));
        foreach (var b in buttons)
        {
            col.AddChild(b);
        }
        Focus.Column(buttons);
        _first = buttons[0];
        if (_cts == null)
        {
            NewCode();
        }
    }

    public override void FocusDefault() => _first?.GrabFocus();

    public override void OnAction(string action)
    {
        if (action == Pad.Secondary && _quick == true)
        {
            NewCode();
        }
    }

    public override void _ExitTree() => _cts?.Cancel();

    private void SetStatus(string text)
    {
        if (_status != null && IsInstanceValid(_status))
        {
            _status.Text = text;
        }
    }

    private async void NewCode()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var jf = Host.Jellyfin;
        if (Offline)
        {
            _quick = true;
            if (_code != null)
            {
                _code.Text = "123456";
            }
            SetStatus("Waiting for you to approve the code…");
            App.UpdateChrome();
            return;
        }
        try
        {
            if (_quick == null)
            {
                _quick = await Task.Run(() => jf.Client.QuickConnectEnabledAsync(_server.Url, ct), ct);
                if (!IsInstanceValid(this) || ct.IsCancellationRequested)
                {
                    return;
                }
                if (_quick == false)
                {
                    Rebuild();
                    App.UpdateChrome();
                    return;
                }
                App.UpdateChrome();
            }
            SetStatus("Getting a code…");
            var code = await Task.Run(() => jf.Client.InitiateQuickConnectAsync(_server.Url, ct), ct);
            if (!IsInstanceValid(this) || ct.IsCancellationRequested)
            {
                return;
            }
            if (_code != null)
            {
                _code.Text = code.Code;
            }
            SetStatus("Waiting for you to approve the code…");
            var login = await Task.Run(() => jf.WaitForQuickConnectAsync(_server, code, ct), ct);
            if (!IsInstanceValid(this) || ct.IsCancellationRequested)
            {
                return;
            }
            if (login == null)
            {
                SetStatus("That code expired. Press New code for another one.");
                return;
            }
            Finish(login);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (Main.IsUserFacing(e))
        {
            Lightcone.Host.AppLog.Warn($"jellyfin quick connect: {e.Message}");
            if (IsInstanceValid(this))
            {
                SetStatus("Quick Connect didn't work: " + e.Message + " You can sign in with your password instead.");
            }
        }
    }

    private void AskUser()
    {
        App.Push(new TextEntryPage("Jellyfin user name", "", user =>
        {
            if (string.IsNullOrWhiteSpace(user))
            {
                return;
            }
            App.Push(new TextEntryPage($"Password for {user}", "", pw =>
            {
                App.Busy("Signing in…", (_, ct) => Host.Jellyfin.Client.AuthenticateAsync(_server.Url, user, pw, ct), Finish);
            }, "Leave it empty if the user has no password.", secret: true));
        }));
    }

    private void Finish(JellyfinLogin login)
    {
        _cts?.Cancel();
        App.Busy($"Loading {_server.Name}…", (_, ct) => Host.Jellyfin.AddServerAsync(_server, login, ct), source =>
        {
            // Back to Sources (this page and the add page).
            while (App.Top is JellyfinSignInPage or JellyfinAddPage)
            {
                App.Pop();
            }
            if (source.Online)
            {
                var n = source.Items.Count(i => i.Kind is Lightcone.Host.Library.MediaKind.Movie or Lightcone.Host.Library.MediaKind.Show);
                App.Success($"{source.Name}: {n} movies and shows added");
            }
            else
            {
                App.Error($"{source.Name}: {source.Status}");
            }
        });
    }
}
