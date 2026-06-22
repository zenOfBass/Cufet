using Cufet.Lexer;

namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    private void CheckForEach(ForEachStatement forEach)
    {
        var inferred = InferType(forEach.Series);
        if (inferred == null)
            return; // unknown type — runtime catches; skip body to avoid cascading false positives

        // Map iteration: bind iterator to MappingType pseudo-record (key/value fields).
        if (inferred is MapType mapType)
        {
            var iterKey = forEach.IteratorName ?? "it";
            EnterScope();
            Scope[iterKey] = new TypeInfo(new MappingType(mapType.KeyType, mapType.ValueType), forEach.Series, forEach.Line);
            try { foreach (var s in forEach.Body) CheckStatement(s); }
            finally { ExitScope(); }
            return;
        }

        if (inferred is not SeriesType seriesType)
            throw new TypeException(FormatTypeError(
                $"{FormatExpr(forEach.Series)} holds {FormatTypePlural(inferred)}",
                $"It evaluates to {FormatTypePlural(inferred)}, not a series",
                forEach.Line,
                "loop over it as if it were a series",
                "Only series and maps can be looped over. Define a series if that's what you need."));

        var iterKey2 = forEach.IteratorName ?? "it";
        // Iterator inherits the series's rabbit depth — elements of a rabbit-scoped
        // series live in the same region and carry the same lifetime constraint.
        int seriesDepth = forEach.Series is VariableReference vrSeries
            && TryLookup(vrSeries.Name, out var seriesTi)
            ? seriesTi.RabbitDepth : _rabbitDepth;
        EnterScope();
        Scope[iterKey2] = new TypeInfo(seriesType.ElementType, forEach.Series, forEach.Line, RabbitDepth: seriesDepth);
        try
        {
            foreach (var s in forEach.Body)
                CheckStatement(s);
        }
        finally { ExitScope(); }
    }

    private void CheckSeriesAdd(SeriesAddStatement add)
    {
        if (add.AfterIndex != null) CheckIndex(add.AfterIndex, add.Line);
        if (!TryLookup(add.SeriesName, out var seriesInfo)) return;
        if (seriesInfo.Type is not SeriesType seriesType) return;

        var valueType = InferType(add.Value);
        if (valueType != null && !IsAssignable(seriesType.ElementType, valueType))
            throw new TypeException(FormatTypeError(
                $"'{add.SeriesName}' holds {FormatTypePlural(seriesType.ElementType)}",
                $"You defined it on line {seriesInfo.EstablishingLine} as a series of {FormatTypePlural(seriesType.ElementType)}, so it can only accept {FormatTypePlural(seriesType.ElementType)}",
                add.Line,
                $"add a {FormatType(valueType)} value to it",
                $"Change the value to a {FormatType(seriesType.ElementType)}, or define a separate series that holds {FormatTypePlural(valueType)}."));

        CheckRegionStore(add.Value, valueType, seriesInfo.RabbitDepth, add.Line,
            $"add a rabbit-scoped value to '{add.SeriesName}' which lives in a longer-lived region");
    }

    private void CheckSeriesRemoveValue(SeriesRemoveValueStatement removeVal)
    {
        if (!TryLookup(removeVal.SeriesName, out var seriesInfo)) return;

        // Map remove: "remove key from map" — key must match map's key type.
        if (seriesInfo.Type is MapType mapType)
        {
            var keyType = InferType(removeVal.Value);
            if (keyType != null && !IsAssignable(mapType.KeyType, keyType))
                throw new TypeException(FormatTypeError(
                    $"'{removeVal.SeriesName}' uses {FormatType(mapType.KeyType)} keys",
                    $"You defined it on line {seriesInfo.EstablishingLine} as a map from {FormatType(mapType.KeyType)} to {FormatType(mapType.ValueType)}",
                    removeVal.Line,
                    $"remove using a {FormatType(keyType)} key",
                    $"Keys in this map are {FormatTypePlural(mapType.KeyType)}."));
            return;
        }

        if (seriesInfo.Type is not SeriesType seriesType) return;

        var valueType = InferType(removeVal.Value);
        if (valueType != null && valueType != seriesType.ElementType)
            throw new TypeException(FormatTypeError(
                $"'{removeVal.SeriesName}' holds {FormatTypePlural(seriesType.ElementType)}",
                $"You defined it on line {seriesInfo.EstablishingLine} as a series of {FormatTypePlural(seriesType.ElementType)}, so only {FormatTypePlural(seriesType.ElementType)} can be removed from it",
                removeVal.Line,
                $"remove a {FormatType(valueType)} value from it",
                $"Make sure the value you're removing is a {FormatType(seriesType.ElementType)}."));
    }

    private void CheckSeriesSet(SeriesSetStatement seriesSet)
    {
        if (seriesSet.Index != null) CheckIndex(seriesSet.Index, seriesSet.Line);
        if (!TryLookup(seriesSet.SeriesName, out var seriesInfo)) return;

        if (seriesInfo.Type is SeriesType seriesType)
        {
            var valueType = InferType(seriesSet.Value);
            if (valueType != null && valueType != seriesType.ElementType)
                throw new TypeException(FormatTypeError(
                    $"'{seriesSet.SeriesName}' holds {FormatTypePlural(seriesType.ElementType)}",
                    $"You defined it on line {seriesInfo.EstablishingLine} as a series of {FormatTypePlural(seriesType.ElementType)}, so its items can only be set to {FormatTypePlural(seriesType.ElementType)}",
                    seriesSet.Line,
                    $"set an item to a {FormatType(valueType)} value",
                    $"Change the new value to a {FormatType(seriesType.ElementType)}."));
            CheckRegionStore(seriesSet.Value, valueType, seriesInfo.RabbitDepth, seriesSet.Line,
                $"set an item in '{seriesSet.SeriesName}' to a rabbit-scoped value from a shorter-lived region");
            return;
        }

        if (seriesInfo.Type is ObjectType ot)
        {
            if (seriesSet.Index == null)
                throw new TypeException(FormatTypeError(
                    "'last' doesn't work on objects",
                    null, seriesSet.Line,
                    "use 'last' on an object",
                    "Use a position like 'the first of ...' or a field name like 'alice's name'."));

            if (seriesSet.Index is NumberLiteral { Value: var ov })
            {
                var idx     = (int)ov;
                var display = $"'{seriesSet.SeriesName}'";
                var allPos  = GetAllPositionalTypes(ot);
                if (idx < 1 || idx > allPos.Count)
                    throw new TypeException(FormatTypeError(
                        allPos.Count == 0
                            ? $"{display} has no positional fields"
                            : $"{display} has {allPos.Count} positional field(s) — there is no position {idx}",
                        null, seriesSet.Line,
                        $"set position {idx}",
                        allPos.Count == 0
                            ? $"Object '{ot.Name}' has no positional fields."
                            : $"Positions run 1 through {allPos.Count}."));

                var fieldType = allPos[idx - 1];
                var valueType = InferType(seriesSet.Value);
                if (valueType != null && valueType != fieldType)
                    throw new TypeException(FormatTypeError(
                        $"position {idx} of {display} holds a {FormatType(fieldType)}, not a {FormatType(valueType)}",
                        null, seriesSet.Line,
                        $"set position {idx} to a {FormatType(valueType)}",
                        $"Position {idx} has type {FormatType(fieldType)}."));
            }
            return;
        }

        if (seriesInfo.Type is RecordType rt)
        {
            if (seriesSet.Index == null)
                throw new TypeException(FormatTypeError(
                    "'last' doesn't work on records",
                    null, seriesSet.Line,
                    "use 'last' on a record",
                    "Use a position like 'the first of ...' or a field name like 'the city of ...'."));

            if (seriesSet.Index is NumberLiteral { Value: var v })
            {
                var idx = (int)v;
                var display = $"'{seriesSet.SeriesName}'";
                if (idx < 1 || idx > rt.PositionalTypes.Count)
                    throw new TypeException(FormatTypeError(
                        rt.PositionalTypes.Count == 0
                            ? $"{display} has no positional fields"
                            : $"{display} has {rt.PositionalTypes.Count} positional field(s) — there is no position {idx}",
                        null, seriesSet.Line,
                        $"set position {idx}",
                        rt.PositionalTypes.Count == 0
                            ? "This record has no positional fields."
                            : $"Positions run 1 through {rt.PositionalTypes.Count}."));

                var fieldType = rt.PositionalTypes[idx - 1];
                var valueType = InferType(seriesSet.Value);
                if (valueType != null && valueType != fieldType)
                    throw new TypeException(FormatTypeError(
                        $"position {idx} of {display} holds a {FormatType(fieldType)}, not a {FormatType(valueType)}",
                        null, seriesSet.Line,
                        $"set position {idx} to a {FormatType(valueType)}",
                        $"Position {idx} has type {FormatType(fieldType)}."));
            }
        }
    }

    private void CheckSeriesRemoveAt(SeriesRemoveAtStatement removeAt)
    {
        if (removeAt.Index != null) CheckIndex(removeAt.Index, removeAt.Line);
    }

    private CufetType InferSeriesLiteral(SeriesLiteral lit)
    {
        // When the annotation is a union type, elements don't need to be homogeneous —
        // check each element is assignable to the union instead.
        if (lit.Annotation is UnionType unionAnnotation)
        {
            foreach (var elem in lit.Elements)
            {
                var elemType = InferType(elem);
                if (elemType != null && !IsAssignable(unionAnnotation, elemType))
                    throw new TypeException(FormatTypeError(
                        $"this item doesn't fit in a {FormatType(unionAnnotation)} collection",
                        $"{FormatType(elemType)} is not one of the allowed types",
                        lit.Line,
                        $"add a {FormatType(elemType)} item",
                        $"Only {FormatType(unionAnnotation)} values are allowed in this collection."));
            }
            return new SeriesType(unionAnnotation);
        }

        CufetType? inferred = null;
        for (int i = 0; i < lit.Elements.Count; i++)
        {
            var elemType = InferType(lit.Elements[i]);
            if (elemType == null) continue;
            if (inferred == null)
            {
                inferred = elemType;
            }
            else if (inferred != elemType)
            {
                throw new TypeException(FormatTypeError(
                    "every item in a series must be the same type",
                    $"The first item is a {FormatType(inferred)}, so all items must be {FormatTypePlural(inferred)}",
                    lit.Line,
                    $"make item {i + 1} a {FormatType(elemType)}",
                    $"Remove the mismatched item, or define two separate series — one for {FormatTypePlural(inferred)} and one for {FormatTypePlural(elemType)}."));
            }
        }

        if (lit.Annotation != null)
        {
            if (inferred != null && inferred != lit.Annotation)
                throw new TypeException(FormatTypeError(
                    $"you said this is a series of {FormatTypePlural(lit.Annotation)}",
                    $"That annotation fixes the element type as {FormatType(lit.Annotation)}",
                    lit.Line,
                    $"put a {FormatType(inferred)} item in it",
                    "Fix the annotation to match the elements, or change the elements to match the annotation."));
            return new SeriesType(lit.Annotation);
        }

        if (inferred == null)
            throw new TypeException(FormatTypeError(
                "an empty series has no items to infer its type from",
                null,
                lit.Line,
                "define an empty series without saying what type of items it will hold",
                "Add an annotation to declare the element type: a series of numbers (), a series of text (), or a series of facts ()."));

        return new SeriesType(inferred);
    }

    private CufetType? InferSeriesAccess(SeriesAccess acc)
    {
        if (acc.Index != null) CheckIndex(acc.Index, acc.Line);
        var targetType = InferType(acc.Target);
        if (targetType == null) return null;

        if (targetType is SeriesType st) return st.ElementType;

        if (targetType is RecordType rt)
        {
            if (acc.Index == null) // "last" — not meaningful for records
                throw new TypeException(FormatTypeError(
                    $"'last' doesn't work on records",
                    null, acc.Line,
                    "use 'last' on a record",
                    "Use a position like 'the first of ...' or a field name like 'the city of ...'."));
            if (acc.Index is NumberLiteral { Value: var v })
            {
                var idx = (int)v;
                var displayName = acc.Target is VariableReference vr ? $"'{vr.Name}'" : "this record";
                if (idx < 1 || idx > rt.PositionalTypes.Count)
                    throw new TypeException(FormatTypeError(
                        rt.PositionalTypes.Count == 0
                            ? $"{displayName} has no positional fields"
                            : $"{displayName} has {rt.PositionalTypes.Count} positional field(s) — there is no position {idx}",
                        null, acc.Line,
                        $"access position {idx}",
                        rt.PositionalTypes.Count == 0
                            ? "This record has no positional fields. Access fields by name instead."
                            : $"Positions run 1 through {rt.PositionalTypes.Count}."));
                return rt.PositionalTypes[idx - 1];
            }
            // Dynamic index — can't check statically; runtime handles it.
            return null;
        }

        if (targetType is ObjectType ot)
        {
            if (acc.Index == null)
                throw new TypeException(FormatTypeError(
                    $"'last' doesn't work on objects",
                    null, acc.Line,
                    "use 'last' on an object",
                    "Use a position like 'the first of ...' or a field name like 'the city of ...'."));
            if (acc.Index is NumberLiteral { Value: var v })
            {
                var idx = (int)v;
                var allPos = GetAllPositionalTypes(ot);
                if (idx < 1 || idx > allPos.Count)
                    throw new TypeException(FormatTypeError(
                        allPos.Count == 0
                            ? $"'{ot.Name}' has no positional fields"
                            : $"'{ot.Name}' has {allPos.Count} positional field(s) — there is no position {idx}",
                        null, acc.Line,
                        $"access position {idx}",
                        allPos.Count == 0
                            ? $"'{ot.Name}' has no positional fields. Access fields by name instead."
                            : $"Positions run 1 through {allPos.Count}."));
                return allPos[idx - 1];
            }
            return null;
        }

        return null;
    }

    private CufetType InferSeriesLength(SeriesLength sl)
    {
        if (TryLookup(sl.SeriesName, out var info) && info.Type is RecordType)
            throw new TypeException(FormatTypeError(
                $"'the number of' works on series, not records",
                null, 0,
                $"get the number of items in '{sl.SeriesName}' (a record)",
                "Records don't have a length. Access individual fields by name or position."));
        return CufetType.Number;
    }

    private CufetType InferRangeExpr(RangeExpression re)
    {
        var startType = InferType(re.Start);
        var endType   = InferType(re.End);

        if (startType != null && startType != CufetType.Number)
            throw new TypeException(FormatTypeError(
                "range start must be a number",
                null,
                re.Line,
                $"use a {FormatType(startType)} as the start of a range",
                "Both ends of a range must be numbers. For example: range 1 to 100."));

        if (endType != null && endType != CufetType.Number)
            throw new TypeException(FormatTypeError(
                "range end must be a number",
                null,
                re.Line,
                $"use a {FormatType(endType)} as the end of a range",
                "Both ends of a range must be numbers. For example: range 1 to 100."));

        if (re.Step != null)
        {
            var stepType = InferType(re.Step);
            if (stepType != null && stepType != CufetType.Number)
                throw new TypeException(FormatTypeError(
                    "range step must be a number",
                    null,
                    re.Line,
                    $"count by a {FormatType(stepType)}",
                    "The step in 'counting by <step>' must be a number. For example: range 1 to 10 counting by 2."));

            var literalStep = TryGetLiteralNumber(re.Step);
            if (literalStep == 0)
                throw new TypeException(FormatTypeError(
                    "'counting by 0' never makes progress",
                    null,
                    re.Line,
                    "count by 0",
                    "Use a step greater than 0. The range's direction already comes from start vs. end."));
            if (literalStep < 0)
                throw new TypeException(FormatTypeError(
                    "the step in 'counting by' must be positive",
                    null,
                    re.Line,
                    $"count by {literalStep}",
                    "Direction comes from start vs. end, not the step's sign. Use a positive step, e.g. 'range 10 to 1 counting by 2' already descends."));
        }

        return new SeriesType(CufetType.Number);
    }

    // Returns the literal numeric value of a number literal or a negated number literal
    // (e.g. NumberLiteral(2) or UnaryExpression(Minus, NumberLiteral(2))), else null.
    private static decimal? TryGetLiteralNumber(IExpression expr) => expr switch
    {
        NumberLiteral nl => nl.Value,
        UnaryExpression { Op: TokenType.Minus, Operand: NumberLiteral nl } => -nl.Value,
        _ => null,
    };
}
