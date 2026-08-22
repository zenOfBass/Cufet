using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>
/// Foreign interoperability — an axiom, its boundary, and the two backends agreeing on it.
/// </summary>
/// <remarks>
/// ★ These live HERE and not in the interpreter suite, and that is not a filing decision. Running
/// an axiom interpreted needs a C toolchain, so the runner is injected from Cufet.Compiler — an
/// interpreter-only test could not reach it, and an oracle test needs both backends anyway.
///
/// ⚠ Every axiom in these tests is DETERMINISTIC on purpose. `getpid()` proves a real syscall is
/// reached but its value differs per process, so it cannot be compared across two backends that
/// run as two processes; anything asserting a value uses arithmetic or libc on a fixed input.
/// </remarks>
public class PipelineForeignTests : PipelineTestBase
{
    [Fact]
    public void Axiom_ReturnedAsNumber_AgreesAcrossBackends()
    {
        const string src = """
            Pull a book on the c-language.
                Define c-language answer as [6 * 7].
                Bind number to the-answer, answer.
                State cast the-answer.
            Done.
            """;
        Assert.Equal("42", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_ReachesLibc()
    {
        // The point is that this is a real call into C, not arithmetic the wrapper could have done.
        // Cast to int deliberately: strlen gives size_t, which the boundary refuses (see below).
        const string src = """
            Pull a book on the c-language.
                Define c-language greeting-length as [(int)strlen("hello, world")].
                Bind number to how-long, greeting-length.
                State cast how-long.
            Done.
            """;
        Assert.Equal("12", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_CarriesAValueWiderThanThirtyTwoBits()
    {
        // ★ A `number` holds every 64-bit integer exactly, which is why this direction of the
        // boundary is the one the first slice carries — nothing here can round.
        const string src = """
            Pull a book on the c-language.
                Define c-language wide as [(long long)1 << 40].
                Bind number to wide-value, wide.
                State cast wide-value.
            Done.
            """;
        Assert.Equal("1099511627776", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_MakesARealSyscall()
    {
        // Not compared across backends — two processes have two pids, which is the point of the
        // call. What is asserted is that a syscall was reached and came back sane.
        const string src = """
            Pull a book on the c-language.
                Define c-language get-pid as [getpid()].
                Bind number to process-id, get-pid.
                State cast process-id > 0.
            Done.
            """;
        Assert.Equal("true", Interpret(src));
        Assert.Equal("true", Compile(src));
    }

    [Fact]
    public void Axiom_KeepsBracketsThatAreItsOwnLanguages()
    {
        // ⚠ The delimiter would be unusable for C if it stopped at the first ']' — a subscript is
        // C's commonest bracket. Pairs nest and survive, the same rule `<<...>>` follows.
        const string src = """
            Pull a book on the c-language.
                Define c-language third-letter as ["abcdef"[2]].
                Bind number to third, third-letter.
                State cast third.
            Done.
            """;
        Assert.Equal("99", Interpret(src));   // 'c'
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_WrittenTwice_IsWrappedOnce()
    {
        // Two returns of the same axiom paste the foreign text once. Not an optimisation — it is
        // what makes the wrapper's name a function of the SOURCE, which is also how the
        // interpreter's shim cache is keyed.
        string c = GenerateC("""
            Pull a book on the c-language.
                Define c-language answer as [6 * 7].
                Bind number to first, answer.
                Bind number to second, answer.
                State cast first + cast second.
            Done.
            """);
        int wrappers = c.Split("static CufetDec " + ForeignC.FunctionPrefix).Length - 1;
        Assert.Equal(1, wrappers);
    }

    [Fact]
    public void Axiom_SurvivesTheGenericInstantiationRebuild()
    {
        // ⚠ Which axiom a return runs is a SIDE CHANNEL on the statement — a property the
        // constructor does not set — and filling a template rebuilds the whole tree reflectively.
        // A rebuild that dropped it would leave the compiler emitting a read of a name that has no
        // value, and only a program with both a template and an axiom would ever show it.
        const string src = """
            Define object box of element with (the element held):
                Bind element to peek, one's held.
            Done.

            Pull a book on the c-language.
                Define c-language answer as [6 * 7].
                Bind number to the-answer, answer.
                Define b as a new box of number { the held cast the-answer }.
                State cast b's peek.
            Done.
            """;
        Assert.Equal("42", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_WorksInsideABuryingBody()
    {
        // A burying function is rewritten into a step-dispatch machine before either backend sees
        // it, so this is the other rewrite an axiom has to survive — and the one that flattens
        // every scope in the body into one.
        const string src = """
            Pull a book on the c-language.
                Define c-language answer as [6 * 7].
                Bind number to the-answer, answer.

                Bind number to ticking, given (the rabbit helper):
                    Define n as cast the-answer.
                    Repeat:
                        Have helper bury n.
                        The n becomes n + 1.
                    Until n > cast the-answer + 3.
                Done.

                Pull a rabbit as hopper.
                    Define beats as cast ticking on (hopper).
                    For each v in beats, repeat:
                        State v.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal("42\n43\n44\n45", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── The boundary refuses rather than truncates ───────────────────────────

    [Fact]
    public void Axiom_ProducingSomethingThatIsNotAWholeNumber_IsRefusedByBothBackends()
    {
        // ★ A `double` is base-2 and a `number` is base-10, so the conversion has to be written
        // once in C and shared. Until it is, truncating would be the only other answer — and a
        // silent wrong answer is the failure mode this project refuses.
        const string src = """
            Pull a book on the c-language.
                Define c-language half as [3.5].
                Bind number to bad, half.
                State cast bad.
            Done.
            """;
        var compiled = Assert.ThrowsAny<Exception>(() => CompileRaw(src));
        Assert.Contains("C whole number", compiled.Message);

        var interpreted = Assert.ThrowsAny<Exception>(() => InterpretRaw(src));
        Assert.Contains("C whole number", interpreted.Message);
    }

    [Fact]
    public void Axiom_ProducingAnUnsignedSixtyFourBitValue_IsRefused()
    {
        // ⚠ size_t is the realistic way to meet this, and it is refused rather than cast: a large
        // value would come back NEGATIVE through `long long`, silently.
        const string src = """
            Pull a book on the c-language.
                Define c-language greeting-length as [strlen("hello")].
                Bind number to how-long, greeting-length.
                State cast how-long.
            Done.
            """;
        var compiled = Assert.ThrowsAny<Exception>(() => CompileRaw(src));
        Assert.Contains("C whole number", compiled.Message);
    }

    [Fact]
    public void Axiom_BlamesTheAuthorsCRatherThanTheCompiler()
    {
        // ⚠ "Every line gcc reads was written by this compiler" stopped being true when axioms
        // arrived. A complaint inside one is the author's to fix, and telling them it is a cufet
        // bug sends them hunting something that is not there.
        const string src = """
            Pull a book on the c-language.
                Define c-language nonsense as [not_a_real_function_anywhere()].
                Bind number to broken, nonsense.
                State cast broken.
            Done.
            """;
        var e = Assert.ThrowsAny<Exception>(() => CompileRaw(src));
        Assert.Contains("foreign source", e.Message);
        Assert.DoesNotContain("bug in the Cufet compiler", e.Message);
    }

    // ── What the checker refuses, before either backend sees it ──────────────

    [Fact]
    public void Axiom_WithoutALanguage_IsRefused()
    {
        var e = Assert.Throws<TypeException>(() => GenerateC("""
            Pull a book on the c-language.
                Define get-pid as [getpid()].
            Done.
            """));
        Assert.Contains("nothing says what language it is in", e.Message);
    }

    [Fact]
    public void Axiom_WithoutItsBook_IsRefused()
    {
        var e = Assert.Throws<TypeException>(() => GenerateC(
            "Define c-language get-pid as [getpid()]."));
        Assert.Contains("c-language book is not in scope", e.Message);
    }

    [Fact]
    public void Axiom_UsedAsAValue_IsRefused()
    {
        // ⚠ The regression this pins is a DIVERGENCE, not a missing feature: `State get-pid.`
        // checked clean, printed a C# object interpreted, and emitted C that would not build.
        var e = Assert.Throws<TypeException>(() => GenerateC("""
            Pull a book on the c-language.
                Define c-language get-pid as [getpid()].
                State get-pid.
            Done.
            """));
        Assert.Contains("can only be run by returning it", e.Message);
    }

    [Theory]
    [InlineData("Bind number to run-it, given (the c-language axiom fragment), 1.")]
    [InlineData("Define object holder with (the c-language axiom fragment).")]
    [InlineData("Bind number to f, given (the series of c-language axiom parts), 1.")]
    public void Axiom_WrittenInASignature_IsRefused(string declaration)
    {
        var e = Assert.Throws<TypeException>(() => GenerateC(
            $"Pull a book on the c-language.\n    {declaration}\nDone."));
        Assert.Contains("can be declared and returned", e.Message);
    }

    [Fact]
    public void Axiom_ReturnedAsSomethingOtherThanANumber_IsRefused()
    {
        var e = Assert.Throws<TypeException>(() => GenerateC("""
            Pull a book on the c-language.
                Define c-language get-pid as [getpid()].
                Bind text to who, get-pid.
            Done.
            """));
        Assert.Contains("cannot come back as a text yet", e.Message);
    }

    [Fact]
    public void Interpreter_WithNoToolchain_SaysTheProgramCannotRunHere()
    {
        // ★ A required outcome, not an oversight. The playground runs this interpreter in wasm,
        // where no foreign call can work at all — so "this program cannot run in this environment"
        // has to be sayable, and it has to be said rather than crashed.
        const string src = """
            Pull a book on the c-language.
                Define c-language get-pid as [getpid()].
                Bind number to process-id, get-pid.
                State cast process-id.
            Done.
            """;
        var tokens = new CufetLexer(src).Tokenize();
        var program = new TypeChecker().Check(new Parser(tokens).Parse());

        var output = new StringWriter();
        var e = Assert.Throws<RuntimeException>(
            () => new CufetInterpreter(output).Execute(program));   // no ForeignRunner
        Assert.Contains("cannot run here", e.Message);
    }
}
