namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // Map keys must be value types (text, number, fact). Reference types — objects, series,
    // maps — can't be keys because their identity changes when copied (Define deep-copies
    // ObjectValues; two List<object> instances with the same content are still different
    // references), so lookups would silently always-miss (the map behaves empty, wrong answers,
    // no error). Every systems language draws this line: Python non-hashable lists, Rust
    // Hash+Eq bounds, Java hashCode/equals contract.
    private static void RequireValidMapKeyType(CufetType keyType, int line)
    {
        if (keyType is NumberType or TextType or FactType) return;

        var typeName = FormatType(keyType);
        var kind = keyType switch
        {
            ObjectType => "an object",
            SeriesType => "a series",
            MapType    => "a map",
            RecordType => "a record",
            _          => "a reference type"
        };
        throw new TypeException(FormatTypeError(
            "map keys must be value types (text, number, or fact)",
            $"'{typeName}' is {kind} — reference types can't be map keys because their identity " +
            "changes when copied, so lookups silently always-fail (the map behaves empty, " +
            "computing wrong answers with no error)",
            line,
            $"use a '{typeName}' as a map key",
            $"Key by a value field instead: e.g. 'map from text to ...' keyed by a name field, " +
            "or 'map from number to ...' keyed by an id field."));
    }

    private void CheckMapSet(MapSetStatement mapSet)
    {
        var mapType = InferType(mapSet.Map);
        if (mapType == null) return;
        if (mapType is not MapType mt)
            throw new TypeException(FormatTypeError(
                "the target of a map entry assignment must be a map",
                null, mapSet.Line,
                "assign a map entry on a non-map value",
                "Only maps support 'in map, the entry for key becomes value'."));

        var keyType = InferType(mapSet.Key);
        if (keyType != null && !IsAssignable(mt.KeyType, keyType))
            throw new TypeException(FormatTypeError(
                $"this map uses {FormatType(mt.KeyType)} keys",
                null, mapSet.Line,
                $"use a {FormatType(keyType)} as a key",
                $"Keys in this map must be {FormatTypePlural(mt.KeyType)}."));

        var valType = InferType(mapSet.Value);
        if (valType != null && !IsAssignable(mt.ValueType, valType))
            throw new TypeException(FormatTypeError(
                $"this map holds {FormatTypePlural(mt.ValueType)}",
                null, mapSet.Line,
                $"store a {FormatType(valType)} in it",
                $"Values in this map must be {FormatTypePlural(mt.ValueType)}."));

        // Region invariant: don't store a rabbit-scoped value in a longer-lived map.
        CheckRegionStore(mapSet.Value, valType, ContainerDepthOf(mapSet.Map), mapSet.Line,
            "store a rabbit-scoped value in a map that lives in a longer-lived region");
    }

    private CufetType? InferMapLiteral(MapLiteral lit)
    {
        // Empty map — type annotation required; provided by parser
        if (lit.Pairs.Count == 0)
        {
            RequireValidMapKeyType(lit.KeyType!, lit.Line);
            return new MapType(lit.KeyType!, lit.ValueType!);
        }

        // Atlas with explicit annotations — validate each pair against the declared types
        // and return the declared map type (preserving union value types).
        if (lit.KeyType != null && lit.ValueType != null)
        {
            RequireValidMapKeyType(lit.KeyType, lit.Line);
            foreach (var (kExpr, vExpr) in lit.Pairs)
            {
                var kType = InferType(kExpr);
                if (kType != null && !IsAssignable(lit.KeyType, kType))
                    throw new TypeException(FormatTypeError(
                        $"this atlas uses {FormatType(lit.KeyType)} keys",
                        null, lit.Line,
                        $"use a {FormatType(kType)} as a key",
                        $"Keys in this atlas must be {FormatTypePlural(lit.KeyType)}."));
                var vType = InferType(vExpr);
                if (vType != null && !IsAssignable(lit.ValueType, vType))
                    throw new TypeException(FormatTypeError(
                        $"this atlas holds {FormatTypePlural(lit.ValueType)}",
                        null, lit.Line,
                        $"store a {FormatType(vType)} in it",
                        $"Values in this atlas must be {FormatTypePlural(lit.ValueType)}."));
            }
            return new MapType(lit.KeyType, lit.ValueType);
        }

        // Populated map — infer key and value types from pairs; all must agree
        CufetType? inferredKey = null;
        CufetType? inferredVal = null;

        for (int i = 0; i < lit.Pairs.Count; i++)
        {
            var (kExpr, vExpr) = lit.Pairs[i];
            var kType = InferType(kExpr);
            var vType = InferType(vExpr);

            if (kType != null)
            {
                if (inferredKey == null)
                {
                    RequireValidMapKeyType(kType, lit.Line);
                    inferredKey = kType;
                }
                else if (inferredKey != kType)
                    throw new TypeException(FormatTypeError(
                        "all keys in a map must be the same type",
                        $"The first key is a {FormatType(inferredKey)}, so all keys must be {FormatTypePlural(inferredKey)}",
                        lit.Line,
                        $"mix a {FormatType(kType)} key with {FormatTypePlural(inferredKey)} keys",
                        "Make all keys the same type."));
            }

            if (vType != null)
            {
                if (inferredVal == null)
                    inferredVal = vType;
                else if (inferredVal != vType)
                    throw new TypeException(FormatTypeError(
                        "all values in a map must be the same type",
                        $"The first value is a {FormatType(inferredVal)}, so all values must be {FormatTypePlural(inferredVal)}",
                        lit.Line,
                        $"mix a {FormatType(vType)} value with {FormatTypePlural(inferredVal)} values",
                        "Make all values the same type."));
            }
        }

        if (inferredKey == null || inferredVal == null)
            return null; // can't determine types statically — runtime will catch type mismatches

        return new MapType(inferredKey, inferredVal);
    }

    private CufetType? InferMapLookup(MapLookup lookup)
    {
        var mapType = InferType(lookup.Map);
        if (mapType == null) return null;
        if (mapType is not MapType mt)
            throw new TypeException(FormatTypeError(
                "'the entry for ... in ...' requires a map",
                null, lookup.Line,
                $"look up an entry in a {FormatType(mapType)}",
                "Only maps support entry lookup. Use 'the entry for key in map'."));

        var keyType = InferType(lookup.Key);
        if (keyType != null && !IsAssignable(mt.KeyType, keyType))
            throw new TypeException(FormatTypeError(
                $"this map uses {FormatType(mt.KeyType)} keys",
                null, lookup.Line,
                $"look up using a {FormatType(keyType)} key",
                $"Keys in this map are {FormatTypePlural(mt.KeyType)}."));

        // Flatten: a map whose value type is already voidable must not produce
        // 'voidable voidable V' from a lookup — the nesting never surfaces to the user.
        return mt.ValueType is VoidableType ? mt.ValueType : new VoidableType(mt.ValueType);
    }

    private CufetType InferMapHasKey(MapHasKey hasKey)
    {
        var mapType = InferType(hasKey.Map);
        if (mapType is MapType mt)
        {
            var keyType = InferType(hasKey.Key);
            if (keyType != null && !IsAssignable(mt.KeyType, keyType))
                throw new TypeException(FormatTypeError(
                    $"this map uses {FormatType(mt.KeyType)} keys",
                    null, hasKey.Line,
                    $"check for a {FormatType(keyType)} key",
                    $"Keys in this map are {FormatTypePlural(mt.KeyType)}."));
        }
        return CufetType.Fact;
    }

    private CufetType InferMapHasEntry(MapHasEntry hasEntry)
    {
        var mapType = InferType(hasEntry.Map);
        if (mapType is MapType mt)
        {
            var keyType = InferType(hasEntry.Key);
            if (keyType != null && !IsAssignable(mt.KeyType, keyType))
                throw new TypeException(FormatTypeError(
                    $"this map uses {FormatType(mt.KeyType)} keys",
                    null, hasEntry.Line,
                    $"check for a {FormatType(keyType)} key",
                    $"Keys in this map are {FormatTypePlural(mt.KeyType)}."));
        }
        return CufetType.Fact;
    }

    private CufetType InferMapSize(MapSize size)
    {
        var mapType = InferType(size.Map);
        if (mapType != null && mapType is not MapType)
            throw new TypeException(FormatTypeError(
                "'the size of' works on maps",
                null, size.Line,
                $"get the size of a {FormatType(mapType)}",
                "For series, use 'the number of'. For text, use 'the length of'."));
        return CufetType.Number;
    }
}
