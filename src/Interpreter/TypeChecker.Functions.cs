namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Return-depth signature inference ─────────────────────────────────────────
    // Determines which parameter indices (by position) "flow to" the return value's depth.
    // Called inside CheckBind's try-block so callee signatures are already available.
    // Returns [] when no params contribute (fresh alloc / scalar / global returns).
    // Falls back to "all reference-type param indices" when the exact flow can't be determined
    // (recursive call found before self's signature is set, or unknown callee).
    // includeReceiver = true for method bodies: adds 'one' → ReceiverDepthIndex to paramIdx
    // so that 'return one's field' correctly taint-propagates the receiver's depth.
    private IReadOnlyList<int> ComputeReturnDepthSignature(BindStatement bind, bool includeReceiver = false)
    {
        // Build param-name → index and reference-type-param-index set.
        var paramIdx     = new Dictionary<string, int>(StringComparer.Ordinal);
        var refParamIdxs = new HashSet<int>();
        for (int i = 0; i < bind.Parameters.Count; i++)
        {
            paramIdx[bind.Parameters[i].Name] = i;
            if (IsReferenceType(ResolveParamType(bind.Parameters[i].Type)))
                refParamIdxs.Add(i);
        }
        if (includeReceiver)
        {
            // 'one' is the receiver — always a reference type (ObjectType).
            paramIdx["one"] = ReceiverDepthIndex;
            refParamIdxs.Add(ReceiverDepthIndex);
        }

        var localDepths = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var result      = new HashSet<int>();
        WalkBodyForReturnDepths(bind.Body, paramIdx, refParamIdxs, localDepths, result);
        return result.OrderBy(x => x).ToList();
    }

    // Getter variant: no explicit params, only the receiver ('one') as a depth source.
    private IReadOnlyList<int> ComputeGetterReturnDepthSignature(GetterDeclaration getter)
    {
        var paramIdx     = new Dictionary<string, int>(StringComparer.Ordinal) { ["one"] = ReceiverDepthIndex };
        var refParamIdxs = new HashSet<int> { ReceiverDepthIndex };
        var localDepths  = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var result       = new HashSet<int>();
        WalkBodyForReturnDepths(getter.Body, paramIdx, refParamIdxs, localDepths, result);
        return result.OrderBy(x => x).ToList();
    }

    private void WalkBodyForReturnDepths(
        IReadOnlyList<IStatement> body,
        Dictionary<string, int>       paramIdx,
        HashSet<int>                  refParamIdxs,
        Dictionary<string, HashSet<int>> localDepths,
        HashSet<int>                  result)
    {
        foreach (var stmt in body)
        {
            switch (stmt)
            {
                case DefineStatement def:
                    localDepths[def.Name] =
                        SymbolicExprDepth(def.Value, paramIdx, refParamIdxs, localDepths);
                    break;

                case BecomesStatement becomes:
                    var bd = SymbolicExprDepth(becomes.Value, paramIdx, refParamIdxs, localDepths);
                    if (localDepths.TryGetValue(becomes.Name, out var prev))
                        foreach (var i in bd) prev.Add(i);
                    else
                        localDepths[becomes.Name] = bd;
                    break;

                case ReturnStatement { Value: not null } ret:
                    foreach (var i in SymbolicExprDepth(ret.Value, paramIdx, refParamIdxs, localDepths))
                        result.Add(i);
                    break;

                case IfStatement ifStmt:
                    foreach (var arm in ifStmt.Arms)
                        WalkBodyForReturnDepths(arm.Body, paramIdx, refParamIdxs, localDepths, result);
                    if (ifStmt.ElseBody != null)
                        WalkBodyForReturnDepths(ifStmt.ElseBody, paramIdx, refParamIdxs, localDepths, result);
                    break;

                case WhileStatement ws:
                    WalkBodyForReturnDepths(ws.Body, paramIdx, refParamIdxs, localDepths, result);
                    break;

                case RepeatUntilStatement rus:
                    WalkBodyForReturnDepths(rus.Body, paramIdx, refParamIdxs, localDepths, result);
                    break;

                case ForEachStatement forEach:
                    // Iterator depth flows from the series — tracks elements of a rabbit-scoped series.
                    var iterName = forEach.IteratorName ?? "it";
                    var iterDepth = SymbolicExprDepth(forEach.Series, paramIdx, refParamIdxs, localDepths);
                    localDepths.TryGetValue(iterName, out var prevIter);
                    localDepths[iterName] = iterDepth;
                    WalkBodyForReturnDepths(forEach.Body, paramIdx, refParamIdxs, localDepths, result);
                    if (prevIter != null) localDepths[iterName] = prevIter;
                    else localDepths.Remove(iterName);
                    break;

                case PullRabbitStatement prs:
                    WalkBodyForReturnDepths(prs.Body, paramIdx, refParamIdxs, localDepths, result);
                    break;

                case TryStatement ts:
                    WalkBodyForReturnDepths(ts.Body, paramIdx, refParamIdxs, localDepths, result);
                    if (ts.FailureHandler != null)
                        WalkBodyForReturnDepths(ts.FailureHandler, paramIdx, refParamIdxs, localDepths, result);
                    if (ts.ExceptionHandler != null)
                        WalkBodyForReturnDepths(ts.ExceptionHandler, paramIdx, refParamIdxs, localDepths, result);
                    break;

                // All other statements (stores, state, etc.) don't define variables or return.
            }
        }
    }

    // Returns the set of caller-parameter indices whose depth flows into this expression.
    // Empty set = expression is always depth-0 (fresh allocation, scalar, or global).
    private HashSet<int> SymbolicExprDepth(
        IExpression                      expr,
        Dictionary<string, int>          paramIdx,
        HashSet<int>                     refParamIdxs,
        Dictionary<string, HashSet<int>> localDepths)
    {
        switch (expr)
        {
            case VariableReference vr:
                if (paramIdx.TryGetValue(vr.Name, out var pi) && refParamIdxs.Contains(pi))
                    return [pi];
                if (localDepths.TryGetValue(vr.Name, out var ld))
                    return new HashSet<int>(ld);   // copy to avoid aliasing
                return [];   // global variable or value-type param

            case CastExpression cast:
                // Look up the callee's already-computed ReturnDepthSignature.
                // For recursive calls the self-signature is still null (computed after this walk),
                // so the fallback fires — sound (conservative/over-strict).
                if (cast.Function is VariableReference funcVr
                    && TryLookup(funcVr.Name, out var callTi)
                    && callTi!.Type is FunctionType callFt)
                {
                    if (callFt.ReturnDepthSignature == null)
                        return new HashSet<int>(refParamIdxs);  // unknown / recursive — conservative

                    var r = new HashSet<int>();
                    foreach (var cpIdx in callFt.ReturnDepthSignature)
                    {
                        if (cpIdx >= 0 && cpIdx < cast.Args.Count)
                            r.UnionWith(SymbolicExprDepth(cast.Args[cpIdx], paramIdx, refParamIdxs, localDepths));
                    }
                    return r;
                }
                // Unknown callee (method dispatch, unresolved name) — conservative.
                return new HashSet<int>(refParamIdxs);

            case SeriesAccess sa:
                // Element depth = series depth (elements are references inside the series).
                return SymbolicExprDepth(sa.Target, paramIdx, refParamIdxs, localDepths);

            case MapLookup ml:
                // Value depth = map depth.
                return SymbolicExprDepth(ml.Map, paramIdx, refParamIdxs, localDepths);

            case PossessiveAccess pa:
                // Field depth = object depth (conservative — the field might be value-typed,
                // but we can't check that here without full type resolution).
                return SymbolicExprDepth(pa.Target, paramIdx, refParamIdxs, localDepths);

            case SeriesLiteral or MapLiteral or ObjectLiteral or MatrixLiteral or MatrixSized or RangeExpression:
                // Fresh allocations: born at current depth (which at definition time is 0).
                return [];

            default:
                // Conservative-safe default: unknown expression → depth flows from nothing (depth 0).
                // This is the lenient direction for the analysis (may undercount contributing params
                // for rare complex expressions), but preserves soundness because missed flows
                // produce false-negatives in the signature → depth-0 return → checked at call site
                // only via the direct CheckRegionStore path.
                return [];
        }
    }

    // Validates a named constructor ('Bind making a <type> to <name>, given (...): ...').
    // Resolves the return type to the canonical ObjectType instance, then delegates to CheckBind.
    private void CheckConstructor(BindStatement ctor)
    {
        if (ctor.UntoType != null)
            throw TypeError(
                $"a constructor can't also be an 'unto' method",
                null, ctor.Line, ctor.Column,
                $"declare 'Bind making a {ctor.ConstructsTypeName} to {ctor.Name} unto ...'",
                "Constructors are free functions — they can't be attached to a type with 'unto'.");

        if (!_objectDefs.TryGetValue(ctor.ConstructsTypeName!, out var objType))
            throw TypeError(
                $"'{ctor.ConstructsTypeName}' is not a defined object type",
                null, ctor.Line, ctor.Column,
                $"declare a constructor for '{ctor.ConstructsTypeName}'",
                $"Define 'object {ctor.ConstructsTypeName}' before declaring constructors for it.");

        // Resolve the shell ObjectType in the return type to the canonical instance before
        // type-checking the body — otherwise IsAssignable against returned object literals fails.
        var resolvedReturn = ctor.ReturnType is FailureType
            ? (CufetType)new FailureType(objType)
            : objType;
        CheckBind(ctor with { ReturnType = resolvedReturn });
    }

    private void CheckBind(BindStatement bind)
    {
        var saved     = SaveScopes();
        bool isNested = _inFunction; // true when we're already inside a function (closure case)
        if (isNested)
        {
            // Nested function body sees the full enclosing scope so captured variables type-check.
            // Reference-type parameters from outer scopes are upgraded to CapturedParameterDepth:
            // they were registered at RabbitDepth=0 (the outer function's perspective), but callers
            // may pass rabbit-allocated values (depth N > 0). Treating them as maximally deep causes
            // CheckRegionStore to reject any outward store — the capture-store soundness hole.
            foreach (var scope in saved.V)
                foreach (var (k, v) in scope)
                    Scope[k] = v.IsParameter && IsReferenceType(v.Type)
                        ? v with { RabbitDepth = CapturedParameterDepth }
                        : v;
        }
        else
        {
            // Top-level function body: function signatures, plus top-level CONSTANTS. See
            // ImportTopLevelVisible — the rule and the reason for it live there, once.
            ImportTopLevelVisible(saved);
        }
        foreach (var (type, name) in bind.Parameters)
            Scope[name] = new TypeInfo(ResolveParamType(type), new VariableReference(name, 0, 0), bind.Line, IsParameter: true);

        var prevInFunction        = _inFunction;
        var prevReturnType        = _expectedReturnType;
        var prevFunctionLine      = _functionDeclarationLine;
        var prevRabbitDepth       = _rabbitDepth;
        var prevHidden            = _hiddenTopLevelData;
        var prevRecordingStash    = _recordingStashFn;
        // Only this body's own locals belong to this body's state machine. A nested Bind sets its
        // own name here (or clears it), so its locals never leak into the enclosing slot table.
        _recordingStashFn         = bind.UntoType == null && _buryingFunctions.Contains(bind.Name)
                                        ? bind.Name : null;
        _inFunction               = true;
        _expectedReturnType       = bind.ReturnType;
        _functionDeclarationLine  = bind.Line;
        _rabbitDepth              = 0; // function bodies start outside any rabbit region

        try
        {
            CheckBlock(bind.Body);

            // Compute return-depth signature so call sites can propagate rabbit depth through
            // function calls instead of treating every return as depth-0.
            // The FunctionType for this binding is in the current scope (it was imported from
            // the outer scope for top-level functions, or registered just before CheckBind for
            // nested functions).  Mutating it here propagates to the saved outer scope because
            // FunctionType is a reference type — after RestoreScopes the caller sees the update.
            var effectiveRetType = bind.ReturnType is FailureType frt0 ? frt0.Inner : bind.ReturnType;
            if (IsReferenceType(effectiveRetType)
                && TryLookup(bind.Name, out var selfTi)
                && selfTi!.Type is FunctionType selfFt)
            {
                selfFt.ReturnDepthSignature = ComputeReturnDepthSignature(bind);
            }
        }
        finally
        {
            _inFunction               = prevInFunction;
            _expectedReturnType       = prevReturnType;
            _functionDeclarationLine  = prevFunctionLine;
            _rabbitDepth              = prevRabbitDepth;
            _hiddenTopLevelData       = prevHidden;
            _recordingStashFn         = prevRecordingStash;
            RestoreScopes(saved);
        }

        // ★ A burying function is exempt, because reaching its end is exactly how it finishes. Its
        // declared type is what it BURIES, not what it returns — the caller gets a stash, and the
        // stash reports "spent" by handing back void. Requiring a terminal `Return` here would be
        // demanding an answer to a question the form does not ask.
        if (bind.ReturnType != null && !_buryingFunctions.Contains(bind.Name) && !DefinitelyReturns(bind.Body))
            throw TypeError(
                $"'{bind.Name}' is declared to give back a {FormatType(bind.ReturnType)}, but it can reach its end without returning one",
                null,
                bind.Line, bind.Column,
                "define a function that might not return a value",
                "Make sure every path through the function ends with a return statement.");
    }

    // Infers and type-checks a lambda literal in one pass.
    // _inferringLambdaReturn = true causes CheckReturn to set _expectedReturnType on the
    // first return encountered (rather than validating), so locals defined before the
    // first return are already in _env when the type is determined.
    // Subsequent returns validate against the inferred type normally.
    private FunctionType InferLambdaLiteral(LambdaLiteral lambda)
    {
        var saved = SaveScopes();

        // ⚠ Deliberately NOT ImportTopLevelVisible: a lambda literal is not a detached body. It
        // CAPTURES its enclosing scope, so nothing is hidden from it — recording hidden names here
        // rejected `Define f as a function: Return the number of nums. Done.` sitting right beside
        // the `nums` it closes over.
        //
        // ⚠⚠ The WHOLE enclosing scope, at every nesting level. A lambda inside a function already
        // took it; a lambda at the top level took only functions and constants and left ordinary
        // locals "to the capture machinery" — which meant to the DEFERRAL, back when an unresolved
        // name in a body was allowed to be found at run time. It no longer is, so the same lambda
        // beside the same `nums` has to see it here. Capturing a name the checker cannot see was
        // always the odd half of this: what a closure captures is exactly what is lexically in
        // scope, which is what makes it a closure rather than a lookup.
        //
        // ★ A reference-typed PARAMETER is re-depthed on the way in: a captured parameter is not
        // owned by this frame's rabbit, so it is marked as coming from outside it (ESC.4).
        foreach (var scope in saved.V)
            foreach (var (k, v) in scope)
                Scope[k] = v.IsParameter && IsReferenceType(v.Type)
                    ? v with { RabbitDepth = CapturedParameterDepth }
                    : v;
        foreach (var (type, name) in lambda.Parameters)
            Scope[name] = new TypeInfo(ResolveParamType(type), new VariableReference(name, 0, 0), lambda.Line, IsParameter: true);

        var prevInFunction        = _inFunction;
        var prevReturnType        = _expectedReturnType;
        var prevFunctionLine      = _functionDeclarationLine;
        var prevInferring         = _inferringLambdaReturn;
        var prevRabbitDepth       = _rabbitDepth;
        var prevOverloadFallible  = _overloadBodyIsFallible;
        var prevHidden            = _hiddenTopLevelData;
        var prevRecordingStash    = _recordingStashFn;
        _recordingStashFn         = null; // a lambda's locals are its own, not the enclosing machine's
        _inFunction               = true;
        _expectedReturnType       = null; // set by first Return via CheckReturn
        _functionDeclarationLine  = lambda.Line;
        _inferringLambdaReturn    = true;
        _rabbitDepth              = 0; // lambda bodies start outside any rabbit region
        _overloadBodyIsFallible   = false; // nested lambdas are standalone, not part of the overload

        CufetType? inferredReturn = null;
        try
        {
            CheckBlock(lambda.Body);
        }
        finally
        {
            _inFunction              = prevInFunction;
            _functionDeclarationLine = prevFunctionLine;
            _inferringLambdaReturn   = prevInferring;
            inferredReturn           = _expectedReturnType; // capture before restoring
            _expectedReturnType      = prevReturnType;
            _rabbitDepth             = prevRabbitDepth;
            _overloadBodyIsFallible  = prevOverloadFallible;
            _hiddenTopLevelData      = prevHidden;
            _recordingStashFn        = prevRecordingStash;
            RestoreScopes(saved);
        }

        if (inferredReturn != null && !DefinitelyReturns(lambda.Body))
            throw TypeError(
                $"this lambda is inferred to give back a {FormatType(inferredReturn)}, but it can reach its end without returning one",
                null,
                lambda.Line, lambda.Column,
                "write a lambda that might not return a value",
                "Make sure every path through the lambda ends with a return statement.");

        var paramTypes = lambda.Parameters.Select(p => (CufetType)ResolveParamType(p.Type)).ToList();
        return new FunctionType(paramTypes, inferredReturn);
    }

    // Validates arg count and types against a resolved FunctionType.
    private void ValidateCastArgs(
        FunctionType funcType, string displayName, int declLine,
        IReadOnlyList<IExpression> args, int callLine, int callCol)
    {
        if (args.Count != funcType.ParameterTypes.Count)
            throw TypeError(
                $"{displayName} expects {funcType.ParameterTypes.Count} argument(s), but you passed {args.Count}",
                $"You declared it on line {declLine} with {funcType.ParameterTypes.Count} parameter(s)",
                callLine, callCol,
                $"call it with {args.Count} argument(s)",
                args.Count < funcType.ParameterTypes.Count
                    ? "Add the missing argument(s)."
                    : "Remove the extra argument(s).");

        for (int i = 0; i < args.Count; i++)
        {
            var argType  = InferType(args[i]);
            if (argType == null) continue;

            // Resolve shell ObjectType params to InterfaceType or full ObjectType as needed.
            var formalType = ResolveParamType(funcType.ParameterTypes[i]);

            if (formalType is InterfaceType ifaceT)
            {
                // Conformance check: argument must be an object type that conforms to the interface.
                if (argType is not ObjectType actualOt ||
                    !_objectDefs.TryGetValue(actualOt.Name, out var actualObjDef) ||
                    !actualObjDef.ConformedInterfaces.Contains(ifaceT.Name))
                {
                    var hint = argType is ObjectType nonConforming
                        ? $"'{nonConforming.Name}' does not declare conformance to '{ifaceT.Name}'. Add 'and {ifaceT.Name}' to its definition."
                        : $"Only objects that conform to '{ifaceT.Name}' can be passed here.";
                    throw TypeError(
                        $"argument {i + 1} of {displayName} must satisfy the '{ifaceT.Name}' interface, but you passed a {FormatType(argType)}",
                        $"You declared {displayName} on line {declLine} with a '{ifaceT.Name}' parameter",
                        callLine, callCol,
                        $"pass a {FormatType(argType)} where a '{ifaceT.Name}' is required",
                        hint);
                }
                continue;
            }

            if (IsAssignable(formalType, argType)) continue;
            throw TypeError(
                $"argument {i + 1} of {displayName} must be a {FormatType(formalType)}, but you passed a {FormatType(argType)}",
                $"You declared {displayName} on line {declLine}, so argument {i + 1} must be a {FormatType(formalType)}",
                callLine, callCol,
                $"pass a {FormatType(argType)} as argument {i + 1}",
                $"Change argument {i + 1} to a {FormatType(formalType)}.");
        }
    }

    private CufetType? InferCastExpr(CastExpression cast)
    {
        // ★ An axiom call reached from anywhere that does NOT declare a type. The two places that
        // do — a `Define` with one written, and a `Return` in a typed function — intercept before
        // this and never arrive here. Everything else has nothing to decide the result from, so it
        // is refused rather than guessed: `State cast open-file on (p, f).` used to check clean and
        // then have no answer to give either backend.
        if (cast.RunsAxiom is null && AxiomCalledBy(cast.Function) is { } axiom)
            return RunAxiomOnCast(cast, axiom, expected: null);

        // A function that left blanks is filled from THIS call's arguments before anything is
        // resolved — the filling is what decides which body the call reaches.
        if (cast.Function is VariableReference gvr && _genericFunctions.ContainsKey(gvr.Name))
            cast.ResolvedFunctionName = InstantiateFunction(gvr.Name, cast.Args, cast.Line, cast.Column);
        // ⚠ Never overwritten once set. Check re-enters itself on a spliced program where the
        // template is GONE and the fillings are ordinary methods, so this returns null there — and
        // assigning that would wipe the answer the first pass worked out. The side channel is the
        // only thing carrying `unique of number` across the two passes.
        else if (cast.ResolvedFunctionName is null
                 && cast.Function is PossessiveAccess gpa
                 && MemberOwnerType(InferType(gpa.Target)) is ObjectType gowner)
            cast.ResolvedFunctionName = InstantiateMethod(gowner, gpa.Member, cast.Args, cast.Line, cast.Column);

        var (funcType, displayName, declLine, argsToValidate) =
            ResolveForCast(cast.Function, cast.Args, cast.Line, cast.Column, cast.ResolvedFunctionName);
        if (funcType == null) return null;

        ValidateCastArgs(funcType, displayName, declLine, argsToValidate, cast.Line, cast.Column);

        if (funcType.ReturnType == null)
            throw TypeError(
                $"{displayName} gives nothing back — it can't be used as a value",
                $"You declared it as void on line {declLine}",
                cast.Line, cast.Column,
                "use its result as a value",
                "Cast it as a statement instead, or change its return type if you need a result.");

        // Determine the receiver for depth tracking:
        //   TryMethodDispatch consumed the receiver from args → receiver is cast.Args[0].
        //   Possessive-form method call (Cast alice's greet on (...)) → receiver is the PossessiveAccess target.
        //   Free-function call → no receiver.
        IExpression? receiverExpr = null;
        if (argsToValidate.Count < cast.Args.Count)
            receiverExpr = cast.Args[0];
        else if (cast.Function is PossessiveAccess methodPa)
            receiverExpr = methodPa.Target;

        // Inside a Try block, if control reaches the next line after a fallible call,
        // the failure branch was not taken — unwrap FailureType(T) to T automatically.
        if (_inTryBlock && funcType.ReturnType is FailureType frt)
        {
            PopulateCastDepthCache(cast, frt.Inner, funcType, argsToValidate, receiverExpr);
            return frt.Inner;
        }

        if (funcType.ReturnType is FailureType && !_inFailureHandledContext)
            throw TypeError(
                $"{displayName} can fail — you must handle the failure",
                null, cast.Line, cast.Column,
                "use a fallible function's result without handling the failure",
                "Wrap the call in a 'Try to: / In case of failure:' block, use 'but on failure <default>', or use 'or pass the failure off'.");

        PopulateCastDepthCache(cast, funcType.ReturnType, funcType, argsToValidate, receiverExpr);
        return funcType.ReturnType;
    }

    // Computes and caches the concrete rabbit depth of a CastExpression's return value.
    // Called right before returning from InferCastExpr so nested casts are already cached.
    //   sig == null  → unanalyzed (shouldn't happen for methods now); depth 0 as fallback.
    //   sig == []    → return is always depth-0 (fresh/global); depth 0.
    //   sig == [i,…] → max depth of contributing args; ReceiverDepthIndex (-1) → receiver depth.
    // receiverExpr is the object the method was called on (null for free-function calls).
    private void PopulateCastDepthCache(
        CastExpression             cast,
        CufetType?                 effectiveRetType,
        FunctionType               funcType,
        IReadOnlyList<IExpression> argsToValidate,
        IExpression?               receiverExpr = null)
    {
        if (!IsReferenceType(effectiveRetType)) return;

        var sig = funcType.ReturnDepthSignature;
        int retDepth = 0;

        if (sig != null)
        {
            foreach (var pIdx in sig)
            {
                if (pIdx == ReceiverDepthIndex)
                {
                    if (receiverExpr != null)
                        retDepth = Math.Max(retDepth, ValueDepthOf(receiverExpr, InferType(receiverExpr)));
                }
                else if (pIdx >= 0 && pIdx < argsToValidate.Count)
                {
                    var argType = InferType(argsToValidate[pIdx]);
                    retDepth = Math.Max(retDepth, ValueDepthOf(argsToValidate[pIdx], argType));
                }
            }
        }

        _castDepthCache[cast] = retDepth;
    }

    // Resolves the function expression to (funcType, displayName, declLine, argsToValidate).
    // When method dispatch is detected, argsToValidate is args[1..] (receiver already consumed).
    // Returns (null, ...) if the type is unknown at compile time — runtime catches it.
    // Throws TypeException for known-bad: non-function type, or method/free-function ambiguity.
    private (FunctionType? funcType, string displayName, int declLine, IReadOnlyList<IExpression> argsToValidate)
        ResolveForCast(IExpression funcExpr, IReadOnlyList<IExpression> args, int callLine, int callCol,
                       string? resolvedName = null)
    {
        if (funcExpr is VariableReference vr)
        {
            // A filled-in function is registered under its filling (`unique of text`); the name
            // written at the call site is the template's, which names no single body.
            string called = resolvedName ?? vr.Name;
            var md    = TryMethodDispatch(called, args, callLine, callCol);
            bool inEnv = TryLookup(called, out var info);

            if (md.HasValue && inEnv && info!.Type is FunctionType)
                throw TypeError(
                    $"'{vr.Name}' is both a method and a free function — this is ambiguous",
                    null,
                    callLine, callCol,
                    $"call '{vr.Name}' ambiguously",
                    $"Use the possessive form to call the method explicitly: Cast <object>'s {vr.Name} on (args).");

            if (md.HasValue)
                return (md.Value.funcType, md.Value.displayName, md.Value.declLine, args.Skip(1).ToList());

            if (inEnv)
            {
                if (info!.Type is not FunctionType ft)
                    throw TypeError(
                        $"'{vr.Name}' holds a {FormatType(info.Type)}, not a function — you can only cast functions",
                        null,
                        callLine, callCol,
                        "cast something that isn't a function",
                        "Only functions can be cast. Make sure the name you're casting refers to a function.");
                return (ft, $"'{vr.Name}'", info.EstablishingLine, args);
            }

            // Not in env and not a method.
            // If first arg is an object/interface, this must be an attempted method call — error now.
            if (args.Count > 0)
            {
                var firstArgType = InferType(args[0]);
                if (firstArgType is ObjectType ot2)
                {
                    var avail = ot2.Methods.Count > 0
                        ? $"Available methods: {string.Join(", ", ot2.Methods.Select(m => $"'{m.MethodName}'"))}."
                        : $"'{ot2.Name}' has no methods.";
                    throw TypeError(
                        $"'{ot2.Name}' has no method named '{vr.Name}'",
                        null, callLine, callCol,
                        $"call method '{vr.Name}' on a {ot2.Name}",
                        avail);
                }
                if (firstArgType is InterfaceType ifaceT2 &&
                    _interfaceDefs.TryGetValue(ifaceT2.Name, out var ifaceDef2))
                {
                    var avail = ifaceDef2.Methods.Count > 0
                        ? $"Available methods: {string.Join(", ", ifaceDef2.Methods.Select(m => $"'{m.MethodName}'"))}."
                        : $"Interface '{ifaceT2.Name}' declares no methods.";
                    throw TypeError(
                        $"interface '{ifaceT2.Name}' has no method named '{vr.Name}'",
                        null, callLine, callCol,
                        $"call method '{vr.Name}' through interface '{ifaceT2.Name}'",
                        avail);
                }
            }

            // Unknown identifier with non-object first arg (or no args) — runtime catches it.
            return (null, $"'{vr.Name}'", callLine, args);
        }

        // General path: PossessiveAccess → FunctionType for method ref, etc.
        // A filled-in METHOD is registered on its type under the filling (`unique of number`), so
        // the member looked up here is that one and not the template's name.
        var lookedUp = resolvedName is not null && funcExpr is PossessiveAccess mpa
            ? new PossessiveAccess(mpa.Target, resolvedName, mpa.Line, mpa.Column)
            : funcExpr;
        var exprType = InferType(lookedUp);
        if (exprType == null) return (null, "this function", callLine, args);
        if (exprType is not FunctionType funcType)
            throw TypeError(
                $"this expression holds a {FormatType(exprType)}, not a function — you can only cast functions",
                null,
                callLine, callCol,
                "cast something that isn't a function",
                "Only functions can be cast.");
        return (funcType, "this function", callLine, args);
    }

    // Returns method's FunctionType (params only, no receiver) and display info when the
    // first arg's type is an object or interface that declares a method with the given name.
    // Returns null if no such method is found.
    private (FunctionType funcType, string displayName, int declLine)? TryMethodDispatch(
        string name, IReadOnlyList<IExpression> args, int callLine, int callCol)
    {
        if (args.Count == 0) return null;

        var firstArgType = InferType(args[0]);
        if (firstArgType == null) return null;

        if (firstArgType is ObjectType ot)
        {
            var sig = FindMethodInOtOrPromoted(ot, name);
            if (sig == null) return null;
            return (sig, $"method '{name}' on '{ot.Name}'", callLine);
        }

        if (firstArgType is InterfaceType ifaceT &&
            _interfaceDefs.TryGetValue(ifaceT.Name, out var ifaceDef))
        {
            var ifaceSig = ifaceDef.Methods.FirstOrDefault(m => m.MethodName == name);
            if (ifaceSig == default) return null;
            return (new FunctionType(ifaceSig.ParamTypes, ifaceSig.ReturnType),
                    $"method '{name}' on interface '{ifaceT.Name}'", callLine);
        }

        return null;
    }

    // Resolves CufetType references throughout the type system:
    //   - ObjectType shells (parser-produced placeholders) → full registered type or InterfaceType
    //   - Compound types (SeriesType, VoidableType, etc.) → recursively resolved inner types
    //   - Book-introduced types not yet in scope (e.g. matrix before Pull) → left as-is (soft)
    //   - Genuinely unknown type names → TypeException with a clear message (TR.3)
    // Called both from Pass2ResolveTypes (eager, no _typeScopes) and from InferType (at inference
    // time, when _typeScopes may contain pulled book types).
    /// <summary>
    /// Works out what a call fills a function's blanks with, and builds that filling.
    /// </summary>
    /// <remarks>
    /// ★★ The first real INFERENCE in the language, and the reason the ROADMAP warns about generic
    /// error quality: an object states its filling outright (`a stack of number`) while a function's
    /// is read off its arguments. It is kept as shallow as inference can be — one structural match
    /// per argument, no unification variables, no ordering, no backtracking. A blank either matches
    /// the same type everywhere it appears or the call is refused by name.
    /// </remarks>
    private string InstantiateFunction(
        string name, IReadOnlyList<IExpression> args, int line, int column)
    {
        var (template, blankNames) = _genericFunctions[name];
        var blanks = new HashSet<string>(blankNames, StringComparer.Ordinal);
        var found  = new Dictionary<string, CufetType>(StringComparer.Ordinal);

        int shared = Math.Min(args.Count, template.Parameters.Count);
        for (int i = 0; i < shared; i++)
        {
            var actual = InferType(args[i]);
            if (actual != null && !Unify(template.Parameters[i].Type, actual, blanks, found))
                throw TypeError(
                    $"'{name}' can't take both a {FormatType(found[Disagreeing(template.Parameters[i].Type, blanks)!])} " +
                    $"and a {FormatType(actual)} for the same blank",
                    $"'{name}' uses one name in more than one place in its signature, so those places have to agree",
                    line, column,
                    $"call '{name}' with arguments that disagree",
                    "Pass values whose types line up, or write a separate function for the other shape.");
        }

        var missing = blankNames.Where(b => !found.ContainsKey(b)).ToList();
        if (missing.Count > 0)
            throw TypeError(
                $"'{name}' can't tell what '{missing[0]}' is here",
                $"'{missing[0]}' is a blank in '{name}', and nothing passed in says what fills it",
                line, column,
                $"call '{name}' without saying what '{missing[0]}' is",
                "The blank is worked out from the arguments, so it has to appear in one of them.");

        string filled = name + string.Concat(blankNames.Select(b => " of " + FormatType(found[b])));
        if (_instantiatedFunctions.ContainsKey(filled) || Scope.ContainsKey(filled)) return filled;

        var concrete = GenericInstantiation.FillFunction(template, filled, found);
        _instantiatedFunctions[filled] = concrete;
        _freeBinds[filled] = concrete;
        Scope[filled] = new TypeInfo(
            new FunctionType(concrete.Parameters.Select(p => ResolveParamType(p.Type)).ToList(),
                             concrete.ReturnType is null ? null : ResolveParamType(concrete.ReturnType)),
            new VariableReference(filled, line, column),
            concrete.Line);
        return filled;
    }

    /// <summary>
    /// Fills a METHOD's blanks from the call, and registers the filling on its owning type.
    /// </summary>
    /// <remarks>
    /// ★ Same shape as a free function's, with one addition: the filling has to become a real
    /// member of the owning ObjectType and a real method on its ObjectDefinition, because both
    /// backends dispatch by looking the member up on the type.
    ///
    /// Returns the filled-in MEMBER name (`unique of number`), or null when the member is not one
    /// that left a blank — in which case ordinary dispatch handles it.
    /// </remarks>
    private string? InstantiateMethod(
        ObjectType owner, string member, IReadOnlyList<IExpression> args, int line, int column)
    {
        if (!_genericMethods.TryGetValue((owner.Name, member), out var held)) return null;

        var (template, blankNames) = held;
        var blanks = new HashSet<string>(blankNames, StringComparer.Ordinal);
        var found  = new Dictionary<string, CufetType>(StringComparer.Ordinal);

        int shared = Math.Min(args.Count, template.Parameters.Count);
        for (int i = 0; i < shared; i++)
        {
            var actual = InferType(args[i]);
            if (actual != null && !Unify(template.Parameters[i].Type, actual, blanks, found))
                throw TypeError(
                    $"'{member}' can't take two different types for the same blank",
                    $"'{member}' uses one name in more than one place in its signature, so those places have to agree",
                    line, column,
                    $"call '{member}' with arguments that disagree",
                    "Pass values whose types line up, or write a separate method for the other shape.");
        }

        var missing = blankNames.Where(b => !found.ContainsKey(b)).ToList();
        if (missing.Count > 0)
            throw TypeError(
                $"'{member}' can't tell what '{missing[0]}' is here",
                $"'{missing[0]}' is a blank in '{member}', and nothing passed in says what fills it",
                line, column,
                $"call '{member}' without saying what '{missing[0]}' is",
                "The blank is worked out from the arguments, so it has to appear in one of them.");

        string filled = member + string.Concat(blankNames.Select(b => " of " + FormatType(found[b])));
        if (owner.Methods.Any(m => m.MethodName == filled)) return filled;

        var concrete = GenericInstantiation.FillFunction(template, filled, found);

        if (!_instantiatedMethods.TryGetValue(owner.Name, out var built))
            _instantiatedMethods[owner.Name] = built = new Dictionary<string, BindStatement>(StringComparer.Ordinal);
        built[filled] = concrete;

        var signature = new FunctionType(
            concrete.Parameters.Select(p => ResolveParamType(p.Type)).ToList(),
            concrete.ReturnType is null ? null : ResolveParamType(concrete.ReturnType));
        _objectDefs[owner.Name] = WithMethods(_objectDefs[owner.Name],
            [.. _objectDefs[owner.Name].Methods, (filled, signature)]);

        return filled;
    }

    /// <summary>Which blank in <paramref name="pattern"/> already has a value — for the message.</summary>
    private static string? Disagreeing(CufetType pattern, HashSet<string> blanks)
    {
        string? hit = null;
        AstRebuilder.SubstituteDeep(pattern, leaf =>
        {
            if (hit == null && leaf is ObjectType { TypeArguments.Count: 0 } shell && blanks.Contains(shell.Name))
                hit = shell.Name;
            return leaf;
        });
        return hit;
    }

    /// <summary>
    /// Matches a signature shape against an actual type, learning what each blank stands for.
    /// </summary>
    /// <remarks>
    /// ⚠ Answers TRUE for shapes it does not understand rather than guessing. A blank can only be
    /// learned where the two shapes line up; everywhere else the ordinary argument check is already
    /// the authority, and a second opinion here would only produce a worse-worded version of the
    /// same error.
    /// </remarks>
    private static bool Unify(
        CufetType pattern, CufetType actual, HashSet<string> blanks, Dictionary<string, CufetType> found)
    {
        if (pattern is ObjectType { TypeArguments.Count: 0 } shell && blanks.Contains(shell.Name))
        {
            if (found.TryGetValue(shell.Name, out var already)) return already.Equals(actual);
            found[shell.Name] = actual;
            return true;
        }

        return (pattern, actual) switch
        {
            (SeriesType p, SeriesType a)                 => Unify(p.ElementType, a.ElementType, blanks, found),
            // ⚠ A stash is a container like the rest of these, and leaving it out did not fail
            // loudly: the arm below answers "matched" for any pair it does not recognise, so
            // `stash of thing` matched `stash of number` and bound NOTHING — and the blank was then
            // reported as one nothing passed in could fill. `series of thing` worked the whole time,
            // which is what made it look like a rule about blanks rather than a missing case.
            (StashType p, StashType a)                   => Unify(p.ElementType, a.ElementType, blanks, found),
            (VoidableType p, VoidableType a)             => Unify(p.Inner, a.Inner, blanks, found),
            (FailureType p, FailureType a)               => Unify(p.Inner, a.Inner, blanks, found),
            (ChannelType p, ChannelType a)               => Unify(p.ElementType, a.ElementType, blanks, found),
            (ReadableStreamType p, ReadableStreamType a) => Unify(p.ElementType, a.ElementType, blanks, found),
            (WritableStreamType p, WritableStreamType a) => Unify(p.ElementType, a.ElementType, blanks, found),
            (MapType p, MapType a)                       => Unify(p.KeyType, a.KeyType, blanks, found)
                                                          & Unify(p.ValueType, a.ValueType, blanks, found),
            _ => true
        };
    }

    /// <summary>
    /// Fills a template's blanks on demand, once per distinct filling.
    /// </summary>
    /// <remarks>
    /// ★ The result is registered as an ORDINARY object under its filled-in name, so everything
    /// downstream — inference, the interpreter, the emitter — meets a concrete type and needs to
    /// know nothing about templates. The definition itself is also collected, because the compiler
    /// emits from the program's STATEMENTS rather than from the checker's tables; see Check.
    /// </remarks>
    private CufetType Instantiate(ObjectType filled)
    {
        var arguments = filled.TypeArguments.Select(ResolveParamType).ToList();
        string name   = GenericInstantiation.NameFor(filled.Name, arguments);

        if (_objectDefs.TryGetValue(name, out var already)) return already;

        if (!_genericObjectDefs.TryGetValue(filled.Name, out var template))
            throw new TypeException(
                $"That doesn't work: '{filled.Name}' does not take a filling.\n\n" +
                (_objectDefs.ContainsKey(filled.Name)
                    ? $"'{filled.Name}' is an ordinary type, so write it on its own — '{filled.Name}', not '{name}'."
                    : $"Define 'object {filled.Name} of <name>' before filling it in, or check the spelling."));

        var blanks = template.TypeParameters!;
        if (blanks.Count != arguments.Count)
            throw new TypeException(
                $"That doesn't work: '{filled.Name}' leaves {blanks.Count} blank(s) to fill, " +
                $"and {arguments.Count} were given.\n\n" +
                $"It is written 'object {filled.Name} {string.Join(" ", blanks.Select(b => "of " + b))}'.");

        var concrete = GenericInstantiation.Fill(
            template, name,
            blanks.Zip(arguments).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal));

        // Registered BEFORE its own field types are resolved, so a template that mentions itself
        // (`the voidable stack of number next`) finds the entry instead of filling forever.
        _instantiated[name] = concrete;
        _objectDefs[name] = new ObjectType(
            name, concrete.PositionalTypes, concrete.NamedFields,
            concrete.Methods.Select(m => (m.Name,
                new FunctionType(m.Parameters.Select(p => p.Type).ToList(), m.ReturnType))).ToList(),
            concrete.Getters.Select(g => (g.Name, g.ReturnType)).ToList(),
            concrete.Setters.Select(s => (s.Name, s.ParamType, s.ParamName)).ToList(),
            concrete.EmbeddedTypeName, concrete.ConformedInterfaces,
            permanentFields: concrete.PermanentFields);

        // Now resolve the filled-in field and signature types, which may fill further templates.
        var positionals = concrete.PositionalTypes.Select(ResolveParamType).ToList();
        var named       = concrete.NamedFields.Select(f => (f.FieldName, ResolveParamType(f.FieldType))).ToList();
        var registered  = _objectDefs[name];
        _objectDefs[name] = new ObjectType(
            name, positionals, named,
            registered.Methods.Select(m => (m.MethodName, (FunctionType)ResolveParamType(m.Signature))).ToList(),
            registered.Getters.Select(g => (g.GetterName, ResolveParamType(g.ReturnType))).ToList(),
            registered.Setters.Select(s => (s.SetterName, ResolveParamType(s.ParamType), s.ParamName)).ToList(),
            registered.EmbeddedTypeName, registered.ConformedInterfaces,
            permanentFields: registered.PermanentFields.ToList());

        return _objectDefs[name];
    }

    private CufetType ResolveParamType(CufetType type) => type switch
    {
        // ── Compound types: recurse into inner types ──────────────────────────
        SeriesType st           => new SeriesType(ResolveParamType(st.ElementType)),
        VoidableType vt         => new VoidableType(ResolveParamType(vt.Inner)),
        FailureType ft          => new FailureType(ResolveParamType(ft.Inner)),
        MapType mt              => new MapType(ResolveParamType(mt.KeyType), ResolveParamType(mt.ValueType)),
        MappingType mt          => new MappingType(ResolveParamType(mt.KeyType), ResolveParamType(mt.ValueType)),
        FunctionType ft         => new FunctionType(
                                    ft.ParameterTypes.Select(ResolveParamType).ToList(),
                                    ft.ReturnType is null ? null : ResolveParamType(ft.ReturnType)),
        ReadableStreamType rst  => new ReadableStreamType(ResolveParamType(rst.ElementType)),
        WritableStreamType wst  => new WritableStreamType(ResolveParamType(wst.ElementType)),
        UnionType { Cases: { } cases } => new UnionType(cases.Select(ResolveParamType).ToList()),
        RecordType rt           => new RecordType(
                                    rt.PositionalTypes.Select(ResolveParamType).ToList(),
                                    rt.NamedFields.Select(f => (f.Name, ResolveParamType(f.Type))).ToList()),

        // ★ An axiom type is refused wherever a type was WRITTEN — a parameter, a field, a return
        // type, an element type. This arm is what confines an axiom to the one shape the first
        // slice carries, and it confines it everywhere at once rather than at each site that would
        // otherwise have to remember. Inferring an axiom's type does not come through here (see
        // InferType), so the declaration that names one still works.
        //
        // ⚠ Refusing is not the end state — the design has axioms passed around unrun, which is what
        // lets a SQL fragment be assembled before it is used. What is missing is the backend half:
        // an axiom has no C representation, so allowing it here would type-check a program the
        // compiler cannot build, and "it checks but does not compile" is the divergence this
        // project spends the most effort refusing.
        AxiomType axiom => throw new TypeException(
            $"That doesn't work: a {axiom.Language} axiom can be declared and returned, and not yet "
          + "written down anywhere else.\n\n" +
            "An axiom runs when it is returned into a number — 'Bind number to <name>, <axiom>.'. "
          + "It cannot yet be a parameter, a field, or something a function hands back unrun."),

        // ── A FILLED template — `a stack of number` ───────────────────────────
        // Ahead of the shell cases below, which do not inspect the filling and would otherwise
        // resolve `stack of number` to the unsubstituted template.
        ObjectType { TypeArguments.Count: > 0 } filled => Instantiate(filled),

        // ── ObjectType shell resolution ───────────────────────────────────────
        ObjectType { PositionalTypes.Count: 0, NamedFields.Count: 0, Methods.Count: 0,
                    EmbeddedTypeName: null, ConformedInterfaces.Count: 0 } ot
            when _interfaceDefs.ContainsKey(ot.Name) => new InterfaceType(ot.Name),
        ObjectType { PositionalTypes.Count: 0, NamedFields.Count: 0, Methods.Count: 0,
                    EmbeddedTypeName: null, ConformedInterfaces.Count: 0 } ot
            when _objectDefs.ContainsKey(ot.Name) => _objectDefs[ot.Name],
        // `the c-language x` — a language book's name in type position is the axiom type it tags.
        // The book introduces no type of its own, so this is the one shape it can mean.
        ObjectType { PositionalTypes.Count: 0, NamedFields.Count: 0, Methods.Count: 0,
                    EmbeddedTypeName: null, ConformedInterfaces.Count: 0 } ot
            when IsLanguageBook(ot.Name) => new AxiomType(ot.Name),
        // Book-introduced types (e.g. matrix) found in the current type scope:
        ObjectType { PositionalTypes.Count: 0, NamedFields.Count: 0, Methods.Count: 0,
                    EmbeddedTypeName: null, ConformedInterfaces.Count: 0 } ot
            when TryLookupScopedType(ot.Name, out var scopedType) => scopedType,
        // Known book-introduced type name but not yet in scope (e.g. matrix before Pull):
        // leave as-is so the inference pass can surface the correct "Pull first" error.
        ObjectType { PositionalTypes.Count: 0, NamedFields.Count: 0, Methods.Count: 0,
                    EmbeddedTypeName: null, ConformedInterfaces.Count: 0 } ot
            when BuiltinBooks.Values.Any(b => b.IntroducedTypes.ContainsKey(ot.Name)) => type,
        // Genuinely unknown type name — not defined, not an interface, not a book type:
        ObjectType { PositionalTypes.Count: 0, NamedFields.Count: 0, Methods.Count: 0,
                    EmbeddedTypeName: null, ConformedInterfaces.Count: 0 } ot
            => throw new TypeException(
                $"That doesn't work: '{ot.Name}' is not a defined type.\n\n" +
                $"Define 'object {ot.Name}' before using it as a type name, or check the spelling."),

        // Already a concrete/fully-resolved type — nothing to do.
        _ => type
    };
}
