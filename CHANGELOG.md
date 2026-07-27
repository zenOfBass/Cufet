# Changelog

All notable changes to Cufet are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning: feature arcs bump the minor version; 1.0.0 marks language stability.

---

## [Unreleased]

### Added

**`bits` — a bit-pattern type (first slice: literals)**

- **`0x` hex, `0b` binary, `0o` octal literals**, with `_` grouping digits. A value prints in
  the base it was written in: `State 0o755.` prints `0o755`. No bare-zero octal — `0755` is
  seven hundred and fifty-five, not 493.

- **`bits` is not `number`, and does not convert to it implicitly.** `0xFF = 255` is a type
  error. A bit pattern is not a quantity: `0o755` is three permission triples, and treating it
  as a decimal is a category error. This is what makes the coming bitwise operators well
  behaved — they will live on `bits` and be absent from `number`, so `not 5` cannot be written
  and cannot surprise anyone with `-6`.

- **★ Leading zeros are significant.** The width comes from the digit count, so `0xF` is 4 bits
  and `0x0F` is 8, and they print differently while comparing equal. This is deliberately
  unlike C, Java, Rust, Go and Python, where the two are identical and width belongs to the
  declared type. It is what will let `not 0xFF` be `0x00` rather than `-6` — the type is
  unsigned and never negative.

- **Ceiling is 64 bits**, which covers every C flag set, file mode and address. Wider is
  cryptography or scientific computing — a different domain, and one for a foreign-function
  boundary rather than for distorting this type.

  Still to come in this arc: the gates (`and`/`or`/`not`/`xor`), arithmetic and integer
  division, shifts, and `converted to hex` / `converted to number`.

**`cufet` is a command now**

- Packaged as a **.NET global tool**, so `cufet myprogram.cufe` replaces
  `dotnet run --project src\App\Cufet.App.csproj -- myprogram.cufe`. The SDK already keeps
  its tools directory on `PATH`, so installing needs no shell configuration and no
  administrator rights, and it behaves identically on Windows, macOS and Linux.

  This is also what makes the editor extension useful outside this repository. It looks for
  a local build first — so your working copy wins while you are developing the compiler —
  and falls back to `cufet` on `PATH`. Without an installed `cufet`, a `.cufe` file opened
  anywhere else had nothing to run and got no diagnostics at all.

  The assembly is still `Cufet.App`, so the development build path is unchanged.

- **`cufet --help` and `cufet --version`.** Anything that is not a recognised verb is treated
  as a filename, so a mistyped verb used to surface as an unhandled `FileNotFoundException`
  and a stack trace. A missing file now reports the problem and points at `--help`, exiting 2.
  Tolerable behaviour for a `dotnet run` incantation; not for a command people type.

**Editor support — syntax highlighting and error squiggles**

- **`editors/vscode/`** — a Visual Studio Code extension. A TextMate grammar for
  highlighting, and a checker that turns the front end's own diagnostics into squiggles.
  No build step and no dependencies: link the directory into your extensions folder and
  reload. Comments nest in the editor the way they nest in the lexer, string interpolation
  highlights the expression inside the hole, and `wordPattern` knows that hyphens are
  identifier characters, so double-clicking `add-edge` selects all of it.

  The grammar assigns *scopes* and never colours — so it fits whatever theme you already
  use. Articles and prepositions (`a`, `the`, `of`, `to`, `as`, `with`, …) are deliberately
  given no scope at all: Cufet reads like English because it is full of them, and colouring
  them would turn every line into a wall of keyword.

  One consequence worth having: because hyphens bind into identifiers, `count - 1` shows
  the `-` as an operator while `count-1` stays one flat name. The sharp edge is now visible
  in the editor instead of waiting to surprise you.

- **`cufet check [--json] [--native] <file>`** — lexes, parses and type-checks a program
  and reports what fails, *without running it*. `Interpret` finds the same errors, but
  finding them by running the program is not an option for an editor when the program reads
  input, writes files, or takes a minute.

  Reports as `path:line: error: message` (which the extension's `$cufet` problem matcher
  parses), or as one JSON object per diagnostic under `--json`, which keeps the multi-line
  body of a type error intact instead of flattening it. `--native` additionally runs code
  generation and reports what the native compiler refuses — as **warnings**, not errors,
  because those programs interpret fine. Exits 0 clean, 1 for problems, 2 if the file could
  not be read.

### Changed

- **Comments nest.** An inner `[[` now opens a nested comment, and the outer one ends only
  at the `]]` that closes it. Previously the first `]]` closed everything, which meant that
  commenting out a block of code containing a comment silently ended the comment early, let
  the rest of the block be read as code, and reported the leftover `]]` as an unexpected
  character — with nothing in the message to suggest a comment was involved. Commenting out
  a block is one of the most common things anyone does while working, and it should not stop
  working just because the block was documented. Nesting is strictly more permissive, so
  every program that worked before still works.

---

## [0.10.0] — 2026-07-25

The **native compiler** era. Cufet now has two backends: the tree-walking interpreter
(the reference implementation) and a compiler that emits C and invokes gcc to produce a
real executable. Every committed grammar feature compiles — verified by a mechanical
sweep for AST nodes with no codegen references.

The interpreter is the **oracle**: the compiler's test suite compiles each program, runs
the binary, and asserts its output equals the interpreter's. Where the two disagreed, one
of them was wrong — that discipline surfaced and closed roughly a dozen latent bugs in the
language itself, several of which predated the compiler entirely.

### Added

**Comments — `[[ ... ]]`**
- `[[` opens a comment; the first `]]` closes it. Everything between is stripped by
  the lexer before tokenisation — dots, keywords, newlines, and any Cufet syntax inside
  are all ignored. Single delimiters cover both single-line and multi-line comments.
- Non-nesting in this release: the first `]]` closed the comment regardless of any
  `[[` inside. (Changed immediately after — see Unreleased.)
- Unterminated comment (`[[` with no `]]` before EOF) is a `LexerException` naming
  the opening line.
- `[` and `]` are otherwise completely unused in Cufet's surface syntax — zero
  collision risk with any existing construct.

**The native compiler — `build` and `emit-c`**
- `build <file.cufe>` compiles to a native binary through a C intermediate.
  `emit-c <file.cufe> [out.c]` emits the C without invoking gcc, for cross-toolchain
  builds and inspection.
- The entire front end (Lexer → Parser → TypeChecker) is **shared**. A program that
  type-checks does so identically in both backends; `Cufet.Compiler` consumes the same AST.
- Requires gcc on `PATH`. No external libraries and no compiler flags beyond `-pthread`
  and `-lm` — the emitted C is self-contained.

**Exact decimal arithmetic in compiled code**
- `number` compiles to `CufetDec`, a self-contained software decimal that is
  bit-identical to .NET's `System.Decimal` — including round-half-to-even, the 28-digit
  scale, and overflow behavior. Chosen over `double` (which would have made compiled
  arithmetic silently disagree with interpreted) and over libmpdec (IEEE-754 decimal, a
  different format, plus a link dependency).
- Math-book transcendentals (`square root`, `log`, `power`) are double-backed, matching
  the interpreter, which is itself double-backed. Documented consequence: fractional
  `power` can differ in the last digit across platforms, because .NET's `Math.Pow` *is*
  the platform libm. This is genuinely platform-owned, not a divergence to fix.

**The full data model, compiled**
- Text (immutable, arena-allocated), series (per-element-type), maps, records
  (structural value structs), objects (nominal value structs, with embedding, getters,
  setters, `unto` methods, named constructors, and value equality), `voidable T`,
  `T or failure`, matrices, and catalogue/atlas (union types).
- Records and objects are C value structs, so Cufet's value semantics fall out of C's
  assignment-copies rule rather than needing runtime support.
- Unions compile to a tagged struct; `is a <case>` is a genuine runtime tag check. This
  is the one place the language needs runtime type identity — interfaces do not (below).

**Memory: arenas and escape analysis**
- `Pull a rabbit.` opens an arena; `Done.` pops it. Allocation is a bump-tracked pointer
  list, thread-local so concurrent tasks never contend.
- Values that would outlive their region are deep-copied into the destination's arena at
  the point of storage. The type checker computes the destination depth and annotates the
  store; the compiler performs the copy. Closure captures that escape are copied the same
  way, including the environment record itself.
- A region is released on every way out of it, not just `Done.` — `return`, `Stop`, `Skip`,
  `Suppress`, a failure unwind, an exception. Returning a value out of a region hands that
  region's memory to the caller's instead of freeing it, so the value stays valid and is
  reclaimed one level out; a long-running loop whose body opens a region stays flat.

**Concurrency — true parallelism**
- Tasks compile to pthreads with a structured join: a rabbit joins every task it spawned
  before popping its arena, so a task provably cannot outlive its region.
- Channels are mutex/condvar queues. Values crossing a thread boundary are deep-copied
  through a heap bridge, so the message owns its memory in transit and neither thread's
  arena is entangled with the other's.
- Streaming pipes run every stage as a concurrent thread. Interrupts use `sigaction` with
  a minimal async-signal-safe handler plus cooperative checkpoints, so a thread blocked in
  a channel receive can now actually be woken — something the cooperative interpreter
  cannot do.
- Compiled concurrent programs are validated against **order-independent invariants**
  rather than interleaving order, and under ThreadSanitizer.
- **A task can capture any type**, not just numbers, facts, and channels. A captured series,
  map, object, record, text, or catalogue is deep-copied across the thread boundary the same
  way a channel message is, so the two threads' memory stays completely separate. A task that
  would *change* anything it captured is refused, and pointed at channels instead: the change
  could not be seen outside, and two tasks changing one captured value is a genuine race that
  only shows up once the tasks really run in parallel. This covers plain numbers too, and
  there it closed a real disagreement — a task doing `tally becomes tally + 5` produced 5
  interpreted and 0 compiled, because the interpreter hands task bodies the live enclosing
  variable while a compiled task writes its own copy. Nothing about a value being small
  makes the write meaningful; the task still cannot show it to anyone.

**I/O, exceptions, and the standard library**
- Files, streams and `With ... open`, stdin/stdout, subprocess `run`, and shell pipes —
  compiled to POSIX `fork`/`execvp`/`pipe`/`waitpid` with no shell involved.
- `In case of exception` / `Suppress` compile to a setjmp/longjmp stack over
  software-detected faults (Cufet's numbers are software decimals, so division by zero is a
  check, not a hardware trap). Cleanup registries ensure files, channels, and arenas are
  released across a nonlocal jump.
- Books (`math`, `collections`, `chance`, matrix, `sorted`) are compile-time resolved
  builtins — no dynamic linking.

**Closures, interfaces, operators, destructors**
- Closures compile to a `{function pointer, environment}` pair; captures are stored by
  value, which reproduces the interpreter's capture semantics without extra machinery.
- **Interfaces are monomorphized.** Cufet's interface polymorphism exists only at the
  function-parameter position and the argument is always a concrete conformer, so the
  compiler emits one specialization per conformer passed. No vtables, no type tags.
- Operator overloading (`+ - * /` on a single object type) resolves at compile time to a
  direct call — exact nominal match with one candidate, so there is nothing to rank.
- Destructors (`unmaking`) fire at block scope exit, LIFO, on every exit path including
  `return`, `Stop`, failure propagation, and exception unwind.

### Changed

- **Record and object fields now print in sorted order** in both backends. Previously the
  construction order was observable, so two structurally-equal records could print
  differently.
- **Container insertion now copies value types.** Adding a record or object to a series or
  map copies it, matching `Define` and argument binding. Binding is binding — storing a
  value into a container is the same operation as naming it.
- **Argument binding copies records and objects**, closing the one site among four that
  shared them and let a mutated parameter leak back to the caller.
- **Remove-by-value and `contains` use value equality**, not reference identity, so a
  record equal to the stored one is found.
- **Directory listings are sorted** (ordinal) in both backends; raw OS order is
  filesystem-dependent.
- **I/O error messages are deterministic templates** rather than the host exception's
  text, so both backends report identically.
- **`is a` is element-aware for containers.** A `series of text` no longer matches
  `is a series of number`. The interpreter survived that (it is dynamically typed and
  simply read the element back); a compiler could not, since it would reinterpret the
  payload at the annotated type. The check is now answered from the declared type in both
  backends, which also decides the empty-container case that no runtime value can answer.
- Matrix values now print as `matrix((1, 2), (3, 4))`; the interpreter previously leaked
  the host type name.

### Fixed

Latent language and soundness bugs surfaced by holding the two backends against each other:

- **Series indexing was unchecked in compiled code** — out-of-bounds was undefined
  behavior. Now a bounds check that raises a catchable exception with the interpreter's
  exact message.
- **Exception messages could dangle.** A message allocated in a nested arena was freed by
  the catch before the handler read it, and the freed block was promptly reused — a
  message would read back as whatever string was concatenated next. Messages are now
  allocated in the target handler's own arena.
- **Values escaping a region were a use-after-free on the normal path.** The outward-store
  invariant covered series, maps, and objects but not `text`, and any value-typed wrapper
  (a record, a voidable) laundered even a covered type past the check. The test is now
  structural over the whole shape.
- **Jumping out of a region corrupted the arena stack.** A `return`, `Stop`, `Skip`,
  `Suppress`, or failure unwind that left a `Pull a rabbit` skipped the rabbit's `Done.`,
  so the arena depth was left one level too high — climbing by one on every such exit until,
  after 64 of them, the next region pushed past the end of the arena table. Any program with
  a helper that returns from inside a region and is called in a loop crashed. Regions now
  unwind alongside files, handlers, and destructors on every exit path: a jump that carries
  nothing out releases them, one carrying a failure message moves the message outward first,
  and a `return` hands its region's memory to the caller's — never copying, because a
  returned value may be the caller's own and the two backends must keep sharing it.
- **A container living outside a region, grown from inside one, was freed with that region.**
  Adding to a `series of number` or a `map from number to number` declared outside a
  `Pull a rabbit` moved the container's own storage into the rabbit, so it dangled after
  `Done.` — even though every element was a plain value. `examples/parallelsum.cufe` was a
  use-after-free. Two things hang off the escape annotation, copying the stored value and
  relocating the container's growth, and it was gated on the condition only the first needs.
- **A map key built inside a region and stored in a longer-lived map was freed with that
  region.** A map is the only place in the language that stores two values at once, and only
  the value half was ever checked for escape.
- **Some refusal messages talked about the compiler's internals instead of your program.**
  Trying to await one task's result from inside another reported `'TaskHandleType' is not yet
  supported by the compiler (slice 5B: records + objects + text)` — an internal class name, an
  internal slice number, and a list of unrelated features. It now says that a task cannot await
  another task's result yet, names the task you referred to, and tells you to await it from the
  rabbit body or pass the value through a channel. The same pass removed every other mention of
  internal slice numbers from user-facing errors, and gave every type a real Cufet name, so no
  message prints an implementation class name any more — the few remaining "this construct
  isn't handled" fallbacks now name the construct in the language's own words.
- **`range` could not be used as a value.** `range 1 to 5` worked as the thing a `For each`
  loops over, but not as something you could name — `Define halves as range 1 to 2 counting by
  0.5.`, an example in the reference itself, would not compile. It now produces a series like
  any other expression, using the same stepping and direction logic as the loop form so the two
  cannot drift apart. A `counting by` step that computes to zero or a negative number now also
  raises the same error compiled as interpreted, instead of looping forever; a literal zero was
  already caught before the program ran.
- **An empty collection did not know what it was meant to hold.** `is a` recurses into a
  collection's elements to answer precisely, but an empty one has no elements — so an empty
  `series of text` answered yes to `is a series of number` as readily as to its own type.
  That was only reachable through a catalogue, where the declared type cannot narrow it, and
  it made compiled and interpreted programs take different branches, so narrowing a catalogue
  that could hold two different collection types was refused outright. Collections now
  remember the element type they were created with, so an empty one answers from that. Both
  backends agree, and the refusal is gone.
- **Pipe stages were never type-checked against each other — or, in a consumer's case,
  at all.** `for each n from the input:` gives the iterator no type, so a stage's input
  type can only come from the stage before it, and nothing was carrying it across. A
  producer emitting numbers into a stage that treated them as text got past the front end
  entirely and failed much later and much worse: interpreted, as a raw .NET exception with
  a stack trace; compiled, as a `gcc` error about generated C. The type checker now walks
  each pipe in order, carries every stage's output type into the next as its input, and
  checks the body against it — so the mismatch is an ordinary Cufet type error on the
  offending line. Reusing one stage function at two different element types is now also a
  front-end error rather than a compiler-only refusal.
- **Ctrl-C was ignored while a task was running.** Only the main thread and streaming-pipe
  stages had somewhere to unwind to, so an interrupt inside a `Have rabbit start a task` body
  did nothing at all — and the main thread, waiting at the rabbit's `Done.`, never looked at the
  interrupt flag either. The program simply kept going. Tasks now unwind like any other thread:
  the task stops where it is, its destructors run and its files close, the rabbit reaps it at the
  join, and the program exits. A task blocked waiting on a channel used to treat the interrupt as
  "the channel closed" and carry on with a value it never received; it now stops. Awaiting a task
  that was interrupted no longer reads from nothing. Streaming-pipe stages, which could already
  be interrupted, now run their destructors and close their files on the way out too.
- **Closures that only wrote to a captured variable never captured it**, and a nested
  lambda parameter could mask a same-named outer variable across the whole enclosing body.
  Both emitted an undeclared C variable.
- **If/While conditions with preparatory steps were never flushed**, miscompiling any
  condition that needed one (environment variable, map lookup, channel delivery).
- **Guard-return narrowing** (`If n is void, return failure.` then using `n` as a plain
  `T`) now works in both backends — the natural error-handling idiom the docs lean on.
- Failure propagation inferred the wrong result type for non-numeric inners; task result
  types fell back to `number` for reference results.

### Notes

- Some compiler tests are Linux-only: POSIX features (concurrency, subprocess, signals)
  cannot be built by mingw, and the sanitizer runs are Linux-only.
- Compiled concurrent programs are genuinely parallel, so the interpreter's deterministic
  interleaving is not a specification. Concurrency tests assert order-independent
  invariants.

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
