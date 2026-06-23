namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    private void ExecutePossessiveSet(PossessiveSetStatement pss)
    {
        var target = Evaluate(pss.Target);
        if (target is not ObjectValue pssOv)
            throw new RuntimeException($"Possessive assignment requires an object (line {pss.Line}).");

        // Route through setter if one exists and we're not already inside it (bypass).
        var setterDef = FindSetterInObjDefs(pssOv, pss.Member);
        if (setterDef != null && _inSetterFor != (pssOv.TypeName, pss.Member))
        {
            ExecuteSetterMethod(pssOv, setterDef, pss.Value, pss.Line);
            return;
        }

        var owner = FindOwnerForNamedField(pssOv, pss.Member);
        if (owner == null)
            throw new RuntimeException($"Object of type '{pssOv.TypeName}' has no field named '{pss.Member}' (line {pss.Line}).");
        var fi = owner.NamedFields.FindIndex(f => f.Name == pss.Member);
        owner.NamedFields[fi] = (pss.Member, Evaluate(pss.Value));
    }

    // ── Getter / setter dispatch ──────────────────────────────────────────────

    // Walks the object's type hierarchy (embed chain) looking for a getter with the given name.
    private GetterDeclaration? FindGetterInObjDefs(ObjectValue ov, string name)
    {
        var current = ov;
        while (current != null)
        {
            if (_objectDefs.TryGetValue(current.TypeName, out var def))
            {
                var g = def.Getters.FirstOrDefault(g => g.Name == name);
                if (g != null) return g;
            }
            current = current.EmbeddedObject;
        }
        return null;
    }

    // Walks the object's type hierarchy (embed chain) looking for a setter with the given name.
    private SetterDeclaration? FindSetterInObjDefs(ObjectValue ov, string name)
    {
        var current = ov;
        while (current != null)
        {
            if (_objectDefs.TryGetValue(current.TypeName, out var def))
            {
                var s = def.Setters.FirstOrDefault(s => s.Name == name);
                if (s != null) return s;
            }
            current = current.EmbeddedObject;
        }
        return null;
    }

    // Runs a getter body with 'one' bound to the receiver; returns the computed value.
    private object ExecuteGetterMethod(ObjectValue receiver, GetterDeclaration getter, int line)
    {
        _callDepth++;
        if (_callDepth > _maxCallDepth)
        {
            _callDepth--;
            throw new RuntimeException($"Getter '{getter.Name}' called itself too many times (line {line}).");
        }

        var saved = SaveScopes();
        foreach (var scope in saved.Scopes)
            foreach (var (k, v) in scope)
                if (v is FunctionValue) Scope[k] = v;
        Scope["one"] = receiver;

        object? returnValue = null;
        try
        {
            foreach (var stmt in getter.Body)
                Execute(stmt);
        }
        catch (ReturnException re)
        {
            returnValue = re.Value;
        }
        finally
        {
            RestoreScopes(saved);
            _callDepth--;
        }

        if (returnValue == null)
            throw new RuntimeException($"Getter '{getter.Name}' did not return a value (line {line}).");
        return returnValue;
    }

    // Runs a setter body: sets _inSetterFor so recursive writes to the same field bypass re-dispatch.
    private void ExecuteSetterMethod(ObjectValue receiver, SetterDeclaration setter, IExpression valueExpr, int line)
    {
        var newValue = Evaluate(valueExpr);
        var prevInSetterFor = _inSetterFor;
        _inSetterFor = (receiver.TypeName, setter.Name);

        _callDepth++;
        if (_callDepth > _maxCallDepth)
        {
            _callDepth--;
            _inSetterFor = prevInSetterFor;
            throw new RuntimeException($"Setter '{setter.Name}' called itself too many times (line {line}).");
        }

        var saved = SaveScopes();
        foreach (var scope in saved.Scopes)
            foreach (var (k, v) in scope)
                if (v is FunctionValue) Scope[k] = v;
        Scope["one"]             = receiver;
        Scope[setter.ParamName]  = newValue;

        try
        {
            foreach (var stmt in setter.Body)
                Execute(stmt);
        }
        catch (ReturnException) { } // setter is void — discard any bare Return
        finally
        {
            RestoreScopes(saved);
            _callDepth--;
            _inSetterFor = prevInSetterFor;
        }
    }

    // ── Embedding helpers (Slice 4) ───────────────────────────────────────────

    // Finds the field value in ov or any embedded object, including the embed handle.
    private bool TryFindNamedFieldValue(ObjectValue ov, string fieldName, out object value)
    {
        // Embed handle: "the person of customer" returns the embedded ObjectValue itself.
        if (ov.EmbeddedObject != null && fieldName == ov.EmbeddedObject.TypeName)
        {
            value = ov.EmbeddedObject;
            return true;
        }
        var fi = ov.NamedFields.FindIndex(f => f.Name == fieldName);
        if (fi >= 0) { value = ov.NamedFields[fi].Value; return true; }
        if (ov.EmbeddedObject != null) return TryFindNamedFieldValue(ov.EmbeddedObject, fieldName, out value);
        value = null!;
        return false;
    }

    // Returns the ObjectValue that directly owns the named field (for in-place mutation).
    private ObjectValue? FindOwnerForNamedField(ObjectValue ov, string fieldName)
    {
        if (ov.NamedFields.FindIndex(f => f.Name == fieldName) >= 0) return ov;
        if (ov.EmbeddedObject != null) return FindOwnerForNamedField(ov.EmbeddedObject, fieldName);
        return null;
    }

    // Returns (owner, zeroBasedIndex) for a positional field, traversing the embed chain.
    private (ObjectValue owner, int idx)? FindOwnerForPositional(ObjectValue ov, int oneBasedIdx)
    {
        if (oneBasedIdx >= 1 && oneBasedIdx <= ov.PositionalFields.Count)
            return (ov, oneBasedIdx - 1);
        if (ov.EmbeddedObject != null)
            return FindOwnerForPositional(ov.EmbeddedObject, oneBasedIdx - ov.PositionalFields.Count);
        return null;
    }

    // Builds an ObjectValue from a flat field list, routing fields to the right level.
    private ObjectValue BuildObjectValue(
        ObjectDefinition def,
        IReadOnlyList<IExpression> allPositionals,
        IReadOnlyList<(string Name, IExpression Value)> allNamed,
        int line)
    {
        int ownPosCount = def.PositionalTypes.Count;

        var ownPosValues = new List<object>(ownPosCount);
        for (int i = 0; i < ownPosCount; i++)
            ownPosValues.Add(Evaluate(allPositionals[i]));

        var ownFieldNames  = new HashSet<string>(def.NamedFields.Select(f => f.FieldName));
        var ownNamedValues = new List<(string Name, object Value)>();
        var remaining      = new List<(string Name, IExpression Value)>();
        foreach (var (name, expr) in allNamed)
        {
            if (ownFieldNames.Contains(name)) ownNamedValues.Add((name, Evaluate(expr)));
            else remaining.Add((name, expr));
        }

        ObjectValue? embeddedObject = null;
        if (def.EmbeddedTypeName != null)
        {
            if (!_objectDefs.TryGetValue(def.EmbeddedTypeName, out var embedDef))
                throw new RuntimeException(
                    $"Object '{def.Name}' embeds '{def.EmbeddedTypeName}', which is not defined (line {line}).");

            var remainingPos = new List<IExpression>();
            for (int i = ownPosCount; i < allPositionals.Count; i++)
                remainingPos.Add(allPositionals[i]);

            embeddedObject = BuildObjectValue(embedDef, remainingPos, remaining, line);
        }

        return new ObjectValue(def.Name, ownPosValues, ownNamedValues, embeddedObject);
    }

    private object EvaluateObjectLiteral(ObjectLiteral ol)
    {
        if (!_objectDefs.TryGetValue(ol.TypeName, out var def))
            throw new RuntimeException(
                $"'{ol.TypeName}' is not a defined object type (line {ol.Line}).");
        return BuildObjectValue(def, ol.PositionalValues, ol.NamedValues, ol.Line);
    }

    private object EvaluatePossessiveAccess(PossessiveAccess pa)
    {
        var target = Evaluate(pa.Target);
        if (target is BookValue bv)
        {
            if (bv.Constants.TryGetValue(pa.Member, out var constVal)) return constVal;
            if (bv.Functions.ContainsKey(pa.Member))
                throw new RuntimeException(
                    $"Book function '{bv.Name}'.'{pa.Member}' must be called with 'of' (line {pa.Line}).");
            throw new RuntimeException($"Book '{bv.Name}' has no member '{pa.Member}' (line {pa.Line}).");
        }
        if (target is not ObjectValue ov)
            throw new RuntimeException(
                $"You're trying to access '{pa.Member}' on something that isn't an object (line {pa.Line}).");

        // Dispatch getter before stored field — uniform access.
        var getter = FindGetterInObjDefs(ov, pa.Member);
        if (getter != null) return ExecuteGetterMethod(ov, getter, pa.Line);

        if (TryFindNamedFieldValue(ov, pa.Member, out var found)) return found;
        throw new RuntimeException(
            $"Object of type '{ov.TypeName}' has no field or getter named '{pa.Member}' (line {pa.Line}).");
    }

    // Fires unmakers for all objects defined in this scope, in reverse definition order (LIFO).
    private void RunScopeUnmakers(List<string> defOrder, Dictionary<string, object> scope)
    {
        for (int i = defOrder.Count - 1; i >= 0; i--)
        {
            var name = defOrder[i];
            if (scope.TryGetValue(name, out var val) && val is ObjectValue ov
                && _unmakeDefs.TryGetValue(ov.TypeName, out var ud))
                ExecuteUnmake(ud, ov);
        }
    }

    // Runs the unmake body with 'one' bound to the receiver. Void and infallible.
    private void ExecuteUnmake(UnmakerDeclaration ud, ObjectValue receiver)
    {
        _callDepth++;
        if (_callDepth > _maxCallDepth)
        {
            _callDepth--;
            return; // infallible — swallow instead of throwing
        }

        var saved = SaveScopes();
        foreach (var scope in saved.Scopes)
            foreach (var (k, v) in scope)
                if (v is FunctionValue) Scope[k] = v;
        Scope["one"] = receiver;

        try
        {
            foreach (var stmt in ud.Body)
                Execute(stmt);
        }
        catch (ReturnException) { } // void — discard any bare return
        finally
        {
            RestoreScopes(saved);
            _callDepth--;
        }
    }
}
