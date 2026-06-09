# Cufet

*From the Muskogee word for rabbit — the trickster who brings fire to mankind.*

Cufet is a natural-language programming language. It borrows English's surface while keeping formal structure visible. Every keyword reads like prose; every control-flow boundary is explicit. No hidden scoping, no ambiguous syntax, no semicolons.

It is Turing complete.

---

## Quick look

```
Define the scores as a series with (92, 85, 71, 88).
Define total as 0.

For each score in the scores, repeat:
    total becomes total + score.
Done.

State total.
```

```
Bind number to factorial, given (the number n):
    If n is less than 2, return 1.
    return n * Cast factorial on (n - 1).
Done.

State Cast factorial on (10).
```

```
Bind number to double, given (the number x): return x * 2. Done.
Bind number to triple, given (the number x): return x * 3. Done.

Define ops as a series of number function given (the number) with (double, triple).

For each op in ops, repeat:
    State Cast op on (5).
Done.
```

---

## Language reference

### Statements

| Syntax | Meaning |
|---|---|
| `State expr.` | Print a value |
| `Define name as expr.` | Declare a variable (error if already defined) |
| `name becomes expr.` | Reassign a variable (error if not declared) |

Articles (`a`, `an`, `the`) are noise everywhere — `Define the total as 0.` and `Define total as 0.` are identical.

### Arithmetic

Standard `+ - * /` with `()` and conventional precedence. Unary `-` supported. Uses `decimal` — no floating-point surprises.

### Comparisons

**In expression context** (right side of `Define`/`becomes`/`State`):

```
State 3 > 1.       → true
State 10 / 4.      → 2.5
State 1 = 1.       → true
```

**In condition context** (after `If`/`While`):

```
If x is 5:
If x is not 3:
If x is greater than 10:
If x is less than 10:
If x is 5 or more:
If x is 5 or less:
```

`=` is equality only — assignment is `becomes`, declaration is `Define`.

### Conditionals

**Inline — comma, one statement, works anywhere:**
```
If x is 1, State "one".

If x is 1, State "one". Otherwise, State "other".

If x is 1, State "one".
Otherwise if x is 2, State "two".
Otherwise, State "other".
```

**Block — colon, Done.-closed, any number of statements:**
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

Comma after the condition → inline single-statement. Colon after the condition → `Done.`-terminated block.

### Loops

```
While x is less than 10, repeat:
    x becomes x + 1.
Done.

Repeat:
    x becomes x + 1.
until x is 10 or more.
```

`Stop.` breaks the innermost loop. `Skip.` continues the next iteration. Both are parse errors outside a loop.

### Series (collections)

**Literal:**
```
Define scores as a series with (90, 85, 70).
Define names  as a series of text with ("alice", "bob").
Define ops    as a series of number function given (the number) with (double, triple).
```

The element type is inferred from the elements, or can be declared explicitly after `of`. Empty series require an explicit annotation:
```
Define log as a series of text.
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
the first of scores becomes 100.
item n of scores becomes 100.
the last of scores becomes 100.
```

Out-of-bounds access or assignment produces a readable runtime error.

### For-each loops

```
For each score in scores, repeat:
    State score.
Done.
```

```
For each in scores, repeat:
    State it.
Done.
```

Named form binds the current element to a new name. Bare-it form binds it to `it` — innermost loop wins when nested. Both restore the previous binding when the loop exits.

Mutating the series being iterated is a runtime error. Use `While` with an index if you need to change the series as you go.

`Stop.` and `Skip.` work the same as in `While` loops.

### Functions

**Declaration:**
```
Bind number to add, given (the number a, the number b):
    return a + b.
Done.

Bind void to greet, given (the text name):
    State name.
Done.

Bind number to get-ten:
    return 10.
Done.
```

`Bind` declares a named function. The return type comes first (`number`, `void`, or a function type — see below). Parameters follow `given`. Functions with no parameters omit `given` entirely.

**Calling:**
```
State Cast add on (3, 4).          ← in expression position
Cast greet on ("hello").           ← as a statement (void or discarded result)
```

`Cast` works on any expression that evaluates to a function — a name, a variable, or a series element.

**Early exit:**
```
return value.    ← return a value
return.          ← void early exit
```

**Functions are first-class values:**
```
Define op as add.
State Cast op on (3, 4).           → 7
```

A function assigned to a variable carries its full type. The type checker catches calling the wrong signature through any alias.

**Function-typed parameters:**
```
Bind number to apply, given (the number x, the number function f given (the number)):
    return Cast f on (x).
Done.

Bind number to double, given (the number x): return x * 2. Done.

State Cast apply on (5, double).   → 10
```

The parameter type `the number function f given (the number)` declares that `f` must be a function taking a number and returning a number. Passing the wrong signature is a static type error.

**Functions as return values:**
```
Bind number function given (the number) to get-doubler:
    return double.
Done.

Define fn as Cast get-doubler on ().
State Cast fn on (5).              → 10
```

The return type `number function given (the number)` declares that this function returns a function. Only top-level named functions can be returned — closures are not yet supported.

**Series of functions:**
```
Define ops as a series of number function given (the number) with (double, triple).

State Cast the first of ops on (5).          → 10

For each op in ops, repeat:
    State Cast op on (5).
Done.
```

A series whose element type is a function type. All the usual series operations apply — access, add, remove, for-each — and any accessed element can be `Cast` directly.

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
- Accessing a non-series with series operations
- Adding or removing the wrong element type from a typed series

Type errors name the violation, the line, and the fix:

```
That doesn't work: 'scores' holds numbers.
You defined it on line 1 as a series of numbers, so it can only accept numbers.
Here on line 4, you're trying to add a text value to it.

Change the value to a number, or define a separate series that holds text.
```

---

## Identifiers

- Must start with a lowercase letter (`total`, `my-var`, `x2`)
- Internal dashes allowed: `receipt-total` is one identifier
- `Total` (uppercase-initial) is a lexer error — uppercase-initial is reserved for keywords
- Binary `-` requires surrounding whitespace: `a - b` is subtraction; `a-b` is an identifier
- `a`, `an`, `the` are reserved as noise (articles) and cannot be used as identifiers

---

## Building and running

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
# Run all tests
dotnet test Cufet.sln

# Run a Cufet program
dotnet run --project src\App\Cufet.App.csproj -- myprogram.cufe

# Or pipe from stdin
echo "State 1 + 1." | dotnet run --project src\App\Cufet.App.csproj
```

---

## Project layout

```
src/
  Lexer/           Cufet.Lexer        — tokenizer
  Interpreter/     Cufet.Interpreter  — AST, parser, type checker, tree-walking interpreter
  App/             Cufet.App          — thin console entry point
tests/
  Lexer.Tests/
  Interpreter.Tests/
```

---

## What's next

- Logical `and` / `or` in conditions
- Closures (functions that capture their enclosing scope)
- String operations

---

*Built in C# / .NET 10. Named for the Muskogee trickster rabbit who — like all good languages — promises to make something very powerful feel surprisingly natural.*
