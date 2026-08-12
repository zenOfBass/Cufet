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
public class PipelineSeriesTests : PipelineTestBase
{

    [Fact]
    public void Arena_SeriesAppend_MatchesInterpreter()
    {
        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (1, 2, 3).
                Insert 4 into xs.
                For each x in xs, repeat:
                    State x.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Arena_SeriesLength_MatchesInterpreter()
    {
        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (10, 20, 30).
                State the number of xs.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Arena_SeriesFirstAndLast_MatchesInterpreter()
    {
        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (10, 20, 30).
                State the first of xs.
                State the last of xs.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Arena_StateSeries_MatchesInterpreter()
    {
        // State a whole series — exercises cufet_print_series
        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (1, 2, 3).
                State xs.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Arena_NestedPull_MatchesInterpreter()
    {
        // Two arenas on the stack: inner frees first, outer second
        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (1, 2).
                Pull a rabbit.
                    Define ys as a series of number with (3, 4).
                    For each y in ys, repeat:
                        State y.
                    Done.
                Done.
                For each x in xs, repeat:
                    State x.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Arena_SeriesGrowBeyondInitialCapacity_MatchesInterpreter()
    {
        // Appending 8+ elements forces the data buffer to grow (initial cap is 4)
        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (1, 2, 3, 4).
                Insert 5 into xs.
                Insert 6 into xs.
                Insert 7 into xs.
                Insert 8 into xs.
                Insert 9 into xs.
                State the number of xs.
                State the last of xs.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Arena_SeriesPrepend_MatchesInterpreter()
    {
        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (2, 3).
                Insert 1 into the start of xs.
                For each x in xs, repeat:
                    State x.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Arena_SeriesRemoveAt_MatchesInterpreter()
    {
        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (1, 2, 3, 4).
                Remove the last item from xs.
                For each x in xs, repeat:
                    State x.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Arena_SeriesUsedInArithmetic_MatchesInterpreter()
    {
        // Series element used in arithmetic — exercises SeriesAccess in EmitExpr
        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (5, 10, 15).
                State the first of xs + the last of xs.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Arena_ForEachOverSeries_WithAccumulator_MatchesInterpreter()
    {
        // Series iteration with an outer accumulator variable (scalar escapes Pull)
        const string src = """
            Define total as 0.
            Pull a rabbit.
                Define xs as a series of number with (1, 2, 3, 4, 5).
                For each x in xs, repeat:
                    total becomes total + x.
                Done.
            Done.
            State total.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Arena_SliceFour_StillPasses_AfterSliceFive()
    {
        // Regression: scalar functions must still compile correctly now that
        // the global arena is pushed in main.
        const string src = """
            Bind number to factorial, given (the number n):
                If n <= 1, return 1.
                return n * cast factorial on (n - 1).
            Done.
            State cast factorial on (10).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Slice 5.5: software decimal (bit-identical to interpreter's System.Decimal) ──
    // These are the fix's proof: programs that diverged under the old double lowering
    // (0.1 + 0.2 == 0.30000000000000004, etc.) now match the interpreter exactly.

    [Fact]
    public void Decimal_PointOnePlusPointTwo_IsExactlyPointThree()
    {
        // The canonical floating-point trap. Under double this printed 0.3 by luck of
        // %.15g rounding, but comparisons diverged (see the equality test below).
        const string src = "State 0.1 + 0.2.";
        Assert.Equal("0.3", Compile(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_EqualityThatDivergesUnderDouble_MatchesInterpreter()
    {
        // THE proof: 4.35 * 100 is exactly 435 in decimal, but 434.99999999999994 in
        // double — so this branch would have gone the wrong way under the old lowering.
        const string src = "If 4.35 * 100 is 435, state \"exact\". Otherwise, state \"wrong\".";
        Assert.Equal("exact", Compile(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_PointOneTimesThree_MatchesInterpreter()
    {
        const string src = "State 0.1 * 3.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_MoneyAddition_MatchesInterpreter()
    {
        // 1.10 + 2.20 == 3.3 exact (double: 3.3000000000000003)
        const string src = "Define price as 1.10. Define shipping as 2.20. State price + shipping.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_Subtraction_MatchesInterpreter()
    {
        // 0.3 - 0.1 == 0.2 exact (double: 0.19999999999999998)
        const string src = "State 0.3 - 0.1.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_TrailingZeroNormalization_MatchesInterpreter()
    {
        // 1.0 + 1.0 keeps scale (2.0) internally; format strips it to "2"
        const string src = "State 1.0 + 1.0. State 1.50 + 0.00. State 3.140.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_DivisionRepeating_MatchesInterpreter()
    {
        // Non-terminating quotients must round to 28-29 digits half-to-even, like .NET.
        const string src = "State 1 / 3. State 2 / 3. State 10 / 3. State 1 / 7. State 1 / 6.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_DivisionExact_MatchesInterpreter()
    {
        const string src = "State 1 / 4. State 7 / 2. State 10 / 2. State 1 / 8.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_DivisionThenMultiplyBackEmergent_MatchesInterpreter()
    {
        // 100 / 3 * 3 == 100 exactly — an emergent property of correct decimal
        // rounding-and-overflow that only holds if / and * are both faithful.
        const string src = "State 100 / 3 * 3.";
        Assert.Equal("100", Compile(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_RoundHalfToEven_MatchesInterpreter()
    {
        // Ties at the 29th place: 0.5 -> 0, 1.5 -> 2, 2.5 -> 2, 3.5 -> 4 (round to even).
        const string src = """
            State 0.5 * 0.0000000000000000000000000001.
            State 1.5 * 0.0000000000000000000000000001.
            State 2.5 * 0.0000000000000000000000000001.
            State 3.5 * 0.0000000000000000000000000001.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_NegativeFractions_MatchesInterpreter()
    {
        const string src = "State -0.5 - 0.25. State 0 - 0.3. State -1 / 3.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_ModuloFractional_MatchesInterpreter()
    {
        // Decimal remainder, sign of dividend.
        const string src = "State 5.5 % 2. State -7 % 3. State 7 % -3.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_LargeIntegerExact_MatchesInterpreter()
    {
        // Beyond double's 2^53 exact-integer range; decimal keeps every digit.
        const string src = "State 12345678901234567890 * 1000000.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_FractionalSeries_SumMatchesInterpreter()
    {
        // Fractional series elements accumulated: 0.1 + 0.2 + 0.3 == 0.6 exact.
        const string src = """
            Define total as 0.
            Pull a rabbit.
                Define xs as a series of number with (0.1, 0.2, 0.3).
                For each x in xs, repeat:
                    total becomes total + x.
                Done.
            Done.
            State total.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_FractionalSeries_PrintMatchesInterpreter()
    {
        // The whole series prints with exact decimal elements.
        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (0.5, 1.25, 3.333).
                State xs.
                State the first of xs + the last of xs.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_FractionalRangeStep_MatchesInterpreter()
    {
        // Range counting by a fractional step — the loop counter is now decimal.
        const string src = "For each n in the range 0 to 1 counting by 0.25, repeat: State n. Done.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Decimal_FunctionWithFractions_MatchesInterpreter()
    {
        // Decimal flows through function params/returns unchanged.
        const string src = """
            Bind number to average, given (the number x, the number y):
                return (x + y) / 2.
            Done.
            State cast average on (0.1, 0.2).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Slice 5B: records (value structs) + text-as-stored-data ──────────
    // Records lower to C value structs — copy-on-assign reproduces the interpreter's
    // value semantics (deep for nested records, shared for series pointers).

    [Fact]
    public void Record_ConstructAndNamedAccess_MatchesInterpreter()
    {
        const string src = """
            Define alice as a record with (the name "Alice", the age 30).
            State alice.
            State the name of alice.
            State the age of alice.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
