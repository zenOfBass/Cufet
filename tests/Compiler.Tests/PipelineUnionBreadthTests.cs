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
public class PipelineUnionBreadthTests : PipelineTestBase
{

    [Fact]
    public void OpenCatalogue_BuiltViaAdds_DiscoveryCatchesAddSites()
    {
        const string src = """
            Define items as a catalogue.
            Insert 1 into items.
            Insert "two" into items.
            State the number of items.
            For each i in items, repeat:
                If i is a number, State "n".
                Otherwise, If i is a text, State "t".
                Otherwise, State "?".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── The discovery pre-pass has to look inside arms ────────────────────
    //
    // `ProgramUsesOpenUnion` decides whether whole-program open-union discovery runs at all. It is
    // the third walk to have carried the arm-record hole: `ConditionArm` and `JudgeArm` implement
    // neither IExpression nor IStatement, so a walk that descended by matching those interfaces saw
    // nothing inside an `If` arm or a judgement — and a catalogue FIRST mentioned in one was left
    // out of the discovery it exists to trigger.
    //
    // ★ The catalogue below is mentioned nowhere else. That is the whole test: a program that also
    // names one at the top level is rescued by that other mention and stays green with the bug back.

    [Fact]
    public void OpenCatalogue_FirstSeenInsideAnIfArm_IsStillDiscovered()
    {
        const string src = """
            Define flag as true.
            If flag is true:
                Define items as a catalogue with (1, "two").
                Insert true into items.
                For each i in items, repeat:
                    If i is a number, State "n".
                    Otherwise, If i is a text, State "t".
                    Otherwise, State "f".
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void OpenCatalogue_FirstSeenInsideAJudgeArm_IsStillDiscovered()
    {
        const string src = """
            Define the (number or text) subject as 7.
            Judge subject, where it is:
                A number:
                    Define items as a catalogue with (1, "two").
                    Insert true into items.
                    For each i in items, repeat:
                        If i is a number, State "n".
                        Otherwise, If i is a text, State "t".
                        Otherwise, State "f".
                    Done.
                Done.
                A text, State "text".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void OpenCatalogue_IsATypeNeverWidenedIn_IsFalse()
    {
        // Bounded tag set: a type that never flows into any open union can't be in one, so the check
        // is statically false — matching the interpreter (the `fact` arm never fires).
        const string src = """
            Define items as a catalogue with (1, "two").
            For each i in items, repeat:
                If i is a fact, State "fact!".
                Otherwise, If i is a number, State "n".
                Otherwise, State "t".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void OpenCatalogues_AreOneType_Interchangeable()
    {
        // MEASURED: all open unions are the SAME front-end type — differently-populated open
        // catalogues pass to the same parameter and assign to each other. So the case set must be
        // GLOBAL (a per-location set would give interchangeable values different representations).
        const string src = """
            Bind void to show, given (the catalogue c):
                State the number of c.
            Done.
            Define a1 as a catalogue with (1, "two").
            Define a2 as a catalogue with (true).
            Cast show on (a1).
            Cast show on (a2).
            a2 becomes a1.
            State the number of a2.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void OpenCatalogue_NeverPopulated_IsDegenerate()
    {
        // An open catalogue that never receives a value: the discovered case set is empty, so the
        // tagged struct is tag-only (nothing can ever be widened in).
        const string src = """
            Define items as a catalogue.
            State the number of items.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void OpenCatalogue_DiscoveryIsComplete_AcrossEmissionOrder()
    {
        // THE COMPLETENESS CRUX: function bodies emit BEFORE main, so this `is a fact` is emitted
        // before main's `Add true` — only a whole-program discovery PRE-PASS makes the fact tag exist
        // at that point. Without it the check would fold to false and print "other" instead of "fact".
        const string src = """
            Bind void to show, given (the catalogue c):
                For each i in c, repeat:
                    If i is a fact, State "fact".
                    Otherwise, If i is a number, State "num".
                    Otherwise, State "other".
                Done.
            Done.
            Define items as a catalogue.
            Insert 1 into items.
            Insert true into items.
            Cast show on (items).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void OpenCatalogue_ContainerCases_NarrowPrecisely()
    {
        // The OPEN-union twin, also lifted by ISA.2d: the discovered case set holds two container
        // types, which used to mean an empty instance of the non-tested one would diverge.
        const string src = """
            Define mixed as a catalogue with ((a series of number with (1,2)), (a series of text with ("a"))).
            For each m in mixed, repeat:
                If m is a series of number, State "nums " joined to (the number of m converted to text).
                Otherwise, State "other".
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Closure_RecursiveNestedBind_RecursiveReturnFirst()
    {
        // Recursion where the FIRST return encountered is the recursive call (no base case first).
        // Resolves because the nested-Bind desugar registers the DECLARED return type before the
        // body's return-type inference runs — locking that ordering against regression.
        const string src = """
            Bind number to compute:
                Bind number to countdown, given (the number k):
                    If k > 0, Return cast countdown on (k - 1).
                    Return 0.
                Done.
                Return cast countdown on (3).
            Done.
            State cast compute.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── CAT.3: union BREADTH — unions in record/object fields, unions across channels/tasks ──
    // A cun_N is a value struct like any other, so a union-typed field stores by value and copies
    // on bind; the work was (a) registering the union struct when it is reached only as a FIELD,
    // and (b) a UnionType arm on the channel-of-T deep-copy family (tag dispatch → the case's copy).

    [Fact]
    public void Union_AsObjectField_ConstructAccessNarrow()
    {
        const string src = """
            Define object slot with (the (number or text) value, the text label).
            Define cat as a catalogue of (number or text) with (5, "hi").
            Define n as item 1 of cat.
            Define t as item 2 of cat.
            Define first as a new slot { the value n, the label "five" }.
            Define second as a new slot { the value t, the label "greet" }.
            State first's value.
            State second's value.
            If first's value is a number, State "first is number".
            If second's value is a text, State "second is text".
            Define v as first's value.
            If v is a number, State v + 1.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Union_InRecordField_TopoOrderingStress()
    {
        // The declaration-order stress: a series of RECORDS whose field is a UNION whose cases
        // include a SERIES and an OBJECT — the union struct must be declared after its case types
        // and before the record that holds it.
        const string src = """
            Define object tag with (the text name).
            Define cat as a catalogue of (number or series of text or tag) with (7).
            Define words as a series of text with ("a", "b").
            Insert words into cat.
            Define mk as a new tag { the name "boom" }.
            Insert mk into cat.
            Define e1 as item 1 of cat.
            Define e2 as item 2 of cat.
            Define e3 as item 3 of cat.
            Define r1 as a record with (the payload e1, the label "one").
            Define r2 as a record with (the payload e2, the label "two").
            Define r3 as a record with (the payload e3, the label "three").
            Define lines as a series with (r1, r2, r3).
            For each ln in lines, repeat:
                State the label of ln.
                Define p as the payload of ln.
                If p is a number, State p * 2.
                Otherwise if p is a series of text, State the number of p.
                Otherwise, State p's name.
            Done.
            State r2.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Union_AsFunctionParameter_AndClosureCapture()
    {
        // Already-working positions, locked against regression: a union is an ordinary value struct,
        // so it passes by value as a parameter and is captured by value (snapshot) in a closure env.
        const string src = """
            Bind text to describe, given (the (number or text) v):
                If v is a number:
                    Return "n".
                Done.
                Otherwise:
                    Return "t".
                Done.
            Done.
            Define cat as a catalogue of (number or text) with (42, "hi").
            Define x as item 1 of cat.
            Define y as item 2 of cat.
            State cast describe on (x).
            State cast describe on (y).
            Define show as a function:
                If x is a number:
                    Return "captured number".
                Done.
                Otherwise:
                    Return "captured text".
                Done.
            Done.
            State cast show on ().
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Union_AsFunctionReturnType_RoundTripsAndNarrows()
    {
        // A union in RETURN position: `Bind (number or text) to …`. The cun_N returns by value like
        // any other value struct, and the caller can narrow the result.
        const string src = """
            Bind (number or text) to pick, given (the fact flag, the catalogue of (number or text) src):
                If flag, return item 1 of src.
                Return item 2 of src.
            Done.
            Define cat as a catalogue of (number or text) with (7, "seven").
            Define one-v as cast pick on (true, cat).
            Define two-v as cast pick on (false, cat).
            State one-v.
            State two-v.
            If one-v is a number, State one-v + 1.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Union_NestedInUnion_FlattensToOneTagSet()
    {
        // `(number or (text or fact))` parses and runs in the interpreter (IsAssignable and
        // RuntimeIsType both RECURSE through a nested case), so no value can tell it apart from
        // the flat spelling — the compiler flattens it to ONE 3-case tagged struct.
        const string src = """
            Define cat as a catalogue of (number or (text or fact)) with (1, "a", true).
            For each e in cat, repeat:
                State e.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Union_IsAAgainstAUnionType_CleanThrow()
    {
        // A runtime tag identifies ONE case, so a SET-valued test (`is a (number or text)`) has no
        // single tag. The interpreter answers it by recursion; folding it to false would silently
        // diverge, so the compiler refuses loudly instead.
        const string src = """
            Define cat as a catalogue of (number or text) with (1, "a").
            Define e as item 2 of cat.
            If e is a (number or text), State "yes".
            """;
        var tokens  = new CufetLexer(src).Tokenize();
        var program = new Parser(tokens).Parse();
        program = new TypeChecker().Check(program);
        Assert.Throws<CompilerException>(() => new CodeGenerator().Generate(program));
    }

    [Fact]
    public void Union_CrossesChannel_TagDispatchDeepCopy()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        const string src = """
            Pull a rabbit.
                Define ch as a channel of (number or text).
                Have rabbit start a task:
                    Define cat as a catalogue of (number or text) with (1, "two", 3).
                    For each e in cat, repeat:
                        Send e through ch.
                    Done.
                Done.
                For each n in the range 1 to 3, repeat:
                    Define d as the delivery from ch.
                    If d is not void:
                        If d is a number:
                            State d + 100.
                        Done.
                        Otherwise:
                            State d.
                        Done.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Union_CrossesChannel_ReferenceCaseIsDeepCopied()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // THE ISOLATION CRUX: the union's live case is an OBJECT holding a SERIES. The producer
        // mutates that series AFTER sending, so a shallow copy would report body-len 4 (or a UAF
        // once the task's arena pops). Deep copy through tag → object → series → text ⇒ 2.
        const string src = """
            Define object bag with (the text label, the series of text body).
            Pull a rabbit.
                Define ch as a channel of (number or bag).
                Have rabbit start a task:
                    Define words as a series of text with ("p", "q").
                    Define holder as a new bag { the label "first", the body words }.
                    Define box as a catalogue of (number or bag) with (1).
                    Insert holder into box.
                    Define e as item 2 of box.
                    Send e through ch.
                    Insert "r" into words.
                    Insert "s" into words.
                    Define n as item 1 of box.
                    Send n through ch.
                Done.
                For each k in the range 1 to 2, repeat:
                    Define d as the delivery from ch.
                    If d is not void:
                        Define v as d.
                        If v is a number:
                            State "N" joined to (v converted to text).
                        Done.
                        Otherwise:
                            State "body-len " joined to (the number of v's body converted to text).
                        Done.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Union_AsTaskResult_ReferenceCaseAndPodFastPath()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // Two awaited union results: one whose live case is a reference type (heap-bridged through
        // the tag dispatch), and one over scalars only — the POD fast path, where every case is
        // arena-pointer-free so the struct copy IS the deep copy (no per-case dispatch emitted).
        const string src = """
            Pull a rabbit.
                Have rabbit start a task as pick:
                    Define cat as a catalogue of (number or series of text) with (5).
                    Define words as a series of text with ("x", "y", "z").
                    Insert words into cat.
                    Return item 2 of cat.
                Done.
                Have rabbit start a task as pod:
                    Define c2 as a catalogue of (number or fact) with (7, true).
                    Return item 2 of c2.
                Done.
                Define r as the awaited result of pick.
                If r is a number:
                    State "num".
                Done.
                Otherwise:
                    State the number of r.
                    State item 1 of r.
                Done.
                Define q as the awaited result of pod.
                If q is a fact:
                    State q.
                Done.
                Otherwise:
                    State "not fact".
                Done.
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void OpenUnion_AsTaskResult_CarriesTheDiscoveredCaseSet()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;
        // An OPEN union crossing the task boundary. Its TypeSig is the constant "U(*)" regardless
        // of the discovered case set, so the deep-copy registry MUST be rebuilt for the real pass —
        // otherwise a discovery iteration's smaller set would be deduped against and the later
        // cases' copy helpers would never be emitted.
        const string src = """
            Bind text to kind, given (the number n):
                If n is 1, return "one".
                Return "many".
            Done.
            Pull a rabbit.
                Have rabbit start a task as grab:
                    Define loose as a catalogue.
                    Define words as a series of text with ("m", "n", "o").
                    Insert 4 into loose.
                    Insert words into loose.
                    Insert "tail" into loose.
                    Return item 2 of loose.
                Done.
                Define r as the awaited result of grab.
                If r is a series of text:
                    State the number of r.
                Done.
                State cast kind on (1).
            Done.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Arc 3: OPERATOR OVERLOADING ──────────────────────────────────────────
    // MEASURED surface: `Bind overloading <op>, given (the <l> is a <T>, the <r> is a <T>)` —
    // free-standing, top-level, `+ - * /` ONLY, both operands the SAME object type, one overload
    // per (type, op) enforced by the type checker. So resolution is an exact nominal match with a
    // single candidate ⇒ a compile-time lookup ⇒ a DIRECT CALL. Comparisons/`is` are NOT
    // overloadable, so the built-in _eq machinery (equality, `unique`, map keys) is untouched.

    [Fact]
    public void Overload_Arithmetic_ChainsAndMayReturnAnotherType()
    {
        // Chaining (left-assoc nested overload calls), an overload returning a DIFFERENT type than
        // its operands (* is a dot product → number), that result flowing into a BUILT-IN operator,
        // and the no-overload-declared path staying exactly the built-in one (3 + 4 → 7).
        const string src = """
            Define object vec2 with (the number x, the number y).
            Bind overloading +, given (the lhs is a vec2, the rhs is a vec2):
                Return a new vec2 { the x lhs's x + rhs's x, the y lhs's y + rhs's y }.
            Done.
            Bind overloading *, given (the lhs is a vec2, the rhs is a vec2):
                Return lhs's x * rhs's x + lhs's y * rhs's y.
            Done.
            Define p as a new vec2 { the x 1, the y 2 }.
            Define q as a new vec2 { the x 10, the y 20 }.
            Define r as a new vec2 { the x 100, the y 200 }.
            State p + q.
            State p + q + r.
            State p * q.
            State (p * q) + 5.
            State 3 + 4.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
