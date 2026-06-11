namespace Cufet.Lexer;

public enum TokenType
{
    Identifier,
    It,       // reserved pronoun — never an Identifier
    Article,  // "a" | "an" | "the" — noise; parser discards
    Number,   // numeric literal: digit { digit } [ "." digit { digit } ]
    String,   // "..." with "" as escaped double-quote
    State,    // keyword — print/announce a value
    Define,   // keyword — declare a new name
    As,       // keyword — separates name from initial value in Define
    Becomes,  // keyword — reassign an existing name
    Dot,      // "." — statement terminator
    Colon,    // ":" — opens a block body

    // ── Control flow ──────────────────────────────────────────────────────
    If,        // "If"
    Otherwise, // "Otherwise"
    Done,      // "Done" — closes a multi-statement block

    // ── Arithmetic ────────────────────────────────────────────────────────
    Plus, Minus, Star, Slash, Percent,
    LParen, RParen,

    // ── Comparison (symbol, expression context) ───────────────────────────
    // = is equality only — assignment is `becomes`, declaration is `Define`.
    Equal,
    Lt, Gt, Lte, Gte,   // < > <= >=

    // ── Comparison (word-form, conditional context only) ─────────────────
    // These appear after If/Otherwise if, never in expression position.
    Is,       // "is"
    Not,      // "not"   — in "is not"
    Greater,  // "greater" — in "is greater than"
    Less,     // "less"  — in "is less than" and "or less"
    Than,     // "than"  — in "is greater/less than"
    Or,       // "or"    — logical-or, and comparison tail in "or more" / "or less"
    And,      // "and"   — logical-and
    More,     // "more"  — in "or more"

    // ── Loops ─────────────────────────────────────────────────────────────
    Comma,    // "," — separates condition from "repeat" in While; also separates series elements
    While,    // "while"
    Repeat,   // "repeat" — in "While..., repeat:" and standalone "Repeat:"
    Until,    // "until" — closes Repeat...Until body
    Stop,     // "stop" — break out of innermost loop
    Skip,     // "skip" — next iteration of innermost loop

    // ── Collections — literal ─────────────────────────────────────────────
    Series,   // "series" — signals a series literal after "as [article] series"
    With,     // "with"   — delimits element-type annotation from contents: "series [of T] with (...)"

    // ── Collections — for-each ────────────────────────────────────────────
    For,      // "for"
    Each,     // "each"
    In,       // "in" — also reserved for future membership test

    // ── Collections — operations ──────────────────────────────────────────
    Ordinal,  // "first".."tenth" and "last" — 1-based positional keywords
    Item,     // "item" — parametric element access
    Of,       // "of" — connects element access / length to series name
    NumberKw, // "number" — in "the number of series"; distinct from Number (numeric literal)
    Add,      // "add" — series mutation keyword
    To,       // "to" — in "add X to series" / "add X to the start of series"
    Start,    // "start" — in "add X to the start"
    After,    // "after" — in "add X after position"
    Remove,   // "remove" — series mutation keyword
    From,     // "from" — in "remove X from series"

    // ── Functions ─────────────────────────────────────────────────────────
    Bind,        // "bind"     — declares a named function
    Cast,        // "cast"     — calls a function
    Given,       // "given"    — introduces the parameter list in a Bind / function-type annotation
    Return,      // "return"   — returns a value (or exits early) from a function
    Void,        // "void"     — marks a function that returns no value
    On,          // "on"       — separates function name from argument list in Cast
    FunctionKw,  // "function" — type keyword in function-type parameter annotations

    // ── Semantic (parser-generated, never emitted by lexer) ───────────────
    NotEqual, // produced by "is not"; used in BinaryExpression only

    Eof,
}
