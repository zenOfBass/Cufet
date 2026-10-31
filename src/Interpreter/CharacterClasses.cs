namespace Cufet.Interpreter;

/// <summary>
/// The one implementation of <c>is alphabetic</c> / <c>is numeric</c> / <c>is whitespace</c> and
/// <c>the code point of</c>, shared by both backends.
/// </summary>
/// <remarks>
/// <para>
/// ★ The classes come from <see cref="CharacterClassData"/>, the table the compiled runtime is
/// emitted from too, for the reason <see cref="CaseTable"/> exists: two backends asking two
/// sources would agree only until one of them was upgraded.
/// </para>
/// <para>
/// The unit is the code point, matching <c>TextPositions</c> and the compiled <c>cufet_u8_len</c>.
/// A text is in a class when EVERY character is, and empty text is in none — `"" is numeric` is
/// false, as `"" converted to number` is void. An unpaired surrogate is in no class.
/// </para>
/// </remarks>
public static class CharacterClasses
{
    /// <summary>Whether every character of the text is a letter.</summary>
    public static bool IsAlphabetic(string text) => All(CharacterClassData.AlphabeticRanges, text);

    /// <summary>Whether every character of the text is a decimal digit.</summary>
    public static bool IsNumeric(string text) => All(CharacterClassData.NumericRanges, text);

    /// <summary>Whether every character of the text is whitespace.</summary>
    public static bool IsWhitespace(string text) => All(CharacterClassData.WhitespaceRanges, text);

    /// <summary>The code point of a one-character text, or null when it is not exactly one.</summary>
    public static int? CodePoint(string text)
    {
        if (text.Length == 1 && !char.IsSurrogate(text[0])) return text[0];
        if (text.Length == 2 && char.IsSurrogatePair(text, 0)) return char.ConvertToUtf32(text, 0);
        return null;
    }

    private static bool All(int[] ranges, string text)
    {
        if (text.Length == 0) return false;
        for (int i = 0; i < text.Length;)
        {
            if (char.IsSurrogate(text[i]) && !char.IsSurrogatePair(text, i)) return false;
            bool pair = char.IsSurrogatePair(text, i);
            if (!In(ranges, char.ConvertToUtf32(text, i))) return false;
            i += pair ? 2 : 1;
        }
        return true;
    }

    // Ranges are first/last pairs, sorted and disjoint, so the last one starting at or before
    // `codePoint` is the only one that could hold it.
    private static bool In(int[] ranges, int codePoint)
    {
        int lo = 0, hi = ranges.Length / 2 - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (ranges[mid * 2] <= codePoint) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found >= 0 && codePoint <= ranges[found * 2 + 1];
    }
}
