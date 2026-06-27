namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // ── Rabbits (block-scoped memory regions) ────────────────────────────────

    // "Pull a rabbit [as <name>]. ... Done."
    // Enters a scope, optionally binds a RabbitValue as the region sentinel, executes the body,
    // exits the scope. In the interpreter (GC-backed) the "freeing at Done" is modeled by the
    // scope exit making the block's values unreachable; the GC reclaims them. The static safety
    // rules (downward-only, hold-only-≥-birth-scope) are enforced by the type checker.
    private void ExecutePullRabbit(PullRabbitStatement prs)
    {
        EnterScope();
        if (prs.Name != null)
            Scope[prs.Name] = new RabbitValue(prs.Name);
        try
        {
            foreach (var s in prs.Body)
                Execute(s);
        }
        finally
        {
            ExitScope();
        }
    }
}
