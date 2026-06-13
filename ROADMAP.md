# Cufet Roadmap

Cufet 0.1.0 is the complete core: imperative language, static type system, and
first-class functions. Everything below is planned but not yet built. Items are
grouped by kind, roughly ordered within each group, but not strictly sequenced.

---

## Planned features

### Language

- **Text operations** — Cufet has `text` values but no way to join or manipulate them. `+` is deliberately *not* overloaded for concatenation, so text-joining needs its own construct (a word, e.g. `join`, or a `followed by` form). To design.

- **Text and general ordering via a `by` modifier** — ordering comparisons currently work on numbers only. Extend ordering to text (and other dimensions) with an explicit basis modifier rather than new operators or a hidden default: `is less than X by length`, `is greater than X by character code`. The basis is always stated, which avoids undefined-collation problems (case / locale / Unicode become named bases, not silent assumptions). Generalizes past text to any orderable dimension (e.g. a series `by size`). Undesigned in detail; this is the intended shape.

- **Constant declarations** — Cufet is mutable-by-default (a `Define`d value can be reassigned with `becomes`). Add an optional, explicit way to declare a value that cannot change, for thea# Cufet Roadmap

Cufet is a statically-typed, natural-language programming language. It borrows
English's surface where that aids clarity and keeps formal structure visible
everywhere else. This document records what is **built**, what is **planned**,
the **design decisions** behind the language, and its **long-term direction**.

Cufet is pre-1.0. The language may still change. Versioning is semantic:
feature arcs bump the minor version; 1.0.0 will mark the point at which the
language is considered stable.

---

## What's built (the language today)

**Core**
- Values and state: `Define name as value.` (declare), `name becomes value.`
  (reassign). No null — every value is initialized.
- Types: static, strong, inferred. Base types `number` (decimal), `text`,
  `fact` (boolean), `series of T`, function types, record types, object types.
- Arithmetic: `+ - * / %` with conventional precedence, whitespace-disambiguated.
- Comparison: word forms in conditions (`is`, `is not`, `is greater than`,
  `is less than`, `is N or more`, `is N or less`); symbol forms (`= < > <= >=`)
  in expression position.
- Logic: `and`, `or`, `not` — words, conventional precedence, short-circuit.
- Conditionals: `If` / `Otherwise if` / `Otherwise`, inline (comma) and block
  (colon + `Done.`) forms.
- Loops: `While ... repeat`, `Repeat ... until`, `For each ... in`,
  with `Stop.` (break) and `Skip.` (continue).
- Educational, cause-located error messages with line numbers throughout;
  "did you mean?" suggestions for undefined names; bespoke nudges (e.g. `=`
  used in a condition).

**Collections**
- Series: homogeneous, ordered. Ordinal access (`the first/second/last of`),
  parametric (`item N of`), length (`the number of`), full mutation
  (`Add`/`Remove`, prepend/insert/by-position/by-value), element assignment.
  Literals use `with (...)`.

**Functions**
- `Bind <return-type|void> to <name>, given (<params>): ... return value.`
  Top-level, hoisted (use-before-declaration and recursion work).
- Fully first-class: stored in variables, passed as parameters, returned, and
  held in series. Function types written `the <return> function <name>,
  given (<params>)`.
- Recursion with a graceful depth limit (kind "missing base case?" error).

**Records** *(complete)*
- Anonymous, structural data. `a record with (<positional>, the <type> <name>, ...)`
  — positional and named fields, positionals first, mixing allowed.
- Access: positional (`the first of r`), named (`the city of r`), chained/nested.
- Mutation: `the city of r becomes "Tulsa"` — value semantics (deep-copy on
  assignment; mutation is in-place on the named binding).
- Record shapes in function parameter and return annotations.
- Series of records: populated infers the shape; empty uses
  `a series of records like (<shape>)`.

**Objects** *(complete)*
- Nominal named types: `Define object person with (<fields>).` Fields use the
  record field syntax (positional + named). Two objects are the same type iff
  same name (nominal), in contrast to records (structural).
- Instances: `a new person {the name "Alice", the age 30}` — `{}` literals.
- Field access (named + positional), reusing record machinery.
- Methods: nested in the definition. Self-reference via `one` (`one's name`).
- Method calls: `Cast greet on alice` (no args) and `Cast steer on (racer, 90)`
  (object as first argument, params follow) — same syntax as function calls.
  Possessive form `Cast racer's steer on (90)` for explicit/disambiguated calls.
- Mutation: value-on-assignment, mutable-in-place (the "struct model") —
  identical to records. Mutating methods (`one's age becomes ...`) mutate the
  actual instance the method was called on.
- Embedding (composition): `Define object customer with (...) and as a person.`
  — promotes the embedded object's fields and methods (transitively).
  Construction is flat (own + all promoted fields in one `{...}`). Name
  collisions between own and promoted members are a compile-time error
  (disambiguate via the type-name handle, e.g. `the name of the person of customer`).
  **Promotion is not subtyping** — a `customer` is not accepted where a `person`
  is expected.
- Interfaces (polymorphism): `Define <name> as an interface for { <method
  signatures> }` (single-method form may drop the braces). Methods are full
  function-type signatures. Conformance is explicit (`Define object person
  with (...) and greeter.`) and statically enforced. An interface name is
  usable as a parameter type, accepting any conforming object.
  **Conformance is not subtyping** — it is a flat compile-time check; no
  variance is introduced.

---

## Planned features

### Language

- **Text operations** — `text` exists but cannot yet be joined or manipulated.
  `+` is deliberately *not* overloaded for concatenation, so text-joining needs
  its own construct (a word, e.g. `joined with`, or a `followed by` form).
  To design. *Wanted: this gap has been reached for repeatedly in practice —
  high priority.*

- **Text and general ordering via a `by` modifier** — ordering currently works
  on numbers only. Extend it with an explicit basis modifier rather than new
  operators or a hidden default: `is less than X by length`,
  `is greater than X by character code`. The basis is always stated, which
  avoids undefined-collation problems (case / locale / Unicode become named
  bases, not silent assumptions). Generalizes to any orderable dimension
  (e.g. a series `by size`). Intended shape; undesigned in detail.

- **Constant declarations** — Cufet is mutable-by-default. Add an optional,
  explicit way to declare a value that cannot change. Backward-compatible. Form
  undecided.

- **Range** — produce a series of consecutive numbers without building it by
  hand (e.g. `1 to 100`), so `for each n in 1 to 100` works. Small; likely
  sugar producing a normal series.

### Types and data structures

- **Maps** — typed key→value collections (e.g. a map from text to numbers). The
  natural structure for lookups like word→frequency. Fully typeable.

- **Heterogeneous data is served by records, objects, and maps — not by
  heterogeneous series.** Series stay homogeneous. An anonymous, mixed-type
  ("any") collection is intentionally *not* planned.

### Functions

- **Closures** — functions that capture variables from an enclosing scope.
  Currently a passed/returned function can only be a top-level function by name
  (no captured state), so a "specialized" function (the classic `make-adder`)
  is not yet expressible. Closures are forced only by **nested function
  declarations** or **anonymous/inline function literals**, neither of which
  exists yet — adding either brings closures into play. Closures are what make
  function-return genuinely powerful.

- **Anonymous / inline functions (lambdas)** — function literals written inline
  rather than declared at top level. Tied to closures.

### Objects (extensions to the complete core)

- **Object equality** — comparison (`=` / `is`) between two object values.
  *Verify current status before designing — may be partially handled.* If
  added, decide identity-equality vs. field-equality given nominal typing.
- **Reference-semantics opt-in** — objects are value-typed; an explicit way to
  ask for shared/reference semantics (Rust-style) is deferred. Separate design.
- **Methods defined outside the object body** — currently methods are nested in
  the definition only. Defining them externally (associated by naming the type)
  is deferred.

### Tooling

- **Style linter** — a layer separate from the parser that flags legal-but-
  unclear code with recommendations (warnings, not errors). First intended rule:
  **warn on nested bare-`it` loops** (shadowing is legal and well-defined —
  innermost wins — but a reader may lose track). Also the natural home for the
  "capitalize the start of a statement" style guidance the parser doesn't enforce,
  and for recommending multiline formatting of large record/object shapes.

- **REPL** — see north stars below; near-term and high-value.

---

## Design decisions (the reasoning behind the language)

These record *why* the language is shaped as it is, so the rationale isn't lost.

- **Arithmetic uses symbols; comparison and logic use words.** Symbols win for
  math (that's how literate people write it). Comparison/logic read better as
  words for the audience and aesthetic. One canonical form per operator — no
  synonyms (the rigor is in the single fixed keyword, not in symbols).

- **`=` in expressions, word-comparisons in conditions (positional split).**
  Comparison-as-a-free-floating-value is in the math domain (symbols);
  comparison-inside-a-conditional reads as a sentence (words). One form per
  context — *not* two interchangeable ways to say one thing. This is settled
  by design; facts being first-class storable values does not destabilize it.

- **No null.** Every value is initialized; absence is expressed structurally,
  never by a null value.

- **Records are structural; objects are nominal.** Two records are the same type
  iff they have the same shape; two objects are the same type iff they have the
  same name. Records are *data* (interchangeable by shape); objects are *things*
  (identity by name). The language reference must explain this split clearly —
  users will be surprised the first time two same-shaped records unify or two
  same-shaped objects don't.

- **Compound-type assignment semantics (intentional split):** records are
  value-typed (copy), objects are value-typed (copy), series are reference-typed
  (share). Records and objects are bounded "things"; series are unbounded
  "collections," and developers intuit copy-vs-share differently. The split is
  principled, not a bug — do not "unify" it. (Objects use the "struct model":
  value-on-assignment, mutable-in-place via `becomes` and via mutating methods.)

- **Objects are flat: no classical inheritance, no subtyping, no variance.**
  Inheritance's central cost is hidden coupling and rigid hierarchy — the exact
  things Cufet's design refuses everywhere. Its central benefit (polymorphism)
  is available without it. So Cufet uses **composition + embedding** (reuse) and
  **interfaces** (polymorphism) instead. This keeps the type-checker free of
  variance — function-signature matching stays exact-match. **Embedding promotes
  members without subtyping; interface conformance is a flat check, not
  subtyping.** Neither makes one object type a subtype of another.

- **`as` for embedding, `is` for interface conformance.** `customer ... and as a
  person` reads "functions as a person" (composition — honest; customer is not a
  person, it has one). `person ... and greeter` / `... and is greetable`-style
  reads as an is-a-kind-of claim, which is honest for interface conformance.
  Avoids overloading `is`, and each word means what's actually happening.

- **Exact-match function-signature matching, expandable later.** Chosen
  deliberately; sufficient without a type hierarchy. If variance is ever needed
  (only if real subtyping is introduced), exact-match is the identity special
  case it would widen from — additive, not a rewrite.

- **Identifiers are lowercase-initial; uppercase-initial is reserved.** This is a
  load-bearing readability guarantee, not vestigial: keywords are
  case-insensitive, but the lexer still rejects uppercase-initial non-keywords so
  that every uppercase word a reader sees is provably a keyword and every
  lowercase word is a variable — roles parseable by eye, no lookup. (All object
  types and instances are therefore lowercase: `person`, `alice`. The
  proper-noun feel lives in string literals like `"Alice"`.)

- **`{}` is the object/OOP world; `()` is the data/call world.** Object instances
  and interface definitions use `{}`; records, series, function args use `()`.
  A consistent visual signal for which world a construct belongs to.

- **`one` is the self-reference inside methods** (`one's name`). Third-person,
  reads like English. Mild collision with the generic English pronoun "one" in
  prose *about* the language — write examples with care (code-font the keyword).

---

## Known minor issues

- **Nested-function-type parameter placeholder names** — in a function-type
  annotation, a nested function-type parameter requires a placeholder name that
  is parsed and discarded. Minor syntax wart. Acceptable; revisit only if nested
  function types become common (unlikely).

---

## Maintenance notes (for future development)

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

---

## Long-term direction (north stars)

These are not queued features — they are directions that orient nearer
decisions. Their main present value is revealing which nearer items are
load-bearing for where Cufet might someday go.

### REPL (the near bridge)

A read-eval-print loop: run `cufet` with no file, get a prompt, type a line, see
it evaluated, repeat — with the environment persisting between lines. The full
pipeline already exists; a REPL is a thin loop around the evaluator that keeps
the environment alive between inputs. Design questions are tractable and fun:
must each line be a complete statement, how are multi-line constructs handled at
a prompt, should a bare expression auto-print its value. Worth considering
*soon* — it is a use-and-joy multiplier that makes trying things frictionless and
accelerates the use-driven development loop, and an interactive back-and-forth
suits a language built to read like natural language.

### Cufet as a shell / scripting language (the far star)

Cufet used not only for pure computation but to orchestrate the system — running
programs, reading/writing files, looping over directories, branching on results;
possibly even a login shell. This is a *different category* from today's Cufet,
which is a **pure computation language** with no way to reach outside itself (no
I/O, no process execution, no filesystem/network, no clock). That sealed-ness is
currently a strength (clean type system, determinism, no null). A shell is
defined by the opposite.

Load-bearing prerequisites (each a real feature):
- **I/O and process execution** — files, external programs, output capture, exit
  codes, environment variables. An entirely new domain.
- **Error *handling* (recovery, not just halting)** — today an error halts; a
  shell must recover ("that failed — do something else"). No recoverable-error
  mechanism exists yet. The shell vision is the strongest argument for it.
- **An "or nothing" type** — `read the file "x"` must express "contents, or
  nothing" without reintroducing null. Doubly-motivated: also needed for
  recursive data structures (linked lists, trees). Design it as an explicit
  optional/maybe, not a null hole.
- **Text operations** — already planned; a shell makes them foundational (a shell
  is almost entirely text manipulation).
- **Possibly streaming / pipes and a concurrency model** — Cufet has no concept
  of this today.

**How this reorganizes the nearer roadmap:** this star reveals that
**error-handling**, an **"or nothing" type**, and **text operations** are not
mere conveniences — they are load-bearing for a possible future, which raises
their priority. Rough order the vision imposes: pure-language maturity first
(records and objects — done; maps, closures, text-ops next) → then the
outside-world layer (I/O, recoverable errors, "or nothing", processes) → then
shell-specific features (pipes, the interactive prompt).

---

*Named for the Mvskoke (Muskogee) word for rabbit, drawn from our traditional
story in which the rabbit steals fire and brings it to the people.* cases that deserve protection. Backward-compatible to add. Form undecided.

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