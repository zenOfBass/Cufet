namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    private CufetType InferChannelCreation(ChannelCreation cc)
        => new ChannelType(ResolveParamType(cc.ElementType));

    private void CheckSend(SendStatement ss)
    {
        var chanType = InferType(ss.Channel);
        if (chanType is not ChannelType ct)
        {
            if (chanType != null)
                throw TypeError(
                    "'Send through' requires a channel", null, ss.Line, ss.Column,
                    $"send through a {FormatType(chanType)}",
                    "Use 'Send <value> through <channel of T>.'. The expression after 'through' must be a channel.");
            return;
        }
        var valType = InferType(ss.Value);
        if (valType != null && !IsAssignable(ct.ElementType, valType))
            throw TypeError(
                $"this channel carries {FormatTypePlural(ct.ElementType)}, but you're sending a {FormatType(valType)}",
                null, ss.Line, ss.Column,
                $"send a {FormatType(valType)} through a channel of {FormatType(ct.ElementType)}",
                $"The sent value must be a {FormatType(ct.ElementType)} to match this channel's type.");
    }

    private CufetType InferDeliveryExpression(DeliveryExpression de)
    {
        var chanType = InferType(de.Channel);
        if (chanType is not ChannelType ct)
        {
            if (chanType != null)
                throw TypeError(
                    "'the delivery from' requires a channel", null, de.Line, de.Column,
                    $"take a delivery from a {FormatType(chanType)}",
                    "Use 'the delivery from <channel of T>'. The expression after 'from' must be a channel.");
            return new VoidableType(CufetType.Number);
        }
        return new VoidableType(ct.ElementType);
    }

    private void CheckClose(CloseStatement cs)
    {
        var chanType = InferType(cs.Channel);
        if (chanType is not ChannelType && chanType != null)
            throw TypeError(
                "'Close' requires a channel", null, cs.Line, cs.Column,
                $"close a {FormatType(chanType)}",
                "Use 'Close <channel>.' The expression must be a channel.");
    }
}
