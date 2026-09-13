using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

/// <summary>
/// A book the program never pulls costs it nothing — including the book's PRIVATE HELPERS.
/// </summary>
///
/// ⚠⚠ This existed as a claim in GRAMMAR long before it was true. `DropUnpulledLayers` dropped a
/// bundled book's module object and left everything declared beside it, so the bodies were emitted
/// into every compiled program on earth. MEASURED 2026-09-13: `State "hello".` carried the whole
/// pattern engine — `cv_match_in_regex`, `cv_reach_in_regex`, `cv_accepting_in_regex`,
/// `cv_blank_in_regex`.
///
/// ★★ It stayed invisible because it was only dead weight. It stopped being invisible the moment a
/// book's helper used something the runtime SPLIT trims away: the `blueprints` walker runs a
/// subprocess, `cufet_run_inherit` is emitted only for a program that runs one, and every compiled
/// program failed to link at once. Dead weight is silent until it is load-bearing.
///
/// ★ The two tests below are a PAIR and neither means anything alone. Dropping everything would
/// pass the first; dropping nothing would pass the second.
public class BookDroppingTests
{
    private static string ProgramHalf(string source)
    {
        var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        program = new TypeChecker().Check(program);
        var (_, _, programHalf) = new CodeGenerator().GenerateSplit(program);
        return programHalf;
    }

    [Fact]
    public void AProgramThatPullsNothing_CarriesNoBookHelpers()
    {
        var emitted = ProgramHalf("""
            State "hello".
            """);

        Assert.DoesNotContain("_in_regex", emitted);
        Assert.DoesNotContain("_in_blueprints", emitted);
    }

    /// <remarks>
    /// ⚠ The guard against over-dropping, and the half that would catch a fix that went too far.
    /// A pulled book's engine must still be there — the program cannot run without it.
    /// </remarks>
    [Fact]
    public void AProgramThatPullsRegex_StillCarriesTheEngine()
    {
        var emitted = ProgramHalf("""
            Pull a book on regex.
                Define regex digits as [^\d+$].
                State (cast digits on ("12345")) converted to text.
            Done.
            """);

        Assert.Contains("_in_regex", emitted);
        // ⚠ And still nothing from a book this program does NOT pull, so the drop is per-book
        // rather than all-or-nothing.
        Assert.DoesNotContain("_in_blueprints", emitted);
    }
}
