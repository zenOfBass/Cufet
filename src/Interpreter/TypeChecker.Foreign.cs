namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Foreign interoperability: axioms ─────────────────────────────────────
    //
    // An axiom is source in another language, held as a value and taken as given. Cufet cannot
    // check a C listing and does not try; what it checks is the BOUNDARY — that the language is
    // named, that its book is pulled, and that what comes back fits the type it is returned into.

    /// <summary>The books that name a LANGUAGE rather than a library.</summary>
    /// <remarks>
    /// ★ `c-language` and not `c`: the style rule refuses a single-letter name, so C is qualified
    /// whatever the rest do. Each language is named by ear — `sql-language` would say language
    /// twice — so this is a list rather than a `<name>-language` rule.
    ///
    /// A language book has no members. You pull it to write axioms in that language at all; a
    /// bundled collection of ready-made axioms would be an ordinary module that happens to contain
    /// some, with no special status here.
    /// </remarks>
    /// ⚠ A method, not a static field. `BuiltinBooks` is a static field in another partial file and
    /// reads this during its own initializer, where a field here may not be assigned yet — which
    /// showed up as a NullReferenceException inside the type initializer, from nowhere the stack
    /// pointed at. A method has no initialization order to get wrong.
    private static string[] LanguageBookNames() => ["c-language"];

    /// <summary>The same list, for the interpreter's book table — one source, two tables.</summary>
    internal static string[] LanguageBookNamesForBooks() => LanguageBookNames();

    internal static bool IsLanguageBook(string name) =>
        LanguageBookNames().Any(known => IsSameLanguage(known, name));

    /// <summary>
    /// Puts the declaration's language tag on an axiom literal, and hands back the declared type
    /// with the shortened spelling resolved.
    /// </summary>
    /// <remarks>
    /// ★ Called BEFORE the value's type is asked for. `[…]` on its own has no type — the brackets
    /// say "this is verbatim foreign text" and cannot say which foreign — so the tag has to be on
    /// before anything can infer anything.
    /// </remarks>
    private CufetType? TagAxiomDeclaration(DefineStatement define)
    {
        var declared = define.DeclaredType;
        // `Define c-language get-pid as […]` — the shorthand, which parses as an ordinary named
        // type. A language book is the only thing that name can mean in type position.
        if (declared is ObjectType { PositionalTypes.Count: 0, NamedFields.Count: 0, Methods.Count: 0 } shell
            && IsLanguageBook(shell.Name))
            declared = new AxiomType(shell.Name);

        if (define.Value is not AxiomLiteral axiom)
        {
            // ⚠ `given` says what a BODY is handed, and only foreign source is a body a `Define`
            // can hold. On anything else it parses and means nothing, which is worse than being
            // refused — the writer would be left thinking the value takes arguments.
            if (define.HasParameterClause)
                throw TypeError(
                    $"'{define.Name}' is not foreign source, so it cannot be 'given' anything",
                    "Only an axiom — source in another language — takes parameters at a 'Define'",
                    define.Line, define.Column,
                    $"declare parameters for '{define.Name}'",
                    $"Use 'Bind ... to {define.Name}, given (...)' for a function, or drop the clause.");
            return declared;
        }

        if (declared is not AxiomType tag)
            throw TypeError(
                $"'{define.Name}' is foreign source, but nothing says what language it is in",
                "Square brackets say the text is not Cufet; they cannot say which language it is, "
              + "and the tag names who reads it",
                axiom.Line, axiom.Column,
                "write foreign source without naming its language",
                $"Name it in the declaration: 'Define c-language {define.Name} as [ ... ].'");

        RequireLanguagePulled(tag.Language, axiom.Line, axiom.Column);
        axiom.Language = tag.Language;
        axiom.ReturnType = tag.ReturnType is { } declaredResult ? ResolveParamType(declaredResult) : null;
        if (axiom.ReturnType is not null) RequireCrossableResult(axiom);
        CheckAxiomParameters(define, axiom);
        return declared;
    }

    /// <summary>Refuses a declared result the boundary cannot bring back.</summary>
    private void RequireCrossableResult(AxiomLiteral axiom)
    {
        if (axiom.ReturnType == CufetType.Number) return;
        throw TypeError(
            $"a {axiom.Language} axiom cannot give back a {FormatType(axiom.ReturnType!)} yet",
            "Only 'number' crosses the boundary so far",
            axiom.Line, axiom.Column,
            $"declare it as giving back a {FormatType(axiom.ReturnType!)}",
            "Declare it 'number' and let the source produce a whole number.");
    }

    /// <summary>Checks what an axiom says it takes, and that the foreign text asks for it.</summary>
    /// <remarks>
    /// ★ The parameter list is the only thing that says what C types the arguments have — the
    /// writer names no C type anywhere — so a type the boundary cannot carry has to be refused
    /// here, at the declaration, rather than at whichever call site happens to be written first.
    ///
    /// ⚠ A declared parameter the text never mentions is refused. It is not pedantry: `the paht`
    /// is left in the C verbatim (only DECLARED names are substituted), so a typo would otherwise
    /// surface as a gcc syntax error about a stray `the` — a message about the writer's spelling
    /// mistake, phrased in a language they were not writing.
    /// </remarks>
    private void CheckAxiomParameters(DefineStatement define, AxiomLiteral axiom)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (type, name) in axiom.Parameters)
        {
            var resolved = ResolveParamType(type);
            if (!ForeignC.CanPassToForeign(resolved))
                throw TypeError(
                    $"a {FormatType(resolved)} cannot be handed to {axiom.Language} source yet",
                    "A number, a fact and a text cross the boundary; nothing else does",
                    axiom.Line, axiom.Column,
                    $"declare '{name}' as a {FormatType(resolved)}",
                    "Take a number, a fact or a text instead, and do the rest inside the source.");

            if (!seen.Add(name))
                throw TypeError(
                    $"'{name}' is declared twice in this axiom's parameters",
                    "Each `the <name>` in the source has to name exactly one of them",
                    axiom.Line, axiom.Column,
                    $"declare '{name}' twice", "Give them different names.");

            if (!ForeignC.Mentions(axiom.Source, name))
                throw TypeError(
                    $"this {axiom.Language} source never uses 'the {name}'",
                    "A parameter reaches the source by its article — `the text path` here, "
                  + "`the path` in the source",
                    axiom.Line, axiom.Column,
                    $"declare '{name}' without using it",
                    $"Write 'the {name}' where the value belongs, or drop it from 'given'.");
        }

        if (define.HasParameterClause && axiom.Parameters.Count == 0)
            throw TypeError(
                $"'{define.Name}' declares an empty parameter list",
                null, define.Line, define.Column,
                "write 'given ()' with nothing in it",
                "Drop the 'given' clause — an axiom that takes nothing needs none.");
    }

    /// <summary>The type of a bare axiom literal — always tagged by then, or already refused.</summary>
    private CufetType InferAxiomLiteral(AxiomLiteral axiom) =>
        axiom.Language is { } language
            ? new AxiomType(language, axiom.ReturnType)
            : throw TypeError(
                "this is foreign source, and nothing says what language it is in",
                "An axiom takes its language from the declaration it is the value of",
                axiom.Line, axiom.Column,
                "write foreign source outside a declaration that names its language",
                "Give it a name first: 'Define c-language <name> as [ ... ].'");

    /// <summary>
    /// A return whose value is an axiom RUNS it, and marshals what comes back.
    /// </summary>
    /// <remarks>
    /// ★ The declared type decides, and that is the existing rule that a declared type is what a
    /// value must fit into rather than a new one. `Bind number to process-id, return get-pid.` runs
    /// the axiom; a `Bind` whose declared type IS an axiom hands it back unrun, which is what keeps
    /// composition possible for a language whose fragments are assembled before they are used.
    ///
    /// ⚠ Everything about the C side is taken on trust; the range check on the way back is not.
    /// A `number` holds every 64-bit integer exactly — 28–29 significant digits against 19 — so
    /// this direction cannot be lossy, which is why it is the one the first slice carries.
    /// </remarks>
    private CufetType RunAxiomOnReturn(ReturnStatement ret, CufetType? expected)
    {
        var axiom = AxiomBehind(ret.Value!);
        if (axiom?.Language is not { } language)
            throw TypeError(
                "an axiom can only be run by returning one that was declared",
                "Foreign source runs from a name, so there is always a declaration to read the "
              + "language and the source off",
                ret.Line, ret.Column,
                "run foreign source that has no declaration",
                "Declare it first: 'Define c-language <name> as [ ... ].' — then return that name.");

        RequireLanguagePulled(language, ret.Line, ret.Column);

        // ★ What it gives back is the axiom's own business now, and the enclosing function's
        // declared return type is checked against it the ordinary way, by the caller of this.
        ret.RunsAxiom = axiom;
        _ = expected;
        return RunResultOf(axiom, ret.Line, ret.Column);
    }

    /// <summary>An axiom reached for anywhere but a return.</summary>
    private TypeException AxiomUsedAsValue(string name, AxiomType axiom, IExpression at)
    {
        var (line, column) = at is VariableReference vr ? (vr.Line, vr.Column) : (0, 0);
        return TypeError(
            $"'{name}' is {axiom.Language} source, and can only be run by returning it",
            "Foreign source is not a value that can be printed, stored, or passed",
            line, column,
            $"use '{name}' as a value",
            $"Wrap it: 'Bind number to <name>, {name}.' — then use that function.");
    }

    /// <summary>A `Define`'s written type, resolved — or null when it declared none.</summary>
    private CufetType? ResolvedDeclaredType(CufetType? declared) =>
        declared is null ? null : ResolveParamType(declared);

    /// <summary>The axiom a `cast` reaches, or null when the call is an ordinary one.</summary>
    private AxiomLiteral? AxiomCalledBy(IExpression function) =>
        function is VariableReference vr && TryLookup(vr.Name, out var info) && info.Type is AxiomType
            ? info.EstablishingExpr as AxiomLiteral
            : null;

    /// <summary>
    /// `cast open-file on (path, flags)` — runs an axiom, and what comes back is the type declared
    /// where the call is used.
    /// </summary>
    /// <remarks>
    /// ★ The same rule as returning one, extended to the only other place an axiom can be reached:
    /// a declared type is what a value must fit into, and for an axiom it is also what decides what
    /// the value IS. Nothing about the C side can say it — `open` gives back an `int` that means a
    /// file descriptor, and only the writer knows that.
    ///
    /// ⚠ Which is why a use site that declares nothing is refused rather than guessed at. It is the
    /// cost of the rule, and it is the one the writer can see and fix.
    /// </remarks>
    private CufetType RunAxiomOnCast(CastExpression cast, AxiomLiteral axiom)
    {
        RequireLanguagePulled(axiom.Language!, cast.Line, cast.Column);
        CheckForeignArguments(cast, axiom);
        cast.RunsAxiom = axiom;
        return RunResultOf(axiom, cast.Line, cast.Column);
    }

    /// <summary>What running this axiom yields — refusing one that never said.</summary>
    /// <remarks>
    /// ★★ Read off the DECLARATION, which is the whole reason a call can now be written anywhere an
    /// ordinary call can. It used to come from the line USING the axiom, and that cost more than it
    /// looked like on paper: the call had to be the entire right-hand side of a typed binding, so
    /// it could not sit in a condition, in an interpolation, inside arithmetic, or as an argument —
    /// every result went through a named intermediate first.
    /// </remarks>
    private CufetType RunResultOf(AxiomLiteral axiom, int line, int column) =>
        axiom.ReturnType ?? throw TypeError(
            $"this {axiom.Language} axiom does not say what it gives back",
            "Cufet cannot read a C listing to find out — an `int` might be a number, a fact, or a "
          + "handle, and only the person who wrote the source knows which",
            line, column,
            "run foreign source that never declared a result",
            $"Say so where it is declared: 'Define {axiom.Language} number <name> as [ ... ].'");

    /// <summary>Checks a call's arguments against what the axiom declared it takes.</summary>
    private void CheckForeignArguments(CastExpression cast, AxiomLiteral axiom)
    {
        if (cast.Args.Count != axiom.Parameters.Count)
            throw TypeError(
                $"this {axiom.Language} source takes {Count(axiom.Parameters.Count, "value")}, "
              + $"and {cast.Args.Count} {(cast.Args.Count == 1 ? "was" : "were")} given",
                null, cast.Line, cast.Column,
                $"pass {Count(cast.Args.Count, "value")}",
                $"It is declared 'given ({string.Join(", ", axiom.Parameters.Select(p => $"the {FormatType(p.Type)} {p.Name}"))})'.");

        for (int i = 0; i < cast.Args.Count; i++)
        {
            var expected = ResolveParamType(axiom.Parameters[i].Type);
            var actual = InferType(cast.Args[i]);
            if (actual != null && !IsAssignable(expected, actual))
                throw TypeError(
                    $"'{axiom.Parameters[i].Name}' takes a {FormatType(expected)}, but a {FormatType(actual)} was given",
                    null, cast.Line, cast.Column,
                    $"pass a {FormatType(actual)} for '{axiom.Parameters[i].Name}'",
                    $"Give it a {FormatType(expected)}.");
        }
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>The axiom a returned expression stands for — the literal itself, or the one a name was defined as.</summary>
    private AxiomLiteral? AxiomBehind(IExpression value) => value switch
    {
        AxiomLiteral literal                                       => literal,
        VariableReference vr when TryLookup(vr.Name, out var info) => info.EstablishingExpr as AxiomLiteral,
        _                                                          => null,
    };

    /// <summary>Refuses foreign source whose language book is not pulled here.</summary>
    /// <remarks>
    /// ⚠ The book is what admits the language, so it is required even though it has no members to
    /// offer. Foreign source is the one place where being wrong means memory corruption rather than
    /// a wrong number, and a pull is the line a reader can see it on.
    /// </remarks>
    private void RequireLanguagePulled(string language, int line, int column)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            foreach (var info in _scopes[i].Values)
                if (info.Type is BookType { Name: var pulled } && IsSameLanguage(pulled, language))
                    return;
                else if (info.Type is ObjectType { Name: var layer } && IsSameLanguage(layer, language))
                    return;

        throw TypeError(
            $"the {language} book is not in scope",
            null, line, column,
            $"write {language} source without pulling the {language} book",
            $"Add 'Pull a book on the {language}.' around this.");
    }

    private static bool IsSameLanguage(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
