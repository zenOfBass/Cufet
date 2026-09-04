using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
public class PipelineChannelTests : PipelineTestBase
{
    // a stage closes its output on return so completion cascades down the pipe. Values stream FIFO,
    // so a linear pipe's output is DETERMINISTIC and matches the interpreter's buffered-sequential
    // order → these ARE true Compile == Interpret oracle tests (the final stage is the only writer).

    [LinuxFact]
    public void TaskPipe_TwoStage_ProducerConsumer()
    {
        const string src = """
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
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void TaskPipe_ConsumerAccumulatesSum()
    {
        // The consumer drains the whole stream, then prints the aggregate — order-independent (15).
        const string src = """
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
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void TaskPipe_ThreeStage_MiddleTransforms()
    {
        // A middle stage both consumes (from the input) AND produces (to the output) — the value
        // crosses two channel boundaries (producer→doubler→consumer). FIFO preserves order → 2,4,6.
        const string src = """
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
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void TaskPipe_FourStage_TwoMiddleTransforms()
    {
        // Two middle stages chained (producer → add-ten → double → consumer): 3 channels, 4 threads.
        const string src = """
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
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void TaskPipe_EmptyProducer_ConsumerBodyNeverRuns()
    {
        // Producer emits nothing and closes; the consumer's drain loop sees void immediately (zero
        // iterations) and continues to its trailing statement. Close-cascades with an empty stream.
        const string src = """
            Bind void to producer:
            Done.
            Bind void to consumer:
              for each item from the input:
                State item.
              Done.
              State "done".
            Done.
            producer | consumer.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void TaskPipe_StopInsideConsumer_ExitsEarly()
    {
        // Stop breaks the consumer's drain loop early — values still in flight remain unreceived in
        // the channel and are freed at teardown (the never-received-bridge free path; see ASan test).
        const string src = """
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
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void TaskPipe_SkipInsideConsumer_SkipsCurrentItem()
    {
        const string src = """
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
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void TaskPipe_EarlyStop_ASan_FreesPendingBridges()
    {
        // The consumer stops at item 3, so items 4 and 5 are produced-but-never-received — they sit
        // in the channel as heap-bridges when the pipe tears down. cufet_chan_free must free those
        // pending nodes (close-with-pending), and every stage-thread + channel frees cleanly. The
        // final stage's `State` output stays deterministic (1,2). ASan/LSan must be clean.
        const string src = """
            Bind void to producer:
              for each n in the range 1 to 20, repeat:
                output n.
              Done.
            Done.
            Bind void to consumer:
              for each item from the input:
                If item = 3, Stop.
                State item.
              Done.
            Done.
            producer | consumer.
            """;
        Assert.Equal(Interpret(src), CompileSanitized(src));
    }

    // ── channel-of-T: channels + task-pipe streams of any element type ──
    // The number-only channel is generalized to a type-erased container with a per-element-type deep
    // copy at the boundary (heap bridge on send, arena copy on recv). A single-producer/single-consumer
    // channel streams FIFO, so the consumer's printed output is deterministic and matches the
    // interpreter's fill-then-drain order → these are true Compile == Interpret oracle tests.

    [LinuxFact]
    public void Channel_OfText_MatchesInterpreter()
    {
        const string src = """
            Pull a rabbit.
                Define ch as a channel of text.
                Have rabbit start a task as producer:
                    Define s as "hello".
                    Send s through ch.
                    Send "world" through ch.
                    Close ch.
                Done.
                Have rabbit start a task as consumer:
                    Define got as the delivery from ch.
                    While got is not void, repeat:
                        State (got but void is "?").
                        got becomes the delivery from ch.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void Channel_OfObject_MatchesInterpreter()
    {
        const string src = """
            Define object person with (the text name, the number age).
            Pull a rabbit.
                Define ch as a channel of person.
                Have rabbit start a task as producer:
                    Define p as a new person { the name "Ada", the age 36 }.
                    Send p through ch.
                    Close ch.
                Done.
                Have rabbit start a task as consumer:
                    Define got as the delivery from ch.
                    While got is not void, repeat:
                        Define r as (got but void is (a new person { the name "?", the age 0 })).
                        State "name=" joined to (the name of r) joined to " age=" joined to ((the age of r) converted to text).
                        got becomes the delivery from ch.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void Channel_OfSeries_DeepCopyIsolation_MatchesInterpreter()
    {
        // The producer mutates the ORIGINAL series after sending; the consumer's arena copy must be
        // unaffected (len=2, not 3). This is the A+B deep-copy isolation, now for a reference element.
        const string src = """
            Pull a rabbit.
                Define ch as a channel of series of text.
                Have rabbit start a task as producer:
                    Define xs as a series of text with ("p", "q").
                    Send xs through ch.
                    Insert "MUT" into xs.
                    Close ch.
                Done.
                Have rabbit start a task as consumer:
                    Define got as the delivery from ch.
                    While got is not void, repeat:
                        Define r as (got but void is (a series of text with ())).
                        State "len=" joined to ((the number of r) converted to text) joined to " first=" joined to (the first of r).
                        got becomes the delivery from ch.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void Channel_OfObjectWithSeriesField_DeepCopyIsolation_MatchesInterpreter()
    {
        // The crux of a GENUINELY-deep copy: the element is an object whose field is a series. The
        // producer mutates the inner series after sending; the whole nested structure must cross the
        // boundary arena-independently, so the consumer's copy still reads nums-len=3 (not 4).
        const string src = """
            Define object bundle with (the text label, the series of number nums).
            Pull a rabbit.
                Define ch as a channel of bundle.
                Have rabbit start a task as producer:
                    Define ns as a series of number with (1, 2, 3).
                    Define b as a new bundle { the label "first", the nums ns }.
                    Send b through ch.
                    Insert 999 into ns.
                    Close ch.
                Done.
                Have rabbit start a task as consumer:
                    Define got as the delivery from ch.
                    While got is not void, repeat:
                        Define r as (got but void is (a new bundle { the label "?", the nums (a series of number with ()) })).
                        State (the label of r) joined to " nums-len=" joined to ((the number of (the nums of r)) converted to text).
                        got becomes the delivery from ch.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void Channel_OfMap_DeepCopyIsolation_MatchesInterpreter()
    {
        const string src = """
            Pull a rabbit.
                Define ch as a channel of map from text to number.
                Have rabbit start a task as producer:
                    Define m as a map from text to number with ("a" : 1, "b" : 2).
                    Send m through ch.
                    In m, the entry for "c" becomes 3.
                    Close ch.
                Done.
                Have rabbit start a task as consumer:
                    Define got as the delivery from ch.
                    While got is not void, repeat:
                        Define r as (got but void is (a map from text to number with ())).
                        State "size=" joined to ((the size of r) converted to text) joined to " a=" joined to ((the entry for "a" in r but void is 0) converted to text).
                        got becomes the delivery from ch.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void TextPipe_ThreeStage_MatchesInterpreter()
    {
        // The capability channel-of-T unblocks: a text pipe. Producer emits text, a middle stage
        // transforms text→text, consumer prints. Linear pipe + FIFO ⇒ deterministic ⇒ oracle test.
        const string src = """
            Bind void to producer:
              output "a".
              output "bb".
              output "ccc".
            Done.
            Bind void to shout:
              for each w from the input:
                output (w joined to "!").
              Done.
            Done.
            Bind void to consumer:
              for each w from the input:
                State w.
              Done.
            Done.
            producer | shout | consumer.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void Channel_OfReference_ASan_DeepCopyFreesAllPaths()
    {
        // Nested reference elements (series-of-series and map-of-series) cross channels while the
        // producers mutate their originals. Every heap bridge — the whole nested tree — must free on
        // every path (received-and-arena-copied, then the bridge freed; teardown of any pending). The
        // deep-copy isolation invariant (outer-len=1, inner-len=3, batch-len=3) must hold, ASan-clean.
        const string src = """
            Pull a rabbit.
                Define ch-list as a channel of series of series of number.
                Define ch-map  as a channel of map from text to series of number.
                Have rabbit start a task as list-producer:
                    Define inner as a series of number with (10, 20, 30).
                    Define outer as a series of series of number with ().
                    Insert inner into outer.
                    Send outer through ch-list.
                    Insert 999 into inner.
                    Insert (a series of number with (7, 8, 9)) into outer.
                    Close ch-list.
                Done.
                Have rabbit start a task as map-producer:
                    Define batch as a series of number with (1, 2, 3).
                    Define data as a map from text to series of number with ().
                    In data, the entry for "batch" becomes batch.
                    Send data through ch-map.
                    Insert 999 into batch.
                    Close ch-map.
                Done.
                Have rabbit start a task as list-consumer:
                    Define received as the delivery from ch-list.
                    While received is not void, repeat:
                        Define r as (received but void is (a series of series of number with ())).
                        Define inner-copy as the first of r.
                        State "outer-len=" joined to (the number of r) converted to text.
                        State "inner-len=" joined to (the number of inner-copy) converted to text.
                        received becomes the delivery from ch-list.
                    Done.
                Done.
                Have rabbit start a task as map-consumer:
                    Define received as the delivery from ch-map.
                    While received is not void, repeat:
                        Define r as (received but void is (a map from text to series of number with ())).
                        Define batch-copy as (the entry for "batch" in r but void is (a series of number with ())).
                        State "batch-len=" joined to (the number of batch-copy) converted to text.
                        received becomes the delivery from ch-map.
                    Done.
                Done.
            Done.
            """;
        // Two independent producer/consumer pairs → their interleaving is nondeterministic, but each
        // consumer's own lines are internally ordered; assert ASan-clean + the isolation invariant via
        // the compiled run alone (not bit-identical to the interpreter's serialized task ordering).
        var outText = CompileSanitized(src);
        Assert.Contains("outer-len=1", outText);
        Assert.Contains("inner-len=3", outText);
        Assert.Contains("batch-len=3", outText);
    }

    // ── CONC.E: native SIGINT (true-preemptive interrupt) ──
    // The deterministic (no-signal) cases are ordinary Compile == Interpret oracle tests and run on
    // BOTH platforms (the signal substrate degrades to no-op stubs on mingw). The actual SIGINT-
    // delivery cases are Linux-only (POSIX signal delivery) and assert the invariant — the program
    // stops + unwinds cleanly (exit 130), NOT bit-identical timing (interrupt timing is nondeterministic).

    [Fact]
    public void Interrupt_NotRequested_PollReadsFalse()
    {
        // `an interrupt is requested` reads the flag as a fact; with no interrupt it is false.
        const string src = """
            Define r as an interrupt is requested.
            If r, State "interrupted". Otherwise, State "ok".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Interrupt_CooperativeAcknowledge_ClearsFlag()
    {
        // `Acknowledge the interrupt.` clears the flag (cooperative handling). With no interrupt the
        // else-branch runs — exercises that Acknowledge compiles and the poll path is wired.
        const string src = """
            If an interrupt is requested:
                Acknowledge the interrupt.
                State "handled".
            Done.
            Otherwise:
                State "normal".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Yield_NoInterrupt_ResumesNormally()
    {
        // `Yield.` with no pending interrupt is a no-op checkpoint — the loop runs to completion.
        const string src = """
            Define count as 0.
            While count is less than 3, repeat:
                Yield.
                count becomes count + 1.
            Done.
            State count.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [LinuxFact]
    public void Interrupt_TightYieldLoop_PreemptivelyStops()
    {
        // A non-terminating tight loop with a Yield checkpoint: a delivered SIGINT unwinds it to a
        // clean exit (130) — it prints its pre-loop line but never reaches the post-loop line. This
        // is the true-preemptive interrupt; the invariant is "stops cleanly", not timing.
        var (code, output) = CompileAndInterrupt("""
            State "looping".
            While 1 is 1, repeat:
                Yield.
            Done.
            State "never".
            """, 500);
        Assert.Equal(130, code);
        Assert.Equal("looping", output);
    }

    [LinuxFact]
    public void Interrupt_BlockedChannelWait_WakesAndStops()
    {
        // The flagship: main blocks in a real pthread_cond_wait on an empty channel (something the
        // cooperative interpreter can't truly do). A delivered SIGINT wakes the blocked wait, unwinds
        // to a clean exit (130), and the interrupt teardown frees the channel + arenas. Prints its
        // pre-wait line, never the post-wait line.
        var (code, output) = CompileAndInterrupt("""
            State "waiting".
            Pull a rabbit.
                Define ch as a channel of number.
                Define v as the delivery from ch.
                State "got it".
            Done.
            State "after".
            """, 500);
        Assert.Equal(130, code);
        Assert.Equal("waiting", output);
    }

    // ── INT.1: worker tasks are interruptible too ─────────────────────────────────────────────
    // Before this, only main and pipe-stage threads established a landing pad, so a worker's
    // `cufet_checkpoint()` was a silent no-op — and main is parked in pthread_join at the rabbit's
    // Done., which is not a checkpoint. Every case below HUNG INDEFINITELY on Ctrl-C (measured).
    // The 3-second-ish timeouts in these tests are the real assertion: a regression re-hangs.

    [LinuxFact]
    public void Interrupt_InsideWorkerTask_UnwindsAndStops()
    {
        var (code, output) = CompileAndInterrupt("""
            Pull a rabbit.
                Have rabbit start a task:
                    Define i as 0.
                    While 1 is 1, repeat:
                        i becomes i + 1.
                        Yield.
                    Done.
                Done.
            Done.
            State "finished".
            """, 500);
        Assert.Equal(130, code);
        Assert.Equal("", output);   // never reaches "finished"
    }

    [LinuxFact]
    public void Interrupt_WorkerTaskBlockedOnChannel_UnwindsAndStops()
    {
        // Previously the interrupted recv returned -1, the no-op checkpoint let it fall through as
        // "stream closed", and the task carried on as though it had been handed a value.
        var (code, output) = CompileAndInterrupt("""
            Pull a rabbit.
                Define work as a channel of number.
                Have rabbit start a task:
                    Define job as the delivery from work.
                    State "got something".
                Done.
            Done.
            State "finished".
            """, 500);
        Assert.Equal(130, code);
        Assert.Equal("", output);
    }

    [LinuxFact]
    public void Interrupt_NamedTaskPendingAwait_DoesNotDereferenceNull()
    {
        // An abandoned task yields NULL instead of a result envelope; the await has to notice
        // rather than hand NULL to arenacopy. ASan-clean in WSL.
        var (code, output) = CompileAndInterrupt("""
            Pull a rabbit.
                Have rabbit start a task as slow:
                    Define i as 0.
                    While 1 is 1, repeat:
                        i becomes i + 1.
                        Yield.
                    Done.
                    return 1.
                Done.
                State the awaited result of slow.
            Done.
            State "finished".
            """, 500);
        Assert.Equal(130, code);
        Assert.Equal("", output);
    }

    [LinuxFact]
    public void Interrupt_InsideWorkerTask_StillRunsDestructors()
    {
        // The unwind runs the thread's pending unmakers and closes its files before tearing down —
        // both registries are _Thread_local, so a worker only ever touches its own.
        var (code, output) = CompileAndInterrupt(UnmakerHdr + """
            Pull a rabbit.
                Have rabbit start a task:
                    Pull a rabbit.
                        Define h as a new handle { the id "IN-TASK" }.
                        Define i as 0.
                        While 1 is 1, repeat:
                            i becomes i + 1.
                            Yield.
                        Done.
                    Done.
                Done.
            Done.
            State "finished".
            """, 500);
        Assert.Equal(130, code);
        Assert.Equal("unmake IN-TASK", output);
    }

    [LinuxFact]
    public void Subprocess_Run_MemorySafety_ASan_ZeroLeaksAndNoUAF()
    {
        // Process handles are reaped (waitpid) and fds closed inside the run primitive, so nothing
        // leaks across statements; capture buffers are arena/free-managed. ASan/LSan must be clean.
        const string src = """
            Pull a rabbit.
                For each n in the range 1 to 10, repeat:
                    Try to:
                        Define r as run "echo" with arguments ("hi") | run "cat".
                        State the output of r.
                    Done.
                    In case of failure:
                        State "fail".
                    Done.
                Done.
            Done.
            """;
        string expected = Interpret(src);
        string actual   = CompileSanitized(src);
        Assert.Equal(expected, actual);
    }

    [LinuxFact]
    public void Arena_MemorySafety_ASan_ZeroLeaksAndNoUAF()
    {
        // Validates arena correctness: compiled binary must pass AddressSanitizer
        // (zero leaks, zero use-after-free, zero dangling pointer reads).
        // Skipped on non-Linux where ASan support is unreliable.

        const string src = """
            Pull a rabbit.
                Define xs as a series of number with (1, 2, 3).
                Insert 4 into xs.
                Pull a rabbit.
                    Define ys as a series of number with (10, 20).
                    Insert 30 into ys.
                    For each y in ys, repeat:
                        Insert y into xs.
                    Done.
                Done.
                For each x in xs, repeat:
                    State x.
                Done.
            Done.
            """;
        string expected = Interpret(src);
        string actual   = CompileSanitized(src);
        Assert.Equal(expected, actual);
    }

    [LinuxFact]
    public void Text_MemorySafety_ASan_ZeroLeaksAndNoUAF()
    {
        // The string runtime cooperates with the arena: a text-op-heavy Pull block allocates
        // many runtime strings (join/case/substring/replace/convert) that must all free at
        // Done. — zero leaks / UAF. Proves immutable arena strings are memory-clean. Linux-only.

        const string src = """
            Pull a rabbit.
                For each n in the range 1 to 25, repeat:
                    Define label as "item-" joined to (n converted to text).
                    Define up as label in uppercase.
                    Define slice as the characters from 1 to 4 of up.
                    Define rep as replace "T" with "x" in slice.
                    State rep joined to " " joined to (the length of label converted to text).
                Done.
            Done.
            """;
        string expected = Interpret(src);
        string actual   = CompileSanitized(src);
        Assert.Equal(expected, actual);
    }

    [LinuxFact]
    public void Map_MemorySafety_ASan_ZeroLeaksAndNoUAF()
    {
        // The last arena-allocated type: a map with nested reference values (series) plus
        // growth (append past initial capacity). The map, its key/value arrays, and the nested
        // series must all free cleanly at Done. — zero leaks / UAF. Linux-only.

        const string src = """
            Pull a rabbit.
                Define scores as a map from text to series of number with ("a": a series of number with (1, 2)).
                In scores, the entry for "b" becomes a series of number with (3, 4, 5).
                In scores, the entry for "c" becomes a series of number with (6).
                In scores, the entry for "d" becomes a series of number with (7).
                In scores, the entry for "e" becomes a series of number with (8).
                For each pair in scores, repeat:
                    State the key of pair.
                    State the value of pair.
                Done.
                State the size of scores.
            Done.
            """;
        string expected = Interpret(src);
        string actual   = CompileSanitized(src);
        Assert.Equal(expected, actual);
    }

    [LinuxFact]
    public void File_ReadResults_MemorySafety_ASan_ZeroLeaksAndNoUAF()
    {
        // File-read results are arena-allocated (the text buffer, the line array, each line string)
        // and must free at Done. — zero leaks / UAF. Proves the OS-error bridge + read results
        // cooperate with the arena, reusing the string/series arena model. Linux-only.

        var path = Path.Combine(Path.GetTempPath(), "cufet-io-asan-" + Guid.NewGuid().ToString("N") + ".txt")
            .Replace('\\', '/');
        var src = $$"""
            Pull a rabbit.
                Write "line one\nline two\nline three" to the file "{{path}}".
                For each n in the range 1 to 20, repeat:
                    Try to:
                        Define whole as read all from the file "{{path}}".
                        Define lines as read all lines from the file "{{path}}".
                        State the length of whole.
                        State the number of lines.
                    Done.
                    In case of failure:
                        State "fail".
                    Done.
                Done.
            Done.
            """;
        try
        {
            string expected = Interpret(src);
            string actual   = CompileSanitized(src);
            Assert.Equal(expected, actual);
        }
        finally { try { File.Delete(path.Replace('/', Path.DirectorySeparatorChar)); } catch { } }
    }

    [LinuxFact]
    public void With_StreamsAndCleanup_MemorySafety_ASan_ZeroLeaksAndNoUAF()
    {
        // Streams + close-on-all-paths inside a Pull: each iteration opens a file, writes, reopens,
        // reads (arena strings + line series), and closes on normal exit — plus arena churn. No
        // leaks / UAF (the FILE* handles all close; the arena frees at Done). Linux-only.

        var path = Path.Combine(Path.GetTempPath(), "cufet-io-with-asan-" + Guid.NewGuid().ToString("N") + ".txt")
            .Replace('\\', '/');
        var src = $$"""
            Pull a rabbit.
                For each n in the range 1 to 15, repeat:
                    With the file "{{path}}" open for writing as out:
                        write "line-a\nline-b\nline-c" to out.
                    Done.
                    With the file "{{path}}" open for reading as inp:
                        Define lines as read all lines from inp.
                        State the number of lines.
                    Done.
                Done.
            Done.
            """;
        try
        {
            string expected = Interpret(src);
            string actual   = CompileSanitized(src);
            Assert.Equal(expected, actual);
        }
        finally { try { File.Delete(path.Replace('/', Path.DirectorySeparatorChar)); } catch { } }
    }

    [LinuxFact]
    public void Series_Heterogeneous_MemorySafety_ASan_ZeroLeaksAndNoUAF()
    {
        // Generalized series across element types in one Pull block: a series of text (arena
        // strings from split), a series of records (value structs), and a nested series of series.
        // The whole structure — series bookkeeping plus each element's own allocations — must free
        // cleanly at Done. This is the "generalized series is arena-clean" proof. Linux-only.

        const string src = """
            Pull a rabbit.
                Define words as "alpha,beta,gamma,delta" split by ",".
                Insert "epsilon" into words.
                For each w in words, repeat:
                    State w in uppercase.
                Done.
                Define people as a series with (a record with (the name "Alice", the age 30)).
                Insert a record with (the name "Bob", the age 25) into people.
                For each p in people, repeat:
                    State the name of p.
                Done.
                Define grid as a series with (a series with (1, 2), a series with (3, 4, 5)).
                Insert a series with (6) into grid.
                For each row in grid, repeat:
                    State the number of row.
                Done.
            Done.
            """;
        string expected = Interpret(src);
        string actual   = CompileSanitized(src);
        Assert.Equal(expected, actual);
    }

    // ── CL.1: closure substrate — function VALUES, no capture (uniform {fn, env} with NULL env) ──
    // A FunctionType lowers to a `cfn_N { ret (*fn)(void* env, …); void* env; }` value struct; a named
    // function used as a value is wrapped in a thunk (ignores env); calls through a function-value are
    // indirect (fn ptr). These are pure (no threads) → Compile == Interpret on both platforms.

    [Fact]
    public void Closure_FunctionValuedVariable_IndirectCall()
    {
        const string src = """
            Bind number to grade, given (the number x):
                Return x + 1.
            Done.
            Define op as grade.
            State cast op on (5).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
