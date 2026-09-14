using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// `blueprints's checksum of (path)` — FNV-1a, 64-bit, over a file's bytes.
/// </summary>
///
/// ★★ THE POINT OF THIS FILE IS THE PUBLISHED VECTORS. The algorithm is written TWICE — in C# for
/// the interpreter and in C for the emitted runtime — which is exactly the divergence the oracle
/// exists to catch, and which the oracle would catch only if some program's output happened to
/// differ. Pinning both to numbers nobody on this project wrote is what makes two implementations
/// safe: neither side is graded by the other.
///
/// ★ That is the same move `examples/algorithms/beamforming.cufe` makes against C, and the same
/// reason `semver.cufe` checks itself against the spec rather than against its own last run.
/// Agreement is not correctness.
public class BlueprintChecksumTests
{
    private static string Run(string source)
    {
        var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        program = new TypeChecker().Check(program);
        var output = new StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    /// <summary>Writes exact bytes — no encoding preamble, no trailing newline.</summary>
    private static string FileHolding(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), "cufet-hash-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(contents));
        return path.Replace("\\", "/");
    }

    [Theory]
    // The canonical FNV-1a 64 vectors. The empty one is the offset basis by definition, which is
    // why it is the one that catches a wrong constant rather than a wrong loop.
    [InlineData("", "0xCBF29CE484222325")]
    [InlineData("a", "0xAF63DC4C8601EC8C")]
    [InlineData("foobar", "0x85944171F73967E8")]
    public void TheHashOfAFile_MatchesThePublishedVector(string contents, string expected)
    {
        var path = FileHolding(contents);
        try
        {
            Assert.Equal(expected, Run($$"""
                Pull a book on blueprints.
                    Define mark as blueprints's checksum of ("{{path}}").
                    State "{mark but void is 0b0}".
                Done.
                """));
        }
        finally { File.Delete(path); }
    }

    /// <remarks>
    /// ★ Void rather than a failure. A build asks about inputs that do not exist yet constantly —
    /// "there is nothing there to hash" is an answer, not an error.
    /// </remarks>
    [Fact]
    public void TheHashOfAMissingFile_IsVoid()
    {
        Assert.Equal("true", Run("""
            Pull a book on blueprints.
                Define mark as blueprints's checksum of ("no-such-file-anywhere.txt").
                State (mark is void) converted to text.
            Done.
            """));
    }

    /// <summary>`step` names the record shape — and is interchangeable with writing it out.</summary>
    /// <remarks>
    /// <para>
    /// ★★ THE WHOLE CLAIM IS INTERCHANGEABILITY, so both directions are here. Record typing is
    /// structural, so `step` is a NAME for a shape rather than a new type: a value built with
    /// `a record with (…)` satisfies it, and a `step`-typed value satisfies a parameter written out
    /// longhand. If that ever stopped being true, every blueprint would break at the point its
    /// result meets the walker, which is a long way from where the mistake would be.
    /// </para>
    /// <para>
    /// ⚠⚠ THE CALL IS OUTSIDE THE PULL, and that placement is the entire test. Everything here
    /// passes with the fix reverted if the call sits INSIDE the pull block — measured, twice, on
    /// two earlier drafts of this test that proved nothing. Outside is where the appended `cufet
    /// build` driver puts it, and outside is where `step` is not in scope, so that is the only
    /// arrangement that meets the defect: the signature resolved before any pull was walked, stayed
    /// an ObjectType shell, and the blueprint checked CLEAN before failing against the walker with
    /// *"a series of step objects"*. See RegisterPulledBookTypes.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheStepType_IsInterchangeableWithTheShapeWrittenOut()
    {
        Assert.Equal("took 1", Run("""
            Bind void to took, given (
                the series of records like (
                    the text name,
                    the series of text needs,
                    the series of text makes,
                    the series of text runs) items):
                State "took {the number of items converted to text}".
            Done.

            Pull a book on blueprints.
                Bind series of step to made:
                    Define work as a series of step.
                    Insert a record with (
                        the name "one-thing",
                        the needs a series of text with (),
                        the makes a series of text with (),
                        the runs a series of text with ()) into work.
                    Return work.
                Done.
            Done.

            Cast took on (cast made).
            """));
    }

    /// <remarks>
    /// ⚠⚠ The guard on the PRELUDE'S OWN PULL. `bp-stamp in blueprints` pulls `blueprints` to reach
    /// `checksum`, and that statement is spliced into every program — so counting it as the
    /// program's pull put `step` in scope everywhere. MEASURED before the guard: a program pulling
    /// nothing could name the type. This is the second time in one day the prelude's own pull had
    /// to be told apart from the writer's; see `_inPrelude`.
    /// </remarks>
    [Fact]
    public void AProgramThatPullsNothing_CannotNameTheStepType()
    {
        var refused = Assert.Throws<TypeException>(() => Run("""
            Define holder as a series of step.
            Insert a record with (
                the name "x",
                the needs a series of text with (),
                the makes a series of text with (),
                the runs a series of text with ()) into holder.
            State "unreachable".
            """));

        Assert.Contains("step", refused.Message);
    }

    /// <remarks>
    /// ⚠ The half that would catch a hash that ignores its input — a constant passes every vector
    /// test above if the constant happens to be right, and passes none of them if it is not, but a
    /// hash that reads only the FIRST byte would pass "a" and fail here.
    /// </remarks>
    [Fact]
    public void TwoFilesDifferingLateInTheirContents_HashDifferently()
    {
        var left  = FileHolding("the same prefix, and then A");
        var right = FileHolding("the same prefix, and then B");
        try
        {
            var answer = Run($$"""
                Pull a book on blueprints.
                    Define left-mark as blueprints's checksum of ("{{left}}").
                    Define right-mark as blueprints's checksum of ("{{right}}").
                    State ((left-mark but void is 0b0) is not (right-mark but void is 0b0)) converted to text.
                Done.
                """);
            Assert.Equal("true", answer);
        }
        finally { File.Delete(left); File.Delete(right); }
    }
}
