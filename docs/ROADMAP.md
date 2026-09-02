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

## Shipping a book, strictly in this order

The `module` interface, the loader that reaches a module in another file, and the types a module
carries are all shipped — so a program can be split across files and a type can cross between
them. What is left is what happens once a module is worth handing to someone else: what you hand
out, and how it travels.

1. **What a module exports.** Every MEMBER is public API, permanently. A module author has no way
   to say *this is my helper, do not call it* about a method.

   ★ **Half of it is already gone.** A loaded file’s top level is private to that file, so a
   helper that never wanted to be a member does not become one. What is left is the member that
   genuinely has to be one — it needs `one`, or it belongs to the type — and is still handed out.

   ⚠⚠ **Still not due, and the reason is worth keeping because it has now been got wrong
   twice.** The loader shipping makes it TRUE that anyone’s book can be depended on, and that
   reads like the trigger — but nothing travels yet. There is no package manager, so loading
   reaches a file you wrote, beside the one you are running. "Everything is public becomes
   permanent" is a fact about a published package, not about a program only you can run.

   **The trigger is distribution, or a second author** — the package manager below, or somebody else’s book
   in your program. Not the ability to split your own program across files.

   ⚠ **Bundled books do not get the file privacy external ones do.** `WithPrelude` splices them
   before the loader runs, so a top-level declaration in `math.cufe` would be global to every
   Cufet program. Latent today — the bundled books open straight with their module and declare
   nothing beside it — but it is why `guarded-times` had nowhere to go and was inlined twice,
   magic constant and all, while an external book’s author now has somewhere to put it. Whatever
   this item decides, the two kinds of book should agree.

   ★ **No new concept is needed, and this is the part worth keeping.** Measured 2026-08-15: an
   interface already restricts what is reachable through it. So export control is binding the
   pulled name at a declared interface rather than at the object type.

   ```
   Define greeter as an interface for { The text function greet }.
   Define object greeting-kit with () and greeter and module: ... Done.

   Pull greeting-kit.
       State cast greeting-kit's greet on ().    ← in `greeter`, so reachable
       State cast greeting-kit's helper on ().   ← refused, by machinery that exists today
   Done.
   ```

   ⚠ **A module IS an object, and an object exposes its methods** — so exposing everything is the
   consistent behaviour, not an accidental default. That is the argument against requiring an
   export surface, and it is why the interface form is a positive declaration of what you hand out
   rather than a marker on what you keep. Whether a module with no declared interface then exports
   everything or nothing is the one real decision left.

2. **A package manager for books.**

## Cufet in Cufet

The ordering is not ceremonial: this tier's real blocker is stated below as **ergonomic rather than capability**, and the only way to find ergonomic blockers is to write large Cufet programs. They are the instrument as much as they are the goal — better to meet the gaps one program at a time than to meet all of them at once inside a compiler.

★ **The REPL is written, and it worked as the instrument.** `tools/repl.cufe` and the `tools/terminal.cufe` module it pulls found four things nothing else had: the oracle could not type-check a multi-file program at all, `cufet check` passed programs that died on an undefined name, a module could carry no types, and a released version can be correct in all nine source places while the installed tool is two releases behind.

1. **A shell, written in Cufet.** `tools/shell.cufe` reads, parses, globs, dispatches, launches with the
    terminal, changes directory, and takes as many arguments as you type.

    ★ **What is left, in order:** an exit status, which needs a spelling — the launching form
    gives back nothing by design; then pipelines and `<`, which need a pipe built from a count
    known at run time and a way to feed a child’s stdin.

    ★ **Job control is last and is not sized.** Process groups and signalling need a way to name a
    running child, and the language has none. Not a language feature when it comes — it is the
    "call a C function" family, and axioms reach it.

    ★ **The editing is the book’s, not the shell’s.** Arrows, Home/End, Ctrl-U and history live in
    `tools/terminal.cufe` and are pulled by both programs. The shell writes no editing code.

2. **The compiler, written in Cufet.** The blockers are ergonomic rather than capability: the
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

3. **Compile-time macros — the `cufet` tag's expander.** Not a third program. It is here 
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

## The design mountains

All need a design session before they can be ordered against anything. They are here because
they are large, not because they are waiting — the order among them means nothing yet.

1. **Rabbits as actors.** Not a new mechanism so much as a NAME for what the region model already
   bought. A rabbit today owns an isolated arena, owns the tasks it spawns and joins them at
   `Done.`, shares nothing mutable across threads, and has escape rules the compiler enforces —
   isolated heap, owned lifetime, supervised children, no shared state. That is the actor
   invariant, and the expensive part of it (the escape analysis) is built and shipping. No VM.

   **★ One piece stands on its own: failure is not isolated.** A task that raises with no local
   `Try` tears down the whole program. (The two backends at least clean up identically on the way
   out now — the destructor divergence there was a separate defect, and it is closed.) "Let it
   crash" wants the inverse of the whole behaviour: the child's
   region dies, the parent is told, the program continues. The mechanism is already there — a
   rabbit's region dying IS "that actor's state is gone" — it is simply not wired to failure.

   **The fork that decides how big the rest is:** a rabbit's lifetime is **lexical** (`Pull …
   Done.` opens, joins, frees) and an actor supervisor's is usually **dynamic** — it outlives the
   children it restarts. If "the block restarts its child in place, until it succeeds or gives up"
   is enough, this is small work on machinery that exists. If supervisors must outlive their
   scope, it reopens the region model's central rule, and that is the same "which restriction?"
   question the rabbit control-flow arc already carries.

   ⚠ **Restart needs re-runnable bodies.** Re-running a body means resources are REACQUIRED, not
   merely re-entered, and the language has no notion of that today.

   ⚠ Identity and a mailbox are the other missing half — today channels are wired by hand, where an
   actor is addressable and has one inbox. ★ The surface may already read for it: `Have <rabbit> …`
   is a message-send shape that exists for other reasons (`Have hopper bury n.`,
   `Have hopper start a task`).

2. **Documentation comments, and generated pages for a book.** What a reader gets when they pull a
   book somebody else wrote.

   **★ It shares its one blocker with editor hover, which is the argument for doing that blocker
   sooner than either feature alone would justify.** Comments are consumed inside the lexer's
   `SkipWhitespace` and never become tokens, so nothing downstream can see them. Teaching the lexer
   to carry them as **trivia** unblocks doc comments and editor hover at once — two features
   behind one change to the shared front end.

   ★ Cufet makes this unusually cheap in two ways. A signature is **already English**, so a page's
   declaration line is the declaration, with no rendering of types into prose. And a book is an
   object, so "what is in it" is a member list the checker already has. ★ The delivery pattern
   exists too: `cufet tokens --json` already answers per-name questions over JSON for the editor,
   and hover is the same data arriving one step earlier than pages do.

   ⚠ **Ordered by value, not blocked** — say it precisely, per the warning at the top of this file.
   Nothing stops generating a page for one `.cufe` today. But pages are worth most when there are
   books by other people to read, and the loader and the package manager are both still below
   ("Shipping a book"). A generator is a tool with little to point at until then.

   **Two forks, both real:**

   - **Output format**, which lands on the deferred `docs/`-folder and GitHub-Pages question — if
     Pages ever publishes from `docs/`, that folder IS the site and generated pages belong to it.
   - **Do the BUNDLED books get generated pages?** REFERENCE documents `math`, `collections` and
     `chance` by hand today. Generating them too is two places telling one story, which this
     project has a rule about. Either generated pages are for USER books only, or that part of
     REFERENCE becomes generated. Decide before building, not after.

   ⚠ **Whatever is generated must be pinned and tested**, the way the doc-block fence tags and
   `examples/expected/` already are — generated output that nothing checks is the same staleness
   in a new place, and a hand-edited "generated" page is the second lying copy immediately.

   ★ **What a doc comment should be FOR** is worth settling early: the signature already says what
   a thing takes and gives, so a comment that restates it is a second copy that drifts. What is
   left is *why*, and *what can go wrong* — which is what this codebase's own ★/⚠ convention
   carries in its C# and C.

## Ongoing, no fixed slot

A formal soundness proof or a fresh-eyes red-team · a periodic error-message audit for internal
vocabulary · design patterns as a book · an in-memory filesystem for the playground

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

- **A cursor for scanning a `chase`.** *Blocker: no demonstrated need.* The buffer ships with bare
  indexing — `item n of`, `For each` — and `huffmancoding` was rewritten onto it without wanting
  anything else. A cursor would only pay for itself in a program that scans BACK AND FORTH, and
  the five text processors that hand-roll their own scanning (`recursivedescent`, `json`, `config`,
  `markov`, `wordfreq`) have not been moved onto it yet.

  **The trigger:** the first of those that ends up carrying its own position variable around just
  to read a buffer. That is the shape a cursor replaces, and until one appears there is nothing to
  design against.

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

  ⚠⚠ **This is the half that carries the collision**, and the loader above does not. Three things
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
