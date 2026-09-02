using System.Text;
using Cufet.Interpreter;
using Cufet.Lexer;

namespace Cufet.Compiler;

/// <summary>Every expression, lowered to C</summary>
/// <remarks>
/// <para>Every expression, lowered to C — operators, conversions, text and file and range reads, literals, and the C type each Cufet type becomes.</para>
/// <para>
/// ★ One class across several files, carved along the boundaries the generator already drew
/// for itself — these were its own section banners, not lines chosen by whoever split it. The
/// state they all share (the struct registries, the arena depth, the pre-emit buffer) stays in
/// <c>CodeGenerator.cs</c>, because it is what the halves talk to each other through.
/// </para>
/// </remarks>
public sealed partial class CodeGenerator
{
    // ── TCAP — can anything OUTSIDE the task tell that the write happened? ─────────────────────
    //
    // A write to a capture is dead compiled (the task holds its own copy) and live interpreted (the
    // task shares the enclosing binding). Those two answers differ only if something afterwards
    // LOOKS at the binding. When nothing does, both backends print exactly the same thing and the
    // write is simply discarded — safe to compile, and worth saying out loud rather than refusing.
    //
    // ★ The test is deliberately blunt: ANY mention of the name anywhere in the program outside this
    // task's own body counts as an observation — a read, a write, a mention inside another rabbit,
    // a mention on a branch that never runs. Over-approximating is the whole safety argument. Being
    // wrong can only keep a refusal that was not strictly necessary; it can never let a divergence
    // through, because a name this says nothing about is a name nothing else touches.
    // Emits a call's arguments against the callee's declared parameter types, so a value narrower
    // than its slot is widened on the way in — the same coercion a field set or a series element
    // already gets. Falls back to a plain emit when the signature is unknown (a call through a
    // variable, a method), which is the pre-existing behaviour and no worse than it was.
    private IEnumerable<string> EmitArgsAsParams(string funcName, IReadOnlyList<IExpression> args)
    {
        _funcTypes.TryGetValue(funcName, out var signature);
        var paramTypes = signature?.ParameterTypes;

        for (int i = 0; i < args.Count; i++)
            yield return paramTypes is not null && i < paramTypes.Count
                ? EmitAsType(args[i], paramTypes[i])
                : EmitExpr(args[i]);
    }

    // Is `name` bound by a Bind ANYWHERE in the program? Used only to tell an unresolved call that
    // is a typo from one that is a forward reference, so each gets the message that fits it. The
    // question is deliberately coarse — it decides which sentence to print, never what to emit.
    private bool IsBoundSomewhere(string name)
    {
        bool found = false;

        void Walk(object? node)
        {
            if (found || node is null) return;

            switch (node)
            {
                case BindStatement b when b.Name == name: found = true; return;
                case string: return;

                case System.Runtime.CompilerServices.ITuple tup:
                    for (int i = 0; i < tup.Length && !found; i++) Walk(tup[i]);
                    return;

                case System.Collections.IEnumerable en:
                    foreach (var item in en) { Walk(item); if (found) return; }
                    return;
            }

            // The same reflection descend the walks above use, so a new AST node needs no arm here.
            foreach (var prop in node.GetType().GetProperties())
            {
                Walk(prop.GetValue(node));
                if (found) return;
            }
        }

        Walk(_program?.Statements);
        return found;
    }

    private bool CaptureWriteIsObservable(IReadOnlyList<IStatement> taskBody, string name)
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

        Walk(_program?.Statements);
        return found;
    }

    // Infers a named task's result type from its returns — mirrors the checker's inference so the
    // heap-bridge C type matches. Scans nested control flow (but not nested tasks). A `return void`
    // makes the result voidable; a `return a failure …` makes it fallible; both compose. Task-body
    // LOCALS are tracked as the walk proceeds (a `return b` where `b` is a locally-defined series/
    // object needs `b`'s type to resolve — captured vars are already in _varTypes; restored after).
    private CufetType? InferTaskResultType(IReadOnlyList<IStatement> body)
    {
        bool hasFailure = false, hasVoid = false;
        CufetType? valueType = null;
        var saved = new Dictionary<string, CufetType>(_varTypes);
        void Walk(IReadOnlyList<IStatement> stmts)
        {
            foreach (var s in stmts)
                switch (s)
                {
                    case DefineStatement d:
                        var dt = d.DeclaredType ?? TypeOf(d.Value); if (dt != null) _varTypes[d.Name] = dt; break;
                    case ReturnStatement { Value: null }: break;               // bare void early-exit
                    case ReturnStatement { Value: FailureLiteral }: hasFailure = true; break;
                    case ReturnStatement { Value: VoidLiteral }: hasVoid = true; break;
                    case ReturnStatement r: valueType ??= TypeOf(r.Value!); break;
                    case IfStatement iff:
                        foreach (var a in iff.Arms) Walk(a.Body);
                        if (iff.ElseBody != null) Walk(iff.ElseBody);
                        break;
                    case WhileStatement w: Walk(w.Body); break;
                    case RepeatUntilStatement ru: Walk(ru.Body); break;
                    case ForEachStatement fe: Walk(fe.Body); break;
                    case PullRabbitStatement pr: Walk(pr.Body); break;
                    // Nested LaunchTaskStatement bodies own their own returns — do not descend.
                }
        }
        Walk(body);
        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
        if (valueType == null && !hasFailure && !hasVoid) return null;   // fire-and-forget (void)
        CufetType t = valueType ?? TNumber;
        if (hasVoid) t = new VoidableType(t);
        if (hasFailure) t = new FailureType(t);
        return t;
    }

    // `the awaited result of <name>` — join the named task once (guarded so a re-await is a cheap
    // read of the cached result), copy the heap-bridged result into the awaiter + free the bridge,
    // and mark the slot joined so the rabbit's Done. teardown won't re-join it. The stored value is
    // then yielded; if the task is fallible it flows through the standard fallible machinery so
    // Try / but-on-failure / propagate compose exactly as for a fallible call.
    private string EmitAwaitedResult(AwaitedResultExpression are)
    {
        string raw = EmitAwaitedRaw(are, out var info);
        if (info.ResultType is FailureType ft)
            return EmitFallibleCheckGoto(raw, RegisterFailableStruct(ft));
        return raw;
    }

    // Emits the guarded join + result-cache preemit and returns the stored-result C var (a cfl/cvd/
    // scalar). Shared by EmitAwaitedResult (bare / in-Try) and EmitFallibleRaw (but-on-failure /
    // propagate), mirroring how file-read fallibility is factored.
    private string EmitAwaitedRaw(AwaitedResultExpression are, out (string Ctx, string ResultCType, CufetType? ResultType) info)
    {
        _usesConcurrency = true;
        if (are.Task is not VariableReference vr || !_taskInfos.TryGetValue(vr.Name, out info))
            throw new CompilerException("'the awaited result of' requires a named task declared with 'Have rabbit start a task as <name>:'.");
        var resultType = info.ResultType ?? TNumber;
        string box = MangleName(vr.Name);
        string rc  = EmitCType(resultType);
        int aid = _freshId++;

        // Wait on the task's result box, then DEEP-COPY the envelope into THIS awaiter's arena.
        // The box keeps owning the envelope — the rabbit's Done. frees it — so several awaiters, in
        // the rabbit body or in other tasks, can each take their own copy. Copying rather than
        // sharing is not a choice: arenas are thread-local, so a value is only usable here once it
        // is in this thread's arena.
        //
        // `box` resolves to the spawn-site variable in the rabbit body and to the captured alias
        // inside a task — the same name in both, which is why this emits one expression.
        //
        // INT.1 — an interrupted task is abandoned at its landing pad and publishes nothing, and an
        // interrupted awaiter stops waiting; both surface as NULL. There is no sensible value to
        // await then, and the interrupt is meant for this thread too, so check in.
        _preEmits.Add($"{rc} cf_ares{aid}; {{ void* cf_ar = cufet_rbox_await({box}); " +
                    $"if (cf_ar) cf_ares{aid} = {ChanArenaCopy(resultType)}(cf_ar); " +
                    $"else {{ cufet_checkpoint(); memset(&cf_ares{aid}, 0, sizeof cf_ares{aid}); }} }}");
        return $"cf_ares{aid}";
    }

    // Collects referenced variable names (refs) and locally-defined/iterated/parameter names (defs) in
    // a statement OR expression subtree — used for task-capture and closure free-variable analysis.
    // A generic reflection walk over the AST (records + tuple-wrapped children) so NO node form is
    // missed (closures capture arbitrary values; an undiscovered ref would be an undeclared C var).
    // Binding forms (Define/ForEach/lambda/nested-Bind params) contribute defs so their bodies' refs
    // to them aren't counted as free. Free vars = refs − defs (computed by the caller).
    private void CollectRefsDefs(object? node, HashSet<string> refs, HashSet<string> defs)
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

    // <fallible> or pass the failure off — on failure, return it from the enclosing (fallible)
    // function immediately; on success, the plain value.
    private string EmitFailurePropagate(FailurePropagate fp)
    {
        var (cflName, rawExpr) = EmitFallibleRaw(fp.Fallible, TypeOf(fp));
        string tmp = $"cf_fp{_freshId++}";
        _preEmits.Add($"{cflName} {tmp} = {rawExpr};");
        string enclosing = _currentReturnType is FailureType ft
            ? RegisterFailableStruct(ft)
            : throw new CompilerException("'or pass the failure off' requires the enclosing function to return 'T or failure'.");
        // ESC.3: only the message/category cross this return, so move them out to the frame's base
        // arena and then genuinely reclaim every rabbit region this propagation jumps out of.
        _preEmits.Add($"if ({tmp}.is_failure) {{ {ArenaStrCopyTo(0, $"{tmp}.message", $"{tmp}.category")}{UnwindTo(FrameExit)}return (({enclosing}){{ .is_failure = 1, .message = {tmp}.message, .category = {tmp}.category }}); }}");
        return $"{tmp}.val";
    }

    // converted to text — number formats to a fresh arena string; fact → static "true"/"false";
    // text is a no-op. (The type checker restricts the operand to number/fact/text.)
    private string EmitTextConvert(TextConvert tc) => TypeOf(tc.Value) switch
    {
        NumberType => $"cufet_text_from_dec({EmitExpr(tc.Value)})",
        FactType   => $"({EmitExpr(tc.Value)} ? \"true\" : \"false\")",
        TextType   => EmitExpr(tc.Value),
        BitsType   => EmitBitsToText(tc),
        // ★★ The explicit COPY. UTF-32 in the buffer becomes UTF-8 here, once, at the end — which
        // is what turns the quadratic build into a linear one. The buffer lives on, independent.
        ChaseType  => $"cufet_chase_text({EmitExpr(tc.Value)})",
        var t => throw new CompilerException($"'converted to text' of a '{FormatTypeName(t)}' is not yet supported by the compiler.")
    };

    // A bits value converts to the text it displays as — "0xFF", prefix and all. Formatted into
    // an arena buffer, since the result is an ordinary Cufet text from here on.
    private string EmitBitsToText(TextConvert tc)
    {
        int id = _freshId++;
        _preEmits.Add($"char* cf_bt{id} = (char*)cufet_arena_alloc(80); " +
                    $"cufet_format_bits(cf_bt{id}, 80, {EmitExpr(tc.Value)});");
        return $"cf_bt{id}";
    }

    // converted to number. From bits this is TOTAL — 64 bits always fits a 96-bit mantissa — so
    // it yields a plain number. From text it may simply not be one, hence the voidable.
    private string EmitNumberConvert(NumberConvert nc)
    {
        if (TypeOf(nc.Value) is BitsType)
            return $"cufet_bits_to_number({EmitExpr(nc.Value)})";

        string cvd = RegisterVoidableStruct(new VoidableType(TNumber));
        string s = EmitExpr(nc.Value);
        int id = _freshId++;
        _preEmits.Add($"CufetDec cf_pn{id}; int cf_pnok{id} = cufet_parse_number({s}, &cf_pn{id});");
        return $"(cf_pnok{id} ? ({cvd}){{ .has = 1, .val = cf_pn{id} }} : ({cvd}){{ .has = 0 }})";
    }

    // the position of <sub> in <text> → voidable number (1-based; void when not found).
    private string EmitTextFind(TextFind tf)
    {
        string cvd = RegisterVoidableStruct(new VoidableType(TNumber));
        int id = _freshId++;
        _preEmits.Add($"int cf_fd{id} = cufet_str_find({EmitExpr(tf.Text)}, {EmitExpr(tf.Substring)});");
        return $"(cf_fd{id} ? ({cvd}){{ .has = 1, .val = cufet_dec_from_ll(cf_fd{id}) }} : ({cvd}){{ .has = 0 }})";
    }

    // `<text> split by <delim>` → a series of text (arena series of arena substrings). Matches
    // the interpreter's C# string.Split(string): N delimiter hits → N+1 parts, empties kept,
    // trailing/leading delimiter → empty parts, delimiter-not-found → single whole-string element.
    // `range A to B [counting by S]` in VALUE position — materialize it as a series of number.
    // Only the for-each form was ever emitted, so `Define halves as range 1 to 2 counting by 0.5.`
    // (a REFERENCE example) did not compile. The loop below mirrors EmitForEachRange exactly —
    // direction taken from start-vs-end, step defaulting to 1 — so the two forms cannot drift
    // apart: iterating a range and materializing it must produce the same numbers.
    private string EmitRangeSeries(RangeExpression range)
    {
        string name      = RegisterSeriesStruct(new SeriesType(TNumber));
        string startExpr = EmitExpr(range.Start);
        string endExpr   = EmitExpr(range.End);
        string stepExpr  = range.Step != null ? EmitExpr(range.Step) : "cufet_dec_from_ll(1)";
        int id = _freshId++;
        string tmp = $"cs_{id}", s = $"cf_rs{id}", e = $"cf_re{id}",
                st = $"cf_rt{id}", d = $"cf_rd{id}", it = $"cf_ri{id}";
        _preEmits.Add($"{name}* {tmp} = {name}_new();");
        _preEmits.Add(
            $"{{ CufetDec {s} = {startExpr}; CufetDec {e} = {endExpr}; CufetDec {st} = {stepExpr}; " +
            $"{RangeStepGuard(st, range.Line)}" +
            $"int {d} = cufet_cmp({s}, {e}) <= 0 ? 1 : -1; " +
            $"for (CufetDec {it} = {s}; {d} > 0 ? cufet_cmp({it}, {e}) <= 0 : cufet_cmp({it}, {e}) >= 0; " +
            $"{it} = {d} > 0 ? cufet_add({it}, {st}) : cufet_sub({it}, {st})) {name}_append({tmp}, {it}); }}");
        return tmp;
    }

    // A non-positive `counting by` step is a runtime error in the interpreter; compiled it would
    // spin forever, so raise the same catchable exception. A LITERAL zero never reaches here — the
    // shared type checker rejects it statically — but a computed step can be anything, and the
    // interpreter distinguishes zero from negative with two different messages, so match both.
    private string RangeStepGuard(string stepVar, int line) =>
        $"if (cufet_cmp({stepVar}, cufet_dec_from_ll(0)) == 0) cufet_raise(cufet_msgf(\"'counting by 0' never makes progress (line {line}).\")); " +
        $"if (cufet_cmp({stepVar}, cufet_dec_from_ll(0)) < 0) cufet_raise(cufet_msgf(\"the step in 'counting by' must be positive (line {line}).\")); ";

    // Casing is the one text operation that needs a table, so it is also the one that has to
    // announce itself — CaseRuntime is emitted only for programs that reach here.
    private string EmitTextCase(TextCase tcase)
    {
        _usesCase = true;
        var text = EmitExpr(tcase.Text);
        return tcase.Uppercase ? $"cufet_str_upper({text})" : $"cufet_str_lower({text})";
    }

    private string EmitTextSplit(TextSplit ts)
    {
        string name = RegisterSeriesStruct(new SeriesType(TText));
        string textExpr  = EmitExpr(ts.Text);
        string delimExpr = EmitExpr(ts.Delimiter);
        int id = _freshId++;
        string tmp = $"cs_{id}", parts = $"cf_sp{id}", n = $"cf_spn{id}", j = $"cf_spj{id}";
        _preEmits.Add($"{name}* {tmp} = {name}_new();");
        _preEmits.Add($"{{ const char** {parts}; int {n} = cufet_str_split({textExpr}, {delimExpr}, &{parts}, {ts.Line}); for (int {j} = 0; {j} < {n}; {j}++) {name}_append({tmp}, {parts}[{j}]); }}");
        return tmp;
    }

    // A file read is a fallible expression: build the raw `cfl` (read into arena, or a failure
    // from errno), then route through the shared fallible check-goto (auto-unwrap in a Try, or
    // fed to but-on-failure / propagate). Mirrors EmitCastExpr for a fallible call.
    private string EmitFileRead(FileReadExpression fr) =>
        EmitFallibleCheckGoto(EmitFileReadRaw(fr), RegisterFailableStruct((FailureType)FallibleReturnType(fr)!));

    // Preemits the read into a `cfl` temp (whole-file text, or a series-of-text of lines) and
    // returns the temp name (the raw fallible value, before the failure check).
    private string EmitFileReadRaw(FileReadExpression fr)
    {
        string cfl = RegisterFailableStruct(new FailureType(FileReadSuccessType(fr)));
        string pathExpr = EmitExpr(fr.Path);
        int id = _freshId++;
        string raw = $"cf_fr{id}", e = $"cf_fre{id}";
        if (fr.Form == FileReadForm.All)
        {
            _preEmits.Add(
                $"{cfl} {raw}; {{ const char* v; CufetFailure {e}; " +
                $"if (cufet_file_read_all({pathExpr}, &v, &{e})) {{ {raw}.is_failure = 0; {raw}.val = v; }} " +
                $"else {{ {raw}.is_failure = 1; {raw}.message = {e}.message; {raw}.category = {e}.category; }} }}");
        }
        else // AllLines → series of text (build the cser inline, like split)
        {
            string ser = RegisterSeriesStruct(new SeriesType(TText));
            string parts = $"cf_lp{id}", n = $"cf_ln{id}", j = $"cf_lj{id}", sv = $"cf_ls{id}";
            _preEmits.Add(
                $"{cfl} {raw}; {{ const char** {parts}; int {n}; CufetFailure {e}; " +
                $"if (cufet_file_read_lines({pathExpr}, &{parts}, &{n}, &{e})) {{ " +
                $"{ser}* {sv} = {ser}_new(); for (int {j} = 0; {j} < {n}; {j}++) {ser}_append({sv}, {parts}[{j}]); " +
                $"{raw}.is_failure = 0; {raw}.val = {sv}; }} " +
                $"else {{ {raw}.is_failure = 1; {raw}.message = {e}.message; {raw}.category = {e}.category; }} }}");
        }
        return raw;
    }

    // `write/append <text> to the file "<path>"` — a fallible statement. On failure it routes to
    // the enclosing Try handler (goto), exactly like a bare fallible call. With no enclosing Try,
    // an I/O failure has nowhere to go: abort with the message (the interpreter's uncaught failure
    // is likewise fatal). The common path — a successful write — emits and continues.
    // `The current directory becomes <path>.` — a fallible statement, emitted exactly like a file
    // write: call, then either jump to the enclosing Try's handler or abort with the message.
    //
    // ★ Refused inside a task body. A process has ONE working directory, so changing it from a
    // task races every other thread's relative-path resolution, with no happens-before edge to
    // order them — two well-defined answers, which is the never-ship class rather than the
    // platform-owned exception. The cooperative interpreter would run it deterministically and
    // hide the problem entirely. (Reading stays allowed everywhere; with writes refused inside
    // tasks, the remaining window is a rabbit body writing while its own tasks read.)
    private void EmitCurrentDirectorySet(StringBuilder sb, CurrentDirectorySetStatement cd, string indent)
    {
        if (_inTaskBody)
            throw new CompilerException(
                "a task cannot change the current directory. A process has only one, so changing it " +
                "from a task would race every other task reading a relative path. Change it in the " +
                "rabbit body before starting the task, or pass the directory in and build full paths.");

        string pathExpr = EmitExpr(cd.Path);
        FlushPreEmits(sb, indent);
        int id = _freshId++;
        string ok = $"cf_cd{id}", err = $"cf_cde{id}";
        sb.AppendLine($"{indent}{{ CufetFailure {err}; int {ok} = cufet_chdir({pathExpr}, &{err});");
        if (_currentTryHandler is { } h)
            sb.AppendLine($"{indent}  if (!{ok}) {{ {FailureGotoBody(h, $"{err}.message", $"{err}.category")} }} }}");
        else
            sb.AppendLine($"{indent}  if (!{ok}) {{ fprintf(stderr, \"%s\\n\", {err}.message); exit(1); }} }}");
    }

    private void EmitFileWrite(StringBuilder sb, FileWriteStatement fw, string indent)
    {
        string valExpr = EmitExpr(fw.Value);
        FlushPreEmits(sb, indent);
        string pathExpr = EmitExpr(fw.Path);
        FlushPreEmits(sb, indent);
        int id = _freshId++;
        string ok = $"cf_w{id}", err = $"cf_we{id}";
        sb.AppendLine($"{indent}{{ CufetFailure {err}; int {ok} = cufet_file_write({pathExpr}, {valExpr}, {(fw.Append ? 1 : 0)}, &{err});");
        if (_currentTryHandler is { } h)
            sb.AppendLine($"{indent}  if (!{ok}) {{ {FailureGotoBody(h, $"{err}.message", $"{err}.category")} }} }}");
        else
            sb.AppendLine($"{indent}  if (!{ok}) {{ fprintf(stderr, \"%s\\n\", {err}.message); exit(1); }} }}");
    }

    // Stream reads (infallible). `read a line from s` → voidable text (void at EOF); `read all
    // from s` → text; `read all lines from s` → series of text. Results are arena-allocated.
    private string EmitReadExpr(ReadExpression re)
    {
        string src = EmitExpr(re.Source);
        switch (re.Form)
        {
            case ReadForm.All:
                return $"cufet_stream_read_all({src})";
            case ReadForm.Line:
            {
                string cvd = RegisterVoidableStruct(new VoidableType(TText));
                int id = _freshId++;
                _preEmits.Add($"const char* cf_rl{id} = cufet_stream_read_line({src});");
                return $"(cf_rl{id} ? ({cvd}){{ .has = 1, .val = cf_rl{id} }} : ({cvd}){{ .has = 0 }})";
            }
            case ReadForm.AllLines:
            {
                string ser = RegisterSeriesStruct(new SeriesType(TText));
                int id = _freshId++;
                string sv = $"cs_{id}", ln = $"cf_rln{id}";
                _preEmits.Add($"{ser}* {sv} = {ser}_new(); {{ const char* {ln}; while (({ln} = cufet_stream_read_line({src})) != NULL) {ser}_append({sv}, {ln}); }}");
                return sv;
            }
            default: throw new CompilerException($"unknown read form {re.Form}");
        }
    }

    // `With the file "<path>" open for reading/writing as s: … Done.` — opens the file, registers
    // its close in the cleanup stack, runs the body, and closes on EVERY exit path (normal end,
    // return, failure-goto, break/continue). An open failure becomes a Cufet failure (goto the
    // enclosing Try, or abort). Guaranteed-close is what makes a buffered write always flush.
    private void EmitWithOpen(StringBuilder sb, WithOpenStatement wos, string indent)
    {
        var inner = indent + "    ";
        int id = _freshId++;
        string pathExpr = EmitExpr(wos.Path);
        FlushPreEmits(sb, indent);
        string sVar = MangleName(wos.BindingName);
        string pathTmp = $"cf_op{id}", err = $"cf_oe{id}";
        string mode = wos.Mode == OpenMode.Reading ? "rb" : "wb";

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{inner}const char* {pathTmp} = {pathExpr};");
        sb.AppendLine($"{inner}FILE* {sVar} = fopen({pathTmp}, \"{mode}\");");
        if (_currentTryHandler is { } h)
            sb.AppendLine($"{inner}if (!{sVar}) {{ CufetFailure {err} = cufet_file_failure({pathTmp}, errno); {FailureGotoBody(h, $"{err}.message", $"{err}.category")} }}");
        else
            sb.AppendLine($"{inner}if (!{sVar}) {{ CufetFailure {err} = cufet_file_failure({pathTmp}, errno); fprintf(stderr, \"%s\\n\", {err}.message); exit(1); }}");

        var savedType = _varTypes.TryGetValue(wos.BindingName, out var pt) ? pt : null;
        _varTypes[wos.BindingName] = wos.Mode == OpenMode.Reading
            ? new ReadableStreamType(TText) : new WritableStreamType(TText);
        // Runtime-register the FILE* (E-prime): a longjmp (exception/interrupt) jumps past the
        // emit-time closes below, so the runtime registry flushes+closes what the jump skipped.
        // All structured closes go through cufet_close_file (fclose + unregister → no double-close).
        sb.AppendLine($"{inner}cufet_reg_file({sVar});");
        _openFiles.Add($"cufet_close_file({sVar});");
        EmitScopedBlock(sb, wos.Body, inner);
        _openFiles.RemoveAt(_openFiles.Count - 1);   // pop; emit the normal-exit close
        sb.AppendLine($"{inner}cufet_close_file({sVar});");
        sb.AppendLine($"{indent}}}");

        if (savedType != null) _varTypes[wos.BindingName] = savedType; else _varTypes.Remove(wos.BindingName);
    }

    // Emits a loop body, remembering the open-file depth at loop entry so a break/continue inside
    // closes files opened within the loop before jumping out.
    private void EmitLoopBody(StringBuilder sb, IReadOnlyList<IStatement> body, string indent)
    {
        // ESC.3 — Stop/Skip pop rabbits opened inside the loop, alongside everything else it opened.
        _loopExits.Add(HereCleanup());
        if (!UsesUnmakers) { EmitBlock(sb, body, indent); }
        else
        {
            // A loop body is a block scope that re-enters each iteration: snapshot at the top, fire
            // (LIFO) at the bottom — so per-iteration objects unmake each iteration (matching d4/d18).
            // Stop/Skip run to this same snapshot (d9).
            _scopeDepth++;
            string snap = $"cf_um{_freshId++}";
            sb.AppendLine($"{indent}int {snap} = cufet_num;");
            // The snapshot only exists once the block has opened, so the mark is completed in
            // place rather than pushed twice — which is what kept the old four lists from lining up.
            _loopExits[^1] = _loopExits[^1] with { UnmakerSnap = snap };
            EmitBlock(sb, body, indent);
            if (!BlockAlwaysExits(body)) sb.AppendLine($"{indent}cufet_run_unmakers_to({snap});");
            _scopeDepth--;
        }
        _loopExits.RemoveAt(_loopExits.Count - 1);
    }



    private string MapName(IExpression mapExpr) => RegisterMapStruct((MapType)TypeOf(mapExpr));

    // A map literal builds an arena map into a temp and populates it (like a series literal);
    // the enclosing statement flushes the pre-emits before using the temp.
    private string EmitMapLiteral(MapLiteral ml)
    {
        var mt = (MapType)MapLiteralType(ml);
        string name = RegisterMapStruct(mt);
        string tmp = $"cs_{_freshId++}";
        _preEmits.Add($"{name}* {tmp} = {name}_new();");
        foreach (var (k, v) in ml.Pairs)
        {
            string keyExpr = EmitExpr(k);
            string valExpr = EmitAsType(v, mt.ValueType);
            _preEmits.Add($"{name}_put({tmp}, {keyExpr}, {valExpr});");
        }
        return tmp;
    }

    // An object literal → a C compound literal (value struct). With embedding, the flat
    // field list is routed to the right level (own vs embedded), recursively — mirroring
    // the interpreter's BuildObjectValue.
    // ResolvedTypeName is set when the literal filled a template's blanks — the definition that
    // exists is `stack of number`, and `stack` alone names nothing.
    private string EmitObjectLiteral(ObjectLiteral ol) =>
        BuildObjectValue(ol.ResolvedTypeName ?? ol.TypeName, ol.PositionalValues, ol.NamedValues);

    private string BuildObjectValue(string objName, IReadOnlyList<IExpression> positionals,
                                    IReadOnlyList<(string Name, IExpression Value)> named)
    {
        var def = _objectDefs[objName];
        var parts = new List<string>();
        int ownPos = def.PositionalTypes.Count;
        // EmitAsType, not EmitExpr: a field slot is an assignment target, so a plain T (or a
        // bare `void`) widens into a voidable/union field — the checker's one implicit coercion.
        for (int i = 0; i < ownPos; i++) parts.Add($".p{i} = {EmitAsType(positionals[i], def.PositionalTypes[i])}");

        var ownFieldNames = def.NamedFields.Select(f => f.FieldName).ToHashSet();
        var remaining = new List<(string, IExpression)>();
        foreach (var (name, val) in named)
        {
            if (ownFieldNames.Contains(name))
                parts.Add($".{MangleName(name)} = {EmitAsType(val, def.NamedFields.First(f => f.FieldName == name).FieldType)}");
            else remaining.Add((name, val));
        }

        if (def.EmbeddedTypeName != null)
        {
            var restPos = positionals.Skip(ownPos).ToList();
            parts.Add($".{MangleName(def.EmbeddedTypeName)} = {BuildObjectValue(def.EmbeddedTypeName, restPos, remaining)}");
        }
        return $"(({ObjStructName(objName)}){{ {string.Join(", ", parts)} }})";
    }

    // A record literal becomes a C compound literal (value struct) with designated
    // initializers, so field order in the source is irrelevant. Any series-valued field
    // pre-emits its construction; the enclosing statement flushes those first.
    private string EmitRecordLiteral(RecordLiteral rl)
    {
        string structName = RegisterRecordStruct((RecordType)TypeOf(rl));
        var parts = new List<string>();
        for (int i = 0; i < rl.PositionalFields.Count; i++)
            parts.Add($".p{i} = {EmitExpr(rl.PositionalFields[i])}");
        foreach (var (name, valExpr) in rl.NamedFields)
            parts.Add($".{MangleName(name)} = {EmitExpr(valExpr)}");
        return $"(({structName}){{ {string.Join(", ", parts)} }})";
    }

    // Emits a series literal as a named temporary, registering construction
    // statements in _preEmits. The caller must FlushPreEmits before using
    // the returned variable name in a statement.
    private string EmitSeriesLiteral(SeriesLiteral sl)
    {
        var elemType = SeriesElementType(sl);
        var st   = new SeriesType(elemType);
        string name = RegisterSeriesStruct(st);
        string tmp = $"cs_{_freshId++}";
        _preEmits.Add($"{name}* {tmp} = {name}_new();");
        foreach (var elem in sl.Elements)
        {
            // Coerce to the element type so a catalogue's elements WIDEN into the union (tag + payload),
            // and a voidable-element series widens likewise.
            string elemExpr = EmitAsType(elem, elemType);
            _preEmits.Add($"{name}_append({tmp}, {elemExpr});");
        }
        return tmp;
    }

    private string EmitSeriesAccess(SeriesAccess sa)
    {
        // Positional access on a record/object (the first/second/... of x) → a struct field.
        var tt = TypeOf(sa.Target);
        if (tt is RecordType)
            return $"({EmitExpr(sa.Target)}).p{LiteralIndex(sa.Index) - 1}";
        if (tt is ObjectType ot)
            return $"({EmitExpr(sa.Target)}).p{ObjectPositionalIndex(ot.Name, sa.Index)}";

        string targetExpr = EmitExpr(sa.Target);
        string nm = SeriesDisplayName(sa.Target);
        // ★ A chase reads through the SAME bounds checks a series does, so an out-of-range message
        // is word for word the one a series gives. Only the element differs: `->data` here is code
        // points, and what comes back has to be text.
        if (tt is ChaseType)
        {
            _usesChase = true;
            return sa.Index == null
                ? $"cufet_chase_at({targetExpr}, cufet_last_check(({targetExpr})->len, \"{nm}\", {sa.Line}))"
                : $"cufet_chase_at({targetExpr}, cufet_idx_check(cufet_to_int({EmitExpr(sa.Index)}), ({targetExpr})->len, \"{nm}\", {sa.Line}))";
        }
        if (sa.Index == null)
            return $"({targetExpr})->data[cufet_last_check(({targetExpr})->len, \"{nm}\", {sa.Line}) - 1]";
        string idxExpr = EmitExpr(sa.Index);
        return $"({targetExpr})->data[cufet_idx_check(cufet_to_int({idxExpr}), ({targetExpr})->len, \"{nm}\", {sa.Line}) - 1]";
    }

    // The name used in OOB messages — the interpreter passes the variable's name (quoted inside
    // the message template); non-variable targets fall back like SeriesDisplayName does.
    private static string SeriesDisplayName(IExpression target) =>
        target is VariableReference vr ? vr.Name : "the series";

    private string EmitCastExpr(CastExpression cast)
    {
        // Foreign source, not a Cufet function — there is no body to call, only C to paste in.
        if (cast.RunsAxiom is { } axiom) return EmitAxiomCall(axiom, cast.Args, cast.Line);

        // ★ The same call reached through a VALUE: no source to paste, so it goes through the
        // {fn, env} struct exactly as a call to a function value does. EmitIndirectCall works
        // unchanged because the struct IS the one a function value uses — see EmitCTypeRaw.
        if (cast.RunsAxiomValue) return EmitIndirectCall(cast.Function, cast.Args);

        var fn = CalledFunction(cast.Function, cast.ResolvedFunctionName, cast.Line, cast.Column);
        // A BARE fallible call (not wrapped by but-on-failure / propagate) is only valid inside
        // a Try: on failure, record the failure and jump to the handler; otherwise yield the T.
        if (FallibleReturnType(cast) is { } ft)
            return EmitFallibleCheckGoto(EmitCall(fn, cast.Args), RegisterFailableStruct(ft));
        return EmitCall(fn, cast.Args);
    }

    /// <summary>The function a cast actually reaches, once a filled-in name is known.</summary>
    /// <remarks>
    /// ★ A function that left blanks is emitted once PER FILLING, under a name naming that filling
    /// (`first-two of number`). The name at the call site is the template's, and no body answers to
    /// it — the template is dropped before either backend runs. The checker records which filling
    /// this call reached; this honours it, and the interpreter has the identical helper.
    /// </remarks>
    private static IExpression CalledFunction(IExpression written, string? resolved, int line, int column) =>
        resolved is null                   ? written
        : written is PossessiveAccess pa   ? new PossessiveAccess(pa.Target, resolved, line, column)
        :                                    new VariableReference(resolved, line, column);

    // Binds the raw fallible result to a temp; if it failed, records it into the current Try's
    // caught-failure var and gotos the handler; the expression value is the success `.val`.
    private string EmitFallibleCheckGoto(string rawCall, string cflName)
    {
        string tmp = $"cf_fl{_freshId++}";
        _preEmits.Add($"{cflName} {tmp} = {rawCall};");
        if (_currentTryHandler is not { } h)
            throw new CompilerException("a fallible call must be handled (Try, 'but on failure', or 'or pass the failure off').");
        _preEmits.Add($"if ({tmp}.is_failure) {{ {FailureGotoBody(h, $"{tmp}.message", $"{tmp}.category")} }}");
        return $"{tmp}.val";
    }

    // Emits the raw fallible expression (the `cfl` tagged struct) without the call-site check —
    // used by but-on-failure and propagate, which inspect is_failure themselves.
    private (string CflName, string Expr) EmitFallibleRaw(IExpression expr, CufetType resultInner)
    {
        if (expr is FileReadExpression fr)
            return (RegisterFailableStruct(new FailureType(FileReadSuccessType(fr))), EmitFileReadRaw(fr));
        if (expr is RunExpression || expr is PipeExpression)
            return (RegisterFailableStruct(new FailureType(RunResultRecordType)), EmitRunRaw(expr));
        if (expr is AwaitedResultExpression are)
        {
            string raw = EmitAwaitedRaw(are, out var info);
            var aft = info.ResultType as FailureType ?? new FailureType(info.ResultType ?? TNumber);
            return (RegisterFailableStruct(aft), raw);
        }
        if (expr is BinaryExpression mb && IsMatrixOp(mb))   // matrix +/−/× with but-on-failure / propagate
            return (RegisterFailableStruct(new FailureType(MatrixType.Instance)), EmitMatrixOpRaw(mb));
        // A FALLIBLE operator overload with but-on-failure / propagate: the raw cfl is just the
        // direct call (the overload function returns the cfl itself), same shape as a fallible call.
        if (expr is BinaryExpression ob && OverloadFor(ob) is { } oovl
            && OverloadReturnType(oovl.LeftTypeName, oovl.RightTypeName, ob.Op) is FailureType ooft)
            return (RegisterFailableStruct(ooft),
                    $"{OverloadFnName(oovl.LeftTypeName, oovl.RightTypeName, ob.Op)}({EmitExpr(ob.Left)}, {EmitExpr(ob.Right)})");
        if (expr is DirectoryContentsExpression dce)         // directory listing with but-on-failure / propagate
            return (RegisterFailableStruct(new FailureType(new SeriesType(TText))), EmitDirRaw(dce));
        if (FallibleReturnType(expr) is { } ft)
            return (RegisterFailableStruct(ft), EmitCall(((CastExpression)expr).Function, ((CastExpression)expr).Args));
        // A bare failure literal (or other failable) as the operand — coerce into cfl of the result T.
        var ftype = new FailureType(resultInner);
        return (RegisterFailableStruct(ftype), EmitAsType(expr, ftype));
    }

    // Resolves a Cast to a free-function call or a direct method dispatch, and returns the
    // C call expression. Method receiver is passed by address (&(recv)); `one` and lvalue
    // variables work directly, and C99 compound-literal temporaries are lvalues too.
    private string EmitCall(IExpression funcExpr, IReadOnlyList<IExpression> args)
    {
        // Recursion inside a nested Bind: its own name → a direct self-call reusing the current env.
        if (funcExpr is VariableReference svr && _closureSelf is { } self && svr.Name == self.Name)
        {
            var selfArgs = new[] { "cf_envp" }.Concat(args.Select(EmitExpr));
            return $"{self.ClosFn}({string.Join(", ", selfArgs)})";
        }
        if (funcExpr is VariableReference vr)
        {
            if (_funcReturnTypes.ContainsKey(vr.Name) && !_varTypes.ContainsKey(vr.Name))   // free function (direct)
            {
                // An interface-taking function is monomorphized: route to the specialization for the
                // concrete conformer(s) passed here (registering it if this is the first such call).
                if (_ifaceFuncs.TryGetValue(vr.Name, out var ifb))
                    return EmitSpecializedCall(ifb, null, args, null);

                // ★ Each argument is emitted AS ITS PARAMETER'S TYPE, not as whatever it happens
                // to be. This is the language's one implicit coercion — widening T into a voidable,
                // a union or a failable — and an argument position is as much a slot as a field or
                // a series element. Emitting the raw expression here produced C that assigned a
                // `cd_box` straight into a `cun_0` parameter: the checker was happy, `check
                // --native` was happy, and gcc refused it. Every other slot already went through
                // EmitAsType; this one was missed.
                return $"{MangleName(vr.Name)}({string.Join(", ", EmitArgsAsParams(vr.Name, args))})";
            }

            // A function-VALUED variable (Define f as …) → an indirect call through the {fn, env} value.
            // NoStashes because a `stash of T` parameter is one of these — see its note.
            if (_varTypes.TryGetValue(vr.Name, out var vt) && NoStashes(vt) is FunctionType)
                return EmitIndirectCall(funcExpr, args);

            // Method dispatch: args[0] is the receiver, the rest are method params.
            if (args.Count > 0 && TypeOf(args[0]) is ObjectType ot)
            {
                var (owner, suffix) = ResolveMethodLevel(ot.Name, vr.Name);
                string recv = $"&(({EmitExpr(args[0])}){suffix})";
                if (_ifaceMethods.TryGetValue((owner, vr.Name), out var ifm))
                    return EmitSpecializedCall(ifm, owner, args.Skip(1).ToList(), recv);
                var call = new[] { recv }.Concat(args.Skip(1).Select(EmitExpr));
                return $"{MethodCName(owner, vr.Name)}({string.Join(", ", call)})";
            }
            // ★ Two very different faults reached this line with one message, and the message
            // named the wrong one. A nested Bind is a CLOSURE, emitted where it stands, so a call
            // to a name declared further down the same block cannot resolve — while the same
            // program interprets fine, because the interpreter resolves the name when the call
            // actually happens. Saying "not a known function" of a function declared six lines
            // below sends the reader hunting for a typo that is not there.
            if (IsBoundSomewhere(vr.Name))
                throw new CompilerException(
                    $"'{vr.Name}' is declared further down this block, and a Bind nested inside a rabbit " +
                    $"or another function is compiled where it stands — so it cannot be called before its " +
                    $"declaration. Reordering does not help two functions that call each other, because " +
                    $"neither can come first. Declare them at the TOP LEVEL, where mutual recursion works " +
                    $"on both backends. (Interpreted, this program runs: the interpreter looks the name up " +
                    $"at the moment of the call.)");

            throw new CompilerException($"'{vr.Name}': unresolved call — not a known function or method.");
        }

        // Book-member call: `<book>'s <member> of (args)` (a Cast of a book possessive-access).
        // A member the book's Cufet layer defines is ordinary method emission instead —
        // funcExpr's member is already the resolved (filled) name. The receiver is a fresh
        // compound literal rather than the pull's binding: the layer object is FIELDLESS by
        // decision (a module with fields is refused at the pull), so an empty receiver is
        // semantically exact — and it keeps a Bind hoisted out of the pull body working, where
        // the pull-scope binding is not in C scope.
        if (funcExpr is PossessiveAccess bpa && bpa.Target is VariableReference bookRef
            && _bookAliases.TryGetValue(bookRef.Name, out var bookName))
        {
            if (CufetLayerHasMethod(bookName, bpa.Member))
            {
                var (lOwner, _) = ResolveMethodLevel(bookName, bpa.Member);
                string lRecv = $"&(({EmitCType(ObjType(bookName))}){{0}})";
                if (_ifaceMethods.TryGetValue((lOwner, bpa.Member), out var lIfm))
                    return EmitSpecializedCall(lIfm, lOwner, args, lRecv);
                var lCall = new[] { lRecv }.Concat(args.Select(EmitExpr));
                return $"{MethodCName(lOwner, bpa.Member)}({string.Join(", ", lCall)})";
            }
            return EmitBookFunction(bookName, bpa.Member, args);
        }

        if (funcExpr is PossessiveAccess pa && TypeOf(pa.Target) is ObjectType pot)   // alice's greet
        {
            var (owner, suffix) = ResolveMethodLevel(pot.Name, pa.Member);
            string recv = $"&(({EmitExpr(pa.Target)}){suffix})";
            if (_ifaceMethods.TryGetValue((owner, pa.Member), out var ifpm))
                return EmitSpecializedCall(ifpm, owner, args, recv);
            var call = new[] { recv }.Concat(args.Select(EmitExpr));
            return $"{MethodCName(owner, pa.Member)}({string.Join(", ", call)})";
        }

        // Any other expression yielding a function value (e.g. an element of a series of functions) →
        // an indirect call through its {fn, env}.
        if (TypeOf(funcExpr) is FunctionType)
            return EmitIndirectCall(funcExpr, args);

        throw new CompilerException("Function-value calls are not yet supported by the compiler.");
    }

    // A named function used as a VALUE → the {fn, NULL} closure value (env NULL — no capture in CL.1).
    private string EmitNamedFunctionValue(string name)
    {
        // A monomorphized function has no single C signature (one per conformer), so it can't be
        // taken as a value. The front-end's parameter-only interface surface means this shouldn't
        // be reachable — refuse loudly rather than pick an arbitrary specialization.
        if (_ifaceFuncs.ContainsKey(name))
            throw new CompilerException(
                $"'{name}' takes an interface parameter, so it exists only as monomorphized copies " +
                "(one per concrete type passed) and can't be used as a function value. Call it directly.");
        _fnThunks.Add(name);
        string cfn = RegisterFuncStruct(_funcTypes[name]);
        return $"({cfn}){{ .fn = {FnThunkName(name)}, .env = NULL }}";
    }

    // Calls through a function VALUE: bind it to a temp (single eval — the expr may add preemits),
    // then `tmp.fn(tmp.env, args…)`. The env is NULL until CL.2, but the call passes it uniformly so
    // a captured closure (CL.2) calls identically.
    private string EmitIndirectCall(IExpression funcExpr, IReadOnlyList<IExpression> args)
    {
        // ★ An axiom value is called through the SAME struct, so this path serves both. The cast it
        // replaces was safe only while a function value was the only callable a name could hold.
        var ft = TypeOf(funcExpr) switch
        {
            FunctionType f => f,
            AxiomType a    => AsFunctionType(a),
            var other      => throw new CompilerException(
                                  $"'{FormatTypeName(other)}' cannot be called through a value."),
        };
        string cfn = RegisterFuncStruct(ft);
        string val = EmitExpr(funcExpr);
        string tmp = $"cf_fn{_freshId++}";
        _preEmits.Add($"{cfn} {tmp} = {val};");
        var call = new[] { $"{tmp}.env" }.Concat(args.Select(EmitExpr));
        return $"{tmp}.fn({string.Join(", ", call)})";
    }

    // The free variables a lambda / nested-Bind body captures: referenced enclosing locals not defined
    // in the body and not its own params. Includes `one` (the method receiver — it IS in _varTypes
    // inside a method, and objects are value types so it snapshots like any value capture). Excludes
    // `input` (it lowers to the `stdin` global — nothing to capture). `the failure` isn't a _varTypes
    // entry (it's the enclosing Try's CufetFailure C var) so it's reported separately via `capturesFailure`.
    private List<string> ClosureFreeVars(IReadOnlyList<(CufetType Type, string Name)> parameters,
                                        IReadOnlyList<IStatement> body, out bool capturesFailure)
    {
        var refs = new HashSet<string>(); var defs = new HashSet<string>();
        foreach (var s in body) CollectRefsDefs(s, refs, defs);
        var pnames = parameters.Select(p => p.Name).ToHashSet();
        capturesFailure = refs.Contains("the failure") && !defs.Contains("the failure") && _currentFailVar != null;
        return refs.Where(r => !defs.Contains(r) && !pnames.Contains(r)
                            && r != "input" && r != "the failure"
                            && _varTypes.ContainsKey(r))
                            .OrderBy(x => x).ToList();
    }

    // A lambda / nested-Bind body's inferred return type (first value-returning path; void if none).
    // Params must already be in _varTypes; tracks body-local DefineStatements as it walks so a
    // `Return <local>` resolves (same trap as InferTaskResultType / InferStageOutput).
    private CufetType? InferBodyReturnType(IReadOnlyList<IStatement> body)
    {
        var saved = new Dictionary<string, CufetType>(_varTypes);
        CufetType? result = null;
        bool found = false;
        void Walk(IReadOnlyList<IStatement> stmts)
        {
            foreach (var s in stmts)
            {
                if (found) return;
                switch (s)
                {
                    case DefineStatement d: var dt = TypeOf(d.Value); if (dt != null) _varTypes[d.Name] = dt; break;
                    case ReturnStatement { Value: null }: found = true; result = null; return;
                    case ReturnStatement { Value: VoidLiteral }: found = true; result = null; return;
                    case ReturnStatement r: found = true; result = TypeOf(r.Value!); return;
                    case IfStatement iff:
                        foreach (var a in iff.Arms) { Walk(a.Body); if (found) return; }
                        if (iff.ElseBody != null) Walk(iff.ElseBody);
                        break;
                    case WhileStatement w: Walk(w.Body); break;
                    case RepeatUntilStatement ru: Walk(ru.Body); break;
                    case ForEachStatement fe: Walk(fe.Body); break;
                    case ForEachFromInputStatement fi: Walk(fi.Body); break;
                }
            }
        }
        Walk(body);
        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
        return result;
    }

    // The FunctionType of a lambda (params + inferred return) — used both to type the value and to
    // pick the cfn struct. Captures are read from the current _varTypes (the creating scope).
    private FunctionType LambdaFunctionType(LambdaLiteral lam)
    {
        var savedVT = new Dictionary<string, CufetType>(_varTypes);
        foreach (var (pt, pn) in lam.Parameters) _varTypes[pn] = pt;
        var ret = InferBodyReturnType(lam.Body);
        _varTypes.Clear();
        foreach (var kv in savedVT) _varTypes[kv.Key] = kv.Value;
        return new FunctionType(lam.Parameters.Select(p => p.Type).ToList(), ret);
    }

    // A lambda literal → a closure VALUE `(cfn_N){ .fn = cv_clos<id>, .env = <env or NULL> }`. The body
    // becomes a top-level function `cv_clos<id>(void* env, params…)` (in _closureFns); when it captures,
    // an env struct `cenv_<id>` (in _closureEnvs) holds the free vars, arena-allocated + populated at the
    // site. Capture policy = value-struct field storage: value types copy in (snapshot), region types
    // store the shared pointer (share) — binding-is-binding, no extra machinery. env=NULL if no capture.
    private string EmitLambda(LambdaLiteral lam) => EmitClosure(lam.Parameters, lam.Body, lam.Line, null);

    // Shared by lambdas and nested Binds (a nested Bind is a named local closure). `selfName`, if set,
    // is the closure's own name — inside the body, a call to it resolves to a SELF-CALL of this closure
    // fn reusing the current env (recursion by-name; the recursive closure has the same captures), so
    // the name is NOT captured (no self-referential env). Matches the interpreter's in-scope-name recursion.
    private string EmitClosure(IReadOnlyList<(CufetType Type, string Name)> parameters,
                            IReadOnlyList<IStatement> body, int line, string? selfName)
    {
        int id = _closureCounter++;
        string clos = $"cv_clos{id}";
        var savedVT = new Dictionary<string, CufetType>(_varTypes);
        var free = ClosureFreeVars(parameters, body, out bool capturesFailure);
        if (selfName != null) free.Remove(selfName);   // recursion is by-name (self-call), not captured
        string? capturedFailVar = capturesFailure ? _currentFailVar : null;   // the enclosing Try's CufetFailure

        // ESC.4 — capture-escape. If this closure ESCAPES to a shallower depth T (threaded from the
        // escaping store's RHS via `_closureEscapeDepth`), each region-bearing capture DECLARED
        // DEEPER than T would dangle after its rabbit's Done.-pop, so it is deep-copied into T's arena
        // at capture time (the ESC.2 copy family). A capture declared at or above T is left shared —
        // its source outlives the destination, so sharing stays correct. And when a closure escapes,
        // any deeper capture's SOURCE is necessarily in a rabbit that pops before the destination is
        // read, so the copy is observationally identical to sharing (nothing can mutate a dead
        // source) — value snapshots and live-region sharing are both preserved.
        int? escTo = _closureEscapeDepth; _closureEscapeDepth = null;   // consume (this closure's, not a nested one's)
        bool Escapes(string v) =>
            escTo is { } t && TypeChecker.IsRegionBearing(savedVT[v]) && CaptureDepth(v) > t;

        var ret = InferBodyReturnTypeWithParams(parameters, body);
        var ft = new FunctionType(parameters.Select(p => p.Type).ToList(), ret);
        string cfn = RegisterFuncStruct(ft);
        string retC = ret == null ? "void" : EmitCType(ret);
        string? envType = (free.Count > 0 || capturesFailure) ? $"cenv_{id}" : null;
        const string capFailField = "cf_capfail";   // fixed name: "the failure" has no valid mangling

        // Env struct (captured free vars), while _varTypes still has the enclosing scope.
        if (envType != null)
        {
            _closureEnvs.AppendLine($"typedef struct {{");
            foreach (var v in free) _closureEnvs.AppendLine($"    {EmitCType(savedVT[v])} {MangleName(v)};");
            if (capturesFailure) _closureEnvs.AppendLine($"    CufetFailure {capFailField};");
            _closureEnvs.AppendLine($"}} {envType};");
        }

        // Emit the closure function into _closureFns with a fresh preemit/return/exc context and the
        // lambda's own scope (captures + params only — enclosing locals are reached via the env).
        var savedPre = new List<string>(_preEmits); _preEmits.Clear();
        var savedRet = _currentReturnType; var savedExc = _excOpen; _excOpen = 0;
        var savedFail = _currentFailVar;
        _currentFailVar = capturesFailure ? capFailField : null;   // `the failure` → the captured copy
        var savedNarrow = new Dictionary<string, (CufetType, string)>(_narrowedVars); _narrowedVars.Clear();
        // `it` does not cross into a closure body, and neither does the arm set that qualifies it.
        var savedArmCases = new Dictionary<string, List<int>>(_armCases); _armCases.Clear();
        var savedSelf = _closureSelf;
        _closureSelf = selfName != null ? (selfName, clos, ft) : null;   // self-reference → self-call
        _currentReturnType = ret;
        _varTypes.Clear();
        foreach (var v in free) _varTypes[v] = savedVT[v];
        foreach (var (pt, pn) in parameters) _varTypes[pn] = pt;
        if (capturesFailure) _varTypes["the failure"] = TFailMarker;   // so `the message of the failure` types

        // Emit into a LOCAL buffer so a NESTED lambda (which appends its own complete function to
        // _closureFns during EmitBlock) lands BEFORE this enclosing function — not spliced into it.
        var fnBuf = new StringBuilder();
        var sigParams = new[] { "void* cf_envp" }.Concat(parameters.Select(p => $"{EmitCType(p.Type)} {MangleName(p.Name)}"));
        fnBuf.AppendLine($"{retC} {clos}({string.Join(", ", sigParams)}) {{");
        if (envType != null)
        {
            fnBuf.AppendLine($"    {envType}* cf_env = ({envType}*)cf_envp;");
            foreach (var v in free)
            {
                // `one` (the method receiver) is held BY VALUE in the env (objects are value types →
                // snapshot); the body emits `(*cv_one)`, so re-create the pointer over a local copy.
                if (v == "one")
                    fnBuf.AppendLine($"    {EmitCType(savedVT[v])} cf_onev{id} = cf_env->cv_one; {EmitCType(savedVT[v])}* cv_one = &cf_onev{id};");
                else
                    fnBuf.AppendLine($"    {EmitCType(savedVT[v])} {MangleName(v)} = cf_env->{MangleName(v)};");
            }
            if (capturesFailure) fnBuf.AppendLine($"    CufetFailure {capFailField} = cf_env->{capFailField};");
        }
        else fnBuf.AppendLine($"    (void)cf_envp;");
        var savedCF = EnterFrame(fnBuf, "    ");
        EmitBlock(fnBuf, body, "    ");
        ExitFrame(savedCF);
        fnBuf.AppendLine($"}}");
        fnBuf.AppendLine();
        _closureFns.Append(fnBuf);

        // Restore the enclosing compiler state.
        _preEmits.Clear(); _preEmits.AddRange(savedPre);
        _currentReturnType = savedRet; _excOpen = savedExc; _currentFailVar = savedFail;
        _closureSelf = savedSelf;
        _narrowedVars.Clear();
        foreach (var kv in savedNarrow) _narrowedVars[kv.Key] = kv.Value;
        _armCases.Clear();
        foreach (var kv in savedArmCases) _armCases[kv.Key] = kv.Value;
        _varTypes.Clear();
        foreach (var kv in savedVT) _varTypes[kv.Key] = kv.Value;

        // At the site: allocate + populate the env, yield the closure value. When the closure escapes
        // (escTo set), the env struct itself AND each escaping capture are allocated in the
        // destination's arena; non-escaping captures are stored as-is (shared/snapshotted at the
        // creating scope, exactly as before).
        if (envType != null)
        {
            string envVar = $"cf_cenv{id}";
            // The env record must live at the destination too (else the struct dangles even if its
            // fields don't) — allocate at escTo's arena depth via the ESC.2 allocation override.
            string envAlloc = escTo is { } et
                ? $"({envType}*)cufet_arena_alloc_at({EscapeArenaDepth(et)}, sizeof({envType}))"
                : $"({envType}*)cufet_arena_alloc(sizeof({envType}))";
            _preEmits.Add($"{envType}* {envVar} = {envAlloc};");
            foreach (var v in free)
            {
                // Synthetic reference to a captured name — only the name is read, so the
                // column has nothing to point at.
                string capExpr = EmitExpr(new VariableReference(v, line, 0));
                if (Escapes(v)) capExpr = EmitEscapeCopy(capExpr, savedVT[v], escTo);   // ESC.4 deep-copy outward
                _preEmits.Add($"{envVar}->{MangleName(v)} = {capExpr};");
            }
            // `the failure` isn't a VariableReference target — copy the enclosing Try's CufetFailure.
            if (capturesFailure) _preEmits.Add($"{envVar}->{capFailField} = {capturedFailVar};");
            return $"({cfn}){{ .fn = {clos}, .env = {envVar} }}";
        }
        return $"({cfn}){{ .fn = {clos}, .env = NULL }}";
    }

    // A captured variable's declaring rabbit depth. Tracked at the in-rabbit birth sites (Define,
    // foreach iterators); anything untracked is a frame-base binding (param / `one` / with-binding),
    // which lives at depth 0 (outer) and is safe to share.
    private int CaptureDepth(string name) => _varRabbitDepth.TryGetValue(name, out var d) ? d : 0;

    private CufetType? InferBodyReturnTypeWithParams(IReadOnlyList<(CufetType Type, string Name)> parameters, IReadOnlyList<IStatement> body)
    {
        var savedVT = new Dictionary<string, CufetType>(_varTypes);
        foreach (var (pt, pn) in parameters) _varTypes[pn] = pt;
        var ret = InferBodyReturnType(body);
        _varTypes.Clear();
        foreach (var kv in savedVT) _varTypes[kv.Key] = kv.Value;
        return ret;
    }

    // ★ NOTHING reaches here any more. Both bundled books are written in Cufet, in their own
    // layers (`src/Interpreter/Prelude/*.cufe`), so every book-member call emits as ordinary
    // method dispatch — CufetLayerHasMethod routes it before this is consulted. The refusal is
    // kept as the honest answer for a member no layer defines, rather than deleted, because a
    // book name with no member behind it should say so rather than emit something.
    private string EmitBookFunction(string bookName, string member, IReadOnlyList<IExpression> args) =>
        throw new CompilerException($"book '{bookName}' has no member '{member}'.");

    // Handles is void / is not void, voidable-vs-voidable, and voidable-vs-plain-T equality.
    // Returns null when neither operand is void/voidable (the caller falls through).
    private string? EmitVoidableComparison(BinaryExpression b)
    {
        bool eq = b.Op == TokenType.Equal;

        // is void / is not void — one operand is the bare `void` literal.
        if (b.Left is VoidLiteral || b.Right is VoidLiteral)
        {
            var side = b.Left is VoidLiteral ? b.Right : b.Left;
            string v = EmitExpr(side);            // evaluated once; only .has is read
            return eq ? $"(!({v}).has)" : $"(({v}).has)";
        }

        var lt = TypeOf(b.Left);
        var rt = TypeOf(b.Right);

        if (lt is VoidableType && rt is VoidableType)            // voidable vs voidable
        {
            string e = EqCall(EmitExpr(b.Left), EmitExpr(b.Right), lt);
            return eq ? $"({e})" : $"(!({e}))";
        }
        if (lt is VoidableType lv && rt.Equals(lv.Inner))         // voidable vs plain T
            return VoidableVsInner(EmitExpr(b.Left), EmitExpr(b.Right), lv.Inner, eq);
        if (rt is VoidableType rv && lt.Equals(rv.Inner))         // plain T vs voidable
            return VoidableVsInner(EmitExpr(b.Right), EmitExpr(b.Left), rv.Inner, eq);

        return null;
    }

    // A voidable equals a plain T iff it's present and the value matches.
    private string VoidableVsInner(string voidableExpr, string tExpr, CufetType inner, bool eq)
    {
        string present = $"(({voidableExpr}).has && {EqCall($"({voidableExpr}).val", tExpr, inner)})";
        return eq ? present : $"(!{present})";
    }

    private string EmitUnary(UnaryExpression u) => u.Op switch
    {
        TokenType.Minus => $"cufet_neg({EmitExpr(u.Operand)})",
        TokenType.Not when TypeOf(u.Operand) is BitsType
                        => $"cufet_bits_not({EmitExpr(u.Operand)})",
        TokenType.Not   => $"(!{EmitExpr(u.Operand)})",
        _ => throw new CompilerException($"Unary operator '{u.Op}' is not yet supported by the compiler.")
    };

    private string EmitBinary(BinaryExpression b)
    {
        // Void / voidable comparisons first — a bare `void` operand has no standalone C form,
        // so it must be handled before EmitExpr touches the operands.
        if (b.Op is TokenType.Equal or TokenType.NotEqual && EmitVoidableComparison(b) is { } vc)
            return vc;

        // Operator overload: same-type object operands with a registered overload take priority over
        // the numeric path (matching the interpreter's dispatch order). Resolution is exact-nominal
        // with one candidate, so this is a compile-time lookup → a DIRECT CALL. A FALLIBLE overload
        // (its body returns a failure) routes through the standard fallible machinery, exactly like
        // matrix arithmetic below — Try / `but on failure` / propagate all compose for free.
        if (OverloadFor(b) is { } ovl)
        {
            var ort = OverloadReturnType(ovl.LeftTypeName, ovl.RightTypeName, b.Op);
            string call = $"{OverloadFnName(ovl.LeftTypeName, ovl.RightTypeName, b.Op)}({EmitExpr(b.Left)}, {EmitExpr(b.Right)})";
            return ort is FailureType oft
                ? EmitFallibleCheckGoto(call, RegisterFailableStruct(oft))
                : call;
        }

        // Matrix arithmetic is FALLIBLE (dimension mismatch → failure): a bare matrix op routes
        // through the standard fallible machinery — check-goto in a Try, exactly like a fallible call.
        // Scaling cannot fail, so it is a plain expression — no failable, no check-goto. It is
        // tested BEFORE IsMatrixOp because `matrix * matrix` and `matrix * number` share an
        // operator and only the operand types tell them apart.
        if (IsMatrixScale(b)) return EmitMatrixScale(b);

        if (IsMatrixOp(b))
            return EmitFallibleCheckGoto(EmitMatrixOpRaw(b), RegisterFailableStruct(new FailureType(MatrixType.Instance)));

        string L = EmitExpr(b.Left);
        string R = EmitExpr(b.Right);

        switch (b.Op)
        {
            // Bit-pattern arithmetic, before the decimal path — the operands are CufetBits,
            // not CufetDec. Division is INTEGER division here.
            case TokenType.Plus    when TypeOf(b.Left) is BitsType: return $"cufet_bits_add({L}, {R}, {b.Line})";
            case TokenType.Minus   when TypeOf(b.Left) is BitsType: return $"cufet_bits_sub({L}, {R}, {b.Line})";
            case TokenType.Star    when TypeOf(b.Left) is BitsType: return $"cufet_bits_mul({L}, {R}, {b.Line})";
            case TokenType.Slash   when TypeOf(b.Left) is BitsType: return $"cufet_bits_div({L}, {R}, {b.Line})";
            case TokenType.Percent when TypeOf(b.Left) is BitsType: return $"cufet_bits_mod({L}, {R}, {b.Line})";
            case TokenType.Plus:    return $"cufet_add({L}, {R})";
            case TokenType.Minus:   return $"cufet_sub({L}, {R})";
            case TokenType.Star:    return $"cufet_mul({L}, {R})";
            case TokenType.Slash:   return $"cufet_div({L}, {R}, {b.Line})";
            case TokenType.Percent: return $"cufet_mod({L}, {R}, {b.Line})";
            // Gates on bit patterns. Note these are function calls, not && / ||: combining two
            // patterns needs both operands, so there is nothing to short-circuit. That
            // asymmetry with the fact case below is deliberate and type-directed.
            case TokenType.And when TypeOf(b.Left) is BitsType: return $"cufet_bits_and({L}, {R})";
            case TokenType.Or  when TypeOf(b.Left) is BitsType: return $"cufet_bits_or({L}, {R})";
            case TokenType.Xor when TypeOf(b.Left) is BitsType: return $"cufet_bits_xor({L}, {R})";
            case TokenType.And:     return $"({L} && {R})";
            case TokenType.Or:      return $"({L} || {R})";
            // On facts, xor cannot short-circuit either — both sides always decide the answer.
            // '!=' on two normalised ints is exclusive-or.
            case TokenType.Xor:     return $"(!!({L}) != !!({R}))";
        }

        // Comparison / equality. Numbers via cufet_cmp; text via strcmp; facts are ints.
        var lt = TypeOf(b.Left);

        // Bit patterns compare on VALUE ALONE, ignoring base and width: 0xFF, 0x00FF and
        // 0b11111111 are the same pattern written three ways. Comparing the structs field by
        // field would call them different, and width must not be load-bearing here.
        if (lt is BitsType)
            return b.Op switch
            {
                TokenType.Equal    => $"(({L}).value == ({R}).value)",
                TokenType.NotEqual => $"(({L}).value != ({R}).value)",
                TokenType.Lt       => $"(({L}).value <  ({R}).value)",
                TokenType.Gt       => $"(({L}).value >  ({R}).value)",
                TokenType.Lte      => $"(({L}).value <= ({R}).value)",
                TokenType.Gte      => $"(({L}).value >= ({R}).value)",
                _ => throw new CompilerException($"Binary operator '{b.Op}' is not yet supported on bits.")
            };

        if (lt is NumberType)
        {
            string cmp = $"cufet_cmp({L}, {R})";
            return b.Op switch
            {
                TokenType.Equal    => $"({cmp} == 0)",
                TokenType.NotEqual => $"({cmp} != 0)",
                TokenType.Lt       => $"({cmp} < 0)",
                TokenType.Gt       => $"({cmp} > 0)",
                TokenType.Lte      => $"({cmp} <= 0)",
                TokenType.Gte      => $"({cmp} >= 0)",
                _ => throw new CompilerException($"Binary operator '{b.Op}' is not yet supported by the compiler.")
            };
        }

        if (lt is TextType)
            return b.Op switch
            {
                TokenType.Equal    => $"(strcmp({L}, {R}) == 0)",
                TokenType.NotEqual => $"(strcmp({L}, {R}) != 0)",
                _ => throw new CompilerException($"Text comparison '{b.Op}' is not yet supported by the compiler.")
            };

        // ★ Everything else compares through EqCall — the ONE place that knows how each type
        // does it: records structurally, objects nominally, series element-wise and in order,
        // maps and matrices by reference (which is what the interpreter does for them), facts as
        // ints. Its default arm refuses by name, so a type nobody taught it fails loudly.
        //
        // ⚠ This used to be two branches: records/objects/series through EqCall, and then a
        // CATCH-ALL emitting `==` "for facts and maps". Anything else the checker allowed landed
        // in that catch-all and became `==` on a C STRUCT, which gcc rejects — so `hopper is
        // grace` type-checked, interpreted to false, and would not build. A function value
        // compared the same way and broke identically. The catch-all was the bug: it assumed
        // what was left rather than saying it, so each new type joined it silently.
        if (b.Op is TokenType.Equal or TokenType.NotEqual)
        {
            string eq = EqCall(L, R, lt);
            return b.Op == TokenType.Equal ? $"({eq})" : $"(!({eq}))";
        }
        throw new CompilerException(
            $"Binary operator '{b.Op}' on a '{FormatTypeName(lt)}' is not supported (only is / is not).");
    }

    // cv_ prefix avoids C keyword collisions (e.g. Cufet "double" → cv_double).
    // Hyphens (and the spaces in a filled-in name) become underscores; Cufet identifiers never
    // contain underscores, so no collision. See CIdent — one flattening rule, used everywhere.
    private static string MangleName(string name) => "cv_" + CIdent(name);

    // Maps a Cufet type to its C type. Records are value structs (synthesized per shape);
    // text is an immutable const char*; series/maps are arena pointers. Objects/maps are
    // not yet lowered (later slices) — the default arm defers cleanly.
    private string EmitCType(CufetType? type) => EmitCTypeRaw(type == null ? null : NoStashes(type));

    private string EmitCTypeRaw(CufetType? type) => type switch
    {
        null       => "void",
        NumberType => "CufetDec",
        BitsType   => "CufetBits",
        FactType   => "int",
        TextType   => "const char*",
        // ★ A foreign pointer, held and handed back and never read through. `void*` deliberately —
        // whatever C called it, Cufet knows only that it is an address, so there is no type here to
        // get wrong and no layout to compute.
        AddressType => "void*",
        SeriesType st => RegisterSeriesStruct(st) + "*",   // series are arena pointers (reference type)
        ChaseType => ChaseCType(),                        // one fixed struct, arena pointer
        RecordType rt => RegisterRecordStruct(rt),
        ObjectType ot => ObjStructName(ot.Name),
        VoidableType vt => RegisterVoidableStruct(vt),
        MapType mt => RegisterMapStruct(mt) + "*",   // maps are arena pointers (reference type)
        FailureType ft => RegisterFailableStruct(ft),
        FailureMarkerType => "CufetFailure",         // a caught / bare failure (message + category)
        ReadableStreamType or WritableStreamType => "FILE*",   // a stream is an open FILE* (or stdin)
        RabbitType => "cufet_rabbit",                          // a rabbit is its name, as in the interpreter
        ChannelType => "cufet_chan*",                          // a channel is a shared mutex/condvar queue
        // ★ A task handle is its result box, shared by pointer exactly as a channel is. This arm
        // used to be missing, and ONE call site special-cased around it (`CapCType` asked
        // `is TaskHandleType ? "cufet_rbox*" : EmitCType(…)`) — so the C type of a task handle was
        // decided outside the switch that decides every other type's, and anything else asking
        // this got a compiler exception instead of an answer. Found by the per-type audit.
        TaskHandleType => "cufet_rbox*",
        MatrixType => MatrixCType(),                           // a matrix is an arena pointer (reference type)
        FunctionType ft => RegisterFuncStruct(ft),             // a function value is a {fn, env} value struct
        // ★★ An axiom VALUE is the same {fn, env} struct a function value is, pointing at a thunk
        // that marshals, calls the wrapper and converts back. Not a bare C function pointer to the
        // wrapper: the wrapper speaks C's types (CufetForeignWhole, const char*), and a value that
        // could be passed to anything expecting a callable has to speak Cufet's. Sharing the struct
        // is what makes passing, storing and calling one work with no new machinery at all.
        AxiomType ax => RegisterFuncStruct(AsFunctionType(ax)),
        UnionType uop when uop.Cases == null => MarkOpenUnion(),   // ALL open unions share one struct
        UnionType u1c when u1c.Cases is { Count: 1 } => EmitCType(u1c.Cases[0]),   // 1-case union IS that type
        UnionType ut => RegisterUnionStruct(ut),               // a closed union is a {tag, payload} value struct

        _ => throw new CompilerException(
                $"the compiler cannot represent a {FormatTypeName(type!)} yet.")
    };

    // Emits a number literal as a CufetDec constructor, decomposing the C# decimal
    // into its 96-bit coefficient (hi/lo halves), scale, and sign via decimal.GetBits.
    // This is bit-identical to the interpreter because both start from the same decimal.
    private static string EmitNumberLiteral(decimal d)
    {
        int[] bits = decimal.GetBits(d);
        ulong lo   = (uint)bits[0];
        ulong mid  = (uint)bits[1];
        ulong hi   = (uint)bits[2];
        int flags  = bits[3];
        int scale  = (flags >> 16) & 0xFF;
        int sign   = flags < 0 ? 1 : 0;
        ulong lo64 = (mid << 32) | lo;   // low 64 bits of the coefficient
        ulong hi64 = hi;                 // high 32 bits of the coefficient
        return $"cufet_dec_lit({hi64}ULL, {lo64}ULL, {scale}, {sign})";
    }

    // The lexer resolves escape sequences, so StringLiteral.Value is the cooked string.
    // Re-escape it for C: backslash and double-quote must be escaped; control chars normalized.
    private static string EscapeStringLiteral(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value)
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"'  => "\\\"",
                '\n' => "\\n",
                '\t' => "\\t",
                '\r' => "\\r",
                _    => c.ToString()
            });
        sb.Append('"');
        return sb.ToString();
    }
}
