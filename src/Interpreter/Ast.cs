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

// the <name> of <record-expr> becomes <value>  — named field assignment
public sealed record RecordNamedSetStatement(
    string FieldName,
    IExpression Record,
    IExpression Value,
    int Line
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
// Series is any expression that evaluates to a series (variable ref, range, literal, etc.)
public sealed record ForEachStatement(
    string? IteratorName,
    IExpression Series,
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

// ── Objects ───────────────────────────────────────────────────────────────────

// Define <name> as an interface for { <method-sig>, ... } / single method without {}
// Methods hold the full signature (return type + param types); no implementation.
public sealed record InterfaceDefinition(
    string Name,
    IReadOnlyList<(string MethodName, CufetType? ReturnType, IReadOnlyList<CufetType> ParamTypes)> Methods,
    int Line
) : IStatement;

// Define object <name> with (<fields>) [and as a <type>] [and <interface> ...] [: <methods> Done.]
// EmbeddedTypeName != null → embedding (Slice 4); null = no embed.
// ConformedInterfaces — interface names declared with "and <interface>" clauses (Slice 5).
// Methods == [] when defined without a body.
public sealed record ObjectDefinition(
    string Name,
    IReadOnlyList<CufetType> PositionalTypes,
    IReadOnlyList<(string FieldName, CufetType FieldType)> NamedFields,
    IReadOnlyList<BindStatement> Methods,
    string? EmbeddedTypeName,
    IReadOnlyList<string> ConformedInterfaces,
    int Line
) : IStatement;

// a new <TypeName> {<fields>}
public sealed record ObjectLiteral(
    string TypeName,
    IReadOnlyList<IExpression> PositionalValues,
    IReadOnlyList<(string Name, IExpression Value)> NamedValues,
    int Line
) : IExpression;

// alice's greet  /  one's name  — possessive field or method reference
public sealed record PossessiveAccess(IExpression Target, string Member, int Line) : IExpression;

// ── Text operations (Slice 1) ─────────────────────────────────────────────────

// "hello" joined to " world" — text concatenation; both sides must be text
public sealed record TextJoin(IExpression Left, IExpression Right, int Line) : IExpression;

// score converted to text — explicit value → text (number, fact, or text no-op)
public sealed record TextConvert(IExpression Value, int Line) : IExpression;

// the length of greeting — character count of a text value; result is number
public sealed record TextLength(IExpression Target, int Line) : IExpression;

// ── Range (Slice 1) ───────────────────────────────────────────────────────────

// range <start> to <end> — materializes an inclusive series of number;
// descending when start > end; single-element when start == end.
public sealed record RangeExpression(IExpression Start, IExpression End, int Line) : IExpression;

// ── Void / Voidable ───────────────────────────────────────────────────────────

// The literal value void — the absent case of any voidable T.
// Used in: Define x as void. / x becomes void. / If x is not void: ...
public sealed record VoidLiteral(int Line) : IExpression;

// <voidable-expr> but void is <default-expr>
// Produces plain T: returns the value if present, otherwise the default.
public sealed record ButVoidDefault(IExpression Voidable, IExpression Default, int Line) : IExpression;

// alice's age becomes X  /  one's age becomes X  — possessive field mutation
public sealed record PossessiveSetStatement(IExpression Target, string Member, IExpression Value, int Line) : IStatement;

// ── Maps ──────────────────────────────────────────────────────────────────────

// a map with ("k":v, ...) — populated; KeyType/ValueType null (infer from pairs)
// a new map from K to V   — empty; KeyType/ValueType explicit; Pairs is empty
public sealed record MapLiteral(
    CufetType? KeyType,
    CufetType? ValueType,
    IReadOnlyList<(IExpression Key, IExpression Value)> Pairs,
    int Line
) : IExpression;

// the entry for <key> in <map>  →  voidable V (void when key absent)
public sealed record MapLookup(IExpression Map, IExpression Key, int Line) : IExpression;

// map has a key for <key>   →  fact (true when the key is present)
public sealed record MapHasKey(IExpression Map, IExpression Key, int Line) : IExpression;

// map has an entry for <key>  →  fact (alias for HasKey this slice)
public sealed record MapHasEntry(IExpression Map, IExpression Key, int Line) : IExpression;

// the size of <map>  →  number (entry count)
public sealed record MapSize(IExpression Map, int Line) : IExpression;

// in <map>, the entry for <key> becomes <value>.
public sealed record MapSetStatement(IExpression Map, IExpression Key, IExpression Value, int Line) : IStatement;

// a function given (<params>): <body> — anonymous function literal; return type inferred from body.
// Body is inline (single stmt) or block (Done.-terminated); parsed by ParseLambdaBody.
public sealed record LambdaLiteral(
    IReadOnlyList<(CufetType Type, string Name)> Parameters,
    IReadOnlyList<IStatement> Body,
    int Line
) : IExpression;

public sealed record Program(IReadOnlyList<IStatement> Statements);
