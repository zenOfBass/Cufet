using Cufet.Interpreter;
using Cufet.Lexer;
using System.Runtime.ExceptionServices;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Acceptance tests for the streaming pipe operator (|).
//
// v1 semantics: sequential / buffered.
//   Producer stage runs to completion, filling the implicit output channel.
//   Consumer stage then drains the channel.
//   True streaming (interleaved execution) is deferred to a future slice.
public class PipeTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var output  = new StringWriter();
        RunOnLargeStack(() => new Interpreter(output).Execute(program));
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static void RunOnLargeStack(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(
            () => { try { action(); } catch (Exception e) { caught = e; } },
            16 * 1024 * 1024);
        thread.Start();
        thread.Join();
        if (caught is not null)
            ExceptionDispatchInfo.Capture(caught).Throw();
    }

    // ── Two-stage task pipe ───────────────────────────────────────────────────────

    // Producer outputs numbers 1-5; consumer prints each one.
    [Fact]
    public void TwoStage_ProducerOutputsConsumerPrints()
    {
        var output = Run("""
            Bind void to producer:
              output 1.
              output 2.
              output 3.
              output 4.
              output 5.
            Done.

            Bind void to consumer:
              for each item from the input:
                State item.
              Done.
            Done.

            producer | consumer.
            """);
        Assert.Equal("1\n2\n3\n4\n5", output);
    }

    // Producer outputs 1-5; consumer accumulates and prints the sum.
    [Fact]
    public void TwoStage_ConsumerAccumulatesSum()
    {
        var output = Run("""
            Bind void to producer:
              output 1.
              output 2.
              output 3.
              output 4.
              output 5.
            Done.

            Bind void to consumer:
              Define total as 0.
              for each item from the input:
                total becomes total + item.
              Done.
              State total.
            Done.

            producer | consumer.
            """);
        Assert.Equal("15", output);
    }

    // ── Three-stage task pipe (middle stage) ─────────────────────────────────────

    // Producer → doubler → consumer: each item is doubled before printing.
    [Fact]
    public void ThreeStage_MiddleStageTransforms()
    {
        var output = Run("""
            Bind void to producer:
              output 1.
              output 2.
              output 3.
            Done.

            Bind void to doubler:
              for each item from the input:
                output item * 2.
              Done.
            Done.

            Bind void to consumer:
              for each item from the input:
                State item.
              Done.
            Done.

            producer | doubler | consumer.
            """);
        Assert.Equal("2\n4\n6", output);
    }

    // Four-stage pipeline: producer → add-ten → double → consumer
    [Fact]
    public void FourStage_TwoMiddleStages()
    {
        var output = Run("""
            Bind void to producer:
              output 1.
              output 2.
            Done.

            Bind void to add-ten:
              for each item from the input:
                output item + 10.
              Done.
            Done.

            Bind void to doubler:
              for each item from the input:
                output item * 2.
              Done.
            Done.

            Bind void to consumer:
              for each item from the input:
                State item.
              Done.
            Done.

            producer | add-ten | doubler | consumer.
            """);
        Assert.Equal("22\n24", output);
    }

    // ── Consumer terminates when producer closes the channel ─────────────────────

    // When a producer emits nothing, the consumer's for-each runs zero times.
    [Fact]
    public void EmptyProducer_ConsumerBodyNeverRuns()
    {
        var output = Run("""
            Bind void to producer:
            Done.

            Bind void to consumer:
              for each item from the input:
                State item.
              Done.
              State "done".
            Done.

            producer | consumer.
            """);
        Assert.Equal("done", output);
    }

    // ── Contextual recognition: 'output' as a variable name ──────────────────────

    // 'output' used as a variable name (not a pipe output keyword) must still work.
    [Fact]
    public void OutputAsVariableName_NoConflict()
    {
        Assert.Equal("42", Run("Define output as 42. State output."));
    }

    // 'output becomes' is reassignment, not a pipe output statement.
    [Fact]
    public void OutputBecomes_IsReassignment()
    {
        Assert.Equal("99", Run("Define output as 1. output becomes 99. State output."));
    }

    // 'output' followed by '|' is the left side of a pipe, not an output statement.
    [Fact]
    public void OutputPipe_VariableNameOnLeftOfPipe()
    {
        // 'output' here is a variable holding a function, piped into consumer.
        // This tests that IsOutputStatement() returns false when next token is Pipe.
        var output = Run("""
            Bind void to emit:
              output 7.
            Done.

            Bind void to print:
              for each item from the input:
                State item.
              Done.
            Done.

            Define output as emit.
            output | print.
            """);
        Assert.Equal("7", output);
    }

    // ── output keyword is contextual inside a pipe stage ─────────────────────────

    // 'output' as the first word of a statement inside a producer function IS a
    // pipe-output statement, even though 'output' is not a reserved keyword globally.
    [Fact]
    public void OutputInsideProducer_SendsToChannel()
    {
        var output = Run("""
            Bind void to emitter:
              output "hello".
              output "world".
            Done.

            Bind void to sink:
              for each item from the input:
                State item.
              Done.
            Done.

            emitter | sink.
            """);
        Assert.Equal("hello\nworld", output);
    }

    // ── for each item / for each <name> both work ────────────────────────────────

    // The iterator name in 'for each from the input' can be any identifier.
    [Fact]
    public void ConsumerCanUseCustomIteratorName()
    {
        var output = Run("""
            Bind void to producer:
              output 10.
              output 20.
            Done.

            Bind void to consumer:
              for each value from the input:
                State value.
              Done.
            Done.

            producer | consumer.
            """);
        Assert.Equal("10\n20", output);
    }

    // The reserved 'item' keyword as iterator name also works.
    [Fact]
    public void ConsumerCanUseItemKeywordAsIteratorName()
    {
        var output = Run("""
            Bind void to producer:
              output 5.
              output 6.
            Done.

            Bind void to consumer:
              for each item from the input:
                State item.
              Done.
            Done.

            producer | consumer.
            """);
        Assert.Equal("5\n6", output);
    }

    // ── Stop inside consumer loop ─────────────────────────────────────────────────

    [Fact]
    public void StopInsideConsumerLoop_ExitsEarly()
    {
        var output = Run("""
            Bind void to producer:
              output 1.
              output 2.
              output 3.
              output 4.
              output 5.
            Done.

            Bind void to consumer:
              for each item from the input:
                If item = 3, Stop.
                State item.
              Done.
            Done.

            producer | consumer.
            """);
        Assert.Equal("1\n2", output);
    }

    // ── Skip inside consumer loop ─────────────────────────────────────────────────

    [Fact]
    public void SkipInsideConsumerLoop_SkipsCurrentItem()
    {
        var output = Run("""
            Bind void to producer:
              output 1.
              output 2.
              output 3.
            Done.

            Bind void to consumer:
              for each item from the input:
                If item = 2, Skip.
                State item.
              Done.
            Done.

            producer | consumer.
            """);
        Assert.Equal("1\n3", output);
    }

    // ── Pipe can call functions defined as variables ───────────────────────────────

    // Stage references can be variable-held functions (lambdas or stored binds).
    [Fact]
    public void StageAsVariableHoldingFunction()
    {
        var output = Run("""
            Bind void to source:
              output 3.
              output 6.
              output 9.
            Done.

            Bind void to printer:
              for each item from the input:
                State item.
              Done.
            Done.

            Define stage as printer.
            source | stage.
            """);
        Assert.Equal("3\n6\n9", output);
    }

    // ── Subprocess pipe (expression position — captures result record) ─────────────

    // A subprocess pipe in expression position captures stdout as the 'output' field.
    // This is command substitution: 'Define x as the output of (run "a" | run "b")'.
    // Uses cmd.exe + findstr.exe — guaranteed on any Windows installation.
    [Fact]
    public void SubprocessPipe_ExprPosition_CapturesOutput()
    {
        var output = Run("""
            Try to:
                Define r as (run "cmd" with arguments ("/c", "echo", "hello") | run "findstr" with arguments ("hello")).
                State (the output of r).
            Done.
            In case of failure:
                State "caught".
            Done.
            """);
        Assert.Contains("hello", output);
    }

    // Exit code 0 when all stages succeed.
    // 'cmd /c exit 0' is the minimal Windows command that exits with a given code.
    [Fact]
    public void SubprocessPipe_ExprPosition_ExitCodeZeroOnSuccess()
    {
        var output = Run("""
            Try to:
                Define r as (run "cmd" with arguments ("/c", "exit", "0") | run "cmd" with arguments ("/c", "exit", "0")).
                State (the exit-code of r) converted to text.
            Done.
            In case of failure:
                State "caught".
            Done.
            """);
        Assert.Equal("0", output);
    }

    // Any-stage semantics: first stage exits non-zero, last stage exits 0 —
    // the pipe's exit-code still reflects the failing stage (rightmost non-zero = stage 1).
    [Fact]
    public void SubprocessPipe_ExprPosition_AnyStageFailureReflectedInExitCode()
    {
        var output = Run("""
            Try to:
                Define r as (run "cmd" with arguments ("/c", "exit", "1") | run "cmd" with arguments ("/c", "exit", "0")).
                State (the exit-code of r) converted to text.
            Done.
            In case of failure:
                State "launch-failed".
            Done.
            """);
        Assert.Equal("1", output);
    }

    // Non-zero exit is observable but NOT auto-fatal: no exception thrown, pipeline completes.
    [Fact]
    public void SubprocessPipe_ExprPosition_NonZeroNotAutoFatal()
    {
        var output = Run("""
            Try to:
                Define r as (run "cmd" with arguments ("/c", "exit", "1") | run "cmd" with arguments ("/c", "exit", "0")).
                State "no-throw".
            Done.
            In case of failure:
                State "threw".
            Done.
            """);
        Assert.Equal("no-throw", output);
    }

    // Launch failure (command not found) is still a catchable failure.
    [Fact]
    public void SubprocessPipe_ExprPosition_LaunchFailureCatchable()
    {
        var output = Run("""
            Try to:
                Define r as (run "nonexistent-xyz-cmd-abc" | run "cmd" with arguments ("/c", "exit", "0")).
                State "no-error".
            Done.
            In case of failure:
                State "caught: " joined to (the category of the failure but void is "unknown").
            Done.
            """);
        Assert.Equal("caught: not-found", output);
    }

    // exit-code of the full pipe is rightmost non-zero: stage 2 exits 2 after stage 1 exits 1.
    [Fact]
    public void SubprocessPipe_ExprPosition_RightmostNonZeroExitCode()
    {
        var output = Run("""
            Try to:
                Define r as (run "cmd" with arguments ("/c", "exit", "1") | run "cmd" with arguments ("/c", "exit", "2")).
                State (the exit-code of r) converted to text.
            Done.
            In case of failure:
                State "launch-failed".
            Done.
            """);
        Assert.Equal("2", output);
    }
}
