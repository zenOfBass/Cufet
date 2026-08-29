using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// `cast area on (the width 3, the height 4)` — arguments matched by name instead of position.
/// </summary>
/// <remarks>
/// <para>
/// ★ No new surface. REFERENCE already states the rule generally — "wherever a field could be
/// positional instead, `the` is what says a name follows" — and object and record literals already
/// implement it. An argument list was the third place a value could be positional or named, and
/// the one place the marker did nothing.
/// </para>
/// <para>
/// ★★ The names come from the DECLARATION, not from the type. `FunctionType` carries parameter
/// names as a non-equality field, so `given (the number width)` and `given (the number w)` stay
/// the same type — and a call reaching its function through a value, which has a type but no
/// declaration, is refused rather than guessed at.
/// </para>
/// <para>
/// ⚠ The reorder happens in the front end and the named list is emptied, so both backends meet an
/// ordinary call in parameter order and neither learns that named arguments exist.
/// </para>
/// </remarks>
public class NamedArgumentTests
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

    private const string Area = """
        Bind number to area, given (the number width, the number height):
            Return width * height.
        Done.

        """;

    private const string Box = """
        Define object box with (the number w):
            Bind number to scaled, given (the number factor, the number bias):
                Return one's w * factor + bias.
            Done.
        Done.

        Define crate as a new box { the w 2 }.

        """;

    [Fact]
    public void ArgumentsCanBeGivenByName()
    {
        Assert.Equal("12", Run(Area + "State cast area on (the width 3, the height 4)."));
    }

    [Fact]
    public void NamedArgumentsMayComeInAnyOrder()
    {
        // ⚠ `area` multiplies, so swapping its two arguments prints 12 either way and would pass
        // with no reorder at all. Division is what actually proves the values reached the
        // parameters they named.
        Assert.Equal("8\n8", Run("""
            Bind number to take-half, given (the number whole, the number divisor):
                Return whole / divisor.
            Done.
            State cast take-half on (the whole 16, the divisor 2).
            State cast take-half on (the divisor 2, the whole 16).
            """));
    }

    [Fact]
    public void PositionalArgumentsMayComeFirst()
    {
        Assert.Equal("12", Run(Area + "State cast area on (3, the height 4)."));
    }

    [Fact]
    public void AMethodTakesThemInBothCallForms()
    {
        // Possessive form and free-cast form. In the free form the receiver is argument one and is
        // not a declared parameter, so the names match against what follows it.
        Assert.Equal("21", Run(Box + "State cast crate's scaled on (the bias 1, the factor 10)."));
        Assert.Equal("21", Run(Box + "State cast scaled on (crate, the bias 1, the factor 10)."));
    }

    [Fact]
    public void ACastStatementTakesThemToo()
    {
        // ⚠ The statement form is a separate node with its own check. Left out, `Cast` as a
        // statement would carry the named arguments to a backend that has never heard of them.
        Assert.Equal("8", Run("""
            Bind void to show-half, given (the number whole, the number divisor):
                State whole / divisor.
            Done.
            Cast show-half on (the divisor 2, the whole 16).
            """));
    }

    // ── `the` still introduces an expression ──────────────────────────────────────────────────

    [Fact]
    public void TheWordTheStillReadsAsNoiseOrAnAccess()
    {
        // ⚠⚠ The regression this feature can cause, and the reason an argument list needs a rule
        // an object literal does not: `(the width)` in a literal has no other reading, but
        // `cast twice on (the width)` is an ordinary call passing a variable, and always was. A
        // named argument must have a VALUE after the name — a `,` or `)` there means the whole
        // thing was an expression all along.
        Assert.Equal("6\n14\n16\n12", Run("""
            Define object box with (the number width, the number height).
            Define crate as a new box { the width 3, the height 4 }.
            Bind number to twice, given (the number n):
                Return n * 2.
            Done.
            Define width as 7.
            State cast twice on (the width of crate).
            State cast twice on (the width).
            State cast twice on (the width + 1).
            State cast twice on (the width - 1).
            """));
    }

    [Fact]
    public void TheMinusSpacingRuleDecidesIt()
    {
        // ★ Inherited whole from IsNamedFieldStart, which record literals already use: binary `-`
        // is written with spaces, because hyphens are identifier characters. So `-1` is a value
        // and `- 1` is a subtraction, and neither is a guess about what was meant.
        Assert.Equal("-4\n24", Run("""
            Bind number to area, given (the number width, the number height):
                Return width * height.
            Done.
            Define width as 7.
            State cast area on (the width -1, the height 4).
            State cast area on (the width - 1, 4).
            """));
    }

    // ── What is refused ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ANameThatIsNotAParameterIsRefused()
    {
        var e = Refused(Area + "State cast area on (the wdith 3, the height 4).");
        Assert.Contains("no parameter called 'wdith'", e.Message);
        Assert.Contains("'width', 'height'", e.Message);   // says what it could have been
    }

    [Fact]
    public void GivingOneArgumentTwiceIsRefused()
    {
        var byBoth = Refused(Area + "State cast area on (3, the width 5, the height 4).");
        Assert.Contains("already given as argument 1", byBoth.Message);

        var byName = Refused(Area + "State cast area on (the width 3, the width 5).");
        Assert.Contains("given twice", byName.Message);
    }

    [Fact]
    public void LeavingOneOutIsRefused()
    {
        var e = Refused(Area + "State cast area on (the width 3).");
        Assert.Contains("no value for 'height'", e.Message);
    }

    [Fact]
    public void APositionalArgumentAfterANamedOneIsRefused()
    {
        // Once a name has been given, position no longer says which parameter is meant.
        var e = Assert.Throws<ParseException>(() => Run(Area + "State cast area on (the width 3, 4)."));
        Assert.Contains("must be too", e.Message);
    }

    [Fact]
    public void ACallThroughAHeldFunctionIsRefused()
    {
        // ★★ The boundary the design draws. A function reached through a value carries its
        // parameter TYPES but not their names — the names live on the declaration, and two
        // functions of the same type may spell them differently. Refused with that said plainly
        // rather than matched against whichever declaration happened to be nearby.
        var e = Refused("""
            Bind number function given (the number) to make-adder, given (the number n):
                Bind number to adder, given (the number x):
                    Return x + n.
                Done.
                Return adder.
            Done.
            Define add-five as cast make-adder on (5).
            State cast add-five on (the x 10).
            """);
        Assert.Contains("can't be called with named arguments", e.Message);
    }
}
