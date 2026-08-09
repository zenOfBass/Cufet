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
public class PipelineVoidableTests : PipelineTestBase
{

    [Fact]
    public void Voidable_ButVoidIs_Narrows_MatchesInterpreter()
    {
        // `but void is` yields a definite T (narrows voidable T → T).
        const string src = """
            Bind voidable number to maybe, given (the number n):
                If n > 0, return n.
                return void.
            Done.
            Define z as cast maybe on (0) but void is 42.
            Define w as cast maybe on (7) but void is 42.
            State z.
            State w.
            State z + w.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Voidable_Series_MatchesInterpreter()
    {
        // Uniform representation for a reference-type inner (series): present → value, absent → void.
        const string src = """
            Bind the voidable series of number to maybe-series, given (the number n):
                If n > 0, return a series of number with (n, n).
                return void.
            Done.
            Pull a rabbit.
                Define s as cast maybe-series on (3).
                Define t as cast maybe-series on (0).
                State s.
                State t.
                State s but void is a series of number with (0).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Voidable_Comparisons_MatchesInterpreter()
    {
        // voidable-vs-plain-T (present && value matches) and voidable-vs-voidable equality.
        const string src = """
            Bind voidable number to maybe, given (the number n):
                If n > 0, return n.
                return void.
            Done.
            Define x as cast maybe on (5).
            Define y as cast maybe on (5).
            Define w as cast maybe on (0).
            If x is 5, state "x-is-5". Otherwise, state "x-not-5".
            If x is 6, state "x-is-6". Otherwise, state "x-not-6".
            If x is y, state "x-eq-y". Otherwise, state "x-ne-y".
            If x is w, state "x-eq-w". Otherwise, state "x-ne-w".
            If w is w, state "w-eq-w". Otherwise, state "w-ne-w".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Voidable_FlowNarrowing_MatchesInterpreter()
    {
        // Inside an `is not void` branch, the voidable variable is narrowed to plain T —
        // so arithmetic on it works, matching the interpreter's variable-level narrowing.
        const string src = """
            Bind voidable number to maybe, given (the number n):
                If n > 0, return n.
                return void.
            Done.
            Define x as cast maybe on (5).
            If x is not void, State x + 1.
            If x is not void, State x * 10.
            Define v as cast maybe on (0).
            If v is not void, State v + 100. Otherwise, State "v-absent".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Voidable object FIELDS: the widening a field slot used to be denied ─────
    //
    // A field slot is an assignment target, so it takes the language's one implicit coercion —
    // a plain T (or a bare `void`) widening into `voidable T`. The checker compared field types
    // with raw equality and the compiler emitted the raw value, so these programs were rejected
    // at check time; both sides now go through IsAssignable / EmitAsType together.

    [Fact]
    public void Object_ConstructVoidableField_PlainValue_MatchesInterpreter()
    {
        const string src = """
            Define object box with (the voidable number maybe).
            Define b as a new box { the maybe 5 }.
            State the maybe of b.
            State b.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_ConstructVoidableField_Void_MatchesInterpreter()
    {
        const string src = """
            Define object box with (the voidable number maybe).
            Define b as a new box { the maybe void }.
            State the maybe of b.
            State b.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_ConstructVoidableField_Positional_MatchesInterpreter()
    {
        // Same widening on the positional construction path (both cases in one program).
        const string src = """
            Define object slot with (voidable number, number).
            Define s as a new slot { 5, 1 }.
            Define t as a new slot { void, 2 }.
            State the first of s.
            State the first of t.
            State s.
            State t.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_SetVoidableField_PlainValue_MatchesInterpreter()
    {
        // Both write forms: possessive and `the <field> of <obj> becomes`.
        const string src = """
            Define object box with (the voidable number maybe).
            Define b as a new box { the maybe void }.
            b's maybe becomes 5.
            State the maybe of b.
            the maybe of b becomes 9.
            State the maybe of b.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_SetVoidableField_Void_MatchesInterpreter()
    {
        const string src = """
            Define object box with (the voidable number maybe).
            Define b as a new box { the maybe 5 }.
            b's maybe becomes void.
            State the maybe of b.
            If b's maybe is void, State "absent". Otherwise, State "present".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_PlainNumberField_ConstructAndSet_MatchesInterpreter()
    {
        // Regression: nothing widens here, so routing through EmitAsType must be a no-op.
        const string src = """
            Define object box with (the number plain).
            Define b as a new box { the plain 1 }.
            State the plain of b.
            b's plain becomes 2.
            State the plain of b.
            State b.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_SetterWithVoidableParam_PlainValue_MatchesInterpreter()
    {
        // A setter's parameter is an assignment target too: a plain number widens into its
        // `voidable number` parameter, and the compiler widens at the call site (EmitAsType
        // against the setter's ParamType) rather than passing a bare CufetDec.
        const string src = """
            Define object sensor with (the voidable number celsius):
                Set display given (the voidable number v):
                    one's celsius becomes v.
                Done.
            Done.
            Define s as a new sensor { the celsius void }.
            s's display becomes 5.
            State the celsius of s.
            the display of s becomes 9.
            State the celsius of s.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Object_SetterWithVoidableParam_Void_MatchesInterpreter()
    {
        const string src = """
            Define object sensor with (the voidable number celsius):
                Set display given (the voidable number v):
                    one's celsius becomes v.
                Done.
            Done.
            Define s as a new sensor { the celsius 5 }.
            State the celsius of s.
            s's display becomes void.
            State the celsius of s.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Record_SetVoidableField_MatchesInterpreter()
    {
        // Records are structural, so a voidable field only exists when the record was built
        // from a voidable value — then a plain number and a bare void both write into it.
        const string src = """
            Bind voidable number to maybe, given (the number n):
                If n > 0, return n.
                return void.
            Done.
            Define r as a record with (the score cast maybe on (1)).
            State the score of r.
            the score of r becomes 7.
            State the score of r.
            the score of r becomes void.
            State the score of r.
            State r.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Slice 5D: maps (arena association list; lookup → voidable; on 5A/5B/5C) ──

    // ── A book pull is a scope the pre-scans used to look straight past ──────
    //
    // `Pull a book on <name>.` is a compile-time scope, but its body is program text. Both
    // discovery pre-scans recursed into rabbits, loops, ifs, tries, with-blocks and binds — and
    // neither had an arm for the book pull. So concurrency or signals used INSIDE one were
    // invisible: the substrate was never emitted, the rabbit never established its context, and a
    // channel declared inside it was refused for not being in a rabbit while sitting in one.
    //
    // Codegen-only (GenerateC), not Compile: these are concurrency programs, which cannot be
    // built or run on Windows. The bug was in discovery, so reaching codegen at all is the test.

    // ── Recursive object types ───────────────────────────────────────────────

    [Fact]
    public void SelfReferentialObject_IsRefusedCleanly()
    {
        // A by-value self-reference has no finite size in C. This used to reach gcc: codegen
        // succeeded, `check --native` reported no problems, and `build` then died with
        // "unknown type name 'cd_node'" plus a cascade — the late, raw failure the whole
        // refuse-rather-than-diverge rule exists to prevent.
        const string src = """
            Define object node with (the number value, the voidable node next):
            Done.
            Bind voidable node to no-node:
                Return void.
            Done.
            Define tail as a new node { the value 3, the next Cast no-node on () }.
            State the value of tail.
            """;
        var ex = Assert.Throws<CompilerException>(() => Compile(src));
        Assert.Contains("contains itself", ex.Message);
        Assert.Contains("series of node", ex.Message);   // names the shape that does work
        Assert.DoesNotContain("slice", ex.Message);
    }

    [Fact]
    public void RecursionThroughASeries_CompilesAndMatchesInterpreter()
    {
        // The supported recursive shape, and the one the refusal points at: a series field is an
        // arena POINTER, so the struct closes. 1 + 2 + 3 = 6.
        const string src = """
            Define object node with (the number value, the series of node children):
            Done.
            Define leaf1 as a new node { the value 2, the children (a series of node) }.
            Define leaf2 as a new node { the value 3, the children (a series of node) }.
            Define root  as a new node { the value 1, the children (a series of node with (leaf1, leaf2)) }.
            Bind number to total, given (the node n):
                Define sum as the value of n.
                For each kid in the children of n, repeat:
                    sum becomes sum + Cast total on (kid).
                Done.
                Return sum.
            Done.
            State Cast total on (root).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void BookPull_ContainingConcurrency_IsDiscovered()
    {
        var c = GenerateC("""
            Pull a book on collections.
                Pull a rabbit.
                    Define ch as a channel of number.
                    Have rabbit start a task as p:
                        Send 7 through ch.
                        Close ch.
                    Done.
                    Define g as the delivery from ch.
                    State g but void is 0.
                Done.
            Done.
            """);
        // The substrate is only emitted when the pre-scan found the concurrency.
        Assert.Contains("cufet_chan", c);
    }

    [Fact]
    public void BookPull_ContainingSignals_IsDiscovered()
    {
        var c = GenerateC("""
            Pull a book on math.
                Define n as 0.
                While n is less than 2, repeat:
                    n becomes n + 1.
                    Yield.
                Done.
                State n.
            Done.
            """);
        Assert.Contains("cufet_checkpoint", c);
    }

    [Fact]
    public void NonAsciiText_SurvivesBothBackends()
    {
        // ★ Weaker than it should be, and worth saying so: this test could not have caught the bug
        // it commemorates. The CLI wrote through the console's default encoding — a legacy code
        // page on Windows — so `State "héllo 👍".` printed `h?llo ??` interpreted and correctly
        // compiled. A real divergence, invisible from here, because Interpret writes to an
        // in-memory StringWriter and Compile reads the binary with StandardOutputEncoding already
        // UTF-8. Both sides are lossless in this harness; only the console lost anything.
        //
        // What this DOES pin is that the two agree on the characters themselves — code points
        // through the lexer, the string table and the emitted C. The console encoding is asserted
        // by examples/expected/json.expected, which is written and compared as UTF-8 bytes.
        const string src = """
            State "héllo 👍".
            State the length of "héllo 👍".
            State "hello" in uppercase.
            State the position of "👍" in "héllo 👍".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Contains("👍", Compile(src));

        // Casing is deliberately NOT asserted on non-ASCII: `"héllo" in uppercase` is `HÉLLO`
        // interpreted and `HéLLO` compiled, which CONTRIBUTING lists among the narrow
        // platform-owned exceptions to the no-divergence rule. Pinning it here would either
        // enshrine the gap or fail for a reason this test is not about.
    }

    [Fact]
    public void CaseTypeWidensThroughAWrapper_OracleMatch()
    {
        // ★ A case type widening into a union nested in `or failure` / `voidable`. The checker
        // refused this outright until the wrapper arms of IsAssignable learned to recurse — and the
        // moment it stopped refusing, the COMPILER emitted the bare object into `.val` where the
        // union struct belongs, which `check --native` passed and gcc rejected with "incompatible
        // types when initializing". Both halves had to move; this is the half only a build catches.
        const string src = """
            Define object leaf with (the number amount).
            Define object branch with (the number width).

            Bind (leaf or branch) or failure to make-leaf:
                Return a new leaf { the amount 1 }.
            Done.

            Bind voidable (leaf or branch) to maybe-branch:
                Return a new branch { the width 7 }.
            Done.

            Define got as cast make-leaf but on failure (a new leaf { the amount 0 }).
            If got is a leaf, State got's amount.
            Otherwise, State "branch".

            Define other as cast maybe-branch but void is (a new leaf { the amount 0 }).
            If other is a branch, State other's width.
            Otherwise, State "leaf".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void ObjectDeclaredInsideABookPull_Compiles()
    {
        // ★ Regression. `CollectObjectDefs` was a hand-written switch over block-bearing statements
        // with no arm for PullStatement, so an object declared inside `Pull a book on ... Done.`
        // was never registered — and building one crashed the compiler with a raw
        // KeyNotFoundException out of _objectDefs rather than any Cufet-level error. `check
        // --native` passed it, because nothing in the check path looks the definition up.
        const string src = """
            Pull a book on collections.
                Define object flagset with (the text name, the bits mask).
                Define modes as a series of flagset with (
                    a new flagset { the name "read", the mask 0b100 },
                    a new flagset { the name "write", the mask 0b010 }).
                For each mode in modes, repeat:
                    State "{mode's name} = {mode's mask}".
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void BookPull_MatrixOverChannel_ReachesCodegen()
    {
        // The shape that surfaced the bug: a matrix channel necessarily sits inside a book pull,
        // because `matrix` is only in scope there.
        var c = GenerateC("""
            Pull a book on collections.
                Pull a rabbit.
                    Define ch as a channel of matrix.
                    Have rabbit start a task as p:
                        Send (a matrix with ((1, 2), (3, 4))) through ch.
                        Close ch.
                    Done.
                    Define g as the delivery from ch.
                    If g is not void: State "received". Done.
                Done.
            Done.
            """);
        Assert.Contains("CufetMatrix", c);
        Assert.Contains("cufet_chan", c);
    }

    [Fact]
    public void Map_TypedWithoutWith_MatchesInterpreter()
    {
        // The empty-map sugar has to build the same map on both backends, not just parse.
        var src = """
            Define m as a map from text to number.
            State the size of m.
            In m, the entry for "alice" becomes 30.
            State the size of m.
            State the entry for "alice" in m but void is 0.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Map_CoreOperations_MatchesInterpreter()
    {
        // Construct, print (insertion order), size, lookup (present/absent → voidable),
        // has key, update, append-on-new-key, for-each key, and reference (pointer) equality.
        const string src = """
            Define m as a map from text to number with ("apple": 3, "banana": 5, "cherry": 7).
            State m.
            State the size of m.
            State the entry for "banana" in m.
            State the entry for "kiwi" in m.
            State the entry for "kiwi" in m but void is 0.
            If m has a key for "apple", state "has-apple". Otherwise, state "no-apple".
            If m has a key for "kiwi", state "has-kiwi". Otherwise, state "no-kiwi".
            In m, the entry for "banana" becomes 50.
            State the entry for "banana" in m.
            In m, the entry for "date" becomes 9.
            State the size of m.
            State m.
            For each pair in m, repeat:
                State the key of pair.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Map_FractionalDecimalValues_MatchesInterpreter()
    {
        // The decimal-fidelity payoff: exact fractional values through a map (5.5+ enabled).
        const string src = """
            Define prices as a map from text to number with ("coffee": 3.50, "tea": 2.25, "cake": 4.75).
            State prices.
            State (the entry for "coffee" in prices but void is 0) + (the entry for "tea" in prices but void is 0).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Map_ParamAndForEachValue_MatchesInterpreter()
    {
        // Map as a function parameter; For each pair with `the value of pair`.
        const string src = """
            Bind number to total-of, given (the map from text to number m):
                Define sum as 0.
                For each pair in m, repeat:
                    sum becomes sum + the value of pair.
                Done.
                return sum.
            Done.
            Define prices as a map from text to number with ("a": 3.50, "b": 2.25, "c": 4.75).
            State cast total-of on (prices).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Map_OfObjects_MatchesInterpreter()
    {
        const string src = """
            Define object person with (the text name, the number age).
            Pull a rabbit.
                Define people as a map from text to person with ("alice": a new person { the name "Alice", the age 30 }).
                In people, the entry for "bob" becomes a new person { the name "Bob", the age 25 }.
                State people.
                State the age of (the entry for "alice" in people but void is a new person { the name "none", the age 0 }).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Map_OfSeries_MatchesInterpreter()
    {
        // Map values that are themselves a reference type (series) — all in the arena.
        const string src = """
            Pull a rabbit.
                Define groups as a map from text to series of number with ("evens": a series of number with (2, 4, 6)).
                State groups.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Record_WithMapField_MatchesInterpreter()
    {
        // A map nested inside a record value struct (map is a pointer field).
        const string src = """
            Pull a rabbit.
                Define config as a record with (the label "prod", the settings a map from text to number with ("timeout": 30)).
                State config.
                State the entry for "timeout" in the settings of config but void is 0.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Slice 6: fallibility (value-level error model; T or failure) ──

    [Fact]
    public void Fallibility_TryAndButOnFailure_MatchesInterpreter()
    {
        // Fallible fn (T or failure); Try/In case of failure catching a failure and reading
        // message + category; but on failure defaulting.
        const string src = """
            Bind number or failure to safe-div, given (the number x, the number y):
                If y is 0, return a failure "divide by zero" of category "math".
                return x / y.
            Done.
            Try to:
                Define r as cast safe-div on (10, 2).
                State r.
                Define bad as cast safe-div on (5, 0).
                State bad.
            Done.
            In case of failure:
                State the message of the failure.
                State the category of the failure.
            Done.
            State cast safe-div on (20, 4) but on failure 0.
            State cast safe-div on (20, 0) but on failure 0.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
