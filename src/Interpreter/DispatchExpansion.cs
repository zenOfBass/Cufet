namespace Cufet.Interpreter;

/// <summary>
/// Turns several functions that share a name into one function that picks between them.
/// </summary>
/// <remarks>
/// <para>
/// ⭐⭐ A front-end pass, ahead of the hoist, the same arrangement <see cref="CiteExpansion"/>
/// uses. After it, "a name with several versions" is not a thing that exists: each version is an
/// ordinary function under a name of its own, and the name the writer called is an ordinary
/// function built out of <c>Judge</c> and <c>If</c>. Neither backend has a word to say about
/// dispatch, and nothing downstream needs one.
/// </para>
/// <para>
/// ★★ Versions are told apart two ways, and they compose. By the TYPE of one argument, which
/// becomes a <c>Judge</c> — it reads the tag a closed union carries, narrows the subject per arm,
/// and PROVES the arms cover every case. And by a <c>when</c> CONDITION, which becomes an
/// <c>If</c> chain over versions already shown to be pairwise disjoint.
/// </para>
/// <para>
/// ★★ That disjointness proof is what makes the generated <c>If</c> chain honest. An ordinary
/// chain is order-dependent — the first arm that holds wins — and order-dependence across
/// declarations, or worse across files, is the fragility this design exists to avoid. Because no
/// two conditions can hold at once, the chain answers the same in any order, so the order it
/// happens to be generated in carries no meaning.
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
    private static string VersionName(string name, CufetType dispatchOn, int ordinal) =>
        ordinal == 0 ? $"{name} given {TypeChecker.FormatType(dispatchOn)}"
                     : $"{name} given {TypeChecker.FormatType(dispatchOn)} {ordinal}";

    /// <summary>
    /// Hands back the very same list when nothing shares a name and nothing carries a condition,
    /// which is the usual case.
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

        // ⚠ A LONE version carrying a condition still comes through here — it needs the fallback
        // rule applied to it, and without this it would be quietly accepted as an ordinary
        // function whose condition nothing ever reads.
        if (!groups.Values.Any(v => v.Count > 1 || v[0].When is not null)) return statements;

        var replacements = new Dictionary<string, List<IStatement>>(StringComparer.Ordinal);
        foreach (var (name, versions) in groups)
            if (versions.Count > 1 || versions[0].When is not null)
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

    /// <summary>Versions sharing one parameter-type signature, told apart by their conditions.</summary>
    private sealed class Signature
    {
        public required IReadOnlyList<CufetType> Types { get; init; }
        public List<BindStatement>               Conditioned { get; } = [];
        public BindStatement?                    Fallback { get; set; }
    }

    private static List<IStatement> BuildDispatch(string name, List<BindStatement> versions)
    {
        var first = versions[0];

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

        var signatures = GroupBySignature(name, versions);

        // ── Which argument is dispatched on ───────────────────────────────────────────────────
        int at = -1;
        if (signatures.Count > 1)
        {
            var varying = new List<int>();
            for (int i = 0; i < first.Parameters.Count; i++)
                if (signatures.Select(sig => sig.Types[i]).Distinct().Count() > 1) varying.Add(i);

            if (varying.Count > 1)
                throw TypeChecker.TypeError(
                    $"the versions of '{name}' differ in more than one argument",
                    $"Arguments {string.Join(" and ", varying.Select(i => i + 1))} vary between them",
                    versions[1].Line, versions[1].Column,
                    $"tell the versions of '{name}' apart by more than one argument",
                    "Versions are told apart by ONE argument's type. Make the others agree, or "
                  + "give these different names.");

            at = varying[0];
        }

        // ── The pieces ────────────────────────────────────────────────────────────────────────
        var built = new List<IStatement>();

        if (signatures.Count == 1)
        {
            // Told apart by condition alone — no type to judge, so the body is the chain itself.
            var only = signatures[0];
            var on   = first.Parameters.Count > 0 ? first.Parameters[0].Type : CufetType.Void;
            built.AddRange(RenamedVersions(name, only, on));
            built.Add(Dispatcher(name, first, first.Parameters,
                ConditionChain(name, only, first, on, dispatchedArg: null, at: -1)));
            return built;
        }

        var arms = new List<JudgeArm>();
        foreach (var sig in signatures)
        {
            var on = sig.Types[at];
            built.AddRange(RenamedVersions(name, sig, on));
            var anchor = sig.Fallback ?? sig.Conditioned[0];
            arms.Add(new JudgeArm(
                [on],
                ConditionChain(name, sig, first, on, dispatchedArg: "it", at: at),
                anchor.Line, anchor.Column));
        }

        var dispatcherParams = first.Parameters
            .Select((p, i) => i == at
                ? (Type: (CufetType)new UnionType(signatures.Select(s => s.Types[at]).ToList()), p.Name)
                : p)
            .ToList();

        built.Add(Dispatcher(name, first, dispatcherParams,
            [new JudgeStatement(
                new VariableReference(first.Parameters[at].Name, first.Line, first.Column),
                arms, OtherwiseBody: null, first.Line, first.Column)]));
        return built;
    }

    // ── Grouping, and the condition rules ─────────────────────────────────────────────────────

    private static List<Signature> GroupBySignature(string name, List<BindStatement> versions)
    {
        var signatures = new List<Signature>();
        foreach (var version in versions)
        {
            var types = version.Parameters.Select(p => p.Type).ToList();
            var sig = signatures.FirstOrDefault(s => s.Types.SequenceEqual(types));
            if (sig is null) signatures.Add(sig = new Signature { Types = types });

            if (version.When is null)
            {
                if (sig.Fallback is not null)
                    throw TypeChecker.TypeError(
                        $"'{name}' is declared twice with the same argument types",
                        $"It was already declared on line {sig.Fallback.Line}",
                        version.Line, version.Column,
                        $"declare '{name}' again with nothing to tell the two apart",
                        "Versions of a name are told apart by one argument's type or by a 'when' "
                      + "condition. These have neither, so no call could pick between them. "
                      + "Rename one of them.");
                sig.Fallback = version;
            }
            else sig.Conditioned.Add(version);
        }

        foreach (var sig in signatures) CheckConditions(name, sig);
        return signatures;
    }

    private static void CheckConditions(string name, Signature sig)
    {
        if (sig.Conditioned.Count == 0) return;

        // ⚠ A condition can be false, and something has to run then. Proving that a SET of
        // conditions covers every case is tautology checking, which this fragment does not
        // promise — so the coverage is required to be written rather than inferred.
        //
        // ★ Widening this later is additive: programs refused here would start compiling, and
        // nothing that compiles today would change meaning.
        if (sig.Fallback is null)
            throw TypeChecker.TypeError(
                $"every version of '{name}' carries a condition",
                "A condition can be false, and something has to run when they all are",
                sig.Conditioned[0].Line, sig.Conditioned[0].Column,
                $"leave '{name}' with nothing to run when no condition holds",
                "Add a version with no 'when' — it runs when none of the others apply.");

        var read = new List<(BindStatement Version, DispatchConditions.Clause Clause)>();
        foreach (var version in sig.Conditioned)
        {
            var clause = DispatchConditions.Read(version.When!);
            if (clause is null)
                throw TypeChecker.TypeError(
                    $"this condition on '{name}' is outside what can be checked for overlap",
                    "Two versions may never both apply, and showing that needs a condition built "
                  + "from tests this checker can compare",
                    version.Line, version.Column,
                    $"tell versions of '{name}' apart by this condition",
                    "A 'when' is built from equality against a literal ('node's left is 0'), a "
                  + "type test ('node is a num-lit'), either of them negated, and 'and', 'or' and "
                  + "'xor'. Ordering and arithmetic are not part of it.");
            read.Add((version, clause));
        }

        for (int i = 0; i < read.Count; i++)
            for (int j = i + 1; j < read.Count; j++)
                if (!DispatchConditions.AreDisjoint(read[i].Clause, read[j].Clause))
                    throw TypeChecker.TypeError(
                        $"two versions of '{name}' can both apply",
                        $"The one on line {read[i].Version.Line} and this one can hold at the same "
                      + "time, and the language does not decide which of two would win",
                        read[j].Version.Line, read[j].Version.Column,
                        $"declare versions of '{name}' whose conditions overlap",
                        "Make the conditions exclude each other — a version for the narrower case "
                      + "and a version for the rest — or fold the two bodies into one.");
    }

    // ── Building ──────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<IStatement> RenamedVersions(string name, Signature sig, CufetType on)
    {
        int ordinal = 0;
        foreach (var version in sig.Conditioned)
            yield return version with { Name = VersionName(name, on, ordinal++), When = null };
        if (sig.Fallback is not null)
            yield return sig.Fallback with { Name = VersionName(name, on, ordinal), When = null };
    }

    /// <summary>
    /// The statements running one signature's versions: each condition in turn, then the fallback.
    /// </summary>
    /// <remarks>
    /// ★ Generated in declaration order, and the order carries NO meaning — the conditions have
    /// already been shown pairwise disjoint, so at most one arm can hold whatever order they sit
    /// in. That is what lets an ordinary `If` chain stand for order-independent dispatch.
    /// </remarks>
    private static IReadOnlyList<IStatement> ConditionChain(
        string name, Signature sig, BindStatement shape, CufetType on, string? dispatchedArg, int at)
    {
        IReadOnlyList<IStatement> CallTo(int ordinal, BindStatement version)
        {
            var args = new List<IExpression>();
            for (int i = 0; i < shape.Parameters.Count; i++)
                args.Add(new VariableReference(
                    // ⚠ `it` at the dispatched position when a Judge is above us. The parameter
                    // still holds the whole union; the arm's `it` carries the narrowed type the
                    // version was declared for.
                    i == at && dispatchedArg is not null ? dispatchedArg : shape.Parameters[i].Name,
                    version.Line, version.Column));

            var call = new CastExpression(
                new VariableReference(VersionName(name, on, ordinal), version.Line, version.Column),
                args, version.Line, version.Column);

            return shape.ReturnType is null
                ? [new CastStatement(call.Function, call.Args, version.Line, version.Column)]
                : [new ReturnStatement(call, version.Line, version.Column)];
        }

        if (sig.Conditioned.Count == 0) return CallTo(0, sig.Fallback!);

        var arms = new List<ConditionArm>();
        int n = 0;
        foreach (var version in sig.Conditioned)
        {
            // ⚠⚠ The condition needs the same rewrite the argument does, and for a sharper
            // reason. Inside a `Judge` arm the PARAMETER still holds the whole union — only `it`
            // carries the narrowed type — so a condition written as `node's left is 0` would be
            // asking for a field of a union and refused outright. The version's body is untouched:
            // it is a separate function whose own parameter is already the narrow type.
            var condition = dispatchedArg is null || at < 0
                ? version.When!
                : Rename(version.When!, shape.Parameters[at].Name, dispatchedArg);
            arms.Add(new ConditionArm(condition, CallTo(n++, version)));
        }

        return [new IfStatement(arms, CallTo(n, sig.Fallback!))];
    }

    /// <summary>The condition with one name swapped for another, throughout.</summary>
    /// <remarks>
    /// Only the shapes a `when` may contain are walked, because only those can appear here —
    /// <see cref="DispatchConditions"/> has already refused anything else.
    /// </remarks>
    private static IExpression Rename(IExpression e, string from, string to) => e switch
    {
        VariableReference v when v.Name == from => v with { Name = to },
        BinaryExpression b  => b with { Left = Rename(b.Left, from, to), Right = Rename(b.Right, from, to) },
        UnaryExpression u   => u with { Operand = Rename(u.Operand, from, to) },
        IsTypeCheck t       => t with { Target = Rename(t.Target, from, to) },
        PossessiveAccess p  => p with { Target = Rename(p.Target, from, to) },
        RecordNamedAccess r => r with { Record = Rename(r.Record, from, to) },
        _                   => e,
    };

    private static BindStatement Dispatcher(
        string name, BindStatement shape,
        IReadOnlyList<(CufetType Type, string Name)> parameters,
        IReadOnlyList<IStatement> body) =>
        new(name, shape.ReturnType, parameters, body,
            UntoType: null, ConstructsTypeName: null, shape.Line, shape.Column);
}
