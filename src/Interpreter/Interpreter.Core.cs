using Cufet.Lexer;

namespace Cufet.Interpreter;

public sealed partial class Interpreter
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

    // The singleton runtime representation of the void value (the absent case of any voidable T).
    // Distinct from C# null, which means "this function returned nothing" in the call machinery.
    private sealed class VoidValue
    {
        public static readonly VoidValue Instance = new();
        private VoidValue() { }
    }

    private sealed class FunctionValue
    {
        public required IReadOnlyList<string>     ParameterNames { get; init; }
        public required IReadOnlyList<IStatement> Body           { get; init; }
    }

    private sealed class ObjectValue
    {
        public string TypeName { get; }
        public List<object>              PositionalFields { get; }
        public List<(string Name, object Value)> NamedFields { get; }
        // Slice 4 — embedding: null means no embedded object.
        public ObjectValue? EmbeddedObject { get; }

        public ObjectValue(
            string typeName,
            IEnumerable<object> positionalFields,
            IEnumerable<(string Name, object Value)> namedFields,
            ObjectValue? embeddedObject = null)
        {
            TypeName         = typeName;
            PositionalFields = positionalFields.ToList();
            NamedFields      = namedFields.ToList();
            EmbeddedObject   = embeddedObject;
        }

        public ObjectValue DeepCopy() => new ObjectValue(
            TypeName,
            PositionalFields.Select(DeepCopyValue),
            NamedFields.Select(f => (f.Name, DeepCopyValue(f.Value))),
            EmbeddedObject?.DeepCopy());

        private static object DeepCopyValue(object v) =>
            v is ObjectValue ov ? ov.DeepCopy() :
            v is RecordValue rv ? rv.DeepCopy() : v;
    }

    private sealed class RecordValue
    {
        public List<object>              PositionalFields { get; }
        public List<(string Name, object Value)> NamedFields { get; }

        public RecordValue(
            IEnumerable<object> positionalFields,
            IEnumerable<(string Name, object Value)> namedFields)
        {
            PositionalFields = positionalFields.ToList();
            NamedFields      = namedFields.ToList();
        }

        public RecordValue DeepCopy() => new RecordValue(
            PositionalFields.Select(DeepCopyValue),
            NamedFields.Select(f => (f.Name, DeepCopyValue(f.Value))));

        private static object DeepCopyValue(object v) =>
            v is RecordValue rv ? rv.DeepCopy() :
            v is ObjectValue ov ? ov.DeepCopy() : v;
    }

    private readonly Dictionary<string, ObjectDefinition> _objectDefs = new();

    private int _callDepth = 0;
    private readonly int _maxCallDepth;

    public Interpreter(TextWriter? output = null, int maxCallDepth = 1000)
    {
        _out = output ?? Console.Out;
        _maxCallDepth = maxCallDepth;
    }

    public void Execute(Program program)
    {
        // Hoist object definitions (before functions, so method bodies can reference them).
        foreach (var stmt in program.Statements)
        {
            if (stmt is ObjectDefinition od)
                _objectDefs[od.Name] = od;
        }

        // Hoist top-level function definitions.
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
            {
                var val = Evaluate(d.Value);
                _env[d.Name] = val is RecordValue rv ? rv.DeepCopy() :
                               val is ObjectValue ov ? ov.DeepCopy() : val;
                break;
            }

            case BecomesStatement b:
                if (!_env.ContainsKey(b.Name))
                {
                    var suggestion = FindSuggestion(b.Name);
                    var msg = $"'{b.Name}' isn't defined on line {b.Line} — use Define to create it first, then becomes to change it.";
                    if (suggestion != null) msg += $" Did you mean '{suggestion}'?";
                    throw new RuntimeException(msg);
                }
            {
                var val = Evaluate(b.Value);
                _env[b.Name] = val is RecordValue rv ? rv.DeepCopy() :
                               val is ObjectValue ov ? ov.DeepCopy() : val;
                break;
            }

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
                if (_env.TryGetValue(ss.SeriesName, out var ssEnvVal))
                {
                    if (ssEnvVal is ObjectValue ssOv)
                    {
                        if (ss.Index == null)
                            throw new RuntimeException($"'last' is not supported for objects on line {ss.Line}.");
                        if (Evaluate(ss.Index) is not decimal ssD)
                            throw new RuntimeException($"Object position must be a number on line {ss.Line}.");
                        var ssIdx = (int)ssD;
                        var ssOwner = FindOwnerForPositional(ssOv, ssIdx);
                        if (ssOwner == null)
                            throw new RuntimeException($"Object '{ssOv.TypeName}' has no positional field at position {ssIdx} (line {ss.Line}).");
                        ssOwner.Value.owner.PositionalFields[ssOwner.Value.idx] = Evaluate(ss.Value);
                        break;
                    }
                    if (ssEnvVal is RecordValue ssRrv)
                    {
                        if (ss.Index == null)
                            throw new RuntimeException($"'last' is not supported for records on line {ss.Line}.");
                        if (Evaluate(ss.Index) is not decimal ssD)
                            throw new RuntimeException($"Record position must be a number on line {ss.Line}.");
                        var ssIdx = (int)ssD;
                        if (ssIdx < 1 || ssIdx > ssRrv.PositionalFields.Count)
                            throw new RuntimeException(ssRrv.PositionalFields.Count == 0
                                ? $"This record has no positional fields (line {ss.Line})."
                                : $"This record has {ssRrv.PositionalFields.Count} positional field(s); there is no position {ssIdx} (line {ss.Line}).");
                        ssRrv.PositionalFields[ssIdx - 1] = Evaluate(ss.Value);
                        break;
                    }
                }
                var list = ExpectSeries(ss.SeriesName, ss.Line);
                list[ResolveIndex(ss.Index, list, ss.SeriesName, ss.Line)] = Evaluate(ss.Value);
                break;
            }

            case RecordNamedSetStatement rnss:
            {
                var recordVal = Evaluate(rnss.Record);
                if (recordVal is ObjectValue rnssOv)
                {
                    var owner = FindOwnerForNamedField(rnssOv, rnss.FieldName);
                    if (owner == null)
                        throw new RuntimeException($"Object of type '{rnssOv.TypeName}' has no field named '{rnss.FieldName}' (line {rnss.Line}).");
                    var fi = owner.NamedFields.FindIndex(f => f.Name == rnss.FieldName);
                    owner.NamedFields[fi] = (rnss.FieldName, Evaluate(rnss.Value));
                    break;
                }
                if (recordVal is not RecordValue rv)
                    throw new RuntimeException($"Expected a record for field assignment on line {rnss.Line}.");
                var fieldIdx = rv.NamedFields.FindIndex(f => f.Name == rnss.FieldName);
                if (fieldIdx < 0)
                    throw new RuntimeException($"This record has no field named '{rnss.FieldName}' (line {rnss.Line}).");
                rv.NamedFields[fieldIdx] = (rnss.FieldName, Evaluate(rnss.Value));
                break;
            }

            case PossessiveSetStatement pss:
                ExecutePossessiveSet(pss);
                break;

            case BindStatement:
            case ObjectDefinition:
            case InterfaceDefinition:
                break; // already hoisted / no runtime action

            case CastStatement cs:
                ExecuteCallExpr(cs.Function, cs.Args, cs.Line);
                break;

            case ReturnStatement ret:
                throw new ReturnException(ret.Value != null ? Evaluate(ret.Value) : null);

            case ForEachStatement fe:
            {
                var seriesVal = Evaluate(fe.Series);
                if (seriesVal is not List<object> list)
                    throw new RuntimeException($"Expected a series for 'for each' loop on line {fe.Line}.");
                string seriesDisplay = fe.Series is VariableReference fvr ? $"'{fvr.Name}'" : "The series";
                int startCount = list.Count;
                string iterKey = fe.IteratorName ?? "it";
                bool hadPrev = _env.TryGetValue(iterKey, out var prev);
                try
                {
                    for (int i = 0; i < startCount; i++)
                    {
                        if (list.Count != startCount)
                            throw new RuntimeException(
                                $"{seriesDisplay} was modified during a for-each loop on line {fe.Line} — use a While loop if you need to change it while looping.");
                        _env[iterKey] = list[i];
                        bool stopped = false;
                        try { foreach (var s in fe.Body) Execute(s); }
                        catch (StopException) { stopped = true; }
                        catch (SkipException) { /* next iteration */ }
                        if (stopped) break;
                        if (list.Count != startCount)
                            throw new RuntimeException(
                                $"{seriesDisplay} was modified during a for-each loop on line {fe.Line} — use a While loop if you need to change it while looping.");
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
        ObjectLiteral    ol   => EvaluateObjectLiteral(ol),
        PossessiveAccess pa   => EvaluatePossessiveAccess(pa),
        CastExpression   cast => ExecuteCallExpr(cast.Function, cast.Args, cast.Line)
                                     ?? throw new RuntimeException(
                                         $"{FuncDisplayName(cast.Function)} gives nothing back — it can't be used as a value (line {cast.Line})."),
        TextJoin   tj => EvaluateTextJoin(tj),
        TextConvert tc => (object)Format(Evaluate(tc.Value)),
        TextLength  tl => (object)(decimal)((string)Evaluate(tl.Target)).Length,
        RangeExpression re  => EvaluateRangeExpr(re),
        VoidLiteral        _  => VoidValue.Instance,
        ButVoidDefault bvd    => EvaluateButVoidDefault(bvd),
        _ => throw new InvalidOperationException($"Unknown expression type: {expr.GetType().Name}"),
    };

    private object EvaluateButVoidDefault(ButVoidDefault bvd)
    {
        var v = Evaluate(bvd.Voidable);
        return v is VoidValue ? Evaluate(bvd.Default) : v;
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

        if (val is ObjectValue ov)
        {
            if (sa.Index == null)
                throw new RuntimeException($"'last' is not supported for objects on line {sa.Line}.");
            if (Evaluate(sa.Index) is not decimal od)
                throw new RuntimeException($"Object position must be a number on line {sa.Line}.");
            var oidx = (int)od;
            var owner = FindOwnerForPositional(ov, oidx);
            if (owner == null)
                throw new RuntimeException($"Object '{ov.TypeName}' has no positional field at position {oidx} (line {sa.Line}).");
            return owner.Value.owner.PositionalFields[owner.Value.idx];
        }

        if (val is not List<object> list)
            throw new RuntimeException($"Expected a series on line {sa.Line}.");
        var sname = sa.Target is VariableReference vr ? vr.Name : "this expression";
        return list[ResolveIndex(sa.Index, list, sname, sa.Line)];
    }

    private object EvaluateRecordNamedAccess(RecordNamedAccess rna)
    {
        var target = Evaluate(rna.Record);

        if (target is ObjectValue ov)
        {
            if (TryFindNamedFieldValue(ov, rna.FieldName, out var found)) return found;
            throw new RuntimeException(
                $"Object of type '{ov.TypeName}' has no field named '{rna.FieldName}' (line {rna.Line}).");
        }

        if (target is not RecordValue rv)
            throw new RuntimeException(
                $"You're trying to access field '{rna.FieldName}' on something that isn't a record (line {rna.Line}).");
        var field = rv.NamedFields.FirstOrDefault(f => f.Name == rna.FieldName);
        if (field == default)
            throw new RuntimeException(
                $"This record has no field named '{rna.FieldName}' (line {rna.Line}).");
        return field.Value;
    }

    private object EvaluateTextJoin(TextJoin tj)
    {
        var l = Evaluate(tj.Left);
        var r = Evaluate(tj.Right);
        if (l is not string ls)
            throw new RuntimeException($"'joined to' requires text on the left side (line {tj.Line}).");
        if (r is not string rs)
            throw new RuntimeException($"'joined to' requires text on the right side (line {tj.Line}).");
        return (object)(ls + rs);
    }

    private object EvaluateRangeExpr(RangeExpression re)
    {
        var startVal = Evaluate(re.Start);
        var endVal   = Evaluate(re.End);
        if (startVal is not decimal start)
            throw new RuntimeException($"range start must be a number (line {re.Line}).");
        if (endVal is not decimal end)
            throw new RuntimeException($"range end must be a number (line {re.Line}).");
        var list = new List<object>();
        if (start <= end)
            for (decimal n = start; n <= end; n++)
                list.Add(n);
        else
            for (decimal n = start; n >= end; n--)
                list.Add(n);
        return (object)list;
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
            TokenType.Equal    => (object)ValuesEqual(lv2, rv2),
            TokenType.NotEqual => (object)!ValuesEqual(lv2, rv2),
            TokenType.Lt       => (object)(ToNumber(lv2, "<")  < ToNumber(rv2, "<")),
            TokenType.Gt       => (object)(ToNumber(lv2, ">")  > ToNumber(rv2, ">")),
            TokenType.Lte      => (object)(ToNumber(lv2, "<=") <= ToNumber(rv2, "<=")),
            TokenType.Gte      => (object)(ToNumber(lv2, ">=") >= ToNumber(rv2, ">=")),
            _ => throw new InvalidOperationException($"Unknown binary operator: {b.Op}"),
        };
    }

    // Deep value equality: same semantics as the spec's "is" / "is not" for records and objects.
    // Scalars use object.Equals; series compare element-wise; records compare structurally
    // (positionals in order, named fields sorted by name); objects compare nominally then
    // field-by-field (including the embedded-object chain recursively).
    private static bool ValuesEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a is VoidValue && b is VoidValue) return true;
        if (a is VoidValue || b is VoidValue) return false;

        if (a is List<object> la && b is List<object> lb)
        {
            if (la.Count != lb.Count) return false;
            for (int i = 0; i < la.Count; i++)
                if (!ValuesEqual(la[i], lb[i])) return false;
            return true;
        }

        if (a is RecordValue ra && b is RecordValue rb)
        {
            if (ra.PositionalFields.Count != rb.PositionalFields.Count) return false;
            for (int i = 0; i < ra.PositionalFields.Count; i++)
                if (!ValuesEqual(ra.PositionalFields[i], rb.PositionalFields[i])) return false;
            var aNamed = ra.NamedFields.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            var bNamed = rb.NamedFields.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            if (aNamed.Count != bNamed.Count) return false;
            for (int i = 0; i < aNamed.Count; i++)
                if (aNamed[i].Name != bNamed[i].Name || !ValuesEqual(aNamed[i].Value, bNamed[i].Value))
                    return false;
            return true;
        }

        if (a is ObjectValue oa && b is ObjectValue ob)
        {
            if (oa.TypeName != ob.TypeName) return false;
            if (oa.PositionalFields.Count != ob.PositionalFields.Count) return false;
            for (int i = 0; i < oa.PositionalFields.Count; i++)
                if (!ValuesEqual(oa.PositionalFields[i], ob.PositionalFields[i])) return false;
            var aNamed = oa.NamedFields.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            var bNamed = ob.NamedFields.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            if (aNamed.Count != bNamed.Count) return false;
            for (int i = 0; i < aNamed.Count; i++)
                if (aNamed[i].Name != bNamed[i].Name || !ValuesEqual(aNamed[i].Value, bNamed[i].Value))
                    return false;
            return ValuesEqual(oa.EmbeddedObject, ob.EmbeddedObject);
        }

        return a.Equals(b);
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
        VoidValue        => "void",
        bool b           => b ? "true" : "false",
        decimal d        => d.ToString(),
        List<object> lst => "(" + string.Join(", ", lst.Select(Format)) + ")",
        FunctionValue    => "<function>",
        RecordValue rv   => FormatRecord(rv),
        ObjectValue ov   => FormatObject(ov),
        _                => val.ToString()!,
    };

    private static string FormatRecord(RecordValue rv)
    {
        var parts = new List<string>();
        foreach (var v in rv.PositionalFields)       parts.Add(Format(v));
        foreach (var (name, v) in rv.NamedFields)    parts.Add($"{name}: {Format(v)}");
        return "record(" + string.Join(", ", parts) + ")";
    }

    private static string FormatObject(ObjectValue ov)
    {
        var parts = new List<string>();
        foreach (var v in ov.PositionalFields)       parts.Add(Format(v));
        foreach (var (name, v) in ov.NamedFields)    parts.Add($"{name}: {Format(v)}");
        if (ov.EmbeddedObject != null)               parts.Add(Format(ov.EmbeddedObject));
        return $"{ov.TypeName}(" + string.Join(", ", parts) + ")";
    }
}
