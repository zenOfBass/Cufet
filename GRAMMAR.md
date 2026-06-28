# Cufet Grammar & Constraints Reference

This document is the **operational** reference for writing Cufet correctly upfront.
It covers the things that would otherwise be discovered by erroring: reserved words
you can't use as names, scope rules inside object methods, which operations accept
complex expressions vs bare names, where constructs are/aren't allowed, and the
sharp edges that look reasonable but parse or type-check differently than expected.

It is **not** a feature tour — see [REFERENCE.md](REFERENCE.md) for that.

**Maintenance:** every feature slice that adds a keyword, syntactic form, or
constraint must update this document. The reserved-keyword list in §1 especially.
A future improvement would generate that list from the lexer's keyword table
automatically so it can never drift; for now it is maintained by hand.

---

## Contents

- [1. Reserved keywords](#1-reserved-keywords)
- [2. Object methods and field access](#2-object-methods-and-field-access)
- [3. Value vs. reference semantics](#3-value-vs-reference-semantics)
- [4. Expression context vs condition context](#4-expression-context-vs-condition-context)
- [5. Which operations accept expressions vs bare names](#5-which-operations-accept-expressions-vs-bare-names)
- [6. Where constructs are allowed](#6-where-constructs-are-allowed)
- [7. Sharp edges](#7-sharp-edges)
- [8. Writing Cufet: the mental model](#8-writing-cufet-the-mental-model)

---

## 1. Reserved keywords

Every token below is lexed as a specific `TokenType` — the lexer will never
produce an `Identifier` for these strings, so they cannot be used as variable
names, field names, function names, or for-each iterator names.

Keywords are **case-insensitive** (`State`, `state`, `STATE` are identical).

### Noise (consumed silently wherever articles appear)

These are **not** reserved in the sense of being forbidden; they are consumed by
the parser before it looks for the next meaningful token. You will never see them
as identifiers, but that is fine — they read as natural articles.

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

### Control flow

| Word | Token |
|---|---|
| `if` | If |
| `otherwise` | Otherwise |
| `done` | Done |
| `while` | While |
| `repeat` | Repeat |
| `until` | Until |
| `try` | Try |
| `case` | Case |
| `suppress` | Suppress |

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
| `add` | Add | Series add statement |
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
| `first` | Ordinal | |
| `second` | Ordinal | |
| `third` | Ordinal | |
| `fourth` | Ordinal | |
| `fifth` | Ordinal | |
| `sixth` | Ordinal | |
| `seventh` | Ordinal | |
| `eighth` | Ordinal | |
| `ninth` | Ordinal | |
| `tenth` | Ordinal | |
| `last` | Ordinal | |
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
| `book` | Book |
| `books` | Books |
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

### Chance and randomness

| Word | Token | Notes |
|---|---|---|
| `random` | Random | `a random number from low to high` / `a random item from series` / `a random guess` |
| `randomly` | Randomly | `randomly shuffled series` |
| `shuffled` | Shuffled | companion to `randomly` |
| `guess` | Guess | `a random guess` — yields a `fact` |
| `seed` | SeedKw | `Seed the chance with N.` — statement keyword |

### Comparison and logic (condition context only)

These are reserved but only meaningful inside conditions (`If`, `While`, `until`).
They still cannot be used as identifiers.

| Word | Token |
|---|---|
| `is` | Is |
| `not` | Not |
| `greater` | Greater |
| `less` | Less |
| `than` | Than |
| `more` | More |
| `and` | And |

### Contextual words — NOT reserved

These are matched by lexeme in specific positions, not by token type. Outside those
positions they parse as regular identifiers and can be used as variable names:

`line`, `lines`, `all`, `input`, `arguments`, `reading`, `writing`, `exists`,
`variable`, `been`, `requested`

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

```
Define object stack with (the series of number items):
    Bind void to push, given (the number val):
        Add val to one's items.
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
```
Define my-items as one's items.    ← was required; now unnecessary
Add val to my-items.
```

Is now simply:
```
Add val to one's items.
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

Add card to one's cards.                                    ← OK (series add)
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
| `matrix` | element mutations are reflected everywhere |

### The mental model

> **Value type?** `Define b as a.` gives `b` a fresh, independent copy. Changes to `b`
> leave `a` untouched.
>
> **Reference type?** `Define b as a.` gives `b` another name for the same collection.
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
Add 4 to ys.
State the number of xs.    ← "4"   — ys aliases xs; Add mutated the shared list
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

```
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

```
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

## 4. Expression context vs condition context

Cufet has two distinct comparison syntaxes that are **not interchangeable**.

### Expression context (right side of `Define`, `becomes`, `State`, `Return`, function argument)

Symbol comparisons produce a `fact` value:

```
Define same as x = y.
Define big  as x > 100.
Define ok   as x >= 0 and x <= 10.
Return x > 0.
```

`=`, `<`, `>`, `<=`, `>=` are the operators. `=` is equality only — assignment
is `becomes`.

### Condition context (after `If`, `While`, `until`, `Repeat ... until`)

Word comparisons only — no symbols:

```
If x is 5:
If x is not 3:
If x is greater than 10:
If x is less than 10:
If x is 5 or more:
If x is 5 or less:
While x is less than bound, repeat:
```

**`is not greater than` and `is not more than` are not valid** — there is no
combined negated word comparison. Use the converse: `is less than` instead of
`is not greater than`, `is greater than` instead of `is not less than`.

**`is more than` is a compile error — use `is greater than` instead.** The parser
catches `is more than` and emits: *"did you mean 'is greater than'?"* Always use
the canonical form:

```
While count is greater than 0, repeat:   ← CORRECT (>)
While count is more than 0, repeat:      ← COMPILE ERROR
```

### Critical: conditions are not general expressions

A word comparison (`is greater than`, `is less than`, etc.) **cannot appear** as
the value in an expression position. It is only valid as the direct condition of
`If`, `While`, or `until`.

This **fails**:
```
Return (the number of items) is 0.    ← ERROR: parenthesized sub-expr is complete;
                                         'is' is then unexpected
Define empty as count is 0.           ← ERROR: same reason
```

Use a symbol comparison in expression position:
```
Bind fact to is-empty:
    Define my-items as one's items.
    Return the number of my-items = 0.
Done.
```

### There are no boolean literals

`true` and `false` are NOT keywords — they are ordinary identifiers. If they are
not in scope, using them causes a runtime "not defined" error. **Never write
`Return true.` or `In map, the entry for k becomes false.`**

Produce boolean values via expressions instead:

| Intent | Expression |
|---|---|
| always-true literal | `1 = 1` |
| always-false literal | `1 = 0` |
| empty-series check | `the number of items = 0` |
| negate a fact | `not (fact-expr)` in condition context |

### Negating a fact in condition context

`not (fact-expr)` is valid after `If`/`While`/`until`:

```
While not (cast is-empty on pq), repeat:
If not (visited has a key for neighbor):
```

### `is true` / `is false` are variable lookups, not keywords

`(expr) is false` in a condition looks up `false` as a variable reference — it is
**not** a special "check if false" form. This will fail at runtime unless you have
`Define false as ...` in scope. Use `not (expr)` instead:

```
While not (cast is-empty on pq), repeat:   ← CORRECT
While (cast is-empty on pq) is false, repeat:  ← RUNTIME ERROR: 'false' not defined
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
Add 1 to (x + y).   ← TYPE ERROR: (x + y) is not a series
```

### Map operations — all accept IExpression

| Syntax | Read or mutate? |
|---|---|
| `In expr, the entry for K becomes V.` | mutate |
| `the entry for K in expr` | read |
| `expr has a key for K` | read |
| `the size of expr` | read |
| `For each pair in expr, repeat:` | read |

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

```
Seed the chance with 42.
```

The seed must be a `number`. Seeding makes the sequence reproducible; without an
explicit seed the RNG is entropy-seeded on interpreter creation.

**Bound-expression level** — `low` and `high` in `a random number from low to high`
are parsed at addition level (same precedence as `the characters from N to M of text`).
Arithmetic works; logical/comparison forms do not:

```
State a random number from 1 to n + 5.    ← OK
State a random number from 1 to 6.        ← OK
```

**Series target** — `a random item from <series>` and `randomly shuffled <series>`
parse their target with `ParseCorePrimary`. Identifiers, possessive access
(`one's cards`), parenthesized expressions, and series literals all work.

**Type-matching `but void is`** — `a random item from series` returns `voidable T`.
The fallback in `but void is` must match the element type `T`:

```
Define picked as a random item from xs but void is 0.   ← OK for series of number
Define picked as a random item from xs but void is "".  ← OK for series of text
```

### `Pull ... Done.` — books and rabbits

`Pull` opens a scope; `Done.` closes it and frees whatever was pulled.

**Single book:**
```
Pull a book on math.
    Define r as the square root of 16.
Done.
```

**Multiple books (shared scope, one `Done.`):**
```
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

**Rabbit (singular only):**
```
Pull a rabbit as den.
    ...
Done.
```
Anonymous form (`Pull a rabbit.`) omits the name. Rabbits stay singular — multiple arenas
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

```
Bind making a graph to new-graph:
    Define empty-nodes as a series of node with ().
    Define empty-adj   as a new map from node to series of edge.
    Return a new graph { the nodes empty-nodes, the adjacency empty-adj }.
Done.
```

Call it with `cast new-graph`.

### Getters: `Get name as type:`

Computed properties are declared inside an object body with `Get name as type:`.
The body has access to `one` (the receiver). Accessed via `obj's name` or
`the name of obj` — uniform access, same syntax as stored fields.

```
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

```
Get count as number:
    Return the number of one's items.
Done.
```

### `Bind overloading` is top-level only

Operator overload declarations cannot appear inside any block:

```
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

```
Define xs as a series of number with ().
Define suits as a series of text with ("Spades", "Hearts", "Diamonds", "Clubs").
Define primes as a series of number with (2, 3, 5, 7, 11).
Return a series of text with ().
In adjacency, the entry for n becomes a series of edge with ().   ← in a map-set
```

Note: in `In map, the entry for K becomes <expr>.`, the value is an IExpression
— so a series literal here is fine.

But `a series of T with ()` cannot be the fallback in `but void is` when nested
in certain positions (it parses ambiguously as a type rather than a value literal
in some contexts). Use a local variable:

```
Define empty as a series of edge with ().
Define edges as the entry for src in adjacency but void is empty.
```

### `a new map from K to V` creates an empty map

In expression position, only `a new map from K to V` is valid for creating an
empty map. The bare `a map from K to V` is a **type annotation** only — it is
not a value expression:

```
Define m as a new map from text to number.    ← OK: creates empty map
                                              ← NOT: "a map from text to number"
```

### For-each iterator names must be identifiers

The iterator variable in `For each <name> in <series>` must be an `Identifier`
token. Any reserved keyword is illegal here, even if it "feels" natural. Common
traps:

- `entry` is reserved → use `pair`, `kv`, or another name
- `start`, `from`, `to`, `end` are reserved → avoid them
- `it` is reserved for the bare-`it` loop form

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

**Tracked debt (pre-native):** the heuristic itself remains lookahead-based. The
proper architectural fix — Approach B, explicit type-annotation contexts — is
deferred to the dedicated pre-native parser-hardening pass, when the parser's
syntax is feature-complete and can be hardened once on its final shape.

---

## 7. Sharp edges

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

### Comparisons after parenthesized sub-expressions

After a parenthesized expression, the parser considers the primary expression
complete and does not continue parsing a word comparison. This means:

```
Return (the number of items) is 0.    ← PARSE ERROR
If (the number of items) is 0:        ← OK: 'is' here is a condition comparison
```

This asymmetry: `(expr) is ...` is valid in condition context (after `If`/`While`)
but not in expression context (after `Return`/`Define`/etc.).

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
cast compute on (x) or pass the failure off.                    ← PARSE ERROR
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

```
For each pair in m, repeat:
    Define k as the key of pair.
    Define v as the value of pair.
Done.
```

Do **not** name the iterator `entry` — it is a reserved keyword. `pair`, `kv`,
`item`, or any non-reserved word works.

### Rabbit lifetime invariant: function calls now carry depth

The downward-only invariant enforces that a reference-typed value can only be
stored into a container whose lifetime is at least as long as the value's. After
the return-depth inference arc, **function calls that return reference types are
also tracked** — the checker infers how the return value's lifetime relates to the
arguments.

**What changes:** passing a rabbit-allocated value through a function no longer
launders its depth. A one-hop identity function doesn't let a shorter-lived
reference escape:

```
Bind series of number to smuggle, given (the series of number s):
    return s.
Done.

Define outer as a series of number with ().
Pull a rabbit.
    Define inner as a series of number with (1, 2, 3).
    outer becomes Cast smuggle on (inner).   ← TYPE ERROR: inner is shorter-lived
Done.
```

The checker infers that `smuggle` returns at the depth of its argument `s`.
Calling it with `inner` (rabbit depth 1) and assigning to `outer` (depth 0) is
caught as an invariant violation.

**Same-depth calls are still legal:**

```
Pull a rabbit.
    Define a as a series of number with (1).
    Define b as a series of number with (2).
    Define chain as a series of series of number with (a, b).   ← OK: all depth 1
    Define first as Cast head on (chain).    ← OK if result stored at depth 1 or deeper
Done.
```

**Conservative fallback:** for functions whose exact depth signature can't be
determined (recursive functions, unknown callees), the checker assumes the return
carries the depth of the deepest reference-type argument. This is sound
(over-strict, never under-strict) — it may reject contrived-safe code but will
never permit an unsafe escape.

**Open gap (method calls, pre-native):** methods defined on object types are not
yet depth-tracked at call sites — their returns are treated as depth-0 for now.
A method that passes a rabbit-allocated field to the outside world would not yet
be caught. This is a tracked pre-native soundness gap (the "capture-store hole")
separate from the return-depth laundering hole that this inference closes.

---

## 8. Writing Cufet: the mental model

### Two grammars, one language

Cufet has two syntactic layers that compose but do not mix:

**Expression grammar** — produces values, uses symbol operators (`+`, `-`, `>`,
`=`, `and`, `or`, `not`), terminated by `.` when it forms a statement. This is
where arithmetic, string ops, function calls, field access, and boolean arithmetic
live.

**Condition grammar** — produces `fact`, uses word comparisons (`is`, `is greater
than`, etc.) and word logical operators, appears only after `If`, `While`, and
`until`. Conditions cannot appear inside expressions.

When you want a boolean value *as a value*, use symbol comparisons (`x > 0`).
When you want to branch or loop on a condition, use word comparisons.

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

```
Define xs as a series of number with ().      ← empty series
Define m  as a new map from text to number.   ← empty map
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

### The for-each body cannot mutate the series

`For each x in series` forbids mutation of `series` during iteration. If you need
to build a filtered/transformed result, collect into a separate series or use
`While` with an index.
