using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>One slice of the pipeline oracle suite — see PipelineTestBase for why it is split.</summary>
///
/// An interface used to be signatures only. `Bind &lt;type&gt; to &lt;name&gt; unto &lt;interface&gt;` now gives it a
/// DEFAULT body, which is most of what traits buy — and it was a static error until this slice, so
/// nothing that already parsed changes meaning.
///
/// ★ The reason these are pipeline tests rather than interpreter tests is the monomorphization
/// claim. Interface polymorphism lives at exactly one position — the function parameter — and the
/// argument is always a concrete conformer at the call site, so a default has a concrete receiver
/// every time and specialises per conformer. If that were wrong, the COMPILED side is where it
/// would show, because it has no vtable to fall back on. Every case here asserts the two backends
/// agree before it asserts anything else.
///
/// ★ The whole feature is a parser expansion (see InterfaceDefaults): each default becomes one
/// ordinary `unto` method on each conformer that lacks it. Nothing downstream knows it exists.
public class PipelineInterfaceDefaultTests : PipelineTestBase
{
    [Fact]
    public void ADefault_SatisfiesConformance()
    {
        // ★ The settled rule, and the one that changes what an interface MEANS: `pigeon` claims
        // `speaker` and writes no `speak` anywhere, and that is legal now. An interface's method
        // list is what a conformer ends up with, not what it must write.
        const string src = """
            Define speaker as an interface for the text function speak.

            Bind text to speak unto speaker:
                Return "...".
            Done.

            Define object pigeon with (the text name) and speaker.

            Define p as a new pigeon { the name "Reg" }.
            State cast speak on p.
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("...", Interpret(src));
    }

    [Fact]
    public void AConformersOwnMethod_BeatsTheDefault()
    {
        // Specialisation. `crow` writes its own, `pigeon` does not — one interface, two behaviours,
        // and the compiled side has to reach a different body per conformer.
        const string src = """
            Define speaker as an interface for the text function speak.

            Bind text to speak unto speaker:
                Return "...".
            Done.

            Define object pigeon with (the text name) and speaker.
            Define object crow with (the text name) and speaker.

            Bind text to speak unto crow:
                Return "caw".
            Done.

            Bind void to say-it, given (the speaker s):
                State cast speak on s.
            Done.

            Cast say-it on (a new pigeon { the name "Reg" }).
            Cast say-it on (a new crow { the name "Vera" }).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("...\ncaw", Interpret(src));
    }

    [Fact]
    public void ADefault_CanCallTheRestOfTheContract()
    {
        // ★ The point of defaults: a body written against the contract, specialised per conformer.
        // `describe` is written once and reaches each type's own `area`. Note `cast area on one`
        // rather than `one's area` — the possessive yields the method, it does not call it.
        const string src = """
            Define shape as an interface for {
                The number function area,
                The text function describe
            }.

            Bind text to describe unto shape:
                Return "area {(cast area on one) converted to text}".
            Done.

            Define object square with (the number side) and shape.
            Bind number to area unto square:
                Return one's side * one's side.
            Done.

            Define object oblong with (the number wide, the number tall) and shape.
            Bind number to area unto oblong:
                Return one's wide * one's tall.
            Done.

            Bind void to show, given (the shape s):
                State cast describe on s.
            Done.

            Cast show on (a new square { the side 5 }).
            Cast show on (a new oblong { the wide 3, the tall 4 }).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("area 25\narea 12", Interpret(src));
    }

    [Fact]
    public void ADefault_TakesParameters()
    {
        const string src = """
            Define greeter as an interface for the text function greet, given (the text who).

            Bind text to greet unto greeter, given (the text who):
                Return "hello {who}".
            Done.

            Define object host with (the text name) and greeter.

            Define h as a new host { the name "Reg" }.
            State cast greet on (h, "world").
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("hello world", Interpret(src));
    }

    [Fact]
    public void ADefault_SeesTheConformersOwnFields()
    {
        // ★ `one` in a default is not "the interface" — there is no such value. It is whichever
        // conformer the copy was made for, so a default reaches fields the interface never
        // mentioned. This is the concrete-receiver property monomorphization depends on.
        const string src = """
            Define named as an interface for the text function label.

            Bind text to label unto named:
                Return "<{one's name}>".
            Done.

            Define object pigeon with (the text name) and named.
            Define object street with (the text name, the number house) and named.

            State cast label on (a new pigeon { the name "Reg" }).
            State cast label on (a new street { the name "Elm", the house 12 }).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("<Reg>\n<Elm>", Interpret(src));
    }

    [Fact]
    public void TwoInterfacesSupplyingTheSameDefault_AreRefused()
    {
        // ★ The settled conflict rule. Injecting both would collide anyway, but on "already has a
        // method", which explains the symptom and not the cause — so it is caught here, naming both
        // interfaces, and pointing at the fix (give the type its own, which beats both).
        const string src = """
            Define alpha as an interface for the text function tag.
            Define beta as an interface for the text function tag.

            Bind text to tag unto alpha:
                Return "a".
            Done.

            Bind text to tag unto beta:
                Return "b".
            Done.

            Define object thing with (the text name) and alpha and beta.

            State cast tag on (a new thing { the name "x" }).
            """;
        var ex = Assert.Throws<ParseException>(() => InterpretRaw(src));
        Assert.Contains("'alpha'", ex.Message);
        Assert.Contains("'beta'", ex.Message);
    }

    [Fact]
    public void TwoInterfacesCollide_ButTheTypeWritesItsOwn_IsFine()
    {
        // The control on the rule above: the conflict is only a conflict when nothing resolves it.
        // A type's own method beats every default, so there is nothing left to be ambiguous about
        // and neither default is injected.
        const string src = """
            Define alpha as an interface for the text function tag.
            Define beta as an interface for the text function tag.

            Bind text to tag unto alpha:
                Return "a".
            Done.

            Bind text to tag unto beta:
                Return "b".
            Done.

            Define object thing with (the text name) and alpha and beta.

            Bind text to tag unto thing:
                Return "mine".
            Done.

            State cast tag on (a new thing { the name "x" }).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("mine", Interpret(src));
    }

    [Fact]
    public void AMethodPromotedByEmbedding_BeatsTheDefault()
    {
        // ★ Specialisation has to agree with conformance about what a type "has", and conformance
        // counts a method promoted through an embedded type. If the expansion looked only at the
        // type's own body it would inject over the embedded one and silently change behaviour.
        const string src = """
            Define speaker as an interface for the text function speak.

            Bind text to speak unto speaker:
                Return "default".
            Done.

            Define object voice with (the text tone):
                Bind text to speak:
                    Return "embedded {one's tone}".
                Done.
            Done.

            Define object parrot with (the text nickname) and speaker and as a voice.

            State cast speak on (a new parrot { the nickname "Polly", the tone "loud" }).
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("embedded loud", Interpret(src));
    }

    [Fact]
    public void ADefaultNoOneConformsTo_IsHarmless()
    {
        // A contract's fallback is not a program on its own. Rust is the same: a trait with a
        // default body and no impl anywhere is legal and emits nothing.
        const string src = """
            Define speaker as an interface for the text function speak.

            Bind text to speak unto speaker:
                Return "never runs".
            Done.

            State "still here".
            """;
        Assert.Equal(InterpretRaw(src), CompileRaw(src));
        Assert.Equal("still here", Interpret(src));
    }
}
