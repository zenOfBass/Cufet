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

1. **Foreign interoperability — the C FFI, axioms, and addresses.** What makes "anything can be
   written in Cufet" literally rather than nearly true. Its consumers are the "call a C function"
   family it collapses: the shell's job control and raw terminal mode, sockets, the POSIX and
   Windows APIs. ★ No bundled book needs it any more — `math` went pure decimal in 0.16.0.

   **Axioms, parameters and splicing already ship on both backends** — see GRAMMAR §6 and REFERENCE
   Part VII for what they do, and CHANGELOG for when. What is left, in the order it makes sense to
   build:

   - **An axiom passed around unrun — SCAFFOLDING IN, the rest still to build.** An axiom can now
     be bound to a name and run from there (`Define alias as answer.`), because the checker follows
     a chain of names back to the source. That needs no runtime representation: both names reach
     the same wrapper and the binding emits nothing.

     ⚠ **What is left is the backend half, and it is the whole of the rest.** An axiom still cannot
     be written down as a TYPE — a parameter, a field, an element type — so one chosen at run time
     cannot be run. Running an axiom pastes its foreign text, and a value that arrives at run time
     has no text to paste. The refusal is one arm in `ResolveParamType`; lifting it alone was tried
     and made four shapes check clean and then fail in the code generator, which is the divergence
     this project refuses hardest.

     ★ **What it would take**, for whoever picks this up: an axiom value has to become a C function
     POINTER to its wrapper (one typedef per parameter-and-result shape) and a delegate on the
     interpreted side, plus a written axiom type that can say what it takes — `<language>
     [<result>] axiom` has nowhere to put a parameter list today, which is why even the
     parameterless case cannot be checked at a call site through a parameter. `AxiomType` already
     carries `ParameterTypes`; nothing writes them from a written type yet.

     ⚠ **The bullet's old motivation was misleading and is corrected here**: this is not about
     assembling an axiom from strings, which the design forbids outright. DESIGN's line is that
     "a function assembling a SQL fragment hands one back unrun" — the block is fixed where it is
     written, and what moves is the axiom VALUE. See DESIGN "One type for code as data", which is
     the arc this is the first brick of.

   **Read [DESIGN.md](DESIGN.md#foreign-interoperability) before starting** — it carries the
   reasoning and the rejected alternatives, which is the part worth having. Addresses exist only
   inside a rabbit block, are never dereferenced except by `the text at <address>`, and are freed by
   the existing unmaker registry via `and free it with <name>`. Cufet never models a C struct: struct work
   happens inside an axiom. Every address and every read is `voidable`, so NULL lands in the
   mechanism the language already has.

   ★ The unwind side is ready: a new kind of releasable thing is one field on `CleanupPoint` and one
   term in `UnwindTo`, and every nonlocal exit gets it at once.

   ⚠ **It does not ship until both backends run it.** The interpreter is the oracle, and FFI is the
   one area where being wrong means memory corruption rather than a wrong number. Interpreted FFI
   therefore needs a C toolchain the first time a given set of axioms is seen; wasm cannot do it at
   all.

2. **Module needs, transitively.** ★ Much smaller than it was. A body now resolves what it can see
   where it is WRITTEN plus any MODULE its caller pulled, so an unresolved *ordinary* name is a
   static error and only module names still defer. Two holes are left, both over that small set:

   - **Not transitively closed.** `geometry`'s method reaching `math` only through a free function
     it calls is not caught — the need is recorded against the function, and nothing checks a free
     function's needs at its call sites. This is the call-site work, now over module names only.
   - **Indirect calls cannot be closed this way at all.** `Define f as needs-math.` then
     `cast f on (…)` names a variable, not a function; there is no statically-known callee to look
     needs up for. Closures, stashes and function values in series all take this shape.

   ⚠ So "check the call sites" is not a complete fix and should not be sold as one — it closes the
   first bullet and leaves the second. Worth knowing before anyone starts.

### The design mountains

All need a design session before they can be ordered against anything. They are here because
they are large, not because they are waiting — the order among them means nothing yet. The
formatter used to be blocked by the inline forms; those shipped in 0.15.0, so nothing blocks it
either.

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
    teaching the **lexer to carry comments as trivia** first — ★ which is also what doc comments
    and editor hover need (item 4), so that one change to the shared front end unblocks three
    things rather than one. Comments are skipped today and
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

3. **Rabbits as actors.** Not a new mechanism so much as a NAME for what the region model already
   bought. A rabbit today owns an isolated arena, owns the tasks it spawns and joins them at
   `Done.`, shares nothing mutable across threads, and has escape rules the compiler enforces —
   isolated heap, owned lifetime, supervised children, no shared state. That is the actor
   invariant, and the expensive part of it (the escape analysis) is built and shipping.

   ⚠ **No VM. BEAM was considered and declined**, and the reason is worth keeping: actors do not
   require one. Erlang's actor runtime is itself written in C, and Cufet already emits C with
   pthreads, channels and a structured join. What BEAM would add — preemption, distribution, a
   million cheap processes — is not what makes the model valuable here, and the price is a THIRD
   backend held bit-identical to the other two, a third `CufetDec` (BEAM has bignums and IEEE
   floats and no decimal), and a second FFI story for a boundary that is C-shaped by design.

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

4. **Documentation comments, and generated pages for a book.** What a reader gets when they pull a
   book somebody else wrote.

   **★ It shares its one blocker with the formatter, which is the argument for doing that blocker
   sooner than either feature alone would justify.** Comments are consumed inside the lexer's
   `SkipWhitespace` and never become tokens, so nothing downstream can see them. Teaching the lexer
   to carry them as **trivia** unblocks the formatter, doc comments, and editor hover at once —
   three features behind one change to the shared front end.

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

   ⚠ **Whatever is generated must be pinned and tested**, the way `doc-blocks.baseline.txt` and
   `examples/expected/` already are — generated output that nothing checks is the same staleness
   in a new place, and a hand-edited "generated" page is the second lying copy immediately.

   ★ **What a doc comment should be FOR** is worth settling early: the signature already says what
   a thing takes and gives, so a comment that restates it is a second copy that drifts. What is
   left is *why*, and *what can go wrong* — which is what this codebase's own ★/⚠ convention
   carries in its C# and C.

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

**`In case of exception:` without the binding, and a NAME for it when you want one.** Two changes
to one clause, and the second is what makes the first worth doing:

```
In case of exception:                     ← binds `exception` implicitly
In case of exception (the trouble):       ← binds `trouble`
```

Today the clause is **mandatory and cannot vary** — `In case of exception (the trouble):` is
refused with "expected Exception, got Identifier", so `(the exception)` is a required phrase that
says one thing at every occurrence. Meanwhile `In case of failure:` takes no binding at all and its
value is reached as `the message of the failure`. So the two arms of one `Try` disagree, and the
one that demands more says less.

★ **The pair already exists elsewhere**: `For each x in xs, repeat:` names the iterand, bare `it` is
the implicit one. Making the parens form a RENAME rather than a synonym is what keeps this from
being two spellings of the same thing — and nesting is what earns it, exactly as it does for `it`.
The `NestedBareItLoops` linter rule exists because bare `it` in nested loops is ambiguous to a
reader; nested `Try` blocks have the same problem and would want the same rule.

⚠ The explicit slot must keep accepting the literal word `exception`, which lexes as a keyword
rather than an identifier — every existing program and doc writes `(the exception)`, so the slot is
"an identifier, or that keyword". Non-breaking either way; the bare form becomes what people write.

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

**Map keys: a refusal that overreaches, and the predicate to replace it with.** Not a new feature —
a rule that is too blunt and explains itself with something untrue.

```
Define spot as a record with (1, 2).
Define grid as a map with (spot : "here").
→ 'record (number, number)' is a record — reference types can't be map keys
  because their identity changes when copied
```

⚠ **A record is not a reference type.** The checker's own `IsReferenceType` is `series, map,
object, matrix, channel`; records are absent from it, deep-copy on binding, and compare
structurally. So the message tells the reader something false about records, which is why the
workaround has never felt principled.

★ **The cost is visible in `examples/algorithms/dijkstra.cufe`**: it keys by `text` node names, so
`pq-entry` carries `the text pq-name` and every node exists twice — as an object and as the string
that stands for it, kept in step by hand. A record of two numbers is also the commonest compound
key there is (`(row, col)` for a grid or a visited-set), and it is refused.

⚠ **The rule is too blunt rather than backwards.** A record can CONTAIN a series, and then
structural equality and hashing would have to reach through an arena pointer — so records are not
uniformly value-like and "records are fine" would be its own overreach in the other direction.

★★ **The exact predicate already exists and is already tested.** `IsRegionBearing` — *"a type is
region-bearing when its compiled representation holds an arena pointer anywhere in its shape"* —
walks the shape transitively, lives in the shared project so the checker and the compiler read one
definition, and has a test locking it against `IsChanPod`'s complement. Map keys become "not
region-bearing" instead of "text, number, or fact": a record of scalars is admitted, a record
holding a series is still refused, and objects are still refused.

⚠ Whatever the rule ends up being, the MESSAGE has to stop calling a record a reference type.

**Named arguments at a call site — `cast area on (the width 3, the height 4)`.** ⚠ **Sequenced
AFTER Approach B above**, and that is the whole reason this entry sits here rather than on its own.

The rule already exists and REFERENCE already states it generally: *"wherever a field could be
positional instead, `the` is what says a name follows."* It is implemented in object literals and
record literals — `a new card { the suit "hearts" }`, `a record with ("hatchback", the make
"Honda")` — and an argument list is the third place a value could be positional or named, and the
one place the marker does nothing. `cast area on (the width 3)` does not parse today.

★ **The semantics need no invention**, because the analogous cases are already decided and
measured: named fields reorder freely (`{ the rank 7, the suit "hearts" }` works), and a record
takes positional first then named. Named arguments would do both the same way.

⚠ **The cost is that it adds a third case to the guess Approach B exists to remove.** `the width 3`
(a named argument) and `the width of box` (a named field access) are told apart by looking ahead
for `of` — which is `IsNamedAccessPattern()`, the lookahead that mis-parsed `the series of number
board`. Adding this before the guess is gone makes that job bigger, in the position where it is
hardest: inside a parenthesised list, where the comma and the `of` are both load-bearing.

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

- **Compile-time macros.** Hygienic, expanding to Cufet AST before the checker runs — *not* fexprs,
  which are first-class and runtime.

  **AMENDED 2026-08-21: this is now one tag of the BLOCKS type, not a feature of its own.**
  Quoted Cufet and embedded foreign source live under one type name; a macro is what consumes the
  `cufet` tag. See [DESIGN.md](DESIGN.md#foreign-interoperability) — including why hygiene and SQL
  injection turn out to be the same problem, which is what makes the unification real rather than
  cosmetic.

  ⚠ **The blocker narrowed with it.** *Self-hosting* still blocks the `cufet` tag's CONSUMER — a
  macro expander generates Cufet AST, so building it in C# now means building it again in Cufet
  later. It does **not** block the blocks type itself, nor foreign tags, which can ship with the
  FFI arc. Macro errors are the worst part of every language that has them, and clear errors are
  this language's distinguishing feature — that tax is still paid deliberately, not early.

  ★ Fexprs stay out, but the recorded reason was the weaker one. Wand's result (no two expressions
  ever equivalent, taking out `check` and monomorphization) is true; the **decisive** reason is that
  a compiled Cufet binary is standalone C, so running a Cufet block at run time needs a Cufet
  interpreter *written in C* — a third implementation, or a divergence. Note also that an explicit
  `eval` is not a fexpr and would cost neither `check` nor monomorphization; the C-interpreter bill
  is what rules it out.

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
