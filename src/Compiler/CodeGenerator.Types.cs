using System.Text;
using Cufet.Interpreter;
using Cufet.Lexer;

namespace Cufet.Compiler;

/// <summary>The C types a program needs SYNTHESISED</summary>
/// <remarks>
/// <para>The C types a program needs SYNTHESISED — interfaces monomorphized, operator overloads resolved, and a struct per distinct record, series, map and failable shape.</para>
/// <para>
/// ★ One class across several files, carved along the boundaries the generator already drew
/// for itself — these were its own section banners, not lines chosen by whoever split it. The
/// state they all share (the struct registries, the arena depth, the pre-emit buffer) stays in
/// <c>CodeGenerator.cs</c>, because it is what the halves talk to each other through.
/// </para>
/// </remarks>
public sealed partial class CodeGenerator
{
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
        [TypeChecker.BookInterface]   = new InterfaceDefinition(TypeChecker.BookInterface, [], 0, 0),
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

    // The decimal↔double bridge for `math`'s three remaining native members (sqrt/log/power).
    //

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
        // ⚠ A WHOLE-PROGRAM question, answered before a single block is emitted, because
        // `UsesUnmakers` decides whether every block gets a snapshot and a run-to at its exit.
        // Discovering it partway through would leave the blocks above it without either.
        // ★ Namespace-keyed, like every other walk here — see the AST-walk rule.
        _usesForeignRelease = false;
        AstSearch.Visit(program.Statements, node =>
        {
            if (node is AxiomLiteral { ReleaseAxiom: not null }) _usesForeignRelease = true;
            // ★ An axiom with NO declared result is SOURCE, not a call — collected whole-program and
            // pasted above every wrapper, because a wrapper may name what it declares. Keyed on the
            // text so the same preamble written twice is emitted once.
            if (node is DefineStatement { Value: AxiomLiteral { ReturnType: null } decl })
                _axiomPreambles[ForeignC.Identity(decl.Language ?? "foreign", decl.Source, decl.Parameters)]
                    = (decl.Language ?? "foreign", decl.Source);
        });

        // ── Runtime: includes + software decimal + print helpers ──────────
        runtime.AppendLine(RuntimePreamble);

        // ⚠ Unconditional, unlike the signal substrate below it: any program at all can recurse
        // past the end of its stack, and what this replaces is a program that vanishes without a
        // word. It costs nothing at run time — nothing is emitted into any function.
        runtime.AppendLine(StackGuardRuntime);

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

        // ⚠⚠ DISCOVERY IS THE REFLECTION WALK, not a hand-written descent. The hand-written one
        // matched `PullStatement` and recursed only into ITS body — its own comment admitted as
        // much — so a `Pull a book` sitting inside a rabbit, a loop, or an `If` arm was never
        // reached at all. Its Binds were then neither hoisted here NOR emitted in place (the pull
        // emitter skips Binds precisely because they are hoisted), so calling one failed with
        // "'<name>' is declared further down this block" about a function declared four lines
        // above the call — while the same program interpreted fine. See CONTRIBUTING on keying a
        // walk to the namespace: this is that bug class, again.
        var allPulls = new List<PullStatement>();
        AstSearch.Visit(program.Statements, n => { if (n is PullStatement p) allPulls.Add(p); });

        foreach (var ps in allPulls)
        {
            // The aliases in force inside this pull: every ENCLOSING pull's books, then its own.
            // Containment is asked of the same walk, so a pull nested behind any construct counts.
            var aliases = new List<(string Local, string Book)>();
            foreach (var outer in allPulls)
                if (!ReferenceEquals(outer, ps)
                    && AstSearch.Contains(outer.Body, n => ReferenceEquals(n, ps)))
                    foreach (var (bookName, localName) in outer.Books)
                        aliases.Add((localName, bookName.ToLowerInvariant()));
            foreach (var (bookName, localName) in ps.Books)
                aliases.Add((localName, bookName.ToLowerInvariant()));

            foreach (var s2 in ps.Body)
                if (s2 is BindStatement pb && pb.UntoType == null) pullBinds.Add((pb, aliases));
        }

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
        //
        // ⭐⭐ Flattened through pull scopes for the `permanently` case, because that is where a
        // shared constant most often is: `Pull a rabbit.` wraps most programs, and a constant
        // declared inside one is a shared constant on both of the other two answers — the checker
        // hoists it and the interpreter shares it. Left as a local of main it had no symbol a
        // hoisted function could reach, so `cast bumped on ()` inside a book pull was refused as a
        // closure capture while the interpreter ran it.
        //
        // ⚠ Only the PERMANENT ones are taken from a nested scope. A function-VALUED binding is
        // hoisted for a different reason (the note above), and whether one inside a pull scope
        // should follow is a question nothing has asked yet — so this changes nothing about it.
        var topLevelStatements = program.Statements.ToHashSet();
        foreach (var topLevelConst in TypeChecker.FlattenHoistable(program.Statements)
                                                 .OfType<DefineStatement>())
        {
            if (!topLevelConst.Permanent && !topLevelStatements.Contains(topLevelConst)) continue;

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
                // ⭐⭐ Object MEMBERS count as known, the same way free functions on the line above
                // do. `cast sum on (the here)` — the free-cast form README teaches — writes the
                // member's name in callee position, where the walk sees an ordinary variable being
                // read. So ANY method call in that spelling, inside a function inside a `Pull`
                // block, was refused as "captures 'sum' from the pull scope": a program the
                // interpreter runs, on a name that is not a variable and cannot be one.
                //
                // ⚠ A DIVERGENCE, and independent of the one CollectRefsDefs now handles — the
                // object here can be declared at the top of the file. The possessive spelling
                // (`the here's sum`) was never affected, because there the member is a bare string
                // the walk cannot see; that is the same asymmetry the free-cast form had for
                // generic methods, and it is why this went unnoticed.
                //
                // ⚠ Best-effort, and this widens it: a captured LOCAL sharing a member's name is
                // now missed, exactly as one sharing a free function's name already was. The trade
                // was already made one line up; what changes here is that correct programs stop
                // being refused.
                // ⚠ SHARED CONSTANTS are known too. One lives at C file scope, so a hoisted
                // function reading it is not closing over anything — it is reading a symbol that
                // outlives every frame. Without this, a `permanently` declared in the same pull
                // block was reported as a capture, which is the fix the checker's own advice for
                // the non-constant case tells a reader to make.
                var known = new HashSet<string>(bind.Parameters.Select(p => p.Name)
                    .Concat(defs).Concat(_funcReturnTypes.Keys).Concat(aliases.Select(a => a.Local))
                    .Concat(_sharedConstTypes.Keys)
                    .Concat(_objectDefs.Values.SelectMany(def =>
                        def.Methods.Select(m => m.Name)
                           .Concat(def.Getters.Select(g => g.Name))
                           .Concat(def.Setters.Select(s => s.Name)))));
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
            // Before anything can recurse. Nothing else in the program is affected by this call:
            // it installs a handler and returns, and no generated function carries a check.
            body.AppendLine("    cufet_watch_stack();");
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
        if (_usesChase) runtime.AppendLine(ChaseRuntime);
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
        // ⚠ Preambles count too. A program whose only foreign source is a resultless axiom emits no
        // wrapper at all, and gating on wrappers alone dropped its headers AND its source.
        if (_axiomFns.Length > 0 || _axiomPreambles.Count > 0)
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
            // ★★ Preambles FIRST, then the wrappers — a wrapper may call a helper a preamble
            // declares, and C needs it declared above the use. This is the whole point of the
            // resultless form: shared source that every axiom in the program can see.
            foreach (var (language, source) in _axiomPreambles.Values)
                sb.Append(ForeignC.Preamble(language, source));
            if (_axiomPreambles.Count > 0) sb.AppendLine();

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

    // ⚠ BOTH operand names are in the C symbol. `vec2 * number` and `number * vec2` are distinct
    // functions, and a name built from one side alone would emit the same symbol for both.
    private static string OverloadFnName(string leftName, string rightName, TokenType op) =>
        $"cop_{leftName.Replace('-', '_')}_{OpWord(op)}_{rightName.Replace('-', '_')}";

    // Walks all statements collecting operator overloads (they are top-level only, but a Pull-book
    // body is morally top-level too — same reach as CollectObjectDefs).
    private void CollectOverloadDefs(IEnumerable<IStatement> stmts)
    {
        foreach (var stmt in stmts)
            switch (stmt)
            {
                case OperatorOverloadDeclaration oad:
                    _overloadDefs[(oad.LeftTypeName, oad.RightTypeName, oad.Operator)] = oad; break;
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
    private CufetType? OverloadReturnType(string leftName, string rightName, TokenType op)
    {
        var key = (leftName, rightName, op);
        if (_overloadReturnTypes.TryGetValue(key, out var cached)) return cached;
        if (!_overloadDefs.TryGetValue(key, out var oad)) return null;
        if (!_overloadInferring.Add(key))
            throw new CompilerException(
                $"the '{leftName} {OpSym(op)} {rightName}' overload is defined in terms of its own result " +
                $"type, so its return type can't be inferred.");
        try
        {
            var saved = new Dictionary<string, CufetType>(_varTypes);
            _varTypes[oad.LeftName]  = OperandType(leftName);
            _varTypes[oad.RightName] = OperandType(rightName);
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
        if (OperandName(TypeOf(b.Left)) is not { } ln || OperandName(TypeOf(b.Right)) is not { } rn)
            return null;
        return _overloadDefs.TryGetValue((ln, rn, b.Op), out var oad) ? oad : null;
    }

    // The VALUE type of an overloaded operator expression: the declared return, with a fallible
    // overload unwrapped to its success type (the raw `T or failure` is seen only by
    // FallibleReturnType — the same unwrap convention as a fallible call). The front-end requires
    // every path of an overload body to return a value, so the return type is never absent.
    private CufetType OverloadValueType(OperatorOverloadDeclaration oad, TokenType op)
    {
        var rt = OverloadReturnType(oad.LeftTypeName, oad.RightTypeName, op);
        return rt is FailureType ft ? ft.Inner : rt ?? TNumber;
    }

    private string OverloadSignature(OperatorOverloadDeclaration oad)
    {
        var rt = OverloadReturnType(oad.LeftTypeName, oad.RightTypeName, oad.Operator);
        string lc = EmitCType(OperandType(oad.LeftTypeName));
        string rc = EmitCType(OperandType(oad.RightTypeName));
        return $"static {(rt == null ? "void" : EmitCType(rt))} "
             + $"{OverloadFnName(oad.LeftTypeName, oad.RightTypeName, oad.Operator)}"
             + $"({lc} {MangleName(oad.LeftName)}, {rc} {MangleName(oad.RightName)})";
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
        _currentReturnType  = OverloadReturnType(oad.LeftTypeName, oad.RightTypeName, oad.Operator);
        _varTypes[oad.LeftName]  = OperandType(oad.LeftTypeName);
        _varTypes[oad.RightName] = OperandType(oad.RightTypeName);

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
        ChaseType  => "CH",   // likewise: one struct, and the type is its own identity
        RabbitType => "RB",
        AddressType => "AD",   // one opaque void* — every foreign pointer is the same type here
        FunctionType fn => "Fn(" + string.Join(",", fn.ParameterTypes.Select(TypeSig)) + "->" +
                           (fn.ReturnType == null ? "v" : TypeSig(fn.ReturnType)) + ")",
        // ★ Distinct from the Fn( … ) above even though the two SHARE a value struct. They are
        // different Cufet types — one is a body, the other is foreign text — and this signature is
        // also a key into `_varTypes`, where conflating them would let an axiom satisfy a function
        // parameter. The struct sharing is decided in EmitCTypeRaw, deliberately and separately.
        AxiomType ax => "Ax:" + ax.Language.ToLowerInvariant() + "(" +
                        string.Join(",", ax.ParameterTypes.Select(TypeSig)) + "->" +
                        (ax.ReturnType == null ? "v" : TypeSig(ax.ReturnType)) + ")",
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

    /// <summary>The struct a value of this type embeds BY VALUE, or null when it embeds none.</summary>
    /// <remarks>
    /// ★ What the struct-emission order is built on: a struct holding another by value needs that
    /// one complete first, and a forward declaration is not an option for a by-value member.
    ///
    /// ⚠⚠ TOTAL, and throwing rather than returning null for anything unaccounted for. It used to
    /// end in `_ => null`, which reads as "no dependency" and is a perfectly plausible answer — so
    /// a type nobody added an arm for got silently ordered as if it depended on nothing. That is
    /// exactly what shipped: an axiom-typed object field emitted its struct above `cfn_0`, and gcc
    /// said "unknown type name". A missing arm must be a FAILURE here, not a default.
    ///
    /// ⚠ Extracted from a local function inside EmitStructs so ExhaustivenessTests can reach it.
    /// A local function cannot be reflected over, which is why this switch was the one per-type
    /// switch the audit could not see.
    ///
    /// ★ Null is a real answer for three groups: scalars carry no struct; a series, a map and a
    /// matrix are ARENA POINTERS, so a field of one is a pointer and C is happy with an incomplete
    /// type behind it; and the live machinery (channels, tasks, streams, rabbits) is either a
    /// pointer or a fixed runtime struct declared with the runtime rather than ordered here.
    /// </remarks>
    private string? DepStructName(CufetType t) => t switch
    {
        RecordType rt   => RegisterRecordStruct(rt),
        ObjectType ot   => ObjStructName(ot.Name),
        VoidableType vt => RegisterVoidableStruct(vt),
        FailureType ft  => RegisterFailableStruct(ft),
        UnionType { Cases: not null } ut => RegisterUnionStruct(ut),
        // ⚠ Looked up rather than REGISTERED: registering here would append to _funcStructs while
        // the caller is walking a snapshot of it, and everything reachable was already registered
        // when the bodies were emitted. An unregistered signature therefore means "not part of this
        // program", which is a genuine null rather than an omission.
        FunctionType ft => _funcStructSig2Name.TryGetValue(TypeSig(ft), out var fn) ? fn : null,
        // ★ An axiom value shares the closure struct, so it has the same dependency and it is just
        // as load-bearing — this is the arm whose absence shipped.
        AxiomType ax => _funcStructSig2Name.TryGetValue(TypeSig(AsFunctionType(ax)), out var axfn) ? axfn : null,

        // No by-value struct to order against. Each of these is a decision, which is the point of
        // listing them rather than letting a fallback answer for them.
        NumberType or BitsType or TextType or FactType or AddressType   // scalars
          or SeriesType or MapType or MatrixType or ChaseType            // arena pointers
          or ChannelType or TaskHandleType                              // shared runtime pointers
          or ReadableStreamType or WritableStreamType or RabbitType     // FILE*, and a region name
          or UnionType                                                  // the ONE open union struct
          or VoidType                                                   // not a value at all
          or StashType                                                  // rewritten to a closure first
          or InterfaceType                                              // monomorphized away
          or BookType or MappingType                                    // checker vocabulary
          or FailureMarkerType or ExceptionMarkerType => null,

        _ => throw new CompilerException(
                 $"the struct-emission order has no entry for a '{FormatTypeName(t)}'. Add one to "
               + "DepStructName: either the struct it embeds by value, or null with the reason it "
               + "embeds none.")
    };

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
            // ★ An axiom field carries a closure struct, and its result may be a voidable that
            // nothing else in the program mentions. Same reasoning as the object arm above: a field
            // the program never reads is declared in the struct and registered nowhere.
            case AxiomType ax: RegisterFuncStruct(AsFunctionType(ax)); break;
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

    /// <summary>
    /// Emits the argv for one `run`, and returns the C variable holding it.
    /// </summary>
    /// <remarks>
    /// ★★ The two forms differ only in whether the LENGTH is known here. A literal list becomes a
    /// C array literal exactly as it always did; a series becomes an arena block sized at run time
    /// and filled by a loop. Both decay to <c>char* const argv[]</c>, so neither
    /// <c>cufet_run_inherit</c> nor <c>cufet_run_capture</c> can tell them apart.
    /// </remarks>
    private string EmitRunArgv(string progVar, IReadOnlyList<IExpression> args,
                               IExpression? argsSeries, string argvVar)
    {
        if (argsSeries == null)
        {
            var elems = new List<string> { $"(char*){progVar}" };
            foreach (var arg in args) elems.Add($"(char*){EmitExpr(arg)}");
            elems.Add("(char*)0");
            _preEmits.Add($"char* {argvVar}[] = {{ {string.Join(", ", elems)} }};");
            return argvVar;
        }

        string ser = RegisterSeriesStruct(new SeriesType(TText));
        string src = EmitExpr(argsSeries);
        // ⚠ The series is read into a temp first: it may be a call, and evaluating it three times
        // (size, loop bound, terminator) would run it three times.
        _preEmits.Add($"{ser}* {argvVar}_s = {src};");
        _preEmits.Add($"char** {argvVar} = (char**)cufet_arena_alloc((size_t)({argvVar}_s->len + 2) * sizeof(char*));");
        _preEmits.Add($"{argvVar}[0] = (char*){progVar};");
        _preEmits.Add($"for (int {argvVar}_i = 0; {argvVar}_i < {argvVar}_s->len; {argvVar}_i++) "
                    + $"{argvVar}[{argvVar}_i + 1] = (char*){argvVar}_s->data[{argvVar}_i];");
        _preEmits.Add($"{argvVar}[{argvVar}_s->len + 1] = (char*)0;");
        return argvVar;
    }

    /// <summary>The C series-struct name for a series-typed expression (used to pick the per-type ops).</summary>
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
        foreach (var def in _objectDefs.Values)    specs[ObjStructName(def.Name)] = (ObjectFields(def), ModuleTypeLifting.DisplayName(def.Name));
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

        string? DepName(CufetType t) => DepStructName(t);
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
}
