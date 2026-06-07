using NLP.Lexer;

namespace NLP.Interpreter;

public interface IExpression { }
public interface IStatement  { }

public sealed record NumberLiteral(decimal Value)                                        : IExpression;
public sealed record StringLiteral(string Value)                                         : IExpression;
public sealed record VariableReference(string Name)                                      : IExpression;
public sealed record UnaryExpression(TokenType Op, IExpression Operand)                  : IExpression;
public sealed record BinaryExpression(IExpression Left, TokenType Op, IExpression Right) : IExpression;
public sealed record SeriesLiteral(IReadOnlyList<IExpression> Elements)                  : IExpression;

// Index == null → last element
public sealed record SeriesAccess(string SeriesName, IExpression? Index) : IExpression;

// the number of <series>
public sealed record SeriesLength(string SeriesName) : IExpression;

// Add X to series (append: AfterIndex=null, ToStart=false)
// Add X to the start (prepend: AfterIndex=null, ToStart=true)
// Add X after position (insert: AfterIndex=expr, ToStart=false)
public sealed record SeriesAddStatement(
    IExpression Value,
    string SeriesName,
    IExpression? AfterIndex,
    bool ToStart
) : IStatement;

// Remove by position (Index == null → last)
public sealed record SeriesRemoveAtStatement(string SeriesName, IExpression? Index) : IStatement;

// Remove first occurrence by value
public sealed record SeriesRemoveValueStatement(string SeriesName, IExpression Value) : IStatement;

// Element assignment (Index == null → last)
public sealed record SeriesSetStatement(
    string SeriesName,
    IExpression? Index,
    IExpression Value
) : IStatement;

public sealed record StateStatement(IExpression Value)                        : IStatement;
public sealed record DefineStatement(string Name, IExpression Value, int Line)  : IStatement;
public sealed record BecomesStatement(string Name, IExpression Value, int Line) : IStatement;

public sealed record ConditionArm(IExpression Condition, IReadOnlyList<IStatement> Body);
public sealed record IfStatement(
    IReadOnlyList<ConditionArm> Arms,
    IReadOnlyList<IStatement>? ElseBody
) : IStatement;

public sealed record WhileStatement(
    IExpression Condition,
    IReadOnlyList<IStatement> Body
) : IStatement;

public sealed record RepeatUntilStatement(
    IReadOnlyList<IStatement> Body,
    IExpression Condition
) : IStatement;

public sealed record StopStatement() : IStatement;
public sealed record SkipStatement() : IStatement;

// IteratorName == null → bare-it loop; element is bound to "it"
public sealed record ForEachStatement(
    string? IteratorName,
    string SeriesName,
    IReadOnlyList<IStatement> Body
) : IStatement;

public sealed record Program(IReadOnlyList<IStatement> Statements);
