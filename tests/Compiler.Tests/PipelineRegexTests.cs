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
        // ⚠ Negation cannot be desugared — "every character except these" is not a finite
        // alternation — so it is refused by name until it has a real representation.
        Assert.Contains("cannot do yet", Refused("[^a-z]").Message);
        Assert.Contains("runs backwards", Refused("[z-a]").Message);
        Assert.Contains("class is empty", Refused("[]").Message);
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
    [Fact]
    public void AMetacharacterNotYetSpelled_IsRefusedByName()
    {
        Assert.Contains("means a count", Refused("a{2}").Message);
        // ⚠ Only `{` and `}` are left. When counts land this test goes with them, and the shedding
        // will have finished — which is the point of writing refusals this way rather than a
        // catch-all "unsupported character".
    }

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
