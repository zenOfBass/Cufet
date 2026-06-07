using NLP.Lexer;

namespace NLP.Interpreter;

public sealed class Parser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;
    private int _loopDepth;

    public Parser(IReadOnlyList<Token> tokens) => _tokens = tokens;

    public Program Parse()
    {
        var stmts = new List<IStatement>();
        while (Peek().Type != TokenType.Eof)
        {
            SkipNoise();
            if (Peek().Type == TokenType.Eof) break;
            stmts.Add(ParseStatement());
        }
        return new Program(stmts);
    }

    private IStatement ParseStatement()
    {
        var tok = Peek();
        return tok.Type switch
        {
            TokenType.State      => ParseStateStatement(),
            TokenType.Define     => ParseDefineStatement(),
            TokenType.Identifier => ParseBecomesStatement(),
            TokenType.If         => ParseIfStatement(),
            TokenType.While      => ParseWhileStatement(),
            TokenType.Repeat     => ParseRepeatUntilStatement(),
            TokenType.Stop       => ParseStopStatement(tok),
            TokenType.Skip       => ParseSkipStatement(tok),
            TokenType.Ordinal    => ParseSeriesSetStatement(),
            TokenType.Item       => ParseSeriesSetStatement(),
            TokenType.Add        => ParseSeriesAddStatement(),
            TokenType.Remove     => ParseSeriesRemoveStatement(),
            TokenType.For        => ParseForEachStatement(),
            _ => throw new ParseException(tok, "statement keyword"),
        };
    }

    private StateStatement ParseStateStatement()
    {
        Consume(TokenType.State);
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new StateStatement(value);
    }

    private DefineStatement ParseDefineStatement()
    {
        var line = Consume(TokenType.Define).Line;
        SkipNoise();
        var name = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();
        Consume(TokenType.As);
        SkipNoise();
        IExpression value = Peek().Type == TokenType.Series
            ? ParseSeriesLiteralExpr()
            : ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new DefineStatement(name, value, line);
    }

    private SeriesLiteral ParseSeriesLiteralExpr()
    {
        var seriesTok = Advance(); // consume "series"
        SkipNoise();

        CufetType? annotation = null;
        if (Peek().Type == TokenType.Of)
        {
            Advance(); // consume "of"
            SkipNoise();
            annotation = ParseTypeAnnotation();
            SkipNoise();
        }

        Consume(TokenType.LParen);
        var elements = new List<IExpression>();
        SkipNoise();
        if (Peek().Type != TokenType.RParen)
        {
            elements.Add(ParseExpression());
            SkipNoise();
            while (Peek().Type == TokenType.Comma)
            {
                Advance();
                SkipNoise();
                elements.Add(ParseExpression());
                SkipNoise();
            }
        }
        Consume(TokenType.RParen);
        return new SeriesLiteral(elements, annotation, seriesTok.Line);
    }

    // Parses the element-type annotation after "of":
    //   type-annotation → "number" | "numbers" | "text" | "fact" | "facts"
    //                   | "series" "of" type-annotation
    private CufetType ParseTypeAnnotation()
    {
        var tok = Peek();

        if (tok.Type == TokenType.NumberKw ||
            (tok.Type == TokenType.Identifier &&
             tok.Lexeme.Equals("numbers", StringComparison.OrdinalIgnoreCase)))
        {
            Advance();
            return new NumberType();
        }
        if (tok.Type == TokenType.Identifier &&
            (tok.Lexeme.Equals("text", StringComparison.OrdinalIgnoreCase) ||
             tok.Lexeme.Equals("texts", StringComparison.OrdinalIgnoreCase)))
        {
            Advance();
            return new TextType();
        }
        if (tok.Type == TokenType.Identifier &&
            (tok.Lexeme.Equals("fact", StringComparison.OrdinalIgnoreCase) ||
             tok.Lexeme.Equals("facts", StringComparison.OrdinalIgnoreCase)))
        {
            Advance();
            return new FactType();
        }
        if (tok.Type == TokenType.Series)
        {
            Advance(); // consume "series"
            SkipNoise();
            Consume(TokenType.Of);
            SkipNoise();
            return new SeriesType(ParseTypeAnnotation());
        }
        throw new ParseException(tok, "type name (number, text, fact, or series of ...)");
    }

    private BecomesStatement ParseBecomesStatement()
    {
        var tok  = Consume(TokenType.Identifier);
        var name = tok.Lexeme;
        var line = tok.Line;
        SkipNoise();
        Consume(TokenType.Becomes);
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new BecomesStatement(name, value, line);
    }

    private IfStatement ParseIfStatement()
    {
        var arms = new List<ConditionArm>();
        IReadOnlyList<IStatement>? elseBody = null;

        Consume(TokenType.If);
        SkipNoise();
        arms.Add(new ConditionArm(ParseCondition(), ParseIfBody()));

        while (true)
        {
            SkipNoise();
            if (Peek().Type != TokenType.Otherwise) break;
            Consume(TokenType.Otherwise);
            SkipNoise();
            if (Peek().Type == TokenType.If)
            {
                Consume(TokenType.If);
                SkipNoise();
                arms.Add(new ConditionArm(ParseCondition(), ParseIfBody()));
            }
            else
            {
                elseBody = ParseIfBody();
                break;
            }
        }

        return new IfStatement(arms, elseBody);
    }

    // Comma → inline single-statement (works anywhere, no Done.).
    // Colon → Done.-terminated block (same machinery as loop bodies).
    // The two forms are unambiguous: the parser knows which it's in from the
    // comma-vs-colon immediately after the condition, before the body is parsed.
    private IReadOnlyList<IStatement> ParseIfBody()
    {
        SkipNoise();
        if (Peek().Type == TokenType.Comma)
        {
            Advance(); // consume ','
            SkipNoise();
            return new IStatement[] { ParseStatement() };
        }
        Consume(TokenType.Colon);
        return ParseLoopBody();
    }

    private WhileStatement ParseWhileStatement()
    {
        Consume(TokenType.While);
        SkipNoise();
        var condition = ParseCondition();
        SkipNoise();
        Consume(TokenType.Comma);
        SkipNoise();
        Consume(TokenType.Repeat);
        SkipNoise();
        Consume(TokenType.Colon);
        _loopDepth++;
        var body = ParseLoopBody();
        _loopDepth--;
        return new WhileStatement(condition, body);
    }

    // Loop bodies always require Done. — no single-statement sugar.
    // A closer on the same line is fine: "While ...: x becomes x + 1. Done."
    private IReadOnlyList<IStatement> ParseLoopBody()
    {
        var stmts = new List<IStatement>();
        while (true)
        {
            SkipNoise();
            if (Peek().Type is TokenType.Done or TokenType.Eof) break;
            stmts.Add(ParseStatement());
        }
        if (stmts.Count == 0)
            throw new ParseException(Peek(), "at least one statement in loop body");
        Consume(TokenType.Done);
        Consume(TokenType.Dot);
        return stmts;
    }

    private RepeatUntilStatement ParseRepeatUntilStatement()
    {
        Consume(TokenType.Repeat);
        SkipNoise();
        Consume(TokenType.Colon);
        _loopDepth++;
        var body = ParseRepeatUntilBody();
        _loopDepth--;
        Consume(TokenType.Until);
        SkipNoise();
        var condition = ParseCondition();
        SkipNoise();
        Consume(TokenType.Dot);
        return new RepeatUntilStatement(body, condition);
    }

    private IReadOnlyList<IStatement> ParseRepeatUntilBody()
    {
        var stmts = new List<IStatement>();
        while (true)
        {
            SkipNoise();
            if (Peek().Type is TokenType.Until or TokenType.Eof) break;
            stmts.Add(ParseStatement());
        }
        if (stmts.Count == 0)
            throw new ParseException(Peek(), "at least one statement in repeat-until body");
        return stmts;
    }

    private StopStatement ParseStopStatement(Token tok)
    {
        if (_loopDepth == 0)
            throw new ParseException(tok, "'Stop' used outside a loop");
        Advance();
        Consume(TokenType.Dot);
        return new StopStatement();
    }

    private SkipStatement ParseSkipStatement(Token tok)
    {
        if (_loopDepth == 0)
            throw new ParseException(tok, "'Skip' used outside a loop");
        Advance();
        Consume(TokenType.Dot);
        return new SkipStatement();
    }

    // ── For-each loop ─────────────────────────────────────────────────────

    private ForEachStatement ParseForEachStatement()
    {
        var forTok = Consume(TokenType.For);
        SkipNoise();
        Consume(TokenType.Each);
        SkipNoise();

        string? iterName = null;
        if (Peek().Type == TokenType.Identifier)
        {
            iterName = Advance().Lexeme;
            SkipNoise();
        }

        Consume(TokenType.In);
        SkipNoise();
        var seriesName = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();
        Consume(TokenType.Comma);
        SkipNoise();
        Consume(TokenType.Repeat);
        SkipNoise();
        Consume(TokenType.Colon);
        _loopDepth++;
        var body = ParseLoopBody();
        _loopDepth--;
        return new ForEachStatement(iterName, seriesName, body, forTok.Line);
    }

    // ── Series operations ─────────────────────────────────────────────────

    // Parses "ORDINAL 'of' IDENTIFIER" or "'item' expr 'of' IDENTIFIER".
    // Returns (seriesName, index, line) where index==null means "last element".
    private (string name, IExpression? index, int line) ParseAccessTarget()
    {
        if (Peek().Type == TokenType.Ordinal)
        {
            var ordTok = Advance();
            var index  = OrdinalToIndex(ordTok.Lexeme);
            SkipNoise();
            Consume(TokenType.Of);
            SkipNoise();
            var name = Consume(TokenType.Identifier).Lexeme;
            return (name, index, ordTok.Line);
        }
        else
        {
            var itemTok = Consume(TokenType.Item);
            SkipNoise();
            var idx = ParseExpression();
            SkipNoise();
            Consume(TokenType.Of);
            SkipNoise();
            var name = Consume(TokenType.Identifier).Lexeme;
            return (name, idx, itemTok.Line);
        }
    }

    // null return → "last" sentinel
    private static IExpression? OrdinalToIndex(string lexeme) =>
        lexeme.ToLowerInvariant() switch
        {
            "first"   => new NumberLiteral(1),
            "second"  => new NumberLiteral(2),
            "third"   => new NumberLiteral(3),
            "fourth"  => new NumberLiteral(4),
            "fifth"   => new NumberLiteral(5),
            "sixth"   => new NumberLiteral(6),
            "seventh" => new NumberLiteral(7),
            "eighth"  => new NumberLiteral(8),
            "ninth"   => new NumberLiteral(9),
            "tenth"   => new NumberLiteral(10),
            "last"    => null,
            _         => throw new InvalidOperationException($"Unknown ordinal: {lexeme}"),
        };

    private SeriesSetStatement ParseSeriesSetStatement()
    {
        var (name, idx, line) = ParseAccessTarget();
        SkipNoise();
        Consume(TokenType.Becomes);
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new SeriesSetStatement(name, idx, value, line);
    }

    private SeriesAddStatement ParseSeriesAddStatement()
    {
        var addTok = Consume(TokenType.Add);
        int line = addTok.Line;
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();

        if (Peek().Type == TokenType.To)
        {
            Consume(TokenType.To);
            SkipNoise();
            if (Peek().Type == TokenType.Start)
            {
                Consume(TokenType.Start);
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                var sname = Consume(TokenType.Identifier).Lexeme;
                SkipNoise();
                Consume(TokenType.Dot);
                return new SeriesAddStatement(value, sname, null, true, line);
            }
            else
            {
                var sname = Consume(TokenType.Identifier).Lexeme;
                SkipNoise();
                Consume(TokenType.Dot);
                return new SeriesAddStatement(value, sname, null, false, line);
            }
        }
        else
        {
            Consume(TokenType.After);
            SkipNoise();
            IExpression? afterIdx;
            if (Peek().Type == TokenType.Ordinal)
            {
                afterIdx = OrdinalToIndex(Advance().Lexeme);
                SkipNoise();
                if (Peek().Type == TokenType.Item) Advance(); // optional decorative "item"
            }
            else
            {
                Consume(TokenType.Item);
                SkipNoise();
                afterIdx = ParseExpression();
            }
            SkipNoise();
            Consume(TokenType.Of);
            SkipNoise();
            var sname = Consume(TokenType.Identifier).Lexeme;
            SkipNoise();
            Consume(TokenType.Dot);
            return new SeriesAddStatement(value, sname, afterIdx, false, line);
        }
    }

    private IStatement ParseSeriesRemoveStatement()
    {
        var removeTok = Consume(TokenType.Remove);
        int line = removeTok.Line;
        SkipNoise();

        if (Peek().Type == TokenType.Ordinal)
        {
            var idx = OrdinalToIndex(Advance().Lexeme);
            SkipNoise();
            if (Peek().Type == TokenType.Item) Advance(); // optional decorative "item"
            SkipNoise();
            Consume(TokenType.From);
            SkipNoise();
            var sname = Consume(TokenType.Identifier).Lexeme;
            SkipNoise();
            Consume(TokenType.Dot);
            return new SeriesRemoveAtStatement(sname, idx, line);
        }
        else if (Peek().Type == TokenType.Item)
        {
            Consume(TokenType.Item);
            SkipNoise();
            var idx = ParseExpression();
            SkipNoise();
            Consume(TokenType.From);
            SkipNoise();
            var sname = Consume(TokenType.Identifier).Lexeme;
            SkipNoise();
            Consume(TokenType.Dot);
            return new SeriesRemoveAtStatement(sname, idx, line);
        }
        else
        {
            var val = ParseExpression();
            SkipNoise();
            Consume(TokenType.From);
            SkipNoise();
            var sname = Consume(TokenType.Identifier).Lexeme;
            SkipNoise();
            Consume(TokenType.Dot);
            return new SeriesRemoveValueStatement(sname, val, line);
        }
    }

    // Condition grammar (conditional context — after If / Otherwise if):
    //   condition     → addition ( is-comparison )?
    //   is-comparison → "is" "not" addition
    //                 | "is" "greater" "than" addition
    //                 | "is" "less" "than" addition
    //                 | "is" addition "or" ( "more" | "less" )
    //                 | "is" addition
    // Symbol comparisons (= < > <= >=) are expression context only.

    private IExpression ParseCondition()
    {
        SkipNoise();
        var left = ParseAddition();
        SkipNoise();
        if (Peek().Type != TokenType.Is) return left;
        var isLine = Consume(TokenType.Is).Line;
        SkipNoise();
        return ParseWordComparison(left, isLine);
    }

    private IExpression ParseWordComparison(IExpression left, int isLine)
    {
        switch (Peek().Type)
        {
            case TokenType.Not:
            {
                var line = Advance().Line;
                SkipNoise();
                return new BinaryExpression(left, TokenType.NotEqual, ParseAddition(), line);
            }
            case TokenType.Greater:
            {
                var line = Advance().Line;
                SkipNoise();
                Consume(TokenType.Than);
                SkipNoise();
                return new BinaryExpression(left, TokenType.Gt, ParseAddition(), line);
            }
            case TokenType.Less:
            {
                var line = Advance().Line;
                SkipNoise();
                Consume(TokenType.Than);
                SkipNoise();
                return new BinaryExpression(left, TokenType.Lt, ParseAddition(), line);
            }
            default:
            {
                // "is expr" or "is expr or more/less"
                var right = ParseAddition();
                SkipNoise();
                if (Peek().Type != TokenType.Or)
                    return new BinaryExpression(left, TokenType.Equal, right, isLine);
                Consume(TokenType.Or);
                SkipNoise();
                switch (Peek().Type)
                {
                    case TokenType.More:
                        Advance();
                        return new BinaryExpression(left, TokenType.Gte, right, isLine);
                    case TokenType.Less:
                        Advance();
                        return new BinaryExpression(left, TokenType.Lte, right, isLine);
                    default:
                        throw new ParseException(Peek(), "more or less");
                }
            }
        }
    }

    // Expression grammar (expression context — right side of Define/becomes/State):
    //   primary       → NUMBER | STRING | IDENTIFIER | "(" expression ")"
    //   unary         → "-" unary | primary
    //   multiplication→ unary  ( ( "*" | "/" ) unary  )*
    //   addition      → multiplication ( ( "+" | "-" ) multiplication )*
    //   comparison    → addition ( ( "=" | "<" | ">" | "<=" | ">=" ) addition )*

    private IExpression ParseExpression() => ParseComparison();

    private IExpression ParseComparison()
    {
        var left = ParseAddition();
        while (Peek().Type is TokenType.Equal or TokenType.Lt or TokenType.Gt
                           or TokenType.Lte or TokenType.Gte)
        {
            var opTok = Advance();
            SkipNoise();
            left = new BinaryExpression(left, opTok.Type, ParseAddition(), opTok.Line);
        }
        return left;
    }

    private IExpression ParseAddition()
    {
        var left = ParseMultiplication();
        while (Peek().Type is TokenType.Plus or TokenType.Minus)
        {
            var opTok = Advance();
            SkipNoise();
            left = new BinaryExpression(left, opTok.Type, ParseMultiplication(), opTok.Line);
        }
        return left;
    }

    private IExpression ParseMultiplication()
    {
        var left = ParseUnary();
        while (Peek().Type is TokenType.Star or TokenType.Slash)
        {
            var opTok = Advance();
            SkipNoise();
            left = new BinaryExpression(left, opTok.Type, ParseUnary(), opTok.Line);
        }
        return left;
    }

    private IExpression ParseUnary()
    {
        if (Peek().Type == TokenType.Minus)
        {
            var line = Advance().Line;
            return new UnaryExpression(TokenType.Minus, ParseUnary(), line);
        }
        return ParsePrimary();
    }

    private IExpression ParsePrimary()
    {
        SkipNoise(); // articles are noise before any value
        var tok = Peek();
        switch (tok.Type)
        {
            case TokenType.Number:
                return new NumberLiteral(decimal.Parse(Advance().Lexeme));
            case TokenType.String:
                return new StringLiteral(Advance().Lexeme);
            case TokenType.Identifier:
                return new VariableReference(Advance().Lexeme);
            case TokenType.It:
                Advance();
                return new VariableReference("it");
            case TokenType.LParen:
                Advance();
                var inner = ParseExpression();
                SkipNoise();
                Consume(TokenType.RParen);
                return inner;
            case TokenType.Ordinal:
            case TokenType.Item:
            {
                var (name, idx, line) = ParseAccessTarget();
                return new SeriesAccess(name, idx, line);
            }
            case TokenType.NumberKw:
            {
                Advance();
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                var name = Consume(TokenType.Identifier).Lexeme;
                return new SeriesLength(name);
            }
            default:
                throw new ParseException(tok, "expression");
        }
    }

    private void SkipNoise()
    {
        while (Peek().IsNoise) Advance();
    }

    private Token Consume(TokenType expected)
    {
        var tok = Peek();
        if (tok.Type != expected)
            throw new ParseException(tok, expected.ToString());
        return Advance();
    }

    private Token Advance() => _tokens[_pos++];
    private Token Peek()    => _tokens[_pos];
}
