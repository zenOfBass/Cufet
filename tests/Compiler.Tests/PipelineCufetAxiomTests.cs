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
    // -- An axiom that says what it gives back is something you RUN ------------
    //
    // ⭐⭐ The c-language tag's rule, unchanged: says what it gives back ⇒ run it; says nothing ⇒
    // source. A runnable cufet axiom is lowered to a `Bind` by the PARSER, so what reaches either
    // backend is an ordinary function — which is the same claim the block half makes, and the
    // compiled side is again where it would break if it were false.

    [Fact]
    public void ARunnableAxiom_RunsTheSameOnBothBackends()
    {
        const string src = """
            Pull a book on cufet.
                Define cufet number doubled, given (the number value), as [
                    Return the value * 2.
                ].
                State cast doubled on (21).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("42", Interpret(src));
    }

    [Fact]
    public void ARunnableAxiomHoldingALoop_RunsTheSameOnBothBackends()
    {
        // ★ A body, not an expression, so a loop goes in one. C reaches the same capability through
        // a statement-expression, which is C's way of putting statements where an expression goes.
        const string src = """
            Pull a book on cufet.
                Define cufet number sum-to, given (the number top), as [
                    Define the total as 0.
                    For each step in range 1 to the top, repeat:
                        The total becomes the total + step.
                    Done.
                    Return the total.
                ].
                State cast sum-to on (10).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("55", Interpret(src));
    }

    [Fact]
    public void ACAxiomHoldingALoop_RunsTheSameOnBothBackends()
    {
        // ★ The other half of "same power, each language spelled its own way" — and this already
        // worked, it was simply never written down. A statement-expression is how C puts statements
        // where an expression goes, and the axiom's source is spliced where an expression goes.
        const string src = """
            Pull a book on the c-language.
                Define c-language number sum-to, given (the number top),
                    as [({ int s = 0; for (int i = 1; i <= (int)the top; i++) s += i; s; })].
                State cast sum-to on (10).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("55", Interpret(src));
    }

    [Fact]
    public void ARunnableAxiomGivingBackASeries_RunsTheSameOnBothBackends()
    {
        // ★ No crossing restriction, and none missing: C is limited to a number, a fact and a
        // voidable text because those are what survive the BOUNDARY, and there is no boundary here.
        // A series has to have a C name and a struct on the compiled side, so this is the case that
        // would show if a cufet axiom were being treated as foreign anywhere.
        const string src = """
            Pull a book on cufet.
                Define cufet series of number first-few as [
                    Return a series of number with (1, 2, 3).
                ].
                State cast first-few on ().
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("(1, 2, 3)", Interpret(src));
    }

    [Fact]
    public void ARunnableAxiomHeldAsAValue_RunsTheSameOnBothBackends()
    {
        const string src = """
            Pull a book on cufet.
                Define cufet number doubled, given (the number value), as [
                    Return the value * 2.
                ].
                Define the operation as doubled.
                State cast the operation on (21).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("42", Interpret(src));
    }

    [Fact]
    public void ACAxiomWithAResultCCannotCarry_SaysSo()
    {
        // ! A knock-on the widened result-type parse had to get right. The gate used to accept five
        // words, so `c-language series of number` was a PARSE error about a stray token; now it
        // parses and reaches the sentence that exists to explain it. Kept here because widening
        // that gate for cufet must not have cost C its message.
        const string src = """
            Pull a book on the c-language.
                Define c-language series of number nope as [getpid()].
                State cast nope on ().
            Done.
            """;
        Assert.Contains("cannot give back a series of numbers yet",
            Assert.Throws<TypeException>(() => CompileRaw(src)).Message);
    }
    [Fact]
    public void AValueFromABlock_LandsAtEachCiteSite_OnBothBackends()
    {
        // ⭐⭐ Where `Cite` earns its keep, and the case the compiled side has to agree on. A TYPE
        // belongs to the program wherever it is written, so citing a block of objects places
        // nothing a plain declaration would not have. A VALUE lands at the site that cited it — so
        // one block, two cite sites, two independent locals. If the splice were doing anything
        // cleverer than placing statements, these two numbers would not both be right.
        const string src = """
            Pull a book on cufet.
                Define cufet counters as [
                    Define the tally as 0.
                ].

                Bind number to first:
                    Cite counters.
                    The tally becomes the tally + 5.
                    Return the tally.
                Done.

                Bind number to second:
                    Cite counters.
                    Return the tally.
                Done.

                State cast first on ().
                State cast second on ().
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("5\n0", Interpret(src));
    }

    [Fact]
    public void ABlockUsingProgramScopeNames_RunsTheSameOnBothBackends()
    {
        // ★ What a block IS allowed to reach for: a function, a `permanently` constant, and a type
        // a sibling block declared. All three mean the same thing wherever the block is placed,
        // which is the test Q1 applies.
        const string src = """
            Pull a book on cufet.
                Define the starting-tally as 10 permanently.

                Bind number to doubled-of, given (the number value):
                    Return the value * 2.
                Done.

                Define cufet shapes as [
                    Define object vec2 with (the number x, the number y):
                        Bind number to sum: Return one's x + one's y. Done.
                    Done.
                ].

                Define cufet counters as [
                    Define the tally as cast doubled-of on (the starting-tally).
                    Define the origin as a new vec2 { the x 0, the y 0 }.
                ].

                Cite shapes.
                Cite counters.
                State the tally.
                State cast sum on (the origin).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("20\n0", Interpret(src));
    }
    [Fact]
    public void ABlockHoldingAFunctionAndAValue_RunsTheSameOnBothBackends()
    {
        // ⭐ The two halves of a block landing in two different places from one `Cite`: the
        // function becomes a free function, the value a local of the scope that cited it. The
        // compiled side is where that would break — a free function is a C function at file scope
        // and a local is a variable in a frame, so getting the two confused could not stay quiet.
        const string src = """
            Pull a book on cufet.
                Define cufet helpers as [
                    Define the scale as 3.
                    Bind number to doubled, given (the number value):
                        Return the value * 2.
                    Done.
                ].

                Cite helpers.
                State cast doubled on (21).
                State the scale.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("42\n3", Interpret(src));
    }
}
