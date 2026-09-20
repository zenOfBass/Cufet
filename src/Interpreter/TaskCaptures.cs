namespace Cufet.Interpreter;

/// <summary>
/// What a task body does with the names it CAPTURES — the three walks both backends ask.
/// </summary>
/// <remarks>
/// <para>
/// ★★ These are LANGUAGE RULES, not lowering details, and they lived in the code generator until
/// 2026-09-19 — which is why `cufet check` passed a program `cufet build` refused, and the
/// interpreter ran it and printed a third answer. Moved here so the CHECKER owns the refusal and
/// all three tools agree. The compiler still calls these, so there is one definition rather than
/// two that can drift.
/// </para>
/// <para>
/// ⚠ The two tiers are deliberate and must not be merged. An OBSERVABLE write (something outside
/// the task reads the name afterwards) is refused, because the backends genuinely disagree — the
/// interpreter writes the enclosing binding, the compiled task writes its own copy. A write
/// nothing outside reads only WARNS, because there the two cannot be told apart, and refusing it
/// would be a rule wider than its reason.
/// </para>
/// <para>
/// ★ Every walk here is keyed on the NAMESPACE rather than on IExpression/IStatement, because
/// `ConditionArm` and `JudgeArm` implement neither and a walk keyed on the interfaces reads past
/// the body of every `If` and every judgement. That is the most-repeated bug in this codebase.
/// </para>
/// </remarks>
public static class TaskCaptures
{
    /// <summary>The names a task body uses but does not define — what crosses the boundary.</summary>
    public static List<string> Of(
        IReadOnlyList<IStatement> body, Func<string, bool> isInScope)
    {
        var refs = new HashSet<string>();
        var defs = new HashSet<string>();
        foreach (var s in body) CollectRefsDefs(s, refs, defs);
        return refs.Where(r => !defs.Contains(r) && isInScope(r))
                   .OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    public static bool WriteIsObservable(
        Program? program, IReadOnlyList<IStatement> taskBody, string name)
    {
        bool found = false;

        void Walk(object? node)
        {
            if (found || node is null) return;

            switch (node)
            {
                // The task's own body is not "elsewhere" — skip the whole subtree it hangs from.
                case LaunchTaskStatement lts when ReferenceEquals(lts.Body, taskBody):
                    return;

                // Reading it.
                case VariableReference v:
                    if (v.Name == name) found = true;
                    return;

                // Writing it. The target is a bare string, invisible to the reflection walk below,
                // and a sibling that only WRITES the name can still tell the two backends apart.
                case BecomesStatement b:
                    if (b.Name == name) { found = true; return; }
                    Walk(b.Value);
                    return;

                case string: return;

                case System.Runtime.CompilerServices.ITuple tup:
                    for (int i = 0; i < tup.Length && !found; i++) Walk(tup[i]);
                    return;

                case System.Collections.IEnumerable en:
                    foreach (var item in en) { Walk(item); if (found) return; }
                    return;
            }

            // Same reflection descend CollectRefsDefs uses, so a new AST node is traversed without
            // needing an arm here.
            foreach (var prop in node.GetType().GetProperties())
            {
                Walk(prop.GetValue(node));
                if (found) return;
            }
        }

        Walk(program?.Statements);
        return found;
    }

    public static void CollectRefsDefs(object? node, HashSet<string> refs, HashSet<string> defs)
    {
        // A nested BINDING FORM (lambda / nested Bind / for-each) binds its params/iterator to its OWN
        // body only. Walking it with the shared `defs` set would let those inner names mask an OUTER
        // variable of the same name for the WHOLE enclosing body — the variable would then look
        // "defined" and never be captured, emitting an undeclared `cv_<name>` (the same symptom as a
        // missed ref). So recurse with a private scope and merge back only what is still free.
        void Nested(IEnumerable<string> bound, IEnumerable<IStatement> body)
        {
            var innerDefs = new HashSet<string>(defs);
            foreach (var b in bound) innerDefs.Add(b);
            var innerRefs = new HashSet<string>();
            foreach (var s in body) CollectRefsDefs(s, innerRefs, innerDefs);
            foreach (var r in innerRefs) if (!innerDefs.Contains(r)) refs.Add(r);
        }

        switch (node)
        {
            case null: return;
            case VariableReference v: refs.Add(v.Name); return;
            // An assignment TARGET is a REFERENCE to an existing binding, but the name is a bare
            // string — invisible to the generic reflection walk below (`case string: break`). Without
            // this, a closure/task that only WRITES a captured variable never captures it and emits an
            // undeclared `cv_<name>`. (A body that also reads it was rescued by the read, which is why
            // this survived: `x becomes x + 1` works, `x becomes 5` did not.)
            case BecomesStatement b: refs.Add(b.Name); CollectRefsDefs(b.Value, refs, defs); return;
            case DefineStatement d: defs.Add(d.Name); CollectRefsDefs(d.Value, refs, defs); return;
            // A return that RUNS an axiom names it, but does not read it: the checker resolved the
            // name to the source and this backend pastes that source in. There is no value to
            // capture, so a body that only reaches for an axiom is not a closure.
            case ReturnStatement { RunsAxiom: not null }: return;
            // ★ The same for one called as a statement — but its ARGUMENTS are still read, so
            // unlike the return above this recurses into them rather than stopping.
            case CastStatement { RunsAxiom: not null } effectCall:
                foreach (var arg in effectCall.Args) CollectRefsDefs(arg, refs, defs);
                return;
            // ⚠ And NOT the same for one reached through a value. That call READS its callee — the
            // name holds the thing being called — so it falls through to the ordinary walk below
            // and is captured like any other free variable. Stopping here instead would build a
            // closure with no slot for the axiom it calls.
            case ForEachStatement fe:
                CollectRefsDefs(fe.Series, refs, defs);   // the series expression is in the OUTER scope
                Nested(fe.IteratorName != null ? [fe.IteratorName] : [], fe.Body);
                return;
            case ForEachFromInputStatement fi:
                Nested([fi.IteratorName], fi.Body);
                return;
            case LambdaLiteral lam:
                Nested(lam.Parameters.Select(p => p.Name), lam.Body);
                return;
            case BindStatement nb:
                defs.Add(nb.Name);                        // the local function's NAME binds in the enclosing scope
                Nested(nb.Parameters.Select(p => p.Name), nb.Body);
                return;
            // ⭐⭐ An object definition sits INSIDE a body without being part of it. Its methods,
            // getters and setters are emitted as C functions of their own, off the program's type
            // table, and the receiver they read fields through is `one` — bound by the member, not
            // by anything in the body the definition was written in.
            //
            // ⚠ Walking them without binding `one` reported it as a capture of the ENCLOSING
            // function, so `Define object …` inside a function inside a `Pull` block was refused
            // with "captures 'one' from the pull scope" — a program the interpreter runs and the
            // compiler would not, on a name no writer ever declared. A DIVERGENCE, and the oracle
            // could not have found it: no test had put those three things together.
            //
            // ★ The bodies are still walked. A method genuinely reaching for a local of the
            // enclosing body is still the deferred closure gap, and still has to be caught — only
            // the receiver and each member's own parameters are bound first.
            case ObjectDefinition od:
                foreach (var method in od.Methods)
                    Nested(["one", .. method.Parameters.Select(p => p.Name)], method.Body);
                foreach (var getter in od.Getters) Nested(["one"], getter.Body);
                foreach (var setter in od.Setters) Nested(["one", setter.ParamName], setter.Body);
                return;
        }
        // Generic: visit every AST child, including tuple-wrapped ones (record/object/map literal
        // fields) and lists thereof.
        //
        // ★ Keyed on the NAMESPACE, not on IExpression/IStatement — the same correction AstSearch
        // carries, and for the same reason. `ConditionArm` and `JudgeArm` are plain records that
        // HOLD statements without implementing either interface, so matching the interfaces walked
        // straight past the condition AND the body of every `If` arm and every judgement.
        //
        // The symptom was a task or closure that referenced an enclosing variable ONLY inside an
        // `If` arm: the name never reached `refs`, so it was never captured, and the emitted C said
        // `cv_<name> undeclared`. It hid for so long because a body that also touches the variable
        // anywhere else is rescued by that other mention — the work-queue collector broke only
        // because `If count is n, Stop.` was its sole use of `n`. `Otherwise` bodies were fine
        // throughout, since ElseBody is an ordinary property rather than an arm.
        void Visit(object? val)
        {
            switch (val)
            {
                case null or string or CufetType: break;
                case System.Runtime.CompilerServices.ITuple tup:
                    for (int i = 0; i < tup.Length; i++) Visit(tup[i]);
                    break;
                case System.Collections.IEnumerable en:
                    foreach (var item in en) Visit(item);
                    break;
                default:
                    if (val.GetType().Namespace == typeof(IStatement).Namespace)
                        CollectRefsDefs(val, refs, defs);
                    break;
            }
        }
        foreach (var prop in node!.GetType().GetProperties())
            Visit(prop.GetValue(node));
    }

    public static bool MayMutate(object? node, string name)
    {
        bool Touches(IExpression? e)
        {
            if (e == null) return false;
            var refs = new HashSet<string>();
            CollectRefsDefs(e, refs, new HashSet<string>());
            return refs.Contains(name);
        }

        switch (node)
        {
            case null: return false;
            // Rebinding the capture: the interpreter would rebind the ENCLOSING binding.
            case BecomesStatement b when b.Name == name: return true;
            case SeriesInsertStatement s          when Touches(s.Series):  return true;
            case SeriesRemoveAtStatement s     when Touches(s.Series):  return true;
            case SeriesRemoveValueStatement s  when Touches(s.Series):  return true;
            case SeriesSetStatement s          when Touches(s.Series):  return true;
            case MatrixSetStatement s          when Touches(s.Matrix):  return true;
            case RecordNamedSetStatement s     when Touches(s.Record):  return true;
            case PossessiveSetStatement s      when Touches(s.Target):  return true;
            case MapSetStatement s             when Touches(s.Map):     return true;
            // Handing the value to a function — the callee may mutate through its parameter.
            case CastExpression ce when ce.Args.Any(a => Touches(a)):   return true;
            case CastStatement cs when cs.Args.Any(a => Touches(a)):    return true;
        }

        // Otherwise descend into every child statement/expression (same reflection walk as
        // CollectRefsDefs, so a new AST node is traversed without needing an arm here).
        bool found = false;
        void Visit(object? val)
        {
            if (found) return;
            switch (val)
            {
                case null or string or CufetType: break;
                case System.Runtime.CompilerServices.ITuple tup:
                    for (int i = 0; i < tup.Length && !found; i++) Visit(tup[i]);
                    break;
                case System.Collections.IEnumerable en:
                    foreach (var item in en) { Visit(item); if (found) break; }
                    break;
                default:
                    // ★ Keyed on the NAMESPACE, not on IExpression/IStatement. `ConditionArm` and
                    // `JudgeArm` implement neither, so matching the interfaces walked past the body
                    // of every `If` arm — and THIS walk decides whether a task's capture-write is
                    // refused. A write hidden one `If` deep was not seen, the refusal never fired,
                    // and the program compiled to something the interpreter disagrees with.
                    // Measured: `If 1 is 1: tally becomes tally + 5. Done.` inside a task printed
                    // 5 interpreted and 0 compiled, with `check --native` reporting no problems.
                    //
                    // This walk must OVER-approximate: missing a write ships a divergence, while an
                    // extra refusal only costs a clean error. Descending into everything in the AST
                    // namespace is the safe direction.
                    if (val.GetType().Namespace == typeof(IStatement).Namespace
                        && MayMutate(val, name)) found = true;
                    break;
            }
        }
        if (node is System.Collections.IEnumerable seq and not string) { Visit(seq); return found; }
        foreach (var prop in node.GetType().GetProperties())
        {
            Visit(prop.GetValue(node));
            if (found) return true;
        }
        return found;
    }
}
