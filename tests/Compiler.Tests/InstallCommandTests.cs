using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `cufet install` — the books a project pins, read from its blueprint.
/// </summary>
/// <remarks>
/// <para>
/// ★★ **Reporting before acting**, which is the order `cufet pulls` established and the reason this
/// slice exists on its own. A dependency list you can read before anything is downloaded is the
/// half that can be checked; one you can only see afterwards is a list you find out about.
/// </para>
/// <para>
/// ★ The command is the same shape as `cufet build`: read the blueprint, append a call to a walker
/// WRITTEN IN CUFET, run it. The CLI holds no policy about what a pin means — that lives in
/// `blueprints`, where a reader can find it.
/// </para>
/// <para>
/// ⚠ A pin records a COMMIT rather than a version. A version string would be a second name for the
/// same thing, and a content checksum cannot survive a `git` checkout that rewrites line endings —
/// MEASURED, this repo runs `core.autocrlf=true` with no `.gitattributes`.
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

    private readonly string _dir =
        Path.Combine(TestScratch.Root, "install-" + Guid.NewGuid().ToString("N"));

    public InstallCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void WriteBlueprint(string body) =>
        File.WriteAllText(Path.Combine(_dir, "blueprint.cufe"), body);

    private (int Exit, string Out, string Err) Install()
    {
        var psi = new ProcessStartInfo(CufetExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = _dir,
        };
        psi.ArgumentList.Add("install");

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, stdout.Replace("\r\n", "\n"), stderr.Replace("\r\n", "\n"));
    }

    [Fact]
    public void EveryPinnedBook_IsReportedWithItsSourceAndCommit()
    {
        WriteBlueprint("""
            Pull a book on blueprints.
                Bind series of pin to books:
                    Return a series of pin with (
                        a record with (
                            the name "canvas",
                            the source "github.com/zenOfBass/canvas",
                            the commit "3f9a1c7e"),
                        a record with (
                            the name "palette",
                            the source "github.com/zenOfBass/palette",
                            the commit "aa12bb34")).
                Done.
            Done.
            """);

        var (exit, stdout, _) = Install();

        Assert.Equal(0, exit);
        Assert.Contains("canvas <- github.com/zenOfBass/canvas at 3f9a1c7e", stdout);
        Assert.Contains("palette <- github.com/zenOfBass/palette at aa12bb34", stdout);
        // ★ In the order the blueprint lists them, because a reader checking a list against a file
        // should not have to sort it first.
        Assert.True(stdout.IndexOf("canvas", StringComparison.Ordinal)
                  < stdout.IndexOf("palette", StringComparison.Ordinal),
                    "pins should be reported in blueprint order");
    }

    /// <remarks>
    /// ⚠⚠ THE ORDINARY CASE, and it must not read as a mistake. Most projects depend on no books at
    /// all, so a blueprint with steps and no pins has no `books` binding — and casting one that does
    /// not exist would refuse with *"'books' isn't defined"*, a message about the language for
    /// something that is simply normal. The CLI asks the PARSED file instead.
    /// </remarks>
    [Fact]
    public void ABlueprintThatPinsNothing_SaysSoAndSucceeds()
    {
        WriteBlueprint("""
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

    /// <remarks>★ The same refusal shape `cufet build` gives, and it names what to do rather than
    /// only what is missing.</remarks>
    [Fact]
    public void WithNoBlueprint_ItSaysWhereItLooked()
    {
        var (exit, _, stderr) = Install();

        Assert.Equal(1, exit);
        Assert.Contains("there is no blueprint.cufe here", stderr);
        Assert.Contains("pinned in its blueprint", stderr);
    }
}
