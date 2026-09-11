using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// Every place a NAME crosses between Cufet and the C library, given a name that is not ASCII.
/// </summary>
/// <remarks>
/// <para>
/// ★★ These exist because the whole suite was ASCII on the path and argument side, and that is
/// what hid nine divergences at once. On Windows the CRT's narrow entry points speak the process
/// ANSI code page while everything else in the runtime is UTF-8, so reading a file whose name is
/// not ASCII reported that it was not found WHILE PRINTING ITS NAME CORRECTLY — the message came
/// from the UTF-8 literal and only the lookup was converted.
/// </para>
/// <para>
/// ⚠⚠ Not platform-gated, deliberately. Every one of these passes on Linux and passed there before
/// the fix, because POSIX argv, getenv, getcwd, stat, fopen and opendir all deal in the caller's
/// own bytes. Gating them to Windows would say this is a Windows FEATURE; they are ordinary oracle
/// tests that happen to have only ever failed on one platform, and they belong red anywhere the
/// boundary is wrong.
/// </para>
/// <para>
/// ★ Two alphabets on purpose. U+00E9 exists in CP-1252, so a mis-encoded name kept its length and
/// its shape and only its bytes were wrong — which is how this survived so long. U+4E2D does not
/// exist there at all, so it cannot round-trip even by accident.
/// </para>
/// <para>
/// ⚠ The NAMES are built from \u escapes rather than written out. What is under test is an
/// encoding, so no character that matters here may depend on how this file itself was saved —
/// a test that could be confounded by its own encoding is worth less than one that cannot.
/// </para>
/// </remarks>
public class PipelineNonAsciiPathTests : PipelineTestBase
{
    private const string Accented = "caf\u00E9";      // "cafe" with an acute e
    private const string Han      = "\u4E2D\u6587";  // two Han characters

    /// <summary>A fresh empty directory whose own name is not ASCII.</summary>
    private static string NonAsciiDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cufet-na-" + Accented + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Slashed(string p) => p.Replace('\\', '/');

    [Fact]
    public void ANonAsciiFileName_IsReadBack_MatchesInterpreter()
    {
        var dir = NonAsciiDir();
        try
        {
            var file = Path.Combine(dir, Accented + "-" + Han + ".txt");
            File.WriteAllText(file, "first\nsecond\n");
            var src = $"""
                Try to:
                    State (read all from the file "{Slashed(file)}") trimmed.
                    Define lines as read all lines from the file "{Slashed(file)}".
                    State the number of lines converted to text.
                    With the file "{Slashed(file)}" open for reading as src:
                        State (read a line from src) but void is "(none)".
                    Done.
                Done.
                In case of failure:
                    State "FAILED".
                    State the message of the failure.
                Done.
                """;
            Assert.Equal(InterpretRaw(src), CompileRaw(src));
            // ⚠ Agreement is not enough on its own: two backends that both said "not found" would
            // agree perfectly. This pins that the file was actually read.
            Assert.Contains("first", InterpretRaw(src));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ★★ THE SILENT ONE, and the reason it asserts on the filesystem instead of on stdout. A
    // compiled write to a non-ASCII name SUCCEEDED and created a DIFFERENT file: the UTF-8 bytes
    // were read as CP-1252 characters and re-encoded. Both backends printed "wrote", so an oracle
    // comparison saw two identical outputs and passed — and a program could write the file, read
    // it back through the same wrong name, and never learn the name on disk was not the one it
    // asked for. Only the directory shows it.
    [Fact]
    public void WritingANonAsciiFileName_CreatesThatExactName()
    {
        foreach (var (label, run) in new (string, Func<string, string>)[]
                 { ("interpreted", s => InterpretRaw(s)), ("compiled", s => CompileRaw(s)) })
        {
            var dir = NonAsciiDir();
            try
            {
                var name = Accented + "-" + Han + ".txt";
                var src = $"""
                    Try to:
                        Write "body" to the file "{Slashed(Path.Combine(dir, name))}".
                        State "wrote".
                    Done.
                    In case of failure:
                        State "FAILED".
                        State the message of the failure.
                    Done.
                    """;
                Assert.Equal("wrote", run(src).Trim());
                var found = Directory.GetFiles(dir).Select(Path.GetFileName).ToArray();
                Assert.True(found.Contains(name),
                    $"{label}: asked for '{name}' and the directory holds [{string.Join(", ", found)}]. " +
                    "The write reported success, so only the filesystem can tell you it landed " +
                    "under a different name.");
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch { } }
        }
    }

    // ⚠ These ANSWER rather than fail, so a broken lookup is indistinguishable from an absent file:
    // the compiled backend said a file that was sitting right there did not exist, and there was no
    // failure to catch.
    [Fact]
    public void ThePathPredicates_AnswerForANonAsciiName_MatchesInterpreter()
    {
        var dir = NonAsciiDir();
        try
        {
            var file = Path.Combine(dir, Han + ".txt");
            File.WriteAllText(file, "x");
            var sub = Path.Combine(dir, Accented + "-sub");
            Directory.CreateDirectory(sub);
            var src = $"""
                State (the path "{Slashed(file)}" exists) converted to text.
                State (the path "{Slashed(file)}" is a file) converted to text.
                State (the path "{Slashed(file)}" is a directory) converted to text.
                State (the path "{Slashed(sub)}" is a directory) converted to text.
                """;
            Assert.Equal(InterpretRaw(src), CompileRaw(src));
            Assert.StartsWith("true", InterpretRaw(src));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ADirectoryListing_CarriesNonAsciiNames_MatchesInterpreter()
    {
        var dir = NonAsciiDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, Accented + ".txt"), "a");
            File.WriteAllText(Path.Combine(dir, Han + ".txt"), "b");
            var src = $"""
                Try to:
                    Define entries as the contents of the directory "{Slashed(dir)}".
                    State the number of entries converted to text.
                    For each entry-name in entries, repeat:
                        State entry-name.
                    Done.
                Done.
                In case of failure:
                    State "FAILED".
                    State the message of the failure.
                Done.
                """;
            Assert.Equal(InterpretRaw(src), CompileRaw(src));
            Assert.Contains(Han, InterpretRaw(src));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // The working directory both ways: entering a non-ASCII directory, then reading back where we
    // are. AssertCwdOracle is not reused because its {DIR} is an ASCII GUID — which is exactly the
    // shape that saw none of this.
    //
    // ⚠ The save/restore and the SetCurrentDirectory before compiling are copied from
    // AssertCwdOracle for its reasons: the working directory is process-global, Interpret runs
    // in-process, and a compiled child starts in its parent's directory.
    [Fact]
    public void TheCurrentDirectory_EntersAndReportsANonAsciiPath_MatchesInterpreter()
    {
        var original = Directory.GetCurrentDirectory();
        var dir = NonAsciiDir();
        try
        {
            var sub = Path.Combine(dir, Han + "-sub");
            Directory.CreateDirectory(sub);
            var src = $"""
                Try to:
                    The current directory becomes "{Slashed(sub)}".
                    Define here as the current directory but void is "(unknown)".
                    State (here contains "{Han}") converted to text.
                Done.
                In case of failure:
                    State "FAILED".
                    State the message of the failure.
                Done.
                """;
            var interpreted = InterpretRaw(src);
            Directory.SetCurrentDirectory(original);   // before compiling, so the child starts clean
            var compiled = CompileRaw(src);
            Assert.Equal(interpreted, compiled);
            Assert.StartsWith("true", interpreted);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ★ The interpreted run reads this process's own environment and the compiled one inherits it
    // from the test host, so setting it here reaches both.
    [Fact]
    public void TheEnvironmentVariable_CarriesTextThatIsNotAscii_MatchesInterpreter()
    {
        const string key = "CUFET_NONASCII_PROBE";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, Accented + "/" + Han);
            var src = $"""
                Define got as the environment variable "{key}" but void is "(unset)".
                State got.
                State (got is "{Accented}/{Han}") converted to text.
                """;
            Assert.Equal(InterpretRaw(src), CompileRaw(src));
            Assert.Contains("true", InterpretRaw(src));
        }
        finally { Environment.SetEnvironmentVariable(key, previous); }
    }
}
