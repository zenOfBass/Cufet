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
}
