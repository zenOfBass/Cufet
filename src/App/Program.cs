using Cufet.Compiler;
using Cufet.Interpreter;
using Cufet.Lexer;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length >= 1 && args[0] is "--help" or "-h" or "help" or "-?" or "/?")
    Help();
else if (args.Length >= 1 && args[0] is "--version" or "-v")
    Console.WriteLine($"cufet {Version()}");
else if (args.Length >= 2 && args[0].Equals("build", StringComparison.OrdinalIgnoreCase))
    Build(args[1]);
else if (args.Length >= 2 && args[0].Equals("emit-c", StringComparison.OrdinalIgnoreCase))
    EmitC(args[1], args.Length >= 3 ? args[2] : Path.ChangeExtension(args[1], ".c"));
else if (args.Length >= 2 && args[0].Equals("check", StringComparison.OrdinalIgnoreCase))
    Check(args[1..]);
else if (args.Length >= 2 && args[0].Equals("tokens", StringComparison.OrdinalIgnoreCase))
    Tokens(args[1..]);
else
    Interpret(args);

static string Version() =>
    typeof(Lexer).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "unknown";

// A verb typed wrong lands here as a filename, so the usage text has to be worth reading.
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

        check exits 0 when the program will run and 1 when it will not. An ERROR means it
        will not run; a WARNING means it will, and something about it is worth knowing, so
        warnings alone still exit 0. --strict makes any warning exit 1, for a CI gate.
        --native adds what the native compiler refuses; those programs still interpret, so
        they come back as warnings. --json writes one diagnostic per line, for editors.
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
        new TypeChecker().Check(program);
        File.WriteAllText(outPath, new CodeGenerator().Generate(program));
        Console.WriteLine($"Emitted: {outPath}");
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

    var checker = new TypeChecker();
    Cufet.Interpreter.Program program;
    IReadOnlyList<Diagnostic> style;
    try
    {
        var tokens = new Lexer(source).Tokenize();
        var parser = new Parser(tokens);
        program = parser.Parse();
        checker.Check(program);
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
        try { generator.Generate(program); }
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
        var checker = new TypeChecker();
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

static void Build(string sourcePath)
{
    string source;
    try { source = File.ReadAllText(sourcePath); }
    catch (IOException e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); return; }

    var checker = new TypeChecker();
    Cufet.Interpreter.Program program;
    try
    {
        var tokens = new Lexer(source).Tokenize();
        program = new Parser(tokens).Parse();
        checker.Check(program);
    }
    catch (LexerException e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); return; }
    catch (ParseException e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); return; }
    catch (TypeException e)  { Console.Error.WriteLine(e.Message); Environment.Exit(1); return; }

    WriteWarnings(sourcePath, checker.Diagnostics);

    try
    {
        var generator = new CodeGenerator();
        var cSource   = generator.Generate(program);
        WriteWarnings(sourcePath, generator.Diagnostics);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var dir      = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        var cPath    = Path.Combine(dir, baseName + ".c");
        var binExt   = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
        var binPath  = Path.Combine(dir, baseName + binExt);

        File.WriteAllText(cPath, cSource);
        try { new GccInvoker().Compile(cPath, binPath); }
        finally { try { File.Delete(cPath); } catch { } }

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
        var checker = new TypeChecker();
        checker.Check(program);
        // To stderr, and before the program starts, so a warning never lands in the middle of the
        // output and never gets mistaken for something the program printed.
        WriteWarnings(args.Length > 0 ? args[0] : "<stdin>", checker.Diagnostics);
        RunOnLargeStack(() => new Interpreter().Execute(program));
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
