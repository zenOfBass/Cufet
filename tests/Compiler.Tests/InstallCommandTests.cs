using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `cufet install` — fetching the books a project pins, at the commits it pins them to.
/// </summary>
/// <remarks>
/// <para>
/// ★★ **A local git repository is a real one**, which is what makes this testable at all. `git
/// clone` takes a path as happily as a URL, so a repo built in a temp directory with a real commit
/// exercises the whole path — clone, resolve a sha, extract, install — with no network and nothing
/// to stub. The chicken-and-egg that blocks the rest of the package manager (no books exist until
/// someone can publish one) does not block its tests.
/// </para>
/// <para>
/// ★ A pin records a COMMIT, not a version: a version string would be a second name for the same
/// thing, and a content checksum cannot survive a git checkout that rewrites line endings.
/// `--no-checkout` plus `git show <commit>:<file>` reads straight out of the object store, which no
/// `core.autocrlf` setting touches.
/// </para>
/// <para>
/// ⚠ The clone is KEPT, at `books/.cufet-cache/‹name›`, and that location is measured rather than
/// chosen: `Write` does not create parent directories and Cufet cannot make one, so `books/` has to
/// be created by something — and `git clone` creates its target including parents.
/// </para>
/// </remarks>
public class InstallCommandTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cufet.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? "";
    }

    private static string CufetExe => Path.Combine(
        RepoRoot, "src", "App", "bin", "Debug", "net10.0",
        "Cufet.App" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""));

    private readonly string _root =
        Path.Combine(TestScratch.Root, "install-" + Guid.NewGuid().ToString("N"));

    private string Project => Path.Combine(_root, "project");
    private string Library => Path.Combine(_root, "library");

    public InstallCommandTests()
    {
        Directory.CreateDirectory(Project);
        Directory.CreateDirectory(Library);
    }

    /// <remarks>
    /// ⚠ GIT MARKS ITS OBJECT FILES READ-ONLY, so a plain recursive delete throws
    /// <see cref="UnauthorizedAccessException"/> — which is NOT an <see cref="IOException"/>, so the
    /// catch every other fixture here uses does not cover it. MEASURED: the tests passed and the
    /// TEARDOWN failed, which reads in the runner as a failing test and sent me looking at the
    /// wrong half.
    /// </remarks>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static (int Exit, string Out, string Err) Run(
        string exe, string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = workingDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        return (p.ExitCode, stdout.Replace("\r\n", "\n"), stderr.Replace("\r\n", "\n"));
    }

    private (int Exit, string Out, string Err) Install() => Run(CufetExe, Project, "install");

    /// <summary>Publishes a one-file book as a real git repo, and hands back its commit sha.</summary>
    private string PublishBook(string name, string body)
    {
        File.WriteAllText(Path.Combine(Library, name + ".cufe"), body);
        Run("git", Library, "init", "--quiet");
        Run("git", Library, "add", name + ".cufe");
        Run("git", Library, "-c", "user.email=t@example.com", "-c", "user.name=t",
            "commit", "--quiet", "-m", name);
        return Run("git", Library, "rev-parse", "HEAD").Out.Trim();
    }

    // ⚠ Forward slashes: a Cufet text literal reads a backslash as an escape.
    private string LibraryPath => Library.Replace('\\', '/');

    private void PinBlueprint(string name, string commit) =>
        File.WriteAllText(Path.Combine(Project, "blueprint.cufe"), $"""
            Pull a book on blueprints.
                Bind series of pin to books:
                    Return a series of pin with (
                        a record with (
                            the name "{name}",
                            the source "{LibraryPath}",
                            the commit "{commit}")).
                Done.
            Done.
            """);

    private const string Canvas = """
        Define object canvas with () and book:
            Bind text to draw: Return "drawn by the fetched canvas". Done.
        Done.
        """;

    [Fact]
    public void APinnedBook_IsFetchedAndCanThenBePulled()
    {
        var sha = PublishBook("canvas", Canvas);
        PinBlueprint("canvas", sha);

        var (exit, stdout, stderr) = Install();
        Assert.True(exit == 0, $"install failed: {stderr}");
        Assert.Contains("canvas at " + sha, stdout);

        // ★ It landed where a pull looks — flat in `books/`, one file per name, which mirrors the
        // language's own rule that a program holds one book per NAME.
        var installed = Path.Combine(Project, "books", "canvas.cufe");
        Assert.True(File.Exists(installed), "books/canvas.cufe was not written");

        // ★★ And the point of the whole exercise: a program can now pull it.
        File.WriteAllText(Path.Combine(Project, "main.cufe"), """
            Pull a book on canvas.
                State cast canvas's draw.
            Done.
            """);
        var used = Run(CufetExe, Project, "main.cufe");
        Assert.Equal(0, used.Exit);
        Assert.Contains("drawn by the fetched canvas", used.Out);
    }

    /// <remarks>★ The clone is a CACHE, so a second install does not fetch again. That is the whole
    /// reason it is kept rather than deleted — and the reason no delete capability was needed.</remarks>
    [Fact]
    public void ASecondInstall_ReusesTheCloneInsteadOfFetchingAgain()
    {
        var sha = PublishBook("canvas", Canvas);
        PinBlueprint("canvas", sha);

        var first = Install();
        Assert.Equal(0, first.Exit);
        Assert.Contains("fetching canvas", first.Out);

        var second = Install();
        Assert.Equal(0, second.Exit);
        Assert.DoesNotContain("fetching canvas", second.Out);
        Assert.Contains("canvas at " + sha, second.Out);
    }

    /// <remarks>
    /// ⚠ A pin that names a commit the source does not have must SAY so. Silently installing
    /// whatever the clone happened to have is the failure mode a pin exists to prevent.
    /// </remarks>
    [Fact]
    public void ACommitThatDoesNotExist_IsRefusedRatherThanApproximated()
    {
        PublishBook("canvas", Canvas);
        PinBlueprint("canvas", "0123456789abcdef0123456789abcdef01234567");

        var (exit, stdout, _) = Install();

        Assert.NotEqual(0, exit);
        Assert.Contains("canvas", stdout);
        Assert.False(File.Exists(Path.Combine(Project, "books", "canvas.cufe")),
                     "nothing should be installed when the pinned commit is missing");
    }

    /// <remarks>
    /// ⚠⚠ THE ORDINARY CASE, and it must not read as a mistake. Most projects depend on no books,
    /// so a blueprint with steps and no pins has no `books` binding — and casting one that does not
    /// exist would refuse with *"'books' isn't defined"*, a message about the language for something
    /// entirely normal. The CLI asks the PARSED file instead.
    /// </remarks>
    [Fact]
    public void ABlueprintThatPinsNothing_SaysSoAndSucceeds()
    {
        File.WriteAllText(Path.Combine(Project, "blueprint.cufe"), """
            Pull a book on blueprints.
                Bind series of step to blueprint:
                    Return a series of step with (
                        a record with (
                            the name "thing",
                            the needs a series of text with ("a.cufe"),
                            the makes a series of text with ("a.exe"),
                            the runs a series of text with ("cufet", "build", "a.cufe"))).
                Done.
            Done.
            """);

        var (exit, stdout, _) = Install();

        Assert.Equal(0, exit);
        Assert.Contains("pins no books", stdout);
        Assert.DoesNotContain("isn't defined", stdout);
    }

    /// <remarks>★ The same refusal shape `cufet build` gives, naming what to do rather than only
    /// what is missing.</remarks>
    [Fact]
    public void WithNoBlueprint_ItSaysWhereItLooked()
    {
        var (exit, _, stderr) = Install();

        Assert.Equal(1, exit);
        Assert.Contains("there is no blueprint.cufe here", stderr);
        Assert.Contains("pinned in its blueprint", stderr);
    }
}
