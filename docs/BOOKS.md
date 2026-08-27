# Cufet Books

A **book** is a module that comes in the box. This is the reference for the ones Cufet ships
with — what they hold, and what pulling them admits.

Books come in two kinds, and the difference is what you get out of one:

- **Standard-library books** have *members you call*: `math`, `collections`, `chance`.
- **Language books** have no members at all. Pulling one admits a *syntax* — writing axioms in
  that language — and `the c-language` is the one that exists today.

`Pull` itself is a module mechanism one level above books, and lives in
[REFERENCE.md](REFERENCE.md#modules-pull). For the language in full, see
[REFERENCE.md](REFERENCE.md); for the rules stated precisely, see [GRAMMAR.md](GRAMMAR.md).

---

## Contents

- [Part I. Standard-library books](#part-i-standard-library-books)
  - [Books (the standard library)](#books-the-standard-library)
  - [Matrix](#matrix)
- [Part II. Language books](#part-ii-language-books)
  - [Foreign source (`axiom`)](#foreign-source-axiom)
    - [Handing values to C](#handing-values-to-c)
    - [What crosses the boundary](#what-crosses-the-boundary)
    - [What an axiom can reach for](#what-an-axiom-can-reach-for)
    - [What it costs](#what-it-costs)
    - [How far an interrupt reaches](#how-far-an-interrupt-reaches)
  - [Cufet source (`cufet`)](#cufet-source-cufet)

---

## Part I. Standard-library books

### Books (the standard library)

Capability that most programs do not need lives in a **book** — a module that comes in the
box. Brought into scope for a block with `Pull a book on <name>. … Done.`, with members
reached by the possessive: `math's square-root of (144)`.

The `book on <name>` spelling is **required** for a book, because the bundled names read badly
without the noun — "Pull a math." A book is still a module, and `Pull` asks it the same
question; only the spelling differs.

Books are resolved at compile time. There is no dynamic loading, and no external loader
yet — the bundled books are `math`, `collections`, and `chance`.

**Several at once** — `Pull books on …` (plural) takes a comma-separated list, with an
optional `and` before the last. One `Done.` closes the block for all of them:

```
Pull books on math, collections, and chance.
    State math's square-root of (16).
    State collections's maximum of (a series of number with (5, 3, 9)).
Done.
```
```
4
9
```

Each entry may carry its own alias:

```
Pull books on math as m, and collections as c.
    State m's square-root of (25).
Done.
```
```
5
```

```
Pull a book on math.
    State math's square-root of (144).
    State math's absolute-value of (0 - 7).
    State math's pi.
Done.
```
```
12
7
3.1415926535897932384626433833
```

`math` provides `square-root`, `log`, `exp`, `power`, `floor`, `ceiling`, `round`,
`absolute-value`, and the constants `pi` and `e`. Rounding, flooring and absolute value are exact
decimal operations, and `pi` and `e` are decimal-precise constants (28 fractional digits,
correctly rounded).

★ **The whole book is written in Cufet, and nothing in it touches a `double`.** The square
root is Newton–Raphson on the decimal; `log` and `exp` reduce their argument and sum a series;
`power` is exact for a whole-number exponent, which is also the only kind a negative base
accepts. Because both backends run the same algorithm on the same arithmetic, they give the
same answer everywhere — a fractional `power` used to differ in its last digit between
platforms, since the library underneath *was* the platform's own, and that is now gone.

```
Pull a book on collections.
    Define scores as a series of number with (5, 3, 9, 3).
    State collections's maximum of (scores).
    State collections's average of (scores).
    State collections's unique of (scores).
Done.
```
```
9
5
(5, 3, 9)
```

`collections` provides `minimum`, `maximum`, `average`, and `unique` (first occurrence
wins, order preserved). Each yields void for an empty series. `average` is exact — it
does not go through floating point. The `matrix` type also lives in this book; see below.

`chance` provides random numbers, random selection, and shuffling, plus `Seed the chance
with <n>.` Seeding makes a run reproducible **within one backend**; the interpreter and a
compiled binary use different generators, so a seeded program is not expected to produce
the same sequence in both.

---

### Matrix

`matrix` is a rectangular grid of numbers, available inside `Pull a book on collections.`
Arithmetic is exact decimal, and is **fallible** — dimensions have to agree, so `+`, `-`
and `*` on matrices must be handled with `Try to:`, `but on failure`, or `or pass the
failure off`. Using one bare is a static type error.

```
Pull a book on collections.
    Define m as a matrix with ((1, 2), (3, 4)).
    Define n as a matrix with ((5, 6), (7, 8)).
    State m.

    Try to:
        State m + n.
        State m * n.
    Done.
    In case of failure:
        State "failed: {message of the failure}".
    Done.

    Define bad as a matrix with ((1, 2, 3)).
    Try to:
        State m + bad.
    Done.
    In case of failure:
        State "failed: {message of the failure}".
    Done.
Done.
```
```
matrix((1, 2), (3, 4))
matrix((6, 8), (10, 12))
matrix((19, 22), (43, 50))
failed: matrices must have equal dimensions for addition
```

`*` is matrix multiplication, not element-wise. `collections's transpose of (m)` flips
rows and columns. There is no matrix division — it would require inversion, which is
deliberately not provided.

**Reading and writing a cell.** `the item at (row, column) of m` is the accessor, and both
indices are **1-based**, like every other position in Cufet. The same phrase on the left of
`becomes` writes the cell. `the rows of m` and `the columns of m` are the dimensions.

```
Pull a book on collections.
    Define grid as a matrix with 2 by 3 filled with 0.
    The item at (1, 2) of grid becomes 7.
    The item at (2, 3) of grid becomes 9.
    State grid.
    State the item at (1, 2) of grid.
    State the rows of grid.
    State the columns of grid.
Done.
```
```
matrix((0, 7, 0), (0, 0, 9))
7
2
3
```

A cell holds a number and nothing else — that is what keeps matrix arithmetic exact.

**A matrix is a reference type**, so a write is visible through every name for it, and a
matrix handed to a function is the caller's matrix rather than a copy. That is what lets a
function update a board in place:

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
```
matrix((0, 0), (1, 0))
```

An index outside the matrix is a runtime failure naming the bound it crossed, the same on a
read and on a write:

```
Row index 3 is out of range — this matrix has 2 row(s) (line 3).
```

---

## Part II. Language books

### Foreign source (`axiom`)

**An axiom is source in another language, held as a value.** Cufet cannot check a C listing
and does not pretend to — it takes it as given, which is what the word means.

```
Pull a book on the c-language.
    Define c-language number get-pid as [getpid()].
    State cast get-pid.
Done.
```

Three parts, and each does one job:

- **`Pull a book on the c-language.`** — a book on a *language* has no members. Pulling it is
  what admits C axioms in this block, and it is the line a reader can see that on.
- **`[ ... ]`** — square brackets appear nowhere else in Cufet. Inside them nothing is Cufet:
  no interpolation, no escapes, no comments. Bracket pairs nest, so `[argv[0]]` is fine.
- **`c-language number`** before the name — the tag says who reads the text, and the type says
  what running it gives back. The full spelling is `Define the c-language number axiom get-pid
  as [ ... ].`; both middle words drop, but the tag can never be left out.

**The result is declared where the axiom is written**, by the person who knows. Cufet cannot read
a C listing: an `int` might be a count, a truth, or a file handle, and only you can say which.

```
Pull a book on the c-language.
    Define c-language number greeting-length as [(int)strlen("hello, world")].
    State cast greeting-length.        ← 12
Done.
```

A call composes anywhere an ordinary call does.

**The source is spliced where an expression goes**, so a loop needs C's own way of putting
statements there — a statement-expression, `({ ... })`, whose last expression is its value:

```
Pull a book on the c-language.
    Define c-language number sum-to, given (the number top),
        as [({ int s = 0; for (int i = 1; i <= (int)the top; i++) s += i; s; })].
    State cast sum-to on (10).
Done.
```
```
55
```

⚠ Written bare — `[int s = 0; for (...) ...; return s;]` — gcc rejects it, because that text is
placed where a value is expected rather than where a body is.

#### An axiom that says nothing is source

**Saying what an axiom gives back is what makes it something to RUN.** An axiom that says nothing
is **source**: it is pasted once, above everything else foreign in the program, and what it declares
is in scope for every axiom after it.

```
Pull a book on the c-language.
    Define c-language helpers as [static int twice(int x) { return x * 2; }].
    Define c-language number four as [twice(2)].
    State cast four.
Done.
```
```
4
```

That is how two axioms share a helper. One declared *inside* an axiom belongs to that axiom alone,
so without this each would carry its own copy — and joining axioms together to share one is
deliberately not offered, because it would merge their parameter lists and leave the article
substitution rewriting someone else's text.

⚠ A resultless axiom **takes no parameters** — a parameter's value comes from a call, and nothing
calls this. And nothing in it is checked: a wrapper's guards ask what an expression produces, and a
declaration produces nothing, so what you write reaches the C compiler exactly as written.

#### An axiom is a value

**An axiom that says what it gives back can be passed around unrun** — handed to a function, kept
in a series, held in an object's field — and run wherever it lands. The type is written the way its
declaration reads, with `given (…)` saying what it takes exactly as a function type does:

```
Pull a book on the c-language.
    Define c-language number length-of, given (the text subject), as [(int)strlen(the subject)].
    Define c-language number first-byte, given (the text subject), as [(int)(the subject)[0]].

    Define the jobs as a series of c-language number axiom given (the text) with (length-of, first-byte).

    For each job in the jobs, repeat:
        State cast job on ("hello").
    Done.
Done.
```
```
5
104
```

Which axiom runs is decided there by the loop, not by the text. The foreign source is still fixed
where it was written — nothing is assembled, and nothing can be injected — and what moves is the
axiom itself.

**Saying the result is what makes it writable as a type.** `the c-language axiom job` is refused:
running it has to produce something, and an axiom that never said what has no shape to be handed
around as. Add the result and it is accepted.

★ **An axiom carrying `and free it with` travels too.** The freeing is registered where the address
is ACQUIRED, so it happens once per call however that call was reached — by name, or through a
value that arrived from somewhere else entirely. The rabbit rule is unchanged: an address still
cannot be held outside a rabbit block, and it is still freed when that block ends, exception
included.

An axiom prints as `<axiom>`, the way a function prints as `<function>` — the source is not
something a program reads back.

#### Handing values to C

An axiom declares what it takes the way every Cufet body does, and reaches those values inside
the foreign text **by the article**:

```
Pull a book on the c-language.
    Define c-language number text-length, given (the text subject), as [(int)strlen(the subject)].

    State cast text-length on ("hello, world").        ← 12
Done.
```

`the subject` works as a marker because it is never valid C — English sitting in code that is not
English — so nothing has to be escaped. It reads as the line above it: `the text subject` in the
declaration, `the subject` in the source.

**Only values cross, never text.** C receives a marshalled number or string; the axiom itself is
fixed where you wrote it and cannot be built up from pieces at run time. That is the same
guarantee `Run "grep" with arguments (…)` gives, and the same reason nothing can be injected into
it.

| You pass | C receives |
| --- | --- |
| `number` | `long long` — **range-checked**; a fractional or oversized value raises rather than being trimmed |
| `text` | `const char*`, UTF-8, valid for the length of the call |
| `fact` | `int`, 1 or 0 |

⚠ A parameter the source never mentions is refused — only names you declared are substituted, so
a misspelling would otherwise reach the C compiler as a stray `the`.

⚠ A reserved word cannot be a parameter name. `path` and `where` are two, so write `file-path` and
`folder`.

**Call one as a statement when you only want the effect**, and the answer is discarded:

```
Cast close-dir on (handle).
```

Everything is checked the same way — the language must be pulled, the arguments must fit, and the
declaration must still say what it gives back, because the C wrapper is built from that whether or
not anyone reads the answer.

#### What crosses the boundary

Five things, and each says what it needs on the declaration:

| You declare | C gives back | |
| --- | --- | --- |
| `number` | any C whole number, signed or unsigned | exact — a decimal holds every 64-bit integer either way |
| `fact` | anything with a truth value | 1 or 0 |
| `voidable number` | a `float`, `double` or `long double` | converted once, in shared C; no representable answer becomes void |
| `voidable address` | a pointer of any kind | held opaquely, never read through; nothing becomes void |
| `voidable text` | a `char*` or `const char*` | **copied**; nothing becomes void |

```
Pull a book on the c-language.
    Define c-language voidable text home as [getenv("HOME")].
    Define c-language voidable text describe, given (the number code), as [strerror(the code)].

    State (cast describe on (2)) but void is "no idea".      ← No such file or directory
Done.
```

**A text result must be `voidable text`, never a plain `text`.** C says nothing is there by
handing back nothing — `getenv` on a name that is not set, `strerror` on a code it does not know —
so absence arrives in the mechanism Cufet already has instead of a promise C cannot keep.

**A text is copied out of C's memory.** The bytes belong to C: `strerror` hands back a buffer the
next call overwrites, and anything allocated dies when its owner says so. A Cufet text pointing at
either would change under your program, so it never points at either.

Everything else is **refused rather than approximated**:

| The axiom produces | What happens |
| --- | --- |
| a floating value, for a `number` | refused — declare it a `voidable number` instead |
| a whole number, for a `voidable number` | refused — declare it a plain `number` instead |
| anything else, for a `voidable text` | refused — it has to be a C string |

These are refused by the C compiler, which is where the type is actually known. The first two are
one question asked from either side: `number` is exact and `voidable number` is the one conversion
that is not, so which you meant is worth saying rather than guessing.

#### Addresses — holding a C pointer

Some C is a handle you get, use, and give back: `opendir`/`readdir`/`closedir`, `fopen`/`fclose`.
That handle crosses as an **`address`** — opaque, held, and handed back, never read through:

```
Pull a book on the c-language.
    Define c-language voidable address open-dir, given (the text folder), as [opendir(the folder)].
    Define c-language voidable text next-name, given (the address handle),
        as [({ struct dirent* e = readdir((DIR*)the handle); e ? e->d_name : (char*)0; })].
    Define c-language number close-dir, given (the address handle), as [closedir((DIR*)the handle)].

    Pull a rabbit.
        Define handle as cast open-dir on ("logs").
        If handle is not void:
            Repeat:
                Define name as cast next-name on (handle).
                If name is void, stop.
                State name.
            Until false.
            Cast close-dir on (handle).
        Done.
    Done.
Done.
```

**There is one kind of address.** A `char*` and a `FILE*` are the same type here — what differs is
not the value but what you do with it. There is **no address-of operator**: an address only ever
comes *from* C and goes *back* to C, so Cufet never makes one, and there is no layout question to
answer because a struct is C's idea and struct work happens in C.

⚠ **An address may only be held inside a rabbit, and cannot outlive one.** That block already means
region-scoped memory work, so it is also where a pointer's lifetime is answerable for — the arena
that knows when the region dies is what knows when the pointer dies. Holding one outside a rabbit
is a static error, and so is putting one somewhere that outlasts the block: an address obeys the
same escape rule as a series or a map, so inserting one into a series declared outside the rabbit
is refused with "this value lives in a shorter-lived rabbit region than its destination".

**NULL is void**, as everywhere else on this boundary, which is why the result is a `voidable
address` — `fopen`, `malloc`, `getenv` and `opendir` all report failure that way.

An address prints as `<address>`, never as its value: a handle is a different number in every
process, so printing it could tell you nothing you could rely on.

**`the text at <address>` reads through one**, and it is the only read there is:

```
Pull a book on the c-language.
    Define c-language number shut, given (the address held), as [closedir((DIR*)the held)].
    Define c-language voidable address open-dir, given (the text folder),
        as [opendir(the folder)], and free it with shut.
    Define c-language voidable address next-found, given (the address held),
        as [({ struct dirent* e = readdir((DIR*)the held); e ? e->d_name : (char*)0; })].

    Pull a rabbit.
        Define handle as cast open-dir on ("logs").
        If handle is not void:
            Repeat:
                Define found as cast next-found on (handle).
                If found is void, stop.
                State the text at found but void is "?".
            Until false.
        Done.
    Done.                              ← the directory is closed here
Done.
```

**It always copies.** What you get back is `voidable text` that belongs to the rabbit, never a view
into C's memory — so it survives C freeing or overwriting the block it came from, which `readdir`
does on the very next call. Reading through a **void** address is void, not a crash.

**Reading a struct or a scalar is not offered, and is not missing.** An axiom can project a field
(`[the point->x]`) or declare a local and hand it back, so those values come home as ordinary
results. Text is the one case with no single-expression answer on the C side, because the bytes
belong to C and have to be copied out.

**`and free it with <name>` says how to release it**, and then Cufet does, on every way out of the
block — its `Done.`, a `Stop`, a `return`, or an exception nobody catches:

```
Pull a book on the c-language.
    Define c-language number shut, given (the address held), as [fclose((FILE*)the held)].
    Define c-language voidable address open-one, given (the text file-path),
        as [fopen(the file-path, "rb")], and free it with shut.

    Pull a rabbit.
        Define handle as cast open-one on ("notes.txt").
        ...
    Done.                              ← freed here, however the block is left
Done.
```

The releasing axiom takes one `address` and nothing else. A void result is never freed — C had
nothing to give, so there is nothing to release.

**Why it sits on the acquiring declaration.** `getenv` and `strdup` both hand back a `char*`, and
one must be freed while freeing the other is a crash. Cufet never reads the foreign text, so it
cannot tell them apart — you can, and this is where you say so, once.

⚠ **Saying nothing frees nothing.** An axiom with no clause is never released, and that is the safe
direction: a leak is recoverable and shows up in a leak checker, where a double free is corruption
that surfaces somewhere else entirely. Nothing checks that the function you named is the *right*
one either — `and free it with fclose` on a directory handle type-checks and then misbehaves. This
is the residue of calling C at all, and it is where the guardrails stop.

**A `size_t` needs no cast.** `strlen`, `sizeof`, `fread` and the rest of libc report a length as an
unsigned 64-bit value, and that is a whole number like any other here — the boundary carries the
value's signedness along with its bits, so a large one arrives as the number it is rather than as a
negative one:

```
Pull a book on the c-language.
    Define c-language number length-of, given (the text subject), as [strlen(the subject)].
    Define c-language number widest as [(unsigned long long)-1].

    State cast length-of on ("hello").      ← 5
    State cast widest.                      ← 18446744073709551615
Done.
```

**A `double` crosses as a `voidable number`, and it is the one lossy conversion.** A `number` is
base-10 and a `double` is base-2, so 17 significant digits — what a `double` round-trips in — is
what arrives:

```
Pull a book on the c-language.
    Define c-language voidable number root-two as [sqrt(2.0)].
    Define c-language voidable number a-third as [1.0 / 3.0].

    State (cast root-two) but void is 0.    ← 1.4142135623730951
    State (cast a-third) but void is 0.     ← 0.33333333333333331
Done.
```

The conversion is written **once**, in C that both backends compile, and it hands back the parts a
decimal is made of rather than the `double` itself — so nothing is converted twice and the last
digit cannot differ between running a program and building it.

**Void is what "no such number" means here**, and it is the same answer `math` already gives:
`math's square-root of (-4)` is void today, and so is `math's log of (0)`. A NaN, an infinity, and a
magnitude outside a decimal's range all arrive as void — including a very small one like `1e-300`,
which is void rather than `0`, because silently answering zero is the failure this refuses.

#### What an axiom can reach for

**A fixed set of headers, already included.** The C standard library, plus the POSIX headers on
Unix and the Win32 and winsock headers on Windows. You do not name headers, and there is no
`#include` to write.

The set is **platform-guarded**, because the interesting headers are not portable: `<termios.h>`,
`<poll.h>`, `<sys/socket.h>` and `<sys/wait.h>` exist on Unix and not on Windows, while
`<windows.h>` and `<winsock2.h>` are the other way round. Nothing is smoothed over — a program
calling `tcgetattr` is a Unix program however it is written, and on Windows the C compiler says so
before the program ever runs.

⚠ **A library of your own is not reachable yet.** The reason is linking rather than headers: a
header gives you declarations, and something still has to put `-lsqlite3` on the command line. Both
halves have to arrive together, so neither has.

⚠ **Foreign state is per-process, and the two backends are not the same process.** A compiled
program is its own process; the interpreter calls into C inside the process running the
interpreter. Anything C remembers globally — winsock initialisation, `errno`, a library's one-time
setup, the current locale — can therefore differ between running a program and building it. Cufet
values cross the boundary identically; what the C side remembers is the C side's business.

#### What it costs

**Running an axiom needs a C toolchain, on either backend.** Compiled, its text is pasted
into the program's own C and called directly. Interpreted, it is compiled into a small shared
library and called through it — cached by content, so `gcc` runs once per distinct axiom per
machine rather than once per run.

Where there is no toolchain at all — the web playground runs the interpreter in wasm — a
program containing an axiom refuses to run and says so.

**Every axiom is built before the program starts**, whichever way you run it. So a program with
one axiom that will not compile prints **nothing at all** — it does not get partway through and
then stop, and running it tells you exactly what building it would have. An axiom you declare and
never return is not built by either backend, so it costs nothing and cannot fail.

**If gcc complains about your C, that is yours to fix.** Everything else in the generated C
was written by cufet, and a failure there is reported as a compiler bug; an axiom is the one
exception, and it is reported as one.

---

### Cufet source (`cufet`)

**A `cufet` axiom is Cufet source held under a name.** The same surface as a foreign one, with a
different mechanism behind it. `[ ... ]` still says *the text inside is not the program around it*,
and that stays true here — the source is parsed, but nothing happens to it until you say so.

**Which kind it is comes from the same rule the C tag follows: says what it gives back, and it is
something you run; says nothing, and it is source.**

```
Pull a book on cufet.
    Define cufet number sum-to, given (the number top), as [
        Define the total as 0.
        For each step in range 1 to the top, repeat:
            The total becomes the total + step.
        Done.
        Return the total.
    ].
    State cast sum-to on (10).
Done.
```
```
55
```

A runnable one's body is a body, so a loop goes in it, and it is a value like any function — bind
it to another name and run it there. It has no crossing restriction either: C is limited to a
number, a fact and a voidable text because those are what survive the boundary, and nothing crosses
one here.

```
Pull a book on cufet.
    Define cufet vector-shape as [
        Define object vec2 with (the number x, the number y):
            Bind number to length-squared:
                Return one's x * one's x + one's y * one's y.
            Done.
        Done.
    ].

    Cite vector-shape.

    Define the arrow as a new vec2 { the x 3, the y 4 }.
    State cast length-squared on (the arrow).
Done.
```
```
25
```

The book is pulled like any other language book, and it is named `cufet` — no article, because
`cufet` is a name where `the c-language` is a common noun.

**What a block holds are declarations** — an object, an interface, a `Define` and a `Bind`. The
difference between them is the point of `Cite`: a type belongs to the program wherever it is
written, so a cited object is program-scope; a value lands where you cited it. Cite one block in two
places and you get two independent locals.

A block may reach for what belongs to the program — a function, a type, a `permanently` constant, a
pulled book — and for what it declares itself. Nothing else, so a block cannot quietly pick up a
local from the place you happened to cite it.

A block that holds a **function** may only be cited where functions belong: at the top level, or
directly inside a `Pull` block. Placed there it is an ordinary free function, which already cannot
read the data around it; placed inside another body it would close over that body, which is the one
thing a block must never do.

**Declaring a block places nothing.** Cite it and its declarations are there; do not, and they are
nowhere. The name holds source rather than a value, so it cannot be stated, passed or read.

Where a cited declaration lands is not a rule of its own. A type declaration belongs to the program
wherever it is written, so a cited object is usable after the function that cited it — exactly as
one written there by hand would be.

⚠ One name holds one block; a second under the same name is refused. ⚠ Source takes no parameters,
exactly as a resultless C axiom takes none — a parameter's value comes from a call, and nothing
calls source. ⚠ `and free it with` is refused, and it is the one thing the two tags differ on: a
release clause hands memory back to the language that allocated it, and nothing was allocated across
a boundary here. ⚠ A message from inside a block reports the line it occupies in the file that
holds it, not the line it would have on its own.

★ Nothing about this reaches a backend. The blocks are placed and then removed while the program is
still being checked, so what runs — interpreted or compiled — is a program of ordinary declarations,
and neither backend has a word to say about a cufet axiom.
