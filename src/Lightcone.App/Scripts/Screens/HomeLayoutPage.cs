using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// Settings → Home → Arrange Home rows: each kind of Home row with Show / Hide (A on its name)
/// and ↑ / ↓ to move it. Same page in both modes; the arrows take clicks too.
/// </summary>
public partial class HomeLayoutPage : Screen
{
    private Control? _first;
    private (string Id, int Column)? _refocus;
    private readonly Dictionary<(string, int), Control> _cells = new();

    protected override void Build()
    {
        _first = null;
        _cells.Clear();
        var col = Column(Desktop ? 10 : 12);
        AddChild(Scroller(col));
        var heading = "Arrange Home";
        col.AddChild(UiTheme.Label(UiTheme.Mono ? Loc.Upper(heading) : heading, Desktop ? UiTheme.TitleSize - 6 : UiTheme.TitleSize - 2, UiTheme.Text));
        col.AddChild(UiTheme.Label("Pick a row to show or hide it; ↑ and ↓ move it. Home's top card always shows what you're watching.",
            UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        var s = Host.Settings;
        var rank = MediaLibrary.SectionOrder(s.HomeOrder);
        var order = MediaLibrary.HomeSections.OrderBy(x => rank[x.Id]).ToList();
        var cells = new List<Control>();
        for (var i = 0; i < order.Count; i++)
        {
            var (id, name) = order[i];
            var shown = !s.HomeHidden.Contains(id);
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 10);
            col.AddChild(line);
            var row = new ListRow($"{i + 1}.  {name}", "", shown ? "Shown" : "Hidden", shown ? UiTheme.Good : UiTheme.Dim,
                onPress: () => Toggle(id), help: $"Home row: {name}")
            {
                CustomMinimumSize = new Vector2(0, Desktop ? 48 : 58),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            row.SetMeta(PosterTile.NoHelp, true);
            line.AddChild(row);
            var up = new ActionButton("↑", () => Move(id, -1, 1), $"Move {name} up") { CustomMinimumSize = new Vector2(Desktop ? 48 : 60, 0) };
            var down = new ActionButton("↓", () => Move(id, 1, 2), $"Move {name} down") { CustomMinimumSize = new Vector2(Desktop ? 48 : 60, 0) };
            up.SetMeta(PosterTile.NoHelp, true);
            down.SetMeta(PosterTile.NoHelp, true);
            line.AddChild(up);
            line.AddChild(down);
            cells.Add(row);
            cells.Add(up);
            cells.Add(down);
            _cells[(id, 0)] = row;
            _cells[(id, 1)] = up;
            _cells[(id, 2)] = down;
        }
        var reset = new ActionButton("Reset to the usual order", () =>
        {
            Host.SaveSettings(Host.Settings with { HomeOrder = [], HomeHidden = [] });
            Rebuild();
        }, "Every row shown, in the usual order.");
        col.AddChild(reset);
        Focus.Grid(cells, 3);
        Focus.Column([reset], above: cells[^3]);
        cells[^3].FocusNeighborBottom = reset.GetPath();
        cells[^2].FocusNeighborBottom = reset.GetPath();
        cells[^1].FocusNeighborBottom = reset.GetPath();
        _first = cells[0];
        if (_refocus is { } r && _cells.TryGetValue(r, out var target))
        {
            _first = target;
            _refocus = null;
            Callable.From(() =>
            {
                if (IsInstanceValid(target))
                {
                    target.GrabFocus();
                }
            }).CallDeferred();
        }
    }

    private void Toggle(string id)
    {
        var hidden = Host.Settings.HomeHidden.ToList();
        if (!hidden.Remove(id))
        {
            hidden.Add(id);
        }
        Host.SaveSettings(Host.Settings with { HomeHidden = hidden });
        _refocus = (id, 0);
        Rebuild();
    }

    private void Move(string id, int dir, int column)
    {
        var rank = MediaLibrary.SectionOrder(Host.Settings.HomeOrder);
        var order = MediaLibrary.HomeSections.Select(x => x.Id).OrderBy(x => rank[x]).ToList();
        var i = order.IndexOf(id);
        var j = Math.Clamp(i + dir, 0, order.Count - 1);
        if (i == j)
        {
            return;
        }
        (order[i], order[j]) = (order[j], order[i]);
        Host.SaveSettings(Host.Settings with { HomeOrder = order });
        _refocus = (id, column);
        Rebuild();
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
