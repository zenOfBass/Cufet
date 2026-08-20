using Cufet.Lexer;

namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Books (bundled standard-library modules) ──────────────────────────────

    private static readonly Dictionary<string, BookType> BuiltinBooks = BuildBuiltinBooks();

    private static Dictionary<string, BookType> BuildBuiltinBooks()
    {
        var books = new Dictionary<string, BookType>(StringComparer.OrdinalIgnoreCase);

        // math book — ★ nothing native left. Every member is written in Cufet
        // (Prelude/math.cufe), including the transcendentals, which are computed on the decimal
        // itself rather than through a double. The book still exists as a NAME so that `Pull
        // math.` resolves and its Cufet layer is found under it.
        books["math"] = new BookType("math", []);

        // collections book — its members all live in the Cufet layer (Prelude/collections.cufe)
        // now; the native side introduces the `matrix` TYPE and nothing else.
        var collectionsTypes = new Dictionary<string, CufetType>(StringComparer.OrdinalIgnoreCase)
        {
            ["matrix"] = MatrixType.Instance,
        };
        books["collections"] = new BookType("collections", [], collectionsTypes);

        // chance book — effectful randomness (stateful global RNG).
        // Functions are NOT registered here as book members because they use natural-language
        // surface syntax (RandomNumber/RandomItem/RandomlyShuffled/RandomGuess AST nodes) rather
        // than the cast-book's-member-on dispatch path.
        books["chance"] = new BookType("chance", []);

        return books;
    }

    // Returns true when any variable in scope has type BookType("chance") — i.e. chance is pulled
    // under any local alias. Used to validate all chance-book expressions.
    private bool IsChancePulled()
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            foreach (var info in _scopes[i].Values)
                if (info.Type is BookType { Name: var n } &&
                    n.Equals("chance", StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    private void RequireChancePulled(int line, int col, string construct)
    {
        if (!IsChancePulled())
            throw TypeError(
                $"the chance book is not in scope",
                null, line, col,
                $"use '{construct}' without pulling the chance book",
                "Add 'Pull a book on chance.' before this line.");
    }

    // ── Chance book — type inference ─────────────────────────────────────────────

    private CufetType InferRandomNumber(RandomNumber rn)
    {
        RequireChancePulled(rn.Line, rn.Column, "a random number from ... to ...");
        var lowType = InferType(rn.Low);
        if (lowType != null && lowType != CufetType.Number)
            throw TypeError(
                $"the lower bound of a random number range must be a number, but found a {FormatType(lowType)}",
                null, rn.Line, rn.Column,
                $"use a {FormatType(lowType)} as a range bound",
                "Use numbers for both bounds (e.g. 'a random number from 1 to 6').");
        var highType = InferType(rn.High);
        if (highType != null && highType != CufetType.Number)
            throw TypeError(
                $"the upper bound of a random number range must be a number, but found a {FormatType(highType)}",
                null, rn.Line, rn.Column,
                $"use a {FormatType(highType)} as a range bound",
                "Use numbers for both bounds (e.g. 'a random number from 1 to 6').");
        return CufetType.Number;
    }

    private CufetType InferRandomItem(RandomItem ri)
    {
        RequireChancePulled(ri.Line, ri.Column, "a random item from ...");
        var seriesType = InferType(ri.Series);
        if (seriesType == null) return new VoidableType(CufetType.Number); // can't infer — fall back
        if (seriesType is SeriesType st) return new VoidableType(st.ElementType);
        throw TypeError(
            $"'a random item from' requires a series, but found a {FormatType(seriesType)}",
            null, ri.Line, ri.Column,
            $"pick a random item from a {FormatType(seriesType)}",
            "'a random item from' works on any series or catalogue.");
    }

    private CufetType InferRandomlyShuffled(RandomlyShuffled rs)
    {
        RequireChancePulled(rs.Line, rs.Column, "randomly shuffled ...");
        var seriesType = InferType(rs.Series);
        if (seriesType == null) return new SeriesType(CufetType.Number); // fallback
        if (seriesType is SeriesType) return seriesType; // same series type (element-type-preserving)
        throw TypeError(
            $"'randomly shuffled' requires a series, but found a {FormatType(seriesType)}",
            null, rs.Line, rs.Column,
            $"shuffle a {FormatType(seriesType)}",
            "'randomly shuffled' works on any series or catalogue.");
    }

    private CufetType InferRandomGuess(RandomGuess rg)
    {
        RequireChancePulled(rg.Line, rg.Column, "a random guess");
        return CufetType.Fact;
    }

    private void CheckSeedChanceStatement(SeedChanceStatement ss)
    {
        RequireChancePulled(ss.Line, ss.Column, "Seed the chance with ...");
        var seedType = InferType(ss.Seed);
        if (seedType != null && seedType != CufetType.Number)
            throw TypeError(
                $"the seed must be a number, but found a {FormatType(seedType)}",
                null, ss.Line, ss.Column,
                $"seed chance with a {FormatType(seedType)}",
                "Use a whole number as the seed (e.g. 'Seed the chance with 42.').");
    }

    /// <summary>
    /// What `Pull &lt;name&gt;` found, or a refusal saying why it found nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ★★ ONE question, asked of everything: <em>is this a module?</em> That is the whole content of
    /// the `module` interface, and asking it in one place is what stops the bundled books being a
    /// privileged category. They are not special because the checker has a branch for them; they are
    /// three modules that happen to ship in the box.
    /// </para>
    /// <para>
    /// A book conforms BY CONSTRUCTION rather than by declaration — there is no `Define object math`
    /// to hang an `and module` clause on, because a book's members are implemented natively rather
    /// than in Cufet. That is a difference in how a conformer is BUILT, not in what the contract
    /// asks, which is exactly the line the module design draws: the contract says "may be pulled",
    /// and everything past that is the conformer's own business.
    /// </para>
    /// </remarks>
    private CufetType ResolveModule(string name, PullStatement ps)
    {
        if (BuiltinBooks.TryGetValue(name, out var bookType))
        {
            // ★ Recorded so the book's Cufet layer can be DROPPED from the program when nothing
            // pulls it — see DropUnpulledLayers. A layer's members are reachable only through a
            // pull, so the pull sites are the whole reachability question, and the checker
            // already visits every one of them. No AST walk of our own, which is the point.
            _pulledBooks.Add(name);
            return bookType;
        }

        if (_objectDefs.TryGetValue(name, out var moduleType))
        {
            // ★ The marker requires no methods, but it does require the CLAIM. Being pullable is
            // something an author says, not something every object accidentally is — otherwise the
            // interface would be decorative and `Pull` would take anything with a name.
            if (!moduleType.ConformedInterfaces.Contains(ModuleInterface))
                throw TypeError(
                    $"'{name}' is not a module, so it can't be pulled",
                    $"Pulling brings a module into scope, and '{name}' doesn't say it is one",
                    ps.Line, ps.Column,
                    $"pull '{name}'",
                    $"Add 'and {ModuleInterface}' to its definition: "
                    + $"'Define object {name} with (...) and {ModuleInterface}:'.");
            return moduleType;
        }

        var available = string.Join(", ", BuiltinBooks.Keys.OrderBy(k => k).Select(k => $"'{k}'"));
        throw TypeError(
            $"there is nothing named '{name}' to pull",
            null, ps.Line, ps.Column,
            $"pull '{name}'",
            $"Pull one of the bundled books ({available}), or define an object named "
            + $"'{name}' as a module: 'Define object {name} with (...) and {ModuleInterface}:'.");
    }

    /// <summary>
    /// The type whose members a possessive target actually offers: an object directly, or — for a
    /// bundled book — its Cufet layer, the prelude-defined module object sharing the book's name.
    /// </summary>
    /// <remarks>
    /// ★ Slice 1 of the 0.16.0 arc: a book and its Cufet layer resolve as ONE module, the Cufet
    /// member winning wherever both define a name. The layer is looked up in `_objectDefs` LIVE
    /// rather than stored on the BookType, because generic-method instantiation replaces the
    /// registered ObjectType as fillings land — a stored reference would go stale mid-check.
    /// </remarks>
    private CufetType? MemberOwnerType(CufetType? targetType) =>
        targetType is BookType bt && _objectDefs.TryGetValue(bt.Name, out var cufetLayer)
            ? cufetLayer
            : targetType;

    private void CheckPullStatement(PullStatement ps)
    {
        EnterScope();
        try
        {
            foreach (var (moduleName, localName) in ps.Books)
            {
                var pulled = ResolveModule(moduleName, ps);
                Scope[localName] = new TypeInfo(pulled, new VariableReference(localName, ps.Line, ps.Column), ps.Line);

                // A book may introduce a type name for the length of the block — `matrix` is the
                // only one today. Nothing else that is pullable does, so this stays book-shaped.
                if (pulled is BookType bookType)
                    foreach (var (typeName, typeObj) in bookType.IntroducedTypes)
                        RegisterScopedType(typeName.ToLowerInvariant(), typeObj);
            }

            CheckBlock(ps.Body);
        }
        finally { ExitScope(); }
    }

    private CufetType InferBookPossessiveAccess(PossessiveAccess poss, BookType bt)
    {
        var memberType = bt.FindMember(poss.Member);
        if (memberType != null) return memberType;

        var available = string.Join(", ", bt.Members.Select(m => $"'{m.MemberName}'"));
        throw TypeError(
            $"book '{bt.Name}' has no member '{poss.Member}'",
            null, poss.Line, poss.Column,
            $"access '{poss.Member}' from book '{bt.Name}'",
            available.Length > 0 ? $"Available: {available}." : $"Book '{bt.Name}' has no members.");
    }

    // ── Matrix type inference ─────────────────────────────────────────────────

    private CufetType InferMatrixLiteral(MatrixLiteral lit)
    {
        if (!TryLookupScopedType("matrix", out _))
            throw TypeError(
                "'matrix' is not available in this scope",
                null, lit.Line, lit.Column,
                "construct a matrix without pulling the 'collections' book first",
                "Add 'Pull a book on collections.' before this line.");

        if (lit.Rows.Count == 0)
            throw TypeError(
                "a matrix must have at least one row",
                null, lit.Line, lit.Column,
                "create a matrix with no rows",
                "Provide at least one row: 'a matrix with ((1, 2), (3, 4))'.");

        int cols = lit.Rows[0].Count;
        if (cols == 0)
            throw TypeError(
                "each matrix row must have at least one element",
                null, lit.Line, lit.Column,
                "create a matrix row with no elements",
                "Provide at least one number in each row.");

        for (int i = 1; i < lit.Rows.Count; i++)
        {
            if (lit.Rows[i].Count != cols)
                throw TypeError(
                    $"matrix rows must be equal length; row {i + 1} has {lit.Rows[i].Count} element(s), expected {cols}",
                    $"Row 1 has {cols} element(s)",
                    lit.Line, lit.Column,
                    "create a matrix with unequal row lengths",
                    "Make all rows the same length to form a rectangle.");
        }

        foreach (var row in lit.Rows)
        {
            foreach (var elem in row)
            {
                var t = InferType(elem);
                if (t != null && t != CufetType.Number)
                    throw TypeError(
                        $"matrix elements must be numbers, but found a {FormatType(t)}",
                        null, lit.Line, lit.Column,
                        $"put a {FormatType(t)} inside a matrix",
                        "All matrix elements must be numbers.");
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
            throw TypeError(
                "'matrix' is not available in this scope",
                null, ms.Line, ms.Column,
                "construct a matrix without pulling the 'collections' book first",
                "Add 'Pull a book on collections.' before this line.");

        var rowsType = InferType(ms.Rows);
        if (rowsType != null && rowsType != CufetType.Number)
            throw TypeError(
                $"matrix row count must be a number, but found a {FormatType(rowsType)}",
                null, ms.Line, ms.Column,
                $"use a {FormatType(rowsType)} as a matrix row count",
                "Row and column counts must be numbers (e.g. 3, 4).");

        var rowLitVal = TryGetLiteralDecimal(ms.Rows);
        if (rowLitVal.HasValue && (rowLitVal.Value != Math.Truncate(rowLitVal.Value) || rowLitVal.Value < 1))
            throw TypeError(
                $"matrix dimensions must be positive whole numbers; got {rowLitVal.Value} for rows",
                null, ms.Line, ms.Column,
                $"use {rowLitVal.Value} as a matrix row count",
                "Use a positive whole number like 1, 2, 3.");

        var colsType = InferType(ms.Cols);
        if (colsType != null && colsType != CufetType.Number)
            throw TypeError(
                $"matrix column count must be a number, but found a {FormatType(colsType)}",
                null, ms.Line, ms.Column,
                $"use a {FormatType(colsType)} as a matrix column count",
                "Row and column counts must be numbers (e.g. 3, 4).");

        var colLitVal = TryGetLiteralDecimal(ms.Cols);
        if (colLitVal.HasValue && (colLitVal.Value != Math.Truncate(colLitVal.Value) || colLitVal.Value < 1))
            throw TypeError(
                $"matrix dimensions must be positive whole numbers; got {colLitVal.Value} for columns",
                null, ms.Line, ms.Column,
                $"use {colLitVal.Value} as a matrix column count",
                "Use a positive whole number like 1, 2, 3.");

        if (ms.Fill != null)
        {
            var fillType = InferType(ms.Fill);
            if (fillType != null && fillType != CufetType.Number)
                throw TypeError(
                    $"matrix fill value must be a number, but found a {FormatType(fillType)}",
                    null, ms.Line, ms.Column,
                    $"use a {FormatType(fillType)} as a matrix fill value",
                    "The fill value must be a number (e.g. 0, 1, -1.5).");
        }

        return MatrixType.Instance;
    }

    private CufetType InferMatrixAccess(MatrixAccess ma)
    {
        var matType = InferType(ma.Matrix);
        if (matType != null && matType is not MatrixType)
            throw TypeError(
                $"'the item at (row, column) of' requires a matrix, but found a {FormatType(matType)}",
                null, ma.Line, ma.Column,
                $"index a {FormatType(matType)} with matrix indexing syntax",
                "Use 'the item at (row, column) of' with a matrix value.");

        var rowType = InferType(ma.Row);
        if (rowType != null && rowType != CufetType.Number)
            throw TypeError(
                $"matrix row index must be a number, but found a {FormatType(rowType)}",
                null, ma.Line, ma.Column,
                $"use a {FormatType(rowType)} as a matrix row index",
                "Row and column indices must be numbers (e.g. 1, 2, 3).");

        var colType = InferType(ma.Col);
        if (colType != null && colType != CufetType.Number)
            throw TypeError(
                $"matrix column index must be a number, but found a {FormatType(colType)}",
                null, ma.Line, ma.Column,
                $"use a {FormatType(colType)} as a matrix column index",
                "Row and column indices must be numbers (e.g. 1, 2, 3).");

        return CufetType.Number;
    }

    // The item at (row, column) of <matrix> becomes <value>.
    //
    // The target and index rules are the read form's, reached through it so the two can never
    // disagree about what a matrix index is. Only the stored value is new — a matrix holds numbers
    // and nothing else, so there is no element type to consult.
    private void CheckMatrixSet(MatrixSetStatement ms)
    {
        InferMatrixAccess(new MatrixAccess(ms.Matrix, ms.Row, ms.Col, ms.Line, ms.Column));

        var valueType = InferType(ms.Value);
        if (valueType != null && valueType != CufetType.Number)
            throw TypeError(
                $"a matrix holds numbers, but found a {FormatType(valueType)}",
                "Every cell of a matrix is a number — that is what makes its arithmetic exact",
                ms.Line, ms.Column,
                $"store a {FormatType(valueType)} in a matrix cell",
                "Change the new value to a number.");
    }
}
