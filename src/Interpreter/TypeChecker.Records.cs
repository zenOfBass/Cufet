namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    private void CheckRecordNamedSet(RecordNamedSetStatement stmt)
    {
        var recordType = InferType(stmt.Record);
        if (recordType == null) return;

        if (recordType is ObjectType ot)
        {
            CheckObjectNamedSet(ot, stmt.FieldName, stmt.Value, stmt.Line);
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

        // Named field access also works on objects (the <name> of <object>).
        // Includes promoted fields from embedded types and the embed handle itself.
        if (recordType is ObjectType ot)
        {
            var found = FindFieldInOtOrPromoted(ot, rna.FieldName);
            if (found != null) return found;
            var allFields = GetAllNamedFields(ot);
            throw new TypeException(FormatTypeError(
                $"'{ot.Name}' has no field named '{rna.FieldName}'",
                null, rna.Line,
                $"access field '{rna.FieldName}'",
                allFields.Count > 0
                    ? $"Available fields: {string.Join(", ", allFields.Select(f => $"'{f.FieldName}'"))}."
                    : $"'{ot.Name}' has no named fields."));
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
