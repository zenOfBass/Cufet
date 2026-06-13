# Cufet Language Reference

The complete reference for Cufet `0.2.0`. For a quick introduction and taste of
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
  - [Records](#records)
  - [Objects](#objects)
    - [Embedding (composition)](#embedding-composition)
    - [Interfaces (polymorphism)](#interfaces-polymorphism)
  - [Functions](#functions)
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
returns a function. Only top-level named functions can be returned — closures are
not yet supported.

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
- Using a `void` result as a value
- Calling a method that doesn't exist on an object
- Accessing a non-series with series operations
- Adding or removing the wrong element type from a typed series
- Assigning the wrong type to a record or object field
- Passing a record that doesn't match the declared shape
- Adding a record to a series whose shape it doesn't match
- An object that claims an interface but doesn't satisfy its contract

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