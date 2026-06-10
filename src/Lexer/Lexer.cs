namespace Cufet.Lexer;

public sealed class Lexer
{
    private readonly string _source;
    private int _pos;
    private int _line;

    public Lexer(string source)
    {
        _source = source;
        _pos = 0;
        _line = 1;
    }

    public IReadOnlyList<Token> Tokenize()
    {
        var tokens = new List<Token>();

        while (!AtEnd())
        {
            SkipWhitespace();
            if (AtEnd()) break;

            char c = Peek();

            if (char.IsLetter(c))
                tokens.Add(ReadWord());
            else if (char.IsDigit(c))
                tokens.Add(ReadNumber());
            else if (c == '"')
                tokens.Add(ReadString());
            else if (c is '+' or '-' or '*' or '/' or '%' or '(' or ')' or '=' or '<' or '>' or ':' or ',')
                tokens.Add(ReadSymbol());
            // DECIDED, DEFERRED:
            //   <<...>> — verbatim strings with distinct open/close delimiters; nestable by depth-counting <</>>.
            //   exactly — raw modifier (exactly "..." / exactly <<...>>) that suppresses interpretation.
            //   Both wait until escape sequences exist and need a contrast.
            else if (c == '.')
            {
                tokens.Add(new Token(TokenType.Dot, ".", _line));
                Advance();
            }
            else
                throw new LexerException(_line, c);
        }

        tokens.Add(new Token(TokenType.Eof, "", _line));
        return tokens;
    }

    private Token ReadWord()
    {
        int start = _pos;

        // Consume letters/digits and internal dashes.
        // A dash is only consumed when the next character is a letter or digit,
        // enforcing "internal" semantics from the grammar.
        while (!AtEnd())
        {
            char c = Peek();
            if (char.IsLetterOrDigit(c))
            {
                Advance();
            }
            else if (c == '-' && _pos + 1 < _source.Length && char.IsLetterOrDigit(_source[_pos + 1]))
            {
                Advance();
            }
            else
            {
                break;
            }
        }

        string lexeme     = _source[start.._pos];
        string normalized = lexeme.ToLowerInvariant();

        // Keywords are case-insensitive: match on the normalized form.
        TokenType type = normalized switch
        {
            "it"        => TokenType.It,
            "a"         => TokenType.Article,
            "an"        => TokenType.Article,
            "the"       => TokenType.Article,
            "state"     => TokenType.State,
            "define"    => TokenType.Define,
            "as"        => TokenType.As,
            "becomes"   => TokenType.Becomes,
            "if"        => TokenType.If,
            "otherwise" => TokenType.Otherwise,
            "done"      => TokenType.Done,
            "is"        => TokenType.Is,
            "not"       => TokenType.Not,
            "greater"   => TokenType.Greater,
            "less"      => TokenType.Less,
            "than"      => TokenType.Than,
            "or"        => TokenType.Or,
            "more"      => TokenType.More,
            "while"     => TokenType.While,
            "repeat"    => TokenType.Repeat,
            "until"     => TokenType.Until,
            "stop"      => TokenType.Stop,
            "skip"      => TokenType.Skip,
            "series"    => TokenType.Series,
            "with"      => TokenType.With,
            "for"       => TokenType.For,
            "each"      => TokenType.Each,
            "in"        => TokenType.In,
            "first"     => TokenType.Ordinal,
            "second"    => TokenType.Ordinal,
            "third"     => TokenType.Ordinal,
            "fourth"    => TokenType.Ordinal,
            "fifth"     => TokenType.Ordinal,
            "sixth"     => TokenType.Ordinal,
            "seventh"   => TokenType.Ordinal,
            "eighth"    => TokenType.Ordinal,
            "ninth"     => TokenType.Ordinal,
            "tenth"     => TokenType.Ordinal,
            "last"      => TokenType.Ordinal,
            "item"      => TokenType.Item,
            "of"        => TokenType.Of,
            "number"    => TokenType.NumberKw,
            "add"       => TokenType.Add,
            "to"        => TokenType.To,
            "start"     => TokenType.Start,
            "after"     => TokenType.After,
            "remove"    => TokenType.Remove,
            "from"      => TokenType.From,
            "bind"      => TokenType.Bind,
            "cast"      => TokenType.Cast,
            "given"     => TokenType.Given,
            "return"    => TokenType.Return,
            "void"      => TokenType.Void,
            "on"        => TokenType.On,
            "function"  => TokenType.FunctionKw,
            _           => TokenType.Identifier,
        };

        // Identifiers must start with a lowercase letter — uppercase-initial is reserved
        // for keywords and produces a visible distinction between keywords and variables.
        if (type == TokenType.Identifier && !char.IsLower(lexeme[0]))
            throw new LexerException(_line, $"identifier '{lexeme}' must start with a lowercase letter");

        return new Token(type, lexeme, _line);
    }

    private Token ReadSymbol()
    {
        char c = Peek();
        Advance();
        switch (c)
        {
            case '+': return new Token(TokenType.Plus,   "+", _line);
            case '-': return new Token(TokenType.Minus,  "-", _line);
            case '*': return new Token(TokenType.Star,    "*", _line);
            case '/': return new Token(TokenType.Slash,  "/", _line);
            case '%': return new Token(TokenType.Percent, "%", _line);
            case '(': return new Token(TokenType.LParen, "(", _line);
            case ')': return new Token(TokenType.RParen, ")", _line);
            case '=': return new Token(TokenType.Equal, "=", _line);
            case ':': return new Token(TokenType.Colon,  ":", _line);
            case ',': return new Token(TokenType.Comma,  ",", _line);
            case '<':
                if (!AtEnd() && Peek() == '=') { Advance(); return new Token(TokenType.Lte, "<=", _line); }
                return new Token(TokenType.Lt, "<", _line);
            case '>':
                if (!AtEnd() && Peek() == '=') { Advance(); return new Token(TokenType.Gte, ">=", _line); }
                return new Token(TokenType.Gt, ">", _line);
            default:
                throw new InvalidOperationException($"ReadSymbol called on non-symbol '{c}'");
        }
    }

    private Token ReadNumber()
    {
        int start = _pos;
        while (!AtEnd() && char.IsDigit(Peek()))
            Advance();
        // A '.' is a decimal point only when the very next character is a digit.
        // Otherwise the number ends here and the dot becomes a statement terminator.
        if (!AtEnd() && Peek() == '.' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1]))
        {
            Advance(); // consume '.'
            while (!AtEnd() && char.IsDigit(Peek()))
                Advance();
        }
        return new Token(TokenType.Number, _source[start.._pos], _line);
    }

    private Token ReadString()
    {
        Advance(); // consume opening '"'
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            if (AtEnd())
                throw new LexerException(_line, "unterminated string literal");
            char c = Peek();
            if (c == '"')
            {
                Advance();
                if (!AtEnd() && Peek() == '"') { Advance(); sb.Append('"'); } // "" → "
                else break;                                                    // closing quote
            }
            else
            {
                if (c == '\n') _line++;
                Advance();
                sb.Append(c);
            }
        }
        return new Token(TokenType.String, sb.ToString(), _line);
    }

    private void SkipWhitespace()
    {
        while (!AtEnd())
        {
            char c = Peek();
            if (c == '\n') { _line++; Advance(); }
            else if (char.IsWhiteSpace(c)) Advance();
            else break;
        }
    }

    private char Peek() => _source[_pos];
    private void Advance() => _pos++;
    private bool AtEnd() => _pos >= _source.Length;
}
