using System.Text.RegularExpressions;
using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

/// <summary>
/// Every code block in the docs, held to what its fence says it is.
/// </summary>
/// <remarks>
/// <para>
/// ★ A documented sample that no longer runs is worse than no sample: a reader copies it, it
/// fails, and they conclude the language is broken rather than the doc. Executing samples has
/// found stale claims by the dozen; reading them has found almost none.
/// </para>
/// <para>
/// ★★ Each fence declares its own promise, and this holds it to exactly that one:
/// </para>
/// <list type="bullet">
/// <item><c>```cufet</c> — a program. It must check clean.</item>
/// <item><c>```cufet-fragment</c> — an illustration. It must PARSE, or fail only by running out
/// of input. The docs are full of statement heads (<c>If x is 5:</c>) and phrase catalogues
/// (<c>nums sorted</c>) that teach a shape; completing them would bury the point. But a fragment
/// that stops parsing for any OTHER reason has gone stale, and that is caught.</item>
/// <item><c>```cufet-refused</c> — a counter-example. It must STAY refused. ⚠ This is the one
/// nothing else could ever check: a counter-example that quietly starts working means the language
/// moved under the doc, and it looks exactly like a counter-example that still fails.</item>
/// <item><c>```output</c> — what the block above it prints. Not asserted yet.</item>
/// </list>
/// <para>
/// ⚠ This REPLACED a hash baseline — a recorded list of blocks that happened to pass on some past
/// day. That had two holes this does not: editing a block changed its hash, so it silently left
/// coverage until someone regenerated the file; and it could not judge the ~155 blocks that fail,
/// because it had no way to tell a deliberate counter-example from a broken sample. Seven doc bugs
/// were sitting in that unjudged pile, all of them "a missing article inside an interpolation".
/// </para>
/// <para>
/// ⚠ The fences are proposed by <c>tools/doc-tag.py</c> and corrected by hand. That tool only
/// claims <c>cufet-refused</c> where a person wrote it in the annotation — a wrong `refused` is
/// silent, a wrong `fragment` is loud, so it guesses only toward the one that announces itself.
/// </para>
/// </remarks>
public class DocBlockTests
{
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

    private sealed record Block(string File, int Line, string Tag, string Source, string Head);

    // Docs annotate samples with a trailing arrow to show a value or a note. That is prose, not
    // code — and it points BOTH ways, which is easy to miss: handling only ← leaves every → sample
    // failing to lex.
    private static readonly Regex Annotation = new(@"\s*[←→].*$", RegexOptions.Multiline);

    // A parse failure caused by the sample simply STOPPING. See the fragment rule above.
    private static readonly Regex RanOutOfInput = new(@"got Eof|the file ended before");

    // ★ A fragment may also fail because of what is NOT around it. `Return the total.` is refused
    // outside a function and `In case of failure:` outside a `Try`, and both are perfectly good
    // illustrations of the statement they show — the docs put the enclosing block in the prose
    // rather than repeating it in every sample. This is the same allowance the tagger makes, and
    // it is narrow on purpose: only refusals that name the MISSING SURROUNDINGS, never a refusal
    // about the statement itself.
    private static readonly Regex NeedsSurroundings = new(
        @"used outside a function|requires an active rabbit|got Case ""case""|got Close ""close""");

    // ── Extraction ────────────────────────────────────────────────────────

    private static IEnumerable<Block> TaggedBlocks()
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
                var tag = lines[i].TrimStart()[3..].Trim();
                int start = i + 1;
                var body = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    body.Add(lines[i].Length >= indent && lines[i][..indent].Trim().Length == 0
                        ? lines[i][indent..] : lines[i]);
                    i++;
                }

                if (tag is not ("cufet" or "cufet-fragment" or "cufet-refused")) continue;

                var raw = string.Join("\n", body);
                var source = Annotation.Replace(raw, "");
                yield return new Block(file, start + 1, tag, source,
                                       raw.Trim().Split('\n')[0].Trim());
            }
        }
    }

    // ── What each promise means ───────────────────────────────────────────

    /// <summary>Null when the block kept its promise, else what went wrong.</summary>
    private static string? Broken(Block block)
    {
        switch (block.Tag)
        {
            case "cufet":
                try
                {
                    new TypeChecker().Check(new Parser(new CufetLexer(block.Source).Tokenize()).Parse());
                    return null;
                }
                catch (Exception e) { return First(e.Message); }

            case "cufet-fragment":
                try
                {
                    new Parser(new CufetLexer(block.Source).Tokenize()).Parse();
                    return null;   // parsed — whether it would type-check is not a fragment's promise
                }
                catch (Exception e)
                {
                    if (RanOutOfInput.IsMatch(e.Message) || NeedsSurroundings.IsMatch(e.Message))
                        return null;
                    // ★ Some illustrations are EXPRESSIONS, not statements — `nums sorted`,
                    // `the length of s`, `false and cast f on ()`. The docs list them to show a
                    // phrase, and a phrase is not a program. Giving one a home is the whole test:
                    // if it reads as a value, it is still Cufet and still current.
                    return ParsesAsAnExpression(block.Source) ? null : First(e.Message);
                }

            case "cufet-refused":
                try
                {
                    new TypeChecker().Check(new Parser(new CufetLexer(block.Source).Tokenize()).Parse());
                    return "it CHECKS CLEAN now — a counter-example that started working means "
                         + "the language moved under the doc";
                }
                catch { return null; }

            default:
                return null;
        }
    }

    /// <summary>
    /// True when every line of the block reads as a VALUE — the shape a phrase catalogue has.
    /// </summary>
    /// <remarks>
    /// ⚠ Each line is given its own home rather than the block as a whole, because these blocks
    /// are LISTS: `nums sorted`, then `nums sorted by the age`, then `nums in reverse`, three
    /// alternatives one under the other. Wrapping the lot would ask the parser to read them as one
    /// expression, which they are not.
    /// </remarks>
    private static bool ParsesAsAnExpression(string source)
    {
        var lines = source.Split('\n')
                          .Select(l => l.Trim())
                          .Where(l => l.Length > 0 && !l.StartsWith("//"))
                          .ToList();
        if (lines.Count == 0) return false;

        foreach (var line in lines)
        {
            // Trailing `//` note, and a trailing `.` if the phrase happened to carry one.
            var phrase = Regex.Replace(line, @"\s*//.*$", "").TrimEnd('.').Trim();
            if (phrase.Length == 0) continue;
            try
            {
                new Parser(new CufetLexer($"Define doc-probe as {phrase}.").Tokenize()).Parse();
            }
            catch { return false; }
        }
        return true;
    }

    private static string First(string message) =>
        message.Replace("\r\n", "\n").Split('\n')[0].Trim();

    // ── The test ──────────────────────────────────────────────────────────

    [Fact]
    public void EveryDocBlock_KeepsThePromiseItsFenceMakes()
    {
        var blocks = TaggedBlocks().ToList();

        // A checker that stops finding its corpus reports perfect health. Guard it the same way
        // the example and soundness suites do.
        Assert.True(blocks.Count >= 250,
            $"only {blocks.Count} tagged doc blocks found — extraction broke, or the tags were lost.");

        var broken = blocks
            .Select(b => (Block: b, Why: Broken(b)))
            .Where(x => x.Why is not null)
            .Select(x => $"  [{x.Block.Tag}] {x.Block.File}:{x.Block.Line}\n" +
                         $"      {x.Block.Head}\n" +
                         $"      → {x.Why}")
            .ToList();

        Assert.True(broken.Count == 0,
            $"{broken.Count} documented sample(s) no longer keep their fence's promise:\n\n" +
            string.Join("\n\n", broken.Take(25)) +
            (broken.Count > 25 ? $"\n\n  … and {broken.Count - 25} more" : "") +
            "\n\nEither the doc is wrong, or its fence tag is. `tools/doc-tag.py` proposes tags;\n" +
            "the tag is a claim about what the block is FOR, so a person decides it.");
    }
}
