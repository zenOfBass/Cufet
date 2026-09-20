namespace Cufet.Interpreter;

/// <summary>
/// Fills a parameterised definition's blanks, producing an ORDINARY object definition.
/// </summary>
/// <remarks>
/// <para>
/// ★★ Monomorphization, done in the front end. `a stack of number` becomes a concrete definition
/// named `stack of number` with every `element` replaced, so neither backend learns what a template
/// is: the interpreter sees an object, the compiler emits a struct, and the topological sort, the
/// deep-copy family and the escape analysis all keep working untouched. This is the same move the
/// language already makes twice — stashes lower to closures before either backend runs, and an
/// interface-taking function is specialised per conformer.
/// </para>
/// <para>
/// ★ The instantiated NAME contains spaces (`stack of number`). That is deliberate: it can never
/// collide with a name a writer could type, because an identifier has no spaces in it, and it is
/// what an error message should say anyway. The compiler mangles it on the way to C.
/// </para>
/// </remarks>
internal static class GenericInstantiation
{
    /// <summary>The name of one filling — `stack of number`, `pair of text of number`.</summary>
    public static string NameFor(string templateName, IReadOnlyList<CufetType> arguments) =>
        templateName + string.Concat(arguments.Select(a => " of " + TypeChecker.FormatType(a)));

    /// <summary>
    /// Builds the concrete definition for one filling.
    /// </summary>
    /// <remarks>
    /// ⚠ The blanks are matched by NAME, and a blank reaches the tree as an ObjectType shell —
    /// `the element value` parses as a named type exactly like `the racer value` does. So the
    /// substitution replaces shells whose name is a blank and leaves every other type alone; it
    /// never descends INTO an ObjectType, which is both unnecessary (a nominal type's fields travel
    /// with its own definition) and unsound to try (a recursive type would not terminate).
    /// </remarks>
    public static ObjectDefinition Fill(
        ObjectDefinition template, string filledName, IReadOnlyDictionary<string, CufetType> blanks)
    {
        // ⚠ Deep, not top-level. A blank almost never appears bare — `the series of element items`
        // is the ordinary case — and a top-level-only match walks straight past it while still
        // reporting that the tree changed.
        CufetType Substitute(CufetType type) => AstRebuilder.SubstituteDeep(type, Blank);

        CufetType Blank(CufetType type) =>
            type is ObjectType { TypeArguments.Count: 0 } shell && blanks.TryGetValue(shell.Name, out var filling)
                ? filling
                : type;

        var filled = AstRebuilder.Rebuild(template, Substitute);

        // The name and the blank list are the two things the rebuild cannot supply: one is a string,
        // and the other has to be EMPTIED or the result would look like a template again.
        return filled with { Name = filledName, TypeParameters = [] };
    }

    /// <summary>The same filling, for a function that left blanks in its signature.</summary>
    /// <remarks>
    /// ★ A function needs no blank list emptied: it never carried one. Its blanks were READ from
    /// the signature rather than declared, so once they are filled the signature names only real
    /// types and nothing marks it as a template any more.
    /// </remarks>
    public static BindStatement FillFunction(
        BindStatement template, string filledName, IReadOnlyDictionary<string, CufetType> blanks)
    {
        CufetType Substitute(CufetType type) => AstRebuilder.SubstituteDeep(type, leaf =>
            leaf is ObjectType { TypeArguments.Count: 0 } shell && blanks.TryGetValue(shell.Name, out var filling)
                ? filling
                : leaf);

        return AstRebuilder.Rebuild(template, Substitute) with { Name = filledName };
    }
}
