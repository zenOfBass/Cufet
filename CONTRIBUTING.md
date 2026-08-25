# Contributing to Cufet

Welcome. Cufet is a statically-typed systems language that reads like natural
language, built to be both genuinely powerful and genuinely learnable. The goal
is a language where `readelf`-ing a Cufet binary someday shows Cufet's own
`.data`/`.text`/`.bss` sections — real native-compiled systems programming with
a humane surface.

This is a personal project, open for contributions that fit the grain.

---

## What Cufet is

Cufet borrows English's surface while keeping formal structure visible everywhere.
Every keyword reads like prose; every control-flow boundary is explicit; no hidden
scoping, no ambiguous syntax. It is *both* a teaching language *and* a systems
language — not one at the expense of the other. Decisions that pull against each
other are named and resolved explicitly.

---

## How to write Cufet

**Read [docs/GRAMMAR.md](docs/GRAMMAR.md) first.** It is the single operational reference for
writing correct Cufet: reserved keywords, one-canonical-way rules, `IExpression` vs.
string distinctions, fallibility rules, sharp edges and how to navigate them.
docs/GRAMMAR.md is continuously maintained — every feature slice updates it. If you are
writing tests, examples, or new language features, docs/GRAMMAR.md is where to look
before anything else.

**Key things to know going in:**
- `a`, `an`, `the` lex as `Article` tokens — they are noise, not identifiers.
  You *cannot* use `a` or `b` as variable names. Use `m`, `n`, `v`, `g`, `t`,
  or any other unambiguous single-letter name.
- `State` (capital S) is the print keyword — it's case-insensitive but the
  convention is capital-initial for keywords.
- Arithmetic uses symbols (`+ - * / %`). Comparisons come in two forms —
  symbol (`= < > <= >=`) and word (`is greater than`, `is less than`, etc.)
  — and **both work in any position** (condition or expression). Word forms
  are idiomatic in `If`/`While` conditions; symbol forms are natural in
  expression position. Either is valid everywhere.
- `Define` declares; `becomes` reassigns. They are not synonyms.
- `Done.` closes every block (loops, functions, object definitions, `Pull … Done.`).
  Lambda bodies use `Done` without the dot — the enclosing statement's `.` closes
  the expression.

---

## Repo structure

```
src/
  Lexer/           Cufet.Lexer          — tokenizer (one file; not split)
  Interpreter/     Cufet.Interpreter    — everything else, split by concern:
    TypeChecker.Core.cs                 — entry points, InferBinary, main dispatch
    TypeChecker.Functions.cs            — function/method/constructor/destructor checking
    TypeChecker.Series.cs               — series type inference
    TypeChecker.Records.cs              — record type inference
    TypeChecker.Objects.cs              — object/interface/getter/setter checking
    TypeChecker.Text.cs                 — text operations
    TypeChecker.Maps.cs                 — map operations
    TypeChecker.Book.cs                 — books (math, collections, chance)
    TypeChecker.Failures.cs             — failure/error-handling checks
    TypeChecker.Sort.cs                 — series sorting
    TypeChecker.Rabbit.cs               — structured-task spawn/join checking
    TypeChecker.Channels.cs             — channel type checking
    TypeChecker.Tasks.cs                — task-result type checking
    TypeChecker.Pipes.cs                — streaming pipe type checking
    Interpreter.Core.cs                 — evaluator entry points, EvaluateBinary
    Interpreter.Functions.cs            — function call dispatch, closures
    Interpreter.Objects.cs              — object/method dispatch
    Interpreter.Maps.cs                 — map operations
    Interpreter.Matrix.cs               — matrix operations
    Interpreter.Book.cs                 — book runtime (math, collections, chance)
    Interpreter.Failures.cs             — failure/exception handling, file I/O, process exec
    Interpreter.Sort.cs                 — series sorting
    Interpreter.Scheduler.cs            — CufetScheduler (cooperative concurrency engine)
    Interpreter.Rabbit.cs               — structured-task spawn/join runtime
    Interpreter.Channels.cs             — channel runtime + deep-copy-at-send
    Interpreter.Tasks.cs                — task-result runtime
    Interpreter.Pipes.cs                — streaming pipe runtime
  Compiler/        Cufet.Compiler       — native backend (AST → C source → gcc):
    CodeGenerator.cs                    — the emitter; also hosts the C runtime as raw-string consts
                                          (arena, software decimal, text, files, threads, signals)
    GccInvoker.cs                       — shells out to gcc
    CompilerException.cs                — clean deferral / refusal type
  App/             Cufet.App            — thin console entry point (interpret / build / emit-c)
tests/
  Lexer.Tests/     Cufet.Lexer.Tests
  Interpreter.Tests/  Cufet.Interpreter.Tests   (InterpreterTests.cs + many feature-specific test files)
  Compiler.Tests/  Cufet.Compiler.Tests  — oracle tests: compiled output vs. interpreted output
  fixtures/soundness/                   — region-model probe programs (see that folder's README)
examples/          .cufe example programs
```

**The lexer, parser, and type checker are shared by both backends.** A front-end change
affects interpreted and compiled programs alike — which is why the checker is the right
place for a rule that must hold everywhere, and the wrong place for one backend's
limitation.

**Navigation tip:** the TypeChecker and Interpreter are split into partial class
files by feature area. Use `grep`/search to locate a construct — there is no
maintained line-number index. The file boundaries are the index.

The Parser is deliberately *not* split — its precedence chain is linear, so
splitting would scatter coupled code.

---

## How to build and test

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download), plus **gcc on `PATH`**
for the compiler tests — they emit C and shell out to it.

```
# Run all tests (the green baseline)
dotnet test Cufet.sln

# Run a Cufet program
dotnet run --project src\App\Cufet.App.csproj -- myprogram.cufe

# Compile it to a native binary instead
dotnet run --project src\App\Cufet.App.csproj -- build myprogram.cufe

# Emit the generated C without compiling (cross-toolchain builds, inspection)
dotnet run --project src\App\Cufet.App.csproj -- emit-c myprogram.cufe myprogram.c
```

The test baseline is roughly **1200 interpreter + 150 lexer + 300 compiler**, all
green. The exact total wobbles with feature churn; what matters is the floor —
the load-bearing guarantee tests must stay present and passing:

- **Compiler oracle tests** (`tests/Compiler.Tests/PipelineTests.cs`) are now the primary
  correctness bar. Each compiles a program, runs the binary, and asserts its output equals
  the interpreter's. If the two disagree, that is a bug in one of them, never a caveat.
- **Soundness fixtures** (`tests/fixtures/soundness/`) pin the region model — six programs
  that must be rejected, three that must compile and match.
- Concurrency `join`/`close`/`deep-copy-isolation`, fallibility, map-key constraint.

Some compiler tests are **Linux-only** and early-return elsewhere: anything using POSIX
features (concurrency, subprocess, signals) can't be built by mingw, and the
sanitizer runs (ASan/LSan/TSan) are Linux-only too. Run the suite under WSL or Linux
before claiming a concurrency or memory-safety change is green.

A contribution should leave all tests passing and should add tests for any new behavior.

---

## Doc-maintenance norm

**Every feature change updates the docs.** This is a standing requirement, not a
nice-to-have. When you add or change language behavior:

Each document answers exactly one question, so there is exactly one right place for
any given change. Nothing restates another document — that is what let docs/ROADMAP.md drift
until it listed shipped features as planned.

| What changed | What to update |
|---|---|
| New syntax or keyword | docs/GRAMMAR.md (§ appropriate section) |
| New built-in behavior | docs/REFERENCE.md (relevant section) |
| A book member, or anything a `Pull a book on …` admits | docs/BOOKS.md |
| Anything user-visible at all | CHANGELOG.md `[Unreleased]` |
| A new design decision, or the reasoning behind one | docs/DESIGN.md |
| A plan changed, or an item shipped / was set aside | docs/ROADMAP.md |
| A codebase invariant a future change could break | CONTRIBUTING.md, *Implementation invariants* |
| An accepted limitation | CONTRIBUTING.md, *Known limitations* |
| **A backend divergence found or closed** | **CHANGELOG.md, and the rule below** |
| Released (minor version bump) | CHANGELOG.md, README.md line 1, docs/REFERENCE.md header, **all four `.csproj` files**, `playground/package.json`, `editors/vscode/package.json` |

**Run any code sample you write into a doc.** `python tools/doc-sweep.py` extracts every fenced
block from README, GRAMMAR, REFERENCE and BOOKS and runs `cufet check` on it. It has found samples naming
variables with reserved words, a form documented but never implemented, and possessive access used
on a record — none of which reading had caught, over many readings. A sample that does not run is
worse than no sample: a reader copies it, it fails, and they conclude the language is broken.

Not every failure it reports is a bug. Fragments (a block opening without its `Done.`), GRAMMAR's
deliberate counter-examples, and non-Cufet blocks are expected; the output groups failures by error
shape so the real ones stand out. `--strict` exits 1 for CI, once those are driven out.

**docs/ROADMAP.md records only what is *not yet done*.** When an item ships, delete it from
the roadmap — its record is the CHANGELOG entry, and its rationale is docs/DESIGN.md. Leaving
a shipped item behind is how the file becomes fiction.

**The version lives in eight files.** Four `.csproj`s, two `package.json`s, and a line each in
README and REFERENCE. The two `package.json` files were both missed on the 0.11.0 pass and found
only by grepping the whole tree for the outgoing version — so do that grep as the last step of any
release, rather than trusting this list to be complete:

```
grep -rn "0\.11\.0" --exclude-dir={node_modules,site,bin,obj,.git} --exclude=CHANGELOG.md .
```

Docs that go stale are worse than no docs. If code and docs disagree, the code
is the truth — but the docs should always catch up before merging.

---

## Design philosophy

**One canonical way.** Cufet has one way to say each thing. `+` means arithmetic
addition (or matrix product for matrices — same surface, one semantic per type pair).
`joined to` means text concatenation. `is greater than` means numeric comparison
— and so does `>`. Both symbol and word comparison forms work in any position;
the choice is stylistic. There are no synonyms for *operations*; the rigor lives
in the single fixed surface for each semantic.
When proposing a new construct, check whether an existing one already covers it.

**Natural language over jargon.** Keywords read like English words in the roles
they play: `State` (print), `Define` (declare), `becomes` (assign), `Try to:` (error
handling). New keywords should pass the "reads like a sentence" test.

**Frequency principle.** Common functionality (~95% of programs) is core grammar —
no imports, no prefixes. Rare or specialized capability is pulled as a book. The
line is frequency of use: if a feature appears in most programs, it belongs in the
grammar; if it appears in a few specialized programs, it belongs in a book.

**Warmth scales with teachable content.** Error messages earn warmth when there is a
genuine *why* and an *actionable fix*. A warm three-line error is not the default —
it is the reward for finding the right explanation. Terse + located (one line with a
line number) is the right default for everything else.

**The outward-only memory invariant.** Values may escape to a *longer-lived* region
but never *inward* to a shorter-lived one. This is the whole safety story for the
regions model. Any feature that touches region depths, function return values, or
captures must be checked against this invariant. See the soundness arc narrative in
[docs/DESIGN.md](docs/DESIGN.md).

---

## The two-backend rule

The interpreter is the **oracle**. A program's compiled output must equal its
interpreted output. This is the project's central correctness discipline, and it has
one sharp edge worth stating plainly:

> **A divergence never ships as a documented caveat.** If the same program takes a
> different branch, or prints something different, compiled versus interpreted, that
> is a bug in one of them. Either make it precise on both sides, or have the compiler
> **refuse** — a clean `CompilerException` is honest; silence is not.

The narrow exception is behavior that is *genuinely* undefined or platform-owned, where
there is no single right answer to converge on: last-ULP differences in `pow`
(`Math.Pow` *is* the platform libm), and filesystem enumeration order. Two well-defined
behaviors differing is never in that category.

> **Casing used to be on that list and should not have been.** `"héllo" in uppercase` was
> `HÉLLO` interpreted and `HéLLO` compiled, because the emitted C carried no case table —
> but Unicode says exactly what uppercasing `é` is, so the clause above excluded it by its
> own last sentence. It was an exception covering a missing implementation, and it is now
> closed: both backends read one generated table (`CaseTableData`, from
> `tools/gen-case-table.cs`), so they cannot disagree rather than being tested for
> agreeing. Worth remembering when adding to this list: "the backends differ" is not
> evidence that a behaviour is undefined.
>
> ★ And note the shape of the fix, because it generalises. Generating the C table from
> .NET would have made the two agree *at generation time* while the interpreter still
> asked ICU at run time — a newer Unicode arriving with a runtime upgrade would silently
> reopen the gap, and a test would only report it after it was already true. **Prefer a
> single source both backends read over two implementations plus a drift test.**

Two consequences for contributors:

- **Measure the interpreter before deciding anything.** When a design question has a
  "what does the reference actually do?" component, go and run it. This repeatedly
  overturned reasonable-sounding assumptions and caught unsoundness that reasoning
  alone missed.
- **Sweep the whole class.** A fix that closes only the case you noticed is not a fix.
  If a bug came from a missing entry in a list, ask whether the list is the wrong
  shape.

---

## Known debts a contributor could take on

These are open tasks that are explicitly tracked, not forgotten:

- **Approach B parser-hardening** — the proper architectural fix for
  `IsNamedAccessPattern()`'s lookahead heuristic. Approach C (the current
  principled keyword exclusion) closed the observed bug class; Approach B (explicit
  type-annotation contexts, so the parser knows from position whether it's in a
  type-annotation or an expression) eliminates the remaining theoretical fragility.
  Its stated precondition — that the parser's syntax be feature-complete — is now met.

- **Formal soundness proof / fresh-eyes red-team** — the three-hole adversarial arc
  (all closed) was adversarial-find-and-fix, not a formal proof. A contributor with
  a background in type theory could take on a formal proof of the outward-only
  invariant, or a fresh-eyes red-team to look for holes that the original arc missed.
  The native backend makes this more valuable, not less: the interpreter's GC forgives
  a region error that compiled code turns into a use-after-free.

- **Move-semantics at channel send** — sends currently deep-copy the value across the
  thread boundary, which is sound but not free. A move (with the sender's binding
  invalidated) would avoid the copy.

*(Previously listed here and since completed: true preemptive SIGINT, task-lifetime
memory scoping, fan-out distribution under OS threads, and the REFERENCE chapters for
concurrency, pipes, regions, books, matrix and operator overloading.)*

---

## Implementation invariants

Things that are true of the codebase and must stay true. Breaking one of these tends to
fail quietly rather than loudly, which is why they are written down.

- **A new `CufetType` must be named in every per-type switch, or say why not.**
  `ExhaustivenessTests` runs all 27 types through `EmitCType`, `TypeSig`,
  `FormatTypeName`, `EqCall` and `WriteCall`, and fails on a refusal that is not
  listed in its `DeliberateRefusals` table with a reason. Adding a type therefore
  tells you exactly which switches to visit. **A refusal with no reason is how a
  missing arm hides** — it looks identical to a decision until a program hits it.
  The table's other half matters too: a listed refusal that starts succeeding also
  fails the test, so the table cannot rot into fiction.

  ⚠ **It covers refusals, not silent wrong answers.** A switch whose fallback
  *returns* something plausible instead of throwing is invisible to it — which is
  exactly how `EmitStructs`' local `DepName` shipped without an `AxiomType` arm,
  returning null ("no dependency") and emitting a struct above its own typedef.
  Extending the audit to that shape is a job on its own, and the reason it is not
  done is that a local function cannot be reached by reflection.

- **`CufetType` equality is explicit, not record-automatic.** Type
  representations are hand-written classes with explicit `Equals` / `GetHashCode`
  (so `FunctionType` can do deep `SequenceEqual` on parameter lists for exact
  matching; record types compare structurally; object types compare nominally).
  **Any new type kind must implement `Equals` / `GetHashCode` correctly —
  including deep/order-correct equality for collection members — or matching
  silently breaks.** (Named record fields compare order-insensitively; positional
  fields order-sensitively — `Equals` and `GetHashCode` must agree on this.)

- **The type checker is effectively two-pass for top-level declarations**
  (signatures/definitions hoisted, then bodies checked) and stays cheap because
  signatures are *explicitly declared* — gather-then-check, not Hindley-Milner
  unification. Preserve explicit signature types to keep any future expansion
  (e.g. nested functions) tractable.

- **Recursion depth (`MaxCallDepth`, default 1000) is decoupled from the native
  stack.** The interpreter runs on a dedicated large-stack thread (16 MB), and
  the test harness does the same (`RunOnLargeStack`), so the graceful limit fires
  before any native overflow. Never let a host/test stack size dictate the
  language's recursion ceiling (this once caused a false-positive recursion error).

- **Possible static-coverage gap in `ToNumber`** — the runtime `ToNumber` check
  fires for non-number arithmetic. Verify whether the type checker should catch
  all such cases statically, or whether this is a genuine runtime backstop for
  `SeriesPending` / unresolved paths. Currently flagged with a code comment.

- **I/O failure boundary is at the .NET edge, not inside Cufet.** All .NET
  `IOException`/`UnauthorizedAccessException`/`Win32Exception` are translated to
  `FailureUnwind` at the outermost call site (file open, file read, process
  launch). Cufet code never sees a .NET exception from an I/O operation —
  only a Cufet failure. This invariant must be preserved when adding new I/O
  primitives: always wrap the outermost .NET call, not inner helpers.

- **`_inTryBlock` flag controls file-read type inference.** Inside a `Try`
  block, `InferFileReadExpr` and `InferRunExpr` return the plain success type
  (not `failure T`), because the `Try` block is the designated handler. This
  allows reading results directly without `or pass the failure off`. Outside a
  `Try` block, the failable type is returned and must be handled. Any new I/O
  primitive that produces a failable value should follow this same pattern —
  check `_inTryBlock` in `InferType`, and return the unwrapped type when true.

- **`ExecuteWithOpen` uses `try/finally` for stream lifecycle.** The scope is
  entered and the stream is bound before the `try`, so the `finally` always
  has the stream to dispose. This is the correct pattern — do not push
  `EnterScope` inside the `try`, because then an open failure would skip
  `ExitScope`. New scoped-resource primitives should follow the same structure:
  open → EnterScope → bind → try { body } finally { ExitScope; Dispose }.

- **Big files are split by concern via `partial class`; the file boundaries are
  the navigation.** `TypeChecker` is split into `Core` + per-feature files
  (`.Functions`, `.Series`, `.Records`, `.Objects`, `.Text`, `.Maps`);
  `Interpreter` similarly (`Core`, `.Functions`, `.Objects`, `.Maps`). A typical
  feature task loads `Core` + the one relevant feature file instead of the whole
  file. The parser is deliberately *not* split — its precedence chain is linear,
  so splitting would scatter coupled code. **Do not maintain a line-number index
  doc** — one was tried and abandoned: keeping line numbers accurate cost more
  than the reading it saved, and a stale line-map is worse than none. The
  self-maintaining file/section boundaries are the index.

---

## Known limitations

Behaviour that is understood, deliberate or accepted — not bugs waiting to be found.
User-facing sharp edges are also called out in [docs/GRAMMAR.md](docs/GRAMMAR.md) §8.

- **`converted to text` precedence in named-access position** — `the value of
  person converted to text` parses as `the value of (person converted to text)`,
  because the named-access path's inner expression parse absorbs the postfix
  `converted to text`. Workaround: name the access first (`Define v as the value
  of person. State v converted to text.`). Pre-existing; consistent with the
  "name your values" guidance, so acceptable. Revisit if it bites in practice.

- **Nested-function-type parameter placeholder names** — in a function-type
  annotation, a nested function-type parameter requires a placeholder name that
  is parsed and discarded. Minor syntax wart. Acceptable; revisit only if nested
  function types become common (unlikely).

- **`or pass the failure off` on file reads inside `Try`** — inside a `Try`
  block, `InferFileReadExpr` returns the plain success type (not `failure T`),
  because the `Try` block is already the handler. This means `or pass the
  failure off` on a file read inside `Try` is a static type error — correctly
  rejected, since there's nothing to propagate past the enclosing `Try`. If you
  need both "catch some failures here" and "propagate others to my caller," use
  `or pass the failure off` outside the `Try` block instead.

- **`With ... open for writing` always truncates** — opening a file for writing
  via the stream form always creates or truncates; append mode for streams is
  deferred (use `append ... to the file` for whole-value appends in the
  meantime).

- **Captured-state mutation in tasks — enforced when compiled, unenforced when
  interpreted.** The rule is: **task bodies must not mutate anything captured from an
  outer scope** — a plain number as much as a series. Compiled, this is a compile-time
  refusal (see the concurrency section above for why refusing beats copying).
  Interpreted, it is still merely a convention — the interpreter gives task bodies
  the live enclosing binding and one task runs at a time, so nothing goes wrong,
  but nothing stops you either. Write to the rule and both backends agree; break
  it and the compiler tells you.

- **SIGINT interruptibility differs by backend.** *Interpreted:* yield-point-only.
  `Yield.`, blocked channel receives, and blocked task-awaits are interrupt points,
  so a program that yields naturally is interruptible without polling — but a tight
  loop with no yield points is not. *Compiled:* genuinely preemptive, via a real
  `sigaction` handler and per-thread `sigsetjmp` landing pads. A thread blocked in
  a channel receive really wakes, and a running worker task really unwinds — running
  its destructors and closing its files on the way out.

- **Destructor timing has two known gaps, matched deliberately on both backends.**
  Unmakers fire at block scope exit, LIFO, for `Define`d object bindings — but not
  at function-frame exit and not at top level. The compiler reproduces the
  interpreter's behaviour exactly, including those gaps and including the
  double-fire on a value copy, rather than fixing one side into disagreement with
  the other. Escaping objects are handled by the region model: a value stored
  outward is deep-copied into the destination's region, so there is no dangling
  object for a destructor to chase. Closing the frame-exit gap is a language
  decision about ownership, not a backend limitation.
