namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    private void ExecutePossessiveSet(PossessiveSetStatement pss)
    {
        var target = Evaluate(pss.Target);
        if (target is not ObjectValue pssOv)
            throw new RuntimeException($"Possessive assignment requires an object (line {pss.Line}).");
        var owner = FindOwnerForNamedField(pssOv, pss.Member);
        if (owner == null)
            throw new RuntimeException($"Object of type '{pssOv.TypeName}' has no field named '{pss.Member}' (line {pss.Line}).");
        var fi = owner.NamedFields.FindIndex(f => f.Name == pss.Member);
        owner.NamedFields[fi] = (pss.Member, Evaluate(pss.Value));
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
        if (target is not ObjectValue ov)
            throw new RuntimeException(
                $"You're trying to access '{pa.Member}' on something that isn't an object (line {pa.Line}).");
        if (TryFindNamedFieldValue(ov, pa.Member, out var found)) return found;
        throw new RuntimeException(
            $"Object of type '{ov.TypeName}' has no field named '{pa.Member}' (line {pa.Line}).");
    }
}
