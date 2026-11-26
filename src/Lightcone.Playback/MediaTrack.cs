using System.Text.Json;

namespace Lightcone.Playback;

public enum TrackKind
{
    Video,
    Audio,
    Subtitle,
}

/// <summary>One entry of mpv's track-list.</summary>
public sealed record MediaTrack(int Id, TrackKind Kind, string? Language, string? Title, string? Codec, bool Default, bool Forced, bool External, bool Selected, int? Channels = null)
{
    /// <summary>What the track picker shows: "English · AC-3 5.1 · Commentary".</summary>
    public string Label
    {
        get
        {
            var parts = new List<string>();
            parts.Add(Languages.Name(Language) ?? (Kind == TrackKind.Subtitle ? "Subtitles" : $"Track {Id}"));
            if (!string.IsNullOrEmpty(Codec))
            {
                var codec = CodecName(Codec);
                parts.Add(Channels is { } ch and > 0 ? $"{codec} {ChannelLayout(ch)}" : codec);
            }
            if (!string.IsNullOrWhiteSpace(Title) && !string.Equals(Title, Languages.Name(Language), StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(Title!);
            }
            if (Forced)
            {
                parts.Add("Forced");
            }
            if (External)
            {
                parts.Add("External");
            }
            return string.Join(" · ", parts);
        }
    }

    private static string CodecName(string codec) => codec switch
    {
        "subrip" => "SRT",
        "ass" => "ASS",
        "hdmv_pgs_subtitle" => "PGS",
        "dvd_subtitle" => "VobSub",
        "webvtt" => "WebVTT",
        "mov_text" => "Text",
        "ac3" => "AC-3",
        "eac3" => "E-AC-3",
        "truehd" => "TrueHD",
        "dts" => "DTS",
        "aac" => "AAC",
        "opus" => "Opus",
        "flac" => "FLAC",
        "mp3" => "MP3",
        "vorbis" => "Vorbis",
        "h264" => "H.264",
        "hevc" => "HEVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        _ => codec.ToUpperInvariant(),
    };

    private static string ChannelLayout(int ch) => ch switch
    {
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        8 => "7.1",
        _ => $"{ch}ch",
    };

    /// <summary>Parses mpv's track-list property (JSON, as mpv_get_property_string returns it).</summary>
    public static IReadOnlyList<MediaTrack> ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        var list = new List<MediaTrack>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                TrackKind? kind = Str(t, "type") switch
                {
                    "video" => TrackKind.Video,
                    "audio" => TrackKind.Audio,
                    "sub" => TrackKind.Subtitle,
                    _ => null,
                };
                if (kind == null || !t.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }
                int? channels = t.TryGetProperty("demux-channel-count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
                list.Add(new MediaTrack(id.GetInt32(), kind.Value, Str(t, "lang"), Str(t, "title"), Str(t, "codec"),
                    Bool(t, "default"), Bool(t, "forced"), Bool(t, "external"), Bool(t, "selected"), channels));
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Display names for the language codes media files use (ISO 639-1/-2).</summary>
public static class Languages
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["eng"] = "English",
        ["ja"] = "Japanese", ["jpn"] = "Japanese",
        ["sv"] = "Swedish", ["swe"] = "Swedish",
        ["de"] = "German", ["ger"] = "German", ["deu"] = "German",
        ["fr"] = "French", ["fre"] = "French", ["fra"] = "French",
        ["es"] = "Spanish", ["spa"] = "Spanish",
        ["it"] = "Italian", ["ita"] = "Italian",
        ["pt"] = "Portuguese", ["por"] = "Portuguese",
        ["ru"] = "Russian", ["rus"] = "Russian",
        ["zh"] = "Chinese", ["chi"] = "Chinese", ["zho"] = "Chinese",
        ["ko"] = "Korean", ["kor"] = "Korean",
        ["nl"] = "Dutch", ["dut"] = "Dutch", ["nld"] = "Dutch",
        ["no"] = "Norwegian", ["nor"] = "Norwegian", ["nb"] = "Norwegian", ["nob"] = "Norwegian",
        ["da"] = "Danish", ["dan"] = "Danish",
        ["fi"] = "Finnish", ["fin"] = "Finnish",
        ["pl"] = "Polish", ["pol"] = "Polish",
        ["ar"] = "Arabic", ["ara"] = "Arabic",
        ["hi"] = "Hindi", ["hin"] = "Hindi",
        ["und"] = "Unknown",
    };

    public static string? Name(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : Names.TryGetValue(code, out var n) ? n : code;
}
