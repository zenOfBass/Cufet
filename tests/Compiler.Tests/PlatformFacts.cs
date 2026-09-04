using System.Runtime.InteropServices;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>A test that only means anything on Linux — reported as SKIPPED anywhere else.</summary>
/// <remarks>
/// <para>
/// ★★ These used to open with <c>if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;</c>,
/// which xUnit records as a PASS. 83 tests did that, so a Windows run reported 861 green with 83 of
/// them never executed — and the number gave no hint. A test of mine sat in that set and had never
/// once run until CI caught it.
/// </para>
/// <para>
/// ★ Setting <see cref="FactAttribute.Skip"/> from the constructor is read at DISCOVERY, so the body
/// never runs and the reason travels with the result. No dependency and no conditional compilation:
/// measured against xUnit 2.4.2, which does NOT honour the dynamic-skip token on a plain
/// <c>[Fact]</c> — that route needs a custom runner, and this does not.
/// </para>
/// <para>
/// ⚠ Skipping is not covering. These still have to RUN somewhere before a change is green — see
/// CONTRIBUTING, and `wsl -e bash -lc "cd /mnt/c/dev/Cufet &amp;&amp; dotnet test tests/Compiler.Tests"`.
/// The point of this attribute is that the Windows report now SAYS so instead of implying otherwise.
/// </para>
/// </remarks>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Skip = "Linux-only: POSIX features (concurrency, subprocess, signals) that mingw cannot "
                 + "build, and the sanitizer runs. Run under WSL or Linux.";
    }
}

/// <summary>A test that only means anything on Windows — reported as SKIPPED anywhere else.</summary>
/// <remarks>The same trap in the other direction, and it was silently passing on Linux.</remarks>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Skip = "Windows-only: behaviour that exists only on Windows.";
    }
}
