using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// `Define cufet &lt;name&gt; as [ … ].` and `Cite &lt;name&gt;.` — Cufet source held under a name, and placed.
/// </summary>
/// <remarks>
/// ★★ The same surface as a foreign axiom, with a different mechanism behind it. `[ … ]` says the
/// text inside is not the program around it, and that is true of a cufet block too — it is parsed,
/// but it is not PLACED until a `Cite` says where. Everything past parsing differs: nothing is
/// marshalled, no boundary is crossed, and no compiler but this one ever reads it.
///
/// ★ What a block holds are DECLARATIONS. An object and an interface are both hoisted to the
/// program wherever they are written, and both are checked in a scope of their own — so a cited one
/// cannot see a local at the site that cited it, and the question of what a free name inside a
/// block means never arises.
/// </remarks>
public class CufetAxiomTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var parsed  = new Parser(tokens).Parse();
        var program = new TypeChecker().Check(parsed);   // ★ the PLACED one, not `parsed`
        var output  = new System.IO.StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static TypeException Refused(string source) =>
        Assert.Throws<TypeException>(() => Run(source));

    // ── Placing what a block holds ────────────────────────────────────────────

    [Fact]
    public void ACitedObject_IsUsableWhereItIsCited()
    {
        Assert.Equal("25", Run("""
            Pull a book on cufet.
                Define cufet vector-shape as [
                    Define object vec2 with (the number x, the number y):
                        Bind number to length-squared:
                            Return one's x * one's x + one's y * one's y.
                        Done.
                    Done.
                ].

                Cite vector-shape.

                Define the arrow as a new vec2 { the x 3, the y 4 }.
                State cast length-squared on (the arrow).
            Done.
            """));
    }

    [Fact]
    public void ACitedInterface_IsUsableWhereItIsCited()
    {
        // The other declaration a block may hold. Kept beside the object so that widening the list
        // later has to answer for both.
        //
        // ⚠ An interface DEFAULT (`Bind text to speak unto speaker`) written outside the block does
        // not yet find a cited interface: defaults are expanded by the parser, in Parse(), which
        // runs before anything is cited. A conformer writing its own method — this — works. Left
        // as it is rather than moved, because moving that pass is a change to how every interface
        // in the language is built, and it deserves its own slice.
        Assert.Equal("4", Run("""
            Pull a book on cufet.
                Define cufet shapes as [
                    Define measured as an interface for the number function sized.
                ].

                Cite shapes.

                Define object square with (the number side) and measured:
                    Bind number to sized: Return one's side. Done.
                Done.

                Define the box as a new square { the side 4 }.
                State cast sized on the box.
            Done.
            """));
    }

    [Fact]
    public void ACitedObject_FromInsideAFunction_BelongsToTheProgram()
    {
        // ★★ Q3a holding, with nothing here saying it. A TYPE declaration belongs to the program
        // wherever it is written, so splicing INLINE at the cite site is all it takes — the object
        // is usable after the function that cited it, without a rule of its own.
        Assert.Equal("7\n2", Run("""
            Pull a book on cufet.
                Define cufet shape as [
                    Define object vec2 with (the number x, the number y):
                        Bind number to sum: Return one's x + one's y. Done.
                    Done.
                ].

                Bind number to made:
                    Cite shape.
                    Define the here as a new vec2 { the x 2, the y 5 }.
                    Return cast sum on (the here).
                Done.

                State cast made on ().
                Define the outside as a new vec2 { the x 1, the y 1 }.
                State cast sum on (the outside).
            Done.
            """));
    }

    [Fact]
    public void ABlockMayBeCitedBeforeItIsDeclared()
    {
        // Every block is gathered before any is placed, so a `Cite` reads like the declaration it
        // is rather than like an order of operations.
        Assert.Equal("8", Run("""
            Pull a book on cufet.
                Cite shape.
                Define the here as a new vec2 { the x 4 }.
                State cast doubled on (the here).

                Define cufet shape as [
                    Define object vec2 with (the number x):
                        Bind number to doubled: Return one's x * 2. Done.
                    Done.
                ].
            Done.
            """));
    }

    [Fact]
    public void AnUncitedBlock_PlacesNothing()
    {
        // ★★ The whole point of there being a `Cite` at all. Holding source under a name declares
        // nothing — if it did, the placement keyword would have no work left to do. Guarded by the
        // one exception in AstSearch, which is the walk every hoist goes through.
        var error = Refused("""
            Pull a book on cufet.
                Define cufet shape as [
                    Define object vec2 with (the number x): Done.
                ].
                Define the here as a new vec2 { the x 1 }.
            Done.
            """);

        Assert.Contains("'vec2' is not a defined object type", error.Message);
    }

    [Fact]
    public void ABlockName_IsNotAValue()
    {
        // A block holds source, not a thing to read. Nothing binds the name.
        Assert.Contains("shape", Refused("""
            Pull a book on cufet.
                Define cufet shape as [
                    Define object vec2 with (the number x): Done.
                ].
                State shape.
            Done.
            """).Message);
    }

    // ── Refusals ──────────────────────────────────────────────────────────────

    [Fact]
    public void ABlockWithoutThePull_IsRefused()
    {
        var error = Refused("""
            Define cufet shape as [
                Define object vec2 with (the number x): Done.
            ].
            Cite shape.
            """);

        Assert.Contains("the cufet book is not in scope", error.Message);
        // ⚠ `Pull a book on cufet.`, with no article. `cufet` is a name and refuses one where
        // `the c-language` is a common noun and takes one — and a suggestion a reader is meant to
        // copy has to be a line they would have written themselves.
        Assert.Contains("Pull a book on cufet.", error.Message);
    }

    [Fact]
    public void CitingANameThatHoldsNoBlock_IsRefused()
    {
        var error = Refused("""
            Pull a book on cufet.
                Cite nothing-here.
            Done.
            """);

        Assert.Contains("there is no cufet source called 'nothing-here' to cite", error.Message);
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void TwoBlocksUnderOneName_AreRefused()
    {
        // ⚠ Refused rather than shadowed, and it is the one redeclaration in this language that is.
        // Every other kind holds a VALUE or a TYPE and has an answer already — `Define a shadow`,
        // or last-wins. This name holds source waiting to be placed, and two under it would leave
        // every `Cite` of it ambiguous at a glance.
        var error = Refused("""
            Pull a book on cufet.
                Define cufet shape as [ Define object vec2 with (the number x): Done. ].
                Define cufet shape as [ Define object vec3 with (the number x): Done. ].
                Cite shape.
            Done.
            """);

        Assert.Contains("there is already cufet source called 'shape', on line 2", error.Message);
        Assert.Equal(3, error.Line);
    }

    [Fact]
    public void ABlockHoldingSomethingOtherThanADeclaration_IsRefused()
    {
        var error = Refused("""
            Pull a book on cufet.
                Define cufet greeting as [ State "hello". ].
                Cite greeting.
            Done.
            """);

        Assert.Contains("cufet source holds declarations, and this is not one", error.Message);
    }

    [Fact]
    public void AnUncitedBlockHoldingAStatement_IsStillRefused()
    {
        // ⚠ Every block is checked, cited or not. Finding out that source could never be placed at
        // the moment someone first cites it would be a message about the wrong line.
        Assert.Contains("holds declarations", Refused("""
            Pull a book on cufet.
                Define cufet greeting as [ State "hello". ].
                State "elsewhere".
            Done.
            """).Message);
    }

    [Theory]
    [InlineData("Define cufet number two as [ 2 ].")]
    [InlineData("Define cufet shape, given (the number n), as [ Define object vec2 with (the number x): Done. ].")]
    public void ACufetBlockWrittenAsSomethingToRun_IsRefused(string declaration)
    {
        // The shapes that mean "run me" — a result type, a `given` clause, a release clause. The
        // parser turns every OTHER cufet block into a block; these fall through to the ordinary
        // axiom path, where the refusal has the machinery to explain itself.
        var error = Refused($"""
            Pull a book on cufet.
                {declaration}
                State "ok".
            Done.
            """);

        Assert.Contains("is Cufet source, which is cited rather than run", error.Message);
    }

    // ── Where a message from inside a block points ────────────────────────────
    //
    // ★★ A lexer starts at line 1 and a block held inside another file does not, so parsed on its
    // own every message from inside one points at a line of nowhere — worse than no message,
    // because it reads like a real one. These three are the whole reason the lexer learned what a
    // fragment is.

    [Fact]
    public void ATypeErrorInsideABlock_ReportsTheLineInTheOuterFile()
    {
        var error = Refused("""
            Pull a book on cufet.
                Define cufet shape as [
                    Define object vec2 with (the number x):
                        Bind number to broken:
                            Return one's nope.
                        Done.
                    Done.
                ].
                Cite shape.
            Done.
            """);

        Assert.Equal(5, error.Line);     // `Return one's nope.` — counted in the file, not the block
        Assert.Contains("'nope'", error.Message);
    }

    [Fact]
    public void ASyntaxErrorInsideABlock_ReportsTheLineInTheOuterFile()
    {
        var error = Assert.Throws<ParseException>(() => Run("""
            Pull a book on cufet.
                Define cufet shape as [
                    Define object vec2 with (the number x):
                        Bind number to shown Return one's x.
                    Done.
                ].
                Cite shape.
            Done.
            """));

        Assert.Equal(4, error.Line);
    }

    [Fact]
    public void AnUnlexableCharacterInsideABlock_ReportsLineAndColumnInTheOuterFile()
    {
        // ⚠ The COLUMN is what the line alone would have hidden: a block's first line is pushed
        // right by whatever preceded it there, and every later line is not.
        var error = Assert.Throws<Cufet.Lexer.LexerException>(() => Run("""
            Pull a book on cufet.
                Define cufet shape as [
                    Define object vec2 with (the number x):
                        Bind number to shown:
                            Return one's x @ 3.
                        Done.
                    Done.
                ].
                Cite shape.
            Done.
            """));

        Assert.Equal(5, error.Line);
        Assert.Equal(32, error.Column);
        Assert.Contains("'@'", error.Message);
    }
}
