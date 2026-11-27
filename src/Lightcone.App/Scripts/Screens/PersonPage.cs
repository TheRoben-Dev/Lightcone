using Lightcone.Host;
using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// Someone from a title's Cast &amp; crew row: their photo and name, then every movie and show in
/// your library they're in (asked of their server), newest first.
/// </summary>
public partial class PersonPage : Screen
{
    private readonly Person _person;
    private IReadOnlyList<MediaItem>? _titles;
    private bool _asked;
    private Control? _first;

    public PersonPage(Person person) => _person = person;

    protected override void Build()
    {
        _first = null;
        var col = Column(Desktop ? 16 : 22);
        AddChild(Scroller(col));
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", Desktop ? 22 : 30);
        col.AddChild(top);
        var photo = new MediaItem
        {
            Id = "person:" + _person.Key,
            Title = _person.Name,
            Kind = MediaKind.Artist,
            PosterUrl = _person.PhotoUrl,
            SourceId = _person.SourceId,
            SourceName = "",
        };
        top.AddChild(new PosterArt(photo, Desktop ? new Vector2(150, 150) : new Vector2(180, 180), null, 90, showTitle: false));
        var info = Column(Desktop ? 6 : 8);
        info.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        top.AddChild(info);
        info.AddChild(UiTheme.Label(_person.Name, Desktop ? UiTheme.TitleSize - 4 : UiTheme.TitleSize + 2, UiTheme.Text, wrap: true));
        info.AddChild(UiTheme.Label(_person.Job == Person.Actor ? "Actor" : _person.Job, UiTheme.SmallSize + 2, UiTheme.Dim));

        if (_titles == null)
        {
            info.AddChild(UiTheme.Label("Looking through your library…", UiTheme.SmallSize + 1, UiTheme.Dim));
            AddBack(col);
            Load();
            return;
        }
        if (_titles.Count == 0)
        {
            col.AddChild(UiTheme.Label($"Nothing else with {_person.Name} in your library.", UiTheme.FontSize - 1, UiTheme.Dim, wrap: true));
            AddBack(col);
            return;
        }
        info.AddChild(UiTheme.Label($"{_titles.Count} {(_titles.Count == 1 ? "title" : "titles")} in your library", UiTheme.SmallSize + 1, UiTheme.Dim));
        col.AddChild(Section("In your library"));
        var gap = Desktop ? 18 : 24;
        var columns = ColumnsFor(PosterTile.SizeFor(TileShape.Poster, Desktop).X, gap);
        var grid = new GridContainer { Columns = columns };
        grid.AddThemeConstantOverride("h_separation", gap);
        grid.AddThemeConstantOverride("v_separation", gap);
        col.AddChild(grid);
        var lib = Host.Library;
        var tiles = new List<Control>();
        foreach (var t in _titles)
        {
            var tile = new PosterTile(t, TileShape.Poster, PosterTile.ProgressOf(lib, t), Desktop, MediaActions.Open);
            grid.AddChild(tile);
            tiles.Add(tile);
        }
        Focus.Grid(tiles, columns);
        _first = tiles[0];
    }

    private void AddBack(VBoxContainer col)
    {
        var back = new ActionButton("Back", () => App.Pop(), "Back");
        col.AddChild(back);
        _first = back;
    }

    private async void Load()
    {
        if (_asked)
        {
            return;
        }
        _asked = true;
        IReadOnlyList<MediaItem> titles;
        try
        {
            titles = await Host.Library.TitlesWithAsync(_person);
        }
        catch (Exception e)
        {
            AppLog.Info($"titles with {_person.Key}: {e.Message}");
            titles = [];
        }
        if (!IsInstanceValid(this))
        {
            return;
        }
        _titles = titles;
        Rebuild();
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
