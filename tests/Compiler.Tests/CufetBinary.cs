using System.Runtime.InteropServices;

namespace Cufet.Compiler.Tests;

/// <summary>Where the real `cufet` command is, for a test that drives it rather than calls into it.</summary>
/// <remarks>
/// <para>
/// ★ ONE ANSWER, because there are now two suites that need it and they must not drift.
/// <c>CommandLineTests</c> drives the CLI to check what it accepts and refuses;
/// <c>ExampleOracleTests</c> drives it to RUN the interpreted half of the corpus. A second copy of
/// this path would be a second thing to fix the day the target framework moves.
/// </para>
/// <para>
/// ⚠ The binary exists during these tests because the csproj references the App with
/// <c>ReferenceOutputAssembly="false"</c> — purely so it is built, not so it is linked.
/// </para>
/// </remarks>
internal static class CufetBinary
{
    /// <summary>The repository root, found by walking up for the solution file.</summary>
    internal static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>The built `cufet` executable.</summary>
    internal static string Path { get; } = System.IO.Path.Combine(
        RepoRoot, "src", "App", "bin", "Debug", "net10.0",
        "Cufet.App" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Cufet.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? "";
    }
}
