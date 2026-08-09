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
public class PipelineMemoryTests : PipelineTestBase
{

    [Fact]
    public void Escape_WrapperLaunderedTypes_StoredOutward()
    {
        // ★ THE LAUNDERING PROOF. `record containing series` is the case a list-based fix (just
        // "add text to IsReferenceType") would miss: series IS covered at top level, but wrapping
        // it in a record hides it from a shallow test. Only a structural test catches this.
        const string recordOfText = """
            Define keeper as a record with (the label "outer").
            Pull a rabbit.
                keeper becomes a record with (the label ("in" joined to "ner")).
            Done.
            State the label of keeper.
            """;
        Assert.Equal(InterpretRaw(recordOfText), CompileRaw(recordOfText));
        const string recordOfSeries = """
            Define keeper as a record with (the items (a series of number with (0))).
            Pull a rabbit.
                keeper becomes a record with (the items (a series of number with (1, 2))).
            Done.
            State the items of keeper.
            """;
        Assert.Equal(InterpretRaw(recordOfSeries), CompileRaw(recordOfSeries));
        const string voidableText = """
            Bind voidable text to make-it, given (the text s):
                Return s.
            Done.
            Define keeper as cast make-it on ("outer").
            Pull a rabbit.
                keeper becomes cast make-it on ("in" joined to "ner").
            Done.
            State keeper.
            """;
        Assert.Equal(InterpretRaw(voidableText), CompileRaw(voidableText));
    }

    [Fact]
    public void Escape_AllStoreRoutes_Fixed()
    {
        // Each route was independently an ASan-verified UAF.
        const string intoSeries = """
            Define bag as a series of text with ().
            Pull a rabbit.
                Add ("a" joined to "b") to bag.
            Done.
            State bag.
            """;                       // also covers the CONTAINER's own growth realloc
        Assert.Equal(InterpretRaw(intoSeries), CompileRaw(intoSeries));
        const string intoObjectField = """
            Define object box with (the text tag).
            Define keeper as a new box { the tag "outer" }.
            Pull a rabbit.
                keeper's tag becomes "in" joined to "ner".
            Done.
            State keeper's tag.
            """;
        Assert.Equal(InterpretRaw(intoObjectField), CompileRaw(intoObjectField));
        const string intoCatalogue = """
            Define keeper as a catalogue of (number or text) with (0).
            Pull a rabbit.
                Add ("a" joined to "b") to keeper.
            Done.
            For each k in keeper, repeat:
                State k.
            Done.
            """;
        Assert.Equal(InterpretRaw(intoCatalogue), CompileRaw(intoCatalogue));
    }

    [Fact]
    public void Escape_NestedValue_CopiesRecursively()
    {
        // A record whose fields are BOTH a series of text and a text, every part built in the
        // rabbit — the copy must recurse through the whole shape.
        const string src = """
            Define keeper as a record with (the lines (a series of text with ("z")), the tag "outer").
            Pull a rabbit.
                Define built as a series of text with ().
                Add ("x" joined to "1") to built.
                Add ("y" joined to "2") to built.
                keeper becomes a record with (the lines built, the tag ("t" joined to "ag")).
            Done.
            State the tag of keeper.
            State the lines of keeper.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Escape_SafeRoutes_Unchanged()
    {
        // The two routes that were ALREADY safe must stay untouched: `return` is safe by
        // don't-pop (T1's load-bearing leak — ESC.3's business, not ESC.2's), and a channel send
        // is safe by heap-bridged deep copy.
        const string returnOut = """
            Bind text to build-it:
                Pull a rabbit.
                    Return "a" joined to "b".
                Done.
            Done.
            State cast build-it.
            """;
        Assert.Equal(InterpretRaw(returnOut), CompileRaw(returnOut));
    }

    [Fact]
    public void Escape_NonEscapingValues_AreNotCopied()
    {
        // Tightness: a value stored at its OWN depth, and non-region-bearing scalars, must emit NO
        // copy at all. Asserted on the generated C, since output alone can't distinguish.
        const string sameDepth = """
            Pull a rabbit.
                Define inner-keeper as "".
                inner-keeper becomes "a" joined to "b".
                State inner-keeper.
            Done.
            """;
        Assert.DoesNotContain("escapecopy(", GenerateC(sameDepth));
        Assert.Equal(InterpretRaw(sameDepth), CompileRaw(sameDepth));
        const string scalars = """
            Define n as 0.
            Pull a rabbit.
                n becomes 41 + 1.
            Done.
            State n.
            """;
        Assert.DoesNotContain("escapecopy(", GenerateC(scalars));
        Assert.Equal(InterpretRaw(scalars), CompileRaw(scalars));
    }

    [Fact]
    public void Escape_RegionBearingTest_MatchesDeepCopyTraversal()
    {
        // ★ The two traversals must not drift: `IsRegionBearing` (checker-side, drives the escape
        // annotation) is the complement of the compiler's "transitively arena-pointer-free" notion
        // that the deep-copy families walk. Locked over a corpus spanning every shape.
        var text   = CufetType.Text;
        var number = CufetType.Number;
        var seriesOfText = new SeriesType(text);
        (CufetType Type, bool Expected)[] corpus =
        {
            (number, false), (CufetType.Fact, false), (text, true),
            (seriesOfText, true), (new SeriesType(number), true),
            (new MapType(text, number), true),
            (new VoidableType(number), false), (new VoidableType(text), true),
            (new FailureType(number), false), (new FailureType(seriesOfText), true),
            (new RecordType([], [("n", number)]), false),          // no region anywhere
            (new RecordType([], [("t", text)]), true),             // text laundered by a record
            (new RecordType([], [("s", seriesOfText)]), true),     // ★ series laundered by a record
            (new VoidableType(new RecordType([], [("s", seriesOfText)])), true),   // nested wrappers
            (new UnionType([number, CufetType.Fact]), false),      // all-scalar union
            (new UnionType([number, text]), true),                 // union with a region case
        };
        foreach (var (t, expected) in corpus)
            Assert.Equal(expected, TypeChecker.IsRegionBearing(t));
    }

    // ── ESC.4 — closure capture escape (the last open UAF route) ─────────────
    // A capture built inside a rabbit and stored into a longer-lived closure used to be: refused for
    // series/map/matrix (CL.3's ad-hoc check), and a SILENT UAF for text (which laundered past it).
    // Now every region-bearing capture that ESCAPES to a shallower depth is deep-copied into the
    // destination's arena at capture time. Per-capture: `declaringDepth > destinationDepth → copy`,
    // else share — so value snapshots and live-region sharing are both preserved.

    [Fact]
    public void Escape_ClosureCaptures_AllTypesFixed()
    {
        // The five leaking capture types, each was an ASan-verified UAF (text) or a clean-throw
        // (series). All now compile + oracle-match. Function-wrapped (the reported reproducer shape).
        const string text = """
            Bind text to outer-fn:
                Define f as a function: Return "start". Done.
                Pull a rabbit.
                    Define built as "a" joined to "b".
                    f becomes a function: Return built. Done.
                Done.
                Return cast f on ().
            Done.
            State cast outer-fn.
            """;
        Assert.Equal(InterpretRaw(text), CompileRaw(text));
        const string recordOfText = """
            Bind text to outer-fn:
                Define f as a function: Return "start". Done.
                Pull a rabbit.
                    Define built as a record with (the label ("a" joined to "b")).
                    f becomes a function: Return the label of built. Done.
                Done.
                Return cast f on ().
            Done.
            State cast outer-fn.
            """;
        Assert.Equal(InterpretRaw(recordOfText), CompileRaw(recordOfText));
        const string series = """
            Bind number to outer-fn:
                Define f as a function: Return 0. Done.
                Pull a rabbit.
                    Define nums as a series of number with (1, 2, 3).
                    f becomes a function: Return the number of nums. Done.
                Done.
                Return cast f on ().
            Done.
            State cast outer-fn.
            """;
        Assert.Equal(InterpretRaw(series), CompileRaw(series));
    }

    [Fact]
    public void Escape_ClosureCapture_SemanticsPreserved()
    {
        // Snapshot (value capture, mutate enclosing after → old value seen: CL.2's 15-not-105) and
        // share (region capture, mutate instance after → new state seen: CL.2's 14-family) must both
        // survive — copy happens only ON ESCAPE, and only for deeper-than-destination captures.
        const string snapshot = """
            Bind number to make:
                Define factor as 5.
                Define f as a function: Return factor * 3. Done.
                factor becomes 35.
                Return cast f on ().
            Done.
            State cast make.
            """;
        Assert.Equal(InterpretRaw(snapshot), CompileRaw(snapshot));   // 15, not 105
        const string share = """
            Bind number to make:
                Define nums as a series of number with (1, 2, 3).
                Define f as a function: Return the number of nums. Done.
                Add 99 to nums.
                Return cast f on ().
            Done.
            State cast make.
            """;
        Assert.Equal(InterpretRaw(share), CompileRaw(share));         // 4 — sees the post-capture Add
    }

    [Fact]
    public void Escape_NonEscapingClosure_NotRefused_NoCopy()
    {
        // ★ CL.3 OVER-REFUSED this — a region-capturing closure used entirely WITHIN its rabbit is
        // safe, but the old `_rabbitDepth > 0` guard rejected it. Now it compiles, and emits no copy.
        const string src = """
            Pull a rabbit.
                Define nums as a series of number with (1, 2, 3).
                Define f as a function: Return the number of nums. Done.
                State cast f on ().
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.DoesNotContain("escapecopy(", GenerateC(src));
        // make-adder: a value capture returned out of a function (safe by don't-pop) — no copy.
        const string adder = """
            Bind number function to make-adder, given (the number n):
                Return a function: Return n + 10. Done.
            Done.
            Define add10 as cast make-adder on (5).
            State cast add10 on ().
            """;
        Assert.Equal(InterpretRaw(adder), CompileRaw(adder));
        Assert.DoesNotContain("escapecopy(", GenerateC(adder));
    }

    [Fact]
    public void Escape_IndirectClosureStore_RefusedNotDangled()
    {
        // A closure stored via an INTERMEDIATE variable then escaping can't be copied (its env is
        // opaque once built), so it is refused loudly rather than left to dangle. (ESC.4's boundary:
        // the escaping closure must be created directly in the store.)
        const string src = """
            Bind number to outer-fn:
                Define f as a function: Return 0. Done.
                Pull a rabbit.
                    Define nums as a series of number with (1, 2, 3).
                    Define g as a function: Return the number of nums. Done.
                    f becomes g.
                Done.
                Return cast f on ().
            Done.
            State cast outer-fn.
            """;
        var tokens  = new CufetLexer(src).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        Assert.Throws<CompilerException>(() => new CodeGenerator().Generate(program));
    }
    //   (b) a nested binding form's params/iterator were added to the SHARED defs set, masking a
    //       same-named OUTER variable across the whole enclosing body.
    // Both were reachable and both produced gcc errors (loud, never silent).

    [Fact]
    public void Capture_WriteOnlyAssignment_IsCaptured()
    {
        // (a) The failing shape is a write with NO read: `x becomes 5`. A body that also reads the
        // variable was rescued by the read — which is why `x becomes x + 1` always worked and this
        // survived undetected. Captures are BY VALUE in both backends, so the enclosing binding is
        // unchanged; the point is that it compiles and agrees with the oracle.
        const string valueType = """
            Bind number to outer-fn:
                Define tally as 7.
                Define f as a function: tally becomes 99. Done.
                Cast f on ().
                Return tally.
            Done.
            State cast outer-fn.
            """;
        Assert.Equal(InterpretRaw(valueType), CompileRaw(valueType));   // 7 — the closure wrote its own copy
        const string regionType = """
            Bind number to outer-fn:
                Define store as a series of number with ().
                Define fresh as a series of number with (7, 8, 9).
                Define f as a function: store becomes fresh. Done.
                Cast f on ().
                Return the number of store.
            Done.
            State cast outer-fn.
            """;
        Assert.Equal(InterpretRaw(regionType), CompileRaw(regionType)); // 0 — likewise
    }

    [Fact]
    public void Capture_OuterNameShadowedByNestedParam_StillCaptured()
    {
        // (b) The outer `limit` is referenced directly by the closure AND collides with a nested
        // lambda's parameter name. The nested param must scope to its own body only — otherwise the
        // outer `limit` looks defined, is never captured, and emits an undeclared `cv_limit`.
        // Cufet's shadow check guards `Define` but not params/iterators, so this shape is legal.
        const string src = """
            Bind number to outer-fn:
                Define limit as 7.
                Define f as a function:
                    Define g as a function given (the number limit): Return limit * 2. Done.
                    Return limit + cast g on (3).
                Done.
                Return cast f on ().
            Done.
            State cast outer-fn.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));   // 13 = outer 7 + (3 * 2)
    }

    [Fact]
    public void Capture_WriteOnlyAssignment_InTaskBody()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // The same gap on the OTHER caller of CollectRefsDefs: a task's captures are the only
        // declarations in its generated thread function, and the task-side capture guard is a TYPE
        // guard that only inspects names already captured — so a missing name never reached it.
        const string src = """
            Pull a rabbit.
                Define tally as 0.
                Have rabbit start a task:
                    tally becomes 5.
                Done.
            Done.
            State "done".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── E-prime exception-message arena lifetime ─────────────────────────────
    // The message is built by cufet_msgf in the arena live at the FAULT site, but the catch pops
    // every arena deeper than the Try before running the handler — so it used to dangle, and the
    // freed block was promptly REUSED by the next arena allocation (a message read back as the very
    // string being concatenated). Fixed by copying the message into the TARGET handler's own arena
    // at raise time (cufet_exc_arena[] + cufet_arena_alloc_at), which the catch never pops; a
    // re-raise re-copies outward. Arena-managed ⇒ no malloc/free discipline, no leak.

    [Fact]
    public void Exception_Message_SurvivesArenaPop_AcrossRabbitBoundary()
    {
        // The fault happens inside a rabbit (a DEEPER arena) and is caught outside it.
        const string src = """
            Try to:
                Pull a rabbit.
                    State "before fault".
                    State 1 / 0.
                Done.
            Done.
            In case of exception (the exception):
                State "caught: " joined to the message of the exception.
                Suppress the exception.
            Done.
            State "after".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Exception_Message_SurvivesReRaise_ThroughNestedTrys()
    {
        // Inner catches and re-raises (the default — no Suppress); the outer handler must still read
        // the ORIGINAL message intact, which requires the re-raise to re-copy it outward.
        const string src = """
            Try to:
                Try to:
                    Pull a rabbit.
                        State 1 / 0.
                    Done.
                Done.
                In case of exception (the exception):
                    State "inner: " joined to the message of the exception.
                Done.
            Done.
            In case of exception (the exception):
                State "outer: " joined to the message of the exception.
                Suppress the exception.
            Done.
            State "after".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Exception_Message_NoClobber_WhenRaisedInsideAHandler()
    {
        // A SECOND exception is raised while the first message is still live and still readable.
        // A single shared message buffer would clobber here; per-Try arena ownership does not.
        const string src = """
            Try to:
                Try to:
                    Pull a rabbit.
                        State 1 / 0.
                    Done.
                Done.
                In case of exception (the exception):
                    State "inner-before: " joined to the message of the exception.
                    Pull a rabbit.
                        State 5 % 0.
                    Done.
                    State "inner-after: " joined to the message of the exception.
                Done.
            Done.
            In case of exception (the exception):
                State "outer: " joined to the message of the exception.
                Suppress the exception.
            Done.
            State "after".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Failure_ArenaTemplatedMessage_SurvivesAcrossRabbitBoundary()
    {
        // The SIBLING SWEEP: I/O failure messages are arena-templated too (cufet_arena_msg). They are
        // NOT affected — the failure path is a compile-time goto that deliberately does not pop
        // arenas (only the exception catch does). Locked so the asymmetry stays intentional.
        const string caught = """
            Try to:
                Pull a rabbit.
                    State "before read".
                    Define body-text as read all from the file "definitely-not-here-12345.txt".
                    State body-text.
                Done.
            Done.
            In case of failure:
                State "failed: " joined to the message of the failure.
            Done.
            State "after".
            """;
        Assert.Equal(InterpretRaw(caught), CompileRaw(caught));
        const string propagated = """
            Bind text or failure to grab:
                Pull a rabbit.
                    Define c as read all from the file "nope-98765.txt" or pass the failure off.
                    Return c.
                Done.
            Done.
            Try to:
                Define r as cast grab.
                State r.
            Done.
            In case of failure:
                State "propagated: " joined to the message of the failure.
            Done.
            """;
        Assert.Equal(InterpretRaw(propagated), CompileRaw(propagated));
    }

    [Fact]
    public void IsA_EmptyContainers_AnsweredByDeclaredType()
    {
        // ISA.2a — the interpreter now answers `is a` TYPE-DIRECTED (like the compiler) instead of
        // value-directed, so an EMPTY container is decided by its DECLARED element type rather than
        // vacuously matching anything. This is what closed the empty-container divergence for
        // concretely-typed operands — a runtime `List` carries no element type, but the declared
        // type always does.
        const string emptySeries = """
            Define empties as a series of text with ().
            If empties is a series of number, State "matched number". Otherwise, State "not number".
            """;
        Assert.Equal(InterpretRaw(emptySeries), CompileRaw(emptySeries));
        const string emptyMap = """
            Define em as a map from text to text with ().
            If em is a map from text to number, State "matched". Otherwise, State "not matched".
            """;
        Assert.Equal(InterpretRaw(emptyMap), CompileRaw(emptyMap));
        const string voidableEmpty = """
            Bind voidable series of text to maybe:
                Return a series of text with ().
            Done.
            Define mv as cast maybe.
            If mv is a voidable series of number, State "matched". Otherwise, State "not matched".
            """;
        Assert.Equal(InterpretRaw(voidableEmpty), CompileRaw(voidableEmpty));
    }

    [Fact]
    public void IsA_VoidableTestedType_MatchesOnBothBackends()
    {
        // ISA.2b — `is a voidable T` as the TESTED type had no arm in StaticKindMatches (it folded to
        // false) while the interpreter answered true for a VOID value. A PRE-EXISTING divergence,
        // independent of ISA.1 and of containers. A void value satisfies any `voidable T`; a present
        // value satisfies it when its inner type does.
        const string src = """
            Bind voidable number to pick, given (the fact flag):
                If flag, return 7.
                Return void.
            Done.
            Define absent as cast pick on (false).
            Define present as cast pick on (true).
            If absent is a voidable number, State "void IS voidable-number". Otherwise, State "void NOT".
            If present is a voidable number, State "present IS voidable-number". Otherwise, State "present NOT".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void IsA_StaysDynamic_ForVoidableAndInterfaceOperands()
    {
        // The type-directed rewrite must NOT over-fold: these operands have ONE static type but
        // genuinely different runtime answers, so the runtime predicate has to survive.
        const string voidable = """
            Bind voidable number to pick, given (the fact flag):
                If flag, return 7.
                Return void.
            Done.
            If (cast pick on (true)) is a number, State "present is number". Otherwise, State "present not".
            If (cast pick on (false)) is a number, State "absent is number". Otherwise, State "absent not".
            """;
        Assert.Equal(InterpretRaw(voidable), CompileRaw(voidable));
        const string iface = """
            Define speaker as an interface for the text function speak.
            Define object dog with (the text name) and speaker:
                Bind text to speak:
                    Return "Woof".
                Done.
            Done.
            Define object cat with (the text name) and speaker:
                Bind text to speak:
                    Return "Meow".
                Done.
            Done.
            Bind text to describe, given (the speaker s):
                If s is a dog, Return "is dog".
                Return "not dog".
            Done.
            State cast describe on (a new dog { the name "R" }).
            State cast describe on (a new cat { the name "T" }).
            """;
        Assert.Equal(InterpretRaw(iface), CompileRaw(iface));
    }

    [Fact]
    public void IsA_PreciseKinds_Unchanged()
    {
        // Objects (nominal), scalars, and voidable-of-scalar were already precise — locked so the
        // element-aware recursion doesn't regress them.
        const string src = """
            Define object dog with (the text name).
            Define object cat with (the text name).
            Define d as a new dog { the name "Rex" }.
            If d is a cat, State "dog is cat". Otherwise, State "dog not cat".
            Define n as 5.
            If n is a text, State "num is text". Otherwise, State "num not text".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── CAT.2: open unions (bounded — the whole-program discovery pass) ──
    // ALL open unions are ONE front-end type (UnionType.Open), so there is ONE global `cun_open` over
    // the bounded set of concrete types ever widened into an open union anywhere in the program. The
    // set is filled by a discovery PRE-PASS (a fixed point), then CAT.1's machinery does the rest.

    [Fact]
    public void OpenCatalogue_DiscoversCaseSet_AndNarrows()
    {
        const string src = """
            Define mixed as a catalogue with (1, "two", true).
            State the number of mixed.
            For each m in mixed, repeat:
                If m is a number, State "n".
                Otherwise, If m is a text, State "t".
                Otherwise, State "f".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Capture completeness: arm-bearing nodes ──────────────────────────
    //
    // ★ These assert on the EMITTED C rather than on a running binary, deliberately. The tests
    // that caught this bug are Linux-only — tasks need pthreads — so on the development machine
    // nothing would notice a relapse until CI ran. Codegen itself is platform-independent, so
    // checking the generated capture struct holds everywhere.
    //
    // The bug: CollectRefsDefs walked children by matching IExpression/IStatement, and
    // `ConditionArm`/`JudgeArm` implement neither. Everything inside an `If` arm — condition AND
    // body — was therefore invisible, so a task referencing an enclosing variable ONLY there never
    // captured it and emitted `cv_<name> undeclared`. Found by the first run of the Linux CI job.

    [Fact]
    public void TaskCapture_UsedOnlyInAnIfCondition_IsStillCaptured()
    {
        // `limit` appears nowhere else in the task. This is the work-queue collector's shape,
        // reduced: `If count is n, Stop.` was its only mention of `n`.
        const string src = """
            Pull a rabbit.
                Define ch as a channel of number.
                Define limit as 3.
                Have rabbit start a task as worker:
                    Define i as 0.
                    While i is 100 or less, repeat:
                        i becomes i + 1.
                        If i is limit, Stop.
                    Done.
                    Send i through ch.
                Done.
                Define d as the delivery from ch.
                State d but void is -1.
            Done.
            """;
        Assert.Matches(@"struct cufet_targ\d+ \{[^}]*cv_limit", GenerateC(src));
    }

    [Fact]
    public void TaskCapture_UsedOnlyInAnIfBody_IsStillCaptured()
    {
        const string src = """
            Pull a rabbit.
                Define ch as a channel of number.
                Define bonus as 41.
                Have rabbit start a task as worker:
                    Define total as 1.
                    If total is 1:
                        total becomes total + bonus.
                    Done.
                    Send total through ch.
                Done.
                Define d as the delivery from ch.
                State d but void is -1.
            Done.
            """;
        Assert.Matches(@"struct cufet_targ\d+ \{[^}]*cv_bonus", GenerateC(src));
    }

    [Fact]
    public void TaskCapture_UsedOnlyInAJudgeArmBody_IsStillCaptured()
    {
        // `JudgeArm` implements neither interface either, so it had the identical hole — but the
        // variable has to be used inside an ARM BODY to prove it. A first version of this test
        // captured the Judge's SUBJECT and passed with the fix reverted, because `Subject` is an
        // ordinary IExpression property of JudgeStatement and the old walk reached it fine. It
        // named a path it never took. `bonus` below appears nowhere but inside an arm.
        const string src = """
            Pull a rabbit.
                Define ch as a channel of number.
                Define bonus as 41.
                Define the (number or text) subject as 7.
                Have rabbit start a task as worker:
                    Define out as 0.
                    Judge subject, where it is:
                        A number, out becomes bonus.
                        A text, out becomes 2.
                    Done.
                    Send out through ch.
                Done.
                Define d as the delivery from ch.
                State d but void is -1.
            Done.
            """;
        Assert.Matches(@"struct cufet_targ\d+ \{[^}]*cv_bonus", GenerateC(src));
    }
}
