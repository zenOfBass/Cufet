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

public record TypeInfo(CufetType Type, IExpression EstablishingExpr, int EstablishingLine);

public sealed class TypeException : Exception
{
    public TypeException(string message) : base(message) { }
}

public sealed class TypeChecker
{
    private readonly Dictionary<string, TypeInfo>           _env           = new();
    private readonly Dictionary<string, ObjectType>         _objectDefs    = new();
    private readonly Dictionary<string, InterfaceDefinition> _interfaceDefs = new();

    // Return context — set when entering a Bind or method body.
    private bool       _inFunction             = false;
    private CufetType? _expectedReturnType     = null; // null = void function
    private int        _functionDeclarationLine = 0;

    public void Check(Program program)
    {
        Pass1Hoist(program);
        foreach (var stmt in program.Statements)
            CheckStatement(stmt);
    }

    // Pass 1: register interfaces (1a), then object types (1b), then function signatures (1c).
    // Interfaces registered first so object conformance declarations can be validated against them.
    private void Pass1Hoist(Program program)
    {
        foreach (var stmt in program.Statements)
        {
            if (stmt is not InterfaceDefinition ifd) continue;
            _interfaceDefs[ifd.Name] = ifd;
        }

        foreach (var stmt in program.Statements)
        {
            if (stmt is not ObjectDefinition od) continue;
            var methodSigs = od.Methods
                .Select(m => (m.Name, new FunctionType(m.Parameters.Select(p => p.Type).ToList(), m.ReturnType)))
                .ToList();
            _objectDefs[od.Name] = new ObjectType(
                od.Name, od.PositionalTypes, od.NamedFields, methodSigs,
                od.EmbeddedTypeName, od.ConformedInterfaces);
        }

        foreach (var stmt in program.Statements)
        {
            if (stmt is not BindStatement bind) continue;
            var paramTypes = bind.Parameters.Select(p => p.Type).ToList();
            _env[bind.Name] = new TypeInfo(
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
                    foreach (var s in arm.Body)
                        CheckStatement(s);
                }
                if (ifStmt.ElseBody != null)
                    foreach (var s in ifStmt.ElseBody)
                        CheckStatement(s);
                break;
            case WhileStatement whileStmt:
                _ = InferType(whileStmt.Condition);
                foreach (var s in whileStmt.Body)
                    CheckStatement(s);
                break;
            case RepeatUntilStatement repeatUntil:
                foreach (var s in repeatUntil.Body)
                    CheckStatement(s);
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
            case SeriesRemoveAtStatement removeAt:
                CheckSeriesRemoveAt(removeAt);
                break;
            case BindStatement bind:
                CheckBind(bind);
                break;
            case CastStatement cs:
            {
                var (funcType, displayName, declLine, argsToValidate) = ResolveForCast(cs.Function, cs.Args, cs.Line);
                if (funcType != null) ValidateCastArgs(funcType, displayName, declLine, argsToValidate, cs.Line);
                break;
            }
            case ReturnStatement ret:
                CheckReturn(ret);
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
        _env[define.Name] = new TypeInfo(type, define.Value, define.Line);
    }

    private void CheckBecomes(BecomesStatement becomes)
    {
        if (!_env.TryGetValue(becomes.Name, out var existing))
            return;

        var rhsType = InferType(becomes.Value);
        if (rhsType == null) return;

        if (rhsType != existing.Type)
            throw new TypeException(FormatTypeError(
                $"'{becomes.Name}' holds {FormatTypePlural(existing.Type)}",
                $"You set it to {FormatExpr(existing.EstablishingExpr)} on line {existing.EstablishingLine}, so it can only ever hold {FormatTypePlural(existing.Type)}",
                becomes.Line,
                $"give it a {FormatType(rhsType)} value",
                $"Variables keep their type for life. If you need a {FormatType(rhsType)} value here, define a new name for it instead."));
    }

    private void CheckBind(BindStatement bind)
    {
        // Build function body env: only function signatures + parameters (no globals).
        // This mirrors runtime scoping — functions cannot see the caller's variables.
        var snapshot = new Dictionary<string, TypeInfo>(_env);
        _env.Clear();
        foreach (var (k, v) in snapshot.Where(kv => kv.Value.Type is FunctionType))
            _env[k] = v;
        foreach (var (type, name) in bind.Parameters)
            _env[name] = new TypeInfo(ResolveParamType(type), new VariableReference(name, 0), bind.Line);

        var prevInFunction        = _inFunction;
        var prevReturnType        = _expectedReturnType;
        var prevFunctionLine      = _functionDeclarationLine;
        _inFunction               = true;
        _expectedReturnType       = bind.ReturnType;
        _functionDeclarationLine  = bind.Line;

        try
        {
            foreach (var stmt in bind.Body)
                CheckStatement(stmt);
        }
        finally
        {
            _inFunction               = prevInFunction;
            _expectedReturnType       = prevReturnType;
            _functionDeclarationLine  = prevFunctionLine;
            _env.Clear();
            foreach (var (k, v) in snapshot) _env[k] = v;
        }

        if (bind.ReturnType != null && !DefinitelyReturns(bind.Body))
            throw new TypeException(FormatTypeError(
                $"'{bind.Name}' is declared to give back a {FormatType(bind.ReturnType)}, but it can reach its end without returning one",
                null,
                bind.Line,
                "define a function that might not return a value",
                "Make sure every path through the function ends with a return statement."));
    }

    private void CheckReturn(ReturnStatement ret)
    {
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
            if (returnType != null && returnType != _expectedReturnType)
                throw new TypeException(FormatTypeError(
                    $"this function is declared to give back a {FormatType(_expectedReturnType)}",
                    $"You declared the return type as {FormatType(_expectedReturnType)} on line {_functionDeclarationLine}",
                    ret.Line,
                    $"return a {FormatType(returnType)} value",
                    $"Change the returned value to a {FormatType(_expectedReturnType)}."));
        }
    }

    // Validates arg count and types against a resolved FunctionType.
    private void ValidateCastArgs(
        FunctionType funcType, string displayName, int declLine,
        IReadOnlyList<IExpression> args, int callLine)
    {
        if (args.Count != funcType.ParameterTypes.Count)
            throw new TypeException(FormatTypeError(
                $"{displayName} expects {funcType.ParameterTypes.Count} argument(s), but you passed {args.Count}",
                $"You declared it on line {declLine} with {funcType.ParameterTypes.Count} parameter(s)",
                callLine,
                $"call it with {args.Count} argument(s)",
                args.Count < funcType.ParameterTypes.Count
                    ? "Add the missing argument(s)."
                    : "Remove the extra argument(s)."));

        for (int i = 0; i < args.Count; i++)
        {
            var argType  = InferType(args[i]);
            if (argType == null) continue;

            // Resolve shell ObjectType params to InterfaceType or full ObjectType as needed.
            var formalType = ResolveParamType(funcType.ParameterTypes[i]);

            if (formalType is InterfaceType ifaceT)
            {
                // Conformance check: argument must be an object type that conforms to the interface.
                if (argType is not ObjectType actualOt ||
                    !_objectDefs.TryGetValue(actualOt.Name, out var actualObjDef) ||
                    !actualObjDef.ConformedInterfaces.Contains(ifaceT.Name))
                {
                    var hint = argType is ObjectType nonConforming
                        ? $"'{nonConforming.Name}' does not declare conformance to '{ifaceT.Name}'. Add 'and {ifaceT.Name}' to its definition."
                        : $"Only objects that conform to '{ifaceT.Name}' can be passed here.";
                    throw new TypeException(FormatTypeError(
                        $"argument {i + 1} of {displayName} must satisfy the '{ifaceT.Name}' interface, but you passed a {FormatType(argType)}",
                        $"You declared {displayName} on line {declLine} with a '{ifaceT.Name}' parameter",
                        callLine,
                        $"pass a {FormatType(argType)} where a '{ifaceT.Name}' is required",
                        hint));
                }
                continue;
            }

            if (argType == formalType) continue;
            throw new TypeException(FormatTypeError(
                $"argument {i + 1} of {displayName} must be a {FormatType(formalType)}, but you passed a {FormatType(argType)}",
                $"You declared {displayName} on line {declLine}, so argument {i + 1} must be a {FormatType(formalType)}",
                callLine,
                $"pass a {FormatType(argType)} as argument {i + 1}",
                $"Change argument {i + 1} to a {FormatType(formalType)}."));
        }
    }

    private void CheckForEach(ForEachStatement forEach)
    {
        if (!_env.TryGetValue(forEach.SeriesName, out var seriesInfo))
            return; // undeclared series — runtime catches; skip body to avoid false positives

        if (seriesInfo.Type is not SeriesType seriesType)
            throw new TypeException(FormatTypeError(
                $"'{forEach.SeriesName}' holds {FormatTypePlural(seriesInfo.Type)}",
                $"You set it to {FormatExpr(seriesInfo.EstablishingExpr)} on line {seriesInfo.EstablishingLine}, so it can only ever hold {FormatTypePlural(seriesInfo.Type)}",
                forEach.Line,
                "loop over it as if it were a series",
                "Only series can be looped over. Define a series if that's what you need."));

        var iterKey = forEach.IteratorName ?? "it";
        var hadPrev = _env.TryGetValue(iterKey, out var prev);
        _env[iterKey] = new TypeInfo(seriesType.ElementType, new VariableReference(forEach.SeriesName, 0), forEach.Line);
        try
        {
            foreach (var s in forEach.Body)
                CheckStatement(s);
        }
        finally
        {
            if (hadPrev) _env[iterKey] = prev!;
            else _env.Remove(iterKey);
        }
    }

    private void CheckSeriesAdd(SeriesAddStatement add)
    {
        if (add.AfterIndex != null) CheckIndex(add.AfterIndex, add.Line);
        if (!_env.TryGetValue(add.SeriesName, out var seriesInfo)) return;
        if (seriesInfo.Type is not SeriesType seriesType) return;

        var valueType = InferType(add.Value);
        if (valueType != null && valueType != seriesType.ElementType)
            throw new TypeException(FormatTypeError(
                $"'{add.SeriesName}' holds {FormatTypePlural(seriesType.ElementType)}",
                $"You defined it on line {seriesInfo.EstablishingLine} as a series of {FormatTypePlural(seriesType.ElementType)}, so it can only accept {FormatTypePlural(seriesType.ElementType)}",
                add.Line,
                $"add a {FormatType(valueType)} value to it",
                $"Change the value to a {FormatType(seriesType.ElementType)}, or define a separate series that holds {FormatTypePlural(valueType)}."));
    }

    private void CheckSeriesRemoveValue(SeriesRemoveValueStatement removeVal)
    {
        if (!_env.TryGetValue(removeVal.SeriesName, out var seriesInfo)) return;
        if (seriesInfo.Type is not SeriesType seriesType) return;

        var valueType = InferType(removeVal.Value);
        if (valueType != null && valueType != seriesType.ElementType)
            throw new TypeException(FormatTypeError(
                $"'{removeVal.SeriesName}' holds {FormatTypePlural(seriesType.ElementType)}",
                $"You defined it on line {seriesInfo.EstablishingLine} as a series of {FormatTypePlural(seriesType.ElementType)}, so only {FormatTypePlural(seriesType.ElementType)} can be removed from it",
                removeVal.Line,
                $"remove a {FormatType(valueType)} value from it",
                $"Make sure the value you're removing is a {FormatType(seriesType.ElementType)}."));
    }

    private void CheckSeriesSet(SeriesSetStatement seriesSet)
    {
        if (seriesSet.Index != null) CheckIndex(seriesSet.Index, seriesSet.Line);
        if (!_env.TryGetValue(seriesSet.SeriesName, out var seriesInfo)) return;

        if (seriesInfo.Type is SeriesType seriesType)
        {
            var valueType = InferType(seriesSet.Value);
            if (valueType != null && valueType != seriesType.ElementType)
                throw new TypeException(FormatTypeError(
                    $"'{seriesSet.SeriesName}' holds {FormatTypePlural(seriesType.ElementType)}",
                    $"You defined it on line {seriesInfo.EstablishingLine} as a series of {FormatTypePlural(seriesType.ElementType)}, so its items can only be set to {FormatTypePlural(seriesType.ElementType)}",
                    seriesSet.Line,
                    $"set an item to a {FormatType(valueType)} value",
                    $"Change the new value to a {FormatType(seriesType.ElementType)}."));
            return;
        }

        if (seriesInfo.Type is ObjectType ot)
        {
            if (seriesSet.Index == null)
                throw new TypeException(FormatTypeError(
                    "'last' doesn't work on objects",
                    null, seriesSet.Line,
                    "use 'last' on an object",
                    "Use a position like 'the first of ...' or a field name like 'alice's name'."));

            if (seriesSet.Index is NumberLiteral { Value: var ov })
            {
                var idx     = (int)ov;
                var display = $"'{seriesSet.SeriesName}'";
                var allPos  = GetAllPositionalTypes(ot);
                if (idx < 1 || idx > allPos.Count)
                    throw new TypeException(FormatTypeError(
                        allPos.Count == 0
                            ? $"{display} has no positional fields"
                            : $"{display} has {allPos.Count} positional field(s) — there is no position {idx}",
                        null, seriesSet.Line,
                        $"set position {idx}",
                        allPos.Count == 0
                            ? $"Object '{ot.Name}' has no positional fields."
                            : $"Positions run 1 through {allPos.Count}."));

                var fieldType = allPos[idx - 1];
                var valueType = InferType(seriesSet.Value);
                if (valueType != null && valueType != fieldType)
                    throw new TypeException(FormatTypeError(
                        $"position {idx} of {display} holds a {FormatType(fieldType)}, not a {FormatType(valueType)}",
                        null, seriesSet.Line,
                        $"set position {idx} to a {FormatType(valueType)}",
                        $"Position {idx} has type {FormatType(fieldType)}."));
            }
            return;
        }

        if (seriesInfo.Type is RecordType rt)
        {
            if (seriesSet.Index == null)
                throw new TypeException(FormatTypeError(
                    "'last' doesn't work on records",
                    null, seriesSet.Line,
                    "use 'last' on a record",
                    "Use a position like 'the first of ...' or a field name like 'the city of ...'."));

            if (seriesSet.Index is NumberLiteral { Value: var v })
            {
                var idx = (int)v;
                var display = $"'{seriesSet.SeriesName}'";
                if (idx < 1 || idx > rt.PositionalTypes.Count)
                    throw new TypeException(FormatTypeError(
                        rt.PositionalTypes.Count == 0
                            ? $"{display} has no positional fields"
                            : $"{display} has {rt.PositionalTypes.Count} positional field(s) — there is no position {idx}",
                        null, seriesSet.Line,
                        $"set position {idx}",
                        rt.PositionalTypes.Count == 0
                            ? "This record has no positional fields."
                            : $"Positions run 1 through {rt.PositionalTypes.Count}."));

                var fieldType = rt.PositionalTypes[idx - 1];
                var valueType = InferType(seriesSet.Value);
                if (valueType != null && valueType != fieldType)
                    throw new TypeException(FormatTypeError(
                        $"position {idx} of {display} holds a {FormatType(fieldType)}, not a {FormatType(valueType)}",
                        null, seriesSet.Line,
                        $"set position {idx} to a {FormatType(valueType)}",
                        $"Position {idx} has type {FormatType(fieldType)}."));
            }
        }
    }

    private void CheckRecordNamedSet(RecordNamedSetStatement stmt)
    {
        var recordType = InferType(stmt.Record);
        if (recordType == null) return;

        if (recordType is ObjectType ot)
        {
            CheckObjectNamedSet(ot, stmt.FieldName, stmt.Value, stmt.Line);
            return;
        }

        if (recordType is not RecordType rt)
            throw new TypeException(FormatTypeError(
                $"you're trying to set field '{stmt.FieldName}' on something that isn't a record or object",
                null, stmt.Line,
                $"set a named field on a {FormatType(recordType)}",
                "Only records and objects have named fields."));

        var field = rt.NamedFields.FirstOrDefault(f => f.Name == stmt.FieldName);
        if (field == default)
        {
            var hint = rt.NamedFields.Count > 0
                ? $"Available named fields: {string.Join(", ", rt.NamedFields.Select(f => f.Name))}."
                : "This record has no named fields.";
            throw new TypeException(FormatTypeError(
                $"this record has no field named '{stmt.FieldName}'",
                null, stmt.Line,
                $"set field '{stmt.FieldName}'",
                hint));
        }

        var valueType = InferType(stmt.Value);
        if (valueType != null && valueType != field.Type)
            throw new TypeException(FormatTypeError(
                $"field '{stmt.FieldName}' holds a {FormatType(field.Type)}, not a {FormatType(valueType)}",
                null, stmt.Line,
                $"set field '{stmt.FieldName}' to a {FormatType(valueType)}",
                $"Field '{stmt.FieldName}' has type {FormatType(field.Type)}."));
    }

    private void CheckPossessiveSet(PossessiveSetStatement stmt)
    {
        var targetType = InferType(stmt.Target);
        if (targetType == null) return;
        if (targetType is not ObjectType ot)
            throw new TypeException(FormatTypeError(
                $"possessive assignment requires an object, but got a {FormatType(targetType)}",
                null, stmt.Line,
                $"set '{stmt.Member}' on a {FormatType(targetType)}",
                "Only objects support possessive field assignment (alice's field becomes X)."));
        CheckObjectNamedSet(ot, stmt.Member, stmt.Value, stmt.Line);
    }

    private void CheckObjectNamedSet(ObjectType ot, string fieldName, IExpression value, int line)
    {
        // Field lookup includes promoted fields from embedded types.
        var fieldType = FindFieldInOtOrPromoted(ot, fieldName);
        if (fieldType == null)
        {
            var allFields = GetAllNamedFields(ot);
            var hint = allFields.Count > 0
                ? $"Available named fields: {string.Join(", ", allFields.Select(f => f.FieldName))}."
                : $"Object '{ot.Name}' has no named fields.";
            throw new TypeException(FormatTypeError(
                $"object '{ot.Name}' has no field named '{fieldName}'",
                null, line,
                $"set field '{fieldName}'",
                hint));
        }
        // Embed handles (ObjectType) can't be set via becomes; only scalar/value types are settable.
        if (fieldType is ObjectType)
            throw new TypeException(FormatTypeError(
                $"'{fieldName}' is an embedded object handle — you can't replace the whole embedded object",
                null, line,
                $"set the embed handle '{fieldName}'",
                $"Mutate individual fields of the embedded object instead."));
        var valueType = InferType(value);
        if (valueType != null && valueType != fieldType)
            throw new TypeException(FormatTypeError(
                $"field '{fieldName}' holds a {FormatType(fieldType)}, not a {FormatType(valueType)}",
                null, line,
                $"set field '{fieldName}' to a {FormatType(valueType)}",
                $"Field '{fieldName}' has type {FormatType(fieldType)}."));
    }

    private void CheckSeriesRemoveAt(SeriesRemoveAtStatement removeAt)
    {
        if (removeAt.Index != null) CheckIndex(removeAt.Index, removeAt.Line);
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
        NumberLiteral                                                           => CufetType.Number,
        StringLiteral                                                           => CufetType.Text,
        UnaryExpression unary                                                   => InferUnary(unary),
        BinaryExpression bin                                                    => InferBinary(bin),
        VariableReference { Name: var n } when _env.TryGetValue(n, out var ti) => ti.Type,
        VariableReference                                                       => null,
        SeriesLiteral lit                                                       => InferSeriesLiteral(lit),
        SeriesAccess acc                                                        => InferSeriesAccess(acc),
        SeriesLength sl                                                         => InferSeriesLength(sl),
        CastExpression cast                                                     => InferCastExpr(cast),
        RecordLiteral lit                                                       => InferRecordLiteral(lit),
        RecordNamedAccess rna                                                   => InferRecordNamedAccess(rna),
        ObjectLiteral lit                                                       => InferObjectLiteral(lit),
        PossessiveAccess poss                                                   => InferPossessiveAccess(poss),
        TextJoin tj                                                             => InferTextJoin(tj),
        TextConvert tc                                                          => InferTextConvert(tc),
        TextLength tl                                                           => InferTextLength(tl),
        _                                                                       => null,
    };

    private CufetType? InferCastExpr(CastExpression cast)
    {
        var (funcType, displayName, declLine, argsToValidate) = ResolveForCast(cast.Function, cast.Args, cast.Line);
        if (funcType == null) return null;

        ValidateCastArgs(funcType, displayName, declLine, argsToValidate, cast.Line);

        if (funcType.ReturnType == null)
            throw new TypeException(FormatTypeError(
                $"{displayName} gives nothing back — it can't be used as a value",
                $"You declared it as void on line {declLine}",
                cast.Line,
                "use its result as a value",
                "Cast it as a statement instead, or change its return type if you need a result."));

        return funcType.ReturnType;
    }

    // Resolves the function expression to (funcType, displayName, declLine, argsToValidate).
    // When method dispatch is detected, argsToValidate is args[1..] (receiver already consumed).
    // Returns (null, ...) if the type is unknown at compile time — runtime catches it.
    // Throws TypeException for known-bad: non-function type, or method/free-function ambiguity.
    private (FunctionType? funcType, string displayName, int declLine, IReadOnlyList<IExpression> argsToValidate)
        ResolveForCast(IExpression funcExpr, IReadOnlyList<IExpression> args, int callLine)
    {
        if (funcExpr is VariableReference vr)
        {
            var md    = TryMethodDispatch(vr.Name, args, callLine);
            bool inEnv = _env.TryGetValue(vr.Name, out var info);

            if (md.HasValue && inEnv && info!.Type is FunctionType)
                throw new TypeException(FormatTypeError(
                    $"'{vr.Name}' is both a method and a free function — this is ambiguous",
                    null,
                    callLine,
                    $"call '{vr.Name}' ambiguously",
                    $"Use the possessive form to call the method explicitly: Cast <object>'s {vr.Name} on (args)."));

            if (md.HasValue)
                return (md.Value.funcType, md.Value.displayName, md.Value.declLine, args.Skip(1).ToList());

            if (inEnv)
            {
                if (info!.Type is not FunctionType ft)
                    throw new TypeException(FormatTypeError(
                        $"'{vr.Name}' holds a {FormatType(info.Type)}, not a function — you can only cast functions",
                        null,
                        callLine,
                        "cast something that isn't a function",
                        "Only functions can be cast. Make sure the name you're casting refers to a function."));
                return (ft, $"'{vr.Name}'", info.EstablishingLine, args);
            }

            // Not in env and not a method.
            // If first arg is an object/interface, this must be an attempted method call — error now.
            if (args.Count > 0)
            {
                var firstArgType = InferType(args[0]);
                if (firstArgType is ObjectType ot2)
                {
                    var avail = ot2.Methods.Count > 0
                        ? $"Available methods: {string.Join(", ", ot2.Methods.Select(m => $"'{m.MethodName}'"))}."
                        : $"'{ot2.Name}' has no methods.";
                    throw new TypeException(FormatTypeError(
                        $"'{ot2.Name}' has no method named '{vr.Name}'",
                        null, callLine,
                        $"call method '{vr.Name}' on a {ot2.Name}",
                        avail));
                }
                if (firstArgType is InterfaceType ifaceT2 &&
                    _interfaceDefs.TryGetValue(ifaceT2.Name, out var ifaceDef2))
                {
                    var avail = ifaceDef2.Methods.Count > 0
                        ? $"Available methods: {string.Join(", ", ifaceDef2.Methods.Select(m => $"'{m.MethodName}'"))}."
                        : $"Interface '{ifaceT2.Name}' declares no methods.";
                    throw new TypeException(FormatTypeError(
                        $"interface '{ifaceT2.Name}' has no method named '{vr.Name}'",
                        null, callLine,
                        $"call method '{vr.Name}' through interface '{ifaceT2.Name}'",
                        avail));
                }
            }

            // Unknown identifier with non-object first arg (or no args) — runtime catches it.
            return (null, $"'{vr.Name}'", callLine, args);
        }

        // General path: PossessiveAccess → FunctionType for method ref, etc.
        var exprType = InferType(funcExpr);
        if (exprType == null) return (null, "this function", callLine, args);
        if (exprType is not FunctionType funcType)
            throw new TypeException(FormatTypeError(
                $"this expression holds a {FormatType(exprType)}, not a function — you can only cast functions",
                null,
                callLine,
                "cast something that isn't a function",
                "Only functions can be cast."));
        return (funcType, "this function", callLine, args);
    }

    // Returns method's FunctionType (params only, no receiver) and display info when the
    // first arg's type is an object or interface that declares a method with the given name.
    // Returns null if no such method is found.
    private (FunctionType funcType, string displayName, int declLine)? TryMethodDispatch(
        string name, IReadOnlyList<IExpression> args, int callLine)
    {
        if (args.Count == 0) return null;

        var firstArgType = InferType(args[0]);
        if (firstArgType == null) return null;

        if (firstArgType is ObjectType ot)
        {
            var sig = FindMethodInOtOrPromoted(ot, name);
            if (sig == null) return null;
            return (sig, $"method '{name}' on '{ot.Name}'", callLine);
        }

        if (firstArgType is InterfaceType ifaceT &&
            _interfaceDefs.TryGetValue(ifaceT.Name, out var ifaceDef))
        {
            var ifaceSig = ifaceDef.Methods.FirstOrDefault(m => m.MethodName == name);
            if (ifaceSig == default) return null;
            return (new FunctionType(ifaceSig.ParamTypes, ifaceSig.ReturnType),
                    $"method '{name}' on interface '{ifaceT.Name}'", callLine);
        }

        return null;
    }

    private CufetType InferSeriesLiteral(SeriesLiteral lit)
    {
        CufetType? inferred = null;
        for (int i = 0; i < lit.Elements.Count; i++)
        {
            var elemType = InferType(lit.Elements[i]);
            if (elemType == null) continue;
            if (inferred == null)
            {
                inferred = elemType;
            }
            else if (inferred != elemType)
            {
                throw new TypeException(FormatTypeError(
                    "every item in a series must be the same type",
                    $"The first item is a {FormatType(inferred)}, so all items must be {FormatTypePlural(inferred)}",
                    lit.Line,
                    $"make item {i + 1} a {FormatType(elemType)}",
                    $"Remove the mismatched item, or define two separate series — one for {FormatTypePlural(inferred)} and one for {FormatTypePlural(elemType)}."));
            }
        }

        if (lit.Annotation != null)
        {
            if (inferred != null && inferred != lit.Annotation)
                throw new TypeException(FormatTypeError(
                    $"you said this is a series of {FormatTypePlural(lit.Annotation)}",
                    $"That annotation fixes the element type as {FormatType(lit.Annotation)}",
                    lit.Line,
                    $"put a {FormatType(inferred)} item in it",
                    "Fix the annotation to match the elements, or change the elements to match the annotation."));
            return new SeriesType(lit.Annotation);
        }

        if (inferred == null)
            throw new TypeException(FormatTypeError(
                "an empty series has no items to infer its type from",
                null,
                lit.Line,
                "define an empty series without saying what type of items it will hold",
                "Add an annotation to declare the element type: a series of numbers (), a series of text (), or a series of facts ()."));

        return new SeriesType(inferred);
    }

    private CufetType? InferSeriesAccess(SeriesAccess acc)
    {
        if (acc.Index != null) CheckIndex(acc.Index, acc.Line);
        var targetType = InferType(acc.Target);
        if (targetType == null) return null;

        if (targetType is SeriesType st) return st.ElementType;

        if (targetType is RecordType rt)
        {
            if (acc.Index == null) // "last" — not meaningful for records
                throw new TypeException(FormatTypeError(
                    $"'last' doesn't work on records",
                    null, acc.Line,
                    "use 'last' on a record",
                    "Use a position like 'the first of ...' or a field name like 'the city of ...'."));
            if (acc.Index is NumberLiteral { Value: var v })
            {
                var idx = (int)v;
                var displayName = acc.Target is VariableReference vr ? $"'{vr.Name}'" : "this record";
                if (idx < 1 || idx > rt.PositionalTypes.Count)
                    throw new TypeException(FormatTypeError(
                        rt.PositionalTypes.Count == 0
                            ? $"{displayName} has no positional fields"
                            : $"{displayName} has {rt.PositionalTypes.Count} positional field(s) — there is no position {idx}",
                        null, acc.Line,
                        $"access position {idx}",
                        rt.PositionalTypes.Count == 0
                            ? "This record has no positional fields. Access fields by name instead."
                            : $"Positions run 1 through {rt.PositionalTypes.Count}."));
                return rt.PositionalTypes[idx - 1];
            }
            // Dynamic index — can't check statically; runtime handles it.
            return null;
        }

        if (targetType is ObjectType ot)
        {
            if (acc.Index == null)
                throw new TypeException(FormatTypeError(
                    $"'last' doesn't work on objects",
                    null, acc.Line,
                    "use 'last' on an object",
                    "Use a position like 'the first of ...' or a field name like 'the city of ...'."));
            if (acc.Index is NumberLiteral { Value: var v })
            {
                var idx = (int)v;
                var allPos = GetAllPositionalTypes(ot);
                if (idx < 1 || idx > allPos.Count)
                    throw new TypeException(FormatTypeError(
                        allPos.Count == 0
                            ? $"'{ot.Name}' has no positional fields"
                            : $"'{ot.Name}' has {allPos.Count} positional field(s) — there is no position {idx}",
                        null, acc.Line,
                        $"access position {idx}",
                        allPos.Count == 0
                            ? $"'{ot.Name}' has no positional fields. Access fields by name instead."
                            : $"Positions run 1 through {allPos.Count}."));
                return allPos[idx - 1];
            }
            return null;
        }

        return null;
    }

    private CufetType InferSeriesLength(SeriesLength sl)
    {
        if (_env.TryGetValue(sl.SeriesName, out var info) && info.Type is RecordType)
            throw new TypeException(FormatTypeError(
                $"'the number of' works on series, not records",
                null, 0,
                $"get the number of items in '{sl.SeriesName}' (a record)",
                "Records don't have a length. Access individual fields by name or position."));
        return CufetType.Number;
    }

    private RecordType InferRecordLiteral(RecordLiteral lit)
    {
        var positionalTypes = new List<CufetType>();
        foreach (var field in lit.PositionalFields)
        {
            var t = InferType(field);
            if (t == null)
                throw new TypeException(FormatTypeError(
                    "the type of a positional record field can't be determined",
                    null, lit.Line,
                    "use an expression whose type can't be inferred as a record field",
                    "Start with a literal value or a defined variable so the field type is clear."));
            positionalTypes.Add(t);
        }

        var namedFields = new List<(string Name, CufetType Type)>();
        foreach (var (name, valueExpr) in lit.NamedFields)
        {
            if (namedFields.Any(f => f.Name == name))
                throw new TypeException(FormatTypeError(
                    $"the record has two fields both named '{name}'",
                    null, lit.Line,
                    $"define a record with duplicate field name '{name}'",
                    "Each named field must have a unique name."));
            var t = InferType(valueExpr);
            if (t == null)
                throw new TypeException(FormatTypeError(
                    $"the type of field '{name}' can't be determined",
                    null, lit.Line,
                    "use an expression whose type can't be inferred as a record field",
                    "Start with a literal value or a defined variable so the field type is clear."));
            namedFields.Add((name, t));
        }

        return new RecordType(positionalTypes, namedFields);
    }

    private CufetType? InferRecordNamedAccess(RecordNamedAccess rna)
    {
        var recordType = InferType(rna.Record);
        if (recordType == null) return null;

        // Named field access also works on objects (the <name> of <object>).
        // Includes promoted fields from embedded types and the embed handle itself.
        if (recordType is ObjectType ot)
        {
            var found = FindFieldInOtOrPromoted(ot, rna.FieldName);
            if (found != null) return found;
            var allFields = GetAllNamedFields(ot);
            throw new TypeException(FormatTypeError(
                $"'{ot.Name}' has no field named '{rna.FieldName}'",
                null, rna.Line,
                $"access field '{rna.FieldName}'",
                allFields.Count > 0
                    ? $"Available fields: {string.Join(", ", allFields.Select(f => $"'{f.FieldName}'"))}."
                    : $"'{ot.Name}' has no named fields."));
        }

        if (recordType is not RecordType rt)
            throw new TypeException(FormatTypeError(
                $"you're trying to access field '{rna.FieldName}' on something that isn't a record or object",
                null, rna.Line,
                $"access a named field of a {FormatType(recordType)}",
                "Only records and objects have named fields."));

        var field = rt.NamedFields.FirstOrDefault(f => f.Name == rna.FieldName);
        if (field == default)
        {
            var suggestion = rt.NamedFields
                .Select(f => (f.Name, dist: Levenshtein(f.Name, rna.FieldName)))
                .Where(p => p.dist <= 2)
                .OrderBy(p => p.dist)
                .Select(p => p.Name)
                .FirstOrDefault();
            var available = rt.NamedFields.Count > 0
                ? $" Named fields: {string.Join(", ", rt.NamedFields.Select(f => $"'{f.Name}'"))}."
                : " This record has no named fields.";
            var fix = suggestion != null
                ? $"Did you mean '{suggestion}'?{available}"
                : available;
            throw new TypeException(FormatTypeError(
                $"this record has no field named '{rna.FieldName}'",
                null, rna.Line,
                $"access field '{rna.FieldName}'",
                fix));
        }

        return field.Type;
    }

    // ── Objects ───────────────────────────────────────────────────────────────

    // ── Embedding helpers (Slice 4) ───────────────────────────────────────────

    // Finds a named field type in ot, including the embed handle and promoted fields.
    // own fields take priority; then the embed handle (fieldName == EmbeddedTypeName);
    // then promoted fields recursively through the embed chain.
    // Returns null if not found (collision detection happens at definition time).
    private CufetType? FindFieldInOtOrPromoted(ObjectType ot, string fieldName)
    {
        var own = ot.NamedFields.FirstOrDefault(f => f.FieldName == fieldName);
        if (own != default) return own.FieldType;

        if (ot.EmbeddedTypeName != null && _objectDefs.TryGetValue(ot.EmbeddedTypeName, out var embed))
        {
            // Embed handle: "the person of customer" returns the embedded ObjectType.
            if (fieldName == ot.EmbeddedTypeName) return embed;
            return FindFieldInOtOrPromoted(embed, fieldName);
        }
        return null;
    }

    // Finds a method signature in ot or its embed chain (promotion).
    private FunctionType? FindMethodInOtOrPromoted(ObjectType ot, string methodName)
    {
        var own = ot.Methods.FirstOrDefault(m => m.MethodName == methodName);
        if (own != default) return own.Signature;

        if (ot.EmbeddedTypeName != null && _objectDefs.TryGetValue(ot.EmbeddedTypeName, out var embed))
            return FindMethodInOtOrPromoted(embed, methodName);

        return null;
    }

    // Collects all positional types: own first, then embedded (recursively).
    private List<CufetType> GetAllPositionalTypes(ObjectType ot)
    {
        var result = new List<CufetType>(ot.PositionalTypes);
        if (ot.EmbeddedTypeName != null && _objectDefs.TryGetValue(ot.EmbeddedTypeName, out var embed))
            result.AddRange(GetAllPositionalTypes(embed));
        return result;
    }

    // Collects all named fields: own first, then embedded (recursively).
    private List<(string FieldName, CufetType FieldType)> GetAllNamedFields(ObjectType ot)
    {
        var result = new List<(string, CufetType)>(ot.NamedFields);
        if (ot.EmbeddedTypeName != null && _objectDefs.TryGetValue(ot.EmbeddedTypeName, out var embed))
            result.AddRange(GetAllNamedFields(embed));
        return result;
    }

    // Returns all field names reachable via promotion (for collision detection).
    private HashSet<string> GetAllPromotedFieldNames(ObjectType embedType)
    {
        var names = new HashSet<string>(embedType.NamedFields.Select(f => f.FieldName));
        if (embedType.EmbeddedTypeName != null && _objectDefs.TryGetValue(embedType.EmbeddedTypeName, out var deeper))
            names.UnionWith(GetAllPromotedFieldNames(deeper));
        return names;
    }

    // Returns all method names reachable via promotion (for collision detection).
    private HashSet<string> GetAllPromotedMethodNames(ObjectType embedType)
    {
        var names = new HashSet<string>(embedType.Methods.Select(m => m.MethodName));
        if (embedType.EmbeddedTypeName != null && _objectDefs.TryGetValue(embedType.EmbeddedTypeName, out var deeper))
            names.UnionWith(GetAllPromotedMethodNames(deeper));
        return names;
    }

    // Validates the embedding clause for an object definition: checks existence and collisions.
    private void ValidateObjectEmbedding(ObjectDefinition od, ObjectType objType)
    {
        if (od.EmbeddedTypeName == null) return;

        if (!_objectDefs.TryGetValue(od.EmbeddedTypeName, out var embedType))
            throw new TypeException(FormatTypeError(
                $"object '{od.Name}' embeds '{od.EmbeddedTypeName}', but no such object type is defined",
                null, od.Line,
                $"embed '{od.EmbeddedTypeName}' in '{od.Name}'",
                $"Define object {od.EmbeddedTypeName} with (...). before defining {od.Name}."));

        var promotedFields  = GetAllPromotedFieldNames(embedType);
        var promotedMethods = GetAllPromotedMethodNames(embedType);

        foreach (var f in objType.NamedFields)
        {
            if (promotedFields.Contains(f.FieldName))
                throw new TypeException(FormatTypeError(
                    $"'{od.Name}' has its own field '{f.FieldName}' which collides with a promoted field from '{od.EmbeddedTypeName}'",
                    null, od.Line,
                    $"define '{f.FieldName}' in '{od.Name}' while embedding '{od.EmbeddedTypeName}'",
                    $"Rename one of the fields. To access the embedded field explicitly, use 'the {f.FieldName} of the {od.EmbeddedTypeName} of ...'."));
        }
        foreach (var m in objType.Methods)
        {
            if (promotedMethods.Contains(m.MethodName))
                throw new TypeException(FormatTypeError(
                    $"'{od.Name}' has its own method '{m.MethodName}' which collides with a promoted method from '{od.EmbeddedTypeName}'",
                    null, od.Line,
                    $"define '{m.MethodName}' in '{od.Name}' while embedding '{od.EmbeddedTypeName}'",
                    $"Rename one of the methods. To call the embedded method explicitly, use 'Cast {m.MethodName} on the {od.EmbeddedTypeName} of ...'."));
        }
    }

    // Resolves ObjectType shells (produced by ParseTypeAnnotation for identifiers) to their
    // proper type: InterfaceType if the name is in _interfaceDefs, full ObjectType if in _objectDefs.
    // Returns the type unchanged if it's already a resolved type or not a named-object shell.
    private CufetType ResolveParamType(CufetType type) => type switch
    {
        ObjectType { PositionalTypes.Count: 0, NamedFields.Count: 0, Methods.Count: 0,
                     EmbeddedTypeName: null, ConformedInterfaces.Count: 0 } ot
            when _interfaceDefs.ContainsKey(ot.Name) => new InterfaceType(ot.Name),
        ObjectType { PositionalTypes.Count: 0, NamedFields.Count: 0, Methods.Count: 0,
                     EmbeddedTypeName: null, ConformedInterfaces.Count: 0 } ot
            when _objectDefs.ContainsKey(ot.Name) => _objectDefs[ot.Name],
        _ => type
    };

    // Validates all conformance declarations for an object: the interface must exist and the
    // object must implement every method with a matching signature (return type + param types).
    private void ValidateObjectConformance(ObjectDefinition od, ObjectType objType)
    {
        foreach (var ifaceName in od.ConformedInterfaces)
        {
            if (!_interfaceDefs.TryGetValue(ifaceName, out var iface))
                throw new TypeException(FormatTypeError(
                    $"'{od.Name}' claims to satisfy '{ifaceName}', but no such interface is defined",
                    null, od.Line,
                    $"conform to '{ifaceName}'",
                    $"Define the interface first: Define {ifaceName} as an interface for {{...}}."));

            foreach (var (methodName, returnType, paramTypes) in iface.Methods)
            {
                var sig = FindMethodInOtOrPromoted(objType, methodName);
                if (sig == null)
                    throw new TypeException(FormatTypeError(
                        $"'{od.Name}' claims to satisfy '{ifaceName}' but has no method '{methodName}'",
                        null, od.Line,
                        $"conform to interface '{ifaceName}'",
                        $"Add a method '{methodName}' to '{od.Name}' (or embed an object that provides it)."));

                if (sig.ReturnType != returnType || !sig.ParameterTypes.SequenceEqual(paramTypes))
                    throw new TypeException(FormatTypeError(
                        $"'{od.Name}'.'{methodName}' has the wrong signature for interface '{ifaceName}'",
                        null, od.Line,
                        $"conform to '{ifaceName}' with a mismatched '{methodName}'",
                        $"Interface '{ifaceName}' requires '{methodName}' to have signature: " +
                        $"{FormatFunctionType(new FunctionType(paramTypes, returnType))}."));
            }
        }
    }

    private void CheckObjectDefinition(ObjectDefinition od)
    {
        if (!_objectDefs.TryGetValue(od.Name, out var objType)) return;

        ValidateObjectEmbedding(od, objType);
        ValidateObjectConformance(od, objType);

        foreach (var method in od.Methods)
        {
            var snapshot = new Dictionary<string, TypeInfo>(_env);
            _env.Clear();

            // Method scope: functions + object functions visible, plus 'one' (self) + parameters.
            foreach (var (k, v) in snapshot.Where(kv => kv.Value.Type is FunctionType))
                _env[k] = v;
            _env["one"] = new TypeInfo(objType, new VariableReference("one", 0), od.Line);
            foreach (var (type, name) in method.Parameters)
                _env[name] = new TypeInfo(ResolveParamType(type), new VariableReference(name, 0), method.Line);

            var prevInFunction       = _inFunction;
            var prevReturnType       = _expectedReturnType;
            var prevFunctionLine     = _functionDeclarationLine;
            _inFunction              = true;
            _expectedReturnType      = method.ReturnType;
            _functionDeclarationLine = method.Line;

            try
            {
                foreach (var stmt in method.Body)
                    CheckStatement(stmt);

                if (method.ReturnType != null && !DefinitelyReturns(method.Body))
                    throw new TypeException(FormatTypeError(
                        $"method '{method.Name}' is declared to give back a {FormatType(method.ReturnType)}, but it can reach its end without returning one",
                        null, method.Line,
                        "define a method that might not return a value",
                        "Make sure every path through the method ends with a return statement."));
            }
            finally
            {
                _inFunction              = prevInFunction;
                _expectedReturnType      = prevReturnType;
                _functionDeclarationLine = prevFunctionLine;
                _env.Clear();
                foreach (var (k, v) in snapshot) _env[k] = v;
            }
        }
    }

    private ObjectType InferObjectLiteral(ObjectLiteral lit)
    {
        if (!_objectDefs.TryGetValue(lit.TypeName, out var objType))
            throw new TypeException(FormatTypeError(
                $"'{lit.TypeName}' is not a defined object type",
                null, lit.Line,
                $"create a new {lit.TypeName} object",
                $"Define the object type first: Define object {lit.TypeName} with (...)."));

        // Flat construction: positionals = own + embedded (all levels), in order.
        var allPositionals = GetAllPositionalTypes(objType);
        if (lit.PositionalValues.Count != allPositionals.Count)
            throw new TypeException(FormatTypeError(
                $"'{lit.TypeName}' expects {allPositionals.Count} positional field(s) (including promoted), but you provided {lit.PositionalValues.Count}",
                null, lit.Line,
                $"provide {lit.PositionalValues.Count} positional field(s)",
                $"'{lit.TypeName}' requires exactly {allPositionals.Count} positional field(s)."));

        for (int i = 0; i < lit.PositionalValues.Count; i++)
        {
            var valType = InferType(lit.PositionalValues[i]);
            if (valType != null && valType != allPositionals[i])
                throw new TypeException(FormatTypeError(
                    $"positional field {i + 1} of '{lit.TypeName}' must be a {FormatType(allPositionals[i])}",
                    null, lit.Line,
                    $"provide a {FormatType(valType)} for positional field {i + 1}",
                    $"Change the value to a {FormatType(allPositionals[i])}."));
        }

        // Flat construction: named fields = own + embedded (all levels).
        var allNamedFields = GetAllNamedFields(objType);

        // Check all required named fields are present.
        foreach (var (requiredName, _) in allNamedFields)
        {
            if (!lit.NamedValues.Any(nv => nv.Name == requiredName))
                throw new TypeException(FormatTypeError(
                    $"field '{requiredName}' of '{lit.TypeName}' is missing",
                    null, lit.Line,
                    $"create a {lit.TypeName} without field '{requiredName}'",
                    $"Add 'the {requiredName} <value>' to the object literal."));
        }

        // Check provided fields are valid (exist somewhere in the chain) and correctly typed.
        foreach (var (name, expr) in lit.NamedValues)
        {
            var fieldType = FindFieldInOtOrPromoted(objType, name);
            if (fieldType == null || fieldType is ObjectType)
                throw new TypeException(FormatTypeError(
                    $"'{lit.TypeName}' has no field named '{name}'",
                    null, lit.Line,
                    $"set unknown field '{name}'",
                    allNamedFields.Count > 0
                        ? $"Available named fields: {string.Join(", ", allNamedFields.Select(f => $"'{f.FieldName}'"))}."
                        : $"'{lit.TypeName}' has no named fields."));
            var valType = InferType(expr);
            if (valType != null && valType != fieldType)
                throw new TypeException(FormatTypeError(
                    $"field '{name}' of '{lit.TypeName}' must be a {FormatType(fieldType)}",
                    null, lit.Line,
                    $"provide a {FormatType(valType)} for field '{name}'",
                    $"Change the value to a {FormatType(fieldType)}."));
        }

        return objType;
    }

    private CufetType? InferPossessiveAccess(PossessiveAccess poss)
    {
        var targetType = InferType(poss.Target);
        if (targetType == null) return null;

        // Interface-typed variable: 's can only reach interface methods.
        if (targetType is InterfaceType ifaceT)
        {
            if (!_interfaceDefs.TryGetValue(ifaceT.Name, out var ifaceDef))
                return null;
            var ifaceSig = ifaceDef.Methods.FirstOrDefault(m => m.MethodName == poss.Member);
            if (ifaceSig == default)
                throw new TypeException(FormatTypeError(
                    $"interface '{ifaceT.Name}' has no method named '{poss.Member}'",
                    null, poss.Line,
                    $"use 's to access '{poss.Member}' through interface '{ifaceT.Name}'",
                    ifaceDef.Methods.Count > 0
                        ? $"Available methods: {string.Join(", ", ifaceDef.Methods.Select(m => $"'{m.MethodName}'"))}."
                        : $"Interface '{ifaceT.Name}' declares no methods."));
            return new FunctionType(ifaceSig.ParamTypes, ifaceSig.ReturnType);
        }

        if (targetType is not ObjectType ot)
            throw new TypeException(FormatTypeError(
                $"possessive access ('s) requires an object, but got a {FormatType(targetType)}",
                null, poss.Line,
                $"use 's on a {FormatType(targetType)}",
                "Only objects support the possessive 's syntax."));

        // Methods take priority over fields; both search includes promoted (embed chain).
        var methodSig = FindMethodInOtOrPromoted(ot, poss.Member);
        if (methodSig != null) return methodSig;

        var fieldType = FindFieldInOtOrPromoted(ot, poss.Member);
        if (fieldType != null) return fieldType;

        var allFields  = GetAllNamedFields(ot);
        var available  = string.Join(", ",
            allFields.Select(f => $"'{f.FieldName}'")
            .Concat(ot.Methods.Select(m => $"'{m.MethodName}' (method)")));
        throw new TypeException(FormatTypeError(
            $"'{ot.Name}' has no field or method named '{poss.Member}'",
            null, poss.Line,
            $"access '{poss.Member}' on a {ot.Name}",
            available.Length > 0 ? $"Available: {available}." : $"'{ot.Name}' has no fields or methods."));
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

    // ── Text operations (Slice 1) ─────────────────────────────────────────────

    private CufetType InferTextJoin(TextJoin tj)
    {
        var left  = InferType(tj.Left);
        var right = InferType(tj.Right);

        if (left != null && left != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "you can only join text to text",
                null,
                tj.Line,
                $"join a {FormatType(left)} to text",
                $"Convert the {FormatType(left)} first: use 'converted to text'.\nFor example: n converted to text joined to \" items\"."));

        if (right != null && right != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "you can only join text to text",
                null,
                tj.Line,
                $"join text to a {FormatType(right)}",
                $"Convert the {FormatType(right)} first: use 'converted to text'.\nFor example: \"score: \" joined to n converted to text."));

        return CufetType.Text;
    }

    private CufetType InferTextConvert(TextConvert tc)
    {
        var operand = InferType(tc.Value);
        if (operand == null) return CufetType.Text;
        if (operand == CufetType.Number || operand == CufetType.Fact || operand == CufetType.Text)
            return CufetType.Text;
        throw new TypeException(FormatTypeError(
            $"'converted to text' doesn't work on {FormatTypePlural(operand)}",
            null,
            tc.Line,
            $"convert a {FormatType(operand)} to text",
            "Only numbers and facts can be converted to text."));
    }

    private CufetType InferTextLength(TextLength tl)
    {
        var operand = InferType(tl.Target);
        if (operand == null) return CufetType.Number;
        if (operand == CufetType.Text) return CufetType.Number;
        throw new TypeException(FormatTypeError(
            "'the length of' works on text only",
            null,
            tl.Line,
            $"get the length of a {FormatType(operand)}",
            "Only text values have a character length. For series, use 'the number of series'."));
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
        SeriesType { ElementType: var elem } => $"series of {FormatTypePlural(elem)}",
        FunctionType ft                      => FormatFunctionType(ft),
        RecordType rt                        => FormatRecordType(rt),
        ObjectType ot                        => ot.Name,
        InterfaceType it                     => it.Name,
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
        SeriesType { ElementType: var elem } => $"series of {FormatTypePlural(elem)}",
        FunctionType                         => "functions",
        RecordType rt                        => FormatRecordType(rt),
        ObjectType ot                        => $"{ot.Name} objects",
        InterfaceType it                     => $"{it.Name} values",
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
