using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// What a program still cleans up on the way out — unmakers on an UNHANDLED fault, and a task
/// body's own scope.
/// </summary>
/// <remarks>
/// ★ Two defects lived here, and neither could be seen by any existing test, for the same reason:
/// the oracle's interpreted half loses its output when a program dies (the fault leaves Execute as
/// an exception and takes the StringWriter with it), so nothing compared what a dying program had
/// printed. <c>AssertFaultOracle</c> is the missing half.
///
/// The two were separate and only looked like one bug:
///
/// 1. <c>cufet_raise</c> with no handler installed printed and called <c>exit(1)</c> without
///    running this thread's pending unmakers, so a fault inside a block abandoned every destructor
///    in it. The path where a handler DOES exist had always run them first.
/// 2. A task body was emitted as a plain FRAME, so an object Defined at its own top level never
///    registered an unmaker at all — and that one is not about dying: a task that completes
///    perfectly normally also skipped it. The interpreter's RunTaskBody wraps the body in
///    EnterScope/ExitScope, not the SaveScopes/RestoreScopes a call gets, which makes a task body
///    a block scope and every other frame not one.
///
/// ⚠ A function body's own top-level Defines still do not fire, on EITHER backend, and neither
/// does a program's top level. That is the settled unmaker rule (see the note on
/// <c>UnmakerHdr</c>), not a third defect — <c>FunctionFrame_TopLevelDefine_StillDoesNotFire</c>
/// pins it so a future change to the task rule cannot quietly drag the function rule along.
/// </remarks>
public class PipelineDyingCleanupTests : PipelineTestBase
{
    [Fact]
    public void UnhandledFault_InBlock_RunsUnmakers()
    {
        const string src = UnmakerHdr + """
            If 1 is 1:
                Define h as a new handle { the id "IN-BLOCK" }.
                State 1 / 0.
            Done.
            """;
        AssertFaultOracle(src);
        // Pin the value too — an oracle equality alone passes when BOTH backends fall silent.
        Assert.Contains("unmake IN-BLOCK", InterpretThroughFaultRaw(src));
        Assert.Contains("unmake IN-BLOCK", CompileRaw(src));
    }

    [Fact]
    public void UnhandledFault_NestedBlocksAndCall_UnwindsAllOfThem()
    {
        // LIFO, through a call frame and out: the innermost block's object first, then the
        // caller's. `inner` is a function frame's own Define and fires on NEITHER backend.
        const string src = UnmakerHdr + """
            Bind void to deep:
                Define inner as a new handle { the id "FRAME" }.
                If 1 is 1:
                    Define deeper as a new handle { the id "DEEPER" }.
                    State 1 / 0.
                Done.
            Done.

            If 1 is 1:
                Define outer as a new handle { the id "OUTER" }.
                Cast deep.
            Done.
            """;
        AssertFaultOracle(src);
        Assert.Equal("unmake DEEPER\nunmake OUTER", Norm(InterpretThroughFaultRaw(src)));
    }

    [Fact]
    public void UnhandledFault_InTask_RunsTheTasksUnmakers()
    {
        const string src = UnmakerHdr + """
            Pull a rabbit as hopper.
                Have hopper start a task:
                    Define h as a new handle { the id "IN-TASK" }.
                    State 1 / 0.
                Done.
            Done.
            """;
        AssertFaultOracle(src);
        Assert.Contains("unmake IN-TASK", CompileRaw(src));
    }

    [Fact]
    public void TaskBody_IsABlockScope_EvenWhenNothingFaults()
    {
        // ★ The one that shows defect 2 is not about exceptions at all. No fault anywhere, and the
        // compiled program still never unmade the task's own object.
        const string src = UnmakerHdr + """
            Pull a rabbit as hopper.
                Have hopper start a task:
                    Define h as a new handle { the id "IN-TASK" }.
                    State "task ran".
                Done.
            Done.
            State "after".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("task ran\nunmake IN-TASK\nafter", Compile(src));
    }

    [Fact]
    public void FunctionFrame_TopLevelDefine_StillDoesNotFire()
    {
        // The rule the task change must NOT have generalised: a call frame is still not a block.
        const string src = UnmakerHdr + """
            Bind void to work:
                Define h as a new handle { the id "FRAME" }.
                State "worked".
            Done.

            Cast work.
            State "after".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("worked\nafter", Compile(src));
    }

    [Fact]
    public void CaughtFault_StillRunsUnmakers_Unchanged()
    {
        // The path that always worked, kept beside the one that did not — the no-handler fix must
        // not have disturbed it, and it is what the fix was modelled on.
        const string src = UnmakerHdr + """
            Try to:
                If 1 is 1:
                    Define h as a new handle { the id "CAUGHT" }.
                    State 1 / 0.
                Done.
            Done.
            In case of exception (the exception):
                State "caught".
                Suppress the exception.
            Done.
            State "after".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("unmake CAUGHT\ncaught\nafter", Compile(src));
    }
}
