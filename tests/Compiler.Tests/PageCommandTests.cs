using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Cufet.Compiler.Tests;

/// <summary>
/// `cufet page` — what a reader gets when they pull a book somebody else wrote.
/// </summary>
/// <remarks>
/// <para>
/// ★★ **Cheap because the language already did the work.** A Cufet signature is ENGLISH, so a
/// page's declaration line is the declaration copied out — there is no rendering of types into
/// prose, which is most of what a documentation generator usually is. And a book is an object, so
/// "what is in it" is a member list the checker already holds.
/// </para>
/// <para>
/// ⚠⚠ **The assertions here insist on the PROSE, not just the headings**, and that is the whole
/// lesson of building it. The first version joined the AST's position to the semantic walk's on
/// LINE AND COLUMN — and an AST node's position is where its KEYWORD starts while the token is on
/// the NAME, fourteen characters along in `Define object math`. Nothing matched, and the failure
/// was SILENT: every page came out as a perfectly correct member list with no documentation on it
/// at all. A test that checked structure would have passed.
/// </para>
/// </remarks>
public class PageCommandTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cufet.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? "";
    }

    private static string CufetExe => Path.Combine(
        RepoRoot, "src", "App", "bin", "Debug", "net10.0",
        "Cufet.App" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : ""));

    private readonly List<string> _written = [];

    public void Dispose()
    {
        foreach (var f in _written)
            try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { }
    }

    private string WriteBook(string body)
    {
        string path = Path.Combine(TestScratch.Root, "page-" + Guid.NewGuid().ToString("N") + ".cufe");
        File.WriteAllText(path, body);
        _written.Add(path);
        return path;
    }

    private static (int Exit, string Out, string Err) Page(string path)
    {
        var psi = new ProcessStartInfo(CufetExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = RepoRoot,
        };
        psi.ArgumentList.Add("page");
        psi.ArgumentList.Add(path);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, stdout.Replace("\r\n", "\n"), stderr.Replace("\r\n", "\n"));
    }

    private const string DocumentedBook = """
        /// Money, counted in whole pennies.
        ///
        /// Rounds HALF UP, so 2.5 becomes 3.
        Define object till with () and book:
            /// Everything added together, to the nearest penny.
            Bind number to total, given (the series of number amounts):
                Return 0.
            Done.

            /// The name this till answers to.
            Get label as text, "front counter".
        Done.
        """;

    [Fact]
    public void ThePage_NamesTheBookAndHowToPullIt()
    {
        var (exit, page, err) = Page(WriteBook(DocumentedBook));

        Assert.True(exit == 0, err);
        Assert.StartsWith("# till\n", page);
        Assert.Contains("Pull a book on till.", page);
    }

    /// <remarks>
    /// ⚠⚠ THE ASSERTION THAT MATTERS. Structure without prose is the exact shape the first
    /// implementation produced, and it looked entirely correct.
    /// </remarks>
    [Fact]
    public void ThePage_CarriesTheDocumentation_NotJustTheMemberNames()
    {
        var (_, page, _) = Page(WriteBook(DocumentedBook));

        // The book's own.
        Assert.Contains("Money, counted in whole pennies.", page);
        Assert.Contains("Rounds HALF UP, so 2.5 becomes 3.", page);
        // And each member's.
        Assert.Contains("Everything added together, to the nearest penny.", page);
        Assert.Contains("The name this till answers to.", page);
    }

    /// <remarks>★ A GETTER IS SURFACE TOO. `the label of till` is reached the same way a method is,
    /// so leaving getters out would make a page quietly incomplete — worse than no page.</remarks>
    [Fact]
    public void ThePage_ListsMethodsAndGetters_WithTheDeclarationAsWritten()
    {
        var (_, page, _) = Page(WriteBook(DocumentedBook));

        Assert.Contains("## total\n", page);
        Assert.Contains("## label\n", page);
        // ★ The declaration is COPIED, not rendered: a Cufet signature is already English.
        Assert.Contains("Bind number to total, given (the series of number amounts):", page);
        Assert.Contains("Get label as text, \"front counter\".", page);
    }

    /// <remarks>★ A book with nothing in it is `chance`'s shape — pulling it IS the whole offer —
    /// and a page has to say that rather than trailing off after the title.</remarks>
    [Fact]
    public void ABookWithNoMembers_SaysSoRatherThanStoppingShort()
    {
        var (exit, page, _) = Page(WriteBook("""
            /// Randomness you can ask for.
            Define object luck with () and book.
            """));

        Assert.Equal(0, exit);
        Assert.Contains("no members", page);
        Assert.Contains("Randomness you can ask for.", page);
    }

    /// <remarks>⚠ A page is for a BOOK. A file of ordinary objects has no reader-facing surface to
    /// describe, and saying so beats printing a title with nothing under it.</remarks>
    [Fact]
    public void AFileWithNoBookInIt_IsRefusedWithWhatIsNeeded()
    {
        var (exit, _, err) = Page(WriteBook("""
            Define object crate with (the number size-of-it):
                Bind number to doubled: Return 2. Done.
            Done.
            """));

        Assert.NotEqual(0, exit);
        Assert.Contains("no book in", err);
        Assert.Contains("and book:", err);
    }

    /// <remarks>
    /// ⚠⚠ A BUNDLED BOOK IS NOT "A FILE WITH NO BOOK IN IT", and saying so was a lie about the one
    /// file that most obviously is one. `blueprints` and `regex` carry no `Define object` at all —
    /// blueprints deliberately, because a Cufet layer would put its native `checksum` out of reach —
    /// so the search finds nothing and the generic refusal read as nonsense.
    ///
    /// ★ The message gives the TRUE reason rather than a policy. An earlier wording said pages were
    /// for books somebody wrote and published, which `cufet page math.cufe` immediately contradicts:
    /// math is bundled and pages perfectly well. What is actually missing here is a Cufet
    /// declaration to read.
    /// </remarks>
    [Fact]
    public void ABundledBookWithNoCufetLayer_SaysWhyRatherThanDenyingItIsABook()
    {
        string blueprints = Path.Combine(
            RepoRoot, "src", "Interpreter", "Prelude", "blueprints.cufe");
        var (exit, _, err) = Page(blueprints);

        Assert.NotEqual(0, exit);
        Assert.Contains("blueprints", err);
        Assert.Contains("declares no members in Cufet", err);
        Assert.Contains("BOOKS.md", err);
        // ⚠ The thing it must NOT say about the blueprints book.
        Assert.DoesNotContain("there is no book in", err);
    }

    /// <remarks>
    /// ★★ AGAINST A BOOK NOBODY WROTE FOR THIS TEST. `math` is the largest documented book that
    /// ships, and it exercises what a fixture cannot: getters and methods interleaved, a book-level
    /// comment of several paragraphs, and members whose docs carry their own markup.
    /// </remarks>
    [Fact]
    public void ARealBundledBook_ProducesAPageWithProseOnEveryDocumentedMember()
    {
        string math = Path.Combine(RepoRoot, "src", "Interpreter", "Prelude", "math.cufe");
        var (exit, page, err) = Page(math);

        Assert.True(exit == 0, err);
        Assert.StartsWith("# math\n", page);
        Assert.Contains("Numbers beyond the operators", page);
        Assert.Contains("## pi\n", page);
        Assert.Contains("## square-root\n", page);

        // ⚠ Counted, not sampled. Every heading must be followed by prose somewhere before the next
        // one — a page that documents its first member and quietly drops the rest is the failure
        // this whole file is shaped around.
        var sections = page.Split("\n## ").Skip(1).ToList();
        Assert.True(sections.Count >= 8, $"only {sections.Count} members on math's page");

        var bare = sections
            .Where(s => s.Split("```").Length >= 3 && s.Split("```")[2].Trim().Length == 0)
            .Select(s => s.Split('\n')[0])
            .ToList();
        Assert.True(bare.Count == 0,
            $"{bare.Count} of math's members came out with no prose: {string.Join(", ", bare)}");
    }
}
