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
    public void ASentenceIsNotFramedAsAnExpectation(string source, string sentence)
    {
        var message = ParseFails(source);
        Assert.Contains(sentence, message);
        Assert.DoesNotContain("expected expected", message);
        Assert.DoesNotContain(", got ", message);
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

    [Fact]
    public void ShadowingABuiltIn_DoesNotPointAtLineZero()
    {
        // ⚠ `input` is given to every program and written nowhere, so its line is 0.
        var message = CheckFails("Bind void to go:\n    Define input as 5.\nDone.\n");
        Assert.Contains("'input' already exists in an enclosing scope", message);
        Assert.Contains("'input' is built into every program", message);
        Assert.DoesNotContain("line 0", message);
    }
}
