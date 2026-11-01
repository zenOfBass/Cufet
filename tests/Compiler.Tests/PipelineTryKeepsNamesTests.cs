using Xunit;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
///
/// A name a `Try` body makes outlives it when every handler leaves (TypeChecker.KeepsBodyNames). The
/// body then opens no block of its own in either backend, so its names, and the unmakers of what it
/// made, are the enclosing block's — and a failure part-way still unmakes what the body made.
public class PipelineTryKeepsNamesTests : PipelineTestBase
{
    private const string Halve = """
        Bind number or failure to halve, given (the number n):
            If n is 0, return a failure "zero".
            Return n / 2.
        Done.

        """;

    [Fact]
    public void ANameMadeInATry_IsReadAfterIt()
    {
        const string body = """
            Bind text to describe, given (the number n):
                Try to:
                    Define half as cast halve on (n).
                    Define twice as half * 4.
                Done.
                In case of failure:
                    Return "no half of {n}: {the message of the failure}".
                Done.
                Return "half {half}, twice that {twice}".
            Done.
            State cast describe on (10).
            State cast describe on (0).
            For each n in a series with (4, 0, 6), repeat:
                Try to:
                    Define part as cast halve on (n).
                Done.
                In case of failure:
                    Skip.
                Done.
                State "part {part}".
            Done.
            """;
        var src = Halve + body;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("half 5, twice that 20\nno half of 0: zero\npart 2\npart 3", Interpret(src));
    }

    [Fact]
    public void WhatAKeptTryMakes_IsUnmadeWhereItsNamesEnd()
    {
        const string body = """
            Define object lamp with (the text name).
            Bind unmaking a lamp to switch-off:
                State "unmade {one's name}".
            Done.
            Bind void to at-frame-level, given (the number n):
                Try to:
                    Define bulb as a new lamp {the name "frame"}.
                    Define size as cast halve on (n).
                Done.
                In case of failure:
                    State "failed".
                    Return.
                Done.
                State "{bulb's name} at {size}".
            Done.
            Bind void to in-a-block, given (the number n):
                If true:
                    Try to:
                        Define bulb as a new lamp {the name "block"}.
                        Define size as cast halve on (n).
                    Done.
                    In case of failure:
                        State "failed".
                        Return.
                    Done.
                    State "{bulb's name} at {size}".
                Done.
                State "after the block".
            Done.
            Cast at-frame-level on (8).
            Cast at-frame-level on (0).
            Cast in-a-block on (8).
            Cast in-a-block on (0).
            If true:
                Cast at-frame-level on (6).
                State "the caller's block ends".
            Done.
            """;
        var src = Halve + body;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        // ⚠ The last call is the one that matters for memory: a frame-level `bulb` left registered
        // would be unmade at the CALLER's block end, through a pointer to a stack slot already gone.
        Assert.Equal("frame at 4\nunmade frame\nfailed\nblock at 4\nunmade block\nafter the block\nunmade block\nfailed"
                   + "\nframe at 3\nthe caller's block ends",
            Interpret(src));
    }
}
