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

Cufet is pre-1.0 and may still change. Versioning is semantic: feature arcs bump the minor version, and 1.0.0 will mark the point at which the language is considered stable — a program that works on 1.0 keeps working on every 1.x (DESIGN, *What 1.0 promises*). What stands between: programs the interpreter runs and the compiler refuses — about eighteen "not yet supported by the compiler" paths, and a task reading a function value it captured — each passed by `check` and refused only by the compiler.

## Cufet in Cufet

The compiler written in Cufet is done: it builds every program in `examples/` and compiles itself to a fixed point (2026-10-26). What is left here is what waited on it.

1. **The C runtime, written in Cufet.** The compiler written in Cufet links `cufet-runtime.c` as it stands. The compiler that would build it exists; what writing the runtime in Cufet still needs is a way for Cufet to say what the runtime does in C — memory, arenas, threads — which today it reaches only through c-language axioms. Programs are held to their output, so the swap changes no test.

2. **Compile-time macros — the `cufet` tag's expander.** Hygienic, expanding to Cufet AST before the checker runs. A macro is what consumes the `cufet` tag of the BLOCKS type, and `Cite` with holes is the substitution half ([DESIGN.md](DESIGN.md#holes-in-cufet-source)). Left: syntax parameters, and generating AST from them, in both front ends and held to the same answers. Macro errors are the worst part of every language that has them, and clear errors are this language's distinguishing feature — that tax is paid deliberately, not early.

## The design mountains

All need a design session before they can be ordered against anything. They are here because they are large, not because they are waiting — the order among them means nothing yet.

1. **A REPL page.** `tools/repl.cufe` keeps state by REPLAY — it re-runs the accumulated lines and shows only what the new run added — and that gets SIMPLER in the browser: `Runtime.Run(source)` already takes a whole program and returns its output, statelessly. Replay repeats side effects, and a page invites longer sessions than a terminal; every line re-runs every earlier one. Accepted natively; write it down rather than rediscover it.

2. **The project tree — the rest of it.** The blueprint's drawing of the tree is BUILT in the C# front end (why it is shaped this way: DESIGN.md, *The tree the blueprint draws*). Left: the editor's maintenance — rewrite an entry when a folder moves or is renamed, ask "own namespace, or part of its parent?" when one is created, offer the edit that fixes a reference broken by the choice, every write confirmed. Cross-folder TYPES stay a gap for folders that remain walls — `snake` cannot name a `terminals` type.

## Ongoing, no fixed slot

**Auditing the checker's messages.** The parser's refusals are kept to a reader's words by a test that breaks programs and reads what comes back. Nothing does that for the checker's, which need programs that parse and make no sense, so they are read over now and then.

**An axiom's output bypasses the interpreter's writer.** The interpreter calls an axiom in its own process, so one that prints writes to the real stdout and not to the writer the interpreter was given. Under `cufet` both are the same stream; it matters only where the writer captures, which is the blueprint run in `App/Program.cs` and the pipeline tests (`InterpretRaw`).

## Deferred

### Language

- **Expression-level flow-narrowing.** Narrowing works on *variables* today (`If maybe-x is not void: … maybe-x`). Narrowing a value produced by an *expression* — say re-reading `the entry for "alice" in ages` inside an already-checked branch without naming it — is not supported. *Blocker:* the checker would have to track which expression was checked and invalidate on mutation, which is unsound against mutable maps unless done very carefully. "Name your lookups" covers the need meanwhile.

### Types and object

- **Reference-semantics opt-in.** Objects are value-typed, and stay so inside a map or a series. An explicit way to ask for shared semantics has no syntax. *Blocker:* its own design session; it interacts with the region model, which is what currently makes value semantics free.

- **Fallible setters.** A setter can clamp or ignore a value, and cannot refuse one out loud, so an object has no way to protect itself from a bad assignment. *Blocker:* a design session, on what `becomes` asks of the writer when a property's setter can fail.

### Tooling

- **Separate compilation.** Compiling files independently and linking the results, rather than resolving them and compiling together. *Blocker:* three things are sound only because the whole program compiles at once, and it reopens all three: dispatch proves coverage by seeing every version of a name, the open-union representation bounds its tag set whole-program, and a generic is monomorphized from every filling the program contains.

  **The trigger:** builds getting slow enough to notice. A small program takes about 2 seconds, and the compiler written in Cufet 53 (measured 2026-11-23).

### Memory and concurrency

- **Streaming subprocess pipes.** `run A | run B` is BUFFERED: each stage runs to completion and its whole output is handed to the next. MEASURED in both backends and identically — `Interpreter.Pipes.cs` says *"Buffered v1"*, and the compiled emitter threads one `cf_cur` through a loop of `cufet_run_capture`. Two consequences: `yes | head -1` never finishes, and a long pipeline holds every intermediate in memory.

  ⚠ **The buffering is what makes the two backends AGREE**, so this is not laziness to be tidied away. Streaming is observable through TERMINATION, not just order, so a streaming compiler against a buffering interpreter would be a divergence of the worst kind — one that hangs. Either both stream or neither does, and the interpreter is .NET while the compiled side has real `pipe()`/`dup2`. *Blocker:* that, and no program has wanted it — `tools/shell.cufe` documents the limit and lives with it.

  Not an axiom-campaign item: nothing reaches for C to do this. The language does it, buffered.

- **Move semantics at channel send.** A send deep-copies across the thread boundary. That is sound, and it is what keeps the two threads' arenas disentangled, but it is not free. A move — transferring ownership and invalidating the sender's binding — would avoid the copy. *Blocker:* the language has no way to express "this binding is spent."

### Books

- **A logic-gates book.** Gates as components you wire together, over a four-valued signal. `examples/circuits/` has the signal, the gates and the settling; there, a wire is a text name, and a mistyped one is just another wire. *Blocker:* what a wire is, if not its name.
