using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
///
/// `<value> when <condition>, otherwise <value>` — a conditional EXPRESSION.
///
/// The feature exists because a value that depends on a condition previously had to be declared
/// and then mutated, which forces a mutable binding — so a `permanently` binding could not be
/// conditionally initialised at all. That is the hole, and PermanentlyBinding_CanBeConditional
/// below is the test that names it.
public class PipelineConditionalTests : PipelineTestBase
{
    [Fact]
    public void Conditional_BothBranches_MatchInterpreter()
    {
        const string src = """
            Define count as 1.
            Define many as 4.
            State "item" when count is 1, otherwise "items".
            State "item" when many is 1, otherwise "items".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void PermanentlyBinding_CanBeConditional()
    {
        // ★ The whole reason this feature outranked the formatter and expression-bodied members.
        // Before it, this had to be `Define fee as 25.` followed by `If member is true, the fee
        // becomes 0.` — and a `permanently` binding cannot be written that way at all.
        const string src = """
            Define member as true.
            Define fee as 0 when member is true, otherwise 25 permanently.
            State fee.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("0", Interpret(src));
    }

    [Fact]
    public void Conditional_OnlyTheTakenArmEvaluates()
    {
        // ★ The guarantee that makes the form safe with effects on either side. If both arms ran,
        // "SIDE EFFECT" would print — and the two backends would have to agree on printing it,
        // which is exactly the divergence the C ternary and the interpreter's single Evaluate
        // both avoid.
        const string src = """
            Bind number to noisy, given (the number amount):
                State "SIDE EFFECT".
                Return amount.
            Done.
            Define flag as true.
            Define picked as 1 when flag is true, otherwise cast noisy on (2).
            State picked.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.DoesNotContain("SIDE EFFECT", Interpret(src));
        Assert.DoesNotContain("SIDE EFFECT", Compile(src));
    }

    [Fact]
    public void Conditional_UntakenArmOnTheTrueSide_AlsoDoesNotEvaluate()
    {
        // The mirror image — the effect is in the FIRST arm and the condition is false. A ternary
        // gets this right by construction; a lowering that hoisted either arm would not.
        const string src = """
            Bind number to noisy, given (the number amount):
                State "SIDE EFFECT".
                Return amount.
            Done.
            Define flag as false.
            Define picked as cast noisy on (1) when flag is true, otherwise 2.
            State picked.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.DoesNotContain("SIDE EFFECT", Compile(src));
    }

    [Fact]
    public void Conditional_MixedArms_FormAUnion()
    {
        // Arms of different types union, matching `a catalogue with (1, "two")` — the language
        // already infers a union nobody declared, so refusing here would make the conditional
        // narrower than the collection literal beside it.
        const string src = """
            Define count as 1.
            State 1 when count is 1, otherwise "none".
            State 5 when count is 2, otherwise "none".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Conditional_Chained_IsRightAssociative()
    {
        // `a when p, otherwise b when q, otherwise c` is a fallback ladder, not a left nest.
        const string src = """
            Define count as 2.
            State "one" when count is 1, otherwise "two" when count is 2, otherwise "many".
            Define big as 9.
            State "one" when big is 1, otherwise "two" when big is 2, otherwise "many".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Conditional_InsideAnArgumentList_IsUnambiguous()
    {
        // ★ `when` REQUIRES `, otherwise`, so the comma can never be mistaken for a separator.
        // The series below is deterministically TWO elements: the conditional, then "fixed".
        // Legal, and left legal deliberately — reading better is a style question, not a grammar
        // one, so the language does not refuse it.
        const string src = """
            Define n as 1.
            Define sizes as a series of text with ("small" when n is 1, otherwise "big", "fixed").
            State the number of sizes.
            State the first of sizes.
            State the second of sizes.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("2\nsmall\nfixed", Interpret(src));
    }

    [Fact]
    public void Conditional_ComposesWithButVoidIs()
    {
        // `when` binds loosest, so this reads as `(raw but void is "unknown") when ...` — the
        // conditional chooses between two whole values, never reaching inside another suffix.
        const string src = """
            Define raw as "abc" converted to number.
            Define shout as true.
            Define parsed as raw but void is 0 when shout is true, otherwise 99.
            State parsed.
            Define quiet as false.
            Define fallback as raw but void is 0 when quiet is true, otherwise 99.
            State fallback.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Conditional_WithoutOtherwise_IsARefusal()
    {
        // The half-written form has no valid reading — it is not "two arguments", it is an
        // unfinished conditional, and saying so is what keeps the argument-list case unambiguous.
        const string src = """
            Define count as 1.
            State "item" when count is 1.
            """;
        Assert.ThrowsAny<Exception>(() => InterpretRaw(src));
    }

    [Fact]
    public void Conditional_NonFactCondition_IsARefusal()
    {
        const string src = """
            Define count as 1.
            State "item" when count, otherwise "items".
            """;
        Assert.ThrowsAny<Exception>(() => InterpretRaw(src));
    }
}
