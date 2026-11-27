using System.Globalization;

namespace Lightcone.Playback;

/// <summary>
/// mpv audio filter chains for Night mode (a compressor: loud scenes come down, quiet dialogue
/// comes up) and Clear voices (the centre channel, where dialogue lives, mixed up front for
/// surround tracks; a presence boost for stereo ones). A limiter keeps either from clipping.
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

    /// <summary>The value for mpv's "af" property ("" = no filters).</summary>
    /// <param name="channels">Channels of the playing audio track, when known.</param>
    public static string Build(bool night, bool voices, int? channels)
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
        if (parts.Count == 0)
        {
            return "";
        }
        parts.Add(Limiter);
        return string.Create(CultureInfo.InvariantCulture, $"lavfi=[{string.Join(',', parts)}]");
    }
}
