using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `cufet pulls <file>` — which FILES a file brings in.
/// </summary>
/// <remarks>
/// <para>
/// ★★ It exists so a BLUEPRINT need not list them. A blueprint can already enumerate its own
/// directory in ordinary Cufet — measured 2026-09-15, including a new file being picked up with no
/// edit — but a directory listing cannot see that `shell.cufe` PULLS `terminal.cufe`, so a
/// generated step's `needs` held only its own file and editing a book rebuilt nothing.
/// </para>
/// <para>
/// ⚠⚠ It reports what the LOADER RESOLVED rather than re-reading the source. Matching `Pull ` by
/// hand over-declares on a comment, which is harmless, and UNDER-declares on any form it does not
/// know — and an under-declared `need` is a silently stale build, which is the failure the whole
/// staleness design exists to avoid.
/// </para>
/// </remarks>
public class PullsCommandTests : IDisposable
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

    private readonly string _dir =
        Path.Combine(TestScratch.Root, "pulls-" + Guid.NewGuid().ToString("N"));

    public PullsCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Write(string name, string body) =>
        File.WriteAllText(Path.Combine(_dir, name), body);

    /// <remarks>⚠ Run IN the fixture directory, so the reported paths are the relative ones a
    /// step's `needs` would actually hold.</remarks>
    private (int Exit, string Out, string Err) Pulls(params string[] args)
    {
        var psi = new ProcessStartInfo(CufetExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = _dir,
        };
        psi.ArgumentList.Add("pulls");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, stdout.Replace("\r\n", "\n"), stderr);
    }

    private const string Helper = """
        Define object helper with () and book:
            Bind text to greet: Return "hi". Done.
        Done.
        """;

    [Fact]
    public void AFileThatPullsAnother_ReportsIt()
    {
        Write("helper.cufe", Helper);
        Write("main.cufe", """
            Pull a book on helper.
                State cast helper's greet.
            Done.
            """);

        var (exit, stdout, _) = Pulls("main.cufe");

        Assert.Equal(0, exit);
        Assert.Equal("main.cufe: helper.cufe\n", stdout);
    }

    /// <remarks>
    /// ★ By construction rather than by filtering: a bundled book is spliced in without ever being
    /// a path, so it never enters the SourceMap. That is exactly right for a build, where a `need`
    /// has to be something that can be hashed — `math` cannot.
    /// </remarks>
    [Fact]
    public void ABundledBook_IsNotAFileAndIsNotReported()
    {
        Write("main.cufe", """
            Pull a book on math.
                State math's pi.
            Done.
            """);

        var (exit, stdout, _) = Pulls("main.cufe");

        Assert.Equal(0, exit);
        Assert.Equal("", stdout);
    }

    /// <remarks>
    /// ⚠ TRANSITIVE, and it has to be: if `middle` pulls `deep`, then editing `deep` is a reason to
    /// rebuild whatever pulled `middle`. A build that stopped at one level would go stale silently.
    /// </remarks>
    [Fact]
    public void APullChain_IsFollowedAllTheWayDown()
    {
        Write("deep.cufe", """
            Define object deep with () and book:
                Bind text to say: Return "deep". Done.
            Done.
            """);
        Write("middle.cufe", """
            Pull a book on deep.
                Define object middle with () and book:
                    Bind text to say: Return "via " joined to cast deep's say. Done.
                Done.
            Done.
            """);
        Write("main.cufe", """
            Pull books on deep, and middle.
                State cast middle's say.
            Done.
            """);

        var (exit, stdout, _) = Pulls("main.cufe");

        Assert.Equal(0, exit);
        Assert.Contains("main.cufe: middle.cufe", stdout);
        Assert.Contains("main.cufe: deep.cufe", stdout);
    }

    /// <remarks>
    /// ⚠ One shape, always prefixed, however many files are asked about. A blueprint is the
    /// consumer, and two output shapes would mean a branch exercised in only one of them.
    /// </remarks>
    [Fact]
    public void SeveralFiles_EachLineSaysWhichFileItIsAbout()
    {
        Write("helper.cufe", Helper);
        Write("one.cufe", """
            Pull a book on helper.
                State cast helper's greet.
            Done.
            """);
        Write("two.cufe", """
            Pull a book on helper.
                State cast helper's greet.
            Done.
            """);

        var (exit, stdout, _) = Pulls("one.cufe", "two.cufe");

        Assert.Equal(0, exit);
        Assert.Contains("one.cufe: helper.cufe", stdout);
        Assert.Contains("two.cufe: helper.cufe", stdout);
    }

    /// <summary>A program that will not type-check still has pulls, and still has to be rebuilt.</summary>
    /// <remarks>
    /// ⚠⚠ A deliberate decision, not an accident of ordering. `pulls` is a question about LOADING,
    /// and a build whose source is momentarily broken must still know what to watch — otherwise
    /// fixing the file would not be noticed. Loading happens early enough in `Check` that the map
    /// is filled before anything can refuse.
    /// </remarks>
    [Fact]
    public void AFileThatDoesNotTypeCheck_StillReportsItsPulls()
    {
        Write("helper.cufe", Helper);
        Write("broken.cufe", """
            Pull a book on helper.
                State cast helper's greet.
                State "this is broken" joined to 3.
            Done.
            """);

        var (exit, stdout, _) = Pulls("broken.cufe");

        Assert.Equal(0, exit);
        Assert.Contains("broken.cufe: helper.cufe", stdout);
    }

    [Fact]
    public void AMissingFile_IsAnError()
    {
        var (exit, _, stderr) = Pulls("nothing-here.cufe");

        Assert.Equal(2, exit);
        Assert.Contains("nothing-here.cufe", stderr);
    }

    [Fact]
    public void NoFileAtAll_SaysWhatItWanted()
    {
        var psi = new ProcessStartInfo(CufetExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = _dir,
        };
        psi.ArgumentList.Add("pulls");
        using var p = Process.Start(psi)!;
        string stderr = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(60_000);

        // ⚠ `cufet pulls` with nothing after it is one argument, so it falls through to the
        // run-a-file branch and is refused there — the same way `cufet tokens` alone is.
        Assert.NotEqual(0, p.ExitCode);
        Assert.NotEqual("", stderr);
    }
}
