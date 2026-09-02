# Cufet Grammar & Constraints Reference

This document is the **operational** reference for writing Cufet correctly upfront.
It covers the things that would otherwise be discovered by erroring: reserved words
you can't use as names, scope rules inside object methods, which operations accept
complex expressions vs bare names, where constructs are/aren't allowed, and the
sharp edges that look reasonable but parse or type-check differently than expected.

It is **not** a feature tour — see [REFERENCE.md](REFERENCE.md) for that, or [BOOKS.md](BOOKS.md)
for the books Cufet ships with.

**Both backends, unless marked.** Cufet runs interpreted or compiles to a native binary,
and the lexer, parser, and type checker are shared — so every constraint here applies to
both. A rule that holds for one and not the other is a bug, not a caveat: either the
behaviour is made precise on both sides or the compiler refuses outright. The deliberate
exceptions are called out inline where they arise, and there are two kinds:
**concurrency scheduling** (interpreted tasks are cooperative, compiled tasks are real OS
threads — so no interleaving is specified), and a few genuinely **platform-owned** results
(`power` with a fractional exponent may differ in its last digit, and filesystem
enumeration order).

**Maintenance:** every feature slice that adds a keyword, syntactic form, or
constraint must update this document. The reserved-keyword list in §1 especially.
A future improvement would generate that list from the lexer's keyword table
automatically so it can never drift; for now it is maintained by hand.

---

## Contents

- [1. Reserved keywords](#1-reserved-keywords)
- [2. Object methods and field access](#2-object-methods-and-field-access)
- [3. Value vs. reference semantics](#3-value-vs-reference-semantics)
- [4. Comparison forms — both work everywhere](#4-comparison-forms--both-work-everywhere)
- [5. Which operations accept expressions vs bare names](#5-which-operations-accept-expressions-vs-bare-names)
- [6. Where constructs are allowed](#6-where-constructs-are-allowed)
- [7. Streaming pipes](#7-streaming-pipes)
- [8. Sharp edges](#8-sharp-edges)
- [9. Writing Cufet: the mental model](#9-writing-cufet-the-mental-model)

---

## 1. Reserved keywords

Every token below is lexed as a specific `TokenType` — the lexer will never
produce an `Identifier` for these strings, so they cannot be used as variable
names, field names, function names, or for-each iterator names.

Keywords are **case-insensitive** (`State`, `state`, `STATE` are identical).

### ★ Where a declaration belongs

**A TYPE declaration belongs to the PROGRAM, wherever it is written.** `Define object`, an
interface, an `unmake`, an operator overload and a named constructor are all registered for the
whole program even when written inside a function, a loop, an `If` arm or a rabbit block — so a
type declared in one is usable after it.

**A VALUE binding does not.** `Define x as 5.` is local to its block, and a `Bind` nested inside a
function is a closure rather than a free function. The two rules are what make `Define object` in a
function body sensible and `Define x as 5.` in one still private.

★ **A type may be declared more than once, and the last declaration wins** — the same rule
shadowing follows everywhere else. An earlier definition is dead: nothing dispatches to it, its
methods are never emitted, and its body is not checked. ⚠ The linter reports it, because a reader
cannot see it from the earlier definition alone.

### Noise (consumed silently wherever articles appear)

These are **not** reserved in the sense of being forbidden; they are consumed by
the parser before it looks for the next meaningful token. You will never see them
as identifiers, but that is fine — they read as natural articles.

⚠ **One place where `a`/`an` and `the` are NOT interchangeable: after `is`.**
`x is a <type>` and `x is not a <type>` are TYPE TESTS, and only `a`/`an` introduce
one. `x is the phrase` is an ordinary COMPARISON against the value named `phrase`,
because a name may be written with its article — which is the house style.

★ This is the only spelling where the article carries meaning rather than being
skipped, and it earned a warning the hard way: `the` used to introduce a type test
too, so `x is the phrase` asked *"is x of type phrase?"* and answered false for
every value of every type, with `x is not the phrase` answering true.

| Word | Token |
|---|---|
| `a` | Article |
| `an` | Article |
| `the` | Article |

### Statement-level keywords

| Word | Token | Notes |
|---|---|---|
| `state` | State | Output statement |
| `define` | Define | Variable declaration |
| `as` | As | |
| `becomes` | Becomes | Reassignment |
| `permanently` | Permanently | Lock a binding |
| `shadow` | Shadow | Deliberate shadowing |
| `return` | Return | Return from function |
| `stop` | Stop | Break loop |
| `skip` | Skip | Continue loop |
| `bury` | Bury | `Have <rabbit> bury <v>.` — hand one value out and suspend there. Never bare: a rabbit always does it |
| `unbury` | Unbury | Resume a stash and take its next value — a `voidable T` |
| `stash` | Stash | The TYPE, as in `a stash of number`. Never a call form |
| `cite` | Cite | `Cite <name>.` — place what a `cufet` axiom holds |

### Control flow

| Word | Token |
|---|---|
| `if` | If |
| `otherwise` | Otherwise |
| `when` | When |
| `done` | Done |
| `while` | While |
| `repeat` | Repeat |
| `until` | Until |
| `try` | Try |
| `case` | Case |
| `suppress` | Suppress |
| `judge` | Judge |
| `where` | Where |
| `descend` | Descend |

### ★ A bits width is data — readable and statable

```cufet-fragment
State the width of 0x0F.        → 8
State 0b0 at 3 bits.            → 0b000
Define p as 0b1 at n bits.      ← the width may be computed
```

A `bits` always carried a width — it is what pads the leading zeros — but a program could neither
read it nor choose one, and it is only ever RAISED to fit the value: `0b0 shifted left by 2` is
`0b0`, not `0b000`. Stating a width is the only way to ask for zeros no operand held, which is why
`0b0 at 3 bits` is also how "three zero bits" is spelled. There is no separate literal form.

**Widening always works. Narrowing is refused when it would drop a set bit** — at check time when
the value and the width are both literal, at run time otherwise, in the class dividing by zero is
in rather than as a `failure`. Narrowing that loses nothing is fine: `0b00000001 at 2 bits` is
`0b01`. Mask with `and` if dropping bits is what you meant.

⚠ **Neither `width`, `at` nor `bits` is reserved.** `the width of p` is resolved in the type
checker, since only a bits value can reach it, so `width` remains a legal field name —
`huffmancoding` had one. `at ... bits` is matched by lexeme in the postfix position, exactly as
`item at (r, c)` is.

### ★ `Increment` / `Decrement` name the target once

```cufet-fragment
Increment i by 1.
Decrement remaining by 1.
Increment one's tally by 3.
Increment total by item at (rr, cc) of board.
```

Pure sugar: `Increment i by 1.` **is** `The i becomes i + 1.` — desugared in the parser, so no
backend knows the form exists.

**Why it earns a keyword.** 35% of the `becomes` statements in `examples/` were `X becomes X + …`,
and that repetition is where a typo hides — it hides *well*, because a line that is genuinely not
self-referential is invisible among thirty-seven that are. Naming the target once makes the odd one
out announce itself: `huffmancoding.cufe` has exactly one `The next-w becomes w + 1.` left, and it
is now the only statement of that shape in the file.

**The amount is any expression.** The target is not: it must be a plain name or a possessive chain,
because the desugaring names it twice and anything with a side effect would run twice.

**Numeric only** — no text joining, no series growth. Growing a series is `Insert`, and the two
never read alike: different verb, different preposition.

  | | |
  |---|---|
  | `Increment total by 1.` | arithmetic |
  | `Insert 1 into totals.` | insertion |

**Not `Increase`/`Decrease`.** Every keyword is barred from being an identifier, and `increase` is
an everyday noun — "a price increase" — of exactly the kind that already costs users names.
`increment` is a programming term, so reserving it takes much less away.

### ★ Every block construct takes a comma and one thing, or a colon and a block

One rule, not one per construct. A **comma** means *one thing, inline*; a **colon** means *a block,
closed by `Done.`* The comma is the point and a colon would be wrong — Cufet already spells those
two things that way, and using a colon for both would leave its only reliable structural signal
meaning two different things.

**What "one thing" is depends on whether the body must produce a value.**

| Body | Inline form | |
|---|---|---|
| function with a return type | expression | `Bind number to double, given (the number amount), amount * 2.` |
| getter | expression | `Get area as number, one's radius * one's radius * 3.` |
| named constructor | expression | `Bind making a vec to square-vec, given (the number seed), a new vec { … }.` |
| operator overload | expression | `Bind overloading +, given (the lhs is a vec, the rhs is a vec), a new vec { … }.` |
| `void` function | statement | `Bind void to shout, given (the text word), State word in uppercase.` |
| setter | statement | `Set radius given (the number r), one's radius becomes r.` |
| destructor | statement | `Bind unmaking a gate to close-gate, State "closing {one's id}".` |
| `If`, `Judge` arm | statement | `If x is 1, State "one".` |
| **task** | statement | `Have rabbit start a task as batch-1, return 1 + 2 + 3.` |

An expression body's **`Return` is implicit** — dropping `Return` and `Done.` is the whole of what
the form buys. A void body has no value to imply a return for, so it takes a statement.

**Loops are separated by `repeat:`, not by the comma**, because the comma is already spent on the
loop's own header:

```cufet-fragment
For each n in items, repeat: State n. Done.     ← block
For each n in items, State n.                   ← inline
While i is less than 3, the i becomes i + 1.    ← inline
```

**The consumer loop is the exception to that exception.** `for each <name> from input` has no
`in <series>` clause, so its header spends no comma and it uses the ordinary comma-versus-colon
rule rather than `repeat:`:

```cufet
for each s from input: output s. Done.          ← block
for each s from input, output s.                ← inline
```

★ **A task takes a STATEMENT, and it is the only value-bearing body that must.** Every other body
that can return states its type on the same line — `Bind number to …`, `Get area as number` — and
that declaration is exactly what lets the inline form drop `Return`. A task's header declares
nothing: it may hand back a result or merely send on a channel. So `return` stays written out, and
what the form buys is the `Done.`:

```cufet-fragment
Have rabbit start a task as batch-1, return 1 + 2 + 3 + 4 + 5.
Have rabbit start a task as producer, send 7 through nums.
```

**A function with no parameters uses the same comma**, and `given` is what tells the two apart:

```cufet
Define legs as 8 permanently.
Bind number to leg-pairs, legs / 2.                 ← inline body, no parameters
Bind number to double, given (the number n), n * 2.
```

**An inline body is an ordinary one-statement body.** Nothing downstream can tell the spellings
apart — same AST, same type-checking, same output on both backends.

**Three constructs are deliberately outside the rule.** `Try` — its body is rarely one statement
and its handler is the part you least want compressed. `Pull a rabbit` — it has no header to hang a
comma on. And a **lambda**, because it appears inside argument lists where the comma is already the
separator: `cast apply on (10, a function given (the number x), x * 2)` could not be read. A lambda
body is always `Done.`-terminated.

**`Judge <subject>, where it is:` — coverage is total, by proof or by default.** Arms are bare
cases (`A num-node`, `A number or a text`), taking the comma form for one statement or a colon and
`Done.` for a block. The subject is evaluated **once** and bound to `it`, which is **narrowed**
inside each arm.

- **Closed-union subject** → exhaustiveness is *proved*; `Otherwise` is optional and a missing
  case is a static error.
- **Any other subject** → `Otherwise` is **required**. Control can never fall off the end.
- **A grouped arm does not narrow** — an arm covering two cases cannot know which arrived, so `it`
  stays the union there.
- **The subject may be an expression.** Narrowing is variable-level, so `If` cannot narrow one;
  binding to `it` is what makes it possible here.
- **Nothing may follow `Otherwise`** — a later arm could never run.
- A judgement whose arms all return counts as returning for the every-path-returns rule.

⚠ **Native backend: closed unions only.** A `Judge` over a non-union subject type-checks and
interprets, and the compiler **refuses it cleanly** — value arms would compare values rather than
dispatch on a tag, and that is not built. `Descend.` (explicit fall-through) is reserved and not
yet accepted.

### Functions and objects

| Word | Token | Notes |
|---|---|---|
| `bind` | Bind | Function / method / overload declaration |
| `cast` | Cast | Function call |
| `given` | Given | Parameter list marker |
| `on` | On | Argument list marker |
| `void` | Void | No-return type, `void` value |
| `voidable` | Voidable | Nullable-like type modifier |
| `object` | Object | Object type declaration |
| `interface` | Interface | Interface declaration |
| `new` | New | Object/map/matrix literal |
| `one` | One | Self-reference inside methods |
| `get` | GetKw | Getter declaration |
| `set` | SetKw | Setter declaration |
| `making` | MakingKw | Named constructor marker |
| `unmaking` | UnmakingKw | Destructor marker |
| `overloading` | OverloadingKw | Operator overload marker |
| `unto` | Unto | External method attachment |
| `function` | FunctionKw | Function type annotation |
| `it` | It | Bare-it for-each variable |

### Series and iteration

| Word | Token | Notes |
|---|---|---|
| `series` | Series | Collection type and literal |
| `increment` | Increment | Self-referential addition |
| `decrement` | Decrement | Self-referential subtraction |
| `insert` | Insert | Series insertion statement |
| `into` | Into | Destination of an `Insert` — a distinct token from `in` |
| `to` | To | Directional keyword (also field names — **cannot be a field name**) |
| `start` | Start | Series prepend target |
| `after` | After | Series insert-after position |
| `remove` | Remove | Series remove statement |
| `from` | From | Series remove source (**cannot be a field name**) |
| `item` | Item | Indexed series access |
| `of` | Of | Possessive / accessor marker |
| `for` | For | For-each, map entry |
| `each` | Each | For-each iterator |
| `in` | In | For-each and map-set context |
| ~~`first`~~ | — | Contextual identifier — see "Contextual words" below |
| ~~`second`~~ through ~~`tenth`~~ | — | Same |
| ~~`last`~~ | — | Same |
| `number` | NumberKw | `the number of <series>` — not the type name `number` |
| `sorted` | Sorted | Sort expression |
| `reverse` | Reverse | Reverse sort |
| `by` | By | Sort field marker |
| `range` | Range | Range expression |
| `counting` | Counting | Range step marker |
| `length` | LengthKw | (legacy text length alias) |

### Text operations

| Word | Token |
|---|---|
| `joined` | Joined |
| `converted` | Converted |
| `split` | Split |
| `contains` | Contains |
| `position` | Position |
| `characters` | Characters |
| `end` | End |
| `replace` | Replace |
| `uppercase` | Uppercase |
| `lowercase` | Lowercase |
| `trimmed` | Trimmed |

**Two literal forms, one type.** Which delimiters you use decides only what the lexer
interprets on the way in; both produce `text`, and after lexing nothing downstream can tell
them apart.

| Form | Escapes (`\n`, `\"`, `\{`, …) | Interpolation (`{...}`) | Nests |
|---|---|---|---|
| `"..."` | yes | yes | — |
| `<<...>>` | **no** | **no** | yes, by depth-counting `<<` / `>>` |

Both may run across lines. `<<...>>` is total by design: `"` and `{` are the two characters a
quoted literal cannot hold plainly, and a form that suppressed one but not the other would
still need an escape for the other. The trade is that there is no hole inside it — use
`joined to`.

**★ A line break inside either form is one `\n`, whatever the file is stored as.** A CRLF source
does not put a `\r` into the text. This is a language rule, not a lexer convenience: without it
the same program means different things depending on how the working tree was checked out —
`the length of` differs, and a comparison against `"a\nb"` fails on one machine and not another.
A *lone* `\r` is not a line break on any platform Cufet targets, so one written deliberately is
kept as a carriage return.

**A character is a Unicode code point.** Every count and position — `the length of`, `the
characters from N to M`, `the first`/`last N characters`, and what `the position of` returns — is
measured in code points on **both** backends, regardless of how each one stores text (UTF-16
interpreted, UTF-8 compiled). This is a language rule, not an implementation detail: counting
storage units instead made the same program answer differently depending on the backend.

⚠ A code point is not a grapheme. `e` followed by a combining accent is **2**, though a reader
sees one character. Grapheme segmentation needs the Unicode tables and is not carried into the
emitted C. Operations that neither count nor return a position — `contains`, `split`, `replace`,
`joined to` — are unaffected, because UTF-8 is self-synchronising and a byte-wise match is
therefore a character-wise match.

### Records and maps

| Word | Token | Notes |
|---|---|---|
| `record` | Record | Record type |
| `with` | With | Field list / element list opener |
| `like` | Like | Record shape annotation |
| `map` | Map | Map type |
| `has` | Has | Map-has-key check |
| `key` | Key | Map iteration: `the key of pair` |
| `entry` | Entry | Map entry access — **cannot be an identifier** |
| `size` | Size | Map size |

### Failures

| Word | Token | Notes |
|---|---|---|
| `failure` | Failure | Failure literal or re-propagation |
| `category` | Category | Failure category label |
| `pass` | Pass | `or pass the failure off` |
| `off` | Off | Same |
| `exception` | Exception | Exception handler binding |
| `but` | But | `but void is` / `but on failure` |
| `or` | Or | Logical or / `X or more` / `or pass the failure off` |

### I/O and system

| Word | Token |
|---|---|
| `read` | Read |
| `file` | File |
| `write` | Write |
| `append` | Append |
| `run` | Run |
| `stream` | Stream |
| `open` | Open |
| `contents` | ContentsKw |
| `directory` | DirectoryKw |
| `path` | PathKw |
| `environment` | EnvironmentKw |
| `interrupt` | InterruptKw |
| `acknowledge` | AcknowledgeKw |
| `catalogue` | CatalogueKw |
| `atlas` | AtlasKw |

### Matrix

| Word | Token |
|---|---|
| `matrix` | Matrix |
| `at` | At |
| `rows` | RowsKw |
| `columns` | ColumnsKw |
| `filled` | FilledKw |

### Bits

| Word | Token | Notes |
|---|---|---|
| `shifted` | Shifted | `<bits> shifted left by <n>` / `shifted right by <n>` |
| `xor` | Xor | Exclusive-or — see [Comparison and logic](#comparison-and-logic) |

`left`, `right`, `hex`, `binary` and `octal` are **not** reserved: `the left of node` and
`Define binary as "…"` both work. The `0x` / `0b` / `0o` literal forms lex as a single `Bits`
token, digit count included — see [Leading zeros are significant](#-leading-zeros-are-significant--the-width-comes-from-the-digit-count).

### Chance and randomness

**None of it is reserved.** `random`, `randomly`, `shuffled`, `guess` and `seed` are all
contextual — see [Contextual words](#contextual-words--not-reserved) below, and the
[Chance book](#chance-and-randomness-1) for what each one does.

### Boolean literals

| Word | Token | Value |
|---|---|---|
| `true` | TrueKw | the true `fact` |
| `false` | FalseKw | the false `fact` |

`true` and `false` are reserved keywords that produce `fact` values — the same type
that comparisons produce. They work wherever a `fact` is valid: expression position,
condition position, `return`, `becomes`, arguments, and boolean-typed fields/params/channels.

### Comparison and logic

These are reserved and meaningful in both condition position (`If`, `While`,
`until`) and expression position. They cannot be used as identifiers.

| Word | Token |
|---|---|
| `is` | Is |
| `not` | Not |
| `greater` | Greater |
| `less` | Less |
| `than` | Than |
| `more` | More |
| `and` | And |
| `xor` | Xor |

`or` is reserved too, and is listed under [Failures](#failures) because it carries
`or pass the failure off` as well. `and`, `or` and `xor` are **gates**: each works on `fact`
(one bit) and on `bits` (N bits), with precedence `and` > `xor` > `or`.

### Contextual words — NOT reserved

These are matched by lexeme or shape in specific positions, not by token type.
Outside those positions they parse as regular identifiers and can be used as variable
names, parameter names, field names, and iterator names:

`line`, `lines`, `all`, `input`, `arguments`, `reading`, `writing`, `exists`,
`variable`, `requested`, `output`

**Type names** — `text`, `fact` and `bits` are contextual too. The lexer has no token for any
of them; they are recognised by lexeme in type position, so `Define text as "hi".` is legal.
(`number` *is* reserved, because `the number of s` needs its own token.)

**`axiom`** is contextual on the same terms, recognised only where a **language book's name comes
immediately before it** — `the c-language axiom fragment`. It qualifies under the rule below
because that preceding name is mandatory and there is no other shape in which two identifiers in
type position are followed by a third. `Define axiom as 1.` and `given (the number axiom)` keep
working. The language book's own name is not reserved either, and cannot be: **no module's name
ever is**, because nobody can reserve `inventory` or `parser` in advance.

**Book vocabulary** — `at`, `filled`, `guess`, `shuffled`, `rows`, `columns`, `matrix`,
`random`, `randomly` and `seed` are contextual. A reserved word is taken from *every* program in
the language, whether or not it pulls the book that wanted it; these are recognised by shape
instead, so `Define rows as 5.` works even though the collections book uses the word.

`seed` was the last one held back, on the grounds that `Seed the chance with <n>.` is capitalised
and an identifier must start lowercase. Capitalised contextual statement words removed that
obstacle, and the word is one the code most likely to pull this book will want.

`the rows of x` and `the columns of x` are resolved by the **type of `x`** — a matrix's row or
column count, or a record's field of that name — exactly as `the key of mapping` already is. The
parser cannot tell, but a reader never has the ambiguity. On a matrix they are the only two
members: an element is reached with `the item at (r, c) of m`, never `the item 3 of m`.

**`current`** is contextual, promoted only when `directory` immediately follows it — so
`the current directory` is the working directory while `Define current as 0.`,
`given (the number current)` and `the current of r` all keep working. It qualifies under the
rule below because `directory` is a mandatory following token and is itself already reserved.
Worth having: `current` is a far more tempting variable name than the alternative spelling
`working` would have been, which is why the shorter phrase is also the safer one.

**The rule for when a word can go contextual**, and the three that cannot:
- Its shape needs a **mandatory distinguishing token**. `a matrix with …` has `with`;
  `a random number/item/guess` and `randomly shuffled` each have a required next word.
  **`catalogue` and `atlas` have optional tails** — `a catalogue` alone is valid — so nothing
  separates them from a variable of that name. They stay reserved.
- A **statement-initial** word may be written with a capital even though it is not reserved.
  `Output 7.` and `output 7.` are the same statement, so a contextual word does not force a
  statement to break the capitalise-the-first-word convention. This costs nothing: an identifier
  must start lowercase, so the capitalised spelling was never available as a name in the first
  place, and the lowercase form stays usable (`Define output as 42.`).

  `seed` is contextual on the same terms, recognised by the **`chance` that must follow it**. That
  is a *positive* test rather than a list of shapes to exclude, and it is exact: no statement form
  is `<variable> <name>`, so a variable called `seed` can never be followed by `chance`.
  `Define seed as 42.`, `seed becomes 43.` and `Seed the chance with seed.` all coexist.

**Ordinals** (`first`, `second`, `third`, `fourth`, `fifth`, `sixth`, `seventh`,
`eighth`, `ninth`, `tenth`, `last`) — contextual in the accessor shape:
- **Accessor position:** `the <ordinal> of <series>` / `<ordinal> of <series>` → positional series access
- **Text-edge substring:** `first <count> characters of <text>` / `last <count> characters of <text>`
- **Everywhere else:** ordinary identifier (`Define first as 5.`, `For each last in items,`, param name, etc.)

The accessor shape takes priority in `the <ordinal> of X` — if you name an object field `first`, access
it via `one's first` / `alice's first` (possessive), not `the first of alice` (which is always series access).

### Bit patterns — `0x` / `0b` / `0o`

`bits` is a **bit pattern, not a quantity**. `0o755` is three permission triples, not "seven
hundred fifty-five" — so it is a separate type from `number`, with no implicit conversion in
either direction. `0xFF = 255` is a type error; cross over with `converted to number` /
`converted to hex`.

```cufet
State 0xFF.   // 0xFF   — hex
State 0b1010.   // 0b1010 — binary
State 0o755.   // 0o755  — octal
State 0xDE_AD_BE_EF.   // 0xDEADBEEF — '_' groups digits and is dropped
```

A value **prints in the base it was written in**. Hex digits print uppercase: a computed value
has no literal to take its case from.

**No bare-zero octal.** `0755` is seven hundred and fifty-five, not 493. C's footgun is not
reproduced — octal must say `0o`.

**`_` groups digits, and only in these bases.** Decimal gets no separator at all: grouping in
hex, binary and octal is structural (nibbles, bytes, permission triples), while in decimal it is
cosmetic and in a fraction it marks nothing. It must sit *between* digits — `0x_FF`, `0xFF_` and
`0xF__F` are all errors.

**Ceiling is 64 bits.** That covers every C flag set, file mode and address there is; anything
wider is cryptography or scientific computing, which belongs behind a foreign-function boundary
rather than distorting this type.

### ★ Leading zeros are significant — the width comes from the digit count

This is the one genuinely unfamiliar rule, and it is **unlike C, Java, Rust, Go and Python**,
where `0x0F` and `0xF` are the same value and width is a property of the declared type.

```cufet
State 0xF.   // 0xF   — 4 bits
State 0x0F.   // 0x0F  — 8 bits
State 0x000F.   // 0x000F — 16 bits
```

They compare equal — equality is on value, so `0xF = 0x0F` is true — but they display
differently, and the width is what `not` flips within. That is the point: it is what lets
`not 0xFF` be `0x00` instead of `-6`, with no signed reading and no negative numbers anywhere
in the type. Zero-padding hex to a byte boundary is already a habit; here it carries meaning.

### Gates — `and` / `or` / `not` / `xor`

**A 32-bit AND is 32 AND gates side by side.** So the same words serve a `fact` (one bit) and a
`bits` value (N of them) — the words are already the gate names, already English, and already
Cufet keywords. Only `xor` is new.

```cufet
State 0xFF and 0x0F.   // 0x0F
State 0xF0 or 0x0F.   // 0xFF
State 0b1100 xor 0b1010.   // 0b0110
State not 0xFF.   // 0x00
State true xor false.   // true
```

**Gates do not work on `number`.** A quantity has no bits to combine — `5 and 3` and `not 5` are
type errors. That separation is the point: it is why `not 0xFF` is `0x00` and why the `-6` a
signed reading would give can never appear.

**`not` flips within the value's own width.** `not 0x0` is `0xF` (one digit, four bits) while
`not 0x00` is `0xFF` (two digits, eight). Clearing a bit is `flags and not MASK`.

**Precedence: `and` > `xor` > `or`**, mirroring `&` > `^` > `|`, and all three sit below
comparisons — so `a and b xor c or d` groups as `((a and b) xor c) or d`.

### ★ The result takes the LEFT operand's base and width

```cufet
State 0xFF and 0b1010.   // 0x0A     — hex on the left, so hex out
State 0b1010 and 0xFF.   // 0b1010   — binary on the left, so binary out
```

Left, because in real bit code the left operand is the **accumulator** — `flags or MASK`,
`flags and not MASK` — so it is the thing you care about and will print. Right-dominance would
let a mask's notation hijack the display of the thing being masked.

A result **widens** when the value needs more room (`0b1 or 0xFF` → `0b11111111`) and never
truncates. Nothing silently falls off the end; narrow deliberately with an `and`.

### Crossing between a quantity and a pattern

There is no implicit conversion, so you say which you mean:

```cufet
State 255 converted to hex.   // 0xFF
State 10 converted to binary.   // 0b1010
State 493 converted to octal.   // 0o755
State 0xFF converted to number.   // 255
State 0xFF converted to text.   // "0xFF"
```

**`bits converted to number` can never fail** — 64 bits always fits a number's 96-bit mantissa —
so it gives a plain `number`, not a voidable one the way `text converted to number` does.

Going the other way **raises** if the number is not a whole, non-negative value below 2⁶⁴. Like
arithmetic overflow and unlike text-to-number, these are programming errors rather than a data
condition, so a voidable would only force an unwrap at every crossing.

This is also what makes a **computed** value showable in hex, which a literal-only notation
could never do:

```cufet
Define total as 200 + 55.
State total converted to hex.   // 0xFF
```

To restate a pattern in another base, go through a number: `x converted to number converted to
binary`.

**`hex`, `binary` and `octal` are not reserved** — they are matched by lexeme in this shape
only, so `Define hex as 5.` still works.

### Shifts — `shifted left by` / `shifted right by`

**Shifting is wiring, not a gate** — it moves bits rather than combining them — so it is a
trailing transform in the `sorted` / `trimmed` family, not an operator.

```cufet
State 0b0001 shifted left by 3.   // 0b1000
State 0xFF shifted left by 4.   // 0xFF0  — widened, nothing lost
State 0xFF shifted right by 4.   // 0x0F
```

**The amount is a `number`, not bits.** It counts *positions* — a quantity, like the `3` in
`item 3 of s`. `0xFF shifted left by 0x1` is a type error. It must be whole and non-negative.

**Left shifts widen; right shifts discard the low bits.** That is the one place something
genuinely falls off, and it is not an inconsistency: discarding them *is* a right shift, rather
than a failure of representation. Being unsigned also means there is no arithmetic-versus-logical
right shift — no sign bit, so only one answer.

Shifting left past the 64-bit ceiling raises, like a multiply overflow.

**`left` and `right` are not reserved.** They are matched by lexeme in this shape only, so
`the left of node` and `Define left as 7.` both keep working — a binary tree should not have to
give up its field names to spell one operator.

```cufet
Define n as 8.
State (0b1 shifted left by n) - 0x1.   // 0b011111111 — the standard n-bit mask
```

### Arithmetic — and what has no representation

`+ - * / %` work on two bit patterns. **`/` is integer division**, unlike on numbers — the same
surface with a different meaning per operand type, as matrix arithmetic already does.

```cufet
State 0x0F + 0x01.   // 0x10
State 0xFF * 0x02.   // 0x1FE  — widened to hold it
State 0xFF / 0x10.   // 0x0F   — integer division
State 0x07 / 0x02.   // 0x03   — where 7 / 2 is 3.5
```

Ordering (`<`, `>`, `<=`, `>=`, and the word forms) works too. **Equality and ordering compare
the VALUE**, so base and width are ignored: `0xFF = 0x00FF` and `0o377 = 0xFF` are both true.

**Unary minus is refused** — bits are unsigned and have no negative. You probably want `not`.

**A result with no representation raises**, exactly as division by zero does:

```cufet
State 0x00 - 0x1.   // would be negative, and bits are unsigned
State 0xFFFFFFFFFFFFFFFF + 0x1.   // does not fit in 64 bits
```

These raise rather than becoming value-level failures on purpose. A failure would ride in the
type as `bits or failure` and force an unwrap after *every* masking expression — which is
precisely why division by zero is not one either. Catch them with `In case of exception` if you
need to.

**Width never shrinks back.** The rule is the left operand's width raised to fit, so once a
value has widened it stays wide: `(0b1 * 0x100) - 0x1` prints `0b011111111`, nine digits, not
eight. Narrow deliberately with an `and` if you want the shorter form.

**★ And width is never raised past what the value occupies — including by a shift.** The same
rule governs `shifted left by`, so it widens only when the value grows into the new positions:
`0b00001111 shifted left by 2` is `0b00111100` (width 8, inherited) but `0b0 shifted left by 2`
is `0b0`, not `0b000`. A pattern is the only thing that carries a width, so an all-zero result
has no width to carry and leading zeros that no operand ever held cannot be produced. A program
for which the width is data rather than decoration — a Huffman table, a bit-packer — must track
it in a second field, because **a bits value's width cannot be read as a number**. Both halves
are on the roadmap.

### ★ `and`/`or` short-circuit on facts, and cannot on bits

```cufet-fragment
false and cast f on ()   // f never runs
0x00  and cast f on ()   // f always runs
```

Combining two patterns needs both patterns, so there is nothing to skip. The same word takes a
different evaluation strategy depending on operand type — statically determined, and the same
deliberate exception matrix arithmetic already makes for `+` and `*`. (`xor` never
short-circuits, on facts either: both sides always decide the answer.)

### C's most famous precedence bug is a type error here

In C, `a & b == c` silently parses as `a & (b == c)` and computes nonsense. Cufet has the same
precedence — but the mis-parse produces `bits and fact`, which is **refused at compile time**:

```cufet-refused
State 0xFF and 0x0F = 0x0F.   // type error, not a wrong answer
```

Keeping bit patterns out of `number` closes that footgun for free.

### Comments — `//` and `/* ... */`

The lexer strips both forms before tokenizing — comment content produces no tokens and is never parsed.

```cufet
// to the end of the line
/* a block, which
   may span lines */
Define x as 5.   // inline
```

**`//` runs to the end of the line.** There is no ambiguity with division: `/` is a
single-character token with no lookahead of its own, so `//` could only ever have parsed as
division by a unary slash, which is not an expression Cufet has.

**Block comments NEST** — an inner `/*` opens a nested comment and the outer one ends only at
the `*/` closing it, so commenting out a block that already contains comments works. This is the
one place Cufet differs from C, and it differs the way Rust, Swift and D do. **Unterminated**
(a `/*` whose `*/` never arrives) is a lexer error naming the **outermost** opening line; a `//`
needs no terminator and may end at end-of-file.

A `/*` inside a `//` comment does not open a block, and a `//` inside a block comment is just
text. Comment markers inside a string literal are text — strings are consumed whole before
whitespace-skipping ever looks at them.

---

## 2. Object methods and field access

### The rule: fields are never in direct scope inside methods

The type-checker and interpreter both set up a method's local scope with **only**:
- `one` — the receiver (self)
- The method's own parameters
- Function-valued bindings visible in the enclosing scope

Object fields from the `with (...)` header are **not** put into the method scope.
A bare reference to `nodes` inside a method will be "undefined variable `nodes`"
at both type-check and runtime.

**Always access fields via `one's fieldname`:**

```cufet
Define object stack with (the series of number items):
    Bind void to push, given (the number val):
        Insert val into one's items.
    Done.

    Bind number to pop:
        Define top as the last of one's items.
        Remove the last item from one's items.
        Return top.
    Done.

    Bind fact to is-empty:
        Return the number of one's items is 0.
    Done.
Done.
```

Series operations take `one's field` directly — no local alias needed.

### Local alias pattern (no longer needed for series)

Series operations used to require extracting a field into a local variable first.
That restriction is gone: **all** series operations now accept any expression that
evaluates to a series, including `one's field`, `alice's cards`, etc.

The old pattern:
```cufet-fragment
Define my-items as one's items.    ← was required; now unnecessary
Insert val into my-items.
```

Is now simply:
```
Insert val into one's items.
```

Local aliases are still fine to write if you prefer them for clarity (e.g. when
you reference the same field many times in one method), but they are not required.

### Map and series operations: `one's field` works everywhere

Both map and series operations take `IExpression` for the container argument.
Possessive access works without any local alias:

```
In one's adjacency, the entry for n becomes fresh.          ← OK (map set)
Define val as the entry for key in one's adjacency.         ← OK (map read)
If one's cache has a key for key:                           ← OK (map check)

Insert card into one's cards.                                    ← OK (series add)
Remove first item from one's cards.                         ← OK (series remove)
item i of one's cards becomes updated.                      ← OK (series set)
the number of one's cards                                   ← OK (series length)
Define top as the first of one's items.                     ← OK (series read)
Define val as item 3 of one's items.                        ← OK (series read)
For each x in one's nodes, repeat: ...                      ← OK (for-each)
```

**Mutating ops require an addressable target** — a variable or field access, not
a computed expression. Writing `Add x to (sorted one's cards)` would mutate the
temporary sorted copy and lose the result. The type checker catches this when the
expression's type is not a series.

### Field mutation: `one's field becomes X`

To replace a field's value wholesale (not mutate in place), use possessive
assignment:

```
one's count becomes one's count + 1.
one's label becomes "updated".
```

This produces a `PossessiveSetStatement` and is valid in method bodies.

### Summary table

| Operation | Accepts expression? | Notes |
|---|---|---|
| `Add X to series` | Yes — IExpression | target must evaluate to a series |
| `Add X to the start of series` | Yes — IExpression | same |
| `Add X after item N of series` | Yes — IExpression | same |
| `Remove item N from series` | Yes — IExpression | same |
| `Remove X from series` (by value) | Yes — IExpression | also works on maps |
| `item N of series becomes X` | Yes — IExpression | target must be series/object/record |
| `the number of series` | Yes — IExpression | same |
| `the first/last of series` | Yes — IExpression | read |
| `item N of series` | Yes — IExpression | read |
| `For each x in series` | Yes — IExpression | read |
| `sorted`/`in reverse` on series | Yes — IExpression | read |
| `In map, the entry for K becomes V` | Yes — IExpression | map mutation |
| `the entry for K in map` | Yes — IExpression | map read |
| `map has a key for K` | Yes — IExpression | map read |
| `one's field becomes X` | Yes — PossessiveSetStatement | field mutation |

---

## 3. Value vs. reference semantics

Every Cufet type falls into one of two categories that determine what `Define` and
`becomes` do when they store a value.

**Value types** — copied on every assignment:

| Type | Notes |
|---|---|
| `number` | scalar |
| `text` | scalar |
| `fact` | scalar |
| `record` | deep copy of all fields |
| any object type | deep copy of all fields, including embedded object chain |

**Reference types** — aliased (shared) on every assignment:

| Type | Notes |
|---|---|
| `series of T` | all add/remove/set ops mutate the shared list |
| `map from K to V` | all entry-set/remove ops mutate the shared map |
| `matrix` | `the item at (r, c) of m becomes v` is reflected everywhere |

### The mental model

> **Value type?** `Define copy as original.` gives `copy` a fresh, independent one. Changes to
> `copy` leave `original` untouched.
>
> **Reference type?** `Define copy as original.` gives `copy` another name for the same collection.
> Mutating through either name mutates both.

```
── Value type (objects, records) ────────────────────────────────────────────────
Define alice as a new person { the name "Alice", the age 30 }.
Define bob   as alice.
the age of bob becomes 31.
State the age of alice.    ← "30"  — bob is a deep copy; alice is unaffected

── Reference type (series, maps) ────────────────────────────────────────────────
Define xs as a series of number with (1, 2, 3).
Define ys as xs.
Insert 4 into ys.
State the number of xs.    ← "4"   — ys aliases xs; Insert mutated the shared list
```

The same rule applies when passing arguments to functions and returning values.

### The series-element gotcha

Pulling an element from a series follows the same type rule:

```
Define elem as item N of my-series.
```

Whether `elem` is a copy or an alias depends on **the element's type**, not on the
series itself.

**Value-typed element (record, object, number, text, fact) — you get a copy:**

```cufet
Define deck as a series of records like (the text suit, the text rank) with (
    a record with (the suit "Clubs",    the rank "Ace"),
    a record with (the suit "Diamonds", the rank "2")
).
Define card as item 1 of deck.
the suit of card becomes "Spades".
State the suit of item 1 of deck.    ← "Clubs"  — card is a copy; deck is unchanged
```

`card` received a full copy of the record at position 1. Mutating `card` does not
touch the series. To actually replace the element, use `item N of series becomes`:

```cufet-fragment
Define updated as a record with (the suit "Spades", the rank "Ace").
item 1 of deck becomes updated.
State the suit of item 1 of deck.    ← "Spades"  — the series element was replaced
```

**Reference-typed element (nested series, map) — you get an alias:**

If the element stored in the series is itself a collection (e.g. a `series of series
of number`), then pulling it gives you an alias to that inner collection. Mutating
through the alias is reflected back in the outer series.

### Why this is correct, not a bug

The value/reference split is the **memory model**, and it is what makes the rest of
Cufet work correctly:

**`Add x to one's cards.` mutates the object's actual field** — series are
reference-typed, so evaluating `one's cards` returns the live list stored in the
field. All series operations mutate that list in place; no write-back step is needed.
This is why the series-ops-take-`IExpression` work (gap #3) operates on `one's
field` directly.

**`Define bob as alice.` gives `bob` an independent life** — objects and records are
value-typed, so every assignment is a deep copy. Mutations to `bob`'s fields never
affect `alice`.

The question to ask: *what type is this?* — the type tells you whether you're looking
at an independent copy or a shared alias. Cufet picks the option that matches the
mental model of each kind: scalars and named composites (records, objects) copy;
collections (series, maps, matrices) share.

---

## 4. Comparison forms — both work everywhere

**Both symbol forms and word forms work in both expression position and condition
position.** They are the same operation (compare two values, produce a `fact`);
the choice is purely stylistic.

### Symbol forms

`=`, `<`, `>`, `<=`, `>=` — terse, math-style:

```cufet-fragment
Define same as x = y.
Define big  as x > 100.
Define ok   as x >= 0 and x <= 10.
If x < 10, State "small".
While count < bound, repeat:
```

`=` is equality only — assignment is `becomes`, declaration is `Define ... as`.

### Word forms

`is`, `is not`, `is greater than`, `is less than`, `is 5 or more`, `is 5 or less`
— verbose, sentence-style:

```cufet-fragment
If x is 5:
If x is not 3:
If x is greater than 10:
If x is less than 10:
While x is less than bound, repeat:
Define in-range as (x is 5 or more).
```

### Idiomatic guidance

Word forms are the **recommended, taught style** for `If`/`While` conditions —
they read like English sentences. Symbol forms are natural in expression position
— they read like math. But either form is accepted everywhere; reach for whichever
reads better in context.

```cufet-fragment
If x < 10, State "small".              ← symbol in condition — works, terse
If x is less than 10, State "small".   ← word in condition — idiomatic, recommended

Define big as x > 100.                 ← symbol in expression — works, idiomatic
Define big as (x is greater than 100). ← word in expression — works, verbose
```

### Equivalence: `=` and `is` both mean equality

`=` and `is` (without a following `greater`/`less`/`not`) both perform equality
comparison — they produce identical AST nodes. Use whichever reads better:

```cufet-fragment
If x = 5, State "five".    ← symbol equality in condition — works
If x is 5, State "five".   ← word equality in condition — idiomatic
State (x = 5).             ← symbol equality in expression — idiomatic
State (x is 5).            ← word equality in expression — works
```

### Negated word forms

The negated word forms are valid — they map to the corresponding comparison:

| Form | Equivalent | Meaning |
|---|---|---|
| `is not greater than X` | `<= X` | at most X |
| `is not less than X` | `>= X` | at least X |
| `is not X` | `!= X` | inequality |
| `is not equal to X` | `!= X` | inequality (verbose form) |

```cufet-fragment
If count is not greater than 10:           ← count <= 10
While x is not less than 0, repeat:        ← x >= 0
If name is not "admin":                    ← inequality
Define ok as (score is not less than 50).  ← expression position
If count is not equal to 0:                ← verbose inequality
```

Both `is not X` and `is not equal to X` mean the same thing — use whichever
reads more naturally in context.

### Invalid forms

**`is more than` is a compile error — use `is greater than` instead.** The parser
catches `is more than` and emits: *"did you mean 'is greater than'?"*

```cufet-refused
While count is greater than 0, repeat:   ← CORRECT
While count is more than 0, repeat:      ← COMPILE ERROR
```

### Boolean literals: `true` and `false`

`true` and `false` are **reserved keywords** that produce `fact` values — the same
type comparisons produce. Use them freely wherever a boolean is needed:

```
return true.
Define flag as false.
If result is false, State "failed".
While keep-going is true, repeat: ... Done.
Send true through ch.           ← channel of fact
```

The old workaround (`1 = 1` for true, `1 = 0` for false) is no longer needed and
is retired from documentation. It still *works* — `1 = 1` is a valid comparison
that yields a `fact` — but `true`/`false` are the natural forms.

### Negating a fact in condition context

`not (fact-expr)` negates a boolean expression:

```cufet-fragment
While not (cast is-empty on pq), repeat:
If not (visited has a key for neighbor):
```

`is false` and `is true` also work as direct comparisons now that `false`/`true`
are keywords:

```cufet-fragment
While (cast is-empty on pq) is false, repeat:   ← NOW CORRECT
If result is true, State "ok".                   ← NOW CORRECT
```

---

## 5. Which operations accept expressions vs bare names

Every series and map operation now accepts an **IExpression** for the container
argument — `one's field`, `alice's cards`, a variable name, or any expression that
evaluates to the right type. There are no bare-name-only positions left in the
series/map layer.

### Series operations — all accept IExpression

| Syntax | Read or mutate? |
|---|---|
| `Add X to expr.` | mutate |
| `Add X to the start of expr.` | mutate |
| `Add X after item N of expr.` | mutate |
| `Remove the last item from expr.` | mutate |
| `Remove item N from expr.` | mutate |
| `Remove X from expr.` (by value) | mutate |
| `item N of expr becomes X.` | mutate |
| `the number of expr` | read |
| `the first/last of expr` | read |
| `item N of expr` | read |
| `For each x in expr, repeat:` | read |
| `expr sorted` / `expr sorted by field` / `in reverse` | read |

**Mutating ops** (`Add`, `Remove`, `item N of ... becomes`) require an
addressable target — a variable or field reference (`my-series`, `one's cards`,
`alice's hand`), not a computed expression. Passing a non-series expression
(e.g. a number) is a **static type error**:

```
Insert 1 into (x + y).   ← TYPE ERROR: (x + y) is not a series
```

### Map operations — all accept IExpression

| Syntax | Read or mutate? |
|---|---|
| `In expr, the entry for K becomes V.` | mutate |
| `the entry for K in expr` | read |
| `expr has a key for K` | read |
| `the size of expr` | read |
| `For each pair in expr, repeat:` | read |

### Collections book — matrix operations

Every form below needs `Pull a book on collections.` in scope, because that is what puts the
`matrix` type there. Indices are **1-based**, and a matrix holds numbers and nothing else.

| Syntax | Read or mutate? |
|---|---|
| `a matrix with ((1, 2), (3, 4))` | construct — rows given literally, rectangularity enforced |
| `a matrix with R by C` | construct — sized, every cell zero |
| `a matrix with R by C filled with V` | construct — sized, every cell `V` |
| `the item at (r, c) of m` | read |
| `The item at (r, c) of m becomes V.` | mutate |
| `the rows of m` / `the columns of m` | read |
| `cast collections's transpose of (m)` | read — returns a new matrix |

`R`, `C` and the indices are any number expressions; `R` and `C` must be positive whole numbers,
checked at compile time when literal and at runtime when computed. A matrix is a **reference
type**, so a write is seen through every name for it — see
[Value vs. reference semantics](#3-value-vs-reference-semantics).

### Collections book — matrix arithmetic

Matrix arithmetic operators (`+`, `-`, `*`) are available inside `Pull a book on collections.`
blocks. All three are **fallible** — dimension mismatch produces a failure that must be handled.

| Expression | Semantics | Dimension requirement | Failure category |
|---|---|---|---|
| `a + b` | element-wise add | `a` and `b` have identical dimensions | `"dimension-mismatch"` |
| `a - b` | element-wise subtract | `a` and `b` have identical dimensions | `"dimension-mismatch"` |
| `a * b` | matrix product | `a.columns == b.rows` | `"dimension-mismatch"` |

`*` is **matrix product** (the standard dot-product triple-loop, yielding an `m×p` result
from an `m×n` left and an `n×p` right). It is NOT element-wise multiplication.

**Strict-fallible rule applies** — using any of these operators outside a `Try to:` block or
without `but on failure <default>` is a static type error:

```cufet-refused
Pull a book on collections.
    Define lhs as a matrix with ((1, 2), (3, 4)).
    Define rhs as a matrix with ((5, 6), (7, 8)).
    Define sum as lhs + rhs.       ← TYPE ERROR: matrix '+' can fail — you must handle the failure
    Try to:
        Define sum as lhs + rhs.   ← OK — inside Try block
    Done.
    In case of failure:
        State "dimension mismatch".
    Done.
    Define sum as lhs + rhs but on failure (a matrix with 2 by 2).  ← OK — inline handler
Done.
```

**Not defined — clear static errors:**
- `matrix / matrix` — matrix division is not a single operation (would need matrix inversion,
  which is deferred). Produces a type error: "arithmetic requires numbers on both sides."
- `matrix * number` (scalar multiply) — mixed-type binary is not supported. Produces the same
  arithmetic type error. Scalar scaling is deferred.
- Element-wise multiply (Hadamard product) — deferred; if ever added, it will be a named
  collections function (`Cast collections's element-wise-multiply on (a, b)`), NOT an operator
  (`*` is reserved for matrix product — one canonical way).

### Chance book operations

All randomness operations require `chance` to be in scope — `Pull a book on chance.`
must appear before any chance expression or `Seed` statement. Using them without a
pull is a **static type error** (TypeException).

| Syntax | Return type | Notes |
|---|---|---|
| `a random number from low to high` | `number` | Whole numbers, inclusive; `low > high` → RuntimeException |
| `a random item from series` | `voidable T` | Empty series → `void`; pair with `but void is default` |
| `randomly shuffled series` | `series of T` | Non-mutating; returns a new series |
| `a random guess` | `fact` | 50/50 true/false |

**Seed statement** — reseeds the per-interpreter RNG:

```cufet-fragment
Seed the chance with 42.
```

The seed must be a `number`. Seeding makes the sequence reproducible; without an
explicit seed the RNG is entropy-seeded on interpreter creation.

**Bound-expression level** — `low` and `high` in `a random number from low to high`
are parsed at addition level (same precedence as `the characters from N to M of text`).
Arithmetic works; logical/comparison forms do not:

```cufet-fragment
State a random number from 1 to n + 5.    ← OK
State a random number from 1 to 6.        ← OK
```

**Series target** — `a random item from <series>` and `randomly shuffled <series>`
parse their target with `ParseCorePrimary`. Identifiers, possessive access
(`one's cards`), parenthesized expressions, and series literals all work.

**Type-matching `but void is`** — `a random item from series` returns `voidable T`.
The fallback in `but void is` must match the element type `T`:

```cufet-fragment
Define picked as a random item from xs but void is 0.   ← OK for series of number
Define picked as a random item from xs but void is "".  ← OK for series of text
```

### `Pull ... Done.` — books and rabbits

★★ **A MODULE's body may reach for a module its caller pulled. A plain function may not.** The
difference is not a special case — a module is pulled INTO a block, so its methods run there and
inherit what that block pulled, and the pull site is where a missing one is reported. Nothing pulls
a plain function into anything, so there is no block for it to inherit from and nowhere the debt
could be checked; a function reaching an unpulled module name is refused where it is WRITTEN.

⚠ A body written *inside* a pull is unaffected and is the ordinary way to write this — the pulled
names are in its lexical scope like any others:

```cufet
Pull a book on math.
    Bind number to rooted, given (the number x):
        Return (math's square-root of (x)) but void is 0.
    Done.

    State cast rooted on (25).
Done.
```

A function written outside one pulls what it needs itself.

`Pull` opens a scope; `Done.` closes it and frees whatever was pulled.

**Single book:**
```cufet
Pull a book on math.
    Define r as math's square-root of 16.
Done.
```

**Multiple books (shared scope, one `Done.`):**
```cufet
Pull books on math, collections, and chance.
    Define m as a matrix with ((1, 2), (3, 4)).
    Define n as a random number from 1 to 6.
Done.
```
Plural `books` for two or more; singular `a book` for one. Number matches count.

**Per-book aliasing (each entry independently optional):**
```
Pull books on math as m, collections as c, and chance.
    ...
Done.
```

**Module — the general form:**
```cufet-fragment
Pull greeting-kit.
Pull greeting-kit as kit.
```
Any object conforming to `module` (`Define object greeting-kit with () and module:`). The
article is noise, so `Pull a greeting-kit.` is the same statement.

★ **This is not a fourth form — it is the form the others are special cases of.** `Pull a
rabbit.` was always `Pull <name>`:

```cufet-fragment
Pull greeting-kit.
Pull greeting-kit as kit.
Pull rabbit.
```

⚠ **A BOOK IS NOT PULLED THIS WAY.** `Pull math.` is refused; write `Pull a book on math.` The
plain form is for a module you hold one of. `Pull math.` used to work and was never a decision —
the general branch swallowed the name on its way past, and a test pinned the accident. Reversed
2026-08-21.

```cufet-fragment
Pull a book on math.                      ← something you consult
Pull a book on math as m.
Pull books on math, and collections.
```

⚠ **A module carries OBJECT TYPES only.** A union of them works and narrows with `Judge`, because
a union is built from object types. An interface does not — a module body takes `Define object` and
nothing else. Axioms need no home here: a module’s METHODS hold them, which is how
`tools/terminal.cufe` reaches `termios`.

### ★ `book` is a subtype of `module`

A **book** is a module you CONSULT rather than one you have one of — what another language would
have made a header file. It is declared with `and book`, and every book is a module:

```cufet-fragment
Define object shapes with () and module:      ← you have one of these
Define object trigonometry with () and book:  ← you consult this one
```

★ **There is one mechanism, not two.** Headers and modules are the same thing here; `book` is the
narrower job rather than a separate kind. Everything downstream of a pull — members, scope,
privacy, the loader — treats a book exactly as it treats any module.

⚠ **The spelling must match what the thing is.** A book pulled plainly, or a module pulled as a
book, is refused and the message names the fix. This is what *"the surface says which KIND of thing
you are pulling"* always claimed; until `book` existed it could only be enforced for the bundled
ones, because a writer had no way to say which kind theirs was.

⚠ **`Pull books on …` applies ONE spelling to every name in it**, so they must all be books. A book
and a module you hold one of cannot be pulled in a single statement — they nest:

```cufet-fragment
Pull a book on math.
    Pull a shapes.
        State cast shapes's scaled on (math's pi).
    Done.
Done.
```

★ **Either form reaches another file.** The loader used to run only for the book form, which made
one word carry two things — what the thing IS, and where it happens to live. Splitting a module
into its own file no longer means calling it something it is not.

★ **No module's NAME is reserved, and neither is `book` or `books`** (both freed 2026-08-19).
`math`, `collections`, `chance`, `rabbit`, `book` and `books` are all ordinary identifiers, so
`For each book in books, repeat:` reads, and a writer may even define a module named `book` and
reach it with `Pull book.`

The rule this settles, and it has to hold for modules that do not exist yet: **a module's name
can never be reserved**, because nobody can reserve `inventory` or `parser` in advance. The
parser still *recognises* these words where they do a job — pulling a rabbit opens a region,
`Have rabbit …` addresses the enclosing one, and `book`/`books` open the `… on <name>` spelling
when `on` follows — because recognising a word is not reserving it.

`book on <name>` is **required** for a book, and it is required because it is what reads: *a book
on math* is natural English and *a math* is not. Pulling is one mechanism — the same question is
asked at every pull site — but the surface says which KIND of thing is being pulled, because
something you consult and something you have one of are not the same thing.

⚠ A book used to be described as conforming by CONSTRUCTION, "its members are native, so there
is no `Define object` to carry `and module`". **That stopped being true in 0.16.0**: `math` has
no native part left at all and `collections`'s only native piece is the `matrix` type. Both are
written in Cufet now, behind a Cufet layer. What makes something a book is that it says `and
book`, not how it happens to be implemented — and nothing about the bundled ones is privileged.

`module` is a **marker interface**: it requires no methods, only the claim. An object that
does not conform is refused at the pull site. Pulling **instantiates**, so a module with
fields is refused — a pull has nowhere to put their values; use `a new <type> { … }`.

### What a body may reach for

**A function or method resolves the names it can see where it is WRITTEN — plus any MODULE its
caller pulled.** Those are the only two, and the second is the whole reason the first has an
exception: a pulled module is a capability of the block that uses the body, which is what lets a
module's method say `math's pi` and leave `math` to whoever pulls it.

A name that is neither is refused **when the program is checked**, not when the line runs:

```cufet-refused
Bind number to sneaky:
    Return borrowed + 1.        ← `borrowed` is a local of the CALLER — refused
Done.
```

⚠ That refusal is newer than the rule (2026-08-21). Every unresolved name in a body used to defer,
which is dynamic scoping, and nothing ever wanted it — it came along with the module rule because
the rule was applied to *every* name rather than to module names. A plain typo was
indistinguishable from a capability and waited until the line ran.

★ **"Module" means DECLARED — a bundled book, or an object marked `and module`. An alias is not
one.** `Pull math as m.` makes `m` an ordinary name in that block, so a body written *inside* the
pull sees it like anything else; a body written OUTSIDE it does not. That shape worked only while
every caller happened to choose the same alias — rename it at one call site and the function breaks
with nothing to point at. An alias is for the block that makes it, not something to publish.

★ A **lambda** is not a detached body at all: it captures its enclosing scope, so it sees
everything lexically around it, aliases and locals included.

⚠ Known gap: a module's needs are checked at its pull, but they are not transitively closed. A
module that reaches `math` only through a free function it calls is not caught, and neither is a
function reached through a variable rather than by name. Bounded to module names, where it used to
be every name.

★ **A bundled book's name is reserved for the book.** Defining an object named `math`,
`collections` or `chance` is refused at the definition — it used to be legal and simply
unpullable, silently shadowed by the book at the pull site. The same wall has no side doors:
`a new collections { }` is refused (**`Pull` is the only constructor** — a book is a
scope-thing, and its construction is the bracket), and `unto` may not target a bundled book
(it would splice a writer's member straight onto the book). The prelude's own definitions of
`collections` and `math` are the one exception: each is its book's **Cufet layer**, and a book
and its layer resolve as ONE module. A member the layer defines is ordinary Cufet method (or
getter) dispatch; whatever the layer does not define is still the native book's — all reached
through the same pulled name. Today **both bundled books are written entirely in Cufet** — the
native side of `collections` only introduces the `matrix` type, and `math` has no native part
left at all. A book the program never pulls is dropped from it, so an unused one costs nothing.

**Rabbit (singular only):**
```
Pull a rabbit as hopper.
    ...
Done.
```
Anonymous form (`Pull a rabbit.`) omits the name. A NAMED rabbit can be given work by name —
`Have hopper start a task as job:` — as well as through the bare `rabbit` keyword, which means the
enclosing one. Naming a rabbit pulled further out is refused: a task is joined by its rabbit's
`Done.`, so it would have to outlive the block it is written in.
 Rabbits stay singular — multiple arenas
usually want independent lifetimes (nest two `Pull a rabbit` blocks with separate `Done.`s).

**Nesting** — any `Pull ... Done.` scope can nest inside another:
```
Pull a book on math.
    Pull a rabbit.
        ...
    Done.
Done.
```

**Bind transparency** — `Bind` declarations inside a `Pull ... Done.` body are treated as
top-level (the pull scope does not count as a "block" for `Bind`-placement purposes).
Hoisting passes (functions, objects, overloads) see through pull bodies automatically.

---

### `Have rabbit start a task` — structured cooperative task spawn

Spawns a cooperative structured task inside the enclosing rabbit's scope.

**Syntax:**
```
Pull a rabbit.
    Have rabbit start a task:
        ... task body ...
    Done.
    Have rabbit start a task as <name>:
        ... task body ...
    Done.
    ... more statements ...
Done.   ← all tasks spawned in this rabbit JOIN here
```

- **Requires an active rabbit** — `Have rabbit start a task` must appear inside a
  `Pull a rabbit. ... Done.` block. Using it outside any rabbit is a parse error.
- **Optional name** (`as <name>`) — binds an identity so the task's result can be awaited
  with `the awaited result of <name>`. Both backends support this; an anonymous task is
  fire-and-forget and its `return` value is dropped.
- **An await may appear inside another task**, not only in the rabbit body, and several tasks
  may await the same task. The awaited task must be declared **earlier**, since its name has to
  be in scope — which makes the wait graph acyclic by construction, so tasks cannot deadlock on
  each other. Awaiting a task declared later is a type error rather than a hang.

**Semantics:**

- **Cooperative when interpreted, genuinely parallel when compiled.** Interpreted, tasks run
  on the cooperative scheduler — one at a time, interleaving only at explicit yield points.
  Compiled, each task is a real OS thread. ★ **This is the one place the two backends
  deliberately differ**, so *do not* write a program that depends on a particular
  interleaving: it is not a specification. Everything below holds on both.
- **Structured (the key guarantee)** — every task spawned inside a rabbit **joins at that
  rabbit's `Done.`**: the `Done.` handler waits for all spawned tasks to complete before
  releasing the scope. A task **cannot outlive its enclosing rabbit**.
- **Sound by construction** — because tasks join before `Done.`, they are shorter-lived than
  their rabbit. The existing region depth machinery (`CheckRegionStore`) handles escapes:
  a task-local reference-type value cannot be stored into a longer-lived container.

**Sharp edge — captured-state mutation (native-era concern, deferred enforcement):**
Task bodies may read variables from the enclosing rabbit scope. They should not **mutate**
captured reference-typed variables from that scope. Under the cooperative scheduler this is
safe (one task runs at a time — no actual races). The native era will introduce true
parallelism, at which point unsynchronized mutation becomes a data race. Slice 2 names this
constraint but does not enforce it; native-era tooling will.

**Example:**
```cufet
Pull a rabbit.
    Have rabbit start a task:
        State "task A".
    Done.
    Have rabbit start a task as worker:
        State "task B".
    Done.
    State "during rabbit".
Done.
State "after".
```
Output: `during rabbit`, `task A`, `task B`, `after` — both tasks complete before `after`.

---

## 6. Where constructs are allowed

### `Define` is forbidden inside object bodies

Only `Bind` (methods), `Get` (getters), and `Set` (setters) are allowed inside an
object definition body. Field declarations go in the `with (...)` header:

```
Define object graph with (the series of node nodes,          ← fields here
                          the map from node to series of edge adjacency):
    Bind void to add-node, given (the text name):            ← methods here
        ...
    Done.
Done.
```

`Define nodes as a series of node.` **inside** an object body is a parse error.

### Named constructors initialize empty complex fields

When fields have types like `series of T` or `map from K to V`, you cannot write
`a new graph` to create an empty instance — you must supply all field values.
Use a `Bind making a` constructor to hide that:

```cufet-fragment
Bind making a graph to new-graph:
    Define empty-nodes as a series of node.
    Define empty-adj   as a map from node to series of edge.
    Return a new graph { the nodes empty-nodes, the adjacency empty-adj }.
Done.
```

Call it with `cast new-graph`.

### Getters: `Get name as type:`

Computed properties are declared inside an object body with `Get name as type:`.
The body has access to `one` (the receiver). Accessed via `obj's name` or
`the name of obj` — uniform access, same syntax as stored fields.

```cufet
Define object card with (the text suit, the text rank):
    Get label as text:
        Return one's rank joined to " of " joined to one's suit.
    Done.
Done.

Define c as a new card { the suit "Spades", the rank "Ace" }.
State c's label.                ← "Ace of Spades"
State the label of c.           ← same thing
```

Getters access fields through `one` exactly like methods:

```cufet
Get count as number:
    Return the number of one's items.
Done.
```

### `Bind overloading` is top-level only

Operator overload declarations cannot appear inside any block:

```cufet-fragment
Bind overloading +, given (the lhs is a vec2, the rhs is a vec2):
    Return a new vec2 { the x lhs's x + rhs's x, the y lhs's y + rhs's y }.
Done.
```

This must be at the top level of the file. Placing it inside a function body,
conditional, loop, or Try block is a parse error.

### `Bind` inside bodies — only in another function or object

Function declarations (`Bind`) are allowed:
- At top level
- Inside another function body
- Inside an object body (methods)

They are **not** allowed inside `If` arms, loop bodies, `Try` blocks, or `With`
blocks.

### `a series of T with (...)` as an expression

A series literal (empty or pre-populated) can appear anywhere an expression is
expected:

```cufet-fragment
Define xs as a series of number.
Define suits as a series of text with ("Spades", "Hearts", "Diamonds", "Clubs").
Define primes as a series of number with (2, 3, 5, 7, 11).
Return a series of text.
In adjacency, the entry for n becomes a series of edge.   ← in a map-set
```

Note: in `In map, the entry for K becomes <expr>.`, the value is an IExpression
— so a series literal here is fine.

But `a series of T with ()` cannot be the fallback in `but void is` when nested
in certain positions (it parses ambiguously as a type rather than a value literal
in some contexts). Use a local variable:

```cufet-fragment
Define empty as a series of edge.
Define edges as the entry for src in adjacency but void is empty.
```

### `a map [from K to V] with (...)` — the three map forms

`map` in expression position always requires a `with (...)` clause. The optional
`from K to V` annotation controls whether types are explicit or inferred:

```cufet-fragment
Define m as a map from text to number.                 ← empty, typed
Define m as a map from text to number with ("k": 1).   ← typed + populated
Define m as a map with ("k": 1).                       ← inferred types
```

`new` is not used for map construction. `a map from K to V` without `with (...)`
is a **type annotation** only — it names a type, not a value.

### For-each iterator names must be identifiers (with one exception)

The iterator variable in `For each <name> in <series>` must be an `Identifier`
token. Any reserved keyword is illegal here, even if it "feels" natural. Common
traps:

- `entry` is reserved → use `pair`, `kv`, or another name
- `start`, `from`, `to`, `end` are reserved → avoid them
- `it` is reserved for the bare-`it` loop form

**Exception — `item` is allowed** as an iterator name in both the series form
(`for each item in my-list`) and the pipe consumer form (`for each item from the
input:`). Inside the loop body, bare `item` (not followed by a number or `at`) is
treated as a variable reference. See §7 for details.

### `to` and `from` cannot be field names

These are reserved structural keywords. If an object needs a source/destination
field, use `src`, `dest`, `origin`, `target`, etc.

### Reserved keywords are never valid field names

The parser's `the <name> of <expr>` heuristic (which disambiguates named field
access from other `the X of Y` constructs like series length, type annotations,
etc.) now excludes the **entire reserved-keyword set** uniformly: no keyword can
ever be a user-defined field name, because field names are identifiers and
keywords are not.

**Practical consequence:** adding a new keyword to the language cannot introduce
a new field-name mis-fire. The exclusion is automatic and complete — you will
not see a repeat of the `the series of number board` n-queens mis-parse.

**Three narrow exceptions kept valid:**
- `key` — for `the key of mapping` (key-value pair access)
- `category` — for `the category of the failure` (failure type property)
- `characters` — for `the characters of r` (user-defined field), disambiguated
  from the substring syntax `the characters from N to M of text` by the presence
  of `from` after the keyword (which causes `IsNamedAccessPattern` to return false
  since it requires `of` immediately after the name)

**The lookahead is no longer what decides.** Approach B shipped: the parser marks the positions
where a TYPE is being read, and the `the <name> of …` guess is not consulted in any of them. The
keyword exclusion above still stands as the rule for field NAMES, but it is no longer carrying the
weight of telling a type from an expression — position does that, which is the one thing lookahead
could never do. `the stack of number counts` and `the city of alice` are the same tokens; only
where they sit differs.

**What this fixed, which the exclusion could not:** a user-defined generic written as a type. A
built-in leads with its own keyword (`series of number`), so the exclusion covered it — but
`the stack of number counts` starts with an identifier, and no exclusion list can hold it without
also taking away every field access. It was refused everywhere a type is written.

### ★ What a burying body may contain

A body holding a `bury` is rewritten into a state machine before either backend
sees it: the block it stops in becomes a step number, and every local that
outlives a resumption is stored beside it. That rewrite is what constrains the
body, and everything below follows from it.

**Allowed, and each keeps its state across a resumption:** straight-line
statements, `If` / `Otherwise`, `While`, `Repeat until`, `For each` over a
series or a stash, `Stop`, `Skip`, nested loops, a reassigned parameter, and a
local of any type — including a series or a map being built up item by item.

A `For each` over a **stash** is allowed here because it is not a loop the
machine has to learn: it is rewritten into its drain — `unbury`, a void test, the
body — before the machine sees it, so what gets linearised is a `Repeat until`
holding a `Define` and an `If`. Inside a burying body this is **delegation**, one
stash consumed while another is produced.

An `If` that **narrows** is allowed and keeps its narrowing, in the arm and in
its `Otherwise` alike: the condition that reached the block is carried in and
re-tested on entry (the `Otherwise` uses the negated form). That is not a branch —
every local is restored from its slot first, so the test gives the answer it gave
the first time. It exists so the type is known again.

⚠ Both narrowing forms count: `x is a <type>` and **`x is not void`**. The second
was missed for a while, and the failure is worth knowing because of its shape —
the arm lost its guard, so the block ran with `x` back at the `voidable T` its
slot holds. The interpreter narrows by value and never noticed; the compiler
refused `x is greater than 3` for operating on a voidable. A front-end rewrite
that drops a guard shows up as a **backend divergence**, not as a bad rewrite.

**A METHOD may bury**, nested or `unto`, on exactly the same terms as a function —
it takes a rabbit, its declared type is what it buries, and it has no `Return`.
The rewrite turns one method into **two methods**, not two functions: the dispatch
reads `one's <field>` the way the body it came from did, so the receiver has to
still be there to resolve against. The closure the factory hands back captures
`one`, which is what makes the state belong to the **instance** — two of a type
give two stashes that share nothing.

⚠ "Does this bury" is answered per `(type, method)`, never per name. Two types may
each have a `ticks` with only one of them burying, and a name-keyed answer would
rewrite the wrong one — or tell the ordinary one its `number` return type is a
stash.

A `Judge` is allowed too, and keeps both the narrowing and the binding. `it`
becomes an ordinary local, so the subject is evaluated once and `it` is restored
from its slot on every re-entry rather than re-evaluated; the arm's cases then
guard the block the way an `If` arm's condition does. A **grouped** arm states
itself as a disjunction (`it is a number or it is a fact`), and an `Otherwise`
states the cases the arms left over the same way.

⚠ **A nested `Judge` inside a burying body rebinds `it` at a narrower type**, and
one name may hold one type in a burying function — so it is refused, by that rule
rather than a judgement-specific one. Bind the inner subject to a name of its own.

**Refused when the program is checked**, so both backends refuse identically:

| Shape | Why |
|---|---|
| `Return` anywhere in the body | A burying function finishes by reaching its end; the stash reports that with `void`. Two ways to say "spent" is one too many. |
| `Bury` inside the `Otherwise` of a judgement on a **non-union** subject | The leftover cases have to be named to resume into them, and only a closed union lists what they are. |
| `Bury` inside `Try to` or `Pull a rabbit` | A handler and a region are context a resumption cannot restore. |
| `Bury` inside `For each` over a **map** | Resuming counts back to where the loop was, and a map's entries have no position to count to. Loop over a series, or use `While`. |
| `Define a shadow` anywhere in the body | Every scope in the body flattens into one, so the shadow would land on the name it was written to hide. |
| One name used at two types in the body | Sibling blocks become one place to store it, and one place holds one type. Legal everywhere else in the language. |

### `For each` takes three kinds of source

| Source | Iterator holds | Ends when |
|---|---|---|
| a series | the element type | the last item is done |
| a map | a `mapping` (`the key of`/`the value of`) | the last entry is done |
| a stash | the **buried type**, not `voidable` of it | the stash answers `void` |

Anything else is a static error: *"Only series, maps and stashes can be looped
over."*

The stash form is a **front-end rewrite**, not a third loop. This:

```cufet-fragment
For each value in counter, repeat:
    State value.
Done.
```

*is* this — the drain that was written by hand before the loop existed:

```cufet-fragment
Repeat:
    Define value as unbury counter.
    If value is not void:
        State value.
    Done.
    Otherwise:
        Stop.
    Done.
Until false.
```

Three things follow from that being the *definition* rather than a description.
`Stop` and `Skip` in the body land on the `Repeat until`, so they mean exactly
what they mean in any loop — and the rewrite's own `Stop` runs before the body is
reached, so the two can never collide. The iterator is a plain `T` because the
body sits inside the `is not void` arm. And neither backend learns anything: what
they run is statements they have always run.

⚠ The `is not void` polarity is load-bearing. Cufet narrows a name **inside an
arm**, not for the rest of a block on the strength of an early exit — so the
body has to be *in* the arm. Written the other way round (`If value is void:
Stop. Done.` then the body) every iterator would be a `voidable T`.

### ★ `stash of T` is an ordinary value type

A stash goes where any value goes — a local, a parameter, an element of a series
— because it lowers to a CLOSURE, and every `stash of T` is therefore the same
two-pointer shape. No vtable is implied and no narrowing is needed.

The front end keeps `stash of T` and `voidable T function given ()` as separate
spellings on purpose: it is what makes an error say *"stash of number"*, and what
stops a stash being `cast` directly instead of unburied. The back end must not,
because a `stash of T` parameter has to accept exactly what `cast`ing a burying
function produces — so the front end substitutes one for the other on the way
out, and no `StashType` survives into either backend.

**A stash lives in the region whose rabbit buried it, and dies with that region.**
A rabbit is REQUIRED — `Have <rabbit> bury <v>.`, never a bare `Bury v.` — because
burying is memory work and a rabbit is the agent that does memory work. A burying
function takes one as a parameter, so the ownership arrives with the job instead of
being ambient. Inside a burrow a stash does everything it does anywhere: be
unburied, handed to a function, put in a series, held in a field. Carrying one OUT
and unburying it later is refused, by the same rule that refuses any closure over
rabbit-scoped state, because that is what burying somewhere means.

⚠ Today that refusal is the COMPILER's, not the checker's, so the escaping
program still interprets — `check --native` reports it as a warning. Under the
rule above the interpreted behaviour is the wrong one: it digs silver out of
ground that no longer exists.

An object **field** may hold one too — `Define object ticker with (the stash of
number source, the text name):` — on both backends.

An ordinary function-valued field works the same way — `the number function twice
given (a number)`. ⚠ The field NAME sits between `function` and `given`, the same
order a function-typed parameter uses; `void` is legal there only as the return
type (`the void function log`), never as a field type on its own.

### ★ Foreign source — an `axiom` in `[ ... ]`

Source in another language, held as a value. `axiom` names the **contract**: it is taken as given
without proof, which is exactly what Cufet does with it — it cannot check a C listing and does not
try.

```cufet
Pull a book on the c-language.
    Define c-language number get-pid as [getpid()].
    State cast get-pid.
Done.
```

**Square brackets appear nowhere else in the language**, and that is why they are the delimiter:
this is the one construct whose contents are not Cufet at all, so reusing existing punctuation
would need disambiguating by context — the last thing wanted around foreign text.

**The tag can be shortened but never dropped.** `Define a c-language axiom x as [ … ].` may be
written `Define c-language x as [ … ].`, because the brackets already say *axiom*. They cannot say
*which* language, and the tag names who reads it — so `Define x as [ … ].` is refused. Inferring it
from what happens to be pulled would make a line's meaning depend on scope above it, and break the
moment two language books are pulled together.

**The language's book must be pulled** — `Pull a book on the c-language.` A language book has no
members; you pull it to write axioms in that language at all. Aliasing works (`as c`), and the tag
still names the language rather than the alias.

**What an axiom gives back is declared where the axiom is WRITTEN**, and the tag qualifies the
axiom rather than the result — `c-language number add` is a C-language axiom that yields a number.
Both middle words drop:

```
Define the c-language number axiom add, given (the number left, the number right), as [ ... ].
Define c-language number add, given (the number left, the number right), as [ ... ].
Define c-language axiom get-pid as [getpid()].        ← says nothing about the result
Define c-language get-pid as [getpid()].
```

⚠ **An axiom that says what it gives back is an EXPRESSION you run.** Cufet cannot read a C
listing, and an `int` might be a number, a fact or a handle — so whoever wrote the source says
which, once.

★★ **An axiom that says NOTHING about its result is SOURCE, and cannot be run.** It is pasted once,
above every wrapper, so anything it declares is in scope for the axioms that come after it:

```cufet
Pull a book on the c-language.
    Define c-language helpers as [static int twice(int x) { return x * 2; }].
    Define c-language number four as [twice(2)].
    State cast four.
Done.
```

That is how two axioms SHARE a helper. A helper declared inside one axiom belongs to that axiom
alone, and the alternative — splicing one axiom into another — was declined: it merges their
parameter namespaces and makes the article substitution a regex over someone else's text. A
preamble has neither problem, because it takes no parameters and substitutes nothing.

⚠ **A resultless axiom therefore takes no parameters.** A parameter's value comes from a call, and
nothing calls a preamble.

⚠ **Nothing in it is guarded or checked.** A wrapper's guards ask what the spliced EXPRESSION
produces; a declaration produces nothing. What is written reaches the C compiler verbatim, and a
mistake in it is reported as the author's C — which it is.

### ★ An axiom's parameters, and splicing by the article

An axiom declares what it takes the way every Cufet body does, and reaches those values inside the
foreign text **by the article**:

```cufet-fragment
Define c-language open-read-only, given (the text file-path), as [open(the file-path, O_RDONLY)].

Define the number fd as cast open-read-only on ("/etc/hostname").
```

`the file-path` is never valid C or SQL — it is English sitting in code that is not English — so
nothing has to be escaped or disambiguated. It reads as the line that declares it: `the text
file-path` in `given`, `the file-path` in the source.

**Only VALUES cross, never text.** The C side receives a marshalled `long long`, `const char*` or
`int`; the axiom itself is fixed where it is written and cannot be assembled from strings. That is
the same rule `Run "grep" with arguments ("-v", "3")` follows, and the same reason there is no
injection there.

| Cufet | reaches C as |
| --- | --- |
| `number` | `long long` — **range-checked**, never truncated; a fractional or oversized value raises |
| `text` | `const char*`, UTF-8, valid for the length of the call |
| `fact` | `int`, 1 or 0 |

**A `cast` of an axiom composes anywhere an ordinary call does** — in a condition, an
interpolation, inside arithmetic, or as an argument — because what it gives back was settled
where the axiom was declared rather than where it is used.

⚠ **A declared parameter the source never mentions is refused**, because only declared names are
substituted — a misspelled `the paht` stays in the C verbatim and would otherwise surface as a gcc
complaint about a stray `the`.

⚠ **Known edge:** `the file-path` inside a foreign *string literal* is substituted too —
`[printf("the file-path is %s", the file-path)]` has one hole and one piece of prose. Every
candidate marker shared this, so it separated none of them, but it is real.

⚠ **A reserved word cannot be a parameter name.** `path` is reserved (`the path <p> exists`), so
`given (the text path)` does not parse — `file-path` does. `where` is another, and both are words a
C binding reaches for naturally; check the reserved list when a parameter name will not parse.

**An axiom binding is permanent whether or not it says so.** The text is fixed at the declaration
and there is no other value it could take — so a function body sees it, by the same carve-out that
lets a body see a `permanently` constant.

| Written | Means |
| --- | --- |
| `Define c-language x as [ … ].` | declare an axiom (`Define a c-language axiom x as [ … ].` is the same) |
| `Bind number to f, x.` | run it, and give back the whole number it produced |
| `Cast x on (a, b).` | run it as a STATEMENT, for its effect — the answer is discarded |
| `… as [ … ], and free it with <name>.` | name the axiom that releases the address this one gives back |
| `the c-language number axiom <name>` | an axiom AS A TYPE — a parameter, a field, an element |
| `the c-language number axiom given (the text) <name>` | the same, saying what running it takes |
| `the text at <address>` | read through a foreign pointer — `voidable text`, always COPIED |
| `Pull a book on the c-language.` | admit C axioms in this block |

★ **A call in statement position discards the answer and checks the same.** The language must be
pulled, the arguments must fit, and the declaration must still say what it gives back — the C
wrapper's return type is built from that whether anyone reads it or not.

**What crosses BACK from foreign source:**

| Declared | C gives | |
| --- | --- | --- |
| `number` | any C whole number, signed or unsigned | exact — a decimal holds every 64-bit integer either way |
| `fact` | anything with a truth value | 1 or 0 |
| `voidable number` | a `float`, `double` or `long double` | converted once, in shared C; NaN, an infinity, or a magnitude no decimal holds becomes void |
| `voidable address` | a pointer of any kind | held opaquely, never read through; NULL becomes void |
| `voidable text` | a `char*` or `const char*` | **copied**; NULL becomes void |

### ★ An axiom as a value

An axiom **that says what it gives back** may be passed, stored and run wherever it lands. The type
is written as the declaration reads, and `given (…)` says what running it takes — the same spelling
and the same parser as a function type's, so the two cannot drift apart:

```cufet-fragment
Bind number to measure, given (the c-language number axiom given (the text) job, the text what):
    Return cast job on (what).
Done.
```

★ **Saying the result is what makes an axiom writable as a type.** `the c-language axiom job` is
refused wherever a type is written: the C wrapper's return type is built from the declared result,
so an axiom with none has no signature to be. The refusal names the fix.

★ **`and free it with` is registered at the ACQUISITION**, not at the binding that catches the
result — so it fires once per call, whether the axiom was reached by name or through a value, and
whether or not anybody named what came back.

⚠ **An axiom and a function stay different types**, even though a value of either is the same pair
of pointers underneath. An axiom has no body to read and its language is part of what it is, so
neither satisfies a parameter declared as the other.

★ **Identity is the SOURCE, not the name or the route.** Two names for one axiom, and a call by
name beside a call through a value, all reach one wrapper — one copy of the foreign text, guarded
once.

An axiom prints as `<axiom>`, as a function prints as `<function>`. ⚠ The compiler refuses `State`
on either; `cufet check --native` reports it as a warning.

⚠ **A parameter, a field, or an element is the only place the `given (…)` type spelling belongs.**
A DECLARATION states its parameters after the name — `Define c-language number length-of, given
(the text subject), as [ … ].` — and writing them twice is not a thing to do.

⚠ **A `text` result must be declared `voidable text`.** C says nothing is there by handing back
nothing — `getenv` on an unset name, `strerror` on a code it does not know — so NULL lands in the
mechanism the language already has. A plain `text` is refused as a promise the C side cannot keep.

⚠ **A text is COPIED out of C's memory, never aliased.** The bytes belong to C: `strerror` hands
back a buffer the next call overwrites, and anything malloc'd dies when its owner says so. A Cufet
text pointing at either would change under the program.

⚠ **A floating result must be declared `voidable number`, and a whole one `number`** — the two
guards are disjoint, so naming the wrong one is refused by the C compiler rather than converted.
`number` is exact; `voidable number` is the one conversion that is not, which makes "which did you
mean" a real question.

★ **The base-2 to base-10 conversion is written ONCE**, in C both backends compile, and it hands
back the three numbers a decimal is made of rather than a `double` — so neither backend converts
anything and the last digit cannot disagree. 17 significant digits, which is what a `double`
round-trips in.

Everything else is refused rather than approximated:

- an axiom as a parameter, a field, an element type, or something a function hands back unrun

That is refused by the checker; the result-type questions above are refused by the **C compiler**,
at the point where the type is actually known. Both name what is wrong.

⚠ **An `address` may only be held inside a rabbit block, and cannot outlive one**, which is what
makes that block the unsafe marker without a new keyword: a pointer is a rabbit responsibility,
because the arena that knows when the region dies is what knows when the pointer dies. `Define
handle as cast open-dir on (…)` outside one is a static error, and so is storing one into anything
declared outside — an address obeys the same escape rule as a series or a map.

★ **`the text at <address>` is the ONLY read there is**, and it always copies into the arena —
`voidable text` that belongs to the rabbit, never a view into foreign memory. Reading through a
void address is void. Reading a struct or a scalar is not offered and is not missing: an axiom can
project a field or declare a local and hand it back, so those come home as ordinary results. ⚠
Neither `text` nor `at` is reserved — the PAIR is what makes the phrase unmistakable.

★ **`and free it with <name>`** names the axiom that frees the address, and the release then runs
on every way out of the block, exception included. The releasing axiom takes one `address`. A void
result is never freed, and an axiom with no clause is never freed — a leak is recoverable where a
double free is not. ⚠ Nothing checks the named function is the RIGHT one; Cufet never reads the
foreign text. The clause costs no reserved word: `it`, `with` and `and` are already tokens, and
`free` is recognised only after `, and`, where nothing else can appear.

★ **There is one kind of address**, so `char*` and `FILE*` are the same type — what differs is not
the value but what the writer does with it. There is **no address-of operator**: an address only
ever comes from C and goes back to C, so Cufet never creates one and never reads through one. An
address prints as `<address>`, never as its value.

★ **`size_t` needs no cast**, and a large one is not turned negative. `strlen`, `sizeof`, `fread`
and the rest of libc's length-reporting family report an unsigned 64-bit value, and the boundary
carries that value's signedness along with its bits — so `[strlen(the subject)]` is written as it
would be in C, and an `unsigned long long` of 2^64−1 arrives as 18446744073709551615.

**An axiom is given a fixed set of headers and writes no `#include`.** The C standard library,
plus POSIX on Unix and Win32/winsock on Windows. The set is **platform-guarded** — `<termios.h>`,
`<poll.h>`, `<sys/socket.h>` and `<sys/wait.h>` are Unix-only; `<windows.h>` and `<winsock2.h>` are
Windows-only — and nothing is smoothed over, so a POSIX-only program is refused by the C compiler
on Windows rather than mis-running.

⚠ **A library of your own cannot be reached yet**, and the obstacle is linking rather than headers:
a header declares, and something must still pass `-lsqlite3`. The two have to arrive together.

⚠ **Foreign state is per-process, and the backends are two different processes** — a compiled
program is its own, while the interpreter calls C inside the process running the interpreter. What
C remembers globally (winsock initialisation, `errno`, a library's one-time setup) can differ
between running and building. Cufet values cross identically; C's own memory is C's business.

⚠ **An axiom needs a C toolchain to run, on either backend.** Compiled, its text is pasted into the
program's own C. Interpreted, it is compiled into a small shared library and called — cached by
content, so `gcc` runs once per distinct axiom per machine. Where no toolchain exists at all (the
playground runs the interpreter in wasm) the program refuses to run, and says so.

★ **Every axiom a program can run is built before the program starts**, on both backends — so a
program with one axiom that will not compile produces **no output at all**, whichever way it is
run. Running it and building it give the same answer, which is the point. An axiom that is
*declared* and never returned is built by neither.

⚠ **gcc's complaint about an axiom is the author's to fix**, not a compiler bug — the one exception
to "every line gcc reads was written by cufet", and it is reported as such.

⚠ **Brackets are counted, and nothing else is read.** Pairs nest, so `[argv[0]]` works; a lone `]`
inside a foreign string literal closes the axiom early. `<<...>>` has the same edge, and closing it
would mean knowing which foreign language this is. In practice what follows an early close is read
as Cufet and refuses.

### ★★ Cufet source — a `cufet` axiom, placed by `Cite`

**A `cufet` axiom is Cufet source held under a name.** Same surface as a foreign one, different
mechanism behind it: `[ … ]` still says *the text inside is not the program around it*, and for a
`cufet` tag that stays true — it is parsed, but nothing happens to it until you say so.

★★ **Which of the two kinds it is comes from the rule the `c-language` tag already follows:
says what it gives back ⇒ something you RUN; says nothing ⇒ SOURCE.** One rule, read off the
declaration, for both tags.

```cufet
Pull a book on cufet.
    // Says it gives back a number ⇒ something you run. Called like any other.
    Define cufet number sum-to, given (the number top), as [
        Define the total as 0.
        For each step in range 1 to the top, repeat:
            The total becomes the total + step.
        Done.
        Return the total.
    ].
    State cast sum-to on (10).      ← 55
Done.
```

**A runnable axiom's body is a BODY**, so it holds whatever a function body holds — a loop, a
condition, its own locals. C reaches the same capability through a statement-expression, which is
C's way of putting statements where an expression goes:

```cufet-fragment
Define c-language number sum-to, given (the number top),
    as [({ int s = 0; for (int i = 1; i <= (int)the top; i++) s += i; s; })].
```

**A runnable cufet axiom has no crossing restriction, and none is missing.** A `c-language` axiom
gives back a number, a fact or a voidable text because those are what survive the boundary; nothing
crosses a boundary here, so any Cufet type comes back — `Define cufet series of number …` is fine.

**It is a value, like any function.** Bind it to another name, pass it, store it, run it there.

```cufet
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
    State cast length-squared on (the arrow).       ← 25
Done.
```

**The book is `cufet`, and it is pulled like any other language book** — `Pull a book on cufet.`,
with no article, because `cufet` is a name where `the c-language` is a common noun.

**A block holds DECLARATIONS** — an object, an interface, a `Define` and a `Bind`. The difference between
them is the whole point of `Cite`: a TYPE belongs to the program wherever it is written, so a cited
object is program-scope however deeply the `Cite` sits, while a VALUE lands as a local at the site
that cited it. One block cited twice therefore makes two independent locals:

```cufet
Pull a book on cufet.
    Define cufet counters as [
        Define the tally as 0.
    ].

    Bind number to first:
        Cite counters.
        The tally becomes the tally + 5.
        Return the tally.       ← 5
    Done.

    Bind number to second:
        Cite counters.
        Return the tally.       ← 0, its own
    Done.
Done.
```

⚠ **A block may only reach for names that belong to the PROGRAM** — a function, a type, a
`permanently` constant, a pulled book — plus whatever it declares itself. Anything else is refused
at the block's own line. That is what makes capture impossible by construction: a name the block did
not declare would otherwise mean whatever the site that cited it happened to have, and one block
cited twice would be two different programs.

★★ **A block that holds a FUNCTION may only be cited where functions belong** — at the top level,
or directly inside a `Pull` block. That is Q1 for a body, and it is a placement rule rather than a
second check: placed where functions live, a `Bind` is a free function, and a free function already
cannot read the data around it. Placed inside another body it would be a closure over the body
citing it, which is the capture Q1 exists to prevent.

Everything else a block holds goes anywhere. A type belongs to the program wherever it is written; a
value is *meant* to land at the cite site. A function is the one thing whose meaning would change
with the company it keeps.

**Where a cited declaration lands falls out of [Where a declaration belongs](#-where-a-declaration-belongs)**,
and nothing about `Cite` adds to it. The block's statements are spliced in at the cite site, so a
cited object belongs to the program however deeply the `Cite` sits — usable after the function that
cited it, on both backends.

**Declaring a block places nothing.** A block that is never cited declares nothing at all; if it
did, `Cite` would have no work to do. The name holds source, not a value, so it cannot be stated,
passed, or read.

⚠ **A block may be cited before it is declared**, the way a declaration is available before its
line everywhere else here.

⚠ **One name holds one block.** A second under the same name is refused rather than shadowed — the
one redeclaration in this language that is. Every other kind holds a value or a type and has an
answer already (`Define a shadow`, or last-wins); a name holding source waiting to be placed would
leave every `Cite` of it ambiguous at a glance.

⚠ **Source takes no parameters**, the same as a resultless `c-language` axiom: a parameter's value
comes from a call, and nothing calls source — it is placed. `Define cufet shape, given (…), as [ … ]`
is refused at the declaration.

⚠ **A runnable axiom cannot be cited**, and citing one says so rather than claiming the name is
undeclared. Call it instead.

⚠ **`and free it with` is refused on a cufet axiom — the one thing the two tags differ on.** A
release clause hands memory back to the language that allocated it, and cufet source allocates
nothing across a boundary; what it produces is an ordinary Cufet value.

⚠ **A message from inside a block reports where the block actually sits**, not where it would sit if
it were a file of its own. A lexer starts at line 1 and a block does not, so the fragment's position
is carried into every token and every refusal it produces.

⚠ **An interface DEFAULT written outside a block does not yet find a cited interface.** Defaults are
expanded by the parser, which runs before anything is cited. A conformer writing its own method
works; `Bind <type> to <name> unto <cited-interface>` does not.

---

## 7. Streaming pipes

The `|` operator connects Cufet functions (or OS subprocesses) into a data pipeline.
Stages are ordinary zero-parameter functions; `|` wires their implicit input/output streams.

### Two branches

| Branch | Left operand | Right operand | Runtime |
|---|---|---|---|
| **Task pipe** | Any function expression | Any function expression | Cufet channels + sequential buffering |
| **Subprocess pipe** | `run "prog" ...` | `run "prog" ...` | OS process stdio chaining |

A pipe statement is detected at statement level (`ParseStatement`) and never appears
in expression position — a `PipeExpression` in expression context is a static type error.

### Task pipe

```cufet
Bind void to producer:
  Output 1.
  Output 2.
  Output 3.
Done.

Bind void to consumer:
  For each item from the input:
    State item.
  Done.
Done.

producer | consumer.
```

Multi-stage (any number of stages):

```cufet-fragment
producer | doubler | consumer.
```

### Surface syntax

**`output <value>.`** — emits a value to the implicit output stream. Valid only inside a
function that is used as a pipe producer or middle stage. Using `output` outside a pipe
context is a runtime error.

**`for each <name> from the input:`** — consumer loop. Reads one value per iteration from
the implicit input channel. Terminates automatically when the producer has finished and the
channel is closed.

- `<name>` is the iterator variable — any identifier, or the keyword `item`.
- `Stop.` inside the body exits the loop early (as with any for-each).
- `Skip.` skips to the next value.

### Contextual recognition — `output` and `input`

Neither `output` nor the word `input` in `from the input` is a globally reserved keyword.

**`output`** is contextually recognized as a pipe-output statement when it appears as the
first word of a statement and is followed by an expression — specifically, when the next
token is NOT `becomes`, `'s`, `=`, or `|`. This means:

```cufet-fragment
Define output as 42.      ← variable declaration — works
Output becomes 99.        ← reassignment — works
Output | consumer.        ← left side of a pipe — 'output' is the variable, not a keyword
Output 7.                 ← PIPE OUTPUT STATEMENT (only valid inside a pipe stage)
Output 7.                 ← the same statement, capitalised
```

**`Output` may be capitalised**, so this statement is written like every other one. The capital
is meaningful only in this position: `Output` is not a second spelling of a variable named
`output`, because an uppercase-initial identifier is not legal anywhere.

**`input`** in `for each <name> from the input:` is matched by lexeme (`"input"`) at the
parse site, not by a reserved `TokenType`. Outside that exact syntactic position the word
`input` refers to the built-in stdin readable stream (`the input`), which is always in
scope. You cannot redefine `input` (it is pre-defined by the type checker as the stdin
stream), but `output` is freely reusable as a variable name.

### `item` as iterator name

`item` is a reserved keyword (`TokenType.Item`) normally used for positional series
access (`item 2 of my-list`) and matrix access (`item at (r, c) of m`).
It is also accepted as the iterator name in for-each loops — including the consumer
form. Inside a loop body, bare `item` (not followed by a number or `at`) is
treated as a variable reference to the iterator binding.

```
For each item from the input:
  State item.          ← 'item' is the bound iterator, not 'item N of ...'
Done.
```

### Subprocess pipe

```cufet-fragment
run "echo" with arguments ("hello") | run "cat".
```

All stages must be `run` expressions. The `|` operator chains stdout → stdin between
adjacent processes. The final process's stdout is written to the program's output.

Failures (missing program, permission denied) surface as Cufet failures with the same
categories as standalone `run` expressions. The must-handle requirement is **waived**
for `run` expressions inside a pipe — the pipe itself is considered an implicit handler.

### An argument list that is not written out

`with arguments` takes either a parenthesised list of expressions or a single expression of type
`series of text`. **One token decides which:** a `(` immediately after `arguments` opens the
written-out list, and anything else is read as the whole list.

```cufet-fragment
Try to:
    Define argv as a series of text with ("-l", "/tmp").
    Run "ls" with arguments argv.          ← the series form
    Run "ls" with arguments ("-l", "/tmp").  ← the written-out form
Done.
In case of failure:
    State "no ls".
Done.
```

⚠ **`with arguments (argv)` is therefore the written-out form with one element in it**, not the
series form. The checker refuses it by name rather than letting the element type decide, because a
reader has to be able to tell the two apart without knowing what `argv` holds. The same rule is why
the parser may commit on a single token.

Both `run` forms accept both spellings, in a pipe stage as well as standalone.

`Run X.` at statement level (without `|`) **launches the program with this terminal** instead of
capturing it. It is a statement, so it hands back nothing — no result record, and so no exit code
to read. It can still fail to launch, so it still must be handled: put it in a `Try to:` block.
The expression form is unchanged.

```cufet-fragment
Try to:
    Run "git" with arguments ("log", "--oneline", "-5").   ← streams; nothing comes back
    Define r as run "git" with arguments ("rev-parse", "HEAD").
    State the output of r trimmed.                          ← captured; nothing is shown
Done.
In case of failure:
    State "git not available".
Done.
```

★ The difference is **who gets the terminal**, not what happens to the text. An expression `run`
hands the child a pipe, which is why nothing appears until it exits and why a program that draws —
`less`, `vim`, `top` — cannot start. Capturing the result and discarding it would have bought
neither.

### Execution model, and what is still restricted

**Buffered when interpreted, truly streaming when compiled.** Interpreted, the producer
stage runs to completion first, filling an in-memory buffer, and the consumer then drains
it. Compiled, every stage runs as its own thread joined by channels, so values stream
through as they are produced. A linear pipe's *observable output order* is the same either
way — values cross each channel FIFO — so this is a throughput and memory difference, not
a semantic one.

**Mixing subprocess stages with function stages** (`run X | consumer-function`) is not
supported. The shared front end rejects it identically on both backends, with a message
naming the `run` result record as the offending stage. A pipe is either all `run` stages
(a subprocess pipeline) or all function stages (a task pipeline).

**Cross-stage element types are checked.** A stage's input type is written down nowhere —
`for each n from the input:` declares no type — so it can only come from the stage
upstream. The type checker walks each pipe left to right, carries every stage's output
element type into the next as its input, and type-checks that stage's body against it.
A producer emitting `number` into a stage that does `the length of n` is a normal type
error, at the offending line, on both backends.

Two consequences worth knowing:

- **A consumer's body is checked at the pipe, not where it is written.** A `Bind` whose
  body reads `from the input` is only fully checked once a pipe says what flows into it.
  A stage function that is never used in any pipe has an unchecked `for each … from the
  input` body.
- **A stage function may be used at only one input element type** across the whole
  program. Feeding the same function `number` in one pipe and `text` in another is a
  clean type error naming both.

Stages reached indirectly — a lambda, or a function value held in a variable — stop the
chain rather than erroring: their bodies were checked where they were written, and there
is no declared element type to propagate, so downstream stages go unchecked instead of
producing a false positive.

---

## 8. Sharp edges

### ★ A function or method sees other functions and CONSTANTS — not top-level data

```cufet-refused
Define max-retries as 3 permanently.
Define counter as 3.

Bind number to budget:
    Return max-retries * 2.     ← fine: permanently, so it cannot be mutated
    Return counter * 2.         ← refused: ordinary top-level data
Done.
```

The rule keeps data flow explicit and prevents hidden global mutation. A `permanently` binding
cannot be mutated, so it is exempt — the restriction was previously broader than the reason for it.

★★ **"Top level" here means anywhere hoisting is transparent to — a `Pull` block included.** A
constant declared inside `Pull a rabbit.` or `Pull a book on …` is a shared constant exactly as one
written at the top of the file is, and a function declared in the same block is a free function for
the same reason. That matters because a rabbit block is where most programs put both:

```cufet
Pull a rabbit.
    Define the limit as 10 permanently.
    Bind number to bumped:
        Return the limit + 1.       ← fine: a shared constant, wherever the block is
    Done.
Done.
```

⚠ **A `permanently` inside a FUNCTION body is shared with nothing.** Hoisting does not enter one,
which is the same reason a nested `Bind` is a closure rather than a free function.

**It applies to every body that leaves the top-level scope**, in exactly the same terms: a
top-level function, a **method**, a **getter**, a **setter**, a **destructor**, an **operator
overload**, a **pipe stage**. There is one rule, not one per body kind.

A **lambda literal is not in that list** — it *captures* its enclosing scope, so a lambda sitting
beside a binding closes over it normally:

```cufet
Pull a rabbit.
    Define nums as a series of number with (1, 2, 3).
    Define f as a function: Return the number of nums. Done.   ← captures nums
Done.
```

The refusal is enforced by the **TypeChecker**, so both backends agree and `cufet check` catches
it. It used to be raised only by the interpreter at run time, which meant `check` reported no
problems, running the program refused, and compiling it emitted undeclared C and blamed the
compiler.

A name that was never defined at all is a different case: it is still reported when the program
runs, not by `check`.

### ★ A `permanently` field is refused down EVERY write route, not just the obvious one

```cufet
Define object user with (the text id permanently, the text name).
```

The adverb **trails the field name** — `the text id permanently` — which is the same position
`Define max-retries as 3 permanently.` already uses. One rule: `permanently` follows the thing it
fixes. It also only reads as English there, because the verb it modifies is the enclosing `Define`.
It is per-FIELD: neighbours in the same object stay writable.

Construction is not a write, so the `a new user { … }` literal sets it normally. Everything after
is refused, and the refusal is deliberately checked in front of the setter branch:

| Route | |
|---|---|
| `alice's id becomes …` | refused |
| `one's id becomes …` inside its own method | refused |
| a promoted field written through an embed | refused |
| a `Set id given (…)` setter | refused — a setter cannot stand in for this |

The setter case is the one worth stating out loud: setters are **infallible and transform-only**,
so one guarding a permanent field could only ignore a bad write, never reject it. Letting the write
route through a setter would leave `permanently` meaning nothing to anyone who declared one.

### ★ `when` binds loosest, and `, otherwise` is mandatory

`<value> when <condition>, otherwise <value>` is an expression, and it binds **looser than
everything else** — including the `but void is` and `but on failure` suffixes:

```
Define parsed as raw but void is 0 when shout is true, otherwise 99.
                 └──────────────┘ one arm      └──┘ the other
```

The `, otherwise` half is **required**. That is not decoration — it is what makes the comma
unambiguous inside an argument or element list, with no lookahead:

```
Define sizes as a series of text with ("small" when n is 1, otherwise "big", "fixed").
                                       └──── element 1 ─────────────────┘  └ el 2 ┘
```

A half-written `f(x when c, y)` is therefore **not** two arguments; it is an unfinished
conditional and is refused as one. Writing a conditional inside a list is legal and reads badly;
naming the value first is the recommended style, but the language does not force it.

Chaining is **right-associative**, so a ladder falls through rather than nesting on the left:

```
"one" when count is 1, otherwise "two" when count is 2, otherwise "many"
```

**Exactly one arm evaluates**, on both backends — the compiler emits a C ternary and the
interpreter evaluates the chosen side only. A call, a failure or a `State` in the untaken arm
does not happen.

**The arms may differ in type**, and the result is their union — the same inference
`a catalogue with (1, "two")` already performs. For strictness, annotate: `Define the number fee
as 0 when member is true, otherwise 25.` makes a mismatched arm an error at the definition.

The condition must be `true` or `false`; there is no truthiness.

### ★ A type may precede the name in `Define`, and a NAME is what tells the two forms apart

Both of these are declarations, and the difference is whether a name follows the type:

```cufet-fragment
Define the text greeting as "hello".      ← declares greeting as text
Define copy as src.                       ← copies src's value; src is not a type
```

The parser reads what follows `as` as a type first. In the second line that attempt swallows
`src` as if it were a type name, then finds `as` where a variable name should be, so the whole
attempt is rolled back and the line means what it always meant. **The rule to remember: a type
before the name only counts when a name comes after it.**

**This is the only way to write a union-typed variable.** Without a declared type, a binding's
type is whatever its first value was, and `42` is a number, not `(number or text)`:

```cufet-fragment
Define the (number or text) x as 42.      ← x holds either; `x becomes "hi"` is legal
Define x as 42.                           ← x holds numbers, and only numbers
```

The value **widens** into the declared type — the same single implicit coercion `becomes` and
`return` perform — so the value only has to *fit*, not match exactly. A value that does not fit
is an error at the declaration.

### String interpolation — `{...}` inside a text literal

Part of the text literal itself, so it is legal **anywhere a text expression is** — a `Define`,
an argument, a file path — not only in `State`.

| Rule | |
|---|---|
| A hole holds any **expression** | `"{count * 2}"`, `"{the length of s}"`, `"{item 2 of parts}"`, `"{here's x}"` |
| Nesting is allowed | `"outer {"inner {who}"}"` |
| The value is **converted to text** for you | `"{count}"` needs no `converted to text` |
| ★ So a hole takes only what `converted to text` takes | text, number, fact, bits — **not** a series, record, object or map |
| A literal brace is escaped | `\{` and `\}` |
| An empty hole is a **parse error** | `"{}"` — always a mistake, never an empty string |

★ **The hole is narrower than `State`.** `State parts.` prints a series as `(a, b)`, but
`State "the parts: {parts}".` is a static type error — the conversion a hole performs has no
answer for a container. Print it on its own line, or build the text explicitly.

### Verbatim text — `<<...>>` cannot spell text ending in `>`

`<<a>>>` closes at the **first** `>>` and leaves a stray `>` behind, which is then a comparison
operator and almost certainly a parse error. Write that one as `"a>"`. Every verbatim syntax has
such a corner; this is Cufet's, and it is the only one.

Otherwise `<` and `>` are untouched: `a < b` and `a <= b` lex exactly as before. `<<` is claimed
only because two comparisons in a row is not an expression Cufet has, so nothing valid was taken
away.

### ★ Transformations TRAIL, accessors LEAD

The single most useful rule for guessing right the first time, because English will
often supply the wrong order confidently.

**Transformations follow the thing they act on** (they read as past participles or
trailing phrases):

```
nums sorted            nums sorted by the age        nums in reverse
s trimmed              s in uppercase                s split by ","
score converted to text                              first joined to last
```

**Accessors lead, as noun phrases** (`the … of …`):

```
the length of s        the number of s               the first of s
the last of s          the position of x in s        the size of m
```

So it is `nums sorted`, never `sorted nums`. If you are reaching for a
transformation, it goes *after*.

### Binary `-` needs spaces, because hyphens are identifier characters

`grand-total` and `start-seed` are single names, so `x-y` is one identifier, not
subtraction. Write `x - y` with spaces. Digits cannot start an identifier, so `1-1`
is unambiguous and works either way — the rule only bites between names.

**The same spacing settles `the <name> -…` inside a record literal**, where `the` could
begin either a named field or an expression:

```cufet-fragment
a record with (the offset -1)      ← a named field 'offset' holding negative one
a record with (the row - 1, 9)     ← the subtraction, a positional field
```

Every other operator is unambiguous there, because none of them can BEGIN a value:
meeting one straight after the name means what came before it was an expression.
`-` is the exception, and spacing is what tells the two apart — the same rule, in a
second place.

### Functions and methods see other functions, but not top-level data

`Bind void to f:` at the top level creates a **global procedure**, not a closure.
When called, it runs in an isolated environment: other top-level functions are
visible (enabling mutual recursion) and top-level `permanently` constants are
visible, but ordinary top-level `Define`d values are not. Methods, getters,
setters, destructors and operator overloads run in the same isolation.

```
Define total as 0.

Bind void to show:
    State total.          ← CHECK ERROR: 'total' is a top-level value,
Done.                        not visible inside a function or method.
Cast show.
```

The error teaches the fix rather than saying "total isn't defined":

```
'total' is a top-level value, but function and method bodies can't see top-level data.
They see other functions (for mutual recursion) and top-level `permanently` constants,
but not top-level data that can change.
Fix: declare it a shared constant if it never changes:
    Define total as <value> permanently.
Or pass 'total' as a parameter, or define your function inside a scope where
'total' is already bound so it captures 'total' as a closure.
```

**Fix 1 — `permanently`, when the value never changes** (see the shared-constants
rule above): the binding becomes a constant and every function and method may read it.

```cufet
Define total as 42 permanently.

Bind void to show:
    State total.          ← OK: a shared constant
Done.
Cast show.
```

**Fix 2 — pass as a parameter** (preferred for pure helpers, and the only option
when the value changes):

```cufet
Define total as 42.

Bind void to show, given (the number total):
    State total.          ← OK: 'total' is a parameter
Done.
Cast show on (total).
```

**Fix 3 — nested closure** (when multiple helpers share the same *mutable* data):

```cufet
Define total as 42.

Bind void to run-report, given (the number t):
    Bind void to show:    ← closure: captures 't' from run-report's scope
        State t.
    Done.
    Cast show.
Done.
Cast run-report on (total).
```

**Why this design**: it's the same principle behind Cufet's message-passing
concurrency — explicit data flow prevents hidden shared-mutable-state bugs. A
function that references a global variable is implicitly coupled to global
execution order; passing data as a parameter makes the dependency visible and
the function independently testable. Nested closures are the right tool when
you need a family of helpers sharing a common context.

**Mutual recursion still works**: top-level functions can call each other freely,
because they see other `Bind`-defined functions (though not mutable `Define`d data).

**A lambda is not affected**: `Define f as a function: … Done.` captures its enclosing
scope, so it reads the locals around it normally. The isolation is about bodies that
*detach* from the top-level scope, not about every body.

⚠ **Top level only, when compiling.** A `Bind` nested inside a rabbit or another function is a
closure, and the native compiler emits it where it stands — so it cannot call a name declared
further down the same block, and two nested functions that call each other cannot both come
first. Interpreted the same program runs, because the interpreter looks the name up when the
call happens; the compiler refuses with a message saying so. Self-recursion nested inside a
block is fine, and so is mutual recursion at the top level. **Declare mutually recursive
functions at the top level** and both backends agree.

### ★ A definition with a blank is not a type

`Define object stack of element with (…)` leaves a blank. The blank is a name **you**
choose, and `of` marks it — the slot after the type's own name, so it is declared by
POSITION and nothing has to be inferred. That is what keeps a mistyped type name an
error instead of quietly becoming a blank.

- **`a stack` alone is refused.** Only `a stack of number` names a type. The refusal
  says the blank needs filling and shows the shape, rather than claiming `stack` is
  undefined — it is defined, it just names nothing on its own.
- **Each filling is its own type.** `stack of number` and `stack of text` share no
  values and no methods; filling happens by copying the definition, not by boxing.
- **More than one blank works** — `object pair of left-thing of right-thing`, written
  `a pair of number of text`. Naming them is what makes that possible.
- ⚠ **A template's body is not checked until it is filled.** `element` is a blank, not
  a type, so there is nothing to check it against. A template nothing fills is never
  checked at all — a mistake inside one surfaces at the first use, not at its
  definition.
- **Neither backend ever sees a template.** A filling becomes an ordinary definition
  named for it (`stack of number`) and is spliced into the program; the template is
  dropped. Same rule that lets no `stash of T` survive the front end. The name has
  spaces in it on purpose — a writer cannot type one, so it cannot collide.

### ★ A function's blanks come from its SIGNATURE, used twice

A function has no slot to declare a blank in the way `object stack of element` does,
so its signature introduces them: a type name that names nothing, appearing at
least **twice** across the parameters and the return type.

- ⚠ **Twice is the guard, and it is applied to the SIGNATURE only.** Used once, a
  name is a spelling mistake and stays an unknown type — `given (the nubmer n)` is
  an error, not a generic function. A body is never read for this: it is checked
  per filling, not once.
- **The filling is read off the arguments**, so a blank has to appear in a
  parameter. One living only in the return type is refused — nothing says what it is.
- **A blank means one type per call.** `cast pick on (1, "two")` against
  `given (the element left, the element right)` is refused by name.
- **Matching reaches inside**, so `voidable element` and `series of element` both
  work — that is `minimum`'s shape and `unique`'s.
- **Each filling is emitted separately**, named for it (`first-two of number`), and
  the template is dropped before either backend runs.

### ★ An interface is a PARAMETER type and nothing else

An interface may be the declared type of a function parameter. It may **not** be the element type
of a series or catalogue, a field type, a return type, or a variable's type, and an
interface-typed parameter may not be reassigned or forwarded to another interface-taking function.

That is not an omission — it is what makes interfaces free. The argument at every call site is a
**concrete** conformer, so each one gets its own specialised copy of the function and method calls
stay direct. **No vtables, no type tags, no runtime dispatch.** Polymorphism that could be stored
would need a representation that travels with the value, which is the cost this design declines.

**A default method (`Bind <type> to <name> unto <interface>`) does not change any of this.** The
body is expanded into one ordinary method per conforming type before the type checker runs, so a
default has a concrete receiver every time it is called and specialises per conformer exactly like
a hand-written method. `one` inside a default is the *conformer*, never the interface — there is no
value of interface type for it to be. A type's own method beats the default; two interfaces
supplying the same defaulted name to one type is refused.

**Hold a mixed group as a closed union and narrow it back.** The union says which types exist; the
interface says what they must be able to do:

```
Define crew as a catalogue of (hopper or thunderbird or beaver) with (…).
For each who in crew, repeat:
    Judge who, where it is:
        A hopper,      State cast report-crossing on (it, distance).
        A thunderbird, State cast report-crossing on (it, distance).
        A beaver,      State cast report-crossing on (it, distance).
    Done.
Done.
```

The arms look repetitive and are not: each compiles to a different specialisation.

⚠ **The mistake this actually produces.** Writing `a catalogue with (…)` and letting the element
type be inferred from mixed values gives an **open union**, and passing that where an interface is
required is refused — correctly, since an open union has no fixed set of conformers to specialise
for. The refusal names a type you never wrote, which is the confusing part: you get told you
passed "an open union" without having typed the word `union` anywhere. Name the element type.

### ★ A book in another file, and where its errors point

`Pull a book on ‹name›.` resolves in three steps: a bundled book, then a module defined in this
file, then `‹name›.cufe` beside the file being run. A missing file is not an error of its own —
the name falls through to the refusal `Pull` already had, which names what is available.

**Loaded in a front-end pass, ahead of `Cite` and the hoist.** After it the loaded file does not
exist: its statements are the program’s, and the checker and both backends meet one longer
program. Rings are refused by name; a book pulled by two others is loaded once.

⚠ **Positions.** A loaded file is lexed at an OFFSET into a virtual line space, because tokens,
AST nodes and exceptions carry a line and a column and no FILE — giving them one would touch every
position in the front end. The reporter maps a virtual line back to its file and line.

★★ **A loaded file’s top level is private to it**, and by RENAMING rather than by a new scope:
everything the file declares beside a `module`-conforming object is renamed to something with a
space in it, which no identifier can contain. The file’s own references are renamed with it, so
its module still reaches its helpers; the host cannot name what it cannot spell.

⚠ The rewrite rides on `AstSearch`, which walks every property of every node by reflection. A
hand-written walk that forgot a node kind would leave a reference pointing at a name that no
longer exists — loud at check time, which is the right direction, but the reflection walk means
it cannot happen. Types are substituted separately, because that walk deliberately does not
descend into a `CufetType`.

★★ **The line appears twice in an error, and both have to agree.** Once in the reporter’s header,
and once in the prose — 163 places in the front end write a line number into a message. So the
resolution is applied to the COMPOSED message, not at each of them, and only rewrites a number
that falls inside a block actually allocated to a loaded file. A single-file program allocates
none, so nothing it prints can change.

### ★ Several functions may share a name when one argument tells them apart

Two `Bind`s of one name are **versions** when exactly one parameter's type differs between them.
The call picks the version by that argument's type — statically when the type is known, and from
the tag the value carries when the argument is a closed union.

**Expanded in the front end, before the hoist.** Each version becomes an ordinary function under an
unwritable name (`eval given num-node` — spaces, the same trick monomorphization uses), and the
name the writer called becomes an ordinary function whose body is a `Judge` over the union of the
versions' types. Neither backend learns dispatch exists.

⚠ **The dispatcher's arms are generated from the versions**, so coverage cannot fall out of step
with them, and a call passing a wider union than the versions cover is refused by ordinary
assignability — naming exactly which case has no version.

Every version must agree on **arity** and on **what it gives back**. The return type is not an
implementation convenience: a caller has to know what it gets back without knowing which version
ran, and the alternative is handing every caller a union to judge.

**More than one argument may dispatch.** Each one becomes a `Judge`, nested, with the innermost
arm running that combination's versions.

⚠ **Coverage stops being free at two.** With one dispatched argument the versions ARE the cases
and the dispatcher's parameter is their union, so nothing callable is unclaimed. With two, the
parameters admit every PAIR and only the pairs someone wrote have a version — so every combination
of the dispatched types must have one, and the missing one is named at the declaration.

⚠ **Each level binds its narrowed subject to a local before descending.** `Judge` narrows `it`
and nothing else — the subject variable keeps the union — so without the binding the inner `Judge`
rebinds `it` and the outer argument's narrowed type is gone by the time the leaf calls the version
that declared it.

Refused: two versions claiming one combination, and a name whose declarations have nothing at all
to tell them apart.

### ★ `when` on a version: the fragment, and why it is bounded

A version may carry `when <condition>` after its `given (…)`. Versions sharing one signature are
told apart by their conditions; **exactly one per signature must carry none**, and that one is the
fallback.

**Overlap is refused, never resolved.** Two versions whose conditions can both hold is a compile
error naming both lines. There is no priority rule, no declaration-order tiebreak and no
specificity system, because the question "which of these two wins" is never asked — which is the
same answer this language gives every other ambiguity.

**Which makes the fragment the design, not a limitation of it.** Deciding whether two arbitrary
boolean expressions can both hold is undecidable; deciding it for these atoms is a comparison:

| Allowed | Example |
|---|---|
| Equality against a literal | `node's left is 0` |
| Inequality against a literal | `tok's kind is not "eof"` |
| Type test | `node is a num-lit` |
| Negated type test | `node is not a num-lit` |
| `and`, `or`, `xor` over those | `left is 0 xor right is 0` |

⚠ **The atom set is closed under negation, and that is load-bearing.** Overlap being refused means
a writer excludes the narrower case by hand — and excluding a conjunction needs a disjunction, so
without `or` the design would refuse overlap and withhold the only way to write around it.

⚠ `xor` carries no expressive power (the atoms already negate) and is in for consistency: `and`,
`xor` and `or` are one family on one precedence line. It normalises by doubling conjuncts, where
`or` only adds.

**Out:** ordering and arithmetic. Those need interval reasoning rather than atom comparison.

⚠ **The check is sound and deliberately incomplete.** Over a `(red or green)`, `is not a red` and
`is not a green` are disjoint by exhaustion and no atom pair says so, so they are refused. Erring
toward refusal is the safe direction — a refused program is a message, an accepted ambiguous one is
a silent wrong answer.

**A fallback is required rather than inferred**, even when the conditions look complementary:
proving a SET of them covers every case is tautology checking, which the fragment does not promise.
Widening either boundary later is additive.

⚠ **The generated `If` chain is order-dependent; the dispatch is not.** Because no two conditions
can hold at once, the chain answers the same in any order, so the order it happens to be generated
in carries no meaning. The condition is also rewritten onto the narrowed subject before it reaches
the chain — inside the generated `Judge` arm the parameter still holds the whole union.

### ★ A name with nothing to tell its declarations apart

Where no argument type differs, a second `Bind` of the same name is refused, naming both lines.
Before this it was accepted silently and the later declaration won.

Compared on the **hoisted** declarations, which has two consequences worth knowing:

| Shape | Verdict | Why |
|---|---|---|
| Two top-level `Bind`s of one name, same parameter types | Refused | Nothing could pick between them |
| Two top-level `Bind`s of one name, one parameter type differing | Allowed | Versions — see above |
| The same name in two separate `Pull` blocks | Refused | Both bodies hoist into the SAME top-level scope, so a call in the first block reached the second block's body. Versions spanning blocks is a separate question, not yet settled |
| A `Bind` inside a body, shadowing a top-level one | Allowed | Not hoisted. An ordinary local declaration, shadowing the way a local binding does |
| Two types each declaring a `speak` method | Allowed | A method is not a free function — it is reached through its owner |
| A `Bind … unto <type>` sharing a free function's name | Allowed | `unto` declares a method, reached through its owner — `cast rex's speak on ()` and `cast speak on (3)` are different calls |

### ★ Named arguments: what tells them from an expression

`the width 3` is a named argument; `the width of box` is a field access; `the width` on its own is
the variable `width` with `the` as the noise it is everywhere else. All three open with the same
two tokens, and an argument list is an EXPRESSION position, so position alone does not separate
them the way it separates a type.

The parser uses `IsNamedFieldStart` — the predicate object and record literals already use for the
identical ambiguity — plus **one rule an argument list needs and a literal does not**: a named
argument must have a VALUE after the name. A `,` or `)` there means the whole thing was an
expression. `(the width)` inside a record literal has no other reading, but `cast twice on (the
width)` is an ordinary call passing a variable and always was.

The minus rule comes with the predicate: binary `-` is written with spaces, so `(the offset -1)`
passes negative one and `(the offset - 1)` is a subtraction.

**Positional first, then named.** Once a name has been given, position no longer says which
parameter is meant, so a positional argument after a named one is a parse error.

**The names come from the declaration, not the type.** `FunctionType.ParameterNames` is set
wherever a declaration registers its signature and left null wherever a function type was WRITTEN —
`the number function given (the number)` names nothing. It is not part of type equality, so
`given (the number width)` and `given (the number w)` stay the same type. A call whose callee has
no names is refused rather than matched against a nearby declaration.

⚠ The checker reorders into `CastExpression.Args` and empties `NamedArgs`, **before** the generic
machinery, which reads arguments by position to decide which body a call reaches. Emptying is also
what makes the second checking pass over a filled generic safe.

### `Return a failure.` re-propagates; `Return a failure "msg".` originates

The parser checks whether a **string literal** immediately follows `failure`:

- **`Return a failure "message".`** → new `FailureLiteral` — creates a fresh
  failure with that message. Valid anywhere a failure can be returned.

- **`Return a failure.`** → `VariableReference("the failure")` — reads the
  variable `the failure`, which is only in scope inside a `In case of failure:`
  handler body. Use this to re-propagate a caught failure unchanged.

Getting these wrong causes type errors or runtime "undefined variable" errors. The
safe rule: always use `Return a failure "message".` when originating; use
`Return a failure.` (or `Return the failure.`) only in handler bodies.

### `size` is reserved — cannot name getters or fields

`size` is `TokenType.Size` (used for `the size of map`). It cannot be used as a
getter name, field name, or variable name. For a computed count property on a
collection type, use `count`, `length`, or `card-count` instead:

```
Get count as number:                      ← OK
    Return the number of one's cards.
Done.

Get size as number:                       ← PARSE ERROR: 'size' is reserved
    ...
Done.
```

### `start` is a reserved keyword

The lexer produces `TokenType.Start` for the word `start`. It cannot be used as a
variable name, field name, or function name. Use `origin`, `src`, `begin`, etc.

### `state` is a reserved keyword

The lexer produces `TokenType.State` for the word `state` (the print-output
statement). It cannot be used as a field name, variable name, or function name.
Use `region`, `status`, `condition`, etc. when the concept of "state" (as a noun)
is needed.

### `=` in statement position is an assignment-mistake error

`=` is **comparison only** in Cufet — assignment is `becomes` (update) or `Define ... as` (introduce).
Writing `x = 5.` as a statement is a **parse-time educational error**:

```cufet-refused
x = 5.
→ Line N: '=' is comparison, not assignment.
  Did you mean 'x becomes 5.' (update) or 'Define x as 5.' (introduce)?
```

`=` still works as comparison in its valid positions:

```cufet-fragment
If x = 5, State "five".        ← OK: comparison in condition
Define b as (x = 5).           ← OK: comparison in expression
```

This design eliminates the C-family `=`-vs-`==` footgun: `=` is unambiguously
equality everywhere it appears. The statement-position error teaches the two
correct assignment forms at the point of the mistake.

### Comparisons after parenthesized sub-expressions

After a parenthesized expression, the parser considers the primary expression
complete and does not continue parsing a word comparison. This means:

```
Return (the number of items) is 0.    ← PARSE ERROR
If (the number of items) is 0:        ← OK: 'is' here is a condition comparison
```

This is a **parser edge case**, not a design split between word and symbol forms.
After a parenthesized sub-expression, the parser considers the primary complete and
does not consume a following word-comparison keyword. Use a symbol form instead:

In expression position, use a symbol comparison or an `If`-return pattern.

### `or pass the failure off` is invalid inside a `Try` block

Inside a `Try to: ... In case of failure:` block, fallible cast results are
**auto-unwrapped** — the type checker strips `FailureType(T)` to `T` because
the Try block IS the failure handler. Using `or pass the failure off` on an
already-unwrapped `T` is a type error:

```
Try to:
    Define x as cast compute on (args).              ← OK: failure auto-caught by Try
    Define x as cast compute on (args) or pass the failure off.  ← TYPE ERROR
Done.
In case of failure:
    ...
Done.
```

### `or pass the failure off` is a postfix expression operator

`expr or pass the failure off` propagates a failure from a fallible expression.
It must appear as **part of an expression** — it is not a statement:

```
Define result as cast compute on (x) or pass the failure off.   ← OK
Cast compute on (x) or pass the failure off.                    ← PARSE ERROR
```

### `but void is` fallback value must be the right type

`voidable-expr but void is default-expr` — the type checker infers the unwrapped
type from the left side. The right side must be assignable to that type. If the
left side's type can't be determined (e.g., map lookup where the map variable is
unknown to the type checker), the whole `Define` will fail with "type can't be
determined". Use a local alias to give the type checker a named, typed binding.

### Operator overloads: same type only, arithmetic operators only

Both operands must be the same object type. The overloadable operators are
`+`, `-`, `*`, `/` only. Comparisons and logical operators cannot be overloaded.

Parameter names cannot be `a`, `an`, or `the` (noise tokens) — use `lhs`/`rhs`
or other identifiers.

### Object literal field order and names

`a new T { the fieldA valA, the fieldB valB }` — fields are supplied by name,
not position, but the type checker validates that all required named fields are
present. Positional fields (unnamed, declared without `the` in the `with (...)`)
are provided positionally in `{ val1, val2 }`.

### `entry` in map iteration

When iterating a map with `For each pair in m`, the iteration variable is a
pseudo-record with fields `key` and `value`:

```cufet-fragment
For each pair in m, repeat:
    Define k as the key of pair.
    Define v as the value of pair.
Done.
```

Do **not** name the iterator `entry` — it is a reserved keyword. `pair`, `kv`,
`item`, or any non-reserved word works.

### Rabbit lifetime invariant: free functions, methods, and getters all carry depth

The downward-only invariant enforces that a reference-typed value can only be
stored into a container whose lifetime is at least as long as the value's. The
return-depth inference system extends this to all call sites: **free functions,
object methods, and getters** that return reference types are tracked — the checker
infers how the return value's lifetime relates to the arguments and the receiver.

**Free function depth laundering is caught:**

```cufet-refused
Bind series of number to smuggle, given (the series of number s):
    Return s.
Done.

Define outer as a series of number.
Pull a rabbit.
    Define inner as a series of number with (1, 2, 3).
    outer becomes Cast smuggle on (inner).   ← TYPE ERROR: inner is shorter-lived
Done.
```

**Method depth laundering is caught:**

```cufet-refused
Define object bag with (the series of number items).
Bind series of number to get-items unto bag:
    Return one's items.
Done.

Define outer as a series of number.
Pull a rabbit.
    Define inner as a series of number with (1, 2, 3).
    Define b as a new bag { the items inner }.
    The outer becomes Cast get-items on (b).   ← TYPE ERROR: b (and its fields) is shorter-lived
Done.
```

**Getter depth laundering is caught:**

```cufet-refused
Define object bag with (the series of number items):
    Get payload as series of number:
        return one's items.
    Done.
Done.

Define outer as a series of number.
Pull a rabbit.
    Define inner as a series of number with (1, 2, 3).
    Define b as a new bag { the items inner }.
    The outer becomes b's payload.   ← TYPE ERROR: b is in a shorter-lived rabbit
Done.
```

**Same-depth calls are still legal** (storing within the same rabbit, or into a
longer-lived container):

```cufet-fragment
Pull a rabbit.
    Define ones as a series of number with (1).
    Define twos as a series of number with (2).
    Define chain as a series of series of number with (ones, twos).   ← OK: all depth 1
    Define first as cast head on (chain).    ← OK if result stored at depth 1 or deeper
Done.
```

**Conservative fallback:** for calls whose exact depth signature can't be
determined (recursive functions, unknown callees), the checker assumes the return
carries the depth of the deepest reference-type input (receiver or argument). This
is sound (over-strict, never under-strict) — it may reject contrived-safe code
but will never permit an unsafe escape.

**Capture-store prohibition:** a nested function (`isNested = true`) that captures
a reference-type **parameter** of its enclosing function cannot store that captured
parameter into any outer state. Parameters are registered at `RabbitDepth = 0` (the
function's own perspective), but callers may pass rabbit-allocated (depth-N) values.
The checker treats captured reference-type parameters as maximally deep so that any
outward store is rejected:

```cufet-refused
Bind void to run-with, given (the series of number s):
    Define sink as a series of number.
    Bind void to tuck, given ():
        The sink becomes s.              ← TYPE ERROR: captured parameter 's' treated as
    Done.                               maximally deep — caller may pass a rabbit value
    Cast tuck on ().
Done.
```

Legal: capturing a reference-type parameter and only **reading** it (no outward
store) is fine. Capturing a **local variable** (not a parameter) is also fine — locals
have a known depth from when they were defined. The workaround for the rare case
where the pattern is genuinely safe: pass the value as an explicit parameter to the
nested function rather than capturing it.

---

## 9. Writing Cufet: the mental model

### Two grammars, one language

Cufet has two syntactic layers that compose but do not mix:

**Expression grammar** — produces values, uses operators (`+`, `-`, `>`, `=`,
`and`, `or`, `not`), terminated by `.` when it forms a statement. This is where
arithmetic, string ops, function calls, field access, and boolean arithmetic live.

**Condition grammar** — produces `fact`, appears after `If`, `While`, and
`until`. Comparison forms are fully unified: both symbol (`<`, `>`, `=`, etc.) and
word (`is less than`, `is greater than`, `is`, etc.) comparisons work in both
expression position and condition position. Word forms are idiomatic in conditions
(they read like sentences); symbol forms are idiomatic in expressions (they read
like math). Both are accepted everywhere.

### Articles are invisible everywhere

`a`, `an`, `the` are consumed before looking for any token. `Define the total as
0.` and `Define total as 0.` are identical. This is purely cosmetic — use
whichever reads more naturally.

### Everything terminates with `.`

Every statement ends with `.`. Multi-statement blocks end with `Done.`. The `.`
is what the parser uses to detect end-of-statement, so forgetting it causes
cascade parse errors.

### Object methods: `one` is the only self-reference

Inside any method (or getter, setter, destructor), `one` is the receiver. Fields
are never in scope directly — always `one's fieldname`. Mutations to series fields
work through a local alias (same reference). Map and scalar field mutations use
possessive-set (`one's field becomes X`) or in-place map operations.

### Empty collection idioms

```cufet
Define xs as a series of number.             ← empty series
Define m  as a map from text to number.      ← empty map
```

Both create empty, typed, mutable collections. Use `Bind making a` constructors
to encapsulate initialization of objects that have collection fields.

### Failure vs void — orthogonal concepts

- `voidable T` — might be absent (`void`); unwrap with `but void is default`.
- `T or failure` — might be a failure; propagate with `or pass the failure off`,
  handle with `Try to: ... In case of failure: ...`.
- Both can combine: `voidable T or failure` is possible but unusual.
- `map lookup` returns `voidable V` (key might not exist).
- Declared-fallible functions return `T or failure`.

### The for-each body cannot add or remove elements during iteration

`For each x in series` forbids **structural mutation** of the iterated collection —
`Add` and `Remove` operations that change the series' length, or any entry-add /
entry-remove on the iterated map. Both are caught at runtime with a named error:

```
'items' was modified during a for-each loop on line 5 — collect into a separate series,
or use a While loop if you need to change it while looping.
```

**Element-value assignment** (`the first of items becomes 99`) is not caught — the
loop still visits all original elements, but you observe the changed values mid-iteration.
If you need predictable per-element values, snapshot the series first.

If you need to add, remove, or filter during a loop, collect into a new series or
use `While` with an index.

### Cooperative scheduler: `Yield.` does not re-enqueue the calling task

> ⚠ **Everything in this subsection describes the INTERPRETER only.** These are artefacts
> of the cooperative scheduler and none of them apply to compiled programs, where each task
> is a real OS thread. Where a consequence below changes when compiled, it says so.

`Yield.` calls `DrainOne()` synchronously — one item from the scheduler queue is
run as a subroutine, then control returns to the calling task. The calling task is
NOT suspended and NOT re-enqueued. Its continuation is on the C# call stack, not
in the scheduler queue.

**Consequence 1 — fan-out work-queues do not distribute.**
In a pattern with N workers reading from a shared channel, only ONE worker ever
gets items. When the channel is empty and multiple workers call
`the delivery from work`, each worker's delivery blocks in `DrainUntil`. The
DrainUntil calls nest synchronously. Whichever worker's DrainUntil condition fires
first (when the channel gets items) exits and processes items uninterrupted — the
other workers' conditions are checked after the first worker finishes, finding the
channel closed-and-empty. Example distribution with 3 workers: 30/0/0, not
10/10/10.

**Compiled, work really does distribute** — the workers are OS threads contending on a
mutex/condvar channel, so each takes items as it becomes free. Do not assert an exact
split, though: the distribution is genuinely nondeterministic. Assert the invariant (every
item processed exactly once, totals correct), which is what the compiler's own fan-out
test does.

**Consequence 2 — `Yield.` in a producer with a blocking collector deadlocks.**
```
Have rabbit start a task as worker:     ← enqueued first
    Define job as the delivery from work.   ← blocks → DrainUntil
    ...
Done.
Have rabbit start a task as producer:   ← enqueued second
    Send 1 through work.
    Yield.                              ← DrainOne → runs collector
    ...
Done.
Have rabbit start a task as collector:  ← enqueued third
    Define got as the delivery from results.  ← blocks → DrainUntil → _ready empty → DEADLOCK
    ...
Done.
```
The producer is on the call stack (not in `_ready`). The collector's `DrainUntil`
exhausts the queue with nothing left to produce results. The scheduler's deadlock
detector fires: "channel deadlock — a task is waiting for delivery but no running
or queued tasks will send."

**Consequence 3 — close does correctly wake all blocked workers.**
When a DrainUntil chain eventually runs the producer (which closes the channel),
ALL outer workers' `DrainUntil` conditions (`chan.IsClosed`) become TRUE. Every
blocked worker exits cleanly via `void`. No worker hangs at close. This is the
coordination property the scheduler does guarantee.

**Where this leaves you.** Compiling is what resolves all three: real threads block
independently, so work distributes, the Consequence-2 deadlock cannot arise, and close still
wakes every blocked receiver. The interpreter's cooperative scheduler is unchanged and
these artefacts remain there — which is why the compiler's concurrency tests assert
order-independent invariants against the binary rather than comparing output with the
interpreter, and why the interpreter's particular interleaving is explicitly not part of
the language specification.

When writing a fan-out program that must run on **both** backends, treat the interpreted
distribution as "first-to-unblock worker gets all" and depend only on the aggregate.
