namespace Cufet.Interpreter;

public sealed partial class Interpreter
{
    private sealed class ChannelValue
    {
        private readonly Queue<object> _queue = new();
        private bool _closed;
        public bool IsEmpty  => _queue.Count == 0;
        public bool IsClosed => _closed;
        public bool HasValue => _queue.Count > 0;
        public void Enqueue(object value) => _queue.Enqueue(value);
        public object Dequeue()           => _queue.Dequeue();
        public void Close() { _closed = true; } // idempotent
    }

    private object EvaluateChannelCreation(ChannelCreation _) => new ChannelValue();

    private void ExecuteSendStatement(SendStatement ss)
    {
        var chan = (ChannelValue)Evaluate(ss.Channel);
        if (chan.IsClosed)
            throw new RuntimeException(
                $"Send through a closed channel on line {ss.Line}. " +
                "'Close ch.' signals no more values — sending after closing is a logic error.");
        chan.Enqueue(DeepCopyForChannel(Evaluate(ss.Value)));
    }

    private object EvaluateDeliveryExpression(DeliveryExpression de)
    {
        var chan = (ChannelValue)Evaluate(de.Channel);
        if (chan.HasValue) return chan.Dequeue();
        if (chan.IsClosed) return VoidValue.Instance;
        _scheduler!.DrainUntil(() => chan.HasValue || chan.IsClosed);
        return chan.HasValue ? chan.Dequeue() : (object)VoidValue.Instance;
    }

    private void ExecuteCloseStatement(CloseStatement cs)
        => ((ChannelValue)Evaluate(cs.Channel)).Close();

    private static object DeepCopyForChannel(object value) => value switch
    {
        List<object> list => list.Select(DeepCopyForChannel).ToList(),
        Dictionary<object, object> dict => dict.ToDictionary(
            kvp => kvp.Key,
            kvp => DeepCopyForChannel(kvp.Value)),
        ObjectValue ov => new ObjectValue(
            ov.TypeName,
            ov.PositionalFields.Select(DeepCopyForChannel),
            ov.NamedFields.Select(f => (f.Name, DeepCopyForChannel(f.Value))),
            ov.EmbeddedObject is null ? null : (ObjectValue)DeepCopyForChannel((object)ov.EmbeddedObject)),
        RecordValue rv => new RecordValue(
            rv.PositionalFields.Select(DeepCopyForChannel),
            rv.NamedFields.Select(f => (f.Name, DeepCopyForChannel(f.Value)))),
        _ => value,  // decimal, string, bool, VoidValue, MatrixValue — immutable/pass-through
    };
}
