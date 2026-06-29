using System.Collections.Concurrent;

namespace Cufet.Interpreter;

// Cooperative task scheduler for Cufet's concurrency model.
//
// Single-threaded: exactly one unit runs at a time; units interleave only at yield
// points. Built on C# async/await with a custom SynchronizationContext: all awaited
// continuations are routed back to this scheduler's queue (not the thread pool), so
// the cooperative invariant holds by construction — no interpreter-internal data races.
//
// Slice 1 (this file): standalone engine, no user-facing Cufet syntax.
//   Run()    — wraps sequential program execution as a single synchronous unit.
//   RunAll() — isolation validator: runs N async units, proves interleaving works.
//   YieldAsync() — the fundamental yield point (Task.Yield() routed through this ctx).
//
// Slice 2 seam (what comes next):
//   Enqueue(Func<Task>) — adds a spawned task mid-run (called by LaunchTask dispatch).
//   ExecuteStatementsAsync — async variant of statement execution for task bodies.
//   Run() becomes the pump for the whole program; spawned tasks join before Done.
//
// Slice 5 seam: SIGINT check goes inside the Drain loop at each dequeue, so every
// yield point is also a potential interrupt point — no user-side polling needed.
internal sealed class CufetScheduler : SynchronizationContext
{
    // Continuations waiting to run. ConcurrentQueue because in slice 2+ async I/O
    // completes on thread-pool threads and posts continuations cross-thread.
    private readonly ConcurrentQueue<Action> _ready = new();

    // Route async continuations back to this scheduler's queue rather than the thread pool.
    public override void Post(SendOrPostCallback d, object? state)
        => _ready.Enqueue(() => d(state));

    // Synchronous send: run on the calling thread immediately.
    public override void Send(SendOrPostCallback d, object? state)
        => d(state);

    // Run a single async unit to completion on the calling thread.
    // Sequential programs use this: the unit is a synchronous lambda wrapping
    // ExecuteCore, so the scheduler pump is a no-op (task completes immediately).
    internal void Run(Func<Task> unit)
    {
        var prev = Current;
        SetSynchronizationContext(this);
        try
        {
            Task task;
            try   { task = unit(); }
            catch (Exception ex) { task = Task.FromException(ex); }

            Drain([task]);
            task.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(prev); }
    }

    // Enqueue a new task mid-run (slice-2+ task spawn).
    // The unit is not started immediately — it is added to the ready queue and will
    // run on the next drain turn. Returns a Task that completes when the unit finishes.
    // Exceptions from the unit are stored on the returned Task and re-thrown by JoinTasks.
    internal Task Enqueue(Func<Task> unit)
    {
        var tcs = new TaskCompletionSource();
        _ready.Enqueue(() =>
        {
            Task inner;
            try   { inner = unit(); }
            catch (Exception ex) { tcs.SetException(ex); return; }

            if (inner.IsCompletedSuccessfully)
            {
                tcs.SetResult();
            }
            else if (inner.IsFaulted)
            {
                tcs.SetException(inner.Exception!.InnerException ?? inner.Exception!);
            }
            else
            {
                // Async inner task (slice 3+): chain completion back through the scheduler queue
                // via GetAwaiter().OnCompleted, which uses the current SynchronizationContext.
                inner.GetAwaiter().OnCompleted(() =>
                {
                    if (inner.IsFaulted)
                        tcs.SetException(inner.Exception!.InnerException ?? inner.Exception!);
                    else
                        tcs.SetResult();
                });
            }
        });
        return tcs.Task;
    }

    // Drain all tasks in the list to completion, then re-throw any exceptions.
    // Called by ExecutePullRabbit at the rabbit's Done. to join spawned tasks.
    // Re-entrant-safe: this is called from within the main synchronous unit while
    // the outer scheduler.Run is still on the call stack. The drain loop is the same
    // queue, so pending continuations (task bodies) are processed here inline.
    internal void JoinTasks(Task[] tasks)
    {
        if (tasks.Length == 0) return;
        Drain(tasks);
        foreach (var t in tasks)
            t.GetAwaiter().GetResult();
    }

    // Run N async units concurrently to completion on the calling thread.
    // Units interleave at yield points; all are started before the drain loop runs.
    // Used for the slice-1 isolation validator and future multi-task test scenarios.
    internal void RunAll(params Func<Task>[] units)
    {
        var prev = Current;
        SetSynchronizationContext(this);
        try
        {
            var tasks = new Task[units.Length];
            for (int i = 0; i < units.Length; i++)
            {
                try   { tasks[i] = units[i](); }
                catch (Exception ex) { tasks[i] = Task.FromException(ex); }
            }

            Drain(tasks);
            foreach (var t in tasks)
                t.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(prev); }
    }

    // Pump the ready queue until all tracked tasks are complete.
    // Slice 5: SIGINT check goes here — each dequeue is a preemption point.
    // Slice 2+ async I/O: replace the deadlock throw with a blocking wait
    // (SemaphoreSlim / Monitor.Wait) so the thread sleeps until I/O posts a continuation.
    private void Drain(Task[] tasks)
    {
        while (!AllDone(tasks))
        {
            if (_ready.TryDequeue(out var work))
                work();
            else
                throw new InvalidOperationException(
                    "CufetScheduler: all units are suspended with no continuations queued " +
                    "(deadlock). In cooperative mode this means a unit is awaiting something " +
                    "that never posts a continuation.");
        }
    }

    private static bool AllDone(Task[] tasks)
    {
        foreach (var t in tasks)
            if (!t.IsCompleted) return false;
        return true;
    }

    // Yield the current unit: suspends it and re-queues its continuation via the
    // SynchronizationContext, giving other ready units a chance to run.
    internal static async Task YieldAsync() { await Task.Yield(); }
}
