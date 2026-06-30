# Changelog

All notable changes to Cufet are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning: feature arcs bump the minor version; 1.0.0 marks language stability.

---

## [Unreleased]

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

## [0.7.0] — 2026-06-21

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

## [0.6.0] — circa 2026

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

## [0.5.0] — circa 2026

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
