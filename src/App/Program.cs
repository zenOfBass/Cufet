using Cufet.Compiler;
using Cufet.Interpreter;
using Cufet.Lexer;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length >= 2 && args[0].Equals("build", StringComparison.OrdinalIgnoreCase))
    Build(args[1]);
else if (args.Length >= 2 && args[0].Equals("emit-c", StringComparison.OrdinalIgnoreCase))
    EmitC(args[1], args.Length >= 3 ? args[2] : Path.ChangeExtension(args[1], ".c"));
else if (args.Length >= 2 && args[0].Equals("check", StringComparison.OrdinalIgnoreCase))
    Check(args[1..]);
else
    Interpret(args);

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
// "<path>:<line>: <severity>: <first line>", with the rest of a multi-line message indented
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
        Report(json, full, LineOf(e), "error", e.Message);
        Environment.Exit(1);
        return;
    }

    // Codegen refusals are compiler-only. The interpreter runs these programs happily, so they
    // are a warning — "this won't build natively" — and not an error. Most carry no line, so
    // they land on line 1; the message names what to change.
    if (native)
    {
        try { new CodeGenerator().Generate(program); }
        catch (CompilerException e)
        {
            Report(json, full, LineOf(e), "warning", e.Message);
            Environment.Exit(1);
            return;
        }
    }

    if (!json) Console.WriteLine($"No problems found in {path}.");
    Environment.Exit(0);
}

// Where a diagnostic points. Lexer and parse errors carry the line structurally. Type errors
// do not — TypeException holds only a message — but nearly all of them are built by
// FormatTypeError, which always writes "Here on line N, you're trying to ...". That anchor is
// tried first precisely because a message may mention several lines ("established on line 4")
// and the violation is the one to underline. Anything else falls back to the first line
// mentioned, then to line 1, so a diagnostic is never dropped for want of a location.
static int LineOf(Exception e) => e switch
{
    LexerException le => le.Line,
    ParseException pe => pe.Line,
    _                 => LineFromMessage(e.Message),
};

static int LineFromMessage(string message)
{
    var anchored = Regex.Match(message, @"Here on line (\d+)");
    if (anchored.Success) return int.Parse(anchored.Groups[1].Value);
    var mentioned = Regex.Match(message, @"\bline (\d+)", RegexOptions.IgnoreCase);
    return mentioned.Success ? int.Parse(mentioned.Groups[1].Value) : 1;
}

static void Report(bool json, string file, int line, string severity, string message)
{
    if (json)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new { file, line, severity, message }));
        return;
    }
    var lines = message.Replace("\r\n", "\n").Split('\n');
    Console.Error.WriteLine($"{file}:{line}: {severity}: {lines[0]}");
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
    var source = args.Length > 0
        ? File.ReadAllText(args[0])
        : Console.In.ReadToEnd();

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
