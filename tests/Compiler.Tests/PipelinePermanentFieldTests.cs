using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
///
/// `Define object user with (the text id permanently, ...)` — a field set when the object is made
/// and never written after.
///
/// Nothing else in the language expresses that invariant. A setter cannot: setters are infallible
/// and transform-only, so one guarding an id could only ignore a bad write rather than reject it,
/// which is worse than no protection at all.
public class PipelinePermanentFieldTests : PipelineTestBase
{
    [Fact]
    public void PermanentField_IsSetAtConstruction_AndReadable()
    {
        const string src = """
            Define object user with (the text id permanently, the text name).
            Define alice as a new user { the id "u-1", the name "Alice" }.
            State alice's id.
            State alice's name.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("u-1\nAlice", Interpret(src));
    }

    [Fact]
    public void NonPermanentField_OnTheSameObject_StillWrites()
    {
        // `permanently` is per-FIELD. The guarantee must not leak onto its neighbours.
        const string src = """
            Define object user with (the text id permanently, the text name).
            Define alice as a new user { the id "u-1", the name "Alice" }.
            The alice's name becomes "Alicia".
            State alice's id.
            State alice's name.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("u-1\nAlicia", Interpret(src));
    }

    [Fact]
    public void WritingAPermanentField_IsRefused()
    {
        const string src = """
            Define object user with (the text id permanently, the text name).
            Define alice as a new user { the id "u-1", the name "Alice" }.
            The alice's id becomes "u-2".
            """;
        var ex = Assert.ThrowsAny<Exception>(() => InterpretRaw(src));
        Assert.Contains("permanent", ex.Message);
    }

    [Fact]
    public void WritingAPermanentField_FromInsideItsOwnMethod_IsRefused()
    {
        // ★ `one's id becomes …` is the write the type's own author is most likely to reach for,
        // and it is the one that would quietly defeat the invariant if only external writes were
        // checked.
        const string src = """
            Define object user with (the text id permanently, the text name):
                Bind void to rename:
                    The one's id becomes "changed".
                Done.
            Done.
            Define alice as a new user { the id "u-1", the name "Alice" }.
            Cast rename on (alice).
            """;
        var ex = Assert.ThrowsAny<Exception>(() => InterpretRaw(src));
        Assert.Contains("permanent", ex.Message);
    }

    [Fact]
    public void WritingAPromotedPermanentField_ThroughAnEmbed_IsRefused()
    {
        // ★ The field belongs to the EMBEDDED type, and the write goes through the outer object,
        // whose own permanent-field set says nothing about it. Checking only the outer type would
        // make embedding a way to launder a permanent field into a mutable one.
        const string src = """
            Define object user with (the text id permanently, the text name).
            Define object admin with (the number level) and as a user.
            Define root as a new admin { the level 9, the id "u-1", the name "Root" }.
            The root's id becomes "u-2".
            """;
        var ex = Assert.ThrowsAny<Exception>(() => InterpretRaw(src));
        Assert.Contains("permanent", ex.Message);
    }

    [Fact]
    public void ReadingAPromotedPermanentField_ThroughAnEmbed_StillWorks()
    {
        // The refusal is on writes only — promotion must still READ through the chain.
        const string src = """
            Define object user with (the text id permanently, the text name).
            Define object admin with (the number level) and as a user.
            Define root as a new admin { the level 9, the id "u-1", the name "Root" }.
            State root's id.
            State root's level.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("u-1\n9", Interpret(src));
    }

    [Fact]
    public void ASetter_CannotBeUsedToWriteAPermanentField()
    {
        // ★ The refusal is checked BEFORE the setter branch. A setter is infallible and
        // transform-only, so if the write routed through one it could only be ignored, never
        // rejected — and `permanently` would mean nothing to anyone who declared a setter.
        const string src = """
            Define object user with (the text id permanently, the text name):
                Set id given (the text raw):
                    The one's name becomes raw.
                Done.
            Done.
            Define alice as a new user { the id "u-1", the name "Alice" }.
            The alice's id becomes "u-2".
            """;
        var ex = Assert.ThrowsAny<Exception>(() => InterpretRaw(src));
        Assert.Contains("permanent", ex.Message);
    }

    [Fact]
    public void PermanentField_ComposesWithAConditionalInitialiser()
    {
        // The pair this arc is about: `when` supplies the value, `permanently` fixes it. Before
        // the conditional expression there was no way to choose a permanent field's value at all.
        const string src = """
            Define object account with (the number fee permanently, the text holder).
            Define member as true.
            Define one-account as a new account { the fee 0 when member is true, otherwise 25, the holder "Ada" }.
            State one-account's fee.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("0", Interpret(src));
    }
}
