namespace Cufet.Lexer;

public enum TokenType
{
    Identifier,
    It,       // reserved pronoun — never an Identifier
    Article,  // "a" | "an" | "the" — noise; parser discards
    Number,   // numeric literal: digit { digit } [ "." digit { digit } ]
    String,   // "..." with backslash escapes; no bare '{' inside

    // ── String interpolation (lexer-generated; never emitted for plain strings) ──
    InterpolOpen,      // opening '"' of an interpolated string literal
    StringPiece,       // a literal text segment within an interpolated string
    InterpolHoleOpen,  // bare '{' that starts an embedded expression
    InterpolHoleClose, // matching '}' that ends an embedded expression
    InterpolClose,     // closing '"' of an interpolated string literal
    State,    // keyword — print/announce a value
    Define,   // keyword — declare a new name
    As,       // keyword — separates name from initial value in Define
    Becomes,  // keyword — reassign an existing name
    Dot,      // "." — statement terminator
    Colon,    // ":" — opens a block body
    Permanently, // "permanently" — trailing adverb on Define; binding can't be reassigned
    Shadow,   // "shadow" — in "Define a shadow x as ..."; deliberate shadowing opt-in

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
    Record,   // "record" — signals a record literal after "[article] record with (...)"
    With,     // "with"   — delimits element-type annotation from contents: "series [of T] with (...)"
    Like,     // "like"   — introduces record shape in empty series: "series of records like (...)"

    // ── Objects & Interfaces ─────────────────────────────────────────────
    Object,     // "object"    — in "Define object <name> with (...)"
    Interface,  // "interface" — in "Define <name> as an interface for {...}"
    New,        // "new"       — in "a new <type> {fields}"
    One,        // "one"      — reserved self-reference keyword inside method bodies
    Possessive, // "'s"       — possessive marker: alice's name, one's field
    LBrace,     // "{"        — opens object literal fields
    RBrace,     // "}"        — closes object literal fields
    Unto,       // "unto"     — in "Bind <ret> to <name> unto <type>: ..." — method defined outside its object's body
    GetKw,      // "get"      — in "Get <name> as <type>: ..." — computed property (getter)
    SetKw,      // "set"      — in "Set <name> given (<param>): ..." — intercepting write (setter)
    MakingKw,   // "making"   — in "Bind making a <type> to <name>, given (...)" — named constructor
    UnmakingKw,   // "unmaking"   — in "Bind unmaking a <type> to <name>: ..."      — destructor
    OverloadingKw, // "overloading" — in "Bind overloading <op>, given (...): ..."    — operator overload

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

    // ── Text operations ───────────────────────────────────────────────────
    LengthKw,  // "length"    — in "the length of <text>"
    Joined,    // "joined"    — in "<text> joined to <text>"
    Converted, // "converted" — in "<value> converted to text"
    Split,      // "split"      — in "<text> split by <delimiter>"
    Contains,   // "contains"   — in "<text> contains <substring>"
    Position,   // "position"   — in "the position of <substring> in <text>"; excluded from
                //                 IsFieldNameToken(forAccess: true) — collides with "the X of" named access
    Characters, // "characters" — in "the characters from N to M of <text>", "the first/last N characters of <text>"
    End,        // "end"        — in "to the end" (substring upper bound = length of the text)
    Replace,    // "replace"    — in "replace <old> with <new> in <text>"
    Uppercase,  // "uppercase"  — in "<text> in uppercase"
    Lowercase,  // "lowercase"  — in "<text> in lowercase"
    Trimmed,    // "trimmed"    — in "<text> trimmed"

    // ── Sort ──────────────────────────────────────────────────────────────────
    Sorted,   // "sorted"   — in "<series> sorted", "<series> sorted in reverse", "<series> sorted by <field>"
    Reverse,  // "reverse"  — in "sorted in reverse"

    // ── Chance book (randomness) ───────────────────────────────────────────────
    Random,   // "random"   — in "a random number/item/guess"
    Randomly, // "randomly" — in "randomly shuffled <series>"
    Shuffled, // "shuffled" — in "randomly shuffled <series>" (past-participle transform, like Sorted)
    Guess,    // "guess"    — in "a random guess"
    SeedKw,   // "seed"     — in "Seed the chance with <number>."

    // ── Range ─────────────────────────────────────────────────────────────────
    Range,     // "range"     — in "range <start> to <end>"; To already exists
    Counting,  // "counting"  — in "range <start> to <end> counting by <step>"
    By,        // "by"        — in "counting by <step>" and "sorted by <field>"

    // ── Voidable ──────────────────────────────────────────────────────────────
    Voidable,  // "voidable"  — in "a voidable number" type annotation
    But,       // "but"       — in "<expr> but void is <default>" / "<expr> but on failure <default>"

    // ── Failures (recoverable errors as values) ─────────────────────────────────
    Failure,  // "failure"  — in "a failure \"msg\" [of category \"tag\"]" (literal) / "the failure" (bare
              //               reference) / "<type> or failure" (return type) / "but on failure"
    Category, // "category" — in "of category \"tag\""; NOT excluded from IsFieldNameToken —
              //               "the category of the failure" must reach the normal named-access path
    Try,      // "try"      — in "Try to: ... Done."
    Case,     // "case"     — in "In case of failure:" / "In case of exception (...):"
    Pass,      // "pass"      — in "or pass the failure off"
    Off,       // "off"       — in "or pass the failure off"
    Exception, // "exception" — in "In case of exception (the exception):" / "the exception" binding
    Suppress,  // "suppress"  — in "Suppress the exception."

    // ── Rabbits (block-scoped memory regions) ────────────────────────────────
    Rabbit,   // "rabbit" — in "With a rabbit <name>: ... Done." and "given (the rabbit <name>)"

    // ── Books (standard-library capability units) ─────────────────────────────
    Pull,   // "pull"   — in "Pull a book on <name> [as <local>]."
    Book,   // "book"   — in "Pull a book on <name>"
    CatalogueKw, // "catalogue" — heterogeneous series: element type is a union
    AtlasKw,     // "atlas"     — heterogeneous map:    value  type is a union

    Matrix,    // "matrix"  — type introduced by the 'collections' book
    At,        // "at"      — in "the item at (row, col) of <matrix>"
    RowsKw,    // "rows"    — in "the rows of <matrix>"; excluded from field-name access
    ColumnsKw, // "columns" — in "the columns of <matrix>"; excluded from field-name access
    FilledKw,  // "filled"  — in "a matrix with <rows> by <columns> filled with <value>"

    // ── I/O ───────────────────────────────────────────────────────────────────
    Read,     // "read"   — starts a read expression; the form words (line/lines/all/input) are
              //            parsed as contextual identifiers (lexeme-checked), not reserved keywords
    File,     // "file"   — in "from the file \"path\"" / "to the file \"path\""
    Write,    // "write"  — in "write <text> to the file \"path\"" (overwrites)
    Append,   // "append" — in "append <text> to the file \"path\"" (appends)
    Run,      // "run"    — in "run <program> [with arguments (...)]"; "arguments" is contextual
    Stream,   // "stream" — in "readable/writable stream of text" type annotation; "input" stays contextual
    Open,     // "open"   — in "With the file ... open for reading/writing as <name>:"

    // ── Maps ──────────────────────────────────────────────────────────────────
    Map,    // "map"   — type annotation and literal
    Has,    // "has"   — in "map has a key/entry for X"
    Key,    // "key"   — in "has a key for X"; NOT excluded from field names (used in "the key of mapping")
    Entry,  // "entry" — in "the entry for X in map", "in map, the entry for X becomes V"
    Size,   // "size"  — in "the size of map"; excluded from IsFieldNameToken(forAccess: true)

    // ── Directory traversal ───────────────────────────────────────────────────────
    ContentsKw,   // "contents"   — in "the contents of the directory <path>"
    DirectoryKw,  // "directory"  — in "the contents of the directory <path>" / "the path ... is a directory"
    PathKw,       // "path"       — in "the path <path> exists/is a directory/is a file"
                  //                 "exists" is contextual (lexeme-checked), not reserved

    // ── Environment ───────────────────────────────────────────────────────────────
    EnvironmentKw, // "environment" — in "the environment variable <name>"; read-only OS env access
                   //                 "variable" is contextual (lexeme-checked), not reserved

    // ── Signals ───────────────────────────────────────────────────────────────────
    InterruptKw,    // "interrupt"   — in "an interrupt has been requested"; "Acknowledge the interrupt."
                    //                 "been"/"requested" are contextual (lexeme-checked), not reserved
    AcknowledgeKw,  // "acknowledge" — in "Acknowledge the interrupt."

    // ── Semantic (parser-generated, never emitted by lexer) ───────────────
    NotEqual, // produced by "is not"; used in BinaryExpression only

    Eof,
}
