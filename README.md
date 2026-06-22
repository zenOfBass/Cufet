# Cufet `0.6.0`

*From the Mvskoke (Muskogee) word for rabbit—the trickster who brings the gift of fire to humankind.*

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

**Closures and lambdas — make a specialized function on the fly:**
```
Bind number function given (the number) to make-adder, given (the number n):
    Return a function given (the number x): Return x + n. Done.
Done.

Define add-five as cast make-adder on (5).
State cast add-five on (10).        → 15
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

**Value equality for records and objects:**
```
Define car1 as a new vehicle { the make "Honda", the year 2021 }.
Define car2 as a new vehicle { the make "Honda", the year 2021 }.
If car1 is car2, state "same car".
```

**Maps, and absence without null:**
```
Define ages as a map with ("alice" : 30, "bob" : 25).

Define alice-age as the entry for "alice" in ages.
If alice-age is not void:
    State alice-age.
Done.
Otherwise:
    State "no entry for alice".
Done.
```

**Failures are values — carry them, handle them, propagate them:**
```
Bind number or failure to parse-age, given (the text raw):
    Define n as raw converted to number.
    If n is void, return a failure "not a number" of category "validation".
    Return n.
Done.

Try to:
    Define age as cast parse-age on ("thirty").
Done.
In case of failure:
    State "bad input: " joined to the message of the failure.
Done.
```

**Read files. Run programs. Cufet now touches the world:**
```
With the file "log.txt" open for writing as log:
    write "Starting.\n" to log.

    Try to:
        Define result as run "date".
        write result's output to log.
    Done.
    In case of failure:
        write "date command not found\n" to log.
    Done.
Done.
```

For the complete language — every statement, the type system, records,
objects, functions, collections, error handling, and I/O — see
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