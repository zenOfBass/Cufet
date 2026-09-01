namespace Cufet.Lexer;

/// <summary>Which spelling a comment was written with.</summary>
public enum CommentKind
{
    /// <summary><c>// to the end of the line</c>.</summary>
    Line,

    /// <summary><c>/* … */</c>, which nests.</summary>
    Block,
}

/// <summary>
/// One comment, kept rather than discarded, and carried on the token that follows it.
/// </summary>
/// <remarks>
/// <para>
/// ★★ Comments used to be eaten inside <c>SkipWhitespace</c> and never became anything, so nothing
/// downstream could see one. That is a fine answer for a compiler and the wrong one for a language
/// with an editor: what a reader most wants when they meet an unfamiliar name is the sentence its
/// author wrote above it, and that sentence was being thrown away before the parser ever ran.
/// </para>
/// <para>
/// ★ Carried as TRIVIA rather than as tokens, and that is the load-bearing decision. A comment
/// token would have to be skipped at every one of the parser's several hundred <c>SkipNoise</c>
/// sites, and a single missed one is a parse error in a program whose only crime is a comment in an
/// unusual place. Riding on the next token instead means the parser cannot notice this change at
/// all.
/// </para>
/// <para>
/// ⚠ <see cref="Text"/> is the comment's INSIDE — the markers removed, nothing else touched. A
/// block comment keeps its line breaks and its indentation, because how to present those is the
/// reader's question and not the lexer's, and stripping them here would be a decision nothing
/// downstream could undo.
/// </para>
/// <para>
/// ⚠ A comment written after code on the same line attaches to the token that FOLLOWS it, which is
/// usually on the next line. Leading trivia is what a doc comment needs and what hover reads;
/// trailing trivia is a separate idea, and nothing wants it yet.
/// </para>
/// </remarks>
/// <param name="Kind">Which spelling was used.</param>
/// <param name="Text">The comment's inside, with the markers removed.</param>
/// <param name="Line">1-based line the opening marker sits on.</param>
/// <param name="Column">1-based column of the opening marker's first character.</param>
public sealed record Comment(CommentKind Kind, string Text, int Line, int Column)
{
    public override string ToString() =>
        $"[{Kind} comment L{Line}:{Column} {Text.Length} chars]";
}
