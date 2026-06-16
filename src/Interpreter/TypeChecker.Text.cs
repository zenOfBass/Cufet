namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Text operations (Slice 1) ─────────────────────────────────────────────

    private CufetType InferTextJoin(TextJoin tj)
    {
        var left  = InferType(tj.Left);
        var right = InferType(tj.Right);

        if (left != null && left != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "you can only join text to text",
                null,
                tj.Line,
                $"join a {FormatType(left)} to text",
                $"Convert the {FormatType(left)} first: use 'converted to text'.\nFor example: n converted to text joined to \" items\"."));

        if (right != null && right != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "you can only join text to text",
                null,
                tj.Line,
                $"join text to a {FormatType(right)}",
                $"Convert the {FormatType(right)} first: use 'converted to text'.\nFor example: \"score: \" joined to n converted to text."));

        return CufetType.Text;
    }

    private CufetType InferTextConvert(TextConvert tc)
    {
        var operand = InferType(tc.Value);
        if (operand == null) return CufetType.Text;
        if (operand == CufetType.Number || operand == CufetType.Fact || operand == CufetType.Text)
            return CufetType.Text;
        throw new TypeException(FormatTypeError(
            $"'converted to text' doesn't work on {FormatTypePlural(operand)}",
            null,
            tc.Line,
            $"convert a {FormatType(operand)} to text",
            "Only numbers and facts can be converted to text."));
    }

    private CufetType InferNumberConvert(NumberConvert nc)
    {
        var operand = InferType(nc.Value);
        if (operand != null && operand != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "'converted to number' expects text",
                null,
                nc.Line,
                $"convert a {FormatType(operand)} to number",
                "Only text can be converted to number. The result is a voidable number — void if the text isn't a valid number."));
        return new VoidableType(CufetType.Number);
    }

    private CufetType InferTextLength(TextLength tl)
    {
        var operand = InferType(tl.Target);
        if (operand == null) return CufetType.Number;
        if (operand == CufetType.Text) return CufetType.Number;
        throw new TypeException(FormatTypeError(
            "'the length of' works on text only",
            null,
            tl.Line,
            $"get the length of a {FormatType(operand)}",
            "Only text values have a character length. For series, use 'the number of series'."));
    }

    // ── Text operations (Slice 2: split, contains, find, substring) ───────────

    private CufetType InferTextSplit(TextSplit split)
    {
        var textType = InferType(split.Text);
        if (textType != null && textType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "'split by' works on text only",
                null, split.Line,
                $"split a {FormatType(textType)}",
                "Only text can be split. Convert the value to text first if needed."));

        var delimType = InferType(split.Delimiter);
        if (delimType != null && delimType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "the delimiter in 'split by' must be text",
                null, split.Line,
                $"split by a {FormatType(delimType)}",
                "Use a text value as the delimiter, e.g. \",\"."));

        if (split.Delimiter is StringLiteral { Value: "" })
            throw new TypeException(FormatTypeError(
                "'split by' needs a non-empty delimiter",
                null, split.Line,
                "split by an empty piece of text",
                "Use a delimiter with at least one character."));

        return new SeriesType(CufetType.Text);
    }

    private CufetType InferTextContains(TextContains contains)
    {
        var textType = InferType(contains.Text);
        if (textType != null && textType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "'contains' works on text only",
                null, contains.Line,
                $"check whether a {FormatType(textType)} contains something",
                "Only text values support 'contains'. Convert the value to text first if needed."));

        var subType = InferType(contains.Substring);
        if (subType != null && subType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "'contains' checks for text only",
                null, contains.Line,
                $"check whether text contains a {FormatType(subType)}",
                "Convert the value to text first if needed."));

        return CufetType.Fact;
    }

    private CufetType InferTextFind(TextFind find)
    {
        var subType = InferType(find.Substring);
        if (subType != null && subType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "'the position of ... in ...' looks for text only",
                null, find.Line,
                $"look for a {FormatType(subType)}",
                "Convert the value to text first if needed."));

        var textType = InferType(find.Text);
        if (textType != null && textType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "'the position of ... in ...' searches text only",
                null, find.Line,
                $"search in a {FormatType(textType)}",
                "Convert the value to text first if needed."));

        return new VoidableType(CufetType.Number);
    }

    private CufetType InferTextSubstringRange(TextSubstringRange range)
    {
        var textType = InferType(range.Text);
        if (textType != null && textType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "'the characters ... of' works on text only",
                null, range.Line,
                $"take characters from a {FormatType(textType)}",
                "Only text has characters to take. Convert the value to text first if needed."));

        var fromType = InferType(range.From);
        if (fromType != null && fromType != CufetType.Number)
            throw new TypeException(FormatTypeError(
                "a character position must be a number",
                null, range.Line,
                $"use a {FormatType(fromType)} as a character position",
                "Positions are counted starting at 1, like series ordinals."));

        if (range.To != null)
        {
            var toType = InferType(range.To);
            if (toType != null && toType != CufetType.Number)
                throw new TypeException(FormatTypeError(
                    "a character position must be a number",
                    null, range.Line,
                    $"use a {FormatType(toType)} as a character position",
                    "Positions are counted starting at 1, like series ordinals."));
        }

        var literalFrom = TryGetLiteralNumber(range.From);
        if (literalFrom <= 0)
            throw new TypeException(FormatTypeError(
                "a character position must be 1 or greater",
                null, range.Line,
                $"start at position {literalFrom}",
                "Positions are counted starting at 1, like series ordinals — not 0."));

        return CufetType.Text;
    }

    private CufetType InferTextSubstringEdge(TextSubstringEdge edge)
    {
        var textType = InferType(edge.Text);
        if (textType != null && textType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "'the first/last ... characters of' works on text only",
                null, edge.Line,
                $"take characters from a {FormatType(textType)}",
                "Only text has characters to take. Convert the value to text first if needed."));

        var countType = InferType(edge.Count);
        if (countType != null && countType != CufetType.Number)
            throw new TypeException(FormatTypeError(
                "a character count must be a number",
                null, edge.Line,
                $"use a {FormatType(countType)} as a character count",
                "Use a number of characters, e.g. 'the first 3 characters of greeting'."));

        return CufetType.Text;
    }

    // ── Text operations (Slice 3: replace, case, trim) ────────────────────────

    private CufetType InferTextReplace(TextReplace tr)
    {
        var textType = InferType(tr.Text);
        if (textType != null && textType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "'replace ... with ... in ...' works on text only",
                null, tr.Line,
                $"replace inside a {FormatType(textType)}",
                "Only text can be searched and replaced. Convert the value to text first if needed."));

        var oldType = InferType(tr.Old);
        if (oldType != null && oldType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "the text being replaced must be text",
                null, tr.Line,
                $"replace a {FormatType(oldType)}",
                "Use a text value as the target, e.g. \"a\"."));

        var newType = InferType(tr.New);
        if (newType != null && newType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "the replacement must be text",
                null, tr.Line,
                $"replace with a {FormatType(newType)}",
                "Use a text value as the replacement, e.g. \"X\" (or \"\" to delete)."));

        if (tr.Old is StringLiteral { Value: "" })
            throw new TypeException(FormatTypeError(
                "'replace' needs a non-empty target",
                null, tr.Line,
                "replace an empty piece of text",
                "Use a target with at least one character."));

        return CufetType.Text;
    }

    private CufetType InferTextCase(TextCase tc)
    {
        var textType = InferType(tc.Text);
        if (textType != null && textType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "'in uppercase'/'in lowercase' work on text only",
                null, tc.Line,
                $"change the case of a {FormatType(textType)}",
                "Only text has a case to change. Convert the value to text first if needed."));
        return CufetType.Text;
    }

    private CufetType InferTextTrim(TextTrim trim)
    {
        var textType = InferType(trim.Text);
        if (textType != null && textType != CufetType.Text)
            throw new TypeException(FormatTypeError(
                "'trimmed' works on text only",
                null, trim.Line,
                $"trim a {FormatType(textType)}",
                "Only text has surrounding whitespace to trim. Convert the value to text first if needed."));
        return CufetType.Text;
    }
}
