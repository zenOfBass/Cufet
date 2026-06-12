using Cufet.Lexer;

namespace Cufet.Interpreter;

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

    private sealed class RecordValue
    {
        public IReadOnlyList<object>              PositionalFields { get; }
        public IReadOnlyList<(string Name, object Value)> NamedFields { get; }

        public RecordValue(
            IReadOnlyList<object> positionalFields,
            IReadOnlyList<(string Name, object Value)> namedFields)
        {
            PositionalFields = positionalFields;
            NamedFields      = namedFields;
        }
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
                    throw new RuntimeException($"'{d.Name}' is already defined on line {d.Line}.");
                _env[d.Name] = Evaluate(d.Value);
                break;

            case BecomesStatement b:
                if (!_env.ContainsKey(b.Name))
                {
                    var suggestion = FindSuggestion(b.Name);
                    var msg = $"'{b.Name}' isn't defined on line {b.Line} — use Define to create it first, then becomes to change it.";
                    if (suggestion != null) msg += $" Did you mean '{suggestion}'?";
                    throw new RuntimeException(msg);
                }
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
                var list  = ExpectSeries(sa.SeriesName, sa.Line);
                var value = Evaluate(sa.Value);
                if (sa.ToStart)
                    list.Insert(0, value);
                else if (sa.AfterIndex == null)
                    list.Add(value);
                else
                    list.Insert(ResolveIndex(sa.AfterIndex, list, sa.SeriesName, sa.Line) + 1, value);
                break;
            }

            case SeriesRemoveAtStatement sra:
            {
                var list = ExpectSeries(sra.SeriesName, sra.Line);
                list.RemoveAt(ResolveIndex(sra.Index, list, sra.SeriesName, sra.Line));
                break;
            }

            case SeriesRemoveValueStatement srv:
            {
                var list  = ExpectSeries(srv.SeriesName, srv.Line);
                var value = Evaluate(srv.Value);
                if (!list.Remove(value))
                    throw new RuntimeException($"Value not found in '{srv.SeriesName}' on line {srv.Line}.");
                break;
            }

            case SeriesSetStatement ss:
            {
                var list = ExpectSeries(ss.SeriesName, ss.Line);
                list[ResolveIndex(ss.Index, list, ss.SeriesName, ss.Line)] = Evaluate(ss.Value);
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
                var list = ExpectSeries(fe.SeriesName, fe.Line);
                int startCount = list.Count;
                string iterKey = fe.IteratorName ?? "it";
                bool hadPrev = _env.TryGetValue(iterKey, out var prev);
                try
                {
                    for (int i = 0; i < startCount; i++)
                    {
                        if (list.Count != startCount)
                            throw new RuntimeException(
                                $"'{fe.SeriesName}' was modified during a for-each loop on line {fe.Line} — use a While loop if you need to change it while looping.");
                        _env[iterKey] = list[i];
                        bool stopped = false;
                        try { foreach (var s in fe.Body) Execute(s); }
                        catch (StopException) { stopped = true; }
                        catch (SkipException) { /* next iteration */ }
                        if (stopped) break;
                        if (list.Count != startCount)
                            throw new RuntimeException(
                                $"'{fe.SeriesName}' was modified during a for-each loop on line {fe.Line} — use a While loop if you need to change it while looping.");
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

    private List<object> ExpectSeries(string name, int line = 0)
    {
        if (!_env.TryGetValue(name, out var val))
            throw new RuntimeException(UndefinedVariableMessage(name, line));
        if (val is not List<object> list)
            throw new RuntimeException(line > 0
                ? $"'{name}' isn't a series on line {line}."
                : $"'{name}' is not a series.");
        return list;
    }

    // Returns 0-based index. indexExpr==null means "last element".
    private int ResolveIndex(IExpression? indexExpr, List<object> list, string seriesName, int line)
    {
        if (indexExpr == null)
        {
            if (list.Count == 0)
                throw new RuntimeException($"Can't access the last item — '{seriesName}' is empty on line {line}.");
            return list.Count - 1;
        }
        var raw = Evaluate(indexExpr);
        if (raw is not decimal d)
            throw new RuntimeException($"Series index must be a number on line {line}.");
        var idx = (int)d;
        if (idx < 1 || idx > list.Count)
        {
            var range = list.Count == 0
                ? $"'{seriesName}' is empty"
                : $"'{seriesName}' has {list.Count} {(list.Count == 1 ? "item" : "items")} (you can reach items 1 through {list.Count})";
            throw new RuntimeException($"There's no item {idx} — {range}. This happened on line {line}.");
        }
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
                                     : throw new RuntimeException(UndefinedVariableMessage(r.Name, r.Line)),
        UnaryExpression  u    => EvaluateUnary(u),
        BinaryExpression b    => EvaluateBinary(b),
        SeriesLiteral    sl   => (object)sl.Elements.Select(Evaluate).ToList(),
        SeriesAccess     sa   => EvaluateSeriesAccess(sa),
        SeriesLength     sl   => (decimal)ExpectSeries(sl.SeriesName).Count,
        RecordLiteral    rl   => (object)new RecordValue(
                                     rl.PositionalFields.Select(Evaluate).ToList(),
                                     rl.NamedFields.Select(f => (f.Name, Evaluate(f.Value))).ToList()),
        RecordNamedAccess rna => EvaluateRecordNamedAccess(rna),
        CastExpression   cast => ExecuteCallExpr(cast.Function, cast.Args, cast.Line)
                                     ?? throw new RuntimeException(
                                         $"{FuncDisplayName(cast.Function)} gives nothing back — it can't be used as a value (line {cast.Line})."),
        _ => throw new InvalidOperationException($"Unknown expression type: {expr.GetType().Name}"),
    };

    private static string FuncDisplayName(IExpression funcExpr) =>
        funcExpr is VariableReference r ? $"'{r.Name}'" : "the function";

    // Evaluates the function expression, then executes the call.
    private object? ExecuteCallExpr(IExpression funcExpr, IReadOnlyList<IExpression> args, int line)
    {
        var funcVal = Evaluate(funcExpr);
        if (funcVal is not FunctionValue func)
            throw new RuntimeException($"{FuncDisplayName(funcExpr)} is not a function (line {line}).");
        return ExecuteCall(func, FuncDisplayName(funcExpr), args, line);
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

    private object EvaluateSeriesAccess(SeriesAccess sa)
    {
        var val = Evaluate(sa.Target);

        if (val is RecordValue rv)
        {
            if (sa.Index == null)
                throw new RuntimeException($"'last' is not supported for records on line {sa.Line}.");
            if (Evaluate(sa.Index) is not decimal d)
                throw new RuntimeException($"Record position must be a number on line {sa.Line}.");
            var idx = (int)d;
            if (idx < 1 || idx > rv.PositionalFields.Count)
                throw new RuntimeException(rv.PositionalFields.Count == 0
                    ? $"This record has no positional fields (line {sa.Line})."
                    : $"This record has {rv.PositionalFields.Count} positional field(s); there is no position {idx} (line {sa.Line}).");
            return rv.PositionalFields[idx - 1];
        }

        if (val is not List<object> list)
            throw new RuntimeException($"Expected a series on line {sa.Line}.");
        var sname = sa.Target is VariableReference vr ? vr.Name : "this expression";
        return list[ResolveIndex(sa.Index, list, sname, sa.Line)];
    }

    private object EvaluateRecordNamedAccess(RecordNamedAccess rna)
    {
        var recordVal = Evaluate(rna.Record);
        if (recordVal is not RecordValue rv)
            throw new RuntimeException(
                $"You're trying to access field '{rna.FieldName}' on something that isn't a record (line {rna.Line}).");
        var field = rv.NamedFields.FirstOrDefault(f => f.Name == rna.FieldName);
        if (field == default)
            throw new RuntimeException(
                $"This record has no field named '{rna.FieldName}' (line {rna.Line}).");
        return field.Value;
    }

    private object EvaluateUnary(UnaryExpression u)
    {
        if (u.Op == TokenType.Not)
        {
            var val = Evaluate(u.Operand);
            if (val is not bool b)
                throw new RuntimeException($"'not' requires a true-or-false value (line {u.Line}).");
            return (object)!b;
        }
        return (object)(-ToNumber(Evaluate(u.Operand), "unary -"));
    }

    private object EvaluateBinary(BinaryExpression b)
    {
        // Short-circuit: evaluate right only when the left doesn't decide the result.
        if (b.Op is TokenType.And or TokenType.Or)
        {
            var opName = b.Op == TokenType.And ? "and" : "or";
            var lv = Evaluate(b.Left);
            if (lv is not bool lb)
                throw new RuntimeException($"'{opName}' requires true-or-false values on both sides (line {b.Line}).");
            if (b.Op == TokenType.And && !lb) return (object)false;
            if (b.Op == TokenType.Or  &&  lb) return (object)true;
            var rv = Evaluate(b.Right);
            if (rv is not bool)
                throw new RuntimeException($"'{opName}' requires true-or-false values on both sides (line {b.Line}).");
            return rv;
        }

        var lv2 = Evaluate(b.Left);
        var rv2 = Evaluate(b.Right);
        return b.Op switch
        {
            TokenType.Plus     => (object)(ToNumber(lv2, "+") + ToNumber(rv2, "+")),
            TokenType.Minus    => (object)(ToNumber(lv2, "-") - ToNumber(rv2, "-")),
            TokenType.Star     => (object)(ToNumber(lv2, "*") * ToNumber(rv2, "*")),
            TokenType.Slash    => ToNumber(rv2, "/") == 0
                                      ? throw new RuntimeException($"Division by zero on line {b.Line}.")
                                      : (object)(ToNumber(lv2, "/") / ToNumber(rv2, "/")),
            TokenType.Percent  => ToNumber(rv2, "%") == 0
                                      ? throw new RuntimeException($"Modulo by zero on line {b.Line}.")
                                      : (object)(ToNumber(lv2, "%") % ToNumber(rv2, "%")),
            TokenType.Equal    => (object)lv2.Equals(rv2),
            TokenType.NotEqual => (object)!lv2.Equals(rv2),
            TokenType.Lt       => (object)(ToNumber(lv2, "<")  < ToNumber(rv2, "<")),
            TokenType.Gt       => (object)(ToNumber(lv2, ">")  > ToNumber(rv2, ">")),
            TokenType.Lte      => (object)(ToNumber(lv2, "<=") <= ToNumber(rv2, "<=")),
            TokenType.Gte      => (object)(ToNumber(lv2, ">=") >= ToNumber(rv2, ">=")),
            _ => throw new InvalidOperationException($"Unknown binary operator: {b.Op}"),
        };
    }

    // Backstop: fires only if a non-number reaches an arithmetic operator at runtime.
    // The type checker should prevent this for well-typed programs — if this fires,
    // investigate whether the checker has a coverage gap on the path that produced the value.
    private static decimal ToNumber(object val, string op) =>
        val is decimal d ? d : throw new RuntimeException($"Operator '{op}' requires a number.");

    private string UndefinedVariableMessage(string name, int line)
    {
        var suggestion = FindSuggestion(name);
        var located    = line > 0 ? $" on line {line}" : "";
        var msg = $"'{name}' isn't defined{located} — it was never given a value with Define.";
        if (suggestion != null)
            msg += $" Did you mean '{suggestion}'?";
        else
            msg += $" Declare it first: Define {name} as <value>.";
        return msg;
    }

    private string? FindSuggestion(string name)
    {
        string? best    = null;
        int     bestDist = 3; // only suggest if Levenshtein distance <= 2
        foreach (var key in _env.Keys)
        {
            var dist = Levenshtein(name, key);
            if (dist < bestDist) { bestDist = dist; best = key; }
        }
        return best;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = a[i - 1] == b[j - 1]
                    ? d[i - 1, j - 1]
                    : 1 + Math.Min(d[i - 1, j - 1], Math.Min(d[i - 1, j], d[i, j - 1]));
        return d[a.Length, b.Length];
    }

    private static string Format(object val) => val switch
    {
        bool b           => b ? "true" : "false",
        decimal d        => d.ToString(),
        List<object> lst => "(" + string.Join(", ", lst.Select(Format)) + ")",
        FunctionValue    => "<function>",
        RecordValue rv   => FormatRecord(rv),
        _                => val.ToString()!,
    };

    private static string FormatRecord(RecordValue rv)
    {
        var parts = new List<string>();
        foreach (var v in rv.PositionalFields)       parts.Add(Format(v));
        foreach (var (name, v) in rv.NamedFields)    parts.Add($"{name}: {Format(v)}");
        return "record(" + string.Join(", ", parts) + ")";
    }
}
