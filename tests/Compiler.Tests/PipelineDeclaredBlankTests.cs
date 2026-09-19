using Cufet.Interpreter;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `Bind void to swallow of thing, given (the thing value)` — a function DECLARES its blanks,
/// the way an object always could.
/// </summary>
/// <remarks>
/// <para>
/// ★★ THE HOLE THIS FILLS. A function's blanks were INFERRED by the twice rule — an unknown type
/// name used at least twice in the signature. That heuristic stands in for the declaration slot
/// objects have (`Define object stack of element`), and it has a gap exactly the shape of a
/// function taking one value of any type and giving nothing back: its blank appears once, so the
/// checker read it as a misspelled type and refused. `State` is that shape and is a statement;
/// nothing a person could write was.
/// </para>
/// <para>
/// ⚠ The twice rule is NOT relaxed, and must not be — with one use you cannot tell a blank from a
/// typo, and silently turning `the nubmer n` into a generic is the error class this language
/// refuses everywhere. Declaring turns the inference off for that one signature; everything
/// undeclared means exactly what it did.
/// </para>
/// </remarks>
public class PipelineDeclaredBlankTests : PipelineTestBase
{
    [Fact]
    public void DeclaredBlank_UsedOnce_IsNowWritable()
    {
        // ★ The shape that had no spelling: one value of any type, nothing given back.
        const string src = """
            Bind void to swallow of thing, given (the thing value):
                State "swallowed one".
            Done.
            Cast swallow on (5).
            Cast swallow on ("text").
            Cast swallow on (true).
            """;
        Assert.Equal("swallowed one\nswallowed one\nswallowed one\n".ReplaceLineEndings(),
                     InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void DeclaredBlank_OnAMethodOfANonGenericObject()
    {
        // ★★ THIS IS THE SHAPE `bury` NEEDS. A rabbit has no `of` slot and must not grow one —
        // `rabbit of number` would mean one rabbit could only ever bury one type. The blank has to
        // belong to the METHOD, and be filled per call.
        const string src = """
            Define object sink with ():
                Bind void to swallow of thing, given (the thing value):
                    State "sank one".
                Done.
            Done.
            Define s as a new sink { }.
            Cast s's swallow on (5).
            Cast s's swallow on ("text").
            """;
        Assert.Equal("sank one\nsank one\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void TheTwiceRule_StillWorksWithNoDeclaration()
    {
        // ⚠ The regression guard. Every generic function written before this slice declares
        // nothing and must keep meaning what it meant.
        const string src = """
            Bind thing to echo-it, given (the thing value):
                Return value.
            Done.
            State cast echo-it on (5).
            State cast echo-it on ("text").
            """;
        Assert.Equal("5\ntext\n".ReplaceLineEndings(), InterpretRaw(src).ReplaceLineEndings());
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }

    [Fact]
    public void DeclaredBlank_ThatShadowsARealType_IsRefused()
    {
        // ⚠ Allowing this would make `of widget` quietly turn every `widget` in the signature into
        // a blank. The signature would still check, and every call site would infer something the
        // writer never meant.
        var ex = Assert.Throws<TypeException>(() => InterpretRaw("""
            Define object widget with (the number weight).
            Bind void to swallow of widget, given (the widget value): State "x". Done.
            Cast swallow on (a new widget { the weight 1 }).
            """));
        Assert.Contains("'widget' is already a type", ex.Message);
    }

    [Fact]
    public void DeclaredBlank_NeverUsed_IsRefused()
    {
        // A blank is filled from where it appears, so one appearing nowhere can never be filled —
        // it is a typo in the signature or a leftover, and both read as "generic" to a skimmer.
        var ex = Assert.Throws<TypeException>(() => InterpretRaw("""
            Bind void to swallow of thing, given (the number value): State "x". Done.
            Cast swallow on (5).
            """));
        Assert.Contains("leaves 'thing' blank but never uses it", ex.Message);
    }

    [Fact]
    public void AFilling_DoesNotInheritTheTemplatesBlankDeclaration()
    {
        // ★ MEASURED as a real bug before the fix: `FillFunction` substitutes the blanks away, so a
        // filling that still carried `of thing` claimed to leave a blank it no longer mentions —
        // and tripped the "never used" refusal on a perfectly correct program. The template is
        // generic; its fillings are not, and nothing downstream should be able to tell they came
        // from one.
        const string src = """
            Bind void to swallow of thing, given (the thing value):
                State "one".
            Done.
            Cast swallow on (5).
            Cast swallow on (6).
            Cast swallow on ("a").
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
    }
}
