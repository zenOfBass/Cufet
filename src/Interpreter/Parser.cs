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
        Advance(); // consume "series"
        SkipNoise();
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
        return new SeriesLiteral(elements);
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
        Consume(TokenType.For);
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
        return new ForEachStatement(iterName, seriesName, body);
    }

    // ── Series operations ─────────────────────────────────────────────────

    // Parses "ORDINAL 'of' IDENTIFIER" or "'item' expr 'of' IDENTIFIER".
    // Returns (seriesName, index) where index==null means "last element".
    private (string name, IExpression? index) ParseAccessTarget()
    {
        if (Peek().Type == TokenType.Ordinal)
        {
            var lexeme = Advance().Lexeme;
            var index  = OrdinalToIndex(lexeme);
            SkipNoise();
            Consume(TokenType.Of);
            SkipNoise();
            var name = Consume(TokenType.Identifier).Lexeme;
            return (name, index);
        }
        else
        {
            Consume(TokenType.Item);
            SkipNoise();
            var idx = ParseExpression();
            SkipNoise();
            Consume(TokenType.Of);
            SkipNoise();
            var name = Consume(TokenType.Identifier).Lexeme;
            return (name, idx);
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
        var (name, idx) = ParseAccessTarget();
        SkipNoise();
        Consume(TokenType.Becomes);
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new SeriesSetStatement(name, idx, value);
    }

    private SeriesAddStatement ParseSeriesAddStatement()
    {
        Consume(TokenType.Add);
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
                return new SeriesAddStatement(value, sname, null, true);
            }
            else
            {
                var sname = Consume(TokenType.Identifier).Lexeme;
                SkipNoise();
                Consume(TokenType.Dot);
                return new SeriesAddStatement(value, sname, null, false);
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
            return new SeriesAddStatement(value, sname, afterIdx, false);
        }
    }

    private IStatement ParseSeriesRemoveStatement()
    {
        Consume(TokenType.Remove);
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
            return new SeriesRemoveAtStatement(sname, idx);
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
            return new SeriesRemoveAtStatement(sname, idx);
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
            return new SeriesRemoveValueStatement(sname, val);
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
        Consume(TokenType.Is);
        SkipNoise();
        return ParseWordComparison(left);
    }

    private IExpression ParseWordComparison(IExpression left)
    {
        switch (Peek().Type)
        {
            case TokenType.Not:
                Advance();
                SkipNoise();
                return new BinaryExpression(left, TokenType.NotEqual, ParseAddition());

            case TokenType.Greater:
                Advance();
                SkipNoise();
                Consume(TokenType.Than);
                SkipNoise();
                return new BinaryExpression(left, TokenType.Gt, ParseAddition());

            case TokenType.Less:
                Advance();
                SkipNoise();
                Consume(TokenType.Than);
                SkipNoise();
                return new BinaryExpression(left, TokenType.Lt, ParseAddition());

            default:
            {
                // "is expr" or "is expr or more/less"
                var right = ParseAddition();
                SkipNoise();
                if (Peek().Type != TokenType.Or)
                    return new BinaryExpression(left, TokenType.Equal, right);
                Consume(TokenType.Or);
                SkipNoise();
                switch (Peek().Type)
                {
                    case TokenType.More:
                        Advance();
                        return new BinaryExpression(left, TokenType.Gte, right);
                    case TokenType.Less:
                        Advance();
                        return new BinaryExpression(left, TokenType.Lte, right);
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
            var op = Advance().Type;
            SkipNoise();
            left = new BinaryExpression(left, op, ParseAddition());
        }
        return left;
    }

    private IExpression ParseAddition()
    {
        var left = ParseMultiplication();
        while (Peek().Type is TokenType.Plus or TokenType.Minus)
        {
            var op = Advance().Type;
            SkipNoise();
            left = new BinaryExpression(left, op, ParseMultiplication());
        }
        return left;
    }

    private IExpression ParseMultiplication()
    {
        var left = ParseUnary();
        while (Peek().Type is TokenType.Star or TokenType.Slash)
        {
            var op = Advance().Type;
            SkipNoise();
            left = new BinaryExpression(left, op, ParseUnary());
        }
        return left;
    }

    private IExpression ParseUnary()
    {
        if (Peek().Type == TokenType.Minus)
        {
            Advance();
            return new UnaryExpression(TokenType.Minus, ParseUnary());
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
                var (name, idx) = ParseAccessTarget();
                return new SeriesAccess(name, idx);
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
