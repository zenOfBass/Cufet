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
- Comparison: word forms (`is`, `is not`, `is greater than`, `is less than`,
  `is N or more`, `is N or less`) and symbol forms (`= < > <= >=`) work in
  **both** condition and expression position. Word forms are idiomatic in
  conditions; symbol forms idiomatic in expressions — the positional restriction
  is retired. Negated word-comparisons (`is not greater than`, `is not less than`,
  `is not N or more`, `is not N or less`) are also valid in both positions.
- `true` / `false` — fact literals (alongside `yes`/`no`). Returning or storing
  `true` / `false` works without defining them as variables first.
- Educational error for `=` in a stand-alone statement (`x = y.` → "did you mean
  `x becomes y`?") and for top-level data referenced inside top-level functions
  (explains the scoping rule rather than misdirecting with "X isn't defined").
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
- Maps: `a map from text to number` — homogeneous typed key→value. Keys must be
  **value types** (`number`, `text`, or `fact`); reference types (objects, series,
  maps) are a static type error at map declaration — reference identity breaks
  across the deep-copy semantics, silently causing all lookups to miss. Values can
  themselves be `voidable V` — a present key can hold an explicitly-void value,
  distinct from an absent key. Lookup (`the entry for K
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

**Constants**
- `Define name as value permanently.` — the binding is locked against
  reassignment (static error on `becomes`). Shallow: fixes the binding, not the
  contents — a permanent map/series/object can still mutate its elements/fields
  since those go through `Add`/`Remove`/field-set, not `becomes`.

**String literals**
- Escape sequences: `\n` `\t` `\r` `\\` `\"` `\{` `\}`. Unrecognized escape is
  a lexer error. `\{`/`\}` produce literal braces (not interpolation).
- String interpolation: `{expr}` embeds an expression's value inline. Numbers
  and facts convert automatically; records, series, and maps are a static type
  error. Desugars to a `joined to`/`converted to text` chain at parse time.

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

**Input and output** *(complete — the outward era)*
- **Standard input** — `read a line from the input` (→ `voidable text`, void at
  EOF), `read all from the input` (→ `text`), `read all lines from the input`
  (→ `series of text`). `the input` is a pre-defined `readable stream of text`
  binding, not magic syntax.
- **File I/O** — `read all from the file <path>` (→ `text or failure`), `read
  all lines from the file <path>` (→ `series of text or failure`), `write
  <text> to the file <path>.` (overwrite), `append <text> to the file <path>.`
  (append). Failure categories: `"not-found"`, `"permission-denied"`,
  `"disk-error"`. Host exceptions translated to Cufet failures at the boundary
  — .NET exceptions never surface as Cufet exceptions.
- **File streams** — `With the file <path> open for reading/writing as <name>:
  ... Done.` opens a scoped stream (`readable stream of text` or `writable
  stream of text`), bound to `<name>` for the block, closed on every exit path
  (normal, failure, exception, `Stop.`) via `try/finally`. Stream direction is
  statically enforced: reading from a writable stream (or vice versa) is a
  compile-time error. All three read forms (`read a line`, `read all`, `read all
  lines`) work on any `readable stream of text`. `write <text> to <stream>`
  writes incrementally to a writable stream (no newline added).
- **Process execution** — `run <program>` and `run <program> with arguments
  (<args>)` run an external program synchronously and return a result record
  (`output` text, `errors` text, `exit-code` number) as a `result or failure`.
  Launch failure (not found, permission denied) is a Cufet failure; nonzero
  exit is a normal result. Arguments pass as individual OS-level strings — no
  shell, no injection possible. Failure categories: `"not-found"`,
  `"permission-denied"`, `"io-error"`.

**Voidable values**
- `void` is a first-class, holdable empty value; `voidable T` is "a T, or void".
  A plain `T` widens to `voidable T`; `voidable T` does not collapse to `T`
  (static error unless handled). `is void` / `is not void`; variable-level
  narrowing inside checked branches; `but void is <default>` inline fallback.
  This is Cufet's answer to "or nothing" — no null, absence is explicit and
  checked.

**Union types, narrowing, atlas, and catalogue**
- `(A or B or C)` — **closed union type**: a parenthesized, `or`-separated list
  of concrete types. A union value holds one of the listed types at runtime; only
  type-agnostic operations (assignment, equality, pass/store as the same union)
  are legal without narrowing. Type-specific ops on an un-narrowed union are a
  static error that points to `is a <type>`.
- `is a <type>` / `is not a <type>` — runtime type-test (generalizes `is void`):
  `if x is a number, ...`. `is an <type>` accepted wherever the article fits;
  both forms are identical.
- **In-branch narrowing** — after `is a <type>`, the value is narrowed to that
  type inside the branch and type-specific operations are legal. Narrowing is
  variable-level (same rule as voidable narrowing); clears on reassignment.
- **Narrowing by elimination** (closed unions) — the `Otherwise` arm after
  checking all but one case automatically narrows to the remaining case(s). In a
  `(number or text)` union, the `Otherwise` after `if x is a number` narrows `x`
  to `text`. Three-case unions leave the third case for `Otherwise`.
- **Open unions** — `a catalogue` with no type annotation / `an atlas` with no
  type annotation accepts any value; the `Otherwise` tail is un-narrowable and
  agnostic-only. Open is sound (narrowing still required), never `any`.
- **`a catalogue`** — heterogeneous series whose element type is a union:
  `a catalogue of (number or text)` (closed) or `a catalogue` (open). All series
  operations apply; `Add` enforces the declared union type.
- **`an atlas`** — heterogeneous map whose value type is a union:
  `an atlas from text to (number or text)` (closed) or `an atlas` (open).
  Retrieval yields `voidable (union)` — absent key = void; present key = union
  value. All map operations apply; value-setting enforces the declared type.
- **`voidable T` is preserved** — the generalization keeps `is void`,
  `but void is`, and all existing voidable behavior working unchanged.

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
- **Getters** (`Get <name> as <type>:` nested, or `Get <name> unto <type> as <type>:`
  outside) — computed read-only property; accessed via possessive or named form,
  indistinguishable from a stored field. Must return. `Get ... as void:` is a parse
  error.
- **Setters** (`Set <name> given (the <type> <param>):` nested, or `Set <name> unto
  <type> given (...):` outside) — intercepts `obj's <name> becomes value`. Infallible
  and transform-only (see design decisions). `one's <this-name> becomes X` inside the
  setter body is a direct write, bypassing re-dispatch.
- **Named constructors** (`Bind making a <type> to <name>[, given (<params>)]:`) —
  registered constructor function returning `<type>`; fallible form `Bind making a
  <type> or failure to <name>:`. Called via `Cast <name> on (args)`. One type may have
  multiple named constructors alongside the default `{...}` literal.
- **Destructors** (`Bind unmaking a <type> to <name>:` — no parameters, infallible,
  top-level only) — fires automatically in LIFO order when an object's scope exits
  (RAII). `one` is the object being destroyed. One destructor per type; duplicate is a
  static error. Infallible: `return a failure` in the body is a static error. See design
  decisions for the close/flush companion convention and the ownership rule.

**Operator overloading** *(complete)*
- User-defined types may declare behavior for `+`, `-`, `*`, etc. via `Bind <return-type> to operator overload, given (<params>):`. Overloaded operators may be **fallible** (`number or failure`) — the open design question whose answer was load-bearing for matrix arithmetic. Strict-fallible rule enforced: an expression whose type is a `failure T` must be inside a `Try to:` block or use `but on failure <default>`, or the type checker raises a static error. Same pattern as user-declared fallible functions.

**Books — specialized capability via `Pull`** *(complete)*
- `Pull a book on <name>.` — scope-local import; the book appears as a typed variable (`BookType`) for the duration of the enclosing scope. `Pull a book on math as the m.` binds under a custom local alias. **Plural form:** `Pull books on <X>, <Y>, and <Z>.` pulls multiple books in one statement.
- **`Pull…Done.` unification** — books, rabbits, and other acquired resources use a unified `Pull <thing>: … Done.` scoped-block syntax. `Pull <thing>.` (dot) is the scope-local form (available for the rest of the enclosing block). Both forms coexist.
- **Type-introducing books** — books can register types (not just functions/constants) into the pulling scope. `BookType.IntroducedTypes` carries the map; `CheckPullStatement` registers each via `RegisterScopedType` in a `_typeScopes` parallel scope chain. Only the pulling scope sees the type; values of that type travel freely as first-class values after that.
- **Three bundled books:**
  - **`math`** — pure functions: `absolute value`, `square root`, `floor`, `ceiling`, `round`, `log`, `power`, `sine`, `cosine`, `tangent`. Constants: `pi`, `e`. Partial functions (`square root` of negative, `log` of ≤ 0) return `voidable number`. `log` = natural log; `round` = away-from-zero.
  - **`collections`** — introduces the `matrix` type. Matrix literal: `a matrix with ((row1), (row2), …)`, rows must be rectangular and all-number (static check). Index: `the item at (row, column) of m` (1-based). Sized constructor: `a matrix with N by M` (zeroed). `the rows of` / `the columns of` (→ number). Matrix arithmetic (see below).
  - **`chance`** — effectful (internal RNG state, per-interpreter so test-isolated). `a random number from low to high` (whole numbers only; `low > high` is a runtime error), `a random item from series` (→ `voidable T`), `randomly shuffled series` (non-mutating Fisher-Yates copy), `a random guess` (50/50 fact). `Seed the chance with N.` reseeds for reproducibility. Separation from `math` is intentional: `math` is pure; `chance` is effectful; the two categories are kept distinct as a named design decision.

**Matrix arithmetic** *(complete, collections-book scope)*
- `m + n` — element-wise addition; requires identical dimensions; `matrix or failure` (failure category `"dimension-mismatch"`).
- `m - n` — element-wise subtraction; identical dimensions required; `matrix or failure`.
- `m * n` — matrix product (standard triple-loop dot product, NOT element-wise); requires `left.columns == right.rows`; yields an `m×p` result from `m×n * n×p`; `matrix or failure`.
- All three are **strictly fallible** (same rule as user-defined overloads: must be inside `Try to:` or `but on failure <default>`, else a static `TypeException`).
- `matrix / matrix` — undefined; falls through to "arithmetic requires numbers" type error (matrix inversion is explicitly deferred; will be a named `collections` function, not an operator, if ever added).
- Scalar multiply (`matrix * number`) and Hadamard product are deferred. Hadamard, if ever added, will be a named collections function, not an operator — the one-canonical-way principle: `*` means matrix product, full stop.
- Scope-locality is enforced by type: `MatrixType` is only in scope inside a `Pull a book on collections.` block, so any `matrix op matrix` expression is implicitly inside a Pull block — no explicit scope depth counter needed.

**Shell prerequisites** *(complete)*
- **Environment variables** — `the environment variable "NAME"` → `voidable text`
  (void if not set). Read-only access to the process environment.
- **Directory traversal** — `the contents of the directory path` → `series of text or
  failure` (entry names only, unsorted; failure categories: `"not-found"`,
  `"permission-denied"`). Path predicates: `the path "x" exists` / `is a directory` /
  `is a file` → `fact` (never fail).
- **Signal handling (cooperative + yield-aware)** — `an interrupt is requested`
  (→ `fact`; true once per `SIGINT`, stays true until cleared) /
  `Acknowledge the interrupt.` (clears the flag). `Yield.` is a cooperative
  scheduler yield that also checks the interrupt flag — every explicit yield point
  and every blocked `the delivery from` / `the awaited result of` is an interrupt
  point, so programs no longer need to poll manually if they yield for other
  reasons. True preemptive interruptibility (mid-tight-loop, no yield) is deferred
  to the native backend.

**Concurrency** *(complete — cooperative, interpreter era)*
- **Scheduler** — `CufetScheduler`, a custom C# `SynchronizationContext`. All
  continuations routed to a single per-thread FIFO queue on the same OS thread —
  no interpreter-internal data races by construction. Sequential programs run
  through the scheduler unchanged (`Execute(Program)` is the same public API).
- **Structured tasks** — `Have rabbit start a task [as <name>]: … Done.` Spawns a
  concurrent unit; the enclosing rabbit's `Done.` joins all spawned tasks before
  releasing the scope (join-at-Done.). Tasks are shorter-lived than their spawning
  rabbit by construction — the existing region model covers soundness, no new
  machinery needed. Sound by inheritance: the sequencing (soundness arc first) was
  not accidental.
- **Channels** — `a channel of T`; `Send <value> through <channel>.`; `the delivery
  from <channel>` (→ `voidable T`; void when closed-empty); `Close <channel>.`
  (idempotent; send-after-close is a runtime error). **Values deep-copied at send**
  — the cross-task aliasing guarantee. Blocked receive suspends until a value
  arrives or the channel closes; interrupt wakes blocked receives.
- **Task results** — named tasks may `return <value>.` inside their body; `the
  awaited result of <name>` collects the result (suspending if still running,
  immediate if already done, cached on double-await). Fallible tasks (`return a
  failure …`) yield a `T or failure` result; unhandled is a static error.
- **`Yield.`** — explicit cooperative scheduler yield; also an interrupt checkpoint.
  The scheduler drain loop checks `_interruptRequested` at each dequeue — every
  `Yield.`, blocked receive, and blocked await is a potential interrupt point,
  eliminating the need for manual `an interrupt is requested` polls in programs
  that already yield for other reasons.

**Streaming pipes**
- **Task pipes** — `producer | consumer.` pipes two or more `void`-returning
  functions as pipeline stages. Inside a stage body: `output <value>.` (contextual
  keyword — emits to the implicit output channel); `for each <name> from the input,
  repeat:` (reads from the implicit input channel). The producer runs to completion,
  filling the channel; the consumer then drains it. Stage references may be
  variables holding function values.
- **Subprocess pipe enhancement** — `run "a" | run "b"` in expression position
  (command substitution) returns a result record (`output`, `errors`, `exit-code`).
  Exit code is the rightmost non-zero stage (0 if all succeed). Launch failure is
  a catchable Cufet failure; non-zero exit is observable but not auto-fatal.

---

## Planned features

### Language

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

- **Number-base literals** — `0x`, `0o`, `0b` prefixes for hexadecimal,
  octal, and binary integer literals. Lexer/parser work; values are still
  `number` (decimal) at runtime. Standalone; no blocking dependencies.

### Types and data structures

- **Recursive data structures (linked lists, trees)** — now expressible, since
  the voidable type provides the "or nothing" terminator a recursive structure
  needs (a node's `next` is `a voidable node`). Unblocked by the voidable type;
  not yet built out or documented as a pattern.

- **Matrix op extensions** — scalar scaling (`matrix * number`, mixed-type dispatch
  deferred), Hadamard product (named `collections` function if ever added, not an
  operator). Matrix arithmetic (`+`, `-`, `*`) is complete — see What's built.

### Functions

*Closures and lambdas (the former "next major frontier") are now complete —
see [What's built](#whats-built-the-language-today) above.*

- **Built-in functions / standard library** — Cufet has no built-in functions
  yet; every function is user-declared. Conversions, math, and (eventually) I/O
  will want them. Introduce *deliberately* as its own feature — not smuggled in
  as a side effect of one construct. (Surfaced when designing `converted to
  text`, which was kept a primitive construct rather than a built-in.)

### Organization and external code

- **External book loading** — the bundled-book mechanism (`Pull a book on math/collections/chance.`) is complete (see What's built). What remains is *external* loading: resolving a book name to an external file/package and fetching code not bundled with the interpreter. This requires a standard-library delivery mechanism (package manager, bundled std-lib directory) and is a std-lib/module-era feature. The `module` interface (a named C# interface defining the contract for any loadable thing) is buildable early as the stable seam program code depends on.

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

### OOP extensions

- **Fallible setters (Option B)** — setters that can reject a value are deliberately
  deferred to a future **effect-tracking arc**. The current infallible-setter rule
  keeps `becomes` infallible everywhere; a fallible setter would require effect
  annotations on every assignment expression. Not designed, not near-term.

- **Multi-directional predicate dispatch** — dispatch on multiple argument types
  simultaneously (CLOS multimethods / Julia-style). A type-system arc larger than
  the entire OOP slice already built. **Design-first** — needs a dedicated design
  session before it enters the build sequence; not orderable until designed. Post-native-backend candidate if the design is sufficiently complex.

- **Design patterns (book)** — common patterns surfaced as a pulled book. Library
  and documentation work; no language or TypeChecker changes required.

- **Scalar matrix multiply** (`matrix * number`) and **Hadamard product** — deferred. Scalar multiply requires mixed-type dispatch (not yet designed). Hadamard, if added, will be a named `collections` function, not an operator (the `*` operator means matrix product, one canonical way). Neither blocks any planned near-term work.

### Memory model (interpreter era)

- **Rabbit — block-scoped arenas (Layer 3)** *(complete)* — `Pull a rabbit. <name> ... Done.`
  creates a named, block-scoped region; reference-typed values inside live in
  the rabbit and are freed at `Done.`. The full three-hole soundness arc was
  built on top of this primitive. See the memory model section in Long-term
  direction for the design; see the soundness arc narrative for the static
  enforcement that makes it safe.

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

- **Cufet's identity: teaching systems language — both, deliberately.** Cufet is
  not purely educational (like Scratch) and not purely industrial (like Zig). It is
  *both simultaneously*, and doing both is the central design challenge. Decisions
  must serve learners (readable surface, warm errors, forgiving defaults) *and*
  systems programmers (static types, real memory, no hidden costs, native-backend
  trajectory). When these pull against each other, name the tension and resolve it
  explicitly — don't drift toward either pole without notice. This is the lens for
  every frequency/feature call.

- **Possessive is always `'s`**, even for words ending in *s* — `series's`,
  `process's`. No English plural-possessive exception (`series'` is wrong in
  Cufet). One rule, no edge case.

- **Destructor close/flush companion convention** (guidance, prevents silent data
  loss): `unmake` is the infallible last-resort backstop. For cleanup that *can* fail
  (flushing a buffered writer, committing a transaction), the object should expose a
  fallible method (`close`/`flush`/`commit`) and the caller handles the failure
  *before* the object's scope ends. Relying on `unmake` alone to flush risks silent
  data loss — the destructor swallows all outcomes. (Same pattern as Rust `Drop`+
  `.close()` / Java `Closeable.close()`+`finalize`.)

- **Destructor ownership rule:** `unmake` closes what the object *opened*, not what
  it *borrowed*. A resource injected from outside is owned by the caller — closing it
  in the destructor is a double-close bug.

- **Setters are infallible and transform-only (Option A — settled).** A setter may
  clamp, convert, normalize, or derive — but it cannot reject. Validation-that-rejects
  belongs to the caller, before the assignment. This keeps `becomes` infallible
  everywhere it appears. Fallible setters (Option B) are deferred to a future
  effect-tracking arc.

- **`sum` is not a series aggregate and will not become one.** Addition is already
  expressed with `+`; a `sum` function would duplicate it and violate the
  one-canonical-way rule. Collections aggregates may exist in future arcs, but `sum`
  is permanently excluded. Revisiting requires a new rationale — "it's convenient"
  is not one.

- **`chance` is separate from `math` — effectful vs. pure.** `math` is a pure
  function book (same inputs, same outputs, no side effects). `chance` has internal
  RNG state — it is effectful by design. Keeping the two separate is a named
  structural choice: as Cufet gains more books, the effectful/pure distinction will
  matter for reasoning about code, testing, and eventually (in the native era) for
  safe concurrency. Per-interpreter RNG (`Random _rng` on the `Interpreter` instance,
  not static) gives free test isolation: each `new Interpreter()` gets its own entropy
  seed.

- **`Pull … Done.` unification — one surface for scoped resources.** Books, rabbits,
  and other acquired resources all use a unified `Pull <thing>: … Done.` block syntax.
  The `pull` verb signals "resource whose lifetime is managed here" and `Done.` closes
  it cleanly. The dot form (`Pull a book on X.`) keeps the short non-block form for
  scope-local imports. The two forms coexist and compose cleanly.

- **Matrix arithmetic is operator syntax, not book functions — settled.** `m + n`,
  `m - n`, `m * n` are the surface, not `collections' add of (m, n)`. The
  one-canonical-way principle: there is one way to say "add these matrices," and it
  is the `+` operator. This was a hard decision — book functions would have been
  faster to build — but building them first as a stopgap would have required
  deprecating them after operator overloading landed. The right sequencing was to
  build operator overloading first, then matrix arithmetic as its first exercise.
  `*` means matrix product (standard dot product), full stop; Hadamard product, if
  ever added, will be a named `collections` function precisely because `*` is taken.

- **Region model soundness — the adversarial arc (all three holes closed, 2026-06-26–28).**
  The outward-only invariant ("a value may escape to a longer-lived region but never
  inward to a shorter-lived one") is the whole safety story for the regions model.
  Its teeth were tested adversarially — deliberately probing whether the invariant
  held against real attacks — and three holes were found and closed.

  *How the holes were found:* the reference-linked-rabbit concept car (a rabbit
  containing objects that reference each other) was used as an adversarial probe:
  "does the downward-only invariant actually prevent unsound escapes, or can we
  launder depth through legitimate-looking code?" It found hole #1 (function-call
  depth laundering). Investigating #1 surfaced #2 and #3.

  **Hole #1 — return-depth laundering through function calls.**
  A function call (`cast f on (ref-param)`) fell through `ValueDepthOf` to depth 0,
  making the return value appear shallower than it was. A function that "returned its
  parameter" would appear to return a depth-0 fresh value, not the depth-N
  rabbit-allocated value it actually returned. *Closed via return-depth inference:*
  `ReturnDepthSignature` on `FunctionType` — a list of which parameter indices (0-based)
  flow into the return. Computed by `ComputeReturnDepthSignature` at the end of
  `CheckBind`. `ValueDepthOf` reads the signature and uses `max(subset)` of the
  actual argument depths. Conservative fallback (unknown callee → max of all ref-type
  inputs) is always safe: over-strict, never under-strict.

  **Hole #3 — methods/getters residue of #1.**
  Methods and getters had `ReturnDepthSignature == null` → depth 0, the same
  laundering vector as free functions. Possessive field reads (`alice's cards`,
  `the items of obj`) also fell through `ValueDepthOf` to 0. *Closed* by extending
  the signature machinery to method/getter bodies with the receiver as a depth source
  (`ReceiverDepthIndex = -1` sentinel means "receiver's depth flows to return").
  `_possessiveDepthCache` / `_rnaDepthCache` populated from `InferPossessiveAccess`
  / `InferRecordNamedAccess`.

  **Hole #2 — capture-store laundering.**
  A nested function that captures a reference-type *parameter* of its enclosing
  function can store it into outer state. Parameters are registered at `RabbitDepth = 0`
  (the function's own perspective), regardless of what the caller passes. If the
  caller passes a rabbit-allocated (depth-N) value, the captured parameter appears at
  depth 0 inside the nested function; the depth check passes (`0 > 0` is false → no
  error). At runtime the value is depth-N → use-after-free in native. *Closed
  conservatively:* `TypeInfo.IsParameter` flag set at all parameter-registration sites
  (free-function params, receiver `one`, method params, setter params, lambda params).
  In nested-scope import (`isNested = true`), any captured `TypeInfo` where
  `IsParameter && IsReferenceType` is upgraded to `RabbitDepth = CapturedParameterDepth
  = int.MaxValue`. The existing `CheckRegionStore` then rejects any outward store
  (MaxValue > any real depth). No new check logic; no call-site changes.

  *Key insights (the load-bearing reasoning for future contributors):*
  - **The depth model is integers joined by `max`** — not Rust's arbitrary lifetime
    parameters. This is why inference was tractable (depth is a simple number; `max`
    is associative and monotone) and why no user-facing annotations were needed
    (identity functions — `f(x) = x` — stay annotation-free because inference derives
    their signature).
  - **Conservative bias is mandatory for soundness.** Over-estimate depth (→ stricter
    → never permit unsafe) rather than under-estimate (→ might permit unsafe). The only
    cost of over-strictness is rejecting contrived-safe code, which can be addressed
    with an explicit annotation. The cost of under-strictness is a soundness hole.
  - **The conservative prohibition (hole #2) is triply rare** — requires: (a) double
    nesting, (b) a reference-type parameter capture, and (c) an outward store. The
    over-rejection cost is nil; the workaround is trivial (pass the value as an
    explicit parameter to the nested function instead of capturing it).
  - **This was adversarial-find-and-fix, not formal proof.** The invariant is sound
    with respect to the three holes found. A fresh-eyes red-team or a formal proof
    remains a reasonable pre-native rung if a contributor wants to take it on.

  *Status:* all three holes closed; no known remaining pre-native soundness gaps.

- **Concurrency arc — message-passing + structured concurrency, cooperative (v0.9.0).**
  The complete concurrency core (all five slices) is built, validated, and hardened
  by five concept cars. The design decisions and coherent narrative:

  *Model decision — message-passing, not shared-state+locks.*
  Shared mutable state destroys the outward-only region invariant: cross-task
  reference aliasing is use-after-free in native, exactly the class of bugs the
  invariant is designed to prevent. Message-passing keeps regions sound by
  construction — values deep-copied at channel boundaries, no cross-task aliasing.
  This is the Hoare CSP / Dijkstra-validated model: the theory-approved choice for
  a language where region safety is load-bearing.

  *Model decision — structured concurrency.*
  A task cannot outlive its spawning scope. Tasks join before the spawning rabbit's
  `Done.`. This composes directly with the `Done.`-bounded region discipline — no
  new lifetime concept needed, and the join is guaranteed even through exceptions.

  *The key insight — "a structured task is just a scope with a name."*
  A structured task joins before its rabbit's `Done.`, making it shorter-lived than
  the spawning scope. The existing region depth model + `CheckRegionStore` handle
  soundness: task-body locals cannot escape to the enclosing scope for the same
  reason inner-scope values cannot escape in sequential programs. **Zero new
  soundness machinery was needed.** The sequencing (soundness arc first, concurrency
  arc second) was deliberate — this inheritance was the goal.

  *Model decision — cooperative scheduling (interpreter era).*
  One task runs at a time; tasks interleave only at explicit yield points. C# async/
  await with `CufetScheduler` (custom `SynchronizationContext`) routes all
  continuations to a single per-thread FIFO queue — no OS-thread parallelism, no
  interpreter-internal data races by construction. Sequential programs unchanged.
  True parallelism is deferred to the native backend.

  *Five slices:*
  1. **Scheduler** — `CufetScheduler` engine. Validated: two async units interleave
     at yield points and both complete; exception propagation correct.
  2. **Structured tasks** — `Have rabbit start a task [as <name>]: … Done.`
     Spawn, task-body scope, join-at-Done.
  3. **Channels** — `a channel of T`; `Send`/`the delivery from`/`Close`.
     Deep-copy at send = the cross-task aliasing guarantee.
  4. **Task results** — `return <value>.` + `the awaited result of <name>`.
     Concurrent functions — same keyword, same fallible/void machinery.
  5. **SIGINT-at-yield + `Yield.`** — scheduler drain loop checks interrupt at
     each dequeue; blocked receive and await also wake on interrupt. Pays down the
     longest-standing interpreter-era debt.

  *Safety guarantee validated — channel-deepcopy concept car.*
  Proved deep-copy holds under nested structures (record-of-series, map-of-series).
  The central safety claim — no cross-task aliasing — is earned, not asserted. Also
  found: series literals not accepted in expression position → wired into
  `ParseCorePrimary`.

  *Fan-out native-characteristic — work-queue concept car.*
  Validated coordination correctness (close reaches all blocked workers, exclusive
  delivery, no hang). Also found: fan-out distribution doesn't balance under the
  cooperative scheduler — one worker drains everything while others starve. This is
  an interpreter-era characteristic: the FIFO cooperative scheduler serves one
  worker until it blocks. Native's OS-thread scheduler resolves it. Logged,
  deferred; "verify at native" note.

  *The Dijkstra connection — map-key value-type constraint (concept car #5).*
  The Dijkstra concept car surfaced the root cause of its silent-wrong-answer bug:
  objects used as map keys break under deep-copy semantics (reference identity
  lost). The fix was a principled type-level constraint — map keys must be value
  types (text, number, fact). Reference-type keys produce a static type error with
  an educational message explaining the identity semantics. Option A (value-equality
  objects as keys, analogous to Python's hashable/Rust's Hash+Eq) is deferred — it
  requires a deliberate equality contract that Cufet doesn't have yet.

  *Remaining named constraint (deferred to native).*
  Task bodies may read reference-type variables from the enclosing scope. Mutating
  them is not enforced against in the cooperative era (safe: one task at a time).
  Named here as a load-bearing note for the native-era enforcer: **task bodies must
  not mutate captured reference-type state from outer scopes.**

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

- **Captured-state mutation in tasks: named constraint, deferred enforcement.**
  Task bodies may read reference-type variables from their enclosing rabbit scope.
  Mutating those variables (series, map, object, matrix) is not enforced against in
  the cooperative era — one task runs at a time, so there are no actual races. The
  native era will introduce true parallelism; the constraint is named here so the
  native-era enforcer (borrow checker or ownership analysis) does not inherit an
  unguarded pattern: **task bodies must not mutate captured reference-type state
  from outer scopes.**

- **SIGINT is yield-point-only (not preemptive).** `Yield.`, blocked channel
  receives, and blocked task-awaits are all interrupt points — programs that yield
  naturally are interruptible without polling. A tight loop with no yield points is
  still not mid-computation interruptible. True preemptive interruptibility (mid-loop,
  no yield) requires native threading and is explicitly deferred to the native backend.

- **Destructor RAII is semantic-only in the interpreter era (native-escape not
  solved).** In the interpreter, `unmake` fires when an object's binding goes out
  of scope — GC handles the actual memory. For the native backend, an object that
  *escapes outward* (returned or stored into a longer-lived rabbit) requires the
  rabbit-passing pattern: the caller allocates/passes a rabbit; the callee fills it.
  The native-allocation mechanism for escaped objects is deferred to the native
  backend and is **not** solved by the destructor slice. The design docs must not
  imply that destructor semantics for escaping objects "just work" in native.

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

The concrete north star is a shell program, process creation, signal handling, memory inspection — *done in Cufet the way it would be done in C++*. Pressure-testing against the actual homework assignments revealed the honest finish line:

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
The 1327 tests (140 lexer + 1187 interpreter) define Cufet's semantics. A future
native backend (native compilation, compile-to-C/LLVM, or a from-scratch
non-managed runtime) implements those same semantics against real metal. Nothing
built is wasted — this is the path most serious languages took (Lua defined its
semantics via tree-walker; LuaJIT implements them natively; Rust bootstrapped
through an OCaml compiler).

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
| Text toolkit complete + string interpolation | ✅ built | Needed for any real program; native backend needs a string library |
| Constants, interfaces, maps | ✅ built | Standard type-system infrastructure |
| Standard input (`read a line/all/all lines from the input`) | ✅ built | Shell needs stdin; pipes need readable streams |
| File I/O (read/write/append/scoped streams) | ✅ built | Core OS capability; `With ... open` lifecycle = RAII analog |
| Process execution (`run` with args, capture output/exit-code) | ✅ built | Shell's `fork`/`exec`/`wait` at the scripting layer |
| Union types + narrowing (`(A or B)`, `is a <type>`, elimination) | ✅ built | Discriminated unions — tagged values with type-safe dispatch; native analog is tag + union struct |
| Environment variables (`the environment variable "X"`) | ✅ built | Shell needs to read `$PATH`, `$HOME`, etc.; pre-process-launch setup |
| Directory traversal (`the contents of the directory`, `the path … exists/is a file/is a directory`) | ✅ built | Shell needs to list directories, test paths; directory walk is a core shell primitive |
| Cooperative signal handling (`an interrupt is requested` / `Acknowledge the interrupt.` / `Yield.`) | ✅ built | SIGINT interruptibility in the interpreter era; preemptive form deferred to native |
| Getters / setters (uniform property access, Dart-style) | ✅ built | Controlled field access without syntax change; relevant to native struct layout control |
| Named constructors (`Bind making a <type>`) | ✅ built | User-defined construction logic; factory pattern without a new keyword |
| Destructors / RAII (`Bind unmaking a <type>`, LIFO scope-exit) | ✅ built | First step toward native RAII; destructor semantics define the cleanup contract the native backend must implement |
| Operator overloading (user-defined `+`, `-`, `*`, etc.; fallible overloads) | ✅ built | Enables domain types to use arithmetic syntax; fallible overloads proven viable |
| Books / `Pull` mechanism (bundled: `math`, `collections`, `chance`) | ✅ built | Module-loading boundary established; type-introducing books work; external loader deferred |
| Matrix type + arithmetic (`+`, `-`, `*`; fallible; dimension-mismatch failures) | ✅ built | First type-introducing book; demonstrates the operator-overloading + fallibility pattern |
| Region-model soundness (three-hole adversarial arc — all holes closed) | ✅ built | Outward-only invariant now sound w.r.t. function-call laundering, method/getter laundering, and capture-store laundering |
| Cooperative concurrency (scheduler + tasks + channels + task results + SIGINT/Yield) | ✅ built | Message-passing + structured concurrency; sound by construction (inherits region model); cooperative scheduler = no interpreter-internal races |
| Streaming task pipes (`producer \| consumer`, `output`, `for each from the input`) | ✅ built | Pipeline composition; subprocess pipe enhancements (command substitution, exit-code, stderr-visible) |
| Map key value-type constraint (text/number/fact only; reference types → static error) | ✅ built | Prevents the silent-miss class of bugs (reference identity lost under deep-copy); Dijkstra bug root cause fixed |
| Comparison unification + trap sweep (`true`/`false`, ordinals-as-identifiers, negated word-forms, educational errors) | ✅ built | Most-slipped-on rules retired; `true`/`false` work; word and symbol comparison forms position-agnostic |

**All interpreter-era language prerequisites are now complete.** The path forward:

- **Native backend** — the mountain, probably larger than everything built so far
  combined. Bundled with native: `pull a rabbit` as a task-lifetime arena (its
  physical-arena point only matters once GC is off), true fan-out distribution
  (the work-queue finding — native's OS-thread scheduler resolves the cooperative-
  era starvation), true preemptive SIGINT (non-yielding tight loops), and
  move-semantics at channel send (ownership transfer).
- Then: **multi-directional predicate dispatch** — design-first arc; the
  type-system mountain. Not orderable until designed.
- Then: close gaps, polish, **finish line**.

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
  series/map lifetimes silently. The native backend needs an ownership model.
  **Decided: regions** — see "The memory model" section below. Series/maps
  live in an implicit scope-region (stack lifetime) or an explicit rabbit
  (arena lifetime); freed when their region ends. No GC, no borrow checker.
- **`text` type for native:** immutable string with rich ops is the right
  semantics. Native implementation needs a real string type
  (`(ptr, len, capacity)` or arena-backed). Non-trivial; its own native
  feature.

### The memory model (the foundational decision)

**Cufet manages memory through *regions*.** A region is a span of memory whose
contents all live and die together. Every value lives in some region; when a
region ends, everything in it is freed at once. There is no garbage collector
and no borrow checker — region lifetimes are determined by program structure,
and one invariant keeps the whole thing safe.

This is the model from which scope, the rabbit, and the native backend all
derive. It is named here once, formally, so everything downstream descends
from it rather than the reverse.

**The two forms of region.** A region comes in two forms — the same mechanism
at two settings:

- **Implicit regions (scope).** Every `Done.`-bounded scope *is* a region.
  Values created in a scope live in that scope's region and are freed when the
  scope exits. Zero-cost default — you never name it, never manage it; it
  happens by virtue of where you wrote the code. (This is what the
  "scope defines lifetime" lean already was — now named.)

- **Explicit regions (the rabbit).** A **rabbit** is a region made explicit:
  named, held as a value, and decoupled from lexical scope. You create a
  rabbit, allocate into it, hold it, pass it; its lifetime is determined by
  whoever holds it (which is itself scope-visible). The rabbit is not built
  *on* the model — **the rabbit *is* the model's explicit lever**, the same
  region mechanism that scope provides implicitly, now under your direct
  control.

So: **scope is the implicit, automatic region; the rabbit is the explicit,
named region.** One mechanism, two settings.

**The invariant (the whole safety story).**

> **A value may escape *outward* — to a longer-lived (enclosing) region —
> but never *inward*, to a shorter-lived one. And this is statically visible.**

That single rule is the entire safety guarantee. Concretely:
- You can **return** a value to an outer scope/region (outward — the caller
  outlives the callee). Safe.
- You can **store** a value into a longer-lived region you hold. Safe.
- You **cannot** make a longer-lived region reference a value in a
  shorter-lived one (inward) — the shorter-lived region will be freed first,
  leaving a dangling reference. **Forbidden, statically.**

Because escaping is *statically visible* (a return, a store-to-outer, a
capture-that-outlives — all readable in the code structure), the compiler can
enforce the invariant *without a borrow checker and without runtime tracking*.
The structure *shows* what escapes; the rule *forbids* the unsafe direction;
safety falls out.

**This invariant is load-bearing. Everything else is derived from or tested
against it.** When questions arise later (the transfer question, the rabbit's
operations), they are answered by "does the outward-only invariant cover
this?" — not by inventing new rules.

**What lives where.**

- **Primitives** (numbers, text, booleans, facts): **value semantics, stack
  lifetime.** Copied on assignment, live where they're used, no region
  management. Nothing to free, nothing to track.
- **Reference types** (series, maps, records, streams, objects): **live in a
  region** — either the implicit scope-region they were created in, or an
  explicit rabbit. Reference semantics; their lifetime is their region's
  lifetime.

**Cycles.**

- *Within a single region*: free by construction. They all die together when
  the region ends — no cycle-detection needed, because freeing is per-region,
  not per-value. (This is what refcounting *cannot* do cleanly — the regions
  model gets it for free.)
- *Cross-region*: impossible by the invariant. A cycle would require a
  longer-lived region to reference into a shorter-lived one — the forbidden
  inward direction. The invariant rules cross-region cycles out structurally.

**Why this model for the native backend.**

- **Implicit regions → stack allocation.** A scope's region is a stack frame;
  exiting the scope pops it.
- **Explicit rabbits → arena allocation.** A rabbit is an arena
  (bump-allocate into it; free the whole arena at once when the rabbit ends).
- **No GC pass.** Freeing is structural (region ends → region freed). Nothing
  traces, nothing pauses.
- **No borrow checker.** The outward-only invariant is checked from static
  scope structure, not a separate borrow-analysis pass.

The two hardest things a native backend does — GC and borrow checking — are
both eliminated. For a language destined for native compilation, the memory
model that makes native *easiest* is the right one. The soul-feature and
native-feasibility align.

**What this resolves.**

- **The reference-type ownership question** (long-open: who frees a series
  when the last reference goes away, without GC?) — answered: a series lives
  in a region; freed when its region ends. No per-value ownership, no GC.
- **The rabbit, formally** — it is the explicit region lever. Derived *from*
  the model, not a metaphor and not a separate construct.
- **The manual-memory promise** — kept: the programmer controls regions
  (explicitly via rabbits, implicitly via scope), memory is real and managed,
  no hidden runtime collector.

**Layer 2 — the transfer question (resolved). The regions model survived its
stress-test.** The bet from Layer 1 — "the outward-only invariant alone keeps
memory safe without a borrow checker" — holds.

The question Layer 2 had to answer: when values cross between regions (function
calls, stores into rabbits), is the outward-only invariant enough to keep things
safe by static structure alone — or does some case need dynamic lifetime-tracking
(the borrow checker creeping back)?

**The resolution:** safe by structure in all cases, via the downward-only rule
for rabbits:

> **A rabbit may be passed to callees (downward, into shorter-lived scopes)
> but may never be returned to callers (upward).**

Why this is the key: if a rabbit *could* be returned, its final lifetime would
be unknown at creation, and enforcing "hold only values ≥ the rabbit's lifetime"
would require tracking the final holder's scope — which *is* lifetime parameters
/ the borrow checker. But because a rabbit **cannot** be returned, **its
birth-scope IS its lifetime, known at creation** — so lifetime comparison becomes
purely structural (compare against the birth-scope, which is lexically known).
The hard sub-case (rabbits decoupled from lexical scope → unknowable lifetimes)
**collapses into the easy one** (lexically-known lifetimes).

**The enforcement mechanism:** a callee cannot store its *own locals* into a
passed-in rabbit, because the locals are shorter-lived than the rabbit's
(structurally-known) birth-scope — caught statically by "hold only values ≥ the
rabbit's birth-scope."

**The idiomatic pattern — rabbit as backing store, not data structure:** you
create the rabbit in the *owning* scope (the caller), pass it *down* to
functions that allocate *into* it, and they hand back *handles/pointers into*
the rabbit — not the rabbit itself. A tree-builder returns the root *node*; the
caller's rabbit holds all the nodes. **This is the Zig allocator pattern** —
idiomatic in real systems programming, not restrictive once internalized.

**Critical discipline — do NOT pre-solve "return a rabbit":** the temptation,
facing "you can't return a rabbit," is to immediately design lifetime parameters
for that case — and *that is exactly how the borrow checker creeps back in.* If
a genuine "build-and-return" use case ever surfaces that handle-passing genuinely
cannot cover, deal with it *then*, as a named exception with explicit syntax —
not by pre-designing full lifetime machinery now. The bet (Zig-validated) is
that handle-passing covers the real cases and that day may never come.

**The unifying framing — one invariant, two faces:** "downward-only for rabbits"
is not a new rule bolted on — it *is* the outward-only invariant applied to
regions themselves. Values may escape outward (to longer-lived regions); regions
(which *are* lifetimes) cannot — a region's lifetime is fixed at birth, so a
region can't travel upward. **Values escape outward; regions flow downward.**
Safe by structure, no borrow checker, no annotations.

**Layer 3 — the rabbit's surface (block-scoped arenas). Built.**

**Surface:** `Pull a rabbit. <name> ... Done.` creates a named, block-scoped
region. Birth-scope = this block; freed at `Done.` — the lifetime is
visually bounded, exactly where you can see it. The `pull` verb unifies with
`Pull a book` — both acquire a named resource whose lifetime is the enclosing
block. Nestable; multiple rabbits in scope simultaneously are disambiguated
by name.

**Passing a rabbit down to a callee:** a rabbit is a normal parameter. The
callee allocates into the passed rabbit; it may pass it further down; it may
never return it. The callee can only store values at least as long-lived as
the rabbit's birth-scope — storing shorter-lived locals is a static error.

**Downward-only enforced statically:**
- Returning a rabbit → static error (*"Rabbits cannot be returned — they flow
  downward only. Pass the rabbit as an argument, or return a value that lives
  in it instead."*)
- Storing a too-short-lived value into a rabbit → static error (*"this value
  is shorter-lived than the rabbit"*). Checked at the store regardless of how
  the rabbit arrived (closes the closure-laundering edge case — no special
  closure handling needed).

**Handles = normal references (no distinct type).** Safety comes from the
downward-only rule (rabbit outlives all callees), not from a special type.

**Interpreter vs. native.** In the interpreter (.NET, GC-backed), "freed at
`Done.`" is modeled semantically — values become unreachable when the block
ends; the GC handles actual reclamation. The interpreter enforces the *static
safety rules* and *observable semantics*; physical arena allocation is the
native backend's job.

**NOT in Layer 3:** `pull a rabbit` for task-lifetime (concurrency era);
physical arena allocation (native backend); returning a rabbit / lifetime
parameters (deliberately not solved — handle-passing covers the real cases).

**Layers still ahead.**

- **`book` as a module conformer:** the loading face, gated by a standard
  library existing.
- **`pull a rabbit` as a task-lifetime arena:** the concurrency model is built
  (v0.9.0), but `pull a rabbit` in the task-lifetime sense — a named arena whose
  physical lifetime is a concurrent task rather than a lexical block — is native-era.
  Its physical-arena point only matters once GC is off. The concurrency model uses
  the block-scoped rabbit for task scope; the task-lifetime arena form is a
  native-backend feature.

---

*Named for the Mvskoke (Muskogee) word for rabbit, drawn from our traditional
story in which the rabbit steals fire and brings it to the people.*