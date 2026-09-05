using System.Runtime.CompilerServices;
using Cufet.Lexer;
using System.Globalization;

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

    // ⚠⚠ Non-zero while a launched child HAS THIS TERMINAL. An interrupt arriving then is the
    // child’s — the terminal signalled it too — so this program neither records it nor counts it
    // toward the second-press escape below. Without this a shell dies on the second Ctrl-C of any
    // child, because it cannot acknowledge anything until the child it is waiting for is gone.
    //
    // Written on the interpreter thread, read on the signal-dispatch thread; Interlocked because,
    // unlike the single-writer flag above, a task could be launching one while another finishes.
    private volatile int _childHasTerminal;

    internal void EnterForegroundChild() => Interlocked.Increment(ref _childHasTerminal);
    internal void LeaveForegroundChild() => Interlocked.Decrement(ref _childHasTerminal);

    // Allow tests to set the interrupt flag directly without synthesizing a real Ctrl-C.
    internal void SimulateInterrupt() => _interruptRequested = true;

    /// <summary>Whether a Ctrl-C is recorded and still unacknowledged. For tests.</summary>
    internal bool InterruptIsPending => _interruptRequested;

    // Whether the program said anything about interrupts — `an interrupt is requested` or
    // `Acknowledge the interrupt.` anywhere in it. Set once from the AST before execution, and read
    // by the checkpoint in Execute to decide whether Ctrl-C unwinds or is left for the program to
    // notice. A whole-program property on purpose: a rule that changed with position would be
    // impossible to reason about from the terminal, where all you know is that you pressed Ctrl-C.
    private bool _programHandlesInterrupts;

    // The compiler asks the same question with the same walk (ProgramUsesSignals), which is what
    // keeps the two backends' answers identical.
    private static bool MentionsInterrupts(object? node) =>
        AstSearch.Contains(node,
            n => n is InterruptRequestedExpression or AcknowledgeInterruptStatement);

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

    // Names of top-level `permanently` bindings — the shared constants a detached body may read.
    // Kept by name because the isolation below filters the caller's scopes by VALUE, and an
    // evaluated constant looks like any other datum.
    private readonly HashSet<string> _permanentTopLevel = new(StringComparer.Ordinal);

    // ★ The ONE place the runtime's top-level visibility rule lives — the mirror of the checker's
    // TypeChecker.ImportTopLevelVisible, which decides legality; this keeps the runtime in step.
    // Every body that runs detached from the top-level scope — a top-level function, a method, a
    // getter, a setter, a destructor, an operator overload — imports exactly this: function values
    // (so mutual recursion resolves) and `permanently` constants. A `permanently` binding cannot be
    // mutated, so sharing it cannot reintroduce the hidden mutation the isolation exists to prevent.
    //
    // On first entry from the top level it also records what was filtered OUT, so
    // UndefinedVariableMessage can teach instead of misdirecting with "isn't defined".
    //
    // ⚠ Callers must save _hiddenTopLevelData before calling and restore it in their finally.
    private void ImportTopLevelVisible(List<Dictionary<string, object>> savedScopes)
    {
        foreach (var scope in savedScopes)
            foreach (var (k, v) in scope)
                if (v is FunctionValue || _permanentTopLevel.Contains(k)) Scope[k] = v;

        if (_callDepth != 1 || savedScopes.Count == 0) return;
        _hiddenTopLevelData = new Dictionary<string, object>();
        foreach (var (k, v) in savedScopes[0])
            if (v is not FunctionValue && !_permanentTopLevel.Contains(k))
                _hiddenTopLevelData[k] = v;
    }

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

    /// <summary>A mutable character buffer — the runtime side of `chase`.</summary>
    /// <remarks>
    /// ⚠⚠ CODE POINTS, not chars, and that is not an implementation detail. A C# string is UTF-16,
    /// so an astral character is two chars and `item 2 of` would land inside one; the compiled side
    /// stores UTF-32, where it does not. Two backends disagreeing about what "the second character"
    /// means is precisely the divergence the oracle exists to catch, and storing the same unit here
    /// is what stops it arising.
    /// </remarks>
    internal sealed class CufetChase : List<int>
    {
        /// <summary>The characters as ordinary text — the explicit copy `converted to text` makes.</summary>
        public string AsText() => string.Concat(this.Select(char.ConvertFromUtf32));

        /// <summary>Appends every character of a text. The one way anything gets in.</summary>
        public void Append(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                int point = char.ConvertToUtf32(text, i);
                Add(point);
                if (char.IsHighSurrogate(text[i])) i++;   // the pair was one character
            }
        }
    }

    internal sealed class CufetMap : Dictionary<object, object>
    {
        public CufetType? DeclaredKey   { get; init; }
        public CufetType? DeclaredValue { get; init; }
    }

    // ── How a map compares its keys ──────────────────────────────────────────────────────────
    //
    // ★ A map is a Dictionary, so it asks two questions of every key: are these equal, and what is
    // the hash. The DEFAULT comparer answers the first with `object.Equals`, which for a record is
    // REFERENCE equality — two records holding the same numbers are different keys, and a lookup
    // silently misses. That is the failure the old refusal warned about; it was real, and it was
    // about how RecordValue is written rather than about what a record IS.
    //
    // ★ Equality here is `ValuesEqual`, the same function `is` and `=` use. One definition, so a
    // key that the language calls equal is a key the map finds. Anything else is a divergence
    // waiting: the compiler's map does a linear scan calling its own `_eq`, which already compares
    // records field by field, so the two agree only if this side asks the same question.
    //
    // ⚠⚠ THE HASH IS THE DANGEROUS HALF, and it has one job: values `ValuesEqual` calls equal MUST
    // hash the same. Hashing more than equality looks at is the silent-wrong-answer bug — the map
    // finds nothing and reports no error. `bits` is the trap: `ValuesEqual` compares bit patterns
    // on VALUE ALONE, ignoring base and width, so 0xFF and 0b11111111 are one key and the hash may
    // not read the other two fields. A test locks the pair together.
    internal sealed class CufetKeyComparer : IEqualityComparer<object>
    {
        public static readonly CufetKeyComparer Instance = new();

        public new bool Equals(object? a, object? b) => ValuesEqual(a, b);

        public int GetHashCode(object value) => HashOf(value);

        private static int HashOf(object? value) => value switch
        {
            null              => 0,
            VoidValue         => 1,
            // Value only — see the warning above. Reading Base or Width here would give 0xFF and
            // 0b11111111 two hashes for one key.
            BitsValue bits    => bits.Value.GetHashCode(),
            RecordValue rec   => HashOfRecord(rec),
            ObjectValue obj   => HashOfObject(obj),
            // A series is not a legal key, but ValuesEqual compares one structurally, so anything
            // that reaches here through an untyped path must still hash consistently with that.
            List<object> list => list.Aggregate(17, (acc, item) => acc * 31 + HashOf(item)),
            _                 => value.GetHashCode(),
        };

        // ⚠ Named fields are ORDER-INDEPENDENT under ValuesEqual — it sorts both sides by name
        // before comparing — so they are combined with a commutative operation here. Folding them
        // in positionally would give two equal records two hashes.
        private static int HashOfRecord(RecordValue rec)
        {
            int hash = rec.PositionalFields.Aggregate(19, (acc, f) => acc * 31 + HashOf(f));
            foreach (var (name, value) in rec.NamedFields)
                hash ^= name.GetHashCode() * 31 + HashOf(value);
            return hash;
        }

        private static int HashOfObject(ObjectValue obj)
        {
            int hash = obj.PositionalFields.Aggregate(obj.TypeName.GetHashCode(), (acc, f) => acc * 31 + HashOf(f));
            foreach (var (name, value) in obj.NamedFields)
                hash ^= name.GetHashCode() * 31 + HashOf(value);
            return hash ^ HashOf(obj.EmbeddedObject);
        }
    }

    /// <summary>A new, empty map — the ONE place a map is created, so every one compares alike.</summary>
    internal static Dictionary<object, object> NewMap() => new(CufetKeyComparer.Instance);

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
        _scopeReleaseBase.Add(_pendingReleases.Count);
    }

    private void ExitScope()
    {
        RunScopeUnmakers(_scopeDefOrder[^1], _scopes[^1]);
        RunForeignReleases(_scopeReleaseBase[^1]);
        _scopeReleaseBase.RemoveAt(_scopeReleaseBase.Count - 1);
        _scopes.RemoveAt(_scopes.Count - 1);
        _scopeDefOrder.RemoveAt(_scopeDefOrder.Count - 1);
    }

    // ★ Book bindings survive function isolation, matching TypeChecker.SaveScopes — see the note
    // there. A pulled book is a lexical capability, not a local: `Pull a book on math.` is in scope
    // for everything written in that block, functions included. Dropping it here type-checked fine
    // and then failed at RUNTIME with "'math' isn't defined", which reads like the pull never
    // happened. Both halves had to move together; fixing only the checker turned a static error
    // into a runtime one.
    private (List<Dictionary<string, object>> Scopes, List<List<string>> DefOrders) SaveScopes()
    {
        var saved = (_scopes.ToList(), _scopeDefOrder.ToList());

        var fresh = new Dictionary<string, object> { ["input"] = new ReadableStreamValue(_in) };
        // Outermost-first so a nearer pull's alias wins, matching ordinary lookup.
        //
        // ★ Any PULLED module, not just a book. A pulled module is a lexical capability rather
        // than a local, so it reaches into a function written in its block — and a writer's own
        // module is one on exactly the same terms. Asking `is BookValue` carried books and left a
        // writer's module behind, which the checker used to hide by refusing it outright.
        foreach (var scope in saved.Item1)
            foreach (var (name, value) in scope)
                if (value is BookValue || _pulledModuleNames.Contains(name)) fresh[name] = value;

        _scopes.Clear();
        _scopes.Add(fresh);
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
    // The native layer is a stateless singleton; a book with a Cufet layer (a prelude-defined
    // module object sharing its name) is bound as a fresh instance-carrying copy at each pull,
    // because pulling INSTANTIATES the module object. Members the instance's type defines win at
    // dispatch; the rest stay native. (Slice 1 of the 0.16.0 arc — see ROADMAP.)
    private sealed class BookValue
    {
        public string Name { get; }
        public IReadOnlyDictionary<string, Func<object[], object?>> Functions { get; }
        public IReadOnlyDictionary<string, object> Constants { get; }
        public ObjectValue? CufetInstance { get; }

        public BookValue(
            string name,
            IReadOnlyDictionary<string, Func<object[], object?>> functions,
            IReadOnlyDictionary<string, object> constants,
            ObjectValue? cufetInstance = null)
        {
            Name          = name;
            Functions     = functions;
            Constants     = constants;
            CufetInstance = cufetInstance;
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

        // In place, on the shared array — a matrix is a reference type, so the write is visible
        // through every name bound to this matrix.
        public void SetItem(int row, int col, decimal value) =>
            _data[(row - 1) * Cols + (col - 1)] = value;
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
    // Keyed on the ORDERED operand pair — `vec2 * number` and `number * vec2` are separate.
    private readonly Dictionary<(string Left, string Right, TokenType Op), OperatorOverloadDeclaration> _overloadDefs = new();

    /// <summary>The overload-table name for a runtime value — the checker's `OperandTypeName`,
    /// asked of a value instead of a type. ⚠ The two must agree, or a program that checks one
    /// way runs another.</summary>
    private static string? RuntimeOperandName(object? v) => v switch
    {
        ObjectValue ov => ov.TypeName,
        decimal        => "number",
        string         => "text",
        bool           => "fact",
        BitsValue      => "bits",
        _              => null,
    };

    private int _callDepth = 0;
    private readonly int _maxCallDepth;

    /// <summary>The one wording for "this went deeper than the interpreter allows".</summary>
    /// <remarks>
    /// <para>
    /// ★★ It states the FACT and stops. The five sites that share it used to diagnose instead —
    /// "caused infinite recursion", "is it missing a base case?" — and that is a guess about cause
    /// which is wrong whenever the real cause is a smaller environment. `sudoku.cufe` recurses
    /// correctly and needs more depth than a browser has; telling its author their working program
    /// is infinitely recursive is exactly the class of confidently-wrong message this language
    /// exists to avoid. The depth is in the text because it is the one thing the reader cannot
    /// otherwise find out, and it differs between hosts.
    /// </para>
    /// <para>
    /// ⚠⚠ The literal `(line N)` fragment is load-bearing beyond reading well: the playground
    /// scrapes it back out with a regex (LineFromMessage) to underline the right line in the
    /// editor. Reword around it, never remove it.
    /// </para>
    /// <para>
    /// ★ <paramref name="subject"/> is a whole phrase rather than a bare name, because the sites
    /// do not all have one: a function is 'loop', a getter is Getter 'width', and an operator
    /// overload has no name at all and describes itself instead.
    /// </para>
    /// </remarks>
    /// <summary>Refuses to descend when the real stack is nearly gone.</summary>
    /// <remarks>
    /// <para>
    /// ★★ This is the REAL limit; <see cref="TooDeep"/>'s call count is a proxy for it. A count
    /// cannot know how much stack a call costs, and the cost varies enormously with the program:
    /// measured in the browser, a minimal recursive function survived depth 275 while one nesting
    /// a few calls inside arithmetic died between 140 and 150. No single number is right for both,
    /// which is why the playground had to pick one low enough to refuse legitimate programs.
    /// </para>
    /// <para>
    /// ⚠ A .NET StackOverflowException cannot be caught — it takes the process down. In a browser
    /// that meant the page died with no message and nothing the visitor had typed survived. This
    /// asks BEFORE descending, so the answer is an ordinary Cufet refusal.
    /// </para>
    /// <para>
    /// ★ Checked where a CALL happens rather than at every statement, and both were measured on
    /// `examples/algorithms/sudoku.cufe` — the program that used to kill the page. Per-statement
    /// works too and costs more: the reserve comfortably covers the handful of frames between one
    /// call and the next. Calls are also where a line number is already in hand.
    /// </para>
    /// <para>
    /// ⚠ Not a total guarantee. A single enormous expression could exhaust the rest between
    /// checks, and recursion that never passes through a call site is not seen at all. It covers
    /// the shape real programs actually have.
    /// </para>
    /// </remarks>
    private RuntimeException? OutOfStack(string subject, int line) =>
        RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? null
            : new RuntimeException(
                $"{subject} ran out of stack (line {line}). How deep a program can go depends on "
                + "where it runs, and this is as far as it could go here — a browser allows far "
                + "less room than a terminal does.");

    private RuntimeException TooDeep(string subject, int line) =>
        new($"{subject} went deeper than {_maxCallDepth} calls (line {line}).");

    /// <summary>What runs this program's foreign axioms, or null where nothing can.</summary>
    /// <remarks>See IForeignRunner — set by whoever wires the interpreter to a C toolchain.</remarks>
    public IForeignRunner? ForeignRunner { get; set; }

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
        // ★ The SECOND Ctrl-C is left alone, so the OS terminates. Taking the kill away is only
        // defensible while something can still act on the flag; if a first Ctrl-C has already been
        // recorded and the program has not acknowledged it, nothing is listening and cancelling
        // again would make the process unkillable from its own terminal. `Acknowledge the
        // interrupt.` clears the flag, so a program that genuinely handles interrupts gets the
        // polite behaviour every time and never reaches this path.
        //
        // Skipped in the browser, where Console.CancelKeyPress throws PlatformNotSupported —
        // there is no Ctrl-C to intercept. Being in the CONSTRUCTOR, it otherwise made every
        // program fail under WebAssembly while the front end worked perfectly, which is a
        // confusing shape of bug: type errors reported fine, nothing would run.
        if (!OperatingSystem.IsBrowser())
            Console.CancelKeyPress += (_, e) => e.Cancel = InterruptArrived();
    }

    /// <summary>One Ctrl-C. True to keep the process alive; false to let the OS have it.</summary>
    /// <remarks>
    /// ★ A method rather than a lambda body so a test can press the key. Synthesising a real
    /// Ctrl-C is not something a test can do portably, and the alternative — a hook that repeats
    /// this decision in the test — would assert that the copy is right about nothing.
    /// </remarks>
    internal bool InterruptArrived()
    {
        // ★★ Recorded, but not COUNTED. A child holding the terminal was signalled by that terminal
        // directly; what it does about that is its own answer, and a shell has to survive as many
        // Ctrl-Cs as anyone cares to press. Only for a program in charge of its own interrupts —
        // one that never mentions them still stops, launch or no launch.
        //
        // ⚠⚠ RECORDED is the load-bearing word, and skipping it was a measured backend DIVERGENCE:
        // the compiled runtime has no second-press escape at all, so its handler had gone on
        // setting the flag while this one had stopped. The same program, interrupted at the same
        // moment, then answered `an interrupt is requested` differently on the two backends — and
        // the oracle cannot see it, because no test in the suite sends a signal.
        //
        // ★ The escape was the only thing that ever needed narrowing. Not taking the flag as well
        // was a rule wider than its own reason.
        if (_childHasTerminal > 0 && _programHandlesInterrupts)
        {
            _interruptRequested = true;
            return true;
        }

        // The second press is left alone, so the OS terminates. Taking the kill away is only
        // defensible while something can still act on the flag.
        if (_interruptRequested) return false;
        _interruptRequested = true;
        return true;
    }

    // Flattens statements through Pull...Done scope bodies so that hoisting passes see
    // Bind/Object/etc. declarations inside Pull scopes (hoisting is transparent to Pull scopes).
    //
    // ⭐ The CHECKER's copy, not a second one of our own. This file had a byte-identical twin, and
    // the two halves of the rule it states — which declarations hoist, and out of which scopes —
    // are exactly what drifted apart for `permanently`: the checker treated a rabbit-block constant
    // as shared and this file did not. One answer, asked in one place.
    private static IEnumerable<IStatement> FlattenHoistable(IEnumerable<IStatement> stmts) =>
        TypeChecker.FlattenHoistable(stmts);

    // True when the program stopped because of a Ctrl-C rather than by reaching its end. The CLI
    // turns this into exit code 130 (128 + SIGINT), so a script wrapping cufet can tell an
    // interrupted run from a completed one.
    public bool WasInterrupted { get; private set; }

    public void Execute(Program program)
    {
        // ★ BEFORE anything runs, and before the scheduler exists. Every axiom is compiled here so
        // that a program the compiled backend would refuse to build is refused here too — with the
        // same output, which is none. See PrepareForeignSource.
        PrepareForeignSource(program);
        _programHandlesInterrupts = MentionsInterrupts(program.Statements);
        _scheduler = new CufetScheduler();
        try
        {
            _scheduler.Run(() => { ExecuteCore(program); return Task.CompletedTask; });
        }
        catch (InterruptUnwind)
        {
            // Ctrl-C on a program that never mentions interrupts. The unwind IS the handling: stop
            // here, run nothing further, and say so in the exit code. Nothing caught this before,
            // so it escaped as an unhandled exception and printed a .NET stack trace — survivable
            // only because it could not be reached without a `Yield.` or a blocking call.
            WasInterrupted = true;
        }
        finally { _scheduler = null; }
    }

    private void ExecuteCore(Program program)
    {
        // Hoist object definitions (before functions, so method bodies can reference them).
        foreach (var stmt in AstSearch.EveryStatement(program.Statements))
        {
            if (stmt is ObjectDefinition od)
                _objectDefs[od.Name] = od;
        }

        // Hoist destructor declarations.
        foreach (var stmt in AstSearch.EveryStatement(program.Statements))
        {
            if (stmt is UnmakerDeclaration ud)
                _unmakeDefs[ud.UnmakesTypeName] = ud;
        }

        // Hoist operator overload declarations.
        foreach (var stmt in AstSearch.EveryStatement(program.Statements))
        {
            if (stmt is OperatorOverloadDeclaration oad)
                _overloadDefs[(oad.LeftTypeName, oad.RightTypeName, oad.Operator)] = oad;
        }

        // Merge 'unto' methods/getters/setters (declared outside the object body) into their
        // target type's member lists. TypeChecker already validated every target exists.
        foreach (var stmt in AstSearch.EveryStatement(program.Statements))
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

        // ⭐⭐ Which names are SHARED CONSTANTS — read off the PROGRAM, not off how deep the scope
        // stack happens to be when a `Define` runs. Hoisting is transparent to a pull scope (the
        // function hoist just below walks the very same statements), so a `permanently` written
        // inside one is a shared constant exactly as a `Bind` there is a free function.
        //
        // ⚠ The old test was `_scopes.Count == 1` at the moment the `Define` executed, and that is
        // a rule wider than its reason: it was there to exclude a `permanently` LOCAL to a function
        // or a loop, and it excluded a rabbit block along with them. So a constant declared in
        // `Pull a rabbit.` — where most programs put theirs — was invisible to every function, while
        // the checker and the compiler both said it was shared. The compiler ran such a program and
        // printed an answer; this one died at run time saying the name was never defined.
        //
        // ⚠ A `permanently` inside a FUNCTION body is still shared with nothing: this walk does
        // not enter one, which is the same reason the function hoist does not.
        foreach (var stmt in FlattenHoistable(program.Statements))
            if (stmt is DefineStatement { Permanent: true } constant)
                _permanentTopLevel.Add(constant.Name);

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
        // ★ The interrupt checkpoint. Without one, a loop of ordinary statements — no `Yield.`, no
        // channel, no task, no subprocess — could never see the flag, so Ctrl-C did nothing at all
        // and the only way out of a running program was to kill the terminal.
        //
        // WHO WINS, when a program handles interrupts itself: it does. Unwinding here unconditionally
        // would break the documented `If an interrupt is requested:` pattern by tearing the program
        // down before it could ever poll. So this only fires for programs that never mention
        // interrupts at all — decided once, from the AST, before anything runs. The rule states in
        // one line: handle interrupts and you are in charge of them; ignore them and Ctrl-C behaves
        // the way it does everywhere else.
        if (_interruptRequested && !_programHandlesInterrupts)
            throw new InterruptUnwind();

        switch (stmt)
        {
            case StateStatement s:
                _out.WriteLine(Format(Evaluate(s.Value)));
                break;

            // ★ A safety valve, not a feature gap. `Bury` never survives type checking — StashTransform
            // rewrites every burying function into an ordinary object with a `next` method, so neither
            // backend has (or needs) suspension machinery. Reaching here means a caller ran the
            // interpreter on the PRE-transform program, which would otherwise fail silently and
            // strangely. `Check` returns the rewritten program; use its return value.
            case BuryStatement bury:
                throw new RuntimeException(
                    $"Internal: a 'bury' on line {bury.Line} reached the interpreter untransformed. "
                    + "Run the program returned by TypeChecker.Check, not the one handed to it.");

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

            case JudgeStatement judge:
            {
                // The subject is evaluated ONCE, however many arms are tested against it — the
                // whole point of naming it `it` rather than repeating the expression per arm.
                var subject = Evaluate(judge.Subject);

                foreach (var arm in judge.Arms)
                {
                    if (!arm.Cases.Any(c => RuntimeIsType(subject, c))) continue;
                    EnterScope();
                    try
                    {
                        Scope["it"] = subject!;
                        foreach (var s in arm.Body) Execute(s);
                    }
                    finally { ExitScope(); }
                    return;
                }

                // The checker guarantees one of these two happens: either the arms covered a
                // closed union exhaustively, or an Otherwise is present. Reaching the end with
                // neither would be a checker bug, not a program error.
                if (judge.OtherwiseBody != null)
                {
                    EnterScope();
                    try
                    {
                        Scope["it"] = subject!;
                        foreach (var s in judge.OtherwiseBody) Execute(s);
                    }
                    finally { ExitScope(); }
                }
                return;
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

            case SeriesInsertStatement sa:
            {
                var saTarget = Evaluate(sa.Series);
                // ★ A chase takes the characters of a text, however many there are. Appending what
                // you just built is the operation a buffer exists for, so it is the one Insert does.
                if (saTarget is CufetChase chaseTarget)
                {
                    chaseTarget.Append((string)Evaluate(sa.Value));
                    break;
                }
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
                if (sraTarget is CufetChase sraChase)
                {
                    sraChase.RemoveAt(ResolveIndex(sra.Index, sraChase.Count,
                                                   SeriesDisplayName(sra.Series), sra.Line));
                    break;
                }
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

            case MatrixSetStatement mss:
                ExecuteMatrixSet(mss);
                break;

            case SeriesSetStatement ss:
            {
                var ssTarget = Evaluate(ss.Series);
                if (ssTarget is CufetChase ssChase)
                {
                    var ssName = ss.Series is VariableReference ssVr ? ssVr.Name : "this expression";
                    int ssAt = ResolveIndex(ss.Index, ssChase.Count, ssName, ss.Line);
                    var one = new CufetChase();
                    one.Append((string)Evaluate(ss.Value));
                    // ⚠ Exactly one character. Taking the first and dropping the rest would be a
                    // silent resolution, which this language refuses everywhere else — and Insert
                    // is already the operation that takes however many.
                    if (one.Count != 1)
                        throw new RuntimeException(
                            $"Setting one position needs exactly one character, and \"{(string)Evaluate(ss.Value)}\" is {one.Count}. " +
                            $"This happened on line {ss.Line}.");
                    ssChase[ssAt] = one[0];
                    break;
                }
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
                // An axiom called for its effect: run it and drop the answer, which is what a
                // statement means. The checker resolved the name to its source.
                if (cs.RunsAxiom is { } effectAxiom)
                    _ = RunAxiomCall(cs.Args, effectAxiom, cs.Line);
                else if (cs.RunsAxiomValue)
                    _ = RunAxiomCall(cs.Args, HeldAxiom(cs.Function, cs.Line), cs.Line);
                else
                    ExecuteCallExpr(CalledFunction(cs.Function, cs.ResolvedFunctionName, cs.Line, cs.Column),
                                    cs.Args, cs.Line);
                break;

            case ReturnStatement ret:
                // An axiom runs when it is returned, and the declared type decides — the checker
                // resolved which and put the axiom itself on the statement.
                throw new ReturnException(
                    ret.RunsAxiom is { } axiom ? RunAxiom(axiom, ret.Line)
                  : ret.Value != null          ? Evaluate(ret.Value)
                  : null);

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

            case RunStatement runStmt: ExecuteRunStatement(runStmt); break;

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

                // ★ A chase iterates as the collection it is, one CHARACTER at a time — each bound
                // as a one-character text, the same thing `item n of` gives back. Before the
                // List<object> test, which it fails.
                if (seriesVal is CufetChase feChase)
                {
                    string feDisplay = collectionDisplay ?? "The chase";
                    int feStart = feChase.Count;
                    for (int i = 0; i < feStart; i++)
                    {
                        if (feChase.Count != feStart)
                            throw new RuntimeException(
                                $"{feDisplay} was modified during a for-each loop on line {fe.Line} — collect into a separate series, or use a While loop if you need to change it while looping.");
                        EnterScope();
                        Scope[iterKey] = char.ConvertFromUtf32(feChase[i]);
                        bool feStopped = false;
                        try { foreach (var st in fe.Body) Execute(st); }
                        catch (StopException) { feStopped = true; }
                        catch (SkipException) { }
                        finally { ExitScope(); }
                        if (feStopped) break;
                        if (feChase.Count != feStart)
                            throw new RuntimeException(
                                $"{feDisplay} was modified during a for-each loop on line {fe.Line} — collect into a separate series, or use a While loop if you need to change it while looping.");
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
    private int ResolveIndex(IExpression? indexExpr, List<object> list, string seriesName, int line) =>
        ResolveIndex(indexExpr, list.Count, seriesName, line);

    /// <summary>Resolves a 1-based index against a COUNT, so a chase reaches the same rules.</summary>
    /// <remarks>
    /// ★ Taking the count rather than the list is what lets a buffer share these bounds messages
    /// word for word. A second copy for the second collection is how the two drift.
    /// </remarks>
    private int ResolveIndex(IExpression? indexExpr, int count, string seriesName, int line)
    {
        if (indexExpr == null)
        {
            if (count == 0)
                throw new RuntimeException($"Can't access the last item — '{seriesName}' is empty on line {line}.");
            return count - 1;
        }
        var raw = Evaluate(indexExpr);
        if (raw is not decimal d)
            throw new RuntimeException($"Series index must be a number on line {line}.");
        var idx = (int)d;
        if (idx < 1 || idx > count)
        {
            var range = count == 0
                ? $"'{seriesName}' is empty"
                : $"'{seriesName}' has {count} {(count == 1 ? "item" : "items")} (you can reach items 1 through {count})";
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
        AxiomLiteral     ax   => AxiomValue(ax),
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
        SeriesLength     sl   => Evaluate(sl.Series) switch
                                 {
                                     // ⚠ Before List<object>, because a chase IS one — it is a
                                     // List<int> of code points, and the general arm would count
                                     // the same thing but only by accident of the base class.
                                     CufetChase chase => (decimal)chase.Count,
                                     List<object> slList => (decimal)slList.Count,
                                     _ => throw new RuntimeException($"Expected a series for 'the number of' on line {sl.Line}."),
                                 },
        RecordLiteral    rl   => (object)new RecordValue(
                                    rl.PositionalFields.Select(Evaluate).ToList(),
                                    rl.NamedFields.Select(f => (f.Name, Evaluate(f.Value))).ToList()),
        BitsAtWidth baw => EvaluateBitsAtWidth(baw),
        RecordNamedAccess rna => EvaluateRecordNamedAccess(rna),
        ObjectLiteral    ol   => EvaluateObjectLiteral(ol),
        PossessiveAccess pa   => EvaluatePossessiveAccess(pa),
        CastExpression   cast => EvaluateCastExpr(cast),
        TextJoin   tj => EvaluateTextJoin(tj),
        ChaseLiteral => new CufetChase(),
        TextConvert tc => (object)ConvertToText(Evaluate(tc.Value)),
        NumberConvert nc => EvaluateNumberConvert(nc),
        BitsConvert bc   => EvaluateBitsConvert(bc),
        // Code points, not .NET's UTF-16 code units — see TextPositions for why the language
        // picks a unit rather than inheriting each backend's storage.
        TextLength  tl => (object)(decimal)TextPositions.Length((string)Evaluate(tl.Target)),
        // ★ COPIED out of C's memory, never aliased — the bytes belong to C and can be freed or
        // overwritten the moment it likes. A void address reads as void.
        ForeignTextAt fta => Evaluate(fta.Address) switch
        {
            ForeignAddress { Handle: var h } when h != 0
                => System.Runtime.InteropServices.Marshal.PtrToStringUTF8(h) ?? "",
            _ => VoidValue.Instance,
        },
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
        ConditionalExpression ce => EvaluateConditional(ce),
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
        // ★ `unbury s` IS `cast s on ()`. A stash lowers to a closure, so resuming one is calling
        // it — which means this needs no machinery of its own, just the call path every function
        // value already uses. (Unlike BuryStatement above, an unbury legitimately survives the
        // transform: only the burying FUNCTION is rewritten, never its call sites.)
        UnburyExpression ub               => EvaluateCastExpr(new CastExpression(ub.Stash, [], ub.Line, ub.Column)),
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

    // `X when C, otherwise Y`
    //
    // ★ The condition first, then EXACTLY ONE arm. Evaluating both would run a call, raise a
    // failure, or print something the program never asked for — and the compiler cannot evaluate
    // both either, so doing it here would be a divergence rather than a mere inefficiency.
    private object EvaluateConditional(ConditionalExpression ce)
    {
        if (Evaluate(ce.Condition) is not bool taken)
            throw new RuntimeException("A 'when' condition must evaluate to true or false.");
        return Evaluate(taken ? ce.Value : ce.Alternative);
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

        // ⚠ Before the List<object> test, which a chase FAILS — it is a List<int>, so without
        // this it reported "Expected a series" for a perfectly good buffer.
        if (val is CufetChase chase)
        {
            var chaseName = sa.Target is VariableReference cvr ? cvr.Name : "this expression";
            int at = ResolveIndex(sa.Index, chase.Count, chaseName, sa.Line);
            return char.ConvertFromUtf32(chase[at]);
        }

        if (val is not List<object> list)
            throw new RuntimeException($"Expected a series on line {sa.Line}.");
        var sname = sa.Target is VariableReference vr ? vr.Name : "this expression";
        return list[ResolveIndex(sa.Index, list, sname, sa.Line)];
    }

    // `<bits> at <n> bits`. Widening is free; narrowing is refused when a set bit would be lost.
    private object EvaluateBitsAtWidth(BitsAtWidth baw)
    {
        if (Evaluate(baw.Target) is not BitsValue bv)
            throw new RuntimeException($"'at ... bits' needs a bits value (line {baw.Line}).");
        if (Evaluate(baw.Width) is not decimal wd || wd < 0 || wd != decimal.Truncate(wd))
            throw new RuntimeException($"a stated width must be a whole, non-negative number of bits (line {baw.Line}).");

        int stated = (int)wd;
        int needed = 64;
        while (needed > 0 && (bv.Value >> (needed - 1)) == 0) needed--;
        if (stated < needed)
            throw new RuntimeException(
                $"{stated} bits cannot hold this value - it needs {needed} (line {baw.Line}). " +
                "Widening is always fine; narrowing is refused when it would drop a set bit. " +
                "Mask with 'and' if dropping them is what you meant.");
        return bv with { Width = stated };
    }

    private object EvaluateRecordNamedAccess(RecordNamedAccess rna)
    {
        // The width a bits value already carries — see TypeChecker.Records for why this is not a
        // keyword. Every bits value has one; it is what drives the leading zeros when it prints.
        if (Evaluate(rna.Record) is BitsValue bw && rna.FieldName == "width")
            return (decimal)bw.Width;

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
        // CaseTable, not ToUpperInvariant: the compiled backend emits this same table, and reading
        // one table is what keeps the two from disagreeing about `"héllo" in uppercase`.
        return (object)(tcase.Uppercase ? CaseTable.ToUpper(text) : CaseTable.ToLower(text));
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

        // Operator overload dispatch: a registered operand PAIR takes priority over the numeric
        // path. The pair is ordered, so `vec2 * number` is found by that key alone.
        if (b.Op is TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash &&
            RuntimeOperandName(lv2) is { } lname && RuntimeOperandName(rv2) is { } rname &&
            _overloadDefs.TryGetValue((lname, rname, b.Op), out var oad))
            return ExecuteOperatorOverload(oad, lv2, rv2, b.Line);

        // Scaling: every element times a number. Cannot fail — no dimensions to disagree — so it
        // hands back a plain matrix, and both orders mean the same thing.
        if (b.Op is TokenType.Star)
        {
            if (lv2 is MatrixValue smL && rv2 is decimal sfR) return MatrixScale(smL, sfR);
            if (lv2 is decimal sfL && rv2 is MatrixValue smR) return MatrixScale(smR, sfL);
        }

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
        // ★ The real limit, asked before descending. See OutOfStack.
        if (OutOfStack($"The '{oad.LeftTypeName} {OpSymbol(oad.Operator)} {oad.RightTypeName}' overload", line) is { } tooDeep)
        {
            _callDepth--;
            throw tooDeep;
        }
        if (_callDepth > _maxCallDepth)
        {
            _callDepth--;
            throw TooDeep(
                $"The '{oad.LeftTypeName} {OpSymbol(oad.Operator)} {oad.RightTypeName}' overload", line);
        }

        var saved      = SaveScopes();
        var prevHidden = _hiddenTopLevelData;
        ImportTopLevelVisible(saved.Scopes);
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
            _hiddenTopLevelData = prevHidden;
        }

        if (returnValue == null)
            throw new RuntimeException(
                $"The '{oad.LeftTypeName} {OpSymbol(oad.Operator)} {oad.RightTypeName}' overload did not return " +
                $"a value (line {line}).");
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

        // ⚠ Before the List<object> arm, which a chase would MISS — it is a List<int>, so it fell
        // through to reference equality while a series of the same shape compared structurally.
        // A chase follows collection conventions, so it compares the way a collection does.
        if (a is CufetChase ca && b is CufetChase cb)
            return ca.Count == cb.Count && !ca.Where((point, i) => point != cb[i]).Any();

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
            return $"'{name}' is a top-level value, but function and method bodies can't see top-level data{located}.\n\n" +
                    $"They see other functions (for mutual recursion) and top-level `permanently` constants, but not top-level data that can change.\n" +
                    $"This keeps data flow explicit and prevents hidden mutations — the same principle behind Cufet's message-passing model.\n\n" +
                    $"Fix: declare it a shared constant if it never changes:\n" +
                    $"    Define {name} as <value> permanently.\n" +
                    $"Or pass '{name}' as a parameter:\n" +
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

    /// <summary>What `converted to text` gives back.</summary>
    /// <remarks>
    /// ⚠⚠ Not <see cref="Format"/>, for a chase. Format prints a buffer as the collection it is —
    /// `(h, e, l, l, o)` — which is right for `State buf.` and exactly wrong here: the whole point
    /// of this conversion is to get `hello` out. Everything else formats the way it prints.
    /// </remarks>
    private static string ConvertToText(object val) =>
        val is CufetChase chase ? chase.AsText() : Format(val);

    private static string Format(object val) => val switch
    {
        VoidValue        => "void",
        bool b           => b ? "true" : "false",
        // ⚠ INVARIANT, for the reason the parser's literal is: `State 1.5.` printed "1,5" on a
        // machine whose culture uses a comma, where the compiled program printed "1.5". Output is
        // half of what a program MEANS, so it cannot be the reader's locale's business.
        decimal d        => NormalizeDecimal(d).ToString(CultureInfo.InvariantCulture),
        BitsValue bv     => bv.ToString(),   // prints in the base it was written in
        List<object> lst => "(" + string.Join(", ", lst.Select(Format)) + ")",
        // ★★ Printed like the COLLECTION it is, not like the text it will become. `(h, e, l, l, o)`
        // rather than `hello`, so a reader never mistakes a buffer for a `text` — and if the text
        // is what you want, `converted to text` is the explicit copy that says so. Above
        // List<object> would be wrong: this holds code points, which print as numbers.
        CufetChase chase => "(" + string.Join(", ", chase.Select(char.ConvertFromUtf32)) + ")",
        FunctionValue        => "<function>",
        // ★ An axiom is a callable held as a value, so it prints the way the other callable does.
        // ⚠ Not cosmetic: the value an axiom name holds IS the AxiomLiteral record, so with no arm
        // here it fell through to ToString() and printed the C# record — source text, line, column
        // and all — while the compiled backend refused the same program at build time.
        AxiomLiteral         => "<axiom>",
        // ★ `<address>`, never the pointer itself, and the reason is the oracle rather than
        // secrecy: two backends are two processes, so the same program's handle is a different
        // number in each and printing it could never agree. The same shape as <function>.
        ForeignAddress       => "<address>",
        // ★ A failure and an exception are OPAQUE the way a function is: what is worth reading out
        // of one is reached by name — `the message of the failure`, `the category of the failure`,
        // `the message of the exception` — so printing the value itself prints a placeholder, as
        // every other value with nothing meaningful to show does.
        //
        // ⚠ These two were the only gaps in this list, and they fell to `val.ToString()` at the
        // bottom, which printed `Cufet.Interpreter.Interpreter+FailureValue` — a C# class name, in
        // a user's output. The compiler meanwhile REFUSED to build the same program, so the pair
        // was a divergence as well as a leak. Same defect, same shape, and the same fix as `State`
        // on a function had: the placeholder, matched on both sides.
        FailureValue         => "<failure>",
        ExceptionValue       => "<exception>",
        // A rabbit prints as the object it is, like every other module — `math()`,
        // `greeting-kit()`, `rabbit()`. It used to print its BINDING's name (`<rabbit hopper>`),
        // which nothing else in the language does: `Define x as 5. State x.` prints 5, not x.
        RabbitValue          => $"{TypeChecker.RabbitModuleName}()",
        ReadableStreamValue  => "<readable stream of text>",
        WritableStreamValue  => "<writable stream of text>",
        RecordValue rv   => FormatRecord(rv),
        ObjectValue ov   => FormatObject(ov),
        // ★ A book prints as the object it is — `math()` — which is what the COMPILER already
        // emitted for one. Without this arm it fell through to val.ToString() and printed
        // `Cufet.Interpreter.Interpreter+BookValue` at the reader: a divergence and a host type
        // name in one. A module has no fields (one with fields is refused at the pull), so there
        // is never anything inside the parentheses.
        BookValue bkv    => $"{bkv.Name}()",
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
        return $"{ModuleTypeLifting.DisplayName(ov.TypeName)}(" + string.Join(", ", parts) + ")";
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
        BitsType      => s is BitsType,
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
        BitsType        => value is BitsValue,
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
