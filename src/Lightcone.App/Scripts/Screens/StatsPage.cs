using System.Globalization;
using Godot;
using Lightcone.App.Ui;
using Lightcone.Host.Library;

namespace Lightcone.App.Screens;

/// <summary>
/// Your watch stats: what's been watched (servers' watched state too, so it starts full), time
/// this week and month, a day streak, the last two weeks as bars, top shows and genres, and what
/// was watched last. Home: X (Controller) or Your stats (Desktop); also Settings → Library.
/// </summary>
public partial class StatsPage : Screen
{
    private Control? _first;

    protected override void Build()
    {
        _first = null;
        var col = Column(Desktop ? 16 : 20);
        AddChild(Scroller(col));
        var heading = "Your stats";
        col.AddChild(UiTheme.Label(UiTheme.Mono ? heading.ToUpperInvariant() : heading, Desktop ? UiTheme.TitleSize - 6 : UiTheme.TitleSize - 2, UiTheme.Text));
        var stats = Try(() => WatchStats.Compute(Host.Library), new WatchStats(), "your watch history", col);
        if (stats.Empty)
        {
            col.AddChild(UiTheme.Label("Nothing watched yet. Watch something (or sign in to Plex or Jellyfin, whose watched history counts too) and your stats show up here.",
                UiTheme.FontSize - 1, UiTheme.Dim, wrap: true));
            var back = new ActionButton("Back", () => App.Pop(), "Back");
            col.AddChild(back);
            _first = back;
            return;
        }

        var tiles = new HBoxContainer();
        tiles.AddThemeConstantOverride("separation", Desktop ? 12 : 16);
        col.AddChild(tiles);
        tiles.AddChild(Tile(Hours(stats.HoursWatched), "watched"));
        tiles.AddChild(Tile(stats.MoviesWatched.ToString(CultureInfo.InvariantCulture), stats.MoviesWatched == 1 ? "movie" : "movies"));
        tiles.AddChild(Tile(stats.EpisodesWatched.ToString(CultureInfo.InvariantCulture), stats.EpisodesWatched == 1 ? "episode" : "episodes"));
        tiles.AddChild(Tile($"{stats.ShowsFinished} / {stats.ShowsStarted}", "shows finished"));
        tiles.AddChild(Tile(stats.Streak.ToString(CultureInfo.InvariantCulture), stats.Streak == 1 ? "day streak" : "day streak", stats.Streak > 0));

        var facts = new List<string>
        {
            $"This week {Hours(stats.HoursThisWeek)}",
            $"This month {Hours(stats.HoursThisMonth)}",
            $"Longest streak {stats.LongestStreak} {(stats.LongestStreak == 1 ? "day" : "days")}",
        };
        if (stats.BusiestDay is { } day)
        {
            facts.Add($"Most watching on {CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day)}s");
        }
        facts.Add($"Played in Lightcone {Hours(stats.HoursInLightcone)}");
        col.AddChild(UiTheme.Label(string.Join("   ·   ", facts), UiTheme.SmallSize + 2, UiTheme.Dim, wrap: true));

        col.AddChild(Section("Last two weeks"));
        col.AddChild(new DayBars(stats.LastTwoWeeks) { CustomMinimumSize = new Vector2(0, Desktop ? 130 : 150) });

        var lists = new HBoxContainer();
        lists.AddThemeConstantOverride("separation", 30);
        col.AddChild(lists);
        var shows = Column(8);
        var genres = Column(8);
        lists.AddChild(shows);
        lists.AddChild(genres);
        shows.AddChild(Section("Top shows"));
        var focusables = new List<Control>();
        foreach (var line in stats.TopShows)
        {
            var id = line.ItemId;
            var row = new ListRow(line.Title, $"{line.Count} {(line.Count == 1 ? "episode" : "episodes")}", Hours(line.Hours), UiTheme.AccentText, onPress: () =>
            {
                if (id != null && Host.Library.Find(id) is { } show)
                {
                    App.Push(new DetailPage(show));
                }
            }, help: $"{line.Title}: details") { CustomMinimumSize = new Vector2(0, Desktop ? 54 : 66) };
            row.SetMeta(PosterTile.NoHelp, true);
            shows.AddChild(row);
            focusables.Add(row);
        }
        if (stats.TopShows.Count == 0)
        {
            shows.AddChild(UiTheme.Label("No episodes watched yet.", UiTheme.SmallSize + 1, UiTheme.Dim));
        }
        genres.AddChild(Section("Top genres"));
        var total = stats.TopGenres.Sum(g => g.Hours);
        foreach (var g in stats.TopGenres)
        {
            genres.AddChild(GenreBar(g, total > 0 ? g.Hours / total : 0));
        }
        if (stats.TopGenres.Count == 0)
        {
            genres.AddChild(UiTheme.Label("Genres show up once your sources list them.", UiTheme.SmallSize + 1, UiTheme.Dim, wrap: true));
        }

        if (stats.Recent.Count > 0)
        {
            col.AddChild(Section("Watched lately"));
            foreach (var (item, at) in stats.Recent)
            {
                var what = item.Kind == MediaKind.Episode ? $"{item.ParentTitle}  ·  {RecapPage.Code(item) ?? ""}".TrimEnd(' ', '·') : item.Year?.ToString(CultureInfo.InvariantCulture) ?? "Movie";
                var target = item;
                var row = new ListRow(item.Title, what, RecapPage.Ago(at), UiTheme.Dim, onPress: () =>
                {
                    var open = target.Kind == MediaKind.Episode && target.ParentId != null ? Host.Library.Find(target.ParentId) ?? target : target;
                    App.Push(new DetailPage(open));
                }, help: $"{item.Title}: details") { CustomMinimumSize = new Vector2(0, Desktop ? 54 : 66) };
                row.SetMeta(PosterTile.NoHelp, true);
                col.AddChild(row);
                focusables.Add(row);
            }
        }
        if (focusables.Count == 0)
        {
            var back = new ActionButton("Back", () => App.Pop(), "Back");
            col.AddChild(back);
            focusables.Add(back);
        }
        Focus.Column(focusables);
        _first = focusables[0];
    }

    private static string Hours(double h) =>
        h < 1 ? $"{Math.Round(h * 60):0} min" : h < 10 ? $"{h:0.0} h" : $"{h:0} h";

    private Control Tile(string value, string label, bool accent = false)
    {
        var panel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var box = UiTheme.Box(UiTheme.Panel with { A = 0.8f }, 1, 14, UiTheme.Line);
        box.ContentMarginLeft = box.ContentMarginRight = 16;
        box.ContentMarginTop = box.ContentMarginBottom = Desktop ? 10 : 14;
        panel.AddThemeStyleboxOverride("panel", box);
        var col = Column(2);
        panel.AddChild(col);
        col.AddChild(UiTheme.Label(value, Desktop ? UiTheme.TitleSize - 6 : UiTheme.TitleSize - 2, accent ? UiTheme.AccentText : UiTheme.Text));
        col.AddChild(UiTheme.Label(label.ToUpperInvariant(), UiTheme.SmallSize, UiTheme.Dim));
        return panel;
    }

    private Control GenreBar(StatsLine g, double share)
    {
        var col = Column(4);
        var top = new HBoxContainer();
        col.AddChild(top);
        var name = UiTheme.Label(g.Title, UiTheme.SmallSize + 2, UiTheme.Text);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        top.AddChild(name);
        top.AddChild(UiTheme.Label($"{share * 100:0}%  ·  {Hours(g.Hours)}", UiTheme.SmallSize + 1, UiTheme.Dim));
        var bar = new ProgressBar { ShowPercentage = false, ClipContents = true, MaxValue = 1, Step = 0, Value = share, CustomMinimumSize = new Vector2(0, 8) };
        bar.AddThemeFontSizeOverride("font_size", 1);
        bar.AddThemeStyleboxOverride("background", UiTheme.Box(UiTheme.Line, 0, 4));
        bar.AddThemeStyleboxOverride("fill", UiTheme.Box(UiTheme.Accent, 0, 4));
        col.AddChild(bar);
        return col;
    }

    public override void FocusDefault() => _first?.GrabFocus();

    /// <summary>Minutes per day as bars, with the weekday under each and today marked.</summary>
    private partial class DayBars : Control
    {
        private readonly IReadOnlyList<(DateOnly Day, double Minutes)> _days;

        public DayBars(IReadOnlyList<(DateOnly Day, double Minutes)> days)
        {
            _days = days;
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public override void _Draw()
        {
            if (_days.Count == 0)
            {
                return;
            }
            var font = GetThemeDefaultFont();
            var fs = UiTheme.SmallSize;
            var labelH = fs + 8;
            var max = Math.Max(30, _days.Max(d => d.Minutes));
            var slot = Size.X / _days.Count;
            var w = Math.Min(36, slot * 0.6f);
            var chartH = Size.Y - labelH - fs - 6;
            DrawLine(new Vector2(0, chartH + fs + 4), new Vector2(Size.X, chartH + fs + 4), UiTheme.Line, 1);
            for (var i = 0; i < _days.Count; i++)
            {
                var (day, minutes) = _days[i];
                var x = slot * i + (slot - w) / 2;
                var h = (float)(minutes / max) * chartH;
                var today = i == _days.Count - 1;
                if (minutes > 0)
                {
                    DrawRect(new Rect2(x, fs + 4 + chartH - Math.Max(3, h), w, Math.Max(3, h)), today ? UiTheme.Accent : UiTheme.Accent with { A = 0.6f });
                    var text = minutes >= 60 ? $"{minutes / 60:0.#}h" : $"{minutes:0}m";
                    var tw = font.GetStringSize(text, HorizontalAlignment.Left, -1, fs).X;
                    DrawString(font, new Vector2(x + (w - tw) / 2, fs + chartH - Math.Max(3, h)), text, HorizontalAlignment.Left, -1, fs, UiTheme.Dim);
                }
                var name = today ? "Today" : CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day.DayOfWeek);
                var nw = font.GetStringSize(name, HorizontalAlignment.Left, -1, fs).X;
                DrawString(font, new Vector2(slot * i + (slot - nw) / 2, Size.Y - 4), name, HorizontalAlignment.Left, -1, fs, today ? UiTheme.AccentText : UiTheme.Dim);
            }
        }
    }
}
