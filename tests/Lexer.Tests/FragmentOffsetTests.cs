using Cufet.Lexer;
using Xunit;

namespace Cufet.Lexer.Tests;

/// <summary>
/// Text lexed on its own, reported where it actually sits inside a larger file.
/// </summary>
/// <remarks>
/// ★★ A lexer starts at line 1, and a fragment of a file does not. Cufet source held inside another
/// Cufet file — `Define cufet &lt;name&gt; as [ … ].` — is exactly that case, and without an offset every
/// message from inside one points at a line of nowhere. That is worse than no message, because it
/// reads like a real one.
///
/// ★ The offsets are applied ONCE, to the finished tokens, rather than threaded through the twenty
/// places a token is built. The scanner stays unaware that a fragment is a thing, and there is no
/// second place for the arithmetic to be got wrong.
/// </remarks>
public class FragmentOffsetTests
{
    private static IReadOnlyList<Token> Lex(string source, int lineOffset, int columnOffset) =>
        new Lexer(source, lineOffset, columnOffset).Tokenize();

    [Fact]
    public void NoOffset_IsExactlyWhatTheOrdinaryConstructorGives()
    {
        // ! The counter-test, and the one that matters most: every caller but one passes no offset,
        // so the ordinary path must be untouched — same lines, same columns, same token objects'
        // worth of information.
        const string source = "State 1.\nState 2.";
        var plain    = new Lexer(source).Tokenize();
        var explicitly = Lex(source, 0, 0);

        Assert.Equal(plain.Count, explicitly.Count);
        for (int i = 0; i < plain.Count; i++)
        {
            Assert.Equal(plain[i].Line, explicitly[i].Line);
            Assert.Equal(plain[i].Column, explicitly[i].Column);
        }
    }

    [Fact]
    public void TheLineOffset_MovesEveryLineDown()
    {
        var tokens = Lex("State 1.\nState 2.", lineOffset: 9, columnOffset: 0);

        Assert.Equal(10, tokens[0].Line);                 // `State` on the fragment's line 1
        Assert.Equal(11, tokens[3].Line);                 // `State` on its line 2
    }

    [Fact]
    public void TheColumnOffset_MovesTheFirstLineOnly()
    {
        // ⚠ This is the whole subtlety. A fragment's first line is pushed right by whatever
        // preceded it there — `Define cufet shape as [` — and every later line begins at column 1
        // in the outer file exactly as it does in the fragment. Adding the offset to all of them
        // would put every message a few characters to the right, which is the kind of wrong that
        // looks right.
        var tokens = Lex("State 1.\nState 2.", lineOffset: 0, columnOffset: 22);

        Assert.Equal(23, tokens[0].Column);               // 1 + 22, on the first line
        Assert.Equal(1,  tokens[3].Column);               // untouched, on the second
    }

    [Fact]
    public void ARefusal_CarriesTheOffsetToo()
    {
        // ⚠ A refusal is thrown rather than returned, so rebasing only the tokens would leave the
        // one message a reader is most likely to see pointing at nowhere.
        var error = Assert.Throws<LexerException>(() =>
            Lex("State 1.\nState @ 2.", lineOffset: 9, columnOffset: 22));

        Assert.Equal(11, error.Line);
        Assert.Equal(7, error.Column);                    // second line: no column offset
        Assert.Contains("'@'", error.Message);
        Assert.Contains("line 11, column 7", error.Message);   // the MESSAGE, not just the fields
    }

    [Fact]
    public void ARefusalOnTheFirstLine_CarriesBothOffsets()
    {
        var error = Assert.Throws<LexerException>(() =>
            Lex("State @ 1.", lineOffset: 9, columnOffset: 22));

        Assert.Equal(10, error.Line);
        Assert.Equal(29, error.Column);                   // 7 + 22
    }
}
