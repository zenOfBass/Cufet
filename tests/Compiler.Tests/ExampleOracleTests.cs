using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

/// <summary>
/// Every program in <c>examples/</c>, run on BOTH backends, output compared.
///
/// ★ These were the most productive bug-finders the project has — ordinary programs, written to do
/// a thing, that turned up a compiler crash, a live divergence and a type the compiler could not
/// name. Until now none of them was run by <c>dotnet test</c>: they were checked by hand, once,
/// and then trusted forever. This makes each one a permanent regression test on both backends, so
/// writing the next example is also writing the next test.
///
/// Examples run with the working directory set to the REPOSITORY ROOT, because that is where a
/// reader runs them from — <c>wordfreq.cufe</c> opens <c>examples/assets/sample.txt</c> by a
/// root-relative path, and testing it from anywhere else would be testing a different program.
/// </summary>
public class ExampleOracleTests
{
    // Walk up for the solution file rather than copying the corpus into the output directory: the
    // examples must be run in place, next to the assets they name.
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cufet.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? "";
    }

    private static string ExampleDir => Path.Combine(RepoRoot, "examples");

    // Pinned outputs live in their own directory rather than beside the programs. `examples/` is
    // read by people looking for programs, and interleaving a fixture with every example halves
    // the signal in that listing — `assets/` already established that support material for the
    // examples belongs in a subdirectory of them.
    private static string ExpectedDir => Path.Combine(ExampleDir, "expected");

    // Only for the `.expected` comparison, never for backend-vs-backend. A pinned file is checked
    // in and travels through git's line-ending conversion, so holding it to the byte would fail on
    // whoever's autocrlf differs. The oracle assertion has no such excuse and compares verbatim.
    private static string Norm(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

    /// <summary>
    /// Examples the COMPILER cannot build on this platform, with the reason. Concurrency and
    /// subprocess programs need pthreads, sigaction and fork — POSIX, guarded in the emitted C, and
    /// unavailable under mingw. They are not skipped silently: SkippedExamples_StillTypeCheck holds
    /// them to the front end, so a skip hides the backend and nothing else.
    /// </summary>
    private static readonly Dictionary<string, string> WindowsOnlySkips = new()
    {
        ["channel-deepcopy.cufe"] = "channels — pthreads",
        ["parallelsum.cufe"]      = "tasks + channels — pthreads",
        ["shell.cufe"]            = "subprocess — fork/exec",
        ["subprocess-pipes.cufe"] = "subprocess pipes — fork/exec",
        ["work-queue.cufe"]       = "tasks + channels — pthreads",
    };

    /// <summary>
    /// Examples whose output is legitimately not reproducible, with the reason. Seeding makes each
    /// backend self-consistent but NOT equal to the other — the interpreter's RNG is .NET's
    /// xoshiro256** and the compiler emits its own xorshift64*, a documented fork rather than a
    /// divergence. Equality is the wrong bar for these, so they get the weaker one that still
    /// means something: both backends must build and run cleanly.
    /// </summary>
    private static readonly Dictionary<string, string> NonDeterministicSkips = new()
    {
        ["markov.cufe"] = "chance — the backends' generators differ, so the babble does too",
        // Both backends are CORRECT here and disagree anyway: the sum is 930 either way and every
        // item is processed exactly once, but the per-worker split is decided by the scheduler.
        // The interpreter's is cooperative and drains one worker first (30/0/0); the compiler's is
        // real pthreads and shares the work out (measured on Linux: 9/17/4). Thread scheduling is
        // the platform's to decide, which is the oracle rule's narrow exception rather than a
        // divergence — so this drops to the weaker bar: both backends must build and run cleanly.
        //
        // Found before it could fail CI, by compiling the example under WSL gcc and comparing:
        // it is Windows-skipped, so nothing had ever built it here, and the new Linux job would
        // have been the first thing to run it.
        ["work-queue.cufe"] = "task scheduling — the split across workers differs between a "
                            + "cooperative scheduler and real threads, though the totals do not",
    };

    /// ★ Skip lists and pins are keyed on the BASE NAME, never on the path. Which folder an example
    /// sits in is a presentation choice for whoever is reading `examples/`; keying on it would mean
    /// every reshuffle edited this file, and a stale key fails as "missing example" rather than as
    /// "moved". Basename uniqueness is what makes that safe — ExampleCorpus_IsPresent asserts it.
    private static bool IsSkipped(string file)
    {
        var name = Path.GetFileName(file);
        return (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && WindowsOnlySkips.ContainsKey(name))
            || NonDeterministicSkips.ContainsKey(name);
    }

    /// Paths relative to ExampleDir, forward-slashed, so a test ID reads `algorithms/dijkstra.cufe`
    /// and looks the same on both platforms.
    ///
    /// ★ RECURSIVE. Examples live in category folders, and the non-recursive scan this replaced
    /// would have dropped an entire folder from the corpus without failing anything — the theories
    /// below simply would not have been handed those files. `expected/` and `assets/` hold no
    /// `.cufe`, so nothing needs excluding; if that ever changes, exclude them here.
    private static IEnumerable<string> ExampleFiles() =>
        Directory.Exists(ExampleDir)
            ? Directory.GetFiles(ExampleDir, "*.cufe", SearchOption.AllDirectories)
                       .Select(p => Path.GetRelativePath(ExampleDir, p).Replace('\\', '/'))
                       .OrderBy(p => p, StringComparer.Ordinal)
            : [];

    private static IEnumerable<object[]> AllExamples() =>
        ExampleFiles().Select(p => new object[] { p });

    public static IEnumerable<object[]> OracleExamples() =>
        AllExamples().Where(a => !IsSkipped((string)a[0]));

    public static IEnumerable<object[]> SkippedExamples() =>
        AllExamples().Where(a => IsSkipped((string)a[0]));

    // The sentinel row exists because xUnit FAILS a [Theory] whose MemberData is empty, and this
    // list is empty until the first chance-using example arrives. Without it the suite would go red
    // for having nothing to do.
    private const string NoSuchExample = "";

    public static IEnumerable<object[]> NonDeterministicExamples()
    {
        var found = AllExamples()
            .Where(a => NonDeterministicSkips.ContainsKey(Path.GetFileName((string)a[0]))
                     && !(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                          && WindowsOnlySkips.ContainsKey(Path.GetFileName((string)a[0]))))
            .ToList();
        return found.Count > 0 ? found : [[NoSuchExample]];
    }

    /// <summary>
    /// The failure mode of any directory-driven suite: stop finding the corpus and every [Theory]
    /// below becomes a silent no-op while the run stays green. A count that only ever grows is the
    /// cheapest guard — it fails if the enumeration breaks, and never nags when an example is added.
    /// </summary>
    [Fact]
    public void ExampleCorpus_IsPresent()
    {
        Assert.True(Directory.Exists(ExampleDir), $"examples/ not found from {AppContext.BaseDirectory}");

        // ★ The floor has to track the corpus. It sat at 20 while 29 examples existed, which meant
        // nine could stop being enumerated and this still passed — and the enumeration was
        // non-recursive, so moving examples into folders was exactly how that would have happened.
        // A floor far below the count is not a guard. Raise it when you add examples; it only ever
        // fails for a deletion or a broken scan, never for an addition.
        var files = ExampleFiles().ToList();
        Assert.True(files.Count >= 29,
            $"only {files.Count} examples found — the corpus has shrunk or the enumeration broke.");

        // ★ Every category folder must contribute. Found by listing directories rather than by
        // filtering the enumeration above, so this fails if the scan stops descending into one —
        // which asserting against the same enumeration could never catch.
        var folders = Directory.GetDirectories(ExampleDir)
            .Select(d => Path.GetFileName(d)!)
            .Where(d => Directory.GetFiles(Path.Combine(ExampleDir, d), "*.cufe", SearchOption.AllDirectories).Length > 0)
            .ToList();
        var missing = folders.Where(d => !files.Any(f => f.StartsWith(d + "/", StringComparison.Ordinal))).ToList();
        Assert.True(missing.Count == 0,
            $"these example folders hold .cufe files that the enumeration did not return: {string.Join(", ", missing)}");

        // ★ Basenames are the key for skips and pins, so two examples sharing one in different
        // folders would silently make a skip or a pinned output apply to whichever was found first.
        var duplicates = files.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ({string.Join(" + ", g)})")
            .ToList();
        Assert.True(duplicates.Count == 0,
            $"example file names must be unique across folders — skips and .expected key on them: {string.Join("; ", duplicates)}");

        // A skip entry for a file that no longer exists is a stale excuse; say so. Matched on
        // basename, so moving an example between folders never makes its skip stale.
        var present = files.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        var stale = WindowsOnlySkips.Keys.Concat(NonDeterministicSkips.Keys)
            .Where(f => !present.Contains(f))
            .ToList();
        Assert.True(stale.Count == 0, $"skip list names missing example(s): {string.Join(", ", stale)}");

        // ★ The pins need the same guard, and they need it MORE since they moved into their own
        // directory. An `.expected` that goes missing takes its assertion with it silently: the
        // comparison is opt-in, so no file simply means no check, and the run stays green. When
        // they sat beside the programs a deletion was at least visible in the listing.
        Assert.True(Directory.Exists(ExpectedDir),
            $"examples/expected/ not found from {AppContext.BaseDirectory} — every pinned output " +
            "has stopped being compared, and nothing else would have said so.");
        var pins = Directory.GetFiles(ExpectedDir, "*.expected").Length;
        Assert.True(pins >= 7, $"only {pins} pinned outputs found — one has been deleted, or the path broke.");
    }

    /// <summary>
    /// ★ The bar: the compiled binary's output must equal the interpreter's, exactly.
    /// </summary>
    [Theory]
    [MemberData(nameof(OracleExamples))]
    public void Example_CompilesAndAgreesWithTheInterpreter(string file)
    {
        var path   = Path.Combine(ExampleDir, file);
        var source = File.ReadAllText(path);

        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        program = new TypeChecker().Check(program);

        string compiled;
        try
        {
            compiled = CompileAndRun(program);
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            // The one case that needs a human decision, so the failure has to name it. A new
            // concurrency or subprocess example cannot be built under mingw, and a raw gcc error
            // says nothing about what to do next.
            throw new Xunit.Sdk.XunitException(
                $"{file} failed to build.\n" +
                $"If it uses tasks, channels or subprocesses, it needs POSIX and cannot compile on " +
                $"Windows — add it to WindowsOnlySkips with a reason, and SkippedExample_StillTypeChecks " +
                $"will keep holding it to the front end.\n" +
                $"Otherwise this is a real compiler regression.\n\n{e.Message}");
        }

        // ★ Byte for byte, deliberately. Both runners return output VERBATIM — see the note on
        // Interpret — because the two backends are meant to agree on every byte, and a comparison
        // that normalises line endings first cannot see a backend that rewrites them.
        var interpreted = Interpret(program);
        Assert.Equal(interpreted, compiled);

        // ★ Agreement is not correctness. The comparison above proves the two backends say the same
        // thing; it cannot tell whether that thing is right. config.cufe carries a deliberately
        // malformed line so its error path runs — and if that line ever stopped producing a warning,
        // both backends would agree on the new output and this test would still pass. A canary
        // nothing checks cannot fail.
        //
        // So an example may have an `examples/expected/<name>.expected` file, and where one exists
        // the output must match it exactly. Opt-in: no file, no assertion — create it empty and
        // regenerate to start pinning one.
        var expectedPath = Path.Combine(ExpectedDir, Path.GetFileNameWithoutExtension(file) + ".expected");
        if (!File.Exists(expectedPath)) return;

        if (Environment.GetEnvironmentVariable("CUFET_EXAMPLE_EXPECTED") == "1")
        {
            File.WriteAllText(expectedPath, Norm(interpreted) + "\n");
            return;
        }

        var expected = Norm(File.ReadAllText(expectedPath));
        Assert.True(expected == Norm(interpreted),
            $"{file} no longer produces its recorded output.\n" +
            "Both backends agree, so this is not a divergence — the program's behaviour changed.\n" +
            "If the new output is correct:\n" +
            "  CUFET_EXAMPLE_EXPECTED=1 dotnet test --filter ExampleOracleTests\n\n" +
            $"--- expected ---\n{expected}\n--- actual ---\n{Norm(interpreted)}");
    }

    /// <summary>
    /// A `.expected` for an example that never runs here is a file nobody checks. Skipped
    /// examples cannot be built on this platform and non-deterministic ones have no fixed output,
    /// so either is a mistake worth naming rather than a quietly dead assertion.
    ///
    /// This matters more now that the pins sit in their own directory: a renamed or deleted
    /// example no longer leaves its orphan sitting visibly next to the gap.
    /// </summary>
    [Fact]
    public void ExpectedOutputFiles_BelongToExamplesThatAreActuallyCompared()
    {
        if (!Directory.Exists(ExpectedDir)) return;

        // Matched on basename against the whole recursive corpus — a pin belongs to its program
        // wherever that program has been filed.
        var present = ExampleFiles().Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);

        var stranded = Directory.GetFiles(ExpectedDir, "*.expected")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !present.Contains(name + ".cufe")
                        || NonDeterministicSkips.ContainsKey(name + ".cufe"))
            .OrderBy(n => n)
            .ToList();

        Assert.True(stranded.Count == 0,
            "these .expected files are never compared — the example is missing or its output is " +
            $"not reproducible: {string.Join(", ", stranded)}");
    }

    /// <summary>
    /// The other half of a skip. A program the compiler cannot build on this platform must still
    /// pass the shared front end, so a platform gap never becomes a blind spot in the language.
    /// </summary>
    [Theory]
    [MemberData(nameof(SkippedExamples))]
    public void SkippedExample_StillTypeChecks(string file)
    {
        var source = File.ReadAllText(Path.Combine(ExampleDir, file));
        var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        new TypeChecker().Check(program);   // throws on failure — that IS the assertion
    }

    /// <summary>
    /// The weaker bar for a program whose output cannot be reproduced: it must still BUILD and RUN
    /// on both backends and produce something. That is not equality, but it is most of what goes
    /// wrong — a refusal, a crash, an empty run — and it is the honest maximum for a program whose
    /// whole point is to be different every time.
    ///
    /// For stronger coverage, write the program to check its own invariants and print deterministic
    /// PASS lines, which is what the compiler's own chance tests do. Then it belongs in the oracle
    /// theory above instead of here.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonDeterministicExamples))]
    public void NonDeterministicExample_RunsOnBothBackends(string file)
    {
        if (file == NoSuchExample) return;   // nothing registered yet — see NonDeterministicExamples

        var source  = File.ReadAllText(Path.Combine(ExampleDir, file));
        var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
        program = new TypeChecker().Check(program);

        Assert.NotEmpty(Interpret(program));
        Assert.NotEmpty(CompileAndRun(program));
    }

    // ── Running ───────────────────────────────────────────────────────────

    // The interpreter reads files through the PROCESS working directory, so it has to be moved to
    // the repo root and put back. Serialised on a lock because xUnit runs classes in parallel and
    // the working directory is global: two tests changing it at once would each see the other's.
    private static readonly object CurrentDirectoryLock = new();

    // ★ On a 16 MB stack, mirroring the CLI's RunOnLargeStack. A recursive Cufet program — a
    // backtracking sudoku solver, say — overflows xUnit's default 1 MB thread, and a stack overflow
    // cannot be caught: it takes the whole test host down with "Test Run Aborted", so the run
    // reports the tests that finished as passing and exits non-zero with no failure to point at.
    // The example ran fine under `cufet`, which has always had the big stack; only the harness did
    // not.
    //
    // ★ Returns output VERBATIM. Normalising line endings here would defeat the comparison this
    // suite exists for — see the note at the assertion.
    private static string Interpret(Program program)
    {
        var sb = new StringWriter();
        lock (CurrentDirectoryLock)
        {
            var saved = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(RepoRoot);
            try
            {
                Exception? caught = null;
                var thread = new Thread(
                    () => { try { new CufetInterpreter(sb).Execute(program); } catch (Exception e) { caught = e; } },
                    16 * 1024 * 1024);
                thread.Start();
                thread.Join();
                if (caught is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(caught).Throw();
            }
            finally { Directory.SetCurrentDirectory(saved); }
        }
        return sb.ToString();
    }

    // The binary gets its working directory set directly, so no global state is touched.
    /// <summary>
    /// Builds and runs one example the way `cufet build` does — and, where the platform allows it,
    /// under the sanitizers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ★ The examples are the only realistic programs the suite has. The hand-written sanitized
    /// tests are small and aimed at features somebody already suspected; these are whole programs
    /// doing bit-packing, matrix work and string slicing in combinations nobody wrote a targeted
    /// test for. Sanitizing them costs NO extra compiles — this method already built and ran every
    /// one — it only makes the builds it was doing anyway instrumented.
    /// </para>
    /// <para>
    /// It matters more now that builds are optimized: `-O2` is what turns latent undefined
    /// behaviour into a wrong answer, where `-O0` forgave it. `-fno-sanitize-recover` makes a UBSan
    /// finding ABORT rather than print to stderr and continue, because a harness that compares
    /// stdout would otherwise let the report scroll past and go green.
    /// </para>
    /// <para>
    /// ⚠ Linux only — mingw has no working sanitizers, which is the same reason 73 tests already
    /// skip on Windows.
    /// </para>
    /// <para>
    /// Leak detection is ON. It was going to be off, on the assumption that an arena which defers
    /// frees and lets the OS reclaim at exit would make LeakSanitizer report the design rather than
    /// a bug — but measured against all 30 examples it reports nothing, so switching it off would
    /// have bought nothing and cost the coverage. Note what it does and does not prove: LSan does
    /// not count still-REACHABLE memory as leaked, and the arena's pointer list is a global, so
    /// anything parked there is reachable by construction. What it therefore catches is memory that
    /// became unreachable without being freed — a channel or buffer whose last pointer was dropped.
    /// </para>
    /// </remarks>
    private static string CompileAndRun(Program program)
    {
        // The SPLIT path, because that is what `cufet build` does — see PipelineTestBase.CompileRaw.
        var (header, runtimeSource, programSource) = new CodeGenerator().GenerateSplit(program);

        // A unique stem WITHOUT creating a file: GetTempFileName is unique only while its file exists,
        // and deleting it to reuse the stem releases the name for another thread to be handed.

        var tmp     = Path.Combine(Path.GetTempPath(), "cufet-" + Guid.NewGuid().ToString("N"));
        var work    = Directory.CreateDirectory(tmp);
        var cPath   = Path.Combine(tmp, "program.c");
        var rtPath  = Path.Combine(tmp, RuntimeSplit.SourceFileName);
        // ⚠ `-bin`, not just `tmp` plus an extension — `tmp` is a directory now, and on Linux the
        // extension is empty, so the two collided and ld reported "cannot open output file: Is a
        // directory". See PipelineTestBase.CompileRaw for the same trap.
        var binPath = tmp + "-bin" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "");

        var sanitized = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string[] flags = sanitized
            ? ["-fsanitize=address,undefined", "-fno-sanitize-recover=undefined", "-g"]
            : [];

        try
        {
            File.WriteAllText(Path.Combine(tmp, RuntimeSplit.HeaderFileName), header);
            File.WriteAllText(cPath, programSource);

            var gcc = new GccInvoker();
            // The cache key carries the flags, so a sanitized runtime object never collides with
            // the ordinary one.
            var cached = new RuntimeCache().ObjectFor(runtimeSource, header, gcc, flags);
            if (cached == null) File.WriteAllText(rtPath, runtimeSource);
            gcc.Compile([cPath, cached ?? rtPath], binPath, flags);
        }
        finally { try { work.Delete(recursive: true); } catch { } }

        try
        {
            var psi = new ProcessStartInfo(binPath)
            {
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = RepoRoot,
                // Redirect stdin and close it immediately: an example that reads input must get
                // EOF, not the TEST HOST's stdin. See PipelineTestBase.CompileRaw for the full
                // story — inheriting it wedges the run for hours under `dotnet test` on Linux.
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (sanitized)
            {
                psi.Environment["ASAN_OPTIONS"] = "detect_leaks=1";
                psi.Environment["UBSAN_OPTIONS"] = "print_stacktrace=1";
            }

            using var proc = Process.Start(psi)!;
            proc.StandardInput.Close();
            var output = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (sanitized && (stderr.Contains("runtime error:")
                           || stderr.Contains("AddressSanitizer")
                           || stderr.Contains("LeakSanitizer")))
                throw new Xunit.Sdk.XunitException(
                    "A sanitizer reported undefined or unsafe behaviour in the generated program.\n" +
                    "This is a real defect in the emitted C or the runtime — it is not a flake, and\n" +
                    "the report below names the file and line.\n\n" + stderr);

            return output;
        }
        finally { try { File.Delete(binPath); } catch { } }
    }
}
