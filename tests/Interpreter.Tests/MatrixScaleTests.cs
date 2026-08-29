using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// Scaling a matrix by a number — `m * 2` and `2 * m`.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ This does NOT go through operator overloading, which was the standing assumption on the
/// roadmap and was wrong. `matrix` is a BUILT-IN the `collections` book puts in scope, not an
/// object type, and overloads only register on object types. Matrix arithmetic is a built-in rule
/// in the checker with native evaluation beside it, and scaling is one more arm in those places.
/// </para>
/// <para>
/// ★★ Scaling is the one matrix operation that CANNOT FAIL — there are no dimensions to disagree
/// — so it gives a plain `matrix`, not `matrix or failure`, and needs no `Try` around it. Making
/// it fallible for consistency with its neighbours would force a failure handler around an
/// operation with no failure in it, which teaches a reader the opposite of the truth.
/// </para>
/// </remarks>
public class MatrixScaleTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var parsed  = new Parser(tokens).Parse();
        var program = new TypeChecker().Check(parsed);
        var output  = new System.IO.StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static TypeException Refused(string source) =>
        Assert.Throws<TypeException>(() => Run(source));

    [Fact]
    public void AMatrixScalesByANumber_WithNoTryAroundIt()
    {
        // ! The absence of `Try to:` here is the assertion. `m * m` in the same position would be
        // refused with "matrix '*' can fail — you must handle the failure".
        Assert.Equal("matrix((2, 4), (6, 8))", Run("""
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                State m * 2.
            Done.
            """));
    }

    [Fact]
    public void TheNumberMayComeFirst()
    {
        // ★ Both orders, because `2M` and `M2` are the same thing wherever matrices are written.
        // ⚠ That is NOT the overload rule — an overload names an ORDERED pair, because a writer's
        // `-` and `/` are not commutative and the language cannot know which of theirs is. This is
        // built-in multiplication by a scalar, commutative by definition.
        Assert.Equal("matrix((3, 6), (9, 12))", Run("""
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                State 3 * m.
            Done.
            """));
    }

    [Fact]
    public void ScalingProducesAPlainMatrix_UsableWithoutHandlingAFailure()
    {
        // ★ The type, not just the value: a scaled matrix flows straight into another expression.
        // If scaling were fallible this would be refused for not handling the failure.
        Assert.Equal("matrix((4, 8), (12, 16))", Run("""
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                Define doubled as m * 2.
                State doubled * 2.
            Done.
            """));
    }

    [Fact]
    public void MatrixTimesMatrix_IsStillFallible()
    {
        // ⚠ The control for the asymmetry. `*` is fallible on two matrices because their
        // dimensions can disagree — nothing about the OPERATOR promises that, so scaling is free
        // of it while the product is not.
        var e = Refused("""
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                State m * m.
            Done.
            """);
        Assert.Contains("can fail", e.Message);
    }

    [Fact]
    public void MatrixPlusMatrix_IsUnchanged()
    {
        Assert.Equal("matrix((2, 4), (6, 8))", Run("""
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                Try to:
                    State m + m.
                Done.
                In case of failure:
                    State the message of the failure.
                Done.
            Done.
            """));
    }

    [Fact]
    public void ScalingIsNotAdditionOrSubtraction()
    {
        // ⚠ Only `*` scales. `m + 2` has no meaning — adding a scalar to every element is a
        // different operation with a different name in every language that has both, and nobody
        // asked for it.
        var e = Refused("""
            Pull a book on collections.
                Define m as a matrix with ((1, 2), (3, 4)).
                State m + 2.
            Done.
            """);
        Assert.Contains("arithmetic requires numbers", e.Message);
    }
}
