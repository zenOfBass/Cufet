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
            if (!_env.TryGetValue(vr.Name, out var val))
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

        // Full snapshot — captures function-typed bindings too, so the finally can do a clean
        // restore even when a parameter holds a FunctionValue.
        var snapshot = new Dictionary<string, object>(_env);

        // Isolate scope: remove non-function bindings (globals, caller locals).
        // FunctionValues stay in _env so the body can call other functions.
        var toRemove = new List<string>();
        foreach (var (k, v) in _env)
            if (v is not FunctionValue) toRemove.Add(k);
        foreach (var k in toRemove) _env.Remove(k);

        // Bind parameters (including any function-typed ones).
        for (int i = 0; i < func.ParameterNames.Count; i++)
            _env[func.ParameterNames[i]] = argValues[i];

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
            // Full restore from pre-call snapshot. No LINQ — plain ops only.
            _env.Clear();
            foreach (var (k, v) in snapshot) _env[k] = v;
            _callDepth--;
        }

        return returnValue;
    }

    private object? DispatchMethod(string methodName, object receiver, IReadOnlyList<IExpression> args, int line)
    {
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
        var snapshot  = new Dictionary<string, object>(_env);

        var toRemove = new List<string>();
        foreach (var (k, v) in _env)
            if (v is not FunctionValue) toRemove.Add(k);
        foreach (var k in toRemove) _env.Remove(k);

        for (int i = 0; i < paramNames.Count; i++)
            _env[paramNames[i]] = argValues[i];
        _env["one"] = receiver;

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
            _env.Clear();
            foreach (var (k, v) in snapshot) _env[k] = v;
            _callDepth--;
        }

        return returnValue;
    }
}
