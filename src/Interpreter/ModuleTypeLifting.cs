namespace Cufet.Interpreter;

/// <summary>
/// Moves the types a module CARRIES out to the top level, under names nobody can write, and records
/// which module carried which — so a `Pull` can put the short name back in scope for the length of
/// its block.
/// </summary>
/// <remarks>
/// <para>
/// ★ **A front-end pass, so neither backend learns that modules can hold types.** By the time the
/// hoist runs, a carried type is an ordinary top-level object declaration and the module is an
/// ordinary object. Both are handed the same program they would have been handed if the author had
/// written the type at the top level themselves.
/// </para>
/// <para>
/// ★ **The lifted name has a SPACE in it** — `point in shapes` — the same trick the book loader's
/// file privacy uses, and unwritable for the same reason: no identifier may contain one. Lifting
/// therefore cannot make the type globally reachable by accident, and the only way to name it stays
/// the pull that introduces it. Two modules may each carry a `point` without colliding, because the
/// module's name is in the lifted name.
/// </para>
/// <para>
/// ⚠ The substitution is scoped to the module and its own carried types, never applied across the
/// whole program. A top-level type elsewhere may legitimately share a carried type's short name,
/// and a global rewrite would capture it.
/// </para>
/// </remarks>
public static class ModuleTypeLifting
{
    /// <summary>The lifted name of a type carried by a module. Unwritable, by the space.</summary>
    public static string LiftedName(string moduleName, string typeName) => $"{typeName} in {moduleName}";

    /// <summary>
    /// The name to SHOW for a type — the one its author wrote, with any owner the front end
    /// appended taken back off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ **Both backends must call this, and the oracle cannot tell you if one forgets.** They
    /// would leak the same synthesized name and agree with each other about it — which is exactly
    /// how <c>tally in privkit(count: 5)</c> shipped in 0.18.0 and went unnoticed: the book
    /// loader's file-privacy rename has always leaked into printed output, and no test could see it
    /// because agreement was never the thing that was wrong.
    /// </para>
    /// <para>
    /// ★ Trimmed at the LAST <c>" in "</c>, not the first, so a carried generic keeps the part that
    /// is really its name: <c>stack of number in shapes</c> shows as <c>stack of number</c>. A name
    /// a person wrote can never contain a space, so nothing legitimate is reachable here.
    /// </para>
    /// </remarks>
    public static string DisplayName(string name)
    {
        int at = name.LastIndexOf(" in ", StringComparison.Ordinal);
        return at > 0 ? name[..at] : name;
    }

    /// <summary>
    /// Rewrites the program, and reports what each module carried as
    /// <c>module → (short name → lifted name)</c>.
    /// </summary>
    public static Program Expand(
        Program program,
        out IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> carried)
    {
        var found = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var rewritten = new List<IStatement>();
        bool any = false;

        foreach (var stmt in program.Statements)
        {
            if (stmt is not ObjectDefinition def || def.Carried is not { Count: > 0 } held)
            {
                rewritten.Add(stmt);
                continue;
            }

            any = true;
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in held)
                names[type.Name] = LiftedName(def.Name, type.Name);

            // The module and the types it carries, rewritten together — they are the only
            // statements in which the short name means the carried type.
            var mine = new List<IStatement>(held.Count + 1);
            foreach (var type in held) mine.Add(type with { Carried = null });
            mine.Add(def with { Carried = null });

            // Types first: AstSearch deliberately does not descend into a CufetType, so the two
            // rewrites do not overlap. Same split as the loader's privacy rename.
            var rebuilt = AstRebuilder.Apply(mine,
                t => AstRebuilder.SubstituteDeep(t, leaf =>
                    leaf is ObjectType o && names.TryGetValue(o.Name, out var to)
                        ? new ObjectType(to, o.PositionalTypes, o.NamedFields, o.Methods)
                        : leaf));

            AstSearch.Visit(rebuilt, node =>
            {
                if (node is ObjectLiteral lit && names.TryGetValue(lit.TypeName, out var to))
                    lit.TypeName = to;
            });

            // The declarations themselves, renamed to match what now refers to them. The module
            // keeps its own name — it is the thing a pull reaches.
            foreach (var statement in rebuilt)
                rewritten.Add(statement is ObjectDefinition o && names.TryGetValue(o.Name, out var lifted)
                    ? o with { Name = lifted }
                    : statement);

            found[def.Name] = names;
        }

        carried = found;
        return any ? new Program(rewritten) : program;
    }
}
