namespace Cufet.Interpreter;

/// <summary>
/// Expands an interface's DEFAULT methods — `Bind text to describe unto &lt;interface&gt;` — into one
/// ordinary method per conforming object type.
/// </summary>
/// <remarks>
/// <para>
/// ★ This runs in the parser, and that is the whole design. After it, an interface default is
/// indistinguishable from a method the conformer wrote itself: the type checker, the interpreter and
/// the code generator never learn the feature exists, and both backends get it for free. It is the
/// same move inline forms and <c>Increment</c> made — the cost of a feature is where you put it.
/// </para>
/// <para>
/// It also makes the four settled rules fall out rather than needing machinery:
/// </para>
/// <list type="bullet">
/// <item><b>A default satisfies conformance.</b> The conformer really does have the method by the
/// time conformance is checked, so the existing check passes with nothing added to it. An
/// interface's method list is what a conformer ends up with, not what it must write.</item>
/// <item><b>A type's own method beats the default</b> — the default is simply not injected when the
/// type already has that name, from its own body, its own <c>unto</c>, or an embedded type.</item>
/// <item><b>Two interfaces supplying the same name is refused</b>, here, with a message that names
/// both. Injecting both would collide anyway, but on "already has a method", which explains the
/// symptom rather than the cause.</item>
/// <item><b>Monomorphization is untouched.</b> Each conformer gets its own copy of the body, so a
/// default has a concrete receiver at every call site — no vtable, no type tag, nothing relaxed.</item>
/// </list>
/// <para>
/// The copies keep the ORIGINAL body's line and column. A default that is wrong for one conformer
/// should point at the text the author actually wrote, not at a position no one can open — and it
/// will be reported once per conformer it is wrong for, which is accurate: it really is wrong that
/// many times.
/// </para>
/// </remarks>
public static class InterfaceDefaults
{
    public static IReadOnlyList<IStatement> Expand(IReadOnlyList<IStatement> statements)
    {
        var interfaceNames = new HashSet<string>(StringComparer.Ordinal);
        var objects = new List<ObjectDefinition>();
        var defaultsByInterface = new Dictionary<string, List<BindStatement>>(StringComparer.Ordinal);

        foreach (var stmt in Flatten(statements))
        {
            if (stmt is InterfaceDefinition ifd) interfaceNames.Add(ifd.Name);
            else if (stmt is ObjectDefinition od) objects.Add(od);
        }

        foreach (var stmt in Flatten(statements))
        {
            if (stmt is BindStatement { UntoType: { } target } bind && interfaceNames.Contains(target))
            {
                if (!defaultsByInterface.TryGetValue(target, out var list))
                    defaultsByInterface[target] = list = [];
                list.Add(bind);
            }
        }

        // Nothing to do — and this is the common case, so the tree is handed back untouched rather
        // than rebuilt. Every program that does not use a default parses to exactly what it did.
        if (defaultsByInterface.Count == 0) return statements;

        var byName = objects.ToDictionary(o => o.Name, StringComparer.Ordinal);
        var injected = new List<IStatement>();

        foreach (var od in objects)
        {
            // Which interface supplied each defaulted name, so a clash can name both.
            var takenFrom = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var ifaceName in od.ConformedInterfaces)
            {
                if (!defaultsByInterface.TryGetValue(ifaceName, out var defaults)) continue;

                foreach (var def in defaults)
                {
                    // Specialisation: anything the type can already answer to wins outright.
                    if (HasMethod(od, def.Name, byName, statements)) continue;

                    if (takenFrom.TryGetValue(def.Name, out var already))
                        throw new ParseException(def.Line, def.Column,
                            $"'{od.Name}' would get a default '{def.Name}' from both '{already}' and "
                            + $"'{ifaceName}', and there is no rule for which should win. Give '{od.Name}' "
                            + $"its own '{def.Name}' — a type's own method beats an interface default.");

                    takenFrom[def.Name] = ifaceName;
                    injected.Add(def with { UntoType = od.Name });
                }
            }
        }

        // The originals are dropped: `unto <interface>` is not a method on anything, and leaving it
        // in would hit the type checker's "that is an interface, not an object type" refusal.
        var kept = Strip(statements, interfaceNames);
        return [.. kept, .. injected];
    }

    // Own body, own `unto`, or promoted through an embedded type — the same three places
    // conformance already looks, so specialisation and conformance agree about what a type "has".
    private static bool HasMethod(
        ObjectDefinition od,
        string methodName,
        Dictionary<string, ObjectDefinition> byName,
        IReadOnlyList<IStatement> statements)
    {
        if (od.Methods.Any(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))) return true;

        foreach (var stmt in Flatten(statements))
            if (stmt is BindStatement { UntoType: { } t } b
                && string.Equals(t, od.Name, StringComparison.Ordinal)
                && string.Equals(b.Name, methodName, StringComparison.Ordinal))
                return true;

        // Embedding is a chain, and a cycle in it is the type checker's error to report, not this
        // pass's — so the walk is bounded rather than trusting the chain to terminate.
        var seen = new HashSet<string>(StringComparer.Ordinal) { od.Name };
        var current = od;
        while (current.EmbeddedTypeName is { } embedded && seen.Add(embedded))
        {
            if (!byName.TryGetValue(embedded, out var next)) break;
            if (next.Methods.Any(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))) return true;
            current = next;
        }
        return false;
    }

    private static IEnumerable<IStatement> Flatten(IEnumerable<IStatement> statements)
    {
        foreach (var s in statements)
        {
            yield return s;
            if (s is PullStatement ps)
                foreach (var inner in Flatten(ps.Body)) yield return inner;
            if (s is PullRabbitStatement prs)
                foreach (var inner in Flatten(prs.Body)) yield return inner;
        }
    }

    private static List<IStatement> Strip(IReadOnlyList<IStatement> statements, HashSet<string> interfaceNames)
    {
        var kept = new List<IStatement>(statements.Count);
        foreach (var s in statements)
        {
            switch (s)
            {
                case BindStatement { UntoType: { } t } when interfaceNames.Contains(t):
                    continue;
                case PullStatement ps:
                    kept.Add(ps with { Body = Strip(ps.Body, interfaceNames) });
                    break;
                case PullRabbitStatement prs:
                    kept.Add(prs with { Body = Strip(prs.Body, interfaceNames) });
                    break;
                default:
                    kept.Add(s);
                    break;
            }
        }
        return kept;
    }
}
