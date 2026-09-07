namespace Cufet.Lexer;

/// <summary>Which spelling a comment was written with.</summary>
public enum CommentKind
{
    /// <summary><c>// to the end of the line</c>.</summary>
    Line,

    /// <summary><c>/* … */</c>, which nests.</summary>
    Block,

    /// <summary><c>/// to the end of the line</c> — documentation.</summary>
    /// <remarks>
    /// ⚠ FOUR or more slashes is an ordinary <see cref="Line"/> comment, not this. A row of
    /// slashes is a divider somebody drew, and a divider silently becoming public documentation is
    /// the one way this marker can go wrong. C# and Rust both carve the same hole.
    /// </remarks>
    DocLine,

    /// <summary><c>/** … */</c> — documentation, and it nests exactly as <see cref="Block"/> does.</summary>
    /// <remarks>
    /// ⚠ <c>/**/</c> is an EMPTY ORDINARY comment, not a doc comment that never closes. Telling
    /// them apart needs one character of lookahead past the second star.
    /// </remarks>
    DocBlock,
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
/// ★★ THE ONE EXCEPTION, and it is a marker rather than presentation: a <see cref="CommentKind.DocBlock"/>
/// drops the leading <c>*</c> that the Javadoc habit puts at the start of each continuation line.
/// A doc comment's content is MARKDOWN, and <c>  * more text</c> is a bullet list item there — so
/// leaving it would not be preserving what the author wrote, it would be rendering a paragraph as
/// a list. Whitespace, one <c>*</c>, and at most one space after it; everything past that is kept,
/// so indentation the author meant (a nested list, an indented code block) survives.
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
