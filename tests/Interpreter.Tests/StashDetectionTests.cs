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
/// reverted. Here the ONLY `bury` is inside an arm body, and the assertion is that `unbury` type-
/// checks, which is true only if the walk actually reached it.
public class StashDetectionTests
{
    private static void Check(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
    }

    [Fact]
    public void ABuryInsideAnIfArm_StillMakesTheFunctionStashProducing()
    {
        // The `bury` appears ONLY inside the arm body — nowhere a walk keyed on IStatement reaches.
        Check("""
            Bind number to picky, given (the fact go):
                If go:
                    Bury 1.
                Done.
            Done.

            Define s as cast picky on (true).
            State unbury s.
            """);
    }

    // ⚠ This one does NOT discriminate, and saying so matters more than the extra green tick.
    // Measured by keying the walk on IStatement/IExpression instead of the namespace: the If-arm and
    // Judge-arm tests above and below both went red, and this one stayed GREEN — an `Otherwise`
    // body is reached through an ordinary property, not through a `ConditionArm`. It is kept as a
    // behaviour test, not as a guard on the walk.
    [Fact]
    public void ABuryInsideAnOtherwiseArm_StillMakesTheFunctionStashProducing()
    {
        Check("""
            Bind number to picky, given (the fact go):
                If go:
                    State "no".
                Done.
                Otherwise:
                    Bury 2.
                Done.
            Done.

            Define s as cast picky on (false).
            State unbury s.
            """);
    }

    [Fact]
    public void ABuryInsideAJudgeArm_StillMakesTheFunctionStashProducing()
    {
        Check("""
            Bind number to sorter, given (the (number or text) thing):
                Judge thing, where it is:
                    A number, bury 1.
                    A text, bury 2.
                Done.
            Done.

            Define s as cast sorter on (5).
            State unbury s.
            """);
    }

    [Fact]
    public void ANestedFunctionsBury_DoesNotLeakOutward()
    {
        // ★ The control on the walk's boundary. A nested `Bind` that buries is its own stash
        // producer; if its `bury` counted for the enclosing function, `outer` would silently become
        // a generator and its ordinary `Return` would stop making sense.
        Check("""
            Bind number to outer, given (the number n):
                Bind number to inner:
                    Bury n.
                Done.
                Return n + 1.
            Done.

            State cast outer on (1).
            """);
    }
}
