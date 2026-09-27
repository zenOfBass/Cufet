using CufetLexer = Cufet.Lexer.Lexer;

namespace Cufet.Compiler.Tests;

/// <summary>Broken programs, made from working ones, for comparing two parsers' refusals.</summary>
/// <remarks>
/// <para>
/// ★ A hand-written list of broken sources reaches the refusals someone thought to write down. These
/// reach the ones the corpus can reach: a file CUT OFF at a token finds every "the file ended
/// before…" and every expectation of a closer, and a file with ONE TOKEN DELETED finds almost every
/// `Consume` a statement makes. Both parsers must give the same first refusal — same words, same place.
/// </para>
/// <para>
/// ⚠ Positions are chosen EVENLY across the file's tokens rather than at random, so a failure names
/// a mutant that is the same on the next run. A token is only deleted where its text stands verbatim
/// in the source — a piece of text with holes, or the inside of an axiom, is not one token's text.
/// </para>
/// </remarks>
public static class SourceMutations
{
    public static IEnumerable<(string Suffix, string Source)> Of(string source, int each)
    {
        IReadOnlyList<Cufet.Lexer.Token> tokens;
        try { tokens = new CufetLexer(source).Tokenize(); }
        catch (Cufet.Lexer.LexerException) { yield break; }

        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < source.Length; i++)
            if (source[i] == '\n') lineStarts.Add(i + 1);
        int OffsetOf(Cufet.Lexer.Token t) => lineStarts[t.Line - 1] + t.Column - 1;

        var real = tokens.Where(t => t.Type != Cufet.Lexer.TokenType.Eof).ToList();
        if (real.Count == 0) yield break;

        int count = Math.Min(each, real.Count);
        for (int k = 0; k < count; k++)
        {
            // Never the first token: a file cut before anything is empty, which both accept.
            int index = 1 + (int)((long)k * (real.Count - 1) / count);
            if (index >= real.Count) continue;
            var t = real[index];
            int at = OffsetOf(t);
            yield return ($"cut{k}", source[..at]);

            if (at + t.Lexeme.Length <= source.Length
                && t.Lexeme.Length > 0
                && string.CompareOrdinal(source, at, t.Lexeme, 0, t.Lexeme.Length) == 0)
                yield return ($"del{k}", source[..at] + source[(at + t.Lexeme.Length)..]);
        }
    }
}
