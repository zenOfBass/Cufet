using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// A book that lives in another file, brought in by the `Pull` that already existed.
/// </summary>
/// <remarks>
/// <para>
/// ★★ Not a new mechanism. `Pull a book on ‹name›.` already resolved a bundled book and
/// `Pull ‹name›.` already resolved a module defined in the same file — and a module is an object
/// claiming the `module` interface, while a book is a module the language ships with. This adds a
/// third place to look for the same object. The namespace, the member access and the scope ending
/// at `Done.` were decided when modules were.
/// </para>
/// <para>
/// ⚠ Resolved and compiled TOGETHER. Whole-program visibility is what lets dispatch prove coverage
/// over every version of a name, bounds the open-union tag set, and monomorphizes a generic from
/// every filling — compiling files independently would reopen all three, and buys only build speed.
/// </para>
/// </remarks>
public class BookLoadingTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cufet-books-" + Guid.NewGuid().ToString("n"));

    public BookLoadingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SourceMap.Current = null;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Write(string name, string body) =>
        File.WriteAllText(Path.Combine(_dir, name + ".cufe"), body);

    private string Run(string source)
    {
        var checker = new TypeChecker { SourceDirectory = _dir };
        SourceMap.Current = checker.Sources;
        var program = checker.Check(new Parser(new CufetLexer(source).Tokenize()).Parse());
        var output = new StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private TypeException Refused(string source) =>
        Assert.Throws<TypeException>(() => Run(source));

    private const string Kit = """
        Define object greeting-kit with () and module:
            Bind text to greet, given (the text who):
                Return "hello, " joined to who.
            Done.
        Done.
        """;

    [Fact]
    public void ABookInAnotherFileIsPulledLikeAnyOther()
    {
        Write("greeting-kit", Kit);
        Assert.Equal("hello, world", Run("""
            Pull a book on greeting-kit.
                State cast greeting-kit's greet on ("world").
            Done.
            """));
    }

    [Fact]
    public void ItsMembersAreReachedThroughItsName()
    {
        // ★ The namespace claim, and it needed nothing new — a module is an object, so its members
        // live on it. A bare `greet` is not in scope, which is the whole point of pulling.
        //
        // ⚠⚠ Refused at RUN time, not at check time, and that is a PRE-EXISTING gap rather than
        // anything loading introduces: the identical shape written in one file, with the module
        // defined beside the pull, also passes `cufet check` with "No problems found" and then
        // dies. Recorded as what actually happens rather than what ought to — if the checker ever
        // catches it, this test goes red and says so.
        Write("greeting-kit", Kit);
        var e = Assert.Throws<RuntimeException>(() => Run("""
            Pull a book on greeting-kit.
                State cast greet on ("world").
            Done.
            """));
        Assert.Contains("'greet' is not a method", e.Message);
    }

    [Fact]
    public void OneBookMayPullAnother()
    {
        Write("inner", """
            Define object inner with () and module:
                Bind number to double, given (the number n): Return n * 2. Done.
            Done.
            """);
        Write("outer", """
            Pull a book on inner.
                State "outer loaded".
            Done.

            Define object outer with () and module:
                Bind number to quadruple, given (the number n): Return n * 4. Done.
            Done.
            """);
        Assert.Equal("outer loaded\n40", Run("""
            Pull a book on outer.
                State cast outer's quadruple on (10).
            Done.
            """));
    }

    [Fact]
    public void ABookThatPullsItselfRoundARingIsRefused()
    {
        // ★ Named rather than silently stopped. A ring is a mistake with a shape, and printing the
        // ring is what makes it fixable.
        Write("left", "Pull a book on right.\n    State \"left\".\nDone.\n");
        Write("right", "Pull a book on left.\n    State \"right\".\nDone.\n");
        var e = Refused("""
            Pull a book on left.
                State "host".
            Done.
            """);
        Assert.Contains("round a ring", e.Message);
    }

    [Fact]
    public void AMissingBookKeepsTheErrorItAlreadyHad()
    {
        // ⚠ The loader says nothing when the file is absent. The checker already refuses a name
        // that is neither bundled nor defined, and says what IS available — one message about
        // pulling, not a second one about files.
        var e = Refused("""
            Pull a book on nowhere-at-all.
                State "never".
            Done.
            """);
        Assert.Contains("nothing named 'nowhere-at-all' to pull", e.Message);
    }

    [Fact]
    public void AnErrorInsideALoadedBookReportsThatBooksOwnLine()
    {
        // ⚠⚠ The reason a loaded file is lexed at an OFFSET. Tokens and exceptions carry a line
        // and no file, so without the map an error from a book names the file that was RUN, at a
        // line out of that file's range — which is the one thing a multi-file error must not do.
        //
        // ★★ The number appears TWICE in a message: once in the reporter's header, and once in the
        // prose. 163 places in the front end put a line into prose, so the resolution happens to
        // the composed message rather than at each of them.
        Write("broken", """
            Define object broken with () and module:
                Bind number to bad, given (the number n):
                    Return n joined to "oops".
                Done.
            Done.
            """);
        var e = Refused("""
            Pull a book on broken.
                State "never".
            Done.
            """);
        Assert.Contains("line 3", e.Message);
        Assert.DoesNotContain("100", e.Message);
    }

    [Fact]
    public void ASingleFileProgramIsUntouched()
    {
        // The control. With no book to load the statements come back as the very same list, and a
        // message with a large number in it is left alone because no block was ever allocated.
        Assert.Equal("100003", Run("State 100003."));
    }
}
