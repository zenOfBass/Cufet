using Cufet.Lexer;

namespace Cufet.Interpreter;

// Semantic tokens: the name-kind information a TextMate grammar cannot know.
//
// A TextMate grammar colours what is lexically visible — keywords, strings, numbers, comments.
// It cannot tell whether a bare word is a variable, a function, a type, a parameter or a field,
// because Cufet's English-like surface does not make that visible at the character level. This
// producer supplies exactly that missing layer, and NOTHING else: it emits a token only for a
// name occurrence whose kind is semantic. Keywords, literals, punctuation and comments are left
// alone — an editor keeps colouring those from the grammar it already has.

// The legend. ORDER IS THE WIRE FORMAT: an LSP semantic-tokens provider registers this array and
// then sends indices into it, so an editor integration must use the same order, not the same set.
// The names and their relative order are a subsequence of the LSP standard token types, so a
// client that already understands LSP token types themes these without extra configuration.
public enum SemanticTokenKind
{
    Namespace = 0,   // a book name or its local alias
    Type      = 1,   // an object or interface type name
    Parameter = 2,   // a function/method/lambda/setter parameter, at its declaration and its uses
    Variable  = 3,   // a bound name used as a value
    Property  = 4,   // an object/record field, getter or setter name
    Function  = 5,   // a Bind-defined function or method name, and the target of a Cast

    // ★ The one kind that is not a name. `output` and `seed` OPEN statements but are deliberately
    // unreserved, so a program may also use either as a variable — which makes the spelling alone
    // ambiguous and puts the answer out of a TextMate grammar's reach. It was tried there twice:
    // colouring the word always made a variable named `output` look like a keyword, and colouring
    // only the capitalised spelling made one statement two different colours depending on whether
    // its line had been capitalised yet. This pass has the parse, so it simply knows, and says so.
    Keyword   = 6,
}

// Only one modifier in this phase, and it is free: the producer already knows which occurrence of
// a name introduced it. Same rule as Kinds — the order is the wire format.
[Flags]
public enum SemanticTokenModifier
{
    None        = 0,
    Declaration = 1 << 0,
}

public static class SemanticTokenLegend
{
    public static readonly string[] Kinds =
        ["namespace", "type", "parameter", "variable", "property", "function", "keyword"];

    public static readonly string[] Modifiers = ["declaration"];

    public static string NameOf(SemanticTokenKind kind) => Kinds[(int)kind];

    public static IReadOnlyList<string> NamesOf(SemanticTokenModifier modifiers)
    {
        var names = new List<string>();
        for (int i = 0; i < Modifiers.Length; i++)
            if (((int)modifiers & (1 << i)) != 0)
                names.Add(Modifiers[i]);
        return names;
    }
}

// Line and Column are 1-based and match the positions the lexer and the AST already carry.
// Length is measured in characters of the name as it was written.
public sealed record SemanticToken(
    int                   Line,
    int                   Column,
    int                   Length,
    SemanticTokenKind     Kind,
    SemanticTokenModifier Modifiers = SemanticTokenModifier.None);

// Walks a CHECKED program and reports the kind of every name occurrence it can place precisely.
//
// Why a post-check walk rather than a side-output from the checker: the checker resolves a name
// in fourteen partial files, and threading an emit call through all of them would put highlighting
// in the way of type checking forever. The walk instead consults the two symbol tables the checker
// itself keeps — TypeChecker.ObjectDefinitions and TypeChecker.InterfaceDefinitions, both filled by
// Pass1Hoist and never cleared — for the one question syntax cannot answer, "is this word a type",
// and answers the rest structurally: a Bind declares a function, a parameter list declares
// parameters, a Cast's callee is a function, a possessive or `the <name> of` reaches a property.
//
// Positions: most name-carrying nodes point at their construct's keyword, not at the name (a
// DefineStatement is positioned on `Define`). Rather than re-lex, the producer walks the token
// list the pipeline already produced, from the node's position forward to the end of the
// construct's header, and takes the position of the matching word. A name it cannot place is not
// emitted — a missing token costs a word its colour, a misplaced one paints the wrong word.
public sealed class SemanticTokenizer
{
    // Names the parser synthesises from a keyword rather than from an identifier the user wrote.
    // `one`, `it` and `item` are keyword tokens the grammar already colours; `the failure` and
    // `the exception` are two-word bindings with no single identifier behind them.
    private static readonly HashSet<string> KeywordBound =
        new(StringComparer.Ordinal) { "one", "it", "item", "the failure", "the exception" };

    private readonly IReadOnlyList<Token> _tokens;
    private readonly TypeChecker          _checker;
    private readonly List<SemanticToken>  _out    = [];
    private readonly List<Dictionary<string, SemanticTokenKind>> _scopes =
        [new(StringComparer.Ordinal)];

    // Where every possessive marker starts. A name whose span ends exactly here owns that marker —
    // `'s` binds to the word immediately before it and nothing else can be in between.
    private readonly HashSet<(int Line, int Column)> _possessiveStarts;

    private SemanticTokenizer(IReadOnlyList<Token> tokens, TypeChecker checker)
    {
        _tokens  = tokens;
        _checker = checker;
        _possessiveStarts = tokens
            .Where(t => t.Type == TokenType.Possessive)
            .Select(t => (t.Line, t.Column))
            .ToHashSet();
    }

    // The one entry point. `checker` must be the instance that already checked `program` — its
    // symbol tables are what makes a type name distinguishable from a variable name.
    public static IReadOnlyList<SemanticToken> Collect(
        Program program, IReadOnlyList<Token> tokens, TypeChecker checker)
    {
        var tokenizer = new SemanticTokenizer(tokens, checker);
        tokenizer.WalkBlock(program.Statements);
        return tokenizer._out
            .OrderBy(t => t.Line)
            .ThenBy(t => t.Column)
            .ToList();
    }

    // ── Emitting ──────────────────────────────────────────────────────────

    // An owner's token is widened to swallow its `'s`. The TextMate grammar deliberately scopes
    // `math's` as one word — see the `possessive` rule's comment — so a semantic token that stopped
    // at `math` would repaint the name and leave the marker behind in the grammar's colour, which
    // is exactly the half-coloured word that rule exists to prevent.
    private void Emit(int line, int column, int length, SemanticTokenKind kind,
                    SemanticTokenModifier modifiers = SemanticTokenModifier.None)
    {
        if (line <= 0 || column <= 0 || length <= 0) return;
        if (_possessiveStarts.Contains((line, column + length))) length += 2;
        _out.Add(new SemanticToken(line, column, length, kind, modifiers));
    }

    // Emits at a name's own node position — used where the node already points at the name.
    private void EmitAt(string name, int line, int column, SemanticTokenKind kind,
                        SemanticTokenModifier modifiers = SemanticTokenModifier.None)
    {
        if (KeywordBound.Contains(name)) return;
        Emit(line, column, name.Length, kind, modifiers);
    }

    // Emits at the position the cursor finds for `name`, and advances the cursor past it.
    private void EmitFound(Cursor cursor, string? name, SemanticTokenKind kind,
                            SemanticTokenModifier modifiers = SemanticTokenModifier.None)
    {
        if (name is null || KeywordBound.Contains(name)) return;
        if (cursor.Next(name) is not { } found) return;
        Emit(found.Line, found.Column, found.Length, kind, modifiers);
    }

    // ── Scopes ────────────────────────────────────────────────────────────

    private void EnterScope() => _scopes.Add(new Dictionary<string, SemanticTokenKind>(StringComparer.Ordinal));
    private void ExitScope()  => _scopes.RemoveAt(_scopes.Count - 1);

    private void Bind(string? name, SemanticTokenKind kind)
    {
        if (name is not null) _scopes[^1][name] = kind;
    }

    // Innermost-first, exactly like the checker's TryLookup. A name that is not bound anywhere and
    // is not a declared type is a value — the program type-checked, so there is nothing else it
    // could be.
    private SemanticTokenKind KindOfReference(string name)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out var kind)) return kind;
        return IsTypeName(name) ? SemanticTokenKind.Type : SemanticTokenKind.Variable;
    }

    private bool IsTypeName(string name) =>
        _checker.ObjectDefinitions.ContainsKey(name) || _checker.InterfaceDefinitions.ContainsKey(name);

    // ── Type annotations ──────────────────────────────────────────────────

    // The type in `given (the card c)` is a NAME in the source and a CufetType in the tree, and a
    // CufetType has no position — it is shared and cached, and a type is not a place. So the names
    // an annotation spells are read off the type and then found in the annotation's own token span
    // by the same cursor that places every other name. Only a word the annotation genuinely names
    // can be emitted this way, which is what keeps a variable that happens to share a type's name
    // from being recoloured: it is never inside an annotation.
    //
    // Built-in scalars (number, text, fact, bits) produce no names on purpose — they are keyword
    // tokens the TextMate grammar already colours, and re-colouring them would only be redundant.
    private void EmitAnnotationNames(Cursor cursor, CufetType? annotation)
    {
        if (annotation is null) return;
        var names = new List<(string Name, SemanticTokenKind Kind)>();
        CollectAnnotationNames(annotation, names);
        foreach (var (name, kind) in names)
            if (kind != SemanticTokenKind.Type || IsTypeName(name))
                EmitFound(cursor, name, kind);
    }

    // Every name an annotation spells, in the order the surface form spells it. ObjectType and
    // InterfaceType end the recursion: an ObjectType carries its own fields and methods, and not
    // one of those words is written at the annotation's site. A record SHAPE is the opposite case —
    // its field names are written right there, between their types.
    private static void CollectAnnotationNames(
        CufetType? type, List<(string Name, SemanticTokenKind Kind)> into)
    {
        switch (type)
        {
            case ObjectType o:         into.Add((o.Name, SemanticTokenKind.Type)); break;
            case InterfaceType i:      into.Add((i.Name, SemanticTokenKind.Type)); break;
            case SeriesType s:         CollectAnnotationNames(s.ElementType, into); break;  // series of card, catalogue of card
            case VoidableType v:       CollectAnnotationNames(v.Inner, into); break;        // voidable card
            case FailureType f:        CollectAnnotationNames(f.Inner, into); break;        // card or failure
            case ChannelType c:        CollectAnnotationNames(c.ElementType, into); break;  // channel of card
            case ReadableStreamType r: CollectAnnotationNames(r.ElementType, into); break;
            case WritableStreamType w: CollectAnnotationNames(w.ElementType, into); break;
            case MapType m:                                                                 // map/atlas from K to V
                CollectAnnotationNames(m.KeyType, into);
                CollectAnnotationNames(m.ValueType, into);
                break;
            case UnionType { Cases: { } cases }:                                            // (card or deck)
                foreach (var c in cases) CollectAnnotationNames(c, into);
                break;
            case RecordType rt:                                                             // records like (the card top, ...)
                foreach (var p in rt.PositionalTypes) CollectAnnotationNames(p, into);
                foreach (var (name, t) in rt.NamedFields)
                {
                    CollectAnnotationNames(t, into);
                    into.Add((name, SemanticTokenKind.Property));
                }
                break;
            case FunctionType ft:
                foreach (var p in ft.ParameterTypes) CollectAnnotationNames(p, into);
                CollectAnnotationNames(ft.ReturnType, into);
                break;
        }
    }

    // ── Statements ────────────────────────────────────────────────────────

    // Bind declarations hoist, so a call may precede the declaration textually. Pre-register the
    // block's own function names before walking it, the way Pass1Hoist does for the whole program.
    private void WalkBlock(IReadOnlyList<IStatement> statements)
    {
        foreach (var s in statements)
            if (s is BindStatement { UntoType: null } bind)
                Bind(bind.Name, SemanticTokenKind.Function);
        foreach (var s in statements)
            Walk(s);
    }

    private void WalkNested(IReadOnlyList<IStatement>? statements)
    {
        if (statements is null) return;
        EnterScope();
        WalkBlock(statements);
        ExitScope();
    }

    private void Walk(IStatement statement)
    {
        switch (statement)
        {
            case DefineStatement d:
                Walk(d.Value);
                EmitFound(Cursor.At(_tokens, d.Line, d.Column), d.Name,
                        SemanticTokenKind.Variable, SemanticTokenModifier.Declaration);
                Bind(d.Name, SemanticTokenKind.Variable);
                break;

            case BecomesStatement b:
                EmitAt(b.Name, b.Line, b.Column, KindOfReference(b.Name));
                Walk(b.Value);
                break;

            case StateStatement s:
                Walk(s.Value);
                break;

            case ObjectDefinition od:
                WalkObjectDefinition(od);
                break;

            case InterfaceDefinition id:
            {
                // 'The void function steer, given (the number angle)' — return type, then the
                // method's name, then its parameter types.
                var cursor = Cursor.At(_tokens, id.Line, id.Column);
                EmitFound(cursor, id.Name, SemanticTokenKind.Type, SemanticTokenModifier.Declaration);
                foreach (var (methodName, returnType, paramTypes) in id.Methods)
                {
                    EmitAnnotationNames(cursor, returnType);
                    EmitFound(cursor, methodName, SemanticTokenKind.Function, SemanticTokenModifier.Declaration);
                    foreach (var p in paramTypes) EmitAnnotationNames(cursor, p);
                }
                break;
            }

            case BindStatement bind:
                WalkBind(bind);
                break;

            case GetterDeclaration g:
            {
                // Get <name> [unto <type>] as <return type>:
                var cursor = Cursor.At(_tokens, g.Line, g.Column);
                EmitFound(cursor, g.Name, SemanticTokenKind.Property, SemanticTokenModifier.Declaration);
                EmitFound(cursor, g.UntoType, SemanticTokenKind.Type);
                EmitAnnotationNames(cursor, g.ReturnType);
                WalkNested(g.Body);
                break;
            }

            case SetterDeclaration st:
            {
                // Set <name> [unto <type>] given (the <param type> <param name>):
                var cursor = Cursor.At(_tokens, st.Line, st.Column);
                EmitFound(cursor, st.Name, SemanticTokenKind.Property, SemanticTokenModifier.Declaration);
                EmitFound(cursor, st.UntoType, SemanticTokenKind.Type);
                EmitAnnotationNames(cursor, st.ParamType);
                EmitFound(cursor, st.ParamName, SemanticTokenKind.Parameter, SemanticTokenModifier.Declaration);
                EnterScope();
                Bind(st.ParamName, SemanticTokenKind.Parameter);
                WalkBlock(st.Body);
                ExitScope();
                break;
            }

            case UnmakerDeclaration u:
            {
                var cursor = Cursor.At(_tokens, u.Line, u.Column);
                EmitFound(cursor, u.UnmakesTypeName, SemanticTokenKind.Type);
                EmitFound(cursor, u.Name, SemanticTokenKind.Function, SemanticTokenModifier.Declaration);
                WalkNested(u.Body);
                break;
            }

            case OperatorOverloadDeclaration o:
            {
                var cursor = Cursor.At(_tokens, o.Line, o.Column);
                EmitFound(cursor, o.LeftName,  SemanticTokenKind.Parameter, SemanticTokenModifier.Declaration);
                EmitFound(cursor, o.OperandTypeName, SemanticTokenKind.Type);
                EmitFound(cursor, o.RightName, SemanticTokenKind.Parameter, SemanticTokenModifier.Declaration);
                EmitFound(cursor, o.OperandTypeName, SemanticTokenKind.Type);
                EnterScope();
                Bind(o.LeftName,  SemanticTokenKind.Parameter);
                Bind(o.RightName, SemanticTokenKind.Parameter);
                WalkBlock(o.Body);
                ExitScope();
                break;
            }

            case IfStatement i:
                foreach (var arm in i.Arms)
                {
                    Walk(arm.Condition);
                    WalkNested(arm.Body);
                }
                WalkNested(i.ElseBody);
                break;

            case WhileStatement w:
                Walk(w.Condition);
                WalkNested(w.Body);
                break;

            case RepeatUntilStatement r:
                WalkNested(r.Body);
                Walk(r.Condition);
                break;

            case ForEachStatement f:
                Walk(f.Series);
                EnterScope();
                EmitFound(Cursor.At(_tokens, f.Line, f.Column), f.IteratorName,
                          SemanticTokenKind.Variable, SemanticTokenModifier.Declaration);
                Bind(f.IteratorName, SemanticTokenKind.Variable);
                WalkBlock(f.Body);
                ExitScope();
                break;

            case ForEachFromInputStatement fi:
                EnterScope();
                EmitFound(Cursor.At(_tokens, fi.Line, fi.Column), fi.IteratorName,
                        SemanticTokenKind.Variable, SemanticTokenModifier.Declaration);
                Bind(fi.IteratorName, SemanticTokenKind.Variable);
                WalkBlock(fi.Body);
                ExitScope();
                break;

            case CastStatement c:
                WalkCallee(c.Function);
                foreach (var a in c.Args) Walk(a);
                break;

            case ReturnStatement ret:
                Walk(ret.Value);
                break;

            case SeriesInsertStatement a2:
                Walk(a2.Value); Walk(a2.Series); Walk(a2.AfterIndex);
                break;

            case SeriesRemoveAtStatement ra:
                Walk(ra.Series); Walk(ra.Index);
                break;

            case SeriesRemoveValueStatement rv:
                Walk(rv.Series); Walk(rv.Value);
                break;

            case SeriesSetStatement ss:
                Walk(ss.Series); Walk(ss.Index); Walk(ss.Value);
                break;

            case MatrixSetStatement mss:
                Walk(mss.Matrix); Walk(mss.Row); Walk(mss.Col); Walk(mss.Value);
                break;

            case RecordNamedSetStatement rns:
                EmitAt(rns.FieldName, rns.Line, rns.Column, SemanticTokenKind.Property);
                Walk(rns.Record); Walk(rns.Value);
                break;

            case PossessiveSetStatement ps:
                Walk(ps.Target);
                EmitFound(Cursor.At(_tokens, ps.Line, ps.Column), ps.Member, SemanticTokenKind.Property);
                Walk(ps.Value);
                break;

            case TryStatement t:
                WalkNested(t.Body);
                WalkNested(t.FailureHandler);
                WalkNested(t.ExceptionHandler);
                break;

            case MapSetStatement ms:
                Walk(ms.Map); Walk(ms.Key); Walk(ms.Value);
                break;

            case CurrentDirectorySetStatement cd:
                Walk(cd.Path);
                break;

            case FileWriteStatement fw:
                Walk(fw.Value); Walk(fw.Path);
                break;

            case WithOpenStatement wo:
                Walk(wo.Path);
                EnterScope();
                EmitFound(Cursor.At(_tokens, wo.Line, wo.Column), wo.BindingName,
                        SemanticTokenKind.Variable, SemanticTokenModifier.Declaration);
                Bind(wo.BindingName, SemanticTokenKind.Variable);
                WalkBlock(wo.Body);
                ExitScope();
                break;

            case WriteToStreamStatement ws:
                Walk(ws.Value); Walk(ws.Stream);
                break;

            case PullRabbitStatement pr:
                EnterScope();
                EmitFound(Cursor.At(_tokens, pr.Line, pr.Column), pr.Name,
                        SemanticTokenKind.Variable, SemanticTokenModifier.Declaration);
                Bind(pr.Name, SemanticTokenKind.Variable);
                WalkBlock(pr.Body);
                ExitScope();
                break;

            case LaunchTaskStatement lt:
                // The task's name is bound in the ENCLOSING scope, not the task body — that is
                // where 'the awaited result of <name>' reads it.
                EmitFound(Cursor.At(_tokens, lt.Line, lt.Column), lt.Name,
                        SemanticTokenKind.Variable, SemanticTokenModifier.Declaration);
                Bind(lt.Name, SemanticTokenKind.Variable);
                WalkNested(lt.Body);
                break;

            case PullStatement pull:
            {
                var cursor = Cursor.At(_tokens, pull.Line, pull.Column);
                EnterScope();
                foreach (var (bookName, localName) in pull.Books)
                {
                    EmitFound(cursor, bookName, SemanticTokenKind.Namespace, SemanticTokenModifier.Declaration);
                    if (!string.Equals(localName, bookName, StringComparison.Ordinal))
                        EmitFound(cursor, localName, SemanticTokenKind.Namespace, SemanticTokenModifier.Declaration);
                    Bind(localName, SemanticTokenKind.Namespace);
                }
                WalkBlock(pull.Body);
                ExitScope();
                break;
            }

            case SendStatement send:
                Walk(send.Value); Walk(send.Channel);
                break;

            case CloseStatement close:
                Walk(close.Channel);
                break;

            // Both of these OPEN a statement while lexing as ordinary identifiers, so the grammar
            // cannot colour them without also colouring a variable that happens to share the
            // spelling. Here the node's own existence is the proof: reaching this arm means the
            // parser decided this occurrence is the statement, whatever its capitalisation. The
            // position is the keyword's own, recorded when the statement was parsed.
            // The subject is an ordinary expression; the arm CASES are type names, which get the
            // same kind an annotation would. `it` is not emitted — it is a keyword, not a name the
            // author chose.
            case JudgeStatement judge:
                Walk(judge.Subject);
                foreach (var arm in judge.Arms)
                {
                    // An arm's cases are spelled in its own header — `A circle:`, or
                    // `A square or a rectangle:` — so one cursor anchored at the arm places them
                    // all, and stops at the ':' that opens the body.
                    var arms = Cursor.At(_tokens, arm.Line, arm.Column);
                    foreach (var oneCase in arm.Cases) EmitAnnotationNames(arms, oneCase);
                    WalkNested(arm.Body);
                }
                WalkNested(judge.OtherwiseBody);
                break;

            case SeedChanceStatement seed:
                Emit(seed.Line, seed.Column, "seed".Length, SemanticTokenKind.Keyword);
                Walk(seed.Seed);
                break;

            case OutputStatement outp:
                Emit(outp.Line, outp.Column, "output".Length, SemanticTokenKind.Keyword);
                Walk(outp.Value);
                break;

            case PipeExpression pipe:
                Walk((IExpression)pipe);
                break;

            // No names: Stop., Skip., Suppress., Acknowledge., Yield.
            default:
                break;
        }
    }

    private void WalkObjectDefinition(ObjectDefinition od)
    {
        // Define object <name> with (<positional types>, the <field type> <field name>, ...)
        //   [and as a <embedded>] [and <interface> ...]
        var cursor = Cursor.At(_tokens, od.Line, od.Column);
        EmitFound(cursor, od.Name, SemanticTokenKind.Type, SemanticTokenModifier.Declaration);
        foreach (var positional in od.PositionalTypes)
            EmitAnnotationNames(cursor, positional);
        foreach (var (fieldName, fieldType) in od.NamedFields)
        {
            EmitAnnotationNames(cursor, fieldType);
            EmitFound(cursor, fieldName, SemanticTokenKind.Property, SemanticTokenModifier.Declaration);
        }
        EmitFound(cursor, od.EmbeddedTypeName, SemanticTokenKind.Type);
        foreach (var conformed in od.ConformedInterfaces)
            EmitFound(cursor, conformed, SemanticTokenKind.Type);

        foreach (var m in od.Methods) WalkBind(m);
        foreach (var g in od.Getters) Walk(g);
        foreach (var s in od.Setters) Walk(s);
    }

    private void WalkBind(BindStatement bind)
    {
        // Source order inside the header: 'making a <type>' or the return type, then the name,
        // then 'unto <type>', then 'the <param type> <param name>' per parameter — the cursor
        // visits them in that order so each name is looked for where it was written.
        var cursor = Cursor.At(_tokens, bind.Line, bind.Column);
        EmitFound(cursor, bind.ConstructsTypeName, SemanticTokenKind.Type);
        // A named constructor's return type IS its ConstructsTypeName, spelled once; the token is
        // already claimed above, so this finds nothing and emits nothing.
        EmitAnnotationNames(cursor, bind.ReturnType);
        EmitFound(cursor, bind.Name, SemanticTokenKind.Function, SemanticTokenModifier.Declaration);
        EmitFound(cursor, bind.UntoType, SemanticTokenKind.Type);
        foreach (var (paramType, paramName) in bind.Parameters)
        {
            EmitAnnotationNames(cursor, paramType);
            EmitFound(cursor, paramName, SemanticTokenKind.Parameter, SemanticTokenModifier.Declaration);
        }

        EnterScope();
        foreach (var (_, paramName) in bind.Parameters)
            Bind(paramName, SemanticTokenKind.Parameter);
        WalkBlock(bind.Body);
        ExitScope();
    }

    // ── Expressions ───────────────────────────────────────────────────────

    // A Cast's callee is a function by construction, whatever shape it takes: a bare name
    // ('Cast run-report on (...)'), a method reached through a possessive ("Cast car's steer"),
    // or a book member ("math's floor of x").
    private void WalkCallee(IExpression callee)
    {
        switch (callee)
        {
            case VariableReference vr:
                EmitAt(vr.Name, vr.Line, vr.Column, SemanticTokenKind.Function);
                break;

            case PossessiveAccess pa:
                Walk(pa.Target);
                EmitFound(Cursor.At(_tokens, pa.Line, pa.Column), pa.Member, SemanticTokenKind.Function);
                break;

            default:
                Walk(callee);
                break;
        }
    }

    private void Walk(IExpression? expression)
    {
        switch (expression)
        {
            case null:
                break;

            case VariableReference vr:
                EmitAt(vr.Name, vr.Line, vr.Column, KindOfReference(vr.Name));
                break;

            case CastExpression c:
                WalkCallee(c.Function);
                foreach (var a in c.Args) Walk(a);
                break;

            case PossessiveAccess pa:
                Walk(pa.Target);
                EmitFound(Cursor.At(_tokens, pa.Line, pa.Column), pa.Member, SemanticTokenKind.Property);
                break;

            case RecordNamedAccess rna:
                EmitAt(rna.FieldName, rna.Line, rna.Column, SemanticTokenKind.Property);
                Walk(rna.Record);
                break;

            case ObjectLiteral ol:
            {
                var cursor = Cursor.At(_tokens, ol.Line, ol.Column);
                EmitFound(cursor, ol.TypeName, SemanticTokenKind.Type);
                foreach (var (name, _) in ol.NamedValues)
                    EmitFound(cursor, name, SemanticTokenKind.Property);
                foreach (var v in ol.PositionalValues) Walk(v);
                foreach (var (_, v) in ol.NamedValues)  Walk(v);
                break;
            }

            case RecordLiteral rl:
            {
                var cursor = Cursor.At(_tokens, rl.Line, rl.Column);
                foreach (var (name, _) in rl.NamedFields)
                    EmitFound(cursor, name, SemanticTokenKind.Property);
                foreach (var v in rl.PositionalFields) Walk(v);
                foreach (var (_, v) in rl.NamedFields)  Walk(v);
                break;
            }

            case SortExpression so:
                Walk(so.Series);
                EmitFound(Cursor.At(_tokens, so.Line, so.Column), so.ByField, SemanticTokenKind.Property);
                break;

            case LambdaLiteral lam:
            {
                var cursor = Cursor.At(_tokens, lam.Line, lam.Column);
                foreach (var (paramType, paramName) in lam.Parameters)
                {
                    EmitAnnotationNames(cursor, paramType);
                    EmitFound(cursor, paramName, SemanticTokenKind.Parameter, SemanticTokenModifier.Declaration);
                }
                EnterScope();
                foreach (var (_, paramName) in lam.Parameters)
                    Bind(paramName, SemanticTokenKind.Parameter);
                WalkBlock(lam.Body);
                ExitScope();
                break;
            }

            case UnaryExpression u:  Walk(u.Operand); break;
            case BinaryExpression b: Walk(b.Left); Walk(b.Right); break;
            case BitsShift bs:       Walk(bs.Target); Walk(bs.Amount); break;
            case BitsConvert bc:     Walk(bc.Target); break;

            // 'a series of card', 'a catalogue of (card or deck)' — the element type is written
            // right after the keyword this node is positioned on.
            case SeriesLiteral sl:
                EmitAnnotationNames(Cursor.At(_tokens, sl.Line, sl.Column), sl.Annotation);
                foreach (var e in sl.Elements) Walk(e);
                break;

            case SeriesAccess sa:    Walk(sa.Target); Walk(sa.Index); break;
            case SeriesLength sn:    Walk(sn.Series); break;
            case RangeExpression re: Walk(re.Start); Walk(re.End); Walk(re.Step); break;

            case TextJoin tj:            Walk(tj.Left); Walk(tj.Right); break;
            case TextConvert tc:         Walk(tc.Value); break;
            case NumberConvert nc:       Walk(nc.Value); break;
            case TextLength tl:          Walk(tl.Target); break;
            case TextSplit tsp:          Walk(tsp.Text); Walk(tsp.Delimiter); break;
            case TextContains tct:       Walk(tct.Text); Walk(tct.Substring); break;
            case TextFind tf:            Walk(tf.Substring); Walk(tf.Text); break;
            case TextSubstringRange tsr: Walk(tsr.Text); Walk(tsr.From); Walk(tsr.To); break;
            case TextSubstringEdge tse:  Walk(tse.Text); Walk(tse.Count); break;
            case TextReplace trp:        Walk(trp.Text); Walk(trp.Old); Walk(trp.New); break;
            case TextCase tcs:           Walk(tcs.Text); break;
            case TextTrim ttr:           Walk(ttr.Text); break;

            case ButVoidDefault bv:   Walk(bv.Voidable); Walk(bv.Default); break;
            case ConditionalExpression ce:
                Walk(ce.Value); Walk(ce.Condition); Walk(ce.Alternative); break;
            case FailureLiteral fl:   Walk(fl.Message); Walk(fl.Category); break;
            case FailureFallback ff:  Walk(ff.Fallible); Walk(ff.Default); break;
            case FailurePropagate fp: Walk(fp.Fallible); break;

            // 'a map from text to card', 'an atlas from text to (card or deck)'
            case MapLiteral ml:
            {
                var cursor = Cursor.At(_tokens, ml.Line, ml.Column);
                EmitAnnotationNames(cursor, ml.KeyType);
                EmitAnnotationNames(cursor, ml.ValueType);
                foreach (var (k, v) in ml.Pairs) { Walk(k); Walk(v); }
                break;
            }
            case MapLookup mlk:   Walk(mlk.Map); Walk(mlk.Key); break;
            case MapHasKey mhk:   Walk(mhk.Map); Walk(mhk.Key); break;
            case MapHasEntry mhe: Walk(mhe.Map); Walk(mhe.Key); break;
            case MapSize msz:     Walk(msz.Map); break;

            case ReadExpression rd:                 Walk(rd.Source); break;
            case FileReadExpression fr:             Walk(fr.Path); break;
            case EnvironmentVariableExpression ev:  Walk(ev.Name); break;
            case DirectoryContentsExpression dc:    Walk(dc.Path); break;
            case PathCheckExpression pc:            Walk(pc.Path); break;
            case RunExpression run:
                Walk(run.Program);
                foreach (var a in run.Args) Walk(a);
                break;

            case MatrixLiteral mtl:
                foreach (var row in mtl.Rows) foreach (var e in row) Walk(e);
                break;
            case MatrixAccess mta: Walk(mta.Matrix); Walk(mta.Row); Walk(mta.Col); break;
            case MatrixSized mts:  Walk(mts.Rows); Walk(mts.Cols); Walk(mts.Fill); break;

            case ChannelCreation cc:         // a channel of card
                EmitAnnotationNames(Cursor.At(_tokens, cc.Line, cc.Column), cc.ElementType);
                break;
            case DeliveryExpression de:      Walk(de.Channel); break;
            case AwaitedResultExpression ar: Walk(ar.Task); break;

            case IsTypeCheck it:        // <expr> is a card
                Walk(it.Target);
                EmitAnnotationNames(Cursor.At(_tokens, it.Line, it.Column), it.Type);
                break;
            case RandomNumber rn:       Walk(rn.Low); Walk(rn.High); break;
            case RandomItem ri:         Walk(ri.Series); break;
            case RandomlyShuffled rs:   Walk(rs.Series); break;

            case PipeExpression pipe:   Walk(pipe.Left); Walk(pipe.Right); break;

            // Literals and nullary forms carry no names.
            default:
                break;
        }
    }

    // ── Finding a name in the token stream ────────────────────────────────

    // A cursor over the already-lexed tokens, anchored at a construct's position and used to place
    // every name that construct declares. Each name it places claims its token, so asking the same
    // cursor for the same word twice gets the two occurrences in turn — which is what keeps
    // 'Bind number to span, given (the number span)' from reporting the parameter at the function
    // name's position.
    //
    // It looks forward first and only then re-scans from the anchor. Forward-first is what makes
    // the repeated-word case land in source order; the re-scan is what saves the cases where a
    // construct's names do not reach the AST in the order they were written, since an object's
    // named fields arrive sorted, not as spelled.
    private sealed class Cursor
    {
        // A construct's header never runs past its own ':' or '.', and never past a nested block's
        // 'Done'. Stopping there is what makes a not-found name stay unemitted instead of matching
        // some unrelated later word.
        private static readonly TokenType[] HeaderEnd =
            [TokenType.Colon, TokenType.Dot, TokenType.Done, TokenType.Eof];

        private readonly IReadOnlyList<Token> _tokens;
        private readonly int                  _anchor;
        private readonly HashSet<int>         _claimed = [];
        private int                           _index;

        private Cursor(IReadOnlyList<Token> tokens, int anchor)
        {
            _tokens = tokens;
            _anchor = anchor;
            _index  = anchor;
        }

        // Anchors a cursor at the first token at or after (line, column).
        public static Cursor At(IReadOnlyList<Token> tokens, int line, int column)
        {
            int lo = 0, hi = tokens.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                var t   = tokens[mid];
                if (t.Line < line || (t.Line == line && t.Column < column)) lo = mid + 1;
                else                                                        hi = mid;
            }
            return new Cursor(tokens, lo);
        }

        // The position and width of the next unclaimed occurrence of `name` in this construct's
        // header, or null when the header holds none. Multi-word names (a book member like
        // "absolute value") span the consecutive tokens that spell them.
        public (int Line, int Column, int Length)? Next(string name) =>
            Scan(name, _index) ?? Scan(name, _anchor);

        private (int Line, int Column, int Length)? Scan(string name, int from)
        {
            var words = name.Split(' ');

            for (int i = from; i < _tokens.Count; i++)
            {
                var t = _tokens[i];
                if (!string.Equals(t.Lexeme, words[0], StringComparison.Ordinal))
                {
                    if (HeaderEnd.Contains(t.Type)) break;
                    continue;
                }
                if (_claimed.Contains(i)) continue;

                int last   = i;
                int length = t.Lexeme.Length;
                for (int w = 1; w < words.Length; w++)
                {
                    var next = last + 1 < _tokens.Count ? _tokens[last + 1] : null;
                    if (next is null || next.Line != t.Line ||
                        !string.Equals(next.Lexeme, words[w], StringComparison.Ordinal))
                        break;
                    last   = last + 1;
                    length = next.Column + next.Lexeme.Length - t.Column;
                }

                for (int c = i; c <= last; c++) _claimed.Add(c);
                _index = Math.Max(_index, last + 1);
                return (t.Line, t.Column, length);
            }

            return null;
        }
    }
}
