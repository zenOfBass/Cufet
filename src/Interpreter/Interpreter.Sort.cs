namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // ── Sort ──────────────────────────────────────────────────────────────────

    private object EvaluateSort(SortExpression sort)
    {
        var seriesVal = Evaluate(sort.Series);

        // ★ A chase sorts by CODE POINT, which is what it stores — and ordering the numbers
        // directly is what keeps both backends agreeing, since the compiled side stores the same
        // unit. Sorting the characters as texts would drag in the comparison rules for text and
        // make the answer depend on which of those each backend reaches for.
        if (seriesVal is CufetChase chase)
        {
            var points = sort.Reverse
                ? chase.OrderByDescending(p => p)
                : chase.OrderBy(p => p);
            var sorted = new CufetChase();
            sorted.AddRange(points);
            return sorted;
        }

        if (seriesVal is not List<object> list)
            throw new RuntimeException($"Expected a series or chase for 'sorted' on line {sort.Line}.");

        // Key extractor: identity for natural sort, named field value for by-field sort, and what
        // the key function gives back for by-function sort.
        // ★ LINQ's OrderBy calls the key selector exactly ONCE per element, in the series' order,
        // before comparing anything — so a key function that prints or counts is seen to run n
        // times, front to back. The compiled sort computes its keys up front for the same reason.
        var keyFunction = (sort.ByLambda ?? sort.KeyFunction) is { } kf
            ? Evaluate(kf) as FunctionValue
              ?? throw new RuntimeException($"The key of 'sorted by' is not a function (line {sort.Line}).")
            : null;
        object KeyOf(object elem) =>
            keyFunction != null ? ExecuteCallOnValues(keyFunction, "the key function", [elem], sort.Line)!
            : sort.ByField == null ? elem
            : GetSortKey(elem, sort.ByField, sort.Line);

        // Use OrderBy / OrderByDescending (both stable in LINQ) so equal-key elements
        // retain their original relative order. Reversing the comparison (not the result)
        // keeps stability correct for the descending case too.
        IOrderedEnumerable<object> ordered = sort.Reverse
            ? list.OrderByDescending(KeyOf, CufetNaturalComparer.Instance)
            : list.OrderBy(KeyOf, CufetNaturalComparer.Instance);

        return Series(ordered, ElementTypeOf(list));   // ISA.2d — a sort keeps the element type
    }

    private object GetSortKey(object element, string fieldName, int line)
    {
        if (element is RecordValue rv)
        {
            var f = rv.NamedFields.FirstOrDefault(f => f.Name == fieldName);
            if (f.Name != null) return f.Value;
        }
        else if (element is ObjectValue ov)
        {
            if (TryFindNamedFieldValue(ov, fieldName, out var val)) return val;
        }
        throw new RuntimeException($"Element has no field '{fieldName}' (line {line}).");
    }

    private sealed class CufetNaturalComparer : IComparer<object>
    {
        public static readonly CufetNaturalComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is decimal dx && y is decimal dy) return dx.CompareTo(dy);
            if (x is string  sx && y is string  sy) return string.Compare(sx, sy, StringComparison.Ordinal);
            throw new RuntimeException("Sort key mismatch: both values must be numbers or both must be text.");
        }
    }
}
