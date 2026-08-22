using Cufet.Interpreter;
using System.Diagnostics;

namespace Cufet.Compiler;

public sealed class GccInvoker
{
    private readonly string _gcc;

    public GccInvoker(string? gccPath = null)
    {
        _gcc = gccPath ?? FindGcc();
    }

    // Compiles cSourcePath to a native binary at outputPath.
    // Throws CompilerException if gcc is missing or the compilation fails.
    public void Compile(string cSourcePath, string outputPath) =>
        Compile(cSourcePath, outputPath, []);

    /// <summary>
    /// Compiles one translation unit to an object file (`-c`), for the cached runtime.
    /// </summary>
    public void CompileObject(string cSourcePath, string objectPath, IReadOnlyList<string> extraFlags) =>
        Compile(cSourcePath, objectPath, [.. extraFlags, "-c"]);

    /// <summary>
    /// Identifies this exact compiler, for the runtime cache key.
    /// </summary>
    /// <remarks>
    /// ★ The PATH alone is not enough. Upgrading gcc in place leaves the path identical while the
    /// object it produces changes, and a stale cached object surfaces as a mystery bug inside
    /// generated code — the worst kind to debug. `gcc --version` moves when the compiler does.
    /// </remarks>
    public string Identification
    {
        get
        {
            if (_identification != null) return _identification;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _gcc,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("--version");
                using var proc = Process.Start(psi)!;
                string first = proc.StandardOutput.ReadLine() ?? "";
                proc.WaitForExit();
                return _identification = $"{_gcc}|{first}";
            }
            catch
            {
                // Unknown version means "assume it changed" — a key nobody matches costs one
                // recompile, where a key that wrongly matches costs a corrupt build.
                return _identification = $"{_gcc}|unknown-{Guid.NewGuid():N}";
            }
        }
    }

    private string? _identification;

    public void Compile(string cSourcePath, string outputPath, IReadOnlyList<string> extraFlags) =>
        Compile([cSourcePath], outputPath, extraFlags);

    /// <summary>
    /// Compiles and links several inputs — the generated program plus either the cached runtime
    /// object or the runtime source, depending on whether the cache was usable.
    /// </summary>
    public void Compile(IReadOnlyList<string> inputPaths, string outputPath, IReadOnlyList<string> extraFlags)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gcc,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var input in inputPaths)
            psi.ArgumentList.Add(input);
        string cSourcePath = inputPaths[0];
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputPath);
        // -pthread: harmless for non-threaded programs; required to link the concurrency runtime
        // (pthreads) on Linux. On mingw it's a no-op-ish flag (concurrency programs are POSIX-only).
        psi.ArgumentList.Add("-pthread");
        // -lm: the math-book transcendentals (sqrt/log/pow) need libm on Linux; a no-op stub on mingw.
        psi.ArgumentList.Add("-lm");
        // -lws2_32 on Windows: sockets are IN libc on Linux and in a separate library here, so
        // without this an axiom calling `socket()` compiles and then fails to link
        // ("undefined reference to `__imp_socket`" — measured). The foreign header set includes
        // <winsock2.h>, and a header whose functions cannot be linked is worse than no header:
        // it is the trap that argues against letting anyone name their own. Costs nothing for a
        // program that uses no sockets — the linker pulls only what is referenced.
        if (OperatingSystem.IsWindows())
            psi.ArgumentList.Add("-lws2_32");
        // ★ Optimized ALWAYS, the Go answer rather than the Rust one. There is no debug build and no
        // --release: "compiled Cufet is fast" should be unconditionally true rather than something a
        // reader has to know a flag to obtain. The common failure of the opt-in design is someone
        // benchmarking the default build, getting a bad number, and concluding the language is slow.
        //
        // Nothing is lost by having no opt-out, because `emit-c` already hands over the source — and
        // anyone who wants -O0 (stepping through generated C in gdb, triaging a suspected
        // miscompilation) needs that source anyway. A flag would be the weaker version of a
        // capability that already ships.
        //
        // ⚠ This makes latent undefined behaviour dangerous where -O0 forgave it, which is why it
        // ships together with the sanitizer sweep rather than on its own.
        if (!extraFlags.Any(f => f.StartsWith("-O", StringComparison.Ordinal)))
            psi.ArgumentList.Add("-O2");
        foreach (var flag in extraFlags)
            psi.ArgumentList.Add(flag);

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new CompilerException("Failed to start gcc.");
        }
        catch (Exception e) when (e is not CompilerException)
        {
            throw new CompilerException($"Could not launch gcc ({_gcc}): {e.Message}\nInstall gcc and add it to your PATH.");
        }

        using (proc)
        {
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new CompilerException(BuildFailureMessage(cSourcePath, stderr.Trim()));
        }
    }

    // ★ gcc failing on the generated C is not the author's mistake to fix.
    //
    // Every line gcc reads was written by this compiler, so an error pointing INSIDE that file
    // means the code generator emitted something invalid — a defect here, not a problem with the
    // Cufet program. Saying only "gcc compilation failed" and pasting a complaint about generated
    // identifiers hands the author a diagnostic they cannot act on and implies the fault is theirs.
    //
    // That is exactly how the Judge grouped-arm bug presented: `cufet check --native` reported the
    // program clean, and the only symptom was gcc objecting to a member of `cun_0` — a name that
    // appears nowhere in the source and means nothing to the person who wrote it.
    //
    // An error that does NOT point into the generated file is an environment problem — no
    // toolchain header, a linker failure — which the author CAN act on, so it is said plainly
    // instead of being dressed up as a bug report.
    //
    // ⚠ And one line of that reasoning stopped being true when axioms arrived: "every line gcc
    // reads was written by this compiler" now has an exception, and it is the one place an author
    // CAN act on. An axiom is C the author wrote and cufet reproduced verbatim, so a complaint
    // inside one is theirs to fix — blaming cufet for it would send someone hunting a bug that is
    // not there.
    private static string BuildFailureMessage(string cSourcePath, string stderr)
    {
        if (ForeignC.BlamesForeignSource(stderr))
            return "gcc rejected the foreign source in this program:\n\n"
                 + $"{stderr}\n\n"
                 + "The C inside [ ... ] is reproduced exactly as written, so this is a complaint "
                 + "about that source — not about anything cufet generated around it.";

        if (!stderr.Contains(Path.GetFileName(cSourcePath), StringComparison.Ordinal))
            return $"gcc could not build the generated C:\n{stderr}\n\n"
                 + "Nothing here points inside the generated file, so this is most likely a problem "
                 + "with the toolchain rather than with your program.";

        return "★ This is a bug in the Cufet compiler, not in your program.\n\n"
             + "The C that gcc rejected was written by cufet, so nothing you change in the .cufe "
             + "file is at fault. What gcc said:\n\n"
             + $"{stderr}\n\n"
             + "Please report it with the program and this message. `cufet emit-c <file.cufe> out.c` "
             + "writes the generated C if that helps.";
    }

    private static string FindGcc()
    {
        // Probe well-known installation paths before falling back to PATH lookup.
        string[] candidates =
        [
            @"C:\msys64\mingw64\bin\gcc.exe",
            @"C:\msys64\usr\bin\gcc.exe",
            @"C:\mingw64\bin\gcc.exe",
            @"C:\cygwin64\bin\gcc.exe",
            "/usr/bin/gcc",
            "/usr/local/bin/gcc",
        ];
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return "gcc";
    }
}
