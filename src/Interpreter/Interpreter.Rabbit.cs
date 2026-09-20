namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // ── Rabbits (block-scoped memory regions) ────────────────────────────────

    // Per-rabbit task lists, stacked to support nested rabbits.
    // Pushed by ExecutePullRabbit when the rabbit opens; popped before Done. joins.
    // ExecuteLaunchTask registers its spawned task on the innermost (top) list.
    private readonly Stack<List<Task>> _rabbitTaskStacks = new();

    // The handle for each entry in the stack above, or null for a fire-and-forget task. Kept
    // parallel rather than folded into one list so `JoinTasks(Task[])` keeps its signature and
    // the scheduler stays ignorant of what a task handle is.
    private readonly Stack<List<TaskHandle?>> _rabbitHandleStacks = new();

    // Runtime handle for a named task (slice 4). Created at task launch and bound to
    // the task's name in scope. The C# Task completes when the task body finishes;
    // the returned value (if any) is stored by RunTaskBody catching ReturnException.
    internal sealed class TaskHandle
    {
        public Task? CSharpTask;
        public object? Result;
        public bool HasResult;
        /// <summary>Whether anything read this task's result.</summary>
        /// <remarks>
        /// ★★ Reading takes ownership of the failure. A task whose result an awaiter read is that
        /// awaiter's business, so the rabbit's `Done.` reports only faults nobody claimed — without
        /// this, catching a fault at the await and suppressing it still ended the program, because
        /// the join announced the same fault a second time. A fire-and-forget task has no handle to
        /// read, so yesterday's behaviour falls out of the same rule rather than being a second one.
        /// </remarks>
        public bool WasRead;
    }

    // "Pull a rabbit [as <name>]. ... Done."
    // Enters a scope, optionally binds a RabbitValue as the region sentinel, executes the body,
    // joins all tasks spawned in this rabbit (structured guarantee — tasks can't outlive their
    // rabbit), then exits the scope. In the interpreter (GC-backed) the "freeing at Done" is
    // modeled by the scope exit making the block's values unreachable; the GC reclaims them.
    // The static safety rules (downward-only, hold-only-≥-birth-scope) are enforced by the
    // type checker; task lifetime is enforced here at the join site.
    private void ExecutePullRabbit(PullRabbitStatement prs)
    {
        var myTasks = new List<Task>();
        var myHandles = new List<TaskHandle?>();
        _rabbitTaskStacks.Push(myTasks);
        _rabbitHandleStacks.Push(myHandles);
        EnterScope();
        if (prs.Name != null)
            Scope[prs.Name] = new RabbitValue(prs.Name);
        try
        {
            foreach (var s in prs.Body)
                Execute(s);
            // Join all spawned tasks before releasing the scope. This is the structured
            // guarantee: tasks cannot outlive their rabbit. The scope is still open here,
            // so task bodies can access rabbit-local variables.
            if (myTasks.Count > 0)
                _scheduler!.JoinTasks([.. myTasks], [.. myHandles]);
        }
        finally
        {
            _rabbitTaskStacks.Pop();
            _rabbitHandleStacks.Pop();
            ExitScope();
        }
    }

    // "Have rabbit start a task [as <name>]: ... Done."
    // Enqueues the task body onto the cooperative scheduler; registers the returned Task
    // with the enclosing rabbit's task list so the rabbit's Done. will join it.
    // If named, creates a TaskHandle, stores the return value at completion, and binds
    // the handle to the task's name in the current scope for slice-4 result-await.
    private void ExecuteLaunchTask(LaunchTaskStatement lts)
    {
        var body   = lts.Body; // capture for closure — do not close over lts
        TaskHandle? handle = lts.Name != null ? new TaskHandle() : null;

        var task = _scheduler!.Enqueue(() =>
        {
            RunTaskBody(body, handle);
            return Task.CompletedTask;
        });

        if (handle != null)
            handle.CSharpTask = task;

        _rabbitTaskStacks.Peek().Add(task);
        _rabbitHandleStacks.Peek().Add(handle);

        if (lts.Name != null)
            Scope[lts.Name] = handle!;
    }

    // Executes a task body in its own nested scope. The enclosing rabbit's scope is still
    // open (this runs during JoinTasks before ExitScope), so task bodies have full read
    // access to rabbit-local variables. If a handle is provided, ReturnException is caught
    // and the returned value is stored on the handle (rather than propagating as a fault).
    private void RunTaskBody(IReadOnlyList<IStatement> body, TaskHandle? handle = null)
    {
        EnterScope();
        try
        {
            foreach (var s in body)
                Execute(s);
        }
        catch (ReturnException re)
        {
            if (handle != null)
            {
                handle.Result    = re.Value;
                handle.HasResult = true;
            }
            // Anonymous task with return: result silently dropped.
        }
        finally
        {
            ExitScope();
        }
    }
}
