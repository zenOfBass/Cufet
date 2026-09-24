namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    private void CheckRecordNamedSet(RecordNamedSetStatement stmt)
    {
        var recordType = InferType(stmt.Record);
        if (recordType == null) return;

        if (recordType is ObjectType ot)
        {
            // Setter intercepts the write if one is defined.
            var setterSig = FindSetterInOtOrPromoted(ot, stmt.FieldName);
            if (setterSig != null)
            {
                var setterValueType = InferType(stmt.Value);
                if (setterValueType != null && !IsAssignable(setterSig.Value.ParamType, setterValueType))
                    throw TypeError(
                        $"setter for '{stmt.FieldName}' expects a {FormatType(setterSig.Value.ParamType)}, not a {FormatType(setterValueType)}",
                        null, stmt.Line, stmt.Column,
                        $"set '{stmt.FieldName}' to a {FormatType(setterValueType)}",
                        $"The setter for '{stmt.FieldName}' accepts a {FormatType(setterSig.Value.ParamType)}.");
                CheckRegionStore(stmt.Value, InferType(stmt.Value), ContainerDepthOf(stmt.Record), stmt.Line, stmt.Column,
                    $"set field '{stmt.FieldName}' to a value from a shorter-lived region");
                stmt.EscapeToDepth = EscapeDepthFor(stmt.Value, InferType(stmt.Value), ContainerDepthOf(stmt.Record));
                return;
            }
            CheckObjectNamedSet(ot, stmt.FieldName, stmt.Value, stmt.Line, stmt.Column);
            var objValueType = InferType(stmt.Value);
            CheckRegionStore(stmt.Value, objValueType, ContainerDepthOf(stmt.Record), stmt.Line, stmt.Column,
                $"set field '{stmt.FieldName}' to a value from a shorter-lived region");
            stmt.EscapeToDepth = EscapeDepthFor(stmt.Value, objValueType, ContainerDepthOf(stmt.Record));
            return;
        }

        if (recordType is not RecordType rt)
            throw TypeError(
                $"you're trying to set field '{stmt.FieldName}' on something that isn't a record or object",
                null, stmt.Line, stmt.Column,
                $"set a named field on a {FormatType(recordType)}",
                "Only records and objects have named fields.");

        var field = rt.NamedFields.FirstOrDefault(f => f.Name == stmt.FieldName);
        if (field == default)
        {
            var hint = rt.NamedFields.Count > 0
                ? $"Available named fields: {string.Join(", ", rt.NamedFields.Select(f => f.Name))}."
                : "This record has no named fields.";
            throw TypeError(
                $"this record has no field named '{stmt.FieldName}'",
                null, stmt.Line, stmt.Column,
                $"set field '{stmt.FieldName}'",
                hint);
        }

        var valueType = InferType(stmt.Value);
        if (valueType != null && !IsAssignable(field.Type, valueType))
            throw TypeError(
                $"field '{stmt.FieldName}' holds a {FormatType(field.Type)}, not a {FormatType(valueType)}",
                null, stmt.Line, stmt.Column,
                $"set field '{stmt.FieldName}' to a {FormatType(valueType)}",
                $"Field '{stmt.FieldName}' has type {FormatType(field.Type)}.");

        // Region invariant: field value cannot outlive the record's rabbit region.
        CheckRegionStore(stmt.Value, valueType, ContainerDepthOf(stmt.Record), stmt.Line, stmt.Column,
            $"set field '{stmt.FieldName}' to a value from a shorter-lived region");
        stmt.EscapeToDepth = EscapeDepthFor(stmt.Value, valueType, ContainerDepthOf(stmt.Record));
    }

    private RecordType InferRecordLiteral(RecordLiteral lit)
    {
        var positionalTypes = new List<CufetType>();
        foreach (var field in lit.PositionalFields)
        {
            var t = InferType(field);
            if (t == null)
                throw TypeError(
                    "the type of a positional record field can't be determined",
                    null, lit.Line, lit.Column,
                    "use an expression whose type can't be inferred as a record field",
                    "Start with a literal value or a defined variable so the field type is clear.");
            positionalTypes.Add(t);
        }

        var namedFields = new List<(string Name, CufetType Type)>();
        foreach (var (name, valueExpr) in lit.NamedFields)
        {
            if (namedFields.Any(f => f.Name == name))
                throw TypeError(
                    $"the record has two fields both named '{name}'",
                    null, lit.Line, lit.Column,
                    $"define a record with duplicate field name '{name}'",
                    "Each named field must have a unique name.");
            var t = InferType(valueExpr);
            if (t == null)
                throw TypeError(
                    $"the type of field '{name}' can't be determined",
                    null, lit.Line, lit.Column,
                    "use an expression whose type can't be inferred as a record field",
                    "Start with a literal value or a defined variable so the field type is clear.");
            namedFields.Add((name, t));
        }

        return new RecordType(positionalTypes, namedFields);
    }

    private CufetType? InferRecordNamedAccess(RecordNamedAccess rna)
    {
        var recordType = InferType(rna.Record);
        if (recordType == null) return null;

        // Map iteration variable: "the key of mapping" / "the value of mapping".
        if (recordType is MappingType mt)
        {
            return rna.FieldName switch
            {
                "key"   => mt.KeyType,
                "value" => mt.ValueType,
                _ => throw TypeError(
                    $"a mapping only has 'key' and 'value' fields",
                    null, rna.Line, rna.Column,
                    $"access field '{rna.FieldName}' on a mapping",
                    "Use 'the key of mapping' or 'the value of mapping'.")
            };
        }

        // A matrix exposes 'rows' and 'columns' as counts. These are ordinary named access, not
        // reserved words, so `the rows of x` reads as a record field when x is a record and as
        // the row count when x is a matrix — resolved here, where the type is known, because the
        // parser has no way to tell and a human reader never has to.
        if (recordType is MatrixType)
        {
            return rna.FieldName switch
            {
                "rows" or "columns" => CufetType.Number,
                _ => throw TypeError(
                    "a matrix only has 'rows' and 'columns'",
                    null, rna.Line, rna.Column,
                    $"access field '{rna.FieldName}' on a matrix",
                    "Use 'the rows of m' or 'the columns of m'. For an element, use " +
                    "'the item at (row, column) of m'.")
            };
        }

        // Exception values expose only 'message' (text).
        if (recordType is ExceptionMarkerType)
        {
            return rna.FieldName switch
            {
                "message" => CufetType.Text,
                _ => throw TypeError(
                    "an exception only has a 'message' field",
                    null, rna.Line, rna.Column,
                    $"access field '{rna.FieldName}' on an exception",
                    "Use 'the message of the exception'.")
            };
        }

        // Failure values expose 'message' (text) and 'category' (voidable text).
        if (recordType is FailureMarkerType)
        {
            return rna.FieldName switch
            {
                "message"  => CufetType.Text,
                "category" => new VoidableType(CufetType.Text),
                _ => throw TypeError(
                    "a failure only has 'message' and 'category' fields",
                    null, rna.Line, rna.Column,
                    $"access field '{rna.FieldName}' on a failure",
                    "Use 'the message of the failure' or 'the category of the failure'.")
            };
        }

        // Named field/getter access on objects (the <name> of <object>).
        // Getters intercept reads before stored fields; includes promoted members.
        if (recordType is ObjectType ot)
        {
            // Check getter before stored field — uniform access.
            var getterType = FindGetterInOtOrPromoted(ot, rna.FieldName);
            if (getterType != null)
            {
                if (IsReferenceType(getterType))
                    _rnaDepthCache[rna] = ComputeMemberAccessDepth(ot, rna.FieldName, rna.Record);
                return getterType;
            }

            var found = FindFieldInOtOrPromoted(ot, rna.FieldName);
            if (found != null)
            {
                if (IsReferenceType(found))
                    _rnaDepthCache[rna] = ComputeMemberAccessDepth(ot, rna.FieldName, rna.Record);
                return found;
            }
            var allFields = GetAllNamedFields(ot);
            var available = string.Join(", ",
                allFields.Select(f => $"'{f.FieldName}'")
                .Concat(ot.Getters.Select(g => $"'{g.GetterName}' (getter)")));
            throw TypeError(
                $"'{ot.Name}' has no field or getter named '{rna.FieldName}'",
                null, rna.Line, rna.Column,
                $"access field '{rna.FieldName}'",
                available.Length > 0
                    ? $"Available: {available}."
                    : $"'{ot.Name}' has no named fields or getters.");
        }

    // ★ `the width of <bits>` — resolved HERE rather than by a keyword. `width` stays a legal
    // field name (huffmancoding has one), because this only fires when the target is a bits and
    // so could never have had a field at all. A reserved `width` would have cost every user a
    // common noun to buy a property only one type has.
    if (recordType is BitsType && rna.FieldName == "width")
            return CufetType.Number;

        /*  `the length of <text>` and `the size of <map>` — resolved here for the same reason
            and by the same rule as `the width of <bits>` just above, and as `the rows of
            <matrix>` further up. Both were RESERVED WORDS until 2026-09-24, which cost every
            program two of the most ordinary nouns in the language as a variable AND as a field
            name, to buy a property that one built-in type each has.

            ★ They stay legal field names because these arms only fire when the target is a text
            or a map, and neither could ever have had a field of its own. `the length of <record>`
            falls straight through to the record lookup below. */
        if (recordType is TextType && rna.FieldName == "length")
            return CufetType.Number;
        if (recordType is SetType && rna.FieldName == "size")
            // ⚠ Checked BEFORE the map arm and kept word for word from the refusal `the size of`
            // gave as a keyword. A set is stored as a map and reads like a collection, so this is
            // exactly the wrong guess someone will make.
            throw TypeError(
                "'the size of' works on maps, and this is a set",
                null, rna.Line, rna.Column,
                $"get the size of {FormatExpr(rna.Record)}",
                $"Write 'the number of {FormatExpr(rna.Record)}' instead.");
        if (recordType is MapType && rna.FieldName == "size")
            return CufetType.Number;

        /*  ⚠⚠ THE REAL COST OF FREEING A WORD, AND WHAT IT WOULD HAVE COST SILENTLY. As keywords
            these two owned their refusals — *"'the length of' works on text only … for series,
            use 'the number of series'"*. Freed and left alone, `the length of scores` would
            answer *"that isn't a record or object"*: true, and it sends the reader nowhere.

            ★ So the messages move here with the words. A record or object still falls through,
            because on those the name really might be a field and the lookup below says so
            better than this could. */
        if (rna.FieldName == "length" && recordType is not (RecordType or ObjectType))
            throw TypeError(
                "'the length of' works on text only",
                null, rna.Line, rna.Column,
                $"get the length of a {FormatType(recordType)}",
                "Only text values have a character length. For series, use 'the number of series'.");
        if (rna.FieldName == "size" && recordType is not (RecordType or ObjectType))
            throw TypeError(
                "'the size of' works on maps",
                null, rna.Line, rna.Column,
                $"get the size of a {FormatType(recordType)}",
                "For series, use 'the number of'. For text, use 'the length of'.");

        if (recordType is not RecordType rt)
            throw TypeError(
                $"you're trying to access field '{rna.FieldName}' on something that isn't a record or object",
                null, rna.Line, rna.Column,
                $"access a named field of a {FormatType(recordType)}",
                "Only records and objects have named fields.");

        var field = rt.NamedFields.FirstOrDefault(f => f.Name == rna.FieldName);
        if (field == default)
        {
            var suggestion = rt.NamedFields
                .Select(f => (f.Name, dist: Levenshtein(f.Name, rna.FieldName)))
                .Where(p => p.dist <= 2)
                .OrderBy(p => p.dist)
                .Select(p => p.Name)
                .FirstOrDefault();
            var available = rt.NamedFields.Count > 0
                ? $" Named fields: {string.Join(", ", rt.NamedFields.Select(f => $"'{f.Name}'"))}."
                : " This record has no named fields.";
            var fix = suggestion != null
                ? $"Did you mean '{suggestion}'?{available}"
                : available;
            throw TypeError(
                $"this record has no field named '{rna.FieldName}'",
                null, rna.Line, rna.Column,
                $"access field '{rna.FieldName}'",
                fix);
        }

        return field.Type;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = a[i - 1] == b[j - 1]
                    ? d[i - 1, j - 1]
                    : 1 + Math.Min(d[i - 1, j - 1], Math.Min(d[i - 1, j], d[i, j - 1]));
        return d[a.Length, b.Length];
    }
}
