using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// What may be a map key: a scalar, or a record of keys.
/// </summary>
/// <remarks>
/// <para>
/// ★ The rule used to be "text, number, or fact", and it was too blunt in one direction while
/// explaining itself with something FALSE in the other — it told the reader that a record is a
/// reference type whose identity changes when copied. A record is neither: `IsReferenceType` is
/// series/map/object/matrix/channel/address, records are absent from it, they deep-copy on
/// binding, and they compare structurally. The refusal was right about records by accident and
/// wrong about the reason, which is why no workaround for it ever felt principled.
/// </para>
/// <para>
/// ⚠⚠ The dangerous half is the HASH. A map is a Dictionary, so it asks two questions of a key —
/// are these equal, and what is the hash — and if the two disagree by even one case the map finds
/// nothing and reports NO ERROR. That is the same silent-wrong-answer shape the old message
/// warned about, moved one layer down. Every test here that stores under one value and looks up
/// with a DIFFERENT-but-equal one is locking that pair together.
/// </para>
/// <para>
/// ⚠ And the two backends do not agree by construction: the interpreter HASHES, while the
/// compiler's map is a linear scan calling its own `_eq`. Nothing makes the hash and that scan
/// answer alike except tests that ask both — which is what the pipeline oracle test for this does.
/// </para>
/// </remarks>
public class MapKeyTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var parsed  = new Parser(tokens).Parse();
        var program = new TypeChecker().Check(parsed);
        var output  = new System.IO.StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static TypeException Refused(string source) =>
        Assert.Throws<TypeException>(() => Run(source));

    // ── What is admitted ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ARecordOfNumbers_IsAKey_AndAnEqualRecordFindsIt()
    {
        // ! The whole point, and the test that would go red if the hash and ValuesEqual drifted:
        // `other` is a DIFFERENT record that happens to hold the same numbers. Reference equality
        // would miss it silently — which is exactly what happened before, and why the refusal
        // existed at all.
        Assert.Equal("here\ntrue", Run("""
            Define spot as a record with (1, 2).
            Define grid as a map with (spot : "here").
            Define other as a record with (1, 2).
            State the entry for other in grid.
            State grid has a key for other.
            """));
    }

    [Fact]
    public void ARecordWithNamedFields_IsAKey_RegardlessOfTheOrderTheyAreWritten()
    {
        // ⚠ ValuesEqual sorts named fields by name before comparing, so two records written in
        // different orders ARE equal — which means the hash must be order-independent too. A
        // positional fold over the named fields would give these two different hashes and this
        // test would find nothing.
        Assert.Equal("here", Run("""
            Define spot as a record with (the row 1, the col 2).
            Define grid as a map with (spot : "here").
            Define same as a record with (the col 2, the row 1).
            State the entry for same in grid.
            """));
    }

    [Fact]
    public void ABitPattern_IsAKey_AndItsBaseAndWidthDoNotCount()
    {
        // ★★ The trap the hash is most likely to fall into. `ValuesEqual` compares bit patterns on
        // VALUE ALONE — 0xFF and 0b11111111 are one value written two ways, and `is` says so — so
        // a hash that read Base or Width would give one key two hashes and this lookup would miss.
        Assert.Equal("ten", Run("""
            Define flags as a map with (0b1010 : "ten").
            State the entry for 0xA in flags.
            """));
    }

    [Fact]
    public void ARecordOfMixedScalars_IsAKey()
    {
        // Text inside a record is the case `IsRegionBearing` would have refused — it calls text
        // region-bearing, because text is arena-allocated in the compiler. Arena residence is not
        // the question a key asks.
        Assert.Equal("found", Run("""
            Define tag as a record with ("north", 3, true).
            Define grid as a map with (tag : "found").
            Define same as a record with ("north", 3, true).
            State the entry for same in grid.
            """));
    }

    [Fact]
    public void TextAndNumberKeys_StillWork()
    {
        // The control. Text is the commonest key there is and the one dijkstra.cufe is built on;
        // a change to this rule that broke it would be worse than the rule it replaced.
        Assert.Equal("one\n2", Run("""
            Define names as a map with ("a" : "one").
            Define ids as a map with (1 : 2).
            State the entry for "a" in names.
            State the entry for 1 in ids.
            """));
    }

    // ── What is refused, and whether it says something true ───────────────────────────────────

    [Fact]
    public void ARecordHoldingASeries_IsRefused_AndTheMessageNamesTheFieldNotTheRecord()
    {
        // ★ A record is refused for what is INSIDE it, so the message has to say so. Being told
        // "a record can't be a key" when a record of two numbers plainly can is the same dead end
        // the old message was.
        var e = Refused("""
            Define bad as a record with (1, a series of number).
            Define grid as a map with (bad : "x").
            """);
        Assert.Contains("this record holds", e.Message);
        Assert.Contains("series", e.Message);
    }

    [Fact]
    public void ASeriesKey_IsRefused_ForBeingMutableRatherThanForItsIdentity()
    {
        // ⚠ The reason matters as much as the refusal. A series is a bad key because it can be
        // CHANGED after it is used as one — not because "its identity changes when copied", which
        // is what the old message said and is not what goes wrong.
        var e = Refused("""
            Define xs as a series of number.
            Define grid as a map with (xs : "x").
            """);
        Assert.Contains("can't be a map key", e.Message);
        Assert.Contains("changed after it is used as a key", e.Message);
    }

    [Fact]
    public void AnObjectKey_IsStillRefused()
    {
        var e = Refused("""
            Define object node with (the text name).
            Define n as a new node { the name "a" }.
            Define grid as a map with (n : "x").
            """);
        Assert.Contains("can't be a map key", e.Message);
    }

    [Fact]
    public void NoRefusalCallsARecordAReferenceType()
    {
        // ! The entry's own last line, as a test: whatever the rule ends up being, the message has
        // to stop saying something untrue about records. This asserts the old wording is gone
        // rather than merely that a new one exists.
        foreach (var source in new[]
        {
            """
            Define bad as a record with (1, a series of number).
            Define grid as a map with (bad : "x").
            """,
            """
            Define xs as a series of number.
            Define grid as a map with (xs : "x").
            """,
        })
        {
            var message = Refused(source).Message;
            Assert.DoesNotContain("reference type", message);
            Assert.DoesNotContain("identity changes", message);
        }
    }
}
