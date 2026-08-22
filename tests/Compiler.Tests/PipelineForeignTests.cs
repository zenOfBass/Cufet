using System.Runtime.InteropServices;
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
                Define c-language number answer as [6 * 7].
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
                Define c-language number greeting-length as [(int)strlen("hello, world")].
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
                Define c-language number wide as [(long long)1 << 40].
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
                Define c-language number get-pid as [getpid()].
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
                Define c-language number third-letter as ["abcdef"[2]].
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
                Define c-language number answer as [6 * 7].
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
                Define c-language number answer as [6 * 7].
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
                Define c-language number answer as [6 * 7].
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

    // ── Parameters, and splicing them by the article ─────────────────────────

    [Fact]
    public void Axiom_SplicesNumbersTextAndFacts()
    {
        // ★ Values cross, never text. The C side receives a marshalled `long long`, a `const char*`
        // and an `int` — the axiom is fixed at its definition and cannot be assembled from strings,
        // which is the same reason `Run "grep" with arguments (…)` has no shell injection.
        const string src = """
            Pull a book on the c-language.
                Define c-language number add, given (the number left, the number right),
                    as [the left + the right].
                Define c-language number text-length, given (the text subject), as [(int)strlen(the subject)].
                Define c-language number pick, given (the fact choose-first, the number first, the number second),
                    as [the choose-first ? the first : the second].

                Define the number sum as cast add on (20, 22).
                Define the number width as cast text-length on ("hello, world").
                Define the number picked as cast pick on (false, 10, 99).

                State sum.
                State width.
                State picked.
            Done.
            """;
        Assert.Equal("42\n12\n99", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_SplicesTheLongerNameWhenTwoOverlap()
    {
        // ⚠ `the flag` is a prefix of `the flag-mask`, and substituting the short one first would
        // leave `cufet_p0-mask` behind — valid C, wrong program. Longest name first.
        const string src = """
            Pull a book on the c-language.
                Define c-language number combine, given (the number flag, the number flag-mask),
                    as [the flag * 100 + the flag-mask].
                Define the number both as cast combine on (7, 42).
                State both.
            Done.
            """;
        Assert.Equal("742", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_SameSourceDifferentParameterTypes_AreDifferentWrappers()
    {
        // ⚠ The wrapper is named after the axiom's IDENTITY, which includes the parameter types.
        // Keying on the source alone would wrap the first one and silently call it for the second,
        // handing C a `const char*` where it declared a `long long`.
        string c = GenerateC("""
            Pull a book on the c-language.
                Define c-language number size-of-number, given (the number thing), as [(int)sizeof(the thing)].
                Define c-language number size-of-text, given (the text thing), as [(int)sizeof(the thing)].
                Define the number number-size as cast size-of-number on (1).
                Define the number text-size as cast size-of-text on ("x").
                State number-size + text-size.
            Done.
            """);
        int wrappers = c.Split("static CufetDec " + ForeignC.FunctionPrefix).Length - 1;
        Assert.Equal(2, wrappers);
    }

    [Fact]
    public void Axiom_ArgumentThatIsNotAWholeNumber_RefusesTheSameWayOnBothBackends()
    {
        // ⚠ A range check, not a conversion — truncating would hand C a different number than the
        // program said. It raises at RUN time on both backends, so the message is caught and
        // PRINTED: that puts it on stdout where the oracle compares it byte for byte, which is the
        // only way to hold the two backends to the same sentence rather than to the same failure.
        const string src = """
            Pull a book on the c-language.
                Define c-language number double-it, given (the number n), as [the n * 2].
                Try to:
                    Define the number bad as cast double-it on (3.50).
                    State bad.
                Done.
                In case of exception (the exception):
                    State the message of the exception.
                    Suppress the exception.
                Done.
            Done.
            """;
        Assert.Contains("Foreign source takes whole numbers, but got 3.5", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_ReachesRealPosixCallsWithArguments()
    {
        // ★★ The case this slice exists for: opening, querying and closing a real file through C.
        // None of it was reachable in slice 1, where an axiom could only be a constant expression.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;

        const string src = """
            Pull a book on the c-language.
                Define c-language number open-read-only, given (the text file-path),
                    as [open(the file-path, O_RDONLY)].
                Define c-language number close-it, given (the number handle), as [close(the handle)].

                Define the number fd as cast open-read-only on ("/etc/hostname").
                State fd > 0.
                Define the number closed as cast close-it on (fd).
                State closed = 0.
                Define the number missing as cast open-read-only on ("/no/such/file/anywhere").
                State missing = 0 - 1.
            Done.
            """;
        Assert.Equal("true\ntrue\ntrue", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── What the declaration and the call site refuse ────────────────────────

    [Fact]
    public void Axiom_WithAParameterItNeverUses_IsRefused()
    {
        // ⚠ Only DECLARED names are substituted, so a misspelling stays in the C verbatim and
        // surfaces as a gcc syntax error about a stray `the` — a message about the writer's typo,
        // phrased in a language they were not writing. Catching it here says it in Cufet.
        var e = Assert.Throws<TypeException>(() => GenerateC("""
            Pull a book on the c-language.
                Define c-language number add, given (the number left, the number right), as [the left + 1].
                Define the number s as cast add on (1, 2).
            Done.
            """));
        Assert.Contains("never uses 'the right'", e.Message);
    }

    [Theory]
    [InlineData("cast add on (1).", "takes 2 values, and 1 was given")]
    [InlineData("cast add on (1, \"two\").", "'right' takes a number, but a text was given")]
    public void Axiom_CalledWrongly_IsRefused(string call, string fragment)
    {
        var e = Assert.Throws<TypeException>(() => GenerateC($$"""
            Pull a book on the c-language.
                Define c-language number add, given (the number left, the number right),
                    as [the left + the right].
                Define the number s as {{call}}
            Done.
            """));
        Assert.Contains(fragment, e.Message);
    }

    [Fact]
    public void Axiom_CallComposesLikeAnyOtherExpression()
    {
        // ★★ What the result type moving to the DECLARATION bought. It used to come from the line
        // using the axiom, which meant a call had to be the entire right-hand side of a typed
        // binding — not in a condition, not in an interpolation, not inside arithmetic, not as an
        // argument. Every one of those went through a named intermediate first. All five shapes
        // below were refused before and are ordinary now.
        const string src = """
            Pull a book on the c-language.
                Define c-language number add, given (the number left, the number right),
                    as [the left + the right].

                State cast add on (1, 2).
                State (cast add on (1, 2)) * 10.
                State "sum: {cast add on (1, 2)}".
                State cast add on (cast add on (1, 1), 2).
                If cast add on (1, 2) > 0:
                    State "positive".
                Done.
            Done.
            """;
        Assert.Equal("3\n30\nsum: 3\n4\npositive", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_ThatNeverSaysWhatItGivesBack_IsRefusedWhenRun()
    {
        // ⚠ Declaring one is fine — that is what leaves room for an axiom passed around unrun.
        // RUNNING one is not: Cufet cannot read a C listing, and an `int` might be a number, a
        // fact, or a handle. Only the writer knows, so only the writer can say.
        var e = Assert.Throws<TypeException>(() => GenerateC("""
            Pull a book on the c-language.
                Define c-language mystery as [1].
                State cast mystery.
            Done.
            """));
        Assert.Contains("does not say what it gives back", e.Message);
    }

    [Fact]
    public void Axiom_TakingATypeTheBoundaryCannotCarry_IsRefused()
    {
        var e = Assert.Throws<TypeException>(() => GenerateC("""
            Pull a book on the c-language.
                Define c-language number total, given (the series of number xs), as [the xs].
            Done.
            """));
        Assert.Contains("cannot be handed to c-language source yet", e.Message);
    }

    [Fact]
    public void Given_OnSomethingThatIsNotAnAxiom_IsRefused()
    {
        // It parses and means nothing, which is worse than being refused — the writer would be
        // left thinking the value takes arguments.
        var e = Assert.Throws<TypeException>(() => GenerateC("Define x, given (the number n), as 5."));
        Assert.Contains("cannot be 'given' anything", e.Message);
    }

    // ── The header set an axiom is given ─────────────────────────────────────
    //
    // ★ Three branches, three tests, because a header list that is wrong on one platform fails at
    // the INCLUDE rather than at the axiom — every Windows build of every axiom-bearing program at
    // once. The split was measured with `gcc -fsyntax-only` on both toolchains rather than
    // remembered; these hold it there.

    [Fact]
    public void Axiom_ReachesAHeaderBeyondTheCoreSet()
    {
        // <limits.h> and <time.h> are in the set on every platform. Neither was there when the
        // first slice shipped, so this fails if the common branch is trimmed back.
        const string src = """
            Pull a book on the c-language.
                Define c-language number bits-per-byte as [CHAR_BIT].
                Define c-language number time-is-wide as [(int)(sizeof(time_t) >= 4)].
                Bind number to byte-width, bits-per-byte.
                Bind number to wide-enough, time-is-wide.
                State cast byte-width.
                State cast wide-enough.
            Done.
            """;
        Assert.Equal("8\n1", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_ReachesPosixOnlyHeaders()
    {
        // The POSIX branch: sockets (<sys/socket.h>, <netinet/in.h>), polling (<poll.h>) and raw
        // terminal mode (<termios.h>) are the three things ROADMAP item 1 names, and mingw has
        // none of their headers — which is the whole reason the set is guarded.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;

        const string src = """
            Pull a book on the c-language.
                Define c-language number internet-family as [AF_INET].
                Define c-language number readable-event as [(int)POLLIN].
                Define c-language number terminal-state-size as [(int)sizeof(struct termios)].
                Bind number to family, internet-family.
                Bind number to readable, readable-event.
                Bind number to termios-bytes, terminal-state-size.
                State cast family.
                State cast readable > 0.
                State cast termios-bytes > 0.
            Done.
            """;
        Assert.Equal("2\ntrue\ntrue", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_ReachesTheWindowsApiAndWinsock()
    {
        // The Windows branch. `htons`/`ntohs` are the load-bearing half: they live in libc on Linux
        // and in ws2_32 here, so this pins the `-lws2_32` on the link line — without it the program
        // compiles and then fails with "undefined reference to `__imp_socket`".
        //
        // ⚠ PURE winsock functions, deliberately. `socket()` was the obvious choice and is the
        // wrong one: whether it succeeds depends on whether WSAStartup has run in THIS PROCESS, and
        // the two backends are not the same process — a compiled binary starts clean, while the
        // interpreter's shim is called inside a .NET host that has already initialised winsock. The
        // backends genuinely disagreed, and neither was wrong. See the note in REFERENCE about
        // process-global C state; a test must not assert across that.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        const string src = """
            Pull a book on the c-language.
                Define c-language number process-handle as [(int)(GetCurrentProcessId() > 0)].
                Define c-language number byte-order-roundtrips as [(int)(ntohs(htons(4242)) == 4242)].
                Bind number to has-a-pid, process-handle.
                Bind number to linked, byte-order-roundtrips.
                State cast has-a-pid.
                State cast linked.
            Done.
            """;
        Assert.Equal("1\n1", Interpret(src));
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
                Define c-language number half as [3.5].
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
                Define c-language number greeting-length as [strlen("hello")].
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
                Define c-language number nonsense as [not_a_real_function_anywhere()].
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
            "Define c-language number get-pid as [getpid()]."));
        Assert.Contains("c-language book is not in scope", e.Message);
    }

    [Fact]
    public void Axiom_UsedAsAValue_IsRefused()
    {
        // ⚠ The regression this pins is a DIVERGENCE, not a missing feature: `State get-pid.`
        // checked clean, printed a C# object interpreted, and emitted C that would not build.
        var e = Assert.Throws<TypeException>(() => GenerateC("""
            Pull a book on the c-language.
                Define c-language number get-pid as [getpid()].
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
    public void Axiom_UsedWhereItsResultDoesNotFit_IsRefusedTheOrDINARYWay()
    {
        // ★ No special case any more, and that is the improvement. The axiom says it gives a
        // number; a function declared to give a text refuses it with the SAME sentence any other
        // type mismatch gets. Foreign source stopped needing its own error here.
        var e = Assert.Throws<TypeException>(() => GenerateC("""
            Pull a book on the c-language.
                Define c-language number get-pid as [getpid()].
                Bind text to who, get-pid.
            Done.
            """));
        Assert.Contains("declared to give back a text", e.Message);
        Assert.Contains("return a number value", e.Message);
    }

    [Fact]
    public void Axiom_DeclaringAResultTheBoundaryCannotCarry_IsRefused()
    {
        var e = Assert.Throws<TypeException>(() => GenerateC("""
            Pull a book on the c-language.
                Define c-language text host-name as ["localhost"].
            Done.
            """));
        Assert.Contains("cannot give back a text yet", e.Message);
    }

    [Fact]
    public void Interpreter_WithNoToolchain_SaysTheProgramCannotRunHere()
    {
        // ★ A required outcome, not an oversight. The playground runs this interpreter in wasm,
        // where no foreign call can work at all — so "this program cannot run in this environment"
        // has to be sayable, and it has to be said rather than crashed.
        const string src = """
            Pull a book on the c-language.
                Define c-language number get-pid as [getpid()].
                Bind number to process-id, get-pid.
                State "before".
                State cast process-id.
            Done.
            """;
        var output = new StringWriter();
        var e = Assert.Throws<RuntimeException>(
            () => new CufetInterpreter(output).Execute(Checked(src)));   // no ForeignRunner
        Assert.Contains("cannot run here", e.Message);

        // ⚠ And NOTHING was printed. The refusal comes before the program starts, so it matches a
        // compiled build refusing — which also produces no output.
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void Interpreter_WithNoToolchain_StillRunsAProgramThatOnlyDECLARESAnAxiom()
    {
        // ⚠ The divergence pointing the other way, and the reason the up-front pass looks at the
        // returns that RUN an axiom rather than at every axiom literal. An axiom nobody returns is
        // compiled by NEITHER backend — the compiler emits no wrapper for it — so refusing it here
        // would refuse a program that builds perfectly well.
        const string src = """
            Pull a book on the c-language.
                Define c-language number never-run as [getpid()].
                State "fine".
            Done.
            """;
        var output = new StringWriter();
        new CufetInterpreter(output).Execute(Checked(src));   // no ForeignRunner, and no complaint
        Assert.Equal("fine", Norm(output.ToString()));
    }

    [Fact]
    public void Axiom_ThatWillNotCompile_RefusesBeforeAnyOutput_OnBothBackends()
    {
        // ★★ The divergence this pass exists to close. `State "before".` runs BEFORE the bad axiom
        // is reached, so the interpreter used to print it and then fail, while the compiler refused
        // at build time and printed nothing. Two answers to one program.
        const string src = """
            Pull a book on the c-language.
                Define c-language number good as [6 * 7].
                Define c-language number bad as [3.5].
                Bind number to fine, good.
                Bind number to broken, bad.
                State "before".
                State cast fine.
                State cast broken.
            Done.
            """;

        var interpreted = Assert.ThrowsAny<Exception>(() => InterpretRaw(src));
        Assert.Contains("C whole number", interpreted.Message);

        var compiled = Assert.ThrowsAny<Exception>(() => CompileRaw(src));
        Assert.Contains("C whole number", compiled.Message);

        // The part that was wrong: the interpreter got as far as printing, and the compiler never
        // started. Both now produce nothing at all.
        var output = new StringWriter();
        Assert.ThrowsAny<Exception>(
            () => new CufetInterpreter(output) { ForeignRunner = new GccForeignRunner() }
                      .Execute(Checked(src)));
        Assert.Equal("", output.ToString());
    }

    private static Program Checked(string source) =>
        new TypeChecker().Check(new Parser(new CufetLexer(source).Tokenize()).Parse());
}
