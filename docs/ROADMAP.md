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

    ⚠⚠ **Do not try to discharge `bury` early by giving a type a suspension VERB — measured and
    abandoned 2026-09-19.** Writing one down needs a way to say *"calling this suspends my
    caller"* as distinct from *"this function is a generator"*, and the second is what a body
    containing `bury` already means (the checker says so: *"'x' buries values, so it has to say
    what kind"*). Three spellings were tried and all failed the same way — each matched a
    precedent on the SURFACE and not in the slot: a new primitive word needed a second marker on
    top of it; `Define` declares TYPES, not members; `of` means CONTAINMENT, not genericity.
    ★ Nothing is privileged in the meantime — `and region` gives every region the same `bury` the
    rabbit gets — so this is a self-hosting debt, not an asymmetry.

2. **Compile-time macros — the `cufet` tag's expander.** Not a third program. It is here 
    rather than in *Deferred* because its blocker is now a numbered item above, which is the one rule that section states about itself.

    Hygienic, expanding to Cufet AST before the checker runs — *not* fexprs, which are first-class and runtime. It is one tag of the BLOCKS type rather than a feature of its own: quoted Cufet and
    embedded foreign source live under one type name, and a macro is what consumes the `cufet` tag. See [DESIGN.md](DESIGN.md#foreign-interoperability) — including why hygiene and SQL injection turn out to be the same problem, which is what makes the unification real rather than cosmetic.

    ★ **The type shipped in 0.17.0, and a deliberately small consumer with it.** `Cite` places what a
    block holds, and a block that says what it gives back is lowered to an ordinary function. What is
    NOT built is the expander this entry means: syntax parameters, and generating AST from them.

    ★★ **DECIDED 2026-09-26 — the grammar stays STRICT, and becomes OPEN.** Books will provide
    DSLs rather than the language growing them (no built-in LINQ; a book on queries). The errors come
    from strictness — the parser always knows which shape it expected — not from the grammar being
    closed. So a book DECLARES phrase shapes: fixed words and typed holes, e.g.
    `from each <name> in <series> where <fact>` (what Agda calls mixfix), and each shape is live only
    inside a pull of that book. It reuses three mechanisms the language already has: contextual words
    (shape words cost no names), words reserved only inside a pull (as `chase` is), and refusal on
    collision (two pulled books declaring clashing shapes are refused, never ranked). Bracketed DSLs,
    as `regex` does now, remain the nearer route; parse-time code that a book RUNS is declined, since
    a parser running arbitrary code cannot say what it expected. The near-term piece is templates for
    the `cufet` tag, after Nim's: hygienic substitution first, full macros later if ever.
    ⚠ **Open, to settle on paper first:** typed holes want types checked BEFORE expansion (as Nim's
    typed macros do), and this entry says the expander runs before the checker.

    ⚠ **Its blocker is item 1 above.** An expander generates Cufet AST, so building one in C# now means building it again in Cufet later. Macro errors are the worst part of every language that has them, and clear errors are this language's distinguishing feature — that tax is still paid deliberately, not early.

    ★ Fexprs stay out, but the recorded reason was the weaker one. Wand's result (no two expressions
    ever equivalent, taking out `check` and monomorphization) is true; the **decisive** reason is that a compiled Cufet binary is standalone C, so running a Cufet block at run time needs a Cufet
    interpreter *written in C* — a third implementation, or a divergence. Note also that an explicit
    `eval` is not a fexpr and would cost neither `check` nor monomorphization; the C-interpreter bill is what rules it out.

## The design mountains

All need a design session before they can be ordered against anything. They are here because
they are large, not because they are waiting — the order among them means nothing yet.

1. **Teaching the language: making the tutorial playable.** `docs/TUTORIAL.md` is written —
   thirteen lessons, every program and message checked by the suite; why it is shaped the way it
   is lives in DESIGN.md, *Teaching the language*. What is left is the SHELL: a tutorial whose
   examples run, in the page, against the same front end that compiles them, is a playground with
   prose around it — so nothing new has to be built to execute a lesson.

   **One decision, still open — what the site IS.** Lessons loaded into the playground, generated
   pages, or something else. ✅ Where it LIVES is answered: Pages publishes an uploaded artifact,
   which is how the playground ships. ⚠ Lessons 11 and 12 span several files, so a shell has to
   place a small project, not one program.

   ▶ **DESIGNED 2026-09-14, not built — the story frame.** Deliberately left out of the first draft.
   - **The Velveteen Rabbit meets Peter Rabbit.** Grace and Hopper are not real yet, and sneak into
     the garden to find out how. The ending is already the language's: lesson 13 shows a rabbit is
     one line a person writes, so they become real because the learner made them.
   - **Dialogue, not narration.** Grace is careful, Hopper is not; the learner overhears, and
     identifies with the one making the mistake. *"Good little rabbits always remember to …"* is
     Grace's catchphrase; Hopper pushing back keeps it from preaching.
   - **Four stages**, mapping onto the language's taxonomy: *it won't run* (a refusal) → *it runs
     and is wrong* (a failure) → *fill in the hole* → *blank page*. The lessons so far are stage one.
   - **Shape:** a branching tree was DECLINED (2^N paths, and it fights the prerequisite order); a
     **navigable map with gates** survives — a locked door is a puzzle you cannot yet solve. ⚠ That is
     a small GAME, none of it Cufet work, and the spine must be decided before rooms are written.

   ★ **A REPL page, the cheapest thing here.** `tools/repl.cufe` keeps state by REPLAY — it re-runs
   the accumulated lines and shows only what the new run added — and that gets SIMPLER in the
   browser: `Runtime.Run(source)` already takes a whole program and returns its output, statelessly.
   ⚠ Replay repeats side effects, and a page invites longer sessions than a terminal; every line
   re-runs every earlier one. Accepted natively; write it down rather than rediscover it.

   ★ **A follow-on lesson on concurrency** — tasks and channels, after lesson 9 — is the one piece of
   content the curriculum deliberately left for later.

   ⚠ **Authoring constraints, for any lesson added or changed:** a broken program is broken in exactly
   ONE legible way, or the refusal points somewhere other than the mistake; the check compares OUTPUT,
   never just "it ran"; and everything stays pinned by the doc fences — a lesson whose code stops
   working is worse than no lesson.

## Ongoing, no fixed slot

A formal soundness proof or a fresh-eyes red-team · a periodic error-message audit for internal
vocabulary

**The compiler re-derives what the checker already decided — so rules exist twice.** Narrowing is
the clearest case: the checker decides where a voidable is known present (`CheckBlock`'s guard
narrowing, the If/Otherwise arms), and `CodeGenerator` works it out AGAIN from the same syntax
(`BlockAlwaysExits`, `ElseNarrow`, `NotVoidNarrow`) to know when to read `.val`. MEASURED
2026-09-26: three fixes that week — narrowing after `stop`/`skip`/`Exit`, the `Otherwise` of
`If x is void`, and a `Judge` over a voidable — each needed the same change in both places, and
each first fix CREATED a divergence (runs interpreted, refused compiled) until the second copy
caught up. The oracle tests contain it; they do not remove it — every new narrowing rule is two
changes, and forgetting one ships a program that works on one backend only.
▶ **Direction:** the checker WRITES its conclusions onto the tree and the compiler READS them,
instead of re-deriving — the pattern already used by `CastExpression.ResolvedFunctionName`,
`IsTypeCheck.StaticTargetType` and `SortExpression.KeyFunction`. For narrowing, that means
recording on each use of a variable the type it is known to have there. ⚠ Large, and it touches
the compiler's type tables everywhere; worth doing before the self-hosted compiler copies the
two-copy shape.

**An axiom's output bypasses the interpreter's writer.** The interpreter loads an axiom with
`NativeLibrary.Load` and calls it IN-PROCESS, so a `fputs(text, stdout)` inside one writes to the
process's real stdout rather than to the `TextWriter` the interpreter was given. Under `cufet`
both land in the same stream and nothing is wrong; it matters only where the interpreter is handed
a CAPTURING writer, which today is `App/Program.cs`'s blueprint run — a blueprint that printed
through an axiom would have those lines escape the capture.

✅ The oracle harness no longer depends on that: it runs the interpreted half as a SUBPROCESS,
exactly like the compiled half, so both are captured by the OS. Fixed 2026-09-22. ⚠ `PipelineTestBase.InterpretRaw`
still captures in-process, so the ~1000 pipeline tests remain blind to an axiom's output — which
matters only for the few that would print through one, and those drive `cufet` themselves
(`Axiom_WritingANewline_SendsTheSameBytesOnBothBackends`). Making every pipeline test pay for a
process would cost far more than it buys.

⚠ **What this hole had actually cost, measured rather than assumed: nothing yet.** An earlier
version of this entry claimed `tools/shell.cufe` and `tools/repl.cufe` had been printing
uncompared bytes for a week. They had not — with stdin closed, `shell.cufe` prints NOTHING and
`repl.cufe` prints one line through `State`, which was always captured. The only program it ever
caught was `tools/snake/snake.cufe`, on the day it was written.

▶ The root is that the language cannot write without ending a line, which is the only reason
`put` is an axiom at all. A newline-free output form would remove the hole rather than route
around it. Nobody has asked for one.

**Composition modes for rabbits** — `sequential` / `parallel` / `automatic`, an optional adjective
in the type-annotation slot: `Pull an automatic rabbit as dispatcher.` The mode governs how a
rabbit's TASKS compose, never its ordinary statements, which stay sequential imperative code.

- ★ **`automatic` is the only new capability, and the gap is real:** `the delivery from <channel>`
  blocks on ONE channel, so nothing today can wait on several and take whichever arrives first.
  Occam's ALT, Go's select.

- **`parallel` is the default**, which is what a task-spawning rabbit already does. All three stay
  sayable: a default is a voiceable choice, not an inferred silence. A rabbit that spawns no tasks
  has no mode at all.
- **One discipline per rabbit.** Mixed needs nest, as composition does everywhere else here.
- ⚠ Inherit the existing concurrency caveat rather than restating it: cooperative interpreted, real
  threads compiled, no interleaving promised.

Open: whether `sequential` and `parallel` are thin labels over the join behaviour that exists, or
need machinery of their own. `automatic` is the real build — a guarded multi-input wait, now
waiting on a witness rather than on a design.

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
- ✅ **SETTLED 2026-09-23 — a circuit is a VALUE.** `examples/circuits/` is a working
  four-valued simulator, and the thing that decides it is FEEDBACK. A latch is two gates wired
  into each other's inputs, so there is no "one end" to push from; a pipeline cannot express a
  cycle. What works is holding the whole state, recomputing every gate from it AT ONCE, and
  repeating to a fixed point — cycles are then not a special case, just a circuit that takes
  more rounds, or never settles, and never settling is a real behaviour (a ring oscillator) that
  has to be reportable rather than a hang.
- ★ **The four-valued signal earned its place, and `z` earned it twice.** A tri-state bus needs a
  driver able to say "not me"; with two values, two drivers on one wire are always contention and
  the ordinary way to build a multiplexer is unwriteable.
- ⚠ **What the witness did NOT need: any of the book.** The simulator is ordinary Cufet — a closed
  union of gate kinds, a map of wires, and a loop. So the book's remaining question is what it
  would ADD over that, which is a different and smaller question than the one above.

---

## Deferred — blocked on something that is not itself on the list

### Language

- **A character cannot be classified.** There is no "is a letter", "is a digit", or code point of a
  one-character text — only patterns (`regex`) and case conversion, so `c in lowercase is not c in
  uppercase` stands in for "is a cased letter". Met writing the tutorial; the Cufet lexer (next on the
  self-hosting path) needs `char.IsLetter`/`IsDigit`/`IsWhiteSpace` equivalents and will MEASURE how
  far the workarounds reach. *Blocker:* none known; wait for that measurement before designing.

- **A series cannot be reversed.** Only `sorted in reverse` exists; `nums in reverse` fails to parse
  (GRAMMAR once listed it, and was corrected 2026-09-24). *Blocker:* none known.

- **A type cannot be given a name.** `examples/circuits/` spells
  `(and-gate or or-gate or xor-gate or nand-gate or not-gate or tri-gate)` out 20 times; the corpus
  writes long unions in full in six places. Idiomatic rather than a mistake, but six members is
  where it starts to hurt. *Blocker:* none known.

- **A series can be stated but not `converted to text`.** `State words.` prints `(pear, fig)`, but
  `words converted to text` is refused — *"Only numbers, facts, bits and a chase can be converted to
  text"* — so a series cannot go in a hole. Met writing `examples/parsing/precedence.cufe`, which built
  its own joining helper instead. *Blocker:* none known.

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

- **Streaming subprocess pipes.** `run A | run B` is BUFFERED: each stage runs to completion and
  its whole output is handed to the next. MEASURED in both backends and identically —
  `Interpreter.Pipes.cs` says *"Buffered v1"*, and the compiled emitter threads one `cf_cur`
  through a loop of `cufet_run_capture`. Two consequences: `yes | head -1` never finishes, and a
  long pipeline holds every intermediate in memory.

  ⚠ **The buffering is what makes the two backends AGREE**, so this is not laziness to be tidied
  away. Streaming is observable through TERMINATION, not just order, so a streaming compiler
  against a buffering interpreter would be a divergence of the worst kind — one that hangs. Either
  both stream or neither does, and the interpreter is .NET while the compiled side has real
  `pipe()`/`dup2`. *Blocker:* that, and no program has wanted it — `tools/shell.cufe` documents the
  limit and lives with it.

  ★ Not an axiom-campaign item: nothing reaches for C to do this. The language does it, buffered.

- **Move semantics at channel send.** A send deep-copies across the thread boundary. That is
  sound, and it is what keeps the two threads' arenas disentangled, but it is not free. A move
  — transferring ownership and invalidating the sender's binding — would avoid the copy.
  *Blocker:* the language has no way to express "this binding is spent."
