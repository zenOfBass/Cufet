using Xunit;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
///
/// `Define shorthand gate as (and-gate or or-gate).` The front end puts the type back wherever the
/// name is written (ShorthandExpansion), so neither backend meets a shorthand; what these hold is that
/// the program each is handed is the written-out one, in every place a type can be written.
public class PipelineShorthandTests : PipelineTestBase
{
    [Fact]
    public void AShorthand_IsTheTypeItStandsFor_Everywhere()
    {
        const string src = """
            Define object circle with (the number radius).
            Define object square with (the number side).
            Define object line with ().
            Define shorthand shape as (circle or square).
            Define shorthand shapes as series of shape.
            Define shorthand drawing as (shape or line).
            Define shorthand maybe-shape as (shape or void).
            Bind number to area, given (the shape figure):
                Judge figure, where it is:
                    A circle, return it's radius * it's radius * 3.
                    A square, return it's side * it's side.
                Done.
            Done.
            Define all as a series of shape with (a new circle {the radius 2}, a new square {the side 3}).
            For each one-shape in all, repeat:
                State cast area on (one-shape).
            Done.
            Define the maybe-shape nothing as void.
            State nothing is void.
            Define the drawing pen as a new line {}.
            If pen is a line, state "a line".
            If item 1 of all is a circle, state "a circle".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("12\n9\ntrue\na line\na circle", Interpret(src));
    }

    [Fact]
    public void AShorthand_InAMapAndAFunctionType()
    {
        const string src = """
            Define shorthand key as (number or text).
            Define shorthand counter as number function given (the number).
            Define the key by-number as 1.
            Define the key by-word as "one".
            Define tally as a map from key to number.
            In tally, the entry for by-number becomes 10.
            In tally, the entry for by-word becomes 20.
            State the entry for by-number in tally but void is 0.
            State the entry for by-word in tally but void is 0.
            Define the counter twice as a function given (the number n): Return n * 2. Done.
            State cast twice on (21).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("10\n20\n42", Interpret(src));
    }
}
