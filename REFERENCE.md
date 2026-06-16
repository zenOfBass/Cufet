# Cufet Language Reference

The complete reference for Cufet `0.3.0`. For a quick introduction and taste of
the language, see [README.md](README.md). For what's planned and the reasoning
behind the design, see [ROADMAP.md](ROADMAP.md).

---

## Contents

- [Cufet Language Reference](#cufet-language-reference)
  - [Contents](#contents)
  - [Statements](#statements)
  - [Arithmetic](#arithmetic)
  - [Comparisons](#comparisons)
  - [Logic](#logic)
  - [Conditionals](#conditionals)
  - [Loops](#loops)
    - [For-each loops](#for-each-loops)
  - [Series (collections)](#series-collections)
  - [Text](#text)
  - [Range](#range)
  - [Records](#records)
  - [Objects](#objects)
    - [Embedding (composition)](#embedding-composition)
    - [Interfaces (polymorphism)](#interfaces-polymorphism)
  - [Functions](#functions)
    - [Closures](#closures)
    - [Lambda literals (anonymous functions)](#lambda-literals-anonymous-functions)
  - [Voidable values (`void` and `voidable T`)](#voidable-values-void-and-voidable-t)
  - [Maps](#maps)
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

## Arithmetic

Standard `+ - * / %` with `()` and conventional precedence. Unary `-` supported.
Uses `decimal` — no floating-point surprises.

`%` is modulo (remainder). Binary `-` requires surrounding whitespace to
distinguish it from a dash inside an identifier: `a - b` is subtraction,
`a-b` is one identifier.

Results print in their minimal form regardless of scale picked up along the
way — `1.5 + 0.5` displays as `2`, not `2.0`.

---

## Comparisons

**In expression context** (right side of `Define` / `becomes` / `State`) — symbols:

```
State 3 > 1.       → true
State 10 / 4.      → 2.5
State 1 = 1.       → true
```

**In condition context** (after `If` / `While` / `until`) — words:

```
If x is 5:
If x is not 3:
If x is greater than 10:
If x is less than 10:
If x is 5 or more:
If x is 5 or less:
```

`=` is equality only — assignment is `becomes`, declaration is `Define`. The
split is deliberate: comparison-as-a-value lives in the math domain (symbols);
comparison-in-a-condition reads as a sentence (words).

---

## Logic

`and`, `or`, and `not` combine conditions. Words, not symbols — consistent with
word-comparisons.

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

## Maps

Typed key→value collections. Keys are one type, values are one type
(homogeneous, like series).

**Type:** `a map from text to number` — text keys, number values. Keys are
`number` or `text`.

**Construction:**
```
Define ages as a map with ("alice" : 30, "bob" : 25).     ← populated, key : value pairs
Define ages as a new map from text to number.             ← empty, typed
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
Define ages as a new map from text to voidable number.
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