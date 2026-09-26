using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// A directory inside a project is a NAMESPACE: its files share one set of names, with nothing
/// pulled between them.
/// </summary>
/// <remarks>
/// <para>
/// ★★ SETTLED 2026-09-20 in <c>docs/DESIGN.md</c>, reached by trying to write a real multi-file
/// program. A folder's files already are a group of declarations under one name; a <c>Pull</c>
/// between two of them says that a second time.
/// </para>
/// <para>
/// ⚠⚠ Every test here writes a REAL entry file, because the rule needs
/// <see cref="TypeChecker.SourceFile"/> and not just the directory — a file's neighbours are its
/// directory minus itself, and a checker that knows only the directory cannot subtract. That is
/// also why the older suites nearby, which check a program built from a string, are untouched by
/// all of this: they set no file, so they have no neighbours.
/// </para>
/// </remarks>
[Collection("SourceMap")]
public class DirectoryNamespaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cufet-namespace-" + Guid.NewGuid().ToString("n"));

    public DirectoryNamespaceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SourceMap.Current = null;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Marks the root a project. Its contents are never read, so anything will do.</summary>
    private void MarkProject(string folder = "") =>
        File.WriteAllText(Path.Combine(Where(folder), BookLoading.BlueprintFile),
                          "// a project lives here");

    private string Where(string folder)
    {
        var at = folder.Length == 0 ? _root : Path.Combine(_root, folder);
        Directory.CreateDirectory(at);
        return at;
    }

    private string Write(string folder, string name, string body)
    {
        var path = Path.Combine(Where(folder), name + ".cufe");
        File.WriteAllText(path, body);
        return path;
    }

    /// <summary>Runs a file that is REALLY THERE, the way the CLI does.</summary>
    private string Run(string path)
    {
        var checker = new TypeChecker
        {
            SourceDirectory = Path.GetDirectoryName(path),
            SourceFile = path,
        };
        SourceMap.Current = checker.Sources;
        var source = File.ReadAllText(path);
        var program = checker.Check(new Parser(new CufetLexer(source).Tokenize()).Parse());
        var output = new StringWriter();
        new Interpreter(output).Execute(program);
        return output.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private TypeException Refuses(string path) =>
        Assert.Throws<TypeException>(() => Run(path));

    private const string Helper = """
        Bind number to doubled, given (the number n):
            Return n * 2.
        Done.
        """;

    private const string UsesHelper = """
        State "{cast doubled on (21)}".
        """;

    // ── The rule ─────────────────────────────────────────────────────────────

    /// <remarks>
    /// ★★ THE WALL THIS REMOVES. Before it, the only way for one file to reach another's function
    /// was to make the other a BOOK and pull it — which meant a marker, a wrapping object and a
    /// pull line, to say the thing the shared folder already said.
    /// </remarks>
    [Fact]
    public void AFileReachesItsNeighbour_WithNothingPulledBetweenThem()
    {
        MarkProject();
        Write("", "helper", Helper);
        Assert.Equal("42", Run(Write("", "main", UsesHelper)));
    }

    /// <remarks>
    /// ⚠⚠ THE GATE, and the whole reason this could land without touching the corpus. Top-level
    /// name collisions between files in one directory are everywhere under <c>examples/</c> —
    /// <c>play</c> in five files of <c>examples/parsing</c> alone — and not one of those
    /// directories has a blueprint above it.
    /// </remarks>
    [Fact]
    public void WithNoBlueprintAbove_AFileHasNoNeighbours()
    {
        Write("", "helper", Helper);
        var refusal = Refuses(Write("", "main", UsesHelper));
        // Still refused — the gate is unchanged. The WORDS changed (2026-09-26): this used to say
        // "'doubled' isn't defined", and now names the neighbour and the missing blueprint, which
        // is the actual reason. Found by writing the tutorial's lesson on projects.
        Assert.Contains("'doubled' is declared in helper.cufe beside this file", refusal.Message);
        Assert.Contains("this folder is not a project", refusal.Message);
    }

    /// <remarks>
    /// ⚠ The blueprint is found by walking UP, so a file in a subfolder of a project is in one
    /// too — this is the same gate as above, reached from one level down.
    /// </remarks>
    [Fact]
    public void AProjectFoundByWalkingUp_StillGivesNeighbours()
    {
        MarkProject();
        Write("game", "helper", Helper);
        Assert.Equal("42", Run(Write("game", "main", UsesHelper)));
    }

    // ── What a neighbour contributes, and what it does not ───────────────────

    /// <remarks>
    /// ★★ ONLY THE FILE YOU RUN RUNS. This is NOT the decision <c>RefuseAProgram</c> makes about a
    /// pulled file, and deliberately: a pulled file is a library and may not be a program, but two
    /// programs sharing a directory is the ordinary case — <c>tools/shell.cufe</c> and
    /// <c>tools/repl.cufe</c> both start something on their last line.
    /// </remarks>
    [Fact]
    public void ANeighbourContributesItsDeclarations_AndRunsNothing()
    {
        MarkProject();
        Write("", "helper", Helper + "\nState \"the neighbour ran\".\n");
        Assert.Equal("42", Run(Write("", "main", UsesHelper)));
    }

    /// <remarks>
    /// ★ A neighbour's helpers are PUBLIC, which is the difference between this and the book
    /// loader's privacy rename. A pulled book's non-module declarations are renamed out of reach;
    /// neighbours are one scope by definition, so there is nothing to hide from.
    /// </remarks>
    [Fact]
    public void ANeighboursDeclarationsAreNotRenamedOutOfReach()
    {
        MarkProject();
        Write("", "helper", """
            Define object greeter with () and book:
                Bind text to hello: Return "hello". Done.
            Done.

            Bind text to shouted:
                Return "HELLO".
            Done.
            """);

        // `shouted` sits BESIDE a module, which is exactly what a pull would have hidden.
        Assert.Equal("HELLO", Run(Write("", "main", "State cast shouted.")));
    }

    /// <remarks>
    /// ⚠ A neighbour keeps its own pulls, and they still resolve — from the project's shared
    /// folder here, which is a place a qualification cannot reach and where `Pull` therefore
    /// survives.
    /// </remarks>
    [Fact]
    public void ANeighbourKeepsItsOwnPull_IntoTheProjectsBooks()
    {
        MarkProject();
        Write("books", "canvas", """
            Define object canvas with () and book:
                Bind text to draw: Return "the shared canvas". Done.
            Done.
            """);
        Write("", "helper", """
            Pull a book on canvas.
                Bind text to painted:
                    Return cast canvas's draw.
                Done.
            Done.
            """);

        Assert.Equal("the shared canvas", Run(Write("", "main", "State cast painted.")));
    }

    // ── The refusal ──────────────────────────────────────────────────────────

    /// <remarks>
    /// ★★ The one thing the rule COSTS, and it has to be loud. Two files in one directory
    /// declaring one name used to be two namespaces; now it is one, and the second declaration
    /// would silently replace the first.
    /// </remarks>
    [Fact]
    public void OneNameDeclaredInTwoFilesOfADirectory_IsRefused_NamingBoth()
    {
        MarkProject();
        Write("", "alpha", Helper);
        Write("", "beta", Helper);

        var refusal = Refuses(Write("", "main", UsesHelper));
        Assert.Contains("'doubled' is declared in two files of one directory", refusal.Message);
        Assert.Contains("alpha.cufe", refusal.Message);
        Assert.Contains("beta.cufe", refusal.Message);
    }

    /// <remarks>
    /// ⚠ The file being CHECKED is part of the namespace too, so a name it shares with a neighbour
    /// is the same collision — and is reported against the neighbour, since the file in front of
    /// the reader is the one that claimed the name first.
    /// </remarks>
    [Fact]
    public void ANameTheRunningFileAlsoDeclares_IsTheSameCollision()
    {
        MarkProject();
        Write("", "helper", Helper);

        var refusal = Refuses(Write("", "main", Helper + "\n" + UsesHelper));
        Assert.Contains("'doubled' is declared in two files of one directory", refusal.Message);
        Assert.Contains("main.cufe", refusal.Message);
    }

    /// <remarks>
    /// ★ A name repeated across two DIRECTORIES is not a collision at all — separating one set of
    /// names from another is the whole job a directory does here.
    /// </remarks>
    [Fact]
    public void TheSameNameInTwoDirectoriesOfOneProject_IsNotACollision()
    {
        MarkProject();
        Write("paint", "helper", Helper);
        Write("game", "helper", Helper);

        Assert.Equal("42", Run(Write("paint", "main", UsesHelper)));
        Assert.Equal("42", Run(Write("game", "main", UsesHelper)));
    }

    /// <remarks>
    /// ⚠ And a file one directory away is not a neighbour, which is the other half of the same
    /// statement. Reaching it is QUALIFICATION, not visibility.
    /// </remarks>
    [Fact]
    public void AFileInAnotherDirectory_IsNotANeighbour()
    {
        MarkProject();
        Write("paint", "helper", Helper);

        var refusal = Refuses(Write("game", "main", UsesHelper));
        Assert.Contains("'doubled' isn't defined", refusal.Message);
    }

    // ── Qualifying another directory ─────────────────────────────────────────

    private const string Keys = """
        Bind text to read-key:
            Return "k".
        Done.
        """;

    /// <remarks>
    /// ★★ THE POINT OF THE WHOLE ARC. <c>tools/snake</c> could not reach
    /// <c>tools/terminals</c> at all, and the only offered answer was to make the other directory
    /// a book — which is what the question *"why do you still need to make a book if it's in
    /// another directory?"* had no recorded answer to.
    /// </remarks>
    [Fact]
    public void AnotherDirectoryIsReachedByQualifyingIt_WithNothingPulled()
    {
        MarkProject();
        Write("terminals", "keys", Keys);
        Assert.Equal("k", Run(Write("game", "main", """State cast terminals's read-key.""")));
    }

    /// <remarks>★ And the qualified directory's own files still see each other flatly.</remarks>
    [Fact]
    public void AQualifiedDirectorysFilesStillSeeEachOther()
    {
        MarkProject();
        Write("terminals", "keys", Keys);
        Write("terminals", "extra", """
            Bind text to twice-over:
                Return "{cast read-key}{cast read-key}".
            Done.
            """);

        Assert.Equal("kk", Run(Write("game", "main", """State cast terminals's twice-over.""")));
    }

    /// <remarks>
    /// <para>
    /// ⚠⚠ The other half of "a directory separates one set of names from another": what a
    /// directory declares is reachable BY QUALIFYING and never by its bare name. Without this the
    /// rule would be a way of writing one flat project, which is what it exists to avoid.
    /// </para>
    /// <para>
    /// ⚠⚠ THE PROGRAM MUST QUALIFY THE DIRECTORY TOO, and that line is the test. Written without
    /// it this passed under sabotage: a directory nobody qualifies is never loaded, so the bare
    /// name was unresolved for a reason that had nothing to do with privacy. It looked like a
    /// privacy test and measured lazy loading.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnotherDirectorysNames_AreNotReachableUnqualified()
    {
        MarkProject();
        Write("terminals", "keys", Keys + """

            Bind text to shouted:
                Return "K".
            Done.
            """);

        var refusal = Refuses(Write("game", "main", """
            State cast terminals's read-key.
            State cast shouted.
            """));
        Assert.Contains("'shouted' isn't defined", refusal.Message);
    }

    /// <remarks>
    /// ★ Measured on the first program written against this, and fixed because of it: the
    /// ordinary refusal blamed the DIRECTORY — "'terminals' isn't defined" — which is both false
    /// and useless, since the directory is plainly there.
    /// </remarks>
    [Fact]
    public void AMemberTheDirectoryHasNot_IsRefusedByNameAndListsWhatItHas()
    {
        MarkProject();
        Write("terminals", "keys", Keys);

        var refusal = Refuses(Write("game", "main", """State cast terminals's nonesuch."""));
        Assert.Contains("the directory 'terminals' declares nothing called 'nonesuch'", refusal.Message);
        Assert.Contains("read-key", refusal.Message);
    }

    /// <remarks>
    /// ⚠⚠ WHAT MAKES THE REWRITE SOUND. The qualification is resolved before any scope exists, so
    /// a name that is both a directory and a value would have two readings with nothing to choose
    /// between them. One of the two readings is removed rather than the pass being taught scope.
    /// </remarks>
    [Fact]
    public void ANameThatIsAlsoADirectoryOfTheProject_IsRefused()
    {
        MarkProject();
        Write("terminals", "keys", Keys);

        var refusal = Refuses(Write("game", "main", """
            Define terminals as 5.
            State cast terminals's read-key.
            """));
        Assert.Contains("'terminals' is a directory of this project", refusal.Message);
    }

    /// <remarks>
    /// ★ A namespace may qualify a THIRD one, so loading runs to a fixpoint rather than one deep.
    /// </remarks>
    [Fact]
    public void AQualifiedDirectoryMayItselfQualifyAnother()
    {
        MarkProject();
        Write("bottom", "b", """
            Bind text to deepest:
                Return "bottom".
            Done.
            """);
        Write("middle", "m", """
            Bind text to relayed:
                Return cast bottom's deepest.
            Done.
            """);

        Assert.Equal("bottom", Run(Write("top", "main", """State cast middle's relayed.""")));
    }

    /// <remarks>
    /// ⚠ Recorded, not refused, until somebody writes the ambiguous qualification — an unused
    /// clash must not break every other program in the project.
    /// </remarks>
    [Fact]
    public void TwoDirectoriesOfOneName_AreRefusedOnlyWhenQualified()
    {
        MarkProject();
        Write("terminals", "keys", Keys);
        Write(Path.Combine("deep", "terminals"), "other", """
            Bind text to read-key: Return "other". Done.
            """);
        Write("game", "helper", Helper);

        // Nobody qualifies `terminals` here, so the clash is nobody's problem.
        Assert.Equal("42", Run(Write("game", "quiet", UsesHelper)));

        var refusal = Refuses(Write("game", "loud", """State cast terminals's read-key."""));
        Assert.Contains("two directories of this project are both named 'terminals'", refusal.Message);
    }

    // ── The blueprint is not in the namespace ────────────────────────────────

    /// <remarks>
    /// ⚠ In NEITHER direction, and that symmetry is the decision. A blueprint describes the
    /// project rather than belonging to it — and letting it join would mean <c>cufet build</c>
    /// parsed every file in the root just to read a plan.
    /// </remarks>
    [Fact]
    public void TheBlueprintIsNotANeighbour()
    {
        File.WriteAllText(Path.Combine(_root, BookLoading.BlueprintFile), Helper);

        var refusal = Refuses(Write("", "main", UsesHelper));
        Assert.Contains("'doubled' isn't defined", refusal.Message);
    }

    /// <remarks>⚠ The other direction: checking the blueprint brings in nobody.</remarks>
    [Fact]
    public void CheckingTheBlueprint_BringsInNoNeighbours()
    {
        MarkProject();
        Write("", "helper", Helper);

        var blueprint = Path.Combine(_root, BookLoading.BlueprintFile);
        File.WriteAllText(blueprint, UsesHelper);

        var refusal = Refuses(blueprint);
        Assert.Contains("'doubled' isn't defined", refusal.Message);
    }
}
