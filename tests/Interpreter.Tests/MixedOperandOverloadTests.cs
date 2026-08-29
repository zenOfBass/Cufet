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
    public void APairThatAlreadyMeansSomething_CannotBeOverloaded()
    {
        // ⚠⚠ The one restriction: the overload lookup runs BEFORE the numeric path, so declaring
        // `number * number` would capture `1 * 2` and multiplication would stop meaning
        // multiplication. REFERENCE has always said built-ins cannot be shadowed.
        foreach (var pair in new[] { ("number", "number", "*", "1 * 2"), ("bits", "bits", "+", "0b1 + 0b1") })
        {
            var e = Refused($"""
                Bind overloading {pair.Item3}, given (the lhs is a {pair.Item1}, the rhs is a {pair.Item2}):
                    Return {(pair.Item1 == "bits" ? "0b0" : "0")}.
                Done.
                State {pair.Item4}.
                """);
            Assert.Contains("already means something", e.Message);
        }
    }

    [Fact]
    public void APairWithNoMeaningYet_MayHaveOne_EvenWithNoObjectInIt()
    {
        // ★ The restriction is exactly as wide as its reason and no wider. `text * number` is an
        // ERROR today — "arithmetic requires numbers on both sides" — so nothing is shadowed by
        // giving it a meaning. Only `number op number` and `bits op bits` are taken.
        Assert.Equal("ababab", Run("""
            Bind overloading *, given (the lhs is a text, the rhs is a number):
                Define out as "".
                For each n in range 1 to rhs, repeat:
                    The out becomes out joined to lhs.
                Done.
                Return out.
            Done.
            State "ab" * 3.
            """));
    }

    [Fact]
    public void OverloadingPlusOnTextIsTheWritersBusiness()
    {
        // ⚠ `joined to` is how text concatenates, and "one canonical way" is a design value — but
        // it is a value about what the LANGUAGE offers, not a rule the checker enforces on a
        // program. Nothing is shadowed here, so nothing refuses it.
        Assert.Equal("hello, world", Run("""
            Bind overloading +, given (the lhs is a text, the rhs is a text):
                Return lhs joined to rhs.
            Done.
            State "hello, " + "world".
            """));
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
