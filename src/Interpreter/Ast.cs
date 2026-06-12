using Cufet.Lexer;

namespace Cufet.Interpreter;

public interface IExpression { }
public interface IStatement  { }

public sealed record NumberLiteral(decimal Value)                                        : IExpression;
public sealed record StringLiteral(string Value)                                         : IExpression;
public sealed record VariableReference(string Name, int Line)                            : IExpression;
public sealed record UnaryExpression(TokenType Op, IExpression Operand, int Line)                  : IExpression;
public sealed record BinaryExpression(IExpression Left, TokenType Op, IExpression Right, int Line) : IExpression;

// Annotation == null → infer element type from elements; must have elements.
// Annotation != null → element type declared; elements (if any) must agree.
public sealed record SeriesLiteral(IReadOnlyList<IExpression> Elements, CufetType? Annotation, int Line) : IExpression;

// Index == null → last element; Target is typically VariableReference but can be any expression
// (e.g., nested ordinal access for chained 'the first of the first of s').
public sealed record SeriesAccess(IExpression Target, IExpression? Index, int Line) : IExpression;

// the number of <series>
public sealed record SeriesLength(string SeriesName) : IExpression;

// Add X to series (append: AfterIndex=null, ToStart=false)
// Add X to the start (prepend: AfterIndex=null, ToStart=true)
// Add X after position (insert: AfterIndex=expr, ToStart=false)
public sealed record SeriesAddStatement(
    IExpression Value,
    string SeriesName,
    IExpression? AfterIndex,
    bool ToStart,
    int Line
) : IStatement;

// Remove by position (Index == null → last)
public sealed record SeriesRemoveAtStatement(string SeriesName, IExpression? Index, int Line) : IStatement;

// Remove first occurrence by value
public sealed record SeriesRemoveValueStatement(string SeriesName, IExpression Value, int Line) : IStatement;

// Element assignment (Index == null → last)
public sealed record SeriesSetStatement(
    string SeriesName,
    IExpression? Index,
    IExpression Value,
    int Line
) : IStatement;

// Record literal: a record with (positional, ..., the name value, ...)
// PositionalFields come first, NamedFields come after (enforced by parser).
public sealed record RecordLiteral(
    IReadOnlyList<IExpression> PositionalFields,
    IReadOnlyList<(string Name, IExpression Value)> NamedFields,
    int Line
) : IExpression;

// the <name> of <record-expr>  — named field access; chains: the city of the home of person
public sealed record RecordNamedAccess(string FieldName, IExpression Record, int Line) : IExpression;

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
    IReadOnlyList<IStatement> Body,
    int Line
) : IStatement;

// Bind <ReturnType|void> to <Name>[, given (<Type Name>, ...)]:
//   ...body...
// Done.
// ReturnType == null means void.
public sealed record BindStatement(
    string Name,
    CufetType? ReturnType,
    IReadOnlyList<(CufetType Type, string Name)> Parameters,
    IReadOnlyList<IStatement> Body,
    int Line
) : IStatement;

// Cast <expr> on (<args>) — function may be a name, a variable holding a function, etc.
// As expression: value is the return value of the function.
public sealed record CastExpression(
    IExpression Function,
    IReadOnlyList<IExpression> Args,
    int Line
) : IExpression;

// Cast as a statement (void call, or discarded return value).
public sealed record CastStatement(
    IExpression Function,
    IReadOnlyList<IExpression> Args,
    int Line
) : IStatement;

// return <value>.  or  return.  (bare, for void early exit)
// Value == null means bare return.
public sealed record ReturnStatement(IExpression? Value, int Line) : IStatement;

public sealed record Program(IReadOnlyList<IStatement> Statements);
