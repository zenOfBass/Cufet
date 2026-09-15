using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// A block whose `Done.` never arrives says which block, and where it began.
/// </summary>
/// <remarks>
/// <para>
/// ⚠⚠ MEASURED 2026-09-14, by writing broken programs to pick a first tutorial lesson. Cufet's
/// TYPE refusals have four parts — the rule, what you did, the fix, a worked example — and its
/// PARSE refusals had one: <c>expected Done, got Eof ""</c>. That lands where it does the most
/// harm, because a beginner leaves a block open long before they mismatch a type, so the first
/// refusal anybody meets was the worst one the language produced.
/// </para>
/// <para>
/// ★★ The good message already existed, for <c>repeat</c> and nothing else. `ParseLoopBody` has
/// always taken an opener token; six of its seven call sites simply never passed one. Nothing here
/// is new analysis — the parser always knew which construct was open and where it started.
/// </para>
/// <para>
/// ⚠ And the `repeat` message was UNTESTED, which is why this file pins it too: the one good parse
/// error in the codebase could have been refactored away in silence.
/// </para>
/// </remarks>
public class UnclosedBlockTests
{
    private static ParseException ParseFails(string source) =>
        Assert.Throws<ParseException>(() => new Parser(new CufetLexer(source).Tokenize()).Parse());

    /// <remarks>★ It points at the `If` on line 1, not at the end of the file — the end of the
    /// file is never where the mistake is.</remarks>
    [Fact]
    public void AnUnclosedIf_NamesTheIfAndWhereItBegan()
    {
        var ex = ParseFails("""
            If 1 is 1:
                State "hi".
            """);

        Assert.Contains("this 'If' opens a block", ex.Message);
        Assert.Equal(1, ex.Line);
        Assert.DoesNotContain("expected Done", ex.Message);
    }

    /// <remarks>
    /// ⚠ The inline advice is OFFERED here and withheld below, and that difference is measured
    /// rather than assumed: `If` and `While` take a comma and one statement, `Try` and
    /// `With … open … as` do NOT. Offering an inline form to a construct that has none would send
    /// a beginner to write something the parser refuses.
    /// </remarks>
    [Fact]
    public void AnUnclosedIf_OffersTheInlineForm()
    {
        var ex = ParseFails("""
            If 1 is 1:
                State "hi".
            """);

        Assert.Contains("inline form", ex.Message);
        Assert.Contains("If x is 1, state \"one\".", ex.Message);
    }

    [Fact]
    public void AnUnclosedTry_DoesNotOfferAnInlineFormItHasNot()
    {
        var ex = ParseFails("""
            Try to:
                State "risky".
            """);

        Assert.Contains("this 'Try' opens a block", ex.Message);
        Assert.DoesNotContain("inline form", ex.Message);
    }

    /// <remarks>
    /// ⚠ `In case of failure:` opens on the word `In`, so the message named a PREPOSITION until it
    /// was given the construct's real name. *"this 'In' opens a block"* is barely better than the
    /// mechanical message it replaced.
    /// </remarks>
    [Fact]
    public void AnUnclosedFailureHandler_IsCalledByItsRealName()
    {
        var ex = ParseFails("""
            Try to:
                State "risky".
            Done.
            In case of failure:
                State "oh".
            """);

        Assert.Contains("this 'In case of failure' opens a block", ex.Message);
        Assert.DoesNotContain("this 'In' opens", ex.Message);
    }

    [Fact]
    public void AnUnclosedExceptionHandler_IsCalledByItsRealName()
    {
        var ex = ParseFails("""
            Try to:
                State "risky".
            Done.
            In case of exception:
                State "oh".
            """);

        Assert.Contains("this 'In case of exception' opens a block", ex.Message);
    }

    /// <remarks>
    /// ⚠⚠ THE ONE THAT ALREADY WORKED, pinned because it did not used to be. Its wording is also
    /// the reason the whole shape exists — for `repeat` the fix is usually to DROP the `repeat:`
    /// rather than to add a `Done.`, which the mechanical message could never have suggested.
    /// </remarks>
    [Fact]
    public void AnUnclosedForEach_StillExplainsItself()
    {
        var ex = ParseFails("""
            Define xs as a series of number with (1, 2).
            For each x in xs, repeat:
                State x converted to text.
            """);

        Assert.Contains("this 'repeat' opens a block", ex.Message);
        Assert.Contains("For each n in items, State n.", ex.Message);
    }

    /// <remarks>⚠ The guard: none of this may change what a VALID program means.</remarks>
    [Fact]
    public void EveryBlockStillParsesWhenItIsClosed()
    {
        var source = """
            Try to:
                If 1 is 1:
                    State "fine".
                Done.
            Done.
            In case of failure:
                State "no".
            Done.
            """;

        var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        Assert.NotEmpty(program.Statements);
    }
}
