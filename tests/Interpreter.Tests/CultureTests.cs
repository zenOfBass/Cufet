using System.Globalization;
using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// A program means the same thing on every machine, whatever locale that machine is set to.
/// </summary>
/// <remarks>
/// <para>
/// !! This was not true, and the way it failed is the worst shape available: SILENTLY, with the
/// wrong answer. A number literal was read with <c>decimal.Parse</c> and no culture, so on a
/// German machine `1.5` was fifteen — the decimal point taken for a thousands separator — and the
/// program went on to compute and print with it. On a French one the same literal threw a raw
/// FormatException out of the parser. Only en-US was right.
/// </para>
/// <para>
/// ⚠⚠ And the COMPILER never had the fault: it emits a literal as raw decimal bits, and C's own
/// formatting is locale-independent. So the same program printed `1.5` compiled and `15`
/// interpreted — a DIVERGENCE that no oracle in this suite could have caught, because every
/// machine that runs it is en-US. It was reachable by anyone outside the English-speaking world
/// who typed a decimal point, and by every visitor to the playground, where the culture is the
/// browser's.
/// </para>
/// <para>
/// ★ The rule is the one the language already states about line endings: a program cannot mean
/// two things depending on where it is opened. Source text is source text.
/// </para>
/// </remarks>
public class CultureTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var parsed  = new Parser(tokens).Parse();
        var program = new TypeChecker().Check(parsed);
        var output  = new System.IO.StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    /// <summary>Runs `body` with the thread's culture pinned, and puts it back afterwards.</summary>
    /// <remarks>
    /// ⚠ Restored in a `finally`. `CurrentCulture` is per-thread and xUnit reuses its threads, so
    /// leaking one would quietly re-run some LATER test under a culture it never asked for — the
    /// same class of fault this file exists to close, moved into the suite itself.
    /// </remarks>
    private static void Under(string culture, Action body)
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            body();
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    // de-DE reads '.' as a thousands separator — the silent-corruption case.
    // fr-FR refuses the literal outright — the crash case.
    // en-US is the one that always worked, kept so the test proves it did not regress.
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void ANumberLiteral_MeansTheSameUnderAnyCulture(string culture) =>
        Under(culture, () => Assert.Equal("1.5\n1234.75", Run("""
            State 1.5.
            State 1234.75.
            """)));

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void ArithmeticOnLiterals_IsTheSameUnderAnyCulture(string culture) =>
        // ! The half that mattered most and would have been missed by checking output alone: under
        // de-DE this printed 30, because both operands had been read as whole numbers. The answer
        // was wrong, not merely spelled differently.
        Under(culture, () => Assert.Equal("3", Run("State 1.5 + 1.5.")));

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void ANumberIsPrinted_TheSameUnderAnyCulture(string culture) =>
        // The other direction: a computed value on its way out. `1,5` is what a comma culture
        // printed, and output is half of what a program means.
        Under(culture, () => Assert.Equal("0.5", Run("""
            Define the half as 1 / 2.
            State the half.
            """)));
}
