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

    // ── Program arguments ─────────────────────────────────────────────────
    //
    // ★★ These have to live HERE rather than in an interpreter test, because the thing under test
    // is the CLI's own slicing: `args[1..]` in Program.cs. An interpreter test would set
    // ProgramArguments by hand and pass no matter what the CLI did with the command line — which
    // is the whole defect these guard against.

    [Fact]
    public void TheArguments_AreWhatFollowedTheScriptPath()
    {
        string file = WriteProgram("For each arg in the arguments, repeat: State arg. Done.\n");
        try
        {
            var (exit, output, _) = Run(file, "one", "two");
            Assert.Equal(0, exit);
            Assert.Equal(["one", "two"], output.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'));
        }
        finally { File.Delete(file); }
    }

    // ⚠ The script path is argument ZERO's business and is never handed out. `cufet script.cufe`
    // and a compiled `./script` disagree about the head — `cufet` against `./script`, with the
    // path present in one and absent in the other — so there is no definition of it the two
    // backends could share. Leaving it out is what makes them agree.
    [Fact]
    public void TheArguments_DoNotIncludeTheScriptPath()
    {
        string file = WriteProgram("State the number of the arguments.\n");
        try
        {
            Assert.Equal("0", Run(file).Out.Trim());
            Assert.Equal("1", Run(file, "only").Out.Trim());
        }
        finally { File.Delete(file); }
    }

    // ★ None is an EMPTY series, never void — arguments are a sequence, not the environment's
    // keyed lookup. A program can walk them without asking whether they are there.
    [Fact]
    public void TheArguments_AreAnEmptySeriesWhenThereAreNone()
    {
        string file = WriteProgram(
            "If the number of the arguments is 0, State \"none\".\n" +
            "For each arg in the arguments, repeat: State arg. Done.\n");
        try
        {
            var (exit, output, _) = Run(file);
            Assert.Equal(0, exit);
            Assert.Equal("none", output.Trim());
        }
        finally { File.Delete(file); }
    }

    // ★★ The oracle, end to end and through the real CLI: the SAME program handed the SAME
    // arguments must answer the same both ways. `cufet script.cufe a b` against `./script a b` is
    // exactly where argument zero would have diverged if it were included.
    [Fact]
    public void TheArguments_AgreeBetweenInterpretedAndCompiled()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;

        string file = WriteProgram(
            "State the number of the arguments.\n" +
            "For each arg in the arguments, repeat: State arg. Done.\n");
        // ⚠ null, NOT "". Path.ChangeExtension(path, "") leaves a TRAILING DOT — `/tmp/x.cufe`
        // becomes `/tmp/x.` — which is a real filename on Windows-by-luck and nothing at all on
        // Linux. Both of these passed here and failed under WSL for exactly that reason.
        string exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.ChangeExtension(file, ".exe")
            : Path.ChangeExtension(file, null)!;
        try
        {
            string interpreted = Run(file, "alpha", "beta").Out.ReplaceLineEndings("\n");

            var (buildExit, _, buildErr) = Run("build", file);
            Assert.True(buildExit == 0, "build failed: " + buildErr);

            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, WorkingDirectory = RepoRoot };
            psi.ArgumentList.Add("alpha");
            psi.ArgumentList.Add("beta");
            using var p = Process.Start(psi)!;
            string compiled = p.StandardOutput.ReadToEnd().ReplaceLineEndings("\n");
            p.WaitForExit(60_000);

            Assert.Equal("2\nalpha\nbeta\n", interpreted);
            Assert.Equal(interpreted, compiled);
        }
        finally
        {
            File.Delete(file);
            if (File.Exists(exe)) File.Delete(exe);
        }
    }

    // ── The exit status a program chooses ─────────────────────────────────
    //
    // ★★ Here rather than in an interpreter test for the same reason the argument tests are: the
    // thing under test is that `cufet script.cufe` LEAVES with the status, which only the real
    // process can show. An interpreter test could read `ExitStatus` and pass while the CLI
    // returned 0 to the shell — which is the defect, not the check.

    [Fact]
    public void Exit_LeavesWithTheStatusItNames()
    {
        string file = WriteProgram("State \"before\".\nExit with 3.\nState \"after\".\n");
        try
        {
            var (exit, output, _) = Run(file);
            Assert.Equal(3, exit);
            Assert.Equal("before", output.Trim());   // nothing after the Exit runs
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void ABareExit_IsStatusZero()
    {
        string file = WriteProgram("State \"a\".\nExit.\nState \"b\".\n");
        try
        {
            var (exit, output, _) = Run(file);
            Assert.Equal(0, exit);
            Assert.Equal("a", output.Trim());
        }
        finally { File.Delete(file); }
    }

    // ★ A status past 255 is refused rather than truncated. POSIX keeps the low eight bits, so
    // `Exit with 256.` would leave with 0 — success, from a line that plainly meant otherwise.
    // Exit 1 here is CUFET refusing the program, not the program's own answer.
    [Fact]
    public void AStatusOutsideZeroToTwoFiveFive_IsRefusedRatherThanTruncated()
    {
        string file = WriteProgram("Exit with 256.\n");
        try
        {
            var (exit, _, err) = Run(file);
            Assert.Equal(1, exit);
            Assert.Contains("0 to 255", err);
        }
        finally { File.Delete(file); }
    }

    // ⚠ A fractional status and an out-of-range one are different mistakes and get different
    // sentences. One message covering both told someone who wrote 2.5 about the low eight bits.
    [Fact]
    public void AFractionalStatus_SaysItIsNotWholeRatherThanOutOfRange()
    {
        string file = WriteProgram("Exit with 2.5.\n");
        try
        {
            var (exit, _, err) = Run(file);
            Assert.Equal(1, exit);
            Assert.Contains("whole number", err);
            Assert.DoesNotContain("eight bits", err);
        }
        finally { File.Delete(file); }
    }

    // ★★ The oracle. `cufet script.cufe` and `./script` must agree on BOTH halves — what was
    // printed and what was left with. The status is the half no output comparison would catch.
    [Fact]
    public void Exit_AgreesBetweenInterpretedAndCompiled()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;

        string file = WriteProgram(
            "Define object conn with (the text name).\n" +
            "Bind unmaking a conn to disconnect: State \"closing {one's name}\". Done.\n" +
            "Pull a rabbit.\n" +
            "    Define held as a new conn { the name \"held\" }.\n" +
            "    State \"working\".\n" +
            "    Exit with 7.\n" +
            "    State \"never reached\".\n" +
            "Done.\n");
        // ⚠ null, NOT "". Path.ChangeExtension(path, "") leaves a TRAILING DOT — `/tmp/x.cufe`
        // becomes `/tmp/x.` — which is a real filename on Windows-by-luck and nothing at all on
        // Linux. Both of these passed here and failed under WSL for exactly that reason.
        string exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.ChangeExtension(file, ".exe")
            : Path.ChangeExtension(file, null)!;
        try
        {
            var (iExit, iOut, _) = Run(file);

            var (buildExit, _, buildErr) = Run("build", file);
            Assert.True(buildExit == 0, "build failed: " + buildErr);

            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, WorkingDirectory = RepoRoot };
            using var p = Process.Start(psi)!;
            string cOut = p.StandardOutput.ReadToEnd();
            p.WaitForExit(60_000);

            // ★ Leaving UNWINDS: the rabbit's destructor still fires, on both backends. A chosen
            // exit that skipped it would be the one way out of a program that loses a destructor.
            Assert.Equal("working\nclosing held\n", iOut.ReplaceLineEndings("\n"));
            Assert.Equal(iOut.ReplaceLineEndings("\n"), cOut.ReplaceLineEndings("\n"));
            Assert.Equal(7, iExit);
            Assert.Equal(iExit, p.ExitCode);
        }
        finally
        {
            File.Delete(file);
            if (File.Exists(exe)) File.Delete(exe);
        }
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

    // ── A file with nothing to run ────────────────────────────────────────
    //
    // ★★ Cufet has no `main`, so the top-level statements ARE the program. A file that only
    // declares would start and finish having done nothing, and `cufet build` used to answer that
    // with a do-nothing binary — measured on `tools/terminal.cufe`, a book, which built happily.

    /// <summary>A declaration and nothing else: a top-level function, no statements.</summary>
    private const string NothingToRun =
        "Bind number to doubled, given (the number n):\n"
      + "    Return n * 2.\n"
      + "Done.\n";

    [Fact]
    public void Build_AFileWithNothingToRun_IsRefused()
    {
        string file = WriteProgram(NothingToRun);
        try
        {
            var (exit, _, err) = Run("build", file);
            Assert.Equal(2, exit);
            Assert.Contains("there is nothing to run", err);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Running_AFileWithNothingToRun_IsRefused()
    {
        // ⚠ The same answer from both verbs. Running one printed nothing and exited 0, which is
        // the same silence a program that worked would give.
        string file = WriteProgram(NothingToRun);
        try
        {
            var (exit, _, err) = Run(file);
            Assert.Equal(2, exit);
            Assert.Contains("there is nothing to run", err);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Check_AFileWithNothingToRun_IsAccepted()
    {
        // ★★ The half that makes the rule right. Checking a library is exactly what a library
        // author wants, and a book is compiled as part of whatever pulls it — so the mistake is
        // only ever in asking THIS file to be a program, never in the file. Refuse it here and
        // `cufet check` would stop working on every book in the repo.
        string file = WriteProgram(NothingToRun);
        try { Assert.Equal(0, Run("check", file).Exit); }
        finally { File.Delete(file); }
    }

    // ── What it still accepts ─────────────────────────────────────────────

    /// <remarks>
    /// ⚠ `emit-c` was missing from this list, and that is exactly how its two-argument form
    /// came to CRASH: the output name is optional, so `args[3..]` had nothing to slice. Every verb
    /// the docs spell belongs here, in every spelling the docs give it — an accepted form nobody
    /// runs is an unrun form.
    /// </remarks>
    [Fact]
    public void EveryDocumentedFormStillWorks()
    {
        string file    = WriteProgram("State 1.\n");
        string dir     = Path.GetDirectoryName(file)!;
        string emitted = Path.ChangeExtension(file, ".c");
        string named   = Path.Combine(dir, "cufet-cli-named.c");
        try
        {
            Assert.Equal(0, Run(file).Exit);                                        // run
            Assert.Equal(0, Run("check", file).Exit);                               // check
            Assert.Equal(0, Run("check", "--json", "--native", "--strict", file).Exit);
            Assert.Equal(0, Run("tokens", "--json", file).Exit);
            Assert.Equal(0, Run("emit-c", file).Exit);                              // emit-c
            Assert.Equal(0, Run("emit-c", file, named).Exit);                       // emit-c <out.c>
        }
        finally
        {
            File.Delete(file);
            foreach (var leftover in new[] { emitted, named,
                                             Path.Combine(dir, "cufet-runtime.c"),
                                             Path.Combine(dir, "cufet-runtime.h") })
                if (File.Exists(leftover)) File.Delete(leftover);
        }
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
