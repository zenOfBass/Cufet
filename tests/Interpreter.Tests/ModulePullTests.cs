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
        // The same `as` clause `Pull a rabbit as hopper.` already had.
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
        // The `book on <name>` spelling stays — it reads better than `Pull a math.` — and nothing
        // about it changed.
        Assert.Equal("3.1415926535897932384626433833", Run("""
            Pull a book on math.
                State math's pi.
            Done.
            """));
    }

    /// <summary>
    /// ★★ A bundled book pulled by the GENERAL form — the second conformer, and the point of the
    /// whole exercise.
    /// </summary>
    /// <remarks>
    /// An interface with one conformer is a guess. This is the seam being pulled on from the other
    /// direction: `math` is implemented natively and has no `Define object` to hang an `and module`
    /// clause on, `greeting-kit` is an ordinary object written in Cufet, and `Pull` treats them the
    /// same because the contract only ever asked "may this be pulled?".
    /// </remarks>
    [Fact]
    public void ABookIsPulledByTheSameFormAsAModule()
    {
        Assert.Equal("3.1415926535897932384626433833", Run("""
            Pull math.
                State math's pi.
            Done.
            """));
    }

    [Fact]
    public void ABookPulledByNameCanBeAliasedToo()
    {
        Assert.Equal("3.1415926535897932384626433833", Run("""
            Pull math as m.
                State m's pi.
            Done.
            """));
    }

    [Fact]
    public void ABookPulledByNameStillIntroducesItsTypes()
    {
        // `collections` brings the `matrix` type name into the block. That is book-specific
        // behaviour riding on the general form, which is what "the conformer's own business" means.
        Assert.Equal("matrix((1, 3), (2, 4))", Run("""
            Pull collections.
                State cast collections's transpose on (a matrix with ((1, 2), (3, 4))).
            Done.
            """));
    }

    [Fact]
    public void AModuleAndABookCanBeUsedTogether()
    {
        Assert.Equal("hello, world\n3.1415926535897932384626433833", Run(Kit + """
            Pull greeting-kit.
                State cast greeting-kit's greet on ("world").
                Pull a book on math.
                    State math's pi.
                Done.
            Done.
            """));
    }

    // ── A book's Cufet layer (0.16.0 arc, slice 1) ─────────────────────────
    //
    // ★★ `unique` is written in Cufet (Prelude/collections.cufe) and the native copy is DELETED,
    // so these tests reach the Cufet path or nothing — which is what makes them proof. A shadowed
    // native member would answer identically and prove only that shadowing works.

    [Fact]
    public void TheCufetLayerAnswersThroughThePulledName()
    {
        // Two fillings of one Cufet method through a book alias — generic-method instantiation
        // reached through a BookType-typed binding.
        Assert.Equal("(1, 2, 3)\n(a, b)", Run("""
            Pull collections as c.
                State cast c's unique on (a series of number with (1, 2, 2, 3, 1)).
                State cast c's unique on (a series of text with ("a", "b", "a")).
            Done.
            """));
    }

    [Fact]
    public void TheNativeLayerStillAnswersThroughTheSameName()
    {
        // A member the Cufet layer does NOT define falls to the native book — the merge is member
        // by member, not book by book.
        Assert.Equal("1\nmatrix((1, 3), (2, 4))", Run("""
            Pull collections as c.
                State cast c's minimum on (a series of number with (3, 1, 2)) but void is -1.
                State cast c's transpose on (a matrix with ((1, 2), (3, 4))).
            Done.
            """));
    }

    [Fact]
    public void TheLayerIsUsableInsideAFunctionDeclaredInThePull()
    {
        // A pulled book is a lexical capability — that carried the layer with it must not regress
        // when a book's binding starts carrying an instance.
        Assert.Equal("(x, y)", Run("""
            Pull collections as c.
                Bind series of text to dedupe, given (the series of text names):
                    Return cast c's unique on (names).
                Done.
                State cast dedupe on (a series of text with ("x", "y", "x")).
            Done.
            """));
    }

    [Fact]
    public void ABundledBookNameCannotBeRedefined()
    {
        // `math` has no Cufet layer yet, so before this guard the definition was legal and simply
        // unpullable — the book shadowed it at the pull site, silently. Refusing at the definition
        // is the honest version.
        var ex = Assert.Throws<TypeException>(() => Run("""
            Define object math with () and module:
            Done.
            State "unreachable".
            """));
        Assert.Contains("is a bundled book", ex.Message);
    }

    [Fact]
    public void PullIsTheOnlyConstructorForABundledBook()
    {
        // The Cufet layer is a registered object type (the merge rides on that), so without the
        // guard `a new collections { }` would build a layer instance with no pull — no scope, no
        // `Done.`, none of what pulling means. A book is a scope-thing; construction is the
        // bracket.
        var ex = Assert.Throws<TypeException>(() => Run("""
            Define orphan as a new collections { }.
            State "unreachable".
            """));
        Assert.Contains("'Pull' is how you get one", ex.Message);
    }

    [Fact]
    public void UntoCannotAddMembersToABundledBook()
    {
        // `unto collections` would splice a writer's member straight onto the book's Cufet layer
        // — the shadowing hole the redefinition guard closes, reopened through a side door.
        var ex = Assert.Throws<TypeException>(() => Run("""
            Bind number to sneak unto collections:
                Return 0.
            Done.
            State "unreachable".
            """));
        Assert.Contains("'unto' can't add members to it", ex.Message);
    }

    [Fact]
    public void ABundledBookNameCannotBeRedefined_EvenWhenThePreludeDefinesIt()
    {
        // `collections` IS defined by the prelude — that definition is the book's Cufet layer, and
        // it is recognised by reference, so a writer's definition of the same name is still refused.
        var ex = Assert.Throws<TypeException>(() => Run("""
            Define object collections with () and module:
            Done.
            State "unreachable".
            """));
        Assert.Contains("is a bundled book", ex.Message);
    }
}
