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
    };

    private static bool IsSkipped(string file) =>
        (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && WindowsOnlySkips.ContainsKey(file))
        || NonDeterministicSkips.ContainsKey(file);

    private static IEnumerable<object[]> AllExamples() =>
        Directory.Exists(ExampleDir)
            ? Directory.GetFiles(ExampleDir, "*.cufe").OrderBy(p => p)
                       .Select(p => new object[] { Path.GetFileName(p) })
            : [];

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
            .Where(a => NonDeterministicSkips.ContainsKey((string)a[0])
                     && !(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                          && WindowsOnlySkips.ContainsKey((string)a[0])))
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
        Assert.True(AllExamples().Count() >= 20,
            $"only {AllExamples().Count()} examples found — the corpus has shrunk or the enumeration broke.");

        // A skip entry for a file that no longer exists is a stale excuse; say so.
        var stale = WindowsOnlySkips.Keys.Concat(NonDeterministicSkips.Keys)
            .Where(f => !File.Exists(Path.Combine(ExampleDir, f)))
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
        Assert.True(pins >= 6, $"only {pins} pinned outputs found — one has been deleted, or the path broke.");
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
        new TypeChecker().Check(program);

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
            File.WriteAllText(expectedPath, interpreted + "\n");
            return;
        }

        var expected = File.ReadAllText(expectedPath).Replace("\r\n", "\n").TrimEnd('\n');
        Assert.True(expected == interpreted,
            $"{file} no longer produces its recorded output.\n" +
            "Both backends agree, so this is not a divergence — the program's behaviour changed.\n" +
            "If the new output is correct:\n" +
            "  CUFET_EXAMPLE_EXPECTED=1 dotnet test --filter ExampleOracleTests\n\n" +
            $"--- expected ---\n{expected}\n--- actual ---\n{interpreted}");
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

        var stranded = Directory.GetFiles(ExpectedDir, "*.expected")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !File.Exists(Path.Combine(ExampleDir, name + ".cufe"))
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
        new TypeChecker().Check(program);

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
        return sb.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    // The binary gets its working directory set directly, so no global state is touched.
    private static string CompileAndRun(Program program)
    {
        var cSource = new CodeGenerator().Generate(program);

        var tmp = Path.GetTempFileName();
        File.Delete(tmp);
        var cPath   = tmp + ".c";
        var binPath = tmp + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "");

        try
        {
            File.WriteAllText(cPath, cSource);
            new GccInvoker().Compile(cPath, binPath);
        }
        finally { try { File.Delete(cPath); } catch { } }

        try
        {
            var psi = new ProcessStartInfo(binPath)
            {
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = RepoRoot,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output.Replace("\r\n", "\n").TrimEnd('\n');
        }
        finally { try { File.Delete(binPath); } catch { } }
    }
}
