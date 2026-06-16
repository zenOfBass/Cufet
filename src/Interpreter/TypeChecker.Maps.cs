namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
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
    }

    private CufetType? InferMapLiteral(MapLiteral lit)
    {
        // Empty map — type annotation required; provided by parser
        if (lit.Pairs.Count == 0)
            return new MapType(lit.KeyType!, lit.ValueType!);

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
                    if (kType != CufetType.Number && kType != CufetType.Text)
                        throw new TypeException(FormatTypeError(
                            "map keys must be numbers or text",
                            null, lit.Line,
                            $"use a {FormatType(kType)} as a map key",
                            "Map keys can only be numbers or text this version."));
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
