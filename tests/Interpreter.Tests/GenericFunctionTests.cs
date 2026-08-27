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

    /// <summary>
    /// A METHOD may leave a blank too — which is what a book written in Cufet needs.
    /// </summary>
    /// <remarks>
    /// ★ A book-as-module is an object whose members are methods, so `unique` being generic at the
    /// top level is not enough: it has to be generic as a member. Three things had to hold at once,
    /// and each was a real bug first:
    ///
    /// ⚠ Detection runs AFTER every type name is registered. Scanning inside the loop that
    /// populates the type table consults a half-built one, so a method taking a type defined later
    /// in the file reads as a blank — and under the twice rule a method with two such parameters
    /// turns generic instead of erroring.
    ///
    /// ⚠ Method lookup goes through the type TABLE, not a captured instance. `Pull` binds the
    /// ObjectType as it was at pull time, and filling a method adds a member afterwards.
    ///
    /// ⚠ The resolved-name side channel is never overwritten. Check re-enters itself on a spliced
    /// program where the template is gone, so instantiation returns null there; assigning that
    /// wipes the answer the first pass worked out.
    /// </remarks>
    [Fact]
    public void AGenericMethodOnAModule_ServesTwoFillings()
    {
        Assert.Equal("(1, 2, 3)\n(a, b)", Run("""
            Define object kit with () and module:
                Bind series of element to unique, given (the series of element xs):
                    Define out as a series of element.
                    For each x in xs, repeat:
                        Define seen as false.
                        For each y in out, repeat:
                            If y is x:
                                The seen becomes true.
                            Done.
                        Done.
                        If seen is false:
                            Insert x into out.
                        Done.
                    Done.
                    Return out.
                Done.
            Done.

            Pull kit.
                State cast kit's unique on (a series of number with (1, 2, 2, 3, 1)).
                State cast kit's unique on (a series of text with ("a", "b", "a")).
            Done.
            """));
    }

    /// <summary>A method taking a type defined LATER in the file is not a blank.</summary>
    /// <remarks>
    /// ⚠ The regression guard for the detection-ordering bug. `holder` is declared after `user`, so
    /// scanning `user`'s methods before every name is registered reads `holder` as an unknown type —
    /// and it appears twice, which is exactly what would tip it into being treated as a blank.
    /// </remarks>
    [Fact]
    public void AMethodTakingATypeDefinedLater_IsNotABlank()
    {
        Assert.Equal("7", Run("""
            Define object user with ():
                Bind number to sum-of, given (the holder left, the holder right):
                    Return left's n + right's n.
                Done.
            Done.

            Define object holder with (the number n).

            Define u as a new user { }.
            State cast sum-of on (u, a new holder { the n 3 }, a new holder { the n 4 }).
            """));
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
    // -- A filled body's refusal is reported where the FILLING happened ---------

    [Fact]
    public void AFilledGenericThatRefuses_ReportsAtTheCall()
    {
        // !! The body is checked by a DIFFERENT TypeChecker, on a spliced program, and that checker
        // has never heard of the call that filled it -- so the error landed on the body's own line.
        // Line 3 below is not wrong; it is only wrong for what line 10 passed in, and telling the
        // writer to fix line 3 would break the filling that works.
        //
        // * The rule this restores is the one the module-needs check states for itself: "reported
        // at the pull rather than at the call: the caller wrote this line, and the missing name
        // belongs in it."
        var e = Refused("""
            Bind element to doubled-first, given (the series of element items):
                Define the head as the first of items.
                Return the head * 2.
            Done.

            Define nums as a series of number with (1, 2, 3).
            State cast doubled-first on (nums).

            Define words as a series of text with ("a", "b").
            State cast doubled-first on (words).
            """);

        // The POSITION is the call, not the body.
        Assert.Equal(10, e.Line);
        Assert.Contains("'doubled-first' does not work when it fills 'element' with text", e.Message);
    }

    [Fact]
    public void AFilledGenericThatRefuses_StillSaysWhy()
    {
        // ! The re-anchoring must not swallow the reason. "This doesn't work" with no account of
        // WHAT the body objected to would trade one useless message for another.
        var e = Refused("""
            Bind element to doubled-first, given (the series of element items):
                Define the head as the first of items.
                Return the head * 2.
            Done.

            Define words as a series of text with ("a", "b").
            State cast doubled-first on (words).
            """);

        // ⚠ The first two alone pass with the fix REVERTED — the un-reframed message contains both.
        // Measured by sabotage. The third is what makes this test discriminate: the reason has to
        // appear UNDER the re-framing, not instead of it.
        Assert.Contains("arithmetic requires numbers on both sides", e.Message);
        Assert.Contains("line 3", e.Message);
        Assert.Contains("Its body is what refuses them:", e.Message);
    }

    [Fact]
    public void AFillingThatWorks_IsUnaffected()
    {
        Assert.Equal("2", Run("""
            Bind element to doubled-first, given (the series of element items):
                Define the head as the first of items.
                Return the head * 2.
            Done.

            State cast doubled-first on (a series of number with (1, 2, 3)).
            """));
    }
}
