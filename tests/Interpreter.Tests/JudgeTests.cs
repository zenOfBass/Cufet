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
        program = new TypeChecker().Check(program);
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

    // ── Value arms ────────────────────────────────────────────────────────
    //
    // An arm that names a VALUE rather than a type. It buys no expressiveness — an `Otherwise if`
    // chain says the same thing — so what these pin is the two properties that make it worth
    // having: `Otherwise` is MANDATORY, because no set of values can be proved to exhaust a type;
    // and an arm cannot name a value the subject could never be. A chain of `If`s can silently do
    // nothing and can silently compare a number against text; this cannot do either.

    [Fact]
    public void AValueArm_MatchesTheValue()
    {
        Assert.Equal("change directory", Run("""
            Define command as "cd".
            Judge command, where it is:
                It is "cd", state "change directory".
                Otherwise, state "something else".
            Done.
            """));
    }

    [Fact]
    public void AValueArm_GroupsWithOr()
    {
        // The `It is` is said once, the way a type arm repeats the article and not the subject.
        Assert.Equal("job control", Run("""
            Define command as "bg".
            Judge command, where it is:
                It is "fg" or "bg", state "job control".
                Otherwise, state "something else".
            Done.
            """));
    }

    [Fact]
    public void AValueArm_FallsToOtherwise()
    {
        Assert.Equal("run ls", Run("""
            Define command as "ls".
            Judge command, where it is:
                It is "cd", state "change directory".
                Otherwise, state "run {it}".
            Done.
            """));
    }

    [Fact]
    public void AValueJudgement_WithoutOtherwise_IsRefused()
    {
        // ★ The refusal that makes value arms worth having at all.
        var ex = Assert.Throws<TypeException>(() => Run("""
            Define command as "cd".
            Judge command, where it is:
                It is "cd", state "change directory".
            Done.
            """));
        Assert.Contains("no 'Otherwise'", ex.Message);
        // ⚠ NOT the type-arm wording. Telling the author that `text` is left over sends them
        // looking for a missing case that could not exist — no arm here ever covered a type.
        Assert.DoesNotContain("does not cover", ex.Message);
    }

    [Fact]
    public void AValueArm_OfTheWrongType_IsRefused()
    {
        var ex = Assert.Throws<TypeException>(() => Run("""
            Define command as "cd".
            Judge command, where it is:
                It is 3, state "three".
                Otherwise, state "something else".
            Done.
            """));
        Assert.Contains("number", ex.Message);
        Assert.Contains("text", ex.Message);
    }

    [Fact]
    public void MixingTypeArmsAndValueArms_IsRefused()
    {
        // Refused on purpose, not because it is hard: a judgement dispatching on a tag AND on a
        // value has to answer what happens when both could match, and nothing has needed to ask.
        var ex = Assert.Throws<TypeException>(() => Run("""
            Define command as "cd".
            Judge command, where it is:
                It is "cd", state "change directory".
                A text, state "some words".
                Otherwise, state "something else".
            Done.
            """));
        Assert.Contains("mixes arms", ex.Message);
    }

    [Fact]
    public void MixingTheOtherWayRound_IsAlsoRefused()
    {
        // The rule is about the judgement, not about which kind happened to come first.
        var ex = Assert.Throws<TypeException>(() => Run("""
            Define command as "cd".
            Judge command, where it is:
                A text, state "some words".
                It is "cd", state "change directory".
                Otherwise, state "something else".
            Done.
            """));
        Assert.Contains("mixes arms", ex.Message);
    }

    [Fact]
    public void AValueArm_NamesALiteral_AndNothingElse()
    {
        // An arm is a case, and a case has to be a fixed thing the reader can see beside the
        // others. An expression here would make the arms order-dependent on side effects and would
        // let two arms name the same value with nothing on the page saying so.
        var ex = Assert.Throws<ParseException>(() => Run("""
            Define command as "cd".
            Define other as "bg".
            Judge command, where it is:
                It is other, state "the same".
                Otherwise, state "something else".
            Done.
            """));
        Assert.Contains("after 'It is'", ex.Message);
    }

    [Fact]
    public void AValueArm_TakesNumbersNegativesAndFacts()
    {
        // `-1` is folded into the constant rather than carried as a unary minus, so an arm holds a
        // value and not an expression to evaluate once per judgement.
        Assert.Equal("interrupted\nyes", Run("""
            Define code as 0 - 1.
            Judge code, where it is:
                It is 0, state "fine".
                It is -1, state "interrupted".
                Otherwise, state "exit {it}".
            Done.
            Define flag as true.
            Judge flag, where it is:
                It is true, state "yes".
                Otherwise, state "no".
            Done.
            """));
    }

    [Fact]
    public void AValueArm_DoesNotNarrowIt()
    {
        // ★ `it` reads at the subject's own type throughout. Matching "cd" says nothing about the
        // type that the subject's declaration did not, which is why a value judgement works on a
        // subject that is not a union at all — and why it costs the back ends no narrowing.
        Assert.Equal("cd is 2 long", Run("""
            Define command as "cd".
            Judge command, where it is:
                It is "cd", state "{it} is {the length of it} long".
                Otherwise, state "something else".
            Done.
            """));
    }
}
