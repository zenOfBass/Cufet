using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
public class PipelineUnionTests : PipelineTestBase
{

    [Fact]
    public void Catalogue_NarrowedCase_UsedAtConcreteType()
    {
        // The narrowed value is used AT its concrete type — arithmetic on the number case proves the
        // payload is read as a real number (1 + 3 == 4), not left as a tagged union.
        const string src = """
            Define stuff as a catalogue of (number or text) with (1, "two", 3).
            Define total as 0.
            For each item in stuff, repeat:
                If item is a number, total becomes total + item.
            Done.
            State total.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Catalogue_ClosedUnion_OfObjects_NominalNarrowing()
    {
        // Object cases are NOMINAL and precisely tagged — dog vs cat distinguish exactly.
        const string src = """
            Define object dog with (the text name).
            Define object cat with (the text name).
            Define pets as a catalogue of (dog or cat) with ((a new dog { the name "Rex" }), (a new cat { the name "Tom" })).
            For each p in pets, repeat:
                If p is a dog, State "dog:" joined to (the name of p).
                Otherwise, State "cat:" joined to (the name of p).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Catalogue_MixedScalarAndObject()
    {
        const string src = """
            Define object dog with (the text name).
            Define things as a catalogue of (number or dog) with (7, (a new dog { the name "Rex" })).
            For each t in things, repeat:
                If t is a number, State "n=" joined to (t converted to text).
                Otherwise, State "dog=" joined to (the name of t).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Catalogue_SeriesOps_FallOutOfPerTypeSynthesis()
    {
        // A catalogue IS a series of union, so the existing per-T series synthesis gives every series
        // op for free: Add, Add-to-start, the number of, ordinal access, Remove.
        const string src = """
            Define stuff as a catalogue of (number or text) with (1, "two").
            Insert 3 into stuff.
            Insert "four" into the start of stuff.
            State the number of stuff.
            Define f as the first of stuff.
            If f is a text, State "first=" joined to f.
            Remove the first item from stuff.
            State the number of stuff.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Atlas_MapOfUnion_LookupAndNarrow()
    {
        // `atlas` (map whose value type is a union) falls out of map-of-T + union with no extra work.
        const string src = """
            Define a1 as an atlas from text to (number or text) with ("a" : 1, "b" : "two").
            Define v as (the entry for "a" in a1 but void is 0).
            If v is a number, State "num:" joined to (v converted to text).
            Otherwise, State "other".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Catalogue_ContainerVsContainerUnion_NarrowsPrecisely()
    {
        // ISA.2d — was an ISA.2c clean-throw, now an oracle match. Narrowing a catalogue that can
        // hold two DIFFERENT container types used to be refused: an empty container carried no
        // element type at runtime, so the compiler answered from its TAG (precise) while the
        // interpreter answered from the VALUE (vacuously matching any container), and the two took
        // different branches. The interpreter's containers now carry their declared element type,
        // so both are precise and the refusal is gone.
        const string src = """
            Define nums as a series of number with (1, 2).
            Define txts as a series of text with ("x").
            Define grids as a catalogue of (series of number or series of text) with (nums, txts).
            For each g in grids, repeat:
                If g is a series of number, State "nums " joined to (the number of g converted to text).
                Otherwise, State "texts " joined to (the number of g converted to text).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ★ The case the refusal existed for: EMPTY containers, where there is no element to inspect
    // and the answer can only come from the type the container was created with.
    [Fact]
    public void Catalogue_EmptyContainerCases_NarrowPreciselyOnBothBackends()
    {
        const string src = """
            Define items as a catalogue of ((series of number) or (series of text)) with ().
            Insert a series of text with () into items.
            Insert a series of number with () into items.
            For each item in items, repeat:
                If item is a series of number:
                    State "matched: series of number".
                Done.
                If item is a series of text:
                    State "matched: series of text".
                Done.
            Done.
            """;
        // Each empty container matches exactly ONE case — previously it matched both interpreted.
        Assert.Equal("matched: series of text\nmatched: series of number", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // Completeness guard for the carrier. An empty series can be produced several ways, and a
    // creation route that forgets to record its element type would silently revert to the old
    // vacuous answer — the exact failure mode that made a side-table design too risky. Each route
    // below must narrow precisely, so a missed one fails loudly here instead.
    [Theory]
    [InlineData("a series of text with ()",                      "text")]
    [InlineData("(a series of text with ()) sorted",             "text")]
    [InlineData("a series of number with ()",                    "number")]
    [InlineData("(a series of number with ()) sorted",           "number")]
    [InlineData("((\"a,b\" split by \",\") sorted)",                "text")]
    [InlineData("((range 1 to 3) sorted in reverse)",             "number")]
    public void EmptySeries_FromEveryCreationRoute_CarriesItsElementType(string expr, string expected)
    {
        string src = $"""
            Define items as a catalogue of ((series of number) or (series of text)) with ().
            Insert {expr} into items.
            For each item in items, repeat:
                If item is a series of number, State "number".
                Otherwise, State "text".
            Done.
            """;
        Assert.Equal(expected, Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void IsA_ElementAware_ForContainers()
    {
        // ISA.1: `is a` recurses into element/key/value types on BOTH backends. Previously every
        // one of these answered the WRONG way (kind-erased → true). NOTE: EMPTY containers are
        // deliberately untouched by ISA.1 (vacuously permissive in the interpreter) — that boundary
        // is ISA.2's to settle, and it is safe either way (no element to misread).
        const string series = """
            Define words as a series of text with ("a", "b").
            If words is a series of number, State "matched number". Otherwise, State "not number".
            """;
        Assert.Equal(InterpretRaw(series), CompileRaw(series));
        const string map = """
            Define wordmap as a map from text to text with ("k" : "v").
            If wordmap is a map from text to number, State "matched". Otherwise, State "not matched".
            """;
        Assert.Equal(InterpretRaw(map), CompileRaw(map));
        const string nested = """
            Define inner as a series of text with ("a").
            Define outer as a series with (inner).
            If outer is a series of series of number, State "matched". Otherwise, State "not matched".
            """;
        Assert.Equal(InterpretRaw(nested), CompileRaw(nested));
    }

    // ── ESC.1/ESC.2 — arena escape: structural region test + copy-at-store ───
    // A value built inside a rabbit and stored into longer-lived storage used to be a
    // heap-use-after-free (the rabbit's Done. frees its arena while the destination still points
    // in). The front-end's outward-store invariant is a TOP-LEVEL type test: it misses `text`, and
    // every value-typed WRAPPER (record / voidable / failable / union) launders even a COVERED type
    // past it. Fixed by a STRUCTURAL region-bearing test (TypeChecker.IsRegionBearing) that
    // annotates each store with the destination's depth, plus a copy-at-store in the compiler that
    // deep-copies the value into that depth's arena — reusing the channel deep-copy families, which
    // were already allocator-parameterized.

    [Fact]
    public void Escape_TextBuiltInRabbit_StoredOutward()
    {
        // THE REPORTED REPRODUCER. Was: heap-use-after-free, printed "keeper=keeper=".
        const string src = """
            Define keeper as "".
            Pull a rabbit.
                keeper becomes "a" joined to "b".
            Done.
            State keeper.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
