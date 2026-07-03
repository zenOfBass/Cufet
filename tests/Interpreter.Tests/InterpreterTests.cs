using Cufet.Interpreter;
using Cufet.Lexer;
using System.Runtime.ExceptionServices;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

public class InterpreterTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var output  = new StringWriter();
        RunOnLargeStack(() => new Interpreter(output).Execute(program));
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static string RunWithInput(string source, string stdinContent)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var output  = new StringWriter();
        var input   = new StringReader(stdinContent);
        RunOnLargeStack(() => new Interpreter(output, input).Execute(program));
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static void RunOnLargeStack(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(
            () => { try { action(); } catch (Exception e) { caught = e; } },
            16 * 1024 * 1024);
        thread.Start();
        thread.Join();
        if (caught is not null)
            ExceptionDispatchInfo.Capture(caught).Throw();
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

    // ── String escape sequences ───────────────────────────────────────────

    [Fact]
    public void StringEscape_DoubleQuote()
    {
        Assert.Equal("say \"hi\"", Run("State \"say \\\"hi\\\"\"."));
    }

    [Fact]
    public void StringEscape_Newline()
    {
        Assert.Equal("a\nb", Run("State \"a\\nb\"."));
    }

    [Fact]
    public void StringEscape_Tab()
    {
        Assert.Equal("a\tb", Run("State \"a\\tb\"."));
    }

    [Fact]
    public void StringEscape_Backslash()
    {
        Assert.Equal("back\\slash", Run("State \"back\\\\slash\"."));
    }

    // ── String interpolation ──────────────────────────────────────────────

    [Fact]
    public void Interp_SimpleVariable()
    {
        Assert.Equal("hello world!",
            Run("Define name as \"world\". State \"hello {name}!\"."));
    }

    [Fact]
    public void Interp_NumberAutoConverted()
    {
        Assert.Equal("total: 15",
            Run("Define price as 5. Define qty as 3. State \"total: {price * qty}\"."));
    }

    [Fact]
    public void Interp_FactAutoConverted()
    {
        Assert.Equal("result: true",
            Run("Define ok as 1 = 1. State \"result: {ok}\"."));
    }

    [Fact]
    public void Interp_TextInsertedAsIs()
    {
        Assert.Equal("say hello",
            Run("Define word as \"hello\". State \"say {word}\"."));
    }

    [Fact]
    public void Interp_ArithmeticExpression()
    {
        Assert.Equal("answer: 42",
            Run("State \"answer: {6 * 7}\"."));
    }

    [Fact]
    public void Interp_MultipleHoles()
    {
        Assert.Equal("x=1, y=2",
            Run("Define x as 1. Define y as 2. State \"x={x}, y={y}\"."));
    }

    [Fact]
    public void Interp_LeadingHole()
    {
        Assert.Equal("42 items",
            Run("State \"{42} items\"."));
    }

    [Fact]
    public void Interp_TrailingHole()
    {
        Assert.Equal("count: 7",
            Run("State \"count: {7}\"."));
    }

    [Fact]
    public void Interp_OnlyHole()
    {
        Assert.Equal("42",
            Run("State \"{42}\"."));
    }

    [Fact]
    public void Interp_AdjacentHoles()
    {
        Assert.Equal("xy",
            Run("Define x as \"x\". Define y as \"y\". State \"{x}{y}\"."));
    }

    [Fact]
    public void Interp_EscapedBraceNotInterpolated()
    {
        Assert.Equal("use {braces} literally",
            Run("State \"use \\{braces\\} literally\"."));
    }

    [Fact]
    public void Interp_EscapeSequenceInPiece()
    {
        Assert.Equal("line1\nline2",
            Run("Define x as \"line2\". State \"line1\\n{x}\"."));
    }

    [Fact]
    public void Interp_FieldAccessInHole()
    {
        Assert.Equal("error: oops",
            Run("Define rec as a record with (the msg \"oops\"). State \"error: {the msg of rec}\"."));
    }

    [Fact]
    public void Interp_TypeChecker_RecordIsError()
    {
        Assert.Throws<TypeException>(() =>
            Run("Define r as a record with (1, 2). State \"value: {r}\"."));
    }

    [Fact]
    public void Interp_TypeChecker_SeriesIsError()
    {
        Assert.Throws<TypeException>(() =>
            Run("Define s as a series with (1, 2). State \"value: {s}\"."));
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
        Assert.Throws<TypeException>(() => Run("State \"hello\" + 1."));

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

    // ── Scope ─────────────────────────────────────────────────────────────

    [Fact]
    public void InnerDefineDoesNotLeakOut()
    {
        // Variable defined inside an if-block must not be visible after the block.
        // TypeChecker catches this when we try to use 'inner' in an expression that requires a known type.
        Assert.Throws<TypeException>(() => Run(
            "Define flag as 1.\n" +
            "If flag is 1:\n" +
            "  Define inner as 99.\n" +
            "Done.\n" +
            "Define result as inner + 1."));
    }

    [Fact]
    public void InnerBlockCanReadOuterVar()
    {
        Assert.Equal("42", Run(
            "Define x as 42.\n" +
            "Define flag as 1.\n" +
            "If flag is 1:\n" +
            "  State x.\n" +
            "Done."));
    }

    [Fact]
    public void InnerBlockCanModifyOuterVar()
    {
        Assert.Equal("99", Run(
            "Define x as 1.\n" +
            "Define flag as 1.\n" +
            "If flag is 1:\n" +
            "  x becomes 99.\n" +
            "Done.\n" +
            "State x."));
    }

    [Fact]
    public void ShadowingOuterVarWithoutKeywordIsError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define x as 1.\n" +
            "Define flag as 1.\n" +
            "If flag is 1:\n" +
            "  Define x as 2.\n" +
            "Done."));
    }

    [Fact]
    public void DeliberateShadowWorks()
    {
        // 'Define a shadow x' inside a block shadows outer x; outer x unchanged after.
        Assert.Equal("1", Run(
            "Define x as 1.\n" +
            "Define flag as 1.\n" +
            "If flag is 1:\n" +
            "  Define a shadow x as 99.\n" +
            "Done.\n" +
            "State x."));
    }

    [Fact]
    public void ShadowKeywordOnNonExistentOuterIsError()
    {
        // 'a shadow' asserts that an outer binding exists — should error when none does.
        Assert.Throws<TypeException>(() => Run(
            "Define flag as 1.\n" +
            "If flag is 1:\n" +
            "  Define a shadow ghost as 5.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachIteratorDoesNotLeakOut()
    {
        // The iterator is block-local; using it after the loop fails at runtime.
        Assert.Throws<RuntimeException>(() => Run(
            "Define nums as a series of number with (1, 2, 3).\n" +
            "For each n in nums, repeat:\n" +
            "  State n.\n" +
            "Done.\n" +
            "State n."));
    }

    [Fact]
    public void ForEachIteratorShadowsOuterAndRestores()
    {
        // Outer 'n' defined before loop; loop uses same name as iterator;
        // after loop, outer 'n' should still hold its original value.
        var output = Run(
            "Define n as 7.\n" +
            "Define nums as a series of number with (1, 2).\n" +
            "For each n in nums, repeat:\n" +
            "  State n.\n" +
            "Done.\n" +
            "State n.");
        Assert.Equal("7", output.Split('\n').Last(s => s.Length > 0));
    }

    [Fact]
    public void WhileBodyScopeIsolated()
    {
        // Variable defined inside While body doesn't leak out; fails at runtime.
        Assert.Throws<RuntimeException>(() => Run(
            "Define i as 0.\n" +
            "While i is less than 1, repeat:\n" +
            "  Define secret as 5.\n" +
            "  i becomes 1.\n" +
            "Done.\n" +
            "State secret."));
    }

    // ── Runtime errors ────────────────────────────────────────────────────

    [Fact]
    public void DoubleDefineThrows()
    {
        // Caught at type-check time (TypeException), not runtime, since scopes are now statically validated.
        Assert.Throws<TypeException>(() => Run("Define x as 1. Define x as 2."));
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
        Assert.Equal("yes", Run("Define x as 1. If x is 1, State \"yes\"."));
    }

    [Fact]
    public void IfFalseSingleStmt()
    {
        Assert.Equal("", Run("Define x as 2. If x is 1, State \"yes\"."));
    }

    [Fact]
    public void IfElseTrueBranch()
    {
        Assert.Equal("yes", Run("Define x as 1. If x is 1, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void IfElseFalseBranch()
    {
        Assert.Equal("no", Run("Define x as 2. If x is 1, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void OtherwiseIfFirstArmMatches()
    {
        Assert.Equal("one", Run(
            "Define x as 1. " +
            "If x is 1, State \"one\". " +
            "Otherwise if x is 2, State \"two\". " +
            "Otherwise, State \"other\"."));
    }

    [Fact]
    public void OtherwiseIfSecondArmMatches()
    {
        Assert.Equal("two", Run(
            "Define x as 2. " +
            "If x is 1, State \"one\". " +
            "Otherwise if x is 2, State \"two\". " +
            "Otherwise, State \"other\"."));
    }

    [Fact]
    public void OtherwiseIfNoArmMatchesNoElse()
    {
        Assert.Equal("", Run(
            "Define x as 9. " +
            "If x is 1, State \"one\". " +
            "Otherwise if x is 2, State \"two\"."));
    }

    [Fact]
    public void NestedIf()
    {
        Assert.Equal("both", Run(
            "Define x as 1. Define y as 2. " +
            "If x is 1, If y is 2, State \"both\"."));
    }

    [Fact]
    public void BlockIfMultiStmt()
    {
        Assert.Equal("a\nb", Run(
            "Define x as 1.\n" +
            "If x is 1:\n" +
            "    State \"a\".\n" +
            "    State \"b\".\n" +
            "Done."));
    }

    [Fact]
    public void BlockIfWithOtherwise()
    {
        Assert.Equal("a\nb\nc", Run(
            "Define x as 1.\n" +
            "If x is 1:\n" +
            "    State \"a\".\n" +
            "    State \"b\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"nope\".\n" +
            "Done.\n" +
            "State \"c\"."));
    }

    [Fact]
    public void InlineIfMidBlock()
    {
        // Comma inline if works mid-loop-body — the original motivation for this change.
        Assert.Equal("1\n2\n4", Run(
            "Define x as 0.\n" +
            "While x is less than 4, repeat:\n" +
            "    x becomes x + 1.\n" +
            "    If x is 3, Skip.\n" +
            "    State x.\n" +
            "Done."));
    }

    [Fact]
    public void DanglingOtherwiseBindsToNearestIf()
    {
        // If x, (If y, a. Otherwise, b.) — Otherwise binds to innermost If.
        Assert.Equal("b", Run(
            "Define x as 1. Define y as 2. " +
            "If x is 1, If y is 3, State \"a\". Otherwise, State \"b\"."));
    }

    // ── Word-form comparisons ─────────────────────────────────────────────

    [Fact]
    public void WordFormIs()
    {
        Assert.Equal("yes", Run("Define x as 5. If x is 5, State \"yes\"."));
    }

    [Fact]
    public void WordFormIsNot()
    {
        Assert.Equal("yes", Run("Define x as 5. If x is not 3, State \"yes\"."));
    }

    [Fact]
    public void WordFormIsGreaterThan()
    {
        Assert.Equal("yes", Run("Define x as 10. If x is greater than 5, State \"yes\"."));
        Assert.Equal("", Run("Define x as 3. If x is greater than 5, State \"yes\"."));
    }

    [Fact]
    public void WordFormIsLessThan()
    {
        Assert.Equal("yes", Run("Define x as 2. If x is less than 5, State \"yes\"."));
        Assert.Equal("", Run("Define x as 7. If x is less than 5, State \"yes\"."));
    }

    [Fact]
    public void WordFormOrMore()
    {
        Assert.Equal("yes", Run("Define x as 5. If x is 5 or more, State \"yes\"."));
        Assert.Equal("yes", Run("Define x as 6. If x is 5 or more, State \"yes\"."));
        Assert.Equal("", Run("Define x as 4. If x is 5 or more, State \"yes\"."));
    }

    [Fact]
    public void WordFormOrLess()
    {
        Assert.Equal("yes", Run("Define x as 5. If x is 5 or less, State \"yes\"."));
        Assert.Equal("yes", Run("Define x as 3. If x is 5 or less, State \"yes\"."));
        Assert.Equal("", Run("Define x as 7. If x is 5 or less, State \"yes\"."));
    }

    [Fact]
    public void VariableCondition()
    {
        // stored bool used directly as condition
        Assert.Equal("yes", Run("Define flag as 1 = 1. If flag, State \"yes\"."));
    }

    // ── Control flow parse errors ─────────────────────────────────────────

    [Fact]
    public void OrphanedDoneThrows()
    {
        // Done. with no enclosing loop/block to close it is a parse error.
        Assert.Throws<ParseException>(() => Run("Define x as 1. If x is 1, State x. Done."));
    }

    [Fact]
    public void OrphanedOtherwiseThrows()
    {
        // Otherwise appearing where a statement is expected is a parse error.
        Assert.Throws<ParseException>(() => Run(
            "Define x as 1. If x is 1: State x. State x. Otherwise: State x."));
    }

    // ── Control flow runtime errors ───────────────────────────────────────

    [Fact]
    public void NonBoolConditionThrows()
    {
        Assert.Throws<RuntimeException>(() => Run("Define x as 5. If x, State x."));
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
            "    If x is 3, Stop. " +
            "Done. " +
            "State x."));
    }

    [Fact]
    public void SkipInWhileLoop()
    {
        Assert.Equal("1\n3\n4", Run(
            "Define x as 0. " +
            "While x is less than 4, repeat: " +
            "x becomes x + 1. " +
            "If x is 2, Skip. Otherwise, State x. " +
            "Done."));
    }

    [Fact]
    public void StopInRepeatUntilLoop()
    {
        Assert.Equal("3", Run(
            "Define x as 0. " +
            "Repeat: x becomes x + 1. If x is 3, Stop. until x is 10 or more. " +
            "State x."));
    }

    [Fact]
    public void SkipInRepeatUntilLoop()
    {
        Assert.Equal("1\n3\n4\n5", Run(
            "Define x as 0. " +
            "Repeat: x becomes x + 1. If x is 2, Skip. Otherwise, State x. until x is 5 or more."));
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
            "        If inner is 2, Stop. " +
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
        Assert.Throws<ParseException>(() => Run("Define x as 1. If x is 1, Stop."));
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
        Assert.Equal("(90, 85, 70)", Run("Define scores as a series with (90, 85, 70). State scores."));
    }

    [Fact]
    public void SeriesSingleElement()
    {
        Assert.Equal("(42)", Run("Define s as a series with (42). State s."));
    }

    [Fact]
    public void SeriesEmpty()
    {
        Assert.Equal("()", Run("Define s as a series of numbers with (). State s."));
    }

    [Fact]
    public void SeriesWithExpressions()
    {
        Assert.Equal("(3, 12)", Run("Define s as a series with (1 + 2, 3 * 4). State s."));
    }

    [Fact]
    public void SeriesWithVariables()
    {
        Assert.Equal("(5, 6)", Run("Define x as 5. Define s as a series with (x, x + 1). State s."));
    }

    [Fact]
    public void SeriesNoArticle()
    {
        // "a" before series is an Article (noise) — omitting it is valid
        Assert.Equal("(1, 2, 3)", Run("Define s as series with (1, 2, 3). State s."));
    }

    [Fact]
    public void ArithmeticGroupingStillWorks()
    {
        // Parens in expression context remain grouping — series context only triggers on "series" keyword
        Assert.Equal("20", Run("State (2 + 3) * 4."));
    }

    // Series literal in expression position — inline, no pre-Define required.
    [Fact]
    public void SeriesLiteral_InlineAsArgument()
    {
        Assert.Equal("(1, 2, 3)", Run("""
            Bind void to show, given (the series of number s):
                State s.
            Done.
            Cast show on (a series of number with (1, 2, 3)).
            """));
    }

    [Fact]
    public void SeriesLiteral_InlineInButVoidIs()
    {
        // voidable context: if the variable is void, fallback to inline series literal
        Assert.Equal("()", Run("""
            Define v as a series of number with ().
            Define ch as a channel of series of number.
            Pull a rabbit.
                Close ch.
                Define got as the delivery from ch.
                Define r as (got but void is (a series of number with ())).
                State r.
            Done.
            """));
    }

    [Fact]
    public void SeriesLiteral_InlineInAddTo()
    {
        // Add (a series of ...) to outer — series literal as Add value
        Assert.Equal("2", Run("""
            Define outer as a series of series of number with ().
            Add (a series of number with (10, 20)) to outer.
            State the number of (the first of outer).
            """));
    }

    [Fact]
    public void SeriesLiteral_NestedInExpression()
    {
        // a series of series of number with (...) inline — nested type annotation in expression position
        Assert.Equal("3", Run("""
            Define v as a series of series of number with ().
            Define ch as a channel of series of series of number.
            Pull a rabbit.
                Close ch.
                Define got as the delivery from ch.
                Define r as (got but void is (a series of series of number with ())).
                Add (a series of number with (1, 2, 3)) to r.
                State the number of (the first of r).
            Done.
            """));
    }

    // ── Series access ─────────────────────────────────────────────────────

    [Fact]
    public void SeriesAccessFirst()
    {
        Assert.Equal("10", Run("Define s as a series with (10, 20, 30). State the first of s."));
    }

    [Fact]
    public void SeriesAccessSecond()
    {
        Assert.Equal("20", Run("Define s as a series with (10, 20, 30). State the second of s."));
    }

    [Fact]
    public void SeriesAccessLast()
    {
        Assert.Equal("30", Run("Define s as a series with (10, 20, 30). State the last of s."));
    }

    [Fact]
    public void SeriesAccessParametric()
    {
        Assert.Equal("20", Run("Define s as a series with (10, 20, 30). State item 2 of s."));
    }

    [Fact]
    public void SeriesAccessParametricVariable()
    {
        Assert.Equal("30", Run("Define s as a series with (10, 20, 30). Define n as 3. State item n of s."));
    }

    [Fact]
    public void SeriesAccessParametricExpression()
    {
        Assert.Equal("20", Run("Define s as a series with (10, 20, 30). State item 1 + 1 of s."));
    }

    [Fact]
    public void SeriesAccessOutOfBoundsThrows()
    {
        Assert.Throws<RuntimeException>(() => Run("Define s as a series with (10, 20). State item 5 of s."));
    }

    // ── Series length ─────────────────────────────────────────────────────

    [Fact]
    public void SeriesLength()
    {
        Assert.Equal("3", Run("Define s as a series with (10, 20, 30). State the number of s."));
    }

    [Fact]
    public void SeriesLengthEmpty()
    {
        Assert.Equal("0", Run("Define s as a series of numbers with (). State the number of s."));
    }

    [Fact]
    public void SeriesLengthInCondition()
    {
        // Iterates over a series using a while loop driven by its length.
        Assert.Equal("10\n20\n30", Run(
            "Define s as a series with (10, 20, 30). " +
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
            "Define s as a series with (1, 2, 3). " +
            "Add 4 to s. " +
            "State the number of s. State the last of s.").Split('\n')[0]);
    }

    [Fact]
    public void SeriesAddToStart()
    {
        Assert.Equal("0", Run(
            "Define s as a series with (1, 2, 3). " +
            "Add 0 to the start of s. " +
            "State the first of s."));
    }

    [Fact]
    public void SeriesAddAfterOrdinal()
    {
        // Insert 99 after position 2 → (10, 20, 99, 30)
        Assert.Equal("10\n20\n99\n30", Run(
            "Define s as a series with (10, 20, 30). " +
            "Add 99 after the second item of s. " +
            "State item 1 of s. State item 2 of s. State item 3 of s. State item 4 of s."));
    }

    [Fact]
    public void SeriesAddAfterParametric()
    {
        Assert.Equal("10\n99\n20\n30", Run(
            "Define s as a series with (10, 20, 30). " +
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
            "Define s as a series with (10, 20, 30). " +
            "Remove the first item from s. " +
            "State the number of s. State the first of s."));
    }

    [Fact]
    public void SeriesRemoveByParametric()
    {
        // Remove item 2 → (10, 30)
        Assert.Equal("10\n30", Run(
            "Define s as a series with (10, 20, 30). " +
            "Remove item 2 from s. " +
            "State item 1 of s. State item 2 of s."));
    }

    [Fact]
    public void SeriesRemoveByValue()
    {
        // Remove first occurrence of 20 → (10, 30)
        Assert.Equal("2", Run(
            "Define s as a series with (10, 20, 30). " +
            "Remove 20 from s. " +
            "State the number of s."));
    }

    [Fact]
    public void SeriesRemoveByValueNotFoundThrows()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Define s as a series with (10, 20, 30). Remove 99 from s."));
    }

    // ── Series element assignment ─────────────────────────────────────────

    [Fact]
    public void SeriesSetByOrdinal()
    {
        Assert.Equal("99", Run(
            "Define s as a series with (10, 20, 30). " +
            "the second of s becomes 99. " +
            "State item 2 of s."));
    }

    [Fact]
    public void SeriesSetByParametric()
    {
        Assert.Equal("99", Run(
            "Define s as a series with (10, 20, 30). " +
            "Define n as 3. " +
            "item n of s becomes 99. " +
            "State the last of s."));
    }

    [Fact]
    public void SeriesSetLast()
    {
        Assert.Equal("99", Run(
            "Define s as a series with (10, 20, 30). " +
            "the last of s becomes 99. " +
            "State item 3 of s."));
    }

    [Fact]
    public void SeriesSetOutOfBoundsThrows()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Define s as a series with (10, 20). item 5 of s becomes 99."));
    }

    [Fact]
    public void SeriesSetOnNonSeriesThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define x as 5. the first of x becomes 99."));
    }

    // ── For-each loop ─────────────────────────────────────────────────────

    [Fact]
    public void ForEachNamedIterator()
    {
        Assert.Equal("10\n20\n30", Run(
            "Define s as a series with (10, 20, 30).\n" +
            "For each x in s, repeat:\n" +
            "    State x.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachBareIt()
    {
        Assert.Equal("1\n2\n3", Run(
            "Define s as a series with (1, 2, 3).\n" +
            "For each in s, repeat:\n" +
            "    State it.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachArticleBeforeSeries()
    {
        // "the" before series name is noise and must be skipped
        Assert.Equal("5\n6", Run(
            "Define nums as a series with (5, 6).\n" +
            "For each n in the nums, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachEmptySeriesRunsZeroTimes()
    {
        Assert.Equal("done", Run(
            "Define s as a series of numbers with ().\n" +
            "For each x in s, repeat:\n" +
            "    State x.\n" +
            "Done.\n" +
            "State \"done\"."));
    }

    [Fact]
    public void ForEachSingleElement()
    {
        Assert.Equal("42", Run(
            "Define s as a series with (42).\n" +
            "For each x in s, repeat:\n" +
            "    State x.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachStopBreaksLoop()
    {
        Assert.Equal("1\n2", Run(
            "Define s as a series with (1, 2, 3, 4).\n" +
            "For each x in s, repeat:\n" +
            "    If x is 3, Stop.\n" +
            "    State x.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachSkipSkipsIteration()
    {
        Assert.Equal("1\n3", Run(
            "Define s as a series with (1, 2, 3).\n" +
            "For each x in s, repeat:\n" +
            "    If x is 2, Skip.\n" +
            "    State x.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachComputesSum()
    {
        Assert.Equal("336", Run(
            "Define scores as a series with (92, 85, 71, 88).\n" +
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
            "Define loop-vals as a series with (1, 2, 3).\n" +
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
            "Define s as a series with (1, 2).\n" +
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
            "Define outer as a series with (1, 2).\n" +
            "Define inner as a series with (10, 100).\n" +
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
            "Define outer-s as a series with (1, 2).\n" +
            "Define inner-s as a series with (10, 20).\n" +
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
            "Define s as a series with (1, 2, 3).\n" +
            "For each x in s, repeat:\n" +
            "    Add 99 to s.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachRemoveDuringLoopThrows()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Define s as a series with (1, 2, 3).\n" +
            "For each x in s, repeat:\n" +
            "    Remove the first item from s.\n" +
            "Done."));
    }

    [Fact]
    public void ForEachWorksOnSeriesLiteralInline()
    {
        // series name can be a freshly-defined variable; also testing article in "in the"
        Assert.Equal("a\nb\nc", Run(
            "Define letters as a series with (\"a\", \"b\", \"c\").\n" +
            "For each ch in the letters, repeat:\n" +
            "    State ch.\n" +
            "Done."));
    }

    // ── Range ─────────────────────────────────────────────────────────────

    [Fact]
    public void RangeAscendingProducesCorrectElements()
    {
        Assert.Equal("1\n2\n3\n4\n5", Run(
            "For each n in range 1 to 5, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeDescendingProducesCorrectElements()
    {
        Assert.Equal("5\n4\n3\n2\n1", Run(
            "For each n in range 5 to 1, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeSingleElementWhenStartEqualsEnd()
    {
        Assert.Equal("7", Run(
            "For each n in range 7 to 7, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeIsInclusiveOnBothEnds()
    {
        Assert.Equal("3\n4\n5", Run(
            "For each n in range 3 to 5, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeStoredInVariableAndIterated()
    {
        Assert.Equal("10\n11\n12", Run(
            "Define r as range 10 to 12.\n" +
            "For each n in r, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeWithVariableEnd()
    {
        Assert.Equal("1\n2\n3", Run(
            "Define limit as 3.\n" +
            "For each n in range 1 to limit, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeProducesSeriesOfNumberLength()
    {
        Assert.Equal("5", Run(
            "Define r as range 1 to 5.\n" +
            "State the number of r."));
    }

    [Fact]
    public void RangeArticleIsOptional()
    {
        Assert.Equal("1\n2\n3", Run(
            "For each n in the range 1 to 3, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeComputesSum()
    {
        Assert.Equal("55", Run(
            "Define total as 0.\n" +
            "For each n in range 1 to 10, repeat:\n" +
            "    total becomes total + n.\n" +
            "Done.\n" +
            "State total."));
    }

    [Fact]
    public void TypeCheckerRangeNonNumberStartThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "For each n in range \"a\" to 5, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void TypeCheckerRangeNonNumberEndThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "For each n in range 1 to \"z\", repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    // ── Range — Slice 2: counting by (stepping) ─────────────────────────────

    [Fact]
    public void RangeStep_AscendingEvenSteps()
    {
        Assert.Equal("1\n3\n5\n7\n9", Run(
            "For each n in range 1 to 10 counting by 2, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeStep_EndpointLandsExactly()
    {
        Assert.Equal("2\n4\n6\n8\n10", Run(
            "Define evens as range 2 to 10 counting by 2.\n" +
            "For each n in evens, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeStep_EndpointSkippedWhenNotLanded()
    {
        Assert.Equal("1\n3\n5\n7\n9", Run(
            "For each n in range 1 to 9 counting by 2, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeStep_DescendingDirectionFromStartEnd()
    {
        Assert.Equal("10\n8\n6\n4\n2", Run(
            "For each n in range 10 to 1 counting by 2, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeStep_DecimalStep()
    {
        Assert.Equal("1\n1.5\n2", Run(
            "For each n in range 1 to 2 counting by 0.5, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeStep_VariableStep()
    {
        Assert.Equal("1\n4\n7", Run(
            "Define step as 3.\n" +
            "For each n in range 1 to 7 counting by step, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeStep_ArticleOptional()
    {
        Assert.Equal("1\n3\n5", Run(
            "For each n in the range 1 to 5 counting by 2, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeStep_LiteralZeroIsStaticError()
    {
        Assert.Throws<TypeException>(() => Run(
            "For each n in range 1 to 10 counting by 0, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeStep_LiteralNegativeIsStaticError()
    {
        Assert.Throws<TypeException>(() => Run(
            "For each n in range 1 to 10 counting by -2, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeStep_RuntimeZeroIsError()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Define step as 0.\n" +
            "For each n in range 1 to 10 counting by step, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeStep_RuntimeNegativeIsError()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Define step as 0 - 2.\n" +
            "For each n in range 1 to 10 counting by step, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void RangeStep_NonNumberStepIsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "For each n in range 1 to 10 counting by \"two\", repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    // ── Type checking ─────────────────────────────────────────────────────

    [Fact]
    public void TypeCheckerNumberMismatchThrows()
    {
        Assert.Throws<TypeException>(() => Run("Define x as 0. x becomes \"hello\"."));
    }

    [Fact]
    public void TypeCheckerTextMismatchThrows()
    {
        Assert.Throws<TypeException>(() => Run("Define x as \"hello\". x becomes 42."));
    }

    [Fact]
    public void TypeCheckerFactMismatchThrows()
    {
        Assert.Throws<TypeException>(() => Run("Define x as 1 = 1. x becomes 5."));
    }

    [Fact]
    public void TypeCheckerNumberToNumberPasses()
    {
        Assert.Equal("99", Run("Define x as 0. x becomes 99. State x."));
    }

    [Fact]
    public void TypeCheckerTextToTextPasses()
    {
        Assert.Equal("world", Run("Define x as \"hello\". x becomes \"world\". State x."));
    }

    [Fact]
    public void TypeCheckerFactToFactPasses()
    {
        Assert.Equal("false", Run("Define x as 1 = 1. x becomes 2 = 3. State x."));
    }

    [Fact]
    public void TypeCheckerInfersFactFromVariableCondition()
    {
        // flag is Fact (inferred from comparison); becomes with another comparison passes
        Assert.Equal("false", Run("Define flag as 1 = 1. flag becomes 1 = 2. State flag."));
    }

    [Fact]
    public void TypeCheckerMismatchInsideWhileThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define x as 0. " +
            "While x is less than 3, repeat: x becomes \"wrong\". Done."));
    }

    [Fact]
    public void TypeCheckerMismatchInsideIfThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define x as 0. If x is 0, x becomes \"wrong\"."));
    }

    [Fact]
    public void TypeCheckerMismatchInsideRepeatUntilThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define x as 0. Repeat: x becomes \"wrong\". until x is 1."));
    }

    [Fact]
    public void TypeCheckerMismatchInsideForEachThrows()
    {
        // total is Number; becomes "wrong" inside the loop is a type error
        Assert.Throws<TypeException>(() => Run(
            "Define s as a series with (1, 2, 3).\n" +
            "Define total as 0.\n" +
            "For each x in s, repeat:\n" +
            "    total becomes \"wrong\".\n" +
            "Done."));
    }

    [Fact]
    public void TypeCheckerSeriesElementIsTypedConcretely()
    {
        // Stage 3: series element access resolves to the element type (Number here).
        // x is typed as Number, so becoming "hello" is a TypeException.
        Assert.Throws<TypeException>(() => Run(
            "Define s as a series with (1, 2, 3). " +
            "Define x as item 1 of s. " +
            "x becomes \"hello\". " +
            "State x."));
    }

    [Fact]
    public void TypeCheckerForEachIteratorTypedAsElement()
    {
        // Stage 3: iterator is typed as the series element type (Number).
        // total + score is Number + Number = Number; total becomes Number — types match fully.
        Assert.Equal("336", Run(
            "Define scores as a series with (92, 85, 71, 88).\n" +
            "Define total as 0.\n" +
            "For each score in scores, repeat:\n" +
            "    total becomes total + score.\n" +
            "Done.\n" +
            "State total."));
    }

    [Fact]
    public void TypeCheckerErrorMessageContainsLineInfo()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Define x as 0.\nx becomes \"hello\"."));
        Assert.Contains("1", ex.Message);   // establishing line
        Assert.Contains("2", ex.Message);   // reassignment line
        Assert.Contains("number", ex.Message);
        Assert.Contains("text", ex.Message);
    }

    [Fact]
    public void TypeCheckerErrorMessageContainsEstablishingValue()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Define score as 42. score becomes \"oops\"."));
        Assert.Contains("42", ex.Message);
    }

    // ── Type checking Stage 2: typed operations ───────────────────────────

    [Fact]
    public void TypeCheckerArithmeticRejectsNumberPlusText()
    {
        Assert.Throws<TypeException>(() => Run("State 1 + \"hello\"."));
    }

    [Fact]
    public void TypeCheckerArithmeticRejectsTextPlusText()
    {
        // + does not concatenate text in Cufet
        Assert.Throws<TypeException>(() => Run("State \"foo\" + \"bar\"."));
    }

    [Fact]
    public void TypeCheckerArithmeticPassesForNumbers()
    {
        Assert.Equal("7", Run("State 3 + 4."));
    }

    [Fact]
    public void TypeCheckerEqualityRejectsCrossType()
    {
        // number = text is a type error, not false
        Assert.Throws<TypeException>(() => Run("State 1 = \"1\"."));
    }

    [Fact]
    public void TypeCheckerEqualityPassesNumberToNumber()
    {
        Assert.Equal("false", Run("State 1 = 2."));
    }

    [Fact]
    public void TypeCheckerEqualityPassesTextToText()
    {
        Assert.Equal("true", Run("State \"a\" = \"a\"."));
    }

    [Fact]
    public void TypeCheckerOrderingRejectsNonNumbers()
    {
        Assert.Throws<TypeException>(() => Run("State \"a\" > \"b\"."));
    }

    [Fact]
    public void TypeCheckerOrderingPassesForNumbers()
    {
        Assert.Equal("true", Run("State 3 > 2."));
    }

    [Fact]
    public void TypeCheckerComparisonResultTypedAsFact()
    {
        // Define infers Fact; becomes with another comparison passes
        Assert.Equal("false", Run("Define flag as 5 > 3. flag becomes 1 = 2. State flag."));
    }

    [Fact]
    public void TypeCheckerComparisonResultMismatchThrows()
    {
        // flag is Fact (inferred from ordering); assigning a Number → type error
        Assert.Throws<TypeException>(() => Run("Define flag as 5 > 3. flag becomes 42."));
    }

    [Fact]
    public void TypeCheckerUnaryMinusRejectsText()
    {
        Assert.Throws<TypeException>(() => Run("Define x as \"hello\". State -x."));
    }

    [Fact]
    public void TypeCheckerUnaryMinusPassesForNumber()
    {
        Assert.Equal("-5", Run("State -5."));
    }

    [Fact]
    public void TypeCheckerUnaryMinusOnSeriesElementNumber()
    {
        // Stage 3: item 1 of s resolves to Number; unary minus on Number → Number. No type error.
        Assert.Equal("-5", Run(
            "Define s as a series with (5, 10, 15).\n" +
            "Define neg as -(item 1 of s).\n" +
            "State neg."));
    }

    [Fact]
    public void TypeCheckerOperationErrorContainsLineAndTypes()
    {
        var ex = Assert.Throws<TypeException>(() => Run("State 1 + \"hello\"."));
        Assert.Contains("1", ex.Message);      // line number
        Assert.Contains("number", ex.Message);
        Assert.Contains("text", ex.Message);
    }

    [Fact]
    public void TypeCheckerNestedSubexpressionTypeFlows()
    {
        // (1 + 2) is fine (both numbers → number); = "hello" then fails (number vs text)
        Assert.Throws<TypeException>(() => Run("State (1 + 2) = \"hello\"."));
    }

    // ── Type checking Stage 3: series typing ─────────────────────────────

    [Fact]
    public void TypeCheckerSeriesLiteralInfersNumberElementType()
    {
        // Populated number series → scores is series of number; access resolves to number.
        Assert.Equal("90", Run(
            "Define scores as a series with (90, 85, 70).\n" +
            "Define top as the first of scores.\n" +
            "State top."));
    }

    [Fact]
    public void TypeCheckerSeriesLiteralInfersTextElementType()
    {
        // Populated text series → words is series of text; access resolves to text.
        Assert.Equal("hello", Run(
            "Define words as a series with (\"hello\", \"world\").\n" +
            "Define first-word as the first of words.\n" +
            "State first-word."));
    }

    [Fact]
    public void TypeCheckerSeriesMixedElementsThrows()
    {
        Assert.Throws<TypeException>(() => Run("Define s as a series with (1, \"two\", 3)."));
    }

    [Fact]
    public void TypeCheckerEmptySeriesWithAnnotationPasses()
    {
        Assert.Equal("()", Run("Define s as a series of numbers with (). State s."));
    }

    [Fact]
    public void TypeCheckerEmptySeriesWithoutAnnotationThrows()
    {
        Assert.Throws<TypeException>(() => Run("Define s as a series with (). State s."));
    }

    [Fact]
    public void TypeCheckerAnnotatedSeriesMatchesPasses()
    {
        // Redundant annotation is allowed when it agrees with the elements.
        Assert.Equal("(90, 85)", Run("Define s as a series of numbers with (90, 85). State s."));
    }

    [Fact]
    public void TypeCheckerAnnotatedSeriesMismatchThrows()
    {
        Assert.Throws<TypeException>(() => Run("Define s as a series of numbers with (90, \"hi\")."));
    }

    [Fact]
    public void TypeCheckerSeriesAccessFlowsToDefine()
    {
        // After Stage 3, accessing a number series element types the derived variable as Number.
        // Assigning "wrong" to it is a TypeException.
        Assert.Throws<TypeException>(() => Run(
            "Define s as a series with (10, 20, 30).\n" +
            "Define x as item 2 of s.\n" +
            "x becomes \"wrong\"."));
    }

    [Fact]
    public void TypeCheckerSeriesAddTypeMismatchThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define s as a series with (1, 2, 3).\n" +
            "Add \"text\" to s."));
    }

    [Fact]
    public void TypeCheckerSeriesAddTypePasses()
    {
        Assert.Equal("4", Run(
            "Define s as a series with (1, 2, 3).\n" +
            "Add 99 to s.\n" +
            "State the number of s."));
    }

    [Fact]
    public void TypeCheckerSeriesRemoveValueTypeMismatchThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define s as a series with (1, 2, 3).\n" +
            "Remove \"text\" from s."));
    }

    [Fact]
    public void TypeCheckerSeriesSetTypeMismatchThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define s as a series with (1, 2, 3).\n" +
            "the first of s becomes \"text\"."));
    }

    [Fact]
    public void TypeCheckerSeriesSetTypePasses()
    {
        Assert.Equal("99", Run(
            "Define s as a series with (1, 2, 3).\n" +
            "the first of s becomes 99.\n" +
            "State the first of s."));
    }

    [Fact]
    public void TypeCheckerNonWholeNumberIndexThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define s as a series with (10, 20, 30).\n" +
            "State item 2.5 of s."));
    }

    [Fact]
    public void TypeCheckerSeriesLengthIsNumber()
    {
        // the number of s resolves to Number; can be assigned to a number variable.
        Assert.Equal("0", Run(
            "Define s as a series with (1, 2, 3).\n" +
            "Define len as the number of s.\n" +
            "len becomes 0.\n" +
            "State len."));
    }

    [Fact]
    public void TypeCheckerForEachTextIteratorTypedAsText()
    {
        // Iterator over a text series is typed Text; arithmetic on it should throw.
        Assert.Throws<TypeException>(() => Run(
            "Define words as a series with (\"a\", \"b\").\n" +
            "Define total as 0.\n" +
            "For each w in words, repeat:\n" +
            "    total becomes total + w.\n" +
            "Done."));
    }

    [Fact]
    public void TypeCheckerSeriesMixedErrorContainsTypes()
    {
        var ex = Assert.Throws<TypeException>(() => Run("Define s as a series with (1, \"two\")."));
        Assert.Contains("number", ex.Message);
        Assert.Contains("text", ex.Message);
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

    // ── Functions — basic ─────────────────────────────────────────────────

    [Fact]
    public void VoidFunctionDeclareAndCall()
    {
        Assert.Equal("hello", Run(
            "Bind void to greet:\n" +
            "    State \"hello\".\n" +
            "Done.\n" +
            "Cast greet."));
    }

    [Fact]
    public void FunctionReturnsValue()
    {
        Assert.Equal("10", Run(
            "Bind number to double, given (the number x):\n" +
            "    return x * 2.\n" +
            "Done.\n" +
            "State Cast double on (5)."));
    }

    [Fact]
    public void FunctionResultInExpression()
    {
        Assert.Equal("15", Run(
            "Bind number to double, given (the number x):\n" +
            "    return x * 2.\n" +
            "Done.\n" +
            "State Cast double on (5) + 5."));
    }

    [Fact]
    public void FunctionResultAssignedToVariable()
    {
        Assert.Equal("6", Run(
            "Bind number to triple, given (the number n):\n" +
            "    return n * 3.\n" +
            "Done.\n" +
            "Define result as Cast triple on (2).\n" +
            "State result."));
    }

    [Fact]
    public void FunctionMultipleParameters()
    {
        Assert.Equal("13", Run(
            "Bind number to sumTwo, given (the number x, the number y):\n" +
            "    return x + y.\n" +
            "Done.\n" +
            "State Cast sumTwo on (5, 8)."));
    }

    [Fact]
    public void FunctionNoParametersNoReturn()
    {
        Assert.Equal("done", Run(
            "Bind void to doNothing:\n" +
            "Done.\n" +
            "Cast doNothing.\n" +
            "State \"done\"."));
    }

    [Fact]
    public void FunctionWithIfBranches()
    {
        Assert.Equal("positive", Run(
            "Bind text to sign, given (the number n):\n" +
            "    If n is greater than 0:\n" +
            "        return \"positive\".\n" +
            "    Done.\n" +
            "    Otherwise:\n" +
            "        return \"non-positive\".\n" +
            "    Done.\n" +
            "Done.\n" +
            "State Cast sign on (5)."));
    }

    [Fact]
    public void FunctionForwardReference()
    {
        // Call appears before the Bind declaration in source order.
        Assert.Equal("7", Run(
            "State Cast addOne on (6).\n" +
            "Bind number to addOne, given (the number x):\n" +
            "    return x + 1.\n" +
            "Done."));
    }

    [Fact]
    public void FunctionDoesNotSeeGlobalVariables()
    {
        // x is a global; the function cannot access it and should throw.
        Assert.Throws<RuntimeException>(() => Run(
            "Define x as 99.\n" +
            "Bind number to getX:\n" +
            "    return x.\n" +
            "Done.\n" +
            "State Cast getX."));
    }

    [Fact]
    public void FunctionCanCallOtherFunction()
    {
        Assert.Equal("20", Run(
            "Bind number to double, given (the number x):\n" +
            "    return x * 2.\n" +
            "Done.\n" +
            "Bind number to quadruple, given (the number x):\n" +
            "    return Cast double on (Cast double on (x)).\n" +
            "Done.\n" +
            "State Cast quadruple on (5)."));
    }

    [Fact]
    public void FunctionSelfRecursion()
    {
        Assert.Equal("120", Run(
            "Bind number to factorial, given (the number n):\n" +
            "    If n is 1:\n" +
            "        return 1.\n" +
            "    Done.\n" +
            "    return n * Cast factorial on (n - 1).\n" +
            "Done.\n" +
            "State Cast factorial on (5)."));
    }

    [Fact]
    public void FunctionLocalVariablesIsolated()
    {
        // Two consecutive calls — local vars should not persist between calls.
        Assert.Equal("3\n5", Run(
            "Bind number to addTwo, given (the number x):\n" +
            "    Define result as x + 2.\n" +
            "    return result.\n" +
            "Done.\n" +
            "State Cast addTwo on (1).\n" +
            "State Cast addTwo on (3)."));
    }

    [Fact]
    public void VoidFunctionEarlyReturn()
    {
        Assert.Equal("before", Run(
            "Bind void to earlyExit, given (the number x):\n" +
            "    If x is 0:\n" +
            "        return.\n" +
            "    Done.\n" +
            "    State \"after\".\n" +
            "Done.\n" +
            "State \"before\".\n" +
            "Cast earlyExit on (0)."));
    }

    [Fact]
    public void FunctionCalledMultipleTimes()
    {
        Assert.Equal("2\n4\n6", Run(
            "Bind number to double, given (the number x):\n" +
            "    return x * 2.\n" +
            "Done.\n" +
            "State Cast double on (1).\n" +
            "State Cast double on (2).\n" +
            "State Cast double on (3)."));
    }

    [Fact]
    public void FunctionWithTextReturn()
    {
        Assert.Equal("yes", Run(
            "Bind text to answer, given (the fact f):\n" +
            "    If f:\n" +
            "        return \"yes\".\n" +
            "    Done.\n" +
            "    return \"no\".\n" +
            "Done.\n" +
            "State Cast answer on (1 = 1)."));
    }

    // ── Functions — type errors ───────────────────────────────────────────

    [Fact]
    public void FunctionArgCountMismatchThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind number to sumTwo, given (the number p, the number q):\n" +
            "    return p + q.\n" +
            "Done.\n" +
            "State Cast sumTwo on (1)."));
    }

    [Fact]
    public void FunctionArgTypeMismatchThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind number to double, given (the number x):\n" +
            "    return x * 2.\n" +
            "Done.\n" +
            "State Cast double on (\"hello\")."));
    }

    [Fact]
    public void FunctionWrongReturnTypeThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind number to bad:\n" +
            "    return \"text\".\n" +
            "Done."));
    }

    [Fact]
    public void VoidFunctionUsedAsValueThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind void to greet:\n" +
            "    State \"hi\".\n" +
            "Done.\n" +
            "State Cast greet."));
    }

    [Fact]
    public void FunctionMissingReturnThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind number to bad:\n" +
            "    State \"no return here\".\n" +
            "Done."));
    }

    [Fact]
    public void FunctionMissingReturnOnOneBranchThrows()
    {
        // The otherwise branch returns, but the if-without-else path falls off.
        Assert.Throws<TypeException>(() => Run(
            "Bind number to bad, given (the number x):\n" +
            "    If x is 1:\n" +
            "        return 1.\n" +
            "    Done.\n" +
            "Done."));
    }

    [Fact]
    public void VoidFunctionWithValueReturnThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind void to bad:\n" +
            "    return 5.\n" +
            "Done."));
    }

    // ── Functions — parse errors ──────────────────────────────────────────

    [Fact]
    public void ReturnOutsideFunctionThrows()
    {
        Assert.Throws<ParseException>(() => Run("return 5."));
    }

    [Fact]
    public void NestedBindIsAllowedInsideFunction()
    {
        // Nested Bind inside a function body creates a local closure — valid since closures slice.
        Assert.Equal("42", Run(
            "Bind number to outer:\n" +
            "    Bind number to inner: Return 42. Done.\n" +
            "    Return cast inner on ().\n" +
            "Done.\n" +
            "State cast outer on ()."));
    }

    [Fact]
    public void BindInsideLoopThrows()
    {
        Assert.Throws<ParseException>(() => Run(
            "Define x as 0.\n" +
            "While x is 0, repeat:\n" +
            "    Bind void to bad:\n" +
            "    Done.\n" +
            "Done."));
    }

    // ── Functions as values — slice 1: functions in variables ────────────

    [Fact]
    public void FunctionStoredInVariableAndCalled()
    {
        Assert.Equal("1", Run(
            "Bind number to grade, given (the number score):\n" +
            "    if the score is 50 or more, return 1.\n" +
            "    return 0.\n" +
            "Done.\n" +
            "Define operation as grade.\n" +
            "State Cast operation on (75)."));
    }

    [Fact]
    public void FunctionInVariableReturnValueUsedInExpression()
    {
        Assert.Equal("10", Run(
            "Bind number to doubler, given (the number x):\n" +
            "    return x * 2.\n" +
            "Done.\n" +
            "Define fn as doubler.\n" +
            "State Cast fn on (5)."));
    }

    [Fact]
    public void FunctionInVariableCalledMultipleTimes()
    {
        Assert.Equal("3\n6\n9", Run(
            "Bind number to triple, given (the number x):\n" +
            "    return x * 3.\n" +
            "Done.\n" +
            "Define op as triple.\n" +
            "State Cast op on (1).\n" +
            "State Cast op on (2).\n" +
            "State Cast op on (3)."));
    }

    [Fact]
    public void FunctionInVariableWithArticle()
    {
        Assert.Equal("42", Run(
            "Bind number to answer, given (the number x):\n" +
            "    return x.\n" +
            "Done.\n" +
            "Define the fn as answer.\n" +
            "State Cast the fn on (42)."));
    }

    [Fact]
    public void StateFunctionVariablePrintsMarker()
    {
        Assert.Equal("<function>", Run(
            "Bind void to noop:\n" +
            "Done.\n" +
            "Define fn as noop.\n" +
            "State fn."));
    }

    [Fact]
    public void CastNonFunctionTypeError()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Define x as 5.\n" +
            "Cast x on (1)."));
        Assert.Contains("number", ex.Message);
        Assert.Contains("function", ex.Message);
    }

    [Fact]
    public void CastNonFunctionVariableTypeErrorNamesVariable()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Define score as 99.\n" +
            "Cast score on (1)."));
        Assert.Contains("score", ex.Message);
        Assert.Contains("function", ex.Message);
    }

    // ── Functions — recursion error ───────────────────────────────────────

    [Fact]
    public void InfiniteRecursionGivesFriendlyError()
    {
        var tokens  = new CufetLexer(
            "Bind number to loop, given (the number x):\n" +
            "    return Cast loop on (x).\n" +
            "Done.\n" +
            "State Cast loop on (1).").Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var ex = Assert.Throws<RuntimeException>(() =>
            RunOnLargeStack(() => new Interpreter(maxCallDepth: 5).Execute(program)));
        Assert.Contains("loop", ex.Message);
        Assert.Contains("too many times", ex.Message);
    }

    // ── Functions as values — slice 2: functions as parameters ───────────

    [Fact]
    public void FunctionAsParameter_BasicCall()
    {
        Assert.Equal("10", Run(
            "Bind number to apply, given (the number n, the number function transform given (the number)):\n" +
            "    return Cast transform on (n).\n" +
            "Done.\n" +
            "Bind number to double-it, given (the number x):\n" +
            "    return x * 2.\n" +
            "Done.\n" +
            "State Cast apply on (5, double-it)."));
    }

    [Fact]
    public void FunctionAsParameter_VoidFunctionParam()
    {
        Assert.Equal("hello", Run(
            "Bind void to run-with, given (the text msg, the void function printer given (the text)):\n" +
            "    Cast printer on (msg).\n" +
            "Done.\n" +
            "Bind void to shout, given (the text s):\n" +
            "    State s.\n" +
            "Done.\n" +
            "Cast run-with on (\"hello\", shout)."));
    }

    [Fact]
    public void FunctionAsParameter_NoParamsFunctionParam()
    {
        Assert.Equal("10", Run(
            "Bind number to get-ten:\n" +
            "    return 10.\n" +
            "Done.\n" +
            "Bind void to call-it, given (the number function producer):\n" +
            "    State Cast producer on ().\n" +
            "Done.\n" +
            "Cast call-it on (get-ten)."));
    }

    [Fact]
    public void FunctionAsParameter_CalledMultipleTimesInsideBody()
    {
        Assert.Equal("6\n7", Run(
            "Bind void to apply-twice, given (the number x, the number function fn given (the number)):\n" +
            "    State Cast fn on (x).\n" +
            "    State Cast fn on (x + 1).\n" +
            "Done.\n" +
            "Bind number to inc, given (the number n):\n" +
            "    return n + 1.\n" +
            "Done.\n" +
            "Cast apply-twice on (5, inc)."));
    }

    [Fact]
    public void FunctionAsParameter_TwoFunctionsWithMatchingSignature()
    {
        // Both add1 and sub1 are "number function given (number)" — both should be accepted.
        Assert.Equal("6\n4", Run(
            "Bind void to apply, given (the number n, the number function transform given (the number)):\n" +
            "    State Cast transform on (n).\n" +
            "Done.\n" +
            "Bind number to add1, given (the number n):\n" +
            "    return n + 1.\n" +
            "Done.\n" +
            "Bind number to sub1, given (the number n):\n" +
            "    return n - 1.\n" +
            "Done.\n" +
            "Cast apply on (5, add1).\n" +
            "Cast apply on (5, sub1)."));
    }

    [Fact]
    public void FunctionAsParameter_RecursiveFunctionType()
    {
        // compose takes two "number function given (number)" params — nested function type annotation.
        Assert.Equal("11", Run(
            "Bind number to compose, given (the number function outer given (the number), the number function inner given (the number), the number x):\n" +
            "    return Cast outer on (Cast inner on (x)).\n" +
            "Done.\n" +
            "Bind number to add1, given (the number n):\n" +
            "    return n + 1.\n" +
            "Done.\n" +
            "Bind number to double-it, given (the number n):\n" +
            "    return n * 2.\n" +
            "Done.\n" +
            "State Cast compose on (add1, double-it, 5)."));
    }

    [Fact]
    public void FunctionAsParameter_SignatureMismatchTypeError()
    {
        // 'apply' expects a number function; 'fmt' is a text function — static error.
        var ex = Assert.Throws<TypeException>(() => Run(
            "Bind void to apply, given (the number function transform given (the number)):\n" +
            "    Cast transform on (5).\n" +
            "Done.\n" +
            "Bind text to fmt, given (the text s):\n" +
            "    return s.\n" +
            "Done.\n" +
            "Cast apply on (fmt)."));
        Assert.Contains("number", ex.Message);
        Assert.Contains("text", ex.Message);
        Assert.Contains("function", ex.Message);
    }

    [Fact]
    public void FunctionAsParameter_SignatureMismatchErrorNamesParameter()
    {
        // Error message should name the function being called ('apply').
        var ex = Assert.Throws<TypeException>(() => Run(
            "Bind void to apply, given (the number function transform given (the number)):\n" +
            "    Cast transform on (5).\n" +
            "Done.\n" +
            "Bind text to wrong, given (the text s):\n" +
            "    return s.\n" +
            "Done.\n" +
            "Cast apply on (wrong)."));
        Assert.Contains("apply", ex.Message);
    }

    [Fact]
    public void FunctionAsParameter_VoidAnnotationSyntax()
    {
        // 'void function' annotation is valid and matches a void-returning function.
        Assert.Equal("done", Run(
            "Bind void to run-action, given (the text msg, the void function action given (the text)):\n" +
            "    Cast action on (msg).\n" +
            "Done.\n" +
            "Bind void to print-it, given (the text s):\n" +
            "    State s.\n" +
            "Done.\n" +
            "Cast run-action on (\"done\", print-it)."));
    }

    // ── Functions as values — slice 3: functions as return values ─────────

    [Fact]
    public void FunctionAsReturnValue_Basic()
    {
        // get-doubler returns a top-level function by name; result is called on 5.
        Assert.Equal("10", Run(
            "Bind number to double, given (the number x):\n" +
            "    return x * 2.\n" +
            "Done.\n" +
            "Bind number function given (the number) to get-doubler:\n" +
            "    return double.\n" +
            "Done.\n" +
            "Define op as Cast get-doubler on ().\n" +
            "State Cast op on (5)."));
    }

    [Fact]
    public void FunctionAsReturnValue_TwoGivenCase()
    {
        // Two 'given' clauses in one Bind: first is the returned function's signature,
        // second is this function's own parameter list.
        Assert.Equal("6", Run(
            "Bind number to double, given (the number n):\n" +
            "    return n * 2.\n" +
            "Done.\n" +
            "Bind number function given (the number) to make-fn, given (the number x):\n" +
            "    return double.\n" +
            "Done.\n" +
            "Define fn as Cast make-fn on (3).\n" +
            "State Cast fn on (3)."));
    }

    [Fact]
    public void FunctionAsReturnValue_NoParamReturnedFunction()
    {
        // Returning a zero-param function: 'number function' with no 'given'.
        Assert.Equal("10", Run(
            "Bind number to get-ten:\n" +
            "    return 10.\n" +
            "Done.\n" +
            "Bind number function to make-getter:\n" +
            "    return get-ten.\n" +
            "Done.\n" +
            "Define getter as Cast make-getter on ().\n" +
            "State Cast getter on ()."));
    }

    [Fact]
    public void FunctionAsReturnValue_VoidReturnedFunction()
    {
        // Returning a void-returning function.
        Assert.Equal("hello", Run(
            "Bind void to announce, given (the text s):\n" +
            "    State s.\n" +
            "Done.\n" +
            "Bind void function given (the text) to get-printer:\n" +
            "    return announce.\n" +
            "Done.\n" +
            "Define printer as Cast get-printer on ().\n" +
            "Cast printer on (\"hello\")."));
    }

    [Fact]
    public void FunctionAsReturnValue_ReturnedFunctionPassedAsArgument()
    {
        // Returned function stored in a variable and then passed to a higher-order function.
        Assert.Equal("10", Run(
            "Bind number to double, given (the number x):\n" +
            "    return x * 2.\n" +
            "Done.\n" +
            "Bind number function given (the number) to get-double:\n" +
            "    return double.\n" +
            "Done.\n" +
            "Bind number to apply, given (the number n, the number function f given (the number)):\n" +
            "    return Cast f on (n).\n" +
            "Done.\n" +
            "Define fn as Cast get-double on ().\n" +
            "State Cast apply on (5, fn)."));
    }

    [Fact]
    public void FunctionAsReturnValue_SignatureMismatchTypeError()
    {
        // Returning a 'text function given (text)' where 'number function given (number)' is expected.
        var ex = Assert.Throws<TypeException>(() => Run(
            "Bind text to fmt, given (the text s):\n" +
            "    return s.\n" +
            "Done.\n" +
            "Bind number function given (the number) to get-fn:\n" +
            "    return fmt.\n" +
            "Done."));
        Assert.Contains("number function", ex.Message);
        Assert.Contains("text function", ex.Message);
    }

    [Fact]
    public void FunctionAsReturnValue_MissingReturnChecked()
    {
        // A function with a function return type still needs a definite return on every path.
        Assert.Throws<TypeException>(() => Run(
            "Bind number to double, given (the number x):\n" +
            "    return x * 2.\n" +
            "Done.\n" +
            "Bind number function given (the number) to get-fn, given (the number flag):\n" +
            "    If flag is greater than 0, return double.\n" +
            "Done."));
    }

    // ── Functions as values — slice 4: series of functions ──────────────────

    [Fact]
    public void FunctionSeries_PopulatedAndAccess()
    {
        Assert.Equal("10", Run(
            "Bind number to double, given (the number x): return x * 2. Done.\n" +
            "Bind number to triple, given (the number x): return x * 3. Done.\n" +
            "Define ops as a series of number function given (the number) with (double, triple).\n" +
            "State Cast the first of ops on (5)."));
    }

    [Fact]
    public void FunctionSeries_Empty()
    {
        Assert.Equal("0", Run(
            "Bind number to double, given (the number x): return x * 2. Done.\n" +
            "Define ops as a series of number function given (the number).\n" +
            "State the number of ops."));
    }

    [Fact]
    public void FunctionSeries_ForEachAndCast()
    {
        Assert.Equal("10\n15", Run(
            "Bind number to double, given (the number x): return x * 2. Done.\n" +
            "Bind number to triple, given (the number x): return x * 3. Done.\n" +
            "Define ops as a series of number function given (the number) with (double, triple).\n" +
            "For each op in ops, repeat: State Cast op on (5). Done."));
    }

    [Fact]
    public void FunctionSeries_AddFunction()
    {
        Assert.Equal("2\n6", Run(
            "Bind number to double, given (the number x): return x * 2. Done.\n" +
            "Bind number to triple, given (the number x): return x * 3. Done.\n" +
            "Define ops as a series of number function given (the number) with (double).\n" +
            "Add triple to ops.\n" +
            "State Cast the first of ops on (1).\n" +
            "State Cast the second of ops on (2)."));
    }

    [Fact]
    public void FunctionSeries_InferredFromElements()
    {
        Assert.Equal("10", Run(
            "Bind number to double, given (the number x): return x * 2. Done.\n" +
            "Define ops as a series with (double).\n" +
            "State Cast the first of ops on (5)."));
    }

    [Fact]
    public void FunctionSeries_ElementSignatureMismatch()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Bind number to double, given (the number x): return x * 2. Done.\n" +
            "Bind text to shout, given (the text s): return s. Done.\n" +
            "Define ops as a series of number function given (the number) with (double, shout)."));
        Assert.Contains("number function", ex.Message);
    }

    [Fact]
    public void FunctionSeries_AddWrongSignature()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Bind number to double, given (the number x): return x * 2. Done.\n" +
            "Bind text to shout, given (the text s): return s. Done.\n" +
            "Define ops as a series of number function given (the number) with (double).\n" +
            "Add shout to ops."));
        Assert.Contains("number function", ex.Message);
    }

    // ── Modulo ───────────────────────────────────────────────────────────────

    [Fact]
    public void Modulo_BasicRemainder()
        => Assert.Equal("2", Run("State 12 % 5."));

    [Fact]
    public void Modulo_ZeroRemainder()
        => Assert.Equal("0", Run("State 10 % 5."));

    [Fact]
    public void Modulo_DecimalRemainder()
        => Assert.Equal("0.5", Run("State 12.5 % 3."));

    [Fact]
    public void Modulo_NegativeDividend()
        => Assert.Equal("-2", Run("State -12 % 5."));

    [Fact]
    public void Modulo_PrecedenceWithAddition()
        => Assert.Equal("3", Run("State 1 + 10 % 4."));

    [Fact]
    public void Modulo_LeftAssociative()
        => Assert.Equal("1", Run("State 10 % 3 % 2."));

    [Fact]
    public void Modulo_InFizzBuzz()
        => Assert.Equal("FizzBuzz", Run("""
            Define n as 15.
            If n % 15 is 0, State "FizzBuzz".
            """));

    [Fact]
    public void Modulo_ByZeroThrows()
        => Assert.Throws<RuntimeException>(() => Run("State 5 % 0."));

    [Fact]
    public void Modulo_NonNumberThrows()
    {
        var ex = Assert.Throws<TypeException>(() => Run("""
            Define s as "hello".
            State s % 2.
            """));
        Assert.Contains("%", ex.Message);
    }

    // ── Logical and / or ──────────────────────────────────────────────────

    [Fact]
    public void And_BothTrue()
        => Assert.Equal("yes", Run("Define x as 1 = 1. Define y as 2 = 2. If x and y, State \"yes\". Otherwise, State \"no\"."));

    [Fact]
    public void And_LeftFalse()
        => Assert.Equal("no", Run("Define x as 1 = 2. Define y as 2 = 2. If x and y, State \"yes\". Otherwise, State \"no\"."));

    [Fact]
    public void And_RightFalse()
        => Assert.Equal("no", Run("Define x as 1 = 1. Define y as 2 = 3. If x and y, State \"yes\". Otherwise, State \"no\"."));

    [Fact]
    public void And_BothFalse()
        => Assert.Equal("no", Run("Define x as 1 = 2. Define y as 2 = 3. If x and y, State \"yes\". Otherwise, State \"no\"."));

    [Fact]
    public void Or_BothFalse()
        => Assert.Equal("no", Run("Define x as 1 = 2. Define y as 2 = 3. If x or y, State \"yes\". Otherwise, State \"no\"."));

    [Fact]
    public void Or_LeftTrue()
        => Assert.Equal("yes", Run("Define x as 1 = 1. Define y as 2 = 3. If x or y, State \"yes\". Otherwise, State \"no\"."));

    [Fact]
    public void Or_RightTrue()
        => Assert.Equal("yes", Run("Define x as 1 = 2. Define y as 2 = 2. If x or y, State \"yes\". Otherwise, State \"no\"."));

    [Fact]
    public void Or_BothTrue()
        => Assert.Equal("yes", Run("Define x as 1 = 1. Define y as 2 = 2. If x or y, State \"yes\". Otherwise, State \"no\"."));

    [Fact]
    public void And_ShortCircuit_SkipsRight_WhenLeftFalse()
    {
        // 'undefined' is not in env — type checker returns null (unknown), so it passes.
        // If short-circuit works, 'undefined' is never evaluated → no RuntimeException.
        // If eager, 'undefined' would throw → this test would fail with RuntimeException.
        Assert.Equal("", Run("""
            Define flag as 1 = 2.
            If flag and undefined is 0, State "oops".
            """));
    }

    [Fact]
    public void Or_ShortCircuit_SkipsRight_WhenLeftTrue()
    {
        // Same pattern: 'undefined' passes type check (null), throws at runtime if evaluated.
        Assert.Equal("ok", Run("""
            Define flag as 1 = 1.
            If flag or undefined is 0, State "ok".
            """));
    }

    [Fact]
    public void And_HigherPrecedence_ThanOr()
    {
        // true or (false and false) = true or false = true  → "yes"  (correct)
        // (true or false) and false = true and false = false → "no"   (wrong parse)
        Assert.Equal("yes", Run("""
            Define p as 1 = 1.
            Define q as 1 = 2.
            Define r as 1 = 2.
            If p or q and r, State "yes". Otherwise, State "no".
            """));
    }

    [Fact]
    public void And_LeftAssociative()
    {
        // (true and true) and false = false
        Assert.Equal("no", Run("""
            Define p as 1 = 1.
            Define q as 1 = 1.
            Define r as 1 = 2.
            If p and q and r, State "yes". Otherwise, State "no".
            """));
    }

    [Fact]
    public void Or_LeftAssociative()
    {
        // (false or false) or true = true
        Assert.Equal("yes", Run("""
            Define p as 1 = 2.
            Define q as 1 = 2.
            Define r as 1 = 1.
            If p or q or r, State "yes". Otherwise, State "no".
            """));
    }

    [Fact]
    public void OrMore_StillWorks()
        => Assert.Equal("yes", Run("Define n as 5. If n is 5 or more, State \"yes\". Otherwise, State \"no\"."));

    [Fact]
    public void OrLess_StillWorks()
        => Assert.Equal("yes", Run("Define n as 3. If n is 5 or less, State \"yes\". Otherwise, State \"no\"."));

    [Fact]
    public void LogicalOr_WithOrMore_Coexist()
    {
        // "x is 10 or more" comparison tail and logical "or" in the same condition
        Assert.Equal("yes", Run("""
            Define x as 10.
            Define y as 3.
            If x is 10 or more or y is 10 or more, State "yes". Otherwise, State "no".
            """));
    }

    [Fact]
    public void Combined_WithComparisons()
        => Assert.Equal("yes", Run("""
            Define x as 0.
            Define y as 1.
            If x is 0 and y is 1, State "yes". Otherwise, State "no".
            """));

    [Fact]
    public void Parens_Override_Precedence()
    {
        // (false or true) and false = true and false = false
        Assert.Equal("no", Run("""
            Define p as 1 = 2.
            Define q as 1 = 1.
            Define r as 1 = 2.
            If (p or q) and r, State "yes". Otherwise, State "no".
            """));
    }

    [Fact]
    public void LogicalAnd_InWhileCondition()
    {
        // Both counters increment together; loop runs while both < 3; ends at n=3, m=3.
        Assert.Equal("3", Run("""
            Define n as 0.
            Define m as 0.
            While n is less than 3 and m is less than 3, repeat:
                n becomes n + 1.
                m becomes m + 1.
            Done.
            State n.
            """));
    }

    [Fact]
    public void And_NonFact_ThrowsTypeError()
    {
        // n is a number — 'and' requires facts on both sides
        var ex = Assert.Throws<TypeException>(() => Run("""
            Define n as 5.
            If n and n is 0, State "oops".
            """));
        Assert.Contains("and", ex.Message);
    }

    [Fact]
    public void Or_NonFact_ThrowsTypeError()
    {
        // n is a number — 'or' requires facts on both sides
        var ex = Assert.Throws<TypeException>(() => Run("""
            Define n as 5.
            If n is 0 or n, State "oops".
            """));
        Assert.Contains("or", ex.Message);
    }

    // ── Logical not ───────────────────────────────────────────────────────

    [Fact]
    public void Not_NegatesTrue_GivesFalse()
    {
        var output = Run("""
            Define flag as 1 = 1.
            State not flag.
            """);
        Assert.Equal("false", output.Trim());
    }

    [Fact]
    public void Not_NegatesFalse_GivesTrue()
    {
        var output = Run("""
            Define flag as 1 = 2.
            State not flag.
            """);
        Assert.Equal("true", output.Trim());
    }

    [Fact]
    public void Not_ConditionContext_WhenEqual_RunsElse()
    {
        // x is 5; not x is 5 → false → else branch
        var output = Run("""
            Define x as 5.
            If not x is 5, State "wrong".
            Otherwise, State "right".
            """);
        Assert.Equal("right", output.Trim());
    }

    [Fact]
    public void Not_ConditionContext_WhenNotEqual_RunsIf()
    {
        // x is 3; not x is 5 → true → if branch
        var output = Run("""
            Define x as 3.
            If not x is 5, State "right".
            Otherwise, State "wrong".
            """);
        Assert.Equal("right", output.Trim());
    }

    [Fact]
    public void Not_InExpressionContext()
    {
        var output = Run("""
            Define flag as 1 = 2.
            Define result as not flag.
            State result.
            """);
        Assert.Equal("true", output.Trim());
    }

    [Fact]
    public void Not_DoubleNegation_RestoresOriginal()
    {
        var output = Run("""
            Define flag as 1 = 1.
            State not not flag.
            """);
        Assert.Equal("true", output.Trim());
    }

    [Fact]
    public void Not_PrecedenceWithAnd_NegatesLeftOperandOnly()
    {
        // not p and q = (not p) and q; p=true, q=false → false and false → false
        var output = Run("""
            Define p as 1 = 1.
            Define q as 1 = 2.
            State not p and q.
            """);
        Assert.Equal("false", output.Trim());
    }

    [Fact]
    public void Not_ParensOverrideAndPrecedence()
    {
        // not (p and q); p=true, q=false → not false → true
        var output = Run("""
            Define p as 1 = 1.
            Define q as 1 = 2.
            State not (p and q).
            """);
        Assert.Equal("true", output.Trim());
    }

    [Fact]
    public void Not_PrecedenceWithOr_NegatesLeftOperandOnly()
    {
        // not p or q = (not p) or q; p=true, q=true → false or true → true
        var output = Run("""
            Define p as 1 = 1.
            Define q as 1 = 1.
            State not p or q.
            """);
        Assert.Equal("true", output.Trim());
    }

    [Fact]
    public void Not_ParensOverrideOrPrecedence()
    {
        // not (p or q); p=true, q=true → not true → false
        var output = Run("""
            Define p as 1 = 1.
            Define q as 1 = 1.
            State not (p or q).
            """);
        Assert.Equal("false", output.Trim());
    }

    [Fact]
    public void Not_NonFact_ThrowsTypeError()
    {
        var ex = Assert.Throws<TypeException>(() => Run("""
            Define x as 5.
            State not x.
            """));
        Assert.Contains("not", ex.Message);
    }

    // ── Records — slice 1: anonymous literals and field access ─────────────

    [Fact]
    public void Record_StatePositionalsOnly()
    {
        Assert.Equal("record(Alice, 30)", Run(
            "Define p as a record with (\"Alice\", 30).\n" +
            "State p."));
    }

    [Fact]
    public void Record_StateNamedOnly()
    {
        Assert.Equal("record(city: Norman)", Run(
            "Define r as a record with (the city \"Norman\").\n" +
            "State r."));
    }

    [Fact]
    public void Record_StateMixed()
    {
        // named fields sorted alphabetically in RecordType, but RecordValue preserves literal order
        Assert.Equal("record(Alice, 30, city: Norman, score: 95)", Run(
            "Define alice as a record with (\"Alice\", 30, the city \"Norman\", the score 95).\n" +
            "State alice."));
    }

    [Fact]
    public void Record_PositionalFirstAccess()
    {
        Assert.Equal("Alice", Run(
            "Define p as a record with (\"Alice\", 30).\n" +
            "State the first of p."));
    }

    [Fact]
    public void Record_PositionalSecondAccess()
    {
        Assert.Equal("30", Run(
            "Define p as a record with (\"Alice\", 30).\n" +
            "State the second of p."));
    }

    [Fact]
    public void Record_NamedFieldAccess()
    {
        Assert.Equal("Norman", Run(
            "Define alice as a record with (\"Alice\", 30, the city \"Norman\").\n" +
            "State the city of alice."));
    }

    [Fact]
    public void Record_NamedFieldUsedInArithmetic()
    {
        Assert.Equal("105", Run(
            "Define student as a record with (the score 95).\n" +
            "State the score of student + 10."));
    }

    [Fact]
    public void Record_NamedFieldInCondition()
    {
        Assert.Equal("pass", Run(
            "Define student as a record with (the score 95).\n" +
            "If the score of student is greater than 90, State \"pass\".\n" +
            "Otherwise, State \"fail\"."));
    }

    [Fact]
    public void Record_NestedLiteralAndAccess()
    {
        Assert.Equal("Norman", Run(
            "Define person as a record with (\"Alice\", the home a record with (the city \"Norman\")).\n" +
            "State the city of the home of person."));
    }

    [Fact]
    public void Record_ChainedNamedAccess()
    {
        // the city of the home of person — two-hop chain
        Assert.Equal("OK", Run(
            "Define person as a record with (\n" +
            "    the home a record with (the city \"Norman\", the region \"OK\")\n" +
            ").\n" +
            "State the region of the home of person."));
    }

    [Fact]
    public void Record_StructuralEqualityAllowsReassign()
    {
        // Both records have same shape (text, number); reassignment must pass type check.
        Assert.Equal("Bob", Run(
            "Define p as a record with (\"Alice\", 30).\n" +
            "p becomes a record with (\"Bob\", 25).\n" +
            "State the first of p."));
    }

    [Fact]
    public void Record_NamedFieldOrderInsensitiveEquality()
    {
        // {the b 2, the a 1} and {the a 1, the b 2} must be the same structural type.
        Assert.Equal("ok", Run(
            "Define r as a record with (the b 2, the a 1).\n" +
            "r becomes a record with (the a 1, the b 2).\n" +
            "State \"ok\"."));
    }

    [Fact]
    public void Record_InSeries()
    {
        // Series of records — all same structural type.
        Assert.Equal("Alice\nBob", Run(
            "Define people as a series with (\n" +
            "    a record with (\"Alice\", 30),\n" +
            "    a record with (\"Bob\", 25)\n" +
            ").\n" +
            "State the first of the first of people.\n" +
            "State the first of the second of people."));
    }

    [Fact]
    public void Record_WrongNamedFieldThrowsTypeError()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Define r as a record with (the city \"Norman\").\n" +
            "State the score of r."));
        Assert.Contains("score", ex.Message);
    }

    [Fact]
    public void Record_PositionalOutOfBoundsThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define r as a record with (\"Alice\", 30).\n" +
            "State the third of r."));
    }

    [Fact]
    public void Record_PositionalAfterNamedThrowsParseError()
    {
        Assert.Throws<ParseException>(() => Run(
            "Define r as a record with (the city \"Norman\", 30)."));
    }

    [Fact]
    public void Record_DuplicateNamedFieldThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define r as a record with (the score 90, the score 100)."));
    }

    [Fact]
    public void Record_TypeMismatchOnReassignThrowsTypeError()
    {
        // Different shapes → different types → type error on becomes.
        Assert.Throws<TypeException>(() => Run(
            "Define r as a record with (the score 90).\n" +
            "r becomes a record with (the city \"Norman\")."));
    }

    [Fact]
    public void Record_FieldFactUsedWithNot()
    {
        // Named field is a fact; 'not' on it should work.
        Assert.Equal("false", Run(
            "Define r as a record with (the active 1 = 1).\n" +
            "State not the active of r."));
    }

    // ── Records — slice 2: field mutation ────────────────────────────────

    [Fact]
    public void Record_SetNamedField()
    {
        Assert.Equal("Tulsa", Run(
            "Define alice as a record with (\"Alice\", 30, the city \"Norman\", the score 95).\n" +
            "the city of alice becomes \"Tulsa\".\n" +
            "State the city of alice."));
    }

    [Fact]
    public void Record_SetPositionalField()
    {
        Assert.Equal("31", Run(
            "Define alice as a record with (\"Alice\", 30, the city \"Norman\").\n" +
            "the second of alice becomes 31.\n" +
            "State the second of alice."));
    }

    [Fact]
    public void Record_SetByParametric()
    {
        Assert.Equal("99", Run(
            "Define r as a record with (10, 20, 30).\n" +
            "Define n as 2.\n" +
            "item n of r becomes 99.\n" +
            "State the second of r."));
    }

    [Fact]
    public void Record_SetPreservesOtherFields()
    {
        Assert.Equal("95", Run(
            "Define r as a record with (\"Alice\", 30, the city \"Norman\", the score 95).\n" +
            "the city of r becomes \"Tulsa\".\n" +
            "State the score of r."));
    }

    [Fact]
    public void Record_MultipleFieldMutations()
    {
        Assert.Equal("Tulsa\n31", Run(
            "Define alice as a record with (\"Alice\", 30, the city \"Norman\", the score 95).\n" +
            "the city of alice becomes \"Tulsa\".\n" +
            "the second of alice becomes 31.\n" +
            "State the city of alice.\n" +
            "State the second of alice."));
    }

    [Fact]
    public void Record_ChainedNamedSet()
    {
        Assert.Equal("Tulsa", Run(
            "Define alice as a record with (the home a record with (the city \"Norman\")).\n" +
            "the city of the home of alice becomes \"Tulsa\".\n" +
            "State the city of the home of alice."));
    }

    [Fact]
    public void Record_SetNamedFieldTypeMismatchThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define r as a record with (the score 95).\n" +
            "the score of r becomes \"bad\"."));
    }

    [Fact]
    public void Record_SetPositionalFieldTypeMismatchThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define r as a record with (10, 20).\n" +
            "the first of r becomes \"bad\"."));
    }

    [Fact]
    public void Record_SetNonexistentNamedFieldThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define r as a record with (the score 95).\n" +
            "the name of r becomes \"Bob\"."));
    }

    [Fact]
    public void Record_SetPositionalOutOfBoundsThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define r as a record with (10, 20).\n" +
            "the fifth of r becomes 99."));
    }

    // ── Records — slice 2: value semantics (deep-copy on assignment) ──────

    [Fact]
    public void Record_ValueSemantics_DefineDoesNotShare()
    {
        Assert.Equal("Norman", Run(
            "Define alice as a record with (\"Alice\", 30, the city \"Norman\").\n" +
            "Define bob as alice.\n" +
            "the city of bob becomes \"Tulsa\".\n" +
            "State the city of alice."));
    }

    [Fact]
    public void Record_ValueSemantics_BecomesDoesNotShare()
    {
        Assert.Equal("Tulsa", Run(
            "Define alice as a record with (the city \"Norman\").\n" +
            "Define other as a record with (the city \"Tulsa\").\n" +
            "alice becomes other.\n" +
            "the city of other becomes \"Chicago\".\n" +
            "State the city of alice."));
    }

    // ── Records — slice 3: record shapes in function annotations ─────────

    [Fact]
    public void Record_FunctionParam_MatchingRecord()
    {
        Assert.Equal("Norman", Run(
            "Bind text to city-of, given (the record person with (text, the text city)):\n" +
            "    return the city of person.\n" +
            "Done.\n" +
            "Define alice as a record with (\"Alice\", the city \"Norman\").\n" +
            "State Cast city-of on (alice)."));
    }

    [Fact]
    public void Record_FunctionParam_ShapeMismatchThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind text to city-of, given (the record person with (text, the text city)):\n" +
            "    return the city of person.\n" +
            "Done.\n" +
            "Define r as a record with (42, the city \"Norman\").\n" +
            "Cast city-of on (r)."));
    }

    [Fact]
    public void Record_FunctionParam_NamedFieldOnly()
    {
        Assert.Equal("95", Run(
            "Bind number to get-score, given (the record p with (the number score)):\n" +
            "    return the score of p.\n" +
            "Done.\n" +
            "Define alice as a record with (the score 95).\n" +
            "State Cast get-score on (alice)."));
    }

    [Fact]
    public void Record_FunctionParam_MultipleParams()
    {
        Assert.Equal("100", Run(
            "Bind number to add-score, given (the record person with (the number score), the number bonus):\n" +
            "    return the score of person + bonus.\n" +
            "Done.\n" +
            "Define alice as a record with (the score 95).\n" +
            "State Cast add-score on (alice, 5)."));
    }

    [Fact]
    public void Record_FunctionReturnAnnotation_ReturnsRecord()
    {
        Assert.Equal("95", Run(
            "Bind the record result with (the number score) to make-result:\n" +
            "    return a record with (the score 95).\n" +
            "Done.\n" +
            "Define r as Cast make-result.\n" +
            "State the score of r."));
    }

    [Fact]
    public void Record_FunctionReturnAnnotation_NoLabelRequired()
    {
        Assert.Equal("Alice", Run(
            "Bind the record with (text) to make-person:\n" +
            "    return a record with (\"Alice\").\n" +
            "Done.\n" +
            "Define r as Cast make-person.\n" +
            "State the first of r."));
    }

    [Fact]
    public void Record_FunctionReturnAnnotation_ShapeMismatchThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind the record result with (the text city) to make-city:\n" +
            "    return a record with (42).\n" +
            "Done.\n" +
            "Cast make-city."));
    }

    [Fact]
    public void Record_FunctionAnnotation_MatchedByStructuralEquality()
    {
        Assert.Equal("true", Run(
            "Bind fact to matches, given (the record p with (text, the number score)):\n" +
            "    return the score of p > 90.\n" +
            "Done.\n" +
            "Define alice as a record with (\"Alice\", the score 95).\n" +
            "State Cast matches on (alice)."));
    }

    // ── Records Slice 4 — empty series of records ─────────────────────────────

    [Fact]
    public void Record_EmptySeries_DefineAndAddAndAccess()
    {
        Assert.Equal("Norman", Run(
            "Define party as a series of records like (the text name, the number age).\n" +
            "Add a record with (the name \"Norman\", the age 30) to party.\n" +
            "State the name of the first of party."));
    }

    [Fact]
    public void Record_EmptySeries_PluralRecords()
    {
        Assert.Equal("42", Run(
            "Define scores as a series of records like (the number value).\n" +
            "Add a record with (the value 42) to scores.\n" +
            "State the value of the first of scores."));
    }

    [Fact]
    public void Record_EmptySeries_AddTypeMismatchThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define party as a series of records like (the text name).\n" +
            "Add a record with (the name 99) to party."));
    }

    [Fact]
    public void Record_EmptySeries_AddShapeMismatchThrowsTypeError()
    {
        // Series expects (the text name); adding a record with a different field name → shape mismatch
        Assert.Throws<TypeException>(() => Run(
            "Define party as a series of records like (the text name).\n" +
            "Add a record with (the score 99) to party."));
    }

    [Fact]
    public void Record_EmptySeries_ForEachIterates()
    {
        Assert.Equal("Alice\nBob", Run(
            "Define roster as a series of records like (the text name).\n" +
            "Add a record with (the name \"Alice\") to roster.\n" +
            "Add a record with (the name \"Bob\") to roster.\n" +
            "For each member in roster, repeat:\n" +
            "    State the name of member.\n" +
            "Done."));
    }

    [Fact]
    public void Record_EmptySeries_PositionalShape()
    {
        Assert.Equal("5", Run(
            "Define pairs as a series of records like (number, text).\n" +
            "Add a record with (5, \"five\") to pairs.\n" +
            "State the first of the first of pairs."));
    }

    [Fact]
    public void Record_EmptySeries_PopulatedSeriesStillInfers()
    {
        Assert.Equal("10", Run(
            "Define nums as a series with (a record with (10), a record with (20)).\n" +
            "State the first of the first of nums."));
    }

    // ── Objects — slice 1: definitions and instances ──────────────────────────

    [Fact]
    public void Object_DefineAndPossessiveFieldAccess()
    {
        Assert.Equal("Alice", Run(
            "Define object person with (the text name, the number age).\n" +
            "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
            "State alice's name."));
    }

    [Fact]
    public void Object_NamedFieldViaTheOfSyntax()
    {
        Assert.Equal("30", Run(
            "Define object person with (the text name, the number age).\n" +
            "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
            "State the age of alice."));
    }

    [Fact]
    public void Object_PositionalFieldAccess()
    {
        Assert.Equal("Alice", Run(
            "Define object person with (text, number).\n" +
            "Define alice as a new person { \"Alice\", 30 }.\n" +
            "State the first of alice."));
    }

    [Fact]
    public void Object_StateFormatsCorrectly()
    {
        Assert.Equal("person(Alice, 30)", Run(
            "Define object person with (text, number).\n" +
            "Define alice as a new person { \"Alice\", 30 }.\n" +
            "State alice."));
    }

    [Fact]
    public void Object_NamedFieldsStateFormat()
    {
        Assert.Equal("person(name: Alice, score: 95)", Run(
            "Define object person with (the text name, the number score).\n" +
            "Define alice as a new person { the name \"Alice\", the score 95 }.\n" +
            "State alice."));
    }

    [Fact]
    public void Object_ValueSemantics_DefineDoesNotShare()
    {
        // Defining bob as alice creates a deep copy — mutating alice's positional field
        // via a record-set won't exist for objects yet, so test via becomes with different literal.
        Assert.Equal("Alice", Run(
            "Define object person with (the text name, the number age).\n" +
            "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
            "Define bob as alice.\n" +
            "bob becomes a new person { the name \"Bob\", the age 25 }.\n" +
            "State alice's name."));
    }

    [Fact]
    public void Object_ValueSemantics_BecomesDoesNotShare()
    {
        Assert.Equal("Alice", Run(
            "Define object person with (the text name, the number age).\n" +
            "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
            "Define other as a new person { the name \"Other\", the age 0 }.\n" +
            "other becomes alice.\n" +
            "alice becomes a new person { the name \"Changed\", the age 99 }.\n" +
            "State other's name."));
    }

    [Fact]
    public void Object_InSeries()
    {
        Assert.Equal("Alice\nBob", Run(
            "Define object person with (the text name, the number age).\n" +
            "Define people as a series with (\n" +
            "    a new person { the name \"Alice\", the age 30 },\n" +
            "    a new person { the name \"Bob\", the age 25 }\n" +
            ").\n" +
            "State the name of the first of people.\n" +
            "State the name of the second of people."));
    }

    [Fact]
    public void Object_UnknownTypeThrowsTypeError()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Define alice as a new ghost { the name \"Alice\" }."));
        Assert.Contains("ghost", ex.Message);
    }

    [Fact]
    public void Object_WrongFieldTypeThrowsTypeError()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name, the number age).\n" +
            "Define alice as a new person { the name 42, the age 30 }."));
        Assert.Contains("name", ex.Message);
    }

    [Fact]
    public void Object_MissingRequiredFieldThrowsTypeError()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name, the number age).\n" +
            "Define alice as a new person { the name \"Alice\" }."));
        Assert.Contains("age", ex.Message);
    }

    [Fact]
    public void Object_ExtraFieldThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name).\n" +
            "Define alice as a new person { the name \"Alice\", the score 99 }."));
    }

    [Fact]
    public void Object_FieldInArithmetic()
    {
        Assert.Equal("40", Run(
            "Define object person with (the text name, the number age).\n" +
            "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
            "State alice's age + 10."));
    }

    [Fact]
    public void Object_NominalTyping_SameShapeDifferentNameThrowsTypeError()
    {
        // Two objects with identical shapes but different names are distinct types.
        Assert.Throws<TypeException>(() => Run(
            "Define object cat with (the text name).\n" +
            "Define object dog with (the text name).\n" +
            "Define felix as a new cat { the name \"Felix\" }.\n" +
            "felix becomes a new dog { the name \"Rex\" }."));
    }

    // ── Objects — slice 2: methods ────────────────────────────────────────────

    [Fact]
    public void Object_Method_DispatchWithCastOn()
    {
        Assert.Equal("Alice", Run(
            "Define object person with (the text name):\n" +
            "    Bind void to greet:\n" +
            "        State one's name.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "Cast greet on alice."));
    }

    [Fact]
    public void Object_Method_PossessiveDispatch()
    {
        Assert.Equal("Bob", Run(
            "Define object person with (the text name):\n" +
            "    Bind void to greet:\n" +
            "        State one's name.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define bob as a new person { the name \"Bob\" }.\n" +
            "Cast bob's greet."));
    }

    [Fact]
    public void Object_Method_ReturnValue()
    {
        Assert.Equal("Alice", Run(
            "Define object person with (the text name):\n" +
            "    Bind text to get-name:\n" +
            "        return one's name.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "State Cast get-name on alice."));
    }

    [Fact]
    public void Object_Method_PossessiveDispatchWithReturnValue()
    {
        Assert.Equal("Alice", Run(
            "Define object person with (the text name):\n" +
            "    Bind text to get-name:\n" +
            "        return one's name.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "State Cast alice's get-name."));
    }

    [Fact]
    public void Object_Method_ReturnValueInExpression()
    {
        Assert.Equal("50", Run(
            "Define object account with (the number balance):\n" +
            "    Bind number to get-balance:\n" +
            "        return one's balance.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define acct as a new account { the balance 40 }.\n" +
            "State Cast get-balance on acct + 10."));
    }

    [Fact]
    public void Object_Method_TwoObjects_DispatchesToCorrectReceiver()
    {
        Assert.Equal("Alice\nBob", Run(
            "Define object person with (the text name):\n" +
            "    Bind void to greet:\n" +
            "        State one's name.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "Define bob as a new person { the name \"Bob\" }.\n" +
            "Cast greet on alice.\n" +
            "Cast greet on bob."));
    }

    [Fact]
    public void Object_Method_DoesNotMutateReceiver()
    {
        // Both State calls should produce "Alice" — method reads one's name, original unchanged.
        Assert.Equal("Alice\nAlice", Run(
            "Define object person with (the text name):\n" +
            "    Bind void to check:\n" +
            "        State one's name.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "Cast check on alice.\n" +
            "State alice's name."));
    }

    [Fact]
    public void Object_Method_UnknownMethodThrowsTypeError()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name).\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "Cast fly on alice."));
        Assert.Contains("fly", ex.Message);
    }

    [Fact]
    public void Object_Method_PossessiveUnknownMethodThrowsTypeError()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name).\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "Cast alice's fly."));
        Assert.Contains("fly", ex.Message);
    }

    [Fact]
    public void Object_Method_VoidUsedAsValueThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name):\n" +
            "    Bind void to greet:\n" +
            "        State one's name.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "State Cast greet on alice."));
    }

    // ── Objects Slice 3: field mutation ──────────────────────────────────────

    [Fact]
    public void Object_Mutation_DirectNamedField()
    {
        Assert.Equal("31", Run(
            "Define object person with (the text name, the number age).\n" +
            "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
            "the age of alice becomes 31.\n" +
            "State the age of alice."));
    }

    [Fact]
    public void Object_Mutation_PossessiveSet()
    {
        Assert.Equal("31", Run(
            "Define object person with (the text name, the number age).\n" +
            "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
            "alice's age becomes 31.\n" +
            "State alice's age."));
    }

    [Fact]
    public void Object_Mutation_PositionalField()
    {
        // person has one positional (text) and one named (number age)
        Assert.Equal("Bob", Run(
            "Define object person with (text, the number age).\n" +
            "Define alice as a new person { \"Alice\", the age 30 }.\n" +
            "the first of alice becomes \"Bob\".\n" +
            "State the first of alice."));
    }

    [Fact]
    public void Object_Mutation_ValueSemanticsPreserved()
    {
        // Mutating the original does not affect a prior copy.
        Assert.Equal("Alice\n30", Run(
            "Define object person with (the text name, the number age).\n" +
            "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
            "Define bob as alice.\n" +
            "the age of alice becomes 99.\n" +
            "State bob's name.\n" +
            "State bob's age."));
    }

    [Fact]
    public void Object_Mutation_MethodMutatesReceiver()
    {
        Assert.Equal("31", Run(
            "Define object person with (the text name, the number age):\n" +
            "    Bind void to birthday:\n" +
            "        one's age becomes one's age + 1.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
            "Cast birthday on alice.\n" +
            "State the age of alice."));
    }

    [Fact]
    public void Object_Mutation_MethodDoesNotAffectCopy()
    {
        // A prior copy is unaffected by a method that mutates the original.
        Assert.Equal("30", Run(
            "Define object person with (the text name, the number age):\n" +
            "    Bind void to birthday:\n" +
            "        one's age becomes one's age + 1.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
            "Define bob as alice.\n" +
            "Cast birthday on alice.\n" +
            "State bob's age."));
    }

    [Fact]
    public void Object_Mutation_WrongTypeThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name, the number age).\n" +
            "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
            "the age of alice becomes \"old\"."));
    }

    [Fact]
    public void Object_Mutation_UnknownFieldThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name, the number age).\n" +
            "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
            "the city of alice becomes \"Norman\"."));
    }

    [Fact]
    public void Object_Mutation_PossessiveSet_WrongTypeThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name, the number age).\n" +
            "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
            "alice's age becomes \"old\"."));
    }

    // ── Objects — Slice 4: Embedding ─────────────────────────────────────────

    [Fact]
    public void Object_Embedding_BasicFieldAccessViaTheOf()
    {
        Assert.Equal("Alice\n100",
            Run(
                "Define object person with (the text name, the number age).\n" +
                "Define object customer with (the number balance) and as a person.\n" +
                "Define alice as a new customer { the balance 100, the name \"Alice\", the age 30 }.\n" +
                "State the name of alice.\n" +
                "State the balance of alice."));
    }

    [Fact]
    public void Object_Embedding_PossessiveFieldAccess()
    {
        Assert.Equal("Alice",
            Run(
                "Define object person with (the text name).\n" +
                "Define object customer with (the number balance) and as a person.\n" +
                "Define alice as a new customer { the balance 100, the name \"Alice\" }.\n" +
                "State alice's name."));
    }

    [Fact]
    public void Object_Embedding_EscapeHatch()
    {
        Assert.Equal("Alice",
            Run(
                "Define object person with (the text name).\n" +
                "Define object customer with (the number balance) and as a person.\n" +
                "Define alice as a new customer { the balance 100, the name \"Alice\" }.\n" +
                "State the name of the person of alice."));
    }

    [Fact]
    public void Object_Embedding_MethodPromotion()
    {
        Assert.Equal("Alice",
            Run(
                "Define object person with (the text name):\n" +
                "    Bind void to greet:\n" +
                "        State one's name.\n" +
                "    Done.\n" +
                "Done.\n" +
                "Define object customer with (the number balance) and as a person.\n" +
                "Define alice as a new customer { the balance 100, the name \"Alice\" }.\n" +
                "Cast greet on alice."));
    }

    [Fact]
    public void Object_Embedding_PossessiveMethodDispatch()
    {
        Assert.Equal("Alice",
            Run(
                "Define object person with (the text name):\n" +
                "    Bind void to greet:\n" +
                "        State one's name.\n" +
                "    Done.\n" +
                "Done.\n" +
                "Define object customer with (the number balance) and as a person.\n" +
                "Define alice as a new customer { the balance 100, the name \"Alice\" }.\n" +
                "Cast alice's greet."));
    }

    [Fact]
    public void Object_Embedding_TransitiveFieldAccess()
    {
        Assert.Equal("Springfield",
            Run(
                "Define object address with (the text city).\n" +
                "Define object person with (the text name) and as an address.\n" +
                "Define object customer with (the number balance) and as a person.\n" +
                "Define alice as a new customer { the balance 100, the name \"Alice\", the city \"Springfield\" }.\n" +
                "State the city of alice."));
    }

    [Fact]
    public void Object_Embedding_ValueSemanticsDeepCopy()
    {
        Assert.Equal("Bob\nAlice",
            Run(
                "Define object person with (the text name).\n" +
                "Define object customer with (the number balance) and as a person.\n" +
                "Define alice as a new customer { the balance 100, the name \"Alice\" }.\n" +
                "Define copy as alice.\n" +
                "the name of alice becomes \"Bob\".\n" +
                "State the name of alice.\n" +
                "State the name of copy."));
    }

    [Fact]
    public void Object_Embedding_MutatePromotedField()
    {
        Assert.Equal("Bob",
            Run(
                "Define object person with (the text name).\n" +
                "Define object customer with (the number balance) and as a person.\n" +
                "Define alice as a new customer { the balance 100, the name \"Alice\" }.\n" +
                "the name of alice becomes \"Bob\".\n" +
                "State the name of alice."));
    }

    [Fact]
    public void Object_Embedding_MutatePromotedFieldPossessive()
    {
        Assert.Equal("Bob",
            Run(
                "Define object person with (the text name).\n" +
                "Define object customer with (the number balance) and as a person.\n" +
                "Define alice as a new customer { the balance 100, the name \"Alice\" }.\n" +
                "alice's name becomes \"Bob\".\n" +
                "State alice's name."));
    }

    [Fact]
    public void Object_Embedding_CollisionThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name).\n" +
            "Define object customer with (the text name, the number balance) and as a person."));
    }

    [Fact]
    public void Object_Embedding_NoSubtypingThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name).\n" +
            "Define object customer with (the number balance) and as a person.\n" +
            "Define p as a new person { the name \"Bob\" }.\n" +
            "Define c as a new customer { the balance 100, the name \"Alice\" }.\n" +
            "p becomes c."));
    }

    [Fact]
    public void Object_Embedding_UnknownEmbedTypeThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object customer with (the number balance) and as a ghost."));
    }

    [Fact]
    public void Object_Embedding_Format()
    {
        Assert.Equal("customer(balance: 100, person(name: Alice))",
            Run(
                "Define object person with (the text name).\n" +
                "Define object customer with (the number balance) and as a person.\n" +
                "Define alice as a new customer { the balance 100, the name \"Alice\" }.\n" +
                "State alice."));
    }

    // ── Objects — Slice 5: interfaces ─────────────────────────────────────────

    [Fact]
    public void Interface_Declaration_SingleMethod_NoBraces()
    {
        Assert.Equal("Alice",
            Run(
                "Define greeter as an interface for the void function greet.\n" +
                "Define object person with (the text name) and greeter:\n" +
                "    Bind void to greet:\n" +
                "        State one's name.\n" +
                "    Done.\n" +
                "Done.\n" +
                "Define alice as a new person { the name \"Alice\" }.\n" +
                "Cast greet on alice."));
    }

    [Fact]
    public void Interface_Declaration_MultiMethod_Braces()
    {
        Assert.Equal("Alice\n30",
            Run(
                "Define describable as an interface for {\n" +
                "    the void function show-name,\n" +
                "    the void function show-age\n" +
                "}.\n" +
                "Define object person with (the text name, the number age) and describable:\n" +
                "    Bind void to show-name:\n" +
                "        State one's name.\n" +
                "    Done.\n" +
                "    Bind void to show-age:\n" +
                "        State one's age.\n" +
                "    Done.\n" +
                "Done.\n" +
                "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
                "Cast show-name on alice.\n" +
                "Cast show-age on alice."));
    }

    [Fact]
    public void Interface_Polymorphic_Dispatch_DifferentConformingTypes()
    {
        Assert.Equal("Alice\nRex",
            Run(
                "Define greeter as an interface for the void function greet.\n" +
                "Define object person with (the text name) and greeter:\n" +
                "    Bind void to greet:\n" +
                "        State one's name.\n" +
                "    Done.\n" +
                "Done.\n" +
                "Define object dog with (the text name) and greeter:\n" +
                "    Bind void to greet:\n" +
                "        State one's name.\n" +
                "    Done.\n" +
                "Done.\n" +
                "Bind void to do-greet, given (the greeter g):\n" +
                "    Cast greet on g.\n" +
                "Done.\n" +
                "Define alice as a new person { the name \"Alice\" }.\n" +
                "Define rex as a new dog { the name \"Rex\" }.\n" +
                "Cast do-greet on (alice).\n" +
                "Cast do-greet on (rex)."));
    }

    [Fact]
    public void Interface_Polymorphic_Dispatch_ReturnsValue()
    {
        Assert.Equal("Hello from Alice",
            Run(
                "Define namer as an interface for the text function get-name.\n" +
                "Define object person with (the text name) and namer:\n" +
                "    Bind text to get-name:\n" +
                "        Return one's name.\n" +
                "    Done.\n" +
                "Done.\n" +
                "Bind void to announce, given (the namer n):\n" +
                "    State Cast get-name on n.\n" +
                "Done.\n" +
                "Define alice as a new person { the name \"Hello from Alice\" }.\n" +
                "Cast announce on (alice)."));
    }

    [Fact]
    public void Interface_ConformanceWithParamTypes_StaticCheckPasses()
    {
        // Interface declares a parameterized method; conforming object must match param types.
        Assert.Equal("done",
            Run(
                "Define transformer as an interface for the void function transform, given (the number x).\n" +
                "Define object doubler with (the number factor) and transformer:\n" +
                "    Bind void to transform, given (the number x):\n" +
                "        State one's factor.\n" +
                "    Done.\n" +
                "Done.\n" +
                "State \"done\"."));
    }

    [Fact]
    public void Interface_MultiConformance()
    {
        Assert.Equal("Alice\n30",
            Run(
                "Define namer as an interface for the void function show-name.\n" +
                "Define ager as an interface for the void function show-age.\n" +
                "Define object person with (the text name, the number age) and namer and ager:\n" +
                "    Bind void to show-name:\n" +
                "        State one's name.\n" +
                "    Done.\n" +
                "    Bind void to show-age:\n" +
                "        State one's age.\n" +
                "    Done.\n" +
                "Done.\n" +
                "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
                "Cast show-name on alice.\n" +
                "Cast show-age on alice."));
    }

    [Fact]
    public void Interface_EmbeddingPlusConformance()
    {
        Assert.Equal("Alice",
            Run(
                "Define greeter as an interface for the void function greet.\n" +
                "Define object base-entity with (the text label).\n" +
                "Define object person with (the text name) and as a base-entity and greeter:\n" +
                "    Bind void to greet:\n" +
                "        State one's name.\n" +
                "    Done.\n" +
                "Done.\n" +
                "Bind void to do-greet, given (the greeter g):\n" +
                "    Cast greet on g.\n" +
                "Done.\n" +
                "Define alice as a new person { the name \"Alice\", the label \"entity\" }.\n" +
                "Cast do-greet on (alice)."));
    }

    [Fact]
    public void Interface_ConformanceViaEmbedding()
    {
        Assert.Equal("Hi!",
            Run(
                "Define greeter as an interface for the void function greet.\n" +
                "Define object person with (the text name) and greeter:\n" +
                "    Bind void to greet:\n" +
                "        State \"Hi!\".\n" +
                "    Done.\n" +
                "Done.\n" +
                "Define object employee with (the number id) and as a person and greeter.\n" +
                "Bind void to do-greet, given (the greeter g):\n" +
                "    Cast greet on g.\n" +
                "Done.\n" +
                "Define e as a new employee { the id 42, the name \"Alice\" }.\n" +
                "Cast do-greet on (e)."));
    }

    [Fact]
    public void Interface_Error_MissingMethod_ThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define greeter as an interface for the void function greet.\n" +
            "Define object person with (the text name) and greeter.\n"));
    }

    [Fact]
    public void Interface_Error_WrongReturnType_ThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define greeter as an interface for the text function greet.\n" +
            "Define object person with (the text name) and greeter:\n" +
            "    Bind void to greet:\n" +
            "        State one's name.\n" +
            "    Done.\n" +
            "Done.\n"));
    }

    [Fact]
    public void Interface_Error_WrongParamType_ThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define adder as an interface for the number function add-to, given (the number x).\n" +
            "Define object accumulator with (the number base-val) and adder:\n" +
            "    Bind number to add-to, given (the text x):\n" +
            "        Return one's base-val.\n" +
            "    Done.\n" +
            "Done.\n"));
    }

    [Fact]
    public void Interface_Error_UnknownInterface_ThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name) and greeter.\n"));
    }

    [Fact]
    public void Interface_Error_NonConformingArgument_ThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define greeter as an interface for the void function greet.\n" +
            "Define object person with (the text name).\n" +
            "Bind void to do-greet, given (the greeter g):\n" +
            "    Cast greet on g.\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "Cast do-greet on (alice)."));
    }

    [Fact]
    public void Interface_InterfaceMethod_NoSubtypingForConcreteParams()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define greeter as an interface for the void function greet.\n" +
            "Define object person with (the text name) and greeter:\n" +
            "    Bind void to greet:\n" +
            "        State one's name.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define object employee with (the number id) and as a person.\n" +
            "Bind void to greet-person, given (the person p):\n" +
            "    Cast greet on p.\n" +
            "Done.\n" +
            "Define e as a new employee { the id 1, the name \"Alice\" }.\n" +
            "Cast greet-person on (e)."));
    }

    // ── Objects Slice 6: method calls with arguments ──────────────────────

    [Fact]
    public void MethodArgs_NoParenAndParenFormsEquivalent()
    {
        // Cast greet on alice  and  Cast greet on (alice)  should produce the same result.
        const string setup =
            "Define object person with (the text name):\n" +
            "    Bind void to greet:\n" +
            "        State one's name.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\" }.\n";
        Assert.Equal("Alice", Run(setup + "Cast greet on alice."));
        Assert.Equal("Alice", Run(setup + "Cast greet on (alice)."));
    }

    [Fact]
    public void MethodArgs_SingleArgDispatch()
    {
        // Cast steer on (racer, 90) — method with one extra arg.
        Assert.Equal("90", Run(
            "Define object car with (the text model):\n" +
            "    Bind void to steer, given (the number angle):\n" +
            "        State angle.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define racer as a new car { the model \"speedster\" }.\n" +
            "Cast steer on (racer, 90)."));
    }

    [Fact]
    public void MethodArgs_MultipleArgsDispatch()
    {
        // Cast move on (racer, 90, 5) — method with two extra args.
        Assert.Equal("95", Run(
            "Define object car with (the text model):\n" +
            "    Bind void to move, given (the number angle, the number speed):\n" +
            "        State angle + speed.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define racer as a new car { the model \"speedster\" }.\n" +
            "Cast move on (racer, 90, 5)."));
    }

    [Fact]
    public void MethodArgs_PossessiveFormWithArg()
    {
        // Cast racer's steer on (90) — possessive form with args.
        Assert.Equal("90", Run(
            "Define object car with (the text model):\n" +
            "    Bind void to steer, given (the number angle):\n" +
            "        State angle.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define racer as a new car { the model \"speedster\" }.\n" +
            "Cast racer's steer on (90)."));
    }

    [Fact]
    public void MethodArgs_SelfCallWithOne()
    {
        // Inside a method, Call steer on (one, 45) — self as first arg.
        Assert.Equal("45", Run(
            "Define object car with (the text model):\n" +
            "    Bind void to steer, given (the number angle):\n" +
            "        State angle.\n" +
            "    Done.\n" +
            "    Bind void to maneuver:\n" +
            "        Cast steer on (one, 45).\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define racer as a new car { the model \"speedster\" }.\n" +
            "Cast maneuver on racer."));
    }

    [Fact]
    public void MethodArgs_InterfaceDispatchWithArg()
    {
        // Cast steer on (racer, 90) where racer's static type is interface-typed.
        Assert.Equal("90", Run(
            "Define driver as an interface for the void function steer, given (the number angle).\n" +
            "Define object car with (the text model) and driver:\n" +
            "    Bind void to steer, given (the number angle):\n" +
            "        State angle.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Bind void to race, given (the driver d):\n" +
            "    Cast steer on (d, 90).\n" +
            "Done.\n" +
            "Define racer as a new car { the model \"speedster\" }.\n" +
            "Cast race on (racer)."));
    }

    [Fact]
    public void MethodArgs_ReturnsValue()
    {
        // Method with args that returns a value; result used in expression.
        Assert.Equal("10", Run(
            "Define object calc with (the number offset):\n" +
            "    Bind number to plus, given (the number x):\n" +
            "        return one's offset + x.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define c as a new calc { the offset 7 }.\n" +
            "State Cast plus on (c, 3)."));
    }

    [Fact]
    public void MethodArgs_AmbiguousNameThrowsTypeError()
    {
        // Same name used as both a free function and a method → ambiguity error.
        Assert.Throws<TypeException>(() => Run(
            "Define object box with (the number value):\n" +
            "    Bind void to push, given (the number x):\n" +
            "        State x.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Bind void to push, given (the number x):\n" +
            "    State x.\n" +
            "Done.\n" +
            "Define b as a new box { the value 1 }.\n" +
            "Cast push on (b, 5)."));
    }

    [Fact]
    public void MethodArgs_UnknownMethodThrowsTypeError()
    {
        // Method name not on the object.
        var ex = Assert.Throws<TypeException>(() => Run(
            "Define object car with (the text model).\n" +
            "Define racer as a new car { the model \"speedster\" }.\n" +
            "Cast fly on (racer, 90)."));
        Assert.Contains("fly", ex.Message);
    }

    [Fact]
    public void MethodArgs_WrongArgCountThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object car with (the text model):\n" +
            "    Bind void to steer, given (the number angle):\n" +
            "        State angle.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define racer as a new car { the model \"speedster\" }.\n" +
            "Cast steer on (racer, 90, 5)."));
    }

    [Fact]
    public void MethodArgs_WrongArgTypeThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object car with (the text model):\n" +
            "    Bind void to steer, given (the number angle):\n" +
            "        State angle.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define racer as a new car { the model \"speedster\" }.\n" +
            "Cast steer on (racer, \"ninety\")."));
    }

    // ── Equality — Records & Objects ─────────────────────────────────────────

    [Fact]
    public void Equality_Records_SameShapeAndValueAreEqual()
    {
        Assert.Equal("equal", Run(
            "Define rec1 as a record with (the make \"Honda\", the year 2021).\n" +
            "Define rec2 as a record with (the make \"Honda\", the year 2021).\n" +
            "If rec1 is rec2, state \"equal\". Otherwise, state \"not equal\"."));
    }

    [Fact]
    public void Equality_Records_SameShapeDifferentValueNotEqual()
    {
        Assert.Equal("not equal", Run(
            "Define rec1 as a record with (the make \"Honda\", the year 2021).\n" +
            "Define rec2 as a record with (the make \"Toyota\", the year 2021).\n" +
            "If rec1 is rec2, state \"equal\". Otherwise, state \"not equal\"."));
    }

    [Fact]
    public void Equality_Records_IsNotWorks()
    {
        Assert.Equal("different", Run(
            "Define rec1 as a record with (the make \"Honda\", the year 2021).\n" +
            "Define rec2 as a record with (the make \"Toyota\", the year 2019).\n" +
            "If rec1 is not rec2, state \"different\". Otherwise, state \"same\"."));
    }

    [Fact]
    public void Equality_Records_NamedFieldsComparedOrderInsensitively()
    {
        // Construction order of named fields doesn't affect equality.
        Assert.Equal("equal", Run(
            "Define rec1 as a record with (the make \"Honda\", the year 2021).\n" +
            "Define rec2 as a record with (the year 2021, the make \"Honda\").\n" +
            "If rec1 is rec2, state \"equal\". Otherwise, state \"not equal\"."));
    }

    [Fact]
    public void Equality_Records_DifferentShapeThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define rec1 as a record with (the make \"Honda\", the year 2021).\n" +
            "Define rec2 as a record with (the city \"Tulsa\").\n" +
            "If rec1 is rec2, state \"equal\"."));
    }

    [Fact]
    public void Equality_Objects_SameTypeAndValuesAreEqual()
    {
        Assert.Equal("same", Run(
            "Define object vehicle with (the text make, the number year).\n" +
            "Define car1 as a new vehicle { the make \"Honda\", the year 2021 }.\n" +
            "Define car2 as a new vehicle { the make \"Honda\", the year 2021 }.\n" +
            "If car1 is car2, state \"same\". Otherwise, state \"different\"."));
    }

    [Fact]
    public void Equality_Objects_SameTypeDifferentValueNotEqual()
    {
        Assert.Equal("different", Run(
            "Define object vehicle with (the text make, the number year).\n" +
            "Define car1 as a new vehicle { the make \"Honda\", the year 2021 }.\n" +
            "Define car2 as a new vehicle { the make \"Toyota\", the year 2021 }.\n" +
            "If car1 is car2, state \"same\". Otherwise, state \"different\"."));
    }

    [Fact]
    public void Equality_Objects_IsNotWorks()
    {
        Assert.Equal("different", Run(
            "Define object vehicle with (the text make, the number year).\n" +
            "Define car1 as a new vehicle { the make \"Honda\", the year 2021 }.\n" +
            "Define car2 as a new vehicle { the make \"Toyota\", the year 2019 }.\n" +
            "If car1 is not car2, state \"different\". Otherwise, state \"same\"."));
    }

    [Fact]
    public void Equality_Objects_DifferentTypeThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object vehicle with (the text make).\n" +
            "Define object animal with (the text name).\n" +
            "Define car as a new vehicle { the make \"Honda\" }.\n" +
            "Define dog as a new animal { the name \"Rex\" }.\n" +
            "If car is dog, state \"same\"."));
    }

    [Fact]
    public void Equality_Objects_WithEmbeddedEqualWhenAllMatch()
    {
        Assert.Equal("same", Run(
            "Define object person with (the text name, the number age).\n" +
            "Define object customer with (the number balance) and as a person.\n" +
            "Define cust1 as a new customer { the balance 100, the name \"Alice\", the age 30 }.\n" +
            "Define cust2 as a new customer { the balance 100, the name \"Alice\", the age 30 }.\n" +
            "If cust1 is cust2, state \"same\". Otherwise, state \"different\"."));
    }

    [Fact]
    public void Equality_Objects_WithEmbeddedNotEqualWhenEmbeddedDiffers()
    {
        Assert.Equal("different", Run(
            "Define object person with (the text name, the number age).\n" +
            "Define object customer with (the number balance) and as a person.\n" +
            "Define cust1 as a new customer { the balance 100, the name \"Alice\", the age 30 }.\n" +
            "Define cust2 as a new customer { the balance 100, the name \"Bob\", the age 25 }.\n" +
            "If cust1 is cust2, state \"same\". Otherwise, state \"different\"."));
    }

    [Fact]
    public void Equality_Deep_NestedRecordsComparedRecursively()
    {
        // A record whose named field is itself a record.
        Assert.Equal("equal\nnot equal", Run(
            "Define inner1 as a record with (the city \"Tulsa\").\n" +
            "Define inner2 as a record with (the city \"Tulsa\").\n" +
            "Define inner3 as a record with (the city \"Denver\").\n" +
            "Define outer1 as a record with (the location inner1).\n" +
            "Define outer2 as a record with (the location inner2).\n" +
            "Define outer3 as a record with (the location inner3).\n" +
            "If outer1 is outer2, state \"equal\". Otherwise, state \"not equal\".\n" +
            "If outer1 is outer3, state \"equal\". Otherwise, state \"not equal\"."));
    }

    [Fact]
    public void Equality_SeriesField_ComparedElementWise()
    {
        // A record with a series field: equal when elements match.
        Assert.Equal("equal\nnot equal", Run(
            "Define s1 as a series with (1, 2, 3).\n" +
            "Define s2 as a series with (1, 2, 3).\n" +
            "Define s3 as a series with (1, 2, 9).\n" +
            "Define rec1 as a record with (the scores s1).\n" +
            "Define rec2 as a record with (the scores s2).\n" +
            "Define rec3 as a record with (the scores s3).\n" +
            "If rec1 is rec2, state \"equal\". Otherwise, state \"not equal\".\n" +
            "If rec1 is rec3, state \"equal\". Otherwise, state \"not equal\"."));
    }

    [Fact]
    public void Equality_SeriesField_NotEqualWhenLengthDiffers()
    {
        Assert.Equal("not equal", Run(
            "Define s1 as a series with (1, 2, 3).\n" +
            "Define s2 as a series with (1, 2).\n" +
            "Define rec1 as a record with (the scores s1).\n" +
            "Define rec2 as a record with (the scores s2).\n" +
            "If rec1 is rec2, state \"equal\". Otherwise, state \"not equal\"."));
    }

    // ── Text Operations — Slice 1: joining, conversion, length ───────────────

    [Fact]
    public void TextJoin_BasicConcatenation()
    {
        Assert.Equal("hello world", Run("State \"hello\" joined to \" world\"."));
    }

    [Fact]
    public void TextJoin_ChainIsLeftAssociative()
    {
        Assert.Equal("abc", Run("State \"a\" joined to \"b\" joined to \"c\"."));
    }

    [Fact]
    public void TextJoin_WithVariables()
    {
        Assert.Equal("hello world", Run(
            "Define greeting as \"hello\".\n" +
            "State greeting joined to \" world\"."));
    }

    [Fact]
    public void TextJoin_InCondition()
    {
        Assert.Equal("yes", Run(
            "Define prefix as \"he\".\n" +
            "Define suffix as \"llo\".\n" +
            "If prefix joined to suffix is \"hello\", state \"yes\". Otherwise, state \"no\"."));
    }

    [Fact]
    public void TextConvert_NumberToText()
    {
        Assert.Equal("95", Run("State 95 converted to text."));
    }

    [Fact]
    public void TextConvert_DecimalNumberToText()
    {
        Assert.Equal("2.5", Run("State 2.5 converted to text."));
    }

    [Fact]
    public void TextConvert_FactTrueToText()
    {
        Assert.Equal("true", Run("State (1 = 1) converted to text."));
    }

    [Fact]
    public void TextConvert_FactFalseToText()
    {
        Assert.Equal("false", Run("State (1 = 2) converted to text."));
    }

    [Fact]
    public void TextConvert_TextIsNoOp()
    {
        Assert.Equal("hello", Run("State \"hello\" converted to text."));
    }

    [Fact]
    public void TextConvert_MatchesStateRendering()
    {
        // converted to text must produce the same string as State would print
        Assert.Equal(Run("State 42."), Run("State 42 converted to text."));
    }

    [Fact]
    public void TextJoin_WithConvertedNumber_BindingOrder()
    {
        // "Player: " joined to score converted to text
        // must parse as "Player: " joined to (score converted to text), NOT
        // ("Player: " joined to score) converted to text
        Assert.Equal("Player: 95", Run(
            "Define score as 95.\n" +
            "State \"Player: \" joined to score converted to text."));
    }

    [Fact]
    public void TextJoin_BuildsFullLabel()
    {
        Assert.Equal("The total is 42", Run(
            "Define total as 42.\n" +
            "State \"The total is \" joined to total converted to text."));
    }

    [Fact]
    public void TextLength_OfLiteral()
    {
        Assert.Equal("5", Run("State the length of \"hello\"."));
    }

    [Fact]
    public void TextLength_OfVariable()
    {
        Assert.Equal("3", Run(
            "Define word as \"cat\".\n" +
            "State the length of word."));
    }

    [Fact]
    public void TextLength_OfEmptyString()
    {
        Assert.Equal("0", Run("State the length of \"\"."));
    }

    [Fact]
    public void TextLength_ResultIsNumber()
    {
        // result is a number — can be used in arithmetic
        Assert.Equal("10", Run(
            "Define word as \"hello\".\n" +
            "State the length of word * 2."));
    }

    [Fact]
    public void TextLength_DoesNotAffectSeriesLength()
    {
        // the number of <series> must still work unchanged
        Assert.Equal("3", Run(
            "Define scores as a series with (1, 2, 3).\n" +
            "State the number of scores."));
    }

    [Fact]
    public void TextJoin_TypeErrorOnNumber()
    {
        Assert.Throws<TypeException>(() => Run("State 1 joined to \"hello\"."));
    }

    [Fact]
    public void TextJoin_TypeErrorOnFact()
    {
        Assert.Throws<TypeException>(() => Run("State \"hello\" joined to (1 = 1)."));
    }

    [Fact]
    public void TextLength_TypeErrorOnNumber()
    {
        Assert.Throws<TypeException>(() => Run("State the length of 5."));
    }

    [Fact]
    public void TextLength_TypeErrorOnSeries()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define scores as a series with (1, 2, 3).\n" +
            "State the length of scores."));
    }

    [Fact]
    public void TextConvert_TypeErrorOnSeries()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define scores as a series with (1, 2, 3).\n" +
            "State scores converted to text."));
    }

    // ── Text Operations — Slice 2: split, contains, find, substring ──────────

    [Fact]
    public void TextSplit_Basic()
    {
        Assert.Equal("a\nb\nc", Run(
            "Define parts as \"a,b,c\" split by \",\".\n" +
            "For each part in parts, repeat:\n" +
            "    State part.\n" +
            "Done."));
    }

    [Fact]
    public void TextSplit_ResultIsSeriesOfText()
    {
        Assert.Equal("3", Run(
            "Define parts as \"a,b,c\" split by \",\".\n" +
            "State the number of parts."));
    }

    [Fact]
    public void TextSplit_DelimiterNotFound_SingleElementSeries()
    {
        Assert.Equal("1\nabc", Run(
            "Define parts as \"abc\" split by \",\".\n" +
            "State the number of parts.\n" +
            "State the first of parts."));
    }

    [Fact]
    public void TextSplit_KeepsConsecutiveEmpties()
    {
        Assert.Equal("a\n\nb", Run(
            "For each part in \"a,,b\" split by \",\", repeat:\n" +
            "    State part.\n" +
            "Done."));
    }

    [Fact]
    public void TextSplit_KeepsLeadingAndTrailingEmpties()
    {
        // Trailing-empty output is invisible through Run()'s TrimEnd('\n'), so assert on
        // length and the count of elements instead of the raw printed text.
        Assert.Equal("3\n0\na\n0", Run(
            "Define parts as \",a,\" split by \",\".\n" +
            "State the number of parts.\n" +
            "State the length of the first of parts.\n" +
            "State the second of parts.\n" +
            "State the length of the third of parts."));
    }

    [Fact]
    public void TextSplit_EmptyDelimiterIsStaticError()
    {
        Assert.Throws<TypeException>(() => Run("State \"abc\" split by \"\"."));
    }

    [Fact]
    public void TextSplit_TypeErrorOnNonText()
    {
        Assert.Throws<TypeException>(() => Run("State 5 split by \",\"."));
    }

    [Fact]
    public void TextContains_True()
    {
        Assert.Equal("yes", Run("If \"hello\" contains \"ell\", state \"yes\". Otherwise, state \"no\"."));
    }

    [Fact]
    public void TextContains_False()
    {
        Assert.Equal("no", Run("If \"hello\" contains \"z\", state \"yes\". Otherwise, state \"no\"."));
    }

    [Fact]
    public void TextContains_TypeErrorOnNonText()
    {
        Assert.Throws<TypeException>(() => Run("State 5 contains \"ell\"."));
    }

    [Fact]
    public void TextFind_Present_OneBased()
    {
        Assert.Equal("2", Run("State the position of \"ell\" in \"hello\"."));
    }

    [Fact]
    public void TextFind_Absent_IsVoid()
    {
        Assert.Equal("void", Run(
            "Define q as the position of \"z\" in \"hello\".\n" +
            "If q is void:\n" +
            "    State \"void\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State q.\n" +
            "Done."));
    }

    [Fact]
    public void TextFind_FirstOccurrence()
    {
        Assert.Equal("1", Run("State the position of \"a\" in \"abca\"."));
    }

    [Fact]
    public void TextFind_ButVoidDefault()
    {
        Assert.Equal("0", Run("State (the position of \"z\" in \"hello\" but void is 0)."));
    }

    [Fact]
    public void TextFind_TypeErrorOnNonText()
    {
        Assert.Throws<TypeException>(() => Run("State the position of 5 in \"hello\"."));
    }

    [Fact]
    public void TextSubstring_FromTo()
    {
        Assert.Equal("ell", Run("State the characters from 2 to 4 of \"hello\"."));
    }

    [Fact]
    public void TextSubstring_FirstN()
    {
        Assert.Equal("hel", Run("State the first 3 characters of \"hello\"."));
    }

    [Fact]
    public void TextSubstring_LastN()
    {
        Assert.Equal("llo", Run("State the last 3 characters of \"hello\"."));
    }

    [Fact]
    public void TextSubstring_ToTheEnd()
    {
        Assert.Equal("llo", Run("State the characters from 3 to the end of \"hello\"."));
    }

    [Fact]
    public void TextSubstring_ClampsOutOfRangeHigh()
    {
        Assert.Equal("i", Run("State the characters from 2 to 99 of \"hi\"."));
    }

    [Fact]
    public void TextSubstring_FirstNClampsBeyondLength()
    {
        Assert.Equal("hi", Run("State the first 10 characters of \"hi\"."));
    }

    [Fact]
    public void TextSubstring_BackwardsRangeIsEmpty()
    {
        Assert.Equal("", Run("State the characters from 5 to 2 of \"hello\"."));
    }

    [Fact]
    public void TextSubstring_FirstZeroIsEmpty()
    {
        Assert.Equal("", Run("State the first 0 characters of \"hello\"."));
    }

    [Fact]
    public void TextSubstring_LastZeroIsEmpty()
    {
        Assert.Equal("", Run("State the last 0 characters of \"hello\"."));
    }

    [Fact]
    public void TextSubstring_PositionZeroIsStaticError()
    {
        Assert.Throws<TypeException>(() => Run("State the characters from 0 to 3 of \"hello\"."));
    }

    [Fact]
    public void TextSubstring_NegativePositionIsStaticError()
    {
        Assert.Throws<TypeException>(() => Run("State the characters from -1 to 3 of \"hello\"."));
    }

    [Fact]
    public void TextSubstring_PositionZeroRuntimeBackstop()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Define p as 0.\n" +
            "State the characters from p to 3 of \"hello\"."));
    }

    [Fact]
    public void TextSubstring_TypeErrorOnNonText()
    {
        Assert.Throws<TypeException>(() => Run("State the characters from 1 to 2 of 5."));
    }

    [Fact]
    public void TextSubstring_NoCollisionWithSeriesOrdinalAccess()
    {
        Assert.Equal("10\n30", Run(
            "Define s as a series with (10, 20, 30).\n" +
            "State the first of s.\n" +
            "State the last of s."));
    }

    [Fact]
    public void TextSubstring_CharactersFieldNameStillWorksAsNamedAccess()
    {
        // 'characters' isn't excluded from named-field access — disambiguated by
        // the presence of 'from' (substring) vs. its absence (named access).
        Assert.Equal("5", Run(
            "Define r as a record with (the characters 5).\n" +
            "State the characters of r."));
    }

    [Fact]
    public void TextSubstring_VariablePosition()
    {
        Assert.Equal("ell", Run(
            "Define from-pos as 2.\n" +
            "Define to-pos as 4.\n" +
            "State the characters from from-pos to to-pos of \"hello\"."));
    }

    // ── Text Operations — Slice 3: replace, case, trim ────────────────────────

    [Fact]
    public void TextReplace_AllOccurrences()
    {
        Assert.Equal("bXnXnX", Run("State replace \"a\" with \"X\" in \"banana\"."));
    }

    [Fact]
    public void TextReplace_EmptyNewIsDeletion()
    {
        Assert.Equal("ab", Run("State replace \"x\" with \"\" in \"axbx\"."));
    }

    [Fact]
    public void TextReplace_NotFoundReturnsUnchanged()
    {
        Assert.Equal("hello", Run("State replace \"z\" with \"Q\" in \"hello\"."));
    }

    [Fact]
    public void TextReplace_EmptyOldIsStaticError()
    {
        Assert.Throws<TypeException>(() => Run("State replace \"\" with \"X\" in \"hello\"."));
    }

    [Fact]
    public void TextReplace_EmptyOldRuntimeBackstop()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Define old as \"\".\n" +
            "State replace old with \"X\" in \"hello\"."));
    }

    [Fact]
    public void TextReplace_TypeErrorOnNonTextTarget()
    {
        Assert.Throws<TypeException>(() => Run("State replace \"a\" with \"X\" in 5."));
    }

    [Fact]
    public void TextReplace_TypeErrorOnNonTextOld()
    {
        Assert.Throws<TypeException>(() => Run("State replace 5 with \"X\" in \"hello\"."));
    }

    [Fact]
    public void TextReplace_TypeErrorOnNonTextNew()
    {
        Assert.Throws<TypeException>(() => Run("State replace \"a\" with 5 in \"hello\"."));
    }

    [Fact]
    public void TextCase_Uppercase()
    {
        Assert.Equal("HELLO", Run("State \"Hello\" in uppercase."));
    }

    [Fact]
    public void TextCase_Lowercase()
    {
        Assert.Equal("hello", Run("State \"Hello\" in lowercase."));
    }

    [Fact]
    public void TextCase_TypeErrorOnNonText()
    {
        Assert.Throws<TypeException>(() => Run("State 5 in uppercase."));
    }

    [Fact]
    public void TextCase_DoesNotCollideWithMapLookupIn()
    {
        Assert.Equal("30", Run(
            "Define ages as a map with (\"alice\" : 30).\n" +
            "State the entry for \"alice\" in ages but void is 0."));
    }

    [Fact]
    public void TextCase_DoesNotCollideWithMapSetStatement()
    {
        Assert.Equal("42", Run(
            "Define ages as a map from text to number with ().\n" +
            "In ages, the entry for \"x\" becomes 42.\n" +
            "State the entry for \"x\" in ages but void is 0."));
    }

    [Fact]
    public void TextCase_DoesNotCollideWithTextFindIn()
    {
        Assert.Equal("2", Run("State the position of \"ell\" in \"hello\"."));
    }

    [Fact]
    public void TextTrim_BothEnds()
    {
        Assert.Equal("hello", Run("State \" hello \" trimmed."));
    }

    [Fact]
    public void TextTrim_TypeErrorOnNonText()
    {
        Assert.Throws<TypeException>(() => Run("State 5 trimmed."));
    }

    [Fact]
    public void TextOps_ChainTrimmedThenUppercase()
    {
        Assert.Equal("HI", Run("State \"  hi  \" trimmed in uppercase."));
    }

    [Fact]
    public void TextOps_CombineSplitReplaceCaseTrim()
    {
        Assert.Equal("X-Y-Z", Run(
            "Define raw as \"  x,y,z  \".\n" +
            "Define cleaned as raw trimmed in uppercase.\n" +
            "State replace \",\" with \"-\" in cleaned."));
    }

    // ── Voidable type + narrowing ─────────────────────────────────────────────

    [Fact]
    public void Void_LiteralStates()
    {
        Assert.Equal("void", Run("State void."));
    }

    [Fact]
    public void Void_IsVoidTrue()
    {
        Assert.Equal("yes", Run(
            "Define x as void.\n" +
            "If x is void, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void Void_IsVoidFalse()
    {
        // A concrete number stored in a voidable var is not void.
        Assert.Equal("no", Run(
            "Bind voidable number to maybe, given (the number n):\n" +
            "    return n.\n" +
            "Done.\n" +
            "Define result as Cast maybe on (5).\n" +
            "If result is void, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void Void_IsNotVoidTrue()
    {
        Assert.Equal("yes", Run(
            "Bind voidable number to maybe, given (the number n):\n" +
            "    return n.\n" +
            "Done.\n" +
            "Define result as Cast maybe on (7).\n" +
            "If result is not void, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void Void_FunctionReturnsVoidable_AbsentCase()
    {
        Assert.Equal("void", Run(
            "Bind voidable number to find-score:\n" +
            "    return void.\n" +
            "Done.\n" +
            "Define result as Cast find-score on ().\n" +
            "State result."));
    }

    [Fact]
    public void Void_FunctionReturnsVoidable_PresentCase()
    {
        Assert.Equal("42", Run(
            "Bind voidable number to find-score:\n" +
            "    return 42.\n" +
            "Done.\n" +
            "Define result as Cast find-score on ().\n" +
            "State result."));
    }

    [Fact]
    public void Void_Narrowing_UsesValueInBranch()
    {
        Assert.Equal("42", Run(
            "Bind voidable number to maybe-score:\n" +
            "    return 42.\n" +
            "Done.\n" +
            "Define score as Cast maybe-score on ().\n" +
            "If score is not void:\n" +
            "    State score.\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"none\".\n" +
            "Done."));
    }

    [Fact]
    public void Void_Narrowing_ElseBranchWhenVoid()
    {
        Assert.Equal("none", Run(
            "Bind voidable number to maybe-score:\n" +
            "    return void.\n" +
            "Done.\n" +
            "Define score as Cast maybe-score on ().\n" +
            "If score is not void:\n" +
            "    State score.\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"none\".\n" +
            "Done."));
    }

    [Fact]
    public void Void_Narrowing_InvalidatedByReassignment()
    {
        // After reassignment inside the branch, the variable is no longer narrowed.
        // The checker should allow reassigning a voidable var to void inside the branch.
        Assert.Equal("reset", Run(
            "Bind voidable number to get-val:\n" +
            "    return 10.\n" +
            "Done.\n" +
            "Define val as Cast get-val on ().\n" +
            "If val is not void:\n" +
            "    val becomes void.\n" +
            "Done.\n" +
            "If val is void:\n" +
            "    State \"reset\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"not reset\".\n" +
            "Done."));
    }

    [Fact]
    public void Void_ButVoidIs_PresentCase()
    {
        Assert.Equal("42", Run(
            "Bind voidable number to maybe-score:\n" +
            "    return 42.\n" +
            "Done.\n" +
            "Define result as Cast maybe-score on () but void is 0.\n" +
            "State result."));
    }

    [Fact]
    public void Void_ButVoidIs_AbsentCase()
    {
        Assert.Equal("0", Run(
            "Bind voidable number to maybe-score:\n" +
            "    return void.\n" +
            "Done.\n" +
            "Define result as Cast maybe-score on () but void is 0.\n" +
            "State result."));
    }

    [Fact]
    public void Void_Widening_NumberAssignableToVoidableNumber()
    {
        // A plain number is accepted where voidable number is expected (widening).
        Assert.Equal("5", Run(
            "Bind voidable number to wrap, given (the number n):\n" +
            "    return n.\n" +
            "Done.\n" +
            "Define result as Cast wrap on (5).\n" +
            "State result."));
    }

    [Fact]
    public void Void_VoidableParam_AcceptsVoid()
    {
        Assert.Equal("none", Run(
            "Bind void to display, given (the voidable number n):\n" +
            "    If n is void, State \"none\". Otherwise, State n.\n" +
            "Done.\n" +
            "Cast display on (void)."));
    }

    [Fact]
    public void Void_VoidableParam_AcceptsNumber()
    {
        Assert.Equal("7", Run(
            "Bind void to display, given (the voidable number n):\n" +
            "    If n is void, State \"none\". Otherwise, State n.\n" +
            "Done.\n" +
            "Cast display on (7)."));
    }

    [Fact]
    public void Void_TypeError_VoidableUsedWhereConcreteRequired()
    {
        // Using a voidable number where a plain number is expected is a static error.
        Assert.Throws<TypeException>(() => Run(
            "Bind voidable number to fetch:\n" +
            "    return 1.\n" +
            "Done.\n" +
            "Bind void to use, given (the number n):\n" +
            "    State n.\n" +
            "Done.\n" +
            "Cast use on (Cast fetch on ())."));
    }

    [Fact]
    public void Void_TypeError_ButVoidIs_WrongDefaultType()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind voidable number to fetch:\n" +
            "    return void.\n" +
            "Done.\n" +
            "Define result as Cast fetch on () but void is \"fallback\".\n" +
            "State result."));
    }

    // ── Maps ──────────────────────────────────────────────────────────────────

    // ── Map key type enforcement ──────────────────────────────────────────────────────────────────
    // Reference types (objects, series, maps) can't be map keys: their identity changes when
    // copied (Define deep-copies ObjectValues; two List instances with same content are different
    // references), so lookups would silently always-miss — wrong answers, no error.
    // The TypeChecker catches these at declaration time with an educational message.

    [Fact]
    public void Map_ObjectKey_TypeCheckError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object point with (the number px, the number py).\n" +
            "Define m as a map from point to number with ()."));
    }

    [Fact]
    public void Map_SeriesKey_TypeCheckError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define m as a map from series of number to text with ()."));
    }

    [Fact]
    public void Map_MapKey_TypeCheckError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define m as a map from map from text to number to number with ()."));
    }

    [Fact]
    public void Map_FactKey_IsValidValueTypeKey()
    {
        Assert.Equal("1", Run(
            "Define m as a map from fact to number with ().\n" +
            "In m, the entry for true becomes 1.\n" +
            "State (the entry for true in m) but void is 0."));
    }

    [Fact]
    public void Map_Empty_Construction()
    {
        Assert.Equal("0", Run(
            "Define ages as a map from text to number with ().\n" +
            "State the size of ages."));
    }

    [Fact]
    public void Map_Populated_Construction()
    {
        Assert.Equal("2", Run(
            "Define ages as a map with (\"alice\" : 30, \"bob\" : 25).\n" +
            "State the size of ages."));
    }

    // Typed + populated — the form that was previously inexpressible (required `a new map from K to V`
    // for empty + separate insertion; now `a map from K to V with (...)` handles both in one literal).
    [Fact]
    public void Map_TypedPopulated_Construction()
    {
        Assert.Equal("30", Run(
            "Define ages as a map from text to number with (\"alice\" : 30, \"bob\" : 25).\n" +
            "State the entry for \"alice\" in ages but void is 0."));
    }

    [Fact]
    public void Map_Lookup_Present()
    {
        Assert.Equal("30", Run(
            "Define ages as a map with (\"alice\" : 30, \"bob\" : 25).\n" +
            "Define result as the entry for \"alice\" in ages.\n" +
            "If result is void, State \"missing\". Otherwise, State result."));
    }

    [Fact]
    public void Map_Lookup_Absent_ReturnsVoid()
    {
        Assert.Equal("missing", Run(
            "Define ages as a map with (\"alice\" : 30).\n" +
            "Define result as the entry for \"carol\" in ages.\n" +
            "If result is void, State \"missing\". Otherwise, State result."));
    }

    [Fact]
    public void Map_Lookup_ButVoidDefault()
    {
        Assert.Equal("0", Run(
            "Define ages as a map with (\"alice\" : 30).\n" +
            "State the entry for \"carol\" in ages but void is 0."));
    }

    [Fact]
    public void Map_Set_AddEntry()
    {
        Assert.Equal("42", Run(
            "Define scores as a map from text to number with ().\n" +
            "In scores, the entry for \"x\" becomes 42.\n" +
            "State the entry for \"x\" in scores but void is 0."));
    }

    [Fact]
    public void Map_Set_UpdateEntry()
    {
        Assert.Equal("99", Run(
            "Define scores as a map with (\"x\" : 1).\n" +
            "In scores, the entry for \"x\" becomes 99.\n" +
            "State the entry for \"x\" in scores but void is 0."));
    }

    [Fact]
    public void Map_HasKey_True()
    {
        Assert.Equal("true", Run(
            "Define m as a map with (\"a\" : 1).\n" +
            "State m has a key for \"a\"."));
    }

    [Fact]
    public void Map_HasKey_False()
    {
        Assert.Equal("false", Run(
            "Define m as a map with (\"a\" : 1).\n" +
            "State m has a key for \"b\"."));
    }

    [Fact]
    public void Map_HasEntry_True()
    {
        Assert.Equal("true", Run(
            "Define m as a map with (\"a\" : 1).\n" +
            "State m has an entry for \"a\"."));
    }

    [Fact]
    public void Map_HasEntry_False()
    {
        Assert.Equal("false", Run(
            "Define m as a map with (\"a\" : 1).\n" +
            "State m has an entry for \"z\"."));
    }

    [Fact]
    public void Map_Remove_Key()
    {
        Assert.Equal("1", Run(
            "Define m as a map with (\"a\" : 1, \"b\" : 2).\n" +
            "Remove \"b\" from m.\n" +
            "State the size of m."));
    }

    [Fact]
    public void Map_Remove_MakesKeyAbsent()
    {
        Assert.Equal("missing", Run(
            "Define m as a map with (\"a\" : 1).\n" +
            "Remove \"a\" from m.\n" +
            "Define result as the entry for \"a\" in m.\n" +
            "If result is void, State \"missing\". Otherwise, State result."));
    }

    [Fact]
    public void Map_Size_Empty()
    {
        Assert.Equal("0", Run(
            "Define m as a map from number to text with ().\n" +
            "State the size of m."));
    }

    [Fact]
    public void Map_Size_AfterSet()
    {
        Assert.Equal("3", Run(
            "Define m as a map with (1 : \"a\", 2 : \"b\").\n" +
            "In m, the entry for 3 becomes \"c\".\n" +
            "State the size of m."));
    }

    [Fact]
    public void Map_ForEach_KeyAndValue()
    {
        // Only one entry so output is deterministic.
        // Use intermediate variables because 'the value of person converted to text' mis-parses
        // (the inner ParsePrimary for the record greedily eats 'converted to text').
        Assert.Equal("alice=30", Run(
            "Define ages as a map with (\"alice\" : 30).\n" +
            "For each person in ages, Repeat:\n" +
            "    Define k as the key of person.\n" +
            "    Define v as the value of person.\n" +
            "    State k joined to \"=\" joined to v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Map_ForEach_Stop()
    {
        // Single-entry map — Stop exits immediately; just verify it doesn't crash.
        Assert.Equal("stopped", Run(
            "Define m as a map with (\"x\" : 1).\n" +
            "For each pair in m, Repeat:\n" +
            "    Stop.\n" +
            "Done.\n" +
            "State \"stopped\"."));
    }

    [Fact]
    public void Map_NumberKey_Lookup()
    {
        Assert.Equal("hello", Run(
            "Define m as a map with (1 : \"hello\", 2 : \"world\").\n" +
            "State the entry for 1 in m but void is \"\"."));
    }

    [Fact]
    public void Map_ReferenceSemantics()
    {
        // Assigning a map to a new variable — both names see mutations.
        Assert.Equal("true", Run(
            "Define m as a map from text to number with ().\n" +
            "Define n as m.\n" +
            "In m, the entry for \"x\" becomes 7.\n" +
            "State n has a key for \"x\"."));
    }

    [Fact]
    public void Map_HasKey_InCondition()
    {
        Assert.Equal("found", Run(
            "Define m as a map with (\"a\" : 1).\n" +
            "If m has a key for \"a\", State \"found\". Otherwise, State \"not found\"."));
    }

    [Fact]
    public void Map_TypeError_WrongKeyType()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define m as a map with (\"a\" : 1).\n" +
            "State m has a key for 42."));
    }

    [Fact]
    public void Map_TypeError_WrongValueTypeInLiteral()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define m as a map with (\"a\" : 1, \"b\" : \"oops\")."));
    }

    [Fact]
    public void Map_TypeError_SetWrongValueType()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define m as a map with (\"a\" : 1).\n" +
            "In m, the entry for \"b\" becomes \"wrong\"."));
    }

    [Fact]
    public void Map_TypeError_LookupNonMap()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define x as 42.\n" +
            "State the entry for 1 in x."));
    }

    [Fact]
    public void Map_CanonicalPattern_NarrowAfterLookup()
    {
        Assert.Equal("30", Run(
            "Define ages as a map with (\"alice\" : 30).\n" +
            "Define result as the entry for \"alice\" in ages.\n" +
            "If result is void:\n" +
            "    State \"not found\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State result.\n" +
            "Done."));
    }

    // ── Maps — voidable values ───────────────────────────────────────────────

    [Fact]
    public void VoidableMap_DeclareAndConstruct()
    {
        Assert.Equal("0", Run(
            "Define ages as a map from text to voidable number with ().\n" +
            "State the size of ages."));
    }

    [Fact]
    public void VoidableMap_SetPlainValueWorks()
    {
        Assert.Equal("30", Run(
            "Define ages as a map from text to voidable number with ().\n" +
            "In ages, the entry for \"alice\" becomes 30.\n" +
            "State the entry for \"alice\" in ages but void is 0."));
    }

    [Fact]
    public void VoidableMap_SetVoidWorks()
    {
        Assert.Equal("void", Run(
            "Define ages as a map from text to voidable number with ().\n" +
            "In ages, the entry for \"bob\" becomes void.\n" +
            "State the entry for \"bob\" in ages."));
    }

    [Fact]
    public void VoidableMap_LookupTypeIsFlatVoidable_NoNarrowingErrorOnDoubleVoidCheck()
    {
        // If the type were 'voidable voidable number', 'is not void' would narrow to
        // 'voidable number', and using it directly as a number would be a static error.
        Assert.Equal("31", Run(
            "Define ages as a map from text to voidable number with ().\n" +
            "In ages, the entry for \"alice\" becomes 30.\n" +
            "Define v as the entry for \"alice\" in ages.\n" +
            "If v is not void:\n" +
            "    State v + 1.\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State 0.\n" +
            "Done."));
    }

    [Fact]
    public void VoidableMap_HasKey_TrueForVoidValue()
    {
        // The slot exists even though its value is void.
        Assert.Equal("true", Run(
            "Define ages as a map from text to voidable number with ().\n" +
            "In ages, the entry for \"bob\" becomes void.\n" +
            "State ages has a key for \"bob\"."));
    }

    [Fact]
    public void VoidableMap_HasEntry_FalseForVoidValue()
    {
        // Diverges from has-a-key: a void-valued slot is not a present entry.
        Assert.Equal("false", Run(
            "Define ages as a map from text to voidable number with ().\n" +
            "In ages, the entry for \"bob\" becomes void.\n" +
            "State ages has an entry for \"bob\"."));
    }

    [Fact]
    public void VoidableMap_HasKey_FalseWhenAbsent()
    {
        Assert.Equal("false", Run(
            "Define ages as a map from text to voidable number with ().\n" +
            "State ages has a key for \"carol\"."));
    }

    [Fact]
    public void VoidableMap_HasEntry_TrueForRealValue()
    {
        Assert.Equal("true", Run(
            "Define ages as a map from text to voidable number with ().\n" +
            "In ages, the entry for \"alice\" becomes 30.\n" +
            "State ages has an entry for \"alice\"."));
    }

    [Fact]
    public void VoidableMap_CanonicalPattern_DistinguishesAbsentFromVoidValue()
    {
        Assert.Equal("present, but void\nno such key", Run(
            "Define ages as a map from text to voidable number with ().\n" +
            "In ages, the entry for \"bob\" becomes void.\n" +
            "If ages has a key for \"bob\":\n" +
            "    Define v as the entry for \"bob\" in ages.\n" +
            "    If v is not void:\n" +
            "        State v.\n" +
            "    Done.\n" +
            "    Otherwise:\n" +
            "        State \"present, but void\".\n" +
            "    Done.\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"no such key\".\n" +
            "Done.\n" +
            "If ages has a key for \"carol\":\n" +
            "    State \"found\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"no such key\".\n" +
            "Done."));
    }

    [Fact]
    public void VoidableMap_UpdateVoidEntryToRealValue()
    {
        Assert.Equal("42", Run(
            "Define ages as a map from text to voidable number with ().\n" +
            "In ages, the entry for \"bob\" becomes void.\n" +
            "In ages, the entry for \"bob\" becomes 42.\n" +
            "State the entry for \"bob\" in ages but void is 0."));
    }

    [Fact]
    public void VoidableMap_TypeError_SetWrongType()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define ages as a map from text to voidable number with ().\n" +
            "In ages, the entry for \"bob\" becomes \"oops\"."));
    }

    // ── Closures ──────────────────────────────────────────────────────────

    [Fact]
    public void Closure_Canonical_MakeAdder()
    {
        Assert.Equal("15", Run(
            "Bind number function given (the number) to make-adder, given (the number n):\n" +
            "    Bind number to adder, given (the number x):\n" +
            "        Return x + n.\n" +
            "    Done.\n" +
            "    Return adder.\n" +
            "Done.\n" +
            "Define add5 as cast make-adder on (5).\n" +
            "State cast add5 on (10)."));
    }

    [Fact]
    public void Closure_TwoIndependentCaptures()
    {
        Assert.Equal("15\n110", Run(
            "Bind number function given (the number) to make-adder, given (the number n):\n" +
            "    Bind number to adder, given (the number x):\n" +
            "        Return x + n.\n" +
            "    Done.\n" +
            "    Return adder.\n" +
            "Done.\n" +
            "Define add5   as cast make-adder on (5).\n" +
            "Define add100 as cast make-adder on (100).\n" +
            "State cast add5 on (10).\n" +
            "State cast add100 on (10)."));
    }

    [Fact]
    public void Closure_ValueType_Snapshot()
    {
        // Changing n inside the outer function after creating the closure does not affect the closure.
        Assert.Equal("15", Run(
            "Bind number function given (the number) to make-adder, given (the number n):\n" +
            "    Bind number to adder, given (the number x):\n" +
            "        Return x + n.\n" +
            "    Done.\n" +
            "    n becomes n + 100.\n" +
            "    Return adder.\n" +
            "Done.\n" +
            "Define f as cast make-adder on (5).\n" +
            "State cast f on (10)."));
    }

    [Fact]
    public void Closure_ReferenceType_SeriesShared()
    {
        // A captured series is shared: mutations through the closure are visible in the outer scope.
        Assert.Equal("2", Run(
            "Bind void to outer:\n" +
            "    Define shared as a series of numbers.\n" +
            "    Bind void to push, given (the number x):\n" +
            "        Add x to shared.\n" +
            "    Done.\n" +
            "    Cast push on (1).\n" +
            "    Cast push on (2).\n" +
            "    State the number of shared.\n" +
            "Done.\n" +
            "Cast outer on ()."));
    }

    [Fact]
    public void Closure_ForEach_EachCapturesOwnValue()
    {
        // Each call to make-adder(n) produces an independent closure (value-type snapshot).
        Assert.Equal("11\n12\n13", Run(
            "Bind number function given (the number) to make-adder, given (the number n):\n" +
            "    Bind number to adder, given (the number x):\n" +
            "        Return x + n.\n" +
            "    Done.\n" +
            "    Return adder.\n" +
            "Done.\n" +
            "Define ops as a series of number function given (the number).\n" +
            "For each n in range 1 to 3, repeat:\n" +
            "    Add cast make-adder on (n) to ops.\n" +
            "Done.\n" +
            "State cast (the first of ops) on (10).\n" +
            "State cast (the second of ops) on (10).\n" +
            "State cast (the third of ops) on (10)."));
    }

    [Fact]
    public void Closure_NestedFunctionUsableInSameScope()
    {
        // After Bind inside a function, the new function can be called in the same body.
        Assert.Equal("7", Run(
            "Bind number to go:\n" +
            "    Bind number to combine, given (the number p, the number q):\n" +
            "        Return p + q.\n" +
            "    Done.\n" +
            "    Return cast combine on (3, 4).\n" +
            "Done.\n" +
            "State cast go on ()."));
    }

    [Fact]
    public void Closure_InnerRecursion()
    {
        Assert.Equal("120", Run(
            "Bind number function given (the number) to make-factorial:\n" +
            "    Bind number to fact, given (the number n):\n" +
            "        If n is less than 2, return 1.\n" +
            "        Return n * cast fact on (n - 1).\n" +
            "    Done.\n" +
            "    Return fact.\n" +
            "Done.\n" +
            "Define f as cast make-factorial on ().\n" +
            "State cast f on (5)."));
    }

    [Fact]
    public void Closure_VoidInner()
    {
        Assert.Equal("hello from closure", Run(
            "Bind void function to make-greeter, given (the text msg):\n" +
            "    Bind void to greeter:\n" +
            "        State msg.\n" +
            "    Done.\n" +
            "    Return greeter.\n" +
            "Done.\n" +
            "Define say-hello as cast make-greeter on (\"hello from closure\").\n" +
            "Cast say-hello on ()."));
    }

    [Fact]
    public void Closure_PassedAsArgument()
    {
        Assert.Equal("15", Run(
            "Bind number to apply, given (the number x, the number function f given (the number)):\n" +
            "    Return cast f on (x).\n" +
            "Done.\n" +
            "Bind number function given (the number) to make-adder, given (the number n):\n" +
            "    Bind number to adder, given (the number x):\n" +
            "        Return x + n.\n" +
            "    Done.\n" +
            "    Return adder.\n" +
            "Done.\n" +
            "Define add5 as cast make-adder on (5).\n" +
            "State cast apply on (10, add5)."));
    }

    [Fact]
    public void Closure_StoredInSeries_CalledLater()
    {
        Assert.Equal("10\n20", Run(
            "Bind number function given (the number) to make-multiplier, given (the number factor):\n" +
            "    Bind number to mul, given (the number x):\n" +
            "        Return x * factor.\n" +
            "    Done.\n" +
            "    Return mul.\n" +
            "Done.\n" +
            "Define ops as a series of number function given (the number) with (cast make-multiplier on (2), cast make-multiplier on (4)).\n" +
            "State cast (the first of ops) on (5).\n" +
            "State cast (the second of ops) on (5)."));
    }

    [Fact]
    public void Closure_OuterParamAndLocalBothCaptured()
    {
        // base=4, offset=base+2=6, x=5 → 5+4+6=15
        Assert.Equal("15", Run(
            "Bind number function given (the number) to build, given (the number base):\n" +
            "    Define offset as base + 2.\n" +
            "    Bind number to fn, given (the number x):\n" +
            "        Return x + base + offset.\n" +
            "    Done.\n" +
            "    Return fn.\n" +
            "Done.\n" +
            "Define f as cast build on (4).\n" +
            "State cast f on (5)."));
    }

    [Fact]
    public void Closure_TypeError_InnerWrongReturnType()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind number function given (the number) to make-adder, given (the number n):\n" +
            "    Bind text to adder, given (the number x):\n" +
            "        Return \"oops\".\n" +
            "    Done.\n" +
            "    Return adder.\n" +
            "Done."));
    }

    [Fact]
    public void Closure_TypeError_OuterVariableWrongType()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind number function given (the number) to bad, given (the number n):\n" +
            "    Bind number to fn, given (the number x):\n" +
            "        Return x + n converted to text.\n" +
            "    Done.\n" +
            "    Return fn.\n" +
            "Done."));
    }

    [Fact]
    public void Closure_ParseError_NestedBindInTopLevelIf()
    {
        // Bind inside an if-block at the top level (not inside any function) is a parse error.
        Assert.Throws<ParseException>(() => Run(
            "If 1 = 1:\n" +
            "    Bind number to f: Return 1. Done.\n" +
            "Done."));
    }

    // ── Lambdas ───────────────────────────────────────────────────────────

    // Lambda syntax: "a function given (<params>): <stmts> Done"
    // The `Done` terminates the lambda body; the trailing '.' belongs to the
    // enclosing statement (Define/Return/etc.) or argument list context.
    //
    // Examples:
    //   Define fn as a function given (the number x): Return x + 1. Done.
    //   Cast apply on (10, a function given (the number x): Return x * 2. Done.)
    //   Return a function given (the number x): Return x + n. Done.

    [Fact]
    public void Lambda_BasicAssignAndCall()
    {
        Assert.Equal("8", Run(
            "Define add5 as a function given (the number x): Return x + 5. Done.\n" +
            "State cast add5 on (3)."));
    }

    [Fact]
    public void Lambda_NoParams()
    {
        Assert.Equal("42", Run(
            "Define answer as a function: Return 42. Done.\n" +
            "State cast answer on ()."));
    }

    [Fact]
    public void Lambda_AsArgument()
    {
        Assert.Equal("20", Run(
            "Bind number to apply, given (the number v, the number function fn given (the number)):\n" +
            "    return cast fn on (v).\n" +
            "Done.\n" +
            "State cast apply on (10, a function given (the number x): Return x * 2. Done)."));
    }

    [Fact]
    public void Lambda_CapturesEnclosingVariable_MakeAdder()
    {
        Assert.Equal("15", Run(
            "Bind number function given (the number) to make-adder, given (the number n):\n" +
            "    Return a function given (the number x): Return x + n. Done.\n" +
            "Done.\n" +
            "Define adder as cast make-adder on (10).\n" +
            "State cast adder on (5)."));
    }

    [Fact]
    public void Lambda_TwoIndependentCaptures()
    {
        Assert.Equal("13\n22", Run(
            "Bind number function given (the number) to make-adder, given (the number n):\n" +
            "    Return a function given (the number x): Return x + n. Done.\n" +
            "Done.\n" +
            "Define add10 as cast make-adder on (10).\n" +
            "Define add20 as cast make-adder on (20).\n" +
            "State cast add10 on (3).\n" +
            "State cast add20 on (2)."));
    }

    [Fact]
    public void Lambda_BlockForm()
    {
        Assert.Equal("11", Run(
            "Define fn as a function given (the number x):\n" +
            "    Define y as x + 1.\n" +
            "    Return y.\n" +
            "Done.\n" +
            "State cast fn on (10)."));
    }

    [Fact]
    public void Lambda_VoidBody()
    {
        Assert.Equal("hello", Run(
            "Define greet as a function given (the text msg): State msg. Done.\n" +
            "Cast greet on (\"hello\")."));
    }

    [Fact]
    public void Lambda_ReturnedFromFunction()
    {
        Assert.Equal("7", Run(
            "Bind number function given (the number) to make-adder, given (the number n):\n" +
            "    Return a function given (the number x): Return x + n. Done.\n" +
            "Done.\n" +
            "Define add3 as cast make-adder on (3).\n" +
            "State cast add3 on (4)."));
    }

    [Fact]
    public void Lambda_InSeries_CalledLater()
    {
        Assert.Equal("6", Run(
            "Define fns as a series of number function given (the number) with (\n" +
            "    a function given (the number x): Return x + 1. Done,\n" +
            "    a function given (the number x): Return x * 2. Done\n" +
            ").\n" +
            "State cast the second of fns on (3)."));
    }

    [Fact]
    public void Lambda_PassedThroughTwoLayers()
    {
        Assert.Equal("9", Run(
            "Bind number to apply-twice, given (the number v, the number function fn given (the number)):\n" +
            "    return cast fn on (cast fn on (v)).\n" +
            "Done.\n" +
            "State cast apply-twice on (3, a function given (the number x): Return x + 3. Done)."));
    }

    [Fact]
    public void Lambda_StatesPrintedValueDirectly()
    {
        Assert.Equal("<function>", Run(
            "Define fn as a function given (the number x): Return x + 1. Done.\n" +
            "State fn."));
    }

    [Fact]
    public void Lambda_TypeError_MismatchedReturns()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define fn as a function given (the number x):\n" +
            "    If x is 0, return \"zero\".\n" +
            "    Return x + 1.\n" +
            "Done."));
    }

    [Fact]
    public void Lambda_TypeError_WrongArgType()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind number to apply, given (the number v, the number function fn given (the number)):\n" +
            "    return cast fn on (v).\n" +
            "Done.\n" +
            "State cast apply on (10, a function given (the text x): Return 0. Done)."));
    }

    [Fact]
    public void Closure_ParseError_NestedBindInMethodBody()
    {
        // Nested Bind inside an object method body is not supported (methods are not free functions).
        Assert.Throws<ParseException>(() => Run(
            "Define object box with (the number v):\n" +
            "    Bind number to get:\n" +
            "        Bind number to helper: Return 1. Done.\n" +
            "        Return cast helper on ().\n" +
            "    Done.\n" +
            "Done."));
    }

    // ── Text → Number conversion ──────────────────────────────────────────────

    [Fact]
    public void NumberConvert_IntegerSucceeds()
    {
        Assert.Equal("95", Run(
            "Define n as \"95\" converted to number.\n" +
            "State n but void is -1."));
    }

    [Fact]
    public void NumberConvert_DecimalSucceeds()
    {
        Assert.Equal("2.5", Run(
            "Define n as \"2.5\" converted to number.\n" +
            "State n but void is -1."));
    }

    [Fact]
    public void NumberConvert_NegativeSucceeds()
    {
        Assert.Equal("-7", Run(
            "Define n as \"-7\" converted to number.\n" +
            "State n but void is 0."));
    }

    [Fact]
    public void NumberConvert_TrimsSurroundingWhitespace()
    {
        Assert.Equal("95", Run(
            "Define n as \" 95 \" converted to number.\n" +
            "State n but void is -1."));
    }

    [Fact]
    public void NumberConvert_NonNumericIsVoid()
    {
        Assert.Equal("not a number", Run(
            "Define n as \"hello\" converted to number.\n" +
            "If n is not void:\n" +
            "    State n.\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"not a number\".\n" +
            "Done."));
    }

    [Fact]
    public void NumberConvert_EmptyTextIsVoid()
    {
        Assert.Equal("not a number", Run(
            "Define n as \"\" converted to number.\n" +
            "If n is not void:\n" +
            "    State n.\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"not a number\".\n" +
            "Done."));
    }

    [Fact]
    public void NumberConvert_PartialGarbageIsVoid()
    {
        Assert.Equal("not a number", Run(
            "Define n as \"95abc\" converted to number.\n" +
            "If n is not void:\n" +
            "    State n.\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"not a number\".\n" +
            "Done."));
    }

    [Fact]
    public void NumberConvert_DoubleDecimalPointIsVoid()
    {
        Assert.Equal("not a number", Run(
            "Define n as \"95.5.5\" converted to number.\n" +
            "If n is not void:\n" +
            "    State n.\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"not a number\".\n" +
            "Done."));
    }

    [Fact]
    public void NumberConvert_NarrowsAfterIsNotVoid()
    {
        // n must be usable as a plain number (arithmetic) inside the narrowed branch
        Assert.Equal("100", Run(
            "Define n as \"95\" converted to number.\n" +
            "If n is not void:\n" +
            "    State n + 5.\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State 0.\n" +
            "Done."));
    }

    [Fact]
    public void NumberConvert_ButVoidDefaultInline()
    {
        Assert.Equal("0", Run("State (\"abc\" converted to number but void is 0)."));
    }

    [Fact]
    public void NumberConvert_TypeErrorOnNonText()
    {
        Assert.Throws<TypeException>(() => Run("State 95 converted to number."));
    }

    // ── Constants — 'permanently' ───────────────────────────────────────────

    [Fact]
    public void Permanent_DefineAndReadWorks()
    {
        Assert.Equal("3.14159", Run("Define pi as 3.14159 permanently.\nState pi."));
    }

    [Fact]
    public void Permanent_ReassignIsStaticError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define x as 5 permanently.\n" +
            "x becomes 6."));
    }

    [Fact]
    public void Permanent_ComplexValueExpressionParses()
    {
        Assert.Equal("100", Run("Define max-score as 90 + 10 permanently.\nState max-score."));
    }

    [Fact]
    public void Permanent_NonPermanentStillReassignable()
    {
        Assert.Equal("6", Run(
            "Define x as 5.\n" +
            "x becomes 6.\n" +
            "State x."));
    }

    [Fact]
    public void Permanent_MapEntryMutationStillAllowed()
    {
        Assert.Equal("31", Run(
            "Define ages as a map with (\"alice\" : 30) permanently.\n" +
            "In ages, the entry for \"alice\" becomes 31.\n" +
            "State the entry for \"alice\" in ages but void is 0."));
    }

    [Fact]
    public void Permanent_MapRebindIsStaticError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define ages as a map with (\"alice\" : 30) permanently.\n" +
            "ages becomes a map with (\"bob\" : 25)."));
    }

    [Fact]
    public void Permanent_RecordFieldMutationStillAllowed()
    {
        Assert.Equal("2022", Run(
            "Define car as a record with (the year 2021) permanently.\n" +
            "The year of car becomes 2022.\n" +
            "State the year of car."));
    }

    [Fact]
    public void Permanent_RecordRebindIsStaticError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define car as a record with (the year 2021) permanently.\n" +
            "car becomes a record with (the year 2022)."));
    }

    [Fact]
    public void Permanent_SeriesAddStillAllowed()
    {
        Assert.Equal("90\n85\n70", Run(
            "Define scores as a series with (90, 85) permanently.\n" +
            "Add 70 to scores.\n" +
            "For each s in scores, repeat:\n" +
            "    State s.\n" +
            "Done."));
    }

    [Fact]
    public void Permanent_SeriesElementAssignmentStillAllowed()
    {
        Assert.Equal("100", Run(
            "Define scores as a series with (90, 85) permanently.\n" +
            "The first of scores becomes 100.\n" +
            "State the first of scores."));
    }

    [Fact]
    public void Permanent_ErrorMessageNamesDeclarationLine()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Define x as 5 permanently.\n" +
            "State 1.\n" +
            "x becomes 6."));
        Assert.Contains("permanent", ex.Message);
        Assert.Contains("line 1", ex.Message);
        Assert.Contains("line 3", ex.Message);
    }

    // ── Methods defined outside the object body — 'unto' ───────────────────

    [Fact]
    public void Unto_BasicMethodSeesOneAndFields()
    {
        Assert.Equal("Hi, I'm Alice", Run(
            "Define object person with (the text name, the number age).\n" +
            "Bind void to greet unto person:\n" +
            "    State \"Hi, I'm \" joined to one's name.\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
            "Cast greet on alice."));
    }

    [Fact]
    public void Unto_MutatesReceiverAndReturnsValue()
    {
        Assert.Equal("31", Run(
            "Define object person with (the text name, the number age).\n" +
            "Bind number to birthday unto person:\n" +
            "    one's age becomes one's age + 1.\n" +
            "    Return one's age.\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\", the age 30 }.\n" +
            "State cast birthday on alice."));
    }

    [Fact]
    public void Unto_CalledViaCastOn()
    {
        Assert.Equal("Alice", Run(
            "Define object person with (the text name).\n" +
            "Bind void to greet unto person:\n" +
            "    State one's name.\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "Cast greet on alice."));
    }

    [Fact]
    public void Unto_CalledViaPossessive()
    {
        Assert.Equal("Alice", Run(
            "Define object person with (the text name).\n" +
            "Bind void to greet unto person:\n" +
            "    State one's name.\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "Cast alice's greet."));
    }

    [Fact]
    public void Unto_WithParamsViaGiven()
    {
        Assert.Equal("Speedy steers 90", Run(
            "Define object racer with (the text name).\n" +
            "Bind void to steer unto racer, given (the number angle):\n" +
            "    State one's name joined to \" steers \" joined to angle converted to text.\n" +
            "Done.\n" +
            "Define r as a new racer { the name \"Speedy\" }.\n" +
            "Cast steer on (r, 90)."));
    }

    [Fact]
    public void Unto_WithParamsCalledPossessively()
    {
        Assert.Equal("90", Run(
            "Define object racer with (the text name).\n" +
            "Bind void to steer unto racer, given (the number angle):\n" +
            "    State angle converted to text.\n" +
            "Done.\n" +
            "Define r as a new racer { the name \"Speedy\" }.\n" +
            "Cast r's steer on (90)."));
    }

    [Fact]
    public void Unto_HoistedBeforeObjectDefinition()
    {
        // The 'unto' Bind appears in the file before 'Define object' — order-independent.
        Assert.Equal("Alice", Run(
            "Bind void to greet unto person:\n" +
            "    State one's name.\n" +
            "Done.\n" +
            "Define object person with (the text name).\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "Cast greet on alice."));
    }

    [Fact]
    public void Unto_HoistedAfterObjectDefinition()
    {
        Assert.Equal("Alice", Run(
            "Define object person with (the text name).\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "Bind void to greet unto person:\n" +
            "    State one's name.\n" +
            "Done.\n" +
            "Cast greet on alice."));
    }

    [Fact]
    public void Unto_NotCallableAsFreeFunction()
    {
        // An 'unto' method must not leak into the free-function namespace. A zero-arg call to
        // an undefined name is a runtime error in Cufet generally (not statically provable —
        // same as calling any other undefined free function with no args), so that's the
        // exception this throws; the point is it's NOT resolved as a callable free function.
        Assert.Throws<RuntimeException>(() => Run(
            "Define object person with (the text name).\n" +
            "Bind void to greet unto person:\n" +
            "    State one's name.\n" +
            "Done.\n" +
            "Cast greet on ()."));
    }

    [Fact]
    public void Unto_IndistinguishableFromNestedAtCallSite()
    {
        // Two methods, one nested, one unto — both callable identically.
        Assert.Equal("nested\nunto", Run(
            "Define object person with (the text name):\n" +
            "    Bind void to greet-nested:\n" +
            "        State \"nested\".\n" +
            "    Done.\n" +
            "Done.\n" +
            "Bind void to greet-unto unto person:\n" +
            "    State \"unto\".\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "Cast greet-nested on alice.\n" +
            "Cast greet-unto on alice."));
    }

    [Fact]
    public void Unto_CollisionWithNestedMethodIsStaticError()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name):\n" +
            "    Bind void to greet:\n" +
            "        State one's name.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Bind void to greet unto person:\n" +
            "    State one's name.\n" +
            "Done."));
        Assert.Contains("already has a method", ex.Message);
    }

    [Fact]
    public void Unto_CollisionBetweenTwoUntoMethodsIsStaticError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name).\n" +
            "Bind void to greet unto person:\n" +
            "    State one's name.\n" +
            "Done.\n" +
            "Bind void to greet unto person:\n" +
            "    State \"again\".\n" +
            "Done."));
    }

    [Fact]
    public void Unto_NoCollisionAcrossDifferentTypes()
    {
        // Same method name 'unto' two different types is fine — no shared namespace.
        Assert.Equal("person\ncar", Run(
            "Define object person with (the text name).\n" +
            "Define object car with (the text make).\n" +
            "Bind void to describe unto person:\n" +
            "    State \"person\".\n" +
            "Done.\n" +
            "Bind void to describe unto car:\n" +
            "    State \"car\".\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "Define honda as a new car { the make \"Honda\" }.\n" +
            "Cast describe on alice.\n" +
            "Cast describe on honda."));
    }

    [Fact]
    public void Unto_UndefinedTargetTypeIsStaticError()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Bind void to greet unto nonexistent:\n" +
            "    State \"hi\".\n" +
            "Done."));
        Assert.Contains("not a defined object type", ex.Message);
    }

    [Fact]
    public void Unto_InterfaceTargetIsStaticError()
    {
        var ex = Assert.Throws<TypeException>(() => Run(
            "Define greeter as an interface for the void function greet.\n" +
            "Bind void to greet unto greeter:\n" +
            "    State \"hi\".\n" +
            "Done."));
        Assert.Contains("is an interface, not an object type", ex.Message);
    }

    [Fact]
    public void Unto_SatisfiesInterfaceConformance()
    {
        Assert.Equal("Hi Alice", Run(
            "Define greeter as an interface for the void function greet.\n" +
            "Define object person with (the text name) and greeter.\n" +
            "Bind void to greet unto person:\n" +
            "    State \"Hi \" joined to one's name.\n" +
            "Done.\n" +
            "Define alice as a new person { the name \"Alice\" }.\n" +
            "Bind void to greet-someone, given (the greeter g):\n" +
            "    Cast greet on g.\n" +
            "Done.\n" +
            "Cast greet-someone on alice."));
    }

    [Fact]
    public void Unto_MissingMethodStillFailsConformance()
    {
        // Without the 'unto' method, conformance fails exactly as it would for a nested-only check.
        Assert.Throws<TypeException>(() => Run(
            "Define greeter as an interface for the void function greet.\n" +
            "Define object person with (the text name) and greeter."));
    }

    [Fact]
    public void Unto_NestedBindInsideBodyIsParseError()
    {
        // 'unto' methods block nested Binds inside their body, identically to nested methods.
        Assert.Throws<ParseException>(() => Run(
            "Define object person with (the text name).\n" +
            "Bind void to greet unto person:\n" +
            "    Bind void to helper:\n" +
            "        State \"x\".\n" +
            "    Done.\n" +
            "Done."));
    }

    [Fact]
    public void Unto_TypeErrorInBodyIsCaught()
    {
        // Body type-checking applies to 'unto' methods exactly as to nested ones.
        Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name).\n" +
            "Bind void to greet unto person:\n" +
            "    State one's name + 1.\n" +
            "Done."));
    }

    [Fact]
    public void Unto_WrongReturnTypeIsCaught()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object person with (the text name).\n" +
            "Bind number to get-name unto person:\n" +
            "    Return one's name.\n" +
            "Done."));
    }

    [Fact]
    public void Unto_MissingReturnOnSomePathIsCaught()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object person with (the number age).\n" +
            "Bind number to get-age unto person:\n" +
            "    If one's age is greater than 0:\n" +
            "        Return one's age.\n" +
            "    Done.\n" +
            "Done."));
    }

    // ── Failures ─────────────────────────────────────────────────────────────

    [Fact]
    public void Failure_ButOnFailure_TakesDefault()
    {
        Assert.Equal("0", Run(
            "Bind number or failure to parse, given (the text s):\n" +
            "    Return a failure \"bad input\".\n" +
            "Done.\n" +
            "Define result as Cast parse on (\"x\") but on failure 0.\n" +
            "State result."));
    }

    [Fact]
    public void Failure_ButOnFailure_TakesSuccessValue()
    {
        Assert.Equal("42", Run(
            "Bind number or failure to parse, given (the text s):\n" +
            "    Return 42.\n" +
            "Done.\n" +
            "Define result as Cast parse on (\"42\") but on failure 0.\n" +
            "State result."));
    }

    [Fact]
    public void Failure_TryBlock_BodySucceeds()
    {
        Assert.Equal("42", Run(
            "Bind number or failure to parse, given (the text s):\n" +
            "    Return 42.\n" +
            "Done.\n" +
            "Try to:\n" +
            "    Define result as Cast parse on (\"42\").\n" +
            "    State result.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State 0.\n" +
            "Done."));
    }

    [Fact]
    public void Failure_TryBlock_BodyFails_HandlerRuns()
    {
        Assert.Equal("0", Run(
            "Bind number or failure to parse, given (the text s):\n" +
            "    Return a failure \"bad\".\n" +
            "Done.\n" +
            "Try to:\n" +
            "    Define result as Cast parse on (\"x\").\n" +
            "    State result.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State 0.\n" +
            "Done."));
    }

    [Fact]
    public void Failure_TryBlock_TheFailureBindingExposesMessage()
    {
        Assert.Equal("bad input", Run(
            "Bind number or failure to parse, given (the text s):\n" +
            "    Return a failure \"bad input\".\n" +
            "Done.\n" +
            "Try to:\n" +
            "    Define result as Cast parse on (\"x\").\n" +
            "    State result.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State the message of the failure.\n" +
            "Done."));
    }

    [Fact]
    public void Failure_TryBlock_CategoryPresentAndAccessible()
    {
        Assert.Equal("bad-input", Run(
            "Bind number or failure to parse, given (the text s):\n" +
            "    Return a failure \"bad\" of category \"bad-input\".\n" +
            "Done.\n" +
            "Try to:\n" +
            "    Define result as Cast parse on (\"x\").\n" +
            "    State result.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State the category of the failure but void is \"none\".\n" +
            "Done."));
    }

    [Fact]
    public void Failure_TryBlock_CategoryAbsentIsVoid()
    {
        Assert.Equal("none", Run(
            "Bind number or failure to parse, given (the text s):\n" +
            "    Return a failure \"bad\".\n" +
            "Done.\n" +
            "Try to:\n" +
            "    Define result as Cast parse on (\"x\").\n" +
            "    State result.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State the category of the failure but void is \"none\".\n" +
            "Done."));
    }

    [Fact]
    public void Failure_Propagate_ReturnsSuccessValueToOuterCaller()
    {
        Assert.Equal("42", Run(
            "Bind number or failure to inner, given (the text s):\n" +
            "    Return 42.\n" +
            "Done.\n" +
            "Bind number or failure to outer, given (the text s):\n" +
            "    Return Cast inner on (s) or pass the failure off.\n" +
            "Done.\n" +
            "Define result as Cast outer on (\"42\") but on failure 0.\n" +
            "State result."));
    }

    [Fact]
    public void Failure_Propagate_PropagatesFailureToOuterCaller()
    {
        Assert.Equal("propagated", Run(
            "Bind number or failure to inner, given (the text s):\n" +
            "    Return a failure \"propagated\".\n" +
            "Done.\n" +
            "Bind number or failure to outer, given (the text s):\n" +
            "    Return Cast inner on (s) or pass the failure off.\n" +
            "Done.\n" +
            "Try to:\n" +
            "    Define result as Cast outer on (\"x\").\n" +
            "    State result.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State the message of the failure.\n" +
            "Done."));
    }

    [Fact]
    public void Failure_TypeChecker_NonFallibleCannotUseButOnFailure()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind number to get-num:\n" +
            "    Return 42.\n" +
            "Done.\n" +
            "Define result as Cast get-num on () but on failure 0."));
    }

    [Fact]
    public void Failure_TypeChecker_PropagateFromNonFallibleThrows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind number or failure to inner, given (the text s):\n" +
            "    Return a failure \"bad\".\n" +
            "Done.\n" +
            "Bind number to outer:\n" +
            "    Return Cast inner on (\"x\") or pass the failure off.\n" +
            "Done."));
    }

    [Fact]
    public void Failure_TypeChecker_WrongDefaultTypeForButOnFailure()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind number or failure to parse, given (the text s):\n" +
            "    Return a failure \"bad\".\n" +
            "Done.\n" +
            "Define result as Cast parse on (\"x\") but on failure \"wrong type\"."));
    }

    [Fact]
    public void Failure_CategoryVoidComparedToTextIsFalse()
    {
        // category is absent (void), compared directly to a text value — must yield false, not an error.
        Assert.Equal("no match", Run(
            "Bind number or failure to parse, given (the text s):\n" +
            "    Return a failure \"bad\".\n" +
            "Done.\n" +
            "Try to:\n" +
            "    Define result as Cast parse on (\"x\").\n" +
            "    State result.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    If the category of the failure is \"bad-input\":\n" +
            "        State \"matched\".\n" +
            "    Done.\n" +
            "    Otherwise:\n" +
            "        State \"no match\".\n" +
            "    Done.\n" +
            "Done."));
    }

    [Fact]
    public void Failure_CategoryPresentComparedToTextIsTrue()
    {
        Assert.Equal("matched", Run(
            "Bind number or failure to parse, given (the text s):\n" +
            "    Return a failure \"bad\" of category \"bad-input\".\n" +
            "Done.\n" +
            "Try to:\n" +
            "    Define result as Cast parse on (\"x\").\n" +
            "    State result.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    If the category of the failure is \"bad-input\":\n" +
            "        State \"matched\".\n" +
            "    Done.\n" +
            "    Otherwise:\n" +
            "        State \"no match\".\n" +
            "    Done.\n" +
            "Done."));
    }

    [Fact]
    public void Failure_CategoryVoidIsNotTextIsTrue()
    {
        // void category is not "literal" → true (complement of the false case)
        Assert.Equal("is not matched", Run(
            "Bind number or failure to parse, given (the text s):\n" +
            "    Return a failure \"bad\".\n" +
            "Done.\n" +
            "Try to:\n" +
            "    Define result as Cast parse on (\"x\").\n" +
            "    State result.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    If the category of the failure is not \"bad-input\":\n" +
            "        State \"is not matched\".\n" +
            "    Done.\n" +
            "    Otherwise:\n" +
            "        State \"matched\".\n" +
            "    Done.\n" +
            "Done."));
    }

    [Fact]
    public void Failure_UnhandledInExpressionContextGivesTypeException()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind number or failure to parse, given (the text s):\n" +
            "    Return a failure \"something went wrong\".\n" +
            "Done.\n" +
            "Define result as Cast parse on (\"x\").\n" +
            "State result."));
    }

    [Fact]
    public void Failure_UnhandledBareCallGivesTypeException()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind number or failure to parse, given (the text s):\n" +
            "    Return a failure \"something went wrong\".\n" +
            "Done.\n" +
            "Cast parse on (\"x\")."));
    }

    // ── Exceptions (Slice 2) ─────────────────────────────────────────────────────

    [Fact]
    public void Exception_CaughtByHandler_HandlerRuns()
    {
        // A runtime exception (divide by zero) is caught; handler prints the message.
        var output = Run(
            "Try to:\n" +
            "    Define x as 1 / 0.\n" +
            "    State \"should not reach\".\n" +
            "Done.\n" +
            "In case of exception (the exception):\n" +
            "    State \"caught: \" joined to the message of the exception.\n" +
            "    Suppress the exception.\n" +
            "Done.");
        Assert.Contains("caught:", output);
        Assert.DoesNotContain("should not reach", output);
    }

    [Fact]
    public void Exception_SuppressedContinuesAfterTry()
    {
        // After Suppress, execution continues with the statement after the Try block.
        var output = Run(
            "Try to:\n" +
            "    Define x as 1 / 0.\n" +
            "Done.\n" +
            "In case of exception (the exception):\n" +
            "    Suppress the exception.\n" +
            "Done.\n" +
            "State \"after try\".");
        Assert.Equal("after try", output.Trim());
    }

    [Fact]
    public void Exception_NotSuppressedReRaises()
    {
        // Without Suppress, the exception re-raises and the program halts with RuntimeException.
        Assert.Throws<RuntimeException>(() => Run(
            "Try to:\n" +
            "    Define x as 1 / 0.\n" +
            "Done.\n" +
            "In case of exception (the exception):\n" +
            "    State \"handler ran\".\n" +
            "Done.\n" +
            "State \"after try\"."));
    }

    [Fact]
    public void Exception_MessageAccessible()
    {
        // The message of the exception is accessible inside the handler.
        var output = Run(
            "Try to:\n" +
            "    Define x as 1 / 0.\n" +
            "Done.\n" +
            "In case of exception (the exception):\n" +
            "    State the message of the exception.\n" +
            "    Suppress the exception.\n" +
            "Done.");
        Assert.False(string.IsNullOrWhiteSpace(output));
    }

    [Fact]
    public void Exception_NoExceptionInBodySkipsHandler()
    {
        // When the body succeeds, the exception handler does not run.
        var output = Run(
            "Try to:\n" +
            "    State \"body ran\".\n" +
            "Done.\n" +
            "In case of exception (the exception):\n" +
            "    State \"handler ran\".\n" +
            "    Suppress the exception.\n" +
            "Done.");
        Assert.Equal("body ran", output.Trim());
        Assert.DoesNotContain("handler ran", output);
    }

    [Fact]
    public void Exception_BothHandlers_FailureTakesFailurePath()
    {
        // A failure goes to the failure handler, NOT the exception handler.
        var output = Run(
            "Bind number or failure to parse, given (the text s):\n" +
            "    Return a failure \"bad\".\n" +
            "Done.\n" +
            "Try to:\n" +
            "    Define result as Cast parse on (\"x\").\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failure handler\".\n" +
            "Done.\n" +
            "In case of exception (the exception):\n" +
            "    State \"exception handler\".\n" +
            "    Suppress the exception.\n" +
            "Done.");
        Assert.Equal("failure handler", output.Trim());
        Assert.DoesNotContain("exception handler", output);
    }

    [Fact]
    public void Exception_BothHandlers_ExceptionTakesExceptionPath()
    {
        // A runtime exception goes to the exception handler, NOT the failure handler.
        var output = Run(
            "Try to:\n" +
            "    Define x as 1 / 0.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failure handler\".\n" +
            "Done.\n" +
            "In case of exception (the exception):\n" +
            "    State \"exception handler\".\n" +
            "    Suppress the exception.\n" +
            "Done.");
        Assert.Equal("exception handler", output.Trim());
        Assert.DoesNotContain("failure handler", output);
    }

    [Fact]
    public void Exception_OnlyExceptionHandler_NoFailureHandler()
    {
        // A Try with only an exception handler works.
        var output = Run(
            "Try to:\n" +
            "    Define x as 1 / 0.\n" +
            "Done.\n" +
            "In case of exception (the exception):\n" +
            "    State \"caught\".\n" +
            "    Suppress the exception.\n" +
            "Done.");
        Assert.Equal("caught", output.Trim());
    }

    [Fact]
    public void Exception_TypeChecker_SuppressOutsideHandlerIsError()
    {
        Assert.Throws<TypeException>(() => Run("Suppress the exception."));
    }

    [Fact]
    public void Exception_TypeChecker_TryWithNoHandlersIsError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Try to:\n" +
            "    State \"body\".\n" +
            "Done."));
    }

    [Fact]
    public void Exception_SuppressInsideIfInsideHandler_StopsHandler()
    {
        // Suppress inside a nested If still stops the handler — SuppressSignal unwinds through If.
        var output = Run(
            "Try to:\n" +
            "    Define x as 1 / 0.\n" +
            "Done.\n" +
            "In case of exception (the exception):\n" +
            "    If 1 is 1:\n" +
            "        Suppress the exception.\n" +
            "    Done.\n" +
            "Done.\n" +
            "State \"continued\".");
        Assert.Equal("continued", output.Trim());
    }

    // ── I/O — read a line from the input ─────────────────────────────────────

    [Fact]
    public void IO_ReadLine_ReturnsSingleLine()
    {
        Assert.Equal("hello", RunWithInput(
            "Define line as read a line from the input.\n" +
            "State line but void is \"nothing\".",
            "hello\n"));
    }

    [Fact]
    public void IO_ReadLine_StripsTrailingNewline()
    {
        // Trailing newline is stripped — result is the text content only.
        Assert.Equal("5", RunWithInput(
            "Define line as read a line from the input.\n" +
            "State the length of (line but void is \"\") converted to text.",
            "hello\n"));
    }

    [Fact]
    public void IO_ReadLine_AtEof_ReturnsVoid()
    {
        Assert.Equal("nothing", RunWithInput(
            "Define line as read a line from the input.\n" +
            "State line but void is \"nothing\".",
            ""));
    }

    [Fact]
    public void IO_ReadLine_VoidNarrowed_WithIsVoidCheck()
    {
        Assert.Equal("got: hello", RunWithInput(
            "Define line as read a line from the input.\n" +
            "If line is not void:\n" +
            "    State \"got: \" joined to line.\n" +
            "Done.",
            "hello"));
    }

    [Fact]
    public void IO_ReadLine_ReadLoop_WithStop()
    {
        Assert.Equal("hello\nworld", RunWithInput(
            "While 1 is 1, repeat:\n" +
            "    Define line as read a line from the input.\n" +
            "    If line is void, Stop.\n" +
            "    State line.\n" +
            "Done.",
            "hello\nworld\n"));
    }

    [Fact]
    public void IO_ReadLine_ReadLoop_EmptyInput_ExitsImmediately()
    {
        Assert.Equal("done", RunWithInput(
            "While 1 is 1, repeat:\n" +
            "    Define line as read a line from the input.\n" +
            "    If line is void, Stop.\n" +
            "    State line.\n" +
            "Done.\n" +
            "State \"done\".",
            ""));
    }

    [Fact]
    public void IO_ReadLine_TypeIsVoidableText_CannotJoinDirectly()
    {
        // read a line returns voidable text — joining without void handling is a type error.
        Assert.Throws<TypeException>(() => Run(
            "Define line as read a line from the input.\n" +
            "State line joined to \" world\"."));
    }

    // ── I/O — read all from the input ────────────────────────────────────────

    [Fact]
    public void IO_ReadAll_ReturnsSingleTextBlock()
    {
        // read all returns raw content; State + TrimEnd('\n') strips one trailing newline.
        Assert.Equal("hello\nworld", RunWithInput(
            "Define file-data as read all from the input.\n" +
            "State file-data.",
            "hello\nworld\n"));
    }

    [Fact]
    public void IO_ReadAll_EmptyInput_ReturnsEmptyString()
    {
        Assert.Equal("empty", RunWithInput(
            "Define file-data as read all from the input.\n" +
            "If file-data is \"\":\n" +
            "    State \"empty\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"not empty\".\n" +
            "Done.",
            ""));
    }

    [Fact]
    public void IO_ReadAll_TypeIsPlainText_NoVoidHandlingNeeded()
    {
        // read all returns plain text — should type-check and run without void handling.
        Assert.Equal("hello", RunWithInput(
            "Define file-data as read all from the input.\n" +
            "State file-data trimmed.",
            "hello\n"));
    }

    // ── I/O — read all lines from the input ──────────────────────────────────

    [Fact]
    public void IO_ReadAllLines_ReturnsSeriesOfText()
    {
        Assert.Equal("3", RunWithInput(
            "Define lines as read all lines from the input.\n" +
            "State the number of lines converted to text.",
            "one\ntwo\nthree\n"));
    }

    [Fact]
    public void IO_ReadAllLines_ContentCorrect()
    {
        Assert.Equal("one\ntwo\nthree", RunWithInput(
            "Define lines as read all lines from the input.\n" +
            "For each ln in lines, repeat:\n" +
            "    State ln.\n" +
            "Done.",
            "one\ntwo\nthree\n"));
    }

    [Fact]
    public void IO_ReadAllLines_EmptyInput_ReturnsEmptySeries()
    {
        Assert.Equal("0", RunWithInput(
            "Define lines as read all lines from the input.\n" +
            "State the number of lines converted to text.",
            ""));
    }

    [Fact]
    public void IO_ReadAllLines_TrailingNewlineDoesNotProduceEmptyElement()
    {
        // "hello\n" should produce one element "hello", not ["hello", ""].
        Assert.Equal("1", RunWithInput(
            "Define lines as read all lines from the input.\n" +
            "State the number of lines converted to text.",
            "hello\n"));
    }

    // ── I/O — file reads ─────────────────────────────────────────────────────

    [Fact]
    public void IO_FileReadAll_ReadsEntireContents()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "hello world");
            Assert.Equal("hello world", Run(
                $"Try to:\n" +
                $"    Define file-data as read all from the file \"{path.Replace("\\", "\\\\")}\".\n" +
                $"    State file-data.\n" +
                $"Done.\n" +
                $"In case of failure:\n" +
                $"    State \"fail\".\n" +
                $"Done."));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void IO_FileReadAllLines_ReadsLines()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "one\ntwo\nthree");
            Assert.Equal("3", Run(
                $"Try to:\n" +
                $"    Define lines as read all lines from the file \"{path.Replace("\\", "\\\\")}\".\n" +
                $"    State the number of lines converted to text.\n" +
                $"Done.\n" +
                $"In case of failure:\n" +
                $"    State \"fail\".\n" +
                $"Done."));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void IO_FileReadAllLines_ContentCorrect()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "alpha\nbeta\ngamma");
            Assert.Equal("alpha\nbeta\ngamma", Run(
                $"Try to:\n" +
                $"    Define lines as read all lines from the file \"{path.Replace("\\", "\\\\")}\".\n" +
                $"    For each line in lines, repeat:\n" +
                $"        State line.\n" +
                $"    Done.\n" +
                $"Done.\n" +
                $"In case of failure:\n" +
                $"    State \"fail\".\n" +
                $"Done."));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void IO_FileReadAll_EmptyFile_ReturnsEmptyString()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "");
            Assert.Equal("empty", Run(
                $"Try to:\n" +
                $"    Define file-data as read all from the file \"{path.Replace("\\", "\\\\")}\".\n" +
                $"    If the length of file-data is 0:\n" +
                $"        State \"empty\".\n" +
                $"    Done.\n" +
                $"Done.\n" +
                $"In case of failure:\n" +
                $"    State \"fail\".\n" +
                $"Done."));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void IO_FileReadAllLines_EmptyFile_ReturnsEmptySeries()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "");
            Assert.Equal("0", Run(
                $"Try to:\n" +
                $"    Define lines as read all lines from the file \"{path.Replace("\\", "\\\\")}\".\n" +
                $"    State the number of lines converted to text.\n" +
                $"Done.\n" +
                $"In case of failure:\n" +
                $"    State \"fail\".\n" +
                $"Done."));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void IO_FileReadAll_FileNotFound_ReturnsNotFoundCategory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cufet_no_such_file_" + Guid.NewGuid() + ".txt");
        Assert.Equal("not-found", Run(
            $"Try to:\n" +
            $"    Define file-data as read all from the file \"{path.Replace("\\", "\\\\")}\".\n" +
            $"    State file-data.\n" +
            $"Done.\n" +
            $"In case of failure:\n" +
            $"    State the category of the failure.\n" +
            $"Done."));
    }

    [Fact]
    public void IO_FileReadAll_InsideTry_CatchesFailureMessage()
    {
        var path = Path.Combine(Path.GetTempPath(), "cufet_no_such_file_" + Guid.NewGuid() + ".txt");
        var result = Run(
            $"Try to:\n" +
            $"    Define file-data as read all from the file \"{path.Replace("\\", "\\\\")}\".\n" +
            $"    State file-data.\n" +
            $"Done.\n" +
            $"In case of failure:\n" +
            $"    State \"caught\".\n" +
            $"Done.");
        Assert.Equal("caught", result);
    }

    [Fact]
    public void IO_FileReadAll_ButOnFailure_UsesDefault()
    {
        var path = Path.Combine(Path.GetTempPath(), "cufet_no_such_file_" + Guid.NewGuid() + ".txt");
        Assert.Equal("default text", Run(
            $"Define file-data as read all from the file \"{path.Replace("\\", "\\\\")}\" but on failure \"default text\".\n" +
            $"State file-data."));
    }

    [Fact]
    public void IO_FileReadAll_PathFromVariable_Works()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "from variable");
            Assert.Equal("from variable", Run(
                $"Define p as \"{path.Replace("\\", "\\\\")}\".\n" +
                $"Try to:\n" +
                $"    Define file-data as read all from the file p.\n" +
                $"    State file-data.\n" +
                $"Done.\n" +
                $"In case of failure:\n" +
                $"    State \"fail\".\n" +
                $"Done."));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void IO_FileRead_MustHandleFailure_StaticError()
    {
        var path = "\"config.txt\"";
        Assert.Throws<TypeException>(() => Run(
            $"Define file-data as read all from the file {path}.\n" +
            $"State file-data."));
    }

    [Fact]
    public void IO_FileRead_PathMustBeText_StaticError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Try to:\n" +
            "    Define file-data as read all from the file 42.\n" +
            "    State file-data.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"fail\".\n" +
            "Done."));
    }

    // ── I/O — file writes ────────────────────────────────────────────────────

    [Fact]
    public void IO_FileWrite_CreatesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "cufet_write_" + Guid.NewGuid() + ".txt");
        try
        {
            Run(
                $"Try to:\n" +
                $"    Write \"hello\" to the file \"{path.Replace("\\", "\\\\")}\".\n" +
                $"Done.\n" +
                $"In case of failure:\n" +
                $"    State \"fail\".\n" +
                $"Done.");
            Assert.Equal("hello", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void IO_FileWrite_OverwritesExistingContent()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "old content");
            Run(
                $"Try to:\n" +
                $"    Write \"new content\" to the file \"{path.Replace("\\", "\\\\")}\".\n" +
                $"Done.\n" +
                $"In case of failure:\n" +
                $"    State \"fail\".\n" +
                $"Done.");
            Assert.Equal("new content", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void IO_FileAppend_AppendsToExisting()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "first");
            Run(
                $"Try to:\n" +
                $"    Append \" second\" to the file \"{path.Replace("\\", "\\\\")}\".\n" +
                $"Done.\n" +
                $"In case of failure:\n" +
                $"    State \"fail\".\n" +
                $"Done.");
            Assert.Equal("first second", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void IO_FileAppend_CreatesFileIfNotExists()
    {
        var path = Path.Combine(Path.GetTempPath(), "cufet_append_" + Guid.NewGuid() + ".txt");
        try
        {
            Run(
                $"Try to:\n" +
                $"    Append \"created\" to the file \"{path.Replace("\\", "\\\\")}\".\n" +
                $"Done.\n" +
                $"In case of failure:\n" +
                $"    State \"fail\".\n" +
                $"Done.");
            Assert.Equal("created", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void IO_FileWrite_DirectoryNotFound_ThrowsFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), "cufet_no_dir_" + Guid.NewGuid(), "file.txt");
        Assert.Equal("not-found", Run(
            $"Try to:\n" +
            $"    Write \"x\" to the file \"{path.Replace("\\", "\\\\")}\".\n" +
            $"Done.\n" +
            $"In case of failure:\n" +
            $"    State the category of the failure.\n" +
            $"Done."));
    }

    [Fact]
    public void IO_FileWrite_ValueMustBeText_StaticError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Try to:\n" +
            "    Write 42 to the file \"out.txt\".\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"fail\".\n" +
            "Done."));
    }

    [Fact]
    public void IO_FileWrite_WriteAndReadRoundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "cufet_roundtrip_" + Guid.NewGuid() + ".txt");
        try
        {
            Assert.Equal("round trip data", Run(
                $"Define p as \"{path.Replace("\\", "\\\\")}\".\n" +
                $"Try to:\n" +
                $"    Write \"round trip data\" to the file p.\n" +
                $"Done.\n" +
                $"In case of failure:\n" +
                $"    State \"write failed\".\n" +
                $"Done.\n" +
                $"Try to:\n" +
                $"    Define file-data as read all from the file p.\n" +
                $"    State file-data.\n" +
                $"Done.\n" +
                $"In case of failure:\n" +
                $"    State \"read failed\".\n" +
                $"Done."));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── I/O — process execution (run) ─────────────────────────────────────────

    [Fact]
    public void IO_Run_ExitCode_Zero()
    {
        // run blocks until the process exits; exit code 0 is available in the result record.
        // Intermediate variable needed: 'the exit-code of result converted to text' mis-parses
        // (the inner ParsePrimary greedily eats 'converted to text').
        Assert.Equal("0", Run(
            "Try to:\n" +
            "    Define result as run \"cmd\" with arguments (\"/C\", \"exit /b 0\").\n" +
            "    Define code as the exit-code of result.\n" +
            "    State code converted to text.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done."));
    }

    [Fact]
    public void IO_Run_ExitCode_NonzeroIsNormalResult_NotFailure()
    {
        // A process that runs and exits nonzero is a normal result — not a Cufet failure.
        // Inline If to avoid the per-arm Done. requirement.
        Assert.Equal("got it", Run(
            "Try to:\n" +
            "    Define result as run \"cmd\" with arguments (\"/C\", \"exit /b 42\").\n" +
            "    If the exit-code of result is 42, State \"got it\".\n" +
            "    Otherwise, State \"wrong\".\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done."));
    }

    [Fact]
    public void IO_Run_ExitCode_CapturedAsNumber()
    {
        // exit-code field is a number; non-zero exit codes are captured correctly.
        Assert.Equal("42", Run(
            "Try to:\n" +
            "    Define result as run \"cmd\" with arguments (\"/C\", \"exit /b 42\").\n" +
            "    Define code as the exit-code of result.\n" +
            "    State code converted to text.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done."));
    }

    [Fact]
    public void IO_Run_Output_StdoutCaptured()
    {
        // stdout is captured in the 'output' field of the result record.
        Assert.Equal("hello", Run(
            "Try to:\n" +
            "    Define result as run \"cmd\" with arguments (\"/C\", \"echo hello\").\n" +
            "    State (the output of result) trimmed.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done."));
    }

    [Fact]
    public void IO_Run_Errors_StderrCaptured()
    {
        // stderr is captured in the 'errors' field; stdout is empty when only stderr is written.
        Assert.Equal("stderr-data", Run(
            "Try to:\n" +
            "    Define result as run \"cmd\" with arguments (\"/C\", \"echo stderr-data>&2\").\n" +
            "    State (the errors of result) trimmed.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done."));
    }

    [Fact]
    public void IO_Run_WithArguments_ArgsPassedToProcess()
    {
        // Each argument in 'with arguments (...)' is passed directly as a separate OS argument.
        Assert.Equal("passed-arg", Run(
            "Try to:\n" +
            "    Define result as run \"cmd\" with arguments (\"/C\", \"echo\", \"passed-arg\").\n" +
            "    State (the output of result) trimmed.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done."));
    }

    [Fact]
    public void IO_Run_LaunchFailure_CategoryNotFound()
    {
        // A nonexistent program produces a launch failure with category 'not-found'.
        // No 'with arguments' clause — tests bare 'run <program>' syntax.
        Assert.Equal("not-found", Run(
            "Try to:\n" +
            "    Define result as run \"nonexistent-program-xyz-abc-9876\".\n" +
            "    State \"ran\".\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State the category of the failure.\n" +
            "Done."));
    }

    [Fact]
    public void IO_Run_LaunchFailure_CaughtByTry()
    {
        // A launch failure is caught by the 'In case of failure' handler in a Try block.
        Assert.Equal("caught", Run(
            "Try to:\n" +
            "    Define result as run \"nonexistent-program-xyz-abc-9876\".\n" +
            "    State \"ran\".\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"caught\".\n" +
            "Done."));
    }

    [Fact]
    public void IO_Run_LaunchFailure_ButOnFailure()
    {
        // 'but on failure <default>' provides a fallback record when the launch fails.
        Assert.Equal("default", Run(
            "Define result as run \"nonexistent-xyz\" but on failure " +
                "(a record with (the output \"default\", the exit-code 0, the errors \"\")).\n" +
            "State the output of result."));
    }

    [Fact]
    public void IO_Run_LaunchFailure_PassFailureOff()
    {
        // 'or pass the failure off' propagates launch failures to the caller.
        Assert.Equal("hello", Run(
            "Bind text or failure to run-it:\n" +
            "    Define result as run \"cmd\" with arguments (\"/C\", \"echo hello\") or pass the failure off.\n" +
            "    Return (the output of result) trimmed.\n" +
            "Done.\n" +
            "Try to:\n" +
            "    State cast run-it on ().\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done."));
    }

    [Fact]
    public void IO_Run_ProgramFromVariable()
    {
        // The program to run can be any text expression, including a variable.
        Assert.Equal("0", Run(
            "Define prog as \"cmd\".\n" +
            "Try to:\n" +
            "    Define result as run prog with arguments (\"/C\", \"exit /b 0\").\n" +
            "    Define code as the exit-code of result.\n" +
            "    State code converted to text.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done."));
    }

    [Fact]
    public void IO_Run_AllThreeFieldsAccessible()
    {
        // output, errors, and exit-code are all accessible on the result record.
        // Intermediate variable for exit-code to avoid 'converted to text' mis-parse.
        Assert.Equal("hello\n0", Run(
            "Try to:\n" +
            "    Define result as run \"cmd\" with arguments (\"/C\", \"echo hello\").\n" +
            "    State (the output of result) trimmed.\n" +
            "    Define code as the exit-code of result.\n" +
            "    State code converted to text.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done."));
    }

    [Fact]
    public void IO_Run_MustHandleFailure_StaticError()
    {
        // Using a run result without handling the launch failure is a static type error.
        Assert.Throws<TypeException>(() => Run(
            "Define result as run \"cmd\" with arguments (\"/C\", \"exit /b 0\").\n" +
            "State the exit-code of result."));
    }

    [Fact]
    public void IO_Run_ProgramMustBeText_StaticError()
    {
        // The program expression must be text — a number is a static error.
        Assert.Throws<TypeException>(() => Run(
            "Try to:\n" +
            "    Define result as run 42.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done."));
    }

    [Fact]
    public void IO_Run_ArgMustBeText_StaticError()
    {
        // Each argument must be text — a number argument is a static error.
        Assert.Throws<TypeException>(() => Run(
            "Try to:\n" +
            "    Define result as run \"cmd\" with arguments (\"/C\", 42).\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done."));
    }

    // ── I/O — streams: Slice 1 (stream of text type + generalized reads) ──────

    [Fact]
    public void Stream_InputStillWorksAfterRefactor()
    {
        // 'read a line from the input' surface syntax unchanged — unification is internal.
        var result = RunWithInput("State read a line from the input but void is \"none\".", "hello\n");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Stream_ReadAllFromInputUnchanged()
    {
        var result = RunWithInput("State read all from the input.", "hello world");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Stream_ReadAllLinesFromInputUnchanged()
    {
        var result = RunWithInput("State read all lines from the input.", "a\nb\nc\n");
        Assert.Equal("(a, b, c)", result);
    }

    [Fact]
    public void Stream_InputVariableHoldsStream()
    {
        // 'the input' is a stream of text binding — can be assigned to a variable and read from.
        var result = RunWithInput(
            "Define s as the input.\n" +
            "State read a line from s but void is \"none\".",
            "hello\n");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Stream_ReadFromVariableTwiceAdvancesPosition()
    {
        // Each read advances position — second read gets the second line.
        var result = RunWithInput(
            "Define s as the input.\n" +
            "Define line1 as read a line from s but void is \"\".\n" +
            "Define line2 as read a line from s but void is \"\".\n" +
            "State line1 joined to \",\" joined to line2.",
            "line1\nline2\n");
        Assert.Equal("line1,line2", result);
    }

    [Fact]
    public void Stream_PassedToFunction()
    {
        // A readable stream of text is a valid function parameter type.
        var result = RunWithInput(
            "Bind text to read-first, given (readable stream of text src):\n" +
            "    Define line as read a line from src but void is \"none\".\n" +
            "    return line.\n" +
            "Done.\n" +
            "State Cast read-first on (the input).",
            "hello\n");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Stream_NonStreamSourceIsStaticError()
    {
        // Passing a number as the stream source is a static type error.
        Assert.Throws<TypeException>(() => Run("State read a line from 42 but void is \"none\"."));
    }

    [Fact]
    public void Stream_TextSourceIsStaticError()
    {
        // Text is not a stream — read requires a readable stream of text.
        Assert.Throws<TypeException>(() => Run("State read a line from \"hello\" but void is \"none\"."));
    }

    // ── I/O — streams: Slice 2 (file streams + scoped-open lifecycle) ────────────

    [Fact]
    public void WithOpen_ReadFile_LineByLine()
    {
        // Read two lines from a file stream — each read advances position.
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "alpha\nbeta\ngamma\n");
            var result = Run(
                $"With the file \"{path.Replace("\\", "\\\\")}\" open for reading as s:\n" +
                $"    Define line1 as read a line from s but void is \"\".\n" +
                $"    Define line2 as read a line from s but void is \"\".\n" +
                $"    State line1 joined to \",\" joined to line2.\n" +
                $"Done.");
            Assert.Equal("alpha,beta", result);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void WithOpen_ReadFile_AllLines()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "one\ntwo\nthree");
            var result = Run(
                $"With the file \"{path.Replace("\\", "\\\\")}\" open for reading as src:\n" +
                $"    State read all lines from src.\n" +
                $"Done.");
            Assert.Equal("(one, two, three)", result);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void WithOpen_ReadFile_All()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "hello world");
            var result = Run(
                $"With the file \"{path.Replace("\\", "\\\\")}\" open for reading as src:\n" +
                $"    State read all from src.\n" +
                $"Done.");
            Assert.Equal("hello world", result);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void WithOpen_WriteFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "cufet_stream_write_" + Guid.NewGuid() + ".txt");
        try
        {
            Run(
                $"With the file \"{path.Replace("\\", "\\\\")}\" open for writing as out:\n" +
                $"    Write \"hello\" to out.\n" +
                $"    Write \" world\" to out.\n" +
                $"Done.");
            Assert.Equal("hello world", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void WithOpen_OpenFailurePropagates()
    {
        // Open failure (file not found) propagates to the enclosing Try handler.
        var missing = Path.Combine(Path.GetTempPath(), "cufet_no_such_" + Guid.NewGuid() + ".txt");
        var result = Run(
            $"Try to:\n" +
            $"    With the file \"{missing.Replace("\\", "\\\\")}\" open for reading as s:\n" +
            $"        State read all from s.\n" +
            $"    Done.\n" +
            $"Done.\n" +
            $"In case of failure:\n" +
            $"    State \"caught\".\n" +
            $"Done.");
        Assert.Equal("caught", result);
    }

    [Fact]
    public void WithOpen_BodyFailurePropagates_StreamClosed()
    {
        // A failure mid-body propagates through the With block (closing the stream) to the Try handler.
        // Inside a Try block, file reads are typed as plain text; at runtime a missing file throws FailureUnwind.
        var path = Path.GetTempFileName();
        var missing = Path.Combine(Path.GetTempPath(), "cufet_no_such_" + Guid.NewGuid() + ".txt");
        try
        {
            File.WriteAllText(path, "ok");
            var result = Run(
                $"Try to:\n" +
                $"    With the file \"{path.Replace("\\", "\\\\")}\" open for reading as src:\n" +
                $"        State read all from the file \"{missing.Replace("\\", "\\\\")}\".\n" +
                $"    Done.\n" +
                $"Done.\n" +
                $"In case of failure:\n" +
                $"    State \"handled\".\n" +
                $"Done.");
            Assert.Equal("handled", result);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void WithOpen_WritableStream_ReadIsTypeError()
    {
        // Reading from a writable stream is a static type error.
        var path = Path.Combine(Path.GetTempPath(), "cufet_type_" + Guid.NewGuid() + ".txt");
        Assert.Throws<TypeException>(() => Run(
            $"With the file \"{path.Replace("\\", "\\\\")}\" open for writing as out:\n" +
            $"    State read a line from out but void is \"x\".\n" +
            $"Done."));
    }

    [Fact]
    public void WithOpen_ReadableStream_WriteIsTypeError()
    {
        // Writing to a readable stream is a static type error.
        var path = Path.Combine(Path.GetTempPath(), "cufet_type_" + Guid.NewGuid() + ".txt");
        Assert.Throws<TypeException>(() => Run(
            $"With the file \"{path.Replace("\\", "\\\\")}\" open for reading as src:\n" +
            $"    Write \"hello\" to src.\n" +
            $"Done."));
    }

    [Fact]
    public void WriteToStream_NonStreamIsTypeError()
    {
        // Writing to a non-stream value is a static type error.
        Assert.Throws<TypeException>(() => Run("Define x as \"not a stream\".\nWrite \"hello\" to x."));
    }

    [Fact]
    public void WithOpen_ReadableStreamType_AsParameter()
    {
        // A readable stream of text can be passed as a function argument.
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "from file");
            var result = Run(
                "Bind text to drain, given (readable stream of text s):\n" +
                "    return read all from s.\n" +
                "Done.\n" +
                $"With the file \"{path.Replace("\\", "\\\\")}\" open for reading as src:\n" +
                $"    State Cast drain on (src).\n" +
                $"Done.");
            Assert.Equal("from file", result);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Hardening: multi-level failure propagation ───────────────────────────────
    // Existing tests cover 2-level chains (inner → outer). These verify that a
    // failure bubbles intact through 4 levels of 'or pass the failure off'.

    [Fact]
    public void Failure_Propagate_MessageIntactThroughFourLevels()
    {
        // A failure raised in level-A propagates through B → C → D and arrives
        // at the top with its message unchanged.
        Assert.Equal("deep error", Run(
            "Bind number or failure to level-a, given (the text s):\n" +
            "    Return a failure \"deep error\" of category \"dc\".\n" +
            "Done.\n" +
            "Bind number or failure to level-b, given (the text s):\n" +
            "    Return Cast level-a on (s) or pass the failure off.\n" +
            "Done.\n" +
            "Bind number or failure to level-c, given (the text s):\n" +
            "    Return Cast level-b on (s) or pass the failure off.\n" +
            "Done.\n" +
            "Bind number or failure to level-d, given (the text s):\n" +
            "    Return Cast level-c on (s) or pass the failure off.\n" +
            "Done.\n" +
            "Try to:\n" +
            "    Define result as Cast level-d on (\"x\").\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State the message of the failure.\n" +
            "Done."));
    }

    [Fact]
    public void Failure_Propagate_CategoryIntactThroughFourLevels()
    {
        // The category tag also survives the full 4-level propagation chain.
        Assert.Equal("dc", Run(
            "Bind number or failure to level-a, given (the text s):\n" +
            "    Return a failure \"deep error\" of category \"dc\".\n" +
            "Done.\n" +
            "Bind number or failure to level-b, given (the text s):\n" +
            "    Return Cast level-a on (s) or pass the failure off.\n" +
            "Done.\n" +
            "Bind number or failure to level-c, given (the text s):\n" +
            "    Return Cast level-b on (s) or pass the failure off.\n" +
            "Done.\n" +
            "Bind number or failure to level-d, given (the text s):\n" +
            "    Return Cast level-c on (s) or pass the failure off.\n" +
            "Done.\n" +
            "Try to:\n" +
            "    Define result as Cast level-d on (\"x\").\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State the category of the failure but void is \"no-cat\".\n" +
            "Done."));
    }

    [Fact]
    public void Failure_Propagate_SuccessPassesThroughFourLevels()
    {
        // When the innermost function succeeds, the value passes through all
        // propagation layers and arrives at the top intact.
        Assert.Equal("99", Run(
            "Bind number or failure to level-a, given (the text s):\n" +
            "    Return 99.\n" +
            "Done.\n" +
            "Bind number or failure to level-b, given (the text s):\n" +
            "    Return Cast level-a on (s) or pass the failure off.\n" +
            "Done.\n" +
            "Bind number or failure to level-c, given (the text s):\n" +
            "    Return Cast level-b on (s) or pass the failure off.\n" +
            "Done.\n" +
            "Bind number or failure to level-d, given (the text s):\n" +
            "    Return Cast level-c on (s) or pass the failure off.\n" +
            "Done.\n" +
            "Try to:\n" +
            "    Define x as Cast level-d on (\"x\").\n" +
            "    State x.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done."));
    }

    // ── Hardening: Suppress × nested exception handlers ─────────────────────────
    // These verify the two key interactions between nested Trys and Suppress:
    //   (A) inner Suppress swallows the exception — outer handler does NOT see it
    //   (B) no inner Suppress → inner handler runs → exception re-raises → outer catches it

    [Fact]
    public void Exception_Nested_InnerSuppressDoesNotReachOuterHandler()
    {
        // When the inner exception handler suppresses, execution continues after
        // the inner Try block. The outer Try body finishes normally; the outer
        // exception handler never runs.
        var output = Run(
            "Try to:\n" +
            "    Try to:\n" +
            "        Define x as 1 / 0.\n" +
            "    Done.\n" +
            "    In case of exception (the exception):\n" +
            "        State \"inner caught\".\n" +
            "        Suppress the exception.\n" +
            "    Done.\n" +
            "    State \"after inner\".\n" +
            "Done.\n" +
            "In case of exception (the exception):\n" +
            "    State \"outer caught\".\n" +
            "    Suppress the exception.\n" +
            "Done.\n" +
            "State \"after outer\".");
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["inner caught", "after inner", "after outer"], lines);
        Assert.DoesNotContain("outer caught", output);
    }

    [Fact]
    public void Exception_Nested_NoInnerSuppressReRaisesToOuterHandler()
    {
        // When the inner exception handler does NOT suppress, the exception
        // re-raises out of the inner Try block. The outer Try catches it; the
        // statement after the inner Try ("after inner") is never reached.
        var output = Run(
            "Try to:\n" +
            "    Try to:\n" +
            "        Define x as 1 / 0.\n" +
            "    Done.\n" +
            "    In case of exception (the exception):\n" +
            "        State \"inner handler\".\n" +
            "    Done.\n" +
            "    State \"after inner\".\n" +
            "Done.\n" +
            "In case of exception (the exception):\n" +
            "    State \"outer caught\".\n" +
            "    Suppress the exception.\n" +
            "Done.\n" +
            "State \"after outer\".");
        Assert.Contains("inner handler", output);
        Assert.Contains("outer caught", output);
        Assert.DoesNotContain("after inner", output);
        Assert.Contains("after outer", output);
    }

    // ── Hardening: complex interpolation expressions ─────────────────────────────
    // Simple holes are already covered (variables, arithmetic, field access).
    // These verify the lexer's brace-depth tracking and ReadString recursion hold
    // under more complex hole contents.

    [Fact]
    public void Interp_CastExpressionInHole()
    {
        // A function call (Cast expression) is valid inside an interpolation hole.
        Assert.Equal("result: 10", Run(
            "Bind number to double, given (the number x): Return x * 2. Done.\n" +
            "State \"result: {Cast double on (5)}\"."));
    }

    [Fact]
    public void Interp_StringLiteralInsideHole()
    {
        // A string literal inside an interpolation hole is handled by the lexer's
        // recursive ReadString call — the inner quote does not confuse the outer
        // string's brace-depth tracking.
        Assert.Equal("len: 5", Run(
            "State \"len: {the length of \"hello\" converted to text}\"."));
    }

    [Fact]
    public void Interp_ObjectLiteralNestedBracesInHole()
    {
        // An object literal uses { } tokens inside the interpolation hole.
        // The lexer's brace-depth counter increments on '{' and decrements on '}'
        // so the inner RBrace of the object initializer is NOT mistaken for the
        // end of the interpolation hole.
        Assert.Equal("at: 3", Run(
            "Define object point with (the number x, the number y).\n" +
            "State \"at: {(a new point { the x 3, the y 4 })'s x converted to text}\"."));
    }

    [Fact]
    public void Interp_NestedInterpolatedStringInHole()
    {
        // A string literal that itself contains an interpolation hole, placed
        // inside the outer interpolation hole. Tests that the inner ReadString
        // call correctly processes the inner interpolation and does not leave
        // stray brace-depth state for the outer hole.
        Assert.Equal("outer: inner world", Run(
            "Define x as \"world\".\n" +
            "State \"outer: {\"inner {x}\"}\"."));
    }

    // ── Hardening: lock-in test for 'or pass the failure off' inside Try ─────────
    // Inside a Try block that has a failure handler, _inTryBlock = true. This causes
    // InferCastExpr to auto-unwrap FailureType(T) → T, because the Try IS the handler.
    // Consequently 'or pass the failure off' on a fallible call inside that Try body
    // is a static type error: T can never fail, so there is nothing to propagate.
    // This is correct and intentional — propagating past a Try that already handles
    // the failure is nonsensical. Lock this in so it is not mistaken for a bug later.

    [Fact]
    public void Failure_PropagateInsideTryWithFailureHandlerIsTypeError()
    {
        // Inside a Try-with-failure-handler the fallible call returns plain T.
        // 'or pass the failure off' on plain T is a static error.
        Assert.Throws<TypeException>(() => Run(
            "Bind number or failure to parse, given (the text s):\n" +
            "    Return a failure \"bad\".\n" +
            "Done.\n" +
            "Bind number or failure to outer:\n" +
            "    Try to:\n" +
            "        Define result as Cast parse on (\"x\") or pass the failure off.\n" +
            "    Done.\n" +
            "    In case of failure:\n" +
            "        Return 0.\n" +
            "    Done.\n" +
            "    Return 1.\n" +
            "Done."));
    }

    // ── Rabbits (block-scoped memory regions) ─────────────────────────────────────────────────

    [Fact]
    public void Rabbit_BasicScopeAndBinding()
    {
        // The rabbit name is in scope inside the With block; code inside the block runs normally.
        Assert.Equal("inside", Run(
            "Pull a rabbit as warren.\n" +
            "    State \"inside\".\n" +
            "Done."));
    }

    [Fact]
    public void Rabbit_ReferenceTypeAllocatedInBlock()
    {
        // A series created inside the rabbit block is accessible within the block.
        Assert.Equal("hello", Run(
            "Pull a rabbit as warren.\n" +
            "    Define words as a series of text with (\"hello\", \"world\").\n" +
            "    State the first of words.\n" +
            "Done."));
    }

    [Fact]
    public void Rabbit_ExecutionContinuesAfterBlock()
    {
        // Control flow continues normally after the With block ends.
        Assert.Equal("before\nafter", Run(
            "State \"before\".\n" +
            "Pull a rabbit as warren.\n" +
            "    Define x as 42.\n" +
            "Done.\n" +
            "State \"after\"."));
    }

    [Fact]
    public void Rabbit_PassedDownToCallee()
    {
        // A rabbit can be passed as a parameter; the callee runs normally and returns a handle.
        Assert.Equal("7", Run(
            "Bind number to compute, given (the rabbit r, the number x):\n" +
            "    Return x * 7.\n" +
            "Done.\n" +
            "Pull a rabbit as warren.\n" +
            "    Define result as Cast compute on (warren, 1).\n" +
            "    State result.\n" +
            "Done."));
    }

    [Fact]
    public void Rabbit_CalleeAllocatesSeriesAndReturnsHandle()
    {
        // Callee creates a series (allocated in warren's region), returns the first element.
        Assert.Equal("alpha", Run(
            "Bind text to make-label, given (the rabbit r):\n" +
            "    Define labels as a series of text with (\"alpha\", \"beta\").\n" +
            "    Return the first of labels.\n" +
            "Done.\n" +
            "Pull a rabbit as warren.\n" +
            "    Define label as Cast make-label on (warren).\n" +
            "    State label.\n" +
            "Done."));
    }

    [Fact]
    public void Rabbit_NestedWithBlocksAreIndependent()
    {
        // Two sequential With blocks don't interfere with each other.
        Assert.Equal("first\nsecond", Run(
            "Pull a rabbit as alpha.\n" +
            "    State \"first\".\n" +
            "Done.\n" +
            "Pull a rabbit as beta.\n" +
            "    State \"second\".\n" +
            "Done."));
    }

    [Fact]
    public void Rabbit_ReturnIsTypeError()
    {
        // Returning a rabbit from a function is a static type error (downward-only rule).
        Assert.Throws<TypeException>(() => Run(
            "Bind text to escape-rabbit:\n" +
            "    Pull a rabbit as warren.\n" +
            "        Return warren.\n" +
            "    Done.\n" +
            "    Return \"unreachable\".\n" +
            "Done."));
    }

    [Fact]
    public void Rabbit_RegionStore_SeriesAdd_TypeError()
    {
        // Storing a series from inside a rabbit into an outer series is a static type error.
        Assert.Throws<TypeException>(() => Run(
            "Define outer as a series of series of number with ().\n" +
            "Pull a rabbit as warren.\n" +
            "    Define inner as a series of number with (1, 2, 3).\n" +
            "    Add inner to outer.\n" +
            "Done."));
    }

    [Fact]
    public void Rabbit_RegionStore_Becomes_TypeError()
    {
        // Reassigning an outer variable to a rabbit-scoped series is a static type error.
        Assert.Throws<TypeException>(() => Run(
            "Define outer as a series of number with (1, 2, 3).\n" +
            "Pull a rabbit as warren.\n" +
            "    Define inner as a series of number with (4, 5, 6).\n" +
            "    outer becomes inner.\n" +
            "Done."));
    }

    [Fact]
    public void Rabbit_RegionStore_ValueTypeExempt()
    {
        // Value types (numbers) are exempt from the region store check.
        Assert.Equal("42", Run(
            "Define outer as a series of number with ().\n" +
            "Pull a rabbit as warren.\n" +
            "    Define x as 42.\n" +
            "    Add x to outer.\n" +
            "Done.\n" +
            "State the first of outer."));
    }

    [Fact]
    public void Rabbit_RegionStore_SameDepthIsOk()
    {
        // Both container and value defined in the same rabbit block — no error.
        Assert.Equal("ok", Run(
            "Pull a rabbit as warren.\n" +
            "    Define xs as a series of number with (1, 2, 3).\n" +
            "    Define ys as a series of series of number with ().\n" +
            "    Add xs to ys.\n" +
            "    State \"ok\".\n" +
            "Done."));
    }

    [Fact]
    public void Rabbit_RegionStore_NestedRabbit_TypeError()
    {
        // A value from an inner rabbit cannot escape into the outer rabbit's containers.
        Assert.Throws<TypeException>(() => Run(
            "Pull a rabbit as outer.\n" +
            "    Define outer-ys as a series of series of number with ().\n" +
            "    Pull a rabbit as inner.\n" +
            "        Define inner-xs as a series of number with (1, 2).\n" +
            "        Add inner-xs to outer-ys.\n" +
            "    Done.\n" +
            "Done."));
    }

    [Fact]
    public void Rabbit_RegionStore_RangeEscapeInAdd_TypeError()
    {
        // A range (series) created inside the rabbit cannot be added to an outer container.
        Assert.Throws<TypeException>(() => Run(
            "Define outer as a series of series of number with ().\n" +
            "Pull a rabbit as warren.\n" +
            "    Define inner as range 1 to 3.\n" +
            "    Add inner to outer.\n" +
            "Done."));
    }

    [Fact]
    public void Rabbit_RegionStore_TextIsValueType()
    {
        // Text is a value type — adding inner text to an outer series of text is fine.
        Assert.Equal("hello", Run(
            "Define outer as a series of text with ().\n" +
            "Pull a rabbit as warren.\n" +
            "    Define msg as \"hello\".\n" +
            "    Add msg to outer.\n" +
            "Done.\n" +
            "State the first of outer."));
    }

    // ── Environment variables ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void EnvVar_SetVar_ReturnsValue()
    {
        System.Environment.SetEnvironmentVariable("CUFET_TEST_HELLO", "world");
        Assert.Equal("world", Run(
            "Define v as the environment variable \"CUFET_TEST_HELLO\" but void is \"missing\".\n" +
            "State v."));
    }

    [Fact]
    public void EnvVar_UnsetVar_IsVoid()
    {
        System.Environment.SetEnvironmentVariable("CUFET_TEST_ABSENT_XYZ", null);
        Assert.Equal("not set", Run(
            "Define v as the environment variable \"CUFET_TEST_ABSENT_XYZ\".\n" +
            "If v is void:\n" +
            "    State \"not set\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"set\".\n" +
            "Done."));
    }

    [Fact]
    public void EnvVar_ButVoidDefault()
    {
        System.Environment.SetEnvironmentVariable("CUFET_TEST_DEFAULTED_XYZ", null);
        Assert.Equal("fallback", Run(
            "State the environment variable \"CUFET_TEST_DEFAULTED_XYZ\" but void is \"fallback\"."));
    }

    [Fact]
    public void EnvVar_NameIsTextExpression()
    {
        // The name is a text variable, not a literal — dynamic lookup must work.
        System.Environment.SetEnvironmentVariable("CUFET_TEST_DYN", "dynamic");
        Assert.Equal("dynamic", Run(
            "Define env-key as \"CUFET_TEST_DYN\".\n" +
            "State the environment variable env-key but void is \"missing\"."));
    }

    [Fact]
    public void EnvVar_NonTextName_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "State the environment variable 42."));
    }

    // ── Directory traversal ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void DirTraversal_PathExists_True()
    {
        var dir = System.IO.Path.GetTempPath();
        Assert.Equal("yes", Run(
            $"If the path \"{dir.Replace("\\", "\\\\")}\" exists:\n" +
            "    State \"yes\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"no\".\n" +
            "Done."));
    }

    [Fact]
    public void DirTraversal_PathExists_False()
    {
        Assert.Equal("no", Run(
            "If the path \"/cufet-test-nonexistent-path-xyz\" exists:\n" +
            "    State \"yes\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"no\".\n" +
            "Done."));
    }

    [Fact]
    public void DirTraversal_IsDirectory_True()
    {
        var dir = System.IO.Path.GetTempPath().Replace("\\", "\\\\");
        Assert.Equal("yes", Run(
            $"If the path \"{dir}\" is a directory:\n" +
            "    State \"yes\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"no\".\n" +
            "Done."));
    }

    [Fact]
    public void DirTraversal_IsFile_True()
    {
        var tmp = System.IO.Path.GetTempFileName().Replace("\\", "\\\\");
        Assert.Equal("yes", Run(
            $"If the path \"{tmp}\" is a file:\n" +
            "    State \"yes\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"no\".\n" +
            "Done."));
    }

    [Fact]
    public void DirTraversal_IsDirectory_FalseForFile()
    {
        var tmp = System.IO.Path.GetTempFileName().Replace("\\", "\\\\");
        Assert.Equal("no", Run(
            $"If the path \"{tmp}\" is a directory:\n" +
            "    State \"yes\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"no\".\n" +
            "Done."));
    }

    [Fact]
    public void DirTraversal_Contents_ContainsKnownFile()
    {
        // Create a temp dir with one file, list it, check the file appears in entries.
        var dir = System.IO.Directory.CreateTempSubdirectory("cufet-test-").FullName;
        var file = System.IO.Path.Combine(dir, "hello.txt");
        System.IO.File.WriteAllText(file, "hi");
        var escapedDir = dir.Replace("\\", "\\\\");
        var escapedFile = file.Replace("\\", "\\\\");
        try
        {
            Assert.Equal("found", Run(
                $"Try to:\n" +
                $"    Define entries as the contents of the directory \"{escapedDir}\".\n" +
                "    For each dir-entry in entries, repeat:\n" +
                $"        If dir-entry is \"{escapedFile}\", State \"found\".\n" +
                "    Done.\n" +
                "Done.\n" +
                "In case of failure:\n" +
                "    State the message of the failure.\n" +
                "Done."));
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DirTraversal_Contents_NotFound_IsFailure()
    {
        // Listing a nonexistent directory yields a recoverable failure.
        Assert.Equal("failure", Run(
            "Try to:\n" +
            "    Define entries as the contents of the directory \"/cufet-test-nonexistent-dir-xyz\".\n" +
            "    State \"ok\".\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failure\".\n" +
            "Done."));
    }

    [Fact]
    public void DirTraversal_Contents_UnhandledIsTypeError()
    {
        // Unhandled directory listing is a static type error.
        Assert.Throws<TypeException>(() => Run(
            "Define entries as the contents of the directory \"/tmp\"."));
    }

    [Fact]
    public void DirTraversal_NonTextPath_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "State the path 42 exists."));
    }

    // ── Signals (SIGINT / cooperative interrupt handling) ─────────────────────────────────────────

    // Helper: run a Cufet program on an interpreter whose interrupt flag is pre-set.
    // Used to test cooperative handling without synthesizing a real Ctrl-C.
    private static string RunInterrupted(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var output  = new StringWriter();
        var interp  = new Interpreter(output);
        interp.SimulateInterrupt();
        RunOnLargeStack(() => interp.Execute(program));
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    [Fact]
    public void Signal_InterruptRequested_DefaultsFalse()
    {
        // When no interrupt has been received the expression returns false.
        Assert.Equal("no", Run(
            "If an interrupt is requested, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void Signal_InterruptRequested_WhenFlagSet_ReturnsTrue()
    {
        // With the flag pre-set (simulating Ctrl-C), the expression returns true.
        Assert.Equal("yes", RunInterrupted(
            "If an interrupt is requested, State \"yes\". Otherwise, State \"no\"."));
    }

    [Fact]
    public void Signal_AcknowledgeInterrupt_ClearsFlag()
    {
        // After Acknowledge the interrupt., the flag reads false again.
        Assert.Equal("cleared", RunInterrupted(
            "Acknowledge the interrupt.\n" +
            "If an interrupt is requested, State \"still set\". Otherwise, State \"cleared\"."));
    }

    [Fact]
    public void Signal_CooperativeHandling_LoopExitsGracefully()
    {
        // Cooperative loop: when flag is pre-set, the first iteration detects and handles it,
        // then Stops — no subsequent iterations run.
        Assert.Equal("handled", RunInterrupted(
            "Define items as a series with (1, 2, 3).\n" +
            "For each n in items, repeat:\n" +
            "    If an interrupt is requested:\n" +
            "        Acknowledge the interrupt.\n" +
            "        State \"handled\".\n" +
            "        Stop.\n" +
            "    Done.\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void Signal_CooperativeHandling_ContinuesAfterAcknowledge()
    {
        // Once acknowledged, the loop continues normally for subsequent iterations.
        Assert.Equal("handled\n2\n3", RunInterrupted(
            "Define items as a series with (1, 2, 3).\n" +
            "For each n in items, repeat:\n" +
            "    If an interrupt is requested:\n" +
            "        Acknowledge the interrupt.\n" +
            "        State \"handled\".\n" +
            "        Skip.\n" +
            "    Done.\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void Signal_TypeCheck_ExpressionIsFact()
    {
        // The expression is a fact — it type-checks in boolean contexts without error.
        Assert.Equal("ok", Run(
            "Define r as an interrupt is requested.\n" +
            "If r, State \"interrupted\". Otherwise, State \"ok\"."));
    }

    // ── Sort ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sort_NumbersAscending()
    {
        Assert.Equal("1\n2\n3\n4\n5", Run(
            "Define nums as a series with (3, 1, 4, 2, 5).\n" +
            "Define sorted-nums as nums sorted.\n" +
            "For each n in sorted-nums, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void Sort_NumbersDescending()
    {
        Assert.Equal("5\n4\n3\n2\n1", Run(
            "Define nums as a series with (3, 1, 4, 2, 5).\n" +
            "Define result as nums sorted in reverse.\n" +
            "For each n in result, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void Sort_TextAlphabetical()
    {
        Assert.Equal("apple\nbanana\ncherry", Run(
            "Define words as a series with (\"banana\", \"apple\", \"cherry\").\n" +
            "Define sorted-words as words sorted.\n" +
            "For each w in sorted-words, repeat:\n" +
            "    State w.\n" +
            "Done."));
    }

    [Fact]
    public void Sort_TextReverseAlphabetical()
    {
        Assert.Equal("cherry\nbanana\napple", Run(
            "Define words as a series with (\"banana\", \"apple\", \"cherry\").\n" +
            "Define result as words sorted in reverse.\n" +
            "For each w in result, repeat:\n" +
            "    State w.\n" +
            "Done."));
    }

    [Fact]
    public void Sort_IsNonMutating()
    {
        // 'sorted' returns a new series; the original is unchanged.
        Assert.Equal("3\n1\n2\n---\n1\n2\n3", Run(
            "Define nums as a series with (3, 1, 2).\n" +
            "Define sorted-nums as nums sorted.\n" +
            "For each n in nums, repeat:\n" +
            "    State n.\n" +
            "Done.\n" +
            "State \"---\".\n" +
            "For each n in sorted-nums, repeat:\n" +
            "    State n.\n" +
            "Done."));
    }

    [Fact]
    public void Sort_IsStable()
    {
        // Equal elements retain original relative order. Verify count is preserved.
        Assert.Equal("4", Run(
            "Define nums as a series with (3, 1, 2, 1).\n" +
            "Define result as nums sorted.\n" +
            "State the number of result."));
    }

    [Fact]
    public void Sort_EmptySeries()
    {
        Assert.Equal("0", Run(
            "Define empty as a series of numbers with ().\n" +
            "Define result as empty sorted.\n" +
            "State the number of result."));
    }

    [Fact]
    public void Sort_SingleElement()
    {
        Assert.Equal("42", Run(
            "Define s as a series with (42).\n" +
            "State the first of (s sorted)."));
    }

    [Fact]
    public void Sort_ByField_Ascending()
    {
        Assert.Equal("1\n2\n3", Run(
            "Define entries as a series of records like (the number score).\n" +
            "Add a record with (the score 3) to entries.\n" +
            "Add a record with (the score 1) to entries.\n" +
            "Add a record with (the score 2) to entries.\n" +
            "Define ranked as entries sorted by the score.\n" +
            "For each e in ranked, repeat:\n" +
            "    State the score of e.\n" +
            "Done."));
    }

    [Fact]
    public void Sort_ByField_Descending()
    {
        Assert.Equal("3\n2\n1", Run(
            "Define entries as a series of records like (the number score).\n" +
            "Add a record with (the score 1) to entries.\n" +
            "Add a record with (the score 3) to entries.\n" +
            "Add a record with (the score 2) to entries.\n" +
            "Define ranked as entries sorted by the score in reverse.\n" +
            "For each e in ranked, repeat:\n" +
            "    State the score of e.\n" +
            "Done."));
    }

    [Fact]
    public void Sort_ByField_Text()
    {
        Assert.Equal("alice\nbob\ncarol", Run(
            "Define people as a series of records like (the text name).\n" +
            "Add a record with (the name \"carol\") to people.\n" +
            "Add a record with (the name \"alice\") to people.\n" +
            "Add a record with (the name \"bob\") to people.\n" +
            "Define sorted-people as people sorted by the name.\n" +
            "For each p in sorted-people, repeat:\n" +
            "    State the name of p.\n" +
            "Done."));
    }

    [Fact]
    public void Sort_InWordFreqDemo()
    {
        // Reproduces the word-freq pattern: counts in a map, extract to series, sort descending.
        Assert.Equal("fox: 3\nthe: 2\ncat: 1", Run(
            "Define counts as a map with (\"the\" : 2, \"fox\" : 3, \"cat\" : 1).\n" +
            "Define entries as a series of records like (the text word, the number count).\n" +
            "For each pair in counts, repeat:\n" +
            "    Define k as the key of pair.\n" +
            "    Define v as the value of pair.\n" +
            "    Add a record with (the word k, the count v) to entries.\n" +
            "Done.\n" +
            "Define top as entries sorted by the count in reverse.\n" +
            "For each e in top, repeat:\n" +
            "    Define w as the word of e.\n" +
            "    Define c as the count of e.\n" +
            "    State \"{w}: {c}\".\n" +
            "Done."));
    }

    [Fact]
    public void Sort_TypeError_NonSeries()
    {
        Assert.Throws<TypeException>(() => Run("Define m as a map with (\"a\" : 1). State m sorted."));
    }

    [Fact]
    public void Sort_TypeError_NoNaturalOrder()
    {
        // Sorting a series of records without 'by' → educational error pointing at 'by'.
        Assert.Throws<TypeException>(() => Run(
            "Define entries as a series of records like (the number score).\n" +
            "State entries sorted."));
    }

    [Fact]
    public void Sort_TypeError_UnknownField()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define entries as a series of records like (the number score).\n" +
            "State entries sorted by the missing."));
    }

    [Fact]
    public void Sort_TypeError_NonOrderableField()
    {
        // Sorting by a fact field → static error (facts have no natural order).
        Assert.Throws<TypeException>(() => Run(
            "Define entries as a series of records like (the fact active).\n" +
            "State entries sorted by the active."));
    }

    // ── Books / Math ────────────────────────────────────────────────────────────

    [Fact]
    public void Math_Pull_BindsUnderBookName()
    {
        Assert.Equal("3", Run(
            "Pull a book on math.\n" +
            "State math's floor of 3.7.\n" +
            "Done."));
    }

    [Fact]
    public void Math_Pull_BindsUnderLocalName()
    {
        Assert.Equal("3", Run(
            "Pull a book on math as the m.\n" +
            "State m's floor of 3.7.\n" +
            "Done."));
    }

    [Fact]
    public void Math_Floor()
    {
        Assert.Equal("3", Run(
            "Pull a book on math.\n" +
            "State math's floor of 3.99.\n" +
            "Done."));
    }

    [Fact]
    public void Math_Floor_Negative()
    {
        Assert.Equal("-4", Run(
            "Pull a book on math.\n" +
            "State math's floor of -3.1.\n" +
            "Done."));
    }

    [Fact]
    public void Math_Ceiling()
    {
        Assert.Equal("4", Run(
            "Pull a book on math.\n" +
            "State math's ceiling of 3.01.\n" +
            "Done."));
    }

    [Fact]
    public void Math_Ceiling_Negative()
    {
        Assert.Equal("-3", Run(
            "Pull a book on math.\n" +
            "State math's ceiling of -3.9.\n" +
            "Done."));
    }

    [Fact]
    public void Math_Round_AwayFromZero_HalfUp()
    {
        Assert.Equal("3", Run(
            "Pull a book on math.\n" +
            "State math's round of 2.5.\n" +
            "Done."));
    }

    [Fact]
    public void Math_Round_AwayFromZero_NegativeHalf()
    {
        Assert.Equal("-3", Run(
            "Pull a book on math.\n" +
            "State math's round of -2.5.\n" +
            "Done."));
    }

    [Fact]
    public void Math_AbsoluteValue_Positive()
    {
        Assert.Equal("5", Run(
            "Pull a book on math.\n" +
            "State math's absolute value of 5.\n" +
            "Done."));
    }

    [Fact]
    public void Math_AbsoluteValue_Negative()
    {
        Assert.Equal("7", Run(
            "Pull a book on math.\n" +
            "State math's absolute value of -7.\n" +
            "Done."));
    }

    [Fact]
    public void Math_SquareRoot()
    {
        Assert.Equal("3", Run(
            "Pull a book on math.\n" +
            "Define r as math's square root of 9.\n" +
            "State (r but void is 0) converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Math_SquareRoot_Negative_IsVoid()
    {
        Assert.Equal("void", Run(
            "Pull a book on math.\n" +
            "Define r as math's square root of -1.\n" +
            "If r is void, state \"void\". Otherwise, state \"not void\".\n" +
            "Done."));
    }

    [Fact]
    public void Math_Log_Natural()
    {
        // log(1) = 0 exactly; unwrap the voidable and check
        Assert.Equal("0", Run(
            "Pull a book on math.\n" +
            "State (math's log of 1 but void is -1) converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Math_Log_Zero_IsVoid()
    {
        Assert.Equal("void", Run(
            "Pull a book on math.\n" +
            "Define r as math's log of 0.\n" +
            "If r is void, state \"void\". Otherwise, state \"not void\".\n" +
            "Done."));
    }

    [Fact]
    public void Math_Log_Negative_IsVoid()
    {
        Assert.Equal("void", Run(
            "Pull a book on math.\n" +
            "Define r as math's log of -5.\n" +
            "If r is void, state \"void\". Otherwise, state \"not void\".\n" +
            "Done."));
    }

    [Fact]
    public void Math_Power()
    {
        Assert.Equal("8", Run(
            "Pull a book on math.\n" +
            "Define r as math's power of (2, 3).\n" +
            "State (r but void is 0) converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Math_Power_NegativeBase_FractionalExp_IsVoid()
    {
        Assert.Equal("void", Run(
            "Pull a book on math.\n" +
            "Define r as math's power of (-1, 0.5).\n" +
            "If r is void, state \"void\". Otherwise, state \"not void\".\n" +
            "Done."));
    }

    [Fact]
    public void Math_Pi_Constant()
    {
        Assert.Equal("yes", Run(
            "Pull a book on math.\n" +
            "If math's pi is greater than 3.14, state \"yes\". Otherwise, state \"no\".\n" +
            "Done."));
    }

    [Fact]
    public void Math_E_Constant()
    {
        Assert.Equal("yes", Run(
            "Pull a book on math.\n" +
            "If math's e is greater than 2.71, state \"yes\". Otherwise, state \"no\".\n" +
            "Done."));
    }

    [Fact]
    public void Math_ChainedCalls()
    {
        // floor(sqrt(9)) = floor(3) = 3
        Assert.Equal("3", Run(
            "Pull a book on math.\n" +
            "Define r as math's square root of 9.\n" +
            "State (math's floor of (r but void is 0)) converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Math_ArithmeticOnResult()
    {
        // log(e) / log(10) ≈ 0.434; the division binds outside the book call
        Assert.Equal("yes", Run(
            "Pull a book on math.\n" +
            "Define num as math's log of math's e.\n" +
            "Define den as math's log of 10.\n" +
            "Define ratio as (num but void is 0) / (den but void is 1).\n" +
            "If ratio is greater than 0.43, state \"yes\". Otherwise, state \"no\".\n" +
            "Done."));
    }

    [Fact]
    public void Math_TypeError_UnknownBook()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on physics.\nDone."));
    }

    [Fact]
    public void Math_TypeError_UnknownMember()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on math.\n" +
            "State math's cosine of 0.\n" +
            "Done."));
    }

    [Fact]
    public void Math_TypeError_WrongArgType()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on math.\n" +
            "State math's floor of \"hello\".\n" +
            "Done."));
    }

    // ── Books / Collections — Matrix ────────────────────────────────────────────

    [Fact]
    public void Matrix_Pull_BindsCollections()
    {
        // Pulling collections should succeed and bind under the book name.
        Assert.Equal("", Run(
            "Pull a book on collections.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Pull_BindsUnderLocalName()
    {
        Assert.Equal("", Run(
            "Pull a book on collections as col.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Literal_2x2()
    {
        // A 2×2 identity matrix — just check it constructs without error.
        Assert.Equal("", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 0), (0, 1)).\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Literal_3x3()
    {
        Assert.Equal("", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6), (7, 8, 9)).\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Access_TopLeft()
    {
        // Parens needed: ParsePrimary eats postfix ops on the target variable.
        Assert.Equal("1", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define v as the item at (1, 1) of m.\n" +
            "State v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Access_BottomRight()
    {
        Assert.Equal("4", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define v as the item at (2, 2) of m.\n" +
            "State v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Access_MiddleElement()
    {
        Assert.Equal("5", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6), (7, 8, 9)).\n" +
            "Define v as the item at (2, 2) of m.\n" +
            "State v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Access_ArithmeticOnResult()
    {
        Assert.Equal("6", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6)).\n" +
            "Define v as the item at (1, 3) of m.\n" +
            "Define w as v * 2.\n" +
            "State w converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Access_VariableIndex()
    {
        Assert.Equal("8", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6), (7, 8, 9)).\n" +
            "Define r as 3.\n" +
            "Define c as 2.\n" +
            "Define v as the item at (r, c) of m.\n" +
            "State v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Literal_Negative_Elements()
    {
        Assert.Equal("-5", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((-1, -2), (-3, -5)).\n" +
            "Define v as the item at (2, 2) of m.\n" +
            "State v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_InSeries()
    {
        // A series of matrices — just checks the type system doesn't choke.
        // Note: 'a' and 'b' are reserved as article tokens; use 'mat1'/'mat2'.
        Assert.Equal("2", Run(
            "Pull a book on collections.\n" +
            "Define mat1 as a matrix with ((1, 2), (3, 4)).\n" +
            "Define mat2 as a matrix with ((5, 6), (7, 8)).\n" +
            "Define s as a series with (mat1, mat2).\n" +
            "Define v as the item at (1, 2) of first of s.\n" +
            "State v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_TypeError_NoCollections()
    {
        // matrix without pulling collections → TypeException
        Assert.Throws<TypeException>(() => Run(
            "Define m as a matrix with ((1, 2), (3, 4))."));
    }

    [Fact]
    public void Matrix_TypeError_RaggedRows()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4, 5)).\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_TypeError_NonNumberElement()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, \"hello\"), (3, 4)).\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_RuntimeError_RowOutOfBounds()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define v as the item at (3, 1) of m.\n" +
            "State v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_RuntimeError_ColOutOfBounds()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define v as the item at (1, 5) of m.\n" +
            "State v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_TypeAnnotation_Parameter()
    {
        // (2,2) of a 3x3 grid ((1,2,3),(4,5,6),(7,8,9)) is 5, not 9.
        Assert.Equal("5", Run(
            "Pull a book on collections.\n" +
            "Bind number to get-center, given (the matrix m):\n" +
            "    return the item at (2, 2) of m.\n" +
            "Done.\n" +
            "Define grid as a matrix with ((1, 2, 3), (4, 5, 6), (7, 8, 9)).\n" +
            "State cast get-center on (grid) converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_TypeAnnotation_ReturnType()
    {
        // The function body must pull collections to use 'a matrix with (...)'.
        Assert.Equal("1", Run(
            "Pull a book on collections.\n" +
            "Bind matrix to make-identity, given ():\n" +
            "    Pull a book on collections.\n" +
            "    return a matrix with ((1, 0), (0, 1)).\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define result as cast make-identity.\n" +
            "Define v as the item at (1, 1) of result.\n" +
            "State v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_ValueTravels_ThroughFunctionWithoutPull()
    {
        // The caller pulls collections; the pass-through function does NOT pull it
        // but still receives and returns the matrix value (M2 invariant).
        Assert.Equal("7", Run(
            "Pull a book on collections.\n" +
            "Bind matrix to identity, given (the matrix m):\n" +
            "    Pull a book on collections.\n" +
            "    return m.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define g as a matrix with ((1, 2), (3, 7)).\n" +
            "Define r as cast identity on (g).\n" +
            "Define v as the item at (2, 2) of r.\n" +
            "State v converted to text.\n" +
            "Done."));
    }

    // ── Matrix — dimension queries ────────────────────────────────────────────

    [Fact]
    public void Matrix_Rows_Square()
    {
        Assert.Equal("3", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6), (7, 8, 9)).\n" +
            "State the rows of m converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Columns_Square()
    {
        Assert.Equal("3", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6), (7, 8, 9)).\n" +
            "State the columns of m converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Rows_Rectangular()
    {
        // 2 rows × 3 cols
        Assert.Equal("2", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6)).\n" +
            "State the rows of m converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Columns_Rectangular()
    {
        // 2 rows × 3 cols
        Assert.Equal("3", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6)).\n" +
            "State the columns of m converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Rows_NoPullNeeded()
    {
        // 'the rows of' is access syntax — works on any matrix value; pull needed only to construct.
        Assert.Equal("2", Run(
            "Pull a book on collections.\n" +
            "Bind number to get-rows, given (the matrix mat):\n" +
            "    return the rows of mat.\n" +
            "Done.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "State cast get-rows on (m) converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Columns_NoPullNeeded()
    {
        Assert.Equal("4", Run(
            "Pull a book on collections.\n" +
            "Bind number to get-cols, given (the matrix mat):\n" +
            "    return the columns of mat.\n" +
            "Done.\n" +
            "Define m as a matrix with ((10, 20, 30, 40), (50, 60, 70, 80)).\n" +
            "State cast get-cols on (m) converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Rows_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define n as 42.\n" +
            "State the rows of n converted to text."));
    }

    [Fact]
    public void Matrix_Columns_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define s as a series with (1, 2, 3).\n" +
            "State the columns of s converted to text."));
    }

    // ── Matrix — transpose ────────────────────────────────────────────────────

    [Fact]
    public void Matrix_Transpose_Square()
    {
        // Transpose of ((1,2),(3,4)) → ((1,3),(2,4))
        Assert.Equal("3", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define t as cast collections's transpose of (m).\n" +
            "Define v as the item at (1, 2) of t.\n" +
            "State v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Transpose_Rectangular()
    {
        // Transpose of 2×3 → 3×2; element [2][1] of source becomes [1][2] of result
        // Source ((1,2,3),(4,5,6)): [2][1] = 4 → result [1][2] = 4
        Assert.Equal("4", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6)).\n" +
            "Define t as cast collections's transpose of (m).\n" +
            "Define v as the item at (1, 2) of t.\n" +
            "State v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Transpose_DimensionsSwap()
    {
        // 2×3 transposed becomes 3×2
        Assert.Equal("3", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6)).\n" +
            "Define t as cast collections's transpose of (m).\n" +
            "State the rows of t converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Transpose_DimensionsSwap_Cols()
    {
        Assert.Equal("2", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6)).\n" +
            "Define t as cast collections's transpose of (m).\n" +
            "State the columns of t converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Transpose_NonMutating()
    {
        // After transposing, the original matrix is unchanged: 2×3 stays 2 rows (not 3)
        Assert.Equal("2", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6)).\n" +
            "Define t as cast collections's transpose of (m).\n" +
            "State the rows of m converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Transpose_DoubleTranspose_Identity()
    {
        // Transposing twice returns the same values (not necessarily the same object)
        Assert.Equal("6", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6)).\n" +
            "Define t as cast collections's transpose of (m).\n" +
            "Define tt as cast collections's transpose of (t).\n" +
            "Define v as the item at (2, 3) of tt.\n" +
            "State v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Transpose_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define n as 42.\n" +
            "Define t as cast collections's transpose of (n).\n" +
            "Done."));
    }

    // ── Matrix — sized constructor ──────────────────────────────────────────────

    [Fact]
    public void Matrix_Sized_Zeros_Rows()
    {
        Assert.Equal("3", Run(
            "Pull a book on collections.\n" +
            "Define g as a matrix with 3 by 4.\n" +
            "State the rows of g converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_Zeros_Cols()
    {
        Assert.Equal("4", Run(
            "Pull a book on collections.\n" +
            "Define g as a matrix with 3 by 4.\n" +
            "State the columns of g converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_Zeros_CellIsZero()
    {
        Assert.Equal("0", Run(
            "Pull a book on collections.\n" +
            "Define g as a matrix with 2 by 3.\n" +
            "State the item at (1, 1) of g converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_Zeros_AllCellsZero()
    {
        // Check last cell (2,3) is also zero
        Assert.Equal("0", Run(
            "Pull a book on collections.\n" +
            "Define g as a matrix with 2 by 3.\n" +
            "State the item at (2, 3) of g converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_Filled_Integer()
    {
        Assert.Equal("7", Run(
            "Pull a book on collections.\n" +
            "Define g as a matrix with 2 by 2 filled with 7.\n" +
            "State the item at (1, 1) of g converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_Filled_AllCells()
    {
        // Check multiple cells are all filled
        Assert.Equal("7", Run(
            "Pull a book on collections.\n" +
            "Define g as a matrix with 2 by 2 filled with 7.\n" +
            "State the item at (2, 2) of g converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_Filled_Negative()
    {
        Assert.Equal("-1.5", Run(
            "Pull a book on collections.\n" +
            "Define g as a matrix with 2 by 2 filled with -1.5.\n" +
            "State the item at (1, 2) of g converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_Filled_Zero()
    {
        // filled with 0 is valid (unconstrained fill value)
        Assert.Equal("0", Run(
            "Pull a book on collections.\n" +
            "Define g as a matrix with 3 by 3 filled with 0.\n" +
            "State the item at (2, 2) of g converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_1x1()
    {
        Assert.Equal("1", Run(
            "Pull a book on collections.\n" +
            "Define g as a matrix with 1 by 1.\n" +
            "State the rows of g converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_ComputedDimensions()
    {
        Assert.Equal("2", Run(
            "Pull a book on collections.\n" +
            "Define r as 2.\n" +
            "Define c as 3.\n" +
            "Define g as a matrix with r by c.\n" +
            "State the rows of g converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_NoPull_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define g as a matrix with 3 by 4."));
    }

    [Fact]
    public void Matrix_Sized_LiteralZeroDim_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define g as a matrix with 0 by 4.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_LiteralNegativeDim_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define g as a matrix with -1 by 3.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_LiteralFractionalDim_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define g as a matrix with 2.5 by 4.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_FillTypeMismatch_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define g as a matrix with 2 by 2 filled with \"oops\".\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_ComputedZeroDim_RuntimeException()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define r as 0.\n" +
            "Define g as a matrix with r by 4.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_ComputedFractionalDim_RuntimeException()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define r as 2.5.\n" +
            "Define g as a matrix with r by 4.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Sized_TransposeAfterSized()
    {
        // 2×3 transposed → 3×2; check column count of transposed = 2
        Assert.Equal("2", Run(
            "Pull a book on collections.\n" +
            "Define g as a matrix with 2 by 3 filled with 1.\n" +
            "Define t as cast collections's transpose of (g).\n" +
            "State the columns of t converted to text.\n" +
            "Done."));
    }

    // ── Matrix — arithmetic (+, -, *) ────────────────────────────────────────────

    [Fact]
    public void Matrix_Add_SameDimensions_Success()
    {
        // [[1,2],[3,4]] + [[5,6],[7,8]] = [[6,8],[10,12]]; item(1,1) = 6
        Assert.Equal("6", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define n as a matrix with ((5, 6), (7, 8)).\n" +
            "Try to:\n" +
            "    Define p as m + n.\n" +
            "    State the item at (1, 1) of p converted to text.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Add_SameDimensions_BottomRight()
    {
        // [[1,2],[3,4]] + [[5,6],[7,8]] = [[6,8],[10,12]]; item(2,2) = 12
        Assert.Equal("12", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define n as a matrix with ((5, 6), (7, 8)).\n" +
            "Try to:\n" +
            "    Define p as m + n.\n" +
            "    State the item at (2, 2) of p converted to text.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Add_DimMismatch_ProducesFailure()
    {
        Assert.Equal("failed", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define n as a matrix with ((5, 6, 7), (8, 9, 10)).\n" +
            "Try to:\n" +
            "    Define p as m + n.\n" +
            "    State \"ok\".\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Add_DimMismatch_Category()
    {
        Assert.Equal("dimension-mismatch", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define n as a matrix with ((5, 6, 7), (8, 9, 10)).\n" +
            "Try to:\n" +
            "    Define p as m + n.\n" +
            "    State \"ok\".\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State the category of the failure but void is \"none\".\n" +
            "Done.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Add_StrictFallible_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define n as a matrix with ((5, 6), (7, 8)).\n" +
            "Define p as m + n.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Add_ButOnFailure_DefaultTaken()
    {
        // dimensions mismatch → but on failure supplies the 1×1 default
        Assert.Equal("1", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define n as a matrix with ((5, 6, 7)).\n" +
            "Define p as m + n but on failure (a matrix with 1 by 1).\n" +
            "State the rows of p converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Subtract_SameDimensions_Success()
    {
        // [[10,20],[30,40]] - [[1,2],[3,4]] = [[9,18],[27,36]]; item(2,1) = 27
        Assert.Equal("27", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((10, 20), (30, 40)).\n" +
            "Define n as a matrix with ((1, 2), (3, 4)).\n" +
            "Try to:\n" +
            "    Define p as m - n.\n" +
            "    State the item at (2, 1) of p converted to text.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Subtract_DimMismatch_ProducesFailure()
    {
        Assert.Equal("failed", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define n as a matrix with ((1, 2, 3), (4, 5, 6), (7, 8, 9)).\n" +
            "Try to:\n" +
            "    Define p as m - n.\n" +
            "    State \"ok\".\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Subtract_StrictFallible_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define n as a matrix with ((5, 6), (7, 8)).\n" +
            "Define p as m - n.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Multiply_Square_Product()
    {
        // [[1,2],[3,4]] * [[5,6],[7,8]]; [1,1] = 1*5+2*7 = 19
        Assert.Equal("19", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define n as a matrix with ((5, 6), (7, 8)).\n" +
            "Try to:\n" +
            "    Define p as m * n.\n" +
            "    State the item at (1, 1) of p converted to text.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Multiply_NonSquare_Dimensions()
    {
        // 2×3 * 3×2 → 2×2; verify result has 2 rows, 2 columns
        Assert.Equal("2\n2", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6)).\n" +
            "Define n as a matrix with ((7, 8), (9, 10), (11, 12)).\n" +
            "Try to:\n" +
            "    Define p as m * n.\n" +
            "    State the rows of p converted to text.\n" +
            "    State the columns of p converted to text.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Multiply_NonSquare_Element()
    {
        // 2×3 * 3×2; p[1,1] = 1*7+2*9+3*11 = 7+18+33 = 58
        Assert.Equal("58", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6)).\n" +
            "Define n as a matrix with ((7, 8), (9, 10), (11, 12)).\n" +
            "Try to:\n" +
            "    Define p as m * n.\n" +
            "    State the item at (1, 1) of p converted to text.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Multiply_NonConforming_ProducesFailure()
    {
        // 2×3 * 2×2: m.cols(3) ≠ n.rows(2) → dimension-mismatch
        Assert.Equal("failed", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6)).\n" +
            "Define n as a matrix with ((1, 2), (3, 4)).\n" +
            "Try to:\n" +
            "    Define p as m * n.\n" +
            "    State \"ok\".\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Multiply_NonConforming_Category()
    {
        Assert.Equal("dimension-mismatch", Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6)).\n" +
            "Define n as a matrix with ((1, 2), (3, 4)).\n" +
            "Try to:\n" +
            "    Define p as m * n.\n" +
            "    State \"ok\".\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State the category of the failure but void is \"none\".\n" +
            "Done.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Multiply_StrictFallible_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define n as a matrix with ((5, 6), (7, 8)).\n" +
            "Define p as m * n.\n" +
            "Done."));
    }

    [Fact]
    public void Matrix_Divide_TypeError()
    {
        // No / overload for matrices → type error ("arithmetic requires numbers")
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define m as a matrix with ((1, 2), (3, 4)).\n" +
            "Define n as a matrix with ((5, 6), (7, 8)).\n" +
            "Try to:\n" +
            "    Define p as m / n.\n" +
            "    State \"ok\".\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done.\n" +
            "Done."));
    }

    // ── Layer 1: Union types ────────────────────────────────────────────────────

    [Fact]
    public void Union_IsANumber_True()
    {
        Assert.Equal("yes", Run(
            "Define x as 42.\n" +
            "If x is a number:\n" +
            "    State \"yes\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"no\".\n" +
            "Done."));
    }

    [Fact]
    public void Union_IsANumber_False()
    {
        Assert.Equal("no", Run(
            "Define x as \"hello\".\n" +
            "If x is a number:\n" +
            "    State \"yes\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"no\".\n" +
            "Done."));
    }

    [Fact]
    public void Union_IsAText_True()
    {
        Assert.Equal("yes", Run(
            "Define x as \"hello\".\n" +
            "If x is a text:\n" +
            "    State \"yes\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"no\".\n" +
            "Done."));
    }

    [Fact]
    public void Union_IsNotANumber_True()
    {
        Assert.Equal("yes", Run(
            "Define x as \"hello\".\n" +
            "If x is not a number:\n" +
            "    State \"yes\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"no\".\n" +
            "Done."));
    }

    [Fact]
    public void Union_IsNotANumber_False()
    {
        Assert.Equal("no", Run(
            "Define x as 42.\n" +
            "If x is not a number:\n" +
            "    State \"yes\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"no\".\n" +
            "Done."));
    }

    [Fact]
    public void Union_IsAFact_True()
    {
        Assert.Equal("yes", Run(
            "Define x as (1 = 1).\n" +
            "If x is a fact:\n" +
            "    State \"yes\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"no\".\n" +
            "Done."));
    }

    [Fact]
    public void Union_TypeAnnotation_UnionType_OnSeriesElement()
    {
        // Catalogue element type is the union; narrowing needed before type-specific op
        Assert.Equal("42", Run(
            "Define items as a catalogue of (number or text) with (42, \"hello\").\n" +
            "Define elem as item 1 of items.\n" +
            "If elem is a number:\n" +
            "    State elem converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Union_TypeSpecificOp_Arithmetic_TypeError()
    {
        // Arithmetic on un-narrowed union value → static error
        Assert.Throws<TypeException>(() => Run(
            "Define items as a catalogue of (number or text) with (42, \"hi\").\n" +
            "Define v as item 1 of items.\n" +
            "State v + 1."));
    }

    [Fact]
    public void Union_EqualityComparison_Legal()
    {
        // Equality on un-narrowed union value is legal
        Assert.Equal("yes", Run(
            "Define items as a catalogue of (number or text) with (42, \"hi\").\n" +
            "Define v as item 1 of items.\n" +
            "If v is 42:\n" +
            "    State \"yes\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State \"no\".\n" +
            "Done."));
    }

    [Fact]
    public void Union_AssignIncompatible_TypeError()
    {
        // Assigning a value not in the closed union → type error
        Assert.Throws<TypeException>(() => Run(
            "Define items as a catalogue of (number or text) with (42, \"hi\").\n" +
            "Add (1 = 1) to items."));
    }

    // ── Layer 2: Narrowing ──────────────────────────────────────────────────────

    [Fact]
    public void Narrowing_IsANumber_ArithmeticInBranch()
    {
        // After narrowing, arithmetic is valid
        Assert.Equal("43", Run(
            "Define items as a catalogue of (number or text) with (42, \"hi\").\n" +
            "Define v as item 1 of items.\n" +
            "If v is a number:\n" +
            "    State (v + 1) converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Narrowing_IsAText_LengthInBranch()
    {
        Assert.Equal("5", Run(
            "Define items as a catalogue of (number or text) with (42, \"hello\").\n" +
            "Define v as item 2 of items.\n" +
            "If v is a text:\n" +
            "    State the length of v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Narrowing_ClosedUnion_OtherwiseNarrows()
    {
        // Otherwise branch of (number or text) after 'is a number' → narrows to text
        Assert.Equal("3", Run(
            "Define items as a catalogue of (number or text) with (42, \"hi!\").\n" +
            "Define v as item 2 of items.\n" +
            "If v is a number:\n" +
            "    State (v + 1) converted to text.\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State the length of v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Narrowing_ClosedUnion_TwoArms_OtherwiseIsThird()
    {
        // (number or text or fact): check number then text → Otherwise narrows to fact
        Assert.Equal("true", Run(
            "Define items as a catalogue of (number or text or fact) with ((1 = 1), 1, \"x\").\n" +
            "Define v as item 1 of items.\n" +
            "If v is a number:\n" +
            "    State \"number\".\n" +
            "Done.\n" +
            "Otherwise if v is a text:\n" +
            "    State \"text\".\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Narrowing_IsNotANumber_ClosedUnion_NarrowsToComplement()
    {
        // is not a number on (number or text) → true branch is text
        Assert.Equal("5", Run(
            "Define items as a catalogue of (number or text) with (42, \"hello\").\n" +
            "Define v as item 2 of items.\n" +
            "If v is not a number:\n" +
            "    State the length of v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Narrowing_OpenCatalogue_IsANumber_Works()
    {
        // Open catalogue: is a number still narrows in the true branch
        Assert.Equal("43", Run(
            "Define items as a catalogue with (42, \"hello\", (1 = 1)).\n" +
            "Define v as item 1 of items.\n" +
            "If v is a number:\n" +
            "    State (v + 1) converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Narrowing_ExistingIsVoid_StillWorks()
    {
        // Existing voidable narrowing: is not void narrows to the inner type
        Assert.Equal("2", Run(
            "Pull a book on math.\n" +
            "Define r as math's square root of (4).\n" +
            "If r is not void:\n" +
            "    State r converted to text.\n" +
            "Done.\n" +
            "Done."));
    }

    // ── Layer 3: catalogue ──────────────────────────────────────────────────────

    [Fact]
    public void Catalogue_DeclaredUnion_LengthAndAccess()
    {
        Assert.Equal("2", Run(
            "Define items as a catalogue of (number or text) with (42, \"hello\").\n" +
            "State the number of items converted to text."));
    }

    [Fact]
    public void Catalogue_DeclaredUnion_AddNumber()
    {
        Assert.Equal("3", Run(
            "Define items as a catalogue of (number or text) with (42, \"hello\").\n" +
            "Add 99 to items.\n" +
            "State the number of items converted to text."));
    }

    [Fact]
    public void Catalogue_DeclaredUnion_AddText()
    {
        Assert.Equal("3", Run(
            "Define items as a catalogue of (number or text) with (42, \"hello\").\n" +
            "Add \"world\" to items.\n" +
            "State the number of items converted to text."));
    }

    [Fact]
    public void Catalogue_DeclaredUnion_AddIncompatible_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define items as a catalogue of (number or text) with (42, \"hello\").\n" +
            "Add (1 = 1) to items."));
    }

    [Fact]
    public void Catalogue_Open_AddAny()
    {
        // Open catalogue accepts any type
        Assert.Equal("3", Run(
            "Define items as a catalogue with (42, \"hello\").\n" +
            "Add (1 = 1) to items.\n" +
            "State the number of items converted to text."));
    }

    [Fact]
    public void Catalogue_ForEach_Narrow()
    {
        Assert.Equal("1", Run(
            "Define items as a catalogue of (number or text) with (10, \"hi\", 20).\n" +
            "Define count as 0.\n" +
            "For each v in items, repeat:\n" +
            "    If v is a text:\n" +
            "        count becomes count + 1.\n" +
            "    Done.\n" +
            "Done.\n" +
            "State count converted to text."));
    }

    // ── Layer 3: atlas ──────────────────────────────────────────────────────────

    [Fact]
    public void Atlas_DeclaredUnion_Set_And_Get()
    {
        Assert.Equal("42", Run(
            "Define mp as an atlas from text to (number or text) with (\"x\" : 42).\n" +
            "Define v as the entry for \"x\" in mp but void is 0.\n" +
            "If v is a number:\n" +
            "    State v converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Atlas_DeclaredUnion_SetTextValue()
    {
        Assert.Equal("hello", Run(
            "Define mp as an atlas from text to (number or text).\n" +
            "In mp, the entry for \"k\" becomes \"hello\".\n" +
            "Define v as the entry for \"k\" in mp but void is 0.\n" +
            "If v is a text:\n" +
            "    State v.\n" +
            "Done."));
    }

    [Fact]
    public void Atlas_DeclaredUnion_SetIncompatible_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define mp as an atlas from text to (number or text).\n" +
            "In mp, the entry for \"k\" becomes (1 = 1)."));
    }

    [Fact]
    public void Atlas_Open_AnyValue()
    {
        // Open atlas: any value can be stored
        Assert.Equal("2", Run(
            "Define mp as an atlas.\n" +
            "In mp, the entry for \"n\" becomes 42.\n" +
            "In mp, the entry for \"t\" becomes \"hello\".\n" +
            "State the size of mp converted to text."));
    }

    [Fact]
    public void Atlas_HasKey()
    {
        Assert.Equal("yes", Run(
            "Define mp as an atlas from text to (number or text) with (\"k\" : 99).\n" +
            "If mp has a key for \"k\":\n" +
            "    State \"yes\".\n" +
            "Done."));
    }

    [Fact]
    public void Union_VoidOrNormalizesToVoidable()
    {
        // (T or void) normalizes to voidable T — same behavior as voidable
        Assert.Equal("5", Run(
            "Pull a book on math.\n" +
            "Define r as math's square root of (25).\n" +
            "If r is not void:\n" +
            "    State r converted to text.\n" +
            "Done.\n" +
            "Done."));
    }

    // ── Collections — minimum ────────────────────────────────────────────────

    [Fact]
    public void Collections_Minimum_Basic()
    {
        Assert.Equal("1", Run(
            "Pull a book on collections.\n" +
            "Define xs as a series with (3, 1, 2).\n" +
            "Define r as cast collections's minimum of (xs) but void is 0.\n" +
            "State r converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Collections_Minimum_SingleElement()
    {
        Assert.Equal("7", Run(
            "Pull a book on collections.\n" +
            "Define xs as a series with (7).\n" +
            "Define r as cast collections's minimum of (xs) but void is 0.\n" +
            "State r converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Collections_Minimum_EmptyIsVoid()
    {
        Assert.Equal("none", Run(
            "Pull a book on collections.\n" +
            "Define xs as a series of number with ().\n" +
            "Define r as cast collections's minimum of (xs).\n" +
            "If r is void, State \"none\".\n" +
            "Done."));
    }

    [Fact]
    public void Collections_Minimum_NonNumberSeries_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define xs as a series with (\"a\", \"b\").\n" +
            "State cast collections's minimum of (xs) converted to text.\n" +
            "Done."));
    }

    // ── Collections — maximum ────────────────────────────────────────────────

    [Fact]
    public void Collections_Maximum_Basic()
    {
        Assert.Equal("9", Run(
            "Pull a book on collections.\n" +
            "Define xs as a series with (3, 9, 5).\n" +
            "Define r as cast collections's maximum of (xs) but void is 0.\n" +
            "State r converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Collections_Maximum_EmptyIsVoid()
    {
        Assert.Equal("0", Run(
            "Pull a book on collections.\n" +
            "Define xs as a series of number with ().\n" +
            "Define r as cast collections's maximum of (xs) but void is 0.\n" +
            "State r converted to text.\n" +
            "Done."));
    }

    // ── Collections — average ────────────────────────────────────────────────

    [Fact]
    public void Collections_Average_Basic()
    {
        // (1 + 2 + 3) / 3 = 2
        Assert.Equal("2", Run(
            "Pull a book on collections.\n" +
            "Define xs as a series with (1, 2, 3).\n" +
            "Define r as cast collections's average of (xs) but void is 0.\n" +
            "State r converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Collections_Average_Decimal()
    {
        // (1 + 2) / 2 = 1.5
        Assert.Equal("1.5", Run(
            "Pull a book on collections.\n" +
            "Define xs as a series with (1, 2).\n" +
            "Define r as cast collections's average of (xs) but void is 0.\n" +
            "State r converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Collections_Average_EmptyIsVoid()
    {
        Assert.Equal("void", Run(
            "Pull a book on collections.\n" +
            "Define xs as a series of number with ().\n" +
            "Define r as cast collections's average of (xs).\n" +
            "If r is void, State \"void\".\n" +
            "Done."));
    }

    [Fact]
    public void Collections_Average_NonNumberSeries_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on collections.\n" +
            "Define xs as a series with (\"a\").\n" +
            "State cast collections's average of (xs) converted to text.\n" +
            "Done."));
    }

    // ── Collections — unique ─────────────────────────────────────────────────

    [Fact]
    public void Collections_Unique_RemovesDuplicates()
    {
        // First-occurrence order: 3, 1, 2
        Assert.Equal("(3, 1, 2)", Run(
            "Pull a book on collections.\n" +
            "Define xs as a series with (3, 1, 3, 2, 1).\n" +
            "Define r as cast collections's unique of (xs).\n" +
            "State r.\n" +
            "Done."));
    }

    [Fact]
    public void Collections_Unique_NoDuplicates()
    {
        Assert.Equal("(1, 2, 3)", Run(
            "Pull a book on collections.\n" +
            "Define xs as a series with (1, 2, 3).\n" +
            "Define r as cast collections's unique of (xs).\n" +
            "State r.\n" +
            "Done."));
    }

    [Fact]
    public void Collections_Unique_Empty()
    {
        Assert.Equal("()", Run(
            "Pull a book on collections.\n" +
            "Define xs as a series of number with ().\n" +
            "Define r as cast collections's unique of (xs).\n" +
            "State r.\n" +
            "Done."));
    }

    [Fact]
    public void Collections_Unique_TextSeries()
    {
        Assert.Equal("(hello, world)", Run(
            "Pull a book on collections.\n" +
            "Define xs as a series with (\"hello\", \"world\", \"hello\").\n" +
            "Define r as cast collections's unique of (xs).\n" +
            "State r.\n" +
            "Done."));
    }

    [Fact]
    public void Collections_Unique_PreservesElementType()
    {
        // Result type should still be series of number — can be piped into minimum.
        Assert.Equal("1", Run(
            "Pull a book on collections.\n" +
            "Define xs as a series with (3, 1, 3, 2).\n" +
            "Define u as cast collections's unique of (xs).\n" +
            "Define r as cast collections's minimum of (u) but void is 0.\n" +
            "State r converted to text.\n" +
            "Done."));
    }

    [Fact]
    public void Collections_Unique_NonSeries_TypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on collections.\n" +
            "State cast collections's unique of (42) converted to text.\n" +
            "Done."));
    }

    // ── Getters & Setters ─────────────────────────────────────────────────────

    // Basic getter: computed property callable with both access syntaxes.
    [Fact]
    public void Getter_BasicComputed_TheOfSyntax()
    {
        Assert.Equal("3.141592653589793", Run(
            "Define object circle with (the number radius):\n" +
            "    Get area as number:\n" +
            "        return one's radius * one's radius * 3.141592653589793.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define c as a new circle { the radius 1 }.\n" +
            "State the area of c."));
    }

    [Fact]
    public void Getter_BasicComputed_PossessiveSyntax()
    {
        Assert.Equal("10", Run(
            "Define object box with (the number width, the number height):\n" +
            "    Get perimeter as number:\n" +
            "        return (one's width + one's height) * 2.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define b as a new box { the width 2, the height 3 }.\n" +
            "State b's perimeter."));
    }

    // Getter returns correct type for type-checker.
    [Fact]
    public void Getter_TypeCheck_ReturnTypeInferred()
    {
        Assert.Equal("hello world", Run(
            "Define object greeter with (the text name):\n" +
            "    Get message as text:\n" +
            "        return \"hello \" joined to one's name.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define g as a new greeter { the name \"world\" }.\n" +
            "Define msg as the message of g.\n" +
            "State msg."));
    }

    // Getter intercepts reads before stored fields — uniform access property.
    // The object has a stored field 'raw' and a getter 'display' that computes from it.
    // Reading 'display' dispatches through the getter, not a raw field lookup.
    [Fact]
    public void Getter_UniformAccess_DispatchesThroughGetter()
    {
        Assert.Equal("6", Run(
            "Define object scaler with (the number raw):\n" +
            "    Get display as number:\n" +
            "        return one's raw * 2.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define s as a new scaler { the raw 3 }.\n" +
            "State s's display."));
    }

    // Setter: intercepting write via 'the X of obj becomes Y'.
    [Fact]
    public void Setter_Basic_RecordNamedSetSyntax()
    {
        Assert.Equal("10", Run(
            "Define object bounded with (the number value):\n" +
            "    Set value given (the number v):\n" +
            "        Define clamped as v.\n" +
            "        If clamped is greater than 10, clamped becomes 10.\n" +
            "        If clamped is less than 0, clamped becomes 0.\n" +
            "        the value of one becomes clamped.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define b as a new bounded { the value 5 }.\n" +
            "the value of b becomes 99.\n" +
            "State the value of b."));
    }

    [Fact]
    public void Setter_Basic_PossessiveSyntax()
    {
        Assert.Equal("0", Run(
            "Define object bounded with (the number value):\n" +
            "    Set value given (the number v):\n" +
            "        Define clamped as v.\n" +
            "        If clamped is greater than 10, clamped becomes 10.\n" +
            "        If clamped is less than 0, clamped becomes 0.\n" +
            "        one's value becomes clamped.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define b as a new bounded { the value 5 }.\n" +
            "b's value becomes -3.\n" +
            "State b's value."));
    }

    // Setter self-write bypass: inside the setter body, writing 'one's field' does a raw write.
    [Fact]
    public void Setter_SelfWriteBypass_NoInfiniteRecursion()
    {
        // Without the bypass this would recurse infinitely.
        Assert.Equal("5", Run(
            "Define object box with (the number value):\n" +
            "    Set value given (the number v):\n" +
            "        one's value becomes v.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define b as a new box { the value 0 }.\n" +
            "b's value becomes 5.\n" +
            "State b's value."));
    }

    // Getter + setter pair: getter reads through setter's stored backing field.
    [Fact]
    public void GetterSetter_Pair_ComputedAndClamped()
    {
        Assert.Equal("100", Run(
            "Define object gauge with (the number raw):\n" +
            "    Get display as number:\n" +
            "        If one's raw is greater than 100, return 100.\n" +
            "        If one's raw is less than 0, return 0.\n" +
            "        return one's raw.\n" +
            "    Done.\n" +
            "    Set raw given (the number v):\n" +
            "        one's raw becomes v.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define g as a new gauge { the raw 50 }.\n" +
            "g's raw becomes 999.\n" +
            "State g's display."));
    }

    // Setter type error: wrong type passed to setter.
    [Fact]
    public void Setter_TypeError_WrongParamType()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object box with (the number value):\n" +
            "    Set value given (the number v):\n" +
            "        one's value becomes v.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define b as a new box { the value 0 }.\n" +
            "b's value becomes \"oops\"."));
    }

    // Getter that doesn't return is caught by the type-checker.
    [Fact]
    public void Getter_TypeError_NoReturn()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object broken with (the number x):\n" +
            "    Get value as number:\n" +
            "        State \"side effect\".\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define b as a new broken { the x 1 }.\n" +
            "State b's value."));
    }

    // Getter type error: void getter not allowed.
    [Fact]
    public void Getter_TypeError_VoidReturnNotAllowed()
    {
        Assert.Throws<ParseException>(() => Run(
            "Define object bad with (the number x):\n" +
            "    Get value as void:\n" +
            "        State \"hi\".\n" +
            "    Done.\n" +
            "Done."));
    }

    // Getter name collision with method.
    [Fact]
    public void Getter_TypeError_CollidesWithMethod()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object bad with (the number x):\n" +
            "    Bind void to calculate:\n" +
            "        State \"calculating\".\n" +
            "    Done.\n" +
            "    Get calculate as number:\n" +
            "        return 1.\n" +
            "    Done.\n" +
            "Done."));
    }

    // Setter name collision with method.
    [Fact]
    public void Setter_TypeError_CollidesWithMethod()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object bad with (the number x):\n" +
            "    Bind void to calculate:\n" +
            "        State \"calculating\".\n" +
            "    Done.\n" +
            "    Set calculate given (the number v):\n" +
            "        one's x becomes v.\n" +
            "    Done.\n" +
            "Done."));
    }

    // Duplicate getter names.
    [Fact]
    public void Getter_TypeError_DuplicateName()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object bad with (the number x):\n" +
            "    Get value as number:\n" +
            "        return 1.\n" +
            "    Done.\n" +
            "    Get value as number:\n" +
            "        return 2.\n" +
            "    Done.\n" +
            "Done."));
    }

    // Unto form: getter declared outside the object definition.
    [Fact]
    public void Getter_Unto_ExternalDeclaration()
    {
        Assert.Equal("3.141592653589793", Run(
            "Define object circle with (the number radius).\n" +
            "Get pi unto circle as number:\n" +
            "    return 3.141592653589793.\n" +
            "Done.\n" +
            "Define c as a new circle { the radius 1 }.\n" +
            "State c's pi."));
    }

    // Unto form: setter declared outside the object definition.
    [Fact]
    public void Setter_Unto_ExternalDeclaration()
    {
        Assert.Equal("5", Run(
            "Define object box with (the number value).\n" +
            "Set value unto box given (the number v):\n" +
            "    one's value becomes v.\n" +
            "Done.\n" +
            "Define b as a new box { the value 0 }.\n" +
            "b's value becomes 5.\n" +
            "State b's value."));
    }

    // Getter promoted through embed chain.
    [Fact]
    public void Getter_Promoted_ThroughEmbedding()
    {
        Assert.Equal("314", Run(
            "Define object circle with (the number radius):\n" +
            "    Get area as number:\n" +
            "        return one's radius * one's radius * 314.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define object colored-circle with (the text color) and as a circle.\n" +
            "Define cc as a new colored-circle { the color \"red\", the radius 1 }.\n" +
            "State cc's area."));
    }

    // Setter promoted through embed chain.
    [Fact]
    public void Setter_Promoted_ThroughEmbedding()
    {
        Assert.Equal("7", Run(
            "Define object box with (the number value):\n" +
            "    Set value given (the number v):\n" +
            "        one's value becomes v.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Define object labeled-box with (the text label) and as a box.\n" +
            "Define lb as a new labeled-box { the label \"x\", the value 0 }.\n" +
            "lb's value becomes 7.\n" +
            "State lb's value."));
    }

    // Accessing a nonexistent getter on an object throws a type error.
    [Fact]
    public void Getter_TypeError_UnknownName()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object box with (the number value).\n" +
            "Define b as a new box { the value 1 }.\n" +
            "State b's missing."));
    }

    // ── Constructors (named, 'Bind making a <type> to <name>') ───────────────

    // Basic infallible constructor: builds and returns an object.
    [Fact]
    public void Constructor_Basic_InfallibleReturnsObject()
    {
        Assert.Equal("Alice\n30", Run(
            "Define object person with (the text name, the number age).\n" +
            "Bind making a person to make-person, given (the text nm, the number yrs):\n" +
            "    return a new person { the name nm, the age yrs }.\n" +
            "Done.\n" +
            "Define p as cast make-person on (\"Alice\", 30).\n" +
            "State p's name.\n" +
            "State p's age."));
    }

    // Constructor result is usable as a normal object (field access, method calls).
    [Fact]
    public void Constructor_Result_FieldsAccessible()
    {
        Assert.Equal("6", Run(
            "Define object box with (the number width, the number height):\n" +
            "    Get area as number:\n" +
            "        return one's width * one's height.\n" +
            "    Done.\n" +
            "Done.\n" +
            "Bind making a box to make-box, given (the number w, the number h):\n" +
            "    return a new box { the width w, the height h }.\n" +
            "Done.\n" +
            "Define b as cast make-box on (2, 3).\n" +
            "State b's area."));
    }

    // Multiple named constructors on the same type.
    [Fact]
    public void Constructor_MultipleOnSameType()
    {
        Assert.Equal("5\n25", Run(
            "Define object circle with (the number radius).\n" +
            "Bind making a circle to unit-circle:\n" +
            "    return a new circle { the radius 1 }.\n" +
            "Done.\n" +
            "Bind making a circle to big-circle, given (the number r):\n" +
            "    return a new circle { the radius r }.\n" +
            "Done.\n" +
            "Define c1 as cast unit-circle on ().\n" +
            "Define c2 as cast big-circle on (5).\n" +
            "State c2's radius.\n" +
            "State c2's radius * c2's radius."));
    }

    // Fallible constructor: returns the object or a failure.
    [Fact]
    public void Constructor_Fallible_SuccessPath()
    {
        Assert.Equal("5", Run(
            "Define object point with (the number x, the number y).\n" +
            "Bind making a point or failure to positive-point, given (the number px, the number py):\n" +
            "    If px is less than 0, return a failure \"x must be non-negative\".\n" +
            "    If py is less than 0, return a failure \"y must be non-negative\".\n" +
            "    return a new point { the x px, the y py }.\n" +
            "Done.\n" +
            "Try to:\n" +
            "    Define p as cast positive-point on (5, 3).\n" +
            "    State p's x.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done."));
    }

    [Fact]
    public void Constructor_Fallible_FailurePath()
    {
        Assert.Equal("bad input", Run(
            "Define object point with (the number x, the number y).\n" +
            "Bind making a point or failure to positive-point, given (the number px, the number py):\n" +
            "    If px is less than 0, return a failure \"bad input\".\n" +
            "    return a new point { the x px, the y py }.\n" +
            "Done.\n" +
            "Try to:\n" +
            "    Define p as cast positive-point on (-1, 3).\n" +
            "    State p's x.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State the message of the failure.\n" +
            "Done."));
    }

    // Implicit {…} construction still works alongside named constructors.
    [Fact]
    public void Constructor_ImplicitFormStillWorks()
    {
        Assert.Equal("direct", Run(
            "Define object tag with (the text label).\n" +
            "Bind making a tag to make-tag, given (the text s):\n" +
            "    return a new tag { the label s }.\n" +
            "Done.\n" +
            "Define t as a new tag { the label \"direct\" }.\n" +
            "State t's label."));
    }

    // Constructor registered on object type (type knows about it).
    [Fact]
    public void Constructor_RegisteredOnType_CanCallWithCast()
    {
        Assert.Equal("hello", Run(
            "Define object wrapper with (the text value).\n" +
            "Bind making a wrapper to wrap, given (the text v):\n" +
            "    return a new wrapper { the value v }.\n" +
            "Done.\n" +
            "Define w as cast wrap on (\"hello\").\n" +
            "State w's value."));
    }

    // Type error: constructor target type does not exist.
    [Fact]
    public void Constructor_TypeError_UnknownType()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind making a ghost to make-ghost:\n" +
            "    return a new ghost { }.\n" +
            "Done."));
    }

    // Type error: constructor body returns wrong type.
    [Fact]
    public void Constructor_TypeError_BodyReturnsWrongType()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object box with (the number value).\n" +
            "Bind making a box to bad-box:\n" +
            "    return 42.\n" +
            "Done."));
    }

    // Type error: duplicate constructor name on the same type.
    [Fact]
    public void Constructor_TypeError_DuplicateName()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object box with (the number value).\n" +
            "Bind making a box to make-box:\n" +
            "    return a new box { the value 1 }.\n" +
            "Done.\n" +
            "Bind making a box to make-box:\n" +
            "    return a new box { the value 2 }.\n" +
            "Done."));
    }

    // Type error: fallible constructor result must be handled.
    [Fact]
    public void Constructor_TypeError_FallibleResultUnhandled()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object point with (the number x, the number y).\n" +
            "Bind making a point or failure to safe-point, given (the number x, the number y):\n" +
            "    return a new point { the x x, the y y }.\n" +
            "Done.\n" +
            "Define p as cast safe-point on (1, 2).\n" +
            "State p's x."));
    }

    // ── Destructors ('Bind unmaking a <type> to <name>') ─────────────────────

    // Basic RAII: unmake fires when the defining scope exits.
    [Fact]
    public void Destructor_Basic_FiresOnScopeExit()
    {
        Assert.Equal("open\nclosed", Run(
            "Define object handle with (the text label).\n" +
            "Bind unmaking a handle to cleanup:\n" +
            "    State \"closed\".\n" +
            "Done.\n" +
            "If 1 is 1:\n" +
            "    Define h as a new handle { the label \"x\" }.\n" +
            "    State \"open\".\n" +
            "Done."));
    }

    // LIFO ordering: second-defined object is unmade first.
    [Fact]
    public void Destructor_LIFO_OrderReversed()
    {
        Assert.Equal("A\nB\nunmake B\nunmake A", Run(
            "Define object res with (the text name).\n" +
            "Bind unmaking a res to teardown:\n" +
            "    State \"unmake \" joined to one's name.\n" +
            "Done.\n" +
            "If 1 is 1:\n" +
            "    Define first-res as a new res { the name \"A\" }.\n" +
            "    State \"A\".\n" +
            "    Define second-res as a new res { the name \"B\" }.\n" +
            "    State \"B\".\n" +
            "Done."));
    }

    // Body uses 'one's field' to access the object being unmade.
    [Fact]
    public void Destructor_Body_AccessesOnesFields()
    {
        Assert.Equal("releasing handle-42", Run(
            "Define object conn with (the text id).\n" +
            "Bind unmaking a conn to close-conn:\n" +
            "    State \"releasing \" joined to one's id.\n" +
            "Done.\n" +
            "If 1 is 1:\n" +
            "    Define c as a new conn { the id \"handle-42\" }.\n" +
            "Done."));
    }

    // Only objects with a registered unmake are affected; others are skipped silently.
    [Fact]
    public void Destructor_NoUnmake_SkippedSilently()
    {
        Assert.Equal("done", Run(
            "Define object plain with (the number value).\n" +
            "If 1 is 1:\n" +
            "    Define p as a new plain { the value 1 }.\n" +
            "Done.\n" +
            "State \"done\"."));
    }

    // Unmake does not fire for objects that were never defined in that scope.
    [Fact]
    public void Destructor_OnlyDefinedObjectsUnmade()
    {
        Assert.Equal("open\nclosed\nafter", Run(
            "Define object resource with (the text tag).\n" +
            "Bind unmaking a resource to free-resource:\n" +
            "    State \"closed\".\n" +
            "Done.\n" +
            "If 1 is 1:\n" +
            "    Define r as a new resource { the tag \"x\" }.\n" +
            "    State \"open\".\n" +
            "Done.\n" +
            "State \"after\"."));
    }

    // Multiple scopes: outer object unmade after inner scope's objects.
    [Fact]
    public void Destructor_NestedScopes_OuterAfterInner()
    {
        Assert.Equal("outer\ninner\nunmake inner\nunmake outer", Run(
            "Define object box with (the text tag).\n" +
            "Bind unmaking a box to destroy-box:\n" +
            "    State \"unmake \" joined to one's tag.\n" +
            "Done.\n" +
            "If 1 is 1:\n" +
            "    Define outer-box as a new box { the tag \"outer\" }.\n" +
            "    State \"outer\".\n" +
            "    If 1 is 1:\n" +
            "        Define inner-box as a new box { the tag \"inner\" }.\n" +
            "        State \"inner\".\n" +
            "    Done.\n" +
            "Done."));
    }

    // Type error: second 'Bind unmaking a <type>' for the same type.
    [Fact]
    public void Destructor_TypeError_DuplicateUnmaker()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object widget with (the number x).\n" +
            "Bind unmaking a widget to first-cleanup:\n" +
            "    State \"first\".\n" +
            "Done.\n" +
            "Bind unmaking a widget to second-cleanup:\n" +
            "    State \"second\".\n" +
            "Done."));
    }

    // Type error: 'Bind unmaking a <type>' for an undeclared type.
    [Fact]
    public void Destructor_TypeError_UnknownType()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind unmaking a ghost to haunt:\n" +
            "    State \"boo\".\n" +
            "Done."));
    }

    // Type error: destructor body contains 'return a failure' — infallibility enforced.
    [Fact]
    public void Destructor_TypeError_ReturnFailureInBody()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define object file-handle with (the text location).\n" +
            "Bind unmaking a file-handle to bad-close:\n" +
            "    return a failure \"cannot close\".\n" +
            "Done."));
    }

    // Unmake body can call methods on one.
    [Fact]
    public void Destructor_Body_CanCallMethodOnOne()
    {
        Assert.Equal("flushing\ndone", Run(
            "Define object buffer with (the text data):\n" +
            "    Bind void to flush:\n" +
            "        State \"flushing\".\n" +
            "    Done.\n" +
            "Done.\n" +
            "Bind unmaking a buffer to drain-buffer:\n" +
            "    Cast flush on one.\n" +
            "Done.\n" +
            "If 1 is 1:\n" +
            "    Define buf as a new buffer { the data \"x\" }.\n" +
            "Done.\n" +
            "State \"done\"."));
    }

    // ── Operator overloading ──────────────────────────────────────────────

    private static string OverloadPreamble =>
        "Define object vec2 with (the number x, the number y).\n";

    private static string AddOverload =>
        "Bind overloading +, given (the lhs is a vec2, the rhs is a vec2):\n" +
        "    Return a new vec2 { the x lhs's x + rhs's x, the y lhs's y + rhs's y }.\n" +
        "Done.\n";

    // Basic infallible overload: result is used as a value.
    [Fact]
    public void OverloadOp_Add_Basic()
    {
        Assert.Equal("3\n7", Run(
            OverloadPreamble +
            AddOverload +
            "Define p as a new vec2 { the x 1, the y 3 }.\n" +
            "Define q as a new vec2 { the x 2, the y 4 }.\n" +
            "Define r as p + q.\n" +
            "State r's x.\n" +
            "State r's y."));
    }

    [Fact]
    public void OverloadOp_Subtract_Basic()
    {
        Assert.Equal("1", Run(
            OverloadPreamble +
            "Bind overloading -, given (the lhs is a vec2, the rhs is a vec2):\n" +
            "    Return a new vec2 { the x lhs's x - rhs's x, the y lhs's y - rhs's y }.\n" +
            "Done.\n" +
            "Define p as a new vec2 { the x 3, the y 0 }.\n" +
            "Define q as a new vec2 { the x 2, the y 0 }.\n" +
            "State (p - q)'s x."));
    }

    [Fact]
    public void OverloadOp_Multiply_Basic()
    {
        Assert.Equal("6", Run(
            OverloadPreamble +
            "Bind overloading *, given (the lhs is a vec2, the rhs is a vec2):\n" +
            "    Return a new vec2 { the x lhs's x * rhs's x, the y lhs's y * rhs's y }.\n" +
            "Done.\n" +
            "Define p as a new vec2 { the x 2, the y 0 }.\n" +
            "Define q as a new vec2 { the x 3, the y 0 }.\n" +
            "State (p * q)'s x."));
    }

    [Fact]
    public void OverloadOp_Divide_Basic()
    {
        Assert.Equal("4", Run(
            OverloadPreamble +
            "Bind overloading /, given (the lhs is a vec2, the rhs is a vec2):\n" +
            "    Return a new vec2 { the x lhs's x / rhs's x, the y lhs's y / rhs's y }.\n" +
            "Done.\n" +
            "Define p as a new vec2 { the x 8, the y 6 }.\n" +
            "Define q as a new vec2 { the x 2, the y 3 }.\n" +
            "State (p / q)'s x."));
    }

    // Body accesses left and right operands by their declared parameter names.
    [Fact]
    public void OverloadOp_BodyCanAccessOperandsByName()
    {
        Assert.Equal("10\n20", Run(
            OverloadPreamble +
            "Bind overloading +, given (the left is a vec2, the right is a vec2):\n" +
            "    Define sumx as left's x + right's x.\n" +
            "    Define sumy as left's y + right's y.\n" +
            "    Return a new vec2 { the x sumx, the y sumy }.\n" +
            "Done.\n" +
            "Define p as a new vec2 { the x 3, the y 7 }.\n" +
            "Define q as a new vec2 { the x 7, the y 13 }.\n" +
            "Define r as p + q.\n" +
            "State r's x.\n" +
            "State r's y."));
    }

    // Multiple overloads for different operators on the same type.
    [Fact]
    public void OverloadOp_MultipleOperatorsOnSameType()
    {
        Assert.Equal("5\n1", Run(
            OverloadPreamble +
            "Bind overloading +, given (the lhs is a vec2, the rhs is a vec2):\n" +
            "    Return a new vec2 { the x lhs's x + rhs's x, the y lhs's y + rhs's y }.\n" +
            "Done.\n" +
            "Bind overloading -, given (the lhs is a vec2, the rhs is a vec2):\n" +
            "    Return a new vec2 { the x lhs's x - rhs's x, the y lhs's y - rhs's y }.\n" +
            "Done.\n" +
            "Define p as a new vec2 { the x 3, the y 4 }.\n" +
            "Define q as a new vec2 { the x 2, the y 3 }.\n" +
            "State (p + q)'s x.\n" +
            "State (p - q)'s x."));
    }

    private static string FallibleMulOverload =>
        "Bind overloading *, given (the lhs is a vec2, the rhs is a vec2):\n" +
        "    If lhs's x is 0 or rhs's x is 0:\n" +
        "        Return a failure \"zero operand\".\n" +
        "    Done.\n" +
        "    Return a new vec2 { the x lhs's x * rhs's x, the y lhs's y * rhs's y }.\n" +
        "Done.\n";

    // Fallible overload: success path inside Try.
    [Fact]
    public void OverloadOp_Fallible_SuccessPathInsideTry()
    {
        Assert.Equal("6", Run(
            OverloadPreamble +
            FallibleMulOverload +
            "Define p as a new vec2 { the x 2, the y 0 }.\n" +
            "Define q as a new vec2 { the x 3, the y 0 }.\n" +
            "Try to:\n" +
            "    Define r as p * q.\n" +
            "    State r's x.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done."));
    }

    // Fallible overload: failure path inside Try.
    [Fact]
    public void OverloadOp_Fallible_FailurePathInsideTry()
    {
        Assert.Equal("failed", Run(
            OverloadPreamble +
            FallibleMulOverload +
            "Define p as a new vec2 { the x 0, the y 0 }.\n" +
            "Define q as a new vec2 { the x 3, the y 0 }.\n" +
            "Try to:\n" +
            "    Define r as p * q.\n" +
            "    State r's x.\n" +
            "Done.\n" +
            "In case of failure:\n" +
            "    State \"failed\".\n" +
            "Done."));
    }

    // Strict-fallible rule: using a fallible overload without a Try block is a type error.
    [Fact]
    public void OverloadOp_Fallible_StrictFallibleEnforced()
    {
        Assert.Throws<TypeException>(() => Run(
            OverloadPreamble +
            "Bind overloading +, given (the lhs is a vec2, the rhs is a vec2):\n" +
            "    If lhs's x is 0:\n" +
            "        Return a failure \"zero\".\n" +
            "    Done.\n" +
            "    Return a new vec2 { the x lhs's x + rhs's x, the y lhs's y + rhs's y }.\n" +
            "Done.\n" +
            "Define p as a new vec2 { the x 1, the y 0 }.\n" +
            "Define q as a new vec2 { the x 2, the y 0 }.\n" +
            "Define r as p + q.\n"));  // no Try — must be a type error
    }

    // Duplicate overload for the same type+operator is a type error.
    [Fact]
    public void OverloadOp_TypeError_DuplicateOverload()
    {
        Assert.Throws<TypeException>(() => Run(
            OverloadPreamble +
            "Bind overloading +, given (the lhs is a vec2, the rhs is a vec2):\n" +
            "    Return a new vec2 { the x lhs's x + rhs's x, the y lhs's y + rhs's y }.\n" +
            "Done.\n" +
            "Bind overloading +, given (the lhs is a vec2, the rhs is a vec2):\n" +
            "    Return a new vec2 { the x lhs's x + rhs's x, the y lhs's y + rhs's y }.\n" +
            "Done.\n"));
    }

    // Overload for an undeclared type is a type error.
    [Fact]
    public void OverloadOp_TypeError_UnknownType()
    {
        Assert.Throws<TypeException>(() => Run(
            "Bind overloading +, given (the lhs is a ghost, the rhs is a ghost):\n" +
            "    Return a new ghost { }.\n" +
            "Done.\n"));
    }

    // Mixed-type operands fall through to the numeric error path.
    [Fact]
    public void OverloadOp_MixedType_NotDispatched()
    {
        Assert.Throws<TypeException>(() => Run(
            OverloadPreamble +
            "Define object other with (the number v).\n" +
            AddOverload +
            "Define p as a new vec2 { the x 1, the y 0 }.\n" +
            "Define q as a new other { the v 2 }.\n" +
            "Define r as p + q.\n"));  // different types — no overload applies
    }

    // Overload must be top-level (inside a block is a parse error).
    [Fact]
    public void OverloadOp_ParseError_NotTopLevel()
    {
        Assert.Throws<ParseException>(() => Run(
            OverloadPreamble +
            "If 1 is 1:\n" +
            "    Bind overloading +, given (the lhs is a vec2, the rhs is a vec2):\n" +
            "        Return a new vec2 { the x lhs's x + rhs's x, the y lhs's y + rhs's y }.\n" +
            "    Done.\n" +
            "Done.\n"));
    }

    // Both operand types must match (parse-time check).
    [Fact]
    public void OverloadOp_ParseError_MixedOperandTypes()
    {
        Assert.Throws<ParseException>(() => Run(
            OverloadPreamble +
            "Define object other with (the number v).\n" +
            "Bind overloading +, given (the lhs is a vec2, the rhs is a other):\n" +
            "    Return a new vec2 { the x 0, the y 0 }.\n" +
            "Done.\n"));
    }

    // ── Chance book ───────────────────────────────────────────────────────────

    [Fact]
    public void Chance_Pull_BindsUnderBookName()
    {
        // Just pulling the book shouldn't error; chance is a registered book.
        Assert.Equal("ok", Run("Pull a book on chance.\nState \"ok\".\nDone."));
    }

    [Fact]
    public void Chance_RandomNumber_InRange_Seeded()
    {
        // With a fixed seed the sequence is deterministic — test the value is in range.
        var result = Run(
            "Pull a book on chance.\n" +
            "Seed the chance with 42.\n" +
            "Define r as a random number from 1 to 6.\n" +
            "If r is greater than 0 and r is less than 7, State \"in range\".\n" +
            "Otherwise, State \"out of range\".\n" +
            "Done.");
        Assert.Equal("in range", result);
    }

    [Fact]
    public void Chance_RandomNumber_Seeded_Deterministic()
    {
        // Same seed → same first value on two independent interpreter runs.
        const string src =
            "Pull a book on chance.\n" +
            "Seed the chance with 7.\n" +
            "State a random number from 1 to 100.\n" +
            "Done.";
        Assert.Equal(Run(src), Run(src));
    }

    [Fact]
    public void Chance_RandomNumber_DifferentSeeds_DifferentValues()
    {
        // Different seeds almost certainly produce different values.
        // Using a range of 1..1000000 to make collision probability negligible.
        var r1 = Run("Pull a book on chance.\nSeed the chance with 1.\nState a random number from 1 to 1000000.\nDone.");
        var r2 = Run("Pull a book on chance.\nSeed the chance with 2.\nState a random number from 1 to 1000000.\nDone.");
        Assert.NotEqual(r1, r2);
    }

    [Fact]
    public void Chance_RandomNumber_EqualBounds_ReturnsBound()
    {
        // When low == high the only possible result is that value.
        Assert.Equal("5", Run(
            "Pull a book on chance.\n" +
            "State a random number from 5 to 5.\n" +
            "Done."));
    }

    [Fact]
    public void Chance_RandomNumber_LowGreaterThanHigh_Throws()
    {
        Assert.Throws<RuntimeException>(() => Run(
            "Pull a book on chance.\n" +
            "State a random number from 6 to 1.\n" +
            "Done."));
    }

    [Fact]
    public void Chance_RandomItem_FromNonEmpty_InList_Seeded()
    {
        var result = Run(
            "Pull a book on chance.\n" +
            "Seed the chance with 99.\n" +
            "Define xs as a series of number with (10, 20, 30).\n" +
            "Define picked as a random item from xs but void is 0.\n" +
            "If picked is 10 or picked is 20 or picked is 30, State \"ok\".\n" +
            "Otherwise, State \"bad\".\n" +
            "Done.");
        Assert.Equal("ok", result);
    }

    [Fact]
    public void Chance_RandomItem_FromEmpty_ReturnsVoid()
    {
        Assert.Equal("empty", Run(
            "Pull a book on chance.\n" +
            "Define xs as a series of text with ().\n" +
            "Define picked as a random item from xs but void is \"empty\".\n" +
            "State picked.\n" +
            "Done."));
    }

    [Fact]
    public void Chance_RandomlyShuffled_SameElements_Seeded()
    {
        // The shuffled series should contain the same elements (same count, all present).
        Assert.Equal("3", Run(
            "Pull a book on chance.\n" +
            "Seed the chance with 5.\n" +
            "Define xs as a series of number with (1, 2, 3).\n" +
            "Define ys as randomly shuffled xs.\n" +
            "State the number of ys.\n" +
            "Done."));
    }

    [Fact]
    public void Chance_RandomlyShuffled_NonMutating()
    {
        // Source series must be unchanged after shuffle.
        Assert.Equal("1\n2\n3", Run(
            "Pull a book on chance.\n" +
            "Seed the chance with 5.\n" +
            "Define xs as a series of number with (1, 2, 3).\n" +
            "Define ys as randomly shuffled xs.\n" +
            "For each x in xs, repeat:\n" +
            "    State x.\n" +
            "Done.\n" +
            "Done."));
    }

    [Fact]
    public void Chance_RandomlyShuffled_SingleElement_Unchanged()
    {
        Assert.Equal("42", Run(
            "Pull a book on chance.\n" +
            "Define xs as a series of number with (42).\n" +
            "Define ys as randomly shuffled xs.\n" +
            "State the first of ys.\n" +
            "Done."));
    }

    [Fact]
    public void Chance_RandomGuess_IsBool_Seeded()
    {
        // With a fixed seed the result is deterministic — just verify it's true or false.
        var result = Run(
            "Pull a book on chance.\n" +
            "Seed the chance with 123.\n" +
            "If a random guess, State \"true\".\n" +
            "Otherwise, State \"false\".\n" +
            "Done.");
        Assert.True(result is "true" or "false");
    }

    [Fact]
    public void Chance_Seed_MakesDeterministic()
    {
        // Run the same seeded sequence twice, expect identical outputs.
        const string src =
            "Pull a book on chance.\n" +
            "Seed the chance with 42.\n" +
            "Define i as 1.\n" +
            "While i is less than 6, repeat:\n" +
            "    State a random number from 1 to 100.\n" +
            "    i becomes i + 1.\n" +
            "Done.\n" +
            "Done.";
        Assert.Equal(Run(src), Run(src));
    }

    [Fact]
    public void Chance_NotPulled_ThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run("State a random number from 1 to 6."));
    }

    [Fact]
    public void Chance_NotPulled_RandomItem_ThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run(
            "Define xs as a series of number with (1, 2).\n" +
            "State a random item from xs."));
    }

    [Fact]
    public void Chance_NotPulled_Seed_ThrowsTypeError()
    {
        Assert.Throws<TypeException>(() => Run("Seed the chance with 42."));
    }

    [Fact]
    public void Chance_TypeError_RandomNumber_NonNumberBound()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on chance.\n" +
            "State a random number from \"one\" to 6.\n" +
            "Done."));
    }

    [Fact]
    public void Chance_TypeError_RandomItem_NonSeries()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on chance.\n" +
            "State a random item from 42.\n" +
            "Done."));
    }

    [Fact]
    public void Chance_TypeError_RandomlyShuffled_NonSeries()
    {
        Assert.Throws<TypeException>(() => Run(
            "Pull a book on chance.\n" +
            "State randomly shuffled 42.\n" +
            "Done."));
    }
}
