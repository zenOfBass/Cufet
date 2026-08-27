using Cufet.Lexer;

namespace Cufet.Interpreter;

public interface IExpression { }
public interface IStatement  { }

public sealed record NumberLiteral(decimal Value) : IExpression;

// A bit pattern, deliberately not a quantity: 0o755 is three permission triples, not "seven
// hundred fifty-five". Base is the display base ('x', 'b' or 'o') — a bits value shows itself
// in the base it was written in. Width is how many bits the literal's digits spelled out, so
// 0x0F is 8 bits where 0xF is 4; that is what lets `not 0xFF` be 0x00 with no signed reading
// and no negative numbers anywhere.
public sealed record BitsLiteral(ulong Value, char Base, int Width, int Line, int Column) : IExpression;

// <bits> shifted left|right by <number>. The Amount is a NUMBER, not bits: it counts positions,
// which is a quantity, the way the 3 in "item 3 of s" is. Left shifts widen so nothing is lost;
// right shifts discard the low bits, which is what a right shift IS rather than a failure of
// representation.
// `<bits> at <n> bits` — the same value carried at a STATED width.
//
// A width is otherwise only ever raised to fit the value, so leading zeros no operand held could
// not be produced: `0b0 shifted left by 2` is `0b0`, not `0b000`. This is what makes a width a
// thing a program can choose. `0b0 at 3 bits` is also how you spell "three zero bits" — no
// separate literal form is needed.
//
// Widening always works. Narrowing is refused when it would drop a set bit, because a bit-packer
// that silently loses its high bits writes a file that decodes to garbage.
public sealed record BitsAtWidth(IExpression Target, IExpression Width, int Line, int Column) : IExpression;

public sealed record BitsShift(IExpression Target, bool Left, IExpression Amount, int Line, int Column) : IExpression;

// <number> converted to hex|binary|octal. ToBase is 'x', 'b' or 'o'. The crossing from quantity
// to pattern is explicit in both directions — there is no implicit conversion — and this is
// also what gives back the expressiveness a display-only transform would have had: a COMPUTED
// value can be shown in hex, not just a literal.
public sealed record BitsConvert(IExpression Target, char ToBase, int Line, int Column) : IExpression;

public sealed record StringLiteral(string Value) : IExpression;
public sealed record BooleanLiteral(bool Value, int Line, int Column) : IExpression;
public sealed record VariableReference(string Name, int Line, int Column) : IExpression;
public sealed record UnaryExpression(TokenType Op, IExpression Operand, int Line, int Column) : IExpression;
public sealed record BinaryExpression(IExpression Left, TokenType Op, IExpression Right, int Line, int Column) : IExpression;

// Annotation == null → infer element type from elements; must have elements.
// Annotation != null → element type declared; elements (if any) must agree.
public sealed record SeriesLiteral(IReadOnlyList<IExpression> Elements, CufetType? Annotation, int Line, int Column) : IExpression;

// Index == null → last element; Target is typically VariableReference but can be any expression
// (e.g., nested ordinal access for chained 'the first of the first of s').
public sealed record SeriesAccess(IExpression Target, IExpression? Index, int Line, int Column) : IExpression;

// the number of <series-expr>
public sealed record SeriesLength(IExpression Series, int Line, int Column) : IExpression;

// Add X to series (append: AfterIndex=null, ToStart=false)
// Add X to the start (prepend: AfterIndex=null, ToStart=true)
// Add X after position (insert: AfterIndex=expr, ToStart=false)
public sealed record SeriesInsertStatement(
    IExpression Value,
    IExpression Series,
    IExpression? AfterIndex,
    bool ToStart,
    int Line,
    int Column
) : IStatement
{
    // ESC.1 — set by the checker when the stored value's region depth is DEEPER than the
    // destination's (the value would be freed by an inner rabbit's Done. while the destination
    // still refers to it). Holds the destination's rabbit depth; the compiler copies the value
    // into that depth's arena before storing. Null ⇒ no escape, no copy.
    public int? EscapeToDepth { get; set; }
}

// Remove by position (Index == null → last)
public sealed record SeriesRemoveAtStatement(IExpression Series, IExpression? Index, int Line, int Column) : IStatement;

// Remove first occurrence by value
public sealed record SeriesRemoveValueStatement(IExpression Series, IExpression Value, int Line, int Column) : IStatement;

// Element assignment (Index == null → last)
public sealed record SeriesSetStatement(
    IExpression Series,
    IExpression? Index,
    IExpression Value,
    int Line,
    int Column
) : IStatement;

// Record literal: a record with (positional, ..., the name value, ...)
// PositionalFields come first, NamedFields come after (enforced by parser).
public sealed record RecordLiteral(
    IReadOnlyList<IExpression> PositionalFields,
    IReadOnlyList<(string Name, IExpression Value)> NamedFields,
    int Line,
    int Column
) : IExpression;

// the <name> of <record-expr>  — named field access; chains: the city of the home of person
public sealed record RecordNamedAccess(string FieldName, IExpression Record, int Line, int Column) : IExpression;

// the <name> of <record-expr> becomes <value>  — named field assignment
public sealed record RecordNamedSetStatement(
    string FieldName,
    IExpression Record,
    IExpression Value,
    int Line,
    int Column
) : IStatement
{
    // ESC.1 — set by the checker when the stored value's region depth is DEEPER than the
    // destination's (the value would be freed by an inner rabbit's Done. while the destination
    // still refers to it). Holds the destination's rabbit depth; the compiler copies the value
    // into that depth's arena before storing. Null ⇒ no escape, no copy.
    public int? EscapeToDepth { get; set; }
}

public sealed record StateStatement(IExpression Value) : IStatement;
// Permanent: true when declared with the trailing 'permanently' adverb — the binding
// (not its contents) can never be reassigned with 'becomes'.
// Shadow: true when declared with the leading 'a shadow' modifier — explicitly shadows an
// outer binding of the same name. Without this flag, shadowing an outer name is a static error.
// DeclaredType is set by the explicit form `Define the <type> <name> as <value>.` — the same
// `the <type> <name>` shape parameters and object fields use. The value widens into it, so the
// binding can hold a wider type than any one value spells (a union is the case that needs it).
// Null ⇒ the plain form, where the type is whatever the value is.
public sealed record DefineStatement(string Name, IExpression Value, bool Permanent, bool Shadow, int Line, int Column, CufetType? DeclaredType = null) : IStatement
{
    /// <summary>Whether this `Define` was written with a `, given (…)` clause.</summary>
    /// <remarks>
    /// ⚠ Recorded rather than inferred from the value, because the point is to REFUSE it on
    /// anything but an axiom. The parameters themselves ride on the AxiomLiteral; this is the flag
    /// that lets `Define x, given (the number n), as 5.` be told apart from `Define x as 5.` after
    /// the clause has been parsed and discarded.
    /// </remarks>
    public bool HasParameterClause { get; init; }
}
public sealed record BecomesStatement(string Name, IExpression Value, int Line, int Column) : IStatement
{
    // ESC.1 — set by the checker when the stored value's region depth is DEEPER than the
    // destination's (the value would be freed by an inner rabbit's Done. while the destination
    // still refers to it). Holds the destination's rabbit depth; the compiler copies the value
    // into that depth's arena before storing. Null ⇒ no escape, no copy.
    public int? EscapeToDepth { get; set; }
}

public sealed record ConditionArm(IExpression Condition, IReadOnlyList<IStatement> Body);
public sealed record IfStatement(
    IReadOnlyList<ConditionArm> Arms,
    IReadOnlyList<IStatement>? ElseBody
) : IStatement;

// One arm of a Judge. `Cases` holds every type the arm matches — `An add-node or a mul-node`
// gives two — because grouping is how the common use of C-style fall-through is served.
public sealed record JudgeArm(
    IReadOnlyList<CufetType> Cases,
    IReadOnlyList<IStatement> Body,
    int Line,
    int Column
);

// Judge <subject>, where it is:
//     A num-node, ...
//     An add-node or a mul-node: ... Done.
//     Otherwise, ...
// Done.
//
// The subject is bound to `it` for the whole block and NARROWED inside each arm to that arm's
// case. Binding to a name is what lets the subject be an arbitrary expression: narrowing is
// variable-level, so `Judge the entry for "alice" in ages, where it is:` narrows where a bare
// `If` on the same expression could not.
//
// OtherwiseBody == null means no Otherwise arm was written. That is legal only when the subject
// is a CLOSED UNION and the arms cover every case — coverage is then proved rather than
// defaulted. For any other subject the checker requires an Otherwise, so control can never fall
// off the end of a Judge.
public sealed record JudgeStatement(
    IExpression Subject,
    IReadOnlyList<JudgeArm> Arms,
    IReadOnlyList<IStatement>? OtherwiseBody,
    int Line,
    int Column
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
    int Line,
    int Column
) : IStatement;

// Bind <ReturnType|void> to <Name>[, given (<Type Name>, ...)]:
//   ...body...
// Done.
// ReturnType == null means void.
// UntoType != null means this Bind is a method of that object type, declared outside its
// body (`Bind ... unto <type>: ...`) — identical to a nested method in every way except
// declaration location.
// ConstructsTypeName != null means this Bind is a named constructor for that type
// (`Bind making a <type> to <name>, given (...)`) — a free function that builds and returns
// an instance of <type>, registered on the type's Constructors list.
public sealed record BindStatement(
    string Name,
    CufetType? ReturnType,
    IReadOnlyList<(CufetType Type, string Name)> Parameters,
    IReadOnlyList<IStatement> Body,
    string? UntoType,
    string? ConstructsTypeName,
    int Line,
    int Column
) : IStatement
{
    /// <summary>Whether this function was written as a runnable `cufet` axiom.</summary>
    /// <remarks>
    /// ★ A cufet axiom that says what it gives back IS a function, and the parser lowers it to one
    /// — which is what gives it everything a function has (called with `cast`, held as a value,
    /// passed, stored) without either backend learning that cufet axioms exist.
    ///
    /// ⚠ One thing does not survive that lowering: the requirement that the language's book be
    /// pulled. Every other axiom is checked for it through the literal it is the value of, and
    /// after lowering there is no literal left. So the fact rides here, and the checker asks the
    /// same RequireLanguagePulled everything else does — one rule, not a second one for this shape.
    /// </remarks>
    public bool FromCufetAxiom { get; init; }
}

// Bind overloading <Op>, given (the <LeftName> is a <OperandTypeName>, the <RightName> is a <OperandTypeName>): ... Done.
// Declares the behaviour of <Op> for two same-type <OperandTypeName> operands.
// No name — invoked by writing the operator. Same-type binary only.
// Fallible when the body contains 'return a failure'; the type checker infers this and
// makes `a <op> b` a fallible expression (FailureType(T)) subject to the strict-fallible rule.
public sealed record OperatorOverloadDeclaration(
    TokenType Operator,       // Plus, Minus, Star, or Slash
    string LeftName,          // parameter name for the left operand
    string RightName,         // parameter name for the right operand
    string OperandTypeName,   // type name string (both operands; resolved in TypeChecker)
    IReadOnlyList<IStatement> Body,
    int Line,
    int Column
) : IStatement;

// Bind unmaking a <UnmakesTypeName> to <Name>: ... Done.
// Declares the destructor for <UnmakesTypeName>. Exactly one per type. No parameters.
// Body accesses 'one's <fields>' to release owned resources.
// Infallible — no 'return a failure'; enforced at type-check time.
public sealed record UnmakerDeclaration(
    string Name,
    string UnmakesTypeName,
    IReadOnlyList<IStatement> Body,
    int Line,
    int Column
) : IStatement;

// Cast <expr> on (<args>) — function may be a name, a variable holding a function, etc.
// As expression: value is the return value of the function.
public sealed record CastExpression(
    IExpression Function,
    IReadOnlyList<IExpression> Args,
    int Line,
    int Column
) : IExpression
{
    // The filled-in function this call actually reaches — `unique of text` for `cast unique on
    // (names)` where names is a series of text. A side channel written by the checker, the same way
    // ObjectLiteral.ResolvedTypeName is, because both backends resolve a named call BY NAME and a
    // function with a blank in its signature has no single body to reach. Null for ordinary calls.
    public string? ResolvedFunctionName { get; set; }

    /// <summary>The axiom this call RUNS, when the name being cast is one.</summary>
    /// <remarks>
    /// ★ The same side channel `ReturnStatement.RunsAxiom` is, and for the same reason: the checker
    /// has already resolved the name to its source, so neither backend looks it up again. What
    /// comes back is decided by the type declared where the call is USED — see RunAxiomOnCast.
    /// </remarks>
    public AxiomLiteral? RunsAxiom { get; set; }

    /// <summary>True when this call runs an axiom that arrived as a VALUE, with no literal behind it.</summary>
    /// <remarks>
    /// ★ Separate from <see cref="RunsAxiom"/> rather than folded into it, because the two carry
    /// opposite information. RunsAxiom names the source to PASTE; this says there is no source to
    /// paste and the callee has to be called through its value — a C function pointer compiled, the
    /// held literal interpreted. A single nullable field could not tell "no axiom here" apart from
    /// "an axiom whose text I do not have".
    /// </remarks>
    public bool RunsAxiomValue { get; set; }
}

// Cast as a statement (void call, or discarded return value).
public sealed record CastStatement(
    IExpression Function,
    IReadOnlyList<IExpression> Args,
    int Line,
    int Column
) : IStatement
{
    /// <inheritdoc cref="CastExpression.ResolvedFunctionName"/>
    public string? ResolvedFunctionName { get; set; }

    /// <inheritdoc cref="CastExpression.RunsAxiom"/>
    /// <remarks>
    /// ★ An axiom called for its EFFECT rather than its answer — `Cast close-dir on (handle).`
    /// The result is discarded, which is what a statement means; the declaration still has to say
    /// what it gives back, because the C wrapper's return type is built from it.
    /// </remarks>
    public AxiomLiteral? RunsAxiom { get; set; }

    /// <inheritdoc cref="CastExpression.RunsAxiomValue"/>
    public bool RunsAxiomValue { get; set; }
}

// return <value>.  or  return.  (bare, for void early exit)
// Value == null means bare return.
public sealed record ReturnStatement(IExpression? Value, int Line, int Column) : IStatement
{
    /// <summary>The axiom this return RUNS, when the declared return type says it should.</summary>
    /// <remarks>
    /// ★ An axiom runs when it is returned, and the declared type decides — a `Bind number to …`
    /// whose value is an axiom runs it and marshals the result, where a `Bind` whose declared type
    /// IS an axiom hands it back unrun. The checker resolves which, and puts the axiom itself here
    /// so neither backend has to look the name up again. Null on every other return.
    /// </remarks>
    public AxiomLiteral? RunsAxiom { get; set; }
}

// ── Foreign source ────────────────────────────────────────────────────────────

// [ ... ] — an AXIOM: source in another language, taken as given.
//
// ★ It names the CONTRACT, not the appearance. An axiom is accepted without proof, which is
// exactly what this is — Cufet cannot check a C listing and cannot prove anything about it.
//
// Language is filled in by the checker from the declaration (`Define c-language get-pid as […]`),
// because the brackets say "this is verbatim foreign text" and cannot say WHICH language; the tag
// names the consumer. An axiom with no tag to take is refused.
public sealed record AxiomLiteral(string Source, int Line, int Column) : IExpression
{
    public string? Language { get; set; }

    /// <summary>What the axiom takes, declared `given (…)` on the line that names it.</summary>
    /// <remarks>
    /// ★ Declared the way every Cufet function declares parameters, and referred to inside the
    /// foreign text BY THE ARTICLE — `the text path` above, `the path` in the body. That marker
    /// works because `the path` is never valid C or SQL: it is English sitting in code that is not
    /// English, so nothing has to be escaped.
    ///
    /// ★ The list is also what marshalling needs. Nothing else says what C types the arguments
    /// have, and the writer never names a C type anywhere.
    ///
    /// ⚠ Only VALUES cross, never text — the precedent is `Run "grep" with arguments (…)`, which
    /// passes a list rather than a concatenated command line, which is why there is no injection
    /// there. An axiom is fixed at its definition and cannot be assembled from strings.
    /// </remarks>
    public IReadOnlyList<(CufetType Type, string Name)> Parameters { get; set; } = [];

    /// <summary>What running it gives back — see AxiomType.ReturnType for why it is declared.</summary>
    public CufetType? ReturnType { get; set; }

    /// <summary>The axiom that frees what this one hands back — `and free it with close-dir`.</summary>
    /// <remarks>
    /// ★ The name as written; the checker resolves it onto <see cref="ReleaseAxiom"/>. It is a
    /// property of the ACQUIRING axiom because nothing else can carry it: `getenv` and `strdup`
    /// hand back the same C type with opposite obligations, and Cufet never reads the foreign text,
    /// so the person who wrote it is the only possible source of this fact.
    ///
    /// ⚠ **Null means free nothing.** A leak is recoverable and visible; a double free is
    /// corruption that surfaces somewhere else entirely. Guardrails fail toward the recoverable side.
    /// </remarks>
    public string? ReleasedBy { get; set; }

    /// <summary>The resolved release axiom, filled in by the checker.</summary>
    public AxiomLiteral? ReleaseAxiom { get; set; }
}

/// <summary>`the text at &lt;address&gt;` — reading through a foreign pointer.</summary>
/// <remarks>
/// <para>
/// ★★ **The only read there is.** Reading a struct or a scalar through an address was considered
/// and is unnecessary: an axiom can project a field (`[readdir(the dir)-&gt;d_name]`) or declare a
/// local and hand it back, so those values return the ordinary way. Text is the one case with no
/// single-expression answer on the C side, because the bytes belong to C and have to be copied out.
/// </para>
/// <para>
/// ★ It ALWAYS COPIES, into the arena on one backend and into a managed string on the other, and
/// never yields a view into foreign memory. That is what keeps an address inert: holding one is
/// harmless, and reading through it is a thing visible in a diff rather than marshalling hidden in
/// a declaration.
/// </para>
/// </remarks>
public sealed record ForeignTextAt(IExpression Address, int Line, int Column) : IExpression;

// ── Cufet source, held as an axiom ─────────────────────────────────────

// Define cufet <name> as [ <cufet source> ].  — declarations, held under a name until cited.
//
// ★★ The SAME SURFACE as a foreign axiom, with a different mechanism behind it — which is the
// design as settled, not an accident of reuse. `[ … ]` says "the text inside is not the program
// around it", and that is true of a cufet block too: it is parsed, but it is not PLACED until a
// `Cite` says where. What differs is everything past parsing — nothing is marshalled, no boundary
// is crossed, and no compiler but this one ever reads it.
//
// ★ A statement of its own rather than a `DefineStatement` holding an `AxiomLiteral`, and that is
// what keeps the foreign machinery away from it by construction: every collector that gathers
// axioms to prepare, to paste, or to wrap asks for that exact shape, so none of them can meet this
// one by accident. There is no `if the language is cufet` anywhere in either backend.
//
// ⚠ Front-end only — CiteExpansion removes it before either backend meets a program, the same
// rule that lets no template and no `stash of T` survive the checker.
public sealed record CufetAxiomDefinition(
    string Name,
    IReadOnlyList<IStatement> Body,
    int Line,
    int Column
) : IStatement
{
    /// <summary>Whether this block was written with a `, given (…)` clause.</summary>
    /// <remarks>
    /// ⚠ Carried rather than refused at the parse, so the refusal can come from the pass that owns
    /// blocks and can say it in the language's own voice. A `Cite` of the name would otherwise
    /// report first — "there is no cufet source called 'shape'" — which is true and useless, because
    /// the source is right there and the clause is the problem.
    /// </remarks>
    public bool HasParameterClause { get; init; }
}

// Cite <name>.  — place the declarations a cufet axiom holds, here.
//
// ★ A STATEMENT, not an expression. Citing yields no value and happens before the program runs;
// spelling it `cast` would have promised both.
public sealed record CiteStatement(string Name, int Line, int Column) : IStatement;

// Bury <value>.  — hand one value out of a stash body and suspend there.
//
// ★ Its PRESENCE is what makes the enclosing function stash-producing; nothing marks the
// declaration. That is the rule the language already uses for fallibility (a body containing
// `return a failure` makes a function fallible — see BindStatement above), so requiring a marker
// here would have been a second convention for one idea.
// Have <rabbit> bury <value>.
// RabbitName is the agent doing the burying, and is always present: burying is memory work, and a
// rabbit is who does memory work. Inside a burying function it is normally a parameter — the agent
// is handed IN, which is what puts the ownership at the call site instead of leaving it ambient.
public sealed record BuryStatement(IExpression Value, int Line, int Column, string? RabbitName = null) : IStatement;

// unbury <stash>  — resume a stash to its next `Bury` and take the value, or void once spent.
// An expression, so the result is handled like any other voidable rather than through hidden state:
// `Define x as unbury s.` The alternative sketched originally had the rabbit HOLD the value in a
// temporary register until used, which is an invisible accumulator in a language that made
// narrowing explicit.
public sealed record UnburyExpression(IExpression Stash, int Line, int Column) : IExpression;

// ── Objects ───────────────────────────────────────────────────────────────────

// Define <name> as an interface for { <method-sig>, ... } / single method without {}
// Methods hold the full signature (return type + param types); no implementation.
public sealed record InterfaceDefinition(
    string Name,
    IReadOnlyList<(string MethodName, CufetType? ReturnType, IReadOnlyList<CufetType> ParamTypes)> Methods,
    int Line,
    int Column
) : IStatement;

// Get <name> as <type>: ... Done.  — computed property; body must Return the declared type.
// UntoType != null → declared outside its object's body with 'unto <type>' (same as Bind unto).
public sealed record GetterDeclaration(
    string Name,
    CufetType ReturnType,
    IReadOnlyList<IStatement> Body,
    string? UntoType,
    int Line,
    int Column
) : IStatement;

// Set <name> given (<param>): ... Done.  — intercepting write; body is void / infallible.
// The setter writes the backing field via a self-bypassing raw write in the interpreter.
// UntoType != null → declared outside its object's body with 'unto <type>'.
public sealed record SetterDeclaration(
    string Name,
    CufetType ParamType,
    string ParamName,
    IReadOnlyList<IStatement> Body,
    string? UntoType,
    int Line,
    int Column
) : IStatement;

// Define object <name> with (<fields>) [and as a <type>] [and <interface> ...] [: <members> Done.]
// EmbeddedTypeName != null → embedding (Slice 4); null = no embed.
// ConformedInterfaces — interface names declared with "and <interface>" clauses (Slice 5).
// Methods/Getters/Setters == [] when defined without a body.
public sealed record ObjectDefinition(
    string Name,
    IReadOnlyList<CufetType> PositionalTypes,
    IReadOnlyList<(string FieldName, CufetType FieldType)> NamedFields,
    IReadOnlyList<BindStatement> Methods,
    IReadOnlyList<GetterDeclaration> Getters,
    IReadOnlyList<SetterDeclaration> Setters,
    string? EmbeddedTypeName,
    IReadOnlyList<string> ConformedInterfaces,
    int Line,
    int Column,
    // Field NAMES declared `the permanently <type> <name>` — set at construction, never written
    // after. Carried as a name set beside NamedFields rather than folded into that tuple: the
    // tuple is read in 98 places across 14 files, and a name-keyed set cannot fall out of step
    // with the field list the way a parallel positional list could.
    IReadOnlyList<string>? PermanentFields = null,
    // The BLANKS this definition leaves to be filled — `Define object stack of element` names one
    // `element`. Empty for an ordinary object, which is every object written so far.
    //
    // ★ The name is the writer's to choose, and `of` is what marks it: the slot after the type's
    // own name is a declaration by position, so nothing has to be inferred and a mistyped type
    // name elsewhere stays an error rather than quietly becoming a blank.
    IReadOnlyList<string>? TypeParameters = null
) : IStatement;

// a new <TypeName> [of <type> ...] {<fields>}
// TypeArguments fill the blanks of a parameterised definition — `a new stack of number { … }`.
// Empty for an ordinary object. The checker resolves the pair to one concrete definition; the
// parser does not name it, because naming a type is the checker's job and it owns the rendering.
public sealed record ObjectLiteral(
    string TypeName,
    IReadOnlyList<IExpression> PositionalValues,
    IReadOnlyList<(string Name, IExpression Value)> NamedValues,
    int Line,
    int Column,
    IReadOnlyList<CufetType>? TypeArguments = null
) : IExpression
{
    // The filled-in definition this literal actually makes — `stack of number` for
    // `a new stack of number { … }`. A side channel written by the checker, the same way
    // IsTypeCheck.StaticTargetType is, because both backends look the definition up BY NAME and the
    // template's own name names no type. Null for an ordinary object, which is nearly all of them.
    public string? ResolvedTypeName { get; set; }
}

// alice's greet  /  one's name  — possessive field or method reference
public sealed record PossessiveAccess(IExpression Target, string Member, int Line, int Column) : IExpression;

// ── Text operations (Slice 1) ─────────────────────────────────────────────────

// "hello" joined to " world" — text concatenation; both sides must be text
public sealed record TextJoin(IExpression Left, IExpression Right, int Line, int Column) : IExpression;

// score converted to text — explicit value → text (number, fact, or text no-op)
public sealed record TextConvert(IExpression Value, int Line, int Column) : IExpression;

// "95" converted to number — text → voidable number; void if the text isn't a clean number literal
public sealed record NumberConvert(IExpression Value, int Line, int Column) : IExpression;

// the length of greeting — character count of a text value; result is number
public sealed record TextLength(IExpression Target, int Line, int Column) : IExpression;

// ── Text operations (Slice 2: split, contains, find, substring) ───────────────

// <text> split by <delimiter> — series of text; empty delimiter is an error,
// delimiter-not-found yields a single-element series, empty pieces are kept.
public sealed record TextSplit(IExpression Text, IExpression Delimiter, int Line, int Column) : IExpression;

// <text> contains <substring> — fact
public sealed record TextContains(IExpression Text, IExpression Substring, int Line, int Column) : IExpression;

// the position of <substring> in <text> — voidable number; 1-based, first occurrence, void if absent
public sealed record TextFind(IExpression Substring, IExpression Text, int Line, int Column) : IExpression;

// the characters from <From> to <To> of <text> — 1-based inclusive; To == null means "to the end".
// Out-of-range-high clamps; To < From yields "". Always returns plain text (never voidable).
public sealed record TextSubstringRange(IExpression Text, IExpression From, IExpression? To, int Line, int Column) : IExpression;

// the first/last <Count> characters of <text> — 1-based count from either edge; clamps to the
// text's length; Count <= 0 yields "".
public sealed record TextSubstringEdge(IExpression Text, IExpression Count, bool FromStart, int Line, int Column) : IExpression;

// ── Text operations (Slice 3: replace, case, trim) ─────────────────────────────

// replace <Old> with <New> in <Text> — replaces all occurrences; empty Old is an error;
// empty New is deletion; Old not found returns Text unchanged.
public sealed record TextReplace(IExpression Text, IExpression Old, IExpression New, int Line, int Column) : IExpression;

// <text> in uppercase / <text> in lowercase — invariant (culture-independent) case conversion
public sealed record TextCase(IExpression Text, bool Uppercase, int Line, int Column) : IExpression;

// <text> trimmed — strips standard whitespace from both ends
public sealed record TextTrim(IExpression Text, int Line, int Column) : IExpression;

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
    int         Line,
    int         Column
) : IExpression;

// ── Range (Slice 1 + Slice 2: stepping) ────────────────────────────────────────

// range <start> to <end> [counting by <step>] — materializes an inclusive series of number;
// descending when start > end; single-element when start == end. Step is a positive magnitude
// (direction always comes from start-vs-end); null Step means step 1.
public sealed record RangeExpression(IExpression Start, IExpression End, IExpression? Step, int Line, int Column) : IExpression;

// ── Void / Voidable ───────────────────────────────────────────────────────────

// The literal value void — the absent case of any voidable T.
// Used in: Define x as void. / x becomes void. / If x is not void: ...
public sealed record VoidLiteral(int Line, int Column) : IExpression;

// <value> when <condition>, otherwise <alternative>
//
// The only way to make a value depend on a condition in expression position. Without it a
// conditional value must be declared and then mutated, which forces a MUTABLE binding — so a
// `permanently` binding could not be conditionally initialised at all. That is the hole this
// closes, and it is why this is not a second spelling of `If`.
//
// ★ Exactly one arm evaluates. The condition is evaluated first and only the chosen side after
// it, so a call or a failure in the untaken arm never happens — same as `If`, and the thing that
// makes the form safe to use with effects on either side.
//
// Field order matches reading order. `Otherwise` is reused rather than a new word, and the comma
// before it is what `If x is 1, state "one".` already does.
public sealed record ConditionalExpression(
    IExpression Value, IExpression Condition, IExpression Alternative, int Line, int Column) : IExpression;

// <voidable-expr> but void is <default-expr>
// Produces plain T: returns the value if present, otherwise the default.
public sealed record ButVoidDefault(IExpression Voidable, IExpression Default, int Line, int Column) : IExpression;

// alice's age becomes X  /  one's age becomes X  — possessive field mutation
public sealed record PossessiveSetStatement(IExpression Target, string Member, IExpression Value, int Line, int Column) : IStatement
{
    // ESC.1 — set by the checker when the stored value's region depth is DEEPER than the
    // destination's (the value would be freed by an inner rabbit's Done. while the destination
    // still refers to it). Holds the destination's rabbit depth; the compiler copies the value
    // into that depth's arena before storing. Null ⇒ no escape, no copy.
    public int? EscapeToDepth { get; set; }
}

// ── Failures (recoverable errors as values) ────────────────────────────────────

// a failure "message" [of category "tag"] — a recoverable-problem value. Category null = no tag.
public sealed record FailureLiteral(IExpression Message, IExpression? Category, int Line, int Column) : IExpression;

// <fallible-expr> but on failure <default-expr>
// Produces plain T: the value on success, the default on failure. Mirrors ButVoidDefault.
public sealed record FailureFallback(IExpression Fallible, IExpression Default, int Line, int Column) : IExpression;

// <fallible-expr> or pass the failure off
// On failure, returns the failure from the enclosing function immediately (requires the
// enclosing function to itself be fallible). On success, yields the plain value.
public sealed record FailurePropagate(IExpression Fallible, int Line, int Column) : IExpression;

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
    int Line,
    int Column
) : IStatement;

// Suppress the exception.
// Valid only inside an 'In case of exception' handler block (static error elsewhere).
// Causes the exception to be swallowed — execution continues after the Try statement
// rather than re-raising the exception. Without this, exceptions re-raise by default.
public sealed record SuppressStatement(int Line, int Column) : IStatement;

// ── Maps ──────────────────────────────────────────────────────────────────────

// a map [from K to V] with ("k":v, ...)
//   — KeyType/ValueType explicit when 'from K to V' is given; null means infer from pairs
//   — Pairs empty = empty map; Pairs non-empty = populated map
public sealed record MapLiteral(
    CufetType? KeyType,
    CufetType? ValueType,
    IReadOnlyList<(IExpression Key, IExpression Value)> Pairs,
    int Line,
    int Column
) : IExpression;

// the entry for <key> in <map>  →  voidable V (void when key absent)
public sealed record MapLookup(IExpression Map, IExpression Key, int Line, int Column) : IExpression;

// map has a key for <key>   →  fact (true when the key is present)
public sealed record MapHasKey(IExpression Map, IExpression Key, int Line, int Column) : IExpression;

// map has an entry for <key>  →  fact (alias for HasKey this slice)
public sealed record MapHasEntry(IExpression Map, IExpression Key, int Line, int Column) : IExpression;

// the size of <map>  →  number (entry count)
public sealed record MapSize(IExpression Map, int Line, int Column) : IExpression;

// in <map>, the entry for <key> becomes <value>.
public sealed record MapSetStatement(IExpression Map, IExpression Key, IExpression Value, int Line, int Column) : IStatement
{
    // ESC.1 — set by the checker when the stored value's region depth is DEEPER than the
    // destination's (the value would be freed by an inner rabbit's Done. while the destination
    // still refers to it). Holds the destination's rabbit depth; the compiler copies the value
    // into that depth's arena before storing. Null ⇒ no escape, no copy.
    public int? EscapeToDepth { get; set; }
    // A map stores its KEY as well as its value, so the key needs its own escape annotation — a
    // rabbit-scoped text key put into a longer-lived map dangles exactly like a value would. This
    // is the only store in the language with two escaping operands.
    public int? KeyEscapeToDepth { get; set; }
}

// a function given (<params>): <body> — anonymous function literal; return type inferred from body.
// Body is inline (single stmt) or block (Done.-terminated); parsed by ParseLambdaBody.
public sealed record LambdaLiteral(
    IReadOnlyList<(CufetType Type, string Name)> Parameters,
    IReadOnlyList<IStatement> Body,
    int Line,
    int Column
) : IExpression;

// ── I/O ───────────────────────────────────────────────────────────────────────

public enum ReadForm { Line, All, AllLines }

// read a line from <stream>       → voidable text (void at end-of-stream; trailing newline stripped)
// read all from <stream>          → text (drains remaining content; empty → "")
// read all lines from <stream>    → series of text (drains and splits; empty → empty series)
// Source is any expression of type readable stream of text.
// 'the input' is a pre-defined always-open readable stream of text (stdin).
public sealed record ReadExpression(ReadForm Form, IExpression Source, int Line, int Column) : IExpression;

public enum FileReadForm { All, AllLines }

// read all from the file "<path>"        → text or failure
// read all lines from the file "<path>"  → series of text or failure
// Path is a text expression (string literal or variable). Failure on not-found / permission / disk-error.
// Whole-file reads return the full contents or a failure — no void (no EOF-absence to express here).
public sealed record FileReadExpression(FileReadForm Form, IExpression Path, int Line, int Column) : IExpression;

// the environment variable <name>  →  voidable text (void when the variable is unset; name is a text expr)
public sealed record EnvironmentVariableExpression(IExpression Name, int Line, int Column) : IExpression;

// the current directory  →  voidable text. Void only in the pathological case where the process
// has no working directory to report — the directory was deleted out from under it. Voidable
// rather than plain text to match `the environment variable`, which asks the OS the same way.
public sealed record CurrentDirectoryExpression(int Line, int Column) : IExpression;

// The current directory becomes <path>.  →  a FALLIBLE statement, like `write ... to ...`:
// the path may not exist, may not be a directory, or may not be reachable.
public sealed record CurrentDirectorySetStatement(IExpression Path, int Line, int Column) : IStatement;

// ── Directory traversal ───────────────────────────────────────────────────────────────────────────

// the contents of the directory <path>  →  series of text or failure
// Returns full absolute paths of every entry (files + subdirs) directly inside the directory.
// Fails on not-found, not-a-directory, or permission-denied.
public sealed record DirectoryContentsExpression(IExpression Path, int Line, int Column) : IExpression;

// the path <path> exists         →  boolean (infallible; uncertainty resolves to false)
// the path <path> is a directory →  boolean (infallible)
// the path <path> is a file      →  boolean (infallible)
public enum PathCheckKind { Exists, IsDirectory, IsFile }
public sealed record PathCheckExpression(IExpression Path, PathCheckKind Kind, int Line, int Column) : IExpression;

// ── Signals ───────────────────────────────────────────────────────────────────────────────────────

// an interrupt is requested  →  fact (boolean, infallible)
// True when _interruptRequested is set; false otherwise.  Cooperative polling — no async.
public sealed record InterruptRequestedExpression(int Line, int Column) : IExpression;

// Acknowledge the interrupt.  →  statement; clears _interruptRequested
// Resets the interrupt flag after the program has noticed and handled it.
public sealed record AcknowledgeInterruptStatement(int Line, int Column) : IStatement;

// Yield.  →  statement; cooperative scheduler yield + interrupt checkpoint (slice 5)
// Gives up the scheduler turn (lets one other ready task run), then throws InterruptUnwind
// if the interrupt flag is set, or resumes execution here otherwise.
public sealed record YieldStatement(int Line, int Column) : IStatement;

// write <value> to the file "<path>"   — overwrite (creates if absent); Append = false
// append <value> to the file "<path>"  — append   (creates if absent); Append = true
// Statements: complete on success; throw FailureUnwind on IO failure (catchable by Try/In case of failure).
public sealed record FileWriteStatement(bool Append, IExpression Value, IExpression Path, int Line, int Column) : IStatement;

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
    int Line,
    int Column
) : IStatement;

// write <value> to <stream> — writes text to a writable stream incrementally (no newline added).
// Failures (disk full, etc.) propagate as Cufet failures.
public sealed record WriteToStreamStatement(IExpression Value, IExpression Stream, int Line, int Column) : IStatement;

// run <program> [with arguments (<arg1>, <arg2>, ...)]
// Blocks until the process exits (synchronous). Returns a record or failure.
// Launch failure (executable not found, permission denied) → Cufet failure.
// Process ran but exited nonzero → normal result (check exit-code field).
// Args is empty when no 'with arguments' clause is present.
public sealed record RunExpression(IExpression Program, IReadOnlyList<IExpression> Args, int Line, int Column) : IExpression;

// Pull a rabbit [as <name>]. ... Done.
// Opens a Done.-delimited arena scope. Reference-typed values created in the scope live in
// the rabbit's region; freed at Done. (ExitScope fires destructors.) Name is optional —
// supply it only when the rabbit needs to be passed as a parameter to a callee.
// The rabbit may be passed DOWN to callees but may never be returned (downward-only rule).
// In the interpreter (GC-backed) the region is semantic — values become unreachable at Done.
public sealed record PullRabbitStatement(
    string? Name,
    IReadOnlyList<IStatement> Body,
    int Line,
    int Column
) : IStatement;

// Have rabbit start a task [as <name>]: ... Done.
// Spawns a cooperative structured task inside the enclosing rabbit's scope.
// Semantics (slice 2):
//   • Runs on the CufetScheduler (cooperative, single-threaded, yield-point interleaving).
//   • Structured: the task body is enqueued and joins at the enclosing rabbit's Done.
//     Tasks cannot outlive their rabbit — sound by construction (shorter-lived scope,
//     existing region depth/CheckRegionStore covers outward escapes).
//   • Name (optional): binds an identity for slice-4 result-await; inert in slice 2.
// Requires an active rabbit in scope (enforced by parser + type checker).
// Have <rabbit|name> start a task [as <name>]: ... Done.
// RabbitName == null → the bare `rabbit` keyword, meaning the enclosing one.
// RabbitName != null → a rabbit addressed BY NAME. A rabbit is an agent you summon and give a job
// to, so naming one at the point you give it work is the whole reason names exist; requiring the
// keyword made every name decorative.
public sealed record LaunchTaskStatement(
    string? Name,
    IReadOnlyList<IStatement> Body,
    int Line,
    int Column,
    string? RabbitName = null
) : IStatement;

// a matrix with ((r1e1, r1e2, ...), (r2e1, r2e2, ...), ...)
// 2D numeric grid; dimensions inferred from literal; rectangularity enforced.
// Constructable only where the 'collections' book has been pulled.
public sealed record MatrixLiteral(
    IReadOnlyList<IReadOnlyList<IExpression>> Rows,
    int Line,
    int Column
) : IExpression;

// the item at (row, column) of <matrix> — 1-based; number result.
// Out-of-bounds mirrors series indexing (RuntimeException).
// The indexed column is 'Col' — 'Column' is the source position every node carries, and it
// pairs with MatrixSized's 'Cols' anyway.
public sealed record MatrixAccess(
    IExpression Matrix,
    IExpression Row,
    IExpression Col,
    int Line,
    int Column
) : IExpression;

// The item at (row, column) of <matrix> becomes <number>.
// The write half of MatrixAccess, and the only thing that mutates a matrix in place. A matrix is a
// reference type, so the write is visible through every name for that matrix — which is what the
// reference-types table in docs/GRAMMAR.md has always said it was.
public sealed record MatrixSetStatement(
    IExpression Matrix,
    IExpression Row,
    IExpression Col,
    IExpression Value,
    int Line,
    int Column
) : IStatement;

// a matrix with <Rows> by <Cols> [filled with <Fill>]
// Sized constructor: Fill == null → all cells zero; requires 'collections' pulled.
// Dimension expressions must evaluate to positive whole numbers.
public sealed record MatrixSized(IExpression Rows, IExpression Cols, IExpression? Fill, int Line, int Column) : IExpression;

// Pull a book on <name> [as <local>]. ... Done.              — single book, Done.-delimited scope
// Pull books on <n1> [as <l1>], <n2> [as <l2>], and <n3>. ... Done. — multiple books, shared scope
// Books is never empty; single-book pull = one-element list; plural = two-or-more.
// ViaBookForm — written as `Pull a book on <name>.` / `Pull books on <a>, and <b>.` rather than the
// general `Pull <name>.`. Recorded because a BUNDLED book must be pulled by the book form: `Pull
// math.` was never a decision, only something the general form swallowed on its way past.
public sealed record PullStatement(
    IReadOnlyList<(string BookName, string LocalName)> Books,  // one entry per pulled book
    IReadOnlyList<IStatement> Body,                            // statements between Pull and Done.
    int Line,
    int Column,
    bool ViaBookForm = false
) : IStatement;

// if x is a number — type test; yields fact (true when the runtime value matches the type).
// Negated: true = "is not a <type>".
// StaticTargetType — ISA.2: the type checker records the TARGET's static type here so the
// interpreter can answer type-directed (like the compiler) instead of value-directed. A runtime
// value cannot answer precisely for an EMPTY container (a bare List carries no element type), but
// the declared type always can. Null when the checker couldn't determine it → value-directed
// fallback. Set during checking; both passes share this AST object, so no pipeline change.
public sealed record IsTypeCheck(IExpression Target, CufetType Type, bool Negated, int Line, int Column) : IExpression
{
    public CufetType? StaticTargetType { get; set; }
}

// ── Chance book (randomness) ─────────────────────────────────────────────────────────────────

// a random number from <Low> to <High> — inclusive whole-number range; requires chance pulled.
// Low > High → RuntimeException (bug, not a recoverable failure).
public sealed record RandomNumber(IExpression Low, IExpression High, int Line, int Column) : IExpression;

// a random item from <Series> — uniformly random element; voidable on empty series/catalogue.
// Generic in element type (series of T → voidable T); works on catalogues.
public sealed record RandomItem(IExpression Series, int Line, int Column) : IExpression;

// randomly shuffled <Series> — returns a new series in random order; source unchanged.
// Generic in element type (series of T → series of T); works on catalogues.
public sealed record RandomlyShuffled(IExpression Series, int Line, int Column) : IExpression;

// a random guess — fact (true or false, 50/50); requires chance pulled.
public sealed record RandomGuess(int Line, int Column) : IExpression;

// Seed the chance with <Seed>. — reseeds the per-interpreter RNG for reproducibility.
// Default (no seed) = entropy-seeded (real randomness). Requires chance pulled.
public sealed record SeedChanceStatement(IExpression Seed, int Line, int Column) : IStatement;

// ── Channels (concurrency slice 3) ────────────────────────────────────────────
// a channel of T — creates a new channel; reference-typed.
public sealed record ChannelCreation(CufetType ElementType, int Line, int Column) : IExpression;
// Send <value> through <channel>. — queues value (non-blocking); deep-copies reference-types.
public sealed record SendStatement(IExpression Value, IExpression Channel, int Line, int Column) : IStatement;
// the delivery from <channel>  →  voidable T
// Non-void if value present; yields if empty-and-open; void if empty-and-closed.
public sealed record DeliveryExpression(IExpression Channel, int Line, int Column) : IExpression;
// Close <channel>. — signals done; future deliveries drain remaining values then return void.
public sealed record CloseStatement(IExpression Channel, int Line, int Column) : IStatement;

// ── Task results (concurrency slice 4) ────────────────────────────────────────
// "the awaited result of <task>" — yields until the named task completes, then
// returns its result (T / voidable T / T or failure depending on the task body).
public sealed record AwaitedResultExpression(IExpression Task, int Line, int Column) : IExpression;

// ── Pipes ──────────────────────────────────────────────────────────────────────
// A | B — streaming pipe statement (and IExpression so it nests for multi-stage).
// Two branches dispatched at runtime on operand type:
//   RunExpression operands → subprocess stdio wiring (C# Process)
//   FunctionValue operands → channel+task wiring (Cufet stages)
// Multi-stage: left-associative, (A|B)|C = PipeExpression(PipeExpression(A,B),C).
public sealed record PipeExpression(IExpression Left, IExpression Right, int Line, int Column) : IExpression, IStatement;

// output <value>. — producer statement: emit a value to the implicit output stream.
// Only meaningful inside a pipe-producer context (enforced at runtime).
// 'output' is NOT a reserved keyword — contextually recognized by shape.
public sealed record OutputStatement(IExpression Value, int Line, int Column) : IStatement;

// for each <name> from the input: <body> Done.
// Consumer loop: iterates the implicit input stream until it closes.
// 'input' is NOT reserved — contextually recognized in this shape.
public sealed record ForEachFromInputStatement(
    string IteratorName,
    IReadOnlyList<IStatement> Body,
    int Line,
    int Column
) : IStatement;

public sealed record Program(IReadOnlyList<IStatement> Statements);

// Whole-tree search, shared by both backends.
//
// ★ WHY REFLECTION rather than a switch with an arm per node: a hand-written walk over ~95 node
// types is a list that goes stale silently, and the failure is invisible — the search returns
// false, the caller concludes the program does not do the thing, and everything still compiles.
// `Judge` proved it: the compiler's hand-written interrupt search was written before Judge existed
// and never grew an arm for it, so a poll inside a judgement was simply not seen.
//
// Both backends ask the same question of the same code here, which is what keeps their answers
// from drifting apart — a divergence the no-divergence rule would otherwise have to catch after
// the fact, at runtime, on a user's program.
public static class AstSearch
{
    // Runs `action` on every node in the tree. The collecting form of Contains — same walk, same
    // reason for it: a hand-written collector that forgets a node type does not fail, it silently
    // collects less, and whatever needed the missing entry crashes somewhere else entirely.
    public static void Visit(object? node, Action<object> action) =>
        Contains(node, n => { action(n); return false; });

    /// <summary>Every statement anywhere in the tree, for declarations that belong to the PROGRAM.</summary>
    /// <remarks>
    /// ★★ A TYPE declaration is program-scope wherever it is written — measured: an object defined
    /// inside a rabbit block is usable after that block closes. A VALUE binding is not, so this is
    /// for the first kind only. Hoisting a nested `Bind` would turn a closure into a free function,
    /// and hoisting a `permanently` local would make it a program-level constant; those sites keep
    /// their narrow walks on purpose.
    ///
    /// ⚠⚠ It lives HERE because the same walk was written by hand THREE times — once in the
    /// checker, once in the interpreter, once in the compiler — and they drifted. Each entered
    /// `PullStatement` and `PullRabbitStatement` and nothing else, so `Define object` inside a
    /// FUNCTION body was registered by none of them: the definition was silently ignored and the
    /// USE failed later with "'square' is not a defined object type — define the object type
    /// first", telling the writer to declare what they had just declared.
    ///
    /// ★ The compiler had already been fixed, and its note says why: its two collectors "were
    /// near-identical hand-written switches that had DRIFTED — this one grew a PullStatement arm
    /// and the other never did". Two more copies of that switch existed; this is the one that
    /// replaces them.
    /// </remarks>
    public static IEnumerable<IStatement> EveryStatement(IEnumerable<IStatement> stmts)
    {
        var found = new List<IStatement>();
        Visit(stmts, node => { if (node is IStatement statement) found.Add(statement); });
        return found;
    }

    public static bool Contains(object? node, Func<object, bool> predicate)
    {
        switch (node)
        {
            // CufetType is a type graph, not program text — nothing is spelled inside one, and it
            // is the one shape in this namespace that can be deep and heavily shared.
            case null or string or CufetType: return false;

            // ★★ A cufet axiom's body is NOT searched — the node itself is offered, its contents are
            // not. What a block holds is not in the program until a `Cite` places it, and every
            // search here answers a question about the program: which types to hoist, which axioms
            // to prepare, whether a `bury` is reachable. Descending would make declaring a block
            // and citing it the same thing, which would leave `Cite` with nothing to do.
            //
            // ⚠ This is why the hoist cannot be trusted to keep an uncited object out of scope on
            // its own: EveryStatement reaches EVERY statement anywhere, and that is exactly its
            // value elsewhere. The exception belongs here, once, rather than at each caller.
            case CufetAxiomDefinition cufetAxiom: return predicate(cufetAxiom);

            case System.Runtime.CompilerServices.ITuple tup:
                for (int i = 0; i < tup.Length; i++)
                    if (Contains(tup[i], predicate)) return true;
                return false;

            case System.Collections.IEnumerable en:
                foreach (var item in en)
                    if (Contains(item, predicate)) return true;
                return false;

            default:
                if (predicate(node)) return true;
                // Keyed on the namespace, not on IExpression/IStatement: not every AST record
                // implements either. ConditionArm and JudgeArm are plain records that HOLD
                // statements, so matching the interfaces walks straight past the body of every
                // `If` and every judgement.
                if (node.GetType().Namespace != typeof(Program).Namespace) return false;
                foreach (var prop in node.GetType().GetProperties())
                    if (Contains(prop.GetValue(node), predicate)) return true;
                return false;
        }
    }
}
