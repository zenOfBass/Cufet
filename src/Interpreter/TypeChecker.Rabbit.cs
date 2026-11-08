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
    /// <summary>The rabbit's object type, from the prelude — the one every rabbit binding has.</summary>
    /// <remarks>
    /// Looked up rather than cached: the registered ObjectType is REPLACED as the checker fills
    /// things in, so a held reference goes stale — the same trap the book layers have. The
    /// fallback keeps a checker running on a program assembled without the prelude (the tests
    /// that build one by hand) rather than crashing on a missing name.
    /// </remarks>
    private CufetType RabbitObjectType() =>
        _objectDefs.TryGetValue(RabbitModuleName, out var rabbitType)
            ? rabbitType
            : new ObjectType(RabbitModuleName, [], [], []);

    // The mode of every rabbit open around the code being checked, innermost last, with a task
    // body of an automatic rabbit marked as such — a task given to that rabbit from INSIDE one of
    // its own tasks would arrive after the rabbit already chose, so it is refused.
    private readonly List<(string? Mode, bool InTask)> _rabbitModes = [];
    // Where each open rabbit's own scope sits in _scopes, parallel to the list above.
    private readonly List<int> _rabbitScopes = [];

    private void CheckPullRabbit(PullRabbitStatement prs)
    {
        _rabbitModes.Add((prs.Mode, false));
        _rabbitScopes.Add(_scopes.Count);
        try { CheckPullRabbitBody(prs); }
        finally { _rabbitModes.RemoveAt(_rabbitModes.Count - 1); _rabbitScopes.RemoveAt(_rabbitScopes.Count - 1); }
    }

    private void CheckPullRabbitBody(PullRabbitStatement prs)
    {
        _rabbitDepth++;
        EnterScope();
        if (prs.Name != null)
            Scope[prs.Name] = new TypeInfo(
                RabbitObjectType(),
                new VariableReference(prs.Name, prs.Line, prs.Column),
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
            throw TypeError(
                "'Have rabbit start a task' requires an active rabbit",
                null, lts.Line, lts.Column,
                "spawning a task outside any rabbit scope",
                "Wrap the task spawn in 'Pull a rabbit. ... Done.'");

        // ★ A named rabbit has to BE one, and has to be the one that will join the task. A task is
        // joined by its rabbit's `Done.`, so naming an OUTER rabbit from an inner block would mean a
        // task outliving the block it was written in — a lifetime extension, not a spelling.
        // Refused for now rather than half-built; the rule the user set is that agents sharing work
        // share a block, and this keeps the structured-join guarantee exactly as it was.
        if (lts.RabbitName is { } named)
        {
            if (!TryLookup(named, out var info) || !IsRabbitType(info!.Type))
                throw TypeError(
                    $"'{named}' is not a rabbit",
                    "Only a rabbit can be given a task",
                    lts.Line, lts.Column,
                    $"give a task to '{named}'",
                    $"Name a rabbit pulled with 'Pull a rabbit as {named}.', or write "
                  + "'Have rabbit start a task' for the enclosing one.");

            if (info.RabbitDepth != _rabbitDepth)
                throw TypeError(
                    $"'{named}' is not the rabbit this block belongs to",
                    $"A task is joined by its rabbit's 'Done.', so '{named}' would have to outlive this block",
                    lts.Line, lts.Column,
                    $"give work to a rabbit pulled further out",
                    "Give the task to the rabbit pulled in this block, or move the work out to where "
                  + $"'{named}' was pulled.");
        }

        // ★★ A task may not CHANGE what it captured, and the refusal lives HERE so that `check`,
        // the interpreter and the compiler all give one answer. It was the code generator's alone
        // until 2026-09-19, and the three tools disagreed: MEASURED on `tally becomes tally + 5`
        // inside a task, `check` passed, the interpreter printed 5 (it hands a task body the LIVE
        // enclosing binding), and `build` refused. One program, three answers.
        //
        // ⚠ Only an OBSERVABLE write is refused — something outside the task reads the name
        // afterwards — because that is the case where the backends genuinely differ. A write
        // nothing reads still only warns, from the compiler, where saying so costs nothing:
        // refusing it would be a rule wider than its reason.
        // ★ Not for an automatic rabbit's task: it runs on the rabbit's own flow, with nothing
        // beside it, so a change to what it uses lands where everyone sees it. See CheckTaskMode.
        foreach (var captured in TaskCaptures.Of(lts.Body, n => TryLookup(n, out _)))
            if (lts.Binder == null && TaskCaptures.MayMutate(lts.Body, captured)
             && TaskCaptures.WriteIsObservable(_wholeProgram, lts.Body, captured))
                throw TypeError(
                    $"this task changes '{captured}', which it captured from outside the task",
                    "A task gets its own copy of everything it captures, because captures cross a "
                  + "thread boundary — so the change would not be visible outside, and two tasks "
                  + "changing it at once would race",
                    lts.Line, lts.Column,
                    $"change '{captured}' from inside a task",
                    $"Send the result back through a channel, or return it from a named task and "
                  + $"await it.");

        var guardType = CheckTaskMode(lts);

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
        bool markedTask = false;
        if (_rabbitModes.Count > 0 && _rabbitModes[^1].Mode == "automatic")
        {
            _rabbitModes[^1] = (_rabbitModes[^1].Mode, true);
            markedTask = true;
        }
        if (lts.Binder is { } binder && guardType != null)
            Scope[binder] = new TypeInfo(guardType, new DeliveryExpression(lts.Source!, lts.Line, lts.Column),
                                         lts.Line, false, _rabbitDepth);
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
            if (markedTask) _rabbitModes[^1] = (_rabbitModes[^1].Mode, false);
            ExitScope();
        }

        // A task that returns a value on some path must return on every path — same rule as
        // lambdas (whose result type is likewise inferred). Otherwise a task could fall off its
        // end without setting a result, and awaiting it would have no value to give back. Tasks
        // with no returns stay fire-and-forget (inferredResultType == null) and are exempt.
        if (inferredResultType != null && !DefinitelyReturns(lts.Body))
            throw TypeError(
                $"this task is inferred to give back a {FormatType(inferredResultType)}, but it can reach its end without returning one",
                null,
                lts.Line, lts.Column,
                "start a task that might not return a value",
                "Make sure every path through the task ends with a return statement — or remove the returns to make it fire-and-forget.");

        if (lts.Name != null)
            Scope[lts.Name] = new TypeInfo(
                new TaskHandleType(inferredResultType),
                new VariableReference(lts.Name, lts.Line, lts.Column),
                lts.Line);
    }

    /// <summary>
    /// The rules a rabbit's mode puts on its tasks; returns what the task's delivery is called
    /// as (`voidable T`), or null for a task that waits on nothing.
    /// </summary>
    /// <remarks>
    /// ★ An automatic rabbit runs exactly ONE of its tasks — the one whose delivery was sent first —
    /// so every task it has must say what it waits on, and no other rabbit's task may. A task that
    /// may never run cannot be named, because nothing could await it.
    /// </remarks>
    private CufetType? CheckTaskMode(LaunchTaskStatement lts)
    {
        var (mode, inTask) = _rabbitModes.Count > 0 ? _rabbitModes[^1] : (null, false);
        bool automatic = mode == "automatic";

        if (automatic && inTask)
            throw TypeError(
                "this task is given to an automatic rabbit from inside one of its own tasks",
                "An automatic rabbit chooses its one task at its 'Done.', so a task started by that task would arrive after the choice",
                lts.Line, lts.Column,
                "start a task inside a task of an automatic rabbit",
                "Pull a rabbit inside the task and give the new task to that one.");

        if (lts.Binder == null)
        {
            if (automatic)
                throw TypeError(
                    "every task of an automatic rabbit waits on a channel",
                    "An automatic rabbit runs the one task whose delivery was sent first, so a task waiting on nothing could never be chosen",
                    lts.Line, lts.Column,
                    "start a task in an automatic rabbit without saying what it waits on",
                    "Say which channel it waits on and what to call the delivery: 'Have rabbit start a task for an order in orders:'.");
            return null;
        }

        if (!automatic)
            throw TypeError(
                "only a task of an automatic rabbit waits on a channel this way",
                "'for an order in orders' says the task is one of several alternatives, and only an automatic rabbit chooses between them",
                lts.Line, lts.Column,
                $"start a task for '{lts.Binder}' in a {mode ?? "parallel"} rabbit",
                "Pull the rabbit as 'Pull an automatic rabbit.', or take the value inside the task with 'the delivery from <channel>'.");

        if (lts.Name != null)
            throw TypeError(
                $"a task of an automatic rabbit cannot be named '{lts.Name}'",
                "Only one of an automatic rabbit's tasks runs, so a name could stand for a task that never ran, with no result to await",
                lts.Line, lts.Column,
                "name a task of an automatic rabbit",
                "Leave the name off. To get a value out, send it through a channel from inside the task.");

        if (TryLookup(lts.Binder, out _))
            throw TypeError(
                $"'{lts.Binder}' already names something here",
                null, lts.Line, lts.Column,
                $"call this task's delivery '{lts.Binder}'",
                "Pick a name that is not already in use for the delivery.");

        // ★ The chosen task runs at the rabbit's `Done.`, as the rabbit's own code. So a `Return`
        // in it would leave whatever the rabbit sits in, and a name from a block that ended
        // before `Done.` — the loop that offered the task, an `If` arm — is gone by then.
        if (HasOwnReturn(lts.Body))
            throw TypeError(
                "a task of an automatic rabbit cannot return",
                "The chosen task runs as the rabbit's own code at its 'Done.', so a return would leave what the rabbit sits in",
                lts.Line, lts.Column,
                "return from a task of an automatic rabbit",
                "Let the task reach its end; use 'If' and 'Otherwise' to skip the rest.");
        int rabbitScope = _rabbitScopes.Count > 0 ? _rabbitScopes[^1] : 0;
        foreach (var used in TaskCaptures.Of(lts.Body, n => TryLookup(n, out _)))
            if (ScopeOf(used) > rabbitScope)
                throw TypeError(
                    $"'{used}' ends before this automatic rabbit chooses",
                    $"The chosen task runs at the rabbit's 'Done.', and '{used}' belongs to a block that has ended by then",
                    lts.Line, lts.Column,
                    $"use '{used}' in a task of an automatic rabbit",
                    $"Define '{used}' in the rabbit's own body, or outside the rabbit.");

        var sourceType = InferType(lts.Source!);
        if (sourceType is not ChannelType ct)
        {
            if (sourceType != null)
                throw TypeError(
                    $"a task waits on a channel, but '{lts.Binder}' would come from a {FormatType(sourceType)}",
                    null, lts.Line, lts.Column,
                    $"wait on a {FormatType(sourceType)}",
                    "The expression after 'in' must be a channel.");
            return new VoidableType(CufetType.Number);
        }
        return new VoidableType(ct.ElementType);
    }

    /// <summary>The index in _scopes of the innermost scope holding the name, or -1.</summary>
    private int ScopeOf(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].ContainsKey(name)) return i;
        return -1;
    }

    /// <summary>Whether a body returns from ITSELF — a return inside a function, a lambda, an
    /// object or another task it holds belongs to that.</summary>
    private static bool HasOwnReturn(object? node) => node switch
    {
        null or string => false,
        ReturnStatement => true,
        BindStatement or LambdaLiteral or LaunchTaskStatement or ObjectDefinition
            or GetterDeclaration or SetterDeclaration or UnmakerDeclaration or OperatorOverloadDeclaration => false,
        System.Collections.IEnumerable items => items.Cast<object?>().Any(HasOwnReturn),
        _ when node.GetType().Namespace == typeof(Program).Namespace && node is not CufetType
            => node.GetType().GetProperties().Any(p => p.GetIndexParameters().Length == 0 && HasOwnReturn(p.GetValue(node))),
        _ => false,
    };

    // ── Outward-only store invariant helpers ─────────────────────────────────

    // Series, maps, objects, and matrices are reference types tracked by rabbit depth.
    // Value types (number, text, fact, record) may be stored anywhere without constraint.
    // ★ NOTE (ESC.1): this SHALLOW, top-level test governs the existing REJECTIONS only, and is
    // deliberately left alone — the TypeChecker is shared, so widening it would reject programs the
    // interpreter runs fine. The escape ANNOTATION below uses the structural test instead.
    // ⚠ AddressType is here and deliberately NOT in IsRegionBearing below, and the two answer
    // different questions. This one decides what gets REFUSED for outliving its rabbit, and an
    // address must be: "leaving the block ends the pointer" is the whole safety claim, and without
    // this arm a rabbit-scoped handle could be inserted into a series declared outside and read
    // after `Done.` — measured, on both backends, before this line existed. IsRegionBearing asks
    // whether the compiler must deep-COPY a value into the destination's arena, and an address must
    // never be copied anywhere: it is not arena memory, it is C's.
    private static bool IsReferenceType(CufetType? t) =>
        t is SeriesType or MapType or ObjectType or MatrixType or ChannelType or AddressType;

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
        // bits is a flat value (pattern + base + width) with no pointer in it, so it carries no
        // region. Explicit rather than left to the default arm below, whose comment enumerates
        // what falls through and would otherwise have to be read as covering scalars too.
        NumberType or BitsType or FactType => false,
        VoidType                 => false,
        TextType                 => true,   // ★ arena-allocated in the compiler; the reported UAF
        SeriesType or MapType    => true,
        MatrixType or ChannelType => true,
        FunctionType             => true,   // a closure env is arena-allocated (ESC.4 territory)
        // ★ A stash IS a closure — it lowers to one before either backend sees it — so it has to
        // give the same answer as the line above. It gave `false` via the catch-all, which is only
        // unreachable because the compiler's own closure-escape rule catches the escaping program
        // downstream. Agreeing with the type it becomes is the point; relying on a later pass to
        // cover for this one is not.
        StashType                => true,
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
    // ★ TWO things hang off this annotation, and they have DIFFERENT triggers:
    //   1. deep-copying the stored VALUE into the destination's arena — only region-bearing values
    //      need it, and the compiler's EmitEscapeCopy no-ops on the rest by itself;
    //   2. redirecting the destination CONTAINER's own growth — a series/map `_ensure` realloc —
    //      into that arena, which is needed whenever the destination outlives the region we are
    //      standing in, WHATEVER its element type.
    // Gating the whole annotation on the value being region-bearing missed (2) entirely: a
    // `series of number` declared outside a rabbit and appended to inside one had its data buffer
    // reallocated into the rabbit's arena, so the series dangled after `Done.` even though every
    // element was a plain value. (Found in examples/concurrency/parallelsum.cufe.) So a non-region-bearing
    // value takes the depth of the store SITE — which is what decides (2).
    private int? EscapeDepthFor(IExpression valueExpr, CufetType? valueType, int targetDepth)
    {
        int valueDepth =
            !IsRegionBearing(valueType) ? _rabbitDepth
            : valueExpr is VariableReference vr && TryLookup(vr.Name, out var ti) ? ti.RabbitDepth
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
    private void CheckRegionStore(IExpression valueExpr, CufetType? valueType, int targetDepth, int line, int col, string action)
    {
        var valueDepth = ValueDepthOf(valueExpr, valueType);
        if (valueDepth <= targetDepth) return;
        // ⚠ The advice names no RABBIT. It used to, from before `and region` let anyone write a
        // region, and a program whose only region was one it declared was told about a rabbit it
        // had never pulled. Both fixes are measured to work.
        throw TypeError(
            "this value lives in a shorter-lived region than its destination — it will be gone when that region ends",
            null, line, col,
            action,
            "Declare the container inside that region too, so both end at its 'Done.', "
          + "or make the value before the region opens, so it lives as long as the container.");
    }
}
