using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
///
/// `Define cufet &lt;name&gt; as [ … ].` holds Cufet source under a name; `Cite &lt;name&gt;.` places what it
/// holds. These are pipeline tests rather than interpreter tests for the same reason the generic
/// ones are: the whole feature is a FRONT-END pass, and the claim being made is that neither
/// backend learns a thing. CiteExpansion splices before the hoist and takes the blocks out before
/// the checker returns, so by the time either backend sees a program there is no such thing as a
/// cufet block — only the ordinary declarations it held, at the sites that cited them.
///
/// ★ If that claim were wrong, the COMPILED side is where it would show: the interpreter can carry
/// a stray node around and still behave, where a struct emitted in the wrong order or a type with
/// no C name cannot. So each case asserts the two agree before it asserts anything else.
public class PipelineCufetAxiomTests : PipelineTestBase
{
    [Fact]
    public void ACitedObject_RunsTheSameOnBothBackends()
    {
        const string src = """
            Pull a book on cufet.
                Define cufet vector-shape as [
                    Define object vec2 with (the number x, the number y):
                        Bind number to length-squared:
                            Return one's x * one's x + one's y * one's y.
                        Done.
                    Done.
                ].

                Cite vector-shape.

                Define the arrow as a new vec2 { the x 3, the y 4 }.
                State cast length-squared on (the arrow).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("25", Interpret(src));
    }

    [Fact]
    public void ACitedObject_HeldInASeries_RunsTheSameOnBothBackends()
    {
        // ★ A cited type is an ORDINARY type by the time anything reads it, and a series is what
        // proves it: the element type has to have a C name, a struct, and an emission order. A node
        // that had survived the front end would show up here rather than in the simple case.
        const string src = """
            Pull a book on cufet.
                Define cufet vector-shape as [
                    Define object vec2 with (the number x, the number y):
                        Bind number to sum: Return one's x + one's y. Done.
                    Done.
                ].

                Cite vector-shape.

                Define the corners as a series of vec2 with (
                    a new vec2 { the x 1, the y 2 },
                    a new vec2 { the x 3, the y 4 }).

                For each corner in the corners, repeat:
                    State cast sum on (the corner).
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("3\n7", Interpret(src));
    }

    [Fact]
    public void ACitedObject_FromInsideAFunction_RunsTheSameOnBothBackends()
    {
        // ★★ Where a cited declaration LANDS, with nothing in the feature saying it. A type
        // declaration belongs to the program wherever it is written, so splicing inline at the cite
        // site is the whole mechanism — the object outlives the function that cited it, on both
        // backends, because that is what the language already said about declarations.
        const string src = """
            Pull a book on cufet.
                Define cufet shape as [
                    Define object vec2 with (the number x, the number y):
                        Bind number to sum: Return one's x + one's y. Done.
                    Done.
                ].

                Bind number to made:
                    Cite shape.
                    Define the here as a new vec2 { the x 2, the y 5 }.
                    Return cast sum on (the here).
                Done.

                State cast made on ().
                Define the outside as a new vec2 { the x 1, the y 1 }.
                State cast sum on (the outside).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("7\n2", Interpret(src));
    }

    // ── Two divergences the cite work walked into, neither of them about citing ──
    //
    // ⚠⚠ Both were shipped, both ran interpreted and were REFUSED by the compiler, and the oracle
    // suite could not have found either: no case had put an object definition, a `Pull` block and a
    // function together, and none had called a method by the free-cast spelling inside one.

    [Fact]
    public void AnObjectDefinedInAFunction_InsideAPullBlock_RunsTheSameOnBothBackends()
    {
        // !! The compiler's pull-scope capture check walked into the object's method bodies and
        // reported `one` — the receiver pronoun, bound by the method, declared by nobody — as a
        // capture of the enclosing function. "captures 'one' from the pull scope", for a program
        // the interpreter runs. The bodies are still walked; `one` and the member's own parameters
        // are bound first.
        const string src = """
            Pull a book on the c-language.
                Bind number to made:
                    Define object vec2 with (the number x, the number y):
                        Bind number to sum: Return one's x + one's y. Done.
                    Done.
                    Define the here as a new vec2 { the x 2, the y 5 }.
                    Return cast sum on (the here).
                Done.
                State cast made on ().
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("7", Interpret(src));
    }

    [Fact]
    public void AMethodCalledByTheFreeCastForm_InsideAPullBlock_RunsTheSameOnBothBackends()
    {
        // !! Independent of the one above — the object here is declared at the top of the file.
        // `cast sum on (the here)` is the spelling README teaches, and it writes the member's name
        // in callee position, where the capture walk saw an ordinary variable being read. So ANY
        // method call in that spelling, inside a function inside a `Pull` block, was refused as
        // "captures 'sum' from the pull scope" — a name that is not a variable and cannot be one.
        //
        // ★ The possessive spelling was never affected: there the member is a bare string the walk
        // cannot see. That is the same asymmetry the free-cast form had for generic methods, and it
        // is why this went unnoticed for so long.
        const string src = """
            Define object vec2 with (the number x, the number y):
                Bind number to sum: Return one's x + one's y. Done.
            Done.

            Pull a book on the c-language.
                Bind number to made:
                    Define the here as a new vec2 { the x 2, the y 5 }.
                    Return cast sum on (the here).
                Done.
                State cast made on ().
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("7", Interpret(src));
    }

    [Fact]
    public void AMethodCalledByThePossessiveForm_InsideAPullBlock_IsUnaffected()
    {
        // ! The counter-test. The spelling that always worked, kept beside the one that did not —
        // the point is that the two agree now.
        const string src = """
            Define object vec2 with (the number x, the number y):
                Bind number to sum: Return one's x + one's y. Done.
            Done.

            Pull a book on the c-language.
                Bind number to made:
                    Define the here as a new vec2 { the x 2, the y 5 }.
                    Return cast the here's sum on ().
                Done.
                State cast made on ().
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("7", Interpret(src));
    }

    [Fact]
    public void AFunctionReachingForAPullScopeLocal_IsStillRefused()
    {
        // ! The guard the widening had to keep: loosening the check until it caught nothing would
        // have been the easy wrong fix.
        //
        // ★ The refusal comes from the CHECKER, not from the capture check the widening touched —
        // and it says more than that check ever could, because it knows why the rule exists. The
        // compiler's version is a backstop behind it, which is exactly what makes widening it safe:
        // a program has to get past this to reach it at all.
        const string src = """
            Pull a book on the c-language.
                Define the sum-so-far as 10.
                Bind number to made:
                    Return the sum-so-far + 1.
                Done.
                State cast made on ().
            Done.
            """;
        Assert.Contains("function and method bodies can't see top-level data",
            Assert.Throws<TypeException>(() => CompileRaw(src)).Message);
    }
}
