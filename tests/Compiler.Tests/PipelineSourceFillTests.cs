using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
///
/// Source FILLED and handed back — `eq-function on (…)` without `cast` — and turned into text with
/// `converted to text` (DESIGN *An axiom's text as a value*). Three kinds of hole: code (another
/// axiom of the same language), a term (text checked as a C name), and a value (a C literal).
/// Nothing here runs C, so none of it needs a toolchain to interpret.
public class PipelineSourceFillTests : PipelineTestBase
{
    [Fact]
    public void FilledSource_IsTheSameTextOnBothBackends()
    {
        const string src = """
            Pull a book on the c-language.
                Define c-language eq-function, given (the term type-c, the term eq-name, the c-language axiom test), as [static int the eq-name(the type-c a, the type-c b) { return the test; }].
                Define c-language value-equal, given (the term eq-name), as [the eq-name(a.value, b.value)].
                Define c-language literal, given (the text words, the number count, the fact loud), as [f(the words, the count, the loud)].

                Define fragment as eq-function on ("CufetDec", "CufetDec_eq", value-equal on ("inner_eq")).
                State fragment converted to text.
                State "hole: {fragment}".
                State eq-function on ("int", "int_eq", [a == b]) converted to text.
                State literal on ("say \"hi\"\n\tnow\\", -5, true) converted to text.
                State literal on ("", 1.25, false) converted to text.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal(string.Join("\n",
            "static int CufetDec_eq(CufetDec a, CufetDec b) { return inner_eq(a.value, b.value); }",
            "hole: static int CufetDec_eq(CufetDec a, CufetDec b) { return inner_eq(a.value, b.value); }",
            "static int int_eq(int a, int b) { return a == b; }",
            "f(\"say \\\"hi\\\"\\n\\tnow\\\\\", (-5), 1)",
            "f(\"\", 1.25, 0)"), Interpret(src));
    }

    /// <remarks>
    /// ★ A function assembling a fragment hands it back unrun — `Bind c-language axiom to …` — and a
    /// bare `[…]` takes its language from the hole it lands in: an argument or a return.
    /// </remarks>
    [Fact]
    public void Source_PassedToAndHandedBackFromFunctions()
    {
        const string src = """
            Pull a book on the c-language.
                Define c-language call, given (the term name, the c-language axiom left, the c-language right), as [the name(the left, the right)].
                Define c-language block, given (the c-language axiom inner, the c-language axiom later), as [{ the inner; the later }].

                Bind c-language axiom to equal-by, given (the text kind, the c-language axiom left, the c-language axiom right):
                    If kind is "text":
                        Return call on ("strcmp", left, right).
                    Done.
                    Return [0].
                Done.

                State cast equal-by on ("text", [a.key], [b.key]).
                State cast equal-by on ("number", [a], [b]).
                Define both as block on (cast equal-by on ("text", [p], [q]), [x = 1]).
                State both.
                Define first as call on ("f", [1], [2]).
                Define second as call on ("f", [1], [2]).
                State first converted to text is second converted to text.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("strcmp(a.key, b.key)\n0\n{ strcmp(p, q); x = 1 }\ntrue", Interpret(src));
    }

    /// <remarks>
    /// ⚠ What fills a hole is NEVER searched for holes. `the left` inside the text handed in stays
    /// as written, because the source is cut at its own holes before anything goes in.
    /// </remarks>
    [Fact]
    public void WhatFillsAHole_IsNeverSearchedForHoles()
    {
        const string src = """
            Pull a book on the c-language.
                Define c-language pair, given (the c-language axiom left, the text right), as [the left + the right].
                State pair on ([the left], "the left") converted to text.
                Define c-language twice, given (the term read, the term read-only), as [the read the read-only].
                State twice on ("r", "ro") converted to text.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("the left + \"the left\"\nr ro", Interpret(src));
    }

    [Fact]
    public void ATermThatIsNotACName_IsRefusedWhenFilled_InTheSameWords()
    {
        const string src = """
            Pull a book on the c-language.
                Define c-language named, given (the term name), as [the name].
                For each written in a series with ("ok_1", "9lives", "two words", "", "int"), repeat:
                    Try to:
                        State named on (written) converted to text.
                    Done.
                    In case of exception (the exception):
                        State the message of the exception.
                        Suppress the exception.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal(string.Join("\n",
            "ok_1",
            "'9lives' is not a name in C, so it cannot fill the term 'name'. This happened on line 5.",
            "'two words' is not a name in C, so it cannot fill the term 'name'. This happened on line 5.",
            "'' is not a name in C, so it cannot fill the term 'name'. This happened on line 5.",
            "int"), Interpret(src));
    }

    /// <remarks>
    /// ★ Filling runs no C, so the interpreter needs no toolchain for it — the playground can run
    /// it. Interpreted here with NO foreign runner at all.
    /// </remarks>
    [Fact]
    public void Filling_NeedsNoToolchain()
    {
        const string src = """
            Pull a book on the c-language.
                Define c-language named, given (the term name, the number n), as [the name(the n)].
                State named on ("f", 3) converted to text.
            Done.
            """;
        var program = new TypeChecker().Check(new Parser(new CufetLexer(src).Tokenize()).Parse());
        var output = new StringWriter();
        new CufetInterpreter(output).Execute(program);
        Assert.Equal("f(3)", Norm(output.ToString()));
    }

    [Fact]
    public void SourceWithHoles_IsNotPastedAboveTheProgram()
    {
        var c = GenerateC("""
            Pull a book on the c-language.
                Define c-language helper as [static int twice(int x) { return x * 2; }].
                Define c-language named, given (the term name), as [static int the name(void);].
                State named on ("f") converted to text.
            Done.
            """);
        Assert.Contains("static int twice(int x)", c);
        Assert.DoesNotContain("static int the name(void);\n", c);
    }

    [Theory]
    [InlineData("State cast eq-function on (\"x\", [1]).", "source, and source is filled rather than run")]
    [InlineData("State seven on ().", "'seven' is not source with holes")]
    [InlineData("State twice on (3).", "'twice' is not source with holes")]
    [InlineData("State eq-function on (\"x\", \"1\") converted to text.", "'test' takes a c-language axiom, but a text was given")]
    [InlineData("State eq-function converted to text.", "has to be filled before it is a value")]
    [InlineData("State eq-function on (the type-c \"x\", the test [1]) converted to text.", "filled by position, not by name")]
    [InlineData("Define c-language bad, given (the c-language number axiom code), as [the code].", "cannot fill c-language source")]
    [InlineData("Define c-language bad, given (the address place), as [the place].", "'place' takes address, and that cannot fill")]
    [InlineData("Define c-language number runs, given (the term t), as [the t].", "'t' is a term, and only source has terms")]
    [InlineData("Bind number to job, given (the c-language axiom given (the text) code): Return 1. Done.", "still has holes")]
    public void SourceMisused_IsRefused(string line, string fragment)
    {
        var e = Assert.Throws<TypeException>(() => GenerateC($$"""
            Pull a book on the c-language.
                Define c-language eq-function, given (the term type-c, the c-language axiom test), as [int the type-c(void) { return the test; }].
                Define c-language number seven as [7].
                Bind number to twice, given (the number n): Return n * 2. Done.
                {{line}}
            Done.
            """));
        Assert.Contains(fragment, e.Message);
    }

    [Fact]
    public void CufetSource_IsFilledByCite_NotByOn()
    {
        // 2026-11-05: cufet source has holes now (PipelineCufetHoleTests), and `Cite` fills them.
        // `on` hands back a value, and cufet source is not a value: it is gone before the program runs.
        var e = Assert.ThrowsAny<Exception>(() => GenerateC("""
            Pull a book on cufet.
                Define cufet src, given (the term made), as [Define the made as 1.].
                State src on ("tally").
            Done.
            """));
        Assert.DoesNotContain("source takes no parameters", e.Message);
    }
}
