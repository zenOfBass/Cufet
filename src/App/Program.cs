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
          cufet check [--json] [--native] <f>  report errors without running it
          cufet build <file.cufe>              compile to a native binary (needs gcc)
          cufet emit-c <file.cufe> [out.c]     write the generated C without compiling

        check exits 0 when clean and 1 when it finds something. --native additionally
        reports what the native compiler refuses; those programs still interpret, so
        they come back as warnings. --json writes one diagnostic per line, for editors.
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
//   cufet check [--json] [--native] <file>
//
// Two output shapes. The default is one human line per diagnostic,
// "<path>:<line>:<column>: <severity>: <first line>", with the rest of a multi-line message indented
// under it — the shape the $cufet problem matcher in editors/vscode/ parses. `--json` writes
// one JSON object per line to stdout instead, which keeps the multi-line body of a type error
// intact rather than flattening it into a matcher-shaped single line.
//
// Exit: 0 clean, 1 problems found (any severity), 2 the file could not be read.
static void Check(string[] rest)
{
    bool json   = rest.Any(a => a.Equals("--json",   StringComparison.OrdinalIgnoreCase));
    bool native = rest.Any(a => a.Equals("--native", StringComparison.OrdinalIgnoreCase));
    var  path   = rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

    if (path is null)
    {
        Console.Error.WriteLine("check: expected a source file — 'cufet check [--json] [--native] <file>'.");
        Environment.Exit(2);
        return;
    }

    string full = Path.GetFullPath(path);
    string source;
    try { source = File.ReadAllText(full); }
    catch (IOException e) { Console.Error.WriteLine(e.Message); Environment.Exit(2); return; }

    Cufet.Interpreter.Program program;
    try
    {
        var tokens = new Lexer(source).Tokenize();
        program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
    }
    catch (Exception e) when (e is LexerException or ParseException or TypeException)
    {
        Report(json, full, PositionOf(e), "error", e.Message);
        Environment.Exit(1);
        return;
    }

    // Codegen refusals are compiler-only. The interpreter runs these programs happily, so they
    // are a warning — "this won't build natively" — and not an error. Most carry no position, so
    // they land on line 1, column 1; the message names what to change.
    if (native)
    {
        try { new CodeGenerator().Generate(program); }
        catch (CompilerException e)
        {
            Report(json, full, PositionOf(e), "warning", e.Message);
            Environment.Exit(1);
            return;
        }
    }

    if (!json) Console.WriteLine($"No problems found in {path}.");
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

    Cufet.Interpreter.Program program;
    try
    {
        var tokens = new Lexer(source).Tokenize();
        program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
    }
    catch (LexerException e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); return; }
    catch (ParseException e) { Console.Error.WriteLine(e.Message); Environment.Exit(1); return; }
    catch (TypeException e)  { Console.Error.WriteLine(e.Message); Environment.Exit(1); return; }

    try
    {
        var cSource  = new CodeGenerator().Generate(program);
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
        new TypeChecker().Check(program);
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
