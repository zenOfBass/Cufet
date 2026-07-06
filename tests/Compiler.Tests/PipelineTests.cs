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
}
