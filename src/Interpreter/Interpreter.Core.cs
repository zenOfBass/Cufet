using Cufet.Lexer;

namespace Cufet.Interpreter;

// Runtime sentinel for a rabbit region. Passed as a parameter so callees can allocate into
// the region. In the interpreter (GC-backed) this is a tag value — region semantics are enforced
// statically by the type checker; the native backend implements the physical arena.
public sealed class RabbitValue
{
    public readonly string Name;
    public RabbitValue(string name) => Name = name;
}

// Reference-typed readable stream — wraps a TextReader for incremental text consumption.
// Stateful: each read advances the position; not reversible.
public sealed class ReadableStreamValue
{
    public readonly TextReader Reader;
    public ReadableStreamValue(TextReader reader) => Reader = reader;
}

// Reference-typed writable stream — wraps a TextWriter for incremental text output.
public sealed class WritableStreamValue
{
    public readonly TextWriter Writer;
    public WritableStreamValue(TextWriter writer) => Writer = writer;
}

public sealed partial class Interpreter
{
    private readonly TextWriter _out;
    private readonly TextReader _in;
    private readonly List<Dictionary<string, object>> _scopes = [new()];
    // Parallel to _scopes: definition order per scope, for LIFO destructor firing.
    private readonly List<List<string>> _scopeDefOrder = [[]];

    // Per-interpreter RNG — entropy-seeded by default (real randomness); re-seedable via
    // 'Seed the chance with N.' for reproducibility. Not static: parallel tests must not share state.
    private Random _rng = new Random();

    // Cooperative SIGINT flag: set by the Console.CancelKeyPress handler (signal-dispatch thread),
    // read by the interpreter's main thread at checkpoints. volatile bool is the complete solution
    // for a single-writer / single-reader flag — no lock, no Interlocked, no barrier needed.
    private volatile bool _interruptRequested;

    // Allow tests to set the interrupt flag directly without synthesizing a real Ctrl-C.
    internal void SimulateInterrupt() => _interruptRequested = true;

    // Non-null while executing a setter body: bypasses re-dispatch for the same (type, field) pair
    // so 'one's field becomes X' inside the setter does a raw write instead of recursing.
    private (string TypeName, string FieldName)? _inSetterFor = null;

    private Dictionary<string, object> Scope => _scopes[^1];

    private bool TryLookupValue(string name, out object val)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out val!)) return true;
        val = null!;
        return false;
    }

    private Dictionary<string, object>? FindOwningScope(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].ContainsKey(name)) return _scopes[i];
        return null;
    }

    private void EnterScope()
    {
        _scopes.Add(new Dictionary<string, object>());
        _scopeDefOrder.Add([]);
    }

    private void ExitScope()
    {
        RunScopeUnmakers(_scopeDefOrder[^1], _scopes[^1]);
        _scopes.RemoveAt(_scopes.Count - 1);
        _scopeDefOrder.RemoveAt(_scopeDefOrder.Count - 1);
    }

    private (List<Dictionary<string, object>> Scopes, List<List<string>> DefOrders) SaveScopes()
    {
        var saved = (_scopes.ToList(), _scopeDefOrder.ToList());
        _scopes.Clear();
        _scopes.Add(new Dictionary<string, object> { ["input"] = new ReadableStreamValue(_in) });
        _scopeDefOrder.Clear();
        _scopeDefOrder.Add([]);
        return saved;
    }

    private void RestoreScopes((List<Dictionary<string, object>> Scopes, List<List<string>> DefOrders) saved)
    {
        _scopes.Clear();
        foreach (var s in saved.Scopes) _scopes.Add(s);
        _scopeDefOrder.Clear();
        foreach (var d in saved.DefOrders) _scopeDefOrder.Add(d);
    }

    // Used internally to implement Stop/Skip — never escape the loop handlers.
    private sealed class StopException  : Exception { }
    private sealed class SkipException  : Exception { }
    private sealed class ReturnException : Exception
    {
        public object? Value { get; }
        public ReturnException(object? value) { Value = value; }
    }

    // Thrown at statement-dispatch checkpoints when _interruptRequested is true.
    // Unwinds the call stack to the REPL top level; never escapes to the user.
    private sealed class InterruptUnwind : Exception { }

    // Runtime representation of a failure value produced by 'return a failure "..."'.
    private sealed class FailureValue
    {
        public string  Message  { get; }
        public string? Category { get; }
        public FailureValue(string message, string? category) { Message = message; Category = category; }
    }

    // Used internally to propagate a failure through the call stack inside Try blocks.
    // Never escapes to the user — caught by TryStatement, FailureFallback, or FailurePropagate.
    private sealed class FailureUnwind : Exception
    {
        public FailureValue Value { get; }
        public FailureUnwind(FailureValue value) { Value = value; }
    }

    // Runtime representation of 'the exception' binding inside an exception handler.
    private sealed class ExceptionValue
    {
        public string Message { get; }
        public ExceptionValue(string message) => Message = message;
    }

    // Thrown by 'Suppress the exception.' inside an exception handler to signal swallow-and-continue.
    // Caught by ExecuteTryStatement; never visible to users.
    private sealed class SuppressSignal : Exception { }

    // Runtime representation of a book value — a named collection of native functions and constants.
    // Stateless singleton; Pull binds the pre-existing instance into the current scope.
    private sealed class BookValue
    {
        public string Name { get; }
        public IReadOnlyDictionary<string, Func<object[], object?>> Functions { get; }
        public IReadOnlyDictionary<string, object> Constants { get; }

        public BookValue(
            string name,
            IReadOnlyDictionary<string, Func<object[], object?>> functions,
            IReadOnlyDictionary<string, object> constants)
        {
            Name      = name;
            Functions = functions;
            Constants = constants;
        }
    }

    // Runtime representation of a matrix value — a 2D numeric grid, reference-typed.
    // Stores data row-major. Indexing is 1-based (row 1 is _data[0..Cols-1]).
    private sealed class MatrixValue
    {
        public int Rows { get; }
        public int Cols { get; }
        private readonly decimal[] _data;

        public MatrixValue(int rows, int cols, decimal[] data)
        {
            Rows  = rows;
            Cols  = cols;
            _data = data;
        }

        public decimal GetItem(int row, int col) => _data[(row - 1) * Cols + (col - 1)];
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
        // null for top-level functions; non-null for closures (captured at creation time).
        public Dictionary<string, object>?        CapturedEnv    { get; init; }
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
    private readonly Dictionary<string, UnmakerDeclaration> _unmakeDefs = new();
    private readonly Dictionary<(string TypeName, TokenType Op), OperatorOverloadDeclaration> _overloadDefs = new();

    private int _callDepth = 0;
    private readonly int _maxCallDepth;

    public Interpreter(TextWriter? output = null, TextReader? input = null, int maxCallDepth = 1000)
    {
        _out = output ?? Console.Out;
        _in  = input  ?? Console.In;
        _maxCallDepth = maxCallDepth;
        _scopes[0]["input"] = new ReadableStreamValue(_in);
        // e.Cancel = true: convert Ctrl-C from "terminate process" into "set our flag."
        // The handler runs on the signal-dispatch thread; volatile bool handles the cross-thread write.
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _interruptRequested = true; };
    }

    // Flattens statements through Pull...Done scope bodies so that hoisting passes see
    // Bind/Object/etc. declarations inside Pull scopes (hoisting is transparent to Pull scopes).
    private static IEnumerable<IStatement> FlattenHoistable(IEnumerable<IStatement> stmts)
    {
        foreach (var s in stmts)
        {
            yield return s;
            if (s is PullStatement ps)
                foreach (var inner in FlattenHoistable(ps.Body)) yield return inner;
            if (s is PullRabbitStatement prs)
                foreach (var inner in FlattenHoistable(prs.Body)) yield return inner;
        }
    }

    public void Execute(Program program)
    {
        var scheduler = new CufetScheduler();
        scheduler.Run(() => { ExecuteCore(program); return Task.CompletedTask; });
    }

    private void ExecuteCore(Program program)
    {
        // Hoist object definitions (before functions, so method bodies can reference them).
        foreach (var stmt in FlattenHoistable(program.Statements))
        {
            if (stmt is ObjectDefinition od)
                _objectDefs[od.Name] = od;
        }

        // Hoist destructor declarations.
        foreach (var stmt in FlattenHoistable(program.Statements))
        {
            if (stmt is UnmakerDeclaration ud)
                _unmakeDefs[ud.UnmakesTypeName] = ud;
        }

        // Hoist operator overload declarations.
        foreach (var stmt in FlattenHoistable(program.Statements))
        {
            if (stmt is OperatorOverloadDeclaration oad)
                _overloadDefs[(oad.OperandTypeName, oad.Operator)] = oad;
        }

        // Merge 'unto' methods/getters/setters (declared outside the object body) into their
        // target type's member lists. TypeChecker already validated every target exists.
        foreach (var stmt in FlattenHoistable(program.Statements))
        {
            if (stmt is BindStatement { UntoType: { } untoType } bind)
            {
                if (_objectDefs.TryGetValue(untoType, out var def))
                    _objectDefs[untoType] = def with { Methods = def.Methods.Append(bind).ToList() };
            }
            else if (stmt is GetterDeclaration { UntoType: { } gUntoType } getter)
            {
                if (_objectDefs.TryGetValue(gUntoType, out var def))
                    _objectDefs[gUntoType] = def with { Getters = def.Getters.Append(getter).ToList() };
            }
            else if (stmt is SetterDeclaration { UntoType: { } sUntoType } setter)
            {
                if (_objectDefs.TryGetValue(sUntoType, out var def))
                    _objectDefs[sUntoType] = def with { Setters = def.Setters.Append(setter).ToList() };
            }
        }

        // Hoist top-level function definitions.
        foreach (var stmt in FlattenHoistable(program.Statements))
        {
            if (stmt is BindStatement { UntoType: null } bind)
                Scope[bind.Name] = new FunctionValue
                {
                    ParameterNames = bind.Parameters.Select(p => p.Name).ToList(),
                    Body           = bind.Body,
                };
        }

        foreach (var stmt in program.Statements)
        {
            try { Execute(stmt); }
            catch (FailureUnwind fu)
            {
                throw new RuntimeException(
                    $"A failure escaped without being handled: \"{fu.Value.Message}\"" +
                    (fu.Value.Category != null ? $" (category: \"{fu.Value.Category}\")" : "") +
                    ". Use a Try block, 'but on failure', or 'or pass the failure off'.");
            }
        }
    }

    private void Execute(IStatement stmt)
    {
        switch (stmt)
        {
            case StateStatement s:
                _out.WriteLine(Format(Evaluate(s.Value)));
                break;

            case DefineStatement d:
                if (Scope.ContainsKey(d.Name))
                    throw new RuntimeException($"'{d.Name}' is already defined on line {d.Line}.");
            {
                var val = Evaluate(d.Value);
                Scope[d.Name] = val is RecordValue rv ? rv.DeepCopy() :
                                val is ObjectValue ov ? ov.DeepCopy() : val;
                _scopeDefOrder[^1].Add(d.Name);
                break;
            }

            case BecomesStatement b:
            {
                var ownerScope = FindOwningScope(b.Name);
                if (ownerScope == null)
                {
                    var suggestion = FindSuggestion(b.Name);
                    var msg = $"'{b.Name}' isn't defined on line {b.Line} — use Define to create it first, then becomes to change it.";
                    if (suggestion != null) msg += $" Did you mean '{suggestion}'?";
                    throw new RuntimeException(msg);
                }
                var val = Evaluate(b.Value);
                ownerScope[b.Name] = val is RecordValue rv ? rv.DeepCopy() :
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
                        EnterScope();
                        try { foreach (var s in arm.Body) Execute(s); }
                        finally { ExitScope(); }
                        executed = true;
                        break;
                    }
                }
                if (!executed && ifStmt.ElseBody is not null)
                {
                    EnterScope();
                    try { foreach (var s in ifStmt.ElseBody) Execute(s); }
                    finally { ExitScope(); }
                }
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
                    EnterScope();
                    bool wsStopped = false;
                    try   { foreach (var s in ws.Body) Execute(s); }
                    catch (StopException) { wsStopped = true; }
                    catch (SkipException) { /* next iteration */ }
                    finally { ExitScope(); }
                    if (wsStopped) break;
                }
                break;
            }

            case RepeatUntilStatement ru:
            {
                while (true)
                {
                    EnterScope();
                    bool stopped = false;
                    try   { foreach (var s in ru.Body) Execute(s); }
                    catch (StopException) { stopped = true; }
                    catch (SkipException) { /* fall through to condition check */ }
                    finally { ExitScope(); }
                    if (stopped) break;
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
                var saTarget = Evaluate(sa.Series);
                if (saTarget is not List<object> list)
                    throw new RuntimeException($"Expected a series for 'Add' on line {sa.Line}.");
                var value = Evaluate(sa.Value);
                if (sa.ToStart)
                    list.Insert(0, value);
                else if (sa.AfterIndex == null)
                    list.Add(value);
                else
                    list.Insert(ResolveIndex(sa.AfterIndex, list, SeriesDisplayName(sa.Series), sa.Line) + 1, value);
                break;
            }

            case SeriesRemoveAtStatement sra:
            {
                var sraTarget = Evaluate(sra.Series);
                if (sraTarget is not List<object> list)
                    throw new RuntimeException($"Expected a series for 'Remove' on line {sra.Line}.");
                list.RemoveAt(ResolveIndex(sra.Index, list, SeriesDisplayName(sra.Series), sra.Line));
                break;
            }

            case SeriesRemoveValueStatement srv:
            {
                var srvTarget = Evaluate(srv.Series);
                if (srvTarget is Dictionary<object, object> srvDict)
                {
                    var key = Evaluate(srv.Value);
                    if (!srvDict.Remove(key))
                        throw new RuntimeException($"Key not found in map on line {srv.Line}.");
                    break;
                }
                if (srvTarget is not List<object> list)
                    throw new RuntimeException($"Expected a series or map for 'Remove' on line {srv.Line}.");
                var value = Evaluate(srv.Value);
                if (!list.Remove(value))
                    throw new RuntimeException($"Value not found in {SeriesDisplayName(srv.Series)} on line {srv.Line}.");
                break;
            }

            case SeriesSetStatement ss:
            {
                var ssTarget = Evaluate(ss.Series);
                if (ssTarget is ObjectValue ssOv)
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
                if (ssTarget is RecordValue ssRrv)
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
                if (ssTarget is not List<object> list)
                    throw new RuntimeException($"Expected a series for item assignment on line {ss.Line}.");
                list[ResolveIndex(ss.Index, list, SeriesDisplayName(ss.Series), ss.Line)] = Evaluate(ss.Value);
                break;
            }

            case RecordNamedSetStatement rnss:
            {
                var recordVal = Evaluate(rnss.Record);
                if (recordVal is ObjectValue rnssOv)
                {
                    // Route through setter if one exists and we're not already inside it (bypass).
                    var rnssSetterDef = FindSetterInObjDefs(rnssOv, rnss.FieldName);
                    if (rnssSetterDef != null && _inSetterFor != (rnssOv.TypeName, rnss.FieldName))
                    {
                        ExecuteSetterMethod(rnssOv, rnssSetterDef, rnss.Value, rnss.Line);
                        break;
                    }
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

            case MapSetStatement mapSet:
                ExecuteMapSet(mapSet);
                break;

            case PossessiveSetStatement pss:
                ExecutePossessiveSet(pss);
                break;

            case ObjectDefinition:
            case InterfaceDefinition:
            case GetterDeclaration:
            case SetterDeclaration:
            case UnmakerDeclaration:
            case OperatorOverloadDeclaration:
                break; // already hoisted / no runtime action

            case BindStatement bind:
                if (_callDepth > 0)
                {
                    // Inside a function body: create a closure carrying the current environment.
                    // Capture before setting the name so we can add self-reference for recursion.
                    var capturedEnv = CaptureClosure();
                    var closureFn = new FunctionValue
                    {
                        ParameterNames = bind.Parameters.Select(p => p.Name).ToList(),
                        Body           = bind.Body,
                        CapturedEnv    = capturedEnv,
                    };
                    Scope[bind.Name]       = closureFn;
                    capturedEnv[bind.Name] = closureFn; // self-reference enables inner recursion
                }
                // else: top-level Bind, already hoisted — no action.
                break;

            case CastStatement cs:
                ExecuteCallExpr(cs.Function, cs.Args, cs.Line);
                break;

            case ReturnStatement ret:
                throw new ReturnException(ret.Value != null ? Evaluate(ret.Value) : null);

            case FileWriteStatement fw:
                ExecuteFileWriteStatement(fw);
                break;

            case WithOpenStatement wos:
                ExecuteWithOpen(wos);
                break;

            case PullRabbitStatement prs:
                ExecutePullRabbit(prs);
                break;

            case PullStatement ps:
                ExecutePullStatement(ps);
                break;

            case WriteToStreamStatement wts:
                ExecuteWriteToStream(wts);
                break;

            case TryStatement trySt:
                ExecuteTryStatement(trySt);
                break;

            case SuppressStatement:
                throw new SuppressSignal();

            case AcknowledgeInterruptStatement:
                _interruptRequested = false;
                break;

            case SeedChanceStatement ss:
                _rng = new Random((int)(decimal)Evaluate(ss.Seed));
                break;

            case ForEachStatement fe:
            {
                var seriesVal = Evaluate(fe.Series);
                string iterKey = fe.IteratorName ?? "it";

                if (seriesVal is Dictionary<object, object> dict)
                {
                    // Snapshot keys so mutation during iteration gives a clear error.
                    var snapshot = dict.ToList();
                    foreach (var kvp in snapshot)
                    {
                        if (dict.Count != snapshot.Count)
                            throw new RuntimeException(
                                $"The map was modified during a for-each loop on line {fe.Line} — use a While loop if you need to change it while looping.");
                        EnterScope();
                        Scope[iterKey] = new MappingValue(kvp.Key, kvp.Value);
                        bool stopped = false;
                        try { foreach (var s in fe.Body) Execute(s); }
                        catch (StopException) { stopped = true; }
                        catch (SkipException) { /* next iteration */ }
                        finally { ExitScope(); }
                        if (stopped) break;
                    }
                    break;
                }

                if (seriesVal is not List<object> list)
                    throw new RuntimeException($"Expected a series or map for 'for each' loop on line {fe.Line}.");
                string seriesDisplay = fe.Series is VariableReference fvr ? $"'{fvr.Name}'" : "The series";
                int startCount = list.Count;
                for (int i = 0; i < startCount; i++)
                {
                    if (list.Count != startCount)
                        throw new RuntimeException(
                            $"{seriesDisplay} was modified during a for-each loop on line {fe.Line} — use a While loop if you need to change it while looping.");
                    EnterScope();
                    Scope[iterKey] = list[i];
                    bool stopped = false;
                    try { foreach (var s in fe.Body) Execute(s); }
                    catch (StopException) { stopped = true; }
                    catch (SkipException) { /* next iteration */ }
                    finally { ExitScope(); }
                    if (stopped) break;
                    if (list.Count != startCount)
                        throw new RuntimeException(
                            $"{seriesDisplay} was modified during a for-each loop on line {fe.Line} — use a While loop if you need to change it while looping.");
                }
                break;
            }
        }
    }

    // Snapshots the full visible scope chain for a closure (outer-to-inner so inner wins).
    // Deep-copies value-typed objects (records, objects) so they're independent;
    // shares reference-typed collections (series, maps) as-is.
    private Dictionary<string, object> CaptureClosure()
    {
        var captured = new Dictionary<string, object>();
        foreach (var scope in _scopes)
            foreach (var (k, v) in scope)
                captured[k] = v is RecordValue rv ? rv.DeepCopy() :
                              v is ObjectValue ov ? ov.DeepCopy() : v;
        return captured;
    }

    private List<object> ExpectSeries(string name, int line = 0)
    {
        if (!TryLookupValue(name, out var val))
            throw new RuntimeException(UndefinedVariableMessage(name, line));
        if (val is not List<object> list)
            throw new RuntimeException(line > 0
                ? $"'{name}' isn't a series on line {line}."
                : $"'{name}' is not a series.");
        return list;
    }

    private static string SeriesDisplayName(IExpression expr) => expr switch
    {
        VariableReference vr => $"'{vr.Name}'",
        PossessiveAccess  pa => $"{SeriesDisplayName(pa.Target)}'s {pa.Member}",
        _                    => "the series",
    };

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
        VariableReference r   => TryLookupValue(r.Name, out var val)
                                     ? val
                                     : throw new RuntimeException(UndefinedVariableMessage(r.Name, r.Line)),
        UnaryExpression  u    => EvaluateUnary(u),
        BinaryExpression b    => EvaluateBinary(b),
        SeriesLiteral    sl   => (object)sl.Elements.Select(Evaluate).ToList(),
        SeriesAccess     sa   => EvaluateSeriesAccess(sa),
        SeriesLength     sl   => Evaluate(sl.Series) is List<object> slList
                                     ? (decimal)slList.Count
                                     : throw new RuntimeException($"Expected a series for 'the number of' on line {sl.Line}."),
        RecordLiteral    rl   => (object)new RecordValue(
                                     rl.PositionalFields.Select(Evaluate).ToList(),
                                     rl.NamedFields.Select(f => (f.Name, Evaluate(f.Value))).ToList()),
        RecordNamedAccess rna => EvaluateRecordNamedAccess(rna),
        ObjectLiteral    ol   => EvaluateObjectLiteral(ol),
        PossessiveAccess pa   => EvaluatePossessiveAccess(pa),
        CastExpression   cast => EvaluateCastExpr(cast),
        TextJoin   tj => EvaluateTextJoin(tj),
        TextConvert tc => (object)Format(Evaluate(tc.Value)),
        NumberConvert nc => EvaluateNumberConvert(nc),
        TextLength  tl => (object)(decimal)((string)Evaluate(tl.Target)).Length,
        TextSplit        split => EvaluateTextSplit(split),
        TextContains     tc2   => EvaluateTextContains(tc2),
        TextFind         find  => EvaluateTextFind(find),
        TextSubstringRange tsr => EvaluateTextSubstringRange(tsr),
        TextSubstringEdge  tse => EvaluateTextSubstringEdge(tse),
        TextReplace      replace => EvaluateTextReplace(replace),
        TextCase         tcase   => EvaluateTextCase(tcase),
        TextTrim         trim    => EvaluateTextTrim(trim),
        SortExpression   sort    => EvaluateSort(sort),
        RangeExpression re  => EvaluateRangeExpr(re),
        VoidLiteral        _  => VoidValue.Instance,
        FailureLiteral fl     => EvaluateFailureLiteral(fl),
        FailureFallback ff    => EvaluateFailureFallback(ff),
        FailurePropagate fp   => EvaluateFailurePropagate(fp),
        ButVoidDefault bvd    => EvaluateButVoidDefault(bvd),
        MapLiteral     ml     => EvaluateMapLiteral(ml),
        MapLookup      mlu    => EvaluateMapLookup(mlu),
        MapHasKey      mhk    => EvaluateMapHasKey(mhk),
        MapHasEntry    mhe    => EvaluateMapHasEntry(mhe),
        MapSize        ms     => EvaluateMapSize(ms),
        LambdaLiteral  lam    => EvaluateLambda(lam),
        ReadExpression re     => EvaluateReadExpr(re),
        FileReadExpression fr => EvaluateFileReadExpr(fr),
        RunExpression run     => EvaluateRunExpr(run),
        MatrixLiteral ml      => EvaluateMatrixLiteral(ml),
        MatrixSized   mz      => EvaluateMatrixSized(mz),
        MatrixAccess  ma      => EvaluateMatrixAccess(ma),
        MatrixRows    mr      => EvaluateMatrixRows(mr),
        MatrixColumns mc      => EvaluateMatrixColumns(mc),
        IsTypeCheck   tc      => EvaluateIsTypeCheck(tc),
        EnvironmentVariableExpression env => EvaluateEnvVar(env),
        DirectoryContentsExpression   dce => EvaluateDirectoryContents(dce),
        PathCheckExpression           pce => EvaluatePathCheck(pce),
        InterruptRequestedExpression      => (object)_interruptRequested,
        RandomNumber  rn                  => EvaluateRandomNumber(rn),
        RandomItem    ri                  => EvaluateRandomItem(ri),
        RandomlyShuffled rs               => EvaluateRandomlyShuffled(rs),
        RandomGuess   rg                  => (object)(_rng.Next(0, 2) == 1),
        _ => throw new InvalidOperationException($"Unknown expression type: {expr.GetType().Name}"),
    };

    private object EvaluateRandomNumber(RandomNumber rn)
    {
        var low  = (decimal)Evaluate(rn.Low);
        var high = (decimal)Evaluate(rn.High);
        if (low > high)
            throw new RuntimeException(
                $"Random number range is invalid: low ({low}) is greater than high ({high}) (line {rn.Line}).");
        var lo = (int)low;
        var hi = (int)high;
        return (object)(decimal)_rng.Next(lo, hi + 1);
    }

    private object EvaluateRandomItem(RandomItem ri)
    {
        var list = (List<object>)Evaluate(ri.Series);
        if (list.Count == 0) return VoidValue.Instance;
        return list[_rng.Next(0, list.Count)];
    }

    private object EvaluateRandomlyShuffled(RandomlyShuffled rs)
    {
        var list = (List<object>)Evaluate(rs.Series);
        var copy = new List<object>(list);
        for (int i = copy.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(0, i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return (object)copy;
    }

    private object EvaluateEnvVar(EnvironmentVariableExpression env)
    {
        var name = (string)Evaluate(env.Name)!;
        var value = System.Environment.GetEnvironmentVariable(name);
        return value ?? (object)VoidValue.Instance;
    }

    private object EvaluateReadExpr(ReadExpression re)
    {
        var sv = (ReadableStreamValue)Evaluate(re.Source);
        switch (re.Form)
        {
            case ReadForm.Line:
                // null at EOF → translate to Cufet void; null never enters the language.
                var oneLine = sv.Reader.ReadLine();
                return oneLine is null ? (object)VoidValue.Instance : oneLine;

            case ReadForm.All:
                return sv.Reader.ReadToEnd();

            case ReadForm.AllLines:
                var lineList = new List<object>();
                string? next;
                while ((next = sv.Reader.ReadLine()) != null)
                    lineList.Add((object)next);
                return lineList;

            default:
                throw new InvalidOperationException($"Unknown ReadForm {re.Form}");
        }
    }

    private object EvaluateLambda(LambdaLiteral lam) => new FunctionValue
    {
        ParameterNames = lam.Parameters.Select(p => p.Name).ToList(),
        Body           = lam.Body,
        CapturedEnv    = CaptureClosure(),
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

        if (target is MappingValue mv)
        {
            return rna.FieldName switch
            {
                "key"   => mv.Key,
                "value" => mv.Value,
                _ => throw new RuntimeException(
                    $"A mapping only has 'key' and 'value' fields (line {rna.Line}).")
            };
        }

        if (target is ObjectValue ov)
        {
            // Dispatch getter before stored field — uniform access.
            var getter = FindGetterInObjDefs(ov, rna.FieldName);
            if (getter != null) return ExecuteGetterMethod(ov, getter, rna.Line);

            if (TryFindNamedFieldValue(ov, rna.FieldName, out var found)) return found;
            throw new RuntimeException(
                $"Object of type '{ov.TypeName}' has no field or getter named '{rna.FieldName}' (line {rna.Line}).");
        }

        if (target is FailureValue fv)
        {
            return rna.FieldName switch
            {
                "message"  => (object)fv.Message,
                "category" => fv.Category != null ? (object)fv.Category : VoidValue.Instance,
                _ => throw new RuntimeException(
                    $"A failure only has 'message' and 'category' fields (line {rna.Line}).")
            };
        }

        if (target is ExceptionValue ev)
        {
            return rna.FieldName switch
            {
                "message" => (object)ev.Message,
                _ => throw new RuntimeException(
                    $"An exception only has a 'message' field (line {rna.Line}).")
            };
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

    private object EvaluateTextSplit(TextSplit split)
    {
        var text      = (string)Evaluate(split.Text);
        var delimiter = (string)Evaluate(split.Delimiter);
        if (delimiter.Length == 0)
            throw new RuntimeException($"'split by' needs a non-empty delimiter (line {split.Line}).");
        return (object)text.Split(delimiter).Select(s => (object)s).ToList();
    }

    private object EvaluateTextContains(TextContains contains)
    {
        var text = (string)Evaluate(contains.Text);
        var sub  = (string)Evaluate(contains.Substring);
        return (object)text.Contains(sub, StringComparison.Ordinal);
    }

    private object EvaluateTextFind(TextFind find)
    {
        var sub  = (string)Evaluate(find.Substring);
        var text = (string)Evaluate(find.Text);
        var idx  = text.IndexOf(sub, StringComparison.Ordinal);
        return idx < 0 ? VoidValue.Instance : (object)(decimal)(idx + 1); // 1-based
    }

    private object EvaluateTextSubstringRange(TextSubstringRange range)
    {
        var text    = (string)Evaluate(range.Text);
        var fromIdx = (int)(decimal)Evaluate(range.From); // 1-based
        if (fromIdx <= 0)
            throw new RuntimeException($"a character position must be 1 or greater — positions start at 1 (line {range.Line}).");

        var toIdx  = range.To != null ? (int)(decimal)Evaluate(range.To) : text.Length;
        var from0  = fromIdx - 1;
        var to0    = Math.Min(toIdx, text.Length) - 1; // clamp high, 0-based inclusive
        var length = to0 - from0 + 1;
        return (object)(length <= 0 ? "" : text.Substring(from0, length));
    }

    private object EvaluateTextSubstringEdge(TextSubstringEdge edge)
    {
        var text    = (string)Evaluate(edge.Text);
        var count   = (int)(decimal)Evaluate(edge.Count);
        var clamped = Math.Clamp(count, 0, text.Length);
        return (object)(edge.FromStart
            ? text.Substring(0, clamped)
            : text.Substring(text.Length - clamped, clamped));
    }

    private object EvaluateTextReplace(TextReplace replace)
    {
        var text   = (string)Evaluate(replace.Text);
        var oldStr = (string)Evaluate(replace.Old);
        var newStr = (string)Evaluate(replace.New);
        if (oldStr.Length == 0)
            throw new RuntimeException($"'replace' needs a non-empty target (line {replace.Line}).");
        return (object)text.Replace(oldStr, newStr);
    }

    private object EvaluateTextCase(TextCase tcase)
    {
        var text = (string)Evaluate(tcase.Text);
        return (object)(tcase.Uppercase ? text.ToUpperInvariant() : text.ToLowerInvariant());
    }

    private object EvaluateTextTrim(TextTrim trim)
    {
        var text = (string)Evaluate(trim.Text);
        return (object)text.Trim();
    }

    // "looks like a Cufet number literal": optional leading '-', digits, optional '.digits'.
    // Mirrors the Lexer's own number-literal acceptance (which never includes the sign — that's
    // unary minus at parse time — so the sign is added back in here for the free-standing-text case).
    private static readonly System.Text.RegularExpressions.Regex NumberLiteralPattern =
        new(@"^-?\d+(\.\d+)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private object EvaluateNumberConvert(NumberConvert nc)
    {
        var text    = (string)Evaluate(nc.Value);
        var trimmed = text.Trim();
        if (NumberLiteralPattern.IsMatch(trimmed) &&
            decimal.TryParse(trimmed, System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var result))
            return (object)result;
        return VoidValue.Instance;
    }

    private object EvaluateRangeExpr(RangeExpression re)
    {
        var startVal = Evaluate(re.Start);
        var endVal   = Evaluate(re.End);
        if (startVal is not decimal start)
            throw new RuntimeException($"range start must be a number (line {re.Line}).");
        if (endVal is not decimal end)
            throw new RuntimeException($"range end must be a number (line {re.Line}).");

        var step = 1m;
        if (re.Step != null)
        {
            var stepVal = Evaluate(re.Step);
            if (stepVal is not decimal s)
                throw new RuntimeException($"range step must be a number (line {re.Line}).");
            if (s == 0)
                throw new RuntimeException($"'counting by 0' never makes progress (line {re.Line}).");
            if (s < 0)
                throw new RuntimeException($"the step in 'counting by' must be positive (line {re.Line}).");
            step = s;
        }

        var list = new List<object>();
        if (start <= end)
            for (decimal n = start; n <= end; n += step)
                list.Add(n);
        else
            for (decimal n = start; n >= end; n -= step)
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

        // Operator overload dispatch: same-type object operands take priority over numeric path.
        if (b.Op is TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash &&
            lv2 is ObjectValue loV && rv2 is ObjectValue roV && loV.TypeName == roV.TypeName &&
            _overloadDefs.TryGetValue((loV.TypeName, b.Op), out var oad))
            return ExecuteOperatorOverload(oad, lv2, rv2, b.Line);

        // Matrix arithmetic: +, -, * built-in for (matrix, matrix) operands.
        if (b.Op is TokenType.Plus or TokenType.Minus or TokenType.Star &&
            lv2 is MatrixValue lmv && rv2 is MatrixValue rmv)
            return ExecuteMatrixOp(b.Op, lmv, rmv, b.Line);

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

    // Executes an operator overload body with `left` and `right` bound to the operand names.
    // May throw FailureUnwind if the overload returns a failure.
    private object ExecuteOperatorOverload(OperatorOverloadDeclaration oad, object left, object right, int line)
    {
        _callDepth++;
        if (_callDepth > _maxCallDepth)
        {
            _callDepth--;
            throw new RuntimeException(
                $"Operator '{OpSymbol(oad.Operator)}' overload for '{oad.OperandTypeName}' caused infinite recursion (line {line}).");
        }

        var saved = SaveScopes();
        foreach (var scope in saved.Scopes)
            foreach (var (k, v) in scope)
                if (v is FunctionValue) Scope[k] = v;
        Scope[oad.LeftName]  = left;
        Scope[oad.RightName] = right;

        object? returnValue = null;
        try
        {
            foreach (var stmt in oad.Body)
                Execute(stmt);
        }
        catch (ReturnException re)
        {
            if (re.Value is FailureValue fv)
                throw new FailureUnwind(fv);
            returnValue = re.Value;
        }
        finally
        {
            RestoreScopes(saved);
            _callDepth--;
        }

        if (returnValue == null)
            throw new RuntimeException(
                $"Operator '{OpSymbol(oad.Operator)}' overload for '{oad.OperandTypeName}' did not return a value (line {line}).");
        return returnValue;
    }

    private static string OpSymbol(TokenType op) => op switch
    {
        TokenType.Plus  => "+",
        TokenType.Minus => "-",
        TokenType.Star  => "*",
        TokenType.Slash => "/",
        _               => op.ToString()
    };

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
        foreach (var scope in _scopes)
            foreach (var key in scope.Keys)
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

    // Strips scale-only trailing zeros (e.g. 2.0 -> 2, 1.50 -> 1.5) without losing precision.
    // Decimal arithmetic preserves the operands' scale (1m + 0.5m + 0.5m == 2.0m, not 2m);
    // dividing by a maximally-scaled 1 forces .NET to re-derive the minimal exact representation.
    private static readonly decimal NormalizingDivisor = 1.0000000000000000000000000000m;
    private static decimal NormalizeDecimal(decimal d) => d / NormalizingDivisor;

    private static string Format(object val) => val switch
    {
        VoidValue        => "void",
        bool b           => b ? "true" : "false",
        decimal d        => NormalizeDecimal(d).ToString(),
        List<object> lst => "(" + string.Join(", ", lst.Select(Format)) + ")",
        FunctionValue        => "<function>",
        RabbitValue rv       => $"<rabbit {rv.Name}>",
        ReadableStreamValue  => "<readable stream of text>",
        WritableStreamValue  => "<writable stream of text>",
        RecordValue rv   => FormatRecord(rv),
        ObjectValue ov   => FormatObject(ov),
        Dictionary<object, object> dict =>
            "map {" + string.Join(", ", dict.Select(kvp => $"{Format(kvp.Key)}: {Format(kvp.Value)}")) + "}",
        MappingValue mv  => $"mapping({Format(mv.Key)}: {Format(mv.Value)})",
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

    private object EvaluateIsTypeCheck(IsTypeCheck tc)
    {
        var value = Evaluate(tc.Target);
        bool matches = RuntimeIsType(value, tc.Type);
        return (object)(tc.Negated ? !matches : matches);
    }

    private static bool RuntimeIsType(object? value, CufetType type) => type switch
    {
        NumberType      => value is decimal,
        TextType        => value is string,
        FactType        => value is bool,
        VoidType        => value is VoidValue,
        SeriesType      => value is List<object>,
        MatrixType      => value is MatrixValue,
        MapType         => value is Dictionary<object, object>,
        RecordType      => value is RecordValue,
        ObjectType ot   => value is ObjectValue ov && ov.TypeName == ot.Name,
        InterfaceType   => false, // interfaces have no runtime representation to check
        UnionType { Cases: null }      => true, // open union: any value matches
        UnionType { Cases: var cases } => cases!.Any(c => RuntimeIsType(value, c)),
        VoidableType vt => value is VoidValue || RuntimeIsType(value, vt.Inner),
        _               => false,
    };
}
