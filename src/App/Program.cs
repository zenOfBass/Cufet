using Cufet.Compiler;
using Cufet.Interpreter;
using Cufet.Lexer;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// ★ UTF-8 out, or the interpreter and the compiled binary disagree on every non-ASCII character.
// A compiled program writes UTF-8 bytes directly; the CLI wrote through the console's default
// encoding, which on Windows is a legacy code page — so `State "héllo 👍".` came out as `h?llo ??`
// interpreted and correctly compiled. A real divergence, and one the test suite cannot see: its
// interpreter side writes to an in-memory StringWriter and its compiled side reads the binary with
// StandardOutputEncoding already set to UTF-8, so both are lossless there and only the console lost
// anything. Wrapped because a redirected stdout needs it as much as a terminal does.
try
{
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
catch (IOException) { /* no console attached (piped into a closed handle) — nothing to set */ }

if (args.Length >= 1 && args[0] is "--help" or "-h" or "help" or "-?" or "/?")
    Help();
else if (args.Length >= 1 && args[0] is "--version" or "-v")
    Console.WriteLine($"cufet {Version()}");
else if (args.Length >= 2 && args[0].Equals("build", StringComparison.OrdinalIgnoreCase))
{
    RefuseExtraArguments("build", args[2..], "cufet build <file.cufe>");
    Build(args[1]);
}
else if (args.Length >= 2 && args[0].Equals("emit-c", StringComparison.OrdinalIgnoreCase))
{
    // ⚠ The output name is OPTIONAL, so there may be no third argument to slice past —
    // `args[3..]` on a two-argument command threw before reaching the compiler at all.
    RefuseExtraArguments("emit-c", args.Length >= 3 ? args[3..] : [], "cufet emit-c <file.cufe> [out.c]");
    EmitC(args[1], args.Length >= 3 ? args[2] : Path.ChangeExtension(args[1], ".c"));
}
else if (args.Length >= 2 && args[0].Equals("check", StringComparison.OrdinalIgnoreCase))
    Check(args[1..]);
else if (args.Length >= 2 && args[0].Equals("tokens", StringComparison.OrdinalIgnoreCase))
    Tokens(args[1..]);
else
    // ⚠ Deliberately NOT refused here, unlike every verb above. `cufet script.cufe one two` drops
    // `one two` today because the language has no way to read them — but that spelling is exactly
    // where program arguments would arrive if they are ever added, and the shell on the roadmap
    // will want them. Refusing it now would have to be un-refused later, so the silence stays until
    // there is something to do with them.
    Interpret(args);

static string Version() =>
    typeof(Lexer).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "unknown";

// ⚠ An argument nobody asked for is a MISTAKE, and dropping it in silence is the worst of the
// available answers: the command appears to work while doing something else. `cufet build a.cufe
// -o out.exe` wrote the binary next to the SOURCE and said nothing about `-o`, which is not a flag
// this CLI has — and a whole session's binaries went somewhere other than where they were asked
// for. A mistyped `--jsno` on `check` disabled JSON just as quietly.
//
// ★ Exit 2, matching the other usage failures here: 0 and 1 are the program's own answers (it ran,
// it did not), so a mistake in the COMMAND has to be a third thing or a script cannot tell them
// apart.
static void RefuseExtraArguments(string verb, IEnumerable<string> extra, string usage)
{
    var unwanted = extra.ToList();
    if (unwanted.Count == 0) return;

    string names = string.Join(" ", unwanted);
    Console.Error.WriteLine(
        $"{verb}: don't know what to do with '{names}' — '{usage}'.");
    if (unwanted.Any(a => a.StartsWith('-')))
        Console.Error.WriteLine(
            $"  '{unwanted.First(a => a.StartsWith('-'))}' is not a flag {verb} takes. "
          + "Run 'cufet --help' for the flags each command has.");
    Environment.Exit(2);
}

// The flags a verb accepts; anything else starting with '-' is a typo rather than a filename.
static IEnumerable<string> UnknownFlags(IEnumerable<string> rest, params string[] known) =>
    rest.Where(a => a.StartsWith('-')
                 && !known.Contains(a, StringComparer.OrdinalIgnoreCase));

// A verb typed wrong lands here as a filename, so the usage text has to be worth reading.
// A file inside a `Prelude` directory IS (a draft of) the bundled prelude — check it as such,
// or the guards protecting bundled-book names would refuse the prelude's own source, and the
// embedded copy prepended on top would make its definitions duplicates.
static TypeChecker MakeChecker(string sourcePath)
{
    var checker = new TypeChecker
    {
        TreatProgramAsPrelude = string.Equals(
            Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ""),
            "Prelude", StringComparison.OrdinalIgnoreCase),

        // A book in another file is looked for beside the one being run.
        SourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath)),
    };

    // ★ The reporter needs this to turn a loaded line back into a file a person can open, and
    // an error is thrown from inside Check rather than handed back — so it is registered here,
    // where every caller passes, rather than at each of the six that catch one.
    SourceMap.Current = checker.Sources;
    return checker;
}

static void Help()
{
    Console.WriteLine($"""
        cufet {Version()} — the Cufet programming language.

          cufet <file.cufe>                    run it
          cufet                                run what is piped in on stdin
          cufet check [--json] [--native] [--strict] <f>
                                               report problems without running it
          cufet tokens --json <file.cufe>      report what each name in the file IS
          cufet build <file.cufe>              compile to a native binary (needs gcc)
          cufet emit-c <file.cufe> [out.c]     write the generated C without compiling
                                       (out.c plus cufet-runtime.c/.h beside it)

        check exits 0 when the program will run and 1 when it will not. An ERROR means it
        will not run; a WARNING means it will, and something about it is worth knowing, so
        warnings alone still exit 0. --strict makes any warning exit 1, for a CI gate.
        --native adds what the native compiler REFUSES; those programs still interpret, so
        they come back as warnings. A clean --native is not a promise the build will
        succeed — only `build` proves that. --json writes one diagnostic per line, for editors.
        Running and building print warnings to stderr and carry on.

        tokens writes the semantic kind — variable, function, type, parameter, property,
        namespace — of every name it can place, as one JSON object per line. A grammar
        cannot tell those apart in Cufet, so an editor layers this over its own colouring.
        Exit: 0 when the file was classified, 1 when it does not lex, parse or type-check
        (an unchecked file cannot be classified reliably), 2 when it cannot be read.
        """);
}

// Emits C source only (no gcc) — used to cross-compile in another toolchain (e.g. WSL gcc for
// POSIX subprocess code that this box's mingw gcc can't build).
static void EmitC(string sourcePath, string outPath)
{
    string source;
    try { source = File.ReadAllText(sourcePath); }
    catch (IOException e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); return; }
    try
    {
        var tokens  = new Lexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        program = MakeChecker(sourcePath).Check(program);

        // ★ Three files, not one. The point of `emit-c` is that a person can READ the C their
        // program became, and that was buried under 955 lines of runtime — measured at 79% of a
        // typical output and 98.9% of a small one. The runtime now sits beside it instead, so
        // `<name>.c` is the program, and the pair still compiles anywhere with nothing but
        // `gcc <name>.c cufet-runtime.c -o <name>`, which keeps the arch-neutral debugging path.
        var (header, runtimeSource, programSource) = new CodeGenerator().GenerateSplit(program);
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
        Directory.CreateDirectory(outDir);
        File.WriteAllText(outPath, programSource);
        File.WriteAllText(Path.Combine(outDir, RuntimeSplit.HeaderFileName), header);
        File.WriteAllText(Path.Combine(outDir, RuntimeSplit.SourceFileName), runtimeSource);
        Console.WriteLine($"Emitted: {outPath}");
        Console.WriteLine($"         {Path.Combine(outDir, RuntimeSplit.SourceFileName)}");
        Console.WriteLine($"         {Path.Combine(outDir, RuntimeSplit.HeaderFileName)}");
    }
    catch (LexerException e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); }
    catch (ParseException e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); }
    catch (TypeException e)  { Console.Error.WriteLine(e.Message); Environment.Exit(1); }
    catch (CompilerException e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); }
}

// Front-end-only pass: lex, parse, type-check, and report what fails — without running the
// program and without writing anything. An editor needs exactly this. `Interpret` finds the
// same errors, but finding them by running the program is not an option when the program
// reads input, writes files, or takes a minute.
//
//   cufet check [--json] [--native] [--strict] <file>
//
// Two output shapes. The default is one human line per diagnostic,
// "<path>:<line>:<column>: <severity>: <first line>", with the rest of a multi-line message indented
// under it — the shape the $cufet problem matcher in editors/vscode/ parses. `--json` writes
// one JSON object per line to stdout instead, which keeps the multi-line body of a type error
// intact rather than flattening it into a matcher-shaped single line.
//
// Exit: 0 the program will run (clean, or warnings only), 1 an error — or, with --strict, any
// warning — 2 the file could not be read. The split is the point of the severity: an error means
// there is nothing to run, a warning means there is, and only the caller knows whether that is
// good enough for what they are doing.
static void Check(string[] rest)
{
    const string usage = "cufet check [--json] [--native] [--strict] <file>";
    RefuseExtraArguments("check", UnknownFlags(rest, "--json", "--native", "--strict"), usage);
    // A second FILE is a mistake too — only the first was ever read, and silently.
    RefuseExtraArguments("check", rest.Where(a => !a.StartsWith('-')).Skip(1), usage);

    bool json   = rest.Any(a => a.Equals("--json",   StringComparison.OrdinalIgnoreCase));
    bool native = rest.Any(a => a.Equals("--native", StringComparison.OrdinalIgnoreCase));
    bool strict = rest.Any(a => a.Equals("--strict", StringComparison.OrdinalIgnoreCase));
    var  path   = rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

    if (path is null)
    {
        Console.Error.WriteLine("check: expected a source file — 'cufet check [--json] [--native] [--strict] <file>'.");
        Environment.Exit(2);
        return;
    }

    string full = Path.GetFullPath(path);
    string source;
    try { source = File.ReadAllText(full); }
    catch (IOException e) { Console.Error.WriteLine(e.Message); Environment.Exit(2); return; }

    var checker = MakeChecker(full);
    Cufet.Interpreter.Program program;
    // ⚠ Not the same tree. `program` is what the reader WROTE, and everything shown back to them is
    // judged on it; `lowered` is what actually runs, with every burying function rewritten into a
    // state machine. Only the code generator wants the second one — it is a back end, and a `bury`
    // never reaches a back end intact. Handing it `program` had `check --native` reporting
    // "a 'bury' reached the code generator untransformed" on every correct stash program.
    Cufet.Interpreter.Program lowered;
    IReadOnlyList<Diagnostic> style;
    try
    {
        var tokens = new Lexer(source).Tokenize();
        var parser = new Parser(tokens);
        program = parser.Parse();
        lowered  = checker.Check(program);
        // Style is judged on a program that parses and type-checks. Advising someone on how a line
        // reads while it is still wrong would bury the thing they actually need to fix.
        style = Linter.Lint(tokens, parser.StatementStarts, program);
    }
    catch (Exception e) when (e is LexerException or ParseException or TypeException)
    {
        Report(json, full, PositionOf(e), "error", e.Message);
        Environment.Exit(1);
        return;
    }

    var warnings = new List<Diagnostic>(checker.Diagnostics.Items);
    warnings.AddRange(style);

    // Codegen refusals are compiler-only. The interpreter runs these programs happily, so they
    // are a warning — "this won't build natively" — and not an error. Most carry no position, so
    // they land on line 1, column 1; the message names what to change.
    if (native)
    {
        var generator = new CodeGenerator();
        try { generator.Generate(lowered); }
        catch (CompilerException e)
        {
            var (line, column) = PositionOf(e);
            warnings.Add(new Diagnostic(DiagnosticSeverity.Warning, e.Message, line, column));
        }
        warnings.AddRange(generator.Diagnostics.Items);
    }

    foreach (var w in warnings)
        Report(json, full, (w.Line, w.Column), w.SeverityName, w.Message);

    if (warnings.Count == 0 && !json) Console.WriteLine($"No problems found in {path}.");

    // A warning means the program runs, so the default is success. --strict is for the caller who
    // wants the build to stop on one anyway — a CI gate, or a native-compatibility check.
    Environment.Exit(strict && warnings.Count > 0 ? 1 : 0);
}

// Semantic tokens: what each NAME in the file is — a variable, a function, a type, a parameter,
// a field, a book. A TextMate grammar already colours keywords, strings, numbers and comments,
// and cannot do any of these, because nothing about the spelling of a Cufet name says which it
// is. This command supplies the missing layer and nothing else.
//
//   cufet tokens [--json] <file>
//
// JSON is the only output shape — there is no human reading of a thousand positions — so --json
// is accepted for symmetry with `check` and is also the default. One object per line, the same
// shape `check --json` uses, so an editor parses both with one reader.
//
// A file that does not type-check is not classified: name resolution is exactly what the front
// end was in the middle of when it gave up, and half-resolved kinds would colour words wrongly.
// The error prints like `check`'s and the exit code says so.
//
// Exit: 0 classified (even with no names in the file), 1 the file has an error, 2 unreadable.
static void Tokens(string[] rest)
{
    const string tokensUsage = "cufet tokens [--json] <file>";
    RefuseExtraArguments("tokens", UnknownFlags(rest, "--json"), tokensUsage);
    RefuseExtraArguments("tokens", rest.Where(a => !a.StartsWith('-')).Skip(1), tokensUsage);

    var path = rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
    if (path is null)
    {
        Console.Error.WriteLine("tokens: expected a source file — 'cufet tokens [--json] <file>'.");
        Environment.Exit(2);
        return;
    }

    string full = Path.GetFullPath(path);
    string source;
    try { source = File.ReadAllText(full); }
    catch (IOException e) { Console.Error.WriteLine(e.Message); Environment.Exit(2); return; }

    IReadOnlyList<SemanticToken> semantic;
    try
    {
        var tokens  = new Lexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        var checker = MakeChecker(full);
        checker.Check(program);
        semantic = SemanticTokenizer.Collect(program, tokens, checker);
    }
    catch (Exception e) when (e is LexerException or ParseException or TypeException)
    {
        Report(true, full, PositionOf(e), "error", e.Message);
        Environment.Exit(1);
        return;
    }

    foreach (var t in semantic)
        Console.Out.WriteLine(JsonSerializer.Serialize(new
        {
            line      = t.Line,
            column    = t.Column,
            length    = t.Length,
            kind      = SemanticTokenLegend.NameOf(t.Kind),
            modifiers = SemanticTokenLegend.NamesOf(t.Modifiers),
        }));

    Environment.Exit(0);
}

// Where a diagnostic points. Lexer, parse and type errors all carry line and column
// structurally. A codegen refusal carries neither, so it falls back to reading the message —
// the "Here on line N" anchor first, precisely because a message may mention several lines
// ("established on line 4") and the violation is the one to underline; then the first line
// mentioned; then line 1, so a diagnostic is never dropped for want of a location. A position
// that falls back this way has no column, and column 1 is the honest answer.
static (int Line, int Column) PositionOf(Exception e) => e switch
{
    LexerException le             => (le.Line, le.Column),
    ParseException pe             => (pe.Line, pe.Column),
    TypeException { Line: > 0 } te => (te.Line, te.Column),
    _                             => (LineFromMessage(e.Message), 1),
};

static int LineFromMessage(string message)
{
    var anchored = Regex.Match(message, @"Here on line (\d+)");
    if (anchored.Success) return int.Parse(anchored.Groups[1].Value);
    var mentioned = Regex.Match(message, @"\bline (\d+)", RegexOptions.IgnoreCase);
    return mentioned.Success ? int.Parse(mentioned.Groups[1].Value) : 1;
}

// Warnings on the paths that are not `check` — running and building. They go to stderr, so a
// program's own output stays exactly what it wrote, and they never change what happens next:
// something worth saying about a program that runs is not a reason to refuse to run it.
static void WriteWarnings(string file, DiagnosticBag bag)
{
    foreach (var w in bag.Items)
        Report(false, Path.GetFullPath(file), (w.Line, w.Column), w.SeverityName, w.Message);
}

static void Report(bool json, string file, (int Line, int Column) at, string severity, string message)
{
    var (line, column) = at;

    // ★ A line past the host file’s own space came from a book in another file. Reporting the
    // file it was RUN from, with a line number out of that file’s range, is the one thing a
    // multi-file error must not do.
    if (SourceMap.Current?.Resolve(line) is { } origin)
        (file, line) = (origin.Path, origin.Line);
    if (json)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new { file, line, column, severity, message }));
        return;
    }
    var lines = message.Replace("\r\n", "\n").Split('\n');
    Console.Error.WriteLine($"{file}:{line}:{column}: {severity}: {lines[0]}");
    foreach (var l in lines.Skip(1))
        Console.Error.WriteLine("  " + l);
}

/// <summary>
/// Refuses a file that declares things and does nothing, for the two verbs that run one.
/// </summary>
/// <remarks>
/// ⚠ NOT asked by `check`. Checking a library is what a library author wants, and a book is
/// compiled as part of whatever pulls it — the mistake is in asking THIS file to be a program.
/// Exit 2, with the other usage mistakes: 0 and 1 are the program’s own answers, and a script has
/// to be able to tell "it ran and said no" from "you asked for the wrong thing".
/// </remarks>
static void RefuseIfNothingToRun(string verb, string shown, Cufet.Interpreter.Program program)
{
    if (!Runnable.NothingToRun(program)) return;
    Console.Error.WriteLine(
        $"{verb}: '{shown}' declares things but never does anything — there is nothing to run.");
    Console.Error.WriteLine(
        "  Every item at its top level is a declaration, so the program would start and finish");
    Console.Error.WriteLine(
        "  having done nothing. A file like this is a library: pull it from the program you are");
    Console.Error.WriteLine(
        "  building, and build that.");
    Environment.Exit(2);
}

static void Build(string sourcePath)
{
    string source;
    try { source = File.ReadAllText(sourcePath); }
    catch (IOException e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); return; }

    var checker = MakeChecker(sourcePath);
    Cufet.Interpreter.Program program;
    try
    {
        var tokens = new Lexer(source).Tokenize();
        program = new Parser(tokens).Parse();
        // The RETURNED program, not the one handed in: a burying function is rewritten
        // into a closure factory by then. (`check` and `tokens` deliberately keep the
        // original — a reader is shown what THEY wrote, not the lowering.)
        program = checker.Check(program);
        RefuseIfNothingToRun("build", sourcePath, program);
    }
    catch (LexerException e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); return; }
    catch (ParseException e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); return; }
    catch (TypeException e)  { Console.Error.WriteLine(e.Message); Environment.Exit(1); return; }

    WriteWarnings(sourcePath, checker.Diagnostics);

    try
    {
        var generator = new CodeGenerator();
        var (header, runtimeSource, programSource) = generator.GenerateSplit(program);
        WriteWarnings(sourcePath, generator.Diagnostics);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var dir      = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        var binExt   = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
        var binPath  = Path.Combine(dir, baseName + binExt);

        // ★ The intermediate C goes to a temporary directory, not next to the source. It used to be
        // written as `<name>.c` beside the .cufe and deleted afterwards, which silently destroyed a
        // hand-written `<name>.c` if the author happened to have one.
        var work = Directory.CreateTempSubdirectory("cufet-build-");
        try
        {
            var cPath = Path.Combine(work.FullName, baseName + ".c");
            File.WriteAllText(Path.Combine(work.FullName, RuntimeSplit.HeaderFileName), header);
            File.WriteAllText(cPath, programSource);

            var gcc    = new GccInvoker();
            var cached = new RuntimeCache().ObjectFor(runtimeSource, header, gcc, []);

            // A cache miss is not a failure — it just means paying for the runtime this once, which
            // is what every build did before the cache existed.
            string runtimeInput;
            if (cached != null) runtimeInput = cached;
            else
            {
                runtimeInput = Path.Combine(work.FullName, RuntimeSplit.SourceFileName);
                File.WriteAllText(runtimeInput, runtimeSource);
            }

            gcc.Compile([cPath, runtimeInput], binPath, []);
        }
        finally { try { work.Delete(recursive: true); } catch { } }

        Console.WriteLine($"Built: {binPath}");
    }
    catch (CompilerException e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); }
}

static void Interpret(string[] args)
{
    string source;
    if (args.Length > 0)
    {
        // Reached by a mistyped verb as well as a missing file, since anything that is not a
        // recognised verb is treated as a path — so point at the usage text rather than
        // letting an unhandled exception print a stack trace at someone.
        try { source = File.ReadAllText(args[0]); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{e.Message} Run 'cufet --help' for usage.");
            Environment.Exit(2);
            return;
        }
    }
    else
    {
        source = Console.In.ReadToEnd();
    }

    try
    {
        var tokens  = new Lexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        var checker = args.Length > 0 ? MakeChecker(args[0]) : new TypeChecker();
        // The RETURNED program, not the one handed in: a burying function is rewritten
        // into a closure factory by then. (`check` and `tokens` deliberately keep the
        // original — a reader is shown what THEY wrote, not the lowering.)
        program = checker.Check(program);
        RefuseIfNothingToRun("cufet", args.Length > 0 ? args[0] : "the program on standard input",
                             program);
        // To stderr, and before the program starts, so a warning never lands in the middle of the
        // output and never gets mistaken for something the program printed.
        WriteWarnings(args.Length > 0 ? args[0] : "<stdin>", checker.Diagnostics);
        // Foreign source is compiled and called, so the interpreter needs a C toolchain to run one.
        // Handed in rather than reached for: the interpreter is the layer the compiler is built on,
        // and an environment with no toolchain (the playground's wasm build) has to be able to say
        // so rather than fail to start.
        var interpreter = new Interpreter { ForeignRunner = new GccForeignRunner() };
        RunOnLargeStack(() => interpreter.Execute(program));
        // 128 + SIGINT, the convention every shell already understands.
        if (interpreter.WasInterrupted) Environment.Exit(130);
    }
    catch (LexerException e)   { Console.Error.WriteLine(e.Message); Environment.Exit(1); }
    catch (ParseException e)   { Console.Error.WriteLine(e.Message); Environment.Exit(1); }
    catch (TypeException e)    { Console.Error.WriteLine(e.Message); Environment.Exit(1); }
    catch (RuntimeException e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); }
}

static void RunOnLargeStack(Action action)
{
    Exception? caught = null;
    var thread = new Thread(
        () => { try { action(); } catch (Exception e) { caught = e; } },
        16 * 1024 * 1024);
    thread.Start();
    thread.Join();
    if (caught is not null)
        ExceptionDispatchInfo.Capture(caught).Throw();
}
