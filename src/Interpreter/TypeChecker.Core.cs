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

// Nominal type — equality by name only. Fields/methods carried for lookup; not part of equality.
public sealed class ObjectType : CufetType
{
    public string Name { get; }
    public IReadOnlyList<CufetType> PositionalTypes { get; }
    public IReadOnlyList<(string FieldName, CufetType FieldType)> NamedFields { get; }
    public IReadOnlyList<(string MethodName, FunctionType Signature)> Methods { get; }
    // Slice 4 — embedding: null means no embed; non-null is the embedded type name (handle).
    public string? EmbeddedTypeName { get; }
    // Slice 5 — conformance: interface names declared with "and <interface>" clauses.
    public IReadOnlyList<string> ConformedInterfaces { get; }

    public ObjectType(
        string name,
        IReadOnlyList<CufetType> positionalTypes,
        IReadOnlyList<(string FieldName, CufetType FieldType)> namedFields,
        IReadOnlyList<(string MethodName, FunctionType Signature)> methods,
        string? embeddedTypeName = null,
        IReadOnlyList<string>? conformedInterfaces = null)
    {
        Name               = name;
        PositionalTypes    = positionalTypes;
        NamedFields        = namedFields.OrderBy(f => f.FieldName, StringComparer.Ordinal).ToList();
        Methods            = methods;
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
public sealed class BookType : CufetType
{
    public string Name { get; }
    public IReadOnlyList<(string MemberName, CufetType MemberType)> Members { get; }

    public BookType(string name, IReadOnlyList<(string MemberName, CufetType MemberType)> members)
    {
        Name    = name;
        Members = members;
    }

    public CufetType? FindMember(string memberName) =>
        Members.FirstOrDefault(m => string.Equals(m.MemberName, memberName, StringComparison.OrdinalIgnoreCase)).MemberType;

    public override bool Equals(object? obj) => obj is BookType b && b.Name == Name;
    public override int GetHashCode() => HashCode.Combine(typeof(BookType), Name);
}

public record TypeInfo(CufetType Type, IExpression EstablishingExpr, int EstablishingLine, bool Permanent = false);

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
    private readonly Dictionary<string, ObjectType>          _objectDefs    = new();
    private readonly Dictionary<string, InterfaceDefinition> _interfaceDefs = new();
    // Active narrowings: variable name → narrowed type (set inside checked branches).
    private readonly Dictionary<string, CufetType>           _narrowedVars  = new();

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

    private void EnterScope() => _scopes.Add(new Dictionary<string, TypeInfo>());
    private void ExitScope()  => _scopes.RemoveAt(_scopes.Count - 1);

    // Save the scope chain and replace it with a fresh single scope (for function isolation).
    // Returns the saved chain for later restoration via RestoreScopes.
    private List<Dictionary<string, TypeInfo>> SaveScopes()
    {
        var saved = _scopes.ToList();
        _scopes.Clear();
        _scopes.Add(new Dictionary<string, TypeInfo> { ["input"] = BuiltinInput });
        return saved;
    }

    private void RestoreScopes(List<Dictionary<string, TypeInfo>> saved)
    {
        _scopes.Clear();
        foreach (var s in saved) _scopes.Add(s);
    }

    // Return context — set when entering a Bind or method body.
    private bool       _inFunction              = false;
    private CufetType? _expectedReturnType      = null; // null = void function
    private int        _functionDeclarationLine  = 0;
    // When true, the first Return statement encountered sets _expectedReturnType
    // instead of validating against it. Used during lambda return-type inference.
    private bool       _inferringLambdaReturn   = false;
    // When true, CastExpression results of type FailureType(T) are auto-unwrapped to T
    // because control only reaches the next line inside a Try block if the call succeeded.
    private bool       _inTryBlock              = false;
    // When true, a CastExpression that returns FailureType(T) is permitted without an explicit
    // handler — set by InferFailureFallback and InferFailurePropagate while checking their
    // inner fallible expression, so the FailureType passes through to their own logic.
    private bool       _inFailureHandledContext = false;
    // When true, 'Suppress the exception.' is valid — only inside an exception handler block.
    private bool       _inExceptionHandler      = false;

    public void Check(Program program)
    {
        _scopes[0]["input"] = BuiltinInput;
        Pass1Hoist(program);
        foreach (var stmt in program.Statements)
            CheckStatement(stmt);
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

        // Gather 'unto'-attached methods by target type name before building ObjectTypes,
        // so their signatures merge into the type's method set regardless of whether the
        // 'unto' Bind appears before or after 'Define object <type>' in the file.
        var untoMethodsByType = new Dictionary<string, List<BindStatement>>();
        foreach (var stmt in program.Statements)
        {
            if (stmt is not BindStatement { UntoType: { } untoType } bind) continue;
            if (!untoMethodsByType.TryGetValue(untoType, out var list))
                untoMethodsByType[untoType] = list = new List<BindStatement>();
            list.Add(bind);
        }

        foreach (var stmt in program.Statements)
        {
            if (stmt is not ObjectDefinition od) continue;
            var methodSigs = od.Methods
                .Select(m => (m.Name, new FunctionType(m.Parameters.Select(p => p.Type).ToList(), m.ReturnType)))
                .ToList();

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

            _objectDefs[od.Name] = new ObjectType(
                od.Name, od.PositionalTypes, od.NamedFields, methodSigs,
                od.EmbeddedTypeName, od.ConformedInterfaces);
        }

        // Anything left in untoMethodsByType targets a name that isn't a defined object type.
        foreach (var (targetName, methods) in untoMethodsByType)
        {
            var reason = _interfaceDefs.ContainsKey(targetName)
                ? $"'{targetName}' is an interface, not an object type — methods can't be attached to it with 'unto'"
                : $"'{targetName}' is not a defined object type";
            throw new TypeException(FormatTypeError(
                reason,
                null, methods[0].Line,
                $"declare a method unto '{targetName}'",
                $"'unto' only attaches methods to object types defined in this program. Define 'object {targetName}' first, or check the spelling."));
        }

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
                foreach (var arm in ifStmt.Arms)
                {
                    _ = InferType(arm.Condition);
                    // If condition is "X is not void", narrow X to its inner type within this arm.
                    string? narrowedVar = null;
                    CufetType? savedNarrowed = null;
                    if (TryGetNotVoidNarrowing(arm.Condition, out var narrowTarget, out var narrowedTo))
                    {
                        narrowedVar = narrowTarget;
                        _narrowedVars.TryGetValue(narrowTarget!, out savedNarrowed);
                        _narrowedVars[narrowTarget!] = narrowedTo!;
                    }
                    EnterScope();
                    foreach (var s in arm.Body)
                        CheckStatement(s);
                    ExitScope();
                    // Restore narrowing state after the arm body.
                    if (narrowedVar != null)
                    {
                        if (savedNarrowed != null) _narrowedVars[narrowedVar] = savedNarrowed;
                        else _narrowedVars.Remove(narrowedVar);
                    }
                }
                if (ifStmt.ElseBody != null)
                {
                    EnterScope();
                    foreach (var s in ifStmt.ElseBody)
                        CheckStatement(s);
                    ExitScope();
                }
                break;
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

        Scope[define.Name] = new TypeInfo(type, define.Value, define.Line, define.Permanent);
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
    }

    private void CheckReturn(ReturnStatement ret)
    {
        if (_inferringLambdaReturn)
        {
            // First return in a lambda body: set the inferred return type.
            _expectedReturnType    = ret.Value != null ? InferType(ret.Value) : null;
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

    // Returns null for genuine inference gaps (undeclared variable, unhandled expression form).
    // Returns a concrete CufetType for anything we can type statically.
    // Throws TypeException for operand type mismatches.
    private CufetType? InferType(IExpression expr) => expr switch
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
        _                                                                                                => null,
    };

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
            if (defaultType != null && !IsAssignable(v.Inner, defaultType))
                throw new TypeException(FormatTypeError(
                    $"the default value is a {FormatType(defaultType)}, but the voidable holds {FormatTypePlural(v.Inner)}",
                    null, bvd.Line,
                    $"use a {FormatType(defaultType)} as the default for a voidable {FormatType(v.Inner)}",
                    $"The default after 'but void is' must be a {FormatType(v.Inner)}."));
            return v.Inner;
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
