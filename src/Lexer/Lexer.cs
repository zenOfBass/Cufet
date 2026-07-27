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
            ReadOneToken(tokens);
        }
        tokens.Add(new Token(TokenType.Eof, "", _line));
        return tokens;
    }

    // Reads exactly one logical token from the current position and appends it (or its
    // sequence, in the case of an interpolated string) to `tokens`.
    private void ReadOneToken(List<Token> tokens)
    {
        char c = Peek();
        if (char.IsLetter(c))
            tokens.Add(ReadWord());
        else if (char.IsDigit(c))
            tokens.Add(ReadNumber());
        else if (c == '"')
            ReadString(tokens);
        else if (c is '+' or '-' or '*' or '/' or '%' or '(' or ')' or '=' or '<' or '>' or ':' or ',' or '{' or '}' or '|')
            tokens.Add(ReadSymbol());
        else if (c == '\'')
            tokens.Add(ReadPossessive());
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
            "and"       => TokenType.And,
            "xor"       => TokenType.Xor,
            "more"      => TokenType.More,
            "while"     => TokenType.While,
            "repeat"    => TokenType.Repeat,
            "until"     => TokenType.Until,
            "stop"      => TokenType.Stop,
            "skip"      => TokenType.Skip,
            "series"    => TokenType.Series,
            "record"    => TokenType.Record,
            "with"      => TokenType.With,
            "like"      => TokenType.Like,
            "object"    => TokenType.Object,
            "interface" => TokenType.Interface,
            "new"       => TokenType.New,
            "one"       => TokenType.One,
            "for"       => TokenType.For,
            "each"      => TokenType.Each,
            "in"        => TokenType.In,
            // first/second/.../tenth/last are contextual identifiers, NOT globally reserved.
            // They are recognized as ordinal accessors only in the "the <ordinal> of <series>" shape.
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
            "length"    => TokenType.LengthKw,
            "joined"    => TokenType.Joined,
            "converted" => TokenType.Converted,
            "range"     => TokenType.Range,
            "counting"  => TokenType.Counting,
            "by"        => TokenType.By,
            "permanently" => TokenType.Permanently,
            "shadow"      => TokenType.Shadow,
            "voidable"  => TokenType.Voidable,
            "but"       => TokenType.But,
            "map"       => TokenType.Map,
            "has"       => TokenType.Has,
            "key"       => TokenType.Key,
            "entry"     => TokenType.Entry,
            "size"      => TokenType.Size,
            "split"      => TokenType.Split,
            "contains"   => TokenType.Contains,
            "position"   => TokenType.Position,
            "characters" => TokenType.Characters,
            "end"        => TokenType.End,
            "replace"    => TokenType.Replace,
            "uppercase"  => TokenType.Uppercase,
            "lowercase"  => TokenType.Lowercase,
            "trimmed"    => TokenType.Trimmed,
            "shifted"    => TokenType.Shifted,
            "sorted"     => TokenType.Sorted,
            "reverse"    => TokenType.Reverse,
            "random"     => TokenType.Random,
            "randomly"   => TokenType.Randomly,
            "shuffled"   => TokenType.Shuffled,
            "guess"      => TokenType.Guess,
            "seed"       => TokenType.SeedKw,
            "unto"       => TokenType.Unto,
            "get"        => TokenType.GetKw,
            "set"        => TokenType.SetKw,
            "making"     => TokenType.MakingKw,
            "unmaking"    => TokenType.UnmakingKw,
            "overloading" => TokenType.OverloadingKw,
            "failure"    => TokenType.Failure,
            "category"   => TokenType.Category,
            "try"        => TokenType.Try,
            "case"       => TokenType.Case,
            "pass"       => TokenType.Pass,
            "off"        => TokenType.Off,
            "exception"  => TokenType.Exception,
            "suppress"   => TokenType.Suppress,
            "read"       => TokenType.Read,
            "file"       => TokenType.File,
            "write"      => TokenType.Write,
            "append"     => TokenType.Append,
            "run"        => TokenType.Run,
            "stream"     => TokenType.Stream,
            "open"       => TokenType.Open,
            "rabbit"     => TokenType.Rabbit,
            "have"       => TokenType.HaveKw,
            "task"       => TokenType.TaskKw,
            "channel"    => TokenType.Channel,
            "send"       => TokenType.Send,
            "through"    => TokenType.Through,
            "delivery"   => TokenType.Delivery,
            "close"      => TokenType.Close,
            "awaited"    => TokenType.Awaited,
            "pull"       => TokenType.Pull,
            "book"       => TokenType.Book,
            "books"      => TokenType.Books,
            "matrix"     => TokenType.Matrix,
            "at"         => TokenType.At,
            "catalogue"  => TokenType.CatalogueKw,
            "atlas"      => TokenType.AtlasKw,
            "rows"       => TokenType.RowsKw,
            "columns"    => TokenType.ColumnsKw,
            "filled"     => TokenType.FilledKw,
            "contents"    => TokenType.ContentsKw,
            "directory"   => TokenType.DirectoryKw,
            "path"        => TokenType.PathKw,
            "environment" => TokenType.EnvironmentKw,
            "interrupt"   => TokenType.InterruptKw,
            "acknowledge" => TokenType.AcknowledgeKw,
            "yield"       => TokenType.YieldKw,
            "true"        => TokenType.TrueKw,
            "false"       => TokenType.FalseKw,
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
            case '{': return new Token(TokenType.LBrace, "{", _line);
            case '}': return new Token(TokenType.RBrace, "}", _line);
            case '|': return new Token(TokenType.Pipe,   "|", _line);
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

    private Token ReadPossessive()
    {
        Advance(); // consume '\''
        if (!AtEnd() && Peek() == 's')
        {
            Advance(); // consume 's'
            return new Token(TokenType.Possessive, "'s", _line);
        }
        throw new LexerException(_line, '\'');
    }

    // Bit-pattern literals: 0x hex, 0b binary, 0o octal. There is deliberately no bare-0 octal
    // (C's footgun, where 0755 silently means 493). Prefixes and hex digits are case-insensitive,
    // matching keywords.
    //
    // '_' groups digits and is dropped. It is allowed ONLY here, never in decimal: grouping in
    // these bases is structural — nibbles, bytes, permission triples — while in decimal it is
    // cosmetic and in a fraction it marks nothing at all.
    //
    // The WIDTH is the digit count times the bits each digit carries, so 0x0F is 8 bits and 0xF
    // is 4. Leading zeros are therefore significant, which is genuinely unlike C, Java, Rust, Go
    // and Python, where 0x0F and 0xF are the same value and width comes from the declared type.
    // It is what lets `not` be obvious: `not 0xFF` is 0x00, with no negative numbers in sight.
    private static int BitsPerDigit(char prefix) => char.ToLowerInvariant(prefix) switch
    {
        'x' => 4,
        'o' => 3,
        'b' => 1,
        _   => throw new InvalidOperationException($"BitsPerDigit called on non-prefix '{prefix}'"),
    };

    private static bool IsDigitOfBase(char c, char prefix) => char.ToLowerInvariant(prefix) switch
    {
        'x' => Uri.IsHexDigit(c),
        'o' => c is >= '0' and <= '7',
        'b' => c is '0' or '1',
        _   => false,
    };

    private static string BaseName(char prefix) => char.ToLowerInvariant(prefix) switch
    {
        'x' => "hex", 'o' => "octal", 'b' => "binary", _ => "unknown",
    };

    private Token ReadBits()
    {
        int startLine = _line;
        Advance();                      // consume '0'
        char prefix = Peek();
        Advance();                      // consume the base prefix

        var digits = new System.Text.StringBuilder();
        while (!AtEnd())
        {
            char c = Peek();
            if (c == '_')
            {
                // A separator has to sit between digits; a leading, trailing or doubled one is
                // a typo, and silently accepting it would let 0xFF__ and 0xFF look different
                // while meaning the same thing.
                if (digits.Length == 0 || _pos + 1 >= _source.Length || !IsDigitOfBase(_source[_pos + 1], prefix))
                    throw new LexerException(startLine,
                        $"'_' must sit between digits in a 0{prefix} literal");
                Advance();
                continue;
            }
            if (!IsDigitOfBase(c, prefix)) break;
            digits.Append(c);
            Advance();
        }

        if (digits.Length == 0)
            throw new LexerException(startLine,
                $"'0{prefix}' needs at least one {BaseName(prefix)} digit after it");

        // A letter or digit still sitting here is a digit of the wrong base — 0b12, 0xG1 — and
        // saying which base it was written in is far more use than "unexpected character".
        if (!AtEnd() && char.IsLetterOrDigit(Peek()))
            throw new LexerException(startLine,
                $"'{Peek()}' is not a {BaseName(prefix)} digit");

        int width = digits.Length * BitsPerDigit(prefix);
        if (width > 64)
            throw new LexerException(startLine,
                $"this literal is {width} bits wide, and bits values hold at most 64 — " +
                $"64 bits covers every C flag set and address, so anything wider belongs in a " +
                $"library reached through the foreign function interface");

        return new Token(TokenType.Bits, $"0{char.ToLowerInvariant(prefix)}{digits}", startLine);
    }

    private Token ReadNumber()
    {
        // 0x / 0b / 0o open a bit pattern rather than a quantity. Any other digit after a
        // leading 0 stays decimal — 0755 is seven hundred and fifty-five.
        if (Peek() == '0' && _pos + 1 < _source.Length
            && char.ToLowerInvariant(_source[_pos + 1]) is 'x' or 'b' or 'o')
            return ReadBits();

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

    // Scans a string literal starting at the current '"'. For plain strings (no bare
    // '{') appends a single String token. For interpolated strings, appends the
    // sequence: InterpolOpen, (StringPiece | InterpolHoleOpen … InterpolHoleClose)*,
    // InterpolClose — allowing the parser to build the join-chain.
    private void ReadString(List<Token> tokens)
    {
        int startLine = _line;
        Advance(); // consume opening '"'
        var sb      = new System.Text.StringBuilder();
        bool isInterp = false;
        var  pieces   = new List<Token>(); // buffer used only when interpolation is found

        while (true)
        {
            if (AtEnd())
                throw new LexerException(_line, "unterminated string literal");
            char c = Peek();

            // ── Closing quote ───────────────────────────────────────────────
            if (c == '"')
            {
                Advance();
                break;
            }

            // ── Escape sequence ─────────────────────────────────────────────
            if (c == '\\')
            {
                Advance();
                if (AtEnd())
                    throw new LexerException(_line, "unterminated string literal");
                char esc = Peek();
                Advance();
                // \{ and \} produce a literal brace — they are NOT interpolation markers.
                sb.Append(esc switch
                {
                    'n'  => '\n',
                    't'  => '\t',
                    'r'  => '\r',
                    '\\' => '\\',
                    '"'  => '"',
                    '{'  => '{',
                    '}'  => '}',
                    _    => throw new LexerException(_line, $"unrecognized escape sequence '\\{esc}'")
                });
                continue;
            }

            // ── Interpolation hole ──────────────────────────────────────────
            if (c == '{')
            {
                isInterp = true;
                if (sb.Length > 0)
                {
                    pieces.Add(new Token(TokenType.StringPiece, sb.ToString(), _line));
                    sb.Clear();
                }
                Advance(); // consume '{'
                pieces.Add(new Token(TokenType.InterpolHoleOpen, "{", _line));

                SkipWhitespace();
                if (AtEnd() || Peek() == '}')
                    throw new LexerException(_line, "empty interpolation — write an expression between the braces");

                // Lex expression tokens with brace-depth tracking.
                // Nested '{' (object literals etc.) increase depth; matching '}' decreases.
                int depth = 1;
                while (depth > 0)
                {
                    if (AtEnd())
                        throw new LexerException(_line, "unterminated interpolation");
                    SkipWhitespace();
                    if (AtEnd())
                        throw new LexerException(_line, "unterminated interpolation");
                    char ec = Peek();

                    if (ec == '{')
                    {
                        depth++;
                        Advance();
                        pieces.Add(new Token(TokenType.LBrace, "{", _line));
                    }
                    else if (ec == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            Advance();
                            pieces.Add(new Token(TokenType.InterpolHoleClose, "}", _line));
                        }
                        else
                        {
                            Advance();
                            pieces.Add(new Token(TokenType.RBrace, "}", _line));
                        }
                    }
                    else
                    {
                        // All other tokens (words, numbers, symbols, nested strings) —
                        // nested ReadString calls handle their own interpolation recursively.
                        ReadOneToken(pieces);
                    }
                }
                continue;
            }

            // ── Ordinary character ───────────────────────────────────────────
            if (c == '\n') _line++;
            Advance();
            sb.Append(c);
        }

        if (!isInterp)
        {
            tokens.Add(new Token(TokenType.String, sb.ToString(), startLine));
        }
        else
        {
            if (sb.Length > 0)
                pieces.Add(new Token(TokenType.StringPiece, sb.ToString(), _line));
            tokens.Add(new Token(TokenType.InterpolOpen, "", startLine));
            tokens.AddRange(pieces);
            tokens.Add(new Token(TokenType.InterpolClose, "", _line));
        }
    }

    private void SkipWhitespace()
    {
        while (!AtEnd())
        {
            char c = Peek();
            if (c == '\n') { _line++; Advance(); }
            else if (char.IsWhiteSpace(c)) Advance();
            else if (c == '[' && _pos + 1 < _source.Length && _source[_pos + 1] == '[')
                SkipComment();
            else break;
        }
    }

    // Consumes a [[ ... ]] comment. Called after consuming the opening '[['.
    //
    // NESTING: an inner '[[' opens a nested comment, and the outer one ends only at the ']]'
    // that closes it. This is what makes "comment out this whole block while I test something"
    // work when the block already contains comments — the most common editing operation there
    // is, and the one C's non-nesting comments famously break. Rust, Swift, Haskell and D all
    // nest for the same reason.
    //
    // It costs a depth counter, not a grammar rule: comments are scanned here in the lexer, so
    // counting is just an integer in a loop that already carries state.
    //
    // Unterminated (depth never returns to zero before EOF) is a lexer error naming the line
    // the OUTERMOST comment opened on — that is the one the author has to go find.
    private void SkipComment()
    {
        int startLine = _line;
        int depth = 1;
        Advance(); // consume first '['
        Advance(); // consume second '['
        while (true)
        {
            if (AtEnd())
                throw new LexerException(startLine, "unterminated comment — expected ']]' to close it");
            char c = Peek();
            if (c == '\n') { _line++; Advance(); }
            else if (c == '[' && _pos + 1 < _source.Length && _source[_pos + 1] == '[')
            {
                depth++;
                Advance(); // consume first '['
                Advance(); // consume second '['
            }
            else if (c == ']' && _pos + 1 < _source.Length && _source[_pos + 1] == ']')
            {
                Advance(); // consume first ']'
                Advance(); // consume second ']'
                if (--depth == 0) return;
            }
            else Advance();
        }
    }

    private char Peek() => _source[_pos];
    private void Advance() => _pos++;
    private bool AtEnd() => _pos >= _source.Length;
}
