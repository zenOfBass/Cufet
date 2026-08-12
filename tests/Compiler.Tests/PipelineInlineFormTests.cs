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

    // ── The failures, which are where a form like this is actually judged ──
    //
    // ★ The rule is learnable; the errors were not. Every one of these used to be a bare parser
    // expectation — "expected expression, got Return" — and one of them ("expected Becomes")
    // pointed at the wrong idea entirely, having decided the first word was an assignment target.
    // A teaching language does its teaching here.

    [Fact]
    public void WritingReturnInAnInlineBody_SaysToDropIt()
    {
        const string src = """
            Bind number to double, given (the number n), Return n * 2.
            State cast double on (4).
            """;
        var ex = Assert.ThrowsAny<Exception>(() => Interpret(src));
        Assert.Contains("gives its value back on its own", ex.Message);
    }

    [Fact]
    public void AStatementWhereAValueBelongs_SaysTheBodyIsAnExpression()
    {
        const string src = """
            Bind number to double, given (the number n), State n.
            State cast double on (4).
            """;
        var ex = Assert.ThrowsAny<Exception>(() => Interpret(src));
        Assert.Contains("opens a statement", ex.Message);
        Assert.Contains("EXPRESSION", ex.Message);
    }

    [Fact]
    public void AnExpressionWhereAStatementBelongs_SaysTheBodyIsAStatement()
    {
        // Previously "expected Becomes" — the parser had decided `w` was an assignment target.
        const string src = """
            Bind void to shout, given (the text w), w in uppercase.
            Cast shout on ("hi").
            """;
        var ex = Assert.ThrowsAny<Exception>(() => Interpret(src));
        Assert.Contains("STATEMENT, not an", ex.Message);
        Assert.DoesNotContain("expected Becomes", ex.Message);
    }

    [Fact]
    public void ARepeatWithNoDone_ReportsAtTheRepeat_NotAtTheEndOfTheFile()
    {
        const string src = """
            Define items as a series of number with (1, 2).
            For each n in items, repeat: State n.
            """;
        var ex = Assert.ThrowsAny<Exception>(() => Interpret(src));
        // Line 2 is the loop. The old message reported line 3, column 1 — the end of the file.
        Assert.Contains("Line 2", ex.Message);
        Assert.DoesNotContain("got Eof", ex.Message);
    }

    // ── The two constructs the first sweep missed ──
    //
    // Both were parsed by branches nowhere near the other body parsers: the consumer loop lives
    // inside ParseForEachStatement behind a `from` check, and a task has its own statement parser
    // entirely. Neither is reachable from the code the rest of this rule went through, which is
    // exactly why they were missed — and why they need tests rather than trust.
    //
    // ⚠ INTERPRETER ONLY. Tasks, channels and pipe stages need pthreads, which mingw does not
    // have, so these programs are Windows-skipped for the compiler in ExampleOracleTests. Holding
    // them to the front end and the interpreter is the honest maximum here.

    [Fact]
    public void AConsumerLoop_TakesAnInlineStatementBody()
    {
        // Its header spends no comma — there is no `in <series>` clause — so the discriminator is
        // the plain comma-versus-colon rule, not `repeat:`.
        const string inline = """
            Bind void to run-report, given (the series of number sums):
                Bind void to emit-sums, for each s in sums, output s.
                Bind void to keep-large, for each s from input, if s is greater than 20, output s.
                Bind void to shout, for each s from input, state "large: {s converted to text}".
                emit-sums | keep-large | shout.
            Done.
            Cast run-report on (a series of number with (5, 30, 40)).
            """;
        Assert.Equal("large: 30\nlarge: 40", Interpret(inline));
    }

    [Fact]
    public void ATask_TakesAnInlineStatementBody_BecauseItDeclaresNoReturnType()
    {
        // ★ The one value-bearing body that CANNOT take the expression form. Every other one
        // states its return type on the same line, and that declaration is what lets `Return` be
        // implicit. A task's header says nothing — it may hand back a result or merely send on a
        // channel — so its inline body is a statement and `return …` stays written out.
        const string returns = """
            Pull a rabbit.
                Have rabbit start a task as batch-1, return 1 + 2 + 3 + 4 + 5.
                Have rabbit start a task as batch-2, return 6 + 7 + 8 + 9 + 10.
                State (the awaited result of batch-1) + (the awaited result of batch-2).
            Done.
            """;
        Assert.Equal("55", Interpret(returns));

        const string sends = """
            Pull a rabbit.
                Define nums as a channel of number.
                Have rabbit start a task as producer, send 7 through nums.
                State (the delivery from nums) but void is 0.
                Close nums.
            Done.
            """;
        Assert.Equal("7", Interpret(sends));
    }
}
