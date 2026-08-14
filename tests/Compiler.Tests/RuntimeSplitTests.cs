using Cufet.Compiler;
using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

/// <summary>
/// The runtime as its own translation unit, and the object cache in front of it.
/// </summary>
///
/// ★ Most of the assurance here is NOT in this file. PipelineTestBase compiles through the split
/// path, so all 648 pipeline tests link a generated program against the derived header and the
/// cached object — if the header lost a prototype or the split dropped a definition, gcc says so
/// hundreds of times over. These cover what that cannot: that the split is well-formed on its own
/// terms, and that the cache is a cache rather than a dependency.
public class RuntimeSplitTests
{
    private static (string Header, string Runtime, string Program) Split(string source)
    {
        var tokens = new CufetLexer(source).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);
        return new CodeGenerator().GenerateSplit(program);
    }

    [Fact]
    public void TheProgramHalf_NoLongerCarriesTheRuntime()
    {
        // The reason `emit-c` exists is that someone can read the C their program became, and that
        // was 79% runtime for a typical example and 98.9% for a small one.
        var (_, runtime, program) = Split("""State "hi".""");

        Assert.Contains("cufet_arena_alloc", runtime);
        Assert.DoesNotContain("static void* cufet_arena_alloc(size_t size) {", program);
        Assert.Contains($"#include \"{RuntimeSplit.HeaderFileName}\"", program);
        Assert.True(program.Length < runtime.Length / 4,
            $"the program half should be a fraction of the runtime, but was {program.Length} to {runtime.Length}");
    }

    [Fact]
    public void TheCombinedForm_IsExactlyTheTwoHalvesJoined()
    {
        // ★ The combined output is not a second code path — it is the concatenation the split is cut
        // from. If these ever diverge, one of the two is a shape nobody tests.
        const string src = """
            Define total as 1 + 2.
            State total.
            """;
        var tokens = new CufetLexer(src).Tokenize();
        var program = new Parser(tokens).Parse();
        new TypeChecker().Check(program);

        // ⚠ A FRESH generator each time. GenerateSplit runs Generate internally, and a CodeGenerator
        // accumulates per-program state, so reusing one instance across both calls emits the second
        // program on top of the first's leftovers.
        string combined = new CodeGenerator().Generate(program);
        var (_, runtime, programHalf) = new CodeGenerator().GenerateSplit(program);

        // The program half gains only the include line the split needs. Strip that, and what remains
        // must be exactly the tail of the combined output — unchanged, not merely equivalent. (The
        // runtime half is not compared byte-for-byte here because the split rewrites it: `static` is
        // dropped so the program can link to it, and declarations move to the header.)
        string withoutInclude = programHalf.Replace($"#include \"{RuntimeSplit.HeaderFileName}\"\n\n", "");
        Assert.EndsWith(withoutInclude, combined, StringComparison.Ordinal);
        Assert.Contains("cufet_arena_alloc", runtime);
    }

    [Fact]
    public void PreprocessorConditionals_SurviveInBothFiles()
    {
        // ⚠ The bug this exists for. The SIGINT substrate is `#if defined(__unix__) ... #else ...
        // #endif` around two complete implementations. Sending the guards to the header only left
        // BOTH bodies in the source unguarded — "redefinition of cufet_interrupted", plus the POSIX
        // branch demanding sigaction on a Windows build. Both files must select the same branch.
        var (header, runtime, _) = Split("""
            Pull a rabbit.
                Have rabbit start a task as batch: return 1 + 2. Done.
                State the awaited result of batch.
            Done.
            """);

        Assert.Equal(0, Balance(header));
        Assert.Equal(0, Balance(runtime));
        Assert.Contains("#if", runtime);
        Assert.Contains("#if", header);
    }

    // #if/#ifdef/#ifndef open a level, #endif closes one. Zero means every conditional was closed.
    private static int Balance(string text)
    {
        int depth = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimStart();
            if (line.StartsWith("#if")) depth++;
            else if (line.StartsWith("#endif")) depth--;
        }
        return depth;
    }

    [Fact]
    public void AnAggregateInitializer_IsNotMistakenForAFunctionBody()
    {
        // `static const cufet_u256 CUFET_DEC_MAX = {...};` has a brace that is not a body. Reading
        // it as one emitted the nonsense `const cufet_u256 CUFET_DEC_MAX =;` into the header.
        var (header, _, _) = Split("""State 1.""");

        Assert.DoesNotContain("=;", header.Replace(" ", ""));
        Assert.Contains("CUFET_DEC_MAX", header);
        Assert.Contains("extern", header);
    }

    [Fact]
    public void TheHeaderDeclaresRatherThanDefines()
    {
        // A definition in a header is a symbol in every unit that includes it. Variables must come
        // through as `extern` with no initializer.
        var (header, _, _) = Split("""State 1.""");

        foreach (var line in header.Replace("\r\n", "\n").Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("extern ") && t.Contains('='))
                Assert.Fail($"header carries an initializer, which defines the object in every includer: {t}");
        }
    }

    [Fact]
    public void TheCache_ReusesOneObjectForTheSameRuntime()
    {
        var root = Directory.CreateTempSubdirectory("cufet-cache-test-");
        try
        {
            var (header, runtime, _) = Split("""State "hi".""");
            var cache = new RuntimeCache(root.FullName);
            var gcc = new GccInvoker();

            string? first = cache.ObjectFor(runtime, header, gcc, []);
            string? second = cache.ObjectFor(runtime, header, gcc, []);

            Assert.NotNull(first);
            Assert.Equal(first, second);
            Assert.True(File.Exists(first));

            // ★ And a DIFFERENT runtime must not collide with it. The failure mode of a key that is
            // too coarse is a stale object linked into a program that no longer matches it, which
            // surfaces as a mystery bug inside generated code.
            string? other = cache.ObjectFor(runtime + "\nstatic int cufet_probe(void) { return 1; }\n", header, gcc, []);
            Assert.NotNull(other);
            Assert.NotEqual(first, other);
        }
        finally { try { root.Delete(recursive: true); } catch { } }
    }

    [Fact]
    public void TheCache_IsInvalidatedByTheCompilerItIsKeyedOn()
    {
        // Upgrading gcc in place leaves the path identical while the object it emits changes, so the
        // key carries `gcc --version`, not just the executable path.
        var gcc = new GccInvoker();
        Assert.False(string.IsNullOrWhiteSpace(gcc.Identification));
        Assert.Contains("|", gcc.Identification);
    }

    [Fact]
    public void AnUnusableCache_ReturnsNullRatherThanFailingTheBuild()
    {
        // ★★ The cache must never be load-bearing. A read-only home, a locked-down CI image or a
        // sandbox has to fall back to compiling the runtime in place — which is exactly what every
        // build did before the cache existed. Keeping `gcc is the only requirement` true is worth
        // more than the few hundred milliseconds.
        string asFile = Path.Combine(Path.GetTempPath(), "cufet-cache-not-a-dir-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(asFile, "this is a file, not a directory");
        try
        {
            var (header, runtime, _) = Split("""State "hi".""");
            var cache = new RuntimeCache(Path.Combine(asFile, "runtime"));

            Assert.Null(cache.ObjectFor(runtime, header, new GccInvoker(), []));
        }
        finally { try { File.Delete(asFile); } catch { } }
    }

    [Fact]
    public void TheCacheRoot_IsAUserDirectoryNotTheProject()
    {
        // Build output belongs to the machine. Dropping a .o into someone's source tree puts a file
        // they did not ask for into their version control and their editor.
        var root = new RuntimeCache().Root;

        Assert.NotNull(root);
        Assert.Contains("cufet", root!, StringComparison.OrdinalIgnoreCase);
        Assert.False(root.StartsWith(Directory.GetCurrentDirectory(), StringComparison.OrdinalIgnoreCase),
            $"the cache must not live under the working directory, but was {root}");
    }
}
