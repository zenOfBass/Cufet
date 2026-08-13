using System.Text;

namespace Cufet.Interpreter;

/// <summary>
/// The one implementation of <c>in uppercase</c> / <c>in lowercase</c>, shared by both backends.
/// </summary>
/// <remarks>
/// <para>
/// ★ This exists so the two backends cannot disagree. Casing used to be implemented twice — the
/// interpreter called .NET's <c>ToUpperInvariant</c>, the compiled runtime called C's
/// <c>toupper()</c> once per byte — so <c>"héllo" in uppercase</c> was <c>HÉLLO</c> interpreted and
/// <c>HéLLO</c> compiled. Generating the C table from .NET would have made them agree only at the
/// moment of generation, because the interpreter would still be asking ICU at run time; a newer
/// Unicode version arriving with a .NET upgrade would silently reopen the gap. Reading one table
/// makes the drift impossible instead of detectable, and pins Cufet's casing to a stated Unicode
/// version rather than to whichever .NET is installed.
/// </para>
/// <para>
/// The mapping is 1:1 for every code point — this is SIMPLE case mapping, so <c>ß</c> stays
/// <c>ß</c>, the <c>ﬁ</c> ligature stays <c>ﬁ</c>, and invariant culture leaves the Turkish pair
/// <c>ı</c>/<c>İ</c> alone rather than picking a side. Nothing ever changes length in code points,
/// which is why the compiled side can size its output buffer without a second pass.
/// </para>
/// <para>
/// The unit is the code point, matching <c>TextPositions</c> and the compiled <c>cufet_u8_len</c>:
/// a surrogate pair is one character to case, as it is one character to count.
/// </para>
/// </remarks>
public static class CaseTable
{
    /// <summary>The .NET version whose invariant casing the table was generated from.</summary>
    public static string SourceRuntime => CaseTableData.SourceRuntime;

    /// <summary>Maps one code point to its uppercase form, or back to itself when it has none.</summary>
    public static int MapUpper(int codePoint) => Map(CaseTableData.UpperRuns, codePoint);

    /// <summary>Maps one code point to its lowercase form, or back to itself when it has none.</summary>
    public static int MapLower(int codePoint) => Map(CaseTableData.LowerRuns, codePoint);

    /// <summary>The text with every character replaced by its uppercase form.</summary>
    public static string ToUpper(string text) => MapAll(CaseTableData.UpperRuns, text);

    /// <summary>The text with every character replaced by its lowercase form.</summary>
    public static string ToLower(string text) => MapAll(CaseTableData.LowerRuns, text);

    // Each run is start/count/stride/delta, flattened four ints at a time. Runs are sorted by start
    // and — this is what makes the search a plain one — their spans never overlap, so the last run
    // beginning at or before `codePoint` is the only one that could contain it. The generator
    // refuses to extend a stride-2 run across a code point that has a mapping of its own precisely
    // to keep that true; without it a later run could start inside an earlier run's span and the
    // search would land on the wrong side of the interleaving.
    private static int Map(int[] runs, int codePoint)
    {
        int lo = 0, hi = runs.Length / 4 - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (runs[mid * 4] <= codePoint) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return codePoint;

        int start = runs[found * 4], count = runs[found * 4 + 1];
        int stride = runs[found * 4 + 2], delta = runs[found * 4 + 3];
        int offset = codePoint - start;
        if (offset > (count - 1) * stride || offset % stride != 0) return codePoint;
        return codePoint + delta;
    }

    private static string MapAll(int[] runs, string text)
    {
        var built = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length;)
        {
            // The ASCII path is the overwhelmingly common one and needs neither the surrogate check
            // nor the binary search, so it is worth taking before either.
            char c = text[i];
            if (c < 0x80)
            {
                built.Append((char)Map(runs, c));
                i++;
                continue;
            }

            // An UNPAIRED surrogate is passed through untouched. It cannot survive a round trip
            // through UTF-8 so it should never reach here from a source file, but a string is free
            // to hold one and ConvertToUtf32 would throw on it — and casing is not the place to
            // start rejecting text that every other operation accepts.
            if (char.IsSurrogate(c) && !char.IsSurrogatePair(text, i))
            {
                built.Append(c);
                i++;
                continue;
            }

            bool pair = char.IsSurrogatePair(text, i);
            built.Append(char.ConvertFromUtf32(Map(runs, char.ConvertToUtf32(text, i))));
            i += pair ? 2 : 1;
        }
        return built.ToString();
    }
}
