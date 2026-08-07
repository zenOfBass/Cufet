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

    public void Compile(string cSourcePath, string outputPath, IReadOnlyList<string> extraFlags)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gcc,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(cSourcePath);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputPath);
        // -pthread: harmless for non-threaded programs; required to link the concurrency runtime
        // (pthreads) on Linux. On mingw it's a no-op-ish flag (concurrency programs are POSIX-only).
        psi.ArgumentList.Add("-pthread");
        // -lm: the math-book transcendentals (sqrt/log/pow) need libm on Linux; a no-op stub on mingw.
        psi.ArgumentList.Add("-lm");
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
    private static string BuildFailureMessage(string cSourcePath, string stderr)
    {
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
