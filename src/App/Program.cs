using Cofet.Lexer;
using Cofet.Interpreter;
using System.Runtime.ExceptionServices;

var source = args.Length > 0
    ? File.ReadAllText(args[0])
    : Console.In.ReadToEnd();

var tokens  = new Lexer(source).Tokenize();
var program = new Parser(tokens).Parse();
new TypeChecker().Check(program);
RunOnLargeStack(() => new Interpreter().Execute(program));

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
