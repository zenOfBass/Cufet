using Cufet.Interpreter;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `and region` — a lifetime a person can write, publish, and be handed work by.
/// </summary>
/// <remarks>
/// <para>
/// ★★ WHAT THIS CLOSES. `rabbit` was the only object in the language that owned a region, and it
/// did not get that by being a module: `Pull a rabbit` was its own AST node emitting
/// `cufet_arena_push()`. So the power existed and the door to it did not, and the settled line —
/// *a module MAY own a lifetime, a book may not* — had no teeth, because nothing else owned one.
/// `rabbit.cufe` now says `and region` like any other region, so rabbit is the FIRST INSTANCE of
/// the rule rather than an exception to it.
/// </para>
/// <para>
/// ★ The soundness story cost nothing extra. Every rule that keeps a rabbit sound is depth
/// arithmetic, so one `_rabbitDepth++` at a region pull buys the whole adversarially-tested
/// outward-only invariant — which is what `ARegionScopedValue_CannotEscapeIt` pins.
/// </para>
/// </remarks>
public class PipelineRegionTests : PipelineTestBase
{
    [Fact]
    public void PullingAUserWrittenRegion_RunsOnBothBackends()
    {
        const string src = """
            Define object workspace with () and region.

            State "before".
            Pull workspace as bench.
                Define scratch as a series of number with (1, 2, 3).
                State the number of scratch.
            Done.
            State "after".
            """;
        Assert.Equal("before\n3\nafter\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void AUserWrittenRegion_CanBeToldToBury()
    {
        // ★★ THE HEADLINE. A region a stranger could write and publish, doing the one thing only
        // the built-in rabbit could do. Not a rabbit anywhere in this program.
        const string src = """
            Define object workspace with () and region.

            Bind number to counting-up, given (the workspace bench, the number first-value):
                Define next as first-value.
                Repeat:
                    Have bench bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Pull workspace as bench.
                Define counter as cast counting-up on (bench, 3).
                State unbury counter.
                State unbury counter.
                State unbury counter.
            Done.
            """;
        Assert.Equal("3\n4\n5\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void ARegionScopedValue_CannotEscapeIt()
    {
        // ⚠ THE SOUNDNESS GUARD, and the reason a region pull raises the depth. Without the
        // increment a region reads as ordinary straight-line code and its values can be stored
        // anywhere — the use-after-free the outward-only invariant exists to prevent.
        var ex = Assert.Throws<TypeException>(() => InterpretRaw("""
            Define object workspace with () and region.
            Define keeper as a series of series of number.
            Pull workspace as bench.
                Define inner as a series of number with (1, 2, 3).
                Insert inner into keeper.
            Done.
            """));
        Assert.Contains("shorter-lived region than its destination", ex.Message);

        // ★ The ADVICE is pinned too. It said "move the container inside the rabbit block" from
        // before a person could write a region, to a program that had pulled no rabbit at all.
        Assert.DoesNotContain("rabbit", ex.Message);
        Assert.Contains("Declare the container inside that region too", ex.Message);
    }

    [Fact]
    public void ARabbitIsNowAnOrdinaryRegion_AndStillWorks()
    {
        // ★ `rabbit.cufe` says `and region` now. This is the regression guard on that: the
        // built-in path must keep behaving exactly as it did while being an instance of the rule.
        const string src = """
            Bind number to counting-up, given (the rabbit helper, the number first-value):
                Define next as first-value.
                Repeat:
                    Have helper bury next.
                    The next becomes next + 1.
                Until false.
            Done.

            Pull a rabbit as hopper.
                Define counter as cast counting-up on (hopper, 3).
                State unbury counter.
                State unbury counter.
            Done.
            """;
        Assert.Equal("3\n4\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    // ── Refusals ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnObjectThatOwnsNoRegion_CannotBeToldToBury()
    {
        // The rule the receiver check is actually about: owning a region, not being a rabbit.
        var ex = Assert.Throws<TypeException>(() => InterpretRaw("""
            Define object toolkit with () and module.
            Bind number to counting-up, given (the toolkit kit, the number first-value):
                Have kit bury first-value.
            Done.
            Pull toolkit as kit.
                Define counter as cast counting-up on (kit, 3).
                State unbury counter.
            Done.
            """));
        Assert.Contains("does not own a region", ex.Message);
    }

    [Fact]
    public void BookAndRegion_AreOppositeClaims_AndRefused()
    {
        // ⚠ A book owns NO lifetime by definition; a region is nothing but one. Refused at the
        // DECLARATION, where the wrong sentence was written, not at whoever pulls it.
        var ex = Assert.Throws<TypeException>(() => InterpretRaw("""
            Define object muddle with () and book and region.
            """));
        Assert.Contains("owns no lifetime", ex.Message);
    }
}
