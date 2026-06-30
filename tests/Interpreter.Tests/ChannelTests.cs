using Cufet.Interpreter;
using Cufet.Lexer;
using System.Runtime.ExceptionServices;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Acceptance tests for concurrency core slice 3: channels.
//   a channel of T, Send <value> through <channel>., the delivery from <channel>, Close <channel>.
public class ChannelTests
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

    // ── Producer / consumer — the canonical pattern ───────────────────────────

    // A task sends three numbers; the main flow receives them via delivery-until-void.
    [Fact]
    public void ProducerConsumerBasic()
    {
        var output = Run("""
            Pull a rabbit.
                Define ch as a channel of number.
                Have rabbit start a task:
                    Send 1 through ch.
                    Send 2 through ch.
                    Send 3 through ch.
                    Close ch.
                Done.
                Define val as the delivery from ch.
                While val is not void, repeat:
                    State val.
                    val becomes the delivery from ch.
                Done.
            Done.
            """);
        Assert.Equal("1\n2\n3", output);
    }

    // ── Channel creation and type annotation ─────────────────────────────────

    // "a channel of number" can be stored in a named variable and used immediately.
    [Fact]
    public void ChannelCreationAndSingleDelivery()
    {
        var output = Run("""
            Pull a rabbit.
                Define ch as a channel of number.
                Send 42 through ch.
                Close ch.
                Define got as the delivery from ch.
                State got.
            Done.
            """);
        Assert.Equal("42", output);
    }

    // Channel of text works end-to-end.
    [Fact]
    public void ChannelOfText()
    {
        var output = Run("""
            Pull a rabbit.
                Define ch as a channel of text.
                Send "hello" through ch.
                Close ch.
                Define got as the delivery from ch.
                State got.
            Done.
            """);
        Assert.Equal("hello", output);
    }

    // ── Void on closed-empty ──────────────────────────────────────────────────

    // Delivering from a closed empty channel immediately returns void.
    [Fact]
    public void DeliveryFromClosedEmptyChannelIsVoid()
    {
        var output = Run("""
            Pull a rabbit.
                Define ch as a channel of number.
                Close ch.
                Define result as the delivery from ch.
                If result is void, State "void". Otherwise, State "not void".
            Done.
            """);
        Assert.Equal("void", output);
    }

    // Double-close is a no-op (idempotent).
    [Fact]
    public void DoubleCloseIsNoOp()
    {
        var output = Run("""
            Pull a rabbit.
                Define ch as a channel of number.
                Send 7 through ch.
                Close ch.
                Close ch.
                Define got as the delivery from ch.
                State got.
            Done.
            """);
        Assert.Equal("7", output);
    }

    // ── Deep-copy at send ────────────────────────────────────────────────────

    // Mutating a series after sending it should not affect the delivered copy.
    [Fact]
    public void DeepCopyAtSendIsolatesSeries()
    {
        var output = Run("""
            Pull a rabbit.
                Define ch as a channel of series of number.
                Define original as a series of number with (10, 20, 30).
                Send original through ch.
                Add 99 to original.
                Close ch.
                Define delivered as the delivery from ch.
                State the number of delivered.
            Done.
            """);
        // original was mutated to 4 elements, but delivered copy should still be 3
        Assert.Equal("3", output);
    }

    // ── Type checking ─────────────────────────────────────────────────────────

    // Sending the wrong type through a typed channel is a type error.
    [Fact]
    public void SendWrongTypeIsTypeError()
    {
        Assert.Throws<TypeException>(() => Run("""
            Pull a rabbit.
                Define ch as a channel of number.
                Send "text" through ch.
            Done.
            """));
    }

    // Using a non-channel as the target of "Send through" is a type error.
    [Fact]
    public void SendThroughNonChannelIsTypeError()
    {
        Assert.Throws<TypeException>(() => Run("""
            Pull a rabbit.
                Define notch as 5.
                Send 1 through notch.
            Done.
            """));
    }

    // Using a non-channel in "the delivery from" is a type error.
    [Fact]
    public void DeliveryFromNonChannelIsTypeError()
    {
        Assert.Throws<TypeException>(() => Run("""
            Pull a rabbit.
                Define notch as 5.
                Define got as the delivery from notch.
            Done.
            """));
    }

    // Closing a non-channel is a type error.
    [Fact]
    public void CloseNonChannelIsTypeError()
    {
        Assert.Throws<TypeException>(() => Run("""
            Pull a rabbit.
                Define notch as 5.
                Close notch.
            Done.
            """));
    }

    // "the delivery from ch" infers as voidable T, so "is void" compiles without error.
    [Fact]
    public void DeliveryInfersVoidableT()
    {
        var output = Run("""
            Pull a rabbit.
                Define ch as a channel of number.
                Close ch.
                Define v as the delivery from ch.
                If v is void, State "ok".
            Done.
            """);
        Assert.Equal("ok", output);
    }

    // ── Send on closed channel is a runtime error ─────────────────────────────

    [Fact]
    public void SendOnClosedChannelIsRuntimeError()
    {
        Assert.Throws<RuntimeException>(() => Run("""
            Pull a rabbit.
                Define ch as a channel of number.
                Close ch.
                Send 1 through ch.
            Done.
            """));
    }

    // ── Multi-value drain loop ────────────────────────────────────────────────

    // A task sends five numbers; consumer sums them via delivery-until-void loop.
    [Fact]
    public void DrainLoopCollectsAllValues()
    {
        var output = Run("""
            Pull a rabbit.
                Define ch as a channel of number.
                Have rabbit start a task:
                    Define i as 1.
                    While i is 5 or less, repeat:
                        Send i through ch.
                        i becomes i + 1.
                    Done.
                    Close ch.
                Done.
                Define sum as 0.
                Define val as the delivery from ch.
                While val is not void, repeat:
                    sum becomes sum + (val but void is 0).
                    val becomes the delivery from ch.
                Done.
                State sum.
            Done.
            """);
        Assert.Equal("15", output);
    }
}
