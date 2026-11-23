using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// What a parse refusal CALLS things, when it has to name them to a person.
/// </summary>
/// <remarks>
/// ⚠⚠ `Consume` used to put the raw enum name into the message — `expected Dot`, `expected Colon`,
/// `expected Identifier`. Nobody outside this repo knows what a `Dot` is, and punctuation is the
/// first thing a beginner gets wrong, so lexer vocabulary was the first vocabulary they met.
///
/// <para>★ Only the JARGON needs translating. A keyword's enum name is the keyword itself, so
/// `expected Done` and `expected As` already read correctly and are left alone — the fix is as
/// narrow as the problem.</para>
/// </remarks>
public class ParseWordingTests
{
    private static ParseException ParseFails(string source) =>
        Assert.Throws<ParseException>(() => new Parser(new CufetLexer(source).Tokenize()).Parse());

    /// <remarks>
    /// ⚠ THE PROGRAM CHANGED, THE SUBJECT DID NOT. This was `State "hello"` with `State "world".`
    /// on the next line, until a line ending without its period got a message of its own
    /// (below). Two words on ONE line still reach the plain `Consume(TokenType.Dot)` wording.
    /// </remarks>
    [Fact]
    public void AMissingFullStop_AsksForTheCharacter_NotForADot()
    {
        var ex = ParseFails("""State "hello" "world".""");

        Assert.Contains("expected '.'", ex.Message);
        Assert.DoesNotContain("expected Dot", ex.Message);
    }

    // —— An `Otherwise` no `If` took ————————————————————————————————————————————————————

    [Theory]
    [InlineData("Otherwise, state \"Go\".", 1)]
    [InlineData("Define hops as 5.\nIf hops is 5, state \"a\".\nState \"between\".\nOtherwise, state \"b\".", 4)]
    [InlineData("Define hops as 5.\nIf hops is 5:\n    State \"a\".\nDone.\nState \"between\".\nOtherwise:\n    State \"b\".\nDone.", 6)]
    public void AStrayOtherwise_SaysItBelongsStraightAfterAnIf(string source, int line)
    {
        var ex = ParseFails(source);
        Assert.Equal($"Line {line}, column 1: 'Otherwise' has to come straight after an 'If' (after its 'Done.', in the "
                   + "block form), with nothing in between. Is something between them, or is the 'If' missing?", ex.Message);
    }

    // —— A lowercase `else`, which the lexer lets through as a name ————————————————————————

    [Theory]
    [InlineData("If true, state \"a\".\nelse, state \"b\".", "else", "Otherwise")]
    [InlineData("If true, state \"a\".\nelse if true, state \"b\".", "else", "Otherwise")]
    [InlineData("If true, state \"a\".\nelif true, state \"b\".", "elif", "Otherwise if")]
    public void ALowercaseElse_IsToldCufetsWord(string source, string word, string own)
    {
        var ex = ParseFails(source);
        Assert.Equal($"Line 2, column 1: '{word}' isn't a word Cufet knows to start a line with. Cufet says '{own}'.", ex.Message);
    }

    /// <remarks>★ The guard: `else` is an ordinary name, and only a refusal is reworded.</remarks>
    [Fact]
    public void ElseAsAName_StillParses() => Parses("Define else as 1.\nelse becomes 3.\nState else.");

    // —— An arm's question with neither a comma nor a colon ————————————————————————————
    //
    // ⚠ `If hops is greater than 3 state "Rest!".` got `expected ':', got State "state"` — one of
    // the two choices, and the one that player did not want; with the block below, it named the
    // block's first line, as the missing period did.

    [Theory]
    [InlineData("Define hops as 5.\nIf hops is greater than 3 state \"Rest!\".", 2, 27, "state")]
    [InlineData("Define hops as 5.\nIf hops is greater than 3, state \"Rest!\".\nOtherwise state \"Go\".", 3, 11, "state")]
    [InlineData("Define hops as 5.\nIf hops is greater than 3 the hops becomes 1.", 2, 27, "the")]
    public void SomethingOnTheLine_IsToldBothChoices(string source, int line, int column, string word)
    {
        var ex = ParseFails(source);
        Assert.Equal($"Line {line}, column {column}: Cufet needs a comma or a colon before '{word}': a comma for one "
                   + "instruction on this line, or a colon to start a block that ends with 'Done.'.", ex.Message);
    }

    [Fact]
    public void NothingMoreOnTheLine_IsToldToEndItWithAColon()
    {
        var ex = ParseFails("Define hops as 5.\nIf hops is greater than 3\n    State \"Rest!\".\nDone.");
        Assert.Equal("Line 2, column 25: this line needs a colon at the end, to start its block. "
                   + "Cufet read on into line 3 looking for it.", ex.Message);
    }

    [Fact]
    public void TheFileEndingAfterTheQuestion_SaysSo()
    {
        var ex = ParseFails("Define hops as 5.\nIf hops is greater than 3");
        Assert.Equal("Line 2, column 25: this line needs a colon at the end, to start its block. "
                   + "The file ended before one came.", ex.Message);
    }

    [Fact]
    public void Then_IsToldToBeAComma()
    {
        var ex = ParseFails("Define hops as 5.\nIf hops is greater than 3 then state \"Rest!\".");
        Assert.Equal("Line 2, column 27: Cufet doesn't use 'then': write a comma where it is.", ex.Message);
    }

    // —— `is` or `as` where `becomes` belonged ——————————————————————————————————
    //
    // ⚠ `The hops is hops + 1.` got `expected Becomes, got Is "is"`. Every way of changing a value
    // waits for `becomes` in one place, so the name, the field, `the … of …` and the item are
    // all one check.

    [Theory]
    [InlineData("Define hops as 1.\nThe hops is hops + 1.", 2, 10)]
    [InlineData("Define hops as 1.\nhops is 5.", 2, 6)]
    [InlineData("Define object rabbit with (the number age).\nDefine grace as a new rabbit { the age 3 }.\ngrace's age is 4.", 3, 13)]
    [InlineData("Define object rabbit with (the number age).\nDefine grace as a new rabbit { the age 3 }.\nThe age of grace is 4.", 3, 18)]
    [InlineData("Define xs as a series of number with (1, 2).\nItem 1 of xs is 5.", 2, 14)]
    public void IsWhereBecomesBelonged_SaysToSwapTheWord(string source, int line, int column)
    {
        var ex = ParseFails(source);
        Assert.Equal($"Line {line}, column {column}: 'is' asks a question, it doesn't change anything. "
                   + "To give this a new value, write 'becomes' instead of 'is'.", ex.Message);
    }

    [Fact]
    public void AsWhereBecomesBelonged_SaysToSwapTheWord()
    {
        var ex = ParseFails("Define hops as 1.\nThe hops as 5.");
        Assert.Equal("Line 2, column 10: 'as' belongs to 'Define', which makes a new name. To give a name "
                   + "that already exists a new value, write 'becomes' instead of 'as'.", ex.Message);
    }

    /// <remarks>★ The guard: an inline void body holding an expression keeps the message about
    /// inline bodies, which used to catch this refusal by its old words.</remarks>
    [Fact]
    public void AnInlineVoidBodyHoldingIs_KeepsTheInlineBodyMessage()
    {
        var ex = ParseFails("Define hops as 1.\nBind void to g, hops is 5.");
        Assert.Contains("this body gives nothing back, so its inline form is a STATEMENT", ex.Message);
    }

    // —— A line that ended without its period ————————————————————————————————
    //
    // ⚠⚠ Reported where Cufet NOTICED, which is the next line with anything on it: `State "Hi"`
    // followed by two blank lines was refused at line 4. The commonest slip in the tutorial's
    // play-tests, and "the error points at the wrong line" twice. Now reported at the line
    // missing it, at the start of its last word (a token records only where it starts).

    [Fact]
    public void ALineWithoutItsPeriod_IsReportedAtThatLine_PastBlankLines()
    {
        var ex = ParseFails("State \"Hi\"\n\n\nState \"There\".");

        Assert.StartsWith("Line 1, column 7: this line needs a period at the end.", ex.Message);
        Assert.Contains("Cufet read on into line 4 looking for it.", ex.Message);
        Assert.Equal(1, ex.Line);
    }

    [Fact]
    public void ALineWithoutItsPeriod_PastAComment_IsReportedAtThatLine()
    {
        var ex = ParseFails("State \"Hi\" // a note\nState \"There\".");

        Assert.StartsWith("Line 1, column 7: this line needs a period at the end.", ex.Message);
    }

    [Fact]
    public void ADoneWithoutItsPeriod_IsReportedAtTheDone()
    {
        var ex = ParseFails("If 2 is 2:\n    State \"a\".\nDone\nState \"b\".");

        Assert.StartsWith("Line 3, column 1: this line needs a period at the end.", ex.Message);
    }

    [Theory]
    [InlineData("State \"Hi\"")]
    [InlineData("State \"Hi\"\n")]
    public void TheLastLineWithoutItsPeriod_SaysTheFileEnded(string source)
    {
        var ex = ParseFails(source);

        Assert.Equal("Line 1, column 7: this line needs a period at the end. The file ended before one came.", ex.Message);
    }

    private static Cufet.Interpreter.Program Parses(string source) =>
        new Parser(new CufetLexer(source).Tokenize()).Parse();

    /// <remarks>⚠ THE PROGRAM CHANGED, THE SUBJECT DID NOT. This was an `If` with no colon until an
    /// arm's question got a message of its own (below); `Try to` is still a plain `Consume`.</remarks>
    [Fact]
    public void AMissingColon_AsksForTheCharacter()
    {
        var ex = ParseFails("""
            Try to
                State "hi".
            Done.
            """);

        Assert.Contains("expected ':'", ex.Message);
        Assert.DoesNotContain("expected Colon", ex.Message);
    }

    /// <remarks>
    /// ★ The guard against translating too much. `Done` and `As` are words a reader has typed, so
    /// the enum name IS the right word and the map must leave them alone.
    ///
    /// ⚠⚠ THE PROGRAM CHANGED, THE SUBJECT DID NOT. This used to be written `Define carrots = 3.`
    /// — until that shape got an educational message of its own and stopped reaching
    /// `expected As` at all. `Define carrots to 3.` is the same `Consume(TokenType.As)` at the same
    /// call site, reached by a wrong word rather than by the one mistake now handled ahead of it.
    /// The assertion is untouched: a test whose fixture is only a VEHICLE gets a new vehicle, never
    /// a weaker claim.
    /// </remarks>
    [Fact]
    public void AKeywordsOwnNameIsLeftAlone()
    {
        var ex = ParseFails("Define carrots to 3.");
        Assert.Contains("expected As", ex.Message);
    }

    /// <summary>A reserved word used as a name says so, rather than naming token types.</summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ THE ONE WITH A WITNESS. `expected Identifier, got Key "key"` leaves a reader to deduce
    /// that `key` is taken. MEASURED across two sessions of writing Cufet: six collisions — `a`,
    /// `one`, `channel`, `key`, `arguments`, `from` — every one an everyday noun somebody would
    /// reach for, and every one costing a parse error and a rename.
    /// </para>
    /// <para>
    /// ★ `channel`, `key` and `entry` were GIVEN BACK on 2026-09-23 and moved out of these
    /// theories to <c>FreedWordTests</c>; `length` and `size` followed on 2026-09-24 and were
    /// replaced here in turn. The words pinned here are the ones still taken, so this test keeps
    /// checking the MESSAGE rather than the list. ⚠ That churn is the point — a theory naming
    /// freed words would pass while asserting the opposite of what the language does.
    /// </para>
    /// <para>
    /// ★ It needs no keyword table. The lexer turns a bare word into an `Identifier` unless the
    /// language has taken that word, so a WORD arriving as anything else is reserved by
    /// construction.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("arguments")]
    [InlineData("range")]
    [InlineData("end")]
    public void AReservedWordAsAName_SaysItIsReserved(string word)
    {
        var ex = ParseFails($"Define {word} as 3.");

        Assert.Contains($"'{word}' is a word Cufet has taken", ex.Message);
        Assert.Contains("neither a variable nor a field", ex.Message);
        Assert.DoesNotContain("expected Identifier", ex.Message);
    }

    /// <remarks>
    /// ⚠ The guard against the heuristic going wide. A bracket or a number arriving where a name
    /// was wanted is a DIFFERENT mistake, and telling someone that `(` is a reserved word would be
    /// false. Letters only.
    /// </remarks>
    [Fact]
    public void SomethingThatIsNotAWord_GetsTheOrdinaryMessage()
    {
        var ex = ParseFails("Define 5 as 3.");

        Assert.DoesNotContain("word Cufet has taken", ex.Message);
        Assert.Contains("expected a name", ex.Message);
    }
    // —— A reserved word where the name slot is OPTIONAL ————————————————————
    //
    // ⚠⚠ THE ME★AGE ABOVE WAS NARROWER THAN IT LOOKED. It fires where the parser D—ANDS an
    // `Identifier`, and a loop's iterator name is optional — `For each in xs, state it.` is a
    // real program. So `For each entry in xs` never consumed an identifier at all, fell through to
    // `Consume(TokenType.In)`, and answered `expected In, got Entry "entry"`: a token type, and
    // the wrong slot blamed.
    //
    // ★★ The rule that fixes it is the same one the message already rests on, read the other way
    // round: the slot takes EVERY identifier, so a word-shaped token still sitting there is
    // reserved by construction. No keyword table, no list to maintain.

    /// <remarks>⚠ Everyday nouns in the one position a beginner writes first. `entry` and `key`
    /// stood here until 2026-09-23; both are names again, and `FreedWordTests` pins that.</remarks>
    [Theory]
    [InlineData("path")]
    [InlineData("file")]
    [InlineData("start")]
    public void AReservedWordAsALoopVariable_SaysItIsReserved(string word)
    {
        var ex = ParseFails($"Define xs as a series of number with (1, 2). For each {word} in xs, state {word}.");

        Assert.Contains($"'{word}' is a word Cufet has taken", ex.Message);
        Assert.DoesNotContain("expected In", ex.Message);
    }

    /// <remarks>
    /// ⚠⚠ THE LINE THE GUARD MUST NOT CRO★. The iterator name is genuinely optional, so `In` and
    /// `From` may legitimately follow the slot — firing on those would refuse two working
    /// programs. Both are pinned here rather than left to the suite at large.
    /// </remarks>
    [Fact]
    public void TheBareLoopForms_StillParse()
    {
        Parses("""
            Define xs as a series of number with (1, 2).
            For each in xs, state it.
            """);

        Parses("For each line from the input, state line.");
    }

    /// <remarks>★ And a name that is merely ordinary is still taken, rather than the guard
    /// grabbing at anything word-shaped.</remarks>
    [Fact]
    public void AnOrdinaryLoopVariable_IsUnaffected()
    {
        Parses("""
            Define xs as a series of number with (1, 2).
            For each n in xs, state n.
            """);
    }
}
