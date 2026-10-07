using Xunit;
using Xunit.Abstractions;
using static Cufet.Compiler.Tests.LexerParityTests;

namespace Cufet.Compiler.Tests;

/// <summary>One program of `self-hosting/Cufet/`, built once for every test that runs it compiled.</summary>
public abstract class CompiledFrontEnd : IDisposable
{
    private readonly string _dir;
    public string Exe { get; }

    protected CompiledFrontEnd(string entry)
    {
        _dir = Directory.CreateTempSubdirectory($"cufet-{entry}-build-").FullName;
        Exe = BuildCopy(entry, _dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

public sealed class CompiledCufetParser() : CompiledFrontEnd("parser");

/// <summary>
/// The parser written in Cufet (`self-hosting/Cufet/parser/parser.cufe`) builds the same tree as the one in
/// C#, and refuses what it refuses in the same words at the same place.
/// </summary>
/// <remarks>
/// <para>
/// ★★ THE ANSWER KEY IS <c>src/Interpreter/Parser.cs</c> ITSELF. Its tree is printed as an
/// S-expression — <c>(TypeName field field …)</c>, one line per top-level statement — by reflection
/// over each node's constructor (<see cref="ParserTreePrinter"/>), and a refusal as <c>error</c> and
/// its message. The Cufet parser prints the same text, and each file is compared line by line.
/// </para>
/// <para>
/// ★★ REFUSALS ARE COMPARED ON BROKEN PROGRAMS MADE FROM WORKING ONES — every corpus file cut off
/// at a token and with a token deleted (<see cref="SourceMutations"/>) — and on a hand-written list
/// for what no mutation of the corpus reaches. Refusals are the language's distinguishing feature,
/// so a parser that builds the right trees and refuses differently would not be a replacement.
/// </para>
/// <para>
/// ⚠ What is printed is what the PARSER built. Properties the checker fills in afterwards are not
/// constructor fields and never appear.
/// </para>
/// </remarks>
public class ParserParityTests(ITestOutputHelper output, CompiledCufetParser compiled)
    : IClassFixture<CompiledCufetParser>
{
    /// <summary>How many corpus files the Cufet parser must parse. Raised as it grows; never lowered.</summary>
    private const int ParsedFloor = 100;

    /// <summary>The same, for the interpreted run's sample.</summary>
    private const int SampleFloor = 5;

    // ── The comparison ──────────────────────────────────────────────────────

    private static (int Parsed, List<string> Problems, List<string> Unsupported) Compare(
        Dictionary<string, List<string>> got, IEnumerable<(string Name, string Source)> files)
    {
        int parsed = 0;
        var problems = new List<string>();
        var unsupported = new List<string>();
        foreach (var (name, source) in files)
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
            var want = ParserTreePrinter.Expected(source);
            int before = problems.Count;
            for (int i = 0; i < Math.Max(want.Count, have.Count); i++)
            {
                string w = i < want.Count ? want[i] : "<no more statements>";
                string h = i < have.Count ? have[i] : "<no more statements>";
                if (w == h) continue;
                problems.Add($"{name}, line {i + 1}:\n    C#:    {Clip(w, h)}\n    Cufet: {Clip(h, w)}");
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
    private void Check(Dictionary<string, List<string>> got, IReadOnlyList<(string, string)> files, int floor, string how)
    {
        var (parsed, problems, unsupported) = Compare(got, files);

        output.WriteLine($"{how}: {parsed} of {files.Count} agree; {unsupported.Count} not yet:");
        foreach (var line in unsupported) output.WriteLine("    " + line);

        Assert.True(problems.Count == 0,
            $"{problems.Count} of {files.Count} differ {how}:\n{string.Join("\n", problems.Take(20))}");
        Assert.True(parsed >= floor,
            $"only {parsed} agree {how}, and the floor is {floor}. Not yet:\n{string.Join("\n", unsupported)}");
    }

    private static List<(string, string)> WithSources(IEnumerable<string> names) =>
        names.Select(n => (n, File.ReadAllText(Path.Combine(RepoRoot, n)))).ToList();

    /// <summary>Writes each source to a temporary directory and runs the compiled parser over them.</summary>
    private Dictionary<string, List<string>> RunOnSources(IReadOnlyList<(string Name, string Source)> sources, string label)
    {
        var dir = Directory.CreateTempSubdirectory($"cufet-parser-{label}-").FullName;
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

    // ── Trees ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheCompiledParser_BuildsTheSameTreeAsCSharp_ForEveryFile()
    {
        var files = Corpus();
        Assert.True(files.Count >= 80, $"only {files.Count} Cufet files found under {RepoRoot}");
        Check(ByFile(Run(compiled.Exe, files, RepoRoot)), WithSources(files), ParsedFloor, "compiled");
    }

    /// <remarks>
    /// ⚠ A SAMPLE, every fifth file, and deliberately so. Interpreted, the whole corpus takes about
    /// two minutes — a third of the suite again — while the compiled run covers every file in
    /// seconds. What this one adds is the other backend running the same parser.
    /// ★ Every 25th file since 2026-10-06 — six; at every fifth it was still 259 s, the second
    /// slowest test in the suite, for a claim a handful of files makes as well.
    /// </remarks>
    [Fact]
    public void TheInterpretedParser_BuildsTheSameTree_OnASampleOfTheCorpus()
    {
        var files = Corpus().Where((_, i) => i % 25 == 0).ToList();
        Assert.True(files.Count >= 5, $"only {files.Count} Cufet files in the sample");

        var got = ByFile(Run(CufetExe, [EntryPath("parser"), .. files], RepoRoot));
        Check(got, WithSources(files), SampleFloor, "interpreted");
    }

    // ── Refusals ────────────────────────────────────────────────────────────

    /// <remarks>
    /// ⚠ TWO mutations per file, about four hundred programs, and not more: the compiled parser
    /// takes around sixty milliseconds a file, so the forty-per-file sweep this was checked against
    /// (every one agreeing) takes minutes and stays a scratch tool. Positions are spread evenly
    /// across each file, so these two land in different places in every one of them.
    /// </remarks>
    [Fact]
    public void BrokenPrograms_AreRefusedTheSameWay_CutShortOrMissingAToken()
    {
        var mutants = new List<(string, string)>();
        foreach (var name in Corpus())
        {
            string flat = name.Replace('/', '_').Replace(".cufe", "");
            foreach (var (suffix, source) in SourceMutations.Of(File.ReadAllText(Path.Combine(RepoRoot, name)), 2))
                mutants.Add(($"{flat}__{suffix}.cufe", source));
        }
        Assert.True(mutants.Count >= 300, $"only {mutants.Count} mutants");
        // ★ Most of them must be refusals, or this is comparing trees again under another name.
        int refused = mutants.Count(m => ParserTreePrinter.Expected(m.Item2)[0].StartsWith("error\t"));
        Assert.True(refused >= mutants.Count / 2, $"only {refused} of {mutants.Count} mutants are refused");

        Check(RunOnSources(mutants, "mutants"), mutants, mutants.Count, "on broken programs");
    }

    /// <summary>Refusals no cut or deletion of the corpus reaches.</summary>
    private static readonly string[] Refused =
    [
        // The lexer, in its own two shapes.
        "State \"never closed.",
        "State 1 # 2.",
        "Define Total as 1.",
        "Add 4 to scores.",
        "Define x as 0b12.",
        "State alice'x.",
        // A reserved word where a name belongs, and a name used like a call or a field.
        "Define path as 1.",
        "For each path in a series with (1), state 1.",
        "Define total as 1.\nState total(4).",
        "Define total as 1.\nState total of (4, 5).",
        "Define total as 1.\nState total of alice.",
        "Pull a book on collections.\n    Define chase as 5.\nDone.",
        // `=` where `as` or `becomes` belongs.
        "Define x = 3.",
        "Define x as 1.\nx = 3.",
        // Inline bodies.
        "Bind number to f, return 5.",
        "Bind number to f, state 5.",
        "Bind void to g, 5 + 1.",
        // Blocks left open, by the file ending or by the next arm.
        "If true:\n    State 1.\n",
        "If true:\n    State 1.\nOtherwise:\n    State 2.\nDone.",
        "Try to:\n    State 1.\n",
        "Try to:\n    State 1.\nDone.\nIn case of failure:\n    State 2.\n",
        "Bind void to g:\n    State 1.\n",
        "For each n in a series with (1), repeat:\n    State n.\n",
        "Bind number to f, given (the number n):\n    Return n.\n",
        // Loops and functions.
        "Stop.",
        "Return 5.",
        "State 1 is more than 2.",
        "Define x as 1.\nJudge x, where it is:\nDone.",
        // Patterns, reported at the `[`.
        "Pull a book on regex.\n    Define regex r as [a{3,1}].\nDone.",
        "Pull a book on regex.\n    Define regex r as [(?=a)b].\nDone.",
        "Pull a book on regex.\n    Define regex r as [[z-a]].\nDone.",
        "Pull a book on regex.\n    Define regex r as [a|].\nDone.",
        "Pull a book on regex.\n    Define regex r, given (the text t), as [a].\nDone.",
        // Cufet held inside Cufet, reported where the block sits.
        "Pull a book on cufet.\n    Define cufet held as [\n        State.\n    ].\nDone.",
        // Two interfaces supplying one default.
        "Define shouter as an interface for the text function shout.\n"
      + "Define talker as an interface for the text function shout.\n"
      + "Bind text to shout unto shouter, \"a\".\n"
      + "Bind text to shout unto talker, \"b\".\n"
      + "Define object hare with (the text name) and shouter and talker.",
        // A value arm in the spelling that used to say `It is` again — and a name where only a
        // value can go, after `or`.
        "Define command as \"cd\".\nJudge command, where it is:\n    It is \"cd\", state \"change directory\".\n    Otherwise, state \"else\".\nDone.",
        "Define command as \"cd\".\nJudge command, where it is:\n    \"cd\" or other, state \"same\".\n    Otherwise, state \"else\".\nDone.",
    ];

    [Fact]
    public void EveryHandWrittenRefusal_IsGivenAtTheSamePlaceInTheSameWords()
    {
        var sources = Refused.Select((s, i) => ($"refused-{i + 1}.cufe", s)).ToList();
        // ⚠ Each has to be a refusal on the C# side too, or the case tests nothing.
        foreach (var (name, source) in sources)
            Assert.True(ParserTreePrinter.Expected(source)[0].StartsWith("error\t"), $"{name} is not refused by C#: {source}");
        Check(RunOnSources(sources, "refused"), sources, sources.Count, "on hand-written refusals");
    }
}
