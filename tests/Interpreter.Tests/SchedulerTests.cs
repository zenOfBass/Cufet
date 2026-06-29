using Cufet.Interpreter;
using Xunit;

namespace Cufet.Interpreter.Tests;

// Validates the CufetScheduler engine in isolation — no Cufet syntax involved.
// These tests prove the cooperative scheduling infrastructure is correct before
// slice 2 bolts task-spawn syntax on top of it.
public class SchedulerTests
{
    // A synchronous unit (never awaits) completes through the scheduler unchanged.
    [Fact]
    public void SingleSyncUnitRunsToCompletion()
    {
        var ran = false;
        var scheduler = new CufetScheduler();
        scheduler.Run(() => { ran = true; return Task.CompletedTask; });
        Assert.True(ran);
    }

    // An async unit that yields once completes with its steps in order.
    [Fact]
    public void SingleAsyncUnitRunsToCompletion()
    {
        var log = new List<string>();
        var scheduler = new CufetScheduler();
        scheduler.Run(async () =>
        {
            log.Add("before");
            await CufetScheduler.YieldAsync();
            log.Add("after");
        });
        Assert.Equal(["before", "after"], log);
    }

    // Two async units interleave at yield points and both complete.
    // Key invariant: B:1 appears before A:2 — B ran while A was suspended.
    // Order is deterministic: A starts first (first in array), yields; B starts,
    // yields; A resumes; B resumes. FIFO continuation queue.
    [Fact]
    public void TwoUnitsInterleaveAtYieldPoints()
    {
        var log = new List<string>();
        var scheduler = new CufetScheduler();

        async Task UnitA()
        {
            log.Add("A:1");
            await CufetScheduler.YieldAsync();
            log.Add("A:2");
        }

        async Task UnitB()
        {
            log.Add("B:1");
            await CufetScheduler.YieldAsync();
            log.Add("B:2");
        }

        scheduler.RunAll(UnitA, UnitB);

        Assert.Equal(new[] { "A:1", "B:1", "A:2", "B:2" }, log);
    }

    // Both units complete even if they yield multiple times.
    [Fact]
    public void BothUnitsCompleteAfterMultipleYields()
    {
        var completedA = false;
        var completedB = false;
        var scheduler = new CufetScheduler();

        async Task UnitA()
        {
            await CufetScheduler.YieldAsync();
            await CufetScheduler.YieldAsync();
            completedA = true;
        }

        async Task UnitB()
        {
            await CufetScheduler.YieldAsync();
            await CufetScheduler.YieldAsync();
            completedB = true;
        }

        scheduler.RunAll(UnitA, UnitB);
        Assert.True(completedA);
        Assert.True(completedB);
    }

    // An exception thrown after a yield propagates out of Run() correctly.
    [Fact]
    public void ExceptionFromAsyncUnitPropagates()
    {
        var scheduler = new CufetScheduler();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            scheduler.Run(async () =>
            {
                await CufetScheduler.YieldAsync();
                throw new InvalidOperationException("unit failure");
            }));
        Assert.Equal("unit failure", ex.Message);
    }

    // An exception thrown synchronously (before first yield) also propagates.
    [Fact]
    public void ExceptionFromSyncUnitPropagates()
    {
        var scheduler = new CufetScheduler();
        static Task ThrowSync() => throw new InvalidOperationException("sync failure");
        Assert.Throws<InvalidOperationException>(() => scheduler.Run(ThrowSync));
    }
}
