using Cufet.Lexer;

namespace Cufet.Interpreter;

// CufetType is a value-equality class hierarchy.
// CufetType.Number / .Text / .Fact are canonical singletons for the three scalars.
// All == comparisons use structural / deep equality.
public abstract class CufetType
{
    public static readonly CufetType Number = new NumberType();
    public static readonly CufetType Bits   = new BitsType();
    public static readonly CufetType Text   = new TextType();
    public static readonly CufetType Fact   = new FactType();
    public static readonly CufetType Void   = new VoidType();
    public static readonly CufetType FailureMarker   = new FailureMarkerType();
    public static readonly CufetType ExceptionMarker = new ExceptionMarkerType();

    public abstract override bool Equals(object? obj);
    public abstract override int GetHashCode();

    public static bool operator ==(CufetType? left, CufetType? right)
        => left is null ? right is null : left.Equals(right);
    public static bool operator !=(CufetType? left, CufetType? right)
        => !(left == right);
}

public sealed class NumberType : CufetType
{
    public override bool Equals(object? obj) => obj is NumberType;
    public override int GetHashCode() => typeof(NumberType).GetHashCode();
}

// A bit pattern: unsigned, at most 64 bits wide, and NOT a quantity. Bitwise operations live
// here and are deliberately absent from `number` — doing them on a decimal was the category
// error that made `not 5` come out as -6. There is no implicit conversion either way, so
// `0xFF = 255` is a type error; cross over explicitly with `converted to number` / `converted
// to hex`. Width and display base ride on the VALUE, not the type, so every bits value is
// assignable to every other and this stays a single type.
public sealed class BitsType : CufetType
{
    public override bool Equals(object? obj) => obj is BitsType;
    public override int GetHashCode() => typeof(BitsType).GetHashCode();
}

public sealed class TextType : CufetType
{
    public override bool Equals(object? obj) => obj is TextType;
    public override int GetHashCode() => typeof(TextType).GetHashCode();
}

public sealed class FactType : CufetType
{
    public override bool Equals(object? obj) => obj is FactType;
    public override int GetHashCode() => typeof(FactType).GetHashCode();
}

/// <summary>A foreign pointer: opaque, rabbit-scoped, and never dereferenced implicitly.</summary>
/// <remarks>
/// <para>
/// ★ `address`, not `pointer` — plain English for what it is, where `pointer` is C's word. There is
/// ONE kind, so `char*` and `FILE*` are the same type here: what differs is not the value but what
/// the writer does with it. An earlier draft split "data" from "handles", and that was a mechanism
/// invented where an operation would do.
/// </para>
/// <para>
/// ⚠ There is no address-OF operator, and Cufet never creates one. An address only ever comes back
/// from C and goes back into C, which is why nothing here can be forged and why no layout question
/// exists — a struct is C's idea and struct work happens in C.
/// </para>
/// </remarks>
public sealed class AddressType : CufetType
{
    public static readonly AddressType Instance = new();
    public override bool Equals(object? obj) => obj is AddressType;
    public override int GetHashCode() => typeof(AddressType).GetHashCode();
}

public sealed class SeriesType : CufetType
{
    public CufetType ElementType { get; }
    public SeriesType(CufetType elementType) => ElementType = elementType;
    public override bool Equals(object? obj) => obj is SeriesType s && ElementType == s.ElementType;
    public override int GetHashCode() => HashCode.Combine(typeof(SeriesType), ElementType);
}

/// <summary>
/// `a stash of T` — a suspended execution that hands out T values one at a time.
/// </summary>
/// <remarks>
/// ★ Not a collection. A series HAS its elements; a stash PRODUCES them, one resumption at a time,
/// and cannot be re-read, counted, or indexed. It exists because a value that is expensive or
/// infinite to materialise can still be walked — which is the whole reason `For each` over a
/// user-defined type was not worth building as external iteration.
///
/// ⚠ **One live resumption, resumed in order.** That restriction is what makes the whole feature a
/// per-function transform rather than a whole-program CPS or a copied machine stack — see
/// StashTransform. It is also what lets both backends run the same rewritten AST, so neither needs
/// suspension machinery of its own.
/// </remarks>
public sealed class StashType : CufetType
{
    public CufetType ElementType { get; }
    public StashType(CufetType elementType) => ElementType = elementType;
    public override bool Equals(object? obj) => obj is StashType s && ElementType == s.ElementType;
    public override int GetHashCode() => HashCode.Combine(typeof(StashType), ElementType);
}

/// <summary>`a c-language axiom` — foreign source held as a value, tagged by the language it is in.</summary>
/// <remarks>
/// ★ The tag is part of the TYPE because it names the consumer: a block is consumed by whoever
/// speaks its language, and nothing else may touch it. Two axioms in different languages are
/// different types for the same reason a number and a text are.
///
/// ⚠ The tag can be shortened at a declaration (`Define c-language x as […]`) but never dropped.
/// Inferring it from what happens to be pulled would make a line's meaning depend on scope above
/// it, and break the moment two language books are pulled together.
/// </remarks>
public sealed class AxiomType : CufetType
{
    public string Language { get; }

    /// <summary>What running this axiom gives back, declared where the axiom is written.</summary>
    /// <remarks>
    /// ★★ On the DECLARATION, by the only party who knows. C's type says how many bits arrive and
    /// not what they mean: `isatty` gives an `int` that is really a fact, `fopen` gives a pointer
    /// that is really a handle, `getchar` gives an `int` that is a character or an end. Inferring
    /// from the C would also make the answer depend on the local toolchain — `size_t` is not the
    /// same width everywhere — and would put a C compiler behind `cufet check`, which today needs
    /// no toolchain at all and runs where none can exist (the playground is wasm).
    ///
    /// ★ Null while an axiom says nothing, which is legal to WRITE and refused to RUN. That is
    /// what leaves room for an axiom passed around unrun — a SQL fragment assembled before use —
    /// without inventing a second rule for it now.
    /// </remarks>
    public CufetType? ReturnType { get; }

    /// <summary>What running it takes, when the type is written down rather than inferred.</summary>
    /// <remarks>
    /// ★ Needed the moment an axiom can be PASSED, because two axioms differing only in what they
    /// are handed are different things to call — and a parameter declared `c-language number axiom`
    /// has to say which shape it accepts, or the call site cannot be checked at all.
    ///
    /// ⚠ Empty is a real answer (an axiom taking nothing), not "unknown". A declaration's inferred
    /// type carries the literal's own parameters, so the two agree by construction.
    /// </remarks>
    public IReadOnlyList<CufetType> ParameterTypes { get; }

    public AxiomType(string language, CufetType? returnType = null,
                     IReadOnlyList<CufetType>? parameterTypes = null)
    {
        Language = language;
        ReturnType = returnType;
        ParameterTypes = parameterTypes ?? [];
    }

    public override bool Equals(object? obj) =>
        obj is AxiomType a
        && string.Equals(Language, a.Language, StringComparison.OrdinalIgnoreCase)
        && ReturnType == a.ReturnType
        && ParameterTypes.SequenceEqual(a.ParameterTypes);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(typeof(AxiomType));
        hash.Add(Language.ToLowerInvariant());
        hash.Add(ReturnType);
        foreach (var p in ParameterTypes) hash.Add(p);
        return hash.ToHashCode();
    }
}

public sealed class RecordType : CufetType
{
    // Positional fields: order-sensitive (position is identity).
    public IReadOnlyList<CufetType> PositionalTypes { get; }
    // Named fields: stored sorted by name for order-insensitive structural equality.
    public IReadOnlyList<(string Name, CufetType Type)> NamedFields { get; }

    public RecordType(
        IReadOnlyList<CufetType> positionalTypes,
        IReadOnlyList<(string Name, CufetType Type)> namedFields)
    {
        PositionalTypes = positionalTypes;
        NamedFields     = namedFields.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
    }

    public override bool Equals(object? obj)
    {
        if (obj is not RecordType other) return false;
        if (PositionalTypes.Count != other.PositionalTypes.Count) return false;
        if (NamedFields.Count     != other.NamedFields.Count)     return false;
        for (int i = 0; i < PositionalTypes.Count; i++)
            if (PositionalTypes[i] != other.PositionalTypes[i]) return false;
        for (int i = 0; i < NamedFields.Count; i++)
            if (NamedFields[i].Name != other.NamedFields[i].Name ||
                NamedFields[i].Type != other.NamedFields[i].Type) return false;
        return true;
    }

    public override int GetHashCode()
    {
        var h = typeof(RecordType).GetHashCode();
        foreach (var t in PositionalTypes)
            h = HashCode.Combine(h, t);
        foreach (var (name, type) in NamedFields)
            h = HashCode.Combine(h, name, type);
        return h;
    }
}

public sealed class FunctionType : CufetType
{
    public IReadOnlyList<CufetType> ParameterTypes { get; }
    public CufetType? ReturnType { get; }   // null = void

    // Which parameter indices (by position) contribute to the return value's rabbit depth.
    // null  = not yet computed (method, or function still being analyzed → depth 0 at call sites).
    // []    = computed; return is always depth-0 (fresh allocation, scalar, global).
    // [i,…] = computed; return depth = max(depth of args at those indices).
    // Not part of type equality — same signature with different depth signatures are still the same type.
    public IReadOnlyList<int>? ReturnDepthSignature { get; set; } = null;

    public FunctionType(IReadOnlyList<CufetType> parameterTypes, CufetType? returnType)
    {
        ParameterTypes = parameterTypes;
        ReturnType = returnType;
    }

    public override bool Equals(object? obj) =>
        obj is FunctionType ft &&
        ReturnType == ft.ReturnType &&
        ParameterTypes.SequenceEqual(ft.ParameterTypes);

    public override int GetHashCode()
    {
        var h = HashCode.Combine(typeof(FunctionType), ReturnType);
        foreach (var pt in ParameterTypes)
            h = HashCode.Combine(h, pt);
        return h;
    }
}

// Nominal type — equality by name only. Fields/methods/getters/setters carried for lookup; not part of equality.
public sealed class ObjectType : CufetType
{
    public string Name { get; }
    public IReadOnlyList<CufetType> PositionalTypes { get; }
    public IReadOnlyList<(string FieldName, CufetType FieldType)> NamedFields { get; }
    public IReadOnlyList<(string MethodName, FunctionType Signature)> Methods { get; }
    // Slice 6 — getters: computed properties accessed with field syntax.
    public IReadOnlyList<(string GetterName, CufetType ReturnType)> Getters { get; }
    // Slice 6 — setters: intercepting writes; body is void/infallible.
    public IReadOnlyList<(string SetterName, CufetType ParamType, string ParamName)> Setters { get; }
    // Slice 7 — named constructors: free functions registered with the type via 'making a <type>'.
    public IReadOnlyList<string> Constructors { get; }
    // Slice 8 — destructor: the name of the 'Bind unmaking a <type>' declaration, if any.
    public string? Unmaker { get; }
    // Slice 4 — embedding: null means no embed; non-null is the embedded type name (handle).
    public string? EmbeddedTypeName { get; }
    // Slice 5 — conformance: interface names declared with "and <interface>" clauses.
    public IReadOnlyList<string> ConformedInterfaces { get; }
    // Fields declared `the permanently <type> <name>` — settable at construction, never after.
    // A NAME set rather than a flag folded into NamedFields, which is read in ~98 places; a
    // name-keyed set cannot fall out of step with the field list the way a parallel list could.
    // Not part of equality — ObjectType is nominal, and permanence is a property of the one
    // declaration that named the type.
    public IReadOnlySet<string> PermanentFields { get; }

    /// <summary>The blanks FILLED at a use site — `a stack of number` carries one.</summary>
    /// <remarks>
    /// ★ Transient. The checker resolves a parameterised shell into a concrete definition named for
    /// its filling (`stack of number`), so by the time either backend sees a type there are no
    /// arguments left to carry — monomorphization in the front end, the way stashes lower before
    /// the backends and interface parameters specialise per conformer. They ARE part of equality
    /// even so: two unresolved shells that differ only in their filling are different types, and
    /// nominal-by-name alone would call them the same.
    /// </remarks>
    public IReadOnlyList<CufetType> TypeArguments { get; }

    public ObjectType(
        string name,
        IReadOnlyList<CufetType> positionalTypes,
        IReadOnlyList<(string FieldName, CufetType FieldType)> namedFields,
        IReadOnlyList<(string MethodName, FunctionType Signature)> methods,
        IReadOnlyList<(string GetterName, CufetType ReturnType)>? getters = null,
        IReadOnlyList<(string SetterName, CufetType ParamType, string ParamName)>? setters = null,
        string? embeddedTypeName = null,
        IReadOnlyList<string>? conformedInterfaces = null,
        IReadOnlyList<string>? constructors = null,
        string? unmaker = null,
        IReadOnlyList<string>? permanentFields = null,
        IReadOnlyList<CufetType>? typeArguments = null)
    {
        TypeArguments      = typeArguments ?? [];
        Name               = name;
        PermanentFields    = permanentFields is null ? [] : new HashSet<string>(permanentFields, StringComparer.Ordinal);
        PositionalTypes    = positionalTypes;
        NamedFields        = namedFields.OrderBy(f => f.FieldName, StringComparer.Ordinal).ToList();
        Methods            = methods;
        Getters            = getters ?? [];
        Setters            = setters ?? [];
        Constructors       = constructors ?? [];
        Unmaker            = unmaker;
        EmbeddedTypeName   = embeddedTypeName;
        ConformedInterfaces = conformedInterfaces ?? [];
    }

    public override bool Equals(object? obj) => obj is ObjectType o && o.Name == Name;
    public override int GetHashCode() => HashCode.Combine(typeof(ObjectType), Name);
}

// Nominal interface type — equality by name only. Used as parameter/return type in annotations.
public sealed class InterfaceType : CufetType
{
    public string Name { get; }
    public InterfaceType(string name) => Name = name;
    public override bool Equals(object? obj) => obj is InterfaceType i && i.Name == Name;
    public override int GetHashCode() => HashCode.Combine(typeof(InterfaceType), Name);
}

// The type of the literal void value. void widens to voidable T for any T.
public sealed class VoidType : CufetType
{
    public override bool Equals(object? obj) => obj is VoidType;
    public override int GetHashCode() => typeof(VoidType).GetHashCode();
}

// a voidable T — holds T, or void. T widens to voidable T; void widens to voidable T.
// voidable T does NOT collapse to T without a checked narrowing branch.
//
// ★ A VOIDABLE NEVER NESTS, and that is enforced here rather than remembered everywhere else.
// `voidable voidable T` IS `voidable T`: there is one absent value, so a second layer of "or
// nothing" adds no state a program could observe — which is why a lookup on a voidable-valued map
// already returns a flat answer and asks you to use `has key` when the distinction matters.
//
// Normalising in the constructor collapses any depth by induction (each layer sees an already-flat
// inner). It is done here because the invariant is load-bearing downstream: the compiler's
// EmitAsType passes an already-voidable value straight through, which is correct only if no outer
// layer exists to wrap it into. Before this, `Define the voidable voidable number x as <voidable>.`
// type-checked, ran interpreted, passed `check --native`, and then failed at gcc with a cvd_inner
// handed to a cvd_outer — the check-passes-then-gcc-dies class the no-divergence rule forbids.
public sealed class VoidableType : CufetType
{
    public CufetType Inner { get; }
    public VoidableType(CufetType inner) => Inner = inner is VoidableType v ? v.Inner : inner;
    public override bool Equals(object? obj) => obj is VoidableType v && Inner == v.Inner;
    public override int GetHashCode() => HashCode.Combine(typeof(VoidableType), Inner);
}

// map from K to V — homogeneous, reference-typed. Keys must be value types (text, number, fact).
public sealed class MapType : CufetType
{
    public CufetType KeyType   { get; }
    public CufetType ValueType { get; }
    public MapType(CufetType keyType, CufetType valueType) { KeyType = keyType; ValueType = valueType; }
    public override bool Equals(object? obj) => obj is MapType m && KeyType == m.KeyType && ValueType == m.ValueType;
    public override int GetHashCode() => HashCode.Combine(typeof(MapType), KeyType, ValueType);
}

// readable stream of T — stateful, reference-typed I/O channel for incremental reading.
// Currently only readable stream of text is supported (stdin, file-for-reading).
public sealed class ReadableStreamType : CufetType
{
    public CufetType ElementType { get; }
    public ReadableStreamType(CufetType elementType) => ElementType = elementType;
    public override bool Equals(object? obj) => obj is ReadableStreamType s && ElementType == s.ElementType;
    public override int GetHashCode() => HashCode.Combine(typeof(ReadableStreamType), ElementType);
}

// writable stream of T — stateful, reference-typed I/O channel for incremental writing.
// Currently only writable stream of text is supported (file-for-writing).
public sealed class WritableStreamType : CufetType
{
    public CufetType ElementType { get; }
    public WritableStreamType(CufetType elementType) => ElementType = elementType;
    public override bool Equals(object? obj) => obj is WritableStreamType s && ElementType == s.ElementType;
    public override int GetHashCode() => HashCode.Combine(typeof(WritableStreamType), ElementType);
}

// rabbit — an explicit block-scoped memory region. Flows downward only (may be passed as a
// parameter, never returned). Reference-typed values created in the rabbit's With block live in
// its region and are freed at Done. In the interpreter (GC-backed) this is a semantic boundary;
// the native backend implements the physical arena.
public sealed class RabbitType : CufetType
{
    public static readonly RabbitType Instance = new();
    public override bool Equals(object? obj) => obj is RabbitType;
    public override int GetHashCode() => typeof(RabbitType).GetHashCode();
}

// Type of the iterator variable in "for each X in map" — pseudo-record with 'key' and 'value' fields.
public sealed class MappingType : CufetType
{
    public CufetType KeyType   { get; }
    public CufetType ValueType { get; }
    public MappingType(CufetType keyType, CufetType valueType) { KeyType = keyType; ValueType = valueType; }
    public override bool Equals(object? obj) => obj is MappingType m && KeyType == m.KeyType && ValueType == m.ValueType;
    public override int GetHashCode() => HashCode.Combine(typeof(MappingType), KeyType, ValueType);
}

// The type of a bare 'a failure "..."' literal, and of 'the failure' inside a handler (a fixed
// pseudo-record exposing 'message' (text) and 'category' (voidable text); see InferRecordNamedAccess).
public sealed class FailureMarkerType : CufetType
{
    public override bool Equals(object? obj) => obj is FailureMarkerType;
    public override int GetHashCode() => typeof(FailureMarkerType).GetHashCode();
}

// a T or failure — holds T, or a failure. The richer sibling of voidable T (which carries no
// "why"). T widens to "T or failure"; a bare failure widens to "T or failure" for any T.
// "T or failure" does NOT collapse to T except inside a Try block's success path, via
// 'but on failure <default>', or via 'or pass the failure off'.
public sealed class FailureType : CufetType
{
    public CufetType Inner { get; }
    public FailureType(CufetType inner) => Inner = inner;
    public override bool Equals(object? obj) => obj is FailureType f && Inner == f.Inner;
    public override int GetHashCode() => HashCode.Combine(typeof(FailureType), Inner);
}

// The type of 'the exception' binding inside an 'In case of exception' handler block.
// Exposes only 'message' (text) via record-style access.
public sealed class ExceptionMarkerType : CufetType
{
    public override bool Equals(object? obj) => obj is ExceptionMarkerType;

    public override int GetHashCode() => typeof(ExceptionMarkerType).GetHashCode();
}

// book '<name>' — a bundled standard-library capability bag. Singleton; no state.
// Members are either FunctionType (callable via 'of') or scalar types (constants read via 's).
// Equality is by name only.
// IntroducedTypes: type names this book registers into the pulling scope (e.g. "matrix" → MatrixType).
public sealed class BookType : CufetType
{
    public string Name { get; }
    public IReadOnlyList<(string MemberName, CufetType MemberType)> Members { get; }
    public IReadOnlyDictionary<string, CufetType> IntroducedTypes { get; }

    public BookType(string name,
        IReadOnlyList<(string MemberName, CufetType MemberType)> members,
        IReadOnlyDictionary<string, CufetType>? introducedTypes = null)
    {
        Name           = name;
        Members        = members;
        IntroducedTypes = introducedTypes
            ?? new Dictionary<string, CufetType>(StringComparer.OrdinalIgnoreCase);
    }

    public CufetType? FindMember(string memberName) =>
        Members.FirstOrDefault(m => string.Equals(m.MemberName, memberName, StringComparison.OrdinalIgnoreCase)).MemberType;

    public override bool Equals(object? obj) => obj is BookType b && b.Name == Name;
    public override int GetHashCode() => HashCode.Combine(typeof(BookType), Name);
}

// matrix — a 2D numeric grid introduced by the 'collections' book.
// Reference-typed (always). Scope-local nameable/constructable (requires pulling 'collections').
// Values travel freely once created. Singleton type identity (all matrices share one type).
public sealed class MatrixType : CufetType
{
    public static readonly MatrixType Instance = new();
    public override bool Equals(object? obj) => obj is MatrixType;
    public override int GetHashCode() => typeof(MatrixType).GetHashCode();
}

// channel of T — unbounded, buffered, cooperative channel. Reference-typed.
// Deep-copies values at send; delivery yields if empty-and-open; void if empty-and-closed.
public sealed class ChannelType : CufetType
{
    public CufetType ElementType { get; }
    public ChannelType(CufetType elementType) => ElementType = elementType;
    public override bool Equals(object? obj) => obj is ChannelType c && ElementType == c.ElementType;
    public override int GetHashCode() => HashCode.Combine(typeof(ChannelType), ElementType);
}

// Handle for a named task (slice 4). Holds the task's inferred result type.
// Bound in scope when 'Have rabbit start a task as <name>:' is processed.
// 'the awaited result of <name>' resolves this to ResultType.
public sealed class TaskHandleType : CufetType
{
    public CufetType? ResultType { get; }
    public TaskHandleType(CufetType? resultType) => ResultType = resultType;
    public override bool Equals(object? obj) => obj is TaskHandleType t && ResultType == t.ResultType;
    public override int GetHashCode() => HashCode.Combine(typeof(TaskHandleType), ResultType);
}

// (A or B or C) — a union type; null Cases = open (the all-types union).
// voidable T is the preferred surface form of (T or void) — (T or void) normalizes to VoidableType(T).
// Operations on an un-narrowed union value that require a known type → static error.
public sealed class UnionType : CufetType
{
    // null = open union (all types). Non-null = closed union with the listed cases.
    public IReadOnlyList<CufetType>? Cases { get; }
    public static readonly UnionType Open = new(null);
    public UnionType(IReadOnlyList<CufetType>? cases) => Cases = cases;
    public override bool Equals(object? obj)
    {
        if (obj is not UnionType u) return false;
        if (Cases == null && u.Cases == null) return true;
        if (Cases == null || u.Cases == null) return false;
        return Cases.SequenceEqual(u.Cases);
    }
    public override int GetHashCode()
    {
        if (Cases == null) return typeof(UnionType).GetHashCode();
        var h = typeof(UnionType).GetHashCode();
        foreach (var c in Cases) h = HashCode.Combine(h, c);
        return h;
    }
}

// ★ IsPulledModule marks a binding made by `Pull`. A pulled module is a lexical CAPABILITY, not
// a local — it is in scope for everything written in that block, functions included — so it
// survives into a detached body where ordinary data does not. This used to be answered by
// "is its type a BookType", which meant BOOKS survived and a writer's own module did not; the
// two are the same thing now, and the flag says what was actually meant.
public record TypeInfo(CufetType Type, IExpression EstablishingExpr, int EstablishingLine, bool Permanent = false, int RabbitDepth = 0, bool IsParameter = false, bool IsPulledModule = false);

// Line/Column point at the violation — the position the message's "Here on line N" sentence
// names. They are 0 only for the rare error with no AST node to blame; a diagnostic consumer
// treats 0 as "no position" and falls back to the top of the file.
// Not sealed: StashUnsupportedException is one of these. A shape the state machine cannot lower is
// a fact about the program, reported where and how every other such fact is, and every caller that
// already handles a type error handles it with no change.
public class TypeException : Exception
{
    public int Line { get; }
    public int Column { get; }

    public TypeException(string message) : base(message) { }

    public TypeException(string message, int line, int column) : base(message)
    {
        Line = line;
        Column = column;
    }
}

public sealed partial class TypeChecker
{
    // What the check found that is worth saying but is not a reason to stop. An error is still
    // thrown; this is only ever added to. Read it after Check returns.
    public DiagnosticBag Diagnostics { get; } = new();

    // Scope chain: [0] = global scope, [^1] = innermost current scope.
    // Every Done.-bounded block (if/while/for/try) pushes a scope on entry and pops on exit.
    // Function bodies replace the whole chain (see CheckBind/CheckMethodBody).
    private readonly List<Dictionary<string, TypeInfo>>      _scopes        = [new()];
    // Parallel type-name scope chain — book-introduced types (e.g. "matrix") registered here
    // when a type-introducing book is pulled. Same push/pop pattern as _scopes.
    private readonly List<Dictionary<string, CufetType>>     _typeScopes    = [new()];
    private readonly Dictionary<string, ObjectType>          _objectDefs    = new();

    /// <summary>
    /// The interface that says "this can be pulled" — and says nothing else.
    /// </summary>
    /// <remarks>
    /// ★★ A MARKER, deliberately, and it is expected to stay one until something real asks
    /// otherwise. Inventing a method for it now would mean guessing at a contract no conformer has
    /// asked for, and a contract is the hardest thing in a language to loosen once things depend on
    /// it. `module` is what makes a book, a rabbit and a writer's own object the same kind of thing;
    /// requiring anything of them beyond "you may be pulled" is a separate decision that has to earn
    /// itself.
    ///
    /// Built in rather than written, because every program would otherwise have to declare it — and
    /// because an interface with an empty body does not currently parse.
    /// </remarks>
    public const string ModuleInterface = "module";

    private readonly Dictionary<string, InterfaceDefinition> _interfaceDefs = new()
    {
        [ModuleInterface] = new InterfaceDefinition(ModuleInterface, [], 0, 0),
    };

    /// <summary>Free functions whose bodies contain a `bury`, so calling one yields a stash.</summary>
    private readonly HashSet<string> _buryingFunctions = new(StringComparer.Ordinal);

    /// <summary>
    /// Methods whose bodies contain a `bury`, by owning type and method name.
    /// </summary>
    /// <remarks>
    /// ⚠ Kept apart from <see cref="_buryingFunctions"/> rather than folded in, because that set is
    /// keyed on a BARE name and a method name is only unique within its type. Two types may each
    /// have a `ticks`, one burying and one not, and a single name-keyed set would answer for both.
    /// </remarks>
    private readonly HashSet<(string Type, string Method)> _buryingMethods = new();

    /// <summary>
    /// What a burying method's locals are recorded under, and what its machine is built for.
    /// </summary>
    /// <remarks>
    /// The apostrophe-s is deliberate: it is how Cufet already spells possession, and no identifier
    /// can contain one, so a method's key can never collide with a free function's name.
    /// </remarks>
    internal static string StashMethodKey(string owner, string method) => $"{owner}'s {method}";

    /// <summary>
    /// A method's signature, with a burying one's return type wrapped in the stash it hands back.
    /// </summary>
    /// <remarks>
    /// ★ The same rule free functions get, in the same place — the SIGNATURE. `cast ticks on (clock)`
    /// then infers `stash of number` through ordinary call inference, with nothing special at the
    /// call site, because the difference really does live in the declaration.
    /// </remarks>
    private FunctionType MethodSignature(BindStatement method, string owner)
    {
        var paramTypes = method.Parameters.Select(p => p.Type).ToList();
        if (!BuriesValues(method)) return new FunctionType(paramTypes, method.ReturnType);

        if (method.ReturnType == null)
            throw TypeError(
                $"'{method.Name}' buries values, so it has to say what kind",
                null, method.Line, method.Column,
                $"declare '{method.Name}' as void when it buries",
                "A burying method's declared type is the type of what it buries: "
                + $"'Bind number to {method.Name}' hands back a stash of number.");

        _buryingMethods.Add((owner, method.Name));
        return new FunctionType(paramTypes, new StashType(method.ReturnType));
    }

    /// <summary>
    /// The types StashTransform cannot work out for itself — every local's type, and every
    /// for-each source's. Filled while checking a burying body; read once, after checking.
    /// </summary>
    private readonly StashFacts _stashFacts = new();

    /// <summary>
    /// Every `For each &lt;name&gt; in &lt;stash&gt;` met while checking, and the drain loop it stands
    /// for — keyed on the loop NODE, because two loops can share a line and column no more than two
    /// statements can, but a rewrite needs the node itself to swap.
    /// </summary>
    private readonly Dictionary<ForEachStatement, IStatement> _stashDrains =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Parameterised definitions, by name — templates awaiting a filling.</summary>
    /// <remarks>
    /// ★ Held apart from <c>_objectDefs</c> deliberately: a template is not a type. `stack` alone
    /// names nothing a value can have, and only `stack of number` does — so the template never
    /// reaches type resolution, and asking for `a stack` without a filling is an ordinary
    /// unknown-type error rather than a half-formed type leaking into inference.
    /// </remarks>
    private readonly Dictionary<string, ObjectDefinition> _genericObjectDefs = new(StringComparer.Ordinal);

    /// <summary>Instantiations already built, keyed by their filled-in name (`stack of number`).</summary>
    private readonly Dictionary<string, ObjectDefinition> _instantiated = new(StringComparer.Ordinal);

    /// <summary>Functions leaving a blank, by name, with the blanks their signature named.</summary>
    private readonly Dictionary<string, (BindStatement Bind, List<string> Blanks)> _genericFunctions =
        new(StringComparer.Ordinal);

    /// <summary>Filled-in functions already built, keyed by their filled-in name (`unique of text`).</summary>
    private readonly Dictionary<string, BindStatement> _instantiatedFunctions = new(StringComparer.Ordinal);

    /// <summary>The same object type with a different method list — ObjectType is immutable.</summary>
    private static ObjectType WithMethods(
        ObjectType ot, IReadOnlyList<(string MethodName, FunctionType Signature)> methods) =>
        new(ot.Name, ot.PositionalTypes, ot.NamedFields, methods,
            ot.Getters.ToList(), ot.Setters.ToList(),
            ot.EmbeddedTypeName, ot.ConformedInterfaces, ot.Constructors, ot.Unmaker,
            ot.PermanentFields.ToList());

    /// <summary>Methods leaving a blank, keyed by (owning type, method name).</summary>
    private readonly Dictionary<(string Owner, string Method), (BindStatement Bind, List<string> Blanks)>
        _genericMethods = new();

    /// <summary>Filled-in methods, by owning type — spliced back onto its definition.</summary>
    private readonly Dictionary<string, Dictionary<string, BindStatement>> _instantiatedMethods =
        new(StringComparer.Ordinal);

    // How many times the check has been re-run to splice in filled-in definitions. One re-run is
    // the normal case; the cap turns an endlessly self-filling template into a clean refusal.
    private int _instantiationDepth;
    private const int MaxInstantiationDepth = 16;

    /// <summary>
    /// The burying function whose body is being checked right now, or null. Set only for the
    /// function's OWN body: a nested `Bind` or lambda clears it, because its locals belong to that
    /// function and never reach the enclosing one's state machine.
    /// </summary>
    private string? _recordingStashFn;

    /// <summary>
    /// Does this function's OWN body bury? Nested functions are excluded deliberately — a lambda or
    /// inner `Bind` that buries is its own stash-producer, and letting its `bury` leak outward would
    /// make the enclosing function a generator by accident.
    /// </summary>
    /// <remarks>
    /// The walk itself lives in StashTransform, which needs the identical boundary when it decides
    /// whether a statement has to be linearised. One rule, stated once.
    /// </remarks>
    private static bool BuriesValues(BindStatement bind) => StashTransform.ContainsBury(bind.Body);

    /// <summary>
    /// Notes a local's type for the state machine, and refuses a name that means two things.
    /// </summary>
    /// <remarks>
    /// ★ Linearising a body FLATTENS its scopes — a local declared inside a loop or an arm ends up a
    /// function-wide slot — so two sibling blocks that each declare `x` at different types would
    /// collide in one slot. Cufet already requires `a shadow` to reuse a name from an ENCLOSING
    /// scope (refused outright below); this catches the sibling case, which is legal everywhere else.
    /// </remarks>
    private void RecordStashLocal(string name, CufetType type, int line, int column)
    {
        if (_recordingStashFn == null) return;
        var key = (_recordingStashFn, name);
        if (_stashFacts.Locals.TryGetValue(key, out var already) && already != type)
            throw TypeError(
                $"'{name}' means a {FormatType(already)} in one part of '{_recordingStashFn}' and a {FormatType(type)} in another",
                "A burying function keeps its locals in one place, so a name can only mean one type there",
                line, column,
                $"use '{name}' for two different types inside a burying function",
                "Rename one of them.");
        _stashFacts.Locals[key] = type;
    }
    // Active narrowings: variable name → narrowed type (set inside checked branches).
    private readonly Dictionary<string, CufetType>           _narrowedVars  = new();

    // Read-only views of the two nominal-name tables, for consumers that run AFTER Check and need
    // to know which words in the program name a type — the semantic-token producer is the one that
    // does. Both are filled by Pass1Hoist and never cleared, so they describe the whole program.
    public IReadOnlyDictionary<string, ObjectType>          ObjectDefinitions    => _objectDefs;
    public IReadOnlyDictionary<string, InterfaceDefinition> InterfaceDefinitions => _interfaceDefs;

    // Registered operator overload return types: (typeName, op) → return type (T or FailureType(T)).
    // Populated by Pass2CheckOverloads before any expression type-checking begins.
    private readonly Dictionary<(string TypeName, TokenType Op), CufetType> _overloadReturnTypes = new();

    // ── Scope chain helpers ────────────────────────────────────────────────
    // The current (innermost) scope.
    private Dictionary<string, TypeInfo> Scope => _scopes[^1];

    // Walk from innermost to outermost; return the first matching TypeInfo.
    private bool TryLookup(string name, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TypeInfo ti)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out ti)) return true;
        ti = default!;
        return false;
    }

    // Walk from second-innermost to outermost only (skips the current scope).
    private bool TryLookupOuter(string name, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TypeInfo ti)
    {
        for (int i = _scopes.Count - 2; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out ti)) return true;
        ti = default!;
        return false;
    }

    private void EnterScope()
    {
        _scopes.Add(new Dictionary<string, TypeInfo>());
        _typeScopes.Add(new Dictionary<string, CufetType>());
    }

    private void ExitScope()
    {
        _scopes.RemoveAt(_scopes.Count - 1);
        _typeScopes.RemoveAt(_typeScopes.Count - 1);
    }

    // Save both scope chains and replace the VALUE chain with a fresh single scope (for function
    // isolation). V = value scopes, T = type scopes. Call sites iterate V to re-import outer
    // bindings.
    //
    // ★ The TYPE chain deliberately survives. Isolating values is what stops a function closing
    // over a local it was never handed; a book-introduced type name is not a value and there is no
    // equivalent hazard — `matrix` inside `Pull a book on collections.` is lexically in scope for
    // everything written in that block, functions included. Clearing it produced a language that
    // accepted `given (the matrix m)` in a signature and then refused `a matrix with 3 by 3` in the
    // body of the same function, because annotations resolve `matrix` in the parser and never
    // consult scope at all.
    //
    // ★ And the BOOK BINDINGS survive with them, for the same reason and a second one. A pulled
    // book is a lexical capability, not a local — `Pull a book on math.` is in scope for everything
    // written in that block. Clearing it made every book unusable inside any function declared in
    // the pull: `math's square-root` reported "'math' isn't defined", and `a random number` reported
    // "the chance book is not in scope" while sitting inside the pull that opened it. That left
    // books good for little but top-level code, which is not what a standard library is for.
    private (List<Dictionary<string, TypeInfo>> V, List<Dictionary<string, CufetType>> T) SaveScopes()
    {
        var savedV = _scopes.ToList();
        var savedT = _typeScopes.ToList();

        var fresh = new Dictionary<string, TypeInfo> { ["input"] = BuiltinInput };
        // Innermost-last so a nearer pull wins, matching ordinary lookup.
        foreach (var scope in savedV)
            foreach (var (name, info) in scope)
                if (info.IsPulledModule || info.Type is BookType) fresh[name] = info;

        _scopes.Clear();
        _scopes.Add(fresh);
        return (savedV, savedT);
    }

    private void RestoreScopes(
        (List<Dictionary<string, TypeInfo>> V, List<Dictionary<string, CufetType>> T) saved)
    {
        _scopes.Clear();
        _typeScopes.Clear();
        foreach (var s in saved.V) _scopes.Add(s);
        foreach (var t in saved.T) _typeScopes.Add(t);
    }

    // Walk type scope chain innermost-first; returns true when typeName is registered.
    private bool TryLookupScopedType(string typeName, out CufetType type)
    {
        for (int i = _typeScopes.Count - 1; i >= 0; i--)
            if (_typeScopes[i].TryGetValue(typeName, out type!)) return true;
        type = null!;
        return false;
    }

    // Register a book-introduced type into the current (innermost) type scope.
    private void RegisterScopedType(string typeName, CufetType type) =>
        _typeScopes[^1][typeName] = type;

    // Return context — set when entering a Bind or method body.
    private bool       _inFunction              = false;
    private CufetType? _expectedReturnType      = null; // null = void function
    private int        _functionDeclarationLine  = 0;
    // When true, the first Return statement encountered sets _expectedReturnType
    // instead of validating against it. Used during lambda return-type inference.
    private bool       _inferringLambdaReturn   = false;
    // When true (inside an overload body check), failure returns are skipped during
    // _inferringLambdaReturn so the success type drives _expectedReturnType, and the
    // success type is immediately wrapped in FailureType(T) once found.
    private bool       _overloadBodyIsFallible  = false;
    // When true, CastExpression results of type FailureType(T) are auto-unwrapped to T
    // because control only reaches the next line inside a Try block if the call succeeded.
    private bool       _inTryBlock              = false;
    // When true, a CastExpression that returns FailureType(T) is permitted without an explicit
    // handler — set by InferFailureFallback and InferFailurePropagate while checking their
    // inner fallible expression, so the FailureType passes through to their own logic.
    private bool       _inFailureHandledContext = false;
    // When true, 'Suppress the exception.' is valid — only inside an exception handler block.
    private bool       _inExceptionHandler      = false;
    // Current rabbit nesting depth: 0 = global/function body, 1 = inside one With-rabbit, etc.
    // Reset to 0 on function/lambda/method entry; restored on exit.
    private int        _rabbitDepth             = 0;

    // Populated by InferCastExpr; read by ValueDepthOf for CastExpression nodes.
    // Stores the concrete rabbit depth of each CastExpression's return value, derived from
    // the callee's ReturnDepthSignature evaluated with the actual argument depths at the call site.
    private readonly Dictionary<CastExpression, int> _castDepthCache = new();

    // Index -1 in ReturnDepthSignature: the receiver ('one') contributes to return depth.
    // Used for method and getter signatures; never present in free-function signatures.
    private const int ReceiverDepthIndex = -1;

    // Assigned to captured reference-type parameters in nested function scopes.
    // Parameters are always registered at RabbitDepth=0 (the function's own perspective),
    // but callers may pass rabbit-allocated (depth-N) values. Upgrading to this sentinel
    // makes CheckRegionStore reject any store of a captured parameter into outer state,
    // preventing the depth-0 sentinel from hiding a potential depth-N escape.
    private const int CapturedParameterDepth = int.MaxValue;

    // Populated by InferPossessiveAccess; read by ValueDepthOf for 'obj's member' accesses.
    private readonly Dictionary<PossessiveAccess, int> _possessiveDepthCache = new();

    // Populated by InferRecordNamedAccess (object type path); read by ValueDepthOf for
    // 'the member of obj' accesses.
    private readonly Dictionary<RecordNamedAccess, int> _rnaDepthCache = new();

    // Getter return-depth signatures: [objTypeName][getterName] → ReturnDepthSignature.
    // Stored separately from ObjectType.Getters (getters have no FunctionType wrapper).
    private readonly Dictionary<string, Dictionary<string, IReadOnlyList<int>>> _getterDepthSigs = new();

    /// <summary>
    /// Type-checks the program and returns the one to RUN, which is not always the one handed in.
    /// </summary>
    /// <remarks>
    /// ★ Returning a program rather than nothing is what lets stashes exist. A burying function is
    /// rewritten into a factory that hands back a closure (see StashTransform), and that rewrite
    /// needs types, so it cannot be a parser pass the way interface defaults are. The signature
    /// change is source-compatible — a caller that only wants validation still writes
    /// `new TypeChecker().Check(program);` and ignores the result — and any caller that DOES need
    /// to run or compile must use the returned program. Both backends refuse a stray `bury` loudly
    /// rather than misbehaving, so forgetting fails at once instead of silently.
    /// </remarks>
    public Program Check(Program program)
    {
        _scopes[0]["input"] = BuiltinInput;
        program = WithPrelude(program);
        Pass1Hoist(program);
        Pass2ResolveTypes();          // resolve all placeholder ObjectType refs in _objectDefs + global scope
        Pass2HoistSharedConstants(program); // top-level `permanently` — visible to bodies checked below
        Pass2CheckOverloads(program); // body-check all overloads; populates _overloadReturnTypes
        CheckBlock(program.Statements);

        // ⚠ HERE, not at each pull. Every module body has now been checked, so every module's needs
        // are known — including a module defined after the block that pulls it, which is the order
        // the at-the-pull check silently let through.
        CheckPendingPulls();

        // ★★ No filled SHELL may survive the front end, the same way no `stash of T` does. A shell
        // reaches a backend wherever a type was merely WRITTEN rather than resolved — an annotation
        // like `a series of box of number` never passes through ResolveParamType — and the backend
        // then meets one type under two spellings: the resolved `box of number` in one place and an
        // unresolved `box` in another, which register as two different series element types with
        // only one of them emitted. Substituting once here makes every written position correct at
        // a stroke, which is the same reasoning StashTypeSubstitution records.
        //
        // ⚠ BEFORE the splice check below, not after: resolving a shell may fill a template nothing
        // else did, and that filling still has to reach the program.
        // No template declared ⇒ no shell can exist, so the walk stays off the path of every
        // ordinary program — the same gate StashTransform.Expand uses for the same reason.
        if (_genericObjectDefs.Count > 0)
            program = new Program(AstRebuilder.Apply(program.Statements,
                t => AstRebuilder.SubstituteDeep(t,
                    inner => inner is ObjectType { TypeArguments.Count: > 0 } ? ResolveParamType(inner) : inner)));

        // ★ A filled-in template became an ordinary definition, but only in this checker's tables —
        // and the COMPILER emits from the program's statements. Splice them in and check once more
        // on a clean checker, which then meets them as the ordinary objects they now are.
        //
        // It terminates: the second pass finds each filling already registered under its own name,
        // so it instantiates nothing new and the recursion stops one level down. A template that
        // fills itself endlessly is caught by the depth guard rather than by running out of stack.
        if (_instantiated.Count > 0 || _instantiatedFunctions.Count > 0 || _instantiatedMethods.Count > 0)
        {
            if (_instantiationDepth >= MaxInstantiationDepth)
                throw new TypeException(
                    "That doesn't work: filling in these types never finishes.\n\n" +
                    "A type that fills itself in — directly or through another — has no end. " +
                    "Break the cycle, or hold the inner one behind a 'voidable'.");

            // ★ The templates themselves are DROPPED here, not just added to. A template is not a
            // type, so a backend meeting one would try to emit a struct for `stack` whose field is
            // an undefined `element` — the same rule that lets no `StashType` survive the front end.
            var spliced = new List<IStatement>(_instantiated.Values);
            spliced.AddRange(_instantiatedFunctions.Values);
            spliced.AddRange(WithFilledMethods(
                WithoutTemplates(program.Statements, _genericFunctions.Keys.ToHashSet(StringComparer.Ordinal))));
            return new TypeChecker { _instantiationDepth = _instantiationDepth + 1 }
                .Check(new Program(spliced));
        }

        // ★ Templates are dropped even when NOTHING filled them. The splice branch above strips
        // them on its way to the re-check, but a program that never casts a generic member skips
        // that branch entirely — and a template's blank has no C type, so leaving it on the
        // definition hands the compiler `cd_element`. Latent since blanks shipped (an unused
        // template method compiled to broken C); the prelude made it the COMMON case, because
        // every program now carries `collections` and its generic `unique`, cast or not.
        if (_genericMethods.Count > 0 || _genericFunctions.Count > 0 || _genericObjectDefs.Count > 0)
            program = new Program(WithFilledMethods(
                WithoutTemplates(program.Statements, _genericFunctions.Keys.ToHashSet(StringComparer.Ordinal))));

        // ⚠ BEFORE Expand, and that ordering is the feature. Inside a burying body a stash loop is
        // DELEGATION — `For each value in inner, repeat: Have helper bury value.` — and the machine
        // builder has to split that across its buries. Handing it the drain loop means it meets
        // `Repeat`, `Define` and `If`, all of which it has always known how to step; handing it a
        // `For each` over something that is not a series would send it down the indexing rewrite.
        var statements = _stashDrains.Count == 0
            ? program.Statements
            : AstRebuilder.Apply(program.Statements, t => t,
                stmt => stmt is ForEachStatement fe && _stashDrains.TryGetValue(fe, out var drain) ? drain : null);

        // Expand hands back the very same list when there was nothing to do, which is the usual case.
        var lowered = StashTransform.Expand(
            DropUnpulledLayers(statements), _buryingFunctions, _stashFacts, _buryingMethods);
        return ReferenceEquals(lowered, program.Statements) ? program : new Program(lowered);
    }

    /// <summary>
    /// The blanks a function's signature leaves — unknown type names used more than once.
    /// </summary>
    /// <remarks>
    /// ⚠ The twice rule is the whole safety argument, so it is applied to the SIGNATURE only. A
    /// body mentioning the same misspelling twice must not conjure a blank, and a body cannot be
    /// read for this anyway: it is checked per filling, not once.
    /// </remarks>
    private List<string> SignatureBlanks(BindStatement bind)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        void Count(CufetType? type)
        {
            if (type == null) return;
            AstRebuilder.SubstituteDeep(type, leaf =>
            {
                if (leaf is ObjectType { TypeArguments.Count: 0 } shell && !IsKnownTypeName(shell.Name))
                    counts[shell.Name] = counts.GetValueOrDefault(shell.Name) + 1;
                return leaf;   // counting only; nothing is replaced
            });
        }

        foreach (var (type, _) in bind.Parameters) Count(type);
        Count(bind.ReturnType);

        return counts.Where(c => c.Value >= 2).Select(c => c.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>Does this name already mean a type — object, template, interface or book-introduced?</summary>
    private bool IsKnownTypeName(string name) =>
        _objectDefs.ContainsKey(name)
        || _genericObjectDefs.ContainsKey(name)
        || _genericObjectDefs.ContainsKey(name)
        || _interfaceDefs.ContainsKey(name)
        || BuiltinBooks.Values.Any(b => b.IntroducedTypes.ContainsKey(name));

    /// <summary>
    /// Cufet source that every program is checked as though it began with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ★★ This is how a book gets WRITTEN IN CUFET rather than built into the compiler: its source
    /// is bundled, parsed, and prepended. It stays inside the locked decision that books are
    /// compile-time-resolved and not dynamically linked — nothing is fetched, nothing is a runtime
    /// value, and there is no import machinery. The whole program still compiles at once, which is
    /// what the bounded open-union representation depends on.
    /// </para>
    /// <para>
    /// ⚠ It is prepended HERE, in Check, and not at each of the places that parse a program. There
    /// are four in the CLI alone plus the test harness and the playground, and a rule copied into
    /// six callers is a rule five of them will eventually not have — the shape this codebase has
    /// been bitten by repeatedly. Check is the one gate they all pass through, and it is already
    /// where instantiated definitions are spliced in.
    /// </para>
    /// </remarks>
    private static readonly string Prelude = LoadPrelude();

    /// <summary>Reads the bundled `Prelude/*.cufe` files embedded in this assembly, in name order.</summary>
    private static string LoadPrelude()
    {
        var assembly = typeof(TypeChecker).Assembly;
        var builder  = new System.Text.StringBuilder();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".cufe", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            builder.AppendLine(reader.ReadToEnd());
        }
        return builder.ToString();
    }

    // The prelude's own top-level statements, by reference — how Pass1Hoist tells the prelude's
    // `Define object collections` from a writer's attempt to redefine a bundled book's name.
    // Filled by WithPrelude at depth 0; deliberately left empty on the re-entrant pass, where the
    // guard is off (everything there was already admitted once).
    private readonly HashSet<IStatement> _preludeStatements = new(ReferenceEqualityComparer.Instance);

    // Bundled books this program actually pulls, filled by ResolveModule as it resolves each
    // pull site. What it is for is DropUnpulledLayers, below.
    private readonly HashSet<string> _pulledBooks = new(StringComparer.OrdinalIgnoreCase);

    // Set while a bundled book's Cufet layer is being checked, so its bodies do not import the
    // writer's top-level names. See the note in CheckMethodBody.
    private bool _checkingBookLayer;

    // The module whose body is being checked, and what each module's bodies REACH FOR without
    // defining. A module's dependencies come from the block it is USED in, not the one it is
    // written in, so an unresolved name here is not an error — it is a requirement on the caller.
    private string? _checkingModuleName;
    private readonly Dictionary<string, HashSet<string>> _moduleNeeds =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Each pull met while checking, with the names visible at it — verified once checking is done.
    /// </summary>
    /// <remarks>
    /// ⚠⚠ Deferred because a module's needs are not known until its BODY has been checked, and a
    /// module may be defined AFTER the block that pulls it. Checking at the pull worked only when
    /// the definition came first: flip the two and the identical program checked clean and died at
    /// run time, advising `Define math as &lt;value&gt;` for something you pull. That is the
    /// three-answers failure this check exists to stop, still live for one of the two orders.
    ///
    /// ★ The names are SNAPSHOT because by the time this is verified the scope is GONE — every
    /// block has been left and `_scopes` is back to the global one, so asking the live scope would
    /// report almost everything as missing. The snapshot is the key set <see cref="TryLookup"/>
    /// searches, so what is recorded and what would have been found agree exactly.
    /// </remarks>
    private readonly List<(string Module, PullStatement Pull, HashSet<string> Visible)>
        _pendingPullChecks = new();

    /// <summary>
    /// Registers every top-level `permanently` constant before any body is checked.
    /// </summary>
    /// <remarks>
    /// ★ A shared constant is a program-level DECLARATION, not a statement whose turn comes round —
    /// which is how the compiler has always emitted one (`_sharedConstants` puts it at C file scope,
    /// "because a top-level function may read it and a local in main is invisible to one"). Hoisting
    /// it here makes the checker agree: a body may read a constant declared further down the file,
    /// exactly as it may call a function declared further down.
    ///
    /// ⚠ Needed once an unresolved name in a body became an ERROR rather than a deferral. Two
    /// bodies are checked before `CheckBlock` ever reaches the constant's line — an operator
    /// overload (checked in the pass below) and any function declared above it — and both used to
    /// get away with it by leaving the name to run time.
    ///
    /// ⚠ A constant whose VALUE cannot be inferred yet is skipped, not reported. Order still
    /// applies to the value itself — `Define a as b permanently.` before `b` exists is a real
    /// error — and `CheckBlock` reaches that Define in order and says so properly. Reporting from
    /// here would blame the wrong line and pre-empt a better message.
    /// </remarks>
    private void Pass2HoistSharedConstants(Program program)
    {
        foreach (var stmt in FlattenHoistable(program.Statements))
        {
            if (stmt is not DefineStatement { Permanent: true } constant) continue;
            if (Scope.ContainsKey(constant.Name)) continue;

            CufetType? type;
            try { type = constant.DeclaredType ?? InferType(constant.Value); }
            catch (TypeException) { continue; }
            if (type == null) continue;

            Scope[constant.Name] = new TypeInfo(
                type, new VariableReference(constant.Name, constant.Line, constant.Column),
                constant.Line, Permanent: true);
            _hoistedConstants.Add(constant.Name);
        }
    }

    /// <summary>Shared constants registered ahead of their declaration, awaiting it.</summary>
    private readonly HashSet<string> _hoistedConstants = new(StringComparer.Ordinal);

    /// <summary>The names a lookup would find right here.</summary>
    private HashSet<string> VisibleNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in _scopes)
            foreach (var name in scope.Keys) names.Add(name);
        return names;
    }

    /// <summary>A RESOLVED name that is another pulled module — still a requirement.</summary>
    /// <remarks>
    /// ⚠ Resolving is not the same as being satisfied. A module written INSIDE `Pull units.` finds
    /// `units` while it is checked, so nothing looked missing — and then it was called from a block
    /// where `units` was not pulled and died at run time. A dependency is a dependency whether or
    /// not the definition site happened to have it, so it is recorded either way and checked where
    /// the module is pulled.
    /// </remarks>
    private CufetType? NoteModuleUse(string name, TypeInfo info)
    {
        if (info.IsPulledModule) NoteModuleNeed(name);
        return info.Type;
    }

    private void NoteModuleNeed(string name)
    {
        if (_checkingModuleName is not { } owner
            || string.Equals(owner, name, StringComparison.OrdinalIgnoreCase)) return;
        if (!_moduleNeeds.TryGetValue(owner, out var needs))
            _moduleNeeds[owner] = needs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        needs.Add(name);
    }

    /// <summary>An unresolved name: recorded as a module's requirement, or deferred as before.</summary>
    /// <remarks>
    /// ★ Deferring is deliberate and stays — an unresolvable name infers as null rather than
    /// cascading false positives. What is new is that a name a MODULE reaches for is remembered,
    /// so `CheckPullStatement` can say which one is missing at the place that can fix it. Without
    /// this the program checked clean, died at runtime pointing INSIDE the module, and compiled to
    /// a message that blamed the compiler.
    /// </remarks>
    private CufetType? NoteUnresolvedName(VariableReference vr)
    {
        NoteModuleNeed(vr.Name);

        // ★ In code whose scope is FINAL — top-level statements, not a detached body — nothing can
        // arrive later to define this name, so it is a mistake and is refused here rather than at
        // run time. `State mystery.` used to check clean.
        if (!_inFunction)
            throw TypeError(
                $"'{vr.Name}' isn't defined",
                "Nothing later in this block can give it a value — a name has to exist before it is used",
                vr.Line, vr.Column,
                $"use '{vr.Name}' here",
                $"Define it first: 'Define {vr.Name} as <value>.' — or check the spelling.");

        // ★★ A detached body defers for exactly ONE kind of name, and only that kind. A pulled
        // module is a capability of the block that uses the body, so `math's pi` inside a method is
        // legitimate whenever the caller pulled `math` — that is the whole reason deferring exists.
        // It used to apply to EVERY name, which made a plain typo indistinguishable from a
        // capability and put off finding it until the line ran.
        //
        // ⚠ What deferred without this was dynamic scoping, and nothing wanted it: a body reaching
        // for `borrowed` — a LOCAL of whoever called it — checked clean and died at run time. The
        // lexical rule was already half in force, since a body using a top-level constant declared
        // further down was refused with a message recommending closures.
        //
        // ⚠ A body written INSIDE a pull is unaffected and never reaches here: SaveScopes carries
        // every pulled module and book into a detached body's scope, aliases included, so those
        // resolve lexically like any other name.
        if (!IsModuleName(vr.Name))
            throw TypeError(
                $"'{vr.Name}' isn't defined",
                "A function or method uses the names it can see where it is WRITTEN — plus any "
              + "module its caller pulled, and no module is named this",
                vr.Line, vr.Column,
                $"use '{vr.Name}' here",
                $"Define it first, take it as a parameter, or — if it is meant to be a module — "
              + $"declare it: 'Define object {vr.Name} with (...) and {ModuleInterface}:'.");

        return null;
    }

    /// <summary>
    /// Drops the Cufet layer of every bundled book the program never pulls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ★ The prelude is prepended to EVERY program, so without this a one-line `State "hi".`
    /// carries all of `math` and `collections` into the emitted C — measured at 20 KB of program
    /// against 54 KB of runtime, which is exactly the ratio the runtime split was built to fix.
    /// A layer's members can only be reached through a pull, so a book nobody pulls is
    /// unreachable in its entirety and the whole definition goes.
    /// </para>
    /// <para>
    /// ⚠ It assumes no layer reaches into ANOTHER book's layer. That is true today (each calls
    /// only its own members, through `one`) and it is what keeps this a one-step drop rather
    /// than a reachability closure. A layer that pulls another book would need this to iterate.
    /// </para>
    /// </remarks>
    private IReadOnlyList<IStatement> DropUnpulledLayers(IReadOnlyList<IStatement> statements)
    {
        if (_pulledBooks.Count == BuiltinBooks.Count) return statements;

        var kept = new List<IStatement>(statements.Count);
        foreach (var statement in statements)
        {
            if (statement is ObjectDefinition od
                && BuiltinBooks.ContainsKey(od.Name)
                && !_pulledBooks.Contains(od.Name))
                continue;
            kept.Add(statement);
        }
        return kept.Count == statements.Count ? statements : kept;
    }

    /// <summary>
    /// Check the program AS the prelude: its own top-level statements get the prelude's standing
    /// (a bundled book's name may be defined), and the embedded prelude is not prepended on top
    /// of it — which would otherwise make the same definition a duplicate. Set by the CLI for a
    /// file inside a `Prelude` directory, so the language's own source can be linted without
    /// tripping the guards that source exists to justify.
    /// </summary>
    public bool TreatProgramAsPrelude { get; init; }

    /// <summary>Prepends the prelude's statements, once.</summary>
    /// <remarks>
    /// ⚠ Only at depth 0. Check re-enters itself to splice in filled-in templates, and that inner
    /// program ALREADY carries the prelude — prepending again would redefine every name in it.
    /// </remarks>
    private Program WithPrelude(Program program)
    {
        if (_instantiationDepth > 0) return program;

        if (TreatProgramAsPrelude)
        {
            foreach (var statement in program.Statements) _preludeStatements.Add(statement);
            return program;
        }

        if (Prelude.Length == 0) return program;

        var statements = new List<IStatement>(
            new Parser(new Lexer.Lexer(Prelude).Tokenize()).Parse().Statements);
        foreach (var statement in statements) _preludeStatements.Add(statement);
        statements.AddRange(program.Statements);
        return new Program(statements);
    }

    /// <summary>
    /// Puts each filled-in method onto its owning definition, and drops the template it came from.
    /// </summary>
    /// <remarks>
    /// ⚠ Both backends emit an object's methods from its ObjectDefinition, not from the checker's
    /// tables — so a filling that exists only in `_objectDefs` is a member the type checker can see
    /// and neither backend can call.
    /// </remarks>
    private IReadOnlyList<IStatement> WithFilledMethods(IReadOnlyList<IStatement> statements)
    {
        if (_instantiatedMethods.Count == 0 && _genericMethods.Count == 0) return statements;

        var rebuilt = new List<IStatement>(statements.Count);
        foreach (var stmt in statements)
        {
            switch (stmt)
            {
                case PullStatement pull:
                    rebuilt.Add(pull with { Body = WithFilledMethods(pull.Body) });
                    continue;

                case ObjectDefinition od
                    when od.Methods.Any(m => _genericMethods.ContainsKey((od.Name, m.Name))):
                {
                    var kept = od.Methods
                        .Where(m => !_genericMethods.ContainsKey((od.Name, m.Name)))
                        .ToList();
                    if (_instantiatedMethods.TryGetValue(od.Name, out var filled))
                        kept.AddRange(filled.Values);
                    rebuilt.Add(od with { Methods = kept });
                    continue;
                }

                default:
                    rebuilt.Add(stmt);
                    continue;
            }
        }
        return rebuilt;
    }

    /// <summary>Drops templates — object and function alike — reaching through `Pull` bodies.</summary>
    private static IReadOnlyList<IStatement> WithoutTemplates(
        IReadOnlyList<IStatement> statements, HashSet<string> genericFunctions)
    {
        var kept = new List<IStatement>(statements.Count);
        foreach (var stmt in statements)
        {
            switch (stmt)
            {
                case ObjectDefinition { TypeParameters.Count: > 0 }:
                    continue;
                case BindStatement bind when genericFunctions.Contains(bind.Name):
                    continue;
                case PullStatement pull:
                    kept.Add(pull with { Body = WithoutTemplates(pull.Body, genericFunctions) });
                    continue;
                default:
                    kept.Add(stmt);
                    continue;
            }
        }
        return kept;
    }

    // Resolves every placeholder ObjectType reference stored inside _objectDefs (field types,
    // method signatures, getter/setter types) and in global-scope function signatures, so that
    // by the time inference begins no placeholder can survive into a type-check result.
    // Runs after Pass1Hoist (all types registered) and before any body-checking.
    private void Pass2ResolveTypes()
    {
        var names = _objectDefs.Keys.ToList();
        foreach (var name in names)
        {
            var ot = _objectDefs[name];
            var positionals = ot.PositionalTypes.Select(ResolveParamType).ToList();
            var named       = ot.NamedFields.Select(f => (f.FieldName, ResolveParamType(f.FieldType))).ToList();
            var methods     = ot.Methods.Select(m => (m.MethodName, (FunctionType)ResolveParamType(m.Signature))).ToList();
            var getters     = ot.Getters.Select(g => (g.GetterName, ResolveParamType(g.ReturnType))).ToList();
            var setters     = ot.Setters.Select(s => (s.SetterName, ResolveParamType(s.ParamType), s.ParamName)).ToList();
            _objectDefs[name] = new ObjectType(
                ot.Name, positionals, named, methods, getters, setters,
                ot.EmbeddedTypeName, ot.ConformedInterfaces, ot.Constructors, ot.Unmaker,
                ot.PermanentFields.ToList());   // ★ rebuilt here — dropping it loses the whole feature
        }
        // Also resolve function signatures registered in global scope by Pass1Hoist so
        // InferType on function references returns fully-resolved FunctionTypes directly.
        foreach (var (key, ti) in Scope.ToList())
            if (ti.Type is FunctionType)
                Scope[key] = ti with { Type = ResolveParamType(ti.Type) };
    }

    // Built-in stream binding — seeded into every scope (global and each fresh function scope)
    // so 'the input' is visible everywhere, including inside function bodies.
    private static readonly TypeInfo BuiltinInput =
        new TypeInfo(new ReadableStreamType(CufetType.Text), new VariableReference("input", 0, 0), 0);

    // Flattens statements through Pull...Done scope bodies so that Bind/Object/etc. declarations
    // inside Pull scopes are visible to the hoisting passes (hoisting is transparent to Pull scopes).
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

    // Pass 1: register interfaces (1a), then object types — merged with their 'unto' methods
    // (1b) — then function signatures, excluding 'unto' methods, which are not free
    // functions (1c). Interfaces registered first so object conformance declarations can be
    // validated against them.
    private void Pass1Hoist(Program program)
    {
        foreach (var stmt in FlattenHoistable(program.Statements))
        {
            if (stmt is not InterfaceDefinition ifd) continue;
            _interfaceDefs[ifd.Name] = ifd;
        }

        // Gather 'unto'-attached methods/getters/setters by target type name before building ObjectTypes,
        // so their signatures merge into the type's member set regardless of declaration order.
        var untoMethodsByType  = new Dictionary<string, List<BindStatement>>();
        var untoGettersByType  = new Dictionary<string, List<GetterDeclaration>>();
        var untoSettersByType  = new Dictionary<string, List<SetterDeclaration>>();
        foreach (var stmt in FlattenHoistable(program.Statements))
        {
            // ★ `unto` may not target a bundled book. The book's Cufet layer is an ordinary
            // registered object, so without this an `unto collections` method would splice a
            // writer's member straight onto the layer — the shadowing hole the definition guard
            // below closes, reopened through a side door.
            var (untoName, untoLine, untoCol) = stmt switch
            {
                BindStatement     { UntoType: { } t } b2 => (t, b2.Line, b2.Column),
                GetterDeclaration { UntoType: { } t } g2 => (t, g2.Line, g2.Column),
                SetterDeclaration { UntoType: { } t } s2 => (t, s2.Line, s2.Column),
                _ => (null as string, 0, 0),
            };
            if (untoName != null && IsBundledModuleName(untoName))
                throw TypeError(
                    $"'{untoName}' comes with the language, so 'unto' can't add members to it",
                    null, untoLine, untoCol,
                    $"attach a member unto '{untoName}'",
                    "Define your own module object and pull it alongside the book instead.");

            if (stmt is BindStatement { UntoType: { } mUnt } bind)
            {
                if (!untoMethodsByType.TryGetValue(mUnt, out var mList))
                    untoMethodsByType[mUnt] = mList = [];
                mList.Add(bind);
            }
            else if (stmt is GetterDeclaration { UntoType: { } gUnt } getter)
            {
                if (!untoGettersByType.TryGetValue(gUnt, out var gList))
                    untoGettersByType[gUnt] = gList = [];
                gList.Add(getter);
            }
            else if (stmt is SetterDeclaration { UntoType: { } sUnt } setter)
            {
                if (!untoSettersByType.TryGetValue(sUnt, out var sList))
                    untoSettersByType[sUnt] = sList = [];
                sList.Add(setter);
            }
        }

        foreach (var stmt in FlattenHoistable(program.Statements))
        {
            if (stmt is not ObjectDefinition od) continue;

            // ★ A bundled book's name belongs to the bundled book. The prelude's own definition of
            // it is the one exception — that IS the book's Cufet layer — and it is recognised by
            // reference, so a writer's definition of the same name is refused rather than silently
            // shadowing (or being shadowed by) the book at the pull site.
            if (_instantiationDepth == 0
                && IsBundledModuleName(od.Name)
                && !_preludeStatements.Contains(stmt))
                throw TypeError(
                    $"'{od.Name}' comes with the language, so its name can't be used for a new object",
                    null, od.Line, od.Column,
                    $"define an object named '{od.Name}'",
                    $"Pick another name — 'Pull {od.Name}.' always finds the one the language ships.");

            var methodSigs = od.Methods
                .Select(m => (m.Name, MethodSignature(m, od.Name)))
                .ToList();
            var getterSigs = od.Getters.Select(g => (g.Name, g.ReturnType)).ToList();
            var setterSigs = od.Setters.Select(s => (s.Name, s.ParamType, s.ParamName)).ToList();

            if (untoMethodsByType.Remove(od.Name, out var untoMethods))
            {
                foreach (var um in untoMethods)
                {
                    if (methodSigs.Any(s => s.Name == um.Name))
                        throw TypeError(
                            $"'{od.Name}' already has a method '{um.Name}'",
                            null, um.Line, um.Column,
                            $"declare another method named '{um.Name}' for '{od.Name}'",
                            "Method names must be unique per type, whether declared nested or with 'unto'. Rename one of them.");
                    methodSigs.Add((um.Name, MethodSignature(um, od.Name)));
                }
            }

            if (untoGettersByType.Remove(od.Name, out var untoGetters))
            {
                foreach (var ug in untoGetters)
                {
                    if (getterSigs.Any(g => g.Item1 == ug.Name))
                        throw TypeError(
                            $"'{od.Name}' already has a getter '{ug.Name}'",
                            null, ug.Line, ug.Column,
                            $"declare another getter named '{ug.Name}' for '{od.Name}'",
                            "Getter names must be unique per type. Rename one of them.");
                    getterSigs.Add((ug.Name, ug.ReturnType));
                }
            }

            if (untoSettersByType.Remove(od.Name, out var untoSetters))
            {
                foreach (var us in untoSetters)
                {
                    if (setterSigs.Any(s => s.Item1 == us.Name))
                        throw TypeError(
                            $"'{od.Name}' already has a setter '{us.Name}'",
                            null, us.Line, us.Column,
                            $"declare another setter named '{us.Name}' for '{od.Name}'",
                            "Setter names must be unique per type. Rename one of them.");
                    setterSigs.Add((us.Name, us.ParamType, us.ParamName));
                }
            }

            // ★ A parameterised definition is a TEMPLATE, not a type. Registering it as one would
            // send Pass2ResolveTypes hunting for a type named `element`, which is the writer's
            // blank rather than anything defined — so it is held aside and instantiated on demand,
            // once per filling, the way an interface-taking function is specialised per conformer.
            if (od.TypeParameters is { Count: > 0 })
            {
                _genericObjectDefs[od.Name] = od;
                continue;
            }

            _objectDefs[od.Name] = new ObjectType(
                od.Name, od.PositionalTypes, od.NamedFields, methodSigs,
                getterSigs, setterSigs,
                od.EmbeddedTypeName, od.ConformedInterfaces,
                permanentFields: od.PermanentFields);
        }

        // ★ Blanks on METHODS, which is what a book written in Cufet needs — its members are
        // methods on a module object, and `unique` is `series of element` → `series of element`
        // there just as it is at the top level.
        //
        // ⚠ A SEPARATE pass, after every type NAME is registered, and the ordering is the
        // correctness. Asking IsKnownTypeName inside the loop above consults a half-built table, so
        // a method taking a type defined further down the file reads as a blank — and under the
        // twice rule a method with two such parameters would quietly turn generic instead of
        // erroring, which is precisely what that rule exists to prevent.
        foreach (var stmt in FlattenHoistable(program.Statements))
        {
            if (stmt is not ObjectDefinition od || od.TypeParameters is { Count: > 0 }) continue;
            if (!_objectDefs.TryGetValue(od.Name, out var ot)) continue;

            foreach (var method in od.Methods)
            {
                var blanks = SignatureBlanks(method);
                if (blanks.Count > 0) _genericMethods[(od.Name, method.Name)] = (method, blanks);
            }

            // A held-aside method is not a member yet: its signature names no types. Each FILLING
            // is registered instead, when a call says what fills it.
            if (od.Methods.Any(m => _genericMethods.ContainsKey((od.Name, m.Name))))
                _objectDefs[od.Name] = WithMethods(ot,
                    ot.Methods.Where(m => !_genericMethods.ContainsKey((od.Name, m.MethodName))).ToList());
        }

        // Anything left in unto* dictionaries targets a name that isn't a defined object type.
        foreach (var (targetName, methods) in untoMethodsByType)
        {
            var reason = _interfaceDefs.ContainsKey(targetName)
                ? $"'{targetName}' is an interface, not an object type — methods can't be attached to it with 'unto'"
                : $"'{targetName}' is not a defined object type";
            throw TypeError(
                reason, null, methods[0].Line, methods[0].Column,
                $"declare a method unto '{targetName}'",
                $"'unto' only attaches methods to object types defined in this program. Define 'object {targetName}' first, or check the spelling.");
        }
        foreach (var (targetName, getters) in untoGettersByType)
            throw TypeError(
                $"'{targetName}' is not a defined object type",
                null, getters[0].Line, getters[0].Column,
                $"declare a getter unto '{targetName}'",
                $"'unto' only attaches getters to object types defined in this program. Define 'object {targetName}' first, or check the spelling.");
        foreach (var (targetName, setters) in untoSettersByType)
            throw TypeError(
                $"'{targetName}' is not a defined object type",
                null, setters[0].Line, setters[0].Column,
                $"declare a setter unto '{targetName}'",
                $"'unto' only attaches setters to object types defined in this program. Define 'object {targetName}' first, or check the spelling.");

        foreach (var stmt in FlattenHoistable(program.Statements))
        {
            if (stmt is not BindStatement bind) continue;
            if (bind.UntoType != null) continue; // 'unto' methods are not free functions
            var paramTypes = bind.Parameters.Select(p => p.Type).ToList();

            // ★ A burying function's CALL type is `stash of T`, not T. Recording it here rather than
            // special-casing the cast site means `cast f on (…)` infers a stash with no change to
            // call inference at all — the difference lives entirely in the signature, which is where
            // the difference actually is.
            if (BuriesValues(bind))
            {
                _buryingFunctions.Add(bind.Name);
                if (bind.ReturnType == null)
                    throw TypeError(
                        $"'{bind.Name}' buries values, so it has to say what kind",
                        null, bind.Line, bind.Column,
                        $"declare '{bind.Name}' as void when it buries",
                        $"A burying function's declared type is the type of what it buries: "
                        + $"'Bind number to {bind.Name}' hands back a stash of number.");
            }

            // ★ A function may leave a BLANK too — `Bind series of element to unique, given (the
            // series of element xs)`. It has no slot to declare one in the way an object does, so
            // the signature introduces it: a type name that names nothing, appearing at least
            // TWICE. Twice is what keeps a typo a typo — `given (the nubmer n)` mentions its
            // mistake once, so it stays an unknown type rather than quietly turning the function
            // generic. Every real case uses its blank twice by nature, because the whole point is
            // that two positions agree.
            var blanks = SignatureBlanks(bind);
            if (blanks.Count > 0)
            {
                _genericFunctions[bind.Name] = (bind, blanks);
                continue;   // not an ordinary function: its signature names no types yet
            }

            Scope[bind.Name] = new TypeInfo(
                new FunctionType(paramTypes,
                    _buryingFunctions.Contains(bind.Name) && bind.ReturnType != null
                        ? new StashType(bind.ReturnType)
                        : bind.ReturnType),
                new VariableReference(bind.Name, 0, 0),
                bind.Line);
            _freeBinds[bind.Name] = bind;   // so a pipe can re-check this body with a known input type
        }

        // Gather named constructors ('Bind making a <type> to <name>'), validate their target types,
        // register them on ObjectType.Constructors, and fix up their scope entries so the return type
        // is the canonical ObjectType instance (not the shell produced by the parser).
        var ctorsByType = new Dictionary<string, List<BindStatement>>();
        foreach (var stmt in FlattenHoistable(program.Statements))
        {
            if (stmt is not BindStatement bind || bind.ConstructsTypeName == null) continue;
            if (!ctorsByType.TryGetValue(bind.ConstructsTypeName, out var cList))
                ctorsByType[bind.ConstructsTypeName] = cList = [];
            cList.Add(bind);
        }
        foreach (var (typeName, ctors) in ctorsByType)
        {
            if (!_objectDefs.TryGetValue(typeName, out var ot))
                throw TypeError(
                    $"'{typeName}' is not a defined object type — 'making a {typeName}' has no type to register on",
                    null, ctors[0].Line, ctors[0].Column,
                    $"declare a constructor for '{typeName}'",
                    $"Define 'object {typeName}' before declaring constructors for it, or check the spelling.");

            var newCtorNames = ot.Constructors.ToList();
            foreach (var ctor in ctors)
            {
                if (newCtorNames.Contains(ctor.Name))
                    throw TypeError(
                        $"'{typeName}' already has a constructor named '{ctor.Name}'",
                        null, ctor.Line, ctor.Column,
                        $"declare another constructor named '{ctor.Name}' for '{typeName}'",
                        "Constructor names must be unique per type. Rename one of them.");
                newCtorNames.Add(ctor.Name);

                // Fix up scope entry: resolve the shell ObjectType to the canonical instance.
                var resolvedReturn = ctor.ReturnType is FailureType ft
                    ? (CufetType)new FailureType(ot)
                    : ot;
                var paramTypes = ctor.Parameters.Select(p => p.Type).ToList();
                Scope[ctor.Name] = new TypeInfo(
                    new FunctionType(paramTypes, resolvedReturn),
                    new VariableReference(ctor.Name, 0, 0),
                    ctor.Line);
            }

            _objectDefs[typeName] = new ObjectType(
                ot.Name, ot.PositionalTypes, ot.NamedFields, ot.Methods,
                ot.Getters, ot.Setters, ot.EmbeddedTypeName, ot.ConformedInterfaces,
                newCtorNames, ot.Unmaker, ot.PermanentFields.ToList());
        }

        // Gather destructors ('Bind unmaking a <type> to <name>'), validate, register on ObjectType.Unmaker.
        // Exactly one destructor per type; a second 'unmaking a <type>' is a declaration-time error.
        var unmakeByType = new Dictionary<string, UnmakerDeclaration>();
        foreach (var stmt in FlattenHoistable(program.Statements))
        {
            if (stmt is not UnmakerDeclaration ud) continue;
            if (unmakeByType.ContainsKey(ud.UnmakesTypeName))
                throw TypeError(
                    $"'{ud.UnmakesTypeName}' already has a destructor — 'Bind unmaking a {ud.UnmakesTypeName}' appeared twice",
                    null, ud.Line, ud.Column,
                    $"declare a second destructor for '{ud.UnmakesTypeName}'",
                    "Remove the duplicate. Each type has exactly one destructor — one way to die.");
            unmakeByType[ud.UnmakesTypeName] = ud;
        }
        foreach (var (typeName, ud) in unmakeByType)
        {
            if (!_objectDefs.TryGetValue(typeName, out var ot))
                throw TypeError(
                    $"'{typeName}' is not a defined object type — 'unmaking a {typeName}' has no type to register on",
                    null, ud.Line, ud.Column,
                    $"declare a destructor for '{typeName}'",
                    $"Define 'object {typeName}' before declaring a destructor for it, or check the spelling.");
            _objectDefs[typeName] = new ObjectType(
                ot.Name, ot.PositionalTypes, ot.NamedFields, ot.Methods,
                ot.Getters, ot.Setters, ot.EmbeddedTypeName, ot.ConformedInterfaces,
                ot.Constructors, ud.Name, ot.PermanentFields.ToList());
        }
    }

    private void CheckStatement(IStatement stmt)
    {
        switch (stmt)
        {
            case DefineStatement define:
                CheckDefine(define);
                break;
            case BecomesStatement becomes:
                CheckBecomes(becomes);
                break;
            case StateStatement state:
                _ = InferType(state.Value);
                break;
            case JudgeStatement judge:
            {
                var subjectType = InferType(judge.Subject);

                // ★ What StashTransform needs to split this judgement into steps: `it` becomes an
                // ordinary hoisted local there, and its slot has to hold the subject at its WIDEST
                // — every arm narrows it, so an arm's type would be too small to store.
                if (subjectType != null)
                    RecordStashLocal("it", subjectType, judge.Line, judge.Column);

                // What is still unaccounted for. Each arm removes its cases; RemoveFromUnion
                // returns null once nothing is left. For a subject that is NOT a union it
                // returns the type unchanged, which is exactly what makes `Otherwise` mandatory
                // there — coverage can never be proved for a type with no enumerable cases.
                CufetType? remaining = subjectType;

                foreach (var arm in judge.Arms)
                {
                    foreach (var oneCase in arm.Cases)
                    {
                        // RemoveFromUnion collapses a two-case union to the bare survivor, and
                        // then declines to remove anything from it because it is no longer a
                        // UnionType. The last case therefore has to be retired here, or a
                        // fully-covered judgement reports its final case as unhandled.
                        if (remaining is not null && remaining.Equals(oneCase)) remaining = null;
                        else remaining = RemoveFromUnion(remaining, oneCase);
                    }

                    // `it` IS the narrowing. Binding the subject to a name is what lets the
                    // subject be an arbitrary expression — narrowing is variable-level, so a
                    // bare `If` on the same expression could not do this.
                    var armType = arm.Cases.Count == 1
                        ? arm.Cases[0]
                        : new UnionType(arm.Cases.ToList());

                    EnterScope();
                    Scope["it"] = new TypeInfo(armType, judge.Subject, arm.Line);
                    try { CheckBlock(arm.Body); } finally { ExitScope(); }
                }

                if (judge.OtherwiseBody != null)
                {
                    // Narrowed by elimination to whatever the arms did not take. When the arms
                    // covered everything, `remaining` is null and there is nothing left to narrow
                    // to — the subject's own type is the honest answer, and it is non-null here
                    // because a subject whose type could not be inferred never reaches this far.
                    EnterScope();
                    Scope["it"] = new TypeInfo(remaining ?? subjectType!, judge.Subject, judge.Line);
                    try { CheckBlock(judge.OtherwiseBody); } finally { ExitScope(); }
                }
                else if (remaining != null)
                {
                    throw TypeError(
                        $"this judgement does not cover {FormatType(remaining)}",
                        $"Every case has to be handled, and {FormatType(remaining)} is left over",
                        judge.Line, judge.Column,
                        "leave a case unhandled",
                        "Add an arm for it, or end with 'Otherwise, ...' to say what happens to " +
                        "everything else.");
                }
                break;
            }
            case IfStatement ifStmt:
            {
                // Track union exhaustion across arms: if every arm type-checks the same variable
                // against a closed union, the Otherwise body narrows to what's left.
                string? unionVar = null;
                CufetType? remainingUnionType = null;
                bool canExhaustNarrow = true;

                // ★ The else of a NEGATED test narrows the other way round. `If x is not a text`
                // reaches its Otherwise exactly when x IS a text, so the else names its type
                // outright instead of eliminating down to a residue. Tracked separately because the
                // exhaustion machinery above is about what arms REMOVE, and this removes nothing.
                string? negatedElseVar = null;
                CufetType? negatedElseType = null;

                foreach (var arm in ifStmt.Arms)
                {
                    _ = InferType(arm.Condition);
                    string? narrowedVar = null;
                    CufetType? narrowedTo = null;
                    CufetType? savedNarrowed = null;

                    if (TryGetNotVoidNarrowing(arm.Condition, out var nvTarget, out var nvNarrowed))
                    {
                        narrowedVar = nvTarget;
                        narrowedTo  = nvNarrowed;
                        canExhaustNarrow = false;
                    }
                    else if (TryGetTypeCheckNarrowing(arm.Condition,
                             out var tcTarget, out var tcType, out bool tcNegated))
                    {
                        narrowedVar = tcTarget;
                        if (!tcNegated)
                        {
                            narrowedTo = tcType;
                            // track exhaustion across arms
                            if (canExhaustNarrow)
                            {
                                if (unionVar == null)
                                {
                                    unionVar = tcTarget;
                                    TryLookup(tcTarget!, out var tinfo);
                                    remainingUnionType = tinfo?.Type;
                                }
                                else if (unionVar != tcTarget)
                                    canExhaustNarrow = false;
                                if (canExhaustNarrow)
                                    remainingUnionType = RemoveFromUnion(remainingUnionType, tcType!);
                            }
                        }
                        else
                        {
                            // negated: true-branch narrows to complement
                            TryLookup(tcTarget!, out var tinfo);
                            narrowedTo = RemoveFromUnion(tinfo?.Type, tcType!);
                            canExhaustNarrow = false;

                            // ⚠ A LONE arm only. With several arms an earlier one may have taken
                            // the value already, so reaching the else no longer implies this test
                            // was the one that failed.
                            if (ifStmt.Arms.Count == 1
                                && tinfo?.Type is UnionType { Cases: { } negCases }
                                && negCases.Any(c => c.Equals(tcType)))
                            {
                                negatedElseVar  = tcTarget;
                                negatedElseType = tcType;
                            }
                        }
                    }
                    else if (TryGetDisjunctionNarrowing(arm.Condition, out var djTarget, out var djTypes))
                    {
                        // ★ A group narrows to the sub-union it names, and for exhaustion it is
                        // simply an arm that removes SEVERAL cases at once — so the `Otherwise`
                        // keeps eliminating through it exactly as it does through a single test.
                        narrowedVar = djTarget;
                        narrowedTo  = new UnionType(djTypes!);
                        if (canExhaustNarrow)
                        {
                            if (unionVar == null)
                            {
                                unionVar = djTarget;
                                TryLookup(djTarget!, out var djInfo);
                                remainingUnionType = djInfo?.Type;
                            }
                            else if (unionVar != djTarget) canExhaustNarrow = false;

                            if (canExhaustNarrow)
                                foreach (var one in djTypes!)
                                {
                                    if (remainingUnionType is not null && remainingUnionType.Equals(one))
                                        remainingUnionType = null;
                                    else remainingUnionType = RemoveFromUnion(remainingUnionType, one);
                                }
                        }
                    }
                    else
                    {
                        canExhaustNarrow = false;
                    }

                    if (narrowedVar != null && narrowedTo != null)
                    {
                        _narrowedVars.TryGetValue(narrowedVar, out savedNarrowed);
                        _narrowedVars[narrowedVar] = narrowedTo;
                    }
                    EnterScope();
                    CheckBlock(arm.Body);
                    ExitScope();
                    if (narrowedVar != null && narrowedTo != null)
                    {
                        if (savedNarrowed != null) _narrowedVars[narrowedVar] = savedNarrowed;
                        else _narrowedVars.Remove(narrowedVar);
                    }
                }
                if (ifStmt.ElseBody != null)
                {
                    // Apply exhaustive narrowing for closed unions
                    string? elseNarrowedVar = null;
                    CufetType? elseNarrowedSaved = null;
                    if (canExhaustNarrow && unionVar != null && remainingUnionType != null)
                    {
                        elseNarrowedVar = unionVar;
                        _narrowedVars.TryGetValue(unionVar, out elseNarrowedSaved);
                        _narrowedVars[unionVar] = remainingUnionType;
                    }
                    else if (negatedElseVar != null && negatedElseType != null)
                    {
                        elseNarrowedVar = negatedElseVar;
                        _narrowedVars.TryGetValue(negatedElseVar, out elseNarrowedSaved);
                        _narrowedVars[negatedElseVar] = negatedElseType;
                    }
                    EnterScope();
                    CheckBlock(ifStmt.ElseBody);
                    ExitScope();
                    if (elseNarrowedVar != null)
                    {
                        if (elseNarrowedSaved != null) _narrowedVars[elseNarrowedVar] = elseNarrowedSaved;
                        else _narrowedVars.Remove(elseNarrowedVar);
                    }
                }
                break;
            }
            case WhileStatement whileStmt:
                _ = InferType(whileStmt.Condition);
                EnterScope();
                CheckBlock(whileStmt.Body);
                ExitScope();
                break;
            case RepeatUntilStatement repeatUntil:
                EnterScope();
                CheckBlock(repeatUntil.Body);
                ExitScope();
                _ = InferType(repeatUntil.Condition);
                break;
            case ForEachStatement forEach:
                CheckForEach(forEach);
                break;
            case SeriesInsertStatement add:
                CheckSeriesAdd(add);
                break;
            case SeriesRemoveValueStatement removeVal:
                CheckSeriesRemoveValue(removeVal);
                break;
            case SeriesSetStatement seriesSet:
                CheckSeriesSet(seriesSet);
                break;
            case MatrixSetStatement matrixSet:
                CheckMatrixSet(matrixSet);
                break;
            case RecordNamedSetStatement recordSet:
                CheckRecordNamedSet(recordSet);
                break;
            case PossessiveSetStatement pss:
                CheckPossessiveSet(pss);
                break;
            case MapSetStatement mapSet:
                CheckMapSet(mapSet);
                break;
            case SeriesRemoveAtStatement removeAt:
                CheckSeriesRemoveAt(removeAt);
                break;
            case BindStatement { UntoType: { } } unto:
                CheckUntoMethod(unto);
                break;
            case BindStatement { ConstructsTypeName: { } } ctor:
                CheckConstructor(ctor);
                break;
            // A function that left blanks cannot have its body checked: its signature names types
            // that do not exist yet. Each FILLING is checked instead, as the ordinary function it
            // becomes — so one nothing calls is never checked, exactly like an unused template.
            case BindStatement templateBind
                when _genericFunctions.TryGetValue(templateBind.Name, out var held)
                     && ReferenceEquals(held.Bind, templateBind):
                break;
            case BindStatement bind:
                // Top-level Bind: already in Scope (= global scope) from Pass1Hoist — skip.
                // Non-top-level Bind (inside a function or block): register in current scope
                // so the function can be returned/passed and can recurse on itself.
                if (!Scope.ContainsKey(bind.Name))
                {
                    var paramTypes = bind.Parameters.Select(p => p.Type).ToList();
                    // A NESTED function buries on exactly the same terms as a top-level one — see
                    // Pass1Hoist, which does this for free functions. Registering it only there left
                    // an inner generator unrecognised, so its missing terminal `Return` was reported
                    // as an error in a function that was never going to return.
                    if (BuriesValues(bind)) _buryingFunctions.Add(bind.Name);
                    Scope[bind.Name] = new TypeInfo(
                        new FunctionType(paramTypes,
                            _buryingFunctions.Contains(bind.Name) && bind.ReturnType != null
                                ? new StashType(bind.ReturnType)
                                : bind.ReturnType),
                        new VariableReference(bind.Name, 0, 0),
                        bind.Line);
                }
                CheckBind(bind);
                break;
            case CastStatement cs:
            {
                // ★ An axiom called for its EFFECT — `Cast close-dir on (handle).` The expression
                // form has always had this hook (InferCastExpr); the statement form did not, so a
                // discarded axiom call fell through to ResolveForCast and was refused as "not a
                // function — you can only cast functions", which is not true of axioms and told
                // the writer to bind a result they had deliberately thrown away.
                if (AxiomCalledBy(cs.Function) is { } statementAxiom)
                {
                    RunAxiomOnCastStatement(cs, statementAxiom);
                    break;
                }
                // ★ The same call with no literal behind it — an axiom that arrived as a value.
                // AxiomCalledBy answers null there by design, so without this the call falls
                // through to ResolveForCast and is refused as "not a function", which is the
                // wrong sentence for the one case that IS legal.
                if (cs.Function is VariableReference axiomValueCall
                    && TryLookup(axiomValueCall.Name, out var axiomValueInfo)
                    && axiomValueInfo!.Type is AxiomType heldAxiom)
                {
                    if (heldAxiom.ReturnType is null)
                        throw AxiomSourceNotReachable(heldAxiom, cs.Line, cs.Column);
                    RunAxiomValueOnCastStatement(cs, heldAxiom);
                    break;
                }
                if (cs.Function is VariableReference gcv && _genericFunctions.ContainsKey(gcv.Name))
                    cs.ResolvedFunctionName = InstantiateFunction(gcv.Name, cs.Args, cs.Line, cs.Column);
                // ⚠ Never overwritten once set — see the matching note in InferCastExpr.
                else if (cs.ResolvedFunctionName is null
                         && cs.Function is PossessiveAccess gcp
                         && MemberOwnerType(InferType(gcp.Target)) is ObjectType gcOwner)
                    cs.ResolvedFunctionName = InstantiateMethod(gcOwner, gcp.Member, cs.Args, cs.Line, cs.Column);

                var (funcType, displayName, declLine, argsToValidate) =
                    ResolveForCast(cs.Function, cs.Args, cs.Line, cs.Column,
                                   out var statementAxiomCallee, cs.ResolvedFunctionName);
                if (statementAxiomCallee is { } statementHeld)
                {
                    if (statementHeld.ReturnType is null)
                        throw AxiomSourceNotReachable(statementHeld, cs.Line, cs.Column);
                    RunAxiomValueOnCastStatement(cs, statementHeld);
                    break;
                }
                if (funcType != null)
                {
                    ValidateCastArgs(funcType, displayName, declLine, argsToValidate, cs.Line, cs.Column);
                    if (!_inTryBlock && funcType.ReturnType is FailureType)
                        throw TypeError(
                            $"{displayName} can fail — you must handle the failure",
                            null, cs.Line, cs.Column,
                            $"call a fallible function without handling the failure",
                            "Wrap this call in a 'Try to: / In case of failure:' block.");
                }
                break;
            }
            // ★ `Have <rabbit> bury <value>.` — the rabbit must be NAMED, and the bare `rabbit`
            // keyword cannot serve. A function body starts outside any region (CheckBind resets the
            // rabbit depth), so there is no enclosing rabbit for the keyword to mean — which is
            // exactly right: the agent doing the burying is handed IN, normally as a parameter, and
            // that is what puts the ownership at the call site rather than leaving it ambient.
            case BuryStatement bury:
            {
                _ = InferType(bury.Value);
                if (bury.RabbitName is not { } agent)
                    throw TypeError(
                        "a bury has to name the rabbit doing it",
                        "'rabbit' means the enclosing one, and a function body is not inside one",
                        bury.Line, bury.Column,
                        "bury with the bare 'rabbit' keyword inside a function",
                        "Take a rabbit as a parameter and name it: "
                        + "'given (the rabbit helper, ...)' then 'Have helper bury <value>.'");

                if (!TryLookup(agent, out var agentInfo) || !IsRabbitType(agentInfo!.Type))
                    throw TypeError(
                        $"'{agent}' is not a rabbit",
                        "Only a rabbit can be told to bury something",
                        bury.Line, bury.Column,
                        $"have '{agent}' bury a value",
                        $"Declare it as one — 'given (the rabbit {agent}, ...)' — or pull one with "
                        + $"'Pull a rabbit as {agent}.'");
                break;
            }

            case ReturnStatement ret:
                CheckReturn(ret);
                break;
            case TryStatement trySt:
                CheckTryStatement(trySt);
                break;
            case SuppressStatement ss:
                if (!_inExceptionHandler)
                    throw TypeError(
                        "'Suppress the exception.' is only valid inside an exception handler",
                        null, ss.Line, ss.Column,
                        "suppress an exception outside an exception handler",
                        "Move 'Suppress the exception.' inside an 'In case of exception' block.");
                break;
            case CurrentDirectorySetStatement cd:
                CheckCurrentDirectorySet(cd);
                break;
            case FileWriteStatement fw:
                CheckFileWrite(fw);
                break;
            case WithOpenStatement wos:
                CheckWithOpen(wos);
                break;
            case PullRabbitStatement prs:
                CheckPullRabbit(prs);
                break;
            case LaunchTaskStatement lts:
                CheckLaunchTask(lts);
                break;
            case SendStatement ss:
                CheckSend(ss);
                break;
            case CloseStatement cs:
                CheckClose(cs);
                break;
            case PullStatement ps:
                CheckPullStatement(ps);
                break;
            case WriteToStreamStatement wts:
                CheckWriteToStream(wts);
                break;
            case ObjectDefinition { TypeParameters.Count: > 0 }:
                // A template's own body cannot be checked: `element` is a blank, not a type. Each
                // FILLING is checked instead, as the ordinary definition it becomes — so a template
                // used nowhere is never checked at all, exactly like an unused generic elsewhere.
                break;
            case ObjectDefinition od:
                CheckObjectDefinition(od);
                break;
            case InterfaceDefinition:
                break; // already hoisted in Pass1
            case AcknowledgeInterruptStatement:
                break; // always valid; no type constraints
            case YieldStatement:
                break; // cooperative yield — valid in any context
            case SeedChanceStatement ss2:
                CheckSeedChanceStatement(ss2);
                break;
            case GetterDeclaration { UntoType: { } } untoGetter:
                CheckUntoGetter(untoGetter);
                break;
            case GetterDeclaration:
                break; // inline getter already checked inside CheckObjectDefinition
            case SetterDeclaration { UntoType: { } } untoSetter:
                CheckUntoSetter(untoSetter);
                break;
            case SetterDeclaration:
                break; // inline setter already checked inside CheckObjectDefinition
            case UnmakerDeclaration ud:
                CheckUnmake(ud);
                break;
            case OperatorOverloadDeclaration:
                break; // already body-checked in Pass2CheckOverloads
            case PipeExpression pipe:
                CheckPipe(pipe);
                break;
            case OutputStatement os:
                CheckOutputStatement(os);
                break;
            case ForEachFromInputStatement fe:
                CheckForEachFromInput(fe);
                break;
        }
    }

    private void CheckDefine(DefineStatement define)
    {
        // ★ First, because an axiom has no type until the declaration gives it one. This also
        // resolves the shortened tag spelling, so `declared` below is an AxiomType either way.
        var declaredType = TagAxiomDeclaration(define);
        // `Define the number fd as cast open-file on (path, flags).` — running an axiom, where the
        // type declared HERE is what comes back. Asked before the value's type is inferred, for the
        // same reason CheckReturn asks: a cast of an axiom has no type of its own to infer.
        // ★ `Define alias as answer.` — binding one axiom name to another, which is the one place
        // an axiom name is a VALUE rather than something being run. Taken before InferType for the
        // same reason CheckReturn takes its case first: the guard there refuses an axiom reached as
        // a value, and this is the reading that is legal. Both names then resolve to the same
        // source, so neither backend needs an axiom to exist at run time.
        if (define.Value is VariableReference && AxiomBehind(define.Value) is not null
            && TryLookup(((VariableReference)define.Value).Name, out var aliasInfo)
            && aliasInfo!.Type is AxiomType aliasedAxiom)
        {
            Scope[define.Name] = new TypeInfo(aliasedAxiom, define.Value, define.Line, true, _rabbitDepth);
            RecordStashLocal(define.Name, aliasedAxiom, define.Line, define.Column);
            return;
        }
        var type = InferType(define.Value);
        if (type == null)
            throw TypeError(
                $"the type of the value for '{define.Name}' can't be determined",
                null,
                define.Line, define.Column,
                "define a variable without a clear starting type",
                "Start with a literal value or a defined variable so the type is clear from the beginning.");

        // ⚠ A hoisted shared constant reaching its OWN declaration is not a redefinition — the entry
        // in scope is the one Pass2HoistSharedConstants put there so bodies above could read it.
        // Removed as it is claimed, so a genuine second `Define` of the name still collides.
        bool reclaimingHoist = define.Permanent && _hoistedConstants.Remove(define.Name);

        if (Scope.ContainsKey(define.Name) && !reclaimingHoist)
            throw TypeError(
                $"'{define.Name}' is already defined in this scope",
                null, define.Line, define.Column,
                $"define '{define.Name}' again in the same block",
                "Each name can only be defined once per block. Use 'becomes' to reassign it, or choose a different name.");

        // ★ Shadowing cannot survive linearisation. The state machine flattens every scope in the
        // body into one, so an inner `x` and the outer `x` it deliberately hides would become the
        // same slot — the shadow would silently clobber what it was written to protect.
        if (define.Shadow && _recordingStashFn != null)
            throw TypeError(
                $"'{_recordingStashFn}' buries, so it can't shadow '{define.Name}'",
                "A burying function is rewritten into one flat block of state, and a shadowed name would land on top of the name it hides",
                define.Line, define.Column,
                $"shadow '{define.Name}' inside a burying function",
                "Give the inner one a different name.");

        if (TryLookupOuter(define.Name, out var outer))
        {
            if (!define.Shadow)
                throw TypeError(
                    $"'{define.Name}' already exists in an enclosing scope",
                    $"It was defined on line {outer.EstablishingLine}",
                    define.Line, define.Column,
                    $"declare '{define.Name}' in this block without shadowing the outer one",
                    $"To deliberately shadow it, write 'Define a shadow {define.Name} as ...'.");
        }
        else if (define.Shadow)
        {
            throw TypeError(
                $"'a shadow {define.Name}' — there's nothing named '{define.Name}' in an enclosing scope to shadow",
                null, define.Line, define.Column,
                $"shadow a name that doesn't exist in any enclosing scope",
                $"Remove 'a shadow' if you're just defining a new variable, or check the spelling.");
        }

        // An explicit annotation is the binding's type; the value only has to fit into it. That is
        // what lets `Define the (number or text) x as 42.` hold a text later.
        var declared = declaredType;
        if (declared != null && !IsAssignable(declared, type))
            throw TypeError(
                $"'{define.Name}' is declared as {FormatType(declared)}, but the value is a {FormatType(type)}",
                null, define.Line, define.Column,
                $"start '{define.Name}' with a value that isn't a {FormatType(declared)}",
                $"Give it a {FormatType(declared)}, or change the declared type to match the value.");

        // ★ An axiom binding is permanent whether or not it says so. The text is fixed at the
        // declaration and there is no other value it could take, so this is not a new rule — it is
        // the `permanently` carve-out (ImportTopLevelVisible) applying to something that cannot be
        // mutated, which is what makes `Bind number to process-id, get-pid.` see the axiom above it.
        bool permanent = define.Permanent || declared is AxiomType;
        RequireRabbitForAddress(declared ?? type, define.Name, define.Line, define.Column);
        Scope[define.Name] = new TypeInfo(declared ?? type, define.Value, define.Line, permanent, _rabbitDepth);
        RecordStashLocal(define.Name, declared ?? type, define.Line, define.Column);
    }

    private void CheckBecomes(BecomesStatement becomes)
    {
        // Reassignment invalidates any active narrowing on this variable.
        _narrowedVars.Remove(becomes.Name);

        if (!TryLookup(becomes.Name, out var existing))
            return;

        if (existing.Permanent)
            throw TypeError(
                $"'{becomes.Name}' is permanent",
                $"It was fixed with a value on line {existing.EstablishingLine} and can't be reassigned",
                becomes.Line, becomes.Column,
                "reassign it",
                "If it needs to change, define it without 'permanently'.");

        var rhsType = InferType(becomes.Value);
        if (rhsType == null) return;

        if (!IsAssignable(existing.Type, rhsType))
            throw TypeError(
                $"'{becomes.Name}' holds {FormatTypePlural(existing.Type)}",
                $"You set it to {FormatExpr(existing.EstablishingExpr)} on line {existing.EstablishingLine}, so it can only ever hold {FormatTypePlural(existing.Type)}",
                becomes.Line, becomes.Column,
                $"give it a {FormatType(rhsType)} value",
                $"Variables keep their type for life. If you need a {FormatType(rhsType)} value here, define a new name for it instead.");

        // Region invariant: don't let a shorter-lived reference escape into longer-lived storage.
        CheckRegionStore(becomes.Value, rhsType, existing.RabbitDepth, becomes.Line, becomes.Column,
            $"reassign '{becomes.Name}' to a value from a shorter-lived rabbit region");
        // ESC.1 — annotate (never reject) so the compiler can copy an escaping value outward.
        becomes.EscapeToDepth = EscapeDepthFor(becomes.Value, rhsType, existing.RabbitDepth);
    }

    private void CheckReturn(ReturnStatement ret)
    {
        if (_inferringLambdaReturn)
        {
            var retType = ret.Value != null ? InferType(ret.Value) : null;
            // Inside a fallible overload body, failure returns skip the type-set so the
            // success type drives _expectedReturnType; _overloadBodyIsFallible wraps it.
            // 'Return a failure.' (no message) parses as VariableReference("the failure"),
            // so check the AST node directly in addition to the inferred type.
            if (_overloadBodyIsFallible &&
                (retType is FailureMarkerType || IsFailureExpr(ret.Value)))
                return;
            // For fallible overload bodies, wrap the inferred success type immediately so
            // subsequent failure returns validate against FailureType(T) rather than T.
            _expectedReturnType    = _overloadBodyIsFallible && retType != null
                                        ? new FailureType(retType)
                                        : retType;
            _inferringLambdaReturn = false;
            return;
        }

        if (_expectedReturnType == null) // void function (or _inFunction guard is parser-level)
        {
            if (ret.Value != null)
                throw TypeError(
                    "this function is declared void — it gives nothing back",
                    null,
                    ret.Line, ret.Column,
                    "return a value from a void function",
                    "Remove the value, or change the function's return type if you need to produce a result.");
            // bare return in void → ok
        }
        else // non-void function
        {
            if (ret.Value == null)
                throw TypeError(
                    $"this function is declared to give back a {FormatType(_expectedReturnType)}",
                    $"You declared the return type as {FormatType(_expectedReturnType)} on line {_functionDeclarationLine}",
                    ret.Line, ret.Column,
                    "return without a value",
                    $"Provide a {FormatType(_expectedReturnType)} value to return.");

            // ★ Asked BEFORE the type is inferred, because returning an axiom is the one place a
            // name bound to one may be reached for at all — see the guard in InferTypeCore. What
            // happens next is RunAxiomOnReturn's: an axiom runs when it is returned, and the
            // declared type decides what it becomes.
            // ⚠ Returning an axiom INTO an axiom type hands the axiom itself back, unrun. Only a
            // return into some other type runs it — which is the whole of "an axiom runs when it is
            // returned", now that there is a type a function can give one back as.
            var returnType = AxiomBehind(ret.Value) is not null && _expectedReturnType is not AxiomType
                ? RunAxiomOnReturn(ret, _expectedReturnType)
                : InferType(ret.Value);
            // Returning an axiom into something that is NOT an axiom type means running it, and
            // running needs its source — which a parameter or another call's result does not carry.
            if (returnType is AxiomType unreachable && _expectedReturnType is not null
                && _expectedReturnType is not AxiomType)
                throw AxiomSourceNotReachable(unreachable, ret.Line, ret.Column);
            if (IsRabbitType(returnType))
                throw TypeError(
                    "rabbits cannot be returned — they flow downward only",
                    null, ret.Line, ret.Column,
                    "return a rabbit from a function",
                    "Pass the rabbit as an argument instead, or return a value that lives in it (a handle into the rabbit).");
            if (returnType != null && !IsAssignable(_expectedReturnType!, returnType))
                throw TypeError(
                    $"this function is declared to give back a {FormatType(_expectedReturnType!)}",
                    $"You declared the return type as {FormatType(_expectedReturnType!)} on line {_functionDeclarationLine}",
                    ret.Line, ret.Column,
                    $"return a {FormatType(returnType)} value",
                    $"Change the returned value to a {FormatType(_expectedReturnType!)}.");
        }
    }

    private static void CheckIndex(IExpression index, int line, int col)
    {
        if (index is NumberLiteral { Value: var v } && v % 1 != 0)
            throw TypeError(
                "item positions must be whole numbers",
                null,
                line, col,
                $"use {v} as a position",
                "Positions are counted 1, 2, 3 and so on. Use a whole number.");
    }

    // Public entry point: infers a type and resolves any ObjectType placeholder that survived
    // into the result (belt-and-suspenders after Pass2ResolveTypes handles _objectDefs).
    private CufetType? InferType(IExpression expr)
    {
        var t = InferTypeCore(expr);
        // ⚠ An axiom type is INFERRED here and refused by ResolveParamType, which is what makes
        // "written down anywhere" and "the type of the declaration that names it" different things.
        return t is null or AxiomType ? t : ResolveParamType(t);
    }

    // `<bits> at <n> bits`. The impossible case is caught at CHECK time when it is knowable: a
    // literal value with a literal width that cannot hold it. Everything else is a runtime error,
    // in the same class as dividing by zero -- not a `failure`, which would force a `Try` around an
    // operation that is almost always fine.
    private CufetType InferBitsAtWidth(BitsAtWidth baw)
    {
        var target = InferType(baw.Target);
        if (target is not null and not BitsType)
            throw TypeError(
                $"'at ... bits' states the width of a bits value, but this is a {FormatType(target)}",
                null, baw.Line, baw.Column,
                "state a width on something that is not a bits value",
                "Only a bits value carries a width, as in '0b0 at 3 bits'.");

        var width = InferType(baw.Width);
        if (width is not null and not NumberType)
            throw TypeError(
                $"a stated width must be a number, but this is a {FormatType(width)}",
                null, baw.Line, baw.Column,
                "state a width that is not a number",
                "Write the count of bits, as in '0b0 at 3 bits'.");

        if (baw.Target is BitsLiteral bl && baw.Width is NumberLiteral nl)
        {
            if (nl.Value < 0 || nl.Value != decimal.Truncate(nl.Value))
                throw TypeError(
                    $"a stated width must be a whole number of bits, not {nl.Value}",
                    null, baw.Line, baw.Column, "state a fractional or negative width",
                    "Write a whole count, as in '0b0 at 3 bits'.");
            int needed = 64;
            while (needed > 0 && (bl.Value >> (needed - 1)) == 0) needed--;
            if ((int)nl.Value < needed)
                throw TypeError(
                    $"{(int)nl.Value} bits cannot hold this value - it needs {needed}",
                    null, baw.Line, baw.Column,
                    $"narrow a value to {(int)nl.Value} bits",
                    "Widening is always fine; narrowing is refused when it would drop a set bit, " +
                    "because a packer that loses its high bits writes data that decodes to garbage. " +
                    "Mask with 'and' if dropping them is what you meant.");
        }
        return CufetType.Bits;
    }

    // Top-level data names hidden from the detached body currently being checked. Empty everywhere
    // else. Set by ImportTopLevelVisible; see the note there for why an unresolved name was not
    // enough on its own.
    private HashSet<string> _hiddenTopLevelData = new(StringComparer.Ordinal);

    // ★ The ONE place the top-level visibility rule lives. Every body that runs detached from the
    // top-level scope — a top-level function, a method, a getter, a setter, a destructor, an
    // operator overload, a top-level lambda — imports exactly this: function signatures (so mutual
    // recursion resolves) and `permanently` constants.
    //
    // The old rule hid all top-level data, justified as "keeps data flow explicit and prevents
    // hidden mutation". A `permanently` binding cannot be mutated, so none of that applies to it —
    // the rule was broader than the reason for it. Letting exactly the immutable half through gives
    // back shared constants without letting global mutable state in. This is what `static` would
    // have been, minus the part worth refusing.
    //
    // ★ It also records what it filtered OUT. Isolating the scope hides those names, but an
    // unresolved name infers to null and the check passes SILENTLY — so a program reading top-level
    // data from a detached body type-checked clean, refused at RUNTIME interpreted, and emitted
    // undeclared C compiled. Three answers to one program. Recording the hidden names lets
    // InferTypeCore refuse at check time, identically for both backends.
    //
    // ⚠ Callers must save _hiddenTopLevelData before calling and restore it in their finally — a
    // nested body must not inherit an outer body's hidden set after the outer scope is restored.
    private void ImportTopLevelVisible(
        (List<Dictionary<string, TypeInfo>> V, List<Dictionary<string, CufetType>> T) saved)
    {
        foreach (var scope in saved.V)
            foreach (var (k, v) in scope.Where(kv => kv.Value.Type is FunctionType || kv.Value.Permanent))
                Scope[k] = v;

        _hiddenTopLevelData = saved.V
            .SelectMany(s => s)
            .Where(kv => kv.Value.Type is not FunctionType && !kv.Value.Permanent)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    // The same rule the interpreter enforced at runtime, moved to check time so both backends
    // refuse identically — and so `check` stops passing a program that cannot run. The wording
    // follows the interpreter's original message, which was already the clearest thing about this.
    private TypeException HiddenTopLevelDataError(VariableReference vr) =>
        TypeError(
            $"'{vr.Name}' is a top-level value, but function and method bodies can't see top-level data",
            null, vr.Line, vr.Column,
            $"read '{vr.Name}' inside a top-level function or a method",
            $"They see other functions (for mutual recursion) and top-level `permanently` constants, " +
            $"but not top-level data that can change. This keeps data flow explicit and prevents " +
            $"hidden mutation.\n" +
            $"Declare it `Define {vr.Name} as <value> permanently.` if it never changes, or pass " +
            $"'{vr.Name}' in as a parameter, or define the function inside a scope where " +
            $"'{vr.Name}' is already bound, so it captures '{vr.Name}' as a closure.");

    // Returns null for genuine inference gaps (undeclared variable, unhandled expression form).
    // Returns a concrete CufetType for anything we can type statically.
    // Throws TypeException for operand type mismatches.
    // `unbury s` gives `voidable T` — the next value, or void once the stash is spent. A voidable
    // rather than a separate "is it done" question, so the spent case is narrowed exactly like any
    // other absent value and cannot be forgotten.
    private CufetType? InferUnbury(UnburyExpression unbury)
    {
        var inner = InferType(unbury.Stash);
        if (inner is StashType stash) return new VoidableType(stash.ElementType);

        throw TypeError(
            inner == null
                ? "'unbury' needs a stash, and the type of what it was given can't be determined"
                : $"'unbury' needs a stash, but this is {FormatType(inner)}",
            null, unbury.Line, unbury.Column,
            "unbury something that is not a stash",
            "A stash comes from calling a function that buries: 'Define s as cast walk on (tree).'");
    }

    private CufetType? InferTypeCore(IExpression expr) => expr switch
    {
        NumberLiteral                                                                                    => CufetType.Number,
        BitsLiteral                                                                                     => CufetType.Bits,
        BitsAtWidth baw                                                                                 => InferBitsAtWidth(baw),
        BitsShift bs                                                                                    => InferBitsShift(bs),
        StringLiteral                                                                                    => CufetType.Text,
        AxiomLiteral axiom                                                                               => InferAxiomLiteral(axiom),
        BooleanLiteral                                                                                   => CufetType.Fact,
        VoidLiteral                                                                                      => CufetType.Void,
        UnburyExpression unbury                                                                          => InferUnbury(unbury),
        UnaryExpression unary                                                                            => InferUnary(unary),
        BinaryExpression bin                                                                             => InferBinary(bin),
        VariableReference { Name: var n } when _narrowedVars.TryGetValue(n, out var narrowed)           => narrowed,
        // An axiom reached for by name, anywhere but a return. `State get-pid.` used to check
        // clean, print a C# object interpreted, and emit C that would not build — three answers to
        // one program, which is the shape a use guard exists to close. CheckReturn takes the legal
        // case before it ever asks for a type, so nothing that reaches here is one.
        // An axiom reached for by name, anywhere but a return, a cast, or a `Define` binding it to
        // another name. `State get-pid.` used to check clean, print a C# object interpreted, and
        // emit C that would not build — three answers to one program, which is the shape a use
        // guard exists to close. CheckReturn and CheckDefine take the legal cases before they ever
        // ask for a type, so nothing that reaches here is one.
        // ⚠ Only an axiom that never said what it gives BACK. One that did is a VALUE now — it can
        // be passed, stored and handed back — so it falls through to the ordinary name lookup below
        // and carries its AxiomType with it. What it still cannot do is be PRINTED, and that is the
        // compiler's own clean refusal rather than a rule repeated here: `State <a function>` is
        // refused the same way, and an axiom is a callable.
        VariableReference { Name: var an } when TryLookup(an, out var ati)
                                            && ati.Type is AxiomType { ReturnType: null } axiomInfo
                                                                                                 => throw AxiomUsedAsValue(an, axiomInfo, expr),
        VariableReference { Name: var n } when TryLookup(n, out var ti)                                  => NoteModuleUse(n, ti),
        VariableReference hv when _hiddenTopLevelData.Contains(hv.Name)                                  => throw HiddenTopLevelDataError(hv),
        VariableReference vr                                                                              => NoteUnresolvedName(vr),
        SeriesLiteral lit                                                                                => InferSeriesLiteral(lit),
        SeriesAccess acc                                                                                 => InferSeriesAccess(acc),
        SeriesLength sl                                                                                  => InferSeriesLength(sl),
        CastExpression cast                                                                              => InferCastExpr(cast),
        RecordLiteral lit                                                                                => InferRecordLiteral(lit),
        RecordNamedAccess rna                                                                            => InferRecordNamedAccess(rna),
        ObjectLiteral lit                                                                                => InferObjectLiteral(lit),
        PossessiveAccess poss                                                                            => InferPossessiveAccess(poss),
        TextJoin tj                                                                                      => InferTextJoin(tj),
        TextConvert tc                                                                                   => InferTextConvert(tc),
        NumberConvert nc                                                                                 => InferNumberConvert(nc),
        BitsConvert bc                                                                                  => InferBitsConvert(bc),
        TextLength tl                                                                                    => InferTextLength(tl),
        ForeignTextAt fta                                                                                => InferForeignTextAt(fta),
        TextSplit split                                                                                  => InferTextSplit(split),
        TextContains contains                                                                            => InferTextContains(contains),
        TextFind find                                                                                    => InferTextFind(find),
        TextSubstringRange range                                                                         => InferTextSubstringRange(range),
        TextSubstringEdge edge                                                                           => InferTextSubstringEdge(edge),
        TextReplace replace                                                                              => InferTextReplace(replace),
        TextCase tcase                                                                                   => InferTextCase(tcase),
        TextTrim trim                                                                                    => InferTextTrim(trim),
        SortExpression sort                                                                              => InferSort(sort),
        RangeExpression re                                                                               => InferRangeExpr(re),
        ButVoidDefault bvd                                                                               => InferButVoidDefault(bvd),
        ConditionalExpression ce                                                                         => InferConditional(ce),
        FailureLiteral fl                                                                                => InferFailureLiteral(fl),
        FailureFallback ff                                                                               => InferFailureFallback(ff),
        FailurePropagate fp                                                                              => InferFailurePropagate(fp),
        MapLiteral ml                                                                                    => InferMapLiteral(ml),
        MapLookup  mlu                                                                                   => InferMapLookup(mlu),
        MapHasKey  mhk                                                                                   => InferMapHasKey(mhk),
        MapHasEntry mhe                                                                                  => InferMapHasEntry(mhe),
        MapSize    ms                                                                                    => InferMapSize(ms),
        LambdaLiteral lambda                                                                             => InferLambdaLiteral(lambda),
        ReadExpression re                                                                                 => InferReadExpr(re),
        FileReadExpression fre                                                                           => InferFileReadExpr(fre),
        RunExpression run                                                                                => InferRunExpr(run),
        MatrixLiteral ml                                                                                 => InferMatrixLiteral(ml),
        MatrixSized   mz                                                                                 => InferMatrixSized(mz),
        MatrixAccess  ma                                                                                 => InferMatrixAccess(ma),
        IsTypeCheck   tc                                                                                 => InferIsTypeCheck(tc),
        EnvironmentVariableExpression env                                                                => InferEnvVar(env),
        // Voidable for the same reason the environment variable is: the answer comes from the OS
        // and the OS is allowed to have none. Void only when the directory was removed underneath
        // the process, which is rare enough that nobody writes for it and real enough to not lie.
        CurrentDirectoryExpression                                                                       => new VoidableType(CufetType.Text),
        DirectoryContentsExpression   dce                                                                => InferDirectoryContents(dce),
        PathCheckExpression           pce                                                                => InferPathCheck(pce),
        InterruptRequestedExpression                                                                     => CufetType.Fact,
        RandomNumber  rn                                                                                 => InferRandomNumber(rn),
        RandomItem    ri                                                                                 => InferRandomItem(ri),
        RandomlyShuffled rs                                                                              => InferRandomlyShuffled(rs),
        RandomGuess   rg                                                                                 => InferRandomGuess(rg),
        ChannelCreation cc                                                                               => InferChannelCreation(cc),
        DeliveryExpression de                                                                            => InferDeliveryExpression(de),
        AwaitedResultExpression are                                                                      => InferAwaitedResultExpression(are),
        PipeExpression pipe                                                                              => InferSubprocessPipeExpr(pipe),
        _                                                                                                => null,
    };

    private CufetType InferEnvVar(EnvironmentVariableExpression env)
    {
        var nameType = InferType(env.Name);
        if (nameType != null && nameType != CufetType.Text)
            throw TypeError(
                "the environment variable name must be text",
                null, env.Line, env.Column,
                $"use a {FormatType(nameType)} as an environment variable name",
                "The variable name must be a text expression (a string literal or a text variable).");
        return new VoidableType(CufetType.Text);
    }

    private CufetType InferReadExpr(ReadExpression re)
    {
        var sourceType = InferType(re.Source);
        if (sourceType != null && sourceType is not ReadableStreamType { ElementType: TextType })
            throw TypeError(
                "read expects a readable stream of text",
                null, re.Line, re.Column,
                $"read from a {FormatType(sourceType)}",
                "Use a readable stream of text as the source — 'the input' is always available, or use 'With the file ... open for reading as s:' for a file stream.");

        return re.Form switch
        {
            ReadForm.Line     => new VoidableType(CufetType.Text),
            ReadForm.All      => CufetType.Text,
            ReadForm.AllLines => new SeriesType(CufetType.Text),
            _                 => throw new InvalidOperationException($"Unknown ReadForm {re.Form}"),
        };
    }

    // <bits> shifted left|right by <number>. Shifting is wiring, not a gate — it moves bits
    // rather than combining them — so it is a transform of its own rather than an operator, and
    // the amount is a COUNT OF POSITIONS: a quantity, like the 3 in 'item 3 of s', not a pattern.
    private CufetType? InferBitsShift(BitsShift shift)
    {
        var target = InferType(shift.Target);
        var amount = InferType(shift.Amount);

        if (target != null && target != CufetType.Bits)
            throw TypeError(
                "shifting works on bits",
                null, shift.Line, shift.Column,
                $"shift a {FormatType(target)}",
                target == CufetType.Number
                    ? "A number is a quantity, and has no bit positions to move. Write the " +
                      "value as a bit pattern (0xFF, 0b1010, 0o755) if that is what you meant."
                    : "Only a bits value can be shifted.");

        if (amount != null && amount != CufetType.Number)
            throw TypeError(
                "the shift amount counts positions, so it is a number",
                null, shift.Line, shift.Column,
                $"shift by a {FormatType(amount)}",
                "Write how many places to move as an ordinary number — 'flags shifted left by 3' " +
                "— not as a bit pattern. It is a count, like the 3 in 'item 3 of s'.");

        return CufetType.Bits;
    }

    private CufetType? InferUnary(UnaryExpression unary)
    {
        var operand = InferType(unary.Operand);
        if (operand == null) return null;
        if (unary.Op == TokenType.Not)
        {
            if (operand == CufetType.Fact) return CufetType.Fact;
            // On bits, 'not' flips every bit inside the value's own width — 'not 0xFF' is 0x00.
            // The type is unsigned and has a width, which is exactly why that is the answer
            // rather than the -6 a signed reading would give.
            if (operand == CufetType.Bits) return CufetType.Bits;
            throw TypeError(
                "'not' flips a fact or a bit pattern",
                null,
                unary.Line, unary.Column,
                $"negate a {FormatType(operand)} value",
                operand == CufetType.Number
                    ? "A number is a quantity, and has no bits to flip. Write the value as a " +
                    "bit pattern (0xFF, 0b1010, 0o755) if that is what you meant, or use " +
                    "unary minus if you wanted to negate the quantity."
                    : "Make sure the value you're negating is a fact (a true or false value) " +
                    "or a bits value. Write a comparison like 'x is 5' if you need one.");
        }
        // unary minus
        if (operand == CufetType.Number) return CufetType.Number;
        throw TypeError(
            "unary minus works on numbers only",
            null,
            unary.Line, unary.Column,
            $"negate a {FormatType(operand)} value",
            operand == CufetType.Bits
                ? "Bits are unsigned — a bit pattern has no negative. Did you mean 'not', " +
                "which flips every bit within the value's width?"
                : "Make sure the value you're negating is a number.");
    }

    private CufetType? InferBinary(BinaryExpression bin)
    {
        var left  = InferType(bin.Left);
        var right = InferType(bin.Right);

        if (left == null || right == null) return null;

        var l = left;
        var r = right;

        // Operator overload: same-type object operands with a registered overload take
        // priority over the numeric path. Dispatch before the switch.
        if (bin.Op is TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash)
        {
            if (l is ObjectType lo && r is ObjectType ro && lo.Name == ro.Name &&
                _overloadReturnTypes.TryGetValue((lo.Name, bin.Op), out var overloadReturn))
            {
                if (overloadReturn is FailureType ft)
                {
                    if (!_inTryBlock && !_inFailureHandledContext)
                        throw TypeError(
                            $"'{FormatOp(bin.Op)}' on '{lo.Name}' can fail — you must handle the failure",
                            null, bin.Line, bin.Column,
                            $"use '{FormatOp(bin.Op)}' on '{lo.Name}' without handling the potential failure",
                            "Wrap this in a 'Try to: / In case of failure:' block, or use 'but on failure <default>'.");
                    return _inTryBlock ? ft.Inner : (CufetType)ft;
                }
                return overloadReturn;
            }

            // Matrix arithmetic: +, -, * are defined for (matrix, matrix) and always fallible
            // (dimension mismatch → failure). The matrix type is only in scope inside a
            // Pull a book on collections block, so scope-locality is enforced by the type itself.
            if (l is MatrixType && r is MatrixType &&
                bin.Op is TokenType.Plus or TokenType.Minus or TokenType.Star)
            {
                var matReturn = new FailureType(MatrixType.Instance);
                if (!_inTryBlock && !_inFailureHandledContext)
                    throw TypeError(
                        $"matrix '{FormatOp(bin.Op)}' can fail — you must handle the failure",
                        null, bin.Line, bin.Column,
                        $"use '{FormatOp(bin.Op)}' on matrices without handling the potential failure",
                        "Wrap this in a 'Try to: / In case of failure:' block, or use 'but on failure <default>'.");
                return _inTryBlock ? MatrixType.Instance : (CufetType)matReturn;
            }
        }

        return bin.Op switch
        {
            TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash or TokenType.Percent
                when l == CufetType.Number && r == CufetType.Number
                => CufetType.Number,
            // Arithmetic on bit patterns. '/' is INTEGER division here, unlike on numbers —
            // the same surface with a different meaning per operand type, as matrix already
            // does. Building a mask needs subtraction ((1 shifted left by n) - 1) and address
            // work needs addition, so leaving arithmetic out would hobble the type.
            TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash or TokenType.Percent
                when l == CufetType.Bits && r == CufetType.Bits
                => CufetType.Bits,
            TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash or TokenType.Percent
                => throw TypeError(
                    "arithmetic requires numbers on both sides",
                    null,
                    bin.Line, bin.Column,
                    $"use {FormatOp(bin.Op)} with {FormatType(l)} and {FormatType(r)}",
                    "If you meant arithmetic, both sides need to be numbers.\nIf you meant to join text, use 'joined to': \"hello\" joined to \" world\"."),
            // is void / is not void: voidable T compared to void
            TokenType.Equal or TokenType.NotEqual
                when (l is VoidableType && r is VoidType) || (l is VoidType && r is VoidableType)
                => CufetType.Fact,
            // voidable T compared directly to a plain T (or vice versa) — void is simply
            // unequal to any T, so this is total and needs no narrowing first. Lets a
            // voidable value (e.g. a failure's category) be tested against a concrete
            // value directly: 'the category of the failure is "bad-input"'.
            TokenType.Equal or TokenType.NotEqual
                when (l is VoidableType lv && r == lv.Inner) || (r is VoidableType rv && l == rv.Inner)
                => CufetType.Fact,
            // ★ A rabbit is never compared, to another rabbit or to anything else. It denotes a
            // REGION with its own lifetime rather than a value, and there is no sense in which
            // two of them are the same one — so the question is refused rather than answered.
            // Refusing is the reversible choice: it makes no claim, and it can become an answer
            // the day something needs to tell rabbits apart. Answering cannot be taken back.
            // Refused HERE, in the shared front end, so both backends refuse identically.
            TokenType.Equal or TokenType.NotEqual
                when IsRabbitType(l) || IsRabbitType(r)
                => throw TypeError(
                    "a rabbit can't be compared, not even to another rabbit",
                    "A rabbit is a region with a lifetime of its own, not a value that can match another",
                    bin.Line, bin.Column,
                    "compare a rabbit",
                    "Nothing tells one rabbit from another today. If you need that, it is worth asking for rather than working around."),
            TokenType.Equal or TokenType.NotEqual
                when l == r
                => CufetType.Fact,
            // Union type compared for equality with any value — legal on un-narrowed unions.
            TokenType.Equal or TokenType.NotEqual
                when l is UnionType || r is UnionType
                => CufetType.Fact,
            TokenType.Equal or TokenType.NotEqual
                => throw TypeError(
                    "equality comparison requires matching types",
                    null,
                    bin.Line, bin.Column,
                    $"compare a {FormatType(l)} to a {FormatType(r)}",
                    $"A {FormatType(l)} and a {FormatType(r)} can never be equal — this is likely a mistake. Check which side has the wrong type."),
            TokenType.Lt or TokenType.Gt or TokenType.Lte or TokenType.Gte
                when (l == CufetType.Number && r == CufetType.Number)
                  || (l == CufetType.Bits   && r == CufetType.Bits)   // unsigned, so well-ordered
                => CufetType.Fact,
            TokenType.Lt or TokenType.Gt or TokenType.Lte or TokenType.Gte
                => throw TypeError(
                    "ordering works on numbers and on bits",
                    null,
                    bin.Line, bin.Column,
                    $"order a {FormatType(l)} and a {FormatType(r)}",
                    "Ordering comparisons (>, <, >=, <=) need both sides to be numbers, or " +
                    "both to be bits."),
            // The gates. A 32-bit AND *is* 32 AND gates side by side, so the same words work at
            // both widths: a fact is one bit, a bits value is N. They stay off `number`
            // deliberately — a quantity has no bits to combine, and that separation is what
            // keeps `not 5` from meaning -6.
            TokenType.And or TokenType.Or or TokenType.Xor
                when l == CufetType.Fact && r == CufetType.Fact
                => CufetType.Fact,
            TokenType.And or TokenType.Or or TokenType.Xor
                when l == CufetType.Bits && r == CufetType.Bits
                => CufetType.Bits,
            TokenType.And or TokenType.Or or TokenType.Xor
                when l == CufetType.Number || r == CufetType.Number
                => throw TypeError(
                    $"'{FormatOp(bin.Op)}' is a gate, and a number has no bits to combine",
                    null,
                    bin.Line, bin.Column,
                    $"use '{FormatOp(bin.Op)}' with {FormatType(l)} and {FormatType(r)}",
                    "Gates work on facts (one bit) and on bits (a pattern of them), not on " +
                    "numbers. A number is a quantity — 255 counts something, where 0xFF is a " +
                    "pattern of eight bits. Write the value as a bit pattern (0xFF, 0b1010, " +
                    "0o755), or convert with 'converted to hex'."),
            TokenType.And or TokenType.Or or TokenType.Xor
                => throw TypeError(
                    $"'{FormatOp(bin.Op)}' needs both sides to be the same kind of thing",
                    null,
                    bin.Line, bin.Column,
                    $"use '{FormatOp(bin.Op)}' with {FormatType(l)} and {FormatType(r)}",
                    $"Both sides of '{FormatOp(bin.Op)}' must be facts, or both must be bits. " +
                    "Did you mean to write a comparison like 'x is 0' rather than just 'x'?"),
            _ => null
        };
    }

    // T is assignable to target when:
    //   target == source (same type)
    //   target is voidable T and source is assignable to T (widening)
    //   target is voidable T and source is void (void is the absent case of any voidable T)
    //   target is T or failure and source is assignable to T, or is a failure
    //
    // ★ The wrappers RECURSE. They used to compare `source == v.Inner` outright, which meant
    // widening stopped dead at a wrapper: `(A or B)` accepted an `A`, but `(A or B) or failure`
    // and `voidable (A or B)` both refused one — the single most natural return type for a
    // recursive-descent parser, where every branch yields one node kind and any branch can fail.
    private static bool IsAssignable(CufetType target, CufetType source)
    {
        if (target == source) return true;
        if (target is VoidableType v)
            return IsAssignable(v.Inner, source) || source is VoidType;
        if (target is FailureType f)
            return IsAssignable(f.Inner, source) || source is FailureMarkerType;
        if (target is UnionType ut)
        {
            if (ut.Cases == null) return true; // open union accepts anything
            // source is one of the union cases (or assignable to a case)
            if (ut.Cases.Any(c => IsAssignable(c, source))) return true;
            // source is a union whose cases are all in the target union
            if (source is UnionType us)
                return us.Cases != null && us.Cases.All(c => ut.Cases.Any(tc => IsAssignable(tc, c)));
            return false;
        }
        return false;
    }

    // "X when C, otherwise Y" → X's type when both arms agree, the UNION of the two when they
    // do not.
    //
    // ★ A union rather than an error, because the language already infers one from mixed
    // elements — `a catalogue with (1, "two")` is a union nobody declared. Refusing here would
    // make the conditional narrower than the collection literal sitting next to it, and would
    // reopen the hole this feature exists to close: a `permanently` binding could only be
    // conditionally initialised when the two arms happened to have the same type.
    //
    // Strictness is still available where it is wanted, through the annotation the language
    // already has: `Define the number fee as 0 when member is true, otherwise 25.` makes a
    // mismatched arm an error at the definition rather than at the first use.
    private CufetType? InferConditional(ConditionalExpression ce)
    {
        var condType = InferType(ce.Condition);
        if (condType is not null and not FactType)
            throw new TypeException(
                $"A 'when' condition must be true or false, but this one is {FormatType(condType)}.",
                ce.Line, ce.Column);

        var valueType = InferType(ce.Value);
        var altType   = InferType(ce.Alternative);

        // An arm whose type is unknown tells us nothing; take the one that does.
        if (valueType is null) return altType;
        if (altType   is null) return valueType;

        // Already compatible in one direction — no union needed, and the wider one wins so a
        // narrow arm widening into a declared union stays that union.
        if (IsAssignable(valueType, altType)) return valueType;
        if (IsAssignable(altType, valueType)) return altType;

        // Genuinely different: flatten both sides so `(a or b) when c, otherwise d` is a
        // three-case union rather than a union holding a union.
        var cases = new List<CufetType>();
        foreach (var t in new[] { valueType, altType })
            if (t is UnionType { Cases: not null } u) cases.AddRange(u.Cases);
            else cases.Add(t);
        return new UnionType(cases);
    }

    // "X but void is Y" → plain T.
    // Checks that X is voidable T and Y is assignable to T; returns T.
    private CufetType? InferButVoidDefault(ButVoidDefault bvd)
    {
        var leftType    = InferType(bvd.Voidable);
        var defaultType = InferType(bvd.Default);

        if (leftType is VoidableType v)
        {
            var inner = v.Inner;
            if (defaultType != null && !IsAssignable(inner, defaultType))
                throw TypeError(
                    $"the default value is a {FormatType(defaultType)}, but the voidable holds {FormatTypePlural(inner)}",
                    null, bvd.Line, bvd.Column,
                    $"use a {FormatType(defaultType)} as the default for a voidable {FormatType(inner)}",
                    $"The default after 'but void is' must be a {FormatType(inner)}.");
            return inner;
        }
        if (leftType is VoidType)
            return defaultType; // always-void: result is always the default
        if (leftType != null)
            throw TypeError(
                $"'{FormatType(leftType)}' can never be void",
                null, bvd.Line, bvd.Column,
                $"use 'but void is' on a {FormatType(leftType)} value",
                "Only voidable values can be void. 'but void is' is only needed for voidable values.");
        return null;
    }

    // Infer type of a union type-test — always yields fact.
    private CufetType InferIsTypeCheck(IsTypeCheck tc)
    {
        // ISA.2: record the target's static type (as known HERE, so flow-narrowing is reflected —
        // the same view the compiler's TypeOf has) so the interpreter can answer type-directed.
        tc.StaticTargetType = InferType(tc.Target);
        return CufetType.Fact;
    }

    // Try to extract narrowing info from an IsTypeCheck condition.
    private static bool TryGetTypeCheckNarrowing(
        IExpression condition, out string? varName, out CufetType? type, out bool negated)
    {
        varName = null; type = null; negated = false;
        if (condition is not IsTypeCheck tc) return false;
        if (tc.Target is not VariableReference vr) return false;
        varName = vr.Name;
        type    = tc.Type;
        negated = tc.Negated;
        return true;
    }

    /// <summary>
    /// `x is a A or x is a B or …` — every operand a POSITIVE type test on the SAME variable.
    /// </summary>
    /// <remarks>
    /// ★ This is what a grouped judgement arm means, said as a condition. It is the only way to
    /// state "one of these, but not the others", which is why a residue could not be carried across
    /// a resumption until it existed: a single test names one case and elimination names all but
    /// one, and a group is neither.
    ///
    /// ⚠ A mixed disjunction (`x is a A or y is a B`, or one negated operand) narrows NOTHING and
    /// must return false rather than a partial answer — reaching the arm would not imply any of it.
    /// </remarks>
    private static bool TryGetDisjunctionNarrowing(
        IExpression condition, out string? varName, out List<CufetType>? types)
    {
        varName = null; types = null;
        if (condition is not BinaryExpression { Op: TokenType.Or }) return false;

        string? name = null;
        var collected = new List<CufetType>();

        bool Walk(IExpression e)
        {
            if (e is BinaryExpression { Op: TokenType.Or } both)
                return Walk(both.Left) && Walk(both.Right);
            if (e is not IsTypeCheck { Negated: false, Target: VariableReference vr } tc) return false;
            if (name == null) name = vr.Name;
            else if (name != vr.Name) return false;
            collected.Add(tc.Type);
            return true;
        }

        if (!Walk(condition) || name == null || collected.Count < 2) return false;
        varName = name; types = collected;
        return true;
    }

    // Remove `removedType` from `unionType`, returning the narrowed remaining type.
    // Open union or non-union input → returned unchanged. Single remaining case → unwrapped.
    private static CufetType? RemoveFromUnion(CufetType? unionType, CufetType removedType)
    {
        if (unionType is not UnionType { Cases: { } cases }) return unionType;
        // Equals, not `!=`. CufetType subclasses override Equals but not the operator, so `!=`
        // is reference equality — which happened to work for the cached primitive instances and
        // silently failed for any type constructed fresh by the parser.
        var remaining = cases.Where(c => !c.Equals(removedType)).ToList();
        if (remaining.Count == 0) return null;
        if (remaining.Count == 1) return remaining[0];
        // (T or void) normalizes to VoidableType(T)
        if (remaining.Count == 2 && remaining.Any(c => c is VoidType))
            return new VoidableType(remaining.First(c => c is not VoidType));
        return new UnionType(remaining);
    }

    // Returns the variable name and its narrowed inner type when the condition is "X is not void"
    // and X is currently typed as voidable T in _env. Returns false otherwise.
    private bool TryGetNotVoidNarrowing(
        IExpression condition, out string? varName, out CufetType? narrowedTo)
    {
        varName    = null;
        narrowedTo = null;
        if (condition is not BinaryExpression { Op: TokenType.NotEqual, Right: VoidLiteral } bin)
            return false;
        if (bin.Left is not VariableReference vr) return false;
        if (!TryLookup(vr.Name, out var info)) return false;
        if (info.Type is not VoidableType vt) return false;
        varName    = vr.Name;
        narrowedTo = vt.Inner;
        return true;
    }

    // Scans a statement list for definite return paths.
    // Returns true only when every execution path through stmts ends at a return.
    private static bool DefinitelyReturns(IReadOnlyList<IStatement> stmts)
    {
        foreach (var stmt in stmts)
        {
            if (stmt is ReturnStatement) return true;
            if (stmt is IfStatement ifStmt && ifStmt.ElseBody != null)
            {
                bool allArmsReturn = ifStmt.Arms.All(a => DefinitelyReturns(a.Body));
                if (allArmsReturn && DefinitelyReturns(ifStmt.ElseBody)) return true;
            }
            // Pull...Done scopes are transparent: if the Pull body definitely returns, so does the Pull.
            // A Judge that reaches the checker has already been proved total — either it covers a
            // closed union exhaustively or it carries an Otherwise. So if every arm returns, the
            // whole judgement returns, and there is no fall-off-the-end path to worry about.
            if (stmt is JudgeStatement judge
                && judge.Arms.All(a => DefinitelyReturns(a.Body))
                && (judge.OtherwiseBody == null || DefinitelyReturns(judge.OtherwiseBody)))
                return true;
            if (stmt is PullStatement ps && DefinitelyReturns(ps.Body)) return true;
            if (stmt is PullRabbitStatement prs && DefinitelyReturns(prs.Body)) return true;
            // Loops are not counted: while/for-each may execute zero times,
            // repeat-until exits after one iteration without requiring a return.
        }
        return false;
    }

    // Checks a linear block of statements, applying "guard narrowing" between them:
    // after an exiting guard — a single-arm `If <cond>, return …` with no else whose body
    // definitely returns — the statements that follow run only when <cond> was false, so the
    // fall-through path can narrow by the negation of <cond>. This makes the natural idiom
    //   `If x is void, return a failure …`  … then use x as non-void
    // type-check without an explicit `is not void` nesting. Narrowings established here are
    // undone at the end of the block so they never leak to sibling blocks (which would be
    // unsound: a guard inside one If-arm says nothing about the path where that arm was skipped).
    private void CheckBlock(IReadOnlyList<IStatement> body)
    {
        var guardNarrowed = new List<(string Name, CufetType? Prev, bool Had)>();
        foreach (var s in body)
        {
            CheckStatement(s);
            if (s is IfStatement { Arms.Count: 1, ElseBody: null } guard
                && DefinitelyReturns(guard.Arms[0].Body))
            {
                var narrowings = new List<(string Name, CufetType Type)>();
                CollectGuardNarrowings(guard.Arms[0].Condition, narrowings);
                foreach (var (name, ty) in narrowings)
                {
                    bool had = _narrowedVars.TryGetValue(name, out var prev);
                    guardNarrowed.Add((name, had ? prev : null, had));
                    _narrowedVars[name] = ty;
                }
            }
        }
        for (int i = guardNarrowed.Count - 1; i >= 0; i--)
        {
            var (name, prev, had) = guardNarrowed[i];
            if (had) _narrowedVars[name] = prev!; // had ⇒ prev non-null
            else _narrowedVars.Remove(name);
        }
    }

    // Collects the narrowings implied by the NEGATION of a guard condition — what holds on the
    // fall-through path once an exiting guard on `condition` has been passed.
    //   `x is void`            → x is non-void        → narrow x to its inner type
    //   `x is a T`             → x is not a T          → remove T from x's union
    //   `A or B`  (¬ = ¬A ∧ ¬B) → both sides' negations hold → collect from each
    // `and` is deliberately not recursed: ¬(A ∧ B) = ¬A ∨ ¬B narrows neither side.
    private void CollectGuardNarrowings(IExpression condition, List<(string, CufetType)> into)
    {
        if (condition is BinaryExpression { Op: TokenType.Or } orExpr)
        {
            CollectGuardNarrowings(orExpr.Left, into);
            CollectGuardNarrowings(orExpr.Right, into);
            return;
        }
        if (condition is BinaryExpression { Op: TokenType.Equal, Right: VoidLiteral, Left: VariableReference vr }
            && TryLookup(vr.Name, out var info) && info!.Type is VoidableType vt)
        {
            into.Add((vr.Name, vt.Inner));
            return;
        }
        if (TryGetTypeCheckNarrowing(condition, out var tcVar, out var tcType, out bool tcNeg) && !tcNeg
            && TryLookup(tcVar!, out var tinfo))
        {
            var remaining = RemoveFromUnion(tinfo!.Type, tcType!);
            if (remaining != null) into.Add((tcVar!, remaining));
        }
    }

    // The one shape every type error takes: what the code says, what was already established,
    // what this line tried to do, and what to write instead. The position travels on the
    // exception rather than only in the prose, so an editor gets it without reading English.
    private static TypeException TypeError(
        string context,
        string? established,
        int violationLine,
        int violationColumn,
        string action,
        string fix)
    {
        var est = established != null ? $"\n{established}." : "";
        return new TypeException(
            $"That doesn't work: {context}.{est}\nHere on line {violationLine}, you're trying to {action}.\n\n{fix}",
            violationLine,
            violationColumn);
    }

    // internal, not private: GenericInstantiation names a filling with it, and one renderer is the
    // point — an instantiated type's name is what an error message shows.
    internal static string FormatType(CufetType type) => type switch
    {
        NumberType                           => "number",
        BitsType                             => "bits",
        TextType                             => "text",
        FactType                             => "fact",
        VoidType                             => "void",
        VoidableType { Inner: var inner }    => $"voidable {FormatType(inner)}",
        SeriesType { ElementType: var elem } => $"series of {FormatTypePlural(elem)}",
        StashType { ElementType: var held }  => $"stash of {FormatTypePlural(held)}",
        AxiomType at                         => FormatAxiomType(at),
        FunctionType ft                      => FormatFunctionType(ft),
        RecordType rt                        => FormatRecordType(rt),
        ObjectType ot                        => ot.Name,
        InterfaceType it                     => it.Name,
        ReadableStreamType { ElementType: var elem } => $"readable stream of {FormatTypePlural(elem)}",
        WritableStreamType { ElementType: var elem } => $"writable stream of {FormatTypePlural(elem)}",
        RabbitType                           => "rabbit",
        AddressType                          => "address",
        MapType mt                           => $"map from {FormatType(mt.KeyType)} to {FormatType(mt.ValueType)}",
        MappingType                          => "mapping",
        FailureMarkerType                    => "failure",
        FailureType { Inner: var inner }     => $"{FormatType(inner)} or failure",
        ExceptionMarkerType                  => "exception",
        BookType bt                          => $"book '{bt.Name}'",
        MatrixType                           => "matrix",
        ChannelType ct                       => $"channel of {FormatType(ct.ElementType)}",
        TaskHandleType tht                   => tht.ResultType != null ? $"task (result: {FormatType(tht.ResultType)})" : "void task",
        UnionType { Cases: null }            => "open union",
        UnionType { Cases: var cs }          => $"({string.Join(" or ", cs!.Select(FormatType))})",
        _                                    => "<unknown>",
    };

    private static string FormatRecordType(RecordType rt)
    {
        var parts = new List<string>();
        foreach (var t in rt.PositionalTypes)         parts.Add(FormatType(t));
        foreach (var (name, t) in rt.NamedFields)     parts.Add($"{name}: {FormatType(t)}");
        return parts.Count == 0 ? "record ()" : $"record ({string.Join(", ", parts)})";
    }

    /// <summary>An axiom type in the spelling it is written in — result and parameters both.</summary>
    /// <remarks>
    /// ⚠ Both halves, always. Printing the result but not the parameters is how a message comes out
    /// as "declared as c-language number axiom, but the value is a c-language number axiom" — two
    /// genuinely different types rendered identically, which says nothing and reads as a compiler
    /// bug rather than as the mismatch it is.
    /// </remarks>
    private static string FormatAxiomType(AxiomType a)
    {
        string head = a.ReturnType is { } gives
            ? $"{a.Language} {FormatType(gives)} axiom"
            : $"{a.Language} axiom";
        return a.ParameterTypes.Count == 0
            ? head
            : $"{head} given ({string.Join(", ", a.ParameterTypes.Select(FormatType))})";
    }

    private static string FormatFunctionType(FunctionType ft)
    {
        var ret = ft.ReturnType == null ? "void" : FormatType(ft.ReturnType);
        if (ft.ParameterTypes.Count == 0)
            return $"{ret} function";
        var paramTypes = string.Join(", ", ft.ParameterTypes.Select(FormatType));
        return $"{ret} function given ({paramTypes})";
    }

    private static string FormatTypePlural(CufetType type) => type switch
    {
        NumberType                           => "numbers",
        BitsType                             => "bits",   // already plural
        TextType                             => "text",
        FactType                             => "facts",
        VoidType                             => "void values",
        VoidableType { Inner: var inner }    => $"voidable {FormatTypePlural(inner)}",
        SeriesType { ElementType: var elem } => $"series of {FormatTypePlural(elem)}",
        StashType { ElementType: var held }  => $"stashes of {FormatTypePlural(held)}",
        FunctionType                         => "functions",
        RecordType rt                        => FormatRecordType(rt),
        ObjectType ot                        => $"{ot.Name} objects",
        InterfaceType it                     => $"{it.Name} values",
        ReadableStreamType { ElementType: var elem } => $"readable streams of {FormatTypePlural(elem)}",
        WritableStreamType { ElementType: var elem } => $"writable streams of {FormatTypePlural(elem)}",
        RabbitType                           => "rabbits",
        MapType mt                           => $"maps from {FormatType(mt.KeyType)} to {FormatType(mt.ValueType)}",
        MappingType                          => "mappings",
        FailureMarkerType                    => "failures",
        FailureType { Inner: var inner }     => $"{FormatTypePlural(inner)} or failures",
        ExceptionMarkerType                  => "exceptions",
        BookType bt                          => $"book '{bt.Name}' values",
        MatrixType                           => "matrices",
        ChannelType ct                       => $"channels of {FormatTypePlural(ct.ElementType)}",
        TaskHandleType tht                   => tht.ResultType != null ? $"task handles (result: {FormatTypePlural(tht.ResultType)})" : "void task handles",
        UnionType { Cases: null }            => "open union values",
        UnionType { Cases: var cs }          => $"({string.Join(" or ", cs!.Select(FormatType))}) values",
        _                                    => "<unknown>",
    };

    private static string FormatExpr(IExpression expr) => expr switch
    {
        NumberLiteral    { Value: var v } => v.ToString(),
        StringLiteral    { Value: var v } => $"\"{v}\"",
        // Rebuilt from the parts rather than kept as source text, so it echoes back in the same
        // base and width the author wrote — quoting "0xFF" at them is only useful if it looks
        // like what they typed.
        BitsLiteral      b                => FormatBitsLiteral(b),
        VariableReference { Name: var n } => n,
        PossessiveAccess pa               => $"{FormatExpr(pa.Target)}'s {pa.Member}",
        _                                 => "<expression>",
    };

    internal static string FormatBitsLiteral(BitsLiteral b)
    {
        int perDigit = b.Base switch { 'x' => 4, 'o' => 3, _ => 1 };
        int declared = (b.Width + perDigit - 1) / perDigit;
        string digits = b.Base switch
        {
            'x' => b.Value.ToString("X"),
            'o' => Convert.ToString((long)b.Value, 8),
            _   => Convert.ToString((long)b.Value, 2),
        };
        return $"0{b.Base}{digits.PadLeft(declared, '0')}";
    }

    private static string FormatOp(TokenType op) => op switch
    {
        TokenType.Plus    => "+",
        TokenType.Minus   => "-",
        TokenType.Star    => "*",
        TokenType.Slash   => "/",
        TokenType.Percent => "%",
        TokenType.And     => "and",
        TokenType.Or      => "or",
        TokenType.Xor     => "xor",
        _                 => op.ToString().ToLower(),
    };
}
