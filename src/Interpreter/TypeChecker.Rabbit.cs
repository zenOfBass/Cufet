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
        try { CheckBlock(prs.Body); }
        finally { ExitScope(); _rabbitDepth--; }
    }

    // "Have rabbit start a task [as <name>]: ... Done."
    // Static checks:
    //   1. Must be inside an active rabbit (_rabbitDepth > 0) — enforced redundantly here
    //      and in the parser (parse error fires first; this is a belt-and-suspenders guard).
    //   2. Task body type-checks as a scope at the current rabbit depth. The task is
    //      structured (joins before its rabbit's Done.), so it is shorter-lived than the
    //      rabbit → no new soundness machinery needed.
    //   3. (slice 4) Return type inferred from the task body (same _inferringLambdaReturn
    //      machinery as lambdas). If the body has failure returns, _overloadBodyIsFallible
    //      is set so the inferred type is wrapped in FailureType(T). The result type is
    //      stored on a TaskHandleType bound to the task's name in the enclosing scope.
    private void CheckLaunchTask(LaunchTaskStatement lts)
    {
        if (_rabbitDepth == 0)
            throw new TypeException(FormatTypeError(
                "'Have rabbit start a task' requires an active rabbit",
                null, lts.Line,
                "spawning a task outside any rabbit scope",
                "Wrap the task spawn in 'Pull a rabbit. ... Done.'"));

        bool bodyIsFallible = HasDirectFailureReturn(lts.Body);

        var prevInFunction       = _inFunction;
        var prevExpectedReturn   = _expectedReturnType;
        var prevFunctionLine     = _functionDeclarationLine;
        var prevInferring        = _inferringLambdaReturn;
        var prevOverloadFallible = _overloadBodyIsFallible;

        _inFunction              = true;
        _expectedReturnType      = null;
        _inferringLambdaReturn   = true;
        _functionDeclarationLine = lts.Line;
        _overloadBodyIsFallible  = bodyIsFallible;

        CufetType? inferredResultType = null;
        EnterScope();
        try
        {
            CheckBlock(lts.Body);
            inferredResultType = _expectedReturnType;
        }
        finally
        {
            _inFunction              = prevInFunction;
            _expectedReturnType      = prevExpectedReturn;
            _functionDeclarationLine = prevFunctionLine;
            _inferringLambdaReturn   = prevInferring;
            _overloadBodyIsFallible  = prevOverloadFallible;
            ExitScope();
        }

        // A task that returns a value on some path must return on every path — same rule as
        // lambdas (whose result type is likewise inferred). Otherwise a task could fall off its
        // end without setting a result, and awaiting it would have no value to give back. Tasks
        // with no returns stay fire-and-forget (inferredResultType == null) and are exempt.
        if (inferredResultType != null && !DefinitelyReturns(lts.Body))
            throw new TypeException(FormatTypeError(
                $"this task is inferred to give back a {FormatType(inferredResultType)}, but it can reach its end without returning one",
                null,
                lts.Line,
                "start a task that might not return a value",
                "Make sure every path through the task ends with a return statement — or remove the returns to make it fire-and-forget."));

        if (lts.Name != null)
            Scope[lts.Name] = new TypeInfo(
                new TaskHandleType(inferredResultType),
                new VariableReference(lts.Name, lts.Line),
                lts.Line);
    }

    // ── Outward-only store invariant helpers ─────────────────────────────────

    // Series, maps, objects, and matrices are reference types tracked by rabbit depth.
    // Value types (number, text, fact, record) may be stored anywhere without constraint.
    // ★ NOTE (ESC.1): this SHALLOW, top-level test governs the existing REJECTIONS only, and is
    // deliberately left alone — the TypeChecker is shared, so widening it would reject programs the
    // interpreter runs fine. The escape ANNOTATION below uses the structural test instead.
    private static bool IsReferenceType(CufetType? t) =>
        t is SeriesType or MapType or ObjectType or MatrixType or ChannelType;

    // ── ESC.1 — the STRUCTURAL region-bearing test ───────────────────────────
    // `IsReferenceType` is a TOP-LEVEL test: it misses `text` entirely, and every value-typed
    // WRAPPER (record / voidable / failable / union) launders even a COVERED type past it — a
    // record holding a series is the proof, so "add text to the list" would not be a fix.
    //
    // A type is REGION-BEARING when its COMPILED representation holds an arena pointer anywhere in
    // its shape. This is exactly the complement of the compiler's `IsChanPod` ("transitively free of
    // arena pointers"), and it walks the SAME shape as the channel deep-copy families (record →
    // per-field, series → element, union → per-case). It lives here, in the shared project, so the
    // checker and the compiler consume ONE definition rather than two that can drift; a test locks
    // it against `IsChanPod`'s complement.
    //
    // ObjectType is treated as region-bearing unconditionally: its fields may be arena pointers, and
    // parser-produced "shell" ObjectTypes carry no field list, so reading fields here could answer
    // "no" for an object that actually holds a series. (Bare object stores are rejected by the
    // shallow test anyway, so this conservatism costs nothing observable.)
    public static bool IsRegionBearing(CufetType? t) => IsRegionBearing(t, new HashSet<string>());

    private static bool IsRegionBearing(CufetType? t, HashSet<string> seen) => t switch
    {
        null                     => false,
        NumberType or FactType   => false,
        VoidType                 => false,
        TextType                 => true,   // ★ arena-allocated in the compiler; the reported UAF
        SeriesType or MapType    => true,
        MatrixType or ChannelType => true,
        FunctionType             => true,   // a closure env is arena-allocated (ESC.4 territory)
        ObjectType               => true,   // conservative — see the note above
        RecordType rt            => rt.PositionalTypes.Any(p => IsRegionBearing(p, seen))
                                 || rt.NamedFields.Any(f => IsRegionBearing(f.Type, seen)),
        VoidableType vt          => IsRegionBearing(vt.Inner, seen),
        FailureType ft           => IsRegionBearing(ft.Inner, seen),
        // An OPEN union can hold anything ever widened into it — conservatively region-bearing.
        UnionType ut             => ut.Cases == null || ut.Cases.Any(c => IsRegionBearing(c, seen)),
        _                        => false,  // interface / task handle / rabbit / book — no payload
    };

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

    // ── ESC.1 — escape ANNOTATION (no rejections) ────────────────────────────
    // Same depth arithmetic as ValueDepthOf, but gated on the STRUCTURAL region test so it also
    // sees text and region-bearing wrappers. Returns the destination depth when the value would
    // outlive its own region (valueDepth > targetDepth) — i.e. when the compiler must copy it into
    // the destination's arena — and null otherwise (no escape, no copy: keeps copying TIGHT).
    private int? EscapeDepthFor(IExpression valueExpr, CufetType? valueType, int targetDepth)
    {
        if (!IsRegionBearing(valueType)) return null;
        int valueDepth =
            valueExpr is VariableReference vr && TryLookup(vr.Name, out var ti) ? ti.RabbitDepth
            : valueExpr is CastExpression cast && _castDepthCache.TryGetValue(cast, out var cd) ? cd
            : valueExpr is PossessiveAccess poss && _possessiveDepthCache.TryGetValue(poss, out var pd) ? pd
            : valueExpr is RecordNamedAccess rna && _rnaDepthCache.TryGetValue(rna, out var rd) ? rd
            // Anything else (a literal, a concatenation, a conversion, …) is BORN HERE, so it
            // belongs to the region currently open.
            : _rabbitDepth;
        return valueDepth > targetDepth ? targetDepth : null;
    }

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
