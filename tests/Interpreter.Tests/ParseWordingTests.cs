using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// What a parse refusal CALLS things, when it has to name them to a person.
/// </summary>
/// <remarks>
/// ⚠⚠ `Consume` used to put the raw enum name into the message — `expected Dot`, `expected Colon`,
/// `expected Identifier`. Nobody outside this repo knows what a `Dot` is, and punctuation is the
/// first thing a beginner gets wrong, so lexer vocabulary was the first vocabulary they met.
///
/// <para>★ Only the JARGON needs translating. A keyword's enum name is the keyword itself, so
/// `expected Done` and `expected As` already read correctly and are left alone — the fix is as
/// narrow as the problem.</para>
/// </remarks>
public class ParseWordingTests
{
    private static ParseException ParseFails(string source) =>
        Assert.Throws<ParseException>(() => new Parser(new CufetLexer(source).Tokenize()).Parse());

    [Fact]
    public void AMissingFullStop_AsksForTheCharacter_NotForADot()
    {
        var ex = ParseFails("""
            State "hello"
            State "world".
            """);

        Assert.Contains("expected '.'", ex.Message);
        Assert.DoesNotContain("expected Dot", ex.Message);
    }

    [Fact]
    public void AMissingColon_AsksForTheCharacter()
    {
        var ex = ParseFails("""
            If 1 is 1
                State "hi".
            Done.
            """);

        Assert.Contains("expected ':'", ex.Message);
        Assert.DoesNotContain("expected Colon", ex.Message);
    }

    /// <remarks>
    /// ★ The guard against translating too much. `Done` and `As` are words a reader has typed, so
    /// the enum name IS the right word and the map must leave them alone.
    ///
    /// ⚠⚠ THE PROGRAM CHANGED, THE SUBJECT DID NOT. This used to be written `Define carrots = 3.`
    /// — until that shape got an educational message of its own and stopped reaching
    /// `expected As` at all. `Define carrots to 3.` is the same `Consume(TokenType.As)` at the same
    /// call site, reached by a wrong word rather than by the one mistake now handled ahead of it.
    /// The assertion is untouched: a test whose fixture is only a VEHICLE gets a new vehicle, never
    /// a weaker claim.
    /// </remarks>
    [Fact]
    public void AKeywordsOwnNameIsLeftAlone()
    {
        var ex = ParseFails("Define carrots to 3.");
        Assert.Contains("expected As", ex.Message);
    }

    /// <summary>A reserved word used as a name says so, rather than naming token types.</summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ THE ONE WITH A WITNESS. `expected Identifier, got Key "key"` leaves a reader to deduce
    /// that `key` is taken. MEASURED across two sessions of writing Cufet: six collisions — `a`,
    /// `one`, `channel`, `key`, `arguments`, `from` — every one an everyday noun somebody would
    /// reach for, and every one costing a parse error and a rename.
    /// </para>
    /// <para>
    /// ★ It needs no keyword table. The lexer turns a bare word into an `Identifier` unless the
    /// language has taken that word, so a WORD arriving as anything else is reserved by
    /// construction.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("key")]
    [InlineData("channel")]
    [InlineData("arguments")]
    public void AReservedWordAsAName_SaysItIsReserved(string word)
    {
        var ex = ParseFails($"Define {word} as 3.");

        Assert.Contains($"'{word}' is a word Cufet has taken", ex.Message);
        Assert.Contains("neither a variable nor a field", ex.Message);
        Assert.DoesNotContain("expected Identifier", ex.Message);
    }

    /// <remarks>
    /// ⚠ The guard against the heuristic going wide. A bracket or a number arriving where a name
    /// was wanted is a DIFFERENT mistake, and telling someone that `(` is a reserved word would be
    /// false. Letters only.
    /// </remarks>
    [Fact]
    public void SomethingThatIsNotAWord_GetsTheOrdinaryMessage()
    {
        var ex = ParseFails("Define 5 as 3.");

        Assert.DoesNotContain("word Cufet has taken", ex.Message);
        Assert.Contains("expected a name", ex.Message);
    }
}
