# Cufet Roadmap

Cufet 0.1.0 is the complete core: imperative language, static type system, and
first-class functions. Everything below is planned but not yet built. Items are
grouped by kind, roughly ordered within each group, but not strictly sequenced.

---

## Planned features

### Language

- **Text operations** — Cufet has `text` values but no way to join or manipulate them. `+` is deliberately *not* overloaded for concatenation, so text-joining needs its own construct (a word, e.g. `join`, or a `followed by` form). To design.

- **Text and general ordering via a `by` modifier** — ordering comparisons currently work on numbers only. Extend ordering to text (and other dimensions) with an explicit basis modifier rather than new operators or a hidden default: `is less than X by length`, `is greater than X by character code`. The basis is always stated, which avoids undefined-collation problems (case / locale / Unicode become named bases, not silent assumptions). Generalizes past text to any orderable dimension (e.g. a series `by size`). Undesigned in detail; this is the intended shape.

- **Constant declarations** — Cufet is mutable-by-default (a `Define`d value can be reassigned with `becomes`). Add an optional, explicit way to declare a value that cannot change, for the cases that deserve protection. Backward-compatible to add. Form undecided.

- **Range** — a way to produce a series of consecutive numbers without building it by hand (e.g. 1 to 100), so for each n in 1 to 100 works. Hit when writing iteration over a numeric span. Small; likely sugar that produces a normal series.

### Types and data structures

- **Maps** — typed key→value collections (e.g. a map from text to numbers). The natural structure for lookups like word→frequency. Fully typeable (keys one type, values one type).

- **Heterogeneous data is served by records and maps, not by heterogeneous series.** Series stay homogeneous. An anonymous, mixed-type ("any") collection is intentionally *not* planned — records and maps cover the real needs without breaking static, strong typing.

### Functions

- **Closures** — functions that capture variables from an enclosing scope. Currently a returned or passed function can only be a top-level function referenced by name (no captured state), so a "specialized" function (the classic `make-adder` that captures its argument) is not yet expressible. Closures are forced only by **nested function declarations** or **anonymous/inline function literals**, neither of which exists yet — so adding either is what brings closures into play. Closures are what make function-return genuinely powerful.

- **Anonymous / inline functions (lambdas)** — function literals written inline rather than declared at top level with `Bind`. Tied to closures (an inline function that references its surroundings captures them).

### Objects (partially complete)

Objects with nominal typing, possessive field access (`alice's name`), and method dispatch (`Cast greet on alice` / `Cast alice's greet`) are implemented. Remaining:

- **Field mutation** — objects are currently value-typed and immutable from outside; there is no syntax yet for changing a field after construction (by contrast, records support `the city of alice becomes "Tulsa"`). Design needed: methods that return modified copies, or direct field assignment, or something else.
- **Object equality** — no `=` / `is` comparison between two object values is currently defined.
- **Richer method dispatch** — methods with parameters, method-return-value chaining.

### Tooling

- **Style linter** — a layer separate from the parser that flags legal-but-unclear code with recommendations (warnings, not errors). First intended rule: **warn on nested bare-`it` loops** (shadowing is legal and well-defined — innermost wins — but a reader may lose track, so the linter would suggest naming iterators when nesting). The style guide also recommends capitalizing the start of a statement, which the parser does not enforce — the linter is the natural home for that kind of guidance.

---

## Known minor issues

- **Nested-function-type parameter placeholder names** — in a function-type annotation, a *nested* function-type parameter requires a placeholder name that is parsed and discarded (the name is meaningless there, since it's describing a type, not declaring a usable parameter). Minor syntax wart. Acceptable for now; revisit only if nested function types become common (they likely won't).

---

## Maintenance notes (for future development)

- **`CufetType` equality is explicit, not record-automatic.** The type records were converted from C# `record` to `class` with hand-written `Equals` / `GetHashCode` so that `FunctionType` can do deep (`SequenceEqual`) comparison of parameter-type lists — required for exact signature matching. **Any new type kind (records, maps, etc.) must implement `Equals` / `GetHashCode` correctly, including deep equality for any collection members, or exact-match silently breaks.**

- **The type checker is single-pass and will need to become multi-pass when use-before-declaration is allowed for functions.** Currently functions are top-level and hoisted in a static pass-1 scan, which already supports forward references and recursion. If/when nested functions, or any genuinely forward-referencing construct beyond the current hoist, are added, the checker converts to multi-pass. **The right multi-pass shape depends on a design choice:** because Cufet signatures have *explicitly declared* parameter and return types (not inferred-across-calls), the conversion is a clean "gather signatures, then check bodies" two-pass — *not* Hindley-Milner unification. Preserve explicit signature types to keep this cheap.

- **Recursion depth (`MaxCallDepth`) vs. native stack** — the language enforces a graceful recursion limit (default 1000) that fires a kind "missing base case?" error before the native stack overflows. The interpreter runs on a dedicated large-stack thread (16 MB) so that limit is reachable gracefully; the test harness does the same via `RunOnLargeStack`. Keep the *language* recursion limit decoupled from any host/test stack size — a test environment's stack must never silently dictate the language's recursion ceiling (this caused a false-positive recursion error early on).

- **Possible static-coverage gap in ToNumber** — The runtime ToNumber check fires for non-number arithmetic — verify whether the type checker should catch all such cases statically, or whether this is a genuine runtime backstop for SeriesPending/unresolved paths. Currently flagged with a code comment; investigate whether the static checker has a hole here. 

- **Compound-type assignment semantics (intentional split)** — Records are value-typed (copy on assignment); series are reference-typed (share on assignment). This split is deliberate and principled — records are bounded "one thing with parts," series are unbounded "collections that grow," and developers intuit copy-vs-share differently for each. Not a bug; do not "unify" them.

---

## Long-term direction (north stars)

These are not planned features with a place in the queue — they are directions
that orient nearer decisions. They are far off and may require Cufet to grow
into new categories. Their main present value is revealing which nearer roadmap
items are load-bearing for where Cufet might someday go.

### Cufet as a shell / scripting language

The vision: Cufet used not only for pure computation but to orchestrate the
system — running programs, reading and writing files, looping over directories,
branching on results. A language you script real tasks in, possibly even a
login shell.

This is a *different category* from what Cufet is today. Cufet is currently a
**pure computation language**: it has no way to reach outside itself — no file
I/O, no process execution, no filesystem or network access, no clock. That
sealed-ness is currently a strength (it keeps the type system clean, keeps
everything deterministic, is part of why there is no null). A shell is defined
by the opposite — its whole job is interacting with a messy, untyped outside
world.

Reaching this would require Cufet to gain an entire outside-world dimension.
The load-bearing prerequisites (each a real feature in its own right):

- **I/O and process execution** — reading/writing files, running external
  programs, capturing their output, exit codes, environment variables. An
  entirely new domain; Cufet has none of it today.
- **Error *handling* (recovery, not just halting)** — today an error halts the
  program. A shell must *recover* ("that command failed — do something else").
  Cufet has no recoverable-error mechanism (no try/catch, no Result type). This
  is the feature a shell most forces into existence, and the shell vision is the
  strongest argument for prioritizing it.
- **An "or nothing" type** — `read the file "x"` must express "the contents, or
  nothing (it didn't exist)" without reintroducing null. This type is *also*
  needed for recursive data structures (linked lists, trees), so it is
  doubly-motivated. Designing it well (an explicit optional/maybe, not a null
  hole) is the open question.
- **Text operations** — already on the roadmap, but a shell makes them clearly
  foundational rather than nice-to-have: a shell is almost entirely text
  manipulation (parsing command output, building paths).
- **Possibly streaming / pipes and a concurrency model** — real shells pipe
  output between commands as it is produced. Cufet has no concept of this.

**How this reorganizes the nearer roadmap:** holding this north star reveals that
**error-handling**, an **"or nothing" type**, and **text operations** are not
merely optional conveniences — they are the load-bearing pieces of a possible
future direction, which raises their priority above other deferred items.

The rough order this vision would impose: pure-language maturity first (records,
objects, a fleshed-out type system) → then the outside-world layer (I/O,
recoverable errors, "or nothing", processes) → then shell-specific features
(pipes, an interactive prompt).

### REPL (the bridge)

A read-eval-print loop: run `cufet` with no file, get a prompt, type a line, see
it evaluated, repeat — with the environment persisting between lines. This is the
*modest, near* version of "interactive Cufet," and it is **step one of the shell
vision** — the shell is, loosely, the REPL plus the whole outside world.

Unlike the shell itself, this is *close*: the full pipeline (lexer → parser →
type checker → interpreter) already exists, and a REPL is a thin loop around the
evaluator that keeps `_env` alive between inputs rather than discarding it. The
real design questions are tractable and fun: must each line be a complete
statement, how are multi-line constructs (a `Bind` block, an `if:`/`Done.`)
handled at a prompt, and should a bare expression auto-print its value (type
`1 + 1`, see `2`, without `State`).

Worth considering *sooner* than its far-future cousin, because it is a
**use-and-joy multiplier**: it makes trying things in Cufet frictionless (no
file, no `dotnet run`), which accelerates the use-driven development loop that
has been surfacing the best design insights. And thematically, an interactive
back-and-forth suits a language built to read like natural language — the rabbit
talking back, one line at a time.

---

*Cufet is pre-1.0. The language may still change. Versioning is semantic: features bump the minor version, and 1.0.0 will mark the point at which the language is considered stable.*