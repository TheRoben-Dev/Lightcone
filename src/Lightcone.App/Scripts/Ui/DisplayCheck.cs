using Godot;

namespace Lightcone.App.Ui;

/// <summary>
/// What the app is really drawing at, and whether Steam is squeezing it. In Game Mode, gamescope
/// gives each game a resolution; when a Steam Deck (16:10 panel) runs Lightcone at a 16:9 one, the
/// whole UI sits in a box with black bars above and below. That's a Steam setting (Properties →
/// Game resolution), which the app can only point out.
/// </summary>
public static class DisplayCheck
{
    private static bool? _deck;

    /// <summary>A Steam Deck (LCD "Jupiter" or OLED "Galileo"), from the firmware's product name.</summary>
    public static bool IsSteamDeck
    {
        get
        {
            if (_deck is { } d)
            {
                return d;
            }
            try
            {
                var name = OperatingSystem.IsLinux() && File.Exists("/sys/devices/virtual/dmi/id/product_name")
                    ? File.ReadAllText("/sys/devices/virtual/dmi/id/product_name").Trim()
                    : "";
                _deck = name is "Jupiter" or "Galileo";
            }
            catch (IOException)
            {
                _deck = false;
            }
            catch (UnauthorizedAccessException)
            {
                _deck = false;
            }
            return _deck.Value;
        }
    }

    public static Vector2I Window => DisplayServer.WindowGetSize();

    public static Vector2I Screen => DisplayServer.ScreenGetSize();

    /// <summary>"1280×800 window on a 1280×800 screen, 60 Hz".</summary>
    public static string Describe()
    {
        var w = Window;
        var s = Screen;
        var hz = DisplayServer.ScreenGetRefreshRate();
        return $"{w.X}×{w.Y} window on a {s.X}×{s.Y} screen" + (hz > 0 ? $", {hz:0} Hz" : "");
    }

    /// <summary>
    /// A Deck in Game Mode given a 16:9 resolution (black bars above and below): what to change,
    /// or null when the picture already fills the screen.
    /// </summary>
    public static string? SteamResolutionTip()
    {
        if (!IsSteamDeck || !Lightcone.Host.SteamSetup.InGameMode)
        {
            return null;
        }
        var s = Screen;
        if (s.Y <= 0)
        {
            return null;
        }
        var aspect = s.X / (float)s.Y;
        return aspect > 1.7f
            ? $"Steam runs Lightcone at {s.X}×{s.Y} (16:9), so it can't fill the Deck's 16:10 screen. In Steam, open Lightcone's Properties → General → Game resolution and pick Native (1280×800)."
            : null;
    }
}
