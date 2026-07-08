namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    private static string FuncDisplayName(IExpression funcExpr) =>
        funcExpr is VariableReference r ? $"'{r.Name}'" : "the function";

    // Evaluates the function expression, then executes the call.
    // Three dispatch forms:
    //   Cast racer's steer on (90)     → PossessiveAccess → DispatchMethod(steer, racer, [90])
    //   Cast steer on (racer, 90)      → VarRef not in env → DispatchMethod(steer, racer, [90])
    //   Cast greet on alice            → VarRef not in env → DispatchMethod(greet, alice, [])
    //   Cast add on (3, 4)             → VarRef in env as FunctionValue → ExecuteCall
    private object? ExecuteCallExpr(IExpression funcExpr, IReadOnlyList<IExpression> args, int line)
    {
        if (funcExpr is PossessiveAccess pa)
        {
            var target = Evaluate(pa.Target);
            return DispatchMethod(pa.Member, target, args, line);
        }

        if (funcExpr is VariableReference vr)
        {
            if (!TryLookupValue(vr.Name, out var val))
            {
                // Not a free function — first arg is the receiver, remaining args are params.
                if (args.Count == 0)
                    throw new RuntimeException($"'{vr.Name}' is not defined (line {line}).");
                var receiver = Evaluate(args[0]);
                return DispatchMethod(vr.Name, receiver, args.Skip(1).ToList(), line);
            }
            if (val is not FunctionValue func)
                throw new RuntimeException($"'{vr.Name}' is not a function (line {line}).");
            return ExecuteCall(func, $"'{vr.Name}'", args, line);
        }

        var funcVal = Evaluate(funcExpr);
        if (funcVal is not FunctionValue f)
            throw new RuntimeException($"{FuncDisplayName(funcExpr)} is not a function (line {line}).");
        return ExecuteCall(f, FuncDisplayName(funcExpr), args, line);
    }

    // Executes a resolved function call and returns the return value (null for void).
    // Manages call depth, argument evaluation, and scope isolation.
    private object? ExecuteCall(FunctionValue func, string displayName, IReadOnlyList<IExpression> args, int line)
    {
        if (args.Count != func.ParameterNames.Count)
            throw new RuntimeException(
                $"{displayName} expects {func.ParameterNames.Count} argument(s), got {args.Count} (line {line}).");

        _callDepth++;
        if (_callDepth > _maxCallDepth)
        {
            _callDepth--;
            throw new RuntimeException(
                $"{displayName} called itself too many times (line {line}) — is it missing a base case, or a case that stops the recursion?");
        }

        // Evaluate args in caller scope before altering env.
        var argValues = args.Select(Evaluate).ToList();

        var saved = SaveScopes();

        if (func.CapturedEnv != null)
        {
            // Closure: load the captured flat env into the function's fresh global scope.
            foreach (var (k, v) in func.CapturedEnv)
                Scope[k] = v;
        }
        else
        {
            // Top-level function: only function-typed bindings from caller are visible.
            foreach (var scope in saved.Scopes)
                foreach (var (k, v) in scope)
                    if (v is FunctionValue) Scope[k] = v;

            // On first entry from the top level, record which data values are now hidden so that
            // UndefinedVariableMessage can produce a teaching error instead of "X isn't defined".
            if (_callDepth == 1 && saved.Scopes.Count > 0)
            {
                _hiddenTopLevelData = new Dictionary<string, object>();
                foreach (var (k, v) in saved.Scopes[0])
                    if (v is not FunctionValue) _hiddenTopLevelData[k] = v;
            }
        }

        // Bind parameters. Binding is binding: arg-passing applies the SAME region-aware
        // copy policy as Define/becomes/closure-capture — value-types (records/objects)
        // deep-copy so a mutated param can't leak to the caller; region-types (series/maps)
        // and immutable scalars/text share. Keeps all four binding sites uniform.
        for (int i = 0; i < func.ParameterNames.Count; i++)
        {
            var arg = argValues[i];
            Scope[func.ParameterNames[i]] = arg is RecordValue rv ? rv.DeepCopy()
                                          : arg is ObjectValue ov ? ov.DeepCopy()
                                          : arg;
        }

        object? returnValue = null;
        try
        {
            foreach (var stmt in func.Body)
                Execute(stmt);
        }
        catch (ReturnException re)
        {
            returnValue = re.Value;
        }
        finally
        {
            RestoreScopes(saved);
            _callDepth--;
            if (_callDepth == 0 && func.CapturedEnv == null)
                _hiddenTopLevelData = null;
        }

        return returnValue;
    }

    private object? DispatchMethod(string methodName, object receiver, IReadOnlyList<IExpression> args, int line)
    {
        if (receiver is BookValue bv)
            return DispatchBookFunction(bv, methodName, args, line);

        if (receiver is ObjectValue ov)
        {
            // Walk own object first, then embedded chain.
            var current = ov;
            while (current != null)
            {
                if (_objectDefs.TryGetValue(current.TypeName, out var def))
                {
                    var method = def.Methods.FirstOrDefault(m => m.Name == methodName);
                    if (method != null)
                        return ExecuteMethod(current, method, args, line);
                }
                current = current.EmbeddedObject;
            }
            throw new RuntimeException($"'{methodName}' is not a method on '{ov.TypeName}' (line {line}).");
        }
        throw new RuntimeException($"'{methodName}' is not a method on this value (line {line}).");
    }

    private object? ExecuteMethod(ObjectValue receiver, BindStatement method, IReadOnlyList<IExpression> args, int line)
    {
        _callDepth++;
        if (_callDepth > _maxCallDepth)
        {
            _callDepth--;
            throw new RuntimeException(
                $"'{method.Name}' called itself too many times (line {line}) — is it missing a base case?");
        }

        var paramNames = method.Parameters.Select(p => p.Name).ToList();
        if (args.Count != paramNames.Count)
            throw new RuntimeException(
                $"'{method.Name}' expects {paramNames.Count} argument(s), got {args.Count} (line {line}).");

        var argValues = args.Select(Evaluate).ToList();
        var saved     = SaveScopes();

        // Method scope: only function-typed bindings from caller visible.
        foreach (var scope in saved.Scopes)
            foreach (var (k, v) in scope)
                if (v is FunctionValue) Scope[k] = v;

        for (int i = 0; i < paramNames.Count; i++)
            Scope[paramNames[i]] = argValues[i];
        Scope["one"] = receiver;

        object? returnValue = null;
        try
        {
            foreach (var stmt in method.Body)
                Execute(stmt);
        }
        catch (ReturnException re)
        {
            returnValue = re.Value;
        }
        finally
        {
            RestoreScopes(saved);
            _callDepth--;
        }

        return returnValue;
    }
}
