using Cufet.Lexer;

namespace Cufet.Interpreter;

public sealed class Parser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;
    private int _loopDepth;
    private int _nestDepth;       // any block depth — used to enforce Bind top-level only
    private int _functionDepth;   // incremented inside a Bind body — for return validation
    private bool _inObjectDef;    // bypasses _nestDepth guard for Bind inside object method blocks
    private bool _inFreeFunction; // true inside a top-level (non-method) Bind body; allows nested Bind

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
            TokenType.Try        => ParseTryStatement(),
            TokenType.Suppress   => ParseSuppressStatement(),
            TokenType.In         => ParseMapSetStatement(),
            TokenType.Write      => ParseWriteStatement(),
            TokenType.Append     => ParseFileWriteStatement(),
            TokenType.With       => PeekAfterCurrent() == TokenType.Rabbit
                                     ? ParseWithRabbitStatement()
                                     : ParseWithOpenStatement(),
            TokenType.Pull       => ParsePullStatement(),
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
        SkipNoise(); // skips leading article ('a', 'an', 'the')
        if (Peek().Type == TokenType.Object)
            return ParseObjectDefinition(line);
        // "Define a shadow <name> as ..." — deliberate shadowing opt-in.
        // SkipNoise() above already consumed the article 'a', so we check for Shadow directly.
        bool shadow = false;
        if (Peek().Type == TokenType.Shadow)
        {
            Advance(); // consume 'shadow'
            shadow = true;
            SkipNoise();
        }
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
        bool permanent = false;
        if (Peek().Type == TokenType.Permanently)
        {
            Advance(); // consume 'permanently'
            permanent = true;
            SkipNoise();
        }
        Consume(TokenType.Dot);
        return new DefineStatement(name, value, permanent, shadow, line);
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

        // Union type: (A or B or C)
        // (T or void) normalizes to VoidableType(T).
        if (tok.Type == TokenType.LParen)
        {
            Advance(); SkipNoise(); // consume '('
            var cases = new List<CufetType>();
            cases.Add(ParseTypeAnnotation()); SkipNoise();
            while (Peek().Type == TokenType.Or)
            {
                Advance(); SkipNoise(); // consume 'or'
                cases.Add(ParseTypeAnnotation()); SkipNoise();
            }
            Consume(TokenType.RParen);
            // (T or void) → VoidableType(T)
            if (cases.Count == 2 && cases.Any(c => c is VoidType))
                return new VoidableType(cases.First(c => c is not VoidType));
            return new UnionType(cases);
        }

        // voidable T — wraps any inner type
        if (tok.Type == TokenType.Voidable)
        {
            Advance(); SkipNoise();
            return new VoidableType(ParseTypeAnnotation());
        }

        // map from K to V — homogeneous map type
        if (tok.Type == TokenType.Map)
        {
            Advance(); SkipNoise();
            Consume(TokenType.From); SkipNoise();
            var keyType = ParseTypeAnnotation(); SkipNoise();
            Consume(TokenType.To); SkipNoise();
            var valueType = ParseTypeAnnotation();
            return new MapType(keyType, valueType);
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
        if (tok.Type == TokenType.Identifier &&
            tok.Lexeme.Equals("readable", StringComparison.OrdinalIgnoreCase))
        {
            Advance(); SkipNoise(); // consume "readable"
            Consume(TokenType.Stream); SkipNoise();
            Consume(TokenType.Of); SkipNoise();
            return new ReadableStreamType(ParseTypeAnnotation());
        }
        if (tok.Type == TokenType.Identifier &&
            tok.Lexeme.Equals("writable", StringComparison.OrdinalIgnoreCase))
        {
            Advance(); SkipNoise(); // consume "writable"
            Consume(TokenType.Stream); SkipNoise();
            Consume(TokenType.Of); SkipNoise();
            return new WritableStreamType(ParseTypeAnnotation());
        }
        if (tok.Type == TokenType.Stream)
            throw new ParseException(tok,
                "stream direction — write 'readable stream of text' or 'writable stream of text'");
        if (tok.Type == TokenType.Rabbit)
        {
            Advance();
            return RabbitType.Instance;
        }
        if (tok.Type == TokenType.Matrix ||
            (tok.Type == TokenType.Identifier &&
             tok.Lexeme.Equals("matrices", StringComparison.OrdinalIgnoreCase)))
        {
            Advance();
            return MatrixType.Instance;
        }
        // catalogue [of (A or B)] as a type annotation — series of union type
        if (tok.Type == TokenType.CatalogueKw)
        {
            Advance(); SkipNoise();
            if (Peek().Type == TokenType.Of)
            {
                Advance(); SkipNoise();
                return new SeriesType(ParseTypeAnnotation());
            }
            return new SeriesType(UnionType.Open);
        }
        // atlas from K to (A or B) as a type annotation — map from K to union type
        if (tok.Type == TokenType.AtlasKw)
        {
            Advance(); SkipNoise();
            if (Peek().Type == TokenType.From)
            {
                Advance(); SkipNoise();
                var keyType = ParseTypeAnnotation(); SkipNoise();
                Consume(TokenType.To); SkipNoise();
                return new MapType(keyType, ParseTypeAnnotation());
            }
            return new MapType(CufetType.Text, UnionType.Open);
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
        var record = ParsePostfix();
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

    // ── Maps ──────────────────────────────────────────────────────────────────

    // "in <map>, the entry for <key> becomes <value>."
    private IStatement ParseMapSetStatement()
    {
        var line = Consume(TokenType.In).Line;
        SkipNoise();
        var mapExpr = ParsePostfix();
        SkipNoise();
        Consume(TokenType.Comma);
        SkipNoise(); // eats 'the' article
        Consume(TokenType.Entry);
        SkipNoise();
        Consume(TokenType.For);
        SkipNoise();
        var keyExpr = ParseExpression();
        SkipNoise();
        Consume(TokenType.Becomes);
        SkipNoise();
        var valueExpr = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new MapSetStatement(mapExpr, keyExpr, valueExpr, line);
    }

    // write <text> to the file "<path>"   — overwrite (creates if absent)
    // append <text> to the file "<path>"  — append   (creates if absent)
    // "write <value> to ..." — dispatches to file-write or stream-write based on what follows 'to'.
    private IStatement ParseWriteStatement()
    {
        var line = Advance().Line; // consume 'write'
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.To);
        SkipNoise(); // eats 'the' article before 'file' or stream source
        if (Peek().Type == TokenType.File)
        {
            Advance(); // consume 'file'
            SkipNoise();
            var path = ParseExprOr();
            SkipNoise();
            Consume(TokenType.Dot);
            return new FileWriteStatement(Append: false, value, path, line);
        }
        // Stream write — 'to <stream-expr>'
        var streamExpr = ParseExprOr();
        SkipNoise();
        Consume(TokenType.Dot);
        return new WriteToStreamStatement(value, streamExpr, line);
    }

    // "append <value> to the file ..." — file-only (streams are always written with 'write').
    private FileWriteStatement ParseFileWriteStatement()
    {
        var line = Advance().Line; // consume 'append'
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.To);
        SkipNoise(); // eats 'the' article before 'file'
        if (Peek().Type != TokenType.File)
            throw new ParseException(Peek(),
                "expected 'the file \"path\"' after 'append <value> to'");
        Advance(); // consume 'file'
        SkipNoise();
        var path = ParseExprOr();
        SkipNoise();
        Consume(TokenType.Dot);
        return new FileWriteStatement(Append: true, value, path, line);
    }

    // "With the file "<path>" open for reading/writing as <name>: ... Done."
    // Safe-by-construction lifecycle: stream is opened, bound, and automatically closed at block-exit.
    private WithOpenStatement ParseWithOpenStatement()
    {
        var line = Advance().Line; // consume 'With'
        SkipNoise();               // eats 'the'
        Consume(TokenType.File);
        SkipNoise();
        var pathExpr = ParseExprOr();
        SkipNoise();
        Consume(TokenType.Open);
        SkipNoise();
        Consume(TokenType.For);
        SkipNoise();
        var modeTok = Peek();
        OpenMode mode;
        if (modeTok.Type == TokenType.Identifier &&
            modeTok.Lexeme.Equals("reading", StringComparison.OrdinalIgnoreCase))
        {
            Advance();
            mode = OpenMode.Reading;
        }
        else if (modeTok.Type == TokenType.Identifier &&
                 modeTok.Lexeme.Equals("writing", StringComparison.OrdinalIgnoreCase))
        {
            Advance();
            mode = OpenMode.Writing;
        }
        else
        {
            throw new ParseException(modeTok, "'reading' or 'writing' after 'for'");
        }
        SkipNoise();
        Consume(TokenType.As);
        SkipNoise();
        var bindingName = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();
        Consume(TokenType.Colon);
        _nestDepth++;
        var body = ParseLoopBody(); // consumes Done.
        _nestDepth--;
        return new WithOpenStatement(mode, pathExpr, bindingName, body, line);
    }

    // "With a rabbit <name>: ... Done."
    // Creates a named block-scoped region; freed at Done. Same With/lifecycle shape as streams.
    private WithRabbitStatement ParseWithRabbitStatement()
    {
        var line = Advance().Line; // consume 'With'
        SkipNoise();               // eats 'a'
        Consume(TokenType.Rabbit); // consume 'rabbit'
        var name = Consume(TokenType.Identifier).Lexeme;
        Consume(TokenType.Colon);
        _nestDepth++;
        var body = ParseLoopBody(); // consumes Done.
        _nestDepth--;
        return new WithRabbitStatement(name, body, line);
    }

    // Pull a book on <name>.
    // Pull a book on <name> as [the] <local>.
    private PullStatement ParsePullStatement()
    {
        var line = Consume(TokenType.Pull).Line; // consume 'Pull'
        SkipNoise();                             // eats 'a'
        Consume(TokenType.Book);                 // consume 'book'
        SkipNoise();
        Consume(TokenType.On);                   // consume 'on'
        SkipNoise();
        var bookName = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();
        string localName = bookName;
        if (Peek().Type == TokenType.As)
        {
            Advance();   // consume 'as'
            SkipNoise(); // eats optional 'the'
            localName = Consume(TokenType.Identifier).Lexeme;
            SkipNoise();
        }
        Consume(TokenType.Dot);
        return new PullStatement(bookName, localName, line);
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

    // Parses a single matrix row: "(<expr>, <expr>, ...)" — at least one element.
    private IReadOnlyList<IExpression> ParseMatrixRow()
    {
        Consume(TokenType.LParen);
        SkipNoise();
        var elems = new List<IExpression>();
        if (Peek().Type != TokenType.RParen)
        {
            elems.Add(ParseExpression());
            SkipNoise();
            while (Peek().Type == TokenType.Comma)
            {
                Advance(); SkipNoise();
                elems.Add(ParseExpression());
                SkipNoise();
            }
        }
        Consume(TokenType.RParen);
        return elems;
    }

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
        // BEFORE SkipNoise: detect type-test forms that use the Article as a discriminator.
        if (Peek().Type == TokenType.Article) // "is a/an <type>"
        {
            Advance(); SkipNoise(); // consume the article
            return new IsTypeCheck(left, ParseTypeAnnotation(), false, isLine);
        }
        if (Peek().Type == TokenType.Not &&
            _pos + 1 < _tokens.Count && _tokens[_pos + 1].Type == TokenType.Article) // "is not a/an <type>"
        {
            Advance(); // consume 'not'
            Advance(); SkipNoise(); // consume the article
            return new IsTypeCheck(left, ParseTypeAnnotation(), true, isLine);
        }
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

        if (Peek().Type == TokenType.But)
        {
            var line = Advance().Line; // consume 'but'
            SkipNoise();
            if (Peek().Type == TokenType.On)
            {
                Advance(); // consume 'on'
                SkipNoise();
                Consume(TokenType.Failure);
                SkipNoise();
                return new FailureFallback(left, ParseExprOr(), line);
            }
            Consume(TokenType.Void);
            SkipNoise();
            Consume(TokenType.Is);
            SkipNoise();
            return new ButVoidDefault(left, ParseExprOr(), line);
        }

        if (Peek().Type == TokenType.Or && PeekAfterCurrent() == TokenType.Pass)
        {
            var line = Advance().Line; // consume 'or'
            SkipNoise();
            Consume(TokenType.Pass);
            SkipNoise();        // eats 'the'
            Consume(TokenType.Failure);
            SkipNoise();
            Consume(TokenType.Off);
            return new FailurePropagate(left, line);
        }

        return left;
    }

    private IExpression ParseExprOr()
    {
        var left = ParseExprAnd();
        while (Peek().Type == TokenType.Or && PeekAfterCurrent() != TokenType.Pass)
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

    // '<map> has a key/entry for <key>' — postfix; returns fact.
    // Sits between ParseJoinedTo and ParseSplitBy so "has" binds tighter than "joined to"
    // but looser than arithmetic, and works naturally in both expression and condition context.
    private IExpression ParseHasCheck()
    {
        var left = ParseSplitBy();
        SkipNoise();
        if (Peek().Type != TokenType.Has) return left;
        var line = Advance().Line; // consume 'has'
        SkipNoise(); // eats 'a' or 'an' article
        bool isEntry = Peek().Type == TokenType.Entry;
        bool isKey   = Peek().Type == TokenType.Key;
        if (!isEntry && !isKey)
            throw new ParseException(Peek(), "'key' or 'entry' after 'has'");
        Advance(); // consume Key or Entry
        SkipNoise();
        Consume(TokenType.For);
        SkipNoise();
        var keyExpr = ParseAddition();
        return isEntry
            ? (IExpression)new MapHasEntry(left, keyExpr, line)
            : new MapHasKey(left, keyExpr, line);
    }

    // '<text> split by <delimiter>' — series of text. Sits between ParseHasCheck and
    // ParseTextContains so split/contains/has/joined-to are all available at the same
    // general "text/collection operator" tier, above arithmetic.
    private IExpression ParseSplitBy()
    {
        var left = ParseTextContains();
        SkipNoise();
        if (Peek().Type != TokenType.Split) return left;
        var line = Advance().Line; // consume 'split'
        SkipNoise();
        Consume(TokenType.By);
        SkipNoise();
        var delimiter = ParseAddition();
        return new TextSplit(left, delimiter, line);
    }

    // '<text> contains <substring>' — fact. Sits just above ParseAddition.
    private IExpression ParseTextContains()
    {
        var left = ParseAddition();
        SkipNoise();
        if (Peek().Type != TokenType.Contains) return left;
        var line = Advance().Line; // consume 'contains'
        SkipNoise();
        var substring = ParseAddition();
        return new TextContains(left, substring, line);
    }

    // '<text> joined to <text>' — left-associative text concatenation.
    // Sits above ParseAddition so that arithmetic binds tighter than joining;
    // sits below ParseComparison so you can compare joined results: 'If x joined to y is z'.
    private IExpression ParseJoinedTo()
    {
        var left = ParseHasCheck(); // has-check sits between joined-to and addition
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
        return ParsePostfix();
    }

    // Wraps ParseCorePrimary with the postfix operators ('converted to text/number', 'trimmed',
    // 'sorted', 'in uppercase/lowercase'). All callers inside ParseCorePrimary's own switch
    // use ParseCorePrimary() directly so a recursive target (e.g. 'm' in 'the item at (r,c) of m')
    // does not accidentally consume postfixes that belong to the containing expression.
    private IExpression ParsePostfix()
    {
        var baseExpr = ParseCorePrimary();

        // 'converted to text' / 'converted to number' postfix — binds tighter than 'joined to'
        // (parsed here at primary level).
        // Handles: score converted to text, car's year converted to text, (x+1) converted to text,
        // "95" converted to number.
        while (Peek().Type == TokenType.Converted)
        {
            var line = Advance().Line; // consume 'converted'
            SkipNoise();
            Consume(TokenType.To);
            SkipNoise();
            var targetTok = Peek();
            if (targetTok.Type == TokenType.NumberKw)
            {
                Advance(); // consume 'number'
                baseExpr = new NumberConvert(baseExpr, line);
            }
            else if (targetTok.Type == TokenType.Identifier &&
                     targetTok.Lexeme.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                Advance(); // consume 'text'
                baseExpr = new TextConvert(baseExpr, line);
            }
            else
            {
                throw new ParseException(targetTok, "text or number — expected after 'converted to'");
            }
            SkipNoise();
        }

        // 'trimmed' / 'in uppercase' / 'in lowercase' postfix — same tier as 'converted to
        // text', chains naturally (e.g. '"  hi  " trimmed in uppercase').
        // 'in' is ALSO used to lead a sub-expression that an enclosing construct will consume
        // itself (e.g. 'the entry for <key> in <map>', 'the position of <substring> in <text>'
        // both parse their first operand via the full expression chain, which bottoms out here
        // before the outer construct's own 'Consume(TokenType.In)' runs). Without a lookahead,
        // this loop would greedily swallow that 'in' and then fail expecting 'uppercase'/
        // 'lowercase'. The fix: only treat 'in' as the case-operator when the token immediately
        // after it is actually 'uppercase' or 'lowercase' — checked via the unguarded
        // PeekAfterCurrent(), so a bare 'in <map-or-text-expr>' is left untouched for the
        // enclosing construct to consume.
        while (Peek().Type == TokenType.Trimmed ||
               Peek().Type == TokenType.Sorted ||
               (Peek().Type == TokenType.In && PeekAfterCurrent() is TokenType.Uppercase or TokenType.Lowercase))
        {
            if (Peek().Type == TokenType.Sorted)
            {
                var line = Advance().Line; // consume 'sorted'
                SkipNoise();
                string? byField = null;
                if (Peek().Type == TokenType.By)
                {
                    Advance(); // consume 'by'
                    SkipNoise(); // eats optional 'the' article before field name
                    byField = Consume(TokenType.Identifier).Lexeme;
                    SkipNoise();
                }
                bool reverse = false;
                if (Peek().Type == TokenType.In && PeekAfterCurrent() == TokenType.Reverse)
                {
                    Advance(); // consume 'in'
                    SkipNoise();
                    Advance(); // consume 'reverse'
                    reverse = true;
                    SkipNoise();
                }
                baseExpr = new SortExpression(baseExpr, byField, reverse, line);
            }
            else if (Peek().Type == TokenType.Trimmed)
            {
                var line = Advance().Line; // consume 'trimmed'
                baseExpr = new TextTrim(baseExpr, line);
            }
            else
            {
                var line = Advance().Line; // consume 'in'
                SkipNoise();
                bool toUpper = Peek().Type == TokenType.Uppercase;
                Advance(); // consume 'uppercase'/'lowercase'
                baseExpr = new TextCase(baseExpr, toUpper, line);
            }
            SkipNoise();
        }

        return baseExpr;
    }

    // Handles only unary minus then calls ParseCorePrimary — no postfix operators applied.
    // Used for the book-'of' single-arg so math's floor of x converted to text correctly
    // produces TextConvert(floor(x)) rather than floor(TextConvert(x)).
    private IExpression ParseNegation()
    {
        if (Peek().Type == TokenType.Minus)
        {
            var line = Advance().Line;
            return new UnaryExpression(TokenType.Minus, ParseCorePrimary(), line);
        }
        return ParseCorePrimary();
    }

    private IExpression ParseCorePrimary()
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
            return new RecordNamedAccess(identTok.Lexeme, ParseCorePrimary(), identTok.Line);
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
            case TokenType.InterpolOpen:
                Advance(); // consume InterpolOpen
                baseExpr = ParseInterpolatedString();
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
                var ordTok = Advance();
                SkipNoise();
                // 'first <count> characters of <text>' / 'last <count> characters of <text>' —
                // text substring from an edge. Distinguished from plain ordinal access ('first
                // of <series>') by a count expression appearing where 'of' would otherwise be.
                bool isFirstOrLast = ordTok.Lexeme.Equals("first", StringComparison.OrdinalIgnoreCase) ||
                                     ordTok.Lexeme.Equals("last", StringComparison.OrdinalIgnoreCase);
                if (isFirstOrLast && Peek().Type != TokenType.Of)
                {
                    var count = ParseAddition();
                    SkipNoise();
                    Consume(TokenType.Characters);
                    SkipNoise();
                    Consume(TokenType.Of);
                    SkipNoise();
                    var textTarget = ParseCorePrimary();
                    bool fromStart = ordTok.Lexeme.Equals("first", StringComparison.OrdinalIgnoreCase);
                    baseExpr = new TextSubstringEdge(textTarget, count, fromStart, ordTok.Line);
                    break;
                }

                // Inline ordinal access: 'first of <target-expr>' where target is parsed as
                // a primary expression, enabling chains like 'the first of the first of s'.
                var index = OrdinalToIndex(ordTok.Lexeme);
                Consume(TokenType.Of);
                SkipNoise();
                var target = ParseCorePrimary();
                baseExpr = new SeriesAccess(target, index, ordTok.Line);
                break;
            }
            case TokenType.Item:
            {
                var itemTok = Advance();
                SkipNoise();
                if (Peek().Type == TokenType.At)
                {
                    // Matrix indexing: "item at (row, col) of <matrix>"
                    Advance(); SkipNoise();              // consume 'at'
                    Consume(TokenType.LParen); SkipNoise();
                    var row = ParseExpression(); SkipNoise();
                    Consume(TokenType.Comma); SkipNoise();
                    var col = ParseExpression(); SkipNoise();
                    Consume(TokenType.RParen); SkipNoise();
                    Consume(TokenType.Of); SkipNoise();
                    var matTarget = ParseCorePrimary();
                    baseExpr = new MatrixAccess(matTarget, row, col, itemTok.Line);
                }
                else
                {
                    // Series indexing: "item <N> of <series>"
                    var idx = ParseExpression();
                    SkipNoise();
                    Consume(TokenType.Of);
                    SkipNoise();
                    var target = ParseCorePrimary();
                    baseExpr = new SeriesAccess(target, idx, itemTok.Line);
                }
                break;
            }
            case TokenType.Matrix:
            {
                var matrixLine = Advance().Line; // consume 'matrix'
                SkipNoise();
                Consume(TokenType.With);
                SkipNoise();
                if (Peek().Type == TokenType.LParen)
                {
                    // Literal: a matrix with ((r1e1, r1e2), (r2e1, r2e2), ...)
                    Consume(TokenType.LParen);
                    SkipNoise();
                    var rows = new List<IReadOnlyList<IExpression>>();
                    if (Peek().Type != TokenType.RParen)
                    {
                        rows.Add(ParseMatrixRow());
                        SkipNoise();
                        while (Peek().Type == TokenType.Comma)
                        {
                            Advance(); SkipNoise();
                            rows.Add(ParseMatrixRow());
                            SkipNoise();
                        }
                    }
                    Consume(TokenType.RParen);
                    baseExpr = new MatrixLiteral(rows, matrixLine);
                }
                else
                {
                    // Sized: a matrix with <rows> by <columns> [filled with <value>]
                    var rowsExpr = ParseExpression();
                    SkipNoise();
                    Consume(TokenType.By);
                    SkipNoise();
                    var colsExpr = ParseExpression();
                    SkipNoise();
                    IExpression? fillExpr = null;
                    if (Peek().Type == TokenType.FilledKw)
                    {
                        Advance(); // consume 'filled'
                        SkipNoise();
                        Consume(TokenType.With);
                        SkipNoise();
                        fillExpr = ParseExpression();
                    }
                    baseExpr = new MatrixSized(rowsExpr, colsExpr, fillExpr, matrixLine);
                }
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
                baseExpr = new TextLength(ParseCorePrimary(), line);
                break;
            }
            case TokenType.Position:
            {
                // 'the position of <substring> in <text>' — mirrors 'the entry for <key> in <map>'.
                var posLine = Advance().Line; // consume 'position'
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                var substringExpr = ParseExpression();
                SkipNoise();
                Consume(TokenType.In);
                SkipNoise();
                baseExpr = new TextFind(substringExpr, ParseCorePrimary(), posLine);
                break;
            }
            case TokenType.Characters:
            {
                // 'the characters from <from> to <to> of <text>' / '... to the end of <text>'.
                var charsLine = Advance().Line; // consume 'characters'
                SkipNoise();
                Consume(TokenType.From);
                SkipNoise();
                var fromExpr = ParseAddition();
                SkipNoise();
                Consume(TokenType.To);
                // 'the end' sentinel — checked directly (not via SkipNoise) because SkipNoise
                // would otherwise treat 'the end of ...' as a would-be named-access pattern and
                // refuse to consume 'the', since Position is the only token excluded for that.
                IExpression? toExpr;
                if (Peek().Type == TokenType.End)
                {
                    Advance(); // consume 'end'
                    toExpr = null;
                }
                else if (Peek().Type == TokenType.Article && PeekAfterCurrent() == TokenType.End)
                {
                    Advance(); // consume the article ('the'/'an')
                    Advance(); // consume 'end'
                    toExpr = null;
                }
                else
                {
                    SkipNoise();
                    toExpr = ParseAddition();
                }
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                var textTarget = ParseCorePrimary();
                baseExpr = new TextSubstringRange(textTarget, fromExpr, toExpr, charsLine);
                break;
            }
            case TokenType.Replace:
            {
                // 'replace <old> with <new> in <text>' — replaces all occurrences.
                var replaceLine = Advance().Line; // consume 'replace'
                SkipNoise();
                var oldExpr = ParseAddition();
                SkipNoise();
                Consume(TokenType.With);
                SkipNoise();
                var newExpr = ParseAddition();
                SkipNoise();
                Consume(TokenType.In);
                SkipNoise();
                baseExpr = new TextReplace(ParseCorePrimary(), oldExpr, newExpr, replaceLine);
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
                SkipNoise();
                IExpression? step = null;
                if (Peek().Type == TokenType.Counting)
                {
                    Advance(); // consume 'counting'
                    SkipNoise();
                    Consume(TokenType.By);
                    SkipNoise();
                    step = ParseExpression();
                }
                baseExpr = new RangeExpression(start, end, step, line);
                break;
            }
            case TokenType.Void:
            {
                var line = Advance().Line;
                baseExpr = new VoidLiteral(line);
                break;
            }
            case TokenType.Failure:
            {
                // The leading article is already stripped by SkipNoise, so 'a failure "..."'
                // and bare 'the failure' are indistinguishable until we look at what follows:
                // a String immediately after means the literal constructor; anything else means
                // the bare implicit reference (only meaningful inside a failure handler).
                var failTok = Advance(); // consume 'failure'
                if (Peek().Type == TokenType.String)
                {
                    var message = new StringLiteral(Advance().Lexeme);
                    SkipNoise();
                    IExpression? category = null;
                    if (Peek().Type == TokenType.Of)
                    {
                        Advance(); // consume 'of'
                        SkipNoise();
                        Consume(TokenType.Category);
                        SkipNoise();
                        category = ParseExpression();
                    }
                    baseExpr = new FailureLiteral(message, category, failTok.Line);
                }
                else
                {
                    baseExpr = new VariableReference("the failure", failTok.Line);
                }
                break;
            }
            case TokenType.Exception:
            {
                // 'the exception' — 'the' is already stripped by SkipNoise.
                // Only meaningful inside an 'In case of exception' handler block.
                var exTok = Advance(); // consume 'exception'
                baseExpr = new VariableReference("the exception", exTok.Line);
                break;
            }
            case TokenType.Cast:
                baseExpr = ParseCastExpression();
                break;
            case TokenType.Record:
                baseExpr = ParseRecordLiteralExpr();
                break;
            case TokenType.New:
            {
                var newLine = Advance().Line; // consume 'new'
                SkipNoise();
                if (Peek().Type == TokenType.Map)
                {
                    // "a new map from K to V" — empty map with explicit type annotation
                    Advance(); SkipNoise(); // consume 'map'
                    Consume(TokenType.From); SkipNoise();
                    var keyType = ParseTypeAnnotation(); SkipNoise();
                    Consume(TokenType.To); SkipNoise();
                    var valueType = ParseTypeAnnotation();
                    baseExpr = new MapLiteral(keyType, valueType, [], newLine);
                    break;
                }
                // "a new TypeName { fields }" — object literal
                var typeName = Consume(TokenType.Identifier).Lexeme;
                SkipNoise();
                Consume(TokenType.LBrace);
                var positionals2 = new List<IExpression>();
                var namedFields2 = new List<(string Name, IExpression Value)>();
                bool namedStarted2 = false;
                if (Peek().Type != TokenType.RBrace)
                {
                    ParseOneRecordField(positionals2, namedFields2, ref namedStarted2);
                    SkipNoise();
                    while (Peek().Type == TokenType.Comma)
                    {
                        Advance();
                        ParseOneRecordField(positionals2, namedFields2, ref namedStarted2);
                        SkipNoise();
                    }
                }
                Consume(TokenType.RBrace);
                baseExpr = new ObjectLiteral(typeName, positionals2, namedFields2, newLine);
                break;
            }
            case TokenType.CatalogueKw:
            {
                // "a catalogue [of (A or B)] [with (...)]" — heterogeneous series
                var catLine = Advance().Line; // consume 'catalogue'
                SkipNoise();
                CufetType? catAnnotation = null;
                if (Peek().Type == TokenType.Of)
                {
                    Advance(); SkipNoise(); // consume 'of'
                    catAnnotation = ParseTypeAnnotation();
                    SkipNoise();
                }
                catAnnotation ??= UnionType.Open;
                var catElems = new List<IExpression>();
                if (Peek().Type == TokenType.With)
                {
                    Advance(); SkipNoise(); // consume 'with'
                    Consume(TokenType.LParen); SkipNoise();
                    if (Peek().Type != TokenType.RParen)
                    {
                        catElems.Add(ParseExpression()); SkipNoise();
                        while (Peek().Type == TokenType.Comma)
                        {
                            Advance(); SkipNoise();
                            catElems.Add(ParseExpression()); SkipNoise();
                        }
                    }
                    Consume(TokenType.RParen);
                }
                baseExpr = new SeriesLiteral(catElems, catAnnotation, catLine);
                break;
            }
            case TokenType.AtlasKw:
            {
                // "an atlas [from K to (A or B)] [with ("k" : v, ...)]" — heterogeneous map
                var atlasLine = Advance().Line; // consume 'atlas'
                SkipNoise();
                CufetType atlasKeyType;
                CufetType atlasValType;
                if (Peek().Type == TokenType.From)
                {
                    Advance(); SkipNoise(); // consume 'from'
                    atlasKeyType = ParseTypeAnnotation(); SkipNoise();
                    Consume(TokenType.To); SkipNoise();
                    atlasValType = ParseTypeAnnotation(); SkipNoise();
                }
                else
                {
                    // bare 'an atlas' — text keys, open value union
                    atlasKeyType = CufetType.Text;
                    atlasValType = UnionType.Open;
                }
                var atlasPairs = new List<(IExpression Key, IExpression Value)>();
                if (Peek().Type == TokenType.With)
                {
                    Advance(); SkipNoise(); // consume 'with'
                    Consume(TokenType.LParen); SkipNoise();
                    if (Peek().Type != TokenType.RParen)
                    {
                        var k = ParseExpression(); SkipNoise();
                        Consume(TokenType.Colon); SkipNoise();
                        var v = ParseExpression();
                        atlasPairs.Add((k, v));
                        SkipNoise();
                        while (Peek().Type == TokenType.Comma)
                        {
                            Advance(); SkipNoise();
                            var k2 = ParseExpression(); SkipNoise();
                            Consume(TokenType.Colon); SkipNoise();
                            var v2 = ParseExpression();
                            atlasPairs.Add((k2, v2));
                            SkipNoise();
                        }
                    }
                    Consume(TokenType.RParen);
                }
                baseExpr = new MapLiteral(atlasKeyType, atlasValType, atlasPairs, atlasLine);
                break;
            }
            case TokenType.Map:
            {
                // "a map with ("k" : v, ...)" — populated map literal
                var mapLine = Advance().Line; // consume 'map'
                SkipNoise();
                Consume(TokenType.With);
                SkipNoise();
                Consume(TokenType.LParen);
                SkipNoise();
                var pairs = new List<(IExpression Key, IExpression Value)>();
                if (Peek().Type != TokenType.RParen)
                {
                    var k = ParseExpression(); SkipNoise();
                    Consume(TokenType.Colon); SkipNoise();
                    var v = ParseExpression();
                    pairs.Add((k, v));
                    SkipNoise();
                    while (Peek().Type == TokenType.Comma)
                    {
                        Advance(); SkipNoise();
                        var k2 = ParseExpression(); SkipNoise();
                        Consume(TokenType.Colon); SkipNoise();
                        var v2 = ParseExpression();
                        pairs.Add((k2, v2));
                        SkipNoise();
                    }
                }
                Consume(TokenType.RParen);
                baseExpr = new MapLiteral(null, null, pairs, mapLine);
                break;
            }
            case TokenType.Entry:
            {
                // "the entry for <key> in <map>"
                var entryLine = Advance().Line; // consume 'entry'
                SkipNoise();
                Consume(TokenType.For);
                SkipNoise();
                var keyExpr = ParseExpression();
                SkipNoise();
                Consume(TokenType.In);
                SkipNoise();
                baseExpr = new MapLookup(ParseCorePrimary(), keyExpr, entryLine);
                break;
            }
            case TokenType.Size:
            {
                // "the size of <map>"
                var sizeLine = Advance().Line; // consume 'size'
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                baseExpr = new MapSize(ParseCorePrimary(), sizeLine);
                break;
            }
            case TokenType.RowsKw:
            {
                // "the rows of <matrix>" — row count as number; access, no pull needed
                var rowsLine = Advance().Line; // consume 'rows'
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                baseExpr = new MatrixRows(ParseCorePrimary(), rowsLine);
                break;
            }
            case TokenType.ColumnsKw:
            {
                // "the columns of <matrix>" — column count as number; access, no pull needed
                var colsLine = Advance().Line; // consume 'columns'
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                baseExpr = new MatrixColumns(ParseCorePrimary(), colsLine);
                break;
            }
            case TokenType.FunctionKw:
            {
                // "a function given (<params>): <body>" — anonymous lambda literal.
                // The leading article 'a'/'an' was already consumed by SkipNoise above.
                var lambdaLine = Advance().Line; // consume 'function'
                SkipNoise();
                var lambdaParams = new List<(CufetType Type, string Name)>();
                if (Peek().Type == TokenType.Given)
                {
                    Advance(); SkipNoise(); // consume 'given'
                    Consume(TokenType.LParen); SkipNoise();
                    if (Peek().Type != TokenType.RParen)
                    {
                        lambdaParams.Add(ParseParameter());
                        SkipNoise();
                        while (Peek().Type == TokenType.Comma)
                        {
                            Advance(); SkipNoise();
                            lambdaParams.Add(ParseParameter());
                            SkipNoise();
                        }
                    }
                    Consume(TokenType.RParen);
                    SkipNoise();
                }
                Consume(TokenType.Colon);
                var savedInFreeFunctionL = _inFreeFunction;
                _inFreeFunction = true;
                _functionDepth++;
                _nestDepth++;
                var lambdaBody = ParseLambdaBody();
                _nestDepth--;
                _functionDepth--;
                _inFreeFunction = savedInFreeFunctionL;
                baseExpr = new LambdaLiteral(lambdaParams, lambdaBody, lambdaLine);
                break;
            }
            case TokenType.Run:
            {
                // run <program>                              → result or failure
                // run <program> with arguments (<arg>, ...) → result or failure
                // "arguments" is contextual (not a reserved keyword) — checked by lexeme.
                // Arguments are passed directly to the OS; no shell is invoked.
                var runLine = Advance().Line; // consume 'run'
                SkipNoise();
                // ParseExprOr not ParseExpression: 'but on failure'/'or pass the failure off'
                // belong to the outer expression wrapping this RunExpression, not to the program name.
                var programExpr = ParseExprOr();
                SkipNoise();
                var runArgs = new List<IExpression>();
                if (Peek().Type == TokenType.With)
                {
                    Advance(); // consume 'with'
                    SkipNoise();
                    if (!IsWord("arguments"))
                        throw new ParseException(Peek(),
                            "expected 'arguments' after 'with' in a run expression");
                    Advance(); // consume 'arguments' (contextual)
                    SkipNoise();
                    Consume(TokenType.LParen);
                    SkipNoise();
                    if (Peek().Type != TokenType.RParen)
                    {
                        runArgs.Add(ParseExpression());
                        SkipNoise();
                        while (Peek().Type == TokenType.Comma)
                        {
                            Advance();
                            SkipNoise();
                            runArgs.Add(ParseExpression());
                            SkipNoise();
                        }
                    }
                    Consume(TokenType.RParen);
                }
                baseExpr = new RunExpression(programExpr, runArgs, runLine);
                break;
            }
            case TokenType.Read:
            {
                // 'read a line from the input'         → voidable text (stdin)
                // 'read all from the input'            → text (stdin)
                // 'read all lines from the input'      → series of text (stdin)
                // 'read all from the file "<path>"'    → text or failure (file)
                // 'read all lines from the file "<path>"' → series of text or failure (file)
                // 'line', 'lines', 'all', and 'input' are contextual words, not reserved
                // keywords — they're parsed by lexeme in this position only.
                var readLine = Advance().Line; // consume 'read'
                SkipNoise(); // eats leading article (e.g. 'a' in 'read a line')

                ReadForm stdinForm;
                if (IsWord("line"))
                {
                    Advance(); // consume 'line'
                    stdinForm = ReadForm.Line;
                }
                else if (IsWord("all"))
                {
                    Advance(); // consume 'all'
                    SkipNoise();
                    if (IsWord("lines"))
                    {
                        Advance(); // consume 'lines'
                        stdinForm = ReadForm.AllLines;
                    }
                    else
                    {
                        stdinForm = ReadForm.All;
                    }
                }
                else
                {
                    throw new ParseException(Peek(),
                        "expected 'line', 'all', or 'all lines' after 'read'");
                }

                SkipNoise();
                Consume(TokenType.From);
                SkipNoise(); // eats 'the' article before 'file' or stream source expression

                if (Peek().Type == TokenType.File)
                {
                    if (stdinForm == ReadForm.Line)
                        throw new ParseException(Peek(),
                            "reading line-by-line from a file is not yet supported — use 'read all' or 'read all lines'");
                    Advance(); // consume 'file'
                    SkipNoise();
                    // ParseExprOr not ParseExpression: stops before 'but on failure' / 'or pass
                    // the failure off', which belong to the outer expression that wraps this read.
                    var pathExpr = ParseExprOr();
                    var fileForm = stdinForm == ReadForm.AllLines ? FileReadForm.AllLines : FileReadForm.All;
                    baseExpr = new FileReadExpression(fileForm, pathExpr, readLine);
                }
                else
                {
                    // General stream source — 'the input' is a pre-defined stream of text binding.
                    // SkipNoise() above already consumed 'the', so we parse the rest of the expression.
                    var sourceExpr = ParseExprOr();
                    baseExpr = new ReadExpression(stdinForm, sourceExpr, readLine);
                }

                break;
            }
            default:
                throw new ParseException(tok, "expression");
        }

        // Possessive postfix: alice's name, one's field, alice's friend's name, math's absolute value
        SkipNoise();
        while (Peek().Type == TokenType.Possessive)
        {
            var possTok = Advance(); // consume "'s"
            // Multi-word member names for book members (e.g. "absolute value", "square root").
            // Skip leading articles, then accumulate consecutive identifier tokens.
            // Single-word members (object fields, methods) collect exactly one token.
            // Non-identifier first token: consume it as-is (keyword-named field fallback).
            while (Peek().Type == TokenType.Article) Advance();
            var parts = new List<string>();
            if (Peek().Type == TokenType.Identifier)
            {
                parts.Add(Advance().Lexeme);
                while (Peek().Type == TokenType.Identifier)
                    parts.Add(Advance().Lexeme);
            }
            else
            {
                parts.Add(Advance().Lexeme);
            }
            baseExpr = new PossessiveAccess(baseExpr, string.Join(" ", parts), possTok.Line);
            SkipNoise();
        }

        // Book-function call postfix: math's floor of x  →  CastExpression(PossessiveAccess(math, "floor"), [x])
        // Only fires when the left side is a PossessiveAccess (no valid Cufet syntax has object-field 'of').
        // Single-arg: ParsePrimary() so arithmetic operators bind to the result, not the argument.
        //   math's log of x / math's log of 10  →  log(x) / log(10), not log(x / log(10))
        // Multi-arg: 'of (<e1>, <e2>, ...)' uses ParseExpression() per arg.
        while (baseExpr is PossessiveAccess && Peek().Type == TokenType.Of)
        {
            var ofLine = Advance().Line; // consume 'of'
            SkipNoise();
            List<IExpression> callArgs;
            if (Peek().Type == TokenType.LParen)
            {
                Advance(); SkipNoise(); // consume '('
                callArgs = [];
                if (Peek().Type != TokenType.RParen)
                {
                    callArgs.Add(ParseExpression()); SkipNoise();
                    while (Peek().Type == TokenType.Comma)
                    {
                        Advance(); SkipNoise();
                        callArgs.Add(ParseExpression()); SkipNoise();
                    }
                }
                Consume(TokenType.RParen);
            }
            else
            {
                // ParseNegation so negation works (math's floor of -3.7 → floor(-3.7)) but
                // postfix operators like 'converted to text' are NOT consumed here — they
                // belong to the outer expression: math's floor of x converted to text →
                // TextConvert(floor(x)), not floor(TextConvert(x)).
                // Arithmetic still binds outside: math's log of x / math's log of 10 → log(x)/log(10).
                callArgs = [ParseNegation()];
            }
            baseExpr = new CastExpression(baseExpr, callArgs, ofLine);
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
        //   Position → 'the position of X in Y' (find) — directly followed by 'of', same shape
        //              as named access, but with a trailing 'in Y' the access check doesn't see
        if (forAccess && tok.Type is TokenType.Ordinal or TokenType.NumberKw or TokenType.Start
                                   or TokenType.LengthKw or TokenType.Size or TokenType.Position
                                   or TokenType.Stream or TokenType.RowsKw or TokenType.ColumnsKw)
            return false;
        return true;
    }

    // ── Functions ─────────────────────────────────────────────────────────

    private BindStatement ParseBindStatement()
    {
        var bindTok = Consume(TokenType.Bind);
        if (_nestDepth > 0 && !_inObjectDef && !_inFreeFunction)
            throw new ParseException(bindTok, "Functions can only be declared at the top level or inside another function, not inside a block");
        var savedInObjectDef   = _inObjectDef;
        var savedInFreeFunction = _inFreeFunction;
        _inObjectDef    = false;               // method body must not allow nested Binds
        SkipNoise();
        var returnType = ParseReturnType();
        SkipNoise();
        Consume(TokenType.To);
        SkipNoise();
        var name = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();

        // 'unto <type>' — declares this Bind as a method of <type>, defined outside its
        // body. Comes right after the name, before the optional ', given (...)' clause.
        // Treated exactly like a nested method below: blocks nested Binds inside its body,
        // same as a method declared inside the object's own definition.
        string? untoType = null;
        if (Peek().Type == TokenType.Unto)
        {
            Advance(); // consume 'unto'
            SkipNoise();
            untoType = Consume(TokenType.Identifier).Lexeme;
            SkipNoise();
        }

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

        // True for free functions, false for method bodies (nested or 'unto').
        _inFreeFunction = untoType == null && !savedInObjectDef;

        Consume(TokenType.Colon);
        _functionDepth++;
        _nestDepth++;
        var body = ParseFunctionBody();
        _nestDepth--;
        _functionDepth--;
        _inObjectDef    = savedInObjectDef;
        _inFreeFunction = savedInFreeFunction;

        return new BindStatement(name, returnType, parameters, body, untoType, bindTok.Line);
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
        if (Peek().Type == TokenType.Or && PeekAfterCurrent() == TokenType.Failure)
        {
            Advance(); // consume 'or'
            SkipNoise();
            Consume(TokenType.Failure);
            SkipNoise();
            return new FailureType(baseType);
        }
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
        var funcExpr = ParsePostfix(); // handles leading articles and possessive postfix
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
                var receiver = ParsePostfix();
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

        // 'cast collections's transpose of (m)' — the book-of loop inside ParsePostfix already
        // built the full CastExpression; returning it without another wrapper is correct here.
        if (funcExpr is CastExpression bookCall)
            return bookCall;

        return new CastExpression(funcExpr, [], line);
    }

    private TryStatement ParseTryStatement()
    {
        var line = Consume(TokenType.Try).Line;
        SkipNoise();
        Consume(TokenType.To);
        SkipNoise();
        Consume(TokenType.Colon);
        _nestDepth++;
        var body = ParseLoopBody();
        _nestDepth--;
        SkipNoise();

        IReadOnlyList<IStatement>? failureHandler   = null;
        IReadOnlyList<IStatement>? exceptionHandler = null;

        // Optional failure handler — must come first if both handlers are present.
        if (PeekHandlerKind() == TokenType.Failure)
        {
            Consume(TokenType.In);   SkipNoise();
            Consume(TokenType.Case); SkipNoise();
            Consume(TokenType.Of);   SkipNoise();
            Consume(TokenType.Failure); SkipNoise();
            Consume(TokenType.Colon);
            _nestDepth++;
            failureHandler = ParseLoopBody();
            _nestDepth--;
            SkipNoise();
        }

        // Optional exception handler.
        if (PeekHandlerKind() == TokenType.Exception)
        {
            Consume(TokenType.In);        SkipNoise();
            Consume(TokenType.Case);      SkipNoise();
            Consume(TokenType.Of);        SkipNoise();
            Consume(TokenType.Exception); SkipNoise();
            // Binding: '(the exception)' — 'the' is noise-skipped inside the parens.
            Consume(TokenType.LParen);    SkipNoise();
            Consume(TokenType.Exception); SkipNoise();
            Consume(TokenType.RParen);    SkipNoise();
            Consume(TokenType.Colon);
            _nestDepth++;
            exceptionHandler = ParseLoopBody();
            _nestDepth--;
        }

        return new TryStatement(body, failureHandler, exceptionHandler, line);
    }

    private SuppressStatement ParseSuppressStatement()
    {
        var line = Consume(TokenType.Suppress).Line;
        SkipNoise(); // skips 'the'
        Consume(TokenType.Exception);
        SkipNoise();
        Consume(TokenType.Dot);
        return new SuppressStatement(line);
    }

    // Returns the handler keyword (Failure or Exception) following 'In case of' at
    // the current position, skipping noise. Returns Eof if no handler pattern follows.
    private TokenType PeekHandlerKind()
    {
        int i = _pos;
        while (i < _tokens.Count && _tokens[i].IsNoise) i++;
        if (i >= _tokens.Count || _tokens[i].Type != TokenType.In) return TokenType.Eof;
        i++;
        while (i < _tokens.Count && _tokens[i].IsNoise) i++;
        if (i >= _tokens.Count || _tokens[i].Type != TokenType.Case) return TokenType.Eof;
        i++;
        while (i < _tokens.Count && _tokens[i].IsNoise) i++;
        if (i >= _tokens.Count || _tokens[i].Type != TokenType.Of) return TokenType.Eof;
        i++;
        while (i < _tokens.Count && _tokens[i].IsNoise) i++;
        return i < _tokens.Count ? _tokens[i].Type : TokenType.Eof;
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

    // Lambda body: same as ParseFunctionBody but does NOT consume the trailing '.'
    // after Done — that '.' is owned by the enclosing statement or argument context.
    // Writers use: "a function given (x): Return x + 1. Done." where the outer
    // statement's '.' immediately follows Done.
    private IReadOnlyList<IStatement> ParseLambdaBody()
    {
        var stmts = new List<IStatement>();
        while (true)
        {
            SkipNoise();
            if (Peek().Type is TokenType.Done or TokenType.Eof) break;
            stmts.Add(ParseStatement());
        }
        Consume(TokenType.Done);
        // Trailing '.' is consumed by the enclosing context, not us.
        return stmts;
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

    // Parses an interpolated string starting just after InterpolOpen was consumed.
    // Collects StringPiece tokens and InterpolHoleOpen…InterpolHoleClose expression
    // sequences, building a left-associative TextJoin chain where each embedded
    // expression is wrapped in TextConvert (text/number/fact — type-checker enforces).
    private IExpression ParseInterpolatedString()
    {
        int line = _tokens[_pos - 1].Line; // line of the InterpolOpen
        IExpression? result = null;

        while (Peek().Type != TokenType.InterpolClose)
        {
            IExpression piece;

            if (Peek().Type == TokenType.StringPiece)
            {
                var tok = Advance();
                piece = new StringLiteral(tok.Lexeme);
            }
            else if (Peek().Type == TokenType.InterpolHoleOpen)
            {
                int holeLine = Advance().Line; // consume InterpolHoleOpen
                if (Peek().Type == TokenType.InterpolHoleClose)
                    throw new ParseException(holeLine, "empty interpolation '{}' — write an expression between the braces");
                var expr = ParseExpression();
                Consume(TokenType.InterpolHoleClose);
                piece = new TextConvert(expr, holeLine);
            }
            else
            {
                throw new ParseException(Peek(), "string piece or interpolation expression");
            }

            result = result == null ? piece : new TextJoin(result, piece, line);
        }

        Consume(TokenType.InterpolClose);
        return result ?? new StringLiteral("");
    }

    private Token Advance() => _tokens[_pos++];
    private Token Peek()    => _tokens[_pos];

    // True when the current token is an Identifier (or any token) whose normalized lexeme
    // matches the given word.  Used for positionally-disambiguated contextual words (line,
    // lines, all, input) that are not reserved keywords.
    private bool IsWord(string word) =>
        Peek().Lexeme.Equals(word, StringComparison.OrdinalIgnoreCase);

    // Returns the type of the first non-noise token after the current position.
    private TokenType PeekAfterCurrent()
    {
        int i = _pos + 1;
        while (i < _tokens.Count && _tokens[i].IsNoise) i++;
        return i < _tokens.Count ? _tokens[i].Type : TokenType.Eof;
    }
}
