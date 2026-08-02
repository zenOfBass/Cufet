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
        parser.Parse();
        return Linter.Lint(tokens, parser.StatementStarts);
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
}
