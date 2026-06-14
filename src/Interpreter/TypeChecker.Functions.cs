namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    private void CheckBind(BindStatement bind)
    {
        // Build function body env: only function signatures + parameters (no globals).
        // This mirrors runtime scoping — functions cannot see the caller's variables.
        var snapshot = new Dictionary<string, TypeInfo>(_env);
        _env.Clear();
        foreach (var (k, v) in snapshot.Where(kv => kv.Value.Type is FunctionType))
            _env[k] = v;
        foreach (var (type, name) in bind.Parameters)
            _env[name] = new TypeInfo(ResolveParamType(type), new VariableReference(name, 0), bind.Line);

        var prevInFunction        = _inFunction;
        var prevReturnType        = _expectedReturnType;
        var prevFunctionLine      = _functionDeclarationLine;
        _inFunction               = true;
        _expectedReturnType       = bind.ReturnType;
        _functionDeclarationLine  = bind.Line;

        try
        {
            foreach (var stmt in bind.Body)
                CheckStatement(stmt);
        }
        finally
        {
            _inFunction               = prevInFunction;
            _expectedReturnType       = prevReturnType;
            _functionDeclarationLine  = prevFunctionLine;
            _env.Clear();
            foreach (var (k, v) in snapshot) _env[k] = v;
        }

        if (bind.ReturnType != null && !DefinitelyReturns(bind.Body))
            throw new TypeException(FormatTypeError(
                $"'{bind.Name}' is declared to give back a {FormatType(bind.ReturnType)}, but it can reach its end without returning one",
                null,
                bind.Line,
                "define a function that might not return a value",
                "Make sure every path through the function ends with a return statement."));
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

            if (argType == formalType) continue;
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

        return funcType.ReturnType;
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
            bool inEnv = _env.TryGetValue(vr.Name, out var info);

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

    // Resolves ObjectType shells (produced by ParseTypeAnnotation for identifiers) to their
    // proper type: InterfaceType if the name is in _interfaceDefs, full ObjectType if in _objectDefs.
    // Returns the type unchanged if it's already a resolved type or not a named-object shell.
    private CufetType ResolveParamType(CufetType type) => type switch
    {
        ObjectType { PositionalTypes.Count: 0, NamedFields.Count: 0, Methods.Count: 0,
                     EmbeddedTypeName: null, ConformedInterfaces.Count: 0 } ot
            when _interfaceDefs.ContainsKey(ot.Name) => new InterfaceType(ot.Name),
        ObjectType { PositionalTypes.Count: 0, NamedFields.Count: 0, Methods.Count: 0,
                     EmbeddedTypeName: null, ConformedInterfaces.Count: 0 } ot
            when _objectDefs.ContainsKey(ot.Name) => _objectDefs[ot.Name],
        _ => type
    };
}
