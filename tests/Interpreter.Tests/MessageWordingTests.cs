using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Refusals whose SENTENCE came out wrong, though the refusal itself was right. Found writing the
// Cufet parser in Cufet, which met most of them from the other side.
public class MessageWordingTests
{
    private static string ParseFails(string source) =>
        Assert.Throws<ParseException>(() => new Parser(new CufetLexer(source).Tokenize()).Parse()).Message;

    private static string CheckFails(string source)
    {
        var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        return Assert.Throws<TypeException>(() => new TypeChecker().Check(program)).Message;
    }

    // ⚠ A sentence handed to the "expected X, got Y" constructor printed as
    // "expected 'Stop' used outside a loop, got Stop" — or, where the sentence already began with
    // "expected", as "expected expected …".
    [Theory]
    [InlineData("Stop.", "'Stop' is used outside a loop")]
    [InlineData("Skip.", "'Skip' is used outside a loop")]
    [InlineData("Return 5.", "'Return' is used outside a function")]
    [InlineData("Define p as the path \"x\" is a number.", "expected 'directory' or 'file'")]
    [InlineData("Define r as run \"x\" with arguments (\"a\") with arguments (\"b\").", "this run already says 'with arguments'")]
    [InlineData("Bind overloading %, given (the a is a number, the b is a text), 1.", "expected an arithmetic operator")]
    [InlineData("Append \"x\" to stdout.", "expected 'the file \"path\"' after 'append <value> to'")]
    public void ASentenceIsNotFramedAsAnExpectation(string source, string sentence)
    {
        var message = ParseFails(source);
        Assert.Contains(sentence, message);
        Assert.DoesNotContain("expected expected", message);
        Assert.DoesNotContain(", but found", message);
    }

    [Theory]
    [InlineData("If true:\n    Bind unmaking a box to tidy, State \"x\".\nDone.", "unmakers must be declared at the top level")]
    [InlineData("Bind unmaking a box to tidy given (the number n), State \"x\".", "unmakers take no parameters")]
    public void AnUnmakerRefusal_DoesNotOpenOnADash(string source, string sentence)
    {
        var message = ParseFails(source);
        Assert.Contains(sentence, message);
        Assert.DoesNotContain("— unmakers", message);
        Assert.DoesNotContain("expected", message);
    }

    /// <summary>
    /// A type nobody defined, tested for. It used to be accepted and simply never true, so a misspelled
    /// type in an `If` or a `Judge` arm was a branch that could not run and nothing said so.
    /// </summary>
    [Theory]
    [InlineData("Define the (number or text) x as 5.\nState x is a nonsense.", 2, "nonsense", null)]
    [InlineData("Define the (number or text) x as 5.\nState x is not a nonsense.", 2, "nonsense", null)]
    [InlineData("Define the (number or text) x as 5.\nIf x is a nubmer, state 1.", 2, "nubmer", "number")]
    [InlineData("Define the (number or text) x as 5.\nIf x is a text:\n    State 1.\nDone.\nOtherwise if x is a nonsense:\n    State 2.\nDone.", 5, "nonsense", null)]
    [InlineData("Define object box with (the number n).\nDefine the (number or text) x as 5.\nState x is a bxo.", 3, "bxo", "box")]
    [InlineData("Define shorthand shape as (number or text).\nDefine the (number or text) x as 5.\nState x is a shpe.", 3, "shpe", "shape")]
    [InlineData("Define interface greeter for the void function greet.\nDefine the (number or text) x as 5.\nState x is a greter.", 3, "greter", "greeter")]
    [InlineData("Define the (number or text) x as 5.\nState x is a series of nonsense.", 2, "nonsense", null)]
    [InlineData("Define the (number or text) x as 5.\nState x is a voidable nonsense.", 2, "nonsense", null)]
    [InlineData("Define the (number or text) x as 5.\nState x is a (number or nonsense).", 2, "nonsense", null)]
    [InlineData("Define the (number or text) x as 5.\nJudge x, where it is:\n    A number, state 1.\n    A nonsense, state 2.\n    Otherwise, state 3.\nDone.", 4, "nonsense", null)]
    [InlineData("Define the (number or text) x as 5.\nJudge x, where it is:\n    A number or a nonsense, state 1.\n    Otherwise, state 3.\nDone.", 3, "nonsense", null)]
    [InlineData("Define phrase as \"hi\".\nDefine the (number or text) x as 5.\nState x is a phrase.", 3, "phrase", null)]
    public void AnUndefinedTypeTestedFor_IsRefused(string source, int line, string name, string? meant)
    {
        Assert.Equal($"That doesn't work: '{name}' is not a defined type.\nThis is in what starts on line {line}.\n\n"
            + (meant is null
                ? $"Define 'object {name}' before using it as a type name, or check the spelling."
                : $"Did you mean '{meant}'? If not, define 'object {name}' before using it as a type name."), CheckFails(source));
    }

    [Theory]
    [InlineData("Define the (number or text) x as 5.\nState x is a number.\nState x is not a text.")]
    [InlineData("Bind fact to holds, given (the element e, the series of element s):\n    Define the (number or text) x as 5.\n    Return x is a element.\nDone.\nState cast holds on (1, a series of number with (1)).")]
    [InlineData("Define object box with (the number n).\nDefine c as a catalogue with (a new box { the n 1 }, 2).\nState item 1 of c is a box.\nJudge item 1 of c, where it is:\n    A box, state \"box\".\n    Otherwise, state \"no\".\nDone.")]
    [InlineData("Define shorthand shape as (number or text).\nDefine the (number or text) x as 5.\nState x is a shape.")]
    public void ADefinedTypeTestedFor_IsAccepted(string source) =>
        new TypeChecker().Check(new Parser(new CufetLexer(source).Tokenize()).Parse());

    /// <summary>A method reading a name of the function or block around it, which is no top-level value.</summary>
    [Theory]
    [InlineData("Define object box with (the number n).\nBind void to f, given (the number k):\n    Bind number to plus unto box, one's n + k.\nDone.\nCast f on (1).", 3)]
    [InlineData("Define object box with (the number n).\nBind void to f:\n    Define k as 1.\n    Bind number to plus unto box, one's n + k.\nDone.\nCast f.", 4)]
    [InlineData("Bind void to f, given (the number k):\n    Define object crate with (the number n):\n        Bind number to plus, one's n + k.\n    Done.\nDone.\nCast f on (1).", 3)]
    [InlineData("Define object box with (the number n).\nPull a rabbit.\n    Define k as 1.\n    Bind number to plus unto box, one's n + k.\nDone.", 4)]
    public void AMethodReadingAroundIt_IsToldWhoseTheNameIs(string source, int line)
    {
        Assert.StartsWith("That doesn't work: 'k' belongs to the block this method is written in, and a method can't see it.\n"
            + $"Here on line {line}, you're trying to read 'k' inside a method.\n\n", CheckFails(source));
    }

    [Fact]
    public void AMethodReadingTopLevelData_IsStillToldSo() =>
        Assert.StartsWith("That doesn't work: 'k' is a top-level value", CheckFails(
            "Define k as 1.\nDefine object box with (the number n).\nBind number to plus unto box, one's n + k."));

    /// <summary>A method using a function of the block it is written in.</summary>
    [Theory]
    [InlineData("Define object box with (the number n).\nBind void to f:\n    Bind number to g:\n        Return 1.\n    Done.\n    Bind number to plus unto box, one's n + cast g.\nDone.\nCast f.", 6)]
    [InlineData("Bind void to f:\n    Bind number to g:\n        Return 1.\n    Done.\n    Define object crate with (the number n):\n        Bind number to plus, one's n + cast g.\n    Done.\nDone.\nCast f.", 6)]
    [InlineData("Define object box with (the number n).\nBind void to f:\n    Bind number to g:\n        Return 1.\n    Done.\n    Get plus unto box as number, one's n + cast g.\nDone.\nCast f.", 6)]
    [InlineData("Define object box with (the number n).\nBind void to f:\n    Bind number to g:\n        Return 1.\n    Done.\n    Set n unto box given (the number k):\n        One's n becomes k + cast g.\n    Done.\nDone.\nCast f.", 7)]
    [InlineData("Define object box with (the number n).\nBind void to f:\n    Bind number to g:\n        Return 1.\n    Done.\n    Bind number to plus unto box:\n        Define h as g.\n        Return cast h.\n    Done.\nDone.\nCast f.", 7)]
    [InlineData("Define object box with (the number n).\nBind void to f:\n    Define g as a function: Return 1. Done.\n    Bind number to plus unto box, one's n + cast g.\nDone.\nCast f.", 4)]
    [InlineData("Define object box with (the number n).\nBind void to f, given (the number function g given ()):\n    Bind number to plus unto box, one's n + cast g.\nDone.", 3)]
    [InlineData("Define object box with (the number n).\nBind void to f:\n    Bind number to plus unto box, one's n + cast g.\n    Bind number to g:\n        Return 1.\n    Done.\nDone.\nCast f.", 3)]
    [InlineData("Define object box with (the number n).\nPull a rabbit.\n    Define g as a function: Return 1. Done.\n    Bind number to plus unto box, one's n + cast g.\nDone.", 4)]
    [InlineData("Define object box with (the number n).\nDefine t as a function:\n    Bind number to g:\n        Return 1.\n    Done.\n    Bind number to plus unto box, one's n + cast g.\n    Return 1.\nDone.\nState cast t.", 6)]
    [InlineData("Define object box with (the number n).\nPull a rabbit.\n    Have rabbit start a task:\n        Bind number to g, 1.\n        Bind number to plus unto box, one's n + cast g.\n    Done.\nDone.", 5)]
    public void AMethodUsingAFunctionOfItsBlock_IsToldWhoseItIs(string source, int line)
    {
        Assert.Equal("That doesn't work: 'g' belongs to the block this method is written in, and a method can't see it.\n"
            + $"Here on line {line}, you're trying to use 'g' inside a method.\n\n"
            + "A method belongs to its type, and can be cast where that block is gone. Pass 'g' in as a parameter, "
            + "or keep it in a field.", CheckFails(source));
    }

    /// <summary>A constant that is something its block holds, or hides the program's, is the block's own.</summary>
    [Theory]
    [InlineData("Pull a rabbit.\n    Define xs as a series of number with (1, 2).\n    Define k as xs permanently.\n    Bind number to f:\n        Return the number of k.\n    Done.\nDone.", 5)]
    [InlineData("Bind series of number to same, given (the series of number s), s.\nPull a rabbit.\n    Define xs as a series of number with (1, 2).\n    Define k as cast same on (xs) permanently.\n    Bind number to f:\n        Return the number of k.\n    Done.\nDone.", 6)]
    [InlineData("Define k as 1 permanently.\nPull a rabbit.\n    Define a shadow k as 2 permanently.\n    Bind number to f, k.\nDone.", 4)]
    public void AFunctionReadingAConstantOfItsBlocksOwn_IsToldSo(string source, int line)
    {
        Assert.Equal("That doesn't work: 'k' belongs to the block it's defined in, and a function can't see it.\n"
            + "A function can be called after that block's 'Done.', and what 'k' stands for is gone by then.\n"
            + $"Here on line {line}, you're trying to read 'k' inside a function.\n\nPass 'k' in as a parameter.",
            CheckFails(source));
    }

    /// <summary>A rabbit's series kept in a constant, which outlives the rabbit.</summary>
    [Fact]
    public void ARabbitsSeriesKeptInAConstant_IsRefused() =>
        Assert.StartsWith("That doesn't work: this value lives in a shorter-lived region than its destination", CheckFails(
            "Pull a rabbit.\n    Define rows as a series of series of number with (a series of number with (1, 2)) permanently.\n    Define xs as a series of number with (1, 2).\n    Insert xs into rows.\nDone."));

    [Fact]
    public void TwoConstantsOfOneNameInTwoBlocks_AreRefused() =>
        Assert.StartsWith("That doesn't work: 'k' already exists in an enclosing scope.", CheckFails(
            "Pull a rabbit.\n    Define x as 2.\n    Define k as x + 1 permanently.\nDone.\nPull a rabbit.\n    Define y as 2.\n    Define k as y + 1 permanently.\nDone."));

    /// <remarks>
    /// ⚠ The resolver that refuses a written type has no place, so this one gave no line at all. It
    /// gives where the statement or definition STARTS, and says so: a type carries no line of its own.
    /// </remarks>
    [Theory]
    [InlineData("Define x as 1.\nBind number to f, given (the nubmer n), 3.", 2)]
    [InlineData("Define x as 1.\nBind nubmer to f, given (the number n), 3.", 2)]
    [InlineData("Define x as 1.\nDefine object box with (the number size,\n    the nubmer width).", 2)]
    [InlineData("If true:\n    Define xs as a series of nubmer.\nDone.", 2)]
    public void ATypeNobodyDefined_SaysWhere(string source, int line)
    {
        var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        var ex = Assert.Throws<TypeException>(() => new TypeChecker().Check(program));
        Assert.StartsWith($"That doesn't work: 'nubmer' is not a defined type.\nThis is in what starts on line {line}.\n\n", ex.Message);
        Assert.Equal(line, ex.Line);
    }

    [Theory]
    [InlineData("Define object tank with (the number litres):\n    Set litres given (the number wanted):\n        One's")]
    [InlineData("Increment one's")]
    [InlineData("State alice's")]
    public void AFileThatEndsWhereAWordWasTaken_IsRefused_NotCrashed(string source)
    {
        // ⚠ These crashed with an index error: the word after `'s` was taken with a bare Advance,
        // which walked off the end of the tokens. Found cutting corpus files short.
        // ★ `State alice's` ends where a period could, so it now gets the line-without-its-period
        // message, which also says the file ended.
        var message = ParseFails(source);
        Assert.Matches("but the file ended|The file ended before one came", message);
    }

    [Fact]
    public void ShadowingABuiltIn_DoesNotPointAtLineZero()
    {
        // ⚠ `input` is given to every program and written nowhere, so its line is 0.
        var message = CheckFails("Bind void to go:\n    Define input as 5.\nDone.\n");
        Assert.Contains("'input' already exists in an enclosing scope", message);
        Assert.Contains("'input' is built into every program", message);
        Assert.DoesNotContain("line 0", message);
    }

    [Theory]
    [InlineData("remaining")]   // a local inside a block of `math`'s power method, line 217
    [InlineData("count")]       // a local inside a block of another `math` method, line 183
    public void AnUndefinedName_IsNotBlamedOnALocalOfABundledBook(string name)
    {
        // ⚠ The prelude is checked before the program, and ending one of its blocks recorded the
        // block's locals as "ended" — so a program long enough to pass the local's line was told
        // the name "was defined on line 217, inside a block that has ended".
        var message = CheckFails(new string('\n', 300) + $"State {name}.\n");
        Assert.Contains($"'{name}' isn't defined", message);
        Assert.DoesNotContain("inside a block that has ended", message);
    }

    [Theory]
    [InlineData("numbers")]
    [InlineData("text")]
    [InlineData("facts")]
    public void AnEmptySeries_IsToldASpellingThatWorks(string of)
    {
        // ⚠ The advice was "a series of numbers ()", which does not parse. Every spelling it
        // offers now has to check clean when written as it says. Found writing the checker in Cufet.
        var message = CheckFails("Define xs as a series with ().\n");
        Assert.Contains($"'a series of {of}'", message);
        var program = new Parser(new CufetLexer($"Define xs as a series of {of}.\n").Tokenize()).Parse();
        new TypeChecker().Check(program);
    }

    // ⚠ REFERENCE promised that using an un-narrowed union where one of its cases is needed "names
    // the expected narrowing form", and the refusal never did — `x + 1` on a `(number or text)` said
    // only that arithmetic needs numbers. The fix line now says how to find out which case it holds.
    [Theory]
    [InlineData("State x + 1.", "'If x is a number:'")]
    [InlineData("State 1 + x.", "'If x is a number:'")]
    [InlineData("If x is greater than 3, state \"big\".", "'If x is a number:'")]
    [InlineData("State -x.", "'If x is a number:'")]
    [InlineData("State the length of x.", "'If x is a text:'")]
    [InlineData("State x joined to \"!\".", "'If x is a text:'")]
    [InlineData("State \"!\" joined to x.", "'If x is a text:'")]
    public void AUnionWhereOneCaseIsNeeded_IsToldToCheckWhichItIs(string use, string check)
    {
        var message = CheckFails("Define the (number or text) x as 4.\n" + use);
        Assert.Contains("'x' could be any of (number or text). Check which it is first: " + check + ".", message);
    }

    [Fact]
    public void AUnionThatIsNotANamedVariable_IsPointedAtJudge()
    {
        // Narrowing reaches variables alone, so an `If` would not help here.
        var message = CheckFails("Define items as a catalogue of (number or text) with (1, \"a\").\nState (item 1 of items) + 1.");
        Assert.Contains("This value could be any of (number or text). Check which it is first: 'Judge …, where it is:' with an arm for 'A number'.", message);
    }

    // Narrowing has to be able to make the line valid, or the advice sends the writer nowhere.
    [Theory]
    [InlineData("Define the (number or text) x as 4.\nState x + \"a\".")]
    [InlineData("Define the (text or fact) x as true.\nState x + 1.")]
    public void AUnionNarrowingCannotHelp_KeepsTheUsualAdvice(string source)
    {
        var message = CheckFails(source);
        Assert.DoesNotContain("Check which it is first", message);
        Assert.Contains("If you meant arithmetic, both sides need to be numbers.", message);
    }
}
