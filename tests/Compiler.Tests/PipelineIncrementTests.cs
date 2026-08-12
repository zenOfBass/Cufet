using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
///
/// `Increment <target> by <amount>.` / `Decrement <target> by <amount>.`
///
/// ★ Pure sugar, desugared in the PARSER into the assignment it stands for, so there is no AST
/// node and the type checker, interpreter and compiler never learn the form exists. These tests
/// assert that equivalence directly: the sugared program and the spelled-out one must produce the
/// same output, and both backends must agree.
///
/// The verbs are `Increment`/`Decrement` rather than `Increase`/`Decrease` deliberately. Every
/// keyword is excluded from being an identifier, and `increase` is an everyday NOUN ("a price
/// increase") of exactly the kind that already costs users names — `key`, `size`, `sorted`,
/// `contains`. `increment` is a programming term, so reserving it takes far less away.
public class PipelineIncrementTests : PipelineTestBase
{
    [Fact]
    public void IncrementAndDecrement_MeanExactlyTheAssignmentTheyStandFor()
    {
        const string sugared = """
            Define i as 0.
            Increment i by 1.
            Increment i by 1.
            Decrement i by 5.
            State i.
            """;
        const string spelled = """
            Define i as 0.
            The i becomes i + 1.
            The i becomes i + 1.
            The i becomes i - 5.
            State i.
            """;
        Assert.Equal(Interpret(spelled), Interpret(sugared));
        Assert.Equal(InterpretRaw(sugared), CompileRaw(sugared));
        Assert.Equal("-3", Interpret(sugared));
    }

    [Fact]
    public void TheAmount_IsAnArbitraryExpression()
    {
        // Settled by the corpus rather than by taste: `The total becomes total + item at (rr, cc)
        // of board.` already existed, so restricting the amount to a literal would have left the
        // statement unable to express the thing it was measured on.
        const string src = """
            Define board as a series of number with (10, 20, 30).
            Define total as 0.
            Increment total by item 2 of board.
            Increment total by the number of board.
            Decrement total by 3 * 2.
            State total.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("17", Interpret(src));
    }

    [Fact]
    public void APossessiveTarget_WorksInsideAMethodAndOutsideIt()
    {
        // `one` is its own token rather than an Identifier, so the target parser has to accept it
        // the way the assignment parser already does — and `Increment one's tally by 1.` is the
        // first thing anyone writing a method reaches for.
        const string src = """
            Define object counter with (the number tally):
                Bind void to bump, Increment one's tally by 3.
            Done.
            Define c as a new counter { the tally 0 }.
            Cast c's bump.
            Cast c's bump.
            Increment c's tally by 10.
            Decrement c's tally by 1.
            State c's tally.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("15", Interpret(src));
    }

    [Fact]
    public void ItComposesWithLoopsAndInlineBodies()
    {
        const string src = """
            Define board as a series of number with (10, 20, 30).
            Define running as 0.
            For each x in board, Increment running by x.
            State running.

            Define countdown as 3.
            While countdown is greater than 0, Decrement countdown by 1.
            State countdown.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("60\n0", Interpret(src));
    }

    [Fact]
    public void ItIsNumericAndSigned()
    {
        // Fractions and negatives fall out of being plain arithmetic — there is no separate
        // "counter" concept, and no text concatenation or series growth hiding behind the verb.
        const string src = """
            Define f as 1.
            Decrement f by 2.5.
            State f.
            Increment f by 0.5.
            State f.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("-1.5\n-1", Interpret(src));
    }
}
