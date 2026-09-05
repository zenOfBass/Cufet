# Cufet `0.19.0`

*From the Mvskoke (Muskogee) word for rabbit—the trickster who brings the gift of fire to humankind.*

Cufet is a statically-typed, natural-language programming language. It borrows
English's surface while keeping formal structure visible. Every keyword reads
like prose; every control-flow boundary is explicit. No hidden scoping, no
ambiguous syntax, no semicolons. It is Turing complete.

**[Try it in your browser →](https://zenofbass.github.io/Cufet/)** — the interpreter, compiled to
wasm. Nothing to install.

Cufet has two backends: a tree-walking **interpreter**, which is the reference
implementation, and a **native compiler** that emits C and produces a real
executable — threads, signals, subprocesses and all. The compiler is held to the
interpreter as an oracle: compiled output must match interpreted output.

```cufet
For each counter in the range 1 to 100, repeat:
    If the counter % 15 is 0, state "FizzBuzz".
    Otherwise if the counter % 3 is 0, state "Fizz".
    Otherwise if the counter % 5 is 0, state "Buzz".
    Otherwise, state the counter.
Done.
```

---

## A taste

**Sum a series — and let it know its own length:**
```cufet
Define the scores as a series with (92, 85, 71, 88).
Define total as 0.

For each score in the scores, repeat:
    The total becomes the total + the score.
Done.

Define the average as the total / the number of the scores.
State the average.
```

**Recursion reads like what it is:**
```cufet
Bind number to factorial, given (the number n):
    If n is less than 2, return 1.
    Return n * cast factorial on (n - 1).
Done.

State cast factorial on (10).
```

**Functions are values — collect them and apply each:**
```cufet
Bind number to double, given (the number x): return x * 2. Done.
Bind number to triple, given (the number x): return x * 3. Done.

Define ops as a series of number function given (the number) with (double, triple).

For each op in ops, repeat:
    State cast op on (5).
Done.
```

**Closures and lambdas — make a specialized function on the fly:**
```cufet
Bind number function given (the number) to make-adder, given (the number n):
    Return a function given (the number x): Return x + n. Done.
Done.

Define add-five as cast make-adder on (5).
State cast add-five on (10).        → 15
```

**Objects with data and behavior:**
```cufet
Define object vehicle with (the text make, the number year):
    Bind void to describe:
        State one's make.
    Done.
Done.

Define car as a new vehicle { the make "Honda", the year 2021 }.
Cast describe on car.
```

**Value equality for records and objects:**
```cufet-fragment
Define car1 as a new vehicle { the make "Honda", the year 2021 }.
Define car2 as a new vehicle { the make "Honda", the year 2021 }.
If car1 is car2, state "same car".
```

**Maps, and absence without null:**
```cufet
Define ages as a map with ("alice" : 30, "bob" : 25).

Define alice-age as the entry for "alice" in ages.
If alice-age is not void, state alice-age.
Otherwise, state "no entry for alice".
```

**Failures are values — carry them, handle them, propagate them:**
```cufet
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
```cufet
With the file "log.txt" open for writing as log:
    Write "Starting.\n" to the log.

    Try to:
        Define the result as run "date".
        Write the output of result to the log.
    Done.
    In case of failure:
        Write "date command not found\n" to the log.
    Done.
Done.
```

**Structured concurrency — tasks, channels, and pipes:**
```cufet
Define ch as a channel of number.

Pull a rabbit.
    Have rabbit start a task as producer:
        Send 1 through ch.
        Send 2 through ch.
        Send 3 through ch.
        Close ch.
    Done.

    Have rabbit start a task as consumer:
        For each n in the range 1 to 3, repeat:
            Define val as the delivery from ch.
            If val is not void, state val.
        Done.
    Done.
Done.
```

**Pipe stages — producer feeds consumer directly:**
```cufet
Bind void to emit-numbers:
    Output 10.
    Output 20.
    Output 30.
Done.

Bind void to print-doubled:
    For each n from the input:
        State n * 2.
    Done.
Done.

emit-numbers | print-doubled.
```

**Command substitution — compose shell commands:**
```cufet
Try to:
    Define result as run "git" with arguments ("log", "--oneline", "-5").
    State the output of result.
Done.
In case of failure:
    State "git not available".
Done.
```

For the language in depth — every statement, the type system, records, objects,
functions, collections, bit patterns, error handling, I/O, regions, concurrency,
pipes and modules — see **[REFERENCE.md](docs/REFERENCE.md)**.
[GRAMMAR.md](docs/GRAMMAR.md) states the rules precisely and collects the sharp edges, and
[BOOKS.md](docs/BOOKS.md) covers the books Cufet ships with — including `the c-language`, the
one that admits C source.

---

## Building and running

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download). Compiling to a
native binary additionally requires **gcc** on `PATH` — the compiler emits C and
invokes it.

### Installing `cufet`

Cufet ships as a .NET global tool, so `cufet` becomes a command on your `PATH`.
The SDK already keeps its tools directory on `PATH` — no shell configuration and
no administrator rights:

```
dotnet pack src\App\Cufet.App.csproj -c Release
dotnet tool install --global --add-source src\App\bin\Release Cufet
```

Then, from anywhere:

```
cufet myprogram.cufe                  # run it
echo "State 1 + 1." | cufet           # or pipe it in
cufet check myprogram.cufe            # report errors without running it
cufet --help
```

Re-run `dotnet pack` and `dotnet tool update --global --add-source src\App\bin\Release Cufet`
to pick up changes. Every command below also works as
`dotnet run --project src\App\Cufet.App.csproj -- <args>` if you would rather not
install anything.

```
# Run all tests
dotnet test Cufet.sln
```

`check` lexes, parses and type-checks, then reports the first problem as
`myprogram.cufe:12: error: ...` and exits 1 — or says nothing and exits 0. Add
`--native` to also flag constructs the native compiler refuses (reported as
warnings, since they interpret fine), and `--json` for one JSON object per
diagnostic, which is what the editor extension consumes.

### Compiling to a native binary

```
# Compile to an executable (emits C, then invokes gcc)
cufet build myprogram.cufe

# Emit the generated C without compiling — useful for cross-toolchain builds
cufet emit-c myprogram.cufe myprogram.c
```

Programs using POSIX-only features — concurrency, subprocesses, signals — need a
POSIX toolchain to build; the rest compile anywhere gcc runs.

### Editor support

[`editors/vscode/`](editors/vscode/) is a Visual Studio Code extension giving
syntax highlighting and error squiggles. It needs no build step — copy it into
your extensions folder, then quit and restart VS Code:

```powershell
Copy-Item "$PWD\editors\vscode" "$env:USERPROFILE\.vscode\extensions\cufet" -Recurse
```

```sh
cp -r "$PWD/editors/vscode" ~/.vscode/extensions/cufet
```

Copy it — do not symlink. A linked directory is silently skipped by the
extension scan, with nothing logged. Insiders uses a separate folder,
`~/.vscode-insiders/extensions`, and fails the same silent way if you install
into the wrong one.

The squiggles are the front end's own diagnostics, by way of `cufet check`
— never a second opinion from a re-implementation that could drift out of step
with it. See [its README](editors/vscode/README.md) for settings and details.

---

## Project layout

```
src/
  Lexer/           Cufet.Lexer        — tokenizer
  Interpreter/     Cufet.Interpreter  — AST, parser, type checker, tree-walking interpreter
                                        (the reference implementation / oracle)
  Compiler/        Cufet.Compiler     — native backend: AST → C source → gcc
  App/             Cufet.App          — thin console entry point
                                        (interpret / check / build / emit-c)
editors/
  vscode/                             — VS Code extension: TextMate grammar + error squiggles
tests/
  Lexer.Tests/
  Interpreter.Tests/
  Compiler.Tests/                     — oracle tests: compiled output vs. interpreted output
  fixtures/soundness/                 — region-model probe programs (see that folder's README)
examples/                             — runnable programs, by category
  basics/                             — the short ones to read first
  algorithms/                         — dijkstra, n-queens, sudoku, huffman, life
  structures/                         — trees, objects, unions
  parsing/                            — json, a recursive-descent parser, config files
  concurrency/                        — tasks and channels
  systems/                            — reaching the OS: C axioms, permissions, subprocess pipes
  language/                           — programs that showcase one feature
  assets/                             — data files the programs read (paths are repo-root-relative)
  expected/                           — pinned outputs, flat and keyed on the program's file name
tools/                                — programs written IN Cufet, and the scripts that maintain the repo
  repl.cufe                           — a read-eval-print loop; hands each line to `cufet`
  terminal.cufe                       — the terminal book: raw mode, keys, a line editor
  shell.cufe                          — a working command shell in ~60 lines
docs/                                 — REFERENCE, BOOKS, GRAMMAR, DESIGN, ROADMAP
```

The lexer, parser, and type checker are **shared** by both backends, so a program
that type-checks does so identically whether it is interpreted or compiled.

See [REFERENCE.md](docs/REFERENCE.md) for the complete language reference.
See [BOOKS.md](docs/BOOKS.md) for the bundled books — `math`, `collections`, `chance`,
and `the c-language`.
See [GRAMMAR.md](docs/GRAMMAR.md) for the grammar and constraints reference — reserved
keywords, object field scope rules, expression vs condition contexts, and sharp
edges for writing Cufet correctly upfront.
See [DESIGN.md](docs/DESIGN.md) for why the language is shaped the way it is.
See [ROADMAP.md](docs/ROADMAP.md) for what comes next.
See [CHANGELOG.md](CHANGELOG.md) for version history.
See [CONTRIBUTING.md](CONTRIBUTING.md) to contribute.

---

*Toolchain built in C# / .NET 10; compiled programs are native binaries with no
managed runtime. Named for the Mvskoke trickster—rabbit—who, like all good
languages, promises to make something very powerful feel surprisingly natural.*