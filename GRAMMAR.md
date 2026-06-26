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
- [3. Expression context vs condition context](#3-expression-context-vs-condition-context)
- [4. Which operations accept expressions vs bare names](#4-which-operations-accept-expressions-vs-bare-names)
- [5. Where constructs are allowed](#5-where-constructs-are-allowed)
- [6. Sharp edges](#6-sharp-edges)
- [7. Writing Cufet: the mental model](#7-writing-cufet-the-mental-model)

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
        Define my-items as one's items.   ← get the field
        Add val to my-items.              ← mutate via the local alias
    Done.

    Bind number to pop:
        Define my-items as one's items.
        Define top as the last of my-items.
        Remove the last item from my-items.
        Return top.
    Done.

    Bind fact to is-empty:
        If the number of one's items is 0:   ← WRONG: SeriesLength takes a bare name
            Return true.
        Done.
        Return false.
    Done.
Done.
```

Wait — `the number of one's items` fails. See the next section for why.

### Local alias pattern for series operations

Several series operations take a **bare variable name** (not an expression). For
those, extract the field into a local variable first:

```
Define my-items as one's items.    ← extract; my-items is the same List reference
Add val to my-items.               ← OK (bare name)
Remove the last item from my-items.← OK (bare name)
the number of my-items             ← OK (bare name)
```

The local alias shares the **same reference** as the object's field, so all
mutations are immediately visible through `one` — no write-back needed.

### Map operations: `one's field` is fine directly

Map operations take `IExpression` for the map argument, so possessive access works
without a local alias:

```
In one's adjacency, the entry for n becomes fresh.          ← OK
Define val as the entry for key in one's adjacency.         ← OK
If one's cache has a key for key:                           ← OK
```

### Series read expressions: `one's field` is fine directly

`the first of`, `the last of`, `item N of`, and `SeriesAccess` generally all take
an `IExpression` for the target, so these work:

```
Define top as the last of one's items.         ← OK
Define val as item 3 of one's items.           ← OK
For each x in one's nodes, repeat: ...         ← OK (ForEach takes IExpression)
```

### Field mutation: `one's field becomes X`

To replace a field's value wholesale (not mutate in place), use possessive
assignment:

```
one's count becomes one's count + 1.
one's label becomes "updated".
```

This produces a `PossessiveSetStatement` and is valid in method bodies.

### Summary table

| Operation | Needs local alias? | Direct `one's field` OK? |
|---|---|---|
| `Add X to series` | **Yes** | No — bare name only |
| `Remove item N from series` | **Yes** | No — bare name only |
| `Remove X from series` (by value) | **Yes** | No — bare name only |
| `series[N] becomes X` | **Yes** | No — bare name only |
| `the number of series` | **Yes** | No — bare name only |
| `the first/last of series` | No | Yes — IExpression |
| `item N of series` | No | Yes — IExpression |
| `For each x in series` | No | Yes — IExpression |
| `In map, the entry for K becomes V` | No | Yes — IExpression for map |
| `the entry for K in map` | No | Yes — IExpression for map |
| `map has a key for K` | No | Yes — IExpression for map |
| `one's field becomes X` | No | Yes — PossessiveSetStatement |

---

## 3. Expression context vs condition context

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

**`is not greater than` is not valid** — there is no combined negated word
comparison. Use the converse: `is less than` instead of `is not greater than`,
`is greater than` instead of `is not less than`.

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

Use an `If` to produce a `fact` in return position:
```
Bind fact to is-empty:
    If the number of my-items is 0:
        Return true.
    Done.
    Return false.
Done.
```

Or use a symbol comparison in expression position:
```
Define empty as count = 0.
Return count = 0.
```

### Boolean results from conditions

A condition produces a `fact`. That `fact` can then be compared in an expression
via `is true` / `is false` in another condition, or via `= true` / `= false` in
expression position:

```
While (cast is-empty on pq) is false, repeat: ...
Define done as (cast is-empty on pq) = true.
```

The parenthesized call result is a `fact`; the outer `is false` / `is true` is
then a valid condition check.

---

## 4. Which operations accept expressions vs bare names

This section lists every place the parser requires a bare identifier (variable
name or field name) where you might want to pass `one's field` or another
complex expression.

### Series operations — bare name required (string parameter in AST)

| Syntax | The bare-name position |
|---|---|
| `Add X to name.` | `name` |
| `Add X to the start of name.` | `name` |
| `Add X after item N of name.` | `name` |
| `Remove the last item from name.` | `name` |
| `Remove item N from name.` | `name` |
| `Remove X from name.` (by value) | `name` |
| `item N of name becomes X.` | `name` |
| `the number of name` | `name` |

For all of these, if your series is an object field, **extract it first**:
```
Define my-items as one's items.
Add x to my-items.
```

### Series operations — IExpression (safe with `one's field`)

| Syntax | Notes |
|---|---|
| `the first of expr` | SeriesAccess |
| `the last of expr` | SeriesAccess |
| `item N of expr` | SeriesAccess |
| `For each x in expr, repeat:` | Series is IExpression |
| `sorted by field` on expr | SortExpression |
| `in reverse` on expr | SortExpression |

### Map operations — all accept IExpression

| Syntax | Map position |
|---|---|
| `In map, the entry for K becomes V.` | IExpression |
| `the entry for K in map` | IExpression |
| `map has a key for K` | IExpression |
| `the size of map` | IExpression |
| `For each pair in map, repeat:` | IExpression |

---

## 5. Where constructs are allowed

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

### `a series of T with ()` as an expression

An empty series literal can appear anywhere an expression is expected:

```
Define xs as a series of number with ().
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

---

## 6. Sharp edges

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

### `start` is a reserved keyword

The lexer produces `TokenType.Start` for the word `start`. It cannot be used as a
variable name, field name, or function name. Use `origin`, `src`, `begin`, etc.

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

---

## 7. Writing Cufet: the mental model

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
