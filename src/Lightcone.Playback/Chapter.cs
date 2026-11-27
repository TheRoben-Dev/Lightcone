using System.Globalization;
using System.Text.Json;

namespace Lightcone.Playback;

/// <summary>A chapter of what's playing (from the file, via mpv's chapter-list).</summary>
public sealed record Chapter(int Index, string Title, double Start)
{
    /// <summary>Parses mpv's chapter-list JSON; untitled chapters are "Chapter N".</summary>
    public static IReadOnlyList<Chapter> ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var list = new List<Chapter>();
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                if (!c.TryGetProperty("time", out var t) || t.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }
                var title = c.TryGetProperty("title", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } s ? s : null;
                var i = list.Count;
                list.Add(new Chapter(i, title ?? string.Create(CultureInfo.InvariantCulture, $"Chapter {i + 1}"), Math.Max(0, t.GetDouble())));
            }
            return list.OrderBy(c => c.Start).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The chapter playing at <paramref name="position"/>, or null before the first.</summary>
    public static Chapter? At(IReadOnlyList<Chapter> chapters, double position) =>
        chapters.LastOrDefault(c => c.Start <= position + 0.5);
}
