namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Return-depth signature inference ─────────────────────────────────────────
    // Determines which parameter indices (by position) "flow to" the return value's depth.
    // Called inside CheckBind's try-block so callee signatures are already available.
    // Returns [] when no params contribute (fresh alloc / scalar / global returns).
    // Falls back to "all reference-type param indices" when the exact flow can't be determined
    // (recursive call found before self's signature is set, or unknown callee).
    private IReadOnlyList<int> ComputeReturnDepthSignature(BindStatement bind)
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

        var localDepths = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var result      = new HashSet<int>();
        WalkBodyForReturnDepths(bind.Body, paramIdx, refParamIdxs, localDepths, result);
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
                        if (cpIdx < cast.Args.Count)
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
            throw new TypeException(FormatTypeError(
                $"a constructor can't also be an 'unto' method",
                null, ctor.Line,
                $"declare 'Bind making a {ctor.ConstructsTypeName} to {ctor.Name} unto ...'",
                "Constructors are free functions — they can't be attached to a type with 'unto'."));

        if (!_objectDefs.TryGetValue(ctor.ConstructsTypeName!, out var objType))
            throw new TypeException(FormatTypeError(
                $"'{ctor.ConstructsTypeName}' is not a defined object type",
                null, ctor.Line,
                $"declare a constructor for '{ctor.ConstructsTypeName}'",
                $"Define 'object {ctor.ConstructsTypeName}' before declaring constructors for it."));

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
            foreach (var scope in saved.V)
                foreach (var (k, v) in scope) Scope[k] = v;
        }
        else
        {
            // Top-level function body: only function signatures visible — no caller/global locals.
            foreach (var scope in saved.V)
                foreach (var (k, v) in scope.Where(kv => kv.Value.Type is FunctionType)) Scope[k] = v;
        }
        foreach (var (type, name) in bind.Parameters)
            Scope[name] = new TypeInfo(ResolveParamType(type), new VariableReference(name, 0), bind.Line);

        var prevInFunction        = _inFunction;
        var prevReturnType        = _expectedReturnType;
        var prevFunctionLine      = _functionDeclarationLine;
        var prevRabbitDepth       = _rabbitDepth;
        _inFunction               = true;
        _expectedReturnType       = bind.ReturnType;
        _functionDeclarationLine  = bind.Line;
        _rabbitDepth              = 0; // function bodies start outside any rabbit region

        try
        {
            foreach (var stmt in bind.Body)
                CheckStatement(stmt);

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
            RestoreScopes(saved);
        }

        if (bind.ReturnType != null && !DefinitelyReturns(bind.Body))
            throw new TypeException(FormatTypeError(
                $"'{bind.Name}' is declared to give back a {FormatType(bind.ReturnType)}, but it can reach its end without returning one",
                null,
                bind.Line,
                "define a function that might not return a value",
                "Make sure every path through the function ends with a return statement."));
    }

    // Infers and type-checks a lambda literal in one pass.
    // _inferringLambdaReturn = true causes CheckReturn to set _expectedReturnType on the
    // first return encountered (rather than validating), so locals defined before the
    // first return are already in _env when the type is determined.
    // Subsequent returns validate against the inferred type normally.
    private FunctionType InferLambdaLiteral(LambdaLiteral lambda)
    {
        var saved     = SaveScopes();
        bool isNested = _inFunction;
        if (isNested)
            foreach (var scope in saved.V)
                foreach (var (k, v) in scope) Scope[k] = v;
        else
            foreach (var scope in saved.V)
                foreach (var (k, v) in scope.Where(kv => kv.Value.Type is FunctionType)) Scope[k] = v;
        foreach (var (type, name) in lambda.Parameters)
            Scope[name] = new TypeInfo(ResolveParamType(type), new VariableReference(name, 0), lambda.Line);

        var prevInFunction        = _inFunction;
        var prevReturnType        = _expectedReturnType;
        var prevFunctionLine      = _functionDeclarationLine;
        var prevInferring         = _inferringLambdaReturn;
        var prevRabbitDepth       = _rabbitDepth;
        var prevOverloadFallible  = _overloadBodyIsFallible;
        _inFunction               = true;
        _expectedReturnType       = null; // set by first Return via CheckReturn
        _functionDeclarationLine  = lambda.Line;
        _inferringLambdaReturn    = true;
        _rabbitDepth              = 0; // lambda bodies start outside any rabbit region
        _overloadBodyIsFallible   = false; // nested lambdas are standalone, not part of the overload

        CufetType? inferredReturn = null;
        try
        {
            foreach (var stmt in lambda.Body)
                CheckStatement(stmt);
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
            RestoreScopes(saved);
        }

        if (inferredReturn != null && !DefinitelyReturns(lambda.Body))
            throw new TypeException(FormatTypeError(
                $"this lambda is inferred to give back a {FormatType(inferredReturn)}, but it can reach its end without returning one",
                null,
                lambda.Line,
                "write a lambda that might not return a value",
                "Make sure every path through the lambda ends with a return statement."));

        var paramTypes = lambda.Parameters.Select(p => (CufetType)ResolveParamType(p.Type)).ToList();
        return new FunctionType(paramTypes, inferredReturn);
    }

    // Validates arg count and types against a resolved FunctionType.
    private void ValidateCastArgs(
        FunctionType funcType, string displayName, int declLine,
        IReadOnlyList<IExpression> args, int callLine)
    {
        if (args.Count != funcType.ParameterTypes.Count)
            throw new TypeException(FormatTypeError(
                $"{displayName} expects {funcType.ParameterTypes.Count} argument(s), but you passed {args.Count}",
                $"You declared it on line {declLine} with {funcType.ParameterTypes.Count} parameter(s)",
                callLine,
                $"call it with {args.Count} argument(s)",
                args.Count < funcType.ParameterTypes.Count
                    ? "Add the missing argument(s)."
                    : "Remove the extra argument(s)."));

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
                    throw new TypeException(FormatTypeError(
                        $"argument {i + 1} of {displayName} must satisfy the '{ifaceT.Name}' interface, but you passed a {FormatType(argType)}",
                        $"You declared {displayName} on line {declLine} with a '{ifaceT.Name}' parameter",
                        callLine,
                        $"pass a {FormatType(argType)} where a '{ifaceT.Name}' is required",
                        hint));
                }
                continue;
            }

            if (IsAssignable(formalType, argType)) continue;
            throw new TypeException(FormatTypeError(
                $"argument {i + 1} of {displayName} must be a {FormatType(formalType)}, but you passed a {FormatType(argType)}",
                $"You declared {displayName} on line {declLine}, so argument {i + 1} must be a {FormatType(formalType)}",
                callLine,
                $"pass a {FormatType(argType)} as argument {i + 1}",
                $"Change argument {i + 1} to a {FormatType(formalType)}."));
        }
    }

    private CufetType? InferCastExpr(CastExpression cast)
    {
        // Collections aggregates (minimum/maximum/average/unique) have type-generic or
        // educational-error constraints that can't be expressed as a plain FunctionType.
        if (IsCollectionsAggregateCast(cast))
            return InferCollectionsAggregateCast(cast);

        var (funcType, displayName, declLine, argsToValidate) = ResolveForCast(cast.Function, cast.Args, cast.Line);
        if (funcType == null) return null;

        ValidateCastArgs(funcType, displayName, declLine, argsToValidate, cast.Line);

        if (funcType.ReturnType == null)
            throw new TypeException(FormatTypeError(
                $"{displayName} gives nothing back — it can't be used as a value",
                $"You declared it as void on line {declLine}",
                cast.Line,
                "use its result as a value",
                "Cast it as a statement instead, or change its return type if you need a result."));

        // Inside a Try block, if control reaches the next line after a fallible call,
        // the failure branch was not taken — unwrap FailureType(T) to T automatically.
        if (_inTryBlock && funcType.ReturnType is FailureType frt)
        {
            PopulateCastDepthCache(cast, frt.Inner, funcType, argsToValidate);
            return frt.Inner;
        }

        if (funcType.ReturnType is FailureType && !_inFailureHandledContext)
            throw new TypeException(FormatTypeError(
                $"{displayName} can fail — you must handle the failure",
                null, cast.Line,
                "use a fallible function's result without handling the failure",
                "Wrap the call in a 'Try to: / In case of failure:' block, use 'but on failure <default>', or use 'or pass the failure off'."));

        PopulateCastDepthCache(cast, funcType.ReturnType, funcType, argsToValidate);
        return funcType.ReturnType;
    }

    // Computes and caches the concrete rabbit depth of a CastExpression's return value.
    // Called right before returning from InferCastExpr so nested casts are already cached.
    //   sig == null  → method or unanalyzed function; depth 0 (backward-compatible, hole tracked separately).
    //   sig == []    → return is always depth-0 (fresh/global); depth 0.
    //   sig == [i,…] → max depth of contributing args; precise tracking.
    private void PopulateCastDepthCache(
        CastExpression           cast,
        CufetType?               effectiveRetType,
        FunctionType             funcType,
        IReadOnlyList<IExpression> argsToValidate)
    {
        if (!IsReferenceType(effectiveRetType)) return;

        var sig = funcType.ReturnDepthSignature;
        int retDepth = 0;

        if (sig != null)
        {
            foreach (var paramIdx in sig)
            {
                if (paramIdx >= argsToValidate.Count) continue;
                var argType = InferType(argsToValidate[paramIdx]);
                retDepth = Math.Max(retDepth, ValueDepthOf(argsToValidate[paramIdx], argType));
            }
        }
        // sig == null: backward-compat depth-0 (method hole remains open, tracked in design notes)

        _castDepthCache[cast] = retDepth;
    }

    // Resolves the function expression to (funcType, displayName, declLine, argsToValidate).
    // When method dispatch is detected, argsToValidate is args[1..] (receiver already consumed).
    // Returns (null, ...) if the type is unknown at compile time — runtime catches it.
    // Throws TypeException for known-bad: non-function type, or method/free-function ambiguity.
    private (FunctionType? funcType, string displayName, int declLine, IReadOnlyList<IExpression> argsToValidate)
        ResolveForCast(IExpression funcExpr, IReadOnlyList<IExpression> args, int callLine)
    {
        if (funcExpr is VariableReference vr)
        {
            var md    = TryMethodDispatch(vr.Name, args, callLine);
            bool inEnv = TryLookup(vr.Name, out var info);

            if (md.HasValue && inEnv && info!.Type is FunctionType)
                throw new TypeException(FormatTypeError(
                    $"'{vr.Name}' is both a method and a free function — this is ambiguous",
                    null,
                    callLine,
                    $"call '{vr.Name}' ambiguously",
                    $"Use the possessive form to call the method explicitly: Cast <object>'s {vr.Name} on (args)."));

            if (md.HasValue)
                return (md.Value.funcType, md.Value.displayName, md.Value.declLine, args.Skip(1).ToList());

            if (inEnv)
            {
                if (info!.Type is not FunctionType ft)
                    throw new TypeException(FormatTypeError(
                        $"'{vr.Name}' holds a {FormatType(info.Type)}, not a function — you can only cast functions",
                        null,
                        callLine,
                        "cast something that isn't a function",
                        "Only functions can be cast. Make sure the name you're casting refers to a function."));
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
                    throw new TypeException(FormatTypeError(
                        $"'{ot2.Name}' has no method named '{vr.Name}'",
                        null, callLine,
                        $"call method '{vr.Name}' on a {ot2.Name}",
                        avail));
                }
                if (firstArgType is InterfaceType ifaceT2 &&
                    _interfaceDefs.TryGetValue(ifaceT2.Name, out var ifaceDef2))
                {
                    var avail = ifaceDef2.Methods.Count > 0
                        ? $"Available methods: {string.Join(", ", ifaceDef2.Methods.Select(m => $"'{m.MethodName}'"))}."
                        : $"Interface '{ifaceT2.Name}' declares no methods.";
                    throw new TypeException(FormatTypeError(
                        $"interface '{ifaceT2.Name}' has no method named '{vr.Name}'",
                        null, callLine,
                        $"call method '{vr.Name}' through interface '{ifaceT2.Name}'",
                        avail));
                }
            }

            // Unknown identifier with non-object first arg (or no args) — runtime catches it.
            return (null, $"'{vr.Name}'", callLine, args);
        }

        // General path: PossessiveAccess → FunctionType for method ref, etc.
        var exprType = InferType(funcExpr);
        if (exprType == null) return (null, "this function", callLine, args);
        if (exprType is not FunctionType funcType)
            throw new TypeException(FormatTypeError(
                $"this expression holds a {FormatType(exprType)}, not a function — you can only cast functions",
                null,
                callLine,
                "cast something that isn't a function",
                "Only functions can be cast."));
        return (funcType, "this function", callLine, args);
    }

    // Returns method's FunctionType (params only, no receiver) and display info when the
    // first arg's type is an object or interface that declares a method with the given name.
    // Returns null if no such method is found.
    private (FunctionType funcType, string displayName, int declLine)? TryMethodDispatch(
        string name, IReadOnlyList<IExpression> args, int callLine)
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

        // ── ObjectType shell resolution ───────────────────────────────────────
        ObjectType { PositionalTypes.Count: 0, NamedFields.Count: 0, Methods.Count: 0,
                     EmbeddedTypeName: null, ConformedInterfaces.Count: 0 } ot
            when _interfaceDefs.ContainsKey(ot.Name) => new InterfaceType(ot.Name),
        ObjectType { PositionalTypes.Count: 0, NamedFields.Count: 0, Methods.Count: 0,
                     EmbeddedTypeName: null, ConformedInterfaces.Count: 0 } ot
            when _objectDefs.ContainsKey(ot.Name) => _objectDefs[ot.Name],
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
