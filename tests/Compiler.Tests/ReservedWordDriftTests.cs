using System.Text.RegularExpressions;
using Cufet.Lexer;
using Xunit;
using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

/// <summary>Pins `GRAMMAR.md` §1 against the lexer, in both directions.</summary>
/// <remarks>
/// <para>
/// ★★ The reserved set is written down in two places and nothing compared them. The audit of
/// 2026-09-23 measured the gap: <b>ten</b> reserved words were missing from §1 — the whole
/// concurrency family, `channel` among them — while five words freed on 2026-09-12 were still
/// listed as taken. §1 opens by promising it lists every word that cannot be a variable name, so
/// a reader checking it before naming a variable `channel` was told it was free. It was not.
/// </para>
/// <para>
/// ⚠ This is not the first time. <c>CHANGELOG.md:6843-6880</c> records an earlier sweep finding
/// the same shape of error: <i>"the word is reserved when the point of unreserving it was that it
/// is not."</i> A doc that drifts twice will drift again, so it gets a test rather than a fix.
/// </para>
/// <para>
/// ★ The lexer is the authority and is consulted by RUNNING it, not by trusting a list. A word is
/// reserved exactly when lexing it alone yields something other than an `Identifier` — no keyword
/// table, nothing to maintain. The one place this test reads source is to enumerate the lexer's
/// literals for the second direction; if that switch is ever reshaped, this fails loudly.
/// </para>
/// </remarks>
public class ReservedWordDriftTests
{
    private static string GrammarPath => Path.Combine(CufetBinary.RepoRoot, "docs", "GRAMMAR.md");
    private static string LexerPath   => Path.Combine(CufetBinary.RepoRoot, "src", "Lexer", "Lexer.cs");

    /// <summary>The words §1 lists in a table as reserved, excluding its contextual-words section.</summary>
    private static HashSet<string> DocumentedReserved()
    {
        var text  = File.ReadAllText(GrammarPath);
        int start = text.IndexOf("## 1. Reserved keywords", StringComparison.Ordinal);
        Assert.True(start >= 0, "GRAMMAR.md has no '## 1. Reserved keywords' section.");

        // ⚠ The contextual-words subsection is INSIDE §1 and its tables list words that are
        // deliberately NOT reserved. Reading to '## 2.' would sweep them in and invert the test.
        int end = text.IndexOf("### Contextual words", start, StringComparison.Ordinal);
        Assert.True(end > start, "GRAMMAR.md §1 has no '### Contextual words' subsection.");

        return [.. Regex.Matches(text[start..end], @"^\| `([a-z-]+)`", RegexOptions.Multiline)
                        .Select(m => m.Groups[1].Value)];
    }

    /// <summary>The lexemes the lexer's keyword switch maps to a token.</summary>
    private static HashSet<string> LexerLiterals()
    {
        var matches = Regex.Matches(File.ReadAllText(LexerPath), @"""([a-z-]+)""\s*=>\s*TokenType\.");
        Assert.True(matches.Count > 100,
            $"Only {matches.Count} keyword literals found in Lexer.cs — the switch has been reshaped " +
            "and this test is no longer reading it. Fix the pattern rather than lowering the bound.");
        return [.. matches.Select(m => m.Groups[1].Value)];
    }

    private static bool IsReserved(string word)
    {
        var tokens = new CufetLexer(word).Tokenize();
        return tokens.Count > 0 && tokens[0].Type != TokenType.Identifier;
    }

    // ── Direction 1: everything §1 calls reserved really is ───────────────
    //
    // ⚠ The costly direction is the OTHER one, but this one catches the freed-word case: a word
    // given back stays listed as taken, and the doc keeps charging for a name that is free.

    [Fact]
    public void EveryWordGrammarCallsReserved_ActuallyIs()
    {
        var stale = DocumentedReserved().Where(w => !IsReserved(w)).OrderBy(w => w).ToList();

        Assert.True(stale.Count == 0,
            "GRAMMAR.md §1 lists these as reserved, but the lexer makes them ordinary identifiers: " +
            string.Join(", ", stale) +
            ". A word that has been given back must move to the contextual-words section.");
    }

    // ── Direction 2: everything reserved is written down ──────────────────
    //
    // ★★ This is the one that matters. §1's own opening sentence promises the list is complete —
    // so a missing word is not an omission, it is the document making a false statement about a
    // name the reader is about to use.

    [Fact]
    public void EveryReservedWord_IsListedInGrammar()
    {
        var documented = DocumentedReserved();

        // `a`, `an` and `the` are articles, listed in §1 under Noise with their own explanation
        // that they are not reserved in the "forbidden as a name" sense. They lex as Article, so
        // `IsReserved` is true for them and they are genuinely in the table — nothing to exempt.
        var missing = LexerLiterals()
            .Where(w => IsReserved(w) && !documented.Contains(w))
            .OrderBy(w => w)
            .ToList();

        Assert.True(missing.Count == 0,
            "These words are reserved by the lexer but appear nowhere in GRAMMAR.md §1: " +
            string.Join(", ", missing) +
            ". §1 promises to list every word that cannot be a variable name, so each of these is " +
            "a name the document currently tells a reader is free.");
    }
}
