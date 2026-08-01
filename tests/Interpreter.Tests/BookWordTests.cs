using Cufet.Interpreter;
using Cufet.Lexer;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Book vocabulary as contextual identifiers.
//
// A reserved word is taken from EVERY program in the language, whether or not it pulls the book
// that wanted it. Reserving `guess` for the chance book means no program anywhere can have a
// variable called `guess` — and the cost compounds with each book added. So book words are
// recognised by SHAPE in the one position that needs them, and are ordinary names everywhere
// else, exactly like the ordinal words and the I/O form words.
//
// Deliberately not scope-aware: the word is recognised in its shape whether or not the book was
// pulled, and using the feature without the book is a type error — which it already was.
public class BookWordTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var output  = new StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    // ── The words are names again ────────────────────────────────────────

    // The nine words the standard library used to take from every program.
    [Theory]
    [InlineData("at")]
    [InlineData("filled")]
    [InlineData("guess")]
    [InlineData("shuffled")]
    [InlineData("rows")]
    [InlineData("columns")]
    [InlineData("matrix")]
    [InlineData("random")]
    [InlineData("randomly")]
    public void BookWord_UsableAsAVariableName(string word)
        => Assert.Equal("5", Run($"Define {word} as 5. State {word}."));

    [Theory]
    [InlineData("at")]
    [InlineData("filled")]
    [InlineData("guess")]
    [InlineData("shuffled")]
    [InlineData("rows")]
    [InlineData("columns")]
    [InlineData("matrix")]
    [InlineData("random")]
    [InlineData("randomly")]
    public void BookWord_UsableAsAParameterName(string word)
        => Assert.Equal("7", Run(
            $"Bind number to echo, given (the number {word}): Return {word}. Done. " +
            $"State cast echo on (7)."));

    [Theory]
    [InlineData("at")]
    [InlineData("filled")]
    [InlineData("guess")]
    [InlineData("shuffled")]
    [InlineData("rows")]
    [InlineData("columns")]
    [InlineData("matrix")]
    [InlineData("random")]
    [InlineData("randomly")]
    public void BookWord_UsableAsAFieldName(string word)
        => Assert.Equal("3", Run(
            $"Define r as a record with (the {word} 3). State the {word} of r."));

    [Fact]
    public void BookWord_RowsAndColumnsAsParameters()
        => Assert.Equal("12", Run(
            "Bind number to area, given (the number rows, the number columns): " +
            "Return rows * columns. Done. State cast area on (3, 4)."));

    [Fact]
    public void BookWord_UsableAsAnIteratorName()
        => Assert.Equal("1\n2", Run(
            "Define xs as a series with (1, 2). For each guess in xs, repeat: State guess. Done."));

    [Fact]
    public void BookWord_SeveralAtOnce()
        => Assert.Equal("45", Run(
            "Define at as 1. Define filled as 2. Define guess as 3. Define shuffled as 4. " +
            "Define rows as 5. Define columns as 6. Define matrix as 7. Define random as 8. " +
            "Define randomly as 9. " +
            "State at + filled + guess + shuffled + rows + columns + matrix + random + randomly."));

    // ── `the rows of x` means two different things, and both are right ───
    // The parser cannot tell them apart — it does not know x's type — so the resolution lives
    // in the type checker, the same way `the key of mapping` already resolves. A human reader
    // never has the ambiguity in the first place.

    [Fact]
    public void RowsAndColumns_ResolveByTheTypeOfTheTarget()
    {
        Assert.Equal("2\n3", Run(
            "Pull a book on collections. " +
            "Define m as a matrix with ((1, 2, 3), (4, 5, 6)). " +
            "State the rows of m. State the columns of m. Done."));

        Assert.Equal("10\n4", Run(
            "Define table as a record with (the rows 10, the columns 4). " +
            "State the rows of table. State the columns of table."));
    }

    [Fact]
    public void Matrix_LiteralStillParsesWhenMatrixIsAlsoAVariable()
    {
        // 'a matrix with (...)' is recognised by the mandatory 'with'; a bare `matrix` is a name.
        Assert.Equal("1", Run(
            "Pull a book on collections. Define matrix as 99. " +
            "Define m as a matrix with ((1, 2), (3, 4)). State the item at (1, 1) of m. Done."));
    }

    [Fact]
    public void Random_ShapeNeedsItsFollowingWord()
    {
        // 'a random number/item/guess' — the next word is mandatory, which is what lets a
        // variable called `random` still be a variable.
        Assert.Equal("8", Run("Define random as 8. State random."));
        Assert.Equal("true", Run(
            "Pull a book on chance. Define g as a random guess. " +
            "State g is true or g is false. Done."));
    }

    // ── 'seed' is contextual too ─────────────────────────────────────────
    //
    // It was the one piece of chance vocabulary held back, because `Seed the chance with <n>.` is
    // written capitalised and an identifier must start lowercase. Once a contextual statement word
    // could be capitalised, that reason went away — and `seed` is a name worth having back, since
    // the code most likely to want it is exactly the code that pulls this book.

    [Fact]
    public void Seed_IsUsableAsAVariable()
    {
        Assert.Equal("43", Run("Define seed as 42. seed becomes 43. State seed."));
    }

    [Fact]
    public void Seed_TheStatementAndAVariableOfThatNameCoexist()
    {
        // The statement is recognised by the word 'chance' following, so the two never collide.
        Assert.Equal("7", Run(
            "Pull a book on chance. Define seed as 7. Seed the chance with seed. State seed. Done."));
    }

    [Fact]
    public void Seed_TheStatementMayBeLowercase()
    {
        Assert.Equal("ok", Run(
            "Pull a book on chance. seed the chance with 42. State \"ok\". Done."));
    }

    [Fact]
    public void Seed_WithoutPullingTheBook_StillSaysSo()
    {
        // Making the word contextual is a parser change; the book requirement is the checker's,
        // and its message has to survive untouched.
        var ex = Assert.Throws<TypeException>(() => Run("Seed the chance with 42."));
        Assert.Contains("chance book is not in scope", ex.Message);
    }

    [Fact]
    public void Seed_TheStatementItselfStillWorks()
    {
        Assert.Equal("true", Run(
            "Pull a book on chance. Seed the chance with 42. " +
            "Define g as a random guess. State g is true or g is false. Done."));
    }

    [Fact]
    public void CatalogueAndAtlas_StayReservedBecauseTheirTailsAreOptional()
    {
        // 'a catalogue' and 'an atlas' are complete on their own, so there is no mandatory
        // following word to tell them apart from a variable of the same name. A word can only
        // go contextual when its shape has a mandatory distinguishing token.
        Assert.Throws<ParseException>(() => Run("Define catalogue as 1."));
        Assert.Throws<ParseException>(() => Run("Define atlas as 1."));
    }

    // ── And the shapes that need them still work ─────────────────────────

    [Fact]
    public void Matrix_ItemAt_StillWorks()
        => Assert.Equal("2", Run(
            "Pull a book on collections. " +
            "Define m as a matrix with ((1, 2), (3, 4)). State the item at (1, 2) of m. Done."));

    [Fact]
    public void Matrix_FilledWith_StillWorks()
        => Assert.Equal("7", Run(
            "Pull a book on collections. " +
            "Define z as a matrix with 2 by 2 filled with 7. State the item at (2, 2) of z. Done."));

    [Fact]
    public void Chance_RandomGuess_StillWorks()
        => Assert.Equal("true", Run(
            "Pull a book on chance. Define g as a random guess. State g is true or g is false. Done."));

    [Fact]
    public void Chance_RandomlyShuffled_StillWorks()
        => Assert.Equal("3", Run(
            "Pull a book on chance. Define xs as a series with (1, 2, 3). " +
            "Define s as randomly shuffled xs. State the number of s. Done."));

    // ── The shape wins over the name, in the shape's own position ────────

    [Fact]
    public void Matrix_ShapeWinsEvenWhenTheWordIsAlsoAVariable()
    {
        // A local named 'at' does not stop 'the item at (r, c) of m' from parsing as indexing:
        // the word is recognised by position, and that position cannot hold a variable.
        Assert.Equal("4", Run(
            "Pull a book on collections. " +
            "Define at as 99. Define m as a matrix with ((1, 2), (3, 4)). " +
            "State the item at (2, 2) of m. Done."));
    }
}
