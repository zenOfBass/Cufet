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
}
