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
}
