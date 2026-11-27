using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// A position or a count asked of something that is not a series is refused before the program runs.
/// </summary>
/// <remarks>
/// ⚠⚠ `item 1 of 5` CHECKED CLEAN and died when it ran, with "Expected a series on line N": the
/// checker answered "type unknown" for everything it had no rule for. MEASURED 2026-11-23 over 26
/// kinds of value and five forms; every refused program here was a hole.
/// </remarks>
public class NotASeriesTests
{
    private static void Check(string source) =>
        new TypeChecker().Check(new Parser(new CufetLexer(source).Tokenize()).Parse());

    private static TypeException Refused(string source) => Assert.Throws<TypeException>(() => Check(source));

    private const string Matrix = "Pull a book on collections.\n    Define x as a matrix with ((1, 2), (3, 4)).\n    ";

    [Theory]
    [InlineData("Define x as 5.\nState item 1 of x.", "x is a number, not a series")]
    [InlineData("State the first of 5.", "5 is a number, not a series")]
    [InlineData("Define x as true.\nState the last of x.", "x is a fact, not a series")]
    [InlineData("Define x as 0b1010.\nState item 1 of x.", "x is bits, not a series")]
    [InlineData("Define x as void.\nState item 1 of x.", "x is void, not a series")]
    [InlineData("Define n as 1.\nDefine x as 5.\nState item n of x.", "x is a number, not a series")]
    [InlineData("State item 1 of (1 + 2).", "this is a number, not a series")]
    [InlineData("Define x as 5.\nState the number of x.", "x is a number, not a series")]
    [InlineData("Define object box with (the number size).\nDefine x as a new box { the size 1 }.\nState the number of x.", "x is a box, not a series")]
    [InlineData("Bind number to g, given (the number n), n.\nDefine x as g.\nState the number of x.", "not a series")]
    public void ItSaysWhatTheValueIs(string source, string fragment)
    {
        var ex = Refused(source);
        Assert.StartsWith("That doesn't work: ", ex.Message);
        Assert.Contains(fragment, ex.Message);
        Assert.Equal(source.Split('\n').Length, ex.Line);
    }

    [Theory]
    [InlineData("Define x as \"abc\".\nState the first of x.", "'the characters from 1 to 1 of x'")]
    [InlineData("Define x as a map with (\"a\" : 1).\nState item 1 of x.", "'the entry for <key> in x'")]
    [InlineData("Pull a book on collections.\n    Define x as a set of number with (1, 2).\n    State item 1 of x.\nDone.", "Go through it with 'For each'.")]
    [InlineData(Matrix + "State item 1 of x.\nDone.", "'the item at (<row>, <column>) of x'")]
    public void AValueWithAFormOfItsOwn_IsTold(string source, string fix) => Assert.Contains(fix, Refused(source).Message);

    private const string Maybe = "Bind voidable series of number to g:\n    Return void.\nDone.\nDefine x as cast g.\n";

    [Theory]
    [InlineData("State item 1 of x.")]
    [InlineData("State the last of x.")]
    [InlineData("State the number of x.")]
    public void OneThatMightBeVoid_HasToBeChecked(string line) =>
        Assert.Contains("'x' might be void, and void has no items", Refused(Maybe + line).Message);

    [Theory]
    [InlineData("number or series of number")]
    [InlineData("series of number or series of text")]
    public void AUnionThatCouldBeASeries_HasToBeNarrowed(string cases)
    {
        var ex = Refused($"Bind void to f, given (the ({cases}) x):\n    State item 1 of x.\nDone.");
        Assert.Contains("which of those it holds is not known yet", ex.Message);
        Assert.Contains("'If x is a series of numbers:'", ex.Message);
    }

    [Theory]
    [InlineData("Define x as a record with (the a 1).\nDefine n as 1.\nState item n of x.", "'x' has no positional fields")]
    [InlineData("Define object box with (the number size).\nDefine x as a new box { the size 1 }.\nDefine n as 1.\nState item n of x.", "'box' has no positional fields")]
    public void ANamedIndexIntoNoPositionsAtAll_IsRefused(string source, string fragment) =>
        Assert.Contains(fragment, Refused(source).Message);

    /// <remarks>
    /// ★ As a named field is. One line printed a number for one caller's object and a text for the
    /// next, and an object with no positions died when it ran and would not build (2026-11-23).
    /// </remarks>
    [Theory]
    [InlineData("item 1 of x")]
    [InlineData("the last of x")]
    [InlineData("item n of x")]
    public void AnInterface_HasNoPositions(string read)
    {
        var ex = Refused("Define n as 1.\nDefine interface shower for the text function show.\n"
                       + $"Bind void to f, given (the shower x):\n    State {read}.\nDone.");
        Assert.Contains("x is a shower, and an interface has no positions", ex.Message);
        Assert.Contains("'If x is a <type>:'", ex.Message);
        Assert.Equal(4, ex.Line);
    }

    /// <remarks>★ The guards: each of these works when it runs, and must go on checking.</remarks>
    [Theory]
    [InlineData("Define x as a series of number with (1, 2).\nState item 1 of x.\nState the number of x.")]
    [InlineData("Pull a book on collections.\n    Define x as a set of number with (1, 2).\n    State the number of x.\nDone.")]
    [InlineData("State item 1 of range 1 to 3.")]
    [InlineData("Pull a book on collections.\n    Define x as a chase.\n    Insert \"abc\" into x.\n    State item 1 of x.\n    State the number of x.\nDone.")]
    [InlineData("Pull a book on collections.\n    Define x as a catalogue with (1, \"two\").\n    State the last of x.\nDone.")]
    [InlineData("Define x as a record with (1, 2).\nDefine n as 1.\nState item n of x.")]
    [InlineData("Define object pair with (number, number).\nDefine x as a new pair { 1, 2 }.\nDefine n as 1.\nState item n of x.")]
    [InlineData(Maybe + "If x is not void, state item 1 of x.")]
    [InlineData("Bind void to f, given (the (number or series of number) x):\n    If x is a series of number, state item 1 of x.\nDone.")]
    [InlineData("Define interface shower for the text function show.\nDefine object pair with (number, number) and shower.\nBind text to show unto pair, \"b\".\nBind void to f, given (the shower x):\n    If x is a pair, state item 1 of x.\nDone.\nCast f on (a new pair { 7, 8 }).")]
    [InlineData("Bind element to front, given (the series of element xs), item 1 of xs.\nState cast front on (a series of number with (7)).")]
    public void WhatWorks_StillChecks(string source) => Check(source);
}
