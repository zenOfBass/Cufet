using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

/// <summary>
/// Every runnable code block in the docs, held to the front end.
///
/// ★ A documented sample that no longer runs is worse than no sample: a reader copies it, it fails,
/// and they conclude the language is broken rather than the doc. This session alone found four
/// stale claims by executing samples and none by reading them.
///
/// This is a CHANGE DETECTOR, not a gate. Of ~238 runnable blocks, ~81 currently fail, and most of
/// those failures are correct — GRAMMAR is a constraints reference full of deliberate
/// counter-examples, and plenty of blocks are fragments that teach a shape rather than run. Judging
/// those needs a fence convention the docs do not have yet (every one of their 534 fences is
/// untagged), which is its own task. See ROADMAP.
///
/// So: the blocks that pass TODAY are recorded, and this fails if one of them stops passing while
/// its text is unchanged. Editing a block changes its hash and drops it out of the baseline until
/// someone regenerates — the known cost of the cheap version, and the reason the failure message
/// says how to regenerate.
///
///     CUFET_DOC_BASELINE=1 dotnet test --filter DocBlockTests
/// </summary>
public class DocBlockTests
{
    // Paths are repo-root-relative with forward slashes: they are recorded in the baseline, so
    // they must not vary by platform.
    private static readonly string[] DocFiles =
        ["README.md", "docs/BOOKS.md", "docs/GRAMMAR.md", "docs/REFERENCE.md"];

    private static string RepoRoot => FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cufet.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? "";
    }

    private static string BaselinePath =>
        Path.Combine(RepoRoot, "tests", "Compiler.Tests", "doc-blocks.baseline.txt");

    private sealed record Block(string File, int Line, string Source, string Hash, string Head);

    // Docs annotate samples with a trailing arrow to show a value or a note. That is prose, not
    // code — and it points BOTH ways, which is easy to miss: handling only ← leaves every → sample
    // failing to lex. (tools/doc-sweep.py learned this the hard way; same rule here.)
    private static readonly Regex Annotation = new(@"\s*[←→].*$", RegexOptions.Multiline);

    // ── Extraction ────────────────────────────────────────────────────────

    private static IEnumerable<Block> RunnableBlocks()
    {
        foreach (var file in DocFiles)
        {
            var path = Path.Combine(RepoRoot, file);
            if (!File.Exists(path)) continue;
            var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("```")) continue;
                int indent = lines[i].Length - lines[i].TrimStart().Length;
                int start = i + 1;
                var body = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    body.Add(lines[i].Length >= indent && lines[i][..indent].Trim().Length == 0
                        ? lines[i][indent..] : lines[i]);
                    i++;
                }

                var raw = string.Join("\n", body);
                if (SkipReason(raw) is not null) continue;

                var source = Annotation.Replace(raw, "");
                yield return new Block(file, start + 1, source, HashOf(source),
                                       raw.Trim().Split('\n')[0].Trim());
            }
        }
    }

    /// <summary>Null if this looks like a Cufet program, else why it is not one. Mirrors
    /// tools/doc-sweep.py's skip_reason — the two must agree or their counts stop meaning the
    /// same thing.</summary>
    private static string? SkipReason(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return "empty";
        if (t.StartsWith('#') || t.StartsWith('$') || t.StartsWith("PS ")) return "shell";
        if (Regex.IsMatch(t, @"^\s*(cufet|dotnet|gcc|npm|git|Copy-Item|New-Item|cd |ls |mkdir)\b")) return "shell";
        if (t.Contains("$PWD") || t.Contains("$env:")) return "shell";
        if (t.Contains("#include") || t.Contains("typedef ") || t.Contains("int main(")) return "C";
        if (t.StartsWith('{') && t.Contains('"')) return "json";
        if (t.Contains('├') || t.Contains('└') || Regex.IsMatch(t, @"(?m)^\S+/\s*$")) return "tree";
        if (t.Contains("──") || t.Contains('│')) return "diagram";
        if (Regex.IsMatch(t, @"That doesn't work:|Here on line \d+|^\s*Line \d+, column")) return "diagnostic";
        if (Regex.IsMatch(t, @"(^|\s)\.\.\.(\s|$)")) return "elided";
        if (Regex.IsMatch(t, @"<[a-z][a-z -]*>") || Regex.IsMatch(t, @"\bitem N\b|\bN of\b")) return "metavariable";
        // No statement word anywhere: almost certainly expected output or prose.
        if (!Regex.IsMatch(t, @"\b(State|Define|If|For|While|Bind|Cast|Return|Add|Remove|Pull|Have|Send|" +
                              @"With|Try|In|Write|Append|Repeat|Stop|Skip|Item|Run|Get|Set|Done|Output|Seed)\b",
                           RegexOptions.IgnoreCase))
            return "not-code";
        return null;
    }

    private static string HashOf(string source)
    {
        // Whitespace-normalised so a reflow or re-indent does not silently drop a block from the
        // baseline — only a real edit to the code should.
        var normalised = string.Join("\n",
            source.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)))[..16].ToLowerInvariant();
    }

    private static bool Checks(string source)
    {
        try
        {
            var program = new Parser(new CufetLexer(source).Tokenize()).Parse();
            new TypeChecker().Check(program);
            return true;
        }
        catch { return false; }
    }

    // ── The test ──────────────────────────────────────────────────────────

    [Fact]
    public void DocBlocksThatPassed_StillPass()
    {
        var blocks = RunnableBlocks().ToList();

        // A change detector that stops finding its corpus reports perfect health. Guard it the same
        // way the example and soundness suites do.
        Assert.True(blocks.Count >= 150,
            $"only {blocks.Count} runnable doc blocks found — extraction broke, or the docs shrank.");

        var passing = blocks.Where(b => Checks(b.Source))
                            .GroupBy(b => b.Hash)
                            .ToDictionary(g => g.Key, g => g.First());
        var present = blocks.GroupBy(b => b.Hash).ToDictionary(g => g.Key, g => g.First());

        if (Environment.GetEnvironmentVariable("CUFET_DOC_BASELINE") == "1")
        {
            WriteBaseline(passing.Values);
            return;
        }

        Assert.True(File.Exists(BaselinePath),
            $"no baseline at {BaselinePath} — generate it with:\n" +
            "  CUFET_DOC_BASELINE=1 dotnet test --filter DocBlockTests");

        var baselineLines = File.ReadAllLines(BaselinePath);
        var baseline = baselineLines
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t')[0])
            .ToHashSet();

        // FIRST, because it can NAME them. A recorded block still present with its text unchanged
        // that no longer checks: the language moved underneath the doc. Blocks whose hash is gone
        // were edited or deleted, which is legitimate and not this check's business.
        var regressed = baseline
            .Where(h => present.ContainsKey(h) && !passing.ContainsKey(h))
            .Select(h => $"  {present[h].File}:{present[h].Line}  {present[h].Head}")
            .OrderBy(s => s)
            .ToList();

        Assert.True(regressed.Count == 0,
            $"{regressed.Count} documented sample(s) stopped checking, unchanged:\n" +
            string.Join("\n", regressed.Take(20)) +
            (regressed.Count > 20 ? $"\n  … and {regressed.Count - 20} more" : "") +
            "\n\nEither the doc is now wrong, or the language changed under it. Fix it, then:\n" +
            "  CUFET_DOC_BASELINE=1 dotnet test --filter DocBlockTests");

        // ★ THEN the count, which is not redundant — it catches what hashing structurally cannot.
        // Editing a block changes its hash, so it drops out of the baseline and the check above
        // sees nothing: breaking a sample by rewriting it looked exactly like legitimately
        // rewriting it, and the first version of this test passed happily while a sample was
        // broken. The count has no such blind spot, at the price of not being able to say which.
        Assert.True(passing.Count >= baseline.Count,
            $"documented samples that check clean dropped from {baseline.Count} to {passing.Count}.\n" +
            "No recorded block regressed, so an EDITED or DELETED one is responsible — a rewrite\n" +
            "that no longer checks looks identical to a legitimate rewrite from here.\n" +
            "If it was a deletion:\n" +
            "  CUFET_DOC_BASELINE=1 dotnet test --filter DocBlockTests");
    }

    private static void WriteBaseline(IEnumerable<Block> passing)
    {
        var rows = passing
            .OrderBy(b => b.File).ThenBy(b => b.Line)
            .Select(b => $"{b.Hash}\t{b.File}:{b.Line}\t{b.Head}");
        File.WriteAllText(BaselinePath,
            "# Doc code blocks that check clean, recorded so a regression is visible.\n" +
            "# Hash is of the block's whitespace-normalised text: edit a block and it drops out\n" +
            "# until this is regenerated. The file/line are for reading, not for matching.\n" +
            "#\n" +
            "#   CUFET_DOC_BASELINE=1 dotnet test --filter DocBlockTests\n" +
            "#\n" +
            string.Join("\n", rows) + "\n");
    }
}
