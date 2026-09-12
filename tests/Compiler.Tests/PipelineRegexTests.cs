using Cufet.Interpreter;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `Pull a book on regex.` — the pattern language, on an automaton written in Cufet.
/// </summary>
/// <remarks>
/// <para>
/// ★★ Every test here is an oracle test for free, and that is the design rather than an effort.
/// The pattern is compiled by the front end and the engine lives in the prelude, so there is
/// exactly one parser and exactly one engine — the two backends have nothing to disagree ABOUT.
/// The alternative, reaching for .NET's `Regex` on one side and POSIX `regcomp` on the other,
/// would have shipped two dialects differing on greediness, classes and empty matches, silently,
/// on inputs nobody thought to test.
/// </para>
/// <para>
/// ⚠ The compiled form's four field names — `kind`, `ch`, `step`, `alt` — are a contract between
/// THREE places: `RegexPattern.State`, the annotations in `Prelude/regex.cufe`, and the type the
/// lowering emits. Nothing checks that they agree. These tests are what stands between them.
/// </para>
/// </remarks>
public class PipelineRegexTests : PipelineTestBase
{
    /// <summary>Runs one pattern against several subjects and pins the answers on both backends.</summary>
    private static void AssertPattern(string pattern, string expected, params string[] subjects)
    {
        var calls = string.Join(" ", subjects.Select(s => $"{{cast p on (\"{s}\")}}"));
        string src = $"""
            Pull a book on regex.
                Define regex p as [{pattern}].
                State "{calls}".
            Done.
            """;
        // ⚠ The ANSWER is pinned as well as the agreement: two backends both saying "false" would
        // agree perfectly and be wrong together.
        Assert.Equal(expected, Interpret(src));
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ⚠ The third subject is the point, and it was ADDED after a sabotage run: with only
    // "hello there" this test passed while the engine searched from position 1 ONLY, because
    // the match happens to start there. A test that names "anywhere" has to prove it.
    [Fact]
    public void ALiteral_IsFoundAnywhereInTheSubject() =>
        AssertPattern("hello", "true false true", "hello there", "goodbye", "well hello");

    [Fact]
    public void AStar_MatchesNoneOrMany() =>
        AssertPattern("a*b", "true true false", "b", "aaab", "aaa");

    [Fact]
    public void APlus_NeedsAtLeastOne() =>
        AssertPattern("a+b", "false true", "b", "aaab");

    [Fact]
    public void AnOption_TakesItOrLeavesIt() =>
        AssertPattern("colou?r", "true true false", "color", "colour", "colur");

    [Fact]
    public void AnyCharacter_MatchesExactlyOne() =>
        AssertPattern("a.c", "true false", "abc", "ac");

    [Fact]
    public void Alternation_TakesEitherSide() =>
        AssertPattern("cat|dog", "true true false", "a cat", "a dog", "a bird");

    [Fact]
    public void AGroup_RepeatsAsAWhole() =>
        AssertPattern("(ab)+c", "true false false", "ababc", "c", "abx");

    // ★ A star over a group, which is where an epsilon CYCLE appears: the group's branch can reach
    // itself without consuming anything. The engine terminates because a state already marked live
    // is not followed again — remove that check and this hangs rather than failing.
    [Fact]
    public void AStarOverAGroup_Terminates() =>
        AssertPattern("(ab)*c", "true true false", "c", "ababc", "ab");

    // ★★ THE TEST THE AUTOMATON WAS CHOSEN FOR. `a*a*a*a*b` against a run of a's with no `b` is the
    // classic catastrophic-backtracking input: a backtracking engine tries every way of dividing
    // those a's between four stars, which is exponential and the reason regex denial-of-service is
    // a category of bug. A state-set simulation visits each position once however the pattern is
    // shaped.
    //
    // ⚠ It asserts an ANSWER rather than a duration, deliberately — a timing assertion would be
    // flaky. If this ever became a backtracking engine the test would not fail, it would HANG, and
    // a hanging suite is its own loud signal.
    [Fact]
    public void APatternThatWouldHangABacktracker_IsAnsweredAtOnce() =>
        AssertPattern("a*a*a*a*b", "false true", new string('a', 24), new string('a', 24) + "b");

    // ── Character classes ────────────────────────────────────────────────────
    //
    // ★★ A class desugars to an ALTERNATION of the characters it admits, so nothing downstream
    // changed — not the construction, not the compiled record, not the engine. `[a-c]` is exactly
    // `(a|b|c)`, which was already expressible; the slice added a way to write it. These tests are
    // therefore as much about the reader as about matching.

    [Fact]
    public void AClass_AdmitsAnyOfItsCharacters() =>
        AssertPattern("[abc]x", "true true false", "bx", "cx", "dx");

    [Fact]
    public void AClass_TakesRanges() =>
        AssertPattern("[0-9]+", "true false", "abc 42", "none");

    [Fact]
    public void AClass_TakesSeveralRangesAndLooseCharacters() =>
        AssertPattern("[a-zA-Z_]+", "true true false", "  hello", "_x", "123");

    // ⚠ A '-' immediately before the ']' is an ordinary character, not a half-written range. That
    // rule is why the reader looks ahead rather than treating every '-' as a range marker.
    [Fact]
    public void ADashAtTheEnd_IsAnOrdinaryCharacter() =>
        AssertPattern("[a-]", "true true false", "a", "-", "z");

    // ★ Classes compose with everything already there, because by the time the compiler sees one
    // it is an `Or` like any other.
    [Fact]
    public void AClass_ComposesWithRepeatsAndGroups() =>
        AssertPattern("([a-c]+-)+z", "true false", "ab-c-z", "abz");

    [Fact]
    public void AClassRefusal_SaysWhichFault()
    {
        Assert.Contains("runs backwards", Refused("[z-a]").Message);
        Assert.Contains("class is empty", Refused("[]").Message);
        // ⚠ The two empty forms are typos in opposite directions, so they are refused separately:
        // `[]` admits nothing and could never match, `[^]` excludes nothing and so means `.`.
        Assert.Contains("excludes nothing", Refused("[^]").Message);
    }

    // ── Negated classes ──────────────────────────────────────────────────────
    //
    // ★★ The asymmetry is the whole story. `[abc]` desugars to `(a|b|c)` and needed no new
    // machinery at all; `[^abc]` cannot — "every character except these" is not a finite
    // alternation — so it carries a state kind of its own. Two spellings one character apart, and
    // only one of them is sugar. That is why classes shipped without touching the engine and this
    // could not.
    //
    // ★ The excluded characters are packed into the existing `ch` field rather than growing the
    // record, so the four-field contract across RegexPattern, the prelude and the lowering is
    // unchanged for a third feature running.

    [Fact]
    public void ANegatedClass_MatchesAnythingElse() =>
        AssertPattern("[^,]+", "true false", "abc", ",,,");

    [Fact]
    public void ANegatedClass_TakesRanges() =>
        AssertPattern("[^a-z]", "false true", "abc", "abZ");

    // ★★ The idiom this feature exists for: reading a field up to a delimiter. It is also proof
    // that a negated class is an ORDINARY CONSUMING STATE — the anchor and the `+` compose with it
    // for free, exactly as they would over a single character.
    [Fact]
    public void ANegatedClass_ComposesWithAnchorsAndRepeats() =>
        AssertPattern("^[^,]+,", "true false", "ab,cd", ",cd");

    // The other everyday use: a quoted run that stops at its own closing mark.
    [Fact]
    public void ANegatedClass_ReadsAQuotedRun() =>
        AssertPattern("'[^']*'", "true false", "say 'hi' now", "nothing here");

    // ⚠ A '^' is a negation ONLY at the very front of a class. Anywhere else it was an ordinary
    // character before this landed and still is — pinned because that is exactly the kind of thing
    // a new meaning for a character quietly breaks.
    [Fact]
    public void ACaretNotAtTheFront_IsStillAnOrdinaryCharacter_NowThatNegationExists() =>
        AssertPattern("[a^]x", "true true false", "^x", "ax", "bx");

    [Fact]
    public void APositiveClass_IsUnchangedByNegationExisting() =>
        AssertPattern("[abc]x", "true false", "bx", "dx");

    // ── Shorthand classes ────────────────────────────────────────────────────
    //
    // ★ Owed because regex has them, not because a program asked — a book on a language does not
    // choose its own contents. All six desugar to classes that were already expressible, so they
    // cost no state kind: `\d` is `[0-9]` and `\D` is `[^0-9]`.

    [Fact]
    public void ADigitShorthand_IsTheDigits() =>
        AssertPattern(@"^\d+$", "true false", "42", "4a");

    [Fact]
    public void ANegatedShorthand_IsEverythingElse() =>
        AssertPattern(@"\D", "false true", "123", "12a");

    [Fact]
    public void AWordShorthand_TakesLettersDigitsAndUnderscore() =>
        AssertPattern(@"^\w+$", "true false", "a_1", "a-1");

    // ⚠ `\s` is the six characters Perl and PCRE agree on, so a TAB is whitespace and not only a
    // space. Pinned because the set is written out in the source rather than asked of a library.
    [Fact]
    public void ASpaceShorthand_IncludesTabsAndNewlines() =>
        AssertPattern(@"a\sb", "true true false", "a b", "a\tb", "ab");

    [Fact]
    public void ANonSpaceShorthand_ExcludesThem() =>
        AssertPattern(@"^\S+$", "true false", "abc", "a c");

    // ★ Inside a class a shorthand contributes its whole set — `[\d-]` is how anyone writes
    // "a number, possibly signed".
    [Fact]
    public void AShorthandInsideAClass_ContributesItsSet()
    {
        AssertPattern(@"^[\d-]+$", "true false", "-42", "-4a");
        AssertPattern(@"^[\w.]+$", "true false", "a.b", "a b");
    }

    // ⚠ A NEGATED shorthand inside a class would be the union of a complement and a list, which a
    // flat list of admitted characters cannot hold. Refused by name, pointing at the spelling that
    // does work rather than leaving the reader to find it.
    [Fact]
    public void ANegatedShorthandInsideAClass_IsRefused_AndNamesTheWorkingSpelling()
    {
        var e = Refused(@"[\D]").Message;
        Assert.Contains("cannot be used inside a class", e);
        Assert.Contains("on its own, outside the brackets", e);
    }

    // ── Counts ───────────────────────────────────────────────────────────────
    //
    // ★★ The last debt, and the one that emptied the refuse-by-name table. Like a class and unlike
    // a NEGATED class, a count is pure sugar: `a{3}` is `aaa`, so the engine never learns it
    // exists.

    [Fact]
    public void AnExactCount_IsThatManyCopies() =>
        AssertPattern("^a{3}$", "true false false", "aaa", "aa", "aaaa");

    [Fact]
    public void ARangedCount_TakesAnythingBetween() =>
        AssertPattern("^a{2,4}$", "false true true false", "a", "aa", "aaaa", "aaaaa");

    [Fact]
    public void AnOpenEndedCount_HasNoUpperLimit() =>
        AssertPattern("^a{2,}$", "false true", "a", "aaaaa");

    [Fact]
    public void ACountFromZero_AllowsNone() =>
        AssertPattern("^a{0,2}$", "true true false", "", "aa", "aaa");

    // ★ Over a class and over a group, which is where counts are actually used.
    [Fact]
    public void ACount_AppliesToWhateverPrecedesIt()
    {
        AssertPattern(@"^[A-Z]{2}\d{3}$", "true false", "AB123", "A1234");
        AssertPattern("^(ab){2}$", "true false", "abab", "aba");
    }

    // ⚠⚠ Written out rather than using AssertPattern, and the reason is worth recording: that
    // helper embeds each subject in an INTERPOLATED string, so a subject containing `{` opens an
    // interpolation and the program does not parse. A verbatim `<<…>>` literal has no
    // interpolation, which is the only way to hand a brace to a pattern from a test.
    //
    // ★ The same rule the engine relies on elsewhere: `{` is meaningful to Cufet's own text
    // literals, quite separately from meaning a count inside a pattern.
    [Fact]
    public void AnEscapedBrace_IsStillAnOrdinaryCharacter_NowThatCountsExist()
    {
        const string src = """
            Pull a book on regex.
                Define regex p as [a\{b].
                State (cast p on (<<xa{by>>)) converted to text.
                State (cast p on ("ab")) converted to text.
            Done.
            """;
        Assert.Equal("true\nfalse", Interpret(src));
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── `(?…)` ───────────────────────────────────────────────────────────────

    // ★ This book's groups already capture nothing — there is no way to ask for a capture — so
    // `(?:ab)+` and `(ab)+` are the same automaton. Accepting the spelling costs nothing and lets a
    // pattern written elsewhere arrive intact.
    [Fact]
    public void ANonCapturingGroup_IsAnOrdinaryGroup() =>
        AssertPattern("^(?:ab)+$", "true true false", "ab", "abab", "aba");

    // ★★ TWO KINDS OF NO, and keeping them apart is the point. Lookaround is refused PERMANENTLY
    // because it is not regular and an automaton cannot express it; flags and captures are refused
    // FOR NOW, the way `^` once was. A reader deserves to know which they are looking at.
    [Fact]
    public void AGroupExtension_IsRefused_AndSaysWhichKindOfNo()
    {
        // PERMANENT: not regular, so no automaton can express it.
        Assert.Contains("an automaton cannot express", Refused("a(?=b)").Message);
        Assert.Contains("an automaton cannot express", Refused("(?<=a)b").Message);
        // NOT YET: regular, and the book owes it.
        Assert.Contains("cannot do yet", Refused("(?<name>a)").Message);
        // Neither: a letter that is not one of the flags there are.
        Assert.Contains("not a flag this pattern understands", Refused("(?y)a").Message);
    }

    // ── Ignoring case ────────────────────────────────────────────────────────
    //
    // ★★ Desugars, like a class and unlike a negated one: under `(?i)` the character `a` becomes
    // the alternation `(a|A)`, so the engine never learns that case-insensitivity exists.
    //
    // ⚠⚠ The casing comes from `CaseTable` — the table BOTH BACKENDS READ — and never from
    // `char.ToUpperInvariant`. .NET's casing is ICU-backed and MEASURED to differ per machine.
    // A pattern is compiled once in the front end, so .NET's would not make the two backends
    // disagree with each other; it would make the same pattern mean different things on different
    // machines, which is the one divergence this project's oracle structurally cannot see, because
    // every machine that runs the suite is en-US.

    [Fact]
    public void AnIgnoreCaseFlag_FoldsWhatFollowsIt() =>
        AssertPattern("(?i)WARN", "true true true false",
                      "a warn line", "a WARN line", "a WaRn line", "a wrn line");

    // ★ The scoped form stops at its own bracket, which is what makes the flag a scope rather than
    // a property of the whole pattern.
    [Fact]
    public void AScopedIgnoreCaseFlag_StopsAtItsGroup() =>
        AssertPattern("(?i:ab)C", "true true false", "ABC", "abC", "abc");

    [Fact]
    public void IgnoringCase_ReachesClassesToo() =>
        AssertPattern("(?i)[a-c]x", "true false", "Bx", "Dx");

    // ── Line structure: `(?s)` and `(?m)` ────────────────────────────────────
    //
    // ⚠⚠ `.` USED TO CROSS A LINE BREAK, and that was wrong rather than a choice: in every regex
    // flavour `.` stops at one unless `(?s)` says otherwise. It was a silent divergence — a reader
    // who knew regex would have written `.` and quietly got different answers on multi-line input.
    // Fixing it is a BREAKING change with no static form to refuse, so it is loud in the CHANGELOG
    // instead.
    //
    // ★ `.` needed no new state kind either: "any character except a line break" is exactly what a
    // negated class already is. Only `(?m)` reached the engine.

    [Fact]
    public void ADot_StopsAtALineBreak() =>
        AssertPattern("a.b", "false true", "a\nb", "axb");

    [Fact]
    public void TheDotAllFlag_LetsItCross() =>
        AssertPattern("(?s)a.b", "true true", "a\nb", "axb");

    // ⚠ The scoped form must not leak, and this is the case that caught a real bug: `_dotAll` was
    // added after `_fold` and was NOT saved at the group close, so the flag escaped its own
    // brackets. Every other flag test passed while that was broken.
    [Fact]
    public void AScopedDotAll_StopsAtItsGroup() =>
        AssertPattern("(?s:a)b.c", "false true", "ab\nc", "abxc");

    // ★★ `(?m)` is the one thing here that reached the ENGINE. "Is the character just consumed a
    // line break" cannot be answered from a position alone, so `reach` carries the subject now —
    // the second and last thing it has ever needed beyond the state series itself.
    [Fact]
    public void TheMultilineFlag_MakesAnchorsMeanEachLine()
    {
        AssertPattern("^beta", "false", "alpha\nbeta\ngamma");
        AssertPattern("(?m)^beta", "true", "alpha\nbeta\ngamma");
        AssertPattern("alpha$", "false", "alpha\nbeta\ngamma");
        AssertPattern("(?m)alpha$", "true", "alpha\nbeta\ngamma");
    }

    // ⚠⚠ THE NEGATIVE CASES, AND A SABOTAGE RUN IS WHY THEY EXIST. Disabling the line-start gate
    // entirely — making `(?m)^` match at every position — left every other multiline test GREEN,
    // because they all assert `true` and a gate stuck open still answers `true`. Only a case that
    // must come back FALSE can tell an open gate from a working one.
    //
    // ★ `(?m)^eta` must fail: `eta` sits inside `beta`, not at the head of a line. `(?m)alph$`
    // must fail for the mirror reason.
    [Fact]
    public void MultilineAnchors_StillRefuseTheMiddleOfALine()
    {
        AssertPattern("(?m)^eta", "false", "alpha\nbeta\ngamma");
        AssertPattern("(?m)alph$", "false", "alpha\nbeta\ngamma");
    }

    // ★ A whole LINE, which is what `^…$` under `(?m)` is for — and the first and last lines count
    // as lines, which is where an off-by-one in either gate would show.
    [Fact]
    public void BothMultilineAnchors_MeanAWholeLine() =>
        AssertPattern("(?m)^beta$", "true false false",
                      "alpha\nbeta\ngamma", "alpha\nbetax\ngamma", "alpha\nxbeta\ngamma");

    [Fact]
    public void MultilineAnchors_CountTheFirstAndLastLines()
    {
        AssertPattern("(?m)^alpha$", "true", "alpha\nbeta\ngamma");
        AssertPattern("(?m)^gamma$", "true", "alpha\nbeta\ngamma");
    }

    // Flags combine, in one bracket or several.
    [Fact]
    public void Flags_Combine() =>
        AssertPattern("(?im)^BETA$", "true", "alpha\nbeta\ngamma");

    [Fact]
    public void AnUnknownFlag_IsRefusedByName() =>
        Assert.Contains("not a flag this pattern understands", Refused("(?x)a").Message);

    [Fact]
    public void ACountRefusal_SaysWhichFault()
    {
        Assert.Contains("counts down", Refused("a{3,1}").Message);
        Assert.Contains("no number in it", Refused("a{}").Message);
        Assert.Contains("never closed", Refused("a{2").Message);
        // ⚠ `{0}` leaves nothing behind, and an empty pattern is already refused as a typo.
        Assert.Contains("repeats nothing zero times", Refused("a{0}").Message);
        // ⚠ Unlike a class, a count's state cost is unbounded by what is written.
        Assert.Contains("more than this pattern will build", Refused("a{5000}").Message);
    }

    // ⚠ `[` and `]` left the not-yet table when classes landed, so this pins that they stayed
    // ESCAPABLE — the character has to remain writable now that the bracket is real syntax.
    [Fact]
    public void AnEscapedBracket_IsStillAnOrdinaryCharacter_NowThatClassesExist() =>
        AssertPattern(@"\[[0-9]\]", "true false", "a[7] b", "a7b");

    // ── Escapes ──────────────────────────────────────────────────────────────

    // ★ The reason the lexer learned to skip an escaped bracket: `[` is a subscript in C and a
    // character class in a pattern, so `[\[]` had to become writable before this book could exist.
    [Fact]
    public void AnEscapedBracket_IsAnOrdinaryCharacter() =>
        AssertPattern(@"\[", "true false", "a[b", "abc");

    [Fact]
    public void AnEscapedMetacharacter_IsTheCharacterItself() =>
        AssertPattern(@"a\*b", "true false", "xa*by", "ab");

    [Fact]
    public void AnEscapedGroupMarker_IsTheCharacterItself() =>
        AssertPattern(@"\(x\)", "true false", "f(x)", "fx");

    // ── What is refused, and refused WHERE IT IS WRITTEN ─────────────────────
    //
    // ★★ This is why a pattern is written in brackets instead of handed over as text. The pattern
    // is fixed at its declaration, so it can be read before the program runs. A function taking
    // `text` could not promise that: its argument is not known until it has one.

    private static Exception Refused(string pattern) =>
        Assert.ThrowsAny<Exception>(() => Interpret($"""
            Pull a book on regex.
                Define regex p as [{pattern}].
            Done.
            """));

    // ⚠⚠ THIS TEST HAS SHED AN ENTRY TWICE NOW, and that is the point rather than churn. It
    // once asserted `a*` was refused — repeats landed. It then asserted `[a-z]` was refused —
    // classes landed. Each time the refusal became support and every program written against
    // it still meant what it meant, because the refusal named the construct instead of
    // quietly treating it as ordinary characters. Reading `a*` as two characters, or `[a-z]`
    // as five, would have changed meaning in silence on the day each shipped.
    // ⚠⚠ THIS TEST IS GONE, AND ITS DISAPPEARANCE IS THE RESULT.
    //
    // It once asserted that `a*`, `[a-z]`, `^ab`, `ab$` and `a{2}` were each refused by name. Every
    // one of them is now supported, and — this is the whole point — **not one pattern written
    // against those refusals changed meaning on the day it landed.** Reading `a*` as two
    // characters, `[a-z]` as five, or `^ab` as a caret and two letters would each have been a
    // silent reinterpretation of somebody's working program.
    //
    // ★ The `NotYet` table is empty now. It stays in the source because the next thing regex has
    // and this book does not will need it, and because an empty table is a clearer statement than
    // a deleted one: nothing is currently known-missing and refused.

    // ★★ What is refused now is what an AUTOMATON cannot express, which is a different kind of
    // limit — permanent and principled rather than "not yet". Backreferences and lookaround are
    // exactly the features that make a "regular expression" not regular, and the book says so on
    // its cover rather than pretending otherwise.

    // ── Anchors ──────────────────────────────────────────────────────────────
    //
    // ★★ THE SLICE A REAL PROGRAM ASKED FOR. `logtriage.cufe` could count and flag but not
    // VALIDATE, because a pattern asks whether a subject HOLDS a match and there was no way to say
    // "and that is the whole of it". `[[0-9]+]` was held by `abc 42` as firmly as by `42`.
    //
    // ★ They are STATE KINDS, not flags on the pattern. Flags would have been less work and could
    // only anchor the very ends; as states they are ordinary nodes, so they compose with
    // alternation and grouping for nothing extra. `AnAnchor_ComposesInsideAnAlternation` is the
    // test that would be impossible under the other design, and it is why the fork went this way.

    [Fact]
    public void AStartAnchor_MatchesOnlyAtTheBeginning() =>
        AssertPattern("^abc", "true false", "abcxx", "xxabc");

    [Fact]
    public void AnEndAnchor_MatchesOnlyAtTheEnd() =>
        AssertPattern("abc$", "true false", "xxabc", "abcxx");

    // ★ Both together is the spelling that was missing: "the subject IS this", not "holds it".
    [Fact]
    public void BothAnchors_MeanTheWholeSubject() =>
        AssertPattern("^[0-9]+$", "true false false", "42", "abc 42", "42 abc");

    [Fact]
    public void AnAnchor_ComposesInsideAnAlternation() =>
        AssertPattern("(^cat|dog$)", "true true false false",
                      "cat here", "a dog", "a cat", "dog here");

    // ⚠ An empty subject is the one place "nothing consumed" and "everything consumed" are the
    // same position, so both gates open at once. Worth pinning: it is the edge where an
    // off-by-one in the anchor comparison would show up and nowhere else.
    [Fact]
    public void BothAnchors_OnAnEmptySubject_Match() =>
        AssertPattern("^$", "true false", "", "x");

    // ⚠⚠ `^` and `$` left the not-yet table, and every character that does must stay ESCAPABLE or
    // a written program silently breaks — `IsMeta` reads that table, so the escape lives there
    // until the character is real syntax. `[` and `]` made this trip when classes landed.
    [Fact]
    public void AnEscapedAnchor_IsStillAnOrdinaryCharacter_NowThatAnchorsExist()
    {
        AssertPattern(@"a\^b", "true false", "xa^by", "ab");
        AssertPattern(@"a\$b", "true false", "xa$by", "ab");
    }

    // ★ A caret is only special as the FIRST thing in a class, where it would mean negation.
    // Anywhere else it was an ordinary character before anchors and still is.
    [Fact]
    public void ACaretInsideAClass_IsStillAnOrdinaryCharacter() =>
        AssertPattern("[a^]x", "true true false", "^x", "ax", "bx");

    [Fact]
    public void AnUnknownEscape_IsRefused() =>
        Assert.Contains(@"'\q' is not an escape", Refused(@"\q").Message);

    // ⚠⚠ `\t` is refused like any unknown escape, but the HINT used to say "write 't' on its own
    // if you meant the character" — which hands the letter t to someone who wanted a tab. Wrong
    // advice, and silently so, because `t` is a perfectly valid pattern that simply matches
    // something else. There is no capability missing here: a tab may be typed straight in.
    [Fact]
    public void AWhitespaceEscape_IsRefused_ButSaysTheCharacterCanBeTypedInstead()
    {
        var tab = Refused(@"a\tb").Message;
        Assert.Contains(@"'\t' is not an escape", tab);
        Assert.Contains("a tab straight into the pattern", tab);
        Assert.DoesNotContain("write 't' on its own", tab);

        Assert.Contains("a line break straight into the pattern", Refused(@"a\nb").Message);
        // Inside a class the hint has to be right too, and says "class" rather than "pattern".
        Assert.Contains("a tab straight into the class", Refused(@"[a\t]").Message);
    }

    // ★ The other half of that message, and the half that makes it true: a literal tab IS a
    // pattern character, in a class as well as outside one. If this ever stopped holding, the hint
    // above would become the very kind of advice it was written to replace.
    [Fact]
    public void ALiteralTab_IsAnOrdinaryCharacter_InAPatternAndInAClass()
    {
        AssertPattern("name\tvalue", "true false", "name\tvalue", "name value");
        AssertPattern("a[\t-]b", "true true false", "a\tb", "a-b", "a b");
    }

    [Fact]
    public void AnEmptyPattern_IsRefused() =>
        Assert.Contains("empty", Refused("").Message);

    // ⚠ `a|` would mean "a, or nothing at all", which matches every subject including the empty
    // one — the least useful answer available, and the same reason an empty pattern is refused.
    [Fact]
    public void AnEmptyAlternative_IsRefused()
    {
        Assert.Contains("empty alternative", Refused("a|").Message);
        Assert.Contains("empty alternative", Refused("|a").Message);
    }

    [Fact]
    public void AnUnbalancedGroup_IsRefused()
    {
        Assert.Contains("never closed", Refused("(ab").Message);
        Assert.Contains("never opened", Refused("ab)").Message);
    }

    [Fact]
    public void ARepeatWithNothingBeforeIt_IsRefused() =>
        Assert.Contains("nothing before it to repeat", Refused("*a").Message);

    // ⚠ A value is spliced INTO a pattern, never handed to it — the same guarantee
    // `run "grep" with arguments (…)` makes, and why injection is structurally impossible here.
    [Fact]
    public void APattern_CannotBeGivenParameters()
    {
        var e = Assert.ThrowsAny<Exception>(() => Interpret("""
            Pull a book on regex.
                Define regex p, given (the text x), as [hello].
            Done.
            """));
        Assert.Contains("cannot be 'given' anything", e.Message);
    }

    // ★★ THE GATE, and it needs its own test because the lowering is what nearly lost it. A pattern
    // becomes an ordinary function in the parser, and after that there is no literal left for the
    // checker to ask about — so this was silently ALLOWED until the lowered function started
    // carrying its language with it. Measured, not hypothetical.
    [Fact]
    public void APattern_RequiresItsBookToBePulled()
    {
        var e = Assert.Throws<TypeException>(() => Interpret("""
            Define regex p as [hello].
            State (cast p on ("hello")) converted to text.
            """));
        Assert.Contains("regex book is not in scope", e.Message);
        // ⚠ The suggestion has to be a line the reader would have written. `regex` takes no
        // article; `the c-language` does. That was the wrong way round when this first ran.
        Assert.Contains("Pull a book on regex.", e.Message);
    }

    // ★★ The engine is a PRIVATE top-level helper of its prelude file, renamed by file privacy to a
    // name with a space in it — so "a language book has no members" stays literally true, and no
    // program can reach the engine or collide with it.
    [Fact]
    public void TheEngine_IsNotReachableAsAMemberOrAName()
    {
        Assert.ThrowsAny<Exception>(() => Interpret("""
            Pull a book on regex.
                State (cast regex's match on ("x")) converted to text.
            Done.
            """));
        // A program may still use `match` for whatever it likes — the engine's real name is
        // unwritable, so nothing was taken away.
        Assert.Equal("mine", Interpret("""
            Bind text to match:
                Return "mine".
            Done.
            State cast match.
            """));
    }
}
