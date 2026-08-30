namespace Cufet.Interpreter;

/// <summary>
/// Turns several functions that share a name into one function that picks between them.
/// </summary>
/// <remarks>
/// <para>
/// ⭐⭐ A front-end pass, ahead of the hoist, the same arrangement <see cref="CiteExpansion"/>
/// uses. After it, "a name with several versions" is not a thing that exists: each version is an
/// ordinary function under a name of its own, and the name the writer called is an ordinary
/// function whose body is an ordinary <c>Judge</c>. Neither backend has a word to say about
/// dispatch, and nothing downstream needs one.
/// </para>
/// <para>
/// ★★ The dispatcher is a <c>Judge</c> because <c>Judge</c> already does this job. It reads the
/// runtime tag a closed union carries, it narrows the subject inside each arm, and it PROVES the
/// arms cover every case. Writing the dispatcher any other way would mean re-deriving all three,
/// and the coverage proof is the one that matters most: an arm per version is generated from the
/// versions themselves, so it cannot fall out of step with them.
/// </para>
/// <para>
/// ⚠ Top-level declarations only. A group spanning `Pull` blocks is left to the hoist's
/// duplicate-name refusal, deliberately — versions living in different blocks is a question about
/// where a name is open for extension, and that is not settled.
/// </para>
/// </remarks>
public static class DispatchExpansion
{
    /// <summary>
    /// The name a single version is given. Spaces make it unwritable as an identifier, which is
    /// the same trick monomorphization uses for `unique of text`.
    /// </summary>
    private static string VersionName(string name, CufetType dispatchOn) =>
        $"{name} given {TypeChecker.FormatType(dispatchOn)}";

    /// <summary>
    /// Hands back the very same list when no name has more than one version, which is the usual
    /// case.
    /// </summary>
    public static IReadOnlyList<IStatement> Expand(IReadOnlyList<IStatement> statements)
    {
        var groups = new Dictionary<string, List<BindStatement>>(StringComparer.Ordinal);
        foreach (var statement in statements)
            if (statement is BindStatement { UntoType: null } bind)
            {
                if (!groups.TryGetValue(bind.Name, out var versions))
                    groups[bind.Name] = versions = new List<BindStatement>();
                versions.Add(bind);
            }

        if (!groups.Values.Any(v => v.Count > 1)) return statements;

        var replacements = new Dictionary<string, List<IStatement>>(StringComparer.Ordinal);
        foreach (var (name, versions) in groups)
            if (versions.Count > 1)
                replacements[name] = BuildDispatch(name, versions);

        var expanded = new List<IStatement>();
        var emitted  = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            if (statement is BindStatement { UntoType: null } bind
                && replacements.TryGetValue(bind.Name, out var built))
            {
                // ⚠ Emitted where the FIRST version stood, and nowhere else. Functions are
                // hoisted, so position does not decide what can call them — but emitting the group
                // once keeps the statement list a faithful record of what the program holds.
                if (emitted.Add(bind.Name)) expanded.AddRange(built);
                continue;
            }
            expanded.Add(statement);
        }
        return expanded;
    }

    private static List<IStatement> BuildDispatch(string name, List<BindStatement> versions)
    {
        var first = versions[0];

        // ── What every version has to agree on ────────────────────────────────────────────────
        foreach (var other in versions.Skip(1))
        {
            if (other.Parameters.Count != first.Parameters.Count)
                throw TypeChecker.TypeError(
                    $"the versions of '{name}' take different numbers of arguments",
                    $"The one on line {first.Line} takes {first.Parameters.Count}, "
                  + $"and this one takes {other.Parameters.Count}",
                    other.Line, other.Column,
                    $"declare a version of '{name}' with a different number of arguments",
                    "Every version of a name is reached by the same call, so they all take the "
                  + "same arguments. Rename this one if it does something else.");

            if (!Equals(other.ReturnType, first.ReturnType))
                throw TypeChecker.TypeError(
                    $"the versions of '{name}' give back different types",
                    $"The one on line {first.Line} gives back "
                  + $"{(first.ReturnType is null ? "nothing" : TypeChecker.FormatType(first.ReturnType))}, "
                  + $"and this one gives back "
                  + $"{(other.ReturnType is null ? "nothing" : TypeChecker.FormatType(other.ReturnType))}",
                    other.Line, other.Column,
                    $"declare a version of '{name}' with another return type",
                    "A caller has to know what it gets back without knowing which version ran, so "
                  + "every version gives back the same type.");
        }

        // ── Which argument is dispatched on ───────────────────────────────────────────────────
        //
        // ★ Exactly one position may vary. Dispatching on several at once is the product of their
        // cases, which is a nested Judge and a wider question — it is left for its own slice
        // rather than smuggled in here.
        var varying = new List<int>();
        for (int i = 0; i < first.Parameters.Count; i++)
        {
            var atI = versions.Select(v => v.Parameters[i].Type).ToList();
            if (atI.Distinct().Count() > 1) varying.Add(i);
        }

        if (varying.Count == 0)
            throw TypeChecker.TypeError(
                $"'{name}' is declared twice with the same argument types",
                $"It was already declared on line {first.Line}",
                versions[1].Line, versions[1].Column,
                $"declare '{name}' again with nothing to tell the two apart",
                "Versions of a name are told apart by the type of one argument. These take the "
              + "same types, so no call could pick between them. Rename one of them.");

        if (varying.Count > 1)
            throw TypeChecker.TypeError(
                $"the versions of '{name}' differ in more than one argument",
                $"Arguments {string.Join(" and ", varying.Select(i => i + 1))} vary between them",
                versions[1].Line, versions[1].Column,
                $"tell the versions of '{name}' apart by more than one argument",
                "Versions are told apart by ONE argument's type. Make the others agree, or give "
              + "these different names.");

        int at = varying[0];
        var cases = versions.Select(v => v.Parameters[at].Type).ToList();

        // Two versions may not claim the same case — nothing could pick between them, and the
        // generated Judge would carry two arms for one type.
        var seen = new HashSet<CufetType>();
        foreach (var version in versions)
            if (!seen.Add(version.Parameters[at].Type))
                throw TypeChecker.TypeError(
                    $"two versions of '{name}' take the same {TypeChecker.FormatType(version.Parameters[at].Type)}",
                    "Versions are told apart by that argument's type, so two of them cannot claim it",
                    version.Line, version.Column,
                    $"declare a second version of '{name}' for the same type",
                    "Rename one of them.");

        // ── The pieces ────────────────────────────────────────────────────────────────────────
        var built = new List<IStatement>();
        var arms  = new List<JudgeArm>();
        string subject = first.Parameters[at].Name;

        foreach (var version in versions)
        {
            var caseType = version.Parameters[at].Type;
            var renamed  = VersionName(name, caseType);
            built.Add(version with { Name = renamed });

            // ⚠ `it`, not the parameter's own name, at the dispatched position. `Judge` narrows
            // its subject under `it`, and the version being called wants the narrowed type — the
            // parameter still holds the whole union.
            var args = new List<IExpression>();
            for (int i = 0; i < first.Parameters.Count; i++)
                args.Add(new VariableReference(
                    i == at ? "it" : first.Parameters[i].Name, version.Line, version.Column));

            var call = new CastExpression(
                new VariableReference(renamed, version.Line, version.Column),
                args, version.Line, version.Column);

            arms.Add(new JudgeArm(
                [caseType],
                first.ReturnType is null
                    ? [new CastStatement(call.Function, call.Args, version.Line, version.Column)]
                    : [new ReturnStatement(call, version.Line, version.Column)],
                version.Line, version.Column));
        }

        var dispatcherParams = first.Parameters
            .Select((p, i) => i == at ? (Type: (CufetType)new UnionType(cases), p.Name) : p)
            .ToList();

        built.Add(new BindStatement(
            name,
            first.ReturnType,
            dispatcherParams,
            [new JudgeStatement(
                new VariableReference(subject, first.Line, first.Column),
                arms, OtherwiseBody: null, first.Line, first.Column)],
            UntoType: null,
            ConstructsTypeName: null,
            first.Line, first.Column));

        return built;
    }
}
