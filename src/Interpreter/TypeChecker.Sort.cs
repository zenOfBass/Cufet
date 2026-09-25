namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Sort ──────────────────────────────────────────────────────────────────

    private CufetType? InferSort(SortExpression sort)
    {
        // Rewritten on every pass, never merely set — see SortExpression.KeyFunction.
        sort.KeyFunction = null;

        var seriesType = InferType(sort.Series);
        if (seriesType == null) return null;

        // ★ A chase sorts as the collection it is, and gives back a chase — the same shape a
        // series keeps. Its elements are one-character texts, so only the NATURAL order applies;
        // there is no field to sort by, and no key function either.
        if (seriesType is ChaseType)
        {
            if (sort.ByField != null || sort.ByLambda != null)
                throw TypeError(
                    "a chase sorts its characters in their natural order only",
                    null, sort.Line, sort.Column,
                    sort.ByField != null ? $"sort a chase by '{sort.ByField}'" : "sort a chase by a function",
                    $"Write '{FormatExpr(sort.Series)} sorted' for the characters in order.");
            return ChaseType.Instance;
        }

        if (seriesType is not SeriesType st)
            throw TypeError(
                "'sorted' works on series and chases only",
                null, sort.Line, sort.Column,
                $"sort a {FormatType(seriesType)}",
                "Only a series or a chase can be sorted. Maps, records, and other types cannot.");

        // 'sorted by a function given (…): … Done' — a lambda can only be a key function.
        if (sort.ByLambda != null)
        {
            var lambdaType = InferType(sort.ByLambda);
            CheckSortKeyFunction(lambdaType, "the function", st.ElementType, sort.Line, sort.Column);
            return st;
        }

        if (sort.ByField == null)
        {
            // Natural sort: element type must be number or text.
            if (st.ElementType != CufetType.Number && st.ElementType != CufetType.Text)
                throw TypeError(
                    $"a series of {FormatTypePlural(st.ElementType)} has no natural order",
                    null, sort.Line, sort.Column,
                    $"sort a series of {FormatTypePlural(st.ElementType)} without specifying a field",
                    $"Sort by a named field instead: '{FormatExpr(sort.Series)} sorted by the <field-name>', " +
                    $"or by a function that gives back a number or text: '{FormatExpr(sort.Series)} sorted by <function-name>'.");
            return st;
        }

        // 'sorted by <name>': the name is a FIELD of the element or a FUNCTION in scope.
        var name = sort.ByField;
        CufetType? fieldType = st.ElementType switch
        {
            RecordType rt => rt.NamedFields.FirstOrDefault(f => f.Name == name) is var f && f != default ? f.Type : null,
            ObjectType ot => FindFieldInOtOrPromoted(ot, name),
            _             => null,
        };
        var functionType = TryLookup(name, out var info) && info.Type is FunctionType ft ? ft : null;

        // ★ Both is refused, never settled by a preference. The language does not decide which of
        // two readings wins — it refuses them, as it does two overlapping versions of a function —
        // and a silent preference here would change which one a program sorts by the day someone
        // adds a same-named field or function somewhere else.
        if (fieldType != null && functionType != null)
            throw TypeError(
                $"'{name}' is both a field of {FormatTypePlural(st.ElementType)} and a function, so 'sorted by {name}' could mean either",
                null, sort.Line, sort.Column,
                $"sort by '{name}'",
                "Rename one of them. Or, to sort by the function, write it out: " +
                $"'sorted by a function given (the {FormatType(st.ElementType)} value): Return cast {name} on (value). Done'.");

        if (fieldType != null)
        {
            CheckSortFieldType(fieldType, name, sort.Line, sort.Column);
            return st;
        }

        if (functionType != null)
        {
            CheckSortKeyFunction(functionType, $"'{name}'", st.ElementType, sort.Line, sort.Column);
            sort.KeyFunction = new VariableReference(name, sort.Line, sort.Column);
            return st;
        }

        if (st.ElementType is RecordType noFieldRecord)
            throw TypeError(
                $"this record has no field named '{name}', and there is no function by that name",
                null, sort.Line, sort.Column,
                $"sort by '{name}'",
                noFieldRecord.NamedFields.Count > 0
                    ? $"Available named fields: {string.Join(", ", noFieldRecord.NamedFields.Select(nf => nf.Name))}."
                    : "This record has no named fields.");

        if (st.ElementType is ObjectType noFieldObject)
            throw TypeError(
                $"'{noFieldObject.Name}' has no field named '{name}', and there is no function by that name",
                null, sort.Line, sort.Column,
                $"sort by '{name}'",
                GetAllNamedFields(noFieldObject).Count > 0
                    ? $"Available named fields: {string.Join(", ", GetAllNamedFields(noFieldObject).Select(nf => nf.FieldName))}."
                    : $"'{noFieldObject.Name}' has no named fields.");

        // ★ A name that IS bound, just not to a function, is a different mistake from a typo, and
        // "there is no function named 'limit'" of a program that defines `limit` reads as false.
        var what = TryLookup(name, out var bound)
            ? $"'{name}' is a {FormatType(bound.Type)}, not a function"
            : $"there is no function named '{name}'";
        throw TypeError(
            $"{what}, and a series of {FormatTypePlural(st.ElementType)} has no fields to sort by",
            null, sort.Line, sort.Column,
            $"sort a series of {FormatTypePlural(st.ElementType)} by '{name}'",
            "Name a function that takes one element and gives back a number or text, or write one in place: " +
            $"'sorted by a function given (the {FormatType(st.ElementType)} value): … Done'.");
    }

    // A key function takes one element and gives back something with a natural order.
    private void CheckSortKeyFunction(CufetType? type, string what, CufetType elementType, int line, int col)
    {
        if (type is not FunctionType ft)
            throw TypeError(
                $"{what} is not a function",
                null, line, col,
                $"sort by {what}",
                "Sort by a field of the elements, or by a function that takes one element.");

        // ⚠ EXACTLY the element type, not anything it widens into. The compiled sort hands each
        // element to the function as it is stored, with no argument slot to widen it through —
        // so a `voidable text` parameter taking a text would pass the checker and be refused by gcc.
        if (ft.ParameterTypes.Count != 1 || !ft.ParameterTypes[0].Equals(elementType))
            throw TypeError(
                $"a key function must take exactly one {FormatType(elementType)}",
                null, line, col,
                $"sort a series of {FormatTypePlural(elementType)} by {what}, which is a {FormatType(ft)}",
                $"Give it one parameter of type {FormatType(elementType)}: it is called once on each element.");

        if (ft.ReturnType != CufetType.Number && ft.ReturnType != CufetType.Text)
            throw TypeError(
                $"a key function must give back a number or text",
                null, line, col,
                $"sort by {what}, which gives back {(ft.ReturnType == null ? "nothing" : FormatType(ft.ReturnType))}",
                "Sort keys must be numbers (ascending) or text (alphabetical).");
    }

    private void CheckSortFieldType(CufetType fieldType, string fieldName, int line, int col)
    {
        if (fieldType != CufetType.Number && fieldType != CufetType.Text)
            throw TypeError(
                $"field '{fieldName}' has type {FormatType(fieldType)}, which has no natural order",
                null, line, col,
                $"sort by a {FormatType(fieldType)} field",
                "Sort keys must be numbers (ascending) or text (alphabetical). Use a different field.");
    }
}
