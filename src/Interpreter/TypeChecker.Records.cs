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
                if (setterValueType != null && setterValueType != setterSig.Value.ParamType)
                    throw new TypeException(FormatTypeError(
                        $"setter for '{stmt.FieldName}' expects a {FormatType(setterSig.Value.ParamType)}, not a {FormatType(setterValueType)}",
                        null, stmt.Line,
                        $"set '{stmt.FieldName}' to a {FormatType(setterValueType)}",
                        $"The setter for '{stmt.FieldName}' accepts a {FormatType(setterSig.Value.ParamType)}."));
                CheckRegionStore(stmt.Value, InferType(stmt.Value), ContainerDepthOf(stmt.Record), stmt.Line,
                    $"set field '{stmt.FieldName}' to a value from a shorter-lived rabbit region");
                return;
            }
            CheckObjectNamedSet(ot, stmt.FieldName, stmt.Value, stmt.Line);
            var objValueType = InferType(stmt.Value);
            CheckRegionStore(stmt.Value, objValueType, ContainerDepthOf(stmt.Record), stmt.Line,
                $"set field '{stmt.FieldName}' to a value from a shorter-lived rabbit region");
            return;
        }

        if (recordType is not RecordType rt)
            throw new TypeException(FormatTypeError(
                $"you're trying to set field '{stmt.FieldName}' on something that isn't a record or object",
                null, stmt.Line,
                $"set a named field on a {FormatType(recordType)}",
                "Only records and objects have named fields."));

        var field = rt.NamedFields.FirstOrDefault(f => f.Name == stmt.FieldName);
        if (field == default)
        {
            var hint = rt.NamedFields.Count > 0
                ? $"Available named fields: {string.Join(", ", rt.NamedFields.Select(f => f.Name))}."
                : "This record has no named fields.";
            throw new TypeException(FormatTypeError(
                $"this record has no field named '{stmt.FieldName}'",
                null, stmt.Line,
                $"set field '{stmt.FieldName}'",
                hint));
        }

        var valueType = InferType(stmt.Value);
        if (valueType != null && valueType != field.Type)
            throw new TypeException(FormatTypeError(
                $"field '{stmt.FieldName}' holds a {FormatType(field.Type)}, not a {FormatType(valueType)}",
                null, stmt.Line,
                $"set field '{stmt.FieldName}' to a {FormatType(valueType)}",
                $"Field '{stmt.FieldName}' has type {FormatType(field.Type)}."));

        // Region invariant: field value cannot outlive the record's rabbit region.
        CheckRegionStore(stmt.Value, valueType, ContainerDepthOf(stmt.Record), stmt.Line,
            $"set field '{stmt.FieldName}' to a value from a shorter-lived rabbit region");
    }

    private RecordType InferRecordLiteral(RecordLiteral lit)
    {
        var positionalTypes = new List<CufetType>();
        foreach (var field in lit.PositionalFields)
        {
            var t = InferType(field);
            if (t == null)
                throw new TypeException(FormatTypeError(
                    "the type of a positional record field can't be determined",
                    null, lit.Line,
                    "use an expression whose type can't be inferred as a record field",
                    "Start with a literal value or a defined variable so the field type is clear."));
            positionalTypes.Add(t);
        }

        var namedFields = new List<(string Name, CufetType Type)>();
        foreach (var (name, valueExpr) in lit.NamedFields)
        {
            if (namedFields.Any(f => f.Name == name))
                throw new TypeException(FormatTypeError(
                    $"the record has two fields both named '{name}'",
                    null, lit.Line,
                    $"define a record with duplicate field name '{name}'",
                    "Each named field must have a unique name."));
            var t = InferType(valueExpr);
            if (t == null)
                throw new TypeException(FormatTypeError(
                    $"the type of field '{name}' can't be determined",
                    null, lit.Line,
                    "use an expression whose type can't be inferred as a record field",
                    "Start with a literal value or a defined variable so the field type is clear."));
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
                _ => throw new TypeException(FormatTypeError(
                    $"a mapping only has 'key' and 'value' fields",
                    null, rna.Line,
                    $"access field '{rna.FieldName}' on a mapping",
                    "Use 'the key of mapping' or 'the value of mapping'."))
            };
        }

        // Exception values expose only 'message' (text).
        if (recordType is ExceptionMarkerType)
        {
            return rna.FieldName switch
            {
                "message" => CufetType.Text,
                _ => throw new TypeException(FormatTypeError(
                    "an exception only has a 'message' field",
                    null, rna.Line,
                    $"access field '{rna.FieldName}' on an exception",
                    "Use 'the message of the exception'."))
            };
        }

        // Failure values expose 'message' (text) and 'category' (voidable text).
        if (recordType is FailureMarkerType)
        {
            return rna.FieldName switch
            {
                "message"  => CufetType.Text,
                "category" => new VoidableType(CufetType.Text),
                _ => throw new TypeException(FormatTypeError(
                    "a failure only has 'message' and 'category' fields",
                    null, rna.Line,
                    $"access field '{rna.FieldName}' on a failure",
                    "Use 'the message of the failure' or 'the category of the failure'."))
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
            throw new TypeException(FormatTypeError(
                $"'{ot.Name}' has no field or getter named '{rna.FieldName}'",
                null, rna.Line,
                $"access field '{rna.FieldName}'",
                available.Length > 0
                    ? $"Available: {available}."
                    : $"'{ot.Name}' has no named fields or getters."));
        }

        if (recordType is not RecordType rt)
            throw new TypeException(FormatTypeError(
                $"you're trying to access field '{rna.FieldName}' on something that isn't a record or object",
                null, rna.Line,
                $"access a named field of a {FormatType(recordType)}",
                "Only records and objects have named fields."));

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
            throw new TypeException(FormatTypeError(
                $"this record has no field named '{rna.FieldName}'",
                null, rna.Line,
                $"access field '{rna.FieldName}'",
                fix));
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
