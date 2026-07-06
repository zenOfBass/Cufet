using Cufet.Compiler;
using Cufet.Interpreter;
using Cufet.Lexer;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

if (args.Length >= 2 && args[0].Equals("build", StringComparison.OrdinalIgnoreCase))
    Build(args[1]);
else
    Interpret(args);

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
