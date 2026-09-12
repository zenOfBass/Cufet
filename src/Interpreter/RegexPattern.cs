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

    // ★ The anchors. Both consume NOTHING, like a branch — what they add is a question about
    // WHERE the subject is, which is the first thing the engine has ever had to know beyond the
    // state series itself. They are states rather than flags on the whole pattern precisely so
    // they compose: `(^a|b$)` is an ordinary alternation of two ordinary paths.
    //
    // ⚠ Neither needs `Ch` or `Alt`, so the four-field record is unchanged — the contract this
    // file shares with `Prelude/regex.cufe` and the lowering holds exactly as it did.
    internal const int KindAtStart = 4;  // passable only with nothing consumed yet, then go to Step
    internal const int KindAtEnd   = 5;  // passable only with everything consumed, then go to Step

    // ★ A negated class. `Ch` carries EVERY excluded character packed into one text, because the
    // four-field record has exactly one text field and growing it for one kind's benefit would
    // change the shape all three places agree on. Membership in a one-character-wide needle is
    // what `contains` already does, so the engine needs no new operation either.
    //
    // ⚠ Unlike a positive class this CANNOT desugar — "every character except these" is not a
    // finite alternation — which is the whole reason it needs a kind while `[abc]` never did.
    internal const int KindNoneOf = 6;   // match any one character NOT in Ch, then go to Step

    // ★ The line-wise anchors, which is what `(?m)` turns `^` and `$` into. Unlike their plain
    // forms these need to SEE the subject — "is the character just consumed a line break" is not
    // answerable from a position alone — which is the second and last thing the engine has ever
    // needed beyond the state series.
    //
    // ★★ `Ch` carries the line separator rather than the engine knowing one. That keeps the
    // four-field record intact for a fourth feature running, and means the separator is decided in
    // exactly one place: here.
    internal const int KindAtLineStart = 7;  // passable at the subject's start or just after Ch
    internal const int KindAtLineEnd   = 8;  // passable at the subject's end or just before Ch

    /// <summary>What separates one line from the next, decided here and nowhere else.</summary>
    /// <remarks>
    /// ★ A line break in a Cufet literal is one `\n` whatever the file is stored as — a CRLF source
    /// does not put a `\r` into the text — so this is the whole answer rather than half of one.
    /// `read all lines from the file` strips the `\r` too, measured.
    /// </remarks>
    private const string LineBreak = "\n";

    /// <summary>What the language will spell later; refused by name until each one lands.</summary>
    /// <remarks>
    /// ★★ EMPTY, and that is the arc FINISHING rather than the mechanism being abandoned. Every
    /// entry this table ever held became support without changing what a single written pattern
    /// meant: `*` then repeats, `[a-z]` then classes, `^` and `$` then anchors, `{` then counts.
    /// Reading any of them as ordinary characters in the meantime would have silently
    /// reinterpreted somebody's working program on the day it landed.
    ///
    /// ★ It stays because the next thing regex has and this book does not will need it, and
    /// because an empty table states something a deleted one could not: nothing is currently
    /// known-missing and refused. What IS refused now is refused permanently, for being outside
    /// what an automaton can express — see <see cref="Reader.ReadGroupExtension"/>.
    /// </remarks>
    private static readonly Dictionary<char, string> NotYet = new();

    // ── The pattern's own syntax tree ────────────────────────────────────────
    private abstract record Node;
    private sealed record Ch(string Value)           : Node;   // one ordinary character
    private sealed record Any                        : Node;   // .
    private sealed record Cat(Node Left, Node Right) : Node;
    private sealed record Or(Node Left, Node Right)  : Node;
    private sealed record Star(Node Inner)           : Node;   // *
    private sealed record Plus(Node Inner)           : Node;   // +
    private sealed record Opt(Node Inner)            : Node;   // ?
    private sealed record AtStart                    : Node;   // ^
    private sealed record AtEnd                      : Node;   // $
    private sealed record NoneOf(string Excluded)    : Node;   // [^…]
    private sealed record AtLineStart                : Node;   // ^ under (?m)
    private sealed record AtLineEnd                  : Node;   // $ under (?m)

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

        // ★ An anchor threads its successor exactly like anything else — it is only the engine
        // that treats it differently, by asking where the subject is before letting the path
        // through. Nothing about the construction is special-cased, which is the whole payoff of
        // choosing states over flags.
        AtStart => Emit(states, new State(KindAtStart, "", next, 0)),
        AtEnd   => Emit(states, new State(KindAtEnd, "", next, 0)),

        // The separator travels in Ch, so the engine never has to know what a line break is.
        AtLineStart => Emit(states, new State(KindAtLineStart, LineBreak, next, 0)),
        AtLineEnd   => Emit(states, new State(KindAtLineEnd, LineBreak, next, 0)),

        // ★ An ordinary CONSUMING state, like a character or a dot — which is why repeats and
        // groups work on it for nothing: `[^,]+` is a plus over one state, exactly as `a+` is.
        NoneOf n => Emit(states, new State(KindNoneOf, n.Excluded, next, 0)),
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

        /// <summary>Set by `(?i)`, and restored at the end of the group it was set inside.</summary>
        private bool _fold;

        /// <summary>Set by `(?s)` — whether `.` may cross a line break.</summary>
        private bool _dotAll;

        /// <summary>Set by `(?m)` — whether `^` and `$` mean each LINE rather than the whole subject.</summary>
        private bool _multiline;

        /// <summary>
        /// Takes `(?i)`, `(?s:`, `(?is)` and the like, applying each letter. Leaves the reader
        /// where it found it and reports false for anything else — `(?:`, `(?=`, `(?&lt;name&gt;`.
        /// </summary>
        /// <remarks>
        /// ★ Scanned to its terminator BEFORE any letter is applied, so a `(?` that turns out not
        /// to be a flag group leaves nothing behind. That matters because `(?=` and `(?:` both
        /// start the same way and mean something else entirely.
        /// </remarks>
        private bool TryTakeFlags(out bool scoped)
        {
            scoped = false;
            if (AtEnd || Peek != '(' || _at + 1 >= source.Length || source[_at + 1] != '?')
                return false;

            int scan = _at + 2;
            while (scan < source.Length && char.IsAsciiLetter(source[scan])) scan++;

            // No letters at all is `(?:` or `(?=`; anything but a terminator is not a flag group.
            if (scan == _at + 2) return false;
            if (scan >= source.Length || (source[scan] != ')' && source[scan] != ':')) return false;

            for (int i = _at + 2; i < scan; i++)
                switch (source[i])
                {
                    case 'i': _fold = true; break;
                    case 's': _dotAll = true; break;
                    case 'm': _multiline = true; break;
                    default:
                        throw fail($"'{source[i]}' is not a flag this pattern understands",
                                   "the flags are 'i' to ignore case, 's' to let '.' cross a line "
                                 + "break, and 'm' to make '^' and '$' mean each line",
                                   "write '(?i)', '(?s)', '(?m)' or any of them together");
                }

            scoped = source[scan] == ':';
            _at = scan + 1;
            return true;
        }

        /// <summary>
        /// Both cases of every character in <paramref name="members"/>, when `(?i)` is in force.
        /// </summary>
        /// <remarks>
        /// ⚠⚠ The casing comes from `CaseTable`, the table BOTH BACKENDS READ — never from
        /// `char.ToUpperInvariant`. .NET's casing is ICU-backed and MEASURED to differ per machine,
        /// and a pattern is compiled once in the front end, so using .NET here would not make the
        /// two backends disagree — it would make the same pattern mean different things on
        /// different machines, which is the one divergence the oracle structurally cannot see.
        /// Every machine that runs this suite is en-US.
        /// </remarks>
        private string Folded(string members)
        {
            if (!_fold) return members;
            var both = new List<char>();
            foreach (char c in members)
            {
                both.Add(c);
                char upper = (char)CaseTable.MapUpper(c);
                char lower = (char)CaseTable.MapLower(c);
                if (upper != c) both.Add(upper);
                if (lower != c) both.Add(lower);
            }
            return new string(both.ToArray());
        }

        /// <summary>One character as a node, carrying both cases when `(?i)` is in force.</summary>
        private Node Character(char c) => _fold ? Admitting(Folded(c.ToString())) : new Ch(c.ToString());

        private Node ReadSequence()
        {
            Node? built = null;
            while (!AtEnd && Peek != '|' && Peek != ')')
            {
                // ★ `(?i)` is a DIRECTIVE, not an atom — it matches nothing and produces no state.
                // It belongs here rather than in ReadAtom because everything ReadAtom returns is
                // something the automaton has to be able to run. The scoped form `(?i:…)` IS an
                // atom, so it is left for ReadAtom to open as a group.
                // ⚠ The scan APPLIES the letters as it goes, so a scoped form has to be undone
                // here and re-read inside the group — otherwise `(?i:ab)c` would fold the `c` too,
                // which is the precise thing the scoped spelling exists to avoid.
                int before = _at;
                (bool wasFolding, bool wasDotAll, bool wasMultiline) = (_fold, _dotAll, _multiline);
                if (TryTakeFlags(out bool scoped))
                {
                    if (!scoped) continue;
                    (_at, _fold, _dotAll, _multiline) = (before, wasFolding, wasDotAll, wasMultiline);
                }

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
            while (!AtEnd && (Peek == '*' || Peek == '+' || Peek == '?' || Peek == '{'))
            {
                if (Peek == '{') { inner = ReadCount(inner); continue; }

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

        /// <summary>`{n}`, `{n,}` or `{n,m}` — desugared to repetition of what came before it.</summary>
        /// <remarks>
        /// ★★ A count says nothing the book could not already say: `a{3}` is `aaa` and `a{2,4}` is
        /// `aa(a(a)?)?`. Like a class and unlike a negated one, it needs no state kind and the
        /// engine never learns it exists — which is why this was the cheapest of the debts and
        /// still the one that empties the refuse-by-name table.
        ///
        /// ⚠ The cost is states, and unlike a class it is UNBOUNDED by what is written: `[a-z]`
        /// costs 26 whatever you do, but `a{5000}` costs 5000. Hence the ceiling below — a pattern
        /// is small by nature, and one that is not should say so out loud rather than quietly
        /// building a machine nobody meant to ask for.
        /// </remarks>
        private const int MostRepeats = 1000;

        private Node ReadCount(Node inner)
        {
            int open = _at;
            _at++;                                  // past '{'

            int? min = ReadCountNumber();
            int? max = min;
            bool openEnded = false;

            if (!AtEnd && Peek == ',')
            {
                _at++;
                if (!AtEnd && Peek == '}') { openEnded = true; max = null; }
                else max = ReadCountNumber();
            }

            if (AtEnd || Peek != '}')
                throw fail("this count is never closed",
                           "a count is written '{n}', '{n,}' or '{n,m}'",
                           @"write '\{' if you meant the character");
            _at++;

            if (min is null || (!openEnded && max is null))
                throw fail("this count has no number in it",
                           "a count says how many times to repeat, so it needs a number",
                           $@"write '{{2}}', '{{2,}}' or '{{2,5}}' — or '\{{' for the character");

            if (!openEnded && max < min)
                throw fail($"'{{{min},{max}}}' counts down, from {min} to {max}",
                           "a count goes from the smaller number to the larger one",
                           $"write '{{{max},{min}}}'");

            // ⚠ Zero on its own means "none of these", which leaves nothing behind — and a pattern
            // with nothing in it is already refused as a typo. `{0,3}` is fine and means what it
            // says; it is only the bare `{0}` that cannot be anything but a mistake.
            if (min == 0 && !openEnded && max == 0)
                throw fail("'{0}' repeats nothing zero times",
                           "a count of zero leaves nothing behind, so the pattern could never use it",
                           "remove it, or write '{0,1}' for something optional");

            if (min > MostRepeats || max > MostRepeats)
                throw fail($"a count above {MostRepeats} is more than this pattern will build",
                           "a count is written out as that many copies, so a large one becomes a "
                         + "very large automaton",
                           $"use a repeat like '+' or '*' instead of counting past {MostRepeats}");

            _ = open;
            return Repeat(inner, min.Value, openEnded ? null : max);
        }

        /// <summary>
        /// A `(?…)` that is not `(?:` — named in the refusal, and split by WHY it is refused.
        /// </summary>
        /// <remarks>
        /// ★★ Two different kinds of no, and conflating them would be the lie. Lookaround is
        /// refused PERMANENTLY: it is not regular, an automaton cannot express it, and this book
        /// chose an automaton for reasons that still hold. Inline flags are refused FOR NOW, the
        /// way `^` once was — they are regular and the book owes them.
        /// </remarks>
        private Exception ReadGroupExtension()
        {
            char next = _at + 1 < source.Length ? source[_at + 1] : '\0';

            if (next is '=' or '!')
                return fail("this pattern uses lookahead, which an automaton cannot express",
                            "lookahead asks what comes next without consuming it, which needs a "
                          + "machine that can backtrack — and backtracking is what lets a pattern "
                          + "hang forever, so this book does not have one",
                            "match what you mean to consume, or split the question into two patterns");

            if (next == '<' && _at + 2 < source.Length && source[_at + 2] is '=' or '!')
                return fail("this pattern uses lookbehind, which an automaton cannot express",
                            "lookbehind asks what came before without consuming it, which needs a "
                          + "machine that can backtrack — and backtracking is what lets a pattern "
                          + "hang forever, so this book does not have one",
                            "match what you mean to consume, or split the question into two patterns");

            if (next == '<')
                return fail("this pattern names a capture, which patterns cannot do yet",
                            "a pattern answers whether the subject holds a match, and gives back no "
                          + "part of what it matched",
                            "use the group without a name");

            // ⚠ `(?i)` and `(?i:…)` both work and are handled before this is ever reached, so
            // arriving here with an 'i' means a form neither of those covers — `(?im)`, say.
            if (next is 'i' or 'm' or 's' or 'x')
                return fail($"'(?{next}' is not a flag this pattern understands",
                            "the only flag is 'i' for ignoring case, written '(?i)' to the end of "
                          + "the group or '(?i:…)' around part of it",
                            "write '(?i)' on its own, or '(?i:' around what it should cover");

            return fail($"'(?{next}' is not something this pattern understands",
                        "a '(' opens a group, and '(?:' opens one that captures nothing",
                        @"write '\(' if you meant the character");
        }

        /// <summary>Digits, or null when there are none.</summary>
        private int? ReadCountNumber()
        {
            int from = _at;
            while (!AtEnd && Peek is >= '0' and <= '9') _at++;
            if (_at == from) return null;
            // A run of digits this long is not a count anyone meant; the ceiling check below
            // reports it, but the parse has to survive reaching it.
            return int.TryParse(source[from.._at], out int n) ? n : MostRepeats + 1;
        }

        /// <summary>
        /// <paramref name="min"/> copies, then either a star or nested options up to
        /// <paramref name="max"/>.
        /// </summary>
        /// <remarks>
        /// ★ The optional tail is built from the inside out — `(a(a)?)?` rather than `(a)?(a)?` —
        /// because the second spelling would let a later copy match while an earlier one did not,
        /// which is not what `{2,4}` means.
        /// </remarks>
        private static Node Repeat(Node inner, int min, int? max)
        {
            Node? built = max is null ? new Star(inner) : null;

            if (max is not null)
                for (int i = 0; i < max.Value - min; i++)
                    built = built is null ? new Opt(inner) : new Opt(new Cat(inner, built));

            for (int i = 0; i < min; i++)
                built = built is null ? inner : new Cat(inner, built);

            return built!;
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

            // ⚠ This list had said "characters, '.', '*', '+', '?', '|' and groups" since before
            // classes shipped, so it described a version of the book that had not existed for two
            // slices. A refusal that lists what IS supported has to be edited by whoever adds
            // support, or it quietly becomes a lie in the one message a stuck reader trusts.
            if (NotYet.TryGetValue(c, out var meaning))
                throw fail($"'{c}' means {meaning}, which patterns cannot do yet",
                           "this version reads characters, '.', '*', '+', '?', '|', groups, "
                         + "classes and the anchors '^' and '$'",
                           $@"write '\{c}' if you meant the character itself");

            if (c == '^') { _at++; return _multiline ? new AtLineStart() : new AtStart(); }
            if (c == '$') { _at++; return _multiline ? new AtLineEnd() : new AtEnd(); }

            if (c == '[') { _at++; return ReadClass(); }

            if (c == '(')
            {
                _at++;

                // ⚠⚠ A flag lasts to the end of the group it was set inside, so the group's own
                // settings are saved here and put back below. That is what makes `(?i:ab)c` fold
                // the `ab` and leave the `c` alone.
                //
                // ⚠ EVERY flag has to be saved, not just the one that existed when this was
                // written. `_dotAll` was added later and missed here, and the leak showed up only
                // in the one probe case written to check for exactly that — `(?s:a)b.c` matched
                // across a line break that was outside the group.
                (bool outerFold, bool outerDotAll, bool outerMultiline) = (_fold, _dotAll, _multiline);

                // ★ `(?:…)` is a non-capturing group, and this book's groups ALREADY capture
                // nothing — there is no way to ask for a capture, so `(ab)+` and `(?:ab)+` are the
                // same automaton. Accepting the spelling costs nothing and lets a pattern written
                // elsewhere arrive intact.
                if (!AtEnd && Peek == '?' && _at + 1 < source.Length && source[_at + 1] == ':')
                    _at += 2;
                else if (!AtEnd && Peek == '?')
                {
                    // `(?i:…)` and friends. The scan sits one character before the '(' this
                    // branch already consumed, so it is rewound for the shared reader.
                    _at--;
                    if (!TryTakeFlags(out bool scopedFlags) || !scopedFlags)
                    {
                        _at++;
                        throw ReadGroupExtension();
                    }
                }

                Node inside = ReadAlternation();
                if (AtEnd || Peek != ')')
                    throw fail("this pattern opens a group that is never closed",
                               "every '(' needs a ')' after it",
                               @"write '\(' if you meant the character");
                _at++;
                (_fold, _dotAll, _multiline) = (outerFold, outerDotAll, outerMultiline);
                return inside;
            }

            // ⚠⚠ `.` STOPS AT A LINE BREAK unless `(?s)` says otherwise, which is what it means in
            // every regex flavour — and which this book had wrong until it was measured. It used
            // to match a newline like anything else, so a pattern read across lines that a reader
            // who knows regex would have expected to stop at one.
            //
            // ★ And it needs no state kind, because "any character except a newline" is exactly
            // what a negated class already is. The dotall form is the one that stays `Any`.
            if (c == '.') { _at++; return _dotAll ? new Any() : new NoneOf("\n"); }

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

                // ★ The shorthand classes. `\d` desugars to the same `Or` chain `[0-9]` produces
                // and `\D` to the same single state `[^0-9]` does, so neither reaches the engine
                // as anything new — they are spellings for what the book could already say.
                if (Shorthand.TryGetValue(next, out var members))
                {
                    _at += 2;
                    return Admitting(Folded(members));
                }
                if (IsNegatedShorthand(next))
                {
                    _at += 2;
                    return new NoneOf(Folded(Shorthand[char.ToLowerInvariant(next)]));
                }

                // Every metacharacter is escapable, and so is the backslash. Nothing else is: an
                // unknown escape is refused rather than quietly meaning the bare character — the
                // rule that kept `\d` meaning nothing until it meant digits.
                if (next != '\\' && !IsMeta(next))
                    throw fail($@"'\{next}' is not an escape this pattern understands",
                               "a backslash may make a metacharacter ordinary, or stand for a backslash",
                               TypedDirectly.TryGetValue(next, out var whitespace)
                                   ? $"write {whitespace} straight into the pattern — it needs no escape"
                                   : $"write '{next}' on its own if you meant the character");

                _at += 2;
                return Character(next);
            }

            _at++;
            return Character(c);
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
        /// ★★ A NEGATED class is the exception, and the asymmetry is the interesting part. `[^abc]`
        /// cannot become an alternation — "every character except these" is not a finite one — so
        /// where `[abc]` needed no new machinery at all, `[^abc]` needs a state kind of its own.
        /// Two spellings one bracket apart, and only one of them is sugar.
        /// </remarks>
        private Node ReadClass()
        {
            // A '^' is a negation ONLY here, at the very front. Anywhere else in a class it is an
            // ordinary character, which is why this is read before the loop and never inside it.
            bool negated = !AtEnd && Peek == '^';
            if (negated) _at++;

            // ⚠ Collected as CHARACTERS rather than built into an `Or` as it goes, because the two
            // spellings need different things from the same list: the positive form folds it into
            // an alternation, the negated form packs it into one state's text.
            var admitted = new List<char>();
            while (!AtEnd && Peek != ']')
            {
                // ★ A shorthand inside a class contributes its whole set — `[\d-]` is digits and a
                // dash, which is how anyone writes "a number, possibly signed".
                if (Peek == '\\' && _at + 1 < source.Length)
                {
                    char shorthand = source[_at + 1];
                    if (Shorthand.TryGetValue(shorthand, out var members))
                    {
                        _at += 2;
                        admitted.AddRange(members);
                        continue;
                    }
                    // ⚠ A NEGATED shorthand inside a class would mean the union of a complement
                    // and a list — still regular, but not something a flat list of admitted
                    // characters can hold, and this class compiles to exactly that. Refused by
                    // name, pointing at the spelling that does work rather than leaving the reader
                    // to find it.
                    if (IsNegatedShorthand(shorthand))
                        throw fail($@"'\{shorthand}' cannot be used inside a class",
                                   "a class is the characters it admits, and a negated shorthand is "
                                 + "every character except some — the two cannot be listed together",
                                   $@"write '\{shorthand}' on its own, outside the brackets");
                }

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
                    for (char c = lo; c <= hi; c++) admitted.Add(c);
                    continue;
                }
                admitted.Add(lo);
            }

            if (AtEnd || Peek != ']')
                throw fail("this pattern opens a class that is never closed",
                           "every '[' needs a ']' after it",
                           @"write '\[' if you meant the character");
            _at++;

            // ⚠ Both empty forms are refused, and each is a typo in its own direction: an empty
            // class admits nothing so the pattern could never match, while an empty NEGATION
            // excludes nothing and so means "any character at all" — which `.` already says, more
            // clearly. Neither is ever what someone meant to write.
            if (admitted.Count == 0)
                throw negated
                    ? fail("this class excludes nothing",
                           "a negated class with no characters in it admits every character, which '.' already says",
                           "write '.' for any character, or list the characters to exclude")
                    : fail("this class is empty",
                           "a class with no characters in it admits nothing, so the pattern could never match",
                           "list the characters it should admit");

            string chosen = Folded(new string(admitted.ToArray()));
            return negated ? new NoneOf(chosen) : Admitting(chosen);
        }

        private static Node Add(Node? built, char c) =>
            built is null ? new Ch(c.ToString()) : new Or(built, new Ch(c.ToString()));

        /// <summary>An alternation over every character in <paramref name="members"/>.</summary>
        private static Node Admitting(string members)
        {
            Node? built = null;
            foreach (char c in members) built = Add(built, c);
            return built!;
        }

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
                           "inside a class a backslash may precede a backslash, a bracket, a dash, "
                         + @"a caret, or one of the shorthands \d \w \s",
                           TypedDirectly.TryGetValue(next, out var whitespace)
                               ? $"write {whitespace} straight into the class — it needs no escape"
                               : $"write '{next}' on its own if you meant the character");
            _at += 2;
            return next;
        }

        /// <summary>The shorthand classes, each as the characters it stands for.</summary>
        /// <remarks>
        /// ★ Every one of these is REGULAR and desugars to a class that was already expressible —
        /// `\d` is `[0-9]`, `\D` is `[^0-9]` — so they cost no new state kind and no engine change.
        /// They are owed because regex has them, not because a program asked; a book on a language
        /// does not get to choose its own contents.
        ///
        /// ⚠ `\s` is the six characters Perl and PCRE agree on. Writing them out rather than
        /// asking a Unicode table keeps both backends reading one list — the same reason the case
        /// table is shared.
        /// </remarks>
        private static readonly Dictionary<char, string> Shorthand = new()
        {
            ['d'] = "0123456789",
            ['w'] = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_",
            ['s'] = " \t\n\r\f\v",
        };

        /// <summary>`\D`, `\W`, `\S` — the same set, complemented.</summary>
        private static bool IsNegatedShorthand(char c) => Shorthand.ContainsKey(char.ToLowerInvariant(c))
                                                       && char.IsUpper(c);

        /// <summary>
        /// The escapes people reach for that this pattern does not have — and the character each
        /// one was after, which CAN simply be typed.
        /// </summary>
        /// <remarks>
        /// ⚠⚠ The generic hint sent these exactly the wrong way. Someone who writes <c>\t</c>
        /// wants a TAB, and "write 't' on its own if you meant the character" hands them the
        /// letter t — advice that is not merely unhelpful but wrong, and silently so, because the
        /// letter is a perfectly valid pattern.
        ///
        /// ★ MEASURED: a literal tab and a literal newline both work inside a pattern, and a
        /// multi-line text literal is the ordinary way to hold a subject with one. So there is no
        /// capability missing here at all — only a spelling — and the hint now says which.
        /// </remarks>
        private static readonly Dictionary<char, string> TypedDirectly = new()
        {
            ['t'] = "a tab",
            ['n'] = "a line break",
            ['r'] = "a carriage return",
        };

        // ⚠⚠ Every character that LEAVES the not-yet table has to be added here in the same
        // change, or it silently stops being escapable — `IsMeta` reads that table, so a
        // metacharacter's escape lives there until the metacharacter is real. `[` and `]` made
        // this trip when classes landed; `^` and `$` made it when anchors did. Forgetting would
        // turn `\^` from "the character" into "not an escape this pattern understands", breaking
        // written programs to add a feature.
        private static bool IsMeta(char c) =>
            c is '*' or '+' or '?' or '|' or '(' or ')' or '.' or '[' or ']' or '^' or '$'
                or '{' or '}'
            || NotYet.ContainsKey(c);
    }
}
