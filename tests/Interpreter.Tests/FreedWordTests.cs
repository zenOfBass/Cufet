using Cufet.Interpreter;
using Cufet.Lexer;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Words given back on 2026-09-23, by the rule `Parser.EffectiveType` already stated:
//
//     Only words whose shape has a MANDATORY distinguishing token can be freed this way.
//
// Each of these has one immediately after it — `characters`/`delivery` take `from`, `entry`/`key`
// take `for`, `channel` takes `of`, `interrupt` takes `is`, `environment` takes `variable`,
// `read` takes `line` or `all`. A variable of the same name never produces that shape, so the
// lookahead is total rather than a heuristic.
//
// ★ Four of these had already cost a rename in this repository's own code: `channel`, `key`,
// `entry` and `interrupt`. That is what a reserved word costs, and it is why the list is audited
// rather than grown.
//
// ⚠⚠ `length`, `size`, `contents` and `position` are NOT here and were deliberately left
// reserved. Their shape is `the <word> of <x>`, which the NAMED-FIELD-ACCESS path claims before
// the expression switch ever runs — so freeing them needs type-directed resolution in the
// checker, the interpreter and the compiler, the way `rows` and `columns` are resolved. The
// prelude's own `the length of` is what proved it: it broke on the first build.
public class FreedWordTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        program = new TypeChecker().Check(program);
        var output  = new StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    // ── The words are names again, in all four positions ──────────────────
    //
    // ⚠ All four matter and they are not the same test. A reserved word is banned as a variable
    // AND as a field AND as a parameter AND as an iterator, and the field ban is the costliest —
    // `Define object thing with (the text key)` is refused outright by a reservation.

    [Theory]
    [InlineData("characters")]
    [InlineData("delivery")]
    [InlineData("entry")]
    [InlineData("key")]
    [InlineData("interrupt")]
    [InlineData("environment")]
    [InlineData("read")]
    [InlineData("channel")]
    public void FreedWord_UsableAsAVariableName(string word)
        => Assert.Equal("5", Run($"Define {word} as 5. State {word}."));

    [Theory]
    [InlineData("characters")]
    [InlineData("delivery")]
    [InlineData("entry")]
    [InlineData("key")]
    [InlineData("interrupt")]
    [InlineData("environment")]
    [InlineData("read")]
    [InlineData("channel")]
    public void FreedWord_UsableAsAParameterName(string word)
        => Assert.Equal("7", Run(
            $"Bind number to echo, given (the number {word}): Return {word}. Done. " +
            $"State cast echo on (7)."));

    [Theory]
    [InlineData("characters")]
    [InlineData("delivery")]
    [InlineData("entry")]
    [InlineData("key")]
    [InlineData("interrupt")]
    [InlineData("environment")]
    [InlineData("read")]
    [InlineData("channel")]
    public void FreedWord_UsableAsAFieldName(string word)
        => Assert.Equal("3", Run(
            $"Define r as a record with (the {word} 3). State the {word} of r."));

    [Theory]
    [InlineData("characters")]
    [InlineData("delivery")]
    [InlineData("entry")]
    [InlineData("key")]
    [InlineData("interrupt")]
    [InlineData("environment")]
    [InlineData("read")]
    [InlineData("channel")]
    public void FreedWord_UsableAsAnIteratorName(string word)
        => Assert.Equal("1\n2", Run(
            $"Define xs as a series with (1, 2). For each {word} in xs, repeat: State {word}. Done."));

    [Fact]
    public void FreedWords_SeveralAtOnce()
        => Assert.Equal("36", Run(
            "Define characters as 1. Define delivery as 2. Define entry as 3. Define key as 4. " +
            "Define interrupt as 5. Define environment as 6. Define read as 7. Define channel as 8. " +
            "State characters + delivery + entry + key + interrupt + environment + read + channel."));

    // ── …and the shapes that wanted them still parse ──────────────────────
    //
    // ★★ The half a freeing is most likely to break, and the half a "does it work as a name?"
    // test cannot see. Each of these is the keyword reading, in the same program as a variable of
    // the same name where one fits — so a lookahead that went too wide fails here.

    [Fact]
    public void Characters_SubstringStillParses()
        => Assert.Equal("ell", Run("Define characters as 2. State the characters from characters to 4 of \"hello\"."));

    [Fact]
    public void Characters_FromAnEdgeStillParses()
        => Assert.Equal("he", Run("State the first 2 characters of \"hello\"."));

    [Fact]
    public void Entry_MapLookupStillParses()
        => Assert.Equal("1", Run(
            "Define m as a map from text to number. In m, the entry for \"a\" becomes 1. " +
            "State the entry for \"a\" in m."));

    [Fact]
    public void Key_MapMembershipStillParses()
        => Assert.Equal("true", Run(
            "Define m as a map from text to number. In m, the entry for \"a\" becomes 1. " +
            "State m has a key for \"a\"."));

    [Fact]
    public void Environment_LookupStillParses()
        => Assert.Equal("none", Run(
            "Define environment as 1. " +
            "State the environment variable \"CUFET_NOT_A_REAL_VARIABLE\" but void is \"none\"."));

    // ⚠ `the characters of r` is the FIELD reading and `the characters from a to b of t` is the
    // keyword one. They differ by a single token, and that token is the whole rule.
    [Fact]
    public void Characters_FieldAndKeywordInTheSameProgram()
        => Assert.Equal("3\nell", Run(
            "Define r as a record with (the characters 3). State the characters of r. " +
            "State the characters from 2 to 4 of \"hello\"."));

    // ══ Freed by TYPE, not by a mandatory token — 2026-09-24 ══════════════
    //
    // ★★ A SECOND mechanism, and the words above could not use it. `the length of <x>` is the
    // shape NAMED FIELD ACCESS claims, and it claims it before the expression switch ever runs —
    // so no lookahead can free these, however mandatory the `of` is. What frees them is the
    // TARGET'S TYPE, decided in the checker: text has a length, a map has a size, and on anything
    // else the name is an ordinary field. `the rows of <matrix>` and `the width of <bits>` were
    // already resolved this way; these two just stopped being the exception.
    //
    // ★ `contents` rides along on a THIRD rule: the target of a field access has to be an
    // expression, and `the contents of the DIRECTORY <path>` puts a keyword there. So the parser
    // can rule out the field reading without knowing any types — the mandatory-token rule again,
    // reaching two tokens out rather than one.
    //
    // ⚠ `position` is NOT here and cannot be. `the position of <needle> in <haystack>` is
    // distinguished by an `in` that sits after a whole expression, so nothing adjacent decides it
    // and the type of the target cannot either — the parse fails before any type is known.

    [Theory]
    [InlineData("length")]
    [InlineData("size")]
    [InlineData("contents")]
    public void TypeFreedWord_UsableAsAVariableName(string word)
        => Assert.Equal("5", Run($"Define {word} as 5. State {word}."));

    [Theory]
    [InlineData("length")]
    [InlineData("size")]
    [InlineData("contents")]
    public void TypeFreedWord_UsableAsAParameterName(string word)
        => Assert.Equal("7", Run(
            $"Bind number to echo, given (the number {word}): Return {word}. Done. " +
            $"State cast echo on (7)."));

    [Theory]
    [InlineData("length")]
    [InlineData("size")]
    [InlineData("contents")]
    public void TypeFreedWord_UsableAsAFieldName(string word)
        => Assert.Equal("3", Run(
            $"Define r as a record with (the {word} 3). State the {word} of r."));

    [Theory]
    [InlineData("length")]
    [InlineData("size")]
    [InlineData("contents")]
    public void TypeFreedWord_UsableAsAnIteratorName(string word)
        => Assert.Equal("1\n2", Run(
            $"Define xs as a series with (1, 2). For each {word} in xs, repeat: State {word}. Done."));

    // ── …and the built-in readings still work ─────────────────────────────

    [Fact]
    public void TheLengthOfTextIsStillItsLength()
        => Assert.Equal("5", Run("State the length of \"hello\"."));

    // ⚠ CODE POINTS, not storage units. `e` + a combining acute is two of them and one glyph;
    // counting UTF-16 units would give the same answer here for the wrong reason, so the pin is
    // on a character OUTSIDE the basic plane as well.
    [Fact]
    public void TheLengthOfTextCountsCodePoints()
        => Assert.Equal("2\n2", Run("State the length of \"e\u0301\". State the length of \"a\U0001F600\"."));

    [Fact]
    public void TheContentsOfADirectoryStillLists()
        => Assert.Equal("true", Run("""
            Try to:
                Define contents as the contents of the directory ".".
                State the number of contents is greater than 0.
            Done.
            In case of failure: State "unreadable". Done.
            """));

    [Fact]
    public void TheSizeOfAMapIsStillItsSize()
        => Assert.Equal("2", Run(
            "Define m as a map from text to number. In m, the entry for \"a\" becomes 1. " +
            "In m, the entry for \"b\" becomes 2. State the size of m."));

    // ★★ The arm that decides the whole design: a type that HAS the field wins over the built-in
    // reading. Without this, freeing the word would be a lie — the name would be legal to declare
    // and impossible to read back.
    [Fact]
    public void AnObjectsOwnFieldBeatsTheBuiltinReading()
        => Assert.Equal("3", Run(
            "Define object box with (the number length). " +
            "Define b as a new box { the length 3 }. State the length of b."));

    // ── …and the refusals the keywords owned came with them ───────────────
    //
    // ⚠⚠ The real cost of freeing a word, and the one that would have gone unnoticed: as keywords
    // these two owned their error messages. Freed and left alone, `the length of scores` would
    // answer *"that isn't a record or object"* — true, and it sends the reader nowhere.

    [Fact]
    public void TheLengthOfASeriesStillSaysUseTheNumberOf()
    {
        var error = Assert.Throws<TypeException>(() => Run(
            "Define scores as a series with (1, 2, 3). State the length of scores."));
        Assert.Contains("'the length of' works on text only", error.Message);
        Assert.Contains("use 'the number of series'", error.Message);
    }

    [Fact]
    public void TheSizeOfASetStillSaysItIsASet()
    {
        var error = Assert.Throws<TypeException>(() => Run(
            "Define s as a set of number. State the size of s."));
        Assert.Contains("works on maps, and this is a set", error.Message);
    }
}
