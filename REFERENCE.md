# Cufet Language Reference

The complete reference for Cufet `0.9.0`. For a quick introduction and taste of
the language, see [README.md](README.md). For what's planned and the reasoning
behind the design, see [ROADMAP.md](ROADMAP.md).

---

## Contents

- [Cufet Language Reference](#cufet-language-reference)
  - [Contents](#contents)
  - [Statements](#statements)
  - [Constants](#constants)
  - [Arithmetic](#arithmetic)
  - [Facts (boolean literals)](#facts-boolean-literals)
  - [Comparisons](#comparisons)
  - [Logic](#logic)
  - [Conditionals](#conditionals)
  - [Loops](#loops)
    - [For-each loops](#for-each-loops)
  - [Scope](#scope)
  - [Series (collections)](#series-collections)
  - [Text](#text)
  - [Range](#range)
  - [Records](#records)
  - [Objects](#objects)
    - [Embedding (composition)](#embedding-composition)
    - [Interfaces (polymorphism)](#interfaces-polymorphism)
    - [Methods defined outside the object body (`unto`)](#methods-defined-outside-the-object-body-unto)
    - [Getters and setters](#getters-and-setters)
    - [Named constructors](#named-constructors)
    - [Destructors](#destructors)
  - [Functions](#functions)
    - [Closures](#closures)
    - [Lambda literals (anonymous functions)](#lambda-literals-anonymous-functions)
  - [Voidable values (`void` and `voidable T`)](#voidable-values-void-and-voidable-t)
  - [Union types and narrowing](#union-types-and-narrowing)
    - [`is a <type>` / `is not a <type>`](#is-a-type--is-not-a-type)
    - [In-branch narrowing](#in-branch-narrowing)
  - [Error handling (failures and exceptions)](#error-handling-failures-and-exceptions)
    - [Failure values (`failure T`)](#failure-values-failure-t)
    - [Block form: `Try to`](#block-form-try-to)
  - [Maps](#maps)
  - [Catalogue and atlas (heterogeneous collections)](#catalogue-and-atlas-heterogeneous-collections)
  - [Input and output](#input-and-output)
    - [Reading from standard input](#reading-from-standard-input)
    - [File I/O](#file-io)
    - [Process execution](#process-execution)
    - [Environment variables](#environment-variables)
    - [Directory traversal](#directory-traversal)
  - [Signal handling](#signal-handling)
  - [Type system](#type-system)
  - [Identifiers](#identifiers)

---

## Statements

| Syntax | Meaning |
|---|---|
| `State expr.` | Print a value |
| `Define name as expr.` | Declare a variable (error if already defined) |
| `name becomes expr.` | Reassign a variable (error if not declared) |

Articles (`a`, `an`, `the`) are noise everywhere — `Define the total as 0.` and
`Define total as 0.` are identical.

Keywords are case-insensitive (`Cast`, `cast`, and `CAST` are the same).
Identifiers are not (see [Identifiers](#identifiers)).

---

## Constants

`Define name as value permanently.` — the trailing adverb locks the binding:

```
Define max-retries as 3 permanently.
Define pi as 3.14159 permanently.
Define greeting as "Hello!" permanently.
```

A permanent binding can never be reassigned — `max-retries becomes 4.` is a
static type error that names both the declaration line and the violation.

**Shallow by construction:** `permanently` fixes the *binding*, not the
*contents*. A permanent series or map can still add and remove elements; a
permanent object can still mutate its fields — those operations go through
`Add`/`Remove`/field-set, not `becomes`, so they are not touched by the
constant rule. Only `becomes` on the name itself is locked.

---

## Arithmetic

Standard `+ - * / %` with `()` and conventional precedence. Unary `-` supported.
Uses `decimal` — no floating-point surprises.

`%` is modulo (remainder). Binary `-` requires surrounding whitespace to
distinguish it from a dash inside an identifier: `a - b` is subtraction,
`a-b` is one identifier.

Results print in their minimal form regardless of scale picked up along the
way — `1.5 + 0.5` displays as `2`, not `2.0`.

---

## Facts (boolean literals)

`true` and `false` are **keywords** that produce `fact` values — the boolean type.
They work exactly like number or text literals: anywhere a `fact` is valid.

```
Define flag as true.
Define done as false.
return true.
return false.
If result is false, State "failed".
While keep-going is true, repeat: ... Done.
Send true through ch.             ← channel of fact
Define b as (x > 5).              ← comparison also produces a fact
```

`fact` is the type produced by comparisons, logic operators, `contains`, `has a key
for`, `an interrupt is requested`, and `a random guess` — `true`/`false` are simply
the literal forms of that same type.

---

## Comparisons

Both **symbol forms** and **word forms** work in both expression position and
condition position — they are the same operation (compare, produce a `fact`).

**Symbol forms** (`=` `<` `>` `<=` `>=`) — terse, math-style:
```
State 3 > 1.              → true
State 1 = 1.              → true
If x < 10, State "small".
While count < bound, repeat:
Define big as x > 100.
```

**Word forms** (`is`, `is not`, `is greater than`, `is less than`, `is N or more`,
`is N or less`) — verbose, sentence-style:
```
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

## Logic

`and`, `or`, and `not` combine conditions. These are always words (no `&&`/`||`/`!`).

```
If x is greater than 0 and x is less than 10, state "in range".
If x is 0 or x is 100, state "edge".
If not (x is 5), state "not five".
```

Conventional precedence: `not` binds tightest, then `and`, then `or`; all looser
than comparisons. Evaluation short-circuits (`and` skips its right side if the
left is false; `or` skips if the left is true).

---

## Conditionals

**Inline — comma, one statement, works anywhere:**
```
If x is 1, state "one".

If x is 1, state "one". Otherwise, state "other".

If x is 1, state "one".
Otherwise if x is 2, state "two".
Otherwise, state "other".
```

**Block — colon, `Done.`-closed, any number of statements:**
```
If x is 1:
    State "one".
    State "also one".
Done.

If x is 1:
    State "one".
Done.
Otherwise:
    State "other".
Done.
```

Comma after the condition → inline single statement. Colon after the condition →
`Done.`-terminated block.

---

## Loops

```
While x is less than 10, repeat:
    x becomes x + 1.
Done.

Repeat:
    x becomes x + 1.
until x is 10 or more.
```

`Stop.` breaks the innermost loop. `Skip.` continues to the next iteration. Both
are parse errors outside a loop.

### For-each loops

```
For each score in scores, repeat:
    State the score.
Done.
```

```
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

## Scope

Every `Done.`-bounded block — an `If` arm, a `While` body, a `For each` body, a
`Repeat...until` body, a function body — introduces a **lexical scope**. Names
declared inside a block do not exist outside it.

**Inner blocks can freely read and modify outer variables:**
```
Define x as 10.
If x is greater than 5:
    x becomes 20.         ← modifies the outer x
Done.
State x.                  → 20
```

**Inner declarations are local — they do not leak out:**
```
Define x as 10.
If x is greater than 5:
    Define y as 99.       ← y lives only inside this block
Done.
State y.                  ← error: y isn't defined here
```

**Shadowing an outer name via `Define` is a static error by default:**
```
Define x as 10.
If x is greater than 5:
    Define x as 99.       ← TypeException: x already exists in an enclosing scope
Done.
```

**Deliberate shadowing requires the `shadow` keyword:**
```
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
```
Define n as 7.
For each n in range 1 to 3, repeat:
    State n.              ← 1, 2, 3  (the iterator)
Done.
State n.                  → 7  (outer n is restored)
```

**`Try` handler bindings** (`the failure`, `the exception`) are also block-local
to their respective handler bodies.

---

## Series (collections)

Ordered, homogeneous collections.

**Literals:**
```
Define scores as a series with (90, 85, 70).
Define tags   as a series of text with ("sedan", "coupe").
Define ops    as a series of number function given (the number) with (double, triple).
```

The element type is inferred from the elements, or declared explicitly after
`of`. Empty series require an explicit annotation:
```
Define log    as a series of text.
Define counts as a series of numbers.
Define fleet  as a series of records like (the text make, the number year).
```

**Access:**
```
State the first of scores.
State the third of scores.
State the last of scores.
State item 2 of scores.
State item n of scores.        ← n is any expression
```

**Length:**
```
State the number of scores.
```

**Mutation:**
```
Add 100 to scores.                         ← append
Add 100 to the start of scores.            ← prepend
Add 100 after the second item of scores.   ← insert after position
Add 100 after item n of scores.

Remove the first item from scores.         ← by position
Remove item n from scores.
Remove 85 from scores.                     ← by value (first occurrence)
```

**Element assignment:**
```
The first of scores becomes 100.
The item n of scores becomes 100.
The last of scores becomes 100.
```

Out-of-bounds access or assignment produces a readable runtime error.

Series are **reference-typed** — assigning a series to a new name shares it
(in contrast to records and objects, which copy). See
[Type system](#type-system).

---

## Text

`text` values are joined, measured, and built from other values with explicit
constructs. `+` is **not** overloaded for concatenation — joining has its own
word, and converting a non-text value to text is always explicit (no hidden
coercion).

**Joining** — `joined to`, left-associative, chains:
```
Define greeting as "hello" joined to " world".          → "hello world"
Define full-name as first joined to " " joined to last.
```
Both sides must be `text`. Joining a non-text value directly is a static type
error — convert it first.

**Converting to text** — `converted to text`, a postfix construct:
```
State "Player: " joined to score converted to text.     → "Player: 95"
Define label as score converted to text.                → "95"
```
Works on numbers and facts (both total — every number and fact has a text form).
It binds tighter than `joined to`, so `"x: " joined to n converted to text`
reads as `"x: " joined to (n converted to text)`.

> **Note (precedence quirk):** in a named-access position, `the value of person
> converted to text` parses as `the value of (person converted to text)`. If you
> need the conversion to apply to the *result* of the access, extract it first:
> `Define v as the value of person. State v converted to text.`

**Length** — `the length of`, the character count:
```
State the length of "hello".          → 5
Define n as the length of greeting.
```

**Converting text to number** — `converted to number`, the inverse of
`converted to text`:
```
Define n as "42" converted to number.
If n is not void:
    State n.                          → 42
Done.
Otherwise:
    State "not a number".
Done.

Define m as ("abc" converted to number but void is 0).      → 0
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
```
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
```
If "hello" contains "ell", state "yes".          → yes
```

**Finding a position** — `the position of <substring> in <text>`, **1-based**:
```
Define p as the position of "ell" in "hello".    → 2
Define q as the position of "z" in "hello".      → void
```
Returns the position of the first occurrence, or `void` if the substring
isn't present — a `voidable number`, handled like any other voidable. There's
no `-1` sentinel.

**Substring** — four forms, all **1-based and inclusive**, always returning
plain `text` (never voidable — out-of-range inputs clamp rather than fail):
```
State the characters from 2 to 4 of "hello".         → "ell"
State the first 3 characters of "hello".             → "hel"
State the last 3 characters of "hello".               → "llo"
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
```
Define s as replace "a" with "X" in "banana".         → "bXnXnX"
Define deleted as replace "x" with "" in "axbx".       → "ab"
```
An empty replacement is deletion (allowed). An empty target is a static error
(when literal — runtime otherwise) — replacing "nothing" is meaningless, the
same reasoning as `split by`'s empty-delimiter error. If `<old>` isn't found,
the text comes back unchanged.

**Case** — `<text> in uppercase` / `<text> in lowercase`:
```
State "Hello" in uppercase.        → "HELLO"
State "Hello" in lowercase.        → "hello"
```
Uses default (invariant, culture-independent) case rules — not locale-sensitive.
Only upper/lower this slice; title-case and capitalize-first are deferred.

> **Note:** `in` is also used to lead the map-set statement (`In ages, the
> entry for "x" becomes ...`) and inside `the entry for K in M` / `the
> position of S in T`. These don't collide: `in uppercase`/`in lowercase` is
> only recognized when `in` is immediately followed by `uppercase` or
> `lowercase` — any other use of `in` is left alone for its own construct.

**Trimming** — `<text> trimmed`, strips whitespace from both ends:
```
State " hello " trimmed.           → "hello"
```
Standard whitespace (spaces, tabs, newlines). Leading-only / trailing-only
trim are deferred. All three operations chain naturally with each other and
with the rest of the text toolkit: `raw trimmed in uppercase`.

---

## Range

`range <start> to <end>` produces a materialized `series of number` — sugar so
you don't build a numeric span by hand:
```
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
```
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

## Records

Anonymous, structural data — a bundle of named and/or positional fields.

**Construction:**
```
Define car as a record with ("hatchback", the make "Honda", the year 2021).
```

Positional fields come first; named fields (introduced with `the`) come after.
Mixed order is a parse error.

**Access:**
```
State the first of car.             ← positional: "hatchback"
State the make of car.              ← named: "Honda"
State the make of the spare of car. ← chained / nested access
```

**Mutation:**
```
The make of car becomes "Toyota".         ← named field
The first of car becomes "coupe".         ← positional ordinal
The item n of car becomes "coupe".        ← positional parametric
```

Assigning the wrong type to a field is a static type error.

**Value semantics** — records copy on assignment; assigning a record to a new
name gives an independent copy:
```
Define truck as car.
The make of truck becomes "Toyota".
State the make of car.              → Honda    (unchanged)
```

**Records in function annotations:**
```
Bind text to make-of, given (the record vehicle with (text, the text make)):
    Return the make of vehicle.
Done.
```

**Series of records:**
```
Define fleet as a series with (
    A record with (the make "Honda",  the year 2021),
    A record with (the make "Toyota", the year 2019)).

Define inventory as a series of records like (the text make, the number year).
Add a record with (the make "Ford", the year 2022) to inventory.
```

A populated series infers its shape from the elements; an empty one declares it
with `like (...)`. Either way, `add` enforces structural matching.

**Equality:**
```
If rec1 is rec2, state "same".
If rec1 is not rec2, state "different".
```
Two records are equal iff all fields are equal by value — named fields compared
by name (order-insensitive), positional fields by position, recursively. Records
of different shapes can't be compared: a compile-time type error. Series fields
compare element-wise by value.

---

## Objects

Named, nominal types that bundle data with behavior. Where records are *data*
(interchangeable by shape), objects are *things* (identity by name).

**Definition:**
```
Define object vehicle with (the text make, the number year).
```

With methods:
```
Define object vehicle with (the text make, the number year):
    Bind void to describe:
        State one's make.
    Done.
Done.
```

Inside a method body, `one` refers to the receiver object (`one's make`).

**Instantiation** — `{}` literal:
```
Define car as a new vehicle { the make "Honda", the year 2021 }.
```

**Access:**
```
State car's make.               ← possessive: "Honda"
State the make of car.          ← named: "Honda"
State the first of car.         ← positional: "Honda"
```

**Method dispatch:**
```
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
```
If car1 is car2, state "same car".
If alice is not bob, state "different people".
```
Two objects are equal iff they are the same type and all fields are equal by
value — including all promoted (embedded) fields, compared recursively through
the embedding chain. Objects of different types can't be compared: a
compile-time type error.

### Embedding (composition)

An object can embed another and promote its fields and methods — composition
that gives the convenience of reuse without inheritance:

```
Define object customer with (the number balance) and as a person.
```

`customer` embeds a `person` and promotes its members. Access reaches through
automatically (transitively, through any chain):
```
State the name of customer.             ← reaches the embedded person's name
Cast greet on customer.                 ← reaches the embedded person's method
```

Construction is **flat** — the object's own fields and all promoted fields are
supplied together in one `{...}`:
```
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

### Interfaces (polymorphism)

An interface is a contract — a set of method signatures an object must have.
It provides polymorphism without a hierarchy.

```
Define driver as an interface for {
    The void function steer, given (the number angle),
    The void function brake,
    The void function accelerate, given (the number amount)
}.
```

A single-method interface may drop the braces:
```
Define greeter as an interface for the void function greet, given (the text name).
```

An object declares conformance explicitly, and it is statically enforced:
```
Define object street-racer with (the text name) and driver.
```

An interface name is usable as a type — a parameter typed by an interface
accepts any conforming object:
```
Bind void to take-lap, given (the driver racer):
    Cast steer on (racer, 90).
    Cast accelerate on (racer, 100).
Done.
```

Conformance **is a flat compile-time check, not subtyping** — no variance is
introduced; objects do not become subtypes of one another.

### Methods defined outside the object body (`unto`)

A method can be declared *outside* its object's definition — attached with
`unto <type>` — for code organization (grouping related methods elsewhere,
splitting a large type across locations). It is **identical in every way**
to a method nested in the definition; only the declaration location differs:

```
Define object person with (the text name, the number age).

Bind void to greet unto person:
    State "Hi, I'm " joined to one's name.
Done.

Bind number to birthday unto person:
    one's age becomes one's age + 1.
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
- **Your own object types only.** `unto` on an undefined name, or on
  something that isn't an object type (e.g. an interface), is a static
  error. This is not foreign-type extension and not overloading.
- **Method names are unique per type, regardless of where declared.** A name
  clash between a nested method and an `unto` method (or between two `unto`
  methods) on the *same* type is a static error. The same name `unto`
  *different* types is fine — there's no shared namespace across types.
- **Satisfies interface conformance** exactly as a nested method would — the
  conformance check looks for "does the type have a matching method?", not
  "where was it declared?"

### Getters and setters

A **getter** is a computed read-only property. Callers access it exactly like a
stored field — no distinction at the call site (Dart-style uniform access):

```
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

```
Define object temp-sensor with (the number celsius):
    Set display given (the number v):
        one's celsius becomes v.
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

### Named constructors

A named constructor is a function that builds and returns an object. It is declared
with `making a <type>` in the return-type slot:

```
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

### Destructors

A destructor runs automatically when an object goes out of scope — RAII at the
`Done.` that closes its declaring block:

```
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
- **`one` is the object being destroyed** — its fields and methods are accessible
  via `one's <field>` and `Cast <method> on one`.
- **Ownership rule** — destroy what you opened, not what you borrowed. A resource
  passed in from outside is the caller's responsibility; closing it in the destructor
  is a double-close bug.

---

## Functions

**Declaration:**
```
Bind number to add, given (the number a, the number b):
    Return a + b.
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
```
State cast add on (3, 4).          ← in expression position
Cast greet on ("hello").           ← as a statement (void or discarded result)
```

`Cast` works on any expression that evaluates to a function — a name, a variable,
a series element, or a method.

**Early exit:**
```
Return value.    ← return a value
Return.          ← void early exit
```

A non-`void` function must return a value on every path; one that can fall off
its end without returning is a compile-time error.

**Functions are first-class values:**
```
Define op as add.
State cast op on (3, 4).           → 7
```

A function assigned to a variable carries its full type. The type checker catches
calling the wrong signature through any alias.

**Function-typed parameters:**
```
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
```
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
```
Define ops as a series of number function given (the number) with (double, triple).

State cast the first of ops on (5).          → 10

For each op in ops, repeat:
    State cast op on (5).
Done.
```

A series whose element type is a function type. All the usual series operations
apply — access, add, remove, for-each — and any accessed element can be `Cast`
directly.

### Closures

A function declared with `Bind` *inside* another function or method body
captures the enclosing variables at the point of declaration:

```
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

### Lambda literals (anonymous functions)

A function literal written inline, with no name — usable anywhere a function
value goes: assigned, passed as an argument, returned, or stored in a series.

```
Define double as a function given (the number x): Return x * 2. Done.
State cast double on (5).                                       → 10

Cast apply on (10, a function given (the number x): Return x * 2. Done).
```

The body is always `Done`-terminated (there's no inline single-statement
form). The return type is **inferred from the body** — there's no syntax to
declare it. Lambdas capture enclosing variables under the same rule as
[Closures](#closures) above, and always carry their captured environment.

---

## Voidable values (`void` and `voidable T`)

Cufet has no null. Absence is expressed with a first-class empty value, `void`,
and a type that admits it, `voidable T`. This is how "a value, or nothing" is
said — explicitly, and checked.

**`void`** is a real, holdable value (it prints as `void`). A `void`-returning
function produces it; a map lookup that misses produces it.

**`voidable T`** is "a `T`, or `void`":
```
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
```
If maybe-score is not void:
    State maybe-score.            ← narrowed to a plain number here, safe to use directly
Done.
Otherwise:
    State "no score".
Done.
```
Inside a branch that has checked a **variable** is not void, the checker narrows
that variable to its plain `T`, so it can be used directly. Narrowing is keyed on
the variable and is cleared if the variable is reassigned within the branch.

> To narrow a value produced by an expression (like a map lookup), name it first
> — `Define s as the entry for "alice" in ages.` then check `s`. A bare literal
> buried inside a lookup is a value worth naming anyway; narrowing follows the
> named binding.

`but void is <default>` — an inline fallback that always yields a plain `T`:
```
Define n as (the entry for "alice" in ages but void is 0).
```

---

## Union types and narrowing

A **union type** is a value that can be one of several listed types. Declared
with `or` in parentheses:

```
Define x as (number or text).
Define y as (number or text or fact).
```

**Type-agnostic operations** — without narrowing, only operations that work on
every case are allowed: assignment, `becomes`, passing to a union-typed parameter,
storing into a catalogue or atlas, and equality comparison (`is`/`is not`) between
two values of the same union type.

**Type-specific operations** — arithmetic, `the length of`, and anything that
only makes sense for one type — require narrowing first. Using them on an
un-narrowed union is a static type error that names the expected narrowing form.

### `is a <type>` / `is not a <type>`

The runtime type-test, generalizing `is void`:

```
If x is a number, state x + 1.
If x is not a text, state "not text".
```

Works with any type name: `is a number`, `is a text`, `is a fact`, object type
names, etc. `is an <type>` is accepted wherever the article fits. Both forms
are identical.

### In-branch narrowing

After a successful `is a <type>` check, the value is that type inside the
branch — type-specific operations are legal there:

```
Define x as (number or text).
x becomes 42.

If x is a number:
    State x + 1.           ← x is a number here; arithmetic is legal
Done.
Otherwise:
    State the length of x. ← x is a text here (narrowed by elimination)
Done.
```

**Narrowing by elimination** — for a **closed** union, the `Otherwise` arm
automatically narrows to the remaining case(s). After `if x is a number` on a
`(number or text)` union, `Otherwise` knows `x` is `text`.

For a three-case union, two tested arms leave the third for `Otherwise`:

```
Define x as (number or text or fact).
x becomes 42.

If x is a number:
    State x + 1.
Done.
Otherwise if x is a text:
    State the length of x.
Done.
Otherwise:
    State x converted to text.    ← x is a fact here
Done.
```

**`is not a <type>`** narrows the true branch to the complement — for a
`(number or text)` union, `if x is not a number` narrows `x` to `text` in the
true branch.

**Open unions** — `Otherwise` after an open union check is *not* narrowable;
only agnostic operations are legal there. Open is sound (narrowing still
required), never `any`.

Narrowing is **variable-level** — the same rule as voidable narrowing. The
narrowed type clears when the variable is reassigned. To narrow a value produced
by an expression, name it first.

---

## Error handling (failures and exceptions)

Cufet distinguishes two kinds of bad outcome:

- **Failures** — expected, recoverable outcomes that are part of a function's
  contract. A file not being found is a failure. A config value being invalid is
  a failure. These are things a caller should plan for.
- **Exceptions** — unexpected outcomes the type system can't prevent at compile
  time. Divide-by-zero is an exception. An out-of-bounds access with a runtime
  index is an exception. These are things that should not happen in correct code.

The two paths are handled separately and cannot be mixed up.

### Failure values (`failure T`)

`failure T` is "either a plain `T` or a failure." A failure carries a text
message and an optional category tag. The parallel to `voidable T` is exact:
same inline-fallback syntax, same propagation operator, same block form.

**Failure literal:**
```
Define err as a failure "not found" of category "not-found".
Define err as a failure "something went wrong".       ← category is optional
```

**Inline fallback — `but on failure <default>`** — collapses `failure T` to
plain `T`, like `but void is` for voidable:
```
Define n as (cast parse-int on (raw) but on failure 0).
```

**Propagation — `or pass the failure off`** — re-raises the failure to the
caller. The function must itself declare a failable return type:
```
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

### Block form: `Try to`

For multiple statements that may produce failures, `Try to:` handles them as a
group:

```
Try to:
    Define contents as read all from the file "data.txt".
    State contents.
Done.
In case of failure:
    State "could not open file: " joined to the message of the failure.
Done.
```

Inside `In case of failure:`, `the failure` is bound to the failure value.
Access its fields with named access:
```
In case of failure:
    State the message of the failure.
    State the category of the failure.    ← text, or void if no category was given
Done.
```

**`In case of exception`** — catches runtime exceptions (divide-by-zero,
dynamic out-of-bounds, etc.) that the type system can't statically prevent:
```
Try to:
    State 1 / 0.
Done.
In case of exception (the exception):
    State "runtime error: " joined to the exception.    ← bound as text
Done.
```

The name in parentheses (`the exception` in the example above) is the binding
for the exception description — it is block-local to the handler.

Exceptions **re-raise by default** after the handler runs. `Suppress.` (only
valid inside `In case of exception`) swallows the exception and continues
execution after the `Try`:
```
In case of exception (the exception):
    State "ignoring: " joined to the exception.
    Suppress.
Done.
```

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

## Maps

Typed key→value collections. Keys are one type, values are one type
(homogeneous, like series).

**Type:** `a map from text to number` — text keys, number values. Keys are
`number` or `text`.

**Construction:**
```
Define ages as a map with ("alice" : 30, "bob" : 25).          ← populated, inferred types
Define ages as a map from text to number with ("alice" : 30).  ← populated, typed
Define ages as a map from text to number with ().               ← empty, typed
```

**Lookup** — returns `voidable <value-type>` (the key might be absent):
```
Define alice-age as the entry for "alice" in ages.        ← a voidable number
Define alice-age as from ages, the entry for "alice".     ← leading-from form
```

**Set** — reuses `becomes`:
```
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
```
Define ages as a map from text to voidable number with ().
In ages, the entry for "alice" becomes 30.
In ages, the entry for "bob" becomes void.        ← present key, void value
```
A lookup always returns a **flat** `voidable V` — never `voidable voidable V`,
even when the map's value type is already voidable. The nesting never surfaces.
This means a plain lookup can't tell "key absent" apart from "key present, but
its value is void" — both produce `void`. Distinguish them with `has a key`
first, then look up:
```
If ages has a key for "bob":
    Define v as the entry for "bob" in ages.      ← void here means the VALUE is void
    If v is not void:
        State v.
    Done.
    Otherwise:
        State "present, but void".
    Done.
Done.
Otherwise:
    State "no such key".
Done.
```

**Remove and size:**
```
Remove "alice" from ages.
State the size of ages.
```

**Iterate** — each element is a `mapping` (a key/value pair):
```
For each mapping in ages, repeat:
    State the key of mapping.
    State the value of mapping.
Done.
```

Maps are **reference-typed** (like series).

**The canonical lookup pattern** — name the lookup, check it, use it:
```
Define alice-age as the entry for "alice" in ages.
If alice-age is not void:
    State alice-age.
Done.
Otherwise:
    State "Sorry, no entry.".
Done.
```

---

## Catalogue and atlas (heterogeneous collections)

**Catalogue** — a series whose element type is a union: a heterogeneous ordered
collection.

**Closed** (declared element type):
```
Define items as a catalogue of (number or text) with (42, "hello").
Define items as a catalogue of (number or text).    ← empty
```

**Open** (any element type):
```
Define items as a catalogue with (42, "hello", (1 = 1)).
Define items as a catalogue.                         ← empty open catalogue
```

Retrieval yields a union value — narrow before using type-specifically:
```
Define first as the first of items.
If first is a number:
    State first + 1.
Done.
Otherwise:
    State the length of first.
Done.
```

All series operations apply: ordinal and parametric access, `the number of`,
`Add`, `Remove`, `for each`, element assignment. `Add` enforces the declared
union type; adding a value outside the union is a static type error. Open
catalogues accept any element type.

---

**Atlas** — a map whose value type is a union: a heterogeneous typed key→value
collection.

**Closed** (declared key and value type):
```
Define mp as an atlas from text to (number or text) with ("x" : 42, "y" : "hello").
Define mp as an atlas from text to (number or text).    ← empty
```

**Open** (any key or value):
```
Define mp as an atlas.
```

Retrieval yields a `voidable (union)` — the absent-key void composes with the
union value type:
```
Define v as the entry for "x" in mp.          ← voidable (number or text)
If v is not void:
    If v is a number:
        State v + 1.
    Done.
Done.
```

All map operations apply: `the entry for`, `has a key for`, `has an entry for`,
`becomes` (set), `Remove`, `the size of`, `for each`. Value-setting enforces the
declared union type; the open atlas accepts any value.

Atlases are **reference-typed** (like maps and series).

---

## Input and output

### Reading from standard input

The pre-defined name `input` holds standard input as a `readable stream of
text`. Three read forms cover common patterns:

```
Define line  as read a line from the input.      ← voidable text (void at EOF)
Define all   as read all from the input.         ← text (empty string at EOF)
Define lines as read all lines from the input.   ← series of text (empty at EOF)
```

`read a line from the input` strips the trailing newline and returns
`voidable text` — `void` signals end-of-input. The typical read loop:
```
Repeat:
    Define line as read a line from the input.
    If line is void, stop.
    State line.
until false.
```

(`until false` is the standard idiom for a loop that exits only via `Stop.`)

`read all from the input` drains all of stdin and returns it as one `text`
value (empty input → `""`; never void). `read all lines from the input`
splits on newlines and returns a `series of text` (empty input → empty series).

### File I/O

**Reading an entire file** — returns a failable value; must be handled:
```
Try to:
    Define text as read all from the file "notes.txt".
    State text.
Done.
In case of failure:
    State "could not read: " joined to the message of the failure.
Done.
```

`read all from the file <path>` returns `text or failure`.
`read all lines from the file <path>` returns `series of text or failure`.
The path is any text expression (literal, variable, or interpolated string).

Failure categories: `"not-found"`, `"permission-denied"`, `"disk-error"`.

**Writing to a file:**
```
write "hello\n" to the file "out.txt".      ← overwrite (create or truncate)
append "more\n" to the file "out.txt".      ← append to end
```

Write and append complete silently on success; on failure they raise a Cufet
failure caught by the enclosing `Try` handler.

**Scoped file streams — `With the file ... open for reading/writing as`:**

For reading line-by-line, or writing incrementally, open the file as a stream
and let Cufet close it automatically:

```
With the file "data.txt" open for reading as stream:
    Define line as read a line from stream.
    State line.
Done.
```

```
With the file "out.txt" open for writing as log:
    write "Line 1\n" to log.
    write "Line 2\n" to log.
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
```
With the file "lines.txt" open for reading as s:
    Define lines as read all lines from s.
    For each line in lines, repeat:
        State line.
    Done.
Done.
```

**Passing a stream to a function:**
```
Bind void to process, given (the readable stream of text src):
    Define line as read a line from src.
    State line.
Done.
```

### Process execution

`run <program>` runs an external program synchronously and collects its output:

```
Try to:
    Define result as run "git" with arguments ("log", "--oneline", "-5").
    State result's output.
    If result's exit-code is not 0:
        State "stderr: " joined to result's errors.
    Done.
Done.
In case of failure:
    State "git not available".
Done.
```

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

> **Note:** `result's exit-code converted to text` mis-parses (a parser quirk
> with `converted to text` in possessive-access position). Workaround: extract
> first — `Define code as result's exit-code. State code converted to text.`

### Environment variables

`the environment variable "NAME"` reads a process environment variable by name,
returning `voidable text`:

```
Define home as the environment variable "HOME".
If home is not void:
    State "home is " joined to home.
Done.
Otherwise:
    State "HOME is not set".
Done.

Define path-val as the environment variable "PATH" but void is "".
```

- Returns `voidable text` — `void` if the variable is not set.
- The name is any text expression (literal, variable, or interpolated string).
- Read-only — Cufet does not expose setting environment variables.

### Directory traversal

**List a directory** — `the contents of the directory path` returns the names of
entries (files and subdirectories) inside the directory as a `series of text or
failure`. Entry names are plain names, not full paths. Order is not guaranteed.

```
Try to:
    Define entries as the contents of the directory "/tmp".
    For each name in entries, repeat:
        State name.
    Done.
Done.
In case of failure:
    State "cannot read: " joined to the message of the failure.
Done.
```

Failure categories: `"not-found"`, `"permission-denied"`.

**Path existence and kind tests** — three boolean predicates (all return `fact`,
never fail, never void):

```
If the path "/tmp/myfile" exists:
    If the path "/tmp/myfile" is a file:
        State "regular file".
    Done.
    Otherwise if the path "/tmp/myfile" is a directory:
        State "directory".
    Done.
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

## Signal handling

Cufet provides **cooperative (poll-based) interrupt handling**. When the process
receives `SIGINT` (e.g. Ctrl+C), a flag is set; the program checks it at controlled
points:

```
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
- **`Yield.`** — cooperative scheduler yield and interrupt checkpoint (v0.9.0).
  The scheduler checks the interrupt flag at each dequeue; blocked `the delivery
  from` and `the awaited result of` also wake on interrupt. Programs that `Yield.`
  naturally are interruptible without polling `an interrupt is requested`.
- **Cooperative, not fully preemptive.** A tight loop with no `Yield.` and no
  blocking calls cannot be mid-loop interrupted. True preemption is deferred to
  the native era (requires OS-thread infrastructure).
- `SIGTERM` and raw `sigaction`-level signal handling are not exposed in the
  interpreter era; they require the native backend.

---

## Type system

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
- `unto` naming an undefined type, or a non-object type (e.g. an interface)
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

## Identifiers

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
  