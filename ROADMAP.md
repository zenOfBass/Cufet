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

**Two backends**

Cufet has a tree-walking **interpreter** and a native **compiler** that emits C and
invokes `gcc` to produce a real executable. The entire front end — lexer, parser,
type checker — is shared, so a program that type-checks does so identically for
both.

The interpreter is the **oracle**. The compiler's test suite compiles each program,
runs the binary, and asserts its output equals the interpreter's. Where the two
disagree, one of them is wrong; it is never written down as a caveat. Either the
behaviour becomes precise on both sides, or the compiler refuses with a clean
error. The narrow exception is behaviour that is genuinely undefined or
platform-owned — `pow`'s last digit, filesystem enumeration order, ASCII casing —
where there is no single right answer to converge on.

Everything described in this section works on **both** backends unless a note says
otherwise. The places where they legitimately differ are few, and each is called
out where it arises: chiefly that compiled concurrency is genuinely parallel while
the interpreter's scheduler is cooperative, so the interpreter's particular
interleaving is not a specification.

```
cufet program.cufe                  run it (interpreted)
cufet build program.cufe            compile to a native binary
cufet emit-c program.cufe out.c     emit the C without invoking gcc
```

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
  reasons. *Compiled:* interruption is genuinely preemptive — a real `sigaction`
  handler plus per-thread `sigsetjmp` landing pads, so a tight loop with a
  checkpoint, a thread blocked in a channel receive, and a running worker task can
  all be interrupted.

**Concurrency** *(complete. Cooperative when interpreted; genuinely parallel when
compiled — this is the one place the two backends deliberately differ, so the
interpreter's particular interleaving is not a specification.)*
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

### Memory model

*(The rabbit/region model is **built**, on both backends — see "What's built" above
and the memory-model section under Long-term direction. What remains planned here
is the one optimisation below.)*

- **Move semantics at channel send** — a send currently deep-copies the value
  across the thread boundary. That is sound, and it is what keeps the two threads'
  arenas disentangled, but it is not free. A move — transferring ownership and
  invalidating the sender's binding — would avoid the copy. Needs a way to express
  "this binding is spent," which the language does not have yet.

### Tooling

- **Style linter** — a layer separate from the parser that flags legal-but-
  unclear code with recommendations (warnings, not errors). First intended rule:
  **warn on nested bare-`it` loops** (shadowing is legal and well-defined —
  innermost wins — but a reader may lose track). Also the natural home for the
  "capitalize the start of a statement" style guidance the parser doesn't enforce,
  and for recommending multiline formatting of large record/object shapes.

- **REPL** — see north stars below; near-term and high-value.

---

## What's next (ordered, post-0.10.0)

The language is feature-complete against its original finish line and released. This is
the ordered backlog, grouped by what unblocks what rather than by size. Two things
determine the order:

- **Sockets, POSIX/Windows APIs, and threading primitives are not separate items.** They
  are all "call a C function," so a **C FFI** collapses them into one item and turns each
  of them into a book rather than a language feature.
- **Multi-directional predicate dispatch is not free-floating** — it is on the critical
  path to self-hosting. A lexer, parser and type checker are one enormous dispatch on node
  type; written as `is a` chains, a Cufet-in-Cufet compiler is miserable to write and worse
  to read.

**Tier 0 — cheap, and closes open edges**
1. Number-base literals (hex/octal/binary) — lexer-only, no interaction with anything,
   table stakes for systems work. The cheapest real win available.
2. Book-scoped keyword reservation — reserve a book's keywords only inside
   `Pull a book on <name>`. This gets harder with every book added; `seed` already
   collided with a user identifier.
3. `cufet` as an installed binary — today it is `dotnet run --project src/App/…`, which
   gates everything in Tier 1.
4. Working directory — there is currently no `cd`, no way to query one, and no way to set
   one on a subprocess. A hole in an otherwise complete OS-orchestration story.
5. Awaits inside tasks — the last loud refusal inside a shipped arc.

**Tier 1 — usable by someone other than the author**
6. REPL.
7. Syntax highlighter. *Not* the same item as an LSP, and far higher value per hour.
   Note the real design problem: because keywords are English words in prose positions,
   naive keyword highlighting lights up `the`, `of`, `to`, `a` on every line.
8. A diagnostics tier (warnings). Everything today is an error or nothing. This unblocks
   the dead-capture-write warning, the style linter above, and a worthwhile formatter.
9. Formatter.

**Tier 2 — leverage**
10. C FFI, including an explicit address-of. This is what makes "anything can be written
    in Cufet" literally rather than nearly true.

**Tier 3 — the design mountain**
11. Multi-directional predicate dispatch. Needs its own design session; watch the
    no-subtyping invariant. See the note above on why it is not optional.

**Tier 4 — modules (strictly in this order)**
12. Separate compilation + an external book loader. ⚠ Known collision: the bounded
    open-union representation is sound *because* the whole program compiles at once.
    Either feature forces revisiting it.
13. A package manager for books.

**Tier 5 — self-hosting (Cufet written in Cufet)**
14. The blockers are ergonomic, not capability: the data model, text handling and I/O are
    already sufficient, and emitting C is a route a Cufet-written compiler can also take.
    ★ The test oracle already exists — a self-hosted compiler can be validated by asserting
    its C output matches this compiler's, giving a third implementation held against the
    other two.

**Ongoing, no fixed slot:** dead-capture-write warning (after diagnostics) · Approach B
parser-hardening · move semantics at channel send · a formal soundness proof or fresh-eyes
red-team · a periodic error-message audit for internal vocabulary · a performance number
against C · logic gates as a book · design patterns as a book.

**Deliberately outside the tiers:** a full shell (Xonsh/csh style) is a *product built with
Cufet*, not work on Cufet — a flagship application, needing `cd`, job control, globbing,
history and completion.

**Considered and set aside:** four-valued logic / tetralemma (`fact`, `voidable` and unions
already cover the space; a fourth overlapping way to say "not exactly true" cuts against
one-canonical-way) · assembly and LLVM IR interop (the emitted C already reaches `asm` when
needed; FFI covers the motivating cases) · an LSP before a highlighter and formatter exist.

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
  matter for reasoning about code, testing, and for safe concurrency (which now
  means real threads). Per-interpreter RNG (`Random _rng` on the `Interpreter` instance,
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
  *Resolved:* the compiler lowers tasks to pthreads, so compiled programs are
  genuinely parallel. Their tests assert order-independent invariants rather than a
  particular interleaving, and run under ThreadSanitizer.

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
  worker until it blocks. *Resolved as predicted* — under real OS threads the work
  actually distributes, and the compiled fan-out test asserts it.

  *The Dijkstra connection — map-key value-type constraint (concept car #5).*
  The Dijkstra concept car surfaced the root cause of its silent-wrong-answer bug:
  objects used as map keys break under deep-copy semantics (reference identity
  lost). The fix was a principled type-level constraint — map keys must be value
  types (text, number, fact). Reference-type keys produce a static type error with
  an educational message explaining the identity semantics. Option A (value-equality
  objects as keys, analogous to Python's hashable/Rust's Hash+Eq) is deferred — it
  requires a deliberate equality contract that Cufet doesn't have yet.

  *Named constraint — now enforced.*
  This was recorded as a note for a future enforcer: **task bodies must not mutate
  captured reference-type state from outer scopes.** The compiler enforces it. A
  captured series, map, object, or text is deep-copied across the thread boundary
  the same way a channel message is, and a task that would *change* one is refused
  at compile time, with the error pointing at channels instead. Three reasons it is
  a refusal rather than a silent copy: the interpreter hands task bodies the live
  enclosing binding, so a copy would visibly disagree with it; sharing instead is
  unsound, because arenas are per-thread and a mutation that grows a shared series
  would reallocate into the task's own arena; and the pattern is a genuine data
  race that only the cooperative scheduler was hiding.

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

### Cufet as a readable systems language (the destination — now reached)

The stated finish line was **Cufet as a native-compiled systems language in the
Rust/Zig/Nim lineage** — "a better C" where `readelf`-ing a Cufet binary shows
real sections, where memory is real and manually managed, and where OS signals
are caught at the signal wire rather than intercepted by a managed runtime.

**That line has been crossed.** `cufet build program.cufe` produces a native
executable through a C intermediate. There is no managed runtime in the result —
no .NET, no GC, no interpreter loop.

The two tasks recorded here as *falsifying tests* were chosen precisely because a
managed runtime could not fake them. Their current status:

- **Task 3 (memory layout — `readelf`/`nm` on a real binary).** Passed. A compiled
  Cufet program is an ordinary ELF executable: `.text`, `.rodata`, `.data`, `.bss`,
  and — because each thread bump-allocates in its own arena — `.tdata`/`.tbss`.
  Cufet functions are real machine symbols (a `Bind` becomes a `.text` entry), the
  arena stack is a `.bss` object, and a `permanently`-bound text literal sits in
  `.rodata`. The one honest qualification: Cufet has no *global* variable form, so
  top-level bindings are `main`'s stack locals rather than `.data` entries. Locals
  on the stack, constants in `.rodata` — inspectable by real tools, as the task
  demanded.
- **Task 4 (catch a real OS signal via `sigaction`).** Mechanism reached, surface
  not yet exposed. The runtime installs a genuine `sigaction` handler for `SIGINT`
  with an async-signal-safe handler and per-thread `sigsetjmp` landing pads, so
  signals *are* caught at the wire. What is not a language feature yet is letting
  a program handle an *arbitrary* signal. `SIGFPE` specifically is a poor fit:
  Cufet's `number` is a software decimal, so division by zero is a checked
  condition raised as a catchable Cufet exception rather than a hardware trap.

**The interpreter is now the reference implementation in a much stronger sense
than "executable spec": it is the compiler's oracle.** Every compiler test
compiles a program, runs the binary, and asserts its output equals the
interpreter's. Where the two disagreed, one of them was wrong — and that
discipline is what most of the correctness work has consisted of.

**The differential-testing result is worth stating plainly.** Holding two
independent backends against each other surfaced roughly seventeen latent bugs.
Most of them were not code-generation mistakes; they were **defects in the
language design** that a single implementation had silently absorbed — `is a`
being kind-erased for containers, record fields printing in construction order,
containers sharing rather than copying value types on insertion, remove-by-value
using reference identity. A tree-walker on a GC forgives all of these. A compiler
does not, and neither would a second independent implementation of any kind.

**The governing rule that came out of it, now locked:** a configuration where the
same program takes a different branch compiled versus interpreted never ships as a
documented caveat. Either the behaviour is made precise on both sides, or the
compiler refuses with a clean error. The only exceptions are cases that are
genuinely undefined or platform-owned, where there is no single right answer to
converge on — last-digit differences in `pow` (where .NET's `Math.Pow` *is* the
platform libm), filesystem enumeration order, ASCII-versus-locale casing.

**Shell / OS orchestration**, recorded here as a waypoint, is complete on both
backends: files, streams, stdin/stdout, subprocess `fork`/`exec`/`wait`, pipes,
environment variables, directory traversal, and interruptible waits.

**The foundations, and what each became natively:**

| Foundation | Status | What it became in the native backend |
|---|---|---|
| Static type system, explicit types everywhere | ✅ built | Type info available for codegen; no runtime type discovery needed |
| Lexical block scope (`Done.`-bounded) | ✅ built | Defines lifetimes; load-bearing for everything after |
| Voidable type (`voidable T`) | ✅ built | Native model for absence (no GC-assisted null) |
| `failure T`, `Try/In case of exception` | ✅ built | `failure T` = Rust's `Result<T,E>`; exceptions → `sigaction` in native |
| Closures (lexical capture) | ✅ built | Closure record / function pointer + captured env — direct native analog |
| Value semantics for records/objects | ✅ built | C/Zig struct semantics — copy on assign, native-compatible |
| Text toolkit complete + string interpolation | ✅ built | Became an immutable, arena-allocated `const char*` — literals stay static, each operation allocates a fresh result in the current region. Immutability is what kept it simple: no capacity, no growth |
| Constants, interfaces, maps | ✅ built | Standard type-system infrastructure |
| Standard input (`read a line/all/all lines from the input`) | ✅ built | Shell needs stdin; pipes need readable streams |
| File I/O (read/write/append/scoped streams) | ✅ built | Core OS capability; `With ... open` lifecycle = RAII analog |
| Process execution (`run` with args, capture output/exit-code) | ✅ built | Shell's `fork`/`exec`/`wait` at the scripting layer |
| Union types + narrowing (`(A or B)`, `is a <type>`, elimination) | ✅ built | Discriminated unions — tagged values with type-safe dispatch; native analog is tag + union struct |
| Environment variables (`the environment variable "X"`) | ✅ built | Shell needs to read `$PATH`, `$HOME`, etc.; pre-process-launch setup |
| Directory traversal (`the contents of the directory`, `the path … exists/is a file/is a directory`) | ✅ built | Shell needs to list directories, test paths; directory walk is a core shell primitive |
| Cooperative signal handling (`an interrupt is requested` / `Acknowledge the interrupt.` / `Yield.`) | ✅ built | Became a real `sigaction` handler + per-thread `sigsetjmp` landing pads: a thread blocked in a channel receive genuinely wakes, which the cooperative interpreter cannot do |
| Getters / setters (uniform property access, Dart-style) | ✅ built | Controlled field access without syntax change; relevant to native struct layout control |
| Named constructors (`Bind making a <type>`) | ✅ built | User-defined construction logic; factory pattern without a new keyword |
| Destructors / RAII (`Bind unmaking a <type>`, LIFO scope-exit) | ✅ built | Fire LIFO on every exit path, including `return`, `Stop`, failure propagation, exception unwind, and now an interrupt — via a thread-local registry, since objects are stack values and must be unmade while still live |
| Operator overloading (user-defined `+`, `-`, `*`, etc.; fallible overloads) | ✅ built | Enables domain types to use arithmetic syntax; fallible overloads proven viable |
| Books / `Pull` mechanism (bundled: `math`, `collections`, `chance`) | ✅ built | Module-loading boundary established; type-introducing books work; external loader deferred |
| Matrix type + arithmetic (`+`, `-`, `*`; fallible; dimension-mismatch failures) | ✅ built | First type-introducing book; demonstrates the operator-overloading + fallibility pattern |
| Region-model soundness (three-hole adversarial arc — all holes closed) | ✅ built | The invariant the native backend actually runs on. Under a GC a region error is invisible; compiled it is a use-after-free, so the arc was re-run against a real allocator and the region test is now *structural* over a value's whole shape rather than a list of types |
| Cooperative concurrency (scheduler + tasks + channels + task results + SIGINT/Yield) | ✅ built | Became real pthreads with a structured join — a rabbit joins every task it spawned before releasing its region, so a task provably cannot outlive it. Channels are mutex/condvar queues; values crossing a thread boundary are deep-copied, so neither thread's arena is entangled with the other's. TSan-clean |
| Streaming task pipes (`producer \| consumer`, `output`, `for each from the input`) | ✅ built | Pipeline composition; subprocess pipe enhancements (command substitution, exit-code, stderr-visible) |
| Map key value-type constraint (text/number/fact only; reference types → static error) | ✅ built | Prevents the silent-miss class of bugs (reference identity lost under deep-copy); Dijkstra bug root cause fixed |
| Comparison unification + trap sweep (`true`/`false`, ordinals-as-identifiers, negated word-forms, educational errors) | ✅ built | Most-slipped-on rules retired; `true`/`false` work; word and symbol comparison forms position-agnostic |

**Every feature in the table above compiles natively**, and every item once
bundled as "deferred to native" has landed: `pull a rabbit` as a real
task-lifetime arena, true fan-out distribution under OS threads, and
preemptive SIGINT that reaches non-yielding loops, blocked channel waits, and
worker tasks alike.

**The three frictions recorded here as unsolved were each solved. How:**

- **`number` = `decimal` had no hardware instruction set.** Resolved with a
  self-contained software decimal, `CufetDec`, that is bit-identical to .NET's
  `System.Decimal` — including round-half-to-even and the 28-digit scale.
  `libmpdec` was rejected: it implements IEEE-754 decimal, a *different* format,
  and would have added a link dependency. `double` was never on the table; it
  would have made compiled arithmetic silently disagree with interpreted, which
  is exactly the divergence class the project refuses.
- **Reference-type lifetimes needed an ownership model.** Resolved with regions
  plus static escape analysis. The type checker computes, for every store, the
  region depth the value must survive to; the compiler deep-copies it there.
  Values escape outward only, never inward. No GC, no borrow checker, no
  reference counting.
- **`text` needed a real native string type.** Resolved as an immutable,
  arena-allocated `const char*`: literals stay static, each operation allocates a
  fresh result in the current region, and everything is released when the region
  ends. Immutability is what made this simple — there is no capacity to grow.

**What genuinely remains:**

- **Multi-directional predicate dispatch** — a design-first arc and the real
  type-system mountain, deliberately placed after the current version rather than
  allowed to drag the finish line. Not orderable until designed.
- **Move semantics at channel send** — sends deep-copy across the thread boundary,
  which is sound but not free. A move, with the sender's binding invalidated,
  would avoid the copy.
- **A REPL** — see above; still the highest-value ergonomic gap.
- **Separate compilation and an external book loader** — the whole program is
  compiled at once today, which is what makes the bounded open-union
  representation sound. Either feature would require revisiting that.

### What a rabbit actually is (the original conception, and where it still leads)

The rabbit shipped as a memory region, and everything written about it above describes it
that way. That is accurate but incomplete about the intent.

**A rabbit was conceived as a control-flow primitive that happens to use memory.** The
arena is the *substrate*; the purpose is control-flow machinery — continuations,
suspend/resume, capturing and restoring execution state. Concurrency belongs to the same
family, because a task that yields and resumes *is* a continuation being captured and
restored. Green threads are continuations; coroutines are continuations; the exception path
is a one-shot escaping continuation. One primitive underneath all of them.

Two pieces of evidence that this is not retrofitted reasoning:

- **The implementation already contains two restricted continuations.** `In case of
  exception` compiles to `setjmp`/`longjmp` — a one-shot escaping continuation. Tasks are
  the parallel form. The unified substrate is half-real already.
- **The surface drifted toward the conception on its own.** An earlier design session
  settled on *implementation coupling* — a unified substrate underneath, but a standalone
  `Start a task:` surface spawnable anywhere. What actually got built is
  `Have rabbit start a task:`, which *requires* an enclosing rabbit, and channels require
  one too. That is surface coupling, and it is what the original conception implies.

**The open questions, in the order they need answering:**

1. **Surface or implementation coupling?** The code and the recorded decision disagree, and
   everything downstream — how `bury`/`unbury`/continuations read — inherits the answer.
2. **Which restriction?** This decides whether the feature is buildable at all. Full
   first-class continuations would require CPS-transforming the whole program (destroying
   the readable, self-contained C the compiler emits) or copying the machine stack
   (nonportable, and in conflict with both the sanitizers and thread-local arenas).
   Coroutine-shaped continuations — save state, resume in order, one live resumption — are
   very achievable and cover nearly all of the value.
   ★ **The no-divergence rule decides this**, independent of implementation cost: whatever
   ships must work identically on both backends, and a tree-walking interpreter cannot
   faithfully offer `call/cc` either. The oracle discipline makes the design call.
3. **No implicit accumulator.** The original sketch had the rabbit *hold* an unburied value
   in temporary state until used — an invisible register. That cuts against a language
   that made narrowing explicit and refuses invisible capture writes. `Define x as unbury
   <stash>.` gets the same feature with no hidden state.

**A stash is saved execution state, not a stack data structure.** It cannot be a library:
suspend/resume needs compiler and runtime support. (The naming is Turing's — the ACE design
used *bury* and *unbury* for subroutine linkage.)

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

**Interpreter vs. compiled.** Interpreted, "freed at `Done.`" is modelled
semantically — values become unreachable when the block ends and the GC reclaims
them whenever it likes. The interpreter enforces the *static safety rules* and the
*observable semantics*; it does not actually free anything on cue.

Compiled, the arena is physical: a `Pull a rabbit` pushes a bump-allocated,
thread-local region and `Done.` frees every block in it at once. A region is
released on **every** way out — `Done.`, `return`, `Stop`, `Skip`, `Suppress`, a
failure unwind, an exception, an interrupt — not just the normal one. Returning a
value out of a region hands that region's memory to the caller's rather than
freeing it, so the value stays valid and is reclaimed one level out; nothing is
copied, because a returned value may be the caller's own and the two backends have
to keep sharing it.

**This is where the GC was doing real work, and it shows.** Because the
interpreter forgives a region mistake and a real allocator does not, moving to a
physical arena turned several latent design bugs into hard use-after-frees. The
outward-store invariant had to become *structural* over a value's whole shape,
rather than a list of reference types, once a `record containing a series` proved
it could launder a covered type past the check. That is the honest argument for
building the compiler: it audits the memory model in a way a tree-walker
structurally cannot.

**Not in Layer 3, and since delivered:** physical arena allocation, and
`pull a rabbit` in the task-lifetime sense. Still deliberately unsolved: returning
a rabbit / lifetime parameters — handle-passing covers the real cases.

**Layers still ahead.**

- **`book` as a module conformer:** the loading face, gated by a standard
  library existing — and now also by separate compilation, since books are
  resolved at compile time today.
- **`pull a rabbit` as a task-lifetime arena — delivered.** This was recorded as
  native-era future work, and the native backend is where it landed, but the
  feature is not a new form of rabbit: it is the **structured join**. A rabbit
  joins every task it spawned before releasing its arena, so a task provably
  cannot outlive the region it was launched from. Each thread bump-allocates in
  its own arena, so concurrent tasks never contend, and values crossing a thread
  boundary are deep-copied rather than shared. The physical-arena point that
  "only matters once GC is off" is exactly what makes this real: the join is not
  bookkeeping, it is what keeps a task from reading freed memory.
  