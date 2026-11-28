using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Lightcone.Host.Library;

/// <summary>A subtitle fetch that failed, with a message meant for the user.</summary>
public sealed class SubtitleException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>A user-facing reason for an HTTP failure talking to <paramref name="server"/>.</summary>
    public static SubtitleException FromHttp(HttpRequestException e, string server, string doing) => new(e.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            $"{server} refused to {doing} ({(int)e.StatusCode!}). This account may not be allowed to add subtitles on that server.",
        HttpStatusCode.NotFound => $"{server} couldn't find that subtitle any more (404). Try another one.",
        HttpStatusCode.TooManyRequests => $"{server} says too many subtitle requests (429). Wait a minute and try again.",
        { } code when (int)code >= 500 =>
            $"{server} failed to {doing} ({(int)code}). OpenSubtitles may be down or out of downloads for today.",
        { } code => $"{server} answered {(int)code} when asked to {doing}.",
        null => $"Couldn't reach {server} to {doing}: {e.Message}",
    }, e);
}

/// <summary>
/// Turns a downloaded subtitle into a file mpv can load: unpacks gzip/zip, converts the text to
/// UTF-8 (OpenSubtitles has plenty of Windows-1252 and UTF-16 files, and this libmpv has no
/// charset guessing), works out the format and saves it with the matching extension.
/// </summary>
public static class SubtitleFiles
{
    private static readonly string[] Extensions = [".srt", ".ass", ".ssa", ".vtt", ".sub", ".smi", ".txt"];

    /// <summary>Where fetched subtitles are kept for the session.</summary>
    public static string Dir => Path.Combine(Path.GetTempPath(), "lightcone-subtitles");

    /// <summary>Saves <paramref name="data"/> as a playable subtitle file and returns its path.</summary>
    public static string Save(byte[] data, string key, string? nameHint = null)
    {
        var (text, ext) = Normalize(data, nameHint);
        Directory.CreateDirectory(Dir);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        var file = Path.Combine(Dir, $"{hash}{ext}");
        File.WriteAllText(file, text, new UTF8Encoding(false));
        return file;
    }

    /// <summary>The subtitle's text (UTF-8 ready) and file extension, or a <see cref="SubtitleException"/>.</summary>
    public static (string Text, string Ext) Normalize(byte[] data, string? nameHint = null)
    {
        if (data.Length == 0)
        {
            throw new SubtitleException("The server sent an empty subtitle file. Try another one.");
        }
        data = Unpack(data, ref nameHint);
        var text = Decode(data).Replace("\0", "", StringComparison.Ordinal);
        var ext = Format(text, nameHint) ?? throw new SubtitleException(LooksLikeHtml(text)
            ? "The server sent a web page instead of a subtitle (it may need you to sign in again)."
            : "That download isn't a subtitle file mpv can read. Try another one.");
        return (text, ext);
    }

    private static byte[] Unpack(byte[] data, ref string? nameHint)
    {
        if (data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b)
        {
            using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            return output.ToArray();
        }
        if (data.Length > 4 && data[0] == (byte)'P' && data[1] == (byte)'K' && data[2] == 3 && data[3] == 4)
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            var entry = zip.Entries.Where(e => e.Length > 0)
                .OrderBy(e => Array.IndexOf(Extensions, Path.GetExtension(e.Name).ToLowerInvariant()) is var i and >= 0 ? i : 99)
                .ThenByDescending(e => e.Length).FirstOrDefault()
                ?? throw new SubtitleException("The subtitle archive was empty. Try another one.");
            nameHint = entry.Name;
            using var s = entry.Open();
            using var output = new MemoryStream();
            s.CopyTo(output);
            return output.ToArray();
        }
        return data;
    }

    /// <summary>UTF-8 (with or without BOM), UTF-16 by BOM or by its zero bytes, else Windows-1252.</summary>
    internal static string Decode(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xef && data[1] == 0xbb && data[2] == 0xbf)
        {
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        }
        if (data.Length >= 2 && data[0] == 0xff && data[1] == 0xfe)
        {
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        }
        if (data.Length >= 2 && data[0] == 0xfe && data[1] == 0xff)
        {
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        }
        if (data.Length >= 4 && data[1] == 0 && data[3] == 0 && data[0] != 0)
        {
            return Encoding.Unicode.GetString(data);
        }
        if (data.Length >= 4 && data[0] == 0 && data[2] == 0 && data[1] != 0)
        {
            return Encoding.BigEndianUnicode.GetString(data);
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Cp1252.GetString(data);
        }
    }

    private static readonly Encoding Cp1252 = CreateCp1252();

    private static Encoding CreateCp1252()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    private static string? Format(string text, string? nameHint)
    {
        var head = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (head.StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            return ".vtt";
        }
        if (head.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase) || text.Contains("[Events]", StringComparison.OrdinalIgnoreCase))
        {
            return ".ass";
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}\s*-->\s*\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}"))
        {
            return ".srt";
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(head, @"^\{\d+\}\{\d*\}"))
        {
            return ".sub";
        }
        if (head.StartsWith("<SAMI", StringComparison.OrdinalIgnoreCase))
        {
            return ".smi";
        }
        if (LooksLikeHtml(text))
        {
            return null;
        }
        var hinted = Path.GetExtension(nameHint ?? "").ToLowerInvariant();
        return hinted is ".srt" or ".ass" or ".ssa" or ".vtt" or ".sub" ? hinted : null;
    }

    private static bool LooksLikeHtml(string text)
    {
        var head = text.TrimStart().AsSpan(0, Math.Min(200, text.TrimStart().Length));
        return head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || head.StartsWith("{", StringComparison.Ordinal);
    }
}
