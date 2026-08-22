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

        if (define.Value is not AxiomLiteral axiom) return declared;

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
        return declared;
    }

    /// <summary>The type of a bare axiom literal — always tagged by then, or already refused.</summary>
    private CufetType InferAxiomLiteral(AxiomLiteral axiom) =>
        axiom.Language is { } language
            ? new AxiomType(language)
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
    private CufetType RunAxiomOnReturn(ReturnStatement ret, CufetType expected)
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

        // The boundary conversions land here as they are built. `number` from a C integer is the
        // first, and it is first because it is the one that cannot be lossy.
        if (expected != CufetType.Number)
            throw TypeError(
                $"a {language} axiom cannot come back as a {FormatType(expected)} yet",
                "Only 'number' crosses the boundary so far",
                ret.Line, ret.Column,
                $"return a {language} axiom into a {FormatType(expected)}",
                "Declare the function 'Bind number to <name>' and let the axiom produce a whole number.");

        ret.RunsAxiom = axiom;
        return CufetType.Number;
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
