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
public class PipelineClosureTests : PipelineTestBase
{

    [Fact]
    public void Closure_PassNamedFunction_HigherOrder()
    {
        // A named function passed as an argument to a higher-order function that calls it twice.
        const string src = """
            Bind number to twice, given (the number function f given (the number), the number x):
                Return cast f on (cast f on (x)).
            Done.
            Bind number to inc, given (the number n):
                Return n + 1.
            Done.
            State cast twice on (inc, 10).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_ReturnNamedFunction_ThenCall()
    {
        // A function that returns a (named) function value; the caller stores and calls it.
        const string src = """
            Bind number function given (the number) to pick:
                Return double-it.
            Done.
            Bind number to double-it, given (the number n):
                Return n * 2.
            Done.
            Define f as cast pick.
            State cast f on (21).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_TextReturningFunctionValue()
    {
        // A function value whose return type is a reference type (text) — the {fn, env} slot is
        // signature-agnostic; the indirect call yields the same text as a direct call.
        const string src = """
            Bind text to shout, given (the text s):
                Return s joined to "!".
            Done.
            Define f as shout.
            State cast f on ("hi").
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── CL.2: closure captures — lambdas + nested Bind (the env-record IS the capture policy) ──
    // The env is a synthesized value-struct of the free vars: value captures store BY VALUE (snapshot),
    // region captures store the SHARED POINTER (share) — binding-is-binding, matching the interpreter.
    // The non-thread cases are pure → Compile == Interpret on both platforms.

    [Fact]
    public void Closure_LambdaValueCapture_IsSnapshot()
    {
        // Capture a number, mutate the enclosing var AFTER creating the lambda → the lambda sees the
        // SNAPSHOT (value stored by value in the env), not the mutation. 5 + 10 == 15 (not 105).
        const string src = """
            Bind void to test:
                Define n as 10.
                Define f as a function given (the number x): Return x + n. Done.
                n becomes 100.
                State cast f on (5).
            Done.
            Cast test.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_LambdaRegionCapture_IsShared()
    {
        // Capture a series, mutate the SERIES after creating the lambda → the lambda sees the mutation
        // (the env stores the shared pointer). 10 + 4 == 14 (not 13). The share half of binding-is-binding.
        const string src = """
            Bind void to test:
                Define xs as a series of number with (1, 2, 3).
                Define f as a function given (the number x): Return x + the number of xs. Done.
                Insert 99 into xs.
                State cast f on (10).
            Done.
            Cast test.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_NestedBind_Captures()
    {
        // A nested Bind (named local closure) captures the enclosing function's parameter.
        const string src = """
            Bind number to outer, given (the number base):
                Bind number to add-base, given (the number y):
                    Return y + base.
                Done.
                Return cast add-base on (7).
            Done.
            State cast outer on (100).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_MakeAdder_ValueCaptureEscapesFunction()
    {
        // The classic make-adder: a lambda captures a value param and is RETURNED out of its creating
        // function. A value capture is self-contained (the env owns its snapshot), so the escape is
        // safe (env lives in the enclosing arena, which a plain function frame doesn't pop).
        const string src = """
            Bind number function given (the number) to make-adder, given (the number n):
                Return a function given (the number x): Return x + n. Done.
            Done.
            Define add10 as cast make-adder on (10).
            State cast add10 on (5).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_LambdaPipeStage_NoCapture()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The unblocked capability: a lambda used as a pipe stage. A stage is a closure value; the
        // pipe runner calls fn(env). A middle lambda stage transforms the stream.
        const string src = """
            Bind void to producer:
              output 1.
              output 2.
              output 3.
            Done.
            Bind void to consumer:
              for each x from the input:
                State x.
              Done.
            Done.
            producer | (a function: for each x from the input: output x * 10. Done. Done) | consumer.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_LambdaPipeStage_CapturesValue()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A CAPTURING lambda pipe stage: the env (a value capture — immutable) crosses the thread
        // boundary, shared read-only while the creating scope blocks on the pipe join → TSan-clean.
        const string src = """
            Bind void to run-pipe, given (the number factor):
              Bind void to producer:
                output 1.
                output 2.
              Done.
              Bind void to consumer:
                for each x from the input:
                  State x.
                Done.
              Done.
              producer | (a function: for each x from the input: output x * factor. Done. Done) | consumer.
            Done.
            Cast run-pipe on (100).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── CL.3: closures breadth + escape interim (closes the arc) ──
    // Function-values as series elements, higher-order-of-higher-order (nested cfn struct ordering),
    // recursive nested Bind (by-name self-call), lambda TEXT pipe stages, and the region-capture-
    // escapes-a-rabbit interim (clean-throw). Pure cases → Compile == Interpret on both platforms.

    [Fact]
    public void Closure_SeriesOfFunctions()
    {
        // A series whose element type is a function value — the cfn_N value struct nests in the series
        // (the cfn struct is emitted before the series runtime; function eq/write added for the series).
        const string src = """
            Bind number to inc, given (the number n): Return n + 1. Done.
            Bind number to dbl, given (the number n): Return n * 2. Done.
            Define ops as a series of number function given (the number) with (inc, dbl).
            State cast (the first of ops) on (10).
            State cast (the second of ops) on (10).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_SeriesOfFunctions_Compared()
    {
        // Found by mutation testing on Linux. Closure_SeriesOfFunctions above notes that function
        // eq is "added for the series" — but it was only ever EMITTED, never called, so EqCall's
        // FunctionType arm could compare the environment pointers with != and the whole suite
        // stayed green.
        //
        // Function values are reference equality on both backends, so two series built from the
        // same two named functions are equal and a reordered one is not. Comparing the series is
        // what reaches the arm; casting an element (as above) never does.
        const string src = """
            Bind number to inc, given (the number n): Return n + 1. Done.
            Bind number to dbl, given (the number n): Return n * 2. Done.
            Define ops as a series of number function given (the number) with (inc, dbl).
            Define twin as a series of number function given (the number) with (inc, dbl).
            Define flipped as a series of number function given (the number) with (dbl, inc).
            If ops is twin, State "twin same". Otherwise, State "twin diff".
            If ops is flipped, State "flipped same". Otherwise, State "flipped diff".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_FunctionReturningFunction_AsValue()
    {
        // make-adder as a VALUE — its type is (number) -> (number -> number), a cfn whose RETURN is a
        // cfn → the nested-cfn topo ordering (inner declared before outer).
        const string src = """
            Bind number function given (the number) to make-adder, given (the number n):
                Return a function given (the number x): Return x + n. Done.
            Done.
            Define maker as make-adder.
            Define add5 as cast maker on (5).
            State cast add5 on (10).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_RecursiveNestedBind()
    {
        // A recursive nested Bind (factorial): recursion resolves BY NAME to a self-call reusing the
        // current env (matching the interpreter's in-scope-name recursion). 100 + 5! == 220.
        const string src = """
            Bind number to compute, given (the number base):
                Bind number to fact, given (the number k):
                    If k < 2, Return 1.
                    Return k * (cast fact on (k - 1)).
                Done.
                Return base + (cast fact on (5)).
            Done.
            State cast compute on (100).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_RecursiveNestedBind_WithCapture()
    {
        // Recursion + capture together: countdown recurses (self-call) AND captures `bump`; the
        // self-call reuses the current env, so the recursive call sees the same capture. 10*3 == 30.
        const string src = """
            Bind number to compute, given (the number bump):
                Bind number to countdown, given (the number k):
                    If k < 1, Return 0.
                    Return bump + (cast countdown on (k - 1)).
                Done.
                Return cast countdown on (3).
            Done.
            State cast compute on (10).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_LambdaTextPipeStage()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // A lambda TEXT pipe stage: AnalyzePipes now propagates the element type THROUGH the lambda,
        // so the named consumer after it reads text (not number) — the fix for the lambda-text UAF.
        const string src = """
            Bind void to producer:
              output "a".
              output "bb".
            Done.
            Bind void to consumer:
              for each w from the input:
                State w.
              Done.
            Done.
            producer | (a function: for each w from the input: output (w joined to "!"). Done. Done) | consumer.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_RegionCaptureEscapingRabbit_CopiedNotThrown()
    {
        // ESC.4 CONVERTED THIS FROM CL.3's clean-throw. A closure capturing a REGION value (series)
        // inside a rabbit and escaping to a shallower depth used to be refused. Now the captured
        // series is DEEP-COPIED into the destination's arena at capture time (its rabbit-local source
        // dies at Done., so copy is observationally identical to sharing) — compiles and matches.
        const string src = """
            Define f as a function given (the number x): Return x. Done.
            Pull a rabbit.
                Define xs as a series of number with (1, 2, 3).
                f becomes a function given (the number x): Return x + the number of xs. Done.
            Done.
            State cast f on (10).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Small edges: implicit-value capture + recursion ordering (closes the audit's small lines) ──
    // `one` (method receiver) and `the failure` (a handler's caught failure) are implicit bindings the
    // free-var analysis now recognizes as capturable, matching the interpreter. `the input` needs no
    // capture (it lowers to the `stdin` global). Recursion whose FIRST return is the recursive call
    // already resolves (the nested-Bind desugar registers the declared return type before inference).

    [Fact]
    public void Closure_CapturesMethodReceiver_One()
    {
        // A lambda inside a method body referencing `one` captures the receiver.
        const string src = """
            Define object box with (the number n):
                Bind number to twice-n:
                    Define f as a function given (the number x): Return x + one's n. Done.
                    Return cast f on (0).
                Done.
            Done.
            Define b as a new box { the n 21 }.
            State cast twice-n on (b).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_MethodReceiverCapture_IsSnapshot()
    {
        // Objects are value types, so capturing `one` SNAPSHOTS the receiver: mutating a field after
        // the lambda is created doesn't change what the lambda sees (7, not 99) — binding-is-binding.
        const string src = """
            Define object box with (the number n):
                Bind number to probe:
                    Define f as a function: Return one's n. Done.
                    one's n becomes 99.
                    Return cast f.
                Done.
            Done.
            Define b as a new box { the n 7 }.
            State cast probe on (b).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_CapturesTheFailure_InHandler()
    {
        // A lambda inside an `In case of failure` handler referencing `the failure` captures the
        // caught CufetFailure (a value → snapshot), so `the message of the failure` resolves inside it.
        const string src = """
            Bind number or failure to risky:
                Return a failure "boom" of category "test".
            Done.
            Bind void to handle:
                Try to:
                    Define v as cast risky.
                    State "ok".
                Done.
                In case of failure:
                    Define f as a function: Return the message of the failure. Done.
                    State cast f.
                Done.
            Done.
            Cast handle.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_ReferencesTheInput_NoCaptureNeeded()
    {
        // `the input` lowers to the `stdin` global — a lambda referencing it needs no capture at all.
        const string src = """
            Bind void to go:
                Define f as a function: Return read a line from the input. Done.
                Define line as cast f.
                State (line but void is "<eof>").
            Done.
            Cast go.
            """;
        Assert.Equal(InterpretRaw(src, "hello\n"), CompileRaw(src, "hello\n"));
    }

    // ── CAT.1: closed unions (catalogue) — the N-case generalization of voidable ──
    // `cun_N { int tag; union { c0; c1; … } val; }` per closed union; widening sets the tag at the
    // (statically typed) store site; `is a <case>` is a genuine RUNTIME tag check; narrowing exposes
    // `.val.c<k>` at the case's concrete type. Scoped to KIND-DISTINGUISHABLE cases.

    [Fact]
    public void Catalogue_ClosedUnion_ScalarsNarrowBothArms()
    {
        // Construct, iterate, narrow both arms (the else arm narrows exhaustively to the one
        // remaining case — matching the front-end's residual-union narrowing).
        const string src = """
            Define stuff as a catalogue of (number or text) with (1, "two", 3).
            For each item in stuff, repeat:
                If item is a number, State "num:" joined to (item converted to text).
                Otherwise, State "txt:" joined to item.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── A closure held in an object FIELD ──
    //
    // ★ An oracle test and NOT an interpreter one, deliberately: this shape always interpreted
    // correctly. It was the COMPILER that could not emit it — object structs were written in one
    // phase and closure structs in a later one, so an object holding a closure named `cfn_0` before
    // it existed and gcc rejected the generated C. Only `Interpret == Compile` can see that class of
    // bug; a test that ran the interpreter alone would have passed throughout.
    //
    // The dependency runs BOTH ways — a closure's parameter may be a record, a record's field may be
    // a closure — so the fix was to put closures into the one topological sort in EmitStructs rather
    // than to reorder two phases. A forward declaration cannot substitute: a by-value struct member
    // needs a complete type.

    [Fact]
    public void AnObjectFieldHoldingAStash_Compiles()
    {
        const string src = """
            Bind number to counting-up, given (the number first-value):
                Define next as first-value.
                Repeat:
                    Bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Define object ticker with (the stash of number source, the text name):
                Bind void to report:
                    Define held as one's source.
                    State one's name joined to ": " joined to ((unbury held but void is 0) converted to text).
                Done.
            Done.

            Define first-ticker as a new ticker { the source (cast counting-up on (50)), the name "fifty" }.
            Cast report on first-ticker.
            Cast report on first-ticker.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// `x is NOT a &lt;case&gt;` narrows the THEN branch, by elimination.
    /// </summary>
    /// <remarks>
    /// ★ A plain divergence, found sideways. The checker narrows this arm, so the program
    /// interprets; the compiler did not, so the arm read x at its full union type and the generated
    /// C would not build. No stash, no module, no closure — just an `If` with a negated type test.
    ///
    /// It surfaced only because the negated test was tried as a guard for resuming a stash into an
    /// `Otherwise`. Fixing it here lifted that restriction with no stash-specific code, which is the
    /// tell that the refusal had been sitting in front of somebody else's bug.
    /// </remarks>
    [Fact]
    public void NegatedTypeCheck_NarrowsTheThenBranch()
    {
        const string src = """
            Define things as a series of (number or text) with (1, "two", 3).
            For each thing in things, repeat:
                If thing is not a text:
                    State "number: " joined to (thing converted to text).
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// The narrowed value can be USED as its narrowed type, not merely printed.
    /// </summary>
    /// <remarks>
    /// ⚠ Note what is NOT tested here: the `Otherwise` of a NEGATED test. The checker narrows the
    /// then-branch of `is not a &lt;type&gt;` but not its else, so
    /// `If x is not a text: … Done. Otherwise: State "t: " joined to x. Done.` does not type-check
    /// at all — a front-end asymmetry, not a compiler one, and there is nothing for an oracle test
    /// to compare while the program is rejected before either backend sees it.
    /// </remarks>
    [Fact]
    public void ANegativelyNarrowedValue_CanBeComputedWith()
    {
        const string src = """
            Define things as a series of (number or text) with (1, "two", 3).
            Define total as 0.
            For each thing in things, repeat:
                If thing is not a text:
                    The total becomes total + thing.
                Done.
            Done.
            State total.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── A bury inside a type test ──
    //
    // ★ Oracle tests, and they have to be. This shape ALWAYS ran interpreted — the interpreter is
    // dynamically typed and never needed the narrowing. What failed was the generated C, because
    // splitting the arm into its own block left the subject at its declared union type. The fix
    // carries the arm's condition into the block and re-tests it on entry; only `Interpret ==
    // Compile` can tell whether that actually restored anything.

    [Fact]
    public void ABuryInsideATypeTest_CompilesWithTheNarrowingIntact()
    {
        const string src = """
            Bind text to texts-only, given (the series of (number or text) things):
                For each thing in things, repeat:
                    If thing is a text:
                        Bury thing.
                    Done.
                Done.
            Done.

            Define source as cast texts-only on (a series of (number or text) with (1, "two", 3, "four")).
            Repeat:
                Define found as unbury source.
                If found is void:
                    Stop.
                Done.
                State found.
            Until false.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void ATypeTestNarrowingToNumber_CompilesToo()
    {
        // The arm narrows to `number` and then does arithmetic on it — a different payload slot
        // from the text case, so it exercises the other half of the union.
        const string src = """
            Bind number to doubled, given (the series of (number or text) things):
                For each thing in things, repeat:
                    If thing is a number:
                        Bury thing * 2.
                    Done.
                Done.
            Done.

            Define source as cast doubled on (a series of (number or text) with (1, "two", 3)).
            Repeat:
                Define value as unbury source.
                If value is void:
                    Stop.
                Done.
                State value.
            Until false.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void TwoObjectsEachHoldingAStash_KeepSeparateState()
    {
        // Each field holds its own closure, so the two tickers must not share a place to stand.
        const string src = """
            Bind number to counting-up, given (the number first-value):
                Define next as first-value.
                Repeat:
                    Bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Define object ticker with (the stash of number source, the text name):
                Bind void to report:
                    Define held as one's source.
                    State one's name joined to ": " joined to ((unbury held but void is 0) converted to text).
                Done.
            Done.

            Define low as a new ticker { the source (cast counting-up on (1)), the name "low" }.
            Define high as a new ticker { the source (cast counting-up on (100)), the name "high" }.
            Cast report on low.
            Cast report on high.
            Cast report on low.
            Cast report on high.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
