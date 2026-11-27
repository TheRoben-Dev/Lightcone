using System.Globalization;

namespace Lightcone.Playback;

/// <summary>
/// The mpv options for how text subtitles look (size, colour, outline or box, height, bold).
/// Picture subtitles (PGS, VobSub) keep their own look; ASS styling is overridden only where
/// the user's choice applies (see sub-ass-override=scale / force).
/// </summary>
public static class SubtitleLook
{
    public static readonly (string Id, string Label, string Color)[] Colors =
    [
        ("white", "White", "#FFFFFFFF"),
        ("yellow", "Yellow", "#FFFFE14D"),
        ("cyan", "Cyan", "#FF7FE8FF"),
        ("green", "Green", "#FF8CF08C"),
    ];

    public static readonly (string Id, string Label)[] Styles =
    [
        ("outline", "Outline"),
        ("shadow", "Drop shadow"),
        ("box", "Dark box"),
    ];

    public static Dictionary<string, string> Options(int scale, string color, string style, int position, bool bold)
    {
        var inv = CultureInfo.InvariantCulture;
        var c = Colors.FirstOrDefault(x => x.Id == color).Color ?? Colors[0].Color;
        var o = new Dictionary<string, string>
        {
            ["sub-scale"] = (Math.Clamp(scale, 50, 200) / 100.0).ToString("0.00", inv),
            ["sub-color"] = c,
            ["sub-bold"] = bold ? "yes" : "no",
            ["sub-pos"] = Math.Clamp(position, 50, 100).ToString(inv),
            ["sub-font-size"] = "46",
        };
        switch (style)
        {
            case "box":
                o["sub-border-style"] = "opaque-box";
                o["sub-back-color"] = "#B0000000";
                o["sub-outline-size"] = "6";
                o["sub-shadow-offset"] = "0";
                break;
            case "shadow":
                o["sub-border-style"] = "outline-and-shadow";
                o["sub-outline-size"] = "0.8";
                o["sub-outline-color"] = "#C0000000";
                o["sub-shadow-offset"] = "2.5";
                o["sub-shadow-color"] = "#C0000000";
                o["sub-back-color"] = "#00000000";
                break;
            default:
                o["sub-border-style"] = "outline-and-shadow";
                o["sub-outline-size"] = "2.2";
                o["sub-outline-color"] = "#FF000000";
                o["sub-shadow-offset"] = "0.8";
                o["sub-shadow-color"] = "#80000000";
                o["sub-back-color"] = "#00000000";
                break;
        }
        return o;
    }
}
