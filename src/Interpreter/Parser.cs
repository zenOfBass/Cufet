using Cufet.Lexer;

namespace Cufet.Interpreter;

public sealed class Parser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;
    private int _loopDepth;
    private int _nestDepth;       // any block depth — used to enforce Bind top-level only
    private int _functionDepth;   // incremented inside a Bind body — for return validation
    private int _rabbitDepth;     // incremented inside a Pull a rabbit. ... Done. scope
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
        return new Program(InterfaceDefaults.Expand(stmts));
    }

    // Where every statement began, in source order, and whether the word it began with is acting as
    // a KEYWORD there rather than as a name. Recorded here because this is the only place that
    // knows both. A statement's position cannot be recovered from the tree (ten node types carry no
    // position at all), nor from the tokens alone — `the` and `with` open statements and also appear
    // midway through them. And the keyword question is genuinely contextual: in `output 7.` the word
    // opens a statement, while in `output becomes 10.` it is a variable of that name, so only the
    // discrimination this method already performs can tell them apart.
    private readonly List<(int Line, int Column, bool KeywordLed)> _statementStarts = [];
    public IReadOnlyList<(int Line, int Column, bool KeywordLed)> StatementStarts => _statementStarts;

    private IStatement ParseStatement()
    {
        var tok = Peek();
        bool keywordLed = tok.Type != TokenType.Identifier
                          || IsOutputStatement() || IsSeedStatement() || IsCurrentDirectorySet();
        _statementStarts.Add((tok.Line, tok.Column, keywordLed));
        return tok.Type switch
        {
            TokenType.State      => ParseStateStatement(),
            TokenType.Define     => ParseDefineStatement(),
            // 'output <value>.' — contextual producer statement; 'output' is NOT reserved
            TokenType.Identifier when IsOutputStatement() => ParseOutputStatement(),
            // 'Seed the chance with <n>.' — contextual, like the rest of the chance vocabulary.
            TokenType.Identifier when IsSeedStatement() => ParseSeedChanceStatement(),
            // 'name | name.' — pipe statement starting with a variable reference
            TokenType.Identifier when PeekAfterCurrent() == TokenType.Pipe => ParsePipeStatement(),
            // 'The current directory becomes <path>.' — the leading article is consumed as noise
            // (IsNamedAccessPattern needs 'of' after the field name and there is none), so the
            // statement arrives here starting at 'current'.
            TokenType.Identifier when IsCurrentDirectorySet() => ParseCurrentDirectorySetStatement(),
            TokenType.Identifier => IsOrdinalAccessorStatement() ? ParseSeriesSetStatement() : ParseBecomesStatement(),
            // 'run X | run Y.' — subprocess pipe (or standalone run that must be piped)
            TokenType.Run        => ParsePipeStatement(),
            TokenType.One        => ParseOneStatement(),
            TokenType.Article    => ParseRecordNamedSetStatement(),
            TokenType.If         => ParseIfStatement(),
            TokenType.Judge      => ParseJudgeStatement(),
            TokenType.While      => ParseWhileStatement(),
            TokenType.Repeat     => ParseRepeatUntilStatement(),
            TokenType.Stop       => ParseStopStatement(tok),
            TokenType.Skip       => ParseSkipStatement(tok),
            TokenType.Item       => ParseSeriesSetStatement(),
            TokenType.Insert     => ParseSeriesInsertStatement(),
            TokenType.Increment  => ParseIncrementStatement(),
            TokenType.Decrement  => ParseIncrementStatement(),
            TokenType.Remove     => ParseSeriesRemoveStatement(),
            TokenType.For        => ParseForEachStatement(),
            TokenType.Bind       => PeekAfterCurrent() == TokenType.UnmakingKw
                                     ? ParseUnmakerDeclaration()
                                     : PeekAfterCurrent() == TokenType.OverloadingKw
                                       ? ParseOverloadDeclaration()
                                       : ParseBindStatement(),
            TokenType.Cast       => ParseCastStatementWrapper(),
            TokenType.Return     => ParseReturnStatement(),
            TokenType.Bury       => ParseBuryStatement(),
            TokenType.Try        => ParseTryStatement(),
            TokenType.Suppress   => ParseSuppressStatement(),
            TokenType.In         => ParseMapSetStatement(),
            TokenType.Write      => ParseWriteStatement(),
            TokenType.Append     => ParseFileWriteStatement(),
            TokenType.With       => ParseWithOpenStatement(),
            TokenType.Pull       => ParsePullStatement(),
            TokenType.HaveKw     => ParseLaunchTaskStatement(),
            TokenType.Send       => ParseSendStatement(),
            TokenType.Close      => ParseCloseStatement(),
            TokenType.AcknowledgeKw => ParseAcknowledgeInterruptStatement(),
            TokenType.YieldKw       => ParseYieldStatement(),
            TokenType.GetKw => ParseGetterUntoDeclaration(),
            TokenType.SetKw => ParseSetterUntoDeclaration(),
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
        var lineTok = Consume(TokenType.Define);
        var line = lineTok.Line;
        var col = lineTok.Column;
        SkipNoise(); // skips leading article ('a', 'an', 'the')
        if (Peek().Type == TokenType.Object)
            return ParseObjectDefinition(line, col);
        // "Define a shadow <name> as ..." — deliberate shadowing opt-in.
        // SkipNoise() above already consumed the article 'a', so we check for Shadow directly.
        bool shadow = false;
        if (Peek().Type == TokenType.Shadow)
        {
            Advance(); // consume 'shadow'
            shadow = true;
            SkipNoise();
        }
        // `Define the text name as "Nathan".` — an explicit type in front of the name, the same
        // `the <type> <name>` shape that parameters and object fields already use.
        CufetType? declaredType = TryParseTypeBeforeName();
        var name = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();
        Consume(TokenType.As);
        SkipNoise(); // skips article 'an' before 'interface'
        if (Peek().Type == TokenType.Interface)
            return ParseInterfaceDefinitionBody(name, line, col);
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
        return new DefineStatement(name, value, permanent, shadow, line, col, declaredType);
    }

    // Define object <name> with (<fields>) [: <bind-stmts> Done.].
    private ObjectDefinition ParseObjectDefinition(int line, int col)
    {
        Consume(TokenType.Object);
        SkipNoise();
        var name = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();
        var shape = ParseRecordShapeAnnotation(out var permanentFields); // consumes "with (...)"
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
        var getters = new List<GetterDeclaration>();
        var setters = new List<SetterDeclaration>();
        if (Peek().Type == TokenType.Colon)
        {
            Advance(); // consume ':'
            _inObjectDef = true;
            _nestDepth++;
            while (true)
            {
                SkipNoise();
                if (Peek().Type is TokenType.Done or TokenType.Eof) break;
                if (Peek().Type == TokenType.Bind)
                    methods.Add(ParseBindStatement());
                else if (Peek().Type == TokenType.GetKw)
                    getters.Add(ParseGetterDeclaration());
                else if (Peek().Type == TokenType.SetKw)
                    setters.Add(ParseSetterDeclaration());
                else
                    throw new ParseException(Peek(),
                        "Bind, Get, or Set — only method, getter, and setter definitions are allowed inside an object body");
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

        return new ObjectDefinition(name, shape.PositionalTypes, shape.NamedFields, methods, getters, setters, embeddedTypeName, conformedInterfaces, line, col, permanentFields);
    }

    // Define <name> as an interface for { <method-sigs> } / single method without {}
    // Called after consuming "Define <name> as an" and seeing the Interface token.
    private InterfaceDefinition ParseInterfaceDefinitionBody(string name, int line, int col)
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
        return new InterfaceDefinition(name, methods, line, col);
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
        return new SeriesLiteral(elements, annotation, seriesTok.Line, seriesTok.Column);
    }

    // Parses the element-type annotation after "of":
    //   type-annotation → "number" | "numbers" | "text" | "fact" | "facts"
    //                   | "series" "of" type-annotation
    // An explicit type sitting between the article and the name, as in `Define the text name as …`.
    // A NAME still follows, which is what tells this apart from the plain form: in `Define x as 5.`
    // the annotation parse swallows `x` as a type name, then finds `as` instead of a name and the
    // whole attempt is rolled back, so `Define copy as src.` keeps meaning "copy src's value".
    private CufetType? TryParseTypeBeforeName()
    {
        int save = _pos;
        try
        {
            var parsed = ParseTypeAnnotation();
            SkipNoise();
            if (Peek().Type == TokenType.Identifier) return parsed;
        }
        catch (ParseException) { }
        _pos = save;
        return null;
    }

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
        // 'bits' is contextual, like 'text' and 'fact' — a type name recognised by lexeme rather
        // than a reserved word, so it stays usable as an ordinary name. Plural deliberately: a
        // bit is one bit and 0xFF is eight, and it pairs with 'fact' as one bit against N.
        if (tok.Type == TokenType.Identifier &&
            tok.Lexeme.Equals("bits", StringComparison.OrdinalIgnoreCase))
        {
            Advance();
            return new BitsType();
        }
        if (tok.Type == TokenType.Series)
        {
            Advance(); // consume "series"
            SkipNoise();
            Consume(TokenType.Of);
            SkipNoise();
            return new SeriesType(ParseTypeAnnotation());
        }
        // a stash of T — reads exactly like `a series of T`, which is the point: the shape is
        // familiar even though a stash produces its elements rather than holding them.
        if (tok.Type == TokenType.Stash)
        {
            Advance(); SkipNoise();   // consume "stash"
            Consume(TokenType.Of);
            SkipNoise();
            return new StashType(ParseTypeAnnotation());
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
        if (tok.Type == TokenType.Channel)
        {
            Advance(); SkipNoise(); // consume 'channel'
            Consume(TokenType.Of); SkipNoise();
            return new ChannelType(ParseTypeAnnotation());
        }
        // 'matrix' is contextual, so in TYPE position it is matched by lexeme. Type position is
        // unambiguous — nothing there can be a variable reference — so no lookahead is needed.
        if (IsWord("matrix") || IsWord("matrices"))
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
    private RecordType ParseRecordShapeAnnotation() => ParseRecordShapeAnnotation(out _);

    private RecordType ParseRecordShapeAnnotation(out List<string> permanentFields)
    {
        Consume(TokenType.With); SkipNoise();
        return ParseRecordShapeBody(out permanentFields);
    }

    // Parses: (<positional-types>, the <type> <field-name>, ...)
    // Called by both ParseRecordShapeAnnotation (after 'with') and the 'series of records like (...)' path.
    private RecordType ParseRecordShapeBody() => ParseRecordShapeBody(out _);

    // `permanentFields` collects the names declared `the permanently <type> <name>`. It is an out
    // parameter rather than part of RecordType because permanence is a property of the DECLARATION,
    // not of the type: two objects with the same field types differ only in what may be written.
    private RecordType ParseRecordShapeBody(out List<string> permanentFields)
    {
        Consume(TokenType.LParen);
        // No SkipNoise here — preserve leading 'the' that signals a named field.

        var positionalTypes = new List<CufetType>();
        var namedFields     = new List<(string Name, CufetType Type)>();
        permanentFields     = [];
        bool seenNamed      = false;

        if (Peek().Type != TokenType.RParen)
        {
            ParseOneRecordShapeField(positionalTypes, namedFields, permanentFields, ref seenNamed);
            SkipNoise(); // safe: after a field, next is comma or RParen
            while (Peek().Type == TokenType.Comma)
            {
                Advance();
                // No SkipNoise — preserve leading 'the' for named field detection.
                ParseOneRecordShapeField(positionalTypes, namedFields, permanentFields, ref seenNamed);
                SkipNoise(); // safe: after a field, next is comma or RParen
            }
        }
        Consume(TokenType.RParen);
        return new RecordType(positionalTypes, namedFields);
    }

    private void ParseOneRecordShapeField(
        List<CufetType> positionalTypes,
        List<(string Name, CufetType Type)> namedFields,
        List<string> permanentFields,
        ref bool seenNamed)
    {
        if (Peek().Type == TokenType.Article) // named: the <type> <name> [permanently]
        {
            Advance(); SkipNoise();
            var fieldType = ParseTypeAnnotation(); SkipNoise();
            var fieldName = Consume(TokenType.Identifier).Lexeme;
            seenNamed = true;
            namedFields.Add((fieldName, fieldType));

            // `the text id permanently` — TRAILING, the same position `Define x as 3 permanently`
            // already uses, so the rule is one rule: `permanently` follows the thing it fixes.
            // It also only reads as English there, because the verb it modifies is the enclosing
            // `Define`: "Define object user with the text id permanently."
            if (Peek().Type == TokenType.Permanently)
            {
                Advance();
                permanentFields.Add(fieldName);
            }
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
        var col = tok.Column;
        SkipNoise();
        if (Peek().Type == TokenType.Possessive)
            return ParsePossessiveSetStatement(new VariableReference(name, line, col));
        if (Peek().Type == TokenType.Equal)
            throw new ParseException(line, col,
                $"'=' is comparison, not assignment. Did you mean '{name} becomes ...' (update) or 'Define {name} as ...' (introduce)?");
        Consume(TokenType.Becomes);
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new BecomesStatement(name, value, line, col);
    }

    private IStatement ParseOneStatement()
    {
        var tok = Consume(TokenType.One);
        SkipNoise();
        return ParsePossessiveSetStatement(new VariableReference("one", tok.Line, tok.Column));
    }

    // `Increment <target> by <amount>.` / `Decrement <target> by <amount>.`
    //
    // ★ Pure SUGAR, desugared right here into the assignment it stands for — `i becomes i + 1`
    // or `one's tally becomes one's tally + 1`. There is no AST node, so the type checker, the
    // interpreter and the compiler never learn the form exists and all three get it for free.
    //
    // 35% of the `becomes` statements in examples/ were `X becomes X + …`, and that repetition is
    // where a typo hides — it hides WELL, because a line that is genuinely not self-referential
    // (`The next-w becomes w + 1.`) is invisible among thirty-seven that are. Naming the target
    // once makes the odd one out announce itself.
    //
    // The amount is an ARBITRARY expression: the corpus already needed
    // `The total becomes total + item at (rr, cc) of board.`
    //
    // ⚠ The target is named twice in the desugaring, so it must be side-effect free. A plain name
    // and a possessive chain both are; that is why those are the only two forms accepted.
    private IStatement ParseIncrementStatement()
    {
        var verb = Advance();                       // Increment | Decrement
        bool up  = verb.Type == TokenType.Increment;
        SkipNoise();

        // `one` is its own token rather than an Identifier, exactly as the assignment parser
        // treats it — `Increment one's tally by 1.` is the form a method reaches for first.
        var nameTok  = Peek().Type == TokenType.One ? Advance() : Consume(TokenType.Identifier);
        var nameText = nameTok.Type == TokenType.One ? "one" : nameTok.Lexeme;
        var target   = new VariableReference(nameText, nameTok.Line, nameTok.Column);
        SkipNoise();

        string? member = null;
        Token? possTok = null;
        if (Peek().Type == TokenType.Possessive)
        {
            possTok = Advance();
            SkipNoise();
            member = Advance().Lexeme;              // field name — any word token
            SkipNoise();
        }

        Consume(TokenType.By);
        SkipNoise();
        var amount = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);

        var op = up ? TokenType.Plus : TokenType.Minus;

        // ★ Every desugared node carries the position of the REAL TOKEN it stands for, never the
        // verb's. Downstream consumers read Line/Column as "where the name is": the semantic
        // tokenizer paints `Name.Length` characters starting there, and the possessive cases scan
        // forward from there for the member. Handing them the verb's position painted the first
        // N letters of `Increment` as a variable — N being the length of the target's name, which
        // is why the damage looked different on every line.
        if (member == null)
            return new BecomesStatement(nameText,
                new BinaryExpression(target, op, amount, nameTok.Line, nameTok.Column),
                nameTok.Line, nameTok.Column);

        var read = new PossessiveAccess(target, member, possTok!.Line, possTok.Column);
        return new PossessiveSetStatement(target, member,
            new BinaryExpression(read, op, amount, possTok.Line, possTok.Column),
            possTok.Line, possTok.Column);
    }

    private PossessiveSetStatement ParsePossessiveSetStatement(IExpression baseExpr)
    {
        var possTok = Consume(TokenType.Possessive);
        SkipNoise();
        var memberTok = Advance(); // field name — any word token
        var line = possTok.Line;
        var col = possTok.Column;
        SkipNoise();
        Consume(TokenType.Becomes);
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new PossessiveSetStatement(baseExpr, memberTok.Lexeme, value, line, col);
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

    // ── The inline-body rule, in one place ────────────────────────────────
    //
    // ★ EVERY block construct offers the same choice: a comma and ONE thing, or a colon and a
    // block closed by `Done.` `If` and `Judge` have always worked this way (ParseIfBody above);
    // these three helpers extend the same rule to the rest, so there is nothing per-construct to
    // remember.
    //
    // The comma is the point and a colon would be wrong: Cufet already spells *one thing, inline*
    // with a comma and *a block* with a colon, so an expression body — being one thing — takes a
    // comma. Spelling it with a colon would leave the only reliable structural signal meaning two
    // different things.
    //
    // An inline body produces an ORDINARY one-statement body, so nothing downstream changes: the
    // AST, the type checker, the interpreter and the compiler cannot tell the two spellings apart.

    // A body that must produce a value (a function or getter with a return type). The one thing is
    // an EXPRESSION and its `Return` is implicit — dropping `Return` and `Done.` is the whole of
    // what this form buys, and it is the same trade the inline `If` already made.
    private IReadOnlyList<IStatement> ParseValueBodyOrBlock()
    {
        SkipNoise();
        if (Peek().Type == TokenType.Comma)
        {
            var comma = Advance();
            SkipNoise();

            // ★ The two ways this goes wrong, both of which used to say only "expected expression".
            // The rule is learnable; the failures were not, and a bare parser expectation teaches
            // nobody which of the two forms they are actually in.
            var opener = Peek();
            if (opener.Type == TokenType.Return)
                throw new ParseException(opener.Line, opener.Column,
                    "an inline body gives its value back on its own, so 'Return' is not written here " +
                    "— drop it, as in 'Bind number to double, given (the number n), n * 2.'. " +
                    "Use the block form ': ... Done.' if the body needs more than one statement.");
            if (IsStatementOpener(opener.Type))
                throw new ParseException(opener.Line, opener.Column,
                    $"'{opener.Lexeme}' opens a statement, but this body has to give a value back, " +
                    "so its inline form is an EXPRESSION — the thing to return, with no 'Return'. " +
                    "Use the block form ': ... Done.' to run statements.");

            _nestDepth++;
            var value = ParseExpression();
            _nestDepth--;
            SkipNoise();
            Consume(TokenType.Dot);
            return new IStatement[] { new ReturnStatement(value, comma.Line, comma.Column) };
        }
        Consume(TokenType.Colon);
        _nestDepth++;
        var body = ParseFunctionBody();
        _nestDepth--;
        return body;
    }

    // Tokens that unmistakably OPEN a statement. Used only to turn "expected expression" into a
    // message that says which of the two inline forms the author is actually in. Deliberately not
    // exhaustive: a word that is merely ambiguous belongs in the ordinary expression path, and a
    // wrong guess here would replace a correct error with a confident wrong one.
    private static bool IsStatementOpener(TokenType type) => type is
        TokenType.State or TokenType.Insert or TokenType.Increment or TokenType.Decrement or
        TokenType.Remove or TokenType.Replace or TokenType.Send or TokenType.Close or
        TokenType.Write or TokenType.Append or TokenType.Open or TokenType.Define or
        TokenType.Bind or TokenType.If or TokenType.While or TokenType.For or TokenType.Try or
        TokenType.Judge or TokenType.Stop or TokenType.Skip or TokenType.GetKw or TokenType.SetKw;

    // A body that returns nothing (a void function, a setter, a destructor). The one thing is a
    // STATEMENT, because there is no value to imply a `Return` for.
    private IReadOnlyList<IStatement> ParseVoidBodyOrBlock()
    {
        SkipNoise();
        if (Peek().Type == TokenType.Comma)
        {
            Advance();
            SkipNoise();
            var opener = Peek();
            _nestDepth++;
            IStatement stmt;
            try
            {
                stmt = ParseStatement();
            }
            catch (ParseException ex) when (ex.Message.Contains("expected Becomes"))
            {
                // ★ An EXPRESSION where a statement belongs. Left alone this reported "expected
                // Becomes", having decided the first word was an assignment target — an error that
                // points at the wrong idea entirely.
                throw new ParseException(opener.Line, opener.Column,
                    "this body gives nothing back, so its inline form is a STATEMENT, not an " +
                    "expression — something like 'State ...' or 'one's field becomes ...'. " +
                    "A body that should return a value needs a return type: " +
                    "'Bind number to ...' rather than 'Bind void to ...'.");
            }
            finally
            {
                _nestDepth--;
            }
            return new[] { stmt };
        }
        Consume(TokenType.Colon);
        _nestDepth++;
        var body = ParseFunctionBody();
        _nestDepth--;
        return body;
    }

    // A loop body. The comma is already spent on the loop's own header, so `repeat:` is what
    // separates the two forms rather than the comma — one token, and no ambiguity:
    //     For each n in items, repeat: State n. Done.
    //     For each n in items, State n.
    private IReadOnlyList<IStatement> ParseLoopBodyOrInline()
    {
        SkipNoise();
        _loopDepth++;
        _nestDepth++;
        try
        {
            if (Peek().Type == TokenType.Repeat)
            {
                var repeatTok = Advance();
                SkipNoise();
                Consume(TokenType.Colon);
                // ★ Reported at the `repeat:` that opened the block, not at the end of the file.
                // "expected Done, got Eof" pointed at the last line of the program, which is never
                // where the mistake is — and the fix is usually to DROP `repeat:` rather than to
                // add `Done.`, which the old message could not suggest.
                return ParseLoopBody(repeatTok);
            }
            return new[] { ParseStatement() };
        }
        finally
        {
            _nestDepth--;
            _loopDepth--;
        }
    }

    // Judge <subject>, where it is:
    //     A num-node, state "leaf".
    //     An add-node or a mul-node: ... Done.
    //     Otherwise, state "something else".
    // Done.
    //
    // The header states subject and verb once so each arm completes the sentence, which is why
    // the arms are bare cases rather than repeating `It is`. Arm bodies reuse ParseIfBody, so the
    // comma-versus-colon rule is the same one `If` already follows and there is nothing new to
    // learn about where a `Done.` belongs.
    private JudgeStatement ParseJudgeStatement()
    {
        var tok = Consume(TokenType.Judge);
        SkipNoise();
        var subject = ParseExpression();
        SkipNoise();
        Consume(TokenType.Comma);
        SkipNoise();
        Consume(TokenType.Where);
        SkipNoise();
        Consume(TokenType.It);
        SkipNoise();
        Consume(TokenType.Is);
        SkipNoise();
        Consume(TokenType.Colon);

        var arms = new List<JudgeArm>();
        IReadOnlyList<IStatement>? otherwise = null;

        while (true)
        {
            SkipNoise();

            if (Peek().Type is TokenType.Done or TokenType.Eof) break;

            // `Otherwise` closes the arm list — nothing may follow it, because an arm after the
            // default could never be reached.
            if (Peek().Type == TokenType.Otherwise)
            {
                Advance();
                otherwise = ParseIfBody();
                SkipNoise();
                break;
            }

            var armTok = Peek();
            var cases  = new List<CufetType>();
            SkipNoise();
            cases.Add(ParseTypeAnnotation());
            SkipNoise();
            // `A num-node or a mul-node` — grouping, which is what C-style fall-through is
            // overwhelmingly used for and the reason `Descend.` is not needed for it.
            while (Peek().Type == TokenType.Or)
            {
                Advance();
                SkipNoise();
                cases.Add(ParseTypeAnnotation());
                SkipNoise();
            }

            arms.Add(new JudgeArm(cases, ParseIfBody(), armTok.Line, armTok.Column));
        }

        Consume(TokenType.Done);
        Consume(TokenType.Dot);

        if (arms.Count == 0)
            throw new ParseException(tok, "at least one case in a Judge");

        return new JudgeStatement(subject, arms, otherwise, tok.Line, tok.Column);
    }

    private WhileStatement ParseWhileStatement()
    {
        Consume(TokenType.While);
        SkipNoise();
        var condition = ParseCondition();
        SkipNoise();
        Consume(TokenType.Comma);
        var body = ParseLoopBodyOrInline();
        return new WhileStatement(condition, body);
    }

    // The BLOCK form of a loop body — reached only after `repeat:`, so it always ends in `Done.`
    // The single-statement form is ParseLoopBodyOrInline's other branch.
    // A closer on the same line is fine: "While ...: x becomes x + 1. Done."
    private IReadOnlyList<IStatement> ParseLoopBody(Token? opener = null)
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
        if (Peek().Type == TokenType.Eof && opener != null)
            throw new ParseException(opener.Line, opener.Column,
                $"this '{opener.Lexeme}' opens a block, and the file ended before its 'Done.'. " +
                "Either close it with 'Done.', or — if the body is a single statement — write the " +
                "inline form instead, which takes a comma and no 'Done.': " +
                "'For each n in items, State n.'");
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

    private IStatement ParseForEachStatement()
    {
        var forTok = Consume(TokenType.For);
        SkipNoise();
        Consume(TokenType.Each);
        SkipNoise();

        // Allow 'item' keyword as iterator name (common in pipes: 'for each item from the input:')
        string? iterName = null;
        if (Peek().Type == TokenType.Identifier || Peek().Type == TokenType.Item)
        {
            iterName = Advance().Lexeme;
            SkipNoise();
        }

        // Consumer for-each: 'for each <name> from the input: <body> Done.'
        if (Peek().Type == TokenType.From)
        {
            Advance(); // consume 'from'
            SkipNoise(); // skips 'the'
            if (Peek().Type != TokenType.Identifier ||
                !Peek().Lexeme.Equals("input", StringComparison.OrdinalIgnoreCase))
                throw new ParseException(Peek(), "'input' (in 'for each ... from the input:')");
            Advance(); // consume 'input'
            SkipNoise();

            // ★ The consumer loop takes the same choice as everything else. Its header spends no
            // comma (there is no `in <series>` clause), so the discriminator is the plain
            // comma-versus-colon rule rather than `repeat:`:
            //     for each s from input, output s.
            //     for each s from input: output s. Done.
            IReadOnlyList<IStatement> consumerBody;
            _loopDepth++;
            _nestDepth++;
            try
            {
                if (Peek().Type == TokenType.Comma)
                {
                    Advance();
                    SkipNoise();
                    consumerBody = new[] { ParseStatement() };
                }
                else
                {
                    var opener = Consume(TokenType.Colon);
                    consumerBody = ParseLoopBody(opener);
                }
            }
            finally
            {
                _nestDepth--;
                _loopDepth--;
            }
            return new ForEachFromInputStatement(iterName ?? "it", consumerBody, forTok.Line, forTok.Column);
        }

        Consume(TokenType.In);
        SkipNoise();
        var seriesExpr = ParseExpression();
        SkipNoise();
        Consume(TokenType.Comma);
        var body = ParseLoopBodyOrInline();
        return new ForEachStatement(iterName, seriesExpr, body, forTok.Line, forTok.Column);
    }

    // ── Pipe statement ────────────────────────────────────────────────────

    // 'A | B | C.' — left-associative pipe chain at statement level.
    // Operands are parsed via ParseExprOr so run-expressions, variable references,
    // and lambda literals all work. The 'but on failure'/'or pass the failure off'
    // wrappers are intentionally excluded (they belong to the outer context).
    private IStatement ParsePipeStatement()
    {
        var lineTok = Peek();
        int line = lineTok.Line;
        int col = lineTok.Column;
        IExpression left = ParsePipeOperand();
        if (Peek().Type != TokenType.Pipe)
            throw new ParseException(Peek(), "'|' — 'run' at statement level must be piped ('run X | run Y.'); use a Try block or expression context for standalone process execution");
        while (Peek().Type == TokenType.Pipe)
        {
            var pipeLineTok = Advance(); // consume '|'
            int pipeLine = pipeLineTok.Line;
            int pipeCol = pipeLineTok.Column;
            SkipNoise();
            left = new PipeExpression(left, ParsePipeOperand(), pipeLine, pipeCol);
        }
        SkipNoise();
        Consume(TokenType.Dot);
        return (PipeExpression)left;
    }

    // Parses one pipe stage operand: a run-expression, variable reference, or lambda.
    // Stops before '|', 'but', 'or pass', or '.'.
    private IExpression ParsePipeOperand()
    {
        SkipNoise();
        return ParseExprOr();
    }

    // True when 'seed' opens `Seed the chance with <n>.` rather than naming a variable.
    //
    // A POSITIVE test, unlike output's: the statement requires the word 'chance' next (the article
    // between them is noise), and no statement form is `<variable> <name>`, so a variable called
    // `seed` can never be followed by it. That makes the test exact rather than a list of shapes to
    // rule out — `seed becomes 43.`, `seed's algorithm becomes …` and `seed | consumer.` all fall
    // through to the variable reading without needing to be named here.
    private bool IsSeedStatement() =>
        Peek().Lexeme.Equals("seed", StringComparison.OrdinalIgnoreCase) && NextWordIs("chance");

    // True when 'output' identifier is followed by a value expression (not becomes/possessive/=/|).
    private bool IsOutputStatement() =>
        Peek().Lexeme.Equals("output", StringComparison.OrdinalIgnoreCase) &&
        PeekAfterCurrent() != TokenType.Becomes &&
        PeekAfterCurrent() != TokenType.Possessive &&
        PeekAfterCurrent() != TokenType.Equal &&
        PeekAfterCurrent() != TokenType.Pipe;

    // Returns true when the current token can be the first token of an expression
    // (used to distinguish 'item <N> of series' from plain 'item' as a variable reference).
    private bool LooksLikeIndexExprStart() => Peek().Type is
        TokenType.Number    or TokenType.String     or TokenType.InterpolOpen or
        TokenType.Identifier or TokenType.It        or TokenType.One          or
        TokenType.LParen    or TokenType.Article    or
        TokenType.TrueKw    or TokenType.FalseKw    or
        TokenType.Minus;

    // 'output <value>.' — producer emits a value to its implicit output stream.
    private OutputStatement ParseOutputStatement()
    {
        var lineTok = Advance(); // consume 'output' identifier
        int line = lineTok.Line;
        int col = lineTok.Column;
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new OutputStatement(value, line, col);
    }

    // ── Series operations ─────────────────────────────────────────────────

    // Parses "ORDINAL 'of' SERIES-EXPR" or "'item' expr 'of' SERIES-EXPR".
    // Returns (series, index, line, col) where index==null means "last element".
    // ParseCorePrimary is used for the series target (not ParsePostfix) so that
    // 'item i of my-series converted to text' binds as TextConvert(SeriesAccess(...))
    // rather than SeriesAccess(TextConvert(my-series), ...). Possessive access
    // ('one's cards', 'alice's cards') is handled inside ParseCorePrimary.
    private (IExpression series, IExpression? index, int line, int col) ParseAccessTarget()
    {
        if (IsOrdinalIdentifier(Peek()))
        {
            var ordTok = Advance();
            var index  = OrdinalToIndex(ordTok.Lexeme);
            SkipNoise();
            Consume(TokenType.Of);
            SkipNoise();
            var series = ParseCorePrimary();
            return (series, index, ordTok.Line, ordTok.Column);
        }
        else
        {
            var itemTok = Consume(TokenType.Item);
            SkipNoise();
            var idx = ParseExpression();
            SkipNoise();
            Consume(TokenType.Of);
            SkipNoise();
            var series = ParseCorePrimary();
            return (series, idx, itemTok.Line, itemTok.Column);
        }
    }

    // Ordinals are contextual identifiers — special only in the accessor shape
    // ("the <ordinal> of <series>"); everywhere else they're plain variable names.
    private static bool IsOrdinalLexeme(string lexeme) =>
        lexeme.ToLowerInvariant() is "first" or "second" or "third" or "fourth" or "fifth"
                                  or "sixth" or "seventh" or "eighth" or "ninth" or "tenth"
                                  or "last";

    private static bool IsOrdinalIdentifier(Token tok) =>
        tok.Type == TokenType.Identifier && IsOrdinalLexeme(tok.Lexeme);

    // True when current position is an ordinal identifier followed immediately by 'of' —
    // i.e. a series positional set statement starts here.
    private bool IsOrdinalAccessorStatement()
    {
        var tok = Peek();
        if (tok.Type != TokenType.Identifier || !IsOrdinalLexeme(tok.Lexeme)) return false;
        int i = _pos + 1;
        while (i < _tokens.Count && _tokens[i].IsNoise) i++;
        return i < _tokens.Count && _tokens[i].Type == TokenType.Of;
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
        var col = fieldTok.Column;
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
        return new RecordNamedSetStatement(fieldTok.Lexeme, record, value, line, col);
    }

    // True when the statement starting here is 'current directory becomes ...'. Checked by lexeme
    // rather than token type because 'current' is an ordinary identifier everywhere else.
    private bool IsCurrentDirectorySet()
    {
        if (!Peek().Lexeme.Equals("current", StringComparison.OrdinalIgnoreCase)) return false;
        int i = _pos + 1;
        while (i < _tokens.Count && _tokens[i].IsNoise) i++;
        return i < _tokens.Count && _tokens[i].Type == TokenType.DirectoryKw;
    }

    // 'The current directory becomes <path>.' — a fallible statement, like writing to a file:
    // the path may not exist, may not be a directory, or may not be reachable.
    private CurrentDirectorySetStatement ParseCurrentDirectorySetStatement()
    {
        var lineTok = Advance();   // consume 'current'
        var line = lineTok.Line;
        var col = lineTok.Column;
        SkipNoise();
        Consume(TokenType.DirectoryKw);
        SkipNoise();
        Consume(TokenType.Becomes);
        SkipNoise();
        var path = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new CurrentDirectorySetStatement(path, line, col);
    }

    // `The item at (r, c) of m becomes 1.` and `The item 3 of s becomes 1.` open identically, and
    // the word after 'item' is what tells them apart — exactly as it does in the read forms inside
    // ParseCorePrimary. Checked here rather than inside ParseAccessTarget so the series path keeps
    // returning its own shape and neither statement has to carry the other's fields.
    private IStatement ParseSeriesSetStatement()
    {
        if (Peek().Type == TokenType.Item && PeekAfterCurrentIsWord("at"))
            return ParseMatrixSetStatement();

        var (series, idx, line, col) = ParseAccessTarget();
        SkipNoise();
        Consume(TokenType.Becomes);
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new SeriesSetStatement(series, idx, value, line, col);
    }

    // The item at (<row>, <column>) of <matrix> becomes <number>.
    private MatrixSetStatement ParseMatrixSetStatement()
    {
        var itemTok = Consume(TokenType.Item);
        SkipNoise();
        Advance();                                   // consume 'at'
        SkipNoise();
        Consume(TokenType.LParen); SkipNoise();
        var row = ParseExpression(); SkipNoise();
        Consume(TokenType.Comma);   SkipNoise();
        var col = ParseExpression(); SkipNoise();
        Consume(TokenType.RParen);  SkipNoise();
        Consume(TokenType.Of);      SkipNoise();
        // ParseCorePrimary, not ParsePostfix — the same reason ParseAccessTarget gives.
        var matrix = ParseCorePrimary();
        SkipNoise();
        Consume(TokenType.Becomes);
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new MatrixSetStatement(matrix, row, col, value, itemTok.Line, itemTok.Column);
    }

    // `Insert <value> into <series>.` / `into the start of <series>.` / `after <position> of …`
    //
    // ★ `into` rather than `to`, and a DISTINCT token from `in`. `in` is an expression operator
    // (`in uppercase`), so a separator spelled `in` could not tell where the value ends —
    // `Insert word in uppercase in words.` has no readable boundary. `into` does:
    // `Insert word in uppercase into words.` parses exactly one way.
    private SeriesInsertStatement ParseSeriesInsertStatement()
    {
        var addTok = Consume(TokenType.Insert);
        int line = addTok.Line;
        int col = addTok.Column;
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();

        if (Peek().Type == TokenType.Into)
        {
            Consume(TokenType.Into);
            SkipNoise();
            if (Peek().Type == TokenType.Start)
            {
                Consume(TokenType.Start);
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                var seriesExpr = ParseCorePrimary();
                SkipNoise();
                Consume(TokenType.Dot);
                return new SeriesInsertStatement(value, seriesExpr, null, true, line, col);
            }
            else
            {
                var seriesExpr = ParseCorePrimary();
                SkipNoise();
                Consume(TokenType.Dot);
                return new SeriesInsertStatement(value, seriesExpr, null, false, line, col);
            }
        }
        else
        {
            Consume(TokenType.After);
            SkipNoise();
            IExpression? afterIdx;
            if (IsOrdinalIdentifier(Peek()))
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
            var seriesExpr = ParseCorePrimary();
            SkipNoise();
            Consume(TokenType.Dot);
            return new SeriesInsertStatement(value, seriesExpr, afterIdx, false, line, col);
        }
    }

    private IStatement ParseSeriesRemoveStatement()
    {
        var removeTok = Consume(TokenType.Remove);
        int line = removeTok.Line;
        int col = removeTok.Column;
        SkipNoise();

        if (IsOrdinalIdentifier(Peek()))
        {
            var idx = OrdinalToIndex(Advance().Lexeme);
            SkipNoise();
            if (Peek().Type == TokenType.Item) Advance(); // optional decorative "item"
            SkipNoise();
            Consume(TokenType.From);
            SkipNoise();
            var seriesExpr = ParseCorePrimary();
            SkipNoise();
            Consume(TokenType.Dot);
            return new SeriesRemoveAtStatement(seriesExpr, idx, line, col);
        }
        else if (Peek().Type == TokenType.Item)
        {
            Consume(TokenType.Item);
            SkipNoise();
            var idx = ParseExpression();
            SkipNoise();
            Consume(TokenType.From);
            SkipNoise();
            var seriesExpr = ParseCorePrimary();
            SkipNoise();
            Consume(TokenType.Dot);
            return new SeriesRemoveAtStatement(seriesExpr, idx, line, col);
        }
        else
        {
            var val = ParseExpression();
            SkipNoise();
            Consume(TokenType.From);
            SkipNoise();
            var seriesExpr = ParseCorePrimary();
            SkipNoise();
            Consume(TokenType.Dot);
            return new SeriesRemoveValueStatement(seriesExpr, val, line, col);
        }
    }

    // ── Maps ──────────────────────────────────────────────────────────────────

    // "in <map>, the entry for <key> becomes <value>."
    // from <map>, the entry for <key>          — the map-first lookup.
    // Deliberately shaped like ParseMapSetStatement below, which it mirrors: same `<map>, the entry
    // for <key>` phrase, one reading it and one writing it.
    private IExpression ParseLeadingMapLookup()
    {
        var fromTok = Consume(TokenType.From);
        SkipNoise();                       // 'the' in `from the map ages, …` is noise like anywhere else
        if (Peek().Type == TokenType.Map) { Advance(); SkipNoise(); }
        var mapExpr = ParsePostfix();
        SkipNoise();
        Consume(TokenType.Comma);
        SkipNoise();                       // eats the 'the' before 'entry'
        Consume(TokenType.Entry);
        SkipNoise();
        Consume(TokenType.For);
        SkipNoise();
        var keyExpr = ParseExpression();
        return new MapLookup(mapExpr, keyExpr, fromTok.Line, fromTok.Column);
    }

    private IStatement ParseMapSetStatement()
    {
        var lineTok = Consume(TokenType.In);
        var line = lineTok.Line;
        var col = lineTok.Column;
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
        return new MapSetStatement(mapExpr, keyExpr, valueExpr, line, col);
    }

    // write <text> to the file "<path>"   — overwrite (creates if absent)
    // append <text> to the file "<path>"  — append   (creates if absent)
    // "write <value> to ..." — dispatches to file-write or stream-write based on what follows 'to'.
    private IStatement ParseWriteStatement()
    {
        var lineTok = Advance(); // consume 'write'
        var line = lineTok.Line;
        var col = lineTok.Column;
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
            return new FileWriteStatement(Append: false, value, path, line, col);
        }
        // Stream write — 'to <stream-expr>'
        var streamExpr = ParseExprOr();
        SkipNoise();
        Consume(TokenType.Dot);
        return new WriteToStreamStatement(value, streamExpr, line, col);
    }

    // "append <value> to the file ..." — file-only (streams are always written with 'write').
    private FileWriteStatement ParseFileWriteStatement()
    {
        var lineTok = Advance(); // consume 'append'
        var line = lineTok.Line;
        var col = lineTok.Column;
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
        return new FileWriteStatement(Append: true, value, path, line, col);
    }

    // "With the file "<path>" open for reading/writing as <name>: ... Done."
    // Safe-by-construction lifecycle: stream is opened, bound, and automatically closed at block-exit.
    private WithOpenStatement ParseWithOpenStatement()
    {
        var lineTok = Advance(); // consume 'With'
        var line = lineTok.Line;
        var col = lineTok.Column;
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
        return new WithOpenStatement(mode, pathExpr, bindingName, body, line, col);
    }

    // Pull a rabbit [as <name>]. ... Done.
    // Pull a book on <name> [as <local>]. ... Done.
    // Pull books on <n1> [as <l1>], <n2> [as <l2>], and <n3>. ... Done.
    // All forms open a Done.-delimited scope; the pulled thing(s) are live until Done.
    private IStatement ParsePullStatement()
    {
        var lineTok = Consume(TokenType.Pull); // consume 'Pull'
        var line = lineTok.Line;
        var col = lineTok.Column;
        SkipNoise();                             // eats 'a' (singular forms); no-op for 'books'

        if (Peek().Type == TokenType.Rabbit)
        {
            Advance(); // consume 'rabbit'
            SkipNoise();
            string? name = null;
            if (Peek().Type == TokenType.As)
            {
                Advance();   // consume 'as'
                SkipNoise(); // eats optional 'the'
                name = Consume(TokenType.Identifier).Lexeme;
                SkipNoise();
            }
            Consume(TokenType.Dot);
            _rabbitDepth++;
            var body = ParsePullBody(); // consumes Done.
            _rabbitDepth--;
            return new PullRabbitStatement(name, body, line, col);
        }

        if (Peek().Type == TokenType.Books)
        {
            // Plural: Pull books on <n1> [as <l1>], <n2> [as <l2>], and <n3>. ... Done.
            Advance(); // consume 'books'
            SkipNoise();
            Consume(TokenType.On);
            SkipNoise();
            var books = new List<(string BookName, string LocalName)>();
            books.Add(ParsePullBookEntry());
            while (Peek().Type == TokenType.Comma || Peek().Type == TokenType.And)
            {
                if (Peek().Type == TokenType.Comma) Advance(); // consume ','
                SkipNoise();
                if (Peek().Type == TokenType.And) Advance();   // consume optional 'and'
                SkipNoise();
                if (Peek().Type == TokenType.Dot) break;       // safety: trailing comma
                books.Add(ParsePullBookEntry());
            }
            Consume(TokenType.Dot);
            var pluralBody = ParsePullBody();
            return new PullStatement(books, pluralBody, line, col);
        }

        // ★ The GENERAL form: Pull <module> [as <local>]. ... Done.
        //
        // A module is an object conforming to `module`, and this is how you bring one into scope.
        // It is not new syntax — `Pull a rabbit.` above is already this shape, and the article is
        // noise, so `Pull rabbit.` and `Pull greeting-kit as kit.` are the same form with different
        // names in it. The `book on <name>` spelling below is the special case being shed; it stays
        // because `math`, `collections` and `chance` read badly without the noun in front.
        //
        // Placed before the Book branch and gated on Identifier, so no existing spelling changes:
        // `book`, `books` and `rabbit` all lex as their own tokens and never reach here.
        if (Peek().Type == TokenType.Identifier)
        {
            var moduleEntry = ParsePullBookEntry();
            Consume(TokenType.Dot);
            var moduleBody = ParsePullBody();
            return new PullStatement([moduleEntry], moduleBody, line, col);
        }

        // Singular: Pull a book on <name> [as <local>]. ... Done.
        Consume(TokenType.Book); // consume 'book'
        SkipNoise();
        Consume(TokenType.On);   // consume 'on'
        SkipNoise();
        var entry = ParsePullBookEntry();
        Consume(TokenType.Dot);
        var bookBody = ParsePullBody(); // consumes Done.
        return new PullStatement([entry], bookBody, line, col);
    }

    // Parses one book entry in a Pull statement: <name> [as <local>]
    // Leaves the parser positioned after the entry (no trailing SkipNoise needed by caller).
    private (string BookName, string LocalName) ParsePullBookEntry()
    {
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
        return (bookName, localName);
    }

    // "Have rabbit start a task [as <name>]: ... Done."
    // Requires an active Pull a rabbit. ... Done. scope (_rabbitDepth > 0).
    // Name is optional — binds identity for slice-4 result-await; inert in slice 2.
    private LaunchTaskStatement ParseLaunchTaskStatement()
    {
        var tok = Peek();
        if (_rabbitDepth == 0)
            throw new ParseException(tok,
                "'Have rabbit start a task' requires an active rabbit — wrap it in 'Pull a rabbit. ... Done.'");
        var lineTok = Consume(TokenType.HaveKw);
        var line = lineTok.Line;
        var col = lineTok.Column;
        // `Have rabbit start …` addresses the enclosing one; `Have den start …` names the agent.
        string? rabbitName = null;
        if (Peek().Type == TokenType.Rabbit) Advance();
        else rabbitName = Consume(TokenType.Identifier).Lexeme;
        Consume(TokenType.Start);
        SkipNoise();                // eats 'a'
        Consume(TokenType.TaskKw);
        SkipNoise();
        string? name = null;
        if (Peek().Type == TokenType.As)
        {
            Advance();   // consume 'as'
            SkipNoise();
            name = Consume(TokenType.Identifier).Lexeme;
            SkipNoise();
        }
        // ★ A task body is a STATEMENT body, not an expression one — and it is the only value-
        // bearing body where that is forced. Every other body that can return declares its type on
        // the same line (`Bind number to …`), which is what lets `Return` be implicit. A task
        // declares nothing: it may hand back a result or merely send on a channel, and the header
        // cannot say which. So `return 1 + 2 + 3.` stays written out — it is one statement, which
        // is exactly what the comma form takes.
        _functionDepth++;
        IReadOnlyList<IStatement> body;
        if (Peek().Type == TokenType.Comma)
        {
            Advance();
            SkipNoise();
            body = new[] { ParseStatement() };
        }
        else
        {
            Consume(TokenType.Colon);
            body = ParsePullBody(); // consumes Done.
        }
        _functionDepth--;
        return new LaunchTaskStatement(name, body, line, col, rabbitName);
    }

    // Body parser for Pull...Done. scopes. Allows zero statements (unlike ParseLoopBody).
    // Does NOT increment _nestDepth — Pull scopes are transparent to the Bind-placement check,
    // so Bind declarations remain valid at whatever nesting depth they had before the Pull.
    private IReadOnlyList<IStatement> ParsePullBody()
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

    // Condition grammar (conditional context — after If / Otherwise if):
    //   condition        → logical-or
    //   logical-or       → logical-and ( "or" logical-and )*
    //   logical-and      → cond-not ( "and" cond-not )*
    //   cond-not         → "not" cond-not | single-condition
    //   single-condition → addition ( is-comparison )?
    //   is-comparison    → "is" "not" addition                           (inequality)
    //                    | "is" "not" "greater" "than" addition          (<=)
    //                    | "is" "not" "less" "than" addition             (>=)
    //                    | "is" "not" "equal" "to" addition              (inequality, verbose)
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
        var left = ParseLogicalXor();
        while (Peek().Type == TokenType.Or)
        {
            var lineTok = Advance();
            var line = lineTok.Line;
            var col = lineTok.Column;
            SkipNoise();
            var right = ParseLogicalXor();
            left = new BinaryExpression(left, TokenType.Or, right, line, col);
        }
        return left;
    }

    // The condition chain mirrors the expression chain — same precedence, so `xor` means the
    // same thing in `If a xor b:` as it does in `Define c as a xor b.`
    private IExpression ParseLogicalXor()
    {
        var left = ParseLogicalAnd();
        while (Peek().Type == TokenType.Xor)
        {
            var lineTok = Advance();
            var line = lineTok.Line;
            var col = lineTok.Column;
            SkipNoise();
            var right = ParseLogicalAnd();
            left = new BinaryExpression(left, TokenType.Xor, right, line, col);
        }
        return left;
    }

    private IExpression ParseLogicalAnd()
    {
        var left = ParseCondNot();
        while (Peek().Type == TokenType.And)
        {
            var lineTok = Advance();
            var line = lineTok.Line;
            var col = lineTok.Column;
            SkipNoise();
            var right = ParseCondNot();
            left = new BinaryExpression(left, TokenType.And, right, line, col);
        }
        return left;
    }

    private IExpression ParseCondNot()
    {
        if (Peek().Type == TokenType.Not)
        {
            var lineTok = Advance();
            var line = lineTok.Line;
            var col = lineTok.Column;
            SkipNoise();
            return new UnaryExpression(TokenType.Not, ParseCondNot(), line, col);
        }
        return ParseSingleCondition();
    }

    private IExpression ParseSingleCondition()
    {
        SkipNoise();
        var left = ParseJoinedTo();
        SkipNoise();
        // Symbol comparisons now work in condition position (unified with expression position).
        // = < > <= >= all produce the same boolean as the equivalent word-form.
        if (Peek().Type is TokenType.Equal or TokenType.Lt or TokenType.Gt
                        or TokenType.Lte or TokenType.Gte)
        {
            var opTok = Advance();
            SkipNoise();
            return new BinaryExpression(left, opTok.Type, ParseJoinedTo(), opTok.Line, opTok.Column);
        }
        if (Peek().Type != TokenType.Is) return left;
        var isLineTok = Consume(TokenType.Is);
        var isLine = isLineTok.Line;
        var isCol = isLineTok.Column;
        // BEFORE SkipNoise: detect type-test forms that use the Article as a discriminator.
        if (Peek().Type == TokenType.Article) // "is a/an <type>"
        {
            Advance(); SkipNoise(); // consume the article
            return new IsTypeCheck(left, ParseTypeAnnotation(), false, isLine, isCol);
        }
        if (Peek().Type == TokenType.Not &&
            _pos + 1 < _tokens.Count && _tokens[_pos + 1].Type == TokenType.Article) // "is not a/an <type>"
        {
            Advance(); // consume 'not'
            Advance(); SkipNoise(); // consume the article
            return new IsTypeCheck(left, ParseTypeAnnotation(), true, isLine, isCol);
        }
        SkipNoise();
        return ParseWordComparison(left, isLine, isCol);
    }

    private IExpression ParseWordComparison(IExpression left, int isLine, int isCol)
    {
        switch (Peek().Type)
        {
            case TokenType.Not:
            {
                var lineTok = Advance(); // consume 'not'
                var line = lineTok.Line;
                var col = lineTok.Column;
                SkipNoise();
                // 'is not greater than' → <=
                if (Peek().Type == TokenType.Greater)
                {
                    Advance(); SkipNoise(); // consume 'greater'
                    Consume(TokenType.Than);
                    SkipNoise();
                    return new BinaryExpression(left, TokenType.Lte, ParseJoinedTo(), line, col);
                }
                // 'is not less than' → >=
                if (Peek().Type == TokenType.Less)
                {
                    Advance(); SkipNoise(); // consume 'less'
                    Consume(TokenType.Than);
                    SkipNoise();
                    return new BinaryExpression(left, TokenType.Gte, ParseJoinedTo(), line, col);
                }
                // 'is not equal to' — 'equal' is contextual (not a keyword; lexes as Identifier)
                if (Peek().Type == TokenType.Identifier &&
                    Peek().Lexeme.Equals("equal", StringComparison.OrdinalIgnoreCase) &&
                    _pos + 1 < _tokens.Count && _tokens[_pos + 1].Type == TokenType.To)
                {
                    Advance(); // consume 'equal'
                    Advance(); // consume 'to'
                    SkipNoise();
                    return new BinaryExpression(left, TokenType.NotEqual, ParseJoinedTo(), line, col);
                }
                // 'is not <value>' → !=
                return new BinaryExpression(left, TokenType.NotEqual, ParseJoinedTo(), line, col);
            }
            case TokenType.Greater:
            {
                var lineTok = Advance();
                var line = lineTok.Line;
                var col = lineTok.Column;
                SkipNoise();
                Consume(TokenType.Than);
                SkipNoise();
                return new BinaryExpression(left, TokenType.Gt, ParseJoinedTo(), line, col);
            }
            case TokenType.Less:
            {
                var lineTok = Advance();
                var line = lineTok.Line;
                var col = lineTok.Column;
                SkipNoise();
                Consume(TokenType.Than);
                SkipNoise();
                return new BinaryExpression(left, TokenType.Lt, ParseJoinedTo(), line, col);
            }
            case TokenType.More:
            {
                var moreTok = Advance();
                throw new ParseException(moreTok.Line, moreTok.Column,
                    "'is more than' isn't a comparison Cufet recognises — did you mean 'is greater than'? " +
                    "For example: 'While count is greater than 0, repeat:'.");
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
                        return new BinaryExpression(left, TokenType.Gte, right, isLine, isCol);
                    }
                    Advance(); // Less
                    return new BinaryExpression(left, TokenType.Lte, right, isLine, isCol);
                }
                return new BinaryExpression(left, TokenType.Equal, right, isLine, isCol);
            }
        }
    }

    // Expression grammar (expression context — right side of Define/becomes/State, and inside parens):
    //   primary       → NUMBER | STRING | IDENTIFIER | "(" expression ")"
    //   unary         → "-" unary | primary
    //   multiplication→ unary  ( ( "*" | "/" | "%" ) unary  )*
    //   addition      → multiplication ( ( "+" | "-" ) multiplication )*
    //   comparison    → addition ( ( "=" | "<" | ">" | "<=" | ">=" ) addition )*
    //                 | addition ( "is" word-comparison )
    //   both symbol and word forms work in both expression and condition position.
    //   expr-not      → "not" expr-not | comparison
    //   expr-and      → expr-not  ( "and" expr-not  )*
    //   expr-or       → expr-and  ( "or"  expr-and  )*
    // not/and/or included here so parenthesised grouping works in condition context
    // (e.g. not (flag or other)) — same precedence as the condition grammar.

    private IExpression ParseExpression()
    {
        var left = ParsePipeExpr();
        SkipNoise();

        if (Peek().Type == TokenType.But)
        {
            var lineTok = Advance(); // consume 'but'
            var line = lineTok.Line;
            var col = lineTok.Column;
            SkipNoise();
            if (Peek().Type == TokenType.On)
            {
                Advance(); // consume 'on'
                SkipNoise();
                Consume(TokenType.Failure);
                SkipNoise();
                left = new FailureFallback(left, ParseExprOr(), line, col);
                return ParseWhenSuffix(left);
            }
            Consume(TokenType.Void);
            SkipNoise();
            Consume(TokenType.Is);
            SkipNoise();
            left = new ButVoidDefault(left, ParseExprOr(), line, col);
            return ParseWhenSuffix(left);
        }

        if (Peek().Type == TokenType.Or && PeekAfterCurrent() == TokenType.Pass)
        {
            var lineTok = Advance(); // consume 'or'
            var line = lineTok.Line;
            var col = lineTok.Column;
            SkipNoise();
            Consume(TokenType.Pass);
            SkipNoise();        // eats 'the'
            Consume(TokenType.Failure);
            SkipNoise();
            Consume(TokenType.Off);
            return ParseWhenSuffix(new FailurePropagate(left, line, col));
        }

        return ParseWhenSuffix(left);
    }

    // `<value> when <condition>, otherwise <alternative>`
    //
    // The loosest-binding thing in the expression grammar, so `a but void is b when c, otherwise d`
    // reads as `(a but void is b) when c, otherwise d` — the conditional chooses between two whole
    // values, which is the only way the form is ever worth reading.
    //
    // ★ There is no ambiguity with a separator comma, and it needs no lookahead: `when` REQUIRES
    // `, otherwise`, so `f(x when c, y)` is not "two arguments", it is an unfinished conditional
    // and says so. That is what makes it safe to allow inside an argument or element list —
    // `("small" when n is 1, otherwise "big", "fixed")` is deterministically two elements.
    //
    // The alternative recurses through ParseExpression, so the form is RIGHT-associative and
    // `a when p, otherwise b when q, otherwise c` chains as a fallback ladder.
    private IExpression ParseWhenSuffix(IExpression left)
    {
        SkipNoise();
        if (Peek().Type != TokenType.When) return left;

        var whenTok = Advance();
        SkipNoise();
        var condition = ParseExprOr();
        SkipNoise();
        Consume(TokenType.Comma);
        SkipNoise();
        Consume(TokenType.Otherwise);
        SkipNoise();
        return new ConditionalExpression(left, condition, ParseExpression(), whenTok.Line, whenTok.Column);
    }

    // Handles '|' in expression context: 'run "a" | run "b"' inside parens/Define/etc.
    // Precedence: lower than all value operators; failure-handler suffixes ('but on failure',
    // 'or pass the failure off') are applied by ParseExpression on the outside of this.
    private IExpression ParsePipeExpr()
    {
        var left = ParseExprOr();
        SkipNoise();
        while (Peek().Type == TokenType.Pipe)
        {
            var pipeLineTok = Advance(); // consume '|'
            var pipeLine = pipeLineTok.Line;
            var pipeCol = pipeLineTok.Column;
            SkipNoise();
            left = new PipeExpression(left, ParseExprOr(), pipeLine, pipeCol);
            SkipNoise();
        }
        return left;
    }

    private IExpression ParseExprOr()
    {
        var left = ParseExprXor();
        while (Peek().Type == TokenType.Or && PeekAfterCurrent() != TokenType.Pass)
        {
            var lineTok = Advance();
            var line = lineTok.Line;
            var col = lineTok.Column;
            SkipNoise();
            left = new BinaryExpression(left, TokenType.Or, ParseExprXor(), line, col);
        }
        return left;
    }

    // xor binds tighter than or and looser than and, mirroring the & > ^ > | nesting every
    // C-family language uses — so `a and b xor c or d` groups as `((a and b) xor c) or d`.
    private IExpression ParseExprXor()
    {
        var left = ParseExprAnd();
        while (Peek().Type == TokenType.Xor)
        {
            var lineTok = Advance();
            var line = lineTok.Line;
            var col = lineTok.Column;
            SkipNoise();
            left = new BinaryExpression(left, TokenType.Xor, ParseExprAnd(), line, col);
        }
        return left;
    }

    private IExpression ParseExprAnd()
    {
        var left = ParseExprNot();
        while (Peek().Type == TokenType.And)
        {
            var lineTok = Advance();
            var line = lineTok.Line;
            var col = lineTok.Column;
            SkipNoise();
            left = new BinaryExpression(left, TokenType.And, ParseExprNot(), line, col);
        }
        return left;
    }

    private IExpression ParseExprNot()
    {
        if (Peek().Type == TokenType.Not)
        {
            var lineTok = Advance();
            var line = lineTok.Line;
            var col = lineTok.Column;
            SkipNoise();
            return new UnaryExpression(TokenType.Not, ParseExprNot(), line, col);
        }
        return ParseComparison();
    }

    private IExpression ParseComparison()
    {
        var left = ParseJoinedTo();

        // Symbol comparisons: = < > <= >=
        while (Peek().Type is TokenType.Equal or TokenType.Lt or TokenType.Gt
                           or TokenType.Lte or TokenType.Gte)
        {
            var opTok = Advance();
            SkipNoise();
            left = new BinaryExpression(left, opTok.Type, ParseJoinedTo(), opTok.Line, opTok.Column);
        }

        // Word-form comparisons in expression position: "is" / "is not" / "is greater than" /
        // "is less than" / "is or more" / "is or less" / "is a <type>" / "is not a <type>".
        // Same operations as the condition-form equivalents — both produce a boolean.
        if (Peek().Type == TokenType.Is)
        {
            var isLineTok = Consume(TokenType.Is);
            var isLine = isLineTok.Line;
            var isCol = isLineTok.Column;
            if (Peek().Type == TokenType.Article) // "is a/an <type>"
            {
                Advance(); SkipNoise();
                return new IsTypeCheck(left, ParseTypeAnnotation(), false, isLine, isCol);
            }
            if (Peek().Type == TokenType.Not &&
                _pos + 1 < _tokens.Count && _tokens[_pos + 1].Type == TokenType.Article)
            {
                Advance(); Advance(); SkipNoise(); // consume 'not', then the article
                return new IsTypeCheck(left, ParseTypeAnnotation(), true, isLine, isCol);
            }
            SkipNoise();
            return ParseWordComparison(left, isLine, isCol);
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
        var lineTok = Advance(); // consume 'has'
        var line = lineTok.Line;
        var col = lineTok.Column;
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
            ? (IExpression)new MapHasEntry(left, keyExpr, line, col)
            : new MapHasKey(left, keyExpr, line, col);
    }

    // '<text> split by <delimiter>' — series of text. Sits between ParseHasCheck and
    // ParseTextContains so split/contains/has/joined-to are all available at the same
    // general "text/collection operator" tier, above arithmetic.
    private IExpression ParseSplitBy()
    {
        var left = ParseTextContains();
        SkipNoise();
        if (Peek().Type != TokenType.Split) return left;
        var lineTok = Advance(); // consume 'split'
        var line = lineTok.Line;
        var col = lineTok.Column;
        SkipNoise();
        Consume(TokenType.By);
        SkipNoise();
        var delimiter = ParseAddition();
        return new TextSplit(left, delimiter, line, col);
    }

    // '<text> contains <substring>' — fact. Sits just above ParseAddition.
    private IExpression ParseTextContains()
    {
        var left = ParseAddition();
        SkipNoise();
        if (Peek().Type != TokenType.Contains) return left;
        var lineTok = Advance(); // consume 'contains'
        var line = lineTok.Line;
        var col = lineTok.Column;
        SkipNoise();
        var substring = ParseAddition();
        return new TextContains(left, substring, line, col);
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
            var lineTok = Advance(); // consume 'joined'
            var line = lineTok.Line;
            var col = lineTok.Column;
            SkipNoise();
            Consume(TokenType.To);
            SkipNoise();
            left = new TextJoin(left, ParseAddition(), line, col);
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
            left = new BinaryExpression(left, opTok.Type, ParseMultiplication(), opTok.Line, opTok.Column);
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
            left = new BinaryExpression(left, opTok.Type, ParseUnary(), opTok.Line, opTok.Column);
        }
        return left;
    }

    private IExpression ParseUnary()
    {
        if (Peek().Type == TokenType.Minus)
        {
            var lineTok = Advance();
            var line = lineTok.Line;
            var col = lineTok.Column;
            return new UnaryExpression(TokenType.Minus, ParseUnary(), line, col);
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
            var lineTok = Advance(); // consume 'converted'
            var line = lineTok.Line;
            var col = lineTok.Column;
            SkipNoise();
            Consume(TokenType.To);
            SkipNoise();
            var targetTok = Peek();
            if (targetTok.Type == TokenType.NumberKw)
            {
                Advance(); // consume 'number'
                baseExpr = new NumberConvert(baseExpr, line, col);
            }
            else if (targetTok.Type == TokenType.Identifier &&
                     targetTok.Lexeme.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                Advance(); // consume 'text'
                baseExpr = new TextConvert(baseExpr, line, col);
            }
            // 'converted to hex/binary/octal' crosses from a quantity to a bit pattern. The base
            // names are contextual, like 'text' — recognised here and ordinary identifiers
            // everywhere else.
            else if (targetTok.Type == TokenType.Identifier
                     && BitsBaseFor(targetTok.Lexeme) is { } toBase)
            {
                Advance(); // consume 'hex' / 'binary' / 'octal'
                baseExpr = new BitsConvert(baseExpr, toBase, line, col);
            }
            else
            {
                throw new ParseException(targetTok,
                    "text, number, hex, binary or octal — expected after 'converted to'");
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
               Peek().Type == TokenType.Shifted ||
               IsWord("at") ||
               (Peek().Type == TokenType.In && PeekAfterCurrent() is TokenType.Uppercase or TokenType.Lowercase))
        {
            // `<bits> at <n> bits`. Matched by LEXEME, exactly as `item at (r, c)` is, because
            // neither `at` nor `bits` is reserved — both stay legal identifiers. The trailing
            // `bits` is what makes the phrase unmistakable.
            if (IsWord("at"))
            {
                var atTok = Advance();          // consume 'at'
                SkipNoise();
                var widthExpr = ParseUnary();
                SkipNoise();
                if (!IsWord("bits"))
                    throw new ParseException(Peek().Line, Peek().Column,
                        "expected 'bits' after a stated width \u2014 the phrase is '<value> at <n> bits', " +
                        "as in '0b0 at 3 bits'.");
                Advance();                      // consume 'bits'
                baseExpr = new BitsAtWidth(baseExpr, widthExpr, atTok.Line, atTok.Column);
                continue;
            }
            if (Peek().Type == TokenType.Shifted)
            {
                var lineTok = Advance();   // consume 'shifted'
                var line = lineTok.Line;
                var col = lineTok.Column;
                SkipNoise();
                // 'left' and 'right' are ordinary identifiers everywhere else — matched here by
                // lexeme so that 'the left of node' keeps working.
                var dir = Peek();
                bool left = dir.Type == TokenType.Identifier
                            && dir.Lexeme.Equals("left", StringComparison.OrdinalIgnoreCase);
                bool right = dir.Type == TokenType.Identifier
                             && dir.Lexeme.Equals("right", StringComparison.OrdinalIgnoreCase);
                if (!left && !right)
                    throw new ParseException(dir.Line, dir.Column, "expected 'left' or 'right' after 'shifted'");
                Advance();                   // consume the direction
                SkipNoise();
                Consume(TokenType.By);
                SkipNoise();
                // ParseUnary, not ParseExpression: 'x shifted left by 2 + 1' still reads as
                // '(x shifted left by 2) + 1', matching how the other trailing transforms bind,
                // but 'by -1' reaches the amount check and gets told what is wrong with it
                // instead of dying as "expected expression, got Minus".
                baseExpr = new BitsShift(baseExpr, left, ParseUnary(), line, col);
                SkipNoise();
                continue;
            }
            if (Peek().Type == TokenType.Sorted)
            {
                var lineTok = Advance(); // consume 'sorted'
                var line = lineTok.Line;
                var col = lineTok.Column;
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
                baseExpr = new SortExpression(baseExpr, byField, reverse, line, col);
            }
            else if (Peek().Type == TokenType.Trimmed)
            {
                var lineTok = Advance(); // consume 'trimmed'
                var line = lineTok.Line;
                var col = lineTok.Column;
                baseExpr = new TextTrim(baseExpr, line, col);
            }
            else
            {
                var lineTok = Advance(); // consume 'in'
                var line = lineTok.Line;
                var col = lineTok.Column;
                SkipNoise();
                bool toUpper = Peek().Type == TokenType.Uppercase;
                Advance(); // consume 'uppercase'/'lowercase'
                baseExpr = new TextCase(baseExpr, toUpper, line, col);
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
            var lineTok = Advance();
            var line = lineTok.Line;
            var col = lineTok.Column;
            return new UnaryExpression(TokenType.Minus, ParseCorePrimary(), line, col);
        }
        return ParseCorePrimary();
    }

    private IExpression ParseCorePrimary()
    {
        // 'from <map>, the entry for <key>' — the map-first way to say a lookup, and the mirror of
        // `In <map>, the entry for <key> becomes <v>`, which is the only way to say a write. Without
        // it the two operations read in opposite orders: the map comes last to read it and first to
        // change it. Same node as the trailing form, so nothing downstream knows the difference.
        //
        // Expression-initial `from` means nothing else — every other use (`remove x from xs`,
        // `read all from the file`, `for each x from the input`) has something in front of it.
        if (Peek().Type == TokenType.From)
            return ParseLeadingMapLookup();

        // 'the <name> of <expr>' → named record field access.
        // Checked BEFORE SkipNoise so we can still see the leading 'the'.
        // 'the first of s' is not named access: ordinal-word identifiers are excluded from
        // IsFieldNameToken(forAccess:true), so IsNamedAccessPattern returns false for them.
        // 'the number of s' is also not named access: NumberKw is not Identifier/Category/Key/Characters.
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
            return new RecordNamedAccess(identTok.Lexeme, ParseCorePrimary(), identTok.Line, identTok.Column);
        }

        SkipNoise(); // articles are noise before any value
        var tok = Peek();
        IExpression baseExpr;
        // EffectiveType, not tok.Type: book words lex as Identifiers so they stay usable as
        // names, and this is where a confirmed shape routes one to its own case.
        switch (EffectiveType(tok))
        {
            case TokenType.Number:
                baseExpr = new NumberLiteral(decimal.Parse(Advance().Lexeme));
                break;
            case TokenType.Bits:
                baseExpr = ParseBitsLiteral(Advance());
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
                var idTok = Advance();
                if (IsOrdinalLexeme(idTok.Lexeme))
                {
                    SkipNoise();
                    // 'first/last <count> characters of <text>' — text substring from edge.
                    // Detected by: isEdge ordinal + count expression (Number or LParen) follows.
                    bool isEdge = idTok.Lexeme.Equals("first", StringComparison.OrdinalIgnoreCase) ||
                                  idTok.Lexeme.Equals("last",  StringComparison.OrdinalIgnoreCase);
                    if (isEdge && Peek().Type is TokenType.Number or TokenType.LParen)
                    {
                        var count = ParseAddition();
                        SkipNoise();
                        Consume(TokenType.Characters);
                        SkipNoise();
                        Consume(TokenType.Of);
                        SkipNoise();
                        var textTarget = ParseCorePrimary();
                        bool fromStart = idTok.Lexeme.Equals("first", StringComparison.OrdinalIgnoreCase);
                        baseExpr = new TextSubstringEdge(textTarget, count, fromStart, idTok.Line, idTok.Column);
                        break;
                    }
                    if (Peek().Type == TokenType.Of)
                    {
                        // '<ordinal> of <series>' — series positional access.
                        var index = OrdinalToIndex(idTok.Lexeme);
                        Consume(TokenType.Of);
                        SkipNoise();
                        var target = ParseCorePrimary();
                        baseExpr = new SeriesAccess(target, index, idTok.Line, idTok.Column);
                        break;
                    }
                    // Ordinal word not in accessor shape → plain variable reference.
                }
                baseExpr = new VariableReference(idTok.Lexeme, idTok.Line, idTok.Column);
                break;
            }
            case TokenType.It:
            {
                var t = Advance();
                baseExpr = new VariableReference("it", t.Line, t.Column);
                break;
            }
            case TokenType.One:
            {
                var t = Advance();
                baseExpr = new VariableReference("one", t.Line, t.Column);
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
            case TokenType.Item:
            {
                var itemTok = Advance();
                SkipNoise();
                if (IsWord("at"))
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
                    baseExpr = new MatrixAccess(matTarget, row, col, itemTok.Line, itemTok.Column);
                }
                else if (LooksLikeIndexExprStart())
                {
                    // Series indexing: "item <N> of <series>" — ParseCorePrimary so
                    // postfix ops bind to the outer access, not to the inner target.
                    var idx = ParseExpression();
                    SkipNoise();
                    Consume(TokenType.Of);
                    SkipNoise();
                    var target = ParseCorePrimary();
                    baseExpr = new SeriesAccess(target, idx, itemTok.Line, itemTok.Column);
                }
                else
                {
                    // Plain variable reference — 'item' used as an iterator name, e.g.
                    // 'for each item from the input:' or 'for each item in series:'.
                    baseExpr = new VariableReference("item", itemTok.Line, itemTok.Column);
                }
                break;
            }
            case TokenType.Matrix:
            {
                var matrixLineTok = Advance(); // consume 'matrix'
                var matrixLine = matrixLineTok.Line;
                var matrixCol = matrixLineTok.Column;
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
                    baseExpr = new MatrixLiteral(rows, matrixLine, matrixCol);
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
                    if (IsWord("filled"))
                    {
                        Advance(); // consume 'filled'
                        SkipNoise();
                        Consume(TokenType.With);
                        SkipNoise();
                        fillExpr = ParseExpression();
                    }
                    baseExpr = new MatrixSized(rowsExpr, colsExpr, fillExpr, matrixLine, matrixCol);
                }
                break;
            }
            case TokenType.Random:
            {
                // 'a random number from <low> to <high>' — inclusive whole-number range
                // 'a random item from <series>'          — voidable random element
                // 'a random guess'                       — fact (coin flip)
                // The leading article 'a' is already consumed by SkipNoise above.
                var randomLineTok = Advance(); // consume 'random'
                var randomLine = randomLineTok.Line;
                var randomCol = randomLineTok.Column;
                SkipNoise();
                if (Peek().Type == TokenType.NumberKw)
                {
                    Advance(); // consume 'number'
                    SkipNoise();
                    Consume(TokenType.From);
                    SkipNoise();
                    var low = ParseAddition();
                    SkipNoise();
                    Consume(TokenType.To);
                    SkipNoise();
                    var high = ParseAddition();
                    baseExpr = new RandomNumber(low, high, randomLine, randomCol);
                }
                else if (Peek().Type == TokenType.Item)
                {
                    Advance(); // consume 'item'
                    SkipNoise();
                    Consume(TokenType.From);
                    SkipNoise();
                    // ParseCorePrimary so postfix ops like 'converted to text' bind to the
                    // outer RandomItem, not to the series target.
                    baseExpr = new RandomItem(ParseCorePrimary(), randomLine, randomCol);
                }
                else if (IsWord("guess"))
                {
                    Advance(); // consume 'guess'
                    baseExpr = new RandomGuess(randomLine, randomCol);
                }
                else
                {
                    throw new ParseException(Peek(), "number, item, or guess — expected after 'random'");
                }
                break;
            }
            case TokenType.Randomly:
            {
                // 'randomly shuffled <series>' — non-mutating shuffle; returns new series of same type.
                var randomlyLineTok = Advance(); // consume 'randomly'
                var randomlyLine = randomlyLineTok.Line;
                var randomlyCol = randomlyLineTok.Column;
                SkipNoise();
                ConsumeWord("shuffled");
                SkipNoise();
                // ParseCorePrimary so postfix ops bind to the outer RandomlyShuffled.
                baseExpr = new RandomlyShuffled(ParseCorePrimary(), randomlyLine, randomlyCol);
                break;
            }
            case TokenType.NumberKw:
            {
                var numLineTok = Advance();
                var numLine = numLineTok.Line;
                var numCol = numLineTok.Column;
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                baseExpr = new SeriesLength(ParseCorePrimary(), numLine, numCol);
                break;
            }
            case TokenType.LengthKw:
            {
                var lineTok = Advance();
                var line = lineTok.Line;
                var col = lineTok.Column;
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                baseExpr = new TextLength(ParseCorePrimary(), line, col);
                break;
            }
            case TokenType.Position:
            {
                // 'the position of <substring> in <text>' — mirrors 'the entry for <key> in <map>'.
                var posLineTok = Advance(); // consume 'position'
                var posLine = posLineTok.Line;
                var posCol = posLineTok.Column;
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                var substringExpr = ParseExpression();
                SkipNoise();
                Consume(TokenType.In);
                SkipNoise();
                baseExpr = new TextFind(substringExpr, ParseCorePrimary(), posLine, posCol);
                break;
            }
            case TokenType.Characters:
            {
                // 'the characters from <from> to <to> of <text>' / '... to the end of <text>'.
                var charsLineTok = Advance(); // consume 'characters'
                var charsLine = charsLineTok.Line;
                var charsCol = charsLineTok.Column;
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
                baseExpr = new TextSubstringRange(textTarget, fromExpr, toExpr, charsLine, charsCol);
                break;
            }
            case TokenType.Replace:
            {
                // 'replace <old> with <new> in <text>' — replaces all occurrences.
                var replaceLineTok = Advance(); // consume 'replace'
                var replaceLine = replaceLineTok.Line;
                var replaceCol = replaceLineTok.Column;
                SkipNoise();
                var oldExpr = ParseAddition();
                SkipNoise();
                Consume(TokenType.With);
                SkipNoise();
                var newExpr = ParseAddition();
                SkipNoise();
                Consume(TokenType.In);
                SkipNoise();
                baseExpr = new TextReplace(ParseCorePrimary(), oldExpr, newExpr, replaceLine, replaceCol);
                break;
            }
            case TokenType.Range:
            {
                var lineTok = Advance(); // consume 'range'
                var line = lineTok.Line;
                var col = lineTok.Column;
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
                baseExpr = new RangeExpression(start, end, step, line, col);
                break;
            }
            case TokenType.Void:
            {
                var lineTok = Advance();
                var line = lineTok.Line;
                var col = lineTok.Column;
                baseExpr = new VoidLiteral(line, col);
                break;
            }
            case TokenType.TrueKw:
            {
                var lineTok = Advance();
                var line = lineTok.Line;
                var col = lineTok.Column;
                baseExpr = new BooleanLiteral(true, line, col);
                break;
            }
            case TokenType.FalseKw:
            {
                var lineTok = Advance();
                var line = lineTok.Line;
                var col = lineTok.Column;
                baseExpr = new BooleanLiteral(false, line, col);
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
                    baseExpr = new FailureLiteral(message, category, failTok.Line, failTok.Column);
                }
                else
                {
                    baseExpr = new VariableReference("the failure", failTok.Line, failTok.Column);
                }
                break;
            }
            case TokenType.Exception:
            {
                // 'the exception' — 'the' is already stripped by SkipNoise.
                // Only meaningful inside an 'In case of exception' handler block.
                var exTok = Advance(); // consume 'exception'
                baseExpr = new VariableReference("the exception", exTok.Line, exTok.Column);
                break;
            }
            case TokenType.Cast:
                baseExpr = ParseCastExpression();
                break;
            case TokenType.Unbury:
            {
                // unbury <stash> — an EXPRESSION giving `voidable T`, so the spent case is narrowed
                // like any other absent value rather than signalled out of band.
                var unTok = Advance();
                SkipNoise();
                baseExpr = new UnburyExpression(ParseUnary(), unTok.Line, unTok.Column);
                break;
            }
            case TokenType.Series:
                baseExpr = ParseSeriesLiteralExpr();
                break;
            case TokenType.Record:
                baseExpr = ParseRecordLiteralExpr();
                break;
            case TokenType.New:
            {
                var newLineTok = Advance(); // consume 'new'
                var newLine = newLineTok.Line;
                var newCol = newLineTok.Column;
                SkipNoise();
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
                baseExpr = new ObjectLiteral(typeName, positionals2, namedFields2, newLine, newCol);
                break;
            }
            case TokenType.CatalogueKw:
            {
                // "a catalogue [of (A or B)] [with (...)]" — heterogeneous series
                var catLineTok = Advance(); // consume 'catalogue'
                var catLine = catLineTok.Line;
                var catCol = catLineTok.Column;
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
                baseExpr = new SeriesLiteral(catElems, catAnnotation, catLine, catCol);
                break;
            }
            case TokenType.AtlasKw:
            {
                // "an atlas [from K to (A or B)] [with ("k" : v, ...)]" — heterogeneous map
                var atlasLineTok = Advance(); // consume 'atlas'
                var atlasLine = atlasLineTok.Line;
                var atlasCol = atlasLineTok.Column;
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
                baseExpr = new MapLiteral(atlasKeyType, atlasValType, atlasPairs, atlasLine, atlasCol);
                break;
            }
            case TokenType.Map:
            {
                // "a map [from K to V] with ("k" : v, ...)" — map literal
                // Optional 'from K to V' gives an explicit key/value type annotation,
                // enabling empty typed maps and typed populated maps.
                var mapLineTok = Advance(); // consume 'map'
                var mapLine = mapLineTok.Line;
                var mapCol = mapLineTok.Column;
                SkipNoise();
                CufetType? mapKeyType = null;
                CufetType? mapValType = null;
                if (Peek().Type == TokenType.From)
                {
                    Advance(); SkipNoise(); // consume 'from'
                    mapKeyType = ParseTypeAnnotation(); SkipNoise();
                    Consume(TokenType.To); SkipNoise();
                    mapValType = ParseTypeAnnotation(); SkipNoise();
                }
                var pairs = new List<(IExpression Key, IExpression Value)>();
                // 'with (...)' is OPTIONAL once the types are given: `a map from text to number.`
                // is an empty typed map, the same sugar `a series of number.` and
                // `an atlas from text to (…)` already had. Map was the only container missing it.
                //
                // Still required when the types are absent, because `a map.` has neither an
                // annotation nor entries to infer from — there would be nothing to build.
                if (mapValType == null || Peek().Type == TokenType.With)
                {
                    Consume(TokenType.With);
                    SkipNoise();
                    Consume(TokenType.LParen);
                    SkipNoise();
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
                }
                baseExpr = new MapLiteral(mapKeyType, mapValType, pairs, mapLine, mapCol);
                break;
            }
            case TokenType.Channel:
            {
                var chanLineTok = Advance(); // consume 'channel'
                var chanLine = chanLineTok.Line;
                var chanCol = chanLineTok.Column;
                SkipNoise();
                Consume(TokenType.Of); SkipNoise();
                var elemType = ParseTypeAnnotation();
                baseExpr = new ChannelCreation(elemType, chanLine, chanCol);
                break;
            }
            case TokenType.Delivery:
            {
                var delLineTok = Advance(); // consume 'delivery'
                var delLine = delLineTok.Line;
                var delCol = delLineTok.Column;
                SkipNoise();
                Consume(TokenType.From); SkipNoise();
                var chanExpr = ParseExpression();
                baseExpr = new DeliveryExpression(chanExpr, delLine, delCol);
                break;
            }
            case TokenType.Awaited:
            {
                var awLineTok = Advance(); // consume 'awaited'
                var awLine = awLineTok.Line;
                var awCol = awLineTok.Column;
                SkipNoise();
                // 'result' is contextual — not a reserved keyword; skip by lexeme
                if (Peek().Type == TokenType.Identifier &&
                    Peek().Lexeme.Equals("result", StringComparison.OrdinalIgnoreCase))
                    Advance();
                SkipNoise();
                Consume(TokenType.Of); SkipNoise();
                // ParseExprOr (not ParseExpression) so that 'but on failure' is left for
                // the outer ParseExpression to apply to the whole AwaitedResultExpression.
                var taskExpr = ParseExprOr();
                baseExpr = new AwaitedResultExpression(taskExpr, awLine, awCol);
                break;
            }
            case TokenType.Entry:
            {
                // "the entry for <key> in <map>"
                var entryLineTok = Advance(); // consume 'entry'
                var entryLine = entryLineTok.Line;
                var entryCol = entryLineTok.Column;
                SkipNoise();
                Consume(TokenType.For);
                SkipNoise();
                var keyExpr = ParseExpression();
                SkipNoise();
                Consume(TokenType.In);
                SkipNoise();
                baseExpr = new MapLookup(ParseCorePrimary(), keyExpr, entryLine, entryCol);
                break;
            }
            case TokenType.Size:
            {
                // "the size of <map>"
                var sizeLineTok = Advance(); // consume 'size'
                var sizeLine = sizeLineTok.Line;
                var sizeCol = sizeLineTok.Column;
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                baseExpr = new MapSize(ParseCorePrimary(), sizeLine, sizeCol);
                break;
            }
            // 'the rows of <matrix>' and 'the columns of <matrix>' have no case here on purpose.
            // They are ordinary named access now, and the type checker decides what the field
            // means from the type of the target — see InferRecordNamedAccess.
            case TokenType.FunctionKw:
            {
                // "a function given (<params>): <body>" — anonymous lambda literal.
                // The leading article 'a'/'an' was already consumed by SkipNoise above.
                var lambdaLineTok = Advance(); // consume 'function'
                var lambdaLine = lambdaLineTok.Line;
                var lambdaCol = lambdaLineTok.Column;
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
                baseExpr = new LambdaLiteral(lambdaParams, lambdaBody, lambdaLine, lambdaCol);
                break;
            }
            case TokenType.Run:
            {
                // run <program>                              → result or failure
                // run <program> with arguments (<arg>, ...) → result or failure
                // "arguments" is contextual (not a reserved keyword) — checked by lexeme.
                // Arguments are passed directly to the OS; no shell is invoked.
                var runLineTok = Advance(); // consume 'run'
                var runLine = runLineTok.Line;
                var runCol = runLineTok.Column;
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
                baseExpr = new RunExpression(programExpr, runArgs, runLine, runCol);
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
                var readLineTok = Advance(); // consume 'read'
                var readLine = readLineTok.Line;
                var readCol = readLineTok.Column;
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
                    // Not a missing feature — a line read needs somewhere to keep its position, and
                    // a path is not that. `read a line from the file "x"` would reopen and hand back
                    // the first line every time. Opening it as a stream is the form that works, and
                    // is what to name here: pointing at `read all` would send someone who is trying
                    // NOT to load the whole file straight to the thing that loads the whole file.
                    if (stdinForm == ReadForm.Line)
                        throw new ParseException(Peek().Line, Peek().Column,
                            "a line has to be read from an open stream, not from a path — a path has nowhere to " +
                            "remember how far you have read, so every read would hand back the first line. Open " +
                            "it first: 'With the file \"x\" open for reading as src:', then 'read a line from src'. " +
                            "('read all from the file \"x\"' needs no stream, because it keeps no position.)");
                    Advance(); // consume 'file'
                    SkipNoise();
                    // ParseExprOr not ParseExpression: stops before 'but on failure' / 'or pass
                    // the failure off', which belong to the outer expression that wraps this read.
                    var pathExpr = ParseExprOr();
                    var fileForm = stdinForm == ReadForm.AllLines ? FileReadForm.AllLines : FileReadForm.All;
                    baseExpr = new FileReadExpression(fileForm, pathExpr, readLine, readCol);
                }
                else
                {
                    // General stream source — 'the input' is a pre-defined stream of text binding.
                    // SkipNoise() above already consumed 'the', so we parse the rest of the expression.
                    var sourceExpr = ParseExprOr();
                    baseExpr = new ReadExpression(stdinForm, sourceExpr, readLine, readCol);
                }

                break;
            }
            case TokenType.ContentsKw:
            {
                // "the contents of the directory <path>"
                var contentsLineTok = Advance(); // consume 'contents'
                var contentsLine = contentsLineTok.Line;
                var contentsCol = contentsLineTok.Column;
                SkipNoise();
                Consume(TokenType.Of);
                SkipNoise();
                Consume(TokenType.DirectoryKw);
                SkipNoise();
                // ParseJoinedTo: leaves 'but on failure' / 'or pass' for the outer ParseExpression.
                baseExpr = new DirectoryContentsExpression(ParseJoinedTo(), contentsLine, contentsCol);
                break;
            }
            case TokenType.PathKw:
            {
                // "the path <path> exists"          →  PathCheckExpression(Exists)
                // "the path <path> is a directory"  →  PathCheckExpression(IsDirectory)
                // "the path <path> is a file"       →  PathCheckExpression(IsFile)
                // ParseJoinedTo for path: doesn't consume 'is' (needed for is-a-directory/file).
                var pathLineTok = Advance(); // consume 'path'
                var pathLine = pathLineTok.Line;
                var pathCol = pathLineTok.Column;
                SkipNoise();
                var pathExpr = ParseJoinedTo();
                SkipNoise();
                if (Peek().Type == TokenType.Identifier &&
                    Peek().Lexeme.Equals("exists", StringComparison.OrdinalIgnoreCase))
                {
                    Advance(); // consume 'exists' (contextual)
                    baseExpr = new PathCheckExpression(pathExpr, PathCheckKind.Exists, pathLine, pathCol);
                }
                else
                {
                    Consume(TokenType.Is);
                    SkipNoise(); // consumes 'a' (Article) — SkipNoise eats all Articles
                    if (Peek().Type == TokenType.DirectoryKw)
                    {
                        Advance(); // consume 'directory'
                        baseExpr = new PathCheckExpression(pathExpr, PathCheckKind.IsDirectory, pathLine, pathCol);
                    }
                    else if (Peek().Type == TokenType.File)
                    {
                        Advance(); // consume 'file'
                        baseExpr = new PathCheckExpression(pathExpr, PathCheckKind.IsFile, pathLine, pathCol);
                    }
                    else
                    {
                        throw new ParseException(Peek(), "expected 'directory' or 'file' after 'the path ... is a'");
                    }
                }
                break;
            }
            case TokenType.EnvironmentKw:
            {
                // "the environment variable <text-name>"
                // 'variable' is contextual — parsed by lexeme, not reserved.
                var envLineTok = Advance(); // consume 'environment'
                var envLine = envLineTok.Line;
                var envCol = envLineTok.Column;
                if (Peek().Type != TokenType.Identifier ||
                    !Peek().Lexeme.Equals("variable", StringComparison.OrdinalIgnoreCase))
                    throw new ParseException(Peek(), "expected 'variable' after 'environment'");
                Advance(); // consume 'variable'
                SkipNoise();
                // ParseExprOr so that 'but void is' (parsed one level above) stays outside the name.
                baseExpr = new EnvironmentVariableExpression(ParseExprOr(), envLine, envCol);
                break;
            }
            case TokenType.CurrentKw:
            {
                // 'the current directory'. 'current' only reaches this case through EffectiveType,
                // which promotes the identifier when 'directory' follows it — so `Define current
                // as 0.` is untouched.
                var cdLineTok = Advance();   // consume 'current'
                var cdLine = cdLineTok.Line;
                var cdCol = cdLineTok.Column;
                SkipNoise();
                Consume(TokenType.DirectoryKw);
                baseExpr = new CurrentDirectoryExpression(cdLine, cdCol);
                break;
            }
            case TokenType.InterruptKw:
            {
                // "an interrupt is requested" — fixed-phrase fact; 'an' consumed by SkipNoise above.
                // 'requested' is contextual (lexeme-checked), not reserved.
                var intLineTok = Advance(); // consume 'interrupt'
                var intLine = intLineTok.Line;
                var intCol = intLineTok.Column;
                Consume(TokenType.Is);
                if (Peek().Type != TokenType.Identifier ||
                    !Peek().Lexeme.Equals("requested", StringComparison.OrdinalIgnoreCase))
                    throw new ParseException(Peek(), "expected 'requested' after 'an interrupt is'");
                Advance(); // consume 'requested'
                baseExpr = new InterruptRequestedExpression(intLine, intCol);
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
            baseExpr = new PossessiveAccess(baseExpr, string.Join(" ", parts), possTok.Line, possTok.Column);
            SkipNoise();
        }

        // Book-function call postfix: math's floor of x  →  CastExpression(PossessiveAccess(math, "floor"), [x])
        // Only fires when the left side is a PossessiveAccess (no valid Cufet syntax has object-field 'of').
        // Single-arg: ParsePrimary() so arithmetic operators bind to the result, not the argument.
        //   math's log of x / math's log of 10  →  log(x) / log(10), not log(x / log(10))
        // Multi-arg: 'of (<e1>, <e2>, ...)' uses ParseExpression() per arg.
        while (baseExpr is PossessiveAccess && Peek().Type == TokenType.Of)
        {
            var ofLineTok = Advance(); // consume 'of'
            var ofLine = ofLineTok.Line;
            var ofCol = ofLineTok.Column;
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
            baseExpr = new CastExpression(baseExpr, callArgs, ofLine, ofCol);
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
        return new RecordLiteral(positionals, namedFields, recordTok.Line, recordTok.Column);
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
    // Reserved keywords are excluded by IsFieldNameToken, and ordinal-word identifiers
    // (first/second/.../last) are also excluded — they're recognized as series positional
    // accessors in this shape, not record field names.
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
    // forAccess=true: excludes the entire reserved-keyword set. No keyword can be a user-defined
    //   field name (field names are identifiers; keywords are not). Three exceptions:
    //   - Key ("the key of mapping") and Category ("the category of the failure") appear in
    //     named-access patterns on language-defined types.
    //   - Characters ("the characters of r") is disambiguated from substring syntax by 'from':
    //     'the characters from N to M of text' has 'from' after the keyword, so IsNamedAccessPattern
    //     returns false (it requires 'of' immediately after the name). 'the characters of r' works.
    // forAccess=false: permissive — all word tokens allowed for field-literal positions where the
    //   field name is followed by its value (not 'of'), so there is no access-pattern ambiguity.
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
        // For access patterns, exclude the entire reserved-keyword set at once.
        // Field names are always user-defined identifiers — no keyword can ever be one, so no
        // keyword can legitimately appear in 'the <keyword> of <expr>'. This kills the
        // keyword-as-field-name mis-fire class completely and covers all future keywords
        // automatically (no per-keyword patches needed when new keywords are added).
        // Three narrow exceptions: Key, Category, and Characters (see comment on forAccess=true above).
        if (forAccess &&
            tok.Type is not TokenType.Identifier
                      and not TokenType.Category
                      and not TokenType.Key
                      and not TokenType.Characters &&
            !(tok.Type == TokenType.Article &&
              !tok.Lexeme.Equals("the", StringComparison.OrdinalIgnoreCase)))
            return false;
        // Ordinal-word identifiers are not valid field names in access position — they are
        // recognized as series positional accessors in "the <ordinal> of <series>" shape.
        if (forAccess && tok.Type == TokenType.Identifier && IsOrdinalLexeme(tok.Lexeme))
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

        // 'Bind making a <type> [or failure] to <name>, given (...): ...'
        // — named constructor; 'making a <type>' sits in the return-type slot.
        string? constructsTypeName = null;
        CufetType? returnType;
        if (Peek().Type == TokenType.MakingKw)
        {
            Advance(); SkipNoise(); // consume 'making'; SkipNoise eats the 'a' article
            constructsTypeName = Consume(TokenType.Identifier).Lexeme;
            SkipNoise();
            // Shell ObjectType used as return type; TypeChecker resolves to canonical instance.
            CufetType inner = new ObjectType(constructsTypeName, [], [], []);
            if (Peek().Type == TokenType.Or && PeekAfterCurrent() == TokenType.Failure)
            {
                Advance(); SkipNoise(); // consume 'or'
                Consume(TokenType.Failure); SkipNoise();
                returnType = new FailureType(inner);
            }
            else
            {
                returnType = inner;
            }
        }
        else
        {
            returnType = ParseReturnType();
            SkipNoise();
        }

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
        // ★ A comma here means one of two things, and `given` is what tells them apart. A function
        // that takes no parameters reaches its inline body through this same comma —
        // `Bind number to leg-pairs, one's legs / 2.` — so consuming `given` unconditionally made
        // the inline form unavailable to exactly the functions shortest enough to want it.
        if (Peek().Type == TokenType.Comma && PeekPastNoiseIs(TokenType.Given))
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
        _inFreeFunction = untoType == null && constructsTypeName == null && !savedInObjectDef;

        // A function that declares a return type gets the EXPRESSION form, so `Return` is implicit;
        // a void one gets the statement form, there being no value to imply a return for. Both are
        // reached by the same comma — the difference is what one thing means for that body.
        _functionDepth++;
        var body = returnType == null ? ParseVoidBodyOrBlock() : ParseValueBodyOrBlock();
        _functionDepth--;
        _inObjectDef    = savedInObjectDef;
        _inFreeFunction = savedInFreeFunction;

        return new BindStatement(name, returnType, parameters, body, untoType, constructsTypeName, bindTok.Line, bindTok.Column);
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
        return new CastStatement(cast.Function, cast.Args, cast.Line, cast.Column);
    }

    // Returns a CastExpression for both free-function calls and method dispatch.
    // Cast greet on alice (no parens) → CastExpression(VarRef("greet"), [alice], line, col).
    // Cast steer on (racer, 90)         → CastExpression(VarRef("steer"), [racer, 90], line, col).
    // Cast racer's steer on (90)        → CastExpression(PossessiveAccess(racer, steer), [90], line, col).
    private IExpression ParseCastExpression()
    {
        var lineTok = Consume(TokenType.Cast);
        var line = lineTok.Line;
        var col = lineTok.Column;
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
                    throw new ParseException(line, col,
                        "identifier — method name must be a plain identifier in 'Cast method on receiver'");
                var receiver = ParsePostfix();
                return new CastExpression(funcExpr, new IExpression[] { receiver }, line, col);
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
            return new CastExpression(funcExpr, args, line, col);
        }

        // 'cast collections's transpose of (m)' — the book-of loop inside ParsePostfix already
        // built the full CastExpression; returning it without another wrapper is correct here.
        if (funcExpr is CastExpression bookCall)
            return bookCall;

        return new CastExpression(funcExpr, [], line, col);
    }

    private TryStatement ParseTryStatement()
    {
        var lineTok = Consume(TokenType.Try);
        var line = lineTok.Line;
        var col = lineTok.Column;
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

        return new TryStatement(body, failureHandler, exceptionHandler, line, col);
    }

    private SuppressStatement ParseSuppressStatement()
    {
        var lineTok = Consume(TokenType.Suppress);
        var line = lineTok.Line;
        var col = lineTok.Column;
        SkipNoise(); // skips 'the'
        Consume(TokenType.Exception);
        SkipNoise();
        Consume(TokenType.Dot);
        return new SuppressStatement(line, col);
    }

    private SendStatement ParseSendStatement()
    {
        var lineTok = Consume(TokenType.Send);
        var line = lineTok.Line;
        var col = lineTok.Column;
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Through);
        SkipNoise();
        var channel = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new SendStatement(value, channel, line, col);
    }

    private CloseStatement ParseCloseStatement()
    {
        var lineTok = Consume(TokenType.Close);
        var line = lineTok.Line;
        var col = lineTok.Column;
        SkipNoise();
        var channel = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new CloseStatement(channel, line, col);
    }

    private AcknowledgeInterruptStatement ParseAcknowledgeInterruptStatement()
    {
        var lineTok = Consume(TokenType.AcknowledgeKw); // consume 'Acknowledge'
        var line = lineTok.Line;
        var col = lineTok.Column;
        SkipNoise();                                       // consumes 'the' (Article)
        Consume(TokenType.InterruptKw);                    // consume 'interrupt'
        SkipNoise();
        Consume(TokenType.Dot);
        return new AcknowledgeInterruptStatement(line, col);
    }

    private YieldStatement ParseYieldStatement()
    {
        var lineTok = Consume(TokenType.YieldKw); // consume 'Yield'
        var line = lineTok.Line;
        var col = lineTok.Column;
        SkipNoise();
        Consume(TokenType.Dot);
        return new YieldStatement(line, col);
    }

    // Seed the chance with <number>.
    private SeedChanceStatement ParseSeedChanceStatement()
    {
        var lineTok = Advance(); // consume 'Seed' — an identifier, matched by lexeme
        var line = lineTok.Line;
        var col = lineTok.Column;
        SkipNoise();                               // eats 'the'
        var chanceTok = Peek();
        if (chanceTok.Type != TokenType.Identifier ||
            !chanceTok.Lexeme.Equals("chance", StringComparison.OrdinalIgnoreCase))
            throw new ParseException(chanceTok, "'chance' — expected after 'Seed the'");
        Advance(); // consume 'chance'
        SkipNoise();
        Consume(TokenType.With);
        SkipNoise();
        var seed = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new SeedChanceStatement(seed, line, col);
    }

    // ── Getters & Setters ─────────────────────────────────────────────────

    // Get <name> [unto <type>] as <type>: ... Done.
    // Parses both the inline form (inside an object body, no unto) and the external form
    // (top-level, with onto).  TypeChecker validates that inline getters have no UntoType.
    private GetterDeclaration ParseGetterDeclaration()
    {
        var savedInObjectDef    = _inObjectDef;
        var savedInFreeFunction = _inFreeFunction;
        _inObjectDef    = false; // getter body must not allow nested declarations
        _inFreeFunction = false;

        var lineTok = Consume(TokenType.GetKw);
        var line = lineTok.Line;
        var col = lineTok.Column;
        SkipNoise();
        var name = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();

        string? untoType = null;
        if (Peek().Type == TokenType.Unto)
        {
            Advance(); SkipNoise(); // consume 'unto'
            untoType = Consume(TokenType.Identifier).Lexeme;
            SkipNoise();
        }

        Consume(TokenType.As);
        SkipNoise(); // skip article 'a'/'an' before type keyword
        var returnType = ParseReturnType();
        if (returnType == null)
            throw new ParseException(Peek(), "a return type — getters cannot be void");
        SkipNoise();
        _functionDepth++;
        var body = ParseValueBodyOrBlock();   // a getter always returns, so its inline form is an expression
        _functionDepth--;

        _inObjectDef    = savedInObjectDef;
        _inFreeFunction = savedInFreeFunction;
        return new GetterDeclaration(name, returnType, body, untoType, line, col);
    }

    // Top-level entry-point: routes to the shared parser which allows the 'unto' clause.
    private GetterDeclaration ParseGetterUntoDeclaration() => ParseGetterDeclaration();

    // Set <name> [unto <type>] given (<param>): ... Done.
    private SetterDeclaration ParseSetterDeclaration()
    {
        var savedInObjectDef    = _inObjectDef;
        var savedInFreeFunction = _inFreeFunction;
        _inObjectDef    = false;
        _inFreeFunction = false;

        var lineTok = Consume(TokenType.SetKw);
        var line = lineTok.Line;
        var col = lineTok.Column;
        SkipNoise();
        var name = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();

        string? untoType = null;
        if (Peek().Type == TokenType.Unto)
        {
            Advance(); SkipNoise(); // consume 'unto'
            untoType = Consume(TokenType.Identifier).Lexeme;
            SkipNoise();
        }

        Consume(TokenType.Given);
        SkipNoise();
        Consume(TokenType.LParen);
        SkipNoise();
        var (paramType, paramName) = ParseParameter();
        SkipNoise();
        Consume(TokenType.RParen);
        SkipNoise();
        _functionDepth++;
        var body = ParseVoidBodyOrBlock();   // a setter is void, so its inline form is a statement
        _functionDepth--;

        _inObjectDef    = savedInObjectDef;
        _inFreeFunction = savedInFreeFunction;
        return new SetterDeclaration(name, paramType, paramName, body, untoType, line, col);
    }

    // Top-level entry-point: routes to the shared parser which allows the 'unto' clause.
    private SetterDeclaration ParseSetterUntoDeclaration() => ParseSetterDeclaration();

    // Bind unmaking a <type> to <name>: ... Done.
    // Top-level only. No parameters. Infallible (enforced in TypeChecker).
    private UnmakerDeclaration ParseUnmakerDeclaration()
    {
        if (_nestDepth > 0)
            throw new ParseException(Peek(),
                "— destructors must be declared at the top level, not inside a block");

        var savedInObjectDef   = _inObjectDef;
        var savedInFreeFunction = _inFreeFunction;
        _inObjectDef    = false;
        _inFreeFunction = false;

        var lineTok = Consume(TokenType.Bind);
        var line = lineTok.Line;
        var col = lineTok.Column;
        Consume(TokenType.UnmakingKw);
        SkipNoise(); // eats 'a' article
        var typeName = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();
        Consume(TokenType.To);
        SkipNoise();
        var name = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();

        if (Peek().Type == TokenType.Given)
            throw new ParseException(Peek(),
                "— destructors take no parameters (omit 'given (...)' entirely)");

        _functionDepth++;
        var body = ParseVoidBodyOrBlock();   // a destructor is void, so its inline form is a statement
        _functionDepth--;

        _inObjectDef    = savedInObjectDef;
        _inFreeFunction = savedInFreeFunction;

        return new UnmakerDeclaration(name, typeName, body, line, col);
    }

    // Bind overloading <op>, given (the <left> is a <type>, the <right> is a <type>): ... Done.
    // Top-level only. No name — invoked by the operator. Same-type binary; arithmetic ops only.
    // Fallibility inferred from body (TypeChecker pass); return type inferred from body.
    private OperatorOverloadDeclaration ParseOverloadDeclaration()
    {
        if (_nestDepth > 0)
            throw new ParseException(Peek(),
                "— operator overloads must be declared at the top level, not inside a block");

        var savedInObjectDef    = _inObjectDef;
        var savedInFreeFunction = _inFreeFunction;
        _inObjectDef    = false;
        _inFreeFunction = false;

        var lineTok = Consume(TokenType.Bind);
        var line = lineTok.Line;
        var col = lineTok.Column;
        Consume(TokenType.OverloadingKw);
        SkipNoise();

        // The operator token: +, -, *, /
        var opTok = Peek();
        if (opTok.Type is not (TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash))
            throw new ParseException(opTok,
                "expected an arithmetic operator (+, -, *, /) after 'overloading'");
        Advance();
        var op = opTok.Type;

        SkipNoise();
        if (Peek().Type == TokenType.Comma) Advance(); // optional comma before 'given'
        SkipNoise();
        Consume(TokenType.Given);
        SkipNoise();
        Consume(TokenType.LParen);
        SkipNoise();

        // Left operand: the <leftname> is a <typename>
        // SkipNoise eats the leading 'the' article
        var leftName = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();
        Consume(TokenType.Is);
        SkipNoise(); // eats 'a' article
        var typeName = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();

        Consume(TokenType.Comma);
        SkipNoise();

        // Right operand: the <rightname> is a <typename>
        var rightName = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();
        Consume(TokenType.Is);
        SkipNoise(); // eats 'a' article
        var rightTypeName = Consume(TokenType.Identifier).Lexeme;
        SkipNoise();

        Consume(TokenType.RParen);
        SkipNoise();

        if (typeName != rightTypeName)
            throw new ParseException(Peek(),
                $"— both operands of an operator overload must be the same type (left is '{typeName}', right is '{rightTypeName}')");

        _functionDepth++;
        var body = ParseValueBodyOrBlock();   // an overload always returns, so its inline form is an expression
        _functionDepth--;

        _inObjectDef    = savedInObjectDef;
        _inFreeFunction = savedInFreeFunction;

        return new OperatorOverloadDeclaration(op, leftName, rightName, typeName, body, line, col);
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
        var lineTok = Consume(TokenType.Return);
        var line = lineTok.Line;
        var col = lineTok.Column;
        if (_functionDepth == 0)
            throw new ParseException(_tokens[_pos - 1], "'return' used outside a function");
        SkipNoise();
        if (Peek().Type == TokenType.Dot)
        {
            Consume(TokenType.Dot);
            return new ReturnStatement(null, line, col); // bare return — void early exit
        }
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new ReturnStatement(value, line, col);
    }

    // Bury <value>.  — always takes a value, unlike `Return`, which has a bare form. A bare bury
    // would mean "suspend and hand out nothing", and a stash's whole contract is that a resumption
    // yields a value or reports it is spent; there is no third answer for a caller to narrow.
    private BuryStatement ParseBuryStatement()
    {
        var lineTok = Consume(TokenType.Bury);
        if (_functionDepth == 0)
            throw new ParseException(lineTok.Line, lineTok.Column,
                "'bury' is only meaningful inside a function — it is what makes that function hand "
                + "back a stash. At the top level there is nothing to suspend.");
        SkipNoise();
        var value = ParseExpression();
        SkipNoise();
        Consume(TokenType.Dot);
        return new BuryStatement(value, lineTok.Line, lineTok.Column);
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

    // One-token lookahead past the noise words, WITHOUT consuming anything — the discriminator for
    // a comma that could open either a parameter list or an inline body. Scans the raw token list
    // rather than calling SkipNoise, because SkipNoise advances and this must not.
    private bool PeekPastNoiseIs(TokenType type)
    {
        int i = _pos;
        if (i < _tokens.Count && _tokens[i].Type == TokenType.Comma) i++;
        while (i < _tokens.Count && _tokens[i].IsNoise) i++;
        return i < _tokens.Count && _tokens[i].Type == type;
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
    // ── Contextual words ─────────────────────────────────────────────────
    //
    // A contextual word is an ordinary Identifier that one construct recognises by LEXEME in one
    // specific position. Everywhere else it is just a name.
    //
    // This is how the standard library pays for its own vocabulary. A reserved word is taken from
    // every program in the language, whether or not it pulls the book that wanted it — so
    // reserving 'rows' for the collections book means no program anywhere can have a variable
    // called `rows`. The cost compounds with each book added, and it has already bitten once.
    // Recognising by shape instead keeps the word available.
    //
    // Deliberately NOT scope-aware: the word is recognised in its shape whether or not the book
    // was pulled, and using the feature without it is a type error — which it already was. That
    // is strictly less machinery for the same diagnosis.
    //
    // Testing is `IsWord`, which already existed for the I/O form words (line, lines, all).
    // This is its mandatory counterpart, for a position where the word is required rather than
    // optional.
    private Token ConsumeWord(string word)
    {
        if (!IsWord(word)) throw new ParseException(Peek(), $"'{word}'");
        return Advance();
    }

    // The base a 'converted to <base>' target names, or null if the word is not one. Contextual
    // by lexeme, so 'hex', 'binary' and 'octal' stay usable as ordinary names.
    private static char? BitsBaseFor(string lexeme) => lexeme.ToLowerInvariant() switch
    {
        "hex"    => 'x',
        "binary" => 'b',
        "octal"  => 'o',
        _        => null,
    };

    // Decodes a Bits token into value, display base and width. The lexer has already validated
    // the digits and stripped the '_' separators, and normalised the prefix to lowercase — so the
    // lexeme is "0" + base + digits, and the digit COUNT is what sets the width. That is why
    // 0x0F and 0xF are different values here despite being equal numerically: the first is 8
    // bits, the second is 4, and `not` reads that width.
    private static BitsLiteral ParseBitsLiteral(Token token)
    {
        char displayBase = token.Lexeme[1];
        string digits    = token.Lexeme[2..];
        int fromBase     = displayBase switch { 'x' => 16, 'o' => 8, 'b' => 2, _ => 10 };
        int bitsPerDigit = displayBase switch { 'x' => 4,  'o' => 3, 'b' => 1, _ => 0 };

        return new BitsLiteral(
            Convert.ToUInt64(digits, fromBase),
            displayBase,
            digits.Length * bitsPerDigit,
            token.Line, token.Column);
    }

    private IExpression ParseInterpolatedString()
    {
        var openTok = _tokens[_pos - 1]; // the InterpolOpen
        int line = openTok.Line;
        int col  = openTok.Column;
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
                var holeLineTok = Advance(); // consume InterpolHoleOpen
                int holeLine = holeLineTok.Line;
                int holeCol = holeLineTok.Column;
                if (Peek().Type == TokenType.InterpolHoleClose)
                    throw new ParseException(holeLine, holeCol, "empty interpolation '{}' — write an expression between the braces");
                var expr = ParseExpression();
                Consume(TokenType.InterpolHoleClose);
                piece = new TextConvert(expr, holeLine, holeCol);
            }
            else
            {
                throw new ParseException(Peek(), "string piece or interpolation expression");
            }

            result = result == null ? piece : new TextJoin(result, piece, line, col);
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

    // Whether the first non-noise token after the current position is spelled `word`. The type-only
    // PeekAfterCurrent cannot answer this: 'at' is book vocabulary, not a token type.
    private bool PeekAfterCurrentIsWord(string word)
    {
        int i = _pos + 1;
        while (i < _tokens.Count && _tokens[i].IsNoise) i++;
        return i < _tokens.Count &&
               _tokens[i].Lexeme.Equals(word, StringComparison.OrdinalIgnoreCase);
    }

    // True when the first non-noise token after the current one has this lexeme.
    private bool NextWordIs(string word)
    {
        int i = _pos + 1;
        while (i < _tokens.Count && _tokens[i].IsNoise) i++;
        return i < _tokens.Count && _tokens[i].Lexeme.Equals(word, StringComparison.OrdinalIgnoreCase);
    }

    // What the primary switch should treat this token AS.
    //
    // Book words lex as ordinary Identifiers so they stay usable as names, but the switch
    // dispatches on token TYPE — so it has to be told which shape it is looking at. The
    // lookahead is what keeps `Define matrix as 5.` a variable reference while
    // `a matrix with (...)` is a matrix literal.
    //
    // Only words whose shape has a MANDATORY distinguishing token can be freed this way.
    // 'catalogue' and 'atlas' cannot: their tails are optional, so `a catalogue` on its own is
    // valid and is indistinguishable from a variable of that name. They stay reserved, and that
    // is the line — not an oversight.
    private TokenType EffectiveType(Token tok)
    {
        if (tok.Type != TokenType.Identifier) return tok.Type;
        return tok.Lexeme.ToLowerInvariant() switch
        {
            "current"  when PeekAfterCurrent() == TokenType.DirectoryKw => TokenType.CurrentKw,
            "matrix"   when PeekAfterCurrent() == TokenType.With     => TokenType.Matrix,
            "randomly" when NextWordIs("shuffled")                   => TokenType.Randomly,
            "random"   when PeekAfterCurrent() is TokenType.NumberKw or TokenType.Item
                            || NextWordIs("guess")                   => TokenType.Random,
            _ => tok.Type,
        };
    }
}
