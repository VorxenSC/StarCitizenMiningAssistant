using System.Text;
using System.Text.RegularExpressions;

namespace MiningAssistant.Core.Ocr;

/// <summary>An integer recovered from an OCR token.</summary>
public sealed record NumberToken(
    string Raw,
    int Value,
    /// <summary>How many confusable letters were mapped to digits (0 = clean read).</summary>
    int Substitutions,
    /// <summary>Line index within the OCR text.</summary>
    int Line,
    /// <summary>True when the token is on a line/position labelled "RS" / "SIGNATURE".</summary>
    bool HasRsLabel);

public sealed class NumberNormalizerOptions
{
    /// <summary>Maximum letters that may be mapped to digits in one token ("358S" = 1).</summary>
    public int MaxSubstitutions { get; set; } = 2;

    /// <summary>Minimum real digits a token must contain before any substitution is attempted.</summary>
    public int MinRealDigits { get; set; } = 2;
}

/// <summary>
/// Conservative OCR → integer normalization for HUD numbers.
/// Maps only well-known confusables (O→0, I/l→1, S→5, B→8, Z→2) and only inside tokens that
/// are already mostly digits, so words like "RS" or "MASS" are never turned into numbers.
/// </summary>
public sealed class OcrNumberNormalizer
{
    private static readonly Dictionary<char, char> Confusables = new()
    {
        ['O'] = '0', ['o'] = '0', ['Q'] = '0', ['D'] = '0', ['ø'] = '0', ['Ø'] = '0', ['θ'] = '0',
        ['I'] = '1', ['l'] = '1', ['|'] = '1', ['i'] = '1',
        ['S'] = '5', ['s'] = '5',
        ['B'] = '8',
        ['Z'] = '2', ['z'] = '2',
    };

    private static readonly Regex RsLabel = new(@"^(RS|R5|RADAR\s*SIG(NATURE)?|SIGNATURE|SIG)\b[:\-=]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AttachedRsPrefix = new(@"^(RS|R5)[:\-=]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ThousandsGrouped = new(@"^\d{1,3}([,.'’]\d{3})+$", RegexOptions.Compiled);
    private static readonly Regex Separators = new(@"[,.'’]", RegexOptions.Compiled);
    private static readonly char[] TrimChars = ":;()[]{}<>\"“”«»-–—=+*#~_/\\!?".ToCharArray();

    public NumberNormalizerOptions Options { get; }

    public OcrNumberNormalizer(NumberNormalizerOptions? options = null) => Options = options ?? new();

    /// <summary>Extracts every plausible integer token from multi-line OCR text.</summary>
    public IReadOnlyList<NumberToken> ExtractNumbers(string text)
    {
        var result = new List<NumberToken>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var lines = text.Replace("\r", "").Split('\n');
        for (var li = 0; li < lines.Length; li++)
        {
            var line = lines[li].Trim();
            if (line.Length == 0) continue;
            var lineHasLabel = RsLabel.IsMatch(line) || line.Contains(" RS ", StringComparison.OrdinalIgnoreCase);

            var words = MergeSpacedThousands(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            for (var wi = 0; wi < words.Count; wi++)
            {
                var word = words[wi];
                var labelled = lineHasLabel;
                if (AttachedRsPrefix.IsMatch(word))
                {
                    word = AttachedRsPrefix.Replace(word, "");
                    labelled = true;
                }
                if (TryNormalize(word, out var value, out var subs))
                    result.Add(new NumberToken(words[wi], value, subs, li, labelled));
            }
        }
        return result;
    }

    /// <summary>Normalizes a single token. Returns false for anything that is not confidently a number.</summary>
    public bool TryNormalize(string token, out int value, out int substitutions)
    {
        value = 0;
        substitutions = 0;
        var t = token.Trim().Trim(TrimChars);
        if (t.Length == 0) return false;

        var sb = new StringBuilder(t.Length);
        var realDigits = 0;
        foreach (var c in t)
        {
            if (char.IsAsciiDigit(c)) { sb.Append(c); realDigits++; }
            else if (Confusables.TryGetValue(c, out var d)) { sb.Append(d); substitutions++; }
            else if (c is ',' or '.' or '\'' or '’') sb.Append(c);
            else return false; // any other letter → it's a word, not a number
        }

        if (realDigits < Options.MinRealDigits) return false;
        if (substitutions > Options.MaxSubstitutions || substitutions >= realDigits) return false;

        var s = sb.ToString();
        if (ThousandsGrouped.IsMatch(s)) s = Separators.Replace(s, "");
        else if (Separators.IsMatch(s)) return false; // decimals like "1.72" are not RS values
        if (s.Length > 9) return false;

        return int.TryParse(s, out value);
    }

    /// <summary>"14 340" → "14340" when OCR splits a grouped number on a space.</summary>
    private static List<string> MergeSpacedThousands(string[] words)
    {
        var list = new List<string>(words.Length);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (i + 1 < words.Length && Regex.IsMatch(w, @"^\d{1,3}$") && Regex.IsMatch(words[i + 1], @"^\d{3}$"))
            {
                list.Add(w + words[i + 1]);
                i++;
                continue;
            }
            list.Add(w);
        }
        return list;
    }
}
