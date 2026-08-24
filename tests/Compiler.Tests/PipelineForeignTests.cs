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
        // The `(int)` was once required — strlen gives size_t and the boundary refused it — and is
        // kept here deliberately, because casting on the C side must go on working now that it is
        // no longer needed. The uncast form is Axiom_ProducingAnUnsignedSixtyFourBitValue_*.
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
        Assert.Equal(1, WrapperCount(c));
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
        Assert.Equal(2, WrapperCount(c));
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

    // ── What an axiom can give back ──────────────────────────────────────────

    [Fact]
    public void Axiom_GivesBackAFact()
    {
        // ★ `fact` needs no boundary guard of its own: `(x) ? 1 : 0` is only valid C for something
        // with a truth value, so C already refuses a struct there in its own words.
        const string src = """
            Pull a book on the c-language.
                Define c-language fact same-text, given (the text left, the text right),
                    as [strcmp(the left, the right) == 0].
                State cast same-text on ("a", "a").
                State cast same-text on ("a", "b").
            Done.
            """;
        Assert.Equal("true\nfalse", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_GivesBackAVoidableText()
    {
        // ★★ Text coming OUT was the whole point of this slice — nothing could get a string from C
        // before it. The bytes are COPIED, never aliased: C's belong to C, and a static buffer the
        // next call overwrites would change under a Cufet text that pointed at it.
        const string src = """
            Pull a book on the c-language.
                Define c-language voidable text greeting as ["hello from C"].
                Define c-language voidable text echo, given (the text subject), as [the subject].
                State (cast greeting) but void is "?".
                State (cast echo on ("abc")) but void is "?".
            Done.
            """;
        Assert.Equal("hello from C\nabc", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_ContainingATopLevelComma_Compiles()
    {
        // ⚠ A real bug, found by writing one: the boundary guards are macros, and a one-parameter
        // macro splits its argument on a comma BEFORE expanding. C's comma operator put a top-level
        // comma in perfectly good foreign source and the guard failed to compile on it. The macros
        // are variadic now, which puts the argument back together.
        const string src = """
            Pull a book on the c-language.
                Define c-language number second-of-two, given (the number left, the number right),
                    as [the left, the right].
                State cast second-of-two on (1, 2).
            Done.
            """;
        Assert.Equal("2", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_TextThatIsNull_ComesBackAsVoid()
    {
        // ⚠ The reason the result must be declared `voidable text` rather than `text`. NULL is C's
        // universal "nothing to give" — `getenv` on an unset name is the everyday case — so it
        // lands in the mechanism the language already has instead of a promise C cannot keep.
        const string src = """
            Pull a book on the c-language.
                Define c-language voidable text missing
                    as [getenv("CUFET_NO_SUCH_VARIABLE_ANYWHERE")].
                State (cast missing) is void.
            Done.
            """;
        Assert.Equal("true", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_DeclaringPlainText_IsRefusedWithTheReason()
    {
        var e = Assert.Throws<TypeException>(() => GenerateC("""
            Pull a book on the c-language.
                Define c-language text greeting as ["hi"].
            Done.
            """));
        Assert.Contains("'voidable text', never a plain 'text'", e.Message);
    }

    [Fact]
    public void Axiom_GivingBackSomethingThatIsNotAString_IsRefusedByTheCCompiler()
    {
        // The other half of the text guard: a `voidable text` result whose C is not a string at
        // all. Refused where the type is actually known, with a message naming the foreign source.
        const string src = """
            Pull a book on the c-language.
                Define c-language voidable text wrong as [42].
                State (cast wrong) but void is "?".
            Done.
            """;
        var e = Assert.ThrowsAny<Exception>(() => CompileRaw(src));
        Assert.Contains("has to produce a C string", e.Message);
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

    // ── An axiom bound to a name ─────────────────────────────────────────────

    [Fact]
    public void Axiom_BoundToAnotherName_RunsFromThere()
    {
        // ★ The first brick of "code as data": an axiom is a VALUE here, not only something being
        // run. `alias` holds the axiom `answer` holds, and running either reaches the same source.
        //
        // ★ It needs no runtime representation at all, which is why this slice could land while
        // passing one through a parameter still cannot: the checker follows the chain of names back
        // to the literal, so both names compile to the same wrapper call and the binding emits
        // nothing. An axiom that has to be chosen at RUN time is the part still missing.
        const string src = """
            Pull a book on the c-language.
                Define c-language number answer as [6 * 7].
                Define alias as answer.
                State cast alias.
            Done.
            """;
        Assert.Equal("42", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_BoundThroughSeveralNames_StillReachesItsSource()
    {
        // The chain is followed, not one hop. Written because one hop was all it used to follow,
        // and a two-link chain reported the second name as undefined rather than as an axiom.
        const string src = """
            Pull a book on the c-language.
                Define c-language number answer as [6 * 7].
                Define first-alias as answer.
                Define second-alias as first-alias.
                State cast second-alias.
            Done.
            """;
        Assert.Equal("42", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_BoundToAName_EmitsNoValue()
    {
        // ⚠ The binding must emit NOTHING. Emitting it produced `CufetDec cv_alias = cv_answer;`
        // against a `cv_answer` that never existed — checked clean, would not build, which is the
        // divergence the axiom guards exist to prevent. One wrapper, no variable of either name.
        string c = GenerateC("""
            Pull a book on the c-language.
                Define c-language number answer as [6 * 7].
                Define alias as answer.
                State cast alias.
            Done.
            """);
        Assert.Equal(1, WrapperCount(c));
        Assert.DoesNotContain("cv_alias", c);
        Assert.DoesNotContain("cv_answer", c);
    }

    [Fact]
    public void Axiom_RunFromAPlaceItsSourceIsNotKnown_IsRefused()
    {
        // ⚠ The boundary of this slice, and it is a REFUSAL rather than a miscompile. Running an
        // axiom pastes its text, so an axiom chosen at run time has no text to paste — and letting
        // it through type-checked programs neither backend could build.
        const string viaParameter = """
            Pull a book on the c-language.
                Define c-language number answer as [6 * 7].
                Bind number to run-it, given (the c-language axiom which):
                    Return which.
                Done.
                State cast run-it on (answer).
            Done.
            """;
        Assert.Contains("not yet written down as a type",
                        Assert.ThrowsAny<Exception>(() => Interpret(viaParameter)).Message);
    }

    // ── Addresses ────────────────────────────────────────────────────────────

    // A pointer out of C, back into C, and used there. `strdup` allocates, `strlen` reads through
    // the pointer on the C side, `free` releases it — no filesystem, so it is the same everywhere.
    // ⚠ `(free(…), 0)` is a top-level comma inside foreign source, which is also what the variadic
    // guard macros exist for.
    private const string AddressHdr =
        "Pull a book on the c-language.\n"
      + "    Define c-language voidable address copy-of, given (the text subject), as [strdup(the subject)].\n"
      + "    Define c-language number length-at, given (the address held), as [strlen((char*)the held)].\n"
      + "    Define c-language number release, given (the address held), as [(free(the held), 0)].\n";

    [Fact]
    public void Address_CrossesOutOfCAndBackIn()
    {
        const string src = AddressHdr + """
                Pull a rabbit.
                    Define copy as cast copy-of on ("hello, world").
                    If copy is void, state "no memory".
                    If copy is not void:
                        State cast length-at on (copy).
                        Cast release on (copy).
                    Done.
                Done.
            Done.
            """;
        Assert.Equal("12", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Address_HeldOutsideARabbit_IsRefused()
    {
        // ★★ The rabbit block IS the unsafe marker, and it needed no new keyword to become one —
        // a pointer is a rabbit responsibility, because the arena that knows when a region dies is
        // what knows when the pointer dies.
        const string src = AddressHdr + """
                Define escaped as cast copy-of on ("nope").
                State "held one outside a rabbit".
            Done.
            """;
        Assert.Contains("only be held inside a rabbit",
                        Assert.ThrowsAny<Exception>(() => Interpret(src)).Message);
    }

    [Fact]
    public void Address_CannotOutliveItsRabbit()
    {
        // ★★ "Leaving the block ends the pointer" is the whole safety claim, and it was NOT
        // enforced: refusing to `Define` one outside a rabbit is only a fraction of the rule, since
        // a handle made INSIDE one could be inserted into a series declared outside and read after
        // `Done.` — measured doing exactly that, on both backends, with `strlen` reading through it.
        //
        // The fix was to let an address into the region model's reference-type test, so the escape
        // check that already guards a series or a map guards this too.
        const string src = AddressHdr + """
                Define escaped as a series of address.
                Pull a rabbit.
                    Define inside as cast copy-of on ("survivor").
                    If inside is not void, insert inside into escaped.
                Done.
            Done.
            """;
        Assert.Contains("shorter-lived rabbit region",
                        Assert.ThrowsAny<Exception>(() => Interpret(src)).Message);
    }

    // `and free it with <name>` — the acquiring axiom names the one that releases what it hands
    // back, because nothing else can: Cufet never reads the foreign text, and `getenv` and `strdup`
    // give back the same type with opposite obligations.
    private const string ReleaseHdr =
        "Pull a book on the c-language.\n"
      + "    Define c-language number shut, given (the address held), as [fclose((FILE*)the held)].\n"
      + "    Define c-language voidable address open-one, given (the text file-path),\n"
      + "        as [fopen(the file-path, \"rb\")], and free it with shut.\n";

    /// <summary>Runs a program against a temp file that EXISTS, asserting the value on both backends.</summary>
    /// <remarks>
    /// ⚠⚠ Not <c>AssertFileOracle</c>, and both differences are load-bearing. It never CREATES the
    /// file, so `fopen` for reading returns NULL on the first try and a counting loop stops at 0;
    /// and it asserts only that the two backends agree, which 0 == 0 satisfies. Written that way,
    /// these tests passed with the release machinery switched off — measured, by sabotage. A count
    /// that proves anything needs a real file and an expected number.
    /// </remarks>
    private static void AssertCountOnBothBackends(string template, string expected)
    {
        var path = WritableTempPath();
        File.WriteAllText(path.Replace('/', Path.DirectorySeparatorChar), "x");
        try
        {
            var src = template.Replace("{PATH}", path);
            Assert.Equal(expected, Interpret(src));
            Assert.Equal(expected, Compile(src));
        }
        finally { try { File.Delete(path.Replace('/', Path.DirectorySeparatorChar)); } catch { } }
    }

    [Fact]
    public void Release_ActuallyFreesAtTheBlocksEnd()
    {
        // ★★ The assertion that means anything here is a COUNT, not an absence of noise. A release
        // that silently does nothing looks identical to one that works — the program prints the
        // same thing either way. Opening past the OS limit is what tells them apart: 509 on
        // Windows, ~1024 on Linux, so 1200 iterations cannot pass unless each `Done.` really freed.
        //
        // ⚠ This is exactly how the compiled backend was caught doing nothing: the registry was
        // there, the registration was emitted, and no block ever ran it because the machinery is
        // gated on a flag that only `Bind unmaking` used to set.
        const string src = ReleaseHdr + """
                Define opened as 0.
                Define ran-out as false.
                For each n in range 1 to 1200, repeat:
                    Pull a rabbit.
                        Define handle as cast open-one on ("{PATH}").
                        If handle is void, the ran-out becomes true.
                        If handle is not void, increment opened by 1.
                    Done.
                    If ran-out is true, stop.
                Done.
                State opened.
            Done.
            """;
        AssertCountOnBothBackends(src, "1200");
    }

    [Fact]
    public void Release_FreesWhenAnExceptionAbandonsTheBlock()
    {
        // ★ The case that cannot be written by hand, and the only thing this clause buys over a
        // `Cast shut on (handle).` at the end of the block: every iteration raises between the
        // open and the block's end, and nothing closes anything explicitly.
        const string src = ReleaseHdr + """
                Define opened as 0.
                Define ran-out as false.
                For each n in range 1 to 1200, repeat:
                    Try to:
                        Pull a rabbit.
                            Define handle as cast open-one on ("{PATH}").
                            If handle is void, the ran-out becomes true.
                            If handle is not void, increment opened by 1.
                            State 1 / 0.
                        Done.
                    Done.
                    In case of exception (the exception):
                        Suppress the exception.
                    Done.
                    If ran-out is true, stop.
                Done.
                State opened.
            Done.
            """;
        AssertCountOnBothBackends(src, "1200");
    }

    [Fact]
    public void Release_OnAVoidResult_FreesNothing()
    {
        // ⚠ NULL is not registered. "C had nothing to give" is not a thing to free, and
        // `fclose(NULL)` is undefined — the guardrail fails toward doing nothing.
        const string src = ReleaseHdr + """
                Pull a rabbit.
                    Define missing as cast open-one on ("no-such-file-anywhere-at-all").
                    State missing is void.
                Done.
            Done.
            """;
        Assert.Equal("true", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Release_ClauseIsCheckedAgainstBothAxioms()
    {
        // Three mistakes, three sentences. None of them is "did you mean the right function" —
        // nothing can check that, and DESIGN accepts the residue.
        const string notAnAddress = """
            Pull a book on the c-language.
                Define c-language number shut, given (the address held), as [fclose((FILE*)the held)].
                Define c-language number counted as [42], and free it with shut.
                Pull a rabbit.
                    State cast counted.
                Done.
            Done.
            """;
        Assert.Contains("does not give back an address",
                        Assert.ThrowsAny<Exception>(() => Interpret(notAnAddress)).Message);

        const string notAnAxiom = """
            Pull a book on the c-language.
                Bind number to shut, given (the number held): Return held. Done.
                Define c-language voidable address grab as [(void*)0], and free it with shut.
                Pull a rabbit.
                    State (cast grab) is void.
                Done.
            Done.
            """;
        Assert.Contains("is not an axiom",
                        Assert.ThrowsAny<Exception>(() => Interpret(notAnAxiom)).Message);

        const string wrongShape = """
            Pull a book on the c-language.
                Define c-language number shut, given (the number held), as [(int)the held].
                Define c-language voidable address grab as [(void*)0], and free it with shut.
                Pull a rabbit.
                    State (cast grab) is void.
                Done.
            Done.
            """;
        Assert.Contains("take exactly one address",
                        Assert.ThrowsAny<Exception>(() => Interpret(wrongShape)).Message);
    }

    [Fact]
    public void Release_TheWordFreeIsNotReserved()
    {
        // ★ The clause costs no reserved word: `it`, `with` and `and` were already tokens, and
        // `free` is recognised by lexeme after `, and`, where nothing else can appear. That matters
        // for a word this ordinary — someone will want it for a binding or a field.
        const string src = """
            Define free as 3.
            Define object budget with (the number free).
            Bind number to free-of, given (the number free): Return free * 2. Done.
            Define plan as a new budget { the free 7 }.
            State free.
            State plan's free.
            State cast free-of on (5).
            """;
        Assert.Equal("3\n7\n10", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── `the text at <address>` — the one read there is ──────────────────────

    private const string ReadHdr =
        "Pull a book on the c-language.\n"
      + "    Define c-language voidable address copy-of, given (the text subject), as [strdup(the subject)].\n"
      + "    Define c-language number let-go, given (the address held), as [({ free(the held); 0; })].\n";

    [Fact]
    public void TextAt_CopiesOutOfForeignMemory()
    {
        // ★★ The assertion the whole design rests on: `the text at` yields rabbit-owned text, never
        // a view into foreign memory. So the C side scribbles over the block AND frees it, and the
        // text read a moment earlier has to be untouched. An aliasing read prints 'XXXX…' or worse.
        const string src = ReadHdr + """
                Define c-language number scribble, given (the address held),
                    as [({ char* p = (char*)the held; size_t n = strlen(p); memset(p, 'X', n); free(p); 0; })].
                Pull a rabbit.
                    Define copy as cast copy-of on ("original bytes").
                    If copy is not void:
                        Define read-out as the text at copy but void is "(nothing)".
                        Cast scribble on (copy).
                        State read-out.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal("original bytes", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void TextAt_ReadsThroughBothAVoidableAndANarrowedAddress()
    {
        // Two branches in the compiler: a `voidable address` carries its own `has`, one narrowed
        // out of its voidable is the bare pointer. Reading a VOID one is void, not a crash.
        const string src = ReadHdr + """
                Define c-language voidable address nowhere as [(void*)0].
                Pull a rabbit.
                    Define copy as cast copy-of on ("held bytes").
                    State the text at copy but void is "(nothing)".
                    Define missing as cast nowhere.
                    State the text at missing but void is "(nothing)".
                    If copy is not void:
                        State the text at copy but void is "(nothing)".
                        Cast let-go on (copy).
                    Done.
                Done.
            Done.
            """;
        Assert.Equal("held bytes\n(nothing)\nheld bytes", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void TextAt_EvaluatesItsAddressOnce()
    {
        // ⚠ The compiled branch for a voidable address needs the `has` and the `val`, and written
        // inline that is the address expression TWICE — two allocations, the first leaked and the
        // second read. It is bound to a local instead; this is what would notice if that changed.
        //
        // ⚠⚠ ONE run per backend, not the oracle pair: the counter is C's own and the interpreter's
        // shim stays loaded in the test host. Source deliberately unique to this test.
        const string src = """
            Pull a book on the c-language.
                Define c-language voidable address counted-copy as
                    [({ static int reads = 0; ++reads; char b[32]; snprintf(b, sizeof b, "read %d", reads); strdup(b); })].
                Pull a rabbit.
                    State the text at (cast counted-copy) but void is "(nothing)".
                Done.
            Done.
            """;
        Assert.Equal("read 1", Interpret(src));
        Assert.Equal("read 1", Compile(src));
    }

    [Fact]
    public void TextAt_RefusesAnythingThatIsNotAnAddress()
    {
        const string src = """
            Define n as 42.
            State the text at n but void is "?".
            """;
        Assert.Contains("reads through a foreign address",
                        Assert.ThrowsAny<Exception>(() => Interpret(src)).Message);
    }

    [Fact]
    public void TextAt_NeitherWordIsReserved()
    {
        // ★ `text` was always contextual and `at` is already matched by lexeme for `<bits> at <n>
        // bits` and `item at (r, c)`. The PAIR is what makes the phrase unmistakable, so the read
        // costs no keyword and both words stay usable as ordinary names.
        const string src = """
            Define text as "a binding named text".
            Define at as 7.
            Define object label with (the text text, the number at).
            Define tag as a new label { the text "held", the at 3 }.
            State text.
            State at.
            State tag's text.
            State tag's at.
            """;
        Assert.Equal("a binding named text\n7\nheld\n3", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Address_ThatIsNotAPointer_IsRefused()
    {
        // The guard that cannot be a `_Generic` — there are infinitely many pointer types, so it
        // asks `__builtin_classify_type` instead. This is what notices when it stops working.
        const string src = """
            Pull a book on the c-language.
                Define c-language voidable address confused as [42].
                Pull a rabbit.
                    Define held as cast confused.
                    State "no".
                Done.
            Done.
            """;
        Assert.Contains("has to produce a C pointer",
                        Assert.ThrowsAny<Exception>(() => CompileRaw(src)).Message);
    }

    [Fact]
    public void Address_NullBecomesVoid()
    {
        // Every address is `voidable address` because NULL is C's universal failure signal —
        // `fopen`, `malloc`, `getenv`, `opendir` all use it. No new failure concept anywhere.
        const string src = """
            Pull a book on the c-language.
                Define c-language voidable address nothing-there as [(void*)0].
                Pull a rabbit.
                    Define held as cast nothing-there.
                    State held is void.
                Done.
            Done.
            """;
        Assert.Equal("true", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Address_PrintsWithoutItsValue()
    {
        // ★ `<address>`, never the pointer, and the reason is the ORACLE rather than secrecy: the
        // two backends are two processes, so the same program's handle is a different number in
        // each and printing it could never agree however correct both were. Same shape as
        // `<function>`, which is there for a different reason and lands in the same place.
        const string src = AddressHdr + """
                Pull a rabbit.
                    Define copy as cast copy-of on ("x").
                    If copy is not void:
                        State copy.
                        Cast release on (copy).
                    Done.
                Done.
            Done.
            """;
        Assert.Equal("<address>", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Address_ComparesByPointer()
    {
        // The only question askable about an address without reading through it. Two separate
        // allocations differ; one compared with itself matches.
        const string src = AddressHdr + """
                Pull a rabbit.
                    Define first as cast copy-of on ("same text").
                    Define second as cast copy-of on ("same text").
                    If first is not void:
                        If second is not void:
                            State first is second.
                            State first is first.
                            Cast release on (first).
                            Cast release on (second).
                        Done.
                    Done.
                Done.
            Done.
            """;
        Assert.Equal("false\ntrue", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── An axiom called for its effect ───────────────────────────────────────

    [Fact]
    public void Axiom_CalledAsAStatement_IsAllowed()
    {
        // ★ `Cast close-dir on (handle).` — the answer is thrown away, which is what a statement
        // means. This used to be REFUSED, and by a message that was not true: "you can only cast
        // functions", when every axiom call in every example is a cast. The expression form had
        // the hook (InferCastExpr) and the statement form did not, so the writer was told to bind
        // a result they had deliberately discarded.
        const string src = """
            Pull a book on the c-language.
                Define c-language number doubled, given (the number n), as [(int)(the n * 2)].
                Cast doubled on (21).
                State "still here".
            Done.
            """;
        Assert.Equal("still here", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_CalledAsAStatement_ActuallyRuns()
    {
        // Discarding the answer must not discard the CALL — the whole point of calling one this
        // way is a side effect in C. The counter is C's own: two discarded calls then one read.
        //
        // ⚠⚠ ONE run per backend, NOT the oracle pair — foreign state is per-process and the
        // interpreter's shim stays loaded in the test host, so a second interpreted run of this
        // same source would continue counting. Same reason as Axiom_WithASideEffect_RunsExactlyOnce.
        //
        // ⚠⚠ And the body must differ from THAT test's, textually. An axiom's identity is its
        // CONTENT — language, result, parameters, spliced source — and deliberately not its name,
        // so two tests whose C happens to match share one cached shim and therefore one counter.
        // Written with the same `++n` first, this read 6 because the other test had already run
        // its three. Any axiom holding state needs source no other test can collide with.
        const string src = """
            Pull a book on the c-language.
                Define c-language number bump as [({ static int discarded_calls = 0; ++discarded_calls; })].
                Cast bump.
                Cast bump.
                State cast bump.
            Done.
            """;
        Assert.Equal("3", Interpret(src));
        Assert.Equal("3", Compile(src));
    }

    [Fact]
    public void Axiom_CalledAsAStatement_IsStillCheckedLikeACall()
    {
        // Discarding the answer discards no CHECK: the arguments still have to fit.
        const string arity = """
            Pull a book on the c-language.
                Define c-language number doubled, given (the number n), as [(int)(the n * 2)].
                Cast doubled.
            Done.
            """;
        Assert.Contains("takes 1 value", Assert.ThrowsAny<Exception>(() => Interpret(arity)).Message);

        const string wrongType = """
            Pull a book on the c-language.
                Define c-language number doubled, given (the number n), as [(int)(the n * 2)].
                Cast doubled on ("not a number").
            Done.
            """;
        Assert.Contains("takes a number", Assert.ThrowsAny<Exception>(() => Interpret(wrongType)).Message);
    }

    [Fact]
    public void Axiom_OnlyEverDiscarded_IsStillBuiltBeforeTheProgramRuns()
    {
        // ⚠ The regression this shape could reopen. Every axiom is compiled BEFORE the first
        // statement runs, so one bad axiom produces no output at all — but the walk that finds them
        // keys on the two nodes that RUN one, and a discarded call is a third. Miss it and this
        // program prints its line and then fails, which is the lazy-compilation divergence again.
        const string src = """
            Pull a book on the c-language.
                Define c-language number broken as [not_a_real_function_at_all()].
                State "this must not print".
                Cast broken.
            Done.
            """;
        var thrown = Assert.ThrowsAny<Exception>(() => InterpretRaw(src));
        Assert.Contains("could not be compiled", thrown.Message);
        Assert.DoesNotContain("this must not print", InterpretThroughFaultRaw(src));
    }

    // ── Floating-point results ───────────────────────────────────────────────

    [Fact]
    public void Axiom_ProducingADouble_CrossesAsAVoidableNumber()
    {
        // ★★ The one conversion DESIGN requires to exist ONCE: base-2 to base-10. The shared C does
        // all of it and hands back the three numbers a decimal is made of, so neither backend
        // converts anything and the last digit cannot disagree between them.
        const string src = """
            Pull a book on the c-language.
                Define c-language voidable number root-two as [sqrt(2.0)].
                Define c-language voidable number a-third as [1.0 / 3.0].
                Define c-language voidable number a-half as [0.5].
                Define c-language voidable number negative as [-2.5].
                Define c-language voidable number a-round-one as [4.0].
                Define c-language voidable number nothing-much as [0.0].
                Define c-language voidable number single as [(float)0.25f].
                State (cast root-two) but void is 0.
                State (cast a-third) but void is 0.
                State (cast a-half) but void is 0.
                State (cast negative) but void is 0.
                State (cast a-round-one) but void is 0.
                State (cast nothing-much) but void is 0.
                State (cast single) but void is 0.
            Done.
            """;
        // 17 significant digits, which is what a double round-trips in and what %.16e produces.
        Assert.Equal("1.4142135623730951\n0.33333333333333331\n0.5\n-2.5\n4\n0\n0.25", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_ProducingSomethingWithNoDecimal_IsVoid()
    {
        // ★ Not a new rule: `math`'s partial functions already answer this way, and the recorded
        // test there is `!IsFinite` rather than `IsNaN` precisely because `log(0)` is an infinity.
        // A magnitude outside a decimal's range joins them — refusing beats a silent 0 or a
        // silently rounded answer, which is why 1e-300 is void rather than zero.
        const string src = """
            Pull a book on the c-language.
                Define c-language voidable number not-a-number as [sqrt(-1.0)].
                Define c-language voidable number too-big as [1.0 / 0.0].
                Define c-language voidable number below-the-floor as [1e-300].
                Define c-language voidable number above-the-ceiling as [1e300].
                State (cast not-a-number) is void.
                State (cast too-big) is void.
                State (cast below-the-floor) is void.
                State (cast above-the-ceiling) is void.
            Done.
            """;
        Assert.Equal("true\ntrue\ntrue\ntrue", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_TheTwoNumberGuards_AreDisjoint()
    {
        // ⚠ Declaring the wrong one of the two is REFUSED, not converted. A `number` is exact and a
        // `voidable number` is the lossy conversion, so which one you asked for is a real question
        // and the C compiler answers it from the expression's own type.
        const string whole = """
            Pull a book on the c-language.
                Define c-language voidable number confused as [42].
                State (cast confused) but void is 0.
            Done.
            """;
        Assert.Contains("C floating-point value", Assert.ThrowsAny<Exception>(() => CompileRaw(whole)).Message);

        const string real = """
            Pull a book on the c-language.
                Define c-language number confused as [4.5].
                State cast confused.
            Done.
            """;
        Assert.Contains("C whole number", Assert.ThrowsAny<Exception>(() => CompileRaw(real)).Message);
    }

    [Fact]
    public void Axiom_GuardMessages_AreAsciiOnly()
    {
        // ⚠ A guard message is a C string literal inside a _Static_assert, and gcc echoes it back
        // with every non-ASCII byte escaped — an em-dash reached a reader as
        // `\37777777742\37777777600\37777777624` mid-sentence. Cheap to pin, and the failure is
        // invisible until someone actually makes the mistake the message is for.
        foreach (var message in new[] { ForeignC.WholeGuardMessage, ForeignC.RealGuardMessage,
                                        ForeignC.TextGuardMessage })
            Assert.All(message, c => Assert.InRange(c, ' ', '~'));
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

    // ── Unsigned 64-bit values ───────────────────────────────────────────────

    [Fact]
    public void Axiom_ProducingAnUnsignedSixtyFourBitValue_CrossesWithoutACast()
    {
        // ★ `size_t` is how most of libc reports a length, so this is the shape the boundary meets
        // most often. It used to be REFUSED, and the example in examples/systems/foreign.cufe had
        // to write `(int)strlen(...)` to get past the guard.
        const string src = """
            Pull a book on the c-language.
                Define c-language number greeting-length as [strlen("hello, world")].
                Define c-language number size-of-a-long as [sizeof(long long)].
                State cast greeting-length.
                State cast size-of-a-long.
            Done.
            """;
        Assert.Equal("12\n8", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_ProducingAnUnsignedValueAboveSignedRange_IsNotNegative()
    {
        // ★★ THE reason the boundary carries a signedness flag rather than one more cast. Every
        // value here is above `long long`'s ceiling, so a plain `(long long)` would have read each
        // one back as a negative number — silently, and only for large inputs, which is the worst
        // shape a wrong answer can have. Both backends reconstruct the same decimal.
        const string src = """
            Pull a book on the c-language.
                Define c-language number widest as [(unsigned long long)-1].
                Define c-language number just-over as [9223372036854775808ULL].
                Define c-language number and-one-more as [(unsigned long long)9223372036854775809ULL].
                State cast widest.
                State cast just-over.
                State cast and-one-more.
            Done.
            """;
        Assert.Equal("18446744073709551615\n9223372036854775808\n9223372036854775809", Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_SignedResults_StillReadBackAsThemselves()
    {
        // The other half of the same change: admitting unsigned must not have disturbed how a
        // signed value crosses. A negative arrives as its two's-complement bits and is read back
        // through the flag being CLEAR, so this is the branch that would break if the flag were
        // ever set wrongly.
        const string src = """
            Pull a book on the c-language.
                Define c-language number below-zero as [-5].
                Define c-language number biggest-signed as [(long long)9223372036854775807LL].
                Define c-language number smallest-signed as [(long long)(-9223372036854775807LL - 1)].
                Define c-language number narrow-unsigned as [(unsigned int)4294967295U].
                Define c-language number flagged-but-small as [(unsigned long)4294967295UL].
                Define c-language number a-byte as [(unsigned char)200].
                Define c-language number a-truth as [(_Bool)1].
                State cast below-zero.
                State cast biggest-signed.
                State cast smallest-signed.
                State cast narrow-unsigned.
                State cast flagged-but-small.
                State cast a-byte.
                State cast a-truth.
            Done.
            """;
        // ★ `flagged-but-small` is the third branch and the easiest one to get wrong: `unsigned
        // long` IS flagged unsigned, and it is only 32 bits on Windows — so the value has to come
        // back the same whichever width the platform gives it.
        Assert.Equal("-5\n9223372036854775807\n-9223372036854775808\n4294967295\n4294967295\n200\n1",
                     Interpret(src));
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void Axiom_WithASideEffect_RunsExactlyOnce()
    {
        // ⚠ The foreign text is written into the wrapper THREE times now — the guard, the value,
        // and the signedness flag — and only one of those may evaluate it. `_Generic` leaves its
        // controlling expression and every unselected association unevaluated, which is what makes
        // that safe; this test is what would notice if a future arm stopped being a `_Generic`.
        //
        // The counter is C's own: each call returns the value AFTER incrementing, so a body
        // evaluated twice per call would count up in twos.
        //
        // ⚠⚠ ONE run per backend, and NOT the usual oracle pair. This axiom keeps state in C, and
        // foreign state is PER-PROCESS: a compiled program is its own process and starts at zero,
        // but the interpreter calls C inside the test host and its shim stays loaded, so a second
        // interpreted run of this same source would count 4, 5, 6. Comparing the two backends here
        // would be comparing a fresh process against a warm one. Do not "fix" this by adding an
        // AssertEqual(InterpretRaw, CompileRaw) beneath it.
        const string src = """
            Pull a book on the c-language.
                Define c-language number tick as [({ static int n = 0; ++n; })].
                State cast tick.
                State cast tick.
                State cast tick.
            Done.
            """;
        Assert.Equal("1\n2\n3", Interpret(src));
        Assert.Equal("1\n2\n3", Compile(src));
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
        Assert.Contains("not yet written down as a type", e.Message);
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
                Define c-language bits flags as [0xF0].
            Done.
            """));
        Assert.Contains("cannot give back a bits yet", e.Message);
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

    /// <summary>How many axiom wrappers the emitted C defines.</summary>
    /// <remarks>
    /// ⚠ Counts DEFINITIONS, not one spelling of them. The wrapper's C return type varies with
    /// what the axiom gives back — `long long`, `int`, `const char*` — so matching on a fixed
    /// prefix silently counted zero the day a second result type arrived.
    /// </remarks>
    private static int WrapperCount(string emittedC) =>
        System.Text.RegularExpressions.Regex.Matches(
            emittedC, "^static .*" + ForeignC.FunctionPrefix,
            System.Text.RegularExpressions.RegexOptions.Multiline).Count;

    private static Program Checked(string source) =>
        new TypeChecker().Check(new Parser(new CufetLexer(source).Tokenize()).Parse());
}
