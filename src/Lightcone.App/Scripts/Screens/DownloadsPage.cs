using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// Downloads: what's saved on this device (and how much room it takes), what's on its way, and
/// what failed. A plays a finished one (works offline); Y deletes; X retries failed ones.
/// Settings → Library → Downloads, or Home's Downloaded row.
/// </summary>
public partial class DownloadsPage : Screen
{
    private Control? _first;
    private readonly Dictionary<string, ListRow> _rows = new();
    private int _shape;

    public override IEnumerable<(string Action, string Label)> Hints =>
        Host.Downloads.Entries.Count == 0 ? [] : [(Pad.Secondary, "Delete"), (Pad.Tertiary, "Retry failed")];

    private static string Mb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1 << 20):0} MB";

    private static (string Text, Color Color) Status(DownloadEntry e) => e.State switch
    {
        DownloadState.Done => ("Ready  ·  " + Mb(e.Bytes), UiTheme.Good),
        DownloadState.Downloading => (e.Fraction is { } f ? $"{f * 100:0}%  ·  {Mb(e.Bytes)}" : Mb(e.Bytes), UiTheme.AccentText),
        DownloadState.Queued => ("Waiting", UiTheme.Dim),
        _ => ("Failed", UiTheme.Bad),
    };

    private static int Shape(IReadOnlyList<DownloadEntry> entries) =>
        HashCode.Combine(entries.Count, string.Join(",", entries.Select(e => e.Item.Id + e.State)));

    protected override void Build()
    {
        _first = null;
        _rows.Clear();
        var col = Column(Desktop ? 10 : 12);
        AddChild(Scroller(col));
        var heading = "Downloads";
        col.AddChild(UiTheme.Label(UiTheme.Mono ? Loc.Upper(heading) : heading, Desktop ? UiTheme.TitleSize - 6 : UiTheme.TitleSize - 2, UiTheme.Text));
        var store = Host.Downloads;
        var entries = store.Entries.OrderBy(e => e.State == DownloadState.Done ? 1 : 0).ThenBy(e => e.Item.ParentTitle ?? e.Item.Title)
            .ThenBy(e => e.Item.Season).ThenBy(e => e.Item.Index).ToList();
        _shape = Shape(store.Entries);
        var done = entries.Count(e => e.State == DownloadState.Done);
        col.AddChild(UiTheme.Label(entries.Count == 0
                ? "Nothing downloaded yet. On a movie, episode or show page, pick Download to watch it without a network (on a plane, say). What you watch offline syncs back to your server later."
                : $"{done} ready to watch offline  ·  {Mb(store.BytesUsed)} used  ·  in {store.Folder()}",
            UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        var focusables = new List<Control>();
        foreach (var e in entries)
        {
            var item = e.Item;
            var what = item.Kind == MediaKind.Episode ? $"{item.ParentTitle}  ·  {RecapPage.Code(item) ?? ""}".TrimEnd(' ', '·') : item.Year?.ToString() ?? "Movie";
            if (e.Quality != "original")
            {
                what += "  ·  " + Lightcone.Playback.QualityPolicy.Find(e.Quality).Label;
            }
            if (e.Unsynced)
            {
                what += "  ·  will sync when online";
            }
            var (text, color) = Status(e);
            var id = item.Id;
            var row = new ListRow(item.Title, e.Error is { } err ? $"{what}  ·  {err}" : what, text, color, onPress: () => Play(id), help: $"{item.Title}: download")
            {
                CustomMinimumSize = new Vector2(0, Desktop ? 54 : 66),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            row.SetMeta(PosterTile.NoHelp, true);
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 10);
            col.AddChild(line);
            line.AddChild(row);
            var delete = new ActionButton("Delete", () => Delete(id), $"Delete the download of {item.Title}");
            delete.SetMeta(PosterTile.NoHelp, true);
            line.AddChild(delete);
            focusables.Add(row);
            focusables.Add(delete);
            _rows[id] = row;
        }
        if (focusables.Count == 0)
        {
            var back = new ActionButton("Back", () => App.Pop(), "Back");
            col.AddChild(back);
            Focus.Column([back]);
            _first = back;
            return;
        }
        if (entries.Any(e => e.State == DownloadState.Failed))
        {
            var retry = new ActionButton("Retry failed downloads", Host.Downloads.Retry, "Tries the failed ones again (X).");
            col.AddChild(retry);
            focusables.Add(retry);
        }
        Focus.Grid(focusables, 2);
        _first = focusables[0];
    }

    public override void _EnterTree()
    {
        Host.Downloads.Changed += OnChanged;
        TreeExiting += () => Host.Downloads.Changed -= OnChanged;
    }

    private void OnChanged() => Callable.From(() =>
    {
        if (!IsInstanceValid(this))
        {
            return;
        }
        var entries = Host.Downloads.Entries;
        if (Shape(entries) != _shape)
        {
            Rebuild();
            return;
        }
        foreach (var e in entries)
        {
            if (_rows.TryGetValue(e.Item.Id, out var row))
            {
                var (text, color) = Status(e);
                row.SetStatus(text, color);
            }
        }
    }).CallDeferred();

    private void Play(string id)
    {
        if (Host.Downloads.Of(id) is not { State: DownloadState.Done } e)
        {
            App.Toast("Not downloaded yet");
            return;
        }
        // The library's copy when its server is known (resume and watched state), else the saved one.
        MediaActions.Play(Host.Library.Find(id) ?? e.Item);
    }

    private string? FocusedId() =>
        GetViewport()?.GuiGetFocusOwner() is ListRow r ? _rows.FirstOrDefault(kv => kv.Value == r).Key : null;

    public override void OnAction(string action)
    {
        if (action == Pad.Tertiary)
        {
            Host.Downloads.Retry();
            return;
        }
        if (action == Pad.Secondary && FocusedId() is { } id)
        {
            Delete(id);
        }
    }

    private void Delete(string id)
    {
        if (Host.Downloads.Of(id) is { } e)
        {
            App.Confirm($"Delete the download of {e.Item.Title}? It stays on your server.", "Delete", () =>
            {
                Host.Downloads.Remove(id);
                App.Toast($"Deleted {e.Item.Title}");
            });
        }
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
