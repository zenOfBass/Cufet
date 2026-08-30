using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// Two free functions cannot share a name.
/// </summary>
/// <remarks>
/// <para>
/// ⚠⚠ This was ACCEPTED before, silently, and the later declaration won. `check` reported "No
/// problems found" for a file declaring the same name and signature twice. Every other place two
/// readings could collapse into one is a refusal — two overloads on an ordered pair, a name that
/// is both a method and a free function, a `Judge` that misses a case — and this was the one that
/// was not.
/// </para>
/// <para>
/// ★ ALL duplicates, not only identical signatures. Different parameter types is the shape open
/// dispatch will eventually give a meaning to; until it does, letting it through leaves exactly
/// the silent trap this closes — the later declaration winning while the writer believes both are
/// reachable. That is not speculation about a future feature: the old behaviour is measured in
/// <see cref="DifferentParameterTypesAreStillTheSameName"/>.
/// </para>
/// </remarks>
public class DuplicateFunctionNameTests
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

    private static TypeException Refused(string source) =>
        Assert.Throws<TypeException>(() => Run(source));

    [Fact]
    public void TheSameFunctionDeclaredTwiceIsRefused()
    {
        var e = Refused("""
            Bind number to eval, given (the number n):
                Return n.
            Done.

            Bind number to eval, given (the number n):
                Return n * 2.
            Done.

            State cast eval on (5).
            """);
        Assert.Contains("'eval' is declared twice", e.Message);
    }

    [Fact]
    public void DifferentParameterTypesAreStillTheSameName()
    {
        // ⚠ The behaviour being replaced, recorded so the reason survives: with two `eval`s of
        // different parameter types, EVERY call reached the second one. Passing a num-node was
        // refused with "argument 1 of 'eval' must be a add-node" — an error about the declaration
        // the writer wasn't calling.
        var e = Refused("""
            Define object num-node with (the number value).
            Define object add-node with (the number left, the number right).

            Bind number to eval, given (the num-node node):
                Return node's value.
            Done.

            Bind number to eval, given (the add-node node):
                Return node's left + node's right.
            Done.

            State cast eval on (a new num-node { the value 7 }).
            """);
        Assert.Contains("'eval' is declared twice", e.Message);
    }

    [Fact]
    public void TheMessageNamesBothDeclarations()
    {
        // ! A reader needs the line they wrote and the line they forgot. One without the other
        // sends them looking.
        var e = Refused("""
            Bind number to helper, given (the number n):
                Return n.
            Done.

            Bind number to helper, given (the number n):
                Return n.
            Done.
            """);
        Assert.Contains("already declared on line 1", e.Message);
        Assert.Contains("line 5", e.Message);
    }

    [Fact]
    public void TwoPullBlocksCannotEachDeclareTheSameName()
    {
        // ★★ Not a false positive — a real collision this surfaces. Both bodies are hoisted into
        // the same top-level scope, so before this refusal a call in the FIRST block reached the
        // SECOND block's body. The blocks look independent and were not.
        var e = Refused("""
            Pull a book on math.
                Bind number to helper, given (the number n):
                    Return n.
                Done.
            Done.

            Pull a book on chance.
                Bind number to helper, given (the number n):
                    Return n * 2.
                Done.
            Done.
            """);
        Assert.Contains("'helper' is declared twice", e.Message);
    }

    // ── What is untouched ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void MethodsOnDifferentTypesMayShareAName()
    {
        // The control that matters most. A method is not a free function — it is reached through
        // its owner, and two types each having a `speak` is the ordinary case, not a collision.
        Assert.Equal("woof\nmeow", Run("""
            Define object dog with (the number age):
                Bind text to speak:
                    Return "woof".
                Done.
            Done.

            Define object cat with (the number age):
                Bind text to speak:
                    Return "meow".
                Done.
            Done.

            Define rex as a new dog { the age 1 }.
            Define tom as a new cat { the age 2 }.
            State cast rex's speak on ().
            State cast tom's speak on ().
            """));
    }

    [Fact]
    public void ANestedFunctionMayShadowATopLevelOne()
    {
        // ⚠ Only HOISTED declarations are compared, and a function declared inside a body is not
        // hoisted — it is an ordinary local declaration that shadows, the way a local binding
        // does. Printing 10 rather than 5 is what says the inner one was reached.
        Assert.Equal("10", Run("""
            Bind number to helper, given (the number n):
                Return n.
            Done.

            Bind number to outer, given (the number n):
                Bind number to helper, given (the number m):
                    Return m * 2.
                Done.
                Return cast helper on (n).
            Done.

            State cast outer on (5).
            """));
    }
}
