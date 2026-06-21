namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Sort ──────────────────────────────────────────────────────────────────

    private CufetType? InferSort(SortExpression sort)
    {
        var seriesType = InferType(sort.Series);
        if (seriesType == null) return null;

        if (seriesType is not SeriesType st)
            throw new TypeException(FormatTypeError(
                "'sorted' works on series only",
                null, sort.Line,
                $"sort a {FormatType(seriesType)}",
                "Only series can be sorted. Maps, records, and other types don't support 'sorted'."));

        if (sort.ByField == null)
        {
            // Natural sort: element type must be number or text.
            if (st.ElementType != CufetType.Number && st.ElementType != CufetType.Text)
                throw new TypeException(FormatTypeError(
                    $"a series of {FormatTypePlural(st.ElementType)} has no natural order",
                    null, sort.Line,
                    $"sort a series of {FormatTypePlural(st.ElementType)} without specifying a field",
                    $"Sort by a named field instead: '{FormatExpr(sort.Series)} sorted by the <field-name>'."));
            return st;
        }

        // 'sorted by <field>': element must be a record or object with that orderable field.
        if (st.ElementType is RecordType rt)
        {
            var field = rt.NamedFields.FirstOrDefault(f => f.Name == sort.ByField);
            if (field == default)
                throw new TypeException(FormatTypeError(
                    $"this record has no field named '{sort.ByField}'",
                    null, sort.Line,
                    $"sort by '{sort.ByField}'",
                    rt.NamedFields.Count > 0
                        ? $"Available named fields: {string.Join(", ", rt.NamedFields.Select(f => f.Name))}."
                        : "This record has no named fields."));
            CheckSortFieldType(field.Type, sort.ByField, sort.Line);
            return st;
        }

        if (st.ElementType is ObjectType ot)
        {
            var fieldType = FindFieldInOtOrPromoted(ot, sort.ByField);
            if (fieldType == null)
                throw new TypeException(FormatTypeError(
                    $"'{ot.Name}' has no field named '{sort.ByField}'",
                    null, sort.Line,
                    $"sort by '{sort.ByField}'",
                    GetAllNamedFields(ot).Count > 0
                        ? $"Available named fields: {string.Join(", ", GetAllNamedFields(ot).Select(f => f.FieldName))}."
                        : $"'{ot.Name}' has no named fields."));
            CheckSortFieldType(fieldType, sort.ByField, sort.Line);
            return st;
        }

        throw new TypeException(FormatTypeError(
            $"'sorted by' requires a series of records or objects",
            null, sort.Line,
            $"sort a series of {FormatTypePlural(st.ElementType)} by field '{sort.ByField}'",
            "Use 'sorted by <field>' only with series of records or objects that have named fields."));
    }

    private void CheckSortFieldType(CufetType fieldType, string fieldName, int line)
    {
        if (fieldType != CufetType.Number && fieldType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                $"field '{fieldName}' has type {FormatType(fieldType)}, which has no natural order",
                null, line,
                $"sort by a {FormatType(fieldType)} field",
                "Sort keys must be numbers (ascending) or text (alphabetical). Use a different field."));
    }
}
