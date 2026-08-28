namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── What may be a map key ────────────────────────────────────────────────────────────────
    //
    // ★ A key has to answer two questions the same way every time it is asked: is this the same as
    // that (compared by CONTENT, not by which copy you are holding), and will it still be what it
    // was when you look the entry up again. So the rule is: **a scalar, or a record of keys.**
    //
    // ⚠ This replaced "text, number, or fact", which was too blunt in one direction and explained
    // itself with something FALSE in the other: it told the reader that a record is a reference
    // type whose identity changes when copied. A record is neither. `IsReferenceType` is
    // series/map/object/matrix/channel/address, records are absent from it, they deep-copy on
    // binding, and they compare structurally. The refusal was right about records by accident and
    // wrong about the reason, which is why no workaround for it ever felt principled.
    //
    // ⚠⚠ NOT `IsRegionBearing`, though the two walk the same shape and it is the obvious reach.
    // That predicate answers *"does the compiled representation hold an arena pointer"*, and it
    // says TRUE of `text` — so keying by text, the commonest key there is and the one
    // `examples/algorithms/dijkstra.cufe` is built on, would have been refused by it. Arena
    // residence is not the question here. Text lives in an arena and is still a perfect key: it is
    // compared by content and nothing can change it afterwards.
    //
    // ★ Why a series is refused, stated correctly: not because its identity changes, but because
    // it is MUTABLE. Insert under a key, change the key, and the entry can never be found again.
    // A record is safe for the mirrored reason — it is deep-copied on binding, so the map holds a
    // key nobody else can reach in to alter. A record HOLDING a series is refused, because the
    // series inside it is shared and mutable; that is what makes this a walk rather than a list.
    private static bool IsValidMapKeyType(CufetType? t) => t switch
    {
        // Scalars. `bits` compares on its VALUE alone, ignoring base and width — 0xFF and
        // 0b11111111 are one key written two ways, exactly as they are one value under `is`.
        NumberType or TextType or FactType or BitsType => true,
        RecordType rt => rt.PositionalTypes.All(IsValidMapKeyType)
                      && rt.NamedFields.All(f => IsValidMapKeyType(f.Type)),

        // ★ The WRAPPERS travel with what they hold, for the same reason a record does. A
        // `voidable number` is void or a number, and both compare by value and cannot change; a
        // `(number or text)` is whichever case it holds. Neither adds a way for a key to shift
        // underneath the map, so neither has a reason to be refused — `7` and `"seven"` filed in
        // one table is an ordinary thing to want.
        VoidableType vt => IsValidMapKeyType(vt.Inner),

        // ⚠ An OPEN union (Cases == null) can hold anything ever widened into it, including a
        // series — so it is refused for exactly the reason its cases would be, without being able
        // to name which one.
        UnionType ut => ut.Cases is { } cases && cases.All(IsValidMapKeyType),

        // ⚠⚠ `T or failure` is REFUSED, and not for the wrapper reason — a failure is what an
        // operation hands back when it cannot answer, not a value anyone looks something up by.
        // Admitting it would mean a map with an entry filed under "this went wrong", which is a
        // sentence with no meaning behind it. This is a judgement about what a failure IS, and it
        // is the one wrapper that stays out.
        FailureType => false,

        // Everything else, and deliberately by omission rather than by enumeration: a type that is
        // not listed above is not a key, and a NEW type is not a key until someone decides it is.
        _ => false,
    };

    private static void RequireValidMapKeyType(CufetType keyType, int line, int col)
    {
        if (IsValidMapKeyType(keyType)) return;

        var typeName = FormatType(keyType);

        // A record is refused for what is INSIDE it, so the message has to name that part — being
        // told "a record can't be a key" when a record of two numbers plainly can is the same dead
        // end the old message was.
        if (keyType is RecordType rt)
        {
            var (badName, badType) = FirstUnkeyableField(rt);
            throw TypeError(
                $"a key has to be a scalar or a record of scalars, and this record holds {FormatType(badType)}",
                $"a {FormatType(badType)} can be changed after it is used as a key, and then the "
              + "entry it was stored under could never be found again",
                line, col,
                $"use '{typeName}' as a map key",
                $"Leave {badName} out of the key — a record of numbers, text, facts or bit patterns "
              + "is fine, and those are what can be compared and cannot change underneath the map.");
        }

        var why = keyType switch
        {
            SeriesType or MapType or MatrixType =>
                "it can be changed after it is used as a key, and then the entry it was stored "
              + "under could never be found again",
            ObjectType =>
                "an object's fields can be written after it is used as a key, and then the entry "
              + "it was stored under could never be found again",
            _ => "only a scalar, or a record of scalars, can be compared as a key",
        };
        throw TypeError(
            $"'{typeName}' can't be a map key",
            why,
            line, col,
            $"use a '{typeName}' as a map key",
            "A key is a number, text, a fact, a bit pattern, or a record of those. Key by a field "
          + "that is one of them — a name, an id — or by a record of them, like a (row, column) pair.");
    }

    /// <summary>The first field of `rt` that cannot be part of a key, named for the message.</summary>
    private static (string Name, CufetType Type) FirstUnkeyableField(RecordType rt)
    {
        for (int i = 0; i < rt.PositionalTypes.Count; i++)
            if (!IsValidMapKeyType(rt.PositionalTypes[i]))
                return ($"the {Ordinal(i + 1)} field", rt.PositionalTypes[i]);
        foreach (var (name, type) in rt.NamedFields)
            if (!IsValidMapKeyType(type))
                return ($"'{name}'", type);
        return ("a field", rt);   // unreachable: only called when the record was refused
    }

    private static string Ordinal(int n) => n switch
    {
        1 => "first", 2 => "second", 3 => "third", 4 => "fourth", 5 => "fifth",
        6 => "sixth", 7 => "seventh", 8 => "eighth", 9 => "ninth", _ => $"{n}th",
    };

    private void CheckMapSet(MapSetStatement mapSet)
    {
        var mapType = InferType(mapSet.Map);
        if (mapType == null) return;
        if (mapType is not MapType mt)
            throw TypeError(
                "the target of a map entry assignment must be a map",
                null, mapSet.Line, mapSet.Column,
                "assign a map entry on a non-map value",
                "Only maps support 'in map, the entry for key becomes value'.");

        var keyType = InferType(mapSet.Key);
        if (keyType != null && !IsAssignable(mt.KeyType, keyType))
            throw TypeError(
                $"this map uses {FormatType(mt.KeyType)} keys",
                null, mapSet.Line, mapSet.Column,
                $"use a {FormatType(keyType)} as a key",
                $"Keys in this map must be {FormatTypePlural(mt.KeyType)}.");

        var valType = InferType(mapSet.Value);
        if (valType != null && !IsAssignable(mt.ValueType, valType))
            throw TypeError(
                $"this map holds {FormatTypePlural(mt.ValueType)}",
                null, mapSet.Line, mapSet.Column,
                $"store a {FormatType(valType)} in it",
                $"Values in this map must be {FormatTypePlural(mt.ValueType)}.");

        // Region invariant: don't store a rabbit-scoped value in a longer-lived map.
        CheckRegionStore(mapSet.Value, valType, ContainerDepthOf(mapSet.Map), mapSet.Line, mapSet.Column,
            "store a rabbit-scoped value in a map that lives in a longer-lived region");
        mapSet.EscapeToDepth = EscapeDepthFor(mapSet.Value, valType, ContainerDepthOf(mapSet.Map));
        // The key is stored too, so it escapes on the same terms as the value (a text key built
        // inside a rabbit and put into a longer-lived map would be freed at that rabbit's Done.
        // while the map still holds it). Annotate only — no CheckRegionStore for keys, so this
        // rejects nothing the interpreter accepts; the compiler copies the key outward instead.
        mapSet.KeyEscapeToDepth = EscapeDepthFor(mapSet.Key, keyType, ContainerDepthOf(mapSet.Map));
    }

    private CufetType? InferMapLiteral(MapLiteral lit)
    {
        // Empty map — type annotation required; provided by parser
        if (lit.Pairs.Count == 0)
        {
            RequireValidMapKeyType(lit.KeyType!, lit.Line, lit.Column);
            return new MapType(lit.KeyType!, lit.ValueType!);
        }

        // Atlas with explicit annotations — validate each pair against the declared types
        // and return the declared map type (preserving union value types).
        if (lit.KeyType != null && lit.ValueType != null)
        {
            RequireValidMapKeyType(lit.KeyType, lit.Line, lit.Column);
            foreach (var (kExpr, vExpr) in lit.Pairs)
            {
                var kType = InferType(kExpr);
                if (kType != null && !IsAssignable(lit.KeyType, kType))
                    throw TypeError(
                        $"this atlas uses {FormatType(lit.KeyType)} keys",
                        null, lit.Line, lit.Column,
                        $"use a {FormatType(kType)} as a key",
                        $"Keys in this atlas must be {FormatTypePlural(lit.KeyType)}.");
                var vType = InferType(vExpr);
                if (vType != null && !IsAssignable(lit.ValueType, vType))
                    throw TypeError(
                        $"this atlas holds {FormatTypePlural(lit.ValueType)}",
                        null, lit.Line, lit.Column,
                        $"store a {FormatType(vType)} in it",
                        $"Values in this atlas must be {FormatTypePlural(lit.ValueType)}.");
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
                    RequireValidMapKeyType(kType, lit.Line, lit.Column);
                    inferredKey = kType;
                }
                else if (inferredKey != kType)
                    throw TypeError(
                        "all keys in a map must be the same type",
                        $"The first key is a {FormatType(inferredKey)}, so all keys must be {FormatTypePlural(inferredKey)}",
                        lit.Line, lit.Column,
                        $"mix a {FormatType(kType)} key with {FormatTypePlural(inferredKey)} keys",
                        "Make all keys the same type.");
            }

            if (vType != null)
            {
                if (inferredVal == null)
                    inferredVal = vType;
                else if (inferredVal != vType)
                    throw TypeError(
                        "all values in a map must be the same type",
                        $"The first value is a {FormatType(inferredVal)}, so all values must be {FormatTypePlural(inferredVal)}",
                        lit.Line, lit.Column,
                        $"mix a {FormatType(vType)} value with {FormatTypePlural(inferredVal)} values",
                        "Make all values the same type.");
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
            throw TypeError(
                "'the entry for ... in ...' requires a map",
                null, lookup.Line, lookup.Column,
                $"look up an entry in a {FormatType(mapType)}",
                "Only maps support entry lookup. Use 'the entry for key in map'.");

        var keyType = InferType(lookup.Key);
        if (keyType != null && !IsAssignable(mt.KeyType, keyType))
            throw TypeError(
                $"this map uses {FormatType(mt.KeyType)} keys",
                null, lookup.Line, lookup.Column,
                $"look up using a {FormatType(keyType)} key",
                $"Keys in this map are {FormatTypePlural(mt.KeyType)}.");

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
                throw TypeError(
                    $"this map uses {FormatType(mt.KeyType)} keys",
                    null, hasKey.Line, hasKey.Column,
                    $"check for a {FormatType(keyType)} key",
                    $"Keys in this map are {FormatTypePlural(mt.KeyType)}.");
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
                throw TypeError(
                    $"this map uses {FormatType(mt.KeyType)} keys",
                    null, hasEntry.Line, hasEntry.Column,
                    $"check for a {FormatType(keyType)} key",
                    $"Keys in this map are {FormatTypePlural(mt.KeyType)}.");
        }
        return CufetType.Fact;
    }

    private CufetType InferMapSize(MapSize size)
    {
        var mapType = InferType(size.Map);
        if (mapType != null && mapType is not MapType)
            throw TypeError(
                "'the size of' works on maps",
                null, size.Line, size.Column,
                $"get the size of a {FormatType(mapType)}",
                "For series, use 'the number of'. For text, use 'the length of'.");
        return CufetType.Number;
    }
}
