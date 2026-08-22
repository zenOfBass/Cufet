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

        // Language books — the tag you pull to write foreign source at all. No members, and never
        // any: a book on a LANGUAGE is not a library of anything, and a collection of ready-made
        // axioms would be an ordinary module. See TypeChecker.Foreign.
        foreach (var language in LanguageBookNames())
            books[language] = new BookType(language, []);

        return books;
    }

    // Returns true when any variable in scope has type BookType("chance") — i.e. chance is pulled
    // under any local alias. Used to validate all chance-book expressions.
    private bool IsChancePulled()
    {
        // ⚠ By NAME, over either shape. A pulled book binds at its Cufet layer (an ObjectType)
        // now, so looking only for a BookType found nothing and every `a random number` refused
        // itself. The BookType arm stays for a book with no layer, which is a state the prelude
        // no longer produces but the checker should not depend on.
        for (int i = _scopes.Count - 1; i >= 0; i--)
            foreach (var info in _scopes[i].Values)
                if (info.Type is BookType { Name: var bookName } && IsChanceName(bookName)) return true;
                else if (info.Type is ObjectType { Name: var objName } && IsChanceName(objName)) return true;
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
        // ⚠ A rabbit reaches this only through `Pull a book on rabbit.` or `Pull rabbit as …`
        // routed as an ordinary module — and neither opens a REGION, which is the one thing
        // pulling a rabbit has to do. `Pull a rabbit.` is parsed into its own statement long
        // before here. So this path would hand back a rabbit standing on no ground; refuse it
        // rather than produce one.
        if (string.Equals(name, RabbitModuleName, StringComparison.OrdinalIgnoreCase))
            throw TypeError(
                "a rabbit isn't a book",
                "Pulling a rabbit opens its region, which the 'book on' spelling does not do",
                ps.Line, ps.Column,
                "pull a rabbit as though it were a book",
                "Write 'Pull a rabbit.' — or 'Pull a rabbit as <name>.' to give it a name.");

        if (BuiltinBooks.TryGetValue(name, out var bookType))
        {
            // ⚠ A BUNDLED BOOK IS PULLED AS A BOOK. `Pull math.` used to work, and was never a
            // decision — the general `Pull <module>` branch simply swallowed the name on its way
            // past, and a test then pinned the accident. A book is a library, not the writer's own
            // object, and the surface says which it is.
            if (!ps.ViaBookForm)
                throw TypeError(
                    $"'{name}' is a book, so it is pulled as one",
                    "The plain form is for a module you defined; a book is a library the language ships",
                    ps.Line, ps.Column,
                    $"pull '{name}' with the plain form",
                    $"Write 'Pull a book on {name}.' — or 'Pull books on {name}, and <other>.' "
                    + "for several at once.");

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

    /// <summary>Names the language itself owns: the bundled books, and the rabbit.</summary>
    /// <remarks>
    /// ★ Each is defined in the prelude and reached by `Pull`, so a writer may not redefine one,
    /// build one with `a new`, or hang members on one with `unto`. For a rabbit that is
    /// load-bearing rather than tidy: **pulling is what opens its region**, so `a new rabbit { }`
    /// would hand back a rabbit standing on no ground at all.
    /// </remarks>
    private static bool IsBundledModuleName(string name) =>
        BuiltinBooks.ContainsKey(name)
        || string.Equals(name, RabbitModuleName, StringComparison.OrdinalIgnoreCase);

    public const string RabbitModuleName = "rabbit";

    private static bool IsChanceName(string name) =>
        string.Equals(name, "chance", StringComparison.OrdinalIgnoreCase);

    /// <summary>Is this the rabbit's type — the prelude-defined object, not a legacy marker?</summary>
    /// <remarks>
    /// ★ A rabbit is an ordinary ObjectType now (`Prelude/rabbit.cufe`), so it is first class and
    /// conforms to `module` by inheritance rather than by a branch anywhere. What is left are the
    /// few places that must still ASK whether something is a rabbit — being given work, being
    /// returned, being compared — and they ask by name here rather than by a type of its own.
    /// </remarks>
    public static bool IsRabbitType(CufetType? type) =>
        type is ObjectType ot
        && string.Equals(ot.Name, RabbitModuleName, StringComparison.OrdinalIgnoreCase);

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

                // ★★ A book BINDS AT ITS CUFET LAYER — an ordinary ObjectType — rather than at
                // BookType. Every book member is written in Cufet now, so the layer IS the book;
                // binding it is what makes a book pass as a `module` VALUE, and it passes by
                // INHERITANCE rather than by a decision made about books. A module is an object,
                // an object is first class, so a module is first class. Nothing here asks what
                // kind of module it is, which is the whole point of the arc.
                var bound = MemberOwnerType(pulled) ?? pulled;
                Scope[localName] = new TypeInfo(
                    bound, new VariableReference(localName, ps.Line, ps.Column), ps.Line,
                    IsPulledModule: true);

                // A book may introduce a type name for the length of the block — `matrix` is the
                // only one. Looked up BY NAME rather than off the bound type, which is no longer
                // a BookType once the layer is what gets bound.
                if (BuiltinBooks.TryGetValue(moduleName, out var bookType))
                    foreach (var (typeName, typeObj) in bookType.IntroducedTypes)
                        RegisterScopedType(typeName.ToLowerInvariant(), typeObj);
            }

            // ★ Every pulled module's requirements must be satisfiable HERE, because here is
            // where they resolve and here is where they can be fixed. Reported at the pull rather
            // than at the call: the caller wrote this line, and the missing name belongs in it.
            //
            // ⚠ Recorded now, verified when checking is DONE — a module defined after this block
            // has not been checked yet, so its needs are not known yet. See _pendingPullChecks.
            var visibleHere = VisibleNames();
            foreach (var (moduleName, _) in ps.Books)
                _pendingPullChecks.Add((moduleName, ps, visibleHere));

            CheckBlock(ps.Body);
        }
        finally { ExitScope(); }
    }

    /// <summary>Refuses a pull whose module reaches for something this block does not have.</summary>
    /// <remarks>
    /// ⚠ The cost this pays for is real and was measured: forgetting a module's dependency used to
    /// give THREE answers to one program — `check` said "No problems found", the interpreter died
    /// pointing at a line INSIDE the module, and the compiler said "field access on 'number' is
    /// not yet supported by the compiler", blaming itself for a scoping mistake.
    ///
    /// Only names the module could not resolve for itself are listed, and only when the pulling
    /// block cannot resolve them either — so a module that needs nothing is never mentioned, and
    /// a dependency pulled further out still satisfies it.
    /// </remarks>
    /// <summary>
    /// Is this the name of something PULLABLE — a bundled book, or an object declared a module?
    /// </summary>
    /// <remarks>
    /// ★★ The one name a detached body may reach for without having it in scope. A body resolves
    /// names where it is WRITTEN, with one exception: a pulled module is a capability of the block
    /// that uses the body, so `math's pi` inside a method is legitimate whenever the caller pulled
    /// `math`. That exception is what the deferral in `NoteUnresolvedName` is FOR, and it used to
    /// apply to every name — which made a plain typo indistinguishable from a capability and put
    /// off finding it until run time.
    ///
    /// ⚠ Declarations only — an ALIAS is deliberately not a module name. `Pull math as m.` makes
    /// `m` a name in that block, and a body written inside it sees `m` lexically like anything
    /// else. What is refused is a body OUTSIDE the pull written against `m`, because such a body
    /// works only while every caller happens to pick the same alias; rename it at one call site and
    /// the function breaks with nothing to point at. An alias is for the block that makes it, not
    /// something to publish.
    ///
    /// ★ Answerable whenever a body is checked: every object definition is registered in Pass1Hoist
    /// before any body, so a module defined further down the file counts here too.
    /// </remarks>
    private bool IsModuleName(string name) =>
        BuiltinBooks.ContainsKey(name)
     || (_objectDefs.TryGetValue(name, out var ot) && ot.ConformedInterfaces.Contains(ModuleInterface));

    /// <summary>Verifies every recorded pull, now that every module's needs are known.</summary>
    internal void CheckPendingPulls()
    {
        foreach (var (moduleName, ps, visible) in _pendingPullChecks)
            CheckModuleNeedsAreInScope(moduleName, ps, visible);
        _pendingPullChecks.Clear();
    }

    private void CheckModuleNeedsAreInScope(string moduleName, PullStatement ps, HashSet<string> visible)
    {
        if (!_moduleNeeds.TryGetValue(moduleName, out var needs)) return;

        var missing = needs.Where(name => !visible.Contains(name)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (missing.Count == 0) return;

        var names = string.Join(", ", missing.Select(m => $"'{m}'"));
        var together = string.Join(", and ", missing.Concat([moduleName]));
        throw TypeError(
            $"'{moduleName}' uses {names}, which {(missing.Count == 1 ? "isn't" : "aren't")} pulled here",
            "A module's dependencies come from the block it is used in, not the one it is written in",
            ps.Line, ps.Column,
            $"pull '{moduleName}' without {names}",
            $"Pull them together: 'Pull books on {together}.'");
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
