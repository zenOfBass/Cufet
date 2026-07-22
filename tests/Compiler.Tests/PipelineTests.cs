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

public class PipelineTests
{
    // Compiles source to a temp native binary, runs it (optionally feeding stdin), returns stdout.
    private static string Compile(string source, string? stdin = null)
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
                StandardOutputEncoding = System.Text.Encoding.UTF8,   // binaries print UTF-8 (e.g. em-dash messages)
                RedirectStandardInput  = stdin != null,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            if (stdin != null) { proc.StandardInput.Write(stdin); proc.StandardInput.Close(); }
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output.Replace("\r\n", "\n").TrimEnd('\n');
        }
        finally
        {
            try { File.Delete(binPath); } catch { }
        }
    }

    // Interprets source and returns stdout trimmed — the oracle. Optionally feeds stdin.
    private static string Interpret(string source, string? stdin = null)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var sb = new StringWriter();
        var reader = stdin != null ? new StringReader(stdin) : null;
        new CufetInterpreter(sb, reader).Execute(program);
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
    public void Series_OfRecords_MatchesInterpreter()
    {
        // Series of records (slice 8): value-type elements copy on insert (binding is binding),
        // remove-by-value uses value equality (same as `is`), and series equality is element-wise.
        const string src = """
            Define people as a series with (a record with (the name "Alice", the age 30), a record with (the name "Bob", the age 25)).
            State people.
            Add a record with (the name "Carol", the age 40) to people.
            State the number of people.
            For each p in people, repeat:
                State the name of p.
            Done.
            Remove a record with (the name "Bob", the age 25) from people.
            State the number of people.
            Define pa as a series with (a record with (the x 1)).
            Define pb as a series with (a record with (the x 1)).
            If pa is pb, state "eq". Otherwise, state "neq".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Series_OfRecords_InsertCopies_MatchesInterpreter()
    {
        // The element is COPIED into the series (value semantics) — mutating the original record
        // afterward does not change the stored element. Interpreter and compiler both copy now.
        const string src = """
            Define r as a record with (the x 1).
            Define s as a series with (r).
            The x of r becomes 99.
            State s.
            State r.
            """;
        Assert.Equal(Interpret(src), Compile(src));
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

    // ── Slice 5C: voidable (uniform tagged struct cvd_N { int has; T val; }) ──

    [Fact]
    public void Voidable_Number_MatchesInterpreter()
    {
        // Present → value, absent → "void"; is void / is not void; but void is (value / default).
        const string src = """
            Bind voidable number to half-if-even, given (the number n):
                If n % 2 is 0, return n / 2.
                return void.
            Done.
            Define x as cast half-if-even on (4).
            Define y as cast half-if-even on (3).
            State x.
            State y.
            If x is void, state "x-void". Otherwise, state "x-present".
            If y is void, state "y-void". Otherwise, state "y-present".
            If x is not void, state "x-notvoid". Otherwise, state "x-isvoid".
            State x but void is 0.
            State y but void is 99.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Voidable_ButVoidIs_Narrows_MatchesInterpreter()
    {
        // `but void is` yields a definite T (narrows voidable T → T).
        const string src = """
            Bind voidable number to maybe, given (the number n):
                If n > 0, return n.
                return void.
            Done.
            Define z as cast maybe on (0) but void is 42.
            Define w as cast maybe on (7) but void is 42.
            State z.
            State w.
            State z + w.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Voidable_Series_MatchesInterpreter()
    {
        // Uniform representation for a reference-type inner (series): present → value, absent → void.
        const string src = """
            Bind the voidable series of number to maybe-series, given (the number n):
                If n > 0, return a series of number with (n, n).
                return void.
            Done.
            Pull a rabbit.
                Define s as cast maybe-series on (3).
                Define t as cast maybe-series on (0).
                State s.
                State t.
                State s but void is a series of number with (0).
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Voidable_Comparisons_MatchesInterpreter()
    {
        // voidable-vs-plain-T (present && value matches) and voidable-vs-voidable equality.
        const string src = """
            Bind voidable number to maybe, given (the number n):
                If n > 0, return n.
                return void.
            Done.
            Define x as cast maybe on (5).
            Define y as cast maybe on (5).
            Define w as cast maybe on (0).
            If x is 5, state "x-is-5". Otherwise, state "x-not-5".
            If x is 6, state "x-is-6". Otherwise, state "x-not-6".
            If x is y, state "x-eq-y". Otherwise, state "x-ne-y".
            If x is w, state "x-eq-w". Otherwise, state "x-ne-w".
            If w is w, state "w-eq-w". Otherwise, state "w-ne-w".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Voidable_FlowNarrowing_MatchesInterpreter()
    {
        // Inside an `is not void` branch, the voidable variable is narrowed to plain T —
        // so arithmetic on it works, matching the interpreter's variable-level narrowing.
        const string src = """
            Bind voidable number to maybe, given (the number n):
                If n > 0, return n.
                return void.
            Done.
            Define x as cast maybe on (5).
            If x is not void, State x + 1.
            If x is not void, State x * 10.
            Define v as cast maybe on (0).
            If v is not void, State v + 100. Otherwise, State "v-absent".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── Slice 5D: maps (arena association list; lookup → voidable; on 5A/5B/5C) ──

    [Fact]
    public void Map_CoreOperations_MatchesInterpreter()
    {
        // Construct, print (insertion order), size, lookup (present/absent → voidable),
        // has key, update, append-on-new-key, for-each key, and reference (pointer) equality.
        const string src = """
            Define m as a map from text to number with ("apple": 3, "banana": 5, "cherry": 7).
            State m.
            State the size of m.
            State the entry for "banana" in m.
            State the entry for "kiwi" in m.
            State the entry for "kiwi" in m but void is 0.
            If m has a key for "apple", state "has-apple". Otherwise, state "no-apple".
            If m has a key for "kiwi", state "has-kiwi". Otherwise, state "no-kiwi".
            In m, the entry for "banana" becomes 50.
            State the entry for "banana" in m.
            In m, the entry for "date" becomes 9.
            State the size of m.
            State m.
            For each pair in m, repeat:
                State the key of pair.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Map_FractionalDecimalValues_MatchesInterpreter()
    {
        // The decimal-fidelity payoff: exact fractional values through a map (5.5+ enabled).
        const string src = """
            Define prices as a map from text to number with ("coffee": 3.50, "tea": 2.25, "cake": 4.75).
            State prices.
            State (the entry for "coffee" in prices but void is 0) + (the entry for "tea" in prices but void is 0).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Map_ParamAndForEachValue_MatchesInterpreter()
    {
        // Map as a function parameter; For each pair with `the value of pair`.
        const string src = """
            Bind number to total-of, given (the map from text to number m):
                Define sum as 0.
                For each pair in m, repeat:
                    sum becomes sum + the value of pair.
                Done.
                return sum.
            Done.
            Define prices as a map from text to number with ("a": 3.50, "b": 2.25, "c": 4.75).
            State cast total-of on (prices).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Map_OfObjects_MatchesInterpreter()
    {
        const string src = """
            Define object person with (the text name, the number age).
            Pull a rabbit.
                Define people as a map from text to person with ("alice": a new person { the name "Alice", the age 30 }).
                In people, the entry for "bob" becomes a new person { the name "Bob", the age 25 }.
                State people.
                State the age of (the entry for "alice" in people but void is a new person { the name "none", the age 0 }).
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Map_OfSeries_MatchesInterpreter()
    {
        // Map values that are themselves a reference type (series) — all in the arena.
        const string src = """
            Pull a rabbit.
                Define groups as a map from text to series of number with ("evens": a series of number with (2, 4, 6)).
                State groups.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Record_WithMapField_MatchesInterpreter()
    {
        // A map nested inside a record value struct (map is a pointer field).
        const string src = """
            Pull a rabbit.
                Define config as a record with (the label "prod", the settings a map from text to number with ("timeout": 30)).
                State config.
                State the entry for "timeout" in the settings of config but void is 0.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── Slice 6: fallibility (value-level error model; T or failure) ──

    [Fact]
    public void Fallibility_TryAndButOnFailure_MatchesInterpreter()
    {
        // Fallible fn (T or failure); Try/In case of failure catching a failure and reading
        // message + category; but on failure defaulting.
        const string src = """
            Bind number or failure to safe-div, given (the number x, the number y):
                If y is 0, return a failure "divide by zero" of category "math".
                return x / y.
            Done.
            Try to:
                Define r as cast safe-div on (10, 2).
                State r.
                Define bad as cast safe-div on (5, 0).
                State bad.
            Done.
            In case of failure:
                State the message of the failure.
                State the category of the failure.
            Done.
            State cast safe-div on (20, 4) but on failure 0.
            State cast safe-div on (20, 0) but on failure 0.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Fallibility_Propagation_MatchesInterpreter()
    {
        // `or pass the failure off` propagates a failure out of the enclosing fallible function;
        // the outer Try catches it. Also: a failure with no category → `the category is void`.
        const string src = """
            Bind number or failure to safe-div, given (the number x, the number y):
                If y is 0, return a failure "div by zero".
                return x / y.
            Done.
            Bind number or failure to compute, given (the number n):
                Define h as cast safe-div on (100, n) or pass the failure off.
                return h + 1.
            Done.
            Try to:
                Define r as cast compute on (0).
                State r.
            Done.
            In case of failure:
                State the message of the failure.
                If the category of the failure is void, state "no-category". Otherwise, state "has-category".
            Done.
            State cast compute on (0) but on failure 0.
            State cast compute on (5) but on failure 0.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Fallibility_ReadmeStyleParse_MatchesInterpreter()
    {
        // The README's parse-age shape (a validating fallible fn + Try/In case of failure),
        // adapted to avoid the deferred text ops (converted to number / joined to).
        const string src = """
            Bind number or failure to parse-positive, given (the number n):
                If n < 0, return a failure "not positive" of category "validation".
                return n.
            Done.
            Try to:
                Define good as cast parse-positive on (42).
                State good.
                Define bad as cast parse-positive on (0 - 7).
                State bad.
            Done.
            In case of failure:
                State the message of the failure.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Fallibility_ExceptionHandler_CompilesAndReRaises()
    {
        // E-prime: `In case of exception` now COMPILES (was the deferral this test used to assert).
        // The handler runs, and WITHOUT Suppress the fault re-raises — the compiled binary exits
        // nonzero after printing the handler's output, exactly like the interpreter's re-throw.
        const string src = """
            Try to:
                State 1 / 0.
            Done.
            In case of exception (the exception):
                State "caught".
                Suppress the exception.
            Done.
            State "after".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── Slice 7: text-as-full-type (immutable const char*, results arena-allocated) ──

    [Fact]
    public void Text_Operations_MatchesInterpreter()
    {
        const string src = """
            State "hello" joined to " " joined to "world".
            State 42 converted to text.
            State (0.1 + 0.2) converted to text.
            State true converted to text.
            State the length of "hello".
            If "hello world" contains "world", state "yes". Otherwise, state "no".
            State the characters from 2 to 4 of "hello".
            State the first 3 characters of "hello".
            State the last 2 characters of "hello".
            State replace "o" with "0" in "hello world".
            State "Hello World" in uppercase.
            State "Hello World" in lowercase.
            State "  spaces  " trimmed.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Text_ConvertAndFind_MatchesInterpreter()
    {
        // converted to number → voidable number (reuses 5C); position of → voidable number.
        const string src = """
            State "  42.5  " converted to number but void is 0.
            State "not a number" converted to number but void is -1.
            State "-3.14" converted to number but void is 0.
            State the position of "world" in "hello world" but void is 0.
            State the position of "xyz" in "hello world" but void is 0.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Text_ReadmeParseAge_MatchesInterpreter()
    {
        // The README parse-age integration verbatim (text→number + fallibility + joined to),
        // written in the natural void-guard idiom `If n is void, return failure. Return n.`
        // Both backends narrow n to non-void on the guard's fall-through (guard-return narrowing).
        const string src = """
            Bind number or failure to parse-age, given (the text raw):
                Define n as raw converted to number.
                If n is void, return a failure "not a number" of category "validation".
                Return n.
            Done.
            Try to:
                Define age as cast parse-age on ("thirty").
                State age.
            Done.
            In case of failure:
                State "bad input: " joined to the message of the failure.
            Done.
            Try to:
                Define age as cast parse-age on ("42").
                State age.
            Done.
            In case of failure:
                State "bad input: " joined to the message of the failure.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void GuardNarrowing_DisjunctiveVoidGuard_MatchesInterpreter()
    {
        // The REFERENCE from-pair shape: `If x is void or y is void, return failure` narrows
        // BOTH x and y to non-void on the fall-through (¬(A or B) = ¬A and ¬B). Constructing a
        // point whose fields are plain `number` proves both were unwrapped, not left voidable.
        const string src = """
            Define object point with (the number x, the number y).
            Bind making a point or failure to from-pair, given (the text sx, the text sy):
                Define x as sx converted to number.
                Define y as sy converted to number.
                If x is void or y is void, return a failure "non-numeric".
                Return a new point { the x x, the y y }.
            Done.
            Try to:
                State cast from-pair on ("3", "4").
                State cast from-pair on ("bad", "4").
            Done.
            In case of failure:
                State "failed: " joined to the message of the failure.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void GuardNarrowing_DoesNotLeakPastArm_MatchesInterpreter()
    {
        // A guard inside an if-arm narrows only within that arm; after the arm the variable is
        // voidable again. Handling it with `but void is` on both paths must agree with the oracle.
        const string src = """
            Bind number to classify, given (the number flag, the text raw):
                Define n as raw converted to number.
                If flag is 1:
                    If n is void, return 0.
                    Return n.
                Done.
                Return n but void is -1.
            Done.
            State cast classify on (1, "5").
            State cast classify on (0, "bad").
            State cast classify on (0, "9").
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Text_RuntimeStringsCompose_MatchesInterpreter()
    {
        // Runtime-built strings as map keys (compared by value) and record fields.
        const string src = """
            Pull a rabbit.
                Define uid as "user-" joined to (42 converted to text).
                Define m as a map from text to number with (uid: 100).
                In m, the entry for ("user-" joined to (99 converted to text)) becomes 7.
                State the entry for "user-42" in m but void is 0.
                State the entry for "user-99" in m but void is 0.
                Define r as a record with (the name ("Ms " joined to "Alice"), the age 30).
                State r.
                If the name of r is "Ms Alice", state "match". Otherwise, state "no-match".
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Text_SplitBy_MatchesInterpreter()
    {
        // split by → series of text (slice 8). Matches C# string.Split(string): empties kept,
        // trailing/leading delimiter → empty parts, delimiter-not-found → single whole element.
        const string src = """
            State "a,b,c" split by ",".
            State "a,,c," split by ",".
            State ",lead" split by ",".
            State "no-delimiter" split by ",".
            State "" split by ",".
            State the number of ("x=1;y=2;z=3" split by ";").
            Define parts as "10,20,30,40" split by ",".
            Define total as 0.
            For each part in parts, repeat:
                total becomes total + (part converted to number but void is 0).
            Done.
            State total.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── Slice 8: series-of-T generalization (per-element-type cser_N; split by rides on it) ──

    [Fact]
    public void Series_OfText_MatchesInterpreter()
    {
        const string src = """
            Define words as a series with ("banana", "apple", "cherry").
            Add "date" to words.
            Add "acai" to the start of words.
            State words.
            State item 2 of words.
            State the number of words.
            Remove "apple" from words.
            State words.
            For each w in words, repeat:
                State w in uppercase.
            Done.
            Define w2 as a series with ("acai", "banana", "cherry", "date").
            If words is w2, state "eq". Otherwise, state "neq".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Series_OfObjects_MatchesInterpreter()
    {
        const string src = """
            Define object point with (the number x, the number y).
            Define pts as a series with (a new point { the x 1, the y 2 }, a new point { the x 3, the y 4 }).
            State pts.
            Add a new point { the x 5, the y 6 } to pts.
            For each p in pts, repeat:
                State the x of p.
            Done.
            Remove a new point { the x 3, the y 4 } from pts.
            State the number of pts.
            Define pa as a series with (a new point { the x 1, the y 2 }).
            Define pb as a series with (a new point { the x 1, the y 2 }).
            If pa is pb, state "eq". Otherwise, state "neq".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Series_Nested_MatchesInterpreter()
    {
        // Series of series: elements are reference-type pointers (shared), equality is deep.
        const string src = """
            Define grid as a series with (a series with (1, 2, 3), a series with (4, 5, 6)).
            State grid.
            Add a series with (7, 8, 9) to grid.
            State the number of grid.
            For each row in grid, repeat:
                State the number of row.
            Done.
            State item 1 of grid.
            Define ga as a series with (a series with (1, 2)).
            Define gb as a series with (a series with (1, 2)).
            If ga is gb, state "eq". Otherwise, state "neq".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Series_OfMaps_MatchesInterpreter()
    {
        const string src = """
            Define m1 as a map from text to number with ("a": 1, "b": 2).
            Define m2 as a map from text to number with ("c": 3).
            Define ms as a series with (m1, m2).
            State the number of ms.
            For each m in ms, repeat:
                State the size of m.
            Done.
            State the entry for "a" in item 1 of ms but void is 0.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── Slice 9A: file I/O (whole-file read/write + path checks; OS-error → Cufet failure) ──

    // A path in the system temp dir — NOT a Controlled-Folder-Access-protected location like
    // Documents (where an unsigned freshly-compiled binary is blocked from writing). Forward-
    // slashed so the interpreter (.NET) and the compiled binary (fopen) resolve it identically.
    private static string WritableTempPath() =>
        Path.Combine(Path.GetTempPath(), "cufet-io-" + Guid.NewGuid().ToString("N") + ".txt")
            .Replace('\\', '/');

    // Runs a source template through the oracle, substituting {PATH}, {PATH2}, {PATH3} with fresh
    // writable temp files; asserts compiled == interpreted and cleans the files up after.
    private static void AssertFileOracle(string template, string? stdin = null)
    {
        var paths = new List<string>();
        var src = template;
        foreach (var token in new[] { "{PATH}", "{PATH2}", "{PATH3}" })
            if (src.Contains(token)) { var p = WritableTempPath(); paths.Add(p); src = src.Replace(token, p); }
        try { Assert.Equal(Interpret(src, stdin), Compile(src, stdin)); }
        finally { foreach (var p in paths) try { File.Delete(p.Replace('/', Path.DirectorySeparatorChar)); } catch { } }
    }

    [Fact]
    public void File_WriteReadRoundtrip_MatchesInterpreter()
    {
        AssertFileOracle("""
            Write "hello world" to the file "{PATH}".
            Try to:
                Define c as read all from the file "{PATH}".
                State c.
                State the length of c.
            Done.
            In case of failure:
                State "read failed".
            Done.
            """);
    }

    [Fact]
    public void File_AppendAndReadLines_MatchesInterpreter()
    {
        // ReadAllLines semantics: 3 lines, no trailing empty (append adds two more lines).
        AssertFileOracle("""
            Write "first" to the file "{PATH}".
            Append "\nsecond\nthird" to the file "{PATH}".
            Try to:
                Define lines as read all lines from the file "{PATH}".
                State the number of lines.
                For each ln in lines, repeat:
                    State "line: " joined to ln.
                Done.
            Done.
            In case of failure:
                State "fail".
            Done.
            """);
    }

    [Fact]
    public void File_PathChecks_MatchesInterpreter()
    {
        AssertFileOracle("""
            Write "x" to the file "{PATH}".
            If the path "{PATH}" exists, state "exists". Otherwise, state "gone".
            If the path "{PATH}" is a file, state "is-file". Otherwise, state "not-file".
            If the path "{PATH}" is a directory, state "is-dir". Otherwise, state "not-dir".
            If the path "no-such-path-zzz" exists, state "exists". Otherwise, state "gone".
            """);
    }

    [Fact]
    public void File_NotFound_FailureMatchesInterpreter()
    {
        // The OS-error bridge: a missing file → not-found failure with the templated message
        // (category + message reproduced bit-identically by the errno path).
        AssertFileOracle("""
            Define fallback as read all from the file "no-such-file-abc.txt" but on failure "DEFAULT".
            State fallback.
            Try to:
                Define x as read all from the file "no-such-file-abc.txt".
                State x.
            Done.
            In case of failure:
                State "cat: " joined to (the category of the failure but void is "none").
                State "msg: " joined to the message of the failure.
            Done.
            """);
    }

    // ── Slice 9B: streams + With…open + stdin (close-on-all-paths cleanup) ──

    [Fact]
    public void With_ReadWriteStreams_MatchesInterpreter()
    {
        AssertFileOracle("""
            With the file "{PATH}" open for writing as out:
                write "alpha\n" to out.
                write "beta\n" to out.
                write "gamma" to out.
            Done.
            With the file "{PATH}" open for reading as inp:
                Define first as read a line from inp.
                State first but void is "?".
                Define second as read a line from inp.
                State second but void is "?".
                State read all from inp.
            Done.
            With the file "{PATH}" open for reading as inp2:
                Define lines as read all lines from inp2.
                State the number of lines.
                For each ln in lines, repeat:
                    State "L: " joined to ln.
                Done.
            Done.
            """);
    }

    [Fact]
    public void With_ReadLine_EofIsVoid_MatchesInterpreter()
    {
        // read a line from a stream → voidable text; void at end-of-stream (a present empty line
        // is "", not void).
        AssertFileOracle("""
            With the file "{PATH}" open for writing as out:
                write "only-line" to out.
            Done.
            With the file "{PATH}" open for reading as inp:
                Define a1 as read a line from inp.
                Define a2 as read a line from inp.
                If a1 is void, state "1-void". Otherwise, state a1.
                If a2 is void, state "2-void". Otherwise, state a2.
            Done.
            """);
    }

    [Fact]
    public void With_WriteThenReturn_FlushesOnAllExits_MatchesInterpreter()
    {
        // The data-loss proof: a write inside a With block must be flushed+closed on EVERY exit —
        // normal end, return-out-of-block, propagated-failure, and Try-failure-goto. A skipped
        // fclose would lose the buffered write; reading the file back proves it landed.
        AssertFileOracle("""
            Bind text to write-then-return, given (the text loc):
                With the file loc open for writing as out:
                    write "RETURN-DATA" to out.
                    return "bailed".
                Done.
                return "normal".
            Done.
            State cast write-then-return on ("{PATH}").
            State (read all from the file "{PATH}" but on failure "LOST").

            Bind text or failure to write-then-propagate, given (the text loc):
                With the file loc open for writing as out:
                    write "PROPAGATE-DATA" to out.
                    Define x as read all from the file "no-such-qq.txt" or pass the failure off.
                    write " unreached" to out.
                    return "ok".
                Done.
                return "normal".
            Done.
            Try to:
                State cast write-then-propagate on ("{PATH2}").
            Done.
            In case of failure:
                State "caught".
            Done.
            State (read all from the file "{PATH2}" but on failure "LOST").

            Try to:
                With the file "{PATH3}" open for writing as out:
                    write "TRYGOTO-DATA" to out.
                    Define y as read all from the file "no-such-qq.txt".
                    write " unreached" to out.
                Done.
            Done.
            In case of failure:
                State "try-caught".
            Done.
            State (read all from the file "{PATH3}" but on failure "LOST").
            """);
    }

    [Fact]
    public void With_NestedReturn_ClosesBothLifo_MatchesInterpreter()
    {
        // A return out of a nested With closes both files (LIFO); code after the inner block is
        // unreached, so the outer file holds only what was written before the inner block.
        AssertFileOracle("""
            Bind text to nested, given (the text locone, the text loctwo):
                With the file locone open for writing as outA:
                    write "AAA" to outA.
                    With the file loctwo open for writing as outB:
                        write "BBB" to outB.
                        return "inner".
                    Done.
                    write " unreachedA" to outA.
                Done.
                return "normal".
            Done.
            State cast nested on ("{PATH}", "{PATH2}").
            State (read all from the file "{PATH}" but on failure "LOST-A").
            State (read all from the file "{PATH2}" but on failure "LOST-B").
            """);
    }

    [Fact]
    public void Stdin_ReadLineAndLines_MatchesInterpreter()
    {
        // `the input` is stdin; read a line + read all lines consume it. Both backends fed the
        // same input via the harness.
        const string src = """
            Define first as read a line from the input.
            State "first: " joined to (first but void is "EOF").
            Define rest as read all lines from the input.
            State the number of rest.
            For each ln in rest, repeat:
                State "got: " joined to ln.
            Done.
            """;
        Assert.Equal(Interpret(src, "hello\nworld\nthree\n"), Compile(src, "hello\nworld\nthree\n"));
    }

    // ── Slice 9C: subprocess (run) + pipes ──
    // POSIX-only (fork/exec/pipe/waitpid). LINUX-ONLY tests: on Windows the compiled binary can't
    // build (mingw has no fork), so skip — on CI Linux both interpreter (.NET) and binary run in
    // the same environment, so command resolution matches. Commands stay trivial + deterministic
    // (echo/true/false/cat/printf) so the output is environment-independent.

    [Fact]
    public void Subprocess_Run_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Launch-failure vs ran-but-nonzero: `false` is a SUCCESS record with exit-code 1; a
        // nonexistent command is a launch FAILURE (→ but-on-failure / the OS-error bridge).
        const string src = """
            Try to:
                Define r as run "echo" with arguments ("hello world").
                State "output=[" joined to (the output of r) joined to "]".
                State "exit=" joined to (the exit-code of r converted to text).
                State "errlen=" joined to (the length of (the errors of r) converted to text).
                Define t as run "true".
                State "true-exit=" joined to (the exit-code of t converted to text).
                Define f as run "false".
                State "false-exit=" joined to (the exit-code of f converted to text).
            Done.
            In case of failure:
                State "launch-failed".
            Done.
            Define fb as run "no-such-command-zzz" but on failure (a record with (the errors "", the exit-code 0, the output "LAUNCHFAIL")).
            State the output of fb.
            Try to:
                Define x as run "no-such-command-zzz".
                State the output of x.
            Done.
            In case of failure:
                State "cat: " joined to (the category of the failure but void is "none").
                State "msg: " joined to the message of the failure.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Subprocess_Pipe_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // run X | run Y: stdout → next stdin (buffered-sequential), pipefail exit (rightmost
        // nonzero), aggregated stderr; a stage's launch failure fails the whole pipe.
        const string src = """
            Try to:
                Define r as run "echo" with arguments ("hello") | run "cat".
                State "piped=[" joined to (the output of r) joined to "]".
                Define r2 as run "printf" with arguments ("one\ntwo\nthree\n") | run "cat".
                State "lines=" joined to (the number of (the output of r2 split by "\n") converted to text).
                Define r3 as run "true" | run "false".
                State "pipefail-exit=" joined to (the exit-code of r3 converted to text).
            Done.
            In case of failure:
                State "pipe-failed".
            Done.
            Try to:
                Define r4 as run "no-such-zzz" | run "cat".
                State the output of r4.
            Done.
            In case of failure:
                State "pipe-launch-failed: " joined to the message of the failure.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Subprocess_BarePipeStatement_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Bare `run X | run Y.` statement → final stdout goes to stdout (the shell pattern).
        const string src = """
            run "echo" with arguments ("streamed to stdout") | run "cat".
            State "after".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── CONC.A+B: threads + structured join + thread-local arena + channels ──
    // LINUX-ONLY (pthreads; mingw has no fork/threads). The interpreter is NOT a bit-oracle here
    // (cooperative → it deadlocks/masks races), so we assert the DETERMINISTIC INVARIANT the
    // parallel result must satisfy regardless of interleaving — not Compile == Interpret.

    [Fact]
    public void Concurrency_ParallelSum_AggregateInvariant()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // N tasks each send their i into a channel; main collects N and sums. Order-independent:
        // total == 1+2+…+8 == 36, whatever order the threads actually run.
        const string src = """
            Pull a rabbit.
                Define ch as a channel of number.
                Define n as 8.
                For each i in the range 1 to n, repeat:
                    Have rabbit start a task:
                        Send i through ch.
                    Done.
                Done.
                Define total as 0.
                For each k in the range 1 to n, repeat:
                    Define d as the delivery from ch.
                    total becomes total + (d but void is 0).
                Done.
                State total.
                Close ch.
            Done.
            """;
        Assert.Equal("36", Compile(src));
    }

    [Fact]
    public void Concurrency_DeepCopyAtSpawn_ClosesParentMutationRace()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The parent mutates a captured variable AFTER spawning; deep-copy-at-spawn means the task
        // sees its spawn-time snapshot (5), not the parent's later 999. (The cooperative interpreter
        // masks this race and would yield 999 — the divergence true parallelism exposes.)
        const string src = """
            Pull a rabbit.
                Define ch as a channel of number.
                Define x as 5.
                Have rabbit start a task:
                    Send x through ch.
                Done.
                x becomes 999.
                Define d as the delivery from ch.
                State d but void is -1.
            Done.
            """;
        Assert.Equal("5", Compile(src));
    }

    // ── CONC.F: fan-out validation (the capstone — the work-queue finding comes home) ──
    // N worker tasks all pull from ONE shared channel (the work queue), doubling each item and
    // fanning results back to a second channel; a collector sums them. Under TRUE parallelism the
    // workers genuinely contend for the queue and the work DISTRIBUTES across them (WSL-verified,
    // e.g. 18/8/4) — vs the cooperative interpreter's one-drains-all (30/0/0). Distribution is
    // nondeterministic, so we assert the ORDER-INDEPENDENT INVARIANT: every item is processed
    // exactly once ⇒ the sum is deterministic (2·(1+…+20) == 420) regardless of who got what.

    private const string FanOutWorkQueue = """
        Pull a rabbit.
            Define work    as a channel of number.
            Define results as a channel of number.
            Define n       as 20.
            Have rabbit start a task as w1:
                Define job as the delivery from work.
                While job is not void, repeat:
                    Send ((job but void is 0) * 2) through results.
                    job becomes the delivery from work.
                Done.
            Done.
            Have rabbit start a task as w2:
                Define job as the delivery from work.
                While job is not void, repeat:
                    Send ((job but void is 0) * 2) through results.
                    job becomes the delivery from work.
                Done.
            Done.
            Have rabbit start a task as w3:
                Define job as the delivery from work.
                While job is not void, repeat:
                    Send ((job but void is 0) * 2) through results.
                    job becomes the delivery from work.
                Done.
            Done.
            Have rabbit start a task as producer:
                Define i as 1.
                While i is n or less, repeat:
                    Send i through work.
                    i becomes i + 1.
                Done.
                Close work.
            Done.
            Have rabbit start a task as collector:
                Define total as 0.
                Define count as 0.
                Define got as the delivery from results.
                While got is not void, repeat:
                    total becomes total + (got but void is 0).
                    count becomes count + 1.
                    If count is n, Stop.
                    got becomes the delivery from results.
                Done.
                State total.
            Done.
        Done.
        """;

    [Fact]
    public void Concurrency_FanOut_WorkQueue_EachItemProcessedOnce()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The correctness invariant: 20 items each processed exactly once by SOME worker ⇒ the
        // fanned-in sum is 2·(1+…+20) == 420, whatever the (nondeterministic) work distribution.
        // Proves the shared-channel dequeue under N-worker contention never double-delivers or drops.
        Assert.Equal("420", Compile(FanOutWorkQueue));
    }

    [Fact]
    public void Concurrency_FanOut_WorkQueue_MemorySafety_ASan()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The sharpest memory test: N workers contending on one channel + a results channel + a
        // collector series, all under ASan/LSan. close-with-contention wakes every blocked worker
        // (broadcast → void → exit), the structured join reaps all five tasks, every channel/arena
        // frees. Zero leaks / UAF, and the aggregate invariant still holds.
        Assert.Equal("420", CompileWithASan(FanOutWorkQueue));
    }

    // ── Arc 1A: book substrate + exact-decimal math + `sorted` ──
    // Books are builtin + compile-time-resolved. These are ordinary Compile == Interpret oracle
    // tests and run on BOTH platforms (no POSIX). Math totals are exact-decimal (bit-identical to
    // the interpreter's decimal overloads); `sorted` is a stable natural/by-field sort.

    [Fact]
    public void Book_Math_ExactFunctions()
    {
        const string src = """
            Pull a book on math.
                State math's floor of 3.99.
                State math's floor of -3.1.
                State math's ceiling of 3.01.
                State math's ceiling of -3.9.
                State math's round of 2.5.
                State math's round of -2.5.
                State math's round of 2.4.
                State math's absolute value of -7.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Book_Math_Constants_BakedExact()
    {
        // pi/e are baked from (decimal)Math.PI / (decimal)Math.E in the compiler (itself .NET), so
        // the CufetDec is bit-identical to the interpreter's stored constant.
        const string src = """
            Pull a book on math.
                State math's pi.
                State math's e.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Book_Math_AliasedPull()
    {
        // `Pull a book on math as the m.` — the alias resolves book-member dispatch just the same.
        const string src = """
            Pull a book on math as the m.
                State m's floor of 3.7.
                State m's pi.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Sort_Numbers_AscendingAndReverse()
    {
        const string src = """
            Define nums as a series with (3, 1, 4, 1, 5, 9, 2, 6).
            State nums sorted.
            State nums sorted in reverse.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Sort_Text_OrdinalOrder()
    {
        const string src = """
            Define words as a series of text with ("banana", "apple", "cherry", "apple").
            State words sorted.
            State words sorted in reverse.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Sort_ByField_Stable()
    {
        // Stability proof: Bob and Cy both have age 30; sorted by age they keep insertion order
        // (Bob before Cy). A stable sort (not qsort) is required to match the interpreter's OrderBy.
        const string src = """
            Define party as a series of records like (the text name, the number age).
            Add a record with (the name "Bob", the age 30) to party.
            Add a record with (the name "Ann", the age 25) to party.
            Add a record with (the name "Cy", the age 30) to party.
            State party sorted by age.
            State party sorted by name in reverse.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── Arc 1B: math transcendentals — the decimal↔double bridge ──
    // sqrt/log/power are double-backed (the settled fork — the interpreter is Math.Sqrt-backed).
    // The bridge replicates .NET 10's DecCalc conversions bit-for-bit: VarR8FromDec on the way in,
    // VarDecFromR8 (15 significant digits, half-even at the 15th) on the way out. sqrt is IEEE-
    // correctly-rounded (C sqrt == .NET Math.Sqrt everywhere); log matched a 300-input corpus.
    // CAVEAT (measured, documented): `power` with fractional exponents is last-ULP libm-dependent —
    // .NET's own Math.Pow IS the platform libm, so 2/240 corpus inputs (e.g. 2^2.65) differ by ±1
    // in the 15th significant digit between ucrt (.NET-on-Windows) and glibc/mingw. These tests use
    // the corpus-verified-matching pow families; the divergence family is documented, not asserted.

    [Fact]
    public void Book_Math_Sqrt_BridgeOracleMatch()
    {
        // 130 sqrt values incl. fractions — every one exercises the 15-sig-digit bridge. sqrt is
        // IEEE-correctly-rounded, so any mismatch would be a BRIDGE bug, not libm.
        const string src = """
            Pull a book on math.
                For each n in the range 1 to 60, repeat:
                    State (math's square root of n) but void is -1.
                    State (math's square root of (n / 7)) but void is -1.
                Done.
                State (math's square root of 2) but void is -1.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Book_Math_Log_BridgeOracleMatch()
    {
        const string src = """
            Pull a book on math.
                For each n in the range 1 to 60, repeat:
                    State (math's log of n) but void is -1.
                    State (math's log of (n / 7)) but void is -1.
                Done.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Book_Math_Power_VerifiedFamilies()
    {
        // Integer/exact powers + the corpus-verified cube and square-root-via-pow families.
        // (2^fractional is the measured ±1-ULP libm-divergent family — documented, not asserted.)
        const string src = """
            Pull a book on math.
                State (math's power of (2, 10)) but void is -1.
                State (math's power of (10, 28)) but void is -1.
                For each n in the range 1 to 40, repeat:
                    State (math's power of (n / 10, 3)) but void is -1.
                    State (math's power of (n, 0.5)) but void is -1.
                Done.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Book_Math_Transcendental_VoidPaths()
    {
        // MathPartial semantics: NaN/±Inf → void (sqrt of negative, log of 0/negative, pow NaN),
        // and decimal-OVERFLOW → void (pow(10,1000) is double-inf; pow(10,30) is a FINITE double
        // that overflows decimal in the conversion — the exp>96 path). All flow as voidable number.
        const string src = """
            Pull a book on math.
                State (math's square root of -1) but void is -999.
                State (math's log of 0) but void is -999.
                State (math's log of -1) but void is -999.
                State (math's power of (-1, 0.5)) but void is -999.
                State (math's power of (10, 1000)) but void is -999.
                State (math's power of (10, 30)) but void is -999.
                State (math's power of (10, 28)) but void is -999.
                Define r as math's square root of 16.
                If r is not void, State "sixteen has a root".
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── Arc 1C: collections aggregates (mechanical — reductions on the compiled series model) ──
    // minimum/maximum/average → voidable number (void on empty, reuses 5C); min/max keep the first
    // of ties; average = sequential exact-decimal sum then ONE divide (LINQ Sum semantics — no
    // double bridge, so fractional averages are EXACT). unique = element-type-preserving first-
    // occurrence dedup via per-type value equality (the series-of-T payoff).

    [Fact]
    public void Collections_MinMaxAverage_ExactDecimal()
    {
        // average of (0.1, 0.2, 0.3) is EXACTLY 0.2 (software decimal — no float drift), and the
        // repeating division (100+3+3)/3 matches the interpreter's 28-digit decimal quotient.
        const string src = """
            Pull a book on collections.
                Define xs as a series with (3, 1, 4, 1, 5, 9, 2, 6).
                State (cast collections's minimum of (xs)) but void is -1.
                State (cast collections's maximum of (xs)) but void is -1.
                State (cast collections's average of (xs)) but void is -1.
                Define fr as a series with (0.1, 0.2, 0.3).
                State (cast collections's average of (fr)) but void is -1.
                Define rep as a series with (100, 3, 3).
                State (cast collections's average of (rep)) but void is -1.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Collections_Aggregates_VoidOnEmpty()
    {
        const string src = """
            Pull a book on collections.
                Define empty as a series of number with ().
                State (cast collections's minimum of (empty)) but void is -999.
                State (cast collections's maximum of (empty)) but void is -999.
                State (cast collections's average of (empty)) but void is -999.
                Define r as cast collections's average of (empty).
                If r is void, State "empty average is void".
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Collections_Unique_FirstOccurrenceAcrossTypes()
    {
        // Numbers, text, and records (structural equality — the two Bob-30s dedup; Ann-25 and
        // Ann-26 stay distinct). First-occurrence order preserved in every case.
        const string src = """
            Pull a book on collections.
                Define nq as a series with (3, 1, 3, 2, 1).
                State cast collections's unique of (nq).
                Define tq as a series of text with ("b", "a", "b", "c", "a").
                State cast collections's unique of (tq).
                Define party as a series of records like (the text name, the number age).
                Add a record with (the name "Bob", the age 30) to party.
                Add a record with (the name "Ann", the age 25) to party.
                Add a record with (the name "Bob", the age 30) to party.
                Add a record with (the name "Ann", the age 26) to party.
                State cast collections's unique of (party).
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Collections_Unique_MemorySafety_ASan()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // unique builds a NEW arena series (like sorted) — must free cleanly at scope exit.
        const string src = """
            Pull a book on collections.
                Define xs as a series with (5, 3, 5, 1, 3, 5, 2).
                Define u as cast collections's unique of (xs).
                State u.
            Done.
            """;
        Assert.Equal(Interpret(src), CompileWithASan(src));
    }

    // ── Arc 1D: matrix (the new-type capstone of the collections book) ──
    // CufetMatrix = arena reference type (shared on assign, matching the interpreter — matrices are
    // immutable after construction). All arithmetic is EXACT CufetDec. Dimension mismatch is a Cufet
    // FAILURE (category "dimension-mismatch") the typechecker requires handling for. Printing uses
    // the FormatMatrix format added to BOTH backends this slice: matrix((1, 2), (3, 4)).

    [Fact]
    public void Matrix_LiteralSizedAccess_OracleMatch()
    {
        const string src = """
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                State m.
                State the rows of m.
                State the columns of m.
                State the item at (1, 2) of m.
                State the item at (2, 1) of m.
                Define g as a matrix with 2 by 3 filled with 7.
                State g.
                Define z as a matrix with 2 by 2.
                State z.
                Define fr as a matrix with ((0.1, 0.2), (1.5, -2.75)).
                State fr.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Matrix_Arithmetic_ExactDecimal_InclNonSquareMultiply()
    {
        // add/sub with fractional elements (exact decimal), square multiply, and the 2×3 · 3×2
        // real matrix product — accumulation order replicates the interpreter, so bit-identical.
        const string src = """
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                Define fr as a matrix with ((0.1, 0.2), (1.5, -2.75)).
                Try to:
                    Define s as m + fr.
                    State s.
                    Define d as m - fr.
                    State d.
                    Define p as m * m.
                    State p.
                    Define ns1 as a matrix with ((1, 2, 3), (4, 5, 6)).
                    Define ns2 as a matrix with ((7, 8), (9, 10), (11, 12)).
                    State ns1 * ns2.
                Done.
                In case of failure:
                    State "unexpected".
                Done.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Matrix_DimensionMismatch_IsCufetFailure()
    {
        // Mismatched add and non-conforming multiply → failures with the interpreter's exact
        // deterministic messages + the "dimension-mismatch" category, caught by Try.
        const string src = """
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                Define wide as a matrix with ((1, 2, 3), (4, 5, 6)).
                Try to:
                    Define oops as m + wide.
                    State "no failure".
                Done.
                In case of failure:
                    State the message of the failure.
                    State the category of the failure but void is "none".
                Done.
                Try to:
                    Define oops2 as wide * wide.
                    State "no failure".
                Done.
                In case of failure:
                    State the message of the failure.
                Done.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Matrix_TransposeAndComposition()
    {
        // transpose (incl. non-square), matrix in a series (reference element), share-on-assign.
        const string src = """
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                State cast collections's transpose of (m).
                Define wide as a matrix with ((1, 2, 3), (4, 5, 6)).
                State cast collections's transpose of (wide).
                Define g as a matrix with 2 by 2 filled with 9.
                Define ms as a series with (m, g).
                State the item at (1, 1) of first of ms.
                Define m2 as m.
                State the item at (2, 2) of m2.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Matrix_FunctionParamsAndReturns()
    {
        // A matrix-typed function must live INSIDE the collections pull (the type isn't in scope
        // outside) — the compiler hoists Pull-body binds to free functions (books are compile-time).
        const string src = """
            Pull a book on collections.
                Bind the matrix to double-it, given (the matrix m):
                    Define d as (m + m) but on failure (m).
                    return d.
                Done.
                Define src as a matrix with ((1.5, 2), (3, 4.25)).
                Define doubled as cast double-it on (src).
                State doubled.
                State the item at (2, 2) of doubled.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Matrix_MemorySafety_ASan()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Matrices + arithmetic results + transposes are all arena allocations — everything frees
        // at Done., zero leaks/UAF.
        const string src = """
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                Try to:
                    Define p as m * m.
                    State the item at (2, 2) of p.
                Done.
                In case of failure:
                    State "unexpected".
                Done.
                State cast collections's transpose of (m).
            Done.
            """;
        Assert.Equal(Interpret(src), CompileWithASan(src));
    }

    // ── Arc 1E: chance (the last Arc-1 slice) — INVARIANT-tested per the settled fork ──
    // Randomness is NOT bit-identical across backends (unseeded System.Random is xoshiro256**,
    // nondeterministic-by-design; the compiler uses its own xorshift64*). So the program CHECKS its
    // own invariants and prints deterministic PASS lines: range+inclusive bounds, shuffle-is-a-
    // permutation (multiset), item-membership, empty→void, guess domain, seeded self-consistency
    // (same seed → same sequence WITHIN a backend), and element-type generality (record shuffle).

    private const string ChanceInvariantBattery = """
        Pull a book on chance.
            Define range-ok as true.
            For each n in the range 1 to 200, repeat:
                Define r as a random number from 1 to 6.
                If r is less than 1, range-ok becomes false.
                If r is greater than 6, range-ok becomes false.
            Done.
            If range-ok, State "range PASS". Otherwise, State "range FAIL".
            Define pin as a random number from 5 to 5.
            If pin is 5, State "inclusive PASS". Otherwise, State "inclusive FAIL".
            Define xs as a series with (1, 2, 3, 4, 5, 6, 7, 8, 9, 10).
            Define sh as randomly shuffled xs.
            Define perm-ok as true.
            If (the number of sh) is not 10, perm-ok becomes false.
            For each want in xs, repeat:
                Define found as false.
                For each got in sh, repeat:
                    If got is want, found becomes true.
                Done.
                If not found, perm-ok becomes false.
            Done.
            If perm-ok, State "permutation PASS". Otherwise, State "permutation FAIL".
            Define words as a series of text with ("alpha", "beta", "gamma").
            Define pick as a random item from words.
            Define pv as pick but void is "NONE".
            Define member-ok as false.
            For each w in words, repeat:
                If pv is w, member-ok becomes true.
            Done.
            If member-ok, State "membership PASS". Otherwise, State "membership FAIL".
            Define empty as a series of number with ().
            Define nothing as a random item from empty.
            If nothing is void, State "empty-void PASS". Otherwise, State "empty-void FAIL".
            Define g as a random guess.
            If g, State "guess in domain". Otherwise, State "guess in domain".
            Seed the chance with 42.
            Define s1 as a series of number with ().
            For each n in the range 1 to 5, repeat:
                Add (a random number from 1 to 1000000) to s1.
            Done.
            Seed the chance with 42.
            Define s2 as a series of number with ().
            For each n in the range 1 to 5, repeat:
                Add (a random number from 1 to 1000000) to s2.
            Done.
            If s1 is s2, State "seed self-consistent PASS". Otherwise, State "seed self-consistent FAIL".
            Define party as a series of records like (the text name, the number age).
            Add a record with (the name "Ann", the age 25) to party.
            Add a record with (the name "Bob", the age 30) to party.
            Add a record with (the name "Cy", the age 35) to party.
            Define shp as randomly shuffled party.
            If (the number of shp) is 3, State "record-shuffle PASS". Otherwise, State "record-shuffle FAIL".
        Done.
        """;

    private const string ChanceExpectedPass =
        "range PASS\ninclusive PASS\npermutation PASS\nmembership PASS\nempty-void PASS\n" +
        "guess in domain\nseed self-consistent PASS\nrecord-shuffle PASS";

    [Fact]
    public void Chance_Invariants_CompiledAllPass()
    {
        Assert.Equal(ChanceExpectedPass, Compile(ChanceInvariantBattery));
    }

    [Fact]
    public void Chance_Invariants_InterpretedAllPass()
    {
        // The same invariants hold in the oracle — each backend is checked against the PROPERTY,
        // not against the other's bit-stream (the CONC.5 discipline for nondeterministic features).
        Assert.Equal(ChanceExpectedPass, Interpret(ChanceInvariantBattery));
    }

    [Fact]
    public void Chance_Shuffle_MemorySafety_ASan()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // randomly shuffled builds a NEW arena series (like sorted/unique) — must free cleanly.
        const string src = """
            Pull a book on chance.
                Define xs as a series with (1, 2, 3, 4, 5, 6, 7, 8).
                Define sh as randomly shuffled xs.
                If (the number of sh) is 8, State "ok". Otherwise, State "bad".
            Done.
            """;
        Assert.Equal("ok", CompileWithASan(src));
    }

    // ── Cleanup slice: the misc smalls (env vars, is-a-type, voidable maps, directory contents) ──

    [Fact]
    public void EnvVar_UnsetIsVoid_PresentMatches()
    {
        // The compiled binary is a child of the test process, so it inherits the same environment —
        // the PATH value oracle-matches exactly; an unset name is void (both backends).
        const string src = """
            Define unset as the environment variable "CUFET_DEFINITELY_UNSET_XYZ".
            If unset is void, State "unset is void". Otherwise, State "FAIL".
            State the environment variable "PATH" but void is "none".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void IsTypeCheck_StaticConstant_AndVoidableRuntime()
    {
        // Static targets are compile-time constants (the monomorphic model); a VOIDABLE target is
        // the one dynamic case — `v is a number` ⇔ present, and the positive arm NARROWS (v + 1
        // reads the inner). Kind-erasure matches the interpreter (series by kind, element-erased).
        const string src = """
            Define n as 5.
            If n is a number, State "number yes". Otherwise, State "FAIL".
            If n is a text, State "FAIL2". Otherwise, State "not text".
            If n is not a text, State "negated ok". Otherwise, State "FAIL3".
            Define words as a series of text with ("x").
            If words is a series of text, State "series kind ok". Otherwise, State "FAIL4".
            Define v as "42" converted to number.
            If v is a number:
                State v + 1.
            Done.
            Define w as "abc" converted to number.
            If w is a number, State "FAIL5". Otherwise, State "unparsed not a number".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void VoidableValuedMaps_LookupFlattens_EntryKeyDiverge()
    {
        // `map from text to voidable number`: lookup FLATTENS (never voidable-voidable — an absent
        // key and a stored void both read as void); `has a key` sees the explicit-void slot but
        // `has an entry` does NOT (the interpreter's is-not-VoidValue rule).
        const string src = """
            Define m as a map from text to voidable number with ().
            In m, the entry for "present" becomes 7.
            In m, the entry for "explicit-void" becomes void.
            If m has a key for "explicit-void", State "void slot has key". Otherwise, State "FAIL".
            If m has an entry for "explicit-void", State "FAIL2". Otherwise, State "void slot has no entry".
            If m has an entry for "present", State "entry present ok".
            State (the entry for "present" in m) but void is -1.
            State (the entry for "nowhere" in m) but void is -99.
            State (the entry for "explicit-void" in m) but void is -7.
            State the size of m.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void DirectoryContents_SortedListing_AndFailurePaths()
    {
        // Both backends SORT entries (ordinal) — the raw OS order is filesystem-dependent, so
        // sorting defines the undefined (normalize-the-unobservable, the FormatRecord move). The
        // full paths use the platform separator, identical same-platform. Failures: not-found
        // message + category, and but-on-failure composes.
        var dir = Path.Combine(Path.GetTempPath(), "cufet-dirtest-" + Guid.NewGuid().ToString("N")[..8])
                      .Replace('\\', '/');
        Directory.CreateDirectory(dir);
        File.WriteAllText(dir + "/zeta.txt", "z");
        File.WriteAllText(dir + "/alpha.txt", "a");
        File.WriteAllText(dir + "/mid.log", "m");
        try
        {
            string src = $"""
                Try to:
                    Define entries as the contents of the directory "{dir}".
                    State entries.
                    State the number of entries.
                Done.
                In case of failure:
                    State "unexpected".
                Done.
                Try to:
                    Define nope as the contents of the directory "{dir}-definitely-not-here".
                    State "no failure".
                Done.
                In case of failure:
                    State the message of the failure.
                    State the category of the failure but void is "none".
                Done.
                """;
            Assert.Equal(Interpret(src), Compile(src));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void DirectoryContents_MemorySafety_ASan()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The listing's arena strings + array free cleanly at scope exit.
        var dir = Path.Combine(Path.GetTempPath(), "cufet-dirasan-" + Guid.NewGuid().ToString("N")[..8])
                      .Replace('\\', '/');
        Directory.CreateDirectory(dir);
        File.WriteAllText(dir + "/one.txt", "1");
        File.WriteAllText(dir + "/two.txt", "2");
        try
        {
            string src = $"""
                Try to:
                    Define entries as the contents of the directory "{dir}".
                    State the number of entries.
                Done.
                In case of failure:
                    State "unexpected".
                Done.
                """;
            Assert.Equal(Interpret(src), CompileWithASan(src));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ── CONC.E-prime: the exception path (In case of exception / Suppress) ──
    // setjmp/longjmp over SOFTWARE faults (Cufet's numbers are software decimals — div/mod-by-zero
    // and OOB are detected checks, not hardware signals). Handlers are a per-thread jmp_buf stack
    // (nested Trys nest; innermost wins); the handler RE-RAISES by default unless it Suppresses.
    // Fault messages replicate the interpreter's RuntimeException text, line numbers included.

    [Fact]
    public void Exception_FaultSites_CaughtWithExactMessages()
    {
        const string src = """
            Try to:
                Define x as 1 / 0.
            Done.
            In case of exception (the exception):
                State the message of the exception.
                Suppress the exception.
            Done.
            Try to:
                Define y as 7 % 0.
            Done.
            In case of exception (the exception):
                State the message of the exception.
                Suppress the exception.
            Done.
            Try to:
                Define xs as a series with (1, 2, 3).
                State item 9 of xs.
            Done.
            In case of exception (the exception):
                State the message of the exception.
                Suppress the exception.
            Done.
            Try to:
                Define empty as a series of number with ().
                State the last of empty.
            Done.
            In case of exception (the exception):
                State the message of the exception.
                Suppress the exception.
            Done.
            State "done".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Exception_NestedInnermostWins_ThenReRaisesOutward()
    {
        // The inner handler catches first; without Suppress it re-raises the SAME exception to the
        // outer handler (same message). The statement after the inner Try is not reached.
        const string src = """
            Try to:
                Try to:
                    Define z as 5 / 0.
                Done.
                In case of exception (the exception):
                    State "inner caught".
                    State the message of the exception.
                Done.
                State "not reached".
            Done.
            In case of exception (the exception):
                State "outer caught the re-raise".
                State the message of the exception.
                Suppress the exception.
            Done.
            State "after nesting".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Exception_ComposesWithFailureHandler()
    {
        // One Try, BOTH handlers: a value failure routes to In case of failure; a runtime fault
        // routes to In case of exception. The two mechanisms (goto vs longjmp) stay independent.
        const string src = """
            Bind the number or failure to risky, given (the fact which):
                If which, return a failure "a value failure" of category "biz".
                return 5.
            Done.
            Try to:
                Define r1 as cast risky on (true).
                State "no failure".
            Done.
            In case of failure:
                State "failure handler: " joined to the message of the failure.
            Done.
            In case of exception (the exception):
                State "exception handler (wrong)".
                Suppress the exception.
            Done.
            Try to:
                Define r2 as cast risky on (false).
                State r2.
                Define r3 as r2 / 0.
            Done.
            In case of failure:
                State "failure handler (wrong)".
            Done.
            In case of exception (the exception):
                State "exception handler: " joined to the message of the exception.
                Suppress the exception.
            Done.
            State "end".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Exception_CleanupOnLongjmp_FileFlushedAndClosed()
    {
        // ★ The crux: a fault INSIDE a With-open block, caught by an OUTER handler — the longjmp
        // jumps past the emit-time fclose, so the RUNTIME registry must flush+close the file. The
        // read-back proves no data loss (the 9B proof, applied to the nonlocal-jump path).
        var path = (Path.GetTempPath().Replace('\\', '/').TrimEnd('/')) + "/cufet-eprime-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        try
        {
            string src = $"""
                Try to:
                    With the file "{path}" open for writing as out:
                        write "written before the fault" to out.
                        Define x as 1 / 0.
                        write "never written" to out.
                    Done.
                Done.
                In case of exception (the exception):
                    State "caught: " joined to the message of the exception.
                    Suppress the exception.
                Done.
                Try to:
                    Define back as read all from the file "{path}".
                    State back.
                Done.
                In case of failure:
                    State "read failed".
                Done.
                """;
            Assert.Equal(Interpret(src), Compile(src));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Exception_Uncaught_KeepsPrintExitBehavior()
    {
        // No handler → the pre-exception behavior is unchanged: message to stderr, exit 1. The
        // interpreter throws RuntimeException; the compiled binary prints what ran before the fault.
        const string src = """
            State "before".
            Define x as 1 / 0.
            State "after".
            """;
        Assert.Throws<RuntimeException>(() => Interpret(src));
        Assert.Equal("before", Compile(src));
    }

    [Fact]
    public void Exception_LoopScopedTry_SuppressAndContinue()
    {
        // A Try inside a loop: each iteration's handler catches independently; setjmp-modified
        // locals (the counter) survive the longjmps (gcc's returns_twice conservatism, verified).
        const string src = """
            Define counter as 0.
            While counter is less than 3, repeat:
                Try to:
                    Define q as 1 / (counter - 1).
                    State q.
                Done.
                In case of exception (the exception):
                    State "loop caught".
                    Suppress the exception.
                Done.
                counter becomes counter + 1.
            Done.
            State "done".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Exception_MemorySafety_ASan()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The registries free exactly-once across the longjmp (no double-close/free; arena pops).
        const string src = """
            Try to:
                Define xs as a series with (1, 2, 3).
                State item 99 of xs.
            Done.
            In case of exception (the exception):
                State "caught oob".
                Suppress the exception.
            Done.
            State "done".
            """;
        Assert.Equal(Interpret(src), CompileWithASan(src));
    }

    [Fact]
    public void Sort_MemorySafety_ASan()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // `sorted` builds a NEW arena series (non-mutating); it must free cleanly at scope exit.
        const string src = """
            Define nums as a series with (5, 3, 8, 1, 9, 2, 7).
            Define s as nums sorted.
            State s.
            """;
        Assert.Equal(Interpret(src), CompileWithASan(src));
    }

    // ── CONC.C: named tasks + `the awaited result of` (result crosses task → awaiter) ──
    // LINUX-ONLY (pthreads). Unlike the channel spawn-collect pattern, an AWAIT drains the
    // cooperative interpreter deterministically (no deadlock) and the awaited VALUE is
    // deterministic regardless of timing — so these ARE true Compile == Interpret oracle tests.

    [Fact]
    public void Concurrency_AwaitedResult_Number()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A named task computes a value and returns it; the awaiter joins, deep-copies the
        // heap-bridged result into itself, and prints it. Deterministic result: 42.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as fetcher:
                    Define x as 21 + 21.
                    return x.
                Done.
                Define answer as the awaited result of fetcher.
                State answer.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Concurrency_DoubleAwait_CachesResult()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Awaiting the same task twice joins it ONCE (guarded by the joined-flag) and reads the
        // cached result the second time — the task body ("task ran") runs exactly once. Proves
        // no double pthread_join (undefined) and no double-free of the result bridge.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as counter:
                    State "task ran".
                    return 7.
                Done.
                Define r1 as the awaited result of counter.
                Define r2 as the awaited result of counter.
                State r1.
                State r2.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Concurrency_TwoTasks_AwaitBoth()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Two named tasks, each result crosses its own task → awaiter boundary; the awaiter sums
        // them (5 + 10 == 15). Each join synchronizes its own result independently.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as t1:
                    return 5.
                Done.
                Have rabbit start a task as t2:
                    return 10.
                Done.
                Define r1 as the awaited result of t1.
                Define r2 as the awaited result of t2.
                State r1 + r2.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Concurrency_FallibleTask_HandledAtAwaitSite()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A task whose result is `number or failure`: the failing path returns a failure, and the
        // awaited result flows through the SAME fallible machinery as a fallible call — `but on
        // failure` supplies the default (99). Reuses slice-6 `cfl_N` end to end.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as risky:
                    return a failure "task failed" of category "err".
                    return 0.
                Done.
                Define r as the awaited result of risky but on failure (99).
                State r.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Concurrency_NeverAwaitedNamedTask_ASan_FreesResultBridge()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A named task that returns a value but is NEVER awaited still runs and joins at the
        // rabbit's Done.; the structured teardown captures + frees its heap-bridged result so it
        // does not leak. ASan/LSan must be clean (the free-on-all-paths proof for un-awaited results).
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as sideEffect:
                    State "side effect ran".
                    return 0.
                Done.
            Done.
            """;
        Assert.Equal(Interpret(src), CompileWithASan(src));
    }

    // ── Text/reference task results (channel-of-T follow-on: `the awaited result of` beyond num/fact) ──
    // The task→awaiter boundary is the third direction of the SAME heap bridge as a channel send: on
    // return the result is deep-copied to a malloc'd envelope (channel-of-T copy-family), pthread_exit'd,
    // and the await joins → arena-copies into the awaiter → frees the envelope. An await drains the
    // interpreter deterministically, so the awaited VALUE is deterministic ⇒ true Compile==Interpret.

    [Fact]
    public void Concurrency_AwaitedResult_Text()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as greeter:
                    Define s as "hello " joined to "world".
                    return s.
                Done.
                Define got as the awaited result of greeter.
                State got.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Concurrency_AwaitedResult_Series()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The result is a series (reference type). The task's arena is popped after return, so the
        // heap bridge + arena copy must be genuinely deep — a shallow copy would be a use-after-free
        // (ASan would catch it); a clean, correct read proves the deep copy crossed arena-independently.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as maker:
                    Define xs as a series of number with (1, 2, 3).
                    return xs.
                Done.
                Define got as the awaited result of maker.
                State "len=" joined to ((the number of got) converted to text) joined to " first=" joined to ((the first of got) converted to text).
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Concurrency_AwaitedResult_ObjectWithSeriesField_DeepCopy()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The crux of a GENUINELY-deep result copy: an object whose field is a series. The whole
        // nested structure must cross the task→awaiter boundary and survive the task's arena teardown.
        const string src = """
            Define object bundle with (the text label, the series of number nums).
            Pull a rabbit.
                Have rabbit start a task as maker:
                    Define ns as a series of number with (10, 20, 30).
                    Define b as a new bundle { the label "made", the nums ns }.
                    return b.
                Done.
                Define got as the awaited result of maker.
                State (the label of got) joined to " nums-len=" joined to ((the number of (the nums of got)) converted to text).
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Concurrency_AwaitedResult_Map()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as maker:
                    Define m as a map from text to number with ("a" : 1, "b" : 2).
                    return m.
                Done.
                Define got as the awaited result of maker.
                State "size=" joined to ((the size of got) converted to text) joined to " a=" joined to ((the entry for "a" in got but void is 0) converted to text).
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Concurrency_DoubleAwait_ReferenceResult()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Double-await of a REFERENCE result: the body ("ran") runs exactly once, the join happens
        // once, and the cached arena copy is read on both awaits — no double-join, no double-free.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as maker:
                    State "ran".
                    return a series of text with ("a", "b").
                Done.
                Define r1 as the awaited result of maker.
                Define r2 as the awaited result of maker.
                State (the first of r1).
                State (the second of r2).
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Concurrency_FallibleTask_TextInner_ComposesWithButOnFailure()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A `text or failure` task result: the wrapper (cfl) composes with the reference inner (text)
        // — the deep-copy family handles the inner T while the failable machinery (5C/6) is untouched.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as risky:
                    If 1 is 2, return a failure "nope" of category "err".
                    return "recovered text".
                Done.
                Define got as the awaited result of risky but on failure ("defaulted").
                State got.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Concurrency_NeverAwaitedReferenceResult_ASan_FreesNestedBridge()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A named task whose REFERENCE result is never awaited: the Done.-join teardown must free the
        // whole heap bridge THROUGH the slot's freeenv (not just the envelope pointer), so the nested
        // series allocations free too. ASan/LSan clean = the free-on-all-paths proof for reference results.
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as maker:
                    State "side effect".
                    return a series of text with ("x", "y", "z").
                Done.
                State "done".
            Done.
            """;
        Assert.Equal(Interpret(src), CompileWithASan(src));
    }

    // ── CONC.D: task pipes (function stages streamed through channels) ──
    // LINUX-ONLY (pthreads). Each stage runs as its own thread; adjacent stages share a channel;
    // a stage closes its output on return so completion cascades down the pipe. Values stream FIFO,
    // so a linear pipe's output is DETERMINISTIC and matches the interpreter's buffered-sequential
    // order → these ARE true Compile == Interpret oracle tests (the final stage is the only writer).

    [Fact]
    public void TaskPipe_TwoStage_ProducerConsumer()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        const string src = """
            Bind void to producer:
              output 1.
              output 2.
              output 3.
              output 4.
              output 5.
            Done.
            Bind void to consumer:
              for each item from the input:
                State item.
              Done.
            Done.
            producer | consumer.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void TaskPipe_ConsumerAccumulatesSum()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The consumer drains the whole stream, then prints the aggregate — order-independent (15).
        const string src = """
            Bind void to producer:
              output 1.
              output 2.
              output 3.
              output 4.
              output 5.
            Done.
            Bind void to consumer:
              Define total as 0.
              for each item from the input:
                total becomes total + item.
              Done.
              State total.
            Done.
            producer | consumer.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void TaskPipe_ThreeStage_MiddleTransforms()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A middle stage both consumes (from the input) AND produces (to the output) — the value
        // crosses two channel boundaries (producer→doubler→consumer). FIFO preserves order → 2,4,6.
        const string src = """
            Bind void to producer:
              output 1.
              output 2.
              output 3.
            Done.
            Bind void to doubler:
              for each item from the input:
                output item * 2.
              Done.
            Done.
            Bind void to consumer:
              for each item from the input:
                State item.
              Done.
            Done.
            producer | doubler | consumer.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void TaskPipe_FourStage_TwoMiddleTransforms()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Two middle stages chained (producer → add-ten → double → consumer): 3 channels, 4 threads.
        const string src = """
            Bind void to producer:
              output 1.
              output 2.
            Done.
            Bind void to add-ten:
              for each item from the input:
                output item + 10.
              Done.
            Done.
            Bind void to doubler:
              for each item from the input:
                output item * 2.
              Done.
            Done.
            Bind void to consumer:
              for each item from the input:
                State item.
              Done.
            Done.
            producer | add-ten | doubler | consumer.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void TaskPipe_EmptyProducer_ConsumerBodyNeverRuns()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Producer emits nothing and closes; the consumer's drain loop sees void immediately (zero
        // iterations) and continues to its trailing statement. Close-cascades with an empty stream.
        const string src = """
            Bind void to producer:
            Done.
            Bind void to consumer:
              for each item from the input:
                State item.
              Done.
              State "done".
            Done.
            producer | consumer.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void TaskPipe_StopInsideConsumer_ExitsEarly()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Stop breaks the consumer's drain loop early — values still in flight remain unreceived in
        // the channel and are freed at teardown (the never-received-bridge free path; see ASan test).
        const string src = """
            Bind void to producer:
              output 1.
              output 2.
              output 3.
              output 4.
              output 5.
            Done.
            Bind void to consumer:
              for each item from the input:
                If item = 3, Stop.
                State item.
              Done.
            Done.
            producer | consumer.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void TaskPipe_SkipInsideConsumer_SkipsCurrentItem()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        const string src = """
            Bind void to producer:
              output 1.
              output 2.
              output 3.
            Done.
            Bind void to consumer:
              for each item from the input:
                If item = 2, Skip.
                State item.
              Done.
            Done.
            producer | consumer.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void TaskPipe_EarlyStop_ASan_FreesPendingBridges()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The consumer stops at item 3, so items 4 and 5 are produced-but-never-received — they sit
        // in the channel as heap-bridges when the pipe tears down. cufet_chan_free must free those
        // pending nodes (close-with-pending), and every stage-thread + channel frees cleanly. The
        // final stage's `State` output stays deterministic (1,2). ASan/LSan must be clean.
        const string src = """
            Bind void to producer:
              for each n in the range 1 to 20, repeat:
                output n.
              Done.
            Done.
            Bind void to consumer:
              for each item from the input:
                If item = 3, Stop.
                State item.
              Done.
            Done.
            producer | consumer.
            """;
        Assert.Equal(Interpret(src), CompileWithASan(src));
    }

    // ── channel-of-T: channels + task-pipe streams of any element type ──
    // The number-only channel is generalized to a type-erased container with a per-element-type deep
    // copy at the boundary (heap bridge on send, arena copy on recv). A single-producer/single-consumer
    // channel streams FIFO, so the consumer's printed output is deterministic and matches the
    // interpreter's fill-then-drain order → these are true Compile == Interpret oracle tests.

    [Fact]
    public void Channel_OfText_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        const string src = """
            Pull a rabbit.
                Define ch as a channel of text.
                Have rabbit start a task as producer:
                    Define s as "hello".
                    Send s through ch.
                    Send "world" through ch.
                    Close ch.
                Done.
                Have rabbit start a task as consumer:
                    Define got as the delivery from ch.
                    While got is not void, repeat:
                        State (got but void is "?").
                        got becomes the delivery from ch.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Channel_OfObject_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        const string src = """
            Define object person with (the text name, the number age).
            Pull a rabbit.
                Define ch as a channel of person.
                Have rabbit start a task as producer:
                    Define p as a new person { the name "Ada", the age 36 }.
                    Send p through ch.
                    Close ch.
                Done.
                Have rabbit start a task as consumer:
                    Define got as the delivery from ch.
                    While got is not void, repeat:
                        Define r as (got but void is (a new person { the name "?", the age 0 })).
                        State "name=" joined to (the name of r) joined to " age=" joined to ((the age of r) converted to text).
                        got becomes the delivery from ch.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Channel_OfSeries_DeepCopyIsolation_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The producer mutates the ORIGINAL series after sending; the consumer's arena copy must be
        // unaffected (len=2, not 3). This is the A+B deep-copy isolation, now for a reference element.
        const string src = """
            Pull a rabbit.
                Define ch as a channel of series of text.
                Have rabbit start a task as producer:
                    Define xs as a series of text with ("p", "q").
                    Send xs through ch.
                    Add "MUT" to xs.
                    Close ch.
                Done.
                Have rabbit start a task as consumer:
                    Define got as the delivery from ch.
                    While got is not void, repeat:
                        Define r as (got but void is (a series of text with ())).
                        State "len=" joined to ((the number of r) converted to text) joined to " first=" joined to (the first of r).
                        got becomes the delivery from ch.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Channel_OfObjectWithSeriesField_DeepCopyIsolation_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The crux of a GENUINELY-deep copy: the element is an object whose field is a series. The
        // producer mutates the inner series after sending; the whole nested structure must cross the
        // boundary arena-independently, so the consumer's copy still reads nums-len=3 (not 4).
        const string src = """
            Define object bundle with (the text label, the series of number nums).
            Pull a rabbit.
                Define ch as a channel of bundle.
                Have rabbit start a task as producer:
                    Define ns as a series of number with (1, 2, 3).
                    Define b as a new bundle { the label "first", the nums ns }.
                    Send b through ch.
                    Add 999 to ns.
                    Close ch.
                Done.
                Have rabbit start a task as consumer:
                    Define got as the delivery from ch.
                    While got is not void, repeat:
                        Define r as (got but void is (a new bundle { the label "?", the nums (a series of number with ()) })).
                        State (the label of r) joined to " nums-len=" joined to ((the number of (the nums of r)) converted to text).
                        got becomes the delivery from ch.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Channel_OfMap_DeepCopyIsolation_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        const string src = """
            Pull a rabbit.
                Define ch as a channel of map from text to number.
                Have rabbit start a task as producer:
                    Define m as a map from text to number with ("a" : 1, "b" : 2).
                    Send m through ch.
                    In m, the entry for "c" becomes 3.
                    Close ch.
                Done.
                Have rabbit start a task as consumer:
                    Define got as the delivery from ch.
                    While got is not void, repeat:
                        Define r as (got but void is (a map from text to number with ())).
                        State "size=" joined to ((the size of r) converted to text) joined to " a=" joined to ((the entry for "a" in r but void is 0) converted to text).
                        got becomes the delivery from ch.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void TextPipe_ThreeStage_MatchesInterpreter()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The capability channel-of-T unblocks: a text pipe. Producer emits text, a middle stage
        // transforms text→text, consumer prints. Linear pipe + FIFO ⇒ deterministic ⇒ oracle test.
        const string src = """
            Bind void to producer:
              output "a".
              output "bb".
              output "ccc".
            Done.
            Bind void to shout:
              for each w from the input:
                output (w joined to "!").
              Done.
            Done.
            Bind void to consumer:
              for each w from the input:
                State w.
              Done.
            Done.
            producer | shout | consumer.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Channel_OfReference_ASan_DeepCopyFreesAllPaths()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Nested reference elements (series-of-series and map-of-series) cross channels while the
        // producers mutate their originals. Every heap bridge — the whole nested tree — must free on
        // every path (received-and-arena-copied, then the bridge freed; teardown of any pending). The
        // deep-copy isolation invariant (outer-len=1, inner-len=3, batch-len=3) must hold, ASan-clean.
        const string src = """
            Pull a rabbit.
                Define ch-list as a channel of series of series of number.
                Define ch-map  as a channel of map from text to series of number.
                Have rabbit start a task as list-producer:
                    Define inner as a series of number with (10, 20, 30).
                    Define outer as a series of series of number with ().
                    Add inner to outer.
                    Send outer through ch-list.
                    Add 999 to inner.
                    Add (a series of number with (7, 8, 9)) to outer.
                    Close ch-list.
                Done.
                Have rabbit start a task as map-producer:
                    Define batch as a series of number with (1, 2, 3).
                    Define data as a map from text to series of number with ().
                    In data, the entry for "batch" becomes batch.
                    Send data through ch-map.
                    Add 999 to batch.
                    Close ch-map.
                Done.
                Have rabbit start a task as list-consumer:
                    Define received as the delivery from ch-list.
                    While received is not void, repeat:
                        Define r as (received but void is (a series of series of number with ())).
                        Define inner-copy as the first of r.
                        State "outer-len=" joined to (the number of r) converted to text.
                        State "inner-len=" joined to (the number of inner-copy) converted to text.
                        received becomes the delivery from ch-list.
                    Done.
                Done.
                Have rabbit start a task as map-consumer:
                    Define received as the delivery from ch-map.
                    While received is not void, repeat:
                        Define r as (received but void is (a map from text to series of number with ())).
                        Define batch-copy as (the entry for "batch" in r but void is (a series of number with ())).
                        State "batch-len=" joined to (the number of batch-copy) converted to text.
                        received becomes the delivery from ch-map.
                    Done.
                Done.
            Done.
            """;
        // Two independent producer/consumer pairs → their interleaving is nondeterministic, but each
        // consumer's own lines are internally ordered; assert ASan-clean + the isolation invariant via
        // the compiled run alone (not bit-identical to the interpreter's serialized task ordering).
        var outText = CompileWithASan(src);
        Assert.Contains("outer-len=1", outText);
        Assert.Contains("inner-len=3", outText);
        Assert.Contains("batch-len=3", outText);
    }

    // ── CONC.E: native SIGINT (true-preemptive interrupt) ──
    // The deterministic (no-signal) cases are ordinary Compile == Interpret oracle tests and run on
    // BOTH platforms (the signal substrate degrades to no-op stubs on mingw). The actual SIGINT-
    // delivery cases are Linux-only (POSIX signal delivery) and assert the invariant — the program
    // stops + unwinds cleanly (exit 130), NOT bit-identical timing (interrupt timing is nondeterministic).

    [Fact]
    public void Interrupt_NotRequested_PollReadsFalse()
    {
        // `an interrupt is requested` reads the flag as a fact; with no interrupt it is false.
        const string src = """
            Define r as an interrupt is requested.
            If r, State "interrupted". Otherwise, State "ok".
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Interrupt_CooperativeAcknowledge_ClearsFlag()
    {
        // `Acknowledge the interrupt.` clears the flag (cooperative handling). With no interrupt the
        // else-branch runs — exercises that Acknowledge compiles and the poll path is wired.
        const string src = """
            If an interrupt is requested:
                Acknowledge the interrupt.
                State "handled".
            Done.
            Otherwise:
                State "normal".
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Yield_NoInterrupt_ResumesNormally()
    {
        // `Yield.` with no pending interrupt is a no-op checkpoint — the loop runs to completion.
        const string src = """
            Define count as 0.
            While count is less than 3, repeat:
                Yield.
                count becomes count + 1.
            Done.
            State count.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Interrupt_TightYieldLoop_PreemptivelyStops()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A non-terminating tight loop with a Yield checkpoint: a delivered SIGINT unwinds it to a
        // clean exit (130) — it prints its pre-loop line but never reaches the post-loop line. This
        // is the true-preemptive interrupt; the invariant is "stops cleanly", not timing.
        var (code, output) = CompileAndInterrupt("""
            State "looping".
            While 1 is 1, repeat:
                Yield.
            Done.
            State "never".
            """, 500);
        Assert.Equal(130, code);
        Assert.Equal("looping", output);
    }

    [Fact]
    public void Interrupt_BlockedChannelWait_WakesAndStops()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The flagship: main blocks in a real pthread_cond_wait on an empty channel (something the
        // cooperative interpreter can't truly do). A delivered SIGINT wakes the blocked wait, unwinds
        // to a clean exit (130), and the interrupt teardown frees the channel + arenas. Prints its
        // pre-wait line, never the post-wait line.
        var (code, output) = CompileAndInterrupt("""
            State "waiting".
            Pull a rabbit.
                Define ch as a channel of number.
                Define v as the delivery from ch.
                State "got it".
            Done.
            State "after".
            """, 500);
        Assert.Equal(130, code);
        Assert.Equal("waiting", output);
    }

    // Compiles + runs the binary, delivers SIGINT after delayMs, returns (exit code, stdout). Linux
    // only (POSIX signal delivery via /bin/kill). Used to verify the true-preemptive interrupt.
    private static (int ExitCode, string Output) CompileAndInterrupt(string source, int delayMs)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var cSource = new CodeGenerator().Generate(program);

        var tmp     = Path.GetTempFileName();
        File.Delete(tmp);
        var cPath   = tmp + ".c";
        var binPath = tmp;
        try
        {
            File.WriteAllText(cPath, cSource);
            new GccInvoker().Compile(cPath, binPath, ["-pthread"]);
        }
        finally { try { File.Delete(cPath); } catch { } }

        try
        {
            var psi = new ProcessStartInfo(binPath)
            {
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,   // binaries print UTF-8 (e.g. em-dash messages)
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            using var proc = Process.Start(psi)!;
            var killer = Task.Run(() =>
            {
                Thread.Sleep(delayMs);
                try
                {
                    using var k = Process.Start(new ProcessStartInfo("/bin/kill", $"-INT {proc.Id}") { UseShellExecute = false });
                    k!.WaitForExit();
                }
                catch { }
            });
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            killer.Wait();
            return (proc.ExitCode, output.Replace("\r\n", "\n").TrimEnd('\n'));
        }
        finally { try { File.Delete(binPath); } catch { } }
    }

    [Fact]
    public void Subprocess_Run_MemorySafety_ASan_ZeroLeaksAndNoUAF()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Process handles are reaped (waitpid) and fds closed inside the run primitive, so nothing
        // leaks across statements; capture buffers are arena/free-managed. ASan/LSan must be clean.
        const string src = """
            Pull a rabbit.
                For each n in the range 1 to 10, repeat:
                    Try to:
                        Define r as run "echo" with arguments ("hi") | run "cat".
                        State the output of r.
                    Done.
                    In case of failure:
                        State "fail".
                    Done.
                Done.
            Done.
            """;
        string expected = Interpret(src);
        string actual   = CompileWithASan(src);
        Assert.Equal(expected, actual);
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
                StandardOutputEncoding = System.Text.Encoding.UTF8,   // binaries print UTF-8 (e.g. em-dash messages)
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

    [Fact]
    public void Text_MemorySafety_ASan_ZeroLeaksAndNoUAF()
    {
        // The string runtime cooperates with the arena: a text-op-heavy Pull block allocates
        // many runtime strings (join/case/substring/replace/convert) that must all free at
        // Done. — zero leaks / UAF. Proves immutable arena strings are memory-clean. Linux-only.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        const string src = """
            Pull a rabbit.
                For each n in the range 1 to 25, repeat:
                    Define label as "item-" joined to (n converted to text).
                    Define up as label in uppercase.
                    Define slice as the characters from 1 to 4 of up.
                    Define rep as replace "T" with "x" in slice.
                    State rep joined to " " joined to (the length of label converted to text).
                Done.
            Done.
            """;
        string expected = Interpret(src);
        string actual   = CompileWithASan(src);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Map_MemorySafety_ASan_ZeroLeaksAndNoUAF()
    {
        // The last arena-allocated type: a map with nested reference values (series) plus
        // growth (append past initial capacity). The map, its key/value arrays, and the nested
        // series must all free cleanly at Done. — zero leaks / UAF. Linux-only.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        const string src = """
            Pull a rabbit.
                Define scores as a map from text to series of number with ("a": a series of number with (1, 2)).
                In scores, the entry for "b" becomes a series of number with (3, 4, 5).
                In scores, the entry for "c" becomes a series of number with (6).
                In scores, the entry for "d" becomes a series of number with (7).
                In scores, the entry for "e" becomes a series of number with (8).
                For each pair in scores, repeat:
                    State the key of pair.
                    State the value of pair.
                Done.
                State the size of scores.
            Done.
            """;
        string expected = Interpret(src);
        string actual   = CompileWithASan(src);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void File_ReadResults_MemorySafety_ASan_ZeroLeaksAndNoUAF()
    {
        // File-read results are arena-allocated (the text buffer, the line array, each line string)
        // and must free at Done. — zero leaks / UAF. Proves the OS-error bridge + read results
        // cooperate with the arena, reusing the string/series arena model. Linux-only.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        var path = Path.Combine(Path.GetTempPath(), "cufet-io-asan-" + Guid.NewGuid().ToString("N") + ".txt")
            .Replace('\\', '/');
        var src = $$"""
            Pull a rabbit.
                Write "line one\nline two\nline three" to the file "{{path}}".
                For each n in the range 1 to 20, repeat:
                    Try to:
                        Define whole as read all from the file "{{path}}".
                        Define lines as read all lines from the file "{{path}}".
                        State the length of whole.
                        State the number of lines.
                    Done.
                    In case of failure:
                        State "fail".
                    Done.
                Done.
            Done.
            """;
        try
        {
            string expected = Interpret(src);
            string actual   = CompileWithASan(src);
            Assert.Equal(expected, actual);
        }
        finally { try { File.Delete(path.Replace('/', Path.DirectorySeparatorChar)); } catch { } }
    }

    [Fact]
    public void With_StreamsAndCleanup_MemorySafety_ASan_ZeroLeaksAndNoUAF()
    {
        // Streams + close-on-all-paths inside a Pull: each iteration opens a file, writes, reopens,
        // reads (arena strings + line series), and closes on normal exit — plus arena churn. No
        // leaks / UAF (the FILE* handles all close; the arena frees at Done). Linux-only.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        var path = Path.Combine(Path.GetTempPath(), "cufet-io-with-asan-" + Guid.NewGuid().ToString("N") + ".txt")
            .Replace('\\', '/');
        var src = $$"""
            Pull a rabbit.
                For each n in the range 1 to 15, repeat:
                    With the file "{{path}}" open for writing as out:
                        write "line-a\nline-b\nline-c" to out.
                    Done.
                    With the file "{{path}}" open for reading as inp:
                        Define lines as read all lines from inp.
                        State the number of lines.
                    Done.
                Done.
            Done.
            """;
        try
        {
            string expected = Interpret(src);
            string actual   = CompileWithASan(src);
            Assert.Equal(expected, actual);
        }
        finally { try { File.Delete(path.Replace('/', Path.DirectorySeparatorChar)); } catch { } }
    }

    [Fact]
    public void Series_Heterogeneous_MemorySafety_ASan_ZeroLeaksAndNoUAF()
    {
        // Generalized series across element types in one Pull block: a series of text (arena
        // strings from split), a series of records (value structs), and a nested series of series.
        // The whole structure — series bookkeeping plus each element's own allocations — must free
        // cleanly at Done. This is the "generalized series is arena-clean" proof. Linux-only.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        const string src = """
            Pull a rabbit.
                Define words as "alpha,beta,gamma,delta" split by ",".
                Add "epsilon" to words.
                For each w in words, repeat:
                    State w in uppercase.
                Done.
                Define people as a series with (a record with (the name "Alice", the age 30)).
                Add a record with (the name "Bob", the age 25) to people.
                For each p in people, repeat:
                    State the name of p.
                Done.
                Define grid as a series with (a series with (1, 2), a series with (3, 4, 5)).
                Add a series with (6) to grid.
                For each row in grid, repeat:
                    State the number of row.
                Done.
            Done.
            """;
        string expected = Interpret(src);
        string actual   = CompileWithASan(src);
        Assert.Equal(expected, actual);
    }

    // ── CL.1: closure substrate — function VALUES, no capture (uniform {fn, env} with NULL env) ──
    // A FunctionType lowers to a `cfn_N { ret (*fn)(void* env, …); void* env; }` value struct; a named
    // function used as a value is wrapped in a thunk (ignores env); calls through a function-value are
    // indirect (fn ptr). These are pure (no threads) → Compile == Interpret on both platforms.

    [Fact]
    public void Closure_FunctionValuedVariable_IndirectCall()
    {
        const string src = """
            Bind number to grade, given (the number x):
                Return x + 1.
            Done.
            Define op as grade.
            State cast op on (5).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_PassNamedFunction_HigherOrder()
    {
        // A named function passed as an argument to a higher-order function that calls it twice.
        const string src = """
            Bind number to twice, given (the number function f given (the number), the number x):
                Return cast f on (cast f on (x)).
            Done.
            Bind number to inc, given (the number n):
                Return n + 1.
            Done.
            State cast twice on (inc, 10).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_ReturnNamedFunction_ThenCall()
    {
        // A function that returns a (named) function value; the caller stores and calls it.
        const string src = """
            Bind number function given (the number) to pick:
                Return double-it.
            Done.
            Bind number to double-it, given (the number n):
                Return n * 2.
            Done.
            Define f as cast pick.
            State cast f on (21).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_TextReturningFunctionValue()
    {
        // A function value whose return type is a reference type (text) — the {fn, env} slot is
        // signature-agnostic; the indirect call yields the same text as a direct call.
        const string src = """
            Bind text to shout, given (the text s):
                Return s joined to "!".
            Done.
            Define f as shout.
            State cast f on ("hi").
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── CL.2: closure captures — lambdas + nested Bind (the env-record IS the capture policy) ──
    // The env is a synthesized value-struct of the free vars: value captures store BY VALUE (snapshot),
    // region captures store the SHARED POINTER (share) — binding-is-binding, matching the interpreter.
    // The non-thread cases are pure → Compile == Interpret on both platforms.

    [Fact]
    public void Closure_LambdaValueCapture_IsSnapshot()
    {
        // Capture a number, mutate the enclosing var AFTER creating the lambda → the lambda sees the
        // SNAPSHOT (value stored by value in the env), not the mutation. 5 + 10 == 15 (not 105).
        const string src = """
            Bind void to test:
                Define n as 10.
                Define f as a function given (the number x): Return x + n. Done.
                n becomes 100.
                State cast f on (5).
            Done.
            Cast test.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_LambdaRegionCapture_IsShared()
    {
        // Capture a series, mutate the SERIES after creating the lambda → the lambda sees the mutation
        // (the env stores the shared pointer). 10 + 4 == 14 (not 13). The share half of binding-is-binding.
        const string src = """
            Bind void to test:
                Define xs as a series of number with (1, 2, 3).
                Define f as a function given (the number x): Return x + the number of xs. Done.
                Add 99 to xs.
                State cast f on (10).
            Done.
            Cast test.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_NestedBind_Captures()
    {
        // A nested Bind (named local closure) captures the enclosing function's parameter.
        const string src = """
            Bind number to outer, given (the number base):
                Bind number to add-base, given (the number y):
                    Return y + base.
                Done.
                Return cast add-base on (7).
            Done.
            State cast outer on (100).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_MakeAdder_ValueCaptureEscapesFunction()
    {
        // The classic make-adder: a lambda captures a value param and is RETURNED out of its creating
        // function. A value capture is self-contained (the env owns its snapshot), so the escape is
        // safe (env lives in the enclosing arena, which a plain function frame doesn't pop).
        const string src = """
            Bind number function given (the number) to make-adder, given (the number n):
                Return a function given (the number x): Return x + n. Done.
            Done.
            Define add10 as cast make-adder on (10).
            State cast add10 on (5).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_LambdaPipeStage_NoCapture()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The unblocked capability: a lambda used as a pipe stage. A stage is a closure value; the
        // pipe runner calls fn(env). A middle lambda stage transforms the stream.
        const string src = """
            Bind void to producer:
              output 1.
              output 2.
              output 3.
            Done.
            Bind void to consumer:
              for each x from the input:
                State x.
              Done.
            Done.
            producer | (a function: for each x from the input: output x * 10. Done. Done) | consumer.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_LambdaPipeStage_CapturesValue()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A CAPTURING lambda pipe stage: the env (a value capture — immutable) crosses the thread
        // boundary, shared read-only while the creating scope blocks on the pipe join → TSan-clean.
        const string src = """
            Bind void to run-pipe, given (the number factor):
              Bind void to producer:
                output 1.
                output 2.
              Done.
              Bind void to consumer:
                for each x from the input:
                  State x.
                Done.
              Done.
              producer | (a function: for each x from the input: output x * factor. Done. Done) | consumer.
            Done.
            Cast run-pipe on (100).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── CL.3: closures breadth + escape interim (closes the arc) ──
    // Function-values as series elements, higher-order-of-higher-order (nested cfn struct ordering),
    // recursive nested Bind (by-name self-call), lambda TEXT pipe stages, and the region-capture-
    // escapes-a-rabbit interim (clean-throw). Pure cases → Compile == Interpret on both platforms.

    [Fact]
    public void Closure_SeriesOfFunctions()
    {
        // A series whose element type is a function value — the cfn_N value struct nests in the series
        // (the cfn struct is emitted before the series runtime; function eq/write added for the series).
        const string src = """
            Bind number to inc, given (the number n): Return n + 1. Done.
            Bind number to dbl, given (the number n): Return n * 2. Done.
            Define ops as a series of number function given (the number) with (inc, dbl).
            State cast (the first of ops) on (10).
            State cast (the second of ops) on (10).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_FunctionReturningFunction_AsValue()
    {
        // make-adder as a VALUE — its type is (number) -> (number -> number), a cfn whose RETURN is a
        // cfn → the nested-cfn topo ordering (inner declared before outer).
        const string src = """
            Bind number function given (the number) to make-adder, given (the number n):
                Return a function given (the number x): Return x + n. Done.
            Done.
            Define maker as make-adder.
            Define add5 as cast maker on (5).
            State cast add5 on (10).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_RecursiveNestedBind()
    {
        // A recursive nested Bind (factorial): recursion resolves BY NAME to a self-call reusing the
        // current env (matching the interpreter's in-scope-name recursion). 100 + 5! == 220.
        const string src = """
            Bind number to compute, given (the number base):
                Bind number to fact, given (the number k):
                    If k < 2, Return 1.
                    Return k * (cast fact on (k - 1)).
                Done.
                Return base + (cast fact on (5)).
            Done.
            State cast compute on (100).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_RecursiveNestedBind_WithCapture()
    {
        // Recursion + capture together: countdown recurses (self-call) AND captures `bump`; the
        // self-call reuses the current env, so the recursive call sees the same capture. 10*3 == 30.
        const string src = """
            Bind number to compute, given (the number bump):
                Bind number to countdown, given (the number k):
                    If k < 1, Return 0.
                    Return bump + (cast countdown on (k - 1)).
                Done.
                Return cast countdown on (3).
            Done.
            State cast compute on (10).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_LambdaTextPipeStage()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A lambda TEXT pipe stage: AnalyzePipes now propagates the element type THROUGH the lambda,
        // so the named consumer after it reads text (not number) — the fix for the lambda-text UAF.
        const string src = """
            Bind void to producer:
              output "a".
              output "bb".
            Done.
            Bind void to consumer:
              for each w from the input:
                State w.
              Done.
            Done.
            producer | (a function: for each w from the input: output (w joined to "!"). Done. Done) | consumer.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_RegionCaptureEscapingRabbit_CleanThrow()
    {
        // The escape interim: a closure capturing a REGION value (series) inside a rabbit can't safely
        // escape (its captured pointer dangles after Done.-pop). The front-end misses this (FunctionType
        // isn't a tracked reference type), so the compiler clean-throws rather than emit a silent dangle.
        const string src = """
            Define f as a function given (the number x): Return x. Done.
            Pull a rabbit.
                Define xs as a series of number with (1, 2, 3).
                f becomes a function given (the number x): Return x + the number of xs. Done.
            Done.
            State cast f on (10).
            """;
        var tokens  = new CufetLexer(src).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        Assert.Throws<CompilerException>(() => new CodeGenerator().Generate(program));
    }

    // ── Small edges: implicit-value capture + recursion ordering (closes the audit's small lines) ──
    // `one` (method receiver) and `the failure` (a handler's caught failure) are implicit bindings the
    // free-var analysis now recognizes as capturable, matching the interpreter. `the input` needs no
    // capture (it lowers to the `stdin` global). Recursion whose FIRST return is the recursive call
    // already resolves (the nested-Bind desugar registers the declared return type before inference).

    [Fact]
    public void Closure_CapturesMethodReceiver_One()
    {
        // A lambda inside a method body referencing `one` captures the receiver.
        const string src = """
            Define object box with (the number n):
                Bind number to twice-n:
                    Define f as a function given (the number x): Return x + one's n. Done.
                    Return cast f on (0).
                Done.
            Done.
            Define b as a new box { the n 21 }.
            State cast twice-n on (b).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_MethodReceiverCapture_IsSnapshot()
    {
        // Objects are value types, so capturing `one` SNAPSHOTS the receiver: mutating a field after
        // the lambda is created doesn't change what the lambda sees (7, not 99) — binding-is-binding.
        const string src = """
            Define object box with (the number n):
                Bind number to probe:
                    Define f as a function: Return one's n. Done.
                    one's n becomes 99.
                    Return cast f.
                Done.
            Done.
            Define b as a new box { the n 7 }.
            State cast probe on (b).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_CapturesTheFailure_InHandler()
    {
        // A lambda inside an `In case of failure` handler referencing `the failure` captures the
        // caught CufetFailure (a value → snapshot), so `the message of the failure` resolves inside it.
        const string src = """
            Bind number or failure to risky:
                Return a failure "boom" of category "test".
            Done.
            Bind void to handle:
                Try to:
                    Define v as cast risky.
                    State "ok".
                Done.
                In case of failure:
                    Define f as a function: Return the message of the failure. Done.
                    State cast f.
                Done.
            Done.
            Cast handle.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Closure_ReferencesTheInput_NoCaptureNeeded()
    {
        // `the input` lowers to the `stdin` global — a lambda referencing it needs no capture at all.
        const string src = """
            Bind void to go:
                Define f as a function: Return read a line from the input. Done.
                Define line as cast f.
                State (line but void is "<eof>").
            Done.
            Cast go.
            """;
        Assert.Equal(Interpret(src, "hello\n"), Compile(src, "hello\n"));
    }

    // ── CAT.1: closed unions (catalogue) — the N-case generalization of voidable ──
    // `cun_N { int tag; union { c0; c1; … } val; }` per closed union; widening sets the tag at the
    // (statically typed) store site; `is a <case>` is a genuine RUNTIME tag check; narrowing exposes
    // `.val.c<k>` at the case's concrete type. Scoped to KIND-DISTINGUISHABLE cases.

    [Fact]
    public void Catalogue_ClosedUnion_ScalarsNarrowBothArms()
    {
        // Construct, iterate, narrow both arms (the else arm narrows exhaustively to the one
        // remaining case — matching the front-end's residual-union narrowing).
        const string src = """
            Define stuff as a catalogue of (number or text) with (1, "two", 3).
            For each item in stuff, repeat:
                If item is a number, State "num:" joined to (item converted to text).
                Otherwise, State "txt:" joined to item.
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

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
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Catalogue_SeriesOps_FallOutOfPerTypeSynthesis()
    {
        // A catalogue IS a series of union, so the existing per-T series synthesis gives every series
        // op for free: Add, Add-to-start, the number of, ordinal access, Remove.
        const string src = """
            Define stuff as a catalogue of (number or text) with (1, "two").
            Add 3 to stuff.
            Add "four" to the start of stuff.
            State the number of stuff.
            Define f as the first of stuff.
            If f is a text, State "first=" joined to f.
            Remove the first item from stuff.
            State the number of stuff.
            """;
        Assert.Equal(Interpret(src), Compile(src));
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
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Catalogue_ContainerVsContainerUnion_CleanThrow()
    {
        // THE SAFETY INVARIANT: `is a` is element-erased for containers, so `(series of number or
        // series of text)` cases can't be told apart at runtime — narrowing would reinterpret one
        // payload as the other (silent UB). The compiler must refuse LOUDLY, never miscompile.
        const string src = """
            Define grids as a catalogue of (series of number or series of text) with ((a series of number with (1,2))).
            For each g in grids, repeat:
                If g is a series of number, State "nums".
                Otherwise, State "texts".
            Done.
            """;
        var tokens  = new CufetLexer(src).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        Assert.Throws<CompilerException>(() => new CodeGenerator().Generate(program));
    }

    // ── CAT.2: open unions (bounded — the whole-program discovery pass) ──
    // ALL open unions are ONE front-end type (UnionType.Open), so there is ONE global `cun_open` over
    // the bounded set of concrete types ever widened into an open union anywhere in the program. The
    // set is filled by a discovery PRE-PASS (a fixed point), then CAT.1's machinery does the rest.

    [Fact]
    public void OpenCatalogue_DiscoversCaseSet_AndNarrows()
    {
        const string src = """
            Define mixed as a catalogue with (1, "two", true).
            State the number of mixed.
            For each m in mixed, repeat:
                If m is a number, State "n".
                Otherwise, If m is a text, State "t".
                Otherwise, State "f".
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void OpenCatalogue_BuiltViaAdds_DiscoveryCatchesAddSites()
    {
        const string src = """
            Define items as a catalogue.
            Add 1 to items.
            Add "two" to items.
            State the number of items.
            For each i in items, repeat:
                If i is a number, State "n".
                Otherwise, If i is a text, State "t".
                Otherwise, State "?".
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void OpenCatalogue_IsATypeNeverWidenedIn_IsFalse()
    {
        // Bounded tag set: a type that never flows into any open union can't be in one, so the check
        // is statically false — matching the interpreter (the `fact` arm never fires).
        const string src = """
            Define items as a catalogue with (1, "two").
            For each i in items, repeat:
                If i is a fact, State "fact!".
                Otherwise, If i is a number, State "n".
                Otherwise, State "t".
            Done.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void OpenCatalogues_AreOneType_Interchangeable()
    {
        // MEASURED: all open unions are the SAME front-end type — differently-populated open
        // catalogues pass to the same parameter and assign to each other. So the case set must be
        // GLOBAL (a per-location set would give interchangeable values different representations).
        const string src = """
            Bind void to show, given (the catalogue c):
                State the number of c.
            Done.
            Define a1 as a catalogue with (1, "two").
            Define a2 as a catalogue with (true).
            Cast show on (a1).
            Cast show on (a2).
            a2 becomes a1.
            State the number of a2.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void OpenCatalogue_NeverPopulated_IsDegenerate()
    {
        // An open catalogue that never receives a value: the discovered case set is empty, so the
        // tagged struct is tag-only (nothing can ever be widened in).
        const string src = """
            Define items as a catalogue.
            State the number of items.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void OpenCatalogue_DiscoveryIsComplete_AcrossEmissionOrder()
    {
        // THE COMPLETENESS CRUX: function bodies emit BEFORE main, so this `is a fact` is emitted
        // before main's `Add true` — only a whole-program discovery PRE-PASS makes the fact tag exist
        // at that point. Without it the check would fold to false and print "other" instead of "fact".
        const string src = """
            Bind void to show, given (the catalogue c):
                For each i in c, repeat:
                    If i is a fact, State "fact".
                    Otherwise, If i is a number, State "num".
                    Otherwise, State "other".
                Done.
            Done.
            Define items as a catalogue.
            Add 1 to items.
            Add true to items.
            Cast show on (items).
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void OpenCatalogue_ContainerAmbiguity_CleanThrow()
    {
        // The CAT.1 safety invariant carries over: an open union whose DISCOVERED set contains two
        // container kinds is element-erased-ambiguous → loud refuse, never a reinterpreting miscompile.
        const string src = """
            Define mixed as a catalogue with ((a series of number with (1,2)), (a series of text with ("a"))).
            For each m in mixed, repeat:
                If m is a series of number, State "nums".
                Otherwise, State "other".
            Done.
            """;
        var tokens  = new CufetLexer(src).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        Assert.Throws<CompilerException>(() => new CodeGenerator().Generate(program));
    }

    [Fact]
    public void Closure_RecursiveNestedBind_RecursiveReturnFirst()
    {
        // Recursion where the FIRST return encountered is the recursive call (no base case first).
        // Resolves because the nested-Bind desugar registers the DECLARED return type before the
        // body's return-type inference runs — locking that ordering against regression.
        const string src = """
            Bind number to compute:
                Bind number to countdown, given (the number k):
                    If k > 0, Return cast countdown on (k - 1).
                    Return 0.
                Done.
                Return cast countdown on (3).
            Done.
            State cast compute.
            """;
        Assert.Equal(Interpret(src), Compile(src));
    }
}
