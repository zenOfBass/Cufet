# Cufet Language Reference

The complete reference for Cufet `0.18.0`. For a quick introduction and taste of
the language, see [README.md](../README.md). For the reasoning behind the
design, see [DESIGN.md](DESIGN.md); for what comes next, see [ROADMAP.md](ROADMAP.md). For reserved words, sharp edges,
and the constraints worth knowing before you hit them, see [GRAMMAR.md](GRAMMAR.md). The books
Cufet ships with — `math`, `collections`, `chance`, and `the c-language` — have their own
reference in [BOOKS.md](BOOKS.md).

Cufet either runs interpreted or compiles to a native binary, and the two share a
front end. **Everything here applies to both** unless it says otherwise; the few
deliberate differences are marked where they arise and summarised under
[Compiling to a native binary](#compiling-to-a-native-binary).

---

## Contents

- [Cufet Language Reference]
  - [Contents]
  - [Part I. Basics](#part-i-basics)
    - [Statements](#statements)
      - [Explicit types](#explicit-types)
    - [Identifiers](#identifiers)
    - [Comments](#comments)
    - [Constants](#constants)
    - [Arithmetic](#arithmetic)
    - [Facts (boolean literals)](#facts-boolean-literals)
    - [Comparisons](#comparisons)
    - [Logic](#logic)
  - [Part II. Control flow](#part-ii-control-flow)
    - [Conditionals](#conditionals)
    - [`Judge` — handling every case](#judge--handling-every-case)
    - [Loops](#loops)
      - [For-each loops](#for-each-loops)
    - [Stashes (`Bury` and `unbury`)](#stashes-bury-and-unbury)
      - [What a burying body cannot do yet](#what-a-burying-body-cannot-do-yet)
      - [A stash is a value](#a-stash-is-a-value)
      - [Where a stash is buried](#where-a-stash-is-buried)
    - [Scope](#scope)
  - [Part III. Data](#part-iii-data)
    - [Text](#text)
    - [Range](#range)
    - [Series (collections)](#series-collections)
    - [Maps](#maps)
    - [Records](#records)
    - [Catalogue and atlas (heterogeneous collections)](#catalogue-and-atlas-heterogeneous-collections)
    - [Bit patterns (`bits`)](#bit-patterns-bits)
      - [Writing one](#writing-one)
      - [Width comes from the digit count](#width-comes-from-the-digit-count)
      - [Gates](#gates)
      - [The left operand decides how the result looks](#the-left-operand-decides-how-the-result-looks)
      - [Arithmetic](#arithmetic-1)
      - [Shifts](#shifts)
      - [Crossing over](#crossing-over)
      - [A free consequence worth knowing](#a-free-consequence-worth-knowing)
      - [Storing one](#storing-one)
  - [Part IV. Objects and functions](#part-iv-objects-and-functions)
    - [Objects](#objects)
      - [Embedding (composition)](#embedding-composition)
      - [Interfaces (polymorphism)](#interfaces-polymorphism)
      - [Methods defined outside the object body (`unto`)](#methods-defined-outside-the-object-body-unto)
      - [Getters and setters](#getters-and-setters)
      - [Named constructors](#named-constructors)
      - [Destructors](#destructors)
      - [Recursive shapes](#recursive-shapes)
    - [Operator overloading](#operator-overloading)
    - [Functions](#functions)
      - [Closures](#closures)
      - [Lambda literals (anonymous functions)](#lambda-literals-anonymous-functions)
    - [Sorting](#sorting)
  - [Part V. The type system](#part-v-the-type-system)
    - [Type system](#type-system)
    - [Voidable values (`void` and `voidable T`)](#voidable-values-void-and-voidable-t)
    - [Union types and narrowing](#union-types-and-narrowing)
      - [`is a <type>` / `is not a <type>`](#is-a-type--is-not-a-type)
      - [In-branch narrowing](#in-branch-narrowing)
    - [Error handling (failures and exceptions)](#error-handling-failures-and-exceptions)
      - [Failure values (`failure T`)](#failure-values-failure-t)
      - [Block form: `Try to`](#block-form-try-to)
  - [Part VI. Input and output](#part-vi-input-and-output)
    - [Input and output](#input-and-output)
      - [Reading from standard input](#reading-from-standard-input)
      - [File I/O](#file-io)
      - [Process execution](#process-execution)
      - [Environment variables](#environment-variables)
      - [The current directory](#the-current-directory)
      - [Directory traversal](#directory-traversal)
  - [Part VII. Systems programming](#part-vii-systems-programming)
    - [Regions (`Pull a rabbit`)](#regions-pull-a-rabbit)
      - [The outward-only rule](#the-outward-only-rule)
      - [Two backends, one rule](#two-backends-one-rule)
      - [When to reach for one](#when-to-reach-for-one)
    - [Concurrency (tasks and channels)](#concurrency-tasks-and-channels)
      - [Tasks](#tasks)
      - [Channels](#channels)
      - [What crosses a boundary, and what a task may touch](#what-crosses-a-boundary-and-what-a-task-may-touch)
      - [Interpreted versus compiled](#interpreted-versus-compiled)
    - [Streaming pipes](#streaming-pipes)
      - [Restrictions](#restrictions)
      - [How stage types are checked](#how-stage-types-are-checked)
    - [Signal handling](#signal-handling)
  - [Part VIII. Modules and books](#part-viii-modules-and-books)
    - [Modules (`Pull`)](#modules-pull)
      - [What a module carries](#what-a-module-carries)
      - [Books and modules](#books-and-modules)
    - [Books, matrices, and foreign source](#books-matrices-and-foreign-source)
  - [Part IX. Compiling](#part-ix-compiling)
    - [Compiling to a native binary](#compiling-to-a-native-binary)
      - [What you get](#what-you-get)
      - [Where the two differ](#where-the-two-differ)
      - [Platform notes](#platform-notes)

---

## Part I. Basics

### Statements

| Syntax | Meaning |
|---|---|
| `State expr.` | Print a value |
| `Define name as expr.` | Declare a variable (error if already defined) |
| `Define the <type> name as expr.` | Declare it with an explicit type |
| `name becomes expr.` | Reassign a variable (error if not declared) |
| `Increment name by expr.` | Add to it in place — `name becomes name + expr` |
| `Decrement name by expr.` | Subtract from it in place |

**`Increment` and `Decrement` name the target once**, which is the whole point —
`X becomes X + 1` repeats the name, and that is where a typo hides:

```cufet-fragment
Increment i by 1.                              ← the i becomes i + 1
Decrement remaining by 1.
Increment one's tally by 3.                    ← a field, from inside a method
Increment total by item at (rr, cc) of board.  ← the amount is any expression
```

The target must be a plain name or a possessive chain, because the desugaring names
it twice. Numeric only: growing a series is `Insert`, and the two never read alike.

Articles (`a`, `an`, `the`) are noise **almost** everywhere — `Define the total as 0.`
and `Define total as 0.` are identical, and so are `given (the number n)` and
`given (number n)`.

★ **The exception: `the` is the named-field marker.** Wherever a field could be
*positional* instead, `the` is what says a name follows — and there it is required,
not decoration:

```cufet
Define object point with (text, number).            ← positional: type only
Define object card  with (the text suit).           ← named: the + type + name

Define p as a new point { "origin", 5 }.            ← positional values
Define c as a new card  { the suit "hearts" }.      ← named values

Define r as a record with ("hatchback", the make "Honda").   ← both at once
```

That last line is why it cannot be dropped: without `the`, `make "Honda"` is
indistinguishable from two positional values. So `{ the suit suit }` is not a
stutter — it is `the` (a name follows), `suit` (the field), `suit` (the variable),
which only reads oddly because the two share a name.

Everywhere a name is the *only* possibility — parameters, `Define`, `In m, entry
for k becomes v` — the article stays pure noise.

#### Explicit types

A type may be written between the article and the name — the same
`the <type> <name>` shape used by parameters and object fields:

```cufet
Define the text name as "Nathan".
Define the number attempts as 0.
```

Without one, a variable's type is whatever its first value was. With one, the
type is what you declared and the value only has to **fit** it — the same single
implicit widening `becomes` and `return` perform. That difference is the whole
point, and it matters most for a union:

```cufet
Define the (number or text) x as 42.
The x becomes "hello".                     ← legal: x holds either
```

`Define x as 42.` would make `x` a number, and the reassignment a type error.
A union-typed variable can only be written this way.

A value that does not fit is an error at the declaration, naming both types.

Keywords are case-insensitive (`Cast`, `cast`, and `CAST` are the same).
Identifiers are not (see [Identifiers](#identifiers)).

---

### Identifiers

- Must start with a lowercase letter (`total`, `my-var`, `x2`).
- Internal dashes allowed: `receipt-total` is one identifier.
- `Total` (uppercase-initial) is a lexer error — uppercase-initial is reserved
  for keywords. (Keywords themselves are case-insensitive, but a non-keyword word
  must start lowercase, so every uppercase-initial word in a program is provably
  a keyword and every lowercase one is a name — roles are parseable by eye.)
- Binary `-` requires surrounding whitespace: `a - b` is subtraction; `a-b` is
  an identifier.
- `a`, `an`, `the` are reserved as noise (articles) and cannot be used as
  identifiers.

### Comments

Cufet has two comment forms, spelled the way C, Rust, Go and JavaScript spell them: `//` to the
end of a line, and `/* ... */` for a block. Everything inside either — including newlines, `.`s,
and any Cufet syntax — is stripped by the lexer before parsing.

```cufet
// this is a comment

Define x as 5.   // inline comment after a statement

/* a longer comment
   spanning multiple
   lines */
Define y as 10.
```

**`//` needs no terminator.** It ends at the newline, or at end of file if it is the last line.

**Block comments nest.** An inner `/*` opens a nested comment, and the outer one ends only at the
`*/` that closes it — so you can comment out a block that already contains comments, which
is the usual reason to reach for a block comment at all:

```
/* disabled while I test something else

Bind number to helper, given (the number n):
    /* double it */
    Return n * 2.
Done.

*/
```

Everything above is commented out, including the inner `/* double it */`. This is the one place
Cufet's comments differ from C's, and it is the difference Rust, Swift and D also make: C's
non-nesting block comment ends at the first `*/`, which breaks exactly the case above.

**They do not interfere with each other.** A `/*` inside a `//` comment does not open a block, and
a `//` inside a block comment is just text. Comment markers inside a string literal are text too —
`State "http://example.com".` prints the whole URL.

**Division is unaffected.** `/` is a single-character token, so `6 / 2` and `6/2` both divide.
Nothing that was previously a valid program has changed meaning.

**Unterminated comment.** A `/*` with no matching `*/` before end of file is a lexer error naming
the line the **outermost** comment opened on — the one you have to go find:

```
/* forgot to close
```
→ `Line N: unterminated comment — expected '*/' to close it.`

---

### Constants

`Define name as value permanently.` — the trailing adverb locks the binding:

```cufet
Define max-retries as 3 permanently.
Define pi as 3.14159 permanently.
Define greeting as "Hello!" permanently.
```

A permanent binding can never be reassigned — `max-retries becomes 4.` is a
static type error that names both the declaration line and the violation.

**A top-level constant is shared** — functions and methods can read it, unlike ordinary top-level
data:

```cufet-refused
Define max-retries as 3 permanently.
Define counter as 3.

Bind number to budget:
    Return max-retries * 2.     ← fine
Done.

Bind number to wrong:
    Return counter * 2.         ← refused: top-level data is not visible
Done.

Define object job with (the number tries):
    Bind fact to exhausted:
        Return one's tries is greater than max-retries.   ← fine: methods see it too
    Done.
Done.
```

Every body that leaves the top-level scope reads them on the same terms — a function, a method, a
getter, a setter, a destructor, an operator overload, a pipe stage.

The difference is mutation, not scope. Functions are kept away from top-level data so data flow
stays explicit and nothing can be changed behind your back — and a permanent binding cannot be
changed at all, so it is safe to share. For anything mutable, pass it in as a parameter.

**Shallow by construction:** `permanently` fixes the *binding*, not the
*contents*. A permanent series or map can still add and remove elements; a
permanent object can still mutate its fields — those operations go through
`Insert`/`Remove`/field-set, not `becomes`, so they are not touched by the
constant rule. Only `becomes` on the name itself is locked — and `Increment`,
which is `becomes` in disguise, is locked with it.

---

### Arithmetic

Standard `+ - * / %` with `()` and conventional precedence. Unary `-` supported.
Uses `decimal` — no floating-point surprises.

`%` is modulo (remainder). Binary `-` requires surrounding whitespace to
distinguish it from a dash inside an identifier: `a - b` is subtraction,
`a-b` is one identifier.

Results print in their minimal form regardless of scale picked up along the
way — `1.5 + 0.5` displays as `2`, not `2.0`.

---

### Facts (boolean literals)

`true` and `false` are **keywords** that produce `fact` values — the boolean type.
They work exactly like number or text literals: anywhere a `fact` is valid.

```
Define flag as true.
Define done as false.
Return true.
Return false.
If result is false, State "failed".
While keep-going is true, repeat: ... Done.
Send true through ch.             ← channel of fact
Define b as (x > 5).              ← comparison also produces a fact
```

`fact` is the type produced by comparisons, logic operators, `contains`, `has a key
for`, `an interrupt is requested`, and `a random guess` — `true`/`false` are simply
the literal forms of that same type.

---

### Comparisons

Both **symbol forms** and **word forms** work in both expression position and
condition position — they are the same operation (compare, produce a `fact`).

**Symbol forms** (`=` `<` `>` `<=` `>=`) — terse, math-style:
```cufet-fragment
State 3 > 1.              → true
State 1 = 1.              → true
If x < 10, State "small".
While count < bound, repeat:
Define big as x > 100.
```

**Word forms** (`is`, `is not`, `is greater than`, `is less than`, `is N or more`,
`is N or less`) — verbose, sentence-style:
```cufet-fragment
If x is 5:
If x is not 3:
If x is greater than 10:
If x is less than 10:
While x is less than bound, repeat:
Define in-range as (x is 5 or more).
```

`=` is equality only — assignment is `becomes`, declaration is `Define ... as`.
Word forms are the **idiomatic, recommended** style for `If`/`While` conditions
because they read like English. Symbol forms are natural in expression position.
Either works anywhere.

---

### Logic

`and`, `or`, and `not` combine conditions. These are always words (no `&&`/`||`/`!`).

```cufet-fragment
If x is greater than 0 and x is less than 10, state "in range".
If x is 0 or x is 100, state "edge".
If not (x is 5), state "not five".
```

Conventional precedence: `not` binds tightest, then `and`, then `or`; all looser
than comparisons. Evaluation short-circuits (`and` skips its right side if the
left is false; `or` skips if the left is true).

---

## Part II. Control flow

### Conditionals

**Inline — comma, one statement, works anywhere:**
```cufet-fragment
If x is 1, state "one".

If x is 1, state "one". Otherwise, state "other".

If x is 1, state "one".
Otherwise if x is 2, state "two".
Otherwise, state "other".
```

**Block — colon, `Done.`-closed, any number of statements:**
```cufet-fragment
If x is 1:
    State "one".
    State "also one".
Done.

If x is 1, state "one".
Otherwise, state "other".
```

Comma after the condition → inline single statement. Colon after the condition →
`Done.`-terminated block.

---

### Conditional values — `when` / `otherwise`

`If` chooses which **statement** runs. `when` chooses which **value** you get, in the middle of
an expression:

```cufet-fragment
Define label as "item" when count is 1, otherwise "items".
State "You have {count converted to text} {label}.".
```

Both halves are required — a `when` always has an `otherwise`.

The reason it exists is immutability. Without it, a value that depends on a condition has to be
declared and then changed:

```cufet-fragment
Define label as "items".
If count is 1, the label becomes "item".
```

That needs `label` to be mutable, so a `permanently` binding could not be conditionally
initialised at all. With `when`, it can:

```cufet-fragment
Define fee as 0 when member is true, otherwise 25 permanently.
```

**Only the chosen side runs.** If the untaken arm calls a function, that call does not happen:

```cufet-fragment
Define picked as 1 when flag is true, otherwise cast expensive on (2).
```

**They chain**, falling through left to right:

```cufet-fragment
Define name as "one" when count is 1,
    otherwise "two" when count is 2,
    otherwise "many".
```

**The two sides may be different types**, in which case the result is a union — the same thing
`a catalogue with (1, "two")` does. If you would rather a mismatch be an error, say the type:

```cufet-fragment
Define the number fee as 0 when member is true, otherwise 25.
```

`when` binds looser than everything else, so it always picks between two whole values — including
across `but void is`:

```cufet-fragment
Define parsed as raw but void is 0 when shout is true, otherwise 99.
```

A conditional is legal inside an argument or element list, but it reads better named first:

```cufet-fragment
Define label as "item" when count is 1, otherwise "items".
Cast show on (label).
```

---

### `Judge` — handling every case

`Judge` dispatches on what a value **is**. The subject and verb are stated once in
the header, and each arm completes the sentence:

```cufet
Define the (number or text or fact) thing as 42.

Judge thing, where it is:
    A number, state "a number".
    A text, state "some text".
    A fact, state "a fact".
Done.
```
```output
a number
```

**Coverage is total — by proof or by default.** A judgement over a **closed union**
whose arms cover every case needs no `Otherwise`; the checker has proved nothing is
left. Miss one and it refuses:

```cufet-fragment
Judge thing, where it is:
    A number, state "a number".
    A text, state "some text".
Done.
```
```
That doesn't work: this judgement does not cover fact.
```

For anything else, `Otherwise` is required. Either way, control can never fall off
the end of a `Judge`. It is the same discipline `voidable` applies to absence:
handle it, or say what happens instead.

```cufet-fragment
Judge thing, where it is:
    A number, state "a number".
    Otherwise, state "not a number".
Done.
```

**Arms** take the comma form for one statement, or a colon and `Done.` for a block —
the same rule `If` follows. `or` groups cases:

```cufet-fragment
Judge thing, where it is:
    A number or a fact, state "not text".
    Otherwise, state "text".
Done.
```

**`it` is the subject, narrowed.** Inside an arm, `it` is that arm's type, so
type-specific operations are legal there:

```cufet-fragment
Judge thing, where it is:
    A text, state the length of it.        ← it is text here
    Otherwise, state "not text".
Done.
```

A **grouped** arm narrows only as far as the group: an arm covering two cases cannot
know which one arrived, so `it` is the sub-union it names and must be tested again
before type-specific use.

> **The subject may be an expression.** Narrowing is variable-level, so `If` cannot
> narrow a value produced by an expression — you have to name it first. `Judge` names
> it for you, as `it`:
>
> ```
> Judge item 1 of words, where it is:
>     A text, state the length of it.
>     Otherwise, state "not text".
> Done.
> ```

- Nothing may follow the `Otherwise` arm; an arm after the default could never run.
- A judgement whose arms all return counts as returning, so it satisfies the
  every-path-returns rule on its own.
- There is no no-op statement, so an arm that means "ignore this case" still has to
  say something.

---

### Loops

```cufet-fragment
While x is less than 10, repeat:
    The x becomes x + 1.
Done.

Repeat:
    The x becomes x + 1.
until x is 10 or more.
```

`Stop.` breaks the innermost loop. `Skip.` continues to the next iteration. Both
are parse errors outside a loop.

#### For-each loops

```cufet-fragment
For each score in scores, repeat:
    State the score.
Done.
```

```cufet-fragment
For each in scores, repeat:
    State it.
Done.
```

The named form binds the current element to a new name. The bare-`it` form binds
it to `it` — the innermost loop wins when nested. Both restore the previous
binding when the loop exits.

Mutating the series being iterated is a runtime error. Use `While` with an index
if you need to change the series as you go.

`Stop.` and `Skip.` work the same as in `While` loops.

---

### Stashes (`Bury` and `unbury`)

A function can stop in the middle of what it is doing, hand one value out, and
carry on from that exact line when someone asks for the next one.

Burying is memory work, and a **rabbit** is the agent that does memory work — so
you tell one to do it. `Have <rabbit> bury <value>.` is the pause; `unbury` is the
wake-up. A burying function takes the rabbit as a parameter, and the caller hands
one over:

```cufet
Bind number to counting-up, given (the rabbit helper, the number first-value):
    Define next as first-value.
    Repeat:
        Have helper bury next.
        The next becomes next + 1.
    Until false.
Done.

Pull a rabbit as hopper.
    Define counter as cast counting-up on (hopper, 3).
    State unbury counter.       // 3
    State unbury counter.       // 4
    State unbury counter.       // 5
Done.
```

⚠ There is no bare `Bury x.` A rabbit is always the one doing it, which is what
puts the ownership of the buried state somewhere you can see rather than leaving
it ambient.

**Nothing marks the declaration.** A function is stash-producing because its body
*contains* a `bury` — the same way a body containing `return a failure` makes a
function fallible. `cast` still means invoke; what changes is the result type.
`Bind number to counting-up` therefore hands back a `stash of number`, and its
declared type says what it **buries**, not what it returns. A burying function
has no `Return`.

`unbury <stash>` gives back a `voidable T`. When the function reaches its end
there is nothing left to bury, so the answer is `void` — and stays `void` however
often you ask.

```cufet-fragment
Define found as cast long-words-in on (hopper, a series with ("a", "rabbit")).
State unbury found.   // "rabbit"
State unbury found.   // void — spent, and spent for good
```

**`For each` takes them all.** Asking until the answer is `void` is what draining
a stash always means, so the loop says it:

```cufet-fragment
Define found as cast long-words-in on (hopper, a series with ("a", "rabbit")).
For each word in found, repeat:
    State word.
Done.
```

It is the same loop a series takes. `Stop` ends it, `Skip` moves on to the next
value, and `word` holds a plain `text` rather than a `voidable text` — reaching
the body is itself the proof that there was one. Over an endless stash the loop
is endless too, so `Stop` is what ends it:

```cufet-fragment
Define counter as cast counting-up on (hopper, 3).
For each value in counter, repeat:
    If value is greater than 5:
        Stop.
    Done.
    State value.
Done.
```

**A stash is not a collection.** A series *has* its items; a stash *produces*
them, one resumption at a time. You cannot count one, index one, or read one
twice. That is exactly what lets `counting-up` above be endless without being a
mistake — nothing in it says how many, and the loop only runs when somebody asks.

Every cast makes a separate stash with its own place to stand:

```cufet-fragment
Define one-counter   as cast counting-up on (hopper, 1).
Define other-counter as cast counting-up on (hopper, 100).
State unbury one-counter.     // 1
State unbury other-counter.   // 100
State unbury one-counter.     // 2
```

`If`, `While`, `Repeat until`, `For each` over a series or a stash, `Stop` and
`Skip` all work inside a burying body, and every local survives a resumption — a
loop counter, a for-each's place in its series, a series being built up item by
item. A `For each` over a stash inside a burying body is **delegation**: one
stash consumed while another is produced.

#### What a burying body cannot do yet

Each of these is refused when the program is checked, so both backends refuse the
same programs.

| Shape | Why |
| --- | --- |
| `Bury` inside the `Otherwise` of a judgement on a **non-union** subject | The leftover cases have to be named to resume into them, and only a closed union says what they are. |
| A nested `Judge` inside a burying body | The inner one rebinds `it` at a narrower type, and one name holds one type here. Bind the inner subject to a name of its own. |
| `Bury` inside `Try to` or a rabbit block | A handler and a region are context a resumption cannot restore. |
| `Bury` inside `For each` over a map | Resuming means counting back to where the loop was, and a map's entries have no position to count to. Loop over a series. |
| `Define a shadow` anywhere in the body | The body is flattened into one set of state, so a shadow would land on the name it was written to hide. |
| One name at two types in the body | Sibling blocks become one place to store it, and one place holds one type. |

#### A stash is a value

A `stash of T` goes wherever a value goes: a local, a parameter, an element of a
series. It is one thing you can hold, hand over, and keep a collection of.

```cufet-fragment
Bind void to take-three, given (the stash of number source, the text label):
    Define taken as 0.
    While taken is less than 3, repeat:
        State label joined to ": " joined to ((unbury source but void is 0) converted to text).
        The taken becomes taken + 1.
    Done.
Done.

Define counter as cast counting-up on (hopper, 7).
Cast take-three on (counter, "seven").
```

A series of them works, and each keeps its own place:

```cufet-fragment
Define many as a series of stash of number.
Insert (cast counting-up on (hopper, 1))   into many.
Insert (cast counting-up on (hopper, 10))  into many.
For each one-stash in many, repeat: State unbury one-stash. Done.   // 1, 10
For each one-stash in many, repeat: State unbury one-stash. Done.   // 2, 11
```

That is also what lets one stash **delegate** to another — hold it and pass its
values along:

```
Bind number to squares-and-cubes, given (the rabbit helper, the number upto):
    Define inner as cast squares on (helper, upto).
    For each value in inner, repeat:
        Have helper bury value.
    Done.
    ...
Done.
```

**A bury inside a type test keeps its narrowing:**

```cufet-fragment
Bind text to texts-only, given (the series of (number or text) things):
    For each thing in things, repeat:
        If thing is a text:
            Have helper bury thing.        // `thing` is a text here, as it would be anywhere else
        Done.
    Done.
Done.
```

The arm's condition is carried into the resumed block and re-tested there. That is
not a real branch — every local is restored before it runs, so the test gives the
answer it gave the first time; it exists so the type is known again.

**A judgement works the same way, when each arm names one type:**

```cufet
Bind number to sizes, given (the rabbit helper, the series of (number or text) items):
    For each thing in items, repeat:
        Judge thing, where it is:
            A number, have helper bury it + 100.
            A text, have helper bury the length of it.
        Done.
    Done.
Done.
```

`it` is kept the way any other local is kept, so the subject is evaluated once and
restored on each resumption rather than worked out again. A **grouped** arm works
the same way — it states itself as `it is a number or it is a fact` — and so does
an `Otherwise`, which names whichever cases the arms left.

A stash can also be an object **field**:

```cufet
Define object ticker with (the stash of number source, the text name):
    Bind void to report:
        Define held as one's source.
        State one's name joined to ": " joined to ((unbury held but void is 0) converted to text).
    Done.
Done.
```

#### A method can bury

A method buries on exactly the same terms as a function: it takes a rabbit, its
declared type is what it *buries*, and it has no `Return`. What it adds is
`one` — the body reads its own object's fields, so the stash starts from
whatever that instance holds:

```cufet
Define object ticker with (the number first-beat):
    Bind number to ticks, given (the rabbit helper):
        Define next as one's first-beat.
        Repeat:
            Have helper bury next.
            The next becomes next + 1.
        Until false.
    Done.
Done.

Pull a rabbit as hopper.
    Define low  as a new ticker { the first-beat 1 }.
    Define high as a new ticker { the first-beat 100 }.
    Define low-beats  as cast ticks on (low, hopper).
    Define high-beats as cast ticks on (high, hopper).
    State unbury low-beats.     // 1
    State unbury high-beats.    // 100
    State unbury low-beats.     // 2
Done.
```

**The state belongs to the instance, not to the type.** Each cast makes its own
stash over its own receiver, so two tickers keep two places and neither knows
about the other — the same rule that already held for two casts of one function.

A method declared outside its object body with `unto` buries the same way:

```cufet-fragment
Bind number to every-other unto ticker, given (the rabbit helper):
    Define next as one's first-beat.
    Repeat:
        Have helper bury next.
        The next becomes next + 2.
    Until false.
Done.
```

Two types may each have a burying method of the same name, and one of them may
not bury at all — a method belongs to its type, and so does the answer to
"does this bury".

#### Where a stash is buried

**A stash lives in the region whose rabbit buried it, and dies with that region.**
The rabbit is not decoration: it is the agent doing the work, and the ground it
stands on is the ground the buried state sits in. That is why a burying function
takes one — the ownership arrives with the job.

A stash is usable for as long as you stay in the burrow:

```cufet-fragment
Pull a rabbit as hopper.
    Define counter as cast counting-up on (hopper, 1).
    State unbury counter.                  // 1
    Cast take-two on (counter, "passed").  // handed inward — fine

    Define many as a series of stash of number.
    Insert (cast counting-up on (hopper, 100)) into many.
    For each one-stash in many, repeat: State unbury one-stash. Done.
Done.
```

⚠ **What you bury in a burrow does not come out of it.** Once the block ends the
ground is gone, and so is everything buried in it — carrying the stash out and
unburying it later is refused, the same way any closure over rabbit-scoped state
is. That is not a restriction bolted onto stashes; it is what burying somewhere
*means*.

The name is Turing's, and so is the lesson. He converted his savings to silver,
buried it in the woods near Bletchley, and wrote down enciphered directions —
then never found it again. The note travelled; the silver did not. A stash is the
same shape: the value you hold is the note, and what it points at stays in the
ground you buried it in.

---

### Scope

Every `Done.`-bounded block — an `If` arm, a `While` body, a `For each` body, a
`Repeat...until` body, a function body — introduces a **lexical scope**. Names
declared inside a block do not exist outside it.

**Inner blocks can freely read and modify outer variables:**
```cufet
Define x as 10.
If x is greater than 5, x becomes 20.         ← modifies the outer x
State x.                                      → 20
```

**Inner declarations are local — they do not leak out:**
```cufet-refused
Define x as 10.
If x is greater than 5, define y as 99.       ← y lives only inside this block
State y.                                      ← error: y isn't defined here
```

**Shadowing an outer name via `Define` is a static error by default:**
```cufet-refused
Define x as 10.
If x is greater than 5, define x as 99.       ← TypeException: x already exists in an enclosing scope
```

**Deliberate shadowing requires the `shadow` keyword:**
```cufet
Define x as 10.
If x is greater than 5:
    Define a shadow x as 99.    ← explicit opt-in; shadow x exists only inside this block
    State x.                    → 99
Done.
State x.                        → 10  (outer x is unchanged)
```

Using `a shadow` when no outer name exists is also a static error — the keyword
asserts that something is being deliberately overridden.

**For-each iterators are block-local automatically**, even if the name matches an
outer variable:
```cufet
Define n as 7.
For each n in range 1 to 3, repeat:
    State n.              ← 1, 2, 3  (the iterator)
Done.
State n.                  → 7  (outer n is restored)
```

**`Try` handler bindings** (`the failure`, `the exception`) are also block-local
to their respective handler bodies.

---

## Part III. Data

### Text

`text` values are joined, measured, and built from other values with explicit
constructs. `+` is **not** overloaded for concatenation — joining has its own
word, and converting a non-text value to text is always explicit (no hidden
coercion).

**Joining** — `joined to`, left-associative, chains:
```cufet-fragment
Define greeting as "hello" joined to " world".          → "hello world"
Define full-name as first joined to " " joined to last.
```
Both sides must be `text`. Joining a non-text value directly is a static type
error — convert it first.

**Converting to text** — `converted to text`, a postfix construct:
```cufet-fragment
State "Player: " joined to score converted to text.     → "Player: 95"
Define label as score converted to text.                → "95"
```
Works on numbers, facts and bits (all total — every one of them has a text form).
It binds tighter than `joined to`, so `"x: " joined to n converted to text`
reads as `"x: " joined to (n converted to text)`.

`converted to` binds to the **result** of a named access, not to its target — `the
value of person converted to text` converts the value, as you would expect.

**Interpolation** — `{...}` inside a text literal, which is usually what you want instead of a
chain of `joined to`:

```cufet
Define who as "world".
Define count as 3.
State "hello {who}, {count} times".
```
```output
hello world, 3 times
```

A hole holds **any expression**, and its value is converted to text for you — so
`{count}` needs no `converted to text`:

```cufet-fragment
Define pat as 0x0F.
Define parts as a series of text with ("a", "b").
State "arithmetic: {count * 2 + 1}".
State "a call: {the length of who}".
State "an element: {item 2 of parts}".
State "a pattern: {pat}".
State "even a nested string: {"inner {who}"}".
```
```
arithmetic: 7
a call: 5
an element: b
a pattern: 0x0F
even a nested string: inner world
```

★ **A hole takes what `converted to text` takes** — text, number, fact, bits — and nothing else.
That is narrower than `State`, which prints anything:

```cufet-refused
State parts.                    ← fine: (a, b)
State "the parts: {parts}".     ← TYPE ERROR: 'converted to text' doesn't work on series of text
```

Write `\{` and `\}` for a literal brace. An empty hole is a parse error rather than an empty
string, because it is always a mistake:

```
State "literal braces: \{not a hole\}".     → literal braces: {not a hole}
State "nothing here: {}".                   ← PARSE ERROR: empty interpolation
```

Interpolation is part of the text literal, so it works **anywhere a text expression does** — not
just in `State`:

```cufet-fragment
Define greeting as "hello {who}".
With the file "logs/{who}.txt" open for writing as log:
    Write greeting to log.
Done.
```

**Verbatim text** — `<<...>>`, where **nothing** is interpreted:

```cufet
State <<C:\Users\me>>.
State <<{"name": "x"}>>.
State <<^\d{3}-\d{4}$>>.
```
```output
C:\Users\me
{"name": "x"}
^\d{3}-\d{4}$
```

`"` and `{` are the two characters a quoted literal cannot hold plainly, and they are exactly
what JSON, regular expressions, Windows paths and Cufet samples inside documentation are made
of. Inside `<<...>>` both are ordinary characters: there are no escape sequences, so a lone `\`
is a backslash and `\q` is two characters rather than an error, and there are no interpolation
holes, so `{x}` is three characters.

The two forms produce the same kind of value. A verbatim literal is a **spelling**, not a type —
everything in this section works on one:

```cufet
Define pattern as <<\d+>>.
State the length of pattern.               → 3
State pattern joined to <<$>>.             → \d+$
If pattern contains <<\d>>, state "yes".   → yes
```

**Nesting** is depth-counted over `<<` and `>>`, the way block comments count `/*` and `*/` — so
text that already contains the delimiters can still be wrapped:

```
State <<a <<b>> c>>.        → a <<b>> c
```

It may run across lines, and the line breaks are part of the text:

```cufet
Define note as <<first line
second line>>.
```

★ **A line break in a literal is one `\n`**, whatever the file is stored as. A CRLF source does
not put a `\r` into the text, so `the length of note` is 22 on every platform and a checkout
that converts line endings cannot change what a program means. This holds for `"..."` too; it
matters most here, because a multi-line verbatim literal is the ordinary way to write one and
there is no escape to reach for instead. (A *lone* `\r` is not a line break, so one you write
deliberately is kept.)

★ **There is no interpolation here** — that is the trade for total literalness. Join instead,
which is what a hole is doing anyway:

```cufet
Define name as "world".
State <<hello, >> joined to name.          → hello, world
```

> ⚠ **Text ending in `>` needs the quoted form.** `<<a>>>` closes at the first `>>` and leaves a
> stray `>` behind. Write `"a>"`. This is the one thing the verbatim form cannot spell.

**Length** — `the length of`, the character count:
```cufet-fragment
State the length of "hello".          → 5
Define n as the length of greeting.
```

> **What a character is.** Every position and count in this section — `the length of`,
> `the characters from`, `the first`/`last N characters`, and the position `the position of`
> returns — is measured in **Unicode code points**, identically on both backends.
>
> ```
> State the length of "héllo".         → 5   (six bytes)
> State the length of "👍".            → 1   (four bytes)
> State the characters from 2 to 2 of "héllo".   → "é"
> ```
>
> A code point is not always what a reader would call one character. `é` can be written as the
> single code point U+00E9 or as `e` followed by a combining acute, and the second counts as
> **2**. Counting what the eye sees means grapheme clusters, which need the Unicode segmentation
> tables — a dependency Cufet will not carry into the C it emits. Code points are exact,
> implementable the same way on both sides, and stated here rather than left to be discovered.

**Converting text to number** — `converted to number`, the inverse of
`converted to text`:
```cufet
Define n as "42" converted to number.
If n is not void, state n.                                 → 42
Otherwise, state "not a number".

Define m as ("abc" converted to number but void is 0).     → 0
```
Parsing can fail (`"hello"` isn't a number), so the result is **always a
`voidable number`** — even for an obviously valid literal — and must be
handled like any other voidable (see
[Voidable values](#voidable-values-void-and-voidable-t)). A text value
converts successfully iff, after trimming surrounding whitespace, it looks
like a Cufet number literal: digits, an optional leading `-`, and an optional
decimal point followed by more digits. Anything else — empty text, trailing
garbage, multiple decimal points — produces `void`.

**Splitting** — `split by`, into a `series of text`:
```cufet
Define parts as "a,b,c" split by ",".          → "a", "b", "c"
For each part in "alice:bob:carol" split by ":", repeat:
    State part.
Done.
```
The delimiter not being found yields a single-element series holding the
whole text. Consecutive, leading, and trailing delimiters produce empty
strings — `"a,,b" split by ","` is `"a", "", "b"`; nothing is collapsed or
trimmed automatically. An empty delimiter (`split by ""`) is a static error.

**Contains** — `contains`, a boolean substring test:
```cufet
If "hello" contains "ell", state "yes".          → yes
```

**Finding a position** — `the position of <substring> in <text>`, **1-based**:
```cufet
Define p as the position of "ell" in "hello".    → 2
Define q as the position of "z" in "hello".      → void
```
Returns the position of the first occurrence, or `void` if the substring
isn't present — a `voidable number`, handled like any other voidable. There's
no `-1` sentinel.

**Substring** — four forms, all **1-based and inclusive**, always returning
plain `text` (never voidable — out-of-range inputs clamp rather than fail):
```cufet
State the characters from 2 to 4 of "hello".         → "ell"
State the first 3 characters of "hello".             → "hel"
State the last 3 characters of "hello".              → "llo"
State the characters from 3 to the end of "hello".   → "llo"
```
- An out-of-range-high end **clamps** to what's there: `the characters from 2
  to 99 of "hi"` is `"i"`; `the first 10 characters of "hi"` is `"hi"`.
- A backwards range (end before start) yields `""`: `the characters from 5 to
  2 of "hello"` is `""`.
- `the first 0` / `the last 0` characters is `""`.
- A character position of `0` or negative is a **mistake, not a clamp case**
  — it's a static error when the position is a literal, and a runtime error
  otherwise: `the characters from 0 to 3 of "hello"` doesn't run.
- `the first of <series>` / `the last of <series>` (series ordinal access,
  see [Series](#series-collections)) are unaffected — the count-and-`characters`
  shape is what distinguishes the substring forms from plain ordinal access.

**Replacing** — `replace <old> with <new> in <text>`, all occurrences:
```cufet
Define s as replace "a" with "X" in "banana".         → "bXnXnX"
Define deleted as replace "x" with "" in "axbx".       → "ab"
```
An empty replacement is deletion (allowed). An empty target is a static error
(when literal — runtime otherwise) — replacing "nothing" is meaningless, the
same reasoning as `split by`'s empty-delimiter error. If `<old>` isn't found,
the text comes back unchanged.

**Case** — `<text> in uppercase` / `<text> in lowercase`:
```cufet
State "Hello" in uppercase.        → "HELLO"
State "Hello" in lowercase.        → "hello"
```
Uses default (invariant, culture-independent) case rules — not locale-sensitive.
Only upper/lower this slice; title-case and capitalize-first are deferred.

**Full Unicode, identically on both backends.** `"héllo" in uppercase` is `HÉLLO`
whether you run it or build it: the two read one shared case table rather than
casing text twice, so they cannot drift apart.

```cufet
State "héllo" in uppercase.        → "HÉLLO"
State "ΑΣΠΙΔΑ" in lowercase.       → "ασπιδα"
State "МОСКВА" in lowercase.       → "москва"
```

> **Note:** this is *simple* case mapping, so one character always becomes exactly
> one character. `ß` stays `ß` rather than becoming `SS`, the `ﬁ` ligature stays
> whole, and the Turkish pair `ı`/`İ` is left alone rather than mapped to a side —
> invariant rules do not pick a locale, and picking one silently would be worse
> than leaving them. Casing never changes a text's length in characters.

> **Note:** `in` is also used to lead the map-set statement (`In ages, the
> entry for "x" becomes ...`) and inside `the entry for K in M` / `the
> position of S in T`. These don't collide: `in uppercase`/`in lowercase` is
> only recognized when `in` is immediately followed by `uppercase` or
> `lowercase` — any other use of `in` is left alone for its own construct.

**Trimming** — `<text> trimmed`, strips whitespace from both ends:
```cufet
State " hello " trimmed.           → "hello"
```
Standard whitespace (spaces, tabs, newlines). Leading-only / trailing-only
trim are deferred. All three operations chain naturally with each other and
with the rest of the text toolkit: `raw trimmed in uppercase`.

---

### Range

`range <start> to <end>` produces a materialized `series of number` — sugar so
you don't build a numeric span by hand:
```cufet
For each n in range 1 to 100, repeat:
    State n.
Done.

Define hundred as range 1 to 100.        ← also valid anywhere a series goes
```

- `start` and `end` are number expressions (`range 1 to n`, `range x to y`).
- The optional article reads naturally: `range 1 to 100` or `the range 1 to 100`.
- **Inclusive of both ends:** `range 1 to 100` is `1, 2, ..., 100`.
- **Counts down when start > end:** `range 100 to 1` is `100, 99, ..., 1`.
- It produces an ordinary `series of number` — all series operations apply.

A range plus `for each` covers everything a C-style counter loop would: there is
no separate index-loop construct, because iterating `range 1 to 100` is the same
thing, read more plainly.

**Stepping** — `counting by <step>` is an optional suffix that changes the
increment from the default of 1:
```cufet
For each n in range 1 to 10 counting by 2, repeat:
    State n.
Done.                                       → 1, 3, 5, 7, 9

Define halves as range 1 to 2 counting by 0.5.    → 1, 1.5, 2
```
- `step` is always a **positive magnitude** — direction still comes from
  start-vs-end, so `range 10 to 1 counting by 2` descends: `10, 8, 6, 4, 2`.
- The end is included only if the step lands on it exactly; otherwise the
  range stops at the last value still within bounds (`range 1 to 10 counting
  by 2` is `1, 3, 5, 7, 9` — `10` is skipped).
- Decimal steps are allowed.
- `counting by 0` or a negative step is an error — caught statically when the
  step is a literal, at runtime otherwise.

---

### Series (collections)

Ordered, homogeneous collections.

**Literals:**
```cufet-fragment
Define scores as a series with (90, 85, 70).
Define tags   as a series of text with ("sedan", "coupe").
Define ops    as a series of number function given (the number) with (double, triple).
```

The element type is inferred from the elements, or declared explicitly after
`of`. Empty series require an explicit annotation:
```cufet
Define log    as a series of text.
Define counts as a series of numbers.
Define fleet  as a series of records like (the text make, the number year).
```

**Access:**
```cufet-fragment
State the first of scores.
State the third of scores.
State the last of scores.
State item 2 of scores.
State item n of scores.        ← n is any expression
```

**Length:**
```cufet-fragment
State the number of scores.
```

**Mutation:**
```cufet-fragment
Insert 100 into scores.                       ← append
Insert 100 into the start of scores.          ← prepend
Insert 100 after the second item of scores.   ← insert after position
Insert 100 after item n of scores.

Remove the first item from scores.         ← by position
Remove item n from scores.
Remove 85 from scores.                     ← by value (first occurrence)
```

**Element assignment:**
```cufet-fragment
The first of scores becomes 100.
The item n of scores becomes 100.
The last of scores becomes 100.
```

Out-of-bounds access or assignment produces a readable runtime error.

Series are **reference-typed** — assigning a series to a new name shares it
(in contrast to records and objects, which copy). See
[Type system](#type-system).

---

### Maps

Typed key→value collections. Keys are one type, values are one type
(homogeneous, like series).

**Type:** `a map from text to number` — text keys, number values.

**What may be a key: a scalar, or a record of keys.** A number, text, a fact, a bit pattern, or a
record whose every field is one of those — nested as deep as you like:

```cufet
Define grid as a map from record like (number, number) to text.
In grid, the entry for a record with (2, 3) becomes "treasure".
State the entry for a record with (2, 3) in grid.               ← "treasure"
```

**A record SHAPE is written `record like (…)`** wherever it stands on its own — as a map key here,
and as a series element in `a series of records like (number, number)`. The `with` form is the one
that carries a name, and the name sits between the word and the shape: `given (the record spot
with (number, number))` declares a parameter called `spot`.

**A wrapper is a key when what it holds is** — `a map from voidable number to text`, or
`a map from (number or text) to text` for a table that files `7` and `"seven"` alike. An *open*
union is refused, because it can hold anything ever widened into it.

A record key is compared by its CONTENTS, so a second record holding the same values finds the
same entry. Named fields compare regardless of the order they were written, exactly as they do
under `is`, and a bit pattern compares on its value alone — `0xA` finds what `0b1010` stored.

A series, a map, an object or a matrix cannot be a key, and a record holding one cannot either.
The reason is that they can be **changed** after they are used as a key: alter the key and the
entry it was stored under can never be found again. A record is safe for the mirrored reason —
it is deep-copied when bound, so the map holds a key nobody else can reach in and alter.

**Construction:**
```cufet-fragment
Define ages as a map with ("alice" : 30, "bob" : 25).           ← populated, inferred types
Define ages as a map from text to number with ("alice" : 30).   ← populated, typed
Define ages as a map from text to number with ().               ← empty, typed
Define ages as a map from text to number.                       ← empty, typed (same thing)
```

**`with ()` is optional once the types are given** — `a map from text to number.` is an empty
typed map, matching `a series of number.`, `a catalogue of (number or text).` and
`an atlas from text to (number or text).`, which all read the same way. The clause is still
required without the types, since `a map.` has neither an annotation nor entries to infer from.

**Lookup** — returns `voidable <value-type>` (the key might be absent):
```cufet-fragment
Define alice-age as the entry for "alice" in ages.        ← a voidable number
Define alice-age as from ages, the entry for "alice".     ← the same, map first
Define alice-age as from the map ages, the entry for "alice".   ← explicit form
```

The map-first form exists because **writing** a map has only that order — `In ages, the entry
for "alice" becomes 30` — so without it, reading and writing the same entry would have to be
said in opposite orders.

**Set** — reuses `becomes`:
```cufet-fragment
In ages, the entry for "alice" becomes 30.
```

**Presence:**
```
If ages has a key for "alice", ...        ← is the key present?
If ages has an entry for "alice", ...     ← is the value present (not void)?
```
For an ordinary (non-voidable-valued) map these two questions always agree —
a present key always has a real value. They only **diverge** for a
voidable-valued map (below): a key can be present with its value explicitly
`void`, in which case `has a key` is true but `has an entry` is false.

**Voidable values** — a map's value type can itself be `voidable V`:
```cufet
Define ages as a map from text to voidable number.
In ages, the entry for "alice" becomes 30.
In ages, the entry for "bob" becomes void.        ← present key, void value
```
A lookup always returns a **flat** `voidable V` — never `voidable voidable V`,
even when the map's value type is already voidable. The nesting never surfaces.
This means a plain lookup can't tell "key absent" apart from "key present, but
its value is void" — both produce `void`. Distinguish them with `has a key`
first, then look up:
```cufet-fragment
If ages has a key for "bob":
    Define v as the entry for "bob" in ages.      ← void here means the VALUE is void
    If v is not void, state v.
    Otherwise, state "present, but void".
Done.
Otherwise, state "no such key".
```

**Remove and size:**
```cufet-fragment
Remove "alice" from ages.
State the size of ages.
```

**Iterate** — each element is a `mapping` (a key/value pair):
```cufet-fragment
For each mapping in ages, repeat:
    State the key of mapping.
    State the value of mapping.
Done.
```

Maps are **reference-typed** (like series).

**The canonical lookup pattern** — name the lookup, check it, use it:
```cufet-fragment
Define alice-age as the entry for "alice" in ages.
If alice-age is not void, state alice-age.
Otherwise, state "Sorry, no entry.".
```

---

### Records

Anonymous, structural data — a bundle of named and/or positional fields.

**Construction:**
```cufet
Define car as a record with ("hatchback", the make "Honda", the year 2021).
```

Positional fields come first; named fields (introduced with `the`) come after.
Mixed order is a parse error.

**Access:**
```cufet-fragment
State the first of car.             ← positional: "hatchback"
State the make of car.              ← named: "Honda"
State the make of the spare of car. ← chained / nested access
```

**Mutation:**
```cufet-fragment
The make of car becomes "Toyota".         ← named field
The first of car becomes "coupe".         ← positional ordinal
The item n of car becomes "coupe".        ← positional parametric
```

Assigning the wrong type to a field is a static type error.

**Value semantics** — records copy on assignment; assigning a record to a new
name gives an independent copy:
```cufet-fragment
Define truck as car.
The make of truck becomes "Toyota".
State the make of car.              → Honda    (unchanged)
```

**Records in function annotations:**
```cufet
Bind text to make-of, given (the record vehicle with (text, the text make)):
    Return the make of vehicle.
Done.
```

**Series of records:**
```cufet
Define fleet as a series with (
    A record with (the make "Honda",  the year 2021),
    A record with (the make "Toyota", the year 2019)).

Define inventory as a series of records like (the text make, the number year).
Insert a record with (the make "Ford", the year 2022) into inventory.
```

A populated series infers its shape from the elements; an empty one declares it
with `like (...)`. Either way, `add` enforces structural matching.

**Equality:**
```cufet-fragment
If rec1 is rec2, state "same".
If rec1 is not rec2, state "different".
```
Two records are equal iff all fields are equal by value — named fields compared
by name (order-insensitive), positional fields by position, recursively. Records
of different shapes can't be compared: a compile-time type error. Series fields
compare element-wise by value.

---

### Catalogue and atlas (heterogeneous collections)

**Catalogue** — a series whose element type is a union: a heterogeneous ordered
collection.

**Closed** (declared element type):
```cufet-fragment
Define items as a catalogue of (number or text) with (42, "hello").
Define items as a catalogue of (number or text).    ← empty
```

**Open** (any element type):
```cufet-fragment
Define items as a catalogue with (42, "hello", (1 = 1)).
Define items as a catalogue.   ← empty open catalogue
```

Retrieval yields a union value — narrow before using type-specifically:
```cufet-fragment
Define first as the first of items.
If first is a number, state first + 1.
Otherwise, state the length of first.
```

All series operations apply: ordinal and parametric access, `the number of`,
`Add`, `Remove`, `for each`, element assignment. `Add` enforces the declared
union type; adding a value outside the union is a static type error. Open
catalogues accept any element type.

---

**Atlas** — a map whose value type is a union: a heterogeneous typed key→value
collection.

**Closed** (declared key and value type):
```cufet-fragment
Define mp as an atlas from text to (number or text) with ("x" : 42, "y" : "hello").
Define mp as an atlas from text to (number or text).    ← empty
```

**Open** (any key or value):
```cufet
Define mp as an atlas.
```

Retrieval yields a `voidable (union)` — the absent-key void composes with the
union value type:
```cufet-fragment
Define v as the entry for "x" in mp.   ← voidable (number or text)
If v is not void:
    If v is a number, state v + 1.
Done.
```

All map operations apply: `the entry for`, `has a key for`, `has an entry for`,
`becomes` (set), `Remove`, `the size of`, `for each`. Value-setting enforces the
declared union type; the open atlas accepts any value.

Atlases are **reference-typed** (like maps and series).

---

### Bit patterns (`bits`)

A **bit pattern is not a quantity.** `0o755` is three permission triples, not "seven hundred and
fifty-five"; `0xFF` is eight set bits, not a count of anything. So `bits` is its own type, and
bitwise operations live here and are absent from `number`.

That separation is what keeps the type well behaved. In a language where you flip the bits of a
decimal, `not 5` comes out as `-6` — correct, and baffling. Here it cannot be written at all.

#### Writing one

```cufet
Define mask as 0xFF.    // hex
Define flag as 0b1010.  // binary
Define mode as 0o755.   // octal
```

`_` groups digits and is dropped: `0xDE_AD_BE_EF`, `0b1010_1010`. It is allowed **only** in
these bases — grouping here is structural (nibbles, bytes, permission triples), while in decimal
it is cosmetic and in a fraction it marks nothing.

There is **no bare-zero octal**: `0755` is seven hundred and fifty-five. Octal must say `0o`.

A value **prints in the base it was written in**, and hex digits print uppercase.

#### Width comes from the digit count

This is the one rule that is unlike other languages. In C, Java, Rust, Go and Python, `0x0F` and
`0xF` are the same value and width belongs to the declared type. Here the digits *are* the
width:

```cufet
State 0xF.    // 0xF    — 4 bits
State 0x0F.   // 0x0F   — 8 bits
State 0x000F. // 0x000F — 16 bits
```

They compare **equal** — equality is on the value — but they display differently, and the width
is what `not` flips within. Zero-padding hex to a byte boundary is already a habit; here it
carries meaning.

The ceiling is **64 bits**, which covers every C flag set, file mode and address there is.

#### Gates

A 32-bit AND *is* 32 AND gates side by side, so the same words serve a `fact` (one bit) and a
`bits` value (N of them):

```cufet
State 0xFF and 0x0F.       // 0x0F  — mask
State 0xF0 or 0x0F.        // 0xFF  — set
State 0b1100 xor 0b1010.   // 0b0110
State not 0xFF.            // 0x00
```

Clearing a bit is `and not`, which is the only clean way to unset one:

```cufet
Define flags as 0b1111.
State flags and not 0b0100. // 0b1011
```

`xor` works on facts too. Precedence is `and` > `xor` > `or`.

**Gates refuse numbers.** `5 and 3` and `not 5` are type errors.

#### The left operand decides how the result looks

In real bit code the left operand is the accumulator — `flags or MASK` — so its base and width
are the ones that survive:

```cufet
State 0xFF and 0b1010.   // 0x0A
State 0b1010 and 0xFF.   // 0b1010
```

A result **widens** when the value needs more room and never truncates. It does not shrink back
afterwards, so a value that has grown stays wide.

#### Arithmetic

`+ - * / %` all work, with **`/` as integer division** — `0x07 / 0x02` is `0x03`, where `7 / 2`
is `3.5`. Ordering comparisons work too. Unary minus is refused: bits are unsigned.

A result with **no representation raises**, exactly as division by zero does — `0x00 - 0x1`
would be negative, `0xFFFFFFFFFFFFFFFF + 0x1` does not fit. They are exceptions rather than
value-level failures, because a failure would ride in the type as `bits or failure` and force an
unwrap after every masking expression.

#### Shifts

```cufet
State 0b0001 shifted left by 3.   // 0b1000
State 0xFF shifted right by 4.    // 0x0F
```

The amount is a **number** — it counts positions, a quantity, like the `3` in `item 3 of s`. It
must be whole and non-negative.

Left shifts widen so nothing is lost; **right shifts discard the low bits**, which is what a
right shift is rather than a failure. Being unsigned, there is no arithmetic-versus-logical
right shift to choose between.

`left` and `right` are **not reserved** — `the left of node` still works.

> **★ A shift does not add width the value does not occupy.** A result's width is the same rule
> everything else follows — the left operand's width, raised to fit the value — so a shift widens
> only when the value grows into the new positions. Leading zeros survive when there is a width to
> inherit them from, and cannot appear when there is not:
>
> ```
> State 0b00001111 shifted left by 2.   // 0b00111100 — width 8 inherited, zeros kept
> State 0b1 shifted left by 2.          // 0b100
> State 0b0 shifted left by 2.          // 0b0        — NOT 0b000
> ```
>
> The last one is the edge. `0b0` is one digit wide, the value stays zero however far it is
> shifted, and a width is only ever carried by the pattern — so there is nothing to widen. Build up
> from a literal of the width you want (`0b000`) when the leading zeros are the point, or track the
> width separately.
>
> This bites hardest when a program computes codes of varying length — a Huffman table, a
> bit-packer — where the width is data rather than decoration. **A bits value's width cannot be
> read as a number**, so such a program must carry it in a second field. Both halves of that are
> on the roadmap.

#### Crossing over

No implicit conversion, in either direction:

```cufet
State 255 converted to hex.      // 0xFF
State 10 converted to binary.    // 0b1010
State 0xFF converted to number.  // 255
State 0xFF converted to text.    // "0xFF"
```

`bits converted to number` **can never fail** — 64 bits always fits a number's 96-bit mantissa —
so it gives a plain number rather than a voidable. The other direction raises if the number is
not whole, is negative, or is past 2⁶⁴.

This is what lets a **computed** value be shown in hex:

```cufet
Define total as 200 + 55.
State total converted to hex.  // 0xFF
```

To restate a pattern in a different base, route through a number:
`x converted to number converted to binary`.

`hex`, `binary` and `octal` are not reserved words.

#### A free consequence worth knowing

C's most famous precedence bug is `a & b == c`, which silently parses as `a & (b == c)`. Cufet
has the same precedence, but the mis-parse produces `bits and fact` — **a type error**, caught
at compile time rather than computing quietly wrong answers.

#### Storing one

A `bits` goes anywhere a value goes — an object field, a series element, a map value, a
`voidable`, a union case, a channel. Carrying a pattern alongside something else is the usual
shape, because a pattern on its own rarely says what it is *for*:

```cufet
Pull a book on collections.
    Define object flagset with (the text name, the bits mask).

    Define modes as a series of flagset with (
        a new flagset { the name "read", the mask 0b100 },
        a new flagset { the name "write", the mask 0b010 }).

    Define by-name as a map from text to bits with ("exec" : 0b001).

    For each mode in modes, repeat:
        State "{mode's name} = {mode's mask}".
    Done.
    State the entry for "exec" in by-name but void is 0b0.
Done.
```
```
read = 0b100
write = 0b010
0b001
```

★ **A width is display, not data.** A stored pattern keeps the width it was built with, but no
program can read that width back as a number — so a format whose field widths vary must carry
them itself, in a second field. That is a known gap, and both halves of it are on the roadmap.

See [`examples/systems/permissions.cufe`](../examples/systems/permissions.cufe) for a worked Unix-permissions
program using all of this.

---

## Part IV. Objects and functions

### Objects

Named, nominal types that bundle data with behavior. Where records are *data*
(interchangeable by shape), objects are *things* (identity by name).

**Definition:**
```cufet
Define object vehicle with (the text make, the number year).
```

With methods:
```cufet
Define object vehicle with (the text make, the number year):
    Bind void to describe:
        State one's make.
    Done.
Done.
```

Inside a method body, `one` refers to the receiver object (`one's make`).

**A type that carries nothing says so once.** `with ()` may be dropped when there are no fields,
and so may the `{ }` when there is nothing to put in it — the same rule maps already follow, where
`a map from text to number.` is an empty typed map:

```cufet
Define object red.
Define light as a new red.
```

★ That is what makes a closed union usable as an **enumeration**, and a stronger one than a
separate `enum` construct would be — `Judge` over a closed union proves every case is handled, so
`Otherwise` is optional and a missing case is a static error:

```cufet
Define object red.
Define object green.
Define object blue.

Bind text to name-of, given (the (red or green or blue) light):
    Judge light, where it is:
        A red, return "red".
        A green, return "green".
        A blue, return "blue".
    Done.
Done.

State cast name-of on (a new green).
```
```output
green
```

**Instantiation** — `{}` literal:
```cufet-fragment
Define car as a new vehicle { the make "Honda", the year 2021 }.
```

**Access:**
```cufet-fragment
State car's make.               ← possessive: "Honda"
State the make of car.          ← named: "Honda"
State the first of car.         ← positional: "Honda"
```

**Leaving a blank** — a definition can leave the type of something to be said later. The blank is
a name you choose, and `of` marks it:

```cufet
Define object stack of element with (the series of element items):
    Bind void to push, given (the element value):
        Insert value into one's items.
    Done.
Done.

Define counts as a new stack of number { the items a series of number }.
Define names  as a new stack of text   { the items a series of text }.

Cast push on (counts, 5).
Cast push on (names, "alice").
State the first of names's items.        → "alice"
```

`a stack of number` reads like `a series of number` because it is the same shape. Each filling is
its own type: `counts` holds numbers and `names` holds text, and neither can take the other's.

More than one blank works, since you name each:

```cufet
Define object pair of left-thing of right-thing with (
    the left-thing one-side, the right-thing other-side).

Define p as a new pair of number of text { the one-side 7, the other-side "seven" }.
```

A definition with a blank is not itself a type — `a stack` on its own is refused, because it does
not say what it holds.

**A function can leave a blank too.** There is no slot to name it in, so the signature introduces
it: a type name that names nothing, used at least **twice**. The call works out what fills it:

```cufet
Bind series of element to first-two, given (the series of element xs):
    Define out as a series of element.
    Insert the first of xs into out.
    Insert item 2 of xs into out.
    Return out.
Done.

Define nums as a series of number with (1, 2, 3).
Define words as a series of text with ("a", "b", "c").

State the number of (cast first-two on (nums)).      → 2
State the first of (cast first-two on (words)).      → "a"
```

Used **once**, a name is a spelling mistake rather than a blank — `given (the nubmer n)` is an
unknown type, as it should be. A blank must also appear in something you pass in, since that is
where its filling is read from; and it has to mean the same type everywhere it appears in one call.

**Function-valued fields** — a field may hold a function, written the way a function-typed
parameter is: the return type, `function`, the field name, then an optional `given (…)`:
```cufet
Define object box with (the number function twice given (a number), the void function log).

Define b as a new box {
    the twice a function given (the number x): Return x * 2. Done,
    the log   a function: State "logged". Done
}.

Define t as the twice of b.
State cast t on (6).                    → 12
```
The name sits between `function` and `given`. `void` is available there as the return type
(`the void function log`) but is not a field type on its own.

**Read-only fields** — `permanently` after the field name, the same place it goes on a `Define`:
```cufet-refused
Define object user with (the text id permanently, the text name).

Define alice as a new user { the id "u-1", the name "Alice" }.
The alice's name becomes "Alicia".      ← fine, name is an ordinary field
The alice's id becomes "u-2".           ← refused, id is permanent
```

The field is set when the object is made and never changes after. It is per-field, so other
fields on the same object stay writable, and it is the same `permanently` that fixes a binding —
shallow in the same way, fixing the field rather than what it holds.

A setter cannot be used to get around it, and that is deliberate: setters are infallible and
transform-only, so one guarding an id could only ignore a bad write rather than reject it. The
same refusal applies inside the object's own methods (`one's id becomes …`) and to a field
inherited through an embed.

Pairs with `when` for the value:
```cufet-fragment
Define account-fee as a new account { the fee 0 when member is true, otherwise 25 }.
```

**Method dispatch:**
```cufet-fragment
Cast describe on car.                   ← verb-first, no extra args
Cast car's describe.                    ← possessive
Cast steer on (car, 90).                ← with arguments: object first, then params
Cast car's steer on (90).               ← possessive form with arguments
```

A method call uses the same syntax as a function call — the object is the first
argument in `on (...)`, with the method's declared parameters following.

**Mutation** — value-on-assignment, mutable-in-place (the same "struct model" as
records):
```
The year of car becomes 2022.           ← direct
```
Inside a mutating method, `one's year becomes ...` changes the actual instance
the method was called on. Assigning a value to a copy leaves the original
unchanged.

**Nominal typing** — two objects with identical fields but different names are
different types. Unlike records, shape alone is not identity; the type name is.

**Value semantics** — objects copy on assignment, the same as records.

**Equality:**
```cufet-fragment
If car1 is car2, state "same car".
If alice is not bob, state "different people".
```
Two objects are equal iff they are the same type and all fields are equal by
value — including all promoted (embedded) fields, compared recursively through
the embedding chain. Objects of different types can't be compared: a
compile-time type error.

#### Embedding (composition)

An object can embed another and promote its fields and methods — composition
that gives the convenience of reuse without inheritance:

```cufet-fragment
Define object customer with (the number balance) and as a person.
```

`customer` embeds a `person` and promotes its members. Access reaches through
automatically (transitively, through any chain):
```cufet-fragment
State the name of customer.             ← reaches the embedded person's name
Cast greet on customer.                 ← reaches the embedded person's method
```

Construction is **flat** — the object's own fields and all promoted fields are
supplied together in one `{...}`:
```cufet-fragment
Define alice as a new customer {
    The balance 100,
    The name "Alice",
    The age 30
}.
```

A name collision between an object's own member and a promoted one is a
compile-time error; disambiguate via the type-name handle
(`the name of the person of customer`).

Embedding **promotes members; it is not subtyping** — a `customer` is not
accepted where a `person` is expected.

#### Interfaces (polymorphism)

An interface is a contract — a set of method signatures an object must have.
It provides polymorphism without a hierarchy.

```cufet
Define driver as an interface for {
    The void function steer, given (the number angle),
    The void function brake,
    The void function accelerate, given (the number amount)
}.
```

A single-method interface may drop the braces:
```cufet
Define greeter as an interface for the void function greet, given (the text name).
```

An object declares conformance explicitly, and it is statically enforced:
```cufet-fragment
Define object street-racer with (the text name) and driver.
```

An interface name is usable as a type — a parameter typed by an interface
accepts any conforming object:
```cufet-fragment
Bind void to take-lap, given (the driver racer):
    Cast steer on (racer, 90).
    Cast accelerate on (racer, 100).
Done.
```

Conformance **is a flat compile-time check, not subtyping** — no variance is
introduced; objects do not become subtypes of one another.

**Default methods.** An interface can supply a body as well as a signature, with
`unto <interface>`. Every conforming type gets it, and a type that writes its own
version wins:

```cufet
Define shape as an interface for {
    The number function area,
    The text function describe
}.

Bind text to describe unto shape:                  ← every conformer gets this
    Return "area {(cast area on one) converted to text}".
Done.

Define object square with (the number side) and shape.
Bind number to area unto square:
    Return one's side * one's side.
Done.

State cast describe on (a new square { the side 5 }).   → "area 25"
```

`square` never writes a `describe` and still has one. Inside a default, `one` is
the *conforming object*, not the interface — so a default reaches that type's own
fields and methods, and specialises per conformer. Call a contract method with
`cast area on one`; `one's area` yields the method rather than calling it.

- **A default satisfies conformance** — an interface's method list is what a
  conformer ends up with, not what it must write.
- **A type's own method beats the default**, whether nested, `unto`, or promoted
  through an embedded type.
- **Two interfaces supplying the same defaulted name to one type is refused**,
  unless the type writes its own — which beats both.
- **Interfaces do not conform to interfaces.** No hierarchy, deliberately.

> **Note:** the default's body lives in its own `Bind … unto` block, which is
> hoisted and may sit anywhere in the file — so reading the interface's method
> list does not tell you which methods already have bodies. Putting defaults
> next to their interface is a strong suggestion, not a rule.

#### Methods defined outside the object body (`unto`)

A method can be declared *outside* its object's definition — attached with
`unto <type>` — for code organization (grouping related methods elsewhere,
splitting a large type across locations). It is **identical in every way**
to a method nested in the definition; only the declaration location differs:

```cufet
Define object person with (the text name, the number age).

Bind void to greet unto person:
    State "Hi, I'm " joined to one's name.
Done.

Bind number to birthday unto person:
    One's age becomes one's age + 1.
    Return one's age.
Done.
```

- `unto <type>` goes right after the method name, before the optional
  `, given (...)` parameter clause: `Bind void to steer unto racer, given
  (the number angle): ... Done.`
- Sees `one` (the receiver) and the object's fields exactly like a nested
  method. Called identically too — `Cast greet on alice`, `Cast alice's
  greet` — indistinguishable at the call site from a nested method.
- **Hoisted, order-independent** — the `unto` method may appear before or
  after `Define object <type>` in the file.
- **Your own object types and interfaces only.** `unto` on an undefined name
  is a static error. This is not foreign-type extension and not overloading.
  `unto <interface>` means something different — it supplies a *default* for
  every conformer, described under [Interfaces](#interfaces-polymorphism)
  above, rather than attaching a method to one type.
- **Method names are unique per type, regardless of where declared.** A name
  clash between a nested method and an `unto` method (or between two `unto`
  methods) on the *same* type is a static error. The same name `unto`
  *different* types is fine — there's no shared namespace across types.
- **Satisfies interface conformance** exactly as a nested method would — the
  conformance check looks for "does the type have a matching method?", not
  "where was it declared?"
- **A definition that leaves a blank takes `unto` two ways.** Naming the
  template attaches the member to every filling; naming one filling attaches
  it to that filling alone:

  ```cufet
  Define object stack of element with (the series of element items).

  Bind number to counted unto stack:              ← every filling gets this
      Return the number of one's items.
  Done.

  Bind number to total unto stack of number:      ← only a stack of number
      Define the sum as 0.
      For each item in one's items, repeat:
          The sum becomes the sum + item.
      Done.
      Return the sum.
  Done.
  ```

  A member written unto the template is written against the blank, exactly
  like one nested in the definition, and `element` is replaced per filling. A
  member written unto one filling is written against the concrete type — which
  is what lets `total` add its items up, something no body written for every
  filling could do. `cast total on <a stack of text>` is refused: that type
  does not have it.

  A filling cannot redeclare a member the template already gives it — the same
  "unique per type" rule as everywhere else, reported when the filling is made.

#### Getters and setters

A **getter** is a computed read-only property. Callers access it exactly like a
stored field — no distinction at the call site (Dart-style uniform access):

```cufet-fragment
Define object circle with (the number radius):
    Get area as number:
        Return one's radius * one's radius * 3.14159.
    Done.
Done.

State circle's area.         ← calls the getter; indistinguishable from a field
State the area of circle.    ← same
```

- `Get <name> as <type>:` declares a getter inside the object body.
  `Get <name> unto <type> as <type>:` declares it outside (same semantics, pure organization).
- The body must return a value. `Get ... as void:` is a parse error.
- A getter name cannot collide with a stored field on the same type — a static error.
- Getters are **infallible** — no `return a failure`.

A **setter** intercepts assignments to a named property:

```cufet-fragment
Define object temp-sensor with (the number celsius):
    Set display given (the number v):
        One's celsius becomes v.
    Done.
Done.

The display of sensor becomes 100.    ← fires the setter
```

- `Set <name> given (the <type> <param>):` intercepts `obj's <name> becomes value` and
  `the <name> of obj becomes value`. `Set <name> unto <type> given (...):` is the
  outside-body form.
- **Infallible and transform-only** — a setter may clamp, convert, or normalize,
  but cannot reject. Validation-that-rejects belongs to the caller before the assignment.
- Inside the setter body, `one's <this-name> becomes X` writes directly to the underlying
  storage, bypassing the setter (no infinite recursion).

**Give both the same name and you have a two-way property.** Nothing special is needed —
a getter and a setter declared on one name simply meet, and the property becomes readable
and writable. It need not correspond to any stored field:

```cufet
Define object temp-sensor with (the number celsius):
    Get fahrenheit as number:
        Return one's celsius * 1.8 + 32.
    Done.
    Set fahrenheit given (the number f):
        One's celsius becomes (f - 32) / 1.8.
    Done.
Done.

Define sensor as a new temp-sensor { the celsius 100 }.
State sensor's fahrenheit.             ← 212, computed by the getter

The fahrenheit of sensor becomes 32.   ← fires the setter
State sensor's celsius.                ← 0, written through
State sensor's fahrenheit.             ← 32, read back
```

`fahrenheit` is stored nowhere. The object holds one number, `celsius`, and the pair
presents a second view of it that reads and writes like an ordinary field. This is the
whole of what other languages call a property, and it falls out of the two declarations
rather than needing a third form.

Either half may stand alone: a getter with no setter is read-only (`circle's area`
above), and a setter with no getter is write-only.

#### Named constructors

A named constructor is a function that builds and returns an object. It is declared
with `making a <type>` in the return-type slot:

```cufet-fragment
Define object point with (the number x, the number y).

Bind making a point to origin:
    Return a new point { the x 0, the y 0 }.
Done.

Bind making a point or failure to from-pair, given (the text s):
    Define parts as s split by ",".
    If the number of parts is not 2, return a failure "expected x,y".
    Define x as item 1 of parts converted to number.
    Define y as item 2 of parts converted to number.
    If x is void or y is void, return a failure "non-numeric coordinates".
    Return a new point { the x x, the y y }.
Done.

Define origin-pt as cast origin.
Define p as cast from-pair on ("3,4").
```

- `Bind making a <type> to <name>[, given (<params>)]:` — the implicit return type
  is `<type>`.
- Fallible form: `Bind making a <type> or failure to <name>:` — the body may
  `return a failure ...`.
- Called via the standard `Cast <name> on (args)` syntax — no new call syntax.
- A type can have multiple named constructors; the `{...}` literal is still available.

#### Destructors

A destructor runs automatically when an object goes out of scope — RAII at the
`Done.` that closes its declaring block:

```cufet-fragment
Bind unmaking a conn to disconnect:
    State "closing " joined to one's host.
    Cast close on one.
Done.

If 1 is 1:
    Define db as cast open-conn on ("localhost").
    Cast query on (db, "SELECT 1").
Done.                              ← destructor fires here, before leaving the block
```

Rules:

- `Bind unmaking a <type> to <name>: ... Done.` — top-level only, no parameters.
- **One per type** — a second destructor for the same type is a static error.
- **Infallible** — `return a failure` in the body is a static error. For cleanup
  that *can* fail, expose a fallible method (`close`/`flush`/`commit`) and call it
  *before* the scope ends. Relying on the destructor for fallible cleanup risks silent
  data loss — the destructor swallows all outcomes.
- **LIFO order** — when multiple objects in the same scope have destructors, they
  fire in reverse definition order (last-defined, first-destroyed).
- **Every way out of the block fires them** — reaching its `Done.`, a `Stop.`, a
  `return`, an exception caught further out, and an exception that is never caught and
  ends the program. A dying program still unmakes what the blocks it is leaving hold.
- **A task body is a block** — an object defined at the top of `Have <rabbit> start a
  task:` is unmade at that task's `Done.`, whether the task finishes or faults.
- **`one` is the object being destroyed** — its fields and methods are accessible
  via `one's <field>` and `Cast <method> on one`.
- **Ownership rule** — destroy what you opened, not what you borrowed. A resource
  passed in from outside is the caller's responsibility; closing it in the destructor
  is a double-close bug.

---

#### Recursive shapes

A tree needs a node that holds nodes. Written directly, that does not work:

```cufet
Define object node with (the text label, the voidable node next).
```

An object is a **value** — its fields are stored inline, one after another. A node
containing a node contains a node, and so on with no end, so the type has no finite
size. Wrapping it in `voidable` changes nothing: `voidable node` still has to reserve
room for a whole node. The same is true of a record containing one, or a catalogue
case.

**Hold the children in a container instead.** A series is a *reference* — the object
stores a pointer to elements that live elsewhere — so the type closes:

```cufet
Define object node with (the text label, the series of node children).
```

That is the whole rule. The indirection is what makes a recursive shape possible,
and a container is how Cufet spells indirection.

**A worked tree**, evaluating `2 + 3 * 4` — this is
[`examples/structures/arbtree.cufe`](../examples/structures/arbtree.cufe):

```cufet
Define object expr with (the text kind, the number value, the series of expr kids).

Bind number to eval, given (the expr e):
    If the kind of e is "num", return the value of e.
    Define lv as Cast eval on (item 1 of the kids of e).
    Define rv as Cast eval on (item 2 of the kids of e).
    If the kind of e is "add", return lv + rv.
    If the kind of e is "mul", return lv * rv.
    Return 0.
Done.

Define three as a new expr { the kind "num", the value 3, the kids a series of expr }.
Define four  as a new expr { the kind "num", the value 4, the kids a series of expr }.
Define product as a new expr { the kind "mul", the value 0,
                               the kids a series of expr with (three, four) }.
```

Three things fall out of the shape:

- **A leaf is a node with no children**, not a different type. `a series of expr`
  with no `with (…)` is the empty one.
- **Children can be attached later.** A node's `children` is an ordinary series, so
  `Add kid to the children of parent.` works and the tree can be grown top-down.
- ★ **But a child is stored by copy.** Objects are values, and a series holds what it
  was given — not a link to it. So this prints `after` then `before`:

  ```cufet-fragment
  Insert kid into the children of parent.
  The kid's label becomes "after".
  State kid's label.                                    ← after
  State the label of item 1 of the children of parent.  ← before
  ```

  Finish a subtree before attaching it. Editing a node after it is in the tree edits
  the one you are holding, not the one the tree has.

**The `voidable` trap — the two backends differ here, and deliberately.** A
self-referential field *runs interpreted*, because the interpreter holds values in a
scope dictionary and never needs a fixed layout. The native compiler cannot, and
refuses rather than emitting something that does not compile:

```
'node' contains itself directly, and a value of it would have no fixed size. …
Hold the nested values in a container instead: `the series of node children` works,
because a series is a reference. A recursive shape needs that indirection.
```

`cufet check --native` reports this as a **warning** and still exits 0 — the program
does run, just not compiled. `cufet build` refuses outright. If a program is meant to
compile, check it with `--native` and this is caught before you reach the compiler.

**For a plain sequence, use a series.** The recursive shape earns its keep when a node
branches — a tree, an expression, a nested document. A linked list of one-child nodes
is a series written the hard way.

---

### Operator overloading

`+`, `-`, `*`, and `/` can be given a meaning for a user-defined object type:

```cufet
Define object vec2 with (the number x, the number y).

Bind overloading +, given (the lhs is a vec2, the rhs is a vec2):
    Return a new vec2 { the x lhs's x + rhs's x, the y lhs's y + rhs's y }.
Done.

Bind overloading *, given (the lhs is a vec2, the rhs is a vec2):
    Return lhs's x * rhs's x + lhs's y * rhs's y.
Done.

Define u as a new vec2 { the x 1, the y 2 }.
Define w as a new vec2 { the x 3, the y 4 }.
State u + w.
State u * w.
```
```output
vec2(x: 4, y: 6)
11
```

The rules are deliberately narrow, which is what keeps the feature predictable:

- **Only `+ - * /`.** Comparisons and `is` are not overloadable, so equality keeps one
  meaning everywhere — including inside `unique`, map keys, and `sorted`.
- **The operand types are an ORDERED PAIR**, and at least one of them must be an object
  type you defined. `vec2 * number` declares that and only that — `number * vec2` is a
  separate declaration, written when you want it:

  ```
  Bind overloading *, given (the lhs is a vec2, the rhs is a number):   ← u * 3
  Bind overloading *, given (the lhs is a number, the rhs is a vec2):   ← 3 * u
  ```

  Ordered, because swapping is not a shorthand anyone could have: `2 - u` is not `u - 2`,
  and `2 / u` is not `u / 2`. There is still no conversion or promotion, so there is never
  more than one candidate and never any ambiguity about which one applies.
- **A pair that already means something cannot be overloaded.** That is exactly two:
  `number op number` and `bits op bits`, which are built-in arithmetic. Everything else
  is free — `text * number` and `text + text` are errors today, so nothing is shadowed by
  giving them a meaning. Whether `+` on text is good style when `joined to` exists is the
  writer's call, not the checker's.
- **One overload per ordered pair and operator**, enforced by the type checker. A type may
  take part in several — `vec2 * number`, `number * vec2` and `vec2 * vec2` coexist.
- **Built-ins cannot be shadowed** — `number + number` always means addition.
- **The return type is whatever the body returns.** A dot product returning `number`, as
  above, is fine.
- **An overload may fail.** Returning `a failure` makes it fallible, and it then composes
  with `Try to:`, `but on failure`, and `or pass the failure off` like any other fallible
  call. This is exactly how matrix arithmetic is built.

Declarations are free-standing and top-level, not members of the object.

---

### Functions

**Declaration:**
```cufet
Bind number to plus, given (the number x, the number y):
    Return x + y.
Done.

Bind void to greet, given (the text name):
    State name.
Done.

Bind number to get-ten:
    Return 10.
Done.
```

`Bind` declares a named function. The return type comes first (`number`, `void`,
or a function type). Parameters follow `given`. Functions with no parameters omit
`given` entirely. Functions are top-level and hoisted, so they may be defined in
any order and may recurse.

**Calling:**
```cufet-fragment
State cast plus on (3, 4).         ← in expression position
Cast greet on ("hello").           ← as a statement (void or discarded result)
```

`Cast` works on any expression that evaluates to a function — a name, a variable,
a series element, or a method.

**Named arguments** — the same `the <name> <value>` form object and record literals use.
They may be given in any order:

```cufet
Bind number to take-half, given (the number whole, the number divisor):
    Return whole / divisor.
Done.

State cast take-half on (the divisor 2, the whole 16).
State cast take-half on (16, the divisor 2).
```
```output
8
8
```

Positional arguments must all come first; once one is named, the ones after it must be
too, because position no longer says which parameter is meant. Naming one that is not a
parameter, giving one twice, or leaving one out is a static error naming the parameter.

The names come from the **declaration**, so a call reaching its function through a value
— a parameter, a field, another call's result — stays positional: the value carries the
parameter types, but not what they are called.

**Several versions of one name** — when exactly one argument's type tells them apart:

```cufet
Define object num-node with (the number value).
Define object add-node with (the number left, the number right).

Bind number to eval, given (the num-node node):
    Return node's value.
Done.

Bind number to eval, given (the add-node node):
    Return node's left + node's right.
Done.

Define nodes as a catalogue of (num-node or add-node) with (
    a new num-node { the value 1 },
    a new add-node { the left 2, the right 3 }).

For each n in nodes, repeat:
    State cast eval on (n).
Done.
```
```output
1
5
```

Each element of that catalogue is a `(num-node or add-node)` — nothing at the call says
which — so the version is chosen from what the value actually is.

Every version must take the same number of arguments and give back the same type, so a
caller knows what it gets without knowing which version ran. Two versions claiming the
same types is an error.

**More than one argument may tell them apart**, and then every combination needs a
version — four of them for two arguments carrying two types each. A missing combination
is an error naming it, because a call could pass that pair and nothing would run.

The versions decide what the name accepts: `eval` above takes a `(num-node or add-node)`,
and passing anything else is an ordinary type error naming the case that has no version.

**A version may carry a condition** with `when`, and one version must carry none — it
runs when no condition holds:

```cufet
Define object add-node with (the number left, the number right).

Bind number to fold, given (the add-node node) when node's left is 0 and node's right is not 0:
    Return node's right.
Done.

Bind number to fold, given (the add-node node) when node's right is 0:
    Return node's left.
Done.

Bind number to fold, given (the add-node node):
    Return node's left + node's right.
Done.

State cast fold on (a new add-node { the left 0, the right 9 }).
State cast fold on (a new add-node { the left 2, the right 3 }).
```
```output
9
5
```

**Two conditions that could both hold are an error.** Above, `left is 0` on its own
would overlap `right is 0` — both hold on `0 + 0` — so the first says `and node's
right is not 0` to exclude it. The language never decides which of two overlapping
versions wins; it refuses them.

A condition is built from equality against a literal (`node's left is 0`), a type
test (`node is a num-lit`), either of them negated, and `and`, `or` and `xor`.
Ordering and arithmetic are not part of it: `when node's left is greater than 3` is
refused, because whether two such conditions overlap is not something the checker
can decide by comparing them.

**Early exit:**
```cufet-fragment
Return value.    ← return a value
Return.          ← void early exit
```

A non-`void` function must return a value on every path; one that can fall off
its end without returning is a compile-time error.

**Functions are first-class values:**
```cufet-fragment
Define op as plus.
State cast op on (3, 4).           → 7
```

A function assigned to a variable carries its full type. The type checker catches
calling the wrong signature through any alias.

**Function-typed parameters:**
```cufet
Bind number to apply, given (the number x, the number function f given (the number)):
    Return cast f on (x).
Done.

Bind number to double, given (the number x): return x * 2. Done.

State cast apply on (5, double).   → 10
```

The parameter type `the number function f given (the number)` declares that `f`
must be a function taking a number and returning a number. Passing the wrong
signature is a static type error.

**Functions as return values:**
```cufet
Bind number to double, given (the number n): Return n * 2. Done.

Bind number function given (the number) to get-doubler:
    Return double.
Done.

Define fn as cast get-doubler on ().
State cast fn on (5).              → 10
```

The return type `number function given (the number)` declares that this function
returns a function. Closures and lambda literals can be returned too — see
[Closures](#closures) and
[Lambda literals](#lambda-literals-anonymous-functions) below.

**Series of functions:**
```cufet-fragment
Define ops as a series of number function given (the number) with (double, triple).

State cast the first of ops on (5).          → 10

For each op in ops, repeat:
    State cast op on (5).
Done.
```

A series whose element type is a function type. All the usual series operations
apply — access, add, remove, for-each — and any accessed element can be `Cast`
directly.

#### Closures

A function declared with `Bind` *inside* another function or method body
captures the enclosing variables at the point of declaration:

```cufet
Bind number function given (the number) to make-adder, given (the number n):
    Bind number to adder, given (the number x):
        Return x + n.
    Done.
    Return adder.
Done.

Define add-five as cast make-adder on (5).
State cast add-five on (10).          → 15
```

Capture follows the same value/reference split used everywhere else in
Cufet: value types (`number`, `text`, `fact`) are captured as a snapshot at
declaration time, so later changes to the outer variable don't affect an
already-created closure; reference types (series, maps, objects) capture the
live instance, so mutations through the closure are visible in the outer
scope and vice versa.

#### Lambda literals (anonymous functions)

A function literal written inline, with no name — usable anywhere a function
value goes: assigned, passed as an argument, returned, or stored in a series.

```cufet
Bind number to apply, given (the number value,
                            the number function transform given (the number)):
    Return cast transform on (value).
Done.

Define double as a function given (the number x): Return x * 2. Done.
State cast double on (5).

State cast apply on (10, a function given (the number x): Return x * 2. Done).
```
```output
10
20
```

The body is always `Done`-terminated (there's no inline single-statement
form). The return type is **inferred from the body** — there's no syntax to
declare it. Lambdas capture enclosing variables under the same rule as
[Closures](#closures) above, and always carry their captured environment.

---

### Sorting

`sorted` is a postfix operator on a series. It returns a **new** series; the original is
untouched.

```cufet
Define nums as a series of number with (5, 1, 4, 1, 3).
State nums sorted.
State nums sorted in reverse.

Define words as a series of text with ("pear", "apple", "fig").
State words sorted.
```
```output
(1, 1, 3, 4, 5)
(5, 4, 3, 1, 1)
(apple, fig, pear)
```

Numbers sort numerically and text sorts ordinally. For a series of records or objects,
sort by a field with `sorted by the <field>`:

```cufet
Define object person with (the text name, the number age).

Define folks as a series of person.
Insert a new person { the name "Ada", the age 36 } into folks.
Insert a new person { the name "Bo", the age 24 } into folks.

For each p in folks sorted by the age, repeat:
    State p's name.
Done.
```
```output
Bo
Ada
```

The sort is **stable** — equal elements keep their original relative order — and
`in reverse` composes with `by the <field>`.

> Because `sorted` produces a copy, `Add x to (items sorted)` would mutate a temporary
> and lose the result. The type checker catches that.

---

## Part V. The type system

### Type system

Cufet has a static type checker that runs before execution. It catches:

- Arithmetic on non-numbers
- Comparing values of different types
- Assigning a value of the wrong type to a variable
- Passing the wrong argument types to a function
- Passing a function with the wrong signature
- Returning the wrong type from a function
- Functions that might not return on every path
- Using a `voidable` value where a plain (non-void) value is required without
  handling the void case
- Using type-specific operations (arithmetic, `the length of`, etc.) on an
  un-narrowed union value — narrow with `is a <type>` first
- Adding a value to a catalogue that doesn't match the declared union element type
- Setting an atlas value that doesn't match the declared union value type
- Calling a method that doesn't exist on an object
- Accessing a non-series with series operations
- Adding or removing the wrong element type from a typed series
- Assigning the wrong type to a record or object field
- Passing a record that doesn't match the declared shape
- Adding a record to a series whose shape it doesn't match
- An object that claims an interface but doesn't satisfy its contract
- Comparing records of incompatible shapes, or objects of different types, with `is`
- Joining a non-text value with `joined to`, or using the wrong key/value types
  with a map
- Converting a non-text value with `converted to number`
- A `range ... counting by` step that is zero or negative (when known at
  compile time; a runtime check catches the rest)
- Using `split by`, `contains`, `the position of ... in ...`, or any
  substring form on a non-text value
- An empty delimiter in `split by`
- A character position of zero or negative in a substring form (when known
  at compile time; a runtime check catches the rest)
- Using `replace`, `in uppercase`/`in lowercase`, or `trimmed` on a non-text
  value
- An empty target in `replace ... with ... in ...`
- Declaring a name already declared in the same scope
- Declaring a name that exists in an enclosing scope without the `shadow` keyword
- Using `Define a shadow x` when no outer `x` exists
- `unto` naming an undefined type (an interface is fine — it supplies a default)
- Two interfaces supplying the same defaulted method name to one type, unless
  that type writes its own
- A method name clash between a nested method and an `unto` method (or
  between two `unto` methods) on the same object type
- Reassigning a `permanently` binding with `becomes`
- Dropping a failable (`failure T`) value without handling it (fallback,
  propagation, or enclosing `Try`) — unhandled failure is always a static error
- Reading from a `writable stream of text`, or writing to a `readable stream
  of text` — stream direction is statically enforced
- File reads (`read all from the file`, `read all lines from the file`) and
  process execution (`run`) outside a `Try` block or propagation context —
  their failable return types must be handled
- Declaring a second destructor (`Bind unmaking a <type>`) for a type that
  already has one — duplicate unmaker is a static error
- Using `return a failure` inside a destructor body — destructors are infallible
- `Get ... as void:` — getters must return a typed value; void is a parse error

**Records use structural typing** — shape is identity. Two records with the same
fields and types are the same type regardless of where they were declared. Named
fields match order-insensitively; positional fields match by order.

**Objects use nominal typing** — the type name is identity. Two objects with
identical fields but different names are different types.

**Assignment semantics differ by kind:** records and objects are value-typed
(copy on assignment); series are reference-typed (share). Records and objects are
bounded "things"; series are unbounded "collections" — and the copy-vs-share
intuition differs accordingly.

Type errors name the violation, the line, and the fix:

```
That doesn't work: 'scores' holds numbers.
You defined it on line 1 as a series of numbers, so it can only accept numbers.
Here on line 4, you're trying to add a text value to it.

Change the value to a number, or define a separate series that holds text.
```

---

### Voidable values (`void` and `voidable T`)

Cufet has no null. Absence is expressed with a first-class empty value, `void`,
and a type that admits it, `voidable T`. This is how "a value, or nothing" is
said — explicitly, and checked.

**`void`** is a real, holdable value (it prints as `void`). A `void`-returning
function produces it; a map lookup that misses produces it.

**`voidable T`** is "a `T`, or `void`":
```cufet-fragment
Define maybe-score as 95.        ← a number is a valid voidable number (present case)
Define maybe-score as void.      ← the absent case
```
Usable in any annotation position — parameters, return types, series elements,
record/object fields:
```
Bind voidable number to find-score, given (the text name):
    ...
Done.
```

**Type rules:**
- A plain `T` widens to `voidable T` automatically (a `number` is accepted where
  a `voidable number` is wanted) — one-way.
- `void` is the empty case of any `voidable T`.
- A `voidable T` does **not** collapse to `T`. Using one where a plain `T` is
  required is a static type error — you must handle the void case first.

**Testing and handling:**

`is void` / `is not void` — a boolean test:
```cufet-fragment
If maybe-score is not void, state maybe-score.  ← narrowed to a plain number here, safe to use directly
Otherwise, state "no score".
```
Inside a branch that has checked a **variable** is not void, the checker narrows
that variable to its plain `T`, so it can be used directly. Narrowing is keyed on
the variable and is cleared if the variable is reassigned within the branch.

**Guard narrowing** — an exiting guard narrows the *fall-through* path. When a
one-line `If x is void, return …` (no `Otherwise`) whose body always returns is
passed, the statements after it run only when `x` was **not** void, so `x` is
narrowed to plain `T` from that point to the end of the block:
```cufet
Bind number or failure to parse-age, given (the text raw):
    Define n as raw converted to number.
    If n is void, return a failure "not a number".
    Return n.                        ← n is a plain number here, not voidable
Done.
```
A disjunctive guard narrows every variable it names: after
`If x is void or y is void, return …`, both `x` and `y` are non-void on the
fall-through. The narrowing lives only in the block that contains the guard —
a guard nested inside an `If`-arm says nothing about the path where that arm was
skipped, so it never leaks past the arm.

> To narrow a value produced by an expression (like a map lookup), name it first
> — `Define s as the entry for "alice" in ages.` then check `s`. A bare literal
> buried inside a lookup is a value worth naming anyway; narrowing follows the
> named binding.

`but void is <default>` — an inline fallback that always yields a plain `T`:
```cufet-fragment
Define n as (the entry for "alice" in ages but void is 0).
```

---

### Union types and narrowing

A **union type** is a value that can be one of several listed types. Declared
with `or` in parentheses:

```cufet-fragment
Define the (number or text) x as 42.
Define the (number or text or fact) y as 42.
```

**Type-agnostic operations** — without narrowing, only operations that work on
every case are allowed: assignment, `becomes`, passing to a union-typed parameter,
storing into a catalogue or atlas, and equality comparison (`is`/`is not`) between
two values of the same union type.

**Type-specific operations** — arithmetic, `the length of`, and anything that
only makes sense for one type — require narrowing first. Using them on an
un-narrowed union is a static type error that names the expected narrowing form.

#### `is a <type>` / `is not a <type>`

The runtime type-test, generalizing `is void`:

```cufet-fragment
If x is a number, state x + 1.
If x is not a text, state "not text".
```

Works with any type name: `is a number`, `is a text`, `is a fact`, object type
names, etc. `is an <type>` is accepted wherever the article fits. Both forms
are identical.

#### In-branch narrowing

After a successful `is a <type>` check, the value is that type inside the
branch — type-specific operations are legal there:

```cufet
Define the (number or text) x as 42.

If x is a number, state x + 1.      ← x is a number here; arithmetic is legal
Otherwise, state the length of x.   ← x is a text here (narrowed by elimination)
```

**Narrowing by elimination** — for a **closed** union, the `Otherwise` arm
automatically narrows to the remaining case(s). After `if x is a number` on a
`(number or text)` union, `Otherwise` knows `x` is `text`.

For a three-case union, two tested arms leave the third for `Otherwise`:

```cufet
Define the (number or text or fact) x as 42.

If x is a number, state x + 1.
Otherwise if x is a text, state the length of x.
Otherwise, state x converted to text.    ← x is a fact here
```

**`is not a <type>`** narrows the true branch to the complement — for a
`(number or text)` union, `if x is not a number` narrows `x` to `text` in the
true branch. Its `Otherwise` narrows the other way, to the type that was tested:
reaching it means `x` **is** a number. That holds for a lone arm only — after
several arms, reaching the `Otherwise` no longer says which test failed.

**A group of tests on one value** narrows to the sub-union it names, and the
`Otherwise` gets whatever is left:

```cufet
Define the (number or text or fact) x as 42.

If x is a number or x is a fact:
    Judge x, where it is:                ← no Otherwise needed: text is ruled out
        A number, state "n".
        A fact, state "f".
    Done.
Done.
Otherwise:
    State the length of x.               ← x is a text here
Done.
```

Every operand has to be a positive test on the **same** name. A mixed
disjunction narrows nothing, because reaching the arm would not imply any one of
its parts.

**Open unions** — `Otherwise` after an open union check is *not* narrowable;
only agnostic operations are legal there. Open is sound (narrowing still
required), never `any`.

Narrowing is **variable-level** — the same rule as voidable narrowing. The
narrowed type clears when the variable is reassigned. To narrow a value produced
by an expression, name it first.

---

### Error handling (failures and exceptions)

Cufet distinguishes two kinds of bad outcome:

- **Failures** — expected, recoverable outcomes that are part of a function's
  contract. A file not being found is a failure. A config value being invalid is
  a failure. These are things a caller should plan for.
- **Exceptions** — unexpected outcomes the type system can't prevent at compile
  time. Divide-by-zero is an exception. An out-of-bounds access with a runtime
  index is an exception. These are things that should not happen in correct code.

The two paths are handled separately and cannot be mixed up.

#### Failure values (`failure T`)

`failure T` is "either a plain `T` or a failure." A failure carries a text
message and an optional category tag. The parallel to `voidable T` is exact:
same inline-fallback syntax, same propagation operator, same block form.

**Failure literal:**
```cufet-fragment
Define err as a failure "not found" of category "not-found".
Define err as a failure "something went wrong".       ← category is optional
```

**Inline fallback — `but on failure <default>`** — collapses `failure T` to
plain `T`, like `but void is` for voidable:
```cufet-fragment
Define n as (cast parse-int on (raw) but on failure 0).
```

**Propagation — `or pass the failure off`** — re-raises the failure to the
caller. The function must itself declare a failable return type:
```cufet
Bind number or failure to to-positive, given (the number n):
    If n is 0 or less, return a failure "must be positive" of category "range".
    Return n.
Done.

Bind number or failure to double-positive, given (the number n):
    Define p as cast to-positive on (n) or pass the failure off.
    Return p * 2.
Done.
```

**Unhandled failure is a static error** — dropping a failable value without
a fallback, a propagation, or a `Try` block is caught by the type checker, not
at runtime.

#### Block form: `Try to`

For multiple statements that may produce failures, `Try to:` handles them as a
group:

```cufet
Try to:
    Define body as read all from the file "data.txt".
    State body.
Done.
In case of failure:
    State "could not open file: {the message of the failure}".
Done.
```

Inside `In case of failure:`, `the failure` is bound to the failure value.
Access its fields with named access:
```cufet-fragment
In case of failure:
    State the message of the failure.
    State the category of the failure.    ← text, or void if no category was given
Done.
```

**`In case of exception`** — catches runtime exceptions (divide-by-zero,
dynamic out-of-bounds, etc.) that the type system can't statically prevent:
```cufet
Try to:
    State 1 / 0.
Done.
In case of exception:
    State the message of the exception.    ← "Division by zero on line 2."
Done.
```

Inside the handler, `the exception` is bound to what was raised, and its text is
reached the same way a failure's is — `the message of the exception`. The value
itself is opaque: printing it gives `<exception>`, as printing a function gives
`<function>`.

**The binding can be renamed, and the parentheses are optional.** Bare, it is
called `the exception`; naming it is what nested handlers want, so the inner one
does not shadow a name the outer one is still using:

```cufet-fragment
In case of exception:                     ← bound as 'the exception'
In case of exception (the trouble):       ← bound as 'the trouble'
In case of exception (the exception):     ← the same as the bare form, said out loud
```

It is block-local to the handler either way. This is the pair `For each` already
has: `For each item in items` names the iterand, and bare `it` is the implicit one.

Exceptions **re-raise by default** after the handler runs.
`Suppress the exception.` (only valid inside `In case of exception`) swallows the
exception and continues execution after the `Try`:
```cufet-fragment
In case of exception (the exception):
    State "ignoring: {exception}".
    Suppress the exception.
Done.
```

Leaving the handler this way releases everything the handler opened — objects
with destructors are unmade, files opened inside it are closed — exactly as
`Stop` does on the way out of a loop.

Both handlers can appear in the same `Try`:
```
Try to:
    ...
Done.
In case of failure:
    ...
Done.
In case of exception (the exception):
    ...
Done.
```

At least one handler is required. The two paths are independent — a failure
goes only to `In case of failure`; an exception goes only to
`In case of exception`.

---

## Part VI. Input and output

### Input and output

#### Reading from standard input

The pre-defined name `input` holds standard input as a `readable stream of
text`. Three read forms cover common patterns:

```cufet
Define line  as read a line from the input.      ← voidable text (void at EOF)
Define all   as read all from the input.         ← text (empty string at EOF)
Define lines as read all lines from the input.   ← series of text (empty at EOF)
```

`read a line from the input` strips the trailing newline and returns
`voidable text` — `void` signals end-of-input. The typical read loop:
```cufet
Repeat:
    Define line as read a line from the input.
    If line is void, stop.
    State line.
Until false.
```

(`until false` is the standard idiom for a loop that exits only via `Stop.`)

`read all from the input` drains all of stdin and returns it as one `text`
value (empty input → `""`; never void). `read all lines from the input`
splits on newlines and returns a `series of text` (empty input → empty series).

#### File I/O

**Reading an entire file** — returns a failable value; must be handled:
```cufet
Try to:
    Define text as read all from the file "notes.txt".
    State text.
Done.
In case of failure:
    State "could not read: {the message of the failure}".
Done.
```

`read all from the file <path>` returns `text or failure`.
`read all lines from the file <path>` returns `series of text or failure`.
The path is any text expression (literal, variable, or interpolated string).

Failure categories: `"not-found"`, `"permission-denied"`, `"disk-error"`.

**Writing to a file:**
```cufet
Write "hello\n" to the file "out.txt".      ← overwrite (create or truncate)
Append "more\n" to the file "out.txt".      ← append to end
```

Write and append complete silently on success; on failure they raise a Cufet
failure caught by the enclosing `Try` handler.

**Scoped file streams — `With the file ... open for reading/writing as`:**

For reading line-by-line, or writing incrementally, open the file as a stream
and let Cufet close it automatically:

```cufet
With the file "data.txt" open for reading as src:
    Define first as read a line from src.
    State first.
    Define second as read a line from src.   ← the stream remembers where it got to
    State second.
Done.
```

**A line can only be read from an open stream, never from a path.** A path has
nowhere to remember how far you have read, so `read a line from the file "x"`
would hand back the first line every time — it is refused, and the error names
this form. Reading a whole file at once (`read all from the file "x"`) needs no
stream, because there is no position to keep.

```cufet
With the file "out.txt" open for writing as log:
    Write "Line 1\n" to log.
    Write "Line 2\n" to log.
Done.
```

`With the file <path> open for reading as <name>: ... Done.` opens the file,
binds it to `<name>` (a `readable stream of text`) for the duration of the
block, and closes it on every exit path — including failures, exceptions, and
`Stop.` inside the block. `for writing` binds a `writable stream of text`.

Stream direction is **statically enforced**: reading from a writable stream, or
writing to a readable stream, is a static type error.

An open failure (file not found, permission denied) propagates to the enclosing
`Try` handler the same as any other file failure.

**Stream reads support all three read forms** — a `readable stream of text`
works anywhere `the input` works:
```cufet
With the file "lines.txt" open for reading as s:
    Define lines as read all lines from s.
    For each line in lines, repeat:
        State line.
    Done.
Done.
```

**Passing a stream to a function:**
```cufet
Bind void to process, given (the readable stream of text src):
    Define line as read a line from src.
    State line.
Done.
```

#### Process execution

`run <program>` runs an external program synchronously and collects its output:

```cufet
Try to:
    Define result as run "git" with arguments ("log", "--oneline", "-5").
    State the output of result.
    If the exit-code of result is not 0, state "stderr: {the errors of result}".
Done.
In case of failure:
    State "git not available".
Done.
```

The result is a **record**, so its fields are read with `the <field> of <record>`.
Possessive `'s` is for objects and is a static error here.

`run <program>` and `run <program> with arguments (<arg1>, <arg2>, ...)`
return a result record with three fields:

| Field | Type | Meaning |
|---|---|---|
| `output` | `text` | everything written to stdout |
| `errors` | `text` | everything written to stderr |
| `exit-code` | `number` | the process exit code |

The return type is `result or failure`. A **launch failure** (program not found,
permission denied) is a Cufet failure. A program that **runs and exits nonzero**
is not a failure — it is a normal result; check `exit-code` or `errors` to
decide what to do.

Failure categories: `"not-found"`, `"permission-denied"`, `"io-error"`.

Arguments are passed as individual strings to the OS — no shell is invoked and
shell injection is structurally impossible. The program name is any text
expression.

**The argument list may be a series instead**, when the program does not know until it runs how
many arguments there will be:

```cufet
Try to:
    Define argv as a series of text with ("--oneline", "-5").
    Define result as run "git" with arguments argv.
    State the output of result.
Done.
In case of failure:
    State "git not available".
Done.
```

The series must be a `series of text`, and an empty one is not a missing one — the program still
launches, with no arguments.

⚠ **A `(` after `arguments` always opens the written-out list.** So `with arguments (argv)` is a
list of ONE argument that happens to be a series, not the series form, and is refused as such:

```cufet-refused
Try to:
    Define argv as a series of text with ("a").
    Define result as run "echo" with arguments (argv).
    State the output of result.
Done.
In case of failure:
    State "no".
Done.
```

The forms are told apart by that one token and never by the type, so that a reader can see which
one is written without knowing what `argv` holds.

**As a statement, it launches the program with this terminal.** Written on its own —
`Run <program>.` — nothing is captured and nothing comes back:

```cufet
Try to:
    Run "vim" with arguments ("notes.txt").
Done.
In case of failure:
    State "vim is not installed".
Done.
```

This is the same distinction the language draws everywhere else: a statement runs something for
its **effect**, an expression for its **value** — `Cast f on (x).` beside
`Define y as cast f on (x).` is the pair it copies.

The difference between the two forms is **who gets the terminal**, not what happens to the text.
The expression form hands the child a pipe, so its output arrives all at once when it exits and a
program that draws — `vim`, `less`, `top` — has no terminal to ask about. The statement form gives
the child this program’s own stdin and stdout, so output streams as it happens and an interactive
program works.

Both wait for the child, and both are fallible for the same reason — the program may not exist —
so the statement form must be handled too. What it does **not** give back is the result record, so
there is no exit code to read: a program that runs and exits nonzero is an ordinary outcome the
statement form does not report. Use the expression form when the output is data or the exit code
matters.

#### Environment variables

`the environment variable "NAME"` reads a process environment variable by name,
returning `voidable text`:

```cufet
Define home as the environment variable "HOME".
If home is not void, state "home is {home}".
Otherwise, state "HOME is not set".

Define path-val as the environment variable "PATH" but void is "".
```

- Returns `voidable text` — `void` if the variable is not set.
- The name is any text expression (literal, variable, or interpolated string).
- Read-only — Cufet does not expose setting environment variables.

#### The current directory

**Read it** — `the current directory` returns `voidable text`:

```cufet
Define here as the current directory but void is "(unknown)".
State here.
```

It is voidable for the same reason the environment variable is: the answer comes from the
operating system, and the operating system is allowed to have none. In practice `void` means the
directory was removed out from under the running process. Every ordinary program gets a value.

**Change it** — `The current directory becomes path.` is a statement, and a fallible one:

```cufet
Try to:
    The current directory becomes "/tmp".
    Write "notes" to the file "scratch.txt".   /* relative to /tmp now */
Done.
In case of failure:
    State "could not move there: {the message of the failure}".
Done.
```

Failure categories, and the message each produces:

| Category | When | Message |
| --- | --- | --- |
| `not-found` | nothing is there | `the directory '<p>' was not found` |
| `not-a-directory` | it exists, but is a file | `'<p>' is not a directory` |
| `permission-denied` | it exists, but you may not enter | `permission denied entering directory '<p>'` |
| `disk-error` | anything else | `changing to the directory '<p>' failed` |

- **It affects relative paths** for everything afterwards — file reads and writes, directory
  listings, and subprocesses launched with `run`, which inherit it.
- **A failure is recoverable.** A bad path costs you a handled failure, not the program, which is
  what lets [`tools/shell.cufe`](../tools/shell.cufe) implement `cd` without a typo ending the
  session.
- **Not allowed inside a task.** A process has exactly one working directory, so changing it from
  a task would race every other task resolving a relative path. The compiler refuses with an
  explanation; change it in the rabbit body before starting tasks, or pass the directory in and
  build full paths. (Reading it from a task is fine.)

> **Windows paths in a quoted literal need doubled backslashes.** `"C:\Windows"` is a *lexer*
> error, because `\W` is not a recognised escape. Write `<<C:\Windows>>`, where nothing is
> escaped at all, or `"C:\\Windows"`, or — often nicest — `"C:/Windows"`, which Windows accepts
> everywhere Cufet passes a path through.

#### Directory traversal

**List a directory** — `the contents of the directory path` returns the names of
entries (files and subdirectories) inside the directory as a `series of text or
failure`. Entry names are plain names, not full paths. Order is not guaranteed.

```cufet
Try to:
    Define entries as the contents of the directory "/tmp".
    For each name in entries, repeat:
        State name.
    Done.
Done.
In case of failure:
    State "cannot read: {the message of the failure}".
Done.
```

Failure categories: `"not-found"`, `"permission-denied"`.

**Path existence and kind tests** — three boolean predicates (all return `fact`,
never fail, never void):

```cufet-fragment
If the path "/tmp/myfile" exists:
    If the path "/tmp/myfile" is a file, state "regular file".
    Otherwise if the path "/tmp/myfile" is a directory, state "directory".
Done.
```

| Test | Returns `true` when |
|---|---|
| `the path expr exists` | the path names any existing filesystem entry |
| `the path expr is a file` | the path names an existing regular file |
| `the path expr is a directory` | the path names an existing directory |

The path expression is any `text`. A path that exists but is neither a regular file
nor a directory (device node, dangling symlink, etc.) makes `exists` true but both
`is a file` and `is a directory` false.

---

## Part VII. Systems programming

### Regions (`Pull a rabbit`)

A **region** — a "rabbit" — is a block whose reference-typed values all live and die
together. `Pull a rabbit.` opens one; `Done.` closes it and releases everything created
inside.

```cufet
Define totals as a series of number.

Pull a rabbit.
    Define scratch as a series of number with (1, 2, 3, 4, 5).
    Define sum as 0.
    For each n in scratch, repeat:
        The sum becomes sum + n.
    Done.
    Insert sum into totals.
Done.

State totals.        ← (15)
```

`scratch` is gone after `Done.`; `totals` is not, and the value added to it survives.

A rabbit may be named. The name is a handle you can pass to a function so the callee
allocates in *your* region:

```cufet
Pull a rabbit as workspace.
    Define note as "built" joined to " inside".
    State note.
Done.
```

#### The outward-only rule

**A value may be stored somewhere longer-lived than itself, never somewhere
shorter-lived.** That single rule is the whole safety story. The type checker enforces
it from the static block structure — there is no garbage collector and no borrow
checker.

Storing outward is fine, and is what the example above does. Storing *inward* — parking
a value in a container that will outlive the region the value came from, in a way that
would leave the container pointing at released memory — is a compile-time error:

```cufet-fragment
Bind series of number to smuggle, given (the series of number s):
    Return s.
Done.

Define outer as a series of number.
Pull a rabbit.
    Define inner as a series of number with (1, 2, 3).
    The outer becomes Cast smuggle on (inner).    ← REJECTED
Done.
```

The error names the region mismatch, and it is caught even though the value was
laundered through a function's return value.

Returning a value out of a region is allowed and does the safe thing: the value stays
valid for the caller.

#### Two backends, one rule

Interpreted, "released at `Done.`" is modelled semantically — values simply become
unreachable and .NET's collector reclaims them whenever it likes. Compiled, the region
is a real bump-allocated arena, thread-local, freed in one shot. A region is released on
**every** exit from it, not only the normal one: `Done.`, `return`, `Stop`, `Skip`,
`Suppress`, a failure unwind, an exception, an interrupt.

The rule above is what makes the compiled version safe, which is why it is enforced even
though the interpreter would forgive breaking it.

#### When to reach for one

- **Long-running loops.** Put a rabbit *inside* the loop body and each iteration's
  working memory is released at the end of that iteration. Without one, a compiled
  program's allocations accumulate until the enclosing block ends.
- **Concurrency.** A rabbit is also the structured-concurrency boundary — see below.

---

### Concurrency (tasks and channels)

#### Tasks

`Have rabbit start a task: … Done.` runs a block concurrently. It must appear inside a
rabbit, and **the rabbit's `Done.` waits for every task it started**. A task therefore
cannot outlive the region that launched it.

**You can name the rabbit you are giving work to** — `Have hopper start a task: … Done.` —
which is what a rabbit's name is for: a rabbit is an agent you summon and hand a job.
The bare `rabbit` keyword still means the enclosing one, and the two forms mix freely.

⚠ Naming a rabbit pulled *further out* is refused. The task would be joined by that
rabbit's `Done.`, so it would have to outlive the block it was written in. Give the work
to the rabbit pulled in this block, or move the work out to where that rabbit lives.

Name a task with `as <name>` and it can `return` a value, which you collect with
`the awaited result of <name>`:

```cufet
Pull a rabbit.
    Have rabbit start a task as left:
        Return 1 + 2 + 3.
    Done.
    Have rabbit start a task as right:
        Return 4 + 5 + 6.
    Done.
    State (the awaited result of left) + (the awaited result of right).
Done.
```
```output
21
```

An unnamed task is fire-and-forget: it still joins at `Done.`, but any value it returns
is dropped. Awaiting the same task twice is fine — the body runs once either way.

**A task can await another task**, so work can be staged rather than only fanned out:

```cufet
Pull a rabbit.
    Have rabbit start a task as fetch:
        Return 21.
    Done.
    Have rabbit start a task as double-it:
        Define v as the awaited result of fetch.
        Return v * 2.
    Done.
    State the awaited result of double-it.
Done.
```
```output
42
```

Several tasks may await the same task; each gets its own copy of the result.

**A task can only await one declared before it**, because the name has to be in scope — which
also means a cycle of tasks waiting on each other cannot be written, so this cannot deadlock.
Awaiting a task declared later is a type error, not a hang.

#### Channels

A channel is a typed queue for passing values between tasks. `Send <value> through
<channel>.` puts one in; `the delivery from <channel>` takes one out, yielding
`voidable T` — void once the channel is closed and empty, which is how a receiver knows
to stop.

```cufet
Pull a rabbit.
    Define results as a channel of number.
    Have rabbit start a task:
        Send 10 through results.
        Send 20 through results.
        Close results.
    Done.
    Define total as 0.
    Define arrival as the delivery from results.
    While arrival is not void, repeat:
        The total becomes total + (arrival but void is 0).
        The arrival becomes the delivery from results.
    Done.
    State total.
Done.
```
```output
30
```

`Close` is idempotent; sending after a close is a runtime error. A blocked receive wakes
when a value arrives, when the channel closes, or when the program is interrupted.

#### What crosses a boundary, and what a task may touch

**Values are deep-copied when they cross between tasks** — both on a channel send and
when a task captures a variable from outside itself. Each side ends up owning its own
memory, which is what keeps one task's region from becoming entangled with another's.

A task may freely **read** anything from the enclosing scope, of any type. A task may
**not change** something it captured — and this holds for a plain number just as much as
for a series:

```cufet
Define data as a series of number with (1, 2, 3).
Define tally as 0.
Pull a rabbit.
    Have rabbit start a task:
        Insert 99 into data.        ← REJECTED when compiled
        The tally becomes tally + 1.   ← REJECTED too, for the same reason
    Done.
Done.
```

The compiler refuses this and points you at channels. The reason is worth knowing: the
task holds its own copy, so the change could never be seen outside — and two tasks doing
it at once is a straightforward data race. Send the result back through a channel, or
`return` it from a named task and await it.

This is about *writing* to a capture, not about captures being restricted. Reading one is
free, and a counter the task defines itself is a local, not a capture:

```cufet
Define step as 5.
Pull a rabbit.
    Have rabbit start a task as run-it:
        Define total as 0.          ← the task's own; fine to change
        Define i as 0.
        While i is less than 4, repeat:
            The total becomes total + step.   ← reads the capture; fine
            The i becomes i + 1.
        Done.
        Return total.
    Done.
    State the awaited result of run-it.
Done.
```
```output
20
```

> ⚠ The interpreter does **not** enforce this — it hands task bodies the live enclosing
> binding, and runs one task at a time, so the mutation appears to work. Write to the
> rule and both backends agree.

#### Interpreted versus compiled

This is the one part of the language where the two backends deliberately differ.
Interpreted, tasks are **cooperative**: one runs at a time, interleaving only at
`Yield.` and at blocking channel operations. Compiled, each task is a **real OS thread**
and they run genuinely in parallel.

So **no particular interleaving is specified**. Write concurrent programs to depend on
the aggregate result, not the order — the same discipline the compiler's own tests use,
which assert order-independent invariants and run under ThreadSanitizer. See
[GRAMMAR.md](GRAMMAR.md) for the cooperative scheduler's specific artefacts, including
why fan-out work-queues do not distribute when interpreted.

---

### Streaming pipes

`producer | consumer.` connects functions into a pipeline. A stage emits with `output
<value>.` and consumes with `for each <name> from the input:`.

```cufet
Bind void to emit-numbers:
    Output 1.
    Output 2.
    Output 3.
    Output 4.
Done.

Bind void to keep-even:
    For each n from the input:
        If n % 2 is 0, output n.
    Done.
Done.

Bind void to show:
    For each n from the input:
        State "kept {n converted to text}".
    Done.
Done.

emit-numbers | keep-even | show.
```
```output
kept 2
kept 4
```

A stage ends its output stream by returning, which tells the next stage downstream that
the values have run out. Stages need no enclosing rabbit — a pipe spawns its stages,
joins them, and cleans up its channels on its own.

Interpreted, a pipe is buffered: each stage runs to completion and the next drains the
buffer. Compiled, every stage is its own thread and values stream through as produced.
The observable output order is the same either way, because each channel is FIFO.

#### Restrictions

- **A pipe is all function stages or all `run` stages** — the subprocess form
  (`run "ls" | run "wc"`, covered under Process execution) cannot be mixed with function
  stages. Doing so is rejected with a message naming the offending stage.
- **A stage function may be used at one input element type** across the whole program.
  Feeding the same function numbers in one pipe and text in another is a type error.

#### How stage types are checked

`for each n from the input:` gives the iterator no type, so a stage's input type can only
come from the stage before it. The type checker walks each pipe left to right, carrying
every stage's output type into the next as its input, and checks that stage's body
against it:

```cufet-refused
Bind void to emit-nums:
    Output 1.
Done.

Bind void to shout:
    For each n from the input:
        State the length of n.      ← type error: 'the length of' works on text only
    Done.
Done.

emit-nums | shout.
```

The consequence to be aware of: **a consumer body is checked at the pipe, not where it is
written.** A stage function never used in a pipe has an unchecked `from the input` body.
And stages reached indirectly — a lambda, or a function held in a variable — stop the
chain, leaving stages after them unchecked rather than wrongly reported.

---

### Signal handling

Cufet provides **cooperative (poll-based) interrupt handling**. When the process
receives `SIGINT` (e.g. Ctrl+C), a flag is set; the program checks it at controlled
points:

```cufet
While 1 is 1, repeat:
    If an interrupt is requested:
        State "shutting down.".
        Acknowledge the interrupt.
        Stop.
    Done.

    Define line as read a line from the input.
    If line is void, stop.
    State line.
Done.
```

- **`an interrupt is requested`** — `fact`; true when a `SIGINT` has arrived
  since the last `Acknowledge the interrupt.` (or since program start). Stays true
  until acknowledged.
- **`Acknowledge the interrupt.`** — statement; clears the pending interrupt flag.
  Subsequent checks return false until the next `SIGINT`.
- **`Yield.`** — yields to the scheduler and acts as an interrupt checkpoint.
  Blocked `the delivery from` and `the awaited result of` also wake on interrupt, so
  a program that yields or blocks naturally is interruptible without polling
  `an interrupt is requested`.

#### How far an interrupt reaches

The polling constructs above behave identically on both backends.

**A program that never mentions interrupts is interrupted for you.** If neither
`an interrupt is requested` nor `Acknowledge the interrupt.` appears anywhere in it,
Ctrl-C stops it: interpreted, every statement is a checkpoint; compiled, the default
signal disposition applies. Either way the program ends and exits 130.

**A program that does mention them is in charge of its own.** Ctrl-C then only sets the
flag, and nothing unwinds until the program polls — which is the entire point of
cooperative handling, and the reason the rule is decided from the whole program rather
than statement by statement. A poll anywhere puts you in charge everywhere.

The practical consequence, if you handle interrupts: a tight loop that contains no
`Yield.`, no blocking call and no poll cannot be interrupted mid-loop. **A second Ctrl-C
always terminates**, so a program in that state is never unkillable from its terminal.

**Compiled, interruption is genuinely preemptive.** The runtime installs a real
`sigaction` handler whose only job is to set a flag (so it is async-signal-safe), and
each thread establishes a landing pad it unwinds to at its next checkpoint. In practice:

| Situation | Interpreted | Compiled |
|---|---|---|
| Loop containing `Yield.` | interruptible | interruptible |
| Tight loop in a program that never mentions interrupts | interruptible — every statement is a checkpoint | interruptible — the default signal disposition applies |
| Tight loop in a program that handles its own interrupts | not interruptible until it polls; a second Ctrl-C terminates | not interruptible until it polls; a second Ctrl-C terminates |
| Blocked on `the delivery from` | interruptible | interruptible — a real blocked thread genuinely wakes |
| Inside a running task | interruptible at its checkpoints | interruptible; the task unwinds, its destructors run, its files close, and the rabbit reaps it at the join |
| Waiting at a rabbit's `Done.` for tasks | n/a | the wait ends as its tasks unwind, and the program then tears down |

An interrupt that is never acknowledged tears the program down cleanly — open files are
flushed and closed, channels freed, regions released — and exits with status 130, the
conventional `128 + SIGINT`.

Handling signals **other than `SIGINT`** is not a language feature on either backend.
Note also that arithmetic faults never arrive as `SIGFPE`: `number` is a software
decimal, so division by zero is a checked condition raised as an ordinary catchable
Cufet exception. See the error-handling section.

---

## Part VIII. Modules

### Modules (`Pull`)

**A module is an object that says it is one, and `Pull` brings it into scope.** That is the
whole idea. A book is a module that ships with the language; there is no privileged
category.

An object declares itself pullable by conforming to `module`:

```cufet
Define object greeting-kit with () and module:
    Bind text to greet, given (the text who):
        Return "hello, " joined to who.
    Done.
Done.

Pull greeting-kit.
    State cast greeting-kit's greet on ("world").
Done.
```

`module` is a **marker** — it requires no methods at all. It exists so that being pullable
is something an author *claims* rather than something every object turns out to have. An
object that does not conform is refused at the pull site, and the message names the fix.

**Articles are noise**, so `Pull greeting-kit.` and `Pull a greeting-kit.` are the same
statement, and the name can be aliased exactly as a rabbit's can:

```cufet-fragment
Pull greeting-kit as kit.
    State cast kit's greet on ("aliased").
Done.
```

This is not new syntax. `Pull a rabbit.` was always this form; what changed is that the
name in it no longer has to be one the language shipped.

**A module may live in another file.** A pull looks for a bundled book, then for a module defined
here, then for `‹name›.cufe` beside the file being run:

```cufet-fragment
Pull a book on greeting-kit.        ← loads greeting-kit.cufe if nothing here is called that
    State cast greeting-kit's greet on ("world").
Done.
```

★ **Either spelling reaches a file.** `Pull a book on ‹name›.` and `Pull a ‹name›.` both load one;
which you write depends on what the thing IS, not on where it lives — see
[Books and modules](#books-and-modules) below.

That file declares its module the same way any file would, and nothing else changes: members are
reached through the name, and the binding ends at `Done.` A module may pull another; a ring of
them is refused, and the message names the ring.

**A file’s top level belongs to that file.** What a loaded file declares beside its module — a
helper function, a constant, a type — is reachable inside that file and nowhere else:

```cufet-fragment
Pull a book on kit.
    State cast kit's use on (1).       ← the module: yours to call
Done.
State cast helper on (10).             ← refused: kit.cufe’s own helper
```

So a book author keeps working material without marking anything private, and two books may
each have a `helper` without colliding. There is no `private` keyword — the file is what hides.

#### What a module carries

**A module may hold TYPE declarations as well as members**, and that is the only way a type crosses
a file boundary — a file's top level is private, and an object body otherwise takes only `Bind`,
`Get` and `Set`:

```cufet-fragment
Define object shapes with () and module:
    Define object point with (the number across, the number up):
        Bind number to sum:
            Return one's across + one's up.
        Done.
    Done.

    Bind point to origin:
        Return a new point { the across 0, the up 0 }.
    Done.
Done.
```

Pulling it brings the type into scope **by its short name**, for the length of the block — the same
way `matrix` arrives with `collections`:

```cufet-fragment
Pull a shapes.
    Define here as a new point { the across 3, the up 4 }.
    State cast here's sum.                                   → 7
Done.

Define there as a new point { the across 1, the up 1 }.      ← refused: not in scope
```

★ **Members are what an object HAS; declarations are what a module CARRIES.** A type is not a
member — it is not reached with `'s` and no interface can require one — so only a module may carry
one, and an ordinary object that tries is refused with a message naming the fix.

⚠ Two modules may each carry a `point` without colliding.

⚠ The files are resolved and compiled **together**, as one program. Cufet has no separate
compilation, and multi-file is about writing a program across files rather than building them
independently.

#### Books and modules

**A book is a module you CONSULT rather than one you have one of** — what another language would
have made a header file. It says so with `and book`, and every book is a module:

```cufet
Pull a book on math.
    State math's pi.
Done.
```

`Pull math.` is refused, and so is `Pull a book on ‹a module you hold one of›.` Pulling is one
mechanism and asks the same question everywhere, but the surface says which KIND of thing you are
pulling. The noun is what reads: *a book on math* is English, *a math* is not.

★ Nothing about the bundled books is privileged here — `math` is a book because it says it is one,
and anybody's module can say the same.

⚠ `Pull books on ‹a›, and ‹b›.` applies one spelling to every name, so all of them must be books.
A book and a module you hold one of are pulled by different words, so they nest rather than
sharing a statement.

**Pulling instantiates.** `Pull a rabbit as hopper.` makes a region rather than naming a shared
one, and a module is the same — so a module with fields is refused, because a pull site has
nowhere to put their values. Build one of those with `a new <type> { … }` instead.

A pull is a **scope**: the binding is gone after `Done.`

### Books, matrices, and foreign source

Books have a reference of their own: **[BOOKS.md](BOOKS.md)**.

- [Books (the standard library)](BOOKS.md#books-the-standard-library) — `math`, `collections`
  and `chance`, and what pulling one gives you
- [Matrix](BOOKS.md#matrix) — a `collections` member, documented with the book it comes from
- [Foreign source (`axiom`)](BOOKS.md#foreign-source-axiom) — `Pull a book on the c-language.`
  is a book with no members: it admits C source held as a value

---

## Part IX. Compiling

### Compiling to a native binary

Cufet runs two ways. Interpreted, it executes directly. Compiled, it emits C, invokes
`gcc`, and produces a native executable with no managed runtime — no .NET, no garbage
collector, no interpreter loop.

```
cufet program.cufe                  run it (interpreted)
cufet build program.cufe            compile to a native binary
cufet emit-c program.cufe out.c     emit the C, without invoking gcc
```

`build` requires **`gcc` on your `PATH`**. Nothing else — the generated C is
self-contained, with no external libraries and no flags beyond `-O2`, `-pthread`
and `-lm`.

**A file with nothing to run is refused, by both verbs.** Cufet has no `main` — the top-level
statements are the program — so a file whose top level is only declarations would start and finish
having done nothing:

```
$ cufet build terminal.cufe
build: 'terminal.cufe' declares things but never does anything — there is nothing to run.
  Every item at its top level is a declaration, so the program would start and finish
  having done nothing. A file like this is a library: pull it from the program you are
  building, and build that.                                              ← exit 2
```

`check` still accepts such a file, and that is deliberate: checking a library is exactly what a
library author wants, and a book is compiled as part of whatever pulls it. The mistake is in asking
that file to BE a program, never in the file.

**Builds are always optimized.** There is no debug build and no `--release`: a
compiled Cufet program is the fast one, every time. If you need an unoptimized
build — stepping through the generated C in a debugger, say — use `emit-c` and
compile it yourself with whatever flags you want.
`emit-c` is the escape hatch for cross-toolchain builds and for reading what was
generated.

**The runtime is a separate translation unit.** `emit-c program.cufe out.c` writes
three files — `out.c` (your program), plus `cufet-runtime.c` and `cufet-runtime.h`
beside it. Compile them together anywhere:

```
gcc out.c cufet-runtime.c -o program -pthread -lm
```

Keeping the runtime out of `out.c` is what makes that file readable: it used to be
about 950 lines of runtime before your program started. `build` compiles the runtime
once and caches the object under your user cache directory (`%LOCALAPPDATA%\cufet` on
Windows, `$XDG_CACHE_HOME/cufet` or `~/.cache/cufet` elsewhere; `CUFET_CACHE_DIR`
overrides it), so later builds only compile your program. **The cache is never
required** — if it cannot be written, the runtime is compiled alongside your program
and the build succeeds anyway.

The lexer, parser, and type checker are shared, so a program that type-checks does so
identically either way, and every error message you have seen in this document is the
same in both.

**Checking native compatibility ahead of time** — `cufet check --native` runs the code
generator without invoking `gcc` and reports what it **refuses**, as warnings, since those
programs still interpret:

```
cufet check --native program.cufe
```

> ★ **A clean `--native` is not a promise the build will succeed.** It reports refusals,
> and a refusal is the code generator saying so out loud. It cannot report a defect *in*
> the generator — code that is emitted but does not compile — because from the generator's
> side that looks like success. `cufet build` is the only thing that proves a program
> builds, because only `gcc` reads the result.
>
> If `build` fails on the generated C, that is a **bug in Cufet, not in your program** —
> every line `gcc` saw was written by the compiler — and it says so, with `emit-c` to get
> the generated source to report alongside it.

#### What you get

A compiled program is an ordinary native executable. Its sections are real: functions
you `Bind` become machine symbols, a text value bound `permanently` sits in read-only
data, and each thread's region stack is thread-local storage.

`number` compiles to an exact software decimal that is bit-for-bit identical to the
interpreter's, so arithmetic does not quietly change meaning when you compile. Regions
become real bump-allocated arenas. Tasks become real threads.

#### Where the two differ

Compiled output is expected to equal interpreted output — that is enforced by the test
suite, which compiles each program, runs the binary, and compares. Where they disagree,
one of them is a bug rather than a documented difference.

The exceptions are few and each is noted where it arises:

- **Concurrency scheduling** — cooperative when interpreted, genuinely parallel when
  compiled. No interleaving is specified.
- **`power` with a fractional exponent** may differ in its last digit; the underlying
  routine is the platform's own.
- **Seeded randomness** is reproducible within a backend, not across them.
- **Filesystem enumeration order** and **ASCII-versus-locale casing** are
  platform-owned.

#### Platform notes

Concurrency, subprocesses, and signal handling use POSIX facilities and need Linux,
macOS, or WSL. On Windows with mingw, programs that avoid those features compile and run
normally; programs that use them will not build.
