using System.Text;
using Cufet.Interpreter;
using Cufet.Lexer;

namespace Cufet.Compiler;

public sealed class CodeGenerator
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

    private bool UsesUnmakers => _unmakeDefs.Count > 0;
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
    private readonly Dictionary<(string TypeName, TokenType Op), OperatorOverloadDeclaration> _overloadDefs = new();
    // Memoized inferred return types (an overload's return is inferred from its body, like a lambda's,
    // and may be ANY type — including `T or failure` when the body returns a failure).
    private readonly Dictionary<(string TypeName, TokenType Op), CufetType?> _overloadReturnTypes = new();
    private readonly HashSet<(string TypeName, TokenType Op)> _overloadInferring = new();

    // ── Interfaces (Arc 3, DD.1) — MONOMORPHIZATION ────────────────────────────
    // MEASURED (and design-locked): interface polymorphism exists at exactly ONE position — the
    // function parameter — and the argument must be a CONCRETE conformer at the call site. It can't
    // be stored in a series, returned, put in a field, reassigned, or forwarded (all rejected by the
    // shared front-end). So the concrete type is statically known at EVERY call site ⇒ emit one
    // specialized copy of each interface-taking callable per conformer actually passed. Inside a
    // specialization the parameter has its concrete type, so method calls fall back to the existing
    // direct dispatch and `s is a dog` folds via StaticKindMatches. No type tags. No vtables.
    // Seeded with `module`, the built-in marker that says a type may be pulled. It is declared
    // nowhere in the program, so conformance validation below would otherwise reject every module.
    // See TypeChecker.ModuleInterface for why it requires nothing.
    private readonly Dictionary<string, InterfaceDefinition> _interfaceDefs = new()
    {
        [TypeChecker.ModuleInterface] = new InterfaceDefinition(TypeChecker.ModuleInterface, [], 0, 0),
    };
    // Interface-taking callables are NEVER emitted unspecialized (their param has no concrete C type).
    private readonly Dictionary<string, BindStatement> _ifaceFuncs = new();
    private readonly Dictionary<(string Owner, string Method), BindStatement> _ifaceMethods = new();
    // Requested specializations, keyed by emitted C name. THE DISCOVERY IS THE EMISSION: a call site
    // registers its specialization as it emits (the CAT.2 trick — a walker could miss a site, but the
    // emitter by construction cannot), then a worklist drains to a fixed point.
    private readonly Dictionary<string, (BindStatement Bind, string? Owner, IReadOnlyList<string> Concretes)> _ifaceSpecReq = new();
    private readonly HashSet<string> _ifaceSpecDone = new();
    private readonly List<string> _ifaceSpecSigs = new();
    // A runaway cross-product (many interface params × many conformers) would emit thousands of
    // copies; realistic programs are tiny, so cap it and say so rather than exploding silently.
    private const int MaxInterfaceSpecializations = 512;

    // Inside a method body: the receiver's object type name (so `one` and its fields resolve).
    private string? _methodReceiverType;
    // Inside a setter body: the field name being set, so `one's <field> becomes X` writes raw
    // (bypasses the setter) — preventing infinite recursion, matching the interpreter's _inSetterFor.
    private string? _inSetterForField;

    // Cached type singletons (record-equality types, so `new NumberType() == Number`).
    private static readonly CufetType TNumber = new NumberType();
    private static readonly CufetType TBits   = new BitsType();
    private static readonly CufetType TFact   = new FactType();
    private static readonly CufetType TText    = new TextType();
    private static readonly CufetType TVoid    = new VoidType();
    private static readonly CufetType TFailMarker = new FailureMarkerType();

    // ── Emitted C runtime ─────────────────────────────────────────────────
    // Self-contained: compiles with plain `gcc file.c`, no external libraries.
    // The software decimal (CufetDec) is bit-identical to .NET System.Decimal:
    //   value = (sign ? -1 : 1) * coef * 10^-scale,  coef <= 2^96-1,  scale in [0,28]
    // Precision-overflow (multiply, division) rounds half-to-even, exactly as
    // measured against the interpreter's decimal. u256 (four 64-bit limbs) carries
    // the up-to-192-bit intermediate products and scaled division numerators.
    private const string RuntimePreamble =
"""
#define _GNU_SOURCE   /* expose POSIX (fileno, fork, execvp, poll…) regardless of -std */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <errno.h>
#include <sys/stat.h>
#include <setjmp.h>
#include <stdarg.h>
#if defined(_WIN32)
#include <io.h>
#include <fcntl.h>
#endif

/* ───────── setjmp WITHOUT a Windows SEH unwind ─────────
   ★★ On x86-64 mingw-w64, plain `setjmp(b)` expands to `_setjmp((b), __builtin_frame_address(0))`
   (setjmp.h), and that saved frame pointer makes `longjmp` perform a full SEH unwind through
   ntdll's RtlUnwindEx. At -O2 that unwinder reads stack memory it cannot validate and
   ACCESS-VIOLATES depending on what happens to be on the stack — measured at 121 crashes in 3000
   serial runs of one binary that raises and catches once. Passing NULL as the context makes
   longjmp restore registers directly and skip the unwind: 3000 runs, 0 crashes.

   ★ Skipping it is not a workaround, it is what this runtime already assumes. The unwinder's job
   is to run __finally blocks and C++ destructors between the jump and its target; generated Cufet
   C has neither, and cufet_raise runs the unmakers, closes the files and pops the arenas ITSELF
   before it jumps. There was never anything for RtlUnwindEx to do.

   ⚠ Two arguments is the mingw-w64 spelling. glibc's `_setjmp` takes one, so this is a _WIN32
   branch and nothing else changes on POSIX.

   ⚠ It presented as a test that "flaked occasionally" for weeks. It was a 4%-of-runs crash in
   every compiled program that catches an exception on Windows, and the reason it looked rare is
   that one suite run launches such a binary a handful of times. */
#if defined(_WIN32)
#define CUFET_PLAIN_SETJMP(b) _setjmp((b), NULL)
#else
#define CUFET_PLAIN_SETJMP(b) setjmp(b)
#endif

/* ───────── Line endings on stdout ─────────
   Windows opens stdout in TEXT mode, where the C runtime rewrites every '\n' on its way out as
   "\r\n". That is fine for a line terminator and wrong for everything else: a '\n' the program
   put INSIDE a text value is data, and rewriting it makes the compiled backend print something
   the interpreter does not. `State "a\nb".` gave 61 0a 62 interpreted and 61 0d 0a 62 compiled.

   So stdout is switched to BINARY at startup — nothing is rewritten, and what a program prints is
   what it wrote — and the line terminator becomes explicit. CUFET_NL is what `State` appends, and
   it is "\r\n" on Windows because that is what the interpreter's WriteLine emits there; the two
   backends have to agree on the terminator as well as on the data.

   ★ Every newline written to stdout is one or the other, and the distinction is the whole point:
   a TERMINATOR calls cufet_nl(), and DATA is passed through untouched. Emitting a bare newline
   through printf is neither, which is why GeneratedC_UsesTheNewlineMacro refuses one — this is
   the per-site pattern that has been reintroduced by hand three times in this codebase, so it is
   held by a test rather than by remembering. stderr is deliberately left in text mode:
   diagnostics are terminator-only, and both backends already agree there. */
#if defined(_WIN32)
#define CUFET_NL "\r\n"
#define CUFET_STDOUT_BINARY() (void)_setmode(_fileno(stdout), _O_BINARY)
#else
#define CUFET_NL "\n"
#define CUFET_STDOUT_BINARY() ((void)0)
#endif
#define cufet_nl() fputs(CUFET_NL, stdout)

/* ───────── One `State` is ONE line, even from two threads ─────────
   A State writes in several calls — the value, then the terminator, and a series or record writes
   every element and separator separately. Two threads printing at once interleaved BETWEEN those
   calls, so output came out spliced: `side effectdone` followed by both newlines. Measured at
   roughly 4-8% of runs on a two-thread program whose output is otherwise deterministic.

   Locking the stream for the whole statement makes a State atomic against other threads. stdio
   calls take this same lock individually, so holding it across them is exactly what it is for, and
   an unthreaded program pays one uncontended lock per line.

   No-op off POSIX: tasks are pthreads-only, so a Windows build has no second thread to race. */
#if defined(__unix__) || defined(__APPLE__)
#define cufet_out_lock()   flockfile(stdout)
#define cufet_out_unlock() funlockfile(stdout)
#else
#define cufet_out_lock()   ((void)0)
#define cufet_out_unlock() ((void)0)
#endif

/* ───────── Exceptions (E-prime): setjmp/longjmp over SOFTWARE faults ─────────
   Cufet numbers are software decimals, so divide/modulo-by-zero, series/matrix OOB, etc. are
   software-DETECTED conditions, not hardware signals. Every fault site calls cufet_raise: if a
   `Try to: … In case of exception:` handler is installed (a per-thread jmp_buf stack — nested
   Trys nest; innermost wins), the fault longjmps to it; otherwise the pre-exception behavior
   (print + exit 1) is unchanged. Messages match the interpreter's RuntimeException text
   (arena-allocated so a bound `the message of the exception` outlives later faults). */
#define CUFET_EXC_MAX 64
static _Thread_local jmp_buf cufet_exc_bufs[CUFET_EXC_MAX];
static _Thread_local int cufet_exc_top = -1;
static _Thread_local const char* cufet_exc_msg = 0;
/* Unmaker (destructor) registry (UNMK) — user `unmaking` bodies run at block scope-exit. A longjmp
   (exception) abandons the C-stack objects those bodies touch, so cufet_raise runs the pending
   unmakers BEFORE it jumps, down to the target handler's snapshot (cufet_exc_um[]). Normal / return /
   Stop / failure-goto exits run them at emit-time via cufet_run_unmakers_to. Zero cost when unused
   (cufet_num stays 0). */
#define CUFET_UNMAKERS_MAX 8192
static _Thread_local void* cufet_um_obj[CUFET_UNMAKERS_MAX];
static _Thread_local void (*cufet_um_fn[CUFET_UNMAKERS_MAX])(void*);
static _Thread_local int cufet_num = 0;
static _Thread_local int cufet_exc_um[CUFET_EXC_MAX];
static void cufet_reg_unmaker(void* o, void (*f)(void*)) { if (cufet_num < CUFET_UNMAKERS_MAX) { cufet_um_obj[cufet_num] = o; cufet_um_fn[cufet_num] = f; cufet_num++; } }
static void cufet_run_unmakers_to(int n) { while (cufet_num > n) { cufet_num--; cufet_um_fn[cufet_num](cufet_um_obj[cufet_num]); } }
static void* cufet_arena_alloc(size_t size);            /* defined with the arena, below */
static void* cufet_arena_alloc_at(int depth, size_t size);
/* Arena depth at each Try's setjmp — the exception MESSAGE is copied into that arena at raise time
   (see cufet_raise) so it survives the arena pops the catch performs on the way in. */
static _Thread_local int cufet_exc_arena[CUFET_EXC_MAX];
static const char* cufet_msgf(const char* fmt, ...) {
    va_list ap; va_start(ap, fmt);
    va_list ap2; va_copy(ap2, ap);
    int need = vsnprintf(NULL, 0, fmt, ap) + 1;
    va_end(ap);
    char* b = (char*)cufet_arena_alloc((size_t)need);
    vsnprintf(b, (size_t)need, fmt, ap2);
    va_end(ap2);
    return b;
}
static void cufet_raise(const char* msg) {
    if (cufet_exc_top >= 0) {
        /* ★ MESSAGE LIFETIME: cufet_msgf allocates in the arena live at the FAULT site, but the catch
           pops every arena deeper than the Try before running the handler — so the message would
           dangle (and its block get reused by the next arena_alloc, reading back as another string).
           Copy it into the TARGET handler's OWN arena, which the catch never pops: that arena outlives
           the handler, and a re-raise outward re-copies into the next handler's arena. Arena-managed,
           so there is no malloc/free discipline and no leak. A raise with no handler needs no copy —
           nothing has been popped yet and we print and exit immediately. */
        if (msg) {
            size_t n = strlen(msg) + 1;
            char* b = (char*)cufet_arena_alloc_at(cufet_exc_arena[cufet_exc_top], n);
            memcpy(b, msg, n);
            msg = b;
        }
        cufet_exc_msg = msg;
        cufet_run_unmakers_to(cufet_exc_um[cufet_exc_top]);
        longjmp(cufet_exc_bufs[cufet_exc_top], 1);
    }
    /* ★ NO HANDLER: the program is ending, but this thread's pending unmakers still run first —
       the same unwind the interpreter performs as the exception propagates out through each open
       block. Skipping them was a real divergence: an object made inside a block that then faulted
       was never unmade, which is invisible until the destructor DOES something (prints, unlinks a
       temp file, releases a lock). The registry is _Thread_local, so a dying worker runs only its
       own; `to(0)` is this thread's whole pending set, which is what unwinding to the top means.
       Free when unused — cufet_num stays 0. Files need no equivalent: exit() flushes them. */
    cufet_run_unmakers_to(0);
    fprintf(stderr, "%s\n", msg);
    exit(1);
}
/* Runtime FILE registry — a longjmp (exception OR interrupt) jumps past the emit-time fclose
   sites, so open files must be runtime-tracked to flush+close on the unwind (the 9B no-data-loss
   discipline, extended to nonlocal jumps). Normal closes unregister → no double-close. */
#define CUFET_FILES_MAX 256
static _Thread_local FILE* cufet_live_files[CUFET_FILES_MAX];
static _Thread_local int cufet_nfiles = 0;
static void cufet_reg_file(FILE* f) { if (cufet_nfiles < CUFET_FILES_MAX) cufet_live_files[cufet_nfiles++] = f; }
static void cufet_close_file(FILE* f) {
    for (int i = cufet_nfiles - 1; i >= 0; i--)
        if (cufet_live_files[i] == f) { for (int j = i; j < cufet_nfiles - 1; j++) cufet_live_files[j] = cufet_live_files[j + 1]; cufet_nfiles--; break; }
    fclose(f);
}
static void cufet_close_files_from(int n) { while (cufet_nfiles > n) fclose(cufet_live_files[--cufet_nfiles]); }

/* 1-based series bounds checks — the messages replicate the interpreter's warm OOB errors.
   (Compiled series access was previously UNCHECKED — E-prime adds the checks so OOB is a real,
   catchable exception instead of undefined behavior.) */
static long long cufet_idx_check(long long idx, int len, const char* name, int line) {
    if (idx >= 1 && idx <= len) return idx;
    if (len == 0)
        cufet_raise(cufet_msgf("There's no item %lld — '%s' is empty. This happened on line %d.", idx, name, line));
    cufet_raise(cufet_msgf("There's no item %lld — '%s' has %d %s (you can reach items 1 through %d). This happened on line %d.",
                           idx, name, len, len == 1 ? "item" : "items", len, line));
    return 0;
}
static long long cufet_last_check(int len, const char* name, int line) {
    if (len == 0) cufet_raise(cufet_msgf("Can't access the last item — '%s' is empty on line %d.", name, line));
    return len;
}

/* ───────── 256-bit unsigned helper (little-endian limbs) ───────── */
typedef struct { unsigned long long v[4]; } cufet_u256;

static void cufet_decimal_overflow(void) { fprintf(stderr, "decimal overflow\n"); exit(1); }

static cufet_u256 u256_zero(void) { cufet_u256 r = {{0,0,0,0}}; return r; }
static cufet_u256 u256_from_u128(unsigned __int128 x) {
    cufet_u256 r; r.v[0] = (unsigned long long)x; r.v[1] = (unsigned long long)(x >> 64); r.v[2] = 0; r.v[3] = 0; return r;
}
static int u256_is_zero(cufet_u256 a) { return (a.v[0] | a.v[1] | a.v[2] | a.v[3]) == 0ULL; }
static int u256_cmp(cufet_u256 a, cufet_u256 b) {
    for (int i = 3; i >= 0; i--) if (a.v[i] != b.v[i]) return a.v[i] < b.v[i] ? -1 : 1;
    return 0;
}
static cufet_u256 u256_add(cufet_u256 a, cufet_u256 b) {
    cufet_u256 r; unsigned __int128 c = 0;
    for (int i = 0; i < 4; i++) { unsigned __int128 s = (unsigned __int128)a.v[i] + b.v[i] + c; r.v[i] = (unsigned long long)s; c = s >> 64; }
    return r;
}
static cufet_u256 u256_sub(cufet_u256 a, cufet_u256 b) { /* assumes a >= b */
    cufet_u256 r; unsigned __int128 br = 0;
    for (int i = 0; i < 4; i++) { unsigned __int128 d = (unsigned __int128)a.v[i] - b.v[i] - br; r.v[i] = (unsigned long long)d; br = (d >> 64) & 1ULL; }
    return r;
}
static cufet_u256 u256_mul(cufet_u256 a, cufet_u256 b) {
    unsigned long long acc[8] = {0,0,0,0,0,0,0,0};
    for (int i = 0; i < 4; i++) {
        unsigned __int128 carry = 0;
        for (int j = 0; j < 4; j++) {
            unsigned __int128 cur = (unsigned __int128)acc[i+j] + (unsigned __int128)a.v[i] * b.v[j] + carry;
            acc[i+j] = (unsigned long long)cur; carry = cur >> 64;
        }
        acc[i+4] += (unsigned long long)carry;
    }
    if (acc[4] | acc[5] | acc[6] | acc[7]) cufet_decimal_overflow();
    cufet_u256 r; r.v[0]=acc[0]; r.v[1]=acc[1]; r.v[2]=acc[2]; r.v[3]=acc[3]; return r;
}
static cufet_u256 u256_mul_small(cufet_u256 a, unsigned long long m) {
    cufet_u256 r; unsigned __int128 c = 0;
    for (int i = 0; i < 4; i++) { unsigned __int128 t = (unsigned __int128)a.v[i] * m + c; r.v[i] = (unsigned long long)t; c = t >> 64; }
    if (c) cufet_decimal_overflow();
    return r;
}
static void u256_divmod(cufet_u256 num, cufet_u256 den, cufet_u256* quo, cufet_u256* rem) {
    cufet_u256 q = {{0,0,0,0}}, r = {{0,0,0,0}};
    for (int i = 255; i >= 0; i--) {
        unsigned long long carry = 0;                                   /* r <<= 1 */
        for (int k = 0; k < 4; k++) { unsigned long long nc = r.v[k] >> 63; r.v[k] = (r.v[k] << 1) | carry; carry = nc; }
        r.v[0] |= (num.v[i >> 6] >> (i & 63)) & 1ULL;                   /* bring down bit i */
        if (u256_cmp(r, den) >= 0) { r = u256_sub(r, den); q.v[i >> 6] |= (1ULL << (i & 63)); }
    }
    *quo = q; *rem = r;
}
static cufet_u256 u256_pow10(int e) { cufet_u256 r = u256_from_u128(1); for (int i = 0; i < e; i++) r = u256_mul_small(r, 10ULL); return r; }
static cufet_u256 u256_mul_u128(unsigned __int128 a, unsigned __int128 b) { return u256_mul(u256_from_u128(a), u256_from_u128(b)); }

/* ───────── Software decimal: bit-identical to .NET System.Decimal ───────── */
typedef struct { unsigned __int128 coef; int scale; int sign; } CufetDec;

/* 2^96 - 1 = decimal.MaxValue coefficient */
static const cufet_u256 CUFET_DEC_MAX = {{0xFFFFFFFFFFFFFFFFULL, 0x00000000FFFFFFFFULL, 0ULL, 0ULL}};

/* Reduce (coef, scale, sign) to canonical form, dropping low digits with round-half-even.
   'inexact' is the division sticky bit: a nonzero true remainder below coef's least digit. */
static CufetDec cufet_dec_reduce(cufet_u256 coef, int scale, int sign, int inexact) {
    for (;;) {
        int d = scale > 28 ? scale - 28 : 0;
        cufet_u256 p = u256_pow10(d), q, r;
        u256_divmod(coef, p, &q, &r);
        while (u256_cmp(q, CUFET_DEC_MAX) > 0) {                        /* drop more until coef fits 96 bits */
            d++; if (scale - d < 0) cufet_decimal_overflow();
            p = u256_mul_small(p, 10ULL); u256_divmod(coef, p, &q, &r);
        }
        int bumped = 0;
        if (d > 0) {                                                    /* round the dropped tail half-to-even */
            cufet_u256 two = u256_from_u128(2), half, dummy;
            u256_divmod(p, two, &half, &dummy);                        /* half = 10^d / 2, exact for d>=1 */
            int c = u256_cmp(r, half);
            if (c > 0 || (c == 0 && (inexact || (q.v[0] & 1ULL)))) { q = u256_add(q, u256_from_u128(1)); bumped = 1; }
        }
        scale -= d;
        if (bumped && u256_cmp(q, CUFET_DEC_MAX) > 0) { coef = q; inexact = 0; continue; }  /* e.g. 999..9 -> 1000..0 */
        CufetDec out;
        out.coef = ((unsigned __int128)q.v[1] << 64) | q.v[0];
        out.scale = scale;
        out.sign = u256_is_zero(q) ? 0 : sign;                         /* zero is unsigned */
        return out;
    }
}

static CufetDec cufet_dec_lit(unsigned long long hi, unsigned long long lo, int scale, int sign) {
    CufetDec d; d.coef = ((unsigned __int128)hi << 64) | lo; d.scale = scale; d.sign = (d.coef == 0) ? 0 : sign; return d;
}
static CufetDec cufet_dec_from_ll(long long v) {
    CufetDec d; d.scale = 0;
    if (v < 0) { d.sign = 1; d.coef = (unsigned __int128)(-(unsigned long long)v); }
    else       { d.sign = 0; d.coef = (unsigned __int128)(unsigned long long)v; }
    if (d.coef == 0) d.sign = 0;
    return d;
}
/* A `number` on its way INTO foreign source. Range-checked, never truncated: C is being handed a
   64-bit integer, and a decimal that is fractional or too large is a mistake in the program rather
   than something to round off quietly. Raised as an ordinary catchable exception, the same class as
   a divide by zero — see cufet_raise. The interpreter checks identically before it marshals. */
static void cufet_raise(const char* msg);
static const char* cufet_msgf(const char* fmt, ...);
static const char* cufet_text_from_dec(CufetDec d);
static long long cufet_foreign_ll(CufetDec d, int line) {
    /* ⚠ The ORIGINAL, kept for the message. Scaling `d` down in place and reporting that prints
       3.50 as "3.5" — a different sentence from the one the interpreter produces for the same
       program, which the oracle compares as strictly as it compares answers. */
    CufetDec as_written = d;
    for (int s = d.scale; s > 0; s--) {
        if (d.coef % 10 != 0)
            cufet_raise(cufet_msgf("Foreign source takes whole numbers, but got %s. This happened on line %d.",
                                   cufet_text_from_dec(as_written), line));   /* ForeignC.WholeArgumentMessage */
        d.coef /= 10; d.scale--;
    }
    if (d.coef > (unsigned __int128)9223372036854775807ULL + (d.sign ? 1u : 0u))
        cufet_raise(cufet_msgf("%s is too large to hand to foreign source. This happened on line %d.",
                               cufet_text_from_dec(as_written), line));       /* ForeignC.LargeArgumentMessage */
    unsigned long long m = (unsigned long long)d.coef;
    return d.sign ? -(long long)m : (long long)m;
}
static int cufet_to_int(CufetDec d) {                                   /* truncate toward zero */
    unsigned __int128 c = d.coef; for (int s = d.scale; s > 0; s--) c /= 10;
    int v = (int)c; return d.sign ? -v : v;
}

static CufetDec cufet_add_signed(CufetDec a, CufetDec b) {
    int s = a.scale > b.scale ? a.scale : b.scale;
    cufet_u256 ca = u256_mul(u256_from_u128(a.coef), u256_pow10(s - a.scale));
    cufet_u256 cb = u256_mul(u256_from_u128(b.coef), u256_pow10(s - b.scale));
    cufet_u256 rc; int rsign;
    if (a.sign == b.sign) { rc = u256_add(ca, cb); rsign = a.sign; }
    else {
        int c = u256_cmp(ca, cb);
        if (c == 0)      { rc = u256_zero(); rsign = 0; }
        else if (c > 0)  { rc = u256_sub(ca, cb); rsign = a.sign; }
        else             { rc = u256_sub(cb, ca); rsign = b.sign; }
    }
    return cufet_dec_reduce(rc, s, rsign, 0);
}
static CufetDec cufet_add(CufetDec a, CufetDec b) { return cufet_add_signed(a, b); }
static CufetDec cufet_sub(CufetDec a, CufetDec b) { b.sign = (b.coef == 0) ? 0 : !b.sign; return cufet_add_signed(a, b); }
static CufetDec cufet_mul(CufetDec a, CufetDec b) {
    cufet_u256 rc = u256_mul_u128(a.coef, b.coef);
    int rsign = (a.coef == 0 || b.coef == 0) ? 0 : (a.sign ^ b.sign);
    return cufet_dec_reduce(rc, a.scale + b.scale, rsign, 0);
}
static CufetDec cufet_neg(CufetDec a) { a.sign = (a.coef == 0) ? 0 : !a.sign; return a; }
static int cufet_cmp(CufetDec a, CufetDec b) {
    if (a.coef == 0 && b.coef == 0) return 0;
    if (a.coef == 0) return b.sign ? 1 : -1;
    if (b.coef == 0) return a.sign ? -1 : 1;
    if (a.sign != b.sign) return a.sign ? -1 : 1;
    int s = a.scale > b.scale ? a.scale : b.scale;
    cufet_u256 ca = u256_mul(u256_from_u128(a.coef), u256_pow10(s - a.scale));
    cufet_u256 cb = u256_mul(u256_from_u128(b.coef), u256_pow10(s - b.scale));
    int c = u256_cmp(ca, cb);
    return a.sign ? -c : c;
}
/* Minimal form, the way .NET leaves a decimal DIVISION: 11/10 is 1.1 at scale 1, not
   1.1000...0 at scale 28. Trailing zeros are invisible when printed (cufet_format_number
   strips them too), so a difference here hides until some LATER operation on the value
   overflows at one scale and not the other — which is exactly how it was found. */
static CufetDec cufet_dec_strip(CufetDec d) {
    while (d.scale > 0 && d.coef != 0 && d.coef % 10 == 0) { d.coef /= 10; d.scale--; }
    return d;
}
static CufetDec cufet_div(CufetDec a, CufetDec b, int line) {
    if (b.coef == 0) cufet_raise(cufet_msgf("Division by zero on line %d.", line));
    int e = (b.scale - a.scale) + 28;                                   /* compute value * 10^28, then reduce */
    cufet_u256 num = u256_from_u128(a.coef), den = u256_from_u128(b.coef);
    if (e >= 0) num = u256_mul(num, u256_pow10(e)); else den = u256_mul(den, u256_pow10(-e));
    cufet_u256 Q, R; u256_divmod(num, den, &Q, &R);
    int rsign = (a.coef == 0) ? 0 : (a.sign ^ b.sign);
    if (u256_cmp(Q, CUFET_DEC_MAX) <= 0) {
        /* Result fits at scale 28: round the sub-unit remainder half-to-even HERE,
           because cufet_dec_reduce only rounds when it must drop digits (d>0), and
           here there are none to drop. 2R vs den decides; tie -> even coefficient.
           (When Q does NOT fit, reduce drops digits and folds R in as a sticky bit.) */
        cufet_u256 twoR = u256_add(R, R);
        int c = u256_cmp(twoR, den);
        if (c > 0 || (c == 0 && (Q.v[0] & 1ULL))) Q = u256_add(Q, u256_from_u128(1));
        return cufet_dec_strip(cufet_dec_reduce(Q, 28, rsign, 0));
    }
    return cufet_dec_strip(cufet_dec_reduce(Q, 28, rsign, !u256_is_zero(R)));
}
static CufetDec cufet_mod(CufetDec a, CufetDec b, int line) {           /* remainder, sign of dividend */
    if (b.coef == 0) cufet_raise(cufet_msgf("Modulo by zero on line %d.", line));
    int e = b.scale - a.scale;
    cufet_u256 num, den;
    if (e >= 0) { num = u256_mul(u256_from_u128(a.coef), u256_pow10(e)); den = u256_from_u128(b.coef); }
    else        { num = u256_from_u128(a.coef); den = u256_mul(u256_from_u128(b.coef), u256_pow10(-e)); }
    cufet_u256 Q, R; u256_divmod(num, den, &Q, &R);                    /* Q = floor(|a|/|b|) */
    CufetDec q; q.coef = ((unsigned __int128)Q.v[1] << 64) | Q.v[0]; q.scale = 0;
    q.sign = (a.sign ^ b.sign); if (q.coef == 0) q.sign = 0;
    return cufet_sub(a, cufet_mul(q, b));                               /* a - trunc(a/b)*b */
}

/* Format matches the interpreter: strip trailing zeros, then plain decimal digits. */
static void cufet_format_number(char* buf, size_t bufsz, CufetDec d) {
    unsigned __int128 c = d.coef; int scale = d.scale;
    while (scale > 0 && c % 10 == 0) { c /= 10; scale--; }
    if (c == 0) { snprintf(buf, bufsz, "0"); return; }
    char ds[40]; int n = 0; unsigned __int128 t = c;
    while (t > 0) { ds[n++] = (char)('0' + (int)(t % 10)); t /= 10; }   /* least-significant first */
    char out[64]; int p = 0;
    if (d.sign) out[p++] = '-';
    if (scale == 0) {
        for (int i = n - 1; i >= 0; i--) out[p++] = ds[i];
    } else if (n > scale) {
        for (int i = n - 1; i >= scale; i--) out[p++] = ds[i];         /* integer part */
        out[p++] = '.';
        for (int i = scale - 1; i >= 0; i--) out[p++] = ds[i];         /* fractional part */
    } else {
        out[p++] = '0'; out[p++] = '.';
        for (int z = 0; z < scale - n; z++) out[p++] = '0';            /* leading fractional zeros */
        for (int i = n - 1; i >= 0; i--) out[p++] = ds[i];
    }
    out[p] = '\0';
    snprintf(buf, bufsz, "%s", out);
}
/* A bit pattern: unsigned, at most 64 bits. `base` is the display base ('x', 'o' or 'b') —
   a pattern shows itself in the base it was written in — and `width` is the bit width the
   literal's digits spelled out, which is what `not` flips within and what pads the display.
   Both ride on the value rather than the type, so every bits value is assignable to any other. */
typedef struct { unsigned long long value; char base; int width; } CufetBits;

/* write_ = format inline (no newline), for nested printing inside records/objects/series.
   print_ = write_ + newline, for a top-level State. */
static void cufet_write_number(CufetDec d) { char b[64]; cufet_format_number(b, sizeof(b), d); printf("%s", b); }
static void cufet_write_fact(int b) { printf("%s", b ? "true" : "false"); }
static void cufet_write_text(const char* s) { printf("%s", s); }
/* Digits are padded out to the declared width, so 0x0F prints as 0x0F and not 0xF. A value that
   outgrew its width prints in the smallest width that holds it — nothing is ever truncated.
   Hex digits are canonically uppercase: a computed value has no literal to take its case from. */
static void cufet_format_bits(char* buf, size_t bufsz, CufetBits x) {
    int per = x.base == 'x' ? 4 : (x.base == 'o' ? 3 : 1);
    int declared = (x.width + per - 1) / per;
    char ds[68];
    int n = 0;
    unsigned long long v = x.value;
    if (v == 0) ds[n++] = '0';
    while (v) {
        int d = (int)(v & (unsigned long long)((1 << per) - 1));
        ds[n++] = (char)(d < 10 ? '0' + d : 'A' + d - 10);
        v >>= per;
    }
    char out[80];
    int p = 0;
    out[p++] = '0';
    out[p++] = x.base;
    for (int i = n; i < declared; i++) out[p++] = '0';
    for (int i = n - 1; i >= 0; i--) out[p++] = ds[i];
    out[p] = '\0';
    snprintf(buf, bufsz, "%s", out);
}
static void cufet_write_bits(CufetBits x) { char b[80]; cufet_format_bits(b, sizeof(b), x); printf("%s", b); }
static void cufet_print_number(CufetDec d) { cufet_write_number(d); cufet_nl(); }
static void cufet_print_fact(int b) { cufet_write_fact(b); cufet_nl(); }
static void cufet_print_text(const char* s) { cufet_write_text(s); cufet_nl(); }
static void cufet_print_bits(CufetBits x) { cufet_write_bits(x); cufet_nl(); }

/* The gates. A result carries the LEFT operand's base and width, widened when the value needs
   more room — left because in real bit code the left operand is the accumulator
   (`flags or MASK`, `flags and not MASK`), so it is the thing you will print. Widening rather
   than truncating means nothing ever silently falls off the end. */
static int cufet_bits_minwidth(unsigned long long v) {
    int n = 0;
    while (v) { n++; v >>= 1; }
    return n;
}
static CufetBits cufet_bits_combine(CufetBits left, unsigned long long result) {
    int min = cufet_bits_minwidth(result);
    CufetBits out;
    out.value = result;
    out.base  = left.base;
    out.width = left.width > min ? left.width : min;
    return out;
}
static CufetBits cufet_bits_and(CufetBits a, CufetBits b) { return cufet_bits_combine(a, a.value & b.value); }
static CufetBits cufet_bits_or (CufetBits a, CufetBits b) { return cufet_bits_combine(a, a.value | b.value); }
static CufetBits cufet_bits_xor(CufetBits a, CufetBits b) { return cufet_bits_combine(a, a.value ^ b.value); }
/* Flips every bit WITHIN the value's own width, so not 0xFF is 0x00 and not 0b1010 is 0b0101.
   Unsigned with a known width is precisely why those are the answers rather than a negative. */
static CufetBits cufet_bits_not(CufetBits a) {
    unsigned long long mask = a.width >= 64 ? ~0ULL : ((1ULL << a.width) - 1ULL);
    CufetBits out;
    out.value = (~a.value) & mask;
    out.base  = a.base;
    out.width = a.width;
    return out;
}

/* Arithmetic. The type is unsigned with a 64-bit ceiling, so a result that would go negative or
   need a 65th bit has no representation and RAISES — the same treatment division by zero already
   gets. A value-level failure would ride in the type as `bits or failure` and force an unwrap
   after every masking expression, which is exactly why divide-by-zero is not one. */
static void cufet_bits_overflow(CufetBits a, CufetBits b, const char* op, const char* why, int line) {
    char x[80], y[80];
    cufet_format_bits(x, sizeof(x), a);
    cufet_format_bits(y, sizeof(y), b);
    cufet_raise(cufet_msgf("%s %s %s %s (line %d).", x, op, y, why, line));
}
static CufetBits cufet_bits_add(CufetBits a, CufetBits b, int line) {
    if (a.value > ~0ULL - b.value) cufet_bits_overflow(a, b, "+", "does not fit in 64 bits", line);
    return cufet_bits_combine(a, a.value + b.value);
}
static CufetBits cufet_bits_sub(CufetBits a, CufetBits b, int line) {
    if (b.value > a.value) cufet_bits_overflow(a, b, "-", "would be negative, and bits are unsigned", line);
    return cufet_bits_combine(a, a.value - b.value);
}
static CufetBits cufet_bits_mul(CufetBits a, CufetBits b, int line) {
    if (a.value != 0 && b.value > ~0ULL / a.value) cufet_bits_overflow(a, b, "*", "does not fit in 64 bits", line);
    return cufet_bits_combine(a, a.value * b.value);
}
static CufetBits cufet_bits_div(CufetBits a, CufetBits b, int line) {
    if (b.value == 0) cufet_raise(cufet_msgf("Division by zero on line %d.", line));
    return cufet_bits_combine(a, a.value / b.value);
}
static CufetBits cufet_bits_mod(CufetBits a, CufetBits b, int line) {
    if (b.value == 0) cufet_raise(cufet_msgf("Modulo by zero on line %d.", line));
    return cufet_bits_combine(a, a.value % b.value);
}

/* Shifting. The amount arrives as a CufetDec because it counts POSITIONS — a quantity, like the
   3 in "item 3 of s" — so it has to be whole and non-negative.

   Note the >= 64 guards: shifting by at least the operand's width is UNDEFINED BEHAVIOUR in C,
   so the answer has to be written out rather than left to the hardware. */
/* <number> converted to hex|binary|octal. Width is the smallest that holds the value, rounded up
   to whole digits of the target base, so 255 becomes 0xFF and 16 becomes 0x10. Raises rather than
   yielding a voidable, matching arithmetic overflow. */
static CufetBits cufet_bits_from_number(CufetDec d, char base_, int line);
static CufetDec cufet_bits_to_number(CufetBits b) {
    CufetDec d;
    d.coef  = (unsigned __int128)b.value;
    d.scale = 0;
    d.sign  = 0;
    return d;
}

/* Whole iff dividing the coefficient down by its scale leaves no remainder. Done on the struct
   so it stays exact and cannot overflow, unlike round-tripping through an int. */
static int cufet_bits_whole(CufetDec d) {
    unsigned __int128 c = d.coef;
    for (int s = d.scale; s > 0; s--) { if (c % 10 != 0) return 0; c /= 10; }
    return 1;
}
static CufetBits cufet_bits_shift(CufetBits a, CufetDec amount, int left, int line) {
    char buf[64];
    if (!cufet_bits_whole(amount)) {
        cufet_format_number(buf, sizeof(buf), amount);
        cufet_raise(cufet_msgf("the shift amount must be a whole number of positions, not %s (line %d).", buf, line));
    }
    if (cufet_cmp(amount, cufet_dec_from_ll(0)) < 0)
        cufet_raise(cufet_msgf("the shift amount cannot be negative — shift the other way instead (line %d).", line));

    /* Clamp before converting: anything past the ceiling behaves identically, and cufet_to_int
       would overflow on a genuinely huge amount. */
    int by = cufet_cmp(amount, cufet_dec_from_ll(64)) > 0 ? 65 : cufet_to_int(amount);

    if (!left) {
        CufetBits out;
        out.value = by >= 64 ? 0ULL : (a.value >> by);
        out.base  = a.base;
        out.width = a.width;
        return out;
    }

    if ((by >= 64 && a.value != 0) || (by < 64 && a.value > (~0ULL >> by))) {
        char x[80];
        cufet_format_bits(x, sizeof(x), a);
        cufet_format_number(buf, sizeof(buf), amount);
        cufet_raise(cufet_msgf("%s shifted left by %s does not fit in 64 bits (line %d).", x, buf, line));
    }
    return cufet_bits_combine(a, by >= 64 ? 0ULL : (a.value << by));
}

/* `<bits> at <n> bits` - the same value carried at a STATED width.

   A width is otherwise only ever raised to fit the value, so leading zeros no operand ever held
   could not be produced: `0b0 shifted left by 2` is `0b0`, not `0b000`. This is what lets a
   program choose one - and `0b0 at 3 bits` is how "three zero bits" is spelled.

   Widening is free. Narrowing is refused when it would drop a set bit, because a packer that
   silently loses its high bits writes a file that decodes to garbage. `cufet_bits_minwidth` is
   the same count Interpreter.EvaluateBitsAtWidth computes, so both backends refuse identically. */
static CufetBits cufet_bits_at_width(CufetBits x, CufetDec w, int line) {
    char buf[64];
    if (!cufet_bits_whole(w) || cufet_cmp(w, cufet_dec_from_ll(0)) < 0) {
        cufet_format_number(buf, sizeof(buf), w);
        cufet_raise(cufet_msgf("a stated width must be a whole, non-negative number of bits, not %s (line %d).", buf, line));
    }
    int stated = cufet_cmp(w, cufet_dec_from_ll(64)) > 0 ? 65 : cufet_to_int(w);
    int needed = cufet_bits_minwidth(x.value);
    if (stated < needed)
        cufet_raise(cufet_msgf("%d bits cannot hold this value - it needs %d (line %d). "
                               "Widening is always fine; narrowing is refused when it would drop a set bit. "
                               "Mask with 'and' if dropping them is what you meant.", stated, needed, line));
    x.width = stated;
    return x;
}

static CufetBits cufet_bits_from_number(CufetDec d, char base_, int line) {
    char buf[64];
    if (!cufet_bits_whole(d)) {
        cufet_format_number(buf, sizeof(buf), d);
        cufet_raise(cufet_msgf("only a whole number can become a bit pattern, and %s is not one (line %d).", buf, line));
    }
    if (d.sign) {
        cufet_format_number(buf, sizeof(buf), d);
        cufet_raise(cufet_msgf("%s is negative, and bit patterns are unsigned (line %d).", buf, line));
    }
    /* Compare against 2^64-1 before narrowing, since the coefficient is 128 bits wide. */
    unsigned __int128 whole = d.coef;
    for (int s = d.scale; s > 0; s--) whole /= 10;
    if (whole > (unsigned __int128)~0ULL) {
        cufet_format_number(buf, sizeof(buf), d);
        cufet_raise(cufet_msgf("%s does not fit in 64 bits (line %d).", buf, line));
    }

    unsigned long long value = (unsigned long long)whole;
    int per = base_ == 'x' ? 4 : (base_ == 'o' ? 3 : 1);
    int min = cufet_bits_minwidth(value);
    if (min < 1) min = 1;
    CufetBits out;
    out.value = value;
    out.base  = base_;
    out.width = (min + per - 1) / per * per;   /* whole digits, no partial leading one */
    return out;
}

/* A caught failure (in an In-case-of-failure handler) — T-agnostic, so one handler works
   regardless of which fallible call's T produced the failure. category NULL = absent. */
typedef struct { const char* message; const char* category; } CufetFailure;

""";

    // Text runtime. Text is `const char*` and immutable — every operation allocates a fresh
    // result in the current arena (freed at Done.); literals stay static. Trim/parse are
    // ASCII/invariant (matching the interpreter for ASCII input); CASING IS NOT HERE — it needs a
    // Unicode table, so it lives in the gated CaseRuntime below and is emitted only when used.
    private const string TextRuntime =
"""
static const char* cufet_str_concat(const char* a, const char* b) {
    size_t la = strlen(a), lb = strlen(b);
    char* r = (char*)cufet_arena_alloc(la + lb + 1);
    memcpy(r, a, la); memcpy(r + la, b, lb + 1);
    return r;
}
static const char* cufet_str_substr(const char* s, int from0, int len) {
    if (len < 0) len = 0;
    char* r = (char*)cufet_arena_alloc((size_t)len + 1);
    memcpy(r, s + from0, (size_t)len); r[len] = '\0';
    return r;
}
/* ── Character positions ──────────────────────────────────────────────────────────────────
   A Cufet character position is a UNICODE CODE POINT, on both backends. Text is stored here as
   UTF-8, so a position is NOT a byte: "héllo" is five characters in six bytes. Counting bytes
   made the compiled program disagree with the interpreted one on every non-ASCII string, which
   the no-divergence rule forbids outright.

   UTF-8 makes the arithmetic cheap. Exactly one byte of each character has a top bit pattern
   other than 10xxxxxx, so counting characters is counting the bytes that are not continuations,
   and no decoding table is needed. See TextPositions in the interpreter for the UTF-16 half of
   the same agreement, and for why the unit is code points and not grapheme clusters. */
static int cufet_u8_len(const char* s) {
    int n = 0;
    for (const unsigned char* p = (const unsigned char*)s; *p; p++)
        if ((*p & 0xC0) != 0x80) n++;
    return n;
}
/* Byte offset at which character number `index` begins, counting from zero. An index at or past
   the end returns the byte length, so a caller can use it as an exclusive bound unguarded. */
static int cufet_u8_offset(const char* s, int index) {
    const unsigned char* p = (const unsigned char*)s;
    int i = 0, n = 0;
    while (p[i] && n < index) {
        i++;
        while ((p[i] & 0xC0) == 0x80) i++;   /* skip this character's continuation bytes */
        n++;
    }
    return i;
}
/* The character index containing byte offset `off` — the inverse, for turning a byte-wise
   search result back into a position someone can hand to `the characters from`. */
static int cufet_u8_index(const char* s, int off) {
    const unsigned char* p = (const unsigned char*)s;
    int n = 0;
    for (int i = 0; i < off && p[i]; i++)
        if ((p[i] & 0xC0) != 0x80) n++;
    return n;
}
static const char* cufet_str_range(const char* s, int from1, int to1, int line) {
    if (from1 <= 0) cufet_raise(cufet_msgf("a character position must be 1 or greater — positions start at 1 (line %d).", line));
    int len = cufet_u8_len(s);
    if (to1 < 0 || to1 > len) to1 = len;      /* to1 < 0 sentinel = to end; clamp high */
    int length = to1 - from1 + 1;              /* 1-based inclusive */
    if (length <= 0) return "";
    int from_b = cufet_u8_offset(s, from1 - 1);
    int to_b   = cufet_u8_offset(s, to1);
    return cufet_str_substr(s, from_b, to_b - from_b);
}
static const char* cufet_str_edge(const char* s, int count, int from_start) {
    int len = cufet_u8_len(s);
    int c = count < 0 ? 0 : (count > len ? len : count);
    if (from_start) return cufet_str_substr(s, 0, cufet_u8_offset(s, c));
    int from_b = cufet_u8_offset(s, len - c);
    return cufet_str_substr(s, from_b, (int)strlen(s) - from_b);
}
static const char* cufet_str_trim(const char* s) {
    const char* start = s;
    while (*start && isspace((unsigned char)*start)) start++;
    const char* end = s + strlen(s);
    while (end > start && isspace((unsigned char)end[-1])) end--;
    size_t n = (size_t)(end - start);
    char* r = (char*)cufet_arena_alloc(n + 1);
    memcpy(r, start, n); r[n] = '\0'; return r;
}
static int cufet_str_find(const char* text, const char* sub) {
    /* The SEARCH is byte-wise and that is correct — UTF-8 is self-synchronising, so one
       character's bytes can never occur inside another and a byte match is a character match.
       Only the POSITION it reports has to be converted, from bytes to characters. */
    const char* p = strstr(text, sub);
    return p ? cufet_u8_index(text, (int)(p - text)) + 1 : 0;   /* 1-based; 0 = not found */
}
/* Splits s on each non-overlapping occurrence of delim, keeping empty parts (C# string.Split
   with StringSplitOptions.None): N hits -> N+1 arena-allocated substrings, written to *out.
   Delimiter-not-found -> one part (the whole string); "" -> one empty part. */
static int cufet_str_split(const char* s, const char* delim, const char*** out, int line) {
    size_t dl = strlen(delim);
    if (dl == 0) cufet_raise(cufet_msgf("'split by' needs a non-empty delimiter (line %d).", line));
    int count = 1;
    for (const char* p = s; (p = strstr(p, delim)) != NULL; p += dl) count++;
    const char** arr = (const char**)cufet_arena_alloc((size_t)count * sizeof(const char*));
    int idx = 0; const char* start = s; const char* p;
    while ((p = strstr(start, delim)) != NULL) {
        size_t len = (size_t)(p - start);
        char* part = (char*)cufet_arena_alloc(len + 1);
        memcpy(part, start, len); part[len] = '\0';
        arr[idx++] = part;
        start = p + dl;
    }
    { size_t len = strlen(start); char* part = (char*)cufet_arena_alloc(len + 1);
      memcpy(part, start, len); part[len] = '\0'; arr[idx++] = part; }
    *out = arr;
    return count;
}
static const char* cufet_str_replace(const char* s, const char* olds, const char* news, int line) {
    size_t lo = strlen(olds);
    if (lo == 0) cufet_raise(cufet_msgf("'replace' needs a non-empty target (line %d).", line));
    size_t ln = strlen(news), ls = strlen(s), count = 0;
    const char* p = s;
    while ((p = strstr(p, olds))) { count++; p += lo; }
    char* r = (char*)cufet_arena_alloc(ls + count * ln + 1);   /* upper bound */
    char* w = r; p = s; const char* q;
    while ((q = strstr(p, olds))) {
        memcpy(w, p, (size_t)(q - p)); w += (q - p);
        memcpy(w, news, ln); w += ln;
        p = q + lo;
    }
    strcpy(w, p);
    return r;
}
static const char* cufet_text_from_dec(CufetDec d) {
    char buf[64]; cufet_format_number(buf, sizeof(buf), d);
    size_t n = strlen(buf); char* r = (char*)cufet_arena_alloc(n + 1);
    memcpy(r, buf, n + 1); return r;
}
/* text -> number: trim, then accept -?\d+(\.\d+)? (mirrors the lexer + decimal.TryParse).
   Returns 1 and writes *out on success; 0 (unparseable) otherwise. */
static int cufet_parse_number(const char* s, CufetDec* out) {
    while (*s && isspace((unsigned char)*s)) s++;
    const char* end = s + strlen(s);
    while (end > s && isspace((unsigned char)end[-1])) end--;
    if (end == s) return 0;
    const char* p = s; int sign = 0;
    if (*p == '-') { sign = 1; p++; }
    if (p == end || *p < '0' || *p > '9') return 0;
    unsigned __int128 coef = 0; int scale = 0;
    while (p < end && *p >= '0' && *p <= '9') { coef = coef * 10 + (unsigned)(*p - '0'); p++; }
    if (p < end && *p == '.') {
        p++;
        if (p == end || *p < '0' || *p > '9') return 0;
        while (p < end && *p >= '0' && *p <= '9') { coef = coef * 10 + (unsigned)(*p - '0'); scale++; p++; }
    }
    if (p != end) return 0;
    if (scale > 28) return 0;
    unsigned __int128 max96 = (((unsigned __int128)0xFFFFFFFFu) << 64) | 0xFFFFFFFFFFFFFFFFull;
    if (coef > max96) return 0;                /* > decimal.MaxValue -> unparseable */
    out->coef = coef; out->scale = scale; out->sign = (coef == 0) ? 0 : sign;
    return 1;
}

""";

    // Unicode casing runtime — emitted only when a program actually cases text (_usesCase), because
    // the table is ~11 KB of source and the runtime is pasted into every generated file.
    //
    // ★ The numbers come from CaseTableData, the SAME table the interpreter reads. That is the whole
    // point: casing used to be implemented twice — ToUpperInvariant here, C's per-byte toupper()
    // there — so `"héllo" in uppercase` was HÉLLO interpreted and HéLLO compiled. Emitting the
    // interpreter's own table means the two cannot disagree, rather than being tested for agreeing.
    private static readonly string CaseRuntime = BuildCaseRuntime();

    private static string BuildCaseRuntime()
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
/* ── Unicode case mapping ────────────────────────────────────────────────────────────────────────
   Generated from src/Interpreter/CaseTableData.cs, which both backends read — see CaseTable.cs.

   Each run is start/count/stride/delta: the `count` code points starting at `start`, spaced
   `stride` apart, each map to themselves plus `delta`. Runs are sorted by start and their spans
   never overlap, so the last run beginning at or before a code point is the only candidate and a
   plain binary search suffices. Stride 2 pays for itself on Latin Extended-A, where upper and
   lower alternate (Ā ā Ă ă …) and each parity forms a run.

   The mapping is 1:1 in CODE POINTS for every one of them — this is simple case mapping, so `ß`
   stays `ß` and no character ever becomes two. In BYTES it can still grow, by at most one per
   character (the worst case is U+023F, two bytes, uppercasing to U+2C7E, three), which is why the
   output buffer is sized at twice the input. */
""");

        EmitCaseRuns(sb, "cufet_case_upper", CaseTableData.UpperRuns);
        EmitCaseRuns(sb, "cufet_case_lower", CaseTableData.LowerRuns);

        sb.Append("""
static int cufet_case_map(const int* runs, int nruns, int cp) {
    int lo = 0, hi = nruns - 1, found = -1;
    while (lo <= hi) {
        int mid = (lo + hi) / 2;
        if (runs[mid * 4] <= cp) { found = mid; lo = mid + 1; } else hi = mid - 1;
    }
    if (found < 0) return cp;
    int start = runs[found * 4], count = runs[found * 4 + 1];
    int stride = runs[found * 4 + 2], delta = runs[found * 4 + 3];
    int offset = cp - start;
    if (offset > (count - 1) * stride || offset % stride != 0) return cp;
    return cp + delta;
}
/* Map every character of a UTF-8 string. Bytes that are not a well-formed sequence are copied
   through untouched rather than replaced or rejected: casing is not the place to start refusing
   text that every other text operation in the language accepts. */
static const char* cufet_str_case(const char* s, const int* runs, int nruns) {
    const unsigned char* p = (const unsigned char*)s;
    size_t n = strlen(s);
    char* r = (char*)cufet_arena_alloc(n * 2 + 1);   /* +1 byte per character, worst case */
    size_t i = 0, o = 0;
    while (i < n) {
        unsigned char b = p[i];
        int cp, width;
        if (b < 0x80)                      { cp = b;             width = 1; }
        else if ((b & 0xE0) == 0xC0 && i + 1 < n && (p[i+1] & 0xC0) == 0x80)
                                           { cp = ((b & 0x1F) << 6) | (p[i+1] & 0x3F); width = 2; }
        else if ((b & 0xF0) == 0xE0 && i + 2 < n && (p[i+1] & 0xC0) == 0x80 && (p[i+2] & 0xC0) == 0x80)
                                           { cp = ((b & 0x0F) << 12) | ((p[i+1] & 0x3F) << 6) | (p[i+2] & 0x3F); width = 3; }
        else if ((b & 0xF8) == 0xF0 && i + 3 < n && (p[i+1] & 0xC0) == 0x80 && (p[i+2] & 0xC0) == 0x80 && (p[i+3] & 0xC0) == 0x80)
                                           { cp = ((b & 0x07) << 18) | ((p[i+1] & 0x3F) << 12) | ((p[i+2] & 0x3F) << 6) | (p[i+3] & 0x3F); width = 4; }
        else                               { r[o++] = (char)b; i++; continue; }   /* malformed: pass through */

        cp = cufet_case_map(runs, nruns, cp);
        i += (size_t)width;
        if (cp < 0x80) { r[o++] = (char)cp; }
        else if (cp < 0x800) {
            r[o++] = (char)(0xC0 | (cp >> 6));
            r[o++] = (char)(0x80 | (cp & 0x3F));
        } else if (cp < 0x10000) {
            r[o++] = (char)(0xE0 | (cp >> 12));
            r[o++] = (char)(0x80 | ((cp >> 6) & 0x3F));
            r[o++] = (char)(0x80 | (cp & 0x3F));
        } else {
            r[o++] = (char)(0xF0 | (cp >> 18));
            r[o++] = (char)(0x80 | ((cp >> 12) & 0x3F));
            r[o++] = (char)(0x80 | ((cp >> 6) & 0x3F));
            r[o++] = (char)(0x80 | (cp & 0x3F));
        }
    }
    r[o] = '\0';
    return r;
}
static const char* cufet_str_upper(const char* s) {
    return cufet_str_case(s, cufet_case_upper, (int)(sizeof(cufet_case_upper) / sizeof(int) / 4));
}
static const char* cufet_str_lower(const char* s) {
    return cufet_str_case(s, cufet_case_lower, (int)(sizeof(cufet_case_lower) / sizeof(int) / 4));
}

""");
        return sb.ToString();
    }

    private static void EmitCaseRuns(StringBuilder sb, string name, int[] runs)
    {
        sb.AppendLine($"static const int {name}[] = {{   /* {runs.Length / 4} runs */");
        for (int i = 0; i < runs.Length; i += 16)
        {
            var line = new StringBuilder("   ");
            for (int j = i; j < Math.Min(i + 16, runs.Length); j += 4)
                line.Append($" {runs[j],7},{runs[j + 1],5},{runs[j + 2],2},{runs[j + 3],7},");
            sb.AppendLine(line.ToString());
        }
        sb.AppendLine("};");
    }

    // File I/O runtime (sub-slice A): whole-file read/write + path checks. Results are arena-
    // allocated (text buffers, line arrays) and freed at Done. OS errors (errno) become Cufet
    // failure values with a deterministic, path-templated message matching the interpreter.
    private const string FileRuntime =
"""
/* Arena-format a one-%s-arg message (deterministic; no host-specific strerror text). */
static const char* cufet_arena_msg(const char* fmt, const char* arg) {
    int n = snprintf(NULL, 0, fmt, arg);
    if (n < 0) n = 0;
    char* buf = (char*)cufet_arena_alloc((size_t)n + 1);
    snprintf(buf, (size_t)n + 1, fmt, arg);
    return buf;
}
/* errno -> Cufet failure (category + templated message), matching the interpreter's FileIoFailure:
   ENOENT -> not-found; EACCES/EPERM -> permission-denied; else -> deterministic disk-error. */
static CufetFailure cufet_file_failure(const char* path, int e) {
    CufetFailure f;
    if (e == ENOENT) {
        f.category = "not-found";
        f.message  = cufet_arena_msg("the file '%s' was not found", path);
    } else if (e == EACCES || e == EPERM) {
        f.category = "permission-denied";
        f.message  = cufet_arena_msg("permission denied accessing '%s'", path);
    } else {
        f.category = "disk-error";
        f.message  = cufet_arena_msg("accessing the file '%s' failed", path);
    }
    return f;
}
/* Reads the whole file into an arena buffer (binary — no newline translation, matching .NET
   ReadAllText's byte fidelity). NUL-terminates and reports the true byte length via *len. */
static int cufet_file_slurp(const char* path, char** buf, long* len, CufetFailure* err) {
    FILE* f = fopen(path, "rb");
    if (!f) { *err = cufet_file_failure(path, errno); return 0; }
    if (fseek(f, 0, SEEK_END) != 0) { *err = cufet_file_failure(path, errno); fclose(f); return 0; }
    long sz = ftell(f);
    if (sz < 0) { *err = cufet_file_failure(path, errno); fclose(f); return 0; }
    rewind(f);
    char* b = (char*)cufet_arena_alloc((size_t)sz + 1);
    size_t rd = fread(b, 1, (size_t)sz, f);
    if (ferror(f)) { *err = cufet_file_failure(path, errno); fclose(f); return 0; }
    b[rd] = '\0';
    fclose(f);
    *buf = b; *len = (long)rd;
    return 1;
}
static int cufet_file_read_all(const char* path, const char** out, CufetFailure* err) {
    char* b; long len;
    if (!cufet_file_slurp(path, &b, &len, err)) return 0;
    *out = b;
    return 1;
}
/* Splits into lines exactly like StreamReader.ReadLine / File.ReadAllLines: a line ends at
   \r, \n, or \r\n; the terminator is dropped; a trailing terminator does NOT yield an empty
   final line; empty input -> zero lines. (Deliberately NOT split-by-"\n", which keeps a trailing
   empty.) Emits arena-allocated substrings into an arena array; *count gets the line count. */
static int cufet_file_read_lines(const char* path, const char*** out, int* count, CufetFailure* err) {
    char* b; long len;
    if (!cufet_file_slurp(path, &b, &len, err)) return 0;
    int n = 0;
    for (long i = 0; i < len; ) {
        while (i < len && b[i] != '\n' && b[i] != '\r') i++;
        n++;
        if (i < len) { if (b[i] == '\r' && i + 1 < len && b[i+1] == '\n') i += 2; else i += 1; }
    }
    const char** arr = (const char**)cufet_arena_alloc((size_t)(n > 0 ? n : 1) * sizeof(const char*));
    int idx = 0;
    for (long i = 0; i < len; ) {
        long start = i;
        while (i < len && b[i] != '\n' && b[i] != '\r') i++;
        size_t ll = (size_t)(i - start);
        char* line = (char*)cufet_arena_alloc(ll + 1);
        memcpy(line, b + start, ll); line[ll] = '\0';
        arr[idx++] = line;
        if (i < len) { if (b[i] == '\r' && i + 1 < len && b[i+1] == '\n') i += 2; else i += 1; }
    }
    *out = arr; *count = idx;
    return 1;
}
static int cufet_file_write(const char* path, const char* text, int append, CufetFailure* err) {
    FILE* f = fopen(path, append ? "ab" : "wb");
    if (!f) { *err = cufet_file_failure(path, errno); return 0; }
    size_t len = strlen(text);
    size_t wr = fwrite(text, 1, len, f);
    if (wr != len || fclose(f) != 0) { *err = cufet_file_failure(path, errno); return 0; }
    return 1;
}
/* Path predicates via stat, matching File.Exists / Directory.Exists (exists = either kind). */
static int cufet_path_exists(const char* path)  { struct stat st; return stat(path, &st) == 0; }
static int cufet_path_is_dir(const char* path)  { struct stat st; return stat(path, &st) == 0 && S_ISDIR(st.st_mode); }
static int cufet_path_is_file(const char* path) { struct stat st; return stat(path, &st) == 0 && S_ISREG(st.st_mode); }

/* ── Streams (slice 9B): a stream is a FILE* (an opened file, or stdin). Read results are
   arena-allocated; the FILE* itself is closed by the With-block cleanup (not the arena). ── */
/* Reads one line, matching StreamReader.ReadLine: content up to \r, \n, or \r\n (terminator
   dropped and \r\n consumed together); NULL at end-of-stream with no content. */
static const char* cufet_stream_read_line(FILE* f) {
    int c = fgetc(f);
    if (c == EOF) return NULL;
    size_t cap = 16, len = 0;
    char* buf = (char*)malloc(cap);
    while (c != EOF && c != '\n' && c != '\r') {
        if (len + 1 >= cap) { cap *= 2; buf = (char*)realloc(buf, cap); }
        buf[len++] = (char)c;
        c = fgetc(f);
    }
    if (c == '\r') { int n = fgetc(f); if (n != '\n' && n != EOF) ungetc(n, f); }
    char* r = (char*)cufet_arena_alloc(len + 1);
    memcpy(r, buf, len); r[len] = '\0';
    free(buf);
    return r;
}
/* Reads the rest of the stream to end (ReadToEnd — "" at end-of-stream, never NULL). */
static const char* cufet_stream_read_all(FILE* f) {
    size_t cap = 256, len = 0;
    char* buf = (char*)malloc(cap);
    int c;
    while ((c = fgetc(f)) != EOF) {
        if (len + 1 >= cap) { cap *= 2; buf = (char*)realloc(buf, cap); }
        buf[len++] = (char)c;
    }
    char* r = (char*)cufet_arena_alloc(len + 1);
    memcpy(r, buf, len); r[len] = '\0';
    free(buf);
    return r;
}

/* ── Current directory ──────────────────────────────────────────────────────
   `the current directory` → voidable text; void only when the process has no working
   directory to report, which in practice means it was removed underneath it.
   `The current directory becomes <p>.` → fallible statement.

   ★ The stat() checks run BEFORE chdir(), and that ordering is load-bearing for matching the
   interpreter. .NET collapses "no such directory" and "that is a file" into a single
   IOException, so the interpreter must test existence itself; relying on errno here instead
   would diverge on Windows, where _chdir onto a file reports ENOENT rather than ENOTDIR.
   Checking the same way on both sides is what makes the failure CATEGORY agree everywhere. */
#include <unistd.h>
static const char* cufet_getcwd(void) {
    /* Grown rather than fixed at PATH_MAX: a truncated answer would be a silent divergence from
       the interpreter, which has no length ceiling. Superseded buffers stay in the arena and die
       with it, and the loop runs a handful of times at most. */
    size_t cap = 512;
    for (;;) {
        char* buf = (char*)cufet_arena_alloc(cap);
        if (getcwd(buf, cap)) return buf;
        if (errno != ERANGE || cap > (1u << 20)) return NULL;
        cap *= 2;
    }
}
static int cufet_chdir(const char* path, CufetFailure* err) {
    struct stat st;
    if (stat(path, &st) != 0) {
        err->category = "not-found";
        err->message  = cufet_arena_msg("the directory '%s' was not found", path);
        return 0;
    }
    if (!S_ISDIR(st.st_mode)) {
        err->category = "not-a-directory";
        err->message  = cufet_arena_msg("'%s' is not a directory", path);
        return 0;
    }
    if (chdir(path) != 0) {
        if (errno == EACCES || errno == EPERM) {
            err->category = "permission-denied";
            err->message  = cufet_arena_msg("permission denied entering directory '%s'", path);
        } else {
            err->category = "disk-error";
            err->message  = cufet_arena_msg("changing to the directory '%s' failed", path);
        }
        return 0;
    }
    return 1;
}

/* ── Directory contents (cleanup slice) ─────────────────────────────────────
   `the contents of the directory <p>` → SORTED (ordinal, strcmp) full paths "<p><sep><name>",
   skipping "." / "..". Both backends sort: the raw OS order is filesystem-dependent, so sorting
   defines the undefined (the FormatRecord normalization move). The separator is the PLATFORM's
   (matching .NET on the same platform); a trailing separator on the input is not doubled. */
#include <dirent.h>
static CufetFailure cufet_dir_failure(const char* path, int e) {
    CufetFailure f;
    if (e == ENOENT) {
        f.category = "not-found";
        f.message  = cufet_arena_msg("the directory '%s' was not found", path);
    } else if (e == EACCES || e == EPERM) {
        f.category = "permission-denied";
        f.message  = cufet_arena_msg("permission denied reading directory '%s'", path);
    } else {
        f.category = "disk-error";
        f.message  = cufet_arena_msg("reading the directory '%s' failed", path);
    }
    return f;
}
static int cufet_dir_cmp(const void* a, const void* b) { return strcmp(*(const char* const*)a, *(const char* const*)b); }
static int cufet_dir_contents(const char* path, const char*** out_items, int* out_n, CufetFailure* err) {
    DIR* d = opendir(path);
    if (!d) { *err = cufet_dir_failure(path, errno); return 0; }
#ifdef _WIN32
    const char sep = '\\';
#else
    const char sep = '/';
#endif
    size_t plen = strlen(path);
    int hasSep = plen > 0 && (path[plen - 1] == '/' || path[plen - 1] == '\\');
    int n = 0, cap = 16;
    const char** items = (const char**)cufet_arena_alloc((size_t)cap * sizeof(char*));
    struct dirent* de;
    while ((de = readdir(d)) != NULL) {
        if (strcmp(de->d_name, ".") == 0 || strcmp(de->d_name, "..") == 0) continue;
        if (n == cap) {
            cap *= 2;
            const char** ni = (const char**)cufet_arena_alloc((size_t)cap * sizeof(char*));
            memcpy(ni, items, (size_t)n * sizeof(char*));
            items = ni;
        }
        size_t nl = strlen(de->d_name);
        char* full = (char*)cufet_arena_alloc(plen + (hasSep ? 0 : 1) + nl + 1);
        memcpy(full, path, plen);
        if (!hasSep) full[plen] = sep;
        memcpy(full + plen + (hasSep ? 0 : 1), de->d_name, nl + 1);
        items[n] = full; n++;
    }
    closedir(d);
    qsort(items, (size_t)n, sizeof(char*), cufet_dir_cmp);
    *out_items = items; *out_n = n;
    return 1;
}

""";

    // Subprocess runtime (slice 9C): POSIX fork/exec/pipe/waitpid — matches the interpreter's
    // no-shell direct exec (ProcessStartInfo.ArgumentList) with separate stdout/stderr + exit code.
    // Emitted ONLY when a program uses `run`/pipe (so non-run programs compile anywhere), and
    // #if-guarded to POSIX (a `run` program is Linux-targeted, like the OS-homework shell; on
    // Windows/mingw — which lacks fork — it simply won't link, which is correct).
    private const string ProcessRuntime =
"""
#if defined(__unix__) || defined(__APPLE__)
#include <unistd.h>
#include <sys/wait.h>
#include <poll.h>
#include <fcntl.h>

/* errno → Cufet launch failure, matching the interpreter's LaunchFailure. */
static CufetFailure cufet_launch_failure(const char* program, int e) {
    CufetFailure f;
    if (e == ENOENT) {
        f.category = "not-found";
        f.message  = cufet_arena_msg("the program '%s' was not found", program);
    } else if (e == EACCES || e == EPERM) {
        f.category = "permission-denied";
        f.message  = cufet_arena_msg("permission denied executing '%s'", program);
    } else {
        f.category = "io-error";
        f.message  = cufet_arena_msg("running the program '%s' failed", program);
    }
    return f;
}

/* Runs `program` with `argv` (NULL-terminated, no shell), optionally feeding `stdin_data`;
   captures stdout + stderr (arena strings) and the exit code. Returns 1 on a successful LAUNCH
   (the process ran — a nonzero exit is still success), 0 on a launch failure (*err set). The
   child is always reaped (waitpid) and all fds closed before returning, so no zombies / leaked
   fds outlive the call — process cleanup is atomic within the primitive, not a later concern. */
static int cufet_run_capture(const char* program, char* const argv[], const char* stdin_data,
                             const char** out_stdout, const char** out_stderr, int* out_exit,
                             CufetFailure* err) {
    int outp[2], errp[2], xp[2];
    if (pipe(outp) < 0 || pipe(errp) < 0 || pipe(xp) < 0) { *err = cufet_launch_failure(program, EIO); return 0; }
    fcntl(xp[1], F_SETFD, FD_CLOEXEC);   /* exec closes it → parent reads EOF = exec ok */
    FILE* infile = NULL; int infd = -1;
    if (stdin_data) { infile = tmpfile(); if (infile) { fputs(stdin_data, infile); fflush(infile); rewind(infile); infd = fileno(infile); } }
    pid_t pid = fork();
    if (pid < 0) {
        if (infile) fclose(infile);
        close(outp[0]); close(outp[1]); close(errp[0]); close(errp[1]); close(xp[0]); close(xp[1]);
        *err = cufet_launch_failure(program, EIO); return 0;
    }
    if (pid == 0) {
        if (infd >= 0) dup2(infd, 0);
        dup2(outp[1], 1); dup2(errp[1], 2);
        close(outp[0]); close(outp[1]); close(errp[0]); close(errp[1]); close(xp[0]);
        execvp(program, argv);
        int e = errno; ssize_t w = write(xp[1], &e, sizeof(e)); (void)w; _exit(127);
    }
    close(outp[1]); close(errp[1]); close(xp[1]);
    if (infile) fclose(infile);
    int child_errno = 0;
    ssize_t xn = read(xp[0], &child_errno, sizeof(child_errno));
    close(xp[0]);
    if (xn > 0) {   /* exec failed in the child → launch failure */
        int st; waitpid(pid, &st, 0);
        close(outp[0]); close(errp[0]);
        *err = cufet_launch_failure(program, child_errno);
        return 0;
    }
    /* Read stdout + stderr concurrently (poll) so neither pipe filling can deadlock the other. */
    char* ob = (char*)malloc(256); size_t oc = 256, ol = 0;
    char* eb = (char*)malloc(256); size_t ec = 256, el = 0;
    struct pollfd pfd[2]; pfd[0].fd = outp[0]; pfd[0].events = POLLIN; pfd[1].fd = errp[0]; pfd[1].events = POLLIN;
    int openfds = 2;
    while (openfds > 0) {
        if (poll(pfd, 2, -1) < 0) { if (errno == EINTR) continue; break; }
        for (int i = 0; i < 2; i++) {
            if (pfd[i].fd < 0) continue;
            if (pfd[i].revents & (POLLIN | POLLHUP | POLLERR)) {
                char tmp[4096]; ssize_t r = read(pfd[i].fd, tmp, sizeof(tmp));
                if (r > 0) {
                    char** b = (i == 0) ? &ob : &eb; size_t* cap = (i == 0) ? &oc : &ec; size_t* len = (i == 0) ? &ol : &el;
                    while (*len + (size_t)r + 1 > *cap) { *cap *= 2; *b = (char*)realloc(*b, *cap); }
                    memcpy(*b + *len, tmp, (size_t)r); *len += (size_t)r;
                } else { close(pfd[i].fd); pfd[i].fd = -1; openfds--; }
            }
        }
    }
    int st; waitpid(pid, &st, 0);
    *out_exit = WIFEXITED(st) ? WEXITSTATUS(st) : (WIFSIGNALED(st) ? 128 + WTERMSIG(st) : -1);
    char* os = (char*)cufet_arena_alloc(ol + 1); memcpy(os, ob, ol); os[ol] = '\0';
    char* es = (char*)cufet_arena_alloc(el + 1); memcpy(es, eb, el); es[el] = '\0';
    free(ob); free(eb);
    *out_stdout = os; *out_stderr = es;
    return 1;
}
#endif

""";

    // The decimal↔double bridge for `math`'s three remaining native members (sqrt/log/power).
    //

    // Matrix runtime (Arc 1D — the collections book's introduced type). A matrix is an ARENA
    // REFERENCE type like series/maps (shared on assign — matches the interpreter, where MatrixValue
    // is never deep-copied; matrices are immutable after construction, so share-vs-copy is
    // unobservable anyway). All arithmetic is EXACT CufetDec (cufet_add/cufet_mul folds — no double
    // bridge). add/sub/mul return NULL on dimension mismatch: the EMIT SITE wraps that into the
    // fallible `matrix or failure` (the typechecker requires handling — dimension mismatch is a
    // Cufet FAILURE with category "dimension-mismatch", not a crash; messages match the interpreter).
    // Element order + the multiply's k-ascending accumulation from 0 replicate Interpreter.Matrix.cs
    // exactly, so results are bit-identical.
    private const string MatrixRuntime =
"""
typedef struct { int rows; int cols; CufetDec* data; } CufetMatrix;
static CufetMatrix* cufet_mat_new(int rows, int cols) {
    CufetMatrix* m = (CufetMatrix*)cufet_arena_alloc(sizeof(CufetMatrix));
    m->rows = rows; m->cols = cols;
    m->data = (CufetDec*)cufet_arena_alloc(sizeof(CufetDec) * (size_t)rows * (size_t)cols);
    memset(m->data, 0, sizeof(CufetDec) * (size_t)rows * (size_t)cols);   /* all-zero bytes == decimal 0 */
    return m;
}
/* 1-based access, bounds-checked — the messages mirror the interpreter's RuntimeException text. */
static CufetDec cufet_mat_get(CufetMatrix* m, long long r, long long c, int line) {
    if (r < 1 || r > m->rows) cufet_raise(cufet_msgf("Row index %lld is out of range — this matrix has %d row(s) (line %d).", r, m->rows, line));
    if (c < 1 || c > m->cols) cufet_raise(cufet_msgf("Column index %lld is out of range — this matrix has %d column(s) (line %d).", c, m->cols, line));
    return m->data[(r - 1) * m->cols + (c - 1)];
}
/* The write half. Same bounds, same messages — a matrix is a pointer, so this mutates every
   binding that names it, exactly as the interpreter's shared decimal[] does. */
static void cufet_mat_set(CufetMatrix* m, long long r, long long c, CufetDec v, int line) {
    if (r < 1 || r > m->rows) cufet_raise(cufet_msgf("Row index %lld is out of range — this matrix has %d row(s) (line %d).", r, m->rows, line));
    if (c < 1 || c > m->cols) cufet_raise(cufet_msgf("Column index %lld is out of range — this matrix has %d column(s) (line %d).", c, m->cols, line));
    m->data[(r - 1) * m->cols + (c - 1)] = v;
}
/* `a matrix of R by C [filled with F]` — runtime validation for non-literal dimensions
   (literals are rejected statically by the typechecker), matching the interpreter's messages. */
static CufetMatrix* cufet_mat_sized(CufetDec rd, CufetDec cd, CufetDec fill, int line) {
    long long r = cufet_to_int(rd), c = cufet_to_int(cd);
    if (cufet_cmp(rd, cufet_dec_from_ll(r)) != 0 || r < 1) cufet_raise(cufet_msgf("Matrix row count must be a positive whole number, but got %s (line %d).", cufet_text_from_dec(rd), line));
    if (cufet_cmp(cd, cufet_dec_from_ll(c)) != 0 || c < 1) cufet_raise(cufet_msgf("Matrix column count must be a positive whole number, but got %s (line %d).", cufet_text_from_dec(cd), line));
    CufetMatrix* m = cufet_mat_new((int)r, (int)c);
    if (cufet_cmp(fill, cufet_dec_from_ll(0)) != 0)   /* interpreter skips the fill when it equals 0 */
        for (long long i = 0; i < r * c; i++) m->data[i] = fill;
    return m;
}
static CufetMatrix* cufet_mat_add(CufetMatrix* a, CufetMatrix* b) {
    if (a->rows != b->rows || a->cols != b->cols) return NULL;
    CufetMatrix* m = cufet_mat_new(a->rows, a->cols);
    for (int i = 0; i < a->rows * a->cols; i++) m->data[i] = cufet_add(a->data[i], b->data[i]);
    return m;
}
static CufetMatrix* cufet_mat_sub(CufetMatrix* a, CufetMatrix* b) {
    if (a->rows != b->rows || a->cols != b->cols) return NULL;
    CufetMatrix* m = cufet_mat_new(a->rows, a->cols);
    for (int i = 0; i < a->rows * a->cols; i++) m->data[i] = cufet_sub(a->data[i], b->data[i]);
    return m;
}
static CufetMatrix* cufet_mat_mul(CufetMatrix* a, CufetMatrix* b) {   /* real matrix product, m×n · n×p */
    if (a->cols != b->rows) return NULL;
    CufetMatrix* m = cufet_mat_new(a->rows, b->cols);
    for (int r = 0; r < a->rows; r++)
        for (int c = 0; c < b->cols; c++) {
            CufetDec s = cufet_dec_from_ll(0);
            for (int k = 0; k < a->cols; k++)
                s = cufet_add(s, cufet_mul(a->data[r * a->cols + k], b->data[k * b->cols + c]));
            m->data[r * b->cols + c] = s;
        }
    return m;
}
static CufetMatrix* cufet_mat_transpose(CufetMatrix* a) {
    CufetMatrix* m = cufet_mat_new(a->cols, a->rows);
    for (int r = 0; r < a->rows; r++)
        for (int c = 0; c < a->cols; c++)
            m->data[c * a->rows + r] = a->data[r * a->cols + c];
    return m;
}
/* matrix((1, 2), (3, 4)) — matches the interpreter's FormatMatrix exactly. */
static void cufet_mat_write(CufetMatrix* m) {
    printf("matrix(");
    for (int r = 0; r < m->rows; r++) {
        if (r) printf(", ");
        printf("(");
        for (int c = 0; c < m->cols; c++) { if (c) printf(", "); cufet_write_number(m->data[r * m->cols + c]); }
        printf(")");
    }
    printf(")");
}

""";

    // Chance runtime (Arc 1E — the chance book). A small self-contained xorshift64* PRNG: seedable
    // via `Seed the chance with N` (truncated to integer, mixed, nonzero-forced), lazily time-seeded
    // on first use when unseeded (each run differs, like the interpreter's unseeded Random). The
    // observable GUARANTEE is per-backend: a seeded run is self-consistent (same seed → same
    // sequence within this backend); cross-backend sequences intentionally differ (settled fork —
    // invariants, not bit-identity). Single global state, matching the interpreter's one _rng.
    private const string ChanceRuntime =
"""
#include <time.h>
static unsigned long long cufet_rng_state;
static int cufet_rng_inited = 0;
static void cufet_rng_seed(long long s) {
    unsigned long long z = (unsigned long long)s + 0x9E3779B97F4A7C15ULL;   /* splitmix64 mix */
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
    cufet_rng_state = z ^ (z >> 31);
    if (cufet_rng_state == 0) cufet_rng_state = 88172645463325252ULL;
    cufet_rng_inited = 1;
}
static unsigned long long cufet_rng_u64(void) {
    if (!cufet_rng_inited) cufet_rng_seed((long long)time(NULL) ^ ((long long)clock() << 20));
    unsigned long long x = cufet_rng_state;
    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
    cufet_rng_state = x;
    return x * 0x2545F4914F6CDD1DULL;
}
static long long cufet_rng_below(long long bound) {   /* uniform-ish in [0, bound); bound > 0 */
    return (long long)(cufet_rng_u64() % (unsigned long long)bound);
}
/* `a random number from L to H` — inclusive bounds ([lo, hi], matching .Next(lo, hi+1)); the
   decimal low>high check + message mirror the interpreter's RuntimeException. */
static CufetDec cufet_random_number(CufetDec low, CufetDec high, int line) {
    if (cufet_cmp(low, high) > 0)
        cufet_raise(cufet_msgf("Random number range is invalid: low (%s) is greater than high (%s) (line %d).",
                    cufet_text_from_dec(low), cufet_text_from_dec(high), line));
    long long lo = cufet_to_int(low), hi = cufet_to_int(high);
    return cufet_dec_from_ll(lo + cufet_rng_below(hi - lo + 1));
}

""";

    // SIGINT signal substrate (CONC.E): true-preemptive interrupt. Emitted when the program uses
    // interrupt constructs OR concurrency (blocked channel-waits become interruptible). The handler is
    // MINIMAL + async-signal-safe (it only sets an atomic flag); all real work happens at cooperative
    // checkpoints (`Yield.`, channel-waits) in normal thread flow. An unhandled interrupt unwinds via a
    // per-thread longjmp landing pad — the main thread's pad tears down (pop all arenas, free channels,
    // flush) + exits; a worker's pad runs its local cleanup + returns (reaped by the structured join).
    // POSIX-guarded; on non-unix (mingw) it degrades to no-op stubs (Ctrl-C = default terminate).
    private const string SignalRuntime =
"""
#if defined(__unix__) || defined(__APPLE__)
#include <signal.h>
#include <setjmp.h>
/* The landing pad is established the same way everywhere; only the FLAVOUR of setjmp differs.
   sigsetjmp/siglongjmp save and restore the signal mask, which is what makes the unwind safe from
   a signal-interrupted checkpoint — and which mingw has no notion of. */
#define CUFET_SETJMP(b) sigsetjmp((b), 1)
static volatile sig_atomic_t cufet_interrupted = 0;
static _Thread_local sigjmp_buf cufet_thread_top;   /* this thread's interrupt landing pad */
static _Thread_local int cufet_pad_set = 0;          /* 1 once this thread has established its pad */
static void cufet_sigint_handler(int sig) { (void)sig; cufet_interrupted = 1; }   /* async-signal-safe */
static void cufet_install_sigint(void) {
    struct sigaction sa; memset(&sa, 0, sizeof(sa));
    sa.sa_handler = cufet_sigint_handler;
    sigaction(SIGINT, &sa, NULL);
}
/* Cooperative interrupt checkpoint: if an interrupt is pending and this thread has a landing pad,
   unwind to it. No-op if no pad (a raw task thread) — its caller handles the -1 recv sentinel. */
static void cufet_checkpoint(void) {
    if (cufet_interrupted && cufet_pad_set) siglongjmp(cufet_thread_top, 1);
}
#else
/* mingw: no sigaction and no signal mask, so Ctrl-C keeps its default (terminate) and the
   checkpoint never unwinds — `cufet_interrupted` is never set. The landing pad still EXISTS,
   because the task and pipe machinery establishes one unconditionally; here setjmp always returns
   0 and the body simply runs. Declaring it is what lets threads compile on a platform that has
   pthreads (mingw-w64 ships winpthreads) but not POSIX signals. */
#include <setjmp.h>
/* ⚠ The no-unwind form, like the exception pad. Nothing longjmps to this pad on mingw today —
   cufet_interrupted is never set — but a pad established with a bare `setjmp` is a crash waiting
   for the day something does jump to it, and the two pads should not differ in a way nobody
   intended. See CUFET_PLAIN_SETJMP. */
#define CUFET_SETJMP(b) CUFET_PLAIN_SETJMP(b)
static volatile int cufet_interrupted = 0;
static _Thread_local jmp_buf cufet_thread_top;
static _Thread_local int cufet_pad_set = 0;
static void cufet_install_sigint(void) {}
static void cufet_checkpoint(void) {}
#endif

""";

    // Concurrency runtime (CONC.A+B): pthreads + a thread-safe channel (mutex + condvar). Emitted
    // only when tasks/channels are used; POSIX-guarded (Linux-targeted, WSL-verified). The channel is
    // ONE type-erased container (like the interpreter's single ChannelValue holding `object`s): each
    // node carries a `void*` to a malloc'd, fully-heap-owned envelope of the element value (built by
    // the per-element-type `cchan_<T>_heapenv` deep-copy on send). Recv hands the envelope back; the
    // caller arena-copies it in + frees it. Teardown of un-received nodes frees each envelope via the
    // channel's `freeval` (installed at creation from the element type) — so channel-of-T is race-free
    // and leak-free by the same construction as the number-only A+B channel, for arbitrary T.
    private const string ConcurrencyRuntime =
"""
#if defined(__unix__) || defined(__APPLE__) || defined(__MINGW32__)
#include <pthread.h>
#include <time.h>
#define CUFET_TASK_MAX 4096
typedef struct cufet_chan_node { void* val; struct cufet_chan_node* next; } cufet_chan_node;
typedef struct { pthread_mutex_t m; pthread_cond_t c; cufet_chan_node* head; cufet_chan_node* tail; int closed; void (*freeval)(void*); } cufet_chan;
/* Live-channel registry — so an interrupt unwind (CONC.E) can free channels the longjmp jumped past.
   A normal cufet_chan_free unregisters; the interrupt teardown frees whatever is still registered. */
static cufet_chan* cufet_live_chans[CUFET_TASK_MAX];
static int cufet_nlive = 0;
static pthread_mutex_t cufet_live_m = PTHREAD_MUTEX_INITIALIZER;
static cufet_chan* cufet_chan_new(void (*freeval)(void*)) {
    cufet_chan* ch = (cufet_chan*)malloc(sizeof(cufet_chan));
    pthread_mutex_init(&ch->m, NULL); pthread_cond_init(&ch->c, NULL);
    ch->head = ch->tail = NULL; ch->closed = 0; ch->freeval = freeval;
    pthread_mutex_lock(&cufet_live_m); if (cufet_nlive < CUFET_TASK_MAX) cufet_live_chans[cufet_nlive++] = ch; pthread_mutex_unlock(&cufet_live_m);
    return ch;
}
/* Enqueues a heap envelope (already a self-contained deep copy of the element — no arena pointers). */
static void cufet_chan_send(cufet_chan* ch, void* env) {
    cufet_chan_node* n = (cufet_chan_node*)malloc(sizeof(cufet_chan_node));
    n->val = env; n->next = NULL;
    pthread_mutex_lock(&ch->m);
    if (ch->tail) ch->tail->next = n; else ch->head = n; ch->tail = n;
    pthread_cond_signal(&ch->c); pthread_mutex_unlock(&ch->m);
}
/* Blocking receive → 1 with *out set to the heap envelope if a value is available, 0 if the channel
   is empty-and-closed (→ Cufet void), -1 if a SIGINT arrived while blocked (CONC.E — the caller runs
   a checkpoint). The wait is a 50ms timed-wait loop so a blocked worker re-checks the interrupt flag
   (true-preemptive: a real pthread_cond_wait can be woken by a signal). Frees the node (not the
   envelope — the caller arena-copies from it, then frees it via cchan_<T>_freeenv). */
static int cufet_chan_recv(cufet_chan* ch, void** out) {
    pthread_mutex_lock(&ch->m);
    while (!ch->head && !ch->closed) {
        if (cufet_interrupted) { pthread_mutex_unlock(&ch->m); return -1; }
        struct timespec ts; clock_gettime(CLOCK_REALTIME, &ts);
        ts.tv_nsec += 50000000L; if (ts.tv_nsec >= 1000000000L) { ts.tv_sec++; ts.tv_nsec -= 1000000000L; }
        pthread_cond_timedwait(&ch->c, &ch->m, &ts);
    }
    if (ch->head) {
        cufet_chan_node* n = ch->head; ch->head = n->next; if (!ch->head) ch->tail = NULL;
        pthread_mutex_unlock(&ch->m);
        *out = n->val; free(n); return 1;
    }
    pthread_mutex_unlock(&ch->m); return 0;
}
/* ── A named task's result box ───────────────────────────────────────────────
   The task publishes its result envelope here exactly once; any number of awaiters — the rabbit
   body, other tasks, or the same awaiter twice — wait for it and deep-copy into their own arena.

   ★ Nobody joins at an await site. pthread_join happens once, in the rabbit's Done. teardown,
   which the structured guarantee requires anyway. That is what makes N awaiters safe BY
   CONSTRUCTION: a check-then-join guard is only sound while exactly one thread may run it, and
   `the awaited result of x` can now appear in several tasks at once.

   The box owns the envelope until teardown frees it through `freeenv`, so awaiters only ever
   read it. Awaiters copy rather than share because arenas are thread-local — each one needs the
   value in its own. */
typedef struct {
    pthread_mutex_t m;
    pthread_cond_t  c;
    int    done;                  /* published (a NULL env means the task was abandoned) */
    void*  env;                   /* malloc'd result envelope, owned by the box */
    void (*freeenv)(void*);       /* per-element-type deep free, recorded at spawn */
} cufet_rbox;

static cufet_rbox* cufet_rbox_new(void (*freeenv)(void*)) {
    cufet_rbox* b = (cufet_rbox*)malloc(sizeof(cufet_rbox));
    pthread_mutex_init(&b->m, NULL);
    pthread_cond_init(&b->c, NULL);
    b->done = 0; b->env = NULL; b->freeenv = freeenv;
    return b;
}
static void cufet_rbox_publish(cufet_rbox* b, void* env) {
    if (!b) { if (env) free(env); return; }
    pthread_mutex_lock(&b->m);
    b->env = env; b->done = 1;
    pthread_cond_broadcast(&b->c);
    pthread_mutex_unlock(&b->m);
}
/* Returns the envelope, still owned by the box. Waits on a 50ms poll for the same reason
   cufet_chan_recv does: a thread blocked in an untimed wait cannot notice SIGINT, and INT.1
   made every blocking point interruptible. NULL means abandoned — the caller checkpoints. */
static void* cufet_rbox_await(cufet_rbox* b) {
    pthread_mutex_lock(&b->m);
    while (!b->done) {
        if (cufet_interrupted) { pthread_mutex_unlock(&b->m); return NULL; }
        struct timespec ts; clock_gettime(CLOCK_REALTIME, &ts);
        ts.tv_nsec += 50000000L; if (ts.tv_nsec >= 1000000000L) { ts.tv_sec++; ts.tv_nsec -= 1000000000L; }
        pthread_cond_timedwait(&b->c, &b->m, &ts);
    }
    void* e = b->env;
    pthread_mutex_unlock(&b->m);
    return e;
}
static void cufet_rbox_free(cufet_rbox* b) {
    if (!b) return;
    if (b->env) { if (b->freeenv) b->freeenv(b->env); else free(b->env); }
    pthread_mutex_destroy(&b->m);
    pthread_cond_destroy(&b->c);
    free(b);
}
static void cufet_chan_close(cufet_chan* ch) {
    pthread_mutex_lock(&ch->m); ch->closed = 1; pthread_cond_broadcast(&ch->c); pthread_mutex_unlock(&ch->m);
}
static void cufet_chan_free(cufet_chan* ch) {   /* frees un-received envelopes (teardown/close-with-pending) */
    pthread_mutex_lock(&cufet_live_m);
    for (int i = 0; i < cufet_nlive; i++) if (cufet_live_chans[i] == ch) { cufet_live_chans[i] = cufet_live_chans[--cufet_nlive]; break; }
    pthread_mutex_unlock(&cufet_live_m);
    cufet_chan_node* n = ch->head; while (n) { cufet_chan_node* x = n->next; if (ch->freeval) ch->freeval(n->val); free(n); n = x; }
    pthread_mutex_destroy(&ch->m); pthread_cond_destroy(&ch->c); free(ch);
}
/* Interrupt-teardown helper: free every still-live channel (the unwind longjmp'd past their frees). */
static void cufet_free_all_chans(void) {
    pthread_mutex_lock(&cufet_live_m);
    while (cufet_nlive > 0) {
        cufet_chan* ch = cufet_live_chans[--cufet_nlive];
        cufet_chan_node* n = ch->head; while (n) { cufet_chan_node* x = n->next; if (ch->freeval) ch->freeval(n->val); free(n); n = x; }
        pthread_mutex_destroy(&ch->m); pthread_cond_destroy(&ch->c); free(ch);
    }
    pthread_mutex_unlock(&cufet_live_m);
}
/* Exception-unwind helper (E-prime): free channels registered AFTER a Try-entry snapshot — the
   longjmp jumped past their rabbit teardown. Freeing from the top preserves snapshot indexing
   (cufet_chan_free swap-removes; removing the last entry is a plain pop). */
static void cufet_free_chans_from(int n) {
    while (cufet_nlive > n) cufet_chan_free(cufet_live_chans[cufet_nlive - 1]);
}
/* Rabbit teardown after a caught exception may see channels ALREADY freed at the catch — free
   only if still registered (idempotent teardown; no double-free). */
static void cufet_chan_free_if_live(cufet_chan* ch) {
    pthread_mutex_lock(&cufet_live_m);
    int live = 0;
    for (int i = 0; i < cufet_nlive; i++) if (cufet_live_chans[i] == ch) { live = 1; break; }
    pthread_mutex_unlock(&cufet_live_m);
    if (live) cufet_chan_free(ch);
}
/* Task pipes (CONC.D): each stage runs as its own thread connected by channels. `output <v>` and
   `for each … from the input` inside a stage read these THREAD-LOCAL implicit channels — mirroring
   the interpreter's per-stage _pipeOutputChan / _pipeInputChan, but thread-local so concurrent
   stages don't clash. A stage closes its output channel when its function returns → downstream sees
   the stream complete (recv returns void on empty-and-closed). All values cross the SAME heap-bridged
   channel boundary as A+B, so inter-stage streaming is race-free by the same construction. */
static _Thread_local cufet_chan* cufet_pipe_in;
static _Thread_local cufet_chan* cufet_pipe_out;
/* A stage is a closure value: fn takes the captured env (NULL for a plain named-function stage, whose
   fn is an env-ignoring thunk). The env is allocated in the pipe's creating scope, which blocks on the
   join, so sharing it read-only with the stage thread is race-free (value captures are immutable). */
typedef struct { cufet_chan* in; cufet_chan* out; void (*fn)(void* env); void* env; } cufet_pipe_arg;
static void* cufet_pipe_stage(void* argp) {
    cufet_pipe_arg* a = (cufet_pipe_arg*)argp;
    cufet_pipe_in = a->in; cufet_pipe_out = a->out;
    cufet_arena_push();
    /* Interrupt landing pad (CONC.E): if a blocked recv inside the stage is interrupted, it unwinds
       to here and the stage tears down normally (arena pop + close output + reaped by the pipe join). */
    if (CUFET_SETJMP(cufet_thread_top) == 0) { cufet_pad_set = 1; a->fn(a->env); }
    /* INT.1: run this thread's pending unmakers + close its open files before tearing down. Both
       registries are _Thread_local, so this touches only the stage's own. On the interrupt path
       these would otherwise be skipped entirely (destructors never fire, buffered writes lost). */
    cufet_run_unmakers_to(0);
    cufet_close_files_from(0);
    cufet_arena_pop();
    if (a->out) cufet_chan_close(a->out);      /* signal downstream: no more values */
    free(a);
    return NULL;
}
#endif

""";

    public string Generate(Program program)
    {
        // Kept for the whole-program questions a single statement cannot answer on its own — chiefly
        // whether anything outside a task ever looks at a binding that task writes to.
        _program = program;
        var sb = new StringBuilder();

        // ★ The FIXED runtime accumulates separately from everything generated. It is the same text
        // in the same order either way — `runtime + sb + body` is byte-for-byte what one buffer
        // produced — but keeping it apart is what lets `build` compile it once and cache the object,
        // and what lets `emit-c` hand back a file containing the program rather than the program
        // buried under 955 lines of prelude. See RuntimeSplit for both.
        var runtime = new StringBuilder();

        // Concurrency is discovered up front (not during the body pass) because a rabbit's header
        // must emit its thread/channel tracking arrays before its body is walked.
        _usesConcurrency = ProgramUsesConcurrency(program.Statements);
        // SIGINT substrate (CONC.E) is likewise discovered up front — main's top installs the handler
        // + landing pad before its body, so it must know whether interrupt handling is in play.
        _usesSignals = ProgramUsesSignals(program.Statements);

        // ── Runtime: includes + software decimal + print helpers ──────────
        runtime.AppendLine(RuntimePreamble);

        // ── Arena allocator ───────────────────────────────────────────────
        // Pull a rabbit → cufet_arena_push(); body; Done. → cufet_arena_pop().
        // Arena is a tracked-pointer list: every cufet_arena_alloc() registers
        // the pointer; cufet_arena_pop() frees all of them in one shot.
        // When a series data buffer grows, the old buffer stays in ptrs
        // (wasted but harmless — freed at pop). No use-after-free, no leak.
        runtime.AppendLine("#define CUFET_ARENA_MAX_DEPTH 64");
        runtime.AppendLine("typedef struct { void** ptrs; int len; int cap; } CufetArena;");
        // Thread-local: each pthread bump-allocates in its OWN arena stack (no cross-thread arena
        // contention — sound because nothing mutable is shared; values cross threads via heap copy).
        runtime.AppendLine("static _Thread_local CufetArena cufet_arenas[CUFET_ARENA_MAX_DEPTH];");
        runtime.AppendLine("static _Thread_local int cufet_arena_top = -1;");
        runtime.AppendLine();
        // ★ A rabbit VALUE. The interpreter's is a name and nothing else (`RabbitValue`), printing
        // as `<rabbit den>`, so this matches it exactly rather than inventing state the oracle does
        // not have. A rabbit is a region, and a region is a scope the compiler already tracks
        // statically — there is nothing to carry at run time yet. When something needs the depth
        // (rabbit-scoped pointers), this is where it goes.
        runtime.AppendLine("typedef struct { const char* name; } cufet_rabbit;");
        runtime.AppendLine("static void cufet_rabbit_write(cufet_rabbit r) { printf(\"<rabbit %s>\", r.name); }");
        runtime.AppendLine("static int cufet_rabbit_eq(cufet_rabbit a, cufet_rabbit b) { return strcmp(a.name, b.name) == 0; }");
        runtime.AppendLine();
        runtime.AppendLine("static void cufet_arena_push(void) {");
        runtime.AppendLine("    ++cufet_arena_top;");
        runtime.AppendLine("    cufet_arenas[cufet_arena_top].ptrs = NULL;");
        runtime.AppendLine("    cufet_arenas[cufet_arena_top].len  = 0;");
        runtime.AppendLine("    cufet_arenas[cufet_arena_top].cap  = 0;");
        runtime.AppendLine("}");
        runtime.AppendLine();
        // Allocate into a SPECIFIC arena depth (not just the top). Used by cufet_raise to place an
        // exception message in the target handler's arena, so it outlives the pops the catch does.
        runtime.AppendLine("static void* cufet_arena_alloc_at(int depth, size_t size) {");
        runtime.AppendLine("    void* p = malloc(size);");
        runtime.AppendLine("    CufetArena* a = &cufet_arenas[depth];");
        runtime.AppendLine("    if (a->len == a->cap) {");
        runtime.AppendLine("        a->cap  = a->cap == 0 ? 8 : a->cap * 2;");
        runtime.AppendLine("        a->ptrs = (void**)realloc(a->ptrs, (size_t)a->cap * sizeof(void*));");
        runtime.AppendLine("    }");
        runtime.AppendLine("    a->ptrs[a->len++] = p;");
        runtime.AppendLine("    return p;");
        runtime.AppendLine("}");
        runtime.AppendLine();
        // ESC.2 — an allocation OVERRIDE. Normally -1 (allocate at the top, as always). An escaping
        // store sets it to the destination's arena depth for the duration of the store, which
        // redirects BOTH the value's own allocations AND the destination container's growth
        // (cser/cmap `_ensure` reallocs) into the arena that outlives the store. Redirecting the
        // allocator itself is what makes this work with the existing runtime unchanged.
        runtime.AppendLine("static _Thread_local int cufet_alloc_override = -1;");
        runtime.AppendLine("static void* cufet_arena_alloc(size_t size) {");
        runtime.AppendLine("    return cufet_arena_alloc_at(cufet_alloc_override >= 0 ? cufet_alloc_override : cufet_arena_top, size);");
        runtime.AppendLine("}");
        runtime.AppendLine();
        runtime.AppendLine("static void cufet_arena_pop(void) {");
        runtime.AppendLine("    CufetArena* a = &cufet_arenas[cufet_arena_top];");
        runtime.AppendLine("    for (int i = 0; i < a->len; i++) free(a->ptrs[i]);");
        runtime.AppendLine("    free(a->ptrs);");
        runtime.AppendLine("    a->ptrs = NULL;");
        runtime.AppendLine("    a->len  = 0;");
        runtime.AppendLine("    a->cap  = 0;");
        runtime.AppendLine("    --cufet_arena_top;");
        runtime.AppendLine("}");
        runtime.AppendLine();
        // ESC.3 — pop WITHOUT freeing: hand the top arena's blocks to its parent, then discard the
        // level. Used where a nonlocal exit leaves a rabbit carrying a value out (a `return`), so the
        // returned value cannot be freed here — but the LEVEL must still go, or cufet_arena_top
        // drifts upward on every such exit (past CUFET_ARENA_MAX_DEPTH = an out-of-bounds write).
        // Ownership moves outward; the blocks die at the destination's own pop. No copying, so a
        // returned value that ALIASES the caller's data stays the same pointer (measured: the
        // interpreter shares here, so copying would diverge).
        runtime.AppendLine("static void cufet_arena_merge_down(void) {");
        runtime.AppendLine("    if (cufet_arena_top < 1) { cufet_arena_pop(); return; }   /* no parent to merge into */");
        runtime.AppendLine("    CufetArena* a = &cufet_arenas[cufet_arena_top];");
        runtime.AppendLine("    CufetArena* p = &cufet_arenas[cufet_arena_top - 1];");
        runtime.AppendLine("    if (a->len > 0) {");
        runtime.AppendLine("        if (p->len + a->len > p->cap) {");
        runtime.AppendLine("            p->cap  = p->len + a->len;");
        runtime.AppendLine("            p->ptrs = (void**)realloc(p->ptrs, (size_t)p->cap * sizeof(void*));");
        runtime.AppendLine("        }");
        runtime.AppendLine("        for (int i = 0; i < a->len; i++) p->ptrs[p->len++] = a->ptrs[i];");
        runtime.AppendLine("    }");
        runtime.AppendLine("    free(a->ptrs);");
        runtime.AppendLine("    a->ptrs = NULL;");
        runtime.AppendLine("    a->len  = 0;");
        runtime.AppendLine("    a->cap  = 0;");
        runtime.AppendLine("    --cufet_arena_top;");
        runtime.AppendLine("}");
        runtime.AppendLine();
        // ESC.3 — copy a string into a specific arena depth. A failure's message/category can be
        // arena-templated (the I/O error bridge builds them with cufet_arena_msg), so a nonlocal exit
        // that pops the arena they live in must move them outward first — the EXCMSG fix, applied to
        // the failure path. A static literal copied here is a small wasted allocation, not a bug.
        runtime.AppendLine("static const char* cufet_arena_str_at(int depth, const char* s) {");
        runtime.AppendLine("    if (!s) return 0;");
        runtime.AppendLine("    size_t n = strlen(s) + 1;");
        runtime.AppendLine("    char* b = (char*)cufet_arena_alloc_at(depth, n);");
        runtime.AppendLine("    memcpy(b, s, n);");
        runtime.AppendLine("    return b;");
        runtime.AppendLine("}");
        runtime.AppendLine();

        // ── Series runtime ────────────────────────────────────────────────
        // Generalized to per-element-type structs (cser_N) synthesized like maps: forward-declared
        // here-adjacent (EmitSeriesForwardDecls, before the value structs) and fully defined after
        // (EmitSeriesRuntime). Nothing series-specific is emitted in the preamble now — a series of
        // number is just cser_<number>, one of many element types.

        // ── Text runtime (immutable strings; results arena-allocated) ─────
        runtime.AppendLine(TextRuntime);
        runtime.AppendLine(FileRuntime);

        // Object definitions are nominal types — collect them all up front (they may be
        // top-level or nested in Pull blocks) so literals and field access resolve.
        // Interfaces first: a parameter's shell ObjectType is only recognizable as an interface once
        // the interface declarations are known.
        CollectInterfaceDefs(program.Statements);
        CollectObjectDefs(program.Statements);
        // Operator overloads, likewise up front: a function emitted before the declaration can still
        // use the operator, so dispatch needs the whole registry before any body emits.
        CollectOverloadDefs(program.Statements);
        MergeUntoMethods(program.Statements);   // fold 'Bind ... unto <type>' methods into their type
        foreach (var def in _objectDefs.Values)
            ValidateObjectSupported(def);

        // Whole-program pipe analysis (channel-of-T): resolve each pipe-stage function's implicit
        // input element type by propagating types left-to-right through every task pipe, so a
        // `for each x from the input` can declare a concrete C type for x. Runs before the body pass.
        AnalyzePipes(program);

        // Method + function bodies + main are emitted into a separate buffer FIRST. That
        // pass discovers every record struct shape used (via TypeOf / EmitCType), so struct
        // declarations — which C requires before any use — can be assembled ahead of them.
        var body = new StringBuilder();

        // Free functions and named constructors (a constructor is just a function whose
        // return type is the object type — its ReturnType already carries that). 'unto'
        // methods are excluded here — they were merged into their object's method list.
        var topFuncs = program.Statements
            .OfType<BindStatement>()
            .Where(b => b.UntoType == null)
            .ToList();

        // Binds declared DIRECTLY inside a top-level `Pull a book` body are HOISTED and compiled as
        // ordinary free functions — the book scope is compile-time, so a Pull-body bind is morally
        // top-level (and matrix-typed functions can ONLY live inside a collections pull, since the
        // type isn't in scope outside it). Their book aliases are re-activated while their bodies
        // emit so book members resolve. Captured pull-scope locals are the closures gap (best-effort
        // clean throw via the task-capture walker).
        var pullBinds = new List<(BindStatement Bind, List<(string Local, string Book)> Aliases)>();
        void CollectPullBinds(IReadOnlyList<IStatement> stmts, List<(string Local, string Book)> aliases)
        {
            foreach (var st in stmts)
                if (st is PullStatement ps)
                {
                    var inner = new List<(string Local, string Book)>(aliases);
                    foreach (var (bookName, localName) in ps.Books) inner.Add((localName, bookName.ToLowerInvariant()));
                    foreach (var s2 in ps.Body)
                        if (s2 is BindStatement pb && pb.UntoType == null) pullBinds.Add((pb, inner));
                    CollectPullBinds(ps.Body, inner);   // nested pulls (the walker only matches PullStatement)
                }
        }
        CollectPullBinds(program.Statements, new List<(string, string)>());

        foreach (var bind in topFuncs)
        {
            _funcReturnTypes[bind.Name] = bind.ReturnType;
            _funcTypes[bind.Name] = new FunctionType(bind.Parameters.Select(p => p.Type).ToList(), bind.ReturnType);
            // Interface-taking functions are monomorphized — recorded here and emitted only as
            // specializations, never in their generic form (an interface param has no C type).
            if (HasIfaceParam(bind, IsIfaceParam)) _ifaceFuncs[bind.Name] = bind;
        }
        foreach (var def in _objectDefs.Values)
            foreach (var m in def.Methods)
                if (HasIfaceParam(m, IsIfaceParam)) _ifaceMethods[(def.Name, m.Name)] = m;
        foreach (var (bind, _) in pullBinds)
        {
            _funcReturnTypes[bind.Name] = bind.ReturnType;
            _funcTypes[bind.Name] = new FunctionType(bind.Parameters.Select(p => p.Type).ToList(), bind.ReturnType);
        }

        // ── Shared constants ──────────────────────────────────────────────────
        // Top-level `permanently` bindings are readable from every detached body, so they cannot be
        // locals of main. Identified by REFERENCE, not by name: a `permanently` local deeper in the
        // program may share a name and must stay a local.
        //
        // ★ Registered HERE, before any body is emitted, not at main-emission time. Bodies emit
        // first, so a function reading a constant needs its type already known — see
        // SeedSharedConstantTypes. Types are computed in source order and recorded into _varTypes as
        // they go, so a constant whose initialiser reads an earlier constant resolves.
        // ★ A function-VALUED top-level binding is hoisted for the same reason and by the same
        // mechanism. `Define doubler as a function given (…): … Done.` was emitted as a LOCAL OF
        // MAIN, so a method calling it got "unresolved call — not a known function or method"
        // while the interpreter ran the program fine. The checker had always allowed it: every
        // detached body imports anything FunctionType, because mutual recursion depends on that.
        // A name a method is allowed to call has to be a symbol a method can reach.
        // Source order, so `Define alias-of-doubler as doubler.` sees the binding it aliases.
        foreach (var topLevelConst in program.Statements.OfType<DefineStatement>())
        {
            // A `permanently` binding and a literal lambda MUST classify — they always could, and
            // swallowing a failure here would turn a loud error into a silently broken program.
            // Everything else is classified only to find function-VALUED bindings that are not
            // literal lambdas (an alias, or any expression yielding one), and a type this pass
            // cannot work out yet simply stays a local of main, exactly as it was before.
            bool mustClassify = topLevelConst.Permanent || topLevelConst.Value is LambdaLiteral;
            CufetType constType;
            if (mustClassify)
                constType = topLevelConst.DeclaredType ?? TypeOf(topLevelConst.Value);
            else
                try { constType = topLevelConst.DeclaredType ?? TypeOf(topLevelConst.Value); }
                catch { continue; }

            if (!topLevelConst.Permanent && constType is not FunctionType) continue;

            _sharedConstants.Add(topLevelConst);
            _sharedConstTypes[topLevelConst.Name] = constType;
            _varTypes[topLevelConst.Name]         = constType;
        }

        // The whole body emission, as a reusable pass: the CAT.2 discovery pre-pass runs it into a
        // throwaway buffer first so the OPEN-union case set is complete before the real pass emits
        // any `is a` tag check (function bodies emit before main, so an `is a T` can precede `Add T`).
        void EmitAllBodies(StringBuilder body)
        {
            // Object method / getter / setter bodies (each a C function taking a receiver pointer).
            foreach (var def in _objectDefs.Values)
            {
                // Interface-taking methods emit only as specializations (see DrainIfaceSpecializations).
                foreach (var method in def.Methods)
                    if (!_ifaceMethods.ContainsKey((def.Name, method.Name))) EmitMethod(body, def, method);
                foreach (var g in def.Getters)      EmitGetter(body, def, g);
                foreach (var s in def.Setters)      EmitSetter(body, def, s);
            }

            // Operator overload bodies (ordinary functions with two by-value operand params).
            foreach (var oad in _overloadDefs.Values)
                EmitOverload(body, oad);

            // Unmaker (destructor) bodies — emitted like no-arg void methods (cu_<type>).
            foreach (var ud in _unmakeDefs.Values)
                EmitUnmaker(body, ud);

            foreach (var bind in topFuncs)
                if (!_ifaceFuncs.ContainsKey(bind.Name)) EmitBind(body, bind);

            foreach (var (bind, aliases) in pullBinds)
            {
                // Best-effort capture check: a hoisted bind must not reference pull-scope locals
                // (params + its own defines + functions + book aliases are fine — anything else is
                // a closure capture, the deferred gap).
                var refs = new HashSet<string>(); var defs = new HashSet<string>();
                foreach (var s in bind.Body) CollectRefsDefs(s, refs, defs);
                var known = new HashSet<string>(bind.Parameters.Select(p => p.Name)
                    .Concat(defs).Concat(_funcReturnTypes.Keys).Concat(aliases.Select(a => a.Local)));
                var captured = refs.Where(r => !known.Contains(r) && r != "it" && r != "input" && r != "the failure").ToList();
                if (captured.Count > 0)
                    throw new CompilerException(
                        $"function '{bind.Name}' (inside a Pull-book block) captures '{captured[0]}' from the pull scope — closures are not yet supported by the compiler.");
                foreach (var (local, book) in aliases) _bookAliases[local] = book;
                EmitBind(body, bind);
                foreach (var (local, _) in aliases) _bookAliases.Remove(local);
            }

            // ── main() ────────────────────────────────────────────────────────
            // A global arena is pushed so series created at top level (outside an
            // explicit Pull) are safely tracked and freed at program exit. The shared constants
            // were registered before any body was emitted (see above); main only ASSIGNS them.
            body.AppendLine("int main(void) {");
            // Before anything is printed. Threads inherit the process's stdout mode, so tasks are
            // covered by this one call. See CUFET_NL for why it is needed at all.
            body.AppendLine("    CUFET_STDOUT_BINARY();");
            // SIGINT landing pad (CONC.E): install the handler + establish main's interrupt pad. On an
            // unhandled interrupt a checkpoint siglongjmps here; we tear down (pop all arenas — nested
            // included — free any live channels, flush) and exit 130 (128+SIGINT). Guarded so a non-signal
            // program is unchanged, and #if'd so mingw (no sigaction) degrades to default Ctrl-C.
            if (_usesSignals || _usesConcurrency)
            {
                body.AppendLine("#if defined(__unix__) || defined(__APPLE__)");
                body.AppendLine("    cufet_install_sigint();");
                body.AppendLine("    if (CUFET_SETJMP(cufet_thread_top)) {");
                body.AppendLine("        cufet_close_files_from(0);   /* flush+close open files (E's file gap, closed by the E-prime registry) */");
                body.AppendLine("        while (cufet_arena_top >= 0) cufet_arena_pop();");
                if (_usesConcurrency)
                    body.AppendLine("        cufet_free_all_chans();");
                body.AppendLine("        fflush(stdout); return 130;");
                body.AppendLine("    }");
                body.AppendLine("    cufet_pad_set = 1;");
                body.AppendLine("#endif");
            }
            body.AppendLine("    cufet_arena_push();");
            foreach (var stmt in program.Statements)
            {
                if (stmt is BindStatement) continue;       // emitted above
                if (stmt is ObjectDefinition) continue;    // declarations, handled up front
                if (stmt is OperatorOverloadDeclaration) continue;   // emitted above as functions
                if (stmt is UnmakerDeclaration) continue;           // emitted above as cu_<type> fns
                if (stmt is InterfaceDefinition) continue;          // no runtime representation
                EmitStatement(body, stmt, "    ");
            }
            body.AppendLine("    cufet_arena_pop();");
            body.AppendLine("    return 0;");
            body.AppendLine("}");
        }


        // ── CAT.2 discovery: fill the bounded open-union case set before the real emission ──
        if (ProgramUsesOpenUnion(program.Statements))
        {
            // Set here (not lazily at EmitCType) because the FIRST EmitCType(open) can happen in
            // EmitSeriesRuntime — which runs AFTER EmitStructs, i.e. too late to emit the struct.
            _usesOpenUnion = true;
            _discoveringOpenUnion = true;
            for (int pass = 0; pass < 8; pass++)
            {
                int before = _openUnionCases.Count;
                ResetEmissionBuffers();
                // A partial pass still discovers: an error here (e.g. a not-yet-discovered case makes a
                // narrow fail) just means this iteration stopped early; the next one gets further. Any
                // GENUINE error resurfaces in the real pass below.
                try { EmitAllBodies(new StringBuilder()); } catch (CompilerException) { }
                if (_openUnionCases.Count == before) break;   // fixed point (the set grows monotonically)
            }
            _discoveringOpenUnion = false;
            ResetEmissionBuffers();
        }
        EmitAllBodies(body);

        // ── Interface monomorphization: emit the specializations the body pass DISCOVERED ──
        // The discovery IS the emission (each call site registered its specialization as it emitted),
        // so no call site can be missed. Draining can register further specializations, hence the
        // fixed point. Runs before the struct/forward-decl sections so their shapes are registered.
        DrainIfaceSpecializations(body);

        // ── The OPTIONAL fixed runtime, all of it, before anything generated ─────────────────────
        //
        // ★ These used to be scattered — matrix here, then case/math/chance/process/signal/
        // concurrency AFTER the struct, series and map sections. That interleaving is what made the
        // emitter's ordering rules something to rediscover rather than state, and it is the direct
        // cause of the recurring "symbol emitted above its own declaration" defect: every fix was a
        // MOVE, which is the tell that a rule was missing rather than broken.
        //
        // Hoisting them is safe because the fixed runtime never names a generated type — no `cser_`,
        // `cmap_`, `crec_`, `cobj_`, `cun_`, `cfn_` or `cenv_` appears anywhere in it. Nothing here
        // can depend on anything below, so "runtime first, generated second" holds unconditionally
        // and there is no longer a per-block placement question to get wrong.
        //
        // Their order RELATIVE TO EACH OTHER still matters and is preserved: the signal substrate
        // supplies the interrupt flag the concurrency runtime's channel-wait checks, and the case
        // table calls the decimal helpers in the preamble.
        if (_usesMatrix) runtime.AppendLine(MatrixRuntime);
        if (_usesCase) runtime.AppendLine(CaseRuntime);
        if (_usesChance) runtime.AppendLine(ChanceRuntime);
        if (_usesProcess) runtime.AppendLine(ProcessRuntime);
        if (_usesSignals || _usesConcurrency) runtime.AppendLine(SignalRuntime);
        if (_usesConcurrency) runtime.AppendLine(ConcurrencyRuntime);

        // ── Series + map struct forward declarations (so value structs can hold their pointers) ──
        EmitSeriesForwardDecls(sb);
        EmitMapForwardDecls(sb);

        // ── Struct declarations (records + objects + voidables) + write/eq helpers ──
        EmitStructs(sb);

        // Closure-value structs (cfn_N {fn, env}) are emitted BY EmitStructs above, in the same
        // topological order as records and objects — the dependency runs both ways, so they cannot
        // be a phase of their own. See the note there.

        // ── Closure env structs (cenv_N; captured free vars) — after cfn (may capture a fn value) ──
        if (_closureEnvs.Length > 0)
        {
            sb.AppendLine("// ── Closure environments (captured free vars) ──");
            sb.Append(_closureEnvs);
            sb.AppendLine();
        }

        // ── Series + map container structs + helpers (need element/K/V + cfn structs above) ──
        EmitSeriesRuntime(sb);
        EmitMapRuntime(sb);

        // File-scope declarations for the shared constants assigned at the top of main. Collected
        // while main was emitted (their C types are only known then) and appended to `sb`, which
        // precedes every function body in the output — so a function can reference one.
        //
        // ★ Emitted HERE, after every type section, not before them. A constant's declaration names
        // its C type, and a REGION-typed one names a generated type: `static cser_0* cv_suits;`
        // two lines above `typedef struct cser_0_s cser_0;` is "unknown type name 'cser_0'". The
        // scalar cases hid it — CufetDec and const char* come from the prelude, so numbers, text
        // and facts all worked while a `permanently` lookup table would not build at all. Sitting
        // after the series/map runtime covers every shape a constant can have: scalars, records
        // and objects (EmitStructs), closures (EmitClosureStructs), series and maps (just above).
        // ── Foreign axioms — the C this program was handed, wrapped where it can be called ──
        // Above every body, below the runtime: an axiom calls cufet_dec_from_ll and nothing else
        // generated, and a body may call an axiom.
        if (_axiomFns.Length > 0)
        {
            // ⚠ The HEADERS go to the very top of the generated file, ahead of every struct and
            // helper — not here with the wrappers. On Windows the set includes <windows.h>, which
            // defines a great many macros, and a macro cannot reach text above it: emitting it in
            // the middle would compile the first half of the program under one macro environment
            // and the second half under another. One state for the whole file is the only version
            // of this worth reasoning about.
            sb.Insert(0, ForeignC.Headers + "\n" + ForeignC.GuardMacro + "\n"
                       + ForeignC.WholeResultType + "\n" + ForeignC.RealResultType + "\n"
                       + ForeignC.RealConversion + "\n\n");

            sb.AppendLine("// ── Foreign axioms (source this program was given, taken as written) ──");
            // ★ A whole number arriving from C, read the way its own C type says. `is_unsigned` is
            // decided at C compile time by CUFET_C_UNSIGNED, so a `size_t` above 2^63 arrives as
            // the number it is instead of a negative one — the coefficient is 128 bits wide, so
            // neither branch can lose anything. The interpreter's C# twin is ForeignShim.ReadResult
            // and the two have to agree on both branches.
            sb.AppendLine("static CufetDec cufet_dec_from_ull(unsigned long long v) {");
            sb.AppendLine("    CufetDec d; d.scale = 0; d.sign = 0; d.coef = (unsigned __int128)v; return d;");
            sb.AppendLine("}");
            sb.AppendLine($"static CufetDec cufet_dec_from_foreign({ForeignC.WholeResultCType} w) {{");
            sb.AppendLine("    return w.is_unsigned ? cufet_dec_from_ull(w.bits)");
            sb.AppendLine("                        : cufet_dec_from_ll((long long)w.bits);");
            sb.AppendLine("}");
            // ★ The wrappers themselves stay here. They call the two above and nothing else
            // generated, so anywhere below the runtime would do; what they must NOT do is drift
            // away from the headers, which is why both are written in this one block.
            sb.Append(_axiomFns);
            sb.AppendLine();
        }

        if (_sharedConstDecls.Count > 0)
        {
            sb.AppendLine("// ── Shared constants (top-level `permanently` bindings) ──");
            foreach (var declLine in _sharedConstDecls) sb.AppendLine(declLine);
            sb.AppendLine();
        }

        // ── Channel-of-T deep-copy helpers (need the series/map/record/object structs above) ──
        EmitChannelDeepCopy(sb);

        // ── Forward declarations: object methods/getters/setters, overloads, then free functions ──
        // These come BEFORE the generated task thread functions (below), which are BODIES — a task
        // body can call any of them. (Emitting the task bodies first made an overload call inside a
        // task an implicit declaration; the same hole existed for a free-function call from a task.)
        foreach (var def in _objectDefs.Values)
        {
            foreach (var method in def.Methods)
                if (!_ifaceMethods.ContainsKey((def.Name, method.Name)))
                    sb.AppendLine($"{MethodSignature(def, method)};");
            foreach (var g in def.Getters)      sb.AppendLine($"{GetterSignature(def, g)};");
            foreach (var s in def.Setters)      sb.AppendLine($"{SetterSignature(def, s)};");
        }
        foreach (var oad in _overloadDefs.Values)
            sb.AppendLine($"{OverloadSignature(oad)};");
        foreach (var typeName in _unmakeDefs.Keys)
            sb.AppendLine($"static void {UnmakerCName(typeName)}(void*);");
        foreach (var sig in _ifaceSpecSigs)     // interface specializations (monomorphized copies)
            sb.AppendLine($"{sig};");
        foreach (var bind in topFuncs)
            if (!_ifaceFuncs.ContainsKey(bind.Name))
                sb.AppendLine($"{EmitFunctionSignature(bind)};");
        foreach (var (bind, _) in pullBinds)
            sb.AppendLine($"{EmitFunctionSignature(bind)};");
        sb.AppendLine();

        // ── Generated task thread functions (bodies — after the forward decls they may call) ──
        if (_usesConcurrency)
            sb.Append(_taskFns);

        // ── Named-function value thunks (after the function forward-decls they call) ──
        EmitFnThunks(sb);

        // ── Closure functions (cv_clos<id>) — after the env structs + fn forward-decls they use ──
        if (_closureFns.Length > 0)
        {
            sb.AppendLine("// ── Closure bodies (lambda / nested-Bind functions) ──");
            sb.Append(_closureFns);
        }

        sb.Append(body);

        _lastRuntime = runtime.ToString();
        _lastProgram = sb.ToString();
        return _lastRuntime + _lastProgram;
    }

    // The two halves of the most recent Generate, kept so a caller that wants them separately does
    // not have to run the whole emitter twice — and so the combined form stays the concatenation of
    // exactly what the split form compiles, rather than a second code path that can drift from it.
    private string _lastRuntime = "";
    private string _lastProgram = "";

    /// <summary>
    /// The program and the fixed runtime as separate translation units, plus the header that
    /// declares the runtime to the program. Call after <see cref="Generate"/>.
    /// </summary>
    /// <remarks>
    /// ★ Only the FIXED runtime moves out. Series, maps, structs, closures and channel deep-copy
    /// helpers are generated per program — a `series of number` is a different C type from a
    /// `series of text` — so they belong to the program and could never be shared. What leaves is
    /// the part that is identical in every build, which measured at 50 KB and ~415 ms of gcc.
    /// </remarks>
    public (string Header, string Runtime, string Program) GenerateSplit(Program program)
    {
        Generate(program);
        var (header, runtimeSource) = RuntimeSplit.Split(_lastRuntime);
        return (header, runtimeSource, $"#include \"{RuntimeSplit.HeaderFileName}\"\n\n{_lastProgram}");
    }

    /// <summary>A Cufet nominal name flattened into something C will accept as an identifier.</summary>
    /// <remarks>
    /// ⚠ Spaces as well as hyphens. A filled-in template is named for its filling
    /// (`stack of number`), and that name is deliberately un-typeable by a writer — which is exactly
    /// what makes it collision-proof, and exactly what makes it illegal C. One helper because the
    /// rule is used by the struct name, the method name, the getter and the setter, and four copies
    /// of it is four chances for the next name shape to be flattened in only three of them.
    /// </remarks>
    private static string CIdent(string name)
    {
        // Fast path: hyphens and spaces are the only exotic characters in writer-typeable names
        // and in fillings by simple types (`stack of number`).
        if (name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or ' ' or '_'))
            return name.Replace('-', '_').Replace(' ', '_');

        // A filling by a STRUCTURAL type (`unique of record (age: number, name: text)`) carries
        // punctuation no single replacement rule can keep distinct — flattening `(`/`:`/`,` all
        // to `_` could collide two different shapes. Flatten for readability, then pin identity
        // with a stable hash of the original name (FNV-1a; string.GetHashCode is randomized
        // per process and must never reach an emitted symbol).
        var flat = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        uint hash = 2166136261;
        foreach (char c in name) { hash ^= c; hash *= 16777619; }
        return $"{flat}_{hash:x8}";
    }

    // Nominal C struct name for an object type.
    private static string ObjStructName(string objectName) => "cd_" + CIdent(objectName);

    // ── Interfaces: monomorphization (Arc 3, DD.1) ────────────────────────────

    // Collects interface declarations (same reach as CollectObjectDefs).
    // Every InterfaceDefinition anywhere in the tree. Generic walk for the same reason its twin
    // CollectObjectDefs uses one: the two were near-identical hand-written switches that had
    // DRIFTED — this one grew a PullStatement arm and the other never did, which is how
    // `Define object` inside a book pull came to crash the compiler.
    private void CollectInterfaceDefs(IEnumerable<IStatement> stmts) =>
        AstSearch.Visit(stmts, n => { if (n is InterfaceDefinition ifd) _interfaceDefs[ifd.Name] = ifd; });

    // Is this parameter type an INTERFACE? The parser emits a bare shell ObjectType for any user
    // type name; the front-end's ResolveParamType turns a shell whose name is a declared interface
    // into an InterfaceType. Mirror that decision here.
    private bool IsIfaceParam(CufetType t) =>
        _interfaceDefs.Count > 0
        && ((t is ObjectType { Methods.Count: 0, NamedFields.Count: 0 } ot && _interfaceDefs.ContainsKey(ot.Name))
            || t is InterfaceType);

    private string IfaceParamName(CufetType t) =>
        t is InterfaceType it ? it.Name : ((ObjectType)t).Name;

    private static bool HasIfaceParam(BindStatement b, Func<CufetType, bool> isIface) =>
        b.Parameters.Any(p => isIface(p.Type));

    // The emitted C name of a specialization: the base name plus the concrete conformer(s) actually
    // passed, so `announce(dog)` and `announce(cat)` are distinct functions.
    private static string SpecSuffix(IEnumerable<string> concretes) =>
        "__" + string.Join("_", concretes.Select(c => c.Replace('-', '_')));

    // Registers (if new) and returns the specialization for `bind` given the concrete argument
    // types at THIS call site, then yields the C call. Registration happens during emission, so a
    // call site that exists cannot fail to produce its specialization.
    private string EmitSpecializedCall(BindStatement bind, string? owner, IReadOnlyList<IExpression> args,
                                       string? receiverPrefix)
    {
        // Method calls pass the receiver first; its params line up with args after that.
        var callArgs = owner != null && receiverPrefix != null ? args : args;
        var concretes = new List<string>();
        for (int i = 0; i < bind.Parameters.Count && i < callArgs.Count; i++)
        {
            if (!IsIfaceParam(bind.Parameters[i].Type)) continue;
            if (TypeOf(callArgs[i]) is not ObjectType cot)
                throw new CompilerException(
                    $"'{bind.Name}': the '{IfaceParamName(bind.Parameters[i].Type)}' parameter must receive a concrete " +
                    "object at the call site (interfaces are monomorphized — there is no runtime interface value).");
            concretes.Add(cot.Name);
        }

        string baseName = owner == null ? MangleName(bind.Name) : MethodCName(owner, bind.Name);
        string specName = baseName + SpecSuffix(concretes);
        if (!_ifaceSpecReq.ContainsKey(specName))
        {
            if (_ifaceSpecReq.Count >= MaxInterfaceSpecializations)
                throw new CompilerException(
                    $"too many interface specializations (over {MaxInterfaceSpecializations}). Interfaces are " +
                    "monomorphized — one copy per combination of concrete types passed — so a function with " +
                    "several interface parameters over many conformers multiplies out. Reduce the number of " +
                    "interface parameters on one function.");
            _ifaceSpecReq[specName] = (bind, owner, concretes);
        }

        var emitted = (receiverPrefix == null ? Enumerable.Empty<string>() : new[] { receiverPrefix })
                      .Concat(callArgs.Select(EmitExpr));
        return $"{specName}({string.Join(", ", emitted)})";
    }

    // Emits one specialization: the SAME body, with each interface parameter re-typed to the
    // concrete conformer. Everything inside is then concrete — method calls become the existing
    // direct dispatch, `is a <T>` constant-folds, field access is direct. No new machinery.
    private void EmitIfaceSpecialization(StringBuilder sb, string specName,
                                         (BindStatement Bind, string? Owner, IReadOnlyList<string> Concretes) req)
    {
        int k = 0;
        var concreteParams = req.Bind.Parameters
            .Select(p => IsIfaceParam(p.Type) ? (Type: (CufetType)ObjType(req.Concretes[k++]), p.Name) : p)
            .ToList();

        if (req.Owner == null)
        {
            // A synthetic Bind carrying the specialized name + concrete params: the ordinary
            // function emitter then produces the right signature and body with zero special-casing.
            var spec = req.Bind with { Name = specName, Parameters = concreteParams };
            _ifaceSpecSigs.Add(EmitSpecFunctionSignature(spec, specName));
            EmitBind(sb, spec, specName);
        }
        else
        {
            var def  = _objectDefs[req.Owner];
            var spec = req.Bind with { Parameters = concreteParams };
            _ifaceSpecSigs.Add(MethodSignature(def, spec, specName));
            EmitMethod(sb, def, spec, specName);
        }
    }

    // Drains the specialization worklist to a fixed point: emitting a specialization can itself
    // discover further call sites (a specialized function calling another interface-taking one),
    // and those are picked up on the next round.
    private void DrainIfaceSpecializations(StringBuilder body)
    {
        while (true)
        {
            var pending = _ifaceSpecReq.Where(kv => !_ifaceSpecDone.Contains(kv.Key)).ToList();
            if (pending.Count == 0) return;
            foreach (var (name, req) in pending)
            {
                _ifaceSpecDone.Add(name);
                EmitIfaceSpecialization(body, name, req);
            }
        }
    }

    // ── Operator overloading (Arc 3) ──────────────────────────────────────────
    // Dispatch is a COMPILE-TIME lookup: exact nominal match on both operands, at most one
    // candidate per (type, op). So an overloaded `a + b` lowers to a direct call — no vtable,
    // no runtime tag, no cost over an ordinary function call.

    private static string OpWord(TokenType op) => op switch
    {
        TokenType.Plus  => "add",
        TokenType.Minus => "sub",
        TokenType.Star  => "mul",
        _               => "div",
    };

    private static string OpSym(TokenType op) => op switch
    {
        TokenType.Plus => "+", TokenType.Minus => "-", TokenType.Star => "*", _ => "/",
    };

    private static string OverloadFnName(string typeName, TokenType op) =>
        $"cop_{typeName.Replace('-', '_')}_{OpWord(op)}";

    // Walks all statements collecting operator overloads (they are top-level only, but a Pull-book
    // body is morally top-level too — same reach as CollectObjectDefs).
    private void CollectOverloadDefs(IEnumerable<IStatement> stmts)
    {
        foreach (var stmt in stmts)
            switch (stmt)
            {
                case OperatorOverloadDeclaration oad: _overloadDefs[(oad.OperandTypeName, oad.Operator)] = oad; break;
                case UnmakerDeclaration ud:          _unmakeDefs[ud.UnmakesTypeName] = ud; break;
                case PullStatement ps:               CollectOverloadDefs(ps.Body); break;
                case PullRabbitStatement pr:         CollectOverloadDefs(pr.Body); break;
            }
    }

    // Emits one unmaker as a C function `void cu_<type>(void* p)` — a no-arg void method, so it
    // reuses the method-body machinery (`one` = the receiver, its fields resolve). Called via the
    // runtime registry (a void* thunk), so it casts the payload back to the concrete struct.
    private void EmitUnmaker(StringBuilder sb, UnmakerDeclaration ud)
    {
        string typeName = ud.UnmakesTypeName;
        var def = _objectDefs[typeName];
        var saved = new Dictionary<string, CufetType>(_varTypes);
        var savedRecv = _methodReceiverType;
        var savedRet = _currentReturnType;
        var savedExcOpen = _excOpen; _excOpen = 0;
        _varTypes.Clear();
        SeedSharedConstantTypes();
        _methodReceiverType = typeName;
        _currentReturnType = null;                    // void + infallible (front-end enforced)
        _varTypes["one"] = ObjType(typeName);

        sb.AppendLine($"static void {UnmakerCName(typeName)}(void* cf_p) {{");
        sb.AppendLine($"    {ObjStructName(typeName)}* cv_one = ({ObjStructName(typeName)}*)cf_p;");
        var savedUF = EnterFrame(sb, "    ");
        EmitBlock(sb, ud.Body, "    ");
        ExitFrame(savedUF);
        sb.AppendLine("}");
        sb.AppendLine();

        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
        _methodReceiverType = savedRecv;
        _currentReturnType = savedRet;
        _excOpen = savedExcOpen;
    }

    // The overload's inferred return type (RAW — keeps FailureType). Mirrors the front-end: the
    // success type is the first non-failure return, and any `return a failure` makes the whole
    // operator fallible (`T or failure`), exactly like matrix arithmetic.
    private CufetType? OverloadReturnType(string typeName, TokenType op)
    {
        var key = (typeName, op);
        if (_overloadReturnTypes.TryGetValue(key, out var cached)) return cached;
        if (!_overloadDefs.TryGetValue(key, out var oad)) return null;
        if (!_overloadInferring.Add(key))
            throw new CompilerException(
                $"the '{OpSym(op)}' overload for '{typeName}' is defined in terms of its own result type " +
                $"(its body uses '{OpSym(op)}' on two {typeName} values), so its return type can't be inferred.");
        try
        {
            var saved = new Dictionary<string, CufetType>(_varTypes);
            _varTypes[oad.LeftName]  = ObjType(typeName);
            _varTypes[oad.RightName] = ObjType(typeName);
            // InferTaskResultType is exactly the walk we need: first non-failure return = the success
            // type, `return a failure` ⇒ wrap in FailureType, `return void` ⇒ wrap in VoidableType.
            var rt = InferTaskResultType(oad.Body);
            _varTypes.Clear();
            foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
            _overloadReturnTypes[key] = rt;
            return rt;
        }
        finally { _overloadInferring.Remove(key); }
    }

    // The overload declared for this binary expression, if any: `+ - * /` with both operands the
    // SAME object type. Anything else (mixed operands, built-in types, other operators) is rejected
    // by the front-end before we get here, so a miss simply means "use the built-in path".
    private OperatorOverloadDeclaration? OverloadFor(BinaryExpression b)
    {
        if (_overloadDefs.Count == 0) return null;
        if (b.Op is not (TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash)) return null;
        if (TypeOf(b.Left) is not ObjectType lo || TypeOf(b.Right) is not ObjectType ro || lo.Name != ro.Name)
            return null;
        return _overloadDefs.TryGetValue((lo.Name, b.Op), out var oad) ? oad : null;
    }

    // The VALUE type of an overloaded operator expression: the declared return, with a fallible
    // overload unwrapped to its success type (the raw `T or failure` is seen only by
    // FallibleReturnType — the same unwrap convention as a fallible call). The front-end requires
    // every path of an overload body to return a value, so the return type is never absent.
    private CufetType OverloadValueType(OperatorOverloadDeclaration oad, TokenType op)
    {
        var rt = OverloadReturnType(oad.OperandTypeName, op);
        return rt is FailureType ft ? ft.Inner : rt ?? TNumber;
    }

    private string OverloadSignature(OperatorOverloadDeclaration oad)
    {
        var rt = OverloadReturnType(oad.OperandTypeName, oad.Operator);
        string oc = EmitCType(ObjType(oad.OperandTypeName));
        return $"static {(rt == null ? "void" : EmitCType(rt))} "
             + $"{OverloadFnName(oad.OperandTypeName, oad.Operator)}"
             + $"({oc} {MangleName(oad.LeftName)}, {oc} {MangleName(oad.RightName)})";
    }

    // An overload body is an ordinary function frame with two by-value operand params — no receiver
    // (`one` is not in scope: an overload is free-standing, not a method).
    private void EmitOverload(StringBuilder sb, OperatorOverloadDeclaration oad)
    {
        var saved        = new Dictionary<string, CufetType>(_varTypes);
        var savedRecv    = _methodReceiverType;
        var savedRet     = _currentReturnType;
        var savedExcOpen = _excOpen; _excOpen = 0;   // exc handlers never span function frames
        _varTypes.Clear();
        SeedSharedConstantTypes();
        _methodReceiverType = null;
        _currentReturnType  = OverloadReturnType(oad.OperandTypeName, oad.Operator);
        _varTypes[oad.LeftName]  = ObjType(oad.OperandTypeName);
        _varTypes[oad.RightName] = ObjType(oad.OperandTypeName);

        sb.AppendLine($"{OverloadSignature(oad)} {{");
        var savedOF = EnterFrame(sb, "    ");
        EmitBlock(sb, oad.Body, "    ");
        ExitFrame(savedOF);
        sb.AppendLine("}");
        sb.AppendLine();

        _varTypes.Clear();
        foreach (var kv in saved) _varTypes[kv.Key] = kv.Value;
        _methodReceiverType = savedRecv;
        _currentReturnType  = savedRet;
        _excOpen            = savedExcOpen;
    }

    // Every ObjectDefinition anywhere in the tree.
    //
    // ★ This was a hand-written switch with an arm per block-bearing statement, and it had no arm
    // for PullStatement — so `Define object` inside `Pull a book on ... Done.` was never collected,
    // and building one crashed the compiler with a raw KeyNotFoundException from _objectDefs. It
    // was also missing Try, With-open, Bind bodies and Judge arms. The generic walk has no list to
    // fall behind.
    private void CollectObjectDefs(IEnumerable<IStatement> stmts) =>
        AstSearch.Visit(stmts, n => { if (n is ObjectDefinition od) _objectDefs[od.Name] = od; });

    // Objects this slice: plain data + methods with direct dispatch. Everything fancier
    // (embedding, interface conformance/dispatch, getters/setters, named constructors,
    // destructors) is deferred — reject cleanly rather than miscompile.
    private void ValidateObjectSupported(ObjectDefinition def)
    {
        if (def.EmbeddedTypeName != null && !_objectDefs.ContainsKey(def.EmbeddedTypeName))
            throw new CompilerException($"object '{def.Name}': embeds '{def.EmbeddedTypeName}', which isn't a plain object type (interface embedding not supported yet).");
        // Interface conformance needs NO representation change: an object that conforms is still an
        // ordinary value struct. Conformance only tells the front-end which concrete types may be
        // passed to an interface parameter — and those parameters are monomorphized away.
        foreach (var iface in def.ConformedInterfaces)
            if (!_interfaceDefs.ContainsKey(iface))
                throw new CompilerException($"object '{def.Name}': conforms to '{iface}', which isn't a declared interface.");
    }

    // ── Record struct synthesis ───────────────────────────────────────────
    // A canonical structural signature dedups shapes; each distinct shape becomes
    // one C struct `cr_N` (positional fields p0.., named fields cv_<name> sorted by
    // name to match the interpreter's canonical print order). Records are C VALUE
    // structs — assignment copies, which reproduces the interpreter's value semantics
    // exactly (nested records/objects copy deeply; series fields are shared pointers).
    // ★ Stashes are normalised away on the way in, so no arm below — and nothing that reads a
    // signature — ever sees one. See NoStashes for why the front end keeps the distinction and the
    // back end must not.
    private string TypeSig(CufetType t) => TypeSigRaw(NoStashes(t));

    private string TypeSigRaw(CufetType t) => t switch
    {
        NumberType => "N",
        BitsType   => "B",
        FactType   => "F",
        TextType   => "T",
        SeriesType s => "S(" + TypeSig(s.ElementType) + ")",
        RecordType r => "R(" + string.Join(",", r.PositionalTypes.Select(TypeSig)) + "|" +
                        string.Join(",", r.NamedFields.Select(f => f.Name + ":" + TypeSig(f.Type))) + ")",
        ObjectType o => "O:" + o.Name,   // nominal — identity is the name
        VoidableType v => "V(" + TypeSig(v.Inner) + ")",
        MapType m => "M(" + TypeSig(m.KeyType) + "," + TypeSig(m.ValueType) + ")",
        FailureType f => "F(" + TypeSig(f.Inner) + ")",
        MatrixType => "MX",   // one fixed runtime struct (CufetMatrix*) — identity is the type itself
        RabbitType => "RB",
        AddressType => "AD",   // one opaque void* — every foreign pointer is the same type here
        FunctionType fn => "Fn(" + string.Join(",", fn.ParameterTypes.Select(TypeSig)) + "->" +
                           (fn.ReturnType == null ? "v" : TypeSig(fn.ReturnType)) + ")",
        // Closed union: cases in DECLARATION order (the front-end's UnionType.Equals is order-sensitive,
        // so `(number or text)` and `(text or number)` are distinct types — don't canonicalize).
        // A 1-case union (from extra parens, e.g. `(number)`) IS that type — normalize so it matches.
        UnionType u1 when u1.Cases is { Count: 1 } => TypeSig(u1.Cases[0]),
        UnionType u => u.Cases == null ? "U(*)" : "U(" + string.Join(",", FlatCases(u.Cases).Select(TypeSig)) + ")",
        _ => throw new CompilerException(
                 $"the compiler cannot represent a {FormatTypeName(t)} yet.")
    };

    /// <summary>Rewrites `stash of T` to its closure form, however deeply it is buried.</summary>
    /// <remarks>
    /// ★★ A stash is not a new runtime thing. StashTransform rewrites every burying function into a
    /// factory handing back a CLOSURE that takes nothing and gives back `voidable T`, and that
    /// closure is the only value a stash ever is. The front end keeps the two spellings APART on
    /// purpose — it is what makes `stash of number` say "stash of number" in an error, and what
    /// stops a stash being called directly instead of unburied — but the back end must not, because
    /// a `stash of T` parameter has to accept exactly what a `cast` of a burying function produces,
    /// and a slot holding one is an ordinary `series of` that closure.
    ///
    /// It RECURSES, and that is the part worth keeping: the stash that broke first was not a bare
    /// one but the element type of a hoisted local's slot, three layers down.
    /// </remarks>
    private static CufetType NoStashes(CufetType type) => type switch
    {
        StashType stash       => new FunctionType([], new VoidableType(NoStashes(stash.ElementType))),
        SeriesType series     => new SeriesType(NoStashes(series.ElementType)),
        VoidableType voidable => new VoidableType(NoStashes(voidable.Inner)),
        MapType map           => new MapType(NoStashes(map.KeyType), NoStashes(map.ValueType)),
        FunctionType fn       => new FunctionType(
                                     [.. fn.ParameterTypes.Select(NoStashes)],
                                     fn.ReturnType == null ? null : NoStashes(fn.ReturnType)),
        _ => type,
    };

    // A union whose case is ITSELF a union (`(number or (text or fact))` — the front-end parses and
    // runs this) is FLATTENED to one level. The nested and flat spellings are distinct front-end
    // types but denote the same value set: `IsAssignable` and `RuntimeIsType` both RECURSE through
    // a nested case, so no value can tell them apart. One flat tagged struct therefore serves both —
    // the same "canonicalize what is observably identical" move as the ONE bounded open union.
    // The common (already-flat) path returns the list unchanged, so tag indices are untouched.
    private IReadOnlyList<CufetType> FlatCases(IReadOnlyList<CufetType> cases)
    {
        if (!cases.Any(c => c is UnionType { Cases: not null })) return cases;
        var flat = new List<CufetType>();
        void Add(IReadOnlyList<CufetType> cs)
        {
            foreach (var c in cs)
                if (c is UnionType { Cases: { } inner }) Add(inner);
                else if (!flat.Any(f => TypeSig(f) == TypeSig(c))) flat.Add(c);
        }
        Add(cases);
        return flat;
    }

    // Ensures a `cfn_N` closure-value struct exists for this signature (and, recursively, for any
    // nested record/series shapes in its params/return). Returns the C struct name.
    private string RegisterFuncStruct(FunctionType ft)
    {
        string sig = TypeSig(ft);
        if (_funcStructSig2Name.TryGetValue(sig, out var name)) return name;
        name = $"cfn_{_funcStructCounter++}";
        _funcStructSig2Name[sig] = name;
        _funcStructs.Add((name, ft));
        foreach (var p in ft.ParameterTypes) RegisterNestedRecords(p);
        if (ft.ReturnType != null) RegisterNestedRecords(ft.ReturnType);
        return name;
    }

    // The C signature of a closure's function pointer: ret (*)(void* env, params…). The leading
    // void* env is the captured environment (NULL until CL.2); a named-function value uses a thunk
    // that ignores it, so the real function keeps its plain signature and direct calls stay direct.
    private string FuncPtrType(FunctionType ft)
    {
        string ret = ft.ReturnType == null ? "void" : EmitCType(ft.ReturnType);
        var ps = new[] { "void*" }.Concat(ft.ParameterTypes.Select(EmitCType));
        return $"{ret} (*)({string.Join(", ", ps)})";
    }

    // The value-struct for each distinct FunctionType: `typedef struct { ret (*fn)(void* env, …); void* env; } cfn_N;`.
    // Uniform two-pointer struct → fits a fixed FunctionType slot and copies by value (sharing the env).
    private void EmitClosureStructs(StringBuilder sb)
    {
        if (_funcStructs.Count == 0) return;
        sb.AppendLine("// ── Closure values ({fn, env}; one per signature) ──");
        // Topo order: a cfn whose param/return is ANOTHER cfn (higher-order-of-higher-order) needs the
        // inner declared first (it's passed/returned by value). Records/series params are already
        // complete/forward-declared above, so only cfn→cfn edges matter.
        var byName = _funcStructs.ToDictionary(s => s.Name, s => s.Type);
        var emitted = new HashSet<string>();
        var order = new List<string>();
        void Visit(string name)
        {
            if (!emitted.Add(name)) return;
            var ft = byName[name];
            foreach (var dep in ft.ParameterTypes.Concat(ft.ReturnType is { } r ? new[] { r } : Array.Empty<CufetType>()))
                if (dep is FunctionType dft && _funcStructSig2Name.TryGetValue(TypeSig(dft), out var depName) && byName.ContainsKey(depName))
                    Visit(depName);
            order.Add(name);
        }
        foreach (var (name, _) in _funcStructs) Visit(name);

        foreach (var name in order)
        {
            var ft = byName[name];
            string ret = ft.ReturnType == null ? "void" : EmitCType(ft.ReturnType);
            var ps = new[] { "void* env" }.Concat(ft.ParameterTypes.Select((p, i) => $"{EmitCType(p)} p{i}"));
            sb.AppendLine($"typedef struct {{ {ret} (*fn)({string.Join(", ", ps)}); void* env; }} {name};");
        }
        sb.AppendLine();
    }

    // A named function used as a VALUE is wrapped in a thunk taking (ignored) env + the params, so the
    // real function keeps its plain signature (direct calls unchanged). One thunk per such function.
    private void EmitFnThunks(StringBuilder sb)
    {
        if (_fnThunks.Count == 0) return;
        sb.AppendLine("// ── Named-function value thunks (ignore env; forward to the real function) ──");
        foreach (var fnName in _fnThunks)
        {
            var ft = _funcTypes[fnName];
            string ret = ft.ReturnType == null ? "void" : EmitCType(ft.ReturnType);
            var decls = new[] { "void* env" }.Concat(ft.ParameterTypes.Select((p, i) => $"{EmitCType(p)} p{i}"));
            var call = string.Join(", ", ft.ParameterTypes.Select((_, i) => $"p{i}"));
            string retKw = ft.ReturnType == null ? "" : "return ";
            sb.AppendLine($"static {ret} {FnThunkName(fnName)}({string.Join(", ", decls)}) {{ (void)env; {retKw}{MangleName(fnName)}({call}); }}");
        }
        sb.AppendLine();
    }

    private static string FnThunkName(string fnName) => "cv_" + fnName.Replace('-', '_') + "__fnthunk";

    // Ensures a struct exists for this record shape (and, recursively, for any nested
    // record shapes in its fields). Returns the C struct name.
    private string RegisterRecordStruct(RecordType rt)
    {
        string sig = TypeSig(rt);
        if (_recordSig2Name.TryGetValue(sig, out var name)) return name;

        name = $"cr_{_recordCounter++}";
        _recordSig2Name[sig] = name;
        _recordStructs.Add((name, rt));
        foreach (var t in rt.PositionalTypes) RegisterNestedRecords(t);
        foreach (var (_, t) in rt.NamedFields)  RegisterNestedRecords(t);
        return name;
    }

    private void RegisterNestedRecords(CufetType t)
    {
        switch (t)
        {
            case RecordType rt:   RegisterRecordStruct(rt); break;
            case SeriesType st:   RegisterSeriesStruct(st); break;
            case VoidableType vt: RegisterVoidableStruct(vt); break;
            case MapType mt:      RegisterMapStruct(mt); break;
            case FailureType ft:  RegisterFailableStruct(ft); break;
            // A union nested in a record/object field (CAT.3). Open unions need no registration —
            // the ONE `cun_open` is emitted from the ProgramUsesOpenUnion gate, not per-site.
            case UnionType ut when ut.Cases != null: RegisterUnionStruct(ut); break;
            // ★ An OBJECT's own field types. A record recurses into its fields (above); an object
            // did not, so `series of holder` registered the outer series and never the
            // `series of number` inside holder.
            //
            // ⚠ It hid because the DISCOVERY pass usually registers the inner series anyway — the
            // body touches it. A field the program never reads is declared in the struct and
            // registered nowhere, and by the time struct emission asks for its C type the list it
            // would join is already being written. Measured, with no generics involved:
            // `Define object holder with (the series of number items).` used as `a series of
            // holder` emitted a struct referencing an undeclared `cser_1`.
            case ObjectType ot: RegisterObjectFields(ot); break;
        }
    }

    /// <summary>Registers the structs an object's own fields need.</summary>
    /// <remarks>
    /// ⚠ The visited set is what makes this terminate. An object may hold itself — a `node` with a
    /// `series of node` — and registration is idempotent by name, so seeing it once is enough.
    /// </remarks>
    private void RegisterObjectFields(ObjectType ot)
    {
        if (!_objectFieldsDone.Add(ot.Name)) return;
        if (!_objectDefs.TryGetValue(ot.Name, out var def)) return;
        foreach (var t in def.PositionalTypes) RegisterNestedRecords(t);
        foreach (var (_, t) in def.NamedFields) RegisterNestedRecords(t);
    }

    private readonly HashSet<string> _objectFieldsDone = new(StringComparer.Ordinal);

    // Ensures a series container struct exists for `series of T` (and T's nested structs).
    // Returns the C struct name. Series is a reference type (arena pointer, shared on assign).
    private string RegisterSeriesStruct(SeriesType st)
    {
        string sig = TypeSig(st);
        if (_seriesSig2Name.TryGetValue(sig, out var name)) return name;
        name = $"cser_{_seriesCounter++}";
        _seriesSig2Name[sig] = name;
        _seriesStructs.Add((name, st.ElementType));
        RegisterNestedRecords(st.ElementType);
        return name;
    }

    // The C series-struct name for a series-typed expression (used to pick the per-type ops).
    private string SeriesStructOf(IExpression seriesExpr) =>
        TypeOf(seriesExpr) is SeriesType st
            ? RegisterSeriesStruct(st)
            : throw new CompilerException("series operation on a non-series value.");

    // Ensures a tagged struct exists for `<inner> or failure` (and the inner's nested structs).
    private string RegisterFailableStruct(FailureType ft)
    {
        string sig = TypeSig(ft);
        if (_failableSig2Name.TryGetValue(sig, out var name)) return name;
        name = $"cfl_{_failableCounter++}";
        _failableSig2Name[sig] = name;
        _failableStructs.Add((name, ft.Inner));
        RegisterNestedRecords(ft.Inner);
        return name;
    }

    // Ensures a map container struct exists for `map from K to V` (and the K/V nested structs,
    // plus the voidable-V struct that lookups return). Returns the C struct name.
    private string RegisterMapStruct(MapType mt)
    {
        string sig = TypeSig(mt);
        if (_mapSig2Name.TryGetValue(sig, out var name)) return name;
        name = $"cmap_{_mapCounter++}";
        _mapSig2Name[sig] = name;
        _mapStructs.Add((name, mt.KeyType, mt.ValueType));
        RegisterNestedRecords(mt.KeyType);
        RegisterNestedRecords(mt.ValueType);
        RegisterVoidableStruct(new VoidableType(mt.ValueType));   // lookup returns voidable V
        return name;
    }

    // Ensures a tagged struct exists for `voidable <inner>` (and its inner's nested structs).
    // Ensures a `cun_N` tagged struct exists for this CLOSED union (and its case types). Open unions
    // (`a catalogue with (…)` — UnionType.Open) are CAT.2 (bounded whole-program tag set) → clean throw.
    private string RegisterUnionStruct(UnionType ut)
    {
        // ★ A BACKSTOP, not a limitation — and the message must not pretend otherwise.
        //
        // It used to read "open catalogues … are not yet supported by the compiler; use a closed
        // catalogue … Open unions are the CAT.2 slice." Two things wrong with that. Open
        // catalogues ARE supported — they compile through the bounded whole-program tag set, and
        // `a catalogue with (1, "two", 3)` builds and runs today — so the message described a
        // shipped feature as missing and would have sent a reader rewriting working code. And
        // "the CAT.2 slice" is this project's internal numbering, which names nothing a user can
        // look up.
        //
        // Reaching here means a caller routed an OPEN union into the CLOSED-union struct builder
        // instead of the open-union path. That is a defect in this compiler, so it says so, in the
        // same terms a rejected gcc build does.
        if (ut.Cases == null)
            throw new CompilerException(
                "★ This is a bug in the Cufet compiler, not in your program.\n\n"
              + "An open union reached the closed-union struct builder. Open catalogues are "
              + "supported — they use a separate whole-program tag set — so nothing in your "
              + "program needs changing.\n\n"
              + "Please report it with the program that produced it.");
        string sig = TypeSig(ut);
        if (_unionSig2Name.TryGetValue(sig, out var name)) return name;
        name = $"cun_{_unionCounter++}";
        _unionSig2Name[sig] = name;
        // Store the FLATTENED union — the payload slots, _write/_eq arms and every tag index are
        // computed from it, so a nested spelling and its flat equivalent share one struct.
        var flat = new UnionType(FlatCases(ut.Cases));
        _unionStructs.Add((name, flat));
        foreach (var c in flat.Cases!) RegisterNestedRecords(c);
        return name;
    }

    // The 0-based case index of `t` within a closed union (the widening site). Compares by TypeSig —
    // the compiler's canonical type identity — rather than CufetType.Equals, which isn't structural
    // for every node kind.
    private int UnionCaseIndex(UnionType ut, CufetType? t)
    {
        if (t == null) return -1;
        string sig = TypeSig(t);
        var cases = UnionCases(ut);
        for (int i = 0; i < cases.Count; i++) if (TypeSig(cases[i]) == sig) return i;
        return -1;
    }

    private string RegisterVoidableStruct(VoidableType vt)
    {
        string sig = TypeSig(vt);
        if (_voidableSig2Name.TryGetValue(sig, out var name)) return name;
        name = $"cvd_{_voidableCounter++}";
        _voidableSig2Name[sig] = name;
        _voidableStructs.Add((name, vt.Inner));
        RegisterNestedRecords(vt.Inner);
        return name;
    }

    private IEnumerable<string> NestedRecordDeps(RecordType rt)
    {
        foreach (var t in rt.PositionalTypes)
            if (t is RecordType nrt) yield return RegisterRecordStruct(nrt);
        foreach (var (_, t) in rt.NamedFields)
            if (t is RecordType nrt) yield return RegisterRecordStruct(nrt);
    }

    // One uniform C field spec: CField is the C member name (p0.. positional, cv_x named);
    // Label is the original field name for named fields (printed "name: value"), null for
    // positionals (printed as just the value).
    private readonly record struct FieldSpec(string CField, string? Label, CufetType Type);

    private List<FieldSpec> RecordFields(RecordType rt)
    {
        var f = new List<FieldSpec>();
        for (int i = 0; i < rt.PositionalTypes.Count; i++) f.Add(new($"p{i}", null, rt.PositionalTypes[i]));
        foreach (var (n, t) in rt.NamedFields) f.Add(new(MangleName(n), n, t));
        return f;
    }

    private List<FieldSpec> ObjectFields(ObjectDefinition def)
    {
        var f = new List<FieldSpec>();
        for (int i = 0; i < def.PositionalTypes.Count; i++) f.Add(new($"p{i}", null, def.PositionalTypes[i]));
        foreach (var (n, t) in def.NamedFields.OrderBy(x => x.FieldName, StringComparer.Ordinal)) f.Add(new(MangleName(n), n, t));
        // Embedding: the embedded object is stored as a bare value-struct field (name = the
        // embedded type). Printed without a "name:" label, appended last — matching FormatObject.
        if (def.EmbeddedTypeName != null)
            f.Add(new(MangleName(def.EmbeddedTypeName), null, ObjType(def.EmbeddedTypeName)));
        return f;
    }

    // Emits all synthesized value structs — records, objects, and voidable tagged structs —
    // in dependency order (a struct's nested value-struct fields / a voidable's inner type
    // must be declared before it; the graph is a DAG), each with `_write` and `_eq` helpers.
    // Refuses an object that transitively contains ITSELF by value. Records, objects, voidables,
    // failables and unions all lower to C structs holding their contents INLINE, so such a type
    // has no finite size — `struct node { struct { int has; struct node val; } next; }` cannot be
    // laid out, and the emitted C fails at gcc with "unknown type name" and a cascade after it.
    //
    // This is a REFUSAL rather than a fix because the fix is a real feature: recursive data needs
    // indirection, and adding it means deciding what a pointer-backed field does to the value
    // semantics every other field has. Refusing keeps the promise that matters — the compiler
    // says no in Cufet's own words instead of letting gcc say it in C's.
    //
    // Going THROUGH a container is fine and is the supported way to build a tree: a series, map or
    // matrix field is an arena POINTER, so the struct closes. `the series of node children` works
    // on both backends today.
    private void GuardNotSelfReferential(string objName, CufetType t, HashSet<string> visiting)
    {
        switch (t)
        {
            case ObjectType ot:
                if (ot.Name == objName)
                    throw new CompilerException(
                        $"'{objName}' contains itself directly, and a value of it would have no fixed size. " +
                        $"A field holding a '{objName}' — or a 'voidable {objName}', a record containing one, " +
                        $"or a catalogue case — stores it inline, so the type would never end. " +
                        $"Hold the nested values in a container instead: `the series of {objName} children` " +
                        $"works, because a series is a reference. A recursive shape needs that indirection.");
                if (!visiting.Add(ot.Name)) return;                 // already on this path — no cycle of our own
                if (_objectDefs.TryGetValue(ot.Name, out var od))
                    foreach (var f in ObjectFields(od)) GuardNotSelfReferential(objName, f.Type, visiting);
                visiting.Remove(ot.Name);
                break;
            // Every arm below stores its contents INLINE, so a self-reference through one is fatal.
            case RecordType rt:   foreach (var f in RecordFields(rt)) GuardNotSelfReferential(objName, f.Type, visiting); break;
            case VoidableType vt: GuardNotSelfReferential(objName, vt.Inner, visiting); break;
            case FailureType ft:  GuardNotSelfReferential(objName, ft.Inner, visiting); break;
            case UnionType ut:    foreach (var c in UnionCases(ut)) GuardNotSelfReferential(objName, c, visiting); break;
            // Series / map / matrix / channel / function are pointers — recursion through them is
            // exactly what makes a tree expressible, so stop descending here.
        }
    }

    private void EmitStructs(StringBuilder sb)
    {
        foreach (var def in _objectDefs.Values)
            foreach (var f in ObjectFields(def))
                GuardNotSelfReferential(def.Name, f.Type, new HashSet<string> { def.Name });

        var specs = new Dictionary<string, (List<FieldSpec> Fields, string WritePrefix)>();
        foreach (var (name, rt) in _recordStructs) specs[name] = (RecordFields(rt), "record");
        foreach (var def in _objectDefs.Values)    specs[ObjStructName(def.Name)] = (ObjectFields(def), def.Name);
        var voidables = new Dictionary<string, CufetType>();
        foreach (var (name, inner) in _voidableStructs) voidables[name] = inner;
        // Failable tagged structs (cfl_N): like voidables but 4-field, and no write/eq
        // (a `T or failure` value is never printed or compared — it's consumed at the call site).
        var failables = new Dictionary<string, CufetType>();
        foreach (var (name, inner) in _failableStructs) failables[name] = inner;
        // Closed-union tagged structs (cun_N): the N-case generalization of a voidable.
        var unions = new Dictionary<string, UnionType>();
        foreach (var (name, ut) in _unionStructs) unions[name] = ut;
        // The ONE open union: its case set was DISCOVERED (bounded whole-program), not declared.
        bool openEmpty = _usesOpenUnion && _openUnionCases.Count == 0;
        if (_usesOpenUnion && !openEmpty) unions[OpenUnionStruct] = new UnionType(_openUnionCases);
        // ★ Closure structs are sorted HERE, with everything else, rather than in a phase of their
        // own afterwards. They used to come after objects, which meant an object holding a closure
        // referenced `cfn_0` before it existed — gcc: "unknown type name 'cfn_0'". The dependency
        // genuinely runs both ways (a closure's parameter may be a record; a record's field may be a
        // closure), so two fixed phases cannot express it and one topological order must. A forward
        // declaration is not an option: a by-value struct member needs a complete type.
        var funcs = new Dictionary<string, FunctionType>();
        foreach (var (name, ft) in _funcStructs) funcs[name] = ft;
        if (specs.Count == 0 && voidables.Count == 0 && failables.Count == 0 && unions.Count == 0
            && funcs.Count == 0 && !openEmpty) return;

        string? DepName(CufetType t) => t switch
        {
            RecordType rt   => RegisterRecordStruct(rt),
            ObjectType ot   => ObjStructName(ot.Name),
            VoidableType vt => RegisterVoidableStruct(vt),
            FailureType ft  => RegisterFailableStruct(ft),
            UnionType ut when ut.Cases != null => RegisterUnionStruct(ut),
            // Looked up rather than registered: registering here would append to _funcStructs while
            // `funcs` is being walked, and everything reachable was already registered when the
            // bodies were emitted.
            FunctionType ft when _funcStructSig2Name.TryGetValue(TypeSig(ft), out var fn) => fn,
            _ => null
        };
        bool Known(string? d) => d != null && (specs.ContainsKey(d) || voidables.ContainsKey(d)
                                            || failables.ContainsKey(d) || unions.ContainsKey(d)
                                            || funcs.ContainsKey(d));

        var emitted = new HashSet<string>();
        var order   = new List<string>();
        void Visit(string cname)
        {
            if (!emitted.Add(cname)) return;
            if (voidables.TryGetValue(cname, out var vInner))       { if (Known(DepName(vInner))) Visit(DepName(vInner)!); }
            else if (failables.TryGetValue(cname, out var fInner))  { if (Known(DepName(fInner))) Visit(DepName(fInner)!); }
            else if (unions.TryGetValue(cname, out var uT))         { foreach (var c in uT.Cases!) if (Known(DepName(c))) Visit(DepName(c)!); }
            // A closure's parameters and return travel by value in its function pointer, so every
            // shape they mention has to be complete first — including another closure.
            else if (funcs.TryGetValue(cname, out var fnT))
            {
                foreach (var p in fnT.ParameterTypes) if (Known(DepName(p))) Visit(DepName(p)!);
                if (fnT.ReturnType is { } rt2 && Known(DepName(rt2))) Visit(DepName(rt2)!);
            }
            else
                foreach (var fs in specs[cname].Fields)
                {
                    var d = DepName(fs.Type);
                    if (Known(d)) Visit(d!);
                }
            order.Add(cname);
        }
        foreach (var cname in specs.Keys.Concat(voidables.Keys).Concat(failables.Keys)
                                        .Concat(unions.Keys).Concat(funcs.Keys).ToList()) Visit(cname);

        sb.AppendLine("// ── Record / object / voidable shapes (value structs) ──");
        if (openEmpty)
        {
            // An open catalogue that never receives a value (`Define items as a catalogue.`): a
            // tag-only struct — nothing can ever be widened in, so there is no payload to hold.
            sb.AppendLine($"typedef struct {{ int tag; }} {OpenUnionStruct};");
            sb.AppendLine($"static void {OpenUnionStruct}_write({OpenUnionStruct} v) {{ (void)v; }}");
            sb.AppendLine($"static int {OpenUnionStruct}_eq({OpenUnionStruct} a, {OpenUnionStruct} b) {{ return a.tag == b.tag; }}");
        }
        foreach (var cname in order)
        {
            if (voidables.TryGetValue(cname, out var inner))
            {
                sb.AppendLine($"typedef struct {{ int has; {EmitCType(inner)} val; }} {cname};");
                continue;
            }
            if (failables.TryGetValue(cname, out var fInner))
            {
                sb.AppendLine($"typedef struct {{ int is_failure; {EmitCType(fInner)} val; const char* message; const char* category; }} {cname};");
                continue;
            }
            if (unions.TryGetValue(cname, out var uDef))
            {
                var payload = string.Join(" ", uDef.Cases!.Select((c, k) => $"{EmitCType(c)} c{k};"));
                sb.AppendLine($"typedef struct {{ int tag; union {{ {payload} }} val; }} {cname};");
                continue;
            }
            if (funcs.TryGetValue(cname, out var fnDef))
            {
                // Uniform two pointers — fits a fixed slot and copies by value, sharing the env.
                string fnRet = fnDef.ReturnType == null ? "void" : EmitCType(fnDef.ReturnType);
                var fnPs = new[] { "void* env" }
                    .Concat(fnDef.ParameterTypes.Select((p, i) => $"{EmitCType(p)} p{i}"));
                sb.AppendLine($"typedef struct {{ {fnRet} (*fn)({string.Join(", ", fnPs)}); void* env; }} {cname};");
                continue;
            }
            sb.AppendLine("typedef struct {");
            foreach (var fs in specs[cname].Fields)
                sb.AppendLine($"    {EmitCType(fs.Type)} {fs.CField};");
            sb.AppendLine($"}} {cname};");
        }
        sb.AppendLine();

        foreach (var cname in order)
        {
            if (failables.ContainsKey(cname)) continue;   // fallible values are never printed
            // A closure has no write/eq of its own: printing one is refused, and comparing two is
            // pointer equality emitted inline by EqCall. It only shares the ORDERING pass above.
            if (funcs.ContainsKey(cname)) continue;
            if (voidables.TryGetValue(cname, out var inner))
            {
                sb.AppendLine($"static void {cname}_write({cname} v) {{ if (v.has) {WriteCall("v.val", inner)}; else printf(\"void\"); }}");
                continue;
            }
            if (unions.TryGetValue(cname, out var uW))
            {
                // A union value prints as its underlying value (the interpreter stores the raw value).
                var arms = uW.Cases!.Select((c, k) => $"if (v.tag == {k}) {{ {WriteCall($"v.val.c{k}", c)}; return; }}");
                sb.AppendLine($"static void {cname}_write({cname} v) {{ {string.Join(" ", arms)} }}");
                continue;
            }
            var (fields, prefix) = specs[cname];
            sb.AppendLine($"static void {cname}_write({cname} v) {{");
            sb.AppendLine($"    printf(\"{prefix}(\");");
            bool first = true;
            foreach (var fs in fields)
            {
                if (!first) sb.AppendLine("    printf(\", \");");
                first = false;
                if (fs.Label != null) sb.AppendLine($"    printf(\"{fs.Label}: \");");
                sb.AppendLine($"    {WriteCall($"v.{fs.CField}", fs.Type)};");
            }
            sb.AppendLine("    printf(\")\");");
            sb.AppendLine("}");
        }
        sb.AppendLine();

        // Value equality (records: structural; objects: nominal same-type; voidables: both-void
        // equal, both-present compare inner) — matching the interpreter's ValuesEqual.
        foreach (var cname in order)
        {
            if (failables.ContainsKey(cname)) continue;   // fallible values are never compared
            if (funcs.ContainsKey(cname)) continue;       // closures compare inline — see EqCall
            if (voidables.TryGetValue(cname, out var inner))
            {
                sb.AppendLine($"static int {cname}_eq({cname} a, {cname} b) {{ if (a.has != b.has) return 0; if (!a.has) return 1; return {EqCall("a.val", "b.val", inner)}; }}");
                continue;
            }
            if (unions.TryGetValue(cname, out var uE))
            {
                // Different cases are different types ⇒ never equal (matches the interpreter comparing
                // the underlying values); same case ⇒ compare that case's payload.
                var arms = uE.Cases!.Select((c, k) => $"if (a.tag == {k}) return {EqCall($"a.val.c{k}", $"b.val.c{k}", c)};");
                sb.AppendLine($"static int {cname}_eq({cname} a, {cname} b) {{ if (a.tag != b.tag) return 0; {string.Join(" ", arms)} return 1; }}");
                continue;
            }
            var fields = specs[cname].Fields;
            string cond = fields.Count == 0 ? "1"
                : string.Join(" && ", fields.Select(fs => EqCall($"a.{fs.CField}", $"b.{fs.CField}", fs.Type)));
            sb.AppendLine($"static int {cname}_eq({cname} a, {cname} b) {{ return {cond}; }}");
        }
        sb.AppendLine();
    }

    // Forward-declares each series container (`typedef struct cser_N_s cser_N;`) so record/object
    // value structs (and other series/maps) can hold a `cser_N*` field before its full definition.
    private void EmitSeriesForwardDecls(StringBuilder sb)
    {
        if (_seriesStructs.Count == 0) return;
        foreach (var (name, _) in _seriesStructs) sb.AppendLine($"typedef struct {name}_s {name};");
        // `_write` AND `_eq` are forward-declared so a struct/voidable/map whose field or element is
        // a series can call them from its own `_write`/`_eq` (emitted in EmitStructs, before the
        // series runtime's full definitions). Unlike maps (pointer equality), series equality is a
        // real element-wise function call, so `_eq` needs the forward declaration too.
        foreach (var (name, _) in _seriesStructs) sb.AppendLine($"static void {name}_write({name}* s);");
        foreach (var (name, _) in _seriesStructs) sb.AppendLine($"static int {name}_eq({name}* a, {name}* b);");
        sb.AppendLine();
    }

    // Emits each series container's full struct + helpers: an arena-allocated growable array of T
    // (T stored by value — a value struct copies into the slot, a reference type stores its pointer,
    // matching the interpreter). By-value ops (remove-by-value, equality) use the element's own
    // equality. Generalizes the former number-only CufetSeries to every element type.
    private void EmitSeriesRuntime(StringBuilder sb)
    {
        if (_seriesStructs.Count == 0) return;
        sb.AppendLine("// ── Series containers (arena growable arrays; per element type) ──");
        foreach (var (name, elem) in _seriesStructs)
        {
            string ec = EmitCType(elem);
            sb.AppendLine($"struct {name}_s {{ {ec}* data; int len; int cap; }};");
            sb.AppendLine($"static {name}* {name}_new(void) {{ {name}* s = ({name}*)cufet_arena_alloc(sizeof({name})); s->data = NULL; s->len = 0; s->cap = 0; return s; }}");
            sb.AppendLine($"static void {name}_ensure({name}* s) {{");
            sb.AppendLine($"    if (s->len >= s->cap) {{");
            sb.AppendLine($"        int nc = s->cap == 0 ? 4 : s->cap * 2;");
            sb.AppendLine($"        {ec}* nd = ({ec}*)cufet_arena_alloc((size_t)nc * sizeof({ec}));");
            sb.AppendLine($"        if (s->len > 0) memcpy(nd, s->data, (size_t)s->len * sizeof({ec}));");
            sb.AppendLine($"        s->data = nd; s->cap = nc;");
            sb.AppendLine($"    }}");
            sb.AppendLine($"}}");
            sb.AppendLine($"static void {name}_append({name}* s, {ec} v) {{ {name}_ensure(s); s->data[s->len++] = v; }}");
            sb.AppendLine($"static void {name}_prepend({name}* s, {ec} v) {{ {name}_ensure(s); if (s->len > 0) memmove(s->data + 1, s->data, (size_t)s->len * sizeof({ec})); s->data[0] = v; s->len++; }}");
            // after1: the correct 0-based insertion index (1-based position it inserts after).
            sb.AppendLine($"static void {name}_insert({name}* s, int after1, {ec} v) {{ {name}_ensure(s); int pos = after1; if (s->len > pos) memmove(s->data + pos + 1, s->data + pos, (size_t)(s->len - pos) * sizeof({ec})); s->data[pos] = v; s->len++; }}");
            // idx1: 1-based; pass -1 for "last".
            sb.AppendLine($"static void {name}_remove_at({name}* s, int idx1) {{ int idx = (idx1 < 0) ? s->len - 1 : idx1 - 1; if (s->len - idx - 1 > 0) memmove(s->data + idx, s->data + idx + 1, (size_t)(s->len - idx - 1) * sizeof({ec})); s->len--; }}");
            sb.AppendLine($"static void {name}_remove_value({name}* s, {ec} v) {{ for (int i = 0; i < s->len; i++) {{ if ({EqCall("s->data[i]", "v", elem)}) {{ if (s->len - i - 1 > 0) memmove(s->data + i, s->data + i + 1, (size_t)(s->len - i - 1) * sizeof({ec})); s->len--; return; }} }} }}");
            // Writes as (e1, e2, ...) — no trailing newline, so it nests inside record/object fields.
            sb.AppendLine($"static void {name}_write({name}* s) {{");
            sb.AppendLine($"    printf(\"(\");");
            sb.AppendLine($"    for (int i = 0; i < s->len; i++) {{ if (i > 0) printf(\", \"); {WriteCall("s->data[i]", elem)}; }}");
            sb.AppendLine($"    printf(\")\");");
            sb.AppendLine($"}}");
            // Element-wise, in-order value equality (series are ordered sequences — no canonicalization).
            sb.AppendLine($"static int {name}_eq({name}* a, {name}* b) {{ if (a->len != b->len) return 0; for (int i = 0; i < a->len; i++) if (!({EqCall("a->data[i]", "b->data[i]", elem)})) return 0; return 1; }}");
        }
        sb.AppendLine();
    }

    // Forward-declares each map container (`typedef struct cmap_N_s cmap_N;`) so record/object
    // value structs can hold a `cmap_N*` field before the full definition appears below.
    private void EmitMapForwardDecls(StringBuilder sb)
    {
        if (_mapStructs.Count == 0) return;
        foreach (var (name, _, _) in _mapStructs) sb.AppendLine($"typedef struct {name}_s {name};");
        // `_write` is forward-declared: a record/object/voidable whose field is a map calls it
        // from its own `_write`, which is emitted before the map runtime's full definitions.
        foreach (var (name, _, _) in _mapStructs) sb.AppendLine($"static void {name}_write({name}* m);");
        sb.AppendLine();
    }

    // Emits each map container's full struct + helpers: an arena-allocated association list
    // (parallel key/value arrays) with linear scan. Keys compared by value; get returns voidable V.
    private void EmitMapRuntime(StringBuilder sb)
    {
        if (_mapStructs.Count == 0) return;
        sb.AppendLine("// ── Map containers (arena association lists; linear scan; keys by value) ──");
        foreach (var (name, k, v) in _mapStructs)
        {
            string kc = EmitCType(k), vc = EmitCType(v);
            // LOOKUP FLATTEN (voidable-valued maps): `the entry for k` on a `map from K to voidable V`
            // yields `voidable V` — NOT voidable-voidable. Absent key → void; present → the stored
            // voidable as-is. So the lookup return struct IS the value struct when V is voidable.
            bool voidableV = v is VoidableType;
            string cvd = voidableV ? vc : RegisterVoidableStruct(new VoidableType(v));
            sb.AppendLine($"struct {name}_s {{ {kc}* keys; {vc}* vals; int len; int cap; }};");
            sb.AppendLine($"static {name}* {name}_new(void) {{ {name}* m = ({name}*)cufet_arena_alloc(sizeof({name})); m->keys = NULL; m->vals = NULL; m->len = 0; m->cap = 0; return m; }}");
            sb.AppendLine($"static void {name}_ensure({name}* m) {{");
            sb.AppendLine($"    if (m->len >= m->cap) {{");
            sb.AppendLine($"        int nc = m->cap == 0 ? 4 : m->cap * 2;");
            sb.AppendLine($"        {kc}* nk = ({kc}*)cufet_arena_alloc((size_t)nc * sizeof({kc}));");
            sb.AppendLine($"        {vc}* nv = ({vc}*)cufet_arena_alloc((size_t)nc * sizeof({vc}));");
            sb.AppendLine($"        if (m->len > 0) {{ memcpy(nk, m->keys, (size_t)m->len * sizeof({kc})); memcpy(nv, m->vals, (size_t)m->len * sizeof({vc})); }}");
            sb.AppendLine($"        m->keys = nk; m->vals = nv; m->cap = nc;");
            sb.AppendLine($"    }}");
            sb.AppendLine($"}}");
            sb.AppendLine($"static int {name}_index({name}* m, {kc} k) {{ for (int i = 0; i < m->len; i++) if ({EqCall("m->keys[i]", "k", k)}) return i; return -1; }}");
            sb.AppendLine($"static void {name}_put({name}* m, {kc} k, {vc} v) {{ int i = {name}_index(m, k); if (i >= 0) {{ m->vals[i] = v; return; }} {name}_ensure(m); m->keys[m->len] = k; m->vals[m->len] = v; m->len++; }}");
            if (voidableV)
                sb.AppendLine($"static {cvd} {name}_get({name}* m, {kc} k) {{ {cvd} r = {{0}}; int i = {name}_index(m, k); if (i >= 0) r = m->vals[i]; return r; }}");
            else
                sb.AppendLine($"static {cvd} {name}_get({name}* m, {kc} k) {{ {cvd} r = {{0}}; int i = {name}_index(m, k); if (i >= 0) {{ r.has = 1; r.val = m->vals[i]; }} return r; }}");
            sb.AppendLine($"static int {name}_has({name}* m, {kc} k) {{ return {name}_index(m, k) >= 0; }}");
            // `has an entry` ≠ `has a key` for voidable values: an explicit stored void counts as a
            // key but NOT an entry (matches the interpreter's EvaluateMapHasEntry is-not-VoidValue).
            if (voidableV)
                sb.AppendLine($"static int {name}_has_entry({name}* m, {kc} k) {{ int i = {name}_index(m, k); return i >= 0 && m->vals[i].has; }}");
            else
                sb.AppendLine($"static int {name}_has_entry({name}* m, {kc} k) {{ return {name}_index(m, k) >= 0; }}");
            sb.AppendLine($"static void {name}_write({name}* m) {{");
            sb.AppendLine($"    printf(\"map {{\");");
            sb.AppendLine($"    for (int i = 0; i < m->len; i++) {{");
            sb.AppendLine($"        if (i > 0) printf(\", \");");
            sb.AppendLine($"        {WriteCall("m->keys[i]", k)}; printf(\": \"); {WriteCall("m->vals[i]", v)};");
            sb.AppendLine($"    }}");
            sb.AppendLine($"    printf(\"}}\");");
            sb.AppendLine($"}}");
        }
        sb.AppendLine();
    }

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
    private static string FormatTypeName(CufetType t) => t switch
    {
        NumberType => "number", TextType => "text", FactType => "fact", BitsType => "bits",
        SeriesType => "series", MapType => "map", RecordType => "record", MatrixType => "matrix",
        StashType s     => $"stash of {FormatTypeName(s.ElementType)}",
        ObjectType o    => o.Name,
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
        AxiomType { ReturnType: { } gives } ar => $"{ar.Language} {FormatTypeName(gives)} axiom",
        AxiomType a     => $"{a.Language} axiom",
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

    // A +/−/× whose operands are both matrices — routed through the FALLIBLE machinery (dimension
    // mismatch is a Cufet failure the typechecker requires handling for).
    private bool IsMatrixOp(BinaryExpression b) =>
        b.Op is TokenType.Plus or TokenType.Minus or TokenType.Star
        && TypeOf(b.Left) is MatrixType && TypeOf(b.Right) is MatrixType;

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
        RabbitType => $"cufet_rabbit_eq({a}, {b})",
        // Two addresses are the same when they are the same pointer — the only question anyone can
        // ask about one without reading through it. The interpreter's ForeignAddress is a record
        // over the same bits, so both sides compare identically.
        AddressType => $"({a} == {b})",
        FunctionType => $"(({a}).fn == ({b}).fn && ({a}).env == ({b}).env)",   // function values: reference equality
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
        FunctionType => $"printf(\"<function>\")",   // matches the interpreter's Format for a FunctionValue
        // ★ Never the pointer itself — see the interpreter's Format. Two backends are two
        // processes, so a printed handle could not agree between them however correct both were.
        AddressType => $"printf(\"<address>\")",
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
                string printStmt = t switch
                {
                    NumberType    => $"cufet_print_number({valExpr})",
                    BitsType      => $"cufet_print_bits({valExpr})",
                    FactType      => $"cufet_print_fact({valExpr})",
                    TextType      => $"cufet_print_text({valExpr})",
                    SeriesType st => $"{RegisterSeriesStruct(st)}_write({valExpr}); cufet_nl()",
                    RecordType rt   => $"{RegisterRecordStruct(rt)}_write({valExpr}); cufet_nl()",
                    ObjectType ot   => $"{ObjStructName(ot.Name)}_write({valExpr}); cufet_nl()",
                    VoidableType vt => $"{RegisterVoidableStruct(vt)}_write({valExpr}); cufet_nl()",
                    MapType mt      => $"{RegisterMapStruct(mt)}_write({valExpr}); cufet_nl()",
                    MatrixType      => $"cufet_mat_write({valExpr}); cufet_nl()",
                    RabbitType      => $"cufet_rabbit_write({valExpr}); cufet_nl()",
                    // ⚠ Routed through WriteCall so the two places that print an address cannot
                    // drift apart. They already had: the first arm added here made the interpreter
                    // and the compiler agree everywhere EXCEPT a bare `State`, which is its own
                    // switch — and only the oracle noticed.
                    AddressType     => $"{WriteCall(valExpr, t)}; cufet_nl()",
                    // A union prints as its underlying value (tag dispatch) — the same _write the
                    // synthesized container helpers call, so a bare `State <union>` matches an
                    // element printed inside a catalogue.
                    UnionType       => $"{WriteCall(valExpr, t)}; cufet_nl()",
                    _ => throw new CompilerException($"State of a '{FormatTypeName(t)}' is not yet supported by the compiler.")
                };
                // Locked for the whole statement: a State is several writes, and a concurrent State
                // on another thread used to splice itself between them. See cufet_out_lock.
                sb.AppendLine($"{indent}cufet_out_lock(); {printStmt}; cufet_out_unlock();");
                break;
            }

            // An axiom declaration stores nothing. Foreign source has no runtime representation
            // here: the text is pasted into this file's C when a return runs it, and the name
            // exists so that return has something to say. See EmitAxiomCall.
            case DefineStatement { Value: AxiomLiteral }:
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
            var savedExcT = _varTypes.TryGetValue("the exception", out var pex) ? pex : null;
            // Marked at handler ENTRY — no statement of the handler has been emitted yet, so these
            // are the depths `Suppress` has to unwind back down to. The snapshot arrives from the
            // scoped block itself, which is the only place it exists.
            _currentExcHandler = (sup, doneL, HereCleanup());
            _currentExcVar = xmsg;
            _varTypes["the exception"] = TExcMarker;
            EmitScopedBlock(sb, trySt.ExceptionHandler!, inner,
                snap => _currentExcHandler = (sup, doneL, HereCleanup(snap)));
            _currentExcHandler = savedExcH;
            _currentExcVar = savedExcV;
            if (savedExcT != null) _varTypes["the exception"] = savedExcT; else _varTypes.Remove("the exception");
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
    private string EmitAxiomCall(AxiomLiteral axiom, IReadOnlyList<IExpression>? args, int line)
    {
        string language = axiom.Language ?? "foreign";
        string key = ForeignC.Identity(language, axiom.Source, axiom.Parameters);
        var result = axiom.ReturnType!;
        if (!_axiomFnNames.TryGetValue(key, out var fnName))
        {
            // ★★ The wrapper text comes from ForeignC, so this is byte-for-byte the function the
            // interpreter's shim compiles. Splicing, the guard and the call all happen once, in
            // one place, for both backends.
            fnName = ForeignC.FunctionName(language, axiom.Source, axiom.Parameters);
            _axiomFnNames[key] = fnName;
            _axiomFns.Append(ForeignC.Wrapper(fnName, "static ", language, axiom.Source,
                                              axiom.Parameters, result));
        }

        // ★ Marshalled per parameter, from the CUFET type the checker put on the declaration —
        // the writer names no C type anywhere, and this is the only place one is chosen.
        var marshalled = axiom.Parameters
            .Select((p, i) => EmitForeignArgument(p.Type, args![i], line))
            .ToList();
        string call = $"{fnName}({string.Join(", ", marshalled)})";

        return result switch
        {
            NumberType => $"cufet_dec_from_foreign({call})",
            FactType   => call,                       // already the 1/0 a Cufet fact is in C
            VoidableType { Inner: NumberType } => EmitForeignReal(call),
            VoidableType { Inner: AddressType } => EmitForeignAddress(call),
            _          => EmitForeignText(call),      // voidable text — copied out of C's memory
        };
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
    private string EmitForeignAddress(string call)
    {
        string cvd = RegisterVoidableStruct(new VoidableType(AddressType.Instance));
        int id = _freshId++;
        _preEmits.Add($"void* cf_fa{id} = {call};");
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
    private string EmitForeignArgument(CufetType type, IExpression arg, int line) => type switch
    {
        NumberType => $"cufet_foreign_ll({EmitExpr(arg)}, {line})",
        FactType   => $"({EmitExpr(arg)} ? 1 : 0)",
        TextType   => EmitExpr(arg),
        // Straight back the way it came, with nothing to check: Cufet never made this value, so
        // the only thing it can be is a pointer C handed over.
        AddressType => EmitExpr(arg),
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
        AxiomLiteral axiom    => new AxiomType(axiom.Language ?? "", axiom.ReturnType),
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
        BinaryExpression b    => IsMatrixOp(b) ? MatrixType.Instance
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
        MatrixSized           => MatrixType.Instance,
        MatrixAccess          => TNumber,
        VariableReference vr  => vr.Name == "input" ? new ReadableStreamType(TText)   // `the input` = stdin
                               : _narrowedVars.TryGetValue(vr.Name, out var nt) ? nt.Type
                               : _closureSelf is { } cs && vr.Name == cs.Name ? cs.Type   // recursive self-reference
                               : _varTypes.TryGetValue(vr.Name, out var t) ? t
                               : _funcTypes.TryGetValue(vr.Name, out var ftv) ? ftv   // a bare named function used as a value
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
                             && OverloadReturnType(ov.OperandTypeName, b.Op) is FailureType oft => oft,
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
            var elems = new List<string> { $"(char*){pg}" };
            foreach (var arg in stages[s].Args) elems.Add($"(char*){EmitExpr(arg)}");
            elems.Add("(char*)0");
            _preEmits.Add($"char* cf_av{id}_{s}[] = {{ {string.Join(", ", elems)} }};");
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

        string CapCType(string c) =>
            Bridged(c) ? "void*" : _varTypes[c] is TaskHandleType ? "cufet_rbox*" : EmitCType(_varTypes[c]);

        // Arg struct + thread function (accumulated; emitted before the bodies). cf_selfbox is where
        // a named task publishes its own result; it is unused by fire-and-forget tasks.
        _taskFns.AppendLine($"struct cufet_targ{tid} {{ cufet_rbox* cf_selfbox; {string.Join(" ", caps.Select(c => $"{CapCType(c)} {MangleName(c)};"))} }};");
        _taskFns.AppendLine($"static void* cufet_task{tid}(void* argp) {{");
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
            && OverloadReturnType(oovl.OperandTypeName, ob.Op) is FailureType ooft)
            return (RegisterFailableStruct(ooft),
                    $"{OverloadFnName(oovl.OperandTypeName, ob.Op)}({EmitExpr(ob.Left)}, {EmitExpr(ob.Right)})");
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
        var ft = (FunctionType)TypeOf(funcExpr);
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
            var ort = OverloadReturnType(ovl.OperandTypeName, b.Op);
            string call = $"{OverloadFnName(ovl.OperandTypeName, b.Op)}({EmitExpr(b.Left)}, {EmitExpr(b.Right)})";
            return ort is FailureType oft
                ? EmitFallibleCheckGoto(call, RegisterFailableStruct(oft))
                : call;
        }

        // Matrix arithmetic is FALLIBLE (dimension mismatch → failure): a bare matrix op routes
        // through the standard fallible machinery — check-goto in a Try, exactly like a fallible call.
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
        RecordType rt => RegisterRecordStruct(rt),
        ObjectType ot => ObjStructName(ot.Name),
        VoidableType vt => RegisterVoidableStruct(vt),
        MapType mt => RegisterMapStruct(mt) + "*",   // maps are arena pointers (reference type)
        FailureType ft => RegisterFailableStruct(ft),
        FailureMarkerType => "CufetFailure",         // a caught / bare failure (message + category)
        ReadableStreamType or WritableStreamType => "FILE*",   // a stream is an open FILE* (or stdin)
        RabbitType => "cufet_rabbit",                          // a rabbit is its name, as in the interpreter
        ChannelType => "cufet_chan*",                          // a channel is a shared mutex/condvar queue
        MatrixType => MatrixCType(),                           // a matrix is an arena pointer (reference type)
        FunctionType ft => RegisterFuncStruct(ft),             // a function value is a {fn, env} value struct
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
