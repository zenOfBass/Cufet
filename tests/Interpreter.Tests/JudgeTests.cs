using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// `Judge` — the exhaustive case construct.
//
// The tests that matter most are the refusals. A judgement that merely reads better than an
// `Otherwise if` chain would be a second spelling of a construct that already exists; what earns
// its place is that control can never fall off the end of one. So coverage is either PROVED (a
// closed union whose cases are all handled) or DEFAULTED (`Otherwise`), and anything else is a
// static error.
public class JudgeTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var output  = new System.IO.StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private const string Union = "Define the (number or text or fact) thing as ";

    // ── Coverage ──────────────────────────────────────────────────────────

    [Fact]
    public void EveryCaseHandled_NeedsNoOtherwise()
    {
        Assert.Equal("a number", Run(Union + "42.\n" +
            "Judge thing, where it is:\n" +
            "    A number, state \"a number\".\n" +
            "    A text, state \"some text\".\n" +
            "    A fact, state \"a fact\".\n" +
            "Done."));
    }

    [Fact]
    public void AMissingCase_IsRefused()
    {
        // ★ The whole point of the construct. This is what an `Otherwise if` chain cannot say.
        var ex = Assert.Throws<TypeException>(() => Run(Union + "42.\n" +
            "Judge thing, where it is:\n" +
            "    A number, state \"a number\".\n" +
            "    A text, state \"some text\".\n" +
            "Done."));
        Assert.Contains("does not cover", ex.Message);
        Assert.Contains("fact", ex.Message);
        Assert.Contains("Otherwise", ex.Message);
    }

    [Fact]
    public void TheLastCase_CountsAsCovered()
    {
        // Regression: the remainder collapses to a bare type once one case is left, and removing
        // from a non-union used to be a silent no-op — so a fully covered judgement reported its
        // FINAL case as unhandled. Every arm order must therefore reach empty.
        Assert.Equal("a fact", Run(Union + "true.\n" +
            "Judge thing, where it is:\n" +
            "    A fact, state \"a fact\".\n" +
            "    A text, state \"some text\".\n" +
            "    A number, state \"a number\".\n" +
            "Done."));
    }

    [Fact]
    public void Otherwise_CoversTheRest()
    {
        Assert.Equal("not a number", Run(Union + "\"hi\".\n" +
            "Judge thing, where it is:\n" +
            "    A number, state \"a number\".\n" +
            "    Otherwise, state \"not a number\".\n" +
            "Done."));
    }

    [Fact]
    public void ThereIsNoNoOpStatementForADeliberateOptOut()
    {
        // ★ Cufet has no "do nothing" statement — `pass` exists only inside `or pass the failure
        // off`. So an author who means "ignore the rest" still has to write something real in the
        // Otherwise arm. Pinned because the checker's error message and the roadmap entry both
        // recommended `Otherwise, pass.` before this was checked, and it does not parse.
        Assert.Throws<ParseException>(() => Run(Union + "true.\n" +
            "Judge thing, where it is:\n" +
            "    A number, state \"a number\".\n" +
            "    Otherwise, pass.\n" +
            "Done."));
    }

    // ── Arms ──────────────────────────────────────────────────────────────

    [Fact]
    public void OrGroupsCases()
    {
        // Grouping is what C-style fall-through is overwhelmingly used for, and it needs no
        // fall-through machinery at all.
        Assert.Equal("scalar-ish", Run(Union + "\"hi\".\n" +
            "Judge thing, where it is:\n" +
            "    A number or a text, state \"scalar-ish\".\n" +
            "    A fact, state \"a fact\".\n" +
            "Done."));
    }

    [Fact]
    public void ArmsTakeBlocksAsWellAsOneLiners()
    {
        Assert.Equal("one\ntwo", Run(Union + "42.\n" +
            "Judge thing, where it is:\n" +
            "    A number:\n" +
            "        State \"one\".\n" +
            "        State \"two\".\n" +
            "    Done.\n" +
            "    Otherwise, state \"other\".\n" +
            "Done."));
    }

    // ── Narrowing ─────────────────────────────────────────────────────────

    [Fact]
    public void ItIsNarrowedInsideEachArm()
    {
        // `the length of` works on text only, so this compiles solely because the arm narrowed.
        Assert.Equal("5", Run(Union + "\"hello\".\n" +
            "Judge thing, where it is:\n" +
            "    A text, state the length of it.\n" +
            "    Otherwise, state \"other\".\n" +
            "Done."));
    }

    [Fact]
    public void TheSubjectMayBeAnExpression()
    {
        // ★ Narrowing is variable-level, so a bare `If` cannot narrow an expression — REFERENCE
        // says to name it first. Binding to `it` IS naming it, so a judgement narrows where an
        // `If` on the same expression could not.
        Assert.Equal("5", Run(
            "Define words as a series of text with (\"hello\").\n" +
            "Define the (number or text) picked as item 1 of words.\n" +
            "Judge picked, where it is:\n" +
            "    A text, state the length of it.\n" +
            "    Otherwise, state \"other\".\n" +
            "Done."));
    }

    [Fact]
    public void AGroupedArmDoesNotNarrow()
    {
        // An arm covering two cases cannot know which one arrived, so `it` stays the union and a
        // type-specific operation on it is refused.
        var ex = Assert.Throws<TypeException>(() => Run(Union + "\"hi\".\n" +
            "Judge thing, where it is:\n" +
            "    A number or a text, state the length of it.\n" +
            "    A fact, state \"a fact\".\n" +
            "Done."));
        Assert.Contains("length", ex.Message);
    }

    // ── Returning ─────────────────────────────────────────────────────────

    [Fact]
    public void AnExhaustiveJudgeSatisfiesTheReturnPathCheck()
    {
        // Every arm returns and the union is fully covered, so the function cannot fall off its
        // end — the return-path analysis has to know that or `Judge` is unusable in a function,
        // which is its main setting.
        Assert.Equal("5", Run(
            "Bind number to size-of, given (the (number or text) value):\n" +
            "    Judge value, where it is:\n" +
            "        A number, return it.\n" +
            "        A text, return the length of it.\n" +
            "    Done.\n" +
            "Done.\n" +
            "State cast size-of on (\"hello\")."));
    }
}
