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
            var row = new ListRow(s.Name, $"{s.Type}  ·  {movies} movies, {shows} shows, {albums} albums", "Connected", UiTheme.Good,
                onPress: () => App.Toast($"{s.Name}: nothing to change yet"), help: $"{s.Name} ({s.Type})");
            col.AddChild(row);
            rows.Add(row);
        }
        col.AddChild(Section("Add a source"));
        void Add(string title, string detail, string when)
        {
            var row = new ListRow(title, detail, when, UiTheme.Dim, onPress: () => App.Toast($"{title}: {when.ToLowerInvariant()}."), help: $"{title}. {detail}");
            col.AddChild(row);
            rows.Add(row);
        }
        Add("Folder", "Movies, shows or music on this device, an SD card or a network share. Artwork and details from TMDB and MusicBrainz.", "Next update");
        Add("Jellyfin", "Sign in to a Jellyfin server: libraries, continue watching, progress sync, intro and credit skipping.", "Coming soon");
        Add("Plex", "Sign in with your Plex account: libraries, continue watching, progress sync, intro and credit markers.", "Coming soon");
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
}
