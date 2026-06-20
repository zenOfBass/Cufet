namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Rabbits (block-scoped memory regions) ────────────────────────────────

    // "With a rabbit <name>: ... Done."
    // Enters a scope, binds the rabbit name as RabbitType, checks the body, exits the scope.
    // Downward-only enforcement:
    //   • Returning the rabbit → caught in CheckReturn (RabbitType guard).
    //   • A rabbit parameter declared with 'given (the rabbit r)' is RabbitType in the callee's
    //     scope; any attempt to return it hits the same CheckReturn guard.
    // "Hold only ≥ birth-scope" (store-site enforcement) is a future layer — the static scope
    // structure already prevents the callee from storing its own locals into the passed rabbit
    // once the birth-scope check is added.
    private void CheckWithRabbit(WithRabbitStatement wrs)
    {
        EnterScope();
        Scope[wrs.Name] = new TypeInfo(
            RabbitType.Instance,
            new VariableReference(wrs.Name, wrs.Line),
            wrs.Line);
        try { foreach (var s in wrs.Body) CheckStatement(s); }
        finally { ExitScope(); }
    }
}
