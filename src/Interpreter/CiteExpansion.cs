using Cufet.Lexer;

namespace Cufet.Interpreter;

/// <summary>
/// Places what a `cufet` block holds, where a `Cite` says — and takes the blocks themselves out.
/// </summary>
/// <remarks>
/// <para>
/// ⭐⭐ A front-end pass, ahead of everything. After it a `cufet` block is not a thing that
/// exists: what it held are ordinary statements at the sites that cited them, and the block is
/// gone. That is why neither backend has a word to say about cufet axioms — the same arrangement
/// that lets no template and no `stash of T` survive the checker, and for the same reason.
/// </para>
/// <para>
/// ★★ Splicing INLINE is what settles where a cited declaration lands, without a rule of its
/// own. A TYPE declaration belongs to the program wherever it is written, so a cited object is
/// program-scope however deeply the `Cite` sits; a VALUE binding does not, so it would be local to
/// the site that cited it. Both fall straight out of what the language already says about where a
/// declaration belongs — nothing here has to say it a second time.
/// </para>
/// <para>
/// ⚠ The blocks are gathered with AstSearch, which does NOT look inside one — see the note on
/// that exception. A block declared and never cited places nothing, which is the whole point of
/// there being a `Cite` at all.
/// </para>
/// </remarks>
public static class CiteExpansion
{
    /// <summary>
    /// Hands back the very same list when the program has no cufet blocks and no citations, which
    /// is the usual case.
    /// </summary>
    public static IReadOnlyList<IStatement> Expand(IReadOnlyList<IStatement> statements) =>
        Expand(statements, out _);

    /// <param name="unfilled">
    /// The program with every block gone and each block nothing cites standing in its place, holes
    /// and all — or null when no such block exists. See <see cref="WithStandIns"/>.
    /// </param>
    public static IReadOnlyList<IStatement> Expand(
        IReadOnlyList<IStatement> statements, out IReadOnlyList<IStatement>? unfilled)
    {
        unfilled = null;
        var blocks = new Dictionary<string, CufetAxiomDefinition>(StringComparer.Ordinal);
        // The cufet axioms that say what they give back — lowered to functions by the parser, and
        // gathered here only so that citing one can say why it cannot be cited.
        var runnable = new HashSet<string>(StringComparer.Ordinal);
        bool cited = false;
        foreach (var statement in AstSearch.EveryStatement(statements))
            switch (statement)
            {
                // ⚠ CUFET only, deliberately. This gathers what a `Cite` can place, and a pattern
                // is not source to place anywhere.
                case BindStatement { FromAxiomLanguage: { } lang } run
                        when TypeChecker.IsCufetLanguage(lang):
                    runnable.Add(run.Name);
                    break;

                case CufetAxiomDefinition block:
                    // ⚠ Refused rather than shadowed. Every other redeclaration in this language
                    // has an answer already — `Define a shadow`, or last-wins for a type — and both
                    // are about a NAME that holds something. This name holds source waiting to be
                    // placed, and two blocks under it would leave every `Cite` of it ambiguous at a
                    // glance, which is the one thing a placement keyword cannot afford to be.
                    if (blocks.TryGetValue(block.Name, out var first))
                        throw TypeChecker.TypeError(
                            $"there is already cufet source called '{block.Name}', on line {first.Line}",
                            "A name holds one block, because a 'Cite' of it has to say which one "
                          + "without looking anywhere else",
                            block.Line, block.Column,
                            $"declare a second block called '{block.Name}'",
                            "Give this one another name, or fold the two together.");
                    blocks[block.Name] = block;
                    break;

                case CiteStatement:
                    cited = true;
                    break;
            }

        if (blocks.Count == 0 && !cited) return statements;

        var programScope = ProgramScopeNames(statements);
        foreach (var block in blocks.Values)
        {
            RequireDeclarationsOnly(block);
            RequireNoCapture(block, programScope);
        }

        // ⭐⭐ Where a `Cite` may go, when what it places is a FUNCTION. Q1 says a block's names
        // resolve at program scope and never at the cite site — and for a function body that is not
        // a thing to check, it is a thing to make true. A `Bind` placed where functions live is a
        // FREE FUNCTION, and the language already refuses a free function that reads top-level data;
        // placed inside another body it is a closure, and closing over the citing body's locals is
        // exactly what Q1 forbids.
        //
        // ★ So the rule is a placement rule, enforced by the scope the statement lands in rather
        // than by a second analysis of what it says. FlattenHoistable answers "which scopes do
        // functions belong to" for the checker and both backends; asking it here is what keeps this
        // from becoming a fourth opinion on the same question.
        var placesAFunction = blocks.Values
            .Where(block => block.Body.Any(held => held is BindStatement))
            .Select(block => block.Name)
            .ToHashSet(StringComparer.Ordinal);

        var functionScope = placesAFunction.Count == 0
            ? []
            : new HashSet<IStatement>(TypeChecker.FlattenHoistable(statements), ByReference.Instance);

        var named = new List<(CiteStatement Cite, SourceHole Hole, VariableReference Name)>();
        var placed = AstRebuilder.Apply(statements, type => type, splice: statement =>
            statement is CiteStatement cite
                ? Held(blocks, runnable, placesAFunction, functionScope, cite, named)
                : null);
        if (named.Count > 0) RequireNamesFit(placed, named);

        var citedNames = AstSearch.EveryStatement(statements).OfType<CiteStatement>()
            .Select(cite => cite.Name).ToHashSet(StringComparer.Ordinal);
        var waiting = new HashSet<IStatement>(
            blocks.Values.Where(block => block.Holes.Count > 0 && !citedNames.Contains(block.Name)),
            ByReference.Instance);
        if (waiting.Count > 0) unfilled = WithStandIns(placed, waiting);
        return placed;
    }

    /// <summary>The program with each waiting block standing in its own place, to be checked.</summary>
    /// <remarks>
    /// ⭐⭐ A block with holes is checked before it is filled, and every fill is checked as the
    /// ordinary code it became — each statement keeps the block's own lines, and a function hole's
    /// name was held to what the hole says — so a block that is cited needs nothing more. This is
    /// the block nobody has cited yet: as in a file open in an editor. It is checked
    /// as written, with a stand-in for what its `given` promises: the holes keep their marked names,
    /// which nothing can collide with and which the checker's messages put back as written.
    ///
    /// ★ A copy of the program, thrown away. Nothing here reaches either backend.
    /// </remarks>
    private static IReadOnlyList<IStatement> WithStandIns(
        IReadOnlyList<IStatement> placed, HashSet<IStatement> waiting) =>
        AstRebuilder.Apply(placed, type => type, splice: statement =>
            statement is not CufetAxiomDefinition block ? null
            : waiting.Contains(block) ? StandIn(block)
            : []);

    private static IReadOnlyList<IStatement> StandIn(CufetAxiomDefinition block)
    {
        var values = new Dictionary<string, IExpression>(StringComparer.Ordinal);
        var stated = new List<IStatement>();
        foreach (var hole in block.Holes)
            switch (hole.Type)
            {
                case FunctionType function: stated.Add(StatedFunction(block, hole, function)); break;
                case TextType:   values[Held(block, hole)] = new StringLiteral(""); break;
                case NumberType: values[Held(block, hole)] = new NumberLiteral(0); break;
                case FactType:   values[Held(block, hole)] = new BooleanLiteral(false, block.Line, block.Column); break;
            }

        var holes = block.Holes.ToDictionary(hole => hole.Name, hole => Held(block, hole), StringComparer.Ordinal);
        var marked = Renamed(block.Body, holes,
            new Dictionary<string, IExpression>(StringComparer.Ordinal), fresh: false);
        return [.. stated, .. Renamed(marked, [], values, fresh: true)];
    }

    /// <summary>A function saying exactly what a hole says, for the body to be checked against.</summary>
    private static BindStatement StatedFunction(
        CufetAxiomDefinition block, SourceHole hole, FunctionType function)
    {
        int line = block.Line, column = block.Column;
        var name = Held(block, hole);
        var parameters = function.ParameterTypes.Select((type, i) => (type, $"given-{i + 1}")).ToList();
        var passed = parameters
            .Select(parameter => (IExpression)new VariableReference(parameter.Item2, line, column))
            .ToList();
        IExpression callee = hole.Unto is null
            ? new VariableReference(name, line, column)
            : new PossessiveAccess(new VariableReference("one", line, column), name, line, column);
        IExpression? given = function.ReturnType switch
        {
            null        => null,
            FailureType => new FailureLiteral(new StringLiteral(""), null, line, column),
            _           => new CastExpression(callee, passed, line, column),
        };
        IReadOnlyList<IStatement> body = given is null ? [] : [new ReturnStatement(given, line, column)];
        return new BindStatement(name, function.ReturnType, parameters, body,
            hole.Unto, ConstructsTypeName: null, line, column);
    }

    /// <summary>Takes the blocks themselves out, once everything that reads one has run.</summary>
    /// <remarks>
    /// ★ Separate from <see cref="Expand"/>, and after the checker rather than before it, because
    /// a block still has ONE thing to answer for once its contents have been placed: that its
    /// language is pulled around it. That is a question about scope, and only the checker can ask
    /// it — so the block stays until it has.
    ///
    /// ⚠ An empty splice is how a statement is removed here. It is the same rule that lets no
    /// template survive: what a backend cannot meet, it cannot get wrong.
    /// </remarks>
    /// <remarks>
    /// ⚠⚠ The GUARD is not an optimisation. Without it this walked the whole tree of every program
    /// ever compiled, for nothing — and AstRebuilder's reflective rebuild asks each node's
    /// constructor for its parameter NAMES, which the trimmed wasm build does not have. So the
    /// playground answered every program, including a nine-line average, with "ObjectDefinition.
    /// has no matching property — AstRebuilder can only rebuild positional records": an empty name
    /// where a name should be, about a construct the program never used.
    ///
    /// ★ Expand guards itself the same way and always did. This is the same rule, and the reason it
    /// is a rule: a pass that does nothing should touch nothing, because "does nothing" and "walks
    /// every node doing nothing" are not the same on every platform.
    /// </remarks>
    public static IReadOnlyList<IStatement> WithoutBlocks(IReadOnlyList<IStatement> statements) =>
        AstSearch.Contains(statements, node => node is CufetAxiomDefinition)
            ? AstRebuilder.Apply(statements, type => type,
                  splice: statement => statement is CufetAxiomDefinition ? [] : null)
            : statements;

    private static IReadOnlyList<IStatement> Held(
        Dictionary<string, CufetAxiomDefinition> blocks,
        HashSet<string> runnable,
        HashSet<string> placesAFunction,
        HashSet<IStatement> functionScope,
        CiteStatement cite,
        List<(CiteStatement Cite, SourceHole Hole, VariableReference Name)> named)
    {
        if (blocks.TryGetValue(cite.Name, out var block))
        {
            // ⚠ A function may only be placed where a function belongs. Everything else a block
            // holds goes anywhere: a type belongs to the program wherever it is written, and a
            // value is meant to land at the cite site — that is the feature. A function is the one
            // thing whose meaning would change with the company it keeps.
            if (placesAFunction.Contains(cite.Name) && !functionScope.Contains(cite))
                throw TypeChecker.TypeError(
                    $"'{cite.Name}' holds a function, so it cannot be cited inside another body",
                    "Placed here it would be a closure over the body citing it, and what a block "
                  + "holds must mean the same thing wherever it is placed. Placed where functions "
                  + "belong it is a free function, which already cannot read the data around it",
                    cite.Line, cite.Column,
                    $"cite '{cite.Name}' inside a body",
                    $"Cite '{cite.Name}' at the top level, or directly inside a 'Pull' block.");

            var filled = Filled(block, cite, named);
            return cite.Carried
                ? filled.Where(held => held is not DefineStatement { Permanent: false }).ToList()
                : filled;
        }

        // ⚠ The name IS declared, just not as source — saying "there is no cufet source called
        // 'two'" and telling the writer to declare it would send them to fix a line that is
        // already right. Which of the two a cufet axiom is comes from one place: whether it says
        // what it gives back.
        if (runnable.Contains(cite.Name))
            throw TypeChecker.TypeError(
                $"'{cite.Name}' says what it gives back, so it is something you run rather than "
              + "source to cite",
                "A cufet axiom that names a result is a body with a name — the same rule the "
              + "c-language tag follows",
                cite.Line, cite.Column,
                $"cite '{cite.Name}'",
                $"Call it: 'cast {cite.Name} on (...)'. Only a block that says nothing about a "
              + "result is cited.");

        throw TypeChecker.TypeError(
            $"there is no cufet source called '{cite.Name}' to cite",
            "'Cite' places what a cufet block holds, and the name is the block's, not a "
          + "variable's",
            cite.Line, cite.Column,
            $"cite '{cite.Name}'",
            $"Declare it first: 'Define cufet {cite.Name} as [ ... ].'");
    }

    /// <summary>What one `Cite` places: the block's own statements, with its holes filled.</summary>
    /// <remarks>
    /// ⭐⭐ One pass, for the reason C source has one: what fills a hole is never searched. Each hole
    /// is first renamed to a name holding a SPACE, which no written name can be, so a fill that
    /// happens to spell another hole cannot be filled a second time.
    ///
    /// ★ A block with no holes is placed as it stands, and always was.
    /// </remarks>
    private static IReadOnlyList<IStatement> Filled(
        CufetAxiomDefinition block, CiteStatement cite,
        List<(CiteStatement Cite, SourceHole Hole, VariableReference Name)> named)
    {
        if (block.Holes.Count == 0)
        {
            if (cite.Arguments.Count == 0) return block.Body;
            throw TypeChecker.TypeError(
                $"'{block.Name}' has no holes, so there is nothing to fill",
                "'on' hands a block what its 'given' asks for, and this block asks for nothing",
                cite.Line, cite.Column,
                $"fill '{block.Name}'",
                $"Write 'Cite {block.Name}.'");
        }
        if (cite.Arguments.Count != block.Holes.Count)
            throw TypeChecker.TypeError(
                $"'{block.Name}' has {block.Holes.Count} holes, and this fills {cite.Arguments.Count}",
                "Each hole is filled by one thing, in the order the 'given' names them",
                cite.Line, cite.Column,
                $"cite '{block.Name}'",
                $"Write 'Cite {block.Name} on (…)' with "
              + string.Join(", ", block.Holes.Select(hole => hole.Name)) + ".");

        var names  = new Dictionary<string, string>(StringComparer.Ordinal);
        var values = new Dictionary<string, IExpression>(StringComparer.Ordinal);
        for (int i = 0; i < block.Holes.Count; i++)
        {
            var hole = block.Holes[i];
            var fill = cite.Arguments[i];
            var held = Held(block, hole);
            if (IsTerm(hole))
                names[held] = TermOf(block, hole, fill, cite);
            else if (hole.Type is FunctionType)
            {
                if (fill is not VariableReference function)
                    throw NotAFill(block, hole, cite, "a name, written bare", hole.Name);
                names[held] = function.Name;
                named.Add((cite, hole, function));
            }
            else
                values[held] = ValueOf(block, hole, fill, cite);
        }

        var holes = block.Holes.ToDictionary(hole => hole.Name, hole => Held(block, hole), StringComparer.Ordinal);
        var marked = Renamed(block.Body, holes,
            new Dictionary<string, IExpression>(StringComparer.Ordinal), fresh: false);
        return Renamed(marked, names, values, fresh: true);
    }

    /// <summary>The name a hole goes by between being cut and being filled.</summary>
    private static string Held(CufetAxiomDefinition block, SourceHole hole) =>
        $"{hole.Name} in {block.Name}";

    private static bool IsTerm(SourceHole hole) =>
        hole.Type is ObjectType { Name: "term" };

    /// <summary>Statements with their names swapped, and with their holes for values filled.</summary>
    /// <param name="fresh">
    /// Whether every call and read comes out a new node, as a generic's fillings do
    /// (GenericInstantiation.FilledCopy). ⚠ A precaution, not a proof: MEASURED 2026-11-05, turning
    /// it off fails no test. A generic needs it because its fillings differ in TYPE; a hole states
    /// its type, so every copy of a block types alike and the checker writes the same notes on each.
    /// </param>
    private static IReadOnlyList<IStatement> Renamed(
        IReadOnlyList<IStatement> body,
        Dictionary<string, string> names,
        Dictionary<string, IExpression> values,
        bool fresh)
    {
        var done = new HashSet<object>(ReferenceEqualityComparer.Instance);
        T Once<T>(T made) where T : class { done.Add(made); return made; }
        string Swap(string name) => names.GetValueOrDefault(name, name);

        IExpression? Expression(IExpression expression)
        {
            if (done.Contains(expression)) return null;
            return expression switch
            {
                VariableReference read when values.TryGetValue(read.Name, out var value) => Once(value),
                VariableReference read when fresh || names.ContainsKey(read.Name) =>
                    Once(read with { Name = Swap(read.Name) }),
                PossessiveAccess access when names.ContainsKey(access.Member) =>
                    Once(access with { Member = Swap(access.Member) }),
                CastExpression call when fresh => Once(call with { }),
                _ => null,
            };
        }

        IStatement? Statement(IStatement statement)
        {
            if (done.Contains(statement)) return null;
            return statement switch
            {
                BindStatement bind when names.ContainsKey(bind.Name) => Once(bind with { Name = Swap(bind.Name) }),
                DefineStatement define when names.ContainsKey(define.Name) => Once(define with { Name = Swap(define.Name) }),
                BecomesStatement becomes when names.ContainsKey(becomes.Name) => Once(becomes with { Name = Swap(becomes.Name) }),
                PossessiveSetStatement set when names.ContainsKey(set.Member) => Once(set with { Member = Swap(set.Member) }),
                CastStatement call when fresh => Once(call with { }),
                _ => null,
            };
        }

        return AstRebuilder.Apply(body, type => type, replace: Statement, rewrite: Expression);
    }

    /// <summary>The name a term hole is filled with, which has to be one.</summary>
    private static string TermOf(
        CufetAxiomDefinition block, SourceHole hole, IExpression fill, CiteStatement cite)
    {
        if (fill is not StringLiteral { Value: var text })
            throw NotAFill(block, hole, cite, "a name in quotes", hole.Name);

        // ⚠ A text that does not lex is simply not a name, so it gets the same answer.
        IReadOnlyList<Token> tokens;
        try { tokens = new Cufet.Lexer.Lexer(text).Tokenize(); }
        catch (LexerException) { tokens = []; }
        if (tokens.Count == 2 && tokens[0].Type == TokenType.Identifier
                              && tokens[0].Lexeme == text) return text;

        throw TypeChecker.TypeError(
            $"'{text}' cannot name anything, so it cannot fill '{hole.Name}'",
            "A term is a name the block makes, so it has to be a name Cufet can read — and not a "
          + "word Cufet has taken",
            cite.Line, cite.Column,
            $"fill '{hole.Name}' with '{text}'",
            "Choose a name made of letters, digits and hyphens.");
    }

    /// <summary>The literal a hole for a value is filled with.</summary>
    /// <remarks>
    /// ⚠ A literal and nothing else. A `Cite` happens before the program runs, so nothing has a
    /// value yet — a name here could only mean its TEXT, and that is the code hole this does not
    /// have.
    /// </remarks>
    private static IExpression ValueOf(
        CufetAxiomDefinition block, SourceHole hole, IExpression fill, CiteStatement cite)
    {
        var fits = hole.Type switch
        {
            TextType   => fill is StringLiteral,
            FactType   => fill is BooleanLiteral,
            NumberType => fill is NumberLiteral or UnaryExpression { Op: TokenType.Minus, Operand: NumberLiteral },
            _          => false,
        };
        if (fits) return fill;
        throw NotAFill(block, hole, cite, $"a {TypeChecker.FormatType(hole.Type)} written out", hole.Name);
    }

    private static Exception NotAFill(
        CufetAxiomDefinition block, SourceHole hole, CiteStatement cite, string wanted, string name) =>
        TypeChecker.TypeError(
            $"'{name}' in '{block.Name}' is filled with {wanted}",
            "A 'Cite' happens before the program runs, so what fills a hole has to be written out "
          + "right there",
            cite.Line, cite.Column,
            $"fill '{name}' this way",
            $"Fill '{name}' with {wanted}.");

    /// <summary>Refuses a name filled into a function hole that is not what the hole says.</summary>
    /// <remarks>
    /// ⭐ Asked after every `Cite` is placed, because a name may be one another `Cite` makes. This is
    /// what puts the blame on the `Cite`: the hole states the type, so a body that is right against
    /// it stays right for any name that fits — and the name that does not fit is the line to fix.
    /// </remarks>
    private static void RequireNamesFit(
        IReadOnlyList<IStatement> placed,
        List<(CiteStatement Cite, SourceHole Hole, VariableReference Name)> named)
    {
        var functions = new List<(BindStatement Bind, string? Owner)>();
        foreach (var statement in AstSearch.EveryStatement(placed))
            switch (statement)
            {
                case BindStatement bind:
                    functions.Add((bind, bind.UntoType));
                    break;
                case ObjectDefinition type:
                    foreach (var method in type.Methods) functions.Add((method, type.Name));
                    break;
            }

        foreach (var (cite, hole, name) in named)
        {
            var wanted = (FunctionType)hole.Type;
            var found = functions.Where(function => function.Bind.Name == name.Name
                                                 && function.Owner == hole.Unto).ToList();
            var whose = hole.Unto is null ? "function" : $"method of '{hole.Unto}'";
            if (found.Count == 0)
                throw TypeChecker.TypeError(
                    $"there is no {whose} called '{name.Name}' to fill '{hole.Name}'",
                    "A hole for a function is filled with the name of one, and that name has to "
                  + "exist",
                    name.Line, name.Column,
                    $"fill '{hole.Name}' with '{name.Name}'",
                    $"Declare it, or fill '{hole.Name}' with a name that is one.");

            if (found.Any(function => Fits(function.Bind, wanted))) continue;
            throw TypeChecker.TypeError(
                $"'{name.Name}' does not fit '{hole.Name}', which asks for "
              + TypeChecker.FormatType(wanted),
                "What a block calls through a hole is checked against what the hole says, so the "
              + "name has to be exactly that",
                name.Line, name.Column,
                $"fill '{hole.Name}' with '{name.Name}'",
                $"Fill '{hole.Name}' with a {whose} of that shape.");
        }
    }

    private static bool Fits(BindStatement bind, FunctionType wanted) =>
        Equals(bind.ReturnType, wanted.ReturnType)
        && bind.Parameters.Select(parameter => parameter.Type).SequenceEqual(wanted.ParameterTypes);

    /// <summary>Refuses a block holding anything but a declaration.</summary>
    /// <remarks>
    /// ★ An object, an interface and a `Define` — and the difference between them is the whole
    /// point of `Cite`. A TYPE belongs to the program wherever it is written, so a cited object is
    /// program-scope however deeply the `Cite` sits. A VALUE does not, so a cited `Define` lands as
    /// a local at the site that cited it. Neither is a rule this pass invents; both fall out of
    /// what the language already says about where a declaration belongs.
    ///
    /// ⚠ A `Bind` is NOT held yet, and the reason is Q1 rather than effort. A function body reads
    /// names, and constraining WHICH names it may read once it has been placed somewhere else is
    /// the scope question <see cref="RequireNoCapture"/> answers for a `Define`'s value and cannot
    /// answer for a body. An object's methods are safe for the opposite reason: they are checked in
    /// a scope of their own, which never contains a local of the cite site.
    ///
    /// ⚠ Checked for EVERY block, cited or not. A block is source the writer meant, and finding
    /// out it could never be placed at the moment someone first cites it would be a message about
    /// the wrong line.
    /// </remarks>
    private static void RequireDeclarationsOnly(CufetAxiomDefinition block)
    {
        // ⚠ The same refusal the c-language tag makes, for the same reason: a parameter's value
        // comes from a call, and nothing calls source — it is placed. Said here rather than left to
        // the checker because a `Cite` of this name would otherwise report first, and "there is no
        // cufet source called 'shape'" is a true sentence about the wrong line.
        if (block.HasParameterClause && block.Holes.Count == 0)
            throw TypeChecker.TypeError(
                $"'{block.Name}' has a 'given' that names nothing",
                "A block's 'given' names the holes a 'Cite' fills",
                block.Line, block.Column,
                $"give '{block.Name}' an empty 'given'",
                "Drop the 'given' clause, or name what it leaves to be filled.");

        foreach (var hole in block.Holes) RequireFillable(block, hole);

        foreach (var statement in block.Body)
        {
            if (statement is ObjectDefinition or InterfaceDefinition
                          or DefineStatement or BindStatement) continue;
            var (line, column) = PositionOf(statement, block.Line, block.Column);

            throw TypeChecker.TypeError(
                $"cufet source holds declarations, and this is not one",
                "A cited block places what it declares where the 'Cite' is. An object, an "
              + "interface and a 'Define' are what it can declare so far",
                line, column,
                $"put this inside '{block.Name}'",
                "Move it out of the block, or declare an object or an interface here.");
        }
    }

    /// <summary>Refuses a hole nothing can fill, or one its block does not honour.</summary>
    /// <remarks>
    /// ★ A hole takes a name or a plain value, which is what keeps a fill from changing the shape
    /// of what it lands in.
    ///
    /// ⚠ A TERM is a name the block MAKES, so the block has to declare it: that declaration is what
    /// says what the name is, and a name it only calls has to say so itself, in the `given`.
    ///
    /// ⚠ A hole for a value is replaced where it is read, so a block that declares the name again
    /// would keep its declaration and lose its reads, without a word.
    /// </remarks>
    private static void RequireFillable(CufetAxiomDefinition block, SourceHole hole)
    {
        if (!AstSearch.Contains(block.Body, node => Uses(node, hole.Name)))
            throw TypeChecker.TypeError(
                $"'{block.Name}' never uses its hole '{hole.Name}'",
                "A hole is filled at every 'Cite', so one nothing reads is a fill that goes nowhere",
                block.Line, block.Column,
                $"leave '{hole.Name}' unused",
                $"Use '{hole.Name}' in the block, or take it out of the 'given'.");

        if (block.Holes.Count(other => other.Name == hole.Name) > 1)
            throw TypeChecker.TypeError(
                $"'{block.Name}' has two holes called '{hole.Name}'",
                "A hole is filled by its place in the 'given', and read by its name",
                block.Line, block.Column,
                $"name two holes '{hole.Name}'",
                "Give one of them another name.");

        if (IsTerm(hole))
        {
            if (block.Body.Any(held => held is BindStatement { Name: var name } && name == hole.Name
                                    || held is DefineStatement { Name: var made } && made == hole.Name))
                return;
            throw TypeChecker.TypeError(
                $"'{hole.Name}' is a term, so '{block.Name}' has to make it, and it does not",
                "A term is a name the block declares. A name it only calls has to say what it is",
                block.Line, block.Column,
                $"leave '{hole.Name}' as a term",
                $"Declare '{hole.Name}' in the block, or say what it is, as in "
              + $"'the number function {hole.Name}'.");
        }

        if (hole.Type is FunctionType) return;

        if (hole.Type is not (TextType or NumberType or FactType))
            throw TypeChecker.TypeError(
                $"'{hole.Name}' cannot be a hole: a hole takes a term, a function's name, or a "
              + "text, number or fact",
                "What fills a hole is written out at the 'Cite', before the program runs",
                block.Line, block.Column,
                $"make '{hole.Name}' a hole of this type",
                "Use one of those.");

        if (AstSearch.Contains(block.Body, node => Declares(node, hole.Name)))
            throw TypeChecker.TypeError(
                $"'{block.Name}' declares '{hole.Name}' again, and it is a hole",
                "A hole is filled where it is read, so a second declaration would be left with "
              + "nothing reading it",
                block.Line, block.Column,
                $"declare '{hole.Name}' inside '{block.Name}'",
                "Give one of the two another name.");
    }

    private static bool Uses(object node, string name) => node switch
    {
        VariableReference read   => read.Name == name,
        PossessiveAccess access  => access.Member == name,
        BindStatement bind       => bind.Name == name,
        DefineStatement define   => define.Name == name,
        _ => false,
    };

    private static bool Declares(object node, string name) => node switch
    {
        DefineStatement define  => define.Name == name,
        ForEachStatement loop   => loop.IteratorName == name,
        BindStatement bind      => bind.Parameters.Any(parameter => parameter.Name == name),
        LambdaLiteral lambda    => lambda.Parameters.Any(parameter => parameter.Name == name),
        _ => false,
    };

    /// <summary>The names a cited block is allowed to reach for — the program's, not the cite site's.</summary>
    /// <remarks>
    /// ★ Exactly the three kinds that belong to the PROGRAM wherever they are written: a free
    /// function, a type, and a `permanently` constant — plus the books a `Pull` brings in. Each is
    /// collected the way the checker collects it, so this list cannot say a name is program-scope
    /// when the checker would not.
    /// </remarks>
    private static HashSet<string> ProgramScopeNames(IReadOnlyList<IStatement> statements)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        // Free functions and shared constants — hoisting is transparent to a pull scope, which is
        // what FlattenHoistable answers and why it is the checker's own copy being asked.
        foreach (var statement in TypeChecker.FlattenHoistable(statements))
            switch (statement)
            {
                case BindStatement { UntoType: null } function: names.Add(function.Name); break;
                case DefineStatement { Permanent: true } constant: names.Add(constant.Name); break;
                case PullStatement pull:
                    foreach (var (book, local) in pull.Books) { names.Add(book); names.Add(local); }
                    break;
            }

        // Types belong to the program wherever they are written — anywhere at all, hence the wider
        // walk. (It does not enter a cufet block; that exception is the point of there being one.)
        foreach (var statement in AstSearch.EveryStatement(statements))
            switch (statement)
            {
                case ObjectDefinition type:      names.Add(type.Name); break;
                case InterfaceDefinition shape:  names.Add(shape.Name); break;
            }

        return names;
    }

    /// <summary>Refuses a block that reaches for a name it did not declare.</summary>
    /// <remarks>
    /// ⭐⭐ This is Q1, and it is a REFUSAL rather than a resolution rule because that is what
    /// makes capture impossible **by construction**. A `Define` from a block lands as a local at the
    /// site that cited it, and its value is evaluated there — so a name it reads that the block did
    /// not declare would silently mean whatever the cite site happens to have under that name. The
    /// same block, cited in two places, would then be two different programs.
    ///
    /// ★ What a block may reach for is what belongs to the PROGRAM: a free function, a type, a
    /// `permanently` constant, a pulled book. Those mean the same thing wherever the block is
    /// placed, which is the whole test.
    ///
    /// ⚠ A lambda's parameters are added as it is entered. A lambda captures its enclosing scope by
    /// design, so one written inside a block is exactly where a capture would hide.
    ///
    /// ⚠ Only a `Define`'s VALUE is walked. An object's methods are checked in a scope of their
    /// own, which never contains a local of the cite site — they cannot capture one, so there is
    /// nothing here to refuse.
    /// </remarks>
    private static void RequireNoCapture(CufetAxiomDefinition block, HashSet<string> programScope)
    {
        var declared = new HashSet<string>(programScope, StringComparer.Ordinal);
        foreach (var statement in block.Body)
            switch (statement)
            {
                case DefineStatement value:      declared.Add(value.Name); break;
                case ObjectDefinition type:      declared.Add(type.Name); break;
                case InterfaceDefinition shape:  declared.Add(shape.Name); break;
            }

        // What the block leaves to be filled comes from the `Cite`, by name, so it captures nothing.
        foreach (var hole in block.Holes) declared.Add(hole.Name);

        // The pronouns, which no writer declares and every body may use.
        foreach (var pronoun in new[] { "it", "one", "input", "failure", "the failure" })
            declared.Add(pronoun);

        foreach (var statement in block.Body)
            if (statement is DefineStatement { Value: var expression })
                Walk(expression, declared);

        void Walk(object? node, HashSet<string> visible)
        {
            switch (node)
            {
                case null or string or CufetType:
                    return;

                case VariableReference reference when !visible.Contains(reference.Name):
                    throw TypeChecker.TypeError(
                        $"cufet source cannot reach for '{reference.Name}' — it declares no such "
                      + "name, and one it does not declare would mean whatever the site that "
                      + "cites it happens to have",
                        "A block may use what belongs to the program — a function, a type, a "
                      + "'permanently' constant, a pulled book — because those mean the same thing "
                      + "wherever it is placed",
                        reference.Line, reference.Column,
                        $"read '{reference.Name}' inside '{block.Name}'",
                        $"Declare '{reference.Name}' inside the block, or make it a 'permanently' "
                      + "constant outside it.");

                case VariableReference:
                    return;

                // A lambda binds its own parameters, and is where a capture would hide.
                case LambdaLiteral lambda:
                {
                    var inner = new HashSet<string>(visible, StringComparer.Ordinal);
                    foreach (var (_, name) in lambda.Parameters) inner.Add(name);
                    foreach (var inside in lambda.Body) Walk(inside, inner);
                    return;
                }

                case System.Runtime.CompilerServices.ITuple tuple:
                    for (int i = 0; i < tuple.Length; i++) Walk(tuple[i], visible);
                    return;

                case System.Collections.IEnumerable sequence:
                    foreach (var item in sequence) Walk(item, visible);
                    return;

                default:
                    // Keyed on the NAMESPACE, the same correction AstSearch carries: several AST
                    // records implement neither IExpression nor IStatement and hold both.
                    if (node.GetType().Namespace != typeof(Program).Namespace) return;
                    foreach (var property in node.GetType().GetProperties())
                        Walk(property.GetValue(node), visible);
                    return;
            }
        }
    }

    /// <summary>Where a statement is, or where the block is when the statement does not say.</summary>
    /// <remarks>
    /// ⚠ Reflective, for the reason every whole-tree question here is: ten statement kinds carry
    /// no position at all, and a switch listing the ones that do is a list that goes stale in
    /// silence. The block's own position is the honest fallback — it is at least the right region
    /// of the right file, which is more than a zero would be.
    /// </remarks>
    private static (int Line, int Column) PositionOf(
        IStatement statement, int fallbackLine, int fallbackColumn)
    {
        var type = statement.GetType();
        return type.GetProperty("Line")?.GetValue(statement) is int line
            && type.GetProperty("Column")?.GetValue(statement) is int column
            ? (line, column)
            : (fallbackLine, fallbackColumn);
    }
}
