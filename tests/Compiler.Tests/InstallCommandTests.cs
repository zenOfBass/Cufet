using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `cufet install` — fetching the books a project pins, at the commits it pins them to, and the
/// books THOSE books pin.
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
/// `--no-checkout` plus `git show &lt;commit&gt;:&lt;file&gt;` reads straight out of the object
/// store, which no `core.autocrlf` setting touches.
/// </para>
/// <para>
/// ⚠ The clone is KEPT, at `books/.cufet-cache/‹name›`, and that location is measured rather than
/// chosen: `Write` does not create parent directories and Cufet cannot make one, so `books/` has to
/// be created by something — and `git clone` creates its target including parents.
/// </para>
/// <para>
/// ⚠⚠ **There is no cycle test, and that is a finding rather than a gap.** A pin names a commit,
/// and a commit cannot contain its own sha — so for book A to pin B while B pins that same A, B
/// would have to be committed knowing a sha that does not exist until B exists. Exact pins are
/// ACYCLIC BY CONSTRUCTION, for the same reason a git history is. The installer's seen-set still
/// guards the loop, and <see cref="ADiamond_AtTheSameCommit_IsFetchedOnce"/> is what exercises it.
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

    public InstallCommandTests() => Directory.CreateDirectory(Project);

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

    private readonly record struct Pinned(string Name, string Source, string Commit);

    /// <summary>Where a published book's own repository lives.</summary>
    private string RepoOf(string name) => Path.Combine(_root, "source-" + name);

    /// <summary>A second, separate repository that happens to hold a book of the same name.</summary>
    private string OtherRepoOf(string name) => Path.Combine(_root, "other-" + name);

    /// <summary>
    /// Publishes a book as a real git repo, with its own blueprint if it pins anything, and hands
    /// back the commit sha. Publishing the same name twice commits again to the same repo, which is
    /// how a source comes to have two commits worth pinning.
    /// </summary>
    private string Publish(string name, string body, params Pinned[] pins) =>
        PublishInto(RepoOf(name), name, body, pins);

    private string PublishInto(string repo, string name, string body, params Pinned[] pins)
    {
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, name + ".cufe"), body);
        if (pins.Length > 0)
            File.WriteAllText(Path.Combine(repo, "blueprint.cufe"), Blueprint(pins));

        Run("git", repo, "init", "--quiet");
        Run("git", repo, "add", "-A");
        Run("git", repo, "-c", "user.email=t@example.com", "-c", "user.name=t",
            "commit", "--quiet", "-m", name);
        return Run("git", repo, "rev-parse", "HEAD").Out.Trim();
    }

    private Pinned Pin(string name, string commit) => new(name, RepoOf(name), commit);

    /// <remarks>
    /// ⚠ Forward slashes: a Cufet text literal reads a backslash as an escape, so a Windows path
    /// written straight into generated Cufet becomes a lexer error about `\U`.
    /// </remarks>
    private static string Blueprint(params Pinned[] pins)
    {
        string records = string.Join(",\n", pins.Select(p =>
            "            a record with (\n"
          + $"                the name \"{p.Name}\",\n"
          + $"                the source \"{p.Source.Replace('\\', '/')}\",\n"
          + $"                the commit \"{p.Commit}\")"));

        return "Pull a book on blueprints.\n"
             + "    Bind series of pin to books:\n"
             + "        Return a series of pin with (\n"
             + records + ").\n"
             + "    Done.\n"
             + "Done.\n";
    }

    private void PinsInProject(params Pinned[] pins) =>
        File.WriteAllText(Path.Combine(Project, "blueprint.cufe"), Blueprint(pins));

    private const string Canvas = """
        Define object canvas with () and book:
            Bind text to draw: Return "drawn by the fetched canvas". Done.
        Done.
        """;

    private const string Deep = """
        Define object deep with () and book:
            Bind text to greet: Return "hello from deep". Done.
        Done.
        """;

    /// <summary>A canvas that cannot work unless `deep` is installed alongside it.</summary>
    private const string CanvasOverDeep = """
        Pull a book on deep.
            Define object canvas with () and book:
                Bind text to draw: Return "canvas says: {cast deep's greet}". Done.
            Done.
        Done.
        """;

    private void ProgramPulling(string book, string call) =>
        File.WriteAllText(Path.Combine(Project, "main.cufe"),
            $"Pull a book on {book}.\n    State cast {book}'s {call}.\nDone.\n");

    private bool Installed(string name) =>
        File.Exists(Path.Combine(Project, "books", name + ".cufe"));

    [Fact]
    public void APinnedBook_IsFetchedAndCanThenBePulled()
    {
        var sha = Publish("canvas", Canvas);
        PinsInProject(Pin("canvas", sha));

        var (exit, stdout, stderr) = Install();
        Assert.True(exit == 0, $"install failed: {stderr}");
        Assert.Contains("canvas at " + sha, stdout);

        // ★ It landed where a pull looks — flat in `books/`, one file per name, which mirrors the
        // language's own rule that a program holds one book per NAME.
        Assert.True(Installed("canvas"), "books/canvas.cufe was not written");

        // ★★ And the point of the whole exercise: a program can now pull it.
        ProgramPulling("canvas", "draw");
        var used = Run(CufetExe, Project, "main.cufe");
        Assert.Equal(0, used.Exit);
        Assert.Contains("drawn by the fetched canvas", used.Out);
    }

    /// <remarks>
    /// ★★ THE POINT OF TRANSITIVE PINS. The project names `canvas` and nothing else; `deep` is
    /// canvas's business and the project never learns it exists. Without this, a library's private
    /// dependencies become part of every consumer's spelling — the exact thing module privacy
    /// removed at the language level, reappearing in the package manager.
    /// </remarks>
    [Fact]
    public void ABookTheProjectNeverPinned_IsFetchedBecauseItsBookPinsIt()
    {
        var deep   = Publish("deep", Deep);
        var canvas = Publish("canvas", CanvasOverDeep, Pin("deep", deep));
        PinsInProject(Pin("canvas", canvas));

        var (exit, stdout, stderr) = Install();
        Assert.True(exit == 0, $"install failed: {stderr}");

        Assert.True(Installed("canvas"), "books/canvas.cufe was not written");
        Assert.True(Installed("deep"), "books/deep.cufe was not written — the pin was not followed");

        // ⚠ SAID OUT LOUD. Running a fetched repository's build description is the one place this
        // tool executes code it did not get from the person running it, so it names each one first.
        Assert.Contains("running canvas's blueprint", stdout);

        ProgramPulling("canvas", "draw");
        var used = Run(CufetExe, Project, "main.cufe");
        Assert.Equal(0, used.Exit);
        Assert.Contains("canvas says: hello from deep", used.Out);
    }

    /// <remarks>★ The case the whole design is FOR: two books wanting the same book at the same
    /// commit is agreement, not conflict, and it costs one fetch.</remarks>
    [Fact]
    public void ADiamond_AtTheSameCommit_IsFetchedOnce()
    {
        var deep  = Publish("deep", Deep);
        var left  = Publish("canvas", Canvas, Pin("deep", deep));
        var right = Publish("palette", Palette, Pin("deep", deep));
        PinsInProject(Pin("canvas", left), Pin("palette", right));

        var (exit, stdout, stderr) = Install();
        Assert.True(exit == 0, $"install failed: {stderr}");
        Assert.True(Installed("deep"));

        // ⚠⚠ COUNTED ON THE INSTALL LINE, not on "fetching". MEASURED: with the seen-set sabotaged
        // away, `deep` is processed TWICE and "fetching deep" still appears once — because the
        // second pass finds the clone already there and says nothing. The test went green over the
        // defect it was written for. What only the seen-set prevents is the second INSTALL.
        Assert.Equal(1, stdout.Split("deep at " + deep).Length - 1);
        Assert.Equal(1, stdout.Split("fetching deep").Length - 1);
    }

    private const string Palette = """
        Define object palette with () and book:
            Bind text to shade: Return "a shade". Done.
        Done.
        """;

    /// <remarks>
    /// ⚠⚠ NAMING BOTH, because neither pin alone is the mistake. A program holds one book per NAME,
    /// so exact pins force a SELECTION — and there is nobody but the author to make it. Picking
    /// "the newer" would need an ordering that commits do not have.
    /// </remarks>
    [Fact]
    public void ADiamond_AtDifferentCommits_IsRefusedNamingBoth()
    {
        var first  = Publish("deep", Deep);
        var second = Publish("deep", Deep.Replace("hello from deep", "hello again"));
        Assert.NotEqual(first, second);

        var left  = Publish("canvas", Canvas, Pin("deep", first));
        var right = Publish("palette", Palette, Pin("deep", second));
        PinsInProject(Pin("canvas", left), Pin("palette", right));

        var (exit, _, stderr) = Install();

        Assert.NotEqual(0, exit);
        Assert.Contains("'deep' is pinned twice", stderr);
        Assert.Contains(first, stderr);
        Assert.Contains(second, stderr);
    }

    /// <remarks>★ A fetched book with a blueprint that pins nothing is the ordinary library: it has
    /// a build description because it is a project when you develop it, and no dependencies.</remarks>
    [Fact]
    public void AFetchedBlueprintThatPinsNothing_StillInstallsItsBook()
    {
        string repo = RepoOf("canvas");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "blueprint.cufe"), StepsOnly);
        var sha = Publish("canvas", Canvas);
        PinsInProject(Pin("canvas", sha));

        var (exit, _, stderr) = Install();

        Assert.True(exit == 0, $"install failed: {stderr}");
        Assert.True(Installed("canvas"));
    }

    private const string StepsOnly = """
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
        """;

    /// <remarks>
    /// ⚠ A pin that names a commit the source does not have must SAY so. Silently installing
    /// whatever the clone happened to have is the failure mode a pin exists to prevent.
    /// </remarks>
    [Fact]
    public void ACommitThatDoesNotExist_IsRefusedRatherThanApproximated()
    {
        Publish("canvas", Canvas);
        PinsInProject(new Pinned("canvas", RepoOf("canvas"),
                                 "0123456789abcdef0123456789abcdef01234567"));

        var (exit, _, stderr) = Install();

        Assert.NotEqual(0, exit);
        Assert.Contains("canvas", stderr);
        Assert.False(Installed("canvas"),
                     "nothing should be installed when the pinned commit is missing");
    }

    /// <remarks>★ The clone is a CACHE, so a second install does not fetch again. That is the whole
    /// reason it is kept rather than deleted — and the reason no delete capability was needed.</remarks>
    [Fact]
    public void ASecondInstall_ReusesTheCloneInsteadOfFetchingAgain()
    {
        var sha = Publish("canvas", Canvas);
        PinsInProject(Pin("canvas", sha));

        var first = Install();
        Assert.Equal(0, first.Exit);
        Assert.Contains("fetching canvas", first.Out);

        var second = Install();
        Assert.Equal(0, second.Exit);
        Assert.DoesNotContain("fetching canvas", second.Out);
        Assert.Contains("canvas at " + sha, second.Out);
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
        File.WriteAllText(Path.Combine(Project, "blueprint.cufe"), StepsOnly);

        var (exit, stdout, _) = Install();

        Assert.Equal(0, exit);
        Assert.Contains("pins no books", stdout);
        Assert.DoesNotContain("isn't defined", stdout);
    }


    // ── The record of what was installed ─────────────────────────────────────────

    private string RecordPath => Path.Combine(Project, ".cufet-pins");

    /// <remarks>
    /// ★★ **WHY A RECORD EXISTS AT ALL**, and it is not the usual lockfile argument. A blueprint may
    /// COMPUTE its pins — that was chosen deliberately — so the closure is not a function of the
    /// files alone. MEASURED: one blueprint, unchanged and at one commit, pinned a different book
    /// depending on whether it ran inside a git repository. Without this file nothing anywhere says
    /// what a project actually installed.
    ///
    /// ⚠ It is the TOOL's file, never `blueprint.cufe`. Writing the closure back into the source
    /// you wrote is `go mod tidy`; every ecosystem that does this splits the two for that reason.
    /// </remarks>
    [Fact]
    public void TheRecord_NamesEveryBookThatWasInstalled()
    {
        var deep   = Publish("deep", Deep);
        var canvas = Publish("canvas", CanvasOverDeep, Pin("deep", deep));
        PinsInProject(Pin("canvas", canvas));

        Assert.Equal(0, Install().Exit);

        var lines = File.ReadAllLines(RecordPath);
        // ⚠ SORTED BY NAME, so the file does not reshuffle with the order the graph was walked —
        // a record that cannot be diffed is one nobody will read.
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("canvas\t", lines[0]);
        Assert.StartsWith("deep\t", lines[1]);
        Assert.Contains(canvas, lines[0]);
        // ★ The transitive one is in it too — the project never named `deep` anywhere.
        Assert.Contains(deep, lines[1]);
    }

    /// <remarks>★ The first install must not be the one that complains: having no record is the
    /// ordinary state of a project nobody has installed yet.</remarks>
    [Fact]
    public void WithNoRecordYet_TheFirstInstallJustInstalls()
    {
        var sha = Publish("canvas", Canvas);
        PinsInProject(Pin("canvas", sha));

        Assert.False(File.Exists(RecordPath));
        Assert.Equal(0, Install().Exit);
        Assert.True(File.Exists(RecordPath));
    }

    /// <remarks>
    /// ⚠⚠ The whole point: a name that resolves somewhere else than last time is REFUSED, naming
    /// both — the same shape as a diamond pinned twice, because it is the same fact arriving from a
    /// different direction. ★ And it refuses BEFORE fetching, so nothing is downloaded on the way to
    /// the complaint.
    /// </remarks>
    [Fact]
    public void APinThatChanged_IsRefusedAgainstTheRecord()
    {
        var first = Publish("canvas", Canvas);
        PinsInProject(Pin("canvas", first));
        Assert.Equal(0, Install().Exit);

        var second = PublishInto(OtherRepoOf("canvas"), "canvas", Canvas);
        PinsInProject(new Pinned("canvas", OtherRepoOf("canvas"), second));

        var (exit, stdout, stderr) = Install();

        Assert.NotEqual(0, exit);
        Assert.Contains("is not what the last install got", stderr);
        Assert.Contains(".cufet-pins", stderr);
        Assert.DoesNotContain("fetching canvas", stdout);
    }

    /// <remarks>
    /// ★ Deleting the record is the escape hatch, and there is no flag — the same deal
    /// `blueprints` gives `.cufet-build`.
    ///
    /// ⚠⚠ AND THE CLONE HAS TO FOLLOW. The cache is keyed by NAME, so repinning a book to another
    /// repository used to leave the OLD repository's clone under the new name, and `git show` then
    /// reported the pinned commit as one that "does not exist" — true of that clone and nothing to
    /// do with what was wrong. MEASURED 2026-09-18 by taking this very escape hatch.
    /// </remarks>
    [Fact]
    public void DeletingTheRecord_LetsTheNewPinIn_AndReClonesFromIt()
    {
        var first = Publish("canvas", Canvas);
        PinsInProject(Pin("canvas", first));
        Assert.Equal(0, Install().Exit);

        const string Elsewhere = """
            Define object canvas with () and book:
                Bind text to draw: Return "drawn by the OTHER canvas". Done.
            Done.
            """;
        var second = PublishInto(OtherRepoOf("canvas"), "canvas", Elsewhere);
        PinsInProject(new Pinned("canvas", OtherRepoOf("canvas"), second));

        File.Delete(RecordPath);
        var (exit, _, stderr) = Install();

        Assert.True(exit == 0, $"install failed after the record was deleted: {stderr}");
        Assert.Contains("drawn by the OTHER canvas",
                        File.ReadAllText(Path.Combine(Project, "books", "canvas.cufe")));
        Assert.Contains(second, File.ReadAllText(RecordPath));
    }

    /// <remarks>⚠ A failed install records nothing — there is no closure to record, and writing a
    /// partial one would make the next install compare against something that never happened.</remarks>
    [Fact]
    public void AFailedInstall_WritesNoRecord()
    {
        Publish("canvas", Canvas);
        PinsInProject(new Pinned("canvas", RepoOf("canvas"),
                                 "0123456789abcdef0123456789abcdef01234567"));

        Assert.NotEqual(0, Install().Exit);
        Assert.False(File.Exists(RecordPath), ".cufet-pins was written by an install that failed");
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
