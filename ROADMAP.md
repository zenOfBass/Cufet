# Cufet Roadmap

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
- **Scope (lexical)** — every `Done.`-bounded block (`If`, `While`,
  `Repeat...until`, `For each`, function bodies) introduces a lexical scope.
  Inner declarations are local (do not leak out). Inner blocks can freely read
  and modify outer variables via `becomes`. Shadowing an outer name via `Define`
  is a static error by default; `Define a shadow x` opts in deliberately and
  asserts the outer name exists. For-each iterators and `Try` handler bindings
  (`the failure`, `the exception`) are automatically block-local.
- Constants: `Define name as value permanently.` — the binding can never be
  reassigned (static error on `becomes`). Shallow: fixes the binding only, not
  the contents — a permanent map/record/object can still mutate its
  entries/fields, since that's not rebinding the name.
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
- Range: `range 1 to 100` — sugar producing a materialized `series of number`.
  Inclusive of both ends; counts down when start > end. With `for each` it covers
  every use of a C-style counter loop, so no separate index-loop construct exists.
  Optional stepping: `range 1 to 10 counting by 2`. The step is always a positive
  magnitude — direction still comes from start-vs-end — and the endpoint is
  included only if the step lands on it exactly.
- Maps: `a map from text to number` — homogeneous typed key→value. Keys `number`
  or `text`. Values can themselves be `voidable V` — a present key can hold an
  explicitly-void value, distinct from an absent key. Lookup (`the entry for K
  in M`) always returns a **flat** `voidable V`, never `voidable voidable V`,
  even when the map's value type is already voidable. `has a key for` (slot
  present) and `has an entry for` (value present and non-void) agree for
  ordinary maps but **diverge** for voidable-valued ones. Set via `becomes`;
  `remove`; `the size of`; iterate gives `mapping`s (`the key of` / `the value
  of`). Reference-typed.

**Text**
- `joined to` (concatenation, text-to-text only, chains), `converted to text`
  (explicit number/fact → text — no hidden coercion), `the length of` (character
  count). `+` is deliberately not concatenation.
- `converted to number` — the inverse direction. Parsing can fail, so the result
  is always `voidable number` (even for an obviously-valid literal), handled with
  the same voidable machinery as everything else. No new handling syntax needed.
- `split by` (→ `series of text`, empties kept, not-found → single-element
  series), `contains` (→ `fact`), `the position of ... in ...` (→ `voidable
  number`, 1-based, first occurrence), and substring access — `the characters
  from N to M of`, `the first/last N characters of`, `... to the end of` (all
  1-based inclusive, always plain `text` via clamping: out-of-range-high
  clamps, backwards range is `""`, position ≤ 0 is an error).
- `replace <old> with <new> in <text>` (all occurrences; empty `<old>` is an
  error, empty `<new>` is deletion, not-found returns the text unchanged),
  `in uppercase` / `in lowercase` (default/invariant case rules), `trimmed`
  (strips whitespace from both ends). **This completes the everyday text
  toolkit** — join, measure, convert both ways, split, search, find, slice,
  replace, case, and trim are all built; only the fancier stuff (regex-ish
  matching, locale-aware casing, a character-sequence type) remains deferred.
- Escape sequences in string literals: `\n` `\t` `\r` `\\` `\"` `\{` `\}`.
  Unrecognized escape is a lexer error. `\{`/`\}` produce literal braces (not
  interpolation). String interpolation: `{expr}` inside a string literal embeds
  the expression's value — numbers and facts convert automatically; records,
  series, and maps are a static type error. `\{` vs. `{` is resolved entirely in
  the lexer (the only clean boundary given that escapes are processed there).

**Error handling**
- **`failure T`** — a failable value: either a plain `T` or a failure with a
  text message and optional category tag. The parallel to `voidable T` is exact:
  same inline fallback (`<expr> but on failure <default>` mirrors `but void is`),
  same propagation operator (`or pass the failure off` — propagates to the
  caller, which must itself return a failable type), same block form.
- **Failure literal:** `a failure "message" [of category "tag"]` — creates a
  failure value. Category tags are plain text; no closed enum.
- **Block form:** `Try to: <body> Done. [In case of failure: <handler> Done.]
  [In case of exception (the exception): <handler> Done.]` — at least one
  handler required. Failure and exception paths are independent: failures go to
  the failure handler only, runtime exceptions go to the exception handler only.
- **Runtime exceptions** (`In case of exception`) — catches things Cufet's type
  system can't prevent at compile time (divide-by-zero, etc.). Inside the
  handler, `the exception` is bound to a text description of what went wrong.
  Exceptions **re-raise by default**; `Suppress.` (inside the handler only)
  swallows the exception and lets execution continue after the `Try`. This
  default-re-raise rule is intentional: silent swallowing is the wrong default
  for recoverable-error design.
- **Unhandled failure is a static error** — a function that returns a failable
  type and discards the failure silently is caught by the type checker, not at
  runtime.

**Voidable values**
- `void` is a first-class, holdable empty value; `voidable T` is "a T, or void".
  A plain `T` widens to `voidable T`; `voidable T` does not collapse to `T`
  (static error unless handled). `is void` / `is not void`; variable-level
  narrowing inside checked branches; `but void is <default>` inline fallback.
  This is Cufet's answer to "or nothing" — no null, absence is explicit and
  checked.

**Functions** *(including closures and lambdas, complete)*
- `Bind <return-type|void> to <name>, given (<params>): ... return value.`
  Top-level, hoisted (use-before-declaration and recursion work).
- Fully first-class: stored in variables, passed as parameters, returned, and
  held in series. Function types written `the <return> function <name>,
  given (<params>)`.
- Recursion with a graceful depth limit (kind "missing base case?" error).
- **Closures** — a `Bind` declared inside another function or method body
  captures the enclosing variables at declaration time. Capture follows the
  same value/reference split as everywhere else: value types snapshot,
  reference types share the live instance.
- **Lambda literals** — anonymous function expressions, `a function given
  (<params>): <body> Done`, usable anywhere a function value goes (assigned,
  passed, returned, stored in a series). Body is always block-form
  (`Done`-terminated, no inline single-statement sugar). Return type is
  **inferred from the body**, never declared. Lambdas always carry a captured
  environment, same capture rule as closures above.

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
- Methods: nested in the definition, or declared externally with `unto` (below).
  Self-reference via `one` (`one's name`).
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
- Methods defined outside the object body: `Bind <ret> to <name> unto <type>:
  ...` — pure code organization, **identical in every way** to a nested
  method (sees `one` + fields, called identically, satisfies interface
  conformance identically). Hoisted/order-independent — may appear before or
  after `Define object <type>`. Attaches only to an object type defined in
  the same program (not foreign-type extension); a method-name clash between
  nested and `unto` (or between two `unto`s) on the same type is a static
  error — not overloading.

---

## Planned features

### Language

- **Standard I/O** — `State` writes to stdout; reading a line from stdin has no
  syntax yet. The REPL vision and the shell vision both need this.

- **Text and general ordering via a `by` modifier** — ordering currently works
  on numbers only. Extend it with an explicit basis modifier rather than new
  operators or a hidden default: `is less than X by length`,
  `is greater than X by character code`. The basis is always stated, which
  avoids undefined-collation problems (case / locale / Unicode become named
  bases, not silent assumptions). Generalizes to any orderable dimension
  (e.g. a series `by size`). Intended shape; undesigned in detail.

- **Text refinements (deferred, pending use cases)** — the everyday text
  toolkit is complete (join, measure, convert both ways, split, search, find,
  slice, replace, case, trim). What's left is all fancier: locale-aware
  casing (this slice's `in uppercase`/`in lowercase` are invariant only),
  title-case / capitalize-first, leading-only / trailing-only trim, and a
  character-sequence type (`text` stays opaque — no character-level indexing).

### Types and data structures

- **Recursive data structures (linked lists, trees)** — now expressible, since
  the voidable type provides the "or nothing" terminator a recursive structure
  needs (a node's `next` is `a voidable node`). Unblocked by the voidable type;
  not yet built out or documented as a pattern.

- **Heterogeneous collections (`atlas`)** — a mixed-type map/series. Deliberately
  *not* built, because it would require **tagged unions / sum types** (so the
  heterogeneity is *typed*, not an `any` hole that breaks static checking). A
  major type-system feature, far future. The name `atlas` is reserved for it.
  Until then, records, objects, and maps cover the real needs; series stay
  homogeneous.

### Functions

*Closures and lambdas (the former "next major frontier") are now complete —
see [What's built](#whats-built-the-language-today) above.*

- **Built-in functions / standard library** — Cufet has no built-in functions
  yet; every function is user-declared. Conversions, math, and (eventually) I/O
  will want them. Introduce *deliberately* as its own feature — not smuggled in
  as a side effect of one construct. (Surfaced when designing `converted to
  text`, which was kept a primitive construct rather than a built-in.)

### Organization and external code

- **`book` and module loading** — Cufet's mechanism for pulling rare or
  specialized capability into a program. A `book` is an object-like value
  (possessive/`of` member access, singleton, stateless capability-bag), but
  pulling one is a *module-loading operation* — it fetches code not already in
  the program. Namespaces were evaluated and rejected (see Design decisions
  above). The `module` interface defines the contract any loadable thing must
  satisfy; `book` conforms to it. **Downstream of both a standard library
  existing and a code-loading mechanism** — a std-lib/module-era feature, not
  near-term. The `module` interface itself (just an interface definition, no
  loader required) is buildable early as the stable boundary program code is
  written against.

### Objects and voidable (extensions to the complete core)

- **Expression-level flow-narrowing ("Slice B")** — narrowing currently works on
  *variables* (`if maybe-x is not void: use maybe-x`). Narrowing a value produced
  by an *expression* (e.g. re-accessing `the entry for "alice" in ages` inside a
  checked branch, without naming it) is deferred. It needs the checker to track
  which expression was checked and invalidate on mutation — harder, and unsound
  against mutable reference maps unless done carefully. The variable-narrowing +
  "name your lookups" path covers the need for now.
- **Reference-semantics opt-in** — objects (and maps' values) are value-typed; an
  explicit way to ask for shared/reference semantics (Rust-style) is deferred.
  Separate design.

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
  never by a null value. Absence has one principled mechanism: the voidable type.

- **`void` is a first-class value; absence is one unified concept.** Rather than
  separate vocabulary for "function returns nothing" and "lookup found nothing,"
  `void` is a single, holdable empty value used for both — the Rust `Unit` model.
  `voidable T` is "a T, or void." This unifies absence under one word, keeps the
  keyword dictionary small, and dissolved the old special "void result used as a
  value" error into ordinary type-mismatch (a `void`/`voidable` used where a
  concrete type is required is just a type error). The voidable type is the
  single load-bearing answer to "or nothing" — it unblocks text→number,
  recursive data structures, and (eventually) file I/O for the shell.

- **Narrowing is variable-level, not expression-level.** A voidable narrows to
  its plain type inside a branch that checked it — but keyed on a *variable*, not
  an arbitrary expression. The principled reason (not just simplicity): a literal
  buried in an inline lookup is a magic-value smell that should be named anyway,
  so the language narrows the *named binding* rather than contorting the checker
  to track inline expressions and their possible mutation. The clean path (name
  it) and the supported path (narrowing) coincide. Expression-level narrowing is
  deferred (and unsound against mutable maps unless done carefully).

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

- **Organization: common-as-grammar, rare-as-book; namespaces permanently
  closed.** Organization philosophy is *frequency of use*: common functionality
  (~95% — text, numbers, collections, control flow) is core grammar — no
  imports, no prefixes. Rare/specialized capability is pulled as a `book` when
  needed. **Namespaces are deliberately not built** — they would be a fourth
  organizer (alongside functions, objects, and lexical scope) adding import
  overhead and prefix noise without providing value the other three don't
  already cover. A `book` is an object-like value (possessive/`of` member
  access, singleton, stateless capability-bag) but pulling one is a
  *module-loading operation*, not object construction. The `module` interface
  is the contract program code depends on; the loader produces
  `module`-conforming values. `book` is-a `module` (same pattern as
  `vehicle`/`car`). Singleton and statelessness are loader-enforced
  conventions, not interface-level constraints — the interface stays minimal
  and general; the loader enforces book-specific behavior. The `module`
  interface can be built early as the stable seam; the real external-code
  loader comes later without touching program code.

---

## Known minor issues

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

### Cufet as a readable systems language (the real destination)

The true finish line: **Cufet as a native-compiled (or non-managed-runtime)
systems language in the Rust/Zig/Nim lineage** — a more humane surface for
real systems programming. "A better C" where `readelf`-ing a Cufet binary
shows Cufet's own `.data`/`.text`/`.bss` sections, where memory is real and
manually managed, where OS signals are caught at the signal wire, not
intercepted by a managed runtime.

The concrete north star is Nathan's Operating Systems coursework — a shell
program, process creation, signal handling, memory inspection — *done in
Cufet the way it would be done in C++*. Pressure-testing against the actual
homework assignments revealed the honest finish line:

- **Task 3 (memory layout — `readelf`/`nm` on a real binary to find
  globals in `.data`, locals on the stack):** a simulated memory arena
  inside the .NET interpreter would only ever show .NET's sections, not
  Cufet's. The "call `readelf` as a subprocess" workaround fails the spirit
  of the task — it explicitly demands real machine memory inspectable by
  real tools. Memory inspection is **post-native-backend**.
- **Task 4 (catch a real `SIGFPE` via `sigaction`):** a managed runtime
  intercepts signals before user code can see them. Catching a real OS
  signal at the `sigaction` level is **post-native-backend**.

These two tasks are the falsifying tests that establish "Cufet as OS
orchestrator" as a *waypoint*, not the destination.

**The current interpreter is the reference implementation / executable spec.**
The 793 tests define Cufet's semantics. A future native backend (native
compilation, compile-to-C/LLVM, or a from-scratch non-managed runtime)
implements those same semantics against real metal. Nothing built is wasted
— this is the path most serious languages took (Lua defined its semantics
via tree-walker; LuaJIT implements them natively; Rust bootstrapped through
an OCaml compiler).

**Shell / OS orchestration as a waypoint.** The interpreter era will be able
to run programs, read/write files, handle stdin/stdout, and orchestrate OS
tools as subprocesses. That's real and valuable — it's the OS homework's
*scripting-layer* tasks (building a shell, running `ls`, reading `$PATH`).
It is not the tasks that require seeing through the managed runtime
(real memory layout, raw OS signals). A Cufet shell that can `fork`/`exec`
subprocesses and handle `SIGINT` is achievable in the interpreter era; a
Cufet binary whose `.data` section you can `readelf` is post-native.

**What's already built toward the systems goal:**

| Foundation | Status | Why it matters for native |
|---|---|---|
| Static type system, explicit types everywhere | ✅ built | Type info available for codegen; no runtime type discovery needed |
| Lexical block scope (`Done.`-bounded) | ✅ built | Defines lifetimes; load-bearing for everything after |
| Voidable type (`voidable T`) | ✅ built | Native model for absence (no GC-assisted null) |
| `failure T`, `Try/In case of exception` | ✅ built | `failure T` = Rust's `Result<T,E>`; exceptions → `sigaction` in native |
| Closures (lexical capture) | ✅ built | Closure record / function pointer + captured env — direct native analog |
| Value semantics for records/objects | ✅ built | C/Zig struct semantics — copy on assign, native-compatible |
| Text toolkit complete | ✅ built | Needed for any real program; native backend needs a string library |
| Constants, interfaces, maps | ✅ built | Standard type-system infrastructure |

**What's needed, in rough interpreter-era order:**

1. **Standard I/O** — stdin read; stdout write already exists.
2. **File I/O and directory traversal.**
3. **Process execution** — run with args, capture output, exit codes.
4. **Environment variables.**
5. **Signal handling** — `SIGINT`/`SIGTERM` via interpreter hooks; `SIGFPE`
   and friends are native-backend concerns.

**Then the native-backend era:**

7. **Native compilation or compile-to-C** — the real mountain. Probably
   larger than everything built so far combined.
8. **Manual memory model** (`rabbit`/`stash` concept) — real heap/stack
   distinction, explicit lifetime management. Must be designed native-first;
   interpreter sugar would be the wrong order for this one.
9. **Real signal handling at the `sigaction` level.**
10. **Real memory layout** — globals in `.data`, locals on the stack,
    inspectable by `readelf`/`nm`/`gdb`.

**Known native-backend friction to record now:**

- **`number` = `decimal`**: C# `decimal` is a 128-bit fixed-point type with
  no hardware instruction set. The native backend needs a software decimal
  library (e.g. `libmpdec`). Switching to `double` would betray the
  "no floating-point surprises" north star. Accepted cost; not a design error.
- **Reference-type lifetime question (series, maps):** .NET's GC handles
  series/map lifetimes silently. The native backend needs an ownership model
  (GC, reference counting, Rust-style ownership, or arena allocation). This
  is the open question the manual-memory feature will answer.
- **`text` type for native:** immutable string with rich ops is the right
  semantics. Native implementation needs a real string type
  (`(ptr, len, capacity)` or arena-backed). Non-trivial; its own native
  feature.

---

*Named for the Mvskoke (Muskogee) word for rabbit, drawn from our traditional
story in which the rabbit steals fire and brings it to the people.*