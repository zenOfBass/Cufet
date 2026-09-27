using Xunit;
using Xunit.Abstractions;
using static Cufet.Compiler.Tests.LexerParityTests;

namespace Cufet.Compiler.Tests;

public sealed class CompiledCufetChecker() : CompiledFrontEnd("checker");

/// <summary>
/// The checker written in Cufet (`tools/front-end/checker.cufe`) accepts what the one in C# accepts,
/// and refuses what it refuses in the same words.
/// </summary>
/// <remarks>
/// <para>
/// ★★ THE ANSWER KEY IS <c>TypeChecker.Check</c> ITSELF, run on each file alone
/// (<see cref="CheckerAnswer"/>): `ok`, or `error` and the whole message on one line. The FIRST
/// refusal is what is compared — C# stops there, so there is no second one to compare.
/// </para>
/// <para>
/// ★★ `unsupported` IS NOT A DIFFERENCE, AND IT IS NOT AGREEMENT. The Cufet checker says it for
/// any program holding something it cannot judge yet, and such a file is listed rather than
/// failed. What holds the checker to its growth is the FLOOR — how many must agree — raised as
/// it grows and never lowered.
/// </para>
/// </remarks>
public class CheckerParityTests(ITestOutputHelper output, CompiledCufetChecker compiled)
    : IClassFixture<CompiledCufetChecker>
{
    /// <summary>How many corpus files must agree. Raised as the checker grows; never lowered.</summary>
    private const int CorpusFloor = 0;

    // ── The comparison ──────────────────────────────────────────────────────

    private void Check(Dictionary<string, List<string>> got, IReadOnlyList<(string Name, string Source)> files,
                       int floor, string how)
    {
        int agreed = 0;
        var problems = new List<string>();
        var unsupported = new List<string>();
        foreach (var (name, source) in files)
        {
            string want = CheckerAnswer.Expected(source);
            string have = got.TryGetValue(name, out var lines) && lines.Count > 0 ? lines[0] : "<nothing printed>";
            if (have.StartsWith("unsupported\t", StringComparison.Ordinal))
                unsupported.Add($"{name}\t{have["unsupported\t".Length..]}");
            else if (have == want)
                agreed++;
            else
                problems.Add($"{name}:\n    C#:    {want}\n    Cufet: {have}");
        }

        output.WriteLine($"{how}: {agreed} of {files.Count} agree; {unsupported.Count} not yet:");
        foreach (var line in unsupported) output.WriteLine("    " + line);

        Assert.True(problems.Count == 0,
            $"{problems.Count} of {files.Count} differ {how}:\n{string.Join("\n", problems.Take(20))}");
        Assert.True(agreed >= floor, $"only {agreed} agree {how}, and the floor is {floor}.");
    }

    /// <summary>Programs the C# parser reads — a parse refusal leaves the checker nothing to say.</summary>
    private static List<(string, string)> Parsed(IEnumerable<(string Name, string Source)> files) =>
        files.Where(f => CheckerAnswer.Expected(f.Source) != "parse").ToList();

    /// <summary>Writes each source to a temporary directory and runs the compiled checker over them.</summary>
    private Dictionary<string, List<string>> RunOnSources(IReadOnlyList<(string Name, string Source)> sources, string label)
    {
        var dir = Directory.CreateTempSubdirectory($"cufet-checker-{label}-").FullName;
        try
        {
            foreach (var (name, source) in sources) File.WriteAllText(Path.Combine(dir, name), source);
            var got = new Dictionary<string, List<string>>();
            // In batches: a Windows command line has a length limit.
            foreach (var batch in sources.Select(s => s.Name).Chunk(200))
                foreach (var (k, v) in ByFile(Run(compiled.Exe, batch, dir))) got[k] = v;
            return got;
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── Hand-written programs ───────────────────────────────────────────────

    /// <summary>
    /// Small programs, accepted and refused, each of which the Cufet checker must JUDGE — none of
    /// them may come back `unsupported`. What the checker has learned to do is written down here.
    /// </summary>
    private static readonly string[] Judged =
    [
        // Names: undefined, a book, a book's type, ended with its block.
        "State 1.",
        "State mystery.",
        "State math.",
        "Define math as 5.",
        "State matrix.",
        "State step.",
        "If true:\n    Define y as 1.\nDone.\nState y.",
        "While false, repeat:\n    State z.\nDone.",
        "The q becomes 3.",
        // Defining twice, shadowing, and the two names every program is given.
        "Define x as 1.\nDefine x as 2.",
        "Define y as 1.\nIf true:\n    Define y as 2.\nDone.",
        "Define y as 1.\nIf true:\n    Define a shadow y as 2.\nDone.",
        "If true:\n    Define a shadow y as 2.\nDone.",
        "Define input as 3.",
        "Define output as 3.",
        "If true:\n    Define input as 3.\nDone.",
        // Reassigning: the type is for life, and a permanent name is fixed.
        "Define x as 1.\nThe x becomes \"a\".",
        "Define x as 1.\nThe x becomes 2.\nState x.",
        "Define x as 1 permanently.\nThe x becomes 2.",
        "Define n as 0.\nRepeat:\n    Increment n by 1.\nUntil n is 3.",
        "Define x as void.\nState x.",
        // A permanent name is known before its line.
        "Define big as 10 permanently.\nState big.",
        "State later.\nDefine later as 1 permanently.",
        // Operators.
        "State 1 + \"a\".",
        "State \"a\" - 1.",
        "State 1 is \"a\".",
        "State 1 is less than \"a\".",
        "State 1 and true.",
        "State true and \"a\".",
        "State not 5.",
        "State not \"a\".",
        "State -\"a\".",
        "Define flag as 0xFF.\nState flag and 0x0F.",
        "Define flag as 0xFF.\nState flag + 1.",
        "Define flag as 0xFF.\nState flag and 1.",
    ];

    [Fact]
    public void EveryHandWrittenProgram_IsJudgedAsCSharpJudgesIt()
    {
        var sources = Judged.Select((s, i) => ($"judged-{i + 1}.cufe", s)).ToList();
        foreach (var (name, source) in sources)
            Assert.True(CheckerAnswer.Expected(source) != "parse", $"{name} does not parse: {source}");
        Check(RunOnSources(sources, "judged"), sources, sources.Count, "on hand-written programs");
    }

    // ── The corpus ──────────────────────────────────────────────────────────

    [Fact]
    public void TheCompiledChecker_JudgesEveryFileAsCSharpDoes()
    {
        var files = Corpus();
        Assert.True(files.Count >= 80, $"only {files.Count} Cufet files found under {RepoRoot}");
        var sources = Parsed(files.Select(n => (n, File.ReadAllText(Path.Combine(RepoRoot, n)))));
        Check(ByFile(Run(compiled.Exe, files, RepoRoot)), sources, CorpusFloor, "on the corpus");
    }
}
