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
        var dict = new Dictionary<object, object>();
        foreach (var (kExpr, vExpr) in lit.Pairs)
        {
            var k = Evaluate(kExpr);
            var v = Evaluate(vExpr);
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
        // Safety net: TypeChecker catches reference-type keys at compile time; guard here too
        // in case an untyped path reaches runtime (dynamic inference gap).
        if (key is ObjectValue or List<object> or Dictionary<object, object>)
            throw new RuntimeException(
                $"Map keys must be text, number, or fact (line {mapSet.Line}). " +
                "Reference types (objects, series, maps) can't be keys — their identity changes " +
                "when copied, causing silent lookup failures. Key by a value field instead " +
                "(e.g. a text name or number id).");
        var val = Evaluate(mapSet.Value);
        dict[key] = val;
    }
}
