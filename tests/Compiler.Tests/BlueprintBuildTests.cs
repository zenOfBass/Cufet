using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `cufet build` skips a step nothing has changed, and rebuilds one that something has.
/// </summary>
/// <remarks>
/// <para>
/// ★★ THE SKIP AND THE THREE REBUILDS ARE ONE TEST, and neither half means anything alone. A build
/// that skips everything passes the skip; a build that runs everything passes all three rebuilds.
/// Only together do they say the answer was decided rather than guessed — which is why this is one
/// sequential test and not four, quite apart from the state genuinely being sequential.
/// </para>
/// <para>
/// ⚠⚠ The rebuilds are the half that had to be written. <b>A build that rebuilds too often looks
/// exactly like one that works</b> — the first version of `bp-recalled` compared a RECONSTRUCTED
/// signature, never matched, rebuilt every step every time, and produced correct output the whole
/// while. Nothing but the silence of the second build catches that.
/// </para>
/// <para>
/// ⚠ The step runs THIS build's `cufet` by absolute path. A bare `cufet` would find whatever is
/// installed on the machine and silently test a different language version.
/// </para>
/// </remarks>
public class BlueprintBuildTests
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

    private static (int Exit, string Out) Build(string workingDirectory)
    {
        var psi = new ProcessStartInfo(CufetExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = workingDirectory,
        };
        psi.ArgumentList.Add("build");

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, stdout + stderr);
    }

    /// <summary>A step that copies one file to another, so "did it run" is answerable.</summary>
    private const string CopyProgram = """
        Try to:
            Define stuff as read all from the file "input.txt".
            Write stuff to the file "output.txt".
        Done.
        In case of failure:
            State "copy: {the message of the failure}".
            Exit with 1.
        Done.
        """;

    /// <remarks>
    /// ⚠ Forward slashes. A Windows path in a Cufet literal would need its backslashes escaped, and
    /// Windows accepts `/` — REFERENCE says so in the escaping section, for this exact reason.
    /// </remarks>
    private static string Blueprint(string extraArgument) => $$"""
        Pull a book on blueprints.
            Bind series of records like (
                the text name,
                the series of text needs,
                the series of text makes,
                the series of text runs) to blueprint:
                Define work as a series of records like (
                    the text name,
                    the series of text needs,
                    the series of text makes,
                    the series of text runs).

                Insert a record with (
                    the name "copy",
                    the needs a series of text with ("input.txt", "copy.cufe"),
                    the makes a series of text with ("output.txt"),
                    the runs a series of text with (
                        "{{CufetExe.Replace('\\', '/')}}", "copy.cufe"{{extraArgument}})) into work.

                Return work.
            Done.
        Done.
        """;

    [Fact]
    public void AStepIsSkippedWhenNothingChanged_AndRebuiltWhenAnythingDid()
    {
        // ⚠ Its own directory, not a shared one. A test in this area passed for the wrong reason
        // once already, by finding an artifact left in a parent directory.
        var project = Path.Combine(TestScratch.Root, "bp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        try
        {
            File.WriteAllText(Path.Combine(project, "input.txt"), "one");
            File.WriteAllText(Path.Combine(project, "copy.cufe"), CopyProgram);
            File.WriteAllText(Path.Combine(project, "blueprint.cufe"), Blueprint(""));

            // Cold: there is no record, so it runs.
            var first = Build(project);
            Assert.Equal(0, first.Exit);
            Assert.Contains("cufet build: copy", first.Out);
            Assert.Equal("one", File.ReadAllText(Path.Combine(project, "output.txt")));

            // ★ THE SKIP. Nothing said, because nothing was done — the emptiness is the report.
            var second = Build(project);
            Assert.Equal(0, second.Exit);
            Assert.Equal("", second.Out.Trim());

            // (a) An input changed.
            File.WriteAllText(Path.Combine(project, "input.txt"), "two");
            Assert.Contains("cufet build: copy", Build(project).Out);
            Assert.Equal("two", File.ReadAllText(Path.Combine(project, "output.txt")));
            Assert.Equal("", Build(project).Out.Trim());

            // (b) An output was deleted. ★ This is the half the signature cannot answer: the inputs
            // are untouched, so only asking after the outputs separately catches it.
            File.Delete(Path.Combine(project, "output.txt"));
            Assert.Contains("cufet build: copy", Build(project).Out);
            Assert.True(File.Exists(Path.Combine(project, "output.txt")));
            Assert.Equal("", Build(project).Out.Trim());

            // (c) The argv changed, and NOTHING ELSE DID. ★★ The case a signature over inputs alone
            // gets wrong: same files, same contents, different command, and it must still rebuild.
            File.WriteAllText(Path.Combine(project, "blueprint.cufe"), Blueprint(", \"--\""));
            Assert.Contains("cufet build: copy", Build(project).Out);
            Assert.Equal("", Build(project).Out.Trim());
        }
        finally
        {
            try { Directory.Delete(project, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A step that copies its first argument to its second, with a mark appended.</summary>
    /// <remarks>
    /// ⚠ Not `from` / `to` — `from` is a reserved word, and finding that out cost a build. Check the
    /// reserved list before naming anything.
    /// </remarks>
    private const string StageProgram = """
        Define source-path as item 1 of the arguments.
        Define target-path as item 2 of the arguments.
        Try to:
            Define stuff as read all from the file source-path.
            Write "{stuff}!" to the file target-path.
        Done.
        In case of failure:
            State "stage: {the message of the failure}".
            Exit with 1.
        Done.
        """;

    /// <remarks>
    /// ★ `second` is declared FIRST, so the order it runs in has to be inferred from needs-vs-makes
    /// rather than read off the file.
    /// </remarks>
    private static string ChainedBlueprint() => $$"""
        Pull a book on blueprints.
            Bind series of records like (
                the text name,
                the series of text needs,
                the series of text makes,
                the series of text runs) to blueprint:
                Define work as a series of records like (
                    the text name,
                    the series of text needs,
                    the series of text makes,
                    the series of text runs).

                Insert a record with (
                    the name "second",
                    the needs a series of text with ("middle.txt", "stage.cufe"),
                    the makes a series of text with ("final.txt"),
                    the runs a series of text with (
                        "{{CufetExe.Replace('\\', '/')}}", "stage.cufe", "middle.txt", "final.txt")) into work.

                Insert a record with (
                    the name "first",
                    the needs a series of text with ("source.txt", "stage.cufe"),
                    the makes a series of text with ("middle.txt"),
                    the runs a series of text with (
                        "{{CufetExe.Replace('\\', '/')}}", "stage.cufe", "source.txt", "middle.txt")) into work.

                Return work.
            Done.
        Done.
        """;

    /// <summary>
    /// Staleness travels along the chain — and stops travelling when the content stops changing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ★★ THE CASE A BUILD SYSTEM EXISTS FOR, and the single-step test above cannot see any of it.
    /// `second` consumes what `first` makes, and nothing anywhere declares that: the order and the
    /// propagation both fall out of needs-vs-makes.
    /// </para>
    /// <para>
    /// ★★ The last assertion is the interesting one — <b>early cutoff</b>. Deleting `middle.txt`
    /// makes `first` run again, and `second` then does NOT, because `first` regenerated byte-identical
    /// content and `second`'s signature never moved. A build keyed on "did my input step run" would
    /// rebuild the world here; one keyed on CONTENT stops at the first thing that really changed.
    /// That is a consequence of hashing rather than something aimed at, which is why it is pinned —
    /// it could be lost by a change that looks entirely reasonable.
    /// </para>
    /// <para>
    /// ⚠ The touch case is the whole timestamps-versus-content argument in one assertion: a build
    /// that consulted mtimes rebuilds there, and this one must not.
    /// </para>
    /// </remarks>
    [Fact]
    public void StalenessTravelsTheChain_AndStopsWhereContentStops()
    {
        var project = Path.Combine(TestScratch.Root, "bp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        try
        {
            string At(string name) => Path.Combine(project, name);
            File.WriteAllText(At("source.txt"), "alpha");
            File.WriteAllText(At("stage.cufe"), StageProgram);
            File.WriteAllText(At("blueprint.cufe"), ChainedBlueprint());

            // Cold. ★ Declared second-then-first, run first-then-second.
            var cold = Build(project);
            Assert.Equal(0, cold.Exit);
            Assert.True(cold.Out.IndexOf("build: first", StringComparison.Ordinal)
                      < cold.Out.IndexOf("build: second", StringComparison.Ordinal),
                "'first' makes what 'second' needs, so it must run before it");
            Assert.Equal("alpha!!", File.ReadAllText(At("final.txt")));
            Assert.Equal("", Build(project).Out.Trim());

            // The change at the head of the chain reaches the end of it.
            File.WriteAllText(At("source.txt"), "delta");
            var changed = Build(project);
            Assert.Contains("build: first", changed.Out);
            Assert.Contains("build: second", changed.Out);
            Assert.Equal("delta!!", File.ReadAllText(At("final.txt")));
            Assert.Equal("", Build(project).Out.Trim());

            // Touched, not changed. Content, never timestamps.
            File.SetLastWriteTimeUtc(At("source.txt"), DateTime.UtcNow.AddMinutes(1));
            Assert.Equal("", Build(project).Out.Trim());

            // ★★ EARLY CUTOFF. `first` runs; `second` does not, because what it needs came back
            // identical.
            File.Delete(At("middle.txt"));
            var cutoff = Build(project);
            Assert.Contains("build: first", cutoff.Out);
            Assert.DoesNotContain("build: second", cutoff.Out);
            Assert.Equal("", Build(project).Out.Trim());
        }
        finally
        {
            try { Directory.Delete(project, recursive: true); } catch (IOException) { }
        }
    }

    /// <remarks>
    /// ⚠ The record is kept beside the blueprint, and deleting it is the whole of "build everything
    /// again" — there is no flag, deliberately. If that ever stops being true, this is the test that
    /// says so.
    /// </remarks>
    [Fact]
    public void DeletingTheRecord_MakesTheBuildRunAgain()
    {
        var project = Path.Combine(TestScratch.Root, "bp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        try
        {
            File.WriteAllText(Path.Combine(project, "input.txt"), "one");
            File.WriteAllText(Path.Combine(project, "copy.cufe"), CopyProgram);
            File.WriteAllText(Path.Combine(project, "blueprint.cufe"), Blueprint(""));

            Assert.Contains("cufet build: copy", Build(project).Out);
            Assert.Equal("", Build(project).Out.Trim());

            var record = Path.Combine(project, ".cufet-build");
            Assert.True(File.Exists(record), ".cufet-build should sit beside the blueprint");
            File.Delete(record);

            Assert.Contains("cufet build: copy", Build(project).Out);
        }
        finally
        {
            try { Directory.Delete(project, recursive: true); } catch (IOException) { }
        }
    }

    // ── A need that is not there ──────────────────────────────────────────
    //
    // ⚠ A step whose need was missing used to RUN anyway, and fail in its own command's words —
    // which could name neither the step nor the path. BOOKS.md says a need "must exist first".

    [Fact]
    public void ANeedNoStepMakes_IsRefusedBeforeTheStepRuns_EveryTime()
    {
        var project = Path.Combine(TestScratch.Root, "bp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        try
        {
            // No input.txt, and nothing in the plan makes it.
            File.WriteAllText(Path.Combine(project, "copy.cufe"), CopyProgram);
            File.WriteAllText(Path.Combine(project, "blueprint.cufe"), Blueprint(""));

            var first = Build(project);
            Assert.Equal(1, first.Exit);
            Assert.Contains("'copy' needs input.txt, and there is no such file. No step makes it", first.Out);
            Assert.DoesNotContain("cufet build: copy\n", first.Out.Replace("\r\n", "\n"));   // it never ran
            Assert.False(File.Exists(Path.Combine(project, "output.txt")));

            // And again: a refused step records nothing, so the next build asks the same question.
            var second = Build(project);
            Assert.Equal(1, second.Exit);
            Assert.Contains("'copy' needs input.txt", second.Out);
        }
        finally
        {
            try { Directory.Delete(project, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ANeedItsMakerDidNotMake_NamesTheMaker()
    {
        var project = Path.Combine(TestScratch.Root, "bp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        try
        {
            string cufet = CufetExe.Replace('\\', '/');
            File.WriteAllText(Path.Combine(project, "quiet.cufe"), "State \"made nothing\".\n");
            File.WriteAllText(Path.Combine(project, "blueprint.cufe"), $$"""
                Pull a book on blueprints.
                    Bind series of step to blueprint:
                        Return a series of step with (
                            a record with (
                                the name "use",
                                the needs a series of text with ("middle.txt"),
                                the makes a series of text with (),
                                the runs a series of text with ("{{cufet}}", "quiet.cufe")),
                            a record with (
                                the name "promise",
                                the needs a series of text with ("quiet.cufe"),
                                the makes a series of text with ("middle.txt"),
                                the runs a series of text with ("{{cufet}}", "quiet.cufe"))).
                    Done.
                Done.
                """);

            var result = Build(project);
            Assert.Equal(1, result.Exit);
            Assert.Contains("cufet build: promise", result.Out);   // the maker ran first
            Assert.Contains("'use' needs middle.txt, which 'promise' should have made", result.Out);
        }
        finally
        {
            try { Directory.Delete(project, recursive: true); } catch (IOException) { }
        }
    }
}
