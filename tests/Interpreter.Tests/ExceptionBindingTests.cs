using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// The `In case of exception` clause: its binding, its name, and what printing one does.
/// </summary>
/// <remarks>
/// <para>
/// ★ The two arms of one `Try` used to disagree, and the one that DEMANDED more said less.
/// `In case of failure:` takes no binding and reaches its value as `the message of the failure`,
/// while `In case of exception (the exception):` required parentheses that could hold only the
/// word `exception` — a mandatory phrase saying the same thing at every occurrence.
/// </para>
/// <para>
/// ★ The pair this restores already exists for loops: `For each item in items` names the iterand,
/// bare `it` is the implicit one. Making the parenthesised form a RENAME rather than a synonym is
/// what keeps it from being two spellings of one thing, and nesting is what earns it — an inner
/// handler should not have to shadow a name the outer one is still using.
/// </para>
/// </remarks>
public class ExceptionBindingTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var parsed  = new Parser(tokens).Parse();
        var program = new TypeChecker().Check(parsed);
        var output  = new System.IO.StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private const string Divide = """
        Try to:
            State 1 / 0.
        Done.
        """;

    [Fact]
    public void TheBinding_IsOptional_AndDefaultsToTheException()
    {
        // ! Was a parse error — "expected LParen, got Colon". The parentheses were mandatory, so
        // the arm that gives you a name demanded you write it out, while the failure arm beside it
        // needed nothing at all.
        Assert.Equal("Division by zero on line 2.", Run(Divide + """
            In case of exception:
                State the message of the exception.
                Suppress the exception.
            Done.
            """));
    }

    [Fact]
    public void TheBinding_CanBeRenamed()
    {
        // ! Was "expected Exception, got Identifier" — the slot accepted one word, which is why it
        // could not be a name at all. REFERENCE nevertheless described it as "the binding for the
        // exception description", so the docs promised this before the parser allowed it.
        Assert.Equal("Division by zero on line 2.", Run(Divide + """
            In case of exception (the trouble):
                State the message of the trouble.
                Suppress the exception.
            Done.
            """));
    }

    [Fact]
    public void TheOldExplicitForm_StillMeansWhatItMeant()
    {
        // ⚠ `exception` lexes as a KEYWORD, not an identifier, and every existing program writes
        // `(the exception)`. The slot has to keep taking it — an identifier, or that one word.
        Assert.Equal("Division by zero on line 2.", Run(Divide + """
            In case of exception (the exception):
                State the message of the exception.
                Suppress the exception.
            Done.
            """));
    }

    [Fact]
    public void ARenamedBinding_DoesNotAlsoAnswerToTheOldName()
    {
        // ★ A RENAME, not a synonym — which is the whole reason it earns its place. If both names
        // worked, this would be two spellings of one thing and the language has a rule about that.
        var e = Assert.Throws<TypeException>(() => Run(Divide + """
            In case of exception (the trouble):
                State the message of the exception.
            Done.
            """));
        Assert.Contains("exception", e.Message);
    }

    [Fact]
    public void NestedHandlers_CanEachNameTheirOwn()
    {
        // The case that earns the rename: two handlers deep, the implicit name says nothing about
        // which one is meant. Same reason `NestedBareItLoops` exists for bare `it` in loops.
        Assert.Equal("inner: Division by zero on line 3.", Run("""
            Try to:
                Try to:
                    State 1 / 0.
                Done.
                In case of exception (the inner-trouble):
                    State "inner: {the message of the inner-trouble}".
                    Suppress the exception.
                Done.
            Done.
            In case of exception (the outer-trouble):
                State "outer: {the message of the outer-trouble}".
                Suppress the exception.
            Done.
            """));
    }

    // ── Printing one ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PrintingAnException_GivesAPlaceholder_NotAHostTypeName()
    {
        // ! Was `Cufet.Interpreter.Interpreter+ExceptionValue` — a C# class name, in a user's
        // output — while the COMPILER refused to build the same program. One backend printed host
        // internals and the other would not compile: a leak and a divergence in one.
        //
        // ★ `<exception>` follows the convention every other opaque value already uses —
        // `<function>`, `<axiom>`, `<address>`. What is worth reading is reached by name.
        Assert.Equal("<exception>", Run(Divide + """
            In case of exception:
                State the exception.
                Suppress the exception.
            Done.
            """));
    }

    [Fact]
    public void PrintingAFailure_GivesAPlaceholderToo()
    {
        // ⚠ The same defect sat in the failure arm, and nothing had caught it there either: the
        // checker said "No problems found", the interpreter printed `...+FailureValue`, and the
        // compiler refused. Both arms were wrong in exactly the same way.
        Assert.Equal("<failure>", Run("""
            Bind number or failure to risky:
                Return failure "nope".
            Done.

            Try to:
                Define x as cast risky on ().
                State x.
            Done.
            In case of failure:
                State the failure.
            Done.
            """));
    }

    [Fact]
    public void TheMessageIsStillHowYouReadOne()
    {
        // The placeholder must not have cost the accessor — which is the documented idiom and the
        // parallel to `the message of the failure`.
        Assert.Equal("nope", Run("""
            Bind number or failure to risky:
                Return failure "nope".
            Done.

            Try to:
                Define x as cast risky on ().
                State x.
            Done.
            In case of failure:
                State the message of the failure.
            Done.
            """));
    }
}
