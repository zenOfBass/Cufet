using System.Diagnostics;
using System.Runtime.InteropServices;
using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetInterpreter = Cufet.Interpreter.Interpreter;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

/// <summary>
/// Runs the soundness probe programs in <c>tests/fixtures/soundness/</c> as enforced regression
/// fixtures. These document the region model's outward-only store invariant — a value may not be
/// stored where it would outlive the rabbit region it lives in.
///
/// The expected outcome is encoded in the FILENAME, so adding a probe is a drop-in with no test
/// wiring to remember:
/// <list type="bullet">
///   <item><c>escape-*.cufe</c> — must be REJECTED by the shared type checker, so both backends
///   refuse it identically and neither can emit a dangling reference.</item>
///   <item><c>*-legal.cufe</c> — must COMPILE, and the native binary's output must match the
///   interpreter's (the standard oracle bar).</item>
/// </list>
/// </summary>
public class SoundnessFixtureTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "soundness");

    private static IEnumerable<object[]> Fixtures(string pattern) =>
        Directory.Exists(FixtureDir)
            ? Directory.GetFiles(FixtureDir, pattern).OrderBy(p => p).Select(p => new object[] { Path.GetFileName(p) })
            : [];

    public static IEnumerable<object[]> EscapeProbes() => Fixtures("escape-*.cufe");
    public static IEnumerable<object[]> LegalProbes()  => Fixtures("*-legal.cufe");

    /// <summary>
    /// Guards the failure mode of a directory-driven suite: if the fixtures stop being copied to
    /// the output directory, every [Theory] above silently becomes a no-op and the suite still goes
    /// green. Assert the corpus is actually present and complete.
    /// </summary>
    [Fact]
    public void FixtureCorpus_IsPresent()
    {
        Assert.True(Directory.Exists(FixtureDir), $"soundness fixtures missing from {FixtureDir}");
        Assert.Equal(6, EscapeProbes().Count());
        Assert.Equal(3, LegalProbes().Count());
    }

    [Theory]
    [MemberData(nameof(EscapeProbes))]
    public void EscapeProbe_IsRejectedByTypeChecker(string name)
    {
        var source = File.ReadAllText(Path.Combine(FixtureDir, name));
        var tokens = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        // The invariant lives in the SHARED checker, so the rejection is identical in both backends
        // and the compiler never reaches codegen — that identical-refusal is the property under test.
        Assert.Throws<TypeException>(() => new TypeChecker().Check(program));
    }

    [Theory]
    [MemberData(nameof(LegalProbes))]
    public void LegalProbe_CompilesAndMatchesInterpreter(string name)
    {
        var source = File.ReadAllText(Path.Combine(FixtureDir, name));
        Assert.Equal(Interpret(source), Compile(source));
    }

    // ── Harness (mirrors PipelineTests) ──────────────────────────────────────

    private static string Interpret(string source)
    {
        var tokens = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var sb = new StringWriter();
        new CufetInterpreter(sb, null).Execute(program);
        return sb.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static string Compile(string source)
    {
        var tokens = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        var cSource = new CodeGenerator().Generate(program);

        // A unique stem WITHOUT creating a file: GetTempFileName is unique only while its file exists,
        // and deleting it to reuse the stem releases the name for another thread to be handed.

        var tmp = Path.Combine(Path.GetTempPath(), "cufet-" + Guid.NewGuid().ToString("N"));
        var cPath = tmp + ".c";
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
                StandardOutputEncoding = System.Text.Encoding.UTF8,   // binaries print UTF-8
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
