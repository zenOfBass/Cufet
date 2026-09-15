using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// Where a pull looks for a book: beside the file that pulls it, then the project's shared folder.
/// </summary>
/// <remarks>
/// <para>
/// ★★ Before this, a book had to sit in the pulling file's own directory and nowhere else — so two
/// programs in two directories could not share one book except by keeping a copy each. That was
/// measured 2026-09-08 and recorded as a LANGUAGE gap rather than a tooling one, because a package
/// manager has nowhere to put anything until this exists.
/// </para>
/// <para>
/// ★ A project is a directory with a <c>blueprint.cufe</c> in it, found by walking up. Its
/// LOCATION is read and its contents never are: `check`, the editor and `run` all have to resolve
/// a pull, and none of them should execute a build description to do it.
/// </para>
/// <para>
/// ⚠ No blueprint above a file means no project, and resolution is then exactly what it always
/// was. A loose <c>.cufe</c> in a downloads folder keeps meaning what it meant.
/// </para>
/// </remarks>
[Collection("SourceMap")]
public class ProjectBookResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cufet-project-" + Guid.NewGuid().ToString("n"));

    public ProjectBookResolutionTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "books"));
        Directory.CreateDirectory(Path.Combine(_root, "paint"));
        Directory.CreateDirectory(Path.Combine(_root, "game"));
    }

    public void Dispose()
    {
        SourceMap.Current = null;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Marks the root. Its contents are never read, so anything will do.</summary>
    private void MarkProject() =>
        File.WriteAllText(Path.Combine(_root, BookLoading.BlueprintFile), "// a project lives here");

    private void Write(string folder, string name, string body) =>
        File.WriteAllText(Path.Combine(_root, folder, name + ".cufe"), body);

    private string RunIn(string folder, string source)
    {
        var checker = new TypeChecker { SourceDirectory = Path.Combine(_root, folder) };
        SourceMap.Current = checker.Sources;
        var program = checker.Check(new Parser(new CufetLexer(source).Tokenize()).Parse());
        var output = new StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private const string SharedCanvas = """
        Define object canvas with () and book:
            Bind text to draw: Return "the shared canvas". Done.
        Done.
        """;

    [Fact]
    public void TwoProgramsInTwoFolders_ShareOneBookFromTheProject()
    {
        MarkProject();
        Write("books", "canvas", SharedCanvas);

        const string body = """
            Pull a book on canvas.
                State cast canvas's draw.
            Done.
            """;

        // ★ THE WALL THIS EXISTS TO REMOVE. Neither folder holds `canvas.cufe`, and before this
        // the only way to have both was a copy in each.
        Assert.Equal("the shared canvas", RunIn("paint", body));
        Assert.Equal("the shared canvas", RunIn("game", body));
    }

    /// <remarks>
    /// ★★ LOCAL WINS, which is what makes the shared folder a default rather than a ceiling: a
    /// program may keep its own version beside it without the project having to agree.
    /// </remarks>
    [Fact]
    public void ABookBesideThePuller_BeatsTheProjectsOwn()
    {
        MarkProject();
        Write("books", "canvas", SharedCanvas);
        Write("paint", "canvas", """
            Define object canvas with () and book:
                Bind text to draw: Return "paint's own canvas". Done.
            Done.
            """);

        const string body = """
            Pull a book on canvas.
                State cast canvas's draw.
            Done.
            """;

        Assert.Equal("paint's own canvas", RunIn("paint", body));
        // ⚠ And the other program is untouched — precedence is per-resolution, not a global choice.
        Assert.Equal("the shared canvas", RunIn("game", body));
    }

    /// <summary>A book may pull another book, and both are found in the project.</summary>
    /// <remarks>
    /// ⚠⚠ THIS DOES NOT TEST "beside the book". It was written believing it did, and the sabotage
    /// check said otherwise: reverting `Gather` to pass the entry program's directory down leaves
    /// this green. The reason is structural — with only two places to look, a book is always FOUND
    /// in one of them, and the shared folder is a fallback at every level, so the two rules cannot
    /// disagree yet. See the note at that line; it needs a book living somewhere that is not itself
    /// a search root before anything can tell.
    ///
    /// <para>★ What it does pin, which is worth pinning: a chain of pulls across files resolves at
    /// all, and `paint/` holds neither file.</para>
    /// </remarks>
    [Fact]
    public void ABookMayPullAnotherBook_BothFoundInTheProject()
    {
        MarkProject();
        Write("books", "palette", """
            Define object palette with () and book:
                Bind text to ink: Return "#". Done.
            Done.
            """);
        Write("books", "canvas", """
            Pull a book on palette.
                Define object canvas with () and book:
                    Bind text to draw: Return "drawn with " joined to cast palette's ink. Done.
                Done.
            Done.
            """);

        // ★★ `palette` is NOT named here, and that is the point of the pair of files. `canvas`
        // pulls it in its own file, so the dependency is satisfied where it was written and stays
        // private — a program wanting `canvas` asks for `canvas`. This assertion USED to name both
        // and carried a comment explaining why it had to; that explanation outlived its fact.
        Assert.Equal("drawn with #", RunIn("paint", """
            Pull a book on canvas.
                State cast canvas's draw.
            Done.
            """));

        // ⚠ And naming it anyway is still fine — a caller's own pull WINS and the lexical one only
        // fills gaps, so adding a dependency to a pull site can never change which one a book got.
        Assert.Equal("drawn with #", RunIn("paint", """
            Pull books on palette, and canvas.
                State cast canvas's draw.
            Done.
            """));
    }

    /// <remarks>
    /// ⚠ The guard against the search widening on its own. Without a blueprint there is no project,
    /// so the shared folder is not consulted even though it is sitting right there.
    /// </remarks>
    [Fact]
    public void WithNoBlueprint_TheSharedFolderIsNotSearched()
    {
        // Deliberately NOT calling MarkProject.
        Write("books", "canvas", SharedCanvas);

        var refused = Assert.Throws<TypeException>(() => RunIn("paint", """
            Pull a book on canvas.
                State cast canvas's draw.
            Done.
            """));

        Assert.Contains("nothing named 'canvas' to pull", refused.Message);
        // ★ And the message says so, rather than implying books in files do not exist.
        Assert.Contains("canvas.cufe", refused.Message);
    }

    /// <remarks>
    /// ⚠ A failed pull used to offer the bundled books and an object definition and stop — telling
    /// a writer whose file was one folder off, in effect, that a book cannot be a file at all.
    /// </remarks>
    [Fact]
    public void AFailedPull_NamesWhereItLooked()
    {
        MarkProject();
        Directory.CreateDirectory(Path.Combine(_root, "books"));

        var refused = Assert.Throws<TypeException>(() => RunIn("paint", """
            Pull a book on missing-thing.
                State "unreachable".
            Done.
            """));

        Assert.Contains("missing-thing.cufe", refused.Message);
        Assert.Contains("beside the file that pulls it", refused.Message);
        Assert.Contains(Path.Combine(_root, "books"), refused.Message);
    }
}
