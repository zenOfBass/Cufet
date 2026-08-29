using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// An operator overload names an ORDERED PAIR of operand types.
/// </summary>
/// <remarks>
/// <para>
/// ★ Both operands used to have to be the same object type, so `u * 2` — scaling the very `vec2`
/// REFERENCE uses in its own overload example — was refused. That is the next thing anyone tries
/// after the documented `u * w` dot product.
/// </para>
/// <para>
/// ★★ ORDERED, deliberately. `vec2 * number` declares that and only that; `number * vec2` is a
/// separate declaration. Making one cover both would have to special-case `+` and `*`, because
/// `2 - u` is not `u - 2` and `2 / u` is not `u / 2` — a second rule to remember in exchange for
/// saving a line. It also keeps the property REFERENCE already claims: never more than one
/// candidate, and never any ambiguity about which applies.
/// </para>
/// </remarks>
public class MixedOperandOverloadTests
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

    private const string Vec = """
        Define object vec2 with (the number x, the number y).

        """;

    private const string Scale = """
        Bind overloading *, given (the lhs is a vec2, the rhs is a number):
            Return a new vec2 { the x lhs's x * rhs, the y lhs's y * rhs }.
        Done.

        """;

    [Fact]
    public void AnObjectCanBeScaledByANumber()
    {
        Assert.Equal("vec2(x: 3, y: 6)", Run(Vec + Scale + """
            Define u as a new vec2 { the x 1, the y 2 }.
            State u * 3.
            """));
    }

    [Fact]
    public void TheOtherOrderIsASeparateDeclaration()
    {
        // ★ Declaring one does NOT declare the other — that is what "ordered" means, and it is the
        // half a reader is most likely to be surprised by, so it gets its own test.
        var e = Refused(Vec + Scale + """
            Define u as a new vec2 { the x 1, the y 2 }.
            State 3 * u.
            """);
        Assert.Contains("arithmetic requires numbers", e.Message);
    }

    [Fact]
    public void BothOrdersCanBeDeclared_AndEachRunsItsOwnBody()
    {
        // ⚠ The bodies differ on purpose: if one declaration were quietly answering for both, this
        // would print the same thing twice.
        Assert.Equal("vec2(x: 3, y: 6)\nleft was 10", Run(Vec + Scale + """
            Bind overloading *, given (the lhs is a number, the rhs is a vec2):
                Return "left was {lhs}".
            Done.

            Define u as a new vec2 { the x 1, the y 2 }.
            State u * 3.
            State 10 * u.
            """));
    }

    [Fact]
    public void TwoObjectTypesCanBeTheOperands()
    {
        Assert.Equal("7", Run(Vec + """
            Define object other with (the number v).

            Bind overloading +, given (the lhs is a vec2, the rhs is a other):
                Return lhs's x + rhs's v.
            Done.

            Define u as a new vec2 { the x 3, the y 0 }.
            Define o as a new other { the v 4 }.
            State u + o.
            """));
    }

    [Fact]
    public void SameTypeOverloads_StillWork()
    {
        // The control: the shape every existing program uses must be untouched by this.
        Assert.Equal("vec2(x: 4, y: 6)", Run(Vec + """
            Bind overloading +, given (the lhs is a vec2, the rhs is a vec2):
                Return a new vec2 { the x lhs's x + rhs's x, the y lhs's y + rhs's y }.
            Done.

            Define u as a new vec2 { the x 1, the y 2 }.
            Define w as a new vec2 { the x 3, the y 4 }.
            State u + w.
            """));
    }

    // ── What is still refused ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoBuiltInsCannotBeOverloaded()
    {
        // ⚠⚠ The rule that keeps mixed-type dispatch from opening a door REFERENCE closed:
        // built-ins cannot be shadowed. One side has to be an object the program defined, or
        // `number * number` could be redefined and arithmetic would stop meaning one thing.
        var e = Refused("""
            Bind overloading *, given (the lhs is a number, the rhs is a number):
                Return 0.
            Done.
            State 1 * 2.
            """);
        Assert.Contains("no object type in it", e.Message);
    }

    [Fact]
    public void ThePairIsWhatMustBeUnique_NotTheType()
    {
        // ★ "One overload per type and operator" became "one per ORDERED PAIR and operator", so a
        // type may take part in several — and a genuine duplicate is still refused.
        var e = Refused(Vec + Scale + """
            Bind overloading *, given (the lhs is a vec2, the rhs is a number):
                Return a new vec2 { the x 0, the y 0 }.
            Done.

            Define u as a new vec2 { the x 1, the y 2 }.
            State u * 3.
            """);
        Assert.Contains("already has an overload", e.Message);
    }

    [Fact]
    public void OneTypeCanTakePartInSeveralPairs()
    {
        // The complement of the test above: `vec2` appears in three overloads of `*`, and none of
        // them collide because each names a different pair.
        Assert.Equal("vec2(x: 2, y: 4)\nleft was 5\n11", Run(Vec + Scale + """
            Bind overloading *, given (the lhs is a number, the rhs is a vec2):
                Return "left was {lhs}".
            Done.

            Bind overloading *, given (the lhs is a vec2, the rhs is a vec2):
                Return lhs's x * rhs's x + lhs's y * rhs's y.
            Done.

            Define u as a new vec2 { the x 1, the y 2 }.
            Define w as a new vec2 { the x 3, the y 4 }.
            State u * 2.
            State 5 * u.
            State u * w.
            """));
    }
}
