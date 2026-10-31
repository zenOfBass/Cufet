namespace Cufet.Interpreter;

/// <summary>
/// Puts each shorthand's type back wherever its name is written, and takes the declarations out.
/// </summary>
/// <remarks>
/// <para>
/// ★ **A front-end pass, so no backend learns that shorthands exist.** `Define shorthand gate as
/// (and-gate or or-gate).` makes `gate` another way to WRITE that union and nothing more: after this
/// pass the program is the one its author would have written by spelling the union out each time,
/// and the checker, the interpreter and both compilers are handed exactly that.
/// </para>
/// <para>
/// ★ **The same as writing it out, down to the shape.** The parser turns `(number or void)` into a
/// voidable and a union written inside a union is still one union to a reader, so a substituted
/// type is folded the same way: `(gate or wire)` is the flat union of all the members, and
/// `(gate or void)` the union with a void case that writing it in full would have made.
/// </para>
/// <para>
/// ★ Program-scope wherever it is written, as every TYPE declaration is (see
/// <see cref="AstSearch.EveryStatement"/>). Runs after the book loader, whose privacy rename has
/// already made a book's own shorthands names nobody else can write.
/// </para>
/// </remarks>
public static class ShorthandExpansion
{
    /// <param name="names">Every shorthand declared, for the editor's colouring — the program
    /// handed back holds none of them.</param>
    public static Program Expand(Program program, out IReadOnlySet<string> names)
    {
        names = new HashSet<string>();
        var declared = new Dictionary<string, ShorthandDefinition>(StringComparer.Ordinal);
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in AstSearch.EveryStatement(program.Statements))
        {
            if (statement is ObjectDefinition o) typeNames.Add(o.Name);
            else if (statement is InterfaceDefinition i) typeNames.Add(i.Name);
        }
        foreach (var statement in AstSearch.EveryStatement(program.Statements))
        {
            if (statement is not ShorthandDefinition shorthand) continue;
            string shown = ModuleTypeLifting.DisplayName(shorthand.Name);
            if (declared.TryGetValue(shorthand.Name, out var first))
                throw TypeChecker.TypeError(
                    $"the shorthand '{shown}' is already defined on line {SourceMap.Display(first.Line)}",
                    null, shorthand.Line, shorthand.Column, $"define the shorthand '{shown}' a second time",
                    "A shorthand stands for one type. Give the second one a different name.");
            if (typeNames.Contains(shorthand.Name))
                throw TypeChecker.TypeError(
                    $"'{shown}' is already the name of a type",
                    null, shorthand.Line, shorthand.Column, $"define a shorthand called '{shown}'",
                    "A shorthand is another way to write a type, so it cannot share a name with one. Give it a different name.");
            declared[shorthand.Name] = shorthand;
        }
        if (declared.Count == 0) return program;
        names = declared.Keys.ToHashSet(StringComparer.Ordinal);

        // Each shorthand's own type first, so one may be written with another — and one that comes
        // back round to itself is refused rather than unfolded forever.
        var resolved = new Dictionary<string, CufetType>(StringComparer.Ordinal);
        CufetType Resolve(ShorthandDefinition shorthand, List<string> path)
        {
            if (resolved.TryGetValue(shorthand.Name, out var done)) return done;
            if (path.Contains(shorthand.Name))
            {
                var ring = string.Join(" → ", path.SkipWhile(n => n != shorthand.Name)
                    .Append(shorthand.Name).Select(ModuleTypeLifting.DisplayName));
                var start = declared[path[0]];
                throw TypeChecker.TypeError(
                    $"the shorthand '{ModuleTypeLifting.DisplayName(path[0])}' stands for itself ({ring})",
                    null, start.Line, start.Column,
                    $"define '{ModuleTypeLifting.DisplayName(path[0])}' in terms of itself",
                    "A shorthand is only another way to write a type, so it has to come down to a type in the end. Write the type out in one of them.");
            }
            path.Add(shorthand.Name);
            var type = Substituted(shorthand.Type, leaf => Leaf(leaf, path));
            path.RemoveAt(path.Count - 1);
            resolved[shorthand.Name] = type;
            return type;
        }
        CufetType Leaf(CufetType leaf, List<string> path) =>
            leaf is ObjectType { TypeArguments.Count: 0 } named && declared.TryGetValue(named.Name, out var meant)
                ? Resolve(meant, path)
                : leaf;
        foreach (var shorthand in declared.Values) Resolve(shorthand, []);

        // `a new gate` names an OBJECT to make, and a shorthand is not one.
        AstSearch.Visit(program.Statements, node =>
        {
            if (node is ObjectLiteral { TypeName: var made } literal && declared.ContainsKey(made))
            {
                var shown = ModuleTypeLifting.DisplayName(made);
                throw TypeChecker.TypeError(
                    $"'{shown}' is a shorthand for a type, not an object",
                    null, literal.Line, literal.Column, $"make a new '{shown}'",
                    $"Only an object can be made. Make one of the types '{shown}' stands for.");
            }
        });

        var rewritten = AstRebuilder.Apply(program.Statements,
            t => Substituted(t, leaf => Leaf(leaf, [])),
            splice: statement => statement is ShorthandDefinition ? [] : null);
        return ReferenceEquals(rewritten, program.Statements) ? program : new Program(rewritten);
    }

    /// <summary>A type as the parser would have read it written out: unions flat, void folded.</summary>
    /// <remarks>Applied only to a type a shorthand was substituted into, so a type with none in it
    /// is handed on exactly as it was parsed.</remarks>
    private static CufetType Substituted(CufetType type, Func<CufetType, CufetType> leaf)
    {
        var substituted = AstRebuilder.SubstituteDeep(type, leaf);
        return ReferenceEquals(substituted, type) ? type : Folded(substituted);
    }

    private static CufetType Folded(CufetType type) => AstRebuilder.SubstituteDeep(type, node => node switch
    {
        UnionType { Cases: { } cases } => FoldedUnion(cases),
        // `(gate or void)` is the union with a void case, as it is written out in full.
        VoidableType voidable => Folded(voidable.Inner) switch
        {
            UnionType { Cases: { } held } => new UnionType([.. held, CufetType.Void]),
            var inner => new VoidableType(inner),
        },
        _ => node,
    });

    private static CufetType FoldedUnion(IReadOnlyList<CufetType> cases)
    {
        var flat = new List<CufetType>();
        foreach (var written in cases)
            switch (Folded(written))
            {
                case UnionType { Cases: { } more }: flat.AddRange(more); break;
                case var inner: flat.Add(inner); break;
            }
        // (T or void) → VoidableType(T), as the parser reads it.
        if (flat.Count == 2 && flat.Any(c => c is VoidType))
            return new VoidableType(flat.First(c => c is not VoidType));
        return new UnionType(flat);
    }
}
