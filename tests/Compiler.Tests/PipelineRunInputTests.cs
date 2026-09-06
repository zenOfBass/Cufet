using Cufet.Interpreter;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// <c>run &lt;prog&gt; with input &lt;text&gt;</c> — feeding a child's standard input.
/// </summary>
/// <remarks>
/// <para>
/// ★★ Before this there was <b>no way at all</b> for a program to supply a child's stdin: the only
/// path was to be a later stage of a <c>|</c> pipeline, and a pipeline is written into the source.
/// A shell cannot write one — it learns how many commands there are when someone types them. With
/// this, a shell writes its own pipeline as a loop over whatever it parsed, and <c>&lt;</c> is a
/// file read handed to the child. Neither needed a new stream kind, because a Cufet pipeline was
/// never OS pipes: <c>cufet_run_capture</c> takes stdin as TEXT and writes it to a temporary file.
/// </para>
/// <para>
/// ⚠ These are oracle-compared, unlike the terminal form's: this is the CAPTURING form, so the
/// child's output comes back in the record rather than going to a descriptor the two harnesses see
/// differently.
/// </para>
/// </remarks>
public class PipelineRunInputTests : PipelineTestBase
{
    [LinuxFact]
    public void RunWithInput_FeedsTheChildsStandardInput()
    {
        const string src = """
            Try to:
                Define shouted as run "tr" with arguments ("a-z", "A-Z") with input "hello there".
                State the output of shouted.
                State "exit=" joined to (the exit-code of shouted converted to text).
            Done.
            In case of failure:
                State "launch-failed".
            Done.
            """;

        Assert.Equal("HELLO THERE\nexit=0", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void EmptyInput_IsAnImmediateEndOfInput_NotAnAbsentOne()
    {
        // ⚠ `with input ""` and no `with input` at all are DIFFERENT, and the difference is
        // load-bearing: the first hands the child an immediate end-of-input, the second leaves it
        // reading whatever this program reads. A child told to count what it is given must see
        // nothing, not block on a terminal.
        const string src = """
            Try to:
                Define counted as run "wc" with arguments ("-c") with input "".
                State "bytes=" joined to ((the output of counted) trimmed).
            Done.
            In case of failure:
                State "launch-failed".
            Done.
            """;

        Assert.Equal("bytes=0", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void ThePipelineAShellCannotWrite_IsWritableAsALoop()
    {
        // ★★ THE POINT OF THE WHOLE FEATURE, and the acceptance test for it. The stages are a value,
        // so their number is not known until this runs — which is exactly the shell's problem, and
        // exactly what `run A | run B` cannot express because it is written into the source.
        const string src = """
            Define stages as a series of text with ("sort", "uniq", "head").
            Try to:
                Define carried as "delta\nalpha\ncharlie\nalpha\nbravo\n".
                For each stage in the stages, repeat:
                    Define step as run stage with input carried.
                    The carried becomes the output of step.
                Done.
                State carried.
            Done.
            In case of failure:
                State "launch-failed".
            Done.
            """;

        Assert.Equal("alpha\nbravo\ncharlie\ndelta", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void TheFirstStageOfAPipe_MayBeFed()
    {
        // `cmd < file | other` — the redirect feeds the head of the pipe, and the rest is carried.
        const string src = """
            Try to:
                Define shouted as run "tr" with arguments ("a-z", "A-Z") with input "piped" | run "tr" with arguments ("A-Z", "a-z").
                State the output of shouted.
            Done.
            In case of failure:
                State "launch-failed".
            Done.
            """;

        Assert.Equal("piped", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void ALaterStageOfAPipe_CannotBeFedTwice()
    {
        var ex = Assert.Throws<TypeException>(() =>
            Interpret("""
                Try to:
                    Define piped as run "echo" with arguments ("hi") | run "cat" with input "no".
                    State the output of piped.
                Done.
                In case of failure:
                    State "no".
                Done.
                """));

        Assert.Contains("only the first stage", ex.Message);
    }

    [Fact]
    public void FeedingAChildThatWasGivenTheKeyboard_IsRefused()
    {
        // ⚠ A contradiction rather than a limitation: `with the terminal` hands over the real
        // keyboard and `with input` types for it. Both are about one descriptor.
        var ex = Assert.Throws<TypeException>(() =>
            Interpret("""
                Try to:
                    Define both as run "cat" with the terminal with input "typed".
                    State the exit-code of both.
                Done.
                In case of failure:
                    State "no".
                Done.
                """));

        Assert.Contains("with the terminal", ex.Message);
        Assert.Contains("with input", ex.Message);
    }

    [Fact]
    public void InputMustBeText()
    {
        var ex = Assert.Throws<TypeException>(() =>
            Interpret("""
                Try to:
                    Define fed as run "cat" with input 5.
                    State the output of fed.
                Done.
                In case of failure:
                    State "no".
                Done.
                """));

        Assert.Contains("must be text", ex.Message);
    }

    [Fact]
    public void SayingWithInputTwice_IsRefused()
    {
        Assert.Throws<ParseException>(() =>
            Interpret("""Define x as run "cat" with input "a" with input "b"."""));
    }

    [Fact]
    public void TheModifierDoesNotDisturbTheGlobalNamedInput()
    {
        // ⚠ `input` is not a free word: it is already bound at global scope to THIS program's
        // standard input stream. The modifier does not reserve it, take it, or shadow it — the two
        // are the same noun with different owners, and `run X with input Y` says whose is whose by
        // putting the value right after it.
        Assert.Equal("", Interpret("""
            Define line as read a line from input but void is "".
            State line.
            """, stdin: ""));
    }
}
