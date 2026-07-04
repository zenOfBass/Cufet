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

The identity in one sentence: **a statically-typed systems language that reads like
natural language — designed to be both genuinely powerful and genuinely learnable.**

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
  App/             Cufet.App            — thin console entry point
tests/
  Lexer.Tests/     Cufet.Lexer.Tests
  Interpreter.Tests/  Cufet.Interpreter.Tests   (InterpreterTests.cs + many feature-specific test files)
examples/          .cufe example programs
```

**Navigation tip:** the TypeChecker and Interpreter are split into partial class
files by feature area. Use `grep`/search to locate a construct — there is no
maintained line-number index. The file boundaries are the index.

The Parser is deliberately *not* split — its precedence chain is linear, so
splitting would scatter coupled code.

---

## How to build and test

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
# Run all tests (the green baseline)
dotnet test Cufet.sln

# Run a Cufet program
dotnet run --project src\App\Cufet.App.csproj -- myprogram.cufe

# Or pipe from stdin
echo "State 1 + 1." | dotnet run --project src\App\Cufet.App.csproj
```

The current test baseline is **1187 interpreter + 140 lexer = 1327 tests**, all
green. The exact total wobbles with feature churn; what matters is the floor:
the load-bearing guarantee tests (soundness `escape-*`, concurrency
`join`/`close`/`deep-copy-isolation`, fallibility, map-key constraint) must
stay present and passing. A contribution should leave all tests passing and
should add tests for any new behavior.

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

## Known pre-native debts a contributor could take on

These are open tasks that are explicitly tracked, not forgotten:

- **Approach B parser-hardening** — the proper architectural fix for
  `IsNamedAccessPattern()`'s lookahead heuristic. Approach C (the current
  principled keyword exclusion) closed the observed bug class; Approach B (explicit
  type-annotation contexts, so the parser knows from position whether it's in a
  type-annotation or an expression) eliminates the remaining theoretical fragility.
  Best done once, deliberately, when the parser's syntax is feature-complete.

- **True preemptive SIGINT** — the cooperative concurrency core (v0.9.0) added
  `Yield.` as an explicit yield-and-interrupt-checkpoint, so programs that yield
  naturally are interruptible. The remaining gap: a tight loop with no `Yield.`
  still cannot be mid-loop interrupted. True preemption requires native threading
  infrastructure and is deferred to the native era.

- **Formal soundness proof / fresh-eyes red-team** — the three-hole adversarial arc
  (all closed) was adversarial-find-and-fix, not a formal proof. A contributor with
  a background in type theory could take on a formal proof of the outward-only
  invariant, or a fresh-eyes red-team to look for holes that the original arc missed.
  This is a reasonable pre-native rung before committing the memory model to hardware.

- **Pull-a-rabbit task-lifetime semantics** — the concurrency core (v0.9.0)
  built cooperative tasks, channels, results, and pipes. The remaining native-era
  items: pull-a-rabbit for a task's physical memory lifetime (arena/ownership
  scope), true fan-out distribution under OS-thread scheduling, and
  move-semantics at channel send (instead of deep copy). These require the
  native backend; none are interpreter-era work.
