using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// A function that leaves a blank in its signature — `Bind series of element to first-two`.
/// </summary>
/// <remarks>
/// ★ An object declares its blank by POSITION, in the slot after its own name. A function has no
/// such slot, so its SIGNATURE introduces them: a type name that names nothing, appearing at least
/// TWICE. Twice is the whole safety argument — a typo mentions its mistake once, so it stays an
/// unknown type rather than quietly turning the function generic. Every real case uses its blank
/// twice by nature, because the point is that two positions agree.
///
/// ★★ This is the language's first real INFERENCE: an object states its filling outright
/// (`a stack of number`) while a function's is read off its arguments. It is kept as shallow as
/// inference can be — one structural match per argument, no unification variables, no ordering, no
/// backtracking. A blank matches the same type everywhere it appears, or the call is refused.
/// </remarks>
public class GenericFunctionTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var parsed  = new Parser(tokens).Parse();
        var program = new TypeChecker().Check(parsed);   // ★ the filled-in one, not `parsed`
        var output  = new System.IO.StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static TypeException Refused(string source) =>
        Assert.Throws<TypeException>(() => Run(source));

    private const string FirstTwo = """
        Bind series of element to first-two, given (the series of element xs):
            Define out as a series of element.
            Insert the first of xs into out.
            Insert item 2 of xs into out.
            Return out.
        Done.

        """;

    /// <summary>One body, two fillings, each keeping its own element type.</summary>
    [Fact]
    public void OneBody_ServesTwoFillings()
    {
        Assert.Equal("2\na", Run(FirstTwo + """
            Define nums as a series of number with (1, 2, 3).
            Define words as a series of text with ("a", "b", "c").
            State the number of (cast first-two on (nums)).
            State the first of (cast first-two on (words)).
            """));
    }

    /// <summary>
    /// The blank may be the thing that varies inside a `voidable` — `minimum`'s exact shape.
    /// </summary>
    /// <remarks>
    /// ★ This is the shape the standard library actually needs: `minimum` and `maximum` are
    /// `series of element` → `voidable element`, void on empty. Matching has to reach through the
    /// voidable to learn the blank, not just compare the outsides.
    /// </remarks>
    [Fact]
    public void ABlankInsideAVoidable_IsFilledIn()
    {
        Assert.Equal("7\nnone", Run("""
            Bind voidable element to first-or-none, given (the series of element xs):
                If the number of xs is 0:
                    Return void.
                Done.
                Return the first of xs.
            Done.

            Define nums as a series of number with (7, 8).
            Define empty as a series of text.
            State (cast first-or-none on (nums)) but void is 0.
            State (cast first-or-none on (empty)) but void is "none".
            """));
    }

    /// <summary>
    /// ⚠ THE guard: a type name used ONCE is a typo, not a blank.
    /// </summary>
    /// <remarks>
    /// Without the twice rule, `given (the nubmer n)` makes a generic function that accepts
    /// anything, and the misspelling never surfaces — trading the language's best asset, errors
    /// that name the mistake, for terseness. It has to stay an unknown type.
    /// </remarks>
    [Fact]
    public void ATypedNameUsedOnce_IsStillATypo()
    {
        Assert.Contains("not a defined type", Refused("""
            Bind number to twice, given (the nubmer n):
                Return n + n.
            Done.
            State cast twice on (2).
            """).Message);
    }

    [Fact]
    public void ArgumentsThatDisagreeAboutABlank_AreRefused()
    {
        Assert.Contains("for the same blank", Refused("""
            Bind element to pick, given (the element left, the element right):
                Return left.
            Done.
            State cast pick on (1, "two").
            """).Message);
    }

    [Fact]
    public void ABlankNoArgumentCanFill_IsRefused()
    {
        // The filling is read off the arguments, so a blank living only in the return type has
        // nothing to be read from.
        Assert.Contains("can't tell what", Refused("""
            Bind map from element to element to make-map, given (the number n):
                Return a map from text to text.
            Done.
            State the number of (cast make-map on (3)).
            """).Message);
    }
}
