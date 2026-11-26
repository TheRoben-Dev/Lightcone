using System.Text.Json;

namespace Lightcone.Playback;

/// <summary>
/// The audio and video formats Lightcone promises to play, checked against what the loaded
/// libmpv's FFmpeg can decode (mpv's decoder-list). `lightcone probe` and the tests fail when one
/// is missing, so a slimmed-down bundled build can't silently drop, say, TrueHD.
/// </summary>
public static class Codecs
{
    /// <summary>
    /// FFmpeg codec names, grouped for the report. mpv's decoder-list holds audio and video only;
    /// subtitles (SRT, ASS, PGS, VobSub...) go through FFmpeg too and the bundled build keeps every
    /// decoder, so text subtitles are checked by playing them (tests) and PGS on real files (bench).
    /// </summary>
    public static readonly IReadOnlyList<(string Group, string[] Codecs)> Required =
    [
        ("Video", ["h264", "hevc", "av1", "vp9", "vp8", "mpeg2video", "mpeg4", "vc1"]),
        ("Audio", ["aac", "ac3", "eac3", "dts", "truehd", "flac", "opus", "vorbis", "mp3", "alac", "pcm_s16le", "pcm_s24le"]),
    ];

    /// <summary>A codec's everyday name ("hevc" is "HEVC", the PCM variants are all "PCM").</summary>
    public static string Display(string codec) => codec switch
    {
        "h264" => "H.264",
        "hevc" => "HEVC",
        "mpeg2video" => "MPEG-2",
        "mpeg4" => "MPEG-4",
        "vc1" => "VC-1",
        "eac3" => "E-AC-3",
        "truehd" => "TrueHD",
        "vorbis" => "Vorbis",
        "opus" => "Opus",
        "alac" => "ALAC",
        _ when codec.StartsWith("pcm_", StringComparison.Ordinal) => "PCM",
        _ => codec.ToUpperInvariant(),
    };

    /// <summary>Codec names in mpv's decoder-list JSON (property "decoder-list").</summary>
    public static HashSet<string> Available(string? decoderListJson)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(decoderListJson))
        {
            return set;
        }
        using var doc = JsonDocument.Parse(decoderListJson);
        foreach (var d in doc.RootElement.EnumerateArray())
        {
            if (d.TryGetProperty("codec", out var c) && c.GetString() is { } name)
            {
                set.Add(name);
            }
        }
        return set;
    }

    private static IReadOnlyList<string>? _loadedMissing;

    /// <summary>
    /// Required codecs the loaded libmpv lacks, checked once with a throwaway player (no video or
    /// audio output). Null when libmpv doesn't load.
    /// </summary>
    public static IReadOnlyList<string>? MissingInLoadedLibrary()
    {
        if (_loadedMissing == null && MpvLibrary.TryLoad(out _))
        {
            using var probe = new MpvPlayer(new Dictionary<string, string> { ["vo"] = "null", ["ao"] = "null" });
            _loadedMissing = Missing(probe.GetString("decoder-list"));
        }
        return _loadedMissing;
    }

    /// <summary>Required codecs the decoder list lacks (empty: all there).</summary>
    public static IReadOnlyList<string> Missing(string? decoderListJson)
    {
        var have = Available(decoderListJson);
        return Required.SelectMany(g => g.Codecs).Where(c => !have.Contains(c)).ToList();
    }
}
