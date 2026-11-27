using Godot;

namespace Lightcone.App.Ui;

/// <summary>
/// Interface languages. English is written in the code; other languages are tables keyed by the
/// English text, plugged into Godot's TranslationServer so every Label, Button and tooltip is
/// translated as it's shown (no call sites to change). Texts built at run time ("Because you
/// watched …", "Downloads (3)") are matched by patterns with {0} in them. Anything without a
/// translation stays English.
/// </summary>
public static partial class Loc
{
    public static readonly (string Id, string Label)[] Choices = [("auto", "Same as the system"), ("en", "English"), ("sv", "Svenska")];

    private static bool _added;

    /// <summary>The language in use: "en" or "sv".</summary>
    public static string Current { get; private set; } = "en";

    /// <summary>Resolves "auto" to the system language and switches every text over.</summary>
    public static void Apply(string setting)
    {
        if (!_added)
        {
            _added = true;
            TranslationServer.AddTranslation(new TableTranslation("sv", Swedish.Table, Swedish.Patterns));
        }
        var lang = setting == "auto" ? (OS.GetLocaleLanguage() == "sv" ? "sv" : "en") : setting;
        Current = lang == "sv" ? "sv" : "en";
        TranslationServer.SetLocale(Current);
    }

    /// <summary>The translation of <paramref name="english"/> (for text drawn by hand or put together before display).</summary>
    public static string T(string english) => Current == "en" ? english : TranslationServer.Translate(english).ToString();

    /// <summary>Translated, then upper case (headings in the monospace themes).</summary>
    public static string Upper(string english) => T(english).ToUpperInvariant();

    /// <summary>A table with exact matches first, then "prefix {0} suffix" patterns ({t} in a translation = the argument, translated too).</summary>
    private sealed partial class TableTranslation : Translation
    {
        private readonly IReadOnlyDictionary<string, string> _table;
        private readonly (string Prefix, string Suffix, string To)[] _patterns;

        public TableTranslation(string locale, IReadOnlyDictionary<string, string> table, IReadOnlyDictionary<string, string> patterns)
        {
            Locale = locale;
            _table = table;
            _patterns = patterns.Select(p =>
            {
                var i = p.Key.IndexOf("{0}", StringComparison.Ordinal);
                return (p.Key[..i], p.Key[(i + 3)..], p.Value);
            }).OrderByDescending(p => p.Item1.Length + p.Item2.Length).ToArray();
        }

        public override StringName _GetMessage(StringName srcMessage, StringName context)
        {
            var s = srcMessage.ToString();
            return Lookup(s) is { } t ? new StringName(t) : new StringName();
        }

        private string? Lookup(string s)
        {
            if (s.Length == 0)
            {
                return null;
            }
            if (_table.TryGetValue(s, out var t))
            {
                return t;
            }
            // Help texts with a second line ("help\ndetail") translate line by line.
            if (s.Contains('\n'))
            {
                var lines = s.Split('\n');
                var parts = lines.Select(l => Lookup(l) ?? l).ToArray();
                return parts.SequenceEqual(lines) ? null : string.Join('\n', parts);
            }
            foreach (var (prefix, suffix, to) in _patterns)
            {
                if (s.Length > prefix.Length + suffix.Length && s.StartsWith(prefix, StringComparison.Ordinal) && s.EndsWith(suffix, StringComparison.Ordinal))
                {
                    var arg = s[prefix.Length..^suffix.Length];
                    return to.Contains("{t}") ? to.Replace("{t}", Lookup(arg) ?? arg) : to.Replace("{0}", arg);
                }
            }
            return null;
        }
    }
}
