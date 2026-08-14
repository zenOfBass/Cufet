using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;
namespace Cufet.Compiler.Tests;

/// <summary>
/// Shared machinery for the pipeline suites; the tests live in the Pipeline*Tests classes.
///
/// ★ Why these are several classes rather than one. xUnit 2.4.2 runs test COLLECTIONS in
/// parallel and a collection is, by default, a class — so tests inside a single class run
/// strictly sequentially. With all 429 facts in one class the compiler suite took 7 minutes
/// pinned to one core while the other 116 tests in the assembly finished beside it in seconds
/// (measured: PipelineTests alone was 7m01s of a 7m05s assembly). Splitting by theme costs
/// nothing and lets the work spread across cores. No test changed.
///
/// Keep new tests in the class whose theme fits, and add a class rather than growing one past
/// its neighbours — the suite is now only as fast as its largest class.
/// </summary>
public abstract class PipelineTestBase
{
    // ★ Raw vs normalised, and which one the ORACLE uses.
    //
    // The oracle assertion — interpreted output equals compiled output — must compare the bytes
    // exactly, because the two backends are supposed to agree on every byte. It used to compare
    // through Norm, and that hid a whole axis: with "\r\n" collapsed to "\n" on both sides, a
    // compiled backend that turned every '\n' in the DATA into "\r\n" compared equal to an
    // interpreter that left it alone. The bug was invisible for as long as the harness existed
    // and only surfaced when a literal happened to contain a "\r\n" PAIR, which normalisation
    // could not flatten. A test that cannot see a difference is not testing for it.
    //
    // So: CompileRaw/InterpretRaw for backend-vs-backend, and Compile/Interpret — which normalise
    // — only where the expected value is a C# literal written with '\n' and no view on line
    // endings. Norm also drops trailing newlines, which is the second thing it was hiding.
    protected static string Norm(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');
    protected static string Compile(string source, string? stdin = null) => Norm(CompileRaw(source, stdin));
    protected static string Interpret(string source, string? stdin = null) => Norm(InterpretRaw(source, stdin));

    /// <summary>
    /// Same lines, order not required — for programs where the ORDER is the scheduler's to choose.
    ///
    /// ★ Only where nothing in the program orders the writes. A task that prints and is never
    /// awaited races the main thread by construction: the cooperative interpreter always resolves
    /// it one way, real threads do not, and neither is wrong. That is the oracle rule's
    /// platform-owned exception, and asserting an exact string there asserts a coincidence — this
    /// one passed for months and then failed in CI.
    ///
    /// NOT a licence to soften a real divergence. Where a program does order its output — an
    /// await, a channel, a join — the exact comparison is the whole point and stays.
    /// </summary>
    protected static void AssertSameLinesInAnyOrder(string expected, string actual)
    {
        static string[] Lines(string s) =>
            Norm(s).Split('\n').OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(Lines(expected), Lines(actual));
    }

    // Compiles source to a temp native binary, runs it (optionally feeding stdin), returns stdout.
    protected static string CompileRaw(string source, string? stdin = null)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);

        // ★ The SPLIT path, because that is what `cufet build` does. The combined single-file form
        // still exists as the concatenation these two halves are cut from, but nothing ships it any
        // more — testing it would be testing a shape no user receives, and would leave the derived
        // header (which is what makes the split work at all) exercised by nothing.
        var (header, runtimeSource, programSource) = new CodeGenerator().GenerateSplit(program);

        // A unique stem WITHOUT creating a file: GetTempFileName is unique only while its file exists,
        // and deleting it to reuse the stem releases the name for another thread to be handed.

        var tmp    = Path.Combine(Path.GetTempPath(), "cufet-" + Guid.NewGuid().ToString("N"));
        var work   = Directory.CreateDirectory(tmp);
        var cPath  = Path.Combine(tmp, "program.c");
        var rtPath = Path.Combine(tmp, RuntimeSplit.SourceFileName);
        var binExt = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
        var binPath = tmp + binExt;

        try
        {
            File.WriteAllText(Path.Combine(tmp, RuntimeSplit.HeaderFileName), header);
            File.WriteAllText(cPath, programSource);

            var gcc    = new GccInvoker();
            var cached = new RuntimeCache().ObjectFor(runtimeSource, header, gcc, []);
            if (cached == null) File.WriteAllText(rtPath, runtimeSource);
            gcc.Compile([cPath, cached ?? rtPath], binPath, []);
        }
        finally
        {
            try { work.Delete(recursive: true); } catch { }
        }

        try
        {
            var psi = new ProcessStartInfo(binPath)
            {
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,   // binaries print UTF-8 (e.g. em-dash messages)
                // ★ ALWAYS redirect, even with no stdin to give. This used to be `stdin != null`,
                // which let a program with no test-supplied input inherit the TEST HOST's stdin —
                // and what that is depends on how the suite was launched. Under `dotnet test` on
                // Linux it is a pipe somebody still holds open, so a program that reads input
                // blocks on read() forever: measured at 2h15m of a single binary sitting in
                // pipe_read having used zero CPU. On Windows the inherited handle gives EOF, so it
                // never showed, and a run redirected from /dev/null hides it too.
                RedirectStandardInput  = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi)!;
            // Write what there is, then ALWAYS close: the close is what turns a read into EOF,
            // and EOF is what the interpreter side gives a program when its reader is null.
            if (stdin != null) proc.StandardInput.Write(stdin);
            proc.StandardInput.Close();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output;
        }
        finally
        {
            try { File.Delete(binPath); } catch { }
        }
    }

    // The emitted C source — for asserting on what the compiler DID (not just what it printed),
    // e.g. that a non-escaping store emits no copy.
    protected static string GenerateC(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        return new CodeGenerator().Generate(program);
    }

    // Interprets source and returns stdout verbatim — the oracle. Optionally feeds stdin.
    protected static string InterpretRaw(string source, string? stdin = null)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var sb = new StringWriter();
        var reader = stdin != null ? new StringReader(stdin) : null;
        new CufetInterpreter(sb, reader).Execute(program);
        return sb.ToString();
    }

    // ── Slice 9A: file I/O (whole-file read/write + path checks; OS-error → Cufet failure) ──

    // A path in the system temp dir — NOT a Controlled-Folder-Access-protected location like
    // Documents (where an unsigned freshly-compiled binary is blocked from writing). Forward-
    // slashed so the interpreter (.NET) and the compiled binary (fopen) resolve it identically.
    protected static string WritableTempPath() =>
        Path.Combine(Path.GetTempPath(), "cufet-io-" + Guid.NewGuid().ToString("N") + ".txt")
            .Replace('\\', '/');

    // Runs a source template through the oracle, substituting {PATH}, {PATH2}, {PATH3} with fresh
    // writable temp files; asserts compiled == interpreted and cleans the files up after.
    protected static void AssertFileOracle(string template, string? stdin = null)
    {
        var paths = new List<string>();
        var src = template;
        foreach (var token in new[] { "{PATH}", "{PATH2}", "{PATH3}" })
            if (src.Contains(token)) { var p = WritableTempPath(); paths.Add(p); src = src.Replace(token, p); }
        try { Assert.Equal(InterpretRaw(src, stdin), CompileRaw(src, stdin)); }
        finally { foreach (var p in paths) try { File.Delete(p.Replace('/', Path.DirectorySeparatorChar)); } catch { } }
    }

    // Like AssertFileOracle, but for programs that CHANGE the working directory. {DIR} becomes a
    // fresh empty temp directory, forward-slashed.
    //
    // The save/restore is not politeness. The working directory is process-global and Interpret()
    // runs in-process, so a leaked change would follow every later test in the class — and would
    // also be inherited by every compiled binary launched afterwards, since a child process starts
    // in its parent's directory. The failure that causes looks like an unrelated test breaking.
    protected static void AssertCwdOracle(string template)
    {
        var original = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "cufet-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var src = template.Replace("{DIR}", dir.Replace('\\', '/'));
        try
        {
            var interpreted = Interpret(src);
            Directory.SetCurrentDirectory(original);   // before compiling, so the child starts clean
            var compiled = Compile(src);
            Assert.Equal(interpreted, compiled);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── CONC.F: fan-out validation (the capstone — the work-queue finding comes home) ──
    // N worker tasks all pull from ONE shared channel (the work queue), doubling each item and
    // fanning results back to a second channel; a collector sums them. Under TRUE parallelism the
    // workers genuinely contend for the queue and the work DISTRIBUTES across them (WSL-verified,
    // e.g. 18/8/4) — vs the cooperative interpreter's one-drains-all (30/0/0). Distribution is
    // nondeterministic, so we assert the ORDER-INDEPENDENT INVARIANT: every item is processed
    // exactly once ⇒ the sum is deterministic (2·(1+…+20) == 420) regardless of who got what.

    protected const string FanOutWorkQueue = """
        Pull a rabbit.
            Define work    as a channel of number.
            Define results as a channel of number.
            Define n       as 20.
            Have rabbit start a task as w1:
                Define job as the delivery from work.
                While job is not void, repeat:
                    Send ((job but void is 0) * 2) through results.
                    job becomes the delivery from work.
                Done.
            Done.
            Have rabbit start a task as w2:
                Define job as the delivery from work.
                While job is not void, repeat:
                    Send ((job but void is 0) * 2) through results.
                    job becomes the delivery from work.
                Done.
            Done.
            Have rabbit start a task as w3:
                Define job as the delivery from work.
                While job is not void, repeat:
                    Send ((job but void is 0) * 2) through results.
                    job becomes the delivery from work.
                Done.
            Done.
            Have rabbit start a task as producer:
                Define i as 1.
                While i is n or less, repeat:
                    Send i through work.
                    i becomes i + 1.
                Done.
                Close work.
            Done.
            Have rabbit start a task as collector:
                Define total as 0.
                Define count as 0.
                Define got as the delivery from results.
                While got is not void, repeat:
                    total becomes total + (got but void is 0).
                    count becomes count + 1.
                    If count is n, Stop.
                    got becomes the delivery from results.
                Done.
                State total.
            Done.
        Done.
        """;

    // ── Arc 1E: chance (the last Arc-1 slice) — INVARIANT-tested per the settled fork ──
    // Randomness is NOT bit-identical across backends (unseeded System.Random is xoshiro256**,
    // nondeterministic-by-design; the compiler uses its own xorshift64*). So the program CHECKS its
    // own invariants and prints deterministic PASS lines: range+inclusive bounds, shuffle-is-a-
    // permutation (multiset), item-membership, empty→void, guess domain, seeded self-consistency
    // (same seed → same sequence WITHIN a backend), and element-type generality (record shuffle).

    protected const string ChanceInvariantBattery = """
        Pull a book on chance.
            Define range-ok as true.
            For each n in the range 1 to 200, repeat:
                Define r as a random number from 1 to 6.
                If r is less than 1, range-ok becomes false.
                If r is greater than 6, range-ok becomes false.
            Done.
            If range-ok, State "range PASS". Otherwise, State "range FAIL".
            Define pin as a random number from 5 to 5.
            If pin is 5, State "inclusive PASS". Otherwise, State "inclusive FAIL".
            Define xs as a series with (1, 2, 3, 4, 5, 6, 7, 8, 9, 10).
            Define sh as randomly shuffled xs.
            Define perm-ok as true.
            If (the number of sh) is not 10, perm-ok becomes false.
            For each want in xs, repeat:
                Define found as false.
                For each got in sh, repeat:
                    If got is want, found becomes true.
                Done.
                If not found, perm-ok becomes false.
            Done.
            If perm-ok, State "permutation PASS". Otherwise, State "permutation FAIL".
            Define words as a series of text with ("alpha", "beta", "gamma").
            Define pick as a random item from words.
            Define pv as pick but void is "NONE".
            Define member-ok as false.
            For each w in words, repeat:
                If pv is w, member-ok becomes true.
            Done.
            If member-ok, State "membership PASS". Otherwise, State "membership FAIL".
            Define empty as a series of number with ().
            Define nothing as a random item from empty.
            If nothing is void, State "empty-void PASS". Otherwise, State "empty-void FAIL".
            Define g as a random guess.
            If g, State "guess in domain". Otherwise, State "guess in domain".
            Seed the chance with 42.
            Define s1 as a series of number with ().
            For each n in the range 1 to 5, repeat:
                Insert (a random number from 1 to 1000000) into s1.
            Done.
            Seed the chance with 42.
            Define s2 as a series of number with ().
            For each n in the range 1 to 5, repeat:
                Insert (a random number from 1 to 1000000) into s2.
            Done.
            If s1 is s2, State "seed self-consistent PASS". Otherwise, State "seed self-consistent FAIL".
            Define party as a series of records like (the text name, the number age).
            Insert a record with (the name "Ann", the age 25) into party.
            Insert a record with (the name "Bob", the age 30) into party.
            Insert a record with (the name "Cy", the age 35) into party.
            Define shp as randomly shuffled party.
            If (the number of shp) is 3, State "record-shuffle PASS". Otherwise, State "record-shuffle FAIL".
        Done.
        """;

    protected const string ChanceExpectedPass =
        "range PASS\ninclusive PASS\npermutation PASS\nmembership PASS\nempty-void PASS\n" +
        "guess in domain\nseed self-consistent PASS\nrecord-shuffle PASS";

    // ── CONC.D: task pipes (function stages streamed through channels) ──
    // LINUX-ONLY (pthreads). Each stage runs as its own thread; adjacent stages share a channel;

    // Compiles + runs the binary, delivers SIGINT after delayMs, returns (exit code, stdout). Linux
    // only (POSIX signal delivery via /bin/kill). Used to verify the true-preemptive interrupt.
    protected static (int ExitCode, string Output) CompileAndInterrupt(string source, int delayMs)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var cSource = new CodeGenerator().Generate(program);

        // A unique stem WITHOUT creating a file: GetTempFileName is unique only while its file exists,
        // and deleting it to reuse the stem releases the name for another thread to be handed.

        var tmp     = Path.Combine(Path.GetTempPath(), "cufet-" + Guid.NewGuid().ToString("N"));
        var cPath   = tmp + ".c";
        var binPath = tmp;
        try
        {
            File.WriteAllText(cPath, cSource);
            new GccInvoker().Compile(cPath, binPath, ["-pthread"]);
        }
        finally { try { File.Delete(cPath); } catch { } }

        try
        {
            var psi = new ProcessStartInfo(binPath)
            {
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,   // binaries print UTF-8 (e.g. em-dash messages)
                RedirectStandardError  = true,
                RedirectStandardInput  = true,   // closed below — a read must give EOF, never the host's stdin
                UseShellExecute        = false,
            };
            using var proc = Process.Start(psi)!;
            proc.StandardInput.Close();
            var killer = Task.Run(() =>
            {
                Thread.Sleep(delayMs);
                try
                {
                    using var k = Process.Start(new ProcessStartInfo("/bin/kill", $"-INT {proc.Id}") { UseShellExecute = false });
                    k!.WaitForExit();
                }
                catch { }
            });
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            killer.Wait();
            return (proc.ExitCode, output.Replace("\r\n", "\n").TrimEnd('\n'));
        }
        finally { try { File.Delete(binPath); } catch { } }
    }

    // Compiles source with -fsanitize=address for memory-safety verification.
    // Skipped when not on Linux (ASan reliable only with Linux gcc).
    protected static string CompileSanitized(string source)
    {
        var tokens  = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var cSource = new CodeGenerator().Generate(program);

        // A unique stem WITHOUT creating a file: GetTempFileName is unique only while its file exists,
        // and deleting it to reuse the stem releases the name for another thread to be handed.

        var tmp     = Path.Combine(Path.GetTempPath(), "cufet-" + Guid.NewGuid().ToString("N"));
        var cPath   = tmp + ".c";
        var binPath = tmp; // no extension on Linux
        try
        {
            File.WriteAllText(cPath, cSource);

            // ★ UNDEFINED as well as ADDRESS, and `-fno-sanitize-recover` so a finding ABORTS.
            // UBSan's default is to print to stderr and carry on, which for a harness that compares
            // stdout means undefined behaviour passes silently — the report scrolls past and the
            // test goes green. Aborting turns it into a failure with the report attached.
            //
            // This matters more now that builds are optimized: -O2 is what turns latent UB into a
            // wrong answer, where -O0 forgave it.
            new GccInvoker().Compile(cPath, binPath,
                ["-fsanitize=address,undefined", "-fno-sanitize-recover=undefined", "-g"]);
        }
        finally { try { File.Delete(cPath); } catch { } }

        try
        {
            var psi = new ProcessStartInfo(binPath)
            {
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,   // binaries print UTF-8 (e.g. em-dash messages)
                RedirectStandardError  = true,
                RedirectStandardInput  = true,   // closed below — a read must give EOF, never the host's stdin
                UseShellExecute        = false,
            };
            using var proc = Process.Start(psi)!;
            proc.StandardInput.Close();
            var output = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0 || stderr.Contains("ERROR: AddressSanitizer"))
                throw new Exception(
                    $"ASan exit {proc.ExitCode}.\nStderr:\n{stderr}");
            return output.Replace("\r\n", "\n").TrimEnd('\n');
        }
        finally { try { File.Delete(binPath); } catch { } }
    }

    // ── Free-variable analysis: capture completeness ─────────────────────────
    // `CollectRefsDefs` computes a closure's / task's captured free variables (refs − defs). Two
    // gaps let a genuinely-free variable go uncaptured, each emitting an undeclared `cv_<name>`:
    //   (a) an assignment TARGET is a bare string (`BecomesStatement.Name`), invisible to the
    //       generic reflection walk — so a body that only WRITES a captured variable missed it;

    // ── Arc 3: DESTRUCTORS (`unmaking`) — UNMK.1 + UNMK.2 (MATCH-EXACTLY) ─────
    // Settled: replicate the interpreter's block-scope LIFO firing precisely — including value-copy /
    // escape DOUBLE-FIRES and both gaps (function frames, top-level). An unmaker is a per-binding
    // scope-exit HOOK, not an ownership destructor (Cufet objects are value types with no identity);
    // it is not deallocation (the arena owns memory). See [[project-design-decisions]] UNMAKERS.
    // All programs share this header (a printing unmaker instruments the firing):
    protected const string UnmakerHdr =
        "Define object handle with (the text id).\n" +
        "Bind unmaking a handle to release:\n" +
        "    State \"unmake \" joined to one's id.\n" +
        "Done.\n";
}
