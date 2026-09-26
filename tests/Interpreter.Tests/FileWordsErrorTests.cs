using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

// Reading or writing a piece of text where a file was meant — `the file` left out.
//
// ⚠ Both answered with advice about opening STREAMS (`With the file … open for reading as s:`), when
// the fix was two missing words. Found by writing the tutorial's lesson on files.
public class FileWordsErrorTests
{
    private static TypeException CheckFails(string source)
    {
        var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        return Assert.Throws<TypeException>(() => new TypeChecker().Check(program));
    }

    [Theory]
    [InlineData("Define notes as read all from \"notes.txt\" but on failure \"\".",
                "'read all from the file \"notes.txt\"'")]
    [InlineData("Define notes as read all lines from \"notes.txt\" but on failure a series of text.",
                "'read all lines from the file \"notes.txt\"'")]
    [InlineData("Write \"hello\" to \"notes.txt\".",
                "'Write … to the file \"notes.txt\".'")]
    public void AFileNameWithoutTheFile_SaysToAddIt(string source, string fix)
    {
        var ex = CheckFails(source);
        Assert.Contains("and this is a piece of text", ex.Message);
        Assert.Contains(fix, ex.Message);
        Assert.DoesNotContain("open for", ex.Message);
    }

    [Fact]
    public void ReadingALineFromText_KeepsTheStreamAdvice()
    {
        // `read a line` has no file form, so pointing at `the file` would be wrong.
        var ex = CheckFails("Define first as read a line from \"notes.txt\".");
        Assert.Contains("read expects a readable stream of text", ex.Message);
    }
}
