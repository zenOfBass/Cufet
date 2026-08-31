using Cufet.Lexer;

namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    private void CheckPossessiveSet(PossessiveSetStatement stmt)
    {
        var targetType = InferType(stmt.Target);
        if (targetType == null) return;
        if (targetType is not ObjectType ot)
            throw TypeError(
                $"possessive assignment requires an object, but got a {FormatType(targetType)}",
                null, stmt.Line, stmt.Column,
                $"set '{stmt.Member}' on a {FormatType(targetType)}",
                "Only objects support possessive field assignment (alice's field becomes X).");

        // ★ Checked BEFORE the setter branch, deliberately. A setter is infallible and
        // transform-only, so one guarding a permanent field could not reject the write — only
        // quietly ignore it, which is worse than no protection. Routing through a setter must
        // therefore not become a way around `permanently`.
        //
        // This covers the write wherever it is written from: `alice's id becomes …` outside the
        // type, and `one's id becomes …` inside one of its own methods. Construction is
        // untouched — the field is set by the `a new …` literal, which is not a write.
        if (IsPermanentInOtOrPromoted(ot, stmt.Member))
            throw TypeError(
                $"'{stmt.Member}' is permanent — it is set when the {ot.Name} is made, and never changes after",
                null, stmt.Line, stmt.Column,
                $"change '{stmt.Member}' after the {ot.Name} was made",
                $"Give '{stmt.Member}' its value in the 'a new {ot.Name}' literal. If it has to change later, " +
                $"declare the field without 'permanently'.");

        // Setter intercepts the write if one is defined; setter param type is the expected type.
        var setterSig = FindSetterInOtOrPromoted(ot, stmt.Member);
        if (setterSig != null)
        {
            var valueType = InferType(stmt.Value);
            if (valueType != null && !IsAssignable(setterSig.Value.ParamType, valueType))
                throw TypeError(
                    $"setter for '{stmt.Member}' expects a {FormatType(setterSig.Value.ParamType)}, not a {FormatType(valueType)}",
                    null, stmt.Line, stmt.Column,
                    $"set '{stmt.Member}' to a {FormatType(valueType)}",
                    $"The setter for '{stmt.Member}' accepts a {FormatType(setterSig.Value.ParamType)}.");
            CheckRegionStore(stmt.Value, InferType(stmt.Value), ContainerDepthOf(stmt.Target), stmt.Line, stmt.Column,
                $"set '{stmt.Member}' to a value from a shorter-lived rabbit region than the object");
            stmt.EscapeToDepth = EscapeDepthFor(stmt.Value, InferType(stmt.Value), ContainerDepthOf(stmt.Target));
            return;
        }

        // No setter — normal field write check.
        CheckObjectNamedSet(ot, stmt.Member, stmt.Value, stmt.Line, stmt.Column);
        // Region invariant: the value being stored cannot outlive the object's rabbit region.
        var valType = InferType(stmt.Value);
        CheckRegionStore(stmt.Value, valType, ContainerDepthOf(stmt.Target), stmt.Line, stmt.Column,
            $"set '{stmt.Member}' to a value from a shorter-lived rabbit region than the object");
        stmt.EscapeToDepth = EscapeDepthFor(stmt.Value, valType, ContainerDepthOf(stmt.Target));
    }

    private void CheckObjectNamedSet(ObjectType ot, string fieldName, IExpression value, int line, int col)
    {
        // Also guarded here, not only in CheckPossessiveSet: this is the shared field-write check
        // and has other callers, and a permanent field must be refused down every route into it.
        if (IsPermanentInOtOrPromoted(ot, fieldName))
            throw TypeError(
                $"'{fieldName}' is permanent — it is set when the {ot.Name} is made, and never changes after",
                null, line, col,
                $"change '{fieldName}' after the {ot.Name} was made",
                $"Give '{fieldName}' its value in the 'a new {ot.Name}' literal. If it has to change later, " +
                $"declare the field without 'permanently'.");

        // Field lookup includes promoted fields from embedded types.
        var fieldType = FindFieldInOtOrPromoted(ot, fieldName);
        if (fieldType == null)
        {
            var allFields = GetAllNamedFields(ot);
            var hint = allFields.Count > 0
                ? $"Available named fields: {string.Join(", ", allFields.Select(f => f.FieldName))}."
                : $"Object '{ot.Name}' has no named fields.";
            throw TypeError(
                $"object '{ot.Name}' has no field named '{fieldName}'",
                null, line, col,
                $"set field '{fieldName}'",
                hint);
        }
        // Embed handles (ObjectType) can't be set via becomes; only scalar/value types are settable.
        if (fieldType is ObjectType)
            throw TypeError(
                $"'{fieldName}' is an embedded object handle — you can't replace the whole embedded object",
                null, line, col,
                $"set the embed handle '{fieldName}'",
                $"Mutate individual fields of the embedded object instead.");
        var valueType = InferType(value);
        if (valueType != null && !IsAssignable(fieldType, valueType))
            throw TypeError(
                $"field '{fieldName}' holds a {FormatType(fieldType)}, not a {FormatType(valueType)}",
                null, line, col,
                $"set field '{fieldName}' to a {FormatType(valueType)}",
                $"Field '{fieldName}' has type {FormatType(fieldType)}.");
    }

    // ── Embedding helpers (Slice 4) ───────────────────────────────────────────

    // Finds a named field type in ot, including the embed handle and promoted fields.
    // own fields take priority; then the embed handle (fieldName == EmbeddedTypeName);
    // then promoted fields recursively through the embed chain.
    // Returns null if not found (collision detection happens at definition time).

    // Is `fieldName` permanent on `ot` or anywhere up its embed chain?
    //
    // ★ The chain matters. A field promoted from an embedded type is written through the OUTER
    // object — `the admin's id becomes …` where `id` belongs to the embedded `user` — and the
    // outer type's own PermanentFields says nothing about it. Checking only the outer set would
    // leave embedding as a way to launder a permanent field into a mutable one.
    private bool IsPermanentInOtOrPromoted(ObjectType ot, string fieldName)
    {
        if (ot.PermanentFields.Contains(fieldName)) return true;
        return ot.EmbeddedTypeName != null
            && _objectDefs.TryGetValue(ot.EmbeddedTypeName, out var embed)
            && IsPermanentInOtOrPromoted(embed, fieldName);
    }

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
        // ⚠ Through _objectDefs, not the instance handed in. An ObjectType is nominal and its
        // members can GROW after a binding captured it — filling a generic method adds one — so a
        // value bound by `Pull` or a `Define` holds a snapshot from before the filling existed.
        // The table is the type; the captured instance is only a copy of how it once looked.
        var canonical = _objectDefs.TryGetValue(ot.Name, out var current) ? current : ot;

        var own = canonical.Methods.FirstOrDefault(m => m.MethodName == methodName);
        if (own != default) return own.Signature;

        if (canonical.EmbeddedTypeName != null
            && _objectDefs.TryGetValue(canonical.EmbeddedTypeName, out var embed))
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

    // Finds the ObjectType in ot's embed chain that directly owns the getter (not promoted).
    // Returns null if name is not a getter on any type in the chain.
    private ObjectType? FindGetterOwnerOt(ObjectType ot, string name)
    {
        if (ot.Getters.Any(g => g.GetterName == name)) return ot;
        if (ot.EmbeddedTypeName != null && _objectDefs.TryGetValue(ot.EmbeddedTypeName, out var embed))
            return FindGetterOwnerOt(embed, name);
        return null;
    }

    // Computes the concrete rabbit depth for a getter or field access on an object.
    // For stored fields: depth = receiver depth (fields live inside the object).
    // For getters: uses the getter's ReturnDepthSignature — if ReceiverDepthIndex is present,
    //   depth = receiver depth; otherwise depth = 0 (fresh alloc). Falls back to receiver
    //   depth when the sig is not yet available (getter body not yet analyzed).
    private int ComputeMemberAccessDepth(ObjectType ot, string memberName, IExpression targetExpr)
    {
        var receiverDepth = ValueDepthOf(targetExpr, InferType(targetExpr));
        var ownerOt = FindGetterOwnerOt(ot, memberName);
        if (ownerOt != null
            && _getterDepthSigs.TryGetValue(ownerOt.Name, out var gDict)
            && gDict.TryGetValue(memberName, out var gSig))
        {
            return gSig.Any(i => i == ReceiverDepthIndex) ? receiverDepth : 0;
        }
        // No sig (stored field, or getter not yet analyzed): conservative = receiver depth.
        return receiverDepth;
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
            throw TypeError(
                $"object '{od.Name}' embeds '{od.EmbeddedTypeName}', but no such object type is defined",
                null, od.Line, od.Column,
                $"embed '{od.EmbeddedTypeName}' in '{od.Name}'",
                $"Define object {od.EmbeddedTypeName} with (...). before defining {od.Name}.");

        var promotedFields  = GetAllPromotedFieldNames(embedType);
        var promotedMethods = GetAllPromotedMethodNames(embedType);
        var promotedGetters = GetAllPromotedGetterNames(embedType);
        var promotedSetters = GetAllPromotedSetterNames(embedType);

        foreach (var f in objType.NamedFields)
        {
            if (promotedFields.Contains(f.FieldName))
                throw TypeError(
                    $"'{od.Name}' has its own field '{f.FieldName}' which collides with a promoted field from '{od.EmbeddedTypeName}'",
                    null, od.Line, od.Column,
                    $"define '{f.FieldName}' in '{od.Name}' while embedding '{od.EmbeddedTypeName}'",
                    $"Rename one of the fields. To access the embedded field explicitly, use 'the {f.FieldName} of the {od.EmbeddedTypeName} of ...'.");
        }
        foreach (var m in objType.Methods)
        {
            if (promotedMethods.Contains(m.MethodName))
                throw TypeError(
                    $"'{od.Name}' has its own method '{m.MethodName}' which collides with a promoted method from '{od.EmbeddedTypeName}'",
                    null, od.Line, od.Column,
                    $"define '{m.MethodName}' in '{od.Name}' while embedding '{od.EmbeddedTypeName}'",
                    $"Rename one of the methods. To call the embedded method explicitly, use 'Cast {m.MethodName} on the {od.EmbeddedTypeName} of ...'.");
        }
        foreach (var g in objType.Getters)
        {
            if (promotedGetters.Contains(g.GetterName))
                throw TypeError(
                    $"'{od.Name}' has its own getter '{g.GetterName}' which collides with a promoted getter from '{od.EmbeddedTypeName}'",
                    null, od.Line, od.Column,
                    $"define getter '{g.GetterName}' in '{od.Name}' while embedding '{od.EmbeddedTypeName}'",
                    $"Rename one of the getters.");
            if (promotedMethods.Contains(g.GetterName))
                throw TypeError(
                    $"'{od.Name}' getter '{g.GetterName}' collides with a promoted method of the same name from '{od.EmbeddedTypeName}'",
                    null, od.Line, od.Column,
                    $"define getter '{g.GetterName}' while embedding a type with a method of the same name",
                    $"Rename the getter or the method.");
        }
        foreach (var s in objType.Setters)
        {
            if (promotedSetters.Contains(s.SetterName))
                throw TypeError(
                    $"'{od.Name}' has its own setter '{s.SetterName}' which collides with a promoted setter from '{od.EmbeddedTypeName}'",
                    null, od.Line, od.Column,
                    $"define setter '{s.SetterName}' in '{od.Name}' while embedding '{od.EmbeddedTypeName}'",
                    $"Rename one of the setters.");
            if (promotedMethods.Contains(s.SetterName))
                throw TypeError(
                    $"'{od.Name}' setter '{s.SetterName}' collides with a promoted method of the same name from '{od.EmbeddedTypeName}'",
                    null, od.Line, od.Column,
                    $"define setter '{s.SetterName}' while embedding a type with a method of the same name",
                    $"Rename the setter or the method.");
        }
    }

    // Validates all conformance declarations for an object: the interface must exist and the
    // object must implement every method with a matching signature (return type + param types).
    private void ValidateObjectConformance(ObjectDefinition od, ObjectType objType)
    {
        foreach (var ifaceName in od.ConformedInterfaces)
        {
            if (!_interfaceDefs.TryGetValue(ifaceName, out var iface))
                throw TypeError(
                    $"'{od.Name}' claims to satisfy '{ifaceName}', but no such interface is defined",
                    null, od.Line, od.Column,
                    $"conform to '{ifaceName}'",
                    $"Define the interface first: Define {ifaceName} as an interface for {{...}}.");

            foreach (var (methodName, returnType, paramTypes) in iface.Methods)
            {
                var sig = FindMethodInOtOrPromoted(objType, methodName);
                if (sig == null)
                    throw TypeError(
                        $"'{od.Name}' claims to satisfy '{ifaceName}' but has no method '{methodName}'",
                        null, od.Line, od.Column,
                        $"conform to interface '{ifaceName}'",
                        $"Add a method '{methodName}' to '{od.Name}' (or embed an object that provides it).");

                if (sig.ReturnType != returnType || !sig.ParameterTypes.SequenceEqual(paramTypes))
                    throw TypeError(
                        $"'{od.Name}'.'{methodName}' has the wrong signature for interface '{ifaceName}'",
                        null, od.Line, od.Column,
                        $"conform to '{ifaceName}' with a mismatched '{methodName}'",
                        $"Interface '{ifaceName}' requires '{methodName}' to have signature: " +
                        $"{FormatFunctionType(new FunctionType(paramTypes, returnType))}.");
            }
        }
    }

    private void CheckObjectDefinition(ObjectDefinition od)
    {
        if (!_objectDefs.TryGetValue(od.Name, out var objType)) return;

        // ⚠⚠ A SUPERSEDED definition is not checked. Redefinition is allowed and the last one wins,
        // so an earlier definition of the same name is dead — nothing dispatches to it, and its
        // methods are never emitted. Checking it anyway meant checking it against the WINNER's
        // fields, which produced an error on a line the writer got right.
        //
        // ★ The linter reports the shadowing, in the same voice it uses for a nested bare `it`:
        // well defined, allowed, and worth saying out loud because the reader cannot see it.
        if (_winningDefinition.TryGetValue(od.Name, out var winner) && !ReferenceEquals(winner, od))
            return;

        // ★ A book's Cufet layer checks with the book's own INTRODUCED TYPES in scope —
        // `transpose`'s body constructs a matrix, and `matrix` is otherwise only in scope inside
        // a pull. This is scoped to the book's own source, not a general loosening: only the
        // prelude can define an object under a book's name (Pass1Hoist refuses a writer's).
        bool isBookLayer = BuiltinBooks.TryGetValue(od.Name, out var layerBook);
        bool prevLayer   = _checkingBookLayer;
        // Names this module's bodies reach for are collected while they are checked — see
        // NoteUnresolvedName. Only for a module, because only a module is later PULLED somewhere
        // that can be told what is missing.
        var prevModule   = _checkingModuleName;
        _checkingModuleName = od.ConformedInterfaces.Contains(ModuleInterface) ? od.Name : null;
        if (isBookLayer)
        {
            EnterScope();
            _checkingBookLayer = true;
            foreach (var (typeName, typeObj) in layerBook!.IntroducedTypes)
                RegisterScopedType(typeName.ToLowerInvariant(), typeObj);
        }
        try
        {
            ValidateObjectEmbedding(od, objType);
            ValidateObjectConformance(od, objType);
            ValidateGetterSetterNames(od, objType);

            foreach (var method in od.Methods)
            {
                // A method that left a blank cannot have its body checked — its signature names types
                // that do not exist yet. Each FILLING is checked instead, as the ordinary method it
                // becomes, so one nothing calls is never checked at all.
                if (_genericMethods.ContainsKey((od.Name, method.Name))) continue;
                CheckMethodBody(method, objType, od.Line);
            }
            foreach (var getter in od.Getters)
                CheckGetterBody(getter, objType, od.Line);
            foreach (var setter in od.Setters)
                CheckSetterBody(setter, objType, od.Line);
        }
        finally
        {
            _checkingBookLayer  = prevLayer;
            _checkingModuleName = prevModule;
            if (isBookLayer) ExitScope();
        }
    }

    // Validates own-type getter/setter name uniqueness and no clashes with methods.
    // (Getter + setter of the same name = valid pair. Getter/setter vs. field = valid backing-field pattern.)
    private void ValidateGetterSetterNames(ObjectDefinition od, ObjectType objType)
    {
        var seenGetters = new HashSet<string>();
        foreach (var g in objType.Getters)
        {
            if (!seenGetters.Add(g.GetterName))
                throw TypeError(
                    $"'{od.Name}' has two getters both named '{g.GetterName}'",
                    null, od.Line, od.Column,
                    $"define duplicate getter '{g.GetterName}'",
                    "Each getter name must be unique. Rename one of them.");
            if (objType.Methods.Any(m => m.MethodName == g.GetterName))
                throw TypeError(
                    $"'{od.Name}' getter '{g.GetterName}' clashes with a method of the same name",
                    null, od.Line, od.Column,
                    $"define getter '{g.GetterName}' when a method of the same name exists",
                    $"Rename the getter or the method — getters and methods can't share a name.");
        }
        var seenSetters = new HashSet<string>();
        foreach (var s in objType.Setters)
        {
            if (!seenSetters.Add(s.SetterName))
                throw TypeError(
                    $"'{od.Name}' has two setters both named '{s.SetterName}'",
                    null, od.Line, od.Column,
                    $"define duplicate setter '{s.SetterName}'",
                    "Each setter name must be unique. Rename one of them.");
            if (objType.Methods.Any(m => m.MethodName == s.SetterName))
                throw TypeError(
                    $"'{od.Name}' setter '{s.SetterName}' clashes with a method of the same name",
                    null, od.Line, od.Column,
                    $"define setter '{s.SetterName}' when a method of the same name exists",
                    $"Rename the setter or the method — setters and methods can't share a name.");
        }
    }

    // Checks a method body — 'one' (self) bound to objType, parameters in scope, identical
    // whether the method is nested in the object's definition or declared via 'unto'.
    private void CheckMethodBody(BindStatement method, ObjectType objType, int selfLine)
    {
        // ★ A FILLED generic method refuses because of the call that filled it, not because its
        // body is wrong — the body is right for every other filling. Re-anchored there, with the
        // body's own explanation kept underneath. Same treatment CheckBind gives a free function.
        if (_instantiationOrigin.TryGetValue(FilledMethodKey(objType.Name, method.Name), out var origin))
        {
            try { CheckMethodBodyCore(method, objType, selfLine); }
            catch (TypeException inner) { throw FilledBodyRefused(origin, inner); }
            return;
        }
        CheckMethodBodyCore(method, objType, selfLine);
    }

    private void CheckMethodBodyCore(BindStatement method, ObjectType objType, int selfLine)
    {
        var saved = SaveScopes();

        // Method scope: functions and top-level constants visible, plus 'one' (self) + parameters.
        //
        // ★ EXCEPT in a bundled book's Cufet layer, which imports nothing from the writer's top
        // level. The prelude is prepended to the program, so without this a book's method body
        // sees the writer's own functions — and a local in the book then COLLIDES with any name
        // they happened to use. A program declaring `Bind number to total` broke `log`, whose
        // running sum is called `total`. That is not a name clash to dodge by renaming: the book
        // was written without sight of the program, so nothing in the program should reach it.
        if (!_checkingBookLayer) ImportTopLevelVisible(saved);
        Scope["one"] = new TypeInfo(objType, new VariableReference("one", 0, 0), selfLine, IsParameter: true);
        foreach (var (type, name) in method.Parameters)
            Scope[name] = new TypeInfo(ResolveParamType(type), new VariableReference(name, 0, 0), method.Line, IsParameter: true);

        bool buries = _buryingMethods.Contains((objType.Name, method.Name));

        var prevInFunction       = _inFunction;
        var prevReturnType       = _expectedReturnType;
        var prevFunctionLine     = _functionDeclarationLine;
        var prevRabbitDepth      = _rabbitDepth;
        var prevHidden           = _hiddenTopLevelData;
        var prevRecordingStash   = _recordingStashFn;
        // Only this body's own locals belong to this body's machine — the same rule CheckBind
        // applies, under the qualified key a method needs.
        _recordingStashFn        = buries ? StashMethodKey(objType.Name, method.Name) : null;
        _inFunction              = true;
        _expectedReturnType      = method.ReturnType;
        _functionDeclarationLine = method.Line;
        _rabbitDepth             = 0; // method bodies start outside any rabbit region

        try
        {
            CheckBlock(method.Body);

            // Compute and store the method's return-depth signature so call sites can
            // propagate rabbit depth through method calls instead of treating every return as depth-0.
            var effectiveRetType = method.ReturnType is FailureType frt0 ? frt0.Inner : method.ReturnType;
            if (IsReferenceType(effectiveRetType))
            {
                var methodFt = FindMethodInOtOrPromoted(objType, method.Name);
                if (methodFt != null)
                    methodFt.ReturnDepthSignature = ComputeReturnDepthSignature(method, includeReceiver: true);
            }

            // ★ A burying method is exempt for the same reason a burying function is: reaching its
            // end is how it finishes, and the caller holds a stash that reports that with void.
            // Without this exemption the refusal blamed a missing `Return` in a method that was
            // never going to return one — the identical wrong message nested functions used to get.
            if (method.ReturnType != null && !buries && !DefinitelyReturns(method.Body))
                throw TypeError(
                    $"method '{method.Name}' is declared to give back a {FormatType(method.ReturnType)}, but it can reach its end without returning one",
                    null, method.Line, method.Column,
                    "define a method that might not return a value",
                    "Make sure every path through the method ends with a return statement.");
        }
        finally
        {
            _inFunction              = prevInFunction;
            _expectedReturnType      = prevReturnType;
            _functionDeclarationLine = prevFunctionLine;
            _rabbitDepth             = prevRabbitDepth;
            _hiddenTopLevelData      = prevHidden;
            _recordingStashFn        = prevRecordingStash;
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

        ImportTopLevelVisible(saved);
        Scope["one"] = new TypeInfo(objType, new VariableReference("one", 0, 0), selfLine, IsParameter: true);

        var prevInFunction       = _inFunction;
        var prevReturnType       = _expectedReturnType;
        var prevFunctionLine     = _functionDeclarationLine;
        var prevRabbitDepth      = _rabbitDepth;
        var prevHidden           = _hiddenTopLevelData;
        _inFunction              = true;
        _expectedReturnType      = getter.ReturnType;
        _functionDeclarationLine = getter.Line;
        _rabbitDepth             = 0;

        try
        {
            CheckBlock(getter.Body);

            // Compute and store the getter's return-depth signature.
            if (IsReferenceType(getter.ReturnType))
            {
                if (!_getterDepthSigs.TryGetValue(objType.Name, out var dict))
                    _getterDepthSigs[objType.Name] = dict = new Dictionary<string, IReadOnlyList<int>>();
                dict[getter.Name] = ComputeGetterReturnDepthSignature(getter);
            }

            if (!DefinitelyReturns(getter.Body))
                throw TypeError(
                    $"getter '{getter.Name}' is declared to give back a {FormatType(getter.ReturnType)}, but it can reach its end without returning one",
                    null, getter.Line, getter.Column,
                    "define a getter that might not return a value",
                    "Make sure every path through the getter ends with a return statement.");
        }
        finally
        {
            _inFunction              = prevInFunction;
            _expectedReturnType      = prevReturnType;
            _functionDeclarationLine = prevFunctionLine;
            _rabbitDepth             = prevRabbitDepth;
            _hiddenTopLevelData      = prevHidden;
            RestoreScopes(saved);
        }
    }

    // Checks the body of a setter: one param (the incoming value), void return (infallible).
    private void CheckSetterBody(SetterDeclaration setter, ObjectType objType, int selfLine)
    {
        var saved = SaveScopes();

        ImportTopLevelVisible(saved);
        Scope["one"] = new TypeInfo(objType, new VariableReference("one", 0, 0), selfLine, IsParameter: true);
        Scope[setter.ParamName] = new TypeInfo(setter.ParamType, new VariableReference(setter.ParamName, 0, 0), setter.Line, IsParameter: true);

        var prevInFunction       = _inFunction;
        var prevReturnType       = _expectedReturnType;
        var prevFunctionLine     = _functionDeclarationLine;
        var prevRabbitDepth      = _rabbitDepth;
        var prevHidden           = _hiddenTopLevelData;
        _inFunction              = true;
        _expectedReturnType      = null; // void — setters never return a value
        _functionDeclarationLine = setter.Line;
        _rabbitDepth             = 0;

        try
        {
            CheckBlock(setter.Body);
        }
        finally
        {
            _inFunction              = prevInFunction;
            _expectedReturnType      = prevReturnType;
            _functionDeclarationLine = prevFunctionLine;
            _rabbitDepth             = prevRabbitDepth;
            _hiddenTopLevelData      = prevHidden;
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
            throw TypeError(
                $"'{ud.UnmakesTypeName}' is not a defined object type",
                null, ud.Line, ud.Column,
                $"declare a destructor for '{ud.UnmakesTypeName}'",
                $"Define 'object {ud.UnmakesTypeName}' before declaring a destructor for it.");

        var saved = SaveScopes();
        ImportTopLevelVisible(saved);
        Scope["one"] = new TypeInfo(objType, new VariableReference("one", 0, 0), ud.Line, IsParameter: true);

        var prevInFunction       = _inFunction;
        var prevReturnType       = _expectedReturnType;
        var prevFunctionLine     = _functionDeclarationLine;
        var prevRabbitDepth      = _rabbitDepth;
        var prevHidden           = _hiddenTopLevelData;
        _inFunction              = true;
        _expectedReturnType      = null; // void — 'return a failure' is caught as "returning a value from void"
        _functionDeclarationLine = ud.Line;
        _rabbitDepth             = 0;

        try { CheckBlock(ud.Body); }
        finally
        {
            _inFunction              = prevInFunction;
            _expectedReturnType      = prevReturnType;
            _functionDeclarationLine = prevFunctionLine;
            _rabbitDepth             = prevRabbitDepth;
            _hiddenTopLevelData      = prevHidden;
            RestoreScopes(saved);
        }
        // No DefinitelyReturns check — destructors are void, return is optional.
    }

    private ObjectType InferObjectLiteral(ObjectLiteral lit)
    {
        // ★ Pull is the ONLY constructor for a bundled book. Its Cufet layer is a registered
        // object type (that is what the merge rides on), so without this guard `a new
        // collections { }` would quietly build a layer instance with no pull — no scope, no
        // `Done.`, none of what pulling means. Same family as the rabbit's rule: a book is a
        // scope-thing, and its construction is the bracket.
        if (IsBundledModuleName(lit.TypeName))
            throw TypeError(
                $"'{lit.TypeName}' comes with the language — 'Pull' is how you get one",
                lit.TypeName.Equals(RabbitModuleName, StringComparison.OrdinalIgnoreCase)
                    ? "Pulling a rabbit is what opens its region, so one built this way would stand on no ground"
                    : null,
                lit.Line, lit.Column,
                $"construct '{lit.TypeName}' with 'a new'",
                $"Write 'Pull {lit.TypeName}.' (or 'Pull {lit.TypeName} as <name>.') and use it inside that block.");

        // `a new stack of number { … }` — fill the blanks first, so everything below reads a
        // concrete definition and knows nothing about templates. The resolved name is recorded on
        // the node because both backends look their definition up BY NAME, and the template's own
        // name names no type.
        ObjectType objType;
        if (lit.TypeArguments is { Count: > 0 } filling)
        {
            // ★ The one place that knows WHERE a filling was asked for. Instantiate runs under this
            // call, several frames down inside type resolution, and reads it back — so an error in
            // a filled method's body can be reported here instead of inside the template.
            var previousSite = _fillingSite;
            _fillingSite = (lit.Line, lit.Column);
            try
            {
                objType = (ObjectType)ResolveParamType(
                    new ObjectType(lit.TypeName, [], [], [], typeArguments: filling));
            }
            finally { _fillingSite = previousSite; }
            lit.ResolvedTypeName = objType.Name;
        }
        // ★ A type a pulled module CARRIES is in scope by its short name for the length of the
        // block, and is not in `_objectDefs` under that name — it was lifted to one with a space in
        // it. Annotations already reached scoped types through ResolveParamType; this is the
        // literal, which went straight to the definitions and so could not see one.
        else if (!_objectDefs.TryGetValue(lit.TypeName, out objType!)
                 && TryLookupScopedType(lit.TypeName, out var scoped) && scoped is ObjectType scopedObject)
        {
            objType = scopedObject;
            lit.ResolvedTypeName = objType.Name;
        }
        else if (objType is null)
            throw _genericObjectDefs.TryGetValue(lit.TypeName, out var template)
                // ⚠ It IS defined — it just names nothing on its own. Saying "not a defined object
                // type" here sends the reader off to define something they already have.
                ? TypeError(
                    $"'{lit.TypeName}' needs its blank filled in",
                    $"'{lit.TypeName}' is written 'object {lit.TypeName} " +
                    string.Join(" ", template.TypeParameters!.Select(b => "of " + b)) + "'",
                    lit.Line, lit.Column,
                    $"create a new {lit.TypeName} without saying what it holds",
                    $"Say what fills it: 'a new {lit.TypeName} " +
                    string.Join(" ", template.TypeParameters!.Select(_ => "of <type>")) + " { ... }'.")
                : TypeError(
                    $"'{lit.TypeName}' is not a defined object type",
                    null, lit.Line, lit.Column,
                    $"create a new {lit.TypeName} object",
                    $"Define the object type first: Define object {lit.TypeName} with (...).");

        // Flat construction: positionals = own + embedded (all levels), in order.
        var allPositionals = GetAllPositionalTypes(objType);
        if (lit.PositionalValues.Count != allPositionals.Count)
            throw TypeError(
                $"'{lit.TypeName}' expects {allPositionals.Count} positional field(s) (including promoted), but you provided {lit.PositionalValues.Count}",
                null, lit.Line, lit.Column,
                $"provide {lit.PositionalValues.Count} positional field(s)",
                $"'{lit.TypeName}' requires exactly {allPositionals.Count} positional field(s).");

        for (int i = 0; i < lit.PositionalValues.Count; i++)
        {
            var valType = InferType(lit.PositionalValues[i]);
            if (valType != null && !IsAssignable(allPositionals[i], valType))
                throw TypeError(
                    $"positional field {i + 1} of '{lit.TypeName}' must be a {FormatType(allPositionals[i])}",
                    null, lit.Line, lit.Column,
                    $"provide a {FormatType(valType)} for positional field {i + 1}",
                    $"Change the value to a {FormatType(allPositionals[i])}.");
        }

        // Flat construction: named fields = own + embedded (all levels).
        var allNamedFields = GetAllNamedFields(objType);

        // Check all required named fields are present.
        foreach (var (requiredName, _) in allNamedFields)
        {
            if (!lit.NamedValues.Any(nv => nv.Name == requiredName))
                throw TypeError(
                    $"field '{requiredName}' of '{lit.TypeName}' is missing",
                    null, lit.Line, lit.Column,
                    $"create a {lit.TypeName} without field '{requiredName}'",
                    $"Add 'the {requiredName} <value>' to the object literal.");
        }

        // Check provided fields are valid (exist somewhere in the chain) and correctly typed.
        foreach (var (name, expr) in lit.NamedValues)
        {
            var fieldType = FindFieldInOtOrPromoted(objType, name);
            if (fieldType == null)
                throw TypeError(
                    $"'{lit.TypeName}' has no field named '{name}'",
                    null, lit.Line, lit.Column,
                    $"set unknown field '{name}'",
                    allNamedFields.Count > 0
                        ? $"Available named fields: {string.Join(", ", allNamedFields.Select(f => $"'{f.FieldName}'"))}."
                        : $"'{lit.TypeName}' has no named fields.");
            var valType = InferType(expr);
            if (valType != null && !IsAssignable(fieldType, valType))
                throw TypeError(
                    $"field '{name}' of '{lit.TypeName}' must be a {FormatType(fieldType)}",
                    null, lit.Line, lit.Column,
                    $"provide a {FormatType(valType)} for field '{name}'",
                    $"Change the value to a {FormatType(fieldType)}.");
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
                throw TypeError(
                    $"interface '{ifaceT.Name}' has no method named '{poss.Member}'",
                    null, poss.Line, poss.Column,
                    $"use 's to access '{poss.Member}' through interface '{ifaceT.Name}'",
                    ifaceDef.Methods.Count > 0
                        ? $"Available methods: {string.Join(", ", ifaceDef.Methods.Select(m => $"'{m.MethodName}'"))}."
                        : $"Interface '{ifaceT.Name}' declares no methods.");
            return new FunctionType(ifaceSig.ParamTypes, ifaceSig.ReturnType);
        }

        if (targetType is BookType bt)
        {
            // ★ The book's Cufet layer wins member by member: a member the prelude-defined module
            // object offers is ordinary object access through the same pulled name, and only the
            // rest falls to the native book. (Slice 1 of the 0.16.0 arc — see ROADMAP.)
            if (MemberOwnerType(bt) is ObjectType cufetLayer
                && (FindMethodInOtOrPromoted(cufetLayer, poss.Member) != null
                    || FindGetterInOtOrPromoted(cufetLayer, poss.Member) != null
                    || FindFieldInOtOrPromoted(cufetLayer, poss.Member) != null))
                targetType = cufetLayer;
            else
                return InferBookPossessiveAccess(poss, bt);
        }

        if (targetType is not ObjectType ot)
            throw TypeError(
                $"possessive access ('s) requires an object, but got a {FormatType(targetType)}",
                null, poss.Line, poss.Column,
                $"use 's on a {FormatType(targetType)}",
                "Only objects and books support the possessive 's syntax.");

        // Methods first, then getters (field-syntax), then fields.
        var methodSig = FindMethodInOtOrPromoted(ot, poss.Member);
        if (methodSig != null) return methodSig;  // method ref: depth tracked at call site via _castDepthCache

        var getterType = FindGetterInOtOrPromoted(ot, poss.Member);
        if (getterType != null)
        {
            if (IsReferenceType(getterType))
                _possessiveDepthCache[poss] = ComputeMemberAccessDepth(ot, poss.Member, poss.Target);
            return getterType;
        }

        var fieldType = FindFieldInOtOrPromoted(ot, poss.Member);
        if (fieldType != null)
        {
            if (IsReferenceType(fieldType))
                _possessiveDepthCache[poss] = ComputeMemberAccessDepth(ot, poss.Member, poss.Target);
            return fieldType;
        }

        var allFields  = GetAllNamedFields(ot);
        var available  = string.Join(", ",
            allFields.Select(f => $"'{f.FieldName}'")
            .Concat(ot.Getters.Select(g => $"'{g.GetterName}' (getter)"))
            .Concat(ot.Methods.Select(m => $"'{m.MethodName}' (method)")));
        throw TypeError(
            $"'{ot.Name}' has no field, getter, or method named '{poss.Member}'",
            null, poss.Line, poss.Column,
            $"access '{poss.Member}' on a {ot.Name}",
            available.Length > 0 ? $"Available: {available}." : $"'{ot.Name}' has no fields, getters, or methods.");
    }

    // ── Operator overloads (Pass2) ─────────────────────────────────────────────

    // Type-checks all OperatorOverloadDeclarations; populates _overloadReturnTypes.
    // Called after Pass1Hoist so all ObjectTypes are registered before body-checking.
    private void Pass2CheckOverloads(Program program)
    {
        var seen = new HashSet<(string, string, TokenType)>();
        foreach (var stmt in FlattenHoistable(program.Statements))
        {
            if (stmt is not OperatorOverloadDeclaration oad) continue;

            var left  = ResolveOperandType(oad, oad.LeftTypeName);
            var right = ResolveOperandType(oad, oad.RightTypeName);

            // ⚠ The one thing an overload may not take is a pair that ALREADY MEANS something.
            // `number * number` and `bits * bits` are built-in arithmetic, and the overload lookup
            // runs before the numeric path — so declaring one would capture `1 * 2` and
            // multiplication would stop meaning multiplication. REFERENCE has always said built-ins
            // cannot be shadowed; this is that sentence, and nothing more.
            //
            // ★ Deliberately no wider than its reason. `text * number` and `text + text` shadow
            // NOTHING — arithmetic on them is an error today — so a program that wants them may
            // have them. Whether concatenating with `+` when `joined to` exists is good style is
            // the writer's business, not the checker's.
            if (AlreadyMeansSomething(left, right))
                throw TypeError(
                    $"'{oad.LeftTypeName} {FormatOp(oad.Operator)} {oad.RightTypeName}' already means "
                  + "something, so it cannot be overloaded",
                    "That is built-in arithmetic, and a built-in cannot be shadowed — otherwise "
                  + $"'{FormatOp(oad.Operator)}' would mean one thing here and another everywhere else",
                    oad.Line, oad.Column,
                    $"overload '{FormatOp(oad.Operator)}' for '{oad.LeftTypeName}' and '{oad.RightTypeName}'",
                    "Overload a pair that has no meaning yet — one with an object type in it, or "
                  + "something like 'text * number'.");

            var key = (oad.LeftTypeName, oad.RightTypeName, oad.Operator);
            if (!seen.Add(key))
                throw TypeError(
                    $"'{oad.LeftTypeName} {FormatOp(oad.Operator)} {oad.RightTypeName}' already has an overload",
                    null, oad.Line, oad.Column,
                    $"declare a second '{FormatOp(oad.Operator)}' overload for that pair",
                    "Each operator can only be overloaded once per ORDERED pair of types. "
                  + $"'{oad.RightTypeName} {FormatOp(oad.Operator)} {oad.LeftTypeName}' is a "
                  + "different pair and may be declared separately.");

            CheckOperatorOverload(oad, left, right);
        }
    }

    /// <summary>True when this operand pair is already built-in arithmetic.</summary>
    /// <remarks>
    /// ⚠ Exactly two pairs, and they are the whole of what an overload may not take. Everything
    /// else — `text * number`, `text + text`, `fact + fact` — is an error today, so nothing is
    /// shadowed by giving it a meaning.
    /// </remarks>
    private static bool AlreadyMeansSomething(CufetType left, CufetType right) =>
        (left == CufetType.Number && right == CufetType.Number)
     || (left == CufetType.Bits   && right == CufetType.Bits);

    /// <summary>One operand's written type name, resolved.</summary>
    /// <remarks>
    /// ⚠ Only the shapes an operand can actually BE: an object the program defined, or a built-in
    /// scalar. A series or a map cannot be an operand type — nothing would be gained and the
    /// dispatch key is a name, which those do not have a single one of.
    /// </remarks>
    private CufetType ResolveOperandType(OperatorOverloadDeclaration oad, string typeName)
    {
        if (_objectDefs.TryGetValue(typeName, out var objType)) return objType;
        return typeName.ToLowerInvariant() switch
        {
            "number" => CufetType.Number,
            "text"   => CufetType.Text,
            "fact"   => CufetType.Fact,
            "bits"   => CufetType.Bits,
            _ => throw TypeError(
                    $"'{typeName}' is not a defined object type — an operator overload has no type to register on",
                    null, oad.Line, oad.Column,
                    $"declare an overload for '{typeName}'",
                    $"Define 'object {typeName}' before declaring operator overloads for it, or check the spelling."),
        };
    }

    private void CheckOperatorOverload(OperatorOverloadDeclaration oad, CufetType leftType, CufetType rightType)
    {
        bool isFallible = HasDirectFailureReturn(oad.Body);

        var saved = SaveScopes();
        ImportTopLevelVisible(saved);
        // Each operand carries ITS OWN type — the two may differ, which is the whole point.
        Scope[oad.LeftName]  = new TypeInfo(leftType,  new VariableReference(oad.LeftName, 0, 0), oad.Line);
        Scope[oad.RightName] = new TypeInfo(rightType, new VariableReference(oad.RightName, 0, 0), oad.Line);

        var prevInFunction       = _inFunction;
        var prevReturnType       = _expectedReturnType;
        var prevFunctionLine     = _functionDeclarationLine;
        var prevRabbitDepth      = _rabbitDepth;
        var prevInferring        = _inferringLambdaReturn;
        var prevOvFallible       = _overloadBodyIsFallible;
        var prevHidden           = _hiddenTopLevelData;

        _inFunction              = true;
        _expectedReturnType      = null;
        _functionDeclarationLine = oad.Line;
        _rabbitDepth             = 0;
        _inferringLambdaReturn   = true;
        _overloadBodyIsFallible  = isFallible;

        try
        {
            CheckBlock(oad.Body);

            if (!DefinitelyReturns(oad.Body))
                throw TypeError(
                    $"the '{oad.LeftTypeName} {FormatOp(oad.Operator)} {oad.RightTypeName}' overload can reach its "
                  + "end without returning a value",
                    null, oad.Line, oad.Column,
                    "define an operator overload that might not return a value",
                    "Make sure every path through the overload ends with a return statement.");
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
            _hiddenTopLevelData      = prevHidden;
            RestoreScopes(saved);

            if (inferredReturn != null)
                _overloadReturnTypes[(oad.LeftTypeName, oad.RightTypeName, oad.Operator)] = inferredReturn;
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
                PullRabbitStatement pr  => [pr.Body],
                PullStatement pl        => [pl.Body],
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
