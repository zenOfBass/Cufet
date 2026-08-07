# Changelog

All notable changes to Cufet are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning: feature arcs bump the minor version; 1.0.0 marks language stability.

---

## [Unreleased]

### Added

- **Verbatim text — `<<...>>`.** A second spelling for a text literal in which **nothing** is
  interpreted: no escape sequences, and no interpolation holes.

  ```
  State <<C:\Users\me>>.
  State <<{"name": "x"}>>.
  State <<^\d{3}-\d{4}$>>.
  ```

  `"` and `{` are the two characters a quoted literal cannot hold plainly, and they are exactly what
  JSON, regular expressions, Windows paths and Cufet samples inside documentation are made of.
  Inside `<<...>>` both are ordinary characters — a lone `\` is a backslash, and `\q` is two
  characters rather than the lexer error it is in a quoted literal.

  Nesting is depth-counted over `<<` and `>>`, the way block comments already count `/*` and `*/`,
  so text containing the delimiters can still be wrapped: `<<a <<b>> c>>` is `a <<b>> c`. It may run
  across lines. There is **no interpolation** inside it — that is the trade for total literalness,
  and `joined to` covers what a hole would have done.

  A verbatim literal is a **spelling, not a type**. It lexes to the same token a quoted literal
  does, so nothing downstream can tell — or needs to tell — which form produced the text, and every
  text operation works on one unchanged.

- **A line break inside a text literal is one `\n`**, whatever the file is stored as, for `"..."`
  as well as `<<...>>`. A CRLF source no longer puts a `\r` into the text.

  This is a language rule rather than a lexer convenience. Without it the same program means
  different things depending on how the working tree was checked out — `the length of` differs, and
  a comparison against `"a\nb"` succeeds on one machine and fails on another. A language that
  already makes "a character is a Unicode code point" a rule binding on both backends cannot leave
  this one to git's autocrlf setting. It matters most for verbatim text, where a multi-line literal
  is the ordinary way to write one and there is no escape to reach for instead.

  A **lone** `\r` is not a line break on any platform Cufet targets, so one written deliberately is
  kept as a carriage return.

  `examples/rawtext.cufe` found this, and it is the first thing the new example harness caught that
  the unit suites could not: the bug needed a real file, stored with real line endings, run on both
  backends.

  ★ **The `exactly` modifier that was planned alongside this is dropped, not deferred.** The two
  were designed as independent switches — `<<...>>` turning escapes off, `exactly` turning
  interpolation off — which would have given four combinations. It does not survive contact: with
  escapes off there is no `\{`, so a form that kept interpolation could express a literal `"` but
  not a literal `{`, and the flagship case (JSON) is made of both. Restoring it needs a `{{`
  doubling rule, which puts a meta-sequence back into the one form whose whole promise is that it
  has none. The remaining cell — escapes on, interpolation off — saves two backslashes over `\{x\}`
  and costs a reserved word. `exactly` stays available as an ordinary name.

  ⚠ **The one corner:** `<<a>>>` closes at the first `>>`, so text *ending* in `>` needs the quoted
  form (`"a>"`). Nothing else changes: `a < b` and `a <= b` lex exactly as before, since `<<` was
  only claimable because two comparisons in a row is not an expression Cufet has.

- **A matrix cell can be written.** `The item at (row, column) of m becomes 7.` — the write half of
  an accessor that until now could only read:

  ```
  Pull a book on collections.
      Bind void to light, given (the matrix board, the number r, the number c):
          The item at (r, c) of board becomes 1.
      Done.
      Define board as a matrix with 2 by 2 filled with 0.
      Cast light on (board, 2, 1).
      State board.
  Done.
  ```

  A matrix has always been listed among the **reference types**, whose entry reads "element
  mutations are reflected everywhere" — a promise with no syntax behind it. A matrix you cannot
  write to is a series of series with worse ergonomics, which is the opposite of why the type
  exists. A write is visible through every name for that matrix, and a matrix passed to a function
  is the caller's matrix, so a board can be updated in place.

  Cells hold numbers and nothing else. Out-of-range indices fault with the same message the read
  gives, on both backends.

- **`examples/gameoflife.cufe`** — Conway's Game of Life on a matrix, with wrapping edges.

- **★ Every example is now an oracle test.** All 20 programs in `examples/` run on both backends
  under `dotnet test`, and the compiled output must equal the interpreted output exactly. Until now
  they were checked by hand, once, and trusted thereafter — nothing in the suite ran them.

  They have been the project's most productive bug-finders: two days of ordinary programs turned up
  a compiler crash, a live divergence, and a type the compiler could not name. Each one is now a
  permanent regression test on both backends, so writing the next example is also writing the next
  test — verified by dropping a new file in and watching the count rise with no wiring.

  Directory-enumerated, following the soundness-fixture pattern, and guarded the same way: a corpus
  check fails if the enumeration ever breaks, since a directory-driven suite that finds nothing
  still goes green. Examples run with the working directory at the **repository root**, because
  that is where a reader runs them and `wordfreq.cufe` opens its sample by a root-relative path.

  Five concurrency and subprocess programs cannot be built under mingw (pthreads, `sigaction`,
  `fork`). They are skipped **with a reason each**, and still held to the shared front end by a
  second theory — a platform gap never becomes a blind spot in the language.

- **An example can pin its output, not just its agreement.** An `examples/expected/<name>.expected`
  file makes the harness assert the output matches it exactly. Opt-in — no file, no assertion — and
  written for `config`, `huffmancoding`, `json`, `sudoku`, `recursivedescent` and `rawtext`.

  The pins sit in their own directory rather than beside the programs: `examples/` is read by people
  looking for programs, and one fixture per example halves the signal in that listing. `assets/`
  already established that support material for the examples belongs in a subdirectory of them.

  ★ **Because agreement is not correctness.** The oracle proves the two backends say the same
  thing; it cannot tell whether that thing is right. `config.cufe` carries a deliberately malformed
  line so its error path runs, and if that warning ever stopped appearing both backends would agree
  on the new output and the test would still pass. Verified by deleting the malformed line: the
  build now fails and shows the missing warning. A second check refuses a `.expected` for an example
  that never runs — a missing one, or a non-deterministic one like `markov` — so the file cannot
  become an assertion nobody evaluates. A third holds the pin count, because a deleted `.expected`
  takes its assertion with it silently: the comparison is opt-in, so no file means no check and the
  run stays green. Verified by moving one out and watching the count guard fail.

- **Documented samples are held to the front end.** `DocBlockTests` extracts every fenced block from
  README, GRAMMAR and REFERENCE, runs the ~238 that look like programs, and records the 157 that
  check clean. A recorded sample that stops checking fails the build, naming its file and line.

  A change detector rather than a gate: the other 81 failures are mostly correct — GRAMMAR is a
  constraints reference full of deliberate counter-examples, and many blocks are fragments. Judging
  those needs a fence convention the docs do not have; see ROADMAP.

  **Two checks, because one has a blind spot.** Hashing block text catches an unchanged sample the
  language broke underneath, and can say which. It cannot see an *edit* — a rewritten block gets a
  new hash and simply drops out of the baseline, so breaking a sample by rewriting it looked
  exactly like rewriting it legitimately, and the first version of this test passed while a sample
  was broken. A count check closes that. Both were verified by reintroducing the failure each is
  meant to catch.

- **Exhaustiveness tests, against the bug class that produced three of the fixes below.** Every one
  was a hand-written switch over types or AST nodes, missing an arm, whose default returned a
  plausible wrong answer rather than failing. Four tests, each verified by reintroducing the bug it
  is meant to catch:
  - `EveryCufetType_HasAName` — no `CufetType` may fall through to `FormatTypeName`'s `"value"`.
    That fallback *is* what "printing a 'value' is not yet supported" was.
  - `EveryCufetType_HasAFactory` — a new type has no test instance, so it fails until someone adds
    one and checks the per-type switches.
  - `EveryBodyBearingNode_HasBeenConsidered` — a new AST node with a statement body cannot appear
    without failing, and the failure names the hand-written descents to revisit. Removing `Judge`
    from its list reproduces the exact miss that shipped.
  - `EveryCufetType_IsNamedByTheFrontEndToo` — the same totality for the checker's `FormatType`.

  Deliberately **not** an audit of every walk against every node: several omissions are correct —
  `InferBodyReturnType` stops at a nested `BindStatement` on purpose. The tests close the door new
  constructs came through rather than judging the existing ~60 walk-by-node cells, which is a
  separate job needing a reason per cell.

### Changed

- **Four more hand-written walks converted to the shared `AstSearch`.** `ProgramUsesConcurrency`
  (whose own comment already recorded losing concurrency inside a book pull),
  `CollectInterfaceDefs`, `CollectObjectDefs` and `MergeUntoMethods` — the last of which was
  silently dropping `unto` methods declared inside a book pull. A generic walk has no list to fall
  behind, which beats a test that reports the list went stale.
- **`FormatTypeName` can now name every type.** Seven — mapping, both stream types, rabbit,
  failure, exception and book — used to be reported as `'value'`.

### Fixed

- **★ Compiled, a newline INSIDE a text value was rewritten on Windows.** `State "a\nb".` printed
  `61 0a 62` interpreted and `61 0d 0a 62` compiled — a live divergence, present since escape
  sequences existed.

  Windows opens stdout in **text mode**, where the C runtime turns every `\n` on its way out into
  `\r\n`. That is right for a line terminator and wrong for data: a `\n` the program put inside a
  text value is content, and rewriting it made the compiled backend print something the interpreter
  did not. Stdout is now opened in **binary** — nothing is rewritten — and the terminator `State`
  appends is explicit, still `\r\n` on Windows to match the interpreter's `WriteLine`. Eleven
  by-hand terminator sites became one `cufet_nl()`. Files were never affected: both backends were
  already byte-clean there, the compiler through `fopen(…, "wb")` and the interpreter through
  `Write`. Subprocess output forwarded to stdout was being mangled the same way and is fixed with it.

  ★ **The oracle could not see it, and that is the more important half.** Both runners normalised
  `\r\n` to `\n` before comparing, so rewritten data compared equal to untouched data — the suite
  was structurally blind to the axis for as long as it has existed. It surfaced only because
  `examples/rawtext.cufe` put a `\r\n` **pair** in a literal, which normalisation cannot flatten.
  **This is the second bug of exactly this shape**, after the CLI encoding one below, so the fix is
  to the harness and not just to the symptom: backend-vs-backend comparisons are now byte-exact,
  and normalisation survives only where the expected value is a C# literal or a checked-in
  `.expected` file that travels through git's line-ending conversion.

  Measured before committing to it: pointing all 379 oracle assertions and the example harness at
  raw output surfaced **exactly one** divergence across 562 tests — this one. Nothing else was
  hiding. `GeneratedC_UsesTheNewlineMacro` now refuses a by-hand newline in the emitted C, because
  a new `State` arm would otherwise reintroduce it silently; verified by putting the bug back at
  one site and watching both it and the behavioural test fail.

- **★ The CLI mangled every non-ASCII character it printed.** `State "héllo 👍".` came out as
  `h?llo ??` interpreted and correctly compiled — a real divergence, because the CLI wrote through
  the console's default encoding, a legacy code page on Windows, while a compiled binary writes
  UTF-8 bytes directly. Now set explicitly, and a redirected stdout gets it too.

  ★ **The test suite could not have caught this**, which is worth recording. Its interpreter side
  writes to an in-memory `StringWriter` and its compiled side reads the binary with
  `StandardOutputEncoding` already UTF-8, so both are lossless in-process and only the console ever
  lost anything. Found by pinning `examples/expected/json.expected` — the round-trip of `["héllo 👍"]` — and
  reading the bytes rather than the terminal. `json.expected` now holds that assertion permanently.

- **★ An object declared inside a book pull crashed the compiler.** `CollectObjectDefs` was a
  hand-written switch over block-bearing statements with no arm for `PullStatement`, so this was
  never registered — and `build` died with a raw `KeyNotFoundException` rather than any
  Cufet-level error. `check --native` passed it, because nothing on the check path looks the
  definition up:

  ```
  Pull a book on collections.
      Define object flagset with (the text name, the bits mask).
      Define modes as a series of flagset with (…).
  Done.
  ```

  Now collected with the shared reflection walk (`AstSearch.Visit`), which has no list to fall
  behind — the third instance in two days of a hand-written per-node switch quietly returning
  less than the truth.
- **★ `bits` worked as a scalar and broke inside every container.** `State`, interpolation, `is`,
  ordering and function parameters were all fine; putting a `bits` in a series, record, object,
  map, voidable, union or channel was not. Declaring an object with a `bits` field was enough on
  its own, and the error blamed printing:

  ```
  Define object holder with (the bits pattern, the number width).
  State "nothing to do with it".      ← "printing a 'value' is not yet supported"
  ```

  `bits` was missing an arm in **seven** per-type switches across both backends — `TypeSig`,
  `WriteCall`, `EqCall`, `IsChanPod`, the channel element list, `StaticKindMatches`,
  `RuntimeIsType`, `StaticMatch` and `IsRegionBearing`. Two were worse than a refusal:
  **`is a bits` silently answered `false`**, because `RuntimeIsType` fell through to its default
  arm; and **`channel of bits` was a live divergence**, running interpreted and refused by the
  compiler. `FormatTypeName` could not name the type either, which is why the message said
  `'value'`.

  This is the second instance in two days of a hand-written per-type switch quietly returning a
  plausible wrong answer — see the `Judge` entry below.
- **★ Ctrl-C did nothing to an interpreted program that did not poll for it.** The key handler set
  `e.Cancel = true` on every program, taking the OS kill away — and the flag was then read only at
  `Yield.`, a channel receive, a task await, a subprocess wait, or an explicit poll. A loop of
  ordinary statements reached none of them, so a running program could not be stopped from its own
  terminal at all. Two changes, and a rule:
  - **Every statement is now an interrupt checkpoint** — but only for a program that never mentions
    interrupts. Handle them and you are in charge of them; ignore them and Ctrl-C behaves the way it
    does everywhere else. The rule is decided once, from the whole program, because a rule that
    changed with position could not be reasoned about from a terminal.
  - **A second Ctrl-C always terminates.** Cancelling the kill is only defensible while something
    can still act on the flag.
  - An interrupted run now unwinds cleanly and exits **130**, as the reference already specified,
    instead of escaping as an unhandled exception and printing a .NET stack trace.
- **The linter told you to capitalise the middle of a sentence.** A one-line `If` whose body wraps
  puts that body at the left margin of a line nobody else opened, and the capitalisation rule read
  leftmost-on-its-line as first-in-its-sentence:

  ```
  If name is "world",
      cast greet on (name).      ← "this line opens with 'cast' — write 'Cast'"
  ```

  The rule now asks what came before: a `.` ended the previous statement and a `:` opened a block,
  so a sentence really does begin — but a `,` means one is already under way.
- **A poll inside a `Judge` was invisible to the compiler.** Both backends decide whether a program
  handles its own interrupts by searching the AST; the compiler's search was a switch with an arm
  per statement type, written before `Judge` existed. A judgement's arms were never searched, so
  such a program compiled with no signal substrate while the interpreter handled it cooperatively.
  Both now use one reflection walk (`AstSearch.Contains`), which searches new node types without
  anyone having to remember them.
- **★ Widening stopped dead at a wrapper.** A case type widens into its union — but not when the
  union sat inside `or failure` or `voidable`, which is the most natural return type a
  recursive-descent parser has: every branch yields one node kind, and any branch can fail.

  ```
  Bind (num-node or add-node or mul-node) or failure to parse-factor:
      Return a new num-node { the value 1 }.      ← refused
  Done.
  ```

  `IsAssignable`'s wrapper arms compared against the inner type instead of recursing into it. They
  recurse now — and **three** places had the same shape. The moment the checker stopped refusing,
  the compiler emitted the bare object where the union struct belongs (`incompatible types when
  initializing`), and `but on failure` emitted an unwidened default, so the two arms of its ternary
  disagreed (`type mismatch in conditional expression`). Both compiler halves now widen through the
  wrapper the way `but void is` already did. `check --native` passed all three; only a build caught
  them.

- **★ No book was usable inside a function.** Every book — `math`, `collections`, `chance` — was
  dropped by function isolation, so a function written inside the pull that opened it could not
  reach it:

  ```
  Pull a book on math.
      Bind number to root-of, given (the number n):
          Return math's square root of (n) but void is 0.   ← "'math' isn't defined"
      Done.
  Done.
  ```

  A pulled book is a lexical capability, not a local you might close over by accident, so it
  survives isolation now — the same reasoning that kept a book's *types* visible, one entry below.
  The two halves had to move together: fixing only the checker turned `chance`'s static refusal
  into `math` failing at **runtime**, which reads like the pull never happened. That left books
  good for little but top-level code, which is not what a standard library is for.

- **A book's types were invisible inside a function body.** Function isolation cleared the type
  scopes along with the value scopes, so `a matrix with 3 by 3` was a static error inside a function
  written within `Pull a book on collections.` — while `given (the matrix m)` in the same signature
  was accepted, because an annotation resolves `matrix` in the parser and never consults scope. A
  book's scope is lexical; only value bindings are isolated now.

- **The playground's semantic-token layer never reached the screen.** Three separate things had to
  be true and none of them was, which is why the registration side kept auditing clean:
  - `registerDocumentSemanticTokensProvider` only fills a registry. What *reads* that registry is an
    editor contribution, and `editor.api` — the entry chosen to avoid shipping ninety grammars —
    imports exactly one contribution, and it is not this one. It is now imported explicitly.
  - The feature then asks whether semantic colouring is enabled. The setting defaults to
    `configuredByTheme`, and a standalone Monaco theme hard-codes that flag to `false` and never
    reads it off the theme data — so `semanticHighlighting: true` on `defineTheme` was inert. The
    setting is now set directly, and the page says so in the console if it fails to take.
  - Monaco asks once per model revision, and the first ask lands seconds before the .NET runtime in
    the worker can answer. That null was never invalidated. The provider now carries an
    `onDidChange` that fires when the runtime becomes answerable.
- **A possessive owner kept the grammar's colour on its `'s`.** The TextMate grammar scopes `rex's`
  as one word on purpose; the semantic token stopped at `rex`, so the name visibly changed colour
  halfway through. The producer now widens an owner's token to cover its marker.
- **The playground's build now warns when its AppBundle is older than `src/`.** The bundle carries a
  compiled interpreter, so an un-republished change shipped as though it had been applied — the page
  loads, runs, and quietly answers with an older front end, which reads as a fix that did not work.
- **`Judge`, `where` and `Descend` were not coloured** — the 0.13.0 keywords never reached the
  TextMate grammar. A judgement's arm cases are now classified as types, too, which is the one
  place a bare type name opens a statement.

---

## [0.13.0] — 2026-08-03

0.12.0 made Cufet explain itself. **0.13.0 makes it account for every case** — a
judgement the compiler proves total, text positions the two backends finally agree on,
and a linter that names what a pass over the source can decide and stays quiet where it
cannot.

### Added

- **`Judge` — an exhaustive case construct.** Dispatch on what a value *is*, with coverage the
  compiler can prove:

  ```
  Judge node, where it is:
      A num-node, return the value of it.
      An add-node or a mul-node, return cast fold on (it).
      Otherwise, return 0.
  Done.
  ```

  The subject and verb are stated once in the header so each arm completes the sentence. It is
  evaluated **once**, bound to `it`, and `it` is **narrowed** inside each arm — so
  `the length of it` is legal in an `A text` arm and nowhere else. A grouped arm does not narrow,
  because an arm covering two cases cannot know which one arrived.

  ★ **Coverage is total, by proof or by default.** Over a **closed union** whose arms cover every
  case, `Otherwise` is optional and a missing case is a static error. For any other subject,
  `Otherwise` is required. Control can never fall off the end of a `Judge` — the same discipline
  `voidable` applies to absence, and stricter than C#, where a non-exhaustive switch expression is
  a warning that throws at runtime.

  **The subject may be an expression.** Narrowing is variable-level, so `If` cannot narrow a value
  produced by an expression — you have to name it first. `Judge` names it, as `it`.

  `or` groups cases, which is what C-style fall-through is overwhelmingly used for; no
  fall-through machinery is needed for it. Arms take the comma form for one statement or a colon
  and `Done.` for a block, exactly as `If` does. A judgement whose arms all return counts as
  returning, so it works as a function's whole body.

  ⚠ **The native backend takes closed unions only.** A `Judge` over any other subject interprets
  and is **refused cleanly** by the compiler; value arms would compare values rather than dispatch
  on a tag. `Descend.` — explicit fall-through — is reserved and not yet accepted.

- **A style linter, reporting through `check`.** Legal code that reads worse than it needs to. It
  cannot produce an error — the checker answers "will this run", this answers "is this how you
  would want to have written it", and the second question has no right to stop the first. Warnings
  are non-fatal, so `check` still exits 0; `--strict` promotes them for a CI gate.

- **First rule: start a line with a capital letter.** A statement reads as a sentence. Keywords are
  case-insensitive, so `for each x in xs, repeat:` and `For each …` are the same program — which is
  exactly why this belongs to a linter and not the parser.

  ★ Only the half that needs no judgement ships. A line opening with a **keyword** can always be
  capitalised and the fix is unambiguous. A line opening with a **variable's own name** is left
  alone: capitalising it would rename it, so only an article could supply the capital, and whether
  `The total becomes 5.` reads better than `total becomes 5.` is a judgement this pass cannot make.
  That half is deliberately unimplemented rather than implemented badly.

  The distinction is genuinely contextual, so the parser records it rather than the linter guessing:
  `output 7.` opens with a keyword, while `output becomes 10.` opens with a variable that happens to
  share the spelling. Suggesting a capital on the second would not improve it — it would break it.

  The judgement half is now **settled rather than pending**: it stays advice for a human and is
  never flagged. Whether an article reads naturally depends on whether the name is a noun, and
  `The got becomes 5.` is not English. The fix there is to rename the variable, which no pass over
  the source is entitled to suggest.

- **Second rule: nested bare-`it` loops.** `For each in xs, repeat:` binds the element to `it`, and
  two of those nested is legal with no doubt about its meaning — the innermost binding wins, like
  any other shadowing. But the reader has to hold which `it` is which, and the source stopped
  saying. Reported at the **inner** loop, because naming that one leaves the outer loop reading
  exactly as it did. A named loop in between does not break the chain; two loops side by side are
  not nested and say nothing.

- **Third rule: change the current directory before starting tasks.** Tasks resolve relative paths
  against the process's current directory, and there is one of those for the whole process. A
  rabbit that changes it while its own tasks are already running is a race — which directory a task
  sees depends on when it happens to run.

  ★ A warning rather than a refusal, and only for the ordering that is actually wrong. The compiler
  already refuses this *inside* a task and its message recommends changing the directory **before**
  spawning; flagging that recommended ordering would mean the two tools contradict each other. A
  change made before the first task starts is silent — it is the fix, not the fault.


- **`from <map>, the entry for <key>`** — the map-first way to read an entry, alongside the
  existing `the entry for <key> in <map>`. `from the map ages, …` is accepted too.

  This closes a real asymmetry rather than adding a synonym: **writing** an entry has only the
  map-first order — `In ages, the entry for "alice" becomes 30` — and there is no trailing form for
  it. So before this, reading and writing the same entry had to be said in opposite orders, with
  neither operation offering the other's. It was in the original map spec as the "optional leading
  form", and was documented in REFERENCE without ever being built.

  Parser-only: it produces the same lookup node as the trailing form, so both backends got it with
  no codegen change, and `check --native` and the compiled binary agree by construction.

### Changed

- **The linter now reads the AST as well as the token stream.** Its first rule judges how a line
  looks before it means anything, which tokens answer; the rules after it judge shape — one loop
  inside another, a statement ordered after a statement — which they cannot. `Linter.Lint` takes the
  parsed program alongside the tokens.

  The shared walk descends into method, getter and setter bodies hanging off an object definition,
  which no pass had reached before, and marks function-like bodies as new scopes so a rule tracking
  "am I inside an X" forgets on the way in. That flag is defensive today — the parser refuses a
  function declared inside a block — and is kept so the rules stay correct if local functions land.

- **A REFERENCE chapter on recursive shapes** — how to write a node that holds nodes, why a field
  holding its own type by value cannot close, and why a container can. The refusal for the by-value
  form has existed since 0.11.0, but its error message was the only place the working alternative
  was written down.

  It documents the backend split honestly: a self-referential field **runs interpreted** — the
  interpreter never needs a fixed layout — and the native compiler refuses it. `check --native`
  reports that as a warning and still exits 0, so it is caught before `build`.

### Fixed

- **A value passed to a union-typed parameter did not compile.** Widening a narrower value into a
  voidable, union or failable is the language's one implicit coercion, and it was applied at every
  slot except one: a call argument emitted the raw expression instead of coercing to the
  parameter's declared type. So an object handed to a `(number or box)` parameter produced C that
  assigned the object struct straight into the union struct.

  The checker accepted it and `check --native` reported **"No problems found"** and exited 0 —
  only `gcc` refused it. Arguments are now emitted **as their parameter's type**, with a fallback
  to the previous behaviour where the signature is unknown.

  ★ This class is invisible to the oracle. The compiler suite proves compiled output equals
  interpreted output, and here there was no binary to compare — the build died first. Both bugs of
  this shape so far (this and nested `voidable`) were found by writing a program in Cufet, not by
  testing.

- **`output` and `seed` are coloured by the parse, not by their spelling.** Both open a statement
  while lexing as ordinary identifiers, so a program may equally name a variable either one. That
  question is not answerable by a regex, and two attempts to answer it in the TextMate grammar
  each failed in a different direction: colouring the word repainted someone's variable as
  language syntax, and colouring only the **capitalised** spelling gave one statement two
  different colours depending on whether its line had been capitalised yet.

  The semantic-token pass has the parse, so it simply knows which one an occurrence is. A
  `keyword` kind joins the legend — the first that is not a name — and the statement is coloured
  identically however it is written, while a variable called `output` stays a variable at every
  use. The grammar now deliberately carries **no** rule for either word, with a comment saying
  why, so the next person does not add one back.

- **The VS Code extension kept showing diagnostics for text that was already gone.** When a file
  changes outside the editor — a git checkout, another tool, a formatter — VS Code reloads the
  buffer and fires `onDidChangeTextDocument`, but the handler discarded it unless `checkOn` was
  set to `type`, and the default is `save`. So the squiggle survived until the next save,
  pointing at a line that had been fixed. A change that leaves the buffer **clean** cannot have
  come from typing, so it now re-checks whatever the mode. A stale warning is worse than no
  warning: it costs the reader a hunt for something that is not there.

- **An unresolved call blamed a typo for what was a forward reference.** A `Bind` nested inside a
  rabbit is a closure and the compiler emits it where it stands, so it cannot call a name declared
  further down the same block — and two nested functions that call each other cannot both come
  first. The refusal is legitimate; the message was not. It read *"'is-odd': unresolved call — not
  a known function or method"* about a function declared six lines below, sending the reader to
  hunt for a misspelling instead of moving the pair to the top level, where mutual recursion works
  on both backends. Found by writing a program in Cufet rather than by inspection.

  A name that genuinely is bound nowhere keeps the blunt message, so the fix does not trade one
  misleading sentence for another. GRAMMAR now records the nested-versus-top-level distinction,
  which it previously left to be discovered.

- **Text positions disagreed between the backends on any non-ASCII string.** The interpreter
  counted UTF-16 code units and the native compiler counted bytes, so the same program gave
  different answers depending on which backend ran it — the exact thing the no-divergence rule
  exists to forbid, and not one of the documented platform exceptions.

  ```
  the length of "héllo"     interpreted 5   compiled 6     → now 5
  the length of "👍"        interpreted 2   compiled 4     → now 1
  the characters from 3 to 5 of "héllo"     "llo" vs mojibake
  the position of "llo" in "héllo"          3 vs 4
  ```

  **A character is now a Unicode code point, on both sides.** Note the `👍` row: the interpreter
  was wrong too, so this was not a matter of making C agree with .NET — a UTF-16 code unit is no
  more a character than a byte is. Both backends changed.

  Four operations were affected: `the length of`, `the characters from`, `the first`/`last N
  characters`, and `the position of`. The rest needed nothing, because UTF-8 is
  self-synchronising — one character's bytes cannot occur inside another, so `contains`, `split`,
  `replace` and `joined to` already gave identical results.

  Code points rather than grapheme clusters: segmenting what a reader sees as one character
  needs the Unicode tables, which the emitted C will not carry. `e` plus a combining accent
  therefore counts as 2, and REFERENCE says so.

  ★ **The oracle was never at fault — its test data was.** The compiler suite already asserts
  compiled output equals interpreted output for every program, and it would have caught this on
  day one had any test string held a character outside ASCII, where a byte, a code unit and a
  character are all the same thing. Non-ASCII cases are now in the suite, plus interpreter tests
  asserting the values outright, since an oracle alone cannot catch both sides being wrong the
  same way.

- **Highlighting split capitalised words down the middle.** The grammar's catch-all identifier rule
  matched `[a-z][A-Za-z0-9-]*` with no leading boundary, so in `Output` the engine failed at `O`,
  succeeded at `u`, and coloured `utput` as a variable — leaving the capital unscoped. It could not
  bite before this release: every capitalised word was already claimed by a keyword rule, and making
  `output` and `seed` capitalisable created the first ones that were not. Anchored with
  `(?<![\w-])`, the same guard the neighbouring rules already used. Both editors read this one file,
  so the fix reaches the extension and the playground together.

- **`Output` and `seed` were coloured inconsistently, each wrong in the opposite direction.**
  `output` was in no keyword rule, so the statement form went uncoloured; `seed` was in the
  case-insensitive statement-verb list, so a variable named `seed` was painted as a keyword — the
  precise thing that word was unreserved to allow.

  Both now match **capitalised only, case-sensitively**. An identifier must start lowercase, so
  `Output` can only ever be the statement and that half is decidable with no context. The lowercase
  spelling is genuinely ambiguous, and it falls through to the identifier rule and reads as a name —
  the safer of the two available wrongs, since repainting someone's variable as a keyword suggests a
  word is reserved when the point of unreserving it was that it is not.


- **`tools/doc-sweep.py`** — extracts every fenced code block from README, GRAMMAR and REFERENCE
  and runs `cufet check` on it, grouping failures by error shape. `--strict` exits 1 for CI. This
  is the sweep below, kept rather than thrown away: doc rot accumulates silently, and reading does
  not catch it.

- **Every code block in README, GRAMMAR and REFERENCE was extracted and run.** 262 blocks; the ones
  that are real programs now execute. Six were broken, and they fall into three kinds:

  - **A form that was documented but never built.** `Define x as from ages, the entry for "k".` —
    the "leading-from form" for map lookup. No parser support, no mention in GRAMMAR, and it does
    not parse. Removed.
  - **Samples naming things with reserved words**, which a reader copying them cannot run:
    `Bind number to add …` (`add` opens a statement), `Define a as a matrix …` (`a` is an article),
    and `Define contents as read all …` (`contents` is a keyword).
  - **Possessive access on a record.** README and REFERENCE both showed `result's output` for a
    subprocess result, but `run` returns a *record* and `'s` requires an object. The working form is
    `the output of result`. REFERENCE now says so explicitly.

  Also removed a note describing a `converted to text` parser quirk in possessive position: it does
  not reproduce, and the expression it warned about is a type error for a separate reason.

  ★ The lesson is the one the sweep exists for — every one of these had been read many times and
  none had been run.

- **The README pipe example mixed two loop forms.** A pipe consumer takes `For each n from the
  input:`; the sample wrote `, repeat:`, which is the series form, so it did not parse.

- **The REFERENCE example for reading a file line-by-line did not run.** It opened the stream `as
  stream` — and `stream` is a reserved word, so the sample every reader would copy failed on its
  first line with *"expected Identifier, got Stream"*. The feature was fine; only its example was
  broken.

- **The refusal for `read a line from the file "…"` said the wrong thing twice.** It claimed
  line-by-line file reading was "not yet supported" — it has been all along, through
  `With the file … open for reading as …` — and it pointed at `read all`, which loads the entire
  file, the one thing someone asking for a single line is trying to avoid. It now explains that a
  path has nowhere to remember how far you have read, and names the form that does.

## [0.12.0] — 2026-08-01

0.11.0 made Cufet usable by someone who is not its author. **0.12.0 makes it explain itself.**

Diagnostics now know where they point — every token and AST node carries a column, and a type error
reports its position as data rather than by reading its own message back with a regex. An editor
can ask what each *name* is, which is the one thing a grammar of regular expressions can never
answer in a language whose surface is English. And a warning is finally something other than an
error that changed its mind: `check` distinguishes "this will not run" from "this will run, and
here is something worth knowing", which is what let a task's discarded write become a note instead
of a refusal.

Two words came back to programs that never asked for them — `seed` is no longer reserved, and
`Output` may be capitalised, which removed the last statement unable to start with a capital.

One new language feature: a type may be written in front of the name. `Define the (number or text) x
as 42.` — and with it, a union-typed variable became expressible at all.

### Added

**Explicit typing**

- **`Define the text name as "Nathan".`** — a type may be written between the article and the name,
  the same `the <type> <name>` shape parameters and object fields have always used. One rule, in
  one more place, rather than a second way to spell a declaration.

- **A union is written the same way**, which is what the form is really for:
  `Define the (number or text) x as 42.` The binding's type is the one declared, not the one the
  first value happens to have, so `x becomes "hello"` is legal afterwards and both branches of
  `is a` narrow. Without this a union-typed local could not be written at all — unions only
  reached a variable through a parameter or a container element.

- The value **widens into the declared type** using the language's one implicit coercion, the same
  one `becomes` and `return` already perform. A value that does not fit is an error at the
  declaration, naming both types.

**`seed` is no longer reserved**

- `Define seed as 42.` works. `seed` was the last piece of chance-book vocabulary still taken from
  every program in the language — `random`, `randomly`, `shuffled` and `guess` have long been
  contextual, on the principle that a book does not get to claim a name from programs that never
  pull it.

- It was held back because `Seed the chance with <n>.` is capitalised and an identifier must start
  lowercase. Capitalised contextual statement words removed that obstacle, so the word goes back —
  and it is one the code most likely to pull this book will reach for.

- The statement is recognised by the **`chance` that must follow it**, which is exact rather than
  approximate: no statement form is `<variable> <name>`, so a variable called `seed` can never be
  followed by `chance`. `Define seed as 42.`, `seed becomes 43.` and `Seed the chance with seed.`
  all coexist in one program. Using the statement without pulling the book reports exactly what it
  did before — that check lives in the type checker and was not touched.

**`Output` may be capitalised**

- `Output 7.` now lexes, and means exactly what `output 7.` means. `output` opens a statement but
  is deliberately not reserved, so a program can still call a variable `output` — which left it as
  the one statement in the language that could not begin with a capital, right as a style rule
  asking for exactly that comes into view.

  It costs nothing to allow, which is the point: an identifier must start lowercase, so `Output`
  was never available as a name to take away. Reserving the lowercase form would have taken one
  from every program. The capital is meaningful only in the statement position — `Output` is not a
  second spelling of a variable named `output`.

**Semantic tokens**

- **`cufet tokens --json <file>`** reports what each *name* in a program is — variable, function,
  type, parameter, property, or namespace — as one JSON object per line with a position and length.
  This is the layer a TextMate grammar cannot reach: a regex over one line cannot tell a function
  from a variable in a language whose surface is English, and in Cufet most of a line is bare words.

- **Both editors consume it.** The VS Code extension and the playground register semantic-token
  providers over the existing grammar, so keywords, strings and comments keep the colours they had
  and names gain their own. The kinds come from the real front end, so the colouring agrees with
  the type checker by construction rather than by resemblance.

- Names inside an interpolated string are coloured too — `"{total} sold"` shows `total` as the
  variable it is, which is precisely the case no grammar can see.

**Warnings**

- **A diagnostic can now be a warning** — true, worth saying, and not a reason to stop. Errors are
  still exceptions, because a program that does not type-check cannot run; a warning is collected
  instead and the pass carries on.

- **`check` exits 0 when the program will run.** An error exits 1 as before; warnings alone no
  longer do. **`--strict`** makes any warning exit 1, for a CI gate that wants the stricter reading.

- **`check --native` reports compatibility as a real warning.** It always called these warnings —
  the interpreter runs those programs happily — but exited 1 anyway. Now the severity and the exit
  code agree. Running and building print warnings to stderr and carry on.

- **A task may write to a captured variable when nothing outside it ever looks.** Writing to a
  capture used to be refused outright, and rightly: the interpreter hands a task the live enclosing
  binding while the compiler hands it a copy, so the write is visible in one and discarded in the
  other. But that only *differs* if something reads the binding afterwards. When nothing does, both
  backends print the same thing, and the program now compiles with a warning saying the write is
  discarded.

  ★ The check is deliberately blunt: any mention of the name anywhere outside the task — a read, a
  write, a mention on a branch that never runs — brings the refusal back. Over-approximating is the
  safety argument. Being wrong can only keep a refusal that was not strictly necessary; it can never
  let a divergence through.

**Column-precise diagnostics**

- Every token and AST node now carries a **column** alongside its line, and diagnostics report
  `file:line:column:` instead of line alone. `check --json` gains a `column` field, and the VS Code
  problem matcher reads it.

- Type errors now carry their position **structurally** rather than having the line scraped back out
  of the message text — the groundwork the column data sits on. The one exception is the known
  `ResolveParamType` unknown-type-name error, which has no expression in scope and stays on the
  line-only fallback.

**The current directory**

- **`the current directory`** reads it, as `voidable text` — void only when the process has none
  to report, which means it was removed underneath it. Voidable for the same reason
  `the environment variable` is: the answer comes from the OS, and the OS is allowed to have none.

- **`The current directory becomes <path>.`** changes it. A fallible *statement*, like writing to
  a file — a bad path is a handled failure, not the end of the program, which is what lets a shell
  implement `cd` without a typo ending the session.

- **`current` is not reserved.** It is promoted to a keyword only when `directory` immediately
  follows, so `Define current as 0.` and `given (the number current)` keep working. This matters
  more than it did for the alternative spelling — `current` is a far more tempting variable name
  than `working`, so the shorter phrase is also the safer one.

- **Four failure categories**, of which one is new: `not-found`, **`not-a-directory`**,
  `permission-denied`, `disk-error`. Changing into a file is an ordinary typo and now says so
  rather than hiding inside a generic error.

  ★ That category cost something to make honest. .NET collapses "no such directory" and "that is
  a file" into one `IOException`, while POSIX `chdir` separates them as `ENOENT` and `ENOTDIR` —
  and Windows reports `ENOENT` for both anyway. So **both backends now `stat` the path before
  changing into it**, which is what makes the category agree everywhere rather than only on Linux.

- **Refused inside a task.** A process has exactly one working directory, so changing it from a
  task races every other task resolving a relative path — with the rabbit's join providing no
  ordering between them. Two well-defined answers is the never-ship class, not the platform-owned
  exception, and the cooperative interpreter would have hidden it by running deterministically.
  The compiler refuses with an explanation. Reading from a task stays allowed.

- **[`examples/shell.cufe`](examples/shell.cufe) has `cd`.** The gap the roadmap called the one
  visible hole in the program most likely to be shown to someone. Bare `cd` goes home; a bad path
  prints and the loop carries on. It also makes the already-permitted `pwd` mean something, since
  it can now report somewhere you chose.


**An empty map no longer needs `with ()`**

- **`Define ages as a map from text to number.`** now builds an empty typed map, the same way
  `a series of number.`, `a catalogue of (number or text).` and
  `an atlas from text to (number or text).` already did. Map was the only container that still
  demanded the empty parentheses — and `atlas`, the map analogue, did not.

  Purely additive: `with ()` keeps working and no existing program is affected. The clause is
  still required when the types are absent, because `a map.` has neither an annotation nor
  entries to infer from.

  Found by writing the examples in the sugared style and noticing where it stopped working — the
  kind of gap that only shows up when someone uses the language uniformly rather than feature by
  feature.

**A task can await another task**

- `the awaited result of <name>` now works **inside a task body**, not only in the rabbit body,
  and **several tasks may await the same task**. Work can be staged rather than only fanned out.
  The interpreter always allowed this; only the native backend refused, so the oracle already
  defined the answer.

- **The compiler's await no longer joins.** A named task publishes its result to a small box
  (mutex, condvar, envelope); awaiters wait on the box and deep-copy into their own arena, and
  `pthread_join` happens exactly once, in the rabbit's `Done.` teardown that the structured
  guarantee requires anyway.

  ★ That change is what makes N awaiters correct **by construction** rather than by guarding.
  The old design's check-then-join guard was sound only while exactly one thread could run it;
  with two tasks awaiting one task, a mutex around it would have had to be held across the join,
  which reintroduces the deadlock the language's own scoping rules had just made impossible.
  Removing the join removed the whole class.

- **No deadlock is possible.** Awaiting a task requires its name to be in scope, so it was
  declared earlier — the wait graph is a DAG by construction, and the forward reference a cycle
  would need is a type error. Nothing had to be built to detect this; the scoping rule already
  guaranteed it.

- Ownership moved with it: the result envelope now lives in the box until `Done.` frees it
  through the recorded deep-free, instead of being freed at an await that may never happen. A
  reference-typed result nobody reads still frees deeply.

- Verified on Linux: oracle-matched across number, text, series, an object holding a series, a
  fallible result, a three-deep chain and two tasks awaiting one — **ASan/LSan clean and TSan
  clean** on every one.

### Fixed

- **A voidable no longer nests, and the annotation that nested one no longer miscompiled.**
  `voidable voidable T` is now simply `voidable T` — there is one absent value, so a second layer of
  "or nothing" adds no state a program could observe.

  ★ It was reachable, and it was the failure the no-divergence rule exists to forbid. Writing
  `Define the voidable voidable number x as the entry for "k" in m.` type-checked, ran correctly
  interpreted, and passed `check --native` — then died at gcc, handing the map's inner voidable
  struct to a binding that wanted the outer one. Every layer behaved reasonably on its own: the
  parser recursed on `voidable`, the checker admitted the value because it matched the target's
  inner type, and the compiler passed an already-voidable value straight through because nothing
  could ever need wrapping.

  Fixed where the shape is made rather than where it broke: `VoidableType` now collapses nesting in
  its constructor, so any depth flattens by induction and the invariant the compiler already relied
  on is true by construction. The whole suite passed unchanged, which is its own evidence — every
  consumer was already written as though a voidable could not nest.

- **Concurrency and signals inside a `Pull a book on …` block were invisible to the compiler.**
  Both discovery pre-passes recursed into rabbits, loops, ifs, tries, with-blocks and binds — and
  neither had an arm for the *book* pull, which is a compile-time scope whose body is still
  ordinary program text. So the substrate was never emitted, the rabbit never established its
  context, and a channel declared inside it was refused with *"a channel has to be created inside
  a rabbit"* — while sitting in one.

  A refusal is a legitimate place to ship, but only when it is *true*; this one was simply wrong,
  and the same program interpreted fine. It bit hardest with `matrix`, whose type only exists
  inside `Pull a book on collections.` — so **any** concurrent matrix program hit it.

  Both pre-passes were fixed together rather than only the one that surfaced: a fix that closes
  the case you noticed is not a fix.

- **A plain value now widens into an optional field.** `a new holder { the maybe 5 }` and
  `the maybe void` were rejected when the field was declared `voidable number`, even though
  `becomes` on a local and `return` widened correctly — object and record field slots were the
  one place the language's single implicit coercion did not reach. Construction, field-set, and a
  value handed to a user setter now all accept a plain `T` (or `void`) into a `voidable T`, union,
  or failable field, the way every other assignment does.

  The type checker was the side rejecting these programs; the compiler already carried the
  widening emitter and now uses it at the same sites, so both backends stay in agreement rather
  than the checker allowing what the generated C would then reject.

---

## [0.11.0] — 2026-07-28

0.10.0 made Cufet compile. **0.11.0 makes it usable by someone who is not its author.**

There is now a **playground** at <https://zenofbass.github.io/Cufet/> — the interpreter compiled
to WebAssembly, so a stranger can run Cufet ten seconds after reading about it, with nothing
installed and nothing sent to a server. There is a **VS Code extension**, and `cufet` is an
**installed command** rather than a `dotnet run` incantation. Comments are spelled the way every
other language spells them.

And there is one new type. `bits` is the first full value type since failures, and the first
built entirely in this release: literals, gates, shifts, arithmetic, and conversions.

**Breaking:** the comment syntax changed. See *Changed*.

### Added

**A browser playground**

- **[`playground/`](playground/)** — the interpreter compiled to `browser-wasm` and served as a
  static page. Nothing executes on a server, deliberately: Cufet can spawn subprocesses and touch
  the filesystem, so running strangers' programs server-side would be a real security hole rather
  than a theoretical one. Deployed by GitHub Actions on every push.

- **The editor is Monaco, fed the same TextMate grammar the VS Code extension uses** — through
  `vscode-textmate` over an Oniguruma engine compiled to WebAssembly, rather than hand-ported to
  Monaco's own Monarch format. One grammar, so the editor and the page cannot drift apart. It is
  also what lets a VS Code colour theme work at all, since themes are scope→colour rules that
  Monarch could not consume.

- **Cufet runs in a Web Worker, not on the page.** The interpreter executes synchronously, so on
  the UI thread a non-terminating program would kill the tab outright — and a playground is
  exactly where someone writes an accidental infinite loop. Stop works by terminating the worker,
  which is the only thing that can interrupt synchronous WebAssembly from outside; a cooperative
  flag would need `SharedArrayBuffer` and headers GitHub Pages cannot send.

- Set in **JetBrains Mono** (OFL-1.1) and coloured with **Arctic Candy Darker** by
  [Kenan Salar](https://github.com/KenanSalar) (MIT), both vendored with their licences. The page
  chrome is generated from the same theme file as the editor, so the two cannot disagree.

**`bits` — a bit-pattern type**

- **`0x` hex, `0b` binary, `0o` octal literals**, with `_` grouping digits. A value prints in
  the base it was written in: `State 0o755.` prints `0o755`. No bare-zero octal — `0755` is
  seven hundred and fifty-five, not 493.

- **`bits` is not `number`, and does not convert to it implicitly.** `0xFF = 255` is a type
  error. A bit pattern is not a quantity: `0o755` is three permission triples, and treating it
  as a decimal is a category error. This is what makes the coming bitwise operators well
  behaved — they will live on `bits` and be absent from `number`, so `not 5` cannot be written
  and cannot surprise anyone with `-6`.

- **★ Leading zeros are significant.** The width comes from the digit count, so `0xF` is 4 bits
  and `0x0F` is 8, and they print differently while comparing equal. This is deliberately
  unlike C, Java, Rust, Go and Python, where the two are identical and width belongs to the
  declared type. It is what will let `not 0xFF` be `0x00` rather than `-6` — the type is
  unsigned and never negative.

- **Ceiling is 64 bits**, which covers every C flag set, file mode and address. Wider is
  cryptography or scientific computing — a different domain, and one for a foreign-function
  boundary rather than for distorting this type.

**`bits` — the gates**

- **`and`, `or`, `not` and `xor` on bit patterns.** A 32-bit AND *is* 32 AND gates side by
  side, so the same words serve a `fact` (one bit) and a `bits` value (N of them) — they are
  already the gate names, already English, and already keywords. Only `xor` is new, and it
  works on facts too, where it was missing.

- **Gates refuse `number`.** `5 and 3` and `not 5` are type errors: a quantity has no bits to
  combine. This is what makes `not 0xFF` equal `0x00` — the `-6` that a signed reading of a
  decimal would produce is now unwriteable.

- **A result takes the LEFT operand's base and width**, because in real bit code the left
  operand is the accumulator (`flags or MASK`, `flags and not MASK`). So `0xFF and 0b1010` is
  `0x0A` while `0b1010 and 0xFF` is `0b1010`. Results widen when the value needs more room and
  never truncate — narrow deliberately with an `and`.

- **Precedence is `and` > `xor` > `or`**, mirroring `&` > `^` > `|`.

- **`and`/`or` short-circuit on facts and cannot on bits** — combining two patterns needs both
  patterns. The same word taking a different evaluation strategy by operand type is the same
  deliberate exception matrix arithmetic already makes for `+` and `*`.

- **C's most famous precedence bug is a type error here.** In C, `a & b == c` silently parses
  as `a & (b == c)` and computes nonsense. Cufet has the same precedence, but the mis-parse
  yields `bits and fact` and is refused at compile time. Keeping bit patterns out of `number`
  closes that footgun for free.

**`bits` — shifts**

- **`n shifted left by 3` / `shifted right by 3`.** Shifting is wiring rather than a gate — it
  moves bits instead of combining them — so it is a trailing transform in the `sorted` /
  `trimmed` family, not an operator.

- **The amount is a `number`, not bits**, because it counts *positions*: a quantity, like the
  `3` in `item 3 of s`. It must be whole and non-negative, and both are checked.

- **Left shifts widen, right shifts discard the low bits.** The second is the one place
  something genuinely falls off, and it is the operation rather than a failure of
  representation. Unsigned means there is no arithmetic-versus-logical right shift to choose
  between. Shifting left past the ceiling raises, like a multiply overflow.

- **`left` and `right` are not reserved words.** They are matched by lexeme in this shape only,
  so `the left of node` and `Define left as 7.` keep working. A binary tree should not have to
  surrender its field names to spell one operator.

  (In the emitted C, shifting by at least the operand width is undefined behaviour, so the
  generated code writes the answer out explicitly rather than trusting the hardware.)

**`bits` — arithmetic, ordering, and equality**

- **`+ - * / %` on bit patterns, with `/` as integer division** — the same surface meaning
  something different per operand type, as matrix arithmetic already does. Building a mask
  (`(1 shifted left by n) - 1`) needs subtraction and address work needs addition, so leaving
  arithmetic out would have hobbled the type.

- **Ordering comparisons** (`<`, `>`, `<=`, `>=` and the word forms) work on bits, which are
  unsigned and therefore well ordered.

- **A result with no representation raises**, exactly as division by zero already does:
  `0x00 - 0x1` would be negative, and `0xFFFFFFFFFFFFFFFF + 0x1` does not fit in 64 bits.
  Deliberately not value-level failures — a failure would ride in the type as `bits or failure`
  and force an unwrap after every masking expression, which is why divide-by-zero is not one.

- **Unary minus is refused** on bits, which are unsigned. The message points at `not`.

**`bits` — conversions, completing the type**

- **`n converted to hex` / `to binary` / `to octal`**, and back with `converted to number` or
  `converted to text`. Postfix transforms, consistent with `converted to text` — the crossing
  between a quantity and a pattern is explicit in both directions, since there is no implicit
  conversion.

- **`bits converted to number` can never fail**, because 64 bits always fits a number's 96-bit
  mantissa. So it yields a plain `number`, not the voidable that `text converted to number`
  gives. Total one way, checked the other.

- Going the other way **raises** on a fraction, a negative, or a value past 2⁶⁴ — matching
  arithmetic overflow rather than becoming a voidable, since these are programming errors and a
  voidable would force an unwrap at every crossing.

- **This is what makes a computed value showable in hex** — `total converted to hex` — which a
  literal-only notation never could. It recovers the one advantage the rejected display-only
  design had, while keeping the type.

- `hex`, `binary` and `octal` are not reserved words.

- **[`examples/permissions.cufe`](examples/permissions.cufe)** — a worked Unix-permissions
  program: building a mode with `or`, testing with `and`, clearing with `and not`, positioning
  with a shift, and the `(1 << n) - 1` mask idiom. No divide-and-modulo standing in for a mask
  anywhere, which is exactly what this type was for.

- A full **REFERENCE chapter**, held back deliberately until the type was whole.

**Book vocabulary no longer costs every program a name**

- **Nine words freed:** `at`, `filled`, `guess`, `shuffled`, `rows`, `columns`, `matrix`,
  `random`, `randomly`. They are now recognised by *shape* in the one position that needs
  them, and are ordinary names everywhere else — so `Define rows as 5.` and
  `given (the number rows, the number columns)` both work, in a language where they were
  previously forbidden.

  A reserved word is taken from every program, whether or not it pulls the book that wanted
  it. Reserving `guess` for the chance book meant no program anywhere could have a variable
  called `guess`, and the cost compounded with each book added.

- **`the rows of x` is now resolved by the type of `x`** — a matrix's row count, or a record's
  field. The parser cannot tell them apart, so the decision moved to the type checker, where
  `the key of mapping` already lives. A reader never had the ambiguity. **This deleted the
  `MatrixRows` and `MatrixColumns` AST nodes**, so the change is net less code.

- **Two rules emerged for when a word can go contextual**, and three words fail them:
  - Its shape needs a **mandatory distinguishing token**. `catalogue` and `atlas` have optional
    tails (`a catalogue` alone is valid), so nothing separates them from a variable name.
  - A **statement-initial** word must be conventionally lowercase, since statement keywords are
    capitalised and identifiers must start lowercase. `Seed the chance with 5.` is capitalised,
    so freeing `seed` would have changed how the statement is written.

  Those three stay reserved, deliberately and for stated reasons rather than by omission.

**`cufet` is a command now**

- Packaged as a **.NET global tool**, so `cufet myprogram.cufe` replaces
  `dotnet run --project src\App\Cufet.App.csproj -- myprogram.cufe`. The SDK already keeps
  its tools directory on `PATH`, so installing needs no shell configuration and no
  administrator rights, and it behaves identically on Windows, macOS and Linux.

  This is also what makes the editor extension useful outside this repository. It looks for
  a local build first — so your working copy wins while you are developing the compiler —
  and falls back to `cufet` on `PATH`. Without an installed `cufet`, a `.cufe` file opened
  anywhere else had nothing to run and got no diagnostics at all.

  The assembly is still `Cufet.App`, so the development build path is unchanged.

- **`cufet --help` and `cufet --version`.** Anything that is not a recognised verb is treated
  as a filename, so a mistyped verb used to surface as an unhandled `FileNotFoundException`
  and a stack trace. A missing file now reports the problem and points at `--help`, exiting 2.
  Tolerable behaviour for a `dotnet run` incantation; not for a command people type.

**Editor support — syntax highlighting and error squiggles**

- **`editors/vscode/`** — a Visual Studio Code extension. A TextMate grammar for
  highlighting, and a checker that turns the front end's own diagnostics into squiggles.
  No build step and no dependencies: link the directory into your extensions folder and
  reload. Comments nest in the editor the way they nest in the lexer, string interpolation
  highlights the expression inside the hole, and `wordPattern` knows that hyphens are
  identifier characters, so double-clicking `add-edge` selects all of it.

  The grammar assigns *scopes* and never colours — so it fits whatever theme you already
  use. Articles and prepositions (`a`, `the`, `of`, `to`, `as`, `with`, …) are deliberately
  given no scope at all: Cufet reads like English because it is full of them, and colouring
  them would turn every line into a wall of keyword.

  One consequence worth having: because hyphens bind into identifiers, `count - 1` shows
  the `-` as an operator while `count-1` stays one flat name. The sharp edge is now visible
  in the editor instead of waiting to surprise you.

- **`cufet check [--json] [--native] <file>`** — lexes, parses and type-checks a program
  and reports what fails, *without running it*. `Interpret` finds the same errors, but
  finding them by running the program is not an option for an editor when the program reads
  input, writes files, or takes a minute.

  Reports as `path:line: error: message` (which the extension's `$cufet` problem matcher
  parses), or as one JSON object per diagnostic under `--json`, which keeps the multi-line
  body of a type error intact instead of flattening it. `--native` additionally runs code
  generation and reports what the native compiler refuses — as **warnings**, not errors,
  because those programs interpret fine. Exits 0 clean, 1 for problems, 2 if the file could
  not be read.

### Changed

- **Comments are now `//` and `/* ... */`, replacing `[[ ... ]]`.** Two things drove this. The
  first is that Cufet had **no line comment at all** — every passing note, every temporarily
  disabled line, paid for an opening *and* a closing delimiter. That is friction on every line of
  every program, and it is what a newcomer feels first. The second is conventionality: an
  unfamiliar delimiter is a cost paid by every reader arriving from any other language, and it
  buys nothing that `//` does not. Every character of this is a straight swap; no program changes
  meaning, only its spelling.

  **Block comments still nest.** An inner `/*` opens a nested comment, and the outer one ends only
  at the `*/` that closes it. C's do not, which is why commenting out a block that already
  contains a comment silently ends the comment early, lets the rest be read as code, and reports
  the leftover `*/` as an unexpected character. Rust, Swift and D all nest theirs for exactly this
  reason, so this is the familiar surface with the better semantics rather than a departure from
  it. (Nesting itself was added earlier in this unreleased cycle, under the old delimiters.)

  **Division is untouched.** `/` is a single-character token with no lookahead of its own, so a
  source `//` could only ever have parsed as division by a unary slash — not an expression Cufet
  has. Nothing valid was taken away, and `6 / 2` and `6/2` both still divide.

  `[` and `]` are now unused in Cufet's surface syntax entirely, and are free for future use.

  Updated with it: the lexer, GRAMMAR and REFERENCE, every example and soundness fixture, the
  VS Code extension's grammar and language configuration, and the playground's editor config.

- **Documentation restructured so each file answers one question.** ROADMAP.md had grown to 1417
  lines and was answering five at once — it listed number-base literals under *Planned* the day
  after they shipped, and described a REPL as worth considering against a settled decision that a
  playground beats one. It drifted precisely *because* it restated the other documents.

  **[DESIGN.md](DESIGN.md) is new** and holds the "why" — what Cufet is for, and the decisions
  that follow from it. ROADMAP.md is now 206 lines and records **only what is not yet done**; when
  something ships it is deleted from the roadmap, because its record is the changelog entry and
  its rationale is DESIGN.md. Implementation invariants and known limitations moved to
  CONTRIBUTING.md. Nothing restates anything else, which is the only thing that actually prevents
  drift.

- **The soundness probes moved** from `examples/` to
  [`tests/fixtures/soundness/`](tests/fixtures/soundness/). Six of the nine are *supposed* to fail
  type-checking, so they read as broken demos while they sat beside the showcase programs. They
  are now enforced by a test that enumerates the directory, so a new probe is a drop-in.

---

### Fixed

- **Equality on bits compared base and width as well as value**, so `0xFF = 0x00FF` came back
  `false` when the two are the same pattern written two ways. Both backends were affected; the
  runtime representation is a value struct on each side and default structural equality took
  all three fields. Equality and ordering now compare the value alone — the one place width
  must not be load-bearing. Introduced by the literals slice, which never compared across
  widths.

- **The interpreter's entire subprocess test surface only ever ran on Windows.** Fifteen tests
  hard-coded `cmd` with `/C`, plus `findstr`, so on Linux every one of them turned into a launch
  failure — `run`, argument passing, exit codes, stderr capture and subprocess pipes had no
  passing coverage on the platform Cufet's compiler actually targets. The programs are now chosen
  per-OS in one place, `tests/Interpreter.Tests/PlatformCommands.cs`, so the next test to launch
  something cannot quietly reintroduce it.

  Found by the first CI run ever executed on this repository, which is the point of having one.
  One test needed real thought rather than translation: the case asserting each argument arrives
  as a *separate* OS argument would have passed while testing nothing under `sh -c`, since
  `sh -c echo passed-arg` makes the argument into `$0` and prints an empty line.

- **A GitHub Actions workflow** now tests the front end and deploys the playground. Its first
  version carried a `paths:` filter that omitted `tests/`, so the commit fixing the tests it
  gates on did not trigger it — a filter that skips a run is indistinguishable from one that
  works, so the filter is gone.

## [0.10.0] — 2026-07-25

The **native compiler** era. Cufet now has two backends: the tree-walking interpreter
(the reference implementation) and a compiler that emits C and invokes gcc to produce a
real executable. Every committed grammar feature compiles — verified by a mechanical
sweep for AST nodes with no codegen references.

The interpreter is the **oracle**: the compiler's test suite compiles each program, runs
the binary, and asserts its output equals the interpreter's. Where the two disagreed, one
of them was wrong — that discipline surfaced and closed roughly a dozen latent bugs in the
language itself, several of which predated the compiler entirely.

### Added

**Comments — `[[ ... ]]`**
- `[[` opens a comment; the first `]]` closes it. Everything between is stripped by
  the lexer before tokenisation — dots, keywords, newlines, and any Cufet syntax inside
  are all ignored. Single delimiters cover both single-line and multi-line comments.
- Non-nesting in this release: the first `]]` closed the comment regardless of any
  `[[` inside. (Changed immediately after — see Unreleased.)
- Unterminated comment (`[[` with no `]]` before EOF) is a `LexerException` naming
  the opening line.
- `[` and `]` are otherwise completely unused in Cufet's surface syntax — zero
  collision risk with any existing construct.

**The native compiler — `build` and `emit-c`**
- `build <file.cufe>` compiles to a native binary through a C intermediate.
  `emit-c <file.cufe> [out.c]` emits the C without invoking gcc, for cross-toolchain
  builds and inspection.
- The entire front end (Lexer → Parser → TypeChecker) is **shared**. A program that
  type-checks does so identically in both backends; `Cufet.Compiler` consumes the same AST.
- Requires gcc on `PATH`. No external libraries and no compiler flags beyond `-pthread`
  and `-lm` — the emitted C is self-contained.

**Exact decimal arithmetic in compiled code**
- `number` compiles to `CufetDec`, a self-contained software decimal that is
  bit-identical to .NET's `System.Decimal` — including round-half-to-even, the 28-digit
  scale, and overflow behavior. Chosen over `double` (which would have made compiled
  arithmetic silently disagree with interpreted) and over libmpdec (IEEE-754 decimal, a
  different format, plus a link dependency).
- Math-book transcendentals (`square root`, `log`, `power`) are double-backed, matching
  the interpreter, which is itself double-backed. Documented consequence: fractional
  `power` can differ in the last digit across platforms, because .NET's `Math.Pow` *is*
  the platform libm. This is genuinely platform-owned, not a divergence to fix.

**The full data model, compiled**
- Text (immutable, arena-allocated), series (per-element-type), maps, records
  (structural value structs), objects (nominal value structs, with embedding, getters,
  setters, `unto` methods, named constructors, and value equality), `voidable T`,
  `T or failure`, matrices, and catalogue/atlas (union types).
- Records and objects are C value structs, so Cufet's value semantics fall out of C's
  assignment-copies rule rather than needing runtime support.
- Unions compile to a tagged struct; `is a <case>` is a genuine runtime tag check. This
  is the one place the language needs runtime type identity — interfaces do not (below).

**Memory: arenas and escape analysis**
- `Pull a rabbit.` opens an arena; `Done.` pops it. Allocation is a bump-tracked pointer
  list, thread-local so concurrent tasks never contend.
- Values that would outlive their region are deep-copied into the destination's arena at
  the point of storage. The type checker computes the destination depth and annotates the
  store; the compiler performs the copy. Closure captures that escape are copied the same
  way, including the environment record itself.
- A region is released on every way out of it, not just `Done.` — `return`, `Stop`, `Skip`,
  `Suppress`, a failure unwind, an exception. Returning a value out of a region hands that
  region's memory to the caller's instead of freeing it, so the value stays valid and is
  reclaimed one level out; a long-running loop whose body opens a region stays flat.

**Concurrency — true parallelism**
- Tasks compile to pthreads with a structured join: a rabbit joins every task it spawned
  before popping its arena, so a task provably cannot outlive its region.
- Channels are mutex/condvar queues. Values crossing a thread boundary are deep-copied
  through a heap bridge, so the message owns its memory in transit and neither thread's
  arena is entangled with the other's.
- Streaming pipes run every stage as a concurrent thread. Interrupts use `sigaction` with
  a minimal async-signal-safe handler plus cooperative checkpoints, so a thread blocked in
  a channel receive can now actually be woken — something the cooperative interpreter
  cannot do.
- Compiled concurrent programs are validated against **order-independent invariants**
  rather than interleaving order, and under ThreadSanitizer.
- **A task can capture any type**, not just numbers, facts, and channels. A captured series,
  map, object, record, text, or catalogue is deep-copied across the thread boundary the same
  way a channel message is, so the two threads' memory stays completely separate. A task that
  would *change* anything it captured is refused, and pointed at channels instead: the change
  could not be seen outside, and two tasks changing one captured value is a genuine race that
  only shows up once the tasks really run in parallel. This covers plain numbers too, and
  there it closed a real disagreement — a task doing `tally becomes tally + 5` produced 5
  interpreted and 0 compiled, because the interpreter hands task bodies the live enclosing
  variable while a compiled task writes its own copy. Nothing about a value being small
  makes the write meaningful; the task still cannot show it to anyone.

**I/O, exceptions, and the standard library**
- Files, streams and `With ... open`, stdin/stdout, subprocess `run`, and shell pipes —
  compiled to POSIX `fork`/`execvp`/`pipe`/`waitpid` with no shell involved.
- `In case of exception` / `Suppress` compile to a setjmp/longjmp stack over
  software-detected faults (Cufet's numbers are software decimals, so division by zero is a
  check, not a hardware trap). Cleanup registries ensure files, channels, and arenas are
  released across a nonlocal jump.
- Books (`math`, `collections`, `chance`, matrix, `sorted`) are compile-time resolved
  builtins — no dynamic linking.

**Closures, interfaces, operators, destructors**
- Closures compile to a `{function pointer, environment}` pair; captures are stored by
  value, which reproduces the interpreter's capture semantics without extra machinery.
- **Interfaces are monomorphized.** Cufet's interface polymorphism exists only at the
  function-parameter position and the argument is always a concrete conformer, so the
  compiler emits one specialization per conformer passed. No vtables, no type tags.
- Operator overloading (`+ - * /` on a single object type) resolves at compile time to a
  direct call — exact nominal match with one candidate, so there is nothing to rank.
- Destructors (`unmaking`) fire at block scope exit, LIFO, on every exit path including
  `return`, `Stop`, failure propagation, and exception unwind.

### Changed

- **Record and object fields now print in sorted order** in both backends. Previously the
  construction order was observable, so two structurally-equal records could print
  differently.
- **Container insertion now copies value types.** Adding a record or object to a series or
  map copies it, matching `Define` and argument binding. Binding is binding — storing a
  value into a container is the same operation as naming it.
- **Argument binding copies records and objects**, closing the one site among four that
  shared them and let a mutated parameter leak back to the caller.
- **Remove-by-value and `contains` use value equality**, not reference identity, so a
  record equal to the stored one is found.
- **Directory listings are sorted** (ordinal) in both backends; raw OS order is
  filesystem-dependent.
- **I/O error messages are deterministic templates** rather than the host exception's
  text, so both backends report identically.
- **`is a` is element-aware for containers.** A `series of text` no longer matches
  `is a series of number`. The interpreter survived that (it is dynamically typed and
  simply read the element back); a compiler could not, since it would reinterpret the
  payload at the annotated type. The check is now answered from the declared type in both
  backends, which also decides the empty-container case that no runtime value can answer.
- Matrix values now print as `matrix((1, 2), (3, 4))`; the interpreter previously leaked
  the host type name.

### Fixed

Latent language and soundness bugs surfaced by holding the two backends against each other:

- **Series indexing was unchecked in compiled code** — out-of-bounds was undefined
  behavior. Now a bounds check that raises a catchable exception with the interpreter's
  exact message.
- **Exception messages could dangle.** A message allocated in a nested arena was freed by
  the catch before the handler read it, and the freed block was promptly reused — a
  message would read back as whatever string was concatenated next. Messages are now
  allocated in the target handler's own arena.
- **Values escaping a region were a use-after-free on the normal path.** The outward-store
  invariant covered series, maps, and objects but not `text`, and any value-typed wrapper
  (a record, a voidable) laundered even a covered type past the check. The test is now
  structural over the whole shape.
- **Jumping out of a region corrupted the arena stack.** A `return`, `Stop`, `Skip`,
  `Suppress`, or failure unwind that left a `Pull a rabbit` skipped the rabbit's `Done.`,
  so the arena depth was left one level too high — climbing by one on every such exit until,
  after 64 of them, the next region pushed past the end of the arena table. Any program with
  a helper that returns from inside a region and is called in a loop crashed. Regions now
  unwind alongside files, handlers, and destructors on every exit path: a jump that carries
  nothing out releases them, one carrying a failure message moves the message outward first,
  and a `return` hands its region's memory to the caller's — never copying, because a
  returned value may be the caller's own and the two backends must keep sharing it.
- **A container living outside a region, grown from inside one, was freed with that region.**
  Adding to a `series of number` or a `map from number to number` declared outside a
  `Pull a rabbit` moved the container's own storage into the rabbit, so it dangled after
  `Done.` — even though every element was a plain value. `examples/parallelsum.cufe` was a
  use-after-free. Two things hang off the escape annotation, copying the stored value and
  relocating the container's growth, and it was gated on the condition only the first needs.
- **A map key built inside a region and stored in a longer-lived map was freed with that
  region.** A map is the only place in the language that stores two values at once, and only
  the value half was ever checked for escape.
- **Some refusal messages talked about the compiler's internals instead of your program.**
  Trying to await one task's result from inside another reported `'TaskHandleType' is not yet
  supported by the compiler (slice 5B: records + objects + text)` — an internal class name, an
  internal slice number, and a list of unrelated features. It now says that a task cannot await
  another task's result yet, names the task you referred to, and tells you to await it from the
  rabbit body or pass the value through a channel. The same pass removed every other mention of
  internal slice numbers from user-facing errors, and gave every type a real Cufet name, so no
  message prints an implementation class name any more — the few remaining "this construct
  isn't handled" fallbacks now name the construct in the language's own words.
- **`range` could not be used as a value.** `range 1 to 5` worked as the thing a `For each`
  loops over, but not as something you could name — `Define halves as range 1 to 2 counting by
  0.5.`, an example in the reference itself, would not compile. It now produces a series like
  any other expression, using the same stepping and direction logic as the loop form so the two
  cannot drift apart. A `counting by` step that computes to zero or a negative number now also
  raises the same error compiled as interpreted, instead of looping forever; a literal zero was
  already caught before the program ran.
- **An empty collection did not know what it was meant to hold.** `is a` recurses into a
  collection's elements to answer precisely, but an empty one has no elements — so an empty
  `series of text` answered yes to `is a series of number` as readily as to its own type.
  That was only reachable through a catalogue, where the declared type cannot narrow it, and
  it made compiled and interpreted programs take different branches, so narrowing a catalogue
  that could hold two different collection types was refused outright. Collections now
  remember the element type they were created with, so an empty one answers from that. Both
  backends agree, and the refusal is gone.
- **Pipe stages were never type-checked against each other — or, in a consumer's case,
  at all.** `for each n from the input:` gives the iterator no type, so a stage's input
  type can only come from the stage before it, and nothing was carrying it across. A
  producer emitting numbers into a stage that treated them as text got past the front end
  entirely and failed much later and much worse: interpreted, as a raw .NET exception with
  a stack trace; compiled, as a `gcc` error about generated C. The type checker now walks
  each pipe in order, carries every stage's output type into the next as its input, and
  checks the body against it — so the mismatch is an ordinary Cufet type error on the
  offending line. Reusing one stage function at two different element types is now also a
  front-end error rather than a compiler-only refusal.
- **Ctrl-C was ignored while a task was running.** Only the main thread and streaming-pipe
  stages had somewhere to unwind to, so an interrupt inside a `Have rabbit start a task` body
  did nothing at all — and the main thread, waiting at the rabbit's `Done.`, never looked at the
  interrupt flag either. The program simply kept going. Tasks now unwind like any other thread:
  the task stops where it is, its destructors run and its files close, the rabbit reaps it at the
  join, and the program exits. A task blocked waiting on a channel used to treat the interrupt as
  "the channel closed" and carry on with a value it never received; it now stops. Awaiting a task
  that was interrupted no longer reads from nothing. Streaming-pipe stages, which could already
  be interrupted, now run their destructors and close their files on the way out too.
- **Closures that only wrote to a captured variable never captured it**, and a nested
  lambda parameter could mask a same-named outer variable across the whole enclosing body.
  Both emitted an undeclared C variable.
- **If/While conditions with preparatory steps were never flushed**, miscompiling any
  condition that needed one (environment variable, map lookup, channel delivery).
- **Guard-return narrowing** (`If n is void, return failure.` then using `n` as a plain
  `T`) now works in both backends — the natural error-handling idiom the docs lean on.
- Failure propagation inferred the wrong result type for non-numeric inners; task result
  types fell back to `number` for reference results.

### Notes

- Some compiler tests are Linux-only: POSIX features (concurrency, subprocess, signals)
  cannot be built by mingw, and the sanitizer runs are Linux-only.
- Compiled concurrent programs are genuinely parallel, so the interpreter's deterministic
  interleaving is not a specification. Concurrency tests assert order-independent
  invariants.

---

## [0.9.0] — 2026-07-03

The complete concurrency core is built, sound, and hardened by five tests programs.
All test findings are resolved. The interpreter-era language is now
complete — native backend is the next era.

### Added

**★ Cooperative concurrency core ★ — the headline**
- **Scheduler (`CufetScheduler`)** — cooperative, C# async/await, custom
  `SynchronizationContext`. All continuations routed to a single per-thread FIFO
  queue; no interpreter-internal data races. Sequential programs run unchanged.
- **Structured tasks** — `Have rabbit start a task [as <name>]: … Done.` Spawn +
  join-at-Done. A task cannot outlive its spawning rabbit. Sound by construction:
  inherits the region model's outward-only invariant — no new soundness machinery.
  `TaskLocalSeriesCannotEscapeToOuterScope` confirms the static error fires from
  inside a task body.
- **Channels** — `a channel of T`; `Send <value> through <channel>.`; `the delivery
  from <channel>` (→ `voidable T`; void on closed-empty channel); `Close <channel>.`
  (idempotent; send-after-close is a runtime error). **Values deep-copied at send**
  — the cross-task aliasing guarantee. Type-checked: wrong type to Send, non-channel
  to Send/delivery/Close are all static errors.
- **Task results** — named tasks may `return <value>.` inside their body; `the
  awaited result of <name>` collects the result. Suspends if the task is running;
  immediate if already done; cached on double-await. Fallible task results
  (`return a failure …`) infer `T or failure`; unhandled fallible result is a static
  error. Void-returning tasks cannot be awaited for a result (static error).
- **`Yield.` + SIGINT-at-yield** — `Yield.` is a cooperative scheduler yield and
  interrupt checkpoint. The scheduler drain loop checks `_interruptRequested` at
  each dequeue; blocked receives and awaits also wake on interrupt. Programs that
  yield naturally are interruptible without polling. Renames `an interrupt has been
  requested` → `an interrupt is requested` (old form is a parse error).

**Streaming task pipes**
- **Task pipes** — `producer | consumer.` pipelines two or more `void`-returning
  functions. `output <value>.` (contextual keyword inside a stage) emits to the
  implicit output channel; `for each <name> from the input, repeat:` reads from it.
  Producer runs to completion, then consumer drains. Stage references may be
  variables holding function values.
- **Subprocess pipe enhancement** — `run "a" | run "b"` in expression position
  returns a result record (`output`, `errors`, `exit-code`). Exit code is the
  rightmost non-zero stage. Launch failure is a catchable Cufet failure; non-zero
  exit is observable but not auto-fatal.

**Map key value-type constraint**
- Map keys must be value types (`text`, `number`, or `fact`). Declaring a map with
  a reference-type key (object, series, map) is a static `TypeException` with an
  educational message explaining why reference identity breaks under deep-copy
  (lookups silently always-miss; the map behaves empty, computing wrong answers with
  no error). Runtime guard in `ExecuteMapSet` as a safety net for any dynamic path
  that reaches runtime. Root cause of the Dijkstra silent-wrong-answer bug.

**Trap-cleanup sweep**
- `true` / `false` — fact literals; `return true.` and `if flag is true` now work
  without defining `true`/`false` as variables.
- Ordinals (`first`, `second`, …, `tenth`, `last`) are now contextual identifiers —
  recognized as positional accessors only in `the <ordinal> of <series>` shape;
  valid as variable names, parameter names, and field names everywhere else.
- Negated word-comparisons — `is not greater than`, `is not less than`,
  `is not N or more`, `is not N or less` are valid in both condition and expression
  position.
- Comparison unification — symbol forms (`= < > <= >=`) and word forms (`is`, `is
  greater than`, etc.) work in **both** condition and expression position. The
  positional restriction is retired; word forms remain idiomatic in conditions.
- `=` in a stand-alone statement now produces an educational error ("did you mean
  `becomes`?") rather than a confusing parse failure.
- Top-level data referenced inside a top-level function now produces an educational
  error naming the variable and explaining the scoping rule.

**Series literal in expression position**
- `a series of number with (1, 2, 3)` is now valid in expression position (as a
  function argument, in `but void is (…)`, etc.). Found during the channel-deepcopy
  testing; wired into `ParseCorePrimary`.

### Changed

- Test count: 1187 interpreter + 140 lexer (1327 total). New tests live in dedicated
  files: `SchedulerTests`, `TaskSpawnTests`, `ChannelTests`, `TaskResultTests`,
  `YieldTests`, `PipeTests`, `ComparisonUnificationTests`, `BooleanLiteralTests`,
  `OrdinalIdentifierTests`, `NegatedWordComparisonTests`, `EqualSignStatementErrorTests`,
  `TopLevelDataScopeErrorTests`.
- ROADMAP.md: concurrency + pipes moved from Planned to What's built; concurrency arc
  narrative added to Design decisions; forward roadmap updated (native backend is next);
  Known minor issues concurrency/SIGINT sections updated; test count and table updated.
- README.md / REFERENCE.md: bumped to 0.9.0.
- `examples/dijkstra.cufe`: complete rewrite using text node names as map keys
  (object-as-key design incompatible with map key value-type constraint; procedural
  rewrite also cleaner). Verifies expected distances and prints `PASS`.

### Test campaign (five test, every finding resolved)

| Program | Finding | Resolution |
|---|---|---|
| `parallelsum` | Top-level function can't read top-level `Define` data | Educational runtime error explaining the scoping rule |
| `channel-deepcopy` | Deep-copy safety validated under nested structures | ✅ guarantee earned; also found series-literal-in-expression gap → wired into `ParseCorePrimary` |
| `subprocess-pipes` | stderr silently discarded; exit codes silently ignored | `errors` field added to result record (F2); result record with `exit-code` (F1) = command substitution |
| `work-queue` | Coordination correctness validated; fan-out distribution imbalanced under cooperative scheduler | ✅ correctness confirmed; fan-out imbalance is a named interpreter-era characteristic → "verify at native" note |
| `dijkstra` | Silent object-as-map-key miss (reference identity lost under deep-copy) | Map keys constrained to value types (Option C — educational type error) |

The recurring signal: every finding was a gap, ergonomic wart, or interpreter-era
characteristic — never a core soundness or correctness bug. The foundations held;
the programs sanded edges.

---

## [0.8.0] — 2026-06-28

The deferred-features ledger is cleared and the region model is sound. This
release closes all six items from the gap-list, completes the
three-hole adversarial soundness arc, and ships matrix arithmetic as the first
exercise of operator overloading.

### Added

**Eagér type resolution — ObjectType placeholder leak killed**
- Parser-created `ObjectType` shells (used in annotations) no longer leak into
  type inference. `ResolveParamType` is now fully recursive (recurses into
  `SeriesType`, `VoidableType`, `FailureType`, `MapType`, `FunctionType`,
  `RecordType`, etc.). `Pass2ResolveTypes` eagerly resolves all type references
  inside `_objectDefs` after hoisting, so no placeholder survives to inference
  time. `InferType` wraps its result through `ResolveParamType` as a final
  backstop. Book-introduced types (`matrix` after `Pull collections`) resolved
  correctly in all positions.

**`is more than` educational error**
- Using `is more than` in a condition (instead of `is greater than`) now
  produces a targeted compile-time diagnostic explaining the correct keyword,
  rather than a confusing parse error.

**Series operations unified to IExpression**
- All five series AST nodes (`SeriesLength`, `SeriesAdd`, `SeriesRemoveAt`,
  `SeriesRemoveValue`, `SeriesSet`) now hold `IExpression Series` instead of
  `string SeriesName`. Series operations now work directly on possessive
  expressions (`one's cards`), eliminating the alias-preamble pattern inside
  object methods. Parser uses `ParseCorePrimary()` for the series target
  (not `ParsePostfix()`, which would have greedily consumed postfix operators).
  TypeChecker throws a static error for `Add x to (a+b)` and similar
  non-series targets.

**Parser keyword-allowlist (Approach C)**
- `IsNamedAccessPattern()` lookahead exclusion list replaced by a principled
  set-based check: any token that is not `Identifier`, `Category`, or `Key` is
  excluded as a field name. Two narrow exceptions: `Category` (for `the category
  of the failure`) and `Key` (for `the key of mapping`). No new keyword can ever
  mis-fire as a field name — the n-queens `the series of number board` class of
  bug is dead. Approach B (explicit type-annotation contexts, the proper
  architectural fix) is tracked as deferred pre-native parser-hardening.

**`chance` book — effectful randomness**
- `Pull a book on chance.` enables: `a random number from low to high` (whole
  numbers; `low > high` runtime error), `a random item from series` (→ `voidable T`;
  empty → void), `randomly shuffled series` (non-mutating Fisher-Yates copy),
  `a random guess` (50/50 fact). `Seed the chance with N.` reseeds for
  reproducibility. Per-interpreter RNG for free test isolation. `chance` is
  intentionally separate from `math` — effectful vs. pure is a named structural
  distinction. Dedicated AST nodes (not book-function dispatch) give access to
  the interpreter's `_rng` instance field.

**`Pull … Done.` unification**
- Books, rabbits, and other acquired resources share a unified `Pull <thing>:
  … Done.` scoped-block syntax. `Pull a book on X.` (dot) remains the
  scope-local form. Plural form: `Pull books on X, Y, and Z.` pulls multiple
  books in one statement. `Pull` scope is hoisted correctly for nested
  declarations.

**Value-vs-reference `Define` semantics documented**
- The principled split (records/objects: value-typed, copy on assignment;
  series/maps: reference-typed, share the live instance) is now explicitly
  documented in GRAMMAR.md and REFERENCE.md, including the `Define alias as
  original.` vs. `Define copy as original.` disambiguation.

**Region-model soundness — three-hole adversarial arc**
- Three holes in the outward-only invariant were found adversarially and closed.
  See DESIGN.md for the full narrative. In brief:
  - *Hole #1 (function-call depth laundering):* `ReturnDepthSignature` on
    `FunctionType`, computed by `ComputeReturnDepthSignature` at `CheckBind`
    time; `ValueDepthOf` reads the signature and takes `max(subset)` of
    argument depths.
  - *Hole #3 (methods/getters residue of #1):* same machinery extended to
    method/getter bodies with receiver as a depth source (`ReceiverDepthIndex =
    -1`); `_possessiveDepthCache` / `_rnaDepthCache` populated from
    `InferPossessiveAccess` / `InferRecordNamedAccess`.
  - *Hole #2 (capture-store laundering):* `TypeInfo.IsParameter` flag set at
    all parameter-registration sites; nested-scope import upgrades any captured
    `IsParameter && IsReferenceType` to `RabbitDepth = int.MaxValue`; existing
    `CheckRegionStore` rejects the outward store with no new logic.
  - No known remaining pre-native soundness gaps.

**Matrix arithmetic (+, -, *) — collections-book operator overloads**
- `m + n` — element-wise addition; identical dimensions required; `matrix or
  failure` (category `"dimension-mismatch"`).
- `m - n` — element-wise subtraction; identical dimensions required; `matrix or
  failure`.
- `m * n` — matrix product (standard triple-loop dot product); requires
  `left.columns == right.rows`; yields `m×p` from `m×n * n×p`; `matrix or
  failure`.
- All three are strictly fallible: must be inside `Try to:` or `but on failure
  <default>`, else a static `TypeException`. Scope-locality enforced by type:
  `MatrixType` only in scope inside `Pull a book on collections.`.
- `matrix / matrix` falls through to "arithmetic requires numbers" type error
  (matrix inversion deferred; will be a named `collections` function if added).

### Changed

- Test count: 1003 interpreter + 140 lexer (1143 total).
- ROADMAP.md: operator overloading and matrix arithmetic moved from Planned to
  What's built; chance and Pull mechanism documented in What's built; soundness
  arc added to Design decisions; forward roadmap updated.
- README.md / REFERENCE.md: bumped to 0.8.0.
- GRAMMAR.md §5: matrix arithmetic section added (operators, dimension rules,
  fallibility, strict-fallible examples, not-defined cases).

---

## [0.7.0] — 2026-06-23

### Added

- **Operator overloading** — user-defined `+`, `-`, `*`, etc. for object types;
  fallible overloads (`T or failure`) supported; strict-fallible rule enforced
  at call sites.
- **Books / `Pull` mechanism** — `Pull a book on <name>.` scope-local import;
  `BookType` with possessive member access; bundled books registered statically.
- **`math` book** — `absolute value`, `square root`, `floor`, `ceiling`, `round`,
  `log`, `power`, `sine`, `cosine`, `tangent`, `pi`, `e`; partial functions return
  `voidable number`.
- **`collections` book — `matrix` type** — literal `a matrix with ((r1), (r2), …)`;
  `the item at (row, column) of m` (1-based); `a matrix with N by M`; `the rows of` /
  `the columns of`; type annotation `the matrix m`. Op-set (arithmetic) deferred to
  0.8.0.
- **Possessive chaining and multi-word book member names** — `math's absolute value
  of x`; `of (e1, e2)` multi-arg form.
- **Parser restructure** — `ParseCorePrimary` / `ParsePostfix` / `ParseNegation`
  split to fix postfix-eating in recursive target-of parses.
- **`_typeScopes` parallel scope chain** — enables type-introducing books;
  `RegisterScopedType` / `TryLookupScopedType` helpers; always in sync with `_scopes`.
- **Destructors / RAII** — `Bind unmaking a <type> to <name>:` fires in LIFO order
  at scope exit; infallible; one destructor per type.
- **Named constructors** — `Bind making a <type> to <name>[, given (…)]:` and
  fallible form `Bind making a <type> or failure to <name>:`; called via `Cast`.
- **Getters and setters** — `Get <name> as <type>:` / `Set <name> given (…):`
  inside object bodies; uniform access property; `unto` forms for outside-body
  declaration; setter self-write bypass.
- Numerous example programs (n-queens, Tower of Hanoi, Dijkstra, card dealing,
  word frequency, arbtree).

---

## [0.6.0] — 2026-06-21

### Added

- **Union types and narrowing** — `(A or B or C)` closed unions; `is a <type>` /
  `is not a <type>` runtime type tests; in-branch narrowing; narrowing by elimination
  in `Otherwise`; open unions (`catalogue`, `atlas`); `catalogue` (heterogeneous
  series) and `atlas` (heterogeneous map).
- **Object interfaces / polymorphism** — `Define <name> as an interface for {…}`;
  explicit conformance (`and <interface>` on object definition); static conformance
  check; interface type as parameter type.
- **`unto` methods** — methods declared outside the object body; hoisted /
  order-independent; identical in every way to nested methods.
- **String interpolation** — `{expr}` inside string literals; lexer-side split;
  desugars to `joined to` / `converted to text` chain; `\{`/`\}` for literal braces.
- **`or pass the failure off`** — failure propagation operator; propagates to the
  caller (which must itself return a failable type).
- **`In case of exception`** — runtime exception handler; `the exception` binding;
  `Suppress.` to swallow; default re-raise.
- **Embedding / composition** — `and as a <type>` on object definition; transitive
  member promotion; flat construction; embed-handle escape hatch; collision → error.
- **Cooperative SIGINT** — `an interrupt has been requested` / `Acknowledge the
  interrupt.`; per-signal flag; not preemptive (preemption deferred to concurrency arc).
- **Directory traversal** — `the contents of the directory path`; `the path "x"
  exists` / `is a directory` / `is a file`.
- **Environment variables** — `the environment variable "NAME"` → `voidable text`.
- **`permanently` constants** — trailing adverb; shallow; static enforcement only.

---

## [0.5.0] — 2026-06-20

### Added

- **File I/O** — `read all from the file <path>`, `read all lines from the file
  <path>`, `write … to the file <path>.`, `append … to the file <path>.`; failure
  categories `"not-found"`, `"permission-denied"`, `"disk-error"`.
- **File streams** — `With the file <path> open for reading/writing as <name>:
  … Done.`; RAII scoped; direction statically enforced.
- **Process execution** — `run <program>` / `run <program> with arguments (…)`;
  result record (`output`, `errors`, `exit-code`); `result or failure`; no shell
  injection.
- **Standard input** — `read a line from the input` (→ `voidable text`), `read all
  from the input`, `read all lines from the input`; `the input` pre-defined.
- **Voidable type + narrowing** — `void`, `voidable T`; `is void` / `is not void`;
  variable-level narrowing; `but void is <default>` inline fallback; `VoidableType`
  in the type system.
- **`failure T` and `Try to:` / `In case of failure:`** — failable values; inline
  `but on failure <default>`; block form with both handlers; `a failure "message" of
  category "tag"` literal; strict-fallible enforcement.
- **Text → number conversion** — `converted to number` → `voidable number`; always
  failable by type.
- **Text operations** — `split by`, `contains`, `the position of … in …` (→
  `voidable number`), substring (`the characters from N to M of`, `the first/last N
  characters of`, `to the end of`); `replace <old> with <new> in <text>`;
  `in uppercase` / `in lowercase`; `trimmed`.
- **String escape sequences** — `\n` `\t` `\r` `\\` `\"` `\{` `\}`.
- **Range stepping** — `range 1 to 10 counting by 2`; positive magnitude; direction
  from start/end; endpoint included only if step lands exactly.
- **`Define a shadow x`** — deliberate shadowing opt-in.
- **Closures and lambdas** — `a function given (…): … Done` anonymous function
  expressions; inferred return type; same capture rule as closures.
- **Records** — `a record with (…)`; positional + named fields; structural typing;
  value semantics (deep copy); record shapes in annotations; empty `series of records
  like (…)`.

---

## [Pre-0.5.0]

The core language was established in versions 0.1.0–0.4.x:
- **0.1.x** — `Define`/`becomes`, arithmetic, `State`, conditionals (`If`/`Otherwise`),
  `While`, `For each`, `Stop.`/`Skip.`; lexical scope; `Done.`-bounded blocks.
- **0.2.x** — Series (homogeneous, mutable, `Add`/`Remove`, ordinal access, `sorted`);
  maps (`a map from T to V`, `the entry for K in M`, `has a key for`); ranges.
- **0.3.x** — Functions (`Bind`); recursion + depth limit; first-class function values;
  function types in annotations.
- **0.4.x** — Objects (nominal typing, `Define object`, `a new T {…}`, methods, `Cast`,
  possessive access, value semantics); `joined to` / `converted to text` / `the length
  of`; maps fully rounded out; voidable-valued maps.
