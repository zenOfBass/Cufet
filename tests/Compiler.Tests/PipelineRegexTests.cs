using Cufet.Interpreter;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `Pull a book on regex.` — slice 1: patterns of ordinary characters, read at check time.
/// </summary>
/// <remarks>
/// <para>
/// ★★ A language book Cufet READS ITSELF. The pattern is parsed by the front end and lowered to an
/// ordinary function before either backend meets the program, which is why every test here can be
/// an oracle test for free: there is only one meaning, so there is nothing for two backends to
/// disagree about. That was the deciding argument for the design — two engines (.NET `Regex` one
/// side, POSIX `regcomp` the other) would differ on greediness, classes and empty-match edges,
/// silently, on inputs nobody wrote a test for.
/// </para>
/// <para>
/// ⚠ SLICE 1 MATCHES LITERALS, and the lowering is `subject contains "…"`. That is deliberately not
/// an engine. What this slice proves is the MECHANISM — the book, the tag, the brackets, check-time
/// validation, the pull gate, one shared meaning — with the matching kept trivial on purpose.
/// </para>
/// </remarks>
public class PipelineRegexTests : PipelineTestBase
{
    [Fact]
    public void APattern_MatchesAndDoesNotMatch_TheSameOnBothBackends()
    {
        const string src = """
            Pull a book on regex.
                Define regex greeting as [hello].
                State (cast greeting on ("hello there")) converted to text.
                State (cast greeting on ("goodbye")) converted to text.
            Done.
            """;
        // ⚠ The ANSWER is pinned as well as the agreement: two backends that both said "false"
        // would agree perfectly and be wrong together.
        Assert.Equal("true\nfalse", Interpret(src));
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ★ The reason the lexer learned to skip an escaped bracket. `[` is a subscript in C and a
    // character class in a pattern, so `[\[]` had to become writable before this book could exist
    // at all — and here it is, end to end: the escape survives the lexer, the pattern parser turns
    // it back into one ordinary character, and the match is on that character.
    [Fact]
    public void AnEscapedBracket_IsAnOrdinaryCharacter()
    {
        const string src = """
            Pull a book on regex.
                Define regex opener as [\[].
                State (cast opener on ("a[b")) converted to text.
                State (cast opener on ("abc")) converted to text.
            Done.
            """;
        Assert.Equal("true\nfalse", Interpret(src));
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ── What is refused, and refused WHERE IT IS WRITTEN ─────────────────────
    //
    // ★★ This is the whole reason a pattern is written in brackets instead of handed over as text.
    // The pattern is fixed at its declaration, so it can be read before the program runs. An
    // ordinary function taking `text` could never promise this: its argument is not known until it
    // has one.

    [Fact]
    public void AMetacharacter_IsRefusedByName_RatherThanTakenLiterally()
    {
        var e = Assert.ThrowsAny<Exception>(() => Interpret("""
            Pull a book on regex.
                Define regex p as [a*].
            Done.
            """));
        Assert.Contains("'*' means a repeat", e.Message);
        Assert.Contains("cannot do yet", e.Message);
    }

    // ⚠ The ordering that makes the refusal above the right one. Treating `a*` as three literal
    // characters would be the other kind of decision — one that silently changes meaning the day
    // quantifiers land. A refusal saying "not yet" becomes support later and every program written
    // against it keeps meaning what it meant.
    [Fact]
    public void AnEscapedMetacharacter_IsTheCharacterItself()
    {
        const string src = """
            Pull a book on regex.
                Define regex star as [a\*b].
                State (cast star on ("xa*by")) converted to text.
                State (cast star on ("ab")) converted to text.
            Done.
            """;
        Assert.Equal("true\nfalse", Interpret(src));
        Assert.Equal(Interpret(src), Compile(src));
    }

    // ⚠ An unknown escape is refused rather than quietly meaning the bare character, for the same
    // reason: `\d` will mean digits one day, and meaning 'd' until then is a change in silence.
    [Fact]
    public void AnUnknownEscape_IsRefused()
    {
        var e = Assert.ThrowsAny<Exception>(() => Interpret("""
            Pull a book on regex.
                Define regex p as [\q].
            Done.
            """));
        Assert.Contains(@"'\q' is not an escape", e.Message);
    }

    // ⚠ Empty would match everywhere, including against the empty subject — the least useful
    // answer available, and far likelier to be a typo than an intention.
    [Fact]
    public void AnEmptyPattern_IsRefused()
    {
        var e = Assert.ThrowsAny<Exception>(() => Interpret("""
            Pull a book on regex.
                Define regex p as [].
            Done.
            """));
        Assert.Contains("empty", e.Message);
    }

    // ⚠ A value is spliced INTO a pattern, never handed to it — the same guarantee
    // `run "grep" with arguments (…)` makes, and the reason injection is structurally impossible
    // here rather than merely discouraged.
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
}
