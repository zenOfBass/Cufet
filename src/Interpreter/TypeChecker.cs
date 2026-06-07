using NLP.Lexer;

namespace NLP.Interpreter;

public enum CufetType { Number, Text, Fact, SeriesPending }

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
            case IfStatement ifStmt:
                foreach (var arm in ifStmt.Arms)
                    foreach (var s in arm.Body)
                        CheckStatement(s);
                if (ifStmt.ElseBody != null)
                    foreach (var s in ifStmt.ElseBody)
                        CheckStatement(s);
                break;
            case WhileStatement whileStmt:
                foreach (var s in whileStmt.Body)
                    CheckStatement(s);
                break;
            case RepeatUntilStatement repeatUntil:
                foreach (var s in repeatUntil.Body)
                    CheckStatement(s);
                break;
            case ForEachStatement forEach:
                CheckForEach(forEach);
                break;
            // StateStatement, SeriesAddStatement, SeriesRemoveAtStatement,
            // SeriesRemoveValueStatement, SeriesSetStatement, StopStatement,
            // SkipStatement — no type-checking in Stage 1
        }
    }

    private void CheckDefine(DefineStatement define)
    {
        var type = InferType(define.Value);
        if (type == null)
            throw new TypeException(
                $"line {define.Line}: cannot infer the type of the value assigned to '{define.Name}'.");
        _env[define.Name] = new TypeInfo(type.Value, define.Value, define.Line);
    }

    private void CheckBecomes(BecomesStatement becomes)
    {
        if (!_env.TryGetValue(becomes.Name, out var existing))
            return; // undeclared — interpreter catches this at runtime

        if (existing.Type == CufetType.SeriesPending)
            return; // targeted deferral: series-derived type — Stage 3 completes this check

        var rhsType = InferType(becomes.Value)
            ?? throw new TypeException(
                $"line {becomes.Line}: cannot infer the type of the value assigned to '{becomes.Name}'.");

        if (rhsType != existing.Type)
            throw new TypeException(
                $"line {becomes.Line}: '{becomes.Name}' is {existing.Type.ToString().ToLower()} " +
                $"(established as {FormatExpr(existing.EstablishingExpr)} on line {existing.EstablishingLine}), " +
                $"but the new value is {rhsType.ToString().ToLower()}.");
    }

    // Mirrors the runtime save/restore pattern: iterator is SeriesPending during body check,
    // previous TypeInfo (if any) is restored after.
    private void CheckForEach(ForEachStatement forEach)
    {
        var iterKey = forEach.IteratorName ?? "it";
        var hadPrev = _env.TryGetValue(iterKey, out var prev);
        _env[iterKey] = new TypeInfo(CufetType.SeriesPending,
            new VariableReference(forEach.SeriesName), 0);
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

    // Returns null for two distinct cases:
    //   - VariableReference to an undeclared name → genuine inference gap, error at call site
    //   - any expression structure not yet handled → also a genuine gap, error at call site
    // Returns SeriesPending for series-derived expressions → targeted deferral, not an error.
    private CufetType? InferType(IExpression expr) => expr switch
    {
        NumberLiteral                                                                   => CufetType.Number,
        StringLiteral                                                                   => CufetType.Text,
        UnaryExpression { Op: TokenType.Minus }                                         => CufetType.Number,
        BinaryExpression { Op: TokenType.Plus  or TokenType.Minus
                              or TokenType.Star or TokenType.Slash }                    => CufetType.Number,
        BinaryExpression { Op: TokenType.Equal    or TokenType.NotEqual
                              or TokenType.Lt     or TokenType.Gt
                              or TokenType.Lte    or TokenType.Gte }                    => CufetType.Fact,
        VariableReference { Name: var n } when _env.TryGetValue(n, out var ti)         => ti.Type,
        VariableReference                                                               => null,
        SeriesLiteral                                                                   => CufetType.SeriesPending,
        SeriesAccess                                                                    => CufetType.SeriesPending,
        SeriesLength                                                                    => CufetType.SeriesPending,
        _                                                                               => null,
    };

    private static string FormatExpr(IExpression expr) => expr switch
    {
        NumberLiteral    { Value: var v } => v.ToString(),
        StringLiteral    { Value: var v } => $"\"{v}\"",
        VariableReference { Name: var n } => n,
        _                                 => "<expression>",
    };
}
