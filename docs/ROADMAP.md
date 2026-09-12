# Cufet Roadmap

Where Cufet is going.

| If you want to know | Read |
| --- | --- |
| What Cufet is, and why you might care | [README.md](../README.md) |
| How to use a feature | [REFERENCE.md](REFERENCE.md) |
| What is in the bundled books | [BOOKS.md](BOOKS.md) |
| Exactly what the rules are, and the sharp edges | [GRAMMAR.md](GRAMMAR.md) |
| What changed, and when | [CHANGELOG.md](../CHANGELOG.md) |
| **Why** the language is like this | [DESIGN.md](DESIGN.md) |
| How to build, test and contribute | [CONTRIBUTING.md](../CONTRIBUTING.md) |

Cufet is pre-1.0 and may still change. Versioning is semantic: feature arcs bump the minor
version, and 1.0.0 will mark the point at which the language is considered stable.

## Cufet in Cufet

The ordering is not ceremonial: this tier's real blocker is stated below as **ergonomic rather than capability**, and the only way to find ergonomic blockers is to write large Cufet programs. They are the instrument as much as they are the goal — better to meet the gaps one program at a time than to meet all of them at once inside a compiler.

1. **The compiler, written in Cufet.** The blockers are ergonomic rather than capability: the
    data model, text handling and I/O are already sufficient, and emitting C is a route a
    Cufet-written compiler can take too.

    ★ **The Cufet-written evaluator lands here, and nowhere earlier.** The REPL weighed writing
    one against shelling out to `cufet`, and shelled out: `cufet` already holds the type checker,
    and a hand-written evaluator would say something worse about a bad line than the compiler
    already says. An evaluator is also a THIRD implementation — and unlike a self-hosted compiler,
    which the oracle checks for free by diffing its C against this one, an evaluator emits nothing
    to diff. It gets validated here or it gets validated twice.

    ★ The test oracle already exists. A self-hosted compiler can be validated by asserting
    its C output matches this compiler's — a third implementation held against the other two.

    ★ **This is where "written in Cufet, no exceptions" is finally discharged.** The language's
    floor — `If`, arithmetic, `bury`'s state-machine transform — is compiler-implemented, so the
    compiler becoming Cufet is what makes every last part of the language Cufet-written. The
    arc above deliberately did not borrow this promise; this item owns it.

2. **Compile-time macros — the `cufet` tag's expander.** Not a third program. It is here 
    rather than in *Deferred* because its blocker is now a numbered item above, which is the one rule that section states about itself.

    Hygienic, expanding to Cufet AST before the checker runs — *not* fexprs, which are first-class and
    runtime. It is one tag of the BLOCKS type rather than a feature of its own: quoted Cufet and
    embedded foreign source live under one type name, and a macro is what consumes the `cufet` tag. See [DESIGN.md](DESIGN.md#foreign-interoperability) — including why hygiene and SQL injection turn out to be the same problem, which is what makes the unification real rather than cosmetic.

    ★ **The type shipped in 0.17.0, and a deliberately small consumer with it.** `Cite` places what a
    block holds, and a block that says what it gives back is lowered to an ordinary function. What is
    NOT built is the expander this entry means: syntax parameters, and generating AST from them.

    ⚠ **Its blocker is item 2 above.** An expander generates Cufet AST, so building one in C# now means building it again in Cufet later. Macro errors are the worst part of every language that has them, and clear errors are this language's distinguishing feature — that tax is still paid deliberately,not early.

    ★ Fexprs stay out, but the recorded reason was the weaker one. Wand's result (no two expressions
    ever equivalent, taking out `check` and monomorphization) is true; the **decisive** reason is that a compiled Cufet binary is standalone C, so running a Cufet block at run time needs a Cufet
    interpreter *written in C* — a third implementation, or a divergence. Note also that an explicit
    `eval` is not a fexpr and would cost neither `check` nor monomorphization; the C-interpreter bill is what rules it out.

## No ordinary capability should require an axiom

A campaign rather than an item. Every `axiom` a program reaches for to do something **ordinary** is
a capability the language should have had, and the axiom is the gap's own witness — written into
the source by somebody who needed it.

⚠ **Strictly about capabilities the language itself should have.** Binding somebody else's C
library — sqlite, zlib, a vendor SDK — is NOT in scope and never becomes so: the FFI is the door to
other people's code and stays. This campaign is about axioms used to reach the operating system or
to fill a hole in the language.

⚠ It is also not about the compiler emitting C or the runtime being written in it. That target is
settled (DESIGN), and "the implementation is Cufet" is the tier above's promise, not this one's.
The claim here is narrower and checkable: point at a program and ask whether it needed C.

**Open, with a witness:**

- **Job control.** `tools/shell.cufe` reaches into a `jobs-support` axiom for `&`, `jobs`, `fg`,
  pipes and `<`. The tool bends around the FFI's own limits to do it — a series cannot cross into
  an axiom, so it asks about ONE job at a time because only one number crosses back. That
  contortion is recorded in the shell's own comments, which is what makes this the clearest gap
  on the list.

★ **The list is rate-limited by how many real programs exist**, and that is the point rather than a
complaint: a capability nobody has reached for is not yet known to be missing. Measured 2026-09-11,
six corpus programs use an axiom and five are demonstrations of the FFI itself — the shell is the
only true witness. The `building` book is expected to produce the next one, since deciding whether
a file is stale is exactly a reach-for-C problem, but it has not been written and is not listed.

## The design mountains

All need a design session before they can be ordered against anything. They are here because
they are large, not because they are waiting — the order among them means nothing yet.

1. **Rabbits as actors.** A rabbit already owns an isolated arena, owns and joins the tasks it
   spawns, shares nothing mutable across threads, and has escape rules the compiler enforces —
   that is the actor invariant, and the expensive part of it is shipping. Failure is settled and
   built — see DESIGN. What is left is supervision: restart, and the mailbox.

   **Settled:**

   - **Lexical supervision only** — restart in place, within the block. Dynamic supervisors park
     with the region-lifetime arc, and a toggle between the two lifetimes is rejected: two rules
     means every escape question gets asked twice.
   - **Identity is the rabbit's name, and the send surface is `Have <rabbit> …`** — a shape that
     already exists for other reasons. The mailbox mechanism is later.
   - **A restartable body must acquire its own resources**, so a re-run re-acquires into a fresh
     region. Parallel to the destructor ownership rule.

   **Open:**

   - ⚠ **How narrow the restart check is.** The capture set is already computed and typed — the
     compiler builds it to pass values across the thread boundary — so the rule is a filter over a
     list that exists: refuse a captured RESOURCE (a type with an unmaker, an address, an open
     file), never a captured `number` or `text`. Anything wider is a rule wider than its reason.
   - Restart policy: how many attempts, and what giving up does.
   - The mailbox itself.

2. **A package manager for books.**

   ⚠⚠ **It has nowhere to put anything, and that is a LANGUAGE gap rather than a tooling one.**
   Measured 2026-09-08: a pull finds a book only in the file's OWN directory — a subdirectory is
   refused outright, and the message lists the bundled books as though nothing else could exist.
   So "install a book" today means copying it beside every file that pulls it. Fetching,
   versioning and a manifest are all downstream of a resolution rule the language does not have.

   ★ **The first slice is therefore WHERE A BOOK LIVES, not the manager.** That is a language
   question and a far better-shaped one than the tooling on top of it.

   ⚠ A second, separate gap in the same area: **a pull RUNS the loaded file's top-level
   statements**, so a file is either a program or a library and nothing says which. This is what
   makes the shell's own machinery unpullable — a script cannot `Pull a book on shell.` because
   that starts the shell. Cufet has no `if __name__ == "__main__"`, and inventing one is a
   language feature rather than a slice.

3. **Generated pages for a book.** What a reader gets when they pull a book somebody else wrote.
   Doc comments and hover are built; pages are the half that is not.

   ★ Cheap here for two reasons. A signature is **already English**, so a page's declaration line
   IS the declaration, with no rendering of types into prose. And a book is an object, so "what is
   in it" is a member list the checker already has.

   ⚠ **Ordered by value, not blocked** — say it precisely, per the warning at the top of this file.
   Nothing stops generating a page for one `.cufe` today. But pages are worth most when there are
   books by other people to read, and the loader and the package manager are both still below
   ("Shipping a book").

   **Two forks, both real:**

   - **Output format**, which lands on the deferred `docs/`-folder and GitHub-Pages question — if
     Pages ever publishes from `docs/`, that folder IS the site and generated pages belong to it.
   - **Do the BUNDLED books get generated pages?** ⚠ No longer hypothetical: `docs/BOOKS.md`
     describes their members in prose, and since 2026-09-07 the books carry `///` in their own
     source saying the same things. **The two places already exist.** Either generated pages are for
     USER books only, or BOOKS.md's member descriptions become generated from the source.

   ⚠ **Whatever is generated must be pinned and tested**, the way the doc-block fence tags and
   `examples/expected/` already are — generated output that nothing checks is the same staleness
   in a new place, and a hand-edited "generated" page is the second lying copy immediately.

4. **Teaching the language: a documentation site, and an interactive tutorial.** The playground
   runs the real interpreter in the browser, loads the corpus, shows squiggles and survives a
   runaway program. What it does not do is teach anybody anything — the only way in is
   `REFERENCE.md`, which is over four thousand lines and is a reference rather than a way in.

   ★ **The vehicle already exists**, which is what makes this an arc rather than a wish: a tutorial
   whose examples RUN, in the page, against the same front end that compiles them, is a playground
   with prose around it. Nothing new has to be built to execute a lesson.

   **Two decisions, both open:**

   - **What the site IS.** Generated pages, hand-written lessons, or REFERENCE reorganised — and
     where it lives, which lands on the same deferred `docs/`-folder and GitHub-Pages question
     item 2 above already carries. Both should be answered once.
   - **What a tutorial teaches first.** Cufet's shape is unusual enough that the ordinary tour
     (variables, loops, functions) may not be the right one. Rabbits and failure-as-a-value are
     what make it different, and they are not chapter nine.

   ★ **A REPL page, and it is the cheapest thing on this entry.** `tools/repl.cufe` already
   answers the question a REPL usually poses — how to keep state across evaluations — and answers
   it by REPLAY: it accumulates the typed lines, re-runs the whole program each time, and shows
   only what the new run said that the previous one had not. *"The old output is a prefix of the
   new one — the same program, with a line on the end."*

   That model gets SIMPLER in the browser. The native REPL needs a scratch file and a subprocess;
   `Runtime.Run(source)` already takes a whole program and hands back its output, statelessly — so
   the two things wasm does not have are the two things a web REPL does not need.

   ⚠ **Replay means side effects repeat**, and a page invites longer sessions than a terminal does.
   Every `State` runs again on every line — the delta hides that for output, but a fifty-line
   session re-runs fifty lines on the fifty-first, and anything that reads input or does real work
   does it again. The native REPL carries the same cost and it is accepted there; write it down
   rather than rediscover it.

   ⚠ Whatever is written must be pinned like the doc fences are: a lesson whose code stops working
   is worse than no lesson, and this project has the machinery to catch that already.

5. **`Pull a book on regex.` — the pattern language.** SHIPPED: the book, the bracketed form,
    check-time validation, the pull gate, a Thompson automaton written in Cufet, and character
    classes. `examples/parsing/logtriage.cufe` is the witness — it triages a log by the shapes its
    lines hold, which is the case patterns are actually for.

    ★ **Settled: an AUTOMATON, not backtracking.** Three reasons, and the third is the one specific
    to Cufet: a backtracking engine can hang on a pattern that looks fine; the usual defence is a
    timeout, and a timeout is nondeterministic where the oracle demands byte-for-byte agreement;
    and the engine is written in Cufet, so it is slow by construction — backtracking's worst case on
    a slow host is not "slow" but "never finishes". The cost is backreferences, which would need
    their own trigger anyway. This decides the dialect.

    ⚠⚠ **The engine is written in Cufet because the oracle requires it**, not as a preference. Two
    engines (.NET `Regex` interpreted, POSIX `regcomp` compiled) would disagree on greediness,
    classes and empty-match edges, silently, on inputs nobody tested. `collections` is the
    precedent: every member written in Cufet, so both backends run the same code.

    ★★ **NEGATED CLASSES SHIPPED** — `[^,]+`, the way anyone reads a field up to a delimiter.
    ★ The asymmetry is worth keeping in mind: `[abc]` desugared to `(a|b|c)` and touched nothing,
    while `[^abc]` cannot be written as a finite alternation and so carries a state kind of its
    own. One character apart, and only one of them was sugar. The excluded characters pack into the
    existing `ch` field, so the four-field record is unchanged for a third feature running.

    ★★ **ANCHORS SHIPPED**, and a real program chose them: `logtriage` could count and flag but not
    VALIDATE, because a pattern asks whether a subject HOLDS a match. `^` and `$` are **state kinds
    rather than flags on the pattern**, which is what lets them compose — `(^cat|dog$)` anchors each
    side of an alternation separately, and a pair of flags could not have said that. They are the
    first thing the engine needed to know beyond the state series itself, so `reach` now carries how
    much of the subject has been consumed.

    ★★ **What is left is a DEBT TO THE LANGUAGE, not a list of candidates awaiting a trigger.** A
    book on a language does not get to choose its own contents: `[a-z]` and `{2,5}` mean what they
    mean in regex everywhere, and a book that cannot spell them is a Cufet-flavoured subset wearing
    regex's name on the cover. **Trigger discipline governs what Cufet INVENTS; fidelity governs a
    book.** Refusing by name is an honest IOU in the meantime — it is why `^` was never quietly
    read as a literal caret.

    - **Counts `{2,5}`** — the last entry left in the refuse-by-name table, and the last debt on
      this list. Desugarable to repetition exactly as classes were, so no engine change.
    - **Inline flags, `(?i)` first** — ⚠ case-sensitive matching is regex's CORRECT default, so
      this is a missing feature rather than a wrong answer. BOOKS.md documents the gap and shows
      `[[Ww]arn]` as today's spelling. ⚠⚠ `(?…)` opens the whole family — non-capturing groups,
      lookahead, named groups — and wants its own conversation before any of it is built.

    ★ **This arc closes when that list is empty**, which is a finish line the language defines
    rather than one we negotiate — and closing it is what makes the release after it 0.22.0.

    Backreferences stay ruled out by the automaton decision above. ⚠ **Splicing a value into a
    pattern is not on this list and never will be** — an axiom's `the`-marker works because `the
    file-path` is not valid C, and regex has no invalid syntax to borrow, since `the needle` is a
    perfectly good pattern. Already refused in code with a test; reasoning in `docs/DESIGN.md`.

6. **`Pull a book on building.` — a build description that is a Cufet program.** No second
    language for the build, the way `build.zig` is a Zig program rather than a Makefile.

    ★ **Settled: a BOOK, not core.** Core was weighed and declined. What Zig gets right is that a
    build script is an ORDINARY PROGRAM USING AN ORDINARY LIBRARY — putting the vocabulary into the
    language would move away from that, spend permanent core surface (a build vocabulary is not
    small: targets, steps, artifacts, toolchains), and buy its way around a module gap instead of
    fixing it.

    ★★ **The prize is bigger than a build tool: a build description is a notion of a PROJECT**, and
    all three module walls are the absence of one. Where a book lives stops being "the file's own
    directory" and becomes something the build file declares; program-versus-library stops needing
    a language feature, because the build file says which a file is; and separate compilation needs
    a build graph before it can mean anything.

    ⚠ A BUNDLED book sidesteps both walls (bundled books are resolved at compile time, so nothing
    runs a pulled file's top level). The walls return exactly when EXTENSIBILITY does, because a
    user-written build extension is an external book. Good ordering: start without the module work,
    and the pressure arrives when it is earned.

    ★ **Settled: staleness by CONTENT HASH, not timestamp.** A timestamp answers "was this touched";
    a build wants "is this different". They differ at the edges — same content touched, or a change
    inside the filesystem's timestamp resolution — and the failure is a WRONG BUILD WITH NO
    COMPLAINT, which is the category this language declines everywhere else (`Exit with 256` is
    refused rather than truncated). Decisive: **git does not preserve modification times**, so a
    timestamp answer depends on how the tree arrived on the machine — the same objection REFERENCE
    already makes about line endings and how git checked a file out. A hash is also the cheaper
    capability to let in: a pure function of bytes that both backends can agree on by construction,
    where a clock is the most machine-dependent domain there is.

    ⚠⚠ **The standard optimisation is the unsound thing.** Stat first and hash only when size or
    mtime changed is what every fast build system does, and it silently reintroduces the resolution
    bug as a "fast path". Written down here so nobody adds it later as an obvious win.

    ⚠ The algorithm must be NAMED by the language, not "whatever the platform provides" — the
    shared case table's lesson, where .NET casing turned out to be ICU-backed and to differ per
    machine.

    **Neither is a blocker on starting.** A build that always rebuilds is correct and merely not yet
    useful, so the hashing surface is its own slice with this book as its witness, rather than a
    prerequisite built on spec.

## Ongoing, no fixed slot

A formal soundness proof or a fresh-eyes red-team · a periodic error-message audit for internal
vocabulary

**Composition modes for rabbits** — `sequential` / `parallel` / `automatic`, an optional adjective
in the type-annotation slot: `Pull an automatic rabbit as dispatcher.` The mode governs how a
rabbit's TASKS compose, never its ordinary statements, which stay sequential imperative code.

★ **The three differ by WHO DECIDES THE ORDER**, which is what the names have to carry: you do,
by writing them in sequence; nobody, because they run at once; or the rabbit does, driven by what
arrives. That axis is why `automatic` beat the alternatives — `hybrid`, `combinator` and
`differential` all said only "this one differs from the other two", which the list already says,
and `responsive` (the earlier name here) collides with responsive design and names a quality
rather than a behaviour. ⚠ No one-word adjective conveys "waits on several and takes the first" —
ALT, `select` and `receive` do not either — so that belongs in the prose, not the name.

- ★ **`automatic` is the only new capability, and the gap is real:** `the delivery from <channel>`
  blocks on ONE channel, so nothing today can wait on several and take whichever arrives first.
  Occam's ALT, Go's select — and how an actor-rabbit would read a mailbox.
- **`parallel` is the default**, which is what a task-spawning rabbit already does. All three stay
  sayable: a default is a voiceable choice, not an inferred silence. A rabbit that spawns no tasks
  has no mode at all.
- **One discipline per rabbit.** Mixed needs nest, as composition does everywhere else here.
- ⚠ Inherit the existing concurrency caveat rather than restating it: cooperative interpreted, real
  threads compiled, no interleaving promised.

Open: whether `sequential` and `parallel` are thin labels over the join behaviour that exists, or
need machinery of their own. `automatic` is the real build — a guarded multi-input wait. The
mailbox and message-send surface are separate, later work.

**Exponent literals** — `6.022e23` on `number`. A lexer feature; today `1.5e3` fails with
`expected Dot, got Identifier "e3"`.

- ⚠ **Notation, not scientific RANGE.** Capped by decimal (~7.9e28), so `1e50` stays
  unrepresentable. Anything wider is floats, which decimal was chosen over.

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

- **A fractional value handed INTO foreign source.** *Blocker: no use case has arrived.* A `double`
  comes BACK as a `voidable number`, and that direction ships. Going the other way is refused: a
  `number` argument arrives as a range-checked `long long`, and nothing spells "this one goes in as
  a double". Whole arguments already work by casting on the C side —
  `[pow((double)the base, (double)the exponent)]` — so what is actually missing is passing `0.5`,
  and nothing has wanted to yet.

  ⚠ **It is also the genuinely lossy direction**, which is why it should not be guessed at: `0.1`
  has no exact `double`, so the conversion has to decide a rounding, and the spelling has to make
  the writer say they meant a double at all. Both of those want a real caller to argue from.

- **A library of your own — headers AND link flags together.** *Blocker: nobody has wanted to bind
  a non-system library.* The bundled header set covers everything that links by default, so the gap
  only shows up as "this needs library X": `#include <sqlite3.h>` gets the declarations and then
  fails with "undefined reference", which is why headers alone would ship a feature that cannot
  work for the case that motivates it. ★ The trigger is checkable, which is what keeps this honest.

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

- **`Descend.` — explicit fall-through in a `Judge`.** The keyword is reserved and the typing rule
  is settled: *a fall-through target is checked under the union of every path that can reach it*.
  *Blocker:* no use case has demanded it. Grouping with `or` covers what C-style fall-through is
  overwhelmingly used for, and the shell — the program that finally wanted to judge a value —
  wanted a default, not a fall-through.

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

### Types and object

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

- **Separate compilation.** Compiling files independently and linking the results, rather than
  resolving them and compiling together. *Blocker:* there is no build-speed problem to solve —
  the whole example corpus is 2,815 lines, and a full build plus 2,782 tests runs in minutes.

  ⚠ **This is the half that carries the collision**, and the loader above does not. Three things
  are sound only because the whole program compiles at once: dispatch proves coverage by seeing
  every version of a name, the open-union representation bounds its tag set whole-program, and a
  generic is monomorphized from every filling the program contains. Separate compilation reopens
  all three at once.

  **The trigger:** builds getting slow enough to notice. Not before — buying incremental rebuilds
  with three invariants is a bad trade at any corpus size that fits on one screen.

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
