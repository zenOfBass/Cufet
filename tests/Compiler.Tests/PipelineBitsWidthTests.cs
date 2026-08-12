using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
///
/// A `bits` has always carried a width — it is what drives the leading zeros when it prints — but
/// a program could neither read it nor choose one. `huffmancoding` carried the width in a second
/// field and kept the two in step by hand; its own header said so.
///
/// ★ Neither half needed a new keyword. `the width of p` already PARSED as an ordinary named-field
/// access and only failed in the checker, so it is resolved there — which is why `width` is still
/// a legal field name. `at` and `bits` are matched by lexeme in the postfix position, exactly as
/// `item at (r, c)` already is, so both stay legal identifiers too.
public class PipelineBitsWidthTests : PipelineTestBase
{
    [Fact]
    public void TheWidthOfABitsValue_IsReadable()
    {
        const string src = """
            Define p as 0b1010.
            State the width of p.
            State the width of 0x0F.
            State the width of 0b000.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("4\n8\n3", Interpret(src));
    }

    [Fact]
    public void AFieldNamedWidth_StillWorks()
    {
        // ★ The control on the no-keyword decision. Reserving `width` would have cost every user a
        // common noun to buy a property only one type has — and `huffmancoding` had such a field.
        const string src = """
            Define object hcode with (the bits pattern, the number width).
            Define c as a new hcode { the pattern 0b1, the width 3 }.
            State the width of c.
            State c's width.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("3\n3", Interpret(src));
    }

    [Fact]
    public void AStatedWidth_ProducesLeadingZerosNoOperandEverHeld()
    {
        // The display gap this closes: a width is otherwise only ever RAISED to fit the value, so
        // `0b0 shifted left by 2` stays `0b0`. A stated width is the only way to ask for the zeros.
        const string src = """
            State 0b0 shifted left by 2.
            State 0b0 at 3 bits.
            State 0b101 at 8 bits.
            State 0x0F at 16 bits.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("0b0\n0b000\n0b00000101\n0x000F", Interpret(src));
    }

    [Fact]
    public void TheWidthMayBeComputed()
    {
        // The whole reason a literal form like `3 zero bits` was not worth adding separately:
        // the width a packer needs is arithmetic, not a constant.
        const string src = """
            Define n as 5.
            Define p as 0b1 at n bits.
            State p.
            State the width of p.
            State 0b1 at (n + 3) bits.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("0b00001\n5\n0b00000001", Interpret(src));
    }

    [Fact]
    public void NarrowingThatWouldDropASetBit_IsRefusedAtCheckTimeWhenKnowable()
    {
        // Both literal, so the answer is knowable before the program runs.
        var ex = Assert.ThrowsAny<Exception>(() => Interpret("State 0b111111 at 4 bits."));
        Assert.Contains("cannot hold this value", ex.Message);
        Assert.Contains("needs 6", ex.Message);
    }

    [Fact]
    public void NarrowingWithAComputedWidth_IsARuntimeErrorOnBothBackends()
    {
        // ★ Not a `failure` — that would force a `Try` around an operation that is almost always
        // fine. It is the class dividing by zero is in. Both backends must say the same thing.
        const string src = """
            Define w as 4.
            State 0b111111 at w bits.
            """;
        var ex = Assert.ThrowsAny<Exception>(() => Interpret(src));
        Assert.Contains("cannot hold this value", ex.Message);

        // Widening the same value is fine, which is what makes the refusal about lost bits
        // rather than about narrowing as such.
        const string ok = """
            Define w as 8.
            State 0b111111 at w bits.
            """;
        Assert.Equal(InterpretRaw(ok), CompileRaw(ok));
        Assert.Equal("0b00111111", Interpret(ok));
    }

    [Fact]
    public void NarrowingThatLosesNothing_IsAllowed()
    {
        // The refusal is about dropped bits, not about the direction of the change.
        const string src = """
            State 0b00000001 at 2 bits.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("0b01", Interpret(src));
    }
}
