namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
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
            CheckMethodBody(method, objType, od.Line);
    }

    // Checks a method body — 'one' (self) bound to objType, parameters in scope, identical
    // whether the method is nested in the object's definition or declared via 'unto'.
    private void CheckMethodBody(BindStatement method, ObjectType objType, int selfLine)
    {
        var snapshot = new Dictionary<string, TypeInfo>(_env);
        _env.Clear();

        // Method scope: functions + object functions visible, plus 'one' (self) + parameters.
        foreach (var (k, v) in snapshot.Where(kv => kv.Value.Type is FunctionType))
            _env[k] = v;
        _env["one"] = new TypeInfo(objType, new VariableReference("one", 0), selfLine);
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

    // 'Bind ... unto <type>: ...' — a method declared outside its object's definition body.
    // Target type existence/kind and name-collision were already validated in Pass1Hoist;
    // here we just check the body, identically to a nested method.
    private void CheckUntoMethod(BindStatement method)
    {
        var objType = _objectDefs[method.UntoType!];
        CheckMethodBody(method, objType, method.Line);
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
}
