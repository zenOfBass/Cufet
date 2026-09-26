using Xunit;
using Xunit.Abstractions;
using static Cufet.Compiler.Tests.LexerParityTests;

namespace Cufet.Compiler.Tests;

/// <summary>
/// The parser written in Cufet (`tools/front-end/parser.cufe`) builds the same tree as the one in C#.
/// </summary>
/// <remarks>
/// <para>
/// ★★ THE ANSWER KEY IS <c>src/Interpreter/Parser.cs</c> ITSELF. Its tree is printed here as an
/// S-expression — <c>(TypeName field field …)</c>, one line per top-level statement — by
/// reflection over each node's constructor, so every node kind prints the same way without a
/// printer to keep in step with Ast.cs. The Cufet parser prints the same text, and each file is
/// compared line by line.
/// </para>
/// <para>
/// ★ ALL OR NOTHING PER FILE, AND STRICT WHERE IT CLAIMS. The Cufet parser is being written a
/// construct at a time, so for a file it cannot parse yet it prints <c>unsupported</c> and stops.
/// Such a file is listed, not failed. A file it DOES parse must match exactly — and a floor on how
/// many it parses rises with each step, so coverage cannot quietly fall back.
/// </para>
/// <para>
/// ⚠ What is printed is what the PARSER built. Properties the checker fills in afterwards
/// (<c>ResolvedFunctionName</c> and the like) are not constructor fields and never appear.
/// </para>
/// </remarks>
public class ParserParityTests(ITestOutputHelper output)
{
    /// <summary>How many corpus files the Cufet parser must parse. Raised as it grows; never lowered.</summary>
    private const int ParsedFloor = 100;

    /// <summary>The same, for the interpreted run's sample.</summary>
    private const int SampleFloor = 20;

    // ── The comparison ──────────────────────────────────────────────────────

    private (int Parsed, List<string> Problems, List<string> Unsupported) Compare(Dictionary<string, List<string>> got, List<string> files)
    {
        int parsed = 0;
        var problems = new List<string>();
        var unsupported = new List<string>();
        foreach (var name in files)
        {
            if (!got.TryGetValue(name, out var have))
            {
                problems.Add($"{name}: the Cufet parser printed nothing for it");
                continue;
            }
            if (have.Count > 0 && have[0].StartsWith("unsupported\t", StringComparison.Ordinal))
            {
                unsupported.Add($"{name}\t{have[0]["unsupported\t".Length..]}");
                continue;
            }
            var want = ParserTreePrinter.Expected(File.ReadAllText(Path.Combine(RepoRoot, name)));
            int before = problems.Count;
            for (int i = 0; i < Math.Max(want.Count, have.Count); i++)
            {
                string w = i < want.Count ? want[i] : "<no more statements>";
                string h = i < have.Count ? have[i] : "<no more statements>";
                if (w == h) continue;
                problems.Add($"{name}, statement {i + 1}:\n    C#:    {Clip(w, h)}\n    Cufet: {Clip(h, w)}");
                break;
            }
            if (problems.Count == before) parsed++;
        }
        return (parsed, problems, unsupported);
    }

    /// <summary>A long line, cut to show where it first differs from the other.</summary>
    private static string Clip(string line, string other)
    {
        int at = 0;
        while (at < line.Length && at < other.Length && line[at] == other[at]) at++;
        int from = Math.Max(0, at - 60);
        return (from > 0 ? "…" : "") + line[from..Math.Min(line.Length, at + 120)];
    }

    /// <summary>Compares, reports what is not yet parsed, and holds the floor.</summary>
    private void Check(Dictionary<string, List<string>> got, List<string> files, int floor, string how)
    {
        var (parsed, problems, unsupported) = Compare(got, files);

        output.WriteLine($"{how}: {parsed} of {files.Count} files parsed; {unsupported.Count} not yet:");
        foreach (var line in unsupported) output.WriteLine("    " + line);

        Assert.True(problems.Count == 0,
            $"{problems.Count} files parse differently {how}:\n{string.Join("\n", problems)}");
        Assert.True(parsed >= floor,
            $"only {parsed} files parsed {how}, and the floor is {floor}. Not yet:\n{string.Join("\n", unsupported)}");
    }

    [Fact]
    public void TheCompiledParser_BuildsTheSameTreeAsCSharp_ForEveryFile()
    {
        var files = Corpus();
        Assert.True(files.Count >= 80, $"only {files.Count} Cufet files found under {RepoRoot}");

        var dir = Directory.CreateTempSubdirectory("cufet-parser-build-").FullName;
        try
        {
            foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot, "tools", "front-end"), "*.cufe"))
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
            // A blueprint marks the directory as a project, so the files see each other as they do
            // in tools/front-end/.
            File.WriteAllText(Path.Combine(dir, "blueprint.cufe"), "");
            Run(CufetExe, ["build", "parser.cufe"], dir);

            string exe = Path.Combine(dir, "parser" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            Check(ByFile(Run(exe, files, RepoRoot)), files, ParsedFloor, "compiled");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <remarks>
    /// ⚠ A SAMPLE, every fifth file, and deliberately so. Interpreted, the whole corpus takes about
    /// two minutes — a third of the suite again — while the compiled run above covers every file in
    /// seconds. What this one adds is the other backend running the same parser, and a fifth of the
    /// corpus reaches every kind of statement the corpus has many of.
    /// </remarks>
    [Fact]
    public void TheInterpretedParser_BuildsTheSameTree_OnASampleOfTheCorpus()
    {
        var files = Corpus().Where((_, i) => i % 5 == 0).ToList();
        Assert.True(files.Count >= 16, $"only {files.Count} Cufet files in the sample");

        var got = ByFile(Run(CufetExe, ["tools/front-end/parser.cufe", .. files], RepoRoot));
        Check(got, files, SampleFloor, "interpreted");
    }
}
