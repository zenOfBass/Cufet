using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// The `cufet` command itself — what it accepts, and what it refuses.
/// </summary>
/// <remarks>
/// <para>
/// ★ The CLI had NO tests before these, and it shows in what was wrong: every verb silently
/// dropped arguments it did not understand. `cufet build a.cufe -o out.exe` wrote the binary beside
/// the SOURCE and said nothing about `-o`, which is not a flag this CLI has — a whole session's
/// binaries went somewhere other than where they were asked for before anyone noticed. A mistyped
/// `--jsno` on `check` disabled JSON just as quietly.
/// </para>
/// <para>
/// ⚠ These drive the real executable as a SUBPROCESS, because `Program.cs` is top-level statements
/// that end in `Environment.Exit` — there is nothing to call into. The csproj references the App
/// with `ReferenceOutputAssembly="false"` purely so the binary exists when these run.
/// </para>
/// <para>
/// Exit codes are the contract being pinned: 0 and 1 are the PROGRAM's answers (it ran, it did
/// not), so a mistake in the COMMAND is 2 — a script cannot tell the three apart otherwise.
/// </para>
/// </remarks>
public class CommandLineTests
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

    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(CufetExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = RepoRoot,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, stdout, stderr);
    }

    /// <summary>A source file that checks and runs cleanly, for the accept-side tests.</summary>
    private static string WriteProgram(string body)
    {
        string path = Path.Combine(Path.GetTempPath(), "cufet-cli-" + Guid.NewGuid().ToString("N") + ".cufe");
        File.WriteAllText(path, body);
        return path;
    }

    // ── What it refuses ───────────────────────────────────────────────────

    [Fact]
    public void Build_WithAnUnknownFlag_IsRefusedRatherThanIgnored()
    {
        // ⚠ THE regression. `-o` is not a flag `build` has, and passing it used to succeed while
        // writing the binary somewhere else entirely.
        string file = WriteProgram("State 1.\n");
        try
        {
            var (exit, _, err) = Run("build", file, "-o", "somewhere.exe");
            Assert.Equal(2, exit);
            Assert.Contains("-o", err);
            Assert.Contains("not a flag build takes", err);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Check_WithAMistypedFlag_IsRefused()
    {
        // `--jsno` used to disable JSON in silence — the output looked fine and was the wrong shape.
        string file = WriteProgram("State 1.\n");
        try
        {
            var (exit, _, err) = Run("check", "--jsno", file);
            Assert.Equal(2, exit);
            Assert.Contains("--jsno", err);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Check_WithASecondFile_IsRefused()
    {
        // Only the first was ever read. A script checking two files got one answer and no warning.
        string file = WriteProgram("State 1.\n");
        try
        {
            var (exit, _, err) = Run("check", file, file);
            Assert.Equal(2, exit);
            Assert.Contains("don't know what to do with", err);
        }
        finally { File.Delete(file); }
    }

    // ── What it still accepts ─────────────────────────────────────────────

    [Fact]
    public void EveryDocumentedFormStillWorks()
    {
        string file = WriteProgram("State 1.\n");
        try
        {
            Assert.Equal(0, Run(file).Exit);                                        // run
            Assert.Equal(0, Run("check", file).Exit);                               // check
            Assert.Equal(0, Run("check", "--json", "--native", "--strict", file).Exit);
            Assert.Equal(0, Run("tokens", "--json", file).Exit);
        }
        finally { File.Delete(file); }
    }

    /// <summary>
    /// Extra arguments after a SOURCE FILE are still accepted, and deliberately.
    /// </summary>
    /// <remarks>
    /// ★ The one place the silence stays. `cufet script.cufe one two` drops `one two` because the
    /// language has no way to read them — but that spelling is exactly where program arguments would
    /// arrive if they are ever added, and the shell on the roadmap will want them. Refusing it now
    /// would only have to be un-refused later.
    /// </remarks>
    [Fact]
    public void ArgumentsAfterASourceFile_AreLeftAlone()
    {
        string file = WriteProgram("State 1.\n");
        try
        {
            var (exit, stdout, _) = Run(file, "one", "two");
            Assert.Equal(0, exit);
            Assert.Contains("1", stdout);
        }
        finally { File.Delete(file); }
    }
}
