using Lightcone.Host;
using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// An item's page: art, facts, overview and actions. Shows list their episodes, albums their
/// tracks. Movies, shows and episodes also get a Trailer button, a Cast &amp; crew row (each person
/// opens their other titles in the library) and Trailers &amp; extras, loaded from the server after
/// the page opens. Used as is in both modes (Desktop only shrinks it).
/// </summary>
public partial class DetailPage : Screen
{
    private readonly MediaItem _item;
    private Control? _first;

    // Shows: the season strip, the picked season and its episode list (swapped in place).
    private IReadOnlyList<ShowSeason> _seasons = [];
    private int? _seasonKey;
    private MediaRow? _strip;
    private VBoxContainer? _episodes;
    private List<Control> _actionButtons = [];
    private Button? _downloadButton;
    private double _downloadClock;
    private readonly Dictionary<Control, NodePath> _belowActions = [];

    /// <summary>
    /// Action buttons on more than one line: up and down move between the lines (nearest button);
    /// only the last line goes on down to what's below.
    /// </summary>
    private void WireActionLines()
    {
        var live = _actionButtons.Where(IsInstanceValid).ToList();
        if (live.Count == 0)
        {
            return;
        }
        var lines = live.GroupBy(b => Mathf.RoundToInt(b.Position.Y)).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        Control Nearest(List<Control> line, Control from) =>
            line.MinBy(b => Math.Abs(b.Position.X + b.Size.X / 2 - (from.Position.X + from.Size.X / 2)))!;
        for (var i = 0; i < lines.Count; i++)
        {
            foreach (var b in lines[i])
            {
                if (i > 0)
                {
                    b.FocusNeighborTop = Nearest(lines[i - 1], b).GetPath();
                }
                else if (b.FocusNeighborTop is { } up && up != b.GetPath() && live.Any(o => o.GetPath() == up))
                {
                    b.FocusNeighborTop = b.GetPath();  // was a line below the top one before a re-flow
                }
                if (i < lines.Count - 1)
                {
                    b.FocusNeighborBottom = Nearest(lines[i + 1], b).GetPath();
                }
                else if (_belowActions.TryGetValue(_actionButtons[0], out var below))
                {
                    b.FocusNeighborBottom = below;
                }
                else if (live.Any(o => o != b && o.GetPath() == b.FocusNeighborBottom))
                {
                    // Pointed at a button on a line that's gone after a re-flow: whatever the
                    // first line's buttons go down to, or nowhere.
                    var other = lines[0][0].FocusNeighborBottom;
                    b.FocusNeighborBottom = live.Any(o => o.GetPath() == other) ? b.GetPath() : other;
                }
            }
        }
    }

    /// <summary>Points the action buttons' down at <paramref name="target"/> (the last line only, when wrapped).</summary>
    private void ActionsDownTo(Control target)
    {
        foreach (var b in _actionButtons)
        {
            b.FocusNeighborBottom = target.GetPath();
        }
        if (_actionButtons.Count > 0)
        {
            _belowActions[_actionButtons[0]] = target.GetPath();
        }
        WireActionLines();
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        _downloadClock -= delta;
        if (_downloadClock > 0 || _downloadButton == null || !IsInstanceValid(_downloadButton))
        {
            return;
        }
        _downloadClock = 0.5;
        var text = DownloadActions.Label(_item);
        if (_downloadButton.Text != text)
        {
            _downloadButton.Text = text;
        }
    }

    // Cast, crew and extras: asked for once, then the page is rebuilt with them.
    private ItemDetails? _details;
    private IReadOnlyList<MediaItem> _similar = [];
    private bool _detailsAsked;
    private VBoxContainer? _detailHost;
    private List<MediaRow> _detailRows = [];

    public DetailPage(MediaItem item) => _item = item;

    private bool Listable => _item.Kind is MediaKind.Movie or MediaKind.Show;

    private bool HasSeasons => _item.Kind == MediaKind.Show && _seasons.Count > 0;

    private ShowSeason? CurrentSeason => _seasons.FirstOrDefault(x => x.Key == _seasonKey) ?? _seasons.FirstOrDefault();

    public override IEnumerable<(string Action, string Label)> Hints
    {
        get
        {
            var hints = new List<(string, string)>();
            if (_item.Playable)
            {
                hints.Add((Pad.Secondary, Host.Library.IsWatched(_item) ? "Mark unwatched" : "Mark watched"));
            }
            else if (HasSeasons && FocusedEpisode() is { Playable: true } ep)
            {
                hints.Add((Pad.Secondary, Host.Library.IsWatched(ep) ? "Episode unwatched" : "Episode watched"));
            }
            else if (HasSeasons && CurrentSeason is { } season)
            {
                hints.Add((Pad.Secondary, SeasonWatched(season) ? "Season unwatched" : "Season watched"));
            }
            if (Listable)
            {
                hints.Add((Pad.Tertiary, Host.Library.OnWatchlist(_item) ? "Remove from watchlist" : "Add to watchlist"));
            }
            if (HasSeasons && _seasons.Count > 1)
            {
                hints.Add((Pad.SeekBack, "Previous season"));
                hints.Add((Pad.SeekForward, "Next season"));
            }
            return hints;
        }
    }

    protected override void Build()
    {
        _first = null;
        _downloadButton = null;
        _belowActions.Clear();
        _strip = null;
        _episodes = null;
        var lib = Host.Library;
        var col = Column(Desktop ? 16 : 22);
        AddChild(Faded(Scroller(col)));

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
        top.AddChild(new PosterArt(_item, Desktop ? art * 0.85f : art, PosterTile.ProgressOf(lib, _item), 14, showTitle: false));
        var info = Column(Desktop ? 8 : 12);
        top.AddChild(info);
        info.AddChild(UiTheme.Label(_item.Title, Desktop ? UiTheme.TitleSize - 4 : UiTheme.TitleSize + 2, UiTheme.Text, wrap: true));
        var meta = new List<string>();
        if (_item.Subtitle != null && _item.Kind is MediaKind.Episode or MediaKind.Album or MediaKind.Track or MediaKind.Collection)
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
        if (_item.ContentRating is { } cr)
        {
            meta.Add(cr);
        }
        if (_item.Rating is { } r)
        {
            meta.Add($"★ {r:0.0}");
        }
        meta.AddRange(_item.Genres);
        info.AddChild(UiTheme.Label(string.Join("  ·  ", meta), UiTheme.SmallSize + 2, UiTheme.Dim, wrap: true));
        if (_item.NotInLibrary)
        {
            // On the Plex watchlist, but no server has it: nothing to play here.
            info.AddChild(UiTheme.Label(UiTheme.Mono ? Loc.Upper("Not on your server") : "Not on your server", UiTheme.SmallSize, UiTheme.AccentText));
            info.AddChild(UiTheme.Label($"It's on {_item.SourceName.Replace("Your ", "your ")}. It shows up to play once your server has it.",
                UiTheme.SmallSize, UiTheme.Dim, wrap: true));
        }
        else
        {
            info.AddChild(UiTheme.Label("From " + _item.SourceName, UiTheme.SmallSize, UiTheme.Dim));
        }
        if (Spoilers.Overview(_item) is { } o)
        {
            var ov = UiTheme.Label(o, UiTheme.FontSize - 2, UiTheme.Text, wrap: true);
            ov.CustomMinimumSize = new Vector2(360, 0);
            info.AddChild(ov);
        }
        // Wraps onto a second line when the buttons don't fit (1280x800 with long labels).
        var actions = new HFlowContainer();
        actions.AddThemeConstantOverride("h_separation", 14);
        actions.AddThemeConstantOverride("v_separation", 12);
        actions.SortChildren += () => Callable.From(WireActionLines).CallDeferred();
        info.AddChild(actions);
        var buttons = new List<Control>();
        void Act(string label, Action a, string help)
        {
            var b = new ActionButton(label, a, help) { CustomMinimumSize = new Vector2(0, Desktop ? 42 : 54) };
            b.SetMeta(PosterTile.NoHelp, true);  // the tooltip names it for tests; the label says it all
            actions.AddChild(b);
            buttons.Add(b);
        }
        var children = lib.Children(_item.Id);
        _seasons = _item.Kind == MediaKind.Show ? lib.Seasons(_item) : [];
        if (_item.Kind == MediaKind.Collection)
        {
            BuildCollection(col, info, children, Act, buttons);
            return;
        }
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
            var watched = lib.IsWatched(_item);
            Act(watched ? "Mark unwatched" : "Mark watched", ToggleWatched, $"{_item.Title}: mark {(watched ? "unwatched" : "watched")}");
            if (DownloadActions.CanDownload(_item))
            {
                Act(DownloadActions.Label(_item), () => DownloadActions.Press(_item), $"{_item.Title}: download");
                _downloadButton = (Button)buttons[^1];
            }
        }
        else if (children.Count > 0)
        {
            var music = _item.Kind == MediaKind.Album;
            var label = music ? "▶  Play album" : "▶  Play next episode";
            if (!music && lib.NextToPlay(_item) is { } next)
            {
                // Name the episode: "▶  Resume S2 · E4", "▶  Play S1 · E1".
                var verb = lib.PositionOf(next) != null ? "Resume" : "Play";
                label = $"▶  {verb} {EpisodeCode(next) ?? next.Title}";
            }
            Act(label, () => MediaActions.PlayContainer(_item), $"{_item.Title}: play");
            if (music || children.Count(c => c.Playable) > 2)
            {
                Act("Shuffle", () => MediaActions.PlayContainer(_item, shuffle: true), $"{_item.Title}: shuffle");
            }
            if (!music && children.Any(DownloadActions.CanDownload))
            {
                Act("⤓  Download…", OfferShowDownload, $"{_item.Title}: download");
            }
        }
        if (lib.CanCast(_item))
        {
            Act("Play on…", () => App.Push(new CastPage(_item, lib.PositionOf(_item) ?? 0)), $"{_item.Title}: play on another screen (a TV or phone app)");
        }
        if (_details?.Trailer is { } trailer)
        {
            Act("▶  Trailer", () => MediaActions.PlayExtra(_item, trailer), $"{_item.Title}: trailer");
        }
        if (Listable)
        {
            var on = lib.OnWatchlist(_item);
            Act(on ? "✓  On watchlist" : "＋  Watchlist", ToggleWatchlist, $"{_item.Title}: {(on ? "remove from" : "add to")} watchlist");
            if (!_item.NotInLibrary)
            {
                Act("Collections…", PickCollection, $"{_item.Title}: collections");
            }
        }
        Focus.Row(buttons);
        _first = buttons.FirstOrDefault();
        _actionButtons = buttons;
        AddTrivia(info);
        if (_item.Kind == MediaKind.Show)
        {
            AddLeftOff(info);
        }

        _detailRows = [];
        _detailHost = Column(Desktop ? 16 : 22);
        if (HasSeasons)
        {
            BuildSeasons(col, buttons);
        }
        else if (children.Count > 0)
        {
            col.AddChild(Section(_item.Kind == MediaKind.Album ? "Tracks" : "Episodes"));
            var rows = new List<Control>();
            foreach (var c in children)
            {
                var progress = PosterTile.ProgressOf(lib, c);
                var status = lib.IsWatched(c) ? "Watched" : progress is { } p ? $"{p * 100:0}%" : c.DurationText;
                var label = c.Index is { } n ? (c.Season is { } sn && children.Any(x => x.Season != sn) ? $"S{sn} E{n}.  {c.Title}" : $"{n}.  {c.Title}") : c.Title;
                var row = new ListRow(label, c.Kind == MediaKind.Episode ? "" : c.Subtitle ?? "", status,
                    lib.IsWatched(c) ? UiTheme.Good : UiTheme.Dim, onPress: () => MediaActions.Play(c), help: $"{c.Title}: play")
                {
                    CustomMinimumSize = new Vector2(0, Desktop ? 48 : 60),
                };
                row.SetMeta(PosterTile.NoHelp, true);
                col.AddChild(row);
                rows.Add(row);
            }
            Focus.Column(rows, above: buttons.FirstOrDefault());
            foreach (var b in buttons)
            {
                b.FocusNeighborBottom = rows[0].GetPath();
            }
            _belowActions[buttons[0]] = rows[0].GetPath();
        }
        col.AddChild(_detailHost);
        // The series it's in shows straight away (from the library); cast, extras and similar
        // titles come from the server, then the page fills them in.
        FillDetails();
        LoadDetails();
    }

    // ---- trivia ----

    /// <summary>A small "Did you know?" box under the buttons, filled in when Wikipedia has something.</summary>
    private async void AddTrivia(VBoxContainer info)
    {
        if (!Lightcone.Host.Library.Trivia.Applies(_item) || !Host.Settings.Trivia)
        {
            return;
        }
        var host = new VBoxContainer { Visible = false };
        info.AddChild(host);
        Lightcone.Host.Library.TriviaFact? fact = null;
        try
        {
            fact = await Host.Trivia.GetAsync(_item);
        }
        catch (Exception e)
        {
            AppLog.Info($"trivia for {_item.Id}: {e.Message}");
        }
        if (fact == null || !IsInstanceValid(host))
        {
            return;
        }
        var box = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel with { A = 0.85f }, 1, 10, UiTheme.Line));
        var inner = new VBoxContainer();
        inner.AddThemeConstantOverride("separation", 4);
        var margin = new MarginContainer();
        foreach (var (side, v) in new[] { ("left", 14), ("right", 14), ("top", 10), ("bottom", 10) })
        {
            margin.AddThemeConstantOverride($"margin_{side}", v);
        }
        margin.AddChild(inner);
        box.AddChild(margin);
        var head = UiTheme.Label(UiTheme.Mono ? Loc.Upper("Did you know?") : "Did you know?", UiTheme.SmallSize, UiTheme.AccentText);
        inner.AddChild(head);
        var text = UiTheme.Label(fact.Text, UiTheme.SmallSize + 1, UiTheme.Text, wrap: true);
        text.MaxLinesVisible = 3;
        text.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        text.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        inner.AddChild(text);
        inner.AddChild(UiTheme.Label("From " + fact.Source, UiTheme.SmallSize - 1, UiTheme.Dim));
        host.AddChild(new Control { CustomMinimumSize = new Vector2(0, 2) });
        host.AddChild(box);
        host.Visible = true;
    }

    // ---- cast, crew and extras ----

    private bool HasDetails => _item.Kind is MediaKind.Movie or MediaKind.Show or MediaKind.Episode && !_item.NotInLibrary;

    private async void LoadDetails()
    {
        if (_detailsAsked || !HasDetails)
        {
            return;
        }
        _detailsAsked = true;
        // Shows and movies list similar titles straight away (from the library), then the server's picks.
        if (_item.Kind is MediaKind.Movie or MediaKind.Show)
        {
            _similar = Host.Library.SimilarInLibrary(_item);
        }
        var similar = _item.Kind is MediaKind.Movie or MediaKind.Show ? Host.Library.SimilarAsync(_item) : Task.FromResult<IReadOnlyList<MediaItem>>([]);
        ItemDetails? details = null;
        try
        {
            details = await Host.Library.DetailsAsync(_item);
        }
        catch (Exception e)
        {
            AppLog.Info($"details of {_item.Id}: {e.Message}");
        }
        IReadOnlyList<MediaItem> more = [];
        try
        {
            more = await similar;
        }
        catch (Exception e)
        {
            AppLog.Info($"similar to {_item.Id}: {e.Message}");
        }
        if (!IsInstanceValid(this))
        {
            return;
        }
        var changed = false;
        if (details != null && (details.People.Count > 0 || details.Extras.Count > 0))
        {
            _details = details;
            changed = true;
        }
        if (more.Count > 0 && !more.Select(m => m.Id).SequenceEqual(_similar.Select(m => m.Id)))
        {
            _similar = more;
            changed = true;
        }
        if (changed || _similar.Count > 0)
        {
            Rebuild();
        }
    }

    /// <summary>Stand-in tile items for people: square photo, name, part or job.</summary>
    private static MediaItem PersonItem(Person p) => new()
    {
        Id = $"person:{p.SourceId}:{p.Key}",
        Title = p.Name,
        Subtitle = p.Credit,
        Kind = MediaKind.Artist,
        PosterUrl = p.PhotoUrl,
        SourceId = p.SourceId,
        SourceName = "",
    };

    private MediaItem ExtraItem(Extra x) => new()
    {
        Id = $"extra:{_item.Id}:{x.Title}",
        Title = x.Title,
        Subtitle = x.Duration is { } d ? $"{x.Kind}  ·  {MediaItem.FormatDuration(d)}" : x.Kind,
        Kind = MediaKind.Movie,
        PosterUrl = x.ThumbUrl ?? _item.PosterUrl,
        Poster = x.ThumbUrl == null ? _item.Poster : null,
        SourceId = _item.SourceId,
        SourceName = _item.SourceName,
    };

    private void FillDetails()
    {
        if (_detailHost == null)
        {
            return;
        }
        var lib = Host.Library;
        if (_details?.People.Count > 0)
        {
            var people = _details.People;
            var items = people.Select(PersonItem).ToList();
            var row = new MediaRow(new HomeRow("cast", "Cast & crew", items), lib, Desktop, it => App.Push(new PersonPage(people[items.IndexOf(it)])), scale: 0.72f);
            _detailHost.AddChild(row);
            _detailRows.Add(row);
        }
        if (_details?.Extras.Count > 0)
        {
            var extras = _details.Extras;
            var items = extras.Select(ExtraItem).ToList();
            var row = new MediaRow(new HomeRow("extras", extras.Count == 1 && extras[0].IsTrailer ? "Trailer" : "Trailers & extras", items, Wide: true), lib, Desktop,
                it => MediaActions.PlayExtra(_item, extras[items.IndexOf(it)]), scale: 0.8f);
            _detailHost.AddChild(row);
            _detailRows.Add(row);
        }
        // The franchise or collection it belongs to: "In John Wick", the others in release order.
        if (!_item.NotInLibrary && lib.SeriesWith(_item).FirstOrDefault() is { } series)
        {
            var others = lib.Children(series.Id).Where(m => m.Id != _item.Id).ToList();
            if (others.Count > 0)
            {
                var row = new MediaRow(new HomeRow("series", $"In {series.Title}", others), lib, Desktop, MediaActions.Open, scale: 0.85f,
                    seeAll: () => App.Push(new DetailPage(series)), seeAllDetail: series.Subtitle);
                _detailHost.AddChild(row);
                _detailRows.Add(row);
            }
        }
        if (_similar.Count > 0)
        {
            var row = new MediaRow(new HomeRow("similar", "More like this", _similar), lib, Desktop, MediaActions.Open, scale: 0.85f);
            _detailHost.AddChild(row);
            _detailRows.Add(row);
        }
        LinkDetails();
    }

    /// <summary>Up / down between the rows above (actions or the episode list) and the cast and extras rows.</summary>
    private void LinkDetails()
    {
        if (_detailRows.Count == 0)
        {
            return;
        }
        Control? above = (_episodes != null ? _episodes.GetChildren().OfType<ListRow>().LastOrDefault() : LastListRow()) ?? _actionButtons.FirstOrDefault();
        MediaRow.LinkRows(_detailRows, above);
        var first = _detailRows[0].Tiles[0];
        if (above is ListRow lastRow)
        {
            lastRow.FocusNeighborBottom = first.GetPath();
        }
        else
        {
            ActionsDownTo(first);
        }
    }

    /// <summary>The last episode / track row of a list without seasons.</summary>
    private ListRow? LastListRow()
    {
        return FindChildren("*", "", true, false).OfType<ListRow>().LastOrDefault();
    }

    /// <summary>A collection: its titles as a poster grid, plus rename / delete for one made here.</summary>
    private void BuildCollection(VBoxContainer col, VBoxContainer info, IReadOnlyList<MediaItem> members, Action<string, Action, string> act, List<Control> buttons)
    {
        var lib = Host.Library;
        // How far through it you are: movies count when watched, shows when every episode is.
        if (members.Count > 0)
        {
            var seen = members.Count(m => m.Kind == MediaKind.Movie ? lib.IsWatched(m) : lib.Matches(m, WatchFilter.Watched));
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 12);
            var bar = new ProgressBar { MinValue = 0, MaxValue = members.Count, Value = seen, ShowPercentage = false, CustomMinimumSize = new Vector2(Desktop ? 160 : 200, 8) };
            bar.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            bar.AddThemeStyleboxOverride("background", UiTheme.Box(UiTheme.Line, 0, 4, UiTheme.Line));
            bar.AddThemeStyleboxOverride("fill", UiTheme.Box(UiTheme.Accent, 0, 4, UiTheme.Accent));
            line.AddChild(bar);
            line.AddChild(UiTheme.Label(seen == members.Count ? $"All {members.Count} watched" : $"{seen} of {members.Count} watched", UiTheme.SmallSize + 1, UiTheme.Dim));
            info.AddChild(line);
            info.MoveChild(line, info.GetChildCount() - 2);  // above the buttons
        }
        // The first one not watched yet: a movie plays, a show plays its next episode.
        var next = members.FirstOrDefault(m => m.Playable && lib.PositionOf(m) != null)
            ?? members.FirstOrDefault(m => m.Kind == MediaKind.Movie ? m.Playable && !lib.IsWatched(m) : !lib.Matches(m, WatchFilter.Watched));
        if (next != null)
        {
            var verb = lib.PositionOf(next) != null ? "Resume" : next.Kind == MediaKind.Show ? "Next up:" : "Play next:";
            act($"▶  {verb} {next.Title}", () =>
            {
                if (next.Kind == MediaKind.Show)
                {
                    MediaActions.PlayContainer(next);
                }
                else
                {
                    MediaActions.Play(next);
                }
            }, $"{_item.Title}: play next unwatched");
        }
        else if (members.FirstOrDefault(m => m.Playable) is { } first)
        {
            act($"▶  Watch again: {first.Title}", () => MediaActions.Play(first, fromStart: true), $"{_item.Title}: play");
        }
        if (MediaLibrary.IsUserCollection(_item.Id))
        {
            var id = MediaLibrary.UserCollectionId(_item);
            act("Rename", () => App.Push(new TextEntryPage("Rename collection", _item.Title, name =>
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    lib.Lists.RenameCollection(id, name);
                    App.Pop();
                    if (lib.Find(_item.Id) is { } renamed)
                    {
                        App.Push(new DetailPage(renamed));
                    }
                }
            })), $"{_item.Title}: rename");
            act("Delete", () => App.Confirm($"Delete the collection \"{_item.Title}\"? The movies and shows in it stay.", "Delete", () =>
            {
                lib.Lists.DeleteCollection(id);
                App.Pop();
                App.Toast($"Deleted {_item.Title}");
            }), $"{_item.Title}: delete");
        }
        Focus.Row(buttons);
        _first = buttons.FirstOrDefault();
        if (members.Count == 0)
        {
            col.AddChild(UiTheme.Label("Nothing in it yet. Open a movie or show and pick Collections… to add it.", UiTheme.FontSize - 1, UiTheme.Dim, wrap: true));
            return;
        }
        col.AddChild(Section("Titles"));
        var shape = TileShape.Poster;
        var gap = Desktop ? 18 : 24;
        var columns = ColumnsFor(PosterTile.SizeFor(shape, Desktop).X, gap);
        var grid = new GridContainer { Columns = columns };
        grid.AddThemeConstantOverride("h_separation", gap);
        grid.AddThemeConstantOverride("v_separation", gap);
        col.AddChild(grid);
        var tiles = new List<Control>();
        foreach (var m in members)
        {
            var tile = new PosterTile(m, shape, PosterTile.ProgressOf(lib, m), Desktop, MediaActions.Open, FillScale());
            grid.AddChild(tile);
            tiles.Add(tile);
        }
        Focus.Grid(tiles, columns);
        _first ??= tiles[0];
        foreach (var b in buttons)
        {
            b.FocusNeighborBottom = tiles[0].GetPath();
        }
        if (buttons.Count > 0)
        {
            _belowActions[buttons[0]] = tiles[0].GetPath();
        }
        for (var i = 0; i < Math.Min(columns, tiles.Count); i++)
        {
            if (buttons.Count > 0)
            {
                tiles[i].FocusNeighborTop = buttons[0].GetPath();
            }
        }
    }

    /// <summary>"Where you left off": the last episode watched before the next one, and what happened in it.</summary>
    private void AddLeftOff(VBoxContainer info)
    {
        var lib = Host.Library;
        if (lib.NextToPlay(_item) is not { } next || lib.PositionOf(next) != null || lib.PreviousEpisode(next) is not { } prev || !lib.IsWatched(prev))
        {
            return;
        }
        var panel = new PanelContainer();
        var box = UiTheme.Box(UiTheme.PanelHi with { A = 0.6f }, 1, 12, UiTheme.Line);
        box.ContentMarginLeft = box.ContentMarginRight = 14;
        box.ContentMarginTop = box.ContentMarginBottom = 10;
        panel.AddThemeStyleboxOverride("panel", box);
        var col = Column(4);
        panel.AddChild(col);
        var when = lib.LastViewed(prev) is { } at ? "  ·  " + RecapPage.Ago(at).ToUpperInvariant() : "";
        col.AddChild(UiTheme.Label("WHERE YOU LEFT OFF" + when, UiTheme.SmallSize, UiTheme.AccentText));
        var code = RecapPage.Code(prev);
        col.AddChild(UiTheme.Label(code != null ? $"{code}  ·  {prev.Title}" : prev.Title, UiTheme.FontSize - 1, UiTheme.Text, wrap: true));
        if (prev.Overview is { Length: > 0 } o)
        {
            var ov = UiTheme.Label(o.ReplaceLineEndings(" "), UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true);
            ov.MaxLinesVisible = 3;
            ov.CustomMinimumSize = new Vector2(360, 0);
            col.AddChild(ov);
        }
        info.AddChild(panel);
    }

    // ---- shows: seasons ----

    /// <summary>"S2 · E4" for an episode (specials: "Special 3"), or null without numbers.</summary>
    private static string? EpisodeCode(MediaItem e) => (e.Season, e.Index) switch
    {
        (0, { } n) => $"Special {n}",
        ({ } s, { } n) => $"S{s} · E{n}",
        (null, { } n) => $"E{n}",
        _ => null,
    };

    private bool SeasonWatched(ShowSeason season)
    {
        var lib = Host.Library;
        var playable = season.Episodes.Where(e => e.Playable).ToList();
        return playable.Count > 0 && playable.All(lib.IsWatched);
    }

    /// <summary>
    /// A strip of season posters (only with more than one season) above the picked season's
    /// episodes. Moving along the strip switches season; LT / RT switch from anywhere on the page.
    /// </summary>
    private void BuildSeasons(VBoxContainer col, List<Control> buttons)
    {
        var lib = Host.Library;
        if (_seasonKey == null || _seasons.All(x => x.Key != _seasonKey))
        {
            // Open on the season of the episode "Play" would start.
            var next = lib.NextToPlay(_item);
            _seasonKey = (next != null ? _seasons.FirstOrDefault(x => x.Episodes.Contains(next)) : null)?.Key ?? _seasons[0].Key;
        }
        if (_seasons.Count > 1)
        {
            var items = _seasons.Select(SeasonItem).ToList();
            // A (click, tap) on a season: pick it (a click may not focus it first) and go to its next episode.
            _strip = new MediaRow(new HomeRow("seasons", $"Seasons  ·  {_seasons.Count}", items), lib, Desktop, it =>
            {
                SelectSeason(_seasons[items.IndexOf(it)].Key, keepFocus: true);
                FocusEpisodes();
            }, scale: 0.7f);
            col.AddChild(_strip);
            for (var i = 0; i < _strip.Tiles.Count; i++)
            {
                var season = _seasons[i];
                var tile = _strip.Tiles[i];
                var playable = season.Episodes.Where(e => e.Playable).ToList();
                var left = playable.Count(e => !lib.IsWatched(e));
                tile.Badge = playable.Count == 0 ? null : left == 0 ? "✓" : left < playable.Count ? left.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                tile.Selected = season.Key == _seasonKey;
                tile.SetProgress(playable.Count > 0 && left > 0 && left < playable.Count ? 1 - (double)left / playable.Count : null);
                tile.FocusEntered += () => SelectSeason(season.Key, keepFocus: true);
            }
            MediaRow.LinkRows([_strip], above: buttons.FirstOrDefault());
            foreach (var b in buttons)
            {
                b.FocusNeighborBottom = SelectedTile()!.GetPath();
            }
            if (buttons.Count > 0)
            {
                _belowActions[buttons[0]] = SelectedTile()!.GetPath();
            }
        }
        _episodes = Column(Desktop ? 8 : 10);
        col.AddChild(_episodes);
        FillEpisodes();
    }

    /// <summary>A stand-in item for a season tile: its poster (or the show's), name and watched count.</summary>
    private MediaItem SeasonItem(ShowSeason season)
    {
        var lib = Host.Library;
        var playable = season.Episodes.Where(e => e.Playable).ToList();
        var watched = playable.Count(lib.IsWatched);
        var count = $"{season.Episodes.Count} episode{(season.Episodes.Count == 1 ? "" : "s")}";
        return new MediaItem
        {
            Id = $"{_item.Id}#season:{season.Key}",
            Title = season.Title,
            Kind = MediaKind.Show,
            Subtitle = watched == 0 || watched == playable.Count ? count : $"{watched} of {playable.Count} watched",
            Poster = season.PosterUrl == null ? _item.Poster : null,
            PosterUrl = season.PosterUrl ?? _item.PosterUrl,
            SourceId = _item.SourceId,
            SourceName = _item.SourceName,
        };
    }

    private PosterTile? SelectedTile()
    {
        var i = _seasons.ToList().FindIndex(x => x.Key == _seasonKey);
        return _strip != null && i >= 0 && i < _strip.Tiles.Count ? _strip.Tiles[i] : null;
    }

    /// <summary>Switches the episode list to another season, in place (the strip and focus stay).</summary>
    private void SelectSeason(int key, bool keepFocus)
    {
        if (key == _seasonKey || _episodes == null)
        {
            return;
        }
        var inEpisodes = GetViewport()?.GuiGetFocusOwner() is { } f && _episodes.IsAncestorOf(f);
        _seasonKey = key;
        if (_strip != null)
        {
            for (var i = 0; i < _strip.Tiles.Count; i++)
            {
                _strip.Tiles[i].Selected = _seasons[i].Key == key;
            }
            if (SelectedTile() is { } tile)
            {
                _strip.Reveal(tile);
                ActionsDownTo(tile);
            }
        }
        FillEpisodes();
        if (!keepFocus && inEpisodes)
        {
            FocusEpisodes();
        }
        App.UpdateChrome();
    }

    private void StepSeason(int dir)
    {
        var i = _seasons.ToList().FindIndex(x => x.Key == _seasonKey);
        var j = Math.Clamp(i + dir, 0, _seasons.Count - 1);
        if (j == i)
        {
            return;
        }
        var focusOnStrip = _strip != null && GetViewport()?.GuiGetFocusOwner() is PosterTile t && _strip.Tiles.Contains(t);
        SelectSeason(_seasons[j].Key, keepFocus: false);
        if (focusOnStrip)
        {
            SelectedTile()?.GrabFocus();
        }
    }

    /// <summary>Focus the picked season's next episode to watch (else its first).</summary>
    private void FocusEpisodes()
    {
        if (_episodes == null || CurrentSeason is not { } season)
        {
            return;
        }
        var lib = Host.Library;
        var rows = _episodes.GetChildren().OfType<ListRow>().ToList();
        if (rows.Count == 0)
        {
            return;
        }
        var next = season.Episodes.FirstOrDefault(e => lib.PositionOf(e) != null) ?? season.Episodes.FirstOrDefault(e => e.Playable && !lib.IsWatched(e));
        var i = next == null ? 0 : season.Episodes.ToList().IndexOf(next);
        rows[Math.Clamp(i, 0, rows.Count - 1)].GrabFocus();
    }

    /// <summary>The picked season's header (name, counts, Mark season watched) and its episodes.</summary>
    private void FillEpisodes()
    {
        if (_episodes == null || CurrentSeason is not { } season)
        {
            return;
        }
        foreach (var c in _episodes.GetChildren())
        {
            _episodes.RemoveChild(c);
            c.QueueFree();
        }
        var lib = Host.Library;
        var playable = season.Episodes.Where(e => e.Playable).ToList();
        var left = playable.Count(e => !lib.IsWatched(e));
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 16);
        _episodes.AddChild(head);
        var titleCol = Column(2);
        titleCol.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(titleCol);
        titleCol.AddChild(UiTheme.Label(UiTheme.Mono ? season.Title.ToUpperInvariant() : season.Title, Desktop ? UiTheme.FontSize + 2 : UiTheme.FontSize + 6, UiTheme.Text));
        var counts = $"{season.Episodes.Count} episode{(season.Episodes.Count == 1 ? "" : "s")}";
        if (playable.Count > 0)
        {
            counts += left == 0 ? "  ·  all watched" : left < playable.Count ? $"  ·  {left} left" : "";
        }
        var runtime = season.Episodes.Sum(e => e.Duration ?? 0);
        if (runtime > 0)
        {
            counts += $"  ·  {MediaItem.FormatDuration(runtime)}";
        }
        titleCol.AddChild(UiTheme.Label(counts, UiTheme.SmallSize + 1, left == 0 && playable.Count > 0 ? UiTheme.Good : UiTheme.Dim));
        ActionButton? mark = null;
        if (playable.Count > 0)
        {
            var all = left == 0;
            mark = new ActionButton(all ? "Mark season unwatched" : "Mark season watched", ToggleSeasonWatched,
                $"{_item.Title}: {season.Title}: mark {(all ? "unwatched" : "watched")}")
            {
                CustomMinimumSize = new Vector2(0, Desktop ? 38 : 48),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
            };
            mark.SetMeta(PosterTile.NoHelp, true);
            head.AddChild(mark);
        }

        var rows = new List<Control>();
        var thumb = Desktop ? new Vector2(128, 72) : new Vector2(160, 90);
        foreach (var e in season.Episodes)
        {
            var progress = PosterTile.ProgressOf(lib, e);
            var watched = lib.IsWatched(e);
            var status = watched ? "✓  Watched" : progress is { } p && e.Duration is { } d ? $"{MediaItem.FormatDuration(d * (1 - p))} left" : e.DurationText;
            var label = e.Index is { } n ? $"{n}.  {e.Title}" : e.Title;
            var art = new PosterArt(e, thumb, progress, 8, showTitle: false) { Badge = watched ? "✓" : null };
            var detail = Spoilers.Overview(e) is { } o ? o.ReplaceLineEndings(" ") : Spoilers.Hidden(e) ? "Summary hidden until you watch it." : "";
            var row = new ListRow(label, detail, status, watched ? UiTheme.Good : UiTheme.Dim, art,
                onPress: () => MediaActions.Play(e), help: $"{e.Title}: play")
            {
                CustomMinimumSize = new Vector2(0, thumb.Y + 18),
            };
            row.SetMeta(PosterTile.NoHelp, true);
            row.SetMeta(EpisodeMeta, e.Id);
            _episodes.AddChild(row);
            rows.Add(row);
        }
        if (rows.Count == 0)
        {
            return;
        }
        Control above = SelectedTile() as Control ?? _actionButtons.FirstOrDefault() ?? rows[0];
        if (mark != null)
        {
            Focus.Row([mark], above: above, below: rows[0]);
            above = mark;
        }
        Focus.Column(rows, above: above);
        var down = mark as Control ?? rows[0];
        if (_strip != null)
        {
            foreach (var t in _strip.Tiles)
            {
                t.FocusNeighborBottom = down.GetPath();
            }
        }
        else
        {
            ActionsDownTo(down);
        }
        LinkDetails();
    }

    /// <summary>A show's Download: the next few episodes to watch, or the season on screen.</summary>
    private void OfferShowDownload()
    {
        var options = new List<(string, Action)>();
        var next = DownloadActions.NextEpisodes(_item, 5);
        if (next.Count > 0)
        {
            options.Add(($"The next {next.Count} to watch (from {RecapPage.Code(next[0]) ?? next[0].Title})", () => DownloadActions.Offer(next, _item.Title)));
        }
        if (CurrentSeason is { } season && season.Episodes.Count(e => e.Playable) > 0)
        {
            var eps = season.Episodes.Where(e => e.Playable).ToList();
            options.Add(($"All of {season.Title} ({eps.Count} episodes)", () => DownloadActions.Offer(eps, $"{_item.Title}: {season.Title}")));
        }
        options.Add(("See downloads", () => App.Push(new DownloadsPage())));
        options.Add(("Cancel", () => { }));
        App.Push(new ChoicePage($"Download {_item.Title} to watch offline:", [.. options]));
    }

    private const string EpisodeMeta = "lightcone_episode";

    /// <summary>The episode whose row has focus, if any.</summary>
    private MediaItem? FocusedEpisode()
    {
        if (GetViewport().GuiGetFocusOwner() is not { } c || !c.HasMeta(EpisodeMeta) || !IsAncestorOf(c))
        {
            return null;
        }
        var id = c.GetMeta(EpisodeMeta).AsString();
        return CurrentSeason?.Episodes.FirstOrDefault(e => e.Id == id);
    }

    private void ToggleEpisodeWatched(MediaItem e)
    {
        var lib = Host.Library;
        var watched = !lib.IsWatched(e);
        lib.SetWatched(e, watched);
        var name = RecapPage.Code(e) ?? e.Title;
        App.Toast(watched ? $"Marked {name} watched" : $"Marked {name} unwatched");
        var id = e.Id;
        Rebuild();
        // Stay on the same episode after the rebuild.
        if (_episodes?.GetChildren().OfType<Control>().FirstOrDefault(r => r.HasMeta(EpisodeMeta) && r.GetMeta(EpisodeMeta).AsString() == id) is { } row)
        {
            row.CallDeferred(Control.MethodName.GrabFocus);
        }
        App.UpdateChrome();
    }

    private void ToggleSeasonWatched()
    {
        if (CurrentSeason is not { } season)
        {
            return;
        }
        var lib = Host.Library;
        var watched = !SeasonWatched(season);
        foreach (var e in season.Episodes.Where(e => e.Playable && lib.IsWatched(e) != watched))
        {
            lib.SetWatched(e, watched);
        }
        App.Toast(watched ? $"Marked {season.Title} watched" : $"Marked {season.Title} unwatched");
        Rebuild();
        App.UpdateChrome();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!HasSeasons || _seasons.Count < 2 || App.Top != this || !e.IsPressed())
        {
            return;
        }
        if (e.IsAction(Pad.SeekBack) || e.IsAction(Pad.SeekForward))
        {
            StepSeason(e.IsAction(Pad.SeekBack) ? -1 : 1);
            GetViewport().SetInputAsHandled();
        }
    }

    private void ToggleWatchlist()
    {
        var lib = Host.Library;
        var on = !lib.OnWatchlist(_item);
        lib.SetWatchlist(_item, on);
        App.Toast(on ? $"Added {_item.Title} to your watchlist" : $"Removed {_item.Title} from your watchlist");
        Rebuild();
        App.UpdateChrome();
    }

    /// <summary>Add to or remove from collections made here, or start a new one with this title.</summary>
    private void PickCollection()
    {
        var lib = Host.Library;
        var options = new List<(string, Action)>();
        foreach (var c in lib.Lists.Collections)
        {
            var inIt = c.Items.Contains(_item.Id);
            options.Add((inIt ? $"✓  {c.Name}" : c.Name, () =>
            {
                lib.Lists.SetInCollection(c.Id, _item.Id, !inIt);
                App.Toast(inIt ? $"Removed from {c.Name}" : $"Added to {c.Name}");
            }));
        }
        options.Add(("＋  New collection…", () => App.Push(new TextEntryPage("New collection", "", name =>
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                var c = lib.Lists.CreateCollection(name, [_item.Id]);
                App.Toast($"Added to {c.Name}");
            }
        }, "Name the collection. You can add more titles from their pages."))));
        options.Add(("Cancel", () => { }));
        var server = _item.Collections.Count > 0 ? $" It's also in {string.Join(", ", _item.Collections)} on {_item.SourceName}." : "";
        App.Push(new ChoicePage($"Collections for {_item.Title}. Pick one to add or remove it.{server}", [.. options]));
    }

    private void ToggleWatched()
    {
        var lib = Host.Library;
        lib.SetWatched(_item, !lib.IsWatched(_item));
        Rebuild();
        App.UpdateChrome();
    }

    public override void OnAction(string action)
    {
        if (action == Pad.Secondary && _item.Playable)
        {
            ToggleWatched();
        }
        else if (action == Pad.Secondary && HasSeasons && FocusedEpisode() is { Playable: true } ep)
        {
            ToggleEpisodeWatched(ep);
        }
        else if (action == Pad.Secondary && HasSeasons)
        {
            ToggleSeasonWatched();
        }
        else if (action == Pad.Tertiary && Listable)
        {
            ToggleWatchlist();
        }
    }

    public override void FocusDefault() => _first?.GrabFocus();
}
