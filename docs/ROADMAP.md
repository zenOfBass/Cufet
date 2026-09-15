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

2. **A package manager for books.**

   ✅ **The first slice — WHERE A BOOK LIVES — is DONE, 2026-09-13.** A pull resolves beside the
   file that pulls it, then in `books/` at the project root, where a project is a directory holding
   `blueprint.cufe`. Local wins. Only the blueprint's LOCATION is read, so no tool has to execute a
   build description to import a book. The refusal now names the places it looked, which it never
   did — that message, more than the rule, is why this read as a language with no file resolution.

   ⚠ **Still missing for a manager: a book has nowhere of its OWN.** Everything installed would
   land flat in one `books/` folder, so two books cannot carry different versions of a third and
   nothing distinguishes a fetched book from one you wrote. The loader already resolves a book's
   own pulls beside THAT BOOK rather than beside the program, which is the half that makes a
   per-book folder work — but that is currently unobservable, and no folder shape is decided.

   ⚠ **And a book still leaks its dependencies to its caller.** MEASURED 2026-09-13 on the first
   real two-directory project: a program pulling `canvas` had to write `Pull books on palette, and
   canvas.` although it never mentions `palette` — because a module's dependencies come from the
   block it is USED in, not the one it is written in. That rule is deliberate and predates books
   living elsewhere; the question it now raises is whether it should reach a book you INSTALLED,
   whose internals are not yours to know. Installing a book otherwise means naming its whole
   transitive dependency set at every pull site.

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

   ⚠⚠ **Found by trying to teach: PARSE errors do not explain themselves.** A missing `Done.` gives
   `Line 4, column 1: expected Done, got Eof ""` against the four-part type errors. A beginner meets
   that long before a type error, so the debugging lessons cannot start where beginners actually
   start. That is a gap in the language's own voice rather than a tutorial problem — see item 6.

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

5. **`Pull a book on blueprints` — a build description that is a Cufet program.** No second
    language for the build, the way `build.zig` is a Zig program rather than a Makefile.

    ★★ **Settled: running the build file PRODUCES A PLAN, it does not perform the build.** Nothing
    is compiled while the file runs; what comes out is a value, and something else walks it. Zig,
    CMake, Bazel and Gradle are all this shape — CMake's configure phase compiles nothing and emits
    a graph, which is also why an IDE can list your targets without building anything. Performing
    is what a `build.sh` does, and what every build system that survived converged away from.

    Three of this entry's own commitments already leaned that way: content-hash staleness must
    inspect a step before running it, a whole graph can be refused before touching disk, and a
    described value has no half-copy seam the way a builder object does. The name agrees — you do
    not execute a blueprint, you build from it.

    ⚠⚠ **`cufet build` IS ALREADY TAKEN**, and by the neighbouring meaning: it compiles one
    `.cufe` to a native binary — exactly Zig's `zig build-exe` versus `zig build`. **Settled:
    OVERLOAD IT BY ARITY.** No argument builds the project from `blueprint.cufe`; a file argument
    compiles that file. ⚠ `cufet build blueprint.cufe` is refused BY NAME, because under the
    overload it would silently compile the build description itself.

    ★★ **Settled 2026-09-13 — the whole shape, found by writing the file before the book.**

    - A blueprint defines `Bind series of records like (the text name, the series of text needs,
      the series of text makes, the series of text runs) to blueprint:` and **calls nothing**.
      Running it performs nothing, which is the fork above being honest.
    - **A step is a STRUCTURAL RECORD, not a named type.** `BookLoading.MakePrivate` renames any
      object a book layer declares to `<name> in <book>`, so a book cannot hand out a nominal type
      without native machinery — `matrix` and `chase` are native C# types for exactly that reason.
      `regex.cufe` already crosses the boundary as records. Measured: the whole shape type-checks
      and runs today with no new machinery at all.
    - **`the runs` is argv, program first.** It began as separate command and arguments fields;
      `arguments` is a RESERVED WORD, and folding them into one series beat renaming — it matches
      what argv is and removes a field. name / needs / makes / runs.
    - ★★ **Dependencies are INFERRED from `needs`/`makes`, never written as edges.** Staleness is
      by content hash, so inputs must be declared regardless; declaring edges too states one fact
      twice, and an edge can then claim a dependency the inputs deny with nothing to catch it.
    - ⚠ **`Pull a book on blueprints.` is KEPT although the record shape does not need it.** The
      book introduces no type and no members: it is a GATE, not a library. The file declares what
      it is, and `cufet build` can refuse a blueprint that does not pull it, rather than treating a
      magic function name as special. Precedent: `chance` and the language books are all
      `BookType(name, [])`.
    - **The walker is written in CUFET, inside the book**, reached by a driver statement the CLI
      APPENDS to the parsed program — the mechanism `WithPrelude` already uses, and the one the
      parser already uses to reach `match in regex`. So there is no marshalling layer and no new
      public API, and the compiled backend gets the build system for free, the way it gets `regex`.

    ✅ **The book is DONE, staleness included** (2026-09-13): steps with inferred dependencies,
    topologically ordered; `blueprints's checksum` on both backends; and a step skipped when its
    signature matches what `.cufet-build` recorded AND every output still exists, rebuilding on a
    changed input, a deleted output, or a changed argv. What remains in this entry is the module
    story below, which is why it is still here.

    ★ Recorded so it is not rediscovered: comparing a RECONSTRUCTED signature never matches. A
    signature ends with a separator, so splitting a cached line yields an empty final field and
    rejoining puts the separator back. Compare whole lines instead. ⚠⚠ It cost nothing visible —
    **a build that rebuilds too often looks exactly like one that works**, which is why the test
    pins the three ways of going stale and not only the skip.

    ⚠⚠ **TWO gaps were claimed here after the first real blueprint and BOTH were WITHDRAWN.** They
    are recorded because each was written in the measured voice and neither survived being checked.

    - *"A blueprint cannot say a file is a LIBRARY."* It never needs to: libraries are not built,
      they are pulled by the programs that are, and a build wanting to check one runs `cufet check`
      as an ordinary step. The refusal that prompted this (`cufet build tools/terminal.cufe` →
      *"declares things but never does anything"*) was the tool correctly catching a mistake in the
      blueprint.
    - *"`the makes` cannot be written portably, so `cufet build` needs an output-path argument."*
      A blueprint is an ORDINARY PROGRAM and computes its own path — `the environment variable
      "OS"` is enough, MEASURED. Adding CLI surface here would have been working around having
      forgotten this entry's own founding premise.

    ⚠⚠ **A root `blueprint.cufe` was written, VERIFIED, committed, and then REMOVED, 2026-09-14.**
    It built `tools/shell.cufe` and `tools/repl.cufe`, both of which need `tools/terminal.cufe` —
    and that shared need was the only thing tying them together. Measured on the real tree: a cold
    build ran both, an unchanged build was silent, TOUCHING `terminal.cufe` stayed silent (content,
    not timestamps) and EDITING it rebuilt both. **The design is proven; only the file is gone.**

    ★ It was removed on the author's call, not because anything was wrong with it. ⚠ Recorded
    because the v0.23.0 TAG says *"Cufet's own tools are built by a blueprint at the root of this
    repo"* — which was true when it was written and is not now, and a tag cannot be edited.

    ⚠ **Its one real friction, if it comes back:** the steps ran `cufet` from PATH, so it needed an
    installed `cufet` at least as new as the checkout. That failure is much better reported now —
    the compiler names the missing `cufet_` function and says the installed build may be older than
    the source, instead of announcing a bug in itself.

    ★ **The real wart is that message**, and it is a compiler-diagnostics job, not a blueprint one:
    "your installed cufet predates this source" is a diagnosable case that currently reads as an
    internal error. Nothing is assigned to it.

    ▶ **A blueprint can already ENUMERATE its own tree — the gap is `needs`.** MEASURED
    2026-09-15: a blueprint is an ordinary program and `the contents of the directory` is sorted
    and identical on both backends, so a loop over `tools/` that generates a step per `.cufe` works
    today with no language change. Verified end to end: cold build ran every file, an unchanged
    build was silent, and **dropping in a new `.cufe` built that one and only that one, with no
    edit to the blueprint.** ★ Convention-over-configuration needs no config FORMAT here — the loop
    is the convention and an `If` is the exception, in one language.

    ✅ **And `cufet pulls` closed the other half, 2026-09-15.** A directory listing cannot see that
    `shell.cufe` needs `terminal.cufe`; the loader can, and now says so. **A blueprint that names no
    file and no dependency is possible today** — MEASURED: it builds a new `.cufe` the moment it
    appears, rebuilds only the file edited, and rebuilds both consumers when their shared book
    changes.

    ⚠ Two shapes were weighed and declined, recorded so they are not re-derived. A **book member**
    would need a C implementation, since a native member is emitted into compiled programs — and
    *"what does this file pull"* means parsing Cufet, which the C runtime cannot do. An **axiom** is
    the same wall: it is a way to call C, and C still cannot parse Cufet. ★ A Cufet-side heuristic
    that greps for `Pull ` works and is the right PROOF that no language surface was needed, but it
    under-declares on any form it does not know, and an under-declared `need` is a silently stale
    build.

    ★ This is also what would let an EDITOR keep a project current without editing the blueprint —
    see item 4's note that a build file which is a program is read by tools rather than written by
    them.

    ▶ **The step's SHAPE is not settled.** The author is not sold on how a blueprint reads, and
    said so after the `step` type and the series-literal return had already taken it from 82 lines
    to 39. What remains is four fields per step, three of which are `a series of text with (…)`.

    ⚠ Two reductions were weighed and declined, both recorded so they are not re-derived:
    **inferring `makes` from `runs`** — derivable for a `cufet build` step and for nothing else, so
    it would make the build book know one program's behaviour and go silently wrong if that
    behaviour changed; and **a step-constructor in the book** — the first item of exactly the build
    vocabulary this entry declined to spend, and it cannot supply `needs` anyway, since
    `tools/terminal.cufe` is a dependency only because `shell.cufe` PULLS it.
    ★ A helper written in the blueprint itself needs no language change at all and is what
    `build.zig` does — measured at this size it costs about twelve lines to save fourteen, so it
    starts paying around five steps.

    ★ The question it raised and did not settle: a project that USES Cufet should say `cufet` in its
    blueprint, but this repo IS Cufet, and a front door bootstrapping from PATH is permanently one
    release behind its own source. Settled for now by what the maintainer actually does — they run
    `cufet` from PATH. The alternative, steps running
    `dotnet run --project src/App/Cufet.App.csproj -- build …`, never goes stale and never reads as
    a demonstration of the feature either.

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

    ⚠ **The one thing the design did not anticipate**, found by building it: a book's own helper
    could not reach the book's native members. A free function reaches a module only where it is
    written, so the helpers pull `blueprints` around their own bodies — and the checker had to learn
    that a pull site from the PRELUDE is not the PROGRAM asking for the book, or `DropUnpulledLayers`
    put the whole walker into every compiled binary. Measured before the fix: 17 leaked helper
    references, and since the walker runs a subprocess, every compiled program failed to link.

6. **A parse error should explain itself the way a type error does.** ⚠⚠ MEASURED 2026-09-14, by
   writing five broken programs to pick a first tutorial lesson. Every TYPE refusal has four parts —
   the rule, what you did, the fix, and a worked example:

   ```
   That doesn't work: you can only join text to text.
     Here on line 2, you're trying to join text to a number.

     Convert the number first: use 'converted to text'.
     For example: "score: " joined to n converted to text.
   ```

   A missing `Done.` gives the whole of this:

   ```
   Line 4, column 1: expected Done, got Eof "".
   ```

   ★★ **This is an inconsistency in the language's own voice, not a missing feature.** Cufet's
   claim is that it refuses clearly and says why; that claim holds in the checker and stops at the
   parser. ⚠ And it lands where it does the most harm: a beginner meets an unclosed block long
   before a type mismatch, so the first refusal anyone sees is the worst one the language produces.

   ⚠ It also blocks item 4 — debugging-first lessons cannot start where beginners actually start.

   ✅ **The UNCLOSED-BLOCK half is done, 2026-09-14** — `If`, `Otherwise`, a `Judge` arm, `Try`,
   both handler arms and `With … open … as` now name the construct and point at where it began,
   joining `repeat`, which already did. ★ It needed no new analysis: `ParseLoopBody` had always
   taken an opener token and six of its seven call sites never passed one. ⚠ The `repeat` message
   was also UNTESTED — the one good parse error in the codebase could have been refactored away in
   silence. `UnclosedBlockTests` pins all of them now.

   ✅ **The EXPECTED half of the mechanical message stopped speaking lexer, same day.** `Consume`
   translated the seven token types that are jargon — `expected '.'`, `expected ':'`, `expected a
   name` — and leaves keywords alone, since a keyword's enum name IS the keyword. ★ And a RESERVED
   WORD in a name's place now says so outright rather than answering *expected Identifier, got Key
   "key"*; no keyword table was needed, because the lexer only refuses a bare word an Identifier
   when the language has taken it.

   ⚠⚠ **THE RESERVED-WORD MESSAGE IS NARROWER THAN IT LOOKS, and it was caught the same day by
   collision number SEVEN.** It fires only where the parser explicitly asks for an `Identifier`. A
   reserved word used as a LOOP VARIABLE does not reach that consume: `For each entry in found`
   answers *expected In, got Entry "entry"*, because the name slot was skipped and the parser was
   already looking for `in`. So `Define key as 3.` is covered and `For each entry in …` is not —
   which is the case a beginner is likelier to write.

   ★ The shape of the fix is probably not another special case: a word-shaped RESERVED token
   appearing where a NAME was wanted is the same mistake whichever token the parser happened to ask
   for next. Recognising it by the token rather than by the expectation would cover every name
   position at once. ⚠ Not attempted — it needs to know which positions are name positions, and
   guessing wrong would mislabel an ordinary syntax error as a reserved word.

   ⚠ **What remains, and the largest piece is deliberately NOT the generic message:**

   - **The `got` half still prints enum names** — *expected As, got Equal "="*. ⚠ Two tests depend
     on that format: `DocBlockTests`' `RanOutOfInput` regex, and a `DoesNotContain("got Eof")`
     assertion in `PipelineInlineFormTests` that would become VACUOUS if the wording moved. The win
     is small anyway, since the lexeme is already quoted beside it.
   - **`Define x = 3`** → *expected As, got Equal "="*. ★ Cufet already has an educational message
     for `x = 5` in STATEMENT position (`EqualSignStatementErrorTests`); the `Define` site simply
     does not reach it. A targeted fix with a working precedent beats widening the generic one.
   - **`Otherwise` without the `Done.` that closes the arm before it** → *expected statement
     keyword*. A plausible belief — that `Otherwise` closes the `If` — answered by nothing.
   - **An unclosed `Bind` body** — still mechanical, because `Bind` does not route through
     `ParseLoopBody`.

   ★ The pattern across all four: the remaining wins are SPECIFIC mistakes deserving their own
   message, not a better generic one. Which is the same shape the type errors already have.

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
  books are user-authored and depended on — so this arrives with the package manager, not before.
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
