using System.Text;
using Cufet.Interpreter;
using Cufet.Lexer;

namespace Cufet.Compiler;

/// <summary>Every statement, lowered to C</summary>
/// <remarks>
/// <para>Every statement, lowered to C — blocks and scopes, If and Judge, the four For-each forms, Try, pipes and tasks.</para>
/// <para>
/// ★ One class across several files, carved along the boundaries the generator already drew
/// for itself — these were its own section banners, not lines chosen by whoever split it. The
/// state they all share (the struct registries, the arena depth, the pre-emit buffer) stays in
/// <c>CodeGenerator.cs</c>, because it is what the halves talk to each other through.
/// </para>
/// </remarks>
public sealed partial class CodeGenerator
{
    // ── ESC.2 — copy an escaping value into the destination's arena ──────────
    // `escapeToDepth` is the checker's annotation: the destination's RABBIT depth. Returns the
    // value expression wrapped in a deep copy targeting that depth, or the expression unchanged
    // when there's no escape (keeps copying tight — non-escaping and non-region-bearing values are
    // never touched). Types the copy family can't express (e.g. closures — ESC.4) pass through.
    // The arena depth (as a C expression) for a checker RABBIT depth in the current frame.
    private string EscapeArenaDepth(int rabbitDepth) =>
        _frameArenaBase == null ? rabbitDepth.ToString() : $"{_frameArenaBase} + {rabbitDepth}";

    // Wraps a container-growing store (series append/insert, map put) in the allocation override, so
    // the CONTAINER'S OWN growth (`_ensure`'s realloc) lands in the destination's arena too — not
    // just the value. Without this the backing array itself is freed by the inner rabbit's Done.
    private void EmitStoreWithEscape(StringBuilder sb, string indent, int? escapeToDepth, string stmt)
    {
        if (escapeToDepth is not { } d) { sb.AppendLine($"{indent}{stmt}"); return; }
        int id = _freshId++;
        sb.AppendLine($"{indent}{{ int cf_ov{id} = cufet_alloc_override; cufet_alloc_override = {EscapeArenaDepth(d)};");
        sb.AppendLine($"{indent}  {stmt}");
        sb.AppendLine($"{indent}  cufet_alloc_override = cf_ov{id}; }}");
    }

    private string EmitEscapeCopy(string valueExpr, CufetType? valueType, int? escapeToDepth)
    {
        if (escapeToDepth is not { } d || valueType == null) return valueExpr;
        if (!TypeChecker.IsRegionBearing(valueType)) return valueExpr;
        // A CLOSURE value escaping through a store is the indirect case: its env is an opaque void*,
        // so it can't be deep-copied here — its captures had to be copied at CREATION (ESC.4, when
        // the env struct type is known). A closure that escapes must therefore be created directly in
        // the escaping store; refuse the indirect form loudly rather than dangle.
        if (valueType is FunctionType)
            throw new CompilerException(
                "a closure that captures a rabbit-scoped value can only escape its rabbit when it is " +
                "created directly in the escaping store (e.g. `outer becomes a function: … Done.`), not " +
                "stored via an intermediate variable first. Inline the closure at the point it escapes.");
        int idx;
        try { idx = RegisterChanElem(valueType, isTop: false); }
        catch (CompilerException) { return valueExpr; }   // not copy-expressible yet
        _escapeElems.Add(TypeSig(valueType));
        return $"cchan_{idx}_escapecopy({valueExpr}, {EscapeArenaDepth(d)})";
    }

    private (int Depth, string? Base, string? ArenaBase, int Rabbit) EnterFrame(StringBuilder sb, string indent)
    {
        var saved = (_scopeDepth, _frameUnmakerBase, _frameArenaBase, _rabbitDepth);
        _scopeDepth = 0;
        // A frame body starts outside any rabbit region (the checker resets rabbit depth per frame),
        // so the destination-depth arithmetic here lines up with the checker's EscapeToDepth.
        _rabbitDepth = 0;
        if (UsesUnmakers)
        {
            string v = $"cf_umb{_freshId++}";
            sb.AppendLine($"{indent}int {v} = cufet_num; (void){v};");
            _frameUnmakerBase = v;
        }
        else _frameUnmakerBase = null;
        // ESC.2 — this frame's base arena depth: a plain call pushes no arena, so the frame starts
        // at whatever depth the caller was at, and rabbit depth `d` inside it is base + d.
        string ab = $"cf_ab{_freshId++}";
        sb.AppendLine($"{indent}int {ab} = cufet_arena_top; (void){ab};");
        _frameArenaBase = ab;
        return saved;
    }

    private void ExitFrame((int Depth, string? Base, string? ArenaBase, int Rabbit) saved)
    {
        _scopeDepth = saved.Depth;
        _frameUnmakerBase = saved.Base;
        _frameArenaBase = saved.ArenaBase;
        _rabbitDepth = saved.Rabbit;
    }

    // A BLOCK scope (rabbit / book / try-arm / with / if-arm / pipe-consumer) — the interpreter's
    // EnterScope/RunScopeUnmakers-at-ExitScope. Unmakeable objects Defined inside fire their unmakers
    // LIFO at this block's NORMAL exit; nonlocal exits (return/Stop/goto/exception) fire via the
    // run-to-snapshot at their own target. `_scopeDepth++` marks "inside a block" so Defines register.
    // <paramref name="withSnap"/> is handed this block's unmaker snapshot once it exists and before
    // the body is emitted, for a nonlocal exit inside the body that has to run back to it —
    // `Suppress`, which jumps to the handler's end and so past the normal run below.
    private void EmitScopedBlock(StringBuilder sb, IReadOnlyList<IStatement> body, string indent,
                                 Action<string?>? withSnap = null)
    {
        if (!UsesUnmakers) { withSnap?.Invoke(null); EmitBlock(sb, body, indent); return; }
        _scopeDepth++;
        string snap = $"cf_um{_freshId++}";
        sb.AppendLine($"{indent}int {snap} = cufet_num;");
        withSnap?.Invoke(snap);
        EmitBlock(sb, body, indent);
        // If the block always returns, the return path already ran these — skip the (unreachable) run.
        if (!BlockAlwaysExits(body)) sb.AppendLine($"{indent}cufet_run_unmakers_to({snap});");
        _scopeDepth--;
    }

    private void EmitBlock(StringBuilder sb, IReadOnlyList<IStatement> body, string indent)
    {
        // Guard narrowing (mirrors the interpreter's type checker): after an exiting guard —
        // a single-arm `if (cond) { … return … }` with no else — the statements that follow run
        // only when `cond` was false, so a voidable var proven non-void by ¬cond reads as `.val`
        // for the rest of the block. Undone at block end so it never leaks to a sibling block.
        var guardNarrowed = new List<(string Name, (CufetType Type, string Access) Prev, bool Had)>();

        // ⚠ A SHADOWING `Define` hides an outer name for this block only, and C agrees — the inner
        // declaration is emitted in an inner brace and the outer variable comes back at the closing
        // one. `_varTypes` did not come back with it: the shadowed entry stayed, so the first read
        // AFTER the block emitted the inner type's accessor against the outer variable.
        // `Define a shadow value as <voidable number>.` over an outer `number` compiled to
        // `cvd_0_write(cv_value)` on a `CufetDec` and gcc refused the program — while the
        // interpreter, whose scopes really do pop, ran it. No stash and no `For each` involved.
        //
        // ★ Only shadowing defines need this. A non-shadowing one cannot have an outer entry to
        // lose: the checker refuses redeclaring a name an enclosing scope already holds.
        var shadowed = new List<(string Name, CufetType? Type, int Depth, bool HadDepth)>();

        foreach (var stmt in body)
        {
            if (stmt is DefineStatement { Shadow: true } shadow)
                shadowed.Add((shadow.Name,
                              _varTypes.TryGetValue(shadow.Name, out var outerType) ? outerType : null,
                              _varRabbitDepth.TryGetValue(shadow.Name, out var outerDepth) ? outerDepth : 0,
                              _varRabbitDepth.ContainsKey(shadow.Name)));

            EmitStatement(sb, stmt, indent);
            if (stmt is IfStatement { Arms.Count: 1, ElseBody: null } guard
                && BlockAlwaysExits(guard.Arms[0].Body))
            {
                foreach (var (name, inner, access) in GuardNarrowings(guard.Arms[0].Condition))
                {
                    bool had = _narrowedVars.TryGetValue(name, out var prev);
                    guardNarrowed.Add((name, had ? prev : default, had));
                    _narrowedVars[name] = (inner, (had ? prev.Access : "") + access);
                }
            }
        }
        for (int i = guardNarrowed.Count - 1; i >= 0; i--)
        {
            var (name, prev, had) = guardNarrowed[i];
            if (had) _narrowedVars[name] = prev!; else _narrowedVars.Remove(name); // had ⇒ prev non-null
        }

        // Innermost first, so nested shadows of one name unwind in the order they were taken.
        for (int i = shadowed.Count - 1; i >= 0; i--)
        {
            var (name, type, depth, hadDepth) = shadowed[i];
            if (type != null) _varTypes[name] = type; else _varTypes.Remove(name);
            if (hadDepth) _varRabbitDepth[name] = depth; else _varRabbitDepth.Remove(name);
        }
    }

    // Voidable narrowings implied by the negation of a guard condition (fall-through path):
    //   `x is void`   → x non-void → (x, inner);   `A or B` (¬ = ¬A ∧ ¬B) → collect from each.
    // `and` is not recursed: ¬(A ∧ B) narrows neither side. Only voidable narrowing is emitted —
    // that's all the compiler's `.val` access mechanism supports (and all the docs' idioms need).
    private IEnumerable<(string Name, CufetType Inner, string Access)> GuardNarrowings(IExpression cond)
    {
        if (cond is BinaryExpression { Op: TokenType.Or } orE)
        {
            foreach (var g in GuardNarrowings(orE.Left))  yield return g;
            foreach (var g in GuardNarrowings(orE.Right)) yield return g;
            yield break;
        }
        if (cond is BinaryExpression { Op: TokenType.Equal } b)
        {
            var varSide = b.Left is VoidLiteral ? b.Right : b.Left;
            var other   = b.Left is VoidLiteral ? b.Left  : b.Right;
            if (other is VoidLiteral && varSide is VariableReference vr && TypeOf(vr) is VoidableType vt)
                yield return (vr.Name, vt.Inner, ".val");
        }
    }

    // Every path through `body` ends at a return. Mirrors the type checker's DefinitelyReturns
    // (loops don't count — they may run zero times). Used to recognize exiting guards.
    private static bool BlockAlwaysExits(IReadOnlyList<IStatement> body)
    {
        foreach (var s in body)
        {
            if (s is ReturnStatement) return true;
            if (s is IfStatement { ElseBody: not null } ifs
                && ifs.Arms.All(a => BlockAlwaysExits(a.Body)) && BlockAlwaysExits(ifs.ElseBody))
                return true;
        }
        return false;
    }

    private void EmitStatement(StringBuilder sb, IStatement stmt, string indent)
    {
        // ★ A safety valve, not a refusal. StashTransform rewrites every burying function into an
        // ordinary object with a `next` method before either backend sees it, so there is nothing
        // here to lower. Reaching this means a caller generated from the PRE-transform program —
        // `Check` returns the rewritten one; use its return value.
        if (stmt is BuryStatement buried)
            throw new CompilerException(
                $"Internal: a 'bury' on line {buried.Line} reached the code generator untransformed. "
                + "Generate from the program returned by TypeChecker.Check.");

        switch (stmt)
        {
            case StateStatement s:
            {
                string valExpr = EmitExpr(s.Value);
                FlushPreEmits(sb, indent);
                var t = TypeOf(s.Value);
                // ★★ ONE switch, not two. This was a per-type switch of its own that duplicated
                // WriteCall arm for arm, and it drifted twice — `AddressType` and `UnionType` were
                // each patched back by routing them here after the fact, the first of them caught
                // only by the oracle. Every `cufet_print_X` it used to call is defined as
                // `cufet_write_X(v); cufet_nl();`, so the duplication bought nothing and cost a
                // whole class of divergence.
                //
                // ★ Collapsing it also closes what the drift was still hiding: WriteCall knows how
                // to print a `function` and an `axiom` and this switch did not, so `State` on either
                // refused to compile while the interpreter printed `<function>` / `<axiom>`. Two
                // backends, two answers, and the fix was deleting a switch rather than adding arms.
                string printStmt = $"{WriteCall(valExpr, t)}; cufet_nl()";
                // Locked for the whole statement: a State is several writes, and a concurrent State
                // on another thread used to splice itself between them. See cufet_out_lock.
                sb.AppendLine($"{indent}cufet_out_lock(); {printStmt}; cufet_out_unlock();");
                break;
            }

            // An axiom binding stores nothing. Foreign source has no runtime representation here:
            // the text is pasted into this file's C where it is run, and the name exists so the
            // run has something to say. See EmitAxiomCall.
            //
            // ★ `Define alias as answer.` binds one axiom name to another and is the same
            // COMPILE-TIME alias — the checker follows the chain to the literal, so both names
            // reach the same wrapper and neither needs a value at run time. Keyed on the TYPE
            // rather than on the value being a literal, which is what makes the second form work:
            // its value is a VariableReference, and emitting it produced `CufetDec cv_alias =
            // cv_answer;` against a `cv_answer` that never existed — checked clean, would not
            // build, which is the divergence the axiom guard used to prevent by refusing the
            // program outright.
            // ⚠ The binding is RECORDED even though nothing is emitted. `TypeOf` falls back to
            // `number` for a name it does not know, so without this `Define alias as answer.`
            // re-derived `answer` as a number and emitted `CufetDec cv_alias = cv_answer;` —
            // against a `cv_answer` that never existed, because an axiom emits no value.
            case DefineStatement { Value: AxiomLiteral lit } axd:
                _varTypes[axd.Name] = new AxiomType(lit.Language ?? "", lit.ReturnType,
                                                    [.. lit.Parameters.Select(p => p.Type)]);
                // ★ The literal is kept as well as the type, because an axiom used as a VALUE has
                // to be turned into a thunk, and a thunk needs the SOURCE. The binding still emits
                // nothing: the value is built where it is used, not stored here.
                _axiomLiterals[axd.Name] = lit;
                break;
            case DefineStatement axiomAlias when TypeOf(axiomAlias.Value) is AxiomType aliased:
                _varTypes[axiomAlias.Name] = aliased;
                // `Define alias as answer.` — both names reach the same source, and the alias needs
                // it for exactly the same reason the original does.
                if (axiomAlias.Value is VariableReference { Name: var aliasOf }
                    && _axiomLiterals.TryGetValue(aliasOf, out var aliasedLiteral))
                    _axiomLiterals[axiomAlias.Name] = aliasedLiteral;
                break;

            case DefineStatement d:
            {
                // An explicit annotation is the binding's type, so the value widens into it —
                // the same coercion `becomes` and `return` already do.
                var vt = d.DeclaredType ?? TypeOf(d.Value);
                string valExpr = d.DeclaredType != null ? EmitAsType(d.Value, d.DeclaredType) : EmitExpr(d.Value);
                FlushPreEmits(sb, indent);
                _varTypes[d.Name] = vt;
                _varRabbitDepth[d.Name] = _rabbitDepth;   // ESC.4 — declaring depth for capture-escape
                // 'permanently' fixes the binding — const on the value's C type. Series/maps
                // are arena pointers; leave those non-const (const applies to value types).
                bool constable = vt is NumberType or FactType or TextType or RecordType;
                string decl = (d.Permanent && constable) ? "const " + EmitCType(vt) : EmitCType(vt);

                // ★ A shared constant lives at FILE scope, because a top-level function may read
                // it and a local in main is invisible to one. Declared there and ASSIGNED here,
                // rather than initialised in place: a Cufet initialiser is not a C constant
                // expression (a number is built by cufet_dec_lit), so a static initialiser would
                // not compile. Assigning at the top of main is safe because nothing can call a
                // function before main starts.
                if (_sharedConstants.Contains(d))
                {
                    _sharedConstDecls.Add($"static {EmitCType(vt)} {MangleName(d.Name)};");
                    sb.AppendLine($"{indent}{MangleName(d.Name)} = {valExpr};");
                    break;
                }

                sb.AppendLine($"{indent}{decl} {MangleName(d.Name)} = {valExpr};");
                // UNMK: register the unmaker for a Define'd unmakeable object — ONLY inside a block
                // scope (matching the interpreter's _scopeDefOrder appending only on Define + firing
                // only at block ExitScope; a top-level or frame-level Define never fires). Fires LIFO
                // at the enclosing block's exit; value-copies register independently (double-fire).
                if (UsesUnmakers && _scopeDepth > 0 && vt is ObjectType uot && _unmakeDefs.ContainsKey(uot.Name))
                    sb.AppendLine($"{indent}cufet_reg_unmaker(&{MangleName(d.Name)}, {UnmakerCName(uot.Name)});");
                // ⚠ `and free it with <name>` is NOT registered here. It rides the same registry
                // an unmaker does, but it is registered at the ACQUISITION — see EmitForeignAddress
                // for why anchoring it to the binding leaked, and why the binding is the more
                // dangerous of the two places.
                break;
            }

            case BecomesStatement b:
            {
                // Coerce so `x becomes 5` / `x becomes void` widens into x's voidable type.
                _varTypes.TryGetValue(b.Name, out var targetType);
                // ESC.4: a closure literal on the RHS handles its own capture-escape at creation (its
                // env is opaque afterwards). Thread the depth in, and don't re-copy the built cfn.
                bool rhsClosure = b.Value is LambdaLiteral;
                if (rhsClosure) _closureEscapeDepth = b.EscapeToDepth;
                string valExpr = EmitAsType(b.Value, targetType);
                _closureEscapeDepth = null;
                // ESC.2: if the checker flagged this store as escaping (the value belongs to a
                // shorter-lived rabbit than the destination), deep-copy it into the destination's arena.
                if (!rhsClosure)
                    valExpr = EmitEscapeCopy(valExpr, targetType ?? TypeOf(b.Value), b.EscapeToDepth);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}{MangleName(b.Name)} = {valExpr};");
                _narrowedVars.Remove(b.Name);   // reassignment clears any active narrowing
                _armCases.Remove(b.Name);       // …and any arm-restricted case set with it
                break;
            }

            case ReturnStatement ret:
                if (_currentTaskReturn is { } tctx)
                {
                    // Inside a named task's thread function: a return heap-bridges the value across the
                    // task→awaiter boundary. The value is DEEP-COPIED to a malloc'd envelope (heapenv,
                    // via the channel-of-T copy-family) BEFORE the task's arena is torn down, so the
                    // returned envelope is self-contained for any T (POD results copy identically — the
                    // envelope is just malloc + a struct copy, matching CONC.C's scalar path exactly).
                    if (ret.Value != null && tctx.ResultCType != null && tctx.ResultType != null)
                    {
                        string retExpr = EmitAsType(ret.Value, _currentReturnType);
                        FlushPreEmits(sb, indent);
                        int rid = _freshId++;
                        sb.AppendLine($"{indent}void* cf_tret{rid} = {ChanHeapEnv(tctx.ResultType)}({retExpr});");
                        // PUBLISHED to the result box rather than returned. The thread's return value
                        // is no longer how a result travels: awaiters read the box, and pthread_join
                        // happens once at the rabbit's Done.
                        //
                        // ★ CLEAN UP FIRST, THEN PUBLISH. Publishing RELEASES THE AWAITER, and an
                        // unmaker is user code — it can print, write a file, close a handle. This
                        // used to publish first, on the reasoning that the envelope is
                        // self-contained heap so the arena pops could not hurt it. True as far as
                        // memory goes, and beside the point: the awaiting thread woke and ran
                        // alongside this thread's unmakers. Measured over 200 runs of a task whose
                        // unmaker prints — 185 in the interpreter's order, 1 reversed, and 14 with
                        // the two lines TORN INTO EACH OTHER (both texts, then both newlines).
                        //
                        // `the awaited result of` has to mean the task is finished, cleanup and all.
                        // ESC.3: the result is already on the heap, so rabbits this return jumps out
                        // of are genuinely reclaimed before the task's own arena goes — and the
                        // envelope outlives every pop below. Publish must still precede free(cf_a),
                        // which owns the box pointer being published to.
                        sb.AppendLine($"{indent}{UnwindTo(FrameExit)}cufet_arena_pop();");
                        sb.AppendLine($"{indent}cufet_rbox_publish(cf_a->cf_selfbox, cf_tret{rid});");
                        sb.AppendLine($"{indent}free(cf_a); return NULL;");
                    }
                    else
                    {
                        // A bare `return.` (or a value dropped by a fire-and-forget task): no result.
                        sb.AppendLine($"{indent}{UnwindTo(FrameExit)}cufet_arena_pop(); free(cf_a); return NULL;");
                    }
                    break;
                }
                if (ret.Value == null)
                    sb.AppendLine($"{indent}{UnwindTo(FrameExit)}return;");
                else
                {
                    // Coerce so `return <T>` / `return void` widens into a voidable return type.
                    // Value is materialized first (preemits), THEN open files close (a returned
                    // arena value never references a FILE*), THEN return.
                    string retExpr = ret.RunsAxiom is { } runsAxiom
                        ? EmitAxiomCall(runsAxiom, null, ret.Line)
                        : EmitAsType(ret.Value, _currentReturnType);
                    FlushPreEmits(sb, indent);
                    // ESC.3 — returning out of one or more rabbits. Bind the value to a temp FIRST so
                    // it is fully materialized before any region is unwound (the return expression
                    // would otherwise be evaluated after the cleanup prefix). A value that can point
                    // into those regions merges them outward (no copy, so aliasing is preserved);
                    // anything else — a number, a fact — reclaims them outright.
                    var retType = _currentReturnType ?? TypeOf(ret.Value);
                    bool mergeArenas = ReturnCarriesArenaData(retType);
                    // The temp is needed exactly when regions will actually be unwound here — the
                    // same test as before the unwind was folded into UnwindTo, asked of the same
                    // string rather than of a bool that only says which KIND of unwinding it is.
                    if (ArenaPopStmts(0, mergeArenas).Length > 0)
                    {
                        int rvid = _freshId++;
                        sb.AppendLine($"{indent}{EmitCType(retType)} cf_rv{rvid} = {retExpr};");
                        retExpr = $"cf_rv{rvid}";
                    }
                    sb.AppendLine($"{indent}{UnwindTo(FrameExit, mergeArenas)}return {retExpr};");
                }
                break;

            case CastStatement cs:
            {
                // Void free-function call, void method dispatch, or an axiom called for its EFFECT
                // (statement position). The axiom's value is emitted and then dropped, which is
                // what a statement means — and `(void)` says so to a C compiler that would
                // otherwise warn about an unused result.
                string call = cs.RunsAxiom is { } effectAxiom
                    ? "(void)" + EmitAxiomCall(effectAxiom, cs.Args, cs.Line)
                    : cs.RunsAxiomValue
                    ? "(void)" + EmitIndirectCall(cs.Function, cs.Args)
                    : EmitCall(CalledFunction(cs.Function, cs.ResolvedFunctionName, cs.Line, cs.Column), cs.Args);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}{call};");
                break;
            }

            case CurrentDirectorySetStatement cd:
                EmitCurrentDirectorySet(sb, cd, indent);
                break;
            case FileWriteStatement fw:
                EmitFileWrite(sb, fw, indent);
                break;

            case WithOpenStatement wos:
                EmitWithOpen(sb, wos, indent);
                break;

            case PipeExpression pipeStmt when !FlattenPipeAll(pipeStmt).TrueForAll(s => s is RunExpression):
                // A TASK pipe (function stages connected by channels) — distinct from a subprocess
                // pipe. Spawns each stage as a thread streaming through channels (CONC.D).
                EmitTaskPipe(sb, FlattenPipeAll(pipeStmt), indent);
                break;

            case PipeExpression pipeStmt:
            {
                // Bare `run X | run Y.` statement — run the pipeline, write its final stdout to
                // stdout and aggregated stderr to stderr (the shell pattern). A launch failure
                // routes to the enclosing Try, or aborts.
                string raw = EmitRunRaw(pipeStmt);
                FlushPreEmits(sb, indent);
                string cr = RegisterRecordStruct(RunResultRecordType);
                string fOut = MangleName("output"), fErr = MangleName("errors");
                if (_currentTryHandler is { } h)
                    sb.AppendLine($"{indent}if ({raw}.is_failure) {{ {FailureGotoBody(h, $"{raw}.message", $"{raw}.category")} }}");
                else
                    sb.AppendLine($"{indent}if ({raw}.is_failure) {{ fprintf(stderr, \"%s\\n\", {raw}.message); exit(1); }}");
                sb.AppendLine($"{indent}fputs({raw}.val.{fErr}, stderr); fputs({raw}.val.{fOut}, stdout);");
                break;
            }

            case RunStatement runStmt:
            {
                // ★ Launched for its effect: the child inherits stdio, so nothing is captured and
                // there is no record to build. Only the launch can fail — a nonzero exit is an
                // ordinary outcome the statement form simply does not report.
                _usesProcess = true;
                int rid = _freshId++;
                string prog = $"cf_rp{rid}";
                _preEmits.Add($"const char* {prog} = {EmitExpr(runStmt.Program)};");
                EmitRunArgv(prog, runStmt.Args, runStmt.ArgsSeries, $"cf_ra{rid}");
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}{{ CufetFailure cf_re{rid};");
                sb.AppendLine($"{indent}  if (!cufet_run_inherit({prog}, cf_ra{rid}, &cf_re{rid})) {{");
                if (_currentTryHandler is { } rh)
                    sb.AppendLine($"{indent}    {FailureGotoBody(rh, $"cf_re{rid}.message", $"cf_re{rid}.category")}");
                else
                    sb.AppendLine($"{indent}    fprintf(stderr, \"%s\n\", cf_re{rid}.message); exit(1);");
                sb.AppendLine($"{indent}  }} }}");
                break;
            }

            case WriteToStreamStatement wts:
            {
                // write <text> to <stream> — incremental, no newline added (fputs); flushed at close.
                string v = EmitExpr(wts.Value);
                FlushPreEmits(sb, indent);
                string strm = EmitExpr(wts.Stream);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}fputs({v}, {strm});");
                break;
            }

            case OutputStatement os:
            {
                // `output <v>` inside a pipe stage → deep-copy `v` to a heap envelope and send it into
                // the stage's (thread-local) output channel — the same A+B heap bridge, for any T.
                var oe = TypeOf(os.Value) ?? TNumber;
                RegisterChanElem(oe, isTop: true);
                _usesConcurrency = true;
                string v = EmitExpr(os.Value);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}cufet_chan_send(cufet_pipe_out, {ChanHeapEnv(oe)}({v}));");
                break;
            }

            case ForEachFromInputStatement fi:
                EmitForEachFromInput(sb, fi, indent);
                break;

            case BindStatement nb:
            {
                // A nested Bind (inside a function/lambda body) is a NAMED local closure — desugar to
                // `name := <closure value>` and store it in a local so calls dispatch indirectly.
                // (Top-level binds are emitted separately and skipped by the main loop; this case only
                // fires for nested ones.) Recursion (the body calling its own name) throws in CL.2.
                var ft = new FunctionType(nb.Parameters.Select(p => p.Type).ToList(), nb.ReturnType);
                _varTypes[nb.Name] = ft;   // so a later `cast name on (…)` resolves as an indirect call
                string val = EmitClosure(nb.Parameters, nb.Body, nb.Line, nb.Name);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}{EmitCType(ft)} {MangleName(nb.Name)} = {val};");
                break;
            }

            case PullRabbitStatement prs:
            {
                // A rabbit is an arena scope AND the structured-concurrency boundary: it tracks
                // the pthreads + channels created inside it, joins all tasks and frees all channels
                // at Done. (before arena_pop) — so tasks provably can't outlive their rabbit.
                var inner = indent + "    ";
                string n = (_taskCounter++).ToString();
                sb.AppendLine($"{indent}cufet_arena_push();");
                sb.AppendLine($"{indent}{{");
                if (_usesConcurrency)
                {
                    sb.AppendLine($"{inner}pthread_t cf_thr{n}[CUFET_TASK_MAX]; int cf_nthr{n} = 0;");
                    sb.AppendLine($"{inner}cufet_chan* cf_chan{n}[CUFET_TASK_MAX]; int cf_nchan{n} = 0;");
                    // cf_rbox[k] = slot k's result box (named tasks; NULL for fire-and-forget). The box
                    // owns the result envelope and knows how to free it deeply, so the teardown below
                    // reclaims a never-awaited result the same way an awaited one is reclaimed.
                    //
                    // There is no longer a cf_jflag: with awaits reading boxes instead of joining,
                    // the teardown joins EVERY task unconditionally, which is both simpler and the
                    // only thing that stays correct once several tasks may await the same task.
                    sb.AppendLine($"{inner}cufet_rbox* cf_rbox{n}[CUFET_TASK_MAX] = {{0}};");
                    sb.AppendLine($"{inner}(void)cf_thr{n}; (void)cf_chan{n}; (void)cf_rbox{n};");
                    _rabbitCtx.Add(n);
                }
                // ★ A NAMED rabbit is a value in its block, matching the interpreter, which binds a
                // `RabbitValue` under the same name. Without this the name existed to the checker
                // and to nothing else, so `State den.` or `given (the rabbit r)` type-checked, ran
                // interpreted, and had no C to emit.
                CufetType? savedRabbitVar = null;
                bool hadRabbitVar = false;
                if (prs.Name is { } rabbitName)
                {
                    // ★ A rabbit is an ordinary OBJECT now (`Prelude/rabbit.cufe`), so its C is the
                    // struct the prelude's definition already makes the emitter declare, and it
                    // needs no type of its own here. That is what lets a rabbit reach an interface
                    // parameter: monomorphization specialises on a concrete object type, and a
                    // marker type was not one.
                    var rabbitType = ObjType(TypeChecker.RabbitModuleName);
                    sb.AppendLine($"{inner}{EmitCType(rabbitType)} {MangleName(rabbitName)} = {{0}};");
                    sb.AppendLine($"{inner}(void){MangleName(rabbitName)};");
                    hadRabbitVar = _varTypes.TryGetValue(rabbitName, out savedRabbitVar);
                    _varTypes[rabbitName] = rabbitType;
                }

                _rabbitDepth++;   // this rabbit pops its arena at Done. (independent of concurrency) —
                EmitScopedBlock(sb, prs.Body, inner);   // so a region-capturing closure created here can dangle
                _rabbitDepth--;

                if (prs.Name is { } boundRabbit)
                {
                    if (hadRabbitVar) _varTypes[boundRabbit] = savedRabbitVar!;
                    else _varTypes.Remove(boundRabbit);
                }
                if (_usesConcurrency)
                {
                    _rabbitCtx.RemoveAt(_rabbitCtx.Count - 1);
                    // Structured join: reap EVERY task — no exceptions now, because no await joins.
                    // Then free each result box, which frees the envelope through its own freeenv, so
                    // a reference-typed result's nested heap goes too whether it was awaited N times
                    // or never. Freeing after the join is what makes it safe: no task can still be
                    // publishing, and no awaiter can still be reading, once every thread has been
                    // reaped and this is the only thread left in the rabbit.
                    sb.AppendLine($"{inner}for (int cf_ji = 0; cf_ji < cf_nthr{n}; cf_ji++) pthread_join(cf_thr{n}[cf_ji], NULL);");
                    sb.AppendLine($"{inner}for (int cf_bi = 0; cf_bi < cf_nthr{n}; cf_bi++) cufet_rbox_free(cf_rbox{n}[cf_bi]);");
                    sb.AppendLine($"{inner}for (int cf_ci = 0; cf_ci < cf_nchan{n}; cf_ci++) cufet_chan_free_if_live(cf_chan{n}[cf_ci]);");
                    // INT.1 — the join above is the one place this thread parks for an unbounded
                    // time, and pthread_join is not a checkpoint. An interrupt delivered while
                    // tasks were running unwinds THEM (their own pads), but this thread would sail
                    // on with the flag still set and never tear down. Check as soon as the join
                    // releases, so Ctrl-C during a task actually ends the program.
                    sb.AppendLine($"{inner}cufet_checkpoint();");
                }
                sb.AppendLine($"{indent}}}");
                sb.AppendLine($"{indent}cufet_arena_pop();");
                break;
            }

            case SendStatement snd:
            {
                // `Send v through ch` → deep-copy v to a heap envelope + enqueue it (the A+B bridge,
                // for any T). The element type is the channel's declared element type (authoritative).
                var se = (TypeOf(snd.Channel) as ChannelType)?.ElementType ?? TNumber;
                RegisterChanElem(se, isTop: true);
                _usesConcurrency = true;
                string val = EmitAsType(snd.Value, se);
                FlushPreEmits(sb, indent);
                string ch = EmitExpr(snd.Channel);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}cufet_chan_send({ch}, {ChanHeapEnv(se)}({val}));");
                break;
            }

            case CloseStatement cls:
            {
                _usesConcurrency = true;
                string ch = EmitExpr(cls.Channel);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}cufet_chan_close({ch});");
                break;
            }

            case LaunchTaskStatement lts:
                EmitLaunchTask(sb, lts, indent);
                break;

            case YieldStatement:
                // `Yield.` — a cooperative interrupt checkpoint (CONC.E). In the interpreter it also
                // hands the scheduler a turn; natively the OS scheduler does that, so this is purely
                // the interrupt check: if a SIGINT is pending, unwind to this thread's landing pad.
                _usesSignals = true;
                sb.AppendLine($"{indent}cufet_checkpoint();");
                break;

            case AcknowledgeInterruptStatement:
                // `Acknowledge the interrupt.` — clear the flag, so a cooperatively-handled interrupt
                // does not later unwind at a checkpoint (mirrors the interpreter's _interruptRequested=false).
                _usesSignals = true;
                sb.AppendLine($"{indent}cufet_interrupted = 0;");
                break;

            case SeedChanceStatement ss:
            {
                // `Seed the chance with N.` — reseed the PRNG (seed truncated to integer, like the
                // interpreter's (int)(decimal) cast). Guarantee: self-consistent WITHIN this backend.
                _usesChance = true;
                string seed = EmitExpr(ss.Seed);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}cufet_rng_seed(cufet_to_int({seed}));");
                break;
            }

            case PullStatement ps:
            {
                // `Pull a book on <name>. … Done.` — books are BUILTIN + compile-time-resolved, so this
                // is purely a scope: register each alias (localName → book) for member-dispatch routing,
                // emit the body in a C block (scopes body-locals like the interpreter's EnterScope), then
                // unregister. No arena push (books allocate nothing), no runtime book value, no linking.
                var added = new List<string>();
                sb.AppendLine($"{indent}{{");
                foreach (var (bookName, localName) in ps.Books)
                {
                    // ★ A MODULE — an object type the writer defined — rather than a bundled book.
                    // Pulling INSTANTIATES it and binds the name, after which `name's member` is
                    // ordinary method dispatch and needs nothing from the book routing below. The
                    // three builtins are not a category; they are the three that ship.
                    //
                    // A bundled book with a Cufet LAYER is both at once: the layer's object is
                    // instantiated and bound, AND the book alias is registered, so per member the
                    // cast routing picks whichever side defines it.
                    if (_objectDefs.TryGetValue(bookName, out var moduleDef))
                    {
                        var moduleType = ObjType(moduleDef.Name);
                        _varTypes[localName] = moduleType;
                        sb.AppendLine($"{indent}    {EmitCType(moduleType)} {MangleName(localName)} = "
                                    + $"({EmitCType(moduleType)}){{0}};");
                        if (!BuiltinBookNames.Contains(bookName)) continue;
                    }
                    _bookAliases[localName] = bookName.ToLowerInvariant();
                    added.Add(localName);
                }
                // Binds in the body were HOISTED to free functions at Generate time — skip them here.
                EmitScopedBlock(sb, ps.Body.Where(s => s is not BindStatement).ToList(), indent + "    ");
                sb.AppendLine($"{indent}}}");
                foreach (var l in added) _bookAliases.Remove(l);
                break;
            }

            case SeriesInsertStatement sa:
            {
                // ★ A chase takes the characters of a text, however many. Before SeriesStructOf,
                // which would refuse a target that is not a series.
                if (TypeOf(sa.Series) is ChaseType)
                {
                    _usesChase = true;
                    string chaseVal = EmitExpr(sa.Value);
                    FlushPreEmits(sb, indent);
                    string chaseTarget = EmitExpr(sa.Series);
                    FlushPreEmits(sb, indent);
                    sb.AppendLine($"{indent}cufet_chase_append({chaseTarget}, {chaseVal});");
                    break;
                }
                string ser = SeriesStructOf(sa.Series);
                // Coerce into the series' ELEMENT type so adding to a catalogue widens into the union.
                var saElem = (TypeOf(sa.Series) as SeriesType)?.ElementType;
                string valExpr = EmitAsType(sa.Value, saElem);
                valExpr = EmitEscapeCopy(valExpr, saElem, sa.EscapeToDepth);   // ESC.2
                FlushPreEmits(sb, indent);
                string serExpr = EmitExpr(sa.Series);
                FlushPreEmits(sb, indent);
                if (sa.ToStart)
                    EmitStoreWithEscape(sb, indent, sa.EscapeToDepth, $"{ser}_prepend({serExpr}, {valExpr});");
                else if (sa.AfterIndex == null)
                    EmitStoreWithEscape(sb, indent, sa.EscapeToDepth, $"{ser}_append({serExpr}, {valExpr});");
                else
                {
                    string idxExpr = EmitExpr(sa.AfterIndex);
                    FlushPreEmits(sb, indent);
                    EmitStoreWithEscape(sb, indent, sa.EscapeToDepth, $"{ser}_insert({serExpr}, cufet_to_int({idxExpr}), {valExpr});");
                }
                break;
            }

            case SeriesRemoveAtStatement sra when TypeOf(sra.Series) is ChaseType:
            {
                _usesChase = true;
                string chSer = EmitExpr(sra.Series);
                FlushPreEmits(sb, indent);
                string chNm = SeriesDisplayName(sra.Series);
                string chIdx = sra.Index == null ? "-1" : $"cufet_to_int({EmitExpr(sra.Index)})";
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}cufet_chase_remove_at({chSer}, {chIdx}, \"{chNm}\", {sra.Line});");
                break;
            }

            case SeriesRemoveAtStatement sra:
            {
                string ser = SeriesStructOf(sra.Series);
                string serExpr = EmitExpr(sra.Series);
                FlushPreEmits(sb, indent);
                if (sra.Index == null)
                    sb.AppendLine($"{indent}{ser}_remove_at({serExpr}, -1);");
                else
                {
                    string idxExpr = EmitExpr(sra.Index);
                    FlushPreEmits(sb, indent);
                    sb.AppendLine($"{indent}{ser}_remove_at({serExpr}, cufet_to_int({idxExpr}));");
                }
                break;
            }

            case SeriesRemoveValueStatement srv:
            {
                string ser = SeriesStructOf(srv.Series);
                string valExpr = EmitAsType(srv.Value, (TypeOf(srv.Series) as SeriesType)?.ElementType);
                FlushPreEmits(sb, indent);
                string serExpr = EmitExpr(srv.Series);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}{ser}_remove_value({serExpr}, {valExpr});");
                break;
            }

            case MatrixSetStatement ms:
            {
                _usesMatrix = true;
                // Matrix, then row, then column, then value — the interpreter's evaluation order,
                // so a program whose indices have side effects sequences the same on both.
                string matExpr = EmitExpr(ms.Matrix);
                string rowExpr = EmitExpr(ms.Row);
                string colExpr = EmitExpr(ms.Col);
                string valExpr = EmitExpr(ms.Value);
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}cufet_mat_set({matExpr}, cufet_to_int({rowExpr}), " +
                              $"cufet_to_int({colExpr}), {valExpr}, {ms.Line});");
                break;
            }

            case SeriesSetStatement ss when TypeOf(ss.Series) is RecordType or ObjectType:
            {
                // Positional field assignment on a record/object: the Nth of x becomes v.
                string baseExpr = EmitExpr(ss.Series);
                string valExpr  = EmitExpr(ss.Value);
                FlushPreEmits(sb, indent);
                int idx0 = TypeOf(ss.Series) is ObjectType sot
                    ? ObjectPositionalIndex(sot.Name, ss.Index)
                    : LiteralIndex(ss.Index) - 1;
                sb.AppendLine($"{indent}({baseExpr}).p{idx0} = {valExpr};");
                break;
            }

            case SeriesSetStatement ss when TypeOf(ss.Series) is ChaseType:
            {
                _usesChase = true;
                string chSer = EmitExpr(ss.Series);
                FlushPreEmits(sb, indent);
                string chVal = EmitExpr(ss.Value);
                FlushPreEmits(sb, indent);
                string chNm = SeriesDisplayName(ss.Series);
                string chWhere = ss.Index == null
                    ? $"cufet_last_check({chSer}->len, \"{chNm}\", {ss.Line})"
                    : $"cufet_idx_check(cufet_to_int({EmitExpr(ss.Index)}), {chSer}->len, \"{chNm}\", {ss.Line})";
                FlushPreEmits(sb, indent);
                sb.AppendLine($"{indent}cufet_chase_set({chSer}, (int){chWhere}, {chVal}, {ss.Line});");
                break;
            }

            case SeriesSetStatement ss:
            {
                string serExpr = EmitExpr(ss.Series);
                FlushPreEmits(sb, indent);
                string valExpr = EmitExpr(ss.Value);
                FlushPreEmits(sb, indent);
                string setNm = SeriesDisplayName(ss.Series);
                if (ss.Index == null)
                    sb.AppendLine($"{indent}{serExpr}->data[cufet_last_check({serExpr}->len, \"{setNm}\", {ss.Line}) - 1] = {valExpr};");
                else
                {
                    string idxExpr = EmitExpr(ss.Index);
                    FlushPreEmits(sb, indent);
                    sb.AppendLine($"{indent}{serExpr}->data[cufet_idx_check(cufet_to_int({idxExpr}), {serExpr}->len, \"{setNm}\", {ss.Line}) - 1] = {valExpr};");
                }
                break;
            }

            case RecordNamedSetStatement rns:
                // the <field> of <record/object> becomes <value> — routes through a setter
                // if the member has one, else a raw in-place field write (value semantics).
                EmitMemberSet(sb, indent, rns.Record, rns.FieldName, rns.Value, rns.EscapeToDepth);
                break;

            case PossessiveSetStatement pss:
                // alice's age becomes 31 / one's age becomes 31 — same, setter-aware.
                EmitMemberSet(sb, indent, pss.Target, pss.Member, pss.Value, pss.EscapeToDepth);
                break;

            case MapSetStatement mss:
            {
                // In m, the entry for k becomes v — scan-update-or-append (cmap put).
                string name    = MapName(mss.Map);
                var    valType = ((MapType)TypeOf(mss.Map)).ValueType;
                string mapExpr = EmitExpr(mss.Map);
                var    keyType = ((MapType)TypeOf(mss.Map)).KeyType;
                string keyExpr = EmitEscapeCopy(EmitExpr(mss.Key), keyType, mss.KeyEscapeToDepth);   // ESC.3
                string valExpr = EmitEscapeCopy(EmitAsType(mss.Value, valType), valType, mss.EscapeToDepth);   // ESC.2
                FlushPreEmits(sb, indent);
                EmitStoreWithEscape(sb, indent, mss.EscapeToDepth, $"{name}_put({mapExpr}, {keyExpr}, {valExpr});");
                break;
            }

            case ObjectDefinition:
            case GetterDeclaration:
            case SetterDeclaration:
            case OperatorOverloadDeclaration:
            case UnmakerDeclaration:
            // An interface has NO runtime representation at all — it only constrains which concrete
            // types may reach an interface parameter, and those parameters are monomorphized away.
            case InterfaceDefinition:
                break;   // declarations — structs, methods, getters, setters, overloads, unmakers emitted in the prelude

            case IfStatement ifStmt:
                EmitIf(sb, ifStmt, indent);
                break;

            case JudgeStatement judge:
                EmitJudge(sb, judge, indent);
                break;

            case WhileStatement ws:
            {
                // Conditions may PREEMIT (env var, map lookup, delivery, …). A while-head can't hold
                // statements, so restructure to a head-checked for(;;): preemits + check re-run every
                // iteration (Skip's `continue` correctly re-enters at the preemits).
                string wcond = EmitExpr(ws.Condition);
                if (_preEmits.Count > 0)
                {
                    sb.AppendLine($"{indent}for (;;) {{");
                    FlushPreEmits(sb, indent + "    ");
                    sb.AppendLine($"{indent}    if (!({wcond})) break;");
                    EmitLoopBody(sb, ws.Body, indent + "    ");
                    sb.AppendLine($"{indent}}}");
                }
                else
                {
                    sb.AppendLine($"{indent}while ({wcond}) {{");
                    EmitLoopBody(sb, ws.Body, indent + "    ");
                    sb.AppendLine($"{indent}}}");
                }
                break;
            }

            case RepeatUntilStatement ru:
            {
                sb.AppendLine($"{indent}do {{");
                EmitLoopBody(sb, ru.Body, indent + "    ");
                string rcond = EmitExpr(ru.Condition);
                if (_preEmits.Count > 0)
                    // A tail-checked condition can't host preemits without breaking Skip's jump-to-
                    // check semantics — bind the condition's value to a variable inside the loop.
                    throw new CompilerException("this 'until' condition form needs a preliminary step — Define the value inside the loop and test the variable in 'until'.");
                sb.AppendLine($"{indent}}} while (!({rcond}));");
                break;
            }

            case StopStatement:
                sb.AppendLine($"{indent}{UnwindTo(LoopExit)}break;");
                break;

            case SkipStatement:
                sb.AppendLine($"{indent}{UnwindTo(LoopExit)}continue;");
                break;

            case TryStatement ts:
                EmitTryStatement(sb, ts, indent);
                break;

            case SuppressStatement:
                // `Suppress the exception.` — mark suppressed and exit the handler immediately
                // (the interpreter's SuppressSignal unwinds the rest of the handler block too).
                if (_currentExcHandler is not { } eh)
                    throw new CompilerException("'Suppress' is only valid inside an 'In case of exception' handler.");
                // ESC.3: Suppress can sit inside a rabbit opened within the handler — the jump to the
                // handler's end must release those regions, exactly like Stop out of a loop.
                //
                // ⚠ "Exactly like Stop out of a loop" is FOUR things, not one. This released arenas
                // only, so a destructor on an object made inside the handler never ran, a file
                // opened there was never closed, and a pad pushed there was never popped. The
                // destructor case was a live divergence: the interpreter unwinds the handler block
                // and runs it. Same quartet, same order, as every other nonlocal exit.
                sb.AppendLine($"{indent}{eh.SupVar} = 1; " +
                              $"{UnwindTo(eh.Exit)}goto {eh.DoneLabel};");
                break;

            case ForEachStatement fe when fe.Series is RangeExpression range:
                EmitForEachRange(sb, fe, range, indent);
                break;

            case ForEachStatement fe when TypeOf(fe.Series) is MapType:
                EmitForEachMap(sb, fe, indent);
                break;

            case ForEachStatement fe when TypeOf(fe.Series) is ChaseType:
                EmitForEachChase(sb, fe, indent);
                break;

            case ForEachStatement fe:
                EmitForEachSeries(sb, fe, indent);
                break;

            default:
                throw new CompilerException(
                    $"'{NodeName(stmt)}' is not yet supported by the compiler.");
        }
    }

    // Judge lowers to a tag dispatch over the subject's union, evaluated ONCE into a C local.
    //
    // ★ That local is literally `cv_it`. Declaring it inside a fresh C block means a nested Judge
    // shadows an outer one exactly the way Cufet's `it` does, with no name generation and no
    // bookkeeping — C's own scoping carries the semantics.
    private void EmitJudge(StringBuilder sb, JudgeStatement judge, string indent)
    {
        var subjType = TypeOf(judge.Subject);
        if (subjType is not UnionType subjUnion || subjUnion.Cases == null)
            throw new CompilerException(
                "a Judge over something that is not a closed union cannot be compiled yet — its " +
                "arms would have to compare values rather than dispatch on a tag. Judge a union, " +
                "or use an 'Otherwise if' chain here.");

        var allCases = UnionCases(subjUnion);
        string subjExpr = EmitExpr(judge.Subject);
        FlushPreEmits(sb, indent);

        string inner = indent + "    ";
        string body  = inner + "    ";
        string itName = MangleName("it");

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{inner}{EmitCType(subjType)} {itName} = {subjExpr};");

        bool hadIt = _varTypes.TryGetValue("it", out var prevIt);
        _varTypes["it"] = subjType;

        // ⚠⚠ `it` is REBOUND here, not narrowed further, and the side tables are keyed by NAME.
        // _narrowedVars COMPOSES accesses — correct for one binding narrowed twice, wrong for a
        // nested Judge, whose inner `it` is a different value living in a different C local. Left
        // in place, an outer arm's `.val.c0` was prefixed onto the inner arm's, emitting
        // `(cv_it).val.c0.val.c0` and reaching for a member of a type that has none. _armCases is
        // the same hazard: an outer grouped arm's surviving cases are not the inner subject's.
        //
        // ★★ The note above says C's own scoping carries the shadowing, and for the emitted local
        // it does. These tables are the part of the shadowing C could not carry, and they are why
        // the defect could only ever appear as a DIVERGENCE — the interpreter shadows properly, so
        // no interpreter test could go red for it.
        bool hadItNarrow = _narrowedVars.TryGetValue("it", out var prevItNarrow);
        _narrowedVars.Remove("it");
        bool hadItCases = _armCases.TryGetValue("it", out var prevItCases);
        _armCases.Remove("it");

        var covered = new HashSet<int>();
        string keyword = "if";

        foreach (var arm in judge.Arms)
        {
            var indices = arm.Cases
                .Select(c => MatchCaseInList(allCases, c))
                .Where(k => k >= 0)
                .ToList();
            foreach (int k in indices) covered.Add(k);

            string test = indices.Count == 0
                ? "0"
                : string.Join(" || ", indices.Select(k => $"(({itName}).tag == {k})"));

            sb.AppendLine($"{inner}{keyword} ({test}) {{");

            // One case ⇒ `it` reads at that case's concrete type. Grouped cases stay the union,
            // matching the checker: an arm covering two cases cannot know which one arrived — but
            // it does know it is one of THOSE, which is what the arm's case set carries so a
            // narrowing inside the arm can finish the job.
            (string, CufetType, string)? narrow = indices.Count == 1
                ? ("it", allCases[indices[0]], $".val.c{indices[0]}")
                : null;
            EmitNarrowedBlock(sb, narrow, arm.Body, body,
                              indices.Count > 1 ? ("it", indices) : null);
            keyword = "} else if";
        }

        if (judge.OtherwiseBody != null)
        {
            sb.AppendLine($"{inner}}} else {{");
            // Same rule the If's else-arm follows: exactly one case left ⇒ read at that case.
            var left = Enumerable.Range(0, allCases.Count).Where(i => !covered.Contains(i)).ToList();
            (string, CufetType, string)? narrow = left.Count == 1
                ? ("it", allCases[left[0]], $".val.c{left[0]}")
                : null;
            EmitNarrowedBlock(sb, narrow, judge.OtherwiseBody, body,
                              left.Count > 1 ? ("it", left) : null);
        }

        sb.AppendLine($"{inner}}}");
        sb.AppendLine($"{indent}}}");

        if (hadIt) _varTypes["it"] = prevIt!; else _varTypes.Remove("it");
        if (hadItNarrow) _narrowedVars["it"] = prevItNarrow; else _narrowedVars.Remove("it");
        if (hadItCases)  _armCases["it"]     = prevItCases!;  else _armCases.Remove("it");
    }

    private void EmitIf(StringBuilder sb, IfStatement ifStmt, string indent)
    {
        var inner = indent + "    ";
        var first = ifStmt.Arms[0];
        // Condition is emitted BEFORE narrowing so `x is not void` reads x's voidable form.
        // The FIRST condition evaluates unconditionally, so its preemits flush before the `if`.
        string cond0 = EmitExpr(first.Condition);
        FlushPreEmits(sb, indent);
        sb.AppendLine($"{indent}if ({cond0}) {{");
        EmitNarrowedBlock(sb, NotVoidNarrow(first.Condition), first.Body, inner,
                          DisjunctionCases(first.Condition));

        for (int i = 1; i < ifStmt.Arms.Count; i++)
        {
            var arm = ifStmt.Arms[i];
            string condN = EmitExpr(arm.Condition);
            if (_preEmits.Count > 0)
                // An else-if condition only evaluates when earlier arms failed — its preemits can't
                // be hoisted (they may be effectful). Bind the value to a variable before the If.
                throw new CompilerException("this condition form in an 'Otherwise if' arm needs a preliminary step — Define the value before the If and test the variable.");
            sb.AppendLine($"{indent}}} else if ({condN}) {{");
            EmitNarrowedBlock(sb, NotVoidNarrow(arm.Condition), arm.Body, inner,
                              DisjunctionCases(arm.Condition));
        }

        if (ifStmt.ElseBody != null)
        {
            sb.AppendLine($"{indent}}} else {{");
            // The else arm runs when every `is a <case>` arm failed — so a closed union narrows to the
            // cases NOT tested. If exactly one remains the front-end narrows it to that concrete case
            // (measured: a 2-case union's else IS text; a 3-case residual `(text or fact)` is rejected
            // for text ops), so match that here.
            EmitNarrowedBlock(sb, ElseNarrow(ifStmt), ifStmt.ElseBody, inner);
        }

        sb.AppendLine($"{indent}}}");
    }

    // Exhaustive else-arm narrowing for a closed union: if every arm tests `<x> is a <case>` on the
    // SAME union variable, the else arm is reached only for the untested cases — and when exactly one
    // remains, x reads at that case's concrete type (mirroring the front-end's residual-union narrowing).
    private (string Name, CufetType Inner, string Access)? ElseNarrow(IfStatement ifStmt)
    {
        // ★ A single NEGATED arm inverts the rule. The else of `x is not a <case>` is reached
        // exactly when x IS that case, so it names the survivor outright rather than eliminating
        // down to one — and it narrows whatever the union's size, where the positive path below
        // needs everything but one case excluded. Matches the checker, which narrows the same else.
        if (ifStmt.Arms.Count == 1
            && ifStmt.Arms[0].Condition is IsTypeCheck { Negated: true, Target: VariableReference nvr } ntc
            && TypeOf(nvr) is UnionType negUnion)
        {
            var negCases = UnionCases(negUnion);
            int negK = MatchCaseInList(negCases, ntc.Type);
            if (negK < 0) return null;
            // Reachability still applies: inside a grouped Judge arm the case may already be out.
            if (_armCases.TryGetValue(nvr.Name, out var negReach) && !negReach.Contains(negK))
                return null;
            return (nvr.Name, negCases[negK], $".val.c{negK}");
        }

        string? name = null; UnionType? ut = null;
        var excluded = new HashSet<int>();
        foreach (var arm in ifStmt.Arms)
        {
            // A GROUPED arm excludes every case it names — it is a single test that removes several,
            // and the checker eliminates through it the same way, so the two must agree here.
            if (DisjunctionGroup(arm.Condition) is { } group)
            {
                if (name == null) { name = group.Name; ut = group.Union; }
                else if (name != group.Name) return null;
                foreach (int gk in group.Cases) excluded.Add(gk);
                continue;
            }
            if (arm.Condition is not IsTypeCheck { Negated: false, Target: VariableReference vr } tc) return null;
            if (TypeOf(vr) is not UnionType u) return null;
            if (name == null) { name = vr.Name; ut = u; }
            else if (name != vr.Name) return null;
            int k = MatchCaseInList(UnionCases(ut!), tc.Type);
            if (k < 0) return null;
            excluded.Add(k);
        }
        if (ut == null || name == null) return null;
        var allCases = UnionCases(ut);
        // Eliminate from what is REACHABLE here, not from the whole union. Inside a Judge's grouped
        // arm those differ, and using the whole union leaves cases the arm already ruled out — which
        // is how an else-arm the checker had narrowed to one concrete case stayed a union here.
        var reachable = _armCases.TryGetValue(name, out var restricted)
            ? restricted
            : Enumerable.Range(0, allCases.Count).ToList();
        var remaining = reachable.Where(i => !excluded.Contains(i)).ToList();
        if (remaining.Count != 1) return null;   // 2+ left ⇒ still a union; the front-end restricts its use
        int j = remaining[0];
        return (name, allCases[j], $".val.c{j}");
    }

    // `x is a A or x is a B or …` on ONE union variable → the set of cases still reachable in the
    // then-branch. The checker narrows the same condition to the sub-union `(A or B)`; here it stays
    // a SET OF INDICES into the representation union, for the reason _armCases exists — a sub-union's
    // own case order need not match the subject's, so substituting a narrower type would make every
    // emitted `.val.c<k>` index the wrong member.
    private (string Name, UnionType Union, List<int> Cases)? DisjunctionGroup(IExpression condition)
    {
        if (condition is not BinaryExpression { Op: TokenType.Or }) return null;

        string? name = null;
        UnionType? ut = null;
        var picked = new List<int>();

        bool Walk(IExpression e)
        {
            if (e is BinaryExpression { Op: TokenType.Or } both)
                return Walk(both.Left) && Walk(both.Right);
            if (e is not IsTypeCheck { Negated: false, Target: VariableReference vr } tc) return false;
            if (TypeOf(vr) is not UnionType u) return false;
            if (name == null) { name = vr.Name; ut = u; }
            else if (name != vr.Name) return false;
            int k = MatchCaseInList(UnionCases(ut!), tc.Type);
            if (k < 0) return false;
            if (!picked.Contains(k)) picked.Add(k);
            return true;
        }

        if (!Walk(condition) || name == null || picked.Count < 2) return null;

        // Intersect with what is REACHABLE here — inside a grouped arm the two differ, and the same
        // source of truth is used by ElseNarrow and NotVoidNarrow.
        if (_armCases.TryGetValue(name, out var restricted))
        {
            picked = picked.Where(restricted.Contains).ToList();
            if (picked.Count == 0) return null;
        }
        picked.Sort();
        return (name, ut!, picked);
    }

    // The same group, shaped for EmitNarrowedBlock's arm-set parameter.
    private (string Name, List<int> Cases)? DisjunctionCases(IExpression condition) =>
        DisjunctionGroup(condition) is { } g ? (g.Name, g.Cases) : null;

    // Emits a block with an optional voidable variable narrowed to its inner type inside it, and
    // an optional restriction on which union cases are still reachable there (see _armCases).
    private void EmitNarrowedBlock(StringBuilder sb, (string Name, CufetType Inner, string Access)? narrow,
                                   IReadOnlyList<IStatement> body, string indent,
                                   (string Name, List<int> Cases)? armCases = null)
    {
        bool hadSet = false;
        List<int>? prevSet = null;
        if (armCases is not null)
        {
            hadSet = _armCases.TryGetValue(armCases.Value.Name, out prevSet);
            _armCases[armCases.Value.Name] = armCases.Value.Cases;
        }

        try
        {
            if (narrow is not var (name, inner, access) || narrow is null) { EmitScopedBlock(sb, body, indent); return; }
            bool had = _narrowedVars.TryGetValue(name, out var prev);
            // Narrowings COMPOSE — a voidable-of-union narrowed by `is not void` then by `is a <case>`
            // reads `.val` (out of the cvd) THEN `.val.c<k>` (out of the cun). Replacing rather than
            // nesting would emit `(x).val.c0` against the cvd and hit a non-existent member.
            _narrowedVars[name] = (inner, (had ? prev.Access : "") + access);
            EmitScopedBlock(sb, body, indent);
            if (had) _narrowedVars[name] = prev!; else _narrowedVars.Remove(name);  // had ⇒ prev non-null
        }
        finally
        {
            if (armCases is not null)
            {
                if (hadSet) _armCases[armCases.Value.Name] = prevSet!; else _armCases.Remove(armCases.Value.Name);
            }
        }
    }

    // `x is not void` (x a voidable variable) → (x, inner); narrows x in the then-branch only.
    // (The interpreter narrows the `is not void` then-branch, not the `is void` else-branch.)
    private (string Name, CufetType Inner, string Access)? NotVoidNarrow(IExpression cond)
    {
        // `x is a <T>` (positive) on a VOIDABLE x whose inner matches T narrows like `is not void`
        // (the typechecker narrows the arm to T, so reads inside must emit `.val`). Static targets
        // need no representation change, so only the voidable case registers.
        // `x is a <case>` on a closed-union x narrows x to that case inside the arm: reads become
        // `.val.c<k>` at the case's concrete type (the N-case generalization of the voidable `.val`).
        if (cond is IsTypeCheck { Negated: false, Target: VariableReference uvr } utc
            && TypeOf(uvr) is UnionType uut)
        {
            var ucases = UnionCases(uut);
            int k = MatchCaseInList(ucases, utc.Type);
            if (k >= 0) return (uvr.Name, ucases[k], $".val.c{k}");
            return null;
        }
        // ★ `x is NOT a <case>` narrows the THEN branch by elimination — the mirror of ElseNarrow,
        // which does the same for the else of a positive test. Without this the arm read x at its
        // full union type: `If thing is not a text: State thing converted to text.` interpreted
        // correctly (the checker DOES narrow it) and would not compile, in ordinary code with no
        // stash in sight. The front end and the back end disagreeing about the same arm is the
        // divergence this closes.
        if (cond is IsTypeCheck { Negated: true, Target: VariableReference nvr } ntc
            && TypeOf(nvr) is UnionType nut)
        {
            var ncases = UnionCases(nut);
            int excluded = MatchCaseInList(ncases, ntc.Type);
            if (excluded < 0) return null;
            // Eliminate from what is REACHABLE here, not from the whole union — inside a grouped
            // Judge arm those differ. Same reasoning as ElseNarrow, and the same source of truth.
            var reachable = _armCases.TryGetValue(nvr.Name, out var restricted)
                ? restricted
                : Enumerable.Range(0, ncases.Count).ToList();
            var remaining = reachable.Where(i => i != excluded).ToList();
            if (remaining.Count != 1) return null;   // 2+ left ⇒ still a union, nothing to reach through
            int j = remaining[0];
            return (nvr.Name, ncases[j], $".val.c{j}");
        }

        if (cond is IsTypeCheck { Negated: false, Target: VariableReference tvr } tc
            && TypeOf(tvr) is VoidableType tvt && StaticKindMatches(tvt.Inner, tc.Type))
            return (tvr.Name, tvt.Inner, ".val");
        if (cond is not BinaryExpression { Op: TokenType.NotEqual } b) return null;
        var (varSide, other) = b.Left is VoidLiteral ? (b.Right, b.Left) : (b.Left, b.Right);
        if (other is VoidLiteral && varSide is VariableReference vr && TypeOf(vr) is VoidableType vt)
            return (vr.Name, vt.Inner, ".val");
        return null;
    }

    // Range semantics mirror the interpreter exactly:
    //   - inclusive both bounds
    //   - ascending when start <= end, descending otherwise
    //   - step is a positive magnitude; direction determined by start/end
    // The loop counter and bounds are decimals so fractional ranges stay exact.
    // cf_ temporaries (not cv_) avoid collision with user-declared variables.
    private void EmitForEachRange(StringBuilder sb, ForEachStatement fe, RangeExpression range, string indent)
    {
        var inner      = indent + "    ";
        var loopIndent = inner  + "    ";
        int id = _forCounter++;
        string s  = $"cf_s{id}";
        string e  = $"cf_e{id}";
        string st = $"cf_st{id}";
        string d  = $"cf_d{id}";
        string iterName = MangleName(fe.IteratorName ?? "it");

        sb.AppendLine($"{indent}{{");
        string startExpr = EmitExpr(range.Start); FlushPreEmits(sb, inner);
        sb.AppendLine($"{inner}CufetDec {s}  = {startExpr};");
        string endExpr = EmitExpr(range.End); FlushPreEmits(sb, inner);
        sb.AppendLine($"{inner}CufetDec {e}  = {endExpr};");
        string stepExpr = range.Step != null ? EmitExpr(range.Step) : "cufet_dec_from_ll(1)";
        FlushPreEmits(sb, inner);
        sb.AppendLine($"{inner}CufetDec {st} = {stepExpr};");
        // Same non-positive-step guard the value form uses — the interpreter raises on both, and
        // without it a zero step spins here forever.
        sb.AppendLine($"{inner}{RangeStepGuard(st, range.Line)}");
        sb.AppendLine($"{inner}int {d}  = cufet_cmp({s}, {e}) <= 0 ? 1 : -1;");
        sb.AppendLine($"{inner}for (CufetDec {iterName} = {s}; {d} > 0 ? cufet_cmp({iterName}, {e}) <= 0 : cufet_cmp({iterName}, {e}) >= 0; {iterName} = {d} > 0 ? cufet_add({iterName}, {st}) : cufet_sub({iterName}, {st})) {{");
        // Track the loop variable's type (a number) so it resolves in the body — and so a task
        // spawned in the body can capture it (consistent with the series/map foreach).
        string raw = fe.IteratorName ?? "it";
        var saved = _varTypes.TryGetValue(raw, out var prev) ? prev : null;
        _varTypes[raw] = TNumber;
        _varRabbitDepth[raw] = _rabbitDepth;   // ESC.4
        EmitLoopBody(sb, fe.Body, loopIndent);
        if (saved != null) _varTypes[raw] = saved; else _varTypes.Remove(raw);
        sb.AppendLine($"{inner}}}");
        sb.AppendLine($"{indent}}}");
    }

    // For each loop over a materialized series (non-range).
    // cf_ temporaries avoid collisions with user variables.
    /// <summary>For each character of a chase — each bound as a one-character text.</summary>
    /// <remarks>
    /// ★ The same shape as the series loop beside it, differing only in the element: `->data` here
    /// is code points, and what the body sees has to be the text a character is spelled as.
    /// </remarks>
    private void EmitForEachChase(StringBuilder sb, ForEachStatement fe, string indent)
    {
        _usesChase = true;
        var inner      = indent + "    ";
        var loopIndent = inner  + "    ";
        int id = _forCounter++;
        string buf = $"cf_ch{id}";
        string idx = $"cf_i{id}";
        string rawName  = fe.IteratorName ?? "it";
        string iterName = MangleName(rawName);

        string bufExpr = EmitExpr(fe.Series);
        FlushPreEmits(sb, indent);

        var savedType = _varTypes.TryGetValue(rawName, out var prev) ? prev : null;
        _varTypes[rawName] = CufetType.Text;
        _varRabbitDepth[rawName] = _rabbitDepth;

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{inner}CufetChase* {buf} = {bufExpr};");
        sb.AppendLine($"{inner}int {buf}_n = {buf}->len;");
        sb.AppendLine($"{inner}for (int {idx} = 0; {idx} < {buf}_n; {idx}++) {{");
        sb.AppendLine($"{loopIndent}const char* {iterName} = cufet_chase_at({buf}, {idx} + 1);");
        EmitLoopBody(sb, fe.Body, loopIndent);
        sb.AppendLine($"{inner}}}");
        sb.AppendLine($"{indent}}}");

        if (savedType != null) _varTypes[rawName] = savedType; else _varTypes.Remove(rawName);
    }

    private void EmitForEachSeries(StringBuilder sb, ForEachStatement fe, string indent)
    {
        var inner      = indent + "    ";
        var loopIndent = inner  + "    ";
        var st         = (SeriesType)TypeOf(fe.Series);
        string name    = RegisterSeriesStruct(st);
        var elem       = st.ElementType;
        int id = _forCounter++;
        string ser = $"cf_ser{id}";
        string idx = $"cf_i{id}";
        string rawName  = fe.IteratorName ?? "it";
        string iterName = MangleName(rawName);

        string serExpr = EmitExpr(fe.Series);
        FlushPreEmits(sb, indent);

        // The iterator's type is the element type — track it so the body's TypeOf resolves
        // print/access/equality correctly (mirrors the map-pair foreach).
        var savedType = _varTypes.TryGetValue(rawName, out var prev) ? prev : null;
        _varTypes[rawName] = elem;
        _varRabbitDepth[rawName] = _rabbitDepth;   // ESC.4

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{inner}{name}* {ser} = {serExpr};");
        sb.AppendLine($"{inner}int {ser}_n = {ser}->len;");
        sb.AppendLine($"{inner}for (int {idx} = 0; {idx} < {ser}_n; {idx}++) {{");
        sb.AppendLine($"{loopIndent}{EmitCType(elem)} {iterName} = {ser}->data[{idx}];");
        EmitLoopBody(sb, fe.Body, loopIndent);
        sb.AppendLine($"{inner}}}");
        sb.AppendLine($"{indent}}}");

        if (savedType != null) _varTypes[rawName] = savedType; else _varTypes.Remove(rawName);
    }

    // For each pair in a map — iterates the association list in insertion order, binding the
    // pair's key/value to cv_<pair>_key / cv_<pair>_value (see EmitMemberAccess for MappingType).
    private void EmitForEachMap(StringBuilder sb, ForEachStatement fe, string indent)
    {
        var inner      = indent + "    ";
        var loopIndent = inner  + "    ";
        var mt = (MapType)TypeOf(fe.Series);
        string name = RegisterMapStruct(mt);
        int id = _forCounter++;
        string m = $"cf_m{id}", idx = $"cf_i{id}";
        string pair = MangleName(fe.IteratorName ?? "it");

        string mapExpr = EmitExpr(fe.Series);
        FlushPreEmits(sb, indent);

        var savedType = _varTypes.TryGetValue(fe.IteratorName ?? "it", out var prev) ? prev : null;
        _varTypes[fe.IteratorName ?? "it"] = new MappingType(mt.KeyType, mt.ValueType);
        _varRabbitDepth[fe.IteratorName ?? "it"] = _rabbitDepth;   // ESC.4

        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{inner}{name}* {m} = {mapExpr};");
        sb.AppendLine($"{inner}int {m}_n = {m}->len;");
        sb.AppendLine($"{inner}for (int {idx} = 0; {idx} < {m}_n; {idx}++) {{");
        sb.AppendLine($"{loopIndent}{EmitCType(mt.KeyType)} {pair}_key = {m}->keys[{idx}];");
        sb.AppendLine($"{loopIndent}{EmitCType(mt.ValueType)} {pair}_value = {m}->vals[{idx}];");
        EmitLoopBody(sb, fe.Body, loopIndent);
        sb.AppendLine($"{inner}}}");
        sb.AppendLine($"{indent}}}");

        if (savedType != null) _varTypes[fe.IteratorName ?? "it"] = savedType;
        else _varTypes.Remove(fe.IteratorName ?? "it");
    }

    // Try to: <body>. In case of failure: <handler>. In case of exception: <handler>. — the two
    // handler paths are INDEPENDENT (matching the interpreter's ExecuteTryStatement): failures are
    // values (goto machinery, slice 6); exceptions are runtime faults (setjmp/longjmp, E-prime).
    // A fault in the FAILURE handler is NOT caught by the sibling exception handler (C# catch
    // semantics) — the failure path uninstalls this Try's jmp_buf on entry.
    private void EmitTryStatement(StringBuilder sb, TryStatement trySt, string indent)
    {
        if (trySt.ExceptionHandler == null && trySt.FailureHandler == null)
            throw new CompilerException("a Try block needs an 'In case of failure' or 'In case of exception' handler.");

        int id = _forCounter++;
        var inner = indent + "    ";
        bool hasExc  = trySt.ExceptionHandler != null;
        bool hasFail = trySt.FailureHandler != null;
        string label = $"try{id}_handler", end = $"try{id}_end", failVar = $"cf_fail{id}";
        string catchL = $"exc{id}_catch", doneL = $"exc{id}_done";
        string sup = $"cf_sup{id}", xmsg = $"cf_xmsg{id}";

        sb.AppendLine($"{indent}{{");
        // One unmaker snapshot for this Try: the failure-goto runs to it, the body's normal exit runs
        // to it, and (for the exc path) cufet_exc_um records it so cufet_raise unwinds to it.
        string? umSnap = null;
        if (UsesUnmakers) { umSnap = $"cf_umt{id}"; sb.AppendLine($"{inner}int {umSnap} = cufet_num;"); }
        if (hasFail)
            sb.AppendLine($"{inner}CufetFailure {failVar};");
        if (hasExc)
        {
            // Runtime cleanup snapshots — set BEFORE setjmp and never modified, so they are safe
            // to read after the longjmp without volatile (C11 7.13.2.1 clobbers only locals
            // MODIFIED between setjmp and longjmp).
            sb.AppendLine($"{inner}int cf_xf{id} = cufet_nfiles;");
            if (_usesConcurrency)
                sb.AppendLine($"{inner}int cf_xc{id} = cufet_nlive;");
            sb.AppendLine($"{inner}int cf_xa{id} = cufet_arena_top;");
            sb.AppendLine($"{inner}int {sup} = 0; (void){sup};");
            sb.AppendLine($"{inner}const char* {xmsg} = 0; (void){xmsg};");
            // ⚠ CUFET_PLAIN_SETJMP, never bare `setjmp` — on Windows the bare form makes the
            // matching longjmp unwind through ntdll and crash ~4% of the time. See the macro.
            sb.AppendLine($"{inner}if (CUFET_PLAIN_SETJMP(cufet_exc_bufs[++cufet_exc_top]) != 0) goto {catchL};");
            // Record the unmaker-registry depth for THIS Try, so cufet_raise runs the pending
            // unmakers (LIFO, while their C-stack objects are still live) down to here before longjmp.
            if (UsesUnmakers) sb.AppendLine($"{inner}cufet_exc_um[cufet_exc_top] = {umSnap};");
            // Record THIS Try's arena depth so cufet_raise can copy the message into an arena the
            // catch below never pops (the catch pops only arenas DEEPER than cf_xa).
            sb.AppendLine($"{inner}cufet_exc_arena[cufet_exc_top] = cf_xa{id};");
            _excOpen++;
        }

        var savedHandler = _currentTryHandler;
        if (hasFail)
            // failure-goto pops NESTED exc handlers + NESTED rabbit arenas only
            _currentTryHandler = (label, failVar, HereCleanup(umSnap));
        // The Try body is a block scope: register its Defines, fire (LIFO) at normal completion.
        if (UsesUnmakers) _scopeDepth++;
        EmitBlock(sb, trySt.Body, inner);
        if (UsesUnmakers && !BlockAlwaysExits(trySt.Body)) sb.AppendLine($"{inner}cufet_run_unmakers_to({umSnap});");
        if (UsesUnmakers) _scopeDepth--;
        if (hasFail)
            _currentTryHandler = savedHandler;

        if (hasExc) { _excOpen--; sb.AppendLine($"{inner}cufet_exc_top--;"); }   // normal completion uninstalls
        sb.AppendLine($"{inner}goto {end};");

        if (hasFail)
        {
            sb.AppendLine($"{inner}{label}:;");
            if (hasExc)
                sb.AppendLine($"{inner}cufet_exc_top--;");   // a fault in the failure handler goes OUTWARD
            var savedFailVar = _currentFailVar;
            var savedType    = _varTypes.TryGetValue("the failure", out var prev) ? prev : null;
            _currentFailVar = failVar;
            _varTypes["the failure"] = TFailMarker;
            EmitScopedBlock(sb, trySt.FailureHandler!, inner);
            _currentFailVar = savedFailVar;
            if (savedType != null) _varTypes["the failure"] = savedType; else _varTypes.Remove("the failure");
            sb.AppendLine($"{inner}goto {end};");
        }

        if (hasExc)
        {
            sb.AppendLine($"{inner}{catchL}:;");
            sb.AppendLine($"{inner}cufet_exc_top--;");
            sb.AppendLine($"{inner}{xmsg} = cufet_exc_msg;");
            // ★ Runtime cleanup: close files / free channels / pop arenas the longjmp jumped past
            // (the 9B close-on-all-paths discipline, extended to nonlocal jumps via the registries).
            sb.AppendLine($"{inner}cufet_close_files_from(cf_xf{id});");
            if (_usesConcurrency)
                sb.AppendLine($"{inner}cufet_free_chans_from(cf_xc{id});");
            sb.AppendLine($"{inner}while (cufet_arena_top > cf_xa{id}) cufet_arena_pop();");
            var savedExcH = _currentExcHandler;
            var savedExcV = _currentExcVar;
            // ⚠ The handler's binding may have been RENAMED — `In case of exception (the trouble):`
            // — and this table is keyed by the name the body will actually reference. Reading
            // `"the exception"` unconditionally left a chosen name untyped, and the reference then
            // fell through to the default type: `the message of the trouble` was refused as
            // "reading 'message' from a number". The AST computes the key, so both backends and
            // the checker read one definition of it.
            var excKey = trySt.ExceptionBindingKey;
            var savedExcT = _varTypes.TryGetValue(excKey, out var pex) ? pex : null;
            // Marked at handler ENTRY — no statement of the handler has been emitted yet, so these
            // are the depths `Suppress` has to unwind back down to. The snapshot arrives from the
            // scoped block itself, which is the only place it exists.
            _currentExcHandler = (sup, doneL, HereCleanup());
            _currentExcVar = xmsg;
            _varTypes[excKey] = TExcMarker;
            EmitScopedBlock(sb, trySt.ExceptionHandler!, inner,
                snap => _currentExcHandler = (sup, doneL, HereCleanup(snap)));
            _currentExcHandler = savedExcH;
            _currentExcVar = savedExcV;
            if (savedExcT != null) _varTypes[excKey] = savedExcT; else _varTypes.Remove(excKey);
            sb.AppendLine($"{inner}{doneL}:;");
            // RE-RAISE BY DEFAULT unless the handler executed `Suppress.` — the distinctive rule.
            sb.AppendLine($"{inner}if (!{sup}) cufet_raise({xmsg});");
        }

        sb.AppendLine($"{inner}{end}:;");
        sb.AppendLine($"{indent}}}");
    }

    // Emits all accumulated pre-emit lines (series literal constructions, etc.)
    // then clears the list. Must be called before emitting the statement that
    // uses the expression returned from EmitExpr.
    private void FlushPreEmits(StringBuilder sb, string indent)
    {
        foreach (var line in _preEmits)
            sb.AppendLine($"{indent}{line}");
        _preEmits.Clear();
    }

    /// <summary>
    /// Full static type of an expression, with every stash already read as the closure it is.
    /// </summary>
    /// <remarks>
    /// ★ The normalisation is here, at the one funnel, rather than at each of the places that go on
    /// to ask what a type IS. A `stash of T` reaches the back end by two routes — a written
    /// annotation on a parameter or a field, and the closure `cast` of a burying function produces —
    /// and everything downstream (assignment, calls, which C struct a series holds) compares the two
    /// for equality. Normalising once means none of that code learns the word "stash"; normalising
    /// at the use sites would mean remembering to, every time, forever.
    /// </remarks>
    /// <summary>A call to this axiom's wrapper, emitting the wrapper the first time it is needed.</summary>
    /// <remarks>
    /// ★ The foreign text is pasted into this program's own C, which is all "compiling an axiom"
    /// means for this backend — there is no marshalling layer and no dynamic dispatch, because the
    /// signature is known here and gcc can see both sides at once. The interpreter reaches the same
    /// place the long way round, through a shim built out of the same wrapper.
    /// </remarks>
    /// <summary>The `void(*)(void*)` the unmaker registry needs, wrapping a release axiom.</summary>
    /// <remarks>
    /// ★ A thunk exists because the registry's shape is fixed and a release axiom's is not:
    /// `closedir` gives back an `int`, `free` gives back nothing, and the registry wants neither.
    /// Discarding the answer here is what lets one registry serve destructors and foreign handles
    /// alike — which is why this whole clause needed no new cleanup machinery.
    ///
    /// ⚠ The release axiom's own wrapper is emitted here too. It may be named ONLY by
    /// `and free it with`, never called by hand, so nothing else would have emitted it.
    /// </remarks>
    private string EmitReleaseThunk(AxiomLiteral release)
    {
        string language = release.Language ?? "foreign";
        string key = ForeignC.Identity(language, release.Source, release.Parameters);
        if (_releaseThunks.TryGetValue(key, out var existing)) return existing;

        string fnName = ForeignC.FunctionName(language, release.Source, release.Parameters);
        if (!_axiomFnNames.ContainsKey(key))
        {
            _axiomFnNames[key] = fnName;
            _axiomFns.Append(ForeignC.Wrapper(fnName, "static ", language, release.Source,
                                              release.Parameters, release.ReturnType!));
        }
        string thunk = fnName + "_release";
        _releaseThunks[key] = thunk;
        _axiomFns.AppendLine($"static void {thunk}(void* cufet_held) {{ (void){fnName}(cufet_held); }}");
        return thunk;
    }

    // Release thunks already emitted, by the axiom they wrap — one per distinct release axiom.
    private readonly Dictionary<string, string> _releaseThunks = new(StringComparer.Ordinal);

    // The source behind every name bound to an axiom, including aliases. Needed only to build an
    // axiom VALUE — a direct call carries its literal on the AST node the checker resolved.
    private readonly Dictionary<string, AxiomLiteral> _axiomLiterals = new(StringComparer.Ordinal);

    // Axiom-value thunks already emitted, keyed the same way wrappers are.
    private readonly Dictionary<string, string> _axiomValueThunks = new(StringComparer.Ordinal);

    /// <summary>The callable shape an axiom presents to Cufet — what its value struct is built from.</summary>
    /// <remarks>
    /// ⚠ NOT a claim that an axiom IS a function. The two stay separate types in the front end (a
    /// function has a body you can read; an axiom is text taken on faith, and its language is part
    /// of what it is). This is only the C LAYOUT question, and for that they are the same two
    /// pointers — which is what lets an axiom be passed, stored and called with no new machinery.
    /// </remarks>
    private static FunctionType AsFunctionType(AxiomType axiom) =>
        new([.. axiom.ParameterTypes], axiom.ReturnType);

    /// <summary>An axiom as a VALUE — `{thunk, NULL}`, the same struct a function value is.</summary>
    /// <remarks>
    /// ★★ The thunk is where the boundary lives. Its parameters are CUFET types, its body marshals
    /// them into C, calls the wrapper, and converts the result back — exactly what a direct call
    /// emits inline, and through the same two helpers, so a call through a value and a call by name
    /// cannot mean different things.
    ///
    /// ⚠ The conversions can add pre-emitted statements (an arena copy for a text, a NULL test for
    /// an address). Those belong to the THUNK's body, not to whatever statement happened to be
    /// under construction when the value was built, so `_preEmits` is swapped out around it the way
    /// EmitClosure swaps it for a lambda body.
    /// </remarks>
    private string EmitAxiomValue(AxiomLiteral axiom, int line)
    {
        string language = axiom.Language ?? "foreign";
        string key = ForeignC.Identity(language, axiom.Source, axiom.Parameters, axiom.ReturnType);
        var shape = new FunctionType([.. axiom.Parameters.Select(p => p.Type)], axiom.ReturnType);
        string cfn = RegisterFuncStruct(shape);

        if (!_axiomValueThunks.TryGetValue(key, out var thunk))
        {
            string fnName = EnsureAxiomWrapper(axiom);
            // ⚠ NOT the ForeignC.FunctionPrefix the wrappers use, and that is load-bearing twice
            // over: that prefix is how a gcc complaint about the AUTHOR'S C is told apart from a
            // code-generator bug (see GccInvoker.BuildFailureMessage), and it is what WrapperCount
            // counts in the tests. A thunk is cufet's own code, so it takes cufet's own `cv_`.
            thunk = $"cv_axiomvalue{_axiomValueThunks.Count}";
            _axiomValueThunks[key] = thunk;

            var savedPre = new List<string>(_preEmits);
            _preEmits.Clear();
            var marshalled = axiom.Parameters
                .Select((p, i) => ForeignArgumentFrom(p.Type, $"p{i}", line))
                .ToList();
            string body = ForeignResultFrom(axiom, $"{fnName}({string.Join(", ", marshalled)})");
            var inner = new List<string>(_preEmits);
            _preEmits.Clear();
            _preEmits.AddRange(savedPre);

            var decls = new[] { "void* cufet_env" }
                .Concat(axiom.Parameters.Select((p, i) => $"{EmitCType(p.Type)} p{i}"));
            var fn = new System.Text.StringBuilder();
            fn.AppendLine($"static {EmitCType(axiom.ReturnType)} {thunk}({string.Join(", ", decls)}) {{");
            fn.AppendLine("    (void)cufet_env;");
            foreach (var statement in inner) fn.AppendLine("    " + statement);
            fn.AppendLine($"    return {body};");
            fn.AppendLine("}");
            _axiomFns.Append(fn);
        }

        return $"({cfn}){{ .fn = {thunk}, .env = NULL }}";
    }

    private string EmitAxiomCall(AxiomLiteral axiom, IReadOnlyList<IExpression>? args, int line)
    {
        string fnName = EnsureAxiomWrapper(axiom);

        // ★ Marshalled per parameter, from the CUFET type the checker put on the declaration —
        // the writer names no C type anywhere, and this is the only place one is chosen.
        var marshalled = axiom.Parameters
            .Select((p, i) => ForeignArgumentFrom(p.Type, EmitExpr(args![i]), line))
            .ToList();
        return ForeignResultFrom(axiom, $"{fnName}({string.Join(", ", marshalled)})");
    }

    /// <summary>The wrapper function for this axiom, emitted once and named.</summary>
    /// <remarks>
    /// ★★ The wrapper text comes from ForeignC, so this is byte-for-byte the function the
    /// interpreter's shim compiles. Splicing, the guard and the call all happen once, in one place,
    /// for both backends.
    ///
    /// ★ Keyed on IDENTITY, not on the name: two names for the same source are one wrapper, and the
    /// same source written twice is one wrapper too.
    /// </remarks>
    private string EnsureAxiomWrapper(AxiomLiteral axiom)
    {
        string language = axiom.Language ?? "foreign";
        string key = ForeignC.Identity(language, axiom.Source, axiom.Parameters);
        if (_axiomFnNames.TryGetValue(key, out var existing)) return existing;

        string fnName = ForeignC.FunctionName(language, axiom.Source, axiom.Parameters);
        _axiomFnNames[key] = fnName;
        _axiomFns.Append(ForeignC.Wrapper(fnName, "static ", language, axiom.Source,
                                          axiom.Parameters, axiom.ReturnType!));
        return fnName;
    }

    /// <summary>What comes back from a wrapper, as the Cufet value the declaration promised.</summary>
    /// <remarks>
    /// ⚠ Takes the call as C TEXT rather than as an AST node, because it is used from two places
    /// that have nothing else in common: a direct call, where the arguments are expressions, and an
    /// axiom-value thunk, where they are that thunk's own C parameters. Writing the conversion
    /// twice is how the two would come to disagree about what a `voidable text` costs.
    /// </remarks>
    private string ForeignResultFrom(AxiomLiteral axiom, string call) => axiom.ReturnType switch
    {
        NumberType => $"cufet_dec_from_foreign({call})",
        FactType   => call,                       // already the 1/0 a Cufet fact is in C
        VoidableType { Inner: NumberType } => EmitForeignReal(call),
        VoidableType { Inner: AddressType } => EmitForeignAddress(call, axiom.ReleaseAxiom),
        _          => EmitForeignText(call),      // voidable text — copied out of C's memory
    };

    /// <summary>`the text at &lt;address&gt;` — copied into the arena, as a `voidable text`.</summary>
    /// <remarks>
    /// ★ The SAME arena copy an axiom's `voidable text` result gets, and for the same reason: the
    /// bytes belong to C, which may free or overwrite them whenever it likes. What differs is only
    /// where the pointer came from — a result there, an address the program was holding here.
    ///
    /// ⚠ Reading through a VOID address is void, not a crash. A voidable address carries its own
    /// `has`; one narrowed out of its voidable is the bare pointer, and that is the only difference
    /// between the two branches below.
    ///
    /// ⚠ The address expression is emitted ONCE, into a local. Written inline it would appear twice
    /// in the voidable branch — and `the text at (cast copy-of on ("x"))` would then allocate twice
    /// and read the second one, which is a leak and a different answer.
    /// </remarks>
    private string EmitForeignTextAt(ForeignTextAt read)
    {
        var addressType = TypeOf(read.Address);
        string cvd = RegisterVoidableStruct(new VoidableType(TText));
        int id = _freshId++;
        string held = $"cf_th{id}";
        _preEmits.Add($"{EmitCType(addressType)} {held} = {EmitExpr(read.Address)};");
        string raw = addressType is VoidableType
            ? $"{held}.has ? (const char*){held}.val : (const char*)0"
            : $"(const char*){held}";
        _preEmits.Add($"const char* cf_ta{id} = {raw};");
        _preEmits.Add($"const char* cf_tc{id} = cf_ta{id} ? cufet_arena_str_at(cufet_arena_top, cf_ta{id}) : (const char*)0;");
        return $"(cf_tc{id} ? ({cvd}){{ .has = 1, .val = cf_tc{id} }} : ({cvd}){{ .has = 0 }})";
    }

    /// <summary>A pointer from foreign source, as the `voidable address` the declaration asked for.</summary>
    /// <remarks>
    /// ★ NOT copied, unlike a text — the pointer IS the value. Nothing is read through it here or
    /// anywhere else without the writer saying so, which is the whole of what makes it inert.
    ///
    /// ★ NULL becomes void, the same as every other absence crossing this boundary: `fopen`,
    /// `malloc`, `getenv` and `opendir` all report failure that way, so every way C can fail lands
    /// in the mechanism the language already has.
    /// </remarks>
    private string EmitForeignAddress(string call, AxiomLiteral? release)
    {
        string cvd = RegisterVoidableStruct(new VoidableType(AddressType.Instance));
        int id = _freshId++;
        _preEmits.Add($"void* cf_fa{id} = {call};");

        // ★★ `and free it with <name>` is registered HERE, at the acquisition, and that is the
        // whole of what makes it work. It used to be registered against the `Define` that caught
        // the result, which anchored a property of the AXIOM to a property of the CALL SITE — and
        // three things fell out of that:
        //
        //   1. An acquisition nobody named leaked. `Cast copy-of on ("x").` called strdup and
        //      registered nothing, as did one used inline in a condition. Measured in emitted C.
        //   2. An axiom with a release clause could not be passed around, because a call reached
        //      through a value has no `Define` to hang the registration on.
        //   3. It was the more dangerous half of the trade it claimed to be making. Registering
        //      per BINDING is what risks a double free, since names multiply and can reach one
        //      pointer; registering per ACQUISITION happens exactly once per malloc by
        //      construction. The old comment had this backwards.
        //
        // ⚠ The RAW pointer is registered, not the `voidable address` struct around it, and a NULL
        // one is not registered at all — `closedir(NULL)` is undefined, and "C had nothing to give"
        // is not a thing to free.
        //
        // ★ The registry is a thread-local stack pushed at whatever point this runs, so a call
        // inside an axiom-value thunk pushes onto the CALLER's block exactly as a direct call does.
        // That is why the value case needed no separate machinery.
        if (release is { } freeIt)
            _preEmits.Add($"if (cf_fa{id}) cufet_reg_unmaker(cf_fa{id}, {EmitReleaseThunk(freeIt)});");

        return $"(cf_fa{id} ? ({cvd}){{ .has = 1, .val = cf_fa{id} }} : ({cvd}){{ .has = 0 }})";
    }

    /// <summary>A `double` from foreign source, as the `voidable number` the declaration asked for.</summary>
    /// <remarks>
    /// ★ Nothing is converted here. The shared C did the whole base-2-to-base-10 conversion and
    /// handed back the three numbers a decimal is made of, so this assembles them with the same
    /// `cufet_dec_lit` a decimal literal uses — the interpreter assembles the identical three.
    ///
    /// ★ `ok` is 0 for NaN, an infinity, or a magnitude no decimal can hold, and all three become
    /// void. That follows `math`'s partial functions rather than inventing a rule: `square-root of
    /// (-4)` is void today, and so is `log of (0)`.
    /// </remarks>
    private string EmitForeignReal(string call)
    {
        string cvd = RegisterVoidableStruct(new VoidableType(TNumber));
        int id = _freshId++;
        _preEmits.Add($"{ForeignC.RealResultCType} cf_fr{id} = {call};");
        return $"(cf_fr{id}.ok ? ({cvd}){{ .has = 1, .val = cufet_dec_lit(cf_fr{id}.hi, cf_fr{id}.lo, "
             + $"cf_fr{id}.scale, cf_fr{id}.sign) }} : ({cvd}){{ .has = 0 }})";
    }

    /// <summary>A `char*` from foreign source, as a `voidable text` that owns its own bytes.</summary>
    /// <remarks>
    /// ★ COPIED into the arena, never aliased. The bytes belong to C: `strerror` hands back a
    /// static buffer the next call overwrites, and anything malloc'd is freed the moment its
    /// owner says so. A Cufet text that pointed at either would change under the program.
    ///
    /// ★ NULL becomes void, which is the whole reason the result type is `voidable text`. It is C's
    /// universal "nothing to give", and it lands in the mechanism the language already has.
    /// </remarks>
    private string EmitForeignText(string call)
    {
        string cvd = RegisterVoidableStruct(new VoidableType(TText));
        int id = _freshId++;
        _preEmits.Add($"const char* cf_fx{id} = cufet_arena_str_at(cufet_arena_top, {call});");
        return $"(cf_fx{id} ? ({cvd}){{ .has = 1, .val = cf_fx{id} }} : ({cvd}){{ .has = 0 }})";
    }

    /// <summary>One Cufet value, as the C parameter the wrapper declared.</summary>
    /// <remarks>
    /// ⚠ `number` is a decimal and C wants an integer, so it is RANGE-CHECKED rather than
    /// truncated: a fractional or oversized argument raises where it is passed, in the same class
    /// as a divide by zero, instead of arriving in C as something else entirely.
    /// </remarks>
    /// <summary>One value on its way into C, from the C text that produces it.</summary>
    /// <remarks>
    /// ⚠ Text in, not an AST node — for the same reason ForeignResultFrom takes text. A direct call
    /// marshals an expression; an axiom-value thunk marshals its own C parameter, which no
    /// expression names.
    /// </remarks>
    private static string ForeignArgumentFrom(CufetType type, string value, int line) => type switch
    {
        NumberType => $"cufet_foreign_ll({value}, {line})",
        FactType   => $"({value} ? 1 : 0)",
        TextType   => value,
        // Straight back the way it came, with nothing to check: Cufet never made this value, so
        // the only thing it can be is a pointer C handed over.
        AddressType => value,
        _          => throw new TypeException(
                          $"That doesn't work: a {FormatTypeName(type)} cannot be handed to foreign source yet."),
    };

    private CufetType TypeOf(IExpression expr) => NoStashes(TypeOfRaw(expr));

    // The re-derivation itself. The program already type-checked, so this is a straightforward
    // walk (no error handling) used to pick C declaration types, print helpers, comparison
    // strategy, and to discover record shapes. Call TypeOf, never this.
    private CufetType TypeOfRaw(IExpression expr) => expr switch
    {
        NumberLiteral         => TNumber,
        BitsLiteral           => TBits,
        BitsShift             => TBits,
        BitsConvert           => TBits,
        BooleanLiteral        => TFact,
        StringLiteral         => TText,
        // Foreign source, tagged by the checker. It has no C representation — nothing stores an
        // axiom — but the var-type prepass asks for the type of every `Define`d value.
        AxiomLiteral axiom    => new AxiomType(axiom.Language ?? "", axiom.ReturnType,
                                               [.. axiom.Parameters.Select(p => p.Type)]),
        RangeExpression       => new SeriesType(TNumber),
        SeriesLiteral sl      => new SeriesType(SeriesElementType(sl)),
        SeriesLength          => TNumber,
        SeriesAccess sa       => SeriesAccessType(sa),
        // 'not' over a bit pattern gives a bit pattern back, same width; over a fact, a fact.
        UnaryExpression u     => u.Op != TokenType.Not ? TNumber
                               : TypeOf(u.Operand) is BitsType ? TBits : TFact,
        // A matrix binary op's VALUE type is matrix (the raw `matrix or failure` is seen only by
        // FallibleReturnType — same unwrap convention as fallible calls).
        // An overloaded operator's VALUE type is the overload's declared return — which may be ANY
        // type, not the operand type (a `vec2 * vec2` dot product returns a number). A fallible
        // overload unwraps to its success type, the same convention as a fallible call.
        BinaryExpression b    => IsMatrixOp(b) || IsMatrixScale(b) ? MatrixType.Instance
                               : OverloadFor(b) is { } ov ? OverloadValueType(ov, b.Op)
                               // A gate over bit patterns yields a bit pattern; over facts, a
                               // fact. Checked before the arithmetic/comparison split, which
                               // would otherwise call every non-arithmetic result a fact.
                               : b.Op is TokenType.And or TokenType.Or or TokenType.Xor
                                 && TypeOf(b.Left) is BitsType ? TBits
                               // Arithmetic over bit patterns stays bits; the ordering and
                               // equality operators still yield a fact, as for any type.
                               : IsArithmeticOp(b.Op) && TypeOf(b.Left) is BitsType ? TBits
                               : IsArithmeticOp(b.Op) ? TNumber : TFact,
        MatrixLiteral         => MatrixType.Instance,
        ChaseLiteral          => ChaseType.Instance,
        MatrixSized           => MatrixType.Instance,
        MatrixAccess          => TNumber,
        VariableReference vr  => vr.Name == "input" ? new ReadableStreamType(TText)   // `the input` = stdin
                               : _narrowedVars.TryGetValue(vr.Name, out var nt) ? nt.Type
                               : _closureSelf is { } cs && vr.Name == cs.Name ? cs.Type   // recursive self-reference
                               : _varTypes.TryGetValue(vr.Name, out var t) ? t
                               : _funcTypes.TryGetValue(vr.Name, out var ftv) ? ftv   // a bare named function used as a value
                               // ★ A module NAMED but not pulled in this file. A module's methods run
                               // inside the block that pulled the module, so they inherit what THAT
                               // block pulled — the checker allows the debt and settles it at the pull.
                               // Without this the name fell through to `number` and reading a member
                               // off it blamed the member, which is exactly what the fallback's own
                               // note warns a scoping mistake looks like.
                               : DeferredModuleType(vr.Name) is { } deferred ? deferred
                               : TNumber,
        CastExpression c      => CastReturnType(c),
        LambdaLiteral lam     => LambdaFunctionType(lam),
        RecordLiteral rl      => new RecordType(
                                     rl.PositionalFields.Select(TypeOf).ToList(),
                                     rl.NamedFields.Select(f => (f.Name, TypeOf(f.Value))).ToList()),
        // `the width of <bits>` — a property of a type that has no fields, so it can never
        // shadow a real one. See TypeChecker.Records for why this is not a keyword.
        BitsAtWidth           => TBits,
        RecordNamedAccess { FieldName: "width" } bw when TypeOf(bw.Record) is BitsType => TNumber,
        RecordNamedAccess rna => FieldType(TypeOf(rna.Record), rna.FieldName),
        ObjectLiteral ol      => ObjType(ol.ResolvedTypeName ?? ol.TypeName),
        PossessiveAccess pa   => pa.Target is VariableReference bvr && _bookAliases.TryGetValue(bvr.Name, out var bn)
                                     ? (CufetLayerGetter(bn, pa.Member)?.ReturnType       // layer getter (math's pi)
                                        ?? BookConstantType(bn, pa.Member))               // native constant
                                     : FieldType(TypeOf(pa.Target), pa.Member),
        VoidLiteral           => TVoid,
        ButVoidDefault bvd    => TypeOf(bvd.Voidable) is VoidableType vt ? vt.Inner : TypeOf(bvd.Default),
        ConditionalExpression ce => ConditionalType(ce),
        MapLiteral ml         => MapLiteralType(ml),
        // Lookup flatten: on a voidable-valued map the entry IS already voidable — never nest.
        MapLookup mlk         => MapValueType(mlk.Map) is VoidableType vvt ? vvt : new VoidableType(MapValueType(mlk.Map)),
        MapHasKey             => TFact,
        MapHasEntry           => TFact,
        MapSize               => TNumber,
        FailureLiteral        => TFailMarker,
        // The operand's raw `T or failure` type comes from FallibleReturnType (TypeOf already
        // unwraps a fallible expr to its inner T, so `TypeOf(...) is FailureType` would never hit).
        FailureFallback ff    => FallibleReturnType(ff.Fallible) is { } ft ? ft.Inner : TypeOf(ff.Default),
        FailurePropagate fp   => FallibleReturnType(fp.Fallible) is { } ft2 ? ft2.Inner : TypeOf(fp.Fallible),
        TextJoin or TextConvert or TextSubstringRange or TextSubstringEdge
            or TextReplace or TextCase or TextTrim => TText,
        TextSplit             => new SeriesType(TText),
        // From bits this is total — 64 bits always fits a 96-bit mantissa — so it is a plain
        // number. From text it may simply not be one, hence the voidable.
        NumberConvert nvc when TypeOf(nvc.Value) is BitsType => TNumber,
        NumberConvert or TextFind => new VoidableType(TNumber),
        TextLength            => TNumber,
        ForeignTextAt         => new VoidableType(TText),
        TextContains          => TFact,
        // A file read is fallible; its post-check VALUE type is the inner success type (the
        // raw `T or failure` is only seen by FallibleReturnType, for Try / but-on-failure / propagate).
        FileReadExpression fr => FileReadSuccessType(fr),
        PathCheckExpression   => TFact,
        // Stream reads (infallible): read a line → voidable text (void at EOF); read all → text;
        // read all lines → series of text.
        ReadExpression re     => re.Form switch
        {
            ReadForm.Line     => new VoidableType(TText),
            ReadForm.AllLines => new SeriesType(TText),
            _                 => TText,
        },
        // run / subprocess pipe are fallible; post-check VALUE type is the run-result record.
        RunExpression or PipeExpression => RunResultRecordType,
        ChannelCreation cc    => new ChannelType(cc.ElementType),
        DeliveryExpression de => new VoidableType((TypeOf(de.Channel) as ChannelType)?.ElementType ?? TNumber),
        InterruptRequestedExpression => TFact,                // `an interrupt is requested` → fact
        // A named task's awaited result — inner success type (raw `T or failure` is seen only by
        // FallibleReturnType, for Try / but-on-failure / propagate), exactly like a fallible call.
        AwaitedResultExpression are => AwaitedResultInnerType(are),
        SortExpression sort   => TypeOf(sort.Series),           // sorted is element-type-preserving
        RandomNumber          => TNumber,
        RandomGuess           => TFact,
        RandomItem ri         => new VoidableType(((SeriesType)TypeOf(ri.Series)).ElementType),   // void on empty
        RandomlyShuffled rs   => TypeOf(rs.Series),             // element-type-preserving, like sorted
        EnvironmentVariableExpression => new VoidableType(TText),   // void when unset
        CurrentDirectoryExpression    => new VoidableType(TText),   // void when there is none to report
        IsTypeCheck           => TFact,
        // `unbury s` is `cast s on ()`, and a stash lowers to a closure — so its type is simply
        // what that closure gives back, which is already `voidable T`.
        UnburyExpression ub   => TypeOf(ub.Stash) is FunctionType uf
            ? uf.ReturnType!
            : throw new CompilerException(
                  $"'unbury' on line {ub.Line} was given something that is not a stash."),
        // A directory listing is fallible; its post-check VALUE type is series of text (raw
        // `series of text or failure` is seen only by FallibleReturnType — the file-read convention).
        DirectoryContentsExpression => new SeriesType(TText),
        _ => throw new CompilerException(
                 $"'{NodeName(expr)}' expressions are not yet supported by the compiler.")
    };

    // A book member's declared type, for TypeOf on a `<book>'s <member> of (...)` cast. No
    // bundled book has a native member left — both are written in Cufet, and a call to one of
    // their members types as ordinary method dispatch long before this is reached. What remains
    // here is the answer for a member that exists in no layer, which the emitter then refuses.
    private CufetType BookMemberReturnType(string bookName, string member, IReadOnlyList<IExpression> args) =>
        TNumber;

    private static CufetType BookConstantType(string bookName, string member) => TNumber;   // math pi / e

    private CufetType MapLiteralType(MapLiteral ml) =>
        ml.KeyType != null && ml.ValueType != null
            ? new MapType(ml.KeyType, ml.ValueType)
            : new MapType(TypeOf(ml.Pairs[0].Key), TypeOf(ml.Pairs[0].Value));

    private CufetType MapValueType(IExpression mapExpr) =>
        TypeOf(mapExpr) is MapType mt ? mt.ValueType
            : throw new CompilerException("map operation on a non-map value.");

    // Minimal nominal ObjectType (fields looked up from _objectDefs by name when needed).
    private static CufetType ObjType(string name) =>
        new ObjectType(name, Array.Empty<CufetType>(), Array.Empty<(string, CufetType)>(),
                       Array.Empty<(string, FunctionType)>());

    private CufetType SeriesElementType(SeriesLiteral sl) =>
        sl.Annotation ?? (sl.Elements.Count > 0 ? TypeOf(sl.Elements[0]) : TNumber);

    private CufetType SeriesAccessType(SeriesAccess sa)
    {
        var tt = TypeOf(sa.Target);
        if (tt is SeriesType st) return st.ElementType;
        // A character is a one-character text — the language has no separate character type.
        if (tt is ChaseType) return CufetType.Text;
        if (tt is RecordType rt) return rt.PositionalTypes[LiteralIndex(sa.Index) - 1];
        if (tt is ObjectType ot) return _objectDefs[ot.Name].PositionalTypes[ObjectPositionalIndex(ot.Name, sa.Index)];
        throw new CompilerException("positional access on this type is not yet supported by the compiler.");
    }

    private CufetType CastReturnType(CastExpression c)
    {
        // A fallible call's VALUE (after the call-site failure check) is the inner success T;
        // the raw `T or failure` type is only seen by but-on-failure / propagate / a Try.
        var rt = RawCastReturnType(c);
        return rt is FailureType ft ? ft.Inner : rt;
    }

    private CufetType RawCastReturnType(CastExpression c)
    {
        // ★ An axiom's result is declared where the axiom is WRITTEN, and the checker has already
        // refused one that never said — see ForeignC for what may cross back.
        if (c.RunsAxiom is { } ax) return ax.ReturnType!;
        // Reached through a value: the WRITTEN type is the only thing that says what comes back.
        if (c.RunsAxiomValue && TypeOf(c.Function) is AxiomType held) return held.ReturnType!;

        // The filling decides the return type — `first-two of text` gives back a series of text.
        if (CalledFunction(c.Function, c.ResolvedFunctionName, c.Line, c.Column) is VariableReference vr)
        {
            if (_closureSelf is { } self && vr.Name == self.Name)                          // recursive self-call
                return self.Type.ReturnType ?? TNumber;
            if (_varTypes.TryGetValue(vr.Name, out var vvt) && vvt is FunctionType vft)     // function-valued variable
                return vft.ReturnType ?? TNumber;
            if (_funcReturnTypes.TryGetValue(vr.Name, out var rt)) return rt ?? TNumber;   // free function
            if (c.Args.Count > 0 && TypeOf(c.Args[0]) is ObjectType ot)                    // method dispatch
                return MethodReturnType(ot.Name, vr.Name);
        }
        if (c.Function is PossessiveAccess bpa && bpa.Target is VariableReference bref
            && _bookAliases.TryGetValue(bref.Name, out var bookName))                      // book member call
        {
            // The book's Cufet layer answers first, by the RESOLVED (filled) member name — and
            // directly by owner name, because inside a Bind hoisted out of a pull body the pulled
            // binding has no _varTypes entry for the object branch below to type the target with.
            if (CalledFunction(c.Function, c.ResolvedFunctionName, c.Line, c.Column)
                    is PossessiveAccess lpa && CufetLayerHasMethod(bookName, lpa.Member))
                return MethodReturnType(bookName, lpa.Member);
            return BookMemberReturnType(bookName, bpa.Member, c.Args);
        }
        // ⚠ The RESOLVED member, not the written one — a filled-in method is a member under its
        // filling (`unique of number`), and the template's name is a member of nothing.
        if (CalledFunction(c.Function, c.ResolvedFunctionName, c.Line, c.Column) is PossessiveAccess pa
            && TypeOf(pa.Target) is ObjectType pot)
            return MethodReturnType(pot.Name, pa.Member);
        // Fallback: any other expression that yields a function value (function-valued call). Kept
        // LAST so a possessive method call (racer's age-in) resolves as a method, not a field access.
        if (TypeOf(c.Function) is FunctionType cft) return cft.ReturnType ?? TNumber;
        return TNumber;
    }

    // If `expr` is a fallible operation (a call to a fallible fn/method, or a fallible I/O op),
    // its `T or failure` return type; else null. Fallible I/O composes with Try / but-on-failure /
    // propagate through exactly the same machinery as a fallible call.
    private FailureType? FallibleReturnType(IExpression expr) => expr switch
    {
        CastExpression c when RawCastReturnType(c) is FailureType ft => ft,
        FileReadExpression fr => new FailureType(FileReadSuccessType(fr)),
        RunExpression or PipeExpression => new FailureType(RunResultRecordType),
        AwaitedResultExpression are when AwaitedRawResultType(are) is FailureType aft => aft,
        BinaryExpression b when IsMatrixOp(b) => new FailureType(MatrixType.Instance),
        // A fallible operator overload — same shape as a matrix op, so Try / `but on failure` /
        // `or pass the failure off` all route through the existing machinery unchanged.
        BinaryExpression b when OverloadFor(b) is { } ov
                             && OverloadReturnType(ov.LeftTypeName, ov.RightTypeName, b.Op) is FailureType oft => oft,
        DirectoryContentsExpression => new FailureType(new SeriesType(TText)),
        _ => null,
    };

    // A named task's declared/inferred result type as tracked at its spawn (raw — keeps FailureType).
    private CufetType AwaitedRawResultType(AwaitedResultExpression are) =>
        are.Task is VariableReference vr && _taskInfos.TryGetValue(vr.Name, out var info)
            ? info.ResultType ?? TNumber
            : TNumber;

    // The awaited result's post-check VALUE type — inner success T when the task is fallible.
    private CufetType AwaitedResultInnerType(AwaitedResultExpression are) =>
        AwaitedRawResultType(are) is FailureType ft ? ft.Inner : AwaitedRawResultType(are);

    private CufetType FileReadSuccessType(FileReadExpression fr) =>
        fr.Form == FileReadForm.AllLines ? new SeriesType(TText) : TText;

    // The `run`/pipe success record — (errors: text, exit-code: number, output: text), named fields
    // alphabetical, matching the interpreter's RunResultType exactly. A launch failure is the `or
    // failure`; a command that runs and exits nonzero is a SUCCESS record with that exit-code.
    private static readonly RecordType RunResultRecordType = new RecordType(
        Array.Empty<CufetType>(),
        new (string, CufetType)[] { ("errors", TText), ("exit-code", TNumber), ("output", TText) });

    private CufetType MethodReturnType(string objName, string methodName)
    {
        var (owner, _) = ResolveMethodLevel(objName, methodName);
        return _objectDefs[owner].Methods.First(m => m.Name == methodName).ReturnType ?? TNumber;
    }

    // Finds which level of the embed chain owns a method, and the C access suffix (a chain of
    // .cv_<embed>) to reach the receiver object at that level.
    private (string ObjName, string Suffix) ResolveMethodLevel(string objName, string method)
    {
        var def = _objectDefs[objName];
        if (def.Methods.Any(m => m.Name == method)) return (objName, "");
        if (def.EmbeddedTypeName != null)
        {
            var (owner, suffix) = ResolveMethodLevel(def.EmbeddedTypeName, method);
            return (owner, $".{MangleName(def.EmbeddedTypeName)}{suffix}");
        }
        throw new CompilerException($"'{objName}' has no method '{method}'.");
    }

    /// <summary>
    /// The type of a MODULE named where it was not pulled, or null when the name is not one.
    /// </summary>
    /// <remarks>
    /// ⚠ A rule the interpreter implemented and the compiler did not — a documented language
    /// behaviour with one back end. Nothing in the corpus met it, because every module that uses
    /// `math` today pulls it in its own file, and ModulePullTests covers the rule thoroughly on the
    /// interpreter alone. Found 2026-08-31 by a two-file example whose module rounds with `math`.
    /// </remarks>
    private CufetType? DeferredModuleType(string name) =>
        _objectDefs.TryGetValue(name, out var def)
        && TypeChecker.IsModuleConformer(def.ConformedInterfaces)
            ? ObjType(def.Name)
            : null;

    private CufetType FieldType(CufetType t, string fieldName)
    {
        // the message of the failure → text; the category of the failure → voidable text.
        if (t is FailureMarkerType) return fieldName == "message" ? TText : new VoidableType(TText);
        // the message of the exception → text (ExceptionValue exposes only Message).
        if (t is ExceptionMarkerType) return TText;
        if (t is MappingType mp) return fieldName == "key" ? mp.KeyType : mp.ValueType;   // the key/value of pair
        if (t is MatrixType) return TNumber;   // the rows/columns of m — counts, via named access
        if (t is RecordType rt) return rt.NamedFields.First(f => f.Name == fieldName).Type;
        if (t is ObjectType ot) return ObjectMemberType(ot.Name, fieldName);
        // ⚠ Honest about WHOSE fault it is. This used to say "field access on 'number' is not yet
        // supported by the compiler", which read as a missing feature and blamed the compiler —
        // and the commonest way to reach it was a scoping mistake, where an unresolved name fell
        // back to `number` and then had a member read off it. Nothing here is unimplemented: a
        // number has no fields, and saying so points at the program.
        throw new CompilerException($"'{fieldName}' can't be read from a {FormatTypeName(t)} — it has no such member.");
    }

    // Static type of an object member, walking the embed chain (getter → own field →
    // embed handle → promoted field).
    private CufetType ObjectMemberType(string objName, string member)
    {
        var def = _objectDefs[objName];
        if (GetterFor(objName, member) is { } g) return g.ReturnType;
        var nf = def.NamedFields.FirstOrDefault(f => f.FieldName == member);
        if (nf.FieldName == member) return nf.FieldType;
        if (def.EmbeddedTypeName == member) return ObjType(member);   // embed handle → embedded object
        if (def.EmbeddedTypeName != null) return ObjectMemberType(def.EmbeddedTypeName, member);
        throw new CompilerException($"'{objName}' has no member '{member}'.");
    }

    // 0-based positional index for an object, guarding the named-field case (which the
    // interpreter rejects too — a named-field object has no positional slots).
    private int ObjectPositionalIndex(string objName, IExpression? index)
    {
        int i = LiteralIndex(index);
        var pos = _objectDefs[objName].PositionalTypes;
        if (i < 1 || i > pos.Count)
            throw new CompilerException($"'{objName}' has no positional field {i} — access named-field objects by name (the <field> of ...).");
        return i - 1;
    }

    // A compile-time positional index from an ordinal literal (the first → 1, ...).
    private static int LiteralIndex(IExpression? index) =>
        index is NumberLiteral n ? (int)n.Value
            : throw new CompilerException("positional record access needs a constant index (the first/second/... of).");

    private static bool IsArithmeticOp(TokenType op) =>
        op is TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash or TokenType.Percent;

    // Emits the C signature of a top-level function. Reference-type params/returns are
    // now supported: records/objects pass by value (value semantics fall out of C struct
    // copy), text as const char*, series as an arena pointer (its region is the caller's).
    private string EmitFunctionSignature(BindStatement bind)
    {
        if (bind.UntoType != null)
            throw new CompilerException($"Object methods declared with 'unto' are not yet supported by the compiler.");
        // Named constructors are ordinary functions here — bind.ReturnType is the object
        // type (or a FailureType for 'or failure', which EmitCType defers cleanly).
        var paramsStr = string.Join(", ", bind.Parameters.Select(p => $"{EmitCType(p.Type)} {MangleName(p.Name)}"));
        return $"{EmitCType(bind.ReturnType)} {MangleName(bind.Name)}({paramsStr})";
    }

    // Same signature, but with an explicit already-mangled C name (interface specializations, whose
    // name carries the concrete conformer suffix).
    private string EmitSpecFunctionSignature(BindStatement bind, string cName)
    {
        var paramsStr = string.Join(", ", bind.Parameters.Select(p => $"{EmitCType(p.Type)} {MangleName(p.Name)}"));
        return $"{EmitCType(bind.ReturnType)} {cName}({paramsStr})";
    }

    // Folds 'Bind <ret> to <name> unto <type> ...' methods into their target object's
    // method list — an unto method is identical to a nested one, only its declaration
    // site differs, so once merged the normal method emission + dispatch handle it.
    // Every `unto` declaration anywhere, merged into its target object.
    //
    // Generic walk: the hand-written version had no PullStatement arm, so a method declared with
    // `unto` inside `Pull a book on ... Done.` was silently dropped — the object compiled without
    // it and the call site failed to resolve. Same omission as its two sibling collectors.
    private void MergeUntoMethods(IEnumerable<IStatement> stmts) =>
        AstSearch.Visit(stmts, n =>
        {
            switch (n)
            {
                case BindStatement { UntoType: { } t } b:
                    _objectDefs[t] = UntoTargetDef(t, "methods") with { Methods = UntoTargetDef(t, "methods").Methods.Append(b).ToList() };
                    break;
                case GetterDeclaration { UntoType: { } t } g:
                    _objectDefs[t] = UntoTargetDef(t, "getters") with { Getters = UntoTargetDef(t, "getters").Getters.Append(g).ToList() };
                    break;
                case SetterDeclaration { UntoType: { } t } s:
                    _objectDefs[t] = UntoTargetDef(t, "setters") with { Setters = UntoTargetDef(t, "setters").Setters.Append(s).ToList() };
                    break;
            }
        });
    private ObjectDefinition UntoTargetDef(string target, string kind) =>
        _objectDefs.TryGetValue(target, out var def) ? def
            : throw new CompilerException($"'unto {target}': {kind} on '{target}' are not yet supported by the compiler (not a plain object type).");

    private void EmitBind(StringBuilder sb, BindStatement bind, string? cName = null)
    {
        // Save and restore _varTypes so function-local names don't pollute
        // the outer scope's type map (and vice versa).
        var saved = new Dictionary<string, CufetType>(_varTypes);
        var savedRet = _currentReturnType;
        var savedExcOpen = _excOpen; _excOpen = 0;   // exc handlers never span function frames
        // When this function is a pipe stage, its `for each from the input` iterator element type was
        // resolved by AnalyzePipes; make it available for the body walk (restored after).
        var savedPipeIn = _currentPipeInputElem;
        _currentPipeInputElem = _stageInputElem.TryGetValue(bind.Name, out var pin) ? pin : null;
        _varTypes.Clear();
        SeedSharedConstantTypes();
        _currentReturnType = bind.ReturnType;
        foreach (var (pType, pName) in bind.Parameters)
            _varTypes[pName] = pType;

        sb.AppendLine($"{(cName == null ? EmitFunctionSignature(bind) : EmitSpecFunctionSignature(bind, cName))} {{");
        var savedFrame = EnterFrame(sb, "    ");
        EmitBlock(sb, bind.Body, "    ");
        ExitFrame(savedFrame);
        sb.AppendLine("}");
        sb.AppendLine();

        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
        _currentReturnType = savedRet;
        _excOpen = savedExcOpen;
        _currentPipeInputElem = savedPipeIn;
    }

    // C function names: methods cm_, getters cg_, setters cst_ (cst_ avoids the cs_ series-temp prefix).
    private static string MethodCName(string objName, string methodName) =>
        "cm_" + CIdent(objName) + "_" + CIdent(methodName);
    private static string GetterCName(string objName, string name) =>
        "cg_" + CIdent(objName) + "_" + CIdent(name);
    private static string SetterCName(string objName, string name) =>
        "cst_" + CIdent(objName) + "_" + CIdent(name);

    private GetterDeclaration? GetterFor(string objName, string member) =>
        _objectDefs.TryGetValue(objName, out var d) ? d.Getters.FirstOrDefault(g => g.Name == member) : null;
    private SetterDeclaration? SetterFor(string objName, string member) =>
        _objectDefs.TryGetValue(objName, out var d) ? d.Setters.FirstOrDefault(s => s.Name == member) : null;

    private void EmitGetter(StringBuilder sb, ObjectDefinition def, GetterDeclaration g)
    {
        var saved = new Dictionary<string, CufetType>(_varTypes);
        var savedRecv = _methodReceiverType;
        var savedRet = _currentReturnType;
        var savedExcOpen = _excOpen; _excOpen = 0;   // exc handlers never span function frames
        _varTypes.Clear();
        SeedSharedConstantTypes();
        _methodReceiverType = def.Name;
        _currentReturnType = g.ReturnType;
        _varTypes["one"] = ObjType(def.Name);
        sb.AppendLine($"{GetterSignature(def, g)} {{");
        var savedGF = EnterFrame(sb, "    ");
        EmitBlock(sb, g.Body, "    ");
        ExitFrame(savedGF);
        sb.AppendLine("}");
        sb.AppendLine();
        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
        _methodReceiverType = savedRecv;
        _currentReturnType = savedRet;
        _excOpen = savedExcOpen;
    }

    private void EmitSetter(StringBuilder sb, ObjectDefinition def, SetterDeclaration s)
    {
        var saved = new Dictionary<string, CufetType>(_varTypes);
        var savedRecv = _methodReceiverType;
        var savedSetter = _inSetterForField;
        _varTypes.Clear();
        SeedSharedConstantTypes();
        _methodReceiverType = def.Name;
        _inSetterForField   = s.Name;                 // one's <name> becomes X → raw write
        _varTypes["one"] = ObjType(def.Name);
        _varTypes[s.ParamName] = s.ParamType;
        sb.AppendLine($"{SetterSignature(def, s)} {{");
        var savedSF = EnterFrame(sb, "    ");
        EmitBlock(sb, s.Body, "    ");
        ExitFrame(savedSF);
        sb.AppendLine("}");
        sb.AppendLine();
        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
        _methodReceiverType = savedRecv;
        _inSetterForField   = savedSetter;
    }

    private string GetterSignature(ObjectDefinition def, GetterDeclaration g) =>
        $"{EmitCType(g.ReturnType)} {GetterCName(def.Name, g.Name)}({ObjStructName(def.Name)}* cv_one)";
    private string SetterSignature(ObjectDefinition def, SetterDeclaration s) =>
        $"void {SetterCName(def.Name, s.Name)}({ObjStructName(def.Name)}* cv_one, {EmitCType(s.ParamType)} {MangleName(s.ParamName)})";

    // Object member READ: getter dispatch, own field, embed handle, or a promoted field
    // reached by walking the embed chain — all resolved statically.
    private string EmitMemberAccess(IExpression target, string member)
    {
        // `<book>'s <member>` — the book's Cufet layer answers first (a getter on the layer
        // object, called on a fresh compound-literal receiver — the layer is fieldless, so an
        // empty receiver is exact); a native constant is the fallback.
        if (target is VariableReference bvr && _bookAliases.TryGetValue(bvr.Name, out var bookName))
            return CufetLayerGetter(bookName, member) is not null
                ? $"{GetterCName(bookName, member)}(&(({EmitCType(ObjType(bookName))}){{0}}))"
                : EmitBookConstant(bookName, member);
        return TypeOf(target) switch
        {
            // the rows/columns of m — named access, resolved by the target's type rather than
            // by reserving the two words.
            MatrixType        => $"cufet_dec_from_ll(({EmitExpr(target)})->{(member == "rows" ? "rows" : "cols")})",
            ObjectType ot     => EmitObjectMemberRead(EmitExpr(target), ot.Name, member),
            // the message of the exception → the saved fault message (arena text).
            ExceptionMarkerType => _currentExcVar ?? throw new CompilerException("'the exception' is only available inside an 'In case of exception' handler."),
        MappingType       => $"{EmitExpr(target)}_{member}",   // the key/value of pair → cv_pair_key/_value
            FailureMarkerType => member == "message" ? $"({EmitExpr(target)}).message" : EmitFailureCategory(EmitExpr(target)),
            RecordType        => $"({EmitExpr(target)}).{MangleName(member)}",   // record field
            // ★ NO catch-all, deliberately. This arm used to be `_ =>` and emitted the record shape
            // for whatever arrived, on the assumption that nothing else could. A union reached it
            // once — see the Judge grouped-arm fix — and the result was C naming a struct member
            // that does not exist: invalid code emitted WITHOUT raising, so `cufet check --native`
            // called the program clean and the only symptom was gcc failing at build time with a
            // message about generated identifiers.
            //
            // Refusing is the whole point. `check --native` reports what the compiler refuses, so a
            // throw here becomes a warning in the editor on the line responsible, while emitting
            // becomes a broken build with nothing to act on. A wrong refusal is a visible bug; a
            // wrong emission is an invisible one.
            var other => throw new CompilerException(
                other is UnionType u
                    ? $"'{member}' cannot be read from a {FormatTypeName(u)} — narrow it to a single " +
                      "case first (`If it is a <case>: …`) and read the member there."
                    : $"reading '{member}' from a {FormatTypeName(other)} is not supported by the compiler."),
        };
    }

    // No native book constants remain — pi and e are getters on math's Cufet layer now
    // (Prelude/math.cufe), routed before this in EmitMemberAccess.
    private static string EmitBookConstant(string bookName, string member) =>
        throw new CompilerException($"book '{bookName}' has no constant '{member}' supported by the compiler.");

    // the category of the failure → voidable text (NULL category → void).
    private string EmitFailureCategory(string failExpr)
    {
        string cvd = RegisterVoidableStruct(new VoidableType(TText));
        return $"(({failExpr}).category ? ({cvd}){{ .has = 1, .val = ({failExpr}).category }} : ({cvd}){{ .has = 0 }})";
    }

    private string EmitObjectMemberRead(string baseExpr, string objName, string member)
    {
        var def = _objectDefs[objName];
        if (GetterFor(objName, member) is not null)
            return $"{GetterCName(objName, member)}(&({baseExpr}))";
        if (def.NamedFields.Any(f => f.FieldName == member) || def.EmbeddedTypeName == member)
            return $"({baseExpr}).{MangleName(member)}";   // own field, or the embed handle
        if (def.EmbeddedTypeName != null)
            return EmitObjectMemberRead($"({baseExpr}).{MangleName(def.EmbeddedTypeName)}", def.EmbeddedTypeName, member);
        throw new CompilerException($"'{objName}' has no member '{member}'.");
    }

    // Object member WRITE: setter dispatch (unless inside that setter for the same field on
    // `one` — the bypass), own field, or a promoted field reached by walking the embed chain.
    private void EmitMemberSet(StringBuilder sb, string indent, IExpression target, string member,
                               IExpression value, int? escapeToDepth = null)
    {
        string baseExpr = EmitExpr(target);
        bool   isRecv   = target is VariableReference { Name: "one" };
        // Widen first (a plain T into a voidable/union slot — the one implicit coercion),
        // then escape-copy the value that is actually stored.
        string val      = EmitEscapeCopy(EmitAsType(value, MemberSetSlotType(TypeOf(target), member, isRecv)),
                                         TypeOf(value), escapeToDepth);                   // ESC.2
        FlushPreEmits(sb, indent);
        string stmt = TypeOf(target) is ObjectType ot
            ? EmitObjectMemberSet(baseExpr, ot.Name, member, val, isRecv)
            : $"({baseExpr}).{MangleName(member)} = {val};";   // record field
        sb.AppendLine($"{indent}{stmt}");
    }

    private string EmitObjectMemberSet(string baseExpr, string objName, string member, string val, bool isReceiver)
    {
        var def = _objectDefs[objName];
        if (SetterFor(objName, member) is not null && !(isReceiver && _inSetterForField == member))
            return $"{SetterCName(objName, member)}(&({baseExpr}), {val});";
        if (def.NamedFields.Any(f => f.FieldName == member) || def.EmbeddedTypeName == member)
            return $"({baseExpr}).{MangleName(member)} = {val};";
        if (def.EmbeddedTypeName != null)
            return EmitObjectMemberSet($"({baseExpr}).{MangleName(def.EmbeddedTypeName)}", def.EmbeddedTypeName, member, val, false);
        throw new CompilerException($"'{objName}' has no member '{member}'.");
    }

    // The DECLARED type of a member write slot — what the value is being stored INTO, so
    // EmitAsType can widen a plain T into it. Mirrors the checker's field resolution: a
    // setter's parameter type when a setter intercepts, else the object's own field, else a
    // promoted field down the embed chain; a record field for a record target. null = nothing
    // to widen into (EmitAsType then falls through to EmitExpr).
    private CufetType? MemberSetSlotType(CufetType? targetType, string member, bool isReceiver) =>
        targetType switch
        {
            ObjectType ot => ObjectMemberSetSlotType(ot.Name, member, isReceiver),
            RecordType rt => rt.NamedFields.FirstOrDefault(f => f.Name == member).Type,
            _             => null,
        };

    private CufetType? ObjectMemberSetSlotType(string objName, string member, bool isReceiver)
    {
        if (!_objectDefs.TryGetValue(objName, out var def)) return null;
        var setter = SetterFor(objName, member);
        if (setter is not null && !(isReceiver && _inSetterForField == member)) return setter.ParamType;
        var own = def.NamedFields.FirstOrDefault(f => f.FieldName == member);
        if (own != default) return own.FieldType;
        if (def.EmbeddedTypeName != null && def.EmbeddedTypeName != member)
            return ObjectMemberSetSlotType(def.EmbeddedTypeName, member, false);
        return null;   // the embed handle itself, or not a field
    }

    // A method's C signature: takes the receiver as a pointer (so mutations to `one`
    // are visible on the caller's object — value-struct-in-place), then its params.
    // `cName` overrides the emitted name (interface specializations); null → the normal method name.
    private string MethodSignature(ObjectDefinition def, BindStatement method, string? cName = null)
    {
        var ps = new List<string> { $"{ObjStructName(def.Name)}* cv_one" };
        ps.AddRange(method.Parameters.Select(p => $"{EmitCType(p.Type)} {MangleName(p.Name)}"));
        return $"{EmitCType(method.ReturnType)} {cName ?? MethodCName(def.Name, method.Name)}({string.Join(", ", ps)})";
    }

    private void EmitMethod(StringBuilder sb, ObjectDefinition def, BindStatement method, string? cName = null)
    {
        var saved = new Dictionary<string, CufetType>(_varTypes);
        var savedRecv = _methodReceiverType;
        var savedRet = _currentReturnType;
        var savedExcOpen = _excOpen; _excOpen = 0;   // exc handlers never span function frames
        _varTypes.Clear();
        SeedSharedConstantTypes();
        _methodReceiverType = def.Name;               // `one` → (*cv_one), resolves fields
        _currentReturnType = method.ReturnType;
        _varTypes["one"] = ObjType(def.Name);
        foreach (var (pType, pName) in method.Parameters)
            _varTypes[pName] = pType;

        sb.AppendLine($"{MethodSignature(def, method, cName)} {{");
        var savedMF = EnterFrame(sb, "    ");
        EmitBlock(sb, method.Body, "    ");
        ExitFrame(savedMF);
        sb.AppendLine("}");
        sb.AppendLine();

        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
        _methodReceiverType = savedRecv;
        _currentReturnType = savedRet;
        _excOpen = savedExcOpen;
    }

    private string EmitExpr(IExpression expr) => expr switch
    {
        NumberLiteral n       => EmitNumberLiteral(n.Value),
        BitsLiteral b         => $"(CufetBits){{ {b.Value}ULL, '{b.Base}', {b.Width} }}",
        BitsShift bs          => $"cufet_bits_shift({EmitExpr(bs.Target)}, {EmitExpr(bs.Amount)}, {(bs.Left ? 1 : 0)}, {bs.Line})",
        BitsConvert bc        => $"cufet_bits_from_number({EmitExpr(bc.Target)}, '{bc.ToBase}', {bc.Line})",
        BooleanLiteral bl     => bl.Value ? "1" : "0",
        StringLiteral s       => EscapeStringLiteral(s.Value),   // text-as-stored-data: static C string
        UnaryExpression u     => EmitUnary(u),
        BinaryExpression b    => EmitBinary(b),
        // `the failure` (in a handler) → the caught CufetFailure; `one` (in a method) → the
        // deref'd receiver; a flow-narrowed voidable var reads as its inner value (`.val`).
        VariableReference v   => v.Name == "the failure" && _currentFailVar != null ? _currentFailVar
                                : v.Name == "one" && _methodReceiverType != null ? "(*cv_one)"
                                : v.Name == "input" ? "stdin"   // `the input` = the stdin stream
                                : _narrowedVars.TryGetValue(v.Name, out var nacc) ? $"({MangleName(v.Name)}){nacc.Access}"
                               // A recursive nested Bind's own name as a VALUE → its closure over the current env.
                                : _closureSelf is { } cse && v.Name == cse.Name ? $"({RegisterFuncStruct(cse.Type)}){{ .fn = {cse.ClosFn}, .env = cf_envp }}"
                               // A bare named function used as a VALUE → the {fn, NULL} closure value.
                                : _funcTypes.ContainsKey(v.Name) && !_varTypes.ContainsKey(v.Name) ? EmitNamedFunctionValue(v.Name)
                               // ★ A DECLARED axiom used as a VALUE → its {thunk, NULL} struct. The
                               // binding itself still emits nothing; the value is built here, where
                               // it is used. An axiom that arrived through a parameter is not in
                               // this map — it is an ordinary local already holding the struct.
                                : _axiomLiterals.TryGetValue(v.Name, out var axiomSource) ? EmitAxiomValue(axiomSource, v.Line)
                               // A deferred module name as a VALUE. A module carries no state — the
                               // pull emits a zero-initialised struct for exactly this reason — so the
                               // receiver can be built here rather than threaded in from the caller.
                                : !_varTypes.ContainsKey(v.Name) && DeferredModuleType(v.Name) is { } dm
                                    ? $"({EmitCType(dm)}){{0}}"
                                : MangleName(v.Name),
        CastExpression cast   => EmitCastExpr(cast),
        LambdaLiteral lam     => EmitLambda(lam),
        SeriesLiteral sl      => EmitSeriesLiteral(sl),
        RangeExpression re    => EmitRangeSeries(re),
        SeriesLength sl2      => $"cufet_dec_from_ll(({EmitExpr(sl2.Series)})->len)",
        SeriesAccess sa       => EmitSeriesAccess(sa),
        RecordLiteral rl      => EmitRecordLiteral(rl),
        BitsAtWidth baw       => $"cufet_bits_at_width({EmitExpr(baw.Target)}, {EmitExpr(baw.Width)}, {baw.Line})",
        RecordNamedAccess { FieldName: "width" } bwa when TypeOf(bwa.Record) is BitsType
                                => $"cufet_dec_from_ll(({EmitExpr(bwa.Record)}).width)",
        RecordNamedAccess rna => EmitMemberAccess(rna.Record, rna.FieldName),
        ObjectLiteral ol      => EmitObjectLiteral(ol),
        PossessiveAccess pa   => EmitMemberAccess(pa.Target, pa.Member),
        ButVoidDefault bvd    => EmitButVoidDefault(bvd),
        ConditionalExpression ce => EmitConditional(ce),
        MapLiteral ml         => EmitMapLiteral(ml),
        MapLookup mlk         => $"{MapName(mlk.Map)}_get({EmitExpr(mlk.Map)}, {EmitExpr(mlk.Key)})",
        MapHasKey mhk         => $"{MapName(mhk.Map)}_has({EmitExpr(mhk.Map)}, {EmitExpr(mhk.Key)})",
        MapHasEntry mhe       => $"{MapName(mhe.Map)}_has_entry({EmitExpr(mhe.Map)}, {EmitExpr(mhe.Key)})",
        MapSize ms            => $"cufet_dec_from_ll(({EmitExpr(ms.Map)})->len)",
        FailureFallback ff    => EmitFailureFallback(ff),
        FailurePropagate fp   => EmitFailurePropagate(fp),
        TextJoin tj           => $"cufet_str_concat({EmitExpr(tj.Left)}, {EmitExpr(tj.Right)})",
        TextConvert tc        => EmitTextConvert(tc),
        NumberConvert nc      => EmitNumberConvert(nc),
        TextLength tl         => $"cufet_dec_from_ll((long long)cufet_u8_len({EmitExpr(tl.Target)}))",
        ForeignTextAt fta     => EmitForeignTextAt(fta),
        TextContains tcn      => $"(strstr({EmitExpr(tcn.Text)}, {EmitExpr(tcn.Substring)}) != NULL)",
        TextFind tf           => EmitTextFind(tf),
        TextSubstringRange r  => $"cufet_str_range({EmitExpr(r.Text)}, cufet_to_int({EmitExpr(r.From)}), {(r.To != null ? $"cufet_to_int({EmitExpr(r.To)})" : "-1")}, {r.Line})",
        TextSubstringEdge e   => $"cufet_str_edge({EmitExpr(e.Text)}, cufet_to_int({EmitExpr(e.Count)}), {(e.FromStart ? "1" : "0")})",
        TextReplace rp        => $"cufet_str_replace({EmitExpr(rp.Text)}, {EmitExpr(rp.Old)}, {EmitExpr(rp.New)}, {rp.Line})",
        TextCase tcs          => EmitTextCase(tcs),
        TextTrim tt           => $"cufet_str_trim({EmitExpr(tt.Text)})",
        TextSplit ts          => EmitTextSplit(ts),
        FileReadExpression fr => EmitFileRead(fr),
        RunExpression or PipeExpression => EmitFallibleCheckGoto(EmitRunRaw(expr), RegisterFailableStruct(new FailureType(RunResultRecordType))),
        ChannelCreation cc    => EmitChannelCreation(cc),
        DeliveryExpression de => EmitDelivery(de),
        AwaitedResultExpression are => EmitAwaitedResult(are),
        InterruptRequestedExpression => EmitInterruptRequested(),
        SortExpression sort   => EmitSort(sort),
        MatrixLiteral ml      => EmitMatrixLiteral(ml),
        ChaseLiteral          => EmitChaseLiteral(),
        MatrixSized ms        => EmitMatrixSized(ms),
        MatrixAccess ma       => EmitMatrixAccess(ma),
        RandomNumber rn       => EmitRandomNumber(rn),
        RandomGuess           => EmitRandomGuess(),
        RandomItem ri         => EmitRandomItem(ri),
        RandomlyShuffled rs   => EmitRandomlyShuffled(rs),
        EnvironmentVariableExpression env => EmitEnvVar(env),
        CurrentDirectoryExpression        => EmitCurrentDirectory(),
        IsTypeCheck tc        => EmitIsTypeCheck(tc),
        DirectoryContentsExpression dce =>
            EmitFallibleCheckGoto(EmitDirRaw(dce), RegisterFailableStruct(new FailureType(new SeriesType(TText)))),
        ReadExpression re     => EmitReadExpr(re),
        PathCheckExpression pc => pc.Kind switch
        {
            PathCheckKind.Exists      => $"cufet_path_exists({EmitExpr(pc.Path)})",
            PathCheckKind.IsDirectory => $"cufet_path_is_dir({EmitExpr(pc.Path)})",
            PathCheckKind.IsFile      => $"cufet_path_is_file({EmitExpr(pc.Path)})",
            _ => throw new CompilerException($"unknown path check {pc.Kind}"),
        },
        // ★ `unbury s` IS `cast s on ()` — a stash lowers to a closure, so resuming one is calling
        // it. No machinery of its own; the closure call path already exists. (An unbury legitimately
        // survives the transform: only the burying FUNCTION is rewritten, never its call sites.)
        UnburyExpression ub   => EmitExpr(new CastExpression(ub.Stash, [], ub.Line, ub.Column)),
        // A bare `void` only has meaning where a voidable is expected (return/becomes/args,
        // handled by EmitAsType) or as an `is void` operand (handled in EmitBinary).
        VoidLiteral           => throw new CompilerException("'void' is only valid where a voidable value is expected."),
        // A failure literal only has meaning where a T-or-failure is expected (return/coercion).
        FailureLiteral        => throw new CompilerException("'a failure' is only valid where a 'T or failure' is expected (e.g. a return)."),
        _ => throw new CompilerException(
                $"'{NodeName(expr)}' expressions are not yet supported by the compiler.")
    };

    // Coerces `expr` to `target`, performing the language's one implicit coercion: widening
    // a plain T (or a bare `void`) into a voidable tagged struct when the target is voidable.
    private string EmitAsType(IExpression expr, CufetType? target)
    {
        // Widening a member value into a closed union: set the tag + store into that case's payload.
        // The widening site is always statically typed, so the tag is known at compile time.
        if (target is UnionType uo && uo.Cases == null)
        {
            var eo = TypeOf(expr);
            if (eo is UnionType { Cases: null }) return EmitExpr(expr);   // already the open union
            if (eo is UnionType) throw new CompilerException(
                "storing a closed catalogue's value into an OPEN catalogue is not supported — the two " +
                "use different tag sets, so the value would need re-tagging at runtime. Narrow to a " +
                "concrete case first (`If v is a number: …`) and store that.");
            int ko = OpenUnionIndex(eo, register: _discoveringOpenUnion);
            if (ko < 0) ko = 0;                                      // pre-pass discovers; real pass finds it
            return $"(({OpenUnionStruct}){{ .tag = {ko}, .val.c{ko} = {EmitExpr(expr)} }})";
        }
        if (target is UnionType ut && ut.Cases != null)
        {
            string cun = RegisterUnionStruct(ut);
            var et0 = TypeOf(expr);
            if (et0 is UnionType eu)
            {
                // Same union (after flattening) ⇒ same struct ⇒ pass through. A DIFFERENT union
                // (a narrower one widening into a wider) has a different tag set, so it would need
                // a runtime re-tag — refuse cleanly rather than emit a mismatched struct.
                if (TypeSig(eu) == TypeSig(ut)) return EmitExpr(expr);
                throw new CompilerException(
                    "storing one catalogue's value into a catalogue with different cases is not " +
                    "supported — the tag sets differ, so the value would need re-tagging at runtime. " +
                    "Narrow to a concrete case first (`If v is a number: …`) and store that.");
            }
            int k = UnionCaseIndex(ut, et0);
            if (k < 0)
                throw new CompilerException(
                    $"a value of type '{(et0 == null ? "unknown" : FormatTypeName(et0))}' is not one of this catalogue's declared cases.");
            return $"(({cun}){{ .tag = {k}, .val.c{k} = {EmitExpr(expr)} }})";
        }
        if (target is VoidableType vt)
        {
            string cvd = RegisterVoidableStruct(vt);
            if (expr is VoidLiteral)          return $"(({cvd}){{ .has = 0 }})";
            // ★ LOAD-BEARING: passing an already-voidable value straight through is right only
            // because a voidable cannot nest — there is no outer layer left to wrap it into. That
            // is guaranteed at the type, by VoidableType's constructor, and not by this line; if
            // that normalisation were ever removed this would hand back a cvd_<inner> where a
            // cvd_<outer> is wanted, silently. It did exactly that once, and gcc caught it after
            // `check --native` had already passed.
            if (TypeOf(expr) is VoidableType) return EmitExpr(expr);                     // already voidable
            // Recursively, because the inner type may itself need widening: `voidable (A or B)`
            // holding an `A` has to become the UNION struct before it goes in .val, not the bare
            // object. Emitting the bare one type-checked and then failed in gcc with
            // "incompatible types when initializing".
            return $"(({cvd}){{ .has = 1, .val = {EmitAsType(expr, vt.Inner)} }})";      // widen T → voidable
        }
        if (target is FailureType ft)
        {
            string cfl = RegisterFailableStruct(ft);
            if (expr is FailureLiteral fl)   // a failure "msg" [of category "cat"]
            {
                string msg = EmitExpr(fl.Message);
                string cat = fl.Category != null ? EmitExpr(fl.Category) : "NULL";
                return $"(({cfl}){{ .is_failure = 1, .message = {msg}, .category = {cat} }})";
            }
            var et = TypeOf(expr);
            if (et is FailureType) return EmitExpr(expr);                                // already failable
            if (et is FailureMarkerType)                                                // re-propagate `the failure`
            {
                string f = EmitExpr(expr);
                return $"(({cfl}){{ .is_failure = 1, .message = ({f}).message, .category = ({f}).category }})";
            }
            // Recursive for the same reason as the voidable arm above: `(A or B) or failure`
            // returning an `A` must tag it into the union first. This is the shape every
            // recursive-descent parser has, and it is what examples/parsing/recursivedescent.cufe hit.
            return $"(({cfl}){{ .is_failure = 0, .val = {EmitAsType(expr, ft.Inner)} }})";  // widen T → success
        }
        return EmitExpr(expr);
    }

    // `<voidable> but void is <default>` → the value if present, else the default. The
    // voidable is bound to a temp (single eval); the default is lazy (only in the else arm).
    // `X when C, otherwise Y` — the result type. Mirrors TypeChecker.InferConditional, which is
    // the source of truth: both arms when they agree, the flattened union of the two when they do
    // not. Deduplicating by signature is what keeps `(number or text) when C, otherwise number`
    // from producing a three-case union with `number` in it twice.
    private CufetType ConditionalType(ConditionalExpression ce)
    {
        var valueType = TypeOf(ce.Value);
        var altType   = TypeOf(ce.Alternative);

        var cases = new List<CufetType>();
        var seen  = new HashSet<string>();
        foreach (var part in new[] { valueType, altType })
            foreach (var one in part is UnionType { Cases: not null } u ? u.Cases : [part])
                if (seen.Add(TypeSig(one))) cases.Add(one);

        return cases.Count == 1 ? cases[0] : new UnionType(cases);
    }

    // ★ A C ternary, so exactly ONE arm runs — the same guarantee the interpreter gives. Both arms
    // are coerced to the RESULT type so they agree: when the arms differ the result is a union, and
    // a bare case value in one arm would otherwise give gcc "type mismatch in conditional
    // expression". Same rule EmitButVoidDefault and EmitFailureFallback follow, for the same reason.
    private string EmitConditional(ConditionalExpression ce)
    {
        var resultType = TypeOf(ce);
        string cond = EmitExpr(ce.Condition);
        return $"({cond} ? {EmitAsType(ce.Value, resultType)} : {EmitAsType(ce.Alternative, resultType)})";
    }

    private string EmitButVoidDefault(ButVoidDefault bvd)
    {
        var vt = (VoidableType)TypeOf(bvd.Voidable);
        string cvd = RegisterVoidableStruct(vt);
        string voidableExpr = EmitExpr(bvd.Voidable);
        string tmp = $"cf_bv{_freshId++}";
        _preEmits.Add($"{cvd} {tmp} = {voidableExpr};");
        // The default must be coerced to the voidable's INNER type so both ternary arms agree — e.g.
        // an atlas lookup yields `voidable <union>`, so a plain `0` default widens into the union.
        return $"({tmp}.has ? {tmp}.val : {EmitAsType(bvd.Default, vt.Inner)})";
    }

    // <fallible> but on failure <default> — the success value, else the default (lazy).
    private string EmitFailureFallback(FailureFallback ff)
    {
        var (cflName, rawExpr) = EmitFallibleRaw(ff.Fallible, TypeOf(ff));
        string tmp = $"cf_ff{_freshId++}";
        _preEmits.Add($"{cflName} {tmp} = {rawExpr};");
        // The default is coerced to the SUCCESS type so both ternary arms agree — the same rule
        // EmitButVoidDefault follows, and for the same reason: when the success type is a union,
        // a bare case value as the default gives gcc "type mismatch in conditional expression".
        return $"({tmp}.is_failure ? {EmitAsType(ff.Default, TypeOf(ff))} : {tmp}.val)";
    }

    // Flattens `run A | run B | run C` into its stages (all must be `run` — the interpreter only
    // handles all-subprocess pipes in expression position; task pipes are the concurrency arc).
    private List<RunExpression> FlattenPipeStages(PipeExpression pipe)
    {
        var stages = new List<RunExpression>();
        void Walk(IExpression e)
        {
            if (e is PipeExpression p) { Walk(p.Left); Walk(p.Right); }
            else if (e is RunExpression r) stages.Add(r);
            else throw new CompilerException("only 'run … | run …' subprocess pipes are supported as a value (task pipes are deferred to the concurrency arc).");
        }
        Walk(pipe);
        return stages;
    }

    // Whole-program pipe analysis: for every task pipe, propagate element types left-to-right (each
    // stage's input = the previous stage's output type) so a `for each x from the input` inside a
    // stage has a concrete C element type. A pure producer's input is null; every downstream stage's
    // input is its predecessor's output (defaulting to number when the output type can't be inferred,
    // preserving the number-only behavior). A function reused at two positions with conflicting input
    // element types is a clean CompilerException (the one-input-type-per-stage restricted form).
    private void AnalyzePipes(Program program)
    {
        var binds = new Dictionary<string, BindStatement>();
        void Collect(IReadOnlyList<IStatement> stmts)
        {
            foreach (var s in stmts)
            {
                if (s is BindStatement b && b.UntoType == null) binds[b.Name] = b;
                if (s is PullStatement ps) Collect(ps.Body);
                if (s is BindStatement nb) Collect(nb.Body);   // nested Binds can be pipe stages too
            }
        }
        Collect(program.Statements);
        foreach (var kv in binds) _namedFuncBodies[kv.Key] = kv.Value;   // for lambda-pipe element inference

        var pipes = new List<List<IExpression>>();
        void Scan(IReadOnlyList<IStatement> stmts)
        {
            foreach (var s in stmts)
            {
                if (s is PipeExpression pe && !FlattenPipeAll(pe).TrueForAll(x => x is RunExpression))
                    pipes.Add(FlattenPipeAll(pe));
                switch (s)
                {
                    case IfStatement iff:
                        foreach (var a in iff.Arms) Scan(a.Body);
                        if (iff.ElseBody != null) Scan(iff.ElseBody); break;
                    case WhileStatement w: Scan(w.Body); break;
                    case RepeatUntilStatement ru: Scan(ru.Body); break;
                    case ForEachStatement fe: Scan(fe.Body); break;
                    case PullRabbitStatement pr: Scan(pr.Body); break;
                    case PullStatement pl: Scan(pl.Body); break;
                    case BindStatement bd: Scan(bd.Body); break;
                    case TryStatement t: Scan(t.Body); if (t.FailureHandler != null) Scan(t.FailureHandler); break;
                    case WithOpenStatement wo: Scan(wo.Body); break;
                    case LaunchTaskStatement lt: Scan(lt.Body); break;
                }
            }
        }
        Scan(program.Statements);

        static bool TypeEq(CufetType? a, CufetType? b) => (a, b) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            _ => a!.Equals(b),
        };
        void SetInput(string name, CufetType? elem)
        {
            if (_stageInputElem.TryGetValue(name, out var prev))
            {
                if (!TypeEq(prev, elem))
                    throw new CompilerException(
                        $"pipe stage '{name}' is used with two different input element types — a function can be a pipe stage for one element type only (this slice).");
            }
            else _stageInputElem[name] = elem;
        }

        foreach (var stages in pipes)
        {
            CufetType? cur = null;   // input to the first stage (a producer) is nothing
            for (int i = 0; i < stages.Count; i++)
            {
                bool last = i == stages.Count - 1;
                switch (stages[i])
                {
                    case VariableReference vr:   // a NAMED stage records its input; propagate its output
                        SetInput(vr.Name, cur);
                        if (!last) cur = binds.TryGetValue(vr.Name, out var b) ? (InferStageOutput(b, cur) ?? TNumber) : null;
                        break;
                    case LambdaLiteral lam:      // a lambda has no name to record, but MUST propagate its
                        if (!last) cur = InferStageOutput(lam.Parameters, lam.Body, cur) ?? TNumber;  // output so a NAMED stage after it gets the right input type
                        break;
                    default: i = stages.Count; break;   // stop at anything else
                }
            }
        }
    }

    private CufetType? InferStageOutput(BindStatement bind, CufetType? inputElem) =>
        InferStageOutput(bind.Parameters, bind.Body, inputElem);

    // Infers a pipe stage's OUTPUT element type = the type of its first `output <expr>` (with the
    // input iterator bound to `inputElem`). Best-effort over the common stage shapes; returns null
    // (→ caller defaults to number) if it can't be determined. Works for named stages and lambda
    // stages (same body shape). Saves/restores _varTypes.
    private CufetType? InferStageOutput(IReadOnlyList<(CufetType Type, string Name)> parameters, IReadOnlyList<IStatement> body, CufetType? inputElem)
    {
        var saved = new Dictionary<string, CufetType>(_varTypes);
        _varTypes.Clear();
        SeedSharedConstantTypes();
        foreach (var (pt, pn) in parameters) _varTypes[pn] = pt;
        CufetType? outType = null;
        void Walk(IReadOnlyList<IStatement> stmts)
        {
            foreach (var s in stmts)
            {
                if (outType != null) return;
                try
                {
                    switch (s)
                    {
                        case DefineStatement d:
                            var dt = TypeOf(d.Value); if (dt != null) _varTypes[d.Name] = dt; break;
                        case OutputStatement os: outType = TypeOf(os.Value); return;
                        case ForEachFromInputStatement fi:
                            if (inputElem != null) _varTypes[fi.IteratorName] = inputElem;
                            Walk(fi.Body); break;
                        case ForEachStatement fe: Walk(fe.Body); break;
                        case IfStatement iff:
                            foreach (var a in iff.Arms) Walk(a.Body);
                            if (iff.ElseBody != null) Walk(iff.ElseBody); break;
                        case WhileStatement w: Walk(w.Body); break;
                        case RepeatUntilStatement ru: Walk(ru.Body); break;
                    }
                }
                catch (CompilerException) { /* best-effort — leave outType null */ }
            }
        }
        Walk(body);
        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
        return outType;
    }

    // Flattens a pipe into ALL its stage expressions (left-associative), without the all-`run`
    // restriction — used to tell a task pipe (function stages) from a subprocess pipe.
    private static List<IExpression> FlattenPipeAll(PipeExpression pipe)
    {
        var stages = new List<IExpression>();
        void Walk(IExpression e)
        {
            if (e is PipeExpression p) { Walk(p.Left); Walk(p.Right); }
            else stages.Add(e);
        }
        Walk(pipe);
        return stages;
    }

    // `for each <name> from the input: … Done.` — a pipe-stage consumer loop. Drains the stage's
    // (thread-local) input channel until it is closed-and-empty (recv → void), binding each value
    // to the iterator. Same shape as the delivery loop, over the implicit `cufet_pipe_in`. Numbers
    // only this slice. Stop → break, Skip → continue (the loop is a plain C `for(;;)`).
    private void EmitForEachFromInput(StringBuilder sb, ForEachFromInputStatement fi, string indent)
    {
        _usesConcurrency = true;
        var elem = _currentPipeInputElem ?? TNumber;   // resolved by AnalyzePipes for this stage
        RegisterChanElem(elem, isTop: true);
        string ec = EmitCType(elem);
        string raw   = fi.IteratorName;
        string it    = MangleName(raw);
        string inner = indent + "    ";
        int id = _freshId++;
        sb.AppendLine($"{indent}for (;;) {{");
        sb.AppendLine($"{inner}void* cf_pe{id} = NULL; int cf_ph{id} = cufet_chan_recv(cufet_pipe_in, &cf_pe{id});");
        sb.AppendLine($"{inner}if (cf_ph{id} < 0) {{ cufet_checkpoint(); break; }}");   // interrupted while blocked
        sb.AppendLine($"{inner}if (cf_ph{id} == 0) break;");                            // stream closed → done
        sb.AppendLine($"{inner}{ec} {it} = {ChanArenaCopy(elem)}(cf_pe{id}); {ChanFreeEnv(elem)}(cf_pe{id});");
        var saved = _varTypes.TryGetValue(raw, out var st) ? st : null;
        _varTypes[raw] = elem;
        EmitLoopBody(sb, fi.Body, inner);   // loop-body scope: file/exc/unmaker depths for Stop/Skip + per-iteration unmakers
        if (saved != null) _varTypes[raw] = saved; else _varTypes.Remove(raw);
        sb.AppendLine($"{indent}}}");
    }

    // A bare `s0 | s1 | … | sN.` task pipe (function stages). Each stage runs as its own thread,
    // adjacent stages share a channel (stage i's output = stage i+1's input); a stage closes its
    // output on return, so completion cascades down the pipe. Self-contained + structured: the pipe
    // spawns every stage, JOINS them all, then frees the channels — no enclosing rabbit needed (the
    // interpreter's task pipes are top-level too). Values stream FIFO, so a linear pipe's observable
    // output is deterministic and matches the interpreter's buffered-sequential order.
    private void EmitTaskPipe(StringBuilder sb, List<IExpression> stages, string indent)
    {
        _usesConcurrency = true;
        // Each stage is a CLOSURE VALUE {fn: void(*)(void* env), env}: a named function → {thunk, NULL}
        // (the CL.1 value thunk), a lambda → {cv_clos, env} (CL.2). The runner calls fn(env). Every
        // stage's signature is `void given ()` → the same cfn struct.
        foreach (var st in stages)
            if (!(TypeOf(st) is FunctionType))
                throw new CompilerException("a task-pipe stage must be a function (a named function or a lambda).");

        int n = stages.Count;
        int id = _freshId++;
        string inner = indent + "    ";

        // Element chain: each stage's INPUT element type (null for the producer) = its predecessor's
        // output, inferred forward for named AND lambda stages (so a lambda TEXT-pipe stage streams
        // text, not number). Defaults to number when un-inferrable — the number-only behavior.
        var stageIn = new CufetType?[n];
        CufetType? cur = null;
        for (int i = 0; i < n; i++)
        {
            stageIn[i] = cur;
            if (i < n - 1)
                cur = stages[i] switch
                {
                    VariableReference vr when _namedFuncBodies.TryGetValue(vr.Name, out var b) => InferStageOutput(b, cur) ?? TNumber,
                    LambdaLiteral lam => InferStageOutput(lam.Parameters, lam.Body, cur) ?? TNumber,
                    _ => TNumber,
                };
        }

        sb.AppendLine($"{indent}{{");

        // Materialize each stage's closure value first (a lambda's env alloc adds preemits to flush).
        // For a LAMBDA stage, set the pipe input element type so its `for each from the input` gets the
        // right C element type (a named stage picks it up in EmitBind from _stageInputElem).
        var stageVars = new List<string>();
        for (int i = 0; i < n; i++)
        {
            string cfn = RegisterFuncStruct((FunctionType)TypeOf(stages[i]));
            var savedPipeIn = _currentPipeInputElem;
            if (stages[i] is LambdaLiteral) _currentPipeInputElem = stageIn[i];
            string val = EmitExpr(stages[i]);
            _currentPipeInputElem = savedPipeIn;
            FlushPreEmits(sb, inner);
            string sv = $"cf_pstg{id}_{i}";
            sb.AppendLine($"{inner}{cfn} {sv} = {val};");
            stageVars.Add(sv);
        }

        sb.AppendLine($"{inner}cufet_chan* cf_pch{id}[{n - 1}];");
        // Channel j (stage j → stage j+1) carries stage j+1's input element type; give it that type's
        // freeval so pending envelopes free on teardown.
        for (int j = 0; j < n - 1; j++)
        {
            var chElem = stageIn[j + 1] ?? TNumber;
            RegisterChanElem(chElem, isTop: true);
            sb.AppendLine($"{inner}cf_pch{id}[{j}] = cufet_chan_new({ChanFreeEnv(chElem)});");
        }
        sb.AppendLine($"{inner}pthread_t cf_pth{id}[{n}];");
        for (int i = 0; i < n; i++)
        {
            string inCh  = i == 0     ? "NULL" : $"cf_pch{id}[{i - 1}]";
            string outCh = i == n - 1 ? "NULL" : $"cf_pch{id}[{i}]";
            sb.AppendLine($"{inner}{{ cufet_pipe_arg* cf_pa = (cufet_pipe_arg*)malloc(sizeof(cufet_pipe_arg));");
            sb.AppendLine($"{inner}  cf_pa->in = {inCh}; cf_pa->out = {outCh}; cf_pa->fn = {stageVars[i]}.fn; cf_pa->env = {stageVars[i]}.env;");
            sb.AppendLine($"{inner}  pthread_create(&cf_pth{id}[{i}], NULL, cufet_pipe_stage, cf_pa); }}");
        }
        sb.AppendLine($"{inner}for (int cf_pj = 0; cf_pj < {n}; cf_pj++) pthread_join(cf_pth{id}[cf_pj], NULL);");
        sb.AppendLine($"{inner}for (int cf_pf = 0; cf_pf < {n - 1}; cf_pf++) cufet_chan_free(cf_pch{id}[cf_pf]);");
        sb.AppendLine($"{indent}}}");
    }

    // Builds the raw fallible run result (a `cfl` of the run-result record). A single `run` runs the
    // command; a pipe runs the stages buffered-sequentially, chaining stdout → next stdin (matching
    // the interpreter): aggregated stderr, rightmost-nonzero exit (pipefail), final stdout. A LAUNCH
    // failure of any stage becomes the `or failure`; a ran-but-nonzero command is a success record.
    private string EmitRunRaw(IExpression expr)
    {
        _usesProcess = true;
        string cr   = RegisterRecordStruct(RunResultRecordType);
        string cfl  = RegisterFailableStruct(new FailureType(RunResultRecordType));
        string fErr = MangleName("errors"), fExit = MangleName("exit-code"), fOut = MangleName("output");
        int id = _freshId++;
        string raw = $"cf_run{id}";
        var stages = expr is PipeExpression pipe ? FlattenPipeStages(pipe) : new List<RunExpression> { (RunExpression)expr };

        // Emit each stage's program + argv temps as separate preemit lines first (their operands
        // may add their own preemits), then the one run/chain block referencing those temps.
        var progVars = new List<string>();
        for (int s = 0; s < stages.Count; s++)
        {
            string pg = $"cf_pg{id}_{s}";
            _preEmits.Add($"const char* {pg} = {EmitExpr(stages[s].Program)};");
            EmitRunArgv(pg, stages[s].Args, stages[s].ArgsSeries, $"cf_av{id}_{s}");
            progVars.Add(pg);
        }

        var b = new StringBuilder();
        b.Append($"{cfl} {raw} = {{0}}; {{ const char* cf_so{id}; const char* cf_se{id}; int cf_ex{id}; CufetFailure cf_e{id}; ");
        if (stages.Count == 1)
        {
            b.Append($"if (cufet_run_capture({progVars[0]}, cf_av{id}_0, NULL, &cf_so{id}, &cf_se{id}, &cf_ex{id}, &cf_e{id})) {{ ");
            b.Append($"{raw}.is_failure = 0; {raw}.val = ({cr}){{ .{fErr} = cf_se{id}, .{fExit} = cufet_dec_from_ll(cf_ex{id}), .{fOut} = cf_so{id} }}; ");
            b.Append($"}} else {{ {raw}.is_failure = 1; {raw}.message = cf_e{id}.message; {raw}.category = cf_e{id}.category; }} ");
        }
        else
        {
            b.Append($"const char* cf_cur{id} = NULL; const char* cf_eagg{id} = \"\"; int cf_code{id} = 0; int cf_ok{id} = 1; ");
            for (int s = 0; s < stages.Count; s++)
            {
                b.Append($"if (cf_ok{id}) {{ if (cufet_run_capture({progVars[s]}, cf_av{id}_{s}, cf_cur{id}, &cf_so{id}, &cf_se{id}, &cf_ex{id}, &cf_e{id})) {{ ");
                b.Append($"cf_eagg{id} = cufet_str_concat(cf_eagg{id}, cf_se{id}); if (cf_ex{id} != 0) cf_code{id} = cf_ex{id}; cf_cur{id} = cf_so{id}; ");
                b.Append($"}} else {{ {raw}.is_failure = 1; {raw}.message = cf_e{id}.message; {raw}.category = cf_e{id}.category; cf_ok{id} = 0; }} }} ");
            }
            b.Append($"if (cf_ok{id}) {{ {raw}.is_failure = 0; {raw}.val = ({cr}){{ .{fErr} = cf_eagg{id}, .{fExit} = cufet_dec_from_ll(cf_code{id}), .{fOut} = cf_cur{id} ? cf_cur{id} : \"\" }}; }} ");
        }
        b.Append("}");
        _preEmits.Add(b.ToString());
        return raw;
    }
}
