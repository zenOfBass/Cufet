using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// A pull is LEXICAL: a function written inside one keeps it, even when called from outside.
/// </summary>
/// <remarks>
/// <para>
/// ⚠⚠ A MEASURED BACKEND DIVERGENCE, 2026-09-15. `check` passed, the compiled program printed the
/// right answer, and the interpreter died with *"'math' isn't defined … Declare it first: Define
/// math as &lt;value&gt;"* — on a program that pulls `math` four lines above. Advice to `Define` a
/// book you pulled, for a program both other halves of the language agreed was fine.
/// </para>
/// <para>
/// ★ The cause: hoisting is transparent to a `Pull … Done.` body, so such a function is callable
/// from outside the block — and the interpreter's `SaveScopes` carries book bindings out of the
/// CALLER's live scope, which has none. The compiler resolved it lexically all along, and its own
/// note says so: it emits a fresh receiver rather than the pull's binding precisely so a `Bind`
/// hoisted out of a pull body works.
/// </para>
/// <para>
/// ⚠⚠ THE ORACLE COULD NOT SEE IT. Every program in the corpus calls such a function from inside
/// the block it was written in, where the dynamic and lexical answers coincide. The suite was green
/// throughout. That is a SHAPE blind spot rather than the machine one already recorded — a program
/// nobody had written, rather than a machine nobody had run on.
/// </para>
/// </remarks>
public class PipelineLexicalPullTests : PipelineTestBase
{
    /// <remarks>★ The minimal witness. Both backends must print the same thing, and it must be
    /// the answer rather than a refusal.</remarks>
    [Fact]
    public void AFunctionWrittenInsideAPull_KeepsItWhenCalledOutside()
    {
        const string src = """
            Pull a book on math.
                Bind number to area, given (the number r):
                    Return math's pi * r * r.
                Done.
            Done.

            State (cast area on (2)) converted to text.
            """;

        var interpreted = Interpret(src);
        Assert.Equal(interpreted, Compile(src));
        Assert.Contains("12.56637", interpreted);
        Assert.DoesNotContain("isn't defined", interpreted);
    }

    /// <remarks>
    /// ⚠ A WRITER'S OWN MODULE, not a bundled book — the fault was never about books. All three
    /// pull kinds diverged identically, which is what says the rule is about pulling rather than
    /// about what was pulled.
    /// </remarks>
    [Fact]
    public void APulledModule_IsKeptTheSameWay()
    {
        const string src = """
            Define object helper with () and module:
                Bind text to greet: Return "hi". Done.
            Done.

            Pull a helper.
                Bind text to speak: Return cast helper's greet. Done.
            Done.

            State cast speak.
            """;

        var interpreted = Interpret(src);
        Assert.Equal(interpreted, Compile(src));
        Assert.Contains("hi", interpreted);
    }

    /// <remarks>
    /// ⚠ Through a CALL CHAIN. The outer function is the one called from outside; the inner one is
    /// reached only from within it, and still needs the pull neither of them is standing in.
    /// </remarks>
    [Fact]
    public void ThePullSurvivesACallChain()
    {
        const string src = """
            Pull a book on math.
                Bind number to inner, given (the number r): Return math's pi * r. Done.
                Bind number to outer, given (the number r): Return cast inner on (r) * r. Done.
            Done.

            State (cast outer on (2)) converted to text.
            """;

        var interpreted = Interpret(src);
        Assert.Equal(interpreted, Compile(src));
        Assert.Contains("12.56637", interpreted);
    }

    /// <remarks>★ NESTED pulls, where the inner one was the first to fail — both levels have to be
    /// carried, not just the nearest.</remarks>
    [Fact]
    public void NestedPulls_AreBothKept()
    {
        const string src = """
            Pull a book on math.
                Pull a book on collections.
                    Bind number to biggest, given (the series of number xs):
                        Return (collections's maximum of (xs) but void is 0) + math's pi.
                    Done.
                Done.
            Done.

            Define xs as a series of number with (1, 5, 3).
            State (cast biggest on (xs)) converted to text.
            """;

        var interpreted = Interpret(src);
        Assert.Equal(interpreted, Compile(src));
        Assert.Contains("8.14159", interpreted);
    }

    /// <remarks>
    /// ⚠ The control, and the reason the bug hid for so long: called from INSIDE the block, the
    /// dynamic and lexical answers are the same. Every corpus program is this shape.
    /// </remarks>
    [Fact]
    public void CalledFromInsideTheBlock_StillWorks()
    {
        const string src = """
            Pull a book on math.
                Bind number to area, given (the number r): Return math's pi * r * r. Done.
                State (cast area on (2)) converted to text.
            Done.
            """;

        Assert.Equal(Interpret(src), Compile(src));
    }
}
