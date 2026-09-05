using System.Text;
using Cufet.Interpreter;
using Cufet.Lexer;

namespace Cufet.Compiler;

public sealed partial class CodeGenerator
{
    // What codegen found that is worth saying but does not stop the build. A refusal is still
    // thrown; this is only ever added to. Read it after Generate returns.
    public DiagnosticBag Diagnostics { get; } = new();

    // The program being generated, for the whole-program questions (see CaptureWriteIsObservable).
    private Program? _program;

    private int _forCounter;
    private int _freshId;
    // Side-channel for pre-emit statements (e.g. series literal construction).
    // Callers must call FlushPreEmits before emitting the final statement line.
    private readonly List<string> _preEmits = new();
    // Full static type of each in-scope variable, so declarations, print dispatch,
    // and struct synthesis all key off the real Cufet type (not just a coarse tag).
    private readonly Dictionary<string, CufetType> _varTypes = new();
    // Return type of each top-level function (null = void), so CastExpression is typed.
    private readonly Dictionary<string, CufetType?> _funcReturnTypes = new();
    // Full FunctionType (params + return) of each named function, so a bare function name used as a
    // VALUE (Define f as grade, passing/returning a function) resolves to its FunctionType and cfn struct.
    private readonly Dictionary<string, FunctionType> _funcTypes = new();

    // Closure-value support (CL.1 — the {fn, env} substrate, no capture yet). A FunctionType lowers to
    // a uniform value struct `cfn_N { ret (*fn)(void* env, params…); void* env; }` (one per distinct
    // signature, deduped by TypeSig) — fits a fixed slot, copies by value. A named function used as a
    // value is wrapped once in a thunk `cv_<name>__fnthunk(void* env, params…)` (ignores env) so the
    // real function keeps its plain signature and direct calls stay unchanged. env is NULL until CL.2.
    private readonly Dictionary<string, string> _funcStructSig2Name = new();
    private readonly List<(string Name, FunctionType Type)> _funcStructs = new();
    private int _funcStructCounter;
    private readonly SortedSet<string> _fnThunks = new();   // named functions needing a value thunk

    // CL.2 — closure captures. Each lambda / capturing nested Bind lowers to a top-level C function
    // `cv_clos<id>(void* env, params…)` (accumulated in _closureFns) plus, when it captures, an env
    // struct `cenv_<id> { <free vars> }` (accumulated in _closureEnvs). The env is arena-allocated at
    // the closure-creation site and populated with the free vars (value types by value → snapshot,
    // region types as the shared pointer → share: the binding-is-binding policy IS value-struct field
    // storage). The closure value is `(cfn_N){ .fn = cv_clos<id>, .env = <env or NULL> }`.
    private readonly StringBuilder _closureFns = new();
    private readonly StringBuilder _closureEnvs = new();
    private int _closureCounter;
    // Inside a recursive nested Bind's body, its own name resolves to a SELF-CALL of the closure fn
    // reusing the current env (`cv_clos<id>(cf_envp, args)`) — recursion by-name, matching the
    // interpreter (the name is in scope in its own body). Saved/restored around nested closures.
    private (string Name, string ClosFn, FunctionType Type)? _closureSelf;

    // Record struct registry: canonical shape signature → C struct name (cr_N).
    // Records are structural (anonymous), so each distinct shape gets one synthesized
    // C struct; the canonical signature dedups shapes that are structurally equal.
    private readonly Dictionary<string, string> _recordSig2Name = new();
    private readonly List<(string Name, RecordType Type)> _recordStructs = new();
    private int _recordCounter;

    // Voidable struct registry — one tagged struct `cvd_N { int has; T val; }` per distinct
    // inner type. Synthesized exactly like record structs (per-type, with _write/_eq helpers).
    private readonly Dictionary<string, string> _voidableSig2Name = new();
    private readonly List<(string Name, CufetType Inner)> _voidableStructs = new();
    private int _voidableCounter;

    // CAT.1 — closed unions: `cun_N { int tag; union { <case_k> c<k>; } val; }`, one per distinct
    // closed UnionType (TypeSig-deduped, folded into the EmitStructs topo pass). This is the N-case
    // GENERALIZATION of the voidable tagged struct (`cvd_N {int has; T val;}` is literally the 2-case
    // instance): the tag says which case, the payload holds it; `is a <case>` reads the tag (a genuine
    // RUNTIME type check), and narrowing exposes `.val.c<k>` at the case's concrete type.
    private readonly Dictionary<string, string> _unionSig2Name = new();
    private readonly List<(string Name, UnionType Type)> _unionStructs = new();
    private int _unionCounter;

    // CAT.2 — OPEN unions (`a catalogue with (…)`). ★ ALL open unions are ONE front-end type
    // (`UnionType.Open`: Cases == null, and Equals returns true for ANY two opens — MEASURED to be
    // observable: two differently-populated open catalogues are interchangeable, `a2 becomes a1` and
    // both pass to `given (the catalogue c)`). So a per-location case set would be UNSOUND; there is
    // exactly ONE `cun_open` over the BOUNDED whole-program set of concrete types ever widened into an
    // open union. The set is filled by a DISCOVERY PRE-PASS (the body pass run once into a throwaway)
    // so it is COMPLETE before any `is a` tag check is emitted — function bodies emit before main, so
    // an `is a T` in a function would otherwise precede main's `Add T`.
    private readonly Dictionary<string, int> _openUnionIndex = new();
    private readonly List<CufetType> _openUnionCases = new();
    private bool _discoveringOpenUnion;   // true during the pre-pass (registration allowed)
    private const string OpenUnionStruct = "cun_open";

    // Index of `t` in the global open-union case set; registers it during the discovery pre-pass.
    // Returns -1 when the type was never widened into an open union (⇒ `is a t` is statically false).
    private bool _usesOpenUnion;
    private string MarkOpenUnion() { _usesOpenUnion = true; return OpenUnionStruct; }

    // Clears the APPEND-ONLY emission buffers between discovery iterations so the real pass doesn't
    // duplicate them. The struct/type registries are deliberately NOT cleared — they dedup by TypeSig,
    // so re-running is idempotent (and the shapes discovered are needed either way).
    private void ResetEmissionBuffers()
    {
        _taskFns.Clear(); _closureFns.Clear(); _closureEnvs.Clear(); _preEmits.Clear();
        _openFiles.Clear(); _loopExits.Clear();
        _rabbitCtx.Clear(); _rabbitDepth = 0; _excOpen = 0;
        _varRabbitDepth.Clear(); _closureEscapeDepth = null;
        _narrowedVars.Clear(); _armCases.Clear(); _currentFailVar = null; _closureSelf = null; _currentTaskReturn = null;
        // The channel deep-copy registry IS reset: an OPEN union's TypeSig is the constant "U(*)"
        // regardless of its discovered case set, so a union registered during an early iteration
        // would dedup against a later, LARGER case set and skip registering the new cases'
        // helpers (a dangling cchan_<i>_copy reference). Rebuilding it in the real pass is exact.
        _chanElemReg.Clear(); _chanElemList.Clear(); _chanTopElems.Clear();
        // Interface specializations are DISCOVERED BY EMITTING, so a discovery iteration registers
        // them too — clear the requests/emitted-set so the real pass emits each exactly once.
        _ifaceSpecReq.Clear(); _ifaceSpecDone.Clear(); _ifaceSpecSigs.Clear();
        _scopeDepth = 0; _frameUnmakerBase = null;
        // Registration is per-pass: the discovery iteration and the real one each build their own
        // struct lists, so "already registered" has to be forgotten between them.
        _objectFieldsDone.Clear();
    }

    // Top-level `permanently` bindings, by reference — the shared constants that must live at C
    // file scope so top-level functions can read them. Reference identity, not name: a
    // `permanently` local elsewhere may share a name and stays a local.
    private readonly HashSet<DefineStatement> _sharedConstants =
        new(ReferenceEqualityComparer.Instance as IEqualityComparer<DefineStatement>);
    private readonly List<string> _sharedConstDecls = [];

    // ── Foreign axioms ────────────────────────────────────────────────────
    // One C function per distinct axiom, keyed by the source it wraps so the same axiom returned
    // in two places is pasted once. Collected while bodies are emitted and appended above them.
    //
    // ★ A FUNCTION rather than the text spliced at each use. It gives the boundary check somewhere
    // to live (a `_Static_assert` is a declaration and cannot sit inside an expression), it keeps
    // the foreign text readable in the output, and it is the same artifact the interpreter's shim
    // builds — "the shim is the compiled axioms" holds for both backends because it is one shape.
    private readonly Dictionary<string, string> _axiomFnNames = new(StringComparer.Ordinal);
    private readonly System.Text.StringBuilder _axiomFns = new();

    // Resultless axioms — source, by identity, emitted above the wrappers. Insertion-ordered so two
    // preambles where the second names the first still compile.
    private readonly Dictionary<string, (string Language, string Source)> _axiomPreambles =
        new(StringComparer.Ordinal);

    // The Cufet type of each shared constant, by name. Computed BEFORE any body is emitted,
    // because bodies emit before main — so the `_varTypes[d.Name] = vt` main performs when it
    // assigns the constant comes far too late for a function that reads one.
    private readonly Dictionary<string, CufetType> _sharedConstTypes = new(StringComparer.Ordinal);

    // ★ Re-seeds the shared constants into a freshly cleared _varTypes. Every DETACHED body — a
    // top-level function, a method, a getter, a setter, a destructor, an operator overload, a pipe
    // stage — clears the type map and seeds only its own parameters and receiver. Without this a
    // constant read inside one has no known type and falls back to number: `State greeting` emitted
    // cufet_print_number on a const char* and gcc rejected the file, under the banner "★ This is a
    // bug in the Cufet compiler" — which it was.
    //
    // The set of bodies here is the same one TypeChecker.ImportTopLevelVisible governs. Keep the
    // two in step: a body that may READ a constant must also KNOW ITS TYPE.
    private void SeedSharedConstantTypes()
    {
        foreach (var (name, type) in _sharedConstTypes) _varTypes[name] = type;
    }

    // Does the program mention an OPEN union anywhere (so the discovery pre-pass is worth running)?
    // Walks AST nodes AND their CufetType annotations — an open union can appear only via a type
    // annotation (`a catalogue …` / `an atlas …`), never as a value literal.
    private static bool ProgramUsesOpenUnion(object? node)
    {
        switch (node)
        {
            case null: return false;
            case string: return false;
            case CufetType t: return TypeHasOpenUnion(t);
            case System.Runtime.CompilerServices.ITuple tup:
                for (int i = 0; i < tup.Length; i++) if (ProgramUsesOpenUnion(tup[i])) return true;
                return false;
            case System.Collections.IEnumerable en:
                foreach (var it in en) if (ProgramUsesOpenUnion(it)) return true;
                return false;
            default:
                // Keyed on the NAMESPACE, not on IStatement/IExpression: `ConditionArm` and
                // `JudgeArm` implement neither, so matching the interfaces walked past every `If`
                // arm and every judgement — and an open union first seen inside one would have
                // been left out of the whole-program discovery this pass exists to perform.
                if (node.GetType().Namespace != typeof(IStatement).Namespace) return false;
                foreach (var prop in node.GetType().GetProperties())
                    if (ProgramUsesOpenUnion(prop.GetValue(node))) return true;
                return false;
        }
    }

    private static bool TypeHasOpenUnion(CufetType t) => t switch
    {
        UnionType u => u.Cases == null || u.Cases.Any(TypeHasOpenUnion),
        SeriesType s => TypeHasOpenUnion(s.ElementType),
        MapType m => TypeHasOpenUnion(m.KeyType) || TypeHasOpenUnion(m.ValueType),
        VoidableType v => TypeHasOpenUnion(v.Inner),
        FailureType f => TypeHasOpenUnion(f.Inner),
        _ => false,
    };

    private int OpenUnionIndex(CufetType? t, bool register)
    {
        if (t == null) return -1;
        string sig = TypeSig(t);
        if (_openUnionIndex.TryGetValue(sig, out var i)) return i;
        if (!register) return -1;
        i = _openUnionCases.Count;
        _openUnionIndex[sig] = i;
        _openUnionCases.Add(t);
        RegisterNestedRecords(t);
        return i;
    }

    // Map struct registry — one arena container `cmap_N { K* keys; V* vals; int len, cap; }`
    // per distinct (K,V), an association list with linear scan. Lookup returns voidable V.
    private readonly Dictionary<string, string> _mapSig2Name = new();
    private readonly List<(string Name, CufetType Key, CufetType Value)> _mapStructs = new();
    private int _mapCounter;

    // Series struct registry — one arena container `cser_N { T* data; int len, cap; }` per
    // distinct element type T. Generalizes the former number-only CufetSeries; synthesized like
    // maps (per-type, arena pointer / reference type, forward-declared + runtime after structs).
    private readonly Dictionary<string, string> _seriesSig2Name = new();
    private readonly List<(string Name, CufetType Elem)> _seriesStructs = new();
    private int _seriesCounter;

    // Channel deep-copy registry (channel-of-T): the type-erased channel container is one C struct
    // (cufet_chan, void* node payloads), but crossing a channel boundary needs a per-element-type
    // deep copy — to malloc'd heap on send (decoupled from both threads' arenas), into the receiver's
    // arena on recv, then free the heap bridge. `_chanElemReg` is the full type closure (each gets a
    // recursive `copy`/`freeheap`); `_chanTopElems` is the subset used AT a boundary (each also gets
    // the heapenv/arenacopy/freeenv wrappers). Deduped by TypeSig, mirroring the series/map synthesis.
    private readonly Dictionary<string, int> _chanElemReg = new();
    private readonly List<(int Idx, CufetType T)> _chanElemList = new();
    private readonly HashSet<string> _chanTopElems = new();
    // ESC.2 — types needing an `escapecopy` wrapper (a value stored into longer-lived storage).
    private readonly HashSet<string> _escapeElems = new();
    // The C var holding this frame's BASE arena depth. A rabbit depth `d` (the checker's counter,
    // which resets per frame) maps to arena depth `base + d`, because every `Pull a rabbit` pushes
    // exactly one arena and a plain call pushes none. Set by EnterFrame; null at top level (base 0).
    private string? _frameArenaBase;

    // ── ESC.3 — arenas in the nonlocal-exit cleanup family ─────────────────────
    // Every nonlocal exit already unwinds the other scoped resources through a cleanup prefix
    // (UnmakerRunStmt + FileCleanupStmts + ExcPopStmts). Arenas were the one resource missing from
    // that list, so a return/Stop/Skip/failure-goto out of a `Pull a rabbit` jumped straight past
    // the rabbit's `Done.` pop — which does NOT merely leak: cufet_arena_top is left one level too
    // high, so every later allocation lands in the dead rabbit's arena and, after
    // CUFET_ARENA_MAX_DEPTH such exits, the push writes past the end of cufet_arenas[].
    //
    // `k` levels to unwind = _rabbitDepth − (the rabbit depth recorded where the jump lands), the
    // same shape as ExcPopStmts. Rabbits are the only arena pushers inside a frame, so the count is
    // exact. How each level goes depends on what the jump carries out:
    //   Stop / Skip          — nothing crosses (outward stores were already copied by ESC.1/ESC.2),
    //                          so a true pop: identical to the rabbit's own normal `Done.` exit.
    //   failure-goto /       — only the message/category strings cross; copy them outward first
    //   `pass the failure off` (ArenaStrCopyTo), then a true pop.
    //   return <value>       — an arbitrary value crosses, and it may ALIAS the caller's own data
    //                          (measured: returning a parameter through a rabbit shares in the
    //                          interpreter), so copying would diverge. Merge down instead.
    // ⚠ Was four parallel lists — file depth, exc depth, rabbit depth, unmaker snap — pushed and
    // popped at three different places, and the snap list was pushed only when the program had
    // unmakers, so the four could legitimately hold DIFFERENT LENGTHS. One list of marks cannot.
    private readonly List<CleanupPoint> _loopExits = new();

    // The `cufet_arena_pop(); ` / `cufet_arena_merge_down(); ` run unwinding rabbit regions down to
    // `toDepth`, or "" when the jump stays inside the same region (the common case — zero cost).
    private string ArenaPopStmts(int toDepth, bool merge = false)
    {
        int k = _rabbitDepth - toDepth;
        return k > 0 ? string.Concat(Enumerable.Repeat(merge ? "cufet_arena_merge_down(); " : "cufet_arena_pop(); ", k)) : "";
    }

    // `lvalue = cufet_arena_str_at(<dest>, lvalue); ` — moves an arena-templated failure string out
    // to the arena the jump lands in. Emitted only when arena levels are actually being unwound.
    private string ArenaStrCopyTo(int toDepth, params string[] lvalues) =>
        _rabbitDepth - toDepth <= 0
            ? ""
            : string.Concat(lvalues.Select(v => $"{v} = cufet_arena_str_at({EscapeArenaDepth(toDepth)}, {v}); "));

    // The body of a jump to the enclosing Try's failure handler: record the failure into the Try's
    // caught-failure var, then unwind every scoped resource between here and the Try — unmakers,
    // files, exception pads, and (ESC.3) rabbit arenas. The message/category move outward FIRST,
    // because the I/O error bridge templates them into the very arena about to be released.
    // Every failure-goto site shares this one definition so none can drift out of step with it —
    // three of the four were already out of step when arenas were added.
    private string FailureGotoBody(
        (string Label, string FailVar, CleanupPoint Exit) h,
        string msgLvalue, string catLvalue) =>
        $"{ArenaStrCopyTo(h.Exit.RabbitDepth, msgLvalue, catLvalue)}{h.FailVar}.message = {msgLvalue}; {h.FailVar}.category = {catLvalue}; " +
        $"{UnwindTo(h.Exit)}goto {h.Label};";

    // True when a returned value can point into an arena that the return's unwinding would free —
    // either the value itself is region-bearing, or it is fallible and carries an arena-templated
    // message. Such a return merges its regions down instead of popping them.
    private static bool ReturnCarriesArenaData(CufetType? t) =>
        t is FailureType || TypeChecker.IsRegionBearing(t);

    // Pipe-stage input element types (channel-of-T text/reference pipes). A `for each x from the
    // input` needs a concrete C type for x; the stream type is implicit in the grammar, so it's
    // inferred by a whole-program pipe pass (each stage's input = the previous stage's output type).
    // A function used at two positions with conflicting element types is a clean CompilerException.
    private readonly Dictionary<string, CufetType?> _stageInputElem = new();
    private CufetType? _currentPipeInputElem;
    private readonly Dictionary<string, BindStatement> _namedFuncBodies = new();   // for pipe element-chain inference

    // Failable struct registry — `cfl_N { int is_failure; T val; const char* message, category }`
    // per distinct inner T. A `T or failure` value: either a T (is_failure=0) or a failure.
    private readonly Dictionary<string, string> _failableSig2Name = new();
    private readonly List<(string Name, CufetType Inner)> _failableStructs = new();
    private int _failableCounter;

    // Inside a Try body: (handler label, the caught-failure C var, open-file depth at Try entry)
    // so a failing fallible call records the failure, closes files opened since the Try, and jumps
    // to the In-case-of-failure handler.
    private (string Label, string FailVar, CleanupPoint Exit)? _currentTryHandler;

    // E-prime — exception-handler bookkeeping. `_excOpen` counts jmp_buf handlers open in the
    // function currently being emitted (reset per function): every NONLOCAL exit (return, Stop/Skip,
    // failure-goto, propagate) must pop `cufet_exc_top` by the handlers it jumps out of, or a later
    // fault would longjmp into a dead frame. `_currentExcHandler` is the active handler's suppress
    // var + done label (for `Suppress.`); `_currentExcVar` the saved message (for `the exception`).
    //
    // ⚠ It carries the SAME four cleanup marks as `_currentTryHandler`, and for the same reason:
    // `Suppress` is a nonlocal exit out of the handler block, so it has to release everything the
    // handler opened. It carried only `RabbitDepth` and released only arenas — so an object with a
    // destructor, made inside a handler that then suppressed, was never unmade. The interpreter
    // unwinds the handler block properly and ran the destructor; the compiled program did not.
    private int _excOpen;
    private (string SupVar, string DoneLabel, CleanupPoint Exit)? _currentExcHandler;
    private string? _currentExcVar;
    private static readonly CufetType TExcMarker = new ExceptionMarkerType();

    // The `cufet_exc_top -= K;` statement popping handlers down to `toDepth`, or "" when none.
    private string ExcPopStmts(int toDepth)
    {
        int k = _excOpen - toDepth;
        return k > 0 ? $"cufet_exc_top -= {k}; " : "";
    }
    // Inside an In-case-of-failure handler: the CufetFailure C var that `the failure` refers to.
    private string? _currentFailVar;

    // Close-on-all-paths cleanup (slice 9B): open file handles (their `fclose(...)` C statements)
    // in open order. Files are closed on EVERY exit from their scope — normal end, return,
    // failure-goto, and loop break/continue — so a write is always flushed (no data loss).
    // (Arenas unwind alongside these as of ESC.3 — see ArenaPopStmts. They were held back until the
    // escape machinery existed to move an escaping value or failure message out of the arena first.)
    private readonly List<string> _openFiles = new();
    // The _openFiles depth at each enclosing loop's entry, so break/continue closes files opened
    // inside the loop body before jumping out of it.

    // ── UNMK — destructors (`unmaking`) ────────────────────────────────────────
    // Declarations collected up front (like the interpreter's _unmakeDefs): typeName → its unmaker.
    // A type is unmakeable iff it has an entry. Everything is gated on this being non-empty (zero cost
    // otherwise). MATCH-EXACTLY (settled): the interpreter fires unmakers at block scope-exit, LIFO,
    // for Define'd object bindings only — NOT at function frames or top-level. Value-copies/escape
    // double-fire (per-binding hook, not an ownership destructor). See [[project-design-decisions]].
    private readonly Dictionary<string, UnmakerDeclaration> _unmakeDefs = new();
    // >0 while emitting inside a BLOCK scope of the current frame. An unmakeable Define registers its
    // unmaker only then, so top-level and function-frame Defines never register → never fire (matching
    // the interpreter's two gaps). Reset to 0 per frame (blocks bump it).
    private int _scopeDepth;
    // The C var holding cufet_num at the current frame's entry — a `return`/propagate runs unmakers
    // to it (firing every still-open block's unmakers in this frame, matching a return unwinding
    // through the block finallys in the interpreter). Null ⇒ no unmakers / not set.
    private string? _frameUnmakerBase;

    // ⚠⚠ A foreign RELEASE rides the same registry, so it has to switch the same machinery on. It
    // used to read `_unmakeDefs.Count > 0` alone, and a program with `and free it with` but no
    // `Bind unmaking` registered handles into a registry that nothing ever ran: the block emitted
    // no snapshot and no run-to at its exit, because those are gated here. Measured as a live
    // divergence — 3000 handles interpreted against 509 compiled, which is the OS limit, i.e. none
    // of them freed. The program printed the right thing throughout.
    private bool UsesUnmakers => _unmakeDefs.Count > 0 || _usesForeignRelease;
    private bool _usesForeignRelease;
    // Inline `cufet_run_unmakers_to(snap); ` for a nonlocal-exit statement; empty when not applicable.
    private string UnmakerRunStmt(string? snap) => UsesUnmakers && snap != null ? $"cufet_run_unmakers_to({snap}); " : "";

    // ── One ownership story: every nonlocal exit releases the same four things ─────────────────
    //
    // ★★ There are FOUR kinds of thing a jump out of a block has to release — unmakers, open
    // files, exception pads, rabbit arenas — and they are always released in that order. That is
    // not four rules: it is one rule with four parts, and it used to be written out longhand at
    // every jump site. Nine sites, four parts each, nothing checking they agreed.
    //
    // They did not agree. `FailureGotoBody`'s own comment records the first time — "three of the
    // four were already out of step when arenas were added" — and it fixed that for FAILURE gotos
    // only. `Suppress` was still releasing arenas alone when this record was written, so a
    // destructor on an object made inside a suppressing handler never ran; a live divergence,
    // because the interpreter unwinds the handler block and runs it.
    //
    // ★ A CleanupPoint is a MARK, taken where a jump will land. Making it one value is what makes
    // the rule enforceable: a new kind of releasable thing is a field here and a term in UnwindTo,
    // and every site gets it at once because no site spells the parts out any more.
    private readonly record struct CleanupPoint(
        int FileDepth, int ExcDepth, string? UnmakerSnap, int RabbitDepth);

    /// <summary>The mark for a jump landing HERE — everything currently open stays open.</summary>
    private CleanupPoint HereCleanup(string? unmakerSnap = null) =>
        new(_openFiles.Count, _excOpen, unmakerSnap, _rabbitDepth);

    /// <summary>The mark for leaving the current FUNCTION frame: everything in it goes.</summary>
    private CleanupPoint FrameExit => new(0, 0, _frameUnmakerBase, 0);

    /// <summary>The mark for leaving the innermost loop — what `Stop` and `Skip` unwind to.</summary>
    private CleanupPoint LoopExit =>
        _loopExits.Count > 0 ? _loopExits[^1] : new CleanupPoint(0, 0, null, 0);

    /// <summary>
    /// The releases a jump to <paramref name="point"/> must make, in the one correct order.
    /// </summary>
    /// <remarks>
    /// ⚠ The ORDER is load-bearing and is why this is one function. Unmakers run first, because a
    /// destructor body is Cufet code that may still read what the later steps free. Arenas go last,
    /// for the same reason. <paramref name="mergeArenas"/> is for a return whose VALUE points into
    /// a region being left — it merges outward instead of popping. See ReturnCarriesArenaData.
    /// </remarks>
    private string UnwindTo(CleanupPoint point, bool mergeArenas = false) =>
        UnmakerRunStmt(point.UnmakerSnap)
      + FileCleanupStmts(point.FileDepth)
      + ExcPopStmts(point.ExcDepth)
      + ArenaPopStmts(point.RabbitDepth, mergeArenas);

    // Set when the program uses `run`/pipe, so the POSIX subprocess runtime is emitted (only then).
    private bool _usesProcess;

    // Arc 1 (stdlib/books): books are BUILTIN + compile-time-resolved (no dynamic linking). A
    // `Pull a book on <name>` registers a compile-time alias (localName → canonical book name) so
    // `<book>'s <member>` dispatch routes to the right emission; the alias is scoped to the Pull body.
    private readonly Dictionary<string, string> _bookAliases = new();

    // The bundled books. A pulled name can carry BOTH a book alias and a module-object binding —
    // that is a book with a Cufet layer (a prelude-defined module object sharing the book's name),
    // and CufetLayerHasMethod decides per member which side answers. (0.16.0 arc, slice 1.)
    private static readonly HashSet<string> BuiltinBookNames =
        new(StringComparer.OrdinalIgnoreCase) { "math", "collections", "chance" };

    // A member the book's Cufet layer defines is ordinary method dispatch on the pulled object
    // binding; only members the layer does NOT define fall to the native book emission. The member
    // arriving here is the RESOLVED name (`unique of number`) wherever a filling decided the body.
    private bool CufetLayerHasMethod(string bookName, string member) =>
        _objectDefs.TryGetValue(bookName, out var layerDef)
        && layerDef.Methods.Any(m => m.Name == member);

    // The layer's getter of that name, if any — how `math's pi` resolves now that the constants
    // live in Cufet.
    private GetterDeclaration? CufetLayerGetter(string bookName, string member) =>
        _objectDefs.TryGetValue(bookName, out var layerDef)
            ? layerDef.Getters.FirstOrDefault(g => g.Name == member)
            : null;
    // Set when the program cases text (`in uppercase`/`in lowercase`), so the Unicode case table is
    // emitted (only then). Worth gating: the table is ~11 KB of the ~1500-line runtime that is
    // pasted into every generated file, and most programs never case anything.
    private bool _usesCase;
    // Set when the program uses matrices (the collections book's introduced type), so the matrix
    // runtime is emitted (only then). A matrix is an arena reference type like series/maps.
    private bool _usesMatrix;
    private bool _usesChase;
    // Set when the program uses the chance book's randomness nodes, so the PRNG runtime is emitted.
    // Per the settled fork: ANY C PRNG + invariant-testing — NOT bit-identity with System.Random
    // (unseeded .NET is xoshiro256**, nondeterministic-by-design; the seeded-port is a documented
    // follow-on in the gap audit). Seeded runs are self-consistent WITHIN a backend.
    private bool _usesChance;

    // CONC.E — set when the program uses interrupt constructs (`Yield.`, `an interrupt is requested`,
    // `Acknowledge the interrupt.`) or concurrency (blocked channel-waits are made interruptible). When
    // set, the SIGINT signal substrate is emitted + main installs the handler and a longjmp landing pad.
    private bool _usesSignals;

    // Concurrency (CONC.A+B): emitted only when tasks/channels are used. Generated task thread
    // functions accumulate in _taskFns; the enclosing rabbit's C var suffix for its thread + channel
    // lists (for the structured join + channel-free at Done.) is the top of _rabbitCtx.
    private bool _usesConcurrency;
    private readonly StringBuilder _taskFns = new();
    private int _taskCounter;
    private readonly List<string> _rabbitCtx = new();   // suffix N of cf_thr{N}/cf_chan{N} per open rabbit
    private int _rabbitDepth;   // open `Pull a rabbit` arena scopes (concurrency-independent; escape guard)
    // ESC.4 — each in-scope variable's DECLARING rabbit depth (mirrors the checker's TypeInfo.RabbitDepth).
    // Used to decide, per closure CAPTURE, whether it escapes the closure's destination: a capture
    // declared DEEPER than the closure's destination depth must be copied (its source dies first);
    // one declared at or above the destination is shared (its source outlives the destination).
    private readonly Dictionary<string, int> _varRabbitDepth = new();
    // The destination rabbit depth of the closure currently being emitted, if it escapes there
    // (threaded from the escaping store's RHS); null ⇒ non-escaping, share captures as before.
    private int? _closureEscapeDepth;

    // CONC.C — named tasks + `the awaited result of`. Per named task in scope: the enclosing rabbit
    // suffix (Ctx), the C type of its heap-bridged result, and its inferred result type (which may be
    // a FailureType/VoidableType). The slot index / stored-result C vars are `cf_slot_<sfx>` /
    // `cf_tres_<sfx>` declared at the spawn site (sfx = name with '-'→'_').
    private readonly Dictionary<string, (string Ctx, string ResultCType, CufetType? ResultType)> _taskInfos = new();
    // While emitting a named task's body: the result C type (null ⇒ void/fire-and-forget), so a
    // `return <v>` heap-bridges the value and unwinds the task's arena instead of a plain C return.
    private (CufetType? ResultType, string? ResultCType)? _currentTaskReturn;
    // True while emitting a task body — awaiting a task's result from inside another task is deferred.
    private bool _inTaskBody;

    // The `fclose(...)` statements for files opened at or after `fromDepth`, innermost-first (LIFO),
    // as one inline C string. Used at return / failure-goto / propagate / break / continue. Does
    // NOT mutate _openFiles (nonlocal exits jump past the normal scope-exit that pops them).
    private string FileCleanupStmts(int fromDepth)
    {
        if (_openFiles.Count <= fromDepth) return "";
        var sb = new StringBuilder();
        for (int i = _openFiles.Count - 1; i >= fromDepth; i--) sb.Append(_openFiles[i]).Append(' ');
        return sb.ToString();
    }

    // The declared return type of the function/method/getter currently being emitted, so a
    // `return <T>` in a `voidable T` body widens the value into the voidable struct.
    private CufetType? _currentReturnType;

    // Flow-narrowed variables: inside an `is not void` branch a voidable variable is treated
    // as its inner T (reads emit `.val`), matching the interpreter's variable-level narrowing.
    // Flow-narrowed variables: the narrowed static type PLUS the C accessor that reaches the payload.
    // Voidable narrows to `.val` (2-case tagged struct); a closed union narrows to `.val.c<k>` (the
    // N-case generalization) — same model, different accessor.
    private readonly Dictionary<string, (CufetType Type, string Access)> _narrowedVars = new();

    // ★ Cases still reachable for a variable the checker narrowed in TYPE but not in REPRESENTATION.
    //
    // A Judge's GROUPED arm is the case that needs this. `A quote or a paragraph:` leaves `it` a
    // union, so there is no payload to reach through and _narrowedVars has nothing to record — but
    // the checker does know `it` is one of TWO cases now, not one of the subject's four. Narrowing
    // again inside the arm (`If it is a quote: … Otherwise: …`) is exhaustive to the checker and
    // was NOT to the compiler, which kept eliminating from all four, found two left, and declined
    // to narrow — then emitted the field access against the union struct anyway. That produced C
    // that gcc rejects, so the failure landed at build time with no diagnostic from `check`.
    //
    // Indices are into the REPRESENTATION union (the subject's, the one cv_it actually is), never
    // into the arm's smaller one, because every emitted `.val.c<k>` has to index the real struct.
    // Keeping the restricted SET rather than substituting a narrower TYPE is what keeps those two
    // apart: an arm's case order need not match the subject's, so a sub-union's own indices would
    // silently reach the wrong member.
    private readonly Dictionary<string, List<int>> _armCases = new();

    // Object definitions by name (nominal types), collected up front. Objects are also
    // C value structs (cd_<name>); methods become C functions taking a receiver pointer.
    private readonly Dictionary<string, ObjectDefinition> _objectDefs = new();

    // Operator overloads, keyed by (object type name, operator). MEASURED surface: `Bind overloading
    // <op>, given (the <l> is a <T>, the <r> is a <T>)` — FREE-STANDING and top-level (not a method),
    // `+ - * /` ONLY, BOTH operands the SAME object type, and the type checker rejects a duplicate
    // (type, op). So resolution is an EXACT nominal match with at most one candidate: a compile-time
    // dictionary lookup, no ranking and no ambiguity. Comparisons/`is` are NOT overloadable, so the
    // built-in _eq machinery (record/object equality, `unique`, map keys) is untouched.
    // Keyed on the ORDERED operand pair, matching the front end: `vec2 * number` is its own entry.
    private readonly Dictionary<(string Left, string Right, TokenType Op), OperatorOverloadDeclaration> _overloadDefs = new();
    // Memoized inferred return types (an overload's return is inferred from its body, like a lambda's,
    // and may be ANY type — including `T or failure` when the body returns a failure).
    private readonly Dictionary<(string Left, string Right, TokenType Op), CufetType?> _overloadReturnTypes = new();

    // The overload-table name for an operand type — the front end's `OperandTypeName`, and it
    // must stay in step with it: a pair the checker resolved one way and the compiler another is
    // a divergence by construction.
    private static string? OperandName(CufetType? t) => t switch
    {
        ObjectType ot => ModuleTypeLifting.DisplayName(ot.Name),
        NumberType    => "number",
        TextType      => "text",
        FactType      => "fact",
        BitsType      => "bits",
        _             => null,
    };

    // The reverse: the type an operand NAME stands for, for seeding the body's variable types.
    private CufetType OperandType(string name) => name.ToLowerInvariant() switch
    {
        "number" => TNumber,
        "text"   => TText,
        "fact"   => TFact,
        "bits"   => TBits,
        _        => ObjType(name),
    };
    private readonly HashSet<(string Left, string Right, TokenType Op)> _overloadInferring = new();

    // ── Channel-of-T deep copy ────────────────────────────────────────────────────
    //
    // A value crossing a channel boundary must be decoupled from BOTH threads' arenas: deep-copied to
    // malloc'd heap on send (the "envelope"), deep-copied into the receiver's arena on recv, then the
    // heap envelope freed. This is the interpreter's DeepCopyForChannel, now in C, per element type.
    //
    // Two recursive helpers are synthesized per type in the closure: `copy(v, alloc)` — a deep copy
    // using the given allocator (malloc for the heap bridge, cufet_arena_alloc for the arena copy) —
    // and `freeheap(v)` — a recursive free of a heap copy. Value-only types (number/fact and nestings
    // thereof) are POD: copy is the identity and freeheap a no-op (the struct copy IS the deep copy,
    // matching the number-only fast path). Text/series/map/matrix/reference types recurse.

    // Is `t` transitively free of arena pointers (so a plain C struct copy is already a deep copy)?
    private bool IsChanPod(CufetType t) => t switch
    {
        // `bits` belongs here for the same reason number and fact do: CufetBits is a flat value
        // struct (value + base + width) with no pointer in it, so a struct copy IS a deep copy.
        NumberType or BitsType or FactType => true,
        VoidableType v => IsChanPod(v.Inner),
        // A failable result (`T or failure`) is POD iff its inner T is: the message/category are
        // const char* to STATIC string literals (task failure messages must be static — CONC.C), so
        // a shallow struct copy is lifetime-safe for them; only the inner value can hold arena pointers.
        FailureType ft => IsChanPod(ft.Inner),
        RecordType rt  => RecordFields(rt).All(f => IsChanPod(f.Type)),
        ObjectType ot  => ObjectFields(_objectDefs[ot.Name]).All(f => IsChanPod(f.Type)),
        // A union is POD iff EVERY case it can hold is — the `number or fact` fast path (no tag
        // dispatch needed, the struct copy IS the deep copy). Open unions use the discovered set.
        UnionType ut   => UnionCases(ut).All(IsChanPod),
        _ => false,   // text, series, map, matrix — hold arena pointers
    };

    // The case list of a union — declared for a closed one, whole-program-discovered for the open one.
    private IReadOnlyList<CufetType> UnionCases(UnionType ut) =>
        ut.Cases == null ? _openUnionCases : FlatCases(ut.Cases);

    // Registers `t` (and, recursively, the component types its copy body references) for deep-copy
    // helper synthesis, returning its index. `isTop` marks a type used AT a channel/pipe boundary
    // (those additionally get the heapenv/arenacopy/freeenv wrappers). Deduped by TypeSig.
    private int RegisterChanElem(CufetType t, bool isTop)
    {
        // A 1-case union IS that case everywhere else (TypeSig/EmitCType normalize it) — normalize
        // here too, so the copy body isn't emitted against a tag-less C type.
        if (t is UnionType { Cases: { Count: 1 } } u1) t = u1.Cases[0];
        string sig = TypeSig(t);
        if (isTop) _chanTopElems.Add(sig);
        if (_chanElemReg.TryGetValue(sig, out var existing)) return existing;
        int idx = _chanElemReg.Count;
        _chanElemReg[sig] = idx;
        _chanElemList.Add((idx, t));
        // Ensure the C struct(s) this type needs are registered (idempotent — also done at send/recv
        // via EmitCType, but registering here keeps the closure self-contained).
        switch (t)
        {
            case SeriesType st: RegisterSeriesStruct(st); RegisterChanElem(st.ElementType, false); break;
            case MapType mt: RegisterMapStruct(mt); RegisterChanElem(mt.KeyType, false); RegisterChanElem(mt.ValueType, false); break;
            case VoidableType vt: RegisterVoidableStruct(vt); RegisterChanElem(vt.Inner, false); break;
            case FailureType ft: RegisterFailableStruct(ft); RegisterChanElem(ft.Inner, false); break;
            case MatrixType: _usesMatrix = true; break;
            case ChaseType:  _usesChase  = true; break;
            case RecordType rt:
                RegisterRecordStruct(rt);
                foreach (var f in RecordFields(rt)) if (!IsChanPod(f.Type)) RegisterChanElem(f.Type, false);
                break;
            case ObjectType ot:
                foreach (var f in ObjectFields(_objectDefs[ot.Name])) if (!IsChanPod(f.Type)) RegisterChanElem(f.Type, false);
                break;
            case UnionType ut:
                if (ut.Cases != null) RegisterUnionStruct(ut); else MarkOpenUnion();
                foreach (var c in UnionCases(ut)) if (!IsChanPod(c)) RegisterChanElem(c, false);
                break;
            case NumberType or BitsType or FactType or TextType: break;
            default:
                throw new CompilerException(
                    $"a channel of '{FormatTypeName(t)}' is not supported (channel elements are number/bits/fact/text/series/map/record/object/union/voidable/matrix).");
        }
        return idx;
    }

    private int ChanIdxOf(CufetType t) => _chanElemReg[TypeSig(t)];
    private string ChanHeapEnv(CufetType t)  => $"cchan_{ChanIdxOf(t)}_heapenv";
    private string ChanArenaCopy(CufetType t) => $"cchan_{ChanIdxOf(t)}_arenacopy";
    private string ChanFreeEnv(CufetType t)  => $"cchan_{ChanIdxOf(t)}_freeenv";

    // Emits the deep-copy helper family for every registered channel element type + its closure.
    // All `copy`/`freeheap` signatures are forward-declared first so the recursion (a series-of-record
    // copy calls the record copy, a record-with-series copy calls the series copy) resolves — the same
    // forward-declare-then-define shape as the series `_write`/`_eq` helpers.
    private void EmitChannelDeepCopy(StringBuilder sb)
    {
        if (_chanElemList.Count == 0) return;
        sb.AppendLine("// ── Channel-of-T deep copy (heap bridge on send, arena copy on recv) ──");
        sb.AppendLine("typedef void* (*cufet_alloc_fn)(size_t);");
        foreach (var (idx, t) in _chanElemList)
        {
            string tc = EmitCType(t);
            sb.AppendLine($"static {tc} cchan_{idx}_copy({tc} v, cufet_alloc_fn a);");
            sb.AppendLine($"static void cchan_{idx}_freeheap({tc} v);");
        }
        foreach (var (idx, t) in _chanElemList)
        {
            EmitChanCopyBody(sb, idx, t);
            EmitChanFreeBody(sb, idx, t);
        }
        // Boundary wrappers (only for types actually used at a channel/pipe edge — avoids unused statics).
        foreach (var (idx, t) in _chanElemList)
        {
            if (!_chanTopElems.Contains(TypeSig(t))) continue;
            string tc = EmitCType(t);
            sb.AppendLine($"static void* cchan_{idx}_heapenv({tc} v) {{ void* e = malloc(sizeof({tc})); *({tc}*)e = cchan_{idx}_copy(v, malloc); return e; }}");
            sb.AppendLine($"static {tc} cchan_{idx}_arenacopy(void* e) {{ return cchan_{idx}_copy(*({tc}*)e, cufet_arena_alloc); }}");
            sb.AppendLine($"static void cchan_{idx}_freeenv(void* e) {{ cchan_{idx}_freeheap(*({tc}*)e); free(e); }}");
        }
        // ESC.2 — escape copies: deep-copy a value into the arena at `d` (the destination's depth),
        // so a value born in a shorter-lived rabbit survives being stored into longer-lived storage.
        // Reuses the copy bodies above verbatim; only the allocator differs.
        foreach (var (idx, t) in _chanElemList)
        {
            if (!_escapeElems.Contains(TypeSig(t))) continue;
            string tc = EmitCType(t);
            sb.AppendLine($"static {tc} cchan_{idx}_escapecopy({tc} v, int d) {{ int s = cufet_alloc_override; cufet_alloc_override = d; {tc} r = cchan_{idx}_copy(v, cufet_arena_alloc); cufet_alloc_override = s; return r; }}");
        }
        sb.AppendLine();
    }

    private void EmitChanCopyBody(StringBuilder sb, int idx, CufetType t)
    {
        string tc = EmitCType(t);
        sb.Append($"static {tc} cchan_{idx}_copy({tc} v, cufet_alloc_fn a) {{ ");
        switch (t)
        {
            case NumberType or FactType:
                sb.Append("(void)a; return v;");
                break;
            case TextType:
                sb.Append("if (!v) return v; size_t n = strlen(v) + 1; char* r = (char*)a(n); memcpy(r, v, n); return r;");
                break;
            case SeriesType st:
            {
                string sn = RegisterSeriesStruct(st), ec = EmitCType(st.ElementType);
                int ei = ChanIdxOf(st.ElementType);
                sb.Append($"if (!v) return v; {sn}* r = ({sn}*)a(sizeof({sn})); r->len = v->len; r->cap = v->len; ");
                sb.Append($"r->data = v->len ? ({ec}*)a((size_t)v->len * sizeof({ec})) : NULL; ");
                sb.Append($"for (int i = 0; i < v->len; i++) r->data[i] = cchan_{ei}_copy(v->data[i], a); return r;");
                break;
            }
            case MapType mt:
            {
                string mn = RegisterMapStruct(mt), kc = EmitCType(mt.KeyType), vc = EmitCType(mt.ValueType);
                int ki = ChanIdxOf(mt.KeyType), vi = ChanIdxOf(mt.ValueType);
                sb.Append($"if (!v) return v; {mn}* r = ({mn}*)a(sizeof({mn})); r->len = v->len; r->cap = v->len; ");
                sb.Append($"r->keys = v->len ? ({kc}*)a((size_t)v->len * sizeof({kc})) : NULL; ");
                sb.Append($"r->vals = v->len ? ({vc}*)a((size_t)v->len * sizeof({vc})) : NULL; ");
                sb.Append($"for (int i = 0; i < v->len; i++) {{ r->keys[i] = cchan_{ki}_copy(v->keys[i], a); r->vals[i] = cchan_{vi}_copy(v->vals[i], a); }} return r;");
                break;
            }
            case MatrixType:
                sb.Append("if (!v) return v; CufetMatrix* r = (CufetMatrix*)a(sizeof(CufetMatrix)); r->rows = v->rows; r->cols = v->cols; ");
                sb.Append("int nn = v->rows * v->cols; r->data = nn ? (CufetDec*)a((size_t)nn * sizeof(CufetDec)) : NULL; ");
                sb.Append("for (int i = 0; i < nn; i++) r->data[i] = v->data[i]; return r;");
                break;
            case VoidableType vt:
            {
                int ii = ChanIdxOf(vt.Inner);
                sb.Append($"if (!v.has) return v; {tc} r; r.has = 1; r.val = cchan_{ii}_copy(v.val, a); return r;");
                break;
            }
            case FailureType ft:
            {
                // Struct copy carries the tag + the static message/category; deep-copy the inner value
                // only on the success side (a failure has no meaningful `val`).
                sb.Append($"{tc} r = v; if (!v.is_failure) r.val = cchan_{ChanIdxOf(ft.Inner)}_copy(v.val, a); return r;");
                break;
            }
            case RecordType or ObjectType:
            {
                var fields = t is RecordType rt ? RecordFields(rt) : ObjectFields(_objectDefs[((ObjectType)t).Name]);
                sb.Append($"{tc} r = v; ");
                foreach (var f in fields)
                    if (!IsChanPod(f.Type)) sb.Append($"r.{f.CField} = cchan_{ChanIdxOf(f.Type)}_copy(v.{f.CField}, a); ");
                sb.Append("return r;");
                break;
            }
            case UnionType ut:
            {
                // The struct copy carries the tag; only the LIVE case's payload needs deep-copying,
                // so dispatch on the tag — the same shape as the FailureType arm, N-way. POD cases
                // contribute no arm (their payload is already a complete copy).
                sb.Append($"(void)a; {tc} r = v; switch (v.tag) {{ ");
                var cases = UnionCases(ut);
                for (int k = 0; k < cases.Count; k++)
                    if (!IsChanPod(cases[k]))
                        sb.Append($"case {k}: r.val.c{k} = cchan_{ChanIdxOf(cases[k])}_copy(v.val.c{k}, a); break; ");
                sb.Append("default: break; } return r;");
                break;
            }
            default:
                throw new CompilerException($"channel deep-copy of '{FormatTypeName(t)}' is unsupported.");
        }
        sb.AppendLine(" }");
    }

    private void EmitChanFreeBody(StringBuilder sb, int idx, CufetType t)
    {
        string tc = EmitCType(t);
        sb.Append($"static void cchan_{idx}_freeheap({tc} v) {{ ");
        switch (t)
        {
            case NumberType or FactType:
                sb.Append("(void)v;");
                break;
            case TextType:
                sb.Append("if (v) free((void*)v);");
                break;
            case SeriesType st:
                sb.Append($"if (!v) return; for (int i = 0; i < v->len; i++) cchan_{ChanIdxOf(st.ElementType)}_freeheap(v->data[i]); free(v->data); free(v);");
                break;
            case MapType mt:
                sb.Append($"if (!v) return; for (int i = 0; i < v->len; i++) {{ cchan_{ChanIdxOf(mt.KeyType)}_freeheap(v->keys[i]); cchan_{ChanIdxOf(mt.ValueType)}_freeheap(v->vals[i]); }} free(v->keys); free(v->vals); free(v);");
                break;
            case MatrixType:
                sb.Append("if (!v) return; free(v->data); free(v);");
                break;
            case VoidableType vt:
                sb.Append($"if (v.has) cchan_{ChanIdxOf(vt.Inner)}_freeheap(v.val);");
                break;
            case FailureType ft:
                sb.Append($"if (!v.is_failure) cchan_{ChanIdxOf(ft.Inner)}_freeheap(v.val);");
                break;
            case RecordType or ObjectType:
            {
                var fields = t is RecordType rt ? RecordFields(rt) : ObjectFields(_objectDefs[((ObjectType)t).Name]);
                foreach (var f in fields)
                    if (!IsChanPod(f.Type)) sb.Append($"cchan_{ChanIdxOf(f.Type)}_freeheap(v.{f.CField}); ");
                sb.Append("(void)v;");
                break;
            }
            case UnionType ut:
            {
                sb.Append("switch (v.tag) { ");
                var cases = UnionCases(ut);
                for (int k = 0; k < cases.Count; k++)
                    if (!IsChanPod(cases[k]))
                        sb.Append($"case {k}: cchan_{ChanIdxOf(cases[k])}_freeheap(v.val.c{k}); break; ");
                sb.Append("default: break; } (void)v;");
                break;
            }
            default:
                throw new CompilerException($"channel deep-copy of '{FormatTypeName(t)}' is unsupported.");
        }
        sb.AppendLine(" }");
    }

    // The C boolean expression comparing two values of type `t` by value.
    // Three-way comparison (<0 / 0 / >0) for sort keys — numbers by decimal compare, text by ordinal.
    private static string CmpCall(string a, string b, CufetType t) => t switch
    {
        NumberType => $"cufet_cmp({a}, {b})",
        TextType   => $"strcmp({a}, {b})",
        _ => throw new CompilerException($"sorting by a '{FormatTypeName(t)}' key is not supported — sort keys must be number or text."),
    };

    // `<series> sorted [in reverse] [by <field>]` — a NEW series (non-mutating), stably sorted.
    // A stable insertion sort (equal keys keep original order) matches the interpreter's stable
    // OrderBy exactly; C's qsort is NOT stable, so we don't use it. Natural order (number/text) or
    // by a named record/object field. Numbers compare via cufet_cmp, text via ordinal strcmp.
    private string EmitSort(SortExpression sort)
    {
        var st       = (SeriesType)TypeOf(sort.Series);
        string ser   = RegisterSeriesStruct(st);
        var elemType = st.ElementType;
        var keyType  = sort.ByField == null ? elemType : FieldType(elemType, sort.ByField);
        string src   = EmitExpr(sort.Series);
        int id = _freshId++;
        string ssrc = $"cf_ss{id}", dst = $"cf_srt{id}";
        // Key of an element expr: the element itself (natural), or its named field (by-field).
        string KeyOf(string e) => sort.ByField == null ? e : $"({e}).{MangleName(sort.ByField)}";
        string cmp = CmpCall(KeyOf($"{dst}->data[cf_j{id}]"), KeyOf($"cf_k{id}"), keyType);
        string outOfOrder = sort.Reverse ? $"({cmp}) < 0" : $"({cmp}) > 0";
        var b = new StringBuilder();
        b.Append($"{ser}* {ssrc} = {src}; {ser}* {dst} = {ser}_new(); ");
        b.Append($"for (int cf_i{id} = 0; cf_i{id} < {ssrc}->len; cf_i{id}++) {ser}_append({dst}, {ssrc}->data[cf_i{id}]); ");
        b.Append($"for (int cf_a{id} = 1; cf_a{id} < {dst}->len; cf_a{id}++) {{ ");
        b.Append($"{EmitCType(elemType)} cf_k{id} = {dst}->data[cf_a{id}]; int cf_j{id} = cf_a{id} - 1; ");
        b.Append($"while (cf_j{id} >= 0 && {outOfOrder}) {{ {dst}->data[cf_j{id} + 1] = {dst}->data[cf_j{id}]; cf_j{id}--; }} ");
        b.Append($"{dst}->data[cf_j{id} + 1] = cf_k{id}; }}");
        _preEmits.Add(b.ToString());
        return dst;
    }

    // ── Misc smalls (cleanup slice): env vars, is-a-type, directory contents ──

    // `the environment variable "X"` → voidable text: void when unset (matches the interpreter's
    // GetEnvironmentVariable-null→void). getenv's storage is stable here (Cufet has no setenv).
    // `the current directory` → voidable text. NULL only when getcwd cannot answer, which mirrors
    // the interpreter turning the equivalent exception into void.
    private string EmitCurrentDirectory()
    {
        string cvd = RegisterVoidableStruct(new VoidableType(TText));
        int id = _freshId++;
        _preEmits.Add($"const char* cf_cwd{id} = cufet_getcwd();");
        return $"(cf_cwd{id} ? ({cvd}){{ .has = 1, .val = cf_cwd{id} }} : ({cvd}){{ .has = 0 }})";
    }

    private string EmitEnvVar(EnvironmentVariableExpression env)
    {
        string cvd = RegisterVoidableStruct(new VoidableType(TText));
        string n = EmitExpr(env.Name);
        int id = _freshId++;
        _preEmits.Add($"const char* cf_ev{id} = getenv({n});");
        return $"(cf_ev{id} ? ({cvd}){{ .has = 1, .val = cf_ev{id} }} : ({cvd}){{ .has = 0 }})";
    }

    // `x is a <type>` — in the monomorphic model this is a COMPILE-TIME CONSTANT except for one
    // case: a VOIDABLE target, where `x is a T` ⇔ present-and-inner-matches (`.has`) and
    // `x is a void` ⇔ absent — the same runtime test the interpreter's RuntimeIsType does. Kind
    // matching mirrors RuntimeIsType's erasure: series/maps/records match by KIND (element/shape-
    // erased, like `value is List<object>`), objects nominally by name.
    private string EmitIsTypeCheck(IsTypeCheck tc)
    {
        GuardTestedNotUnion(tc.Type);
        var tt = TypeOf(tc.Target);
        // `is a <case>` on a closed union = a genuine RUNTIME tag check (this is where runtime type
        // identity lives — unlike the monomorphic model's compile-time StaticKindMatches fold).
        if (tt is UnionType uop && uop.Cases == null)
        {
            GuardUnionContainerNarrow(_openUnionCases, tc.Type);
            int ko = MatchCaseInList(_openUnionCases, tc.Type);   // -1 ⇒ never widened in ⇒ statically false
            string vo = EmitExpr(tc.Target);
            string testo = ko < 0 ? "0" : $"(({vo}).tag == {ko})";
            return tc.Negated ? $"(!{testo})" : testo;
        }
        if (tt is UnionType ut && ut.Cases != null)
        {
            GuardUnionContainerNarrow(UnionCases(ut), tc.Type);
            int k = UnionMatchCase(ut, tc.Type);          // -1 = no case matches (statically false)
            string v = EmitExpr(tc.Target);
            string test = k < 0 ? "0" : $"(({v}).tag == {k})";
            return tc.Negated ? $"(!{test})" : test;
        }
        if (tt is VoidableType vt)
        {
            string v = EmitExpr(tc.Target);
            // ISA.2b: `is a voidable X` is satisfied by a VOID value (void matches any voidable) or
            // by a present value whose inner type matches — mirroring the interpreter exactly.
            string test = tc.Type switch
            {
                VoidType         => $"(!({v}).has)",
                VoidableType tv2 => StaticKindMatches(vt.Inner, tv2.Inner) ? "1" : $"(!({v}).has)",
                _                => StaticKindMatches(vt.Inner, tc.Type) ? $"(({v}).has)" : "0",
            };
            return tc.Negated ? $"(!{test})" : test;
        }
        bool matches = StaticKindMatches(tt, tc.Type);
        return (tc.Negated ? !matches : matches) ? "1" : "0";
    }

    // Which case of a closed union does `tested` select? Exactly one ⇒ its index. None ⇒ -1 (the check
    // is statically false). ISA.1 made StaticKindMatches ELEMENT-AWARE, so container-vs-container
    // unions now resolve to exactly one case (the CAT.1 clean-throw below is effectively unreachable
    // for element-distinguishable cases — it remains as a guard against any residual ambiguity).
    private int UnionMatchCase(UnionType ut, CufetType tested) => MatchCaseInList(UnionCases(ut), tested);

    // Shared by closed unions (declared cases) and open unions (the discovered set).
    private static int MatchCaseInList(IReadOnlyList<CufetType> cases, CufetType tested)
    {
        GuardTestedNotUnion(tested);
        var hits = new List<int>();
        for (int i = 0; i < cases.Count; i++)
            if (StaticKindMatches(cases[i], tested)) hits.Add(i);
        if (hits.Count > 1)
            throw new CompilerException(
                $"this catalogue's cases can't be told apart at runtime: `is a {FormatTypeName(tested)}` matches " +
                $"{hits.Count} of its cases. Narrowing would reinterpret one case as " +
                "another. Use cases distinguishable by type.");
        return hits.Count == 1 ? hits[0] : -1;
    }

    // A user-facing name for a type. Every arm matters: this feeds error messages, and the
    // fallback used to print the C# class name — a reader hitting an unsupported feature was told
    // about a 'TaskHandleType', which is not a phrase that appears anywhere in Cufet.
    // The same rule as TypeChecker.FormatAxiomType, and it has to stay the same: result AND
    // parameters, or two different axiom types render identically and a mismatch message says
    // nothing. Kept beside FormatTypeName so the pair is read together.
    private static string FormatAxiomTypeName(AxiomType a)
    {
        string head = a.ReturnType is { } gives
            ? $"{a.Language} {FormatTypeName(gives)} axiom"
            : $"{a.Language} axiom";
        return a.ParameterTypes.Count == 0
            ? head
            : $"{head} given ({string.Join(", ", a.ParameterTypes.Select(FormatTypeName))})";
    }

    private static string FormatTypeName(CufetType t) => t switch
    {
        NumberType => "number", TextType => "text", FactType => "fact", BitsType => "bits",
        SeriesType => "series", MapType => "map", RecordType => "record", MatrixType => "matrix",
        ChaseType => "chase",
        StashType s     => $"stash of {FormatTypeName(s.ElementType)}",
        ObjectType o    => ModuleTypeLifting.DisplayName(o.Name),
        VoidType        => "void",
        VoidableType v  => $"voidable {FormatTypeName(v.Inner)}",
        FailureType f   => $"{FormatTypeName(f.Inner)} or failure",
        ChannelType c   => $"channel of {FormatTypeName(c.ElementType)}",
        TaskHandleType  => "task",
        FunctionType    => "function",
        InterfaceType i => i.Name,
        // ★ The tail below is not decoration. Every one of these used to fall to the `"value"`
        // catch-all, so a refusal naming a type the compiler could not represent said "a 'value'"
        // — which is how a missing `bits` arm produced "printing a 'value' is not yet supported"
        // and cost an afternoon. Names match TypeChecker.FormatType so the two agree.
        MappingType     => "mapping",
        AddressType     => "address",
        ReadableStreamType r => $"readable stream of {FormatTypeName(r.ElementType)}",
        WritableStreamType w => $"writable stream of {FormatTypeName(w.ElementType)}",
        RabbitType      => "rabbit",
        FailureMarkerType   => "failure",
        ExceptionMarkerType => "exception",
        BookType b      => $"book '{b.Name}'",
        AxiomType a     => FormatAxiomTypeName(a),
        UnionType u     => u.Cases == null ? "catalogue value"
                             : string.Join(" or ", u.Cases.Select(FormatTypeName)),
        // Unreachable while every CufetType above has an arm — pinned by
        // EveryCufetType_HasAName in the compiler tests, which fails the moment a new one does not.
        _               => "value",
    };

    // A readable name for an AST node, for the three "this construct isn't handled" catch-alls.
    // They used to print the C# class name verbatim — a reader who tried a range in value position
    // was told about a 'RangeExpression'. Split the CamelCase and drop the Expression/Statement
    // suffix so the phrase at least reads like the language it is refusing.
    private static string NodeName(object node)
    {
        var n = node.GetType().Name;
        foreach (var suffix in new[] { "Expression", "Statement", "Literal", "Declaration" })
            if (n.EndsWith(suffix) && n.Length > suffix.Length) { n = n[..^suffix.Length]; break; }
        var sb = new StringBuilder();
        for (int i = 0; i < n.Length; i++)
        {
            if (i > 0 && char.IsUpper(n[i])) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(n[i]));
        }
        return sb.ToString();
    }

    // ISA.1 — ELEMENT-AWARE for containers (was kind-erased, matching the interpreter's old bug).
    // Series/maps now recurse into their element/key/value types, so `series of text` no longer
    // matches `is a series of number`. This is what lets MatchCaseInList find EXACTLY ONE case for a
    // container-vs-container union — lifting CAT.1's clean-throw — since the union tag already
    // carries the exact type (no runtime element inspection needed on this side).
    // Records stay shape-erased: a record shape is not expressible in an `is a` / union-case position
    // at all (the parser rejects it), so the arm is unreachable for containers-of-records purposes.
    private static bool StaticKindMatches(CufetType t, CufetType tested) => tested switch
    {
        NumberType => t is NumberType,
        BitsType   => t is BitsType,
        TextType   => t is TextType,
        FactType   => t is FactType,
        SeriesType st => t is SeriesType ts && StaticKindMatches(ts.ElementType, st.ElementType),
        MapType mt    => t is MapType tm && StaticKindMatches(tm.KeyType, mt.KeyType)
                                         && StaticKindMatches(tm.ValueType, mt.ValueType),
        RecordType => t is RecordType,        // shape-erased (unreachable — see above)
        MatrixType => t is MatrixType,
        ChaseType => t is ChaseType,
        ObjectType ot => t is ObjectType o2 && o2.Name == ot.Name,   // nominal
        VoidType   => false,                  // a non-voidable value is never void
        // ISA.2b — `is a voidable X` as the TESTED type had NO arm (it fell through to false) while
        // the interpreter answered true for a void value and for a concrete value matching X. That
        // was a PRE-EXISTING divergence, independent of containers. A concrete value satisfies
        // `voidable X` exactly when it satisfies X; the void-ness half is handled at the emit site.
        VoidableType tv => StaticKindMatches(t, tv.Inner),
        // A union in a NESTED position (the element type of `series of (number or text)`) is an
        // ordinary structural type comparison, not a tag question — compare the types directly.
        // The TOP-LEVEL `is a <union>` refusal lives at the call sites (GuardTestedNotUnion), where
        // it genuinely means "a set-valued test with no single tag".
        UnionType  => t is UnionType && t.Equals(tested),
        _ => false,
    };

    // `x is a (text or fact)` — a COMPOUND test at the TOP level. The interpreter answers it by
    // recursing through the union (true for a text), but a flat tag can't: one tag test can't stand
    // for a set of cases, and folding it to false would silently diverge. Refuse loudly instead.
    // (Nested unions are fine — see the StaticKindMatches UnionType arm.)
    // ISA.2c — REFUSE rather than diverge. The compiler answers a union `is a` from its TAG, which is
    // precise even for an empty payload; the interpreter answers from the VALUE, and an empty
    // container matches any container type vacuously (a bare List carries no element type). So when
    // the union holds a container case that does NOT match the tested type, an EMPTY instance of that
    // case would take different branches in the two backends. Both answers are safe (an empty
    // container has no element to misread) but they are observably different — and a divergence never
    // ships. Refusing is honest and preserves the invariant until the runtime type-carrier (ISA.2d)
    // lets the interpreter answer by declared type; then this guard lifts again.
    private static bool IsErasableContainer(CufetType t) => t is SeriesType or MapType;

    // ISA.2d — LIFTED. This used to refuse `is a <container>` on a union that could also hold a
    // DIFFERENT container type, because an empty container carried no element type at runtime: the
    // interpreter answered vacuously true for every container case while the compiler answered by
    // tag, so the two backends took different branches. The interpreter's containers now carry the
    // element type they were created with (Interpreter.Core.cs, CufetSeries/CufetMap), so it answers
    // an empty container from that type — precisely, and agreeing with the tag. Nothing to guard.
    private static void GuardUnionContainerNarrow(IReadOnlyList<CufetType> cases, CufetType tested)
    {
        _ = cases; _ = tested;
    }

    private static void GuardTestedNotUnion(CufetType tested)
    {
        if (tested is UnionType)
            throw new CompilerException(
                "`is a` against a union type (`is a (text or fact)`) is not supported by the compiler — " +
                "a runtime tag identifies ONE case, so a set-valued test has no single tag. " +
                "Test the individual cases instead (`is a text` / `is a fact`).");
    }

    // `the contents of the directory <p>` — fallible (series of text or failure). The raw cfl:
    // the runtime returns a SORTED (ordinal) arena array of "<p><sep><name>" paths, or a mapped
    // errno failure (not-found / permission-denied / disk-error — the interpreter's templates).
    private string EmitDirRaw(DirectoryContentsExpression dce)
    {
        string ser = RegisterSeriesStruct(new SeriesType(TText));
        string cfl = RegisterFailableStruct(new FailureType(new SeriesType(TText)));
        string p = EmitExpr(dce.Path);
        int id = _freshId++;
        _preEmits.Add($"const char* cf_dp{id} = {p};");
        _preEmits.Add(
            $"{cfl} cf_dc{id} = {{0}}; {{ const char** cf_di{id}; int cf_dn{id}; CufetFailure cf_de{id}; " +
            $"if (cufet_dir_contents(cf_dp{id}, &cf_di{id}, &cf_dn{id}, &cf_de{id})) {{ " +
            $"{ser}* cf_ds{id} = {ser}_new(); for (int cf_i{id} = 0; cf_i{id} < cf_dn{id}; cf_i{id}++) {ser}_append(cf_ds{id}, cf_di{id}[cf_i{id}]); " +
            $"cf_dc{id}.is_failure = 0; cf_dc{id}.val = cf_ds{id}; }} " +
            $"else {{ cf_dc{id}.is_failure = 1; cf_dc{id}.message = cf_de{id}.message; cf_dc{id}.category = cf_de{id}.category; }} }}");
        return $"cf_dc{id}";
    }

    // ── Chance (Arc 1E) ───────────────────────────────────────────────────────

    private string EmitRandomNumber(RandomNumber rn)
    {
        _usesChance = true;
        string lo = EmitExpr(rn.Low);
        string hi = EmitExpr(rn.High);
        return $"cufet_random_number({lo}, {hi}, {rn.Line})";
    }

    private string EmitRandomGuess()
    {
        _usesChance = true;
        return "(cufet_rng_below(2) == 1)";   // a fact, uniform over {true, false}
    }

    // `a random item from xs` → voidable element: void on an empty series (matches the interpreter),
    // else a uniform pick. Element-type-general (the series-of-T payoff).
    private string EmitRandomItem(RandomItem ri)
    {
        _usesChance = true;
        var st = (SeriesType)TypeOf(ri.Series);
        string cvd = RegisterVoidableStruct(new VoidableType(st.ElementType));
        string src = EmitExpr(ri.Series);
        int id = _freshId++;
        _preEmits.Add(
            $"{RegisterSeriesStruct(st)}* cf_ri{id} = {src}; int cf_rh{id} = cf_ri{id}->len > 0; " +
            $"{EmitCType(st.ElementType)} cf_rv{id}; " +
            $"if (cf_rh{id}) cf_rv{id} = cf_ri{id}->data[cufet_rng_below(cf_ri{id}->len)];");
        return $"(cf_rh{id} ? ({cvd}){{ .has = 1, .val = cf_rv{id} }} : ({cvd}){{ .has = 0 }})";
    }

    // `randomly shuffled xs` → a NEW arena series (non-mutating, like sorted/unique), Fisher-Yates
    // downward with j in [0, i] — the interpreter's exact procedure (over a different PRNG).
    private string EmitRandomlyShuffled(RandomlyShuffled rs)
    {
        _usesChance = true;
        var st = (SeriesType)TypeOf(rs.Series);
        string ser = RegisterSeriesStruct(st);
        string src = EmitExpr(rs.Series);
        int id = _freshId++;
        _preEmits.Add(
            $"{ser}* cf_sh{id} = {src}; {ser}* cf_sd{id} = {ser}_new(); " +
            $"for (int cf_i{id} = 0; cf_i{id} < cf_sh{id}->len; cf_i{id}++) {ser}_append(cf_sd{id}, cf_sh{id}->data[cf_i{id}]); " +
            $"for (int cf_i{id} = cf_sd{id}->len - 1; cf_i{id} > 0; cf_i{id}--) {{ " +
            $"long long cf_j{id} = cufet_rng_below(cf_i{id} + 1); " +
            $"{EmitCType(st.ElementType)} cf_t{id} = cf_sd{id}->data[cf_i{id}]; " +
            $"cf_sd{id}->data[cf_i{id}] = cf_sd{id}->data[cf_j{id}]; cf_sd{id}->data[cf_j{id}] = cf_t{id}; }}");
        return $"cf_sd{id}";
    }

    // ── Matrix (Arc 1D) ───────────────────────────────────────────────────────

    private string MatrixCType() { _usesMatrix = true; return "CufetMatrix*"; }

    private string ChaseCType() { _usesChase = true; return "CufetChase*"; }

    // A +/−/× whose operands are both matrices — routed through the FALLIBLE machinery (dimension
    // mismatch is a Cufet failure the typechecker requires handling for).
    private bool IsMatrixOp(BinaryExpression b) =>
        b.Op is TokenType.Plus or TokenType.Minus or TokenType.Star
        && TypeOf(b.Left) is MatrixType && TypeOf(b.Right) is MatrixType;

    // ★ `m * 2` / `2 * m`. Separate from IsMatrixOp because it CANNOT FAIL: no failable wrapper,
    // no `Try` required of the writer, and a plain `CufetMatrix*` comes back.
    private bool IsMatrixScale(BinaryExpression b) =>
        b.Op is TokenType.Star
        && ((TypeOf(b.Left) is MatrixType && TypeOf(b.Right) is NumberType)
         || (TypeOf(b.Left) is NumberType && TypeOf(b.Right) is MatrixType));

    // The scaled matrix, inline — the matrix operand first, whichever side it was written on.
    private string EmitMatrixScale(BinaryExpression b)
    {
        _usesMatrix = true;
        bool matrixOnLeft = TypeOf(b.Left) is MatrixType;
        string mat    = EmitExpr(matrixOnLeft ? b.Left  : b.Right);
        string factor = EmitExpr(matrixOnLeft ? b.Right : b.Left);
        return $"cufet_mat_scale({mat}, {factor})";
    }

    // The raw `matrix or failure` (cfl) for a matrix binary op: the runtime fn returns NULL on a
    // dimension mismatch; the emit site wraps that into the cfl with the interpreter's exact
    // deterministic message + "dimension-mismatch" category.
    private string EmitMatrixOpRaw(BinaryExpression b)
    {
        _usesMatrix = true;
        string cfl = RegisterFailableStruct(new FailureType(MatrixType.Instance));
        string l = EmitExpr(b.Left);
        string r = EmitExpr(b.Right);
        (string fn, string msg) = b.Op switch
        {
            TokenType.Plus  => ("cufet_mat_add", "matrices must have equal dimensions for addition"),
            TokenType.Minus => ("cufet_mat_sub", "matrices must have equal dimensions for subtraction"),
            _               => ("cufet_mat_mul", "left matrix columns must equal right matrix rows for matrix product"),
        };
        int id = _freshId++;
        _preEmits.Add(
            $"{cfl} cf_mx{id}; {{ CufetMatrix* cf_mr{id} = {fn}({l}, {r}); " +
            $"if (cf_mr{id}) {{ cf_mx{id}.is_failure = 0; cf_mx{id}.val = cf_mr{id}; cf_mx{id}.message = 0; cf_mx{id}.category = 0; }} " +
            $"else {{ cf_mx{id}.is_failure = 1; cf_mx{id}.message = \"{msg}\"; cf_mx{id}.category = \"dimension-mismatch\"; }} }}");
        return $"cf_mx{id}";
    }

    private string EmitMatrixSized(MatrixSized ms)
    {
        _usesMatrix = true;
        string r = EmitExpr(ms.Rows);
        string c = EmitExpr(ms.Cols);
        string f = ms.Fill != null ? EmitExpr(ms.Fill) : "cufet_dec_from_ll(0)";
        return $"cufet_mat_sized({r}, {c}, {f}, {ms.Line})";
    }

    private string EmitMatrixAccess(MatrixAccess ma)
    {
        _usesMatrix = true;
        string m = EmitExpr(ma.Matrix);
        string r = EmitExpr(ma.Row);
        string c = EmitExpr(ma.Col);
        return $"cufet_mat_get({m}, cufet_to_int({r}), cufet_to_int({c}), {ma.Line})";
    }

    // `a matrix with ((1, 2), (3, 4))` — dimensions are literal-known; elements evaluated row-major
    // (the interpreter's order, so side effects and preemits sequence identically).
    /// <summary>`a chase` — a new, empty buffer.</summary>
    private string EmitChaseLiteral() { _usesChase = true; return "cufet_chase_new()"; }

    private string EmitMatrixLiteral(MatrixLiteral ml)
    {
        _usesMatrix = true;
        int rows = ml.Rows.Count, cols = ml.Rows[0].Count;
        int id = _freshId++;
        string tmp = $"cf_mt{id}";
        _preEmits.Add($"CufetMatrix* {tmp} = cufet_mat_new({rows}, {cols});");
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                string el = EmitExpr(ml.Rows[r][c]);
                _preEmits.Add($"{tmp}->data[{r * cols + c}] = {el};");
            }
        return tmp;
    }

    private string EqCall(string a, string b, CufetType t) => t switch
    {
        NumberType => $"cufet_cmp({a}, {b}) == 0",
        // Value alone, ignoring base and width — 0xFF, 0x00FF and 0b11111111 are one pattern
        // written three ways. This must stay identical to the `is` operator's own lowering
        // (EmitBinary's BitsType arm); a container that compared the struct field by field would
        // call those three different and disagree with `a is b` on the very same values.
        BitsType   => $"(({a}).value == ({b}).value)",
        FactType   => $"({a} == {b})",
        TextType   => $"strcmp({a}, {b}) == 0",
        SeriesType st => $"{RegisterSeriesStruct(st)}_eq({a}, {b})",
        RecordType rt => $"{RegisterRecordStruct(rt)}_eq({a}, {b})",
        ObjectType ot => $"{ObjStructName(ot.Name)}_eq({a}, {b})",
        VoidableType vt => $"{RegisterVoidableStruct(vt)}_eq({a}, {b})",
        MapType => $"({a} == {b})",   // maps: reference (pointer) equality, like the interpreter
        MatrixType => $"({a} == {b})",   // matrices: reference equality (interpreter ValuesEqual fallthrough)
        // ⚠ STRUCTURAL, unlike the two above, and matching the interpreter deliberately. A chase
        // follows collection conventions, and a series of the same characters compares by content;
        // a buffer comparing by pointer would be the odd one out in its own family.
        ChaseType => $"cufet_chase_eq({a}, {b})",
        RabbitType => $"cufet_rabbit_eq({a}, {b})",
        // Two addresses are the same when they are the same pointer — the only question anyone can
        // ask about one without reading through it. The interpreter's ForeignAddress is a record
        // over the same bits, so both sides compare identically.
        AddressType => $"({a} == {b})",
        FunctionType => $"(({a}).fn == ({b}).fn && ({a}).env == ({b}).env)",   // function values: reference equality
        // The same struct, so the same comparison. Two names for one axiom share a thunk, which is
        // what makes them compare equal — identity is the SOURCE, as it is everywhere else here.
        AxiomType => $"(({a}).fn == ({b}).fn && ({a}).env == ({b}).env)",
        UnionType uqo when uqo.Cases == null => $"{OpenUnionStruct}_eq({a}, {b})",
        UnionType uq when uq.Cases != null => $"{RegisterUnionStruct(uq)}_eq({a}, {b})",   // tag + payload
        _ => throw new CompilerException($"equality on a '{FormatTypeName(t)}' is not yet supported by the compiler.")
    };

    // The C expression that writes `valExpr` inline (no trailing newline), dispatching
    // on its static type — used by record/object write helpers and by State.
    private string WriteCall(string valExpr, CufetType t) => t switch
    {
        NumberType => $"cufet_write_number({valExpr})",
        BitsType   => $"cufet_write_bits({valExpr})",
        FactType   => $"cufet_write_fact({valExpr})",
        TextType   => $"cufet_write_text({valExpr})",
        RabbitType => $"cufet_rabbit_write({valExpr})",
        SeriesType st => $"{RegisterSeriesStruct(st)}_write({valExpr})",
        RecordType rt => $"{RegisterRecordStruct(rt)}_write({valExpr})",
        ObjectType ot => $"{ObjStructName(ot.Name)}_write({valExpr})",
        VoidableType vt => $"{RegisterVoidableStruct(vt)}_write({valExpr})",
        MapType mt => $"{RegisterMapStruct(mt)}_write({valExpr})",
        MatrixType => $"cufet_mat_write({valExpr})",
        // ★★ Shown as the COLLECTION it is — `(h, e, l, l, o)` — never as the text it will become.
        // A reader must never mistake a buffer for a `text`; when the text is what you want,
        // `converted to text` is the explicit copy that says so.
        // ⚠ Wrapped in a WRITE. Every other arm here CALLS something that writes; this one
        // builds a string and hands it back, so bare it computed the line and threw it away —
        // a blank line where the interpreter printed the buffer. The oracle caught it.
        ChaseType => $"cufet_write_text(cufet_chase_show({valExpr}))",
        FunctionType => $"printf(\"<function>\")",   // matches the interpreter's Format for a FunctionValue
        AxiomType    => $"printf(\"<axiom>\")",      // and the same for the other kind of callable
        // ★ Never the pointer itself — see the interpreter's Format. Two backends are two
        // processes, so a printed handle could not agree between them however correct both were.
        AddressType => $"printf(\"<address>\")",
        // Opaque like the callables above, and for the same reason: what is worth reading is
        // reached by name (`the message of the failure`). Matches the interpreter's Format.
        FailureMarkerType   => $"printf(\"<failure>\")",
        ExceptionMarkerType => $"printf(\"<exception>\")",
        UnionType uwo when uwo.Cases == null => $"{OpenUnionStruct}_write({valExpr})",
        UnionType uw when uw.Cases != null => $"{RegisterUnionStruct(uw)}_write({valExpr})",   // prints as the underlying value
        _ => throw new CompilerException(
                 $"printing a '{FormatTypeName(t)}' is not yet supported by the compiler.")
    };

    // ── UNMK frame setup ───────────────────────────────────────────────────────
    // A FRAME (function / method / getter / setter / overload / closure / task / unmaker body) is
    // NOT a block scope — its own Defines don't fire (the interpreter's SaveScopes/RestoreScopes
    // bypasses RunScopeUnmakers). ⚠ A TASK is the one exception and the caller adds the block back:
    // RunTaskBody uses EnterScope/ExitScope, not SaveScopes, so a task body's own Defines DO fire.
    // So reset `_scopeDepth` to 0 (nested blocks bump it) and capture the registry depth at entry
    // into `_frameUnmakerBase` so a `return` runs the still-open block
    // unmakers to it (a return unwinds through the frame's blocks, firing each — d17/d19).
    private string UnmakerCName(string typeName) => "cu_" + typeName.Replace('-', '_');

    // ── Concurrency (CONC.A+B) ────────────────────────────────────────────────

    // Whether the program needs the concurrency substrate emitted.
    //
    // Presence of any of these nodes ANYWHERE in the tree — the generic walk supplies the descent,
    // so there is no list of block-bearing statements to fall behind. That list is what previously
    // went stale: it had no arm for PullStatement, and concurrency inside a book pull was invisible
    // to this scan, so the substrate was never emitted and a channel declared inside a rabbit was
    // refused for not being in one. Interpreted fine; compiled, refused.
    //
    // The walk is broader than the old arms in one way — a channel created inside, say, a call
    // argument now counts, where before only Define/becomes/State values were inspected. That
    // direction is safe: over-detection emits substrate nothing uses, under-detection miscompiles.
    private bool ProgramUsesConcurrency(IEnumerable<IStatement> stmts) =>
        AstSearch.Contains(stmts, n =>
            n is LaunchTaskStatement or SendStatement or CloseStatement
            or OutputStatement or ForEachFromInputStatement
            or ChannelCreation or DeliveryExpression
            // A pipe needs the substrate unless every stage is a subprocess run — those lower to
            // plain sequential calls with no channel between them.
            || (n is PipeExpression pe && !FlattenPipeAll(pe).TrueForAll(x => x is RunExpression)));

    // Whether the program uses interrupt constructs (CONC.E) — so main installs the SIGINT handler +
    // landing pad and the substrate is emitted. Discovered up front (like concurrency) because main's
    // top is emitted before its body is walked. Concurrency programs also get the substrate (their
    // blocked channel-waits are made interruptible), gated separately at emission.
    //
    // ★ This was a hand-written switch with an arm per statement type, and it had gone stale: no arm
    // for JudgeStatement, so a poll inside a judgement compiled with NO signal substrate while the
    // interpreter handled it cooperatively — a silent divergence. The interpreter decides the same
    // question with the same walk (Interpreter.MentionsInterrupts), so the two cannot drift again.
    // `YieldStatement` is here and not there on purpose: Yield needs the substrate to have a
    // checkpoint to unwind from, but writing `Yield.` is not a claim to handle your own interrupts.
    private bool ProgramUsesSignals(IEnumerable<IStatement> stmts) =>
        AstSearch.Contains(stmts,
            n => n is InterruptRequestedExpression or AcknowledgeInterruptStatement or YieldStatement);

    private static bool ExprUsesChannel(IExpression e) => e switch
    {
        ChannelCreation or DeliveryExpression => true,
        ButVoidDefault bvd => ExprUsesChannel(bvd.Voidable) || ExprUsesChannel(bvd.Default),
        BinaryExpression b => ExprUsesChannel(b.Left) || ExprUsesChannel(b.Right),
        UnaryExpression u  => ExprUsesChannel(u.Operand),
        _ => false,
    };

    // `a channel of T` — any element type (channel-of-T). Allocated with the enclosing rabbit tracking
    // it so it's freed at Done. (after tasks join), and given a per-element-type `freeval` so un-received
    // envelopes are freed on teardown. Returns a temp so the tracking side-effect precedes use.
    private string EmitChannelCreation(ChannelCreation cc)
    {
        _usesConcurrency = true;
        if (_rabbitCtx.Count == 0)
            throw new CompilerException("a channel has to be created inside a rabbit — put `Define <name> as a channel of <type>.` inside a `Pull a rabbit. … Done.` block, which is what frees it when the rabbit ends.");
        RegisterChanElem(cc.ElementType, isTop: true);
        string ctx = _rabbitCtx[^1];
        int id = _freshId++;
        string tmp = $"cf_ch{id}";
        _preEmits.Add($"cufet_chan* {tmp} = cufet_chan_new({ChanFreeEnv(cc.ElementType)}); cf_chan{ctx}[cf_nchan{ctx}++] = {tmp};");
        return tmp;
    }

    // `the delivery from ch` → voidable T: blocking receive; void when empty-and-closed. The received
    // heap envelope is deep-copied into this thread's arena, then freed (the A+B model, for any T).
    private string EmitDelivery(DeliveryExpression de)
    {
        _usesConcurrency = true;
        var elem = (TypeOf(de.Channel) as ChannelType)?.ElementType ?? TNumber;
        RegisterChanElem(elem, isTop: true);
        string ec = EmitCType(elem);
        string cvd = RegisterVoidableStruct(new VoidableType(elem));
        string ch = EmitExpr(de.Channel);
        int id = _freshId++;
        // recv → 1 (value), 0 (empty+closed → void), -1 (interrupted while blocked). On -1 run the
        // checkpoint: if this thread has a landing pad it unwinds; otherwise the interrupt reads as void.
        _preEmits.Add($"void* cf_de{id} = NULL; int cf_dh{id} = cufet_chan_recv({ch}, &cf_de{id}); if (cf_dh{id} < 0) {{ cufet_checkpoint(); cf_dh{id} = 0; }}");
        _preEmits.Add($"{ec} cf_dv{id} = {{0}}; if (cf_dh{id}) {{ cf_dv{id} = {ChanArenaCopy(elem)}(cf_de{id}); {ChanFreeEnv(elem)}(cf_de{id}); }}");
        return $"(cf_dh{id} ? ({cvd}){{ .has = 1, .val = cf_dv{id} }} : ({cvd}){{ .has = 0 }})";
    }

    // `an interrupt is requested` → the current interrupt flag as a fact (0/1).
    private string EmitInterruptRequested()
    {
        _usesSignals = true;
        return "(cufet_interrupted ? 1 : 0)";
    }

    // `Have rabbit start a task [as <name>]: … Done.` → a pthread. Captured enclosing locals are
    // snapshot into a heap arg struct at spawn (deep-copy-at-spawn — value types copied, channels
    // shared) so a parent mutation after spawn can't race the task's read. The thread runs in its
    // own (thread-local) arena. A NAMED task (CONC.C) additionally returns a heap-bridged result via
    // the pthread void* return; `the awaited result of <name>` joins it, deep-copies the result into
    // the awaiter's arena, and frees the bridge. The result is deep-copied to the heap on return via
    // the channel-of-T copy-family (any T — text/series/map/record/object + voidable/failable), so the
    // bridge is arena-independent; a failable result's failure message must be a static literal
    // (arena-templated I/O-failure messages would dangle past the task's arena_pop — I/O-in-tasks out).
    private void EmitLaunchTask(StringBuilder sb, LaunchTaskStatement lts, string indent)
    {
        if (_rabbitCtx.Count == 0)
            throw new CompilerException("'Have rabbit start a task' requires an enclosing rabbit.");
        _usesConcurrency = true;
        string ctx = _rabbitCtx[^1];
        int tid = _taskCounter++;

        // Result type (named tasks only) — inferred from the body's returns, mirroring the checker.
        CufetType? resultType = lts.Name != null ? InferTaskResultType(lts.Body) : null;
        string? resultCType = null;
        if (lts.Name != null && resultType != null)
        {
            // Register the result type with the deep-copy family so the return heap-bridge (heapenv on
            // return) and the await (arenacopy + freeenv) can cross the task→awaiter boundary for any T.
            RegisterChanElem(resultType, isTop: true);
            resultCType = EmitCType(resultType);
            _taskInfos[lts.Name] = (ctx, resultCType, resultType);
            _varTypes[lts.Name] = new TaskHandleType(resultType);
        }

        // Captured free variables = referenced enclosing locals not defined inside the task body.
        var refs = new HashSet<string>(); var defs = new HashSet<string>();
        foreach (var s in lts.Body) CollectRefsDefs(s, refs, defs);
        var caps = refs.Where(r => !defs.Contains(r) && _varTypes.ContainsKey(r)).OrderBy(x => x).ToList();
        // TCAP — a capture of ANY type is allowed, but the task must not MUTATE one. Every capture
        // crosses a thread boundary and so is the task's OWN copy; writing to it changes only that
        // copy. See TaskBodyMayMutate for why this is a refusal rather than a silent copy.
        // A captured TASK HANDLE means this body awaits another task. It rides as the awaited task's
        // result-box pointer — shared, never copied, because a box is a synchronisation object like
        // a channel rather than arena memory.
        //
        // A cycle cannot be built out of these: awaiting a task requires its name to be in scope,
        // which means it was declared earlier, so the wait graph is a DAG by construction and the
        // front end rejects the forward reference a cycle would need.
        foreach (var c in caps) if (_varTypes[c] is TaskHandleType) _usesConcurrency = true;

        foreach (var c in caps)
            if (TaskBodyMayMutate(lts.Body, c))
            {
                if (CaptureWriteIsObservable(lts.Body, c))
                    throw new CompilerException(
                        $"this task changes '{c}', which it captured from outside the task. A task gets its own copy of " +
                        $"everything it captures — captures cross a thread boundary — so the change would not be visible " +
                        $"outside the task, and two tasks changing it at once would race. Send the result back through a " +
                        $"channel, or return it from a named task and await it.");

                // Nothing outside the task ever looks at it, so the two backends cannot be told
                // apart here: the write lands on the enclosing binding interpreted and on the
                // task's own copy compiled, and either way nobody reads the result.
                Diagnostics.Warn(
                    $"this task changes '{c}', which it captured from outside the task, and nothing outside the task " +
                    $"reads '{c}' afterwards — so the change is discarded. A task gets its own copy of everything it " +
                    $"captures. If the new value was meant to be seen, send it back through a channel, or return it " +
                    $"from a named task and await it.",
                    lts.Line, lts.Column);
            }

        // TCAP — a capture crosses a thread boundary, so it travels the same way a channel message
        // does: a POD (number/fact) rides in the arg struct directly, a CHANNEL is shared by pointer
        // (it IS the sharing primitive — mutex-protected, not arena memory), and everything else is
        // deep-copied into a malloc'd envelope at spawn and copied into the task's OWN arena on
        // arrival. That keeps the two threads' arenas completely disentangled, exactly as
        // `Send v through ch` already does — the same cchan_<i> family, no new machinery.
        // A TASK HANDLE joins the shared-by-pointer group for the same reason a channel does.
        bool Bridged(string c) => _varTypes[c] is not (NumberType or FactType or ChannelType or TaskHandleType);
        foreach (var c in caps) if (Bridged(c)) RegisterChanElem(_varTypes[c], isTop: true);

        // ★ No task-handle special case any more: EmitCType knows the type, so this asks it.
        string CapCType(string c) => Bridged(c) ? "void*" : EmitCType(_varTypes[c]);

        // Arg struct + thread function (accumulated; emitted before the bodies). cf_selfbox is where
        // a named task publishes its own result; it is unused by fire-and-forget tasks.
        _taskFns.AppendLine($"struct cufet_targ{tid} {{ cufet_rbox* cf_selfbox; {string.Join(" ", caps.Select(c => $"{CapCType(c)} {MangleName(c)};"))} }};");
        _taskFns.AppendLine($"static void* cufet_task{tid}(void* argp) {{");
        // ⚠ Per thread, and this is why: a rabbit runs on its OWN stack, with its own bounds and
        // its own need for somewhere to land when that stack runs out. The handler is installed
        // process-wide, but the alternate stack and the bounds it compares against are thread-local,
        // so a worker that overflows without this would fall through to the default and die silently
        // — exactly the thing being fixed, hidden one level down.
        _taskFns.AppendLine($"    cufet_watch_stack();");
        _taskFns.AppendLine($"    struct cufet_targ{tid}* cf_a = (struct cufet_targ{tid}*)argp;");
        // The arena must exist before a bridged capture can be copied into it.
        _taskFns.AppendLine($"    cufet_arena_push();");
        foreach (var c in caps)
        {
            string m = MangleName(c);
            // A captured task handle materialises under the same name the rabbit body uses for it,
            // so an await inside this task emits exactly the expression it would outside one.
            if (_varTypes[c] is TaskHandleType)
            {
                _taskFns.AppendLine($"    cufet_rbox* {m} = cf_a->{m}; (void){m};");
                continue;
            }
            string t = EmitCType(_varTypes[c]);
            _taskFns.AppendLine(Bridged(c)
                ? $"    {t} {m} = {ChanArenaCopy(_varTypes[c])}(cf_a->{m}); {ChanFreeEnv(_varTypes[c])}(cf_a->{m}); (void){m};"
                : $"    {t} {m} = cf_a->{m}; (void){m};");
        }
        var savedRet     = _currentReturnType;
        var savedTaskRet = _currentTaskReturn;
        var savedInTask  = _inTaskBody;
        var savedExcOpen = _excOpen; _excOpen = 0;   // task body = its own function frame
        _currentReturnType = resultType;
        _currentTaskReturn = (resultType, resultCType);
        _inTaskBody        = true;
        // INT.1 — this thread's interrupt landing pad, mirroring cufet_pipe_stage. Without it a
        // worker's `cufet_checkpoint()` is a silent no-op (the check requires cufet_pad_set, which
        // is _Thread_local), so Ctrl-C during a task was simply ignored and the program hung.
        // The body is INLINE here rather than behind a function pointer as it is for a pipe stage,
        // but wrapping the emitted statements works the same way. Only `cf_a` is read after the
        // pad, and it is assigned before the sigsetjmp and never reassigned, so C11's
        // indeterminate-locals rule is satisfied (the same argument the pipe stage relies on).
        bool pad = _usesSignals || _usesConcurrency;
        string bodyIndent = "    ";
        if (pad)
        {
            _taskFns.AppendLine($"#if defined(__unix__) || defined(__APPLE__)");
            _taskFns.AppendLine($"    if (CUFET_SETJMP(cufet_thread_top) == 0) {{ cufet_pad_set = 1;");
            _taskFns.AppendLine($"#endif");
            bodyIndent = "        ";
        }
        var savedTF = EnterFrame(_taskFns, bodyIndent);
        // ★ A task body IS a block scope, unlike every other frame — which is why this is
        // EmitScopedBlock and a function body is not. The interpreter's RunTaskBody wraps it in
        // EnterScope/ExitScope, NOT the SaveScopes/RestoreScopes a call gets, so an object Defined
        // at the task body's own top level is unmade at its `Done.`. Emitting it as a plain frame
        // meant `_scopeDepth` stayed 0 there and the Define never REGISTERED an unmaker at all —
        // so no exit path could run it, and the epilogue's run-to-0 had nothing to find. Measured
        // both ways: a task that faults and a task that completes normally each printed `closed`
        // interpreted and nothing compiled. It stays a frame for everything else (rabbit depth,
        // arena base, escape arithmetic); only the block scope is added back.
        EmitScopedBlock(_taskFns, lts.Body, bodyIndent);
        ExitFrame(savedTF);
        _currentReturnType = savedRet;
        _excOpen = savedExcOpen;
        _currentTaskReturn = savedTaskRet;
        _inTaskBody        = savedInTask;
        if (pad)
        {
            _taskFns.AppendLine($"#if defined(__unix__) || defined(__APPLE__)");
            _taskFns.AppendLine($"    }}");
            _taskFns.AppendLine($"#endif");
        }
        // Fall-through epilogue — reached by a fire-and-forget/void task finishing normally, and by
        // an INTERRUPTED task of any kind unwinding to the pad above. A value-returning task is
        // required to return on every path (CheckLaunchTask), so for it this is the interrupt path
        // only, and it yields NULL: the task is ABANDONED and reaped by the rabbit's structured
        // join, exactly as an interrupted pipe stage is. Run this thread's pending unmakers and
        // close its open files first — both registries are _Thread_local, so a worker touches only
        // its own — otherwise an interrupt would skip every destructor and lose buffered writes.
        //
        // ★ And they run BEFORE the publish below, for the reason the value-returning return
        // documents: publishing releases the awaiter, so doing it first lets that thread run
        // concurrently with this one's unmakers, which are user code that can print.
        _taskFns.AppendLine($"    {UnmakerRunStmt("0")}cufet_close_files_from(0);");
        // ★ Publish nothing, but publish it. A task reaching here either fell off the end or was
        // abandoned at its landing pad by an interrupt, and in both cases it never published a
        // result — so any awaiter would wait forever. An empty publish wakes them with NULL, which
        // the await site reads as "no result" and turns into a checkpoint. Harmless when nobody is
        // waiting, and NULL for a fire-and-forget task whose box does not exist.
        _taskFns.AppendLine($"    cufet_rbox_publish(cf_a->cf_selfbox, NULL);");
        _taskFns.AppendLine($"    cufet_arena_pop();");
        _taskFns.AppendLine($"    free(cf_a);");
        _taskFns.AppendLine($"    return NULL;");
        _taskFns.AppendLine($"}}");

        // A named task gets a result box, held in a variable named exactly as the task is. That
        // naming is load-bearing: inside a task that awaits this one, the capture machinery
        // materialises the captured handle under the SAME mangled name, so `the awaited result of
        // <name>` emits one expression that is correct in the rabbit body and in a task alike.
        if (lts.Name != null && resultType != null)
            sb.AppendLine($"{indent}cufet_rbox* {MangleName(lts.Name)} = cufet_rbox_new({ChanFreeEnv(resultType)});");

        // Spawn: snapshot captures into the heap arg (PODs copied, channels shared by pointer,
        // regions deep-copied into a heap envelope the task owns and frees) + create.
        sb.AppendLine($"{indent}{{ struct cufet_targ{tid}* cf_a = (struct cufet_targ{tid}*)malloc(sizeof(struct cufet_targ{tid}));");
        foreach (var c in caps)
            sb.AppendLine($"{indent}  cf_a->{MangleName(c)} = {(Bridged(c) ? $"{ChanHeapEnv(_varTypes[c])}({MangleName(c)})" : MangleName(c))};");
        // The task publishes into its own box, so it needs a pointer to it; the rabbit records the
        // same pointer per slot so the teardown can free it.
        if (lts.Name != null && resultType != null)
        {
            sb.AppendLine($"{indent}  cf_a->cf_selfbox = {MangleName(lts.Name)};");
            sb.AppendLine($"{indent}  cf_rbox{ctx}[cf_nthr{ctx}] = {MangleName(lts.Name)};");
        }
        else
            // Fire-and-forget: malloc does not zero, and the unwind path publishes unconditionally.
            sb.AppendLine($"{indent}  cf_a->cf_selfbox = NULL;");
        sb.AppendLine($"{indent}  pthread_create(&cf_thr{ctx}[cf_nthr{ctx}++], NULL, cufet_task{tid}, cf_a); }}");
    }

    // A named task's C-identifier suffix (Cufet ids have no '_'; '-'→'_' keeps it valid).
    private static string TaskSuffix(string name) => name.Replace('-', '_');

    // ── TCAP — may this task body CHANGE the captured binding `name`? ──────────────────────────
    // Every capture crosses the thread boundary as the task's OWN copy — a value one is snapshot
    // into the arg struct, a region one is deep-copied through the channel-send bridge. So a task
    // that mutated a capture would change only that copy, while the interpreter, which hands task
    // bodies the LIVE enclosing binding, changes the original. The rabbit's join is a happens-before
    // edge, so both answers are well-defined and they DIFFER: the class that never ships silently.
    // Hence the refusal.
    //
    // ★ This applies to plain numbers exactly as much as to series. It originally did not, and that
    // gap was a live divergence: `tally becomes tally + 5` inside a task printed 5 interpreted and
    // 0 compiled. Nothing about the value being small or copyable makes the write meaningful.
    //
    // Sharing instead is not available for regions: arenas are thread-local, so a mutation that
    // grows a shared series reallocates into the TASK's arena and dangles the parent's pointer when
    // the task pops. And refusing is the honest answer on its own terms — two tasks writing one
    // captured variable is a real data race that the cooperative interpreter merely hides.
    //
    // The statement list below IS the complete set of mutating statements in Ast.cs (the ones
    // carrying a write target). A CALL is treated as a possible mutation because argument binding
    // shares series and maps with the callee, so a callee can mutate through its parameter. Target
    // expressions are matched by "mentions the name anywhere", which over-approximates safely.
    private bool TaskBodyMayMutate(object? node, string name)
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
                        && TaskBodyMayMutate(val, name)) found = true;
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
