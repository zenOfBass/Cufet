using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// `a catalogue of (red or green or blue) with one of each` — the values read off the union.
/// </summary>
/// <remarks>
/// <para>
/// ★ The problem is DRIFT, not typing. Writing the three values out beside the union was never
/// hard; it was that adding a fourth case is checked wherever the union is judged and silently
/// ignored by a hand-written list of values, so the list quietly stops meaning "every case".
/// </para>
/// <para>
/// ★★ Read off the ANNOTATION — a written-out type — and never off a variable. A variable's type
/// at a point is not its declared type, because a `Judge` arm narrows it, so `one of each` on a
/// subject would answer differently inside an arm than outside one and the answer would depend on
/// where the writer happened to be standing. That is a hazard, not a preference.
/// </para>
/// <para>
/// ★ It costs no new keyword: `one` and `each` are both already reserved, and the phrase is the one
/// the language's own prose was already using for this collection.
/// </para>
/// </remarks>
public class OneOfEachTests
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

    private const string Lights = """
        Define object red.
        Define object green.
        Define object blue.

        """;

    private const string NameOf = """
        Bind text to name-of, given (the (red or green or blue) light):
            Judge light, where it is:
                A red, return "red".
                A green, return "green".
                A blue, return "blue".
            Done.
        Done.

        """;

    [Fact]
    public void OneOfEach_MakesOnePerCase_InTheUnionsOrder()
    {
        // ! The ORDER is asserted, not just the membership — the catalogue is a series, and a
        // reader who writes `the first of lights` is owed the case the union named first.
        Assert.Equal("red\ngreen\nblue", Run(Lights + NameOf + """
            Define lights as a catalogue of (red or green or blue) with one of each.
            For each light in lights, repeat:
                State cast name-of on (light).
            Done.
            """));
    }

    [Fact]
    public void TheListFollowsTheUnion_WhichIsTheWholePoint()
    {
        // ★★ The whole feature, in one assertion: the two programs differ by ONE word, in the
        // union, and the number of values follows. A hand-written `with (a new red, ...)` would
        // print 3 both times, which is the silent drift this replaces.
        var lights = (string union) => $$"""
            Define object red.
            Define object green.
            Define object blue.
            Define object yellow.
            Define lights as a catalogue of {{union}} with one of each.
            State the number of lights.
            """;

        Assert.Equal("3", Run(lights("(red or green or blue)")));
        Assert.Equal("4", Run(lights("(red or green or blue or yellow)")));
    }

    [Fact]
    public void EachValueIsItsOwnCase_NotThreeOfTheFirst()
    {
        // ⚠ The control for the test above. Counting alone would pass if every element were `a new
        // red`, so this judges each one back to a distinct name.
        Assert.Equal("redgreenblue", Run(Lights + NameOf + """
            Define lights as a catalogue of (red or green or blue) with one of each.
            Define out as "".
            For each light in lights, repeat:
                The out becomes out joined to cast name-of on (light).
            Done.
            State out.
            """));
    }

    [Fact]
    public void ARepeatedCaseIsStillOneCase()
    {
        // ⚠ Found by using the feature, not by reading it. `(red or red)` names ONE case and the
        // language already says so — a single `A red` arm satisfies `Judge` over that union,
        // because object types are nominal and compare by name. `one of each` counted the
        // mentions instead of the cases, so the two disagreed about how many cases a union has.
        Assert.Equal("1", Run(Lights + """
            Define lights as a catalogue of (red or red) with one of each.
            State the number of lights.
            """));
    }

    [Fact]
    public void TheWrittenOutFormStillWorks()
    {
        // The control: `with (...)` is untouched, and stays the way to say which values you mean.
        Assert.Equal("red\nblue", Run(Lights + NameOf + """
            Define some as a catalogue of (red or green or blue) with (a new red, a new blue).
            For each light in some, repeat:
                State cast name-of on (light).
            Done.
            """));
    }

    // ── What is refused ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACatalogueThatNamesNoCases_IsRefused()
    {
        // An open catalogue has no case list to read, so there is nothing for `one of each` to be
        // one of each OF.
        var e = Assert.Throws<ParseException>(() => Run("Define lights as a catalogue with one of each."));
        Assert.Contains("say which cases it holds", e.Message);
    }

    [Fact]
    public void AScalarCase_IsRefused()
    {
        // ⚠ The ⚠ the roadmap entry carried: `(number or text)` has no "one of each" — there is no
        // one particular number. Caught in the parser, because a keyword type is not a name that
        // could ever be made with nothing in it.
        var e = Assert.Throws<ParseException>(() =>
            Run("Define things as a catalogue of (number or text) with one of each."));
        Assert.Contains("can't make a number", e.Message);
    }

    [Fact]
    public void ACaseThatCarriesFields_IsRefused()
    {
        // ⚠⚠ The reason the AST carries an `OneOfEach` flag at all. The parser has already turned
        // the phrase into `a new light`, so without the checker's half the writer would be told a
        // literal they never wrote is missing a field they never saw — on a line whose text says
        // `one of each`.
        var e = Assert.Throws<TypeException>(() => Run("""
            Define object red.
            Define object light with (the number watts).
            Define lights as a catalogue of (red or light) with one of each.
            """));
        Assert.Contains("one of each", e.Message);
        Assert.Contains("carries watts", e.Message);
    }

    [Fact]
    public void AnUndefinedCase_IsRefused()
    {
        var e = Assert.Throws<TypeException>(() => Run("""
            Define object red.
            Define lights as a catalogue of (red or teal) with one of each.
            """));
        Assert.Contains("isn't a defined object type", e.Message);
    }
}
