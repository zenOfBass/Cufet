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
        program = new TypeChecker().Check(program);
        var cSource = new CodeGenerator().Generate(program);

        // A unique stem WITHOUT creating a file: GetTempFileName is unique only while its file exists,
        // and deleting it to reuse the stem releases the name for another thread to be handed.

        var tmp = Path.Combine(Path.GetTempPath(), "cufet-" + Guid.NewGuid().ToString("N"));
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
                RedirectStandardInput = true,   // closed below — a read must give EOF, never the host's stdin
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            proc.StandardInput.Close();
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
        program = new TypeChecker().Check(program);
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

    // ── Equality is on the VALUE — base and width are ignored ────────────
    // The one place width must not be load-bearing. This shipped broken in the literals slice:
    // the runtime value is a record struct, whose default equality compares base and width too,
    // so 0xFF = 0x00FF came back false.

    [Fact]
    public void Bits_EqualityIgnoresWidthAndBase()
    {
        Oracle("State 0xFF = 0x00FF.",      "true");
        Oracle("State 0xFF = 0b11111111.",  "true");
        Oracle("State 0xF = 0x0F.",         "true");
        Oracle("State 0o377 = 0xFF.",       "true");
        Oracle("State 0xFF = 0xF0.",        "false");
    }

    // ── Arithmetic ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("State 0x0F + 0x01.", "0x10")]
    [InlineData("State 0x10 - 0x0F.", "0x01")]
    [InlineData("State 0xFF * 0x02.", "0x1FE")]
    [InlineData("State 0xFF / 0x10.", "0x0F")]   // integer division: 255 / 16 = 15
    [InlineData("State 0xFF % 0x10.", "0x0F")]
    public void Bits_Arithmetic(string source, string expected) => Oracle(source, expected);

    [Fact]
    public void Bits_ArithmeticWidensButNeverShrinks()
    {
        Oracle("State 0xFF + 0x01.", "0x100");     // grew past 8 bits
        // Width never shrinks back once widened — the rule is the LEFT operand's width, raised
        // to fit, and the left operand of the subtraction is already 9 bits wide.
        Oracle("Define b as 0b1.\nState (b * 0x100) - 0x1.", "0b011111111");
    }

    [Fact]
    public void Bits_DivisionIsIntegerDivision()
    {
        // '/' means something different on bits than on numbers — the same surface with a
        // different meaning per operand type, as matrix arithmetic already does.
        Oracle("State 0x07 / 0x02.", "0x03");
        Oracle("State 7 / 2.",       "3.5");
    }

    // ── No representation means it raises, not fails ─────────────────────
    // A value-level failure would ride in the type as `bits or failure` and force an unwrap
    // after every masking expression, which is why divide-by-zero is not one either.

    [Fact]
    public void Bits_SubtractionBelowZeroRaises()
    {
        var ex = Assert.Throws<RuntimeException>(() => Interpret("State 0x00 - 0x1."));
        Assert.Contains("would be negative", ex.Message);
        Assert.Contains("unsigned", ex.Message);
    }

    [Fact]
    public void Bits_OverflowPastSixtyFourBitsRaises()
    {
        var add = Assert.Throws<RuntimeException>(() => Interpret("State 0xFFFFFFFFFFFFFFFF + 0x1."));
        Assert.Contains("64 bits", add.Message);
        var mul = Assert.Throws<RuntimeException>(() => Interpret("State 0x1000000000000000 * 0x10."));
        Assert.Contains("64 bits", mul.Message);
    }

    [Fact]
    public void Bits_DivideAndModuloByZeroRaise()
    {
        Assert.Throws<RuntimeException>(() => Interpret("State 0xFF / 0x0."));
        Assert.Throws<RuntimeException>(() => Interpret("State 0xFF % 0x0."));
    }

    [Fact]
    public void Bits_OverflowMessageIsIdenticalInBothBackends()
    {
        // The message quotes both operands in their own base and width, so the two backends
        // have to format bits identically — worth pinning, since each has its own formatter.
        const string src = "State 0x00 - 0x1.";
        var interpreted = Assert.Throws<RuntimeException>(() => Interpret(src)).Message;
        Assert.Equal("0x00 - 0x1 would be negative, and bits are unsigned (line 1).", interpreted);
    }

    // ── Ordering ─────────────────────────────────────────────────────────

    [Fact]
    public void Bits_Ordering()
    {
        Oracle("State 0x0F is less than 0xFF.",    "true");
        Oracle("State 0xFF is greater than 0x0F.", "true");
        Oracle("State 0xFF < 0x0F.",               "false");
        // Ordering is on the value, so width is ignored here too.
        Oracle("State 0x0F < 0x00FF.",             "true");
    }

    // ── Unary minus is refused ───────────────────────────────────────────

    [Fact]
    public void Bits_UnaryMinusIsRefused()
    {
        var ex = Assert.Throws<TypeException>(() => Interpret("State -0xFF."));
        Assert.Contains("unsigned", ex.Message);
        Assert.Contains("not", ex.Message);   // points at the operation they probably wanted
    }

    [Fact]
    public void Bits_ArithmeticWithANumberIsRefused()
    {
        Assert.Throws<TypeException>(() => Interpret("State 0xFF + 1."));
        Assert.Throws<TypeException>(() => Interpret("State 1 + 0xFF."));
        Assert.Throws<TypeException>(() => Interpret("State 0xFF < 255."));
    }

    // ── Shifts ───────────────────────────────────────────────────────────
    // Shifting is wiring, not a gate: it moves bits rather than combining them. So it is a
    // trailing transform in the `sorted` / `trimmed` family, not an operator.

    [Theory]
    [InlineData("State 0b0001 shifted left by 3.",  "0b1000")]
    [InlineData("State 0xFF shifted left by 4.",    "0xFF0")]   // widened, nothing lost
    [InlineData("State 0xFF shifted right by 4.",   "0x0F")]
    [InlineData("State 0b1111 shifted right by 2.", "0b0011")]
    [InlineData("State 0x1 shifted left by 0.",     "0x1")]
    public void Bits_Shifts(string source, string expected) => Oracle(source, expected);

    [Fact]
    public void Bits_RightShiftDiscardsTheLowBits()
    {
        // The one place something genuinely falls off — and it is not an inconsistency with
        // "nothing is ever lost": discarding the low bits IS a right shift, rather than a
        // failure of representation. Unsigned also means there is no arithmetic-vs-logical
        // question, because there is no sign bit.
        Oracle("State 0b1011 shifted right by 3.", "0b0001");
        Oracle("State 0b1111 shifted right by 8.", "0b0000");
    }

    [Fact]
    public void Bits_ShiftAmountIsANumberNotAPattern()
    {
        // It counts POSITIONS — a quantity, like the 3 in 'item 3 of s'.
        var ex = Assert.Throws<TypeException>(() => Interpret("State 0xFF shifted left by 0x1."));
        Assert.Contains("counts positions", ex.Message);
    }

    [Fact]
    public void Bits_ShiftingANumberIsRefused()
    {
        var ex = Assert.Throws<TypeException>(() => Interpret("State 5 shifted left by 1."));
        Assert.Contains("no bit positions", ex.Message);
    }

    [Fact]
    public void Bits_ShiftAmountMustBeWholeAndNonNegative()
    {
        var frac = Assert.Throws<RuntimeException>(() => Interpret("State 0xFF shifted left by 1.5."));
        Assert.Contains("whole number of positions", frac.Message);
        var neg = Assert.Throws<RuntimeException>(() => Interpret("State 0xFF shifted left by -1."));
        Assert.Contains("cannot be negative", neg.Message);
    }

    [Fact]
    public void Bits_LeftShiftPastTheCeilingRaises()
    {
        // Shifting by at least the width is undefined behaviour in C, so the answer is written
        // out explicitly in the emitted code rather than left to the hardware.
        var ex = Assert.Throws<RuntimeException>(() => Interpret("State 0x1 shifted left by 64."));
        Assert.Contains("64 bits", ex.Message);
        Assert.Throws<RuntimeException>(() => Interpret("State 0xFF shifted left by 60."));
    }

    [Fact]
    public void Bits_TheMaskIdiom()
    {
        // (1 << n) - 1, the standard way to build a mask of n bits.
        Oracle("Define n as 8.\nState (0b1 shifted left by n) - 0x1.", "0b011111111");
    }

    // ── 'left' and 'right' stay ordinary identifiers ─────────────────────

    [Fact]
    public void Shift_DirectionWordsAreNotReserved()
    {
        // 'the left of node' is the most natural thing anyone writes for a binary tree, and one
        // operator is not worth forbidding it. They are matched by lexeme in the shift shape only.
        Oracle("Define node as a record with (the left 1, the right 2).\nState the left of node.", "1");
        Oracle("Define left as 7.\nState left.", "7");
        Oracle("Define right as 9.\nState right.", "9");
    }

    // ── Crossing between quantity and pattern ────────────────────────────
    // Explicit in both directions, since there is no implicit conversion. This is also what
    // recovers the expressiveness a display-only transform would have had: a COMPUTED value can
    // be shown in hex, not only a literal written that way.

    [Theory]
    [InlineData("State 255 converted to hex.",    "0xFF")]
    [InlineData("State 16 converted to hex.",     "0x10")]
    [InlineData("State 0 converted to hex.",      "0x0")]
    [InlineData("State 10 converted to binary.",  "0b1010")]
    [InlineData("State 493 converted to octal.",  "0o755")]
    public void Bits_FromNumber(string source, string expected) => Oracle(source, expected);

    [Fact]
    public void Bits_AComputedValueCanBeShownInHex()
    {
        Oracle("Define total as 200 + 55.\nState total converted to hex.", "0xFF");
    }

    [Fact]
    public void Bits_ToNumberIsTotal()
    {
        // 64 bits always fits a 96-bit mantissa, so this cannot fail — it yields a plain
        // number, not a voidable one the way text-to-number does.
        Oracle("State 0xFF converted to number.",    "255");
        Oracle("State 0b1010 converted to number.",  "10");
        Oracle("State (0xF0 or 0x0F) converted to number.", "255");
        // Plain, not voidable: it can be used directly in arithmetic with no unwrapping.
        Oracle("State (0xFF converted to number) + 1.", "256");
    }

    [Fact]
    public void Bits_ToText()
        => Oracle("State 0xFF converted to text.", "0xFF");

    [Fact]
    public void Bits_ConversionRaisesRatherThanYieldingAVoidable()
    {
        // Matching arithmetic overflow: these are programming errors, not the data condition
        // that makes text-to-number voidable, so a voidable would only force an unwrap at
        // every crossing.
        var frac = Assert.Throws<RuntimeException>(() => Interpret("State 3.5 converted to hex."));
        Assert.Contains("whole number", frac.Message);
        var neg = Assert.Throws<RuntimeException>(() =>
            Interpret("Define n as -1.\nState n converted to hex."));
        Assert.Contains("unsigned", neg.Message);
        var big = Assert.Throws<RuntimeException>(() =>
            Interpret("Define big as 20000000000000000000.\nState big converted to hex."));
        Assert.Contains("64 bits", big.Message);
    }

    [Fact]
    public void Bits_ConvertingABitsValueToABaseIsRefused()
    {
        // It is already a pattern; the message says how to restate it in another base.
        var ex = Assert.Throws<TypeException>(() => Interpret("State 0xFF converted to hex."));
        Assert.Contains("already a bit pattern", ex.Message);
    }

    [Fact]
    public void Bits_ShowingAPatternInAnotherBase()
        => Oracle("State 0xFF converted to number converted to binary.", "0b11111111");

    [Fact]
    public void Bits_BaseNamesAreNotReserved()
    {
        // 'hex', 'binary' and 'octal' are matched by lexeme in the conversion shape only.
        Oracle("Define hex as 5.\nState hex.", "5");
        Oracle("Define binary as 6.\nState binary.", "6");
        Oracle("Define octal as 7.\nState octal.", "7");
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
