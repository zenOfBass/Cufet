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
        return (object)dict.ContainsKey(Evaluate(hasEntry.Key));
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
        var val = Evaluate(mapSet.Value);
        dict[key] = val;
    }
}
