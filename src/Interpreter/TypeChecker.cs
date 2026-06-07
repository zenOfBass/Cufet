using NLP.Lexer;

namespace NLP.Interpreter;

// CufetType is a recursive value-equality record hierarchy.
// CufetType.Number / .Text / .Fact are canonical singletons for the three scalars.
public abstract record CufetType
{
    public static readonly CufetType Number = new NumberType();
    public static readonly CufetType Text   = new TextType();
    public static readonly CufetType Fact   = new FactType();
}
public sealed record NumberType()                      : CufetType;
public sealed record TextType()                        : CufetType;
public sealed record FactType()                        : CufetType;
public sealed record SeriesType(CufetType ElementType) : CufetType;

public record TypeInfo(CufetType Type, IExpression EstablishingExpr, int EstablishingLine);

public sealed class TypeException : Exception
{
    public TypeException(string message) : base(message) { }
}

public sealed class TypeChecker
{
    private readonly Dictionary<string, TypeInfo> _env = new();

    public void Check(Program program)
    {
        foreach (var stmt in program.Statements)
            CheckStatement(stmt);
    }

    private void CheckStatement(IStatement stmt)
    {
        switch (stmt)
        {
            case DefineStatement define:
                CheckDefine(define);
                break;
            case BecomesStatement becomes:
                CheckBecomes(becomes);
                break;
            case StateStatement state:
                _ = InferType(state.Value);
                break;
            case IfStatement ifStmt:
                foreach (var arm in ifStmt.Arms)
                {
                    _ = InferType(arm.Condition);
                    foreach (var s in arm.Body)
                        CheckStatement(s);
                }
                if (ifStmt.ElseBody != null)
                    foreach (var s in ifStmt.ElseBody)
                        CheckStatement(s);
                break;
            case WhileStatement whileStmt:
                _ = InferType(whileStmt.Condition);
                foreach (var s in whileStmt.Body)
                    CheckStatement(s);
                break;
            case RepeatUntilStatement repeatUntil:
                foreach (var s in repeatUntil.Body)
                    CheckStatement(s);
                _ = InferType(repeatUntil.Condition);
                break;
            case ForEachStatement forEach:
                CheckForEach(forEach);
                break;
            case SeriesAddStatement add:
                CheckSeriesAdd(add);
                break;
            case SeriesRemoveValueStatement removeVal:
                CheckSeriesRemoveValue(removeVal);
                break;
            case SeriesSetStatement seriesSet:
                CheckSeriesSet(seriesSet);
                break;
            case SeriesRemoveAtStatement removeAt:
                CheckSeriesRemoveAt(removeAt);
                break;
        }
    }

    private void CheckDefine(DefineStatement define)
    {
        var type = InferType(define.Value);
        if (type == null)
            throw new TypeException(
                $"line {define.Line}: cannot infer the type of the value assigned to '{define.Name}'.");
        _env[define.Name] = new TypeInfo(type, define.Value, define.Line);
    }

    private void CheckBecomes(BecomesStatement becomes)
    {
        if (!_env.TryGetValue(becomes.Name, out var existing))
            return;

        var rhsType = InferType(becomes.Value);
        if (rhsType == null) return;

        if (rhsType != existing.Type)
            throw new TypeException(
                $"line {becomes.Line}: '{becomes.Name}' is {FormatType(existing.Type)} " +
                $"(established as {FormatExpr(existing.EstablishingExpr)} on line {existing.EstablishingLine}), " +
                $"but the new value is {FormatType(rhsType)}.");
    }

    private void CheckForEach(ForEachStatement forEach)
    {
        if (!_env.TryGetValue(forEach.SeriesName, out var seriesInfo))
            return; // undeclared series — runtime catches; skip body to avoid false positives

        if (seriesInfo.Type is not SeriesType seriesType)
            throw new TypeException(
                $"line {forEach.Line}: '{forEach.SeriesName}' is {FormatType(seriesInfo.Type)}, not a series.");

        var iterKey = forEach.IteratorName ?? "it";
        var hadPrev = _env.TryGetValue(iterKey, out var prev);
        _env[iterKey] = new TypeInfo(seriesType.ElementType, new VariableReference(forEach.SeriesName), forEach.Line);
        try
        {
            foreach (var s in forEach.Body)
                CheckStatement(s);
        }
        finally
        {
            if (hadPrev) _env[iterKey] = prev!;
            else _env.Remove(iterKey);
        }
    }

    private void CheckSeriesAdd(SeriesAddStatement add)
    {
        if (add.AfterIndex != null) CheckIndex(add.AfterIndex, add.Line);
        if (!_env.TryGetValue(add.SeriesName, out var seriesInfo)) return;
        if (seriesInfo.Type is not SeriesType seriesType) return;

        var valueType = InferType(add.Value);
        if (valueType != null && valueType != seriesType.ElementType)
            throw new TypeException(
                $"line {add.Line}: '{add.SeriesName}' holds {FormatType(seriesType.ElementType)}s " +
                $"but the added value is {FormatType(valueType)}.");
    }

    private void CheckSeriesRemoveValue(SeriesRemoveValueStatement removeVal)
    {
        if (!_env.TryGetValue(removeVal.SeriesName, out var seriesInfo)) return;
        if (seriesInfo.Type is not SeriesType seriesType) return;

        var valueType = InferType(removeVal.Value);
        if (valueType != null && valueType != seriesType.ElementType)
            throw new TypeException(
                $"line {removeVal.Line}: '{removeVal.SeriesName}' holds {FormatType(seriesType.ElementType)}s " +
                $"but the removed value is {FormatType(valueType)}.");
    }

    private void CheckSeriesSet(SeriesSetStatement seriesSet)
    {
        if (seriesSet.Index != null) CheckIndex(seriesSet.Index, seriesSet.Line);
        if (!_env.TryGetValue(seriesSet.SeriesName, out var seriesInfo)) return;
        if (seriesInfo.Type is not SeriesType seriesType) return;

        var valueType = InferType(seriesSet.Value);
        if (valueType != null && valueType != seriesType.ElementType)
            throw new TypeException(
                $"line {seriesSet.Line}: '{seriesSet.SeriesName}' holds {FormatType(seriesType.ElementType)}s " +
                $"but the new value is {FormatType(valueType)}.");
    }

    private void CheckSeriesRemoveAt(SeriesRemoveAtStatement removeAt)
    {
        if (removeAt.Index != null) CheckIndex(removeAt.Index, removeAt.Line);
    }

    private static void CheckIndex(IExpression index, int line)
    {
        if (index is NumberLiteral { Value: var v } && v % 1 != 0)
            throw new TypeException(
                $"line {line}: item positions must be whole numbers; {v} isn't one.");
    }

    // Returns null for genuine inference gaps (undeclared variable, unhandled expression form).
    // Returns a concrete CufetType for anything we can type statically.
    // Throws TypeException for operand type mismatches.
    private CufetType? InferType(IExpression expr) => expr switch
    {
        NumberLiteral                                                           => CufetType.Number,
        StringLiteral                                                           => CufetType.Text,
        UnaryExpression unary                                                   => InferUnary(unary),
        BinaryExpression bin                                                    => InferBinary(bin),
        VariableReference { Name: var n } when _env.TryGetValue(n, out var ti) => ti.Type,
        VariableReference                                                       => null,
        SeriesLiteral lit                                                       => InferSeriesLiteral(lit),
        SeriesAccess acc                                                        => InferSeriesAccess(acc),
        SeriesLength                                                            => CufetType.Number,
        _                                                                       => null,
    };

    private CufetType InferSeriesLiteral(SeriesLiteral lit)
    {
        CufetType? inferred = null;
        for (int i = 0; i < lit.Elements.Count; i++)
        {
            var elemType = InferType(lit.Elements[i]);
            if (elemType == null) continue;
            if (inferred == null)
            {
                inferred = elemType;
            }
            else if (inferred != elemType)
            {
                throw new TypeException(
                    $"line {lit.Line}: every item in a series must be the same type — " +
                    $"the first item is {FormatType(inferred)}, but item {i + 1} is {FormatType(elemType)}.");
            }
        }

        if (lit.Annotation != null)
        {
            if (inferred != null && inferred != lit.Annotation)
                throw new TypeException(
                    $"line {lit.Line}: you said a series of {FormatType(lit.Annotation)}s, " +
                    $"but an element is {FormatType(inferred)}.");
            return new SeriesType(lit.Annotation);
        }

        if (inferred == null)
            throw new TypeException(
                $"line {lit.Line}: cannot determine the element type of this series — " +
                $"add an annotation like 'a series of numbers ()'.");

        return new SeriesType(inferred);
    }

    private CufetType? InferSeriesAccess(SeriesAccess acc)
    {
        if (acc.Index != null) CheckIndex(acc.Index, acc.Line);
        if (!_env.TryGetValue(acc.SeriesName, out var seriesInfo)) return null;
        if (seriesInfo.Type is SeriesType st) return st.ElementType;
        return null;
    }

    private CufetType? InferUnary(UnaryExpression unary)
    {
        var operand = InferType(unary.Operand);
        if (operand == null) return null;
        if (operand == CufetType.Number) return CufetType.Number;
        throw new TypeException(
            $"line {unary.Line}: unary minus requires a number but got {FormatType(operand)}.");
    }

    private CufetType? InferBinary(BinaryExpression bin)
    {
        var left  = InferType(bin.Left);
        var right = InferType(bin.Right);

        if (left == null || right == null) return null;

        var l = left;
        var r = right;

        return bin.Op switch
        {
            TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash
                when l == CufetType.Number && r == CufetType.Number
                => CufetType.Number,
            TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash
                => throw new TypeException(
                    $"line {bin.Line}: arithmetic requires numbers (got {FormatType(l)} and {FormatType(r)})."),
            TokenType.Equal or TokenType.NotEqual
                when l == r
                => CufetType.Fact,
            TokenType.Equal or TokenType.NotEqual
                => throw new TypeException(
                    $"line {bin.Line}: equality comparison requires matching types (got {FormatType(l)} and {FormatType(r)})."),
            TokenType.Lt or TokenType.Gt or TokenType.Lte or TokenType.Gte
                when l == CufetType.Number && r == CufetType.Number
                => CufetType.Fact,
            TokenType.Lt or TokenType.Gt or TokenType.Lte or TokenType.Gte
                => throw new TypeException(
                    $"line {bin.Line}: ordering requires numbers (got {FormatType(l)} and {FormatType(r)})."),
            _ => null
        };
    }

    private static string FormatType(CufetType type) => type switch
    {
        NumberType                           => "number",
        TextType                             => "text",
        FactType                             => "fact",
        SeriesType { ElementType: var elem } => $"series of {FormatType(elem)}",
        _                                    => "<unknown>",
    };

    private static string FormatExpr(IExpression expr) => expr switch
    {
        NumberLiteral    { Value: var v } => v.ToString(),
        StringLiteral    { Value: var v } => $"\"{v}\"",
        VariableReference { Name: var n } => n,
        _                                 => "<expression>",
    };
}
