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
    // ★★ OPT-IN, and the reason why is the whole point of the design.
    //
    // .NET's invariant casing is NOT the same on every machine. It is ICU-backed, and ICU versions
    // differ per platform: measured 2026-08-13, Arch's .NET 10.0.11 knows the Unicode 16 additions
    // (Garay U+10D50.., the Latin Extended-D block U+A7CB.., U+019B gaining an uppercase) and
    // Windows' .NET 10.0.8 does not. Twenty code points, same runtime major version, different
    // answers. So "matches live .NET" is not a property that holds anywhere in particular, and a
    // test asserting it is a test that fails depending on who runs it.
    //
    // ★ It is also not a property that MATTERS any more, which is the payoff of both backends
    // reading one table. When .NET moves and the table does not, nothing breaks: the interpreter
    // and the compiler still agree, because neither of them asks .NET. Drift is information about
    // the outside world, not a defect — so it is worth being able to check deliberately, and wrong
    // to fail a build over. (Had the C table been GENERATED from .NET at build time instead, this
    // same measurement would have been a live divergence: a Linux interpreter casing one way and
    // its own compiled binary casing another.)
    //
    // Run it when you want to know:  CUFET_CASE_DRIFT=1 dotnet test --filter CaseTableTests
    [Fact]
    public void TheTable_MatchesDotNetInvariantCasing_ForEveryCodePoint()
    {
        if (Environment.GetEnvironmentVariable("CUFET_CASE_DRIFT") != "1") return;

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
            $"The case table differs from THIS machine's .NET invariant casing (table generated from "
            + $"{CaseTable.SourceRuntime}, running on {Environment.Version}).\n"
            + $"This does NOT mean anything is broken: both backends read the table, so they still "
            + $"agree with each other. It means this machine's ICU knows mappings the table was "
            + $"generated before — or, less likely, fewer.\n"
            + $"To adopt them: `dotnet run tools/gen-case-table.cs` ON THE MACHINE WITH THE NEWER "
            + $"ICU, review the diff, and note the Unicode version in the changelog.\n  "
            + string.Join("\n  ", drifted));
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

    // ★ The real gate, and it asserts LITERAL expected values rather than comparing to .NET — so it
    // means the same thing on every machine, which the .NET comparisons above do not. These are all
    // long-settled mappings; if one of them ever changes, that is a Unicode earthquake and we want
    // to be told, not quietly re-baselined against whatever the local ICU thinks today.
    [Fact]
    public void TheTable_GetsTheSettledMappingsRight()
    {
        Assert.Equal("HÉLLO WÖRLD", CaseTable.ToUpper("héllo wörld"));
        Assert.Equal("héllo wörld", CaseTable.ToLower("HÉLLO WÖRLD"));
        Assert.Equal("ΑΣΠΙΔΑ", CaseTable.ToUpper("ασπιδα"));
        Assert.Equal("МОСКВА", CaseTable.ToUpper("москва"));
        Assert.Equal("москва", CaseTable.ToLower("МОСКВА"));
        Assert.Equal("𐐀𐐁𐐂", CaseTable.ToUpper("𐐨𐐩𐐪"));          // above the BMP
        Assert.Equal("", CaseTable.ToUpper(""));

        // Simple case mapping: one character in, one character out, always.
        Assert.Equal("STRAßE", CaseTable.ToUpper("straße"));      // NOT "STRASSE"
        Assert.Equal("ﬁANCÉ", CaseTable.ToUpper("ﬁancé"));        // the ligature stays whole
        Assert.Equal("ı AND İ", CaseTable.ToUpper("ı and İ"));    // invariant declines to pick a locale
    }

    // ★ A per-character table is only sufficient if casing is NOT CONTEXTUAL. Greek final sigma is
    // the standard counter-example — in locale-aware casing Σ lowercases to ς at the end of a word
    // and σ elsewhere, which no per-character map can express. Invariant casing does not do that,
    // and this pins the consequence directly: every sigma lowercases to σ, wherever it sits.
    [Fact]
    public void CasingIsNotContextual_SoAPerCharacterTableIsEnough()
    {
        Assert.Equal("οδυσσευσ", CaseTable.ToLower("ΟΔΥΣΣΕΥΣ"));   // final sigma would be ς
        Assert.Equal("ασ", CaseTable.ToLower("ΑΣ"));
        Assert.Equal("σ", CaseTable.ToLower("Σ"));
        Assert.Equal("σσσ", CaseTable.ToLower("ΣΣΣ"));
        Assert.DoesNotContain("ς", CaseTable.ToLower("ΑΣΠΙΔΑ ΣΤΟ ΤΕΛΟΣ"));

        // A titlecase letter has different upper and lower forms, so it appears in both halves of
        // the table with unequal deltas — the case a run-length encoder is most likely to get wrong.
        Assert.Equal("Ǆ Ǆ Ǆ", CaseTable.ToUpper("Ǆ ǅ ǆ"));
        Assert.Equal("ǆ ǆ ǆ", CaseTable.ToLower("Ǆ ǅ ǆ"));
    }

    [Fact]
    public void RandomMultiCharacterText_CasesLikeDotNet()
    {
        // Opt-in for the same reason as the exhaustive check above — it compares against live .NET,
        // which is ICU-dependent and therefore machine-dependent.
        if (Environment.GetEnvironmentVariable("CUFET_CASE_DRIFT") != "1") return;

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
