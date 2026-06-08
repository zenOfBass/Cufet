using NLP.Lexer;

namespace NLP.Interpreter;

public sealed class Interpreter
{
    private readonly TextWriter _out;
    private readonly Dictionary<string, object> _env = new();

    // Used internally to implement Stop/Skip — never escape the loop handlers.
    private sealed class StopException  : Exception { }
    private sealed class SkipException  : Exception { }
    private sealed class ReturnException : Exception
    {
        public object? Value { get; }
        public ReturnException(object? value) { Value = value; }
    }

    private sealed class FunctionValue
    {
        public required IReadOnlyList<string>     ParameterNames { get; init; }
        public required IReadOnlyList<IStatement> Body           { get; init; }
    }

    private int _callDepth = 0;
    private readonly int _maxCallDepth;

    public Interpreter(TextWriter? output = null, int maxCallDepth = 1000)
    {
        _out = output ?? Console.Out;
        _maxCallDepth = maxCallDepth;
    }

    public void Execute(Program program)
    {
        // Hoist all function definitions before executing any statement,
        // enabling forward references and mutual recursion.
        foreach (var stmt in program.Statements)
        {
            if (stmt is BindStatement bind)
                _env[bind.Name] = new FunctionValue
                {
                    ParameterNames = bind.Parameters.Select(p => p.Name).ToList(),
                    Body           = bind.Body,
                };
        }

        foreach (var stmt in program.Statements)
            Execute(stmt);
    }

    private void Execute(IStatement stmt)
    {
        switch (stmt)
        {
            case StateStatement s:
                _out.WriteLine(Format(Evaluate(s.Value)));
                break;

            case DefineStatement d:
                if (_env.ContainsKey(d.Name))
                    throw new RuntimeException($"'{d.Name}' is already defined.");
                _env[d.Name] = Evaluate(d.Value);
                break;

            case BecomesStatement b:
                if (!_env.ContainsKey(b.Name))
                    throw new RuntimeException($"'{b.Name}' is not defined — use Define to declare it first.");
                _env[b.Name] = Evaluate(b.Value);
                break;

            case IfStatement ifStmt:
            {
                bool executed = false;
                foreach (var arm in ifStmt.Arms)
                {
                    var condVal = Evaluate(arm.Condition);
                    if (condVal is not bool b)
                        throw new RuntimeException("If condition must evaluate to true or false.");
                    if (b)
                    {
                        foreach (var s in arm.Body) Execute(s);
                        executed = true;
                        break;
                    }
                }
                if (!executed && ifStmt.ElseBody is not null)
                    foreach (var s in ifStmt.ElseBody) Execute(s);
                break;
            }

            case WhileStatement ws:
            {
                while (true)
                {
                    var condVal = Evaluate(ws.Condition);
                    if (condVal is not bool b)
                        throw new RuntimeException("While condition must evaluate to true or false.");
                    if (!b) break;
                    try   { foreach (var s in ws.Body) Execute(s); }
                    catch (StopException) { break; }
                    catch (SkipException) { continue; }
                }
                break;
            }

            case RepeatUntilStatement ru:
            {
                while (true)
                {
                    try   { foreach (var s in ru.Body) Execute(s); }
                    catch (StopException) { break; }
                    catch (SkipException) { /* fall through to condition check */ }
                    var condVal = Evaluate(ru.Condition);
                    if (condVal is not bool b)
                        throw new RuntimeException("Until condition must evaluate to true or false.");
                    if (b) break;
                }
                break;
            }

            case StopStatement:
                throw new StopException();

            case SkipStatement:
                throw new SkipException();

            case SeriesAddStatement sa:
            {
                var list  = ExpectSeries(sa.SeriesName);
                var value = Evaluate(sa.Value);
                if (sa.ToStart)
                    list.Insert(0, value);
                else if (sa.AfterIndex == null)
                    list.Add(value);
                else
                    list.Insert(ResolveIndex(sa.AfterIndex, list) + 1, value);
                break;
            }

            case SeriesRemoveAtStatement sra:
            {
                var list = ExpectSeries(sra.SeriesName);
                list.RemoveAt(ResolveIndex(sra.Index, list));
                break;
            }

            case SeriesRemoveValueStatement srv:
            {
                var list  = ExpectSeries(srv.SeriesName);
                var value = Evaluate(srv.Value);
                if (!list.Remove(value))
                    throw new RuntimeException("value not found in the series.");
                break;
            }

            case SeriesSetStatement ss:
            {
                var list = ExpectSeries(ss.SeriesName);
                list[ResolveIndex(ss.Index, list)] = Evaluate(ss.Value);
                break;
            }

            case BindStatement:
                break; // already hoisted in Execute(Program)

            case CastStatement cs:
                ExecuteCallExpr(cs.Function, cs.Args, cs.Line);
                break;

            case ReturnStatement ret:
                throw new ReturnException(ret.Value != null ? Evaluate(ret.Value) : null);

            case ForEachStatement fe:
            {
                var list = ExpectSeries(fe.SeriesName);
                int startCount = list.Count;
                string iterKey = fe.IteratorName ?? "it";
                bool hadPrev = _env.TryGetValue(iterKey, out var prev);
                try
                {
                    for (int i = 0; i < startCount; i++)
                    {
                        if (list.Count != startCount)
                            throw new RuntimeException(
                                "the series was modified during a for-each loop; use a while loop if you need to change it while looping.");
                        _env[iterKey] = list[i];
                        bool stopped = false;
                        try { foreach (var s in fe.Body) Execute(s); }
                        catch (StopException) { stopped = true; }
                        catch (SkipException) { /* next iteration */ }
                        if (stopped) break;
                        if (list.Count != startCount)
                            throw new RuntimeException(
                                "the series was modified during a for-each loop; use a while loop if you need to change it while looping.");
                    }
                }
                finally
                {
                    if (hadPrev) _env[iterKey] = prev!;
                    else _env.Remove(iterKey);
                }
                break;
            }
        }
    }

    private List<object> ExpectSeries(string name)
    {
        if (!_env.TryGetValue(name, out var val))
            throw new RuntimeException($"'{name}' is not defined.");
        if (val is not List<object> list)
            throw new RuntimeException($"'{name}' is not a series.");
        return list;
    }

    // Returns 0-based index. indexExpr==null means "last element".
    private int ResolveIndex(IExpression? indexExpr, List<object> list)
    {
        if (indexExpr == null)
        {
            if (list.Count == 0)
                throw new RuntimeException("Cannot access an element of an empty series.");
            return list.Count - 1;
        }
        var raw = Evaluate(indexExpr);
        if (raw is not decimal d)
            throw new RuntimeException("Series index must be a number.");
        var idx = (int)d;
        if (idx < 1 || idx > list.Count)
            throw new RuntimeException(
                $"there is no {OrdinalSuffix(idx)} item; the series has {list.Count}");
        return idx - 1; // convert to 0-based
    }

    private static string OrdinalSuffix(int n) => (n % 100) switch
    {
        11 or 12 or 13 => $"{n}th",
        _ => (n % 10) switch
        {
            1 => $"{n}st",
            2 => $"{n}nd",
            3 => $"{n}rd",
            _ => $"{n}th",
        },
    };

    private object Evaluate(IExpression expr) => expr switch
    {
        NumberLiteral    n    => (object)n.Value,  // decimal — no floating-point surprises
        StringLiteral    s    => s.Value,
        VariableReference r   => _env.TryGetValue(r.Name, out var val)
                                     ? val
                                     : throw new RuntimeException($"'{r.Name}' is not defined."),
        UnaryExpression  u    => EvaluateUnary(u),
        BinaryExpression b    => EvaluateBinary(b),
        SeriesLiteral    sl   => (object)sl.Elements.Select(Evaluate).ToList(),
        SeriesAccess     sa   => EvaluateSeriesAccess(sa),
        SeriesLength     sl   => (decimal)ExpectSeries(sl.SeriesName).Count,
        CastExpression   cast => ExecuteCallExpr(cast.Function, cast.Args, cast.Line)
                                     ?? throw new RuntimeException(
                                         $"{FuncDisplayName(cast.Function)} gives nothing back — it can't be used as a value."),
        _ => throw new InvalidOperationException($"Unknown expression type: {expr.GetType().Name}"),
    };

    private static string FuncDisplayName(IExpression funcExpr) =>
        funcExpr is VariableReference r ? $"'{r.Name}'" : "the function";

    // Evaluates the function expression, then executes the call.
    private object? ExecuteCallExpr(IExpression funcExpr, IReadOnlyList<IExpression> args, int line)
    {
        var funcVal = Evaluate(funcExpr);
        if (funcVal is not FunctionValue func)
            throw new RuntimeException($"{FuncDisplayName(funcExpr)} is not a function.");
        return ExecuteCall(func, FuncDisplayName(funcExpr), args, line);
    }

    // Executes a resolved function call and returns the return value (null for void).
    // Manages call depth, argument evaluation, and scope isolation.
    private object? ExecuteCall(FunctionValue func, string displayName, IReadOnlyList<IExpression> args, int line)
    {
        if (args.Count != func.ParameterNames.Count)
            throw new RuntimeException(
                $"{displayName} expects {func.ParameterNames.Count} argument(s), got {args.Count}.");

        _callDepth++;
        if (_callDepth > _maxCallDepth)
        {
            _callDepth--;
            throw new RuntimeException(
                $"{displayName} called itself too many times — is it missing a base case, or a case that stops the recursion?");
        }

        // Evaluate args in caller scope before altering env.
        var argValues = args.Select(Evaluate).ToList();

        // Save all non-function bindings (globals, caller locals).
        // Function values stay in _env so callees can call other functions.
        var snapshot = _env
            .Where(kv => kv.Value is not FunctionValue)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var key in snapshot.Keys) _env.Remove(key);

        // Bind parameters.
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
            // Restore caller env: remove all function-local bindings (params + any Define'd locals),
            // then restore snapshot. No LINQ — plain foreach avoids extra frames during unwind.
            var toRemove = new List<string>();
            foreach (var (k, v) in _env)
                if (v is not FunctionValue) toRemove.Add(k);
            foreach (var k in toRemove) _env.Remove(k);
            foreach (var (k, v) in snapshot) _env[k] = v;
            _callDepth--;
        }

        return returnValue;
    }

    private object EvaluateSeriesAccess(SeriesAccess sa)
    {
        var list = ExpectSeries(sa.SeriesName);
        return list[ResolveIndex(sa.Index, list)];
    }

    private object EvaluateUnary(UnaryExpression u) =>
        (object)(-ToNumber(Evaluate(u.Operand), "unary -"));

    private object EvaluateBinary(BinaryExpression b)
    {
        var lv = Evaluate(b.Left);
        var rv = Evaluate(b.Right);
        return b.Op switch
        {
            TokenType.Plus     => (object)(ToNumber(lv, "+") + ToNumber(rv, "+")),
            TokenType.Minus    => (object)(ToNumber(lv, "-") - ToNumber(rv, "-")),
            TokenType.Star     => (object)(ToNumber(lv, "*") * ToNumber(rv, "*")),
            TokenType.Slash    => ToNumber(rv, "/") == 0
                                      ? throw new RuntimeException("Division by zero.")
                                      : (object)(ToNumber(lv, "/") / ToNumber(rv, "/")),
            TokenType.Equal    => (object)lv.Equals(rv),
            TokenType.NotEqual => (object)!lv.Equals(rv),
            TokenType.Lt       => (object)(ToNumber(lv, "<")  < ToNumber(rv, "<")),
            TokenType.Gt       => (object)(ToNumber(lv, ">")  > ToNumber(rv, ">")),
            TokenType.Lte      => (object)(ToNumber(lv, "<=") <= ToNumber(rv, "<=")),
            TokenType.Gte      => (object)(ToNumber(lv, ">=") >= ToNumber(rv, ">=")),
            _ => throw new InvalidOperationException($"Unknown binary operator: {b.Op}"),
        };
    }

    private static decimal ToNumber(object val, string op) =>
        val is decimal d ? d : throw new RuntimeException($"Operator '{op}' requires a number.");

    private static string Format(object val) => val switch
    {
        bool b           => b ? "true" : "false",
        decimal d        => d.ToString(),
        List<object> lst => "(" + string.Join(", ", lst.Select(Format)) + ")",
        FunctionValue    => "<function>",
        _                => val.ToString()!,
    };
}
