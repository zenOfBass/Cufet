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
            Scope[iterKey] = new TypeInfo(new MappingType(ResolveParamType(mapType.KeyType), ResolveParamType(mapType.ValueType)), forEach.Series, forEach.Line);
            try { CheckBlock(forEach.Body); }
            finally { ExitScope(); }
            return;
        }

        // A stash is not a series — it PRODUCES its values one resumption at a time — so it is not
        // looped over by indexing but by draining, and the loop stands for the drain people used to
        // write by hand. Checking the drain rather than the `For each` is the point: what is checked
        // here is the very statement both backends will run, so there is nothing to keep in step.
        if (inferred is StashType)
        {
            // ⚠ Asked HERE, outside the scopes the drain runs in. A `For each` binds rather than
            // declares — `For each value in <series>` quietly shadows an outer `value` and always
            // has — so the drain's `Define` must be spelled as the shadow it is, or the stash form
            // would refuse what the series form allows. Inside a burying body the shadow is refused,
            // and correctly: linearisation flattens the scopes, so the two would land on one slot.
            var drain = StashDrainLoop(forEach, TryLookup(forEach.IteratorName ?? "it", out _));
            _stashDrains[forEach] = drain;
            EnterScope();
            try { CheckStatement(drain); }
            finally { ExitScope(); }
            return;
        }

        if (inferred is not SeriesType seriesType)
            throw TypeError(
                $"{FormatExpr(forEach.Series)} holds {FormatTypePlural(inferred)}",
                $"It evaluates to {FormatTypePlural(inferred)}, not a series",
                forEach.Line, forEach.Column,
                "loop over it as if it were a series",
                "Only series, maps and stashes can be looped over. Define a series if that's what you need.");

        var iterKey2 = forEach.IteratorName ?? "it";
        // What StashTransform needs to rewrite this loop into an indexed one: the source's type (to
        // give its slot an element type) and the iterator's (it becomes a slot of its own).
        _stashFacts.ForEachSources[(forEach.Line, forEach.Column)] = seriesType;
        RecordStashLocal(iterKey2, ResolveParamType(seriesType.ElementType), forEach.Line, forEach.Column);
        // Iterator inherits the series's rabbit depth — elements of a rabbit-scoped
        // series live in the same region and carry the same lifetime constraint.
        int seriesDepth = forEach.Series is VariableReference vrSeries
            && TryLookup(vrSeries.Name, out var seriesTi)
            ? seriesTi.RabbitDepth : _rabbitDepth;
        EnterScope();
        Scope[iterKey2] = new TypeInfo(ResolveParamType(seriesType.ElementType), forEach.Series, forEach.Line, RabbitDepth: seriesDepth);
        try
        {
            CheckBlock(forEach.Body);
        }
        finally { ExitScope(); }
    }

    /// <summary>
    /// The loop a `For each &lt;name&gt; in &lt;stash&gt;` stands for: take one, stop when the stash is
    /// spent, otherwise run the body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ★ This is the drain people wrote by hand — three times in the stash example alone — and it is
    /// built out of nothing new. `unbury` already lowers to calling the stash's closure, and a
    /// spent stash already answers void, so the whole loop is statements both backends have run
    /// since long before stashes existed. Neither backend learns anything for this feature.
    /// </para>
    /// <para>
    /// ★ `Stop` and `Skip` need no protecting from each other. The body's `Stop` ends this loop and
    /// its `Skip` takes the next value, which is exactly what they mean in a `For each` — the
    /// desugaring's own `Stop` fires before the body is reached, so the two can never meet.
    /// </para>
    /// <para>
    /// ⚠ Built here rather than after checking, because it is what gets CHECKED: narrowing past the
    /// void test is what gives the body a plain `T`, and a burying function's hoisting learns the
    /// iterator's slot type from this Define like any other local. A drain synthesised after the
    /// checker had gone home would have to be trusted instead.
    /// </para>
    /// </remarks>
    private static RepeatUntilStatement StashDrainLoop(ForEachStatement forEach, bool shadows)
    {
        var name = forEach.IteratorName ?? "it";
        int line = forEach.Line, column = forEach.Column;

        var take = new DefineStatement(
            name, new UnburyExpression(forEach.Series, line, column),
            Permanent: false, Shadow: shadows, line, column);

        // ⚠ `is not void` with the body in the ARM, not `is void` with a `Stop` and the body after
        // it. Narrowing here is arm-shaped — `TryGetNotVoidNarrowing` gives the plain `T` back
        // inside the arm it proves — and Cufet does not narrow a name for the REST of a block on
        // the strength of an early exit. Written the other way round, every body would see a
        // `voidable T` and `If value is greater than 7` would be refused.
        var alive = new IfStatement(
            [new ConditionArm(
                new BinaryExpression(
                    new VariableReference(name, line, column), TokenType.NotEqual,
                    new VoidLiteral(line, column), line, column),
                forEach.Body)],
            ElseBody: [new StopStatement()]);

        return new RepeatUntilStatement([take, alive], new BooleanLiteral(false, line, column));
    }

    private void CheckSeriesAdd(SeriesInsertStatement add)
    {
        if (add.AfterIndex != null) CheckIndex(add.AfterIndex, add.Line, add.Column);
        var containerType = InferType(add.Series);
        if (containerType == null) return;
        if (containerType is not SeriesType seriesType)
            throw TypeError(
                $"{FormatExpr(add.Series)} is not a series",
                $"It evaluates to {FormatTypePlural(containerType)}, which can't be inserted into",
                add.Line, add.Column,
                "insert into a non-series expression",
                "Only a series (like 'my-list' or 'one's cards') can be the target of 'Add ... to'.");

        var valueType = InferType(add.Value);
        if (valueType != null && !IsAssignable(seriesType.ElementType, valueType))
            throw TypeError(
                $"{FormatExpr(add.Series)} holds {FormatTypePlural(seriesType.ElementType)}",
                $"It is a series of {FormatTypePlural(seriesType.ElementType)}, so it can only accept {FormatTypePlural(seriesType.ElementType)}",
                add.Line, add.Column,
                $"add a {FormatType(valueType)} value to it",
                $"Change the value to a {FormatType(seriesType.ElementType)}, or define a separate series that holds {FormatTypePlural(valueType)}.");

        CheckRegionStore(add.Value, valueType, ContainerDepthOf(add.Series), add.Line, add.Column,
            $"add a rabbit-scoped value to a series in a longer-lived region");
        add.EscapeToDepth = EscapeDepthFor(add.Value, valueType, ContainerDepthOf(add.Series));
    }

    private void CheckSeriesRemoveValue(SeriesRemoveValueStatement removeVal)
    {
        var containerType = InferType(removeVal.Series);
        if (containerType == null) return;

        // Map remove: "remove key from map" — key must match map's key type.
        if (containerType is MapType mapType)
        {
            var keyType = InferType(removeVal.Value);
            if (keyType != null && !IsAssignable(mapType.KeyType, keyType))
                throw TypeError(
                    $"{FormatExpr(removeVal.Series)} uses {FormatType(mapType.KeyType)} keys",
                    $"It is a map from {FormatType(mapType.KeyType)} to {FormatType(mapType.ValueType)}",
                    removeVal.Line, removeVal.Column,
                    $"remove using a {FormatType(keyType)} key",
                    $"Keys in this map are {FormatTypePlural(mapType.KeyType)}.");
            return;
        }

        if (containerType is not SeriesType seriesType)
            throw TypeError(
                $"{FormatExpr(removeVal.Series)} is not a series or map",
                $"It evaluates to {FormatTypePlural(containerType)}, which can't have items removed",
                removeVal.Line, removeVal.Column,
                "remove from a non-series expression",
                "Only a series or map can be the target of 'Remove ... from'.");

        var valueType = InferType(removeVal.Value);
        if (valueType != null && valueType != seriesType.ElementType)
            throw TypeError(
                $"{FormatExpr(removeVal.Series)} holds {FormatTypePlural(seriesType.ElementType)}",
                $"It is a series of {FormatTypePlural(seriesType.ElementType)}, so only {FormatTypePlural(seriesType.ElementType)} can be removed from it",
                removeVal.Line, removeVal.Column,
                $"remove a {FormatType(valueType)} value from it",
                $"Make sure the value you're removing is a {FormatType(seriesType.ElementType)}.");
    }

    private void CheckSeriesSet(SeriesSetStatement seriesSet)
    {
        if (seriesSet.Index != null) CheckIndex(seriesSet.Index, seriesSet.Line, seriesSet.Column);
        var containerType = InferType(seriesSet.Series);
        if (containerType == null) return;

        if (containerType is SeriesType seriesType)
        {
            var valueType = InferType(seriesSet.Value);
            if (valueType != null && valueType != seriesType.ElementType)
                throw TypeError(
                    $"{FormatExpr(seriesSet.Series)} holds {FormatTypePlural(seriesType.ElementType)}",
                    $"It is a series of {FormatTypePlural(seriesType.ElementType)}, so its items can only be set to {FormatTypePlural(seriesType.ElementType)}",
                    seriesSet.Line, seriesSet.Column,
                    $"set an item to a {FormatType(valueType)} value",
                    $"Change the new value to a {FormatType(seriesType.ElementType)}.");
            CheckRegionStore(seriesSet.Value, valueType, ContainerDepthOf(seriesSet.Series), seriesSet.Line, seriesSet.Column,
                $"set an item in a series to a rabbit-scoped value from a shorter-lived region");
            return;
        }

        if (containerType is ObjectType ot)
        {
            if (seriesSet.Index == null)
                throw TypeError(
                    "'last' doesn't work on objects",
                    null, seriesSet.Line, seriesSet.Column,
                    "use 'last' on an object",
                    "Use a position like 'the first of ...' or a field name like 'alice's name'.");

            if (seriesSet.Index is NumberLiteral { Value: var ov })
            {
                var idx     = (int)ov;
                var display = FormatExpr(seriesSet.Series);
                var allPos  = GetAllPositionalTypes(ot);
                if (idx < 1 || idx > allPos.Count)
                    throw TypeError(
                        allPos.Count == 0
                            ? $"{display} has no positional fields"
                            : $"{display} has {allPos.Count} positional field(s) — there is no position {idx}",
                        null, seriesSet.Line, seriesSet.Column,
                        $"set position {idx}",
                        allPos.Count == 0
                            ? $"Object '{ot.Name}' has no positional fields."
                            : $"Positions run 1 through {allPos.Count}.");

                var fieldType = allPos[idx - 1];
                var valueType = InferType(seriesSet.Value);
                if (valueType != null && valueType != fieldType)
                    throw TypeError(
                        $"position {idx} of {display} holds a {FormatType(fieldType)}, not a {FormatType(valueType)}",
                        null, seriesSet.Line, seriesSet.Column,
                        $"set position {idx} to a {FormatType(valueType)}",
                        $"Position {idx} has type {FormatType(fieldType)}.");
            }
            return;
        }

        if (containerType is RecordType rt)
        {
            if (seriesSet.Index == null)
                throw TypeError(
                    "'last' doesn't work on records",
                    null, seriesSet.Line, seriesSet.Column,
                    "use 'last' on a record",
                    "Use a position like 'the first of ...' or a field name like 'the city of ...'.");

            if (seriesSet.Index is NumberLiteral { Value: var v })
            {
                var idx = (int)v;
                var display = FormatExpr(seriesSet.Series);
                if (idx < 1 || idx > rt.PositionalTypes.Count)
                    throw TypeError(
                        rt.PositionalTypes.Count == 0
                            ? $"{display} has no positional fields"
                            : $"{display} has {rt.PositionalTypes.Count} positional field(s) — there is no position {idx}",
                        null, seriesSet.Line, seriesSet.Column,
                        $"set position {idx}",
                        rt.PositionalTypes.Count == 0
                            ? "This record has no positional fields."
                            : $"Positions run 1 through {rt.PositionalTypes.Count}.");

                var fieldType = rt.PositionalTypes[idx - 1];
                var valueType = InferType(seriesSet.Value);
                if (valueType != null && valueType != fieldType)
                    throw TypeError(
                        $"position {idx} of {display} holds a {FormatType(fieldType)}, not a {FormatType(valueType)}",
                        null, seriesSet.Line, seriesSet.Column,
                        $"set position {idx} to a {FormatType(valueType)}",
                        $"Position {idx} has type {FormatType(fieldType)}.");
            }
            return;
        }

        throw TypeError(
            $"{FormatExpr(seriesSet.Series)} is not a series, object, or record",
            $"It evaluates to {FormatTypePlural(containerType)}, which can't have items assigned by position",
            seriesSet.Line, seriesSet.Column,
            "set an item in a non-series expression",
            "Only a series, object, or record can be the target of 'item N of ... becomes'.");
    }

    private void CheckSeriesRemoveAt(SeriesRemoveAtStatement removeAt)
    {
        if (removeAt.Index != null) CheckIndex(removeAt.Index, removeAt.Line, removeAt.Column);
        var containerType = InferType(removeAt.Series);
        if (containerType == null) return;
        if (containerType is not SeriesType)
            throw TypeError(
                $"{FormatExpr(removeAt.Series)} is not a series",
                $"It evaluates to {FormatTypePlural(containerType)}, which can't have items removed by position",
                removeAt.Line, removeAt.Column,
                "remove an item from a non-series expression",
                "Only a series can be the target of 'Remove first/last/item N from'.");
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
                    throw TypeError(
                        $"this item doesn't fit in a {FormatType(unionAnnotation)} collection",
                        $"{FormatType(elemType)} is not one of the allowed types",
                        lit.Line, lit.Column,
                        $"add a {FormatType(elemType)} item",
                        $"Only {FormatType(unionAnnotation)} values are allowed in this collection.");
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
                throw TypeError(
                    "every item in a series must be the same type",
                    $"The first item is a {FormatType(inferred)}, so all items must be {FormatTypePlural(inferred)}",
                    lit.Line, lit.Column,
                    $"make item {i + 1} a {FormatType(elemType)}",
                    $"Remove the mismatched item, or define two separate series — one for {FormatTypePlural(inferred)} and one for {FormatTypePlural(elemType)}.");
            }
        }

        if (lit.Annotation != null)
        {
            if (inferred != null && inferred != lit.Annotation)
                throw TypeError(
                    $"you said this is a series of {FormatTypePlural(lit.Annotation)}",
                    $"That annotation fixes the element type as {FormatType(lit.Annotation)}",
                    lit.Line, lit.Column,
                    $"put a {FormatType(inferred)} item in it",
                    "Fix the annotation to match the elements, or change the elements to match the annotation.");
            return new SeriesType(lit.Annotation);
        }

        if (inferred == null)
            throw TypeError(
                "an empty series has no items to infer its type from",
                null,
                lit.Line, lit.Column,
                "define an empty series without saying what type of items it will hold",
                "Add an annotation to declare the element type: a series of numbers (), a series of text (), or a series of facts ().");

        return new SeriesType(inferred);
    }

    private CufetType? InferSeriesAccess(SeriesAccess acc)
    {
        if (acc.Index != null) CheckIndex(acc.Index, acc.Line, acc.Column);
        var targetType = InferType(acc.Target);
        if (targetType == null) return null;

        if (targetType is SeriesType st) return st.ElementType;

        if (targetType is RecordType rt)
        {
            if (acc.Index == null) // "last" — not meaningful for records
                throw TypeError(
                    $"'last' doesn't work on records",
                    null, acc.Line, acc.Column,
                    "use 'last' on a record",
                    "Use a position like 'the first of ...' or a field name like 'the city of ...'.");
            if (acc.Index is NumberLiteral { Value: var v })
            {
                var idx = (int)v;
                var displayName = acc.Target is VariableReference vr ? $"'{vr.Name}'" : "this record";
                if (idx < 1 || idx > rt.PositionalTypes.Count)
                    throw TypeError(
                        rt.PositionalTypes.Count == 0
                            ? $"{displayName} has no positional fields"
                            : $"{displayName} has {rt.PositionalTypes.Count} positional field(s) — there is no position {idx}",
                        null, acc.Line, acc.Column,
                        $"access position {idx}",
                        rt.PositionalTypes.Count == 0
                            ? "This record has no positional fields. Access fields by name instead."
                            : $"Positions run 1 through {rt.PositionalTypes.Count}.");
                return rt.PositionalTypes[idx - 1];
            }
            // Dynamic index — can't check statically; runtime handles it.
            return null;
        }

        if (targetType is ObjectType ot)
        {
            if (acc.Index == null)
                throw TypeError(
                    $"'last' doesn't work on objects",
                    null, acc.Line, acc.Column,
                    "use 'last' on an object",
                    "Use a position like 'the first of ...' or a field name like 'the city of ...'.");
            if (acc.Index is NumberLiteral { Value: var v })
            {
                var idx = (int)v;
                var allPos = GetAllPositionalTypes(ot);
                if (idx < 1 || idx > allPos.Count)
                    throw TypeError(
                        allPos.Count == 0
                            ? $"'{ot.Name}' has no positional fields"
                            : $"'{ot.Name}' has {allPos.Count} positional field(s) — there is no position {idx}",
                        null, acc.Line, acc.Column,
                        $"access position {idx}",
                        allPos.Count == 0
                            ? $"'{ot.Name}' has no positional fields. Access fields by name instead."
                            : $"Positions run 1 through {allPos.Count}.");
                return allPos[idx - 1];
            }
            return null;
        }

        return null;
    }

    private CufetType InferSeriesLength(SeriesLength sl)
    {
        var containerType = InferType(sl.Series);
        if (containerType is RecordType)
            throw TypeError(
                $"'the number of' works on series, not records",
                null, sl.Line, sl.Column,
                $"get the number of items in {FormatExpr(sl.Series)} (a record)",
                "Records don't have a length. Access individual fields by name or position.");

        // ⚠ A map and a text each have their OWN word for this, and both used to fall through here
        // to a RUNTIME exception — "Expected a series for 'the number of'", thrown from the
        // evaluator with no line of the writer's program in it. The reverse direction has said the
        // helpful thing all along: `the size of <series>` is a check-time error that names
        // `the number of`. This is that sentence, pointed back the other way.
        if (containerType is MapType)
            throw TypeError(
                "'the number of' works on series, not maps",
                null, sl.Line, sl.Column,
                $"get the number of entries in {FormatExpr(sl.Series)} (a map)",
                "For maps, use 'the size of'. For text, use 'the length of'.");

        if (containerType is TextType)
            throw TypeError(
                "'the number of' works on series, not text",
                null, sl.Line, sl.Column,
                $"get the number of characters in {FormatExpr(sl.Series)} (a text)",
                "For text, use 'the length of'. For maps, use 'the size of'.");

        return CufetType.Number;
    }

    private CufetType InferRangeExpr(RangeExpression re)
    {
        var startType = InferType(re.Start);
        var endType   = InferType(re.End);

        if (startType != null && startType != CufetType.Number)
            throw TypeError(
                "range start must be a number",
                null,
                re.Line, re.Column,
                $"use a {FormatType(startType)} as the start of a range",
                "Both ends of a range must be numbers. For example: range 1 to 100.");

        if (endType != null && endType != CufetType.Number)
            throw TypeError(
                "range end must be a number",
                null,
                re.Line, re.Column,
                $"use a {FormatType(endType)} as the end of a range",
                "Both ends of a range must be numbers. For example: range 1 to 100.");

        if (re.Step != null)
        {
            var stepType = InferType(re.Step);
            if (stepType != null && stepType != CufetType.Number)
                throw TypeError(
                    "range step must be a number",
                    null,
                    re.Line, re.Column,
                    $"count by a {FormatType(stepType)}",
                    "The step in 'counting by <step>' must be a number. For example: range 1 to 10 counting by 2.");

            var literalStep = TryGetLiteralNumber(re.Step);
            if (literalStep == 0)
                throw TypeError(
                    "'counting by 0' never makes progress",
                    null,
                    re.Line, re.Column,
                    "count by 0",
                    "Use a step greater than 0. The range's direction already comes from start vs. end.");
            if (literalStep < 0)
                throw TypeError(
                    "the step in 'counting by' must be positive",
                    null,
                    re.Line, re.Column,
                    $"count by {literalStep}",
                    "Direction comes from start vs. end, not the step's sign. Use a positive step, e.g. 'range 10 to 1 counting by 2' already descends.");
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
