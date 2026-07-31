namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Text operations (Slice 1) ─────────────────────────────────────────────

    private CufetType InferTextJoin(TextJoin tj)
    {
        var left  = InferType(tj.Left);
        var right = InferType(tj.Right);

        if (left != null && left != CufetType.Text)
            throw TypeError(
                "you can only join text to text",
                null,
                tj.Line, tj.Column,
                $"join a {FormatType(left)} to text",
                $"Convert the {FormatType(left)} first: use 'converted to text'.\nFor example: n converted to text joined to \" items\".");

        if (right != null && right != CufetType.Text)
            throw TypeError(
                "you can only join text to text",
                null,
                tj.Line, tj.Column,
                $"join text to a {FormatType(right)}",
                $"Convert the {FormatType(right)} first: use 'converted to text'.\nFor example: \"score: \" joined to n converted to text.");

        return CufetType.Text;
    }

    private CufetType InferTextConvert(TextConvert tc)
    {
        var operand = InferType(tc.Value);
        if (operand == null) return CufetType.Text;
        // A bits value converts to the text it displays as — "0xFF", prefix and all.
        if (operand == CufetType.Number || operand == CufetType.Fact || operand == CufetType.Text
            || operand == CufetType.Bits)
            return CufetType.Text;
        throw TypeError(
            $"'converted to text' doesn't work on {FormatTypePlural(operand)}",
            null,
            tc.Line, tc.Column,
            $"convert a {FormatType(operand)} to text",
            "Only numbers, facts and bits can be converted to text.");
    }

    private CufetType InferNumberConvert(NumberConvert nc)
    {
        var operand = InferType(nc.Value);

        // Bits to number is TOTAL and cannot fail: bits hold at most 64 bits, and a number's
        // mantissa is 96, so every pattern has a quantity. So this yields a plain number, not a
        // voidable one — unlike text, which may simply not be a number at all.
        if (operand == CufetType.Bits) return CufetType.Number;

        if (operand != null && operand != CufetType.Text)
            throw TypeError(
                "'converted to number' expects text or bits",
                null,
                nc.Line, nc.Column,
                $"convert a {FormatType(operand)} to number",
                "Text converts to a voidable number — void if it isn't a valid number. Bits " +
                "convert to a plain number, since every bit pattern is some quantity.");
        return new VoidableType(CufetType.Number);
    }

    // <number> converted to hex|binary|octal — the crossing from quantity to pattern.
    //
    // This is what recovers the expressiveness a display-only transform would have had: a
    // COMPUTED value can be shown in hex, not just a literal written that way.
    //
    // It RAISES rather than yielding a voidable, for the same reason arithmetic overflow does —
    // a voidable would ride in the type and force an unwrap at every crossing. The failures are
    // programming errors (a fraction, a negative, something past 64 bits), not data conditions
    // the way "this text isn't a number" is.
    private CufetType InferBitsConvert(BitsConvert bc)
    {
        var operand = InferType(bc.Target);
        if (operand != null && operand != CufetType.Number)
            throw TypeError(
                $"'converted to {BitsBaseName(bc.ToBase)}' expects a number",
                null,
                bc.Line, bc.Column,
                $"convert a {FormatType(operand)} to {BitsBaseName(bc.ToBase)}",
                operand == CufetType.Bits
                    ? "This is already a bit pattern. To show it in another base, convert it to " +
                      "a number first: 'x converted to number converted to binary'."
                    : "Only a number can be converted to a bit pattern.");
        return CufetType.Bits;
    }

    internal static string BitsBaseName(char b) => b switch
    {
        'x' => "hex", 'o' => "octal", 'b' => "binary", _ => "bits",
    };

    private CufetType InferTextLength(TextLength tl)
    {
        var operand = InferType(tl.Target);
        if (operand == null) return CufetType.Number;
        if (operand == CufetType.Text) return CufetType.Number;
        throw TypeError(
            "'the length of' works on text only",
            null,
            tl.Line, tl.Column,
            $"get the length of a {FormatType(operand)}",
            "Only text values have a character length. For series, use 'the number of series'.");
    }

    // ── Text operations (Slice 2: split, contains, find, substring) ───────────

    private CufetType InferTextSplit(TextSplit split)
    {
        var textType = InferType(split.Text);
        if (textType != null && textType != CufetType.Text)
            throw TypeError(
                "'split by' works on text only",
                null, split.Line, split.Column,
                $"split a {FormatType(textType)}",
                "Only text can be split. Convert the value to text first if needed.");

        var delimType = InferType(split.Delimiter);
        if (delimType != null && delimType != CufetType.Text)
            throw TypeError(
                "the delimiter in 'split by' must be text",
                null, split.Line, split.Column,
                $"split by a {FormatType(delimType)}",
                "Use a text value as the delimiter, e.g. \",\".");

        if (split.Delimiter is StringLiteral { Value: "" })
            throw TypeError(
                "'split by' needs a non-empty delimiter",
                null, split.Line, split.Column,
                "split by an empty piece of text",
                "Use a delimiter with at least one character.");

        return new SeriesType(CufetType.Text);
    }

    private CufetType InferTextContains(TextContains contains)
    {
        var textType = InferType(contains.Text);
        if (textType != null && textType != CufetType.Text)
            throw TypeError(
                "'contains' works on text only",
                null, contains.Line, contains.Column,
                $"check whether a {FormatType(textType)} contains something",
                "Only text values support 'contains'. Convert the value to text first if needed.");

        var subType = InferType(contains.Substring);
        if (subType != null && subType != CufetType.Text)
            throw TypeError(
                "'contains' checks for text only",
                null, contains.Line, contains.Column,
                $"check whether text contains a {FormatType(subType)}",
                "Convert the value to text first if needed.");

        return CufetType.Fact;
    }

    private CufetType InferTextFind(TextFind find)
    {
        var subType = InferType(find.Substring);
        if (subType != null && subType != CufetType.Text)
            throw TypeError(
                "'the position of ... in ...' looks for text only",
                null, find.Line, find.Column,
                $"look for a {FormatType(subType)}",
                "Convert the value to text first if needed.");

        var textType = InferType(find.Text);
        if (textType != null && textType != CufetType.Text)
            throw TypeError(
                "'the position of ... in ...' searches text only",
                null, find.Line, find.Column,
                $"search in a {FormatType(textType)}",
                "Convert the value to text first if needed.");

        return new VoidableType(CufetType.Number);
    }

    private CufetType InferTextSubstringRange(TextSubstringRange range)
    {
        var textType = InferType(range.Text);
        if (textType != null && textType != CufetType.Text)
            throw TypeError(
                "'the characters ... of' works on text only",
                null, range.Line, range.Column,
                $"take characters from a {FormatType(textType)}",
                "Only text has characters to take. Convert the value to text first if needed.");

        var fromType = InferType(range.From);
        if (fromType != null && fromType != CufetType.Number)
            throw TypeError(
                "a character position must be a number",
                null, range.Line, range.Column,
                $"use a {FormatType(fromType)} as a character position",
                "Positions are counted starting at 1, like series ordinals.");

        if (range.To != null)
        {
            var toType = InferType(range.To);
            if (toType != null && toType != CufetType.Number)
                throw TypeError(
                    "a character position must be a number",
                    null, range.Line, range.Column,
                    $"use a {FormatType(toType)} as a character position",
                    "Positions are counted starting at 1, like series ordinals.");
        }

        var literalFrom = TryGetLiteralNumber(range.From);
        if (literalFrom <= 0)
            throw TypeError(
                "a character position must be 1 or greater",
                null, range.Line, range.Column,
                $"start at position {literalFrom}",
                "Positions are counted starting at 1, like series ordinals — not 0.");

        return CufetType.Text;
    }

    private CufetType InferTextSubstringEdge(TextSubstringEdge edge)
    {
        var textType = InferType(edge.Text);
        if (textType != null && textType != CufetType.Text)
            throw TypeError(
                "'the first/last ... characters of' works on text only",
                null, edge.Line, edge.Column,
                $"take characters from a {FormatType(textType)}",
                "Only text has characters to take. Convert the value to text first if needed.");

        var countType = InferType(edge.Count);
        if (countType != null && countType != CufetType.Number)
            throw TypeError(
                "a character count must be a number",
                null, edge.Line, edge.Column,
                $"use a {FormatType(countType)} as a character count",
                "Use a number of characters, e.g. 'the first 3 characters of greeting'.");

        return CufetType.Text;
    }

    // ── Text operations (Slice 3: replace, case, trim) ────────────────────────

    private CufetType InferTextReplace(TextReplace tr)
    {
        var textType = InferType(tr.Text);
        if (textType != null && textType != CufetType.Text)
            throw TypeError(
                "'replace ... with ... in ...' works on text only",
                null, tr.Line, tr.Column,
                $"replace inside a {FormatType(textType)}",
                "Only text can be searched and replaced. Convert the value to text first if needed.");

        var oldType = InferType(tr.Old);
        if (oldType != null && oldType != CufetType.Text)
            throw TypeError(
                "the text being replaced must be text",
                null, tr.Line, tr.Column,
                $"replace a {FormatType(oldType)}",
                "Use a text value as the target, e.g. \"a\".");

        var newType = InferType(tr.New);
        if (newType != null && newType != CufetType.Text)
            throw TypeError(
                "the replacement must be text",
                null, tr.Line, tr.Column,
                $"replace with a {FormatType(newType)}",
                "Use a text value as the replacement, e.g. \"X\" (or \"\" to delete).");

        if (tr.Old is StringLiteral { Value: "" })
            throw TypeError(
                "'replace' needs a non-empty target",
                null, tr.Line, tr.Column,
                "replace an empty piece of text",
                "Use a target with at least one character.");

        return CufetType.Text;
    }

    private CufetType InferTextCase(TextCase tc)
    {
        var textType = InferType(tc.Text);
        if (textType != null && textType != CufetType.Text)
            throw TypeError(
                "'in uppercase'/'in lowercase' work on text only",
                null, tc.Line, tc.Column,
                $"change the case of a {FormatType(textType)}",
                "Only text has a case to change. Convert the value to text first if needed.");
        return CufetType.Text;
    }

    private CufetType InferTextTrim(TextTrim trim)
    {
        var textType = InferType(trim.Text);
        if (textType != null && textType != CufetType.Text)
            throw TypeError(
                "'trimmed' works on text only",
                null, trim.Line, trim.Column,
                $"trim a {FormatType(textType)}",
                "Only text has surrounding whitespace to trim. Convert the value to text first if needed.");
        return CufetType.Text;
    }
}
