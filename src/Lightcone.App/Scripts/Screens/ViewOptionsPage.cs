using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// Sort and filter for a library page, as a card over it (Y in Controller mode): a sort column
/// and, for Movies and Shows, which titles to list by watched state. Picks apply at once.
/// </summary>
public partial class ViewOptionsPage : Screen
{
    public static readonly (WatchFilter Key, string Label, string Detail)[] Filters =
    [
        (WatchFilter.All, "Everything", ""),
        (WatchFilter.Unwatched, "Unwatched", "Movies not seen, shows with episodes left"),
        (WatchFilter.InProgress, "In progress", "Started but not finished"),
        (WatchFilter.Watched, "Watched", "Seen it all"),
    ];

    private readonly IReadOnlyList<(SortKey Key, string Label)> _sorts;
    private readonly Func<(SortKey Sort, bool Descending, WatchFilter Filter)> _state;
    private readonly Action<SortKey> _sort;
    private readonly Action<WatchFilter>? _filter;
    private Control? _first;

    public ViewOptionsPage(IReadOnlyList<(SortKey, string)> sorts, Func<(SortKey, bool, WatchFilter)> state, Action<SortKey> sort, Action<WatchFilter>? filter)
    {
        _sorts = sorts;
        _state = state;
        _sort = sort;
        _filter = filter;
    }

    public override bool IsOverlay => true;

    public override void AnimateOpen() => Anim.FadeIn(this, Anim.Normal);

    protected override void Build()
    {
        var (sortKey, desc, filterKey) = _state();
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 30);
        AddChild(CardFrame(row));
        var sortCol = Column(8);
        row.AddChild(sortCol);
        sortCol.AddChild(Section("Sort by"));
        var sortRows = new List<Control>();
        foreach (var (key, label) in _sorts)
        {
            var k = key;
            var on = key == sortKey;
            var status = on ? (desc ? "✓  ↓" : "✓  ↑") : null;
            var r = new ListRow(label, on ? "Pick again to flip the order" : "", status, UiTheme.Accent, onPress: () =>
            {
                _sort(k);
                Callable.From(Rebuild).CallDeferred();
            }, help: "Sort: " + label) { CustomMinimumSize = new Vector2(0, 66) };
            sortCol.AddChild(r);
            sortRows.Add(r);
        }
        var filterRows = new List<Control>();
        if (_filter != null)
        {
            var filterCol = Column(8);
            row.AddChild(filterCol);
            filterCol.AddChild(Section("Show"));
            foreach (var (key, label, detail) in Filters)
            {
                var k = key;
                var r = new ListRow(label, detail, key == filterKey ? "✓" : null, UiTheme.Accent, onPress: () =>
                {
                    _filter(k);
                    Callable.From(Rebuild).CallDeferred();
                }, help: "Show: " + label) { CustomMinimumSize = new Vector2(0, 66) };
                filterCol.AddChild(r);
                filterRows.Add(r);
            }
        }
        Focus.Column(sortRows, right: filterRows.FirstOrDefault());
        if (filterRows.Count > 0)
        {
            Focus.Column(filterRows, left: sortRows.FirstOrDefault());
        }
        _first = sortRows.FirstOrDefault();
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
