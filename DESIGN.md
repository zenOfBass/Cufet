# Cufet Design Decisions

Why Cufet is the way it is.

This is the record of decisions that are **settled** — the reasoning behind them, and in
several cases the alternative that was tried and rejected. It exists so that a question
answered once does not have to be answered again, and so that a future reader (including a
future me) can tell a deliberate choice from an accident.

It is not a specification. [GRAMMAR.md](GRAMMAR.md) states the rules precisely and
[REFERENCE.md](REFERENCE.md) explains how to use them. It is not a history either —
[CHANGELOG.md](CHANGELOG.md) records what changed and when. This file answers only *why*.

Where a decision has a cost, the cost is stated. Where one was reversed, the reversal is
kept rather than tidied away: the reasoning that failed is usually more useful than the
reasoning that held.

---

## What Cufet is for

The decision the rest of them answer to.

- **Cufet's identity: teaching systems language — both, deliberately.** Cufet is
  not purely educational (like Scratch) and not purely industrial (like Zig). It is
  *both simultaneously*, and doing both is the central design challenge. Decisions
  must serve learners (readable surface, warm errors, forgiving defaults) *and*
  systems programmers (static types, real memory, no hidden costs, native-backend
  trajectory). When these pull against each other, name the tension and resolve it
  explicitly — don't drift toward either pole without notice. This is the lens for
  every frequency/feature call.

---

## Surface syntax

How the language reads on the page.

- **Arithmetic uses symbols; comparison and logic use words.** Symbols win for
  math (that's how literate people write it). Comparison/logic read better as
  words for the audience and aesthetic. One canonical form per operator — no
  synonyms (the rigor is in the single fixed keyword, not in symbols).

- **`=` in expressions, word-comparisons in conditions (positional split).**
  Comparison-as-a-free-floating-value is in the math domain (symbols);
  comparison-inside-a-conditional reads as a sentence (words). One form per
  context — *not* two interchangeable ways to say one thing. This is settled
  by design; facts being first-class storable values does not destabilize it.

- **Identifiers are lowercase-initial; uppercase-initial is reserved.** This is a
  load-bearing readability guarantee, not vestigial: keywords are
  case-insensitive, but the lexer still rejects uppercase-initial non-keywords so
  that every uppercase word a reader sees is provably a keyword and every
  lowercase word is a variable — roles parseable by eye, no lookup. (All object
  types and instances are therefore lowercase: `person`, `alice`. The
  proper-noun feel lives in string literals like `"Alice"`.)

- **`{}` is the object/OOP world; `()` is the data/call world.** Object instances
  and interface definitions use `{}`; records, series, function args use `()`.
  A consistent visual signal for which world a construct belongs to.

- **`one` is the self-reference inside methods** (`one's name`). Third-person,
  reads like English. Mild collision with the generic English pronoun "one" in
  prose *about* the language — write examples with care (code-font the keyword).

- **Possessive is always `'s`**, even for words ending in *s* — `series's`,
  `process's`. No English plural-possessive exception (`series'` is wrong in
  Cufet). One rule, no edge case.

---

## Absence, and the type system

What it means for a value not to be there, and how types relate.

- **No null.** Every value is initialized; absence is expressed structurally,
  never by a null value. Absence has one principled mechanism: the voidable type.

- **`void` is a first-class value; absence is one unified concept.** Rather than
  separate vocabulary for "function returns nothing" and "lookup found nothing,"
  `void` is a single, holdable empty value used for both — the Rust `Unit` model.
  `voidable T` is "a T, or void." This unifies absence under one word, keeps the
  keyword dictionary small, and dissolved the old special "void result used as a
  value" error into ordinary type-mismatch (a `void`/`voidable` used where a
  concrete type is required is just a type error). The voidable type is the
  single load-bearing answer to "or nothing" — it unblocks text→number,
  recursive data structures, and (eventually) file I/O for the shell.

- **Narrowing is variable-level, not expression-level.** A voidable narrows to
  its plain type inside a branch that checked it — but keyed on a *variable*, not
  an arbitrary expression. The principled reason (not just simplicity): a literal
  buried in an inline lookup is a magic-value smell that should be named anyway,
  so the language narrows the *named binding* rather than contorting the checker
  to track inline expressions and their possible mutation. The clean path (name
  it) and the supported path (narrowing) coincide. Expression-level narrowing is
  deferred (and unsound against mutable maps unless done carefully).

- **Records are structural; objects are nominal.** Two records are the same type
  iff they have the same shape; two objects are the same type iff they have the
  same name. Records are *data* (interchangeable by shape); objects are *things*
  (identity by name). The language reference must explain this split clearly —
  users will be surprised the first time two same-shaped records unify or two
  same-shaped objects don't.

- **Compound-type assignment semantics (intentional split):** records are
  value-typed (copy), objects are value-typed (copy), series are reference-typed
  (share). Records and objects are bounded "things"; series are unbounded
  "collections," and developers intuit copy-vs-share differently. The split is
  principled, not a bug — do not "unify" it. (Objects use the "struct model":
  value-on-assignment, mutable-in-place via `becomes` and via mutating methods.)

- **Objects are flat: no classical inheritance, no subtyping, no variance.**
  Inheritance's central cost is hidden coupling and rigid hierarchy — the exact
  things Cufet's design refuses everywhere. Its central benefit (polymorphism)
  is available without it. So Cufet uses **composition + embedding** (reuse) and
  **interfaces** (polymorphism) instead. This keeps the type-checker free of
  variance — function-signature matching stays exact-match. **Embedding promotes
  members without subtyping; interface conformance is a flat check, not
  subtyping.** Neither makes one object type a subtype of another.

- **`as` for embedding, `is` for interface conformance.** `customer ... and as a
  person` reads "functions as a person" (composition — honest; customer is not a
  person, it has one). `person ... and greeter` / `... and is greetable`-style
  reads as an is-a-kind-of claim, which is honest for interface conformance.
  Avoids overloading `is`, and each word means what's actually happening.

- **Exact-match function-signature matching, expandable later.** Chosen
  deliberately; sufficient without a type hierarchy. If variance is ever needed
  (only if real subtyping is introduced), exact-match is the identity special
  case it would widen from — additive, not a rewrite.

---

## Objects: lifetime and accessors

What an object owns, and what a property may do.

- **Destructor close/flush companion convention** (guidance, prevents silent data
  loss): `unmake` is the infallible last-resort backstop. For cleanup that *can* fail
  (flushing a buffered writer, committing a transaction), the object should expose a
  fallible method (`close`/`flush`/`commit`) and the caller handles the failure
  *before* the object's scope ends. Relying on `unmake` alone to flush risks silent
  data loss — the destructor swallows all outcomes. (Same pattern as Rust `Drop`+
  `.close()` / Java `Closeable.close()`+`finalize`.)

- **Destructor ownership rule:** `unmake` closes what the object *opened*, not what
  it *borrowed*. A resource injected from outside is owned by the caller — closing it
  in the destructor is a double-close bug.

- **Setters are infallible and transform-only (Option A — settled).** A setter may
  clamp, convert, normalize, or derive — but it cannot reject. Validation-that-rejects
  belongs to the caller, before the assignment. This keeps `becomes` infallible
  everywhere it appears. Fallible setters (Option B) are deferred to a future
  effect-tracking arc.

---

## The standard library, and what earns a place in the grammar

Which capabilities are spelled into the language and which are pulled from a book.

- **Organization: common-as-grammar, rare-as-book; namespaces permanently
  closed.** Organization philosophy is *frequency of use*: common functionality
  (~95% — text, numbers, collections, control flow) is core grammar — no
  imports, no prefixes. Rare/specialized capability is pulled as a `book` when
  needed. **Namespaces are deliberately not built** — they would be a fourth
  organizer (alongside functions, objects, and lexical scope) adding import
  overhead and prefix noise without providing value the other three don't
  already cover. A `book` is an object-like value (possessive/`of` member
  access, singleton, stateless capability-bag) but pulling one is a
  *module-loading operation*, not object construction. The `module` interface
  is the contract program code depends on; the loader produces
  `module`-conforming values. `book` is-a `module` (same pattern as
  `vehicle`/`car`). Singleton and statelessness are loader-enforced
  conventions, not interface-level constraints — the interface stays minimal
  and general; the loader enforces book-specific behavior. The `module`
  interface can be built early as the stable seam; the real external-code
  loader comes later without touching program code.

- **`sum` is not a series aggregate and will not become one.** Addition is already
  expressed with `+`; a `sum` function would duplicate it and violate the
  one-canonical-way rule. Collections aggregates may exist in future arcs, but `sum`
  is permanently excluded. Revisiting requires a new rationale — "it's convenient"
  is not one.

- **`chance` is separate from `math` — effectful vs. pure.** `math` is a pure
  function book (same inputs, same outputs, no side effects). `chance` has internal
  RNG state — it is effectful by design. Keeping the two separate is a named
  structural choice: as Cufet gains more books, the effectful/pure distinction will
  matter for reasoning about code, testing, and for safe concurrency (which now
  means real threads). Per-interpreter RNG (`Random _rng` on the `Interpreter` instance,
  not static) gives free test isolation: each `new Interpreter()` gets its own entropy
  seed.

- **`Pull … Done.` unification — one surface for scoped resources.** Books, rabbits,
  and other acquired resources all use a unified `Pull <thing>: … Done.` block syntax.
  The `pull` verb signals "resource whose lifetime is managed here" and `Done.` closes
  it cleanly. The dot form (`Pull a book on X.`) keeps the short non-block form for
  scope-local imports. The two forms coexist and compose cleanly.

- **Matrix arithmetic is operator syntax, not book functions — settled.** `m + n`,
  `m - n`, `m * n` are the surface, not `collections' add of (m, n)`. The
  one-canonical-way principle: there is one way to say "add these matrices," and it
  is the `+` operator. This was a hard decision — book functions would have been
  faster to build — but building them first as a stopgap would have required
  deprecating them after operator overloading landed. The right sequencing was to
  build operator overloading first, then matrix arithmetic as its first exercise.
  `*` means matrix product (standard dot product), full stop; Hadamard product, if
  ever added, will be a named `collections` function precisely because `*` is taken.

---

## Memory and concurrency

The two arcs where soundness was the whole problem.

- **Region model soundness — the adversarial arc (all three holes closed, 2026-06-26–28).**
  The outward-only invariant ("a value may escape to a longer-lived region but never
  inward to a shorter-lived one") is the whole safety story for the regions model.
  Its teeth were tested adversarially — deliberately probing whether the invariant
  held against real attacks — and three holes were found and closed.

  *How the holes were found:* the reference-linked-rabbit test (a rabbit
  containing objects that reference each other) was used as an adversarial probe:
  "does the downward-only invariant actually prevent unsound escapes, or can we
  launder depth through legitimate-looking code?" It found hole #1 (function-call
  depth laundering). Investigating #1 surfaced #2 and #3.

  **Hole #1 — return-depth laundering through function calls.**
  A function call (`cast f on (ref-param)`) fell through `ValueDepthOf` to depth 0,
  making the return value appear shallower than it was. A function that "returned its
  parameter" would appear to return a depth-0 fresh value, not the depth-N
  rabbit-allocated value it actually returned. *Closed via return-depth inference:*
  `ReturnDepthSignature` on `FunctionType` — a list of which parameter indices (0-based)
  flow into the return. Computed by `ComputeReturnDepthSignature` at the end of
  `CheckBind`. `ValueDepthOf` reads the signature and uses `max(subset)` of the
  actual argument depths. Conservative fallback (unknown callee → max of all ref-type
  inputs) is always safe: over-strict, never under-strict.

  **Hole #3 — methods/getters residue of #1.**
  Methods and getters had `ReturnDepthSignature == null` → depth 0, the same
  laundering vector as free functions. Possessive field reads (`alice's cards`,
  `the items of obj`) also fell through `ValueDepthOf` to 0. *Closed* by extending
  the signature machinery to method/getter bodies with the receiver as a depth source
  (`ReceiverDepthIndex = -1` sentinel means "receiver's depth flows to return").
  `_possessiveDepthCache` / `_rnaDepthCache` populated from `InferPossessiveAccess`
  / `InferRecordNamedAccess`.

  **Hole #2 — capture-store laundering.**
  A nested function that captures a reference-type *parameter* of its enclosing
  function can store it into outer state. Parameters are registered at `RabbitDepth = 0`
  (the function's own perspective), regardless of what the caller passes. If the
  caller passes a rabbit-allocated (depth-N) value, the captured parameter appears at
  depth 0 inside the nested function; the depth check passes (`0 > 0` is false → no
  error). At runtime the value is depth-N → use-after-free in native. *Closed
  conservatively:* `TypeInfo.IsParameter` flag set at all parameter-registration sites
  (free-function params, receiver `one`, method params, setter params, lambda params).
  In nested-scope import (`isNested = true`), any captured `TypeInfo` where
  `IsParameter && IsReferenceType` is upgraded to `RabbitDepth = CapturedParameterDepth
  = int.MaxValue`. The existing `CheckRegionStore` then rejects any outward store
  (MaxValue > any real depth). No new check logic; no call-site changes.

  *Key insights (the load-bearing reasoning for future contributors):*
  - **The depth model is integers joined by `max`** — not Rust's arbitrary lifetime
    parameters. This is why inference was tractable (depth is a simple number; `max`
    is associative and monotone) and why no user-facing annotations were needed
    (identity functions — `f(x) = x` — stay annotation-free because inference derives
    their signature).
  - **Conservative bias is mandatory for soundness.** Over-estimate depth (→ stricter
    → never permit unsafe) rather than under-estimate (→ might permit unsafe). The only
    cost of over-strictness is rejecting contrived-safe code, which can be addressed
    with an explicit annotation. The cost of under-strictness is a soundness hole.
  - **The conservative prohibition (hole #2) is triply rare** — requires: (a) double
    nesting, (b) a reference-type parameter capture, and (c) an outward store. The
    over-rejection cost is nil; the workaround is trivial (pass the value as an
    explicit parameter to the nested function instead of capturing it).
  - **This was adversarial-find-and-fix, not formal proof.** The invariant is sound
    with respect to the three holes found. A fresh-eyes red-team or a formal proof
    remains open for a contributor to take on, and the native backend makes it worth
    more rather than less: the interpreter's GC forgives a region error that compiled
    code turns into a use-after-free.

  *Status:* all three holes closed; no known remaining soundness gaps.

- **Concurrency arc — message-passing + structured concurrency, cooperative (v0.9.0).**
  The complete concurrency core (all five slices) is built, validated, and hardened
  by five concept cars. The design decisions and coherent narrative:

  *Model decision — message-passing, not shared-state+locks.*
  Shared mutable state destroys the outward-only region invariant: cross-task
  reference aliasing is use-after-free in native, exactly the class of bugs the
  invariant is designed to prevent. Message-passing keeps regions sound by
  construction — values deep-copied at channel boundaries, no cross-task aliasing.
  This is the Hoare CSP / Dijkstra-validated model: the theory-approved choice for
  a language where region safety is load-bearing.

  *Model decision — structured concurrency.*
  A task cannot outlive its spawning scope. Tasks join before the spawning rabbit's
  `Done.`. This composes directly with the `Done.`-bounded region discipline — no
  new lifetime concept needed, and the join is guaranteed even through exceptions.

  *The key insight — "a structured task is just a scope with a name."*
  A structured task joins before its rabbit's `Done.`, making it shorter-lived than
  the spawning scope. The existing region depth model + `CheckRegionStore` handle
  soundness: task-body locals cannot escape to the enclosing scope for the same
  reason inner-scope values cannot escape in sequential programs. **Zero new
  soundness machinery was needed.** The sequencing (soundness arc first, concurrency
  arc second) was deliberate — this inheritance was the goal.

  *Model decision — cooperative scheduling (interpreter era).*
  One task runs at a time; tasks interleave only at explicit yield points. C# async/
  await with `CufetScheduler` (custom `SynchronizationContext`) routes all
  continuations to a single per-thread FIFO queue — no OS-thread parallelism, no
  interpreter-internal data races by construction. Sequential programs unchanged.
  *Resolved:* the compiler lowers tasks to pthreads, so compiled programs are
  genuinely parallel. Their tests assert order-independent invariants rather than a
  particular interleaving, and run under ThreadSanitizer.

  *Five slices:*
  1. **Scheduler** — `CufetScheduler` engine. Validated: two async units interleave
     at yield points and both complete; exception propagation correct.
  2. **Structured tasks** — `Have rabbit start a task [as <name>]: … Done.`
     Spawn, task-body scope, join-at-Done.
  3. **Channels** — `a channel of T`; `Send`/`the delivery from`/`Close`.
     Deep-copy at send = the cross-task aliasing guarantee.
  4. **Task results** — `return <value>.` + `the awaited result of <name>`.
     Concurrent functions — same keyword, same fallible/void machinery.
  5. **SIGINT-at-yield + `Yield.`** — scheduler drain loop checks interrupt at
     each dequeue; blocked receive and await also wake on interrupt. Pays down the
     longest-standing interpreter-era debt.

  *Safety guarantee validated — channel-deepcopy concept car.*
  Proved deep-copy holds under nested structures (record-of-series, map-of-series).
  The central safety claim — no cross-task aliasing — is earned, not asserted. Also
  found: series literals not accepted in expression position → wired into
  `ParseCorePrimary`.

  *Fan-out native-characteristic — work-queue concept car.*
  Validated coordination correctness (close reaches all blocked workers, exclusive
  delivery, no hang). Also found: fan-out distribution doesn't balance under the
  cooperative scheduler — one worker drains everything while others starve. This is
  an interpreter-era characteristic: the FIFO cooperative scheduler serves one
  worker until it blocks. *Resolved as predicted* — under real OS threads the work
  actually distributes, and the compiled fan-out test asserts it.

  *The Dijkstra connection — map-key value-type constraint (test #5).*
  The Dijkstra example surfaced the root cause of its silent-wrong-answer bug:
  objects used as map keys break under deep-copy semantics (reference identity
  lost). The fix was a principled type-level constraint — map keys must be value
  types (text, number, fact). Reference-type keys produce a static type error with
  an educational message explaining the identity semantics. Option A (value-equality
  objects as keys, analogous to Python's hashable/Rust's Hash+Eq) is deferred — it
  requires a deliberate equality contract that Cufet doesn't have yet.

  *Named constraint — now enforced.*
  This was recorded as a note for a future enforcer: **task bodies must not mutate
  captured reference-type state from outer scopes.** The compiler enforces it. A
  captured series, map, object, or text is deep-copied across the thread boundary
  the same way a channel message is, and a task that would *change* one is refused
  at compile time, with the error pointing at channels instead. Three reasons it is
  a refusal rather than a silent copy: the interpreter hands task bodies the live
  enclosing binding, so a copy would visibly disagree with it; sharing instead is
  unsound, because arenas are per-thread and a mutation that grows a shared series
  would reallocate into the task's own arena; and the pattern is a genuine data
  race that only the cooperative scheduler was hiding.
