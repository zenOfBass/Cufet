using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// `Pull &lt;module&gt;` — a writer's own object brought into scope the same way a book is.
/// </summary>
/// <remarks>
/// <para>
/// ★ The point of the feature is that the three bundled books stop being a privileged category.
/// A module is an object that says it is one, and `Pull` is how you bring it into scope; a book is
/// then simply a module that ships with the language. So the tests that matter most are the ones
/// asserting that a writer's object and a builtin behave the same at the pull site.
/// </para>
/// <para>
/// It is not new syntax. `Pull a rabbit.` was always this shape, and the article is noise — which
/// is why `Pull greeting-kit.` and `Pull a greeting-kit as kit.` are the same form with different
/// words in it.
/// </para>
/// </remarks>
public class ModulePullTests
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

    private const string Kit = """
        Define object greeting-kit with () and module:
            Bind text to greet, given (the text who):
                Return "hello, " joined to who.
            Done.
        Done.

        """;

    [Fact]
    public void AModuleIsPulledByName()
    {
        Assert.Equal("hello, world", Run(Kit + """
            Pull greeting-kit.
                State cast greeting-kit's greet on ("world").
            Done.
            """));
    }

    [Fact]
    public void TheArticleIsNoise()
    {
        // `Pull a greeting-kit.` and `Pull greeting-kit.` are the same statement — articles carry no
        // scope anywhere in Cufet, which is what lets this form read naturally for any name.
        Assert.Equal("hello, world", Run(Kit + """
            Pull a greeting-kit.
                State cast greeting-kit's greet on ("world").
            Done.
            """));
    }

    [Fact]
    public void AModuleCanBeAliased()
    {
        // The same `as` clause `Pull a rabbit as den.` already had.
        Assert.Equal("hello, aliased", Run(Kit + """
            Pull greeting-kit as kit.
                State cast kit's greet on ("aliased").
            Done.
            """));
    }

    /// <summary>
    /// A pull is a scope: the binding is gone after `Done.`
    /// </summary>
    /// <remarks>
    /// ⚠ Caught at RUNTIME, not by the checker — and that is not a module quirk. `Pull a book on
    /// math. … Done. State math's pi.` type-checks clean and fails the same way, because an
    /// unresolvable name infers as null and the checker deliberately defers rather than cascade
    /// false positives. Modules inherit the behaviour books already had, which is the consistency
    /// worth having; that the checker lets either through is a pre-existing gap, recorded here
    /// because a test asserting the wrong layer is how it stays invisible.
    /// </remarks>
    [Fact]
    public void TheBindingLeavesScopeAtDone()
    {
        var ex = Assert.Throws<RuntimeException>(() => Run(Kit + """
            Pull greeting-kit.
                State cast greeting-kit's greet on ("inside").
            Done.
            State cast greeting-kit's greet on ("outside").
            """));
        Assert.Contains("greeting-kit", ex.Message);
    }

    // ── What `module` is for ───────────────────────────────────────────────

    [Fact]
    public void AnObjectThatDoesNotSayItIsAModule_CannotBePulled()
    {
        // ★ The marker requires no METHODS, but it does require the claim. Being pullable is
        // something an author declares, not something every object turns out to have — otherwise
        // the interface would be decorative and `Pull` would accept anything with a name.
        var ex = Assert.Throws<TypeException>(() => Run("""
            Define object plain-thing with ():
                Bind text to greet, given (the text who): Return "nope". Done.
            Done.

            Pull plain-thing.
                State cast plain-thing's greet on ("x").
            Done.
            """));
        Assert.Contains("is not a module", ex.Message);
        Assert.Contains("and module", ex.Message);   // the message names the fix
    }

    [Fact]
    public void ConformingToModuleRequiresNoMethods()
    {
        // The marker is empty on purpose. An object with nothing in it at all still conforms, which
        // is the proof that `module` is not quietly demanding a shape.
        Assert.Equal("pulled", Run("""
            Define object empty-module with () and module:
                Bind text to name: Return "pulled". Done.
            Done.

            Pull empty-module.
                State cast empty-module's name on ().
            Done.
            """));
    }

    [Fact]
    public void PullingSomethingThatDoesNotExist_SaysWhatCanBePulled()
    {
        var ex = Assert.Throws<TypeException>(() => Run("""
            Pull nowhere-thing.
                State "unreachable".
            Done.
            """));
        Assert.Contains("nothing named 'nowhere-thing' to pull", ex.Message);
    }

    // ── The bundled books are unchanged ────────────────────────────────────

    [Fact]
    public void TheBuiltinBookFormStillWorks()
    {
        // `math`, `collections` and `chance` read badly without the noun (`Pull a math.`), so the
        // `book on <name>` spelling stays. Nothing about it changed.
        Assert.Equal("3.14159265358979", Run("""
            Pull a book on math.
                State math's pi.
            Done.
            """));
    }

    [Fact]
    public void AModuleAndABookCanBeUsedTogether()
    {
        Assert.Equal("hello, world\n3.14159265358979", Run(Kit + """
            Pull greeting-kit.
                State cast greeting-kit's greet on ("world").
                Pull a book on math.
                    State math's pi.
                Done.
            Done.
            """));
    }
}
