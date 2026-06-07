# Cufet

*From the Muskogee word for rabbit — the trickster who brings fire to mankind.*

Cufet is a natural-language programming language. It borrows English's surface while keeping formal structure visible. Every keyword reads like prose; every control-flow boundary is explicit. No hidden scoping, no ambiguous syntax, no semicolons.

It is Turing complete.

---

## Quick look

```
Define the scores as a series (92, 85, 71, 88).
Define total as 0.

For each score in the scores, repeat:
    total becomes total + score.
Done.

State total.
```

```
Define fib-a as 0.
Define fib-b as 1.

While fib-a is less than 1000, repeat:
    State fib-a.
    Define temp as fib-b.
    fib-b becomes fib-a + fib-b.
    fib-a becomes temp.
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

### Conditionals

```
If x is 1: State "one".

If x is 1: State "one". Otherwise: State "other".

If x is 1:
    State "one".
    State "definitely one".
Done.

If x is 1: State "one".
Otherwise if x is 2: State "two".
Otherwise: State "other".
```

Single-statement arms close with a period. Multi-statement arms close with `Done.`

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
Define scores as a series (90, 85, 70).
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

Named form binds the current element to a new name. Bare-it form binds it to `it` — innermost loop wins when nested. Both forms restore the previous value (or remove the binding) when the loop exits.

Mutating the series being iterated inside the loop is a runtime error. Use a `While` loop with an index if you need to change the series as you go.

`Stop.` and `Skip.` work the same as in `While` loops.

---

## Identifiers

- Must start with a lowercase letter (`total`, `my-var`, `x2`)
- Internal dashes allowed: `receipt-total` is one identifier
- `Total` (uppercase-initial) is a lexer error — uppercase-initial is reserved for keywords
- Binary `-` requires surrounding whitespace: `a - b` is subtraction; `a-b` is an identifier

---

## Building and running

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
# Run all tests
dotnet test NLP.sln

# Run a Cufet program
dotnet run --project src\App\NLP.App.csproj -- myprogram.cufet

# Or pipe from stdin
echo "State 1 + 1." | dotnet run --project src\App\NLP.App.csproj
```

---

## Project layout

```
src/
  Lexer/           NLP.Lexer        — tokenizer
  Interpreter/     NLP.Interpreter  — AST, parser, tree-walking interpreter
  App/             NLP.App          — thin console entry point
tests/
  Lexer.Tests/
  Interpreter.Tests/
```

---

## What's next

- Logical `and` / `or` in conditions
- Functions / named procedures
- Scope

---

*Built in C# / .NET 10. Named for the Muskogee trickster rabbit who — like all good languages — promises to make something very powerful feel surprisingly natural.*
