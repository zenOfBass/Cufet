using Cufet.Lexer;

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

        // Setter intercepts the write if one is defined; setter param type is the expected type.
        var setterSig = FindSetterInOtOrPromoted(ot, stmt.Member);
        if (setterSig != null)
        {
            var valueType = InferType(stmt.Value);
            if (valueType != null && valueType != setterSig.Value.ParamType)
                throw new TypeException(FormatTypeError(
                    $"setter for '{stmt.Member}' expects a {FormatType(setterSig.Value.ParamType)}, not a {FormatType(valueType)}",
                    null, stmt.Line,
                    $"set '{stmt.Member}' to a {FormatType(valueType)}",
                    $"The setter for '{stmt.Member}' accepts a {FormatType(setterSig.Value.ParamType)}."));
            CheckRegionStore(stmt.Value, InferType(stmt.Value), ContainerDepthOf(stmt.Target), stmt.Line,
                $"set '{stmt.Member}' to a value from a shorter-lived rabbit region than the object");
            return;
        }

        // No setter — normal field write check.
        CheckObjectNamedSet(ot, stmt.Member, stmt.Value, stmt.Line);
        // Region invariant: the value being stored cannot outlive the object's rabbit region.
        var valType = InferType(stmt.Value);
        CheckRegionStore(stmt.Value, valType, ContainerDepthOf(stmt.Target), stmt.Line,
            $"set '{stmt.Member}' to a value from a shorter-lived rabbit region than the object");
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

    // Finds a getter return type in ot or its embed chain (promotion).
    private CufetType? FindGetterInOtOrPromoted(ObjectType ot, string name)
    {
        var own = ot.Getters.FirstOrDefault(g => g.GetterName == name);
        if (own != default) return own.ReturnType;

        if (ot.EmbeddedTypeName != null && _objectDefs.TryGetValue(ot.EmbeddedTypeName, out var embed))
            return FindGetterInOtOrPromoted(embed, name);

        return null;
    }

    // Finds a setter signature in ot or its embed chain (promotion).
    private (string SetterName, CufetType ParamType, string ParamName)? FindSetterInOtOrPromoted(ObjectType ot, string name)
    {
        var own = ot.Setters.FirstOrDefault(s => s.SetterName == name);
        if (own != default) return own;

        if (ot.EmbeddedTypeName != null && _objectDefs.TryGetValue(ot.EmbeddedTypeName, out var embed))
            return FindSetterInOtOrPromoted(embed, name);

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

    // Returns all getter names reachable via promotion (for collision detection).
    private HashSet<string> GetAllPromotedGetterNames(ObjectType embedType)
    {
        var names = new HashSet<string>(embedType.Getters.Select(g => g.GetterName));
        if (embedType.EmbeddedTypeName != null && _objectDefs.TryGetValue(embedType.EmbeddedTypeName, out var deeper))
            names.UnionWith(GetAllPromotedGetterNames(deeper));
        return names;
    }

    // Returns all setter names reachable via promotion (for collision detection).
    private HashSet<string> GetAllPromotedSetterNames(ObjectType embedType)
    {
        var names = new HashSet<string>(embedType.Setters.Select(s => s.SetterName));
        if (embedType.EmbeddedTypeName != null && _objectDefs.TryGetValue(embedType.EmbeddedTypeName, out var deeper))
            names.UnionWith(GetAllPromotedSetterNames(deeper));
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
        var promotedGetters = GetAllPromotedGetterNames(embedType);
        var promotedSetters = GetAllPromotedSetterNames(embedType);

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
        foreach (var g in objType.Getters)
        {
            if (promotedGetters.Contains(g.GetterName))
                throw new TypeException(FormatTypeError(
                    $"'{od.Name}' has its own getter '{g.GetterName}' which collides with a promoted getter from '{od.EmbeddedTypeName}'",
                    null, od.Line,
                    $"define getter '{g.GetterName}' in '{od.Name}' while embedding '{od.EmbeddedTypeName}'",
                    $"Rename one of the getters."));
            if (promotedMethods.Contains(g.GetterName))
                throw new TypeException(FormatTypeError(
                    $"'{od.Name}' getter '{g.GetterName}' collides with a promoted method of the same name from '{od.EmbeddedTypeName}'",
                    null, od.Line,
                    $"define getter '{g.GetterName}' while embedding a type with a method of the same name",
                    $"Rename the getter or the method."));
        }
        foreach (var s in objType.Setters)
        {
            if (promotedSetters.Contains(s.SetterName))
                throw new TypeException(FormatTypeError(
                    $"'{od.Name}' has its own setter '{s.SetterName}' which collides with a promoted setter from '{od.EmbeddedTypeName}'",
                    null, od.Line,
                    $"define setter '{s.SetterName}' in '{od.Name}' while embedding '{od.EmbeddedTypeName}'",
                    $"Rename one of the setters."));
            if (promotedMethods.Contains(s.SetterName))
                throw new TypeException(FormatTypeError(
                    $"'{od.Name}' setter '{s.SetterName}' collides with a promoted method of the same name from '{od.EmbeddedTypeName}'",
                    null, od.Line,
                    $"define setter '{s.SetterName}' while embedding a type with a method of the same name",
                    $"Rename the setter or the method."));
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
        ValidateGetterSetterNames(od, objType);

        foreach (var method in od.Methods)
            CheckMethodBody(method, objType, od.Line);
        foreach (var getter in od.Getters)
            CheckGetterBody(getter, objType, od.Line);
        foreach (var setter in od.Setters)
            CheckSetterBody(setter, objType, od.Line);
    }

    // Validates own-type getter/setter name uniqueness and no clashes with methods.
    // (Getter + setter of the same name = valid pair. Getter/setter vs. field = valid backing-field pattern.)
    private void ValidateGetterSetterNames(ObjectDefinition od, ObjectType objType)
    {
        var seenGetters = new HashSet<string>();
        foreach (var g in objType.Getters)
        {
            if (!seenGetters.Add(g.GetterName))
                throw new TypeException(FormatTypeError(
                    $"'{od.Name}' has two getters both named '{g.GetterName}'",
                    null, od.Line,
                    $"define duplicate getter '{g.GetterName}'",
                    "Each getter name must be unique. Rename one of them."));
            if (objType.Methods.Any(m => m.MethodName == g.GetterName))
                throw new TypeException(FormatTypeError(
                    $"'{od.Name}' getter '{g.GetterName}' clashes with a method of the same name",
                    null, od.Line,
                    $"define getter '{g.GetterName}' when a method of the same name exists",
                    $"Rename the getter or the method — getters and methods can't share a name."));
        }
        var seenSetters = new HashSet<string>();
        foreach (var s in objType.Setters)
        {
            if (!seenSetters.Add(s.SetterName))
                throw new TypeException(FormatTypeError(
                    $"'{od.Name}' has two setters both named '{s.SetterName}'",
                    null, od.Line,
                    $"define duplicate setter '{s.SetterName}'",
                    "Each setter name must be unique. Rename one of them."));
            if (objType.Methods.Any(m => m.MethodName == s.SetterName))
                throw new TypeException(FormatTypeError(
                    $"'{od.Name}' setter '{s.SetterName}' clashes with a method of the same name",
                    null, od.Line,
                    $"define setter '{s.SetterName}' when a method of the same name exists",
                    $"Rename the setter or the method — setters and methods can't share a name."));
        }
    }

    // Checks a method body — 'one' (self) bound to objType, parameters in scope, identical
    // whether the method is nested in the object's definition or declared via 'unto'.
    private void CheckMethodBody(BindStatement method, ObjectType objType, int selfLine)
    {
        var saved = SaveScopes();

        // Method scope: functions visible, plus 'one' (self) + parameters.
        foreach (var scope in saved.V)
            foreach (var (k, v) in scope.Where(kv => kv.Value.Type is FunctionType)) Scope[k] = v;
        Scope["one"] = new TypeInfo(objType, new VariableReference("one", 0), selfLine);
        foreach (var (type, name) in method.Parameters)
            Scope[name] = new TypeInfo(ResolveParamType(type), new VariableReference(name, 0), method.Line);

        var prevInFunction       = _inFunction;
        var prevReturnType       = _expectedReturnType;
        var prevFunctionLine     = _functionDeclarationLine;
        var prevRabbitDepth      = _rabbitDepth;
        _inFunction              = true;
        _expectedReturnType      = method.ReturnType;
        _functionDeclarationLine = method.Line;
        _rabbitDepth             = 0; // method bodies start outside any rabbit region

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
            _rabbitDepth             = prevRabbitDepth;
            RestoreScopes(saved);
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

    // Checks the body of a getter: zero-arg, must return the declared type on all paths.
    private void CheckGetterBody(GetterDeclaration getter, ObjectType objType, int selfLine)
    {
        var saved = SaveScopes();

        foreach (var scope in saved.V)
            foreach (var (k, v) in scope.Where(kv => kv.Value.Type is FunctionType)) Scope[k] = v;
        Scope["one"] = new TypeInfo(objType, new VariableReference("one", 0), selfLine);

        var prevInFunction       = _inFunction;
        var prevReturnType       = _expectedReturnType;
        var prevFunctionLine     = _functionDeclarationLine;
        var prevRabbitDepth      = _rabbitDepth;
        _inFunction              = true;
        _expectedReturnType      = getter.ReturnType;
        _functionDeclarationLine = getter.Line;
        _rabbitDepth             = 0;

        try
        {
            foreach (var stmt in getter.Body)
                CheckStatement(stmt);

            if (!DefinitelyReturns(getter.Body))
                throw new TypeException(FormatTypeError(
                    $"getter '{getter.Name}' is declared to give back a {FormatType(getter.ReturnType)}, but it can reach its end without returning one",
                    null, getter.Line,
                    "define a getter that might not return a value",
                    "Make sure every path through the getter ends with a return statement."));
        }
        finally
        {
            _inFunction              = prevInFunction;
            _expectedReturnType      = prevReturnType;
            _functionDeclarationLine = prevFunctionLine;
            _rabbitDepth             = prevRabbitDepth;
            RestoreScopes(saved);
        }
    }

    // Checks the body of a setter: one param (the incoming value), void return (infallible).
    private void CheckSetterBody(SetterDeclaration setter, ObjectType objType, int selfLine)
    {
        var saved = SaveScopes();

        foreach (var scope in saved.V)
            foreach (var (k, v) in scope.Where(kv => kv.Value.Type is FunctionType)) Scope[k] = v;
        Scope["one"] = new TypeInfo(objType, new VariableReference("one", 0), selfLine);
        Scope[setter.ParamName] = new TypeInfo(setter.ParamType, new VariableReference(setter.ParamName, 0), setter.Line);

        var prevInFunction       = _inFunction;
        var prevReturnType       = _expectedReturnType;
        var prevFunctionLine     = _functionDeclarationLine;
        var prevRabbitDepth      = _rabbitDepth;
        _inFunction              = true;
        _expectedReturnType      = null; // void — setters never return a value
        _functionDeclarationLine = setter.Line;
        _rabbitDepth             = 0;

        try
        {
            foreach (var stmt in setter.Body)
                CheckStatement(stmt);
        }
        finally
        {
            _inFunction              = prevInFunction;
            _expectedReturnType      = prevReturnType;
            _functionDeclarationLine = prevFunctionLine;
            _rabbitDepth             = prevRabbitDepth;
            RestoreScopes(saved);
        }
    }

    private void CheckUntoGetter(GetterDeclaration getter)
    {
        var objType = _objectDefs[getter.UntoType!];
        CheckGetterBody(getter, objType, getter.Line);
    }

    private void CheckUntoSetter(SetterDeclaration setter)
    {
        var objType = _objectDefs[setter.UntoType!];
        CheckSetterBody(setter, objType, setter.Line);
    }

    // Checks a destructor body: 'one' (self) bound to objType, no params, void/infallible.
    // Infallibility is enforced naturally: 'return a failure' in void context is already a TypeError;
    // unhandled fallible operations are already rejected outside _inTryBlock/_inFailureHandledContext.
    private void CheckUnmake(UnmakerDeclaration ud)
    {
        if (!_objectDefs.TryGetValue(ud.UnmakesTypeName, out var objType))
            throw new TypeException(FormatTypeError(
                $"'{ud.UnmakesTypeName}' is not a defined object type",
                null, ud.Line,
                $"declare a destructor for '{ud.UnmakesTypeName}'",
                $"Define 'object {ud.UnmakesTypeName}' before declaring a destructor for it."));

        var saved = SaveScopes();
        foreach (var scope in saved.V)
            foreach (var (k, v) in scope.Where(kv => kv.Value.Type is FunctionType)) Scope[k] = v;
        Scope["one"] = new TypeInfo(objType, new VariableReference("one", 0), ud.Line);

        var prevInFunction       = _inFunction;
        var prevReturnType       = _expectedReturnType;
        var prevFunctionLine     = _functionDeclarationLine;
        var prevRabbitDepth      = _rabbitDepth;
        _inFunction              = true;
        _expectedReturnType      = null; // void — 'return a failure' is caught as "returning a value from void"
        _functionDeclarationLine = ud.Line;
        _rabbitDepth             = 0;

        try { foreach (var stmt in ud.Body) CheckStatement(stmt); }
        finally
        {
            _inFunction              = prevInFunction;
            _expectedReturnType      = prevReturnType;
            _functionDeclarationLine = prevFunctionLine;
            _rabbitDepth             = prevRabbitDepth;
            RestoreScopes(saved);
        }
        // No DefinitelyReturns check — destructors are void, return is optional.
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

        if (targetType is BookType bt)
            return InferBookPossessiveAccess(poss, bt);

        if (targetType is not ObjectType ot)
            throw new TypeException(FormatTypeError(
                $"possessive access ('s) requires an object, but got a {FormatType(targetType)}",
                null, poss.Line,
                $"use 's on a {FormatType(targetType)}",
                "Only objects and books support the possessive 's syntax."));

        // Methods first, then getters (field-syntax), then fields.
        var methodSig = FindMethodInOtOrPromoted(ot, poss.Member);
        if (methodSig != null) return methodSig;

        var getterType = FindGetterInOtOrPromoted(ot, poss.Member);
        if (getterType != null) return getterType;

        var fieldType = FindFieldInOtOrPromoted(ot, poss.Member);
        if (fieldType != null) return fieldType;

        var allFields  = GetAllNamedFields(ot);
        var available  = string.Join(", ",
            allFields.Select(f => $"'{f.FieldName}'")
            .Concat(ot.Getters.Select(g => $"'{g.GetterName}' (getter)"))
            .Concat(ot.Methods.Select(m => $"'{m.MethodName}' (method)")));
        throw new TypeException(FormatTypeError(
            $"'{ot.Name}' has no field, getter, or method named '{poss.Member}'",
            null, poss.Line,
            $"access '{poss.Member}' on a {ot.Name}",
            available.Length > 0 ? $"Available: {available}." : $"'{ot.Name}' has no fields, getters, or methods."));
    }

    // ── Operator overloads (Pass2) ─────────────────────────────────────────────

    // Type-checks all OperatorOverloadDeclarations; populates _overloadReturnTypes.
    // Called after Pass1Hoist so all ObjectTypes are registered before body-checking.
    private void Pass2CheckOverloads(Program program)
    {
        var seen = new HashSet<(string, TokenType)>();
        foreach (var stmt in program.Statements)
        {
            if (stmt is not OperatorOverloadDeclaration oad) continue;

            if (!_objectDefs.TryGetValue(oad.OperandTypeName, out var objType))
                throw new TypeException(FormatTypeError(
                    $"'{oad.OperandTypeName}' is not a defined object type — operator overload has no type to register on",
                    null, oad.Line,
                    $"declare an overload for '{oad.OperandTypeName}'",
                    $"Define 'object {oad.OperandTypeName}' before declaring operator overloads for it, or check the spelling."));

            var key = (oad.OperandTypeName, oad.Operator);
            if (!seen.Add(key))
                throw new TypeException(FormatTypeError(
                    $"'{oad.OperandTypeName}' already has an overload for '{FormatOp(oad.Operator)}'",
                    null, oad.Line,
                    $"declare a second '{FormatOp(oad.Operator)}' overload for '{oad.OperandTypeName}'",
                    "Each operator can only be overloaded once per type. Remove the duplicate."));

            CheckOperatorOverload(oad, objType);
        }
    }

    private void CheckOperatorOverload(OperatorOverloadDeclaration oad, ObjectType objType)
    {
        bool isFallible = HasDirectFailureReturn(oad.Body);

        var saved = SaveScopes();
        foreach (var scope in saved.V)
            foreach (var (k, v) in scope.Where(kv => kv.Value.Type is FunctionType)) Scope[k] = v;
        Scope[oad.LeftName]  = new TypeInfo(objType, new VariableReference(oad.LeftName, 0), oad.Line);
        Scope[oad.RightName] = new TypeInfo(objType, new VariableReference(oad.RightName, 0), oad.Line);

        var prevInFunction       = _inFunction;
        var prevReturnType       = _expectedReturnType;
        var prevFunctionLine     = _functionDeclarationLine;
        var prevRabbitDepth      = _rabbitDepth;
        var prevInferring        = _inferringLambdaReturn;
        var prevOvFallible       = _overloadBodyIsFallible;

        _inFunction              = true;
        _expectedReturnType      = null;
        _functionDeclarationLine = oad.Line;
        _rabbitDepth             = 0;
        _inferringLambdaReturn   = true;
        _overloadBodyIsFallible  = isFallible;

        try
        {
            foreach (var stmt in oad.Body)
                CheckStatement(stmt);

            if (!DefinitelyReturns(oad.Body))
                throw new TypeException(FormatTypeError(
                    $"'{FormatOp(oad.Operator)}' overload for '{oad.OperandTypeName}' can reach its end without returning a value",
                    null, oad.Line,
                    "define an operator overload that might not return a value",
                    "Make sure every path through the overload ends with a return statement."));
        }
        finally
        {
            var inferredReturn       = _expectedReturnType;
            _inFunction              = prevInFunction;
            _expectedReturnType      = prevReturnType;
            _functionDeclarationLine = prevFunctionLine;
            _rabbitDepth             = prevRabbitDepth;
            _inferringLambdaReturn   = prevInferring;
            _overloadBodyIsFallible  = prevOvFallible;
            RestoreScopes(saved);

            if (inferredReturn != null)
                _overloadReturnTypes[(oad.OperandTypeName, oad.Operator)] = inferredReturn;
        }
    }

    // Walks a statement list looking for any direct 'return a failure' or 'or pass the
    // failure off' statement. Does NOT recurse into nested Bind/lambda bodies (those are
    // separate function scopes). Used to pre-detect overload fallibility before body-check.
    private static bool HasDirectFailureReturn(IReadOnlyList<IStatement> stmts)
    {
        foreach (var s in stmts)
        {
            if (s is ReturnStatement rs2 && IsFailureExpr(rs2.Value)) return true;

            List<IReadOnlyList<IStatement>>? children = s switch
            {
                WhileStatement ws       => [ws.Body],
                RepeatUntilStatement ru => [ru.Body],
                ForEachStatement fe     => [fe.Body],
                WithOpenStatement wo    => [wo.Body],
                WithRabbitStatement wr  => [wr.Body],
                _                       => null
            };

            if (children == null)
            {
                if (s is IfStatement ifs)
                {
                    children = [..ifs.Arms.Select(a => a.Body)];
                    if (ifs.ElseBody != null) children.Add(ifs.ElseBody);
                }
                else if (s is TryStatement ts)
                {
                    children = [ts.Body];
                    if (ts.FailureHandler   != null) children.Add(ts.FailureHandler);
                    if (ts.ExceptionHandler != null) children.Add(ts.ExceptionHandler);
                }
            }

            if (children != null)
                foreach (var child in children)
                    if (HasDirectFailureReturn(child)) return true;
        }
        return false;
    }

    // True for any expression that represents a failure return:
    //   'return a failure "msg"'  → FailureLiteral
    //   'return a failure.'       → VariableReference("the failure") (no message string)
    //   'or pass the failure off' → FailurePropagate
    private static bool IsFailureExpr(IExpression? expr) => expr switch
    {
        FailureLiteral or FailurePropagate                          => true,
        VariableReference { Name: "the failure" }                   => true,
        _                                                           => false
    };
}
