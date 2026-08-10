using System.Runtime.CompilerServices;

namespace Cufet.Compiler.Tests;

// A test must never read the console.
//
// `Interpreter`'s constructor does `_in = input ?? Console.In`, which is right for the real CLI
// and wrong here: roughly thirty helpers across the test projects build an interpreter with no
// reader, so a program that reads input silently consumes whatever the TEST HOST's stdin happens
// to be. What that is depends entirely on how the suite was launched — and under `dotnet test` on
// Linux with a live pipe it is a handle that never delivers and never closes.
//
// Measured: a test-host thread parked in `pipe_read` with no child process and zero CPU, the whole
// suite stopped behind it. It cannot happen on Windows (the inherited handle gives EOF) and cannot
// happen in CI or under the mutation harness (both redirect from /dev/null), so only an
// interactive run through wsl.exe ever showed it.
//
// ★ Fixed once, here, rather than at thirty call sites — the same reasoning as keying an AST walk
// on the namespace instead of listing node types. A module initializer runs before any test, so a
// helper added tomorrow inherits the fix without knowing it exists.
//
// The compiled backend has the same hazard through a different mechanism (a child process
// inheriting the host's stdin) and is handled separately, at each launch site, guarded by
// ExhaustivenessTests.EveryCompiledBinaryLauncher_ClosesStdin.
internal static class TestConsoleInput
{
    [ModuleInitializer]
    internal static void RedirectToEof() => Console.SetIn(TextReader.Null);
}
