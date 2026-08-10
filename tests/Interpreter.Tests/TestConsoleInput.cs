using System.Runtime.CompilerServices;

namespace Cufet.Interpreter.Tests;

// See tests/Compiler.Tests/TestConsoleInput.cs for the full story. Short version: `Interpreter`
// falls back to `Console.In` when given no reader, which is right for the CLI and wrong for a
// test — a program that reads input would consume the test host's stdin, and under `dotnet test`
// on Linux with a live pipe that blocks forever.
//
// One initializer per assembly, because a module initializer only covers its own.
internal static class TestConsoleInput
{
    [ModuleInitializer]
    internal static void RedirectToEof() => Console.SetIn(TextReader.Null);
}
