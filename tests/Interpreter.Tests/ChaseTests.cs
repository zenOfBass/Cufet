using Cufet.Interpreter;
using Cufet.Lexer;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// `chase` — the mutable character buffer the `collections` book introduces.
/// </summary>
/// <remarks>
/// <para>
/// ★★ MUTABILITY is the whole distinction, not byte access. `text` is an immutable value, so
/// building one a piece at a time is quadratic — every join rebuilds the whole string. This is the
/// thing you build in, crossed to `text` once at the end.
/// </para>
/// <para>
/// ★ It follows COLLECTION conventions rather than text ones, and the tests below are written to
/// hold that line: `Insert`, `the number of`, and printing that looks like a collection. The moment
/// it grows a parallel copy of text's API the split stops meaning anything, and a reader is back to
/// asking which of the two they are holding.
/// </para>
/// </remarks>
public class ChaseTests
{
    private static string Run(string source)
    {
        var program = new TypeChecker().Check(new Parser(new CufetLexer(source).Tokenize()).Parse());
        var output  = new StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    /// <summary>Wraps a body in the pull that introduces the type.</summary>
    private static string InCollections(string body) =>
        "Pull a book on collections.\n" + body + "\nDone.";

    // ── Building one ──────────────────────────────────────────────────────

    [Fact]
    public void AChase_StartsEmpty()
    {
        Assert.Equal("0", Run(InCollections(
            "    Define out as a chase.\n" +
            "    State the number of out.")));
    }

    [Fact]
    public void Insert_AppendsEveryCharacterOfTheText()
    {
        // ★★ A text, not a single character, and this is the decision the type turns on. Appending
        // what you just built is the operation a buffer exists for — requiring exactly one character
        // would make the common case a length check on a program that reads perfectly well.
        Assert.Equal("hello", Run(InCollections(
            "    Define out as a chase.\n" +
            "    Insert \"he\" into out.\n" +
            "    Insert \"llo\" into out.\n" +
            "    State out converted to text.")));
    }

    [Fact]
    public void TheNumberOf_CountsCharacters()
    {
        Assert.Equal("5", Run(InCollections(
            "    Define out as a chase.\n" +
            "    Insert \"hello\" into out.\n" +
            "    State the number of out.")));
    }

    // ── What it looks like, and what it is not ────────────────────────────

    [Fact]
    public void State_PrintsItAsTheCollectionItIs()
    {
        // ⚠ `(h, e, l, l, o)`, never `hello`. A reader must never mistake a buffer for a `text` —
        // and when the text is what they want, `converted to text` is the explicit copy that says
        // so. This is the one place the two could have been confused, so it is the one pinned.
        Assert.Equal("(h, e, l, l, o)", Run(InCollections(
            "    Define out as a chase.\n" +
            "    Insert \"hello\" into out.\n" +
            "    State out.")));
    }

    [Fact]
    public void ConvertedToText_CopiesAndLeavesTheBufferAlone()
    {
        // ★ Not a consuming move and not a view: the buffer lives on, independent. A `text` that
        // changed under you would break the one thing `text` promises.
        Assert.Equal("ab\nabc", Run(InCollections(
            "    Define out as a chase.\n" +
            "    Insert \"ab\" into out.\n" +
            "    Define taken as out converted to text.\n" +
            "    Insert \"c\" into out.\n" +
            "    State taken.\n" +
            "    State out converted to text.")));
    }

    [Fact]
    public void AnEmptyChase_ConvertsToEmptyTextAndPrintsAsAnEmptyCollection()
    {
        Assert.Equal("()\n", Run(InCollections(
            "    Define out as a chase.\n" +
            "    State out.\n" +
            "    State out converted to text.")) + "\n");
    }

    // ── Characters, not bytes and not UTF-16 units ────────────────────────

    [Fact]
    public void ACharacterOutsideAscii_CountsAsOne()
    {
        // ⚠⚠ The buffer holds CODE POINTS. `ö` is two bytes in UTF-8 and one character here, and
        // the compiled side stores UTF-32 for the same reason — two backends disagreeing about what
        // "the second character" means is exactly the divergence the oracle exists to catch.
        Assert.Equal("5", Run(InCollections(
            "    Define out as a chase.\n" +
            "    Insert \"wörld\" into out.\n" +
            "    State the number of out.")));
    }

    [Fact]
    public void AnAstralCharacter_IsAlsoOne()
    {
        // A character beyond the basic plane is a surrogate PAIR in a C# string — two chars for one
        // character. Counting chars rather than code points would say 2 here, and the compiled side,
        // storing UTF-32, would say 1.
        Assert.Equal("1", Run(InCollections(
            "    Define out as a chase.\n" +
            "    Insert \"\U0001F600\" into out.\n" +
            "    State the number of out.")));
    }

    // ── The name is only spent where the book was asked for ───────────────

    [Fact]
    public void OutsideThePull_ChaseIsAnOrdinaryName()
    {
        // ★★ Reserved BY BOOK. A word spent on one construct is a name a writer loses forever, so a
        // type a book introduces only claims its name where that book was pulled.
        Assert.Equal("6", Run("Define chase as 5.\nState chase + 1."));
    }

    [Fact]
    public void OutsideThePull_BuildingOneIsRefused()
    {
        Assert.Throws<TypeException>(() => Run("Define out as a chase.\nState the number of out."));
    }

    [Fact]
    public void InsertingSomethingThatIsNotText_IsRefused()
    {
        var ex = Assert.Throws<TypeException>(() => Run(InCollections(
            "    Define out as a chase.\n" +
            "    Insert 42 into out.")));
        Assert.Contains("holds characters", ex.Message);
    }
}
