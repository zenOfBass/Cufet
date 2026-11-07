using Cufet.Interpreter;
using System.Runtime.ExceptionServices;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Composition modes for rabbits: `Pull an automatic rabbit.` (Occam's ALT — the one task whose
// delivery was SENT FIRST runs), `Pull a sequential rabbit.` (one task at a time, in the order
// started), and `parallel`, the default.
public class RabbitModeTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        program = new TypeChecker().Check(program);
        var output  = new StringWriter();
        Exception? caught = null;
        var thread = new Thread(
            () => { try { new Interpreter(output).Execute(program); } catch (Exception e) { caught = e; } },
            16 * 1024 * 1024);
        thread.Start();
        thread.Join();
        if (caught is not null) ExceptionDispatchInfo.Capture(caught).Throw();
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static string Refusal(string source) =>
        Assert.Throws<TypeException>(() => Run(source)).Message;

    // ★ The rule the user chose: whatever was SENT first wins — across channels, and whichever
    // task was written first. Ticket 17 went in before either order, so it is handled first.
    [Fact]
    public void Automatic_TakesWhateverWasSentFirst_AcrossChannels()
    {
        Assert.Equal("ticket 17\norder kale\norder carrots\nticket 18", Run("""
            Pull a rabbit.
                Define orders as a channel of text.
                Define cancels as a channel of number.
                Send 17 through cancels.
                Send "kale" through orders.
                Send "carrots" through orders.
                Send 18 through cancels.
                For each round in range 1 to 4, repeat:
                    Pull an automatic rabbit as dispatcher.
                        Have dispatcher start a task for an order in orders:
                            State "order {order but void is "none"}".
                        Done.
                        Have dispatcher start a task for a ticket in cancels:
                            State "ticket {ticket but void is 0}".
                        Done.
                    Done.
                Done.
            Done.
            """));
    }

    // A closed, empty channel is ready too: its task runs, with void — exactly what `the delivery
    // from` gives once a channel is closed and empty.
    [Fact]
    public void Automatic_AClosedEmptyChannel_RunsItsTaskWithVoid()
    {
        Assert.Equal("got 5\nclosed", Run("""
            Pull a rabbit.
                Define numbers as a channel of number.
                Send 5 through numbers.
                Close numbers.
                For each round in range 1 to 2, repeat:
                    Pull an automatic rabbit.
                        Have rabbit start a task for a reading in numbers:
                            If reading is void:
                                State "closed".
                            Done.
                            Otherwise:
                                State "got {reading}".
                            Done.
                        Done.
                    Done.
                Done.
            Done.
            """));
    }

    // It WAITS for a delivery, and another task's send is what releases it.
    [Fact]
    public void Automatic_WaitsForADeliveryFromAnotherTask()
    {
        Assert.Equal("heard hello", Run("""
            Pull a rabbit.
                Define words as a channel of text.
                Define quiet as a channel of number.
                Have rabbit start a task:
                    Send "hello" through words.
                Done.
                Pull an automatic rabbit.
                    Have rabbit start a task for a word in words:
                        State "heard {word but void is ""}".
                    Done.
                    Have rabbit start a task for a reading in quiet:
                        State "never".
                    Done.
                Done.
            Done.
            """));
    }

    // Two tasks waiting on one channel see the same item; the one written first takes it.
    [Fact]
    public void Automatic_TwoTasksOnOneChannel_TheFirstWrittenTakesIt()
    {
        Assert.Equal("first 1", Run("""
            Pull a rabbit.
                Define numbers as a channel of number.
                Send 1 through numbers.
                Pull an automatic rabbit.
                    Have rabbit start a task for a reading in numbers:
                        State "first {reading but void is 0}".
                    Done.
                    Have rabbit start a task for a reading in numbers:
                        State "second {reading but void is 0}".
                    Done.
                Done.
            Done.
            """));
    }

    [Fact]
    public void Sequential_RunsItsTasksOneAtATime_InTheOrderStarted()
    {
        Assert.Equal("one\ntwo\nthree", Run("""
            Pull a sequential rabbit.
                Have rabbit start a task:
                    State "one".
                Done.
                Have rabbit start a task:
                    State "two".
                Done.
                Have rabbit start a task:
                    State "three".
                Done.
            Done.
            """));
    }

    [Fact]
    public void Parallel_IsTheDefault_SaidOutLoud()
    {
        Assert.Equal("ran", Run("""
            Pull a parallel rabbit.
                Have rabbit start a task:
                    State "ran".
                Done.
            Done.
            """));
    }

    // ★ None of the three words is reserved: each is a mode only where `rabbit` follows it.
    [Fact]
    public void TheModeWords_StayFreeAsNames()
    {
        Assert.Equal("6", Run("""
            Define automatic as 1.
            Define sequential as 2.
            Define parallel as 3.
            State automatic + sequential + parallel.
            """));
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public void Automatic_ATaskThatWaitsOnNothing_IsRefused() =>
        Assert.Contains("every task of an automatic rabbit waits on a channel", Refusal("""
            Pull an automatic rabbit.
                Have rabbit start a task:
                    State "x".
                Done.
            Done.
            """));

    [Fact]
    public void AWaitingTask_OutsideAnAutomaticRabbit_IsRefused() =>
        Assert.Contains("only a task of an automatic rabbit waits on a channel", Refusal("""
            Pull a rabbit.
                Define orders as a channel of text.
                Have rabbit start a task for an order in orders:
                    State "x".
                Done.
            Done.
            """));

    [Fact]
    public void Automatic_ANamedTask_IsRefused() =>
        Assert.Contains("cannot be named 'packer'", Refusal("""
            Pull a rabbit.
                Define orders as a channel of text.
                Pull an automatic rabbit.
                    Have rabbit start a task for an order in orders as packer:
                        State "x".
                    Done.
                Done.
            Done.
            """));

    [Fact]
    public void Automatic_WaitingOnSomethingNotAChannel_IsRefused() =>
        Assert.Contains("would come from a number", Refusal("""
            Pull a rabbit.
                Define orders as 5.
                Pull an automatic rabbit.
                    Have rabbit start a task for an order in orders:
                        State "x".
                    Done.
                Done.
            Done.
            """));

    [Fact]
    public void Automatic_ADeliveryNamedLikeSomethingInScope_IsRefused() =>
        Assert.Contains("'order' already names something here", Refusal("""
            Pull a rabbit.
                Define orders as a channel of text.
                Define order as 1.
                Pull an automatic rabbit.
                    Have rabbit start a task for an order in orders:
                        State "x".
                    Done.
                Done.
            Done.
            """));

    [Fact]
    public void Automatic_ATaskGivenFromInsideOneOfItsTasks_IsRefused() =>
        Assert.Contains("from inside one of its own tasks", Refusal("""
            Pull a rabbit.
                Define orders as a channel of text.
                Pull an automatic rabbit.
                    Have rabbit start a task for an order in orders:
                        Have rabbit start a task for an other in orders:
                            State "x".
                        Done.
                    Done.
                Done.
            Done.
            """));

    // The delivery is `voidable T`, so using it as a T without handling void is refused.
    [Fact]
    public void Automatic_TheDeliveryIsVoidable() =>
        Assert.Contains("voidable text", Refusal("""
            Pull a rabbit.
                Define orders as a channel of text.
                Pull an automatic rabbit.
                    Have rabbit start a task for an order in orders:
                        Define size as the length of order.
                    Done.
                Done.
            Done.
            """));
}
