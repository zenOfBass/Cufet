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

- **The language closes a circle, and that is the pedagogical model.** The facilities a
  learner reaches for on day one are not privileged compiler magic — they are objects and
  modules, of the same kind the learner will eventually build. `pull` a book to get at
  capability; later learn to make an object, then an interface, then a module — and by then
  you know how to write a **book** (an import) and a **rabbit** (an agent-helper) yourself.
  What you used at the start is the thing you build at the end.

  **This is a constraint, not a curriculum.** It is why a book is an object-like value with
  possessive member access rather than a namespace — namespaces are rejected below, and this
  is the deeper reason: a namespace is a compiler fiction nobody could ever author, while an
  object is something a learner grows into writing. Any future facility should be checked
  against it: *could a user eventually have written this?* A "no" is not fatal, but it is a
  cost that has to be named.

  ⚠ **The two halves are not equally close, and pretending otherwise would mislead.** A book
  is a stateless capability bag — an object with members, and reachable. A **rabbit is a scope
  with a lifetime**, with compiler-enforced region and escape rules, so a user-definable
  rabbit means user-definable continuations. That lands on the unanswered "which restriction?"
  question in the rabbit control-flow arc, and it is the ambitious end of this idea rather
  than the near one. `book` is-a `module` is buildable now; `rabbit` is-a `module` is a
  direction.

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

- **And no variance for BLANKS either, if they are ever extended.** Not
  covariance, not contravariance. A blank is filled by one structural match per
  argument and means the same type everywhere it appears in a call; that is the
  whole of the inference and it stays that way. Variance is the part of generics
  nobody can explain to a learner, and a teaching language that ships
  `IEnumerable<out T>` has lost the plot.

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

  ★ **`chance` has no members and never had any.** Its whole surface is statement and
  expression syntax — `a random number from 1 to 6`, `randomly shuffled`, `a random guess`,
  `Seed the chance with 42.` — which the language parses directly rather than dispatching
  through the book, so pulling it is what *licenses* those forms rather than what supplies
  them. It still has a Cufet layer of its own, carrying nothing, so that no book sits outside
  the rule that a module is an object.

- **There is no iterator concept, and there will not be one.** A stash *is* the
  iterator: it produces values one at a time, it is a first-class value, and it goes
  wherever a value goes. Adding a separate steppable-thing abstraction would be a
  second way to say what a stash already says. `For each` over a stash is therefore
  not "iterator support" — it is one more source kind on the loop, rewritten in the
  front end into the drain it stands for.

  ★ **And `For each` over a user-defined object stays refused.** The simple case is
  already spelled `For each x in obj's items`, and a real structure has *several*
  orders — a tree has three at least — so no single `For each` could pick the right
  one. Naming the walk is the better surface, and a walk is a burying function, which
  is a stash, which the loop above takes. Measured 2026-08-20: the shape that used to
  make this expensive (an interface can be neither a return type nor generic, so
  nothing could declare "hands back something steppable") is gone, and the answer is
  still no — for the reason above rather than for cost.

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

---

## Foreign interoperability

How anything that is not Cufet gets reached from inside Cufet — C libraries, and source
written in other languages. Designed 2026-08-21; **nothing here is built yet.** The
ordered work lives in [ROADMAP.md](ROADMAP.md); this is the *why*.

### The rabbit block is the unsafe marker

Pointers exist **only inside a rabbit block**, and nowhere else. That block already means
*region-scoped memory work*, so it is also the closest thing Cufet has to `unsafe` — and
it needs no new keyword to say so. Leaving the block ends the pointer.

The reason it is the rabbit and not a new marker: **a pointer is a rabbit responsibility.**
The arena that knows when a region dies is the thing that knows when a pointer dies. That
*extends* the existing safety model rather than holing it.

### The type is an `address`

Not `pointer`. `address` is plain English for exactly what it is, where `pointer` is C's word for
it — and the language already reached for it on its own: `the address of settings` is how the
struct case reads, and the FFI item always described "an explicit address-of".

★ It costs nothing. `address` and `pointer` both have zero uses across `examples/` and the prelude,
and a type name does not have to be reserved — `rabbit` appears nowhere in the lexer and is
resolved in the type checker instead. So `the text address` stays available as a field name.

```
Cast tcgetattr on (fd, the address of settings).
Define c-language close-file, given (the address handle), as [fclose(the handle)].
```

### One concept, and it is inert

There is **one kind of foreign pointer**: opaque, rabbit-scoped, and impossible to
dereference implicitly. Reading through it is an **explicit act that always copies into the
arena** — `the text at p` yields rabbit-owned text, never a view into foreign memory.

★ `char*` and `FILE*` are therefore the *same type*. What differs is not the value but what
the writer does with it: you read through the first and never through the second. An earlier
draft split "data" from "handles" and that was a mechanism invented where an operation would
do.

★ Explicit reads are the guardrail working in the open. You are inside a rabbit block
*because* this is the dangerous area, so reading foreign memory should be a thing visible in
a diff rather than marshalling hidden in a declaration.

⚠ **The residual danger, accepted deliberately:** a stale handle can still be handed back to
C. Refusing that would mean never letting the pointer exist at all, which costs `fopen`.
This is the smallest residue that still lets real C be called, and it is where "as many
guardrails as we reasonably can" ends.

### Freeing is the unmaker registry

A foreign allocation is registered with the function that releases it, exactly as an
unmakeable object already is:

| C call | registers |
|---|---|
| `strdup` | the pointer, with `free` |
| `fopen` | the handle, with `fclose` |
| `opendir` | the handle, with `closedir` |
| `getenv` | *nothing* — static memory |

There is no second list for "does not need freeing"; those are simply not registered. The
writer never frees anything by hand — the **declaration** names the release function, once
per binding, because `getenv` and `strdup` have identical C signatures and opposite
obligations and nothing can infer which is which.

★ `UnwindTo` already fires unmakers at every nonlocal exit, so a handle is released whether
the block is left by `Return`, `Stop`, an exception or `Suppress`, with no new cleanup code.
That was the point of collapsing the cleanup families into one `CleanupPoint`.

⚠ **When a declaration says nothing, do not free.** A leak is recoverable, visible to a leak
checker, and bounded by the rabbit's lifetime; a double-free is memory corruption that
surfaces somewhere else entirely. Guardrails fail toward the recoverable side.

### The boundary conversions, and the shim

**One number type survives FFI**, as it has survived everything else. C types live only in
the declaration.

| C | Cufet | why it is safe |
|---|---|---|
| `uint8_t`/`uint32_t`, flags, masks | `bits` at that width | `bits` carries a width, and narrowing already refuses to drop a set bit — loudly, in the divide-by-zero class |
| `int`, `long`, `ssize_t` | `number` | `bits` is unsigned (`not 0b0000` is `0b1111`), and `read()` returning −1 must be −1 |
| `char*` | `text` | the arena copy above |
| `void*`, `FILE*` | the opaque pointer | never dereferenced |
| `bool` | `fact` | |
| `double` | `number` | ⚠ **the only lossy conversion in the boundary** |

★ Neither integer path can be silently wrong. `number` is a decimal with 28–29 significant
digits, so it holds every `int64` *exactly* — converting is a **range check**, not a lossy
narrowing, and it refuses loudly like the `bits` rule does.

⚠ **`double` is the exception, and it is why the shim exists.** `number` is base-10 and a C
`double` is base-2; `0.1` is exact as one and not the other. Two separately-written
conversions would differ in the last ULP — which is exactly the libm caveat this project
already retired once by going pure decimal, and exactly the shape of the casing bug that the
shared case table exists to neutralise.

**So the shim owns the conversion rules**, written once in C, called by both backends. It is
not "FFI implemented twice": the compiler knows each signature statically and emits a direct
call, so only the *interpreter* dispatches dynamically. What is shared is the part that can
silently disagree.

★ Because the dynamic dispatch lives in C, the interpreter's P/Invoke surface is a handful
of fixed `DllImport`s — no `DynamicMethod`, no `calli`.

**FFI does not ship until both backends run it.** The interpreter is the oracle; FFI is the
one area where being wrong means memory corruption rather than a wrong number, and it is the
last place to give up a second opinion.

⚠ The playground runs the interpreter in **wasm**, where FFI cannot work at all. "This
program cannot run in this environment" is therefore a required outcome regardless.

### Structs: Cufet owns the memory wherever it can

Structs travel as **pointers**, never by value — which is what keeps the signature set below
simple. But the targets need to read and write struct *fields*: raw terminal mode is
`tcgetattr`, set `c_lflag`, `tcsetattr` back; a socket bind fills `sin_family`, `sin_port`,
`sin_addr`. So layout is on the critical path, not a later item.

★ **The common direction needs no dereferencing at all.** Cufet owns the memory, C fills it:

```
Pull a rabbit as hopper.
    Define settings as a new termios { … }             ← arena-allocated, C layout
    Cast tcgetattr on (fd, the address of settings)    ← C writes into it
    The settings' c-lflag becomes …                    ← ordinary Cufet field mutation
    Cast tcsetattr on (fd, the address of settings)    ← C reads it back
Done.
```

No foreign pointer is dereferenced; mutation is ordinary Cufet mutation. This is what "an
explicit address-of" was always for.

The other direction — `getpwnam` handing back a `struct passwd *` — is a foreign pointer, and
reading it **copies into a Cufet record**, exactly as `char*` copies into `text`.

⚠ A Cufet record already becomes a real C struct (`cr_N`) in the compiled backend, but its
FIELDS are Cufet representations — `number` is a 128-bit `CufetDec`, not an `int`. So a
C-compatible record is one whose fields are declared as **C types**; ordinary records are not
layout-compatible and must not be passed off as such.

### Nobody reimplements the ABI

The C struct declarations live in the shim, so **the C compiler lays them out** and both
backends read those offsets. There is no struct-layout algorithm in C#, and therefore no way
for the two to disagree — the same reasoning as the conversions.

⚠ **The rejected alternative, recorded because it will look tempting:** having the interpreter
compute layouts from standard alignment rules. It works for scalars, arrays and nested structs
and quietly gets **bitfields, packed structs and unions** wrong — silently, which is the exact
failure the shim exists to prevent. If that path is ever wanted, the honest form is to compute
the easy cases and *refuse* the hard ones, never to guess.

**The cost, stated accurately:** the shim cannot be a fixed prebuilt library, because it must
contain the program's own struct declarations. So it is **generated from the declarations and
compiled**. Two things make that mild rather than severe:

- **It caches.** `RuntimeCache` already content-addresses compiled objects by a SHA of source,
  header, gcc identification and flags. A generated shim keyed the same way means gcc runs
  **once per distinct set of declarations**, not once per run.
- **The common structs ship precompiled.** `termios`, `sockaddr_in`, `stat`, `timeval` — what
  the stated targets need — can be bundled, so the shell, sockets and terminal work with no
  toolchain at all.

> **The constraint, in full:** interpreted FFI needs a C toolchain the first time a given set of
> declarations is seen, and not at all for the bundled structs. Wasm cannot do it in any case.

### One `c` book

C bindings live in **one book**, not split by domain and not split by platform.

The arguments against it were both wrong and are recorded so they do not come back:

- *"libc is too big"* — a C book holds what the project has actually **bound**, not all of libc,
  and members are reached as `c's read`, so a large book pollutes nothing at the pull site.
- *"split by domain so the platform difference lands on the book"* — weak. Portability is not a
  naming question: a program calling `tcgetattr` is POSIX-only whatever the book is called. Naming
  changes only *where* the failure is reported, not whether it happens.

⚠ Splitting by platform (`c-windows`, `c-linux`) is the wrong axis entirely — it puts the platform
in the book's identity, and a program written against one still does not run on the other.

**No portability warning.** A linter note saying *"you used a Linux-only function without handling
other platforms"* was considered and rejected: writers get access to platform-specific functions
without being nagged for using them. ★ It is also a weaker case than it looks next to the void
guardrail — void must be handled because ignoring it gives a **silent wrong answer**, whereas a
function absent on this platform fails loudly and early. The guardrails exist where failures hide.

★ A warning is only worth issuing if the reader can act on it, and acting on this one would need a
platform-branch concept in the language. That is a real feature, worth revisiting only if programs
are ever distributed to machines the author does not control — which needs a loader and a package
manager first.

### Bounded signature set, not libffi — for now

The shim calls foreign functions through a generated switch over the signature shapes
actually supported, rather than taking a dependency. libffi is the conventional answer and
would be the right one the moment any of these arrive:

- struct-by-value in either direction — hand-rolling the x86-64 SysV classification
  algorithm, with different rules on Windows x64, would be a genuine mistake
- varargs beyond a fixed shape
- **callbacks from C into Cufet**

★ With scalars and pointers only, every argument passes the same way and the switch is small
and inspectable. The design keeps it that way: foreign pointers are opaque, so structs arrive
and depart *as pointers*, never by value.

**Callbacks are out of this arc**, and the deciding rule is: *you need a callback when the
library owns the loop.* Nothing in the target set does — sockets, `ioctl`, terminal mode,
`epoll`/`select` (you poll them), job control. Signals are already handled by the emitted
runtime's own `sigaction`, and a Cufet signal handler would be wrong anyway, since handlers
must be async-signal-safe and cannot run an arena allocator.

⚠ **Callbacks are also asymmetric between backends**, which is the sharper reason. In the
compiled backend C-calls-Cufet is nearly free, because compiled Cufet *is* C. In the
interpreter the same callback needs a trampoline that re-enters the interpreter. Under
"ships only when both backends run it", that makes them expensive, full stop.

**The trigger for revisiting:** the first API actually wanted that owns the loop. Checkable,
unlike "when we need more power". Reversing is cheap — the conversion rules are identical
either way, and the switch is the throwaway part.

### An axiom: foreign source as a value

The type is an **axiom**, written in square brackets, tagged by the language book it belongs to:

```
Pull a book on the c-language.
    Define a c-language axiom get-pid as [getpid()].
    Bind number to process-id, return get-pid.
Done.
```

**Why `axiom`.** It names the *contract*, not the appearance. An axiom is taken as given without
proof — which is exactly what this is: Cufet cannot check a C listing, cannot prove anything about
it, and accepts it on trust. `listing`, `source` and `block` describe how it looks; only this one
describes what the language is agreeing to. It also fits how Cufet names things — `rabbit`, `bury`,
`stash`, `book` are all evocative rather than literal.

★ Measured before choosing: `source` appears 48 times in `examples/` and the prelude and `block` 12,
against 0 for `axiom`. `block` also collides with the language's own word for a `… Done.` structure,
and `expression` is a core term used 72 times in GRAMMAR and REFERENCE. `code` is a mass noun — *a
code* is a cipher, which is the sense `huffmancoding.cufe` already uses it in.

**Square brackets**, which appear nowhere else in the language. They earn the last free delimiter
because this is the one construct whose content is not Cufet at all: anything reusing existing
punctuation would need disambiguating by context, which is what you least want around foreign text.

⚠ **The tag cannot be dropped.** `Define a c-language axiom x as […]` may shorten to
`Define c-language x as […]` — the brackets say "axiom" — but not to `Define x as […]`. The
brackets say *this is verbatim foreign text*; they cannot say *which* language, and the tag names
the consumer. Making it inferable from what happens to be pulled would make a line's meaning depend
on scope above it, and break the moment both `c-language` and `sql` are pulled.

### Splicing: values stay values, and the marker is `the`

Parameters are declared the way every Cufet function declares them, and referred to inside the
axiom by **the article**:

```
Define c-language open-file, given (the text path, the number flags),
    as [open(the path, the flags)].

Define handle as cast open-file on (config-path, read-only).
```

★ **`the path` is never valid C or SQL.** That is what makes a symbol-free marker unambiguous —
it is English sitting in code that is not English, so nothing has to be escaped or disambiguated.
It also reads as the line above it: `the text path` in the declaration, `the path` in the body.

⚠ Rejected markers, with the reason each failed: `?` says nothing about which argument goes where;
bare `x, y, z` collide with the foreign language's own identifiers and break the no-single-letter
rule; `#path` collides with the C preprocessor, which a C axiom will very often open with; `{path}`
is taken twice in Cufet (interpolation, object construction) and is C's commonest punctuation —
`{x}` is also a valid scalar initialiser. `@path` was the best symbol and remains the fallback.

★ **The precedent is `run`.** `Run "grep" with arguments ("-v", "3")` passes a *list of values*,
never a concatenated command string — which is why there is no shell injection there. An axiom does
the same: the text is fixed at definition and only values vary at use, so an axiom cannot be
assembled from strings. The C side receives a marshalled `int`; the SQL side receives a bound
parameter. Neither receives text.

★ The parameter list also supplies what marshalling needs — the C types of the arguments — which
otherwise had nowhere to come from once binding declarations were dropped.

⚠ **Known edge:** `the path` inside a string literal in the foreign text would be substituted —
`[printf("the path is %s", the path)]` has one hole and one piece of prose. Every candidate marker
shares this, so it did not separate them, but it is real.

**An axiom runs when it is returned**, and the declared type decides: a `Bind number to …` whose
value is an axiom runs it and marshals the result, while a `Bind` whose declared type *is* an axiom
hands it back unrun. That is the existing rule that a declared type is what the value must fit into,
and it keeps composition possible — a function that assembles a SQL fragment and returns it still
can. Passing an axiom around does not run it.

### One type for code as data

Quoted Cufet and embedded foreign source live under **one type name**, tagged by language.

★ **The unification is real, not cosmetic: hygiene and SQL injection are the same problem.**
Both are "splice this in as a **value**, never as text". One splicing rule gives macros their
hygiene and SQL its parameterisation, from the same mechanism.

★ It is also the same shape as the pointer design one level up — a block is inert until an
explicit consumer interprets it, exactly as a foreign pointer is inert until an explicit read.

**Two rules, both single:**

1. **The tag names the consumer.** A block is consumed by whoever speaks its language, at
   whatever moment that consumer exists. Cufet's consumer is the compiler, so a `cufet` block
   is consumed at compile time; a database is a runtime program, so a `sql` block is consumed
   at run time. They differ in *when* because they are different programs — not because
   blocks behave inconsistently.
2. **A block is validated as early as its language allows.** Fully for `cufet`, by the
   checker. As far as a supplied validator manages for the rest — which lets a SQL wrapper get
   better at checking over time without the language changing.

Neither timing is a choice. Running a `cufet` block at run time hits the wall below;
executing a `sql` block at compile time has no database to execute against.

⚠ **DSLs bottom out in FFI. They never get their own execution path** — that is the specific
discipline that keeps this from becoming JNI plus JDBC plus annotations, three mechanisms
where one belongs. A SQL wrapper contributes *syntax and validation*; FFI carries it.

### Runtime `eval` of Cufet stays out — the reason that actually holds

Fexprs were already ruled out on Wand's result: no two expressions are ever equivalent, which
takes out `check`, monomorphization, and any compiled backend that is not an embedded
interpreter.

⚠ That is true but it is **not the decisive reason**, and the decisive one should be on
record because it survives disagreeing with the theory:

> A compiled Cufet binary is standalone C from gcc. Running a Cufet block at run time would
> require **a Cufet interpreter written in C**.

"We already have an interpreter" does not transfer — that one is C#, and the compiled artifact
deliberately does not depend on .NET. So the options are a second interpreter in C (three
implementations to keep bit-identical, when two is already the hardest thing here), or
compiled binaries that refuse `eval` (a divergence), or not having it.

★ Note the distinction the earlier framing missed: an explicit `eval` is **not** a fexpr. A
fexpr makes *every* call site potentially non-evaluating, which is what collapses the theory;
an explicit `eval` is visible where it is used and leaves reasoning about the rest of the
program intact. `check` and monomorphization would survive it. The C-interpreter cost is what
does not.

★ **The umbrella survives intact anyway:** a `cufet` block need not exist at run time at all,
because a macro consumes it before the checker.

⚠ **Monomorphization is load-bearing, not speculative** — the prelude ships
`Bind series of element to unique, given (the series of element xs)`, so every program that
calls `unique` monomorphizes. It is the mechanism generics run on.

---

## Two backends, one language

Why the interpreter and the compiler must agree, and what that agreement is standing in for.

- **The interpreter is the oracle, and every disagreement is a bug — settled.** A program's
  compiled output must equal its interpreted output, and a divergence never ships as a
  documented caveat. [CONTRIBUTING.md](CONTRIBUTING.md) states the rule as practice; this
  is the reasoning under it.

  *Why agreement rather than conformance.* Two implementations of a language are normally
  only obliged to satisfy its specification, and are free to differ wherever the
  specification is silent — which is why GCC, Clang and MSVC are all correct C++ compilers
  that produce different programs. That freedom is not available here, for a plain reason:
  **Cufet has no written formal semantics.** The interpreter is the definition. Agreement
  is not a stylistic preference between two peers; it is how a second implementation is
  checked against the only definition that exists.

  *Why there is nowhere for them to legitimately differ.* The C++ answer works because C++
  has a category called undefined behaviour — a place the specification deliberately
  declines to look, where implementations may diverge and remain conforming. Cufet has no
  such category, and that is itself a teaching decision. "This is undefined; consult your
  implementation" teaches a learner a rule that holds until it silently doesn't, which is
  the single worst thing a teaching language can do. Having refused the category, the
  project cannot also claim the latitude that comes with it. The narrow exception is
  behaviour that is genuinely platform-owned — last-ULP `pow`, filesystem enumeration
  order — where there is no single right answer to converge on.

  *What the rule buys.* Every disagreement has a known location and a forced resolution:
  make it precise on both sides, or make the compiler **refuse**. A `CompilerException` is
  an honest admission that one backend cannot yet do what the other does; silence is a
  program that means two things.

  *The cost, stated plainly.* Making the interpreter the definition makes an interpreter
  bug correct by construction. Nothing in the discipline can catch "both backends agree,
  and both are wrong" — that needs a reader with an opinion about what the program *should*
  do, and the rule offers no help in forming one.

  *What agreement does not prove.* Output-equality over a finite suite samples
  observational equivalence over exactly one observable, on exactly the programs someone
  thought to write. Task interleaving, timing, memory use, stack-depth limits and
  non-terminating programs all escape it, and two backends can agree on every printed line
  while differing on all of them. `chance` already forced the edge into the open: random
  output cannot be compared for equality at all, so those tests assert invariants the
  program checks about itself. That is what every one of these tests is really doing —
  the rest just have the luxury of an invariant that reads as "the same bytes".

  *The path from "tested to agree" to "both conform", for whoever wants it.* Widen the
  observables (exit status, stderr, filesystem effects — partly done). Widen the inputs,
  by generating programs rather than writing them. And eventually write the semantics
  down, so both implementations are checked against a definition instead of against each
  other. [REFERENCE.md](REFERENCE.md) is the closest thing that exists today, and the gap
  is exact: it describes what each construct does, rather than defining it.
