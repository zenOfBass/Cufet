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

Ordered by what unblocks what, not by size.

⚠ **A "blocked on" claim names the missing THING, never a tier number, and has to be checkable.**
"Blocked on item 4" cannot be tested, so nobody tests it, so it rots and then drives decisions —
`collections` carried "blocked on the C FFI" for weeks and was never blocked at all; the claim was
true of `math` and got copied. Write "needs `square root`, which is a C call" instead, check it when
you write it, and check it again before ordering work off it. A dependency you cannot state
precisely enough to test does not belong in this file.

### Next, in order

Two framings that set the order:

- **Sockets, POSIX and Windows APIs, and threading primitives are not separate items.** They
  are all "call a C function", so a **C FFI** collapses them into one item and turns each of
  them into a book rather than a language feature.
- **Multi-directional predicate dispatch is not free-floating** — it is on the critical path
  to self-hosting. A lexer, parser and type checker are one enormous dispatch on node type;
  written as `is a` chains, a Cufet-in-Cufet compiler is miserable to write and worse to read.

1. **One ownership story — really one, exceptions.** The rabbit is already the single boundary
   for arenas, pthreads, channels and result boxes: it pushes an arena, registers what is created
   inside it, and joins every task and frees every channel at `Done.` before the pop.

   What is not folded in is **exception bookkeeping**. `_rabbitDepth`, `_excOpen` and `_openFiles`
   are three parallel depth stacks, each with its own unwind helper and its own per-loop list
   (`_loopRabbitDepths`, `_loopExcDepths`, `_loopFileDepths`), and every nonlocal exit has to
   unwind all three in the right order. The tell is `_currentTryHandler`, which carries
   `FileDepth`, `ExcDepth` and `RabbitDepth` as separate fields. Collapsing them into one context
   record is a refactor against a sharp invariant, not a feature.

2. **Pointers scoped to a rabbit**, which is what gates the C FFI below. ⚠ Nothing exists yet —
   no surface, no design session. This is the item that sets the real distance to "done".

3. **C FFI, including an explicit address-of.** What makes "anything can be written in Cufet"
   literally rather than nearly true. ★ No bundled book needs it any more — `math` went pure
   decimal in the arc above — so its consumers are the "call a C function" family it collapses:
   the shell's job control and raw terminal mode, sockets, the POSIX and Windows APIs.

### The design mountains

All need a design session before they can be ordered against anything. They are here because
they are large, not because they are waiting. The formatter used to be blocked by the inline
forms; those shipped in 0.15.0, so it is unblocked and simply last.

1. **Multi-directional predicate dispatch.** Watch the no-subtyping invariant. See above for why
   it is not optional.

2. **Formatter.** It owns **multiline layout of large record and object shapes**, which was
    briefly a linter rule and is not one. Both tools would need the same "how large is large"
    threshold, and one number owned in two places is one number that drifts. The severity settles
    it too: every other linter rule flags something a tool cannot fix for you — nesting you have to
    rename your way out of, an ordering you have to rethink, a capital you have to type. Layout is
    pure mechanism, so a warning about it is noise next to a tool that simply does it.

    **One blocker left.** The inline forms it used to wait on shipped in 0.15.0. What remains is
    that doing it properly means
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

### Shipping a book, strictly in this order

The `module` interface itself is the language seam and shipped with the arc above. What is left
here is everything about code that is **not already in the program**, which is a separate hard
problem and is nobody's contract.

1. **Separate compilation and an external book loader.** ⚠ Known collision: the bounded
   open-union representation is sound *because* the whole program compiles at once. Either
   feature forces revisiting it.
2. **What a module exports.** Every member is public API, permanently, because there is no way to
   mark one internal. A module author has no way to say *this is my helper, do not call it*.

   ★ **It bites when a module can be DEPENDED ON, and not before.** Nothing is distributable —
   there is no loader and no package manager (the items either side of this one) — so no one can
   rely on your helper yet.

   ⚠⚠ **AMENDED 2026-08-19: that precondition is now PARTLY MET, and it arrived from an
   unexpected direction.** Writing the bundled books in Cufet (the 0.16.0 arc) made the prelude
   ship with the language and travel into every program, so **the books are modules that really
   are depended on — by everyone, permanently, with no distribution mechanism needed.** A helper
   added to `math` is in every Cufet program forever.

   It bit once while `power` was being written: an overflow-guarded multiply was wanted in two
   places, and factoring it out would have made `guarded-times` a permanent member of `math`, so
   **it was inlined twice instead** — the magic constant with it. That is the predicted failure
   exactly: not "could not be done", but "written worse to avoid the API". One duplicated guard
   does not justify reordering this item, and the fix here is still the right one — but the next
   person writing a bundled book will meet the same wall, and should know it was expected. This item belongs here, next to the things that make distribution real.

   ⚠ **Corrected 2026-08-15.** An earlier version of this entry claimed the decision could not wait
   and had to be made before a module could be shared. That was wrong twice over, and the reasoning
   is worth keeping so it is not repeated:
   - **The urgency was borrowed from a world that does not exist yet.** "Everything is public
     becomes permanent" is true of a published package, not of a program you can only run.
   - **The obvious fix would undo the unification.** An object exposes all its methods; a module IS
     an object; so a module exposing all its methods is not an accidental default, it is the
     consistent behaviour. Requiring modules to declare an export surface would make them behave
     differently from ordinary objects — the special-casing the arc above exists to delete. It also cuts
     against the language's temper: you *should* keep your helpers to yourself, and we do not make
     you, the same way we do not stop you writing a magic number.

   ★ **When it is time, no new concept is needed — and this is the part worth keeping.** Measured
   2026-08-15: **an interface already restricts what is reachable.** Calling a method that is not on
   the interface, through the interface, is refused with *"interface 'greeter' has no method named
   'helper'. Available methods: 'greet'."* So export control is: bind the pulled name at a declared
   interface instead of at the object type.

   ```
   Define greeter as an interface for { The text function greet }.
   Define object greeting-kit with () and greeter and module: ... Done.

   Pull greeting-kit.
       State cast greeting-kit's greet on ().    ← in `greeter`, so reachable
       State cast greeting-kit's helper on ().   ← refused, by machinery that exists today
   Done.
   ```

   No `private` keyword, no visibility modifiers, no per-member markers — a positive declaration of
   what you hand out, which is how Cufet says things elsewhere (`a shadow` announces rather than
   hides). Whether a module with no declared surface then exports everything or nothing is the one
   real decision, and it is due when distribution is, not now.

3. **A package manager for books.**

### Cufet in Cufet

Three programs in increasing size, ending with the compiler. The ordering is not ceremonial:
this tier's real blocker is stated below as **ergonomic rather than capability**, and the only
way to find ergonomic blockers is to write large Cufet programs. These are the two largest
realistic ones, so they are the instrument as much as they are the goal — better to meet the
gaps across a REPL and a shell than to meet all of them at once inside a compiler.

1. **A REPL, written in Cufet.** Read a line, evaluate it, print the result, keep the bindings.

    ★ **An open design question, deliberately unresolved here:** does it *shell out* to `cufet`
    for each line, or evaluate Cufet with a Cufet-written evaluator? The first is buildable today
    and is a good program; the second is literally self-hosting's front half. Only the second
    makes this a stepping stone rather than a stop along the way, and the choice should be made
    when the work starts rather than assumed now.

2. **A shell, written in Cufet.** `examples/systems/shell.cufe` is the seed: it already reads, parses,
    dispatches and launches, and now changes directory too.

    ⚠ **Blocked on the C FFI (*After the arc*, above).** Job control needs process groups and signalling a child;
    completion needs raw terminal mode. Neither is in the language and neither should become a
    language feature — they are exactly the "call a C function" family the FFI collapses.
    Globbing and history need nothing new.

3. **The compiler, written in Cufet.** The blockers are ergonomic rather than capability: the
    data model, text handling and I/O are already sufficient, and emitting C is a route a
    Cufet-written compiler can take too.

    ★ The test oracle already exists. A self-hosted compiler can be validated by asserting
    its C output matches this compiler's — a third implementation held against the other two.

    ★ **This is where "written in Cufet, no exceptions" is finally discharged.** The language's
    floor — `If`, arithmetic, `bury`'s state-machine transform — is compiler-implemented, so the
    compiler becoming Cufet is what makes every last part of the language Cufet-written. The
    arc above deliberately did not borrow this promise; this item owns it.

### Ongoing, no fixed slot

A formal soundness proof or a fresh-eyes red-team · a periodic error-message audit for internal
vocabulary · design patterns as a book · an in-memory filesystem for the playground

**Mixed-type operator dispatch, and with it `matrix * number` scalar scaling.** Overloading
resolves by exact nominal match today, so an operator whose two sides are different types has
nowhere to land — which is the whole reason scaling a matrix by a number is unbuilt. Blanks made
the shape expressible, so this is unblocked rather than waiting; it simply has not been wanted
yet. ★ Both settled decisions around it live in [DESIGN.md](DESIGN.md): no variance, and the
Hadamard product as a named `collections` function if ever, because `*` means matrix product.

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

  *Blocker: self-hosting (the Cufet-in-Cufet tier).* A macro expander generates Cufet AST, so
  building it in C# now means building it again in Cufet later. ⚠ Macro errors are the worst part of every language that
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

  ⚠⚠ **MEASURED 2026-08-20, and it is the number this item needs: 54 of the 190 pinned blocks
  are FRAGMENTS, not programs.** Making an unresolved name a static error in final scope dropped
  the baseline from 190 to 136 in one step — every block that fell out is an illustration
  referencing names no fence defines (`Increment i by 1.`, `Define n as the length of greeting.`).
  They never ran; they passed `check` only because unresolved names were tolerated, so the
  baseline had been pinning "checks clean" for samples that could not execute. That is exactly the
  confusion below, now with a count attached — and it means the tagging work would recover 54
  samples' worth of real coverage rather than merely tidying.

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
