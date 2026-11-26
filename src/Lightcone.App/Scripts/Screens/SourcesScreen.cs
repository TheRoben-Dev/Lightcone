using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>Where media comes from: the sources added so far, and adding more.</summary>
public partial class SourcesScreen : Screen
{
    private Control? _first;

    protected override void Build()
    {
        var lib = Host.Library;
        var col = Column(Desktop ? 10 : 14);
        AddChild(Scroller(col));
        col.AddChild(UiTheme.Label("Sources", Desktop ? UiTheme.TitleSize - 8 : UiTheme.TitleSize - 4, UiTheme.Text));
        var rows = new List<Control>();
        col.AddChild(Section("Your sources"));
        if (lib.Sources.Count == 0)
        {
            col.AddChild(UiTheme.Label("None yet.", UiTheme.FontSize - 1, UiTheme.Dim));
        }
        foreach (var s in lib.Sources)
        {
            var movies = s.Items.Count(i => i.Kind == MediaKind.Movie);
            var shows = s.Items.Count(i => i.Kind == MediaKind.Show);
            var albums = s.Items.Count(i => i.Kind == MediaKind.Album);
            var source = s;
            var row = new ListRow(s.Name, $"{s.Type}  ·  {movies} movies, {shows} shows, {albums} albums", s.Status, s.Online ? UiTheme.Good : UiTheme.Warn,
                onPress: () => SourceOptions(source), help: $"{s.Name} ({s.Type}): {s.Status}");
            row.SetMeta(PosterTile.NoHelp, true);
            col.AddChild(row);
            rows.Add(row);
        }
        var plex = Host.Plex;
        if (plex.Account.SignedIn)
        {
            col.AddChild(Section("Plex account"));
            var account = new ListRow($"Plex · {plex.Account.Name ?? "signed in"}", "Choose servers, or sign out.", "Signed in", UiTheme.Good,
                onPress: PlexAccountOptions, help: "Plex account");
            account.SetMeta(PosterTile.NoHelp, true);
            col.AddChild(account);
            rows.Add(account);
        }
        col.AddChild(Section("Add a source"));
        void Add(string title, string detail, string when, Action? press = null)
        {
            var row = new ListRow(title, detail, when, press != null ? UiTheme.AccentText : UiTheme.Dim,
                onPress: press ?? (() => App.Toast($"{title}: {when.ToLowerInvariant()}.")), help: $"{title}. {detail}");
            row.SetMeta(PosterTile.NoHelp, true);
            col.AddChild(row);
            rows.Add(row);
        }
        Add("Folder", "Movies, shows or music on this device, an SD card or a network share. Artwork and details from TMDB and MusicBrainz.", "Next update");
        if (!plex.Account.SignedIn)
        {
            Add("Plex", "Sign in with a code at plex.tv/link: libraries, continue watching, progress sync, intro and credit skipping.", "Sign in",
                () => App.Push(new PlexSignInPage()));
        }
        Add("Jellyfin", "Find a server on this network or type its address, then sign in with Quick Connect: libraries, continue watching, progress sync, box sets, intro skipping.",
            Host.Jellyfin.Sources.Count > 0 ? "Add another" : "Add server", () => App.Push(new JellyfinAddPage()));
        Add("Find servers on this network", "DLNA / UPnP media servers.", "Later");
        col.AddChild(Section("Sample library"));
        var demo = Toggle.Create("Sample library", Host.Settings.DemoLibrary, on =>
        {
            Host.SaveSettings(Host.Settings with { DemoLibrary = on });
            Host.ApplyDemo(on);
            Rebuild();
        }, "Sample movies, shows and music that play without any files: handy to try Lightcone out.",
            "Shown on Home and in every section, marked “Sample library”.", "Hidden.");
        col.AddChild(demo);
        rows.Add(demo);
        Focus.Column(rows);
        _first = rows.FirstOrDefault();
    }

    public override void FocusDefault() => _first?.GrabFocus();

    private void SourceOptions(IMediaSource s)
    {
        if (s is Lightcone.Host.Plex.PlexSource p)
        {
            App.Push(new ChoicePage($"{p.Name} (Plex): {p.Status}.",
                ("Refresh now", () => App.Busy($"Refreshing {p.Name}…", async (_, ct) =>
                {
                    await p.RefreshAsync(ct);
                    return p.Online;
                }, ok =>
                {
                    if (ok)
                    {
                        App.Success($"{p.Name} is up to date");
                    }
                    else
                    {
                        App.Error($"{p.Name}: {p.Status}");
                    }
                })),
                ("Remove from Lightcone", () => App.Confirm($"Stop showing {p.Name}? Your Plex account stays signed in.", "Remove", () =>
                {
                    Host.Plex.RemoveServer(p.Entry.MachineId);
                    Rebuild();
                })),
                ("Cancel", () => { })));
        }
        else if (s is Lightcone.Host.Jellyfin.JellyfinSource j)
        {
            App.Push(new ChoicePage($"{j.Name} (Jellyfin, signed in as {j.Entry.UserName}): {j.Status}.",
                ("Refresh now", () => App.Busy($"Refreshing {j.Name}…", async (_, ct) =>
                {
                    await j.RefreshAsync(ct);
                    return j.Online;
                }, ok =>
                {
                    if (ok)
                    {
                        App.Success($"{j.Name} is up to date");
                    }
                    else
                    {
                        App.Error($"{j.Name}: {j.Status}");
                    }
                })),
                ("Sign in again", () => App.Busy("Connecting to Jellyfin…", (_, ct) => Host.Jellyfin.Client.PublicInfoAsync(j.Entry.Url, ct),
                    info => App.Push(new JellyfinSignInPage(info)))),
                ("Remove and sign out", () => App.Confirm($"Remove {j.Name} from Lightcone and sign this device out of it?", "Remove", () =>
                {
                    Host.Jellyfin.RemoveServer(j.Entry.ServerId);
                    Rebuild();
                })),
                ("Cancel", () => { })));
        }
        else
        {
            App.Toast($"{s.Name}: nothing to change yet");
        }
    }

    private void PlexAccountOptions()
    {
        App.Push(new ChoicePage($"Plex account: {Host.Plex.Account.Name}.",
            ("Choose servers", () => App.Busy("Finding your servers…", (_, ct) => Host.Plex.ServersAsync(ct),
                servers => App.Push(new PlexServersPage(servers)))),
            ("Sign out", () => App.Confirm("Sign out of Plex? Its servers disappear from Lightcone until you sign in again.", "Sign out", () =>
            {
                Host.Plex.SignOut();
                Rebuild();
            })),
            ("Cancel", () => { })));
    }
}
