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
                throw new CompilerException($"gcc compilation failed:\n{stderr.Trim()}");
        }
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
