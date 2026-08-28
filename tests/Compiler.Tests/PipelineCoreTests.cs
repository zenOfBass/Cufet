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
public class PipelineCoreTests : PipelineTestBase
{

    // ── Acceptance bar: State 1 + 1. → binary runs → prints 2 ──────────

    [Fact]
    public void State_Addition_PrintsResult()
    {
        Assert.Equal("2", Compile("State 1 + 1."));
    }

    [Fact]
    public void State_Addition_MatchesInterpreter()
    {
        const string src = "State 1 + 1.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Oracle: compiled output == interpreter output ────────────────────

    [Fact]
    public void State_Literal_MatchesInterpreter()
    {
        const string src = "State 5.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void State_Subtraction_MatchesInterpreter()
    {
        const string src = "State 10 - 3.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void DeclaredUnion_NarrowsToNumber_MatchesInterpreter()
    {
        const string src =
            "Define the (number or text) x as 42.\n" +
            "If x is a number:\n" +
            "    State x + 1.\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State the length of x.\n" +
            "Done.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void DeclaredUnion_NarrowsToTextByElimination_MatchesInterpreter()
    {
        const string src =
            "Define the (number or text) x as 42.\n" +
            "x becomes \"hello\".\n" +
            "If x is a number:\n" +
            "    State x + 1.\n" +
            "Done.\n" +
            "Otherwise:\n" +
            "    State the length of x.\n" +
            "Done.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void DeclaredType_PlainNumber_MatchesInterpreter()
    {
        const string src =
            "Define the number n as 3.\n" +
            "Define the text who as \"Nathan\".\n" +
            "State n + 1.\n" +
            "State who.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ★ A voidable does not nest, and this is the case that proved it has to not. The annotation
    // type-checked, ran interpreted, and passed `check --native` — then gcc rejected the generated
    // C, because the map's get returns a cvd_<inner> and the binding wanted a cvd_<outer>. Check
    // passing and the build failing is the divergence class that never ships, so VoidableType now
    // collapses nesting in its constructor and `voidable voidable number` simply IS
    // `voidable number`.
    [Fact]
    public void NestedVoidableAnnotation_IsFlattened_MatchesInterpreter()
    {
        const string src =
            "Define ages as a map from text to number with (\"a\" : 1).\n" +
            "Define the voidable voidable number present as the entry for \"a\" in ages.\n" +
            "Define the voidable voidable number absent as the entry for \"b\" in ages.\n" +
            "State present.\n" +
            "State absent.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void NestedVoidableAnnotation_TakesAPlainValue_MatchesInterpreter()
    {
        // Flattened, so it widens a bare number exactly as `voidable number` does.
        const string src =
            "Define the voidable voidable number x as 5.\n" +
            "State x.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void State_Multiplication_MatchesInterpreter()
    {
        const string src = "State 3 * 4.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void State_Division_MatchesInterpreter()
    {
        const string src = "State 10 / 2.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void State_Parenthesized_MatchesInterpreter()
    {
        const string src = "State 2 * (3 + 4).";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void State_UnaryNegation_MatchesInterpreter()
    {
        const string src = "State -5.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void State_MultipleStatements_MatchesInterpreter()
    {
        const string src = "State 1 + 1. State 3 * 3.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void State_Zero_MatchesInterpreter()
    {
        const string src = "State 0.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Slice 2: variables ───────────────────────────────────────────────

    [Fact]
    public void Variable_DefineAndUse_MatchesInterpreter()
    {
        const string src = "Define x as 5. State x.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Variable_DefineAndReassign_MatchesInterpreter()
    {
        const string src = "Define x as 3. x becomes 7. State x.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Variable_ChainedDefines_MatchesInterpreter()
    {
        const string src = "Define x as 3. Define y as x + 5. State y.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Variable_SelfReferenceReassignment_MatchesInterpreter()
    {
        const string src = "Define x as 1. x becomes x + 1. State x.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Variable_HyphenatedName_MatchesInterpreter()
    {
        const string src = "Define grand-total as 100. State grand-total.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Variable_MultipleVarsInteracting_MatchesInterpreter()
    {
        const string src = "Define x as 3. Define y as 4. State x + y.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Variable_FullSpecExample_MatchesInterpreter()
    {
        // Define x as 5. Define y as x + 3. y becomes y * 2. State y. → 16
        const string src = "Define x as 5. Define y as x + 3. y becomes y * 2. State y.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Variable_Permanent_MatchesInterpreter()
    {
        const string src = "Define x as 10 permanently. State x.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Variable_VariableInArithmetic_MatchesInterpreter()
    {
        const string src = "Define width as 6. Define height as 7. State width * height.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Variable_MixedWithStateArithmetic_MatchesInterpreter()
    {
        // Slice 1 arithmetic alongside slice 2 variables
        const string src = "State 1 + 1. Define x as 10. x becomes x - 3. State x.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Slice 3: control flow ────────────────────────────────────────────

    [Fact]
    public void If_TrueBranch_MatchesInterpreter()
    {
        const string src = "Define x as 5. If x is 5, state x. Otherwise, state 0.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    /// <summary>
    /// A quotient must come back in MINIMAL form, the way .NET leaves one.
    /// </summary>
    /// <remarks>
    /// ⚠ The tell is invisible: `11 / 10` printed `1.1` on both backends, but the compiled value
    /// carried it as 1.1000…0 at scale 28, because the division reduced to scale 28 and never
    /// stripped the zeros — and printing strips trailing zeros too, so nothing showed. It only
    /// surfaced when a LATER operation on that value overflowed at one scale and not the other.
    /// Dividing the largest number by the quotient is the smallest thing that makes it visible.
    /// </remarks>
    /// <summary>
    /// Every type's `is` goes through EqCall, including the struct-shaped ones.
    /// </summary>
    /// <remarks>
    /// ⚠ Both of these type-checked and interpreted fine while emitting C that gcc REFUSED —
    /// `invalid operands to binary ==`. The equality emitter sent records, objects and series to
    /// EqCall and let a catch-all handle "facts and maps", so anything else the checker allowed
    /// arrived at `==` on a C struct. Confirmed red by restoring the catch-all: both of these
    /// fail to build under it.
    /// </remarks>
    [Fact]
    public void Equality_OnAFunctionValue_CompilesAndMatchesInterpreter()
    {
        const string src = """
            Bind number to twice, given (the number n): Return n * 2. Done.
            Bind number to thrice, given (the number n): Return n * 3. Done.
            Define first-fn as twice.
            Define same-fn as twice.
            Define other-fn as thrice.
            State first-fn is same-fn.
            State first-fn is other-fn.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Division_LeavesQuotientInMinimalForm()
    {
        const string src = """
            Define ratio as 11 / 10.
            State ratio.
            State 79228162514264337593543950335 / ratio.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
    // -- A type declaration is program-scope wherever it is written ------------

    [Fact]
    public void AnObjectDefinedInsideAFunction_Works()
    {
        // !! The declaration was silently IGNORED and the use failed four lines later with
        // "'square' is not a defined object type -- define the object type first", telling the
        // writer to declare what they had just declared.
        //
        // ** The cause was a hand-written walk. Three copies of it existed -- checker, interpreter,
        // compiler -- each entering PullStatement and PullRabbitStatement and nothing else. The
        // compiler had already been converted to the reflection walk; the other two had not, so a
        // type declared in a FUNCTION body was registered by neither.
        const string src = """
            Bind number to make-and-measure, given (the number side):
                Define object square with (the number edge):
                    Bind number to area:
                        Return one's edge * one's edge.
                    Done.
                Done.

                Define the shape as a new square { the edge side }.
                Return cast area on (the shape).
            Done.

            State cast make-and-measure on (5).
            """;
        Assert.Equal("25", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void AnObjectDefinedInsideALoop_Works()
    {
        // * Not a function-shaped fix. The walk reaches every construct, so a loop body, an If arm
        // and a Try block all register a type the same way -- which is what "program-scope wherever
        // it is written" has to mean to be a rule rather than a list.
        const string src = """
            For each n in the range 1 to 1, repeat:
                Define object tally with (the number total):
                    Bind number to doubled:
                        Return one's total * 2.
                    Done.
                Done.
            Done.

            Define the count as a new tally { the total 21 }.
            State cast doubled on (the count).
            """;
        Assert.Equal("42", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void ABindInsideAFunction_IsStillAClosure()
    {
        // !! The line that must NOT move. A VALUE binding is not program-scope: hoisting a nested
        // Bind would turn a closure into a free function, so those sites keep the narrow walk. This
        // is the counter-test for the change above -- widening everything would have passed the two
        // tests before it and broken this.
        const string src = """
            Bind number to outer, given (the number factor):
                Bind number to inner, given (the number x):
                    Return x * factor.
                Done.
                Return cast inner on (10).
            Done.

            State cast outer on (4).
            """;
        Assert.Equal("40", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
    // -- Redefining a type: the last one wins ---------------------------------

    [Fact]
    public void ARedefinedType_TakesTheLastDefinition()
    {
        // * Allowed, and the last one wins -- the same rule shadowing follows everywhere else here.
        // The linter reports it; the language does not refuse it.
        const string src = """
            Define object point with (the number x):
                Bind number to shown: Return 1. Done.
            Done.

            Define object point with (the number x):
                Bind number to shown: Return 2. Done.
            Done.

            Define the here as a new point { the x 9 }.
            State cast shown on (the here).
            """;
        Assert.Equal("2", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void ARedefinedType_WithADifferentShape_ChecksAgainstTheWinner()
    {
        // !! The bug. The SUPERSEDED definition's body used to be checked against the WINNER's
        // fields, so this reported "'point' has no field named 'x'" on line 2 -- inside the first
        // definition, on the line that declares `x`. Correct code, blamed for a definition further
        // down the file, and dead code at that: nothing dispatches to it and its methods are never
        // emitted. A superseded definition is not checked at all now.
        const string src = """
            Define object point with (the number x):
                Bind number to shown: Return one's x. Done.
            Done.

            Define object point with (the number y):
                Bind number to shown: Return one's y. Done.
            Done.

            Define the here as a new point { the y 5 }.
            State cast shown on (the here).
            """;
        Assert.Equal("5", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
    // -- The two backends agree under a comma locale --------------------------

    [Fact]
    public void ANumberLiteral_AgreesAcrossBackends_UnderACommaCulture()
    {
        // !! The divergence this suite structurally could not see. A number literal was parsed
        // with the ambient culture, so on a German machine `1.5` was FIFTEEN — while the compiler,
        // which emits a literal as raw decimal bits, went on printing 1.5. Same program, two
        // answers, and the wrong one arrived silently with the arithmetic already done.
        //
        // ⚠ Every machine that runs this suite is en-US, which is exactly why nothing here caught
        // it. Pinning the culture is the only way the oracle can be asked the question at all.
        //
        // ★ One culture rather than three: the compiled side does not vary (C's formatting is
        // locale-independent), so this is asserting the INTERPRETER against a fixed answer, and
        // CultureTests already walks the interpreter through the others far more cheaply.
        const string src = """
            Define the half as 1.5 + 1.5.
            State the half.
            State 1234.75.
            """;

        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(InterpretRaw(src), CompileRaw(src));
            Assert.Equal("3\n1234.75", Interpret(src));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }
}
