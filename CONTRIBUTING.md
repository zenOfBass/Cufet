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
- Arithmetic uses symbols (`+ - * / %`); comparisons in conditions use words
  (`is greater than`, `is less than`, etc.). The two forms are not
  interchangeable — this is settled design, not a gap.
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
    Interpreter.Core.cs                 — evaluator entry points, EvaluateBinary
    Interpreter.Functions.cs            — function call dispatch, closures
    Interpreter.Objects.cs              — object/method dispatch
    Interpreter.Maps.cs                 — map operations
    Interpreter.Matrix.cs               — matrix operations
  App/             Cufet.App            — thin console entry point
tests/
  Lexer.Tests/     Cufet.Lexer.Tests
  Interpreter.Tests/  Cufet.Interpreter.Tests   (one file: InterpreterTests.cs)
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

The current test baseline is **1003 interpreter + 140 lexer = 1143 tests**, all
green. A contribution should leave all tests passing and should add tests for any
new behavior.

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
`joined to` means text concatenation. `is greater than` means numeric comparison in
condition context. There are no synonyms; the rigor lives in the single fixed surface.
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

- **True preemptive SIGINT** — the current cooperative-poll model (code polls
  `an interrupt has been requested` explicitly) cannot mid-loop interrupt a tight
  computation. True preemptive interruptibility requires native threading
  infrastructure and is owed during the concurrency arc. Tracked explicitly.

- **Formal soundness proof / fresh-eyes red-team** — the three-hole adversarial arc
  (all closed) was adversarial-find-and-fix, not a formal proof. A contributor with
  a background in type theory could take on a formal proof of the outward-only
  invariant, or a fresh-eyes red-team to look for holes that the original arc missed.
  This is a reasonable pre-native rung before committing the memory model to hardware.

- **Concurrency model design** — the next major arc. Async/parallel tasks,
  `pull a rabbit` (task-lifetime rabbit), pipes, and preemptive SIGINT all
  come together here. The design needs to come first; the arc is too large to
  start building without a spec. A contributor with concurrent systems experience
  could take on the design session.
