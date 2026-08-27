using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// A definition that leaves a blank — `Define object stack of element` — and the fillings of it.
/// </summary>
/// <remarks>
/// ★ The blank is named by the writer and marked by `of`, the slot after the type's own name. That
/// is a declaration BY POSITION, so nothing needs inferring and a mistyped type name elsewhere stays
/// an ordinary error rather than quietly becoming a blank.
///
/// ★★ Filling happens in the FRONT END: `a stack of number` becomes an ordinary definition named
/// `stack of number`, spliced into the program, and neither backend learns what a template is. Same
/// move the language already makes for stashes (lowered to closures before either backend runs) and
/// for interface parameters (specialised per conformer).
/// </remarks>
public class GenericObjectTests
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

    private const string Stack = """
        Define object stack of element with (the series of element items):
            Bind void to push, given (the element value):
                Insert value into one's items.
            Done.
            Bind number to how-many:
                Return the number of one's items.
            Done.
        Done.

        """;

    [Fact]
    public void AFilledTemplate_HoldsAndReportsItsElements()
    {
        Assert.Equal("2", Run(Stack + """
            Define counts as a new stack of number { the items a series of number }.
            Cast push on (counts, 5).
            Cast push on (counts, 7).
            State cast how-many on (counts).
            """));
    }

    /// <summary>
    /// Two fillings of one template are two separate types, each with its own methods.
    /// </summary>
    /// <remarks>
    /// ★ The point of monomorphizing rather than boxing: `the first of names's items` is a text,
    /// and only because `stack of text` has its own `items` field typed `series of text`. A single
    /// shared definition would have had to hold something both a number and a text can be.
    /// </remarks>
    [Fact]
    public void TwoFillings_AreSeparateTypes()
    {
        Assert.Equal("2\n1\nalice", Run(Stack + """
            Define counts as a new stack of number { the items a series of number }.
            Cast push on (counts, 5).
            Cast push on (counts, 7).

            Define names as a new stack of text { the items a series of text }.
            Cast push on (names, "alice").

            State cast how-many on (counts).
            State cast how-many on (names).
            State the first of names's items.
            """));
    }

    [Fact]
    public void ATemplateWithNoFilling_IsRefusedAndSaysWhatItNeeds()
    {
        // ⚠ NOT "not a defined object type" — it IS defined, it just names nothing on its own, and
        // that message would send the reader off to define what they already have.
        var message = Refused(Stack + """
            Define bad as a new stack { the items a series of number }.
            """).Message;
        Assert.Contains("needs its blank filled in", message);
        Assert.Contains("of element", message);
    }

    [Fact]
    public void TooManyFillings_AreRefused()
    {
        Assert.Contains("1 blank", Refused(Stack + """
            Define bad as a new stack of number of text { the items a series of number }.
            """).Message);
    }

    [Fact]
    public void FillingAnOrdinaryType_IsRefused()
    {
        Assert.Contains("does not take a filling", Refused("""
            Define object plain with (the number n).
            Define bad as a new plain of number { the n 1 }.
            """).Message);
    }

    /// <summary>Two blanks, because the writer names each one.</summary>
    /// <remarks>
    /// ★ This is what naming them buys. A single fixed placeholder word — one spelling meaning "the
    /// blank" — could only ever have marked ONE, so a two-blank type would have had nothing to say
    /// which filling went where.
    /// </remarks>
    [Fact]
    public void TwoBlanks_AreFilledInOrder()
    {
        Assert.Equal("7\nseven", Run("""
            Define object pair of left-thing of right-thing with (
                the left-thing one-side, the right-thing other-side).
            Define p as a new pair of number of text { the one-side 7, the other-side "seven" }.
            State p's one-side.
            State p's other-side.
            """));
    }

    /// <summary>The blank reaches into compound types, which is where nearly every use of it is.</summary>
    /// <remarks>
    /// ⚠ This is the one that caught a real bug: a substitution matching only the TOP level replaces
    /// a bare `element` and walks straight past `series of element`, while still reporting that the
    /// tree changed. The template below never mentions the blank bare.
    /// </remarks>
    [Fact]
    public void ABlankInsideACompoundType_IsFilledIn()
    {
        Assert.Equal("3", Run("""
            Define object box of thing with (the series of thing items).
            Define b as a new box of number { the items a series of number with (1, 2, 3) }.
            State the number of b's items.
            """));
    }
    // -- A filled body's refusal is reported where the FILLING happened ---------
    //
    // ** Three ways a blank gets filled, and all three used to report inside the template:
    // a free function's call, a generic METHOD's call, and a generic OBJECT's literal. The first
    // two carry a call site; the third does not, because an object's blanks are filled during type
    // RESOLUTION -- so the literal leaves its position for the filler to read.

    [Fact]
    public void AFilledGenericMethod_ReportsAtTheCall()
    {
        var e = Assert.Throws<TypeException>(() => Run("""
            Define object box with (the number tag):
                Bind element to twice-of, given (the element value):
                    Return value * 2.
                Done.
            Done.

            Define the crate as a new box { the tag 1 }.
            State cast the crate's twice-of on ("no").
            """));

        Assert.Equal(8, e.Line);
        Assert.Contains("'twice-of' does not work when it fills 'element' with text", e.Message);
        Assert.Contains("Its body is what refuses them:", e.Message);
    }

    [Fact]
    public void AFilledGenericObject_ReportsAtTheLiteral()
    {
        // ! The one with no call site. Reported at the `a new holder of text` on line 8, not at the
        // method body on line 4 -- which is correct for `holder of number` and always will be.
        var e = Assert.Throws<TypeException>(() => Run("""
            Define object holder of element with (the series of element items):
                Bind element to doubled-first:
                    Define the head as the first of one's items.
                    Return the head * 2.
                Done.
            Done.

            Define words as a new holder of text { the items a series of text with ("a") }.
            State cast words's doubled-first.
            """));

        Assert.Equal(8, e.Line);
        Assert.Contains("'holder' does not work when it fills 'element' with text", e.Message);
        Assert.Contains("you're trying to create 'holder'", e.Message);
        Assert.Contains("Its body is what refuses them:", e.Message);
    }

    [Fact]
    public void AFilledGenericObjectThatWorks_IsUnaffected()
    {
        Assert.Equal("2", Run("""
            Define object holder of element with (the series of element items):
                Bind element to doubled-first:
                    Define the head as the first of one's items.
                    Return the head * 2.
                Done.
            Done.

            Define nums as a new holder of number { the items a series of number with (1) }.
            State cast nums's doubled-first.
            """));
    }
    // -- A generic method is reachable by BOTH spellings -----------------------

    [Fact]
    public void AGenericMethod_WorksThroughTheFreeCastForm()
    {
        // !! `cast <method> on (<receiver>, …)` is the spelling README teaches for calling a
        // method, and a generic one was unreachable through it: only the POSSESSIVE form filled a
        // blank, so this failed with "'box' has no method named 'twice-of'" -- for a method the
        // type plainly has, with a filling that plainly works.
        Assert.Equal("42", Run("""
            Define object box with (the number tag):
                Bind element to twice-of, given (the element value):
                    Return value * 2.
                Done.
            Done.

            Define the crate as a new box { the tag 1 }.
            State cast twice-of on (the crate, 21).
            """));
    }

    [Fact]
    public void AGenericMethod_WorksThroughThePossessiveForm()
    {
        // The spelling that always worked, kept beside it -- the point is that the two agree.
        Assert.Equal("42", Run("""
            Define object box with (the number tag):
                Bind element to twice-of, given (the element value):
                    Return value * 2.
                Done.
            Done.

            Define the crate as a new box { the tag 1 }.
            State cast the crate's twice-of on (21).
            """));
    }

    [Fact]
    public void AGenericMethod_FreeCastForm_ReportsAtTheCall()
    {
        // The free-cast form has to reach the same refusal, anchored the same way -- otherwise
        // fixing the dispatch would have left one spelling with the old message.
        var e = Assert.Throws<TypeException>(() => Run("""
            Define object box with (the number tag):
                Bind element to twice-of, given (the element value):
                    Return value * 2.
                Done.
            Done.

            Define the crate as a new box { the tag 1 }.
            State cast twice-of on (the crate, "no").
            """));

        Assert.Equal(8, e.Line);
        Assert.Contains("'twice-of' does not work when it fills 'element' with text", e.Message);
    }

    [Fact]
    public void AnOrdinaryMethod_FreeCastForm_IsUnaffected()
    {
        // ! The counter-test. The new branch runs for ANY cast whose first argument is an object,
        // and it must do nothing at all when the member left no blank.
        Assert.Equal("7", Run("""
            Define object tally with (the number total):
                Bind number to shown:
                    Return one's total.
                Done.
            Done.

            Define the count as a new tally { the total 7 }.
            State cast shown on (the count).
            """));
    }
}
