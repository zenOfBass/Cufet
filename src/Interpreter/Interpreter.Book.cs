namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    // ── Books (bundled standard-library modules) ──────────────────────────────

    private static readonly Dictionary<string, BookValue> BuiltinBookValues = BuildBuiltinBookValues();

    private static Dictionary<string, BookValue> BuildBuiltinBookValues()
    {
        var books = new Dictionary<string, BookValue>(StringComparer.OrdinalIgnoreCase);

        var mathFunctions = new Dictionary<string, Func<object[], object?>>(StringComparer.OrdinalIgnoreCase)
        {
            // Total functions: use decimal overloads directly — no double conversion needed.
            ["floor"]          = args => (object)(decimal)Math.Floor((decimal)args[0]),
            ["ceiling"]        = args => (object)(decimal)Math.Ceiling((decimal)args[0]),
            ["round"]          = args => (object)(decimal)Math.Round((decimal)args[0], MidpointRounding.AwayFromZero),
            ["absolute value"] = args => (object)Math.Abs((decimal)args[0]),
            // Partial functions: decimal→double for the call, !IsFinite check, double→decimal back.
            // Math.Log(0) returns NegativeInfinity, not NaN — must use !IsFinite, not IsNaN.
            ["square root"]    = args => MathPartial(Math.Sqrt((double)(decimal)args[0])),
            ["log"]            = args => MathPartial(Math.Log((double)(decimal)args[0])),
            ["power"]          = args => MathPartial(Math.Pow((double)(decimal)args[0], (double)(decimal)args[1])),
        };

        var mathConstants = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["pi"] = (object)(decimal)Math.PI,
            ["e"]  = (object)(decimal)Math.E,
        };

        books["math"] = new BookValue("math", mathFunctions, mathConstants);

        // collections book — introduces the matrix type + transpose operation.
        var collectionsFunctions = new Dictionary<string, Func<object[], object?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["transpose"] = args =>
            {
                var mv = (MatrixValue)args[0];
                var data = new decimal[mv.Rows * mv.Cols];
                for (int r = 0; r < mv.Rows; r++)
                    for (int c = 0; c < mv.Cols; c++)
                        data[c * mv.Rows + r] = mv.GetItem(r + 1, c + 1);
                return (object)new MatrixValue(mv.Cols, mv.Rows, data);
            },
        };
        books["collections"] = new BookValue(
            "collections",
            collectionsFunctions,
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase));

        return books;
    }

    private static object? MathPartial(double result)
    {
        if (!double.IsFinite(result)) return VoidValue.Instance;
        try   { return (object)(decimal)result; }
        catch (OverflowException) { return VoidValue.Instance; }
    }

    private void ExecutePullStatement(PullStatement ps)
    {
        if (!BuiltinBookValues.TryGetValue(ps.BookName, out var bookValue))
            throw new RuntimeException($"No book named '{ps.BookName}' (line {ps.Line}).");
        Scope[ps.LocalName] = bookValue;
    }

    private object? DispatchBookFunction(BookValue bv, string memberName, IReadOnlyList<IExpression> args, int line)
    {
        if (!bv.Functions.TryGetValue(memberName, out var fn))
        {
            if (bv.Constants.ContainsKey(memberName))
                throw new RuntimeException(
                    $"'{memberName}' in book '{bv.Name}' is a constant — access it via '{bv.Name}'s {memberName}' without 'of' (line {line}).");
            throw new RuntimeException($"Book '{bv.Name}' has no function '{memberName}' (line {line}).");
        }
        var argValues = args.Select(Evaluate).ToArray();
        return fn(argValues);
    }
}
