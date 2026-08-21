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
    /// ★ Caught by the CHECKER as of 2026-08-20. This note used to record the opposite — that it
    /// was a runtime failure, because an unresolvable name inferred as null and was deferred — and
    /// called it "a pre-existing gap". The gap is closed where the scope is FINAL: after `Done.`
    /// nothing can bring the binding back, so the checker refuses instead of letting the program
    /// start. A detached body still defers, since a method resolves names where it is CALLED.
    /// </remarks>
    [Fact]
    public void TheBindingLeavesScopeAtDone()
    {
        var ex = Assert.Throws<TypeException>(() => Run(Kit + """
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
        Assert.Contains("comes with the language", ex.Message);
    }

    /// <summary>
    /// Stating a module prints it as the object it is, whoever wrote it.
    /// </summary>
    /// <remarks>
    /// ⚠ A book used to print `Cufet.Interpreter.Interpreter+BookValue` — `Format` had no arm for
    /// one, so it fell through to `val.ToString()`. The compiler already printed `math()`, so this
    /// was a divergence AND a host type name shown to a reader. The same fallthrough had leaked
    /// `MatrixValue` once before, which is recorded in a comment right beside it.
    /// </remarks>
    [Fact]
    public void StatingAModulePrintsItAsAnObject()
    {
        Assert.Equal("greeting-kit()\nmath()", Run(Kit + """
            Pull greeting-kit as kit.
                State kit.
                Pull a book on math.
                    State math.
                Done.
            Done.
            """));
    }

    /// <summary>
    /// ★★ The arc's finish line: a writer's object, a rabbit and a book are all `module` VALUES.
    /// </summary>
    /// <remarks>
    /// They pass by INHERITANCE rather than by any decision made about them: a module is an
    /// object, an object is first class, so a module is first class. Nothing in the checker asks
    /// which KIND of module arrived — which is the whole of what the 0.16.0 arc was for.
    /// ⚠ A book reaching here at all took binding the pulled name at its Cufet LAYER instead of
    /// at `BookType`; a BookType is not an object, so conformance had nothing to inherit from.
    /// </remarks>
    [Fact]
    public void EveryKindOfModulePassesAsAModuleValue()
    {
        Assert.Equal("kit\nrabbit\nbook", Run(Kit + """
            Bind text to which, given (the module m, the text label): Return label. Done.
            Pull greeting-kit as kit.
                State cast which on (kit, "kit").
            Done.
            Pull a rabbit as hopper.
                State cast which on (hopper, "rabbit").
            Done.
            Pull a book on math.
                State cast which on (math, "book").
            Done.
            """));
    }

    /// <summary>
    /// `rabbit` is a module's NAME, not a reserved word — so a writer may use it for their own.
    /// </summary>
    /// <remarks>
    /// ★ No bundled module's name is reserved: `math`, `collections` and `chance` are ordinary
    /// identifiers and always were. `rabbit` was the one exception, which is precisely what made
    /// the rabbit a privileged builtin rather than a module that ships in the box. What the books
    /// reserve is grammar — `book`, `books`, `on` — never identity.
    /// </remarks>
    [Fact]
    public void RabbitIsAName_NotAReservedWord()
    {
        Assert.Equal("42", Run("""
            Define rabbit as 42.
            State rabbit.
            """));
    }

    /// <summary>
    /// `book` and `books` are ordinary words too — the line below could not be written before.
    /// </summary>
    /// <remarks>
    /// ★ They appear in exactly ONE spelling, `Pull a book on <name>.`, and a word spent on a
    /// single construct is a name every writer loses forever. The `on` is what makes the word
    /// decidable without reserving it: `Pull a book on math.` and `Pull book.` differ in their
    /// second token. Same move as `rabbit` — recognise a word where it does a job rather than
    /// take it away everywhere.
    /// </remarks>
    [Fact]
    public void BookAndBooksAreNames_NotReservedWords()
    {
        Assert.Equal("Dune\nEmma", Run("""
            Define books as a series of text with ("Dune", "Emma").
            For each book in books, repeat:
                State book.
            Done.
            """));
    }

    [Fact]
    public void AModuleMayBeNamedBook()
    {
        // The reading that has to survive: `Pull book.` reaches a module actually called `book`,
        // while `Pull a book on math.` still opens the bundled one. One token apart.
        Assert.Equal("mine\nmine", Run("""
            Define object book with () and module:
                Bind text to title: Return "mine". Done.
            Done.
            Pull book.
                State cast book's title on ().
            Done.
            Pull book as tome.
                State cast tome's title on ().
            Done.
            """));
    }

    [Fact]
    public void BothBookSpellingsStillRead()
    {
        Assert.Equal("3\n3.1415926535897932384626433833", Run("""
            Pull a book on math.
                State math's floor of (3.7).
            Done.
            Pull books on math as m, and collections as c.
                State m's pi.
            Done.
            """));
    }

    [Fact]
    public void EveryRabbitFormStillReads_AfterUnreserving()
    {
        // The general form `Pull <name> [as <alias>]` now reaches a rabbit the same way it
        // reaches a book — `Pull rabbit.` is new, and it is the point of un-reserving the word.
        // The bare `Have rabbit …` still addresses the enclosing one.
        Assert.Equal("inner\nouter", Run("""
            Pull rabbit.
                State "inner".
            Done.
            Pull rabbit as hopper.
                Have rabbit start a task as job, return "outer".
                State the awaited result of job.
            Done.
            """));
    }

    /// <summary>
    /// A rabbit is never compared — the question is refused rather than answered.
    /// </summary>
    /// <remarks>
    /// Decided 2026-08-19. A rabbit denotes a region with a lifetime, not a value, so there is no
    /// sense in which two of them are the same one. Refusing makes no claim and can become an
    /// answer the day something needs to tell rabbits apart; answering could not be taken back.
    /// ⚠ Refused in the shared front end, so BOTH backends refuse — it used to type-check,
    /// interpret to `false`, and emit C that gcc rejected.
    /// </remarks>
    [Fact]
    public void ARabbitIsNeverCompared()
    {
        var ex = Assert.Throws<TypeException>(() => Run("""
            Pull a rabbit as hopper.
                Pull a rabbit as grace.
                    State hopper is grace.
                Done.
            Done.
            """));
        Assert.Contains("can't be compared", ex.Message);
    }

    [Fact]
    public void ARabbitIsNotComparedToItselfEither()
    {
        // Reflexivity is not a loophole: the refusal is about the KIND of thing a rabbit is, so
        // `hopper is hopper` is refused too rather than quietly answering true.
        var ex = Assert.Throws<TypeException>(() => Run("""
            Pull a rabbit as hopper.
                State hopper is hopper.
            Done.
            """));
        Assert.Contains("can't be compared", ex.Message);
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
        Assert.Contains("comes with the language", ex.Message);
    }

    // ── A module's dependencies, checked at the pull ───────────────────────
    //
    // A module resolves names in the block it is USED in, so what it reaches for without defining
    // is a requirement on whoever pulls it. Forgetting one used to give three answers to one
    // program: `check` said nothing, the interpreter died pointing INSIDE the module, and the
    // compiler blamed itself. These pin the one answer.

    private const string GeometryNeedingMath = """
        Define object geometry with () and module:
            Bind number to circle-area, given (the number radius):
                Return math's pi * radius * radius.
            Done.
        Done.

        """;

    [Fact]
    public void AMissingDependency_IsRefusedAtThePull()
    {
        var ex = Assert.Throws<TypeException>(() => Run(GeometryNeedingMath + """
            Pull geometry.
                State cast geometry's circle-area on (2).
            Done.
            """));
        Assert.Contains("'geometry' uses 'math', which isn't pulled here", ex.Message);
        Assert.Contains("Pull books on math, and geometry.", ex.Message);
    }

    [Fact]
    public void ADependencyPulledAlongside_Satisfies()
    {
        Assert.Equal("12.566370614359172953850573533", Run(GeometryNeedingMath + """
            Pull books on math, and geometry.
                State cast geometry's circle-area on (2).
            Done.
            """));
    }

    [Fact]
    public void AModuleDefinedAfterThePull_StillHasItsNeedsChecked()
    {
        // ⚠ REGRESSION. The check ran AT the pull, so it could only see modules already checked —
        // and a module written after the block that pulls it had not been. The identical program
        // passed or failed on definition ORDER: this order checked clean and died at run time,
        // advising `Define math as <value>` for something you pull. Verification is deferred to
        // the end of checking now, when every module's needs are known.
        var ex = Assert.Throws<TypeException>(() => Run("""
            Pull geometry.
                State cast geometry's circle-area on (2).
            Done.

            Define object geometry with () and module:
                Bind number to circle-area, given (the number radius):
                    Return math's pi * radius * radius.
                Done.
            Done.
            """));
        Assert.Contains("'geometry' uses 'math', which isn't pulled here", ex.Message);
    }

    [Fact]
    public void ADefineAfterThePull_DoesNotSatisfyTheDependency()
    {
        // ★ Why the visible names are SNAPSHOT at the pull rather than read from the live scope
        // when the check finally runs: by then `factor` exists, but it did not exist at the pull,
        // and the pulled module's body would have run without it.
        var ex = Assert.Throws<TypeException>(() => Run("""
            Define object helper-kit with () and module:
                Bind number to doubled, given (the number x):
                    Return x * factor.
                Done.
            Done.

            Pull helper-kit.
                State cast helper-kit's doubled on (5).
            Done.
            Define factor as 2.
            """));
        Assert.Contains("'helper-kit' uses 'factor'", ex.Message);
    }
}
