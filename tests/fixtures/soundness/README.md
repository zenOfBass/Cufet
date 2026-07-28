# Soundness probes — the region model

These are small Cufet programs that pin down the **outward-only region-store invariant**:

> A value may not be stored where it would outlive the rabbit region it lives in.

`Pull a rabbit.` opens a memory region. In the native backend that region is an arena, popped at the
matching `Done.` — so a reference stored *outward*, into a binding that survives the pop, would
dangle. The shared type checker refuses those stores, which is why both backends reject them
identically and the compiler never reaches codegen.

They live here rather than in `examples/` because most of them are *supposed to fail*, and a
browsable examples directory should contain programs that run.

## The filename convention is the expectation

`SoundnessFixtureTests` enumerates this directory and asserts per file:

| Pattern | Expected |
|---|---|
| `escape-*.cufe` | **Rejected** by the type checker (`TypeException`) |
| `*-legal.cufe` | **Compiles**, and the native binary's output matches the interpreter |

This is deliberately directory-driven rather than a hand-maintained list: adding a probe is a
drop-in, with no test wiring to forget. A `FixtureCorpus_IsPresent` test guards the failure mode
that comes with that — if the files ever stop being copied to the test output directory, the
theories would silently become no-ops and the suite would still pass.

## The probes

**Rejected — one per escape route:**

| File | Route |
|---|---|
| `escape-becomes.cufe` | Direct reassignment to an outer binding |
| `escape-seriesadd.cufe` | Insertion into a longer-lived container |
| `escape-capture.cufe` | Closure capture, stored outward |
| `escape-function.cufe` | Laundered through a function that returns its own parameter |
| `escape-getter.cufe` | Out through a computed property |
| `escape-method.cufe` | Out through an `unto` method |

**Legal — the mirror images:**

| File | Property |
|---|---|
| `capture-legal.cufe` | Captures that don't escape. Also pins that captures are **by value**: writing through a capture updates the closure's own copy, so the enclosing binding is unchanged. |
| `method-getter-legal.cufe` | Same-depth getter/method access — nothing moves outward |
| `reflinked-legal.cufe` | Aliasing *within* a region is fine; series share on binding |

## Adding a probe

Drop in a `.cufe` file named for its expected outcome, and lead with a `/* EXPECTED: … */` comment
saying which route it exercises and why. The test suite picks it up automatically; update the
`FixtureCorpus_IsPresent` counts in `tests/Compiler.Tests/SoundnessFixtureTests.cs`.

Note that these probes cover the **front-end's** refusals. Escapes the front end permits — text and
region-bearing values reaching a longer-lived destination — are handled in the compiler by
copying the value into the destination's arena, and are covered by the `Escape_*` tests in
`PipelineTests.cs`.
