using Cufet.Interpreter;
using Cufet.Lexer;
using System.Runtime.ExceptionServices;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

public class CommentTests
{
    private static string Run(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var output  = new StringWriter();
        RunOnLargeStack(() => new Interpreter(output).Execute(program));
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static void RunOnLargeStack(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(
            () => { try { action(); } catch (Exception e) { caught = e; } },
            16 * 1024 * 1024);
        thread.Start();
        thread.Join();
        if (caught is not null)
            ExceptionDispatchInfo.Capture(caught).Throw();
    }

    [Fact]
    public void Comment_BeforeStatement_Ignored()
    {
        Assert.Equal("5", Run("[[ note ]] Define x as 5. State x."));
    }

    [Fact]
    public void Comment_AfterStatement_Ignored()
    {
        Assert.Equal("5", Run("Define x as 5. [[ note ]] State x."));
    }

    [Fact]
    public void Comment_InlineAfterStatementBeforeNext_Ignored()
    {
        Assert.Equal("5", Run("Define x as 5. [[ note ]] State x."));
    }

    [Fact]
    public void Comment_MultiLine_Ignored()
    {
        Assert.Equal("5", Run("[[ line one\nline two ]] Define x as 5. State x."));
    }

    [Fact]
    public void Comment_DotInsideIsNotTerminator()
    {
        // The '.' inside the comment must not end a statement — x should still be defined.
        Assert.Equal("5", Run("[[ this. has. periods. ]] Define x as 5. State x."));
    }

    [Fact]
    public void Comment_CufetSyntaxInsideIsNotParsed()
    {
        // "Define y as 99." inside a comment — y must NOT be defined.
        // The only variable defined and stated is x = 5.
        Assert.Equal("5", Run("[[ Define y as 99. ]] Define x as 5. State x."));
    }

    [Fact]
    public void Comment_BetweenStatements_Ignored()
    {
        Assert.Equal("5\n10", Run("Define x as 5. [[ between ]] Define y as 10. State x. State y."));
    }
}
