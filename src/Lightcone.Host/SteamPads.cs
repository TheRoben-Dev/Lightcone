namespace Lightcone.Host;

/// <summary>
/// What controller is really behind a Steam Virtual Gamepad. With Steam Input on, every pad shows
/// up as "Steam Virtual Gamepad" (an Xbox layout); Steam writes the real type of each slot to the
/// file named by the SteamVirtualGamepadInfo environment variable (the same file SDL reads).
/// </summary>
public static class SteamPads
{
    /// <summary>The glyph family of a controller type or name: "playstation", "nintendo", "deck" or "xbox".</summary>
    public static string FamilyOf(string typeOrName)
    {
        var t = typeOrName.ToLowerInvariant().Replace(" ", "");
        if (t.Contains("ps3") || t.Contains("ps4") || t.Contains("ps5") || t.Contains("playstation") || t.Contains("dualsense") || t.Contains("dualshock"))
        {
            return "playstation";
        }
        if (t.Contains("switch") || t.Contains("joycon") || t.Contains("nintendo") || t.Contains("procontroller"))
        {
            return "nintendo";
        }
        if (t.Contains("steamdeck") || t.Contains("neptune"))
        {
            return "deck";
        }
        return "xbox";
    }

    /// <summary>The family by USB vendor (Sony, Nintendo, Valve's Deck controller), or null.</summary>
    public static string? FamilyOfVendor(int vendor, int product) => (vendor, product) switch
    {
        (0x054c, _) => "playstation",
        (0x057e, _) => "nintendo",
        (0x28de, 0x1205) => "deck",
        _ => null,
    };

    /// <summary>Each virtual slot's type from Steam's info file ("[slot 0]" sections with type= / VID= / PID=).</summary>
    public static IReadOnlyList<string> ParseInfo(string text)
    {
        var types = new List<string>();
        string? type = null;
        int vid = 0, pid = 0;
        void Flush()
        {
            if (type != null || vid != 0)
            {
                types.Add(type is { Length: > 0 } t && FamilyOf(t) != "xbox" ? FamilyOf(t) : FamilyOfVendor(vid, pid) ?? FamilyOf(type ?? ""));
            }
            type = null;
            vid = pid = 0;
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                Flush();
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var key = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "type":
                    type = value;
                    break;
                case "vid":
                    vid = Hex(value);
                    break;
                case "pid":
                    pid = Hex(value);
                    break;
            }
        }
        Flush();
        return types;
    }

    private static int Hex(string v)
    {
        v = v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? v[2..] : v;
        return int.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out var n) ? n : 0;
    }

    /// <summary>The real families of Steam's virtual pads, or empty outside Steam Input.</summary>
    public static IReadOnlyList<string> VirtualPads()
    {
        try
        {
            return Environment.GetEnvironmentVariable("SteamVirtualGamepadInfo") is { Length: > 0 } f && File.Exists(f) ? ParseInfo(File.ReadAllText(f)) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
