namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // ── Rabbits (block-scoped memory regions) ────────────────────────────────

    // "With a rabbit <name>: ... Done."
    // Enters a scope, binds a RabbitValue as the region sentinel, executes the body, exits the
    // scope. In the interpreter (GC-backed) the "freeing at Done" is modeled by the scope exit
    // making the block's values unreachable; the GC reclaims them. The static safety rules
    // (downward-only, hold-only-≥-birth-scope) are enforced by the type checker.
    private void ExecuteWithRabbit(WithRabbitStatement wrs)
    {
        EnterScope();
        Scope[wrs.Name] = new RabbitValue(wrs.Name);
        try
        {
            foreach (var s in wrs.Body)
                Execute(s);
        }
        finally
        {
            ExitScope();
        }
    }
}
