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

    private static TypeException Refused(string source) =>
        Assert.Throws<TypeException>(() => Run(source));

    private const string Nodes = """
        Define object num-node with (the number value).
        Define object add-node with (the number left, the number right).
        Define object neg-node with (the number operand).

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

    // ── What is refused ───────────────────────────────────────────────────────────────────────

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
    public void OnlyOneArgumentMayTellVersionsApart()
    {
        // ⚠ Two varying positions is the product of their cases — a nested Judge, and a wider
        // question. Refused rather than half-built.
        var e = Refused(Nodes + """
            Bind number to eval, given (the num-node node, the number k): Return 1. Done.
            Bind number to eval, given (the add-node node, the text k): Return 2. Done.
            """);
        Assert.Contains("more than one argument", e.Message);
    }
}
