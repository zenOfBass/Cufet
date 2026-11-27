using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// Six refusals that sent the reader the wrong way: each blamed a word that was right, or advised
/// a fix that is itself refused. Each test holds the new message and checks the old one is gone.
/// </summary>
public class MisdirectedMessageTests
{
    private static string Refusal(string source)
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
            new TypeChecker().Check(program);
        });
        return ex.Message;
    }

    [Fact]
    public void AnArticleAsAName_BlamesTheArticle_NotTheWordAfterIt()
    {
        var m = Refusal("Define a as 1.");
        Assert.Contains("'a' is an article", m);
        Assert.Contains("no name before 'as'", m);
        Assert.DoesNotContain("'as' is a word Cufet has taken", m);
    }

    [Fact]
    public void AReservedWordAfterAnArticle_IsStillTheReservedWord()
    {
        var m = Refusal("Define the range as 1.");
        Assert.Contains("'range' is a word Cufet has taken", m);
    }

    [Fact]
    public void AFunctionParameterWithNoName_SaysTheNameIsMissing()
    {
        var m = Refusal("""
            Bind number to hof, given (the number function given (the number)):
                Return 1.
            Done.
            """);
        Assert.Contains("a name is missing before 'given'", m);
        Assert.DoesNotContain("'given' is a word Cufet has taken", m);
    }

    [Fact]
    public void AJudgeArmWithAHole_SaysAValueArmIsWrittenOutInFull()
    {
        var m = Refusal("""
            Define x as "hi".
            Judge x, where it is:
                "{x}", state 1.
                Otherwise, state 2.
            Done.
            """);
        Assert.Contains("cannot have a '{…}' hole", m);
        Assert.DoesNotContain("expected a type", m);
    }

    [Fact]
    public void AValueInAnObjectBody_SaysItIsAField_NotAType()
    {
        var m = Refusal("""
            Define object box with (the number size):
                Define y as 2.
            Done.
            """);
        Assert.Contains("A value the object keeps is a field", m);
        Assert.DoesNotContain("declare a type", m);
    }

    [Fact]
    public void AnObjectNamedForABook_AdvisesThePullThatWorks()
    {
        var m = Refusal("Define object math with (the text word).");
        Assert.Contains("'Pull a book on math.'", m);
        Assert.DoesNotContain("'Pull math.'", m);
    }

    [Fact]
    public void ARunStageAmongFunctionStages_SaysAPipeCannotMixThem()
    {
        var m = Refusal("""
            Bind void to dbl:
                For each n from the input:
                    State n.
                Done.
            Done.
            run "ls" | dbl.
            """);
        Assert.Contains("a pipe is all function stages or all 'run' stages", m);
        Assert.DoesNotContain("must be a function", m);
    }
}
