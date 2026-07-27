using System.Diagnostics;
using System.Runtime.InteropServices;
using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

// The `bits` type — a bit pattern, deliberately not a quantity.
//
// Every test here is an ORACLE test: it asserts the compiled binary prints exactly what the
// interpreter prints. A new type is precisely where the two backends can quietly drift apart,
// because each has its own representation — a C# record struct on one side and a C struct on
// the other — and its own formatting code.
public class BitsTests
{
    private static string Compile(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var cSource = new CodeGenerator().Generate(program);

        var tmp = Path.GetTempFileName();
        File.Delete(tmp);
        var cPath   = tmp + ".c";
        var binExt  = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
        var binPath = tmp + binExt;

        try
        {
            File.WriteAllText(cPath, cSource);
            new GccInvoker().Compile(cPath, binPath);
        }
        finally { try { File.Delete(cPath); } catch { } }

        try
        {
            var psi = new ProcessStartInfo(binPath)
            {
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output.Replace("\r\n", "\n").TrimEnd('\n');
        }
        finally { try { File.Delete(binPath); } catch { } }
    }

    private static string Interpret(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var sb = new StringWriter();
        new CufetInterpreter(sb, null).Execute(program);
        return sb.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static void Oracle(string source, string expected)
    {
        Assert.Equal(expected, Interpret(source));
        Assert.Equal(Interpret(source), Compile(source));
    }

    // ── A pattern shows itself in the base it was written in ─────────────

    [Theory]
    [InlineData("State 0xFF.",     "0xFF")]
    [InlineData("State 0b1010.",   "0b1010")]
    [InlineData("State 0o755.",    "0o755")]
    [InlineData("State 0xDEADBEEF.", "0xDEADBEEF")]
    public void Bits_PrintsInItsOwnBase(string source, string expected)
        => Oracle(source, expected);

    [Fact]
    public void Bits_HexDigitsAreCanonicallyUppercase()
    {
        // A COMPUTED value has no literal to take its case from, so tracking case as well as
        // base would be extra state for a purely cosmetic gain. Uppercase is the convention.
        Oracle("State 0xff.", "0xFF");
        Oracle("State 0xAb.", "0xAB");
    }

    // ── Width comes from the digit count, and leading zeros are significant ──
    // Unlike C, Java, Rust, Go and Python, where 0x0F and 0xF are the same value and width is
    // a property of the declared type. Here the width is what `not` will flip within.

    [Fact]
    public void Bits_LeadingZerosSetTheWidthAndSurviveToOutput()
    {
        Oracle("State 0xF.",    "0xF");
        Oracle("State 0x0F.",   "0x0F");
        Oracle("State 0x000F.", "0x000F");
        Oracle("State 0b0001.", "0b0001");
        Oracle("State 0o007.",  "0o007");
    }

    // ── Separators are structural here, and dropped ──────────────────────

    [Theory]
    [InlineData("State 0xDE_AD_BE_EF.", "0xDEADBEEF")]
    [InlineData("State 0b1010_1010.",   "0b10101010")]
    [InlineData("State 0o7_5_5.",       "0o755")]
    public void Bits_SeparatorsGroupDigitsWithoutChangingTheValue(string source, string expected)
        => Oracle(source, expected);

    // ── Edges of the representation ──────────────────────────────────────

    [Fact]
    public void Bits_Zero()
    {
        Oracle("State 0x0.",  "0x0");
        Oracle("State 0x00.", "0x00");
        Oracle("State 0b0.",  "0b0");
    }

    [Fact]
    public void Bits_FullSixtyFourBits()
    {
        // The ceiling: 64 bits covers every C flag set, file mode and address there is.
        Oracle("State 0xFFFFFFFFFFFFFFFF.", "0xFFFFFFFFFFFFFFFF");
        Oracle("State 0x8000000000000000.", "0x8000000000000000");
    }

    // ── It flows through the language like any other value type ──────────

    [Fact]
    public void Bits_InAVariable()
        => Oracle("Define mode as 0o755.\nState mode.", "0o755");

    [Fact]
    public void Bits_AsParameterAndReturnType()
    {
        const string src = """
            Bind bits to echo, given (the bits b):
                Return b.
            Done.
            State cast echo on (0xDEAD).
            """;
        Oracle(src, "0xDEAD");
    }

    [Fact]
    public void Bits_ReassignmentKeepsBaseAndWidthOfTheNewValue()
    {
        // Base and width ride on the VALUE, not the type — which is what keeps `bits` a single
        // type with every value assignable to every other.
        Oracle("Define m as 0xFF.\nThe m becomes 0b1010.\nState m.", "0b1010");
    }

    // ── A bit pattern is not a quantity ──────────────────────────────────

    [Fact]
    public void Bits_DoesNotImplicitlyConvertToNumber()
    {
        var ex = Assert.Throws<TypeException>(() =>
            Interpret("Define x as 0xFF.\nDefine y as 255.\nx becomes y."));
        Assert.Contains("bits", ex.Message);
    }

    [Fact]
    public void Bits_CannotBeComparedToANumber()
    {
        var ex = Assert.Throws<TypeException>(() => Interpret("State 0xFF = 255."));
        Assert.Contains("bits", ex.Message);
        Assert.Contains("number", ex.Message);
    }

    [Fact]
    public void Bits_ErrorMessageEchoesTheLiteralAsWritten()
    {
        // Quoting the literal back is only useful if it looks like what was typed, so the
        // message rebuilds it in the author's own base and width.
        var ex = Assert.Throws<TypeException>(() =>
            Interpret("Define x as 0x0F.\nx becomes 3."));
        Assert.Contains("0x0F", ex.Message);
    }

    // ── The gates ────────────────────────────────────────────────────────
    // A 32-bit AND is 32 AND gates side by side, so the same words serve a fact (one bit) and
    // a bits value (N of them).

    [Theory]
    [InlineData("State 0xFF and 0x0F.",     "0x0F")]
    [InlineData("State 0xF0 or 0x0F.",      "0xFF")]
    [InlineData("State 0b1100 xor 0b1010.", "0b0110")]
    public void Bits_Gates(string source, string expected) => Oracle(source, expected);

    [Fact]
    public void Bits_NotFlipsWithinItsOwnWidth()
    {
        // The headline. A signed reading would make `not 0xFF` come out as -6-style nonsense;
        // unsigned with a known width makes it 0x00, which is what anyone would expect.
        Oracle("State not 0xFF.",   "0x00");
        Oracle("State not 0b1010.", "0b0101");
        Oracle("State not 0x0.",    "0xF");     // one digit = 4 bits, so all four flip
        Oracle("State not 0x00.",   "0xFF");    // two digits = 8 bits
    }

    [Fact]
    public void Bits_ClearingABit()
    {
        // `flags and not MASK` is the only clean way to unset a bit, and is why `not` had to
        // exist on bits at all rather than being dropped for being surprising.
        Oracle("Define flags as 0b1111.\nState flags and not 0b0100.", "0b1011");
    }

    // ── The left operand dominates, for both base and width ──────────────
    // In real bit code the left operand is the accumulator, so its notation is the one that
    // should survive into the output.

    [Fact]
    public void Bits_ResultTakesTheLeftOperandsBaseAndWidth()
    {
        Oracle("State 0xFF and 0b1010.", "0x0A");     // hex on the left → hex out
        Oracle("State 0b1010 and 0xFF.", "0b1010");   // binary on the left → binary out
    }

    [Fact]
    public void Bits_ResultWidensWhenTheValueNeedsMoreRoom()
    {
        // Nothing ever silently falls off the end; narrow deliberately with an `and`.
        Oracle("State 0x0F or 0xF0.", "0xFF");
        Oracle("State 0b1 or 0xFF.",  "0b11111111");  // 1-bit left operand grows to hold 255
    }

    // ── Precedence: and > xor > or, mirroring & > ^ > | ──────────────────

    [Fact]
    public void Bits_XorBindsTighterThanOrAndLooserThanAnd()
    {
        // 0b1100 and 0b1010 = 0b1000; 0b1000 xor 0b0011 = 0b1011; 0b1011 or 0b0100 = 0b1111.
        // Any other grouping gives a different answer, so this pins the precedence.
        Oracle("State 0b1100 and 0b1010 xor 0b0011 or 0b0100.", "0b1111");
    }

    [Fact]
    public void Xor_OnFacts()
    {
        Oracle("State true xor false.", "true");
        Oracle("State true xor true.",  "false");
        Oracle("State false xor false.", "false");
    }

    [Fact]
    public void Xor_WorksInAConditionToo()
    {
        // The condition parser has its own precedence chain; xor has to mean the same in both.
        Oracle("If true xor false:\n    State \"yes\".\nDone.", "yes");
    }

    // ── Gates stay off numbers, which is the whole point ─────────────────

    [Fact]
    public void Gates_RefuseNumbers()
    {
        var ex = Assert.Throws<TypeException>(() => Interpret("State 5 and 3."));
        Assert.Contains("gate", ex.Message);
        Assert.Contains("quantity", ex.Message);
    }

    [Fact]
    public void Not_RefusesNumbers()
    {
        // `not 5` is the expression that started the whole design conversation. It cannot be
        // written, so it cannot surprise anyone with -6.
        var ex = Assert.Throws<TypeException>(() => Interpret("State not 5."));
        Assert.Contains("no bits to flip", ex.Message);
    }

    [Fact]
    public void Gates_RefuseMixedFactAndBits()
    {
        Assert.Throws<TypeException>(() => Interpret("State 0xFF and true."));
        Assert.Throws<TypeException>(() => Interpret("State true and 0xFF."));
    }

    [Fact]
    public void Gates_CsMostFamousPrecedenceBugIsATypeErrorHere()
    {
        // In C, `a & b == c` silently parses as `a & (b == c)` and computes nonsense. Cufet has
        // the same precedence, but keeping bit patterns out of `number` turns the mis-parse into
        // `bits and fact` — refused at compile time instead of quietly wrong.
        Assert.Throws<TypeException>(() => Interpret("State 0xFF and 0x0F = 0x0F."));
    }

    // ── The deliberate short-circuit asymmetry ───────────────────────────

    [Fact]
    public void Gates_ShortCircuitOnFactsButNotOnBits()
    {
        // On facts `and` skips the right side when the left already decides it. On bits it
        // cannot — combining two patterns needs both. Same word, different strategy, chosen by
        // type: the exception matrix arithmetic already makes for '+' and '*'.
        const string facts = """
            Bind fact to noisy, given (the number n):
                State "ran".
                Return true.
            Done.
            If false and cast noisy on (1):
                State "unreachable".
            Done.
            State "end".
            """;
        Oracle(facts, "end");   // "ran" never printed — the right side was skipped
    }

    [Fact]
    public void Gates_BitsAlwaysEvaluateBothSides()
    {
        const string bits = """
            Bind bits to noisy, given (the number n):
                State "ran".
                Return 0x0F.
            Done.
            State 0x00 and cast noisy on (1).
            """;
        Oracle(bits, "ran\n0x00");   // "ran" printed even though 0x00 fixes the answer
    }
}
