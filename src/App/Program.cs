using NLP.Lexer;
using NLP.Interpreter;

var source = args.Length > 0
    ? File.ReadAllText(args[0])
    : Console.In.ReadToEnd();

var tokens  = new Lexer(source).Tokenize();
var program = new Parser(tokens).Parse();
new TypeChecker().Check(program);
new Interpreter().Execute(program);
