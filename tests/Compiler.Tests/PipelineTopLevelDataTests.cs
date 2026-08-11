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
    public void APermanentlyBinding_IsRefusedTheSameWay_ForNow()
    {
        // Documents today's behaviour deliberately: `permanently` makes no difference yet, because
        // the rule is about top-level DATA rather than about mutability. Relaxing it for exactly
        // the immutable case is the shared-constants item, and this test is what will change when
        // that lands — which is the point of writing it down now.
        const string src = """
            Define max-retries as 3 permanently.
            Bind number to budget:
                Return max-retries * 2.
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
}
