using Cufet.Lexer;

namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Books (bundled standard-library modules) ──────────────────────────────

    private static readonly Dictionary<string, BookType> BuiltinBooks = BuildBuiltinBooks();

    private static Dictionary<string, BookType> BuildBuiltinBooks()
    {
        var books = new Dictionary<string, BookType>(StringComparer.OrdinalIgnoreCase);

        // math book — deterministic operations return number;
        // partial operations (undefined for some inputs) return voidable number.
        var mathMembers = new List<(string, CufetType)>
        {
            ("floor",          new FunctionType([CufetType.Number], CufetType.Number)),
            ("ceiling",        new FunctionType([CufetType.Number], CufetType.Number)),
            ("round",          new FunctionType([CufetType.Number], CufetType.Number)),
            ("absolute value", new FunctionType([CufetType.Number], CufetType.Number)),
            ("square root",    new FunctionType([CufetType.Number], new VoidableType(CufetType.Number))),
            ("log",            new FunctionType([CufetType.Number], new VoidableType(CufetType.Number))),
            ("power",          new FunctionType([CufetType.Number, CufetType.Number], new VoidableType(CufetType.Number))),
            ("pi",             CufetType.Number),
            ("e",              CufetType.Number),
        };
        books["math"] = new BookType("math", mathMembers);

        // collections book — introduces the matrix type + transpose operation.
        var collectionsTypes = new Dictionary<string, CufetType>(StringComparer.OrdinalIgnoreCase)
        {
            ["matrix"] = MatrixType.Instance,
        };
        var collectionsMembers = new List<(string, CufetType)>
        {
            ("transpose", new FunctionType([MatrixType.Instance], MatrixType.Instance)),
        };
        books["collections"] = new BookType("collections", collectionsMembers, collectionsTypes);

        return books;
    }

    private void CheckPullStatement(PullStatement ps)
    {
        if (!BuiltinBooks.TryGetValue(ps.BookName, out var bookType))
        {
            var available = string.Join(", ", BuiltinBooks.Keys.OrderBy(k => k).Select(k => $"'{k}'"));
            throw new TypeException(FormatTypeError(
                $"there is no book named '{ps.BookName}'",
                null, ps.Line,
                $"pull a book named '{ps.BookName}'",
                $"Available books: {available}."));
        }

        if (Scope.ContainsKey(ps.LocalName))
            throw new TypeException(FormatTypeError(
                $"'{ps.LocalName}' is already defined in this scope",
                null, ps.Line,
                $"bind book '{ps.BookName}' to '{ps.LocalName}'",
                "Choose a different local name."));

        Scope[ps.LocalName] = new TypeInfo(bookType, new VariableReference(ps.LocalName, ps.Line), ps.Line);

        // Register any types the book introduces into the current type scope.
        foreach (var (typeName, typeObj) in bookType.IntroducedTypes)
            RegisterScopedType(typeName.ToLowerInvariant(), typeObj);
    }

    // Returns true when the cast is to a collections aggregate whose type cannot be expressed as
    // a plain FunctionType (minimum/maximum/average: numeric reduction, void-on-empty;
    // unique: element-type-preserving dedup). Handled in InferCastExpr before ResolveForCast.
    private bool IsCollectionsAggregateCast(CastExpression cast)
    {
        if (cast.Function is not PossessiveAccess poss) return false;
        if (InferType(poss.Target) is not BookType bt) return false;
        if (!bt.Name.Equals("collections", StringComparison.OrdinalIgnoreCase)) return false;
        var m = poss.Member.ToLowerInvariant();
        return m is "minimum" or "maximum" or "average" or "unique";
    }

    private CufetType? InferCollectionsAggregateCast(CastExpression cast)
    {
        var poss   = (PossessiveAccess)cast.Function;
        var member = poss.Member.ToLowerInvariant();

        if (cast.Args.Count != 1)
            throw new TypeException(FormatTypeError(
                $"'{poss.Member}' takes one argument (a series), but {cast.Args.Count} were given",
                null, cast.Line,
                $"call '{poss.Member}' with {cast.Args.Count} arguments",
                $"Use: cast collections' {poss.Member} of (xs)."));

        var argType = InferType(cast.Args[0]);

        if (member != "unique")
        {
            if (argType is SeriesType { ElementType: var et } && et != CufetType.Number)
                throw new TypeException(FormatTypeError(
                    $"'{poss.Member}' works on a series of numbers, but this series holds {FormatTypePlural(et)}",
                    null, cast.Line,
                    $"apply '{poss.Member}' to a series of {FormatTypePlural(et)}",
                    $"'{poss.Member}' requires a series of number."));

            if (argType != null && argType is not SeriesType)
                throw new TypeException(FormatTypeError(
                    $"'{poss.Member}' works on a series of numbers, but got a {FormatType(argType)}",
                    null, cast.Line,
                    $"apply '{poss.Member}' to a {FormatType(argType)}",
                    $"'{poss.Member}' requires a series of number."));

            return new VoidableType(CufetType.Number);
        }

        // unique: series of T → series of T (element-type-preserving).
        if (argType != null && argType is not SeriesType)
            throw new TypeException(FormatTypeError(
                $"'unique' works on a series, but got a {FormatType(argType)}",
                null, cast.Line,
                $"apply 'unique' to a {FormatType(argType)}",
                "'unique' requires a series of any element type."));

        return argType; // same SeriesType, or null if unresolvable (runtime catches it)
    }

    private CufetType InferBookPossessiveAccess(PossessiveAccess poss, BookType bt)
    {
        var memberType = bt.FindMember(poss.Member);
        if (memberType != null) return memberType;

        var available = string.Join(", ", bt.Members.Select(m => $"'{m.MemberName}'"));
        throw new TypeException(FormatTypeError(
            $"book '{bt.Name}' has no member '{poss.Member}'",
            null, poss.Line,
            $"access '{poss.Member}' from book '{bt.Name}'",
            available.Length > 0 ? $"Available: {available}." : $"Book '{bt.Name}' has no members."));
    }

    // ── Matrix type inference ─────────────────────────────────────────────────

    private CufetType InferMatrixLiteral(MatrixLiteral lit)
    {
        if (!TryLookupScopedType("matrix", out _))
            throw new TypeException(FormatTypeError(
                "'matrix' is not available in this scope",
                null, lit.Line,
                "construct a matrix without pulling the 'collections' book first",
                "Add 'Pull a book on collections.' before this line."));

        if (lit.Rows.Count == 0)
            throw new TypeException(FormatTypeError(
                "a matrix must have at least one row",
                null, lit.Line,
                "create a matrix with no rows",
                "Provide at least one row: 'a matrix with ((1, 2), (3, 4))'."));

        int cols = lit.Rows[0].Count;
        if (cols == 0)
            throw new TypeException(FormatTypeError(
                "each matrix row must have at least one element",
                null, lit.Line,
                "create a matrix row with no elements",
                "Provide at least one number in each row."));

        for (int i = 1; i < lit.Rows.Count; i++)
        {
            if (lit.Rows[i].Count != cols)
                throw new TypeException(FormatTypeError(
                    $"matrix rows must be equal length; row {i + 1} has {lit.Rows[i].Count} element(s), expected {cols}",
                    $"Row 1 has {cols} element(s)",
                    lit.Line,
                    "create a matrix with unequal row lengths",
                    "Make all rows the same length to form a rectangle."));
        }

        foreach (var row in lit.Rows)
        {
            foreach (var elem in row)
            {
                var t = InferType(elem);
                if (t != null && t != CufetType.Number)
                    throw new TypeException(FormatTypeError(
                        $"matrix elements must be numbers, but found a {FormatType(t)}",
                        null, lit.Line,
                        $"put a {FormatType(t)} inside a matrix",
                        "All matrix elements must be numbers."));
            }
        }

        return MatrixType.Instance;
    }

    // Returns the constant value if expr is a numeric literal or unary-minus-of-literal; null otherwise.
    private static decimal? TryGetLiteralDecimal(IExpression expr) => expr switch
    {
        NumberLiteral nl => nl.Value,
        UnaryExpression { Op: TokenType.Minus, Operand: NumberLiteral nl } => -nl.Value,
        _ => null,
    };

    private CufetType InferMatrixSized(MatrixSized ms)
    {
        if (!TryLookupScopedType("matrix", out _))
            throw new TypeException(FormatTypeError(
                "'matrix' is not available in this scope",
                null, ms.Line,
                "construct a matrix without pulling the 'collections' book first",
                "Add 'Pull a book on collections.' before this line."));

        var rowsType = InferType(ms.Rows);
        if (rowsType != null && rowsType != CufetType.Number)
            throw new TypeException(FormatTypeError(
                $"matrix row count must be a number, but found a {FormatType(rowsType)}",
                null, ms.Line,
                $"use a {FormatType(rowsType)} as a matrix row count",
                "Row and column counts must be numbers (e.g. 3, 4)."));

        var rowLitVal = TryGetLiteralDecimal(ms.Rows);
        if (rowLitVal.HasValue && (rowLitVal.Value != Math.Truncate(rowLitVal.Value) || rowLitVal.Value < 1))
            throw new TypeException(FormatTypeError(
                $"matrix dimensions must be positive whole numbers; got {rowLitVal.Value} for rows",
                null, ms.Line,
                $"use {rowLitVal.Value} as a matrix row count",
                "Use a positive whole number like 1, 2, 3."));

        var colsType = InferType(ms.Cols);
        if (colsType != null && colsType != CufetType.Number)
            throw new TypeException(FormatTypeError(
                $"matrix column count must be a number, but found a {FormatType(colsType)}",
                null, ms.Line,
                $"use a {FormatType(colsType)} as a matrix column count",
                "Row and column counts must be numbers (e.g. 3, 4)."));

        var colLitVal = TryGetLiteralDecimal(ms.Cols);
        if (colLitVal.HasValue && (colLitVal.Value != Math.Truncate(colLitVal.Value) || colLitVal.Value < 1))
            throw new TypeException(FormatTypeError(
                $"matrix dimensions must be positive whole numbers; got {colLitVal.Value} for columns",
                null, ms.Line,
                $"use {colLitVal.Value} as a matrix column count",
                "Use a positive whole number like 1, 2, 3."));

        if (ms.Fill != null)
        {
            var fillType = InferType(ms.Fill);
            if (fillType != null && fillType != CufetType.Number)
                throw new TypeException(FormatTypeError(
                    $"matrix fill value must be a number, but found a {FormatType(fillType)}",
                    null, ms.Line,
                    $"use a {FormatType(fillType)} as a matrix fill value",
                    "The fill value must be a number (e.g. 0, 1, -1.5)."));
        }

        return MatrixType.Instance;
    }

    private CufetType InferMatrixRows(MatrixRows mr)
    {
        var t = InferType(mr.Target);
        if (t != null && t is not MatrixType)
            throw new TypeException(FormatTypeError(
                $"'the rows of' requires a matrix, but found a {FormatType(t)}",
                null, mr.Line,
                $"query the row count of a {FormatType(t)}",
                "Use 'the rows of' with a matrix value."));
        return CufetType.Number;
    }

    private CufetType InferMatrixColumns(MatrixColumns mc)
    {
        var t = InferType(mc.Target);
        if (t != null && t is not MatrixType)
            throw new TypeException(FormatTypeError(
                $"'the columns of' requires a matrix, but found a {FormatType(t)}",
                null, mc.Line,
                $"query the column count of a {FormatType(t)}",
                "Use 'the columns of' with a matrix value."));
        return CufetType.Number;
    }

    private CufetType InferMatrixAccess(MatrixAccess ma)
    {
        var matType = InferType(ma.Matrix);
        if (matType != null && matType is not MatrixType)
            throw new TypeException(FormatTypeError(
                $"'the item at (row, column) of' requires a matrix, but found a {FormatType(matType)}",
                null, ma.Line,
                $"index a {FormatType(matType)} with matrix indexing syntax",
                "Use 'the item at (row, column) of' with a matrix value."));

        var rowType = InferType(ma.Row);
        if (rowType != null && rowType != CufetType.Number)
            throw new TypeException(FormatTypeError(
                $"matrix row index must be a number, but found a {FormatType(rowType)}",
                null, ma.Line,
                $"use a {FormatType(rowType)} as a matrix row index",
                "Row and column indices must be numbers (e.g. 1, 2, 3)."));

        var colType = InferType(ma.Column);
        if (colType != null && colType != CufetType.Number)
            throw new TypeException(FormatTypeError(
                $"matrix column index must be a number, but found a {FormatType(colType)}",
                null, ma.Line,
                $"use a {FormatType(colType)} as a matrix column index",
                "Row and column indices must be numbers (e.g. 1, 2, 3)."));

        return CufetType.Number;
    }
}
