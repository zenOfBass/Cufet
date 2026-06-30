namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Rabbits (block-scoped memory regions) ────────────────────────────────

    // "Pull a rabbit [as <name>]. ... Done."
    // Increments _rabbitDepth so every variable defined inside inherits that depth.
    // Downward-only enforcement:
    //   • Returning the rabbit → caught in CheckReturn (RabbitType guard).
    //   • A rabbit parameter declared with 'given (the rabbit r)' is RabbitType in the callee's
    //     scope; any attempt to return it hits the same CheckReturn guard.
    // Outward-only store invariant (CheckRegionStore):
    //   • Called at every store site (Becomes, SeriesAdd, SeriesSet, MapSet, field sets).
    //   • source.depth > target.depth → error (shorter-lived into longer-lived).
    private void CheckPullRabbit(PullRabbitStatement prs)
    {
        _rabbitDepth++;
        EnterScope();
        if (prs.Name != null)
            Scope[prs.Name] = new TypeInfo(
                RabbitType.Instance,
                new VariableReference(prs.Name, prs.Line),
                prs.Line,
                RabbitDepth: _rabbitDepth);
        try { foreach (var s in prs.Body) CheckStatement(s); }
        finally { ExitScope(); _rabbitDepth--; }
    }

    // "Have rabbit start a task [as <name>]: ... Done."
    // Static checks:
    //   1. Must be inside an active rabbit (_rabbitDepth > 0) — enforced redundantly here
    //      and in the parser (parse error fires first; this is a belt-and-suspenders guard).
    //   2. Task body type-checks as a scope at the current rabbit depth. The task is
    //      structured (joins before its rabbit's Done.), so it is shorter-lived than the
    //      rabbit → no new soundness machinery needed. The existing region depth checks in
    //      CheckRegionStore fire if the task body tries to store a task-local reference
    //      into a longer-lived container, exactly as they would for any nested scope.
    // Note: name (slice-4 result-await identity) is recorded but not type-checked yet.
    private void CheckLaunchTask(LaunchTaskStatement lts)
    {
        if (_rabbitDepth == 0)
            throw new TypeException(FormatTypeError(
                "'Have rabbit start a task' requires an active rabbit",
                null, lts.Line,
                "spawning a task outside any rabbit scope",
                "Wrap the task spawn in 'Pull a rabbit. ... Done.'"));
        EnterScope();
        try { foreach (var s in lts.Body) CheckStatement(s); }
        finally { ExitScope(); }
    }

    // ── Outward-only store invariant helpers ─────────────────────────────────

    // Series, maps, objects, and matrices are reference types tracked by rabbit depth.
    // Value types (number, text, fact, record) may be stored anywhere without constraint.
    private static bool IsReferenceType(CufetType? t) =>
        t is SeriesType or MapType or ObjectType or MatrixType or ChannelType;

    // Returns the rabbit depth of an expression's value.
    //   VariableReference       → stored depth from its TypeInfo.
    //   Reference-type literal  → _rabbitDepth (born here, in current rabbit).
    //   CastExpression          → depth from _castDepthCache (set by InferCastExpr using the
    //                             callee's ReturnDepthSignature + receiver depth at the call site).
    //   PossessiveAccess        → depth from _possessiveDepthCache (set by InferPossessiveAccess;
    //                             = receiver depth for fields; getter-sig-derived for getters).
    //   RecordNamedAccess       → depth from _rnaDepthCache (set by InferRecordNamedAccess;
    //                             same logic as possessive, for 'the member of obj' syntax).
    //   Anything else           → 0 (value type, unknown — treated as safe).
    private int ValueDepthOf(IExpression expr, CufetType? inferredType)
    {
        if (!IsReferenceType(inferredType)) return 0;
        if (expr is VariableReference vr && TryLookup(vr.Name, out var ti)) return ti.RabbitDepth;
        if (expr is SeriesLiteral or MapLiteral or MatrixLiteral or MatrixSized
                 or ObjectLiteral or RangeExpression or ChannelCreation)
            return _rabbitDepth;
        if (expr is CastExpression cast && _castDepthCache.TryGetValue(cast, out var cachedDepth))
            return cachedDepth;
        if (expr is PossessiveAccess poss && _possessiveDepthCache.TryGetValue(poss, out var possDepth))
            return possDepth;
        if (expr is RecordNamedAccess rna && _rnaDepthCache.TryGetValue(rna, out var rnaDepth))
            return rnaDepth;
        return 0;
    }

    // Returns the rabbit depth of a container expression (the target of a set/insert).
    // Falls back to _rabbitDepth when the target isn't a plain variable reference, so
    // complex target expressions are treated as same-depth (avoids false positives).
    private int ContainerDepthOf(IExpression expr) =>
        expr is VariableReference vr && TryLookup(vr.Name, out var ti) ? ti.RabbitDepth : _rabbitDepth;

    // Core check: source depth > target depth means a shorter-lived value would be stored
    // in a longer-lived container — a future use-after-free in the native backend.
    // `action` fills the "you're trying to ..." slot in the error message.
    private void CheckRegionStore(IExpression valueExpr, CufetType? valueType, int targetDepth, int line, string action)
    {
        var valueDepth = ValueDepthOf(valueExpr, valueType);
        if (valueDepth <= targetDepth) return;
        throw new TypeException(FormatTypeError(
            "this value lives in a shorter-lived rabbit region than its destination — it will be gone when the rabbit ends",
            null, line,
            action,
            "Move the container inside the rabbit block, or restructure so this value does not outlive its rabbit."));
    }
}
