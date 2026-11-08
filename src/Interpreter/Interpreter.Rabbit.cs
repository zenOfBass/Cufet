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

    // How each open rabbit composes its tasks, parallel to the stacks above. A sequential rabbit
    // remembers its last task so the next can wait for it; an automatic one collects its tasks
    // here instead of starting them, and starts the chosen one at its `Done.`.
    private sealed class RabbitMode
    {
        public string? Mode;
        public Task? LastTask;
        public readonly List<(ChannelValue Channel, string Binder, IReadOnlyList<IStatement> Body)> Alternatives = [];
    }
    private readonly Stack<RabbitMode> _rabbitModeStack = new();

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
        var myMode = new RabbitMode { Mode = prs.Mode };
        _rabbitTaskStacks.Push(myTasks);
        _rabbitHandleStacks.Push(myHandles);
        _rabbitModeStack.Push(myMode);
        EnterScope();
        if (prs.Name != null)
            Scope[prs.Name] = new RabbitValue(prs.Name);
        try
        {
            foreach (var s in prs.Body)
                Execute(s);
            if (myMode.Alternatives.Count > 0)
                StartChosenAlternative(myMode, myTasks, myHandles);
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
            _rabbitModeStack.Pop();
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
        var mode   = _rabbitModeStack.Peek();

        // An automatic rabbit's task does not start here: it joins the alternatives, and the
        // channel it waits on is the one this line names NOW.
        if (lts.Binder is { } binder)
        {
            mode.Alternatives.Add(((ChannelValue)Evaluate(lts.Source!), binder, body));
            return;
        }

        TaskHandle? handle = lts.Name != null ? new TaskHandle() : null;

        // A sequential rabbit's task waits for the one started before it — whether that one ended
        // normally or failed, since every task still runs and its faults are reported together.
        var previous = mode.Mode == "sequential" ? mode.LastTask : null;

        // ★★ A task gets its OWN copy of what it uses, taken NOW, when it is started — as a
        // compiled task's captures are copied in at spawn. The body only runs later, at a yield
        // or the rabbit's `Done.`, and it used to read the live names then: a loop variable was
        // gone by that time ("'n' isn't defined" for a task started inside a `For each`), and a
        // name changed after the start was seen changed here and unchanged compiled. Writes to
        // what a task captured are refused where anyone could see them, so a copy loses nothing.
        // Set into the task's scope directly, not Defined, so a copied object is never unmade.
        var captured = new Dictionary<string, object>();
        foreach (var name in TaskCaptures.Of(body, n => TryLookupValue(n, out _)))
            if (TryLookupValue(name, out var value)) captured[name] = DeepCopyForChannel(value);

        var task = _scheduler!.Enqueue(async () =>
        {
            if (previous != null)
                try { await previous; } catch { /* reported at the join */ }
            RunTaskBody(body, handle, captured: captured);
        });
        mode.LastTask = task;

        if (handle != null)
            handle.CSharpTask = task;

        _rabbitTaskStacks.Peek().Add(task);
        _rabbitHandleStacks.Peek().Add(handle);

        if (lts.Name != null)
            Scope[lts.Name] = handle!;
    }

    // An automatic rabbit's `Done.`: wait until one of its channels holds something (or is closed
    // and empty), take what was SENT FIRST across all of them, and run that one task with it.
    // ★ Two tasks waiting on the same channel see the same item; the one written first takes it.
    // ★★ The chosen task runs HERE, on the rabbit's own flow, as an Occam ALT's branch does: nothing
    // runs beside it, so it is ordinary code and may change the names around it.
    private void StartChosenAlternative(RabbitMode mode, List<Task> tasks, List<TaskHandle?> handles)
    {
        var alternatives = mode.Alternatives;
        int Chosen()
        {
            int best = -1; long bestStamp = long.MaxValue;
            for (int i = 0; i < alternatives.Count; i++)
                if (alternatives[i].Channel.ReadyStamp is long s && s < bestStamp) { best = i; bestStamp = s; }
            return best;
        }
        if (Chosen() < 0)
        {
            _scheduler!.DrainUntil(() => Chosen() >= 0 || _interruptRequested);
            if (_interruptRequested) throw new InterruptUnwind();
        }
        var (channel, binder, body) = alternatives[Chosen()];
        object delivered = channel.HasValue ? channel.Dequeue() : VoidValue.Instance;
        RunTaskBody(body, null, (binder, delivered));
    }

    // Executes a task body in its own nested scope. The enclosing rabbit's scope is still
    // open (this runs during JoinTasks before ExitScope), so task bodies have full read
    // access to rabbit-local variables. If a handle is provided, ReturnException is caught
    // and the returned value is stored on the handle (rather than propagating as a fault).
    private void RunTaskBody(IReadOnlyList<IStatement> body, TaskHandle? handle = null,
                             (string Name, object Value)? delivery = null,
                             Dictionary<string, object>? captured = null)
    {
        EnterScope();
        _taskBodyDepth++;
        if (captured != null)
            foreach (var (name, value) in captured) Scope[name] = value;
        if (delivery is { } d) Scope[d.Name] = d.Value;
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
            _taskBodyDepth--;
            ExitScope();
        }
    }

    // How many task bodies are running, innermost-first. Task bodies nest like calls — another
    // task only ever runs inside a drain this one started — so a counter is enough.
    private int _taskBodyDepth;
}
