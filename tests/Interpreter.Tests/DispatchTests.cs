using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// Several functions may share a name when one argument's type tells them apart.
/// </summary>
/// <remarks>
/// <para>
/// ★★ Expanded in the FRONT END, before the hoist. Each version becomes an ordinary function
/// under a name of its own, and the name the writer called becomes an ordinary function whose body
/// is an ordinary <c>Judge</c>. Neither backend learns that dispatch exists.
/// </para>
/// <para>
/// ★★ The dispatcher is a <c>Judge</c> because <c>Judge</c> already does this job — it reads the
/// tag a closed union carries, narrows the subject per arm, and PROVES the arms cover every case.
/// The coverage proof is the one that matters: the arms are generated from the versions, so they
/// cannot fall out of step with them.
/// </para>
/// <para>
/// ⚠ Before this, two functions of one name were accepted silently and the later one won — every
/// call reached it, so passing the FIRST version's type was refused with an error about the
/// declaration the writer was not calling. See <see cref="DuplicateFunctionNameTests"/> for what
/// stays refused.
/// </para>
/// </remarks>
public class DispatchTests
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

    private const string LF = "\n";

    private static TypeException Refused(string source) =>
        Assert.Throws<TypeException>(() => Run(source));

    private const string Nodes = """
        Define object num-node with (the number value).
        Define object add-node with (the number left, the number right).
        Define object neg-node with (the number operand).

        """;

    private const string Typed = """
        Define object num-lit with (the number value).
        Define object text-lit with (the text value).
        Define object int-type.
        Define object text-type.

        """;

    private const string Check = """
        Bind text to check, given (the num-lit node, the int-type want): Return "num/int". Done.
        Bind text to check, given (the num-lit node, the text-type want): Return "num/text". Done.
        Bind text to check, given (the text-lit node, the int-type want): Return "text/int". Done.
        Bind text to check, given (the text-lit node, the text-type want): Return "text/text". Done.

        """;

    private const string Eval = """
        Bind number to eval, given (the num-node node):
            Return node's value.
        Done.

        Bind number to eval, given (the add-node node):
            Return node's left + node's right.
        Done.

        Bind number to eval, given (the neg-node node):
            Return 0 - node's operand.
        Done.

        """;

    [Fact]
    public void ACallReachesTheVersionForItsArgumentType()
    {
        Assert.Equal("7\n7\n-5", Run(Nodes + Eval + """
            State cast eval on (a new num-node { the value 7 }).
            State cast eval on (a new add-node { the left 3, the right 4 }).
            State cast eval on (a new neg-node { the operand 5 }).
            """));
    }

    [Fact]
    public void AUnionTypedArgumentPicksItsVersionAtRunTime()
    {
        // ★★ The feature, as opposed to overloading. Every element here is statically the union —
        // nothing at the call site says which node it holds — so the version is chosen from the
        // tag the value carries. This is the shape a lexer, parser or type checker is made of.
        Assert.Equal("1\n5\n-4", Run(Nodes + Eval + """
            Define nodes as a catalogue of (num-node or add-node or neg-node) with (
                a new num-node { the value 1 },
                a new add-node { the left 2, the right 3 },
                a new neg-node { the operand 4 }).
            For each n in nodes, repeat:
                State cast eval on (n).
            Done.
            """));
    }

    [Fact]
    public void TheUncoveredCaseIsRefusedAtTheCall()
    {
        // ★ Coverage, and it needs no machinery of its own: the dispatcher takes the union of the
        // types its versions declare, so passing a wider one is an ordinary assignability error
        // that names exactly what is missing.
        var e = Refused(Nodes + """
            Bind number to eval, given (the num-node node): Return node's value. Done.
            Bind number to eval, given (the add-node node): Return node's left. Done.

            State cast eval on (a new neg-node { the operand 1 }).
            """);
        Assert.Contains("must be a (num-node or add-node)", e.Message);
        Assert.Contains("you passed a neg-node", e.Message);
    }

    [Fact]
    public void VoidVersionsDispatchToo()
    {
        // ⚠ A version that gives nothing back needs a `Cast` statement in its arm rather than a
        // `Return`. Left out, a void group would not build at all.
        Assert.Equal("value 7\nsum 7", Run(Nodes + """
            Bind void to show, given (the num-node node):
                State "value {node's value}".
            Done.

            Bind void to show, given (the add-node node):
                State "sum {node's left + node's right}".
            Done.

            Cast show on (a new num-node { the value 7 }).
            Cast show on (a new add-node { the left 3, the right 4 }).
            """));
    }

    [Fact]
    public void ParametersBesideTheDispatchedOneAreCarriedThrough()
    {
        // The dispatched argument is not always the only one. Everything beside it must reach the
        // version unchanged, and in position.
        Assert.Equal("14\n21", Run(Nodes + """
            Bind number to scaled, given (the num-node node, the number factor):
                Return node's value * factor.
            Done.

            Bind number to scaled, given (the add-node node, the number factor):
                Return (node's left + node's right) * factor.
            Done.

            State cast scaled on (a new num-node { the value 7 }, 2).
            State cast scaled on (a new add-node { the left 3, the right 4 }, 3).
            """));
    }

    [Fact]
    public void ScalarTypesCanTellVersionsApart()
    {
        // Nothing here is object-specific. A closed union of scalars carries a tag the same way.
        Assert.Equal("number 7\ntext hi", Run("""
            Bind text to describe, given (the number thing):
                Return "number {thing}".
            Done.

            Bind text to describe, given (the text thing):
                Return "text {thing}".
            Done.

            State cast describe on (7).
            State cast describe on ("hi").
            """));
    }

    // ── Conditions ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AConditionTellsVersionsOfOneSignatureApart()
    {
        // ★ The conditions must EXCLUDE each other — `left is 0` and `right is 0` would both hold
        // on `0 + 0` and are refused, so the narrower case says so itself. That is the design: the
        // language never decides which of two overlapping versions wins.
        Assert.Equal("9\n4\n0\n5", Run(Nodes + """
            Bind number to fold, given (the add-node node) when node's left is 0 and node's right is not 0:
                Return node's right.
            Done.

            Bind number to fold, given (the add-node node) when node's right is 0:
                Return node's left.
            Done.

            Bind number to fold, given (the add-node node):
                Return node's left + node's right.
            Done.

            State cast fold on (a new add-node { the left 0, the right 9 }).
            State cast fold on (a new add-node { the left 4, the right 0 }).
            State cast fold on (a new add-node { the left 0, the right 0 }).
            State cast fold on (a new add-node { the left 2, the right 3 }).
            """));
    }

    [Fact]
    public void ConditionsAndTypeDispatchCompose()
    {
        // ⚠⚠ The condition has to be rewritten onto the narrowed subject, not the parameter.
        // Inside the generated `Judge` arm the parameter still holds the whole union, so
        // `node's value is 0` would be asking for a field of a union and refused outright.
        Assert.Equal("zero\nnumber 7\nleft-identity\nsum", Run(Nodes + """
            Bind text to describe, given (the num-node node) when node's value is 0:
                Return "zero".
            Done.

            Bind text to describe, given (the num-node node):
                Return "number {node's value}".
            Done.

            Bind text to describe, given (the add-node node) when node's left is 0:
                Return "left-identity".
            Done.

            Bind text to describe, given (the add-node node):
                Return "sum".
            Done.

            Define nodes as a catalogue of (num-node or add-node) with (
                a new num-node { the value 0 },
                a new num-node { the value 7 },
                a new add-node { the left 0, the right 3 },
                a new add-node { the left 1, the right 3 }).

            For each n in nodes, repeat:
                State cast describe on (n).
            Done.
            """));
    }

    [Fact]
    public void XorIsPartOfTheFragment()
    {
        // ★ `xor` carries no expressive power — the atoms already negate — but leaving it out
        // would be arbitrary: `and`, `xor` and `or` are one family on one precedence line. It
        // normalises to a disjunction, which is how `left is 0 xor right is 0` is shown disjoint
        // from `left is 0 and right is 0`.
        Assert.Equal("1\n2\n3", Run(Nodes + """
            Bind number to fold, given (the add-node node) when node's left is 0 xor node's right is 0:
                Return 1.
            Done.

            Bind number to fold, given (the add-node node) when node's left is 0 and node's right is 0:
                Return 2.
            Done.

            Bind number to fold, given (the add-node node):
                Return 3.
            Done.

            State cast fold on (a new add-node { the left 0, the right 5 }).
            State cast fold on (a new add-node { the left 0, the right 0 }).
            State cast fold on (a new add-node { the left 1, the right 5 }).
            """));
    }

    // ── More than one argument ──────────────────────────────────────────

    [Fact]
    public void TwoArgumentsCanTellVersionsApart()
    {
        Assert.Equal("num/int|num/text|text/int|text/text", Run(Typed + Check + """
            Define out as "".
            The out becomes cast check on (a new num-lit { the value 1 }, a new int-type).
            The out becomes out joined to "|" joined to cast check on (a new num-lit { the value 1 }, a new text-type).
            The out becomes out joined to "|" joined to cast check on (a new text-lit { the value "x" }, a new int-type).
            The out becomes out joined to "|" joined to cast check on (a new text-lit { the value "x" }, a new text-type).
            State out.
            """));
    }

    [Fact]
    public void BothArgumentsArePickedAtRunTime()
    {
        // ★★ The feature. Neither argument's type is known at the call — both are elements of a
        // catalogue — so the version comes from two tags read in turn. That is what a type
        // checker dispatching on (node, expected) is made of.
        //
        // ⚠⚠ Each nested `Judge` rebinds `it`, so the outer argument's narrowed value has to be
        // bound to a local before descending, or it is gone by the time the leaf calls a version
        // that declared the narrow type.
        Assert.Equal("num/int" + LF + "num/text" + LF + "text/int" + LF + "text/text",
            Run(Typed + Check + """
            Define nodes as a catalogue of (num-lit or text-lit) with (
                a new num-lit { the value 1 }, a new text-lit { the value "x" }).
            Define wants as a catalogue of (int-type or text-type) with (
                a new int-type, a new text-type).
            For each n in nodes, repeat:
                For each w in wants, repeat:
                    State cast check on (n, w).
                Done.
            Done.
            """));
    }

    [Fact]
    public void ConditionsComposeWithTwoArgumentDispatch()
    {
        // The condition is rewritten onto whatever holds the narrowed value at its position —
        // `it` at the innermost level, the bound local at an outer one.
        Assert.Equal("zero is fine" + LF + "num/int 5" + LF + "text/int", Run(Typed + """
            Bind text to check, given (the num-lit node, the int-type want) when node's value is 0:
                Return "zero is fine".
            Done.

            Bind text to check, given (the num-lit node, the int-type want):
                Return "num/int {node's value}".
            Done.

            Bind text to check, given (the num-lit node, the text-type want): Return "num/text". Done.
            Bind text to check, given (the text-lit node, the int-type want): Return "text/int". Done.
            Bind text to check, given (the text-lit node, the text-type want): Return "text/text". Done.

            Define nodes as a catalogue of (num-lit or text-lit) with (
                a new num-lit { the value 0 },
                a new num-lit { the value 5 },
                a new text-lit { the value "x" }).
            For each n in nodes, repeat:
                State cast check on (n, a new int-type).
            Done.
            """));
    }

    // ── What is refused ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConditionsThatCanBothHoldAreRefused()
    {
        // ★★ The whole design in one test. `left is 0` and `right is 0` both hold on `0 + 0`, and
        // the language does not pick — CLOS resolves this with prefer-method, Julia with a
        // specificity lattice, and Cufet refuses. There is no priority rule to learn because the
        // question is never asked.
        var e = Refused(Nodes + """
            Bind number to fold, given (the add-node node) when node's left is 0: Return 1. Done.
            Bind number to fold, given (the add-node node) when node's right is 0: Return 2. Done.
            Bind number to fold, given (the add-node node): Return 3. Done.
            """);
        Assert.Contains("can both apply", e.Message);
    }

    [Fact]
    public void ConditionsWithNoFallbackAreRefused()
    {
        // ⚠ Even when the conditions look complementary. Proving a SET of them covers every case
        // is tautology checking, which the fragment does not promise — so the coverage is written
        // rather than inferred. Widening this later would be additive.
        var e = Refused(Nodes + """
            Bind number to fold, given (the add-node node) when node's left is 0: Return 1. Done.
            Bind number to fold, given (the add-node node) when node's left is not 0: Return 2. Done.
            """);
        Assert.Contains("carries a condition", e.Message);
    }

    [Fact]
    public void ALoneVersionWithAConditionIsRefused()
    {
        // The same rule reaching the case that has no second version at all — without it, the
        // condition would be quietly ignored on an ordinary function.
        var e = Refused(Nodes + """
            Bind number to fold, given (the add-node node) when node's left is 0: Return 1. Done.
            """);
        Assert.Contains("carries a condition", e.Message);
    }

    [Fact]
    public void AConditionOutsideTheFragmentIsRefused()
    {
        // ⚠ Ordering needs interval reasoning rather than an atom comparison, so it is out — and
        // refused by name rather than accepted and left unchecked for overlap.
        var e = Refused(Nodes + """
            Bind number to fold, given (the add-node node) when node's left is greater than 3: Return 1. Done.
            Bind number to fold, given (the add-node node): Return 2. Done.
            """);
        Assert.Contains("outside what can be checked", e.Message);
    }


    [Fact]
    public void TwoVersionsClaimingOneTypeAreRefused()
    {
        var e = Refused(Nodes + """
            Bind number to eval, given (the num-node node): Return 1. Done.
            Bind number to eval, given (the num-node node): Return 2. Done.
            """);
        Assert.Contains("same argument types", e.Message);
    }

    [Fact]
    public void VersionsMustAgreeOnHowManyArgumentsTheyTake()
    {
        var e = Refused(Nodes + """
            Bind number to eval, given (the num-node node): Return 1. Done.
            Bind number to eval, given (the add-node node, the number extra): Return 2. Done.
            """);
        Assert.Contains("different numbers of arguments", e.Message);
    }

    [Fact]
    public void VersionsMustAgreeOnWhatTheyGiveBack()
    {
        // ★ Not an implementation convenience. A caller has to know what it gets back without
        // knowing which version ran — the alternative is handing every caller a union to judge.
        var e = Refused(Nodes + """
            Bind number to eval, given (the num-node node): Return 1. Done.
            Bind text to eval, given (the add-node node): Return "x". Done.
            """);
        Assert.Contains("give back different types", e.Message);
    }

    [Fact]
    public void EveryCombinationMustHaveAVersion()
    {
        // ★★ Coverage stops being free once TWO arguments dispatch. With one, the versions ARE
        // the cases and the dispatcher's parameter is their union, so nothing callable is
        // unclaimed. With two, the parameters admit every pair and only the pairs someone wrote
        // have a version — so the missing one is named at the declaration rather than left to
        // surface as a `Judge` that fails to cover its union.
        var e = Refused(Typed + """
            Bind text to check, given (the num-lit node, the int-type want): Return "num/int". Done.
            Bind text to check, given (the text-lit node, the text-type want): Return "text/text". Done.
            """);
        Assert.Contains("has no version for", e.Message);
        Assert.Contains("argument 1 a num-lit", e.Message);
        Assert.Contains("argument 2 a text-type", e.Message);
    }
}
