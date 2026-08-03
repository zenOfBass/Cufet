namespace Cufet.Interpreter;

// Character positions in Cufet text, counted in UNICODE CODE POINTS.
//
// ★ WHY THIS EXISTS. `text` is stored as a .NET string, whose natural unit is the UTF-16 code
// unit — and a code unit is not a character. "👍" is one character and two code units, so
// `the length of "👍"` answered 2 before this file existed. The native compiler stores text as
// UTF-8 bytes and answered 4. Both were wrong, and they were wrong differently, which is the
// shape of bug the no-divergence rule exists to forbid: the same program taking a different
// branch depending on which backend ran it.
//
// So the language picks ONE unit and both backends count in it. The C runtime does the same
// arithmetic over UTF-8 (a code point begins at every byte where `(b & 0xC0) != 0x80`); these
// functions are the UTF-16 half of that agreement.
//
// ★ WHY CODE POINTS AND NOT GRAPHEMES. A reader's idea of "one character" is really a grapheme
// cluster: "é" written as `e` + U+0301 is one thing on screen and two code points. Graphemes
// would be the truer answer, and they need the UAX #29 segmentation tables — a dependency this
// project is not going to carry into the C it emits. Code points are implementable identically
// on both sides with a dozen lines and no tables, and they are enormously closer to right than
// either unit that was in use. The cost is that combining marks count separately, which is
// written down rather than left to be discovered.
//
// Only the operations that COUNT or RETURN a position need this. Searching, splitting and
// replacing are already identical on both backends because UTF-8 is self-synchronising: the
// bytes of one character can never appear inside another, so a byte-wise substring search finds
// exactly what a character-wise one would.
public static class TextPositions
{
    // A code point is one char, except for a well-formed surrogate pair, which is two. A LONE
    // surrogate — possible in a .NET string, and not valid text — counts as one, so that broken
    // input still produces an answer rather than an exception.
    private static bool IsPairAt(string s, int i) =>
        char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);

    /// How many code points are in <paramref name="s"/>.
    public static int Length(string s)
    {
        int count = 0;
        for (int i = 0; i < s.Length; i++, count++)
            if (IsPairAt(s, i)) i++;
        return count;
    }

    /// The UTF-16 offset at which code point number <paramref name="index"/> begins, counting
    /// from zero. An index at or past the end returns the length, so callers can use it as an
    /// exclusive upper bound without a special case.
    public static int OffsetOf(string s, int index)
    {
        int i = 0;
        for (int n = 0; i < s.Length && n < index; n++, i++)
            if (IsPairAt(s, i)) i++;
        return i;
    }

    /// The code point index containing UTF-16 offset <paramref name="offset"/> — the inverse of
    /// <see cref="OffsetOf"/>, for turning a .NET search result back into a Cufet position.
    public static int IndexAt(string s, int offset)
    {
        int count = 0;
        for (int i = 0; i < s.Length && i < offset; i++, count++)
            if (IsPairAt(s, i)) i++;
        return count;
    }

    /// <paramref name="length"/> code points starting at code point <paramref name="start"/>.
    /// Both are clamped, so an over-long range yields what is there rather than throwing.
    public static string Slice(string s, int start, int length)
    {
        if (length <= 0) return "";
        int from = OffsetOf(s, start);
        int to   = OffsetOf(s, start + length);
        return s[from..to];
    }
}
