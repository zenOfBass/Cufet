# Cufet Roadmap

Where Cufet is going.

This file answers one question, and deliberately only one. It does not describe what the
language can already do, why it is shaped the way it is, or how to work on it — three other
documents do that better, and a roadmap that restates them goes stale the moment they change.

| If you want to know | Read |
| --- | --- |
| What Cufet is, and why you might care | [README.md](README.md) |
| How to use a feature | [REFERENCE.md](REFERENCE.md) |
| Exactly what the rules are, and the sharp edges | [GRAMMAR.md](GRAMMAR.md) |
| What changed, and when | [CHANGELOG.md](CHANGELOG.md) |
| **Why** the language is like this | [DESIGN.md](DESIGN.md) |
| How to build, test and contribute | [CONTRIBUTING.md](CONTRIBUTING.md) |

Cufet is pre-1.0 and may still change. Versioning is semantic: feature arcs bump the minor
version, and 1.0.0 will mark the point at which the language is considered stable.

---

## Where things stand

Cufet has two backends sharing one front end: a tree-walking interpreter and a compiler that emits C and invokes `gcc`. Every committed grammar feature works on both.

The interpreter is the **oracle**. The compiler's test suite compiles each program, runs the
binary, and asserts its output equals the interpreter's. Where the two disagree, one of them
is wrong — it is never written down as a caveat. Either the behaviour becomes precise on both
sides, or the compiler refuses with a clean error. The narrow exception is behaviour that is
genuinely undefined or platform-owned, where there is no single right answer to converge on.

That rule is the reason this list is short on soundness work and long on reach.

---

## What's next

Ordered by what unblocks what, not by size. Two framings set the order:

- **Sockets, POSIX and Windows APIs, and threading primitives are not separate items.** They
  are all "call a C function", so a **C FFI** collapses them into one item and turns each of
  them into a book rather than a language feature.
- **Multi-directional predicate dispatch is not free-floating** — it is on the critical path
  to self-hosting. A lexer, parser and type checker are one enormous dispatch on node type;
  written as `is a` chains, a Cufet-in-Cufet compiler is miserable to write and worse to read.

### Tier 1 — leverage

1. **`unto` targeting an INTERFACE — default methods, which is most of what traits give.**

   ```
   Bind text to describe unto shape:        ← every conformer gets it
       Return "a {one's name} of area {one's area converted to text}".
   Done.
   ```

   ★ **It fits the locked monomorphization design without touching it.** Interface polymorphism
   lives at exactly one position — the function parameter — and the argument is always a concrete
   conformer at the call site. So a default method has a concrete receiver every time it is called
   and specialises per conformer. No vtable, no type tag, nothing to relax.

   **It needs no new concepts.** `InterfaceDefinition` already carries full method signatures, so a
   default can be written against the contract (`one's area`). `unto` already exists. The current
   refusal even names the extension point: *"'X' is an interface, not an object type — methods
   can't be attached to it with 'unto'."*

   **Chosen over a separate `trait` construct**, and over mixins: no trait state, which is the line
   Rust draws too, and no new keyword to learn.

   **Settle before building:**
   - **Specialisation** — a type's own method beats the interface's default.
   - **Conflict** — two interfaces supplying the same method name to one type must be refused, not
     silently resolved.
   - **Does a default satisfy conformance?** If `shape` requires `describe` *and* supplies one, must
     a conformer still define it? Rust says no. That is the useful answer, but it turns "interface"
     from *contract* into *contract with fallbacks* — decide it deliberately.
   - **Interfaces conforming to interfaces** — start with no. That is where trait systems get deep.

2. **C FFI, including an explicit address-of.** What makes "anything can be written in Cufet"
   literally rather than nearly true.
3. **`For each` over a user-defined type.** Today it walks core collections only — a user-defined
   tree, linked structure or wrapper cannot be looped over at all:

   ```
   For each n in b, repeat:      ← b holds bag objects.
                                   It evaluates to bag objects, not a series.
   ```

   The workaround is to flatten into a series first, which pays full materialisation for a walk
   that may happen once. An interface contract — hand back the next one, or void when done — closes
   it with machinery the language already has.

   ★ **Deliberately EXTERNAL iteration, not generators.** A generator that yields and suspends is a
   coroutine, and coroutines belong to *the rabbit as a control-flow primitive* below; building
   them here would be a second suspend-and-resume mechanism competing with the one that item exists
   to unify, and would inherit that item's unanswered "which restriction?" question. This is only
   method dispatch: no suspension, so no arena question about where paused state lives, and nothing
   the two backends could disagree about.

4. **The runtime as a compiled unit, not 1500 lines pasted into every file.** Today the C runtime
   is emitted inline into each generated program. That is the direct cause of a recurring bug
   class: a symbol emitted above its own declaration.

   ★ **Measured on 2026-08-12 — three defects in one session, all the same shape.** A shared
   constant of series type emitted `static cser_0* cv_suits;` two lines above
   `typedef struct cser_0_s cser_0;`. `cufet_bits_at_width` was placed above `cufet_dec_to_dbl`,
   which it calls. A top-level lambda had to be hoisted to file scope by hand so functions could
   reach it. Each was found by gcc rather than by a test, and each was fixed by moving code rather
   than by fixing a rule — which is the tell that the rule is missing.

   **Emit the runtime as its own `.c`, compile it once, cache the `.o`.** Not a prebuilt
   `libcufet.a`: shipping a binary would trade away the property that makes the toolchain simple —
   *gcc is the only requirement* — and would break the arch-neutrality that makes `emit-c` useful
   for cross-toolchain builds. The source stays the artifact; only the build is incremental.

   **What it buys.** The ordering class disappears, because the emitted file no longer contains the
   definitions it depends on. `emit-c` output becomes *the program* rather than the program plus a
   runtime, which is most of what makes that command worth having. Every `cufet build` stops
   recompiling the runtime. And it is the one piece of work any future backend change would need
   anyway, so it buys optionality for free.

   **Settle before building:** whether the `_usesX` gating survives (a program with no concurrency
   currently emits no pthread code at all) or gives way to one runtime with dead-strip; and where
   the cached object lives so that a clean checkout still builds with nothing but gcc.

   Pairs with a **declaration prologue** for the program's own symbols, which closes the rest of
   the class. Neither is a language change; both are the emitter learning to emit in two passes.

### Tier 2 — the design mountains

All need a design session before they can be ordered against anything. They are here because
they are large, not because they are waiting — except the formatter, which is genuinely
blocked by the inline forms in Tier 1.

5. **Multi-directional predicate dispatch.** Watch the no-subtyping invariant. See above for why
   it is not optional.

6. **User-defined generics.** A `stack of number`, a `tree of text` — today a user-defined
   container holds one concrete type or fakes it with an open union.

   ★ **The syntax already exists.** `a series of number`, `a map from text to number` — a
   user-defined `a stack of number` reads exactly like the built-ins, so there is nothing to invent
   at the use site. Most languages have to bolt on `<>`. The implementation strategy is also already
   chosen: monomorphization, the same technique interface parameters use today, with **interfaces as
   the constraint mechanism** rather than a new concept.

   **Do it AFTER `unto` on interfaces** (item 1). Default methods may absorb enough of the pressure
   that generics can be scoped smaller and aimed at cases that are known rather than guessed.

   ⚠ **The cost lands on the language's best asset.** Cufet's distinguishing feature is errors that
   name the line, the violation and the fix. Generic type errors are the worst errors in every
   language that has them, because the failure is a unification that went wrong three inferences
   deep. Budget for that specifically.

   **Two decisions to take deliberately, both against the grain of other languages:**
   - **Require explicit type arguments; do not infer them.** Cufet already makes you write
     `Define the text name as`. Inference is where generic errors become unreadable, and declining
     it is in character rather than a limitation.
   - **No variance.** Not covariance, not contravariance. It is the part nobody can explain to a
     learner, and a teaching language that ships `IEnumerable<out T>` has lost the plot.

   **The trigger:** the first time a container is written twice for two element types, or an open
   union is used to fake one.

   It also unlocks something concrete and small: **mixed-type operator dispatch**, and with it
   `matrix * number` scalar scaling, which is deferred today for exactly that reason. (The
   Hadamard product is *not* blocked — it is decided: if ever added it will be a named
   `collections` function, never an operator, because `*` means matrix product and there is one
   canonical way.)

7. **The rabbit as a control-flow primitive.** It shipped as a memory region and everything
   written about it says so, which is accurate but incomplete: **the arena is the substrate, and
   the purpose is control-flow machinery** — continuations, suspend and resume, capturing and
   restoring execution state. A task that yields and resumes *is* a continuation; so are green
   threads, coroutines, and the exception path. One primitive underneath all of them.

   **Generator-style iteration is one of them, and is not listed separately.** A generator that
   yields mid-body is a coroutine wearing a different name, so it arrives with this and not before
   it. (`For each` over a user-defined type is in Tier 2, and is deliberately the *external* kind —
   dispatch, not suspension — precisely so it does not pre-empt this design.)

   Not retrofitted reasoning. **Two restricted continuations already exist** — `In case of
   exception` compiles to `setjmp`/`longjmp` (a one-shot escaping continuation) and tasks are the
   parallel form — and the surface drifted toward the conception on its own: the recorded decision
   was a standalone `Start a task:`, but what got built was `Have rabbit start a task:`, which
   *requires* an enclosing rabbit, as do channels.

   **The open questions, in the order they need answering:**
   1. **Surface or implementation coupling?** The code and the recorded decision disagree, and
      everything downstream — how `bury`, `unbury` and continuations read — inherits the answer.
   2. **Which restriction?** This decides whether it is buildable at all. Full first-class
      continuations need either CPS-transforming the whole program (destroying the readable,
      self-contained C the compiler emits) or copying the machine stack (nonportable, and in
      conflict with both the sanitizers and the thread-local arenas). Coroutine-shaped ones —
      save state, resume in order, one live resumption — are very achievable and cover nearly all
      the value. ★ **The no-divergence rule decides this independent of implementation cost:**
      whatever ships must work identically on both backends, and a tree-walking interpreter
      cannot faithfully offer `call/cc` either.
   3. **No implicit accumulator.** The original sketch had the rabbit *hold* an unburied value in
      temporary state until used — an invisible register, which cuts against a language that made
      narrowing explicit and refuses any capture write something could see. `Define x as unbury
      <stash>.` gets
      the same feature with no hidden state.

   **A stash is saved execution state, not a stack data structure**, so it cannot be a library:
   suspend and resume need compiler and runtime support. (The naming is Turing's — the ACE design
   used *bury* and *unbury* for subroutine linkage.)

8. **Formatter.** It owns **multiline layout of large record and object shapes**, which was
    briefly a linter rule and is not one. Both tools would need the same "how large is large"
    threshold, and one number owned in two places is one number that drifts. The severity settles
    it too: every other linter rule flags something a tool cannot fix for you — nesting you have to
    rename your way out of, an ordering you have to rethink, a capital you have to type. Layout is
    pure mechanism, so a warning about it is noise next to a tool that simply does it.

    **Blocked twice over.** It must come after the inline forms in Tier 1, or it would be taught
    layout rules for constructs whose spelling is about to change. And doing it properly means
    teaching the **lexer to carry comments as trivia** first — comments are skipped today and
    never reach the AST, so a printer built from the AST would silently delete all 241 comment
    lines in `examples/`, including the 34-line header on `binarysearchtree.cufe`. That is a
    change to the shared front end both backends sit on.

    **When it is built**, prefer a **token-stream** formatter to an AST printer: it rewrites only
    the whitespace between tokens, so comments are safe because it never moves them, and it gets
    a mechanical oracle — `format(x)` must lex to the same token sequence as `x`, and `format`
    must be idempotent. Both are checkable across every example and fixture.

    **Still undecided:** whether continuation lines align to the open delimiter (today's style,
    but the indent then depends on the name's length, so a rename reflows the block) or explode
    to a fixed indent; and the width that makes a shape "large" — the corpus median is 43 and p90
    is 82, so 90–100 leaves nearly everything alone.

### Tier 3 — modules, strictly in this order

9. **The `module` interface.** A named interface defining the contract for any loadable thing.
   It comes first because it is the stable seam everything else in this tier depends on, and it
   is buildable well before the loader — which means the loader can arrive later without
   churning what already uses a book.
10. **Separate compilation and an external book loader.** ⚠ Known collision: the bounded
    open-union representation is sound *because* the whole program compiles at once. Either
    feature forces revisiting it.
11. **What a book exports.** Every member of a book is public API, permanently, because there is
    no way to mark one internal. It does not bite yet — the bundled three are built in and you
    cannot write a book — but the moment the loader below lands, a book author has no way to say
    *this is my helper, do not call it*.

    ★ **The default is the part that cannot wait.** Enforcement can ship with the loader; the
    default cannot be changed after it. Once books are published and depended on, "everything is
    public" is permanent, and every internal becomes someone's dependency. Deciding now that a
    book exports a **stated surface** costs nothing and cannot be retrofitted.

    Deliberately book-level, not per-member `private` on objects. Cufet's encapsulation unit is
    the book, so the boundary is *what a book hands out* — the object question is a different and
    much weaker one, since within a file a visibility marker is a comment with ceremony attached.

12. **A package manager for books.**

### Tier 4 — Cufet in Cufet

Three programs in increasing size, ending with the compiler. The ordering is not ceremonial:
this tier's real blocker is stated below as **ergonomic rather than capability**, and the only
way to find ergonomic blockers is to write large Cufet programs. These are the two largest
realistic ones, so they are the instrument as much as they are the goal — better to meet the
gaps across a REPL and a shell than to meet all of them at once inside a compiler.

13. **A REPL, written in Cufet.** Read a line, evaluate it, print the result, keep the bindings.

    ★ **An open design question, deliberately unresolved here:** does it *shell out* to `cufet`
    for each line, or evaluate Cufet with a Cufet-written evaluator? The first is buildable today
    and is a good program; the second is literally self-hosting's front half. Only the second
    makes this a stepping stone rather than a stop along the way, and the choice should be made
    when the work starts rather than assumed now.

14. **A shell, written in Cufet.** `examples/systems/shell.cufe` is the seed: it already reads, parses,
    dispatches and launches, and now changes directory too.

    ⚠ **Blocked on the C FFI (Tier 2).** Job control needs process groups and signalling a child;
    completion needs raw terminal mode. Neither is in the language and neither should become a
    language feature — they are exactly the "call a C function" family the FFI collapses.
    Globbing and history need nothing new.

15. **The compiler, written in Cufet.** The blockers are ergonomic rather than capability: the
    data model, text handling and I/O are already sufficient, and emitting C is a route a
    Cufet-written compiler can take too.

    ★ The test oracle already exists. A self-hosted compiler can be validated by asserting
    its C output matches this compiler's — a third implementation held against the other two.

### Ongoing, no fixed slot

A formal soundness proof or a fresh-eyes red-team · a periodic error-message audit for internal
vocabulary · design patterns as a book · an in-memory filesystem for the playground

**Parser-hardening.** `IsNamedAccessPattern()` decides whether `the <word> of <thing>`
is a named-field access by **looking ahead**, and that guess once mis-parsed `the series of number
board` in n-queens. Approach C shipped and closed the observed bug class: no keyword can be a
user-defined field name, so the whole reserved set is excluded at once, with three narrow
exceptions kept valid (`key`, `category`, `characters`). **Approach B removes the guess instead of
enumerating where it fails** — give the parser explicit type-annotation contexts so it knows *from
position* whether it is reading a type or an expression.

The two decision points are small. The work is threading that context through every position where
a type is parsed — parameters, field declarations, `Define … as a <type>`, `Bind <type> to`,
element types — because missing one is exactly how a regression gets in. n-queens is the canary.

★ **Insurance, not repair, which is why it has no slot.** The previous approach closed every case anyone has actually hit; this closes the remaining theoretical fragility. Its precondition — a feature-complete parser syntax, so the hardening happens once against the final shape — **is met**, so it is unblocked rather than waiting on anything. It is written up as a contributor-sized task in
CONTRIBUTING's *known debts*.

**A logic-gates book** — circuit composition over `bits`: gates as components you wire together,
rather than the operators `bits` already shipped.

- **Its signal is four-valued, because hardware is.** Verilog uses `0`/`1`/`X`/`Z`; a two-valued
  signal cannot model an uninitialised line, a tri-state bus, or two drivers contending. The
  tetralemma maps onto it exactly — *both* is contention, *neither* is floating — which is why
  four-valued logic is rejected in core (a rival spelling of "not exactly true") and right here
  (the state of a wire). ⚠ **Truth tables come from Verilog, not the philosophy:** `0 & X` is `0`,
  which naive four-valued logic gets wrong.
- **Settle before building:** is a circuit a **value** you construct then evaluate, or a
  **pipeline** you push signals through? Cufet has both shapes; the answer sets the whole surface.

---

## Deferred — blocked on something that is not itself on the list

These are **not** numbered above, and that is the point rather than an oversight. Everything in
*What's next* is ordered because its blocker is either nothing or another numbered item. Each
entry here is blocked on an arc that has not been designed, or on a use case that has not
arrived — so giving it a position would be fiction, and the ordering above is only worth
anything if it means something.

Nothing here has been argued down. Each states its blocker, because a deferral without one is
indistinguishable from having forgotten.

**Promote an item the moment its blocker becomes a numbered item.**

### Language

- **Named loops — a label so `Stop.` can leave an OUTER loop.** *Blocker: no demonstrated need.*
  Across 28 examples — a sudoku solver with triple-nested loops, a JSON parser, recursive descent,
  Dijkstra, Huffman — there are 7 uses of `Stop.`/`Skip.` and **not one wants to escape an outer
  loop**, nor is there any trace of the flag workaround that would appear if the need were being
  routed around. The deepest nesting escapes with `return` from inside a function, and extracting a
  nested search into a named function is usually better than labelling the loop anyway.

  **The argument worth revisiting is readability, not capability:** `Stop.` silently means "the
  innermost one", and in a triple-nested loop the reader has to count. An optional label could make
  *existing* code clearer without enabling anything new.

  **The trigger:** the first program that needs a mutable flag purely to break an outer loop.

- **`is any of (…)` — membership as a comparison.** `If x is any of (1, 2, 3)` over
  `If x is 1 or x is 2 or x is 3`. *Blocker: small win.* ⚠ If built, it must be a **comparison,
  never a value** — `Define maybe as any of (1,2,3).` would import Raku-style junctions, whose
  threading order is explicitly undefined and therefore incompatible with no-divergence.

- **Compile-time macros.** Hygienic, expanding to Cufet AST before the checker runs — *not* fexprs,
  which are first-class and runtime. Fexprs are ruled out on their own terms: Wand's result means
  no two expressions are ever equivalent, which takes out `check`, monomorphization, and any
  compiled backend that is not an embedded interpreter.

  *Blocker: self-hosting (Tier 5).* A macro expander generates Cufet AST, so building it in C# now
  means building it again in Cufet later. ⚠ Macro errors are the worst part of every language that
  has them, and clear errors are this language's distinguishing feature — that tax should be paid
  deliberately, not absorbed early.

- **`Judge` value arms and `Descend.`** The construct ships — closed-union subjects, `or`
  grouping, `it` narrowed per arm, `Otherwise`, and coverage proved or defaulted so control can
  never fall off the end. Two pieces of the decided design are unbuilt: **value arms** (`It is 1`,
  `It is "red"`), which the native backend refuses cleanly because they compare values rather than
  dispatch on a tag; and **`Descend.`**, explicit fall-through, whose keyword is reserved and whose
  typing rule is already settled — *a fall-through target is checked under the union of every path
  that can reach it*. *Blocker:* no use case has demanded either. Grouping with `or` covers what
  C-style fall-through is overwhelmingly used for, and nothing has yet wanted to judge a value.

- **A no-op statement.** An `Otherwise` arm meaning "ignore the rest" has to say something real,
  because Cufet has none — `pass` exists only in `or pass the failure off`. *Blocker:* it may not
  want fixing. Requiring the arm to say something is what makes coverage mean *you thought about*
  the remaining cases; a no-op is the `catch {}` of case dispatch. Revisit if writing real
  statements in ignore-this arms becomes a genuine irritation rather than a hypothetical one.

- **Ordering by an explicit basis.** Ordering works on numbers and bits. Extending it to text
  and beyond should use a stated basis rather than new operators or a silent default:
  `is less than X by length`, `is greater than X by character code`, a series sorted `by size`.
  Naming the basis is what avoids undefined-collation problems — case, locale and Unicode
  become named bases instead of hidden assumptions. *Blocker:* intended shape only, undesigned
  in detail.

- **Text refinements.** The everyday toolkit is complete (join, measure, convert both ways,
  split, search, find, slice, replace, case, trim). What remains is fancier: locale-aware
  casing and *full* case mapping (`in uppercase`/`in lowercase` are invariant and simple today,
  so `ß` stays `ß` rather than becoming `SS`), title-case, leading-only or
  trailing-only trim, and a character-sequence type — `text` stays opaque, with no
  character-level indexing. *Blocker:* waiting on a real use case, deliberately.

- **Expression-level flow-narrowing.** Narrowing works on *variables* today
  (`If maybe-x is not void: … maybe-x`). Narrowing a value produced by an *expression* — say
  re-reading `the entry for "alice" in ages` inside an already-checked branch without naming
  it — is not supported. *Blocker:* the checker would have to track which expression was
  checked and invalidate on mutation, which is unsound against mutable maps unless done very
  carefully. "Name your lookups" covers the need meanwhile.

### Types and objects

- **★ A mutable character buffer — DESIGNED 2026-08-12, unscheduled.** `text` is an immutable
  value, so building one in a loop is quadratic — `The out becomes out joined to node's symbol.` in
  `huffmancoding.cufe` rebuilds the whole string every pass. This is its mutable companion, and it
  lives in the `collections` book.

  **The split is EASE OF USE versus FULL CONTROL**, which is a different axis from Rust's. Rust
  splits `String` from `&str` because it has no runtime and ownership must be visible; that split
  is forced. This one is chosen, and a user can reason about it at the moment they pick: `text` for
  the common case, the buffer when you need to edit in place.

  ### What it is

  - **A mutable REFERENCE type**, region-allocated like a series or a map, freed with its rabbit.
    ★ **Mutability is the whole distinction** — not byte access. That difference alone earns the
    type; byte access was only ever a side effect of one storage choice.
  - **Elements are characters (code points), stored FIXED-WIDTH internally** — four bytes each,
    UTF-32 in the buffer, converted to UTF-8 when it becomes `text`. That buys O(1) `item n of`
    and O(1)-plus-shift insert, and means no variable-width hazard exists *inside* the buffer at
    all. The cost is 4× memory on a thing you build and discard, which is where that trade is
    cheapest.
  - **It follows COLLECTION conventions, not text ones.** `Insert`, `Remove`, `item n of`,
    `the number of`, `For each`. Treat it like an array.
  - **It converts to `text` by an explicit COPY.** The buffer lives on, independent. Not a
    consuming move (the language has no move semantics and should not grow them for this) and not
    a view (a `text` that changed under you would break the one thing `text` promises). The copy is
    once at the end, so O(n²) becomes O(n).

  ### What it deliberately does NOT get

  - **No parity with `text`.** No `trimmed`, no interpolation holes, no raw-string concerns. The
    moment it grows a parallel copy of text's API the split stops meaning anything and the reader
    is back to "which one am I holding." Uppercase/lowercase are the plausible exceptions.
  - **No byte-level access.** `bits` already owns the binary world — wire formats, encoding work.
    Handing that job to a text-adjacent type is what forces "byte 5 or character 5?", and refusing
    it is what keeps the type honest.
  - **No zero-copy views into it**, since a view would be UTF-32 while `text` is UTF-8. Whether
    that matters depends on whether the scanning job wants slices or just positions — open.

  ### Why it matters here specifically

  Text manipulation is not peripheral in a language whose whole surface is English. Five of the
  29 examples are text processors — `recursivedescent`, `json`, `config`, `markov`, `wordfreq` —
  and each hand-rolls its own scanning. `huffmancoding` is the one paying the quadratic build.

  **Settle before building:** whether `State buf.` prints it the way `State` prints a series
  (probably yes, and it covers most of what interpolation would have been for); and whether
  scanning wants a cursor rather than bare indexing.

  ⚠ **Correction, 2026-08-13:** this entry used to warn that including uppercase/lowercase would
  make the buffer grow, because German `ß` uppercases to `SS`. Measuring it while building the
  shared case table showed otherwise — Cufet's casing is *simple* case mapping and is strictly 1:1
  across all 1,114,112 code points, so `ß` stays `ß` and nothing ever changes length in characters.
  Casing a buffer in place is therefore a plain per-element map, with no resize and no special
  case. The growth problem is real only for *full* case mapping, which neither backend does.

- **★ Enums — DECLINED. A closed union already is one, and a stronger one.** The
  property worth having is not the syntax, it is that the compiler proves every case is handled,
  and `Judge` over a closed union proves exactly that: `Otherwise` becomes optional and a missing
  case is a static error. Adding enums would be a second closed-set mechanism doing what the first
  already does, and it would fork `Judge` — some closed sets unions, some enums, two stories about
  exhaustiveness instead of one. This works today:

  ```
  Define object red with ().
  Define object green with ().
  Define object blue with ().

  Bind text to name-of, given (the (red or green or blue) c):
      Judge c, where it is:
          A red, return "red".
          A green, return "green".
          A blue, return "blue".
      Done.
  Done.
  ```

  Three real gaps remain, and each is an addition to UNIONS rather than a reason for a new
  construct. Recorded so the enum question does not have to be re-argued to reach them:

  - **`Define object red.` is a parse error** — a case that carries nothing still needs
    `with ()`, and `a new green` still needs `{ }`. This is the friction that would make someone
    ask for enums in the first place, and it is a parser tweak rather than a feature. The
    cheapest of the three by far.
  - **A union cannot be asked for its members**, so there is no way to walk every case. Real,
    occasionally missed. *Blocker:* what it would even return — the members are different types,
    so a series of them is not a type the language can currently spell.
  - **No ordinal or ordering.** Rarely what anyone actually wants from an enum, and easy to fake
    with a method. Listed for completeness; would decline again.

- **Reference-semantics opt-in.** Objects and map values are value-typed. An explicit way to
  ask for shared semantics has no syntax. *Blocker:* its own design session; it interacts with
  the region model, which is what currently makes value semantics free.

- **Fallible setters.** A setter that can reject a value is deliberately not supported, because
  the current rule keeps `becomes` infallible *everywhere*. *Blocker:* an effect-tracking arc —
  a fallible setter would require effect annotations on every assignment expression. Not
  designed, not near-term.

- **Optional fields with a default.** Every field must be supplied at construction — including
  `voidable` ones, where omitting the field is still an error — so an object has no unset state
  at all. That invariant is worth keeping, and it is the opposite pressure to C#, which added
  `required` and `init` to retrofit onto defaults-everywhere. *Blocker:* no use case until a type
  crosses a **version boundary**. Adding a field is a breaking change for every construction
  site, which is nobody's problem while one person owns them all and everybody's the moment
  books are user-authored and depended on — so this arrives with the package manager, not before.
  Until then, named constructors (`making a <type>`) already cover "I do not want to write six
  fields", and `voidable` already covers "may be absent" while keeping the absence visible where
  the object is built.

### Tooling

- **Self-verifying docs — tagged fences, checked output, checked refusals.** Every code block in
  REFERENCE and GRAMMAR would be run, its **output block asserted to match**, and its
  counter-examples asserted to be *still refused*. A block marked REFUSED that starts passing means
  the language moved under the doc, and nothing catches that today.

  `DocBlockTests` already runs the ~238 runnable blocks and pins the 157 that pass, so a sample
  the language breaks is caught now. What it cannot do is judge the other 81: most of those
  failures are correct — GRAMMAR is a constraints reference full of deliberate counter-examples,
  and many blocks are fragments teaching a shape rather than programs.

  *Blocker:* the docs cannot say which is which. **All 534 fences are untagged**, so nothing can
  distinguish a runnable program from expected output from a fragment from a counter-example. The
  work is a fence convention (` ```cufet `, ` ```output `, ` ```cufet-fragment `,
  ` ```cufet-refused `) applied across both files — mechanical but large, and `tools/doc-sweep.py`
  already knows enough from pass/fail and adjacency to propose a first pass to review as a diff.

  The payoff is the output assertion. Executing a sample proves it runs; comparing its printed
  result to the block underneath is what catches a doc that is merely *wrong* — the failure mode
  that has actually recurred.

- **An LSP.** A run stops at its first error, so the front end reports at most one — plus any
  warnings it collected on the way, each with a line, a column and a long prose explanation. LSP's
  incremental machinery has nothing to earn back on a report that small.
  *Blocker:* wanting go-to-definition, completion or rename — the features that genuinely need a
  resident index, and that nobody has asked for yet.

### Memory and concurrency

- **Move semantics at channel send.** A send deep-copies across the thread boundary. That is
  sound, and it is what keeps the two threads' arenas disentangled, but it is not free. A move
  — transferring ownership and invalidating the sender's binding — would avoid the copy.
  *Blocker:* the language has no way to express "this binding is spent."
