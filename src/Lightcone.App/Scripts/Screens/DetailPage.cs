using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// An item's page: art, facts, overview and actions. Shows list their episodes, albums their
/// tracks. Used as is in both modes (Desktop only shrinks it).
/// </summary>
public partial class DetailPage : Screen
{
    private readonly MediaItem _item;
    private Control? _first;

    public DetailPage(MediaItem item) => _item = item;

    public override IEnumerable<(string Action, string Label)> Hints =>
        _item.Playable ? [(Pad.Secondary, Host.Library.Progress.IsWatched(_item.Id) ? "Mark unwatched" : "Mark watched")] : [];

    protected override void Build()
    {
        _first = null;
        var lib = Host.Library;
        var col = Column(Desktop ? 16 : 22);
        AddChild(Scroller(col));

        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", Desktop ? 26 : 34);
        col.AddChild(top);
        var shape = PosterTile.ShapeFor(_item);
        var art = shape switch
        {
            TileShape.Square => new Vector2(260, 260),
            TileShape.Wide => new Vector2(420, 236),
            _ => new Vector2(240, 360),
        };
        top.AddChild(new PosterArt(_item, Desktop ? art * 0.85f : art, PosterTile.ProgressOf(lib, _item), 14));
        var info = Column(Desktop ? 8 : 12);
        top.AddChild(info);
        info.AddChild(UiTheme.Label(_item.Title, Desktop ? UiTheme.TitleSize - 4 : UiTheme.TitleSize + 2, UiTheme.Text, wrap: true));
        var meta = new List<string>();
        if (_item.Subtitle != null && _item.Kind is MediaKind.Episode or MediaKind.Album or MediaKind.Track)
        {
            meta.Add(_item.Subtitle);
        }
        if (_item.Year is { } y)
        {
            meta.Add(y.ToString());
        }
        if (_item.DurationText is { } d)
        {
            meta.Add(d);
        }
        meta.AddRange(_item.Genres);
        info.AddChild(UiTheme.Label(string.Join("  ·  ", meta), UiTheme.SmallSize + 2, UiTheme.Dim, wrap: true));
        info.AddChild(UiTheme.Label("From " + _item.SourceName, UiTheme.SmallSize, UiTheme.Dim));
        if (_item.Overview is { } o)
        {
            var ov = UiTheme.Label(o, UiTheme.FontSize - 2, UiTheme.Text, wrap: true);
            ov.CustomMinimumSize = new Vector2(360, 0);
            info.AddChild(ov);
        }
        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 14);
        info.AddChild(actions);
        var buttons = new List<Control>();
        void Act(string label, Action a, string help)
        {
            var b = new ActionButton(label, a, help) { CustomMinimumSize = new Vector2(0, Desktop ? 42 : 54) };
            actions.AddChild(b);
            buttons.Add(b);
        }
        var children = lib.Children(_item.Id);
        if (_item.Playable)
        {
            if (lib.PositionOf(_item) is { } pos)
            {
                Act($"▶  Resume from {MediaItem.FormatDuration(pos, clock: true)}", () => MediaActions.Play(_item), $"{_item.Title}: resume");
                Act("Play from the start", () => MediaActions.Play(_item, fromStart: true), $"{_item.Title}: from the start");
            }
            else
            {
                Act("▶  Play", () => MediaActions.Play(_item), $"{_item.Title}: play");
            }
            var watched = lib.Progress.IsWatched(_item.Id);
            Act(watched ? "Mark unwatched" : "Mark watched", ToggleWatched, $"{_item.Title}: mark {(watched ? "unwatched" : "watched")}");
        }
        else if (children.Count > 0)
        {
            var music = _item.Kind == MediaKind.Album;
            Act(music ? "▶  Play album" : "▶  Play next episode", () => MediaActions.PlayContainer(_item), $"{_item.Title}: play");
            if (music)
            {
                Act("Shuffle", () => MediaActions.PlayContainer(_item, shuffle: true), $"{_item.Title}: shuffle");
            }
        }
        Focus.Row(buttons);
        _first = buttons.FirstOrDefault();

        if (children.Count > 0)
        {
            col.AddChild(Section(_item.Kind == MediaKind.Album ? "Tracks" : "Episodes"));
            var rows = new List<Control>();
            foreach (var c in children)
            {
                var progress = PosterTile.ProgressOf(lib, c);
                var status = lib.Progress.IsWatched(c.Id) ? "Watched" : progress is { } p ? $"{p * 100:0}%" : c.DurationText;
                var label = c.Index is { } n ? $"{n}.  {c.Title}" : c.Title;
                var row = new ListRow(label, c.Kind == MediaKind.Episode ? c.DurationText ?? "" : c.Subtitle ?? "", status,
                    lib.Progress.IsWatched(c.Id) ? UiTheme.Good : UiTheme.Dim, onPress: () => MediaActions.Play(c), help: $"{c.Title}: play")
                {
                    CustomMinimumSize = new Vector2(0, Desktop ? 48 : 60),
                };
                col.AddChild(row);
                rows.Add(row);
            }
            Focus.Column(rows, above: buttons.FirstOrDefault());
            foreach (var b in buttons)
            {
                b.FocusNeighborBottom = rows[0].GetPath();
            }
        }
    }

    private void ToggleWatched()
    {
        var lib = Host.Library;
        lib.Progress.SetWatched(_item.Id, !lib.Progress.IsWatched(_item.Id));
        Rebuild();
        App.UpdateChrome();
    }

    public override void OnAction(string action)
    {
        if (action == Pad.Secondary && _item.Playable)
        {
            ToggleWatched();
        }
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
