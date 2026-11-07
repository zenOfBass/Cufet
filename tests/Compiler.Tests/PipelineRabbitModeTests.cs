using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>Composition modes for rabbits, compiled against interpreted.</summary>
/// <remarks>
/// ★ Every program here is deterministic on real threads: what an automatic rabbit chooses depends
/// only on the ORDER things were sent, and these send before anything chooses; a sequential
/// rabbit's tasks are ordered by construction. A program whose sends race its choice would print
/// in either order compiled, and is no oracle test.
/// </remarks>
public class PipelineRabbitModeTests : PipelineTestBase
{
    [Fact]
    public void Automatic_TakesWhateverWasSentFirst_AcrossChannels()
    {
        const string src = """
            Pull a rabbit.
                Define orders as a channel of text.
                Define cancels as a channel of number.
                Send 17 through cancels.
                Send "kale" through orders.
                Send "carrots" through orders.
                Send 18 through cancels.
                Close orders.
                For each turn in range 1 to 6, repeat:
                    Pull an automatic rabbit as dispatcher.
                        Have dispatcher start a task for an order in orders:
                            State "order {order but void is "(closed)"}".
                        Done.
                        Have dispatcher start a task for a ticket in cancels:
                            State "ticket {ticket but void is 0}".
                        Done.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal("ticket 17\norder kale\norder carrots\nticket 18\norder (closed)\norder (closed)", Interpret(src));
        Assert.Equal(Interpret(src), Compile(src));
    }

    // What an offered task captured is copied in when it is offered — and freed, not leaked, when
    // another task is chosen. A series crosses the channel whole.
    [Fact]
    public void Automatic_CapturesAndSeries_CrossOnlyIntoTheChosenTask()
    {
        const string src = """
            Pull a rabbit.
                Define batches as a channel of series of number.
                Define notes as a channel of text.
                Define label as "batch".
                Send "first note" through notes.
                Send a series with (1, 2, 3) through batches.
                For each turn in range 1 to 2, repeat:
                    Pull an automatic rabbit.
                        Have rabbit start a task for a batch in batches:
                            Define numbers as batch but void is a series with (0).
                            Define total as 0.
                            For each n in numbers, repeat:
                                The total becomes total + n.
                            Done.
                            State "{label} totals {total}".
                        Done.
                        Have rabbit start a task for a note in notes:
                            State "{label} note: {note but void is ""}".
                        Done.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal("batch note: first note\nbatch totals 6", Interpret(src));
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Automatic_TwoTasksOnOneChannel_TheFirstWrittenTakesIt()
    {
        const string src = """
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
            """;
        Assert.Equal("first 1", Interpret(src));
        Assert.Equal(Interpret(src), Compile(src));
    }

    [Fact]
    public void Sequential_RunsItsTasksOneAtATime_InTheOrderStarted()
    {
        const string src = """
            Pull a sequential rabbit.
                Have rabbit start a task:
                    State "task 1".
                Done.
                Have rabbit start a task:
                    State "task 2".
                Done.
                Have rabbit start a task:
                    State "task 3".
                Done.
            Done.
            """;
        Assert.Equal("task 1\ntask 2\ntask 3", Interpret(src));
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ★ A task whose body pulls a rabbit and starts a task of its own — what an automatic rabbit
    // inside a worker needs. Its C used to land inside the outer task's, and gcc refused it.
    [Fact]
    public void ATaskInsideATasksRabbit_Builds()
    {
        const string src = """
            Pull a rabbit.
                Have rabbit start a task:
                    Pull a rabbit.
                        Have rabbit start a task:
                            State "inner".
                        Done.
                    Done.
                    State "outer".
                Done.
            Done.
            """;
        Assert.Equal("inner\nouter", Interpret(src));
        Assert.Equal(Interpret(src), Compile(src));
    }

    // Every task of a sequential rabbit still runs when one fails, and the faults are reported
    // together, in the order started — as a parallel rabbit's are.
    [Fact]
    public void Sequential_AFailingTask_DoesNotStopTheNext()
    {
        const string src = """
            Pull a sequential rabbit.
                Have rabbit start a task:
                    State item 5 of a series with (1).
                Done.
                Have rabbit start a task:
                    State "second ran".
                Done.
            Done.
            """;
        AssertFaultOracle(src);
        Assert.StartsWith("second ran", InterpretThroughFaultRaw(src));
    }
}
