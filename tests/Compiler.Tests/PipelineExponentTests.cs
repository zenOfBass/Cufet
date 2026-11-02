using Xunit;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
///
/// Exponents, `6.022e23`, in a literal and in a text converted to number — and what does not fit
/// exactly ROUNDS in both, half to even, as .NET's decimal parse does. The C runtime's
/// cufet_parse_number is a port of that parse; before it, a text with more than 28 places was void
/// compiled and rounded interpreted.
public class PipelineExponentTests : PipelineTestBase
{
    [Fact]
    public void AnExponentLiteral_MeansTheNumberWrittenOut()
    {
        const string src = """
            Define avogadro as 6.022e23.
            State avogadro.
            State 1.5E3 + 1e-3.
            State 2e+2 - 2E-2.
            State 0 - 1e3.
            State 1e-40 is 0.
            State 7.9228162514264337593543950335e28.
            State 0.123456789012345678901234567891e0.
            State 1.24E-16 * 1e16.
            Define e as 3.
            State 2 * e.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("602200000000000000000000\n1500.001\n199.98\n-1000\ntrue\n"
            + "79228162514264337593543950335\n0.1234567890123456789012345679\n1.24\n6", Interpret(src));
    }

    /// <remarks>
    /// The expected values are .NET's own `decimal.TryParse(…, NumberStyles.Float, …)` answers,
    /// taken from .NET directly rather than from either backend.
    /// </remarks>
    [Fact]
    public void ATextConvertedToNumber_RoundsAsTheLiteralDoes()
    {
        string[] cases =
        [
            "0.12345678901234567890123456785", "0.12345678901234567890123456775",
            "0.123456789012345678901234567850000001", "0.12345678901234567890123456784999999",
            "12345678901234567890.123456789012", "79228162514264337593543950335.4",
            "79228162514264337593543950335.5", "7922816251426433759354395033.55",
            "7922816251426433759354395033.45", "1e28", "1e29", "5e-29", "6e-29", "2.5e-28",
            "1.24E-16", "12e+2", "1e999999999", "1e-999999999", "3.0500000000000000000001e-27",
            "-1.5e3", " 2e2 ", "1e", "1.e3", "+1e3", "1e00000000000000000003", "1e100000000",
        ];
        var src = string.Join("\n", cases.Select(c => $"State \"{c}\" converted to number."));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal(string.Join("\n",
            "0.1234567890123456789012345678", "0.1234567890123456789012345678",
            "0.1234567890123456789012345679", "0.1234567890123456789012345678",
            "12345678901234567890.123456789", "79228162514264337593543950335",
            "void", "7922816251426433759354395034",
            "7922816251426433759354395033.4", "10000000000000000000000000000", "void", "0",
            "0.0000000000000000000000000001", "0.0000000000000000000000000002",
            "0.000000000000000124", "1200", "void", "0", "0.0000000000000000000000000031",
            "-1500", "200", "void", "void", "void", "1000", "void"), Interpret(src));
    }
}
