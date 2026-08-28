namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    private sealed class MappingValue
    {
        public object Key   { get; }
        public object Value { get; }
        public MappingValue(object key, object value) { Key = key; Value = value; }
    }

    private object EvaluateMapLiteral(MapLiteral lit)
    {
        var dict = NewMap();
        foreach (var (kExpr, vExpr) in lit.Pairs)
        {
            var k = Evaluate(kExpr);
            var v = BindCopy(Evaluate(vExpr));   // value types copy on insert (binding is binding)
            dict[k] = v;
        }
        return dict;
    }

    private object EvaluateMapLookup(MapLookup lookup)
    {
        var mapVal = Evaluate(lookup.Map);
        if (mapVal is not Dictionary<object, object> dict)
            throw new RuntimeException($"Expected a map for entry lookup on line {lookup.Line}.");
        var key = Evaluate(lookup.Key);
        return dict.TryGetValue(key, out var found) ? found : VoidValue.Instance;
    }

    private object EvaluateMapHasKey(MapHasKey hasKey)
    {
        var mapVal = Evaluate(hasKey.Map);
        if (mapVal is not Dictionary<object, object> dict)
            throw new RuntimeException($"Expected a map for 'has a key for' on line {hasKey.Line}.");
        return (object)dict.ContainsKey(Evaluate(hasKey.Key));
    }

    private object EvaluateMapHasEntry(MapHasEntry hasEntry)
    {
        var mapVal = Evaluate(hasEntry.Map);
        if (mapVal is not Dictionary<object, object> dict)
            throw new RuntimeException($"Expected a map for 'has an entry for' on line {hasEntry.Line}.");
        // Diverges from 'has a key': a slot holding an explicit void value counts as no entry.
        return (object)(dict.TryGetValue(Evaluate(hasEntry.Key), out var val) && val is not VoidValue);
    }

    private object EvaluateMapSize(MapSize size)
    {
        var mapVal = Evaluate(size.Map);
        if (mapVal is not Dictionary<object, object> dict)
            throw new RuntimeException($"Expected a map for 'the size of' on line {size.Line}.");
        return (object)(decimal)dict.Count;
    }

    private void ExecuteMapSet(MapSetStatement mapSet)
    {
        var mapVal = Evaluate(mapSet.Map);
        if (mapVal is not Dictionary<object, object> dict)
            throw new RuntimeException($"Expected a map for entry assignment on line {mapSet.Line}.");
        var key = Evaluate(mapSet.Key);
        // Safety net: the TypeChecker refuses these at check time; guarded here too in case an
        // untyped path reaches runtime (dynamic inference gap).
        // ⚠ A RECORD is deliberately absent from this list now — a record of scalars is a legal
        // key, and the map compares it structurally.
        if (key is ObjectValue or List<object> or Dictionary<object, object>)
            throw new RuntimeException(
                $"A map key has to be a scalar, or a record of scalars (line {mapSet.Line}). " +
                "A series, a map or an object can be changed after it is used as a key, and then " +
                "the entry it was stored under could never be found again. Key by a field that " +
                "cannot change — a name, an id — or by a record of them.");
        var val = BindCopy(Evaluate(mapSet.Value));   // value types copy on insert (binding is binding)
        dict[key] = val;
    }
}
