using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// The style linter. Every rule here is advice, so the tests that matter most are the ones proving
// it stays quiet — a warning nobody asked for on code that is already fine is worse than no linter.
public class LinterTests
{
    private static IReadOnlyList<Diagnostic> Lint(string source)
    {
        var tokens = new CufetLexer(source).Tokenize();
        var parser = new Parser(tokens);
        var program = parser.Parse();
        return Linter.Lint(tokens, parser.StatementStarts, program);
    }

    private static IReadOnlyList<string> Words(string source) =>
        Lint(source).Select(d => d.Message.Split('\'')[1]).ToList();

    // ── What it should flag ───────────────────────────────────────────────

    [Fact]
    public void LowercaseKeywordOpeningALine_IsFlagged()
    {
        var w = Words("Define xs as a series of number with (1, 2).\nfor each n in xs, repeat:\n    State n.\nDone.");
        Assert.Equal(["for"], w);
    }

    [Fact]
    public void TheSuggestionIsTheCapitalisedWord()
    {
        var only = Assert.Single(Lint("Define x as 1.\nif x is 1, State \"one\"."));
        Assert.Contains("opens with 'if'", only.Message);
        Assert.Contains("write 'If'", only.Message);
        Assert.Equal(DiagnosticSeverity.Warning, only.Severity);
    }

    [Fact]
    public void ContextualStatementWords_AreFlagged()
    {
        // `output` and `seed` open statements while lexing as identifiers. Both were made
        // capitalisable so that this rule could reach them at all.
        Assert.Equal(["output"], Words("Bind void to emit:\n    output 1.\nDone."));
        Assert.Equal(["seed"], Words("Pull a book on chance.\n    seed the chance with 42.\nDone."));
    }

    // ── What it must NOT flag ─────────────────────────────────────────────

    [Fact]
    public void ALineOpeningWithAVariablesOwnName_IsLeftAlone()
    {
        // Capitalising this would rename the variable. Only an article could supply the capital,
        // and whether that reads naturally is the judgement half of the rule.
        Assert.Empty(Lint("Define total as 0.\ntotal becomes 5.\nState total."));
    }

    [Fact]
    public void AVariableSpelledLikeAStatementWord_IsLeftAlone()
    {
        // ★ `output 7.` is a statement; `output becomes 10.` is a variable that shares the
        // spelling. Suggesting a capital on the second would not improve it — it would break it,
        // because `Output` is not that variable's name.
        Assert.Empty(Lint("Define output as 9.\noutput becomes 10.\nState output."));
    }

    [Fact]
    public void AContinuationLine_IsNotALineStart()
    {
        // `the` and `with` open statements AND appear midway through them, which is why the rule
        // cannot be decided from the token stream alone.
        Assert.Empty(Lint(
            "Define object expr with (the text kind, the number value).\n" +
            "Define e as a new expr { the kind \"mul\",\n" +
            "                         the value 0 }.\n" +
            "Define xs as a series of number\n" +
            "    with (1, 2)."));
    }

    [Fact]
    public void AStatementInlineAfterAnother_IsNotALineStart()
    {
        // The line already opened with a capital; the inline statement is mid-sentence.
        Assert.Empty(Lint("Define x as 1.\nIf x is 1, state \"one\"."));
    }

    [Fact]
    public void ProseInsideABlockComment_IsNotCode()
    {
        Assert.Empty(Lint(
            "/* a comment\n" +
            "   the second line opens lowercase\n" +
            "   with words that look like keywords */\n" +
            "State \"done\"."));
    }

    [Fact]
    public void AlreadyCapitalised_SaysNothing()
    {
        Assert.Empty(Lint("Define xs as a series of number with (1, 2).\nFor each n in xs, repeat:\n    State n.\nDone."));
    }

    // ── Nested bare-`it` loops ────────────────────────────────────────────
    //
    // Every source here opens its lines with a capital, so anything these tests see came from the
    // nesting rule rather than the one above it.

    private const string Xs = "Define xs as a series of number with (1, 2).\n";

    [Fact]
    public void ABareItLoopInsideABareItLoop_IsFlagged()
    {
        var only = Assert.Single(Lint(Xs +
            "For each in xs, repeat:\n" +
            "    For each in xs, repeat:\n" +
            "        State it.\n" +
            "    Done.\n" +
            "Done."));
        Assert.Contains("both bind 'it'", only.Message);
        Assert.Contains("line 2", only.Message);          // names the OUTER loop
        Assert.Equal(3, only.Line);                       // reported at the INNER one
        Assert.Equal(DiagnosticSeverity.Warning, only.Severity);
    }

    [Fact]
    public void ABareItLoopNestedThroughANamedOne_IsStillFlagged()
    {
        // The named loop in the middle does not break the chain: the innermost `it` still shadows
        // the outermost one, and the reader still has to work out which is which.
        var only = Assert.Single(Lint(Xs +
            "For each in xs, repeat:\n" +
            "    For each n in xs, repeat:\n" +
            "        For each in xs, repeat:\n" +
            "            State it.\n" +
            "        Done.\n" +
            "    Done.\n" +
            "Done."));
        Assert.Equal(4, only.Line);
        Assert.Contains("line 2", only.Message);
    }

    [Fact]
    public void ANamedInnerLoop_SaysNothing()
    {
        Assert.Empty(Lint(Xs +
            "For each in xs, repeat:\n" +
            "    For each n in xs, repeat:\n" +
            "        State n.\n" +
            "    Done.\n" +
            "Done."));
    }

    [Fact]
    public void TwoBareItLoopsSideBySide_SayNothing()
    {
        // Not nested, so no shadowing — each `it` is unambiguous where it stands.
        Assert.Empty(Lint(Xs +
            "For each in xs, repeat:\n    State it.\nDone.\n" +
            "For each in xs, repeat:\n    State it.\nDone."));
    }

    [Fact]
    public void AFunctionCannotBeDeclaredInsideALoop()
    {
        // ★ Pins the reason ChildBlocks' new-scope flag is defensive rather than load-bearing: a
        // function body cannot sit lexically inside a loop, so the outer `it` can never reach one.
        // If local functions ever land, this test fails and the flag starts earning its keep.
        var e = Assert.Throws<ParseException>(() => Lint(Xs +
            "For each in xs, repeat:\n" +
            "    Bind void to emit:\n" +
            "        State 1.\n" +
            "    Done.\n" +
            "Done."));
        Assert.Contains("not inside a block", e.Message);
    }

    [Fact]
    public void NestingInsideAMethod_IsReached()
    {
        // Methods hang off the type rather than standing as statements, so they are only walked
        // because ChildBlocks reaches into the object definition for them.
        var only = Assert.Single(Lint(
            "Define object bag with (the number weight).\n" +
            "Bind void to dump unto bag:\n" +
            "    Define xs as a series of number with (1, 2).\n" +
            "    For each in xs, repeat:\n" +
            "        For each in xs, repeat:\n" +
            "            State it.\n" +
            "        Done.\n" +
            "    Done.\n" +
            "Done."));
        Assert.Contains("both bind 'it'", only.Message);
        Assert.Equal(5, only.Line);
    }

    // ── Change the current directory before starting tasks ────────────────

    [Fact]
    public void ChangingTheDirectoryAfterStartingATask_IsFlagged()
    {
        var only = Assert.Single(Lint(
            "Pull a rabbit.\n" +
            "    Have rabbit start a task:\n" +
            "        State \"work\".\n" +
            "    Done.\n" +
            "    The current directory becomes \"/tmp\".\n" +
            "Done."));
        Assert.Contains("already started a task", only.Message);
        Assert.Contains("Change it before starting any task", only.Message);
        Assert.Equal(5, only.Line);
        Assert.Equal(DiagnosticSeverity.Warning, only.Severity);
    }

    [Fact]
    public void ChangingItInsideATryAfterStartingATask_IsStillFlagged()
    {
        // How it is actually written — the change is fallible. The handler blocks are ordinary
        // nested statements, so the rule has to see through them.
        var only = Assert.Single(Lint(
            "Pull a rabbit.\n" +
            "    Have rabbit start a task:\n" +
            "        State \"work\".\n" +
            "    Done.\n" +
            "    Try to:\n" +
            "        The current directory becomes \"/tmp\".\n" +
            "    Done.\n" +
            "    In case of failure:\n" +
            "        State \"could not move there\".\n" +
            "    Done.\n" +
            "Done."));
        Assert.Equal(6, only.Line);
    }

    [Fact]
    public void ChangingTheDirectoryBeforeStartingATask_SaysNothing()
    {
        // ★ This is the ordering the compiler's own refusal message recommends. Flagging it would
        // mean the two tools contradict each other.
        Assert.Empty(Lint(
            "Pull a rabbit.\n" +
            "    The current directory becomes \"/tmp\".\n" +
            "    Have rabbit start a task:\n" +
            "        State \"work\".\n" +
            "    Done.\n" +
            "Done."));
    }

    [Fact]
    public void ChangingTheDirectoryInARabbitWithNoTasks_SaysNothing()
    {
        Assert.Empty(Lint(
            "Pull a rabbit.\n" +
            "    The current directory becomes \"/tmp\".\n" +
            "    State \"moved\".\n" +
            "Done."));
    }
}
