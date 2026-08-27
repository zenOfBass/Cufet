# Changelog

All notable changes to Cufet are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning: feature arcs bump the minor version; 1.0.0 marks language stability.

---

## [Unreleased]

### Added

- **An axiom that says nothing about its result is now SOURCE** — pasted once, above every wrapper,
  so what it declares is in scope for the axioms that follow:

  ```
  Pull a book on the c-language.
      Define c-language helpers as [static int twice(int x) { return x * 2; }].
      Define c-language number four as [twice(2)].
      State cast four.                    ← 4
  Done.
  ```

  ★ That form was always legal to write and had no meaning — the docs said it "may be written but
  not run" and left it there. ⚠ It was worse than inert: the resultless axiom was dropped while an
  axiom CALLING what it declared was emitted anyway, so the program checked clean and produced C
  naming an undefined function, reported against the author's own C for a helper they had written
  down. Both backends, and only when gcc ran.

  ★ This is also how two axioms share a helper. One declared inside an axiom belongs to that axiom
  alone; joining axioms together to share one stays refused, because it would merge their parameter
  lists and leave the article substitution rewriting someone else's text.

  ⚠ A resultless axiom takes no parameters — a parameter's value comes from a call, and nothing
  calls this. That was refused too, for the same reason: it spliced a parameter name into a
  file-scope declaration, which checked clean and would not build.

- **An axiom is a value: it can be passed around unrun and run wherever it lands.** A parameter, an
  object field, a series element — and which axiom runs is decided at run time:

  ```
  Pull a book on the c-language.
      Define c-language number length-of, given (the text subject), as [(int)strlen(the subject)].
      Define c-language number first-byte, given (the text subject), as [(int)(the subject)[0]].

      Define the jobs as a series of c-language number axiom given (the text) with (length-of, first-byte).

      For each job in the jobs, repeat:
          State cast job on ("hello").        ← 5, then 104
      Done.
  Done.
  ```

  An axiom is now writable as a **type**, and `given (…)` says what running it takes — the same
  spelling a function type uses (`number function given (the number)`) and the same parser, so the
  two cannot drift. The foreign text is still fixed where it was written: nothing is assembled from
  strings, and what moves is the axiom itself.

  Along the way, `Define alias as answer.` binds an axiom to a name and runs it from there; a chain
  of names is followed back to the source, so both names reach one wrapper.

  ⚠ **An axiom written as a type must say what it gives back**: the C wrapper's return type is
  built from exactly that, so `the c-language axiom job` has no signature to be. An axiom and a
  function also stay different types, whatever they share underneath.

- **Foreign pointers, as an `address`** — opaque, rabbit-scoped, and never dereferenced implicitly.
  A pointer comes back from C as a `voidable address` and goes back into C as an `address`, and
  that is all it does:

  ```
  Pull a book on the c-language.
      Define c-language voidable address open-dir, given (the text folder), as [opendir(the folder)].
      Define c-language number close-dir, given (the address handle), as [closedir((DIR*)the handle)].

      Pull a rabbit.
          Define handle as cast open-dir on ("logs").
          If handle is not void, cast close-dir on (handle).
      Done.
  Done.
  ```

  **There is one kind**, so `char*` and `FILE*` are the same type: what differs is not the value
  but what you do with it. There is no address-*of* operator and Cufet never creates one — an
  address only ever comes from C and goes back to C, which is why no layout question exists.

  **A pointer may only be held inside a rabbit, and cannot outlive one.** That block needed no new
  keyword to become the unsafe marker: a rabbit already means region-scoped memory work, and a
  pointer is a rabbit responsibility, because the arena that knows when a region dies is what knows
  when the pointer dies. Holding one outside is a static error, and so is storing one into anything
  that outlives the block — an address is a reference type to the region model, so the escape check
  that already guards a series or a map guards this too. NULL is void, as everywhere else here.

  **`the text at <address>` is the one read there is**, and it always copies:

  ```
  Define c-language voidable address next-found, given (the address held),
      as [({ struct dirent* e = readdir((DIR*)the held); e ? e->d_name : (char*)0; })].

  State the text at found but void is "?".
  ```

  It yields `voidable text` — rabbit-owned, never a view into foreign memory, so the bytes survive
  C freeing or overwriting the block they came from. Reading through a void address is void rather
  than a crash. Reading a struct or a scalar was considered and is unnecessary: an axiom can
  project a field or declare a local and hand it back, so those come home as ordinary results. Text
  is the one case with no single-expression answer on the C side.

  Neither word is reserved: `text` was always contextual and `at` is already matched this way for
  `<bits> at <n> bits`, so the pair is what makes the phrase unmistakable and both stay usable as
  ordinary names.

  **`and free it with <name>` names the axiom that releases it**, and the release then happens on
  every way out of the block — reaching its `Done.`, a `Stop`, a `return`, or an exception nobody
  catches:

  ```
  Define c-language number shut, given (the address held), as [fclose((FILE*)the held)].
  Define c-language voidable address open-one, given (the text file-path),
      as [fopen(the file-path, "rb")], and free it with shut.
  ```

  It rides the destructor registry, so it needed no new cleanup machinery — LIFO at the block's
  exit, and run by `cufet_raise` on the way out of an uncaught fault, were both already true.
  Measured: 1200 handles opened and freed where 509 is the limit, including a version where every
  single one is abandoned by an exception.

  The clause has to be on the acquiring declaration because nothing else can carry it — `getenv`
  and `strdup` hand back the same type with opposite obligations, and Cufet never reads the foreign
  text, so the person who wrote it is the only possible source of the fact. ⚠ **Saying nothing
  frees nothing:** a leak is recoverable and visible, where a double free is corruption that
  surfaces somewhere else entirely. Nothing checks that the named function is the *right* one.

  It costs no reserved word — `it`, `with` and `and` were already tokens, and `free` is recognised
  by lexeme after `, and`, where nothing else can appear, so it stays usable as an ordinary name.

  An address prints as `<address>`, never as its value — the two backends are two processes, so a
  printed handle could not agree between them however correct both were.

- **An axiom can be called as a statement**, for its effect: `Cast close-dir on (handle).` The
  answer is discarded, which is what a statement means.

  It used to be refused, and by a message that was not true — *"'close-dir' holds a c-language
  number axiom, not a function — you can only cast functions... Only functions can be cast"* — when
  every axiom call is written as a cast. The expression form had the hook and the statement form
  did not, so the writer was told to bind a result they had deliberately thrown away. Every check
  the expression form makes still applies: the language must be pulled, the arguments must fit, and
  the axiom is still built before the program runs.

- **Foreign source can hand back a `double`**, declared as a `voidable number` — `[sqrt(2.0)]` is
  `1.4142135623730951`. This is the boundary's one lossy conversion, and the only one that could
  have differed between the two backends: `number` is base-10 and a `double` is base-2.

  So it is written **once**, in C that both backends compile, and it hands back the coefficient,
  scale and sign a decimal is made of rather than the `double` itself. Neither backend converts
  anything — both assemble the same three numbers — and 17 significant digits cross, which is what
  a `double` round-trips in.

  **A value with no decimal becomes void**: NaN, an infinity, or a magnitude outside a decimal's
  range. That is not a new rule but `math`'s existing one — `square-root of (-4)` and `log of (0)`
  are both void already.

  ⚠ The two number guards are **disjoint**. A floating value declared `number`, or a whole one
  declared `voidable number`, is refused by the C compiler rather than converted: `number` is exact
  and `voidable number` is not, so which you meant is a real question.

  ⚠ **Passing a fractional value INTO foreign source is still refused** — a `number` argument
  arrives as a range-checked `long long`, so `[pow((double)the base, (double)the exponent)]` works
  for whole arguments and 0.5 does not cross. That direction waits for a use case.

- **Foreign source can hand back an unsigned 64-bit value** — `size_t` and `unsigned long long`,
  which is how most of libc reports a length. `[strlen(the subject)]` and `[sizeof(long long)]` are
  written as they would be in C, with no cast to get past the boundary.

  They were refused before, because a value in [2^63, 2^64) taken through a `long long` reads back
  negative — silently, and only for large inputs. The wrapper now hands back the bits together with
  one bit saying how to read them, decided at C compile time by the expression's own type
  (`CUFET_C_UNSIGNED`), so `(unsigned long long)-1` arrives as 18446744073709551615 rather than as
  -1. Both backends reconstruct the same decimal from the same pair, and a decimal holds every
  64-bit integer — signed or unsigned — exactly, so neither rounds.

  A `double` is still refused: base-2 against a base-10 decimal is a conversion with rounding to
  decide, not a widening.

- **Foreign source — an `axiom`, and the first slice of the C FFI.** Cufet can call C, on both
  backends:

  ```
  Pull a book on the c-language.
      Define c-language number get-pid as [getpid()].
      State cast get-pid.
  Done.
  ```

  `axiom` names the **contract** rather than the appearance: it is taken as given without proof,
  which is exactly what Cufet does with a C listing it cannot check. Square brackets are the
  delimiter because they appear nowhere else in the language — this is the one construct whose
  contents are not Cufet at all, so nothing about them needs disambiguating by context. Bracket
  pairs nest and survive, which is what makes `[argv[0]]` lex.

  The tag can be shortened (`Define a c-language axiom x as […]` → `Define c-language x as […]`)
  but never dropped. The brackets say *this is verbatim foreign text*; they cannot say *which*
  language, and the tag names who reads it — inferring it from what happens to be pulled would make
  a line's meaning depend on the scope above it.

  **What crosses so far is a C whole number into a `number`, and that is deliberate:** it is the
  one direction that cannot be lossy, since a decimal with 28–29 significant digits holds every
  64-bit integer exactly. A `double`, or an unsigned value that can exceed `long long`, is
  **refused** rather than cast — the second would come back negative, silently, which is the
  failure mode this project keeps refusing.

  ★ **Both backends put the same wrapper around an axiom**, from one place (`ForeignC`). The
  compiled backend pastes the text into its own C; the interpreter compiles it into a small shared
  library and calls it. Two wrappers would have meant one program with two answers, both looking
  right — the same reasoning the shared case table records.

  ⚠ **It needs a C toolchain on either backend**, which is the price of the interpreter staying an
  oracle here. The interpreter's shim caches by content, so `gcc` runs once per distinct axiom per
  machine rather than once per run. Where there is no toolchain at all — the playground runs the
  interpreter in wasm — the program refuses to run and says so, which is a required outcome rather
  than an oversight.

  ⚠ **gcc complaining about an axiom is now the author's message, not a bug report.** "Every line
  gcc reads was written by this compiler" acquired its first exception, and reporting it the old
  way would send someone hunting a cufet bug that is not there.

  An axiom's name may be used in exactly one place — returned. Reaching for one anywhere else is
  refused when the program is checked: `State get-pid.` used to check clean, print a C# object
  interpreted, and emit C that would not build. Three answers to one program.

  **An axiom is given a platform-guarded header set and writes no `#include`.** The C standard
  library everywhere, POSIX on Unix (`<termios.h>`, `<poll.h>`, `<sys/socket.h>`, `<sys/wait.h>`
  and the rest), Win32 and winsock on Windows. The split was measured with `gcc -fsyntax-only` on
  both toolchains rather than remembered: Linux has every header in the set and mingw has none of
  the ten POSIX-only ones, so guessing it would have failed every Windows build of an axiom-bearing
  program at the include rather than at the axiom.

  ★ **A fixed set rather than letting a writer name headers, and the reason is linking.**
  Everything in the set links by default — libc and POSIX need no flag, and mingw links
  kernel32/user32/advapi32 for `<windows.h>` on its own. A third-party library does not:
  `#include <sqlite3.h>` gets declarations and then fails with "undefined reference". Header
  control on its own would ship a feature that cannot work for the case that makes someone want
  it, so if it comes it comes as headers **and** link flags together.

  Windows sockets needed exactly that treatment: `-lws2_32` is now on the link line, because
  `socket()` is in libc on Linux and in a separate library here. Without it an axiom calling it
  compiled and then failed with `undefined reference to __imp_socket`.

  ⚠ **Foreign state is per-process, and the two backends are two processes.** A compiled program is
  its own; the interpreter calls C inside the process running the interpreter. Anything C remembers
  globally — winsock initialisation, `errno`, a library's one-time setup — can differ between
  running a program and building it. Found by a test that asserted `socket()` succeeded: it does in
  the .NET test host, which has already initialised winsock, and does not in a fresh binary. Both
  backends were right, and the test was wrong to reach across.

  ★ **Every axiom a program can run is built before the program starts, on both backends.** The
  compiled backend meets them all at build time and refuses the whole program if one will not
  compile; the interpreter used to compile each on first use, so a bad axiom late in a file printed
  all the earlier output and then failed, where building it printed nothing. Two answers to one
  program. Both now produce no output at all.

  ⚠ The set is the returns that RUN an axiom, not every axiom written down — which is exactly what
  the compiler emits a wrapper for. An axiom declared and never returned is built by neither, so
  preparing it would refuse a program that builds perfectly well: the same divergence pointing the
  other way, and there is a test for each direction.

  **An axiom takes parameters, and reaches them by the article.** Until now an axiom could only be
  a constant C expression, which meant no real binding was possible — no `open`, no `read`, no
  `ioctl`:

  ```
  Define c-language number open-read-only, given (the text file-path),
      as [open(the file-path, O_RDONLY)].

  Define fd as cast open-read-only on ("/etc/hostname").
  ```

  ★ `the file-path` is never valid C or SQL, which is what makes a **symbol-free** marker
  unambiguous: it is English sitting in code that is not English, so nothing has to be escaped.
  It also reads as the line that declares it.

  ★ **Only values cross, never text.** C receives a marshalled `long long`, `const char*` or `int`
  — the axiom is fixed where it is written and cannot be assembled from strings, the same way
  `Run "grep" with arguments (…)` passes a list rather than a command line.

  ⚠ A `number` is **range-checked** on the way in, not truncated: `cast double-it on (3.50)` raises
  rather than quietly handing C a 3. The message lives once, in `ForeignC`, and the emitted runtime
  quotes it — so both backends refuse in the same words, which the oracle compares byte for byte.

  ⚠ A declared parameter the source never mentions is refused. Only declared names are substituted,
  so a misspelled `the paht` would otherwise reach gcc as a stray `the` — a complaint about the
  writer's spelling, phrased in a language they were not writing.

  ⚠ **A reserved word cannot be a parameter name**, and `path` is one, so `given (the text path)`
  does not parse. `file-path` does.

  **What an axiom gives back is declared where the axiom is written** — `c-language number add`
  reads as what it is, a C-language axiom that yields a number. The tag qualifies the *axiom*, not
  the number, and both middle words drop: `the c-language number axiom add` → `c-language number
  add` → `c-language axiom add` → `c-language add`.

  ★ **This replaced taking the type from the line that USED the axiom**, which shipped earlier the
  same day and cost far more in practice than on paper: a call had to be the entire right-hand side
  of a typed binding, so it could not sit in a condition, an interpolation, arithmetic, or an
  argument list. Measured at one line becoming two at three of four call sites in the first real
  program written against it — the cost fell on *uses*, which are what multiply. A call now
  composes anywhere an ordinary call does.

  ★ **It is not inferred from the C, and could not be.** The C type is knowable — `_Generic`
  already reads it, which is why no C type is ever written down — but it is needed before any
  toolchain exists (`cufet check` needs none, and the playground is wasm), it would vary by
  platform (`size_t` is not one width), and above all **it does not determine the meaning**:
  `isatty` gives an `int` that is a fact, `fopen` a pointer that is a handle, `getchar` an `int`
  that is a character or an end. C says how many bits arrive, never what they are.

  ⚠ An axiom that says nothing about its result may be **written but not run**, which is what keeps
  room for handing one around unrun. And a use whose type does not fit is now refused by the
  ordinary type-mismatch message rather than a special one — foreign source stopped needing its own
  error there.

  **A `fact` and a `voidable text` cross back, not just a number** — so C can finally hand Cufet a
  string:

  ```
  Define c-language voidable text describe, given (the number code), as [strerror(the code)].

  State (cast describe on (2)) but void is "no idea".      ← No such file or directory
  ```

  ⚠ **A text result must be declared `voidable text`.** C says nothing is there by handing back
  nothing — `getenv` on an unset name is the everyday case — so NULL lands in the mechanism the
  language already has rather than in a promise C cannot keep. A plain `text` is refused, and says
  why.

  ⚠ **The text is COPIED out of C's memory, never aliased**, on both backends: into the arena when
  compiled, into a managed string when interpreted. `strerror` hands back a buffer the next call
  overwrites and anything allocated dies when its owner says so, so a Cufet text pointing at either
  would change under the program.

  ★ Both backends now compile the **same wrapper function, byte for byte**, from one builder — the
  splice, the guard, the C types and the call are decided once and compiled twice, rather than
  described twice and compiled once each.

  ⚠ Two bugs found by writing programs against it, both invisible to reading: an axiom containing a
  **top-level comma** (C's comma operator) broke the boundary guards, because a one-parameter macro
  splits its argument before expanding — the macros are variadic now. And the interpreter had a
  **use-after-free**: an axiom can hand back a pointer it was *given* (`[the subject]`), and the
  argument buffers were released before the text was copied out.

  Still design and not built: `address`, `the text at`, `released by`, and passing an axiom around
  unrun. See
  [DESIGN.md](docs/DESIGN.md#foreign-interoperability), which carries the reasoning and the rejected
  alternatives for all of it.

- **`For each` over a stash.** The last mile of coroutines. Suspend and resume shipped in
  0.13.0, but *consuming* a stash still cost a six-line drain loop — and that loop appeared three
  times in `examples/language/stashes.cufe`, the feature's own example. Now:

  ```
  Define found as cast long-words-in on (hopper, a series with ("a", "rabbit", "in", "the")).
  For each word in found, repeat:
      State word.
  Done.
  ```

  It is the same loop a series takes. `Stop` ends it, `Skip` moves to the next value, and the
  iterator holds the **plain buried type** rather than a `voidable` of it — reaching the body is
  itself the proof there was a value. Over an endless stash the loop is endless, so `Stop` ends
  it. A spent stash ends it on its own.

  ★ **Neither backend learned anything.** The loop is rewritten in the front end into the drain
  people wrote by hand, so what runs is `Repeat until` holding a `Define` and an `If` — statements
  both backends have run since long before stashes existed. The checker checks the rewritten loop
  rather than the written one, so there is no second semantics to keep in step.

  ★ Inside a burying body this is **delegation** — one stash consumed while another is produced —
  which is why the rewrite runs *before* the state-machine builder: what gets linearised is
  statements the machine already knows how to step.

  ⚠ No `Stop` collision, and none was possible: the rewrite's own `Stop` fires before the body is
  reached. The worry was that the two would meet; they cannot.

  This needed no interface, no conformance and no new declaration syntax. `For each` over a
  user-defined type was dropped in 0.11.0 because an interface can be neither a return type nor
  generic, so nothing could declare "hands back something steppable" — but coroutines did not
  produce an open family of steppable things. They produced **one concrete type**, `stash of T`.

- **A method can bury.** Found by writing a program with the loop above: a `bury` inside an
  object's method said *"declared to give back a number, but it can reach its end without returning
  one"* — blaming a missing `Return` in a method that was never going to return one. Behind the
  message, nothing supported it: methods were never registered as burying, and the rewrite did not
  walk into object definitions at all.

  ```
  Define object ticker with (the number first-beat):
      Bind number to ticks, given (the rabbit helper):
          Define next as one's first-beat.
          Repeat:
              Have helper bury next.
              The next becomes next + 1.
          Until false.
      Done.
  Done.
  ```

  ★ **The state belongs to the INSTANCE.** One method becomes two *methods* — not two functions —
  so the dispatch still reads `one's <field>`, and the closure the factory hands back captures the
  receiver. Two tickers give two stashes that share nothing, which is the rule two casts of one
  function already followed.

  `unto` methods bury too, and needed their own arm: an `unto` method is never moved into its
  type's method list, only its signature is registered, so both halves of the rewrite have to keep
  the `unto` or they land as free functions with no receiver.

  ⚠ "Does this bury" is answered per `(type, method)`, never per name. Two types may each have a
  `ticks` with only one of them burying.

  ★ Also fixed by the same recursion: a burying function nested inside an **ordinary** method was
  not rewritten either, and its `bury` survived to a backend.

### Changed

- **The reference is split, and the four long docs moved into `docs/`.** Books have their own
  document now: [BOOKS.md](docs/BOOKS.md) holds `math`, `collections`, `chance`, `matrix`, and
  foreign source — because `Pull a book on the c-language.` is the same construct as `Pull a book
  on math.`, so a language book is documented where books are. `Pull` itself stays in REFERENCE:
  pulling is a module mechanism one level above books. REFERENCE, GRAMMAR, DESIGN and ROADMAP now
  live under `docs/`; README, CHANGELOG, CONTRIBUTING, LICENSE and NOTICE stay at the root. **Any
  link to the old top-level paths needs updating.**

- **A bundled book is pulled as a book.** `Pull math.` is refused; write
  `Pull a book on math.` (or `Pull books on math, and collections.`). The plain
  `Pull <name>.` form is for a module you defined. Programs using the old spelling must change.

  ```
  That doesn't work: 'math' is a book, so it is pulled as one.
  The plain form is for a module you defined; a book is a library the language ships.
    Write 'Pull a book on math.' — or 'Pull books on math, and <other>.' for several at once.
  ```

  ⚠ **This reverses something 0.16.0 shipped, and it was never a decision.** The general
  `Pull <module>` branch swallowed bundled names on its way past, and a test then pinned the
  accident — it was called `ABookIsPulledByTheSameFormAsAModule` and its note called the plain form
  "the point of the whole exercise". Pulling *is* one mechanism and asks the same question
  everywhere; what it should not do is hide which KIND of thing is being pulled. A library the
  language ships and an object you wrote are not the same thing, and the noun is what reads besides:
  *a book on math* is English, *a math* is not.

  ★ Also corrected in GRAMMAR and REFERENCE: a book was described as conforming "by CONSTRUCTION
  (its members are native)". **That stopped being true in 0.16.0** — `math` has no native part left
  and `collections`'s only native piece is the `matrix` type. What makes something a book is that it
  is a **library**, not how it is implemented.

- **A body resolves the names it can see where it is WRITTEN — plus any MODULE its caller
  pulled.** Deferring an unresolved name in a function or method body exists for one reason: a
  pulled module is a capability of the block that uses the body, which is what lets a module's
  method say `math's pi` and leave `math` to whoever pulls it. That reason only ever covered module
  names. It was applied to *every* name, so a plain typo was indistinguishable from a capability and
  waited until the line ran to say so:

  ```
  Bind number to sneaky:
      Return borrowed + 1.        ← `borrowed` is a local of the CALLER
  Done.
  ```

  That is dynamic scoping, it checked clean, and nothing ever wanted it. It is a static error now.
  The lexical half of the rule was already in force — a body using a top-level constant declared
  further down was refused, with a message recommending closures — so this finishes a rule the
  error messages already assumed.

  ★ **"Module" means DECLARED**: a bundled book, or an object marked `and module`. An **alias is
  not one**. `Pull math as m.` makes `m` an ordinary name in that block, so a body written inside
  the pull still sees it; a body written outside does not. Measured before choosing: no aliased
  pull exists anywhere in the prelude or the examples, and both aliased-pull tests declare their
  function inside the pull. The one shape this removes worked only while every caller happened to
  pick the same alias — rename it at one call site and the function breaks with nothing to point at.

  ⚠ **Known gap, now bounded.** A module's needs are checked at its pull but are not transitively
  closed: a module reaching `math` only through a free function it calls is still missed, as is a
  function reached through a variable rather than by name. That surface used to be every name; it is
  now module names only.

- **One ownership story: every nonlocal exit releases the same four things, through one place.**
  A jump out of a block has to run unmakers, close files, pop exception pads and pop rabbit arenas —
  always those four, always in that order. That was written out longhand at **nine** sites, with
  four parallel per-loop stacks feeding them and two handler records carrying four loose cleanup
  fields each. Nothing checked that the nine agreed, and twice they did not: `FailureGotoBody`'s own
  comment records the first time (*"three of the four were already out of step when arenas were
  added"*), and `Suppress` was the second — see below.

  There is now one `CleanupPoint` — a mark taken where a jump will land — and one `UnwindTo(point)`:

  ```
  _loopExits.Add(HereCleanup());                          // was three Adds and a conditional fourth
  sb.AppendLine($"{indent}{UnwindTo(LoopExit)}break;");   // was four nested calls
  ```

  ★ The invariant is structural now rather than remembered: a new kind of releasable thing is one
  field on `CleanupPoint` and one term in `UnwindTo`, and every site gets it, because no site spells
  the parts out any more.

  ⚠ It also removed a shape that could not be right: the four per-loop stacks were pushed at three
  different places, and the unmaker one only when the program had unmakers — so they could hold
  **different lengths**. Nothing indexed them together, so nothing had gone wrong yet. One list of
  marks cannot have that shape.

### Fixed

- **An error message could print `<unknown>`.** `the text at <a name that resolves to nothing>`
  reported *"reads through a foreign address, and this is a `<unknown>`"* — the discard arm of the
  type formatter, in front of a reader. The name-resolution pass answers null for a name it cannot
  resolve, on purpose, and that null reached the message.

  ⚠ The test that stops internal vocabulary reaching a reader could not catch it: it scans string
  literals and strips interpolation holes, and `<unknown>` arrives through a hole. The null case is
  refused with its own sentence now — refused, not skipped, because with the name genuinely in
  scope this is the only check that stops the program.

- **A type can be declared anywhere, including inside a function.** `Define object` in a function
  body, a loop, an `If` arm or a `Try` block is registered like any other type declaration:

  ```
  Bind number to make-and-measure, given (the number side):
      Define object square with (the number edge):
          Bind number to area: Return one's edge * one's edge. Done.
      Done.
      Define the shape as a new square { the edge side }.
      Return cast area on (the shape).
  Done.
  ```

  ⚠ It used to be silently ignored, and the USE failed four lines later with *"'square' is not a
  defined object type — define the object type first"*, telling the writer to declare what they
  had just declared.

  ★ The rule it makes good is one the language already followed everywhere else: a **type**
  declaration belongs to the program wherever it is written, while a **value** binding does not.
  `Define x as 5.` inside a block is still local to that block, and a nested `Bind` is still a
  closure rather than a free function.

- **Generic errors point at the call that filled the blank, not at the body.** A body written once
  is right for every filling but the bad one, so being told to fix it was misleading:

  ```
  gen.cufe:10:7: error: 'doubled-first' does not work when it fills 'element' with text.
    Here on line 10, you're trying to call 'doubled-first' with those types.

    Its body is what refuses them:
      That doesn't work: arithmetic requires numbers on both sides.
      Here on line 3, you're trying to use * with text and number.
  ```

  A filled body is checked by a separate pass over a spliced program, and that pass had never seen
  the call that caused it. The filling site now travels with the filling, and the body's own
  explanation is kept underneath. ⚠ Generic **methods** still report at the body: an object filling
  happens during type resolution, which has no call site to carry.

- **`x is the <name>` silently answered false, for every value of every type.** `a`, `an` and
  `the` all lex as one article token, and the `is` parse treated any of them as the start of
  `is a <type>` — so `the phrase` was read as a TYPE annotation and the comparison became "is this
  value of type `phrase`?". `x is not the <name>` was the same arm, always true.

  ```
  Define the phrase as "hello".
  Define plain as "hello".
  If plain is the phrase, …        ← was false, now true
  ```

  ⚠ It hit **idiomatic** code hardest, which is why it lasted: the style is to lead a name with
  `The`, so `the phrase` is the natural way to write it and `x is the phrase` the natural way to
  compare against it. And the negative form is invisible whenever the values genuinely differ, so a
  guard written that way let everything through while looking correct. Both backends were
  identically wrong — the front end is shared — so no oracle test could see it.

  ★ Only `a`/`an` introduce a type test now, which is the form GRAMMAR always documented. Nothing
  in the examples, tests or docs used `is the <type>`.

- **A `Bind` inside a pull block inside a rabbit did not compile**, and said so with a message that
  was untrue: *"'doubled' is declared further down this block"* about a function declared four
  lines above the call. Interpreted, it ran.

  ★ The fault was the walk, not the ordering. Binds in a pull body are hoisted to free functions,
  and the pull emitter skips them because of that — but the collector doing the hoisting matched
  `PullStatement` and recursed only into its body, so a pull nested inside a rabbit was never
  reached and its Binds were emitted nowhere. Discovery now uses the reflection walk, which
  descends through every construct. Either nesting alone always worked, which is what hid it.

- **`State` on a function or an axiom refused to compile while the interpreter printed it.** The
  statement carried a per-type switch of its own that duplicated `WriteCall` arm for arm, and it
  had drifted twice already — addresses and unions were each patched back into agreement after the
  fact, the first caught only by the oracle. `WriteCall` knew how to print a callable and that
  switch did not, so one backend printed `<function>` and the other refused the build.

  ★ Fixed by deleting the switch rather than adding arms to it: every `cufet_print_X` it called is
  defined as `cufet_write_X(v); cufet_nl();`, so `State` is now `WriteCall` plus a newline and the
  two cannot drift again.

- **A function reaching a module it never pulled checked clean and then failed three different
  ways.** `cufet check` said "No problems found"; the interpreter died with *"'math' isn't defined
  — Declare it first: Define math as <value>"*, which is not how you get `math`; and the compiler
  said *"'square-root' can't be read from a number"*, blaming a number that appears nowhere in the
  program. All three measured on one file.

  ```
  Bind number to rooted, given (the number x):
      Return math's square-root of (x).        ← now refused where it is written
  Done.
  ```

  ★ A **module's** body may still reach for a module its caller pulled — that is what a pull is
  for, and it is verified at the pull site. The permission was simply wider than its own reason: a
  module is pulled into a block and its methods inherit what that block pulled, while nothing pulls
  a plain function into anything. A function written outside a pull now pulls what it needs, and one
  written inside a pull is unaffected.

- **`and free it with` leaked an acquisition nobody named.** The release was registered against
  the `Define` that caught the result, so `Cast open-one on ("f").` opened a handle and registered
  nothing — and neither did an acquisition used inline in a condition. Both backends leaked
  identically, which is why no oracle test could see it. Release is now registered at the
  **acquisition**, so it fires once per call however the call was reached.

  ★ That also removes a limitation rather than adding a rule: an axiom carrying a release clause
  can be passed around like any other, because a value-carried call no longer needs a binding to
  hang the registration on. And it is the safer of the two places by construction — names multiply
  and can reach one pointer, while an allocation happens once.

- **`State` on an axiom printed C# internals.** The value an axiom name holds is its literal, and
  the interpreter had no arm for it, so printing one produced `AxiomLiteral { Source = …, Line = …`
  — source text, line and column — while the compiled backend refused the same program at build
  time. An axiom now reads as `<axiom>`, beside `<function>`.


- **A typo in an axiom killed the interpreter's host process on Linux**, instead of being reported.
  A Windows DLL must resolve every symbol at link time, so `[not_a_real_function()]` failed the
  shim build there and was correctly blamed on the author's C. An ELF shared object has no such
  rule: it linked clean, loaded, and the process died on the call with `symbol lookup error` —
  under CI, taking the whole test host with it. The shim is now linked with `--no-undefined`
  (`-undefined,error` on macOS), so both platforms refuse it at build time.

  Caught by GitHub Actions against a suite that was green on Windows, which is the shape a
  one-platform developer cannot see. ⚠ `-lm` had to come with it: refusing undefined symbols means
  everything must resolve at link time, and on glibc `sqrt` and `pow` still live in libm for the
  linker even though the runtime merged it into libc — measured on glibc 2.44, where the `double`
  axioms stopped linking the moment the flag went on.

- **A compiled program skipped its destructors when an exception went uncaught**, where the
  interpreter ran them. `cufet_raise` with no handler installed printed its message and called
  `exit(1)` without unwinding, so an object made inside a block that then faulted was never unmade
  — invisible until the destructor does something, which is exactly when it matters. The path where
  a handler *does* exist had always run the pending unmakers first; the no-handler path now does
  too, for the faulting thread's whole pending set.

- **A task's own objects were never unmade in a compiled program**, and not only when the task
  died — one that ran to completion skipped them just as thoroughly. A task body was emitted as a
  function frame, and a frame's own `Define`s do not register a destructor at all, so no exit path
  had anything to run. The interpreter treats a task body as the block it is written as, and the
  compiler now does the same. A function body is unchanged: its own top-level `Define`s still do
  not fire, on either backend.

- **Catching an exception crashed 4% of the time on Windows.** Not a rare edge — a compiled program
  that raises and catches once died with an access violation in roughly one run in twenty-five, on
  every Windows build. Measured at **121 crashes in 3000 serial runs** of a single binary.

  On x86-64 mingw-w64, `setjmp(b)` expands to `_setjmp((b), __builtin_frame_address(0))`, and that
  saved frame pointer makes `longjmp` perform a full SEH unwind through ntdll's `RtlUnwindEx`. At
  `-O2` the unwinder reads stack memory it cannot validate and faults depending on what happens to
  be lying there — which is why it looked random. The generated pad now passes a NULL context
  (`CUFET_PLAIN_SETJMP`), so `longjmp` restores registers and skips the unwind: **3000 runs, 0
  crashes.**

  ★ Skipping the unwind is not a workaround; it is what the runtime already assumed. The unwinder
  exists to run `__finally` blocks and C++ destructors between the jump and its target, and
  generated Cufet C has neither — `cufet_raise` runs the unmakers, closes the files and pops the
  arenas itself before it jumps. There was never anything for `RtlUnwindEx` to do.

  ⚠ **It had been dismissed as a flaky test for weeks.** One test compiled such a program, ran it
  once, and passed 96% of the time; the failures read as parallelism or antivirus, and a retry was
  nearly added to paper over it. **No test in the suite could have caught it**, because every one of
  them compiles once and runs once — so the fix ships with a test that compiles once and runs the
  same binary 300 times. That shape is the durable part; the bug is not the last defect that will
  only appear in a fraction of runs.

  The interrupt landing pad's mingw branch had the same spelling and is fixed with it. Nothing
  jumps to that pad on Windows today, so it was latent rather than live.

- **`Suppress` released arenas and nothing else.** A destructor on an object made inside an
  exception handler never ran when the handler suppressed — a live divergence, since the interpreter
  unwinds the handler block and runs it:

  ```
  In case of exception (the exception):
      Define inside as a new noisy { the tag "in-handler" }.
      Suppress the exception.
  Done.
  ```
  > interpreter: `before / handling / unmade: in-handler / after`
  > compiled: `before / handling / after`

  `Suppress` is a nonlocal exit out of the handler block, and the code said so — *"exactly like Stop
  out of a loop"*. `Stop` does all four releases; this did one. Files opened in the handler were
  likewise never closed. Found by reading the sites while scoping the refactor above, which is the
  refactor's own argument.

- **★ The CLI silently dropped arguments it did not understand.** `cufet build a.cufe -o out.exe`
  wrote the binary beside the SOURCE and said nothing — `-o` is not a flag this CLI has, and a
  whole session's binaries went somewhere other than where they were asked for before anyone
  noticed. A mistyped `--jsno` on `check` disabled JSON just as quietly, and a second file argument
  was read by nobody. Every verb refuses what it cannot use now, and exits **2** — 0 and 1 are the
  program's own answers (it ran, it did not), so a mistake in the *command* has to be a third thing.

  ```
  build: don't know what to do with '-o out.exe' — 'cufet build <file.cufe>'.
    '-o' is not a flag build takes. Run 'cufet --help' for the flags each command has.
  ```

  ★ **One place keeps the silence, deliberately:** arguments after a source file
  (`cufet script.cufe one two`). The language has no way to read them yet, but that is exactly
  where program arguments would arrive if they are ever added, and refusing now would only have to
  be un-refused later.

  ⚠ The CLI had **no tests at all** — no test project referenced it — which is why this survived.
  It has some now, driving the real executable as a subprocess.

- **`Suppress.` was documented but does not parse.** REFERENCE showed the short form; the parser has
  always required `Suppress the exception.`

- **★ A module's dependency was only checked when the module was defined BEFORE the pull.** The
  check ran at the pull, so it could only see modules already checked. Flip the two and the
  identical program passed checking and died at run time — advising `Define math as <value>` for
  something you pull. Verification is deferred to the end of checking now, when every module's needs
  are known, with the names visible at each pull SNAPSHOT so a later `Define` cannot retroactively
  satisfy a dependency the pull needed earlier. Neither path had a test; both do now.

- **A top-level closure could not see the local it captured.** A lambda inside a function took
  its whole enclosing scope; a lambda at the top level took only functions and constants and left
  ordinary locals "to the capture machinery" — which meant to the deferral above. So the checker
  never saw the very name the closure captured, in a program as plain as
  `Define f as a function: Return the number of nums. Done.` sitting beside its `nums`. A closure
  captures what is lexically in scope; that is what makes it a closure rather than a lookup. Both
  nesting levels take the whole scope now.

- **`x is not void` kept its narrowing across a bury — a live backend divergence.** A burying
  body is cut into blocks at each `bury`, and an arm's condition is carried into its block as a
  guard so the narrowing survives the cut. Only `x is a <type>` was recognised as narrowing;
  `x is not void` — the other narrowing form, which both front ends already treat as one — was
  not. So the arm's block ran with the name back at the `voidable T` its slot holds:

  ```
  Define value as unbury inner.
  If value is not void:
      If value is greater than 12:      ← compiler: "Binary operator 'Gt' on a 'voidable number'"
  ```

  The interpreter narrows by **value** and ran the program correctly; the compiler refused it, or
  in the `bury value + 1` shape emitted C that gcc rejected. No `For each` involved — the shape is
  the hand-written drain and was already broken. Found while building the loop above, which walks
  straight into it.

  ⚠ Worth the note for its shape: a front-end rewrite that drops a guard surfaces as a **backend
  divergence**, and only the compiler can see it. An interpreter-side test of this cannot go red,
  so the regression test lives in the pipeline oracle suite.

- **★ A name shadowed at a different type stayed shadowed after the block — the compiler's second
  divergence of the day, and also older than anything in this release.** The C was right: the inner
  declaration sits in an inner brace and the outer variable comes back at the closing one. The
  compiler's type table did not come back with it, so the first read *after* the block reached the
  outer variable through the inner type's accessor:

  ```
  Define value as 99.
  Repeat:
      Define a shadow value as cast maybe-num on (7).   ← voidable number
      State value.
      Stop.
  Until false.
  State value.                                          ← cvd_0_write(cv_value) on a CufetDec
  ```

  gcc refused the program; the interpreter, whose scopes really do pop, printed `7` then `99`. Found
  because a stash loop shadows its iterator the way a series loop always has — but no stash is
  needed to hit it, and `Define a shadow` at a new type was enough.

- **A stash could not be named in a type error.** `FormatTypePlural` had no `StashType` arm, so
  every message that reached for one said `<unknown>` — including the refusal for looping over a
  stash, which read *"counter holds `<unknown>`"*. `FormatType` had always had its arm.

- **A generic blank inside a `stash of T` parameter could not be filled in.** `Unify` matches a
  blank against an argument and had arms for series, voidable, failable, channel, both streams and
  map — not for a stash. Its catch-all answers "matched", so `stash of thing` matched
  `stash of number` and bound nothing, and the blank was reported as one *"nothing passed in says
  what fills it"*. `series of thing` worked throughout, which is what made it read as a rule about
  blanks rather than a missing case. Generics and coroutines compose now.

- **A stash answered "not region-bearing".** `IsRegionBearing` decides the escape *annotation*, and
  had no `StashType` arm — so it said `false` where the closure a stash lowers to says `true`.
  Latent rather than live: the compiler's own closure-escape rule catches the escaping program
  first, which is now pinned by a test so it stays caught. ★ Three type switches in one day were
  missing the same arm; a stash is a container like any other and wants looking for.

- **A module's missing dependency is caught by the checker, at the pull.** Found by writing a
  module from outside the prelude for the first time — the bundled books never hit it, because
  all three are self-contained and use nothing.

  A module resolves names in the block it is USED in, so `geometry` needing `math` is a
  requirement on whoever pulls it. Forget it and you got three answers to one program: `check`
  said *No problems found*, the interpreter died pointing at a line INSIDE `geometry`, and the
  compiler said *"field access on 'number' is not yet supported"* — blaming itself for a scoping
  mistake. Now:

  ```
  That doesn't work: 'geometry' uses 'math', which isn't pulled here.
    A module's dependencies come from the block it is used in, not the one it is written in.
    Pull them together: 'Pull books on math, and geometry.'
  ```

  Nothing new to declare: the checker records what a module's body reaches for and verifies it at
  each pull. It also catches the subtler case where a module *resolves* its dependency at its own
  definition and is then called somewhere that dependency is not live.

- **An unresolved name is a static error where the scope is FINAL.** `State mystery.` used to
  check clean and fail at run time. It is refused now — at the top level, after a `Done.`, or
  anywhere nothing can arrive later to define the name.

  ⚠ **A detached body still defers, and that is not laziness.** A method or function resolves
  names in the scope it is CALLED from, so `math's pi` inside an ordinary object's method is
  legitimate whenever the caller pulled `math`. Refusing there would break working programs. Doing
  for every object and free function what is now done for modules — recording needs, checking them
  at each call site — is the other half, and is not built.

  ★ **What it flushed out is the measurement the *self-verifying docs* item was missing: 54 of the
  190 pinned doc blocks are FRAGMENTS, not programs.** They reference names no fence defines
  (`Increment i by 1.`), so they never ran — they passed `check` only because unresolved names
  were tolerated. The baseline is re-pinned at 136, which is what is actually true.

- **The compiler stopped blaming itself for a scoping mistake.** `field access on 'number' is not
  yet supported by the compiler` came from an unresolved name falling back to `number` and then
  having a member read off it. It says `'square-root' can't be read from a number — it has no such
  member` now, which points at the program.


## [0.16.0] — 2026-08-20

**Everything pullable is an object.** A book is a module, a rabbit is a module, and a writer's
own object is a module on exactly the same terms — there is no privileged builtin category left.
Both bundled books are written in Cufet, transcendentals and all; a rabbit is an object defined
in one line of it; and all three pass as `module` values by *inheritance*, because nothing in the
checker asks which kind arrived.

### Added

- **The whole `math` book is written in Cufet, transcendentals included — and the libm
  caveat is retired.** Slice 3 of the 0.16.0 arc is complete. `square-root`, `log`, `exp` and
  `power` are computed on the decimal itself, in `Prelude/math.cufe`; nothing in either bundled
  book touches a `double` any more, and neither book has a native member left.

  **What that buys, and it is the point of the whole slice:** the two backends no longer share a
  platform library, they run the *same algorithm on the same arithmetic*, so they agree **by
  construction**. The documented platform-owned caveat — `power` with a fractional exponent
  differing in its last digit between ucrt and glibc, because .NET's `Math.Pow` *is* the
  platform's libm — is gone, and the family it made untestable is now asserted
  (`Book_Math_Power_FormerlyLibmDivergentFamily`).

  - `square-root` is Newton–Raphson, after scaling into `[1, 100)` by **even** powers of ten so
    the root scales by an exact power of ten. It stops when the iteration stops descending,
    which is exact and needs no epsilon. Measured: `√2` and `√10` correct in all 28 digits.
  - `log` reduces by powers of ten and then of two, and sums the `atanh` series that converges
    fastest near 1.
  - **`exp` is a new member** — `math`'s eleventh. `power` needs it, and a math book without the
    companion to `log` would be an odd thing to ship; it reduces onto a power of two and sums
    the ordinary series.
  - `power` gives an **exact** answer for a whole-number exponent by repeated squaring — which
    is also the only path a negative base can take — and `x^0.5` is routed to `square-root`.
    Everything else is `exp(y · log x)`.
  - **Accuracy, measured rather than claimed:** `square-root` and `exp` came out correctly
    rounded on every value checked; `log` is within ~2 units in the last place at 28 digits.
    Whole-number powers are exact, so they are *more* accurate than the double-backed versions
    they replace. What is guaranteed absolutely is that both backends give the same answer.
  - **The transcendentals return far more precision.** `math's square-root of (2)` was
    `1.4142135623731` (the double bridge's 15 significant digits) and is now
    `1.4142135623730950488016887242`. Perfect squares are unchanged.
  - **★ One old answer was not merely imprecise, it was wrong.** `power of (2, 50)` returned
    `1125899906842620`; the value is `1125899906842624`. A double carries 15 significant digits
    and that needs 16, so the last one was lost — and a test pinned the wrong number, faithfully
    recording what the implementation did. Whole-number powers are exact now.
  - **~120 lines of C runtime deleted**: the entire decimal↔double bridge, which existed only to
    replicate .NET's `DecCalc` conversions bit-for-bit so the two backends could agree about
    doubles. With no doubles left, there is nothing to agree about.
  - **A book nobody pulls is dropped from the program.** The prelude is prepended to every
    program, so once the books were written in Cufet a one-line `State "hi".` began carrying all
    of `math` and `collections` into its emitted C — 20 KB of program against 54 KB of runtime,
    which is the ratio the runtime split exists to prevent. A layer's members are reachable only
    through a `Pull`, so a book that is never pulled is dropped whole; that one line is back to
    210 bytes. The pull sites are collected by the type checker as it resolves them, so there is
    no separate AST walk to keep in step.

### Fixed

- **★ Stating a book printed a C# class name.** `State math.` gave
  `Cufet.Interpreter.Interpreter+BookValue` interpreted, while the compiler printed `math()` —
  a divergence and internal vocabulary shown to a reader, in one. `Format` had no arm for a
  book, so it fell through to `val.ToString()`.

  A module now prints as the object it is, everywhere: `math()`, `greeting-kit()`. There is
  never anything inside the parentheses, because a module with fields is refused at the pull.

  ⚠ **The same fallthrough leaked `MatrixValue` once before** — there is a comment recording it
  three lines away. Twice is a pattern: a catch-all that ends in `ToString()` turns every type
  nobody remembered into a host type name printed at the user.

- **`is` on a rabbit or a function value emitted C that gcc refused.** Both type-checked and
  ran interpreted, so `Pull a rabbit as hopper. … State hopper is grace.` printed `false` and
  then would not build — *invalid operands to binary ==*.

  The cause was the shape of the emitter rather than a missing type. Equality sent records,
  objects and series to `EqCall` and let a **catch-all** handle everything else with `==`, on
  the assumption that what was left were facts and maps. Anything else the checker permitted
  arrived at `==` applied to a C struct. `EqCall` even had a correct rabbit arm — nothing
  reached it, because a direct `is` never went there.

  All equality now goes through `EqCall`, the one place that knows how each type compares, and
  whose default arm refuses by name. ⚠ **The catch-all was the bug**: it assumed what was left
  instead of saying it, so every type added since had joined it silently. A type nobody has
  taught it now fails loudly instead of miscompiling.

- **A bundled book's code could collide with names in the program that used it.** The prelude
  is prepended to the program, and a method body imports the top-level functions and constants
  around it — so a book's own local shared a scope with the writer's names. A program declaring
  `Bind number to total` broke `log`, whose running sum is called `total`, with *'total' is
  already defined*.

  A book's Cufet layer now imports nothing from the writer's top level, on both backends. That
  is the rule rather than a renaming: a book is written without sight of the program that pulls
  it, so nothing in the program should be able to reach inside it. ⚠ The failure was invisible
  until the books were written in Cufet — native members had no Cufet scope to collide with.

- **A compiled division left its quotient in the wrong form, and it hid behind printing.**
  `cufet_div` always reduced to scale 28 and never stripped the trailing zeros, so `11 / 10`
  was carried as `1.1000…0` where .NET leaves `1.1`. Both backends *printed* `1.1` — the
  formatter strips trailing zeros too — so the difference was invisible until some later
  operation on that value overflowed at one scale and not the other. Found by exactly that
  route: `power of (1.1, 3)` worked with a literal and failed with a computed `11 / 10`.

  A quotient is now left in minimal form, matching the oracle. ⚠ **Worth remembering as a
  shape:** a divergence that the printer normalises away is invisible to every output-comparing
  test in the suite, and only becomes visible when it changes whether something *fails*.

### Changed

- **The arc's finish line: a writer's object, a rabbit and a book are all `module` VALUES.**
  `given (the module m)` accepts all three on identical terms, on both backends — and they pass
  by **inheritance**, not by any decision made about them. A module is an object, an object is
  first class, so a module is first class. Nothing in the checker asks which *kind* of module
  arrived, which is the whole of what this arc was for.

  A book reaching that point took one change with a long run-up: **a pulled book binds at its
  Cufet layer** — an ordinary object — rather than at `BookType`. That is only honest because
  slices 1–3 moved every book member into Cufet first, so the layer *is* the book; a `BookType`
  is not an object, so conformance had nothing to inherit from. `chance` gained a layer of its
  own (`Prelude/chance.cufe`, carrying nothing, because its whole surface is syntax rather than
  members) so that no book is left outside the rule.

  ⚠ One thing this quietly fixed: `IsChancePulled` looked for a `BookType` in scope, so once the
  layer was what got bound it found nothing and every `a random number` would have refused
  itself. It asks by name over either shape now.

- **★ A writer's own module reaches inside a function written in its pull, as a book always
  could.** A pulled module is a lexical *capability*, not a local — it is in scope for everything
  written in that block. Both backends decided that by asking "is this a book", so a book
  survived into a detached body and a writer's module was dropped: interpreted it failed with
  *'kit' isn't defined*, and the checker refused it before anyone noticed the backends disagreed.

  They ask whether the binding came from a `Pull` now, which is what was meant all along. The
  asymmetry had been invisible because the only modules anyone wrote were bundled books.

- **A rabbit is an object, defined in Cufet — and a rabbit now passes as a `module` value.**
  Slice 4 of the 0.16.0 arc, which carried slice 5's headline with it: `given (the module m)`
  accepts a rabbit, a book, and a writer's own object on identical terms, on both backends. That
  was never a separate decision — a module is an object, an object is first class, so a module is
  first class **by inheritance**, and making the rabbit an object is what let the inheritance
  happen.

  Its definition lives in `Prelude/rabbit.cufe` and is one line: `Define object rabbit with ()
  and module.` That became possible only once `rabbit` stopped being a reserved word, and it is
  deliberately contentless — a rabbit has no fields, and its verbs are the language's floor
  rather than methods with Cufet bodies. `bury` suspends the function around it, which is a
  rewrite of that function rather than a call, so the compiler provides it exactly as it provides
  `If`; when the compiler is itself written in Cufet, that provision moves with it.

  - **`given (the rabbit r)` needs no special case anywhere.** `rabbit` is an identifier naming
    an object type, so it resolves down the same path as `person` or `stack of number` — the
    parser arm that produced a marker type is deleted rather than rerouted.
  - **`State hopper.` prints `rabbit()`**, not `<rabbit hopper>`. It used to print its
    *binding's* name, which nothing else in the language does — `Define x as 5. State x.` prints
    `5`, not `x`.
  - **Four ways to get a rabbit without its region are refused**: `a new rabbit { }`, redefining
    the name, `unto rabbit`, and `Pull a book on rabbit.` **`Pull` is the only constructor**, and
    for a rabbit that is load-bearing rather than tidy — pulling is what opens the region, so any
    other route hands back a rabbit standing on no ground.
  - The compiler binds a rabbit as the ordinary object struct the prelude already makes it
    declare. That is also what unblocked passing one to an interface parameter: monomorphization
    specialises on a concrete object type, and a marker type was not one.

- **`book` and `books` are no longer reserved words either.** `For each book in books,
  repeat:` is a line this language could not write — in a program about a library, which is the
  first thing anyone would try. Both words are ordinary identifiers now, and a writer may even
  define a module named `book` and pull it with `Pull book.`

  They appear in exactly **one** spelling, `Pull a book on <name>.`, and a word spent on a
  single construct is a name every writer loses forever. The `on` is what makes them decidable
  without reserving anything: `Pull a book on math.` and `Pull book.` differ in their *second*
  token, so one look settles which is meant. Both book spellings still read exactly as before.

  ⚠ The two book branches had to move **ahead of** the general `Pull <module>` form in the
  parser. That branch is gated on an identifier, and `book` is one now — it would otherwise
  swallow the word as a module's name and then meet `on` where it wanted a `.`

- **`rabbit` is no longer a reserved word.** It is a module's *name*, and no other module's
  name is reserved — `math`, `collections` and `chance` are ordinary identifiers and always
  were. What the books reserve is grammar (`book`, `books`, `on`), never identity; the rabbit
  was the one module whose own name was a keyword, and that was the last thing making it a
  privileged builtin rather than a module that ships in the box.

  `Pull rabbit.` now works, so the general form `Pull <name> [as <alias>]` reaches a rabbit on
  exactly the terms it reaches a book or a writer's own module. A writer may also use `rabbit`
  for their own names.

  ⚠ **Nothing else changed**: the word is still recognised where the parser needs it — pulling a
  rabbit opens a region, and `Have rabbit …` addresses the enclosing one — but *recognising a
  name is not reserving a word*. The whole suite passed unchanged, which is the proof.

- **★ A rabbit is never compared.** `hopper is grace` used to type-check, interpret to `false`,
  and emit C that gcc rejected. It is refused now, in the shared front end so both backends
  refuse alike — including `hopper is hopper`, since the refusal is about what a rabbit *is*
  rather than about which two you named.

  A rabbit denotes a region with a lifetime of its own, not a value that can match another.
  Refusing makes no claim and can become an answer the day something needs to tell rabbits
  apart; answering could not be taken back.

- **`math`'s two multi-word members are hyphenated — `square-root` and
  `absolute-value`.** `math's square root of (144)` is now `math's square-root of (144)`.

  The decision behind it is not about spelling. A book could name a member something a writer
  **cannot** name a member on their own module, because an identifier holds no spaces — one more
  way a bundled book was a privileged category, which is exactly what the 0.16.0 arc exists to
  delete. Hyphenated compounds are also what every multi-word name in Cufet already looks like
  (`add-edge`, `grand-total`, `parse-factor`, `how-many`); `square root` was the outlier.

  Three things fall out of it, and they are the reason it was worth doing:

  - **`absolute-value` is written in Cufet now**, since it is finally spellable. The whole of
    `math` is Cufet except `square-root`, `log` and `power` — the three double-backed
    transcendentals, which are the arc's remaining numerics work.
  - **The parser stopped guessing.** A possessive member name is ONE token again. It used to
    accumulate consecutive identifiers after `'s` — a greedy lookahead that existed solely to
    spell those two names, and that decided how much to swallow by scanning ahead. It is the
    same family as the `IsNamedAccessPattern` fragility the roadmap tracks, removed rather than
    bounded.
  - **~25 lines of dead C runtime are gone.** `cufet_math_floor`, `_ceiling`, `_round` and
    `_abs` had no call site left once those members became Cufet; they were still being emitted.
    The math runtime is now just the decimal↔double bridge the three transcendentals need.

  Sweeping the docs for the rename also turned up a sample that had **never** been valid —
  GRAMMAR showed `Define r as the square-root of 16.`, with no possessive, which is not how a
  book member is reached. Corrected, and the doc-block baseline regenerated: it now pins **190**
  samples, up from 186, the rest being blocks that had been checking clean since the generics
  docs landed without ever being recorded.

### Added

- **★ `math` is half in Cufet: `floor`, `ceiling`, `round`, and decimal-precise `pi` and `e`.**
  The start of slice 3 of the 0.16.0 arc. The three rounding members are pure decimal
  arithmetic in `Prelude/math.cufe` (`x % 1` is the fractional part; halves still round away
  from zero), with identical outputs to the native copies they delete. The constants are
  **getters on the book's Cufet layer** — the first layer getters, with the compiler routing
  `math's pi` to a getter call before any native constant — and they are now correct to 28
  fractional digits.

  - **`pi` and `e` print differently.** They were `(decimal)Math.PI` — double-derived,
    ~15 significant digits; they are now decimal-precise (`3.1415926535897932384626433833`).
    More digits, all of them right.
  - Still native, deliberately: `square root`, `log` and `power` (the arc's remaining
    pure-decimal numerics work) and `absolute value` — a **multi-word member name**, which a
    Cufet method cannot yet carry; how the layer spells multi-word members is an open decision
    the transcendentals also need.
  - Also fixed on the way: the CLI treats a `.cufe` file inside a `Prelude` directory AS the
    prelude — its own statements get the prelude's standing and the embedded copy is not
    prepended on top — so linting the language's own source no longer trips the bundled-book
    guards it exists to justify.

- **The whole `collections` book is written in Cufet.** Slice 2 of the 0.16.0 arc:
  `minimum`, `maximum`, `average` and `transpose` moved into `Prelude/collections.cufe` beside
  `unique`, and their native copies are deleted — the native side of the book now introduces the
  `matrix` type and nothing else. Behaviour is preserved exactly, pinned by the existing tests:
  first-of-ties for `minimum`/`maximum`, void on an empty series, and the exact-decimal average
  (one running sum, one division — `average of (0.1, 0.2, 0.3)` is still exactly `0.2`).

  - **The pre-blanks special dispatch path is gone entirely** (`IsCollectionsAggregateCast` and
    its bespoke inference). It existed because the aggregates' types could not be written before
    blanks; they can now, so the members are ordinary methods and the checker has one less way
    to call something. The bespoke educational errors it carried are replaced by the standard
    argument-type refusals.
  - **A book's Cufet layer checks with the book's own introduced types in scope** — `transpose`
    constructs a matrix, and `matrix` is otherwise only in scope inside a pull. Scoped to the
    book's own source: only the prelude can define an object under a book's name.

- **The first book member written in Cufet — `unique` — and the merge machinery every later
  one rides on.** Slice 1 of the 0.16.0 arc (*everything pullable is an object*; see ROADMAP).

  - **The prelude is real now.** `src/Interpreter/Prelude/collections.cufe` is embedded in the
    checker and prepended to every program, so both backends meet its definitions as ordinary
    statements. The hook existed since 0.15.0's generic-method work; this fills it.
  - **A book and its Cufet layer resolve as ONE module, member by member.** The prelude defines
    `Define object collections with () and module:` under the book's own name; a member the
    layer defines is ordinary (generic) method dispatch through the pulled binding, and
    everything it does not define — `transpose`, `minimum`, `maximum`, `average`, the `matrix`
    type — is still the native book's, through the same name. Half-migrated is a supported
    state, which is what lets the members move one at a time with the oracle watching.
  - **The native `unique` is deleted in the same change**, on the test-reaches-its-path rule: a
    shadowed native member would answer identically and prove nothing, so deletion is what makes
    every existing `unique` test proof that the Cufet path runs. Its pre-blanks special-case
    typing path (`IsCollectionsAggregateCast`) shrinks to the three numeric aggregates.
  - **A bundled book's name is refused as an object name.** `Define object
    math …` used to be legal and simply unpullable — the book silently shadowed it at the pull
    site. It is now refused at the definition with a message that names the book. The wall has
    no side doors: `a new collections { }` is refused too (**`Pull` is the only constructor** —
    a book is a scope-thing, and its construction is the bracket), and `unto` may not target a
    bundled book, which would otherwise splice a writer's member straight onto its Cufet layer.
  - Fixed in passing: the compiler resolved a pulled name against object definitions FIRST while
    the checker and interpreter tried the books first — a latent three-way divergence that the
    merge rule replaces with one order everywhere.
  - Fixed in passing, and latent since blanks shipped: **an unused template compiled to broken
    C.** Templates were stripped from the program only on the instantiation-splice path, so a
    generic method (or function, or object) that no call ever filled stayed in the emitted
    program with its blank as an unknown C type. The prelude turned that from an unlikely
    program into every program — the full suite caught it within seconds of the prelude landing
    — and templates are now dropped unconditionally at the end of the check.
  - Fixed in passing, same class: **a filling by a structural type emitted an illegal C name.**
    `unique of record (age: number, name: text)` is a fine member name — deliberately
    un-typeable by a writer, which is what makes it collision-proof — but the C-identifier
    mangler only flattened hyphens and spaces, so the parentheses and colons reached gcc.
    Exotic characters now flatten with a stable FNV-1a hash appended, so two different shapes
    can never collide into one symbol.
  - Fixed in passing, in the tests: **24 test helpers executed the program they PARSED, not the
    program `Check` returned.** The gap was recorded when stashes made Check return a program
    ("a helper that runs `parsed` instead silently tests nothing") and each new lowering got its
    own corrected helper — the old ones were left because unlowered stashes refuse loudly. The
    prelude is the first thing they miss silently, so five `unique` tests failed the moment it
    landed; every helper now runs the returned program.

### Changed

- **★ Compiled programs are optimized.** `build` passes `-O2`. Until now it passed no `-O` flag at
  all, so "compiles to a native binary" was delivering an unoptimized one.

  **Always on, with no opt-out** — the Go answer rather than the Rust one. There is no debug build
  and no `--release`, because the common failure of the opt-in design is someone benchmarking the
  default build, getting a bad number, and concluding the language is slow. Nothing is lost by
  having no flag: `emit-c` already hands over the source, and anyone who wants `-O0` — stepping
  through generated C in a debugger, triaging a suspected miscompilation — needs that source
  anyway. Measured cost to the test suite: 17 seconds.

  ⚠ `-O2` is what turns latent undefined behaviour into a wrong answer, so it ships with sanitizer
  coverage rather than on its own:

  - The sanitized test harness now compiles with `-fsanitize=address,undefined` and
    `-fno-sanitize-recover=undefined`, so a UBSan finding **aborts** instead of printing to stderr
    and letting a stdout-comparing test pass.
  - **Every example now runs under ASan + UBSan + LeakSanitizer on Linux**, as part of the oracle
    test that already built and ran them — so it costs no extra compiles. The examples are the only
    realistic programs the suite has; the other sanitized tests are small and aimed at features
    somebody already suspected.

  Both were clean on first run (Linux gcc 16.1.1), including the three POSIX-only examples that
  cannot build under mingw at all.

- **★ The C runtime is its own translation unit, not 950 lines pasted into every file.** `emit-c`
  now writes three files — your program, plus `cufet-runtime.c` and `cufet-runtime.h` beside it —
  and they still compile anywhere with `gcc out.c cufet-runtime.c -o program`.

  The point is readability and a missing rule, in that order. Emitted C was measured at **79% runtime
  for a typical example and 98.9% for a small one** (`fibonacci`: 578 bytes of program in a 51 KB
  file); `huffmancoding` went from 72 KB to 22 KB. And because a single file had to define every
  runtime symbol above its first use, the emitter carried an ordering rule per block — the direct
  cause of three "symbol emitted above its own declaration" defects in one session, each fixed by
  moving code rather than by fixing a rule. The fixed runtime is now emitted before anything
  generated, unconditionally, and that class is gone.

  `build` compiles the runtime once and caches the object under the user cache directory
  (`CUFET_CACHE_DIR` overrides; keyed by the runtime source, the gcc version and the flags, so a
  compiler upgrade invalidates it). **The cache is never required** — if it cannot be written the
  runtime is compiled alongside the program, so `gcc` remains the only thing anyone must install.
  Measured saving is ~150 ms of gcc's ~500 ms; the remaining ~220 ms is link overhead that caching
  cannot touch.

  Also fixed in passing: `build` wrote its intermediate `<name>.c` beside the source and deleted it
  afterwards, which destroyed a hand-written `<name>.c` if one existed. It uses a temporary
  directory now.

### Added

- **A METHOD can leave a blank, so a book can be written in Cufet.** A module's members are
  methods, so generic free functions were not enough on their own:

  ```
  Define object kit with () and module:
      Bind series of element to unique, given (the series of element xs):
          …
      Done.
  Done.

  Pull kit.
      State cast kit's unique on (a series of number with (1, 2, 2, 3, 1)).   → (1, 2, 3)
      State cast kit's unique on (a series of text with ("a", "b", "a")).     → (a, b)
  Done.
  ```

  Each filling becomes a real member under its filled name and is spliced onto the definition, so
  both backends emit it as an ordinary method. ⚠ Blanks on methods are detected only once every
  type NAME is registered — scanning earlier consults a half-built table, and a method taking a
  type defined further down the file would read as a blank rather than as the type it is.

- **A FUNCTION can leave a blank too.** The signature introduces it, and the call fills it from
  the arguments:

  ```
  Bind series of element to first-two, given (the series of element xs):
      Define out as a series of element.
      Insert the first of xs into out.
      Insert item 2 of xs into out.
      Return out.
  Done.

  State the number of (cast first-two on (nums)).     ← nums is a series of number
  State the first of (cast first-two on (words)).     ← words is a series of text
  ```

  A function has no slot to declare a blank in the way an object does, so its **signature** does it:
  a type name that names nothing, appearing at least **twice**. ⚠ Twice is the whole safety
  argument — a typo mentions its mistake once, so `given (the nubmer n)` stays an unknown type
  instead of quietly turning the function generic. Every real case uses its blank twice by nature,
  because the point is that two positions agree. `voidable element` works too, which is `minimum`'s
  and `maximum`'s shape.

  **This is the language's first real inference**, and it is deliberately the shallowest kind: one
  structural match per argument, no unification variables, no ordering, no backtracking. A blank
  either means the same type everywhere it appears or the call is refused by name — *"'pick' can't
  take both a number and a text for the same blank"*. A blank no argument mentions is refused too,
  since there is nothing to read it from.

- **A definition can leave a blank — `Define object stack of element`.** The writer names the
  blank, and `of` marks it: the slot after the type's own name.

  ```
  Define object stack of element with (the series of element items):
      Bind void to push, given (the element value):
          Insert value into one's items.
      Done.
  Done.

  Define counts as a new stack of number { the items a series of number }.
  Define names  as a new stack of text   { the items a series of text }.
  ```

  The use site needed nothing invented — `a stack of number` already reads like `a series of
  number`. Marking the blank by POSITION rather than by a keyword is what keeps a mistyped type name
  an error instead of quietly becoming a blank.

  **Filling happens in the front end.** `stack of number` becomes an ordinary definition named for
  its filling, spliced into the program, and the template is dropped before either backend runs —
  the same rule that lets no `stash of T` survive. So the interpreter sees an object and the
  compiler emits a struct, and the topological sort, the deep-copy family and the escape analysis
  all keep working untouched. Two fillings are two types, each with its own methods and its own
  element type, which is the point of monomorphizing rather than boxing.

  The filled-in name contains spaces (`stack of number`) deliberately: a writer cannot type one, so
  it cannot collide with anything they write.

  **More than one blank works**, since the writer names each: `Define object pair of left-thing of
  right-thing`, written `a pair of number of text`. Naming them is what makes that possible — a
  single fixed placeholder word could only ever have marked one.

- **A group of type tests narrows to the sub-union.** `x is a A or x is a B` now narrows `x` to
  `(A or B)` in the branch, and the `Otherwise` eliminates through it to whatever is left:

  ```
  If x is a number or x is a fact:
      Judge x, where it is:            ← no Otherwise needed: text is ruled out
          A number, state "n".
          A fact, state "f".
      Done.
  Done.
  Otherwise:
      State the length of x.           ← x is a text here
  Done.
  ```

  Every operand must be a positive test on the same name; a mixed disjunction narrows nothing. The
  two front ends keep the answer in different shapes and have to agree: the checker narrows to the
  sub-union type, while the compiler keeps a **set of indices** into the representation union —
  a sub-union's own case order need not match the subject's, so substituting a narrower type there
  would make every emitted member access index the wrong case.

- **A `bury` inside a judgement arm**, grouped arms and `Otherwise` included:

  ```
  Bind number to sizes, given (the rabbit helper, the series of (number or text) items):
      For each thing in items, repeat:
          Judge thing, where it is:
              A number, have helper bury it + 100.
              A text, have helper bury the length of it.
          Done.
      Done.
  Done.
  ```

  An arm carries two things across a resumption where an `If` arm carries one. The narrowing is a
  guard, as it already was for `If`. The **binding** is what made this different — `it` is not a
  condition that can be restated — so `it` becomes an ordinary local: it earns a hoisting slot, the
  subject is evaluated once, and every re-entry restores `it` from its slot rather than
  re-evaluating a subject that may have moved on.

  A **grouped** arm states itself as a disjunction, and an `Otherwise` names whichever cases the
  arms left — so both rest on the narrowing above rather than on anything stash-specific.

  Two shapes are refused. A judgement on a **non-union** subject cannot have a burying `Otherwise`,
  because the leftover cases have to be named and only a closed union lists them. And a **nested**
  `Judge` rebinds `it` at a narrower type, which the existing one-name-one-type rule for burying
  bodies already refuses — bind the inner subject to a name of its own.

- **A function-valued object field.** `the number function twice given (a number)` now writes in an
  object or record-shape header, on both backends:

  ```
  Define object box with (the number function twice given (a number), the void function log).

  Define b as a new box {
      the twice a function given (the number x): Return x * 2. Done,
      the log   a function: State "logged". Done
  }.

  Define t as the twice of b.
  State cast t on (6).
  ```

  The emitter already handled this — a `stash of T` field, which shipped earlier, normalises to the
  same `FunctionType` — so the missing half was purely the parser. The postfix `function` is not
  part of `ParseTypeAnnotation` (a bare `void` is not a type), so each position that admits a
  function type consumes it itself, and the field header was the one that never did. The field NAME
  sits between `function` and `given (…)`, the same order a function-typed parameter uses, so the
  type cannot be completed before the name is read. `void` is accepted there only as a return type.

- **Modules — a writer's own object can be `Pull`ed.** A module is an object that says it is
  one, and `Pull` brings it into scope. A book is a module that ships with the language; there is
  no privileged category any more.

  ```
  Define object greeting-kit with () and module:
      Bind text to greet, given (the text who):
          Return "hello, " joined to who.
      Done.
  Done.

  Pull greeting-kit.
      State cast greeting-kit's greet on ("world").
  Done.
  ```

  **It is not new syntax.** `Pull a rabbit.` was always `Pull <name>`, and articles are noise, so
  `Pull greeting-kit.`, `Pull a greeting-kit.` and `Pull greeting-kit as kit.` are one form. What
  changed is that the name no longer has to be one the language shipped. `Pull a book on <name>`
  stays for the bundled three, whose names read badly without the noun.

  `module` is a **marker interface** — it requires no methods, only the claim, so that being
  pullable is something an author declares rather than something every object accidentally has. An
  object that does not conform is refused at the pull site, and the message names the fix. No
  requirement will be added to it until a real one arises; a contract is the hardest thing to
  loosen once things depend on it.

  Pulling **instantiates**, matching `Pull a rabbit as den.`, which keeps a book's singleton-ness a
  property of books rather than of the mechanism. A module with fields is refused — a pull site has
  nowhere to put their values.

  **The bundled books answer to the same form** — `Pull math.`, `Pull collections as c.` — which is
  what makes "a book is a module" a fact rather than a turn of phrase. One question is asked of
  everything at the pull site: *is this a module?* A book conforms by CONSTRUCTION (its members are
  native, so there is no `Define object` to carry an `and module` clause); a writer's object
  conforms by DECLARATION. That is a difference in how a conformer is built, not in what the
  contract asks.

  ⚠ **Rabbits are not a conformer yet.** `Pull a rabbit` still travels its own branch, because a
  rabbit is not an object — which is the next piece, and the one that puts `bury` and `unbury`
  where they belong.

- * Stashes — `Bury` and `unbury`.** A function can stop in the middle of what it is doing, hand
  one value out, and pick up from that exact line when someone asks for the next one.

  ```
  Bind number to counting-up, given (the number first-value):
      Define next as first-value.
      Repeat:
          Bury next.
          The next becomes next + 1.
      Until false.
  Done.

  Define counter as cast counting-up on (3).
  State unbury counter.       // 3
  State unbury counter.       // 4
  ```

  **Two words and a type name, and nothing marks the declaration.** A function becomes
  stash-producing by *containing* a `bury`, exactly as a body containing `return a failure` makes it
  fallible; `cast` still means invoke, and its result type becomes `stash of T`. `unbury s` gives
  back `voidable T`, so a spent stash reports itself as void and there is no second way to ask.

  A stash is not a collection. A series *has* its items; a stash *produces* them, one resumption at
  a time, and cannot be counted, indexed or re-read — which is what lets `counting-up` above be
  endless without being a mistake.

  `If`, `While`, `Repeat until`, `For each` over a series, `Stop` and `Skip` all work inside a
  burying body: it is rewritten into a state machine whose step number is the program counter, and
  every local that outlives a resumption is stored beside it. Loop counters, iterators and
  part-built series all survive.

  **A stash is a VALUE, not a special form.** It goes wherever a value goes — a local, a parameter,
  an element of a series — and that is what lets one stash *delegate* to another:

  ```
  Bind void to take-three, given (the stash of number source, the text label): … Done.

  Define many as a series of stash of number.
  Insert (cast counting-up on (1)) into many.
  ```

  No vtable is implied, because a stash lowers to a **closure** — two pointers, one uniform shape,
  so every `stash of T` is the same size. The front end keeps `stash of T` and its closure type as
  distinct spellings (it is what makes an error say "stash of number", and what stops a stash being
  `cast` directly rather than unburied) and substitutes one for the other on the way out, so no
  `StashType` reaches either backend.

  ⚠ **Not yet:** a `stash of T` as an object FIELD interprets but does not compile — the generated
  C declares object structs before closure structs and the two can refer to each other, so it needs
  forward declarations that are not emitted. And a bury inside a judgement, a `Try`, a rabbit block,
  a for-each over a map, or an `If` that tests a type is refused at check time, with a message
  saying which and why: each carries something — a narrowing, a handler, a region — that a step
  number cannot restore, and a refusal both backends share is the only answer the no-divergence
  rule allows.

  The naming is Turing's: the ACE design used *bury* and *unbury* for subroutine linkage.

- **★ An interface can supply a DEFAULT method** — most of what traits give, with no new keyword
  and no new construct. `Bind <type> to <name> unto <interface>` was a static error; it now gives
  every conforming type that method.

  ```
  Bind text to describe unto shape:
      Return "area {(cast area on one) converted to text}".
  Done.
  ```

  A conformer that writes no `describe` still has one. Inside a default, `one` is the *conforming
  object* — so a default reaches that type's own fields, and specialises per conformer.

  **A default satisfies conformance**, so an interface's method list is what a conformer ends up
  with rather than what it must write. A type's own method beats the default, whether nested,
  `unto`, or promoted through an embedded type. Two interfaces supplying the same defaulted name
  to one type is refused unless that type writes its own. Interfaces still do not conform to
  interfaces.

  Monomorphization is untouched: the defaults are expanded into ordinary per-conformer methods in
  the parser, so the type checker, interpreter and code generator never learn the feature exists —
  no vtable, no type tag, nothing relaxed.

### Fixed

- **An object used as a series element brings its own field types.** This interprets and would not
  compile — no generics involved:

  ```
  Define object holder with (the series of number items).
  Define many as a series of holder.
  State the number of many.
  ```

  `RegisterNestedRecords` recursed into a **record's** field types but had no case for an object, so
  registering `series of holder` never registered the `series of number` inside it, and the emitted
  struct referenced an undeclared one. What hid it: the discovery pass registers the inner series
  whenever the body touches it, which nearly every program does — it takes a field the program never
  *reads* for the gap to show. Found while probing generic objects, which have that shape.

- **The type-substitution walk no longer chokes on an enum.** Four enums live in the AST's namespace
  (`ReadForm`, `FileReadForm`, `PathCheckKind`, `OpenMode`), an enum has no constructor, and the
  walk's rebuild called `GetConstructors().First()` on one — so the type checker threw *"Sequence
  contains no elements"* instead of checking the program.

  ⚠ Reachable before this release: the walk runs for any program containing a `bury`, so a burying
  program that also opened a file or read `the input` would have crashed. It survived because those
  two features were each well covered and never crossed in one test.

- **The `Otherwise` of a negated type test now narrows.** `If x is not a text: … Otherwise: …`
  reaches its `Otherwise` exactly when x IS a text, and both front ends now say so:

  ```
  Bind void to show, given (the (number or text) v):
      If v is not a text:
          State v + 1.
      Done.
      Otherwise:
          State the length of v.
      Done.
  Done.
  ```

  This had to land in both at once. The checker narrowed a negated test's then-branch only, so the
  program was rejected before either backend saw it; the compiler's else-arm narrowing additionally
  required every arm to be un-negated, so fixing the checker alone would have emitted C reading the
  subject at its full union type. It narrows for a **lone** arm only — with several arms, reaching
  the else no longer implies this test was the one that failed.

- **Burying is commanded: `Have <rabbit> bury <value>.`** There is no bare `Bury x.`
  any more. A rabbit is the agent you summon to do memory work, burying *is* memory work, so a
  rabbit does it and a burying function takes one as a parameter.

  ```
  Bind number to counting-up, given (the rabbit helper, the number first-value):
      Define next as first-value.
      Repeat:
          Have helper bury next.
          The next becomes next + 1.
      Until false.
  Done.

  Pull a rabbit as den.
      Define counter as cast counting-up on (den, 3).
      State unbury counter.       // 3
  Done.
  ```

  **The point is where the ownership lives.** A stash's state has to sit in some region, and it
  always did — the region simply happened to be whichever one was current. Now the agent that owns
  it is named at the call site, so "this stash belongs to that rabbit and dies when it does" is
  something you can read rather than something you have to know.

  `unbury` does NOT take a rabbit: `unbury s` already names the stash, and the stash knows its own
  ground. `bury` and `unbury` also do not match in shape — one is a statement, the other an
  expression (`Define x as unbury s.`) — which is honest about their being different kinds of thing.

  ⚠ The bare `Bury` spelling is kept in the parser for one purpose: to say what to write instead.

- **★ A rabbit is a value the compiler can represent.** `State den.` and `given (the rabbit r)`
  type-checked and interpreted, and had **no C representation at all** — `RabbitType` appeared once
  in the whole code generator, in the function that formats type names for error messages. Nothing
  had noticed, because nothing had ever passed a rabbit anywhere.

  ```
  Bind void to inspect, given (the rabbit r):
      State r.
  Done.

  Pull a rabbit as den.
      Cast inspect on (den).       // <rabbit den>
  Done.
  ```

  The representation matches the interpreter rather than improving on it: a rabbit is its **name and
  nothing else**, exactly as `RabbitValue` is. Giving it the arena depth was tempting — the compiler
  identifies regions by depth, and rabbit-scoped pointers will want it — but inventing state the
  oracle does not have is how two backends drift. It goes in when something reads it.

  Also fixed at the same time: a named rabbit's name was known to the checker and to nothing else,
  so the pull site now binds it.

- **Tasks and channels compile and run on Windows.** They had been POSIX-only since the
  concurrency arc shipped, and the recorded reason — "mingw has no pthreads" — was simply not true:
  **mingw-w64 ships winpthreads**, and the bundled gcc compiles and runs a pthreads program fine.

  What actually blocked it was in our own emitted C. The threading runtime was fenced to
  `__unix__ || __APPLE__`, so on Windows none of it was emitted while the generator still produced
  task code referring to `pthread_t`; and that runtime leans on the interrupt landing pad, which
  existed only in the POSIX branch. Widening the fence and giving the mingw branch a plain `setjmp`
  pad (behind one `CUFET_SETJMP` macro) was the whole fix.

  Windows keeps its documented interrupt behaviour: no POSIX signals, so the pad is never jumped to,
  `setjmp` returns 0, the body runs, and Ctrl-C stays default-terminate.

  `parallelsum` and `channel-deepcopy` now match the interpreter exactly on Windows; `work-queue`
  differs only in how work splits across workers, which is the non-determinism already documented
  for Linux (real threads share out, a cooperative scheduler does not) with identical totals. All
  three came off the Windows skip list, which now holds only `fork`/`exec` programs — genuinely
  POSIX, with no mingw equivalent to reach for.

- **★ A rabbit can be given work BY NAME.** `Have den start a task as job: … Done.` — previously
  only the bare `rabbit` keyword was accepted, which made `Pull a rabbit as den.` half-wired: the
  name bound a value you could print and pass, but not the one form that actually takes a rabbit.

  A rabbit is an agent you summon and hand a job to, so naming one where you give it work is what
  the name is for. The keyword still means "the enclosing one" and the two forms mix freely.

  ⚠ Naming a rabbit pulled further out is refused, and that is a lifetime rule rather than a
  spelling one: a task is joined by its rabbit's `Done.`, so an outer rabbit's task would have to
  outlive the block it was written in. That is a real feature and not one to acquire by accident
  while adding a name.

- **★ `is not a <type>` narrows in the compiled backend.** A divergence in ordinary code, with no
  stash, module or closure involved:

  ```
  Define things as a series of (number or text) with (1, "two").
  For each thing in things, repeat:
      If thing is not a text:
          State "number: " joined to (thing converted to text).
      Done.
  Done.
  ```

  The checker narrows that arm, so the program interprets. The compiler did not, so it read `thing`
  at the full union type and the generated C would not build. A negated test is elimination applied
  to the *then* branch, which is the mirror of the else-arm narrowing already there — so it now uses
  the same reachable-case set and the same payload access.

  ⚠ The `Otherwise` of a negated test still does not narrow, and that one is a FRONT-END asymmetry:
  the checker narrows `is not a <type>` in the then-branch only, so such a program is rejected
  before either backend sees it.

- **★ A `Bury` inside a type test keeps its narrowing.** `If thing is a text: Bury thing. Done.`
  inside a burying function was refused; it now works on both backends.

  Splitting an `If` arm into its own resumable block left the narrowing behind — the block resumed
  with the subject back at its declared union type, and the generated C would not build. Each block
  now records the conditions it was entered under and **re-tests them on entry**. That is not a real
  branch: every hoisted local is restored from its slot first, so the subject holds exactly what it
  held when the arm was chosen and the condition gives exactly the answer it gave then. It is a
  restatement for the type checker and the code generator, not a decision.

  The `Otherwise` of a type test works too, guarded by the negated test — which is only possible
  because of the compiler fix above. It was refused until that landed, and then lifted with **no
  stash-specific code at all**: a refusal whose stated reason lives in another component is one that
  disappears when that component is fixed.

- **★ A closure — and so a stash — can be an object FIELD.** It interpreted correctly and refused to
  compile, with gcc reporting `unknown type name 'cfn_0'` under a "this is a bug in the Cufet
  compiler" banner.

  The cause was emission ORDER, not representation. Object structs were written in one phase and
  closure structs in a later one, so an object holding a closure named a type that did not exist
  yet. The dependency runs both ways — a closure's parameter may be a record, a record's field may
  be a closure — so two fixed phases cannot express it, and a forward declaration cannot help
  either, because a by-value struct member needs a complete type. Closures now take part in the
  **same topological sort** as records, objects, voidables, failables and unions.

  ⚠ Still not writable: an ordinary function-valued field (`the number function maker`) is rejected
  by the parser, which is a separate gap from the one fixed here.

- **`check --native` handed the code generator the wrong program.** It ran the generator on the tree
  as WRITTEN rather than the one that runs, so every correct stash program came back with an
  internal "a 'bury' reached the code generator untransformed". The linter still reads the original
  — style advice is about what you wrote — and only the back end gets the lowering.

- **★ `in uppercase` / `in lowercase` are full Unicode on both backends.** `"héllo" in uppercase`
  was `HÉLLO` interpreted and `HéLLO` compiled — the emitted C cased one byte at a time, so
  everything outside `A–Z` passed through. This was the last known divergence.

  Both backends now read **one** generated table (`src/Interpreter/CaseTableData.cs`, from
  `tools/gen-case-table.cs`) rather than casing text twice. Generating the C table from .NET would
  have made them agree only at generation time, since the interpreter would still ask ICU at run
  time; sharing the table makes drift impossible instead of detectable, and pins casing to a stated
  Unicode version rather than to whichever .NET is installed.

  Full coverage rather than a documented subset: the mapping run-length-encodes into 380 runs, so
  carrying all 2,877 mappings is cheaper than any boundary would have been. The table is emitted
  only into programs that actually case text.

  Note this is *simple* case mapping, and strictly 1:1 — `ß` stays `ß`, the `ﬁ` ligature stays
  whole, and invariant rules leave the Turkish pair `ı`/`İ` alone rather than picking a locale.

## [0.15.0] — 2026-08-12

### Changed

- **★ `Add <x> to <series>` is now `Insert <x> into <series>`.** There is no alias.

  ```
  Insert 100 into scores.
  Insert 100 into the start of scores.
  Insert 100 after the second item of scores.
  ```

  `Add 1 to tally.` read as arithmetic when the elements were numbers, and `insert` is the verb
  that actually covers all four positional forms. `into` rather than `to` because `in` is an
  expression operator (`in uppercase`) — with `in` as the separator, `Insert word in uppercase in
  words.` would have no readable boundary. `add` is a free identifier again; `insert` and `into`
  are now reserved.

### Added

- **★ A bits value's width is data.** Read it with `the width of p`; state one with
  `<value> at <n> bits`.

  ```
  State the width of 0x0F.        → 8
  State 0b0 at 3 bits.            → 0b000
  State 0b101 at 8 bits.          → 0b00000101
  ```

  A width was always carried — it drives the leading zeros when a value prints — but nothing could
  read it, and it was only ever raised to fit the value, so `0b0 shifted left by 2` stayed `0b0`.
  Widening always works; narrowing is refused when it would drop a set bit, at check time when both
  the value and the width are literal and at run time otherwise.

  **No new reserved words.** `the width of p` already parsed as a named-field access and is resolved
  in the type checker, so `width` is still a legal field name; `at` and `bits` are matched by lexeme
  in that position, as `item at (r, c)` already is.

- **★ `Increment` / `Decrement` — self-referential arithmetic that names the target once.**

  ```
  Increment i by 1.
  Decrement remaining by 1.
  Increment one's tally by 3.
  Increment total by item at (rr, cc) of board.
  ```

  35% of the `becomes` statements in `examples/` were `X becomes X + …`, which is where a typo
  hides. Pure parser sugar for `X becomes X + <amount>`, so no backend changed. The amount is any
  expression; the target must be a plain name or a possessive chain, since the desugaring names it
  twice. Numeric only — growing a series is `Insert`. 41 statements in `examples/` now use it.

  Not `Increase`/`Decrease`: every keyword is barred from being an identifier, and `increase` is an
  everyday noun of the kind that already costs users names.

- **★ Inline forms for every block construct — one rule.** A **comma** takes one thing; a **colon**
  takes a block closed by `Done.` `If` and `Judge` already worked this way; functions, getters,
  setters, constructors, operator overloads, destructors and loops now do too.

  ```
  Bind number to double, given (the number amount), amount * 2.
  Get area as number, one's radius * one's radius * 3.
  Set radius given (the number r), one's radius becomes r.
  For each n in items, State n.
  ```

  A body that returns a value takes an **expression**, with `Return` implicit; a void body takes a
  **statement**. Loops are separated by `repeat:` rather than the comma, which their header already
  uses. `Try`, `Pull a rabbit` and lambdas are outside the rule — a lambda sits in argument lists
  where the comma is already the separator.

  An inline body parses to an ordinary one-statement body, so the AST, both backends and every
  existing program are unaffected.

- **★ Shared constants — a top-level `permanently` binding is visible inside any function or
  method.**

  ```
  Define max-retries as 3 permanently.
  Bind number to budget:
      Return max-retries * 2.       ← an error before this
  Done.
  ```

  **The rule was broader than its own justification.** Top-level functions could not see top-level
  data, to keep data flow explicit and prevent hidden mutation — the error message said so. But a
  `permanently` binding **cannot be mutated**, so none of that reasoning applied to it. Only the
  immutable half comes back; an ordinary top-level binding is still refused, because that is the
  hidden global mutable state the rule exists to prevent.

  This is what `static` would have been, minus the part worth refusing. Static *methods* already
  exist as top-level functions and static *factories* as named constructors; only shared data was
  missing, and only its immutable half should return.

  **Every detached body reads them, not just top-level functions** — methods, getters, setters,
  destructors, operator overloads and pipe stages all leave the top-level scope in exactly the way
  a function does, so they all see exactly what a function sees. Each backend now states that rule
  in one place rather than at each body kind: `TypeChecker.ImportTopLevelVisible` decides what is
  legal, `Interpreter.ImportTopLevelVisible` keeps the runtime in step, and
  `CodeGenerator.SeedSharedConstantTypes` gives the C generator the constant's type. A lambda
  literal is deliberately excluded — it *captures* its enclosing scope, so nothing is hidden from
  it in the first place.

  **The mutable half is now refused by the checker in those bodies too.** It was hidden but
  unresolved, and an unresolved name infers to nothing, so a method reading top-level data passed
  `check` in silence and failed later — at run time interpreted, at `gcc` compiled, with the
  compiler blaming itself for a program that was simply invalid.

  **Compiled, a shared constant is a file-scope global assigned at the top of `main`** — not a
  local of `main`, which no function could see. Declared rather than initialised in place because
  a Cufet initialiser is not a C constant expression (a number is built by `cufet_dec_lit`), and
  assigning at the top of `main` is safe since nothing can call a function before `main` starts.
  The compiler identifies them **by reference, not by name**, so a `permanently` local deeper in
  the program may share a name and stays a local. Their *types* are registered before any body is
  emitted, since bodies emit before `main`: without that a text constant fell back to number and
  `State greeting` emitted `cufet_print_number` on a `const char*`.

  Pairs with read-only fields below — both are `permanently` earning its keep.

- **★ Read-only fields — `permanently` on an object field.**

  ```
  Define object user with (the text id permanently, the text name).
  Define alice as a new user { the id "u-1", the name "Alice" }.
  The alice's id becomes "u-2".        ← refused
  ```

  Set when the object is made, never written after. **Nothing else in the language expressed that
  invariant.** A setter cannot: setters are infallible and transform-only, so one guarding an id
  could only ignore a bad write, never reject it — which is worse than no protection, because it
  looks like protection.

  It reuses a word rather than importing one. `permanently` already fixes a binding and is already
  documented as shallow — it fixes the binding, not the contents — and a field carries the same
  rule, so there is no `readonly` or `final` to learn.

  The refusal covers every route into the field, each of which would otherwise have been a silent
  way around it:
  - **From inside the type's own method** — `one's id becomes …`, the write its author is most
    likely to reach for.
  - **Through an embed.** A promoted field belongs to the embedded type while the write goes
    through the outer object, whose own permanent set says nothing about it. Checking only the
    outer type would have made embedding a laundering route.
  - **Through a setter.** Checked *before* the setter branch, so declaring a setter cannot turn a
    permanent field back into a mutable one.

  Pairs with the conditional expression above: `a new account { the fee 0 when member is true,
  otherwise 25 }` — `when` supplies the value and `permanently` fixes it. Before both landed there
  was no way to choose a permanent field's value at all.

  Carried as a **name-keyed set** beside `NamedFields` rather than folded into that tuple, which is
  read in ~98 places across 14 files; a name set cannot fall out of step with the field list the
  way a parallel positional list could. Four separate places rebuild `ObjectType` and all four
  forward it — dropping any one loses the feature silently.

- **★ A conditional expression — `<value> when <condition>, otherwise <value>`.**

  ```
  Define label as "item" when count is 1, otherwise "items".
  Define fee as 0 when member is true, otherwise 25 permanently.
  ```

  **This closes a hole rather than adding a second spelling.** A value depending on a condition
  previously had to be declared and then mutated — `Define label as "items".` followed by
  `If count is 1, the label becomes "item".` — which forces a **mutable** binding. So a
  `permanently` binding could not be conditionally initialised **at all**: immutability was
  unavailable at exactly the point a value depends on a condition. Nothing else in the language
  does this job.

  `when` is the only new reserved word; `otherwise` already was one, and the comma before it is
  what `If x is 1, state "one".` already does.

  - **Exactly one arm evaluates**, on both backends — a C ternary compiled, a single `Evaluate`
    interpreted. A call or a `State` in the untaken arm does not happen, tested with the effect on
    each side in turn.
  - **`, otherwise` is mandatory, and that is what removes the ambiguity.** A comma inside an
    argument or element list cannot be confused with the conditional's, because a half-written
    `f(x when c, y)` has no valid reading as two arguments — it is an unfinished conditional and
    says so. No lookahead, no backtracking. So `("small" when n is 1, otherwise "big", "fixed")`
    is deterministically two elements. Legal, and left legal: it reads worse than naming the value
    first, but that is a style question, not a grammar one.
  - **Binds loosest of everything**, including `but void is` and `but on failure`, so it always
    chooses between two whole values.
  - **Right-associative**, so `a when p, otherwise b when q, otherwise c` is a fallback ladder.
  - **The arms may differ in type**, giving their union — the same inference
    `a catalogue with (1, "two")` already performs. Refusing here would have made the conditional
    narrower than the collection literal beside it, and would have reopened the hole for every
    pair of arms that did not happen to match. Strictness stays available through the existing
    annotation: `Define the number fee as 0 when member is true, otherwise 25.`

- **★ Two tests for gaps found by the first mutation run measured against the WHOLE suite.**
  Mutation sampling had only ever run on Windows, where the 73 pthread tests do not exist, so
  every previous score was taken against 87% of the suite. Run on Linux — same method, same fixed
  seed, 20 mutants — it caught **13 of 18** (two more did not compile and are discarded, since a
  fault the compiler rejects is not one the suite could have caught).

  Four survived. Two are **equivalent mutants**, catchable by nothing: `power > 28` → `>= 28`
  assigns 28 to an `int` already holding 28, and `ValuesEqual`'s null guard is unreachable — a
  probe that threw on that line ran all 1947 tests without firing. Discounting those, **13/16**.

  The other two were real, and both hid the same way — **the code was emitted but never called**:
  - `cufet_parse_number`'s overflow guard, `coef > max96`, where `max96` is 2⁹⁶−1. Nothing
    converted text at either side of the decimal maximum, so the boundary could move by one digit
    of magnitude and stay green. Moving it makes the interpreter parse
    `79228162514264337593543950335` while the compiled binary calls it unparseable — a divergence
    `check --native` reports nothing about.
  - `EqCall`'s `FunctionType` arm, which compares a function value's `fn` and `env` pointers.
    `Closure_SeriesOfFunctions` builds a series of functions and its own comment says function
    equality was "added for the series" — but it only ever *casts* an element, never compares the
    series, so the arm was emitted and never reached. Comparing two series built from the same
    functions is what reaches it.

  Both verified by reintroducing the mutation and watching the new test fail.

- **The mutation harness runs on Linux.** Process-group kill (`setsid` + `killpg`) rather than
  `taskkill /T /F`, an ext4 workspace under `$HOME` rather than the 9p mount, and a **baseline
  gate**: all three tiers must pass on the unmutated copy before anything is scored, because a
  suite that is red for an unrelated reason scores every mutant as caught and reports a perfect
  number that means nothing. It still mutates a COPY; the live repo is only ever read.

  One mutant **HUNG** rather than being caught: making `void is void` return false wedges the
  interpreter suite instead of failing it. That is its own verdict — not caught, since nothing
  reports, but not silent either. In CI it looks like a hang, not a failure.

### Fixed

- **A top-level `Define`d lambda could not be called from a function or a method when compiled**
  — `'doubler': unresolved call`, while the interpreter ran it. It was emitted as a local of `main`,
  so no other function could reach it. Now hoisted to file scope like a shared constant. Aliases
  (`Define f as doubler.`) too.

- **A shared constant of series or map type did not compile at all** — its declaration named a
  generated C type and was emitted above that type's definition. Scalars were unaffected, which is
  why it went unnoticed.

- **One program, three answers: a top-level function reading top-level data.** The rule — top
  level functions see other functions but not top-level data — lived only in the **interpreter**,
  and only at **run time**. So:

  | | |
  |---|---|
  | `cufet check` | "No problems found" |
  | interpreted | refused, with a good teaching message |
  | compiled | `cv_max_retries` undeclared → **"★ This is a bug in the Cufet compiler, not in your program."** |

  The compiled message is the worst part. It is *technically* true — the compiler should not have
  emitted that C — but it sends the reader hunting for a defect in Cufet when their program was
  invalid and three tools failed to say so.

  It hid because isolating the scope was silent: the checker removes top-level data from a
  top-level function's scope, and an unresolved name **infers to null rather than erroring**, so
  the check passed with nothing to report.

  Fixed by recording which names the isolation removed and refusing a reference to one **at check
  time**, with the interpreter's original wording — which was already the clearest thing about
  this. Both backends run the checker, so they now refuse identically and `check` regains its
  contract of catching what will not run.

  A genuinely undefined name is deliberately left alone: it still infers to null and is reported
  by the interpreter, because "never defined anywhere" is a different case from "deliberately
  hidden from this scope". `GenuineUndefined_GivesPlainError` stays on the runtime path to prove
  the new refusal is specific rather than catching every unresolvable name.

  Groundwork for shared constants, which is now just relaxing this one static rule for
  `permanently` bindings.

- **The test suite could hang forever, on both backends, for one reason: "no input supplied"
  silently meant "inherit whatever the host has" instead of "EOF".** Found by running the suite
  interactively through `wsl.exe`, which is the only launch that hands it a live pipe — a pipe
  nobody writes to and nobody closes. Measured: **2h15m** with a single compiled binary parked in
  `pipe_read` having used zero CPU, the whole run stopped behind it.

  It could not appear on Windows, where the inherited handle gives EOF. It could not appear in CI
  or under the mutation harness, both of which redirect from `/dev/null`. Only a real terminal
  exposed it, which is why a green CI and a green local run had both been true for months.

  The same mistake existed once per backend, reached by different mechanisms:
  - **Compiled** — six places start a compiled binary, each with its own hand-rolled
    `ProcessStartInfo`, and five did not redirect stdin, so the child inherited the test host's.
    All six now redirect and close it; the close is what turns a read into EOF.
  - **Interpreted** — `Interpreter`'s constructor does `_in = input ?? Console.In`, which is
    correct for the CLI and wrong for a test, and ~30 helpers build an interpreter with no reader.
    Fixed **once**, with a `[ModuleInitializer]` per test assembly setting `Console.SetIn(
    TextReader.Null)`, rather than at 30 call sites — so a helper written tomorrow inherits it.

- **A guard so launcher number seven cannot forget.** `EveryCompiledBinaryLauncher_ClosesStdin`
  fails when a `new ProcessStartInfo(binPath)` appears without redirecting and closing stdin, and
  names the file and line. Verified by reverting one launcher. Patching five of six by hand is the
  same shape as the arm-record walk bug — one rule, N copies, one forgotten, silent when wrong —
  so it gets the same treatment.

---

## [0.14.0] — 2026-08-09

0.13.0 made Cufet account for every case. **0.14.0 makes the two backends prove they
agree.** Every example is now an oracle test, the whole suite runs on Linux, and the
harness that resulted found two places where a compiled program and an interpreted one
had been quietly disagreeing. The language gained verbatim text and a writable matrix
cell; most of the work went underneath.

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

- **★ A structural guard against the arm-record walk bug — the most-repeated bug in this codebase,
  seven instances.** A generic walk that descends with `if (child is IExpression or IStatement)`
  reads as complete and is not: `ConditionArm` (every `If`/`Otherwise` arm) and `JudgeArm` implement
  neither interface, so it steps over the condition *and* the body of every `If` and every
  judgement. Two of the seven shipped as live divergences.

  All six reflection walks in `src/` are correct today; the point is the seventh. Two tests, in
  `ExhaustivenessTests`:
  - `ReflectionWalks_DoNotGateDescentOnTheInterfaces` — finds every member containing the walk
    fingerprint (`GetType().GetProperties()`) and fails if it gates descent on either interface.
    Gating on the namespace is right; gating on **nothing** is also right, and two walks do that —
    descending into everything only over-approximates, which is the safe direction. Members are
    delimited by declaration lines at class indentation rather than by brace matching, because
    `CodeGenerator.cs` emits C and its string literals are full of braces.
  - `EveryReflectionWalk_IsAccountedFor` — a seventh walk fails until it is shown to see inside
    `ConditionArm` and `JudgeArm`, and the message says how to write that test.

  Both verified by reintroducing the bug: the interface gate put back in `CollectRefsDefs` names the
  member, line, and matched text; a dummy seventh walk fails the inventory.

- **Two tests for open-union discovery inside arms**, closing the last of the three walks that
  carried the hole to have no behavioural coverage. A catalogue first mentioned inside an `If` arm
  and inside a `Judge` arm — mentioned *nowhere else*, since a program that also names one at the
  top level is rescued by that other mention and stays green with the bug back. Verified: with the
  interface gate restored in `ProgramUsesOpenUnion` the pre-pass never runs, `cun_open` is emitted
  with an empty case set, and gcc rejects it.

### Changed

- **★ CI now runs the whole suite on Linux — `.github/workflows/full-suite.yml`.**

  **73 of the 549 compiler tests — 13% — have never run automatically.** They open with
  `if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;` because subprocesses,
  concurrency, signals and every AddressSanitizer sweep need pthreads, fork/exec and sigaction,
  which the mingw toolchain on the development machine does not have. Five of the twenty-six
  examples are skipped there for the same reason. Until now they ran only when someone remembered
  to go through WSL by hand.

  That was the largest blind spot left — not a thin patch of coverage but an entire platform, and
  the one where the riskiest code lives: a fault in arena escape, in a pthread's lifetime, or in
  fork/exec is a memory-safety bug rather than a wrong answer.

  It also qualifies the mutation-testing figure below. 14/18 was measured on a machine where 13%
  of the suite does not execute, and one survivor sat in a POSIX `poll` loop Windows never reaches.

  **It found something before its first run.** WSL was reachable after all — the failure earlier
  was `wsl` resolving to the zero-byte `WindowsApps` execution-alias stub rather than the real
  `C:\Windows\System32\wsl.exe`. There is no dotnet inside WSL, so the xUnit suite still cannot be
  run there, but `emit-c` plus real Linux gcc previews the codegen half. All five POSIX-only
  examples **compile** under gcc 15.1.1, and `channel-deepcopy` and `parallelsum` match the
  interpreter exactly. `work-queue.cufe` does not, and never will: the totals agree (930, every
  item processed exactly once) while the per-worker split is the scheduler's to choose — 30/0/0
  from the interpreter's cooperative scheduler against 9/17/4 from real pthreads. That is the
  oracle rule's narrow platform-owned exception, so it joins `markov` under the weaker bar of
  building and running cleanly on both backends.

  Still unpreviewable from here: `shell.cufe` and `subprocess-pipes.cufe`, whose *interpreted*
  output is itself platform-dependent (`cmd` versus `sh`), so Windows cannot stand in for the
  comparison CI will make. If the first run finds anything, expect it there.

  Deliberately its own workflow rather than part of the deploy: `playground.yml` gates publishing
  the site, and a failure in newly-exercised tests must not stand between a fix and a published
  page. **Expect red before green** — 73 tests are about to run for the first time, and failures
  there are the return on building it. Every job in both workflows now carries `timeout-minutes`,
  because mutation testing measured that 1 in 23 injected faults makes a test *hang* rather than
  fail, and GitHub's default job limit is six hours.

- **★ The test suite runs in half the time — `PipelineTests` split into 17 classes.** The full run
  went from **~600s to 318s**, with every one of the 2173 tests unchanged and still passing.

  xUnit 2.4.2 parallelises across *collections*, and by default a collection is a class — so tests
  inside one class run strictly sequentially. All 429 compiler tests lived in a single 8,800-line
  class, which pinned them to one core. Measured before touching anything: `PipelineTests` alone
  was **7m01s of a 7m05s assembly**, so it was not merely the slowest part, it was the entire
  runtime; the other 116 tests finished beside it in seconds.

  The split is by theme, into classes of ~30 tests each. Balance is the constraint that matters —
  the suite is only as fast as its largest class, and whatever is still running at the end has the
  machine to itself. `PipelineTestBase` holds the shared helpers.

  **What the measurements said, including the parts that did not work.** A first cut left one class
  at 91 tests and another at 4; rebalancing to a ~35 maximum bought **10 seconds**, so imbalance was
  not the limit. The remaining ceiling is the machine: gcc is CPU-bound and this is a **4-core**
  box, and every generated binary is written and executed under real-time antivirus. Excluding the
  build temp directory is the next lever, and it belongs to the machine rather than to the repo.

  ⚠ **It also exposed a latent race, which is now fixed.** Every harness built its temp paths as
  `Path.GetTempFileName()`, deleted the file, and reused the stem for `<stem>.c` and `<stem>.exe`.
  That name is unique only *because* the file exists — deleting it hands the name straight back, so
  two tests can be given the same one. Serially it never mattered; at 17-way parallelism the
  example suite failed roughly **one run in three**. All eight sites now build a stem that is unique
  by construction (`cufet-<guid>`), with no file created and no name to reuse: **0 failures in 6
  runs**, where the same loop had reproduced it twice.

- **Four tests added for gaps that mutation testing found.** A fault was injected into the code
  generator at each of these points and **nothing went red**.

  - **Bits ordering at equality.** `Lte` emitted `<=`; changing it to `<` survived, because
    nothing compared two bits values that were *equal* — the only input where the two differ.
  - **Matrix equality inside a container.** Equality has two lowerings: `EmitBinary` for a bare
    `is`, and `EqCall` for a value compared as part of something else. The survivor was in
    `EqCall`, so a test of `m is n` does not reach it — written first, that test passed happily
    with the mutation reintroduced. A matrix inside a record is what reaches it.
  - **`cufet_dec_from_dbl` either side of `power == -1`**, a boundary with no coverage at all.

  ★ **One of the four "gaps" turned out not to be one.** The `power == -1` mutant is
  **equivalent**: inverting it divides by 10, but such a value is necessarily above 1e14, so the
  next line's `dbl < 1e14` bump multiplies by 10 and increments `power` straight back. Both paths
  reach the same mantissa and scale, so no test can tell them apart. It was misfiled as a coverage
  gap until the test was written and *failed to fail* — which is why each of these was checked by
  reintroducing its own mutation rather than by trusting the analysis. That correction moves the
  sample's score from 14/19 to **14/18**.

- **Four more hand-written walks converted to the shared `AstSearch`.** `ProgramUsesConcurrency`
  (whose own comment already recorded losing concurrency inside a book pull),
  `CollectInterfaceDefs`, `CollectObjectDefs` and `MergeUntoMethods` — the last of which was
  silently dropping `unto` methods declared inside a book pull. A generic walk has no list to fall
  behind, which beats a test that reports the list went stale.
- **`FormatTypeName` can now name every type.** Seven — mapping, both stream types, rabbit,
  failure, exception and book — used to be reported as `'value'`.

### Fixed

- **★ A task never captured a variable it used only inside an `If` or `Judge` arm.** The emitted C
  said `cv_<name> undeclared` and gcc refused it.

  `CollectRefsDefs` — the free-variable analysis that decides what a task or closure captures —
  walked children by matching `IExpression`/`IStatement`. **`ConditionArm` and `JudgeArm` implement
  neither.** They are plain records that *hold* statements, so the walk stepped straight over the
  condition and the body of every `If` arm and every judgement. `AstSearch` had already been
  corrected for exactly this and even names both records in its comment; this was a separate
  hand-written walk that never got converted, so it is now keyed on the namespace too.

  It hid because a body that mentions the variable anywhere else is rescued by that other mention —
  the same reason the `BecomesStatement` case above it survived so long. The work-queue collector
  broke because `If count is n, Stop.` was its *only* use of `n`; the producer in the same program
  captured `n` correctly from a `While` condition. `Otherwise` bodies were never affected, since
  `ElseBody` is an ordinary property rather than an arm.

  ★ **Found by the first run of the new Linux CI job**, in two tests that had never executed
  anywhere — `Concurrency_FanOut_WorkQueue_EachItemProcessedOnce` and its ASan twin. It was
  reproduced and fixed on Windows without needing Linux at all, because *code generation* runs on
  both platforms; those tests are Linux-only merely because mingw cannot build pthreads. The
  regression tests therefore assert on the **emitted C**, so a relapse fails on the development
  machine rather than waiting for CI.

  The gcc failure also arrived correctly labelled "This is a bug in the Cufet compiler, not in your
  program" — the message added hours earlier, on its first real encounter.

- **★ One `State` is now ONE line, even from two threads.** A `State` writes in several calls — the
  value, then the terminator, and a series or record writes every element and separator
  separately — so a concurrent `State` on another thread spliced itself between them. Output came
  out as `side effectdone` followed by both newlines, in **4-8% of runs** of a two-thread program.

  The statement now holds the stream lock for its whole emission, which is what that lock is for:
  stdio takes it per call anyway, so an unthreaded program pays one uncontended lock per line. It
  is a no-op off POSIX, where there is no second thread to race. Measured on the program that
  produced the splicing: **300/300 clean**, from 12-25 torn per 300 before.

  ⚠ **Ordering is a separate question and is NOT guaranteed.** A task that prints and is never
  awaited races the main thread by construction; the lock stopped the *tearing*, not the race. The
  test that caught this asserted an exact string on two lines nothing sequences, so it was
  asserting a coincidence — it passed for months, then failed in CI. It now compares the lines
  order-independently through `AssertSameLinesInAnyOrder`, whose comment is explicit that this is
  only for output nothing orders, and never a way to soften a real divergence. A scan of the other
  concurrency tests found no second instance: every other task-printing test has an await or a
  channel between the writes, so its order is real and its exact assertion stays.

- **★ A refusal described a shipped feature as missing, in this project's private vocabulary.**
  Registering a union struct refused an open one with *"open catalogues … are not yet supported by
  the compiler … Open unions are the CAT.2 slice."*

  Open catalogues **are** supported — `a catalogue with (1, "two", 3)` builds and runs — so the
  message would have sent a reader rewriting working code, and "the CAT.2 slice" names nothing
  anyone can look up. It is also a **backstop rather than a limitation**: reaching it means a
  caller routed an open union into the closed-union builder, which is a defect here. It now says
  so, in the same terms a rejected `gcc` build does.

  Found by the **error-message audit** the roadmap had deferred. That sweep also named a category
  the item did not anticipate: several refusals say "not yet supported by the compiler" while
  guarding paths that are simply unreachable — `unto` methods and function-value calls both compile
  today, and their refusals sit on branches a correct caller never takes. A backstop that
  apologises like a limitation teaches a reader to work around something that is not there.
  `UserFacingMessages_DoNotLeakInternalVocabulary` now runs the vocabulary half of the audit on
  every build, so "periodic" no longer depends on remembering.

- **A task published its result BEFORE running its own unmakers, so an awaiter raced the
  cleanup.** `the awaited result of` woke the waiting thread while the task thread was still
  executing destructors.

  An unmaker is **user code** — it can print, write a file, close a handle — so this is not an
  internal ordering detail. Measured over 200 runs of one task whose unmaker prints a line: **185**
  in the interpreter's order, **1** reversed, and **14 with the two lines torn into each other**
  (both texts emitted, then both newlines), because two threads were writing at once. After the
  fix, **200/200**.

  The emitted C said it plainly — `cufet_rbox_publish(...)` then `cufet_run_unmakers_to(...)`. The
  comment beside it argued that publishing early was safe because the returned envelope is
  self-contained heap; true about memory, and beside the point, since publishing is what releases
  the awaiter. Both publish sites now clean up first — the value-returning `return` and the
  fall-off-the-end path — and publish still precedes `free(cf_a)`, which owns the box.

  **`the awaited result of` now means the task is finished, cleanup included**, which is what a
  structured join should have meant all along.

  Caught by the Linux CI job on a later push, as an intermittent failure of
  `Unmaker_InsideConcurrentTask_FiresOnItsOwnThread`. Verified by re-emitting the C and running it
  200 times under WSL, and the concurrency examples were re-checked under gcc **and ASan** — all
  clean — because this reorder touches every task return and almost none of that surface runs on
  Windows.

- **The capture-write refusal missed any write one `If` or `Judge` arm deep — a LIVE DIVERGENCE.**

  ```
  Define tally as 0.
  Pull a rabbit.
      Have rabbit start a task as bump:
          If 1 is 1:
              tally becomes tally + 5.      ← never refused
          Done.
          return 1.
      Done.
      State the awaited result of bump.
  Done.
  State tally.
  ```

  `cufet check --native` reported **no problems**, the interpreter printed **5**, and the compiled
  binary printed **0**. Take the `If` away and the same write is refused, as it has been since TCAP
  shipped — the refusal exists precisely because the interpreter hands a task the live enclosing
  binding while the compiler hands it a copy.

  `TaskBodyMayMutate` had the same arm-record hole as `CollectRefsDefs` above, and this one does not
  fail loudly: a missed write is not a compile error, it is a program that quietly means two
  different things. **This walk must over-approximate** — a missed write ships a divergence, an
  extra refusal costs only a clean error — so it now descends into everything in the AST namespace.

  Found by **auditing for the same hole** once CI exposed the first one, which also turned up
  `ProgramUsesOpenUnion` (open-union discovery would have missed a union first seen inside an arm);
  fixed the same way. Two further reflection walks were checked and were already safe, descending
  unconditionally. **That makes seven instances of this one bug class**, so the rule is worth
  stating plainly: a walk over the AST must key on the **namespace**, never on
  `IExpression`/`IStatement`, because `ConditionArm` and `JudgeArm` implement neither.

- **★ Compiled, narrowing again inside a Judge's grouped arm emitted C that would not compile.**

  ```
  Judge thing, where it is:
      An alpha or a beta:
          If it is an alpha, return it's source.
          Otherwise, return it's body.       ← 'cun_0' has no member named 'cv_body'
      Done.
      A gamma, return it's tag.
  Done.
  ```

  A grouped arm leaves `it` a union, so the arm narrows its **type** without changing its
  **representation** — the C variable is still the subject's whole union struct. Narrowing again
  inside the arm is exhaustive to the checker, which eliminates from the arm's two cases and lands
  on one. The compiler eliminated from the subject's three, found two left, declined to narrow, and
  then emitted the field access against the union anyway.

  The fix records which cases an arm leaves reachable and eliminates from that, while every emitted
  access still indexes the representation union — a set rather than a substituted narrower type,
  because an arm's case order need not match the subject's and a sub-union's own indices would
  reach the wrong member. Three cases is the smallest shape that shows the bug: with two, the arm
  covers everything and the two elimination sets agree.

  ⚠ **`cufet check --native` reported no problems on it.** Its job is to run codegen and report what
  the compiler refuses, but it only reports what codegen *throws* on — here codegen ran to
  completion and produced C that gcc rejects, so the editor, the check verb and any front-end CI
  pass all said clean and the failure appeared only at `build`. The narrowing bug is fixed; **that
  gap is not**, and it is the more general problem.

- **★ The code generator could emit C that would not build, without saying anything.** The member
  access it emits ended in a catch-all — *anything that gets here is a record* — so whatever
  arrived got the record shape. A union arrived (the Judge bug above) and the result was C naming
  a struct member that does not exist.

  The catch-all is gone. Every type the checker permits `'s` on has an explicit arm, and anything
  else is **refused** instead of emitted. That matters beyond the one bug: `cufet check --native`
  reports what the generator refuses, so a refusal becomes a warning on the responsible line in the
  editor, while a bad emission is a build failure with nothing to act on.

  ⚠ **Said plainly in `REFERENCE.md` and in `--help`: a clean `--native` is not a promise the build
  will succeed.** It reports refusals, and it cannot report a defect *in* the generator, because
  from the generator's side emitting bad code looks like success. `cufet build` is the only proof.

- **A failed `gcc` build now says it is a compiler bug.** Every line `gcc` reads was written by
  `cufet`, so an error pointing inside the generated file is never the author's to fix. It used to
  print `gcc compilation failed:` and paste a complaint about identifiers like `cun_0`, which names
  nothing in the source and reads as though the program were at fault. It now says the defect is in
  Cufet, keeps `gcc`'s own words, and points at `emit-c` for reporting it. An error that does *not*
  point into the generated file is a toolchain problem and is still reported as one.

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

  Measured before committing to it: pointing all 379 oracle assertions and the example harness at
  raw output surfaced **exactly one** divergence across 562 tests — this one. Nothing else was
  hiding.

  **Every survivor from the sample has now been resolved**, each by experiment rather than by
  reading the code — five equivalent (`845` bits width, `1453` POSIX `poll`, `6808` range direction,
  `1542` the decimal `power == -1` case, and `Interpreter.Core.cs:1710`, whose null guard is
  unreachable because embed-ness is fixed by an object's type), and three genuine gaps now tested.
  The last of them, `6967`, turned out to be observable **only in the wording of a refusal**: the
  two stream types are otherwise indistinguishable to the code generator, since the `fopen` mode is
  decided separately — and mutating *that* is caught, because it truncates the file being read. A
  refusal naming the wrong type sends a reader hunting for a bug they never wrote, so it is pinned. `GeneratedC_UsesTheNewlineMacro` now refuses a by-hand newline in the emitted C, because
  a new `State` arm would otherwise reintroduce it silently; verified by putting the bug back at
  one site and watching both it and the behavioural test fail.

- **★ The CLI mangled every non-ASCII character it printed.** `State "héllo 👍".` came out as
  `h?llo ??` interpreted and correctly compiled — a real divergence, because the CLI wrote through
  the console's default encoding, a legacy code page on Windows, while a compiled binary writes
  UTF-8 bytes directly. Now set explicitly, and a redirected stdout gets it too.

  ★ **The test suite could not have caught this**, which is worth recording. Its interpreter side
  writes to an in-memory `StringWriter` and its compiled side reads the binary with
  `StandardOutputEncoding` already UTF-8, so both are lossless in-process and only the console ever
  lost anything.

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

  **[DESIGN.md](docs/DESIGN.md) is new** and holds the "why" — what Cufet is for, and the decisions
  that follow from it. ROADMAP.md is now 206 lines and records **only what is not yet done**; when
  something ships it is deleted from the roadmap, because its record is the changelog entry and
  its rationale is DESIGN.md. Implementation invariants and known limitations moved to
  CONTRIBUTING.md. Nothing restates anything else, which is the only thing that actually prevents
  drift.

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
  fallibility, not-defined cases).

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
