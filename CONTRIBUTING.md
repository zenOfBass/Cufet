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

**Read [GRAMMAR.md](GRAMMAR.md) first.** It is the single operational reference for
writing correct Cufet: reserved keywords, one-canonical-way rules, `IExpression` vs.
string distinctions, fallibility rules, sharp edges and how to navigate them.
GRAMMAR.md is continuously maintained — every feature slice updates it. If you are
writing tests, examples, or new language features, GRAMMAR.md is where to look
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

| What changed | What to update |
|---|---|
| New syntax or keyword | GRAMMAR.md (§ appropriate section) |
| New built-in behavior | REFERENCE.md (relevant section) |
| New planned/done feature | ROADMAP.md (What's built or Planned features) |
| New design decision / rationale | ROADMAP.md Design decisions section |
| **New compiler slice / codegen change** | **CHANGELOG.md `[Unreleased]`; ROADMAP.md if it closes a planned item** |
| **A backend divergence found or closed** | **CHANGELOG.md, and the rule below** |
| Released (minor version bump) | CHANGELOG.md, README.md, REFERENCE.md header, .csproj files |

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
ROADMAP.md Design decisions.

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
(`Math.Pow` *is* the platform libm), filesystem enumeration order, ASCII-vs-locale
casing. Two well-defined behaviors differing is never in that category.

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

- **Documentation catch-up** — REFERENCE.md has no sections for concurrency, streaming
  pipes, regions (`Pull a rabbit`), the standard-library books, matrix, or operator
  overloading, all of which ship in both backends. That is the largest body of
  undocumented working surface in the repo.

*(Previously listed here and since completed: true preemptive SIGINT, task-lifetime
memory scoping, and fan-out distribution under OS threads — all shipped with the
native backend.)*
