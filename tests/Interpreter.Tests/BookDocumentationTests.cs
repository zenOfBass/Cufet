using System.Text.RegularExpressions;
using Cufet.Interpreter;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// `docs/BOOKS.md` and the bundled books agree about what those books hand out.
/// </summary>
/// <remarks>
/// <para>
/// ★★ **Two places telling one story means one is already lying.** A book's members are written
/// down twice — once in `docs/BOOKS.md`, once as `///` in the book's own source — and nothing made
/// them agree. This is the self-verifying move the doc fence tags already make: the docs are
/// checked by running them, rather than by somebody remembering to update them.
/// </para>
/// <para>
/// ⚠ MEASURED 2026-09-16, before this existed: the two places are NOT duplicates, and the ROADMAP's
/// claim that they "say the same things" was wrong. BOOKS.md is an INVENTORY plus book-level
/// behaviour (*"Each yields void for an empty series"*); the `///` carries per-member semantics
/// (*"`-2.5` floors to `-3`, not `-2`"*) that appear nowhere in BOOKS.md. They are complementary,
/// so there is nothing to generate away. The only real staleness is the INVENTORY — which is
/// exactly, and only, what these tests pin.
/// </para>
/// <para>
/// ★ The public surface is small and was measured at the same time: 13 Cufet-layer members
/// (`math` 8, `collections` 5), `math`'s `pi` and `e`, and `blueprints`' native `checksum`.
/// Sixteen. ⚠ The other 13 `Bind`s in the prelude files are free helpers, not members — measured
/// by asking for one: *"book 'blueprints' has no member 'bp-stamp'"*.
/// </para>
/// </remarks>
public class BookDocumentationTests
{
    private static readonly string BooksDoc = ReadBooksDoc();

    private static string ReadBooksDoc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cufet.sln")))
            dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "docs", "BOOKS.md"));
    }

    /// <summary>The books a reader can pull for MEMBERS — a language book offers none by design.</summary>
    /// <remarks>
    /// ⚠ Read off the book table rather than a hand-kept list, so a book added later is covered
    /// without anyone remembering this file. A LANGUAGE book is excluded by the same table that
    /// defines it: it admits axioms rather than handing out members.
    /// </remarks>
    private static IEnumerable<string> MemberBearingBooks() =>
        TypeChecker.BundledBooks.Keys
            .Where(name => !TypeChecker.LanguageBookNamesForBooks().Contains(name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal);

    /// <summary>Everything `‹book›'s ‹name›` can reach: the Cufet layer, plus the native members.</summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ READ FROM THE PRELUDE SOURCE, PARSED AND NOT CHECKED, and that is not a shortcut. The
    /// first version of this asked a CHECKED program, and `collections`' `unique` vanished from it:
    /// a function with a blank in its signature is emitted once PER FILLING, so **the template is
    /// dropped before either backend runs**. A program that calls nothing produces no filling, and
    /// a real, working, documented member simply was not there. Parsing answers what the book
    /// DECLARES, which is what a reader is promised.
    /// </para>
    /// <para>
    /// ★ It descends with `FlattenHoistable` rather than reading the top level, so a book whose
    /// body sits inside a `Pull … Done.` is read the same way the rest of the language reads it.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> PublicMembersOf(string book)
    {
        var source = TypeChecker.PreludeSources
            .FirstOrDefault(p => string.Equals(p.Book, book, StringComparison.OrdinalIgnoreCase))
            .Source;

        var names = new List<string>();
        if (source is not null)
        {
            var parsed = new Parser(new CufetLexer(source).Tokenize()).Parse();
            foreach (var statement in TypeChecker.FlattenHoistable(parsed.Statements))
                if (statement is ObjectDefinition od
                    && string.Equals(od.Name, book, StringComparison.OrdinalIgnoreCase))
                {
                    names.AddRange(od.Methods.Select(m => m.Name));
                    names.AddRange(od.Getters.Select(g => g.Name));
                }
        }

        names.AddRange(TypeChecker.BundledBooks[book].Members.Select(m => m.MemberName));
        return names;
    }

    /// <summary>Every word BOOKS.md sets in `code style`, inline or in a fenced block.</summary>
    /// <remarks>
    /// <para>
    /// ⚠ Word-inside-a-span, not the whole span. MEASURED: `collections`' `transpose` is documented
    /// as `` `collections's transpose of (m)` `` — a member named inside a bigger span — so looking
    /// for a bare `` `transpose` `` reported a documented member as missing. A member counts as
    /// named wherever the prose actually names it.
    /// </para>
    /// <para>
    /// ⚠⚠ FENCED BLOCKS ARE TAKEN FIRST AND THEN REMOVED, and that is load-bearing rather than
    /// tidy. A ``` fence is a RUN of backticks, so pairing inline spans across one shifts every
    /// span after it by one delimiter — measured, `round` and `pi` resolved while `floor`, `exp`
    /// and `power` did not, from a single doc whose text names all five identically. An
    /// alternation bug that reports some of a list and not the rest is the kind that reads as a
    /// real finding.
    /// </para>
    /// <para>
    /// ★ A fenced sample counts as naming a member, deliberately: `State collections's unique of
    /// (scores).` is how BOOKS.md introduces `unique`, and a worked example is documentation.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> CodeWords = BuildCodeWords();

    private static HashSet<string> BuildCodeWords()
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Harvest(string text)
        {
            foreach (Match word in Regex.Matches(text, "[A-Za-z][A-Za-z0-9-]*"))
                words.Add(word.Value);
        }

        // ⚠ `\r?\n`: this reads the file verbatim, where a text-mode reader would have normalised
        // the line endings for us. The repo's endings are mixed, so assuming either one is a bug.
        var fence = new Regex(@"```[A-Za-z-]*\r?\n(.*?)```", RegexOptions.Singleline);
        foreach (Match block in fence.Matches(BooksDoc)) Harvest(block.Groups[1].Value);

        // ⚠ `[^`\r\n]+` keeps a span on one line, so an unmatched backtick cannot swallow a
        // paragraph and quietly widen what counts as documented.
        foreach (Match span in Regex.Matches(fence.Replace(BooksDoc, "\n"), "`([^`\r\n]+)`"))
            Harvest(span.Groups[1].Value);

        return words;
    }

    // ── Every member a book hands out is named in BOOKS.md ───────────────────
    //
    // ★ This is the direction that catches the real mistake: a member ADDED or RENAMED while the
    // prose stays as it was. A renamed member fails here because its new name appears nowhere.

    [Fact]
    public void EveryPublicMemberOfEveryBundledBook_IsNamedInBooksDoc()
    {
        var missing = new List<string>();

        foreach (var book in MemberBearingBooks())
            foreach (var member in PublicMembersOf(book))
                if (!CodeWords.Contains(member))
                    missing.Add($"{book}'s {member}");

        Assert.True(missing.Count == 0,
            "These members exist but are named nowhere in docs/BOOKS.md: " + string.Join(", ", missing));
    }

    // ── And every member BOOKS.md claims a book provides still exists ────────
    //
    // ⚠ Scoped to the `‹book› provides …` SENTENCE, and to SINGLE-WORD backticked names inside it.
    // Both limits are measured rather than stylistic: `collections`' next sentence names the
    // `matrix`, `set` and `chase` TYPES, which are not members; and `chance`'s own sentence offers
    // `Seed the chance with ‹n›.`, a statement form rather than a member, which the single-word
    // rule excludes without needing `chance` to be special-cased.

    [Theory]
    [InlineData("math")]
    [InlineData("collections")]
    [InlineData("chance")]
    public void EveryMemberThatBooksDocClaims_StillExists(string book)
    {
        var sentence = ProvidesSentence(book);
        Assert.NotNull(sentence);

        var members = PublicMembersOf(book);
        var claimed = Regex.Matches(sentence!, "`([A-Za-z][A-Za-z0-9-]*)`")
                           .Select(m => m.Groups[1].Value)
                           .Where(name => !string.Equals(name, book, StringComparison.OrdinalIgnoreCase))
                           .ToList();

        var gone = claimed
            .Where(name => !members.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(gone.Count == 0,
            $"docs/BOOKS.md says '{book}' provides these, and it does not: " + string.Join(", ", gone));
    }

    /// <summary>The `‹book› provides …` sentence, ending at the first `.` OUTSIDE a backtick span.</summary>
    /// <remarks>
    /// ⚠ Outside a span, because `chance`'s sentence ends with a period INSIDE one —
    /// `Seed the chance with ‹n›.` — and splitting on the first `.` would cut the sentence in half
    /// and change what the test reads.
    /// </remarks>
    private static string? ProvidesSentence(string book)
    {
        var start = BooksDoc.IndexOf($"`{book}` provides", StringComparison.Ordinal);
        if (start < 0) return null;

        bool inSpan = false;
        for (int i = start; i < BooksDoc.Length; i++)
        {
            if (BooksDoc[i] == '`') inSpan = !inSpan;
            if (BooksDoc[i] == '.' && !inSpan) return BooksDoc[start..i];
        }
        return BooksDoc[start..];
    }
}
