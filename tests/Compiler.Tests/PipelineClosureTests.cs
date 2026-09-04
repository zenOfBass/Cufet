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

    [LinuxFact]
    public void Closure_LambdaPipeStage_NoCapture()
    {
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

    [LinuxFact]
    public void Closure_LambdaPipeStage_CapturesValue()
    {
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

    [LinuxFact]
    public void Closure_LambdaTextPipeStage()
    {
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
            Bind number to counting-up, given (the rabbit helper, the number first-value):
                Define next as first-value.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Define object ticker with (the stash of number source, the text name):
                Bind void to report:
                    Define held as one's source.
                    State one's name joined to ": " joined to ((unbury held but void is 0) converted to text).
                Done.
            Done.

            Pull a rabbit as den.
                Define first-ticker as a new ticker { the source (cast counting-up on (den, 50)), the name "fifty" }.
                Cast report on first-ticker.
                Cast report on first-ticker.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── An ORDINARY function held in an object FIELD ──
    //
    // The emitter half above was the whole fix for `stash of T`; a plain `number function` field was
    // still rejected by the PARSER, so the shape the topological sort had just been taught to handle
    // could not be written except through a stash. `stash of T` normalises to a FunctionType
    // (CodeGenerator.NoStashes), so this is the same lowered thing under its own spelling — which is
    // why no emitter work was needed and why an oracle test is still the right guard.

    [Fact]
    public void AnObjectFieldHoldingAPlainFunction_Compiles()
    {
        const string src = """
            Define object box with (
                the void function log,
                the number function zero,
                the number function twice given (a number) permanently,
                the text label
            ).

            Define b as a new box {
                the log a function: State "logged". Done,
                the zero a function: Return 7. Done,
                the twice a function given (the number x): Return x * 2. Done,
                the label "box"
            }.

            Define l as the log of b.
            Cast l on ().
            Define z as the zero of b.
            State cast z on ().
            Define t as the twice of b.
            State cast t on (6).
            State the label of b.
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

    /// <summary>
    /// The `Otherwise` of a NEGATED test narrows too — reaching it means the value IS that case.
    /// </summary>
    /// <remarks>
    /// ★ The mirror image of the then-branch fix above, and it had to land in BOTH front ends at
    /// once. The checker narrowed a negated test's then-branch only, so this program was rejected
    /// before either backend saw it; the compiler's `ElseNarrow` additionally required every arm to
    /// be un-negated, so fixing the checker alone would have emitted C that read the subject at its
    /// full union type.
    ///
    /// It narrows for a LONE arm only. With several arms, reaching the else no longer implies this
    /// test was the one that failed — an earlier arm may have taken the value first.
    /// </remarks>
    [Fact]
    public void TheOtherwiseOfANegatedTypeCheck_Narrows()
    {
        const string src = """
            Bind void to show, given (the (number or text) v):
                If v is not a text:
                    State v + 1.
                Done.
                Otherwise:
                    State the length of v.
                Done.
            Done.

            Cast show on (42).
            Cast show on ("hello").
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// A bury inside a judgement arm — the binding of `it` and its narrowing both survive.
    /// </summary>
    /// <remarks>
    /// ★ An oracle test for the usual reason: this always ran interpreted, because the interpreter
    /// is dynamically typed and never needed the narrowing. What could fail is the generated C,
    /// where splitting an arm into its own block leaves `it` at the subject's declared union type.
    ///
    /// `it` is not restated as a condition — it becomes an ordinary hoisted local, so the subject
    /// is evaluated ONCE and restored from its slot on every re-entry. `it + 100` and
    /// `the length of it` then only compile if the guard handed the narrowing back.
    /// </remarks>
    [Fact]
    public void ABuryInsideAJudgementArm_CompilesWithTheNarrowingIntact()
    {
        const string src = """
            Bind number to walk, given (the rabbit helper, the series of (number or text) items):
                For each thing in items, repeat:
                    Judge thing, where it is:
                        A number, have helper bury it + 100.
                        A text, have helper bury the length of it.
                    Done.
                Done.
            Done.

            Pull a rabbit as den.
                Define things as a series of (number or text) with (1, "hello", 7, "hi").
                Define source as cast walk on (den, things).
                Repeat:
                    Define next as unbury source.
                    If next is void:
                        Stop.
                    Done.
                    State next.
                Until false.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// `x is a A or x is a B` narrows to the sub-union — in the arm, and by residue in the else.
    /// </summary>
    /// <remarks>
    /// ★ Ordinary code, no stash: the two front ends keep the answer in DIFFERENT shapes and have to
    /// agree anyway. The checker narrows to the sub-union type `(number or fact)`; the compiler keeps
    /// a SET OF INDICES into the representation union, because a sub-union's own case order need not
    /// match the subject's and substituting a narrower type would make every `.val.c&lt;k&gt;` index the
    /// wrong member.
    ///
    /// The inner judgement is the probe for the arm: it needs no `Otherwise`, which is only true if
    /// `text` was ruled out. `the length of v` is the probe for the else.
    /// </remarks>
    [Fact]
    public void ADisjunctionOfTypeTests_NarrowsToTheSubUnion()
    {
        const string src = """
            Bind void to show, given (the (number or text or fact) v):
                If v is a number or v is a fact:
                    Judge v, where it is:
                        A number, state "n".
                        A fact, state "f".
                    Done.
                Done.
                Otherwise:
                    State the length of v.
                Done.
            Done.

            Cast show on (1).
            Cast show on (true).
            Cast show on ("hello").
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// A bury inside a GROUPED judgement arm, and inside an `Otherwise` following several arms.
    /// </summary>
    /// <remarks>
    /// ★ Both were refused until a disjunction narrowed, and both lifted together — a residue is
    /// just the group of cases the arms did not take.
    ///
    /// ⚠ The bodies are shaped to REACH that narrowing, and a first version of this test did not.
    /// `have helper bury 1` in a grouped arm never mentions `it`, and a residue of one case is a
    /// plain type test rather than a disjunction — so the test passed with the feature disabled on
    /// BOTH sides. What forces it is an inner `If it is a <case>` whose `Otherwise` narrows by
    /// elimination: that only reaches one survivor if the group restricted the reachable set first,
    /// and `If it` is legal only once the survivor is known to be a fact.
    /// </remarks>
    [Fact]
    public void ABuryInsideAGroupedArmAndAResidueOtherwise_Compiles()
    {
        const string src = """
            Bind number to walk, given (the rabbit helper, the series of (number or text or fact) items):
                For each thing in items, repeat:
                    Judge thing, where it is:
                        A number or a fact:
                            If it is a number:
                                Have helper bury it + 1.
                            Done.
                            Otherwise:
                                If it:
                                    Have helper bury 999.
                                Done.
                                Otherwise:
                                    Have helper bury 0.
                                Done.
                            Done.
                        Done.
                        A text, have helper bury the length of it.
                    Done.
                Done.
            Done.

            Pull a rabbit as den.
                Define things as a series of (number or text or fact) with (7, "hello", true, false).
                Define grouped as cast walk on (den, things).
                Repeat:
                    Define next as unbury grouped.
                    If next is void:
                        Stop.
                    Done.
                    State next.
                Until false.
            Done.
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
            Bind text to texts-only, given (the rabbit helper, the series of (number or text) things):
                For each thing in things, repeat:
                    If thing is a text:
                        Have helper bury thing.
                    Done.
                Done.
            Done.

            Pull a rabbit as den.
                Define source as cast texts-only on (den, a series of (number or text) with (1, "two", 3, "four")).
                Repeat:
                    Define found as unbury source.
                    If found is void:
                        Stop.
                    Done.
                    State found.
                Until false.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void ATypeTestNarrowingToNumber_CompilesToo()
    {
        // The arm narrows to `number` and then does arithmetic on it — a different payload slot
        // from the text case, so it exercises the other half of the union.
        const string src = """
            Bind number to doubled, given (the rabbit helper, the series of (number or text) things):
                For each thing in things, repeat:
                    If thing is a number:
                        Have helper bury thing * 2.
                    Done.
                Done.
            Done.

            Pull a rabbit as den.
                Define source as cast doubled on (den, a series of (number or text) with (1, "two", 3)).
                Repeat:
                    Define value as unbury source.
                    If value is void:
                        Stop.
                    Done.
                    State value.
                Until false.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void TwoObjectsEachHoldingAStash_KeepSeparateState()
    {
        // Each field holds its own closure, so the two tickers must not share a place to stand.
        const string src = """
            Bind number to counting-up, given (the rabbit helper, the number first-value):
                Define next as first-value.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Define object ticker with (the stash of number source, the text name):
                Bind void to report:
                    Define held as one's source.
                    State one's name joined to ": " joined to ((unbury held but void is 0) converted to text).
                Done.
            Done.

            Pull a rabbit as den.
                Define low as a new ticker { the source (cast counting-up on (den, 1)), the name "low" }.
                Define high as a new ticker { the source (cast counting-up on (den, 100)), the name "high" }.
                Cast report on low.
                Cast report on high.
                Cast report on low.
                Cast report on high.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── `is not void` across a split ──

    /// <summary>
    /// A `x is not void` arm inside a burying body keeps its narrowing after the linearisation.
    /// </summary>
    /// <remarks>
    /// ⚠ REGRESSION, and a DIVERGENCE — which is why it has to live on this side. The machine
    /// builder carries an arm's condition into the arm's block as a guard so the narrowing survives
    /// the split, but it recognised only `is a &lt;type&gt;` as narrowing. With no guard the block
    /// ran with `value` back at the `voidable number` its slot holds, and the compiler refused
    /// `value is greater than 6` — "Binary operator 'Gt' on a 'voidable number'". The interpreter
    /// narrows by VALUE, so it ran the identical program and reported nothing wrong; an
    /// interpreter-side test of this cannot go red.
    ///
    /// ★ No `For each` in it. The shape is the hand-written drain, and it was already broken.
    /// </remarks>
    [Fact]
    public void ANotVoidArmInsideABuryingBody_KeepsItsNarrowing()
    {
        const string src = """
            Bind number to counting-up, given (the rabbit helper, the number first-value):
                Define next as first-value.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Bind number to evens-of, given (the rabbit helper):
                Define inner as cast counting-up on (helper, 1).
                Repeat:
                    Define value as unbury inner.
                    If value is not void:
                        If value is greater than 6:
                            Stop.
                        Done.
                        If value % 2 is 0:
                            Have helper bury value.
                        Done.
                    Done.
                    Otherwise:
                        Stop.
                    Done.
                Until false.
            Done.

            Pull a rabbit as hopper.
                Define source as cast evens-of on (hopper).
                Repeat:
                    Define found as unbury source.
                    If found is void:
                        Stop.
                    Done.
                    State found.
                Until false.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// The narrowed value survives well enough to be COMPUTED with on its way into a bury.
    /// </summary>
    /// <remarks>
    /// ⚠ The same missing guard, failing differently: `bury value + 1` did not refuse, it emitted C
    /// that gcc rejected. A refusal and a broken build are the same bug wearing two faces, so both
    /// are pinned.
    /// </remarks>
    [Fact]
    public void ANarrowedValueInsideABuryingBody_CanBeComputedWith()
    {
        const string src = """
            Bind number to counting-up, given (the rabbit helper, the number first-value):
                Define next as first-value.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Bind number to one-more-than, given (the rabbit helper):
                Define inner as cast counting-up on (helper, 1).
                Repeat:
                    Define value as unbury inner.
                    If value is not void:
                        Have helper bury value + 1.
                    Done.
                    Otherwise:
                        Stop.
                    Done.
                Until false.
            Done.

            Pull a rabbit as hopper.
                Define source as cast one-more-than on (hopper).
                State unbury source.
                State unbury source.
                State unbury source.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── `For each` over a stash ──
    //
    // The loop is rewritten to the drain in the FRONT end, so neither backend learns anything for
    // it — which is exactly why it is worth an oracle test on each shape: the rewrite is the only
    // thing that could differ, and it cannot, because there is one of it.

    [Fact]
    public void AForEachOverAStash_MatchesInterpreter()
    {
        const string src = """
            Bind text to long-words-in, given (the rabbit helper, the series of text words):
                For each word in words, repeat:
                    If the length of word is less than 4:
                        Skip.
                    Done.
                    Have helper bury word.
                Done.
            Done.

            Pull a rabbit as hopper.
                Define found as cast long-words-in on (hopper, a series with ("a", "rabbit", "in", "the", "warren")).
                For each word in found, repeat:
                    State word.
                Done.
                State unbury found.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void AForEachOverAnEndlessStash_StopsAndSkipsLikeAnyLoop()
    {
        const string src = """
            Bind number to counting-up, given (the rabbit helper, the number first-value):
                Define next as first-value.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Pull a rabbit as hopper.
                Define counter as cast counting-up on (hopper, 1).
                For each value in counter, repeat:
                    If value is greater than 9:
                        Stop.
                    Done.
                    If value % 2 is 0:
                        Skip.
                    Done.
                    State value * 10.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void AStashLoopInsideABuryingBody_Delegates()
    {
        // ⚠ Why the rewrite runs before the machine builder: this loop is split across the outer
        // function's own buries, and the machine can only step statements it already knows.
        const string src = """
            Bind number to counting-up, given (the rabbit helper, the number first-value):
                Define next as first-value.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Bind number to evens-of, given (the rabbit helper):
                Define inner as cast counting-up on (helper, 1).
                For each value in inner, repeat:
                    If value is greater than 12:
                        Stop.
                    Done.
                    If value % 2 is 0:
                        Have helper bury value.
                    Done.
                Done.
            Done.

            Pull a rabbit as hopper.
                Define evens as cast evens-of on (hopper).
                For each even-one in evens, repeat:
                    State even-one.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// A generic function whose blank sits inside a `stash of T` fills in from the argument.
    /// </summary>
    /// <remarks>
    /// ⚠ `Unify` — which matches a blank against an argument — had arms for series, voidable,
    /// failable, channel, both streams and map, but not for a stash. Its catch-all answers
    /// "matched", so `stash of thing` matched `stash of number` and bound NOTHING; the blank was
    /// then reported as one nothing passed in could fill. `series of thing` worked throughout,
    /// which is what made it look like a rule about blanks instead of a missing case.
    /// </remarks>
    [Fact]
    public void ABlankInsideAStashParameter_FillsFromTheArgument()
    {
        const string src = """
            Bind number to counting-from, given (the rabbit helper, the number first-value):
                Define next as first-value.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Bind text to letters-of, given (the rabbit helper, the series of text parts):
                For each part in parts, repeat:
                    Have helper bury part.
                Done.
            Done.

            Bind series of thing to first-two, given (the stash of thing source):
                Define taken as a series of thing.
                For each value in source, repeat:
                    Insert value into taken.
                    If the number of taken is 2:
                        Stop.
                    Done.
                Done.
                Return taken.
            Done.

            Pull a rabbit as hopper.
                State cast first-two on (cast counting-from on (hopper, 10)).
                State cast first-two on (cast letters-of on (hopper, a series with ("alpha", "beta", "gamma"))).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── A method that buries ──
    //
    // The rewrite turns one method into two, and both stay METHODS so the dispatch can still read
    // `one's <field>`. The receiver rides in the closure, which is what makes the state belong to
    // the instance rather than to the type.

    [Fact]
    public void ABuryingMethod_MatchesInterpreter()
    {
        const string src = """
            Define object ticker with (the number first-beat, the text label):
                Bind number to ticks, given (the rabbit helper):
                    Define next as one's first-beat.
                    Repeat:
                        Have helper bury next.
                        The next becomes next + 1.
                    Until false.
                Done.

                Bind text to describe:
                    Return one's label.
                Done.
            Done.

            Pull a rabbit as hopper.
                Define low  as a new ticker { the first-beat 1,   the label "low" }.
                Define high as a new ticker { the first-beat 100, the label "high" }.

                Define low-beats  as cast ticks on (low, hopper).
                Define high-beats as cast ticks on (high, hopper).
                State unbury low-beats.
                State unbury high-beats.
                State unbury low-beats.
                State unbury high-beats.

                Define counted as cast ticks on (low, hopper).
                Define taken as 0.
                For each beat in counted, repeat:
                    If taken is 3:
                        Stop.
                    Done.
                    State (cast describe on (low)) joined to ": " joined to (beat converted to text).
                    The taken becomes taken + 1.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void AnUntoBuryingMethod_MatchesInterpreter()
    {
        const string src = """
            Define object ticker with (the number first-beat):
                Bind text to describe:
                    Return "a ticker".
                Done.
            Done.

            Bind number to every-other unto ticker, given (the rabbit helper):
                Define next as one's first-beat.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 2.
                Until false.
            Done.

            Pull a rabbit as hopper.
                Define low as a new ticker { the first-beat 1 }.
                Define odds as cast every-other on (low, hopper).
                For each odd-beat in odds, repeat:
                    If odd-beat is greater than 9:
                        Stop.
                    Done.
                    State odd-beat.
                Done.
                State cast describe on (low).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void AStashLoopsBareItAndInlineForms_MatchInterpreter()
    {
        const string src = """
            Bind number to upto, given (the rabbit helper, the number limit):
                Define next as 1.
                While next is not greater than limit, repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Done.
            Done.

            Pull a rabbit as hopper.
                Define counter as cast upto on (hopper, 2).
                For each in counter, repeat:
                    State it.
                Done.

                Define other as cast upto on (hopper, 2).
                For each value in other, State value * 100.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void AStashLoopShadowingAnOuterName_MatchesInterpreter()
    {
        const string src = """
            Bind number to upto, given (the rabbit helper, the number limit):
                Define next as 1.
                While next is not greater than limit, repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Done.
            Done.

            Pull a rabbit as hopper.
                Define value as 99.
                Define counter as cast upto on (hopper, 2).
                For each value in counter, repeat:
                    State value.
                Done.
                State value.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void AStashLoopInsideAStashLoop_MatchesInterpreter()
    {
        const string src = """
            Bind number to counting-up, given (the rabbit helper, the number first-value):
                Define next as first-value.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Pull a rabbit as hopper.
                Define outer-stash as cast counting-up on (hopper, 1).
                For each left in outer-stash, repeat:
                    If left is greater than 3:
                        Stop.
                    Done.
                    Define inner-stash as cast counting-up on (hopper, 10).
                    For each right in inner-stash, repeat:
                        If right is greater than 11:
                            Stop.
                        Done.
                        State (left converted to text) joined to "-" joined to (right converted to text).
                    Done.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
