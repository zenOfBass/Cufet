namespace NLP.Interpreter;

public sealed class RuntimeException : Exception
{
    public RuntimeException(string message) : base(message) { }
}
