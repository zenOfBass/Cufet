using Cufet.Interpreter;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// <c>run &lt;prog&gt; with the terminal</c> — the expression form that hands the child this
/// program's own terminal and still returns its exit code.
/// </summary>
/// <remarks>
/// <para>
/// ⚠⚠ <b>THE ORACLE RULE FOR THIS FORM: the child must print nothing.</b> This form hands the
/// child this program's own stdout, and the two harnesses then disagree for harness reasons rather
/// than language ones — interpreted, the child writes to the real console rather than the
/// <c>TextWriter</c> <see cref="PipelineTestBase.InterpretRaw"/> injects, so the harness never sees
/// it; compiled, <see cref="PipelineTestBase.CompileRaw"/> captures the binary's stdout and the
/// child inherits exactly that, so it does. It failed on CI only, and
/// <c>PipelineStreamTests.cs</c> records the original. Every oracle test below therefore uses a
/// child that exits with a code and says nothing; the parent printing the exit code is the
/// parent's own output and compares fine.
/// </para>
/// <para>
/// ★ Linux-gated wherever a child actually runs, like every other subprocess test — not because
/// the launch is POSIX-only any more (the terminal form builds on Windows since the `_spawnvp`
/// path), but because these particular children are <c>sh</c>. The parse and type refusals need no
/// child and run everywhere. ⚠ The CAPTURING form is still POSIX-only, which is what keeps
/// <c>shell</c> and <c>repl</c> off Windows.
/// </para>
/// </remarks>
public class PipelineTerminalRunTests : PipelineTestBase
{
    /// <summary>An argument holding a space stays ONE argument, on both backends and both OSes.</summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ A MEASURED DIVERGENCE, caught the hour the Windows launch was written. POSIX hands argv to
    /// the child as a vector; Windows joins it into one command line that the child splits back up,
    /// and <c>_spawnvp</c> joins WITHOUT quoting — so <c>("two words", "plain")</c> reached the
    /// child as THREE arguments compiled and two interpreted. Not a display difference: argument
    /// boundaries were destroyed, and any program taking a path with a space in it was broken.
    /// </para>
    /// <para>
    /// ★ The child is COMPILED rather than borrowed from the OS, which is what lets this run
    /// everywhere — there is no argument-counting program both platforms ship. And it prints
    /// nothing, exiting with its own argument count, which is what the oracle rule above demands
    /// of this form: the parent's line is the only output either harness sees.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnArgumentHoldingASpace_StaysOneArgument()
    {
        var child = CompileToBinary("""
            Define count as the number of the arguments.
            Exit with count.
            """);
        try
        {
            var src = $$"""
                Try to:
                    Define seen as run "{{child.Replace('\\', '/')}}" with the terminal
                        with arguments ("two words", "plain").
                    State "count=" joined to (the exit-code of seen converted to text).
                Done.
                In case of failure:
                    State "failed: " joined to (the message of the failure).
                Done.
                """;

            var interpreted = InterpretRaw(src);
            Assert.Equal(interpreted, CompileRaw(src));
            // ⚠ And the value itself, or both backends agreeing on 3 would pass the line above.
            Assert.Contains("count=2", interpreted);
        }
        finally { try { File.Delete(child); } catch (IOException) { } }
    }

    /// <summary>A child that fills BOTH pipes is drained without deadlocking.</summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ THE FAILURE THIS GUARDS IS A HANG, not a wrong answer. Read stdout to the end first and a
    /// child that has filled its stderr pipe blocks forever waiting for someone to drain it, while
    /// the parent blocks waiting for more stdout. The POSIX path uses `poll`; Windows pipes are not
    /// pollable, so that side asks `PeekNamedPipe` which stream has bytes and never issues a read
    /// that could block. Both are easy to write in a way that works on small output and hangs on
    /// real output — which is why the sizes below are past the pipe buffer rather than illustrative.
    /// </para>
    /// <para>
    /// ★ Cufet has no way to write to stderr, so the child cannot be a Cufet program: it is the
    /// platform's own shell running a loop. That is also why this is Linux-gated like its
    /// neighbours — the Windows half of the same code is exercised by hand and by `shell.cufe`,
    /// which is no longer skipped there.
    /// </para>
    /// </remarks>
    [LinuxFact]
    public void AChildFillingBothPipes_IsDrainedWithoutDeadlock()
    {
        // ★ 3000 lines of ~60 bytes on each stream — comfortably past the 64 KB a pipe will hold,
        // so a one-stream-then-the-other reader cannot get to the end of this.
        const string src = """
            Try to:
                Define loud as run "sh" with arguments (
                    "-c",
                    "i=0; while [ $i -lt 3000 ]; do echo \"OUT $i padding padding padding padding padding\"; echo \"ERR $i padding padding padding padding padding\" 1>&2; i=$((i+1)); done; exit 7").
                State "exit=" joined to (the exit-code of loud converted to text).
                State "out=" joined to (the length of (the output of loud) converted to text).
                State "err=" joined to (the length of (the errors of loud) converted to text).
            Done.
            In case of failure:
                State "failed: " joined to (the message of the failure).
            Done.
            """;

        var interpreted = InterpretRaw(src);
        Assert.Equal(interpreted, CompileRaw(src));
        // ⚠ And that it really did overflow the buffer, or a child that printed nothing would pass.
        Assert.Contains("exit=7", interpreted);
        Assert.DoesNotContain("failed:", interpreted);
        var outLength = int.Parse(interpreted.Split("out=")[1].Split('\n')[0].Trim());
        Assert.True(outLength > 100_000, $"expected well over one pipe buffer, got {outLength}");
    }

    [LinuxFact]
    public void TerminalRun_ReportsTheRealExitCodeAndEmptyStreams()
    {
        // `sh -c "exit 3"` prints nothing, which is what makes this comparable at all.
        const string src = """
            Try to:
                Define quiet as run "sh" with the terminal with arguments ("-c", "exit 3").
                State "exit=" joined to (the exit-code of quiet converted to text).
                State "out=[" joined to (the output of quiet) joined to "]".
                State "err=[" joined to (the errors of quiet) joined to "]".
                Define fine as run "sh" with the terminal with arguments ("-c", "exit 0").
                State "clean=" joined to (the exit-code of fine converted to text).
            Done.
            In case of failure:
                State "launch-failed".
            Done.
            """;

        // ★ A nonzero exit is a normal result, not a failure — the same rule the capturing form
        // follows. Reaching "launch-failed" here would mean the form had started treating a
        // program's own answer as an error.
        Assert.Contains("exit=3", Interpret(src));
        Assert.DoesNotContain("launch-failed", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void TerminalRun_AChildKilledBySignal_Reports128PlusTheSignal()
    {
        // ★★ 128 + signum is the universal shell convention, and both backends already derived it
        // this way before this form existed — the compiled runtime through cufet_exit_status, .NET
        // through Process.ExitCode. SIGINT is 2, so a child that kills itself reports 130.
        const string src = """
            Try to:
                Define killed as run "sh" with the terminal with arguments ("-c", "kill -INT $$").
                State "killed=" joined to (the exit-code of killed converted to text).
            Done.
            In case of failure:
                State "launch-failed".
            Done.
            """;

        Assert.Contains("killed=130", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void TerminalRun_ALaunchFailure_IsAFailureLikeTheOtherForms()
    {
        const string src = """
            Try to:
                Define gone as run "no-such-command-zzz" with the terminal.
                State "exit=" joined to (the exit-code of gone converted to text).
            Done.
            In case of failure:
                State "cat: " joined to (the category of the failure but void is "none").
            Done.
            """;

        Assert.Contains("cat: not-found", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void TerminalRun_ModifiersComposeInEitherOrder()
    {
        // ⚠ The fourth case is the one that was expected to fight and did not: a series argument
        // followed by `with the terminal` means the series expression sits directly before a `with`,
        // and `with` is also how a record shape is spelled. It parses.
        const string src = """
            Define argv as a series of text with ("-c", "exit 5").
            Try to:
                Define alpha as run "sh" with the terminal with arguments ("-c", "exit 5").
                Define beta as run "sh" with arguments ("-c", "exit 5") with the terminal.
                Define gamma as run "sh" with the terminal with arguments argv.
                Define delta as run "sh" with arguments argv with the terminal.
                State "alpha=" joined to (the exit-code of alpha converted to text).
                State "beta=" joined to (the exit-code of beta converted to text).
                State "gamma=" joined to (the exit-code of gamma converted to text).
                State "delta=" joined to (the exit-code of delta converted to text).
            Done.
            In case of failure:
                State "launch-failed".
            Done.
            """;

        Assert.Equal("alpha=5\nbeta=5\ngamma=5\ndelta=5", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void TerminalRun_AChildThatPrints_StillReportsEmptyStreams()
    {
        // ⚠ NOT oracle-compared, and that is the rule above being obeyed rather than an omission:
        // this child prints on purpose, so the two harnesses see its bytes in different places.
        // What is asserted is the record — which must be empty either way, because the bytes went
        // to the terminal instead of into a pipe.
        const string src = """
            Try to:
                Define loud as run "sh" with the terminal with arguments ("-c", "echo to-the-screen; echo also-here 1>&2").
                State "out=[" joined to (the output of loud) joined to "]".
                State "err=[" joined to (the errors of loud) joined to "]".
            Done.
            In case of failure:
                State "launch-failed".
            Done.
            """;

        string compiled = Compile(src);
        Assert.Contains("out=[]", compiled);
        Assert.Contains("err=[]", compiled);
    }

    [Fact]
    public void TerminalRun_OutsideATry_IsRefusedLikeEveryOtherFailableForm()
    {
        var ex = Assert.Throws<TypeException>(() =>
            Interpret("""Define gone as run "sh" with the terminal."""));

        Assert.Contains("failure", ex.Message);
    }

    [Fact]
    public void TerminalRun_AsAStatement_IsRefusedAndSaysWhereToPutIt()
    {
        // ⚠ Refused rather than quietly treated as a plain `Run …`: the whole reason to say
        // `with the terminal` is to get the exit code back, and a statement has nowhere to put it,
        // so accepting this would silently discard the only thing the modifier is for.
        var ex = Assert.Throws<ParseException>(() =>
            Interpret("""
                Try to:
                    Run "sh" with the terminal.
                Done.
                In case of failure:
                    State "no".
                Done.
                """));

        Assert.Contains("nowhere to put it", ex.Message);
        Assert.Contains("Define", ex.Message);   // the message must show the way out, not just refuse
    }

    [Fact]
    public void TerminalRun_InAPipe_IsRefusedAsAContradiction()
    {
        // ★ Not a limitation — a contradiction. A pipe carries the child's output to the next
        // stage; this form gives that output to the screen. Both asks want the same bytes.
        var ex = Assert.Throws<TypeException>(() =>
            Interpret("""
                Try to:
                    Define piped as run "echo" with the terminal with arguments ("hi") | run "cat".
                    State the output of piped.
                Done.
                In case of failure:
                    State "no".
                Done.
                """));

        Assert.Contains("pipe", ex.Message);
        Assert.Contains("with the terminal", ex.Message);
    }

    [Fact]
    public void SayingAModifierTwice_IsRefusedRatherThanTakingTheLast()
    {
        Assert.Throws<ParseException>(() =>
            Interpret("""Define x as run "sh" with the terminal with the terminal."""));

        Assert.Throws<ParseException>(() =>
            Interpret("""Define x as run "sh" with arguments ("a") with arguments ("b")."""));
    }

    [Fact]
    public void Terminal_IsAnOrdinaryIdentifierEverywhereElse()
    {
        // ★★ The word is matched by LEXEME in the run-modifier slot, never reserved. Reserving it
        // would take `Define terminal as …` from every program forever and collide with the bundled
        // terminal module for nothing — the module is reached through Pull, a different slot.
        Assert.Equal("5", Interpret("""
            Define terminal as 5.
            State terminal.
            """));
    }
}
