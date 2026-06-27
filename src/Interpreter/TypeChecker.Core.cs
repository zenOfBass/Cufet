using Cufet.Lexer;

namespace Cufet.Interpreter;

// CufetType is a value-equality class hierarchy.
// CufetType.Number / .Text / .Fact are canonical singletons for the three scalars.
// All == comparisons use structural / deep equality.
public abstract class CufetType
{
    public static readonly CufetType Number = new NumberType();
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

public sealed class SeriesType : CufetType
{
    public CufetType ElementType { get; }
    public SeriesType(CufetType elementType) => ElementType = elementType;
    public override bool Equals(object? obj) => obj is SeriesType s && ElementType == s.ElementType;
    public override int GetHashCode() => HashCode.Combine(typeof(SeriesType), ElementType);
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
        string? unmaker = null)
    {
        Name               = name;
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
public sealed class VoidableType : CufetType
{
    public CufetType Inner { get; }
    public VoidableType(CufetType inner) => Inner = inner;
    public override bool Equals(object? obj) => obj is VoidableType v && Inner == v.Inner;
    public override int GetHashCode() => HashCode.Combine(typeof(VoidableType), Inner);
}

// map from K to V — homogeneous, reference-typed. Keys must be number or text.
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

public record TypeInfo(CufetType Type, IExpression EstablishingExpr, int EstablishingLine, bool Permanent = false, int RabbitDepth = 0);

public sealed class TypeException : Exception
{
    public TypeException(string message) : base(message) { }
}

public sealed partial class TypeChecker
{
    // Scope chain: [0] = global scope, [^1] = innermost current scope.
    // Every Done.-bounded block (if/while/for/try) pushes a scope on entry and pops on exit.
    // Function bodies replace the whole chain (see CheckBind/CheckMethodBody).
    private readonly List<Dictionary<string, TypeInfo>>      _scopes        = [new()];
    // Parallel type-name scope chain — book-introduced types (e.g. "matrix") registered here
    // when a type-introducing book is pulled. Same push/pop pattern as _scopes.
    private readonly List<Dictionary<string, CufetType>>     _typeScopes    = [new()];
    private readonly Dictionary<string, ObjectType>          _objectDefs    = new();
    private readonly Dictionary<string, InterfaceDefinition> _interfaceDefs = new();
    // Active narrowings: variable name → narrowed type (set inside checked branches).
    private readonly Dictionary<string, CufetType>           _narrowedVars  = new();
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

    // Save both scope chains and replace them with fresh single scopes (for function isolation).
    // V = value scopes, T = type scopes. Call sites iterate V to re-import outer bindings.
    private (List<Dictionary<string, TypeInfo>> V, List<Dictionary<string, CufetType>> T) SaveScopes()
    {
        var savedV = _scopes.ToList();
        var savedT = _typeScopes.ToList();
        _scopes.Clear();
        _typeScopes.Clear();
        _scopes.Add(new Dictionary<string, TypeInfo> { ["input"] = BuiltinInput });
        _typeScopes.Add(new Dictionary<string, CufetType>());
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

    public void Check(Program program)
    {
        _scopes[0]["input"] = BuiltinInput;
        Pass1Hoist(program);
        Pass2ResolveTypes();          // resolve all placeholder ObjectType refs in _objectDefs + global scope
        Pass2CheckOverloads(program); // body-check all overloads; populates _overloadReturnTypes
        foreach (var stmt in program.Statements)
            CheckStatement(stmt);
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
                ot.EmbeddedTypeName, ot.ConformedInterfaces, ot.Constructors, ot.Unmaker);
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
        new TypeInfo(new ReadableStreamType(CufetType.Text), new VariableReference("input", 0), 0);

    // Pass 1: register interfaces (1a), then object types — merged with their 'unto' methods
    // (1b) — then function signatures, excluding 'unto' methods, which are not free
    // functions (1c). Interfaces registered first so object conformance declarations can be
    // validated against them.
    private void Pass1Hoist(Program program)
    {
        foreach (var stmt in program.Statements)
        {
            if (stmt is not InterfaceDefinition ifd) continue;
            _interfaceDefs[ifd.Name] = ifd;
        }

        // Gather 'unto'-attached methods/getters/setters by target type name before building ObjectTypes,
        // so their signatures merge into the type's member set regardless of declaration order.
        var untoMethodsByType  = new Dictionary<string, List<BindStatement>>();
        var untoGettersByType  = new Dictionary<string, List<GetterDeclaration>>();
        var untoSettersByType  = new Dictionary<string, List<SetterDeclaration>>();
        foreach (var stmt in program.Statements)
        {
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

        foreach (var stmt in program.Statements)
        {
            if (stmt is not ObjectDefinition od) continue;
            var methodSigs = od.Methods
                .Select(m => (m.Name, new FunctionType(m.Parameters.Select(p => p.Type).ToList(), m.ReturnType)))
                .ToList();
            var getterSigs = od.Getters.Select(g => (g.Name, g.ReturnType)).ToList();
            var setterSigs = od.Setters.Select(s => (s.Name, s.ParamType, s.ParamName)).ToList();

            if (untoMethodsByType.Remove(od.Name, out var untoMethods))
            {
                foreach (var um in untoMethods)
                {
                    if (methodSigs.Any(s => s.Name == um.Name))
                        throw new TypeException(FormatTypeError(
                            $"'{od.Name}' already has a method '{um.Name}'",
                            null, um.Line,
                            $"declare another method named '{um.Name}' for '{od.Name}'",
                            "Method names must be unique per type, whether declared nested or with 'unto'. Rename one of them."));
                    methodSigs.Add((um.Name, new FunctionType(um.Parameters.Select(p => p.Type).ToList(), um.ReturnType)));
                }
            }

            if (untoGettersByType.Remove(od.Name, out var untoGetters))
            {
                foreach (var ug in untoGetters)
                {
                    if (getterSigs.Any(g => g.Item1 == ug.Name))
                        throw new TypeException(FormatTypeError(
                            $"'{od.Name}' already has a getter '{ug.Name}'",
                            null, ug.Line,
                            $"declare another getter named '{ug.Name}' for '{od.Name}'",
                            "Getter names must be unique per type. Rename one of them."));
                    getterSigs.Add((ug.Name, ug.ReturnType));
                }
            }

            if (untoSettersByType.Remove(od.Name, out var untoSetters))
            {
                foreach (var us in untoSetters)
                {
                    if (setterSigs.Any(s => s.Item1 == us.Name))
                        throw new TypeException(FormatTypeError(
                            $"'{od.Name}' already has a setter '{us.Name}'",
                            null, us.Line,
                            $"declare another setter named '{us.Name}' for '{od.Name}'",
                            "Setter names must be unique per type. Rename one of them."));
                    setterSigs.Add((us.Name, us.ParamType, us.ParamName));
                }
            }

            _objectDefs[od.Name] = new ObjectType(
                od.Name, od.PositionalTypes, od.NamedFields, methodSigs,
                getterSigs, setterSigs,
                od.EmbeddedTypeName, od.ConformedInterfaces);
        }

        // Anything left in unto* dictionaries targets a name that isn't a defined object type.
        foreach (var (targetName, methods) in untoMethodsByType)
        {
            var reason = _interfaceDefs.ContainsKey(targetName)
                ? $"'{targetName}' is an interface, not an object type — methods can't be attached to it with 'unto'"
                : $"'{targetName}' is not a defined object type";
            throw new TypeException(FormatTypeError(
                reason, null, methods[0].Line,
                $"declare a method unto '{targetName}'",
                $"'unto' only attaches methods to object types defined in this program. Define 'object {targetName}' first, or check the spelling."));
        }
        foreach (var (targetName, getters) in untoGettersByType)
            throw new TypeException(FormatTypeError(
                $"'{targetName}' is not a defined object type",
                null, getters[0].Line,
                $"declare a getter unto '{targetName}'",
                $"'unto' only attaches getters to object types defined in this program. Define 'object {targetName}' first, or check the spelling."));
        foreach (var (targetName, setters) in untoSettersByType)
            throw new TypeException(FormatTypeError(
                $"'{targetName}' is not a defined object type",
                null, setters[0].Line,
                $"declare a setter unto '{targetName}'",
                $"'unto' only attaches setters to object types defined in this program. Define 'object {targetName}' first, or check the spelling."));

        foreach (var stmt in program.Statements)
        {
            if (stmt is not BindStatement bind) continue;
            if (bind.UntoType != null) continue; // 'unto' methods are not free functions
            var paramTypes = bind.Parameters.Select(p => p.Type).ToList();
            Scope[bind.Name] = new TypeInfo(
                new FunctionType(paramTypes, bind.ReturnType),
                new VariableReference(bind.Name, 0),
                bind.Line);
        }

        // Gather named constructors ('Bind making a <type> to <name>'), validate their target types,
        // register them on ObjectType.Constructors, and fix up their scope entries so the return type
        // is the canonical ObjectType instance (not the shell produced by the parser).
        var ctorsByType = new Dictionary<string, List<BindStatement>>();
        foreach (var stmt in program.Statements)
        {
            if (stmt is not BindStatement bind || bind.ConstructsTypeName == null) continue;
            if (!ctorsByType.TryGetValue(bind.ConstructsTypeName, out var cList))
                ctorsByType[bind.ConstructsTypeName] = cList = [];
            cList.Add(bind);
        }
        foreach (var (typeName, ctors) in ctorsByType)
        {
            if (!_objectDefs.TryGetValue(typeName, out var ot))
                throw new TypeException(FormatTypeError(
                    $"'{typeName}' is not a defined object type — 'making a {typeName}' has no type to register on",
                    null, ctors[0].Line,
                    $"declare a constructor for '{typeName}'",
                    $"Define 'object {typeName}' before declaring constructors for it, or check the spelling."));

            var newCtorNames = ot.Constructors.ToList();
            foreach (var ctor in ctors)
            {
                if (newCtorNames.Contains(ctor.Name))
                    throw new TypeException(FormatTypeError(
                        $"'{typeName}' already has a constructor named '{ctor.Name}'",
                        null, ctor.Line,
                        $"declare another constructor named '{ctor.Name}' for '{typeName}'",
                        "Constructor names must be unique per type. Rename one of them."));
                newCtorNames.Add(ctor.Name);

                // Fix up scope entry: resolve the shell ObjectType to the canonical instance.
                var resolvedReturn = ctor.ReturnType is FailureType ft
                    ? (CufetType)new FailureType(ot)
                    : ot;
                var paramTypes = ctor.Parameters.Select(p => p.Type).ToList();
                Scope[ctor.Name] = new TypeInfo(
                    new FunctionType(paramTypes, resolvedReturn),
                    new VariableReference(ctor.Name, 0),
                    ctor.Line);
            }

            _objectDefs[typeName] = new ObjectType(
                ot.Name, ot.PositionalTypes, ot.NamedFields, ot.Methods,
                ot.Getters, ot.Setters, ot.EmbeddedTypeName, ot.ConformedInterfaces,
                newCtorNames, ot.Unmaker);
        }

        // Gather destructors ('Bind unmaking a <type> to <name>'), validate, register on ObjectType.Unmaker.
        // Exactly one destructor per type; a second 'unmaking a <type>' is a declaration-time error.
        var unmakeByType = new Dictionary<string, UnmakerDeclaration>();
        foreach (var stmt in program.Statements)
        {
            if (stmt is not UnmakerDeclaration ud) continue;
            if (unmakeByType.ContainsKey(ud.UnmakesTypeName))
                throw new TypeException(FormatTypeError(
                    $"'{ud.UnmakesTypeName}' already has a destructor — 'Bind unmaking a {ud.UnmakesTypeName}' appeared twice",
                    null, ud.Line,
                    $"declare a second destructor for '{ud.UnmakesTypeName}'",
                    "Remove the duplicate. Each type has exactly one destructor — one way to die."));
            unmakeByType[ud.UnmakesTypeName] = ud;
        }
        foreach (var (typeName, ud) in unmakeByType)
        {
            if (!_objectDefs.TryGetValue(typeName, out var ot))
                throw new TypeException(FormatTypeError(
                    $"'{typeName}' is not a defined object type — 'unmaking a {typeName}' has no type to register on",
                    null, ud.Line,
                    $"declare a destructor for '{typeName}'",
                    $"Define 'object {typeName}' before declaring a destructor for it, or check the spelling."));
            _objectDefs[typeName] = new ObjectType(
                ot.Name, ot.PositionalTypes, ot.NamedFields, ot.Methods,
                ot.Getters, ot.Setters, ot.EmbeddedTypeName, ot.ConformedInterfaces,
                ot.Constructors, ud.Name);
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
            case IfStatement ifStmt:
            {
                // Track union exhaustion across arms: if every arm type-checks the same variable
                // against a closed union, the Otherwise body narrows to what's left.
                string? unionVar = null;
                CufetType? remainingUnionType = null;
                bool canExhaustNarrow = true;

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
                    foreach (var s in arm.Body) CheckStatement(s);
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
                    EnterScope();
                    foreach (var s in ifStmt.ElseBody) CheckStatement(s);
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
                foreach (var s in whileStmt.Body)
                    CheckStatement(s);
                ExitScope();
                break;
            case RepeatUntilStatement repeatUntil:
                EnterScope();
                foreach (var s in repeatUntil.Body)
                    CheckStatement(s);
                ExitScope();
                _ = InferType(repeatUntil.Condition);
                break;
            case ForEachStatement forEach:
                CheckForEach(forEach);
                break;
            case SeriesAddStatement add:
                CheckSeriesAdd(add);
                break;
            case SeriesRemoveValueStatement removeVal:
                CheckSeriesRemoveValue(removeVal);
                break;
            case SeriesSetStatement seriesSet:
                CheckSeriesSet(seriesSet);
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
            case BindStatement bind:
                // Top-level Bind: already in Scope (= global scope) from Pass1Hoist — skip.
                // Non-top-level Bind (inside a function or block): register in current scope
                // so the function can be returned/passed and can recurse on itself.
                if (!Scope.ContainsKey(bind.Name))
                {
                    var paramTypes = bind.Parameters.Select(p => p.Type).ToList();
                    Scope[bind.Name] = new TypeInfo(
                        new FunctionType(paramTypes, bind.ReturnType),
                        new VariableReference(bind.Name, 0),
                        bind.Line);
                }
                CheckBind(bind);
                break;
            case CastStatement cs:
            {
                var (funcType, displayName, declLine, argsToValidate) = ResolveForCast(cs.Function, cs.Args, cs.Line);
                if (funcType != null)
                {
                    ValidateCastArgs(funcType, displayName, declLine, argsToValidate, cs.Line);
                    if (!_inTryBlock && funcType.ReturnType is FailureType)
                        throw new TypeException(FormatTypeError(
                            $"{displayName} can fail — you must handle the failure",
                            null, cs.Line,
                            $"call a fallible function without handling the failure",
                            "Wrap this call in a 'Try to: / In case of failure:' block."));
                }
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
                    throw new TypeException(FormatTypeError(
                        "'Suppress the exception.' is only valid inside an exception handler",
                        null, ss.Line,
                        "suppress an exception outside an exception handler",
                        "Move 'Suppress the exception.' inside an 'In case of exception' block."));
                break;
            case FileWriteStatement fw:
                CheckFileWrite(fw);
                break;
            case WithOpenStatement wos:
                CheckWithOpen(wos);
                break;
            case WithRabbitStatement wrs:
                CheckWithRabbit(wrs);
                break;
            case PullStatement ps:
                CheckPullStatement(ps);
                break;
            case WriteToStreamStatement wts:
                CheckWriteToStream(wts);
                break;
            case ObjectDefinition od:
                CheckObjectDefinition(od);
                break;
            case InterfaceDefinition:
                break; // already hoisted in Pass1
            case AcknowledgeInterruptStatement:
                break; // always valid; no type constraints
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
        }
    }

    private void CheckDefine(DefineStatement define)
    {
        var type = InferType(define.Value);
        if (type == null)
            throw new TypeException(FormatTypeError(
                $"the type of the value for '{define.Name}' can't be determined",
                null,
                define.Line,
                "define a variable without a clear starting type",
                "Start with a literal value or a defined variable so the type is clear from the beginning."));

        if (Scope.ContainsKey(define.Name))
            throw new TypeException(FormatTypeError(
                $"'{define.Name}' is already defined in this scope",
                null, define.Line,
                $"define '{define.Name}' again in the same block",
                "Each name can only be defined once per block. Use 'becomes' to reassign it, or choose a different name."));

        if (TryLookupOuter(define.Name, out var outer))
        {
            if (!define.Shadow)
                throw new TypeException(FormatTypeError(
                    $"'{define.Name}' already exists in an enclosing scope",
                    $"It was defined on line {outer.EstablishingLine}",
                    define.Line,
                    $"declare '{define.Name}' in this block without shadowing the outer one",
                    $"To deliberately shadow it, write 'Define a shadow {define.Name} as ...'."));
        }
        else if (define.Shadow)
        {
            throw new TypeException(FormatTypeError(
                $"'a shadow {define.Name}' — there's nothing named '{define.Name}' in an enclosing scope to shadow",
                null, define.Line,
                $"shadow a name that doesn't exist in any enclosing scope",
                $"Remove 'a shadow' if you're just defining a new variable, or check the spelling."));
        }

        Scope[define.Name] = new TypeInfo(type, define.Value, define.Line, define.Permanent, _rabbitDepth);
    }

    private void CheckBecomes(BecomesStatement becomes)
    {
        // Reassignment invalidates any active narrowing on this variable.
        _narrowedVars.Remove(becomes.Name);

        if (!TryLookup(becomes.Name, out var existing))
            return;

        if (existing.Permanent)
            throw new TypeException(FormatTypeError(
                $"'{becomes.Name}' is permanent",
                $"It was fixed with a value on line {existing.EstablishingLine} and can't be reassigned",
                becomes.Line,
                "reassign it",
                "If it needs to change, define it without 'permanently'."));

        var rhsType = InferType(becomes.Value);
        if (rhsType == null) return;

        if (!IsAssignable(existing.Type, rhsType))
            throw new TypeException(FormatTypeError(
                $"'{becomes.Name}' holds {FormatTypePlural(existing.Type)}",
                $"You set it to {FormatExpr(existing.EstablishingExpr)} on line {existing.EstablishingLine}, so it can only ever hold {FormatTypePlural(existing.Type)}",
                becomes.Line,
                $"give it a {FormatType(rhsType)} value",
                $"Variables keep their type for life. If you need a {FormatType(rhsType)} value here, define a new name for it instead."));

        // Region invariant: don't let a shorter-lived reference escape into longer-lived storage.
        CheckRegionStore(becomes.Value, rhsType, existing.RabbitDepth, becomes.Line,
            $"reassign '{becomes.Name}' to a value from a shorter-lived rabbit region");
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
                throw new TypeException(FormatTypeError(
                    "this function is declared void — it gives nothing back",
                    null,
                    ret.Line,
                    "return a value from a void function",
                    "Remove the value, or change the function's return type if you need to produce a result."));
            // bare return in void → ok
        }
        else // non-void function
        {
            if (ret.Value == null)
                throw new TypeException(FormatTypeError(
                    $"this function is declared to give back a {FormatType(_expectedReturnType)}",
                    $"You declared the return type as {FormatType(_expectedReturnType)} on line {_functionDeclarationLine}",
                    ret.Line,
                    "return without a value",
                    $"Provide a {FormatType(_expectedReturnType)} value to return."));

            var returnType = InferType(ret.Value);
            if (returnType is RabbitType)
                throw new TypeException(FormatTypeError(
                    "rabbits cannot be returned — they flow downward only",
                    null, ret.Line,
                    "return a rabbit from a function",
                    "Pass the rabbit as an argument instead, or return a value that lives in it (a handle into the rabbit)."));
            if (returnType != null && !IsAssignable(_expectedReturnType!, returnType))
                throw new TypeException(FormatTypeError(
                    $"this function is declared to give back a {FormatType(_expectedReturnType!)}",
                    $"You declared the return type as {FormatType(_expectedReturnType!)} on line {_functionDeclarationLine}",
                    ret.Line,
                    $"return a {FormatType(returnType)} value",
                    $"Change the returned value to a {FormatType(_expectedReturnType!)}."));
        }
    }

    private static void CheckIndex(IExpression index, int line)
    {
        if (index is NumberLiteral { Value: var v } && v % 1 != 0)
            throw new TypeException(FormatTypeError(
                "item positions must be whole numbers",
                null,
                line,
                $"use {v} as a position",
                "Positions are counted 1, 2, 3 and so on. Use a whole number."));
    }

    // Public entry point: infers a type and resolves any ObjectType placeholder that survived
    // into the result (belt-and-suspenders after Pass2ResolveTypes handles _objectDefs).
    private CufetType? InferType(IExpression expr)
    {
        var t = InferTypeCore(expr);
        return t is null ? null : ResolveParamType(t);
    }

    // Returns null for genuine inference gaps (undeclared variable, unhandled expression form).
    // Returns a concrete CufetType for anything we can type statically.
    // Throws TypeException for operand type mismatches.
    private CufetType? InferTypeCore(IExpression expr) => expr switch
    {
        NumberLiteral                                                                                    => CufetType.Number,
        StringLiteral                                                                                    => CufetType.Text,
        VoidLiteral                                                                                      => CufetType.Void,
        UnaryExpression unary                                                                            => InferUnary(unary),
        BinaryExpression bin                                                                             => InferBinary(bin),
        VariableReference { Name: var n } when _narrowedVars.TryGetValue(n, out var narrowed)           => narrowed,
        VariableReference { Name: var n } when TryLookup(n, out var ti)                                  => ti.Type,
        VariableReference                                                                                => null,
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
        TextLength tl                                                                                    => InferTextLength(tl),
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
        MatrixRows    mr                                                                                 => InferMatrixRows(mr),
        MatrixColumns mc                                                                                 => InferMatrixColumns(mc),
        IsTypeCheck   tc                                                                                 => InferIsTypeCheck(tc),
        EnvironmentVariableExpression env                                                                => InferEnvVar(env),
        DirectoryContentsExpression   dce                                                                => InferDirectoryContents(dce),
        PathCheckExpression           pce                                                                => InferPathCheck(pce),
        InterruptRequestedExpression                                                                     => CufetType.Fact,
        _                                                                                                => null,
    };

    private CufetType InferEnvVar(EnvironmentVariableExpression env)
    {
        var nameType = InferType(env.Name);
        if (nameType != null && nameType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "the environment variable name must be text",
                null, env.Line,
                $"use a {FormatType(nameType)} as an environment variable name",
                "The variable name must be a text expression (a string literal or a text variable)."));
        return new VoidableType(CufetType.Text);
    }

    private CufetType InferReadExpr(ReadExpression re)
    {
        var sourceType = InferType(re.Source);
        if (sourceType != null && sourceType is not ReadableStreamType { ElementType: TextType })
            throw new TypeException(FormatTypeError(
                "read expects a readable stream of text",
                null, re.Line,
                $"read from a {FormatType(sourceType)}",
                "Use a readable stream of text as the source — 'the input' is always available, or use 'With the file ... open for reading as s:' for a file stream."));

        return re.Form switch
        {
            ReadForm.Line     => new VoidableType(CufetType.Text),
            ReadForm.All      => CufetType.Text,
            ReadForm.AllLines => new SeriesType(CufetType.Text),
            _                 => throw new InvalidOperationException($"Unknown ReadForm {re.Form}"),
        };
    }

    private CufetType? InferUnary(UnaryExpression unary)
    {
        var operand = InferType(unary.Operand);
        if (operand == null) return null;
        if (unary.Op == TokenType.Not)
        {
            if (operand == CufetType.Fact) return CufetType.Fact;
            throw new TypeException(FormatTypeError(
                "'not' works on true-or-false values only",
                null,
                unary.Line,
                $"negate a {FormatType(operand)} value",
                "Make sure the value you're negating is a fact (a true or false value). Write a comparison like 'x is 5' if you need one."));
        }
        // unary minus
        if (operand == CufetType.Number) return CufetType.Number;
        throw new TypeException(FormatTypeError(
            "unary minus works on numbers only",
            null,
            unary.Line,
            $"negate a {FormatType(operand)} value",
            "Make sure the value you're negating is a number."));
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
                        throw new TypeException(FormatTypeError(
                            $"'{FormatOp(bin.Op)}' on '{lo.Name}' can fail — you must handle the failure",
                            null, bin.Line,
                            $"use '{FormatOp(bin.Op)}' on '{lo.Name}' without handling the potential failure",
                            "Wrap this in a 'Try to: / In case of failure:' block, or use 'but on failure <default>'."));
                    return _inTryBlock ? ft.Inner : (CufetType)ft;
                }
                return overloadReturn;
            }
        }

        return bin.Op switch
        {
            TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash or TokenType.Percent
                when l == CufetType.Number && r == CufetType.Number
                => CufetType.Number,
            TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash or TokenType.Percent
                => throw new TypeException(FormatTypeError(
                    "arithmetic requires numbers on both sides",
                    null,
                    bin.Line,
                    $"use {FormatOp(bin.Op)} with {FormatType(l)} and {FormatType(r)}",
                    "If you meant arithmetic, both sides need to be numbers.\nIf you meant to join text, use 'joined to': \"hello\" joined to \" world\".")),
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
            TokenType.Equal or TokenType.NotEqual
                when l == r
                => CufetType.Fact,
            // Union type compared for equality with any value — legal on un-narrowed unions.
            TokenType.Equal or TokenType.NotEqual
                when l is UnionType || r is UnionType
                => CufetType.Fact,
            TokenType.Equal or TokenType.NotEqual
                => throw new TypeException(FormatTypeError(
                    "equality comparison requires matching types",
                    null,
                    bin.Line,
                    $"compare a {FormatType(l)} to a {FormatType(r)}",
                    $"A {FormatType(l)} and a {FormatType(r)} can never be equal — this is likely a mistake. Check which side has the wrong type.")),
            TokenType.Lt or TokenType.Gt or TokenType.Lte or TokenType.Gte
                when l == CufetType.Number && r == CufetType.Number
                => CufetType.Fact,
            TokenType.Lt or TokenType.Gt or TokenType.Lte or TokenType.Gte
                => throw new TypeException(FormatTypeError(
                    "ordering works on numbers only",
                    null,
                    bin.Line,
                    $"order a {FormatType(l)} and a {FormatType(r)}",
                    "Ordering comparisons (>, <, >=, <=) require both sides to be numbers.")),
            TokenType.And or TokenType.Or
                when l == CufetType.Fact && r == CufetType.Fact
                => CufetType.Fact,
            TokenType.And or TokenType.Or
                => throw new TypeException(FormatTypeError(
                    $"'{FormatOp(bin.Op)}' requires true-or-false values on both sides",
                    null,
                    bin.Line,
                    $"use '{FormatOp(bin.Op)}' with {FormatType(l)} and {FormatType(r)}",
                    $"Both sides of '{FormatOp(bin.Op)}' must be a fact (a true or false value). Did you mean to write a comparison like 'x is 0' rather than just 'x'?")),
            _ => null
        };
    }

    // T is assignable to target when:
    //   target == source (same type)
    //   target is voidable T and source == T (widening)
    //   target is voidable T and source is void (void is the absent case of any voidable T)
    private static bool IsAssignable(CufetType target, CufetType source)
    {
        if (target == source) return true;
        if (target is VoidableType v)
            return source == v.Inner || source is VoidType;
        if (target is FailureType f)
            return source == f.Inner || source is FailureMarkerType;
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
                throw new TypeException(FormatTypeError(
                    $"the default value is a {FormatType(defaultType)}, but the voidable holds {FormatTypePlural(inner)}",
                    null, bvd.Line,
                    $"use a {FormatType(defaultType)} as the default for a voidable {FormatType(inner)}",
                    $"The default after 'but void is' must be a {FormatType(inner)}."));
            return inner;
        }
        if (leftType is VoidType)
            return defaultType; // always-void: result is always the default
        if (leftType != null)
            throw new TypeException(FormatTypeError(
                $"'{FormatType(leftType)}' can never be void",
                null, bvd.Line,
                $"use 'but void is' on a {FormatType(leftType)} value",
                "Only voidable values can be void. 'but void is' is only needed for voidable values."));
        return null;
    }

    // Infer type of a union type-test — always yields fact.
    private CufetType InferIsTypeCheck(IsTypeCheck tc)
    {
        _ = InferType(tc.Target); // validate the target expression
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

    // Remove `removedType` from `unionType`, returning the narrowed remaining type.
    // Open union or non-union input → returned unchanged. Single remaining case → unwrapped.
    private static CufetType? RemoveFromUnion(CufetType? unionType, CufetType removedType)
    {
        if (unionType is not UnionType { Cases: { } cases }) return unionType;
        var remaining = cases.Where(c => c != removedType).ToList();
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
            // Loops are not counted: while/for-each may execute zero times,
            // repeat-until exits after one iteration without requiring a return.
        }
        return false;
    }

    private static string FormatTypeError(
        string context,
        string? established,
        int violationLine,
        string action,
        string fix)
    {
        var est = established != null ? $"\n{established}." : "";
        return $"That doesn't work: {context}.{est}\nHere on line {violationLine}, you're trying to {action}.\n\n{fix}";
    }

    private static string FormatType(CufetType type) => type switch
    {
        NumberType                           => "number",
        TextType                             => "text",
        FactType                             => "fact",
        VoidType                             => "void",
        VoidableType { Inner: var inner }    => $"voidable {FormatType(inner)}",
        SeriesType { ElementType: var elem } => $"series of {FormatTypePlural(elem)}",
        FunctionType ft                      => FormatFunctionType(ft),
        RecordType rt                        => FormatRecordType(rt),
        ObjectType ot                        => ot.Name,
        InterfaceType it                     => it.Name,
        ReadableStreamType { ElementType: var elem } => $"readable stream of {FormatTypePlural(elem)}",
        WritableStreamType { ElementType: var elem } => $"writable stream of {FormatTypePlural(elem)}",
        RabbitType                           => "rabbit",
        MapType mt                           => $"map from {FormatType(mt.KeyType)} to {FormatType(mt.ValueType)}",
        MappingType                          => "mapping",
        FailureMarkerType                    => "failure",
        FailureType { Inner: var inner }     => $"{FormatType(inner)} or failure",
        ExceptionMarkerType                  => "exception",
        BookType bt                          => $"book '{bt.Name}'",
        MatrixType                           => "matrix",
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
        TextType                             => "text",
        FactType                             => "facts",
        VoidType                             => "void values",
        VoidableType { Inner: var inner }    => $"voidable {FormatTypePlural(inner)}",
        SeriesType { ElementType: var elem } => $"series of {FormatTypePlural(elem)}",
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
        UnionType { Cases: null }            => "open union values",
        UnionType { Cases: var cs }          => $"({string.Join(" or ", cs!.Select(FormatType))}) values",
        _                                    => "<unknown>",
    };

    private static string FormatExpr(IExpression expr) => expr switch
    {
        NumberLiteral    { Value: var v } => v.ToString(),
        StringLiteral    { Value: var v } => $"\"{v}\"",
        VariableReference { Name: var n } => n,
        _                                 => "<expression>",
    };

    private static string FormatOp(TokenType op) => op switch
    {
        TokenType.Plus    => "+",
        TokenType.Minus   => "-",
        TokenType.Star    => "*",
        TokenType.Slash   => "/",
        TokenType.Percent => "%",
        TokenType.And     => "and",
        TokenType.Or      => "or",
        _                 => op.ToString().ToLower(),
    };
}
