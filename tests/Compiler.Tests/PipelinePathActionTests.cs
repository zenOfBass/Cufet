using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `Make the directory …`, `Remove the file …`, `Remove the directory …` — the three things the
/// language could not do, and the refusals that are most of what they are.
/// </summary>
/// <remarks>
/// <para>
/// ★★ **The axiom campaign's second real witness, closed.** Cufet could read a file, write a file
/// and list a directory, and could neither create nor remove one. It was found by building the
/// package manager, which routed around it — `git clone` made the directory and the clone was kept
/// as a cache. That is a real design, but it was a constraint wearing a decision's clothes.
/// </para>
/// <para>
/// ⚠⚠ **EVERY ONE OF THESE RUNS BOTH BACKENDS AND COMPARES**, which matters more here than usual:
/// the two arrive at the same refusals by different routes. .NET's `CreateDirectory` makes the
/// whole chain and succeeds on a directory already there, so the interpreter asks explicitly; C's
/// `mkdir` declines both by itself, so the compiler maps errno. Nothing but the oracle holds those
/// two together.
/// </para>
/// <para>
/// ⚠ The paths are absolute, under a scratch directory `AssertCwdOracle` makes and deletes. A
/// relative path would be resolved against the process working directory, which the interpreter
/// shares with the test host.
/// </para>
/// <para>
/// ⚠⚠ **EACH PROGRAM HERE MUST LEAVE THE WORLD AS IT FOUND IT**, because the oracle RUNS IT TWICE
/// — once interpreted, once compiled, in the same directory. MEASURED: a test that made a
/// directory and did not remove it passed interpreted and then died on the compiled run, which
/// found the directory already there. The compiled output was EMPTY and the diff looked like a
/// backend divergence. It was the test.
/// </para>
/// </remarks>
public class PipelinePathActionTests : PipelineTestBase
{
    [Fact]
    public void MakeWriteRemove_RoundTrip_MatchesInterpreter()
    {
        AssertCwdOracle("""
            Try to:
                Make the directory "{DIR}/books".
                State "made it".
                Write "hi" to the file "{DIR}/books/note.txt".
                State "wrote into it".
                State the contents of the directory "{DIR}/books".
                Remove the file "{DIR}/books/note.txt".
                State "removed the file".
                Remove the directory "{DIR}/books".
                State "removed the directory".
                If the path "{DIR}/books" exists, state "still there". Otherwise, state "gone".
            Done.
            In case of failure:
                State "failed: {the message of the failure}".
            Done.
            """);
    }

    /// <remarks>
    /// ⚠⚠ NOT RECURSIVE, and that is the decision rather than the implementation. Three directories
    /// appearing because one was asked for is exactly the quiet resolution this language declines
    /// everywhere else — and the refusal names the thing you can now go and make.
    /// </remarks>
    [Fact]
    public void MakingInsideAMissingParent_IsRefused_MatchesInterpreter()
    {
        AssertCwdOracle("""
            Try to:
                Make the directory "{DIR}/a/b/c".
                State "unreachable".
            Done.
            In case of failure:
                Define why as the category of the failure but void is "none".
                State "{why}: {the message of the failure}".
            Done.
            """);
    }

    [Fact]
    public void MakingOneThatIsAlreadyThere_IsRefused_MatchesInterpreter()
    {
        AssertCwdOracle("""
            Make the directory "{DIR}/twice".
            Try to:
                Make the directory "{DIR}/twice".
                State "unreachable".
            Done.
            In case of failure:
                Define why as the category of the failure but void is "none".
                State "{why}: {the message of the failure}".
            Done.
            Remove the directory "{DIR}/twice".
            """);
    }

    /// <remarks>
    /// ⚠⚠ THE ONE OPERATION HERE THAT CAN DESTROY SOMEBODY'S WORK, so it refuses rather than
    /// guessing what you meant. Emptying a directory is a loop you write on purpose.
    /// </remarks>
    [Fact]
    public void RemovingANonEmptyDirectory_IsRefused_MatchesInterpreter()
    {
        AssertCwdOracle("""
            Make the directory "{DIR}/full".
            Write "x" to the file "{DIR}/full/x.txt".
            Try to:
                Remove the directory "{DIR}/full".
                State "unreachable".
            Done.
            In case of failure:
                Define why as the category of the failure but void is "none".
                State "{why}: {the message of the failure}".
            Done.
            If the path "{DIR}/full/x.txt" exists, state "the file survived".
            Remove the file "{DIR}/full/x.txt".
            Remove the directory "{DIR}/full".
            """);
    }

    [Fact]
    public void RemovingWhatIsNotThere_IsRefused_MatchesInterpreter()
    {
        AssertCwdOracle("""
            Try to: Remove the file "{DIR}/ghost.txt". Done.
            In case of failure: State "file: {the message of the failure}". Done.

            Try to: Remove the directory "{DIR}/ghost". Done.
            In case of failure: State "directory: {the message of the failure}". Done.
            """);
    }

    /// <remarks>
    /// ⚠⚠ THE MESSAGE THIS SLICE CAME FROM. Writing into a directory that does not exist used to
    /// answer *"the file '…' was not found"* — nothing was being looked for, and the thing that was
    /// missing was never named. ★ A write cannot fail because the FILE is missing; it creates one.
    /// So a not-found on a write can only mean the directory, and both backends tell them apart by
    /// the OPERATION rather than by inspecting the path — which is what keeps them identical.
    /// </remarks>
    [Fact]
    public void WritingIntoAMissingDirectory_NamesTheDirectory_MatchesInterpreter()
    {
        AssertCwdOracle("""
            Try to:
                Write "x" to the file "{DIR}/nope/out.txt".
                State "unreachable".
            Done.
            In case of failure:
                Define why as the category of the failure but void is "none".
                State "{why}: {the message of the failure}".
            Done.
            """);
    }

    /// <remarks>
    /// ★★ `make` IS NOT RESERVED. It is recognised only at a statement head with `directory`
    /// following, the same way `output`, `Seed` and `The current directory` already are — so a word
    /// this ordinary stays available to every program. Spending it would have cost a name forever,
    /// and the naming rule's first question is whether a keyword is needed at all.
    /// </remarks>
    [Fact]
    public void MakeIsStillAnOrdinaryName_MatchesInterpreter()
    {
        const string src = """
            Define make as "Toyota".
            State make.
            Define makes as a series of text with ("a", "b").
            State the number of makes.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
