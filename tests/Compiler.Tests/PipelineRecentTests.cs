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
public class PipelineRecentTests : PipelineTestBase
{

    [Fact]
    public void MemberAccess_OnSomethingWithNoMembers_IsRefusedByTheFrontEnd()
    {
        // ★ The front end owns this, and the generator's refusal sits BEHIND it as a backstop with
        // no route from source — every type the checker permits `'s` on (object, matrix, mapping,
        // record, failure, exception, book) now has an explicit arm in EmitMemberAccess, so the
        // throw is unreachable today and deliberately so. It exists because the arm it replaced was
        // a catch-all that emitted the record shape for whatever arrived: when a union reached it
        // through the Judge grouped-arm bug, the result was invalid C emitted WITHOUT raising, so
        // `check --native` called the program clean. Should checker and generator ever disagree
        // again, that now costs a refusal — which `check --native` reports — instead of a build
        // that fails with a message about generated identifiers.
        var e = Assert.Throws<TypeException>(() => GenerateC("""
            Define nums as a series of number with (1, 2).
            State nums's nope.
            """));
        Assert.Contains("requires an object", e.Message);
    }

    // ── Judge: narrowing again inside a grouped arm ──────────────────────
    //
    // A grouped arm (`A quote or a paragraph:`) leaves `it` a union, so the arm itself narrows the
    // TYPE without changing the REPRESENTATION — cv_it stays the subject's whole union struct.
    // Narrowing again inside the arm is exhaustive to the checker and was not to the compiler,
    // which kept eliminating from every case of the subject rather than from the arm's, found two
    // left, declined to narrow, and then emitted the field access against the union anyway. The
    // result was C that gcc rejects, so it surfaced at build time with `check --native` silent.
    //
    // Found by examples/renderer.cufe. The three-case shape is the smallest that shows it: with a
    // two-case union the arm covers everything, so the two elimination sets are equal and agree.

    [Fact]
    public void JudgeGroupedArm_NarrowedAgainInside_MatchesInterpreter()
    {
        const string src = """
            Define object alpha with (the text body, the text source).
            Define object beta with (the text body).
            Define object gamma with (the text tag).

            Bind text to pick, given (the (alpha or beta or gamma) thing):
                Judge thing, where it is:
                    An alpha or a beta:
                        If it is an alpha, return it's source.
                        Otherwise, return it's body.
                    Done.
                    A gamma, return it's tag.
                Done.
            Done.

            State cast pick on (a new alpha { the body "B", the source "S" }).
            State cast pick on (a new beta { the body "B2" }).
            State cast pick on (a new gamma { the tag "T" }).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("S\nB2\nT", Compile(src));
    }

    [Fact]
    public void JudgeGroupedArm_ArmOrderNeedNotMatchTheSubjects()
    {
        // ★ Why the fix keeps a case SET rather than substituting a narrower union TYPE. The arm
        // lists its cases in the opposite order to the subject, so a sub-union's own indices would
        // reach the wrong member — every emitted access has to index the representation union.
        const string src = """
            Define object alpha with (the text a-field).
            Define object beta with (the number b-field).
            Define object gamma with (the text c-field).

            Bind text to pick, given (the (alpha or beta or gamma) thing):
                Judge thing, where it is:
                    A gamma or a beta:
                        If it is a beta, return "beta".
                        Otherwise, return it's c-field.
                    Done.
                    An alpha, return it's a-field.
                Done.
            Done.

            State cast pick on (a new gamma { the c-field "G" }).
            State cast pick on (a new beta { the b-field 1 }).
            State cast pick on (a new alpha { the a-field "A" }).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("G\nbeta\nA", Compile(src));
    }

    [Fact]
    public void JudgeOtherwise_CoveringTwoCases_NarrowsAgainInside()
    {
        // The same restriction applies to a Judge's own Otherwise when it covers more than one case.
        const string src = """
            Define object alpha with (the text body).
            Define object beta with (the text body).
            Define object gamma with (the text tag).

            Bind text to pick, given (the (alpha or beta or gamma) thing):
                Judge thing, where it is:
                    A gamma, return it's tag.
                    Otherwise:
                        If it is an alpha, return "a:" joined to it's body.
                        Otherwise, return "b:" joined to it's body.
                    Done.
                Done.
            Done.

            State cast pick on (a new alpha { the body "X" }).
            State cast pick on (a new beta { the body "Y" }).
            State cast pick on (a new gamma { the tag "Z" }).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("a:X\nb:Y\nZ", Compile(src));
    }

    [Fact]
    public void JudgeGroupedArm_DoesNotOverNarrowAfterTheArm()
    {
        // ★ The restriction must not leak. After the Judge, `thing` is the full union again, so an
        // else-arm out here still has two cases left and must NOT narrow — the bug's mirror image,
        // where a stale arm set would narrow something that is genuinely still a union.
        const string src = """
            Define object alpha with (the text body).
            Define object beta with (the text body).
            Define object gamma with (the text body).

            Bind text to pick, given (the (alpha or beta or gamma) thing):
                Judge thing, where it is:
                    An alpha or a beta:
                        If it is an alpha, return "a".
                        Otherwise, return "b".
                    Done.
                    A gamma, return "g".
                Done.
            Done.

            Bind text to recheck, given (the (alpha or beta or gamma) thing):
                If thing is an alpha, return "A".
                Otherwise, return "not-A".
            Done.

            State cast pick on (a new beta { the body "y" }).
            State cast recheck on (a new beta { the body "y" }).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("b\nnot-A", Compile(src));
    }

    // ── Line endings on stdout ───────────────────────────────────────────
    //
    // Windows opens stdout in text mode, so the C runtime rewrote every '\n' on its way out as
    // "\r\n". A '\n' the program put INSIDE a text value is data, and rewriting it made the
    // compiled backend print something the interpreter did not.
    //
    // ★ The oracle could not see it. Both runners normalised "\r\n" to "\n" before comparing, so
    // the rewritten data compared equal to the untouched data. It took a literal containing a
    // "\r\n" PAIR — which normalisation cannot flatten — for the difference to surface at all.
    // The comparison is byte-exact now; these tests pin the behaviour directly so the two halves
    // (data preserved, terminator agreed) are each named rather than implied.

    [Fact]
    public void EmbeddedNewline_IsDataAndIsNotRewritten()
    {
        // The bug, at its smallest. Compiled used to give "a\r\nb"; the interpreter gave "a\nb".
        Assert.Equal("a\nb" + Environment.NewLine, CompileRaw("State \"a\\nb\"."));
        Assert.Equal(InterpretRaw("State \"a\\nb\"."), CompileRaw("State \"a\\nb\"."));
    }

    [Fact]
    public void EmbeddedCarriageReturnNewline_SurvivesAsTyped()
    {
        // The shape that finally exposed it, because normalising cannot flatten a pair.
        Assert.Equal("a\r\nb" + Environment.NewLine, CompileRaw("State \"a\\r\\nb\"."));
        Assert.Equal(InterpretRaw("State \"a\\r\\nb\"."), CompileRaw("State \"a\\r\\nb\"."));
    }

    [Fact]
    public void StateTerminator_IsThePlatformNewline()
    {
        // The other half: the terminator must still agree with the interpreter's WriteLine.
        Assert.Equal("hi" + Environment.NewLine, CompileRaw("State \"hi\"."));
        Assert.Equal(InterpretRaw("State \"hi\"."), CompileRaw("State \"hi\"."));
    }

    [Fact]
    public void GeneratedC_UsesTheNewlineMacro()
    {
        // ★ The guard against the next one. Eleven sites printed a terminator by hand, and a new
        // `State` arm added later would silently reintroduce the bug on Windows — the per-site
        // pattern this codebase keeps getting caught by. Exercises every arm of the State switch
        // so a newly added one cannot slip past by not being covered.
        const string src = """
            Define object point with (the number x, the number y).
            Define nums as a series of number with (1, 2).
            Define pair as a record with (the a 1, the b 2).
            Define lookup as a map from text to number with ("a" : 1).
            Define spot as a new point { the x 1, the y 2 }.
            Define the (number or text) either as 1.
            Define the voidable number maybe as 7.
            State 1.
            State "text".
            State true.
            State 0x0F.
            State nums.
            State pair.
            State spot.
            State lookup.
            State either.
            State maybe.
            """;

        // The matrix arm needs its book, so it gets its own program rather than reshaping this one.
        const string matrixSrc = """
            Pull a book on collections.
                Define grid as a matrix with 2 by 2 filled with 0.
                State grid.
            Done.
            """;

        foreach (var c in new[] { GenerateC(src), GenerateC(matrixSrc) })
        {
            Assert.DoesNotContain("printf(\"\\n\")", c);
            Assert.Contains("cufet_nl()", c);
            Assert.Contains("CUFET_STDOUT_BINARY();", c);
        }
    }

    // ── Verbatim text: <<...>> ───────────────────────────────────────────
    //
    // Lexing is covered in Cufet.Lexer.Tests.RawTextTests. What is at stake HERE is that the
    // text survives the trip through C: a verbatim literal makes it easy to write characters
    // that previously reached a string only via an escape, and the emitter has to hold them.

    [Fact]
    public void RawText_Prints_MatchesInterpreter()
    {
        const string src = "State <<hello>>.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("hello", Compile(src));
    }

    [Fact]
    public void RawText_WithQuotes_MatchesInterpreter()
    {
        const string src = "State <<say \"hi\">>.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("say \"hi\"", Compile(src));
    }

    [Fact]
    public void RawText_WithBackslashes_MatchesInterpreter()
    {
        // A lone trailing backslash is the case a C emitter gets wrong if it forwards the text
        // unescaped — it would swallow the closing quote of the emitted C literal.
        const string src = @"State <<C:\Users\>>.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal(@"C:\Users\", Compile(src));
    }

    [Fact]
    public void RawText_WithBraces_MatchesInterpreter()
    {
        const string src = "State <<{\"name\": \"x\"}>>.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("{\"name\": \"x\"}", Compile(src));
    }

    [Fact]
    public void RawText_UninterpretedEscapeIsTwoCharacters_MatchesInterpreter()
    {
        // `\n` here is a backslash and an n, so this prints on ONE line. If either backend
        // interpreted it, this would print on two and the lengths would differ.
        const string src = "State the length of <<a\\nb>>.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("4", Compile(src));
    }

    [Fact]
    public void RawText_MultiLine_MatchesInterpreter()
    {
        const string src = "State <<one\ntwo>>.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("one\ntwo", Compile(src));
    }

    [Fact]
    public void RawText_Nested_MatchesInterpreter()
    {
        const string src = "State <<a <<b>> c>>.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("a <<b>> c", Compile(src));
    }

    [Fact]
    public void RawText_JoinedToAQuotedLiteral_MatchesInterpreter()
    {
        // Joining is what stands in for the interpolation this form deliberately lacks.
        const string src =
            "Define name as \"world\".\n" +
            "State <<{hello}: >> joined to name.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("{hello}: world", Compile(src));
    }

    [Fact]
    public void RawText_IsOrdinaryText_MatchesInterpreter()
    {
        // Same type, same operations — the form is a spelling, not a kind of value.
        const string src =
            "Define pattern as <<^\\d{3}$>>.\n" +
            "State the length of pattern.\n" +
            "State pattern in uppercase.\n" +
            "State pattern contains <<\\d>>.";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void RawText_InsideAnInterpolationHole_MatchesInterpreter()
    {
        const string src = "State \"[{<<{x}>>}]\".";
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("[{x}]", Compile(src));
    }
}
