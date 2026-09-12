namespace Cufet.Interpreter;

/// <summary>
/// Reads the text inside `Define regex <name> as [ … ]` and says what it means, or why it cannot.
/// </summary>
/// <remarks>
/// <para>
/// ★★ This is the whole reason the pattern is written in brackets rather than handed over as text.
/// The pattern is fixed where it is written, so it can be READ AT CHECK TIME — a malformed one is a
/// static error naming the fault, not a failure when the line finally runs. An ordinary function
/// taking `text` could never promise that, because its argument is not known until it has one.
/// </para>
/// <para>
/// ⚠ It is also why nothing here parses a pattern built at run time: there is no such pattern. A
/// value is spliced in as a VALUE, never concatenated into the source, which is the same guarantee
/// `run "grep" with arguments (…)` makes and the same reason injection is structurally impossible
/// rather than merely discouraged.
/// </para>
/// <para>
/// ★ SLICE 1 IS LITERALS ONLY, and every metacharacter is refused BY NAME rather than ignored. That
/// ordering is deliberate: a refusal that says "not supported yet" becomes support later, and a
/// program written against it keeps meaning what it meant. Treating `a*` as three literal
/// characters would be the other kind of decision — one that silently changes meaning the day
/// quantifiers land.
/// </para>
/// </remarks>
internal static class RegexPattern
{
    /// <summary>What the language will eventually spell; refused by name until each one lands.</summary>
    /// <remarks>
    /// ⚠ `]` and `[` are absent on purpose — they are the AXIOM delimiter, and the lexer resolves
    /// them before this ever runs. A pattern reaches here having already been counted; an
    /// unescaped `[` inside one simply opened a nested bracket pair and is part of the text.
    /// </remarks>
    private static readonly Dictionary<char, string> NotYet = new()
    {
        ['*'] = "a repeat",
        ['+'] = "a repeat",
        ['?'] = "an option",
        ['|'] = "an alternative",
        ['('] = "a group",
        [')'] = "a group",
        ['['] = "a character class",
        [']'] = "a character class",
        ['.'] = "any character",
        ['^'] = "a start anchor",
        ['$'] = "an end anchor",
        ['{'] = "a count",
        ['}'] = "a count",
    };

    /// <summary>
    /// The literal text this pattern matches. Throws with a reader's explanation if it is not a
    /// pattern this slice can read.
    /// </summary>
    /// <param name="source">The text between the brackets, exactly as written.</param>
    /// <param name="fail">
    /// How to report a fault — supplied by the caller so the message arrives in the checker's own
    /// voice, positioned on the declaration, rather than as an exception this file invents a shape
    /// for.
    /// </param>
    public static string LiteralOf(string source, Func<string, string, string, Exception> fail)
    {
        var text = new System.Text.StringBuilder();

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];

            if (c == '\\')
            {
                // ⚠⚠ UNREACHABLE FROM SOURCE, and kept anyway — say which, because a guard whose
                // status nobody wrote down becomes a guard nobody dares remove. A trailing
                // backslash escapes the closing bracket, so the axiom never closes and the LEXER
                // reports it first: MEASURED, `[abc\]` gives "unterminated foreign source". No
                // pattern can arrive here ending in a backslash.
                //
                // ★ It stays because without it `source[++i]` below walks off the end — a crash
                // instead of a sentence — and because this function should be total on its own
                // terms rather than on a caller's promise.
                if (i + 1 >= source.Length)
                    throw fail(
                        "this pattern ends in a backslash, which escapes nothing",
                        "a backslash makes the character after it ordinary, so there has to be one",
                        "write '\\\\' for a backslash you meant literally");

                char next = source[++i];
                // Every metacharacter is escapable, and so is the backslash. Nothing else is:
                // an unknown escape is refused rather than silently meaning the bare character,
                // because `\d` will mean digits one day and quietly meaning 'd' until then is
                // exactly the change-in-silence this refuses.
                if (next != '\\' && !NotYet.ContainsKey(next))
                    throw fail(
                        $"'\\{next}' is not an escape this pattern understands",
                        "a backslash may make a metacharacter ordinary, or stand for a backslash",
                        $"write '{next}' on its own if you meant the character");

                text.Append(next);
                continue;
            }

            if (NotYet.TryGetValue(c, out var meaning))
                throw fail(
                    $"'{c}' means {meaning}, which patterns cannot do yet",
                    "this version reads patterns of ordinary characters only",
                    $"write '\\{c}' if you meant the character itself");

            text.Append(c);
        }

        // ⚠ An EMPTY pattern is refused rather than matching everywhere. It is far more likely to
        // be a typo than an intention, and the thing it would otherwise do — succeed against every
        // subject, including the empty one — is the least useful answer available.
        if (text.Length == 0)
            throw fail(
                "this pattern is empty",
                "a pattern with nothing in it would match everywhere, which is never what was meant",
                "write the text the pattern should find");

        return text.ToString();
    }
}
