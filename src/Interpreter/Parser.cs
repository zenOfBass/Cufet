using Cufet.Lexer;

namespace Cufet.Interpreter;

public sealed class Parser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;
    private int _loopDepth;
    private int _nestDepth;     // any block depth — used to enforce Bind top-level only
    private int _functionDepth; // incremented inside a Bind body — for return validation
    private bool _inObjectDef;  // bypasses _nestDepth guard for Bind inside object method blocks

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
            TokenType.One        => ParseOneStatement(),
            TokenType.Article    => ParseRecordNamedSetStatement(),
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
            TokenType.Bind       => ParseBindStatement(),
            TokenType.Cast       => ParseCastStatementWrapper(),
            TokenType.Return     => ParseReturnStatement(),
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

    private IStatement ParseDefineStatement()
    {
        var line = Consume(TokenType.Define).Line;
        SkipNoise();
        if (Peek().Type == TokenType.Object)
            return ParseObjectDefinition(line);
        var name = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();
        Consume(TokenType.As);
        SkipNoise(); // skips article 'an' before 'interface'
        if (Peek().Type == TokenType.Interface)
            return ParseInterfaceDefinitionBody(name, line);
        IExpression value = Peek().Type == TokenType.Series
            ? ParseSeriesLiteralExpr()
            : ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new DefineStatement(name, value, line);
    }

    // Define object <name> with (<fields>) [: <bind-stmts> Done.].
    private ObjectDefinition ParseObjectDefinition(int line)
    {
        Consume(TokenType.Object);
        SkipNoise();
        var name = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();
        var shape = ParseRecordShapeAnnotation(); // consumes "with (...)"
        SkipNoise();

        // Optional trailing 'and' clauses:
        //   and as a <type-name>   — embedding (at most one)
        //   and <interface-name>   — conformance (repeatable)
        string? embeddedTypeName = null;
        var conformedInterfaces = new List<string>();
        while (Peek().Type == TokenType.And)
        {
            Advance(); // consume 'and'
            SkipNoise();
            if (Peek().Type == TokenType.As)
            {
                Consume(TokenType.As);
                SkipNoise(); // skips the article 'a'/'an'
                embeddedTypeName = Consume(TokenType.Identifier).Lexeme;
            }
            else
            {
                conformedInterfaces.Add(Consume(TokenType.Identifier).Lexeme);
            }
            SkipNoise();
        }

        var methods = new List<BindStatement>();
        if (Peek().Type == TokenType.Colon)
        {
            Advance(); // consume ':'
            _inObjectDef = true;
            _nestDepth++;
            while (true)
            {
                SkipNoise();
                if (Peek().Type is TokenType.Done or TokenType.Eof) break;
                if (Peek().Type != TokenType.Bind)
                    throw new ParseException(Peek(),
                        "Bind — only method definitions are allowed inside an object body");
                methods.Add(ParseBindStatement());
            }
            _nestDepth--;
            _inObjectDef = false;
            Consume(TokenType.Done);
            Consume(TokenType.Dot);
        }
        else
        {
            Consume(TokenType.Dot);
        }

        return new ObjectDefinition(name, shape.PositionalTypes, shape.NamedFields, methods, embeddedTypeName, conformedInterfaces, line);
    }

    // Define <name> as an interface for { <method-sigs> } / single method without {}
    // Called after consuming "Define <name> as an" and seeing the Interface token.
    private InterfaceDefinition ParseInterfaceDefinitionBody(string name, int line)
    {
        Consume(TokenType.Interface); SkipNoise();
        Consume(TokenType.For);      SkipNoise();

        var methods = new List<(string MethodName, CufetType? ReturnType, IReadOnlyList<CufetType> ParamTypes)>();

        if (Peek().Type == TokenType.LBrace)
        {
            // Braced form: { method-sig, method-sig, ... }
            Advance(); SkipNoise(); // consume '{'
            methods.Add(ParseInterfaceMethodSig());
            SkipNoise();
            while (Peek().Type == TokenType.Comma)
            {
                Advance(); SkipNoise(); // consume inter-method ','
                methods.Add(ParseInterfaceMethodSig());
                SkipNoise();
            }
            Consume(TokenType.RBrace);
        }
        else
        {
            // Brace-less single-method form
            methods.Add(ParseInterfaceMethodSig());
        }

        SkipNoise();
        Consume(TokenType.Dot);
        return new InterfaceDefinition(name, methods, line);
    }

    // Parses one interface method signature:
    //   the <return-type> function <name> [, given (<type name>, ...)]
    // Returns (methodName, returnType, paramTypes).
    private (string MethodName, CufetType? ReturnType, IReadOnlyList<CufetType> ParamTypes) ParseInterfaceMethodSig()
    {
        SkipNoise(); // skip 'the' article before return type

        CufetType? returnType;
        if (Peek().Type == TokenType.Void)
        {
            Advance(); SkipNoise();
            returnType = null;
        }
        else
        {
            returnType = ParseTypeAnnotation(); SkipNoise();
        }

        Consume(TokenType.FunctionKw); SkipNoise();
        var methodName = Consume(TokenType.Identifier).Lexeme; SkipNoise();

        // Optional ", given (<named-params>)" — disambiguated from inter-method ',' by peeking for 'given'
        var paramTypes = new List<CufetType>();
        if (Peek().Type == TokenType.Comma &&
            _pos + 1 < _tokens.Count && _tokens[_pos + 1].Type == TokenType.Given)
        {
            Advance(); SkipNoise(); // consume ','
            Consume(TokenType.Given); SkipNoise();
            Consume(TokenType.LParen); SkipNoise();
            if (Peek().Type != TokenType.RParen)
            {
                paramTypes.Add(ParseParameter().Type); SkipNoise();
                while (Peek().Type == TokenType.Comma)
                {
                    Advance(); SkipNoise();
                    paramTypes.Add(ParseParameter().Type); SkipNoise();
                }
            }
            Consume(TokenType.RParen); SkipNoise();
        }

        return (methodName, returnType, paramTypes);
    }

    // new <typeName> { [fields] }
    private ObjectLiteral ParseObjectLiteralExpr()
    {
        var line = Advance().Line; // consume 'new'
        SkipNoise();
        var typeName = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();
        Consume(TokenType.LBrace);
        // No SkipNoise — preserve leading 'the' for IsNamedFieldStart

        var positionals = new List<IExpression>();
        var namedFields = new List<(string Name, IExpression Value)>();
        bool namedStarted = false;

        if (Peek().Type != TokenType.RBrace)
        {
            ParseOneRecordField(positionals, namedFields, ref namedStarted);
            SkipNoise();
            while (Peek().Type == TokenType.Comma)
            {
                Advance();
                // No SkipNoise — preserve leading 'the'
                ParseOneRecordField(positionals, namedFields, ref namedStarted);
                SkipNoise();
            }
        }

        Consume(TokenType.RBrace);
        return new ObjectLiteral(typeName, positionals, namedFields, line);
    }

    private SeriesLiteral ParseSeriesLiteralExpr()
    {
        var seriesTok = Advance(); // consume "series"
        SkipNoise();

        CufetType? annotation = null;
        if (Peek().Type == TokenType.Of)
        {
            Advance(); SkipNoise(); // consume "of"
            if (Peek().Type == TokenType.Record ||
                (Peek().Type == TokenType.Identifier &&
                 Peek().Lexeme.Equals("records", StringComparison.OrdinalIgnoreCase)))
            {
                Advance(); SkipNoise(); // consume 'record'/'records'
                Consume(TokenType.Like); SkipNoise();
                annotation = ParseRecordShapeBody();
            }
            else if (Peek().Type == TokenType.Void)
            {
                Advance(); SkipNoise(); // consume 'void'
                Consume(TokenType.FunctionKw); SkipNoise();
                annotation = new FunctionType(ParseFunctionParamTypeList(), null);
            }
            else
            {
                annotation = ParseTypeAnnotation();
                SkipNoise();
                if (Peek().Type == TokenType.FunctionKw)
                {
                    Advance(); SkipNoise(); // consume 'function'
                    annotation = new FunctionType(ParseFunctionParamTypeList(), annotation);
                }
            }
            SkipNoise();
        }

        var elements = new List<IExpression>();
        if (Peek().Type == TokenType.With)
        {
            Advance(); SkipNoise(); // consume 'with'
            Consume(TokenType.LParen); SkipNoise();
            if (Peek().Type != TokenType.RParen)
            {
                elements.Add(ParseExpression());
                SkipNoise();
                while (Peek().Type == TokenType.Comma)
                {
                    Advance(); SkipNoise();
                    elements.Add(ParseExpression());
                    SkipNoise();
                }
            }
            Consume(TokenType.RParen);
        }
        return new SeriesLiteral(elements, annotation, seriesTok.Line);
    }

    // Parses the element-type annotation after "of":
    //   type-annotation → "number" | "numbers" | "text" | "fact" | "facts"
    //                   | "series" "of" type-annotation
    private CufetType ParseTypeAnnotation()
    {
        var tok = Peek();

        // voidable T — wraps any inner type
        if (tok.Type == TokenType.Voidable)
        {
            Advance(); SkipNoise();
            return new VoidableType(ParseTypeAnnotation());
        }

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
        // Named type: object or interface name — resolved by TypeChecker.
        if (tok.Type == TokenType.Identifier)
        {
            Advance();
            return new ObjectType(tok.Lexeme, [], [], []);
        }
        throw new ParseException(tok, "type name (number, text, fact, series of ..., or a defined type name)");
    }

    // Parses: with (<positional-types>, the <type> <field-name>, ...)
    // Positional types are bare type keywords; named fields start with 'the'.
    // Positionals must come before named fields — parser error otherwise.
    private RecordType ParseRecordShapeAnnotation()
    {
        Consume(TokenType.With); SkipNoise();
        return ParseRecordShapeBody();
    }

    // Parses: (<positional-types>, the <type> <field-name>, ...)
    // Called by both ParseRecordShapeAnnotation (after 'with') and the 'series of records like (...)' path.
    private RecordType ParseRecordShapeBody()
    {
        Consume(TokenType.LParen);
        // No SkipNoise here — preserve leading 'the' that signals a named field.

        var positionalTypes = new List<CufetType>();
        var namedFields     = new List<(string Name, CufetType Type)>();
        bool seenNamed      = false;

        if (Peek().Type != TokenType.RParen)
        {
            ParseOneRecordShapeField(positionalTypes, namedFields, ref seenNamed);
            SkipNoise(); // safe: after a field, next is comma or RParen
            while (Peek().Type == TokenType.Comma)
            {
                Advance();
                // No SkipNoise — preserve leading 'the' for named field detection.
                ParseOneRecordShapeField(positionalTypes, namedFields, ref seenNamed);
                SkipNoise(); // safe: after a field, next is comma or RParen
            }
        }
        Consume(TokenType.RParen);
        return new RecordType(positionalTypes, namedFields);
    }

    private void ParseOneRecordShapeField(
        List<CufetType> positionalTypes,
        List<(string Name, CufetType Type)> namedFields,
        ref bool seenNamed)
    {
        if (Peek().Type == TokenType.Article) // named: the <type> <name>
        {
            Advance(); SkipNoise();
            var fieldType = ParseTypeAnnotation(); SkipNoise();
            var fieldName = Consume(TokenType.Identifier).Lexeme;
            seenNamed = true;
            namedFields.Add((fieldName, fieldType));
        }
        else
        {
            if (seenNamed)
                throw new ParseException(Peek(),
                    "type — positional fields must come before named fields in a record shape");
            var fieldType = ParseTypeAnnotation();
            positionalTypes.Add(fieldType);
        }
    }

    private IStatement ParseBecomesStatement()
    {
        var tok  = Consume(TokenType.Identifier);
        var name = tok.Lexeme;
        var line = tok.Line;
        SkipNoise();
        if (Peek().Type == TokenType.Possessive)
            return ParsePossessiveSetStatement(new VariableReference(name, line));
        Consume(TokenType.Becomes);
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new BecomesStatement(name, value, line);
    }

    private IStatement ParseOneStatement()
    {
        var tok = Consume(TokenType.One);
        SkipNoise();
        return ParsePossessiveSetStatement(new VariableReference("one", tok.Line));
    }

    private PossessiveSetStatement ParsePossessiveSetStatement(IExpression baseExpr)
    {
        var possTok = Consume(TokenType.Possessive);
        SkipNoise();
        var memberTok = Advance(); // field name — any word token
        var line = possTok.Line;
        SkipNoise();
        Consume(TokenType.Becomes);
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new PossessiveSetStatement(baseExpr, memberTok.Lexeme, value, line);
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
            _nestDepth++;
            var stmt = ParseStatement();
            _nestDepth--;
            return new IStatement[] { stmt };
        }
        Consume(TokenType.Colon);
        _nestDepth++;
        var result = ParseLoopBody();
        _nestDepth--;
        return result;
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
        _nestDepth++;
        var body = ParseLoopBody();
        _nestDepth--;
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
        _nestDepth++;
        var body = ParseRepeatUntilBody();
        _nestDepth--;
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
        var seriesExpr = ParseExpression();
        SkipNoise();
        Consume(TokenType.Comma);
        SkipNoise();
        Consume(TokenType.Repeat);
        SkipNoise();
        Consume(TokenType.Colon);
        _loopDepth++;
        _nestDepth++;
        var body = ParseLoopBody();
        _nestDepth--;
        _loopDepth--;
        return new ForEachStatement(iterName, seriesExpr, body, forTok.Line);
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

    // 'the <name> of <record-expr> becomes <value>.'
    // SkipNoise in the Parse() loop stopped at 'the' because IsNamedAccessPattern() returned true.
    // No SkipNoise between 'the' and field name — same rule as named-access in expressions.
    private RecordNamedSetStatement ParseRecordNamedSetStatement()
    {
        Consume(TokenType.Article); // 'the'
        var fieldTok = Advance();   // field name immediately follows
        var line = fieldTok.Line;
        SkipNoise();
        Consume(TokenType.Of);
        SkipNoise();
        var record = ParsePrimary();
        SkipNoise();
        Consume(TokenType.Becomes);
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new RecordNamedSetStatement(fieldTok.Lexeme, record, value, line);
    }

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
    //   condition        → logical-or
    //   logical-or       → logical-and ( "or" logical-and )*
    //   logical-and      → cond-not ( "and" cond-not )*
    //   cond-not         → "not" cond-not | single-condition
    //   single-condition → addition ( is-comparison )?
    //   is-comparison    → "is" "not" addition
    //                    | "is" "greater" "than" addition
    //                    | "is" "less" "than" addition
    //                    | "is" addition "or" ( "more" | "less" )
    //                    | "is" addition
    // "or" after "is N" is disambiguated by peeking one token ahead:
    //   next is "more"/"less" → comparison tail (or more / or less)
    //   anything else         → logical-or; the "or" is left unconsumed for this level
    // "not" is unambiguous: after "is" it's consumed inside ParseWordComparison as "is not";
    //   at the start of a condition it's prefix negation via ParseCondNot.
    // Symbol comparisons (= < > <= >=) are expression context only.

    private IExpression ParseCondition() => ParseLogicalOr();

    private IExpression ParseLogicalOr()
    {
        var left = ParseLogicalAnd();
        while (Peek().Type == TokenType.Or)
        {
            var line = Advance().Line;
            SkipNoise();
            var right = ParseLogicalAnd();
            left = new BinaryExpression(left, TokenType.Or, right, line);
        }
        return left;
    }

    private IExpression ParseLogicalAnd()
    {
        var left = ParseCondNot();
        while (Peek().Type == TokenType.And)
        {
            var line = Advance().Line;
            SkipNoise();
            var right = ParseCondNot();
            left = new BinaryExpression(left, TokenType.And, right, line);
        }
        return left;
    }

    private IExpression ParseCondNot()
    {
        if (Peek().Type == TokenType.Not)
        {
            var line = Advance().Line;
            SkipNoise();
            return new UnaryExpression(TokenType.Not, ParseCondNot(), line);
        }
        return ParseSingleCondition();
    }

    private IExpression ParseSingleCondition()
    {
        SkipNoise();
        var left = ParseJoinedTo();
        SkipNoise();
        if (Peek().Type == TokenType.Equal)
            throw new ParseException(Peek().Line,
                "you used '=' in a condition — in Cufet, conditions use 'is' (for example, 'If n is 0'). Did you mean 'is'?");
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
                return new BinaryExpression(left, TokenType.NotEqual, ParseJoinedTo(), line);
            }
            case TokenType.Greater:
            {
                var line = Advance().Line;
                SkipNoise();
                Consume(TokenType.Than);
                SkipNoise();
                return new BinaryExpression(left, TokenType.Gt, ParseJoinedTo(), line);
            }
            case TokenType.Less:
            {
                var line = Advance().Line;
                SkipNoise();
                Consume(TokenType.Than);
                SkipNoise();
                return new BinaryExpression(left, TokenType.Lt, ParseJoinedTo(), line);
            }
            default:
            {
                // "is expr" or "is expr or more/less"
                // Peek past 'or' before consuming: if followed by more/less it's a comparison
                // tail; otherwise it's logical-or and we leave it for ParseLogicalOr to handle.
                var right = ParseJoinedTo();
                SkipNoise();
                if (Peek().Type == TokenType.Or &&
                    PeekAfterCurrent() is TokenType.More or TokenType.Less)
                {
                    Consume(TokenType.Or);
                    SkipNoise();
                    if (Peek().Type == TokenType.More)
                    {
                        Advance();
                        return new BinaryExpression(left, TokenType.Gte, right, isLine);
                    }
                    Advance(); // Less
                    return new BinaryExpression(left, TokenType.Lte, right, isLine);
                }
                return new BinaryExpression(left, TokenType.Equal, right, isLine);
            }
        }
    }

    // Expression grammar (expression context — right side of Define/becomes/State, and inside parens):
    //   primary       → NUMBER | STRING | IDENTIFIER | "(" expression ")"
    //   unary         → "-" unary | primary
    //   multiplication→ unary  ( ( "*" | "/" | "%" ) unary  )*
    //   addition      → multiplication ( ( "+" | "-" ) multiplication )*
    //   comparison    → addition ( ( "=" | "<" | ">" | "<=" | ">=" ) addition )*
    //   expr-not      → "not" expr-not | comparison
    //   expr-and      → expr-not  ( "and" expr-not  )*
    //   expr-or       → expr-and  ( "or"  expr-and  )*
    // not/and/or included here so parenthesised grouping works in condition context
    // (e.g. not (flag or other)) — same precedence as the condition grammar.

    private IExpression ParseExpression()
    {
        var left = ParseExprOr();
        SkipNoise();
        if (Peek().Type != TokenType.But) return left;
        var line = Advance().Line; // consume 'but'
        SkipNoise();
        Consume(TokenType.Void);
        SkipNoise();
        Consume(TokenType.Is);
        SkipNoise();
        return new ButVoidDefault(left, ParseExprOr(), line);
    }

    private IExpression ParseExprOr()
    {
        var left = ParseExprAnd();
        while (Peek().Type == TokenType.Or)
        {
            var line = Advance().Line;
            SkipNoise();
            left = new BinaryExpression(left, TokenType.Or, ParseExprAnd(), line);
        }
        return left;
    }

    private IExpression ParseExprAnd()
    {
        var left = ParseExprNot();
        while (Peek().Type == TokenType.And)
        {
            var line = Advance().Line;
            SkipNoise();
            left = new BinaryExpression(left, TokenType.And, ParseExprNot(), line);
        }
        return left;
    }

    private IExpression ParseExprNot()
    {
        if (Peek().Type == TokenType.Not)
        {
            var line = Advance().Line;
            SkipNoise();
            return new UnaryExpression(TokenType.Not, ParseExprNot(), line);
        }
        return ParseComparison();
    }

    private IExpression ParseComparison()
    {
        var left = ParseJoinedTo();
        while (Peek().Type is TokenType.Equal or TokenType.Lt or TokenType.Gt
                           or TokenType.Lte or TokenType.Gte)
        {
            var opTok = Advance();
            SkipNoise();
            left = new BinaryExpression(left, opTok.Type, ParseJoinedTo(), opTok.Line);
        }
        return left;
    }

    // '<text> joined to <text>' — left-associative text concatenation.
    // Sits above ParseAddition so that arithmetic binds tighter than joining;
    // sits below ParseComparison so you can compare joined results: 'If x joined to y is z'.
    private IExpression ParseJoinedTo()
    {
        var left = ParseAddition();
        SkipNoise();
        while (Peek().Type == TokenType.Joined)
        {
            var line = Advance().Line; // consume 'joined'
            SkipNoise();
            Consume(TokenType.To);
            SkipNoise();
            left = new TextJoin(left, ParseAddition(), line);
            SkipNoise();
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
        while (Peek().Type is TokenType.Star or TokenType.Slash or TokenType.Percent)
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
        // 'the <name> of <expr>' → named record field access.
        // Checked BEFORE SkipNoise so we can still see the leading 'the'.
        // 'the first of s', 'the number of s' are not named access: the token immediately
        // after 'the' is Ordinal/NumberKw, not Identifier/Article, so IsNamedAccessPattern returns false.
        // No SkipNoise between 'the' and the field name — 'a'/'an' may be field names.
        if (Peek().Type == TokenType.Article &&
            Peek().Lexeme.Equals("the", StringComparison.OrdinalIgnoreCase) &&
            IsNamedAccessPattern())
        {
            Advance(); // consume 'the'
            var identTok = Advance(); // field name immediately follows — no SkipNoise
            SkipNoise();
            Advance(); // consume 'of'
            SkipNoise();
            return new RecordNamedAccess(identTok.Lexeme, ParsePrimary(), identTok.Line);
        }

        SkipNoise(); // articles are noise before any value
        var tok = Peek();
        IExpression baseExpr;
        switch (tok.Type)
        {
            case TokenType.Number:
                baseExpr = new NumberLiteral(decimal.Parse(Advance().Lexeme));
                break;
            case TokenType.String:
                baseExpr = new StringLiteral(Advance().Lexeme);
                break;
            case TokenType.Identifier:
            {
                var t = Advance();
                baseExpr = new VariableReference(t.Lexeme, t.Line);
                break;
            }
            case TokenType.It:
            {
                var t = Advance();
                baseExpr = new VariableReference("it", t.Line);
                break;
            }
            case TokenType.One:
            {
                var t = Advance();
                baseExpr = new VariableReference("one", t.Line);
                break;
            }
            case TokenType.LParen:
            {
                Advance();
                var inner = ParseExpression();
                SkipNoise();
                Consume(TokenType.RParen);
                baseExpr = inner;
                break;
            }
            case TokenType.Ordinal:
            {
                // Inline ordinal access: 'first of <target-expr>' where target is parsed as
                // a primary expression, enabling chains like 'the first of the first of s'.
                var ordTok = Advance();
                var index = OrdinalToIndex(ordTok.Lexeme);
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                var target = ParsePrimary();
                baseExpr = new SeriesAccess(target, index, ordTok.Line);
                break;
            }
            case TokenType.Item:
            {
                var itemTok = Advance();
                SkipNoise();
                var idx = ParseExpression();
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                var target = ParsePrimary();
                baseExpr = new SeriesAccess(target, idx, itemTok.Line);
                break;
            }
            case TokenType.NumberKw:
            {
                Advance();
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                var name = Consume(TokenType.Identifier).Lexeme;
                baseExpr = new SeriesLength(name);
                break;
            }
            case TokenType.LengthKw:
            {
                var line = Advance().Line;
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                baseExpr = new TextLength(ParsePrimary(), line);
                break;
            }
            case TokenType.Range:
            {
                var line = Advance().Line; // consume 'range'
                SkipNoise();
                var start = ParseExpression();
                SkipNoise();
                Consume(TokenType.To);
                SkipNoise();
                var end = ParseExpression();
                baseExpr = new RangeExpression(start, end, line);
                break;
            }
            case TokenType.Void:
            {
                var line = Advance().Line;
                baseExpr = new VoidLiteral(line);
                break;
            }
            case TokenType.Cast:
                baseExpr = ParseCastExpression();
                break;
            case TokenType.Record:
                baseExpr = ParseRecordLiteralExpr();
                break;
            case TokenType.New:
                baseExpr = ParseObjectLiteralExpr();
                break;
            default:
                throw new ParseException(tok, "expression");
        }

        // Possessive postfix: alice's name, one's field, alice's friend's name
        SkipNoise();
        while (Peek().Type == TokenType.Possessive)
        {
            var possTok = Advance(); // consume "'s"
            SkipNoise();
            var memberTok = Advance(); // member name — any word token
            baseExpr = new PossessiveAccess(baseExpr, memberTok.Lexeme, possTok.Line);
            SkipNoise();
        }

        // 'converted to text' postfix — binds tighter than 'joined to' (parsed here at primary level).
        // Handles: score converted to text, car's year converted to text, (x+1) converted to text.
        while (Peek().Type == TokenType.Converted)
        {
            var line = Advance().Line; // consume 'converted'
            SkipNoise();
            Consume(TokenType.To);
            SkipNoise();
            var textTok = Peek();
            if (textTok.Type != TokenType.Identifier ||
                !textTok.Lexeme.Equals("text", StringComparison.OrdinalIgnoreCase))
                throw new ParseException(textTok, "text — expected after 'converted to'");
            Advance(); // consume 'text'
            baseExpr = new TextConvert(baseExpr, line);
            SkipNoise();
        }

        return baseExpr;
    }

    // ── Records ───────────────────────────────────────────────────────────

    // record with (<positional>, ..., the <name> <value>, ...)
    // The leading article ('a record', 'the record') has already been consumed as noise.
    private RecordLiteral ParseRecordLiteralExpr()
    {
        var recordTok = Consume(TokenType.Record);
        SkipNoise();
        Consume(TokenType.With);
        SkipNoise();
        Consume(TokenType.LParen);
        // No SkipNoise here — IsNamedFieldStart must see a leading 'the'.

        var positionals  = new List<IExpression>();
        var namedFields  = new List<(string Name, IExpression Value)>();
        bool namedStarted = false;

        if (Peek().Type != TokenType.RParen)
        {
            ParseOneRecordField(positionals, namedFields, ref namedStarted);
            SkipNoise(); // safe: after a field value, before comma check
            while (Peek().Type == TokenType.Comma)
            {
                Advance(); // consume ','
                // No SkipNoise — preserve leading 'the' for IsNamedFieldStart.
                ParseOneRecordField(positionals, namedFields, ref namedStarted);
                SkipNoise(); // safe: after a field value
            }
        }

        Consume(TokenType.RParen);
        return new RecordLiteral(positionals, namedFields, recordTok.Line);
    }

    private void ParseOneRecordField(
        List<IExpression> positionals,
        List<(string Name, IExpression Value)> namedFields,
        ref bool namedStarted)
    {
        if (IsNamedFieldStart())
        {
            namedStarted = true;
            Advance(); // consume 'the'
            // No SkipNoise here — field name immediately follows 'the' and may itself be an
            // Article token (e.g., 'the a 1' where 'a' is the field name, not filler).
            var name = Advance().Lexeme;
            SkipNoise();
            namedFields.Add((name, ParseExpression()));
        }
        else
        {
            if (namedStarted)
                throw new ParseException(Peek(),
                    "positional fields must come before named fields — move all 'the name value' fields to the end");
            positionals.Add(ParseExpression());
        }
    }

    // Returns true when the current position starts a named field: 'the' <name> <non-of>.
    // No noise-skip between 'the' and the name — 'a'/'an' are valid field names and Article
    // tokens would be wrongly consumed. Any word token (including keywords) is a valid name.
    private bool IsNamedFieldStart()
    {
        int i = _pos;
        if (i >= _tokens.Count ||
            _tokens[i].Type != TokenType.Article ||
            !_tokens[i].Lexeme.Equals("the", StringComparison.OrdinalIgnoreCase))
            return false;
        i++; // directly at the field-name token
        if (i >= _tokens.Count || !IsFieldNameToken(_tokens[i], forAccess: false)) return false;
        i++;
        while (i < _tokens.Count && _tokens[i].IsNoise) i++;
        return i < _tokens.Count && _tokens[i].Type != TokenType.Of;
    }

    // Returns true when the current position starts a named record access: 'the' <name> 'of'.
    // Ordinal and NumberKw are excluded so 'the first of s' and 'the number of s' still parse
    // as series operations. All other word tokens (including keywords like State) are valid names.
    private bool IsNamedAccessPattern()
    {
        int i = _pos; // at 'the'
        i++; // directly at the field-name token — no noise-skip
        if (i >= _tokens.Count || !IsFieldNameToken(_tokens[i], forAccess: true)) return false;
        i++;
        while (i < _tokens.Count && _tokens[i].IsNoise) i++;
        return i < _tokens.Count && _tokens[i].Type == TokenType.Of;
    }

    // Decides whether a token can serve as a record field name.
    // forAccess=true: also excludes Ordinal and NumberKw to keep 'the first/number of s' as series ops.
    // forAccess=false: allows Ordinal/NumberKw (in a field literal the value comes after, not 'of').
    private static bool IsFieldNameToken(Token tok, bool forAccess)
    {
        // Exclude structural delimiters, operators, and value literals.
        if (tok.Type is TokenType.Of or TokenType.Dot or TokenType.Colon or
                        TokenType.LParen or TokenType.RParen or TokenType.Comma or
                        TokenType.Number or TokenType.String or
                        TokenType.Plus or TokenType.Minus or TokenType.Star or
                        TokenType.Slash or TokenType.Percent or
                        TokenType.Equal or TokenType.Lt or TokenType.Gt or
                        TokenType.Lte or TokenType.Gte or TokenType.NotEqual or
                        TokenType.Eof)
            return false;
        // Exclude "the" — avoids 'the the name ...' being treated as a field.
        if (tok.Type == TokenType.Article &&
            tok.Lexeme.Equals("the", StringComparison.OrdinalIgnoreCase))
            return false;
        // For access patterns, exclude keywords that carry special meaning in 'the X of s' series syntax:
        //   Ordinal → 'the first of s' (positional access)
        //   NumberKw → 'the number of s' (series length)
        //   Start → 'to the start of s' (add-to-start)
        if (forAccess && tok.Type is TokenType.Ordinal or TokenType.NumberKw or TokenType.Start or TokenType.LengthKw)
            return false;
        return true;
    }

    // ── Functions ─────────────────────────────────────────────────────────

    private BindStatement ParseBindStatement()
    {
        var bindTok = Consume(TokenType.Bind);
        if (_nestDepth > 0 && !_inObjectDef)
            throw new ParseException(bindTok, "Functions can only be declared at the top level, not inside a block");
        var savedInObjectDef = _inObjectDef;
        _inObjectDef = false; // method body must not allow nested Binds
        SkipNoise();
        var returnType = ParseReturnType();
        SkipNoise();
        Consume(TokenType.To);
        SkipNoise();
        var name = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();

        var parameters = new List<(CufetType Type, string Name)>();
        if (Peek().Type == TokenType.Comma)
        {
            Advance(); // consume ','
            SkipNoise();
            Consume(TokenType.Given);
            SkipNoise();
            Consume(TokenType.LParen);
            SkipNoise();
            if (Peek().Type != TokenType.RParen)
            {
                parameters.Add(ParseParameter());
                SkipNoise();
                while (Peek().Type == TokenType.Comma)
                {
                    Advance();
                    SkipNoise();
                    parameters.Add(ParseParameter());
                    SkipNoise();
                }
            }
            Consume(TokenType.RParen);
            SkipNoise();
        }

        Consume(TokenType.Colon);
        _functionDepth++;
        _nestDepth++;
        var body = ParseFunctionBody();
        _nestDepth--;
        _functionDepth--;
        _inObjectDef = savedInObjectDef;

        return new BindStatement(name, returnType, parameters, body, bindTok.Line);
    }

    // null return → void (this function returns nothing)
    // FunctionType return → this function returns a function
    // RecordType return → this function returns a record (optional label consumed and discarded)
    private CufetType? ParseReturnType()
    {
        if (Peek().Type == TokenType.Void)
        {
            Advance(); SkipNoise();
            if (Peek().Type != TokenType.FunctionKw)
                return null; // bare void — this function returns nothing
            Advance(); SkipNoise(); // consume 'function'
            return new FunctionType(ParseFunctionParamTypeList(), null);
        }
        if (Peek().Type == TokenType.Record)
        {
            Advance(); SkipNoise(); // consume 'record'
            if (Peek().Type == TokenType.Identifier) { Advance(); SkipNoise(); } // optional label, discarded
            return ParseRecordShapeAnnotation();
        }
        var baseType = ParseTypeAnnotation();
        SkipNoise();
        if (Peek().Type != TokenType.FunctionKw)
            return baseType;
        Advance(); SkipNoise(); // consume 'function'
        return new FunctionType(ParseFunctionParamTypeList(), baseType);
    }

    // Parses a named parameter in a Bind declaration:
    //   <base-type> <name>
    //   (<base-type> | "void") "function" <name> ["given" "(" <param-type-list> ")"]
    //   "record" <name> "with" "(" <record-shape> ")"
    private (CufetType Type, string Name) ParseParameter()
    {
        SkipNoise();

        if (Peek().Type == TokenType.Record)
        {
            Advance(); SkipNoise(); // consume 'record'
            var recParamName = Consume(TokenType.Identifier).Lexeme; SkipNoise();
            var rt = ParseRecordShapeAnnotation();
            return (rt, recParamName);
        }

        CufetType? candidateType;
        if (Peek().Type == TokenType.Void)
        {
            Advance(); SkipNoise();
            if (Peek().Type != TokenType.FunctionKw)
                throw new ParseException(Peek(),
                    "function — 'void' can only appear as the return type of a function-typed parameter");
            candidateType = null;
        }
        else
        {
            candidateType = ParseTypeAnnotation();
            SkipNoise();
            if (Peek().Type != TokenType.FunctionKw)
            {
                var regularName = Consume(TokenType.Identifier).Lexeme;
                return (candidateType, regularName);
            }
        }

        // Function-typed parameter: <return-type> function <name> [given (<param-type-list>)]
        Advance(); SkipNoise(); // consume 'function'
        var paramName = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();

        var innerParamTypes = ParseFunctionParamTypeList();
        return (new FunctionType(innerParamTypes, candidateType), paramName);
    }

    // Parses a type inside a function-type annotation's given(...) list.
    // Simple types have no name. Function types include a placeholder name (required by grammar,
    // discarded after parsing — the name disambiguates the 'given' that belongs to the inner type).
    private CufetType ParseFunctionParamType()
    {
        SkipNoise();

        CufetType? returnType;
        bool seenVoid = false;
        if (Peek().Type == TokenType.Void)
        {
            Advance(); SkipNoise();
            seenVoid = true;
            returnType = null;
        }
        else
        {
            returnType = ParseTypeAnnotation();
            SkipNoise();
        }

        if (Peek().Type == TokenType.FunctionKw)
        {
            Advance(); SkipNoise(); // consume 'function'
            Consume(TokenType.Identifier); // placeholder name — consumed, not stored
            SkipNoise();
            return new FunctionType(ParseFunctionParamTypeList(), returnType);
        }

        if (seenVoid)
            throw new ParseException(Peek(), "function — 'void' can only appear as a function return type");

        return returnType!;
    }

    // Parses the optional "given (<param-type-list>)" trailer in a function-type annotation.
    // Returns an empty list when 'given' is absent.
    private List<CufetType> ParseFunctionParamTypeList()
    {
        var types = new List<CufetType>();
        if (Peek().Type != TokenType.Given) return types;

        Advance(); SkipNoise(); // consume 'given'
        Consume(TokenType.LParen); SkipNoise();
        if (Peek().Type != TokenType.RParen)
        {
            types.Add(ParseFunctionParamType());
            SkipNoise();
            while (Peek().Type == TokenType.Comma)
            {
                Advance(); SkipNoise();
                types.Add(ParseFunctionParamType());
                SkipNoise();
            }
        }
        Consume(TokenType.RParen);
        return types;
    }

    // Function bodies end at Done. — like loop bodies, but no empty-body restriction.
    private IReadOnlyList<IStatement> ParseFunctionBody()
    {
        var stmts = new List<IStatement>();
        while (true)
        {
            SkipNoise();
            if (Peek().Type is TokenType.Done or TokenType.Eof) break;
            stmts.Add(ParseStatement());
        }
        Consume(TokenType.Done);
        Consume(TokenType.Dot);
        return stmts;
    }

    private IStatement ParseCastStatementWrapper()
    {
        var cast = (CastExpression)ParseCastExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new CastStatement(cast.Function, cast.Args, cast.Line);
    }

    // Returns a CastExpression for both free-function calls and method dispatch.
    // Cast greet on alice (no parens) → CastExpression(VarRef("greet"), [alice], line).
    // Cast steer on (racer, 90)         → CastExpression(VarRef("steer"), [racer, 90], line).
    // Cast racer's steer on (90)        → CastExpression(PossessiveAccess(racer, steer), [90], line).
    private IExpression ParseCastExpression()
    {
        var line = Consume(TokenType.Cast).Line;
        var funcExpr = ParsePrimary(); // handles leading articles and possessive postfix
        SkipNoise();

        if (Peek().Type == TokenType.On)
        {
            Advance(); // consume 'on'
            SkipNoise();

            if (Peek().Type != TokenType.LParen)
            {
                // No-paren form: Cast greet on alice — normalizes to CastExpression with one arg.
                if (funcExpr is not VariableReference)
                    throw new ParseException(line,
                        "identifier — method name must be a plain identifier in 'Cast method on receiver'");
                var receiver = ParsePrimary();
                return new CastExpression(funcExpr, new IExpression[] { receiver }, line);
            }

            // Function call: Cast func on (<args>)
            Consume(TokenType.LParen);
            SkipNoise();
            var args = new List<IExpression>();
            if (Peek().Type != TokenType.RParen)
            {
                args.Add(ParseExpression());
                SkipNoise();
                while (Peek().Type == TokenType.Comma)
                {
                    Advance();
                    SkipNoise();
                    args.Add(ParseExpression());
                    SkipNoise();
                }
            }
            Consume(TokenType.RParen);
            return new CastExpression(funcExpr, args, line);
        }

        return new CastExpression(funcExpr, [], line);
    }

    private ReturnStatement ParseReturnStatement()
    {
        var line = Consume(TokenType.Return).Line;
        if (_functionDepth == 0)
            throw new ParseException(_tokens[_pos - 1], "'return' used outside a function");
        SkipNoise();
        if (Peek().Type == TokenType.Dot)
        {
            Consume(TokenType.Dot);
            return new ReturnStatement(null, line); // bare return — void early exit
        }
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new ReturnStatement(value, line);
    }

    private void SkipNoise()
    {
        while (Peek().IsNoise)
        {
            // Preserve 'the' when it opens a named record access ('the <name> of <record>').
            // Without this guard, 'state the city of alice.' would have 'the' eaten before
            // ParsePrimary's named-access check could see it.
            if (Peek().Lexeme.Equals("the", StringComparison.OrdinalIgnoreCase) &&
                IsNamedAccessPattern())
                break;
            Advance();
        }
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

    // Returns the type of the first non-noise token after the current position.
    private TokenType PeekAfterCurrent()
    {
        int i = _pos + 1;
        while (i < _tokens.Count && _tokens[i].IsNoise) i++;
        return i < _tokens.Count ? _tokens[i].Type : TokenType.Eof;
    }
}
