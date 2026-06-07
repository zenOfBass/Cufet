using NLP.Interpreter;
using NLP.Lexer;
using Xunit;
using NlpLexer = NLP.Lexer.Lexer;

namespace NLP.Interpreter.Tests;

public class InterpreterTests
{
    private static string Run(string source)
    {
        var tokens  = new NlpLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        var output  = new StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    // ── Keyword case-insensitivity ────────────────────────────────────────

    [Fact]
    public void KeywordsAreCaseInsensitiveEndToEnd()
    {
        Assert.Equal("42", Run("define x as 42. state x."));
        Assert.Equal("42", Run("DEFINE x AS 42. STATE x."));
    }

    // ── State ─────────────────────────────────────────────────────────────

    [Fact]
    public void StateInteger()
    {
        Assert.Equal("5", Run("State 5."));
    }

    [Fact]
    public void StateLargeInteger()
    {
        Assert.Equal("1000000", Run("State 1000000."));
    }

    [Fact]
    public void MultipleStateStatements()
    {
        Assert.Equal("1\n2\n3", Run("State 1. State 2. State 3."));
    }

    [Fact]
    public void StateWithArticleNoise()
    {
        // Articles before the number are noise and must be silently skipped
        Assert.Equal("42", Run("State the 42."));
    }

    [Fact]
    public void StateAcrossMultipleLines()
    {
        Assert.Equal("7\n8", Run("State 7.\nState 8."));
    }

    // ── Decimal numbers ───────────────────────────────────────────────────

    [Fact]
    public void StateDecimal()
    {
        Assert.Equal("3.14", Run("State 3.14."));
    }

    // ── String literals ───────────────────────────────────────────────────

    [Fact]
    public void StateString()
    {
        Assert.Equal("hello", Run("State \"hello\"."));
    }

    [Fact]
    public void StateStringWithSingleQuotes()
    {
        Assert.Equal("I can't do that", Run("State \"I can't do that\"."));
    }

    [Fact]
    public void StateStringWithDoubledQuote()
    {
        Assert.Equal("say \"hi\"", Run("State \"say \"\"hi\"\"\"."));
    }

    // ── Arithmetic ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("State 1 + 2.",    "3")]
    [InlineData("State 10 - 3.",   "7")]
    [InlineData("State 2 * 5.",    "10")]
    [InlineData("State 10 / 4.",   "2.5")]
    [InlineData("State -5.",       "-5")]
    [InlineData("State -(3 + 2).", "-5")]
    public void ArithmeticLiterals(string src, string expected) =>
        Assert.Equal(expected, Run(src));

    [Fact]
    public void PrecedenceMultiplicationBeforeAddition() =>
        Assert.Equal("14", Run("State 2 + 3 * 4."));

    [Fact]
    public void ParenthesesOverridePrecedence() =>
        Assert.Equal("20", Run("State (2 + 3) * 4."));

    [Fact]
    public void ArithmeticWithVariables()
    {
        Assert.Equal("8", Run("Define x as 5. Define y as 3. State x + y."));
    }

    [Fact]
    public void DivisionByZeroThrows() =>
        Assert.Throws<RuntimeException>(() => Run("State 1 / 0."));

    [Fact]
    public void ArithmeticOnStringThrows() =>
        Assert.Throws<RuntimeException>(() => Run("State \"hello\" + 1."));

    // ── Comparison ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("State 1 = 1.",  "true")]
    [InlineData("State 1 = 2.",  "false")]
    [InlineData("State 2 > 1.",  "true")]
    [InlineData("State 1 < 2.",  "true")]
    [InlineData("State 2 >= 2.", "true")]
    [InlineData("State 1 <= 0.", "false")]
    public void ComparisonResults(string src, string expected) =>
        Assert.Equal(expected, Run(src));

    [Fact]
    public void StringEqualityComparison()
    {
        Assert.Equal("true",  Run("State \"hi\" = \"hi\"."));
        Assert.Equal("false", Run("State \"hi\" = \"bye\"."));
    }

    // ── Define and variable references ───────────────────────────────────

    [Fact]
    public void DefineAndStateVariable()
    {
        Assert.Equal("42", Run("Define total as 42. State total."));
    }

    [Fact]
    public void DefineWithArticleIsIdentical()
    {
        // "Define the total as 0." and "Define total as 0." are the same statement
        Assert.Equal("0", Run("Define the total as 0. State the total."));
    }

    [Fact]
    public void DefineString()
    {
        Assert.Equal("hello", Run("Define greeting as \"hello\". State greeting."));
    }

    [Fact]
    public void BecomesReassignsValue()
    {
        Assert.Equal("99", Run("Define score as 0. score becomes 99. State score."));
    }

    [Fact]
    public void BecomesFromVariable()
    {
        Assert.Equal("7", Run("Define x as 7. Define y as 0. y becomes x. State y."));
    }

    // ── Runtime errors ────────────────────────────────────────────────────

    [Fact]
    public void DoubleDefineThrows()
    {
        Assert.Throws<RuntimeException>(() => Run("Define x as 1. Define x as 2."));
    }

    [Fact]
    public void BecomesUndefinedThrows()
    {
        Assert.Throws<RuntimeException>(() => Run("x becomes 5."));
    }

    [Fact]
    public void ReferenceUndefinedThrows()
    {
        Assert.Throws<RuntimeException>(() => Run("State x."));
    }

    // ── If / Otherwise if / Otherwise ────────────────────────────────────

    [Fact]
    public void IfTrueSingleStmt()
    {
        Assert.Equal("yes", Run("Define x as 1. If x is 1: State \"yes\"."));
    }

    [Fact]
    public void IfFalseSingleStmt()
    {
        Assert.Equal("", Run("Define x as 2. If x is 1: State \"yes\"."));
    }

    [Fact]
    public void IfElseTrueBranch()
    {
        Assert.Equal("yes", Run("Define x as 1. If x is 1: State \"yes\". Otherwise: State \"no\"."));
    }

    [Fact]
    public void IfElseFalseBranch()
    {
        Assert.Equal("no", Run("Define x as 2. If x is 1: State \"yes\". Otherwise: State \"no\"."));
    }

    [Fact]
    public void OtherwiseIfFirstArmMatches()
    {
        Assert.Equal("one", Run(
            "Define x as 1. " +
            "If x is 1: State \"one\". " +
            "Otherwise if x is 2: State \"two\". " +
            "Otherwise: State \"other\"."));
    }

    [Fact]
    public void OtherwiseIfSecondArmMatches()
    {
        Assert.Equal("two", Run(
            "Define x as 2. " +
            "If x is 1: State \"one\". " +
            "Otherwise if x is 2: State \"two\". " +
            "Otherwise: State \"other\"."));
    }

    [Fact]
    public void OtherwiseIfNoArmMatchesNoElse()
    {
        Assert.Equal("", Run(
            "Define x as 9. " +
            "If x is 1: State \"one\". " +
            "Otherwise if x is 2: State \"two\"."));
    }

    [Fact]
    public void MultiStmtBodyWithDone()
    {
        Assert.Equal("a\nb", Run(
            "Define x as 1. " +
            "If x is 1: State \"a\". State \"b\". Done."));
    }

    [Fact]
    public void NestedIf()
    {
        Assert.Equal("both", Run(
            "Define x as 1. Define y as 2. " +
            "If x is 1: If y is 2: State \"both\"."));
    }

    // ── Word-form comparisons ─────────────────────────────────────────────

    [Fact]
    public void WordFormIs()
    {
        Assert.Equal("yes", Run("Define x as 5. If x is 5: State \"yes\"."));
    }

    [Fact]
    public void WordFormIsNot()
    {
        Assert.Equal("yes", Run("Define x as 5. If x is not 3: State \"yes\"."));
    }

    [Fact]
    public void WordFormIsGreaterThan()
    {
        Assert.Equal("yes", Run("Define x as 10. If x is greater than 5: State \"yes\"."));
        Assert.Equal("", Run("Define x as 3. If x is greater than 5: State \"yes\"."));
    }

    [Fact]
    public void WordFormIsLessThan()
    {
        Assert.Equal("yes", Run("Define x as 2. If x is less than 5: State \"yes\"."));
        Assert.Equal("", Run("Define x as 7. If x is less than 5: State \"yes\"."));
    }

    [Fact]
    public void WordFormOrMore()
    {
        Assert.Equal("yes", Run("Define x as 5. If x is 5 or more: State \"yes\"."));
        Assert.Equal("yes", Run("Define x as 6. If x is 5 or more: State \"yes\"."));
        Assert.Equal("", Run("Define x as 4. If x is 5 or more: State \"yes\"."));
    }

    [Fact]
    public void WordFormOrLess()
    {
        Assert.Equal("yes", Run("Define x as 5. If x is 5 or less: State \"yes\"."));
        Assert.Equal("yes", Run("Define x as 3. If x is 5 or less: State \"yes\"."));
        Assert.Equal("", Run("Define x as 7. If x is 5 or less: State \"yes\"."));
    }

    [Fact]
    public void VariableCondition()
    {
        // stored bool used directly as condition
        Assert.Equal("yes", Run("Define flag as 1 = 1. If flag: State \"yes\"."));
    }

    // ── Control flow parse errors ─────────────────────────────────────────

    [Fact]
    public void DoneWithSingleStmtThrows()
    {
        Assert.Throws<ParseException>(() => Run("Define x as 1. If x is 1: State x. Done."));
    }

    [Fact]
    public void MultiStmtBodyMissingDoneThrows()
    {
        Assert.Throws<ParseException>(() => Run(
            "Define x as 1. If x is 1: State x. State x. Otherwise: State x."));
    }

    // ── Control flow runtime errors ───────────────────────────────────────

    [Fact]
    public void NonBoolConditionThrows()
    {
        Assert.Throws<RuntimeException>(() => Run("Define x as 5. If x: State x."));
    }

    // ── While loops ──────────────────────────────────────────────────────

    [Fact]
    public void WhileRunsMultipleTimes()
    {
        // Prints x on every iteration — confirms the loop body ran 5 times.
        Assert.Equal("1\n2\n3\n4\n5", Run(
            "Define x as 0. " +
            "While x is less than 5, repeat: x becomes x + 1. State x. Done."));
    }

    [Fact]
    public void WhileRunsZeroTimesWhenFalseInitially()
    {
        // Condition false before first iteration — body never executes.
        Assert.Equal("", Run(
            "Define x as 10. " +
            "While x is less than 5, repeat: State x. Done."));
    }

    [Fact]
    public void WhileMultiStmtBody()
    {
        // After the loop both counters should be 3; State is at outer scope after Done.
        Assert.Equal("3\n3", Run(
            "Define x as 0. Define count as 0. " +
            "While x is less than 3, repeat: x becomes x + 1. count becomes count + 1. Done. " +
            "State x. State count."));
    }

    // ── Repeat...Until loops ─────────────────────────────────────────────

    [Fact]
    public void RepeatUntilRunsAtLeastOnce()
    {
        // x >= 0 is true before the loop starts, but the body still executes once.
        Assert.Equal("1", Run(
            "Define x as 0. " +
            "Repeat: x becomes x + 1. until x is 0 or more. " +
            "State x."));
    }

    [Fact]
    public void RepeatUntilRunsMultipleTimes()
    {
        Assert.Equal("5", Run(
            "Define x as 0. " +
            "Repeat: x becomes x + 1. until x is 5 or more. " +
            "State x."));
    }

    [Fact]
    public void RepeatUntilMultiStmtBody()
    {
        Assert.Equal("3\n3", Run(
            "Define x as 0. Define count as 0. " +
            "Repeat: x becomes x + 1. count becomes count + 1. until x is 3 or more. " +
            "State x. State count."));
    }

    // ── Stop / Skip ───────────────────────────────────────────────────────

    [Fact]
    public void StopExitsWhileLoop()
    {
        Assert.Equal("3", Run(
            "Define x as 0. " +
            "While x is less than 10, repeat: " +
            "    x becomes x + 1. " +
            "    If x is 3: Stop. " +
            "Done. " +
            "State x."));
    }

    [Fact]
    public void SkipInWhileLoop()
    {
        // Otherwise terminates the if arm cleanly so ParseBody sees Otherwise, not Done.
        // When x is 2 the Skip arm fires; all other values print.
        Assert.Equal("1\n3\n4", Run(
            "Define x as 0. " +
            "While x is less than 4, repeat: " +
            "x becomes x + 1. " +
            "If x is 2: Skip. Otherwise: State x. " +
            "Done."));
    }

    [Fact]
    public void StopInRepeatUntilLoop()
    {
        Assert.Equal("3", Run(
            "Define x as 0. " +
            "Repeat: x becomes x + 1. If x is 3: Stop. until x is 10 or more. " +
            "State x."));
    }

    [Fact]
    public void SkipInRepeatUntilLoop()
    {
        // Otherwise terminates the if arm; the else body sees `until` as its natural break.
        Assert.Equal("1\n3\n4\n5", Run(
            "Define x as 0. " +
            "Repeat: x becomes x + 1. If x is 2: Skip. Otherwise: State x. until x is 5 or more."));
    }

    [Fact]
    public void NestedLoopsStopInnerOnly()
    {
        // Stop breaks the inner loop; the outer loop runs to completion.
        Assert.Equal("3", Run(
            "Define outer as 0. Define inner as 0. " +
            "While outer is less than 3, repeat: " +
            "    outer becomes outer + 1. " +
            "    inner becomes 0. " +
            "    While inner is less than 5, repeat: " +
            "        inner becomes inner + 1. " +
            "        If inner is 2: Stop. " +
            "    Done. " +
            "Done. " +
            "State outer."));
    }

    // ── Loop parse errors ─────────────────────────────────────────────────

    [Fact]
    public void StopOutsideLoopThrows()
    {
        Assert.Throws<ParseException>(() => Run("Stop."));
    }

    [Fact]
    public void SkipOutsideLoopThrows()
    {
        Assert.Throws<ParseException>(() => Run("Skip."));
    }

    [Fact]
    public void StopInIfOutsideLoopThrows()
    {
        Assert.Throws<ParseException>(() => Run("Define x as 1. If x is 1: Stop."));
    }

    // ── Loop runtime errors ───────────────────────────────────────────────

    [Fact]
    public void NonBoolWhileConditionThrows()
    {
        Assert.Throws<RuntimeException>(() => Run("Define x as 5. While x, repeat: x becomes x + 1. Done."));
    }

    [Fact]
    public void NonBoolUntilConditionThrows()
    {
        Assert.Throws<RuntimeException>(() => Run("Define x as 5. Repeat: x becomes x + 1. until x."));
    }

    // ── Series literals ───────────────────────────────────────────────────

    [Fact]
    public void DefineAndStateSeries()
    {
        Assert.Equal("(90, 85, 70)", Run("Define scores as a series (90, 85, 70). State scores."));
    }

    [Fact]
    public void SeriesSingleElement()
    {
        Assert.Equal("(42)", Run("Define s as a series (42). State s."));
    }

    [Fact]
    public void SeriesEmpty()
    {
        Assert.Equal("()", Run("Define s as a series (). State s."));
    }

    [Fact]
    public void SeriesWithExpressions()
    {
        Assert.Equal("(3, 12)", Run("Define s as a series (1 + 2, 3 * 4). State s."));
    }

    [Fact]
    public void SeriesWithVariables()
    {
        Assert.Equal("(5, 6)", Run("Define x as 5. Define s as a series (x, x + 1). State s."));
    }

    [Fact]
    public void SeriesNoArticle()
    {
        // "a" before series is an Article (noise) — omitting it is valid
        Assert.Equal("(1, 2, 3)", Run("Define s as series (1, 2, 3). State s."));
    }

    [Fact]
    public void ArithmeticGroupingStillWorks()
    {
        // Parens in expression context remain grouping — series context only triggers on "series" keyword
        Assert.Equal("20", Run("State (2 + 3) * 4."));
    }

    // ── Series access ─────────────────────────────────────────────────────

    [Fact]
    public void SeriesAccessFirst()
    {
        Assert.Equal("10", Run("Define s as a series (10, 20, 30). State the first of s."));
    }

    [Fact]
    public void SeriesAccessSecond()
    {
        Assert.Equal("20", Run("Define s as a series (10, 20, 30). State the second of s."));
    }

    [Fact]
    public void SeriesAccessLast()
    {
        Assert.Equal("30", Run("Define s as a series (10, 20, 30). State the last of s."));
    }

    [Fact]
    public void SeriesAccessParametric()
    {
        Assert.Equal("20", Run("Define s as a series (10, 20, 30). State item 2 of s."));
    }

    [Fact]
    public void SeriesAccessParametricVariable()
    {
        Assert.Equal("30", Run("Define s as a series (10, 20, 30). Define n as 3. State item n of s."));
    }

    [Fact]
    public void SeriesAccessParametricExpression()
    {
        Assert.Equal("20", Run("Define s as a series (10, 20, 30). State item 1 + 1 of s."));
    }

    [Fact]
    public void SeriesAccessOutOfBoundsThrows()
    {
        Assert.Throws<RuntimeException>(() => Run("Define s as a series (10, 20). State item 5 of s."));
    }

    // ── Series length ─────────────────────────────────────────────────────

    [Fact]
    public void SeriesLength()
    {
        Assert.Equal("3", Run("Define s as a series (10, 20, 30). State the number of s."));
    }

    [Fact]
    public void SeriesLengthEmpty()
    {
        Assert.Equal("0", Run("Define s as a series (). State the number of s."));
    }

    [Fact]
    public void SeriesLengthInCondition()
    {
        // Iterates over a series using a while loop driven by its length.
        Assert.Equal("10\n20\n30", Run(
            "Define s as a series (10, 20, 30). " +
            "Define i as 0. " +
            "While i is less than the number of s, repeat: " +
            "i becomes i + 1. State item i of s. " +
            "Done."));
    }

    // ── Series add ────────────────────────────────────────────────────────

    [Fact]
    public void SeriesAddAppend()
    {
        Assert.Equal("4", Run(
            "Define s as a series (1, 2, 3). " +
            "Add 4 to s. " +
            "State the number of s. State the last of s.").Split('\n')[0]);
    }

    [Fact]
    public void SeriesAddToStart()
    {
        Assert.Equal("0", Run(
            "Define s as a series (1, 2, 3). " +
            "Add 0 to the start of s. " +
            "State the first of s."));
    }

    [Fact]
    public void SeriesAddAfterOrdinal()
    {
        // Insert 99 after position 2 → (10, 20, 99, 30)
        Assert.Equal("10\n20\n99\n30", Run(
            "Define s as a series (10, 20, 30). " +
            "Add 99 after the second item of s. " +
            "State item 1 of s. State item 2 of s. State item 3 of s. State item 4 of s."));
    }

    [Fact]
    public void SeriesAddAfterParametric()
    {
        Assert.Equal("10\n99\n20\n30", Run(
            "Define s as a series (10, 20, 30). " +
            "Define n as 1. " +
            "Add 99 after item n of s. " +
            "State item 1 of s. State item 2 of s. State item 3 of s. State item 4 of s."));
    }

    // ── Series remove ─────────────────────────────────────────────────────

    [Fact]
    public void SeriesRemoveByOrdinal()
    {
        // Remove first item → (20, 30)
        Assert.Equal("2\n20", Run(
            "Define s as a series (10, 20, 30). " +
            "Remove the first item from s. " +
            "State the number of s. State the first of s."));
    }

    [Fact]
    public void SeriesRemoveByParametric()
    {
        // Remove item 2 → (10, 30)
        Assert.Equal("10\n30", Run(
            "Define s as a series (10, 20, 30). " +
            "Remove item 2 from s. " +
            "State item 1 of s. State item 2 of s."));
    }

    [Fact]
    public void SeriesRemoveByValue()
    {
        // Remove first occurrence of 20 → (10, 30)
        Assert.Equal("2", Run(
            "Define s as a series (10, 20, 30). " +
            "Remove 20 from s. " +
            "State the number of s."));
    }

    [Fact]
    public void SeriesRemoveByValueNotFoundThrows()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Define s as a series (10, 20, 30). Remove 99 from s."));
    }

    // ── Series element assignment ─────────────────────────────────────────

    [Fact]
    public void SeriesSetByOrdinal()
    {
        Assert.Equal("99", Run(
            "Define s as a series (10, 20, 30). " +
            "the second of s becomes 99. " +
            "State item 2 of s."));
    }

    [Fact]
    public void SeriesSetByParametric()
    {
        Assert.Equal("99", Run(
            "Define s as a series (10, 20, 30). " +
            "Define n as 3. " +
            "item n of s becomes 99. " +
            "State the last of s."));
    }

    [Fact]
    public void SeriesSetLast()
    {
        Assert.Equal("99", Run(
            "Define s as a series (10, 20, 30). " +
            "the last of s becomes 99. " +
            "State item 3 of s."));
    }

    [Fact]
    public void SeriesSetOutOfBoundsThrows()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Define s as a series (10, 20). item 5 of s becomes 99."));
    }

    [Fact]
    public void SeriesSetOnNonSeriesThrows()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Define x as 5. the first of x becomes 99."));
    }

    // ── For-each loop ─────────────────────────────────────────────────────

    [Fact]
    public void ForEachNamedIterator()
    {
        Assert.Equal("10\n20\n30", Run(
            "Define s as a series (10, 20, 30).\n" +
            "For each x in s, repeat:\n" +
            "    State x.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachBareIt()
    {
        Assert.Equal("1\n2\n3", Run(
            "Define s as a series (1, 2, 3).\n" +
            "For each in s, repeat:\n" +
            "    State it.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachArticleBeforeSeries()
    {
        // "the" before series name is noise and must be skipped
        Assert.Equal("5\n6", Run(
            "Define nums as a series (5, 6).\n" +
            "For each n in the nums, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachEmptySeriesRunsZeroTimes()
    {
        Assert.Equal("done", Run(
            "Define s as a series ().\n" +
            "For each x in s, repeat:\n" +
            "    State x.\n" +
            "Done.\n" +
            "State \"done\"."));
    }

    [Fact]
    public void ForEachSingleElement()
    {
        Assert.Equal("42", Run(
            "Define s as a series (42).\n" +
            "For each x in s, repeat:\n" +
            "    State x.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachStopBreaksLoop()
    {
        // Inline "If: Stop." must be the last statement before Done. (grammar invariant).
        // Accumulate count before the stop check so we can verify early exit.
        Assert.Equal("2", Run(
            "Define s as a series (1, 2, 3, 4).\n" +
            "Define count as 0.\n" +
            "For each x in s, repeat:\n" +
            "    count becomes count + 1.\n" +
            "    If x is 2: Stop.\n" +
            "Done.\n" +
            "State count."));
    }

    [Fact]
    public void ForEachSkipSkipsIteration()
    {
        // "If: Skip. Otherwise: ..." — Otherwise is a break-inducer so the if-arm stays single-stmt.
        Assert.Equal("12", Run(
            "Define s as a series (1, 2, 3, 4, 5).\n" +
            "Define total as 0.\n" +
            "For each x in s, repeat:\n" +
            "    If x is 3: Skip.\n" +
            "    Otherwise: total becomes total + x.\n" +
            "Done.\n" +
            "State total."));
    }

    [Fact]
    public void ForEachComputesSum()
    {
        Assert.Equal("336", Run(
            "Define scores as a series (92, 85, 71, 88).\n" +
            "Define total as 0.\n" +
            "For each score in scores, repeat:\n" +
            "    total becomes total + score.\n" +
            "Done.\n" +
            "State total."));
    }

    [Fact]
    public void ForEachIteratorShadowsAndRestores()
    {
        // x exists before the loop; loop uses x as iterator; x is restored to 99 after
        Assert.Equal("99\n1\n2\n3\n99", Run(
            "Define x as 99.\n" +
            "Define loop-vals as a series (1, 2, 3).\n" +
            "State x.\n" +
            "For each x in loop-vals, repeat:\n" +
            "    State x.\n" +
            "Done.\n" +
            "State x."));
    }

    [Fact]
    public void ForEachItRestoredAfterLoop()
    {
        // Bare-it loop; "it" was not defined before; after loop it is removed
        Assert.Throws<RuntimeException>(() => Run(
            "Define s as a series (1, 2).\n" +
            "For each in s, repeat:\n" +
            "    State it.\n" +
            "Done.\n" +
            "State it."));
    }

    [Fact]
    public void ForEachNestedNamedIterators()
    {
        // Nested named loops; each State is its own line
        // outer (1,2) × inner (10,100): 1+10=11, 1+100=101, 2+10=12, 2+100=102
        Assert.Equal("11\n101\n12\n102", Run(
            "Define outer as a series (1, 2).\n" +
            "Define inner as a series (10, 100).\n" +
            "For each x in outer, repeat:\n" +
            "    For each y in inner, repeat:\n" +
            "        State x + y.\n" +
            "    Done.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachNestedBareItShadowing()
    {
        // Inside inner loop, it = inner element.
        // After inner Done., it = outer element again (restored).
        // outer (1,2), inner (10,20):
        //   pass 1: state it=1, inner prints 10,20, state it=1 again
        //   pass 2: state it=2, inner prints 10,20, state it=2 again
        Assert.Equal("1\n10\n20\n1\n2\n10\n20\n2", Run(
            "Define outer-s as a series (1, 2).\n" +
            "Define inner-s as a series (10, 20).\n" +
            "For each in outer-s, repeat:\n" +
            "    State it.\n" +
            "    For each in inner-s, repeat:\n" +
            "        State it.\n" +
            "    Done.\n" +
            "    State it.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachMutationDuringLoopThrows()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Define s as a series (1, 2, 3).\n" +
            "For each x in s, repeat:\n" +
            "    Add 99 to s.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachRemoveDuringLoopThrows()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Define s as a series (1, 2, 3).\n" +
            "For each x in s, repeat:\n" +
            "    Remove the first item from s.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachWorksOnSeriesLiteralInline()
    {
        // series name can be a freshly-defined variable; also testing article in "in the"
        Assert.Equal("a\nb\nc", Run(
            "Define letters as a series (\"a\", \"b\", \"c\").\n" +
            "For each ch in the letters, repeat:\n" +
            "    State ch.\n" +
            "Done."));
    }

    // ── Parse errors ──────────────────────────────────────────────────────

    [Fact]
    public void MissingDotThrows()
    {
        Assert.Throws<ParseException>(() => Run("State 5"));
    }

    [Fact]
    public void UnrecognizedUppercaseWordThrows()
    {
        // Uppercase-initial non-keywords are caught at the lexer, not the parser
        Assert.Throws<LexerException>(() => Run("Print 5."));
    }
}
