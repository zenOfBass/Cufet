# Cufet Design Decisions

Why Cufet is the way it is.

This is the record of decisions that are **settled** — the reasoning behind them, and in
several cases the alternative that was tried and rejected. It exists so that a question
answered once does not have to be answered again, and so that a future reader (including a
future me) can tell a deliberate choice from an accident.

It is not a specification. [GRAMMAR.md](GRAMMAR.md) states the rules precisely and
[REFERENCE.md](REFERENCE.md) explains how to use them. It is not a history either —
[CHANGELOG.md](../CHANGELOG.md) records what changed and when. This file answers only *why*.

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

  ✅ **DECIDED 2026-09-17: that direction is taken, and it is what the book/module distinction
  MEANS.** A **module may own a lifetime; a book may not** — and a person must be able to write
  a lifetime-owning module, not only pull the one the compiler provides. ★★ Every other candidate
  line collapsed under measurement, and they collapsed the same way: they were already true of
  BOTH. Statelessness is the floor for anything pullable (a book with fields and a module with
  fields draw the identical refusal). Instantiation is not it either — a bundled book with a Cufet
  layer is instantiated too. **Lifetime is the only asymmetry the language has**, and today exactly
  one object holds it, through a statement of its own rather than through being a module.

  ✅ **BUILT 2026-09-19 — `and region`.** `Define object workspace with () and region.` opens an
  arena scope on `Pull`, can be told to bury, and enforces the outward-only invariant on the type
  you wrote. `Prelude/rabbit.cufe` says `and region` too, so a rabbit is the FIRST INSTANCE of the
  rule rather than an exception to it — its regionhood is a line in Cufet instead of a branch in
  the compiler.

  ★★ **The cost named above was WRONG, and measuring it is what made the work small.** *"A
  user-definable lifetime means user-definable continuations"* was asserted twice in this entry and
  never checked. MEASURED: nothing in `bury`/`unbury` depends on the region being a rabbit —
  `cd_rabbit` compiles to an EMPTY STRUCT that is threaded into the closure and never read, and
  swapping which rabbit a bury names changes one line of emitted C that nothing loads. The two
  halves were separable all along. The region half came to five small changes plus one predicate,
  and the *"which restriction?"* question never entered into it.

  ★ **What actually paid for it** was the thing this document already claimed: every rule keeping
  a rabbit sound is depth arithmetic, so one increment at a region pull buys the whole
  adversarially-tested outward-only invariant. Nothing new had to be proved.

  ⚠ **What is NOT here, and never was:** `bury` and `unbury` written in Cufet. They are the
  language's floor, compiler-provided the way `If` is. Nothing is privileged — any region gets
  them — but a person cannot implement one, and that promise belongs to *the compiler, written in
  Cufet*, which names `bury`'s state-machine transform by name. This line was about WHICH
  ASYMMETRY the distinction means; it never borrowed that promise.

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

- **No enums. A closed union already is one, and a stronger one.** The property worth having is
  not the syntax — it is that the compiler proves every case is handled, and `Judge` over a closed
  union proves exactly that: `Otherwise` becomes optional and a missing case is a static error
  naming the case. An enum would be a second closed-set mechanism doing what the first already
  does, and it would fork `Judge` into two stories about exhaustiveness instead of one.

  ```cufet
  Define object red.
  Define object green.
  Define object blue.

  Bind text to name-of, given (the (red or green or blue) light):
      Judge light, where it is:
          A red, return "red".
          A green, return "green".
          A blue, return "blue".
      Done.
  Done.

  State cast name-of on (a new green).
  ```
  ```output
  green
  ```

  ★ A case that carries nothing says so once — `Define object red.` and `a new red`, with no
  `with ()` and no `{ }`. That ceremony was the friction that would have made someone ask for
  enums, and removing it was a parser tweak rather than a feature.

  ⚠ **No ordinal or ordering either**, and that is the same decision rather than a separate gap.
  It is rarely what anyone actually wants from an enum, and a method on the object gives it to
  whoever does.

---

## Objects: lifetime and accessors

What an object owns, and what a property may do.

- **Unmaker close/flush companion convention** (guidance, prevents silent data
  loss): `unmake` is the infallible last-resort backstop. For cleanup that *can* fail
  (flushing a buffered writer, committing a transaction), the object should expose a
  fallible method (`close`/`flush`/`commit`) and the caller handles the failure
  *before* the object's scope ends. Relying on `unmake` alone to flush risks silent
  data loss — the unmaker swallows all outcomes. (Same pattern as Rust `Drop`+
  `.close()` / Java `Closeable.close()`+`finalize`.)

- **Unmaker ownership rule:** `unmake` closes what the object *opened*, not what
  it *borrowed*. A resource injected from outside is owned by the caller — closing it
  in the unmaker is a double-close bug.

- **Unmaker FIRE-TIMING — settled 2026-07 after ~19 probes. Do not re-derive it.** An unmaker
  does not fire for a binding declared directly in a function or method FRAME, nor at the
  program's top level, and it may fire more than once for one logical object. MEASURED in both
  backends. It looks like a bug every time somebody meets it; REFERENCE documents the behaviour,
  and this is why it is the behaviour.

  The decision was MATCH-EXACTLY: replicate the interpreter's block-scope LIFO firing precisely,
  *including* the value-copy and escape double-fires and both gaps. ★★ Four reasons, and each one
  kills the obvious repair:

  1. **Deterministic, so sound and oracle-able as it stands.** Block-exit LIFO is the same shape
     the open-files cleanup stack already uses.
  2. **The double-fire IS the language.** Cufet objects are value types with NO IDENTITY, so an
     unmaker is a per-binding HOOK and N copies mean N unmakings. ⚠ Exempting "the returned
     binding" — the obvious narrow fix — requires exactly the identity the language declines.
  3. **The frame gap is LOAD-BEARING.** Firing at frames would unmake a returned local WHILE IT IS
     BEING RETURNED. Closing it properly needs escape analysis.
  4. **Unmaking is NOT deallocation.** The arena owns all memory and exposes no `free`, so an
     unmaker body is ordinary user code — which is why a repeat is observable but SAFE.

  ⚠ **FFI resources are not affected the same way**, which is worth knowing before anyone
  reopens this: foreign releases are a FLAT list with a per-block base, so one acquired inside a
  function body still runs at the nearest enclosing block — late, not never. Unmaker bindings live
  in per-scope dictionaries and are simply dropped.

  ▶ **To reopen it is to take the escape-analysis arc**, not to write a narrow rule. It shares a
  blocker with move-semantics-at-channel-send: a way to say *"this binding is spent."*

- **Fields may have a DEFAULT; parameters may not — and the asymmetry is the point.**
  `the number age with default 0` lets a construction site leave the field out.

  ★★ **The invariant is kept rather than loosened.** Every field is still set on every object;
  an object has no unset state. What changed is only who wrote the value down. That matters
  because the pressure here is the opposite of C#'s, which added `required` and `init` to retrofit
  mandatory fields onto defaults-everywhere — starting from "everything must be supplied" and
  making individual fields optional keeps the property that made it worth having.

  ★ **The default is an EXPRESSION filled in at each construction site**, never a value computed
  once at the definition, so `with default a series of number` gives every object its own. The
  mutable-default trap is answered by construction rather than by a rule forbidding it. The
  checker fills them onto the literal, so neither backend learns that defaults exist.

  ⚠ **Parameters did NOT get the same spelling, and it is not an oversight.** A parameter's arity
  is read by OVERLOAD DISPATCH — several versions of one name told apart by argument types, plus
  `when` clauses — so an optional parameter changes what "the same signature" means, and two
  versions that differ only in an optional tail may or may not be distinguishable. A field has no
  such entanglement: it is named at construction and nothing dispatches on how many were given.
  That interaction wants deciding before the syntax is copied across.

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
  `vehicle`/`car`). ⚠⚠ **This used to read "singleton and statelessness are
  loader-enforced conventions", and both halves were wrong.** MEASURED
  2026-09-17: nothing enforces either one, and neither distinguishes a book from
  a module in any case — a book with fields and a module with fields draw the
  same refusal, *"a pull has nowhere to put their values"*. Statelessness is the
  FLOOR for everything pullable, not a line between the two. The line is
  LIFETIME, and it is recorded under *What Cufet is for*. The `module`
  interface can be built early as the stable seam; the real external-code
  loader comes later without touching program code.

- **Text orders ordinally, and `<` on text stays refused.** `sorted` on a series of text gives
  code-point order — `(Apple, apple, banana, pear, Ápple)` — while `"a" is less than "b"` is
  refused outright: *"ordering works on numbers and on bits."*

  ★ **Ordinal is the order that costs nothing to agree on.** It is locale-free, so both backends
  produce it by construction rather than by sharing a table — the same class of problem the shared
  case table solves the hard way, avoided here by never having it. A locale-aware order would make
  the answer depend on the machine, and the oracle is structurally blind to that: every machine
  that runs the suite is en-US, so a per-machine difference would ship unseen.

  ⚠ **The refusal on `<` is the other half, and it is a teaching decision.** `"apple" is less
  than "Banana"` is false in code-point order and true in every dictionary a learner has met.
  Offering the operator would teach the wrong rule silently. `sorted` is where ordering is the
  stated point, so ordering lives there and nowhere else.

- **An `Otherwise` arm must say something real — there is no no-op statement.** Cufet has none:
  `pass` exists only in `or pass the failure off`. ★ Requiring the arm to say something is what
  makes coverage mean *you thought about the remaining cases*; a no-op is the `catch {}` of case
  dispatch. Revisit only if writing real statements in ignore-this arms becomes a genuine
  irritation rather than a hypothetical one.

- **No membership comparison (`is any of`), and if one ever arrives it must be a COMPARISON.**
  `If x is 1 or x is 2` says it today. ⚠ The constraint that matters is on the shape, not the
  need: `Define maybe as any of (1, 2, 3).` would import Raku-style junctions as VALUES, whose
  threading order is explicitly undefined and therefore incompatible with the no-divergence rule.
  A comparison is expressible; a junction value is not.

- **`text` is OPAQUE — no character-level indexing, and casing is invariant.** The everyday
  toolkit is complete (join, measure, convert both ways, split, search, find, slice, replace,
  case, trim). What is absent is absent by decision rather than by omission: `in uppercase` is
  invariant and simple, so `ß` stays `ß` rather than becoming `SS`, and there is no way to index a
  character out of a text. ★ Locale-aware casing is the same per-machine hazard the shared case
  table exists to neutralise — see *Two backends, one language*. A character sequence you can
  index is `chase`, which the `collections` book hands out, and that separation is the point.

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

### The pattern engine is an automaton, and is written in Cufet

`regex` ships a Thompson automaton, never a backtracking matcher, and the engine lives in
`src/Interpreter/Prelude/regex.cufe` rather than in either backend. Both choices are forced, and by
the same thing.

★★ **The engine is Cufet because the ORACLE requires it.** Reaching for a ready-made engine on each
side — .NET's `Regex` interpreted, POSIX `regcomp` compiled — would have shipped two dialects that
disagree about greediness, character classes and empty matches: silently, on inputs nobody thought
to test. One implementation spliced in before either backend sees a program has nothing to diverge
from. `collections` is the precedent.

★★ **An automaton because backtracking cannot be made deterministic here.** A backtracking engine
can hang forever on a pattern that looks fine, and the usual defence is a timeout — which is
nondeterministic exactly where this project demands byte-for-byte agreement. ★ The third reason is
specific to Cufet and is the decisive one: **the engine is itself written in Cufet**, so
backtracking's worst case on a slow host is not "slow", it is "never finishes".

⚠ That decision is what rules out **backreferences**, permanently rather than pending a trigger —
along with lookahead and lookbehind, which are precisely the features that make a "regular
expression" not regular. All three need the backtracking this book declined at the start.

⚠ **CAPTURES are the one exception, and are refused as NOT YET rather than CANNOT.** The three
above need backtracking and so can never arrive; an automaton can carry captures, at real cost in
complexity. Nothing has asked for them — but do not read the list above as covering them, because
the reason that kills the others does not apply.

★ **A book on a LANGUAGE does not choose its own contents.** `[a-z]` and `{2,5}` mean what they mean
in regex everywhere, and a book that cannot spell them is a Cufet-flavoured subset wearing regex's
name on the cover. **Trigger discipline governs what Cufet INVENTS; FIDELITY governs a book.**
Refusing unbuilt metacharacters BY NAME was the honest IOU in the meantime — it paid off five times
without one written pattern changing meaning.

### Named loops stay out, and patterns are why

`Stop.` and `Skip.` act on the innermost loop, and there is no label to make either act on an outer
one. That was originally *no demonstrated need* — an absence, which rots. It is now a reason.

**MEASURED 2026-09-13, across 48 examples and 3 tools** including a triple-nested sudoku search: 5
uses of `Stop.` and 11 of `Skip.`, and **not one wants to escape an outer loop**. The deepest
nesting escapes with `return` from inside a function, and extracting a nested search into a named
function is usually better than labelling the loop anyway.

★★ **Two programs were written to hunt the witness, and failed for DIFFERENT reasons — which is
what turns an absence into an argument.**

- `examples/algorithms/beamforming.cufe` could not produce one structurally. Its loops ACCUMULATE —
  visit everything and sum — so by construction there is nothing to leave early.
- `examples/parsing/readings.cufe` was written to the shape that should have produced one: a CSV
  validator, predicted in advance to want a *continue-outer*. **No nested loop appeared at all.** A
  row's fields are judged by checks that share no rule, so nothing loops over them — and the
  character-level scanning that WOULD have looped is what a pattern is for.

★★ **So `regex` structurally removes the shape that would want a named loop.** A continue-outer
needs a nested scan, and in a language with patterns the nested scan does not get written. That is
why five real programs have now failed to produce a witness.

⚠ **The one flag that looks like the workaround is not one.** `tools/shell.cufe` sets `broken` in
its redirect parse, but the loop is single and the flag's job is to carry *"this failed"* PAST the
loop's end — which no label would remove. Check for that shape before reading a flag as evidence.

⚠ **What would reopen this**, and it is a program this project has already committed to writing: a
hand-written LEXER, walking characters one at a time — the one scan a pattern cannot do for you —
advancing position, line and column together. Extraction resists hardest when an early exit must
also advance several scalars, because records and objects copy while only collections are
reference-typed. If the compiler-in-Cufet does not want a named loop either, that is close to
decisive.

★ And the argument none of this touches is **readability, not capability**: `Stop.` silently means
"the innermost one", and in a triple-nested loop the reader has to count. An optional label could
make *existing* code clearer without enabling anything new. That is the case that could still carry
the feature even if no program ever strictly needs it.

---

## The build description, and what a project is

Shipped 0.23.0. The decisions are here because the entry that argued them is gone; the history is
in the changelog.

**A blueprint PRODUCES A PLAN — it performs nothing.** Running the file compiles nothing; what
comes out is a value, and `cufet build` walks it. Zig, CMake, Bazel and Gradle all converged here,
and performing is what a `build.sh` does. It is also what lets a whole graph be refused before
touching disk, and what lets a tool list a project's targets without building anything.

★★ **The graph is never written down.** A step declares `needs` and `makes`; a step that needs a
path another step makes runs second, and nothing anywhere states an edge. Staleness is by content,
so inputs must be declared regardless — declaring edges too would state one fact twice, and the two
could then disagree with nothing to catch it.

**A step is a structural record, and `step` is a NAME for that shape.** A book cannot hand out a
NOMINAL type without native machinery — `BookLoading.MakePrivate` renames whatever a book layer
declares, which is why `matrix` and `chase` are C# types. A shape needs none of that: record typing
is structural, so `a record with (the name …, …)` builds one and the longhand spelling stays
interchangeable. ⚠ The rule that said a book "cannot hand out a type" was wider than its reason.

**`Pull a book on blueprints.` is a GATE, not a library.** The record shape does not need it. It is
there so a file can declare what it IS, which is what lets `cufet build` refuse a blueprint that
does not say so, rather than treating a bare function name as magic.

★ **A book, not core.** Putting a build vocabulary into the language — targets, steps, artifacts,
toolchains — would spend permanent surface and move away from what Zig gets right: a build script
is an ordinary program using an ordinary library. Two consequences follow and were both measured.
A blueprint computes its own paths, so `the environment variable "OS"` supplies a binary's
extension and no CLI output flag is needed. And a blueprint never needs to say a file is a LIBRARY:
libraries are not built, they are pulled by the programs that are, and a build wanting one checked
runs `cufet check` as an ordinary step.

★ **How a blueprint reads was worked until it was right.** `step` as a name for the record shape,
and returning a series literal rather than building one up, took this repo's own blueprint from 82
lines to 39. ⚠ Two further reductions were weighed and DECLINED, recorded so they are not
re-derived:

- **Inferring `makes` from `runs`.** Derivable for a `cufet build` step and nothing else, so it
  would make the build book know one program's behaviour, and go silently wrong if that behaviour
  changed. ⚠ `needs` cannot be inferred that way at all — `terminals.cufe` is a dependency only
  because `shell.cufe` PULLS it, which is what `cufet pulls` exists to answer.
- **A step-constructor in the book.** The first item of exactly the build vocabulary this design
  declined to spend. ★ A helper written in the blueprint ITSELF needs no language change and is
  what `build.zig` does — MEASURED at two steps it costs about twelve lines to save fourteen, so it
  starts paying somewhere around five.

★★ **A project is a directory holding `blueprint.cufe`**, and only the file's LOCATION is read —
never its contents. Every tool that resolves a pull needs the project root, and none of them should
execute a build description to import a book. Finding a file is a walk up the tree; running one is
arbitrary code.

### Staleness is by content, and the fast path is the unsound one

**Content hash, never timestamps.** A timestamp answers *"was this touched"*; a build wants *"is
this different"*. They differ at the edges — a file touched without changing, or a change inside
the filesystem's timestamp resolution — and the failure is a wrong build with no complaint, which
is the category this language declines everywhere else. Decisive: **git does not preserve
modification times**, so a timestamp answer depends on how the tree arrived on the machine.

⚠⚠ **The standard optimisation is the unsound thing.** Stat first and hash only when size or mtime
changed is what every fast build system does, and it silently reintroduces the resolution bug as a
"fast path". Written down so nobody adds it later as an obvious win.

⚠ The algorithm is NAMED by the language rather than taken from the platform — FNV-1a, written out
on both backends and pinned to published vectors. The lesson is the shared case table's: .NET
casing turned out to be ICU-backed and to differ per machine.

★★ **A build that rebuilds too often looks exactly like one that works.** This is why the skip is
never tested alone: an over-declared dependency, a signature compared after being reconstructed, a
step with no outputs — each produces correct output forever while doing far too much work. Only the
three ways of going stale, tested separately, say the skip was decided rather than guessed.

### A blueprint can maintain itself

`the contents of the directory` is sorted and identical on both backends, so a blueprint can
enumerate its own tree and generate a step per file. `cufet pulls <file>` supplies the other half —
which FILES a file brings in, reported from what the loader resolved rather than from re-reading
the source. Together, a blueprint that names no file and no dependency rebuilds correctly.

★ Convention-over-configuration needs no config FORMAT here, because the build file is a program:
the loop is the convention and an `If` is the exception, in one language.

⚠ Two shapes were declined for `pulls` and should not be re-derived. A **book member** would need a
C implementation, since a native member is emitted into compiled programs — and answering *"what
does this file pull"* means parsing Cufet, which the C runtime cannot do. An **axiom** hits the same
wall: it is a way to call C, and C still cannot parse Cufet. A Cufet-side heuristic that matches
`Pull ` works and proves no language surface was needed, but it under-declares on any form it does
not know, and an under-declared `need` is a silently stale build.

### Where a book comes from, and what a pin is

The package manager, settled across 2026-09-13 to 09-18 and shipped. Recorded here because the
reasoning is what nobody should have to re-derive; what it DOES is in the changelog and in
[BOOKS.md](BOOKS.md#pinned-books-cufet-install).

★★ **One constraint decides nearly all of it: ONE BOOK PER NAME in a program, wherever it came
from.** `MakePrivate` renames what a book declares to `<name> in <book>`, so the name IS the
namespace, and two versions of one book cannot coexist by construction. Cufet therefore needs
version SELECTION and never isolation — npm's nested private copies are structurally unavailable.
★ Python is the precedent rather than npm: `site-packages` is flat with one version per name, and
virtualenvs exist because of it. Cufet already has the virtualenv — `books/` is per-project by
construction.

What follows from that constraint alone:

- **Exact pins, so there is no resolver.** No constraint sublanguage and no negotiation. A diamond
  at two commits is a REFUSAL naming both, which is the answer this language gives everywhere else,
  rather than npm nesting two copies or pip taking whichever landed last.
- **`books/` stays FLAT**, one file per name on disk, mirroring one book per name in the program.
  ⚠ The old complaint that *"two books cannot carry different versions of a third"* DISSOLVES here:
  it is not a folder problem and no folder shape fixes it.
- **Dependencies are DERIVED, not declared.** `Pull a book on canvas.` IS the declaration, and
  `cufet pulls` reports what the loader resolved. A manifest listing dependencies would be a second
  copy of something derivable. What cannot be derived is ORIGIN — which source, which commit — and
  that is exactly what a pin carries and nothing else.
- **A book is a PROJECT when you develop it and a FILE when you consume it.** Its own
  `blueprint.cufe` carries its pins and travels only as far as the installer; only the `.cufe` lands
  in `books/`. ⚠ Forced rather than chosen: a blueprint inside `books/` would mark that directory a
  project root.
- ★ **Fetched versus written is then free** — anything in `books/` was fetched, because a book you
  wrote lives beside the file that pulls it, where *nearest wins* already puts it first.

★★ **A SOURCE IS A GIT REPOSITORY AND A COMMIT SHA, and the pin format and the transport are
COUPLED — which is what decided it.** A pin recording a CONTENT CHECKSUM cannot be fetched by git:
MEASURED in this repository, `core.autocrlf=true` with no `.gitattributes`, so a clone rewrites LF
to CRLF and a fetched book's bytes differ from the publisher's. Hashing after normalising "fixes"
that by discarding what a checksum is for. A commit sha names the commit rather than the working
tree, git guarantees it, and it stays true whoever fetched it — so the transport can change later
without invalidating a pin already published. ★ Both ecosystems set that precedent: `go get` shelled
out to `git` for years before `GOPROXY`, and pip still does for `pip install git+https://…`.

⚠ **What that costs, recorded so nobody rediscovers it as a surprise:** a consumer needs `git` on
PATH; a fetch failure arrives in git's vocabulary rather than Cufet's own voice; and two
machines with different autocrlf settings end up with different BYTES in `books/`, so `blueprints`
sees a changed input and rebuilds when nothing changed. That last is wasted work rather than a wrong
answer, but this project has a line about exactly it — *"a build that rebuilds too often looks
exactly like one that works"*.

⚠⚠ **The installer RUNS a fetched blueprint, and the loader never does.** *"Only its LOCATION is
read"* constrains the LOADER — `check`, `run`, the editor — so importing a book can never execute a
build description. An installer is a build-time tool and by the time the loader runs, `books/` is
already populated. The exception is deliberate, so that a blueprint may COMPUTE its pins, and the
installer names each blueprint before running it.

★★ **Which is precisely why there is a record.** Because pins can be computed, the closure is not a
function of the files at all — MEASURED: one blueprint, unchanged and at one commit, pinned a
different book depending on whether it ran inside a git repository. `.cufet-pins` is the tool's own
file and never `blueprint.cufe`: writing a closure back into the source you wrote is `go mod tidy`,
and every ecosystem keeping a lock splits the two for that reason. Deleting it is the escape hatch,
with no flag, exactly as `.cufet-build` offers the build.

⚠ **The honest limitation underneath all of it.** `Resolve` accepts exactly `<name>.cufe`, so a
book is precisely one file, and a book outgrowing one file must split into OTHER books that take
global names. Two libraries each needing a different `utils` cannot coexist — they are refused
rather than silently merged, which is the right answer to one-book-per-name, and it is still a
limit. ★ This is the one thing that could argue for per-book directories or a real namespace,
against the flat shape above.

---

## Tooling

What the toolchain is, and what it declines to become.

- **GENERATED PAGES — BUILT, 2026-09-18.** `cufet page` writes a reader's page for a book. What it
  does is in the changelog; the decisions are here.

  ★★ **The generator is small because the language did the work.** A Cufet signature is English, so
  a page's declaration line is the declaration copied out and there is no rendering of types into
  prose — which is most of what a documentation generator normally is. A book is an object, so the
  member list is one the checker already holds.

  ★ **It prints to stdout and decides nothing about where a page lives.** *Where is it published*
  sat as an open fork for weeks alongside a hosting question that had already been answered. It was
  never a question the tool should have an opinion about: `check`, `tokens` and `pulls` all report
  and let you redirect, and doing the same dissolves the fork rather than answering it.

  ✅ **Pages are for books, not for the bundled ones — and the premise that made this a fork was
  wrong.** It was claimed that `BOOKS.md` and the `///` comments *"say the same things"*. MEASURED:
  they do not. `BOOKS.md` is an INVENTORY plus book-level behaviour (*"Each yields void for an empty
  series"*); the `///` carries per-member semantics (*"`-2.5` floors to `-3`, not `-2`"*) that appear
  nowhere in `BOOKS.md`. They are complementary, so there was nothing to generate away. ★ The only
  real staleness was the inventory, and `BookDocumentationTests` pins it in both directions.

  ⚠ **A book whose surface is SYNTAX or a native member has no page**, and says why rather than
  claiming not to be a book — `blueprints` carries no `Define object` on purpose, because a Cufet
  layer would put its native `checksum` out of reach. ⚠ This is not a policy about bundled books:
  `math` is bundled and pages perfectly well. What is missing is a declaration to read.

  ⚠ **Parameters cannot be documented individually**, because the language has no syntax for it. A
  page lists a signature and cannot annotate its arguments. Stated so nobody reads it as a bug.

- **A LANGUAGE SERVER — DECLINED, 2026-09-17.** Not deferred. It sat under *Deferred* for months
  waiting on a trigger, and the last candidate has now been ruled out too.

  ★ **The front end reports at most ONE error.** A run stops at the first one, so a report is that
  error plus whatever warnings were collected on the way — each already carrying a line, a column
  and a prose explanation. A language server exists so that incremental, resident analysis pays for
  itself across many diagnostics. There is nothing here for it to earn back.

  ★★ **And the editor already has both halves of what it would provide.** `cufet check --json`
  writes one diagnostic per line, so the squiggles are the front end's OWN answers rather than a
  second implementation that could disagree with it; `cufet tokens --json` writes the semantic kind
  of every name it can place, which is the other thing an editor normally gets from a server. A
  grammar cannot tell a type from a parameter in Cufet, so that half had to exist regardless.

  ⚠ **The last candidate trigger is gone.** Building a blueprint's steps ALGORITHMICALLY looked
  like the one thing that might want a resident index, and it does not: a blueprint is an ordinary
  Cufet program, so it gets the same one-error report as everything else.

  ▶ **What would reopen it:** wanting go-to-definition, completion or rename. Those genuinely need
  a resident index and nothing else here does — and nobody has asked for them.

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

- **A task's fault reaches its rabbit, and an exception does not become a failure on the way
  (2026-09-08).** Three rules, one sentence each, and the third is the one that was weighed and
  declined.

  *A fault nobody claimed surfaces at the rabbit's `Done.`* The rabbit already owns the tasks and
  joins them, so it is the thing to tell. A worker that ended the process instead cut the rabbit's
  own body off mid-sentence — measured at 48 to 87 lines out of 200, a different number every run.
  Several failures are reported together, in the order the tasks were **started**: completion order
  would make the same program print different text on each backend, which is a worse bug than the
  truncation it replaced because it looks deliberate.

  *Reading a result takes ownership of its failure.* A task whose result was awaited is that
  awaiter's business, so the join does not announce it again — without that rule, catching a task's
  fault at the await and suppressing it still ended the program. Fire-and-forget needs no separate
  rule: it has no result to read, so it is never claimed and always reports.

  ★★ *An exception does NOT become a failure by crossing a task boundary — weighed and declined.*
  The tempting version makes every `the awaited result of` a `T or failure`, so an awaiter is
  compelled to handle whatever the task might do. It was declined on consistency: `1 / n` does not
  make you prove `n` is not zero anywhere else in the language, and a division by zero should not
  change category because it happened one thread over. Awaiting would have become the single place
  where a possible exception is forced into a type.

  ⚠ The argument on the other side is real and was not dismissed: you can see `1 / n` in front of
  you and cannot see inside a task body from the await site, which is exactly why concurrency is
  different. What settled it is that the parent is *already told* — the fault is raised at the
  await, and a `Try` there catches it exactly as it would locally. The choice was between telling
  and compelling, not between telling and silence. A deliberate `return a failure` is unaffected:
  that rides in the type as it always has, and the await must still handle it.

- **A repeating task form — BUILT AND REMOVED THE SAME DAY, 2026-09-20.**
  `Have rabbit start a task, repeat: … Done.` made a task's body a loop: `Stop` ended it, `Skip`
  took the next turn, and an undeclared fault ended the turn rather than the program. It worked on
  both backends and was removed anyway. **Do not rebuild it without answering what follows.**

  ★★ **The justification was "let it crash", and the analogy does not survive contact.** That
  pattern is valuable because a crashing process **loses corrupted state and restarts clean**. A
  repeating task has no state to lose: each turn is a scope, so a body-local resets, and the place
  a loop normally keeps a total — *above* the loop — does not exist, because the body **is** the
  loop. Nothing to corrupt and nothing to restore, so the argument was borrowed rather than earned.

  ★★ **MEASURED: a `While` loop with a `Try` inside an ordinary task already does more.** It
  survives the faulting job, keeps a tally, *and counts what it dropped* — verified identical on
  both backends. The repeating form could not produce that last line at all. Its one distinctive
  behaviour was swallowing a fault **silently, with no way to observe it happened**, which is a
  misfeature rather than a feature.

  ⚠ **And the obvious fix converges on what exists.** The cure for the tally gap is a preamble
  before the loop — at which point the construct *is* a loop inside an ordinary task.

  ★ What it did buy was real but small: the receive written once instead of twice, since a
  `While`-shaped worker must prime the pump and advance at the bottom. Not worth its price, which
  was a **narrowed grammar** — `Have rabbit start a task, Repeat: … Until x.` was legal before —
  plus a new form in two documents and a capture rule existing only to serve it.

  ★ **It was not wasted.** Building it surfaced a live divergence reaching back to 0.23.0: a rabbit
  ran its own scope's unmakers *before* joining its tasks, destroying rabbit-local objects while
  the tasks that could still read them ran. That fix stayed. See the CHANGELOG entry.

---

## Foreign interoperability

How anything that is not Cufet gets reached from inside Cufet — C libraries, and source
written in other languages. Designed 2026-08-21; **the first slice — an axiom returned as a
number — runs on both backends, and everything else here is still design.** The
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
it. The FFI item always described "an explicit address-of", so the word was already in play.

★ It costs nothing. `address` and `pointer` both have zero uses across `examples/` and the prelude,
and a type name does not have to be reserved — `rabbit` appears nowhere in the lexer and is
resolved in the type checker instead. So `the text address` stays available as a field name.

⚠ There is no address-*of* operator. Cufet never creates an address: one only ever comes back from
C and goes back into C. See "Cufet never models a C struct".

```
Define c-language close-file, given (the address handle), as [fclose(the handle)].
Define handle as cast open-file on (path, "r").      ← voidable address; NULL is void
```

### One concept, and it is inert

There is **one kind of foreign pointer**: opaque, rabbit-scoped, and impossible to
dereference implicitly. Reading through it is an **explicit act that always copies into the
arena** — `the text at handle` yields rabbit-owned text, never a view into foreign memory.

★ **`the text at <address>` is the only read there is**, and it yields `voidable text`. Reading a
struct or a scalar was considered and is unnecessary: an axiom can project a field
(`[readdir(the dir)->d_name]`) or declare a local and return it
(`[int status; waitpid(the pid, &status, 0); return status;]`), so the value comes back as an
ordinary return rather than through an address. Text is the one case with no single-expression
answer on the C side, because the bytes belong to C and have to be copied out.

★ **Every address coming back from C is `voidable address`.** NULL is C's universal failure signal
— `fopen`, `malloc`, `getenv`, `opendir` all use it — so every way C can fail lands in the
mechanism the language already has, and the checker will not let it be skipped. No new failure
concept anywhere in the FFI.

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

⚠ **A subprocess pipeline BUFFERS, and that is what keeps the two backends agreeing.** `run A |
run B` runs each stage to completion and hands its whole output to the next, in both backends
identically. Streaming would be faster and would let `yes | head -1` finish — but streaming is
observable through TERMINATION rather than merely through output order, so a streaming compiler
against a buffering interpreter is a divergence that HANGS rather than one that prints the wrong
thing. ★ Contrast the FUNCTION pipe, where the two legitimately differ (buffered interpreted,
one thread per stage compiled) because each channel is FIFO and the observable order is therefore
the same. The subprocess form has no such guarantee, so it buffers on both sides until it can
stream on both.

⚠ **Headers and LINK FLAGS are one feature, not two.** The bundled header set covers what links
by default, so binding a library of your own is the gap — and shipping headers alone would be
worse than shipping nothing: `#include <sqlite3.h>` gets the declarations and then fails at
"undefined reference", i.e. a feature that cannot work for the case that motivates it.

**FFI does not ship until both backends run it.** The interpreter is the oracle; FFI is the
one area where being wrong means memory corruption rather than a wrong number, and it is the
last place to give up a second opinion.

⚠ The playground runs the interpreter in **wasm**, where FFI cannot work at all. "This
program cannot run in this environment" is therefore a required outcome regardless.

### Cufet never models a C struct

There is **no C-compatible record type, no layout question, and no address-of operator.** A struct
is C's idea, so struct work happens in C:

```
Define c-language set-raw-mode, given (the number fd),
    as [struct termios t;
        tcgetattr(the fd, &t);
        t.c_lflag &= ~(ICANON | ECHO);
        tcsetattr(the fd, TCSANOW, &t);
        return 0;].
```

One axiom, the whole job. The struct is declared, addressed and mutated where structs are cheap and
correct by construction, and what crosses back is a plain return value.

A struct that must **persist** across calls — saved terminal state to restore on exit — is held as
an opaque `address`, allocated in one axiom and `released by` another. Cufet stores it and hands it
back; it never looks inside.

★ **This is the same argument that removed struct reads and scalar reads**, applied once more.
Every out-parameter, every field access, every layout question is answerable in C, by the person who
is already writing C. Modelling any of it on the Cufet side would be building a second, worse C.

**What it removes**, all of which earlier drafts of this section carried:

- C type names inside Cufet (`the int input-flags`) — the boundary has Cufet types only
- struct layout, alignment, and the question of who computes offsets
- `the address of <value>` — an address now only ever comes *from* C and goes *back* to C, so
  Cufet never creates one and needs no operator to
- reading a struct or a scalar through an address — `the text at` is the only read there is

⚠ The rejected alternative is recorded because it will look tempting: declaring C structs in Cufet
and having the interpreter compute layouts from standard alignment rules. It works for scalars,
arrays and nested structs and quietly gets **bitfields, packed structs and unions** wrong —
silently, which is the failure mode this project keeps refusing.

### The shim is the compiled axioms

The shim is not a fixed prebuilt library and not generated from declarations — **it is the
program's axioms, compiled.** The compiled backend pastes them into its own C; the interpreter
compiles and loads them. One artifact, and no separate declaration language to keep in step with it.

**The cost:** interpreted FFI needs a C toolchain. Two things make that mild:

- **It caches.** `RuntimeCache` already content-addresses compiled objects by a SHA of source,
  header, gcc identification and flags. A shim keyed the same way means gcc runs **once per
  distinct set of axioms**, not once per run.
- **A book's axioms can ship precompiled**, so a bundled `c` book works with no toolchain at all.

> **The constraint, in full:** interpreted FFI needs a C toolchain the first time a given set of
> axioms is seen, and not at all for bundled books. Wasm cannot do it in any case.

### `c-language` is the tag; a bundled collection is optional

Two things could be called "the C book" and only one has to exist.

- **`c-language` is the tag book.** You pull it to write C axioms at all. It is the language, not a
  library of anything.
- **A bundled collection of ready-made axioms** — common POSIX calls, say — is *optional*. Axioms
  live wherever they are defined: at the top level, or inside a module a writer wrote. A "book of C
  bindings" is now just a module that happens to contain axioms, with no special status.

★ That dissolves a gap the earlier design had and never closed: if a `c` book were **bundled**,
writers could not add to it, because `unto` may not target a bundled book. Under axioms there is
nothing to add to — you write your own.

**If a bundled collection does ship, it is one book** — not split by domain and not split by
platform. The arguments against that were both wrong and are recorded so they do not come back:

- *"libc is too big"* — such a book holds what was actually **bound**, not all of libc, and members
  are reached as `c's read`, so a large book pollutes nothing at the pull site.
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
    Bind number to process-id, get-pid.
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

### Naming a language book

By ear — the language's common name, qualified only where the bare name needs it:

```
Pull a book on the c-language.      ← `c` alone is a single letter
Pull a book on sql.                 ← `sql-language` says language twice
Pull a book on regex.
```

★ The constraint that survives in every case: `c` on its own is a single letter, which the style
rule refuses, so C is qualified regardless of what the rest do.

⚠ A mechanical `<name>-language` rule was the alternative and was not taken. It buys consistency —
you always know how to spell it, and the suffix says *this is a foreign language book* at a glance —
at the cost of reading redundantly wherever the name already ends in "language", which SQL and HTML
both do. The judgment is per language now, so someone adding a binding decides rather than looks up.

### Splicing: values stay values, and the marker is `the`

Parameters are declared the way every Cufet function declares them, and referred to inside the
axiom by **the article**:

```
Define c-language open-file, given (the text file-path, the number flags),
    as [open(the file-path, the flags)].

Define the number handle as cast open-file on (config-path, read-only).
```

★ **`the file-path` is never valid C or SQL.** That is what makes a symbol-free marker unambiguous —
it is English sitting in code that is not English, so nothing has to be escaped or disambiguated.
It also reads as the line above it: `the text file-path` in the declaration, `the file-path` in the
body. ⚠ `path` itself is RESERVED (`the path <p> exists`), so it cannot be a parameter name.

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

⚠ **Known edge:** `the file-path` inside a string literal in the foreign text would be substituted —
`[printf("the file-path is %s", the file-path)]` has one hole and one piece of prose. Every candidate marker
shares this, so it did not separate them, but it is real.

### ⚠⚠ Splicing cannot reach `regex`, and the reason is the marker's own reason

A pattern takes no parameters — `Define regex p, given (…)` is refused, with a test. That is not a
temporary limitation awaiting a slice; it follows from why `the` works everywhere else.

★★ **The marker is unambiguous because the embedded language has a syntax that can be VIOLATED.**
`the file-path` is not valid C, not valid SQL, not valid Cufet — it is English sitting in code that
is not English, which is exactly what makes it impossible to mistake for the host text.

**Regex has no invalid syntax to borrow.** `the needle` is a perfectly good pattern: it matches the
literal characters `the needle`. Every character sequence is a valid regex, so there is no marker —
not `the`, not `@`, not any candidate weighed above — that a pattern could not also have meant
literally. The property every rejected marker failed on is the property regex denies to all of them
at once.

★ Nor does the embedded language supply one. **No regex flavour interpolates.** Perl's `/$foo/` and
Ruby's `/#{foo}/` are the HOST language's interpolation applied before the engine ever sees a
string; Python, Java and JS build a string and hand it over. That every one of those ecosystems also
ships an escaping function — `re.escape`, `Regexp.quote`, `Pattern.quote` — is the evidence that
splicing into a pattern is a known hazard rather than a feature regex offers.

⚠ So the honest consequence is the one the book already states: **a pattern is fixed where it is
written.** A program cannot take a pattern on its command line, and `examples/systems/search.cufe`
can never become grep. That is the guarantee working, not a gap in it — and the reason a bad
pattern is refused at its declaration rather than failing on the line that uses it.

### What an axiom gives back is on its DECLARATION

```
Define c-language number add, given (the number left, the number right), as [the left + the right].
Define the c-language number axiom add, ...                    ← the same thing, spelled out
```

★ **The order is `c-language number axiom`, not `number c-language axiom`**, because the tag
qualifies the *axiom*: this is a C-language axiom that yields a number, not a number that came
from C. Both middle words drop, and every rung of the ladder still reads —
`the c-language number axiom add`, `c-language number add`, `c-language axiom add`, `c-language
add`.

⚠ **The alternative was tried first and reversed the same day.** The result type came from the line
*using* the axiom (`Define the number fd as cast open-file on (…)`), on the reasoning that a
declared type is already what a value must fit into. It works, and the cost was much larger in
practice than on paper: the call had to be the **entire right-hand side of a typed binding**, so it
could not appear in a condition, in an interpolation, inside arithmetic, or as an argument. Every
result went through a named intermediate first — measured at one line becoming two at three of four
call sites in the first real program written against it. The cost fell on *uses*, and uses are what
multiply.

⚠ **And it could not be inferred from the C**, which is the tempting third answer. The C type is
knowable — `_Generic` already reads it, which is why no C type is ever written down — but three
things stop it deciding the *Cufet* type:

- **It is needed too early.** The checker must know the type to check `s + 1`; that happens in the
  front end with no toolchain in sight. Inferring would put a C compiler behind `cufet check`,
  which today needs none and runs where none can exist — the playground is wasm.
- **It would vary by platform.** `size_t` and `time_t` are not the same width everywhere, so the
  same program would have different Cufet types on different machines. That is a divergence in the
  language, not in a backend.
- **★ The C type does not determine the meaning.** `isatty` gives an `int` that is really a fact;
  `fopen` gives a pointer that is really a handle; `getchar` gives an `int` that is a character *or*
  an end. C says how many bits arrive, never what they are. Inferring would be Cufet claiming to
  know something it cannot, which is exactly what "taken as given without proof" refuses.

So the one party who knows says so, once, where the source is written.

**An axiom runs when it is returned or cast**, and an axiom that declares no result may be written
but not run — which is what keeps composition possible, since a function assembling a SQL fragment
hands one back unrun. Passing an axiom around does not run it.

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
  documented caveat. [CONTRIBUTING.md](../CONTRIBUTING.md) states the rule as practice; this
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
  order, **running out of stack** — where there is no single right answer to converge on.

  *Running out of stack, in full, because it is the widest of those exceptions.* The
  interpreter refuses at a fixed call depth and raises an ordinary exception: `In case of
  exception` catches it, `Suppress the exception.` continues, and the program exits 0. A
  compiled program instead runs until the machine's real stack is gone, and then prints
  that it ran out and exits 1 — uncatchable, and naming no function or line. So the two
  differ in **how deep**, in **what is said**, and in **whether it can be caught**.

  The depth is the easy part: a compiled program should be allowed the room it actually
  has rather than be held to a number chosen for an interpreter. The other two are the
  price of *catching* the overflow rather than *predicting* it, and predicting it was
  measured and rejected. Any per-call check has to take the address of a local, and gcc
  will not reuse the frame of a function whose local's address was taken — so a depth
  counter and a how-much-room-is-left test both disable tail-call flattening. Measured on
  one self-recursive function at `-O2`: with no check it flattened into a loop and ran
  forever on no stack at all; with a depth counter it segfaulted; with a headroom check it
  grew the stack until it tripped. **Either check turns a program that runs in constant
  space into one that dies.** Refusing to break those programs is worth a coarser message
  and an uncatchable end, on the grounds that running out of stack is a defect rather than
  a condition anyone plans around.

  ⚠⚠ **That measurement was of gcc's willingness, and it was not ours to rely on.** Emitted
  C returns a number through a hidden pointer — `CufetDec` is 24 bytes — so the callee's
  result is copied into the caller's slot *after the call comes back*, and `return f(x)` is
  therefore not a tail call at all. Measured 2026-09-06: `__attribute__((musttail))` on that
  shape is refused outright — *"cannot tail-call: return value used after call"* — while the
  same function returning a fact is accepted and loops with no optimiser at all. A recent gcc
  at `-O2` sees through the copy and jumps anyway; an older one does not, so the same program
  ran in constant space on one machine and died on another.

  **The compiler now emits the loop itself.** A `return <call to this same function>`, where
  nothing has to run between the call and the return, becomes an assignment to the parameters
  and a jump to the top of the function — no call, so no frame. It holds at every `-O` level
  and on mingw, which is why the test that pins it is no longer Linux-only. The reasoning above
  is unchanged and still decides the general case: a per-call check would still take a local's
  address, and gcc's own flattening of every shape this rewrite does not cover still depends on
  it not being there.

  ⚠ What is NOT accepted here is silence, which is what this replaced: a compiled program
  that overflowed used to exit `0xC00000FD` on Windows with nothing on either stream, and
  segfault on Linux where the only word — "Segmentation fault" — comes from the shell and
  vanishes the moment the program is run from anything else.

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
