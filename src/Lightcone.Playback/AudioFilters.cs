using System.Globalization;

namespace Lightcone.Playback;

/// <summary>
/// mpv audio filter chains for Night mode (a compressor: loud scenes come down, quiet dialogue
/// comes up) and Clear voices (the centre channel, where dialogue lives, mixed up front for
/// surround tracks; a presence boost for stereo ones), plus the Media volume gain. A limiter keeps
/// any of them from clipping.
/// </summary>
public static class AudioFilters
{
    private const string Night = "acompressor=threshold=0.05:ratio=6:attack=5:release=300:makeup=4:knee=4";
    private const string Limiter = "alimiter=limit=0.9:level=disabled";

    /// <summary>Surround (5.1, 7.1, ...) to stereo with the dialogue channel lifted.</summary>
    private const string VoicesSurround =
        "aformat=channel_layouts=5.1,pan=stereo|FL=0.55*FL+1.1*FC+0.35*BL+0.25*LFE|FR=0.55*FR+1.1*FC+0.35*BR+0.25*LFE,equalizer=f=2800:t=q:w=1.2:g=3";

    /// <summary>Stereo: speech frequencies up, rumble down.</summary>
    private const string VoicesStereo = "equalizer=f=2800:t=q:w=1.2:g=5,equalizer=f=150:t=q:w=1:g=-3";

    /// <summary>Media volume choices in dB (0 = as mixed).</summary>
    public static readonly int[] GainSteps = [-12, -9, -6, -3, 0, 3, 6, 9, 12];

    /// <summary>The nearest allowed Media volume step to <paramref name="db"/>.</summary>
    public static int SnapGain(int db) => GainSteps.OrderBy(s => Math.Abs(s - db)).First();

    /// <summary>"0 dB (as mixed)", "+6 dB", "−6 dB".</summary>
    public static string GainLabel(int db) => db switch
    {
        0 => "0 dB, as mixed",
        > 0 => $"+{db} dB",
        _ => $"\u2212{-db} dB",
    };

    /// <summary>The value for mpv's "af" property ("" = no filters).</summary>
    /// <param name="channels">Channels of the playing audio track, when known.</param>
    /// <param name="gainDb">Media volume: extra gain (or cut) in dB, applied after the other filters.</param>
    public static string Build(bool night, bool voices, int? channels, int gainDb = 0)
    {
        var parts = new List<string>();
        if (voices)
        {
            parts.Add(channels is > 2 ? VoicesSurround : VoicesStereo);
        }
        if (night)
        {
            parts.Add(Night);
        }
        gainDb = Math.Clamp(gainDb, -24, 18);
        if (gainDb != 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"volume={gainDb}dB"));
        }
        if (parts.Count == 0)
        {
            return "";
        }
        if (night || voices || gainDb > 0)
        {
            parts.Add(Limiter);
        }
        return string.Create(CultureInfo.InvariantCulture, $"lavfi=[{string.Join(',', parts)}]");
    }
}
