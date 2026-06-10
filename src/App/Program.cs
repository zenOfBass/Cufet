using Cufet.Lexer;
using Cufet.Interpreter;
using System.Runtime.ExceptionServices;

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
catch (LexerException e)      { Console.Error.WriteLine(e.Message); Environment.Exit(1); }
catch (ParseException e)      { Console.Error.WriteLine(e.Message); Environment.Exit(1); }
catch (TypeException e)       { Console.Error.WriteLine(e.Message); Environment.Exit(1); }
catch (RuntimeException e)    { Console.Error.WriteLine(e.Message); Environment.Exit(1); }

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
