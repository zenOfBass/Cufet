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

The original finish line — **a readable systems language that compiles to a native
binary** — has been reached. Cufet has two backends sharing one front end: a tree-walking
interpreter and a compiler that emits C and invokes `gcc`. Every committed grammar feature
works on both.

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

### Tier 0 — cheap, and closes open edges

1. **Awaits inside tasks.** The last loud refusal inside a shipped arc.
2. **Write up the recursive-structure pattern in REFERENCE.** `voidable` supplies the "or
   nothing" terminator a recursive shape needs — a node's `next` is `a voidable node` — and
   [`examples/arbtree.cufe`](examples/arbtree.cufe) exercises it, but REFERENCE never states
   the pattern, so a reader has to reverse-engineer it from an example. Nothing blocks this; it
   is simply unwritten.

### Tier 1 — usable by someone other than the author

3. **Column tracking, then semantic tokens.** `Token` carries a line and no column, and the
   AST carries `Line` in roughly ninety places. Threading columns through the shared front
   end pays for two things at once: **semantic highlighting** that knows a name's kind the
   way a language server does, and **diagnostics that underline the actual expression**
   instead of the whole line.

   Worth being clear about the ceiling it lifts: a TextMate grammar is regex over one line
   and cannot know what a name *refers to*. No amount of grammar work closes that gap.

4. **A diagnostics tier (warnings).** Everything today is an error or nothing — which is what
   blocks the two items below, plus the dead-capture-write warning.
5. **Style linter.** A layer separate from the parser, flagging legal-but-unclear code as
   warnings and never errors. First intended rule: **warn on nested bare-`it` loops** —
   shadowing is legal and well defined (innermost wins), but a reader loses track. Also the
   natural home for the "capitalise the start of a statement" guidance the parser deliberately
   does not enforce, and for suggesting multiline formatting of large record and object shapes.
6. **Formatter.**

### Tier 2 — leverage

7. **C FFI, including an explicit address-of.** What makes "anything can be written in Cufet"
   literally rather than nearly true.

### Tier 3 — the design mountain

8. **Multi-directional predicate dispatch.** Needs its own design session; watch the
   no-subtyping invariant. See above for why it is not optional.

   It also unlocks something concrete and small: **mixed-type operator dispatch**, and with it
   `matrix * number` scalar scaling, which is deferred today for exactly that reason. (The
   Hadamard product is *not* blocked — it is decided: if ever added it will be a named
   `collections` function, never an operator, because `*` means matrix product and there is one
   canonical way.)

### Tier 4 — modules, strictly in this order

9. **The `module` interface.** A named interface defining the contract for any loadable thing.
    It comes first because it is the stable seam everything else in this tier depends on, and it
    is buildable well before the loader — which means the loader can arrive later without
    churning what already uses a book.
10. **Separate compilation and an external book loader.** ⚠ Known collision: the bounded
    open-union representation is sound *because* the whole program compiles at once. Either
    feature forces revisiting it.
11. **A package manager for books.**

### Tier 5 — self-hosting

12. **Cufet written in Cufet.** The blockers are ergonomic rather than capability: the data
    model, text handling and I/O are already sufficient, and emitting C is a route a
    Cufet-written compiler can take too.

    ★ The test oracle already exists. A self-hosted compiler can be validated by asserting
    its C output matches this compiler's — a third implementation held against the other two.

### Ongoing, no fixed slot

Dead-capture-write warning (after diagnostics) · Approach B parser-hardening · a formal
soundness proof or a fresh-eyes red-team · a periodic error-message audit for internal
vocabulary · a performance number against C · logic gates as a book · design patterns as a
book · an in-memory filesystem for the playground.

---

## Deferred — blocked on something that is not itself on the list

These are **not** numbered above, and that is the point rather than an oversight. Everything in
*What's next* is ordered because its blocker is either nothing or another numbered item. Each
entry here is blocked on an arc that has not been designed, or on a use case that has not
arrived — so giving it a position would be fiction, and the ordering above is only worth
anything if it means something.

They are also not *Considered and set aside* below: nothing here has been argued down. Each
states its blocker, because a deferral without one is indistinguishable from having forgotten.

**Promote an item the moment its blocker becomes a numbered item.**

### Language

- **Ordering by an explicit basis.** Ordering works on numbers and bits. Extending it to text
  and beyond should use a stated basis rather than new operators or a silent default:
  `is less than X by length`, `is greater than X by character code`, a series sorted `by size`.
  Naming the basis is what avoids undefined-collation problems — case, locale and Unicode
  become named bases instead of hidden assumptions. *Blocker:* intended shape only, undesigned
  in detail.

- **Text refinements.** The everyday toolkit is complete (join, measure, convert both ways,
  split, search, find, slice, replace, case, trim). What remains is fancier: locale-aware
  casing (`in uppercase`/`in lowercase` are invariant-only today), title-case, leading-only or
  trailing-only trim, and a character-sequence type — `text` stays opaque, with no
  character-level indexing. *Blocker:* waiting on a real use case, deliberately.

- **Expression-level flow-narrowing.** Narrowing works on *variables* today
  (`If maybe-x is not void: … maybe-x`). Narrowing a value produced by an *expression* — say
  re-reading `the entry for "alice" in ages` inside an already-checked branch without naming
  it — is not supported. *Blocker:* the checker would have to track which expression was
  checked and invalidate on mutation, which is unsound against mutable maps unless done very
  carefully. "Name your lookups" covers the need meanwhile.

### Types and objects

- **Reference-semantics opt-in.** Objects and map values are value-typed. An explicit way to
  ask for shared semantics has no syntax. *Blocker:* its own design session; it interacts with
  the region model, which is what currently makes value semantics free.

- **Fallible setters.** A setter that can reject a value is deliberately not supported, because
  the current rule keeps `becomes` infallible *everywhere*. *Blocker:* an effect-tracking arc —
  a fallible setter would require effect annotations on every assignment expression. Not
  designed, not near-term.

### Memory and concurrency

- **Move semantics at channel send.** A send deep-copies across the thread boundary. That is
  sound, and it is what keeps the two threads' arenas disentangled, but it is not free. A move
  — transferring ownership and invalidating the sender's binding — would avoid the copy.
  *Blocker:* the language has no way to express "this binding is spent."

---

## Long-term direction

Not queued features. These are the directions that orient nearer decisions, and their main
present value is revealing which nearer items are load-bearing.

### The memory model

**Cufet manages memory through regions.** A region is a span of memory whose contents all
live and die together: every value lives in some region, and when the region ends, everything
in it is freed at once. There is no garbage collector and no manual `free`.

`Pull a rabbit.` opens a region; its `Done.` closes it. The invariant that makes this sound
is **outward-only**: a value may be stored into a longer-lived region, never the reverse. The
type checker annotates every store that crosses a boundary, and the compiler copies the value
outward so nothing is left pointing into memory that is about to vanish.

This is settled and shipped. It is listed here because it is the decision everything else
rests on — see [DESIGN.md](DESIGN.md) for the reasoning and the adversarial arc that closed
the last holes in it.

### What a rabbit actually is

The rabbit shipped as a memory region, and everything written about it describes it that way.
That is accurate but incomplete about the intent.

**A rabbit was conceived as a control-flow primitive that happens to use memory.** The arena
is the *substrate*; the purpose is control-flow machinery — continuations, suspend and resume,
capturing and restoring execution state. Concurrency belongs to the same family, because a
task that yields and resumes *is* a continuation being captured and restored. Green threads
are continuations; coroutines are continuations; the exception path is a one-shot escaping
continuation. One primitive underneath all of them.

Two pieces of evidence that this is not retrofitted reasoning:

- **The implementation already contains two restricted continuations.** `In case of exception`
  compiles to `setjmp`/`longjmp` — a one-shot escaping continuation. Tasks are the parallel
  form. The unified substrate is half-real already.
- **The surface drifted toward the conception on its own.** An earlier design session settled
  on *implementation* coupling: a unified substrate underneath, but a standalone
  `Start a task:` surface spawnable anywhere. What actually got built is
  `Have rabbit start a task:`, which *requires* an enclosing rabbit — and channels require one
  too. That is surface coupling, and it is what the original conception implies.

**The open questions, in the order they need answering:**

1. **Surface or implementation coupling?** The code and the recorded decision disagree, and
   everything downstream — how `bury`, `unbury` and continuations read — inherits the answer.
2. **Which restriction?** This decides whether the feature is buildable at all. Full
   first-class continuations would need CPS-transforming the whole program (destroying the
   readable, self-contained C the compiler emits) or copying the machine stack (nonportable,
   and in conflict with both the sanitizers and the thread-local arenas). Coroutine-shaped
   continuations — save state, resume in order, one live resumption — are very achievable and
   cover nearly all of the value.

   ★ **The no-divergence rule decides this independent of implementation cost.** Whatever
   ships must work identically on both backends, and a tree-walking interpreter cannot
   faithfully offer `call/cc` either. The oracle discipline makes the design call.
3. **No implicit accumulator.** The original sketch had the rabbit *hold* an unburied value in
   temporary state until used — an invisible register. That cuts against a language that made
   narrowing explicit and refuses invisible capture writes. `Define x as unbury <stash>.` gets
   the same feature with no hidden state.

**A stash is saved execution state, not a stack data structure.** It cannot be a library:
suspend and resume need compiler and runtime support. (The naming is Turing's — the ACE design
used *bury* and *unbury* for subroutine linkage.)

---

## Considered and set aside

Recorded so they stop coming back. Each can be reopened by a new argument, not by a fresh
suggestion of the same one.

- **A REPL, as a near-term item.** The funnel is *read a post → click a link → try it*, and a
  REPL still requires an install. A browser playground converts far better for the same work,
  which is why it holds the Tier 1 slot instead. A REPL remains a pleasant thing to have
  eventually; it is not the bridge to users.
- **Four-valued logic / tetralemma.** `fact`, `voidable` and unions already cover the space,
  and a fourth overlapping way to say "not exactly true" cuts against one-canonical-way.
- **Assembly and LLVM IR interop.** The emitted C already reaches `asm` when needed, and FFI
  covers the motivating cases.
- **An LSP.** The front end emits one diagnostic per run with a line and a long prose
  explanation, so LSP's incremental machinery has nothing to earn back. Revisit for
  go-to-definition, completion and rename — the features that genuinely need a resident index.
- **A full shell (Xonsh or csh style).** This is a *product built with Cufet*, not work on
  Cufet — a flagship application needing `cd`, job control, globbing, history and completion.
