namespace Cufet.Lexer;

public sealed class Lexer
{
    private readonly string _source;
    private int _pos;
    private int _line;
    // Offset of the first character of the current line. Every '\n' that bumps _line moves this
    // to just past that newline, so a column is a subtraction rather than a rescan.
    private int _lineStart;

    public Lexer(string source)
    {
        _source = source;
        _pos = 0;
        _line = 1;
        _lineStart = 0;
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
        tokens.Add(new Token(TokenType.Eof, "", _line, ColumnAt(_pos)));
        return tokens;
    }

    // 1-based column of the character at `offset`, which must sit on the current line.
    private int ColumnAt(int offset) => offset - _lineStart + 1;

    // Words that OPEN a statement but are deliberately left unreserved, so a program can still
    // use them as ordinary names. Only these may be written with a capital and still lex — see
    // the note in ReadWord for why that is free rather than a concession.
    private static readonly HashSet<string> CapitalisableStatementWords =
        new(StringComparer.OrdinalIgnoreCase) { "output", "seed" };

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
        // Tested BEFORE the symbol branch, which would otherwise take the first '<' as Lt.
        // Nothing is taken away by that precedence: '<' '<' is two comparisons in a row, which
        // no Cufet expression has.
        else if (c == '<' && Next() == '<')
            tokens.Add(ReadRawText());
        // '[' has no other job in Cufet — it is the last free delimiter, and it goes to the one
        // construct whose contents are not Cufet at all. Nothing here looks INSIDE the brackets.
        else if (c == '[')
            tokens.Add(ReadAxiomText());
        else if (c is '+' or '-' or '*' or '/' or '%' or '(' or ')' or '=' or '<' or '>' or ':' or ',' or '{' or '}' or '|')
            tokens.Add(ReadSymbol());
        else if (c == '\'')
            tokens.Add(ReadPossessive());
        else if (c == '.')
        {
            tokens.Add(new Token(TokenType.Dot, ".", _line, ColumnAt(_pos)));
            Advance();
        }
        else
            throw new LexerException(_line, ColumnAt(_pos), c);
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
            "when"      => TokenType.When,
            "judge"     => TokenType.Judge,
            "where"     => TokenType.Where,
            "descend"   => TokenType.Descend,
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
            "increment" => TokenType.Increment,
            "decrement" => TokenType.Decrement,
            "insert"    => TokenType.Insert,
            "into"      => TokenType.Into,
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
            "bury"      => TokenType.Bury,
            "unbury"    => TokenType.Unbury,
            "stash"     => TokenType.Stash,
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
            // 'random' and 'randomly' are contextual — see Parser.EffectiveType. Their shapes
            // ('a random number/item/guess', 'randomly shuffled X') each have a mandatory next
            // word, which is what lets a variable of the same name still parse as a variable.
            // 'shuffled' and 'guess' are CONTEXTUAL — see Parser.IsWord. The standard library
            // does not get to take a name from every program that never pulls its book.
            // 'seed' is CONTEXTUAL too — see Parser.IsSeedStatement. It was the one piece of book
            // vocabulary held back, on the reasoning that 'Seed the chance with <n>.' is written
            // capitalised and an identifier must start lowercase. Capitalised contextual statement
            // words settled that (see CapitalisableStatementWords), so the word goes back to the
            // programs that never pull the book — `Define seed as 42.` is a natural thing to write
            // in exactly the code that would.
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
            // ★ `rabbit` is NOT reserved. It is a module's name, exactly like `math`,
            // `collections` and `chance` — none of which is a keyword either — and reserving it
            // was the last thing making the rabbit a privileged builtin rather than a module
            // that ships in the box. The parser recognises the NAME where it needs to.
            "have"       => TokenType.HaveKw,
            "task"       => TokenType.TaskKw,
            "channel"    => TokenType.Channel,
            "send"       => TokenType.Send,
            "through"    => TokenType.Through,
            "delivery"   => TokenType.Delivery,
            "close"      => TokenType.Close,
            "awaited"    => TokenType.Awaited,
            "pull"       => TokenType.Pull,
            // ★ `book` and `books` are NOT reserved. They appear in exactly one spelling —
            // `Pull a book on <name>.` — and a word spent on a single construct is a name a
            // writer loses forever: `For each book in books` is a line this language should be
            // able to write. The parser recognises them positionally, where `on` follows.
            // 'matrix' is contextual — 'a matrix with …' in expression position (the 'with' is
            // mandatory, so it disambiguates) and by lexeme in type position.
            // 'at' is contextual — only in 'the item at (row, col) of <matrix>'.
            "catalogue"  => TokenType.CatalogueKw,
            "atlas"      => TokenType.AtlasKw,
            // 'rows' and 'columns' are contextual. They reach the ordinary named-access path
            // ('the rows of m'), and the TYPE of the target decides whether that means a
            // matrix's row count or a record's field — the same way 'the key of mapping'
            // already resolves. Reserving them would cost every program two of the names most
            // likely to be wanted for tabular data.
            // 'filled' is contextual — only in 'a matrix with R by C filled with V'.
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

        // A contextual statement word may also be written with a capital. Such a word is NOT
        // reserved — a program can still call a variable `output` — and this costs nothing to
        // allow, because the CAPITALISED spelling could never have been an identifier in the
        // first place: the rule immediately below already forbids it. Reserving the lowercase
        // form would take a name away from every program; permitting the capitalised one takes
        // none, and it lets a statement begin with a capital like every other statement.
        //
        // The lexeme is kept as it was typed. These are matched case-insensitively by the parser
        // where the statement's shape allows, so the capital is meaningful only in that position:
        // `Output` is not another way to spell a variable named `output`.
        if (type == TokenType.Identifier && !char.IsLower(lexeme[0])
            && CapitalisableStatementWords.Contains(normalized))
            return new Token(type, lexeme, _line, ColumnAt(start));

        // Identifiers must start with a lowercase letter — uppercase-initial is reserved
        // for keywords and produces a visible distinction between keywords and variables.
        if (type == TokenType.Identifier && !char.IsLower(lexeme[0]))
            throw new LexerException(_line, ColumnAt(start), $"identifier '{lexeme}' must start with a lowercase letter");

        return new Token(type, lexeme, _line, ColumnAt(start));
    }

    private Token ReadSymbol()
    {
        int  start = _pos;
        int  col   = ColumnAt(start);
        char c     = Peek();
        Advance();
        switch (c)
        {
            case '+': return new Token(TokenType.Plus,   "+", _line, col);
            case '-': return new Token(TokenType.Minus,  "-", _line, col);
            case '*': return new Token(TokenType.Star,    "*", _line, col);
            case '/': return new Token(TokenType.Slash,  "/", _line, col);
            case '%': return new Token(TokenType.Percent, "%", _line, col);
            case '(': return new Token(TokenType.LParen, "(", _line, col);
            case ')': return new Token(TokenType.RParen, ")", _line, col);
            case '=': return new Token(TokenType.Equal, "=", _line, col);
            case ':': return new Token(TokenType.Colon,  ":", _line, col);
            case ',': return new Token(TokenType.Comma,  ",", _line, col);
            case '{': return new Token(TokenType.LBrace, "{", _line, col);
            case '}': return new Token(TokenType.RBrace, "}", _line, col);
            case '|': return new Token(TokenType.Pipe,   "|", _line, col);
            case '<':
                if (!AtEnd() && Peek() == '=') { Advance(); return new Token(TokenType.Lte, "<=", _line, col); }
                return new Token(TokenType.Lt, "<", _line, col);
            case '>':
                if (!AtEnd() && Peek() == '=') { Advance(); return new Token(TokenType.Gte, ">=", _line, col); }
                return new Token(TokenType.Gt, ">", _line, col);
            default:
                throw new InvalidOperationException($"ReadSymbol called on non-symbol '{c}'");
        }
    }

    private Token ReadPossessive()
    {
        int col = ColumnAt(_pos);
        Advance(); // consume '\''
        if (!AtEnd() && Peek() == 's')
        {
            Advance(); // consume 's'
            return new Token(TokenType.Possessive, "'s", _line, col);
        }
        throw new LexerException(_line, col, '\'');
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
        int startCol  = ColumnAt(_pos);
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
                    throw new LexerException(startLine, startCol,
                        $"'_' must sit between digits in a 0{prefix} literal");
                Advance();
                continue;
            }
            if (!IsDigitOfBase(c, prefix)) break;
            digits.Append(c);
            Advance();
        }

        if (digits.Length == 0)
            throw new LexerException(startLine, startCol,
                $"'0{prefix}' needs at least one {BaseName(prefix)} digit after it");

        // A letter or digit still sitting here is a digit of the wrong base — 0b12, 0xG1 — and
        // saying which base it was written in is far more use than "unexpected character".
        if (!AtEnd() && char.IsLetterOrDigit(Peek()))
            throw new LexerException(startLine, startCol,
                $"'{Peek()}' is not a {BaseName(prefix)} digit");

        int width = digits.Length * BitsPerDigit(prefix);
        if (width > 64)
            throw new LexerException(startLine, startCol,
                $"this literal is {width} bits wide, and bits values hold at most 64 — " +
                $"64 bits covers every C flag set and address, so anything wider belongs in a " +
                $"library reached through the foreign function interface");

        return new Token(TokenType.Bits, $"0{char.ToLowerInvariant(prefix)}{digits}", startLine, startCol);
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
        return new Token(TokenType.Number, _source[start.._pos], _line, ColumnAt(start));
    }

    // Scans a string literal starting at the current '"'. For plain strings (no bare
    // '{') appends a single String token. For interpolated strings, appends the
    // sequence: InterpolOpen, (StringPiece | InterpolHoleOpen … InterpolHoleClose)*,
    // InterpolClose — allowing the parser to build the join-chain.
    private void ReadString(List<Token> tokens)
    {
        // A string literal may run across newlines, so its own position is captured at the
        // opening quote. The tokens emitted mid-scan (pieces, hole braces) each report where the
        // scan stands at the moment they are emitted, which is the position _line already names.
        int startLine = _line;
        int startCol  = ColumnAt(_pos);
        Advance(); // consume opening '"'
        var sb      = new System.Text.StringBuilder();
        bool isInterp = false;
        var  pieces   = new List<Token>(); // buffer used only when interpolation is found

        while (true)
        {
            if (AtEnd())
                throw new LexerException(_line, ColumnAt(_pos), "unterminated string literal");
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
                    throw new LexerException(_line, ColumnAt(_pos), "unterminated string literal");
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
                    _    => throw new LexerException(_line, ColumnAt(_pos - 1), $"unrecognized escape sequence '\\{esc}'")
                });
                continue;
            }

            // ── Interpolation hole ──────────────────────────────────────────
            if (c == '{')
            {
                isInterp = true;
                if (sb.Length > 0)
                {
                    pieces.Add(new Token(TokenType.StringPiece, sb.ToString(), _line, ColumnAt(_pos)));
                    sb.Clear();
                }
                Advance(); // consume '{'
                pieces.Add(new Token(TokenType.InterpolHoleOpen, "{", _line, ColumnAt(_pos - 1)));

                SkipWhitespace();
                if (AtEnd() || Peek() == '}')
                    throw new LexerException(_line, ColumnAt(_pos), "empty interpolation — write an expression between the braces");

                // Lex expression tokens with brace-depth tracking.
                // Nested '{' (object literals etc.) increase depth; matching '}' decreases.
                int depth = 1;
                while (depth > 0)
                {
                    if (AtEnd())
                        throw new LexerException(_line, ColumnAt(_pos), "unterminated interpolation");
                    SkipWhitespace();
                    if (AtEnd())
                        throw new LexerException(_line, ColumnAt(_pos), "unterminated interpolation");
                    char ec = Peek();

                    if (ec == '{')
                    {
                        depth++;
                        Advance();
                        pieces.Add(new Token(TokenType.LBrace, "{", _line, ColumnAt(_pos - 1)));
                    }
                    else if (ec == '}')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            Advance();
                            pieces.Add(new Token(TokenType.InterpolHoleClose, "}", _line, ColumnAt(_pos - 1)));
                        }
                        else
                        {
                            Advance();
                            pieces.Add(new Token(TokenType.RBrace, "}", _line, ColumnAt(_pos - 1)));
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
            // A line break inside a literal is ONE '\n', whatever the file is stored as — see
            // the note on NewlineInLiteral.
            if (NewlineInLiteral()) { sb.Append('\n'); continue; }
            Advance();
            sb.Append(c);
        }

        if (!isInterp)
        {
            tokens.Add(new Token(TokenType.String, sb.ToString(), startLine, startCol));
        }
        else
        {
            if (sb.Length > 0)
                pieces.Add(new Token(TokenType.StringPiece, sb.ToString(), _line, ColumnAt(_pos)));
            tokens.Add(new Token(TokenType.InterpolOpen, "", startLine, startCol));
            tokens.AddRange(pieces);
            tokens.Add(new Token(TokenType.InterpolClose, "", _line, ColumnAt(_pos)));
        }
    }

    // Verbatim text: <<...>>. NOTHING inside is interpreted — no escape sequences, no
    // interpolation holes — so the delimiters are the only structure a reader has to see.
    //
    // That totality is the whole point, and it is why the form gets its own delimiters rather
    // than a modifier word on the quoted form. `"` and `{` are the two characters a quoted
    // literal cannot hold plainly, and they are precisely the two that JSON, regular
    // expressions, Windows paths and Cufet samples inside documentation are made of. A form
    // that suppressed only one of them would still need an escape for the other, and an
    // escape is the thing being escaped from.
    //
    // The cost, stated plainly: no interpolation here. `<<C:\Users\>> joined to name` builds
    // what a hole would have built, and joining already chains.
    //
    // NESTING is depth-counted over '<<' and '>>', exactly as block comments count '/*' and
    // '*/', and for the same reason — so that wrapping text that already contains the
    // delimiters works. Inner pairs are kept in the text; only the matching outer '>>' closes.
    //
    // The one thing this cannot spell is text ENDING in '>', since '>>>' closes at the first
    // two. Write that one with the quoted form. Every verbatim syntax has such a corner; this
    // one is cheap to state and cheap to sidestep.
    //
    // The token is an ordinary String. A verbatim literal is a spelling, not a type: once
    // lexed, nothing downstream can tell — or needs to tell — which form produced the text.
    private Token ReadRawText()
    {
        // Like a quoted literal, this may run across newlines, so its position is the opener's.
        int startLine = _line;
        int startCol  = ColumnAt(_pos);
        Advance(); // consume first '<'
        Advance(); // consume second '<'
        var sb = new System.Text.StringBuilder();
        int depth = 1;

        while (true)
        {
            if (AtEnd())
                throw new LexerException(startLine, startCol,
                    "unterminated verbatim text — expected '>>' to close it");
            char c = Peek();

            if (c == '<' && Next() == '<')
            {
                depth++;
                Advance();
                Advance();
                sb.Append("<<");
            }
            else if (c == '>' && Next() == '>')
            {
                Advance();
                Advance();
                if (--depth == 0) break;
                sb.Append(">>");
            }
            else
            {
                if (NewlineInLiteral()) { sb.Append('\n'); continue; }
                Advance();
                sb.Append(c);
            }
        }

        return new Token(TokenType.String, sb.ToString(), startLine, startCol);
    }

    // `[ ... ]` — foreign source, kept exactly as written.
    //
    // ★ Bracket PAIRS nest and survive, which is what makes `[getenv(argv[0])]` lex at all: C's
    // commonest use of a bracket is a subscript, so a scanner that stopped at the first ']' would
    // be unusable for the language this delimiter exists to carry. Same depth counting as
    // ReadRawText, and for the same reason.
    //
    // ⚠ It counts brackets and nothing else — an UNBALANCED bracket inside a foreign string
    // literal (`[printf("]")]`) closes the axiom early. `<<...>>` has the same edge and the same
    // answer, and closing it here would mean knowing which foreign language this is, which the
    // brackets deliberately do not say.
    private Token ReadAxiomText()
    {
        // Foreign source is normally multi-line, so the position reported is the opener's.
        int startLine = _line;
        int startCol  = ColumnAt(_pos);
        Advance(); // consume '['
        var sb = new System.Text.StringBuilder();
        int depth = 1;

        while (true)
        {
            if (AtEnd())
                throw new LexerException(startLine, startCol,
                    "unterminated foreign source — expected ']' to close it");
            char c = Peek();

            if (c == '[')
            {
                depth++;
                Advance();
                sb.Append('[');
            }
            else if (c == ']')
            {
                Advance();
                if (--depth == 0) break;
                sb.Append(']');
            }
            else
            {
                if (NewlineInLiteral()) { sb.Append('\n'); continue; }
                Advance();
                sb.Append(c);
            }
        }

        return new Token(TokenType.Axiom, sb.ToString(), startLine, startCol);
    }

    // A line break inside a text literal, consumed and counted. Returns false if the position is
    // not one, having consumed nothing.
    //
    // ★ A break is ONE '\n' in the value regardless of how the file stores it, so a CRLF source
    // does not silently put a '\r' into the text. This is a LANGUAGE RULE, not a convenience:
    // without it the same program means different things depending on how git checked it out —
    // `the length of` differs, a comparison against "a\nb" fails — and a language that already
    // makes "a character is a Unicode code point" a rule on both backends cannot leave this to
    // the working tree. It matters most for verbatim text, where a multi-line literal is the
    // normal way to write one and there is no escape to reach for instead.
    //
    // Lone CR is left alone: it is not a line break on any platform Cufet targets, so a program
    // that has one meant a carriage return and gets one.
    private bool NewlineInLiteral()
    {
        if (Peek() == '\r' && Next() == '\n') Advance();   // fall through to the '\n'
        else if (Peek() != '\n') return false;

        _line++;
        Advance();
        _lineStart = _pos;
        return true;
    }

    private void SkipWhitespace()
    {
        while (!AtEnd())
        {
            char c = Peek();
            if (c == '\n') { _line++; _lineStart = _pos + 1; Advance(); }
            else if (char.IsWhiteSpace(c)) Advance();
            else if (c == '/' && Next() == '/') SkipLineComment();
            else if (c == '/' && Next() == '*') SkipBlockComment();
            else break;
        }
    }

    // Consumes a // comment: everything to the end of the line.
    //
    // The newline itself is deliberately LEFT for SkipWhitespace to consume, so that _line is
    // incremented in exactly one place. A comment on the last line of a file simply ends at EOF.
    //
    // There is no ambiguity with division. '/' is a single-character token with no lookahead of
    // its own, so a source '//' could only ever have parsed as division by a unary slash, which
    // is not an expression Cufet has. Nothing valid is being taken away.
    private void SkipLineComment()
    {
        while (!AtEnd() && Peek() != '\n') Advance();
    }

    // Consumes a /* ... */ comment.
    //
    // NESTING: an inner '/*' opens a nested comment, and the outer one ends only at the '*/'
    // that closes it. This is what makes "comment out this whole block while I test something"
    // work when the block already contains comments — the most common editing operation there
    // is, and the one C's non-nesting comments famously break. Rust, Swift and D spell their
    // block comments exactly this way AND nest them, so this is the familiar surface with the
    // better semantics, not a departure from it.
    //
    // It costs a depth counter, not a grammar rule: comments are scanned here in the lexer, so
    // counting is just an integer in a loop that already carries state.
    //
    // Unterminated (depth never returns to zero before EOF) is a lexer error naming the line
    // the OUTERMOST comment opened on — that is the one the author has to go find.
    private void SkipBlockComment()
    {
        int startLine = _line;
        int startCol  = ColumnAt(_pos);
        int depth = 1;
        Advance(); // consume '/'
        Advance(); // consume '*'
        while (true)
        {
            if (AtEnd())
                throw new LexerException(startLine, startCol, "unterminated comment — expected '*/' to close it");
            char c = Peek();
            if (c == '\n') { _line++; _lineStart = _pos + 1; Advance(); }
            else if (c == '/' && Next() == '*')
            {
                depth++;
                Advance(); // consume '/'
                Advance(); // consume '*'
            }
            else if (c == '*' && Next() == '/')
            {
                Advance(); // consume '*'
                Advance(); // consume '/'
                if (--depth == 0) return;
            }
            else Advance();
        }
    }

    private char Peek() => _source[_pos];

    // One character of lookahead, '\0' past the end. Both comment forms are two characters, so
    // every one of their checks needs this; returning a sentinel rather than making each caller
    // bounds-check keeps those conditions readable.
    private char Next() => _pos + 1 < _source.Length ? _source[_pos + 1] : '\0';

    private void Advance() => _pos++;
    private bool AtEnd() => _pos >= _source.Length;
}
