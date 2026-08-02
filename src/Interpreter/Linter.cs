using Cufet.Lexer;

namespace Cufet.Interpreter;

// Style warnings — legal code that reads worse than it needs to.
//
// Deliberately a separate pass from the checker, and deliberately incapable of producing an error.
// The checker answers "will this run"; this answers "is this how you would want to have written
// it", and the second question has no right to stop the first. Everything here is advice, and
// advice that cannot be ignored is not advice.
//
// Each rule owes an explanation of what to do instead, not just what is wrong — a warning that
// only names a fault makes the reader do the work twice.
public static class Linter
{
    public static IReadOnlyList<Diagnostic> Lint(
        IReadOnlyList<Token> tokens, IReadOnlyList<(int Line, int Column, bool KeywordLed)> statementStarts)
    {
        var bag = new DiagnosticBag();
        CapitaliseTheStartOfALine(bag, tokens, statementStarts);
        return bag.Items;
    }

    // ── Start a line with a capital letter ────────────────────────────────────
    //
    // A Cufet statement reads as a sentence, and a sentence opens with a capital. Keywords are
    // case-insensitive, so `for each x in xs, repeat:` and `For each …` are the same program —
    // which is exactly why this is the linter's business and not the parser's.
    //
    // ★ Only the half that needs no judgement. A line opening with a KEYWORD can always be
    // capitalised, and the fix is to capitalise that word — nothing else changes and no reading is
    // at stake. A line opening with a variable's own name is left alone: capitalising it would
    // rename it, so the only way to satisfy the rule there is to insert an article, and whether
    // `The total becomes 5.` reads better than `total becomes 5.` is a judgement this pass cannot
    // make. That half of the rule is deliberately not implemented rather than implemented badly.
    private static void CapitaliseTheStartOfALine(
        DiagnosticBag bag, IReadOnlyList<Token> tokens,
        IReadOnlyList<(int Line, int Column, bool KeywordLed)> statementStarts)
    {
        // The leftmost token on each line. A statement that begins further right on a line someone
        // else already opened is not what the rule is about — `If x is 1, state "one".` opens with
        // `If`, and the inline `state` is mid-sentence.
        var lineOpener = new Dictionary<int, int>();
        foreach (var t in tokens)
        {
            if (t.Type == TokenType.Eof) continue;
            if (!lineOpener.TryGetValue(t.Line, out int col) || t.Column < col)
                lineOpener[t.Line] = t.Column;
        }

        var byPosition = new Dictionary<(int, int), Token>();
        foreach (var t in tokens) byPosition[(t.Line, t.Column)] = t;

        var reported = new HashSet<(int, int)>();
        foreach (var (line, column, keywordLed) in statementStarts)
        {
            // A name is not capitalisable — an identifier must start lowercase, so the capital
            // could only come from an article, and that is the judgement half of the rule. The
            // parser decides this, because it is contextual: `output 7.` opens with a keyword and
            // `output becomes 10.` opens with a variable that happens to share the spelling.
            // Suggesting a capital on the second would not improve it, it would break it.
            if (!keywordLed) continue;
            if (!lineOpener.TryGetValue(line, out int opener) || opener != column) continue;
            if (!byPosition.TryGetValue((line, column), out var tok)) continue;
            if (!reported.Add((line, column))) continue;
            if (tok.Lexeme.Length == 0 || !char.IsLower(tok.Lexeme[0])) continue;

            string capitalised = char.ToUpperInvariant(tok.Lexeme[0]) + tok.Lexeme[1..];
            bag.Warn(
                $"this line opens with '{tok.Lexeme}' — write '{capitalised}'. A statement reads as a " +
                $"sentence, and a sentence starts with a capital. Keywords are case-insensitive, so " +
                $"this changes nothing but how the line reads.",
                line, column);
        }
    }
}
