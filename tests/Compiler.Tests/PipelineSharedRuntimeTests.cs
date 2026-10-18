using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>Which parts of the C runtime every build carries.</summary>
///
/// ★ The matrix, chance, signal and concurrency runtimes used to be written only into a program
/// that used them, from when the runtime was pasted into every generated file. It is one object
/// now, linked by every program — and the compiler written in Cufet links that object ALONE, so a
/// runtime kept out of it was out of that compiler's reach. Measured 2026-10-18 before the move:
/// no warning on mingw or WSL gcc at -Wall -Wextra, no new link flag, nothing run before main, and
/// about 14 KB on a hello-world.
public class PipelineSharedRuntimeTests
{
    [Fact]
    public void AProgramUsingNoneOfThem_StillCarriesTheMatrixChanceSignalAndConcurrencyRuntimes()
    {
        var (_, runtime, program) = Split("State 1.");
        foreach (var defined in new[] { "cufet_mat_write", "cufet_rng_below", "cufet_install_sigint", "cufet_free_all_chans" })
        {
            Assert.Contains(defined, runtime);
            Assert.DoesNotContain(defined, program);
        }
    }

    private static (string Header, string Runtime, string Program) Split(string source)
    {
        var tokens = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        program = new TypeChecker().Check(program);
        return new CodeGenerator().GenerateSplit(program);
    }
}
