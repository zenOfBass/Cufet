using System.Text;
using Cufet.Interpreter;
using Xunit;

namespace Cufet.Interpreter.Tests;

// The case table is the ONE implementation of `in uppercase` / `in lowercase`: the interpreter
// looks up through it and the compiler emits the same numbers into the generated C. So these tests
// are not about the interpreter agreeing with the compiler — that is true by construction now, and
// PipelineCaseTests only has to confirm the emitted C reads the table correctly. These are about
// the table itself being RIGHT, and about the three properties the compiled runtime is built on
// top of: the mapping is 1:1, it is not contextual, and it grows text by at most a byte a character.
public class CaseTableTests
{
    // ★ The drift detector, and the reason it is exhaustive rather than a sample. The table is
    // generated from .NET's invariant casing and then FROZEN, which is the point — a program's
    // meaning should not change because someone upgraded a runtime. But frozen is only defensible
    // if we know when the world moved, so this holds the table against live .NET and fails loudly
    // when a newer ICU disagrees. That failure is not a bug: it is the signal to run
    // `dotnet run tools/gen-case-table.cs`, read the diff, and decide whether to adopt it.
    [Fact]
    public void TheTable_MatchesDotNetInvariantCasing_ForEveryCodePoint()
    {
        var drifted = new List<string>();

        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp >= 0xD800 && cp <= 0xDFFF) continue;      // lone surrogates are not characters

            string one = char.ConvertFromUtf32(cp);
            int wantUpper = char.ConvertToUtf32(one.ToUpperInvariant(), 0);
            int wantLower = char.ConvertToUtf32(one.ToLowerInvariant(), 0);

            if (CaseTable.MapUpper(cp) != wantUpper)
                drifted.Add($"upper U+{cp:X4}: table says U+{CaseTable.MapUpper(cp):X4}, .NET says U+{wantUpper:X4}");
            if (CaseTable.MapLower(cp) != wantLower)
                drifted.Add($"lower U+{cp:X4}: table says U+{CaseTable.MapLower(cp):X4}, .NET says U+{wantLower:X4}");

            if (drifted.Count >= 20) break;
        }

        Assert.True(drifted.Count == 0,
            $"The case table no longer matches this .NET's invariant casing (table generated from "
            + $"{CaseTable.SourceRuntime}, running on {Environment.Version}). This is expected after a "
            + $"runtime upgrade that carries a newer Unicode version, and it does NOT mean the two "
            + $"backends disagree — they read the same table either way. Regenerate with "
            + $"`dotnet run tools/gen-case-table.cs`, review the diff, and update the changelog if you "
            + $"adopt it.\n  " + string.Join("\n  ", drifted));
    }

    // ★ The property the whole design rests on. Simple case mapping is 1:1, so `ß` stays `ß` and no
    // character ever becomes two. If that ever stopped being true, the compiled runtime's "one code
    // point in, one code point out" loop would be wrong before anything else was, and the mutable
    // buffer type would inherit the same problem — so it is asserted, not assumed.
    [Fact]
    public void CasingNeverChangesTheNumberOfCharacters()
    {
        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp >= 0xD800 && cp <= 0xDFFF) continue;
            string one = char.ConvertFromUtf32(cp);
            Assert.Equal(1, CodePointCount(one.ToUpperInvariant()));
            Assert.Equal(1, CodePointCount(one.ToLowerInvariant()));
        }
    }

    // ★ Per-character agreement would not be enough on its own. A culture-aware caser can be
    // CONTEXTUAL — Greek final sigma is the standard example, where Σ lowercases to ς only at the
    // end of a word — and a contextual rule would make a table wrong for whole strings while every
    // single character passed. Invariant culture is documented as non-contextual; this holds it to
    // that, on the strings the table is actually used for.
    [Fact]
    public void CasingIsNotContextual_SoAPerCharacterTableIsEnough()
    {
        foreach (var word in new[]
        {
            "ΟΔΥΣΣΕΥΣ", "ΑΣ", "Σ", "ΣΣΣ", "οδυσσευς", "ΑΣΠΙΔΑ ΣΤΟ ΤΕΛΟΣ",
            "straße", "STRASSE", "İstanbul", "ırmak", "ǅungla", "Ǆ ǅ ǆ",
            "héllo wörld", "𐐨𐐩𐐪 mixed 𐐀", "",
        })
        {
            Assert.Equal(word.ToUpperInvariant(), CaseTable.ToUpper(word));
            Assert.Equal(word.ToLowerInvariant(), CaseTable.ToLower(word));
        }
    }

    [Fact]
    public void RandomMultiCharacterText_CasesLikeDotNet()
    {
        // The fuzz behind the previous test: 20,000 strings drawn from the code points that actually
        // have mappings, so most characters exercise the table rather than the ASCII fast path.
        var alphabet = new List<int>();
        for (int cp = 0; cp <= 0xFFFF; cp++)
        {
            if (cp >= 0xD800 && cp <= 0xDFFF) continue;
            string one = char.ConvertFromUtf32(cp);
            if (one.ToUpperInvariant() != one || one.ToLowerInvariant() != one) alphabet.Add(cp);
        }
        alphabet.Add(' ');

        var random = new Random(20260813);
        for (int trial = 0; trial < 20_000; trial++)
        {
            var built = new StringBuilder();
            int length = random.Next(1, 9);
            for (int i = 0; i < length; i++)
                built.Append(char.ConvertFromUtf32(alphabet[random.Next(alphabet.Count)]));

            string text = built.ToString();
            Assert.Equal(text.ToUpperInvariant(), CaseTable.ToUpper(text));
            Assert.Equal(text.ToLowerInvariant(), CaseTable.ToLower(text));
        }
    }

    // ★ The bound the compiled runtime sizes its output buffer on: it allocates twice the input's
    // bytes, which is only safe while no character gains more than one byte. U+023F (two bytes)
    // uppercasing to U+2C7E (three) is the worst case today.
    [Fact]
    public void CasingGrowsTextByAtMostOneBytePerCharacter()
    {
        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp >= 0xD800 && cp <= 0xDFFF) continue;
            int before = Utf8Length(cp);
            Assert.True(Utf8Length(CaseTable.MapUpper(cp)) - before <= 1, $"U+{cp:X4} grew too much uppercasing");
            Assert.True(Utf8Length(CaseTable.MapLower(cp)) - before <= 1, $"U+{cp:X4} grew too much lowercasing");
        }
    }

    [Fact]
    public void AnUnpairedSurrogate_PassesThroughUntouched()
    {
        // It cannot survive a round trip through UTF-8 so it should never reach the caser from a
        // source file, but a string is free to hold one and ConvertToUtf32 throws on it. Casing is
        // not the place to start rejecting text the rest of the language accepts.
        string lone = "a\uD800b";
        Assert.Equal("A\uD800B", CaseTable.ToUpper(lone));
        Assert.Equal(lone, CaseTable.ToLower("A\uD800B"));
    }

    [Fact]
    public void TheTableStampsTheRuntimeItCameFrom()
    {
        // So a reader of a failure — or of the generated C — can tell which Unicode version is
        // baked in without going digging.
        Assert.False(string.IsNullOrWhiteSpace(CaseTable.SourceRuntime));
    }

    private static int CodePointCount(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i += char.IsSurrogatePair(s, i) ? 2 : 1) n++;
        return n;
    }

    private static int Utf8Length(int cp) => cp < 0x80 ? 1 : cp < 0x800 ? 2 : cp < 0x10000 ? 3 : 4;
}
