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
// Permanent: true when declared with the trailing 'permanently' adverb — the binding
// (not its contents) can never be reassigned with 'becomes'.
// Shadow: true when declared with the leading 'a shadow' modifier — explicitly shadows an
// outer binding of the same name. Without this flag, shadowing an outer name is a static error.
public sealed record DefineStatement(string Name, IExpression Value, bool Permanent, bool Shadow, int Line) : IStatement;
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
// UntoType != null means this Bind is a method of that object type, declared outside its
// body (`Bind ... unto <type>: ...`) — identical to a nested method in every way except
// declaration location.
public sealed record BindStatement(
    string Name,
    CufetType? ReturnType,
    IReadOnlyList<(CufetType Type, string Name)> Parameters,
    IReadOnlyList<IStatement> Body,
    string? UntoType,
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

// "95" converted to number — text → voidable number; void if the text isn't a clean number literal
public sealed record NumberConvert(IExpression Value, int Line) : IExpression;

// the length of greeting — character count of a text value; result is number
public sealed record TextLength(IExpression Target, int Line) : IExpression;

// ── Text operations (Slice 2: split, contains, find, substring) ───────────────

// <text> split by <delimiter> — series of text; empty delimiter is an error,
// delimiter-not-found yields a single-element series, empty pieces are kept.
public sealed record TextSplit(IExpression Text, IExpression Delimiter, int Line) : IExpression;

// <text> contains <substring> — fact
public sealed record TextContains(IExpression Text, IExpression Substring, int Line) : IExpression;

// the position of <substring> in <text> — voidable number; 1-based, first occurrence, void if absent
public sealed record TextFind(IExpression Substring, IExpression Text, int Line) : IExpression;

// the characters from <From> to <To> of <text> — 1-based inclusive; To == null means "to the end".
// Out-of-range-high clamps; To < From yields "". Always returns plain text (never voidable).
public sealed record TextSubstringRange(IExpression Text, IExpression From, IExpression? To, int Line) : IExpression;

// the first/last <Count> characters of <text> — 1-based count from either edge; clamps to the
// text's length; Count <= 0 yields "".
public sealed record TextSubstringEdge(IExpression Text, IExpression Count, bool FromStart, int Line) : IExpression;

// ── Text operations (Slice 3: replace, case, trim) ─────────────────────────────

// replace <Old> with <New> in <Text> — replaces all occurrences; empty Old is an error;
// empty New is deletion; Old not found returns Text unchanged.
public sealed record TextReplace(IExpression Text, IExpression Old, IExpression New, int Line) : IExpression;

// <text> in uppercase / <text> in lowercase — invariant (culture-independent) case conversion
public sealed record TextCase(IExpression Text, bool Uppercase, int Line) : IExpression;

// <text> trimmed — strips standard whitespace from both ends
public sealed record TextTrim(IExpression Text, int Line) : IExpression;

// ── Sort ──────────────────────────────────────────────────────────────────────

// <series> sorted                       — natural ascending order
// <series> sorted in reverse            — natural descending order
// <series> sorted by <field>            — ascending by named field (records/objects)
// <series> sorted by <field> in reverse — descending by named field
// Returns a new series (non-mutating). Delegates to host stable sort.
public sealed record SortExpression(
    IExpression Series,
    string?     ByField,  // null = natural order; non-null = sort by this named field
    bool        Reverse,
    int         Line
) : IExpression;

// ── Range (Slice 1 + Slice 2: stepping) ────────────────────────────────────────

// range <start> to <end> [counting by <step>] — materializes an inclusive series of number;
// descending when start > end; single-element when start == end. Step is a positive magnitude
// (direction always comes from start-vs-end); null Step means step 1.
public sealed record RangeExpression(IExpression Start, IExpression End, IExpression? Step, int Line) : IExpression;

// ── Void / Voidable ───────────────────────────────────────────────────────────

// The literal value void — the absent case of any voidable T.
// Used in: Define x as void. / x becomes void. / If x is not void: ...
public sealed record VoidLiteral(int Line) : IExpression;

// <voidable-expr> but void is <default-expr>
// Produces plain T: returns the value if present, otherwise the default.
public sealed record ButVoidDefault(IExpression Voidable, IExpression Default, int Line) : IExpression;

// alice's age becomes X  /  one's age becomes X  — possessive field mutation
public sealed record PossessiveSetStatement(IExpression Target, string Member, IExpression Value, int Line) : IStatement;

// ── Failures (recoverable errors as values) ────────────────────────────────────

// a failure "message" [of category "tag"] — a recoverable-problem value. Category null = no tag.
public sealed record FailureLiteral(IExpression Message, IExpression? Category, int Line) : IExpression;

// <fallible-expr> but on failure <default-expr>
// Produces plain T: the value on success, the default on failure. Mirrors ButVoidDefault.
public sealed record FailureFallback(IExpression Fallible, IExpression Default, int Line) : IExpression;

// <fallible-expr> or pass the failure off
// On failure, returns the failure from the enclosing function immediately (requires the
// enclosing function to itself be fallible). On success, yields the plain value.
public sealed record FailurePropagate(IExpression Fallible, int Line) : IExpression;

// Try to: <Body> Done.
//   [In case of failure: <FailureHandler> Done.]        — optional, null if absent
//   [In case of exception (the exception): <ExceptionHandler> Done.] — optional, null if absent
// At least one handler must be present (enforced by TypeChecker, not Parser).
// Failure and exception paths are independent — failures go to FailureHandler only,
// runtime exceptions go to ExceptionHandler only.
public sealed record TryStatement(
    IReadOnlyList<IStatement> Body,
    IReadOnlyList<IStatement>? FailureHandler,    // null = no failure handler
    IReadOnlyList<IStatement>? ExceptionHandler,  // null = no exception handler
    int Line
) : IStatement;

// Suppress the exception.
// Valid only inside an 'In case of exception' handler block (static error elsewhere).
// Causes the exception to be swallowed — execution continues after the Try statement
// rather than re-raising the exception. Without this, exceptions re-raise by default.
public sealed record SuppressStatement(int Line) : IStatement;

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

// ── I/O ───────────────────────────────────────────────────────────────────────

public enum ReadForm { Line, All, AllLines }

// read a line from <stream>       → voidable text (void at end-of-stream; trailing newline stripped)
// read all from <stream>          → text (drains remaining content; empty → "")
// read all lines from <stream>    → series of text (drains and splits; empty → empty series)
// Source is any expression of type readable stream of text.
// 'the input' is a pre-defined always-open readable stream of text (stdin).
public sealed record ReadExpression(ReadForm Form, IExpression Source, int Line) : IExpression;

public enum FileReadForm { All, AllLines }

// read all from the file "<path>"        → text or failure
// read all lines from the file "<path>"  → series of text or failure
// Path is a text expression (string literal or variable). Failure on not-found / permission / disk-error.
// Whole-file reads return the full contents or a failure — no void (no EOF-absence to express here).
public sealed record FileReadExpression(FileReadForm Form, IExpression Path, int Line) : IExpression;

// write <value> to the file "<path>"   — overwrite (creates if absent); Append = false
// append <value> to the file "<path>"  — append   (creates if absent); Append = true
// Statements: complete on success; throw FailureUnwind on IO failure (catchable by Try/In case of failure).
public sealed record FileWriteStatement(bool Append, IExpression Value, IExpression Path, int Line) : IStatement;

public enum OpenMode { Reading, Writing }

// With the file "<path>" open for reading as <name>: ... Done.
// With the file "<path>" open for writing as <name>: ... Done.
// Opens the file, binds the stream to <name> (scoped to the block), then closes
// it automatically at block-exit — guaranteed on every exit path (normal, failure, exception).
// An open failure propagates as a Cufet failure to the enclosing handler.
public sealed record WithOpenStatement(
    OpenMode Mode,
    IExpression Path,
    string BindingName,
    IReadOnlyList<IStatement> Body,
    int Line
) : IStatement;

// write <value> to <stream> — writes text to a writable stream incrementally (no newline added).
// Failures (disk full, etc.) propagate as Cufet failures.
public sealed record WriteToStreamStatement(IExpression Value, IExpression Stream, int Line) : IStatement;

// run <program> [with arguments (<arg1>, <arg2>, ...)]
// Blocks until the process exits (synchronous). Returns a record or failure.
// Launch failure (executable not found, permission denied) → Cufet failure.
// Process ran but exited nonzero → normal result (check exit-code field).
// Args is empty when no 'with arguments' clause is present.
public sealed record RunExpression(IExpression Program, IReadOnlyList<IExpression> Args, int Line) : IExpression;

// With a rabbit <name>: ... Done.
// Creates a named block-scoped region (arena). Reference-typed values created in the block
// live in the rabbit's region; freed at Done. The rabbit may be passed DOWN to callees as a
// parameter but may never be returned (downward-only rule, enforced statically).
// In the interpreter (GC-backed) the region is semantic — values become unreachable at Done.
public sealed record WithRabbitStatement(
    string Name,
    IReadOnlyList<IStatement> Body,
    int Line
) : IStatement;

// Pull a book on <name>.                  — binds the book under <name>
// Pull a book on <name> as [the] <local>. — binds the book under <local>
// Books are singleton capability bags; Pull just introduces a scope-local alias.
public sealed record PullStatement(
    string BookName,   // the canonical name of the book (e.g. "math")
    string LocalName,  // the scope-binding name (default = BookName)
    int Line
) : IStatement;

public sealed record Program(IReadOnlyList<IStatement> Statements);
