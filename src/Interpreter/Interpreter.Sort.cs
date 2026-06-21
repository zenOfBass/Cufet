namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // ── Sort ──────────────────────────────────────────────────────────────────

    private object EvaluateSort(SortExpression sort)
    {
        var seriesVal = Evaluate(sort.Series);
        if (seriesVal is not List<object> list)
            throw new RuntimeException($"Expected a series for 'sorted' on line {sort.Line}.");

        // Key extractor: identity for natural sort, named field value for by-field sort.
        object KeyOf(object elem) => sort.ByField == null
            ? elem
            : GetSortKey(elem, sort.ByField, sort.Line);

        // Use OrderBy / OrderByDescending (both stable in LINQ) so equal-key elements
        // retain their original relative order. Reversing the comparison (not the result)
        // keeps stability correct for the descending case too.
        IOrderedEnumerable<object> ordered = sort.Reverse
            ? list.OrderByDescending(KeyOf, CufetNaturalComparer.Instance)
            : list.OrderBy(KeyOf, CufetNaturalComparer.Instance);

        return ordered.ToList();
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
