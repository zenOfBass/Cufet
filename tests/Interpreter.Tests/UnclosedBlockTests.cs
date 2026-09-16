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
    // ———— Declaration bodies ————————————————————————————————————————————————————————————————EEE
    //
    // ⚠⚠ THESE WERE THE SIX LEFT OUT, and `Bind` is the commonest block in the language. The
    // pass above gave every BLOCK STAT—ENT its opener and left every DECLARATION body answering
    // `expected Done, got Eof ""` at the end of the file. The gap was widest exactly where a
    // beginner hits it first.
    //
    // ★ Every inline form quoted in these messages was RUN before it was written into one. A
    // refusal that hands someone a line the parser would reject is worse than the mechanical
    // message it replaced, and the setter's shape took two tries to get right.

    [Fact]
    public void AnUnclosedBind_NamesTheBindAndWhereItBegan()
    {
        var ex = ParseFails("""
            Bind number to double, given (the number n):
                Return n * 2.
            """);

        Assert.Contains("this 'Bind' opens a block", ex.Message);
        Assert.Equal(1, ex.Line);
        Assert.DoesNotContain("expected Done", ex.Message);
        // ★ The inline form it offers is the VALUE one, because this body gives something back.
        Assert.Contains("Bind number to double, given (the number n), n * 2.", ex.Message);
    }

    /// <remarks>⚠ A void body's inline form is a STAT—ENT, not an expression, so the two Bind
    /// branches cannot share one example — offering the wrong one sends a reader to write
    /// something that is refused.</remarks>
    [Fact]
    public void AnUnclosedVoidBind_OffersTheStatementForm()
    {
        var ex = ParseFails("""
            Bind void to greet:
                State "hi".
            """);

        Assert.Contains("this 'Bind' opens a block", ex.Message);
        Assert.Contains("Bind void to greet, State", ex.Message);
    }

    [Fact]
    public void AnUnclosedGetter_NamesTheGet()
    {
        var ex = ParseFails("""
            Define object box with (the number n).
            Get doubled unto box as number:
                Return one's n * 2.
            """);

        Assert.Contains("this 'Get' opens a block", ex.Message);
        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public void AnUnclosedSetter_NamesTheSet()
    {
        var ex = ParseFails("""
            Define object box with (the number n).
            Set bump unto box given (the number v):
                One's n becomes v.
            """);

        Assert.Contains("this 'Set' opens a block", ex.Message);
        Assert.Equal(2, ex.Line);
    }

    /// <remarks>
    /// ★★ THESE TWO OPEN ON THE WORD `Bind`, so they say WHAT they are. *"this 'Bind' opens a
    /// block"* is true of an unmaker and useless — it is the same reason the failure-handler arms
    /// pass a construct rather than letting the message name a preposition.
    /// </remarks>
    [Fact]
    public void AnUnclosedUnmaker_SaysWhichKindOfBind()
    {
        var ex = ParseFails("""
            Define object handle with (the number id).
            Bind unmaking a handle to release:
                State "closed".
            """);

        Assert.Contains("this 'Bind unmaking' opens a block", ex.Message);
        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public void AnUnclosedOverload_SaysWhichKindOfBind()
    {
        var ex = ParseFails("""
            Define object vec with (the number x).
            Bind overloading +, given (the lhs is a vec, the rhs is a vec):
                Return a new vec { the x lhs's x + rhs's x }.
            """);

        Assert.Contains("this 'Bind overloading' opens a block", ex.Message);
        Assert.Equal(2, ex.Line);
    }
    // —— Left open by what came NEXT ————————————————————
    //
    // ⚠⚠ A block can be left open by the next ARM arriving, not only by the file ending, and
    // only the second half had a sentence. MEASURED: a block-form `If` with no `Done.` before its
    // `Otherwise` answered `expected statement keyword, got Otherwise "Otherwise"`, blamed the
    // `Otherwise`, and never mentioned the `If` still open two lines up.
    //
    // ★★ THE LANGUAGE INVITES THIS ONE, which is why it earns its own message rather than a
    // generic one. The INLINE form takes no `Done.` at all — fizzbuzz.cufe is written that way —
    // while the block form requires one before every arm. Two neighbouring spellings disagree, so
    // the message names both.

    private static Cufet.Interpreter.Program Parses(string source) =>
        new Parser(new CufetLexer(source).Tokenize()).Parse();

    [Fact]
    public void AnUnclosedIfBeforeOtherwise_NamesTheIfAndTheArm()
    {
        var ex = ParseFails("""
            If 1 is 1:
                State "one".
            Otherwise:
                State "other".
            Done.
            """);

        Assert.Contains("this 'If' opens a block", ex.Message);
        Assert.Contains("'Otherwise' arrived on line 3", ex.Message);
        // ★ It points at the `If`, not at the token it tripped over.
        Assert.Equal(1, ex.Line);
        Assert.DoesNotContain("expected statement keyword", ex.Message);
    }

    /// <remarks>★ Both spellings are named, because the reader's next question is which one they
    /// meant to be writing.</remarks>
    [Fact]
    public void ThatMessage_NamesBothFormsAndTheirDoneRule()
    {
        var ex = ParseFails("""
            If 1 is 1:
                State "one".
            Otherwise:
                State "other".
            Done.
            """);

        Assert.Contains("every arm closes before the next one begins", ex.Message);
        Assert.Contains("the one that needs no 'Done.'", ex.Message);
    }

    /// <remarks>⚠ A `Judge` arm body is parsed by the same helper, so it was the same hole.</remarks>
    [Fact]
    public void AnUnclosedJudgeArmBeforeOtherwise_IsCaughtToo()
    {
        var ex = ParseFails("""
            Define object circle with (the number r).
            Define s as a new circle { the r 2 }.
            Judge s, where it is:
                A circle:
                    State "round".
                Otherwise:
                    State "other".
            Done.
            """);

        Assert.Contains("opens a block", ex.Message);
        Assert.Contains("'Otherwise' arrived on line 6", ex.Message);
    }

    // —— The controls ————————————————————

    /// <remarks>★ The correct block form still parses — the new check must not fire on the shape
    /// it is teaching people to write.</remarks>
    [Fact]
    public void TheCorrectBlockForm_StillParses()
    {
        Parses("""
            If 1 is 1:
                State "one".
            Done.
            Otherwise:
                State "other".
            Done.
            """);
    }

    /// <remarks>★ And the inline form, which needs no `Done.` anywhere.</remarks>
    [Fact]
    public void TheInlineForm_NeedsNoDone()
    {
        Parses("""
            If 1 is 1, state "one".
            Otherwise, state "other".
            """);
    }

    /// <remarks>
    /// ⚠⚠ THE LINE THE CHECK MUST NOT CRO★. An `Otherwise` inside a LOOP body, with no `If`
    /// open at all, is an ordinary mistake — reporting the loop as unclosed would be a confident
    /// wrong answer, which is the trap `IsStatementOpener` already warns about. Only
    /// `ParseIfBody` passes the continuation token, so a loop body is untouched.
    /// </remarks>
    [Fact]
    public void AStrayOtherwiseInALoop_IsNotBlamedOnTheLoop()
    {
        var ex = ParseFails("""
            For each n in the range 1 to 2, repeat:
                Otherwise:
                    State "x".
            Done.
            """);

        Assert.DoesNotContain("opens a block", ex.Message);
        Assert.Contains("Otherwise", ex.Message);
    }
}
