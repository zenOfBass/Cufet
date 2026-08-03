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
    private readonly TextWriter _err;
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

    private void ExecuteYield()
    {
        // Give other ready tasks one turn, then check the interrupt flag before resuming.
        _scheduler?.DrainOne();
        if (_interruptRequested)
            throw new InterruptUnwind();
    }

    // Non-null while executing a setter body: bypasses re-dispatch for the same (type, field) pair
    // so 'one's field becomes X' inside the setter does a raw write instead of recursing.
    private (string TypeName, string FieldName)? _inSetterFor = null;

    // Active cooperative scheduler — set for the duration of Execute(Program), null otherwise.
    // ExecuteLaunchTask enqueues task bodies via _scheduler.Enqueue; ExecutePullRabbit joins
    // them via _scheduler.JoinTasks before releasing the rabbit scope.
    private CufetScheduler? _scheduler;

    // Non-function top-level data hidden from the current top-level function call (set in
    // ExecuteCall when entering a top-level function from depth 0). Consulted by
    // UndefinedVariableMessage to emit a teaching error instead of a misdirecting "isn't defined".
    private Dictionary<string, object>? _hiddenTopLevelData;

    private Dictionary<string, object> Scope => _scopes[^1];

    private bool TryLookupValue(string name, out object val)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out val!)) return true;
        val = null!;
        return false;
    }

    // "Binding is binding": a value type (record/object) is COPIED at every site where it is
    // stored — Define, becomes, closure capture, argument binding, AND container insertion
    // (series/map element stores). Region types (series/maps) are shared by reference. This is
    // the single policy that keeps value semantics consistent everywhere a value comes to rest,
    // and it matches the native compiler (where a value struct copies on every store). Records
    // and objects DeepCopy so nested value fields copy too; series/maps and scalars pass through.
    // ── ISA.2d — containers that remember the element type they were declared with ────────────
    // A bare List carries no element type, so an EMPTY one cannot say whether it is a
    // `series of number` or a `series of text`. That is the last place `is a` was imprecise:
    // ISA.1 answers a NON-empty container by recursing into its elements, and ISA.2a answers from
    // the declared static type wherever there is one — but a value reached through a UNION has no
    // useful static type, and if it is also empty there is nothing left to ask.
    //
    // These subclass List/Dictionary rather than wrapping them, so every `is List<object>` and
    // `(List<object>)` site in the interpreter keeps working untouched — the carrier is additive.
    // The type is recorded at CREATION, which is the only place it is known; a container that
    // reaches `is a` without one falls back to the old vacuous answer rather than guessing.
    internal sealed class CufetSeries : List<object>
    {
        public CufetType? DeclaredElement { get; init; }
        public CufetSeries() { }
        public CufetSeries(IEnumerable<object> items) : base(items) { }
    }

    internal sealed class CufetMap : Dictionary<object, object>
    {
        public CufetType? DeclaredKey   { get; init; }
        public CufetType? DeclaredValue { get; init; }
    }

    // Builds a series carrying `elem`. Used at every site that creates one.
    private static List<object> Series(IEnumerable<object> items, CufetType? elem) =>
        new CufetSeries(items) { DeclaredElement = elem };

    // The element type a series is carrying, if any — so a derived series (sorted, shuffled,
    // unique, a channel copy) inherits it rather than losing it.
    private static CufetType? ElementTypeOf(object? v) =>
        v is CufetSeries cs ? cs.DeclaredElement : null;

    private static object BindCopy(object v) =>
        v is RecordValue rv ? rv.DeepCopy() :
        v is ObjectValue ov ? ov.DeepCopy() : v;

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

    // A bit pattern. Unsigned and at most 64 bits, which is every C flag set, file mode and
    // address there is — anything wider is cryptography or scientific computing, a different
    // domain that belongs behind the foreign function interface rather than distorting this type.
    //
    // Base and Width ride on the VALUE rather than the type, so all bits values remain mutually
    // assignable and `bits` stays a single type. Base is the display base, because a pattern
    // shows itself in the base it was written in. Width is what `not` flips within, and is the
    // only place width is load-bearing: equality compares Value alone, so 0xFF equals 0x00FF.
    private readonly record struct BitsValue(ulong Value, char Base, int Width)
    {
        // Digits are padded out to the declared width, which is what makes 0x0F print as 0x0F
        // rather than 0xF. A value that outgrew its width (0xFF + 1) prints in the smallest
        // width that holds it instead of being truncated — nothing ever falls off the end.
        public override string ToString()
        {
            int perDigit = Base switch { 'x' => 4, 'o' => 3, _ => 1 };
            int declared = (Width + perDigit - 1) / perDigit;
            string digits = Base switch
            {
                'x' => Value.ToString("X"),
                'o' => Convert.ToString((long)Value, 8),
                _   => Convert.ToString((long)Value, 2),
            };
            return $"0{Base}{digits.PadLeft(declared, '0')}";
        }
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

    public Interpreter(TextWriter? output = null, TextReader? input = null, TextWriter? error = null, int maxCallDepth = 1000)
    {
        _out = output ?? Console.Out;
        _err = error  ?? Console.Error;
        _in  = input  ?? Console.In;
        _maxCallDepth = maxCallDepth;
        _scopes[0]["input"] = new ReadableStreamValue(_in);
        // e.Cancel = true: convert Ctrl-C from "terminate process" into "set our flag."
        // The handler runs on the signal-dispatch thread; volatile bool handles the cross-thread write.
        //
        // Skipped in the browser, where Console.CancelKeyPress throws PlatformNotSupported —
        // there is no Ctrl-C to intercept. Being in the CONSTRUCTOR, it otherwise made every
        // program fail under WebAssembly while the front end worked perfectly, which is a
        // confusing shape of bug: type errors reported fine, nothing would run.
        if (!OperatingSystem.IsBrowser())
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
        _scheduler = new CufetScheduler();
        _scheduler.Run(() => { ExecuteCore(program); return Task.CompletedTask; });
        _scheduler = null;
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
                Scope[d.Name] = BindCopy(Evaluate(d.Value));
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
                ownerScope[b.Name] = BindCopy(Evaluate(b.Value));
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
                var value = BindCopy(Evaluate(sa.Value));   // value types copy on insert (binding is binding)
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
                // Remove-by-value uses value equality (the same notion as `is`), NOT reference
                // identity — a value-equal-but-distinct record/object must match. List.Remove would
                // use object.Equals (reference) for records/objects, diverging from series equality.
                int removeAt = list.FindIndex(e => ValuesEqual(e, value));
                if (removeAt < 0)
                    throw new RuntimeException($"Value not found in {SeriesDisplayName(srv.Series)} on line {srv.Line}.");
                list.RemoveAt(removeAt);
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
                list[ResolveIndex(ss.Index, list, SeriesDisplayName(ss.Series), ss.Line)] = BindCopy(Evaluate(ss.Value));
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

            case CurrentDirectorySetStatement cd:
                ExecuteCurrentDirectorySetStatement(cd);
                break;
            case FileWriteStatement fw:
                ExecuteFileWriteStatement(fw);
                break;

            case WithOpenStatement wos:
                ExecuteWithOpen(wos);
                break;

            case PullRabbitStatement prs:
                ExecutePullRabbit(prs);
                break;

            case LaunchTaskStatement lts:
                ExecuteLaunchTask(lts);
                break;

            case SendStatement ss:
                ExecuteSendStatement(ss);
                break;

            case CloseStatement cs:
                ExecuteCloseStatement(cs);
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

            case YieldStatement:
                ExecuteYield();
                break;

            case SeedChanceStatement ss:
                _rng = new Random((int)(decimal)Evaluate(ss.Seed));
                break;

            case PipeExpression pipe:
                ExecutePipe(pipe);
                break;

            case OutputStatement os:
                ExecuteOutputStatement(os);
                break;

            case ForEachFromInputStatement fe2:
                ExecuteForEachFromInput(fe2);
                break;

            case ForEachStatement fe:
            {
                var seriesVal = Evaluate(fe.Series);
                string iterKey = fe.IteratorName ?? "it";
                string? collectionDisplay = fe.Series is VariableReference dvr ? $"'{dvr.Name}'" : null;

                if (seriesVal is Dictionary<object, object> dict)
                {
                    // Snapshot keys so mutation during iteration gives a clear error.
                    var snapshot = dict.ToList();
                    foreach (var kvp in snapshot)
                    {
                        if (dict.Count != snapshot.Count)
                            throw new RuntimeException(
                                $"{collectionDisplay ?? "The map"} was modified during a for-each loop on line {fe.Line} — collect into a separate series, or use a While loop if you need to change it while looping.");
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
                string seriesDisplay = collectionDisplay ?? "The series";
                int startCount = list.Count;
                for (int i = 0; i < startCount; i++)
                {
                    if (list.Count != startCount)
                        throw new RuntimeException(
                            $"{seriesDisplay} was modified during a for-each loop on line {fe.Line} — collect into a separate series, or use a While loop if you need to change it while looping.");
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
                            $"{seriesDisplay} was modified during a for-each loop on line {fe.Line} — collect into a separate series, or use a While loop if you need to change it while looping.");
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
                captured[k] = BindCopy(v);
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
        BitsLiteral      b    => new BitsValue(b.Value, b.Base, b.Width),
        BitsShift        bs   => EvaluateBitsShift(bs),
        StringLiteral    s    => s.Value,
        BooleanLiteral   b    => (object)b.Value,
        VariableReference r   => TryLookupValue(r.Name, out var val)
                                     ? val
                                     : throw new RuntimeException(UndefinedVariableMessage(r.Name, r.Line)),
        UnaryExpression  u    => EvaluateUnary(u),
        BinaryExpression b    => EvaluateBinary(b),
        // ISA.2d — the literal's Annotation is the declared element type, and it is REQUIRED for an
        // empty literal, so `a series of text with ()` always knows what it holds.
        SeriesLiteral    sl   => Series(sl.Elements.Select(e => BindCopy(Evaluate(e))), sl.Annotation),
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
        BitsConvert bc   => EvaluateBitsConvert(bc),
        // Code points, not .NET's UTF-16 code units — see TextPositions for why the language
        // picks a unit rather than inheriting each backend's storage.
        TextLength  tl => (object)(decimal)TextPositions.Length((string)Evaluate(tl.Target)),
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
        IsTypeCheck   tc      => EvaluateIsTypeCheck(tc),
        EnvironmentVariableExpression env => EvaluateEnvVar(env),
        CurrentDirectoryExpression => EvaluateCurrentDirectory(),
        DirectoryContentsExpression   dce => EvaluateDirectoryContents(dce),
        PathCheckExpression           pce => EvaluatePathCheck(pce),
        InterruptRequestedExpression      => (object)_interruptRequested,
        RandomNumber  rn                  => EvaluateRandomNumber(rn),
        RandomItem    ri                  => EvaluateRandomItem(ri),
        RandomlyShuffled rs               => EvaluateRandomlyShuffled(rs),
        RandomGuess   rg                  => (object)(_rng.Next(0, 2) == 1),
        ChannelCreation cc                => EvaluateChannelCreation(cc),
        DeliveryExpression de             => EvaluateDeliveryExpression(de),
        AwaitedResultExpression are       => EvaluateAwaitedResultExpression(are),
        PipeExpression pipe               => EvaluatePipeExpr(pipe),
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
        var copy = new List<object>(list);   // ISA.2d: rebuilt as a carrier on return
        for (int i = copy.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(0, i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return Series(copy, ElementTypeOf(list));   // ISA.2d — a shuffle keeps the element type
    }

    private object EvaluateEnvVar(EnvironmentVariableExpression env)
    {
        var name = (string)Evaluate(env.Name)!;
        var value = System.Environment.GetEnvironmentVariable(name);
        return value ?? (object)VoidValue.Instance;
    }

    // `the current directory` → voidable text. Void is the pathological case only: the process's
    // working directory was deleted while it was running, which POSIX getcwd reports as an error
    // and .NET surfaces as an exception. Every ordinary program gets a value.
    private object EvaluateCurrentDirectory()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VoidValue.Instance;
        }
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
                return Series(lineList, new TextType());   // ISA.2d

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

        // 'the rows of m' / 'the columns of m' — the same named-access shape a record uses,
        // resolved by the target's type rather than by reserving the two words.
        if (target is MatrixValue mxv)
        {
            return rna.FieldName switch
            {
                "rows"    => (object)(decimal)mxv.Rows,
                "columns" => (object)(decimal)mxv.Cols,
                _ => throw new RuntimeException(
                    $"A matrix only has 'rows' and 'columns' (line {rna.Line}).")
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
        return Series(text.Split(delimiter).Select(s => (object)s), new TextType());   // ISA.2d
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
        // Ordinal search is correct as-is — UTF-8 and UTF-16 both find the same occurrence — but
        // the OFFSET it reports is in UTF-16 code units, and a Cufet position is in code points.
        var idx  = text.IndexOf(sub, StringComparison.Ordinal);
        return idx < 0 ? VoidValue.Instance : (object)(decimal)(TextPositions.IndexAt(text, idx) + 1); // 1-based
    }

    private object EvaluateTextSubstringRange(TextSubstringRange range)
    {
        var text    = (string)Evaluate(range.Text);
        var fromIdx = (int)(decimal)Evaluate(range.From); // 1-based
        if (fromIdx <= 0)
            throw new RuntimeException($"a character position must be 1 or greater — positions start at 1 (line {range.Line}).");

        // Every count here is in code points, so the arithmetic is unchanged and only the unit
        // moved. TextPositions.Slice does the conversion to UTF-16 offsets at the last moment.
        var chars  = TextPositions.Length(text);
        var toIdx  = range.To != null ? (int)(decimal)Evaluate(range.To) : chars;
        var from0  = fromIdx - 1;
        var to0    = Math.Min(toIdx, chars) - 1; // clamp high, 0-based inclusive
        var length = to0 - from0 + 1;
        return (object)TextPositions.Slice(text, from0, length);
    }

    private object EvaluateTextSubstringEdge(TextSubstringEdge edge)
    {
        var text    = (string)Evaluate(edge.Text);
        var count   = (int)(decimal)Evaluate(edge.Count);
        var chars   = TextPositions.Length(text);
        var clamped = Math.Clamp(count, 0, chars);
        return (object)(edge.FromStart
            ? TextPositions.Slice(text, 0, clamped)
            : TextPositions.Slice(text, chars - clamped, clamped));
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
        // Evaluated ONCE, then its type picks the path — re-evaluating inside a pattern test
        // would run the operand's side effects twice.
        var value = Evaluate(nc.Value);

        // Bits to number is total — 64 bits always fits a 96-bit mantissa — so it yields the
        // quantity directly rather than a voidable.
        if (value is BitsValue bits) return (object)(decimal)bits.Value;

        var text    = (string)value;
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
        // ISA.2d — a descending or zero-width range yields an EMPTY series of number.
        if (start <= end)
            for (decimal n = start; n <= end; n += step)
                list.Add(n);
        else
            for (decimal n = start; n >= end; n -= step)
                list.Add(n);
        return Series(list, new NumberType());   // ISA.2d
    }

    private object EvaluateUnary(UnaryExpression u)
    {
        if (u.Op == TokenType.Not)
        {
            var val = Evaluate(u.Operand);
            // On a bit pattern, flip every bit WITHIN ITS OWN WIDTH: 'not 0xFF' is 0x00, and
            // 'not 0b1010' is 0b0101. That the type is unsigned and carries a width is exactly
            // why those are the answers, instead of the -6 a signed reading would produce.
            // The width is unchanged — flipping bits cannot need more of them.
            if (val is BitsValue bits)
            {
                ulong mask = bits.Width >= 64 ? ulong.MaxValue : (1UL << bits.Width) - 1;
                return new BitsValue(~bits.Value & mask, bits.Base, bits.Width);
            }
            if (val is not bool b)
                throw new RuntimeException($"'not' requires a true-or-false value or a bits value (line {u.Line}).");
            return (object)!b;
        }
        return (object)(-ToNumber(Evaluate(u.Operand), "unary -"));
    }

    // The width a gate's result carries: the LEFT operand's, widened when the value needs more.
    // Left because in real bit code the left operand is the accumulator — `flags or MASK`,
    // `flags and not MASK` — so it is the thing you care about and will print. Widening rather
    // than truncating means nothing ever silently falls off the end; narrow deliberately with
    // an `and` if that is what you want.
    private static BitsValue Combine(BitsValue left, ulong result)
        => new(result, left.Base, Math.Max(left.Width, MinimumWidth(result)));

    private static int MinimumWidth(ulong v)
    {
        int bits = 0;
        while (v != 0) { bits++; v >>= 1; }
        return bits;
    }

    // <number> converted to hex|binary|octal.
    //
    // The width is the smallest that holds the value, rounded up to whole digits of the target
    // base — so 255 becomes 0xFF and 16 becomes 0x10. Raises rather than yielding a voidable,
    // matching arithmetic overflow: the failures here are programming errors, not the data
    // condition that makes text-to-number voidable.
    private object EvaluateBitsConvert(BitsConvert convert)
    {
        decimal raw = ToNumber(Evaluate(convert.Target), "converted to bits");

        if (raw % 1 != 0)
            throw new RuntimeException(
                $"only a whole number can become a bit pattern, and {raw} is not one (line {convert.Line}).");
        if (raw < 0)
            throw new RuntimeException(
                $"{raw} is negative, and bit patterns are unsigned (line {convert.Line}).");
        if (raw > ulong.MaxValue)
            throw new RuntimeException(
                $"{raw} does not fit in 64 bits (line {convert.Line}).");

        ulong value = (ulong)raw;
        int perDigit = convert.ToBase switch { 'x' => 4, 'o' => 3, _ => 1 };
        int minimum  = Math.Max(MinimumWidth(value), 1);
        // Round the width up to whole digits, so the display has no partial leading digit.
        int width    = (minimum + perDigit - 1) / perDigit * perDigit;
        return new BitsValue(value, convert.ToBase, width);
    }

    // <bits> shifted left|right by <number>.
    //
    // Shifting LEFT widens, so nothing is lost — until the 64-bit ceiling, where the bits that
    // would leave have nowhere to go and it raises, like a multiply overflow.
    //
    // Shifting RIGHT discards the low bits. That is the one place something genuinely falls off,
    // and it is not an inconsistency: discarding them IS the operation, not a failure of
    // representation. Bits being unsigned also means there is no arithmetic-versus-logical
    // question here — no sign bit, so only one answer.
    private object EvaluateBitsShift(BitsShift shift)
    {
        if (Evaluate(shift.Target) is not BitsValue bits)
            throw new RuntimeException($"only a bits value can be shifted (line {shift.Line}).");

        decimal raw = ToNumber(Evaluate(shift.Amount), "shifted by");
        if (raw % 1 != 0)
            throw new RuntimeException(
                $"the shift amount must be a whole number of positions, not {raw} (line {shift.Line}).");
        if (raw < 0)
            throw new RuntimeException(
                $"the shift amount cannot be negative — shift the other way instead (line {shift.Line}).");

        int by = raw > 64 ? 65 : (int)raw;   // anything past the ceiling behaves the same

        if (!shift.Left)
            return new BitsValue(by >= 64 ? 0 : bits.Value >> by, bits.Base, bits.Width);

        // Left: refuse to drop bits off the top rather than wrapping silently.
        if (by >= 64 && bits.Value != 0
            || by < 64 && bits.Value > ulong.MaxValue >> by)
            throw new RuntimeException(
                $"{bits} shifted left by {raw} does not fit in 64 bits (line {shift.Line}).");

        return Combine(bits, by >= 64 ? 0 : bits.Value << by);
    }

    // Arithmetic on bit patterns. Division is integer division, and the type is unsigned with a
    // 64-bit ceiling — so a result that would go negative or need a 65th bit has no
    // representation. Those RAISE, like division by zero already does, rather than becoming
    // value-level failures: a failure would ride in the type as `bits or failure` and force an
    // unwrap after every masking expression, which is precisely why divide-by-zero is not one.
    private static object EvaluateBitsArithmetic(TokenType op, BitsValue l, BitsValue r, int line)
    {
        switch (op)
        {
            case TokenType.Plus:
            {
                if (l.Value > ulong.MaxValue - r.Value)
                    throw new RuntimeException(
                        $"{l} + {r} does not fit in 64 bits (line {line}).");
                return Combine(l, l.Value + r.Value);
            }
            case TokenType.Minus:
            {
                if (r.Value > l.Value)
                    throw new RuntimeException(
                        $"{l} - {r} would be negative, and bits are unsigned (line {line}).");
                return Combine(l, l.Value - r.Value);
            }
            case TokenType.Star:
            {
                if (l.Value != 0 && r.Value > ulong.MaxValue / l.Value)
                    throw new RuntimeException(
                        $"{l} * {r} does not fit in 64 bits (line {line}).");
                return Combine(l, l.Value * r.Value);
            }
            case TokenType.Slash:
                if (r.Value == 0) throw new RuntimeException($"Division by zero on line {line}.");
                return Combine(l, l.Value / r.Value);
            case TokenType.Percent:
                if (r.Value == 0) throw new RuntimeException($"Modulo by zero on line {line}.");
                return Combine(l, l.Value % r.Value);

            // Ordering is on the value, so it ignores base and width just as equality does.
            case TokenType.Lt:  return (object)(l.Value <  r.Value);
            case TokenType.Gt:  return (object)(l.Value >  r.Value);
            case TokenType.Lte: return (object)(l.Value <= r.Value);
            case TokenType.Gte: return (object)(l.Value >= r.Value);
        }
        throw new RuntimeException($"'{op}' does not work on bits (line {line}).");
    }

    private object EvaluateBinary(BinaryExpression b)
    {
        // The gates. The left operand is evaluated ONCE here and the type it produces picks the
        // strategy — evaluating it inside a pattern test per branch would run its side effects
        // more than once.
        if (b.Op is TokenType.And or TokenType.Or or TokenType.Xor)
        {
            var opName = b.Op == TokenType.And ? "and" : b.Op == TokenType.Or ? "or" : "xor";
            var lv = Evaluate(b.Left);

            // On bit patterns, no short-circuit is possible — you need both patterns in hand to
            // combine them. Same word, different evaluation strategy chosen by type: the same
            // deliberate exception matrix arithmetic already makes for '+' and '*'.
            if (lv is BitsValue lbits)
            {
                if (Evaluate(b.Right) is not BitsValue rbits)
                    throw new RuntimeException($"'{opName}' needs a bits value on both sides (line {b.Line}).");
                return Combine(lbits, b.Op switch
                {
                    TokenType.And => lbits.Value & rbits.Value,
                    TokenType.Or  => lbits.Value | rbits.Value,
                    _             => lbits.Value ^ rbits.Value,
                });
            }

            if (lv is not bool lb)
                throw new RuntimeException($"'{opName}' requires true-or-false values on both sides (line {b.Line}).");

            // xor never short-circuits, on facts either: both sides always decide the answer.
            if (b.Op == TokenType.Xor)
            {
                if (Evaluate(b.Right) is not bool rxb)
                    throw new RuntimeException($"'xor' requires true-or-false values on both sides (line {b.Line}).");
                return (object)(lb ^ rxb);
            }

            // Short-circuit: evaluate right only when the left doesn't decide the result.
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

        // Bit-pattern arithmetic and ordering. Before the numeric switch, which would otherwise
        // try to read a BitsValue as a decimal.
        if (lv2 is BitsValue lbv && rv2 is BitsValue rbv
            && b.Op is TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash
                     or TokenType.Percent or TokenType.Lt or TokenType.Gt or TokenType.Lte or TokenType.Gte)
            return EvaluateBitsArithmetic(b.Op, lbv, rbv, b.Line);

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

        // Bit patterns compare by VALUE, ignoring base and width — 0xFF, 0x00FF and 0b11111111
        // are all the same pattern, differing only in how they were written and displayed.
        // Without this the record struct's own equality would compare all three fields and call
        // them different, which is the one place width must NOT be load-bearing.
        if (a is BitsValue ab && b is BitsValue bb) return ab.Value == bb.Value;

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
        var located = line > 0 ? $" on line {line}" : "";

        if (_hiddenTopLevelData != null && _hiddenTopLevelData.ContainsKey(name))
            return $"'{name}' is a top-level value, but top-level functions can't see top-level data{located}.\n\n" +
                   $"Top-level functions see other functions (for mutual recursion) but not top-level data.\n" +
                   $"This keeps data flow explicit and prevents hidden mutations — the same principle behind Cufet's message-passing model.\n\n" +
                   $"Fix: pass '{name}' as a parameter:\n" +
                   $"    Bind void to your-function, given (the <type> {name}): ...\n" +
                   $"Or define your function inside a scope where '{name}' is already bound, so it captures '{name}' as a closure.";

        var suggestion = FindSuggestion(name);
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
        BitsValue bv     => bv.ToString(),   // prints in the base it was written in
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
        MatrixValue mx   => FormatMatrix(mx),
        _                => val.ToString()!,
    };

    // matrix((1, 2), (3, 4)) — mirrors the literal syntax (rows as parenthesized lists), the same
    // convention as record(...)/mapping(...). Added when the native compiler gained matrices: the
    // previous fallthrough leaked the host type name (Cufet.Interpreter.Interpreter+MatrixValue).
    private static string FormatMatrix(MatrixValue mx)
    {
        var sb = new System.Text.StringBuilder("matrix(");
        for (int r = 1; r <= mx.Rows; r++)
        {
            if (r > 1) sb.Append(", ");
            sb.Append('(');
            for (int c = 1; c <= mx.Cols; c++)
            {
                if (c > 1) sb.Append(", ");
                sb.Append(Format(mx.GetItem(r, c)));
            }
            sb.Append(')');
        }
        return sb.Append(')').ToString();
    }

    // Named fields print sorted by name (Ordinal) so that structurally-equal records —
    // which are order-insensitive by type — always print identically regardless of the
    // order fields were written at construction. This also makes the native compiler's
    // struct-based representation exact (one struct per shape, canonical field order).
    private static string FormatRecord(RecordValue rv)
    {
        var parts = new List<string>();
        foreach (var v in rv.PositionalFields)                                        parts.Add(Format(v));
        foreach (var (name, v) in rv.NamedFields.OrderBy(f => f.Name, StringComparer.Ordinal)) parts.Add($"{name}: {Format(v)}");
        return "record(" + string.Join(", ", parts) + ")";
    }

    // Same canonical-order rule as records: named fields sorted by name so that equal
    // objects print identically no matter the construction order.
    private static string FormatObject(ObjectValue ov)
    {
        var parts = new List<string>();
        foreach (var v in ov.PositionalFields)                                        parts.Add(Format(v));
        foreach (var (name, v) in ov.NamedFields.OrderBy(f => f.Name, StringComparer.Ordinal)) parts.Add($"{name}: {Format(v)}");
        if (ov.EmbeddedObject != null)                                                parts.Add(Format(ov.EmbeddedObject));
        return $"{ov.TypeName}(" + string.Join(", ", parts) + ")";
    }

    // ISA.2a — `is a` is answered TYPE-DIRECTED, mirroring the compiler exactly: the declared type
    // decides the type comparison, and only the predicate a declared type genuinely cannot answer
    // stays dynamic. A runtime value cannot answer for an EMPTY container (a bare List carries no
    // element type) but the declared type always can — that is what closes the empty-container
    // divergence. Value-directed evaluation remains ONLY where the static type is deliberately
    // imprecise about the runtime case:
    //   • UNION operands   — the concrete case is knowable only at runtime (the compiler reads its
    //                        tag; container-vs-container narrowing is refused compiler-side, ISA.2c).
    //   • INTERFACE operands — the conformer varies per call site (the compiler monomorphizes).
    //   • no recorded type — the checker couldn't determine it; fall back rather than guess.
    private object EvaluateIsTypeCheck(IsTypeCheck tc)
    {
        var value = Evaluate(tc.Target);
        var s = tc.StaticTargetType;
        bool matches;

        if (s is null or UnionType or InterfaceType)
        {
            matches = RuntimeIsType(value, tc.Type);
        }
        else if (s is VoidableType sv)
        {
            // Void-ness is the one genuinely runtime predicate here; the inner comparison is static.
            bool isVoid = value is VoidValue;
            matches = tc.Type switch
            {
                VoidType        => isVoid,
                VoidableType tv => isVoid || StaticMatch(sv.Inner, tv.Inner),   // void matches any voidable
                _               => !isVoid && StaticMatch(sv.Inner, tc.Type),
            };
        }
        else
        {
            // Concrete declared type ⇒ fully statically decided (this is exactly what the compiler folds).
            matches = tc.Type is VoidableType tv2 ? StaticMatch(s, tv2.Inner) : StaticMatch(s, tc.Type);
        }
        return (object)(tc.Negated ? !matches : matches);
    }

    // Does a value whose DECLARED type is `s` satisfy `is a t`? Mirrors the compiler's
    // StaticKindMatches one-for-one (element-aware containers, nominal objects, nested unions
    // structural) — the two must agree, so keep them in step.
    private static bool StaticMatch(CufetType s, CufetType t) => t switch
    {
        NumberType    => s is NumberType,
        TextType      => s is TextType,
        FactType      => s is FactType,
        SeriesType ts => s is SeriesType ss && StaticMatch(ss.ElementType, ts.ElementType),
        MapType tm    => s is MapType sm && StaticMatch(sm.KeyType, tm.KeyType)
                                        && StaticMatch(sm.ValueType, tm.ValueType),
        RecordType    => s is RecordType,
        MatrixType    => s is MatrixType,
        ObjectType to => s is ObjectType so && so.Name == to.Name,   // nominal
        VoidType      => false,          // a non-voidable declared type is never void
        // A concrete value satisfies `voidable X` exactly when it satisfies X.
        VoidableType tv => StaticMatch(s, tv.Inner),
        UnionType     => s is UnionType && s.Equals(t),
        _             => false,
    };

    // ISA.1 — ELEMENT-AWARE for containers. `is a` used to be kind-erased: a `series of text`
    // matched `is a series of number` (and a map matched any map), which is simply false — the
    // interpreter survived it (reading an element just returns the text) but it is a latent language
    // bug, and a compiler cannot survive it (it would reinterpret the payload at the annotated type).
    // Now a container matches only when EVERY element matches the annotated element type, recursively
    // (so `series of series of text` vs `series of series of number` resolves too).
    // ★ EMPTY CONTAINERS ARE DELIBERATELY UNCHANGED: `All` on an empty collection is vacuously true,
    // which is exactly today's permissive answer. That boundary is ISA.2's decision to settle — it is
    // SAFE either way, because misreading a payload requires an element and an empty one has none.
    private static bool RuntimeIsType(object? value, CufetType type) => type switch
    {
        NumberType      => value is decimal,
        TextType        => value is string,
        FactType        => value is bool,
        VoidType        => value is VoidValue,
        // ISA.2d — a non-empty container is answered by its elements (ISA.1). An EMPTY one has no
        // element to ask, so it answers from the type it was created with; only a container that
        // reached here without a carrier falls back to the old vacuously-true answer.
        SeriesType st   => value is List<object> sl
                           && (sl.Count > 0
                                 ? sl.All(e => RuntimeIsType(e, st.ElementType))
                                 : ElementTypeOf(sl) is not { } de || StaticMatch(de, st.ElementType)),
        MatrixType      => value is MatrixValue,
        MapType mt      => value is Dictionary<object, object> md
                           && (md.Count > 0
                                 ? md.All(kv => RuntimeIsType(kv.Key, mt.KeyType)
                                             && RuntimeIsType(kv.Value, mt.ValueType))
                                 : md is not CufetMap cm || cm.DeclaredKey is not { } dk
                                     || cm.DeclaredValue is not { } dv
                                     || (StaticMatch(dk, mt.KeyType) && StaticMatch(dv, mt.ValueType))),
        RecordType      => value is RecordValue,
        ObjectType ot   => value is ObjectValue ov && ov.TypeName == ot.Name,
        InterfaceType   => false, // interfaces have no runtime representation to check
        UnionType { Cases: null }      => true, // open union: any value matches
        UnionType { Cases: var cases } => cases!.Any(c => RuntimeIsType(value, c)),
        VoidableType vt => value is VoidValue || RuntimeIsType(value, vt.Inner),
        ChannelType     => value is ChannelValue,
        TaskHandleType  => value is TaskHandle,
        _               => false,
    };
}
