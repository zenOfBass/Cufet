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
only true witness. ✅ The `blueprints` book was expected to produce the next one, since deciding
whether a file is stale is exactly a reach-for-C problem. It is written now, and it produced the
OPPOSITE: `blueprints's checksum` is a native member of the book, so no program reaches for an
axiom to hash a file. ★ That is this campaign working rather than failing — the prediction was that
a capability would be missing, and the answer was to supply it.

★★ **THE SECOND REAL WITNESS, and it came from building the package manager: Cufet can read a
file, write a file and list a directory, and can neither CREATE nor DELETE one.** MEASURED — `Write`
fails on a missing parent. ⚠ It produced no axiom, because there is no axiom to write: creating a
directory is not something the FFI reaches for here, it is something the language simply lacks. The
installer routed around it honestly — `git clone` makes the directory and the clone is KEPT as a
cache rather than removed — and a cache is a real design rather than a contortion. But the next
program that needs a directory may not be so lucky, and that is the witness.

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
     region. Parallel to the unmaker ownership rule.

   **Open:**

   - ⚠ **How narrow the restart check is.** The capture set is already computed and typed — the
     compiler builds it to pass values across the thread boundary — so the rule is a filter over a
     list that exists: refuse a captured RESOURCE (a type with an unmaker, an address, an open
     file), never a captured `number` or `text`. Anything wider is a rule wider than its reason.
   - Restart policy: how many attempts, and what giving up does.
   - The mailbox itself.

2. **Generated pages for a book.** What a reader gets when they pull a book somebody else wrote.
   Doc comments and hover are built; pages are the half that is not.

   ★ Cheap here for two reasons. A signature is **already English**, so a page's declaration line
   IS the declaration, with no rendering of types into prose. And a book is an object, so "what is
   in it" is a member list the checker already has.

   ✅ **ITS REASON TO WAIT EXPIRED, 2026-09-17.** This said pages were worth most once there were
   books by other people to read, *"and the loader and the package manager are both still below"*.
   Both have shipped: a pull resolves across files, and `cufet install` fetches a book from a git
   repository at a pinned commit, transitively. So a person can now hold a book somebody else wrote
   — and has **no way to find out what is in it** short of opening the file. That is the gap this
   item closes, and it did not exist a week ago.

   ★ A good way in is to install a real book and try to use it knowing nothing, because what you
   cannot find out is the specification for the page. Designing the shape first is guessing.

   ✅ **The bundled-book fork is SETTLED, 2026-09-16: pages are for USER books only**, and the
   premise it rested on was wrong. The entry claimed BOOKS.md and the `///` comments "say the same
   things". MEASURED: they do not. BOOKS.md is an INVENTORY plus book-level behaviour (*"Each
   yields void for an empty series"*); the `///` carries per-member semantics (*"`-2.5` floors to
   `-3`, not `-2`"*) that appear nowhere in BOOKS.md. They are complementary, so there was nothing
   to generate away.

   ★ **The only real staleness was the inventory, and it is now pinned** by
   `BookDocumentationTests`: every public member is named in BOOKS.md, and every member BOOKS.md
   claims a book provides still exists. ⚠ MEASURED public surface: sixteen — 13 Cufet-layer
   members, `math`'s `pi` and `e`, and `blueprints`' native `checksum`.

   ✅ **Hosting is NOT a fork — that was settled 2026-08-24 and both halves have landed.**
   `playground.yml` publishes Pages from an UPLOADED ARTIFACT (`configure-pages` →
   `upload-pages-artifact` → `deploy-pages`), not from a folder on main, so `docs/` is a plain
   directory with nothing to conflict over, and the `docs/` move itself shipped. ⚠ This entry
   deferred to that question for three weeks after it was answered.

   **One fork left:**

   - **What a page IS, and where it is published.** The shape of the generated page, and whether it
     joins the playground in the Pages artifact or is a file in the repo. ★ Neither is blocked —
     both are now ordinary decisions about output rather than a wait on hosting.

   ⚠ **Whatever is generated must be pinned and tested**, the way the doc-block fence tags and
   `examples/expected/` already are — generated output that nothing checks is the same staleness
   in a new place, and a hand-edited "generated" page is the second lying copy immediately.

3. **Teaching the language: a documentation site, and an interactive tutorial.** The playground
   runs the real interpreter in the browser, loads the corpus, shows squiggles and survives a
   runaway program. What it does not do is teach anybody anything — the only way in is
   `REFERENCE.md`, which is over four thousand lines and is a reference rather than a way in.

   ★ **The vehicle already exists**, which is what makes this an arc rather than a wish: a tutorial
   whose examples RUN, in the page, against the same front end that compiles them, is a playground
   with prose around it. Nothing new has to be built to execute a lesson.

   **One decision, still open:**

   - **What the site IS.** Generated pages, hand-written lessons, or REFERENCE reorganised.
     ✅ **Where it LIVES is already answered** — Pages publishes an uploaded artifact, which is
     how the playground ships, so a site has somewhere to go without anyone deciding anything.
     ⚠ This bullet used to defer to a "`docs/`-folder and GitHub-Pages question" settled on
     2026-08-24, and pointed at the wrong item while doing it.

   ✅ **THE CURRICULUM IS SETTLED, 2026-09-16**, and lesson 1 is written — `docs/TUTORIAL.md`,
   with every program and every message in it run by the suite.

   ★★ **Ordered by the refusals a beginner MEETS, not by topic.** `REFERENCE.md` reaches the type
   system in Part V; lesson 1 meets a type refusal on its SECOND LINE. A tutorial built on the
   reference's shape would teach in chapter five what the reader hit in minute one.

   1. Say and compute — text vs number. ✅ written.
   2. Absence — `void`, `but void is`.
   3. Choose and repeat — `If`/`Otherwise`, `For each`, `Judge`.
   4. Collections — series, maps, records.
   5. Borrowing — `Pull a book on math.` and `collections`.
   6. Your own words — `Bind`, parameters, `Return`.
   7. When it can fail — `Try`, failure. The language makes you handle it, so it cannot be late.
   8. Your own things — objects, fields, methods, and their UNMAKERS.
   9. Things that own things — rabbits, and why a structure needs a region.
   10. Reading and writing — files, arguments, input.
   11. Your own books — a second file, modules, and the library-versus-program rule.
   12. A project — `blueprints` and `cufet build`.

   ⚠ **MEASURED, and it placed 9:** no example in `basics/` needs a rabbit. Twelve of fifty pull
   one, and every one is a data structure, concurrency, `patterns`/`stashes`, or FFI — so a rabbit
   arrives exactly when something OWNS something else, and not before.

   ✅ **Lesson 8 is unblocked: item 5 is SETTLED, not pending.** An unmaker fires at the
   `Done.` of the BLOCK a binding was declared in — an `If` or a loop is enough, no rabbit
   needed — and a frame body is deliberately not a block. So the lesson teaches the block rule
   and the idempotency caveat, which is what a reader needs, rather than waiting on a change that is
   not coming.

   ✅ **Lessons 11 and 12 used to depend on the package manager, and no longer do** — it shipped.
   They teach what `books/` holds, what a `blueprint.cufe` carries and whether a book is one file,
   and all three are now settled: flat, pins as well as steps, and yes. Lessons 2 to 10 never
   depended on any of it.

   ★ **Deliberately outside the string**, to stop it becoming a second reference: concurrency
   (tasks and channels, a follow-on after 9) and the type system proper (interfaces, generics,
   unions — introduced where they are needed rather than as a unit).

   ⚠ The order of 2 and 7 is the least defended. Absence and fallibility are both distinctive
   enough to want to be earlier, and writing the broken programs would settle it.

   ▶ **DESIGNED 2026-09-14, not built.** The author's vision, and the reasoning that came out of
   working it through. The CONTENT is the work and it is medium-independent — write the lessons as
   markdown pinned by the doc fences first, and decide the shell afterwards with them in hand.

   - **The frame: the Velveteen Rabbit meets Peter Rabbit.** Grace and Hopper are not real yet, and
     sneak into the garden to find out how. ★ The mascots are a LANGUAGE FEATURE — `Pull a rabbit.`
     brings one into being — so the last lesson can be the learner writing it, and they become real
     because the learner made them. The story's ending is the language's most distinctive construct.
   - ★★ **Debugging FIRST, writing later.** Most tutorials teach the happy path and leave the first
     error message as a cliff. Cufet inverts well because its refusals explain themselves. Four
     stages, and they map onto the language's own taxonomy: *it won't run* (a refusal) → *it runs
     and is wrong* (a failure) → *fill in the hole* → *blank page*.
   - ★★ **The curriculum is the refusal messages.** Every "no" already names the rule, what you
     did, the fix, and an example. A lesson picks which no to teach and lets the compiler deliver
     it. It cannot drift from the language, because the language is doing the teaching.
   - **Dialogue, not narration.** Grace is careful, Hopper is not. The learner overhears rather than
     being lectured, and identifies with the one making the mistake. *"Good little rabbits always
     remember to …"* is Grace's catchphrase; Hopper pushing back is what keeps it from preaching.
   - ⚠ **Authoring constraint:** a broken program must be broken in exactly ONE legible way, or the
     refusal points somewhere other than the mistake and the learner concludes the compiler is
     confusing. And the check must compare OUTPUT, never just "it ran" — otherwise deleting the
     offending line passes.
   - **Shape, weighed:** a branching tree was DECLINED (2^N paths, and it fights the prerequisite
     order). What survives is a **navigable map with gates**, which makes the dependency graph
     diegetic — nodes are written once, and a locked door is a puzzle you cannot yet solve rather
     than a permission check. ⚠ That is a small GAME, and none of it is Cufet work. The spine must
     be decided before rooms are written, or the becoming-real ending cannot know you are ready.

   ✅ **The parse-error blocker is CLEARED, 2026-09-15**, and it was found by trying to teach: a
   beginner meets a missing `Done.` long before a type error, so debugging lessons could not start
   where beginners actually start. Unclosed blocks and declaration bodies, a missing `Done.` before
   `Otherwise`, `Define x = 3`, and a reserved word used as a loop variable all explain themselves
   now. ★ It was a gap in the language's own voice rather than a tutorial problem, which is why it
   was worth fixing first — the lessons would have had to apologise for it.

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

4. **Unmaker fire-timing — SETTLED 2026-07, and do not re-derive it.** ⚠⚠ An unmaker does not
   fire for a binding declared directly in a function or method FRAME, nor at the program's top
   level. MEASURED in both backends. It looks like a bug every time somebody meets it, and it is
   not one.

   ★★ **Settled MATCH-EXACTLY after ~19 probes:** replicate the interpreter's block-scope LIFO
   firing precisely, *including* the value-copy and escape double-fires and both gaps — don't
   "fix" any of it. The four recorded reasons, each of which kills the obvious repair:

   1. **Deterministic, so sound and oracle-able as it stands.** Block-exit LIFO is the same shape
      the open-files cleanup stack already uses.
   2. **The double-fire IS the language.** Cufet objects are value types with NO IDENTITY, so an
      unmaker is a per-binding HOOK and N copies mean N unmakings. ⚠ Exempting "the returned
      binding" — the obvious narrow fix — requires exactly the identity the language declines.
   3. **The frame gap is LOAD-BEARING.** Firing at frames would unmake a returned local WHILE IT IS
      BEING RETURNED. Closing it properly needs escape analysis, which is the deferred arena arc.
   4. **Unmaking is NOT deallocation.** The arena owns all memory and exposes no `free`, so an
      unmaker body is ordinary user code. That is why a repeat is observable but SAFE.

   ✅ **What was genuinely overdue was the DOCUMENTATION the decision called for, and it is done
   now (2026-09-16).** REFERENCE described RAII without saying a frame body is excluded, and never
   carried the idempotency caveat at all. Both are in it.

   ⚠ **FFI resources are NOT affected the same way**, which is worth knowing before anyone reopens
   this: foreign releases are a FLAT list with a per-block base, so one acquired inside a function
   body still runs at the nearest enclosing block — late, not never. Unmaker bindings live in
   per-scope dictionaries and are simply dropped.

   ▶ **To reopen it is to take the escape-analysis arc**, not to write a narrow rule. The blocker
   it shares with **Move semantics at channel send** is the same missing concept: a way to say
   *"this binding is spent."*

5. **A module a person can write that owns a lifetime.** ✅ The DISTINCTION is settled
   (2026-09-17, see DESIGN under *What Cufet is for*): **a module may own a lifetime, a book may
   not.** What is unbuilt is both halves of making that true.

   - **Nobody can write one.** `rabbit` is the only object in the language that owns a region, and
     it does not get that by being a module — `Pull a rabbit` is its own AST node emitting
     `cufet_arena_push()`, while a book or module pull is *"no arena push, no runtime book value,
     no linking"*. So the power exists and the door to it does not.
   - **Nothing stops a book from owning one either**, because nothing owns one. The restriction
     has no teeth until there is something to restrain.

   ⚠⚠ **This is the ambitious end, and DESIGN already prices it:** a user-definable lifetime
   means user-definable continuations, and it lands on the unanswered *"which restriction?"*
   question in the rabbit control-flow arc. Settling THAT is the first move, not writing syntax.

   ★ What is already paid for: the region model's outward-only invariant was tested adversarially
   and its three holes closed (see DESIGN, *Memory and concurrency*). The soundness story a
   user-written region would need is the one rabbits already run on.

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

- **Named captures for `regex`.** *Blocker: no witness has asked.* The only part of the pattern
  book refused as *not yet* rather than *cannot* — an automaton can carry them, at real cost in
  complexity. ⚠ Everything else still refused is refused PERMANENTLY and for a stated reason; see
  DESIGN, *The pattern engine is an automaton, and is written in Cufet*.

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
  books are user-authored and depended on. ⚠ The package manager has now SHIPPED, so the condition
  is met and this is waiting on real third-party books rather than on a tool.
  Until then, named makers (`making a <type>`) already cover "I do not want to write six
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

### Memory and concurrency

- **Move semantics at channel send.** A send deep-copies across the thread boundary. That is
  sound, and it is what keeps the two threads' arenas disentangled, but it is not free. A move
  — transferring ownership and invalidating the sender's binding — would avoid the copy.
  *Blocker:* the language has no way to express "this binding is spent."
