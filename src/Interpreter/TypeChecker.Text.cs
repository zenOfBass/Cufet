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
}
