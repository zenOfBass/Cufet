using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// A function becomes stash-producing by CONTAINING a `bury` — nothing marks the declaration, the
/// same way nothing marks a function fallible and `return a failure` in the body decides it.
/// </summary>
///
/// ★ Which makes the DETECTION WALK load-bearing, and this file is the proof the exhaustiveness
/// suite demands before a new reflection walk may be registered. `ConditionArm` and `JudgeArm`
/// implement neither `IExpression` nor `IStatement`, so a walk keyed on those interfaces reads
/// straight past the body of every `If` and every judgement. A `bury` hiding in one would leave the
/// function looking ordinary: `cast` would give a plain number instead of a stash, and the failure
/// would surface far away as "'unbury' needs a stash".
///
/// The probe is deliberately indirect for the reason that file warns about — a first attempt at one
/// of these captured the Judge's `Subject`, which is an ordinary property, and passed with the bug
/// reverted. Here the ONLY `bury` is inside an arm body.
public class StashDetectionTests
{
    private static Program Check(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        return new TypeChecker().Check(program);
    }

    /// <summary>
    /// Asserts the function was RECOGNISED as burying, by way of the state machine that only a
    /// recognised one gets.
    /// </summary>
    /// <remarks>
    /// ★ Checking for the rewrite rather than for a clean check is what makes this discriminating.
    /// An unrecognised burying function type-checks perfectly happily — its `bury` is simply left
    /// where it sits, and the mistake surfaces far away as "'unbury' needs a stash". The resume
    /// function only exists if the walk found the bury.
    /// </remarks>
    private static void DetectedAsBurying(string source, string functionName)
    {
        var program = Check(source);
        Assert.Contains(program.Statements,
            s => s is BindStatement bind && bind.Name == "stash_resume_" + functionName);
    }

    [Fact]
    public void ABuryInsideAnIfArm_StillMakesTheFunctionStashProducing()
    {
        // The `bury` appears ONLY inside the arm body — nowhere a walk keyed on IStatement reaches.
        DetectedAsBurying("""
            Bind number to picky, given (the rabbit helper, the fact go):
                If go:
                    Have helper bury 1.
                Done.
            Done.

            Pull a rabbit as den.
                Define s as cast picky on (den, true).
                State unbury s.
            Done.
            """, "picky");
    }

    // ⚠ This one does NOT discriminate, and saying so matters more than the extra green tick.
    // Measured by keying the walk on IStatement/IExpression instead of the namespace: the If-arm and
    // Judge-arm tests above and below both went red, and this one stayed GREEN — an `Otherwise`
    // body is reached through an ordinary property, not through a `ConditionArm`. It is kept as a
    // behaviour test, not as a guard on the walk.
    [Fact]
    public void ABuryInsideAnOtherwiseArm_StillMakesTheFunctionStashProducing()
    {
        DetectedAsBurying("""
            Bind number to picky, given (the rabbit helper, the fact go):
                If go:
                    State "no".
                Done.
                Otherwise:
                    Have helper bury 2.
                Done.
            Done.

            Pull a rabbit as den.
                Define s as cast picky on (den, false).
                State unbury s.
            Done.
            """, "picky");
    }

    /// <summary>
    /// A `bury` hiding in a judgement ARM body is found by the walk.
    /// </summary>
    /// <remarks>
    /// ★ `JudgeArm` implements neither `IExpression` nor `IStatement`, so a walk keyed on those
    /// interfaces reads straight past every arm body. The only `bury` here is inside an arm, which
    /// is what makes this discriminating — and the assertion is the REWRITE rather than a clean
    /// check, because an unrecognised burying function type-checks perfectly happily and only
    /// misbehaves far away, at the `unbury`.
    /// </remarks>
    [Fact]
    public void ABuryInsideAJudgeArm_StillMakesTheFunctionStashProducing()
    {
        DetectedAsBurying("""
            Bind number to sorter, given (the rabbit helper, the (number or text) thing):
                Judge thing, where it is:
                    A number, have helper bury 1.
                    A text, have helper bury 2.
                Done.
            Done.

            Pull a rabbit as den.
                Define s as cast sorter on (den, 5).
                State unbury s but void is 0.
            Done.
            """, "sorter");
    }

    [Fact]
    public void ANestedFunctionsBury_DoesNotLeakOutward()
    {
        // ★ The control on the walk's boundary. A nested `Bind` that buries is its own stash
        // producer; if its `bury` counted for the enclosing function, `outer` would silently become
        // a generator and its ordinary `Return` would stop making sense.
        Check("""
            Bind number to outer, given (the rabbit helper, the number n):
                Bind number to inner, given (the rabbit digger):
                    Have digger bury n.
                Done.
                Return n + 1.
            Done.

            Pull a rabbit as den.
                State cast outer on (den, 1).
            Done.
            """);
    }
}
