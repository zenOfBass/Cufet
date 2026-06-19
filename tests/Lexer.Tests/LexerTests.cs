using Cufet.Lexer;
using Xunit;

namespace Cufet.Lexer.Tests;

public class LexerTests
{
    private static IReadOnlyList<Token> Lex(string source) => new Lexer(source).Tokenize();

    // Strips the trailing Eof so assertions stay concise.
    private static IReadOnlyList<Token> LexTokens(string source)
    {
        var all = Lex(source);
        return all.Take(all.Count - 1).ToList();
    }

    // ── Identifiers ────────────────────────────────────────────────────────

    [Fact]
    public void SimpleIdentifier()
    {
        var tokens = LexTokens("foo");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("foo", tokens[0].Lexeme);
    }

    [Fact]
    public void IdentifierWithInternalDash()
    {
        var tokens = LexTokens("foo-bar");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("foo-bar", tokens[0].Lexeme);
    }

    [Fact]
    public void IdentifierWithMultipleInternalDashes()
    {
        var tokens = LexTokens("foo-bar-baz");
        Assert.Single(tokens);
        Assert.Equal("foo-bar-baz", tokens[0].Lexeme);
    }

    [Fact]
    public void IdentifierWithDigits()
    {
        var tokens = LexTokens("x2");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("x2", tokens[0].Lexeme);
    }

    [Fact]
    public void TrailingDashIsMinusOperator()
    {
        // "foo-" → Identifier "foo" + Minus; '-' is now a valid arithmetic token
        var tokens = LexTokens("foo-");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal(TokenType.Minus,      tokens[1].Type);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("myVar")]
    [InlineData("total")]
    public void LowercaseInitialIdentifierIsValid(string word)
    {
        var tokens = LexTokens(word);
        Assert.Single(tokens);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal(word, tokens[0].Lexeme); // case is preserved in the body
    }

    [Theory]
    [InlineData("Foo")]
    [InlineData("FOO")]
    [InlineData("Total")]
    public void UppercaseInitialIdentifierThrows(string word)
    {
        Assert.Throws<LexerException>(() => LexTokens(word));
    }

    [Fact]
    public void IdentifierBodyCaseIsPreserved()
    {
        // First char must be lowercase; remaining chars retain their case.
        // myVar and myvar are distinct identifiers.
        var tokens = LexTokens("myVar myvar");
        Assert.Equal(2, tokens.Count);
        Assert.Equal("myVar", tokens[0].Lexeme);
        Assert.Equal("myvar", tokens[1].Lexeme);
    }

    // ── Reserved keyword: it ──────────────────────────────────────────────

    [Fact]
    public void ItIsPronounNotIdentifier()
    {
        var tokens = LexTokens("it");
        Assert.Single(tokens);
        Assert.Equal(TokenType.It, tokens[0].Type);
    }

    [Fact]
    public void ItKeywordIsCaseInsensitive()
    {
        foreach (var s in new[] { "it", "It", "IT" })
        {
            var tokens = LexTokens(s);
            Assert.Single(tokens);
            Assert.Equal(TokenType.It, tokens[0].Type);
        }
    }

    [Fact]
    public void ItPrefixIdentifierIsIdentifier()
    {
        // "itself" starts with "it" but is not the keyword
        var tokens = LexTokens("itself");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("itself", tokens[0].Lexeme);
    }

    // ── Articles (noise tokens) ───────────────────────────────────────────

    [Theory]
    [InlineData("a")]
    [InlineData("an")]
    [InlineData("the")]
    public void ArticlesAreNoise(string article)
    {
        var tokens = LexTokens(article);
        Assert.Single(tokens);
        Assert.Equal(TokenType.Article, tokens[0].Type);
        Assert.True(tokens[0].IsNoise);
    }

    [Fact]
    public void ArticlesAreCaseInsensitive()
    {
        foreach (var s in new[] { "the", "The", "THE", "a", "A", "an", "AN" })
        {
            var tokens = LexTokens(s);
            Assert.Single(tokens);
            Assert.Equal(TokenType.Article, tokens[0].Type);
        }
    }

    // ── Reference forms ──────────────────────────────────────────────────

    [Fact]
    public void ArticlePrecedingIdentifier()
    {
        var tokens = LexTokens("the counter");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenType.Article, tokens[0].Type);
        Assert.Equal(TokenType.Identifier, tokens[1].Type);
        Assert.Equal("counter", tokens[1].Lexeme);
    }

    [Fact]
    public void ArticlePrecedingIdentifierWithDash()
    {
        var tokens = LexTokens("a retry-count");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenType.Article, tokens[0].Type);
        Assert.Equal("retry-count", tokens[1].Lexeme);
    }

    // ── Multiline / line tracking ─────────────────────────────────────────

    [Fact]
    public void LineNumbersTracked()
    {
        var tokens = LexTokens("foo\nbar");
        Assert.Equal(1, tokens[0].Line);
        Assert.Equal(2, tokens[1].Line);
    }

    // ── Numbers ───────────────────────────────────────────────────────────

    [Fact]
    public void IntegerLiteral()
    {
        var tokens = LexTokens("42");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Number, tokens[0].Type);
        Assert.Equal("42", tokens[0].Lexeme);
    }

    [Fact]
    public void SingleDigit()
    {
        var tokens = LexTokens("0");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Number, tokens[0].Type);
    }

    [Fact]
    public void DecimalLiteral()
    {
        var tokens = LexTokens("3.14");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Number, tokens[0].Type);
        Assert.Equal("3.14", tokens[0].Lexeme);
    }

    [Fact]
    public void DecimalFollowedByTerminator()
    {
        // 3.14. → Number("3.14") + Dot
        var tokens = LexTokens("3.14.");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(TokenType.Number, tokens[0].Type);
        Assert.Equal("3.14", tokens[0].Lexeme);
        Assert.Equal(TokenType.Dot, tokens[1].Type);
    }

    [Fact]
    public void IntegerFollowedByTerminator()
    {
        // 5. → the dot is NOT a decimal point (no digit after it) → Number("5") + Dot
        var tokens = LexTokens("5.");
        Assert.Equal(2, tokens.Count);
        Assert.Equal("5", tokens[0].Lexeme);
        Assert.Equal(TokenType.Dot, tokens[1].Type);
    }

    [Fact]
    public void DotOnIdentifierIsTerminator()
    {
        // x.5 → Identifier + Dot + Number (downstream parse error, correct lex)
        var tokens = LexTokens("x.5");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal(TokenType.Dot,        tokens[1].Type);
        Assert.Equal(TokenType.Number,     tokens[2].Type);
    }

    // ── Strings ───────────────────────────────────────────────────────────

    [Fact]
    public void SimpleString()
    {
        var tokens = LexTokens("\"hello\"");
        Assert.Single(tokens);
        Assert.Equal(TokenType.String, tokens[0].Type);
        Assert.Equal("hello", tokens[0].Lexeme);
    }

    [Fact]
    public void StringWithSingleQuotes()
    {
        var tokens = LexTokens("\"I can't do that\"");
        Assert.Single(tokens);
        Assert.Equal("I can't do that", tokens[0].Lexeme);
    }

    [Fact]
    public void String_Escape_DoubleQuote()
    {
        // Cufet source: "say \"hi\""  →  decoded: say "hi"
        var tokens = LexTokens("\"say \\\"hi\\\"\"");
        Assert.Single(tokens);
        Assert.Equal("say \"hi\"", tokens[0].Lexeme);
    }

    [Fact]
    public void String_Escape_Newline()
    {
        var tokens = LexTokens("\"a\\nb\"");
        Assert.Single(tokens);
        Assert.Equal("a\nb", tokens[0].Lexeme);
    }

    [Fact]
    public void String_Escape_Tab()
    {
        var tokens = LexTokens("\"a\\tb\"");
        Assert.Single(tokens);
        Assert.Equal("a\tb", tokens[0].Lexeme);
    }

    [Fact]
    public void String_Escape_CarriageReturn()
    {
        var tokens = LexTokens("\"a\\rb\"");
        Assert.Single(tokens);
        Assert.Equal("a\rb", tokens[0].Lexeme);
    }

    [Fact]
    public void String_Escape_Backslash()
    {
        var tokens = LexTokens("\"a\\\\b\"");
        Assert.Single(tokens);
        Assert.Equal("a\\b", tokens[0].Lexeme);
    }

    [Fact]
    public void String_Escape_LBrace()
    {
        var tokens = LexTokens("\"a\\{b\"");
        Assert.Single(tokens);
        Assert.Equal("a{b", tokens[0].Lexeme);
    }

    [Fact]
    public void String_Escape_RBrace()
    {
        var tokens = LexTokens("\"a\\}b\"");
        Assert.Single(tokens);
        Assert.Equal("a}b", tokens[0].Lexeme);
    }

    [Fact]
    public void String_UnrecognizedEscapeThrows()
    {
        Assert.Throws<LexerException>(() => Lex("\"\\z\""));
    }

    [Fact]
    public void UnterminatedStringThrows()
    {
        Assert.Throws<LexerException>(() => Lex("\"oops"));
    }

    // ── String interpolation ──────────────────────────────────────────────

    // Helper: extract token types (without Eof) from an interpolated-string source.
    private static IReadOnlyList<TokenType> LexTypes(string source) =>
        LexTokens(source).Select(t => t.Type).ToList();

    [Fact]
    public void Interp_PlainStringUnchanged()
    {
        // No bare '{' → still emits a single String token, not InterpolOpen.
        var tokens = LexTokens("\"hello\"");
        Assert.Single(tokens);
        Assert.Equal(TokenType.String, tokens[0].Type);
    }

    [Fact]
    public void Interp_EscapedBraceIsNotInterpolation()
    {
        // \{ and \} produce literal braces; the string is still plain.
        var tokens = LexTokens("\"\\{not interp\\}\"");
        Assert.Single(tokens);
        Assert.Equal(TokenType.String, tokens[0].Type);
        Assert.Equal("{not interp}", tokens[0].Lexeme);
    }

    [Fact]
    public void Interp_SimpleTokenStream()
    {
        // "hello {name}!" → InterpolOpen, StringPiece, InterpolHoleOpen,
        //                    Identifier, InterpolHoleClose, StringPiece, InterpolClose
        var types = LexTypes("\"hello {name}!\"");
        Assert.Equal(
            new[] { TokenType.InterpolOpen, TokenType.StringPiece,
                    TokenType.InterpolHoleOpen, TokenType.Identifier, TokenType.InterpolHoleClose,
                    TokenType.StringPiece, TokenType.InterpolClose },
            types);
    }

    [Fact]
    public void Interp_TokenStream_LeadingHole()
    {
        // "{name} says" — no leading StringPiece (empty pieces are omitted)
        var types = LexTypes("\"{name} says\"");
        Assert.Equal(
            new[] { TokenType.InterpolOpen,
                    TokenType.InterpolHoleOpen, TokenType.Identifier, TokenType.InterpolHoleClose,
                    TokenType.StringPiece, TokenType.InterpolClose },
            types);
    }

    [Fact]
    public void Interp_TokenStream_TrailingHole()
    {
        // "hello {name}" — no trailing StringPiece
        var types = LexTypes("\"hello {name}\"");
        Assert.Equal(
            new[] { TokenType.InterpolOpen, TokenType.StringPiece,
                    TokenType.InterpolHoleOpen, TokenType.Identifier, TokenType.InterpolHoleClose,
                    TokenType.InterpolClose },
            types);
    }

    [Fact]
    public void Interp_TokenStream_AdjacentHoles()
    {
        // "{x}{y}" — two holes with no piece between them
        var types = LexTypes("\"{x}{y}\"");
        Assert.Equal(
            new[] { TokenType.InterpolOpen,
                    TokenType.InterpolHoleOpen, TokenType.Identifier, TokenType.InterpolHoleClose,
                    TokenType.InterpolHoleOpen, TokenType.Identifier, TokenType.InterpolHoleClose,
                    TokenType.InterpolClose },
            types);
    }

    [Fact]
    public void Interp_TokenStream_ArithmeticHole()
    {
        // "{1 + 2}" — arithmetic inside hole
        var types = LexTypes("\"{1 + 2}\"");
        Assert.Equal(
            new[] { TokenType.InterpolOpen,
                    TokenType.InterpolHoleOpen,
                    TokenType.Number, TokenType.Plus, TokenType.Number,
                    TokenType.InterpolHoleClose,
                    TokenType.InterpolClose },
            types);
    }

    [Fact]
    public void Interp_TokenStream_NestedBracesInHole()
    {
        // "{x {y: 1}}" — object literal inside hole; outer } is the hole close
        var types = LexTypes("\"{x {y: 1}}\"");
        Assert.Equal(
            new[] { TokenType.InterpolOpen,
                    TokenType.InterpolHoleOpen,
                    TokenType.Identifier, TokenType.LBrace, TokenType.Identifier,
                    TokenType.Colon, TokenType.Number, TokenType.RBrace,
                    TokenType.InterpolHoleClose,
                    TokenType.InterpolClose },
            types);
    }

    [Fact]
    public void Interp_StringPieceLexemesPreserved()
    {
        // The literal parts must survive correctly.
        var tokens = LexTokens("\"hello {name}!\"");
        var pieces = tokens.Where(t => t.Type == TokenType.StringPiece).ToList();
        Assert.Equal(2, pieces.Count);
        Assert.Equal("hello ", pieces[0].Lexeme);
        Assert.Equal("!", pieces[1].Lexeme);
    }

    [Fact]
    public void Interp_EscapesInsidePiece()
    {
        // Escape sequences inside the literal part of an interpolated string still work.
        var tokens = LexTokens("\"line1\\n{x}line2\"");
        var piece = tokens.First(t => t.Type == TokenType.StringPiece);
        Assert.Equal("line1\n", piece.Lexeme);
    }

    [Fact]
    public void Interp_EmptyHoleThrows()
    {
        Assert.Throws<LexerException>(() => Lex("\"{  }\""));
    }

    [Fact]
    public void Interp_UnterminatedHoleThrows()
    {
        Assert.Throws<LexerException>(() => Lex("\"hello {name\""));
    }

    // ── Define / as / becomes keywords ───────────────────────────────────

    [Theory]
    [InlineData("Define", TokenType.Define)]
    [InlineData("as",     TokenType.As)]
    [InlineData("becomes",TokenType.Becomes)]
    public void ControlKeywords(string word, TokenType expected)
    {
        var tokens = LexTokens(word);
        Assert.Single(tokens);
        Assert.Equal(expected, tokens[0].Type);
    }

    [Fact]
    public void DefineStatementTokenizes()
    {
        var tokens = LexTokens("Define total as 0.");
        Assert.Equal(5, tokens.Count);
        Assert.Equal(TokenType.Define,     tokens[0].Type);
        Assert.Equal(TokenType.Identifier, tokens[1].Type);
        Assert.Equal(TokenType.As,         tokens[2].Type);
        Assert.Equal(TokenType.Number,     tokens[3].Type);
        Assert.Equal(TokenType.Dot,        tokens[4].Type);
    }

    [Fact]
    public void BecomesStatementTokenizes()
    {
        var tokens = LexTokens("total becomes 1.");
        Assert.Equal(4, tokens.Count);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal(TokenType.Becomes,    tokens[1].Type);
        Assert.Equal(TokenType.Number,     tokens[2].Type);
        Assert.Equal(TokenType.Dot,        tokens[3].Type);
    }

    // ── Operator symbols ─────────────────────────────────────────────────

    [Theory]
    [InlineData("+",  TokenType.Plus)]
    [InlineData("-",  TokenType.Minus)]
    [InlineData("*",  TokenType.Star)]
    [InlineData("/",  TokenType.Slash)]
    [InlineData("(",  TokenType.LParen)]
    [InlineData(")",  TokenType.RParen)]
    [InlineData("=",  TokenType.Equal)]
    [InlineData(":",  TokenType.Colon)]
    [InlineData(",",  TokenType.Comma)]
    [InlineData("<",  TokenType.Lt)]
    [InlineData(">",  TokenType.Gt)]
    [InlineData("<=", TokenType.Lte)]
    [InlineData(">=", TokenType.Gte)]
    public void OperatorSymbols(string src, TokenType expected)
    {
        var tokens = LexTokens(src);
        Assert.Single(tokens);
        Assert.Equal(expected, tokens[0].Type);
    }

    [Fact]
    public void BinaryMinusRequiresSurroundingWhitespace()
    {
        // "x - y" → Identifier, Minus, Identifier
        var tokens = LexTokens("x - y");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal(TokenType.Minus,      tokens[1].Type);
        Assert.Equal(TokenType.Identifier, tokens[2].Type);
    }

    [Fact]
    public void DashWithoutSpaceIsPartOfIdentifier()
    {
        // "a-b" → single identifier — unchanged from before, confirmed here
        var tokens = LexTokens("a-b");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("a-b", tokens[0].Lexeme);
    }

    // ── Control flow keywords ─────────────────────────────────────────────

    [Theory]
    [InlineData("If",        TokenType.If)]
    [InlineData("Otherwise", TokenType.Otherwise)]
    [InlineData("Done",      TokenType.Done)]
    [InlineData("While",     TokenType.While)]
    [InlineData("Repeat",    TokenType.Repeat)]
    [InlineData("Until",     TokenType.Until)]
    [InlineData("Stop",      TokenType.Stop)]
    [InlineData("Skip",      TokenType.Skip)]
    public void ControlFlowKeywords(string word, TokenType expected)
    {
        var tokens = LexTokens(word);
        Assert.Single(tokens);
        Assert.Equal(expected, tokens[0].Type);
    }

    [Fact]
    public void IfLowercaseIsKeyword()
    {
        // "if" (lowercase, as used in "Otherwise if") maps to TokenType.If via case-insensitive lookup
        var tokens = LexTokens("if");
        Assert.Single(tokens);
        Assert.Equal(TokenType.If, tokens[0].Type);
    }

    // ── Collection keywords ───────────────────────────────────────────────

    [Fact]
    public void SeriesKeyword()
    {
        var tokens = LexTokens("series");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Series, tokens[0].Type);
    }

    [Theory]
    [InlineData("first",   TokenType.Ordinal)]
    [InlineData("second",  TokenType.Ordinal)]
    [InlineData("third",   TokenType.Ordinal)]
    [InlineData("fourth",  TokenType.Ordinal)]
    [InlineData("fifth",   TokenType.Ordinal)]
    [InlineData("sixth",   TokenType.Ordinal)]
    [InlineData("seventh", TokenType.Ordinal)]
    [InlineData("eighth",  TokenType.Ordinal)]
    [InlineData("ninth",   TokenType.Ordinal)]
    [InlineData("tenth",   TokenType.Ordinal)]
    [InlineData("last",    TokenType.Ordinal)]
    public void OrdinalKeywords(string word, TokenType expected)
    {
        var tokens = LexTokens(word);
        Assert.Single(tokens);
        Assert.Equal(expected, tokens[0].Type);
    }

    [Theory]
    [InlineData("item",   TokenType.Item)]
    [InlineData("of",     TokenType.Of)]
    [InlineData("number", TokenType.NumberKw)]
    [InlineData("add",    TokenType.Add)]
    [InlineData("to",     TokenType.To)]
    [InlineData("start",  TokenType.Start)]
    [InlineData("after",  TokenType.After)]
    [InlineData("remove", TokenType.Remove)]
    [InlineData("from",   TokenType.From)]
    public void SeriesOperationKeywords(string word, TokenType expected)
    {
        var tokens = LexTokens(word);
        Assert.Single(tokens);
        Assert.Equal(expected, tokens[0].Type);
    }

    // ── Word-form comparison keywords ─────────────────────────────────────

    [Theory]
    [InlineData("is",      TokenType.Is)]
    [InlineData("not",     TokenType.Not)]
    [InlineData("greater", TokenType.Greater)]
    [InlineData("less",    TokenType.Less)]
    [InlineData("than",    TokenType.Than)]
    [InlineData("or",      TokenType.Or)]
    [InlineData("more",    TokenType.More)]
    public void WordComparisonKeywords(string word, TokenType expected)
    {
        var tokens = LexTokens(word);
        Assert.Single(tokens);
        Assert.Equal(expected, tokens[0].Type);
    }

    // ── State keyword ─────────────────────────────────────────────────────

    [Fact]
    public void StateKeyword()
    {
        var tokens = LexTokens("State");
        Assert.Single(tokens);
        Assert.Equal(TokenType.State, tokens[0].Type);
    }

    [Theory]
    [InlineData("state",   TokenType.State)]
    [InlineData("State",   TokenType.State)]
    [InlineData("STATE",   TokenType.State)]
    [InlineData("define",  TokenType.Define)]
    [InlineData("Define",  TokenType.Define)]
    [InlineData("DEFINE",  TokenType.Define)]
    [InlineData("If",      TokenType.If)]
    [InlineData("IF",      TokenType.If)]
    [InlineData("Otherwise",  TokenType.Otherwise)]
    [InlineData("OTHERWISE",  TokenType.Otherwise)]
    [InlineData("done",    TokenType.Done)]
    [InlineData("DONE",    TokenType.Done)]
    public void KeywordsAreCaseInsensitive(string word, TokenType expected)
    {
        var tokens = LexTokens(word);
        Assert.Single(tokens);
        Assert.Equal(expected, tokens[0].Type);
    }

    // ── Dot (statement terminator) ────────────────────────────────────────

    [Fact]
    public void DotToken()
    {
        var tokens = LexTokens(".");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Dot, tokens[0].Type);
    }

    [Fact]
    public void FullStatementTokenizes()
    {
        var tokens = LexTokens("State 5.");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenType.State,  tokens[0].Type);
        Assert.Equal(TokenType.Number, tokens[1].Type);
        Assert.Equal(TokenType.Dot,    tokens[2].Type);
    }

    // ── EOF ───────────────────────────────────────────────────────────────

    [Fact]
    public void EmptySourceYieldsOnlyEof()
    {
        var tokens = Lex("");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Eof, tokens[0].Type);
    }

    [Fact]
    public void WhitespaceOnlyYieldsOnlyEof()
    {
        var tokens = Lex("   \n\t  ");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Eof, tokens[0].Type);
    }

    // ── I/O keywords ──────────────────────────────────────────────────────────

    [Fact]
    public void ReadIsKeyword()
    {
        var tokens = LexTokens("read");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Read, tokens[0].Type);
    }

    [Theory]
    [InlineData("line")]
    [InlineData("lines")]
    [InlineData("all")]
    [InlineData("input")]
    public void IOContextualWordsAreIdentifiers(string word)
    {
        // Contextual words parsed by lexeme inside read expressions — not reserved keywords.
        var tokens = LexTokens(word);
        Assert.Single(tokens);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
    }

    // ── I/O Slice 3: file keywords ────────────────────────────────────────────

    [Fact]
    public void FileIsKeyword()
    {
        var tokens = LexTokens("file");
        Assert.Single(tokens);
        Assert.Equal(TokenType.File, tokens[0].Type);
    }

    [Fact]
    public void WriteIsKeyword()
    {
        var tokens = LexTokens("write");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Write, tokens[0].Type);
    }

    [Fact]
    public void AppendIsKeyword()
    {
        var tokens = LexTokens("append");
        Assert.Single(tokens);
        Assert.Equal(TokenType.Append, tokens[0].Type);
    }
}
