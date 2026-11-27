# Cufet Grammar & Constraints Reference

The exact rules of Cufet: what is allowed, what it means, and what is refused. To learn the language, read [REFERENCE.md](REFERENCE.md); for the books it ships with, [BOOKS.md](BOOKS.md).

Every rule holds interpreted and compiled, unless it says otherwise. Two things differ on purpose: the order concurrent tasks interleave in, and the sequence a random generator gives. Where the compiler cannot build something the interpreter runs, it refuses it.

---

## Contents

- [1. Reserved keywords](#1-reserved-keywords)
- [2. Object methods and field access](#2-object-methods-and-field-access)
- [3. Value vs. reference semantics](#3-value-vs-reference-semantics)
- [4. Comparison forms — both work everywhere](#4-comparison-forms--both-work-everywhere)
- [5. Collections, books, pulls and tasks](#5-collections-books-pulls-and-tasks)
- [6. Where constructs are allowed](#6-where-constructs-are-allowed)
- [7. Streaming pipes](#7-streaming-pipes)
- [8. Sharp edges](#8-sharp-edges)
- [9. Loops, and the interpreter's scheduler](#9-loops-and-the-interpreters-scheduler)

---

## 1. Reserved keywords

Each word in these tables is reserved: it cannot name a variable, a field, a parameter, a function, a method, a type, or the variable of a `For each`. Reserved words are case-insensitive (`State`, `state` and `STATE` are one word).

### Articles

| Word |
|---|
| `a` |
| `an` |
| `the` |

An article may stand before any name and changes nothing there. After `is` it does:

- `x is a <type>` and `x is not a <type>` test a type.
- `x is the <name>` compares with the value of that name, as `x is <name>` does.

### Statement-level keywords

| Word | Where |
|---|---|
| `state` | `State <value>.` |
| `define` | `Define <name> as <value>.` |
| `as` | |
| `becomes` | `The <name> becomes <value>.` |
| `permanently` | `Define <name> as <value> permanently.` |
| `shadow` | `Define a shadow <name> as <value>.` |
| `return` | `Return <value>.` |
| `stop` | `Stop.` ends the loop |
| `exit` | `Exit.`, `Exit with <number>.` end the program |
| `skip` | `Skip.` goes to the loop's next turn |
| `bury` | `Have <rabbit> bury <value>.`, and never without the rabbit |
| `unbury` | `unbury <stash>`, a `voidable T` |
| `stash` | the type, `a stash of number` |
| `cite` | `Cite <name>.`, `Cite <name> on (…).` |

### Control flow

| Word | Where |
|---|---|
| `if` | `If <fact>:` |
| `otherwise` | `Otherwise:`, `Otherwise if <fact>:`, and in `when` |
| `when` | `<value> when <fact>, otherwise <value>` |
| `done` | `Done.` ends a block |
| `while` | `While <fact>, repeat:` |
| `repeat` | `Repeat:` … `Until <fact>.`, and `, repeat:` |
| `until` | |
| `try` | `Try to:` |
| `case` | `In case of failure:`, `In case of exception:` |
| `suppress` | `Suppress the exception.` |
| `judge` | `Judge <value>, where it is:` |
| `where` | |

### A bits width

```cufet-fragment
State the width of 0x0F.        → 8
State 0b0 at 3 bits.            → 0b000
Define p as 0b1 at n bits.      ← the width may be computed
```

- `the width of <bits>` is the value's width, a number.
- `<bits> at <n> bits` is the value at width `n`; `n` is any number expression.
- Otherwise a width only grows to fit the value: `0b0 shifted left by 2` is `0b0`.
- Widening always works. Narrowing that drops only zeros works: `0b00000001 at 2 bits` is `0b01`.
- Narrowing that drops a set bit is refused: by the checker when the value and the width are both literal, and otherwise at run time as an exception, as dividing by zero is. `In case of exception` catches it; `In case of failure` does not.
- `width`, `at` and `bits` are not reserved: each may name a variable or a field.

### `Increment` / `Decrement`

```cufet-fragment
Increment i by 1.
Decrement remaining by 1.
Increment one's tally by 3.
Increment total by item at (rr, cc) of board.
```

- `Increment <target> by <amount>.` is `The <target> becomes <target> + <amount>.`, and `Decrement` is the same with `-`.
- The target is a name or a possessive chain (`one's tally`). Anything else is refused.
- The amount is any expression.
- Numbers only: `Increment` on text is refused. Growing a series is `Insert`.
- A `permanently` name is refused, as `becomes` on it is.

### A body takes a comma and one thing, or a colon and a block

- After a header, a **comma** takes one thing on the same line, and a **colon** opens a block that `Done.` closes.
- For a body that gives back a value, the one thing is an expression, and `Return` is implied. For any other body it is a statement.

| Body | Inline form | |
|---|---|---|
| function with a return type | expression | `Bind number to double, given (the number amount), amount * 2.` |
| getter | expression | `Get area as number, one's radius * one's radius * 3.` |
| named maker | expression | `Bind making a vec to square-vec, given (the number seed), a new vec { … }.` |
| operator overload | expression | `Bind overloading +, given (the lhs is a vec, the rhs is a vec), a new vec { … }.` |
| `void` function | statement | `Bind void to shout, given (the text word), State word in uppercase.` |
| setter | statement | `Set radius given (the number r), one's radius becomes r.` |
| unmaker | statement | `Bind unmaking a gate to close-gate, State "closing {one's id}".` |
| `If`, `Judge` arm | statement | `If x is 1, State "one".` |
| task | statement | `Have rabbit start a task as batch-1, return 1 + 2 + 3.` |

- A task's inline body is a statement, so a value it gives back is written with `return`.
- A function with no parameters has its inline body straight after the comma: `Bind number to leg-pairs, legs / 2.` `given` is what marks parameters.
- A loop's header already holds a comma, so a loop opens a block with `, repeat:`, and its inline form is a statement after the comma:

```cufet-fragment
For each n in items, repeat: State n. Done.     ← block
For each n in items, State n.                   ← inline
While i is less than 3, the i becomes i + 1.    ← inline
```

- The consumer loop of a pipe stage, `for each <name> from input`, has no `in` clause and takes a comma or a colon as other bodies do. `, repeat:` is refused there.
- An inline body is a one-statement block: it checks and runs the same.
- `Try`, `Pull` and a function with no name take only the block form.

### `Judge`

```cufet-fragment
Judge thing, where it is:
    A number, state "a number".
    A text or a fact:
        State "not a number".
    Done.
Done.
```

- The subject is any expression. It is evaluated once, and is `it` inside the arms.
- An arm names types (`A num-node`, `A number or a text`) or values (`"cd"`, `"fg" or "bg"`), then takes a comma and a statement, or a colon and a block.
- A value is a literal: a number (`-1` included), text without holes, bits, `true` or `false`.
- Every arm of one judgement names types, or every arm names values. Mixing them is refused.
- Over a closed union with type arms, `Otherwise` is optional and a case no arm covers is refused. Every other judgement must end with `Otherwise`.
- Nothing may follow `Otherwise`.
- A type arm narrows `it` to the types it names. A value arm narrows nothing.
- An arm an earlier arm already covers is legal and never runs: the first match wins. `cufet check` warns about it.
- A judgement whose arms all return counts as returning.
- Compiled, type arms need a closed-union subject; the compiler refuses any other, and `cufet check --native` warns. Value arms compile on any subject.

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
| `shorthand` | Shorthand | Shorthand declaration: `Define shorthand <name> as <type>.` |
| `new` | New | Object/map/matrix literal |
| `one` | One | Self-reference inside methods |
| `get` | GetKw | Getter declaration |
| `set` | SetKw | Setter declaration |
| `making` | MakingKw | Named maker marker |
| `unmaking` | UnmakingKw | Unmaker marker |
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
| `reverse` | Reverse | `<series> in reverse`, and a reverse sort after `sorted` |
| `by` | By | Sort key marker — a field or a function |
| `range` | Range | Range expression |
| `counting` | Counting | Range step marker |

### Text operations

| Word | Token |
|---|---|
| `joined` | Joined |
| `converted` | Converted |
| `split` | Split |
| `contains` | Contains |
| `position` | Position |
| `end` | End |
| `replace` | Replace |
| `uppercase` | Uppercase |
| `lowercase` | Lowercase |
| `trimmed` | Trimmed |
| `alphabetic` | Alphabetic |
| `numeric` | Numeric |
| `whitespace` | Whitespace |

**Two literal forms, one type.** Both give `text`, and nothing after the lexer can tell them apart.

| Form | Escapes (`\n`, `\"`, `\{`, …) | Holes (`{...}`) | Nests |
|---|---|---|---|
| `"..."` | yes | yes | — |
| `<<...>>` | no | no | yes, counting `<<` and `>>` |

- Both may run across lines. A line break inside either is one `\n`, whatever line endings the file has. A lone `\r` is kept.
- A character is a Unicode code point, on both backends. `the length of`, `the characters from N to M`, `the first`/`last N characters` and `the position of` all count code points.
- A code point is not a grapheme: `e` followed by a combining accent has length 2.

### Records and maps

| Word | Token | Notes |
|---|---|---|
| `record` | Record | Record type |
| `with` | With | Field list / element list opener |
| `like` | Like | Record shape annotation |
| `map` | Map | Map type |
| `has` | Has | Map-has-key check |

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
| `file` | File |
| `write` | Write |
| `append` | Append |
| `run` | Run |
| `stream` | Stream |
| `open` | Open |
| `directory` | DirectoryKw |
| `path` | PathKw |
| `arguments` | Arguments |
| `acknowledge` | AcknowledgeKw |
| `catalogue` | CatalogueKw |
| `atlas` | AtlasKw |

### Concurrency

| Word | Token | Notes |
|---|---|---|
| `have` | HaveKw | `Have <rabbit> start a task …` |
| `task` | TaskKw | |
| `send` | Send | `Send <value> through <channel>.` |
| `through` | Through | |
| `close` | Close | `Close <channel>.` |
| `awaited` | Awaited | `the awaited result of <task>` |
| `yield` | YieldKw | |
| `pull` | Pull | `Pull a book on <name>.` — `book` and `books` are NOT reserved |

`channel` and `delivery` are **not** here: both take a mandatory `of`/`from`, so they are contextual — see [Contextual words](#contextual-words--not-reserved).

### Matrix

**None of it is reserved.** `matrix`, `at`, `rows`, `columns` and `filled` are all contextual — see [Contextual words](#contextual-words--not-reserved) below. `Define rows as 5.` works even though the collections book uses the word.

### Bits

| Word | Token | Notes |
|---|---|---|
| `shifted` | Shifted | `<bits> shifted left by <n>` / `shifted right by <n>` |
| `xor` | Xor | Exclusive-or — see [Comparison and logic](#comparison-and-logic) |

`left`, `right`, `hex`, `binary` and `octal` are **not** reserved: `the left of node` and `Define binary as "…"` both work. The `0x` / `0b` / `0o` literal forms lex as a single `Bits` token, digit count included — see [Leading zeros are significant](#leading-zeros-are-significant--the-width-comes-from-the-digit-count).

### Chance and randomness

**None of it is reserved.** `random`, `randomly`, `shuffled`, `guess` and `seed` are all contextual — see [Contextual words](#contextual-words--not-reserved) below, and the [Chance book](#chance-book-operations) for what each one does.

### Boolean literals

| Word | Token | Value |
|---|---|---|
| `true` | TrueKw | the true `fact` |
| `false` | FalseKw | the false `fact` |

`true` and `false` are reserved keywords that produce `fact` values — the same type that comparisons produce. They work wherever a `fact` is valid: expression position, condition position, `return`, `becomes`, arguments, and boolean-typed fields/params/channels.

### Comparison and logic

These are reserved and meaningful in both condition position (`If`, `While`, `until`) and expression position. They cannot be used as identifiers.

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

`or` is reserved too, and is listed under [Failures](#failures) because it carries `or pass the failure off` as well. `and`, `or` and `xor` are **gates**: each works on `fact` (one bit) and on `bits` (N bits), with precedence `and` > `xor` > `or`.

### Contextual words — NOT reserved

Each of these words has a meaning only in the place shown. Anywhere else it is an ordinary name, so it may be a variable, a parameter, a field or a loop's variable.

| Word | Recognised in |
|---|---|
| `line`, `lines`, `all` | `read a line from …`, `read all from …`, `read all lines from …` |
| `reading`, `writing` | `… open for reading as …`, `… open for writing as …` |
| `exists` | `the path <p> exists` |
| `characters` | `the characters from <a> to <b> of <t>`, `the first <n> characters of <t>` |
| `delivery` | `the delivery from <channel>` |
| `entry` | `the entry for <k> in <m>` |
| `key` | `<m> has a key for <k>` |
| `channel` | `a channel of <T>` |
| `interrupt`, `requested` | `the interrupt is requested` |
| `environment`, `variable` | `the environment variable <n>` |
| `read` | `read a line …`, `read all …` |
| `contents` | `the contents of the directory <path>` |
| `current` | `the current directory` |
| `make` | `Make the directory <path>.` |
| `text`, `fact`, `bits` | a type |
| `axiom` | `the c-language axiom …`, after a language book's name |
| `default` | `with default <value>` after a field |
| `starts`, `ends` | `<text> starts with …`, `<text> ends with …` |
| `code`, `point` | `the code point of <text>` |
| `region` | `and region` in an object's conformance list |
| `terminal` | `run <prog> with the terminal` |
| `left`, `right` | `shifted left by`, `shifted right by` |
| `hex`, `binary`, `octal` | `converted to hex` / `binary` / `octal` |
| `width`, `at`, `bits` | `the width of <bits>`, `<bits> at <n> bits` |
| `at`, `filled`, `rows`, `columns`, `matrix` | the collections book |
| `random`, `randomly`, `shuffled`, `guess`, `seed` | the chance book; `Seed the chance with <n>.` |
| `first` … `tenth`, `last` | `the <ordinal> of <series>`, `first`/`last <n> characters of <text>` |
| `book`, `books`, `module`, `rabbit` | `Pull`, and a conformance list |

- `input` and `output` mean a stream in a pipe stage (§7). Each may be a parameter, a field or a loop's variable, but not a variable: both are already defined everywhere.
- `the length of`, `the size of`, `the rows of`, `the columns of`, `the width of` and `the key of` are decided by the type of what follows `of`. On any other type the word is that value's field.
- `the <ordinal> of x` is always a position. A field named `first` is reached as `x's first`.
- A statement's first word may be capitalised even when it is not reserved: `Output 7.`, `Seed the chance with 42.`, `Make the directory "out".`
- `Remove` is decided by what follows it: `Remove <x> from <series>.`, `Remove the file <path>.`, `Remove the directory <path>.`
- An object may say `and module and region`. `and book and region` is refused.
- These stay **reserved**: `arguments`, `position`, `number`, `path`, `catalogue`, `atlas`, `alphabetic`, `numeric`, `whitespace`.
- `<text> is alphabetic` (or `numeric`, `whitespace`) is true when every character is in the class. Empty text is in none.

### Bit patterns — `0x` / `0b` / `0o`

```cufet
State 0xFF.   // 0xFF   — hex
State 0b1010.   // 0b1010 — binary
State 0o755.   // 0o755  — octal
State 0xDE_AD_BE_EF.   // 0xDEADBEEF — '_' groups digits and is dropped
```

- `bits` is its own type. Nothing converts between it and `number` without being asked: `0xFF = 255` is refused.
- A value prints in the base it was written in. Hex digits print in capitals.
- `0755` is the number seven hundred and fifty-five. Octal is written `0o755`.
- `_` may group the digits of a `0x`, `0b` or `0o` literal, between two digits only: `0x_FF`, `0xFF_` and `0xF__F` are refused. A decimal number takes no `_`.
- A value is at most 64 bits wide. A wider literal is refused.

### Leading zeros are significant — the width comes from the digit count

```cufet
State 0xF.   // 0xF   — 4 bits
State 0x0F.   // 0x0F  — 8 bits
State 0x000F.   // 0x000F — 16 bits
```

- Each hex digit is 4 bits, each octal digit 3, each binary digit 1.
- Equality and ordering compare the value: `0xF = 0x0F` and `0o377 = 0xFF` are true.

### Gates — `and` / `or` / `not` / `xor`

```cufet
State 0xFF and 0x0F.   // 0x0F
State 0xF0 or 0x0F.   // 0xFF
State 0b1100 xor 0b1010.   // 0b0110
State not 0xFF.   // 0x00
State true xor false.   // true
```

- `and`, `or`, `xor` and `not` work on `fact` and on `bits`, never on `number`: `5 and 3` and `not 5` are refused.
- Both sides of a gate are facts, or both are bits. So `0xFF and 0x0F = 0x0F`, which groups as `0xFF and (0x0F = 0x0F)`, is refused.
- `not` flips within the value's own width: `not 0x0` is `0xF`, `not 0x00` is `0xFF`.
- Precedence is `and`, then `xor`, then `or`, all below comparisons: `a and b xor c or d` is `((a and b) xor c) or d`.
- On facts, `and` and `or` skip their right side when the left decides. On bits both sides always run, and `xor` always runs both.

### The result takes the LEFT operand's base and width

```cufet
State 0xFF and 0b1010.   // 0x0A     — hex on the left, so hex out
State 0b1010 and 0xFF.   // 0b1010   — binary on the left, so binary out
```

- A result has the left operand's base and width, raised to fit the value: `0b1 or 0xFF` is `0b11111111`. It never narrows, so `(0b1 * 0x100) - 0x1` is `0b011111111`.
- A width is raised only as far as the value reaches: `0b00001111 shifted left by 2` is `0b00111100`, and `0b0 shifted left by 2` is `0b0`.
- To narrow, mask with `and`, or say the width with `at <n> bits`.

### Crossing between a quantity and a pattern

```cufet
State 255 converted to hex.   // 0xFF
State 10 converted to binary.   // 0b1010
State 493 converted to octal.   // 0o755
State 0xFF converted to number.   // 255
State 0xFF converted to text.   // "0xFF"
```

- `<bits> converted to number` is a plain `number`; it cannot fail.
- `<number> converted to hex` (or `binary`, `octal`) is plain `bits`. A number that is negative, not whole, or 2⁶⁴ or more raises an exception.
- To change base, go through a number: `x converted to number converted to binary`.

### Shifts — `shifted left by` / `shifted right by`

```cufet
State 0b0001 shifted left by 3.   // 0b1000
State 0xFF shifted left by 4.   // 0xFF0  — widened, nothing lost
State 0xFF shifted right by 4.   // 0x0F
```

- The amount is a whole, non-negative `number`. A `bits` amount is refused; a negative or fractional one raises an exception.
- A left shift widens; past 64 bits it raises an exception. A right shift drops the low bits.

```cufet
Define n as 8.
State (0b1 shifted left by n) - 0x1.   // 0b011111111 — the standard n-bit mask
```

### Arithmetic

```cufet
State 0x0F + 0x01.   // 0x10
State 0xFF * 0x02.   // 0x1FE  — widened to hold it
State 0xFF / 0x10.   // 0x0F   — integer division
State 0x07 / 0x02.   // 0x03   — where 7 / 2 is 3.5
```

- `+ - * / %` work on two bits values. `/` is integer division.
- `<`, `>`, `<=`, `>=` and their word forms compare the value.
- Unary minus is refused.
- A result below zero or past 64 bits raises an exception, as dividing by zero does. `In case of exception` catches it.

```cufet-fragment
State 0x00 - 0x1.   // would be negative, and bits are unsigned
State 0xFFFFFFFFFFFFFFFF + 0x1.   // does not fit in 64 bits
```

### Comments — `//`, `/* ... */`, and the documentation forms `///` and `/** ... */`

```cufet
// to the end of the line
/* a block, which
   may span lines */
Define x as 5.   // inline
```

- `//` runs to the end of the line, and may end the file.
- `/* … */` nests: an inner `/*` needs its own `*/`. One that never closes is refused, at the outermost `/*`.
- A `/*` inside a `//` comment opens nothing, and `//` inside a block comment is text. Comment marks inside a string are text.
- `///` and `/** … */` are documentation, and their content is Markdown. `////` (four or more slashes) and `/**/` are ordinary comments.

```cufet
/// Splits a line on the separator.
Define first-word as "cufet".

/** Fails when the separator is empty.
  * The star column is stripped.
  */
Define second-word as "shell".
```

- A doc comment belongs to the declaration below it, and an editor shows it wherever that name is used:

| Documented | Shown at |
|---|---|
| A function | its declaration and every call |
| A type | every place its name is written |
| A book or module | `Pull`, and every `‹book›'s ‹member›` |
| A method or getter of a book | uses through its owner: `math's round`, `math's pi` |

- A book's member is found only through its owner, never by its bare name.
- A name a book was pulled under (`Pull greeting-kit as kit.`) shows nothing.
- The bundled books carry documentation of their own.
- A blank `///` line is a paragraph break. In a `/** … */` block, a leading `*` on a continuation line, with the space before it and one space after, is removed; the rest is kept as written.

---

## 2. Object methods and field access

- Inside a method, the names in scope are `one` (the object the method was called on), the method's parameters, and the functions and constants visible where the object is defined.
- A field is never in scope by its bare name; it is `one's <field>`. A bare field name is refused.

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

- `one's <field>`, like any expression, may be the series or map of every series and map operation: `Insert`, `Remove`, `Item N of … becomes`, `In …, the entry for K becomes V`, `has a key for`, `the number of`, `For each`, `sorted`, and the rest.
- `one's <field> becomes <value>.` replaces the field.
- ⚠ A change made to a computed series is lost, and nothing reports it: `Insert x into (one's cards sorted)` inserts into a copy nothing holds. A target that is not a series at all is refused.

---

## 3. Value vs. reference semantics

Storing a value copies it or shares it, and its type decides which.

| Type | When stored |
|---|---|
| `number`, `text`, `fact`, `bits` | copied |
| record, object (with what it embeds) | copied |
| series, catalogue, map, atlas | shared |
| `matrix`, `set`, `chase` | shared |
| stash, channel | shared |
| a voidable, a union | as the value it holds |

A value is stored by `Define`, by `becomes` (to a name, a field, an item or an entry), by `Insert`, as an argument, in a literal's items, entries or fields, as the variable of a `For each`, as the `it` of a `Judge`, and in a function that captures it.

- **A copy applies the same rule to each field.** A record inside a record is copied with it; a series or a map inside a record is shared with the original.
- **`becomes` on a shared name points that name elsewhere.** The other names keep what they had. This includes a parameter: changing the series it names reaches the caller, and giving it another does not.
- **What is taken out goes by its own type.** `Define card as item 1 of deck.` copies a record and shares a series. The same holds for a map's entry and for a field.
- **A `For each` variable is a copy of a record or an object**, so changing it leaves the collection alone. Over a map, the key and the value are each copied. A `Judge`'s `it` is a copy too; inside `If x is a …`, `x` is still `x`.
- **A field reached through the container is changed in place**: `The suit of item 1 of deck becomes "Spades".`
- **`one` is the object the method was called on**, not a copy, wherever that object is held: a name, a field, or an item of a series.
- **A record or an object nothing holds is a value of its own**: the result of a call, of a getter, of `when`, or of `but void is`. A method called on it, or a field set in it, changes that value and nothing else.
- **Between tasks every value is copied**, whatever its type, by a channel and by a task that captures it. A channel itself is shared.

```cufet
Define tray as a record with (the label "a", the cards a series of number with (1)).
Define other as tray.
The label of other becomes "b".
Insert 2 into the cards of other.
State the label of tray.
State the number of the cards of tray.
```
```output
a
2
```

---

## 4. Comparison forms — both work everywhere

Every form below works in a condition and in an expression, and each gives a `fact`.

| Symbol | Word | Meaning |
|---|---|---|
| `x = y` | `x is y` | equal |
| | `x is not y`, `x is not equal to y` | not equal |
| `x > y` | `x is greater than y` | greater |
| `x < y` | `x is less than y` | less |
| `x >= y` | `x is y or more`, `x is not less than y` | at least |
| `x <= y` | `x is y or less`, `x is not greater than y` | at most |

- `=` is only equality. Assignment is `becomes`; a declaration is `Define … as`.
- There is no `!=`, no `is equal to`, and no `is more than`; `is more than` is refused with a pointer to `is greater than`.
- `true` and `false` are `fact` values, usable wherever a `fact` is.
- `not <fact>` negates; `x is true` and `x is false` compare.
- REFERENCE teaches words in conditions and symbols in expressions. That is style; the checker accepts either in both places.

```cufet-fragment
If count is not greater than 10:           ← count <= 10
While not (cast is-empty on pq), repeat:
Define ok as (score is not less than 50).
Define big as x > 100.
```

---

## 5. Collections, books, pulls and tasks

Wherever an operation below names a series, a map, a set or a matrix, any expression of that type may stand there: a name, `one's cards`, `alice's hand`, a call.

### Series operations

| Syntax | Read or change |
|---|---|
| `Insert X into s.` | change |
| `Insert X into the start of s.` | change |
| `Insert X after item N of s.` | change |
| `Remove the last item from s.` | change |
| `Remove item N from s.` | change |
| `Remove X from s.` | change: the first item equal to `X` |
| `Item N of s becomes X.` | change |
| `the number of s` | read |
| `the first of s`, `the last of s` | read |
| `item N of s` | read |
| `For each x in s, repeat:` | read |
| `s sorted`, `s sorted by <field or function>`, `s in reverse` | read |

- A change to a series that is computed rather than named (`Insert 1 into (s sorted).`) is lost, and nothing reports it. A target that is not a series is refused.

### Map operations

| Syntax | Read or change |
|---|---|
| `In m, the entry for K becomes V.` | change |
| `Remove K from m.` | change |
| `the entry for K in m` | read, a voidable |
| `m has a key for K` | read |
| `the size of m` | read |
| `For each pair in m, repeat:` | read |

### Set operations

| Syntax | Read or change |
|---|---|
| `a set of T`, `a set of T with (a, b, c)` | build |
| `Insert V into s.` | change |
| `s has V` | read |
| `the number of s` | read |
| `For each name in s, repeat:` | read |

- A set holds each value once, and inserting one it holds changes nothing.
- A set's order is the order its values were first inserted, on both backends. Which types may be a value, and how they compare, are the rules for a map's keys.
- A set has no positions: `Insert V after the first item of s.` is refused.
- `the size of` a set is refused; it is `the number of`. `m has V` on a map is refused; it is `m has a key for V`. `s has a key for V` on a set is refused; it is `s has V`. `has a key for` on anything but a map is refused.
- A value cannot be removed from a set.

### Matrices — the collections book

Every form below needs `Pull a book on collections.` in scope.

| Syntax | |
|---|---|
| `a matrix with ((1, 2), (3, 4))` | rows given; they must be the same length |
| `a matrix with R by C` | every cell 0 |
| `a matrix with R by C filled with V` | every cell `V` |
| `the item at (r, c) of m` | read |
| `The item at (r, c) of m becomes V.` | change |
| `the rows of m`, `the columns of m` | read |
| `cast collections's transpose of (m)` | a new matrix |

- A matrix holds numbers only. Indices start at 1.
- `R` and `C` are whole numbers above zero: refused when checked if they are literals, and an exception when the line runs if they are computed.
- `item N of m` is refused; a cell is `the item at (r, c) of m`.

| Expression | Meaning | Needs |
|---|---|---|
| `a + b`, `a - b` | element by element | the same rows and columns |
| `a * b` | matrix product | `a`'s columns = `b`'s rows |
| `m * n`, `n * m` (`n` a number) | every element times `n` | |

- `+`, `-` and `*` between matrices can fail, with category `"dimension-mismatch"`, so they must be handled: inside `Try to:`, or with `but on failure <default>`.
- `/` between matrices, and `+`, `-` or `/` between a matrix and a number, are refused.

```cufet-refused
Pull a book on collections.
    Define lhs as a matrix with ((1, 2), (3, 4)).
    Define rhs as a matrix with ((5, 6), (7, 8)).
    Define sum as lhs + rhs.       ← refused: matrix '+' can fail
    Define sum2 as lhs + rhs but on failure (a matrix with 2 by 2).  ← fine
Done.
```

### Chance book operations

Every form below needs `Pull a book on chance.` in scope; without it, it is refused.

| Syntax | Type | |
|---|---|---|
| `a random number from low to high` | `number` | a whole number, both ends included |
| `a random item from s` | `voidable T` | `void` when `s` is empty |
| `randomly shuffled s` | `series of T` | a new series; `s` is unchanged |
| `a random guess` | `fact` | |
| `Seed the chance with <number>.` | statement | the same seed gives the same sequence |

- `low` greater than `high` raises an exception.
- `low` and `high` may be arithmetic (`from 1 to n + 5`), not a comparison.
- Without `Seed`, each run differs.

### `Pull … Done.`

`Pull` opens a scope and `Done.` closes it. What was pulled is in scope until then.

```cufet-fragment
Pull a book on math.                         ← a book
Pull a book on math as m.
Pull books on math, collections, and chance.
Pull books on math as m, collections as c, and chance.
Pull greeting-kit.                           ← a module you hold one of
Pull greeting-kit as kit.
Pull a rabbit.                               ← a rabbit, which is a region
Pull a rabbit as hopper.
```

- A book is pulled `a book on <name>` or `books on <a>, … and <z>`; anything else is pulled by its name. A book pulled plainly, or anything else pulled as a book, is refused, and so is `Pull books on` naming one that is not a book. To pull both kinds, nest the pulls.
- The article before a module's name is noise: `Pull a greeting-kit.` is `Pull greeting-kit.`
- Pulls nest. A `Bind` inside a `Pull` body is a top-level `Bind`.

**Modules and books.**

- An object is a module when it says `and module`, and a book when it says `and book`. Every book is a module. `and book and module` is refused, and so is `and book and region`.
- A book owns no lifetime. A module may own a region, with `and region`.
- An object that is not a module cannot be pulled. A module with fields is refused when the pull runs; make one with `a new <type> { … }` instead.
- A module's body holds `Define object` declarations and methods. Its object types, and unions of them, are reachable through it; an interface cannot be declared there.
- A module that declares interfaces hands out only what they declare. One that declares none hands out every method. `module` and `book` declare no methods. Inside the module, through `one`, everything is reachable.

```cufet-fragment
Define interface greeter for { The text function greet }.

Define object greeting-kit with () and greeter and module:
    Bind text to greet: Return cast one's helper on (). Done.
    Bind text to helper: Return "hello". Done.
Done.

Pull greeting-kit.
    State cast greeting-kit's greet on ().     ← in `greeter`, so reachable
    State cast greeting-kit's helper on ().    ← refused, also through an alias
Done.
```

- No module's name is reserved, and neither are `book` and `books`. A bundled book's name (`math`, `collections`, `chance`, `rabbit`, …) cannot name a new object; `a new collections { }` and `unto` on a bundled book are refused.
- `blueprints`, `regex` and the language books are books without a `Define object`.

### What a body may reach for

- A function or method reaches the names visible where it is written, and any module its caller pulled. A name that is neither is refused when the program is checked.
- A plain function reaches a module only where it is written inside that module's `Pull`.
- A module written inside a `Pull` keeps what that pull gives it. One written at the top level takes its modules from where it is pulled, and pulling it without them is refused.
- A name made with `as` (`Pull a book on math as m.`) is an ordinary name of that block. A function written outside the block cannot reach it.
- A function with no name sees everything around it, aliases and locals included.
- A module's needs are not followed through a free function it calls, or through a function held in a variable.

```cufet
Define object circles with () and book:
    Bind number to area, given (the number r):
        Return math's pi * r * r.
    Done.
Done.

Pull books on math, and circles.
    State (cast circles's area on (2)) converted to text.
Done.
```

### Rabbits and tasks

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

- `Have rabbit start a task` must be inside a rabbit's `Pull`; outside one it is refused. `rabbit` means the innermost rabbit. A rabbit pulled `as hopper` may be named instead: `Have hopper start a task …`. Naming a rabbit pulled further out is refused.
- `Pull an automatic rabbit.`, `Pull a sequential rabbit.` and `Pull a parallel rabbit.` (the default) say how its tasks run together; REFERENCE, *Composition modes*. None of the three words is reserved.
- Every task joins at its rabbit's `Done.`, so no task outlives its rabbit.
- A task named with `as <name>` gives its result through `the awaited result of <name>`. An unnamed task's `return` value is dropped.
- A task may await another, and several may await the same one, but only one declared earlier.
- In an automatic rabbit, each task says what it waits on: `Have dispatcher start a task for an order in orders:`. `order` is the delivery, a `voidable T`; `orders` is the channel. Such a task has no name, runs as the rabbit's own code at its `Done.`, may change names around it, may not `return`, and may not use a name from a block already ended.
- A task gets a copy of what it captures. Changing a captured name is refused when something outside the task reads that name afterwards; in an automatic rabbit it is allowed.
- Interpreted, tasks take turns on one thread; compiled, each is a thread of its own. Which order they run in is not specified.

---

## 6. Where constructs are allowed

### Where a declaration belongs

- An object, an interface and a shorthand belong to the program wherever they are written (in a function, an `If`, a loop, a task or a rabbit's block), and are usable before and after it.
- A value belongs to its block: `Define x as 5.` lasts until that block's `Done.`
- A function (`Bind`) may be declared at the top level, inside a `Pull` body (where it is top-level), inside another function, and as a method inside an object. Inside an `If`, a loop, a `Try` or a `With` it is refused.
- An operator overload and an unmaker may be declared only at the top level.
- An object or interface declared twice is the second one; `cufet check` warns that nothing reaches the first. A second shorthand of one name, a second free function with the same argument types, a second method of one name in one object (nested or `unto`), a second overload for one ordered pair of types, and a second unmaker for one type are refused.

### Inside an object's body

- An object's body holds methods (`Bind`), getters (`Get`) and setters (`Set`). Its fields are in the `with (…)` header. A `Define` in the body is refused, except that a module's body may declare object types (§5).
- `a new <type> { … }` must give every field that has no default. A maker hides that:

```cufet-fragment
Bind making a graph to new-graph:
    Define empty-nodes as a series of node.
    Define empty-adj   as a map from node to series of edge.
    Return a new graph { the nodes empty-nodes, the adjacency empty-adj }.
Done.
```

- A getter, `Get <name> as <type>:`, is read like a field: `c's label` or `the label of c`. Its body reaches fields through `one`.

```cufet
Define object card with (the text suit, the text rank):
    Get label as text:
        Return one's rank joined to " of " joined to one's suit.
    Done.
Done.

Define c as a new card { the suit "Spades", the rank "Ace" }.
State c's label.
State the label of c.
```

### Series and map values

```cufet-fragment
Define xs as a series of number.                       ← empty
Define primes as a series of number with (2, 3, 5).
Define m as a map from text to number.                 ← empty
Define m as a map from text to number with ("k": 1).
Define m as a map with ("k": 1).                       ← types inferred
```

- Each is an expression, usable anywhere a value is: an argument, a `Return`, a map entry, a `but void is` default.
- `new` does not make a series or a map.

### Names a field or a loop's variable cannot take

- A reserved word (§1) cannot name a field, a parameter or a loop's variable. `to`, `from`, `start`, `end`, `it` and `category` are the ones most often reached for.
- `item` may be a loop's variable, in a series loop and in a pipe's consumer loop. Inside the loop, `item` alone is the variable; `item 2 of …` and `item at …` keep their meaning.
- `For each in s` (no name) binds `it`.
- Where a type is expected, `the <name> of …` is read as a type; elsewhere it is a field access. So `the stack of number counts` (a generic type) and `the city of alice` (a field) do not clash.

### `For each` takes five kinds of source

| Source | The variable holds | Ends when |
|---|---|---|
| a series | the element type | the last item is done |
| a map | a `mapping` (`the key of`, `the value of`) | the last entry is done |
| a set | the element type | the last value is done |
| a chase | each character, as a one-character text | the last character is done |
| a stash | the buried type, not `voidable` of it | the stash answers `void` |

- Anything else is refused. A text is not a source; put it in a chase.
- Over a stash, the loop is the same as this, so `Stop` and `Skip` mean what they mean in any loop:

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

### What a burying body may contain

- Allowed: plain statements, `If`/`Otherwise`, `While`, `Repeat … Until`, `For each` over a series or a stash, `Stop`, `Skip`, nested loops, a parameter given a new value, and locals of any type. Each keeps its state across a resumption.
- An `If` that narrows (`x is a <type>`, `x is not void`) keeps its narrowing, in the arm and in its `Otherwise`.
- A `Judge` keeps its narrowing and its `it`; `it` is evaluated once.
- A method may bury, as a function does. Its state belongs to the object it was called on.

Refused when the program is checked:

| In a burying body | |
|---|---|
| `Return` anywhere | a burying function ends by reaching its end |
| a `Judge` inside a `Judge` whose `it` has another type | one name holds one type |
| `Have … bury` in the `Otherwise` of a judgement with type arms over a subject that is not a closed union | |
| `Have … bury` inside `Try to`, a rabbit's `Pull`, a task, or a file block | |
| `Have … bury` inside `For each` over a map | |
| `Define a shadow` | |
| one name used at two types | |

### A stash, and function-valued fields

- `stash of T` is a value type: a local, a parameter, an item, a field (`Define object ticker with (the stash of number source):`).
- A stash lives in the region of the rabbit that buried it. Carrying it out of that region is refused when the program is checked.
- A field may hold a function: `the number function twice given (the number)`. The field's name sits between `function` and `given`. `void` is allowed there only as the return type (`the void function log`).
- In a series of functions, the `given` clause divides the type from the name:

| Written | Means |
|---|---|
| `the series of number function make given (the number)` | a function returning a series of numbers |
| `the series of number function given (the number) ops` | a series of functions |

- A function element type must carry its `given` clause wherever a name follows it. In a series literal nothing follows, so `a series of number function with (twice)` is a series of functions that take nothing.

```cufet
Bind number to twice, given (the number n): Return n * 2. Done.
Bind number to thrice, given (the number n): Return n * 3. Done.

Bind number to apply-all, given (the series of number function given (the number) ops, the number seed):
    Define acc as seed.
    For each op in ops, repeat: The acc becomes cast op on (acc). Done.
    Return acc.
Done.

State cast apply-all on (a series of number function given (the number) with (twice, thrice), 5).
```
```output
30
```

### Foreign source — an `axiom` in `[ ... ]`

```cufet
Pull a book on the c-language.
    Define c-language number get-pid as [getpid()].
    State cast get-pid.
Done.
```

- Square brackets hold foreign source, and appear nowhere else.
- Brackets nest; a bracket after a backslash does not count. Nothing is unescaped: the foreign side gets exactly the characters written. A lone `]` inside a foreign string ends the source early; write `\]`.
- The tag says the language and cannot be dropped: `Define c-language x as [ … ].` (or `Define a c-language axiom x as [ … ].`). `Define x as [ … ].` is refused.
- The language's book must be pulled: `Pull a book on the c-language.` It may be aliased (`as c`); the tag still names the language.
- What an axiom gives back is declared where it is written: `Define c-language number add, …`.
  - An axiom that names a result is run with `cast`, and the call works wherever any call does. As a statement, `Cast x on (…).` discards the result.
  - An axiom that names no result and takes no parameters is a preamble: pasted once, above every other axiom, so its declarations are shared. It is never run.
  - One that names no result and takes parameters is source with holes (below).
- An axiom binding is permanent, so a function body may use it.

### An axiom's parameters

```cufet-fragment
Define c-language open-read-only, given (the text file-path), as [open(the file-path, O_RDONLY)].
```

- A parameter is reached in the source by its article: `the file-path`. That is substituted everywhere in the source, inside a foreign string too.
- A parameter the source never uses is refused.
- Values cross, never source text:

| Cufet | reaches C as |
| --- | --- |
| `number` | `long long`; a fractional or out-of-range value raises an exception |
| `text` | `const char*`, UTF-8, valid for the call |
| `fact` | `int`, 1 or 0 |

- What comes back:

| Declared | C gives | |
| --- | --- | --- |
| `number` | any C whole number, signed or unsigned | exact; `size_t` arrives unsigned |
| `fact` | anything with a truth value | 1 or 0 |
| `voidable number` | `float`, `double`, `long double` | 17 significant digits; NaN, an infinity, or a magnitude no decimal holds is void |
| `voidable address` | any pointer | held, never read through; NULL is void |
| `voidable text` | `char*`, `const char*` | copied; NULL is void |

- A text result must be `voidable text`; plain `text` is refused. A floating result must be `voidable number` and a whole one `number`; the wrong one is refused by the C compiler.

| Written | Means |
| --- | --- |
| `Define c-language x as [ … ].` | declare an axiom |
| `Cast x on (a, b).` | run it as a statement; the result is discarded |
| `… as [ … ], and free it with <name>.` | the axiom that releases the address this one gives back |
| `the c-language number axiom <name>` | an axiom as a type: a parameter, a field, an item |
| `the c-language number axiom given (the text) <name>` | the same, saying what running it takes |
| `Define c-language x, given (the term t, …), as [ … ].` | source with holes: filled, never run |
| `x on (a, b)` | fill source and keep it, unrun |
| `the c-language axiom <name>` | complete source as a value |
| `<source> converted to text` | the source's text |
| `the text at <address>` | read through an address: a copied `voidable text` |

### Source with holes

```cufet
Pull a book on the c-language.
    Define c-language eq-function, given (the term type-c, the term eq-name, the c-language axiom test),
        as [static int the eq-name(the type-c a, the type-c b) { return the test; }].
    Define c-language value-equal, given (the term eq-name), as [the eq-name(a.value, b.value)].

    Define fragment as eq-function on ("int", "int_eq", value-equal on ("inner_eq")).
    State fragment converted to text.
Done.
```
```output
static int int_eq(int a, int b) { return inner_eq(a.value, b.value); }
```

- `x on (…)` fills and keeps; `cast x on (…)` on source with holes is refused, and `x on (…)` on anything that is not source is refused. Holes are filled by position; a named argument is refused.

| Declared | Takes | Goes in as |
| --- | --- | --- |
| `the c-language axiom test` | complete source of the same language | its text |
| `the term type-c` | a text | itself; when filled it must be a C name (letters, digits, `_`, not starting with a digit), or an exception is raised |
| `the text x`, `the number n`, `the fact f` | a value | a C literal: a quoted, escaped string; a number, bracketed when negative; 1 or 0 |

- What fills a hole is not searched for holes.
- A bare `[…]` may be an argument filling a code hole, or the `Return` of a function giving back `c-language axiom`; it takes its language from there. Anywhere else it is refused.
- A hole counts only where no letter, digit, `_` or `-` follows it: `the type-c_eq` and `the held->len` fill nothing. Write `(the held)->len`, or pass the joined name as a term.
- Filled source is never run, so it needs no C toolchain.
- `on` fills only `c-language` source; `cufet` source is filled by `Cite`.

### An axiom as a value

```cufet-fragment
Bind number to measure, given (the c-language number axiom given (the text) job, the text what):
    Return cast job on (what).
Done.
```

- An axiom that names its result may be passed, stored and run. `given (…)` belongs only in that type spelling; a declaration lists its parameters after the name.
- A `c-language axiom` type with no result is complete source. Source still taking parameters, written as a type, is refused.
- An axiom and a function are different types; neither fills a parameter declared as the other.
- `and free it with` runs once per call that acquires the address, however the axiom was reached.
- An axiom prints as `<axiom>`. Source prints as its text.

### Addresses

- An `address` may be held only inside a rabbit's block. Holding one outside any rabbit is refused.
- An address lives in the rabbit open where it was got, and may not be stored where it would outlive it: into a series, a map or a name made in an outer rabbit.
- `the text at <address>` is the only read through one, and it copies into the rabbit's region. A void address reads as void. An address prints as `<address>`.
- There is one address type and no address-of operator.
- `and free it with <name>` names an axiom taking one `address`; it runs on every way out of the block, an exception included. A void result is not freed, and an axiom with no clause is never freed. Nothing checks that the named axiom is the right one.

### Running an axiom

- An axiom sees the C standard library, plus POSIX on Unix and Win32/winsock on Windows, and writes no `#include`. `<termios.h>`, `<poll.h>`, `<sys/socket.h>` and `<sys/wait.h>` are Unix-only; `<windows.h>` and `<winsock2.h>` are Windows-only.
- A library of your own cannot be linked yet.
- Running an axiom needs a C toolchain on both backends. Interpreted, each axiom is compiled once into a shared library, cached by its content. Where there is no toolchain (the playground) the program refuses to run.
- Every axiom a program can run is built before it starts, so one that does not compile means no output at all, either way the program is run.
- C's own global state belongs to one process: the interpreter's, or the compiled program's.

### Cufet source — a `cufet` axiom, placed by `Cite`

```cufet
Pull a book on cufet.
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

- The book is pulled as `Pull a book on cufet.`
- A `cufet` axiom that names its result is a function body: any Cufet type may come back, and it is a value like any function. One that names none is a block of declarations, placed by `Cite`.
- A block holds objects, interfaces, `Define`s and `Bind`s. `Cite <name>.` places them where the `Cite` is: a type belongs to the program wherever that is, and a value is a local there, so one block cited twice makes two separate locals.

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

- A block may use only what belongs to the program (a function, a type, a `permanently` constant, a pulled book) and what it declares itself.
- A block that holds a function may be cited only at the top level or directly inside a `Pull`.
- A block may be cited before its declaration. A block never cited places nothing, and its name cannot be stated, passed or read.
- One name holds one block; a second is refused.
- A runnable `cufet` axiom cannot be cited. `and free it with` on a `cufet` axiom is refused.
- A mistake inside a block is reported at the block's own line, once the block is cited.

#### Holes in cufet source

```cufet
Define object counter with (the number place):
    Bind number to step:
        One's place becomes one's place + 1.
        Return one's place.
    Done.
Done.

Pull a book on cufet.
    Define cufet twice-over, given (the term made, the number function next unto counter, the number extra), as [
        Bind number to made unto counter:
            Cast one's next.
            Return cast one's next + extra.
        Done.
    ].

    Cite twice-over on ("two-steps", step, 0).
    Cite twice-over on ("four-steps", two-steps, 100).
Done.

Define the tally as a new counter { the place 0 }.
State cast tally's four-steps.
State tally's place.
```
```output
104
4
```

| Declared | Means | Filled with |
| --- | --- | --- |
| `the term made` | a name the block makes | a name in quotes |
| `the number function next`, `… unto counter` | a function, or a method of that object, the block calls | its name, bare |
| `the text x`, `the number n`, `the fact f` | a value | a literal |

- `Cite <name> on (…)` fills the holes and places a copy. `on` without `Cite` does not fill cufet source.
- A function hole may say what it gives back as a `Bind` does, and what it takes after that: `the node or failure function next unto parser given (the node)`.
- What fills a hole is written out at the `Cite`; a variable is refused where a literal goes.
- There is no hole for code.
- A term must be declared by the block, with a `Bind` or a `Define`. A hole nothing uses is refused. A value hole is refused if the block declares that name again.
- A folder carries its blocks and `Cite`s: a file may cite a block from its own folder or a folder above. A neighbour's `Cite` places declarations only. A name a `Cite` makes is its folder's, reached from outside as `lib's stamped`. A pulled book carries neither.
- An interface default written outside a block does not find a cited interface: `Bind <type> to <name> unto <cited-interface>` is not found. A conformer's own method works.

---

## 7. Streaming pipes

`|` joins stages into a pipe. Either every stage is a function (a task pipe), or every stage is a `run` (a subprocess pipe). Mixing the two is refused.

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

- A stage is a function with no parameters, or a function value. Any number of stages may be joined: `producer | doubler | consumer.`
- A task pipe is a statement. In expression position it is refused.
- `Output <value>.` sends a value to the next stage. It may be capitalised. Outside a pipe stage it stops the program when it runs.
- `For each <name> from the input:` (or `from input`) takes one value per turn and ends when the stage before has finished. `Stop` and `Skip` work as in any loop. The name may be `item`.
- `input` and `output` are not reserved, and neither can be defined as a variable (§1).
- What a stage reads is the type the stage before it outputs, and its body is checked with that type, at the pipe. A stage used in no pipe has an unchecked consumer loop. One stage may be used with only one input type in a program.
- A stage reached through a function value is not followed: stages after it are not checked.
- Interpreted, each stage runs to its end before the next starts; compiled, the stages run at once, joined by channels. The order of what a linear pipe prints is the same either way.

### Subprocess pipe

```cufet-fragment
run "echo" with arguments ("hello") | run "cat".
```

- Each stage's output is the next one's input; the last one's output is the program's.
- A pipe of `run`s need not be handled: a failure to launch ends the program as an unhandled failure.

### `with arguments`

```cufet-fragment
Try to:
    Define argv as a series of text with ("-l", "/tmp").
    Run "ls" with arguments argv.               ← the whole list
    Run "ls" with arguments ("-l", "/tmp").     ← written out
Done.
In case of failure:
    State "no ls".
Done.
```

- `(` straight after `arguments` opens a written-out list; anything else is the whole list, a `series of text`. `with arguments (argv)` with a series inside is refused.
- `Run X.` as a statement gives the program the terminal and hands back nothing. `run X` as an expression captures the output into a result record. Both can fail to launch, so both must be handled.

---

## 8. Sharp edges

### A body sees functions and constants, not top-level data

```cufet-refused
Define max-retries as 3 permanently.
Define counter as 3.

Bind number to budget:
    Return max-retries * 2.     ← fine: permanently, so it cannot be mutated
    Return counter * 2.         ← refused: ordinary top-level data
Done.
```

- A top-level function, a method, a getter, a setter, an unmaker, an operator overload and a pipe stage see the program's functions and its `permanently` constants, and not its other top-level values. Reading one is refused when the program is checked. So is a name defined nowhere.
- A constant made at the top level of a `Pull` block is the program's, as one at the top of the file is: functions read it, the program reads it after the block's `Done.`, and no other block may define a constant of that name.
- Two constants are the block's own, readable in it and by no function: one whose value is something the block holds (another name for a rabbit's series), and one made `a shadow`.

```cufet-refused
Pull a rabbit.
    Define xs as a series of number with (1, 2).
    Define k as xs permanently.
    Bind number to f:
        Return the number of k.     ← refused: 'k' belongs to the block it's defined in
    Done.
Done.
```

- A `permanently` inside a function body is that body's alone.
- A body may run before the line that gives a constant its value. Reading it then stops the program: `'k' has no value yet on line 3. Line 2 gives it one, and that line hasn't run.`
- A method sees nothing of the block it is written in: a function bound in the function around it is refused there. A function bound in a rabbit's or a book's `Pull` block is the program's, and a method may call it.
- A function with no name captures what is around it, and is not affected.
- What a body cannot see, pass in as a parameter, make `permanently`, or reach by nesting the body inside a function that has it:

```cufet
Define total as 42.

Bind void to run-report, given (the number t):
    Bind void to show:    ← captures 't' from run-report's scope
        State t.
    Done.
    Cast show.
Done.
Cast run-report on (total).
```

- Functions bound one after another inside a body, with no statement between them, see one another and may call each other. A statement between two ends the group; calling across it is refused.

### A `permanently` field

```cufet
Define object user with (the text id permanently, the text name).
```

- `permanently` follows the field it fixes, and fixes only that field.
- `a new user { … }` gives it its value. Every later write is refused: `alice's id becomes …`, `one's id becomes …` in a method, a write through an embed, and a setter named for it.

### `when`

```
Define parsed as raw but void is 0 when shout is true, otherwise 99.
Define sizes as a series of text with ("small" when n is 1, otherwise "big", "fixed").
"one" when count is 1, otherwise "two" when count is 2, otherwise "many"
```

- `<value> when <fact>, otherwise <value>` binds looser than everything, `but void is` and `but on failure` included.
- `, otherwise` is required. In a list, `x when c, y` is an unfinished `when`, not two items.
- It chains to the right.
- Only the chosen side is evaluated, on both backends.
- The two sides may differ in type; the result is their union. `Define the number fee as …` makes a side that is not a number an error.
- The condition must be a `fact`.

### A type before the name in `Define`

```cufet-fragment
Define the text greeting as "hello".      ← greeting is declared text
Define copy as src.                       ← copies src
Define the (number or text) x as 42.      ← x may later hold text
```

- `Define the <type> <name> as <value>.` declares the name's type. A type counts there only when a name follows it.
- Without a type, a name's type is its first value's, for life. This is the only way to give a variable a union type.
- The value must fit the declared type; it widens into it.

### Holes in text — `{...}`

| | |
|---|---|
| A hole holds any expression | `"{count * 2}"`, `"{the length of s}"`, `"{item 2 of parts}"` |
| Holes nest | `"outer {"inner {who}"}"` |
| The value is converted to text | `"{count}"` |
| What can be converted: | text, a number, a fact, bits, a chase, a series of those. Not a map, a record or an object |
| A literal brace is escaped | `\{` and `\}` |
| An empty hole is refused | `"{}"` |

- Holes work anywhere a text does: a `Define`, an argument, a file path.

### `<<...>>` cannot end in `>`

- `<<a>>>` closes at the first `>>`. Write `"a>"`.
- `<` and `>` are otherwise untouched: `a < b` and `a <= b` are comparisons.

### Transformations trail, accessors lead

```
nums sorted            nums sorted by the age        nums in reverse
s trimmed              s in uppercase                s split by ","
score converted to text                              first joined to last

the length of s        the number of s               the first of s
the last of s          the position of x in s        the size of m
```

### `sorted by <name>`

- The name is a field of the element or a function in scope. Both at once is refused; write the function inline: `sorted by a function given (the person value): Return cast age on (value). Done`.
- A variable of that name is not a key and does not collide.
- The article is noise: `sorted by the size-of` is `sorted by size-of`.
- A key function takes exactly the element type, not one it widens into.
- The key is a number or a text.
- A chase takes no key function.
- The key function runs once per element, in order, before any comparison, on both backends.

### Binary `-` needs spaces

- `x-y` is one name. Subtraction is `x - y`. `1-1` is subtraction, since a name cannot start with a digit.
- In a record literal or an argument list, `the offset -1` is a field `offset` holding minus one, and `the row - 1` is a subtraction. A word that cannot begin a value works the same way: `the entry for "a" in ages but void is 0` is an expression.

### A definition with a blank

```cufet-fragment
Define object stack of element with (the series of element items).
Define counts as a new stack of number { the items a series of number }.
```

- `of <blank>` after a type's name declares a blank; more blanks may follow: `object pair of left-thing of right-thing`, used as `a pair of number of text`.
- `a stack` alone is refused; only a filled one is a type.
- Each filling is its own type, sharing no values with another.
- A definition's body is checked once filled. A definition nothing fills is never checked.

### A function's blanks come from its signature

- A type name that names nothing and appears at least twice across a function's parameters and return type is a blank. Once, it is an unknown type, refused.
- A blank must appear in a parameter; one only in the return type is an unknown type.
- A blank means one type per call: `cast pick on (1, "two")` against `given (the element left, the element right)` is refused.
- Blanks match inside: `voidable element`, `series of element`.

### An interface is a parameter type and nothing else

- An interface may be the type of a function's parameter. As the type of a field, an item, a return value or a variable it accepts no conforming value. An interface-typed parameter cannot be given a new value, or passed on to another function taking an interface.
- A default method, `Bind <type> to <name> unto <interface>`, is given to every conforming type. Inside it, `one` is the conforming object. A type's own method of that name wins. Two interfaces giving one type the same default is refused.
- A module that declares an interface hands out only what its interfaces declare (§5). A plain object hands out every method, whatever it declares.
- To hold different conformers together, use a closed union and `Judge` it back. A catalogue whose type is inferred from mixed values is an open union, and passing one where an interface is wanted is refused. Name the element type.

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

### A book in another file

- `Pull a book on ‹name›.` looks for a bundled book, then a module in this file, then `‹name›.cufe` beside the file that pulls it, then `‹name›.cufe` in the project's `books` folder. The nearest wins. A name found nowhere is refused, listing the places looked.
- A project is a directory holding `blueprint.cufe`, found by walking up. Its location is read, not its contents.
- A pulled file is a library: its top level may declare things, `Define` values, and hold `Pull … Done.` blocks. A file that does anything else at its top level is refused at the pull.
- A `Define` whose value does something still runs when the file is pulled.
- Two files answering to one name in one program are refused, naming both. Two pulls reaching the same file are fine. A file pulled by two others is loaded once. A ring of pulls is refused.
- A book's own pulls are looked for beside that book.
- What a pulled file declares beside its module is private to that file.

### `the output`

- `output` is a name already defined, a `writable stream of text` on standard output, as `input` is standard input. `Define output as …` is refused. `Write <text> to the output.` writes to it; reading from it is refused.
- In a pipe stage, `output <value>.` sends a value on instead (§7).
- It is flushed on every write.
- A newline written through it is a line feed. `State` ends its line with the platform's line end, CRLF on Windows.

### Directory namespaces

- In a project, each directory is a namespace named after itself. Its files share one top level. Another directory's member is reached by qualifying it, `terminals's read-key`, with nothing pulled.
- A directory offers what its files declare, and their `permanently` constants. A plain `Define` at a file's top level stays in that file.
- Only the file you run runs; its neighbours give declarations only.
- `blueprint.cufe` is in no namespace.
- When the blueprint draws a tree (`Bind text to tree` returning a written-out text), the drawing decides the namespaces: a folder at the left edge is a namespace, and one indented under another belongs to it. A file sees its own folder's names and those of each folder above it; a name declared in a folder and in one above it is refused. A sibling or lower folder is reached by qualifying it. Siblings may repeat a name. From outside, a namespace offers its own folder's names.
- Refused: tabs mixed with spaces in the drawing, an indent matching no level above, a missing folder, a folder drawn twice, a source folder not drawn, a tree body that is not a written-out text; a top-level name repeated within one directory; qualifying a member the directory does not declare; a declaration or `Define` taking a directory's name; two directories of one name.
- A parameter or a loop variable taking a directory's name is not refused.

### Versions: several functions of one name

```cufet-fragment
Bind number to eval, given (the num-node n), n's value.
Bind number to eval, given (the add-node n), (cast eval on (n's left)) + (cast eval on (n's right)).
```

- Two `Bind`s of one name are versions when parameter types tell them apart. A call picks one by the argument's type, or, for a closed union, by what the value is at run time. A union wider than the versions cover is refused, naming the missing case.
- Every version takes the same number of arguments and gives back the same type.
- When two or more arguments tell versions apart, every combination of their types needs a version; a missing one is refused at the declaration.
- Refused: two versions for one combination; two `Bind`s of one name with nothing to tell them apart, including in two different `Pull` blocks.
- Allowed: a `Bind` inside a body with a top-level function's name (it shadows it); two types each with a method of one name; a method (`unto`) sharing a free function's name.

### `when` on a version

```cufet-fragment
Bind text to kind, given (the number n) when n is 0, "zero".
Bind text to kind, given (the number n), "other".
```

- Versions with one signature are told apart by `when <condition>` after `given (…)`. Exactly one per signature has no condition, and runs when none holds.
- Two versions whose conditions can both hold are refused.
- A condition is built from: equality or inequality against a literal (`n is 0`, `k is not "eof"`), a type test (`n is a num-lit`, `n is not a num-lit`), and `and`, `or`, `xor` over those. Ordering and arithmetic are refused.
- The overlap check can refuse conditions that never overlap: over `(red or green)`, `is not a red` and `is not a green`.

### Named arguments

```cufet-fragment
Cast area on (the width 3, the height 4).
Cast area on (2, the height 5).
```

- `the <parameter> <value>` names an argument. `the width` with no value after it is the variable `width`, and `the width of box` is a field.
- Positional arguments come first; a positional one after a named one is refused.
- The names are the declaration's. A call through a function value held in a parameter or a field cannot name arguments.

### Failures

- `Return a failure "message".` makes a new failure. `Return a failure.` (or `Return the failure.`) hands on the one being handled, inside `In case of failure:`.
- `<expr> or pass the failure off` hands a failure to the caller; so does `Cast f on (x) or pass the failure off.`, which drops the value. The call must be able to fail, the function around it too, and neither may be inside `Try`. Inside `Try to:`, a failing call's value is already unwrapped.
- In `<voidable> but void is <default>`, the default must fit the voidable's type.

### Words that read as statements

- `x = 5.` as a statement is refused, pointing at `becomes` and `Define`. `=` compares.
- `start` and `state` are reserved; `size` is not.

### Operator overloads

- The operand types are an ordered pair, at least one an object type: `number * vec` and `vec * number` are two overloads.
- Only `+`, `-`, `*` and `/` can be overloaded.
- `a`, `an` and `the` cannot name parameters: use `lhs` and `rhs`. See REFERENCE, *Operator overloading*.

### Object literals

- `a new T { the field value, … }` gives fields by name; every field without a default must be given. A field declared without a name (`with (number, the text label)`) is given by position: `{ 5, the label "a" }`.
- A map's loop variable is a `mapping`: `the key of pair`, `the value of pair`.

### Region rules reach calls

A reference value may be stored only where it lives at least as long. That reaches through a call: what a function, a method or a getter gives back is as short-lived as what it could be made from.

```cufet-refused
Bind series of number to smuggle, given (the series of number s):
    Return s.
Done.

Define outer as a series of number.
Pull a rabbit.
    Define inner as a series of number with (1, 2, 3).
    The outer becomes cast smuggle on (inner).   ← refused: inner is shorter-lived
Done.
```

- Where the checker cannot tell (recursion, an unknown function), it takes the deepest argument or receiver. This can refuse a program that is safe; it does not accept one that is not.
- A function nested in another that stores a captured reference parameter of the outer function into anything outside is refused. Reading it is fine, and so is storing a captured local. Pass the value in as a parameter of the nested function instead.
- SOUNDNESS.md has the whole account.

---

## 9. Loops, and the interpreter's scheduler

### Changing a collection while looping over it

- Inside `For each x in s`, inserting into or removing from `s` (a series, a map, a set or a chase) stops the program when it runs, checked before each pass and after the last: `'items' was modified during a for-each loop on line 5 — collect into a separate series, or use a While loop if you need to change it while looping.`
- Setting an item is allowed. An item set ahead of the loop is seen with its new value when the loop reaches it, on both backends. To loop over the values as they were, copy the series first.

### The interpreter's scheduler

Everything below is the interpreter's. Compiled, each task is a thread of its own, and none of it applies. Which order tasks run in is not part of the language.

- `Yield.` runs one queued task to its next stop, then returns to the task that yielded. That task is not put back in the queue.
- Workers sharing one channel do not share its work: the first to be unblocked takes everything (30 and 0, not 15 and 15). Compiled, the work spreads, in no fixed split. A program meant for both should depend only on totals.
- A producer that yields while a later task blocks on another channel can end in `channel deadlock — a task is waiting for delivery but no running or queued tasks will send.`
- Closing a channel wakes every task blocked on it; each gets `void`.
