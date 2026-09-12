namespace Cufet.Interpreter;

/// <summary>
/// Reads the text inside `Define regex &lt;name&gt; as [ … ]` and compiles it, or says why it cannot.
/// </summary>
/// <remarks>
/// <para>
/// ★★ This is the whole reason the pattern is written in brackets rather than handed over as text.
/// The pattern is fixed where it is written, so it can be READ AT CHECK TIME — a malformed one is a
/// static error naming the fault, not a failure when the line finally runs. An ordinary function
/// taking `text` could never promise that, because its argument is not known until it has one.
/// </para>
/// <para>
/// ★★ It compiles to DATA, and that is a decision rather than a convenience. Handing the pattern
/// on as text for something else to parse at run time would mean TWO PARSERS for one pattern
/// language — one that validates and one that runs — which would then have to agree. That is the
/// two-engines hazard that decided this whole design, in miniature, and it is refused here for the
/// same reason it was refused there.
/// </para>
/// <para>
/// ⚠ Nothing here parses a pattern built at run time, because there is no such pattern. A value is
/// spliced in as a VALUE, never concatenated into the source — the same guarantee
/// `run "grep" with arguments (…)` makes, and the same reason injection is structurally impossible
/// rather than merely discouraged.
/// </para>
/// </remarks>
internal static class RegexPattern
{
    /// <summary>One state of the compiled automaton, as the engine reads it.</summary>
    /// <remarks>
    /// ⚠⚠ The engine in `Prelude/regex.cufe` reads exactly these four fields under exactly these
    /// names. This is one format described in two places and nothing checks that they agree, so a
    /// change here is a change there — the tests are the only thing standing between the two.
    /// </remarks>
    internal readonly record struct State(int Kind, string Ch, int Step, int Alt);

    internal const int KindChar   = 0;   // match Ch, then go to Step
    internal const int KindAny    = 1;   // match any one character, then go to Step
    internal const int KindBranch = 2;   // go to Step or to Alt, consuming nothing
    internal const int KindAccept = 3;

    /// <summary>What the language will spell later; refused by name until each one lands.</summary>
    /// <remarks>
    /// ★ Refused BY NAME rather than taken literally, and the ordering is deliberate: a refusal
    /// that says "not supported yet" becomes support later, and a program written against it keeps
    /// meaning what it meant. That is not hypothetical — `*` was refused this way, repeats then
    /// landed, and every program written against the refusal still means what it meant.
    /// </remarks>
    private static readonly Dictionary<char, string> NotYet = new()
    {
        ['^'] = "a start anchor",
        ['$'] = "an end anchor",
        ['{'] = "a count",
        ['}'] = "a count",
    };

    // ── The pattern's own syntax tree ────────────────────────────────────────
    private abstract record Node;
    private sealed record Ch(string Value)           : Node;   // one ordinary character
    private sealed record Any                        : Node;   // .
    private sealed record Cat(Node Left, Node Right) : Node;
    private sealed record Or(Node Left, Node Right)  : Node;
    private sealed record Star(Node Inner)           : Node;   // *
    private sealed record Plus(Node Inner)           : Node;   // +
    private sealed record Opt(Node Inner)            : Node;   // ?

    /// <summary>
    /// The automaton this pattern compiles to. State 1 is always the entry, so the engine is told
    /// nothing beyond the series itself.
    /// </summary>
    /// <param name="source">The text between the brackets, exactly as written.</param>
    /// <param name="fail">
    /// How to report a fault — supplied by the caller so the message arrives in that caller's own
    /// voice, positioned on the declaration, rather than as a shape this file invents.
    /// </param>
    public static IReadOnlyList<State> Compile(
        string source, Func<string, string, string, Exception> fail)
    {
        var reader = new Reader(source, fail);
        Node root  = reader.ReadAlternation();
        reader.ExpectEnd();

        var states = new List<State>();
        // ⚠ Index 1 is reserved BEFORE anything is compiled and filled in last. The construction
        // below discovers its entry state only when it has finished, and the engine's contract is
        // that the entry is state 1 — so a placeholder is cheaper than renumbering every reference
        // afterwards. It costs one branch that goes the same way twice.
        states.Add(default);
        int accept = Emit(states, new State(KindAccept, "", 0, 0));
        int start  = Build(states, root, accept);
        states[0]  = new State(KindBranch, "", start, start);
        return states;
    }

    private static int Emit(List<State> states, State s)
    {
        states.Add(s);
        return states.Count;            // 1-based, the way Cufet indexes
    }

    /// <summary>Compiles <paramref name="node"/> so that finishing it lands on <paramref name="next"/>.</summary>
    /// <remarks>
    /// ★ The SUCCESSOR is threaded inward rather than patched afterwards. Thompson's original
    /// builds fragments with dangling outputs and patches them later; passing `next` down gives the
    /// same automaton with no patch lists, and the only place still needing a placeholder is a
    /// loop, where the body's successor is a state that does not exist yet.
    /// </remarks>
    private static int Build(List<State> states, Node node, int next) => node switch
    {
        Ch c   => Emit(states, new State(KindChar, c.Value, next, 0)),
        Any    => Emit(states, new State(KindAny, "", next, 0)),
        Cat x  => Build(states, x.Left, Build(states, x.Right, next)),
        Or x   => Emit(states, new State(KindBranch, "",
                        Build(states, x.Left, next), Build(states, x.Right, next))),

        // `?` — take the body or skip it. Nothing loops, so nothing needs reserving.
        Opt x  => Emit(states, new State(KindBranch, "", Build(states, x.Inner, next), next)),

        // `*` and `+` differ only in WHERE the loop is entered: a star enters at the branch and may
        // skip the body entirely; a plus enters at the body, so the body runs at least once.
        Star x => Loop(states, x.Inner, next, enterAtBody: false),
        Plus x => Loop(states, x.Inner, next, enterAtBody: true),

        _ => throw new InvalidOperationException("unknown pattern node"),
    };

    private static int Loop(List<State> states, Node inner, int next, bool enterAtBody)
    {
        int branch = Emit(states, default);          // reserved: the body must point back at it
        int body   = Build(states, inner, branch);
        states[branch - 1] = new State(KindBranch, "", body, next);
        return enterAtBody ? body : branch;
    }

    // ── Reading the pattern ──────────────────────────────────────────────────
    private sealed class Reader(string source, Func<string, string, string, Exception> fail)
    {
        private int _at;

        private bool AtEnd => _at >= source.Length;
        private char Peek  => source[_at];

        public void ExpectEnd()
        {
            if (!AtEnd && Peek == ')')
                throw fail("this pattern closes a group that was never opened",
                           "every ')' needs a '(' before it",
                           @"write '\)' if you meant the character");
        }

        public Node ReadAlternation()
        {
            Node left = ReadSequence();
            while (!AtEnd && Peek == '|')
            {
                _at++;
                left = new Or(left, ReadSequence());
            }
            return left;
        }

        private Node ReadSequence()
        {
            Node? built = null;
            while (!AtEnd && Peek != '|' && Peek != ')')
            {
                Node piece = ReadRepeated();
                built = built is null ? piece : new Cat(built, piece);
            }
            // ⚠ An empty side is refused for the reason an empty PATTERN is: `a|` would mean "a, or
            // nothing at all", which matches every subject including the empty one. That is the
            // least useful answer available and far likelier to be a typo than an intention.
            return built ?? throw fail(
                "this pattern has an empty alternative",
                "one side of a '|' has nothing on it, which would match everywhere",
                "write what that side should find, or drop the '|'");
        }

        private Node ReadRepeated()
        {
            Node inner = ReadAtom();
            // Stacked, so `a+?` reads left to right the way everything else in this language does.
            while (!AtEnd && (Peek == '*' || Peek == '+' || Peek == '?'))
            {
                char mark = source[_at++];
                inner = mark switch
                {
                    '*' => new Star(inner),
                    '+' => new Plus(inner),
                    _   => new Opt(inner),
                };
            }
            return inner;
        }

        private Node ReadAtom()
        {
            if (AtEnd)
                throw fail("this pattern is empty",
                           "a pattern with nothing in it would match everywhere, which is never what was meant",
                           "write the text the pattern should find");

            char c = Peek;

            if (c is '*' or '+' or '?')
                throw fail($"'{c}' has nothing before it to repeat",
                           "a repeat applies to whatever comes just before it",
                           $@"write '\{c}' if you meant the character itself");

            if (NotYet.TryGetValue(c, out var meaning))
                throw fail($"'{c}' means {meaning}, which patterns cannot do yet",
                           "this version reads characters, '.', '*', '+', '?', '|' and groups",
                           $@"write '\{c}' if you meant the character itself");

            if (c == '[') { _at++; return ReadClass(); }

            if (c == '(')
            {
                _at++;
                Node inside = ReadAlternation();
                if (AtEnd || Peek != ')')
                    throw fail("this pattern opens a group that is never closed",
                               "every '(' needs a ')' after it",
                               @"write '\(' if you meant the character");
                _at++;
                return inside;
            }

            if (c == '.') { _at++; return new Any(); }

            if (c == '\\')
            {
                // ⚠⚠ UNREACHABLE FROM SOURCE, and kept anyway — say which, because a guard whose
                // status nobody wrote down becomes a guard nobody dares remove. A trailing
                // backslash escapes the closing bracket, so the axiom never closes and the LEXER
                // reports it first: MEASURED, `[abc\]` gives "unterminated foreign source". It
                // stays because without it the read below walks off the end — a crash instead of a
                // sentence — and because this should be total on its own terms, not on a caller's.
                if (_at + 1 >= source.Length)
                    throw fail("this pattern ends in a backslash, which escapes nothing",
                               "a backslash makes the character after it ordinary, so there has to be one",
                               @"write '\\' for a backslash you meant literally");

                char next = source[_at + 1];
                // Every metacharacter is escapable, and so is the backslash. Nothing else is: an
                // unknown escape is refused rather than quietly meaning the bare character, because
                // `\d` will mean digits one day and meaning 'd' until then is a change nobody sees.
                if (next != '\\' && !IsMeta(next))
                    throw fail($@"'\{next}' is not an escape this pattern understands",
                               "a backslash may make a metacharacter ordinary, or stand for a backslash",
                               $"write '{next}' on its own if you meant the character");

                _at += 2;
                return new Ch(next.ToString());
            }

            _at++;
            return new Ch(c.ToString());
        }

        /// <summary>Reads `[ … ]` and desugars it to an alternation of the characters it admits.</summary>
        /// <remarks>
        /// ★★ A class becomes an `Or` chain of ordinary characters, so NOTHING downstream changes —
        /// not the construction, not the compiled record, not the engine. `[a-c]` is exactly
        /// `(a|b|c)`, which was already expressible; what this slice adds is a way to WRITE it.
        /// That is why it carries no new state kind.
        ///
        /// ⚠ The cost is states: `[a-z]` expands to 26 characters plus the 25 branches joining
        /// them. Patterns are small and the simulation is linear in states times subject length, so
        /// this is affordable — but a class over a large range is not free, and a representation
        /// that holds ranges directly would be about THAT, not about what a class means.
        ///
        /// ⚠ A NEGATED class is refused by name. It cannot be desugared this way — "every
        /// character except these" is not a finite alternation — so it needs a real representation
        /// in the automaton and belongs in its own slice.
        /// </remarks>
        private Node ReadClass()
        {
            if (!AtEnd && Peek == '^')
                throw fail("a class beginning with '^' means every character EXCEPT those, which "
                         + "patterns cannot do yet",
                           "this version spells out the characters a class admits, and there is no "
                         + "finite way to spell out the ones it does not",
                           @"list what should match instead, or write '\^' for the character");

            Node? built = null;
            while (!AtEnd && Peek != ']')
            {
                char lo = ReadClassCharacter();
                // `a-z`, unless the '-' is the last thing before the ']' and so an ordinary dash.
                if (!AtEnd && Peek == '-' && _at + 1 < source.Length && source[_at + 1] != ']')
                {
                    _at++;
                    char hi = ReadClassCharacter();
                    if (hi < lo)
                        throw fail($"'{lo}-{hi}' is a range that runs backwards",
                                   "a range goes from the earlier character to the later one",
                                   $"write '{hi}-{lo}'");
                    for (char c = lo; c <= hi; c++) built = Add(built, c);
                    continue;
                }
                built = Add(built, lo);
            }

            if (AtEnd || Peek != ']')
                throw fail("this pattern opens a class that is never closed",
                           "every '[' needs a ']' after it",
                           @"write '\[' if you meant the character");
            _at++;

            // ⚠ An empty class admits nothing, so the pattern could never match. A pattern that
            // cannot succeed is a typo every time, and saying so beats failing silently forever.
            return built ?? throw fail(
                "this class is empty",
                "a class with no characters in it admits nothing, so the pattern could never match",
                "list the characters it should admit");
        }

        private static Node Add(Node? built, char c) =>
            built is null ? new Ch(c.ToString()) : new Or(built, new Ch(c.ToString()));

        /// <summary>One character inside a class, with escapes honoured.</summary>
        /// <remarks>
        /// ⚠ The escapable set differs INSIDE a class: `-` and `]` matter here and nowhere else,
        /// and `*` or `+` are already ordinary characters in here. Refusing an unknown escape is
        /// the same call the outer reader makes, for the same reason.
        /// </remarks>
        private char ReadClassCharacter()
        {
            char c = source[_at];
            if (c != '\\') { _at++; return c; }

            if (_at + 1 >= source.Length)
                throw fail("this class ends in a backslash, which escapes nothing",
                           "a backslash makes the character after it ordinary, so there has to be one",
                           @"write '\\' for a backslash you meant literally");

            char next = source[_at + 1];
            if (next is not ('\\' or ']' or '[' or '-' or '^'))
                throw fail($@"'\{next}' is not an escape a class understands",
                           "inside a class a backslash may precede a backslash, a bracket, a dash "
                         + "or a caret",
                           $"write '{next}' on its own if you meant the character");
            _at += 2;
            return next;
        }

        // ⚠ `[` and `]` stay escapable outside a class even though they left the not-yet table:
        // they are now real syntax, so `\[` has to keep meaning the character.
        private static bool IsMeta(char c) =>
            c is '*' or '+' or '?' or '|' or '(' or ')' or '.' or '[' or ']'
            || NotYet.ContainsKey(c);
    }
}
