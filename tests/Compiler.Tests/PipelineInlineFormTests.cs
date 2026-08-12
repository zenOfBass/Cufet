using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
///
/// ★ ONE rule, not one per construct: <b>every block construct takes a comma and one thing, or a
/// colon and a block closed by `Done.`</b> `If` and `Judge` always worked this way; the rest did
/// not, and nobody could predict which.
///
/// What "one thing" means depends on whether the body must produce a value. A function, getter,
/// constructor or operator overload takes an EXPRESSION and its `Return` is implicit — dropping
/// `Return` and `Done.` is the whole of what the form buys. A void function, setter or destructor
/// takes a STATEMENT, there being no value to imply a return for.
///
/// An inline body parses to an ORDINARY one-statement body, so nothing downstream can tell the two
/// spellings apart — which is what these tests assert: inline output == block output, on both
/// backends.
public class PipelineInlineFormTests : PipelineTestBase
{
    // ── Value bodies: the expression form, with an implicit Return ────────

    [Fact]
    public void AFunction_TakesAnInlineExpressionBody()
    {
        const string inline = """
            Bind number to double, given (the number amount), amount * 2.
            State cast double on (21).
            """;
        const string block = """
            Bind number to double, given (the number amount):
                Return amount * 2.
            Done.
            State cast double on (21).
            """;
        Assert.Equal(Interpret(block), Interpret(inline));
        Assert.Equal(InterpretRaw(inline), CompileRaw(inline));
        Assert.Equal("42", Interpret(inline));
    }

    [Fact]
    public void AGetter_TakesAnInlineExpressionBody()
    {
        const string inline = """
            Define object circle with (the number radius):
                Get area as number, one's radius * one's radius * 3.
            Done.
            Define c as a new circle { the radius 2 }.
            State c's area.
            """;
        const string block = """
            Define object circle with (the number radius):
                Get area as number:
                    Return one's radius * one's radius * 3.
                Done.
            Done.
            Define c as a new circle { the radius 2 }.
            State c's area.
            """;
        Assert.Equal(Interpret(block), Interpret(inline));
        Assert.Equal(InterpretRaw(inline), CompileRaw(inline));
        Assert.Equal("12", Interpret(inline));
    }

    [Fact]
    public void AConstructorAndAnOperatorOverload_TakeInlineExpressionBodies()
    {
        // The overload's expression deliberately spans two lines: an inline body is an expression,
        // and expressions have never cared about newlines. Only the closing `.` ends it.
        const string src = """
            Define object vec with (the number across, the number down).
            Bind making a vec to square-vec, given (the number seed), a new vec { the across seed, the down seed }.
            Bind overloading +, given (the lhs is a vec, the rhs is a vec),
                a new vec { the across lhs's across + rhs's across, the down lhs's down + rhs's down }.

            Define p as cast square-vec on (5).
            Define q as a new vec { the across 1, the down 2 }.
            Define total as p + q.
            State total's across.
            State total's down.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("6\n7", Interpret(src));
    }

    // ── Void bodies: the statement form ───────────────────────────────────

    [Fact]
    public void AVoidFunctionAndASetter_TakeInlineStatementBodies()
    {
        const string inline = """
            Define object circle with (the number radius):
                Set radius given (the number r), one's radius becomes r * 2.
            Done.
            Bind void to shout, given (the text word), State word in uppercase.

            Define c as a new circle { the radius 1 }.
            The c's radius becomes 5.
            State c's radius.
            Cast shout on ("hey").
            """;
        Assert.Equal(InterpretRaw(inline), CompileRaw(inline));
        Assert.Equal("10\nHEY", Interpret(inline));
    }

    [Fact]
    public void ADestructor_TakesAnInlineStatementBody()
    {
        const string src = """
            Define object gate with (the number id).
            Bind unmaking a gate to close-gate, State "closing {one's id}".
            Pull a rabbit.
                Define g as a new gate { the id 9 }.
                State "open".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("open\nclosing 9", Interpret(src));
    }

    // ── Loops: `repeat:` is the discriminator, not the comma ──────────────

    [Fact]
    public void ALoop_TakesAnInlineStatementBody()
    {
        // ★ A loop's comma is already spent on its own header, so the two forms are separated by
        // `repeat:` instead. One token, and the inline form drops it.
        const string inline = """
            Define items as a series of number with (1, 2, 3).
            For each n in items, State n.
            Define i as 0.
            While i is less than 3, the i becomes i + 1.
            State i.
            """;
        const string block = """
            Define items as a series of number with (1, 2, 3).
            For each n in items, repeat:
                State n.
            Done.
            Define i as 0.
            While i is less than 3, repeat:
                The i becomes i + 1.
            Done.
            State i.
            """;
        Assert.Equal(Interpret(block), Interpret(inline));
        Assert.Equal(InterpretRaw(inline), CompileRaw(inline));
        Assert.Equal("1\n2\n3\n3", Interpret(inline));
    }

    [Fact]
    public void AFunctionWithNoParameters_ReachesItsInlineBodyThroughTheSameComma()
    {
        // ★ The comma after the name meant `, given (…)` unconditionally, so the inline form was
        // unavailable to exactly the functions short enough to want it. `given` is the
        // discriminator — the same one-token lookahead the interface-method parser already uses.
        const string src = """
            Define object animal with (the text species, the number legs):
                Get loud-species as text, one's species in uppercase.
                Bind number to leg-pairs, one's legs / 2.
            Done.
            Bind text to greeting, "hello".

            Define rex as a new animal { the species "canine", the legs 4 }.
            State rex's loud-species.
            State cast rex's leg-pairs.
            State cast greeting.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("CANINE\n2\nhello", Interpret(src));
    }

    [Fact]
    public void InlineBodies_CombineWithTheRestOfTheLanguage()
    {
        // The combinations a parser change can quietly break: `Skip` inside an inline loop body
        // (the helper has to raise _loopDepth for it), a conditional expression as an inline
        // expression body, an inline `If` nested as the one statement of an inline body, and an
        // expression body widening into a fallible return type.
        const string src = """
            Bind number to fee, given (the fact is-member), 0 when is-member, otherwise 25.
            Bind void to report, given (the number n), If n is greater than 2, State "big".
            Bind number or failure to halve, given (the number n), n / 2.

            Define items as a series of number with (1, 2, 3, 4).
            For each n in items, Skip.
            For each n in items, If n is greater than 2, State n.
            State cast fee on (true).
            State cast fee on (false).
            Cast report on (5).
            Try to:
                State cast halve on (10).
            Done.
            In case of failure:
                State "the call failed".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("3\n4\n0\n25\nbig\n5", Interpret(src));
    }

    [Fact]
    public void TheBlockFormsAllStillParse()
    {
        // The control. Every construct the inline rule touched, in its block spelling — because a
        // parser change that adds a form and quietly breaks the old one passes every test above.
        const string src = """
            Define object vec with (the number across, the number down):
                Get doubled as number:
                    Return one's across * 2.
                Done.
                Set across given (the number incoming):
                    One's across becomes incoming.
                Done.
                Bind number to sum-parts:
                    Return one's across + one's down.
                Done.
            Done.
            Bind overloading +, given (the lhs is a vec, the rhs is a vec):
                Return a new vec { the across lhs's across + rhs's across, the down 0 }.
            Done.
            Bind void to announce, given (the text word):
                State word.
            Done.

            Define p as a new vec { the across 3, the down 4 }.
            State p's doubled.
            State cast p's sum-parts.
            The p's across becomes 10.
            State p's across.
            Define items as a series of number with (1, 2).
            For each n in items, repeat:
                State n.
            Done.
            Cast announce on ("done").
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("6\n7\n10\n1\n2\ndone", Interpret(src));
    }
}
