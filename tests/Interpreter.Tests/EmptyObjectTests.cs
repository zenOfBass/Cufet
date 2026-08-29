using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// A type that carries nothing does not have to say so twice.
/// </summary>
/// <remarks>
/// <para>
/// ★ `Define object red.` and `a new red` used to be parse errors — the empty shape had to be
/// written out as `with ()`, and the empty literal as `{ }`. Neither said anything: the ceremony
/// was there because the parser required it, not because a reader learned from it.
/// </para>
/// <para>
/// ★★ This is what makes a closed union usable as an ENUMERATION, which is the whole reason it
/// was worth doing. `(red or green or blue)` is a closed set, and `Judge` over one proves every
/// case is handled — `Otherwise` becomes optional and a missing case is a static error. That is
/// strictly stronger than an `enum` construct, and DESIGN records enums as declined for exactly
/// that reason. The friction that would have made someone ask for enums anyway was `with ()` on
/// every empty case, and it was a parser tweak rather than a feature.
/// </para>
/// </remarks>
public class EmptyObjectTests
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

    [Fact]
    public void AnObjectThatCarriesNothing_NeedsNoShape()
    {
        // ! Was: "expected With, got Dot".
        Assert.Equal("true", Run("""
            Define object red.
            Define light as a new red { }.
            State light is a red.
            """));
    }

    [Fact]
    public void AnEmptyLiteral_NeedsNoBraces()
    {
        // ! Was: "expected LBrace, got Dot".
        Assert.Equal("true", Run("""
            Define object red with ().
            Define light as a new red.
            State light is a red.
            """));
    }

    [Fact]
    public void TheLongFormsStillMeanWhatTheyMeant()
    {
        // ⚠ Both spellings stay legal — every existing program writes the long one, and this is a
        // shorthand rather than a replacement.
        Assert.Equal("true", Run("""
            Define object red with ().
            Define light as a new red { }.
            State light is a red.
            """));
    }

    [Fact]
    public void AClosedUnionOfEmptyObjects_IsAnEnumeration()
    {
        // ★ The shape the whole change exists for.
        Assert.Equal("green\nblue", Run(Lights + """
            Bind text to name-of, given (the (red or green or blue) light):
                Judge light, where it is:
                    A red, return "red".
                    A green, return "green".
                    A blue, return "blue".
                Done.
            Done.

            State cast name-of on (a new green).
            State cast name-of on (a new blue).
            """));
    }

    [Fact]
    public void AMissingCase_IsStillAStaticError()
    {
        // ★★ The property that makes the union stronger than an enum, and the one this change had
        // to leave intact: coverage is PROVED, so `Otherwise` is optional and a gap is refused by
        // name rather than discovered at runtime.
        var e = Assert.Throws<TypeException>(() => Run(Lights + """
            Bind text to name-of, given (the (red or green or blue) light):
                Judge light, where it is:
                    A red, return "red".
                    A green, return "green".
                Done.
            Done.

            State cast name-of on (a new red).
            """));
        Assert.Contains("does not cover blue", e.Message);
    }

    // ── What still has to follow an omitted shape ─────────────────────────────────────────────

    [Fact]
    public void AnOmittedShape_StillTakesMethods()
    {
        Assert.Equal("red", Run("""
            Define object red:
                Bind text to shown:
                    Return "red".
                Done.
            Done.
            State cast shown on (a new red).
            """));
    }

    [Fact]
    public void AnOmittedShape_StillTakesAConformanceClause()
    {
        // ⚠ The `and <interface>` and `and as a <type>` clauses are parsed AFTER the shape, so
        // making the shape optional had to leave them reachable.
        Assert.Equal("red\nblue", Run("""
            Define namer as an interface for the text function shown.

            Define object red and namer:
                Bind text to shown:
                    Return "red".
                Done.
            Done.

            Define object blue and namer:
                Bind text to shown:
                    Return "blue".
                Done.
            Done.

            State cast shown on (a new red).
            State cast shown on (a new blue).
            """));
    }

    [Fact]
    public void AnOmittedShape_StillTakesABlank()
    {
        // A template that carries nothing is still a template: the `of <name>` slots are parsed
        // before the shape, so they had to survive it going missing.
        Assert.Equal("true", Run("""
            Define object holder of thing.
            Define box as a new holder of number.
            State box is a holder of number.
            """));
    }
}
