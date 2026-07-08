using System.Diagnostics;
using System.Runtime.InteropServices;
using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

public class PipelineTests
{
    // Compiles source to a temp native binary, runs it, returns stdout trimmed.
    private static string Compile(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);

        var cSource = new CodeGenerator().Generate(program);

        var tmp    = Path.GetTempFileName();
        File.Delete(tmp);
        var cPath  = tmp + ".c";
        var binExt = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
        var binPath = tmp + binExt;

        try
        {
            File.WriteAllText(cPath, cSource);
            new GccInvoker().Compile(cPath, binPath);
        }
        finally
        {
            try { File.Delete(cPath); } catch { }
        }

        try
        {
            var psi = new ProcessStartInfo(binPath)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output.Replace("\r\n", "\n").TrimEnd('\n');
        }
        finally
        {
            try { File.Delete(binPath); } catch { }
        }
    }

    // Interprets source and returns stdout trimmed — the oracle.
    private static string Interpret(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var sb = new StringWriter();
        new CufetInterpreter(sb).Execute(program);
        return sb.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    // ── Acceptance bar: State 1 + 1. → binary runs → prints 2 ──────────

    [Fact]
    public void State_Addition_PrintsResult()
    {
        Assert.Equal("2", Compile("State 1 + 1."));
    }

    [Fact]
    public void State_Addition_MatchesInterpreter()
    {
        const string src = "State 1 + 1.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── Oracle: compiled output == interpreter output ────────────────────

    [Fact]
    public void State_Literal_MatchesInterpreter()
    {
        const string src = "State 5.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void State_Subtraction_MatchesInterpreter()
    {
        const string src = "State 10 - 3.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void State_Multiplication_MatchesInterpreter()
    {
        const string src = "State 3 * 4.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void State_Division_MatchesInterpreter()
    {
        const string src = "State 10 / 2.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void State_Parenthesized_MatchesInterpreter()
    {
        const string src = "State 2 * (3 + 4).";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void State_UnaryNegation_MatchesInterpreter()
    {
        const string src = "State -5.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void State_MultipleStatements_MatchesInterpreter()
    {
        const string src = "State 1 + 1. State 3 * 3.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void State_Zero_MatchesInterpreter()
    {
        const string src = "State 0.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── Slice 2: variables ───────────────────────────────────────────────

    [Fact]
    public void Variable_DefineAndUse_MatchesInterpreter()
    {
        const string src = "Define x as 5. State x.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Variable_DefineAndReassign_MatchesInterpreter()
    {
        const string src = "Define x as 3. x becomes 7. State x.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Variable_ChainedDefines_MatchesInterpreter()
    {
        const string src = "Define x as 3. Define y as x + 5. State y.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Variable_SelfReferenceReassignment_MatchesInterpreter()
    {
        const string src = "Define x as 1. x becomes x + 1. State x.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Variable_HyphenatedName_MatchesInterpreter()
    {
        const string src = "Define grand-total as 100. State grand-total.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Variable_MultipleVarsInteracting_MatchesInterpreter()
    {
        const string src = "Define x as 3. Define y as 4. State x + y.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Variable_FullSpecExample_MatchesInterpreter()
    {
        // Define x as 5. Define y as x + 3. y becomes y * 2. State y. → 16
        const string src = "Define x as 5. Define y as x + 3. y becomes y * 2. State y.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Variable_Permanent_MatchesInterpreter()
    {
        const string src = "Define x as 10 permanently. State x.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Variable_VariableInArithmetic_MatchesInterpreter()
    {
        const string src = "Define width as 6. Define height as 7. State width * height.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Variable_MixedWithStateArithmetic_MatchesInterpreter()
    {
        // Slice 1 arithmetic alongside slice 2 variables
        const string src = "State 1 + 1. Define x as 10. x becomes x - 3. State x.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── Slice 3: control flow ────────────────────────────────────────────

    [Fact]
    public void If_TrueBranch_MatchesInterpreter()
    {
        const string src = "Define x as 5. If x is 5, state x. Otherwise, state 0.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void If_FalseBranch_MatchesInterpreter()
    {
        const string src = "Define x as 3. If x is 5, state x. Otherwise, state 0.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void If_OtherwiseIf_MatchesInterpreter()
    {
        const string src = "Define x as 3. If x is 5, state 5. Otherwise if x is 3, state 3. Otherwise, state 0.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void If_NoElse_MatchesInterpreter()
    {
        const string src = "Define x as 5. If x is 5, state x.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void While_Counting_MatchesInterpreter()
    {
        const string src = "Define n as 1. While n <= 3, repeat: State n. n becomes n + 1. Done.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void While_Accumulation_MatchesInterpreter()
    {
        // 1 + 2 + ... + 10 = 55
        const string src = "Define n as 1. Define total as 0. While n <= 10, repeat: total becomes total + n. n becomes n + 1. Done. State total.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void ForEach_Range_Ascending_MatchesInterpreter()
    {
        const string src = "For each n in the range 1 to 5, repeat: State n. Done.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void ForEach_Range_Descending_MatchesInterpreter()
    {
        const string src = "For each n in the range 5 to 1, repeat: State n. Done.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void ForEach_Range_WithStep_MatchesInterpreter()
    {
        // 1, 3, 5, 7, 9
        const string src = "For each n in the range 1 to 10 counting by 2, repeat: State n. Done.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void ForEach_Squares_MatchesInterpreter()
    {
        const string src = "For each n in the range 1 to 5, repeat: State n * n. Done.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Stop_ExitsLoop_MatchesInterpreter()
    {
        // Prints 1, 2, 3 — breaks before printing 4
        const string src = "Define n as 1. While n <= 10, repeat: If n is 4, stop. State n. n becomes n + 1. Done.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Skip_ContinuesLoop_MatchesInterpreter()
    {
        // Prints 1, 3, 5 — skips even values
        const string src = "For each n in the range 1 to 5, repeat: If n % 2 is 0, skip. State n. Done.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void RepeatUntil_MatchesInterpreter()
    {
        // Prints 1, 2, 3
        const string src = "Define x as 0. Repeat: x becomes x + 1. State x. Until x is 3.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void FizzBuzz_1_to_15_MatchesInterpreter()
    {
        // The README flagship example — exercises For each + If/Otherwise if/Otherwise + fmod
        const string src = """
            For each counter in the range 1 to 15, repeat:
                If the counter % 15 is 0, state "FizzBuzz".
                Otherwise if the counter % 3 is 0, state "Fizz".
                Otherwise if the counter % 5 is 0, state "Buzz".
                Otherwise, state the counter.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Nested_IfInLoop_MatchesInterpreter()
    {
        // Accumulate only positive contributions: 1+3+5 = 9
        const string src = """
            Define total as 0.
            For each n in the range 1 to 5, repeat:
                If n % 2 is not 0, total becomes total + n.
            Done.
            State total.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void BooleanLogic_And_MatchesInterpreter()
    {
        // True only when both conditions hold
        const string src = "Define x as 5. If x > 3 and x < 10, state 1. Otherwise, state 0.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── Slice 4: scalar functions ────────────────────────────────────────────

    [Fact]
    public void Function_Simple_DoubleValue_MatchesInterpreter()
    {
        const string src = """
            Bind number to double-it, given (the number x):
                return x * 2.
            Done.
            State cast double-it on (5).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Function_Simple_Triple_MatchesInterpreter()
    {
        const string src = """
            Bind number to triple, given (the number x):
                return x * 3.
            Done.
            State cast triple on (4).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Function_MultipleParams_MatchesInterpreter()
    {
        // 'add' is a reserved token; use a hyphenated name
        const string src = """
            Bind number to sum-up, given (the number x, the number y):
                return x + y.
            Done.
            State cast sum-up on (3, 4).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Function_NestedCalls_MatchesInterpreter()
    {
        // cast double-it on (cast triple on (5)) → 5*3=15, 15*2=30
        const string src = """
            Bind number to double-it, given (the number x):
                return x * 2.
            Done.
            Bind number to triple, given (the number x):
                return x * 3.
            Done.
            State cast double-it on (cast triple on (5)).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Function_Recursion_Factorial_MatchesInterpreter()
    {
        // The README flagship recursion example — factorial(10) = 3628800
        const string src = """
            Bind number to factorial, given (the number n):
                If n <= 1, return 1.
                return n * cast factorial on (n - 1).
            Done.
            State cast factorial on (10).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Function_UsedInControlFlow_MatchesInterpreter()
    {
        // Square each number in a range — exercises function + for-each together
        const string src = """
            Bind number to square, given (the number n):
                return n * n.
            Done.
            For each n in the range 1 to 5, repeat:
                State cast square on (n).
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Function_VoidCastStatement_MatchesInterpreter()
    {
        // Void function called via CastStatement; void return type declared with 'void' keyword
        const string src = """
            Bind void to print-double, given (the number x):
                State x * 2.
            Done.
            Cast print-double on (7).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Function_FactReturn_MatchesInterpreter()
    {
        // Function returning a fact (boolean) used in a condition
        const string src = """
            Bind fact to is-positive, given (the number n):
                return n > 0.
            Done.
            If cast is-positive on (5), state 1. Otherwise, state 0.
            If cast is-positive on (-3), state 1. Otherwise, state 0.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Function_ForwardReference_MatchesInterpreter()
    {
        // Function A (defined first) calls function B (defined after) — requires forward decls
        const string src = """
            Bind number to add-one-then-double, given (the number x):
                return cast double-it on (x + 1).
            Done.
            Bind number to double-it, given (the number x):
                return x * 2.
            Done.
            State cast add-one-then-double on (4).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Function_MutualRecursion_MatchesInterpreter()
    {
        // is-even calls is-odd and vice versa — exercises forward declarations
        const string src = """
            Bind fact to is-even, given (the number n):
                If n is 0, return true.
                return cast is-odd on (n - 1).
            Done.
            Bind fact to is-odd, given (the number n):
                If n is 0, return false.
                return cast is-even on (n - 1).
            Done.
            If cast is-even on (4), state 1. Otherwise, state 0.
            If cast is-even on (7), state 1. Otherwise, state 0.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Function_WithLocalVariables_MatchesInterpreter()
    {
        // Function body uses local variables (Define/becomes inside function)
        const string src = """
            Bind number to sum-to, given (the number n):
                Define total as 0.
                For each i in the range 1 to n, repeat:
                    total becomes total + i.
                Done.
                return total.
            Done.
            State cast sum-to on (10).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Function_ReferenceTypeParam_MatchesInterpreter()
    {
        // Reference-type (series) parameters are supported as of slice 5B — the series is
        // an arena pointer whose region is the caller's, so passing it down just works.
        const string src = """
            Bind number to count-items, given (the series of number items):
                return the number of items.
            Done.
            Pull a rabbit.
                Define xs as a series of number with (5, 10, 15, 20).
                State cast count-items on (xs).
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── Slice 5A: arena + series ─────────────────────────────────────────

    [Fact]
    public void Arena_SimpleSeriesCreateAndIterate_MatchesInterpreter()
    {
        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (1, 2, 3).
                For each x in xs, repeat:
                    State x.
                Done.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Arena_SeriesAppend_MatchesInterpreter()
    {
        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (1, 2, 3).
                Add 4 to xs.
                For each x in xs, repeat:
                    State x.
                Done.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Arena_SeriesGrowBeyondInitialCapacity_MatchesInterpreter()
    {
        // Appending 8+ elements forces the data buffer to grow (initial cap is 4)
        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (1, 2, 3, 4).
                Add 5 to xs.
                Add 6 to xs.
                Add 7 to xs.
                Add 8 to xs.
                Add 9 to xs.
                State the number of xs.
                State the last of xs.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Arena_SeriesPrepend_MatchesInterpreter()
    {
        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (2, 3).
                Add 1 to the start of xs.
                For each x in xs, repeat:
                    State x.
                Done.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Decimal_EqualityThatDivergesUnderDouble_MatchesInterpreter()
    {
        // THE proof: 4.35 * 100 is exactly 435 in decimal, but 434.99999999999994 in
        // double — so this branch would have gone the wrong way under the old lowering.
        const string src = "If 4.35 * 100 is 435, state \"exact\". Otherwise, state \"wrong\".";
        Assert.Equal("exact", Compile(src));
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Decimal_PointOneTimesThree_MatchesInterpreter()
    {
        const string src = "State 0.1 * 3.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Decimal_MoneyAddition_MatchesInterpreter()
    {
        // 1.10 + 2.20 == 3.3 exact (double: 3.3000000000000003)
        const string src = "Define price as 1.10. Define shipping as 2.20. State price + shipping.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Decimal_Subtraction_MatchesInterpreter()
    {
        // 0.3 - 0.1 == 0.2 exact (double: 0.19999999999999998)
        const string src = "State 0.3 - 0.1.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Decimal_TrailingZeroNormalization_MatchesInterpreter()
    {
        // 1.0 + 1.0 keeps scale (2.0) internally; format strips it to "2"
        const string src = "State 1.0 + 1.0. State 1.50 + 0.00. State 3.140.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Decimal_DivisionRepeating_MatchesInterpreter()
    {
        // Non-terminating quotients must round to 28-29 digits half-to-even, like .NET.
        const string src = "State 1 / 3. State 2 / 3. State 10 / 3. State 1 / 7. State 1 / 6.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Decimal_DivisionExact_MatchesInterpreter()
    {
        const string src = "State 1 / 4. State 7 / 2. State 10 / 2. State 1 / 8.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Decimal_DivisionThenMultiplyBackEmergent_MatchesInterpreter()
    {
        // 100 / 3 * 3 == 100 exactly — an emergent property of correct decimal
        // rounding-and-overflow that only holds if / and * are both faithful.
        const string src = "State 100 / 3 * 3.";
        Assert.Equal("100", Compile(src));
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Decimal_NegativeFractions_MatchesInterpreter()
    {
        const string src = "State -0.5 - 0.25. State 0 - 0.3. State -1 / 3.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Decimal_ModuloFractional_MatchesInterpreter()
    {
        // Decimal remainder, sign of dividend.
        const string src = "State 5.5 % 2. State -7 % 3. State 7 % -3.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Decimal_LargeIntegerExact_MatchesInterpreter()
    {
        // Beyond double's 2^53 exact-integer range; decimal keeps every digit.
        const string src = "State 12345678901234567890 * 1000000.";
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Decimal_FractionalRangeStep_MatchesInterpreter()
    {
        // Range counting by a fractional step — the loop counter is now decimal.
        const string src = "For each n in the range 0 to 1 counting by 0.25, repeat: State n. Done.";
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Record_PositionalAccess_MatchesInterpreter()
    {
        const string src = "Define point as a record with (3, 4). State the first of point. State the second of point.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Record_FieldsCanonicalPrintOrder_MatchesInterpreter()
    {
        // Fields written in non-sorted order still print sorted (canonical), matching the interpreter.
        const string src = "State a record with (the name \"Zed\", the age 9, the city \"Tulsa\").";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Record_NamedFieldSet_MatchesInterpreter()
    {
        const string src = """
            Define alice as a record with (the name "Alice", the age 30).
            the age of alice becomes 31.
            State alice.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Record_PositionalFieldSet_MatchesInterpreter()
    {
        const string src = "Define point as a record with (3, 4). the first of point becomes 10. State point.";
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Record_ValueSemantics_DefineCopies_MatchesInterpreter()
    {
        // Define copies (value semantics): mutating the copy leaves the original untouched.
        const string src = """
            Define alice as a record with (the name "Alice", the age 30).
            Define bob as alice.
            the name of bob becomes "Bob".
            the age of bob becomes 99.
            State alice.
            State bob.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Record_Nested_MatchesInterpreter()
    {
        // A record field that is itself a record — deep-copied inline (value struct).
        const string src = """
            Define alice as a record with (the name "Alice", the age 30).
            Define row as a record with (the person alice, the score 95).
            State row.
            State the name of the person of row.
            State the score of row.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Record_WithSeriesField_MatchesInterpreter()
    {
        // A record holding a series (reference type) — the struct carries a CufetSeries*.
        const string src = """
            Pull a rabbit.
                Define team as a record with (the label "A", the scores a series of number with (10, 20, 30)).
                State team.
                State the first of the scores of team.
                Add 40 to the scores of team.
                State team.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Record_TextFieldEquality_MatchesInterpreter()
    {
        // Text-as-stored-data: text field compared by value (strcmp), not pointer.
        const string src = """
            Define alice as a record with (the name "Alice", the age 30).
            If the name of alice is "Alice", state "match". Otherwise, state "no".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Record_ReturnedFromFunction_MatchesInterpreter()
    {
        // A function that builds and returns a record (record return type, by value).
        const string src = """
            Bind the record result with (the text name, the number age) to make-person, given (the text n, the number years):
                return a record with (the name n, the age years).
            Done.
            Define p as cast make-person on ("Alice", 30).
            State p.
            State the age of p.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Record_ReadOnlyParam_MatchesInterpreter()
    {
        // A function reading (not mutating) a record param — by-value matches the oracle.
        const string src = """
            Bind number to get-age, given (the record p with (the text name, the number age)):
                return the age of p.
            Done.
            Define alice as a record with (the name "Alice", the age 42).
            State cast get-age on (alice).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Record_SeriesOfRecordsDeferred_ThrowsCleanly()
    {
        // Series-of-records isn't lowered yet — must defer with a clean CompilerException,
        // not crash. (TypeChecker accepts this valid Cufet program.)
        const string src = """
            Define people as a series of records like (the text name, the number age).
            """;
        var tokens  = new CufetLexer(src).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        Assert.Throws<CompilerException>(() => new CodeGenerator().Generate(program));
    }

    [Fact]
    public void Record_MutatedParam_NoLeak_MatchesInterpreter()
    {
        // Binding is binding: a record arg is copied, so a function mutating its param
        // does NOT change the caller's record — and compiled matches interpreted exactly
        // (both copy). This was the pre-fix divergence; it's now locked shut.
        const string src = """
            Bind the record result with (the text name, the number age) to make-older, given (the record p with (the text name, the number age)):
                the age of p becomes the age of p + 1.
                return p.
            Done.
            Define alice as a record with (the name "Alice", the age 30).
            Define older as cast make-older on (alice).
            State alice.
            State older.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Series_MutatedParam_Shares_MatchesInterpreter()
    {
        // The region-model flip side: a series arg is shared, so mutating it inside a
        // function IS visible to the caller — compiled and interpreted agree on that too.
        const string src = """
            Bind void to grow, given (the series of number s):
                Add 99 to s.
            Done.
            Pull a rabbit.
                Define xs as a series of number with (1, 2, 3).
                Cast grow on (xs).
                State xs.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── Slice 5B: objects (nominal value structs + methods, direct dispatch) ──

    [Fact]
    public void Object_ConstructAccessAndPrint_MatchesInterpreter()
    {
        const string src = """
            Define object person with (the text name, the number age).
            Define alice as a new person { the name "Alice", the age 30 }.
            State alice.
            State alice's name.
            State the age of alice.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Object_VoidMutatingMethod_MatchesInterpreter()
    {
        // `one's age becomes ...` mutates the receiver in place (receiver passed by pointer).
        const string src = """
            Define object person with (the text name, the number age):
                Bind void to birthday:
                    one's age becomes one's age + 1.
                Done.
            Done.
            Define alice as a new person { the name "Alice", the age 30 }.
            Cast birthday on alice.
            State the age of alice.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Object_ValueReturningMethod_MatchesInterpreter()
    {
        const string src = """
            Define object person with (the text name, the number age):
                Bind number to doubled-age:
                    return one's age * 2.
                Done.
            Done.
            Define alice as a new person { the name "Alice", the age 21 }.
            State cast alice's doubled-age.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Object_MethodWithArgs_MatchesInterpreter()
    {
        // Method dispatch with extra args: receiver first, params follow.
        const string src = """
            Define object person with (the text name, the number age):
                Bind number to age-in, given (the number years):
                    return one's age + years.
                Done.
            Done.
            Define alice as a new person { the name "Alice", the age 30 }.
            State cast age-in on (alice, 5).
            State cast alice's age-in on (10).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Object_ValueSemantics_DefineCopies_MatchesInterpreter()
    {
        // Objects are value structs: a copy is fully independent (methods on one don't
        // touch the other), matching the interpreter's deep-copy on Define.
        const string src = """
            Define object person with (the text name, the number age):
                Bind void to birthday:
                    one's age becomes one's age + 1.
                Done.
            Done.
            Define alice as a new person { the name "Alice", the age 30 }.
            Define bob as alice.
            the name of bob becomes "Bob".
            Cast birthday on bob.
            State alice.
            State bob.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Object_MutatedParam_NoLeak_MatchesInterpreter()
    {
        // Binding is binding: an object arg is copied, so a function mutating its param
        // (via a method) does NOT change the caller's object. Compiled == interpreted.
        const string src = """
            Define object person with (the text name, the number age):
                Bind void to birthday:
                    one's age becomes one's age + 1.
                Done.
            Done.
            Bind void to age-it, given (the person p):
                Cast birthday on p.
            Done.
            Define alice as a new person { the name "Alice", the age 30 }.
            Cast age-it on (alice).
            State the age of alice.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Object_AsFunctionReturn_MatchesInterpreter()
    {
        const string src = """
            Define object person with (the text name, the number age).
            Bind the person to make-alice:
                return a new person { the name "Alice", the age 30 }.
            Done.
            Define alice as cast make-alice.
            State alice.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Object_AsRecordField_MatchesInterpreter()
    {
        // A record whose field is an object (value struct nested in a value struct).
        const string src = """
            Define object person with (the text name, the number age).
            Define alice as a new person { the name "Alice", the age 30 }.
            Define row as a record with (the who alice, the score 95).
            State row.
            State the age of the who of row.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Object_Embedding_MatchesInterpreter()
    {
        // Composition-with-promotion: promoted field/method access, embed handle, promoted
        // set, and print (own fields then embedded object) — all bit-identical.
        const string src = """
            Define object animal with (the text name, the number legs):
                Bind text to describe:
                    return one's name.
                Done.
            Done.
            Define object dog with (the number age) and as an animal.
            Define rex as a new dog { the age 3, the name "Rex", the legs 4 }.
            State rex.
            State the name of rex.
            State rex's name.
            State the age of rex.
            State cast rex's describe.
            State the animal of rex.
            the name of rex becomes "Max".
            State rex.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── Slice 5B object core: equality, unto, constructors, getters/setters ──

    [Fact]
    public void RecordEquality_Structural_MatchesInterpreter()
    {
        // Structural: field order at construction doesn't matter; series fields element-wise.
        const string src = """
            Define alice as a record with (the name "Alice", the age 30).
            Define alice2 as a record with (the age 30, the name "Alice").
            Define bob as a record with (the name "Bob", the age 30).
            If alice is alice2, state "eq". Otherwise, state "ne".
            If alice is not bob, state "ne2". Otherwise, state "eq2".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void ObjectEquality_Nominal_MatchesInterpreter()
    {
        const string src = """
            Define object person with (the text name, the number age).
            Define p1 as a new person { the name "Alice", the age 30 }.
            Define p2 as a new person { the name "Alice", the age 30 }.
            Define p3 as a new person { the name "Alice", the age 31 }.
            If p1 is p2, state "eq". Otherwise, state "ne".
            If p1 is p3, state "eq2". Otherwise, state "ne2".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void RecordEquality_SeriesField_MatchesInterpreter()
    {
        const string src = """
            Define t1 as a record with (the items a series of number with (1, 2, 3)).
            Define t2 as a record with (the items a series of number with (1, 2, 3)).
            Define t3 as a record with (the items a series of number with (1, 2, 4)).
            If t1 is t2, state "eq". Otherwise, state "ne".
            If t1 is t3, state "eq2". Otherwise, state "ne2".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Object_UntoMethods_MatchesInterpreter()
    {
        const string src = """
            Define object person with (the text name, the number age).
            Bind void to birthday unto person:
                one's age becomes one's age + 1.
            Done.
            Bind number to age-plus unto person, given (the number d):
                return one's age + d.
            Done.
            Define alice as a new person { the name "Alice", the age 30 }.
            Cast birthday on alice.
            State the age of alice.
            State cast age-plus on (alice, 100).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Object_NamedConstructor_MatchesInterpreter()
    {
        const string src = """
            Define object person with (the text name, the number age).
            Bind making a person to teen, given (the text n):
                return a new person { the name n, the age 13 }.
            Done.
            Define alice as cast teen on ("Alice").
            State alice.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Object_GettersSetters_MatchesInterpreter()
    {
        // Getter computes (no stored field); setter intercepts + clamps; self-write bypass.
        const string src = """
            Define object circle with (the number radius):
                Get area as number:
                    return one's radius * one's radius * 3.
                Done.
                Set radius given (the number r):
                    If r < 0, one's radius becomes 0.
                    Otherwise, one's radius becomes r.
                Done.
            Done.
            Define c as a new circle { the radius 2 }.
            State c's area.
            State the area of c.
            c's radius becomes 5.
            State c's radius.
            State c's area.
            c's radius becomes -3.
            State c's radius.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Object_PositionalAccessOnNamedFields_ThrowsCleanly()
    {
        // Named-field objects have no positional slots — the interpreter errors, and the
        // compiler must reject cleanly (not emit broken C).
        const string src = """
            Define object person with (the text name, the number age).
            Define alice as a new person { the name "Alice", the age 30 }.
            State the first of alice.
            """;
        var tokens  = new CufetLexer(src).Tokenize();
        var program = new Parser(tokens).Parse();
        try { new TypeChecker().Check(program); } catch (TypeException) { return; } // TC may reject first
        Assert.Throws<CompilerException>(() => new CodeGenerator().Generate(program));
    }

    [Fact]
    public void Object_TransitiveEmbedding_MatchesInterpreter()
    {
        // Multi-level embedding (employee → person → address): promoted access + set reach
        // through two levels; equality recurses the whole chain.
        const string src = """
            Define object address with (the text city).
            Define object person with (the text name) and as an address.
            Define object employee with (the number salary) and as a person.
            Define e as a new employee { the salary 100, the name "Alice", the city "Tulsa" }.
            State e.
            State the city of e.
            the city of e becomes "Norman".
            State the city of e.
            Define e2 as a new employee { the salary 100, the name "Alice", the city "Norman" }.
            If e is e2, state "eq". Otherwise, state "ne".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Object_Interface_StillDeferred_ThrowsCleanly()
    {
        // Interface conformance / dynamic dispatch remains deferred (its own slice).
        const string src = """
            Define greeter as an interface for the void function greet.
            Define object robot with (the text id) and greeter:
                Bind void to greet:
                    State one's id.
                Done.
            Done.
            """;
        var tokens  = new CufetLexer(src).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        Assert.Throws<CompilerException>(() => new CodeGenerator().Generate(program));
    }

    // Compiles source with -fsanitize=address for memory-safety verification.
    // Skipped when not on Linux (ASan reliable only with Linux gcc).
    private static string CompileWithASan(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var cSource = new CodeGenerator().Generate(program);

        var tmp     = Path.GetTempFileName();
        File.Delete(tmp);
        var cPath   = tmp + ".c";
        var binPath = tmp; // no extension on Linux
        try
        {
            File.WriteAllText(cPath, cSource);
            new GccInvoker().Compile(cPath, binPath, ["-fsanitize=address", "-g"]);
        }
        finally { try { File.Delete(cPath); } catch { } }

        try
        {
            var psi = new ProcessStartInfo(binPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0 || stderr.Contains("ERROR: AddressSanitizer"))
                throw new Exception(
                    $"ASan exit {proc.ExitCode}.\nStderr:\n{stderr}");
            return output.Replace("\r\n", "\n").TrimEnd('\n');
        }
        finally { try { File.Delete(binPath); } catch { } }
    }

    [Fact]
    public void Arena_MemorySafety_ASan_ZeroLeaksAndNoUAF()
    {
        // Validates arena correctness: compiled binary must pass AddressSanitizer
        // (zero leaks, zero use-after-free, zero dangling pointer reads).
        // Skipped on non-Linux where ASan support is unreliable.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (1, 2, 3).
                Add 4 to xs.
                Pull a rabbit.
                    Define ys as a series of number with (10, 20).
                    Add 30 to ys.
                    For each y in ys, repeat:
                        Add y to xs.
                    Done.
                Done.
                For each x in xs, repeat:
                    State x.
                Done.
            Done.
            """;
        string expected = Interpret(src);
        string actual   = CompileWithASan(src);
        Assert.Equal(expected, actual);
    }
}
