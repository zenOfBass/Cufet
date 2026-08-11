using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
///
/// A top-level function cannot see top-level data. The rule is old; where it is ENFORCED is new.
///
/// It used to live only in the interpreter, at run time, so one program got three different
/// answers: `cufet check` reported no problems, running it refused with a good message, and
/// compiling it emitted `cv_max_retries` undeclared and told the user "★ This is a bug in the
/// Cufet compiler, not in your program" — which sent them looking for a defect in Cufet when
/// their program was simply invalid and nothing had said so.
///
/// The rule now lives in the TypeChecker, which BOTH backends run before doing anything.
public class PipelineTopLevelDataTests : PipelineTestBase
{
    private const string ReadsTopLevelData = """
        Define max-retries as 3.
        Bind number to budget:
            Return max-retries * 2.
        Done.
        State cast budget.
        """;

    [Fact]
    public void TopLevelData_ReadInAFunction_IsRefusedByTheChecker()
    {
        var ex = Assert.ThrowsAny<Exception>(() => InterpretRaw(ReadsTopLevelData));
        Assert.Contains("top-level", ex.Message);
        Assert.Contains("max-retries", ex.Message);
    }

    [Fact]
    public void TopLevelData_ReadInAFunction_NeverReachesGcc()
    {
        // ★ The point of moving the rule. Before, the compiler got as far as handing invalid C to
        // gcc and then blamed itself in front of the user. Now it refuses in the same place and
        // with the same words as the interpreter, so the two backends agree about what is legal.
        var ex = Assert.ThrowsAny<Exception>(() => CompileRaw(ReadsTopLevelData));
        Assert.Contains("top-level", ex.Message);
        Assert.DoesNotContain("bug in the Cufet compiler", ex.Message);
        Assert.DoesNotContain("undeclared", ex.Message);
    }

    [Fact]
    public void APermanentlyBinding_IsASharedConstant()
    {
        // ★ The shared-constants feature, and the exact test that was written to fail when it
        // landed. The old rule hid ALL top-level data, justified as keeping data flow explicit and
        // preventing hidden mutation — but a `permanently` binding cannot be mutated, so the rule
        // was broader than its own reason. Only the immutable half comes back.
        const string src = """
            Define max-retries as 3 permanently.
            Bind number to budget:
                Return max-retries * 2.
            Done.
            State cast budget.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("6", Interpret(src));
    }

    [Fact]
    public void SharedConstants_ComposeWithEachOther()
    {
        // Source order, and an initialiser reading an earlier constant.
        const string src = """
            Define max-retries as 3 permanently.
            Define total-budget as max-retries * 10 permanently.
            Bind number to budget:
                Return total-budget.
            Done.
            State cast budget.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("30", Interpret(src));
    }

    [Fact]
    public void SharedConstants_AreReadableFromSeveralFunctions()
    {
        // ★ The compiled form is a FILE-SCOPE global assigned at the top of main — not a local of
        // main, which no function could see. Two readers prove the declaration is shared rather
        // than duplicated per function.
        const string src = """
            Define greeting as "hi" permanently.
            Bind text to loud:
                Return greeting in uppercase.
            Done.
            Bind text to quiet:
                Return greeting.
            Done.
            State cast loud.
            State cast quiet.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("HI\nhi", Interpret(src));
    }

    [Fact]
    public void AMutableTopLevelBinding_IsStillRefused()
    {
        // The half of the rule that stays. Without `permanently` this is exactly the hidden global
        // mutable state the isolation exists to prevent.
        const string src = """
            Define counter as 3.
            Bind number to budget:
                Return counter * 2.
            Done.
            State cast budget.
            """;
        var ex = Assert.ThrowsAny<Exception>(() => InterpretRaw(src));
        Assert.Contains("top-level", ex.Message);
    }

    [Fact]
    public void PassingItAsAParameter_Works()
    {
        // The fix the message recommends, exercised so the advice stays true.
        const string src = """
            Define max-retries as 3.
            Bind number to budget, given (the number retries):
                Return retries * 2.
            Done.
            State cast budget on (max-retries).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("6", Interpret(src));
    }

    [Fact]
    public void ANestedFunction_StillCapturesItsEnclosingScope()
    {
        // The other fix the message recommends, and the control that keeps the rule narrow: this
        // is about TOP-LEVEL functions only. A function defined inside another FUNCTION captures
        // that function's scope, and must keep working.
        //
        // ★ It must be inside a function, not merely inside a rabbit. `Bind` becomes a closure
        // only when the interpreter is already executing a call (Interpreter.Core, `_callDepth > 0`);
        // a rabbit block is a memory region, not a call, so a `Bind` there is still a TOP-LEVEL
        // function and genuinely cannot see the rabbit's locals. The checker's `_inFunction` draws
        // the line in exactly the same place — which is what makes the refusal safe to enforce
        // statically.
        const string src = """
            Bind number to scaled, given (the number factor):
                Bind number to triple, given (the number n):
                    Return n * factor.
                Done.
                Return cast triple on (5).
            Done.
            State cast scaled on (3).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("15", Interpret(src));
    }

    // ── The rule applies to every detached body, not just top-level functions ─────────
    //
    // ★ Shared constants first shipped in CheckBind alone, so a method could not read one — and
    // the three backends disagreed about that program too, in BOTH directions:
    //
    //   a method reading a constant     → checked clean, ran with "'limit' isn't defined",
    //                                     compiled and printed the right answer
    //   a method reading mutable data   → checked clean, ran with the teaching refusal,
    //                                     compiled to `cv_tally undeclared` and blamed the compiler
    //
    // Methods, getters, setters, destructors and operator overloads all detach from the top-level
    // scope exactly as a function does, so they all import exactly what a function imports.

    [Fact]
    public void AMethod_CanReadASharedConstant()
    {
        const string src = """
            Define toll as 5 permanently.
            Define object bridge with (the number span):
                Bind number to cost:
                    Return one's span + toll.
                Done.
            Done.
            Define b as a new bridge { the span 2 }.
            State cast b's cost.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("7", Interpret(src));
    }

    [Fact]
    public void AGetterAndASetter_CanReadASharedConstant()
    {
        const string src = """
            Define toll as 5 permanently.
            Define object bridge with (the number span):
                Get cost as number:
                    Return one's span + toll.
                Done.
                Set span given (the number s):
                    One's span becomes s - toll.
                Done.
            Done.
            Define b as a new bridge { the span 2 }.
            State b's cost.
            b's span becomes 20.
            State b's span.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("7\n15", Interpret(src));
    }

    [Fact]
    public void AnOperatorOverload_CanReadASharedConstant()
    {
        const string src = """
            Define toll as 5 permanently.
            Define object bridge with (the number span).
            Bind overloading +, given (the lhs is a bridge, the rhs is a bridge):
                Return a new bridge { the span lhs's span + rhs's span + toll }.
            Done.
            Define first-span as a new bridge { the span 1 }.
            Define second-span as a new bridge { the span 2 }.
            Define summed as first-span + second-span.
            State summed's span.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("8", Interpret(src));
    }

    [Fact]
    public void ATextSharedConstant_KnowsItIsText_InsideAFunction()
    {
        // ★ The minimal repro of the compiler half. `SharedConstants_AreReadableFromSeveralFunctions`
        // passed with a text constant only because a declared `text` return type told the generator
        // what it was. Where nothing else supplies the type — `State it`, or an interpolation hole —
        // the body's type map decided, and a detached body's map was cleared and never re-seeded, so
        // a text constant defaulted to NUMBER: gcc got cufet_print_number on a const char*.
        const string src = """
            Define farewell as "closing" permanently.
            Bind void to play:
                State farewell.
                State "{farewell} now".
            Done.
            Cast play.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("closing\nclosing now", Interpret(src));
    }

    [Fact]
    public void ASharedConstant_MayBeASeriesOrAMap()
    {
        // ★ A region-typed constant declares a GENERATED C type — `static cser_0* cv_suits;` — so
        // its declaration has to sit AFTER the series/map sections, not before them. It was emitted
        // before, two lines above `typedef struct cser_0_s cser_0;`, and only scalars survived
        // that: CufetDec and const char* come from the prelude, so numbers, text and facts all
        // worked while a `permanently` lookup table — the most natural shared constant there is —
        // did not build at all. Interpreted it was correct the whole time.
        const string src = """
            Define suits as a series of text with ("clubs", "hearts", "spades") permanently.
            Define weights as a map from text to number permanently.

            Define object hand with (the number slot):
                Bind text to pick:
                    Define slot-index as one's slot.
                    Return item slot-index of suits.
                Done.
            Done.

            Bind text to first-suit:
                Return item 1 of suits.
            Done.

            In weights, the entry for "clubs" becomes 3.
            Define h as a new hand { the slot 2 }.
            State cast first-suit.
            State cast h's pick.
            State (the entry for "clubs" in weights) but void is 0.
            State the number of suits.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("clubs\nhearts\n3\n3", Interpret(src));
    }

    [Fact]
    public void ATopLevelLambda_IsCallableFromEveryDetachedBody()
    {
        // ★ The checker had always allowed this — every detached body imports anything FunctionType,
        // because mutual recursion depends on it — but the compiler emitted a `Define`d lambda as a
        // LOCAL OF MAIN. So it compiled when called from top-level code and refused with
        // "'doubler': unresolved call" from a function or a method, while the interpreter ran all
        // three. A name a method is allowed to call has to be a symbol a method can reach.
        const string src = """
            Define doubler as a function given (the number value): Return value * 2. Done.
            Define alias-of-doubler as doubler.

            Define object counter with (the number tally):
                Bind number to doubled:
                    Return cast doubler on (one's tally).
                Done.
            Done.

            Bind number to twice-five:
                Return cast alias-of-doubler on (5).
            Done.

            Define c as a new counter { the tally 21 }.
            State cast doubler on (1).
            State cast twice-five.
            State cast c's doubled.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("2\n10\n42", Interpret(src));
    }

    [Fact]
    public void ATopLevelLambda_MayReadASharedConstant()
    {
        const string src = """
            Define factor as 3 permanently.
            Define scaler as a function given (the number value): Return value * factor. Done.

            Define object box with (the number width):
                Bind number to scaled:
                    Return cast scaler on (one's width).
                Done.
            Done.

            Define b as a new box { the width 7 }.
            State cast b's scaled.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("21", Interpret(src));
    }

    [Fact]
    public void ADestructor_CanReadASharedConstant()
    {
        const string src = """
            Define farewell as "closing" permanently.
            Define object gate with (the number id).
            Bind unmaking a gate to close-gate:
                State "{farewell} {one's id}".
            Done.
            Pull a rabbit.
                Define g as a new gate { the id 4 }.
                State "open".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("open\nclosing 4", Interpret(src));
    }

    [Fact]
    public void AMethod_ReadingMutableTopLevelData_IsRefusedByTheChecker()
    {
        // ★ The half that used to pass the checker silently: the name was hidden but unresolved,
        // and an unresolved name infers to null, so nothing complained until run time (interpreted)
        // or gcc (compiled). Neither backend now gets that far.
        const string src = """
            Define tally as 7.
            Define object bridge with (the number span):
                Bind number to cost:
                    Return one's span + tally.
                Done.
            Done.
            Define b as a new bridge { the span 2 }.
            State cast b's cost.
            """;
        var ex = Assert.ThrowsAny<Exception>(() => InterpretRaw(src));
        Assert.Contains("top-level", ex.Message);
        Assert.Contains("tally", ex.Message);

        var cex = Assert.ThrowsAny<Exception>(() => CompileRaw(src));
        Assert.Contains("top-level", cex.Message);
        Assert.DoesNotContain("undeclared", cex.Message);
        Assert.DoesNotContain("bug in the Cufet compiler", cex.Message);
    }

    [Fact]
    public void ALambdaBesideItsData_StillCapturesRatherThanRefusing()
    {
        // ★ The control on the fix. A lambda literal is NOT a detached body — it closes over its
        // enclosing scope — so the hidden-name recording must not reach it. Recording it here
        // rejected a lambda sitting directly beside the binding it captures.
        const string src = """
            Pull a rabbit.
                Define nums as a series of number with (1, 2, 3).
                Define f as a function: Return the number of nums. Done.
                State cast f on ().
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("3", Interpret(src));
    }
}
