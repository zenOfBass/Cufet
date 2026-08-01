using System.Collections.Concurrent;

namespace Cufet.Interpreter;

// The cooperative scheduler behind `Have rabbit start a task:` and channels.
//
// Single-threaded: exactly one unit runs at a time, and units interleave only at yield
// points. Built on C# async/await with a custom SynchronizationContext, so every awaited
// continuation is routed back to this queue rather than to the thread pool — which is what
// makes the cooperative invariant hold by construction, with no interpreter-internal races.
//
// ★ This is the interpreter's answer only. The compiler's tasks are pthreads, genuinely
// parallel, and the two agree on results because the language forbids the shapes where
// they would not — a task may not write to anything outside it that something else reads.
//
// The entry points, and who calls them:
//   Run(unit)         — the whole program, as one unit. ExecuteCore is synchronous, so the
//                       pump has nothing to do until a task is spawned.
//   Enqueue(unit)     — a task body, queued to start on the next turn (ExecuteLaunchTask).
//   JoinTasks(tasks)  — the rabbit's `Done.`, which is where spawned tasks are joined.
//   DrainUntil(cond)  — pump until something becomes true (a channel receive waiting on a
//                       delivery, or on the interrupt flag).
//   DrainOne()        — give other units exactly one turn (a yield, and pipe stages).
//   YieldAsync()      — the yield point itself.
//   RunAll(units)     — test support only; the interpreter never calls it.
//
// ★ Interrupts are NOT handled in here, deliberately. The scheduler has no view of
// interpreter state, so Ctrl-C is polled by the interpreter at the two places where a
// program can be waiting: ExecuteYield checks the flag after giving up a turn, and a
// channel receive folds it into its DrainUntil condition. Pumping the queue is this
// class's whole job; deciding what a set flag means is not.
internal sealed class CufetScheduler : SynchronizationContext
{
    // Continuations waiting to run. Nothing posts to this from another thread today — the
    // scheduler is single-threaded and Post is only ever reached from the unit that yielded —
    // so the concurrent queue is insurance rather than a requirement. It stays because the
    // thing that would need it is exactly what a SynchronizationContext is for: a genuinely
    // asynchronous wait completing on a thread-pool thread and posting its continuation back.
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

    // Enqueue a task body mid-run — the spawn behind `Have rabbit start a task:`.
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
                // The unit yielded rather than finishing in one go: chain its completion back
                // through the scheduler queue via GetAwaiter().OnCompleted, which posts through
                // the current SynchronizationContext — this one.
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

    // Run N async units concurrently to completion on the calling thread. Units interleave at
    // yield points; all are started before the drain loop runs.
    //
    // Test support: the interpreter spawns tasks through Enqueue and joins them through
    // JoinTasks, and never calls this. It survives because it drives the interleaving directly,
    // which is what SchedulerTests needs to assert the cooperative invariant without a Cufet
    // program in the way.
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

    // Pump the ready queue until every tracked task is complete.
    //
    // The throw is reachable only by a unit awaiting something that will never post a
    // continuation, which in a purely cooperative, single-threaded pump means a real deadlock
    // and not a wait worth sleeping through. Were an asynchronous wait ever added, this is the
    // line that would change: a blocking wait (SemaphoreSlim / Monitor.Wait) rather than a
    // throw, so the thread sleeps until the completion posts.
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

    // Dequeue and run one unit of work if any is ready — one turn for somebody else, without
    // blocking the current flow. Called by a yield, and by a pipe stage handing its output on.
    internal void DrainOne()
    {
        if (_ready.TryDequeue(out var work))
            work();
    }

    // Pump the ready queue until condition is met.
    // Re-entrant-safe: single-threaded, so a nested drain is just queue pumping.
    internal void DrainUntil(Func<bool> condition)
    {
        while (!condition())
        {
            if (!_ready.TryDequeue(out var work))
                throw new InvalidOperationException(
                    "CufetScheduler: channel deadlock — a task is waiting for delivery but no " +
                    "running or queued tasks will send to this channel.");
            work();
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
