# Cufet `0.2.0`

*From the Mvskoke (Muskogee) word for rabbit — the trickster who steals fire and brings it to the people.*

Cufet is a statically-typed, natural-language programming language. It borrows
English's surface while keeping formal structure visible. Every keyword reads
like prose; every control-flow boundary is explicit. No hidden scoping, no
ambiguous syntax, no semicolons. It is Turing complete.

```
Define i as 1.

While i is 100 or less, repeat:
    If i % 15 is 0, state "FizzBuzz".
    Otherwise if i % 3 is 0, state "Fizz".
    Otherwise if i % 5 is 0, state "Buzz".
    Otherwise, state i.
    i becomes i + 1.
Done.
```

---

## A taste

**Sum a series — and let it know its own length:**
```
Define the scores as a series with (92, 85, 71, 88).
Define total as 0.

For each score in the scores, repeat:
    The total becomes the total + the score.
Done.

Define average as the total / the number of the scores.
State average.
```

**Recursion reads like what it is:**
```
Bind number to factorial, given (the number n):
    If n is less than 2, return 1.
    Return n * cast factorial on (n - 1).
Done.

State cast factorial on (10).
```

**Functions are values — collect them and apply each:**
```
Bind number to double, given (the number x): return x * 2. Done.
Bind number to triple, given (the number x): return x * 3. Done.

Define ops as a series of number function given (the number) with (double, triple).

For each op in ops, repeat:
    State cast op on (5).
Done.
```

**Objects with data and behavior:**
```
Define object vehicle with (the text make, the number year):
    Bind void to describe:
        State one's make.
    Done.
Done.

Define car as a new vehicle { the make "Honda", the year 2021 }.
Cast describe on car.
```

For the complete language — every statement, the type system, records,
objects, functions, collections, and the rules behind them — see
**[REFERENCE.md](REFERENCE.md)**.

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

See [ROADMAP.md](ROADMAP.md) for what's built, what's planned, and the design
decisions behind the language.

---

*Built in C# / .NET 10. Named for the Mvskoke trickster, rabbit who—like all
good languages—promises to make something very powerful feel surprisingly
natural.*