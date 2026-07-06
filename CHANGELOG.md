# Changelog

All notable changes to Cufet are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning: feature arcs bump the minor version; 1.0.0 marks language stability.

---

## [Unreleased]

### Added

**Comments — `[[ ... ]]`**
- `[[` opens a comment; the first `]]` closes it. Everything between is stripped by
  the lexer before tokenisation — dots, keywords, newlines, and any Cufet syntax inside
  are all ignored. Single delimiters cover both single-line and multi-line comments.
- Non-nesting: the first `]]` always closes, regardless of any `[[` inside the
  comment. Consistent with standard lexer comment semantics.
- Unterminated comment (`[[` with no `]]` before EOF) is a `LexerException` naming
  the opening line.
- `[` and `]` are otherwise completely unused in Cufet's surface syntax — zero
  collision risk with any existing construct.
- 12 new lexer tests + 7 new interpreter tests (1356 total).

**Compiler — Slice 1 (C backend, `cufet build`)**
- The compiler era begins. `cufet build <file.cufe>` compiles a Cufet source file to
  a native binary via a C intermediate. The front-end (Lexer → Parser → TypeChecker)
  is reused unchanged; `Cufet.Compiler` is a new library that consumes the same AST.
- Slice 1 scope: `State <integer arithmetic>.` — `+`, `-`, `*`, `/`, unary negation,
  parentheses. Everything else throws a clear "not yet implemented in this slice" error.
- `CodeGenerator` emits a C source file with a `cufet_print_number(double)` helper
  that matches the interpreter's `Format(decimal)` output for integer arithmetic
  (integers print without a decimal point; `double` is used as the C numeric type —
  a known approximation, noted and deferred to a later numeric-representation slice).
- `GccInvoker` probes known installation paths before falling back to PATH; captures
  `stderr` and throws `CompilerException` with the full gcc error on failure; deletes
  the `.c` intermediate after a successful compile.
- **Slice 1 validation bar met:** `State 1 + 1.` → gcc → native binary → prints `2`.
  Oracle test: compiled output matches interpreter output for the same source — the
  test pattern for the entire compiler era.
- 10 new compiler tests in `Cufet.Compiler.Tests/PipelineTests.cs` (1366 total).

**Compiler — Slice 2 (variables: `Define` / `becomes` / `VariableReference`)**
- Number variables compile to C locals. `Define x as 5.` → `double cv_x = 5.0;`.
  `x becomes x + 1.` → `cv_x = (cv_x + 1.0);`. Variables are readable in any expression.
- Permanent variables (`… permanently.`) emit as `const double` — the TypeChecker already
  enforces no reassignment, so the C compiler gets the same invariant for free.
- **Name mangling:** `cv_` prefix + hyphens replaced by underscores. `grand-total` →
  `cv_grand_total`. The `cv_` prefix guards against C keyword collisions (`double`,
  `int`, etc.). Cufet identifiers never contain underscores, so the substitution is
  collision-free within the language.
- Oracle test pattern continues: compiled output must match interpreter output for every
  test input, including hyphenated names and self-referencing reassignment.
- 10 new compiler tests (1376 total).

**Compiler — Slice 3 (control flow: `If` / `While` / `For each` over ranges)**
- `IfStatement` → `if (cond) { … } else if (cond) { … } else { … }`. Arbitrary arm
  count; optional else; block-scoped bodies match the interpreter's per-arm scoping.
- `WhileStatement` → `while (cond) { … }`.
- `RepeatUntilStatement` → `do { … } while (!(cond));`.
- `StopStatement` / `SkipStatement` → `break;` / `continue;`. Both propagate correctly
  through nested if-inside-loop structures.
- `ForEachStatement` over a `RangeExpression` → a direction variable computed at runtime
  from start and end, then a single `for` loop (no body duplication). Range semantics:
  inclusive both bounds; ascending when start ≤ end, descending otherwise; step is a
  positive magnitude. Matches the interpreter's range materialisation exactly.
- All comparison and logical operators: `==`, `!=`, `<`, `>`, `<=`, `>=`, `&&`, `||`,
  unary `!`. Modulo (`%`) emits `fmod()` from `<math.h>` — C's `%` is integers-only;
  `fmod` has matching sign-of-dividend semantics.
- String literals in `State` emit via a new `cufet_print_text(const char*)` runtime
  helper, enabling FizzBuzz's mixed number/string output. String variables and string
  expressions other than literal `State` arguments remain deferred.
- **FizzBuzz validation bar met:** the README's flagship example compiles to a native
  binary and its output matches the interpreter's exactly.
- 16 new compiler tests (1392 total).

**Compiler — Slice 5A (arena allocator + series: `Pull a rabbit` / `Done.` / series)**
- The first reference type through the compiler. `Pull a rabbit.` maps to
  `cufet_arena_push()`; `Done.` maps to `cufet_arena_pop()`. Nested
  `Pull … Done.` blocks nest arenas correctly via a global depth stack
  (`CufetArena cufet_arenas[64]`, `int cufet_arena_top`).
- Arena design: **tracked-pointer list** (not a bump buffer). Each
  `cufet_arena_alloc(size)` calls `malloc()` and registers the pointer in the
  arena's `void** ptrs` array. `cufet_arena_pop()` frees all tracked pointers
  then the array itself. `free(NULL)` is a C no-op, so empty arenas are harmless.
- Global arena wraps all of `main()` — `cufet_arena_push()`/`cufet_arena_pop()`
  emitted unconditionally. Scalar-only programs pay zero observable cost (empty
  arena, no allocations). Top-level series are legal at depth 0.
- `CufetSeries { double* data; int len; int cap }`. Series creation, append,
  prepend, 1-based insert, remove-at (-1 = last), remove-value (first match),
  and print are all implemented. Growth: new larger buffer via `cufet_arena_alloc`,
  old buffer stays in the arena's tracked list — no UAF (old buffer never accessed
  after copy), no leak (both freed at pop). ASan confirms this pattern.
- `cufet_format_number` helper extracted from `cufet_print_number`; shared by
  `cufet_print_number` (adds `\n`) and the new `cufet_print_series` (formats
  elements as `(e1, e2, …)\n`).
- `ForEachStatement` over a series variable emits a `cf_ser{id}` pointer temp and
  a `cf_i{id}` index loop; the iterator variable is declared inside the loop body.
- `_preEmits` side-channel: `SeriesLiteral` nodes produce multiple C statements
  but `EmitExpr` must return a single expression string. Preparatory statements
  accumulate in `_preEmits`; `FlushPreEmits` emits them before the parent
  statement. Transparent to all existing scalar code.
- `GccInvoker` gains an `extraFlags` overload (`IReadOnlyList<string>`) for
  passing `-fsanitize=address -g` in the ASan test helper.
- **Two validation dimensions:** oracle (compiled output == interpreter output)
  and memory-correctness (ASan: zero leaks, zero use-after-free). ASan test is
  Linux-only (skips on Windows — ASan unreliable with MinGW).
- **Slice 5B seam:** maps, records, and objects attach to the same arena cursor;
  no further infrastructure changes needed.
- 13 new compiler tests (1417 total); slices 1–4 all still pass.

**Compiler — Slice 4 (scalar functions: `Bind` / `Cast` / `return`)**
- Named scalar functions compile to C functions. `Bind number to factorial, given (the number n): … Done.`
  → a C `double cv_factorial(double cv_n) { … }` emitted before `main`.
- `Cast factorial on (n - 1)` → `cv_factorial((cv_n - 1.0))` in any expression position.
  `Cast print-double on (7).` as a statement → `cv_print_double(7.0);`.
- `return <expr>.` → `return <expr>;`. Bare `return.` → `return;`. Void functions
  declare `void` return type in C.
- **Forward declarations first:** all top-level function signatures are emitted as C
  forward declarations before any function body, enabling mutual recursion and
  forward calls with no ordering constraint on the Cufet source.
- **Scalar boundary:** parameters and return types must be `number` (`double`) or
  `fact` (`int`) or void — C scalar pass-by-value, no arena needed.
  Reference-type params/returns (`series`, `text`, maps, objects) throw a clear
  `CompilerException("not yet implemented")` so the compiler defers cleanly;
  TypeChecker accepts these programs normally.
- **Recursive factorial (`cast factorial on (n - 1)`) works:** C self-call via the
  forward declaration. Matches interpreter output for all inputs tested.
- **Mutual recursion** (`is-even` ↔ `is-odd`) works via the forward declaration pass.
- Void function syntax requires the explicit `void` keyword: `Bind void to <name>,
  given (…):` — the bare `Bind to <name>:` form is not valid Cufet grammar.
- 12 new compiler tests (1404 total).

---

## [0.9.0] — 2026-07-03

The complete concurrency core is built, sound, and hardened by five concept cars.
All concept-car findings are resolved. The interpreter-era language is now
complete — native backend is the next era.

### Added

**★ Cooperative concurrency core ★ — the headline**
- **Scheduler (`CufetScheduler`)** — cooperative, C# async/await, custom
  `SynchronizationContext`. All continuations routed to a single per-thread FIFO
  queue; no interpreter-internal data races. Sequential programs run unchanged.
- **Structured tasks** — `Have rabbit start a task [as <name>]: … Done.` Spawn +
  join-at-Done. A task cannot outlive its spawning rabbit. Sound by construction:
  inherits the region model's outward-only invariant — no new soundness machinery.
  `TaskLocalSeriesCannotEscapeToOuterScope` confirms the static error fires from
  inside a task body.
- **Channels** — `a channel of T`; `Send <value> through <channel>.`; `the delivery
  from <channel>` (→ `voidable T`; void on closed-empty channel); `Close <channel>.`
  (idempotent; send-after-close is a runtime error). **Values deep-copied at send**
  — the cross-task aliasing guarantee. Type-checked: wrong type to Send, non-channel
  to Send/delivery/Close are all static errors.
- **Task results** — named tasks may `return <value>.` inside their body; `the
  awaited result of <name>` collects the result. Suspends if the task is running;
  immediate if already done; cached on double-await. Fallible task results
  (`return a failure …`) infer `T or failure`; unhandled fallible result is a static
  error. Void-returning tasks cannot be awaited for a result (static error).
- **`Yield.` + SIGINT-at-yield** — `Yield.` is a cooperative scheduler yield and
  interrupt checkpoint. The scheduler drain loop checks `_interruptRequested` at
  each dequeue; blocked receives and awaits also wake on interrupt. Programs that
  yield naturally are interruptible without polling. Renames `an interrupt has been
  requested` → `an interrupt is requested` (old form is a parse error).

**Streaming task pipes**
- **Task pipes** — `producer | consumer.` pipelines two or more `void`-returning
  functions. `output <value>.` (contextual keyword inside a stage) emits to the
  implicit output channel; `for each <name> from the input, repeat:` reads from it.
  Producer runs to completion, then consumer drains. Stage references may be
  variables holding function values.
- **Subprocess pipe enhancement** — `run "a" | run "b"` in expression position
  returns a result record (`output`, `errors`, `exit-code`). Exit code is the
  rightmost non-zero stage. Launch failure is a catchable Cufet failure; non-zero
  exit is observable but not auto-fatal.

**Map key value-type constraint**
- Map keys must be value types (`text`, `number`, or `fact`). Declaring a map with
  a reference-type key (object, series, map) is a static `TypeException` with an
  educational message explaining why reference identity breaks under deep-copy
  (lookups silently always-miss; the map behaves empty, computing wrong answers with
  no error). Runtime guard in `ExecuteMapSet` as a safety net for any dynamic path
  that reaches runtime. Root cause of the Dijkstra silent-wrong-answer bug.

**Trap-cleanup sweep**
- `true` / `false` — fact literals; `return true.` and `if flag is true` now work
  without defining `true`/`false` as variables.
- Ordinals (`first`, `second`, …, `tenth`, `last`) are now contextual identifiers —
  recognized as positional accessors only in `the <ordinal> of <series>` shape;
  valid as variable names, parameter names, and field names everywhere else.
- Negated word-comparisons — `is not greater than`, `is not less than`,
  `is not N or more`, `is not N or less` are valid in both condition and expression
  position.
- Comparison unification — symbol forms (`= < > <= >=`) and word forms (`is`, `is
  greater than`, etc.) work in **both** condition and expression position. The
  positional restriction is retired; word forms remain idiomatic in conditions.
- `=` in a stand-alone statement now produces an educational error ("did you mean
  `becomes`?") rather than a confusing parse failure.
- Top-level data referenced inside a top-level function now produces an educational
  error naming the variable and explaining the scoping rule.

**Series literal in expression position**
- `a series of number with (1, 2, 3)` is now valid in expression position (as a
  function argument, in `but void is (…)`, etc.). Found during the channel-deepcopy
  testing; wired into `ParseCorePrimary`.

### Changed

- Test count: 1187 interpreter + 140 lexer (1327 total). New tests live in dedicated
  files: `SchedulerTests`, `TaskSpawnTests`, `ChannelTests`, `TaskResultTests`,
  `YieldTests`, `PipeTests`, `ComparisonUnificationTests`, `BooleanLiteralTests`,
  `OrdinalIdentifierTests`, `NegatedWordComparisonTests`, `EqualSignStatementErrorTests`,
  `TopLevelDataScopeErrorTests`.
- ROADMAP.md: concurrency + pipes moved from Planned to What's built; concurrency arc
  narrative added to Design decisions; forward roadmap updated (native backend is next);
  Known minor issues concurrency/SIGINT sections updated; test count and table updated.
- README.md / REFERENCE.md: bumped to 0.9.0.
- `examples/dijkstra.cufe`: complete rewrite using text node names as map keys
  (object-as-key design incompatible with map key value-type constraint; procedural
  rewrite also cleaner). Verifies expected distances and prints `PASS`.

### Test campaign (five test, every finding resolved)

| Program | Finding | Resolution |
|---|---|---|
| `parallelsum` | Top-level function can't read top-level `Define` data | Educational runtime error explaining the scoping rule |
| `channel-deepcopy` | Deep-copy safety validated under nested structures | ✅ guarantee earned; also found series-literal-in-expression gap → wired into `ParseCorePrimary` |
| `subprocess-pipes` | stderr silently discarded; exit codes silently ignored | `errors` field added to result record (F2); result record with `exit-code` (F1) = command substitution |
| `work-queue` | Coordination correctness validated; fan-out distribution imbalanced under cooperative scheduler | ✅ correctness confirmed; fan-out imbalance is a named interpreter-era characteristic → "verify at native" note |
| `dijkstra` | Silent object-as-map-key miss (reference identity lost under deep-copy) | Map keys constrained to value types (Option C — educational type error) |

The recurring signal: every finding was a gap, ergonomic wart, or interpreter-era
characteristic — never a core soundness or correctness bug. The foundations held;
the programs sanded edges.

---

## [0.8.0] — 2026-06-28

The deferred-features ledger is cleared and the region model is sound. This
release closes all six items from the gap-list, completes the
three-hole adversarial soundness arc, and ships matrix arithmetic as the first
exercise of operator overloading.

### Added

**Eagér type resolution — ObjectType placeholder leak killed**
- Parser-created `ObjectType` shells (used in annotations) no longer leak into
  type inference. `ResolveParamType` is now fully recursive (recurses into
  `SeriesType`, `VoidableType`, `FailureType`, `MapType`, `FunctionType`,
  `RecordType`, etc.). `Pass2ResolveTypes` eagerly resolves all type references
  inside `_objectDefs` after hoisting, so no placeholder survives to inference
  time. `InferType` wraps its result through `ResolveParamType` as a final
  backstop. Book-introduced types (`matrix` after `Pull collections`) resolved
  correctly in all positions.

**`is more than` educational error**
- Using `is more than` in a condition (instead of `is greater than`) now
  produces a targeted compile-time diagnostic explaining the correct keyword,
  rather than a confusing parse error.

**Series operations unified to IExpression**
- All five series AST nodes (`SeriesLength`, `SeriesAdd`, `SeriesRemoveAt`,
  `SeriesRemoveValue`, `SeriesSet`) now hold `IExpression Series` instead of
  `string SeriesName`. Series operations now work directly on possessive
  expressions (`one's cards`), eliminating the alias-preamble pattern inside
  object methods. Parser uses `ParseCorePrimary()` for the series target
  (not `ParsePostfix()`, which would have greedily consumed postfix operators).
  TypeChecker throws a static error for `Add x to (a+b)` and similar
  non-series targets.

**Parser keyword-allowlist (Approach C)**
- `IsNamedAccessPattern()` lookahead exclusion list replaced by a principled
  set-based check: any token that is not `Identifier`, `Category`, or `Key` is
  excluded as a field name. Two narrow exceptions: `Category` (for `the category
  of the failure`) and `Key` (for `the key of mapping`). No new keyword can ever
  mis-fire as a field name — the n-queens `the series of number board` class of
  bug is dead. Approach B (explicit type-annotation contexts, the proper
  architectural fix) is tracked as deferred pre-native parser-hardening.

**`chance` book — effectful randomness**
- `Pull a book on chance.` enables: `a random number from low to high` (whole
  numbers; `low > high` runtime error), `a random item from series` (→ `voidable T`;
  empty → void), `randomly shuffled series` (non-mutating Fisher-Yates copy),
  `a random guess` (50/50 fact). `Seed the chance with N.` reseeds for
  reproducibility. Per-interpreter RNG for free test isolation. `chance` is
  intentionally separate from `math` — effectful vs. pure is a named structural
  distinction. Dedicated AST nodes (not book-function dispatch) give access to
  the interpreter's `_rng` instance field.

**`Pull … Done.` unification**
- Books, rabbits, and other acquired resources share a unified `Pull <thing>:
  … Done.` scoped-block syntax. `Pull a book on X.` (dot) remains the
  scope-local form. Plural form: `Pull books on X, Y, and Z.` pulls multiple
  books in one statement. `Pull` scope is hoisted correctly for nested
  declarations.

**Value-vs-reference `Define` semantics documented**
- The principled split (records/objects: value-typed, copy on assignment;
  series/maps: reference-typed, share the live instance) is now explicitly
  documented in GRAMMAR.md and REFERENCE.md, including the `Define alias as
  original.` vs. `Define copy as original.` disambiguation.

**Region-model soundness — three-hole adversarial arc**
- Three holes in the outward-only invariant were found adversarially and closed.
  See Design decisions in ROADMAP.md for the full narrative. In brief:
  - *Hole #1 (function-call depth laundering):* `ReturnDepthSignature` on
    `FunctionType`, computed by `ComputeReturnDepthSignature` at `CheckBind`
    time; `ValueDepthOf` reads the signature and takes `max(subset)` of
    argument depths.
  - *Hole #3 (methods/getters residue of #1):* same machinery extended to
    method/getter bodies with receiver as a depth source (`ReceiverDepthIndex =
    -1`); `_possessiveDepthCache` / `_rnaDepthCache` populated from
    `InferPossessiveAccess` / `InferRecordNamedAccess`.
  - *Hole #2 (capture-store laundering):* `TypeInfo.IsParameter` flag set at
    all parameter-registration sites; nested-scope import upgrades any captured
    `IsParameter && IsReferenceType` to `RabbitDepth = int.MaxValue`; existing
    `CheckRegionStore` rejects the outward store with no new logic.
  - No known remaining pre-native soundness gaps.

**Matrix arithmetic (+, -, *) — collections-book operator overloads**
- `m + n` — element-wise addition; identical dimensions required; `matrix or
  failure` (category `"dimension-mismatch"`).
- `m - n` — element-wise subtraction; identical dimensions required; `matrix or
  failure`.
- `m * n` — matrix product (standard triple-loop dot product); requires
  `left.columns == right.rows`; yields `m×p` from `m×n * n×p`; `matrix or
  failure`.
- All three are strictly fallible: must be inside `Try to:` or `but on failure
  <default>`, else a static `TypeException`. Scope-locality enforced by type:
  `MatrixType` only in scope inside `Pull a book on collections.`.
- `matrix / matrix` falls through to "arithmetic requires numbers" type error
  (matrix inversion deferred; will be a named `collections` function if added).

### Changed

- Test count: 1003 interpreter + 140 lexer (1143 total).
- ROADMAP.md: operator overloading and matrix arithmetic moved from Planned to
  What's built; chance and Pull mechanism documented in What's built; soundness
  arc added to Design decisions; forward roadmap updated.
- README.md / REFERENCE.md: bumped to 0.8.0.
- GRAMMAR.md §5: matrix arithmetic section added (operators, dimension rules,
  fallibility, strict-fallible examples, not-defined cases).

---

## [0.7.0] — 2026-06-23

### Added

- **Operator overloading** — user-defined `+`, `-`, `*`, etc. for object types;
  fallible overloads (`T or failure`) supported; strict-fallible rule enforced
  at call sites.
- **Books / `Pull` mechanism** — `Pull a book on <name>.` scope-local import;
  `BookType` with possessive member access; bundled books registered statically.
- **`math` book** — `absolute value`, `square root`, `floor`, `ceiling`, `round`,
  `log`, `power`, `sine`, `cosine`, `tangent`, `pi`, `e`; partial functions return
  `voidable number`.
- **`collections` book — `matrix` type** — literal `a matrix with ((r1), (r2), …)`;
  `the item at (row, column) of m` (1-based); `a matrix with N by M`; `the rows of` /
  `the columns of`; type annotation `the matrix m`. Op-set (arithmetic) deferred to
  0.8.0.
- **Possessive chaining and multi-word book member names** — `math's absolute value
  of x`; `of (e1, e2)` multi-arg form.
- **Parser restructure** — `ParseCorePrimary` / `ParsePostfix` / `ParseNegation`
  split to fix postfix-eating in recursive target-of parses.
- **`_typeScopes` parallel scope chain** — enables type-introducing books;
  `RegisterScopedType` / `TryLookupScopedType` helpers; always in sync with `_scopes`.
- **Destructors / RAII** — `Bind unmaking a <type> to <name>:` fires in LIFO order
  at scope exit; infallible; one destructor per type.
- **Named constructors** — `Bind making a <type> to <name>[, given (…)]:` and
  fallible form `Bind making a <type> or failure to <name>:`; called via `Cast`.
- **Getters and setters** — `Get <name> as <type>:` / `Set <name> given (…):`
  inside object bodies; uniform access property; `unto` forms for outside-body
  declaration; setter self-write bypass.
- Numerous example programs (n-queens, Tower of Hanoi, Dijkstra, card dealing,
  word frequency, arbtree).

---

## [0.6.0] — 2026-06-21

### Added

- **Union types and narrowing** — `(A or B or C)` closed unions; `is a <type>` /
  `is not a <type>` runtime type tests; in-branch narrowing; narrowing by elimination
  in `Otherwise`; open unions (`catalogue`, `atlas`); `catalogue` (heterogeneous
  series) and `atlas` (heterogeneous map).
- **Object interfaces / polymorphism** — `Define <name> as an interface for {…}`;
  explicit conformance (`and <interface>` on object definition); static conformance
  check; interface type as parameter type.
- **`unto` methods** — methods declared outside the object body; hoisted /
  order-independent; identical in every way to nested methods.
- **String interpolation** — `{expr}` inside string literals; lexer-side split;
  desugars to `joined to` / `converted to text` chain; `\{`/`\}` for literal braces.
- **`or pass the failure off`** — failure propagation operator; propagates to the
  caller (which must itself return a failable type).
- **`In case of exception`** — runtime exception handler; `the exception` binding;
  `Suppress.` to swallow; default re-raise.
- **Embedding / composition** — `and as a <type>` on object definition; transitive
  member promotion; flat construction; embed-handle escape hatch; collision → error.
- **Cooperative SIGINT** — `an interrupt has been requested` / `Acknowledge the
  interrupt.`; per-signal flag; not preemptive (preemption deferred to concurrency arc).
- **Directory traversal** — `the contents of the directory path`; `the path "x"
  exists` / `is a directory` / `is a file`.
- **Environment variables** — `the environment variable "NAME"` → `voidable text`.
- **`permanently` constants** — trailing adverb; shallow; static enforcement only.

---

## [0.5.0] — 2026-06-20

### Added

- **File I/O** — `read all from the file <path>`, `read all lines from the file
  <path>`, `write … to the file <path>.`, `append … to the file <path>.`; failure
  categories `"not-found"`, `"permission-denied"`, `"disk-error"`.
- **File streams** — `With the file <path> open for reading/writing as <name>:
  … Done.`; RAII scoped; direction statically enforced.
- **Process execution** — `run <program>` / `run <program> with arguments (…)`;
  result record (`output`, `errors`, `exit-code`); `result or failure`; no shell
  injection.
- **Standard input** — `read a line from the input` (→ `voidable text`), `read all
  from the input`, `read all lines from the input`; `the input` pre-defined.
- **Voidable type + narrowing** — `void`, `voidable T`; `is void` / `is not void`;
  variable-level narrowing; `but void is <default>` inline fallback; `VoidableType`
  in the type system.
- **`failure T` and `Try to:` / `In case of failure:`** — failable values; inline
  `but on failure <default>`; block form with both handlers; `a failure "message" of
  category "tag"` literal; strict-fallible enforcement.
- **Text → number conversion** — `converted to number` → `voidable number`; always
  failable by type.
- **Text operations** — `split by`, `contains`, `the position of … in …` (→
  `voidable number`), substring (`the characters from N to M of`, `the first/last N
  characters of`, `to the end of`); `replace <old> with <new> in <text>`;
  `in uppercase` / `in lowercase`; `trimmed`.
- **String escape sequences** — `\n` `\t` `\r` `\\` `\"` `\{` `\}`.
- **Range stepping** — `range 1 to 10 counting by 2`; positive magnitude; direction
  from start/end; endpoint included only if step lands exactly.
- **`Define a shadow x`** — deliberate shadowing opt-in.
- **Closures and lambdas** — `a function given (…): … Done` anonymous function
  expressions; inferred return type; same capture rule as closures.
- **Records** — `a record with (…)`; positional + named fields; structural typing;
  value semantics (deep copy); record shapes in annotations; empty `series of records
  like (…)`.

---

## [Pre-0.5.0]

The core language was established in versions 0.1.0–0.4.x:
- **0.1.x** — `Define`/`becomes`, arithmetic, `State`, conditionals (`If`/`Otherwise`),
  `While`, `For each`, `Stop.`/`Skip.`; lexical scope; `Done.`-bounded blocks.
- **0.2.x** — Series (homogeneous, mutable, `Add`/`Remove`, ordinal access, `sorted`);
  maps (`a map from T to V`, `the entry for K in M`, `has a key for`); ranges.
- **0.3.x** — Functions (`Bind`); recursion + depth limit; first-class function values;
  function types in annotations.
- **0.4.x** — Objects (nominal typing, `Define object`, `a new T {…}`, methods, `Cast`,
  possessive access, value semantics); `joined to` / `converted to text` / `the length
  of`; maps fully rounded out; voidable-valued maps.
